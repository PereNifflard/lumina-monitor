using System.Buffers.Binary;
using System.Text;

namespace LuminaMonitor.Formats.Apfs;

/// <summary>
/// j_key_t helpers and the file-system record ordering (Apple File System
/// Reference, 2020-06-22, "File-System Objects" / "File-System Constants").
/// </summary>
/// <remarks>
/// j_key_t = obj_id_and_type (u64): object id in the low 60 bits
/// (OBJ_ID_MASK 0x0fffffffffffffff), record type in the high 4 bits
/// (OBJ_TYPE_SHIFT 60). Records sort by 1. object id, 2. type, 3. for
/// extended attributes and directory entries, the name. A search key made of
/// the header alone is defined to sort before every full key with the same
/// header, which is how prefix scans of one object's records start.
/// </remarks>
internal static class FsKey
{
    public const ulong ObjIdMask = 0x0FFFFFFFFFFFFFFF;
    public const int TypeShift = 60;
    public const byte TypeInode = 3, TypeXattr = 4, TypeDstreamId = 6, TypeFileExtent = 8, TypeDirRec = 9;

    public static ulong Compose(ulong objId, byte type) => ((ulong)type << TypeShift) | (objId & ObjIdMask);
    public static ulong ObjId(ulong header) => header & ObjIdMask;
    public static int Type(ulong header) => (int)(header >> TypeShift);
    public static ulong Header(BTreeEntry entry) => Le.U64(entry.Key, 0);

    public static byte[] Prefix(ulong objId, byte type)
    {
        var key = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(key, Compose(objId, type));
        return key;
    }

    public static int Compare(ReadOnlySpan<byte> stored, ReadOnlySpan<byte> search)
    {
        ulong a = Le.U64(stored, 0), b = Le.U64(search, 0);
        int c = ObjId(a).CompareTo(ObjId(b));
        if (c != 0) return c;
        c = Type(a).CompareTo(Type(b));
        if (c != 0) return c;
        if (search.Length <= 8) return stored.Length > 8 ? 1 : 0;
        if (stored.Length <= 8) return -1;
        return stored[8..].SequenceCompareTo(search[8..]); // byte-wise tie-break; this reader only issues header-only searches
    }
}

/// <summary>
/// j_inode_val_t. Offsets: parent_id 0, private_id 8, create_time 16,
/// mod_time 24, change_time 32, access_time 40, internal_flags 48,
/// nchildren/nlink 56 (i32), default_protection_class 60 (u32),
/// write_generation_counter 64, bsd_flags 68, owner 72, group 76,
/// mode 80 (u16), pad1 82, uncompressed_size 84 (u64), xfields 92.
/// xfields = xf_blob_t {xf_num_exts u16 @92, xf_used_data u16 @94, xf_data @96}:
/// an array of x_field_t {x_type u8, x_flags u8, x_size u16} followed by the
/// fields' data in the same order, each datum padded to a multiple of 8.
/// INO_EXT_TYPE_DSTREAM (8) holds a j_dstream_t {size u64 @0, alloced_size @8,
/// default_crypto_id @16, total_bytes_written @24, total_bytes_read @32}.
/// </summary>
internal sealed class InodeRecord
{
    public const int FixedSize = 92;
    public const ushort ModeTypeMask = 0xF000, ModeDirectory = 0x4000, ModeRegular = 0x8000, ModeSymlink = 0xA000;
    public const uint BsdFlagCompressed = 0x20; // UF_COMPRESSED
    public const byte XFieldName = 4, XFieldDataStream = 8;

    public ulong Id { get; private init; }
    public ulong ParentId { get; private init; }
    public ulong PrivateId { get; private init; }
    public ulong InternalFlags { get; private init; }
    public uint BsdFlags { get; private init; }
    public ushort Mode { get; private init; }
    public ulong UncompressedSize { get; private init; }
    public bool HasDataStream { get; private set; }
    public ulong DataStreamSize { get; private set; }

    public bool IsDirectory => (Mode & ModeTypeMask) == ModeDirectory;
    public bool IsSymlink => (Mode & ModeTypeMask) == ModeSymlink;
    public bool IsCompressed => (BsdFlags & BsdFlagCompressed) != 0;

    public static InodeRecord Parse(ulong id, ReadOnlySpan<byte> v)
    {
        if (v.Length < FixedSize)
            throw new InvalidDataException($"enregistrement d'inode {id} tronqué");
        var inode = new InodeRecord
        {
            Id = id,
            ParentId = Le.U64(v, 0),
            PrivateId = Le.U64(v, 8),
            InternalFlags = Le.U64(v, 48),
            BsdFlags = Le.U32(v, 68),
            Mode = Le.U16(v, 80),
            UncompressedSize = Le.U64(v, 84),
        };
        if (v.Length < FixedSize + 4)
            return inode;
        int count = Le.U16(v, 92);
        int cursor = 96 + 4 * count;
        for (int i = 0; i < count; i++)
        {
            byte type = v[96 + 4 * i];
            int size = Le.U16(v, 96 + 4 * i + 2);
            if (cursor + size > v.Length)
                throw new InvalidDataException($"champs étendus de l'inode {id} tronqués");
            if (type == XFieldDataStream && size >= 40)
            {
                inode.HasDataStream = true;
                inode.DataStreamSize = Le.U64(v, cursor);
            }
            cursor += (size + 7) & ~7;
        }
        return inode;
    }
}

internal readonly record struct DirEntry(string Name, ulong FileId, int FileType)
{
    public bool IsDirectory => FileType == DirRecord.DtDir;
}

/// <summary>
/// Directory entry records. Key: j_drec_key_t {hdr @0, name_len u16 @8, name @10}
/// or j_drec_hashed_key_t {hdr @0, name_len_and_hash u32 @8 (length in the low
/// 10 bits, hash in the high 22), name @12}; the length counts the trailing NUL.
/// Value: j_drec_val_t {file_id u64 @0, date_added u64 @8, flags u16 @16
/// (file type in the low 4 bits), xfields @18}.
/// </summary>
internal static class DirRecord
{
    public const int ValueFixedSize = 18;
    public const ushort TypeMask = 0x000F;
    public const int DtDir = 4, DtReg = 8, DtLnk = 10;

    public static DirEntry Parse(BTreeEntry e, bool hashed)
    {
        ReadOnlySpan<byte> key = e.Key, value = e.Value;
        int start = hashed ? 12 : 10;
        int len = hashed ? (int)(Le.U32(key, 8) & 0x3FF) : Le.U16(key, 8);
        if (len == 0 || start + len > key.Length || value.Length < ValueFixedSize)
            throw new InvalidDataException("entrée de répertoire invalide");
        ReadOnlySpan<byte> name = key.Slice(start, len);
        if (name[^1] == 0) name = name[..^1];
        return new DirEntry(Encoding.UTF8.GetString(name), Le.U64(value, 0), Le.U16(value, 16) & TypeMask);
    }
}

/// <summary>
/// Extended attribute records. Key: j_xattr_key_t {hdr @0, name_len u16 @8
/// (with NUL), name @10}. Value: j_xattr_val_t {flags u16 @0, xdata_len u16 @2,
/// xdata @4}; xdata is the data itself (XATTR_DATA_EMBEDDED) or a
/// j_xattr_dstream_t {xattr_obj_id u64, j_dstream_t} (XATTR_DATA_STREAM).
/// </summary>
internal static class XattrRecord
{
    public const ushort FlagDataStream = 0x1, FlagEmbedded = 0x2, FlagFileSystemOwned = 0x4;

    public static string Name(BTreeEntry e)
    {
        ReadOnlySpan<byte> key = e.Key;
        int len = Le.U16(key, 8);
        if (len == 0 || 10 + len > key.Length)
            throw new InvalidDataException("clé d'attribut étendu invalide");
        ReadOnlySpan<byte> name = key.Slice(10, len);
        if (name[^1] == 0) name = name[..^1];
        return Encoding.UTF8.GetString(name);
    }

    /// <summary>Embedded data, or null with the stream id and size when the data lives in a data stream.</summary>
    public static byte[]? Data(BTreeEntry e, out ulong streamId, out ulong streamSize)
    {
        ReadOnlySpan<byte> v = e.Value;
        streamId = 0;
        streamSize = 0;
        if (v.Length < 4)
            throw new InvalidDataException("valeur d'attribut étendu tronquée");
        ushort flags = Le.U16(v, 0);
        int len = Le.U16(v, 2);
        if ((flags & FlagEmbedded) != 0)
        {
            if (4 + len > v.Length)
                throw new InvalidDataException("attribut étendu embarqué tronqué");
            return v.Slice(4, len).ToArray();
        }
        if ((flags & FlagDataStream) != 0)
        {
            if (v.Length < 4 + 48)
                throw new InvalidDataException("j_xattr_dstream_t tronqué");
            streamId = Le.U64(v, 4);
            streamSize = Le.U64(v, 12);
            return null;
        }
        throw new InvalidDataException("attribut étendu sans mode de stockage");
    }
}

/// <summary>
/// File extent records. Key: j_file_extent_key_t {hdr @0, logical_addr u64 @8};
/// the object id is the data stream's id (the inode's private_id, or an
/// xattr's xattr_obj_id). Value: j_file_extent_val_t {len_and_flags u64 @0
/// (length in the low 56 bits), phys_block_num u64 @8, crypto_id u64 @16}.
/// </summary>
internal static class FileExtentRecord
{
    public const ulong LenMask = 0x00FFFFFFFFFFFFFF;

    public static ExtentStream.Extent Parse(BTreeEntry e)
    {
        if (e.Key.Length < 16 || e.Value.Length < 24)
            throw new InvalidDataException("enregistrement d'extent tronqué");
        return new ExtentStream.Extent(Le.U64(e.Key, 8), Le.U64(e.Value, 0) & LenMask, Le.U64(e.Value, 8));
    }
}

/// <summary>
/// The 22-bit name hash of j_drec_hashed_key_t: NFD-normalised name →
/// UTF-32 code points (terminator excluded) → CRC-32C → complement → low 22
/// bits. Standard CRC-32C already ends with a complement, so the two cancel
/// and the raw register (initial value 0xFFFFFFFF, no final XOR) is used, as
/// the spec itself notes. Case-insensitive volumes fold case before hashing.
/// </summary>
internal static class DirectoryEntryHash
{
    private static readonly uint[] Table = BuildTable();

    public static uint Compute(string name, bool foldCase)
    {
        string normalized = name.Normalize(NormalizationForm.FormD);
        if (foldCase) normalized = normalized.ToLowerInvariant();
        uint crc = 0xFFFFFFFF;
        foreach (Rune rune in normalized.EnumerateRunes())
        {
            uint cp = (uint)rune.Value;
            for (int i = 0; i < 4; i++)
                crc = Table[(crc ^ (cp >> (8 * i))) & 0xFF] ^ (crc >> 8);
        }
        return crc & 0x3FFFFF;
    }

    /// <summary>Standard CRC-32C (Castagnoli, reflected, init and final XOR 0xFFFFFFFF).</summary>
    public static uint Crc32C(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0x82F63B78 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}
