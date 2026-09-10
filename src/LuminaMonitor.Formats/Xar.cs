using System.Buffers.Binary;
using System.IO.Compression;
using System.Xml.Linq;

namespace LuminaMonitor.Formats;

/// <summary>
/// Reader for xar archives — the container behind Apple's <c>.xip</c>.
/// </summary>
/// <remarks>
/// A xar file is a 28-byte big-endian header, a zlib-compressed XML table of
/// contents, then a heap of file data addressed by offsets relative to the
/// heap start. Xcode's XIP holds exactly two entries: a small <c>Metadata</c>
/// and a multi-gigabyte <c>Content</c>, the latter stored uncompressed at the
/// xar level because it is a pbzx stream, compressed on its own terms. The
/// XIP signature is not verified here: the payload's integrity is checked
/// downstream by the xz block checksums, and its authenticity by the phone
/// when the image is personalised.
/// </remarks>
public sealed class Xar : IDisposable
{
    private const uint Magic = 0x78617221;            // "xar!"
    private readonly Stream _file;
    private readonly long _heapOffset;

    /// <summary>
    /// A file entry. <see cref="Path"/> is the full path inside the archive
    /// (xar nests file elements for directories); <see cref="Name"/> the leaf.
    /// </summary>
    public sealed record Entry(string Name, string Path, long Offset, long Length, long Size, string Encoding);

    public IReadOnlyList<Entry> Entries { get; }

    private Xar(Stream file, long heapOffset, IReadOnlyList<Entry> entries)
    {
        _file = file;
        _heapOffset = heapOffset;
        Entries = entries;
    }

    public static Xar Open(string path) =>
        Open(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16));

    /// <summary>Opens an archive held in any seekable stream — a package inside a disk image, say.</summary>
    public static Xar Open(Stream file)
    {
        file.Position = 0;
        Span<byte> header = stackalloc byte[28];
        file.ReadExactly(header);
        if (BinaryPrimitives.ReadUInt32BigEndian(header) != Magic)
            throw new InvalidDataException("pas une archive xar (magie absente)");
        ushort headerSize = BinaryPrimitives.ReadUInt16BigEndian(header[4..]);
        long tocCompressed = (long)BinaryPrimitives.ReadUInt64BigEndian(header[8..]);
        long tocUncompressed = (long)BinaryPrimitives.ReadUInt64BigEndian(header[16..]);

        file.Position = headerSize;
        byte[] toc = new byte[tocUncompressed];
        using (var zlib = new ZLibStream(new SubStream(file, headerSize, tocCompressed), CompressionMode.Decompress))
            zlib.ReadExactly(toc);

        var entries = new List<Entry>();
        var doc = XDocument.Parse(System.Text.Encoding.UTF8.GetString(toc));
        foreach (var fileElement in doc.Descendants("file"))
        {
            var data = fileElement.Element("data");
            if (data is null) continue;
            string name = fileElement.Element("name")?.Value ?? "";
            // Directories nest their children as <file> elements: rebuild the path.
            var parts = new List<string> { name };
            for (var parent = fileElement.Parent; parent is not null && parent.Name.LocalName == "file"; parent = parent.Parent)
                parts.Insert(0, parent.Element("name")?.Value ?? "");
            long offset = long.Parse(data.Element("offset")?.Value ?? "0");
            long length = long.Parse(data.Element("length")?.Value ?? "0");
            long size = long.Parse(data.Element("size")?.Value ?? length.ToString());
            string encoding = data.Element("encoding")?.Attribute("style")?.Value ?? "application/octet-stream";
            entries.Add(new Entry(name, string.Join('/', parts), offset, length, size, encoding));
        }

        return new Xar(file, headerSize + tocCompressed, entries);
    }

    /// <summary>The stored bytes of an entry, decompressed when xar compressed them.</summary>
    public Stream Open(Entry entry)
    {
        Stream raw = new SubStream(_file, _heapOffset + entry.Offset, entry.Length);
        return entry.Encoding switch
        {
            "application/x-gzip" or "application/zlib" => new ZLibStream(raw, CompressionMode.Decompress),
            "application/x-bzip2" => throw new NotSupportedException("entree xar en bzip2"),
            _ => raw,
        };
    }

    public void Dispose() => _file.Dispose();
}

/// <summary>A read-only window onto a range of a seekable stream.</summary>
public sealed class SubStream : Stream
{
    private readonly Stream _inner;
    private readonly long _start;
    private readonly long _length;
    private long _position;

    public SubStream(Stream inner, long start, long length)
    {
        _inner = inner;
        _start = start;
        _length = length;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position { get => _position; set => _position = Math.Clamp(value, 0, _length); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        long remaining = _length - _position;
        if (remaining <= 0) return 0;
        int count = (int)Math.Min(buffer.Length, remaining);
        _inner.Position = _start + _position;
        int read = _inner.Read(buffer[..count]);
        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => _length + offset,
        };
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
