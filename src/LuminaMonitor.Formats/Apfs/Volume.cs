using System.Text;

namespace LuminaMonitor.Formats.Apfs;

/// <summary>
/// One APFS volume: its apfs_superblock_t, object map and file-system tree
/// (Apple File System Reference, 2020-06-22, "Volumes", "File-System
/// Objects", "Data Streams", "Extended Fields").
/// </summary>
/// <remarks>
/// apfs_superblock_t offsets (obj_phys_t = 32 bytes, packed; wrapped_meta_crypto_state_t = 20 bytes):
/// <code>
///  32 u32  apfs_magic 'APSB' → 0x42535041 LE    36 u32 apfs_fs_index
///  40 u64  apfs_features                          48 u64 apfs_readonly_compatible_features
///  56 u64  apfs_incompatible_features             64 u64 apfs_unmount_time
///  72 u64  apfs_fs_reserve_block_count            80 u64 apfs_fs_quota_block_count
///  88 u64  apfs_fs_alloc_count                    96 wrapped_meta_crypto_state_t apfs_meta_crypto (20)
/// 116 u32  apfs_root_tree_type                   120 u32 apfs_extentref_tree_type
/// 124 u32  apfs_snap_meta_tree_type              128 oid_t apfs_omap_oid (0x80, physical address)
/// 136 oid_t apfs_root_tree_oid (0x88, virtual)   144 oid_t apfs_extentref_tree_oid
/// 152 oid_t apfs_snap_meta_tree_oid              160 xid_t apfs_revert_to_xid
/// 168 oid_t apfs_revert_to_sblock_oid            176 u64 apfs_next_obj_id
/// 184..216 u64 apfs_num_files/directories/symlinks/other_fsobjects/snapshots
/// 224 u64  apfs_total_blocks_alloced             232 u64 apfs_total_blocks_freed
/// 240 uuid_t apfs_vol_uuid (16)                  256 u64 apfs_last_mod_time
/// 264 u64  apfs_fs_flags (0x108)                 272 apfs_modified_by_t apfs_formatted_by (48)
/// 320 apfs_modified_by_t apfs_modified_by[8] (384)
/// 704 u8   apfs_volname[256] (0x2C0)             960 u32 apfs_next_doc_id   964 u16 apfs_role
/// </code>
/// </remarks>
internal sealed class Volume
{
    public const uint Magic = 0x42535041; // "APSB"
    public const ulong FsFlagUnencrypted = 0x1;
    public const ulong IncompatCaseInsensitive = 0x1, IncompatNormalizationInsensitive = 0x8, IncompatSealed = 0x20;
    public const ulong RootDirInode = 2;

    private readonly BlockSource _blocks;
    private readonly ObjectMap _omap;
    private readonly BTree _tree;

    public string Name { get; }
    public ulong FsFlags { get; }
    public ulong IncompatibleFeatures { get; }
    public bool CaseInsensitive => (IncompatibleFeatures & IncompatCaseInsensitive) != 0;
    /// <summary>Directory entries use j_drec_hashed_key_t when names are compared insensitively.</summary>
    public bool HashedNames => (IncompatibleFeatures & (IncompatCaseInsensitive | IncompatNormalizationInsensitive)) != 0;

    public Volume(BlockSource blocks, ulong superblockPaddr, ulong xid)
    {
        _blocks = blocks;
        byte[] sb = blocks.ReadBlock(superblockPaddr);
        if (Le.U32(sb, 32) != Magic || new ObjectHeader(sb).BaseType != ObjectTypes.Fs)
            throw new InvalidDataException($"le bloc {superblockPaddr} n'est pas un superbloc de volume APFS");
        FsFlags = Le.U64(sb, 264);
        if ((FsFlags & FsFlagUnencrypted) == 0)
            throw new NotSupportedException("volume APFS chiffré : non pris en charge");
        IncompatibleFeatures = Le.U64(sb, 56);
        ReadOnlySpan<byte> name = sb.AsSpan(704, 256);
        int end = name.IndexOf((byte)0);
        Name = Encoding.UTF8.GetString(end < 0 ? name : name[..end]);
        _omap = new ObjectMap(blocks, Le.U64(sb, 128));
        ulong rootTreeOid = Le.U64(sb, 136);
        _tree = new BTree(blocks, _omap.Lookup(rootTreeOid, xid), oid => _omap.Lookup(oid, xid));
    }

    public IEnumerable<(string Name, bool IsDirectory, ulong FileId)> ListDirectory(string path)
    {
        ulong id = ResolvePath(path);
        if (!ReadInode(id).IsDirectory)
            throw new IOException($"{path} n'est pas un répertoire");
        return Entries(id).Select(e => (e.Name, e.IsDirectory, e.FileId));
    }

    public long FileSize(string path)
    {
        InodeRecord inode = ReadInode(ResolvePath(path));
        if (inode.IsCompressed)
            return (long)Decmpfs.UncompressedSize(ReadXattr(inode.Id, Decmpfs.AttrName)
                ?? throw new InvalidDataException($"{path} : attribut decmpfs absent"));
        return (long)inode.DataStreamSize;
    }

    public Stream OpenFile(string path)
    {
        InodeRecord inode = ReadInode(ResolvePath(path));
        if (inode.IsDirectory)
            throw new IOException($"{path} est un répertoire");
        if (inode.IsSymlink)
            throw new NotSupportedException($"{path} est un lien symbolique : non suivi");
        if (!inode.IsCompressed)
            return OpenDataStream(inode.PrivateId, inode.DataStreamSize);
        byte[] attr = ReadXattr(inode.Id, Decmpfs.AttrName)
            ?? throw new InvalidDataException($"{path} : attribut decmpfs absent");
        byte[] data = Decmpfs.Decode(attr, () => ReadXattr(inode.Id, Decmpfs.ResourceForkName)
            ?? throw new InvalidDataException($"{path} : resource fork absente"));
        return new MemoryStream(data, writable: false);
    }

    /// <summary>Walks <paramref name="path"/> from the root directory (inode 2); symlinks are not followed.</summary>
    public ulong ResolvePath(string path)
    {
        ulong id = RootDirInode;
        foreach (string segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            id = Lookup(id, segment) ?? throw new FileNotFoundException($"chemin introuvable dans le volume : {path}", path);
        }
        return id;
    }

    public ulong? Lookup(ulong directoryId, string name)
    {
        foreach (DirEntry entry in Entries(directoryId))
            if (NamesEqual(entry.Name, name))
                return entry.FileId;
        return null;
    }

    private bool NamesEqual(string stored, string wanted)
    {
        if (!HashedNames)
            return string.Equals(stored, wanted, StringComparison.Ordinal);
        return string.Equals(stored.Normalize(NormalizationForm.FormD), wanted.Normalize(NormalizationForm.FormD),
            CaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    public InodeRecord ReadInode(ulong id)
    {
        byte[] key = FsKey.Prefix(id, FsKey.TypeInode);
        if (!_tree.TryFindFloor(key, FsKey.Compare, out BTreeEntry entry) || FsKey.Header(entry) != Le.U64(key, 0))
            throw new FileNotFoundException($"inode {id} introuvable");
        return InodeRecord.Parse(id, entry.Value);
    }

    public IEnumerable<DirEntry> Entries(ulong directoryId)
    {
        foreach (BTreeEntry e in Records(directoryId, FsKey.TypeDirRec))
            yield return DirRecord.Parse(e, HashedNames);
    }

    /// <summary>All records of one object and type, in key order.</summary>
    private IEnumerable<BTreeEntry> Records(ulong objId, byte type)
    {
        byte[] prefix = FsKey.Prefix(objId, type);
        ulong header = FsKey.Compose(objId, type);
        foreach (BTreeEntry e in _tree.EnumerateFrom(prefix, FsKey.Compare))
        {
            if (FsKey.Header(e) != header)
                yield break;
            yield return e;
        }
    }

    public byte[]? ReadXattr(ulong inodeId, string name)
    {
        foreach (BTreeEntry e in Records(inodeId, FsKey.TypeXattr))
        {
            if (XattrRecord.Name(e) != name)
                continue;
            byte[]? embedded = XattrRecord.Data(e, out ulong streamId, out ulong streamSize);
            if (embedded is not null)
                return embedded;
            if (streamSize > int.MaxValue)
                throw new NotSupportedException($"attribut étendu {name} trop grand");
            using Stream stream = OpenDataStream(streamId, streamSize);
            var buffer = new byte[streamSize];
            stream.ReadExactly(buffer);
            return buffer;
        }
        return null;
    }

    /// <summary>Data stream <paramref name="streamId"/> (an inode's private_id or an xattr's stream id) as a seekable stream.</summary>
    public ExtentStream OpenDataStream(ulong streamId, ulong size) =>
        new(_blocks, Records(streamId, FsKey.TypeFileExtent).Select(FileExtentRecord.Parse), checked((long)size));
}
