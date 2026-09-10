// Clean-room implementation from Apple TN1150 "HFS Plus Volume Format", sections "Unicode Subtleties",
// "Canonical Decomposition" and "Case-Insensitive String Comparison Algorithm" (FastUnicodeCompare).
//
// HFS+ ('H+', and HFSX whose catalog keyCompareType is 0xCF) orders names case-insensitively: each UTF-16
// unit goes through Apple's lower-case table; units folded to 0 are ignorable (skipped); U+0000 folds to
// 0xFFFF and therefore sorts after everything; when one string is a prefix of the other the shorter one
// sorts first. HFSX with keyCompareType 0xBC compares the raw UTF-16 units as unsigned integers, with no
// folding and no ignorable characters.
//
// TN1150 publishes the algorithm and the *shape* of the table but not its data (it lives in a separate
// download that this clean-room code does not reproduce). Fold() therefore approximates the table:
//   - exact for U+0000, the ignorable characters, ASCII and Latin-1 (the only ranges that matter for
//     Apple's developer disk images);
//   - the blocks Apple's table covers (Latin Extended-A, Greek, Cyrillic, Armenian, Georgian, letterlike
//     symbols / Roman numerals, full-width forms) use .NET invariant lower-casing, which may differ for
//     code points whose case pairs entered Unicode after the table was frozen;
//   - every other block (Latin Extended Additional, CJK, Hangul jamo, ...) is left unchanged, as in the table.
// The table is a LOWER-case table, so folding must go to lower case: folding to upper case would place
// '_', '[', '^' and '`' (0x5B..0x60) on the wrong side of the letters and break B-tree navigation.
// Catalog.Find() compensates for a residual ordering mismatch by scanning the directory when a B-tree
// search for a non-ASCII name fails, so lookups stay correct; only the listing order could differ.
using System.Text;

namespace LuminaMonitor.Formats.Hfs;

internal static class HfsName
{
    public static int Compare(ReadOnlySpan<char> a, ReadOnlySpan<char> b, bool caseSensitive) =>
        caseSensitive ? CompareBinary(a, b) : CompareFolded(a, b);

    /// <summary>HFSX case-sensitive ordering: unsigned UTF-16 code units, shorter prefix first.</summary>
    public static int CompareBinary(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            if (a[i] != b[i]) return a[i] < b[i] ? -1 : 1;
        }
        return a.Length == b.Length ? 0 : (a.Length < b.Length ? -1 : 1);
    }

    /// <summary>HFS+ case-insensitive ordering (FastUnicodeCompare semantics).</summary>
    public static int CompareFolded(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        int i = 0, j = 0;
        while (true)
        {
            ushort ca = NextSignificant(a, ref i);
            ushort cb = NextSignificant(b, ref j);
            if (ca != cb) return ca < cb ? -1 : 1;
            if (ca == 0) return 0; // both strings exhausted at the same time
        }
    }

    private static ushort NextSignificant(ReadOnlySpan<char> s, ref int index)
    {
        while (index < s.Length)
        {
            ushort folded = Fold(s[index++]);
            if (folded != 0) return folded;
        }
        return 0;
    }

    /// <summary>Folds one UTF-16 unit; 0 means "ignorable".</summary>
    public static ushort Fold(char c)
    {
        if (c == '\0') return 0xFFFF;
        // Ignorable formatting characters: ZWNJ..RLM, LRE..RLO, U+206A..U+206F, ZWNBSP.
        if ((c >= '\u200C' && c <= '\u200F') || (c >= '\u202A' && c <= '\u202E') ||
            (c >= '\u206A' && c <= '\u206F') || c == '\uFEFF')
            return 0;
        // Georgian capitals (Khutsuri) fold to Mkhedruli in Apple's table, not to Nuskhuri as in modern Unicode.
        if (c >= '\u10A0' && c <= '\u10C5') return (ushort)(c + 0x30);
        switch (c >> 8)
        {
            case 0x00: case 0x01: case 0x03: case 0x04: case 0x05: case 0x21: case 0xFF:
                return char.ToLowerInvariant(c);
            default:
                return c;
        }
    }

    /// <summary>
    /// Converts a name to the form stored on disk: canonical decomposition (NFD), except that
    /// U+2000..U+2FFF and U+F900..U+FAFF are left as they are (TN1150 "Canonical Decomposition").
    /// </summary>
    public static string ToDiskForm(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        bool ascii = true;
        foreach (char c in name)
        {
            if (c >= 0x80) { ascii = false; break; }
        }
        if (ascii) return name;

        var result = new StringBuilder(name.Length + 8);
        int runStart = 0;
        for (int i = 0; i <= name.Length; i++)
        {
            bool boundary = i == name.Length || IsLeftUndecomposed(name[i]);
            if (!boundary) continue;
            if (i > runStart) result.Append(Decompose(name.Substring(runStart, i - runStart)));
            if (i < name.Length) result.Append(name[i]);
            runStart = i + 1;
        }
        return result.ToString();
    }

    private static bool IsLeftUndecomposed(char c) =>
        (c >= '\u2000' && c <= '\u2FFF') || (c >= '\uF900' && c <= '\uFAFF');

    private static string Decompose(string run)
    {
        try { return run.Normalize(NormalizationForm.FormD); }
        catch (ArgumentException) { return run; } // invalid surrogates: keep as is
    }
}
