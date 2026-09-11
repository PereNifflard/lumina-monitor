namespace LuminaMonitor.Core.Media.Aac;

/// <summary>
/// <c>section_data()</c>: which Huffman codebook each scalefactor band was coded
/// with.
/// </summary>
/// <remarks>
/// <para><b>Syntax.</b> ISO/IEC 14496-3 §4.4.2.7 and Table 4.51, in the error
/// resilient form — <c>huffcb()</c> of the reference software's
/// <c>huffdec2.c</c>. A section is a four-bit codebook number followed by its
/// length in bands, and the length is read in five-bit instalments for a long
/// window (<c>LONG_SECT_BITS</c>): a value of 31, all ones, means "add 31 and
/// read another instalment". Sections follow one another until the bands below
/// <c>max_sfb</c> are covered.</para>
///
/// <para>The ER form differs from plain AAC only in that the escape loop stops
/// at <c>max_sfb</c> rather than at the total band count, which matters when the
/// last section ends exactly on an instalment boundary. An ELD frame is always
/// one long window with one group, so the grouping arithmetic of the reference
/// function collapses to nothing and is not reproduced here.</para>
///
/// <para><b>Above <c>max_sfb</c>.</b> Those bands are not transmitted and are
/// codebook 0, "all values zero" — the zero section the reference inserts by
/// hand. So the caller gets a codebook for every band of the frame, not only for
/// the transmitted ones, and nothing downstream has to know where the
/// transmission stopped.</para>
/// </remarks>
internal static class SectionData
{
    /// <summary>How many bits one instalment of a section length takes in a long window.</summary>
    private const int SectionLengthBits = 5;

    /// <summary>The codebook number that means "this band is all zeroes".</summary>
    public const int ZeroCodebook = 0;

    /// <summary>The codebook number that means "this band is noise at a transmitted energy".</summary>
    public const int NoiseCodebook = 13;

    /// <summary>The two codebook numbers that mean "this band is the other channel, scaled".</summary>
    public const int IntensityCodebookOutOfPhase = 14;

    /// <summary>The in-phase intensity codebook.</summary>
    public const int IntensityCodebook = 15;

    /// <summary>True for the codebooks that carry spectral values rather than a substitution.</summary>
    public static bool IsSpectral(int codebook) => codebook is >= 1 and <= 11;

    /// <summary>
    /// Reads the sections of one channel and writes one codebook number per band.
    /// </summary>
    /// <param name="reader">The bitstream, positioned on the first section.</param>
    /// <param name="maxSfb">How many bands the channel transmits.</param>
    /// <param name="codebooks">
    /// One slot per band of the frame; filled for every band, zero above
    /// <paramref name="maxSfb"/>.
    /// </param>
    /// <exception cref="AacBitstreamException">
    /// A section runs past <paramref name="maxSfb"/>, or the frame ends first.
    /// </exception>
    public static void Read(ref BitReader reader, int maxSfb, Span<byte> codebooks)
    {
        codebooks.Clear();
        if (maxSfb == 0)
            return;
        if (maxSfb > codebooks.Length)
            throw reader.Error($"max_sfb {maxSfb} pour {codebooks.Length} bandes");

        const int escape = (1 << SectionLengthBits) - 1;
        int band = 0;
        while (band < maxSfb)
        {
            int codebook = (int)reader.Read(4);
            if (codebook == 12)
                throw reader.Error("livre 12 dans section_data : reserve au livre des facteurs d'echelle");

            // The escape: an instalment of all ones adds its own value and is
            // followed by another. The loop's guard is the running band count
            // against max_sfb, which is the ER form's one difference from plain
            // AAC — it guards against tot_sfb there, and the two disagree
            // whenever a section ends exactly on an instalment boundary.
            int start = band;
            int length = (int)reader.Read(SectionLengthBits);
            while (length == escape && band < maxSfb)
            {
                band += escape;
                length = (int)reader.Read(SectionLengthBits);
            }
            band += length;
            if (band == start)
                throw reader.Error($"section de longueur nulle a la bande {start}");
            if (band > maxSfb)
                throw reader.Error(
                    $"section {codebook} de {band - start} bandes depuis {start}, au-dela de max_sfb {maxSfb}");

            for (int at = start; at < band; at++)
                codebooks[at] = (byte)codebook;
        }
    }
}
