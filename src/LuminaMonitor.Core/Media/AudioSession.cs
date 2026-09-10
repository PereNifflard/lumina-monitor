using System.Security.Cryptography;
using LuminaMonitor.Core.RemoteXpc;
using LuminaMonitor.Core.Tunnel;
using XpcService = LuminaMonitor.Core.RemoteXpc.RemoteXpc;

namespace LuminaMonitor.Core.Media;

/// <summary>
/// The phone's system audio, over RTP, on its own UDP port.
/// </summary>
/// <remarks>
/// Everything <see cref="MediaSession"/> does for the picture, done for the
/// sound and nothing else: no depacketizer, no decoder, no watchdog — the
/// packets are counted and handed to whoever asked, because until Windows can
/// decode what arrives there is nothing else to do with them. The same two
/// rules of order still hold, and for the same reasons: the UDP port is
/// claimed and listening <b>before</b> <c>startmediastream</c> is sent, and a
/// receiver report goes out every second or the phone reaps the session after
/// twenty (<c>RTCPTimeoutInterval</c>).
///
/// <para>Unlike the video's, the audio receiver report cannot wait for the
/// first packet: the phone's screen is usually silent, so nothing arrives
/// until someone plays something, and a session that has not been reported on
/// is gone by then. So the reports start as soon as the answer names the SSRCs,
/// with a highest-sequence of zero, which is a truthful report of having heard
/// nothing.</para>
///
/// <para>The stream is opened on a display-service channel of its own. That is
/// not a preference: the channel a media stream was started on is the one the
/// daemon hangs up when the session ends, and sharing it with the video would
/// take the picture down with the sound.</para>
/// </remarks>
internal sealed class AudioSession
{
    /// <summary>How long the phone is given to hang up on the service before we do.</summary>
    private static readonly TimeSpan HangUpPatience = TimeSpan.FromSeconds(1);

    private readonly TunnelNet _net;
    private readonly Rsd _rsd;
    private XpcService? _display;
    private XpcUuid _sessionId;
    private ushort _udpPort;
    private RtcpSession? _rtcp;
    private CancellationTokenSource? _stopping;
    private Task? _reports;
    private long _packets, _bytes, _rtcpPackets, _mediaPackets;
    private uint _firstTimestamp, _lastTimestamp;
    private bool _haveFirstTimestamp;

    public AudioSession(TunnelNet net, Rsd rsd)
    {
        _net = net;
        _rsd = rsd;
    }

    /// <summary>Every datagram the phone sends to our audio port, control packets included.</summary>
    public event Action<ReadOnlyMemory<byte>>? RtpPacket;

    /// <summary>What the offer asks for; <see cref="AudioOfferOptions.Default"/> is Apple's own.</summary>
    public AudioOfferOptions Offer { get; set; } = AudioOfferOptions.Default;

    /// <summary>
    /// The session id to start under: null takes a fresh one, anything else
    /// groups this stream with the video stream that already uses it.
    /// </summary>
    public XpcUuid? PairedSessionId { get; set; }

    /// <summary>Always <c>"output"</c> in practice; the probe sets it to ask the phone about <c>"input"</c>.</summary>
    public string Direction { get; set; } = "output";

    /// <summary>Whether the phone accepted the stream.</summary>
    public bool Streaming { get; private set; }

    /// <summary>The daemon's refusal, whole and untouched, when the offer was turned down.</summary>
    public string? Failure { get; private set; }

    /// <summary>The daemon's answer to <c>startmediastream</c>, untouched.</summary>
    public Dictionary<string, object?>? StreamConfig { get; private set; }

    /// <summary>The whole answer, of which <see cref="StreamConfig"/> is one branch.</summary>
    public Dictionary<string, object?>? Answer { get; private set; }

    /// <summary>The UDP port the phone was told to send to.</summary>
    public ushort ReceiverPort => _udpPort;

    /// <summary>What arrived: datagrams, bytes, and how many of them were RTCP.</summary>
    public (long Packets, long Bytes, long Rtcp) Counts =>
        (Interlocked.Read(ref _packets), Interlocked.Read(ref _bytes), Interlocked.Read(ref _rtcpPackets));

    /// <summary>Receiver reports sent, and sender reports heard.</summary>
    public (long Sent, long Heard) Reports => (_rtcp?.ReportsSent ?? 0, _rtcp?.SenderReports ?? 0);

    /// <summary>
    /// How far the RTP timestamp advances from one packet to the next, on
    /// average; zero before the second packet.
    /// </summary>
    /// <remarks>
    /// The one measurement that settles the frame length, and it settles it
    /// against the AudioSpecificConfig rather than agreeing with it: at 48 kHz
    /// one unit is one sample, so 480 is a ten-millisecond frame and 512 is
    /// 10.67 ms. The config's own <c>frameLengthFlag</c> reads 0, which the
    /// standard maps to 512; the wire says otherwise, and the wire wins.
    /// </remarks>
    public double MeanTimestampStep
    {
        get
        {
            long packets = Interlocked.Read(ref _mediaPackets);
            return packets > 1 ? unchecked((int)(_lastTimestamp - _firstTimestamp)) / (double)(packets - 1) : 0;
        }
    }

    /// <summary>
    /// Claims the port, opens the service, and asks the phone to start sending.
    /// </summary>
    /// <remarks>
    /// A refusal is not an exception here: which offers the phone turns down is
    /// exactly what the probe is measuring, so the daemon's answer is kept in
    /// <see cref="Failure"/> and the caller reads <see cref="Streaming"/>.
    /// </remarks>
    public async Task StartAsync(IProgress<string>? progress = null, Action<string>? serviceTrace = null)
    {
        void Say(string line) => progress?.Report(line);

        // The same guard the video session runs, and it matters more here: an
        // audio session the phone was never told to end is a system-audio
        // capture session iOS has not handed back, and while it stands the
        // person's own microphone is unavailable to their other apps.
        await MediaHygiene.ReleaseOrphansAsync(_rsd, Say, PairedSessionId);

        _display = await _rsd.OpenAsync(DisplayService.ServiceName, serviceTrace);
        _stopping = new CancellationTokenSource();
        _udpPort = _net.ListenUdp(OnDatagram);
        _sessionId = PairedSessionId ?? new XpcUuid(RandomNumberGenerator.GetBytes(16));
        byte[] offer = AudioOffer.Build(MediaOffer.NewCallId(),
            (uint)RandomNumberGenerator.GetInt32(int.MaxValue), options: Offer);
        Say($"Ouverture du flux audio vers [{_net.Local}]:{_udpPort}"
            + $" (offre : {Offer}, direction « {Direction} »,"
            + $" session {(PairedSessionId is null ? "propre" : "partagee avec la video")})…");
        try
        {
            Answer = await DisplayService.StartAudioStreamAsync(_display, _net.Local.ToString(), _udpPort,
                _net.Peer.ToString(), _sessionId, offer, direction: Direction);
            StreamConfig = Find(Answer, "streamConfig") as Dictionary<string, object?>;
            Streaming = true;
            StartReports(Say);
        }
        catch (InvalidOperationException exception)
        {
            Failure = exception.Message;
        }
    }

    /// <summary>Stops the stream the way the daemon expects: reports, BYE, stop, then the channel.</summary>
    public async Task StopAsync()
    {
        _stopping?.Cancel();
        if (_reports is not null) { try { await _reports; } catch (Exception) { } _reports = null; }

        var display = _display;
        _display = null;
        if (_rtcp is { Usable: true })
        {
            try { await _rtcp.SendByeAsync(); }
            catch (Exception exception) { _net.Log?.Invoke($"RTCP BYE audio non envoye : {exception.Message}"); }
        }
        if (display is not null && Streaming)
            await DisplayService.StopMediaStreamAsync(display, _sessionId);
        Streaming = false;
        _rtcp = null;
        if (_udpPort != 0) { _net.StopUdp(_udpPort); _udpPort = 0; }
        if (display is not null)
        {
            var (byThePhone, milliseconds) = await display.CloseAsync(HangUpPatience);
            _net.Log?.Invoke($"service d'affichage (audio) ferme en {milliseconds:F0} ms "
                + (byThePhone ? "par le telephone." : "de notre cote (le telephone n'a pas raccroche)."));
        }
    }

    /// <summary>One value of the daemon's answer, found wherever it nested it, rendered for a log line.</summary>
    public string? ConfigText(string key) => Find(StreamConfig ?? Answer, key) switch
    {
        null => null,
        XpcUInt64 u => u.Value.ToString(),
        XpcInt64 i => i.Value.ToString(),
        bool b => b ? "true" : "false",
        double d => d.ToString("0.###"),
        { } other => other.ToString(),
    };

    private void OnDatagram(ReadOnlyMemory<byte> datagram, ushort sourcePort)
    {
        Interlocked.Increment(ref _packets);
        Interlocked.Add(ref _bytes, datagram.Length);
        RtpPacket?.Invoke(datagram);

        var span = datagram.Span;
        if (Media.RtpPacket.LooksLikeRtcp(span))
        {
            Interlocked.Increment(ref _rtcpPackets);
            _rtcp?.OnRtcp(span, sourcePort);
            return;
        }
        var packet = Media.RtpPacket.Parse(span);
        if (!packet.Valid)
            return;
        if (!_haveFirstTimestamp) { _firstTimestamp = packet.Timestamp; _haveFirstTimestamp = true; }
        _lastTimestamp = packet.Timestamp;
        Interlocked.Increment(ref _mediaPackets);
        _rtcp?.OnRtp(packet.Sequence, packet.Timestamp, packet.Marker, System.Diagnostics.Stopwatch.GetTimestamp());
    }

    private void StartReports(Action<string> say)
    {
        uint local = (uint)Number("RemoteSSRC");        // the names are the phone's point of view
        uint remote = (uint)Number("LocalSSRC");
        ushort port = (ushort)Number("SourcePort");
        _rtcp = new RtcpSession(local, remote, port, (destination, packet) =>
            _net.SendUdpAsync(_udpPort, destination, packet), RtcpSession.AudioJitterClock);
        if (!_rtcp.Usable)
        {
            say($"Flux audio accepte — RTCP impossible (SourcePort={port}, LocalSSRC={remote}, RemoteSSRC={local}).");
            return;
        }
        say($"Flux audio accepte par le telephone (RTCP vers le port {port}, SSRC {remote:X8} -> {local:X8}).");
        var token = _stopping!.Token;
        _reports = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            try
            {
                while (await timer.WaitForNextTickAsync(token))
                    await _rtcp.ReportAsync();
            }
            catch (OperationCanceledException) { }
            catch (Exception exception) { _net.Log?.Invoke($"RTCP audio arrete : {exception.Message}"); }
        }, token);
    }

    private ulong Number(string key) => Find(StreamConfig ?? Answer, key) switch
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
