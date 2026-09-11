namespace LuminaMonitor.Core.Media.Aac;

/// <summary>
/// <c>scale_factor_data()</c>: one number per transmitted band, and it means
/// three different things.
/// </summary>
/// <remarks>
/// <para><b>Syntax.</b> ISO/IEC 14496-3 §4.4.2.8 and Table 4.52 —
/// <c>hufffac()</c> of the reference software's <c>huffdec2.c</c>. Every band
/// whose codebook is not zero carries one codeword of the scalefactor book, and
/// what that codeword means depends on the codebook:</para>
///
/// <list type="bullet">
/// <item><description>a spectral book (1 to 11): a scalefactor, differential
/// against the previous scalefactor, the first one differential against
/// <c>global_gain</c>;</description></item>
/// <item><description>the noise book (13): a noise energy, differential against
/// the previous one, the first one differential against
/// <c>global_gain − 90</c> — and the <em>first</em> noise band of the channel
/// does not use the book at all but nine raw bits offset by 256;</description></item>
/// <item><description>an intensity book (14 or 15): an intensity position,
/// differential against zero.</description></item>
/// </list>
///
/// <para>The three run as three independent chains: a scalefactor does not shift
/// the noise energy and neither shifts the intensity position. The chain value
/// is the codeword's table index minus 60, the scalefactor book's LAV, so 60
/// means "same as the last one".</para>
///
/// <para>A scalefactor outside 0 to 255 is refused rather than clamped. The
/// reference clamps, but only under its error-protection flag; with resilience
/// off it exits, and an out-of-range scalefactor here means the parse has lost
/// the thread, which is worth knowing about at the bit it happened.</para>
/// </remarks>
internal static class ScaleFactors
{
    /// <summary>What a scalefactor codeword's table index is offset by.</summary>
    private const int DifferenceOffset = 60;

    /// <summary>What the first noise energy of a channel is offset from <c>global_gain</c> by.</summary>
    private const int NoiseOffset = 90;

    /// <summary>How many raw bits the first noise energy of a channel takes.</summary>
    private const int NoisePcmBits = 9;

    /// <summary>What those raw bits are offset by.</summary>
    private const int NoisePcmOffset = 1 << (NoisePcmBits - 1);

    /// <summary>The largest scalefactor the dequantizer's exponent table covers.</summary>
    private const int MaxScaleFactor = 255;

    /// <summary>
    /// Reads one number per band, in the units its codebook implies.
    /// </summary>
    /// <param name="reader">The bitstream, positioned on the first codeword.</param>
    /// <param name="codebooks">The codebook of each band, from <see cref="SectionData"/>.</param>
    /// <param name="globalGain">The eight-bit gain the channel opened with.</param>
    /// <param name="factors">One slot per band; zero where the band carries nothing.</param>
    /// <exception cref="AacBitstreamException">A scalefactor leaves its range, or the frame ends.</exception>
    public static void Read(ref BitReader reader, ReadOnlySpan<byte> codebooks, int globalGain, Span<short> factors)
    {
        factors.Clear();
        var book = HuffmanBook.ByNumber(12);
        int scaleFactor = globalGain;
        int intensityPosition = 0;
        int noiseEnergy = globalGain - NoiseOffset;
        bool firstNoiseBand = true;

        for (int band = 0; band < codebooks.Length; band++)
        {
            int codebook = codebooks[band];
            if (codebook == SectionData.ZeroCodebook)
                continue;

            if (SectionData.IsSpectral(codebook))
            {
                scaleFactor += book.ReadIndex(ref reader) - DifferenceOffset;
                if (scaleFactor is < 0 or > MaxScaleFactor)
                    throw reader.Error($"facteur d'echelle {scaleFactor} hors de 0..{MaxScaleFactor} a la bande {band}");
                factors[band] = (short)scaleFactor;
            }
            else if (codebook == SectionData.NoiseCodebook)
            {
                if (firstNoiseBand)
                {
                    firstNoiseBand = false;
                    noiseEnergy += (int)reader.Read(NoisePcmBits) - NoisePcmOffset;
                }
                else
                {
                    noiseEnergy += book.ReadIndex(ref reader) - DifferenceOffset;
                }
                factors[band] = (short)Math.Clamp(noiseEnergy, short.MinValue, short.MaxValue);
            }
            else
            {
                // 14 and 15: an intensity position, which is an exponent like the
                // others but is not bounded the same way — it is read against
                // zero and may legitimately be negative.
                intensityPosition += book.ReadIndex(ref reader) - DifferenceOffset;
                factors[band] = (short)Math.Clamp(intensityPosition, short.MinValue, short.MaxValue);
            }
        }
    }
}
