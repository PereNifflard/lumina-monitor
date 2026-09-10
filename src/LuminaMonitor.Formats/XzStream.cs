// ---------------------------------------------------------------------------
// xz container reader — clean-room implementation.
//
// Written solely from these public-domain specifications; no decoder source
// (liblzma, xz-embedded, 7-zip, or any other) was read or copied:
//
//   * The .xz File Format, version 1.2.1 (Lasse Collin et al.),
//     https://tukaani.org/xz/xz-file-format.txt
//     — stream header / footer, stream flags and check types, block header
//       with its variable-length integers and filter flags, block padding,
//       index, stream padding, CRC32 (IEEE 802.3) and CRC64 (ECMA-182)
//       definitions, LZMA2 filter ID 0x21 and its dictionary-size property.
//
//   * LZMA specification (lzma-specification.txt, LZMA SDK, Igor Pavlov,
//     https://7-zip.org/sdk.html)
//     — range decoder, probability model, literal / length / distance coding
//       and the state machine implemented in Lzma2Decoder.cs.
//
// Scope: single LZMA2 filter per block (Apple's pbzx chunks), checks None,
// CRC32, CRC64 and SHA-256, any number of blocks, concatenated streams with
// stream padding. BCJ / Delta filters and reserved check types are rejected
// with NotSupportedException rather than silently producing wrong bytes.
// ---------------------------------------------------------------------------

using System.Buffers.Binary;
using System.Security.Cryptography;

namespace LuminaMonitor.Formats;

/// <summary>
/// Read-only stream that decodes an xz file (one or more concatenated streams) and
/// verifies every CRC and integrity check the format carries.
/// </summary>
/// <remarks>
/// WHY verify everything: the DDI unpack is a multi-gigabyte pipeline (pbzx → cpio →
/// disk image); a silently corrupt byte would surface much later as an unexplained
/// mount failure. The check over the uncompressed data is computed as bytes flow to the
/// caller, and the index / footer are cross-checked against what was actually read, so
/// <see cref="Read(Span{byte})"/> returns 0 only once the whole file has been proven
/// consistent. Any mismatch throws <see cref="InvalidDataException"/> naming the field.
/// </remarks>
internal sealed class XzStream : Stream
{
    private static ReadOnlySpan<byte> HeaderMagic => [0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00];
    private static ReadOnlySpan<byte> FooterMagic => [0x59, 0x5A];

    private const int CheckNone = 0x00;
    private const int CheckCrc32 = 0x01;
    private const int CheckCrc64 = 0x04;
    private const int CheckSha256 = 0x0A;
    private const long FilterLzma2 = 0x21;

    private readonly Stream _inner;
    private readonly bool _leaveOpen;
    private readonly BufferedByteReader _in;
    private readonly List<(long Unpadded, long Uncompressed)> _records = new();

    // Current stream.
    private byte _flagsByte;
    private int _checkType;
    private int _checkSize;
    private bool _inStream;
    private int _streamsRead;
    private long _indexSize;

    // Current block.
    private Lzma2Decoder? _block;
    private long _headerSize;
    private long _dataStart;
    private long _declaredCompressed = -1;
    private long _declaredUncompressed = -1;
    private long _blockUncompressed;
    private uint _crc32;
    private ulong _crc64;
    private IncrementalHash? _sha256;

    private bool _eof;
    private long _position;

    /// <param name="inner">Stream positioned at the xz magic bytes.</param>
    /// <param name="leaveOpen">Keep <paramref name="inner"/> open when this stream is disposed.</param>
    public XzStream(Stream inner, bool leaveOpen = false)
    {
        _inner = inner;
        _leaveOpen = leaveOpen;
        _in = new BufferedByteReader(inner);
    }

    /// <summary>Number of xz streams whose header has been read (progress / diagnostics).</summary>
    public int StreamsRead => _streamsRead;

    /// <summary>Number of blocks fully decoded and verified so far.</summary>
    public long BlocksRead { get; private set; }

    // ------------------------------------------------------------------ Stream surface

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty)
            return 0;
        while (!_eof)
        {
            if (_block is null && !OpenNextBlock())
            {
                _eof = true;
                break;
            }
            int n = _block!.Read(buffer);
            if (n > 0)
            {
                UpdateCheck(buffer[..n]);
                _blockUncompressed += n;
                _position += n;
                return n;
            }
            CloseBlock();
        }
        return 0;
    }

    public override int ReadByte()
    {
        Span<byte> one = stackalloc byte[1];
        return Read(one) == 1 ? one[0] : -1;
    }

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
        if (disposing)
        {
            _sha256?.Dispose();
            _block?.Dispose();
            if (!_leaveOpen)
                _inner.Dispose();
        }
        base.Dispose(disposing);
    }

    // ------------------------------------------------------------------ Streams

    /// <summary>Positions on the next block; returns false at the clean end of the last stream.</summary>
    private bool OpenNextBlock()
    {
        while (true)
        {
            if (!_inStream)
            {
                if (!BeginStream())
                    return false;
                _inStream = true;
            }

            // A block starts with its (non-zero) header size; the index starts with 0x00.
            int first = _in.ReadByte();
            if (first < 0)
                throw new InvalidDataException("xz stream is truncated: no index or footer");
            if (first == 0)
            {
                ReadIndex();
                ReadFooter();
                _records.Clear();
                _inStream = false;
                continue;
            }
            BeginBlock(first);
            return true;
        }
    }

    /// <summary>
    /// Reads a stream header (12 bytes). Between streams any number of four-byte groups of
    /// zeros (stream padding) may appear; end of input there is the normal end of the file.
    /// </summary>
    private bool BeginStream()
    {
        if (_streamsRead > 0)
        {
            Span<byte> pad = stackalloc byte[4];
            while (true)
            {
                int b = _in.PeekByte();
                if (b < 0)
                    return false;
                if (b != 0)
                    break;
                _in.ReadExactly(pad);
                if (pad[1] != 0 || pad[2] != 0 || pad[3] != 0)
                    throw new InvalidDataException("xz stream padding is not made of four-byte zero groups");
            }
        }
        else if (_in.PeekByte() < 0)
        {
            throw new InvalidDataException("not an xz stream: no data");
        }

        Span<byte> header = stackalloc byte[12];
        _in.ReadExactly(header);
        if (!header[..6].SequenceEqual(HeaderMagic))
            throw new InvalidDataException("not an xz stream: bad magic bytes");
        if (header[6] != 0 || (header[7] & 0xF0) != 0)
            throw new InvalidDataException("xz stream flags use reserved bits (newer format version?)");
        uint stored = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
        uint computed = Crc32.Compute(header[6..8]);
        if (stored != computed)
            throw new InvalidDataException($"xz stream header CRC32 mismatch: stored {stored:X8}, computed {computed:X8}");

        _flagsByte = header[7];
        _checkType = header[7] & 0x0F;
        _checkSize = CheckSize(_checkType);
        if (_checkType is not (CheckNone or CheckCrc32 or CheckCrc64 or CheckSha256))
            throw new NotSupportedException(
                $"xz check type 0x{_checkType:X2} is reserved by the format; integrity cannot be verified");
        _streamsRead++;
        return true;
    }

    /// <summary>Check field size by type (xz spec 2.1.1.2); reserved IDs are grouped by size.</summary>
    private static int CheckSize(int type) => type switch
    {
        0 => 0,
        <= 3 => 4,
        <= 6 => 8,
        <= 9 => 16,
        <= 12 => 32,
        _ => 64,
    };

    // ------------------------------------------------------------------ Blocks

    /// <summary>
    /// Parses a block header whose first byte was already read: real size = (byte + 1) * 4,
    /// flags, optional compressed / uncompressed sizes, filter flags, zero padding, CRC32.
    /// </summary>
    private void BeginBlock(int sizeByte)
    {
        int headerSize = (sizeByte + 1) * 4;
        byte[] header = new byte[headerSize];
        header[0] = (byte)sizeByte;
        _in.ReadExactly(header.AsSpan(1));

        uint stored = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(headerSize - 4));
        uint computed = Crc32.Compute(header.AsSpan(0, headerSize - 4));
        if (stored != computed)
            throw new InvalidDataException($"xz block header CRC32 mismatch: stored {stored:X8}, computed {computed:X8}");

        int flags = header[1];
        if ((flags & 0x3C) != 0)
            throw new InvalidDataException("xz block header uses reserved flag bits");
        int filterCount = (flags & 0x03) + 1;
        ReadOnlySpan<byte> body = header.AsSpan(0, headerSize - 4);
        int pos = 2;
        _declaredCompressed = (flags & 0x40) != 0 ? ReadVli(body, ref pos) : -1;
        _declaredUncompressed = (flags & 0x80) != 0 ? ReadVli(body, ref pos) : -1;

        if (filterCount != 1)
            throw new NotSupportedException(
                $"xz block chains {filterCount} filters; only a single LZMA2 filter is supported (no BCJ / Delta)");
        long filterId = ReadVli(body, ref pos);
        long propsSize = ReadVli(body, ref pos);
        if (filterId != FilterLzma2)
            throw new NotSupportedException($"xz filter {FilterName(filterId)} is not supported; only LZMA2 (0x21) is");
        if (propsSize != 1 || pos + 1 > body.Length)
            throw new InvalidDataException("xz LZMA2 filter flags must carry exactly one property byte");
        byte dictionaryProperty = body[pos++];
        for (; pos < body.Length; pos++)
        {
            if (body[pos] != 0)
                throw new InvalidDataException("xz block header padding is not zero");
        }

        _headerSize = headerSize;
        _dataStart = _in.Position;
        _blockUncompressed = 0;
        _crc32 = Crc32.Initial;
        _crc64 = Crc64.Initial;
        if (_checkType == CheckSha256)
            (_sha256 ??= IncrementalHash.CreateHash(HashAlgorithmName.SHA256)).GetHashAndReset();

        _block = new Lzma2Decoder(_in, dictionaryProperty, _declaredUncompressed >= 0 ? _declaredUncompressed : null);
    }

    private static string FilterName(long id) => id switch
    {
        0x03 => "Delta (0x03)",
        0x04 => "x86 BCJ (0x04)",
        0x05 => "PowerPC BCJ (0x05)",
        0x06 => "IA-64 BCJ (0x06)",
        0x07 => "ARM BCJ (0x07)",
        0x08 => "ARM-Thumb BCJ (0x08)",
        0x09 => "SPARC BCJ (0x09)",
        0x0A => "ARM64 BCJ (0x0A)",
        0x0B => "RISC-V BCJ (0x0B)",
        _ => $"0x{id:X}",
    };

    /// <summary>
    /// After the LZMA2 end marker: declared sizes, block padding to a four-byte boundary,
    /// then the check over the uncompressed data. Records the sizes the index must repeat.
    /// </summary>
    private void CloseBlock()
    {
        long compressed = _in.Position - _dataStart;
        if (_declaredCompressed >= 0 && _declaredCompressed != compressed)
            throw new InvalidDataException(
                $"xz block header declares {_declaredCompressed} compressed bytes, block holds {compressed}");
        if (_declaredUncompressed >= 0 && _declaredUncompressed != _blockUncompressed)
            throw new InvalidDataException(
                $"xz block header declares {_declaredUncompressed} uncompressed bytes, block produced {_blockUncompressed}");

        for (int padding = (int)(-compressed & 3); padding > 0; padding--)
        {
            int b = _in.ReadByte();
            if (b < 0)
                throw new InvalidDataException("xz block is truncated in its padding");
            if (b != 0)
                throw new InvalidDataException("xz block padding is not zero");
        }

        Span<byte> check = stackalloc byte[64];
        check = check[.._checkSize];
        _in.ReadExactly(check);
        VerifyCheck(check);

        _records.Add((_headerSize + compressed + _checkSize, _blockUncompressed));
        _block!.Dispose();
        _block = null;
        BlocksRead++;
    }

    private void UpdateCheck(ReadOnlySpan<byte> data)
    {
        switch (_checkType)
        {
            case CheckCrc32:
                _crc32 = Crc32.Update(_crc32, data);
                break;
            case CheckCrc64:
                _crc64 = Crc64.Update(_crc64, data);
                break;
            case CheckSha256:
                _sha256!.AppendData(data);
                break;
        }
    }

    private void VerifyCheck(ReadOnlySpan<byte> stored)
    {
        switch (_checkType)
        {
            case CheckCrc32:
            {
                uint want = BinaryPrimitives.ReadUInt32LittleEndian(stored);
                uint got = Crc32.Finish(_crc32);
                if (want != got)
                    throw new InvalidDataException($"xz block CRC32 mismatch: stored {want:X8}, computed {got:X8}");
                break;
            }
            case CheckCrc64:
            {
                ulong want = BinaryPrimitives.ReadUInt64LittleEndian(stored);
                ulong got = Crc64.Finish(_crc64);
                if (want != got)
                    throw new InvalidDataException($"xz block CRC64 mismatch: stored {want:X16}, computed {got:X16}");
                break;
            }
            case CheckSha256:
            {
                Span<byte> got = stackalloc byte[32];
                _sha256!.GetHashAndReset(got);
                if (!stored.SequenceEqual(got))
                    throw new InvalidDataException(
                        $"xz block SHA-256 mismatch: stored {Convert.ToHexString(stored)}, computed {Convert.ToHexString(got)}");
                break;
            }
        }
    }

    // ------------------------------------------------------------------ Index and footer

    /// <summary>
    /// Index (indicator already consumed): record count, one (unpadded size, uncompressed
    /// size) pair per block, zero padding to a four-byte boundary, CRC32. Every record is
    /// compared with what the blocks actually contained.
    /// </summary>
    private void ReadIndex()
    {
        var bytes = new List<byte> { 0x00 };
        long count = ReadVli(bytes);
        if (count != _records.Count)
            throw new InvalidDataException($"xz index lists {count} block(s) but the stream holds {_records.Count}");
        for (int i = 0; i < _records.Count; i++)
        {
            long unpadded = ReadVli(bytes);
            long uncompressed = ReadVli(bytes);
            if (unpadded != _records[i].Unpadded)
                throw new InvalidDataException(
                    $"xz index record {i}: unpadded size {unpadded} differs from the block's {_records[i].Unpadded}");
            if (uncompressed != _records[i].Uncompressed)
                throw new InvalidDataException(
                    $"xz index record {i}: uncompressed size {uncompressed} differs from the block's {_records[i].Uncompressed}");
        }
        while (bytes.Count % 4 != 0)
        {
            int b = _in.ReadByte();
            if (b != 0)
                throw new InvalidDataException(b < 0 ? "xz index is truncated" : "xz index padding is not zero");
            bytes.Add(0);
        }

        Span<byte> crc = stackalloc byte[4];
        _in.ReadExactly(crc);
        uint stored = BinaryPrimitives.ReadUInt32LittleEndian(crc);
        uint computed = Crc32.Compute(bytes.ToArray());
        if (stored != computed)
            throw new InvalidDataException($"xz index CRC32 mismatch: stored {stored:X8}, computed {computed:X8}");
        _indexSize = bytes.Count + 4;
    }

    /// <summary>Footer: CRC32, backward size (index size / 4 - 1), stream flags again, "YZ".</summary>
    private void ReadFooter()
    {
        Span<byte> footer = stackalloc byte[12];
        _in.ReadExactly(footer);
        if (!footer[10..].SequenceEqual(FooterMagic))
            throw new InvalidDataException("xz stream footer magic \"YZ\" missing");
        uint stored = BinaryPrimitives.ReadUInt32LittleEndian(footer);
        uint computed = Crc32.Compute(footer[4..10]);
        if (stored != computed)
            throw new InvalidDataException($"xz stream footer CRC32 mismatch: stored {stored:X8}, computed {computed:X8}");
        long backward = ((long)BinaryPrimitives.ReadUInt32LittleEndian(footer[4..]) + 1) * 4;
        if (backward != _indexSize)
            throw new InvalidDataException($"xz footer backward size {backward} differs from the index size {_indexSize}");
        if (footer[8] != 0 || footer[9] != _flagsByte)
            throw new InvalidDataException("xz stream footer flags differ from the stream header");
    }

    // ------------------------------------------------------------------ Variable-length integers

    /// <summary>
    /// VLI (xz spec 1.2): little-endian, seven bits per byte, high bit means "more", at most
    /// nine bytes, and a continuation must not be followed by a 0x00 byte.
    /// </summary>
    private static long ReadVli(ReadOnlySpan<byte> buf, ref int pos)
    {
        ulong value = 0;
        for (int i = 0; i < 9; i++)
        {
            if (pos >= buf.Length)
                throw new InvalidDataException("xz variable-length integer runs past the end of its field");
            byte b = buf[pos++];
            if (i > 0 && b == 0)
                throw new InvalidDataException("xz variable-length integer has a zero continuation byte");
            value |= (ulong)(b & 0x7F) << (7 * i);
            if ((b & 0x80) == 0)
                return (long)value;
        }
        throw new InvalidDataException("xz variable-length integer is longer than nine bytes");
    }

    /// <summary>Same as above, straight from the input; bytes are appended to <paramref name="sink"/> for the index CRC.</summary>
    private long ReadVli(List<byte> sink)
    {
        ulong value = 0;
        for (int i = 0; i < 9; i++)
        {
            int b = _in.ReadByte();
            if (b < 0)
                throw new InvalidDataException("xz index is truncated");
            if (i > 0 && b == 0)
                throw new InvalidDataException("xz variable-length integer has a zero continuation byte");
            sink.Add((byte)b);
            value |= (ulong)(b & 0x7F) << (7 * i);
            if ((b & 0x80) == 0)
                return (long)value;
        }
        throw new InvalidDataException("xz variable-length integer is longer than nine bytes");
    }
}

/// <summary>
/// CRC32 as xz uses it: IEEE 802.3 polynomial 0x04C11DB7 in reflected form (0xEDB88320),
/// initial value and final XOR all ones. Slicing-by-eight tables keep it well above the
/// LZMA decoder's own throughput so the check never dominates.
/// </summary>
internal static class Crc32
{
    public const uint Initial = 0xFFFFFFFFu;
    private static readonly uint[] Table = BuildTable();

    public static uint Compute(ReadOnlySpan<byte> data) => Finish(Update(Initial, data));

    public static uint Finish(uint state) => ~state;

    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        uint[] t = Table;
        int i = 0;
        for (; i + 8 <= data.Length; i += 8)
        {
            uint one = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i, 4)) ^ crc;
            uint two = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i + 4, 4));
            crc = t[7 * 256 + (int)(one & 0xFF)]
                ^ t[6 * 256 + (int)((one >> 8) & 0xFF)]
                ^ t[5 * 256 + (int)((one >> 16) & 0xFF)]
                ^ t[4 * 256 + (int)(one >> 24)]
                ^ t[3 * 256 + (int)(two & 0xFF)]
                ^ t[2 * 256 + (int)((two >> 8) & 0xFF)]
                ^ t[1 * 256 + (int)((two >> 16) & 0xFF)]
                ^ t[(int)(two >> 24)];
        }
        for (; i < data.Length; i++)
            crc = t[(int)((crc ^ data[i]) & 0xFF)] ^ (crc >> 8);
        return crc;
    }

    private static uint[] BuildTable()
    {
        const uint poly = 0xEDB88320u;
        var t = new uint[8 * 256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? (c >> 1) ^ poly : c >> 1;
            t[n] = c;
        }
        for (int slice = 1; slice < 8; slice++)
        {
            for (int n = 0; n < 256; n++)
            {
                uint prev = t[(slice - 1) * 256 + n];
                t[slice * 256 + n] = (prev >> 8) ^ t[(int)(prev & 0xFF)];
            }
        }
        return t;
    }
}

/// <summary>
/// CRC64 as xz uses it: ECMA-182 polynomial 0x42F0E1EBA9EA3693 in reflected form
/// (0xC96C5795D7870F42), initial value and final XOR all ones. Check value of
/// "123456789" is 0x995DC9BBDF1939FA.
/// </summary>
internal static class Crc64
{
    public const ulong Initial = 0xFFFFFFFFFFFFFFFFul;
    private static readonly ulong[] Table = BuildTable();

    public static ulong Compute(ReadOnlySpan<byte> data) => Finish(Update(Initial, data));

    public static ulong Finish(ulong state) => ~state;

    public static ulong Update(ulong crc, ReadOnlySpan<byte> data)
    {
        ulong[] t = Table;
        int i = 0;
        for (; i + 8 <= data.Length; i += 8)
        {
            crc ^= BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(i, 8));
            crc = t[7 * 256 + (int)(crc & 0xFF)]
                ^ t[6 * 256 + (int)((crc >> 8) & 0xFF)]
                ^ t[5 * 256 + (int)((crc >> 16) & 0xFF)]
                ^ t[4 * 256 + (int)((crc >> 24) & 0xFF)]
                ^ t[3 * 256 + (int)((crc >> 32) & 0xFF)]
                ^ t[2 * 256 + (int)((crc >> 40) & 0xFF)]
                ^ t[1 * 256 + (int)((crc >> 48) & 0xFF)]
                ^ t[(int)(crc >> 56)];
        }
        for (; i < data.Length; i++)
            crc = t[(int)((crc ^ data[i]) & 0xFF)] ^ (crc >> 8);
        return crc;
    }

    private static ulong[] BuildTable()
    {
        const ulong poly = 0xC96C5795D7870F42ul;
        var t = new ulong[8 * 256];
        for (uint n = 0; n < 256; n++)
        {
            ulong c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? (c >> 1) ^ poly : c >> 1;
            t[n] = c;
        }
        for (int slice = 1; slice < 8; slice++)
        {
            for (int n = 0; n < 256; n++)
            {
                ulong prev = t[(slice - 1) * 256 + n];
                t[slice * 256 + n] = (prev >> 8) ^ t[(int)(prev & 0xFF)];
            }
        }
        return t;
    }
}
