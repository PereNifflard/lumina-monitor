namespace LuminaMonitor.Formats.Apfs;

/// <summary>
/// Compares a key stored in a node with the caller's search key. Negative when
/// the stored key sorts first, zero when equal, positive when it sorts after.
/// </summary>
internal delegate int KeyComparer(ReadOnlySpan<byte> stored, ReadOnlySpan<byte> search);

/// <summary>A key/value pair viewed in place inside a node buffer.</summary>
internal readonly struct BTreeEntry
{
    private readonly byte[] _node;
    private readonly int _keyOff, _keyLen, _valOff, _valLen;

    public BTreeEntry(byte[] node, int keyOff, int keyLen, int valOff, int valLen)
    {
        _node = node; _keyOff = keyOff; _keyLen = keyLen; _valOff = valOff; _valLen = valLen;
    }

    public ReadOnlySpan<byte> Key => new(_node, _keyOff, _keyLen);
    public ReadOnlySpan<byte> Value => HasValue ? new(_node, _valOff, _valLen) : default;
    /// <summary>False for a "ghost" (value offset BTOFF_INVALID).</summary>
    public bool HasValue => _valOff >= 0;
}

/// <summary>
/// btree_node_phys_t (Apple File System Reference, 2020-06-22, "B-Trees").
/// </summary>
/// <remarks>
/// Offsets: btn_o 0 (32 bytes); btn_flags 32 (u16); btn_level 34 (u16);
/// btn_nkeys 36 (u32); btn_table_space 40 (nloc_t: off u16 @40, len u16 @42);
/// btn_free_space 44; btn_key_free_list 48; btn_val_free_list 52; btn_data 56.
/// Layout inside btn_data: the table of contents starts at 56 + table_space.off
/// and is table_space.len bytes; the key area starts right after it and key
/// offsets are relative to that point; value offsets are counted BACKWARD from
/// the end of the value area, which is the end of the node — or 40 bytes
/// before it in a root node, where a btree_info_t closes the node.
/// ToC entries are kvloc_t {k.off u16, k.len u16, v.off u16, v.len u16} (8 bytes),
/// or kvoff_t {k u16, v u16} (4 bytes) when BTNODE_FIXED_KV_SIZE is set, the
/// lengths then coming from btree_info_t (non-leaf values are child oids: 8 bytes).
/// </remarks>
internal sealed class BTreeNode
{
    public const ushort FlagRoot = 0x0001, FlagLeaf = 0x0002, FlagFixedKvSize = 0x0004, FlagHashed = 0x0008, FlagNoHeader = 0x0010;
    public const int DataOffset = 56, InfoSize = 40, InvalidOffset = 0xFFFF;

    public byte[] Data { get; }
    public ushort Flags { get; }
    public int Level { get; }
    public int KeyCount { get; }
    public bool IsRoot => (Flags & FlagRoot) != 0;
    public bool IsLeaf => (Flags & FlagLeaf) != 0;
    public bool FixedKv => (Flags & FlagFixedKvSize) != 0;

    private readonly int _tocStart, _keyAreaStart, _valueAreaEnd, _fixedKeyLen, _fixedValLen;

    public BTreeNode(byte[] data, int fixedKeyLen, int fixedValLen)
    {
        Data = data;
        Flags = Le.U16(data, 32);
        Level = Le.U16(data, 34);
        KeyCount = checked((int)Le.U32(data, 36));
        _tocStart = DataOffset + Le.U16(data, 40);
        _keyAreaStart = _tocStart + Le.U16(data, 42);
        _valueAreaEnd = data.Length - (IsRoot ? InfoSize : 0);
        _fixedKeyLen = fixedKeyLen;
        _fixedValLen = fixedValLen;
        int entrySize = FixedKv ? 4 : 8;
        if (_keyAreaStart > _valueAreaEnd || _tocStart + (long)KeyCount * entrySize > _keyAreaStart)
            throw new InvalidDataException("table des matières du nœud B-tree incohérente");
    }

    public BTreeEntry Entry(int i)
    {
        if ((uint)i >= (uint)KeyCount)
            throw new ArgumentOutOfRangeException(nameof(i));
        int keyOff, keyLen, valOff, valLen;
        if (FixedKv)
        {
            int toc = _tocStart + i * 4;
            keyOff = Le.U16(Data, toc); valOff = Le.U16(Data, toc + 2);
            keyLen = _fixedKeyLen; valLen = IsLeaf ? _fixedValLen : 8;
        }
        else
        {
            int toc = _tocStart + i * 8;
            keyOff = Le.U16(Data, toc); keyLen = Le.U16(Data, toc + 2);
            valOff = Le.U16(Data, toc + 4); valLen = Le.U16(Data, toc + 6);
        }
        int keyStart = _keyAreaStart + keyOff;
        if (keyStart + keyLen > _valueAreaEnd)
            throw new InvalidDataException("clé hors du nœud B-tree");
        if (valOff == InvalidOffset)
            return new BTreeEntry(Data, keyStart, keyLen, -1, 0);
        int valStart = _valueAreaEnd - valOff;
        if (valStart < _keyAreaStart || valStart + valLen > _valueAreaEnd)
            throw new InvalidDataException("valeur hors du nœud B-tree");
        return new BTreeEntry(Data, keyStart, keyLen, valStart, valLen);
    }

    /// <summary>Child object identifier stored in a non-leaf entry (first 8 bytes of its value; also valid for btn_index_node_val_t).</summary>
    public ulong ChildOid(int i)
    {
        BTreeEntry e = Entry(i);
        if (!e.HasValue || e.Value.Length < 8)
            throw new InvalidDataException("entrée de nœud interne sans identifiant d'enfant");
        return Le.U64(e.Value, 0);
    }
}

/// <summary>
/// Read-only APFS B-tree: descends from the root with a caller-supplied key
/// comparer. Child oids are turned into block addresses by the resolver —
/// identity for physical trees (object maps), an object-map lookup for
/// virtual trees (file-system trees).
/// </summary>
internal sealed class BTree
{
    // btree_info_t, last 40 bytes of the root node: bt_flags 0, bt_node_size 4,
    // bt_key_size 8, bt_val_size 12, bt_longest_key 16, bt_longest_val 20,
    // bt_key_count 24 (u64), bt_node_count 32 (u64).
    public const uint FlagUint64Keys = 0x1, FlagAllowGhosts = 0x4, FlagEphemeral = 0x8, FlagPhysical = 0x10, FlagKvNonAligned = 0x40, FlagHashed = 0x80, FlagNoHeader = 0x100;

    private readonly BlockSource _blocks;
    private readonly Func<ulong, ulong> _resolveChild;
    private readonly int _nodeSize, _keyLen, _valLen;

    public BTreeNode Root { get; }
    public uint Flags { get; }

    public BTree(BlockSource blocks, ulong rootPaddr, Func<ulong, ulong>? resolveChild = null)
    {
        _blocks = blocks;
        _resolveChild = resolveChild ?? (static (ulong oid) => oid);
        byte[] data = blocks.ReadBlock(rootPaddr);
        if (new ObjectHeader(data).BaseType != ObjectTypes.Btree)
            throw new InvalidDataException($"le bloc {rootPaddr} n'est pas une racine de B-tree");
        // btree_info_t sits at the end of the root node; nodes are "almost
        // always one logical block" (spec), the only size this reader handles.
        int info = data.Length - BTreeNode.InfoSize;
        Flags = Le.U32(data, info);
        _nodeSize = checked((int)Le.U32(data, info + 4));
        _keyLen = checked((int)Le.U32(data, info + 8));
        _valLen = checked((int)Le.U32(data, info + 12));
        if (_nodeSize != data.Length)
            throw new NotSupportedException($"nœuds B-tree de {_nodeSize} octets (≠ taille de bloc) : non pris en charge");
        if ((Flags & FlagNoHeader) != 0)
            throw new NotSupportedException("B-tree sans en-têtes d'objet : non pris en charge");
        Root = new BTreeNode(data, _keyLen, _valLen);
        if (!Root.IsRoot)
            throw new InvalidDataException("la racine du B-tree n'a pas le drapeau BTNODE_ROOT");
    }

    private BTreeNode ReadChild(BTreeNode parent, int index)
    {
        ulong paddr = _resolveChild(parent.ChildOid(index));
        var child = new BTreeNode(_blocks.ReadObject(paddr, _nodeSize), _keyLen, _valLen);
        if (child.Level != parent.Level - 1)
            throw new InvalidDataException("niveau de nœud B-tree incohérent");
        return child;
    }

    /// <summary>Index of the last entry whose key is not greater than <paramref name="search"/>, or -1.</summary>
    private static int LastNotGreater(BTreeNode node, ReadOnlySpan<byte> search, KeyComparer cmp)
    {
        int lo = 0, hi = node.KeyCount - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (cmp(node.Entry(mid).Key, search) <= 0) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found;
    }

    /// <summary>Finds the entry with the greatest key not greater than <paramref name="search"/>.</summary>
    public bool TryFindFloor(ReadOnlySpan<byte> search, KeyComparer cmp, out BTreeEntry entry)
    {
        BTreeNode node = Root;
        while (true)
        {
            int idx = LastNotGreater(node, search, cmp);
            if (node.IsLeaf)
            {
                entry = idx < 0 ? default : node.Entry(idx);
                return idx >= 0;
            }
            node = ReadChild(node, Math.Max(idx, 0));
        }
    }

    /// <summary>
    /// Enumerates entries in key order, starting at the first key not less
    /// than <paramref name="search"/>; ghosts are skipped. The caller stops
    /// when keys leave the range of interest.
    /// </summary>
    public IEnumerable<BTreeEntry> EnumerateFrom(byte[] search, KeyComparer cmp) =>
        Walk(DescendToLowerBound(search, cmp));

    /// <summary>Path from the root to the leaf slot of the first key not less than <paramref name="search"/> (the slot may be one past the leaf's last entry).</summary>
    private List<(BTreeNode Node, int Index)> DescendToLowerBound(byte[] search, KeyComparer cmp)
    {
        var path = new List<(BTreeNode Node, int Index)>();
        BTreeNode node = Root;
        while (true)
        {
            int idx = LastNotGreater(node, search, cmp);
            if (node.IsLeaf)
            {
                if (idx < 0 || cmp(node.Entry(idx).Key, search) < 0)
                    idx++;
                path.Add((node, idx));
                return path;
            }
            int child = Math.Max(idx, 0);
            path.Add((node, child));
            node = ReadChild(node, child);
        }
    }

    // Kept free of Span locals: C# 12 forbids ref structs inside iterators.
    private IEnumerable<BTreeEntry> Walk(List<(BTreeNode Node, int Index)> path)
    {
        while (path.Count > 0)
        {
            var (leaf, i) = path[^1];
            if (i < leaf.KeyCount)
            {
                path[^1] = (leaf, i + 1);
                BTreeEntry e = leaf.Entry(i);
                if (e.HasValue)
                    yield return e;
                continue;
            }
            path.RemoveAt(path.Count - 1);
            while (path.Count > 0)
            {
                var (parent, pi) = path[^1];
                if (pi + 1 >= parent.KeyCount)
                {
                    path.RemoveAt(path.Count - 1);
                    continue;
                }
                path[^1] = (parent, pi + 1);
                BTreeNode n = ReadChild(parent, pi + 1);
                while (!n.IsLeaf)
                {
                    path.Add((n, 0));
                    n = ReadChild(n, 0);
                }
                path.Add((n, 0));
                break;
            }
        }
    }
}
