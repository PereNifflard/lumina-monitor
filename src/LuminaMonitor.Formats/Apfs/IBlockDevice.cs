namespace LuminaMonitor.Formats.Apfs;

/// <summary>
/// Random-access, read-only byte source backing an APFS container: a raw
/// partition, a file, or a decoded DMG partition adapted by another component.
/// </summary>
internal interface IBlockDevice
{
    /// <summary>Total size in bytes.</summary>
    long Length { get; }

    /// <summary>
    /// Fills <paramref name="destination"/> entirely with the bytes starting at
    /// <paramref name="offset"/>; implementations throw if the range is outside
    /// the device rather than returning a short read.
    /// </summary>
    void Read(long offset, Span<byte> destination);
}
