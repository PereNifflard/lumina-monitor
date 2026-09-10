using System.Buffers.Binary;
using System.IO.Compression;

namespace LuminaMonitor.Formats.Apfs;

/// <summary>
/// Minimal decoder for Apple's transparent file compression ("decmpfs"): a
/// file whose inode carries UF_COMPRESSED has a <c>com.apple.decmpfs</c>
/// extended attribute holding a 16-byte header {magic "fpmc", compression
/// type u32, uncompressed size u64} followed by the payload. Supported:
/// type 3 (zlib stream inside the attribute) and type 4 (zlib chunks of
/// 64 KiB inside the <c>com.apple.ResourceFork</c> attribute).
/// </summary>
internal static class Decmpfs
{
    public const string AttrName = "com.apple.decmpfs";
    public const string ResourceForkName = "com.apple.ResourceFork";
    public const int HeaderSize = 16;
    public const int ChunkSize = 65536;

    public static ulong UncompressedSize(ReadOnlySpan<byte> attr)
    {
        CheckHeader(attr);
        return Le.U64(attr, 8);
    }

    public static byte[] Decode(ReadOnlySpan<byte> attr, Func<byte[]> resourceFork)
    {
        CheckHeader(attr);
        uint type = Le.U32(attr, 4);
        ulong size = Le.U64(attr, 8);
        if (size > int.MaxValue)
            throw new NotSupportedException("fichier decmpfs trop grand pour être décodé en mémoire");
        var output = new byte[size];
        switch (type)
        {
            case 3:
                DecodeChunk(attr[HeaderSize..], output);
                break;
            case 4:
                DecodeResourceFork(resourceFork(), output);
                break;
            default:
                throw new NotSupportedException(
                    $"decmpfs : type de compression {type} non pris en charge (seuls 3 = zlib dans l'attribut et 4 = zlib dans la resource fork le sont)");
        }
        return output;
    }

    private static void CheckHeader(ReadOnlySpan<byte> attr)
    {
        if (attr.Length < HeaderSize || !attr[..4].SequenceEqual("fpmc"u8))
            throw new InvalidDataException("attribut com.apple.decmpfs invalide");
    }

    /// <summary>
    /// A chunk whose first byte has its low nibble set to 0xF is stored raw
    /// after that byte; anything else is a zlib (RFC 1950) stream.
    /// </summary>
    private static void DecodeChunk(ReadOnlySpan<byte> chunk, Span<byte> output)
    {
        if (chunk.Length == 0)
        {
            if (output.Length != 0)
                throw new InvalidDataException("bloc decmpfs vide");
            return;
        }
        if ((chunk[0] & 0x0F) == 0x0F)
        {
            if (chunk.Length - 1 != output.Length)
                throw new InvalidDataException("bloc decmpfs brut de taille inattendue");
            chunk[1..].CopyTo(output);
            return;
        }
        using var inflater = new ZLibStream(new MemoryStream(chunk.ToArray()), CompressionMode.Decompress);
        int total = 0;
        while (total < output.Length)
        {
            int n = inflater.Read(output[total..]);
            if (n == 0) break;
            total += n;
        }
        if (total != output.Length)
            throw new InvalidDataException("flux zlib decmpfs tronqué");
    }

    /// <summary>
    /// Resource fork layout (classic Mac OS header, big-endian): data offset
    /// u32 @0, map offset @4, data length @8, map length @12. At the data
    /// offset: a big-endian u32 length, then the blob: u32 LE offset of the
    /// chunk data (equal to the table size), u32 LE chunk count, then count
    /// entries of {u32 LE offset, u32 LE size} relative to the blob start.
    /// </summary>
    private static void DecodeResourceFork(byte[] fork, Span<byte> output)
    {
        if (fork.Length < 16)
            throw new InvalidDataException("resource fork tronquée");
        int dataOffset = checked((int)BinaryPrimitives.ReadUInt32BigEndian(fork));
        if (dataOffset < 16 || dataOffset + 4 > fork.Length)
            throw new InvalidDataException("resource fork : décalage de données invalide");
        int blob = dataOffset + 4;
        int blobLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(fork.AsSpan(dataOffset)));
        if (blobLength < 8 || blob + blobLength > fork.Length)
            throw new InvalidDataException("resource fork : longueur de données invalide");
        int count = checked((int)Le.U32(fork, blob + 4));
        int expected = (output.Length + ChunkSize - 1) / ChunkSize;
        if (count != expected || 8 + 8L * count > blobLength)
            throw new InvalidDataException($"resource fork : {count} blocs pour {expected} attendus");
        for (int i = 0; i < count; i++)
        {
            int entry = blob + 8 + 8 * i;
            int offset = checked((int)Le.U32(fork, entry));
            int length = checked((int)Le.U32(fork, entry + 4));
            if (offset < 0 || length < 0 || offset + length > blobLength)
                throw new InvalidDataException($"resource fork : bloc {i} hors limites");
            int outStart = i * ChunkSize;
            int outLength = Math.Min(ChunkSize, output.Length - outStart);
            DecodeChunk(fork.AsSpan(blob + offset, length), output.Slice(outStart, outLength));
        }
    }
}
