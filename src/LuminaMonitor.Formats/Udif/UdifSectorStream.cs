namespace LuminaMonitor.Formats;

/// <summary>
/// Read-only, seekable view over a sector range of a <see cref="UdifImage"/>,
/// so file-system parsers can consume a partition as an ordinary stream.
/// </summary>
/// <remarks>
/// Reads aligned on whole sectors go straight into the caller's buffer; the
/// unaligned head and tail of a request pass through a one-sector scratch
/// buffer. Disposing the stream does not dispose the image, which may be
/// shared by several partitions.
/// </remarks>
internal sealed class UdifSectorStream : Stream
{
    private const int SectorSize = UdifImage.SectorSize;

    private readonly UdifImage _image;
    private readonly long _firstSector;
    private readonly long _length;
    private readonly byte[] _scratch = new byte[SectorSize];
    private long _position;

    internal UdifSectorStream(UdifImage image, long firstSector, long sectorCount)
    {
        _image = image;
        _firstSector = firstSector;
        _length = checked(sectorCount * SectorSize);
    }

    public override int Read(Span<byte> buffer)
    {
        if (_position >= _length)
            return 0;

        int want = (int)Math.Min(buffer.Length, _length - _position);
        int done = 0;
        while (done < want)
        {
            long sector = _firstSector + _position / SectorSize;
            int offset = (int)(_position % SectorSize);
            int remaining = want - done;

            if (offset == 0 && remaining >= SectorSize)
            {
                int sectors = remaining / SectorSize;
                int bytes = sectors * SectorSize;
                _image.ReadSectors(sector, sectors, buffer.Slice(done, bytes));
                done += bytes;
                _position += bytes;
            }
            else
            {
                _image.ReadSectors(sector, 1, _scratch);
                int bytes = Math.Min(SectorSize - offset, remaining);
                _scratch.AsSpan(offset, bytes).CopyTo(buffer.Slice(done, bytes));
                done += bytes;
                _position += bytes;
            }
        }
        return done;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        if (target < 0)
            throw new IOException("seek before the start of the stream");
        _position = target;
        return _position;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
