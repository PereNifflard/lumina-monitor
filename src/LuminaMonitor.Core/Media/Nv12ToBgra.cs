using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace LuminaMonitor.Core.Media;

/// <summary>
/// NV12 to BGRA, BT.709 studio range — the colours a phone display encodes.
/// </summary>
/// <remarks>
/// Sixteen pixels at a time with AVX2, eight with SSE2, one at a time when the
/// processor offers neither. At 1328x2896 and sixty pictures a second there are
/// 230 megapixels to convert every second, and the scalar version — good enough
/// when the only consumer was a probe saving one bitmap — cost four to five
/// milliseconds a picture, spent on the decode thread, which is a third of the
/// budget of a sixty-hertz mirror.
///
/// <para><b>The arithmetic.</b> Six-bit fixed point throughout, and the luma
/// term comes from a single unsigned high multiply: a byte unpacked against
/// itself is exactly <c>y * 257</c>, and <c>(y * 257 * 18997) >> 16</c> is
/// <c>1.164 * 64 * y</c> to better than a tenth of a level — far closer than
/// rounding 1.164 * 64 to an integer would give. Everything after that is
/// saturating sixteen-bit arithmetic, so a value that overflows lands on 32767
/// and then clamps to white, which is where it belonged.</para>
///
/// <para>Two rows share one chroma row and two columns share one chroma pair,
/// and the chroma sample is read once for both columns — no interpolation,
/// which is what a screen capture wants: sharp text, no invented colour between
/// glyph edges.</para>
///
/// <para>The destination overload writes straight into caller memory — a
/// <c>WriteableBitmap</c> back buffer — which is why the window pays for one
/// pass over the picture instead of a conversion, a copy into a staging array
/// and a copy out of it.</para>
/// </remarks>
public static class Nv12ToBgra
{
    private const int Shift = 6;

    /// <summary>round(1.164 * 64 * 65536 / 257): the luma gain, for an unsigned high multiply.</summary>
    private const ushort LumaGain = 18997;

    /// <summary>1.164 * 64 * -16, plus half a unit so the shift rounds instead of truncating.</summary>
    private const short LumaBias = -1160;

    private const short Rv = 115;      // 1.793 * 64
    private const short Gu = 14;       // 0.213 * 64
    private const short Gv = 34;       // 0.533 * 64
    private const short Bu = 135;      // 2.112 * 64

    /// <summary>Which bytes of a 16-byte chroma run carry U, each one twice.</summary>
    private static readonly Vector128<byte> UPicker = Vector128.Create(
        (byte)0, 0, 2, 2, 4, 4, 6, 6, 8, 8, 10, 10, 12, 12, 14, 14);

    /// <summary>The same for V.</summary>
    private static readonly Vector128<byte> VPicker = Vector128.Create(
        (byte)1, 1, 3, 3, 5, 5, 7, 7, 9, 9, 11, 11, 13, 13, 15, 15);

    public static byte[] Convert(VideoFrame frame) =>
        Convert(frame.Nv12, frame.Width, frame.Height, frame.Stride);

    public static byte[] Convert(byte[] nv12, int width, int height, int stride)
    {
        byte[] bgra = new byte[width * height * 4];
        unsafe
        {
            fixed (byte* destination = bgra)
                Convert(nv12, width, height, stride, (IntPtr)destination, width * 4);
        }
        return bgra;
    }

    /// <summary>
    /// Converts the picture into caller memory, bands of rows in parallel.
    /// </summary>
    /// <param name="nv12">The luma plane, then the interleaved chroma plane.</param>
    /// <param name="width">Picture width in pixels; the destination rows are this wide.</param>
    /// <param name="height">Picture height in pixels.</param>
    /// <param name="stride">Bytes per luma row, which is the decoder alignment, not the width.</param>
    /// <param name="destination">The first byte of the top row of the destination.</param>
    /// <param name="destinationStride">Bytes per destination row, at least <paramref name="width"/> * 4.</param>
    public static unsafe void Convert(ReadOnlySpan<byte> nv12, int width, int height, int stride,
        IntPtr destination, int destinationStride)
    {
        if (width <= 0 || height <= 0)
            return;
        if (stride < width)
            throw new ArgumentOutOfRangeException(nameof(stride), "Le pas NV12 est plus etroit que l'image.");
        if (destinationStride < width * 4)
            throw new ArgumentOutOfRangeException(nameof(destinationStride), "Le pas de destination est trop etroit.");
        if (nv12.Length < stride * height * 3 / 2)
            throw new ArgumentException("Plan NV12 incomplet.", nameof(nv12));

        fixed (byte* source = nv12)
        {
            // Pointers cannot cross into a lambda, so the addresses travel as
            // integers; the fixed block outlives the loop because Parallel.For
            // is synchronous.
            nint luma = (nint)source;
            nint chroma = luma + (nint)stride * height;
            nint target = destination;

            // Bands rather than single rows: 1448 row pairs of a few
            // microseconds each would spend a visible share of the picture in
            // the scheduler rather than in the conversion.
            int rowPairs = (height + 1) / 2;
            int bands = Math.Clamp(Environment.ProcessorCount, 1, rowPairs);
            int pairsPerBand = (rowPairs + bands - 1) / bands;

            Parallel.For(0, bands, band =>
            {
                int from = band * pairsPerBand;
                int to = Math.Min(rowPairs, from + pairsPerBand);
                for (int pair = from; pair < to; pair++)
                {
                    byte* chromaRow = (byte*)chroma + (nint)pair * stride;
                    for (int row = pair * 2; row < pair * 2 + 2 && row < height; row++)
                        ConvertRow((byte*)luma + (nint)row * stride, chromaRow,
                            (byte*)target + (nint)row * destinationStride, width);
                }
            });
        }
    }

    private static unsafe void ConvertRow(byte* luma, byte* chroma, byte* destination, int width)
    {
        int x = 0;
        if (Avx2.IsSupported)
            x = ConvertRowAvx2(luma, chroma, destination, width);
        else if (Sse2.IsSupported)
            x = ConvertRowSse2(luma, chroma, destination, width);
        ConvertRowScalar(luma, chroma, destination, x, width);
    }

    /// <summary>Sixteen pixels an iteration.</summary>
    private static unsafe int ConvertRowAvx2(byte* luma, byte* chroma, byte* destination, int width)
    {
        Vector256<ushort> gain = Vector256.Create(LumaGain);
        Vector256<ushort> spread = Vector256.Create((ushort)257);
        Vector256<short> bias = Vector256.Create(LumaBias);
        Vector256<short> half = Vector256.Create((short)128);
        Vector256<short> rv = Vector256.Create(Rv);
        Vector256<short> gu = Vector256.Create(Gu);
        Vector256<short> gv = Vector256.Create(Gv);
        Vector256<short> bu = Vector256.Create(Bu);
        Vector128<byte> alpha = Vector128.Create((byte)255);

        int x = 0;
        for (; x + 16 <= width; x += 16)
        {
            Vector256<ushort> scaled = Avx2.MultiplyLow(
                Avx2.ConvertToVector256Int16(Sse2.LoadVector128(luma + x)).AsUInt16(), spread);
            Vector256<short> y = Avx2.AddSaturate(Avx2.MultiplyHigh(scaled, gain).AsInt16(), bias);

            Vector128<byte> pairs = Sse2.LoadVector128(chroma + x);
            Vector256<short> u = Avx2.Subtract(
                Avx2.ConvertToVector256Int16(Ssse3.Shuffle(pairs, UPicker)), half);
            Vector256<short> v = Avx2.Subtract(
                Avx2.ConvertToVector256Int16(Ssse3.Shuffle(pairs, VPicker)), half);

            Vector256<short> r = Avx2.ShiftRightArithmetic(
                Avx2.AddSaturate(y, Avx2.MultiplyLow(v, rv)), (byte)Shift);
            Vector256<short> g = Avx2.ShiftRightArithmetic(
                Avx2.SubtractSaturate(y, Avx2.AddSaturate(
                    Avx2.MultiplyLow(u, gu), Avx2.MultiplyLow(v, gv))), (byte)Shift);
            Vector256<short> b = Avx2.ShiftRightArithmetic(
                Avx2.AddSaturate(y, Avx2.MultiplyLow(u, bu)), (byte)Shift);

            Vector128<byte> r8 = Sse2.PackUnsignedSaturate(r.GetLower(), r.GetUpper());
            Vector128<byte> g8 = Sse2.PackUnsignedSaturate(g.GetLower(), g.GetUpper());
            Vector128<byte> b8 = Sse2.PackUnsignedSaturate(b.GetLower(), b.GetUpper());

            Vector128<ushort> bgLow = Sse2.UnpackLow(b8, g8).AsUInt16();
            Vector128<ushort> bgHigh = Sse2.UnpackHigh(b8, g8).AsUInt16();
            Vector128<ushort> raLow = Sse2.UnpackLow(r8, alpha).AsUInt16();
            Vector128<ushort> raHigh = Sse2.UnpackHigh(r8, alpha).AsUInt16();

            byte* output = destination + x * 4;
            Sse2.Store(output, Sse2.UnpackLow(bgLow, raLow).AsByte());
            Sse2.Store(output + 16, Sse2.UnpackHigh(bgLow, raLow).AsByte());
            Sse2.Store(output + 32, Sse2.UnpackLow(bgHigh, raHigh).AsByte());
            Sse2.Store(output + 48, Sse2.UnpackHigh(bgHigh, raHigh).AsByte());
        }
        return x;
    }

    /// <summary>Eight pixels an iteration, with nothing newer than SSE2.</summary>
    private static unsafe int ConvertRowSse2(byte* luma, byte* chroma, byte* destination, int width)
    {
        Vector128<ushort> gain = Vector128.Create(LumaGain);
        Vector128<short> bias = Vector128.Create(LumaBias);
        Vector128<short> half = Vector128.Create((short)128);
        Vector128<short> rv = Vector128.Create(Rv);
        Vector128<short> gu = Vector128.Create(Gu);
        Vector128<short> gv = Vector128.Create(Gv);
        Vector128<short> bu = Vector128.Create(Bu);
        Vector128<byte> alpha = Vector128.Create((byte)255);

        int x = 0;
        for (; x + 8 <= width; x += 8)
        {
            // A byte unpacked against itself is that byte times 257, which is
            // exactly what the high multiply below expects.
            Vector128<byte> eight = Sse2.LoadScalarVector128((long*)(luma + x)).AsByte();
            Vector128<ushort> scaled = Sse2.UnpackLow(eight, eight).AsUInt16();
            Vector128<short> y = Sse2.AddSaturate(Sse2.MultiplyHigh(scaled, gain).AsInt16(), bias);

            // [U0 V0 U1 V1 U2 V2 U3 V3] shuffled into [U0 U0 U1 U1 …] and [V0 V0 V1 V1 …].
            Vector128<byte> pairs = Sse2.LoadScalarVector128((long*)(chroma + x)).AsByte();
            Vector128<short> interleaved = Sse2.UnpackLow(pairs, Vector128<byte>.Zero).AsInt16();
            Vector128<short> u = Sse2.Subtract(
                Sse2.ShuffleHigh(Sse2.ShuffleLow(interleaved, 0xA0), 0xA0), half);
            Vector128<short> v = Sse2.Subtract(
                Sse2.ShuffleHigh(Sse2.ShuffleLow(interleaved, 0xF5), 0xF5), half);

            Vector128<short> r = Sse2.ShiftRightArithmetic(
                Sse2.AddSaturate(y, Sse2.MultiplyLow(v, rv)), (byte)Shift);
            Vector128<short> g = Sse2.ShiftRightArithmetic(
                Sse2.SubtractSaturate(y, Sse2.AddSaturate(
                    Sse2.MultiplyLow(u, gu), Sse2.MultiplyLow(v, gv))), (byte)Shift);
            Vector128<short> b = Sse2.ShiftRightArithmetic(
                Sse2.AddSaturate(y, Sse2.MultiplyLow(u, bu)), (byte)Shift);

            Vector128<ushort> bg = Sse2.UnpackLow(
                Sse2.PackUnsignedSaturate(b, b), Sse2.PackUnsignedSaturate(g, g)).AsUInt16();
            Vector128<ushort> ra = Sse2.UnpackLow(Sse2.PackUnsignedSaturate(r, r), alpha).AsUInt16();

            byte* output = destination + x * 4;
            Sse2.Store(output, Sse2.UnpackLow(bg, ra).AsByte());
            Sse2.Store(output + 16, Sse2.UnpackHigh(bg, ra).AsByte());
        }
        return x;
    }

    /// <summary>The tail, and the whole row on a processor with no vector unit.</summary>
    private static unsafe void ConvertRowScalar(byte* luma, byte* chroma, byte* destination, int from, int width)
    {
        for (int x = from; x < width; x++)
        {
            int pair = x & ~1;
            int u = chroma[pair] - 128;
            int v = chroma[pair + 1] - 128;
            int y = (luma[x] * 257 * LumaGain >> 16) + LumaBias;
            byte* output = destination + x * 4;
            output[0] = Saturate(y + Bu * u);
            output[1] = Saturate(y - Gu * u - Gv * v);
            output[2] = Saturate(y + Rv * v);
            output[3] = 255;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Saturate(int value)
    {
        value >>= Shift;
        return (byte)(value < 0 ? 0 : value > 255 ? 255 : value);
    }
}
