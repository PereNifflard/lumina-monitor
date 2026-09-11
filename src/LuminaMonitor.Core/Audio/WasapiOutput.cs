using System.Runtime.InteropServices;
using LuminaMonitor.Core.Audio.CoreAudio;

namespace LuminaMonitor.Core.Audio;

/// <summary>
/// The sound leaving this PC: one WASAPI render stream, shared mode, driven by
/// the endpoint's own event.
/// </summary>
/// <remarks>
/// <para><b>Event-driven, not timed.</b> The endpoint signals an event every
/// period and the thread fills whatever room the buffer has; nothing here has a
/// clock of its own. A loop paced by a timer instead would drift against the
/// sound card within minutes, and the drift would come out as a click every few
/// seconds — indistinguishable, to a listener, from a decoder fault.</para>
///
/// <para><b>The format.</b> 48 kHz, stereo, 32-bit float — exactly what the
/// decoder produces — with <c>AUTOCONVERTPCM</c> and
/// <c>SRC_DEFAULT_QUALITY</c>, so the audio engine does the resampling and the
/// remixing to whatever the endpoint runs at. That is the whole reason this
/// project ships no resampler. A driver that refuses those flags is answered by
/// the engine's own mix format, which is accepted only when this side can lay
/// samples out in it — see <see cref="AudioFormat"/>; a mix format at another
/// sample rate is refused out loud rather than resampled badly.</para>
///
/// <para><b>An endpoint that goes.</b> Headphones unplugged, a virtual cable
/// stopped: the calls answer <c>AUDCLNT_E_DEVICE_INVALIDATED</c> and the thread
/// opens the default output instead, saying so in the journal. A chosen endpoint
/// is only ever given up for the default one, never silently for a third.</para>
///
/// <para><b>Volume.</b> <see cref="Gain"/> is applied to the samples as they are
/// pulled out of the jitter buffer. Nothing here touches the system mixer: the
/// endpoint is shared with everything else on the machine, and turning its volume
/// down for the phone would turn it down for all of it.</para>
/// </remarks>
internal sealed class WasapiOutput : IAudioSink
{
    /// <summary>The endpoint buffer asked for, in 100 ns units: 60 ms, room for three periods.</summary>
    private const long BufferHns = 600_000;

    /// <summary>How long to wait for the endpoint's event before looking around anyway.</summary>
    private const int EventPatienceMs = 200;

    /// <summary>How long to wait before opening the output again after a failure.</summary>
    private const int RetryMs = 1000;

    /// <summary>Failures in a row on the default output before the sink gives up.</summary>
    private const int MaxFailures = 5;

    private readonly AudioJitterBuffer _buffer;
    private readonly Action<string>? _log;
    private readonly AutoResetEvent _ready = new(false);
    private readonly ManualResetEventSlim _settled = new(false);
    private readonly Thread _thread;
    private volatile bool _stopping;
    private volatile bool _switching;
    private volatile string? _wanted;
    private float[] _scratch = [];

    public WasapiOutput(AudioJitterBuffer buffer, string? deviceId, Action<string>? log)
    {
        _buffer = buffer;
        _wanted = deviceId;
        _log = log;
        _thread = new Thread(Run) { IsBackground = true, Name = "LuminaAudioOut" };
        _thread.SetApartmentState(ApartmentState.MTA);
    }

    public string Device { get; private set; } = "";

    public string Format { get; private set; } = "";

    public string? Failure { get; private set; }

    public double LatencyMs { get; private set; }

    public float Gain { get; set; } = 1f;

    /// <summary>Starts the thread and waits, briefly, for the first opening to settle.</summary>
    public void Start()
    {
        _thread.Start();
        _settled.Wait(TimeSpan.FromSeconds(3));
    }

    /// <summary>Plays on another endpoint from now on; null is Windows's default output.</summary>
    public void Use(string? deviceId)
    {
        if (string.Equals(deviceId, _wanted, StringComparison.OrdinalIgnoreCase))
            return;
        _wanted = deviceId;
        _switching = true;
        _ready.Set();
    }

    /// <summary>Stops the output; safe to call twice, which a chain shut down twice does.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _stopping = true;
        _ready.Set();
        if (_thread.IsAlive)
            _thread.Join(TimeSpan.FromSeconds(3));
        _ready.Dispose();
        _settled.Dispose();
    }

    private int _disposed;

    /// <summary>
    /// Opens the output, plays, and answers whatever ends it.
    /// </summary>
    /// <remarks>
    /// The loop is the recovery: a device switch and a device that died both come
    /// back here, and the only difference between them is whether the run threw. A
    /// chosen endpoint that fails is dropped for the default one — once — and the
    /// default one failing is retried on a delay, five times, rather than spun on.
    /// </remarks>
    private void Run()
    {
        CoreAudioNative.EnsureMta();
        int failures = 0;
        while (!_stopping)
        {
            _switching = false;
            string? wanted = _wanted;
            bool clean = false;
            try
            {
                RunOne(wanted);
                clean = true;
                Failure = null;
            }
            catch (Exception exception)
            {
                Failure = exception.Message;
                Device = "";
                Format = "";
                LatencyMs = 0;
                _log?.Invoke($"sortie audio indisponible : {exception.Message}");
            }
            _settled.Set();
            if (_stopping || clean)
                continue;

            if (_wanted is not null)
            {
                _log?.Invoke("la sortie choisie a disparu : repli sur la sortie par defaut de Windows.");
                _wanted = null;
                failures = 0;
                continue;
            }
            if (++failures >= MaxFailures)
            {
                _log?.Invoke($"sortie audio abandonnee apres {MaxFailures} tentatives : {Failure}");
                return;
            }
            _ready.WaitOne(RetryMs);
        }
    }

    /// <summary>One opening of the endpoint, played until it ends or is replaced.</summary>
    private void RunOne(string? deviceId)
    {
        var objects = new List<object>();
        IntPtr wave = IntPtr.Zero, mix = IntPtr.Zero;
        IAudioClient? client = null;
        bool started = false;
        try
        {
            var enumerator = CoreAudioNative.CreateEnumerator();
            objects.Add(enumerator);
            var device = AudioEndpoints.Open(enumerator, deviceId, out string name, out bool substituted);
            objects.Add(device);
            if (substituted)
                _log?.Invoke("la sortie enregistree est introuvable : sortie par defaut de Windows.");

            // First choice: our own format, and let the audio engine convert.
            var shape = AudioFormat.Source;
            wave = shape.Allocate();
            client = Activate(device, objects);
            const uint flags = CoreAudioNative.StreamFlagsEventCallback
                | CoreAudioNative.StreamFlagsAutoConvertPcm
                | CoreAudioNative.StreamFlagsSrcDefaultQuality;
            int hr = client.Initialize(0, flags, BufferHns, 0, wave, IntPtr.Zero);
            if (hr < 0)
            {
                // A client whose Initialize failed is not to be initialised
                // again: a fresh one is activated for the second attempt.
                _log?.Invoke($"AUTOCONVERTPCM refuse par le pilote (0x{hr:X8}) :"
                    + " repli sur le format de mixage du moteur audio.");
                client = Activate(device, objects);
                CoreAudioNative.Check(client.GetMixFormat(out mix), "IAudioClient.GetMixFormat");
                shape = AudioFormat.Read(mix);
                if (!shape.IsWritable)
                    throw new InvalidOperationException(
                        $"format de mixage inutilisable sans reechantillonneur : {shape}"
                        + $" (attendu {AudioFormat.Source.SampleRate} Hz).");
                CoreAudioNative.Check(
                    client.Initialize(0, CoreAudioNative.StreamFlagsEventCallback, BufferHns, 0, mix, IntPtr.Zero),
                    "IAudioClient.Initialize (format de mixage)");
            }

            CoreAudioNative.Check(client.GetBufferSize(out uint frames), "IAudioClient.GetBufferSize");
            CoreAudioNative.Check(client.SetEventHandle(_ready.SafeWaitHandle.DangerousGetHandle()),
                "IAudioClient.SetEventHandle");
            Guid renderIid = CoreAudioGuids.IidAudioRenderClient;
            CoreAudioNative.Check(client.GetService(ref renderIid, out object? service),
                "IAudioClient.GetService(IAudioRenderClient)");
            objects.Add(service!);
            var writer = (IAudioRenderClient)service!;

            LatencyMs = client.GetStreamLatency(out long latency) >= 0 ? latency / 10_000.0 : 0;
            Device = name;
            Format = shape.ToString();
            int wanted = (int)frames * AudioFormat.Source.Channels;
            if (_scratch.Length < wanted)
                _scratch = new float[wanted];
            _log?.Invoke($"sortie audio : « {name} » — {shape}, tampon {frames} trames"
                + $" ({frames * 1000.0 / shape.SampleRate:F0} ms), latence annoncee {LatencyMs:F1} ms.");

            // The buffer starts full of silence, so the first event arrives with
            // a period already accounted for rather than with a gap to fill.
            if (writer.GetBuffer(frames, out _) >= 0)
                writer.ReleaseBuffer(frames, CoreAudioNative.BufferFlagsSilent);
            CoreAudioNative.Check(client.Start(), "IAudioClient.Start");
            started = true;
            _settled.Set();

            while (!_stopping && !_switching)
            {
                _ready.WaitOne(EventPatienceMs);
                if (_stopping || _switching)
                    break;
                CoreAudioNative.Check(client.GetCurrentPadding(out uint padding), "IAudioClient.GetCurrentPadding");
                uint room = frames > padding ? frames - padding : 0;
                if (room == 0)
                    continue;
                CoreAudioNative.Check(writer.GetBuffer(room, out IntPtr target), "IAudioRenderClient.GetBuffer");
                var pcm = _scratch.AsSpan(0, (int)room * AudioFormat.Source.Channels);
                _buffer.Fill(pcm, Gain);
                shape.Write(pcm, target, (int)room);
                writer.ReleaseBuffer(room, 0);
            }
        }
        finally
        {
            if (started)
            {
                try { client!.Stop(); }
                catch (Exception) { /* it is being released anyway */ }
            }
            for (int index = objects.Count - 1; index >= 0; index--)
                Marshal.ReleaseComObject(objects[index]);
            if (wave != IntPtr.Zero) Marshal.FreeCoTaskMem(wave);
            if (mix != IntPtr.Zero) Marshal.FreeCoTaskMem(mix);
        }
    }

    private static IAudioClient Activate(IMMDevice device, List<object> objects)
    {
        Guid iid = CoreAudioGuids.IidAudioClient;
        CoreAudioNative.Check(device.Activate(ref iid, CoreAudioNative.ClsCtxAll, IntPtr.Zero, out object? client),
            "IMMDevice.Activate(IAudioClient)");
        objects.Add(client!);
        return (IAudioClient)client!;
    }
}
