using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Threading.Channels;

namespace LuminaMonitor.Core.Tunnel;

/// <summary>
/// One client TCP connection over the tunnel, usable as a <see cref="Stream"/>
/// so that TLS or HTTP/2 code can sit on it without knowing where the bytes go.
/// </summary>
/// <remarks>
/// Deliberately the smallest TCP that is still correct for this link: a
/// three-way handshake, in-order delivery, cumulative acknowledgements sent
/// at once (a delayed ACK would only add latency here), the peer's window
/// honoured on send, and a retransmission timer kept because the far end is
/// a real TCP that will wait for one. What is missing on purpose —
/// reordering, congestion control, selective ACK — cannot be needed on a
/// point-to-point pipe that never drops or reorders: the segment order is the
/// byte order of the USB stream underneath.
///
/// <para>Closing is done properly, because the phone keeps a real socket per
/// connection: our FIN is tracked until acknowledged, the phone's FIN is
/// acknowledged, and only then is the connection forgotten. A connection
/// that stays half-closed for ten seconds is reset rather than leaked.</para>
///
/// <para>Failures are kept rather than pushed. Nothing here ever puts an
/// exception into a <see cref="TaskCompletionSource"/>: one nobody happens to
/// be parked on carries its fault to the finalizer, which raises it as an
/// unobserved exception of the whole process minutes later — "RST recu du port
/// 64006" landing in a journal that has long moved on, and terminating the
/// process outright under the default configuration of some hosts. The reason
/// is stored in <see cref="_fault"/> instead, every wait is released empty, and
/// the next <see cref="ReadAsync"/> or <see cref="WriteAsync"/> throws it in
/// the caller's own hand, where there is somebody to catch it.</para>
/// </remarks>
internal sealed class TcpConnection : Stream
{
    private enum State { Closed, SynSent, Established, FinWait1, FinWait2, Closing, CloseWait, LastAck }

    private const int HeaderSize = 20;
    private const byte FlagFin = 0x01, FlagSyn = 0x02, FlagRst = 0x04, FlagPsh = 0x08, FlagAck = 0x10;
    private const ushort Window = 65535;
    private static readonly TimeSpan Rto = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan Linger = TimeSpan.FromSeconds(10);
    private const int MaxRetries = 8;

    private readonly TunnelNet _net;
    private readonly object _gate = new();
    private readonly Channel<byte[]> _inbound = Channel.CreateUnbounded<byte[]>();
    private readonly List<(uint Seq, byte[] Segment, DateTime Sent, int Tries)> _unacked = [];
    private readonly TaskCompletionSource _established = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _cts = new();

    private State _state = State.Closed;
    private uint _sndUna, _sndNxt, _rcvNxt;
    private uint _sndWnd = Window;
    private TaskCompletionSource _windowOpened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _mss;
    private byte[] _leftover = [];
    private int _leftoverOffset;
    private bool _disposed;
    private Exception? _fault;

    public ushort LocalPort { get; }
    public ushort RemotePort { get; }

    /// <summary>
    /// How long one write may wait on the phone's receive window before this
    /// connection is declared stuck. <see cref="Timeout.InfiniteTimeSpan"/> —
    /// the default — waits for as long as it takes.
    /// </summary>
    /// <remarks>
    /// The two kinds of traffic on this pipe want opposite answers. A transfer
    /// has nowhere else to go: a service directory or a media offer that waits
    /// two seconds for room is a transfer that succeeds, and one that gives up
    /// is a session that never opens. An input report is the reverse — it is
    /// aimed at a moment, and a report that arrives late is worse than one that
    /// never arrives, so its channel is given a second and no more.
    ///
    /// <para>The deadline is not really about the report, though: it is about
    /// the lock above it. The RemoteXPC channel holds its write lock for the
    /// whole of a message, so one write parked here with no deadline parks
    /// every message behind it, on that channel, for ever. That is what several
    /// seconds of delay between a click and the phone looked like from in
    /// here.</para>
    /// </remarks>
    internal TimeSpan WritePatience { get; init; } = Timeout.InfiniteTimeSpan;

    /// <summary>
    /// Segments sent and not yet acknowledged, and the bytes they carry: the
    /// depth of this connection's send queue.
    /// </summary>
    /// <remarks>
    /// This is the only place a backlog can hide once the layers above stop
    /// queueing: a report already on the wire cannot be taken back, so a growing
    /// figure here means the phone is not draining what we send as fast as we
    /// send it — which is exactly the queue a fast mouse used to build.
    /// </remarks>
    internal (int Segments, long Bytes) SendDepth
    {
        get
        {
            lock (_gate)
            {
                long bytes = 0;
                foreach (var u in _unacked)
                    bytes += SegmentDataLength(u.Segment);
                return (_unacked.Count, bytes);
            }
        }
    }

    internal TcpConnection(TunnelNet net, ushort localPort, ushort remotePort)
    {
        _net = net;
        LocalPort = localPort;
        RemotePort = remotePort;
        _mss = net.Mtu - Ipv6.HeaderSize - HeaderSize;
    }

    // --- Handshake -----------------------------------------------------------

    internal async Task ConnectAsync(CancellationToken cancellation)
    {
        uint isn = BinaryPrimitives.ReadUInt32LittleEndian(RandomNumberGenerator.GetBytes(4));
        lock (_gate)
        {
            _sndUna = isn;
            _sndNxt = isn + 1;
            _state = State.SynSent;
        }

        // SYN carries the MSS option so the phone never sends a segment the
        // tunnel MTU cannot carry.
        byte[] options = [2, 4, (byte)(_mss >> 8), (byte)(_mss & 0xFF)];
        await Transmit(isn, 0, FlagSyn, options, [], track: true).ConfigureAwait(false);
        _ = RetransmitLoopAsync();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(6));
        try
        {
            await _established.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Abort(new OperationCanceledException(cancellation));
            throw;
        }
        catch (OperationCanceledException)
        {
            Abort(new TimeoutException($"pas de SYN-ACK du port {RemotePort}"));
            throw new TimeoutException(CoreTexts.Current.TcpNoAnswer(RemotePort));
        }
        // The handshake wait is released empty when it fails, like every other
        // wait here, so the reason is read rather than caught.
        if (Fault is { } fault)
            throw new IOException($"Connexion TCP vers le port {RemotePort} : {fault.Message}", fault);
    }

    // --- Inbound segments (called from the tunnel pump) ----------------------

    internal void OnSegment(ReadOnlySpan<byte> segment)
    {
        uint seq = BinaryPrimitives.ReadUInt32BigEndian(segment[4..]);
        uint ack = BinaryPrimitives.ReadUInt32BigEndian(segment[8..]);
        int dataOffset = (segment[12] >> 4) * 4;
        byte flags = segment[13];
        ushort window = BinaryPrimitives.ReadUInt16BigEndian(segment[14..]);
        if (dataOffset < HeaderSize || dataOffset > segment.Length)
            return;
        var data = segment[dataOffset..];

        byte[]? ackToSend = null;
        byte[]? deliver = null;
        bool finished = false;
        bool closedNow = false;

        lock (_gate)
        {
            if (_state == State.Closed)
                return;

            if ((flags & FlagRst) != 0)
            {
                AbortLocked(new IOException($"RST recu du port {RemotePort}"));
                return;
            }

            if (_state == State.SynSent)
            {
                if ((flags & (FlagSyn | FlagAck)) != (FlagSyn | FlagAck) || ack != _sndNxt)
                    return;
                ReadPeerMss(segment[HeaderSize..dataOffset]);
                _rcvNxt = seq + 1;
                _sndUna = ack;
                _sndWnd = window;
                _unacked.Clear();
                _state = State.Established;
                ackToSend = BuildSegment(_sndNxt, _rcvNxt, FlagAck, [], []);
                _established.TrySetResult();
            }
            else
            {
                // Cumulative ACK, and every ACK carries the peer's window — a
                // pure window update (ack == sndUna) matters just as much.
                if ((flags & FlagAck) != 0 && !Later(_sndUna, ack))
                {
                    if (Later(ack, _sndUna))
                    {
                        _sndUna = ack;
                        _unacked.RemoveAll(u =>
                        {
                            int len = SegmentDataLength(u.Segment);
                            uint occupied = len > 0 ? (uint)len : 1u;   // SYN and FIN occupy one number
                            return !Later(u.Seq + occupied, ack);
                        });
                    }
                    _sndWnd = window;
                    var opened = _windowOpened;
                    _windowOpened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    opened.TrySetResult();

                    if (ack == _sndNxt)
                    {
                        if (_state == State.FinWait1) _state = State.FinWait2;
                        else if (_state is State.Closing or State.LastAck) { _state = State.Closed; closedNow = true; }
                    }
                }

                bool inOrder = seq == _rcvNxt;
                if (data.Length > 0 || (flags & FlagFin) != 0 || !inOrder)
                {
                    if (inOrder)
                    {
                        if (data.Length > 0)
                        {
                            deliver = data.ToArray();
                            _rcvNxt += (uint)data.Length;
                        }
                        if ((flags & FlagFin) != 0)
                        {
                            _rcvNxt += 1;
                            finished = true;
                            switch (_state)
                            {
                                case State.Established: _state = State.CloseWait; break;
                                case State.FinWait1: _state = State.Closing; break;
                                case State.FinWait2: _state = State.Closed; closedNow = true; break;
                            }
                        }
                    }
                    // In order or not, acknowledge what we have: a duplicate
                    // ACK is how the far end learns to resend, and a bare
                    // out-of-window segment is how it probes that we are alive.
                    ackToSend = BuildSegment(_sndNxt, _rcvNxt, FlagAck, [], []);
                }
            }
        }

        if (deliver is not null)
            _inbound.Writer.TryWrite(deliver);
        if (finished)
            _inbound.Writer.TryComplete();
        if (ackToSend is not null)
            _net.FireAndForget(_net.SendAsync(Ipv6.ProtoTcp, ackToSend), "ACK");
        if (closedNow)
            Forget();
    }

    private void ReadPeerMss(ReadOnlySpan<byte> options)
    {
        for (int i = 0; i < options.Length;)
        {
            byte kind = options[i];
            if (kind == 0) break;
            if (kind == 1) { i++; continue; }
            if (i + 1 >= options.Length) break;
            int len = options[i + 1];
            if (len < 2 || i + len > options.Length) break;
            if (kind == 2 && len == 4)
                _mss = Math.Min(_mss, BinaryPrimitives.ReadUInt16BigEndian(options[(i + 2)..]));
            i += len;
        }
    }

    private static int SegmentDataLength(byte[] segment) => segment.Length - (segment[12] >> 4) * 4;

    private static bool Later(uint a, uint b) => (int)(a - b) > 0;

    // --- Outbound ------------------------------------------------------------

    private async Task Transmit(uint seq, uint ack, byte flags, byte[] options, byte[] payload, bool track)
    {
        byte[] segment = BuildSegment(seq, ack, flags, options, payload);
        if (track)
            lock (_gate) _unacked.Add((seq, segment, DateTime.UtcNow, 0));
        await _net.SendAsync(Ipv6.ProtoTcp, segment).ConfigureAwait(false);
    }

    private byte[] BuildSegment(uint seq, uint ack, byte flags, byte[] options, ReadOnlySpan<byte> payload) =>
        BuildBare(_net, LocalPort, RemotePort, seq, ack, flags, options, payload);

    internal static byte[] BuildBare(TunnelNet net, ushort localPort, ushort remotePort,
        uint seq, uint ack, byte flags, byte[] options, ReadOnlySpan<byte> payload)
    {
        int optionsLength = (options.Length + 3) & ~3;
        byte[] segment = new byte[HeaderSize + optionsLength + payload.Length];
        var span = segment.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(span, localPort);
        BinaryPrimitives.WriteUInt16BigEndian(span[2..], remotePort);
        BinaryPrimitives.WriteUInt32BigEndian(span[4..], seq);
        BinaryPrimitives.WriteUInt32BigEndian(span[8..], ack);
        span[12] = (byte)(((HeaderSize + optionsLength) / 4) << 4);
        span[13] = flags;
        BinaryPrimitives.WriteUInt16BigEndian(span[14..], Window);
        options.CopyTo(span[HeaderSize..]);
        payload.CopyTo(span[(HeaderSize + optionsLength)..]);
        ushort sum = Ipv6.UpperLayerChecksum(net.Local, net.Peer, Ipv6.ProtoTcp, segment);
        BinaryPrimitives.WriteUInt16BigEndian(span[16..], sum);
        return segment;
    }

    private async Task RetransmitLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(100, _cts.Token).ConfigureAwait(false);
                List<byte[]> resend = [];
                lock (_gate)
                {
                    var now = DateTime.UtcNow;
                    for (int i = 0; i < _unacked.Count; i++)
                    {
                        var u = _unacked[i];
                        if (now - u.Sent < Rto)
                            continue;
                        if (u.Tries >= MaxRetries)
                        {
                            AbortLocked(new IOException($"port {RemotePort} : {MaxRetries} retransmissions sans accuse"));
                            return;
                        }
                        _unacked[i] = (u.Seq, u.Segment, now, u.Tries + 1);
                        resend.Add(u.Segment);
                    }
                }
                foreach (var segment in resend)
                    await _net.SendAsync(Ipv6.ProtoTcp, segment).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            lock (_gate) AbortLocked(new IOException("emission dans le tunnel en echec", exception));
        }
    }

    internal void Abort(Exception reason)
    {
        lock (_gate) AbortLocked(reason);
    }

    /// <summary>Why this connection stopped working, once it has.</summary>
    private Exception? Fault
    {
        get { lock (_gate) return _fault; }
    }

    /// <summary>
    /// Ends the connection, keeps the reason, and wakes everyone empty-handed.
    /// </summary>
    /// <remarks>
    /// The first reason wins: what follows a reset is only the wreckage of it.
    /// Waits are released with a result and never with an exception — see the
    /// class remarks — so the reason travels through <see cref="_fault"/> and is
    /// thrown by whichever call comes next. The inbound channel is the one
    /// exception, because a channel's completion fault is observed by the
    /// runtime itself and its readers do get told why the stream ended.
    /// </remarks>
    private void AbortLocked(Exception reason)
    {
        _fault ??= reason;
        _state = State.Closed;
        _unacked.Clear();
        _established.TrySetResult();
        _windowOpened.TrySetResult();
        _inbound.Writer.TryComplete(reason);
        Forget();
    }

    private void Forget()
    {
        _cts.Cancel();
        _net.Forget(this);
    }

    // --- Stream --------------------------------------------------------------

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_leftoverOffset >= _leftover.Length)
        {
            try
            {
                if (!await _inbound.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                    return 0;                                       // FIN drained: end of stream
                if (!_inbound.Reader.TryRead(out var chunk))
                    return 0;
                _leftover = chunk;
                _leftoverOffset = 0;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new IOException("connexion TCP rompue", exception);
            }
        }
        int count = Math.Min(buffer.Length, _leftover.Length - _leftoverOffset);
        _leftover.AsSpan(_leftoverOffset, count).CopyTo(buffer.Span);
        _leftoverOffset += count;
        return count;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            Task? wait = null;
            uint seq = 0, ack = 0;
            int count = 0;
            lock (_gate)
            {
                if (_state is not (State.Established or State.CloseWait))
                    throw _fault is { } fault
                        ? new IOException($"connexion TCP fermee : {fault.Message}", fault)
                        : new IOException("connexion TCP fermee");
                // Never send past the peer's window: what it cannot hold it
                // drops, and a dropped segment looks exactly like a dead link
                // to the retransmission timer. The comparison is made before
                // the subtraction because both sides are unsigned: a window the
                // phone has shrunk below what is already in flight would
                // otherwise wrap round to four billion bytes of room.
                uint inFlight = _sndNxt - _sndUna;
                int room = _sndWnd > inFlight ? (int)Math.Min(int.MaxValue, _sndWnd - inFlight) : 0;
                if (room <= 0)
                    wait = _windowOpened.Task;
                else
                {
                    count = Math.Min(_mss, Math.Min(room, buffer.Length - offset));
                    seq = _sndNxt;
                    ack = _rcvNxt;
                    _sndNxt += (uint)count;
                }
            }
            if (wait is not null)
            {
                await WaitForWindowAsync(wait, cancellationToken).ConfigureAwait(false);
                continue;
            }
            await Transmit(seq, ack, (byte)(FlagPsh | FlagAck), [], buffer.Slice(offset, count).ToArray(), track: true).ConfigureAwait(false);
            offset += count;
        }
    }

    /// <summary>
    /// Waits for the phone to make room, for no longer than
    /// <see cref="WritePatience"/> allows.
    /// </summary>
    /// <remarks>
    /// Deliberately outside <see cref="_gate"/> and outside the tunnel's own
    /// send lock: several connections and the video share that one pipe, and a
    /// channel whose window is shut must not be able to stop any of them. The
    /// only lock still held while this waits belongs to the caller above — which
    /// is precisely why it must end.
    ///
    /// <para>Past the deadline the connection is declared in default rather
    /// than retried. A receive window the daemon has kept shut for a second is
    /// not about to open: what is on the other side is a service that has
    /// stopped reading, and the answer to that is a fresh channel, which is
    /// what the <see cref="TimeoutException"/> lets the caller go and build.
    /// The lock it holds is released on the way out of its own
    /// <c>finally</c>.</para>
    /// </remarks>
    private async Task WaitForWindowAsync(Task opened, CancellationToken cancellationToken)
    {
        if (WritePatience == Timeout.InfiniteTimeSpan)
        {
            await opened.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        try
        {
            await opened.WaitAsync(WritePatience, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            var reason = new TimeoutException(
                $"port {RemotePort} : fenetre de reception fermee depuis {WritePatience.TotalMilliseconds:0} ms");
            Abort(reason);
            throw reason;
        }
    }

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            byte[]? outgoing = null;
            bool linger = false;
            lock (_gate)
            {
                switch (_state)
                {
                    case State.Established:
                    case State.CloseWait:
                        outgoing = BuildSegment(_sndNxt, _rcvNxt, (byte)(FlagFin | FlagAck), [], []);
                        _unacked.Add((_sndNxt, outgoing, DateTime.UtcNow, 0));
                        _sndNxt += 1;
                        _state = _state == State.CloseWait ? State.LastAck : State.FinWait1;
                        linger = true;
                        break;
                    case State.SynSent:
                        outgoing = BuildSegment(_sndNxt, 0, FlagRst, [], []);
                        AbortLocked(new ObjectDisposedException(nameof(TcpConnection)));
                        break;
                }
            }
            // A reader parked on this stream must wake up: end of stream.
            _inbound.Writer.TryComplete();
            if (outgoing is not null)
                _net.FireAndForget(_net.SendAsync(Ipv6.ProtoTcp, outgoing), "FIN");
            if (linger)
                _ = LingerAsync();
        }
        base.Dispose(disposing);
    }

    /// <summary>A close the phone never completes is reset rather than leaked.</summary>
    private async Task LingerAsync()
    {
        try { await Task.Delay(Linger, _cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }      // closed properly in time
        byte[]? rst = null;
        lock (_gate)
        {
            if (_state != State.Closed)
            {
                rst = BuildSegment(_sndNxt, _rcvNxt, (byte)(FlagRst | FlagAck), [], []);
                AbortLocked(new IOException("fermeture TCP jamais achevee par le telephone"));
            }
        }
        if (rst is not null)
            _net.FireAndForget(_net.SendAsync(Ipv6.ProtoTcp, rst), "RST");
    }
}
