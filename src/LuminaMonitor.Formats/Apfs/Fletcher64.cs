using System.Buffers.Binary;

namespace LuminaMonitor.Formats.Apfs;

/// <summary>
/// Fletcher-64 as used by the <c>o_cksum</c> field of every APFS object
/// (Apple File System Reference, 2020-06-22, "Objects" → obj_phys_t).
/// </summary>
/// <remarks>
/// The two running sums are taken over the object's 32-bit little-endian
/// words that FOLLOW the 8-byte checksum field, modulo 2^32 - 1. The stored
/// value is the pair of check words — c1 in the low 32 bits, c2 in the high
/// 32 bits — chosen so that continuing the same sums over (c1, c2) after the
/// data yields (0, 0): the classic Fletcher "checksum of zero" construction.
/// Written from the algorithm's definition; no implementation was consulted.
/// </remarks>
internal static class Fletcher64
{
    private const ulong Mod = 0xFFFFFFFFUL;

    /// <summary>
    /// Computes the checksum word pair for <paramref name="data"/>, which must
    /// be the object WITHOUT its first 8 bytes and a multiple of 4 bytes long.
    /// </summary>
    public static ulong Compute(ReadOnlySpan<byte> data)
    {
        Sums(data, out ulong sum1, out ulong sum2);
        ulong c1 = Mod - ((sum1 + sum2) % Mod);
        ulong c2 = Mod - ((sum1 + c1) % Mod);
        return (c2 << 32) | c1;
    }

    /// <summary>
    /// True when the checksum stored in the first 8 bytes of
    /// <paramref name="block"/> matches the rest of the block. The check is
    /// done on the defining congruences rather than by byte-comparing a
    /// recomputed value, so both encodings of "zero" modulo 2^32 - 1
    /// (0 and 0xFFFFFFFF) are accepted.
    /// </summary>
    public static bool Verify(ReadOnlySpan<byte> block)
    {
        if (block.Length < 12 || block.Length % 4 != 0)
            return false;
        ulong stored = BinaryPrimitives.ReadUInt64LittleEndian(block);
        ulong c1 = stored & Mod, c2 = stored >> 32;
        Sums(block[8..], out ulong sum1, out ulong sum2);
        return (sum1 + sum2 + c1) % Mod == 0 && (sum1 + c1 + c2) % Mod == 0;
    }

    private static void Sums(ReadOnlySpan<byte> data, out ulong sum1, out ulong sum2)
    {
        if (data.Length % 4 != 0)
            throw new ArgumentException("la longueur doit être un multiple de 4", nameof(data));
        sum1 = 0;
        sum2 = 0;
        for (int i = 0; i < data.Length; i += 4)
        {
            sum1 = (sum1 + BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i, 4))) % Mod;
            sum2 = (sum2 + sum1) % Mod;
        }
    }
}
