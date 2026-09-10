namespace LuminaMonitor.Formats;

/// <summary>
/// One partition of a disk image, seen as a flat block device by the
/// filesystem readers. Reads are served from the image's run cache, so
/// random access into a compressed image costs one run decompression at
/// most per distinct region touched.
/// </summary>
internal sealed class UdifBlockDevice : Apfs.IBlockDevice, Hfs.IBlockDevice
{
    private const int SectorSize = 512;
    private readonly UdifImage _image;
    private readonly long _firstSector;

    public UdifBlockDevice(UdifImage image, long firstSector, long sectorCount)
    {
        _image = image;
        _firstSector = firstSector;
        Length = sectorCount * SectorSize;
    }

    public long Length { get; }

    public void Read(long offset, Span<byte> destination)
    {
        if (offset < 0 || offset + destination.Length > Length)
            throw new ArgumentOutOfRangeException(nameof(offset), "lecture hors de la partition");

        // Sector-align the request; the readers ask for whole blocks anyway.
        long firstSector = offset / SectorSize;
        int skip = (int)(offset % SectorSize);
        int sectors = (int)((skip + destination.Length + SectorSize - 1) / SectorSize);
        byte[] buffer = new byte[sectors * SectorSize];
        _image.ReadSectors(_firstSector + firstSector, sectors, buffer);
        buffer.AsSpan(skip, destination.Length).CopyTo(destination);
    }
}
