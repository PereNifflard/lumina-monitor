// Read-only random-access byte source consumed by the HFS+ reader.
//
// NOTE: the APFS reader was expected to define an identical interface
// (LuminaMonitor.Formats.Apfs.IBlockDevice). It did not exist when this file was
// written, so the HFS+ reader carries its own copy with exactly the same shape.
// When both exist, keep one of them (or add a one-line adapter): the member
// signatures are identical on purpose.
namespace LuminaMonitor.Formats.Hfs;

internal interface IBlockDevice
{
    /// <summary>Total size of the device in bytes.</summary>
    long Length { get; }

    /// <summary>Fills <paramref name="destination"/> entirely from <paramref name="offset"/>, or throws.</summary>
    void Read(long offset, Span<byte> destination);
}

/// <summary>A byte window over another device (used for an HFS+ volume embedded in an HFS wrapper).</summary>
internal sealed class WindowBlockDevice : IBlockDevice
{
    private readonly IBlockDevice _inner;
    private readonly long _offset;

    public WindowBlockDevice(IBlockDevice inner, long offset, long length)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (offset < 0 || length < 0 || offset + length > inner.Length)
            throw new ArgumentOutOfRangeException(nameof(offset), "Window exceeds the underlying device.");
        _inner = inner;
        _offset = offset;
        Length = length;
    }

    public long Length { get; }

    public void Read(long offset, Span<byte> destination)
    {
        if (offset < 0 || offset + destination.Length > Length)
            throw new ArgumentOutOfRangeException(nameof(offset), "Read outside the device window.");
        _inner.Read(_offset + offset, destination);
    }
}
