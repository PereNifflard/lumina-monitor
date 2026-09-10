// Read-only HFS+ / HFSX volume facade. Clean-room implementation from Apple TN1150 "HFS Plus Volume
// Format" (volume header, catalog, extents overflow, attributes, hard links, HFS wrapper) plus the decmpfs
// layout documented in Decmpfs.cs. Paths use '/' separators and are resolved from the root folder (CNID 2);
// names are matched the way the volume itself does (case-insensitive on 'H+', binary on case-sensitive HFSX)
// after canonical decomposition. Hard links (files and directories) are followed transparently; the two
// private metadata folders are hidden from root listings, as TN1150 recommends. Symbolic links are NOT
// followed: OpenFile() on a symlink returns its target path (the data fork), see CatalogEntry.IsSymlink.
namespace LuminaMonitor.Formats.Hfs;

internal sealed class HfsVolume
{
    public const string FileLinkFolderName = "\0\0\0\0HFS+ Private Data";
    public const string DirLinkFolderName = ".HFS+ Private Directory Data\r";

    private readonly IBlockDevice _device;
    private readonly Catalog _catalog;
    private readonly ExtentsOverflow _extents;
    private readonly AttributesFile? _attributes;
    private CatalogEntry? _root;
    private uint? _fileLinkFolder;
    private uint? _dirLinkFolder;

    public VolumeHeader Header { get; }
    public bool IsCaseSensitive => _catalog.CaseSensitive;
    public string VolumeName => Root.Name;

    public static HfsVolume Open(IBlockDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        device = UnwrapHfsWrapper(device);
        if (device.Length < VolumeHeader.Offset + VolumeHeader.Size)
            throw new InvalidDataException("Device too small to hold an HFS+ volume header.");
        Span<byte> raw = stackalloc byte[VolumeHeader.Size];
        device.Read(VolumeHeader.Offset, raw);
        return new HfsVolume(device, VolumeHeader.Parse(raw));
    }

    private HfsVolume(IBlockDevice device, VolumeHeader header)
    {
        _device = device;
        Header = header;
        uint blockSize = header.BlockSize;
        // The extents overflow file cannot overflow itself: its eight header extents must suffice (TN1150).
        _extents = new ExtentsOverflow(new BTreeFile(ForkStream.Open(device, blockSize, header.ExtentsFile, null)));
        var catalogTree = new BTreeFile(ForkStream.Open(device, blockSize, header.CatalogFile,
            _extents.ResolverFor(VolumeHeader.CatalogFileId, ExtentsOverflow.DataFork)));
        _catalog = new Catalog(catalogTree, header.IsHfsx);
        if (header.AttributesFile.LogicalSize > 0)
        {
            var attributesTree = new BTreeFile(ForkStream.Open(device, blockSize, header.AttributesFile,
                _extents.ResolverFor(VolumeHeader.AttributesFileId, ExtentsOverflow.DataFork)));
            _attributes = new AttributesFile(attributesTree, device, blockSize);
        }
    }

    /// <summary>Entries of a folder. Size is the (decompressed) data fork size for files and 0 for folders.</summary>
    public IEnumerable<(string Name, bool IsDirectory, uint Cnid, long Size)> ListDirectory(string path)
    {
        var folder = TryResolve(path) ?? throw new FileNotFoundException($"'{path}' was not found on the HFS+ volume.");
        if (!folder.IsDirectory) throw new IOException($"'{path}' is not a directory.");
        foreach (var child in _catalog.List(folder.Cnid))
        {
            if (folder.Cnid == Catalog.RootFolderId && IsPrivateMetadataFolder(child)) continue;
            var target = FollowHardLink(child);
            yield return (child.Name, target.IsDirectory, target.Cnid, target.IsDirectory ? 0 : FileSize(target));
        }
    }

    public long FileSize(string path) => FileSize(RequireFile(path));

    /// <summary>Data fork of a file, transparently decompressed when the file is decmpfs-compressed.</summary>
    public Stream OpenFile(string path)
    {
        var file = RequireFile(path);
        if (file.IsCompressed)
        {
            var attribute = ReadDecmpfs(file);
            if (attribute is not null)
                return Decmpfs.Open(attribute, () => OpenFork(file, ExtentsOverflow.ResourceFork));
        }
        return OpenFork(file, ExtentsOverflow.DataFork);
    }

    /// <summary>Raw resource fork (for a decmpfs type-4 file this is the compressed container).</summary>
    public Stream OpenResourceFork(string path) => OpenFork(RequireFile(path), ExtentsOverflow.ResourceFork);

    public IEnumerable<string> ExtendedAttributeNames(string path)
    {
        var entry = TryResolve(path) ?? throw new FileNotFoundException($"'{path}' was not found on the HFS+ volume.");
        return _attributes?.Names(entry.Cnid) ?? Enumerable.Empty<string>();
    }

    public byte[]? ReadExtendedAttribute(string path, string name)
    {
        var entry = TryResolve(path) ?? throw new FileNotFoundException($"'{path}' was not found on the HFS+ volume.");
        return _attributes?.Read(entry.Cnid, name);
    }

    /// <summary>Catalog entry for a path (hard links followed), or null when a component is missing.</summary>
    public CatalogEntry? TryResolve(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var current = Root;
        var ancestors = new Stack<CatalogEntry>();
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (ancestors.Count > 0) current = ancestors.Pop();
                continue;
            }
            if (!current.IsDirectory) return null;
            var child = _catalog.Find(current.Cnid, part);
            if (child is null) return null;
            ancestors.Push(current);
            current = FollowHardLink(child);
        }
        return current;
    }

    private CatalogEntry Root => _root ??= new CatalogEntry
    {
        Cnid = Catalog.RootFolderId,
        ParentId = Catalog.RootParentId,
        IsDirectory = true,
        Name = _catalog.Thread(Catalog.RootFolderId)?.Name ?? string.Empty,
    };

    private CatalogEntry RequireFile(string path)
    {
        var entry = TryResolve(path) ?? throw new FileNotFoundException($"'{path}' was not found on the HFS+ volume.");
        if (entry.IsDirectory) throw new IOException($"'{path}' is a directory.");
        return entry;
    }

    private long FileSize(CatalogEntry file)
    {
        if (file.IsCompressed)
        {
            var attribute = ReadDecmpfs(file);
            if (attribute is not null) return Decmpfs.ParseHeader(attribute).UncompressedSize;
        }
        return file.DataFork.LogicalSize;
    }

    private byte[]? ReadDecmpfs(CatalogEntry file) => _attributes?.Read(file.Cnid, Decmpfs.AttributeName);

    private ForkStream OpenFork(CatalogEntry file, byte forkType)
    {
        var fork = forkType == ExtentsOverflow.ResourceFork ? file.ResourceFork : file.DataFork;
        return ForkStream.Open(_device, Header.BlockSize, fork, _extents.ResolverFor(file.Cnid, forkType));
    }

    private CatalogEntry FollowHardLink(CatalogEntry entry)
    {
        if (entry.IsHardLink)
        {
            uint folder = MetadataFolder(ref _fileLinkFolder, FileLinkFolderName);
            return (folder == 0 ? null : _catalog.Find(folder, "iNode" + entry.Permissions.Special))
                ?? throw new FileNotFoundException($"Hard link '{entry.Name}' points to the missing indirect node file iNode{entry.Permissions.Special}.");
        }
        if (entry.IsDirectoryHardLink)
        {
            uint folder = MetadataFolder(ref _dirLinkFolder, DirLinkFolderName);
            return (folder == 0 ? null : _catalog.Find(folder, "dir_" + entry.Permissions.Special))
                ?? throw new FileNotFoundException($"Directory hard link '{entry.Name}' points to the missing folder dir_{entry.Permissions.Special}.");
        }
        return entry;
    }

    private uint MetadataFolder(ref uint? cache, string name)
    {
        if (cache is null)
        {
            var folder = _catalog.Find(Catalog.RootFolderId, name);
            cache = folder is { IsDirectory: true } ? folder.Cnid : 0u;
        }
        return cache.Value;
    }

    private static bool IsPrivateMetadataFolder(CatalogEntry entry) =>
        entry.IsDirectory && (entry.Name == FileLinkFolderName || entry.Name == DirLinkFolderName);

    /// <summary>
    /// TN1150 "HFS Wrapper": a classic HFS master directory block ('BD' at offset 1024) whose drEmbedSigWord
    /// (MDB+0x7C) is 'H+' embeds an HFS+ volume at drAlBlSt (MDB+0x1C, in 512-byte sectors) plus
    /// drEmbedExtent.startBlock (MDB+0x7E) * drAlBlkSiz (MDB+0x14); its length is blockCount (MDB+0x80) * drAlBlkSiz.
    /// </summary>
    private static IBlockDevice UnwrapHfsWrapper(IBlockDevice device)
    {
        if (device.Length < VolumeHeader.Offset + VolumeHeader.Size) return device;
        Span<byte> mdb = stackalloc byte[VolumeHeader.Size];
        device.Read(VolumeHeader.Offset, mdb);
        if (Be.U16(mdb, 0) != VolumeHeader.SignatureHfs || Be.U16(mdb, 0x7C) != VolumeHeader.SignatureHfsPlus) return device;
        long allocationBlockSize = Be.U32(mdb, 0x14);
        long offset = Be.U16(mdb, 0x1C) * 512L + Be.U16(mdb, 0x7E) * allocationBlockSize;
        long length = Be.U16(mdb, 0x80) * allocationBlockSize;
        if (allocationBlockSize == 0 || length == 0 || offset + length > device.Length)
            throw new InvalidDataException("HFS wrapper describes an embedded HFS+ volume outside the device.");
        return new WindowBlockDevice(device, offset, length);
    }
}
