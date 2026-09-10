namespace LuminaMonitor.Formats.Apfs;

/// <summary>
/// nx_superblock_t — the container superblock (Apple File System Reference,
/// 2020-06-22, "Container"). Only the fields a read-only mount needs are kept.
/// </summary>
/// <remarks>
/// Byte offsets, derived from the struct listing (obj_phys_t = 32 bytes, packed):
/// <code>
///   0   obj_phys_t   nx_o
///  32   u32          nx_magic                 'NXSB' → 0x4253584E little-endian
///  36   u32          nx_block_size
///  40   u64          nx_block_count
///  48   u64          nx_features
///  56   u64          nx_readonly_compatible_features
///  64   u64          nx_incompatible_features
///  72   uuid_t       nx_uuid                  (16 bytes)
///  88   oid_t        nx_next_oid
///  96   xid_t        nx_next_xid
/// 104   u32          nx_xp_desc_blocks        (bit 31 set: area is a B-tree, unsupported here)
/// 108   u32          nx_xp_data_blocks
/// 112   paddr_t      nx_xp_desc_base
/// 120   paddr_t      nx_xp_data_base
/// 128   u32          nx_xp_desc_next
/// 132   u32          nx_xp_data_next
/// 136   u32          nx_xp_desc_index
/// 140   u32          nx_xp_desc_len
/// 144   u32          nx_xp_data_index
/// 148   u32          nx_xp_data_len
/// 152   oid_t        nx_spaceman_oid
/// 160   oid_t        nx_omap_oid              (0xA0 — physical address of the container omap)
/// 168   oid_t        nx_reaper_oid
/// 176   u32          nx_test_type             (0xB0)
/// 180   u32          nx_max_file_systems
/// 184   oid_t        nx_fs_oid[100]           (0xB8; NX_MAX_FILE_SYSTEMS = 100 → ends at 984)
/// </code>
/// </remarks>
internal sealed class ContainerSuperblock
{
    public const uint Magic = 0x4253584E; // "NXSB"
    public const int MaxFileSystems = 100;

    public ObjectHeader Header { get; private init; }
    public uint BlockSize { get; private init; }
    public ulong BlockCount { get; private init; }
    public ulong Features { get; private init; }
    public ulong ReadOnlyCompatibleFeatures { get; private init; }
    public ulong IncompatibleFeatures { get; private init; }
    public Guid Uuid { get; private init; }
    public ulong NextOid { get; private init; }
    public ulong NextXid { get; private init; }
    public uint XpDescBlocks { get; private init; }
    public uint XpDataBlocks { get; private init; }
    public ulong XpDescBase { get; private init; }
    public ulong XpDataBase { get; private init; }
    public ulong SpacemanOid { get; private init; }
    public ulong OmapOid { get; private init; }
    public ulong ReaperOid { get; private init; }
    public uint MaxFileSystemCount { get; private init; }
    public ulong[] FsOids { get; private init; } = Array.Empty<ulong>();

    /// <summary>Transaction identifier of this superblock (its o_xid).</summary>
    public ulong Xid => Header.Xid;

    public static ContainerSuperblock Parse(ReadOnlySpan<byte> block)
    {
        if (Le.U32(block, 32) != Magic)
            throw new InvalidDataException("magie NXSB absente");
        var fsOids = new ulong[MaxFileSystems];
        for (int i = 0; i < MaxFileSystems; i++)
            fsOids[i] = Le.U64(block, 184 + 8 * i);
        return new ContainerSuperblock
        {
            Header = new ObjectHeader(block),
            BlockSize = Le.U32(block, 36),
            BlockCount = Le.U64(block, 40),
            Features = Le.U64(block, 48),
            ReadOnlyCompatibleFeatures = Le.U64(block, 56),
            IncompatibleFeatures = Le.U64(block, 64),
            Uuid = new Guid(block.Slice(72, 16)),
            NextOid = Le.U64(block, 88),
            NextXid = Le.U64(block, 96),
            XpDescBlocks = Le.U32(block, 104),
            XpDataBlocks = Le.U32(block, 108),
            XpDescBase = Le.U64(block, 112),
            XpDataBase = Le.U64(block, 120),
            SpacemanOid = Le.U64(block, 152),
            OmapOid = Le.U64(block, 160),
            ReaperOid = Le.U64(block, 168),
            MaxFileSystemCount = Le.U32(block, 180),
            FsOids = fsOids,
        };
    }

    /// <summary>
    /// Locates the latest valid checkpoint superblock, following the spec's
    /// "Mounting an Apple File System Partition" procedure:
    /// 1. block 0 holds a copy of the container superblock (possibly stale);
    /// 2. its nx_xp_desc_base / nx_xp_desc_blocks give the checkpoint
    ///    descriptor area, a ring of blocks that are each either a
    ///    checkpoint_map_phys_t (type 0x0C) or an nx_superblock_t (type 0x01);
    /// 3. every nx_superblock_t whose magic, object type and Fletcher-64
    ///    checksum are valid is a candidate; the one with the highest xid is
    ///    the latest valid checkpoint. Block 0 itself is a candidate too.
    /// </summary>
    public static ContainerSuperblock ReadLatest(IBlockDevice device, out BlockSource blocks)
    {
        Span<byte> probe = stackalloc byte[40];
        device.Read(0, probe);
        if (Le.U32(probe, 32) != Magic)
            throw new InvalidDataException("bloc 0 : ce n'est pas un conteneur APFS (magie NXSB absente)");
        uint blockSize = Le.U32(probe, 36);
        if (blockSize < 4096 || blockSize > 65536 || (blockSize & (blockSize - 1)) != 0)
            throw new InvalidDataException($"taille de bloc invalide : {blockSize}");

        blocks = new BlockSource(device, (int)blockSize);
        byte[] block0 = blocks.ReadBlock(0, verify: false);
        ContainerSuperblock fromBlock0 = Parse(block0);
        ContainerSuperblock? best = Fletcher64.Verify(block0) ? fromBlock0 : null;

        if ((fromBlock0.XpDescBlocks & 0x80000000) != 0)
            throw new NotSupportedException("zone de descripteurs de checkpoint non contiguë (B-tree) : non prise en charge");

        for (uint i = 0; i < fromBlock0.XpDescBlocks; i++)
        {
            byte[] candidate = blocks.ReadBlock(fromBlock0.XpDescBase + i, verify: false);
            if (Le.U32(candidate, 32) != Magic)
                continue; // checkpoint map or unused slot
            if (new ObjectHeader(candidate).BaseType != ObjectTypes.NxSuperblock)
                continue;
            if (!Fletcher64.Verify(candidate))
                continue; // torn write: not part of a valid checkpoint
            ContainerSuperblock sb = Parse(candidate);
            if (best is null || sb.Xid > best.Xid)
                best = sb;
        }

        return best ?? throw new InvalidDataException("aucun superbloc conteneur avec somme de contrôle valide");
    }
}
