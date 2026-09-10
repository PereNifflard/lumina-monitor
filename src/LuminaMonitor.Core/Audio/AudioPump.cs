using System.Runtime.InteropServices;
using LuminaMonitor.Core.Audio.CoreAudio;

namespace LuminaMonitor.Core.Audio;

/// <summary>
/// One direction of sound, from a capture endpoint to a render endpoint, in
/// WASAPI shared mode: what "listen to this device" does, but between two
/// endpoints of our choosing.
/// </summary>
/// <remarks>
/// <para><b>Format.</b> Both streams are opened in one format of our own —
/// 48 kHz, stereo, 32-bit float — with <c>AUTOCONVERTPCM</c>: the audio engine
/// resamples and remixes to each endpoint's own format (8 or 16 kHz mono for a
/// hands-free link, 48 kHz stereo for headphones). The pump itself never
/// converts anything: it copies frames.</para>
///
/// <para><b>Latency.</b> The capture side is event-driven; each packet is
/// written into the render buffer only as far as the render queue stays under
/// <see cref="MaxQueuedMs"/>. What does not fit is dropped rather than queued:
/// on a call, late is worse than a missing ten milliseconds.</para>
///
/// <para><b>End.</b> An endpoint that disappears — the call ends and Windows
/// closes the hands-free audio link — answers AUDCLNT_E_DEVICE_INVALIDATED;
/// the pump stops, keeps that HRESULT in <see cref="Failure"/> and raises
/// <see cref="Died"/>.</para>
/// </remarks>
internal sealed class AudioPump : IDisposable
{
    private const int SampleRate = 48_000;
    private const int Channels = 2;
    private const int BytesPerFrame = Channels * sizeof(float);
    private const long BufferHns = 1_000_000;   // 100 ms, in 100 ns units
    private const int MaxQueuedMs = 60;
    private const int PrimeMs = 20;

    private readonly string _captureId;
    private readonly string _renderId;
    private readonly float _gain;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly AutoResetEvent _packets = new(false);
    private volatile bool _stopping;
    private Exception? _startFailure;

    public AudioPump(string name, string captureId, string renderId, float gain = 1f)
    {
        Name = name;
        _captureId = captureId;
        _renderId = renderId;
        _gain = gain;
        _thread = new Thread(Run) { IsBackground = true, Name = "AudioPump " + name };
        _thread.SetApartmentState(ApartmentState.MTA);
    }

    public string Name { get; }
    public long FramesCaptured { get; private set; }
    public long FramesRendered { get; private set; }
    public long FramesDropped { get; private set; }
    /// <summary>The HRESULT that stopped the pump, when it did not stop on request.</summary>
    public int? Failure { get; private set; }
    public bool IsRunning => _thread.IsAlive && !_stopping;

    /// <summary>Raised on the pump's thread when it stops on its own (endpoint gone, error).</summary>
    public event Action<AudioPump>? Died;

    /// <summary>Opens both streams and starts moving sound; throws if either endpoint refuses.</summary>
    public void Start()
    {
        _thread.Start();
        _ready.Wait();
        if (_startFailure is not null)
        {
            _thread.Join();
            throw _startFailure;
        }
    }

    /// <summary>Stops the pump and waits for its thread; safe to call twice and from any thread but the pump's.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stopping = true;
        _packets.Set();
        if (_thread.IsAlive) _thread.Join(TimeSpan.FromSeconds(3));
        _packets.Dispose();
        _ready.Dispose();
    }

    private int _disposed;

    private void Run()
    {
        var objects = new List<object>();
        IntPtr format = IntPtr.Zero;
        IAudioClient? capture = null, render = null;
        bool started = false;
        try
        {
            format = MakeFormat();
            var enumerator = CoreAudioNative.CreateEnumerator();
            objects.Add(enumerator);
            capture = Open(enumerator, _captureId, objects);
            render = Open(enumerator, _renderId, objects);

            const uint convert = CoreAudioNative.StreamFlagsAutoConvertPcm | CoreAudioNative.StreamFlagsSrcDefaultQuality;
            CoreAudioNative.Check(capture.Initialize(0, convert | CoreAudioNative.StreamFlagsEventCallback, BufferHns, 0, format, IntPtr.Zero),
                "IAudioClient.Initialize (capture)");
            CoreAudioNative.Check(capture.SetEventHandle(_packets.SafeWaitHandle.DangerousGetHandle()), "IAudioClient.SetEventHandle");
            CoreAudioNative.Check(render.Initialize(0, convert, BufferHns, 0, format, IntPtr.Zero), "IAudioClient.Initialize (rendu)");
            CoreAudioNative.Check(render.GetBufferSize(out uint renderFrames), "IAudioClient.GetBufferSize");

            Guid captureIid = CoreAudioGuids.IidAudioCaptureClient;
            CoreAudioNative.Check(capture.GetService(ref captureIid, out object? captureService), "GetService(IAudioCaptureClient)");
            objects.Add(captureService!);
            var reader = (IAudioCaptureClient)captureService!;
            Guid renderIid = CoreAudioGuids.IidAudioRenderClient;
            CoreAudioNative.Check(render.GetService(ref renderIid, out object? renderService), "GetService(IAudioRenderClient)");
            objects.Add(renderService!);
            var writer = (IAudioRenderClient)renderService!;

            // A little silence ahead, so the first packets do not find the render queue empty.
            uint prime = Math.Min(renderFrames, (uint)(SampleRate * PrimeMs / 1000));
            if (writer.GetBuffer(prime, out _) >= 0) writer.ReleaseBuffer(prime, CoreAudioNative.BufferFlagsSilent);

            CoreAudioNative.Check(render.Start(), "IAudioClient.Start (rendu)");
            CoreAudioNative.Check(capture.Start(), "IAudioClient.Start (capture)");
            started = true;
            _ready.Set();

            uint cap = Math.Min(renderFrames, (uint)(SampleRate * MaxQueuedMs / 1000));
            while (!_stopping)
            {
                _packets.WaitOne(200);
                if (_stopping) break;
                if (!Drain(reader, render, writer, renderFrames, cap)) break;
            }
        }
        catch (Exception exception)
        {
            if (!started)
            {
                _startFailure = exception;
                _ready.Set();
            }
            else
            {
                Failure = exception.HResult;
                _stopping = true;
            }
        }
        finally
        {
            if (started)
            {
                capture?.Stop();
                render?.Stop();
            }
            for (int i = objects.Count - 1; i >= 0; i--) Marshal.ReleaseComObject(objects[i]);
            if (format != IntPtr.Zero) Marshal.FreeCoTaskMem(format);
        }
        if (started && Failure is not null) Died?.Invoke(this);
    }

    /// <summary>Moves every waiting capture packet into the render queue; false when the pump must stop.</summary>
    private unsafe bool Drain(IAudioCaptureClient reader, IAudioClient render, IAudioRenderClient writer, uint renderFrames, uint cap)
    {
        while (true)
        {
            int hr = reader.GetNextPacketSize(out uint waiting);
            if (hr < 0) { Failure = hr; return false; }
            if (waiting == 0) return true;

            hr = reader.GetBuffer(out IntPtr source, out uint frames, out uint flags, out _, out _);
            if (hr == CoreAudioNative.BufferEmpty) return true;
            if (hr < 0) { Failure = hr; return false; }
            FramesCaptured += frames;

            hr = render.GetCurrentPadding(out uint padding);
            if (hr < 0) { reader.ReleaseBuffer(frames); Failure = hr; return false; }
            uint room = Math.Min(renderFrames - padding, cap > padding ? cap - padding : 0);
            uint count = Math.Min(frames, room);
            if (count > 0)
            {
                hr = writer.GetBuffer(count, out IntPtr target);
                if (hr < 0) { reader.ReleaseBuffer(frames); Failure = hr; return false; }
                bool silent = (flags & CoreAudioNative.BufferFlagsSilent) != 0 || _gain == 0f;
                if (!silent)
                {
                    var from = new ReadOnlySpan<float>((void*)source, (int)count * Channels);
                    var to = new Span<float>((void*)target, (int)count * Channels);
                    if (_gain == 1f) from.CopyTo(to);
                    else for (int i = 0; i < from.Length; i++) to[i] = from[i] * _gain;
                }
                writer.ReleaseBuffer(count, silent ? CoreAudioNative.BufferFlagsSilent : 0);
                FramesRendered += count;
            }
            FramesDropped += frames - count;
            reader.ReleaseBuffer(frames);
        }
    }

    private static IAudioClient Open(IMMDeviceEnumerator enumerator, string id, List<object> objects)
    {
        CoreAudioNative.Check(enumerator.GetDevice(id, out var device), "IMMDeviceEnumerator.GetDevice");
        objects.Add(device!);
        Guid iid = CoreAudioGuids.IidAudioClient;
        CoreAudioNative.Check(device!.Activate(ref iid, CoreAudioNative.ClsCtxAll, IntPtr.Zero, out object? client),
            "IMMDevice.Activate(IAudioClient)");
        objects.Add(client!);
        return (IAudioClient)client!;
    }

    /// <summary>WAVEFORMATEX for 48 kHz stereo IEEE float, in CoTaskMem.</summary>
    private static IntPtr MakeFormat()
    {
        IntPtr format = Marshal.AllocCoTaskMem(18);
        Marshal.WriteInt16(format, 0, 3);                              // WAVE_FORMAT_IEEE_FLOAT
        Marshal.WriteInt16(format, 2, Channels);
        Marshal.WriteInt32(format, 4, SampleRate);
        Marshal.WriteInt32(format, 8, SampleRate * BytesPerFrame);    // nAvgBytesPerSec
        Marshal.WriteInt16(format, 12, BytesPerFrame);                 // nBlockAlign
        Marshal.WriteInt16(format, 14, 32);                            // wBitsPerSample
        Marshal.WriteInt16(format, 16, 0);                             // cbSize
        return format;
    }
}
