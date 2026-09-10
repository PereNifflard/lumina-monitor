// Clean-room implementation from Apple TN1150 "HFS Plus Volume Format", sections "Fork Data Structure"
// and "Extents Overflow File Usage": a fork is described by up to eight extents kept in its catalog
// (or volume header / attribute) record; further extents are fetched, eight at a time, from the extents
// overflow B-tree (or from kHFSPlusAttrExtents records for attribute forks) keyed by the fork-relative
// block where each record starts.
namespace LuminaMonitor.Formats.Hfs;

/// <summary>Fetches the extent record starting at fork block <paramref name="forkStartBlock"/> (see <see cref="ExtentsOverflow.Lookup"/>).</summary>
internal delegate ExtentDescriptor[]? ExtentOverflowResolver(uint forkStartBlock, out uint recordStartBlock);

/// <summary>Maps fork-relative blocks to allocation blocks, extending itself lazily through the overflow resolver.</summary>
internal sealed class ForkMap
{
    private readonly List<ExtentDescriptor> _extents = new();
    private readonly List<long> _firstForkBlock = new();
    private readonly ExtentOverflowResolver? _overflow;
    private readonly long _totalBlocks;
    private long _covered;
    private bool _exhausted;

    public ForkMap(IEnumerable<ExtentDescriptor> inlineExtents, long totalBlocks, ExtentOverflowResolver? overflow)
    {
        ArgumentNullException.ThrowIfNull(inlineExtents);
        _overflow = overflow;
        _totalBlocks = totalBlocks;
        foreach (var extent in inlineExtents)
        {
            if (extent.IsEmpty) break;
            Append(extent);
        }
    }

    public bool TryMap(long forkBlock, out uint physicalBlock, out long contiguousBlocks)
    {
        while (forkBlock >= _covered && Extend()) { }
        if (forkBlock < 0 || forkBlock >= _covered)
        {
            physicalBlock = 0;
            contiguousBlocks = 0;
            return false;
        }
        int lo = 0, hi = _extents.Count - 1;
        while (lo < hi)
        {
            int mid = lo + (hi - lo + 1) / 2;
            if (_firstForkBlock[mid] <= forkBlock) lo = mid; else hi = mid - 1;
        }
        long offset = forkBlock - _firstForkBlock[lo];
        physicalBlock = (uint)(_extents[lo].StartBlock + offset);
        contiguousBlocks = _extents[lo].BlockCount - offset;
        return true;
    }

    private void Append(ExtentDescriptor extent)
    {
        _extents.Add(extent);
        _firstForkBlock.Add(_covered);
        _covered += extent.BlockCount;
    }

    private bool Extend()
    {
        if (_exhausted || _overflow is null || _covered >= _totalBlocks || _covered > uint.MaxValue)
        {
            _exhausted = true;
            return false;
        }
        var record = _overflow((uint)_covered, out uint recordStart);
        if (record is null)
        {
            _exhausted = true;
            return false;
        }
        if (recordStart != _covered)
            throw new InvalidDataException($"Extents overflow record starts at fork block {recordStart}, expected {_covered}.");
        int before = _extents.Count;
        foreach (var extent in record)
        {
            if (extent.IsEmpty) break;
            Append(extent);
        }
        if (_extents.Count == before)
        {
            _exhausted = true;
            return false;
        }
        return true;
    }
}

/// <summary>Seekable read-only stream over one fork of a file.</summary>
internal sealed class ForkStream : Stream
{
    private readonly IBlockDevice _device;
    private readonly ForkMap _map;
    private readonly uint _blockSize;
    private readonly long _length;
    private long _position;

    public ForkStream(IBlockDevice device, uint blockSize, long logicalSize, ForkMap map)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(map);
        if (blockSize == 0) throw new ArgumentOutOfRangeException(nameof(blockSize));
        _device = device;
        _blockSize = blockSize;
        _length = logicalSize;
        _map = map;
    }

    public static ForkStream Open(IBlockDevice device, uint blockSize, ForkData fork, ExtentOverflowResolver? overflow)
    {
        ArgumentNullException.ThrowIfNull(fork);
        long needed = Math.Max(fork.TotalBlocks, (fork.LogicalSize + blockSize - 1) / blockSize);
        return new ForkStream(device, blockSize, fork.LogicalSize, new ForkMap(fork.Extents, needed, overflow));
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

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        int total = 0;
        while (buffer.Length > 0 && _position < _length)
        {
            long block = _position / _blockSize;
            int inBlock = (int)(_position % _blockSize);
            if (!_map.TryMap(block, out uint physical, out long contiguous))
                throw new InvalidDataException($"Fork block {block} has no extent (missing extents overflow record).");
            long available = contiguous * _blockSize - inBlock;
            int n = (int)Math.Min(buffer.Length, Math.Min(available, _length - _position));
            _device.Read((long)physical * _blockSize + inBlock, buffer.Slice(0, n));
            _position += n;
            total += n;
            buffer = buffer.Slice(n);
        }
        return total;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        if (target < 0) throw new IOException("Cannot seek before the beginning of the fork.");
        _position = target;
        return target;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
