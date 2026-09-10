using System.Buffers.Binary;

namespace LuminaMonitor.Formats;

/// <summary>
/// A UDIF checksum record (136 bytes): algorithm, digest width in bits and
/// a 128-byte digest area. Kept verbatim so a caller can verify an image
/// later; this reader never validates checksums itself.
/// </summary>
internal readonly struct UdifChecksum
{
    public const int Size = 136;

    /// <summary>0 = none, 2 = CRC-32 (the only value hdiutil emits in practice).</summary>
    public uint Type { get; }

    /// <summary>Digest width in bits (32 for CRC-32).</summary>
    public uint Bits { get; }

    /// <summary>The 32 big-endian words of the digest area, as raw bytes.</summary>
    public byte[] Data { get; }

    private UdifChecksum(uint type, uint bits, byte[] data)
    {
        Type = type;
        Bits = bits;
        Data = data;
    }

    public static UdifChecksum Read(ReadOnlySpan<byte> s)
        => new(BinaryPrimitives.ReadUInt32BigEndian(s),
               BinaryPrimitives.ReadUInt32BigEndian(s[4..]),
               s.Slice(8, 128).ToArray());

    /// <summary>First digest word, which is the whole digest for CRC-32.</summary>
    public uint FirstWord => BinaryPrimitives.ReadUInt32BigEndian(Data);
}

/// <summary>
/// The 512-byte 'koly' block that closes every UDIF image.
/// </summary>
/// <remarks>
/// A DMG is read from the end: the trailer says where the XML property list
/// describing the block map lives and where the data fork starts. Every
/// field is big-endian. Offsets below were checked against a real hdiutil
/// image: <see cref="ImageVariant"/> sits at byte 488 and
/// <see cref="SectorCount"/> at byte 492 (three reserved words fill 500-511);
/// reading the sector count at 496 instead yields garbage.
/// </remarks>
internal sealed class KolyTrailer
{
    public const int Size = 512;
    private const uint Magic = 0x6B6F6C79; // 'koly'

    public uint Version { get; private init; }
    public uint HeaderSize { get; private init; }
    public uint Flags { get; private init; }
    public long RunningDataForkOffset { get; private init; }

    /// <summary>Where the compressed run data starts in the file (0 for every image seen so far).</summary>
    public long DataForkOffset { get; private init; }
    public long DataForkLength { get; private init; }
    public long RsrcForkOffset { get; private init; }
    public long RsrcForkLength { get; private init; }
    public uint SegmentNumber { get; private init; }
    public uint SegmentCount { get; private init; }
    public byte[] SegmentId { get; private init; } = new byte[16];

    /// <summary>Checksum over the data fork bytes (compressed form).</summary>
    public UdifChecksum DataChecksum { get; private init; }
    public long XmlOffset { get; private init; }
    public long XmlLength { get; private init; }

    /// <summary>Checksum over the concatenated 'mish' checksums.</summary>
    public UdifChecksum MasterChecksum { get; private init; }
    public uint ImageVariant { get; private init; }

    /// <summary>Size of the virtual disk in 512-byte sectors.</summary>
    public long SectorCount { get; private init; }

    private KolyTrailer() { }

    /// <summary>Parses the trailer; <paramref name="trailer"/> must be the last 512 bytes of the file.</summary>
    public static KolyTrailer Parse(ReadOnlySpan<byte> trailer)
    {
        if (trailer.Length < Size)
            throw new InvalidDataException("UDIF trailer shorter than 512 bytes");
        if (BinaryPrimitives.ReadUInt32BigEndian(trailer) != Magic)
            throw new InvalidDataException("not a UDIF image: no 'koly' trailer");

        var t = new KolyTrailer
        {
            Version = U32(trailer, 4),
            HeaderSize = U32(trailer, 8),
            Flags = U32(trailer, 12),
            RunningDataForkOffset = U64(trailer, 16),
            DataForkOffset = U64(trailer, 24),
            DataForkLength = U64(trailer, 32),
            RsrcForkOffset = U64(trailer, 40),
            RsrcForkLength = U64(trailer, 48),
            SegmentNumber = U32(trailer, 56),
            SegmentCount = U32(trailer, 60),
            SegmentId = trailer.Slice(64, 16).ToArray(),
            DataChecksum = UdifChecksum.Read(trailer[80..]),   // 80..215
            XmlOffset = U64(trailer, 216),
            XmlLength = U64(trailer, 224),
            // 232..351: reserved
            MasterChecksum = UdifChecksum.Read(trailer[352..]), // 352..487
            ImageVariant = U32(trailer, 488),
            SectorCount = U64(trailer, 492),
        };

        if (t.HeaderSize != Size)
            throw new InvalidDataException($"unexpected UDIF trailer size {t.HeaderSize}");
        if (t.SegmentCount > 1)
            throw new NotSupportedException($"segmented UDIF image ({t.SegmentCount} segments) is not supported");
        return t;
    }

    private static uint U32(ReadOnlySpan<byte> s, int offset) => BinaryPrimitives.ReadUInt32BigEndian(s[offset..]);

    /// <summary>Offsets and lengths are u64 on disk; anything beyond long.MaxValue is corrupt for our purposes.</summary>
    private static long U64(ReadOnlySpan<byte> s, int offset)
    {
        ulong v = BinaryPrimitives.ReadUInt64BigEndian(s[offset..]);
        if (v > long.MaxValue)
            throw new InvalidDataException($"UDIF trailer field at {offset} out of range");
        return (long)v;
    }
}
