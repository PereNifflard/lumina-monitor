// Clean-room implementation from Apple TN1150 "HFS Plus Volume Format", section "Catalog File"
// ("Catalog File Key", "Catalog File Data", "HFSPlusBSDInfo", "Hard Links"). No code taken from any implementation.
//
// HFSPlusCatalogKey, key bytes after the UInt16 keyLength (keyLength = 6 + 2*length, min 6, max 516):
//   +0  UInt32 parentID
//   +4  HFSUniStr255 nodeName { UInt16 length; UniChar unicode[length] (UTF-16BE, decomposed) }
// HFSPlusCatalogFolder - 88 bytes:
//   +0  SInt16 recordType = 1   +2 UInt16 flags   +4 UInt32 valence   +8 UInt32 folderID
//   +12 UInt32 createDate  +16 contentModDate  +20 attributeModDate  +24 accessDate  +28 backupDate
//   +32 HFSPlusBSDInfo permissions (16)  +48 FolderInfo userInfo (16)  +64 ExtendedFolderInfo finderInfo (16)
//   +80 UInt32 textEncoding  +84 UInt32 reserved (folderCount when kHFSHasFolderCountBit is set)
// HFSPlusCatalogFile - 248 bytes:
//   +0  SInt16 recordType = 2   +2 UInt16 flags   +4 UInt32 reserved1   +8 UInt32 fileID
//   +12..+28 the five dates as above
//   +32 HFSPlusBSDInfo permissions (16)
//   +48 FileInfo userInfo (16): +48 UInt32 fdType, +52 UInt32 fdCreator, +56 UInt16 fdFlags, +58 Point, +62 UInt16
//   +64 ExtendedFileInfo finderInfo (16)  +80 UInt32 textEncoding  +84 UInt32 reserved2
//   +88 HFSPlusForkData dataFork (80)   +168 HFSPlusForkData resourceFork (80)
// HFSPlusCatalogThread: +0 SInt16 recordType (3 folder / 4 file)  +2 SInt16 reserved  +4 UInt32 parentID  +8 HFSUniStr255 nodeName
// HFSPlusBSDInfo - 16 bytes: +0 UInt32 ownerID  +4 UInt32 groupID  +8 UInt8 adminFlags  +9 UInt8 ownerFlags
//   +10 UInt16 fileMode  +12 UInt32 special (iNodeNum for hard-link files / linkCount / rawDevice)
//   ownerFlags is the low byte of BSD st_flags: UF_NODUMP 0x01, UF_IMMUTABLE 0x02, UF_APPEND 0x04,
//   UF_OPAQUE 0x08, UF_COMPRESSED 0x20 (<sys/stat.h>; the file's data lives in the com.apple.decmpfs xattr).
// Hard links (TN1150): fdType 'hlnk' + fdCreator 'hfs+', special = link reference; the content is the file
//   "iNode<ref>" in the root folder "\0\0\0\0HFS+ Private Data". Directory hard links (Apple hfs_format.h,
//   not TN1150): fdType 'fdrp' + fdCreator 'MACS', folder "dir_<ref>" in ".HFS+ Private Directory Data\r".
namespace LuminaMonitor.Formats.Hfs;

internal static class CatalogKey
{
    public const int MinLength = 6, MaxLength = 516, MaxNameUnits = 255;

    public static uint ParentId(ReadOnlySpan<byte> key) => Be.U32(key, 0);

    /// <summary>Same as <see cref="ParentId(ReadOnlySpan{byte})"/> for callers that cannot hold a span (iterators).</summary>
    public static uint KeyParentId(ReadOnlyMemory<byte> key) => Be.U32(key.Span, 0);

    /// <summary>Copies the key's name into <paramref name="destination"/> (at least 255 chars) and returns its length.</summary>
    public static int ReadName(ReadOnlySpan<byte> key, Span<char> destination)
    {
        if (key.Length < MinLength) throw new InvalidDataException("Catalog key shorter than 6 bytes.");
        int n = Be.U16(key, 4);
        if (n > MaxNameUnits || key.Length < MinLength + 2 * n)
            throw new InvalidDataException($"Catalog key name length {n} exceeds the record.");
        for (int i = 0; i < n; i++) destination[i] = (char)Be.U16(key, MinLength + 2 * i);
        return n;
    }

    public static string Name(ReadOnlySpan<byte> key) => ReadUniStr(key, 4);

    /// <summary>Reads an HFSUniStr255 (UInt16 length + UTF-16BE units) at <paramref name="offset"/>.</summary>
    public static string ReadUniStr(ReadOnlySpan<byte> s, int offset)
    {
        int n = Be.U16(s, offset);
        if (n > MaxNameUnits || s.Length < offset + 2 + 2 * n)
            throw new InvalidDataException($"Unicode string length {n} exceeds the record.");
        Span<char> buffer = stackalloc char[MaxNameUnits];
        for (int i = 0; i < n; i++) buffer[i] = (char)Be.U16(s, offset + 2 + 2 * i);
        return new string(buffer[..n]);
    }

    /// <summary>Builds the key bytes (without the keyLength field) as the B-tree comparers see them.</summary>
    public static byte[] Build(uint parentId, ReadOnlySpan<char> name)
    {
        if (name.Length > MaxNameUnits) throw new ArgumentException("HFS+ names are limited to 255 UTF-16 units.", nameof(name));
        var key = new byte[MinLength + 2 * name.Length];
        Be.WriteU32(key, 0, parentId);
        Be.WriteU16(key, 4, (ushort)name.Length);
        for (int i = 0; i < name.Length; i++) Be.WriteU16(key, MinLength + 2 * i, name[i]);
        return key;
    }
}

internal readonly record struct BsdInfo(uint OwnerId, uint GroupId, byte AdminFlags, byte OwnerFlags, ushort FileMode, uint Special)
{
    public const byte UF_COMPRESSED = 0x20;
    public const ushort S_IFMT = 0xF000, S_IFDIR = 0x4000, S_IFREG = 0x8000, S_IFLNK = 0xA000;

    public bool IsCompressed => (OwnerFlags & UF_COMPRESSED) != 0;
    public bool IsSymlink => (FileMode & S_IFMT) == S_IFLNK;

    public static BsdInfo Parse(ReadOnlySpan<byte> s) =>
        new(Be.U32(s, 0), Be.U32(s, 4), s[8], s[9], Be.U16(s, 10), Be.U32(s, 12));
}

internal readonly record struct CatalogThread(uint ParentId, string Name)
{
    public static CatalogThread? TryParse(ReadOnlyMemory<byte> data)
    {
        var d = data.Span;
        if (d.Length < 10) return null;
        short type = Be.I16(d, 0);
        if (type != CatalogEntry.FolderThreadRecord && type != CatalogEntry.FileThreadRecord) return null;
        return new CatalogThread(Be.U32(d, 4), CatalogKey.ReadUniStr(d, 8));
    }
}

internal sealed class CatalogEntry
{
    public const short FolderRecord = 1, FileRecord = 2, FolderThreadRecord = 3, FileThreadRecord = 4;
    public const int FolderRecordSize = 88, FileRecordSize = 248;
    public const uint HardLinkFileType = 0x686C6E6B, HfsPlusCreator = 0x6866732B;   // 'hlnk', 'hfs+'
    public const uint DirLinkFileType = 0x66647270, DirLinkCreator = 0x4D414353;    // 'fdrp', 'MACS'

    public uint ParentId { get; init; }
    public string Name { get; init; } = string.Empty;
    public uint Cnid { get; init; }
    public bool IsDirectory { get; init; }
    public ushort Flags { get; init; }
    public uint Valence { get; init; }
    public uint CreateDate { get; init; }
    public uint ContentModDate { get; init; }
    public BsdInfo Permissions { get; init; }
    public uint FileType { get; init; }
    public uint FileCreator { get; init; }
    public uint TextEncoding { get; init; }
    public ForkData DataFork { get; init; } = ForkData.Empty;
    public ForkData ResourceFork { get; init; } = ForkData.Empty;

    public bool IsHardLink => !IsDirectory && FileType == HardLinkFileType && FileCreator == HfsPlusCreator;
    public bool IsDirectoryHardLink => !IsDirectory && FileType == DirLinkFileType && FileCreator == DirLinkCreator;
    public bool IsCompressed => !IsDirectory && Permissions.IsCompressed;
    public bool IsSymlink => !IsDirectory && Permissions.IsSymlink;

    /// <summary>Parses a folder or file record; returns null for thread records.</summary>
    public static CatalogEntry? TryParse(ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> data)
    {
        var k = key.Span;
        var d = data.Span;
        if (d.Length < 2) throw new InvalidDataException("Catalog record too short.");
        short type = Be.I16(d, 0);
        if (type == FolderThreadRecord || type == FileThreadRecord) return null;
        uint parentId = CatalogKey.ParentId(k);
        string name = CatalogKey.Name(k);
        if (type == FolderRecord)
        {
            if (d.Length < FolderRecordSize) throw new InvalidDataException("Catalog folder record shorter than 88 bytes.");
            return new CatalogEntry
            {
                ParentId = parentId, Name = name, IsDirectory = true, Flags = Be.U16(d, 2), Valence = Be.U32(d, 4),
                Cnid = Be.U32(d, 8), CreateDate = Be.U32(d, 12), ContentModDate = Be.U32(d, 16),
                Permissions = BsdInfo.Parse(d.Slice(32, 16)), TextEncoding = Be.U32(d, 80),
            };
        }
        if (type == FileRecord)
        {
            if (d.Length < FileRecordSize) throw new InvalidDataException("Catalog file record shorter than 248 bytes.");
            return new CatalogEntry
            {
                ParentId = parentId, Name = name, IsDirectory = false, Flags = Be.U16(d, 2),
                Cnid = Be.U32(d, 8), CreateDate = Be.U32(d, 12), ContentModDate = Be.U32(d, 16),
                Permissions = BsdInfo.Parse(d.Slice(32, 16)), FileType = Be.U32(d, 48), FileCreator = Be.U32(d, 52),
                TextEncoding = Be.U32(d, 80),
                DataFork = ForkData.Parse(d.Slice(88, ForkData.Size)), ResourceFork = ForkData.Parse(d.Slice(168, ForkData.Size)),
            };
        }
        throw new InvalidDataException($"Unknown catalog record type {type}.");
    }
}
