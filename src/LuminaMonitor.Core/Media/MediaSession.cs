using System.Security.Cryptography;
using LuminaMonitor.Core.RemoteXpc;
using LuminaMonitor.Core.Tunnel;
using XpcService = LuminaMonitor.Core.RemoteXpc.RemoteXpc;

namespace LuminaMonitor.Core.Media;

/// <summary>
/// The video stream: the HID gate, and now the picture as well.
/// </summary>
/// <remarks>
/// While a media stream runs, backboardd treats our HID surfaces as built-in
/// and delivers the reports; that is why the stream is opened before the first
/// touch and kept up for the whole session. What arrives on the receiver port
/// is now taken seriously: RTP goes to the depacketizer and, if asked, to a
/// decoder thread, while the RTCP multiplexed on the same port is answered
/// once a second — without those reports the phone stops encoding after twenty
/// seconds.
///
/// <para>Order matters at both ends. The UDP port is claimed and listening
/// <b>before</b> <c>startmediastream</c> is even sent, because the phone starts
/// sending the moment it answers and the parameter sets and the only key frame
/// of the session are in those first milliseconds. Stopping is best effort:
/// the phone usually drops the channel while stopping, which is success.</para>
/// </remarks>
internal sealed class MediaSession
{
    /// <summary>How many undecoded pictures may wait before the backlog is declared lost.</summary>
    private const int QueueDepth = 16;

    /// <summary>How often the watch looks at the packet counter.</summary>
    private const int WatchIntervalMs = 500;

    /// <summary>How long the phone is given to hang up on a service before we do.</summary>
    private static readonly TimeSpan HangUpPatience = TimeSpan.FromSeconds(1);

    /// <summary>The pause between stopping a stream and asking for the next one.</summary>
    private const int SettleMs = 1000;

    private readonly TunnelNet _net;
    private readonly Rsd _rsd;
    private readonly H264Depacketizer _depacketizer = new();
    private readonly Queue<AccessUnit> _queue = new();
    private readonly object _queueGate = new();
    private readonly System.Diagnostics.Stopwatch _watchClock = System.Diagnostics.Stopwatch.StartNew();
    private IProgress<string>? _progress;
    private Action<string>? _serviceTrace;
    private CancellationTokenSource? _watching;
    private StreamWatchdog? _watchdog;
    private Task? _monitor;
    private XpcService? _display;
    private XpcUuid _sessionId;
    private ushort _udpPort;
    private RtcpSession? _rtcp;
    private CancellationTokenSource? _stopping;
    private Task? _reports;
    private Task? _rctlLoop;
    private Thread? _decoder;
    private long _packets, _bytes, _rtcpPackets, _framesDecoded, _lateDrops;
    private long _pendingUnits, _maxPendingUnits;
    private double _lastLatencyMs, _wireMs, _queueMs, _decodeMs, _maxQueueMs;
    private long _lastKeyFrameRequestTicks;
    private long _keyFramesWanted;

    // --- Drift ------------------------------------------------------------------
    // The gap between the phone's clock and ours, both counted from the first
    // picture of the session, closed on the marker bit that ends a picture.
    // Flat means the stream keeps up whatever it carries; a climb towards a
    // second means pictures are reaching us that old, and no counter below this
    // one can see it.

    /// <summary>The stream's RTP clock as the captures show it: 24 kHz, 400 units a picture at sixty a second.</summary>
    private const double MediaClockKhz = 24.0;

    private readonly object _driftGate = new();
    private bool _haveDriftOrigin;
    private uint _driftOriginTimestamp, _driftLastTimestamp;
    private long _driftOriginTicks, _driftLastTicks;
    private double _driftSum, _driftMax, _driftLast;
    private long _driftFrames;

    public MediaSession(TunnelNet net, Rsd rsd)
    {
        _net = net;
        _rsd = rsd;
        _depacketizer.Completed += Enqueue;
    }

    /// <summary>Every RTP datagram the phone sends to our tunnel address, control packets included.</summary>
    public event Action<ReadOnlyMemory<byte>>? RtpPacket;

    /// <summary>Every decoded picture. The pixels belong to the decoder until the handler returns.</summary>
    public event Action<VideoFrame>? FrameDecoded;

    /// <summary>
    /// The stream went quiet, and this is what the watchdog wants done about it.
    /// </summary>
    /// <remarks>
    /// Deliberately a <see cref="Func{T, TResult}"/> rather than a plain event:
    /// the answer takes seconds (a key frame request and its grace period, a
    /// whole new media session, a developer image taken down and put back up)
    /// and the watch must not raise the next rung of the ladder while the
    /// previous one is still being climbed. The watch task awaits the handler,
    /// so the handler is the only thing running.
    /// </remarks>
    public event Func<StallAction, Task>? StreamStalled;

    /// <summary>Set before <see cref="StartAsync"/> to run the decoder; off, only the counters move.</summary>
    public bool DecodeVideo { get; set; }

    /// <summary>Whether the phone accepted the stream. False means the gate stayed shut.</summary>
    public bool Streaming { get; private set; }

    /// <summary>Which codec banks the offer advertises. Bank 123, H.264, is the one Windows decodes.</summary>
    public VideoCodecs Codecs { get; set; } = VideoCodecs.AvcOnly;

    /// <summary>Every lever of the negotiation and of the feedback loop, in one value.</summary>
    public StreamTuning Tuning { get; set; } = StreamTuning.Default;

    /// <summary>The daemon's refusal, whole and untouched, when the offer was turned down.</summary>
    public string? Failure { get; private set; }

    /// <summary>How many RTP datagrams arrived since the port was claimed.</summary>
    public long PacketCount => Interlocked.Read(ref _packets);

    /// <summary>What the feedback loop has sent, for the probe's report; zeroes when it is off.</summary>
    public (long Receipts, long Reports) RctlCounts => _rtcp?.RctlCounts ?? (0, 0);

    /// <summary>The end-to-end delay the sender reports allow to be measured; NaN before the first one.</summary>
    public double PipelineMs => _rtcp?.PipelineMs ?? double.NaN;

    /// <summary>The wall clock the phone published in its last sender report, or null.</summary>
    public DateTime? SenderClock => _rtcp?.SenderClock;

    /// <summary>The daemon's answer to <c>startmediastream</c>, untouched.</summary>
    public Dictionary<string, object?>? StreamConfig { get; private set; }

    public MediaStats Stats
    {
        get
        {
            double mean, max, last, clock;
            lock (_driftGate)
            {
                mean = _driftFrames > 0 ? _driftSum / _driftFrames : 0;
                max = _driftMax;
                last = _driftLast;
                clock = MeasuredClockKhz();
            }
            return new MediaStats(
                Interlocked.Read(ref _packets), Interlocked.Read(ref _bytes), Interlocked.Read(ref _rtcpPackets),
                _depacketizer.AccessUnits, _depacketizer.KeyFrames, _depacketizer.Lost, _depacketizer.Dropped,
                _depacketizer.Trailers, Interlocked.Read(ref _framesDecoded), Interlocked.Read(ref _lateDrops),
                _lastLatencyMs, _rtcp?.ReportsSent ?? 0, _rtcp?.SenderReports ?? 0, _rtcp?.KeyFrameRequests ?? 0,
                Interlocked.Read(ref _pendingUnits), Interlocked.Read(ref _maxPendingUnits),
                _wireMs, _queueMs, _decodeMs, _maxQueueMs,
                mean, max, last, clock,
                _h264?.InFlight ?? 0, _h264?.LowLatencySet ?? false);
        }
    }

    /// <summary>
    /// The RTP clock as this session actually ran, in kHz.
    /// </summary>
    /// <remarks>
    /// The drift above is computed against the nominal 24 kHz, so that the
    /// figure means the same thing here as in the probe's motion test. That
    /// only holds if the phone's clock really is 24 kHz: measured against our
    /// own over tens of seconds it is, and a reading that wandered from it
    /// would make every drift on the line an artefact of the wrong divisor.
    /// One number to rule that out. Meaningless — and reported as zero — until
    /// the session has run long enough to divide by.
    /// </remarks>
    private double MeasuredClockKhz()
    {
        double seconds = (_driftLastTicks - _driftOriginTicks) / (double)System.Diagnostics.Stopwatch.Frequency;
        if (!_haveDriftOrigin || seconds < 1.0)
            return 0;
        return unchecked((int)(_driftLastTimestamp - _driftOriginTimestamp)) / seconds / 1000.0;
    }

    /// <summary>Forgets the high-water marks, so the next window measures itself.</summary>
    public void ResetPeaks()
    {
        Interlocked.Exchange(ref _maxPendingUnits, Interlocked.Read(ref _pendingUnits));
        _maxQueueMs = 0;
        lock (_driftGate)
        {
            _driftSum = 0;
            _driftMax = 0;
            _driftFrames = 0;
        }
    }

    public async Task StartAsync(IProgress<string>? progress = null, Action<string>? serviceTrace = null)
    {
        void Say(string line) => progress?.Report(line);

        // Kept so a restart can say the same things in the same places without
        // the caller having to be there to hand them over again.
        _progress = progress;
        _serviceTrace = serviceTrace;

        // A new stream is a new RTP clock with a new origin: keeping the old one
        // would make the first picture of the restart look hours late.
        lock (_driftGate)
        {
            _haveDriftOrigin = false;
            _driftSum = _driftMax = _driftLast = 0;
            _driftFrames = 0;
        }

        _display = await _rsd.OpenAsync(DisplayService.ServiceName, serviceTrace);
        _stopping = new CancellationTokenSource();
        _udpPort = _net.ListenUdp(OnDatagram);
        if (DecodeVideo)
        {
            _decoder = new Thread(DecodeLoop) { IsBackground = true, Name = "LuminaDecode" };
            _decoder.SetApartmentState(ApartmentState.MTA);
            _decoder.Start();
        }
        _sessionId = new XpcUuid(RandomNumberGenerator.GetBytes(16));
        byte[] offer = MediaOffer.BuildVideo(MediaOffer.NewCallId(),
            (uint)RandomNumberGenerator.GetInt32(int.MaxValue), hostModel: Tuning.HostModel,
            allowRtcpFb: Tuning.AllowRtcpFb, ltrpEnabled: Tuning.LtrpEnabled, fecEnabled: Tuning.FecEnabled,
            tilesPerFrame: Tuning.TilesPerFrame, codecs: Codecs, options: Tuning.Offer);
        Say($"Ouverture du flux video vers [{_net.Local}]:{_udpPort}…");
        try
        {
            StreamConfig = await DisplayService.StartVideoStreamAsync(_display, _net.Local.ToString(), _udpPort,
                _net.Peer.ToString(), _sessionId, offer,
                clientSupportedFeatures: Tuning.ClientSupportedFeatures,
                accessNetworkType: Tuning.AccessNetworkType,
                transportProtocolType: Tuning.TransportProtocolType);
            Streaming = true;
            StartReports(Say);
            await Task.Delay(300);
        }
        catch (InvalidOperationException exception)
        {
            // The gate may be closed on this iOS; the reports are still worth
            // sending, to learn whether the daemon takes them without it.
            Failure = exception.Message;
            string reason = exception.Message.Replace("\r", "").Replace("\n", " ");
            Say($"Porte media indisponible — essai du tap sans flux video. Motif : {(reason.Length > 200 ? reason[..200] + "…" : reason)}");
        }
    }

    /// <summary>Asks the encoder for a fresh key frame, at most once a second.</summary>
    public Task RequestKeyFrameAsync()
    {
        if (_rtcp is null || !_rtcp.Usable)
            return Task.CompletedTask;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (now - _lastKeyFrameRequestTicks < System.Diagnostics.Stopwatch.Frequency)
            return Task.CompletedTask;
        _lastKeyFrameRequestTicks = now;
        return _rtcp.RequestKeyFrameAsync();
    }

    /// <summary>
    /// Starts watching the pace of the stream.
    /// </summary>
    /// <remarks>
    /// Called once the whole session is up rather than at the end of
    /// <see cref="StartAsync"/>, because the HID channel opens between the two:
    /// a stall raised while it does would restart a stream nobody was watching
    /// yet.
    /// </remarks>
    public void StartWatch()
    {
        if (!Streaming || _monitor is not null)
            return;
        _watching = new CancellationTokenSource();
        _watchdog = new StreamWatchdog(_watchClock.Elapsed.TotalSeconds);
        var token = _watching.Token;
        _monitor = Task.Run(() => WatchAsync(token), token);
    }

    /// <summary>
    /// Stops this stream properly and opens a new one, leaving everything below
    /// it alone.
    /// </summary>
    /// <remarks>
    /// The tunnel, the service directory and the HID channel are all still good
    /// when the daemon abandons a media session — it is the session that has to
    /// be rebuilt, and rebuilding the rest costs the person the whole climb
    /// again for nothing. Returns what the watchdog makes of the outcome: a
    /// second consecutive failure asks for a soft reset.
    /// </remarks>
    public async Task<StallAction> RestartAsync()
    {
        _progress?.Report("Relance du flux video…");
        await StopStreamAsync(final: false);

        // A breath between the stop and the start. The daemon is known to
        // refuse a new session for a while after the previous one, and to renew
        // the refusal at every attempt: asking in the same millisecond it was
        // told to stop is the surest way to be turned down. One second is not a
        // cure — the cure, if the hypothesis holds, is the proper stop above —
        // but it costs nothing next to the ladder's own five.
        await Task.Delay(SettleMs);

        bool restarted;
        try
        {
            await StartAsync(_progress, _serviceTrace);
            restarted = Streaming;
        }
        catch (Exception exception)
        {
            _progress?.Report($"Relance du flux refusee : {exception.Message}");
            restarted = false;
        }
        _progress?.Report(restarted ? "Flux video relance." : "Flux video non relance.");
        return _watchdog?.OnRestart(restarted, _watchClock.Elapsed.TotalSeconds) ?? StallAction.None;
    }

    public Task StopAsync() => StopStreamAsync(final: true);

    /// <summary>
    /// Takes the stream down the way the daemon expects, and times it.
    /// </summary>
    /// <remarks>
    /// The order is the whole point. The report loop stops first, so nothing
    /// contradicts what comes next; then an RTCP BYE, which says the receiver
    /// is leaving while the session it belongs to still exists; then
    /// <c>stopmediastream</c>; and only then the channel itself, and even that
    /// is the phone's to close — we wait a second for it and send a FIN rather
    /// than a RST if it does not. The working hypothesis for a display service
    /// that freezes after a few sessions is that it was being cut off mid
    /// sentence every time.
    ///
    /// <para>A final stop also ends the watch. The watch task is cancelled and
    /// dropped rather than awaited: a stall handler is what usually calls this,
    /// and awaiting the task that is calling you is a deadlock.</para>
    /// </remarks>
    private async Task StopStreamAsync(bool final)
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        if (final)
        {
            _watching?.Cancel();
            _watching = null;
            _watchdog = null;
            _monitor = null;
        }
        _stopping?.Cancel();
        if (_reports is not null) { try { await _reports; } catch (Exception) { } _reports = null; }
        if (_rctlLoop is not null) { try { await _rctlLoop; } catch (Exception) { } _rctlLoop = null; }

        var display = _display;
        _display = null;
        if (_rtcp is { Usable: true })
        {
            try { await _rtcp.SendByeAsync(); }
            catch (Exception exception) { _net.Log?.Invoke($"RTCP BYE non envoye : {exception.Message}"); }
        }
        if (display is not null && Streaming)
            await DisplayService.StopMediaStreamAsync(display, _sessionId);
        Streaming = false;
        _rtcp = null;

        if (_udpPort != 0) { _net.StopUdp(_udpPort); _udpPort = 0; }
        lock (_queueGate) Monitor.PulseAll(_queueGate);
        _decoder?.Join(TimeSpan.FromSeconds(2));
        _decoder = null;
        _depacketizer.Reset();

        if (display is not null)
        {
            var (byThePhone, milliseconds) = await display.CloseAsync(HangUpPatience);
            _net.Log?.Invoke($"service d'affichage ferme en {milliseconds:F0} ms "
                + (byThePhone ? "par le telephone." : "de notre cote (le telephone n'a pas raccroche)."));
        }
        double total = (System.Diagnostics.Stopwatch.GetTimestamp() - start)
            * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        _net.Log?.Invoke($"fin de flux media en {total:F0} ms.");
    }

    /// <summary>
    /// Reads the packet counter every half second and climbs the watchdog's
    /// ladder, one rung at a time and never two at once.
    /// </summary>
    private async Task WatchAsync(CancellationToken token)
    {
        long seen = Interlocked.Read(ref _packets);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(WatchIntervalMs));
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                if (_watchdog is not { } watchdog)
                    continue;
                double now = _watchClock.Elapsed.TotalSeconds;
                long packets = Interlocked.Read(ref _packets);
                StallAction action = packets != seen ? watchdog.OnPacket(now) : watchdog.Tick(now);
                if (action is StallAction.None) { seen = packets; continue; }
                if (StreamStalled is { } handler)
                    await handler(action);
                // The handler may have taken seconds and a whole new stream:
                // whatever arrived while it ran is not silence.
                seen = Interlocked.Read(ref _packets);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { _net.Log?.Invoke($"veille du flux arretee : {exception.Message}"); }
    }

    // --- Reception ---------------------------------------------------------------

    private void OnDatagram(ReadOnlyMemory<byte> datagram, ushort sourcePort)
    {
        Interlocked.Increment(ref _packets);
        Interlocked.Add(ref _bytes, datagram.Length);
        RtpPacket?.Invoke(datagram);

        var span = datagram.Span;
        if (global::LuminaMonitor.Core.Media.RtpPacket.LooksLikeRtcp(span))
        {
            Interlocked.Increment(ref _rtcpPackets);
            _rtcp?.OnRtcp(span, sourcePort);
            return;
        }
        var packet = global::LuminaMonitor.Core.Media.RtpPacket.Parse(span);
        if (!packet.Valid)
            return;
        long arrival = System.Diagnostics.Stopwatch.GetTimestamp();
        _rtcp?.OnRtp(packet.Sequence, packet.Timestamp, packet.Marker, arrival);
        if (packet.Marker)
            Drift(packet.Timestamp, arrival);
        if (DecodeVideo)
            _depacketizer.Add(packet, arrival);
    }

    /// <summary>
    /// Closes one picture's drift: our elapsed time minus the phone's, from the
    /// first picture of the session.
    /// </summary>
    /// <remarks>
    /// Only on the marker bit, which is the one packet per picture that means
    /// "that was all of it" — the packets before it share the same timestamp
    /// and would each report the same drift with a slightly better arrival,
    /// weighting the average by picture size for no reason. Normalised on the
    /// first picture because the two clocks have no common origin: what is
    /// being measured is the change, not the offset, and the offset is what the
    /// stopwatch on the phone's own screen is for.
    /// </remarks>
    private void Drift(uint timestamp, long arrival)
    {
        lock (_driftGate)
        {
            if (!_haveDriftOrigin)
            {
                _haveDriftOrigin = true;
                _driftOriginTimestamp = timestamp;
                _driftOriginTicks = arrival;
            }
            _driftLastTimestamp = timestamp;
            _driftLastTicks = arrival;
            double ours = (arrival - _driftOriginTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            double theirs = unchecked((int)(timestamp - _driftOriginTimestamp)) / MediaClockKhz;
            double drift = ours - theirs;
            _driftFrames++;
            _driftSum += drift;
            _driftLast = drift;
            if (_driftFrames == 1 || drift > _driftMax)
                _driftMax = drift;
        }
    }

    /// <summary>
    /// Hands the access unit to the decoder, and never quietly loses one.
    /// </summary>
    /// <remarks>
    /// An earlier version dropped the oldest waiting unit whenever the queue was
    /// full, which is the wrong loss for H.264: a dropped P picture is not one
    /// missing frame, it is every frame until the next IDR — and this stream
    /// sends exactly one IDR unless asked for another. The decoder is far faster
    /// than the stream (some hundreds of pictures a second), so the queue is
    /// normally empty and the bound below is a fault detector rather than a
    /// policy. When it does trip, the honest answer is to throw the whole
    /// backlog away at once and ask for a fresh key frame, not to hand the
    /// decoder a picture whose reference it no longer has. Frames that arrive
    /// faster than the window can show them are dropped on the other side of the
    /// decoder, where dropping one costs only that one.
    /// </remarks>
    private void Enqueue(AccessUnit unit)
    {
        lock (_queueGate)
        {
            if (_queue.Count >= QueueDepth)
            {
                Interlocked.Add(ref _lateDrops, _queue.Count);
                _queue.Clear();
                _net.FireAndForget(RequestKeyFrameAsync(), "demande d'image cle (file saturee)");
            }
            _queue.Enqueue(unit);
            long depth = _queue.Count;
            Interlocked.Exchange(ref _pendingUnits, depth);
            if (depth > Interlocked.Read(ref _maxPendingUnits))
                Interlocked.Exchange(ref _maxPendingUnits, depth);
            Monitor.Pulse(_queueGate);
        }
    }

    /// <summary>
    /// The live decoder, for the counters only: what it holds is the one delay
    /// no timestamp on this side can show, because every picture keeps the time
    /// its own packets arrived however long the decoder then sits on it.
    /// </summary>
    private volatile H264Decoder? _h264;

    private void DecodeLoop()
    {
        H264Decoder? decoder = null;
        try
        {
            decoder = new H264Decoder();
            _h264 = decoder;
            while (true)
            {
                AccessUnit unit;
                lock (_queueGate)
                {
                    while (_queue.Count == 0)
                    {
                        if (_stopping?.IsCancellationRequested ?? true)
                            return;
                        Monitor.Wait(_queueGate, 200);
                    }
                    unit = _queue.Dequeue();
                    Interlocked.Exchange(ref _pendingUnits, _queue.Count);
                }
                decoder.Decode(unit, OnFrame);
            }
        }
        catch (Exception exception)
        {
            _net.Log?.Invoke($"decodeur arrete : {exception.Message}");
        }
        finally
        {
            _h264 = null;
            decoder?.Dispose();
        }
    }

    private void OnFrame(VideoFrame frame)
    {
        Interlocked.Increment(ref _framesDecoded);
        _lastLatencyMs = frame.LatencyMs;
        _wireMs = frame.WireMs;
        _queueMs = frame.QueueMs;
        _decodeMs = frame.DecodeMs;
        if (frame.QueueMs > _maxQueueMs)
            _maxQueueMs = frame.QueueMs;
        FrameDecoded?.Invoke(frame);
    }

    // --- RTCP --------------------------------------------------------------------

    private void StartReports(Action<string> say)
    {
        uint local = (uint)Number("RemoteSSRC");        // the names are the phone's point of view
        uint remote = (uint)Number("LocalSSRC");
        ushort port = (ushort)Number("SourcePort");
        _rtcp = new RtcpSession(local, remote, port, (destination, packet) =>
            _net.SendUdpAsync(_udpPort, destination, packet));
        if (!_rtcp.Usable)
        {
            say($"Flux video accepte — RTCP impossible (SourcePort={port}, LocalSSRC={remote}, RemoteSSRC={local}).");
            return;
        }
        say($"Flux video accepte par le telephone (RTCP vers le port {port}, SSRC {remote:X8} -> {local:X8}).");
        var token = _stopping!.Token;

        // The private feedback loop, when asked for: receipts leave from the
        // receive path itself, on each picture's last packet, and only the
        // periodic report is paced from here.
        if (Tuning.Rctl)
        {
            _rtcp.EnableRctl(Tuning.RctlMaxBitrateKbps, Tuning.RctlArrival);
            say($"Boucle RCTL active : un recu par image, {Tuning.RctlReportHz:F0} rapports/s,"
                + $" borne annoncee {Tuning.RctlMaxBitrateKbps} kbit/s,"
                + $" arrivee w4 {(Tuning.RctlArrival == RctlArrivalClock.MediaClock ? "sur l'horloge media" : "sur notre montre")}.");
            _rctlLoop = Task.Run(async () =>
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000.0 / Tuning.RctlReportHz));
                try
                {
                    while (await timer.WaitForNextTickAsync(token))
                        await _rtcp.SendRctlReportAsync();
                }
                catch (OperationCanceledException) { }
                catch (Exception exception) { _net.Log?.Invoke($"boucle RCTL arretee : {exception.Message}"); }
            }, token);
        }

        _reports = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            try
            {
                while (await timer.WaitForNextTickAsync(token))
                {
                    await _rtcp.ReportAsync();
                    long wanted = _depacketizer.KeyFrameWanted;
                    if (wanted > _keyFramesWanted)
                    {
                        _keyFramesWanted = wanted;
                        await RequestKeyFrameAsync();
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception exception) { _net.Log?.Invoke($"RTCP arrete : {exception.Message}"); }
        }, token);
    }

    /// <summary>
    /// One value of the daemon's answer, found wherever it nested it, rendered
    /// for a log line; null when the answer never mentions the key.
    /// </summary>
    public string? ConfigText(string key) => Find(StreamConfig, key) switch
    {
        null => null,
        XpcUInt64 u => u.Value.ToString(),
        XpcInt64 i => i.Value.ToString(),
        bool b => b ? "true" : "false",
        double d => d.ToString("0.###"),
        { } other => other.ToString(),
    };

    /// <summary>Finds one number anywhere in the daemon's answer, whatever it wrapped it in.</summary>
    private ulong Number(string key) => Find(StreamConfig, key) switch
    {
        XpcUInt64 u => u.Value,
        XpcInt64 i => (ulong)i.Value,
        long l => (ulong)l,
        ulong u => u,
        int i => (ulong)i,
        double d => (ulong)d,
        string s when ulong.TryParse(s, out ulong parsed) => parsed,
        _ => 0,
    };

    private static object? Find(object? node, string key)
    {
        switch (node)
        {
            case IDictionary<string, object?> map:
                if (map.TryGetValue(key, out object? found))
                    return found;
                foreach (object? value in map.Values)
                    if (Find(value, key) is { } hit)
                        return hit;
                return null;
            case System.Collections.IEnumerable list and not string:
                foreach (object? item in list)
                    if (Find(item, key) is { } hit)
                        return hit;
                return null;
            default:
                return null;
        }
    }
}

