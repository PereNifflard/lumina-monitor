namespace LuminaMonitor.Core.Media.Aac;

/// <summary>
/// The two stereo tools: mid/side, and intensity.
/// </summary>
/// <remarks>
/// <para><b>Order matters.</b> ISO/IEC 14496-3 §4.6.8, and the sequence the
/// reference decoder runs in <c>decoder_tf.c</c>: the mask is mapped, then
/// mid/side is undone, then noise substitution fills its bands, then intensity
/// copies the left channel into the right. Intensity reads a left channel that
/// mid/side has already put back, so swapping the two gives a stereo image that
/// is wrong in exactly the bands the encoder cared most about.</para>
///
/// <para><b>The mask has three states.</b> Two bits: 0 means no mid/side
/// anywhere, 1 means one bit per transmitted band, 2 means mid/side over the
/// whole spectrum with no bits sent at all. The third state is the one a reader
/// that treats the field as a flag gets wrong, and a whole-spectrum frame decoded
/// without it comes out as a very wide, very wrong stereo image.</para>
///
/// <para><b>Why the mask is mapped.</b> A band can be both intensity-coded and
/// mid/side-flagged, and that combination does not mean "do both": it means the
/// intensity is out of phase. So <see cref="MapMask"/> moves that information
/// from the mask into the right channel's codebook — 15 becomes 14 and 14 becomes
/// 15 — and clears the mask bit so mid/side leaves the band alone. The same
/// trick marks a noise band as correlated between the two channels, which is what
/// <see cref="Pns"/> reads. This is <c>map_mask()</c> of the reference
/// software's <c>stereo.c</c>; the noise marker there is "codebook + 100", and
/// <see cref="Pns.CorrelatedNoiseCodebook"/> is the same idea named.</para>
/// </remarks>
internal static class Stereo
{
    /// <summary>The mask state that means "mid/side everywhere, no bits transmitted".</summary>
    public const int MaskWholeSpectrum = 2;

    /// <summary>
    /// Reads <c>ms_mask_present</c> and the mask itself.
    /// </summary>
    /// <param name="reader">The bitstream, positioned on the two mask bits.</param>
    /// <param name="maxSfb">How many bands the pair transmits.</param>
    /// <param name="mask">One slot per band of the frame; filled for every band.</param>
    /// <returns>The two-bit state, so the caller can tell 2 from 1.</returns>
    public static int ReadMask(ref BitReader reader, int maxSfb, Span<byte> mask)
    {
        mask.Clear();
        int state = (int)reader.Read(2);
        if (state == 3)
            throw reader.Error("ms_mask_present = 3, reserve");
        if (state == 0)
            return 0;
        if (state == MaskWholeSpectrum)
        {
            mask.Fill(1);
            return state;
        }
        for (int band = 0; band < maxSfb; band++)
            mask[band] = (byte)reader.ReadBit();
        return state;
    }

    /// <summary>
    /// Moves the mask bit of every substitution band into the right channel's
    /// codebook, and clears it there.
    /// </summary>
    public static void MapMask(Span<byte> rightCodebooks, Span<byte> mask, int maskState)
    {
        for (int band = 0; band < rightCodebooks.Length; band++)
        {
            if (mask[band] == 0)
                continue;
            switch (rightCodebooks[band])
            {
                case SectionData.IntensityCodebook:
                    if (maskState == 1)
                        rightCodebooks[band] = SectionData.IntensityCodebookOutOfPhase;
                    mask[band] = 0;
                    break;
                case SectionData.IntensityCodebookOutOfPhase:
                    if (maskState == 1)
                        rightCodebooks[band] = SectionData.IntensityCodebook;
                    mask[band] = 0;
                    break;
                case SectionData.NoiseCodebook:
                    rightCodebooks[band] = Pns.CorrelatedNoiseCodebook;
                    mask[band] = 0;
                    break;
                default:
                    break;
            }
        }
    }

    /// <summary>
    /// Undoes mid/side over the bands the mask marks: left becomes mid + side,
    /// right becomes mid − side.
    /// </summary>
    public static void MiddleSide(Span<double> left, Span<double> right, ReadOnlySpan<byte> mask,
        ReadOnlySpan<ushort> bandOffsets)
    {
        for (int band = 0; band < mask.Length; band++)
        {
            if (mask[band] == 0)
                continue;
            int stop = bandOffsets[band + 1];
            for (int line = bandOffsets[band]; line < stop; line++)
            {
                double mid = left[line];
                double side = right[line];
                left[line] = mid + side;
                right[line] = mid - side;
            }
        }
    }

    /// <summary>
    /// Fills the right channel's intensity bands from the left channel, scaled by
    /// the transmitted position and signed by which of the two books was used.
    /// </summary>
    public static void Intensity(ReadOnlySpan<double> left, Span<double> right,
        ReadOnlySpan<byte> rightCodebooks, ReadOnlySpan<short> rightFactors, ReadOnlySpan<ushort> bandOffsets)
    {
        for (int band = 0; band < rightCodebooks.Length; band++)
        {
            int codebook = rightCodebooks[band];
            if (codebook is not (SectionData.IntensityCodebook or SectionData.IntensityCodebookOutOfPhase))
                continue;

            double scale = codebook == SectionData.IntensityCodebook
                ? Math.Pow(0.5, 0.25 * rightFactors[band])
                : -Math.Pow(0.5, 0.25 * rightFactors[band]);
            int stop = bandOffsets[band + 1];
            for (int line = bandOffsets[band]; line < stop; line++)
                right[line] = left[line] * scale;
        }
    }
}
