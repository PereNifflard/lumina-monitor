namespace LuminaMonitor.Core.Media;

/// <summary>What a stream that went quiet is worth doing about.</summary>
internal enum StallAction
{
    /// <summary>Nothing to do; the stream is either healthy or already being seen to.</summary>
    None,
    /// <summary>Ask the encoder for a fresh picture and give it a moment.</summary>
    RequestKeyFrame,
    /// <summary>The picture never came back: stop the stream properly and open a new one.</summary>
    RestartStream,
    /// <summary>Restarting the stream does not help either: take the developer image down and up.</summary>
    SoftReset,
    /// <summary>Pictures are arriving again after a stall; said once, so it can be counted.</summary>
    Recovered,
}

/// <summary>
/// Watches the pace of the video stream and says what to do when it stops.
/// </summary>
/// <remarks>
/// The ladder is deliberately gentle at the bottom and brutal at the top,
/// because the three failures seen on the phone need three different answers.
/// A stream that lost its reference picture needs a Picture Loss Indication and
/// nothing else. A media session the daemon abandoned needs a new session, and
/// nothing below that will do. A display service that stopped answering
/// altogether needs its daemon restarted, which only unmounting the developer
/// image does. So: three seconds of silence buys a key frame request, two more
/// seconds of silence buys a fresh stream, and two consecutive failures to open
/// one buys a soft reset.
///
/// <para>There is no clock in here on purpose. Every entry point takes the
/// current time in seconds, so the whole ladder can be walked in a test with
/// invented instants — see the probe's <c>watchdog-selftest</c>, which is the
/// only way any of this gets exercised without a phone.</para>
/// </remarks>
internal sealed class StreamWatchdog
{
    /// <summary>Silence past which the stream is considered stalled.</summary>
    public const double StallSeconds = 3.0;

    /// <summary>How long a key frame request is given before the stream is restarted.</summary>
    public const double KeyFrameGraceSeconds = 2.0;

    /// <summary>Failed restarts in a row before the developer image is taken down.</summary>
    public const int RestartFailuresBeforeReset = 2;

    private enum Phase
    {
        /// <summary>Packets are arriving, or the silence is still short.</summary>
        Healthy,
        /// <summary>A key frame has been asked for and the grace period is running.</summary>
        KeyFrameAsked,
        /// <summary>A restart was ordered; the outcome is owed through <see cref="OnRestart"/>.</summary>
        Restarting,
    }

    private readonly object _gate = new();
    private Phase _phase = Phase.Healthy;
    private double _lastPacket;
    private double _asked;
    private int _failures;
    private bool _stalled;

    public StreamWatchdog(double now) => _lastPacket = now;

    /// <summary>How many times the ladder has been climbed to a restart since the last recovery.</summary>
    public int RestartFailures { get { lock (_gate) return _failures; } }

    /// <summary>A packet arrived. Clears the alarm, and says so once when it was up.</summary>
    public StallAction OnPacket(double now)
    {
        lock (_gate)
        {
            _lastPacket = now;
            if (_phase == Phase.Restarting)
                return StallAction.None;            // the outcome call settles it
            _phase = Phase.Healthy;
            if (!_stalled)
                return StallAction.None;
            _stalled = false;
            _failures = 0;
            return StallAction.Recovered;
        }
    }

    /// <summary>One beat of the clock with no packet since the last one.</summary>
    public StallAction Tick(double now)
    {
        lock (_gate)
        {
            switch (_phase)
            {
                case Phase.Healthy:
                    if (now - _lastPacket < StallSeconds)
                        return StallAction.None;
                    _stalled = true;
                    _phase = Phase.KeyFrameAsked;
                    _asked = now;
                    return StallAction.RequestKeyFrame;

                case Phase.KeyFrameAsked:
                    if (now - _asked < KeyFrameGraceSeconds)
                        return StallAction.None;
                    _phase = Phase.Restarting;
                    return StallAction.RestartStream;

                default:
                    return StallAction.None;        // a restart is in flight
            }
        }
    }

    /// <summary>
    /// How the restart went. A success only rearms the watch — the stream is
    /// declared recovered by the first packet, not by the daemon's answer.
    /// </summary>
    public StallAction OnRestart(bool succeeded, double now)
    {
        lock (_gate)
        {
            _phase = Phase.Healthy;
            _lastPacket = now;
            if (succeeded)
                return StallAction.None;
            if (++_failures < RestartFailuresBeforeReset)
                return StallAction.None;            // the next silence climbs the ladder again
            _failures = 0;
            return StallAction.SoftReset;
        }
    }

    /// <summary>Starts the watch over, after a reset or a fresh stream.</summary>
    public void Rearm(double now)
    {
        lock (_gate)
        {
            _phase = Phase.Healthy;
            _lastPacket = now;
            _failures = 0;
            _stalled = false;
        }
    }
}
