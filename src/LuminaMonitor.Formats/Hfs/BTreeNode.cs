// Clean-room implementation from Apple TN1150 "HFS Plus Volume Format", section "B-Trees"
// ("Node Structure", "Node Descriptor", "Keyed Records"). No code taken from any implementation.
//
// BTNodeDescriptor - 14 bytes at node offset 0:
//   +0  UInt32 fLink        next node of the same kind (0 = none)
//   +4  UInt32 bLink        previous node of the same kind (0 = none)
//   +8  SInt8  kind         -1 leaf, 0 index, 1 header, 2 map
//   +9  UInt8  height       0 for header/map nodes, 1 for leaves, +1 per index level
//   +10 UInt16 numRecords
//   +12 UInt16 reserved
// Record offsets: UInt16 each, stored from the END of the node backwards. The offset of record i is at
// nodeSize - 2*(i+1); there are numRecords+1 entries, the extra one being the offset of the free space
// (i.e. the end of the last record). The first record always starts at offset 14.
// Keyed record: keyLength (UInt16 when kBTBigKeysMask is set, always the case for HFS+; else UInt8),
// key bytes, a pad byte when (sizeof(keyLength) + key size) is odd, then the data (2-byte aligned).
// In an index node the key occupies maxKeyLength bytes unless kBTVariableIndexKeysMask is set, and the
// data is the UInt32 number of the child node.
namespace LuminaMonitor.Formats.Hfs;

internal sealed class BTreeNode
{
    public const int DescriptorSize = 14;
    public const sbyte LeafKind = -1, IndexKind = 0, HeaderKind = 1, MapKind = 2;

    private readonly byte[] _bytes;
    private readonly bool _bigKeys;
    private readonly bool _variableIndexKeys;
    private readonly int _maxKeyLength;

    public uint Number { get; }
    public uint FLink { get; }
    public uint BLink { get; }
    public sbyte Kind { get; }
    public byte Height { get; }
    public int NumRecords { get; }

    public bool IsLeaf => Kind == LeafKind;
    public bool IsIndex => Kind == IndexKind;
    public bool IsHeader => Kind == HeaderKind;

    public BTreeNode(uint number, byte[] bytes, bool bigKeys, bool variableIndexKeys, int maxKeyLength)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length < DescriptorSize + 2) throw new InvalidDataException("B-tree node smaller than its descriptor.");
        _bytes = bytes;
        _bigKeys = bigKeys;
        _variableIndexKeys = variableIndexKeys;
        _maxKeyLength = maxKeyLength;
        Number = number;
        FLink = Be.U32(bytes, 0);
        BLink = Be.U32(bytes, 4);
        Kind = unchecked((sbyte)bytes[8]);
        Height = bytes[9];
        NumRecords = Be.U16(bytes, 10);
        if (DescriptorSize + 2 * (NumRecords + 1) > bytes.Length)
            throw new InvalidDataException($"B-tree node {number} declares {NumRecords} records, more than fit in {bytes.Length} bytes.");
    }

    /// <summary>Byte offset of record <paramref name="index"/>; index == NumRecords gives the free-space offset.</summary>
    public int RecordOffset(int index)
    {
        if ((uint)index > (uint)NumRecords) throw new ArgumentOutOfRangeException(nameof(index));
        int offset = Be.U16(_bytes, _bytes.Length - 2 * (index + 1));
        int offsetsStart = _bytes.Length - 2 * (NumRecords + 1);
        if (offset < DescriptorSize || offset > offsetsStart)
            throw new InvalidDataException($"B-tree node {Number}: record offset {offset} is outside the record area.");
        return offset;
    }

    /// <summary>Whole record <paramref name="index"/> (key length field, key, pad and data).</summary>
    public ReadOnlyMemory<byte> Record(int index)
    {
        int start = RecordOffset(index), end = RecordOffset(index + 1);
        if (end < start) throw new InvalidDataException($"B-tree node {Number}: record {index} has a negative size.");
        return _bytes.AsMemory(start, end - start);
    }

    /// <summary>Key bytes of record <paramref name="index"/>, without the key length field.</summary>
    public ReadOnlyMemory<byte> Key(int index)
    {
        Locate(index, out int keyStart, out int keyLength, out _, out _);
        return _bytes.AsMemory(keyStart, keyLength);
    }

    /// <summary>Data of record <paramref name="index"/> (after the key and its alignment pad).</summary>
    public ReadOnlyMemory<byte> Data(int index)
    {
        Locate(index, out _, out _, out int dataStart, out int end);
        return _bytes.AsMemory(dataStart, end - dataStart);
    }

    /// <summary>Child node number stored in index record <paramref name="index"/>.</summary>
    public uint ChildNode(int index)
    {
        if (!IsIndex) throw new InvalidOperationException($"B-tree node {Number} is not an index node.");
        var data = Data(index).Span;
        if (data.Length < 4) throw new InvalidDataException($"B-tree node {Number}: index record {index} has no child pointer.");
        return Be.U32(data, 0);
    }

    private void Locate(int index, out int keyStart, out int keyLength, out int dataStart, out int end)
    {
        int start = RecordOffset(index);
        end = RecordOffset(index + 1);
        int lengthField = _bigKeys ? 2 : 1;
        if (end - start < lengthField) throw new InvalidDataException($"B-tree node {Number}: record {index} is too small for a key.");
        keyLength = _bigKeys ? Be.U16(_bytes, start) : _bytes[start];
        keyStart = start + lengthField;
        int keySpace = (IsIndex && !_variableIndexKeys) ? _maxKeyLength : keyLength;
        if (keyLength > keySpace || keyStart + keySpace > end)
            throw new InvalidDataException($"B-tree node {Number}: record {index} key length {keyLength} exceeds the record.");
        dataStart = keyStart + keySpace;
        if (((lengthField + keySpace) & 1) != 0) dataStart++;
        if (dataStart > end) dataStart = end;
    }
}
