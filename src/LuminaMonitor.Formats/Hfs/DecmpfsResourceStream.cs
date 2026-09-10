// Seekable view of a decmpfs type-4 file (zlib chunks stored in the resource fork); layout in Decmpfs.cs.
using System.Buffers.Binary;

namespace LuminaMonitor.Formats.Hfs;

internal sealed class DecmpfsResourceStream : Stream
{
    private const int ResourceHeaderSize = 16;

    private readonly Stream _resourceFork;
    private readonly long _length;
    private readonly long _tableBase;
    private readonly (uint Offset, uint Size)[] _chunks;
    private long _position;
    private int _cachedIndex = -1;
    private byte[] _cached = Array.Empty<byte>();

    public DecmpfsResourceStream(Stream resourceFork, long uncompressedSize)
    {
        ArgumentNullException.ThrowIfNull(resourceFork);
        if (!resourceFork.CanSeek) throw new ArgumentException("The resource fork stream must be seekable.", nameof(resourceFork));
        if (uncompressedSize < 0) throw new ArgumentOutOfRangeException(nameof(uncompressedSize));
        _resourceFork = resourceFork;
        _length = uncompressedSize;
        long chunkCount = (uncompressedSize + Decmpfs.ChunkSize - 1) / Decmpfs.ChunkSize;
        if (chunkCount > int.MaxValue / 8) throw new NotSupportedException("decmpfs file too large.");
        _chunks = new (uint, uint)[chunkCount];
        if (chunkCount == 0) return;

        Span<byte> header = stackalloc byte[ResourceHeaderSize];
        ReadAt(0, header);
        uint dataOffset = Be.U32(header, 0);
        _tableBase = dataOffset + 4L; // skip the UInt32 BE length of the resource data
        Span<byte> countBytes = stackalloc byte[4];
        ReadAt(_tableBase, countBytes);
        uint declared = BinaryPrimitives.ReadUInt32LittleEndian(countBytes);
        if (declared < chunkCount)
            throw new InvalidDataException($"decmpfs chunk table has {declared} entries; {chunkCount} are needed for {uncompressedSize} bytes.");
        var table = new byte[8 * chunkCount];
        ReadAt(_tableBase + 4, table);
        for (int i = 0; i < chunkCount; i++)
        {
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(8 * i));
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(8 * i + 4));
            if (size == 0 || size > Decmpfs.MaxChunkBytes)
                throw new InvalidDataException($"decmpfs chunk {i} has an implausible stored size of {size} bytes.");
            _chunks[i] = (offset, size);
        }
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
            int index = (int)(_position / Decmpfs.ChunkSize);
            byte[] chunk = GetChunk(index);
            int inChunk = (int)(_position % Decmpfs.ChunkSize);
            int n = Math.Min(buffer.Length, chunk.Length - inChunk);
            if (n <= 0) throw new InvalidDataException($"decmpfs chunk {index} decoded shorter than expected.");
            chunk.AsSpan(inChunk, n).CopyTo(buffer);
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
        if (target < 0) throw new IOException("Cannot seek before the beginning of the file.");
        _position = target;
        return target;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _resourceFork.Dispose();
        base.Dispose(disposing);
    }

    private byte[] GetChunk(int index)
    {
        if (index == _cachedIndex) return _cached;
        var (offset, size) = _chunks[index];
        var stored = new byte[size];
        ReadAt(_tableBase + offset, stored);
        int expected = (int)Math.Min(Decmpfs.ChunkSize, _length - (long)index * Decmpfs.ChunkSize);
        _cached = Decmpfs.DecodeChunk(stored, expected);
        _cachedIndex = index;
        return _cached;
    }

    private void ReadAt(long offset, Span<byte> destination)
    {
        _resourceFork.Position = offset;
        _resourceFork.ReadExactly(destination);
    }
}
