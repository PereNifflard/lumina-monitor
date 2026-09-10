using System.Globalization;
using System.Text;

namespace LuminaMonitor.Formats;

/// <summary>
/// Forward-only reader for cpio archives in the portable ASCII ("odc",
/// magic 070707) and SVR4 ("newc", magic 070701) flavours — the archive a
/// XIP's payload decompresses into.
/// </summary>
/// <remarks>
/// The archive is several gigabytes and is read once, in order, from a
/// decompressing stream that cannot seek; so entries are visited
/// sequentially and the data of entries nobody wants is skipped by reading
/// it through. Only the one file this project needs is ever copied out.
/// </remarks>
public sealed class CpioReader
{
    private readonly Stream _inner;
    private long _remaining;   // unread data bytes of the current entry
    private int _padding;      // trailing pad after the current entry (newc)

    public sealed record Entry(string Name, long Size, uint Mode);

    public CpioReader(Stream inner) => _inner = inner;

    /// <summary>Advances to the next entry; null at the trailer.</summary>
    public Entry? Next()
    {
        SkipCurrent();
        Span<byte> magic = stackalloc byte[6];
        if (!ReadFully(magic))
            return null;
        string m = Encoding.ASCII.GetString(magic);
        Entry entry = m switch
        {
            "070707" => ReadOdc(),
            "070701" or "070702" => ReadNewc(),
            _ => throw new InvalidDataException($"magie cpio inconnue : {m}"),
        };
        if (entry.Name == "TRAILER!!!")
            return null;
        return entry;
    }

    /// <summary>The current entry's data, to be consumed before calling <see cref="Next"/>.</summary>
    public Stream OpenCurrent() => new EntryStream(this);

    private Entry ReadOdc()
    {
        Span<byte> fields = stackalloc byte[70];      // 6*6 + 11 + 6 + 11 after the magic
        ReadExactly(fields);
        uint mode = Octal(fields[6..12]);
        int nameSize = (int)Octal(fields[53..59]);
        long fileSize = Octal(fields[59..70]);
        string name = ReadName(nameSize);
        _remaining = fileSize;
        _padding = 0;
        return new Entry(name, fileSize, mode);
    }

    private Entry ReadNewc()
    {
        Span<byte> fields = stackalloc byte[104];     // 13 hex fields of 8 chars
        ReadExactly(fields);
        uint mode = Hex(fields[8..16]);
        long fileSize = Hex(fields[48..56]);
        int nameSize = (int)Hex(fields[88..96]);
        string name = ReadName(nameSize);
        // Header (6 + 104) + name is padded to a multiple of 4, and so is the data.
        int headerPad = (4 - (110 + nameSize) % 4) % 4;
        Skip(headerPad);
        _remaining = fileSize;
        _padding = (int)((4 - fileSize % 4) % 4);
        return new Entry(name, fileSize, mode);
    }

    private string ReadName(int size)
    {
        byte[] name = new byte[size];
        ReadExactly(name);
        int end = Array.IndexOf(name, (byte)0);
        return Encoding.UTF8.GetString(name, 0, end < 0 ? size : end);
    }

    private void SkipCurrent()
    {
        Skip(_remaining);
        Skip(_padding);
        _remaining = 0;
        _padding = 0;
    }

    private void Skip(long count)
    {
        if (count <= 0) return;
        byte[] buffer = new byte[Math.Min(count, 1 << 20)];
        while (count > 0)
        {
            int read = _inner.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
            if (read <= 0) throw new EndOfStreamException("archive cpio tronquee");
            count -= read;
        }
    }

    private bool ReadFully(Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = _inner.Read(buffer[total..]);
            if (read <= 0) return total == 0 ? false : throw new EndOfStreamException("archive cpio tronquee");
            total += read;
        }
        return true;
    }

    private void ReadExactly(Span<byte> buffer)
    {
        if (!ReadFully(buffer)) throw new EndOfStreamException("archive cpio tronquee");
    }

    private static uint Octal(ReadOnlySpan<byte> field) =>
        Convert.ToUInt32(Encoding.ASCII.GetString(field), 8);

    private static uint Hex(ReadOnlySpan<byte> field) =>
        uint.Parse(Encoding.ASCII.GetString(field), NumberStyles.HexNumber);

    private sealed class EntryStream : Stream
    {
        private readonly CpioReader _reader;
        public EntryStream(CpioReader reader) => _reader = reader;

        public override int Read(Span<byte> buffer)
        {
            if (_reader._remaining <= 0) return 0;
            int count = (int)Math.Min(buffer.Length, _reader._remaining);
            int read = _reader._inner.Read(buffer[..count]);
            if (read <= 0) throw new EndOfStreamException("archive cpio tronquee");
            _reader._remaining -= read;
            return read;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
