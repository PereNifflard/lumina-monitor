namespace LuminaMonitor.Core.Media;

/// <summary>
/// One decoded picture: NV12 as the decoder produced it, BGRA on demand.
/// </summary>
/// <remarks>
/// The conversion is deliberately lazy. At 1328x2896 and sixty pictures a
/// second the decoder hands over 230 megapixels a second; a consumer that only
/// wants one of them as a bitmap — the probe writing a BMP, a thumbnail —
/// should not pay to convert the other fifty-nine. The window does want every
/// picture, but not here: it converts the one picture it is about to show,
/// straight into the bitmap's back buffer, so <see cref="Bgra"/> stays what it
/// always was — the convenience for whoever wants a managed array.
/// </remarks>
public sealed class VideoFrame
{
    private byte[]? _bgra;

    internal VideoFrame(int width, int height, int stride, byte[] nv12, uint timestamp,
        long firstArrivalTicks, long arrivalTicks, long decodeStartTicks, long decodedTicks)
    {
        Width = width;
        Height = height;
        Stride = stride;
        Nv12 = nv12;
        Timestamp = timestamp;
        FirstArrivalTicks = firstArrivalTicks;
        ArrivalTicks = arrivalTicks;
        DecodeStartTicks = decodeStartTicks;
        DecodedTicks = decodedTicks;
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>Bytes per luma row; the chroma plane uses the same stride.</summary>
    public int Stride { get; }

    /// <summary>The luma plane, then the interleaved chroma plane.</summary>
    public byte[] Nv12 { get; }

    /// <summary>How many bytes of <see cref="Nv12"/> the picture actually occupies.</summary>
    public int Nv12Length => Stride * Height * 3 / 2;

    /// <summary>The RTP timestamp of the access unit this picture came from.</summary>
    public uint Timestamp { get; }

    /// <summary><see cref="System.Diagnostics.Stopwatch"/> ticks when the first packet of the picture arrived.</summary>
    public long FirstArrivalTicks { get; }

    /// <summary><see cref="System.Diagnostics.Stopwatch"/> ticks when the last packet of the picture arrived.</summary>
    public long ArrivalTicks { get; }

    /// <summary><see cref="System.Diagnostics.Stopwatch"/> ticks when the decoder was handed the access unit.</summary>
    public long DecodeStartTicks { get; }

    /// <summary><see cref="System.Diagnostics.Stopwatch"/> ticks when the decoder gave the picture back.</summary>
    public long DecodedTicks { get; }

    /// <summary>Milliseconds from the last packet of the picture to the decoded picture.</summary>
    public double LatencyMs => Ms(DecodedTicks - ArrivalTicks);

    /// <summary>Milliseconds the picture spent waiting in the access-unit queue.</summary>
    public double QueueMs => Ms(DecodeStartTicks - ArrivalTicks);

    /// <summary>Milliseconds spent inside the decoder itself.</summary>
    public double DecodeMs => Ms(DecodedTicks - DecodeStartTicks);

    /// <summary>Milliseconds from the first packet of the picture to the last.</summary>
    public double WireMs => Ms(ArrivalTicks - FirstArrivalTicks);

    private static double Ms(long ticks) => ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    /// <summary>
    /// The picture as 32-bit BGRA, top row first, converted once. The rows are
    /// packed: the stride is <see cref="Width"/> * 4, not <see cref="Stride"/>,
    /// which is the decoder's own alignment for the NV12 planes.
    /// </summary>
    public byte[] Bgra => _bgra ??= Nv12ToBgra.Convert(this);

    /// <summary>
    /// A copy that owns its pixels: <see cref="Nv12"/> belongs to the decoder
    /// and is written over by the next picture, so anything kept past the
    /// callback — the last frame of a run, a still to save — is copied first.
    /// </summary>
    public VideoFrame Copy() =>
        new(Width, Height, Stride, Nv12[..Nv12Length], Timestamp,
            FirstArrivalTicks, ArrivalTicks, DecodeStartTicks, DecodedTicks);
}
