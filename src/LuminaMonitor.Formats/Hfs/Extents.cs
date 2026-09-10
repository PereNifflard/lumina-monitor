// Clean-room implementation from Apple TN1150 "HFS Plus Volume Format", section "Extents Overflow File".
//
// HFSPlusExtentKey, key bytes after the UInt16 keyLength (keyLength is always 10):
//   +0  UInt8  forkType     0x00 data fork, 0xFF resource fork
//   +1  UInt8  pad
//   +2  UInt32 fileID
//   +6  UInt32 startBlock   fork-relative allocation block of the first extent in the record
// Keys are compared by fileID, then forkType, then startBlock (all unsigned).
// Record data: HFSPlusExtentRecord = 8 x { UInt32 startBlock; UInt32 blockCount } = 64 bytes; each record
// continues the fork where the previous extents (catalog record first, then earlier overflow records) ended.
namespace LuminaMonitor.Formats.Hfs;

internal sealed class ExtentsOverflow
{
    public const byte DataFork = 0x00, ResourceFork = 0xFF;
    public const int KeyLength = 10;

    private readonly BTreeFile _tree;

    public ExtentsOverflow(BTreeFile tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        _tree = tree;
    }

    /// <summary>
    /// Returns the extent record of the fork that covers fork block <paramref name="startBlock"/> (the record
    /// with the greatest key not above it), and the fork block where that record starts; null when none exists.
    /// </summary>
    public ExtentDescriptor[]? Lookup(uint fileId, byte forkType, uint startBlock, out uint recordStartBlock)
    {
        recordStartBlock = 0;
        var cursor = _tree.Search(candidate => Compare(fileId, forkType, startBlock, candidate));
        if (cursor is null) return null;
        var node = _tree.ReadNode(cursor.Value.Node);
        var key = node.Key(cursor.Value.Index).Span;
        if (key.Length < KeyLength || key[0] != forkType || Be.U32(key, 2) != fileId) return null;
        recordStartBlock = Be.U32(key, 6);
        var data = node.Data(cursor.Value.Index).Span;
        if (data.Length < ExtentDescriptor.RecordSize)
            throw new InvalidDataException($"Extents overflow record for file {fileId} is shorter than 64 bytes.");
        return ExtentDescriptor.ParseRecord(data);
    }

    /// <summary>Overflow resolver for one fork, in the shape <see cref="ForkStream"/> expects.</summary>
    public ExtentOverflowResolver ResolverFor(uint fileId, byte forkType) =>
        (uint start, out uint recordStart) => Lookup(fileId, forkType, start, out recordStart);

    internal static int Compare(uint fileId, byte forkType, uint startBlock, ReadOnlySpan<byte> candidate)
    {
        if (candidate.Length < KeyLength) throw new InvalidDataException("Extents overflow key shorter than 10 bytes.");
        uint candidateFile = Be.U32(candidate, 2);
        if (fileId != candidateFile) return fileId < candidateFile ? -1 : 1;
        byte candidateFork = candidate[0];
        if (forkType != candidateFork) return forkType < candidateFork ? -1 : 1;
        uint candidateStart = Be.U32(candidate, 6);
        return startBlock == candidateStart ? 0 : (startBlock < candidateStart ? -1 : 1);
    }

    /// <summary>Builds the key bytes (without the keyLength field) for tests and diagnostics.</summary>
    public static byte[] BuildKey(uint fileId, byte forkType, uint startBlock)
    {
        var key = new byte[KeyLength];
        key[0] = forkType;
        Be.WriteU32(key, 2, fileId);
        Be.WriteU32(key, 6, startBlock);
        return key;
    }
}
