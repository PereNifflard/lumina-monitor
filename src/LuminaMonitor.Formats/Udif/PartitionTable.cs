using System.Buffers.Binary;
using System.Text;

namespace LuminaMonitor.Formats;

internal enum PartitionScheme
{
    /// <summary>No table recognised: the whole image is one volume.</summary>
    None,
    Gpt,
    ApplePartitionMap,
}

/// <summary>
/// Locates the volumes inside a UDIF image: a GUID Partition Table (what
/// hdiutil writes today), the classic Apple Partition Map, or nothing at
/// all, in which case the whole disk is reported as a single volume.
/// </summary>
/// <remarks>
/// The blkx names in the plist are free text and cannot be trusted to
/// identify a file system; the partition table is authoritative. Only the
/// primary GPT is consulted; the backup copy is not needed for a read-only
/// image that was written in one go.
/// </remarks>
internal sealed class PartitionTable
{
    private const int SectorSize = UdifImage.SectorSize;
    private const int MaxGptEntries = 1024;
    private const int MaxApmEntries = 256;

    public PartitionScheme Scheme { get; }
    public IReadOnlyList<(string Type, long FirstSector, long SectorCount)> Entries { get; }

    private PartitionTable(PartitionScheme scheme, IReadOnlyList<(string, long, long)> entries)
    {
        Scheme = scheme;
        Entries = entries;
    }

    public static PartitionTable Read(UdifImage image)
    {
        if (image.SectorCount >= 2)
        {
            Span<byte> head = stackalloc byte[2 * SectorSize];
            image.ReadSectors(0, 2, head);
            var sector1 = head[SectorSize..];

            if (sector1[..8].SequenceEqual("EFI PART"u8))
                return new PartitionTable(PartitionScheme.Gpt, ReadGpt(image, sector1));

            // A Driver Descriptor Map ('ER') in sector 0 may declare a block
            // size other than 512; the map then starts at block 1 of that size.
            int blockSize = head[..2].SequenceEqual("ER"u8) ? BinaryPrimitives.ReadUInt16BigEndian(head[2..]) : SectorSize;
            if (blockSize >= SectorSize && blockSize % SectorSize == 0 && blockSize <= 65536)
            {
                var apm = ReadApm(image, blockSize);
                if (apm is not null)
                    return new PartitionTable(PartitionScheme.ApplePartitionMap, apm);
            }
        }

        return new PartitionTable(PartitionScheme.None, [("Whole image", 0L, image.SectorCount)]);
    }

    private static List<(string, long, long)> ReadGpt(UdifImage image, ReadOnlySpan<byte> header)
    {
        long entriesLba = (long)BinaryPrimitives.ReadUInt64LittleEndian(header[72..]);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(header[80..]);
        uint entrySize = BinaryPrimitives.ReadUInt32LittleEndian(header[84..]);
        if (entrySize < 128 || entrySize % 8 != 0 || count > MaxGptEntries || entriesLba <= 0)
            throw new InvalidDataException("GPT header with implausible entry array");

        long bytes = count * (long)entrySize;
        int sectors = (int)((bytes + SectorSize - 1) / SectorSize);
        var table = new byte[sectors * SectorSize];
        image.ReadSectors(entriesLba, sectors, table);

        var result = new List<(string, long, long)>();
        for (int i = 0; i < count; i++)
        {
            var e = table.AsSpan(i * (int)entrySize, (int)entrySize);
            var type = new Guid(e[..16]); // GPT stores GUIDs in the same mixed-endian layout as System.Guid
            if (type == Guid.Empty)
                continue;
            long first = (long)BinaryPrimitives.ReadUInt64LittleEndian(e[32..]);
            long last = (long)BinaryPrimitives.ReadUInt64LittleEndian(e[40..]);
            if (last < first)
                continue;
            result.Add((GptTypeName(type), first, last - first + 1));
        }
        return result;
    }

    private static string GptTypeName(Guid type) => type.ToString("D").ToUpperInvariant() switch
    {
        "7C3457EF-0000-11AA-AA11-00306543ECAC" => "Apple_APFS",
        "48465300-0000-11AA-AA11-00306543ECAC" => "Apple_HFS",
        "426F6F74-0000-11AA-AA11-00306543ECAC" => "Apple_Boot",
        "55465300-0000-11AA-AA11-00306543ECAC" => "Apple_UFS",
        "53746F72-6167-11AA-AA11-00306543ECAC" => "Apple_CoreStorage",
        "C12A7328-F81F-11D2-BA4B-00A0C93EC93B" => "EFI_System",
        "EBD0A0A2-B9E5-4433-87C0-68B6B72699C7" => "Microsoft_Basic_Data",
        "0FC63DAF-8483-4772-8E79-3D69D8477DE4" => "Linux_Filesystem",
        var other => other,
    };

    /// <summary>
    /// Apple Partition Map: consecutive 'PM' blocks from block 1, each
    /// describing one partition (the map itself included) with big-endian
    /// start and length in blocks, and the type as a NUL-padded string.
    /// </summary>
    private static List<(string, long, long)>? ReadApm(UdifImage image, int blockSize)
    {
        int sectorsPerBlock = blockSize / SectorSize;
        var block = new byte[blockSize];
        var result = new List<(string, long, long)>();
        uint mapBlocks = 1;

        for (uint b = 1; b <= mapBlocks && b <= MaxApmEntries; b++)
        {
            long sector = (long)b * sectorsPerBlock;
            if (sector + sectorsPerBlock > image.SectorCount)
                break;
            image.ReadSectors(sector, sectorsPerBlock, block);
            if (!block.AsSpan(0, 2).SequenceEqual("PM"u8))
            {
                if (b == 1) return null;
                break;
            }
            if (b == 1)
                mapBlocks = BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(4));

            long start = BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(8)) * (long)sectorsPerBlock;
            long length = BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(12)) * (long)sectorsPerBlock;
            result.Add((CString(block.AsSpan(48, 32)), start, length));
        }
        return result;
    }

    private static string CString(ReadOnlySpan<byte> field)
    {
        int end = field.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end < 0 ? field : field[..end]).Trim();
    }
}
