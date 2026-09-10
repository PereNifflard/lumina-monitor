namespace LuminaMonitor.Formats.Apfs;

/// <summary>
/// Read-only access to one volume of an unencrypted APFS container, written
/// from Apple's published "Apple File System Reference" (2020-06-22,
/// developer.apple.com/support/downloads/Apple-File-System-Reference.pdf).
/// </summary>
/// <remarks>
/// Opening follows the reference's mount procedure: block 0 → checkpoint
/// descriptor area → latest checksummed container superblock → container
/// object map → the volume's apfs_superblock_t (nx_fs_oid[i], virtual) → the
/// volume's own object map → its file-system B-tree.
/// </remarks>
internal sealed class ApfsVolume
{
    private readonly Volume _volume;

    public ContainerSuperblock Container { get; }
    public string Name => _volume.Name;
    public bool CaseInsensitive => _volume.CaseInsensitive;

    private ApfsVolume(ContainerSuperblock container, Volume volume)
    {
        Container = container;
        _volume = volume;
    }

    /// <summary>Opens volume number <paramref name="volumeIndex"/> (nx_fs_oid[volumeIndex]) of the container on <paramref name="device"/>.</summary>
    public static ApfsVolume Open(IBlockDevice device, int volumeIndex = 0)
    {
        ContainerSuperblock container = ContainerSuperblock.ReadLatest(device, out BlockSource blocks);
        if ((uint)volumeIndex >= ContainerSuperblock.MaxFileSystems || container.FsOids[volumeIndex] == 0)
            throw new InvalidDataException($"le conteneur n'a pas de volume à l'index {volumeIndex}");
        var containerOmap = new ObjectMap(blocks, container.OmapOid);
        ulong superblockPaddr = containerOmap.Lookup(container.FsOids[volumeIndex], container.Xid);
        return new ApfsVolume(container, new Volume(blocks, superblockPaddr, container.Xid));
    }

    /// <summary>Entries of the directory at <paramref name="path"/> ("/" separated, root = "/").</summary>
    public IEnumerable<(string Name, bool IsDirectory, ulong FileId)> ListDirectory(string path) => _volume.ListDirectory(path);

    /// <summary>Logical size in bytes of the file at <paramref name="path"/> (decompressed size for decmpfs files).</summary>
    public long FileSize(string path) => _volume.FileSize(path);

    /// <summary>Opens the file at <paramref name="path"/> for reading; decmpfs files are decoded in memory.</summary>
    public Stream OpenFile(string path) => _volume.OpenFile(path);
}
