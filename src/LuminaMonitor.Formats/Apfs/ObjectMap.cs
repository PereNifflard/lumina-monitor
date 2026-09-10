namespace LuminaMonitor.Formats.Apfs;

/// <summary>
/// omap_phys_t and its B-tree — maps a virtual object identifier to the block
/// holding the object's copy for a transaction (Apple File System Reference,
/// 2020-06-22, "Object Maps").
/// </summary>
/// <remarks>
/// omap_phys_t offsets: om_o 0 (32); om_flags 32 (u32); om_snap_count 36 (u32);
/// om_tree_type 40 (u32); om_snapshot_tree_type 44 (u32); om_tree_oid 48 (oid_t);
/// om_snapshot_tree_oid 56; om_most_recent_snap 64 (xid_t);
/// om_pending_revert_min 72; om_pending_revert_max 80.
/// omap_key_t (16 bytes): ok_oid 0 (u64), ok_xid 8 (u64).
/// omap_val_t (16 bytes): ov_flags 0 (u32), ov_size 4 (u32), ov_paddr 8 (u64).
/// The mapping tree is a physical B-tree (BTREE_PHYSICAL): om_tree_oid and the
/// child oids inside it are block addresses. Keys sort by oid, then xid; a
/// lookup for (oid, xid) takes the entry with that oid whose xid is the
/// greatest not above the requested one.
/// </remarks>
internal sealed class ObjectMap
{
    public const uint ValDeleted = 0x1, ValSaved = 0x2, ValEncrypted = 0x4, ValNoHeader = 0x8, ValCryptoGeneration = 0x10;

    private readonly BTree _tree;

    public ObjectMap(BlockSource blocks, ulong paddr)
    {
        byte[] block = blocks.ReadBlock(paddr);
        var header = new ObjectHeader(block);
        if (header.BaseType != ObjectTypes.Omap)
            throw new InvalidDataException($"le bloc {paddr} n'est pas une object map (type 0x{header.BaseType:x})");
        _tree = new BTree(blocks, Le.U64(block, 48));
        if ((_tree.Flags & BTree.FlagPhysical) == 0)
            throw new NotSupportedException("object map dont l'arbre n'est pas physique : non pris en charge");
    }

    /// <summary>Resolves <paramref name="oid"/> as of transaction <paramref name="maxXid"/>; false when unmapped or deleted.</summary>
    public bool TryLookup(ulong oid, ulong maxXid, out ulong paddr, out uint flags)
    {
        Span<byte> key = stackalloc byte[16];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(key, oid);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(key[8..], maxXid);
        paddr = 0;
        flags = 0;
        if (!_tree.TryFindFloor(key, CompareKeys, out BTreeEntry entry) || Le.U64(entry.Key, 0) != oid)
            return false;
        if (entry.Value.Length < 16)
            throw new InvalidDataException("valeur d'object map tronquée");
        flags = Le.U32(entry.Value, 0);
        paddr = Le.U64(entry.Value, 8);
        return (flags & ValDeleted) == 0;
    }

    /// <summary>Resolves <paramref name="oid"/> or throws; encrypted and headerless objects are refused.</summary>
    public ulong Lookup(ulong oid, ulong maxXid)
    {
        if (!TryLookup(oid, maxXid, out ulong paddr, out uint flags))
            throw new InvalidDataException($"objet virtuel {oid} introuvable dans l'object map (xid ≤ {maxXid})");
        if ((flags & ValEncrypted) != 0)
            throw new NotSupportedException($"objet virtuel {oid} chiffré : non pris en charge");
        if ((flags & ValNoHeader) != 0)
            throw new NotSupportedException($"objet virtuel {oid} sans en-tête : non pris en charge");
        return paddr;
    }

    /// <summary>omap_key_t ordering: by oid, then by xid (both unsigned).</summary>
    public static int CompareKeys(ReadOnlySpan<byte> stored, ReadOnlySpan<byte> search)
    {
        int c = Le.U64(stored, 0).CompareTo(Le.U64(search, 0));
        return c != 0 ? c : Le.U64(stored, 8).CompareTo(Le.U64(search, 8));
    }
}
