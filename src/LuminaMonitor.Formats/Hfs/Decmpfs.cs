// decmpfs - Apple's transparent file compression (Mac OS X 10.6+). It is not part of TN1150; the layout
// below follows the public header <sys/decmpfs.h> (decmpfs_disk_header) and the documented resource-fork
// container. Unlike every HFS+ structure, the decmpfs fields are LITTLE-endian.
//
//   xattr "com.apple.decmpfs":
//   +0  UInt32 compression_magic   bytes 'f','p','m','c' = 0x636D7066 when read little-endian
//   +4  UInt32 compression_type    1 raw data in the xattr; 3 zlib in the xattr; 4 zlib chunks in the resource
//                                  fork; 7/8 LZVN (xattr / resource fork); 11/12 LZFSE (xattr / resource fork)
//   +8  UInt64 uncompressed_size
//   +16 payload (types 1, 3, 7, 11). Type 3: one zlib stream (RFC 1950), or 0xFF followed by the raw bytes.
//   Type 4 resource fork: classic resource-fork header, big-endian:
//     +0 UInt32 dataOffset   +4 UInt32 mapOffset   +8 UInt32 dataLength   +12 UInt32 mapLength
//   at dataOffset: UInt32 BE resource data length, then the chunk table (little-endian):
//     UInt32 chunkCount, then chunkCount x { UInt32 offset (relative to the start of the chunk table); UInt32 size }.
//   Every chunk decodes to 65536 bytes except the last one; a chunk whose first byte is 0xFF is stored raw.
using System.Buffers.Binary;
using System.IO.Compression;

namespace LuminaMonitor.Formats.Hfs;

internal readonly record struct DecmpfsHeader(uint CompressionType, long UncompressedSize)
{
    public bool DataInResourceFork =>
        CompressionType is Decmpfs.TypeZlibResource or Decmpfs.TypeLzvnResource or Decmpfs.TypeLzfseResource;
}

internal static class Decmpfs
{
    public const string AttributeName = "com.apple.decmpfs";
    public const uint Magic = 0x636D7066; // 'fpmc'
    public const int HeaderSize = 16;
    public const int ChunkSize = 0x10000;
    public const uint TypeRawAttr = 1, TypeZlibAttr = 3, TypeZlibResource = 4;
    public const uint TypeLzvnAttr = 7, TypeLzvnResource = 8, TypeLzfseAttr = 11, TypeLzfseResource = 12;
    /// <summary>Upper bound for one stored chunk: a raw chunk is 65537 bytes; zlib expands by well under 1%.</summary>
    public const int MaxChunkBytes = ChunkSize + 4096;

    public static DecmpfsHeader ParseHeader(ReadOnlySpan<byte> attribute)
    {
        if (attribute.Length < HeaderSize) throw new InvalidDataException("com.apple.decmpfs attribute shorter than 16 bytes.");
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(attribute);
        if (magic != Magic) throw new InvalidDataException($"com.apple.decmpfs magic 0x{magic:X8} is not 'fpmc'.");
        uint type = BinaryPrimitives.ReadUInt32LittleEndian(attribute.Slice(4));
        ulong size = BinaryPrimitives.ReadUInt64LittleEndian(attribute.Slice(8));
        if (size > long.MaxValue) throw new InvalidDataException("com.apple.decmpfs uncompressed size out of range.");
        return new DecmpfsHeader(type, (long)size);
    }

    /// <summary>Opens the decompressed content described by a com.apple.decmpfs attribute.</summary>
    /// <param name="openResourceFork">Called only for types that keep their data in the resource fork.</param>
    public static Stream Open(byte[] attribute, Func<Stream> openResourceFork)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        ArgumentNullException.ThrowIfNull(openResourceFork);
        var header = ParseHeader(attribute);
        var payload = attribute.AsSpan(HeaderSize);
        switch (header.CompressionType)
        {
            case TypeRawAttr:
            case TypeZlibAttr:
            {
                if (header.UncompressedSize > int.MaxValue)
                    throw new NotSupportedException("decmpfs inline payload larger than 2 GiB.");
                int size = (int)header.UncompressedSize;
                byte[] data = header.CompressionType == TypeRawAttr ? TakeRaw(payload, size) : DecodeChunk(payload, size);
                return new MemoryStream(data, writable: false);
            }
            case TypeZlibResource:
                return new DecmpfsResourceStream(openResourceFork(), header.UncompressedSize);
            case TypeLzvnAttr:
            case TypeLzvnResource:
                throw new NotSupportedException($"decmpfs compression type {header.CompressionType} (LZVN) is not supported by this reader.");
            case TypeLzfseAttr:
            case TypeLzfseResource:
                throw new NotSupportedException($"decmpfs compression type {header.CompressionType} (LZFSE) is not supported by this reader.");
            default:
                throw new NotSupportedException($"decmpfs compression type {header.CompressionType} is unknown.");
        }
    }

    /// <summary>Decodes one chunk: raw when it starts with 0xFF, otherwise a zlib stream producing <paramref name="expectedSize"/> bytes.</summary>
    public static byte[] DecodeChunk(ReadOnlySpan<byte> chunk, int expectedSize)
    {
        if (expectedSize == 0) return Array.Empty<byte>();
        if (chunk.Length == 0) throw new InvalidDataException("Empty decmpfs chunk.");
        if (chunk[0] == 0xFF) return TakeRaw(chunk.Slice(1), expectedSize);
        var output = new byte[expectedSize];
        using var input = new MemoryStream(chunk.ToArray(), writable: false);
        using var inflater = new ZLibStream(input, CompressionMode.Decompress);
        try
        {
            inflater.ReadExactly(output);
        }
        catch (EndOfStreamException e)
        {
            throw new InvalidDataException("decmpfs zlib chunk is shorter than declared.", e);
        }
        return output;
    }

    private static byte[] TakeRaw(ReadOnlySpan<byte> payload, int size)
    {
        if (payload.Length < size)
            throw new InvalidDataException($"decmpfs raw payload holds {payload.Length} bytes, {size} expected.");
        return payload.Slice(0, size).ToArray();
    }
}
