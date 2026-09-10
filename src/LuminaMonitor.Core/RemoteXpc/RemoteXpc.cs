using System.Buffers.Binary;
using System.Text;
using System.Threading.Channels;

namespace LuminaMonitor.Core.RemoteXpc;

/// <summary>
/// A RemoteXPC connection: Apple's XPC messages carried in HTTP/2 DATA frames
/// over a TCP connection through the tunnel.
/// </summary>
/// <remarks>
/// This is not HTTP. The HTTP/2 layer is used as a framing and multiplexing
/// substrate only: two long-lived streams are opened with empty HEADERS
/// blocks, and XPC messages travel in DATA frames. Stream 1 is the root
/// channel (requests and, for some daemons, their replies); stream 3 is the
/// reply channel, on which the CoreDevice daemons answer and volunteer their
/// own traffic. The opening choreography — settings,
/// window update, the two streams, an empty message, a terminator with flags
/// 0x0201 and an init-handshake marker — mirrors what devicectl does on the
/// wire, in that order, because the daemon is picky about it.
///
/// <para>One pump task owns every read for the life of the connection. That
/// is what keeps the link healthy while input reports stream out with no
/// reply expected: pings are answered, the daemon's flow-control windows are
/// replenished, a GOAWAY is seen the moment it arrives, and a message split
/// across several frames — the daemon caps frames at 16 KiB unless told
/// otherwise — is reassembled from its own length before being decoded.
/// Requests are serialised and their replies matched in order; a timeout
/// cancels only the wait, never a socket read, so the framing stays
/// aligned.</para>
///
/// <para>The HTTP/2 subset is hand-rolled on purpose: <c>System.Net.Http</c>
/// cannot open a stream without HTTP semantics, and only SETTINGS, HEADERS,
/// DATA, WINDOW_UPDATE, PING, GOAWAY and RST_STREAM ever appear here.</para>
/// </remarks>
internal sealed class RemoteXpc : IDisposable
{
    private const int RootChannel = 1;
    private const int ReplyChannel = 3;
    private const byte FrameData = 0, FrameHeaders = 1, FrameRstStream = 3, FrameSettings = 4,
        FramePing = 6, FrameGoAway = 7, FrameWindowUpdate = 8;
    private const uint OurWindow = 16 * 1024 * 1024;
    private const uint OurMaxFrame = 0xFFFFFF;
    private const ulong MaxBody = 64 * 1024 * 1024;

    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private readonly Channel<Xpc.Message> _replies = Channel.CreateUnbounded<Xpc.Message>();
    private readonly Dictionary<int, MemoryStream> _partial = new();
    private readonly TaskCompletionSource _settingsSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private ulong _nextRootId;
    private int _staleReplies;
    private Task? _pump;
    private Exception? _fault;

    // The daemon's view of what we may still send.
    private long _connWindow = 65535, _rootWindow = 65535;
    private uint _peerInitialWindow = 65535, _peerMaxFrame = 16384;
    private TaskCompletionSource _windowOpened = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Action<string>? Log { get; set; }

    /// <summary>Input reports discarded because the daemon's window was exhausted.</summary>
    public long DroppedMessages { get; private set; }

    private int _writeWaiters;

    /// <summary>
    /// Callers parked on the write lock right now, and what the connection
    /// underneath has sent without an acknowledgement yet.
    /// </summary>
    /// <remarks>
    /// The two halves of "how deep is the send queue". Anything above one waiter
    /// while input is streaming means reports are being produced faster than the
    /// wire drains them, which is felt as a pointer that lags further behind the
    /// faster the hand moves.
    /// </remarks>
    public (int Waiters, int Segments, long Bytes) SendDepth
    {
        get
        {
            var (segments, bytes) = _stream is Tunnel.TcpConnection tcp ? tcp.SendDepth : (0, 0L);
            return (Volatile.Read(ref _writeWaiters), segments, bytes);
        }
    }

    public RemoteXpc(Stream stream) => _stream = stream;

    public async Task ConnectAsync()
    {
        await _stream.WriteAsync("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"u8.ToArray()).ConfigureAwait(false);
        await WriteFrameAsync(FrameSettings, 0, 0, Settings((3, 100), (4, OurWindow), (5, OurMaxFrame))).ConfigureAwait(false);
        await WriteFrameAsync(FrameWindowUpdate, 0, 0, U32(OurWindow - 65535)).ConfigureAwait(false);

        await WriteFrameAsync(FrameHeaders, 0x4, RootChannel, []).ConfigureAwait(false);          // END_HEADERS, empty block
        await WriteFrameAsync(FrameData, 0, RootChannel, Xpc.BuildWrapper(Xpc.FlagAlwaysSet, TakeId(), new Dictionary<string, object?>())).ConfigureAwait(false);
        await WriteFrameAsync(FrameHeaders, 0x4, ReplyChannel, []).ConfigureAwait(false);
        await WriteFrameAsync(FrameData, 0, RootChannel, Xpc.BuildWrapper(0x0201, 0, null)).ConfigureAwait(false);
        await WriteFrameAsync(FrameData, 0, ReplyChannel, Xpc.BuildWrapper(Xpc.FlagAlwaysSet | Xpc.FlagInitHandshake, 0, null)).ConfigureAwait(false);
        await _stream.FlushAsync().ConfigureAwait(false);

        _pump = Task.Run(PumpAsync);

        // The daemon's SETTINGS is the sign of life.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await _settingsSeen.Task.WaitAsync(cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw new TimeoutException(CoreTexts.Current.RemoteXpcNoSettings); }
        // A channel that died before saying anything releases that wait empty,
        // like every other wait here, so the reason is read rather than caught.
        if (Fault is { } fault)
            throw new IOException($"RemoteXPC : {fault.Message}", fault);
    }

    /// <summary>Why this channel stopped working, once it has.</summary>
    private Exception? Fault
    {
        get { lock (_gate) return _fault; }
    }

    /// <summary>Sends a request on the root channel and waits for its reply.</summary>
    /// <remarks>
    /// Requests are serialised: the daemon answers in order, so the next
    /// non-empty message on the root channel is the reply. An empty
    /// dictionary is never a reply — the daemon answers the opening message
    /// with one. A request that timed out leaves its reply in flight; that
    /// reply is counted as stale and skipped when it eventually lands.
    /// </remarks>
    public async Task<Dictionary<string, object?>> SendReceiveAsync(Dictionary<string, object?> request, TimeSpan? timeout = null)
    {
        await _requestLock.WaitAsync().ConfigureAwait(false);
        try
        {
            // Whatever is queued now arrived before this request existed, so it
            // cannot be its reply: the daemon volunteers notifications on the
            // root channel, and one must never be mistaken for an answer.
            while (_replies.Reader.TryRead(out var unsolicited))
                if (unsolicited.Body is Dictionary<string, object?> d && d.Count > 0)
                    Log?.Invoke($"message spontane ignore (id {unsolicited.MessageId}, {d.Count} cle(s))");

            await SendDataAsync(RootChannel, Xpc.FlagAlwaysSet | Xpc.FlagWantingReply | DataFlag(request), request, waitForWindow: true).ConfigureAwait(false);

            using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
            try
            {
                while (true)
                {
                    var message = await _replies.Reader.ReadAsync(cts.Token).ConfigureAwait(false);
                    if (message.Body is not Dictionary<string, object?> body || body.Count == 0)
                        continue;
                    if (_staleReplies > 0) { _staleReplies--; continue; }
                    return body;
                }
            }
            catch (OperationCanceledException)
            {
                _staleReplies++;
                throw new TimeoutException(CoreTexts.Current.RemoteXpcNoReply);
            }
            catch (ChannelClosedException exception)
            {
                throw new IOException("RemoteXPC : connexion fermee", exception.InnerException ?? exception);
            }
        }
        finally
        {
            _requestLock.Release();
        }
    }

    /// <summary>
    /// Fire-and-forget request — the hot path for input reports. When the
    /// daemon's window is exhausted the report is dropped and counted rather
    /// than queued: a late touch is worse than a missing one.
    /// </summary>
    public Task SendAsync(Dictionary<string, object?> request) =>
        SendDataAsync(RootChannel, Xpc.FlagAlwaysSet | DataFlag(request), request, waitForWindow: false);

    private static uint DataFlag(Dictionary<string, object?> body) => body.Count > 0 ? Xpc.FlagData : 0;

    private ulong TakeId()
    {
        lock (_gate) return _nextRootId++;
    }

    /// <summary>Ids are shared between the two directions: never fall back behind one already seen.</summary>
    private void BumpId(ulong seen)
    {
        lock (_gate) _nextRootId = Math.Max(_nextRootId, seen + 1);
    }

    // --- Sending ---------------------------------------------------------------

    private async Task SendDataAsync(int stream, uint flags, Dictionary<string, object?> body, bool waitForWindow)
    {
        Interlocked.Increment(ref _writeWaiters);
        try { await _writeLock.WaitAsync().ConfigureAwait(false); }
        finally { Interlocked.Decrement(ref _writeWaiters); }
        try
        {
            byte[] wrapper = Xpc.BuildWrapper(flags, TakeId(), body);
            for (int offset = 0; offset < wrapper.Length;)
            {
                int chunk = (int)Math.Min(_peerMaxFrame, (uint)(wrapper.Length - offset));
                while (true)
                {
                    Task? wait = null;
                    lock (_gate)
                    {
                        // A window that will never open again is not worth
                        // waiting on: the pump releases the wait when it dies,
                        // and this is where the reason it left behind is read.
                        if (_fault is { } fault)
                            throw new IOException($"RemoteXPC : {fault.Message}", fault);
                        if (Math.Min(_connWindow, stream == RootChannel ? _rootWindow : long.MaxValue) >= chunk)
                        {
                            _connWindow -= chunk;
                            if (stream == RootChannel) _rootWindow -= chunk;
                        }
                        else if (waitForWindow)
                            wait = _windowOpened.Task;
                        else
                        {
                            DroppedMessages++;
                            if (DroppedMessages == 1 || DroppedMessages % 100 == 0)
                                Log?.Invoke($"fenetre HTTP/2 epuisee : {DroppedMessages} message(s) abandonne(s)");
                            return;
                        }
                    }
                    if (wait is null) break;
                    await wait.WaitAsync(_cts.Token).ConfigureAwait(false);
                }
                await WriteFrameLockedAsync(FrameData, 0, stream, wrapper.AsMemory(offset, chunk)).ConfigureAwait(false);
                offset += chunk;
            }
            await _stream.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task WriteFrameAsync(byte type, byte flags, int stream, byte[] payload)
    {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try { await WriteFrameLockedAsync(type, flags, stream, payload).ConfigureAwait(false); }
        finally { _writeLock.Release(); }
    }

    private async Task WriteFrameLockedAsync(byte type, byte flags, int stream, ReadOnlyMemory<byte> payload)
    {
        if (payload.Length > 0xFFFFFF)
            throw new ArgumentOutOfRangeException(nameof(payload), "trame HTTP/2 trop grande");
        byte[] frame = new byte[9 + payload.Length];
        frame[0] = (byte)(payload.Length >> 16);
        frame[1] = (byte)(payload.Length >> 8);
        frame[2] = (byte)payload.Length;
        frame[3] = type;
        frame[4] = flags;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(5), (uint)stream);
        payload.CopyTo(frame.AsMemory(9));
        await _stream.WriteAsync(frame).ConfigureAwait(false);
    }

    // --- Receiving: the pump ---------------------------------------------------

    private async Task PumpAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
                await HandleFrameAsync(await ReadFrameAsync().ConfigureAwait(false)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (!_cts.IsCancellationRequested)
                Log?.Invoke($"RemoteXPC : {exception.Message}");
            // Kept, not pushed. A TaskCompletionSource nobody happens to be
            // parked on carries its exception to the finalizer, which raises it
            // as an unobserved fault of the whole process long afterwards —
            // "Unable to read beyond the end of the stream", in a journal that
            // has moved on. The waits are released empty and read the reason
            // below; the replies channel keeps its own, because the runtime
            // observes a channel's completion fault itself and its readers are
            // told why the channel ended.
            lock (_gate)
            {
                _fault ??= exception;
                OpenWindowLocked();
            }
            _replies.Writer.TryComplete(exception);
            _settingsSeen.TrySetResult();
        }
    }

    private readonly record struct Frame(byte Type, byte Flags, int Stream, byte[] Payload);

    private async Task<Frame> ReadFrameAsync()
    {
        byte[] header = new byte[9];
        await _stream.ReadExactlyAsync(header).ConfigureAwait(false);
        int length = (header[0] << 16) | (header[1] << 8) | header[2];
        int stream = (int)(BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(5)) & 0x7FFFFFFF);
        byte[] payload = new byte[length];
        await _stream.ReadExactlyAsync(payload).ConfigureAwait(false);
        return new Frame(header[3], header[4], stream, payload);
    }

    private async Task HandleFrameAsync(Frame frame)
    {
        switch (frame.Type)
        {
            case FrameSettings:
                if ((frame.Flags & 0x1) != 0) return;                    // our ACK, echoed back
                ApplyPeerSettings(frame.Payload);
                await WriteFrameAsync(FrameSettings, 0x1, 0, []).ConfigureAwait(false);
                _settingsSeen.TrySetResult();
                return;

            case FramePing:
                if ((frame.Flags & 0x1) == 0)
                    await WriteFrameAsync(FramePing, 0x1, 0, frame.Payload).ConfigureAwait(false);
                return;

            case FrameWindowUpdate:
            {
                uint increment = BinaryPrimitives.ReadUInt32BigEndian(frame.Payload) & 0x7FFFFFFF;
                lock (_gate)
                {
                    if (frame.Stream == 0) _connWindow += increment;
                    else if (frame.Stream == RootChannel) _rootWindow += increment;
                    OpenWindowLocked();
                }
                return;
            }

            case FrameGoAway:
            {
                uint error = BinaryPrimitives.ReadUInt32BigEndian(frame.Payload.AsSpan(4));
                string debug = frame.Payload.Length > 8 ? Encoding.UTF8.GetString(frame.Payload.AsSpan(8)) : "";
                throw new IOException($"GOAWAY du telephone (erreur {error}) {debug}".TrimEnd());
            }

            case FrameRstStream:
                throw new IOException($"RST_STREAM sur le flux {frame.Stream}");

            case FrameData:
                await HandleDataAsync(frame).ConfigureAwait(false);
                return;
        }
    }

    private void ApplyPeerSettings(byte[] payload)
    {
        lock (_gate)
        {
            for (int i = 0; i + 6 <= payload.Length; i += 6)
            {
                ushort id = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(i));
                uint value = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(i + 2));
                switch (id)
                {
                    case 4:                                                   // INITIAL_WINDOW_SIZE: shifts open streams
                        _rootWindow += (long)value - _peerInitialWindow;
                        _peerInitialWindow = value;
                        break;
                    case 5:                                                   // MAX_FRAME_SIZE
                        _peerMaxFrame = Math.Clamp(value, 16384, 0xFFFFFF);
                        break;
                }
            }
            OpenWindowLocked();
        }
    }

    private void OpenWindowLocked()
    {
        var opened = _windowOpened;
        _windowOpened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        opened.TrySetResult();
    }

    private async Task HandleDataAsync(Frame frame)
    {
        ReadOnlyMemory<byte> data = frame.Payload;
        if ((frame.Flags & 0x8) != 0 && data.Length > 0)                    // PADDED
            data = data.Slice(1, data.Length - 1 - frame.Payload[0]);

        // Keep the daemon's windows open: acknowledge every byte received.
        if (frame.Payload.Length > 0)
        {
            await WriteFrameAsync(FrameWindowUpdate, 0, 0, U32((uint)frame.Payload.Length)).ConfigureAwait(false);
            await WriteFrameAsync(FrameWindowUpdate, 0, frame.Stream, U32((uint)frame.Payload.Length)).ConfigureAwait(false);
        }

        if (!_partial.TryGetValue(frame.Stream, out var buffer))
            _partial[frame.Stream] = buffer = new MemoryStream();
        buffer.Write(data.Span);

        // Extract every complete message; a message knows its own length.
        while (true)
        {
            var all = buffer.GetBuffer().AsMemory(0, (int)buffer.Length);
            if (all.Length < 24) break;
            if (BinaryPrimitives.ReadUInt32LittleEndian(all.Span) != Xpc.WrapperMagic)
                throw new InvalidDataException($"flux XPC {frame.Stream} desynchronise");
            ulong bodyLength = BinaryPrimitives.ReadUInt64LittleEndian(all.Span[8..]);
            if (bodyLength > MaxBody)
                throw new InvalidDataException($"corps XPC de {bodyLength} octets");
            int total = (int)(24 + bodyLength);
            if (all.Length < total) break;

            Xpc.Message? message = null;
            try { message = Xpc.ParseWrapper(all.Span[..total]); }
            catch (InvalidDataException exception) { Log?.Invoke($"message XPC ignore sur le flux {frame.Stream} : {exception.Message}"); }

            byte[] rest = all[total..].ToArray();
            buffer.SetLength(0);
            buffer.Write(rest);

            if (message is not null)
                await DispatchAsync(frame.Stream, message).ConfigureAwait(false);
        }

        if ((frame.Flags & 0x1) != 0)                                        // END_STREAM
        {
            if (frame.Stream == RootChannel)
                throw new IOException("flux racine ferme par le telephone");
            Log?.Invoke($"flux {frame.Stream} ferme par le telephone");
        }
    }

    private async Task DispatchAsync(int stream, Xpc.Message message)
    {
        Log?.Invoke($"  <- flux {stream} : flags 0x{message.Flags:X} id {message.MessageId} " +
            $"corps {(message.Body is Dictionary<string, object?> d ? $"dict[{d.Count}]" : message.Body?.GetType().Name ?? "aucun")}");

        if (stream == RootChannel)
            BumpId(message.MessageId);

        if ((message.Flags & Xpc.FlagWantingReply) != 0 && message.Body is null)
        {
            // Heartbeat-style ping from the daemon: answer in kind.
            await WriteFrameAsync(FrameData, 0, stream, Xpc.BuildWrapper(Xpc.FlagAlwaysSet | Xpc.FlagReply, message.MessageId, null)).ConfigureAwait(false);
            return;
        }

        // A daemon may answer on the reply channel rather than the root one
        // (the CoreDevice services do): any non-empty dictionary, whichever
        // stream carried it, is a candidate reply for the request in flight.
        if (stream == RootChannel || (message.Body is Dictionary<string, object?> body && body.Count > 0))
            _replies.Writer.TryWrite(message);
    }

    // --- Helpers -----------------------------------------------------------------

    private static byte[] Settings(params (ushort Id, uint Value)[] settings)
    {
        byte[] payload = new byte[6 * settings.Length];
        for (int i = 0; i < settings.Length; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(i * 6), settings[i].Id);
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(i * 6 + 2), settings[i].Value);
        }
        return payload;
    }

    private static byte[] U32(uint value)
    {
        byte[] b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, value);
        return b;
    }

    /// <summary>
    /// Lets the daemon hang up first, then closes our end properly.
    /// </summary>
    /// <remarks>
    /// The pump task ends exactly when the channel does — an END_STREAM on the
    /// root channel, a GOAWAY, or the TCP connection running out — so waiting on
    /// it is waiting on the phone. What follows either way is
    /// <see cref="Dispose"/>, which sends a FIN through
    /// <see cref="Tunnel.TcpConnection"/> rather than a RST: a service told the
    /// truth about being closed is a service that can be opened again, and the
    /// display daemon that freezes after a few sessions is being cut off mid
    /// sentence today. Returns whether the phone closed it first, and how long
    /// the wait took, so the difference can be read in the journal.
    /// </remarks>
    public async Task<(bool ByThePhone, double Milliseconds)> CloseAsync(TimeSpan patience)
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        bool byThePhone = false;
        if (_pump is Task pump)
        {
            try
            {
                await pump.WaitAsync(patience).ConfigureAwait(false);
                byThePhone = true;
            }
            catch (TimeoutException) { }
            catch (Exception) { byThePhone = true; }         // the pump only ends with the channel
        }
        Dispose();
        double milliseconds = (System.Diagnostics.Stopwatch.GetTimestamp() - start)
            * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        return (byThePhone, milliseconds);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _replies.Writer.TryComplete();
        _stream.Dispose();
    }
}
