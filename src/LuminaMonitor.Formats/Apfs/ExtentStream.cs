namespace LuminaMonitor.Formats.Apfs;

/// <summary>
/// Read-only, seekable view of a data stream assembled from its file-extent
/// records in logical order. Gaps between extents and extents whose physical
/// block number is zero (sparse files) read as zeros.
/// </summary>
internal sealed class ExtentStream : Stream
{
    internal readonly record struct Extent(ulong LogicalStart, ulong Length, ulong PhysicalBlock);

    private readonly BlockSource _blocks;
    private readonly Extent[] _extents; // sorted by LogicalStart, non-overlapping
    private readonly long _length;
    private long _position;

    public ExtentStream(BlockSource blocks, IEnumerable<Extent> extents, long length)
    {
        _blocks = blocks;
        _extents = extents.OrderBy(e => e.LogicalStart).ToArray();
        _length = length;
        for (int i = 1; i < _extents.Length; i++)
            if (_extents[i].LogicalStart < _extents[i - 1].LogicalStart + _extents[i - 1].Length)
                throw new InvalidDataException("extents de fichier chevauchants");
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => _position = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> destination)
    {
        if (_position >= _length)
            return 0;
        int want = (int)Math.Min(destination.Length, _length - _position);
        int done = 0;
        while (done < want)
        {
            long pos = _position + done;
            int i = LastExtentStartingAtOrBefore(pos);
            int chunk;
            if (i >= 0 && (ulong)pos < _extents[i].LogicalStart + _extents[i].Length)
            {
                Extent e = _extents[i];
                long within = pos - (long)e.LogicalStart;
                chunk = (int)Math.Min(want - done, (long)e.Length - within);
                Span<byte> target = destination.Slice(done, chunk);
                if (e.PhysicalBlock == 0)
                    target.Clear();
                else
                    _blocks.Device.Read(checked((long)e.PhysicalBlock * _blocks.BlockSize + within), target);
            }
            else
            {
                long next = i + 1 < _extents.Length ? (long)_extents[i + 1].LogicalStart : _length;
                chunk = (int)Math.Min(want - done, next - pos);
                destination.Slice(done, chunk).Clear();
            }
            done += chunk;
        }
        _position += done;
        return done;
    }

    private int LastExtentStartingAtOrBefore(long pos)
    {
        int lo = 0, hi = _extents.Length - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if ((long)_extents[mid].LogicalStart <= pos) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long basis = origin switch
        {
            SeekOrigin.Begin => 0,
            SeekOrigin.Current => _position,
            SeekOrigin.End => _length,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        return Position = basis + offset;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
