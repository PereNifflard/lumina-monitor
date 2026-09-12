using LuminaMonitor.Core.Audio;
using LuminaMonitor.Core.RemoteXpc;
using LuminaMonitor.Core.Tunnel;

namespace LuminaMonitor.Core.Media;

/// <summary>
/// The phone's sound, from the negotiation to the speaker: an
/// <see cref="AudioSession"/> for the stream and an <see cref="AudioRenderer"/>
/// for everything that happens to what arrives on it.
/// </summary>
/// <remarks>
/// <para><b>Attached to the picture.</b> The stream is opened under the video
/// stream's own client session identifier, which is how Xcode's mirror groups
/// its two — proven on 10 September 2026 at 21:12 with
/// <c>audio-info 5 --video</c>: video first, audio joining it, 503 packets in
/// five seconds, no loss, both closing cleanly. So this object is only ever built
/// once a video session exists, and it is given that session's identifier.</para>
///
/// <para><b>Never fatal.</b> A refused audio offer leaves the mirror exactly as
/// it was. The sound is the one thing in this ladder that a person can do without,
/// and taking the picture down because the phone would not talk about audio would
/// be the worst possible trade. <see cref="Failure"/> says why, the panel shows
/// it, and asking again costs one call.</para>
///
/// <para><b>The synchronisation diagnostic.</b> Both streams publish RTCP sender
/// reports carrying the phone's own NTP clock, which is the only clock the two
/// have in common. Comparing what each one says about the media being played
/// right now gives the gap between the sound and the picture, in milliseconds,
/// measured rather than guessed — written to the journal every five seconds and
/// used for nothing else. This version holds the sound back by a fixed delay a
/// person sets by ear; the measurement is what a later one would need to set it
/// itself.</para>
/// </remarks>
internal sealed class AudioStream
{
    /// <summary>How often the skew between the two streams reaches the journal.</summary>
    private static readonly TimeSpan SkewInterval = TimeSpan.FromSeconds(5);

    private readonly ILog? _log;
    private readonly Func<double> _videoDelayMs;
    private readonly AudioSession _session;
    private readonly AudioRenderer _renderer;
    private CancellationTokenSource? _stopping;
    private Task? _skewWatch;
    private double _skewMs = double.NaN;

    /// <param name="videoDelayMs">
    /// How long the picture being shown took to get here, in milliseconds, from
    /// the video session's own counters. Handed in rather than read, because the
    /// last stage — the decoder's queue and the window itself — belongs to the
    /// caller and not to Core.
    /// </param>
    public AudioStream(TunnelNet net, Rsd rsd, XpcUuid pairedSessionId, AudioOptions options, ILog? log,
        Func<double> videoDelayMs)
    {
        _log = log;
        _videoDelayMs = videoDelayMs;
        _session = new AudioSession(net, rsd) { PairedSessionId = pairedSessionId };
        _renderer = new AudioRenderer(options, line => _log?.Info("audio : " + line));
    }

    /// <summary>Whether the phone is sending sound.</summary>
    public bool Streaming => _session.Streaming;

    /// <summary>The session this stream runs under, for a video stream to spare.</summary>
    public XpcUuid SessionId => _session.SessionId;

    /// <summary>Why there is no sound: the daemon's refusal, or the output's.</summary>
    public string? Failure => _session.Failure ?? _renderer.Stats.Failure;

    /// <summary>Everything the sound path counted, the session's own part included.</summary>
    public AudioStats Stats => _renderer.Stats with
    {
        Streaming = _session.Streaming,
        Failure = Failure,
        SkewMs = Volatile.Read(ref _skewMs),
    };

    /// <summary>Volume, mute, target delay and endpoint, applied to a chain already running.</summary>
    public void Apply(AudioOptions options) => _renderer.Apply(options);

    /// <summary>
    /// Opens the stream, and reports a refusal instead of throwing it.
    /// </summary>
    /// <remarks>
    /// The one exception that does come out is a display service that has stopped
    /// answering, because that is not an audio fault: it is the whole media path,
    /// and the session above has a ladder for it.
    /// </remarks>
    public async Task StartAsync(IProgress<string>? progress)
    {
        _session.RtpPacket += OnDatagram;
        await _session.StartAsync(progress);
        if (!_session.Streaming)
            return;
        _stopping = new CancellationTokenSource();
        var token = _stopping.Token;
        _skewWatch = Task.Run(() => WatchSkewAsync(token), token);
    }

    /// <summary>Stops the stream the way the daemon expects, then the chain behind it.</summary>
    /// <remarks>
    /// The order is the stream first: the phone is told to stop sending before the
    /// output is closed, so no frame is decoded into a queue nobody will drain.
    ///
    /// <para>Told by receiver reports and BYE, never by <c>stopmediastream</c>:
    /// that call names a session, this stream shares the video's, and on
    /// 11 September 2026 the sound switched off in the window took the picture
    /// down a second later. Measured the other way the same day
    /// (<c>audio-info --video --stop=bye --hold=25</c>): the picture keeps its
    /// 117 packets a second, the phone goes on sending audio for the twenty
    /// seconds of its RTCP timeout and then ends that stream alone, and the
    /// status afterwards lists the video and nothing else. Twenty seconds of a
    /// capture nobody decodes is the price of a picture that stays.</para>
    /// </remarks>
    public async Task StopAsync()
    {
        _stopping?.Cancel();
        if (_skewWatch is not null)
        {
            try { await _skewWatch; }
            catch (Exception) { /* it only ever waits on a timer */ }
            _skewWatch = null;
        }
        _session.RtpPacket -= OnDatagram;
        try { await _session.StopAsync(tellDaemon: false); }
        catch (Exception exception) { _log?.Warn($"arret du flux audio incomplet : {exception.Message}"); }
        _renderer.Dispose();
        _stopping?.Dispose();
        _stopping = null;
    }

    /// <summary>
    /// Every datagram of the audio port, straight into the chain.
    /// </summary>
    /// <remarks>
    /// Called on the tunnel's receive path, so it does exactly two things and
    /// neither of them waits: the RTCP is already handled by the session before
    /// this runs, and the decode is 0.14 ms against a 10 ms frame.
    /// </remarks>
    private void OnDatagram(ReadOnlyMemory<byte> datagram) => _renderer.Accept(datagram.Span);

    /// <summary>
    /// Writes the gap between the sound and the picture every five seconds.
    /// </summary>
    /// <remarks>
    /// Each side's figure is the delay between the phone stamping the media and
    /// this side playing it: the sender report's own end-to-end delay, plus what
    /// is still waiting here. For the sound that is the jitter buffer and the
    /// endpoint's own latency; for the picture it is whatever the caller counts.
    /// The difference is what a listener hears as lip sync, and its sign is the
    /// useful part — positive means the sound is behind the picture, so the delay
    /// setting should come down.
    /// </remarks>
    private async Task WatchSkewAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(SkewInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                var stats = _renderer.Stats;
                double audio = _session.PipelineMs + stats.QueuedMs + stats.OutputLatencyMs;
                double video = _videoDelayMs();
                if (double.IsNaN(audio) || double.IsNaN(video))
                    continue;
                double skew = audio - video;
                Volatile.Write(ref _skewMs, skew);
                _log?.Info($"audio : desynchro son-image {skew:+0;-0;0} ms"
                    + $" (son {audio:F0} ms = fil {_session.PipelineMs:F0} + file {stats.QueuedMs:F0}"
                    + $" + sortie {stats.OutputLatencyMs:F1} ; image {video:F0} ms)"
                    + $" — cible de retard {stats.TargetMs:F0} ms, aucun rattrapage automatique.");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { _log?.Warn($"diagnostic de synchro arrete : {exception.Message}"); }
    }
}
