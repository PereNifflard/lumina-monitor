using System.Diagnostics;
using System.Runtime.InteropServices;
using LuminaMonitor.Core.Media.MediaFoundation;

namespace LuminaMonitor.Core.Media;

/// <summary>
/// H.264 to NV12 through the decoder Windows already has.
/// </summary>
/// <remarks>
/// The Microsoft H.264 Video Decoder MFT ships in every Windows 10 and 11 —
/// no Store extension, unlike HEVC — which is the whole reason this project
/// asks the phone for H.264. It is driven the plain synchronous way: feed one
/// access unit, pull pictures until it says it wants more. Three habits of
/// this MFT are worth naming, because each of them looks like a bug the first
/// time:
///
/// <list type="bullet">
/// <item>it will not say how large a picture is until it has parsed a real
/// SPS, so the first <c>ProcessOutput</c> after the first key frame answers
/// MF_E_TRANSFORM_STREAM_CHANGE and the output type has to be set again;</item>
/// <item>it allocates nothing: the sample and its buffer are ours, resized
/// whenever the format changes;</item>
/// <item>left alone it buffers several pictures before giving the first one
/// back, so <c>CODECAPI_AVLowLatencyMode</c> is set before the media types —
/// afterwards is too late.</item>
/// </list>
///
/// <para>Every call is on one thread, which must be COM-initialised as MTA:
/// the decoder is a free-threaded in-process object and an STA would put a
/// message pump between us and every frame.</para>
/// </remarks>
internal sealed class H264Decoder : IDisposable
{
    /// <summary>
    /// The RTP timestamp clock of this stream, in hertz.
    /// </summary>
    /// <remarks>
    /// Not the 90 kHz every H.264 textbook names: the phone advances the
    /// timestamp by exactly 400 units per picture at sixty a second, which is a
    /// 24 kHz clock, and it was measured on the wire rather than assumed. At
    /// 90 kHz the presentation times handed to the decoder ran nearly four times
    /// too slow, which a low-latency decoder tolerates but nothing downstream
    /// should have to.
    /// </remarks>
    private const int RtpClock = 24_000;

    private readonly IMFTransform _transform;
    private IMFSample? _output;
    private IMFMediaBuffer? _outputBuffer;
    private byte[]? _nv12;
    private uint _outputSize;
    private uint _firstTimestamp;
    private bool _seenFirst;
    private bool _started;
    private bool _disposed;

    public H264Decoder()
    {
        MfNative.Check(MfNative.CoInitializeEx(IntPtr.Zero, MfNative.CoinitMultithreaded), "CoInitializeEx");
        MfNative.Check(MfNative.MFStartup(MfNative.MfVersion, MfNative.MfStartupLite), "MFStartup");
        MfNative.Check(MfNative.CoCreateInstance(ref MfGuids.ClsidH264Decoder, IntPtr.Zero,
            MfNative.ClsCtxInprocServer, ref MfGuids.IidTransform, out object instance), "CoCreateInstance(H264 MFT)");
        _transform = (IMFTransform)instance;
        LowLatency();
        SetInputType();
        NegotiateOutput();
        MfNative.Check(_transform.ProcessMessage(MfNative.MessageNotifyBeginStreaming, IntPtr.Zero),
            "MFT_MESSAGE_NOTIFY_BEGIN_STREAMING");
        MfNative.Check(_transform.ProcessMessage(MfNative.MessageNotifyStartOfStream, IntPtr.Zero),
            "MFT_MESSAGE_NOTIFY_START_OF_STREAM");
        _started = true;
    }

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Stride { get; private set; }
    public long FramesDecoded { get; private set; }
    public long StreamChanges { get; private set; }

    /// <summary>Whether the decoder accepted low-latency mode by either door.</summary>
    public bool LowLatencySet { get; private set; }

    /// <summary>Access units handed to the decoder, whether or not a picture came back.</summary>
    public long UnitsFed { get; private set; }

    /// <summary>
    /// Pictures the decoder is sitting on: everything fed, less everything
    /// given back. Zero is what this stream now settles at; any stable figure
    /// is a reorder queue, and the mirror is that many pictures behind the
    /// phone — forty-eight before low latency, twelve before the SPS was
    /// rewritten, and each of them was a fifth of a second or worse.
    /// </summary>
    public long InFlight => UnitsFed - FramesDecoded;

    /// <summary>
    /// Feeds one access unit and hands back every picture it completes.
    /// </summary>
    /// <remarks>
    /// The picture buffer belongs to the decoder and is written over by the
    /// next call: at this size — five megabytes and a half a picture — handing
    /// out a fresh array sixty times a second would spend more time in the
    /// garbage collector than in the decoder. A consumer that keeps a picture
    /// past the callback calls <see cref="VideoFrame.Copy"/>.
    /// </remarks>
    public void Decode(in AccessUnit unit, Action<VideoFrame> onFrame)
    {
        long start = Stopwatch.GetTimestamp();
        if (!_seenFirst) { _firstTimestamp = unit.Timestamp; _seenFirst = true; }
        long time = (long)(int)(unit.Timestamp - _firstTimestamp) * 10_000_000L / RtpClock;

        IMFSample input = NewSample(unit.Data, time);
        int hr = _transform.ProcessInput(0, input, 0);
        Marshal.FinalReleaseComObject(input);
        MfNative.Check(hr, "ProcessInput");
        UnitsFed++;
        Drain(unit.FirstArrivalTicks, unit.ArrivalTicks, start, onFrame);
    }

    /// <summary>Ends the stream and hands back the pictures the decoder still holds.</summary>
    public void Finish(Action<VideoFrame> onFrame)
    {
        MfNative.Check(_transform.ProcessMessage(MfNative.MessageNotifyEndOfStream, IntPtr.Zero),
            "MFT_MESSAGE_NOTIFY_END_OF_STREAM");
        MfNative.Check(_transform.ProcessMessage(MfNative.MessageCommandDrain, IntPtr.Zero),
            "MFT_MESSAGE_COMMAND_DRAIN");
        long now = Stopwatch.GetTimestamp();
        Drain(now, now, now, onFrame);
    }

    /// <summary>Throws away what the decoder holds; the next access unit must be a key frame.</summary>
    public void Flush()
    {
        MfNative.Check(_transform.ProcessMessage(MfNative.MessageCommandFlush, IntPtr.Zero), "MFT_MESSAGE_COMMAND_FLUSH");
        _seenFirst = false;
    }

    private void Drain(long firstArrivalTicks, long arrivalTicks, long decodeStartTicks, Action<VideoFrame> onFrame)
    {
        while (true)
        {
            if (_output is null)
                AllocateOutput(_outputSize == 0 ? 1u << 20 : _outputSize);
            // The buffer is ours and reused, so it still carries the length of
            // the picture before: left alone, the MFT sees a full buffer with
            // no room and answers a bare E_FAIL.
            MfNative.Check(_outputBuffer!.SetCurrentLength(0), "SetCurrentLength(0)");
            var buffers = new MftOutputDataBuffer { StreamId = 0, Sample = _output };
            int hr = _transform.ProcessOutput(0, 1, ref buffers, out _);
            buffers.Sample = null;
            if (hr == MfNative.NeedMoreInput)
                return;
            if (hr == MfNative.StreamChange)
            {
                StreamChanges++;
                NegotiateOutput();
                continue;
            }
            MfNative.Check(hr, $"ProcessOutput (images {FramesDecoded}, changements {StreamChanges},"
                + $" tampon {_outputSize} octets, {Width}x{Height})");
            onFrame(ReadFrame(firstArrivalTicks, arrivalTicks, decodeStartTicks));
        }
    }

    private VideoFrame ReadFrame(long firstArrivalTicks, long arrivalTicks, long decodeStartTicks)
    {
        IMFMediaBuffer buffer = _outputBuffer ?? throw new InvalidOperationException("aucun tampon de sortie.");
        MfNative.Check(buffer.GetCurrentLength(out uint length), "GetCurrentLength");
        MfNative.Check(buffer.Lock(out IntPtr data, out _, out _), "IMFMediaBuffer.Lock");
        int wanted = Math.Max(Stride * Height * 3 / 2, (int)length);
        if (_nv12 is null || _nv12.Length < wanted)
            _nv12 = new byte[wanted];
        byte[] nv12 = _nv12;
        try
        {
            Marshal.Copy(data, nv12, 0, (int)length);
        }
        finally
        {
            buffer.Unlock();
        }
        MfNative.Check(_output!.GetSampleTime(out long time), "GetSampleTime");
        FramesDecoded++;
        return new VideoFrame(Width, Height, Stride, nv12,
            (uint)(time * RtpClock / 10_000_000L + _firstTimestamp),
            firstArrivalTicks, arrivalTicks, decodeStartTicks, Stopwatch.GetTimestamp());
    }

    // --- Setup -------------------------------------------------------------------

    /// <summary>
    /// Low latency before the media types: the MFT reads it when it configures
    /// itself. It is asked twice, because the same setting has two doors and
    /// this decoder does not open both: <c>MF_LOW_LATENCY</c> on the
    /// transform's own attribute store, and <c>CODECAPI_AVLowLatencyMode</c> on
    /// its codec interface — one GUID, two spellings.
    /// </summary>
    /// <remarks>
    /// Without it the Microsoft H.264 decoder keeps a reorder queue and hands
    /// back nothing until it is full: measured at forty-eight pictures held,
    /// every session, which at the rate this phone streams is between one and
    /// two seconds of screen that has already happened. Nothing downstream can
    /// see it — each picture still carries the time its own packets arrived —
    /// so the mirror looks instantaneous by every counter and lags by two
    /// seconds to the eye. <see cref="LowLatencySet"/> says whether either door
    /// opened, so a future decoder that refuses both is noticed rather than
    /// silently slow.
    ///
    /// <para>It is necessary and not sufficient. With both doors open the
    /// decoder still held exactly twelve pictures, which is not a setting but
    /// arithmetic: the phone's SPS declares no reorder depth, so the decoder
    /// assumes the largest the level allows. That one is fixed upstream, in
    /// <see cref="SpsRewriter"/>, and only then does <see cref="InFlight"/>
    /// settle at zero.</para>
    /// </remarks>
    private void LowLatency()
    {
        if (_transform.GetAttributes(out var attributes) == 0 && attributes is not null)
        {
            if (attributes.SetUINT32(ref MfGuids.CodecApiLowLatency, 1) == 0)
                LowLatencySet = true;
            Marshal.ReleaseComObject(attributes);
        }

        if (_transform is not ICodecAPI codec)
            return;
        IntPtr variant = Marshal.AllocCoTaskMem(24);
        try
        {
            for (int i = 0; i < 24; i += 8) Marshal.WriteInt64(variant, i, 0);
            Marshal.WriteInt16(variant, 0, (short)MfNative.VariantBool);
            Marshal.WriteInt16(variant, 8, MfNative.VariantTrue);
            if (codec.SetValue(ref MfGuids.CodecApiLowLatency, variant) == 0)
                LowLatencySet = true;
        }
        finally
        {
            Marshal.FreeCoTaskMem(variant);
        }
    }

    private void SetInputType()
    {
        MfNative.Check(MfNative.MFCreateMediaType(out IMFMediaType type), "MFCreateMediaType");
        MfNative.Check(type.SetGUID(ref MfGuids.MajorTypeKey, ref MfGuids.MediaTypeVideo), "MF_MT_MAJOR_TYPE");
        MfNative.Check(type.SetGUID(ref MfGuids.SubtypeKey, ref MfGuids.VideoFormatH264), "MF_MT_SUBTYPE");
        MfNative.Check(type.SetUINT32(ref MfGuids.InterlaceModeKey, 2), "MF_MT_INTERLACE_MODE");
        MfNative.Check(_transform.SetInputType(0, type, 0), "SetInputType(H264)");
        Marshal.FinalReleaseComObject(type);
    }

    /// <summary>Picks the NV12 output type, then sizes our picture buffer from what the MFT asks for.</summary>
    private void NegotiateOutput()
    {
        IMFMediaType? chosen = null;
        for (uint index = 0; ; index++)
        {
            int hr = _transform.GetOutputAvailableType(0, index, out IMFMediaType? candidate);
            if (hr == MfNative.NoMoreTypes || candidate is null)
                break;
            MfNative.Check(hr, "GetOutputAvailableType");
            if (candidate.GetGUID(ref MfGuids.SubtypeKey, out Guid subtype) == MfNative.Ok
                && subtype == MfGuids.VideoFormatNv12)
            {
                chosen = candidate;
                break;
            }
            Marshal.FinalReleaseComObject(candidate);
        }
        if (chosen is null)
            throw new InvalidOperationException("Le decodeur H.264 n'offre aucun type de sortie NV12.");

        MfNative.Check(_transform.SetOutputType(0, chosen, 0), "SetOutputType(NV12)");
        ReadGeometry(chosen);
        Marshal.FinalReleaseComObject(chosen);

        MfNative.Check(_transform.GetOutputStreamInfo(0, out MftOutputStreamInfo info), "GetOutputStreamInfo");
        uint size = info.Size != 0 ? info.Size : (uint)Math.Max(1, Stride * Height * 3 / 2);
        if (_output is null || size > _outputSize)
            AllocateOutput(size);
    }

    private void ReadGeometry(IMFMediaType type)
    {
        if (type.GetUINT64(ref MfGuids.FrameSizeKey, out ulong frameSize) == MfNative.Ok)
        {
            Width = (int)(frameSize >> 32);
            Height = (int)(frameSize & 0xFFFFFFFF);
        }
        Stride = type.GetUINT32(ref MfGuids.DefaultStrideKey, out uint stride) == MfNative.Ok && stride != 0
            ? Math.Abs((int)stride)
            : Width;
    }

    private void AllocateOutput(uint size)
    {
        ReleaseOutput();
        MfNative.Check(MfNative.MFCreateMemoryBuffer(size, out IMFMediaBuffer buffer), "MFCreateMemoryBuffer");
        MfNative.Check(MfNative.MFCreateSample(out IMFSample sample), "MFCreateSample");
        MfNative.Check(sample.AddBuffer(buffer), "IMFSample.AddBuffer");
        _outputBuffer = buffer;
        _output = sample;
        _outputSize = size;
    }

    private static IMFSample NewSample(byte[] data, long time)
    {
        MfNative.Check(MfNative.MFCreateMemoryBuffer((uint)data.Length, out IMFMediaBuffer buffer), "MFCreateMemoryBuffer");
        MfNative.Check(buffer.Lock(out IntPtr target, out _, out _), "IMFMediaBuffer.Lock");
        Marshal.Copy(data, 0, target, data.Length);
        buffer.Unlock();
        MfNative.Check(buffer.SetCurrentLength((uint)data.Length), "SetCurrentLength");
        MfNative.Check(MfNative.MFCreateSample(out IMFSample sample), "MFCreateSample");
        MfNative.Check(sample.AddBuffer(buffer), "IMFSample.AddBuffer");
        MfNative.Check(sample.SetSampleTime(time), "SetSampleTime");
        MfNative.Check(sample.SetSampleDuration(10_000_000L / 60), "SetSampleDuration");
        Marshal.FinalReleaseComObject(buffer);
        return sample;
    }

    private void ReleaseOutput()
    {
        if (_outputBuffer is not null) { Marshal.FinalReleaseComObject(_outputBuffer); _outputBuffer = null; }
        if (_output is not null) { Marshal.FinalReleaseComObject(_output); _output = null; }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_started)
            _transform.ProcessMessage(MfNative.MessageNotifyEndStreaming, IntPtr.Zero);
        ReleaseOutput();
        Marshal.FinalReleaseComObject(_transform);
        MfNative.MFShutdown();
    }
}
