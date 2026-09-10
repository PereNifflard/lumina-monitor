using System.Buffers.Binary;
using System.Globalization;

namespace LuminaMonitor.Formats;

/// <summary>How the sectors of one block run are stored in the data fork.</summary>
internal enum BlkxRunType : uint
{
    ZeroFill = 0x00000000,
    Raw = 0x00000001,
    Ignore = 0x00000002,
    Adc = 0x80000004,
    Zlib = 0x80000005,
    Bzip2 = 0x80000006,
    Lzfse = 0x80000007,
    Lzma = 0x80000008,
    Comment = 0x7FFFFFFE,
    Terminator = 0xFFFFFFFF,
}

/// <summary>
/// One run of a 'mish' block map. <see cref="SectorNumber"/> is relative to
/// the owning partition's first sector and <see cref="CompressedOffset"/> is
/// relative to the partition's data offset inside the data fork; see
/// <see cref="BlkxTable"/> for the resolution rule.
/// </summary>
internal readonly record struct BlkxRun(
    BlkxRunType Type,
    long SectorNumber,
    long SectorCount,
    long CompressedOffset,
    long CompressedLength)
{
    /// <summary>True for runs that occupy sectors of the virtual disk (comments and the terminator do not).</summary>
    public bool CoversSectors => SectorCount > 0 && Type is not (BlkxRunType.Comment or BlkxRunType.Terminator);
}

/// <summary>One blkx resource: a partition (or metadata region) and its runs.</summary>
internal sealed class BlkxPartition
{
    public required string Name { get; init; }
    public required int Id { get; init; }

    /// <summary>First sector of this partition on the virtual disk.</summary>
    public required long SectorNumber { get; init; }
    public required long SectorCount { get; init; }

    /// <summary>Base, inside the data fork, of every run's <see cref="BlkxRun.CompressedOffset"/>.</summary>
    public required long DataOffset { get; init; }
    public required uint BlockDescriptors { get; init; }
    public required UdifChecksum Checksum { get; init; }
    public required IReadOnlyList<BlkxRun> Runs { get; init; }

    public override string ToString() => $"{Name} [{SectorNumber}, +{SectorCount}) runs={Runs.Count}";
}

/// <summary>
/// The block map of a UDIF image: the 'blkx' entries of the resource fork
/// dictionary in the XML plist, each carrying a big-endian 'mish' record.
/// </summary>
/// <remarks>
/// Layout of a 'mish' record: signature (0), version (4), sectorNumber u64
/// (8), sectorCount u64 (16), dataOffset u64 (24), buffersNeeded u32 (32),
/// blockDescriptors u32 (36), six reserved words (40), a 136-byte checksum
/// (64), the run count u32 (200) and then 40-byte runs from offset 204.
/// <para>
/// Where a run's bytes live: the published field descriptions call
/// dataOffset "offset in the data fork of the start of this blkx's data"
/// and compressedOffset "start of the chunk in the data fork", which reads
/// as absolute = dataForkOffset + mish.dataOffset + run.compressedOffset.
/// Every image met so far has dataForkOffset = 0 and dataOffset = 0, so
/// compressedOffset is then simply a file offset; the additive rule keeps the
/// rare images whose partitions carry a non-zero dataOffset readable too.
/// </para>
/// </remarks>
internal sealed class BlkxTable
{
    private const uint MishMagic = 0x6D697368; // 'mish'
    private const int MishHeaderSize = 204;
    private const int RunSize = 40;

    public IReadOnlyList<BlkxPartition> Partitions { get; }

    private BlkxTable(IReadOnlyList<BlkxPartition> partitions) => Partitions = partitions;

    /// <summary>Parses the plist found at the trailer's xmlOffset.</summary>
    public static BlkxTable Parse(byte[] xml)
    {
        var root = PlistReader.Parse(xml);
        var blkx = PlistReader.Get(PlistReader.Get(root, "resource-fork"), "blkx") as List<object?>
            ?? throw new InvalidDataException("UDIF plist has no resource-fork/blkx array");

        var partitions = new List<BlkxPartition>(blkx.Count);
        foreach (var entry in blkx)
        {
            string name = PlistReader.Get(entry, "Name") as string
                ?? PlistReader.Get(entry, "CFName") as string
                ?? $"blkx #{partitions.Count}";
            int id = PlistReader.Get(entry, "ID") is string s
                && int.TryParse(s.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int parsed)
                ? parsed : partitions.Count;
            var data = PlistReader.Get(entry, "Data") as byte[]
                ?? throw new InvalidDataException($"blkx entry '{name}' has no Data");
            partitions.Add(ParseMish(name, id, data));
        }
        return new BlkxTable(partitions);
    }

    /// <summary>Decodes one 'mish' record (the Data blob of a blkx entry).</summary>
    public static BlkxPartition ParseMish(string name, int id, ReadOnlySpan<byte> mish)
    {
        if (mish.Length < MishHeaderSize || BinaryPrimitives.ReadUInt32BigEndian(mish) != MishMagic)
            throw new InvalidDataException($"blkx entry '{name}' is not a 'mish' block");

        uint runCount = BinaryPrimitives.ReadUInt32BigEndian(mish[200..]);
        if (runCount > (uint)((mish.Length - MishHeaderSize) / RunSize))
            throw new InvalidDataException($"blkx entry '{name}' announces {runCount} runs but is too short");

        var runs = new BlkxRun[runCount];
        for (int i = 0; i < runs.Length; i++)
        {
            var r = mish.Slice(MishHeaderSize + i * RunSize, RunSize);
            runs[i] = new BlkxRun(
                (BlkxRunType)BinaryPrimitives.ReadUInt32BigEndian(r),
                U64(r, 8, name),
                U64(r, 16, name),
                U64(r, 24, name),
                U64(r, 32, name));
        }

        return new BlkxPartition
        {
            Name = name,
            Id = id,
            SectorNumber = U64(mish, 8, name),
            SectorCount = U64(mish, 16, name),
            DataOffset = U64(mish, 24, name),
            BlockDescriptors = BinaryPrimitives.ReadUInt32BigEndian(mish[36..]),
            Checksum = UdifChecksum.Read(mish[64..]),
            Runs = runs,
        };
    }

    private static long U64(ReadOnlySpan<byte> s, int offset, string name)
    {
        ulong v = BinaryPrimitives.ReadUInt64BigEndian(s[offset..]);
        if (v > long.MaxValue)
            throw new InvalidDataException($"blkx entry '{name}': field out of range");
        return (long)v;
    }
}
