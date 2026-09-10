using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;

namespace LuminaMonitor.Core.Tunnel;

/// <summary>
/// The host end of the tunnel network: reads every IPv6 packet the phone
/// sends, answers the neighbour-discovery and ping chatter it expects from a
/// live host, and routes TCP segments to the connections that own them.
/// </summary>
/// <remarks>
/// One pump task owns the tunnel's read side for the lifetime of the net;
/// writes are serialised by a semaphore because several connections share the
/// single pipe. The phone treats the tunnel as a real link: before it will
/// deliver anything to <c>fdd0:…::2</c> it may ask "who has that address?"
/// (Neighbour Solicitation), and a host that stays silent is unreachable.
///
/// <para>A segment for a connection we no longer know gets a reset, as any
/// host would send one: that is what lets the phone tear down its side
/// instead of retransmitting into the void for minutes.</para>
/// </remarks>
internal sealed class TunnelNet : IAsyncDisposable
{
    private const byte ProtoUdp = Ipv6.ProtoUdp;

    private readonly CdTunnel _tunnel;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<(ushort Local, ushort Remote), TcpConnection> _tcp = new();
    private readonly ConcurrentDictionary<ushort, Action<ReadOnlyMemory<byte>, ushort>> _udp = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _pump;
    private int _nextPort;

    // --- What the pump did with its time ---------------------------------------
    // Written by the pump task and nobody else, read once a window under the
    // gate. The lock is taken once per packet, uncontended except for that one
    // reader a second; the alternative — interlocked adds racing an exchange —
    // silently loses counts, which is worse than a few nanoseconds on a path
    // that spends microseconds.

    private readonly object _statsGate = new();
    private readonly Stopwatch _statsWindow = Stopwatch.StartNew();
    private long _pumpPackets, _pumpBytes;
    private long _turnTicks, _turnMaxTicks;
    private long _readTicks, _readMaxTicks;
    private long _handlerTicks, _handlerMaxTicks;
    private long _udpTicks, _tcpTicks;
    private long _turnUdpTicks, _turnTcpTicks;      // pump-only scratch, one turn's worth

    public IPAddress Local { get; }
    public IPAddress Peer { get; }
    public int Mtu { get; }
    public Action<string>? Log { get; set; }

    /// <summary>
    /// How many bytes are waiting unread in the socket underneath, when asked.
    /// </summary>
    /// <remarks>
    /// The tunnel is a <see cref="Stream"/> here and a stream has no such
    /// notion, so the owner of the socket — the session that opened it through
    /// the multiplexer — hands the question over rather than the answer.
    /// </remarks>
    public Func<int>? SocketAvailable { get; set; }

    /// <summary>Closes the pump's window, reports it and starts the next.</summary>
    public TunnelStats TakeStats()
    {
        lock (_statsGate)
        {
            double seconds = Math.Max(0.001, _statsWindow.Elapsed.TotalSeconds);
            long packets = Math.Max(1, _pumpPackets);
            var stats = new TunnelStats(
                seconds, _pumpPackets, _pumpBytes,
                SocketAvailable?.Invoke() ?? 0,
                Ms(_turnTicks / packets), Ms(_turnMaxTicks),
                Ms(_readTicks / packets), Ms(_readMaxTicks),
                Ms(_handlerTicks / packets), Ms(_handlerMaxTicks),
                Ms(_udpTicks), Ms(_tcpTicks));
            _pumpPackets = _pumpBytes = 0;
            _turnTicks = _turnMaxTicks = 0;
            _readTicks = _readMaxTicks = 0;
            _handlerTicks = _handlerMaxTicks = 0;
            _udpTicks = _tcpTicks = 0;
            _statsWindow.Restart();
            return stats;
        }
    }

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    public TunnelNet(CdTunnel tunnel, CdTunnel.Handshake handshake)
    {
        _tunnel = tunnel;
        Local = IPAddress.Parse(handshake.ClientAddress);
        Peer = IPAddress.Parse(handshake.ServerAddress);
        Mtu = handshake.Mtu;
        _nextPort = RandomNumberGenerator.GetInt32(16384);
    }

    public void Start() => _pump = Task.Run(PumpAsync);

    /// <summary>Ephemeral ports 49152..65535, never 0, never reused while still in use.</summary>
    private ushort NextPort(Func<ushort, bool> free)
    {
        for (int attempt = 0; attempt < 16384; attempt++)
        {
            ushort port = (ushort)(49152 + (uint)Interlocked.Increment(ref _nextPort) % 16384);
            if (free(port))
                return port;
        }
        throw new InvalidOperationException("plus de port local disponible");
    }

    /// <summary>Opens a TCP connection to the phone; returns once established.</summary>
    /// <param name="writePatience">
    /// How long a write on it may wait on the phone's receive window before the
    /// connection is declared stuck; null waits for as long as it takes. See
    /// <see cref="TcpConnection.WritePatience"/>: transfers want no deadline,
    /// input channels want a short one.
    /// </param>
    public async Task<TcpConnection> ConnectTcpAsync(int remotePort, TimeSpan? writePatience = null, CancellationToken cancellation = default)
    {
        TcpConnection? connection = null;
        NextPort(port =>
        {
            var candidate = new TcpConnection(this, port, (ushort)remotePort)
            {
                WritePatience = writePatience ?? Timeout.InfiniteTimeSpan,
            };
            if (!_tcp.TryAdd((port, (ushort)remotePort), candidate))
                return false;
            connection = candidate;
            return true;
        });
        try
        {
            await connection!.ConnectAsync(cancellation).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            _tcp.TryRemove((connection!.LocalPort, connection.RemotePort), out _);
            throw;
        }
    }

    internal void Forget(TcpConnection connection) =>
        _tcp.TryRemove((connection.LocalPort, connection.RemotePort), out _);

    // --- UDP -------------------------------------------------------------------
    // The media stream arrives as RTP over UDP, addressed to our tunnel
    // address, and its RTCP is multiplexed on the very same port. The phone
    // stops encoding after twenty silent seconds, so this side does answer:
    // the handler is told which port a datagram came from, and
    // <see cref="SendUdpAsync"/> sends the receiver reports back.

    /// <summary>Claims a UDP port on our tunnel address; returns it.</summary>
    /// <param name="onDatagram">The payload and the phone's source port.</param>
    public ushort ListenUdp(Action<ReadOnlyMemory<byte>, ushort> onDatagram) =>
        NextPort(port => _udp.TryAdd(port, onDatagram));

    public void StopUdp(ushort port) => _udp.TryRemove(port, out _);

    /// <summary>Sends one UDP datagram to the phone, from a port we listen on.</summary>
    /// <remarks>
    /// IPv6 makes the checksum mandatory — a zero is not "unchecked" as it was
    /// in IPv4 — so it is computed over the pseudo-header like TCP's.
    /// </remarks>
    public Task SendUdpAsync(ushort localPort, ushort remotePort, ReadOnlyMemory<byte> payload) =>
        SendAsync(ProtoUdp, BuildUdp(localPort, remotePort, payload.Span));

    private byte[] BuildUdp(ushort localPort, ushort remotePort, ReadOnlySpan<byte> payload)
    {
        byte[] datagram = new byte[8 + payload.Length];
        var span = datagram.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(span, localPort);
        BinaryPrimitives.WriteUInt16BigEndian(span[2..], remotePort);
        BinaryPrimitives.WriteUInt16BigEndian(span[4..], (ushort)datagram.Length);
        payload.CopyTo(span[8..]);
        ushort sum = Ipv6.UpperLayerChecksum(Local, Peer, ProtoUdp, datagram);
        BinaryPrimitives.WriteUInt16BigEndian(span[6..], sum == 0 ? (ushort)0xFFFF : sum);
        return datagram;
    }

    // --- ICMPv6 echo -----------------------------------------------------------
    // Pinging the phone through the tunnel times the transport on its own —
    // cable, multiplexer, daemon packet loop — with nothing above IPv6 in the
    // way. That figure is the floor every tap and every frame has to pay.

    /// <summary>Where Echo Replies are handed over: the whole ICMPv6 message, header included.</summary>
    public Action<ReadOnlyMemory<byte>>? EchoReply { get; set; }

    /// <summary>Sends one ICMPv6 Echo Request to the phone.</summary>
    public Task SendEchoAsync(ushort identifier, ushort sequence, ReadOnlyMemory<byte> payload) =>
        SendAsync(Ipv6.ProtoIcmpv6, BuildEcho(identifier, sequence, payload.Span));

    private byte[] BuildEcho(ushort identifier, ushort sequence, ReadOnlySpan<byte> payload)
    {
        byte[] echo = new byte[8 + payload.Length];
        var span = echo.AsSpan();
        span[0] = 128;                                                      // Echo Request, code 0
        BinaryPrimitives.WriteUInt16BigEndian(span[4..], identifier);
        BinaryPrimitives.WriteUInt16BigEndian(span[6..], sequence);
        payload.CopyTo(span[8..]);
        BinaryPrimitives.WriteUInt16BigEndian(span[2..],
            Ipv6.UpperLayerChecksum(Local, Peer, Ipv6.ProtoIcmpv6, echo));
        return echo;
    }

    // --- Sending ---------------------------------------------------------------

    /// <summary>Sends one upper-layer datagram to the phone inside an IPv6 packet.</summary>
    internal async Task SendAsync(byte protocol, byte[] upper, IPAddress? destination = null, byte hopLimit = 64)
    {
        byte[] packet = Ipv6.Build(Local, destination ?? Peer, protocol, upper, hopLimit);
        await _sendLock.WaitAsync(_cts.Token).ConfigureAwait(false);
        try { await _tunnel.SendPacketAsync(packet, _cts.Token).ConfigureAwait(false); }
        finally { _sendLock.Release(); }
    }

    /// <summary>Runs a send nobody awaits, and says so if it fails instead of losing the fault.</summary>
    /// <remarks>
    /// The fault is read first and judged afterwards. Reading it is what marks
    /// it observed, and the version that read it only when it also had
    /// something to say left every failure during a teardown — which is when
    /// they come in handfuls — to be raised by the finalizer as an unobserved
    /// exception of the process.
    /// </remarks>
    internal void FireAndForget(Task task, string what)
    {
        _ = task.ContinueWith(t =>
        {
            string message = t.Exception?.GetBaseException().Message ?? "sans raison";
            if (!_cts.IsCancellationRequested)
                Log?.Invoke($"emission {what} en echec : {message}");
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    // --- Receiving -------------------------------------------------------------

    private async Task PumpAsync()
    {
        Exception? reason = null;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                long started = Stopwatch.GetTimestamp();
                byte[] packet = await _tunnel.ReceivePacketAsync(_cts.Token).ConfigureAwait(false);
                long read = Stopwatch.GetTimestamp();
                _turnUdpTicks = _turnTcpTicks = 0;
                Dispatch(packet);
                Account(packet.Length, started, read, Stopwatch.GetTimestamp());
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            reason = exception;
            Log?.Invoke($"tunnel ferme : {exception.Message}");
        }
        foreach (var connection in _tcp.Values)
            connection.Abort(reason ?? new ObjectDisposedException(nameof(TunnelNet)));
    }

    /// <summary>Adds one turn of the pump to the window.</summary>
    private void Account(int bytes, long started, long read, long done)
    {
        lock (_statsGate)
        {
            _pumpPackets++;
            _pumpBytes += bytes;
            long turn = done - started, wait = read - started, handler = done - read;
            _turnTicks += turn;
            _readTicks += wait;
            _handlerTicks += handler;
            _udpTicks += _turnUdpTicks;
            _tcpTicks += _turnTcpTicks;
            if (turn > _turnMaxTicks) _turnMaxTicks = turn;
            if (wait > _readMaxTicks) _readMaxTicks = wait;
            if (handler > _handlerMaxTicks) _handlerMaxTicks = handler;
        }
    }

    private void Dispatch(byte[] packet)
    {
        var (protocol, offset) = Ipv6.UpperLayer(packet);
        if (offset >= packet.Length)
            return;

        switch (protocol)
        {
            case Ipv6.ProtoTcp:
            {
                var segment = packet.AsSpan(offset);
                if (segment.Length < 20)
                    return;
                ushort remote = BinaryPrimitives.ReadUInt16BigEndian(segment);
                ushort local = BinaryPrimitives.ReadUInt16BigEndian(segment[2..]);
                long entered = Stopwatch.GetTimestamp();
                if (_tcp.TryGetValue((local, remote), out var connection))
                    connection.OnSegment(segment);
                else
                    ResetOrphan(segment, local, remote);
                _turnTcpTicks += Stopwatch.GetTimestamp() - entered;
                break;
            }
            case Ipv6.ProtoIcmpv6:
                HandleIcmp(packet, offset);
                break;
            case ProtoUdp: // [src port][dst port][length][checksum] then payload
            {
                var udp = packet.AsSpan(offset);
                if (udp.Length < 8)
                    return;
                ushort src = BinaryPrimitives.ReadUInt16BigEndian(udp);
                ushort dst = BinaryPrimitives.ReadUInt16BigEndian(udp[2..]);
                if (_udp.TryGetValue(dst, out var handler))
                {
                    long entered = Stopwatch.GetTimestamp();
                    handler(packet.AsMemory(offset + 8), src);
                    _turnUdpTicks += Stopwatch.GetTimestamp() - entered;
                }
                break;
            }
        }
    }

    /// <summary>RFC 9293 §3.10.7.1: a segment for no connection is answered with a reset — never a reset itself.</summary>
    private void ResetOrphan(ReadOnlySpan<byte> segment, ushort local, ushort remote)
    {
        byte flags = segment[13];
        if ((flags & 0x04) != 0)
            return;
        uint seq = BinaryPrimitives.ReadUInt32BigEndian(segment[4..]);
        uint ack = BinaryPrimitives.ReadUInt32BigEndian(segment[8..]);
        int len = segment.Length - (segment[12] >> 4) * 4 + ((flags & 0x02) != 0 ? 1 : 0) + ((flags & 0x01) != 0 ? 1 : 0);
        byte[] rst = (flags & 0x10) != 0
            ? TcpConnection.BuildBare(this, local, remote, ack, 0, 0x04, [], [])
            : TcpConnection.BuildBare(this, local, remote, 0, seq + (uint)len, 0x14, [], []);
        FireAndForget(SendAsync(Ipv6.ProtoTcp, rst), "RST orphelin");
    }

    private void HandleIcmp(byte[] packet, int offset)
    {
        var icmp = packet.AsSpan(offset);
        if (icmp.Length < 8)
            return;
        var source = Ipv6.Source(packet);

        switch (icmp[0])
        {
            case 135: // Neighbour Solicitation — "who has our address?"
            {
                if (icmp.Length < 24 || !new IPAddress(icmp.Slice(8, 16)).Equals(Local))
                    return;
                // Neighbour Advertisement: Solicited + Override, target = us,
                // no link-layer option because a tunnel has no link layer.
                byte[] na = new byte[24];
                na[0] = 136;
                BinaryPrimitives.WriteUInt32BigEndian(na.AsSpan(4), 0x60000000);
                Local.TryWriteBytes(na.AsSpan(8, 16), out _);
                ushort sum = Ipv6.UpperLayerChecksum(Local, source, Ipv6.ProtoIcmpv6, na);
                BinaryPrimitives.WriteUInt16BigEndian(na.AsSpan(2), sum);
                FireAndForget(SendAsync(Ipv6.ProtoIcmpv6, na, source, hopLimit: 255), "annonce de voisinage");
                Log?.Invoke($"voisinage : sollicitation de {source}, annonce renvoyee");
                break;
            }
            case 128: // Echo Request — answer, it is a cheap liveness proof
            {
                byte[] reply = icmp.ToArray();
                reply[0] = 129;
                reply[2] = 0; reply[3] = 0;
                ushort sum = Ipv6.UpperLayerChecksum(Local, source, Ipv6.ProtoIcmpv6, reply);
                BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(2), sum);
                FireAndForget(SendAsync(Ipv6.ProtoIcmpv6, reply, source), "echo");
                break;
            }
            case 129: // Echo Reply — the answer to one of ours, if anyone is timing them
                EchoReply?.Invoke(packet.AsMemory(offset));
                break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var connection in _tcp.Values)
            connection.Abort(new ObjectDisposedException(nameof(TunnelNet)));
        _cts.Cancel();
        if (_pump is not null)
        {
            try { await _pump.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
            catch (Exception) { }
        }
    }
}
