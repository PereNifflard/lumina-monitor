using System.Buffers.Binary;

namespace LuminaMonitor.Formats;

/// <summary>
/// Reader for Apple's pbzx stream: the payload format inside a XIP.
/// </summary>
/// <remarks>
/// After the four magic bytes and a 64-bit flags word, the stream is a run
/// of chunks, each announced by a 64-bit flags word and a 64-bit length. A
/// chunk whose bytes start with the xz magic is a complete xz stream
/// (typically 16 MiB once decompressed); any other chunk is stored raw. The
/// run ends when a chunk's flags no longer carry bit 0x01000000. Everything
/// is big-endian. The decompressed chunks concatenate into a cpio archive.
/// </remarks>
public sealed class PbzxStream : Stream
{
    private static readonly byte[] XzMagic = [0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00];
    private const ulong MoreChunks = 0x01000000;

    private readonly Stream _inner;
    private Stream? _chunk;
    private long _chunkEnd = -1;
    private bool _more = true;
    private long _position;

    public PbzxStream(Stream inner)
    {
        _inner = inner;
        Span<byte> head = stackalloc byte[12];
        _inner.ReadExactly(head);
        if (!head[..4].SequenceEqual("pbzx"u8))
            throw new InvalidDataException("pas un flux pbzx");
        _more = (BinaryPrimitives.ReadUInt64BigEndian(head[4..]) & MoreChunks) != 0;
    }

    /// <summary>Number of chunks opened so far — progress for a multi-gigabyte unpack.</summary>
    public int ChunksRead { get; private set; }

    private bool NextChunk()
    {
        _chunk?.Dispose();
        _chunk = null;
        if (!_more)
            return false;

        // A decoder may stop short of its window's end (xz padding, index,
        // footer); the next chunk header sits exactly at the end of the
        // previous chunk, wherever the decoder left the underlying stream.
        if (_chunkEnd >= 0)
            _inner.Position = _chunkEnd;

        Span<byte> head = stackalloc byte[16];
        _inner.ReadExactly(head);
        ulong flags = BinaryPrimitives.ReadUInt64BigEndian(head);
        long length = (long)BinaryPrimitives.ReadUInt64BigEndian(head[8..]);
        _more = (flags & MoreChunks) != 0;

        // Peek at the chunk's first bytes to tell xz from raw without
        // consuming them: the chunk is handed over as a bounded window.
        long start = _inner.Position;
        _chunkEnd = start + length;
        Span<byte> magic = stackalloc byte[6];
        int peeked = _inner.Read(magic);
        _inner.Position = start;
        var window = new SubStream(_inner, start, length);
        _chunk = peeked == 6 && magic.SequenceEqual(XzMagic) ? new XzStream(window) : window;
        ChunksRead++;
        return true;
    }

    public override int Read(Span<byte> buffer)
    {
        while (true)
        {
            if (_chunk is null && !NextChunk())
                return 0;
            int read = _chunk!.Read(buffer);
            if (read > 0)
            {
                _position += read;
                return read;
            }
            _chunk.Dispose();
            _chunk = null;
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _position; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _chunk?.Dispose();
        base.Dispose(disposing);
    }
}
