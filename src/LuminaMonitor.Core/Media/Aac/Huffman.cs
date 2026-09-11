using LuminaMonitor.Core.Media.Aac.Tables;

namespace LuminaMonitor.Core.Media.Aac;

/// <summary>
/// One AAC Huffman codebook turned into something that can be read from a
/// bitstream.
/// </summary>
/// <remarks>
/// <para><b>Shape.</b> A binary tree, held in one <c>int</c> array: slots
/// <c>2n</c> and <c>2n+1</c> are node <c>n</c>'s branches for a zero and a one
/// bit; a positive slot is the next node, a negative slot is
/// <c>-(index + 1)</c>, the table index the codeword decodes to. Decoding is one
/// array read per bit, which for books whose codewords average nine or ten bits
/// is fast enough that the filterbank dominates a frame by two orders of
/// magnitude. A flat lookup table would be faster still and would cost two
/// megabytes for the nineteen-bit scalefactor book; the tree costs fifty
/// kilobytes for all twelve.</para>
///
/// <para><b>Why a tree is safe here.</b> The books are complete prefix codes —
/// <c>aac-tables-selftest</c> checks the Kraft sum is exactly 1 and that no
/// codeword prefixes another — so every path of the tree ends on a leaf and no
/// leaf is reachable two ways. <see cref="Build"/> asserts both while building,
/// because a table transcription error that got past the self-test would
/// otherwise show up as a decoder that reads a plausible wrong value.</para>
///
/// <para><b>Index, not value.</b> What a codeword carries is a table index, and
/// the values are the digits of that index in base <see cref="Modulus"/> offset
/// by <see cref="Offset"/> — ISO/IEC 14496-3 §4.6.3, and <c>unpack_idx()</c> of
/// the reference software's <c>huffdec3.c</c>. <see cref="Unpack"/> does that
/// arithmetic; the sign bits and escape sequences that follow belong to the
/// spectral syntax and live in <see cref="SpectralData"/>.</para>
/// </remarks>
internal sealed class HuffmanBook
{
    private static readonly HuffmanBook?[] Cache = new HuffmanBook?[13];
    private readonly int[] tree;

    private HuffmanBook(AacCodebook book)
    {
        Number = book.Index;
        Dimension = book.Dimension;
        Lav = book.Lav;
        Signed = book.Signed;
        Modulus = book.Signed ? (2 * book.Lav) + 1 : book.Lav + 1;
        Offset = book.Signed ? book.Lav : 0;
        tree = Build(book);
    }

    /// <summary>The codebook number the bitstream uses (12 is the scalefactor book).</summary>
    public int Number { get; }

    /// <summary>How many values one codeword carries.</summary>
    public int Dimension { get; }

    /// <summary>The largest absolute value a codeword can hold.</summary>
    public int Lav { get; }

    /// <summary>True when the codeword carries the signs itself.</summary>
    public bool Signed { get; }

    /// <summary>The base the table index is written in.</summary>
    public int Modulus { get; }

    /// <summary>What each digit of the table index is offset by.</summary>
    public int Offset { get; }

    /// <summary>The book that number names, built once and shared.</summary>
    public static HuffmanBook ByNumber(int codebook)
    {
        if (codebook is < 1 or > 12)
            throw new ArgumentOutOfRangeException(nameof(codebook), codebook, "Pas un livre de Huffman AAC.");
        return Cache[codebook] ??= new HuffmanBook(HuffmanTables.ByNumber(codebook));
    }

    /// <summary>Reads one codeword and returns its table index.</summary>
    /// <exception cref="AacBitstreamException">The frame ends inside the codeword.</exception>
    public int ReadIndex(ref BitReader reader)
    {
        int node = 0;
        while (true)
        {
            int slot = tree[(2 * node) + reader.ReadBit()];
            if (slot < 0)
                return -slot - 1;
            node = slot;
        }
    }

    /// <summary>
    /// Spreads a table index over <see cref="Dimension"/> values, most
    /// significant digit first.
    /// </summary>
    public void Unpack(int index, Span<int> values)
    {
        for (int i = Dimension - 1; i >= 0; i--)
        {
            values[i] = (index % Modulus) - Offset;
            index /= Modulus;
        }
    }

    /// <summary>
    /// Builds the tree, refusing anything that is not a complete prefix code.
    /// </summary>
    private static int[] Build(AacCodebook book)
    {
        // A complete prefix code over n leaves has n - 1 internal nodes, so two
        // slots per entry is always enough and usually generous.
        var tree = new int[2 * (book.Entries + 1)];
        int next = 1;
        for (int entry = 0; entry < book.Entries; entry++)
        {
            int length = book.Lengths[entry];
            uint codeword = book.Codewords[entry];
            int node = 0;
            for (int bit = length - 1; bit > 0; bit--)
            {
                int slot = (2 * node) + (int)((codeword >> bit) & 1);
                if (tree[slot] == 0)
                    tree[slot] = next++;
                else if (tree[slot] < 0)
                    throw new InvalidDataException(
                        $"Livre {book.Index} : le mot de code {entry} a pour prefixe un mot deja place.");
                node = tree[slot];
            }
            int leaf = (2 * node) + (int)(codeword & 1);
            if (tree[leaf] != 0)
                throw new InvalidDataException($"Livre {book.Index} : le mot de code {entry} est deja pris.");
            tree[leaf] = -(entry + 1);
        }

        // Every slot of every node built must lead somewhere: an unused branch
        // is a hole in the code, and a bitstream that walks into it would loop
        // here for ever rather than fail.
        for (int slot = 0; slot < 2 * next; slot++)
        {
            if (tree[slot] == 0)
                throw new InvalidDataException($"Livre {book.Index} : code incomplet, branche {slot} sans suite.");
        }
        return tree;
    }
}
