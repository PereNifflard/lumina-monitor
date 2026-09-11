namespace LuminaMonitor.Core.Media.Aac;

/// <summary>
/// Inverse quantization: quantized integers back to spectral coefficients.
/// </summary>
/// <remarks>
/// <para><b>The two steps.</b> ISO/IEC 14496-3 §4.6.2 and §4.6.11 — the
/// reference software's <c>esc_iquant()</c> and the rescaling loop at the end of
/// <c>HuffSpecFrame()</c>, in <c>huffdec2.c</c>. First the companding, the same
/// for every line: <c>sign(q)·|q|^(4/3)</c>. Then the band's own gain,
/// <c>2^(0.25·(scalefactor − 100))</c>, where the 100 is the offset that keeps
/// <c>global_gain</c> positive on the wire.</para>
///
/// <para><b>Two tables.</b> <c>|q|^(4/3)</c> for every value the syntax can
/// carry, which is 0 to 8191, and <c>2^(0.25·f)</c> for every exponent a legal
/// scalefactor produces. So a frame costs one multiply and two array reads per
/// line, and no call to <c>pow</c> at all. The reference builds the same two
/// tables and falls back to <c>pow</c> past them; here the tables cover the whole
/// legal range and the fallback would be dead code, so a value outside it is a
/// bug and is left to throw.</para>
///
/// <para><b>Only the spectral bands.</b> Bands coded as noise or as intensity
/// carry a number in the scalefactor slot that is not a scalefactor — an energy
/// or a stereo position — and their lines are written later, by
/// <see cref="Pns"/> and <see cref="Stereo"/>. Their lines are zero here and
/// scaling them would be harmless arithmetic on zeroes, except that an intensity
/// position of 200 would raise two to the fiftieth power for nothing. So the
/// gain is applied where it means something and nowhere else.</para>
/// </remarks>
internal static class Dequantizer
{
    /// <summary>The largest magnitude the escape syntax can carry.</summary>
    private const int MaxQuantized = 8191;

    /// <summary>What a scalefactor is offset by before it becomes an exponent.</summary>
    private const int ScaleFactorOffset = 100;

    /// <summary>The largest scalefactor the syntax allows.</summary>
    private const int MaxScaleFactor = 255;

    private static readonly double[] Companded = BuildCompanded();
    private static readonly double[] Gains = BuildGains();

    /// <summary>
    /// Turns one channel's quantized lines into scaled spectral coefficients.
    /// </summary>
    /// <param name="quantized">The lines as <see cref="SpectralData"/> read them.</param>
    /// <param name="codebooks">The codebook of each band.</param>
    /// <param name="factors">The scalefactor of each band.</param>
    /// <param name="bandOffsets">Where each band starts; the last entry is the frame length.</param>
    /// <param name="spectrum">The coefficients; cleared first, so noise and intensity bands start at zero.</param>
    public static void Dequantize(ReadOnlySpan<int> quantized, ReadOnlySpan<byte> codebooks,
        ReadOnlySpan<short> factors, ReadOnlySpan<ushort> bandOffsets, Span<double> spectrum)
    {
        spectrum.Clear();
        for (int band = 0; band < codebooks.Length; band++)
        {
            if (!SectionData.IsSpectral(codebooks[band]))
                continue;
            double gain = Gains[factors[band]];
            int stop = bandOffsets[band + 1];
            for (int line = bandOffsets[band]; line < stop; line++)
            {
                int value = quantized[line];
                spectrum[line] = value >= 0
                    ? Companded[value] * gain
                    : -Companded[-value] * gain;
            }
        }
    }

    /// <summary><c>|q|^(4/3)</c> for every magnitude the syntax can carry.</summary>
    private static double[] BuildCompanded()
    {
        var table = new double[MaxQuantized + 1];
        for (int value = 1; value <= MaxQuantized; value++)
            table[value] = Math.Pow(value, 4.0 / 3.0);
        return table;
    }

    /// <summary><c>2^(0.25·(f − 100))</c> for every legal scalefactor.</summary>
    private static double[] BuildGains()
    {
        var table = new double[MaxScaleFactor + 1];
        for (int factor = 0; factor <= MaxScaleFactor; factor++)
            table[factor] = Math.Pow(2.0, 0.25 * (factor - ScaleFactorOffset));
        return table;
    }
}
