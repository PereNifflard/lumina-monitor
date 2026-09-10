// Clean-room implementation from Apple TN1150 "HFS Plus Volume Format", section "B-Trees"
// ("Header Record", "Header Node", "Searching"). No code taken from any implementation.
//
// BTHeaderRec - 106 bytes; first record of node 0 (the header node), so at node offset 14:
//   rec+0   UInt16 treeDepth        (node offset 14)
//   rec+2   UInt32 rootNode         (16)   0 when the tree is empty
//   rec+6   UInt32 leafRecords      (20)
//   rec+10  UInt32 firstLeafNode    (24)
//   rec+14  UInt32 lastLeafNode     (28)
//   rec+18  UInt16 nodeSize         (32)   power of two, 512..32768
//   rec+20  UInt16 maxKeyLength     (34)
//   rec+22  UInt32 totalNodes       (36)
//   rec+26  UInt32 freeNodes        (40)
//   rec+30  UInt16 reserved1        (44)
//   rec+32  UInt32 clumpSize        (46)
//   rec+36  UInt8  btreeType        (50)
//   rec+37  UInt8  keyCompareType   (51)   0xCF kHFSCaseFolding, 0xBC kHFSBinaryCompare (HFSX)
//   rec+38  UInt32 attributes       (52)   bit0 kBTBadCloseMask, bit1 kBTBigKeysMask, bit2 kBTVariableIndexKeysMask
//   rec+42  UInt32 reserved3[16]    (56..120)
// Header node records: [0] header record @14, [1] user data (128 bytes) @120, [2] map record @248
// up to nodeSize-8; the four record offsets occupy the last 8 bytes of the node.
namespace LuminaMonitor.Formats.Hfs;

/// <summary>Returns the sign of (search key - candidate key): negative when the search key sorts first.</summary>
internal delegate int BTreeKeyComparison(ReadOnlySpan<byte> candidateKey);

/// <summary>Position of a leaf record; <see cref="Exact"/> tells whether it matched the search key.</summary>
internal readonly record struct BTreeCursor(uint Node, int Index, bool Exact);

internal readonly record struct BTreeRecord(ReadOnlyMemory<byte> Key, ReadOnlyMemory<byte> Data);

internal sealed class BTreeFile
{
    public const int HeaderRecordOffset = 14;
    public const int HeaderRecordSize = 106;
    public const int MinNodeSize = 512, MaxNodeSize = 32768;
    public const uint BadCloseMask = 0x1, BigKeysMask = 0x2, VariableIndexKeysMask = 0x4;
    public const byte CaseFolding = 0xCF, BinaryCompare = 0xBC;

    private const int CacheLimit = 256;
    private readonly Stream _fork;
    private readonly Dictionary<uint, BTreeNode> _cache = new();

    public ushort TreeDepth { get; }
    public uint RootNode { get; }
    public uint LeafRecords { get; }
    public uint FirstLeafNode { get; }
    public uint LastLeafNode { get; }
    public ushort NodeSize { get; }
    public ushort MaxKeyLength { get; }
    public uint TotalNodes { get; }
    public uint FreeNodes { get; }
    public byte BTreeType { get; }
    public byte KeyCompareType { get; }
    public uint Attributes { get; }

    public bool BigKeys => (Attributes & BigKeysMask) != 0;
    public bool VariableIndexKeys => (Attributes & VariableIndexKeysMask) != 0;

    /// <param name="fork">Seekable read-only stream over the B-tree file's data fork.</param>
    public BTreeFile(Stream fork)
    {
        ArgumentNullException.ThrowIfNull(fork);
        _fork = fork;
        Span<byte> head = stackalloc byte[HeaderRecordOffset + HeaderRecordSize];
        ReadExact(0, head);
        if (unchecked((sbyte)head[8]) != BTreeNode.HeaderKind)
            throw new InvalidDataException("B-tree node 0 is not a header node.");
        var h = head.Slice(HeaderRecordOffset);
        TreeDepth = Be.U16(h, 0);
        RootNode = Be.U32(h, 2);
        LeafRecords = Be.U32(h, 6);
        FirstLeafNode = Be.U32(h, 10);
        LastLeafNode = Be.U32(h, 14);
        NodeSize = Be.U16(h, 18);
        MaxKeyLength = Be.U16(h, 20);
        TotalNodes = Be.U32(h, 22);
        FreeNodes = Be.U32(h, 26);
        BTreeType = h[36];
        KeyCompareType = h[37];
        Attributes = Be.U32(h, 38);
        if (NodeSize < MinNodeSize || NodeSize > MaxNodeSize || (NodeSize & (NodeSize - 1)) != 0)
            throw new InvalidDataException($"Invalid B-tree node size {NodeSize}.");
    }

    public BTreeNode ReadNode(uint number)
    {
        if (_cache.TryGetValue(number, out var cached)) return cached;
        if (number >= TotalNodes)
            throw new InvalidDataException($"B-tree node {number} is beyond totalNodes ({TotalNodes}).");
        var bytes = new byte[NodeSize];
        ReadExact((long)number * NodeSize, bytes);
        var node = new BTreeNode(number, bytes, BigKeys, VariableIndexKeys, MaxKeyLength);
        if (_cache.Count >= CacheLimit) _cache.Clear();
        _cache[number] = node;
        return node;
    }

    /// <summary>
    /// Descends from the root and returns the leaf record with the greatest key that is less than or
    /// equal to the search key (Exact = true when equal), or null when every key is greater.
    /// </summary>
    public BTreeCursor? Search(BTreeKeyComparison compare)
    {
        ArgumentNullException.ThrowIfNull(compare);
        uint nodeNumber = RootNode;
        if (nodeNumber == 0) return null;
        for (int level = 0; level < 64; level++)
        {
            var node = ReadNode(nodeNumber);
            if (node.NumRecords == 0) return null;
            int lo = 0, hi = node.NumRecords - 1, best = -1, bestCompare = 1;
            while (lo <= hi)
            {
                int mid = lo + (hi - lo) / 2;
                int c = compare(node.Key(mid).Span);
                if (c >= 0) { best = mid; bestCompare = c; lo = mid + 1; }
                else hi = mid - 1;
            }
            if (node.IsLeaf) return best < 0 ? null : new BTreeCursor(nodeNumber, best, bestCompare == 0);
            if (!node.IsIndex)
                throw new InvalidDataException($"B-tree node {nodeNumber} has kind {node.Kind}; expected a leaf or index node.");
            nodeNumber = node.ChildNode(best < 0 ? 0 : best);
        }
        throw new InvalidDataException("B-tree deeper than 64 levels (corrupt tree).");
    }

    /// <summary>Leaf records from the cursor onwards, following fLink across leaves.</summary>
    public IEnumerable<BTreeRecord> RecordsFrom(BTreeCursor cursor)
    {
        uint nodeNumber = cursor.Node;
        int index = Math.Max(cursor.Index, 0);
        for (long visited = 0; visited <= TotalNodes; visited++)
        {
            var node = ReadNode(nodeNumber);
            if (!node.IsLeaf) throw new InvalidDataException($"B-tree node {nodeNumber} is not a leaf node.");
            for (int i = index; i < node.NumRecords; i++)
                yield return new BTreeRecord(node.Key(i), node.Data(i));
            if (node.FLink == 0) yield break;
            nodeNumber = node.FLink;
            index = 0;
        }
        throw new InvalidDataException("B-tree leaf chain loops (corrupt tree).");
    }

    /// <summary>Every leaf record in key order.</summary>
    public IEnumerable<BTreeRecord> AllLeafRecords() =>
        FirstLeafNode == 0 ? Array.Empty<BTreeRecord>() : RecordsFrom(new BTreeCursor(FirstLeafNode, 0, false));

    /// <summary>
    /// Leaf records in key order starting at the search key's neighbourhood: the exact record when it exists,
    /// otherwise the greatest smaller record (callers skip it) or the very first record when every key is
    /// greater. Meant for range scans such as "all children of a folder" or "all attributes of a file".
    /// </summary>
    public IEnumerable<BTreeRecord> RecordsFromKey(BTreeKeyComparison compare)
    {
        var cursor = Search(compare);
        if (cursor is null)
        {
            if (FirstLeafNode == 0) return Array.Empty<BTreeRecord>();
            cursor = new BTreeCursor(FirstLeafNode, 0, false);
        }
        return RecordsFrom(cursor.Value);
    }

    private void ReadExact(long offset, Span<byte> destination)
    {
        _fork.Position = offset;
        int total = 0;
        while (total < destination.Length)
        {
            int n = _fork.Read(destination.Slice(total));
            if (n <= 0) throw new EndOfStreamException($"B-tree fork ends before offset {offset + destination.Length}.");
            total += n;
        }
    }
}
