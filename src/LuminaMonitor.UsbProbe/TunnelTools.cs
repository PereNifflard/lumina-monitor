using System.Buffers.Binary;
using System.Net;
using System.Threading.Channels;
using LuminaMonitor.Core.RemoteXpc;
using LuminaMonitor.Core.Tunnel;

/// <summary>
/// The tunnel's own TCP, exercised against a phone that is not there.
/// </summary>
/// <remarks>
/// Everything below RemoteXPC is ours — the IPv6 header, the segments, the
/// state machine — and the one failure that mattered most is invisible with a
/// real phone on the other end, because a healthy daemon never shuts its
/// receive window and keeps it shut. Here it can be shut on command: two
/// in-memory pipes stand in for the cable, and a peer of about a hundred lines
/// answers the handshake, acknowledges what it is sent, says just enough HTTP/2
/// for a RemoteXPC channel to open, and then stops reading.
///
/// <para>What that proves, and nothing else can offline: a write parked on a
/// shut window gives up on its own deadline; the RemoteXPC write lock it was
/// holding is free the moment it does, so the next message is not parked behind
/// a report that will never leave; and a reset arriving on a connection nobody
/// is reading or writing leaks no exception to the finalizer — which is where
/// "RST recu du port 64006" used to surface, minutes later, as a fault of the
/// whole process.</para>
/// </remarks>
internal static class TunnelTools
{
    /// <summary>Long enough for the in-memory pipes to go quiet, short enough to be a test.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(250);

    /// <summary>The deadline the input channels really run with.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(1);

    public static async Task<int> TcpSelfTestAsync(Action<string> say)
    {
        say($"Echeance d'ecriture des canaux d'entree : {Patience.TotalMilliseconds:0} ms ; "
            + "des transferts : illimitee.");

        var leaks = new List<string>();
        void Collect(object? _, UnobservedTaskExceptionEventArgs args)
        {
            lock (leaks) leaks.Add(args.Exception.GetBaseException().Message);
            args.SetObserved();
        }

        TaskScheduler.UnobservedTaskException += Collect;
        int failures;
        try
        {
            failures = await HealthyAsync(say).ConfigureAwait(false);
            failures += await ShutWindowAsync(say).ConfigureAwait(false);
            failures += await ResetIgnoredAsync(say).ConfigureAwait(false);
        }
        finally
        {
            // Unobserved faults are raised by the finalizer, so the finalizer
            // has to run before the count means anything: collect, finalize,
            // collect again for what the finalizers themselves released.
            for (int pass = 0; pass < 3; pass++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            GC.Collect();
            TaskScheduler.UnobservedTaskException -= Collect;
        }

        lock (leaks)
        {
            if (leaks.Count == 0)
            {
                say("  OK    aucune exception non observee n'a atteint le finaliseur.");
            }
            else
            {
                failures += leaks.Count;
                foreach (string leak in leaks)
                    say($"  ECHEC exception non observee : {leak}");
            }
        }

        say(failures == 0
            ? "Pile TCP du tunnel : tous les scenarios passent."
            : $"Pile TCP du tunnel : {failures} scenario(s) en echec.");
        return failures == 0 ? 0 : 9;
    }

    // --- The scenarios -----------------------------------------------------------

    /// <summary>A peer that reads: the channel opens and a report leaves at once.</summary>
    private static async Task<int> HealthyAsync(Action<string> say)
    {
        await using var rig = new Rig(Patience);
        await rig.OpenChannelAsync().ConfigureAwait(false);
        double milliseconds = await rig.TimeSendAsync().ConfigureAwait(false);

        bool ok = rig.Failure is null && milliseconds < 100;
        say($"  {(ok ? "OK   " : "ECHEC")} fenetre ouverte : rapport parti en {milliseconds:0.0} ms"
            + $"{(rig.Failure is { } failure ? $", {failure}" : "")}.");
        return ok ? 0 : 1;
    }

    /// <summary>
    /// The failure itself: a peer that stops reading, and the two things that
    /// must follow.
    /// </summary>
    /// <remarks>
    /// The first send is expected to give up on the connection's own deadline,
    /// not to hang. The second is the real assertion — it can only be quick if
    /// the write lock the first one held was released, which is exactly what a
    /// deadline in the pump above could never do on its own.
    /// </remarks>
    private static async Task<int> ShutWindowAsync(Action<string> say)
    {
        await using var rig = new Rig(Patience);
        await rig.OpenChannelAsync().ConfigureAwait(false);
        await rig.ShutWindowAsync().ConfigureAwait(false);

        double first = await rig.TimeSendAsync().ConfigureAwait(false);
        string? blocked = rig.Failure;
        double second = await rig.TimeSendAsync().ConfigureAwait(false);
        string? after = rig.Failure;

        bool onTime = blocked is not null && first >= 800 && first <= 2500;
        bool freed = after is not null && second < 250;
        say($"  {(onTime ? "OK   " : "ECHEC")} fenetre fermee : abandon en {first:0} ms ({blocked ?? "aucune erreur"}).");
        say($"  {(freed ? "OK   " : "ECHEC")} verrou RemoteXPC libere : envoi suivant rendu en {second:0.0} ms ({after ?? "aucune erreur"}).");
        return (onTime ? 0 : 1) + (freed ? 0 : 1);
    }

    /// <summary>A reset on a connection nobody is reading or writing.</summary>
    /// <remarks>
    /// The shape that used to leak: no reader, no writer, so the reason had
    /// nowhere to go but into a wait nobody was in — and from there to the
    /// finalizer. Nothing is asserted here beyond the connection dying; the
    /// verdict is the leak count taken after the finalizers have run.
    /// </remarks>
    private static async Task<int> ResetIgnoredAsync(Action<string> say)
    {
        string outcome;
        await using (var rig = new Rig(Patience))
        {
            await rig.OpenTcpAsync().ConfigureAwait(false);
            rig.Reset();
            await Task.Delay(Settle).ConfigureAwait(false);
            outcome = await rig.ProbeWriteAsync().ConfigureAwait(false);
        }
        bool ok = outcome.Contains("RST", StringComparison.Ordinal);
        say($"  {(ok ? "OK   " : "ECHEC")} RST sans lecteur : l'ecriture suivante leve « {outcome} ».");
        return ok ? 0 : 1;
    }

    // --- The rig -------------------------------------------------------------------

    /// <summary>One tunnel, one paper phone, and the channel between them.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private const int RemoteServicePort = 64_000;

        private readonly Loopback _toPhone = new();
        private readonly Loopback _toHost = new();
        private readonly CancellationTokenSource _closing = new();
        private readonly TunnelNet _net;
        private readonly Peer _peer;
        private readonly Task _peerLoop;
        private readonly TimeSpan _patience;

        private Stream? _tcp;
        private RemoteXpc? _xpc;

        /// <summary>What the last timed send threw, if anything.</summary>
        public string? Failure { get; private set; }

        public Rig(TimeSpan patience)
        {
            _patience = patience;
            var host = IPAddress.Parse("fdd0::2");
            var phone = IPAddress.Parse("fdd0::1");
            _net = new TunnelNet(new CdTunnel(new Duplex(_toHost, _toPhone)),
                new CdTunnel.Handshake(host.ToString(), 1500, phone.ToString(), 0));
            _net.Start();
            _peer = new Peer(_toPhone, _toHost, host, phone);
            _peerLoop = Task.Run(() => _peer.RunAsync(_closing.Token));
        }

        /// <summary>Just the TCP connection, with no channel on top of it.</summary>
        public async Task OpenTcpAsync() =>
            _tcp = await _net.ConnectTcpAsync(RemoteServicePort, _patience).ConfigureAwait(false);

        /// <summary>The TCP connection and a RemoteXPC channel opened on it.</summary>
        public async Task OpenChannelAsync()
        {
            _peer.SpeaksHttp2 = true;
            await OpenTcpAsync().ConfigureAwait(false);
            _xpc = new RemoteXpc(_tcp!);
            await _xpc.ConnectAsync().ConfigureAwait(false);
        }

        /// <summary>Tells the peer to advertise no room at all, and to stop acknowledging.</summary>
        /// <remarks>
        /// The second wait is not padding. Closing the window is one last
        /// acknowledgement travelling the other way, and until the connection
        /// has read it the send window it knows about is still the old, open
        /// one — a send timed in that gap leaves at once and the scenario reads
        /// its two verdicts in the wrong order, which is what made this
        /// self-test fail about every other run.
        /// </remarks>
        public async Task ShutWindowAsync()
        {
            await Task.Delay(Settle).ConfigureAwait(false);       // let the opening drain
            _peer.Shut();
            await Task.Delay(Settle).ConfigureAwait(false);       // let the closing arrive
        }

        /// <summary>One fire-and-forget message, timed, with whatever it threw kept.</summary>
        public async Task<double> TimeSendAsync()
        {
            Failure = null;
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                await _xpc!.SendAsync(new Dictionary<string, object?> { ["messageType"] = "SelfTest" })
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Failure = exception.Message;
            }
            return (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0
                / System.Diagnostics.Stopwatch.Frequency;
        }

        /// <summary>Writes one byte to the bare connection and says what came back.</summary>
        public async Task<string> ProbeWriteAsync()
        {
            try
            {
                await _tcp!.WriteAsync(new byte[] { 0 }).ConfigureAwait(false);
                return "rien";
            }
            catch (Exception exception)
            {
                return exception.Message;
            }
        }

        public void Reset() => _peer.Reset();

        public async ValueTask DisposeAsync()
        {
            _xpc?.Dispose();
            _tcp?.Dispose();
            _closing.Cancel();
            _toPhone.Close();
            _toHost.Close();
            try { await _peerLoop.ConfigureAwait(false); } catch (Exception) { }
            await _net.DisposeAsync().ConfigureAwait(false);
            _closing.Dispose();
        }
    }

    // --- The paper phone ------------------------------------------------------------

    /// <summary>
    /// A peer that is only a TCP endpoint: it answers the handshake,
    /// acknowledges what it is sent, and does what the scenario tells it.
    /// </summary>
    /// <remarks>
    /// No retransmission, no reordering, no window of its own to fill — the
    /// point is not to be a TCP, it is to be the exact TCP the connection under
    /// test needs in order to reach the state nobody can reach on purpose with a
    /// real phone.
    /// </remarks>
    private sealed class Peer
    {
        private const byte FlagFin = 0x01, FlagSyn = 0x02, FlagRst = 0x04, FlagAck = 0x10;

        private readonly Loopback _in, _out;
        private readonly IPAddress _host, _self;
        private readonly object _gate = new();

        private ushort _hostPort, _selfPort;
        private uint _seq = 1_000, _ack;
        private ushort _window = 65535;
        private bool _acknowledging = true;
        private bool _greeted;

        /// <summary>Answers the first payload with an empty SETTINGS frame, which is all a channel needs to open.</summary>
        public bool SpeaksHttp2 { get; set; }

        public Peer(Loopback inbound, Loopback outbound, IPAddress host, IPAddress self)
        {
            _in = inbound;
            _out = outbound;
            _host = host;
            _self = self;
        }

        /// <summary>Stops reading: one last acknowledgement, advertising no room.</summary>
        public void Shut()
        {
            lock (_gate)
            {
                _window = 0;
                _acknowledging = false;
                Send(FlagAck, []);
            }
        }

        public void Reset()
        {
            lock (_gate) Send(FlagRst | FlagAck, []);
        }

        public async Task RunAsync(CancellationToken cancellation)
        {
            try
            {
                while (!cancellation.IsCancellationRequested)
                    Handle(await ReadPacketAsync(cancellation).ConfigureAwait(false));
            }
            catch (OperationCanceledException) { }
            catch (EndOfStreamException) { }                      // the rig went away
        }

        private void Handle(byte[] packet)
        {
            if (packet[6] != Ipv6.ProtoTcp)
                return;
            var segment = packet.AsSpan(Ipv6.HeaderSize);
            if (segment.Length < 20)
                return;
            uint seq = BinaryPrimitives.ReadUInt32BigEndian(segment[4..]);
            int dataOffset = (segment[12] >> 4) * 4;
            byte flags = segment[13];
            int dataLength = segment.Length - dataOffset;

            lock (_gate)
            {
                if ((flags & FlagSyn) != 0)
                {
                    _selfPort = BinaryPrimitives.ReadUInt16BigEndian(segment[2..]);
                    _hostPort = BinaryPrimitives.ReadUInt16BigEndian(segment);
                    _ack = seq + 1;
                    Send(FlagSyn | FlagAck, []);
                    _seq += 1;
                    return;
                }
                if (dataLength <= 0 && (flags & FlagFin) == 0)
                    return;                                       // a bare acknowledgement of ours

                _ack = seq + (uint)dataLength + (uint)((flags & FlagFin) != 0 ? 1 : 0);
                if (!_acknowledging)
                    return;
                Send(FlagAck, []);
                if (!SpeaksHttp2 || _greeted || dataLength <= 0)
                    return;
                _greeted = true;
                Send(FlagAck, Http2Settings());
            }
        }

        /// <summary>An empty SETTINGS frame: type 4, no flags, stream 0, no payload.</summary>
        private static byte[] Http2Settings() => [0, 0, 0, 4, 0, 0, 0, 0, 0];

        /// <summary>One segment out, checksummed like a real one and wrapped in IPv6.</summary>
        private void Send(int flags, ReadOnlySpan<byte> payload)
        {
            byte[] segment = new byte[20 + payload.Length];
            var span = segment.AsSpan();
            BinaryPrimitives.WriteUInt16BigEndian(span, _selfPort);
            BinaryPrimitives.WriteUInt16BigEndian(span[2..], _hostPort);
            BinaryPrimitives.WriteUInt32BigEndian(span[4..], _seq);
            BinaryPrimitives.WriteUInt32BigEndian(span[8..], _ack);
            span[12] = 5 << 4;
            span[13] = (byte)flags;
            BinaryPrimitives.WriteUInt16BigEndian(span[14..], _window);
            payload.CopyTo(span[20..]);
            BinaryPrimitives.WriteUInt16BigEndian(span[16..],
                Ipv6.UpperLayerChecksum(_self, _host, Ipv6.ProtoTcp, segment));
            _out.Write(Ipv6.Build(_self, _host, Ipv6.ProtoTcp, segment));
            _seq += (uint)payload.Length;
        }

        /// <summary>One IPv6 packet in, delimited by the payload length in its own header.</summary>
        private async Task<byte[]> ReadPacketAsync(CancellationToken cancellation)
        {
            byte[] header = new byte[Ipv6.HeaderSize];
            await _in.ReadExactlyAsync(header, cancellation).ConfigureAwait(false);
            int length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4, 2));
            byte[] packet = new byte[Ipv6.HeaderSize + length];
            header.CopyTo(packet, 0);
            await _in.ReadExactlyAsync(packet.AsMemory(Ipv6.HeaderSize, length), cancellation).ConfigureAwait(false);
            return packet;
        }
    }

    // --- The cable ------------------------------------------------------------------

    /// <summary>One direction of an in-memory pipe: writes never block, reads wait.</summary>
    private sealed class Loopback
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();
        private byte[] _pending = [];
        private int _offset;

        public void Write(ReadOnlySpan<byte> data) => _chunks.Writer.TryWrite(data.ToArray());

        public void Close() => _chunks.Writer.TryComplete();

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellation)
        {
            while (_offset >= _pending.Length)
            {
                if (!await _chunks.Reader.WaitToReadAsync(cancellation).ConfigureAwait(false))
                    return 0;
                if (!_chunks.Reader.TryRead(out byte[]? chunk))
                    return 0;
                _pending = chunk;
                _offset = 0;
            }
            int count = Math.Min(buffer.Length, _pending.Length - _offset);
            _pending.AsSpan(_offset, count).CopyTo(buffer.Span);
            _offset += count;
            return count;
        }

        public async ValueTask ReadExactlyAsync(Memory<byte> buffer, CancellationToken cancellation)
        {
            for (int filled = 0; filled < buffer.Length;)
            {
                int read = await ReadAsync(buffer[filled..], cancellation).ConfigureAwait(false);
                if (read == 0)
                    throw new EndOfStreamException();
                filled += read;
            }
        }
    }

    /// <summary>The two directions seen as one stream, which is what the tunnel wants.</summary>
    private sealed class Duplex : Stream
    {
        private readonly Loopback _read, _write;

        public Duplex(Loopback read, Loopback write)
        {
            _read = read;
            _write = write;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _read.ReadAsync(buffer, cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _write.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override void Write(byte[] buffer, int offset, int count) => _write.Write(buffer.AsSpan(offset, count));
    }
}
