namespace LuminaMonitor.Core.Media.Aac.Tables;

/// <summary>
/// One AAC Huffman codebook: what its codewords mean, not just what they are.
/// </summary>
/// <param name="Index">The codebook number as the bitstream names it (1 to 11, and 12 for the scalefactor book).</param>
/// <param name="Dimension">How many spectral values one codeword carries (4 or 2; 1 for the scalefactor book).</param>
/// <param name="Lav">The largest absolute value a codeword can hold — the "LAV" of the standard.</param>
/// <param name="Signed">
/// True when the codeword already carries the signs, false when a sign bit
/// follows the codeword for every non-zero value it decoded.
/// </param>
/// <param name="MaxCodewordBits">The longest codeword, as the reference software declares it.</param>
/// <param name="Lengths">Codeword length in bits, indexed by table index.</param>
/// <param name="Codewords">Codeword values, right aligned in <paramref name="Lengths"/> bits.</param>
/// <remarks>
/// The table index is not the decoded value: for an unsigned book of dimension
/// <c>d</c> the index is the base-(LAV+1) number formed by the absolute values,
/// most significant first; for a signed book it is the base-(2·LAV+1) number
/// formed by the values offset by LAV. That arithmetic is decoder logic and
/// lives with the decoder, not here — this folder holds data only.
/// </remarks>
internal readonly record struct AacCodebook(int Index, int Dimension, int Lav, bool Signed,
    int MaxCodewordBits, byte[] Lengths, uint[] Codewords)
{
    /// <summary>How many codewords the book has.</summary>
    public int Entries => Lengths.Length;

    /// <summary>How many codewords the dimension and LAV say it must have.</summary>
    /// <remarks>
    /// (2·LAV+1)^dimension for a signed book, (LAV+1)^dimension for an unsigned
    /// one. The two must agree with <see cref="Entries"/>, which is one of the
    /// checks <c>aac-tables-selftest</c> runs.
    /// </remarks>
    public int ExpectedEntries
    {
        get
        {
            int alphabet = Signed ? 2 * Lav + 1 : Lav + 1;
            int total = 1;
            for (int i = 0; i < Dimension; i++)
                total *= alphabet;
            return total;
        }
    }
}

/// <summary>
/// The Huffman codebooks an AAC-ELD stream needs: eleven spectral books and the
/// scalefactor book.
/// </summary>
/// <remarks>
/// <para>The codewords themselves are in <see cref="HuffmanCodebooks1To6"/>,
/// <see cref="HuffmanCodebooks7To11"/> and
/// <see cref="HuffmanScalefactorBook"/>; this file only says what each book is.
/// Source and licence: <see cref="TablesProvenance"/> — the dimension, LAV and
/// sign of each book are the arguments of the <c>hufftab()</c> calls in
/// <c>huffinit.c</c>, the sign flags being the <c>HUFnSGN</c> constants of
/// <c>interface.h</c>.</para>
///
/// <para>Codebook 0 is not a book: it means "this whole section is zero". Books
/// 12 to 15 are not books either (11 is the escape book and covers everything
/// above LAV 16), and 13 to 15 only appear in other object types. The
/// resilience books — RVLC and the escape-scalefactor book — are absent
/// deliberately: the phone's ELDSpecificConfig sets all three resilience flags
/// to zero, so a stream that used them would already be a stream this decoder
/// refuses.</para>
/// </remarks>
internal static class HuffmanTables
{
    /// <summary>The codebook number the bitstream uses for "all values zero".</summary>
    public const int ZeroCodebook = 0;

    /// <summary>The codebook number of the escape book, whose values may exceed its LAV.</summary>
    public const int EscapeCodebook = 11;

    /// <summary>What the escape book's LAV becomes once escape sequences are read.</summary>
    /// <remarks>
    /// 8191, the reference software's <c>lavInclEsc</c> for book 11: a value of
    /// 16 in that book is followed by an escape sequence, and the widest one the
    /// syntax allows lands here.
    /// </remarks>
    public const int EscapeLavIncludingEscapes = 8191;

    /// <summary>The offset the scalefactor book's index is read against.</summary>
    /// <remarks>Index 60 means "no change"; the difference is the index minus this.</remarks>
    public const int ScalefactorZeroIndex = 60;

    /// <summary>The eleven spectral codebooks, at their bitstream numbers (index 0 is unused).</summary>
    public static readonly AacCodebook[] Spectral =
    [
        default,
        new(1, 4, 1, true, 11, HuffmanCodebooks1To6.Book1Lengths, HuffmanCodebooks1To6.Book1Codewords),
        new(2, 4, 1, true, 9, HuffmanCodebooks1To6.Book2Lengths, HuffmanCodebooks1To6.Book2Codewords),
        new(3, 4, 2, false, 16, HuffmanCodebooks1To6.Book3Lengths, HuffmanCodebooks1To6.Book3Codewords),
        new(4, 4, 2, false, 12, HuffmanCodebooks1To6.Book4Lengths, HuffmanCodebooks1To6.Book4Codewords),
        new(5, 2, 4, true, 13, HuffmanCodebooks1To6.Book5Lengths, HuffmanCodebooks1To6.Book5Codewords),
        new(6, 2, 4, true, 11, HuffmanCodebooks1To6.Book6Lengths, HuffmanCodebooks1To6.Book6Codewords),
        new(7, 2, 7, false, 12, HuffmanCodebooks7To11.Book7Lengths, HuffmanCodebooks7To11.Book7Codewords),
        new(8, 2, 7, false, 10, HuffmanCodebooks7To11.Book8Lengths, HuffmanCodebooks7To11.Book8Codewords),
        new(9, 2, 12, false, 15, HuffmanCodebooks7To11.Book9Lengths, HuffmanCodebooks7To11.Book9Codewords),
        new(10, 2, 12, false, 12, HuffmanCodebooks7To11.Book10Lengths, HuffmanCodebooks7To11.Book10Codewords),
        new(11, 2, 16, false, 12, HuffmanCodebooks7To11.Book11Lengths, HuffmanCodebooks7To11.Book11Codewords),
    ];

    /// <summary>The scalefactor codebook: one differential scalefactor per codeword.</summary>
    public static readonly AacCodebook Scalefactor =
        new(12, 1, 60, true, 19, HuffmanScalefactorBook.ScalefactorLengths,
            HuffmanScalefactorBook.ScalefactorCodewords);

    /// <summary>The book the bitstream's codebook number names.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The number is not a book this decoder knows.</exception>
    public static AacCodebook ByNumber(int codebook) => codebook switch
    {
        >= 1 and <= 11 => Spectral[codebook],
        12 => Scalefactor,
        _ => throw new ArgumentOutOfRangeException(nameof(codebook), codebook, "Pas un livre de Huffman AAC."),
    };
}
