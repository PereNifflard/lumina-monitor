// Clean-room implementation. TN1150 "Attributes File" documents the fork-data (0x20) and extents (0x30)
// records and lists the key as "not finalized"; the key and inline record used by every real volume are the
// ones Apple published in the public header hfs_format.h (Mac OS X 10.4+), reproduced here from that
// documentation, not from any implementation.
//
// HFSPlusAttrKey, key bytes after the UInt16 keyLength (keyLength = 12 + 2*attrNameLen, max 266):
//   +0  UInt16 pad
//   +2  UInt32 fileID
//   +6  UInt32 startBlock     0 for the attribute itself; fork-relative block for an extents (0x30) record
//   +10 UInt16 attrNameLen
//   +12 UniChar attrName[attrNameLen]   UTF-16BE, not normalised
// Ordering: fileID, then attrName code units (unsigned; a shorter name that is a prefix sorts first), then startBlock.
// Records (recordType is a UInt32):
//   kHFSPlusAttrInlineData = 0x10: +0 recordType, +4 UInt32 reserved[2], +12 UInt32 attrSize, +16 UInt8 attrData[attrSize]
//   kHFSPlusAttrForkData   = 0x20: +0 recordType, +4 UInt32 reserved, +8 HFSPlusForkData theFork (80 bytes)
//   kHFSPlusAttrExtents    = 0x30: +0 recordType, +4 UInt32 reserved, +8 HFSPlusExtentRecord extents (64 bytes)
// Extra extents of a fork-data attribute are 0x30 records with the same fileID/name and a startBlock key
// equal to the fork block where they start (they never go to the extents overflow file).
namespace LuminaMonitor.Formats.Hfs;

internal sealed class AttributesFile
{
    public const uint InlineDataRecord = 0x10, ForkDataRecord = 0x20, ExtentsRecord = 0x30;
    public const int KeyFixedLength = 12, MaxNameUnits = 127;

    private readonly BTreeFile _tree;
    private readonly IBlockDevice _device;
    private readonly uint _blockSize;

    public AttributesFile(BTreeFile tree, IBlockDevice device, uint blockSize)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(device);
        _tree = tree;
        _device = device;
        _blockSize = blockSize;
    }

    /// <summary>Whole value of the extended attribute <paramref name="name"/> of <paramref name="fileId"/>, or null.</summary>
    public byte[]? Read(uint fileId, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length > MaxNameUnits) return null;
        var cursor = _tree.Search(candidate => Compare(fileId, name, 0, candidate));
        if (cursor is not { Exact: true } hit) return null;
        var d = _tree.ReadNode(hit.Node).Data(hit.Index).Span;
        if (d.Length < 4) throw new InvalidDataException("Attribute record shorter than 4 bytes.");
        switch (Be.U32(d, 0))
        {
            case InlineDataRecord:
            {
                if (d.Length < 16) throw new InvalidDataException("Inline attribute record shorter than 16 bytes.");
                uint size = Be.U32(d, 12);
                if (size > (uint)(d.Length - 16))
                    throw new InvalidDataException($"Inline attribute '{name}' declares {size} bytes but the record holds {d.Length - 16}.");
                return d.Slice(16, (int)size).ToArray();
            }
            case ForkDataRecord:
            {
                if (d.Length < 8 + ForkData.Size) throw new InvalidDataException("Fork attribute record shorter than 88 bytes.");
                return ReadFork(fileId, name, ForkData.Parse(d.Slice(8, ForkData.Size)));
            }
            default:
                return null; // TN1150: implementations must ignore record types they do not know
        }
    }

    /// <summary>Names of the extended attributes of <paramref name="fileId"/>, in key order.</summary>
    public IEnumerable<string> Names(uint fileId)
    {
        foreach (var record in _tree.RecordsFromKey(candidate => Compare(fileId, string.Empty, 0, candidate)))
        {
            var (recordFile, startBlock, name) = ParseKey(record.Key);
            if (recordFile < fileId) continue;
            if (recordFile > fileId) break;
            if (startBlock == 0) yield return name;
        }
    }

    private byte[] ReadFork(uint fileId, string name, ForkData fork)
    {
        if (fork.LogicalSize > int.MaxValue) throw new NotSupportedException($"Attribute '{name}' is larger than 2 GiB.");
        using var stream = ForkStream.Open(_device, _blockSize, fork,
            (uint start, out uint recordStart) => LookupExtents(fileId, name, start, out recordStart));
        var bytes = new byte[fork.LogicalSize];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private ExtentDescriptor[]? LookupExtents(uint fileId, string name, uint startBlock, out uint recordStartBlock)
    {
        recordStartBlock = startBlock;
        var cursor = _tree.Search(candidate => Compare(fileId, name, startBlock, candidate));
        if (cursor is not { Exact: true } hit) return null;
        var d = _tree.ReadNode(hit.Node).Data(hit.Index).Span;
        if (d.Length < 8 + ExtentDescriptor.RecordSize || Be.U32(d, 0) != ExtentsRecord) return null;
        return ExtentDescriptor.ParseRecord(d.Slice(8, ExtentDescriptor.RecordSize));
    }

    internal static (uint FileId, uint StartBlock, string Name) ParseKey(ReadOnlyMemory<byte> key)
    {
        var k = key.Span;
        if (k.Length < KeyFixedLength) throw new InvalidDataException("Attribute key shorter than 12 bytes.");
        int n = Be.U16(k, 10);
        if (n > MaxNameUnits || k.Length < KeyFixedLength + 2 * n)
            throw new InvalidDataException($"Attribute name length {n} exceeds the key.");
        Span<char> buffer = stackalloc char[MaxNameUnits];
        for (int i = 0; i < n; i++) buffer[i] = (char)Be.U16(k, KeyFixedLength + 2 * i);
        return (Be.U32(k, 2), Be.U32(k, 6), new string(buffer[..n]));
    }

    /// <summary>Sign of (search key - candidate key), see the ordering rule in the header comment.</summary>
    internal static int Compare(uint fileId, string name, uint startBlock, ReadOnlySpan<byte> candidate)
    {
        if (candidate.Length < KeyFixedLength) throw new InvalidDataException("Attribute key shorter than 12 bytes.");
        uint candidateFile = Be.U32(candidate, 2);
        if (fileId != candidateFile) return fileId < candidateFile ? -1 : 1;
        int candidateLength = Be.U16(candidate, 10);
        if (candidate.Length < KeyFixedLength + 2 * candidateLength)
            throw new InvalidDataException($"Attribute name length {candidateLength} exceeds the key.");
        int common = Math.Min(name.Length, candidateLength);
        for (int i = 0; i < common; i++)
        {
            ushort a = name[i], b = Be.U16(candidate, KeyFixedLength + 2 * i);
            if (a != b) return a < b ? -1 : 1;
        }
        if (name.Length != candidateLength) return name.Length < candidateLength ? -1 : 1;
        uint candidateStart = Be.U32(candidate, 6);
        return startBlock == candidateStart ? 0 : (startBlock < candidateStart ? -1 : 1);
    }

    /// <summary>Builds the key bytes (without the keyLength field) for tests and diagnostics.</summary>
    public static byte[] BuildKey(uint fileId, string name, uint startBlock)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length > MaxNameUnits) throw new ArgumentException("Attribute names are limited to 127 UTF-16 units.", nameof(name));
        var key = new byte[KeyFixedLength + 2 * name.Length];
        Be.WriteU32(key, 2, fileId);
        Be.WriteU32(key, 6, startBlock);
        Be.WriteU16(key, 10, (ushort)name.Length);
        for (int i = 0; i < name.Length; i++) Be.WriteU16(key, KeyFixedLength + 2 * i, name[i]);
        return key;
    }
}
