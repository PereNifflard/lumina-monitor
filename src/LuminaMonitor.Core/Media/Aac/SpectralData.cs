namespace LuminaMonitor.Core.Media.Aac;

/// <summary>
/// <c>spectral_data()</c>: the quantized spectrum itself.
/// </summary>
/// <remarks>
/// <para><b>Syntax.</b> ISO/IEC 14496-3 §4.4.2.9 and Table 4.54 —
/// <c>HuffSpecDecDefault()</c> and <c>HuffSpecKernelPure()</c> of the reference
/// software's <c>huffdec2.c</c>. Every band whose codebook carries spectral
/// values is read in tuples: quadruples for books 1 to 4, pairs for 5 to 11. All
/// band widths are multiples of four, so neither tuple ever straddles a band.</para>
///
/// <para><b>Three things per tuple, in this order.</b> The codeword, whose table
/// index spreads into the tuple's values; then, for a book that does not carry
/// its own signs, one sign bit per value that is not zero — a one meaning
/// negative, as in two's complement; then, for book 11 only, an escape sequence
/// for every value that came out at exactly ±16, the book's LAV. Getting the
/// order wrong is undetectable on a frame whose values happen to be small and
/// catastrophic on any other.</para>
///
/// <para><b>The escape.</b> A run of ones then a zero, the run counted from four,
/// then that many bits: the value is <c>2^n + offset</c>. The run is at most
/// eight ones, so <c>n</c> is at most twelve and the widest value the syntax can
/// carry is 8191 — the reference software's <c>lavInclEsc</c> for book 11.</para>
/// </remarks>
internal static class SpectralData
{
    /// <summary>The book number above which codewords carry pairs rather than quadruples.</summary>
    private const int LastQuadrupleBook = 4;

    /// <summary>What the escape run counts up from.</summary>
    private const int EscapeFirstLength = 4;

    /// <summary>The longest escape run the syntax allows, in ones.</summary>
    private const int EscapeMaxRun = 8;

    /// <summary>
    /// Reads the spectrum of one channel into quantized integers, one per line.
    /// </summary>
    /// <param name="reader">The bitstream, positioned on the first codeword.</param>
    /// <param name="codebooks">The codebook of each band, from <see cref="SectionData"/>.</param>
    /// <param name="bandOffsets">Where each band starts; <c>bandOffsets[^1]</c> is the frame length.</param>
    /// <param name="quantized">One slot per spectral line; zeroed first.</param>
    /// <exception cref="AacBitstreamException">The frame ends inside the spectrum.</exception>
    public static void Read(ref BitReader reader, ReadOnlySpan<byte> codebooks,
        ReadOnlySpan<ushort> bandOffsets, Span<int> quantized)
    {
        quantized.Clear();
        Span<int> tuple = stackalloc int[4];

        for (int band = 0; band < codebooks.Length; band++)
        {
            int codebook = codebooks[band];
            if (!SectionData.IsSpectral(codebook))
                continue;

            var book = HuffmanBook.ByNumber(codebook);
            int step = codebook <= LastQuadrupleBook ? 4 : 2;
            bool escapes = codebook == 11;
            int stop = bandOffsets[band + 1];
            for (int line = bandOffsets[band]; line < stop; line += step)
            {
                book.Unpack(book.ReadIndex(ref reader), tuple);
                if (!book.Signed)
                {
                    for (int i = 0; i < step; i++)
                    {
                        if (tuple[i] != 0 && reader.ReadBit() != 0)
                            tuple[i] = -tuple[i];
                    }
                }
                if (escapes)
                {
                    for (int i = 0; i < step; i++)
                        tuple[i] = ReadEscape(ref reader, tuple[i], book.Lav);
                }
                for (int i = 0; i < step; i++)
                    quantized[line + i] = tuple[i];
            }
        }
    }

    /// <summary>
    /// Widens a value that saturated the escape book's LAV; anything else is
    /// returned untouched.
    /// </summary>
    private static int ReadEscape(ref BitReader reader, int value, int lav)
    {
        if (value != lav && value != -lav)
            return value;

        int length = EscapeFirstLength;
        while (reader.ReadBit() != 0)
        {
            length++;
            if (length > EscapeFirstLength + EscapeMaxRun)
                throw reader.Error($"prefixe d'echappement de plus de {EscapeMaxRun} bits");
        }
        int widened = (int)reader.Read(length) + (1 << length);
        return value < 0 ? -widened : widened;
    }
}
