using LuminaMonitor.Core.Audio;
using LuminaMonitor.Core.Media;

namespace LuminaMonitor.Core;

/// <summary>
/// The sound rung of the ladder: the last one, the only optional one, and the
/// only one whose failure changes nothing else.
/// </summary>
/// <remarks>
/// <para><b>Order.</b> Video first, then the sound attached to it — the same
/// order Xcode's mirror uses and the one proven on 10 September 2026 at 21:12
/// (<c>audio-info 5 --video</c>: 503 packets in five seconds, no loss, both
/// streams closing cleanly). The audio stream carries the video session's own
/// <c>avcMediaStreamOptionClientSessionID</c>, which is what makes the phone treat
/// the two as one mirror rather than two unrelated captures.</para>
///
/// <para><b>Not on the critical path.</b> The mirror is declared up before this
/// runs, and this runs on a task of its own: a person waiting for a picture and a
/// finger should not wait for a sound as well. The sound follows the picture by
/// the second the phone takes to answer the second offer, and a refusal leaves
/// everything else exactly as it was.</para>
///
/// <para><b>The guard is still at the entry.</b> <c>AudioSession.StartAsync</c>
/// releases every orphaned media session before opening its own, sparing the video
/// session it is joining. That matters more here than anywhere: an audio session
/// iOS was never told to end is a system-audio capture it has not handed back, and
/// while it stands the phone's own microphone is unavailable to its other
/// apps.</para>
/// </remarks>
public sealed partial class DeviceSession
{
    /// <summary>
    /// The pause between the video stream and the audio one, in milliseconds.
    /// </summary>
    /// <remarks>
    /// None. The probe first proved the two streams could share a session with a
    /// six-second pause between them, and the application copied the pause; on
    /// 11 September 2026 <c>audio-info --video --settle=0</c> showed the phone
    /// accepting the audio offer the moment the video stream was in place (400
    /// packets in four seconds, both streams closing cleanly), with one second
    /// and with nothing at all. The display service's known refusals are between
    /// two <i>sessions</i>; a second stream joining the session it already runs is
    /// not one. Kept as a constant so that the wait, should a phone ever need
    /// one, has a name and a place.
    /// </remarks>
    private const int AudioSettleMs = 0;

    private AudioStream? _audio;
    private AudioOptions _audioOptions = AudioOptions.Default;
    private Task? _audioOpening;
    private CancellationTokenSource? _audioGate;

    /// <summary>
    /// Why the last attempt failed, kept after the stream itself has been let go.
    /// </summary>
    /// <remarks>
    /// A refused stream is taken down rather than kept, so that asking again builds
    /// a fresh one — a half-open stream answering "no" to every retry would make the
    /// panel's button a button that does nothing. But the reason has to outlive it,
    /// or the panel would have nothing to show and no button to show it with.
    /// </remarks>
    private string? _audioRefusal;

    /// <summary>
    /// Whether to ask for sound, on which output, how loud and how far behind.
    /// </summary>
    /// <remarks>
    /// Assigning applies at once to a stream already running — volume, mute, target
    /// delay and endpoint all four — but does not open or close the stream:
    /// <see cref="OpenAudioAsync"/> and <see cref="CloseAudioAsync"/> do that, so
    /// that a window can move a slider without the phone being asked anything.
    /// </remarks>
    public AudioOptions AudioOptions
    {
        get => _audioOptions;
        set
        {
            _audioOptions = value;
            _audio?.Apply(value);
        }
    }

    /// <summary>What the sound path has counted, or null while there is no audio stream.</summary>
    public AudioStats? AudioStatistics => _audio?.Stats;

    /// <summary>Whether the phone is sending sound right now.</summary>
    public bool AudioStreaming => _audio?.Streaming ?? false;

    /// <summary>
    /// Why there is no sound: the daemon's refusal, the output's, or null when
    /// nobody asked for any.
    /// </summary>
    public string? AudioFailure => _audio?.Failure ?? _audioRefusal;

    /// <summary>True while the audio stream is being opened, so a panel can say so.</summary>
    public bool AudioOpening => _audioOpening is { IsCompleted: false };

    /// <summary>
    /// Opens the audio stream now, on the video session that is already up.
    /// </summary>
    /// <remarks>
    /// What the panel's switch calls when somebody turns the sound on mid-session,
    /// and what its retry calls after a refusal. Returns false, having said why in
    /// the log, when there is no mirror to attach to or when the phone says no —
    /// never by throwing, because the caller is a click handler and the mirror is
    /// not at stake.
    /// </remarks>
    public async Task<bool> OpenAudioAsync()
    {
        if (_audio is not null)
            return _audio.Streaming;
        if (_net is null || _rsd is null || _media is not { Streaming: true } media)
        {
            Info("Son demande sans miroir ouvert : rien a rattacher, l'audio attendra la session.");
            return false;
        }

        var stream = new AudioStream(_net, _rsd, media.SessionId, _audioOptions, _log,
            () => VideoDelayMs(media));
        _audio = stream;
        _audioRefusal = null;
        try
        {
            await stream.StartAsync(new LogProgress(_log));
        }
        catch (Exception exception)
        {
            // A display service that has stopped answering is not an audio fault,
            // but it is not this method's ladder to climb either: the video
            // watchdog owns that, and it is still watching.
            Warn($"Flux audio impossible a ouvrir : {exception.Message}");
            _audio = null;
            _audioRefusal = exception.Message;
            try { await stream.StopAsync(); } catch (Exception) { }
            return false;
        }

        if (!stream.Streaming)
        {
            string reason = (stream.Failure ?? "(aucun motif rapporte)").Replace("\r", "").Replace("\n", " ");
            Warn($"Flux audio refuse par le telephone : {(reason.Length > 200 ? reason[..200] + "…" : reason)}");
            // Let go of it, so that asking again is a real second attempt and not
            // this same refusal read back.
            _audio = null;
            _audioRefusal = reason;
            try { await stream.StopAsync(); } catch (Exception) { }
            return false;
        }
        Info($"Son du telephone en place, rattache a la session video"
            + $" {Convert.ToHexString(media.SessionId.Bytes)} — retard cible {_audioOptions.ClampedDelayMs} ms.");
        return true;
    }

    /// <summary>
    /// Stops the audio stream and its output, leaving the picture alone.
    /// </summary>
    /// <remarks>
    /// An opening still under way is cancelled rather than waited out. It is
    /// awaited all the same, and that is the point: a cable pulled while the audio
    /// offer is in flight must not leave a task that opens an audio stream on a
    /// tunnel nobody owns any more — which on the phone's side is a capture
    /// session with nothing left to close it.
    /// </remarks>
    public async Task CloseAudioAsync()
    {
        _audioGate?.Cancel();
        var opening = _audioOpening;
        _audioOpening = null;
        if (opening is not null)
        {
            try { await opening; }
            catch (Exception) { /* it reports its own failures */ }
        }
        _audioGate?.Dispose();
        _audioGate = null;
        _audioRefusal = null;
        var stream = _audio;
        _audio = null;
        if (stream is null)
            return;
        try { await stream.StopAsync(); }
        catch (Exception exception) { Warn($"Arret du son incomplet : {exception.Message}"); }
        Info("Son du telephone arrete.");
    }

    /// <summary>
    /// Turns the sound on or off from the window, mid-session.
    /// </summary>
    /// <remarks>
    /// Off means off at the source: the stream is stopped, so the phone stops
    /// capturing its own output and nothing is decoded. Muting would have left both
    /// running for no purpose.
    /// </remarks>
    public async Task SetAudioEnabledAsync(bool enabled)
    {
        AudioOptions = _audioOptions with { Enabled = enabled };
        if (enabled)
            await OpenAudioAsync();
        else
            await CloseAudioAsync();
    }

    /// <summary>
    /// Starts the audio stream behind the mirror, as soon as the mirror is up.
    /// </summary>
    /// <remarks>
    /// Fire and forget on purpose, and checked on the way out: the session may
    /// have been released or rebuilt while the offer was in flight, and opening an
    /// audio stream on a tunnel that no longer exists would leave a capture
    /// session on the phone with nobody to close it.
    /// </remarks>
    private void OpenAudioAfterVideo(MediaSession media)
    {
        if (!_audioOptions.Enabled || _net is null)
            return;
        Info(AudioSettleMs == 0
            ? "Son du telephone : ouverture aussitot l'image en place."
            : $"Son du telephone : ouverture dans {AudioSettleMs} ms.");
        var gate = new CancellationTokenSource();
        _audioGate = gate;
        _audioOpening = Task.Run(async () =>
        {
            try { await Task.Delay(AudioSettleMs, gate.Token); }
            catch (OperationCanceledException) { return; }
            if (gate.IsCancellationRequested || !ReferenceEquals(media, _media) || State != SessionState.MediaUp)
                return;
            await OpenAudioAsync();
        });
        _net.FireAndForget(_audioOpening, "ouverture du flux audio");
    }

    /// <summary>
    /// How long the picture being shown took to get here, in milliseconds, as far
    /// as Core can see it.
    /// </summary>
    /// <remarks>
    /// The sender report's end-to-end delay, plus the two waits this side counts:
    /// the queue in front of the decoder and the decoder itself. What is <i>not</i>
    /// in it is the last stage — the window presenting the picture — because that
    /// belongs to the application and not here. So the skew this feeds is the skew
    /// up to the decoder's output, and the picture's real delay is that much
    /// larger; docs/AUDIO.md says so where the figure is explained.
    /// </remarks>
    private static double VideoDelayMs(MediaSession media)
    {
        var stats = media.Stats;
        return media.PipelineMs + stats.QueueMs + stats.DecodeMs;
    }
}
