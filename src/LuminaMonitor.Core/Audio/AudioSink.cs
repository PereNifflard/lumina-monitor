using System.Diagnostics;

namespace LuminaMonitor.Core.Audio;

/// <summary>
/// Where the decoded frames end up: a Windows endpoint, or nothing at all.
/// </summary>
/// <remarks>
/// The abstraction is worth one interface and not a line more, and it exists for
/// one reason: every measurement of this chain has to be possible without a
/// sound coming out of a speaker. A continuous-integration runner has no
/// endpoint, and a test run on a machine somebody is working at must not make a
/// noise on it. So <see cref="DryAudioSink"/> is the same consumer, paced by the
/// same clock, reading the same jitter buffer — and dropping the samples at the
/// end instead of handing them to a driver.
/// </remarks>
internal interface IAudioSink : IDisposable
{
    /// <summary>The output in use, as Windows names it.</summary>
    string Device { get; }

    /// <summary>The format it is being fed in.</summary>
    string Format { get; }

    /// <summary>Why there is no sound, when there is none.</summary>
    string? Failure { get; }

    /// <summary>What the endpoint says it holds, in milliseconds.</summary>
    double LatencyMs { get; }

    /// <summary>The factor applied to the samples on their way out; read on the sink's own thread.</summary>
    float Gain { get; set; }

    /// <summary>Opens the output and starts pulling; returns once it is open or has failed.</summary>
    void Start();

    /// <summary>Plays on another endpoint from now on; null means Windows's default output.</summary>
    void Use(string? deviceId);
}

/// <summary>
/// A consumer with no endpoint behind it: the same pull, on the same beat, into
/// nothing.
/// </summary>
/// <remarks>
/// Paced by a stopwatch rather than by a driver's event, which is the one
/// difference from <see cref="WasapiOutput"/>, and it is paced properly: a pull
/// every ten milliseconds on a deadline that does not drift, sleeping down to the
/// last millisecond and spinning the rest, exactly as the video replay does. A
/// loop that pulled as fast as it could would drain the queue in a few
/// microseconds and report a buffer that never fills, which is the opposite of
/// the measurement wanted.
///
/// <para>It also keeps the queue's fill at every pull — mean, lowest, highest —
/// because that is what says whether a target delay is actually held, and it is
/// invisible in an average taken once a second.</para>
/// </remarks>
internal sealed class DryAudioSink : IAudioSink
{
    private readonly AudioJitterBuffer _buffer;
    private readonly float[] _scratch;
    private readonly int _framesPerPull;
    private readonly Thread _thread;
    private readonly object _gate = new();
    private volatile bool _stopping;
    private long _pulls;
    private double _fillSum, _fillMin = double.MaxValue, _fillMax;

    public DryAudioSink(AudioJitterBuffer buffer, int framesPerPull)
    {
        _buffer = buffer;
        _framesPerPull = framesPerPull;
        _scratch = new float[framesPerPull * AudioFormat.Source.Channels];
        _thread = new Thread(Run) { IsBackground = true, Name = "LuminaAudioDry" };
    }

    public string Device => "(sortie a sec, aucun peripherique ouvert)";

    public string Format => AudioFormat.Source.ToString();

    public string? Failure => null;

    public double LatencyMs => _framesPerPull * 1000.0 / AudioFormat.Source.SampleRate;

    public float Gain { get; set; } = 1f;

    /// <summary>How many times the sink pulled, and what the queue held each time.</summary>
    public (long Pulls, double MeanMs, double MinMs, double MaxMs) Fill
    {
        get
        {
            lock (_gate)
                return (_pulls, _pulls == 0 ? 0 : _fillSum / _pulls, _pulls == 0 ? 0 : _fillMin, _fillMax);
        }
    }

    public void Start() => _thread.Start();

    public void Use(string? deviceId)
    {
        // Nothing to switch to: there was never an endpoint. Accepted rather
        // than refused so that a dry run exercises the same calls as a real one.
    }

    /// <summary>Stops pulling; safe to call twice, which a chain shut down twice does.</summary>
    public void Dispose()
    {
        _stopping = true;
        if (_thread.IsAlive)
            _thread.Join(TimeSpan.FromSeconds(2));
    }

    private void Run()
    {
        // The platform timer is held at one millisecond for the run, exactly as
        // the video replay holds it: a ten-millisecond beat kept with the default
        // fifteen-millisecond timer would arrive in lumps, and the measurement
        // would be of the lumps rather than of the buffer.
        using var fineTimer = Hid.TimerResolution.Hold();
        long period = Stopwatch.Frequency * _framesPerPull / AudioFormat.Source.SampleRate;
        long due = Stopwatch.GetTimestamp() + period;
        while (!_stopping)
        {
            WaitUntil(due);
            if (_stopping)
                return;
            due += period;

            double queued = _buffer.QueuedMs;
            _buffer.Fill(_scratch, Gain);

            // The pulls before the queue has reached its target are not measured:
            // they are the priming, they are supposed to find it short, and
            // counting them would put a zero in every minimum ever reported.
            if (_buffer.Played == 0)
                continue;
            lock (_gate)
            {
                _pulls++;
                _fillSum += queued;
                if (queued < _fillMin) _fillMin = queued;
                if (queued > _fillMax) _fillMax = queued;
            }
        }
    }

    /// <summary>
    /// Waits for a due time without burning a processor doing it.
    /// </summary>
    /// <remarks>
    /// Asleep the whole way, down to a millisecond, and never spinning: the
    /// deadline is absolute — the caller advances it by a fixed period — so
    /// overshooting one pull by a millisecond costs a millisecond on that pull and
    /// nothing afterwards. A spin-wait would hold the last millisecond of every
    /// ten at a hundred per cent of a core, which on a measurement run is a tenth
    /// of the machine spent proving the queue is full.
    /// </remarks>
    private void WaitUntil(long dueTicks)
    {
        while (!_stopping)
        {
            long left = dueTicks - Stopwatch.GetTimestamp();
            if (left <= 0)
                return;
            double milliseconds = left * 1000.0 / Stopwatch.Frequency;
            Thread.Sleep(milliseconds > 1.5 ? (int)(milliseconds - 0.5) : 1);
        }
    }
}
