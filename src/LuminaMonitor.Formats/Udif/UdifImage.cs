using System.IO.Compression;

namespace LuminaMonitor.Formats;

/// <summary>
/// Read-only, random-access reader for Apple UDIF disk images (.dmg).
/// </summary>
/// <remarks>
/// The image is opened from its 'koly' trailer, which leads to the XML block
/// map; the runs of every partition are flattened into one array sorted by
/// first sector so a sector lookup is a binary search. Compressed runs are
/// decompressed whole on first touch and kept in a small LRU cache, because
/// file-system parsers read the same 1 MiB run many times over. Sectors that
/// no run covers, and zero-fill / ignore runs, read as zeros. Not thread-safe:
/// one reader per thread.
/// </remarks>
internal sealed class UdifImage : IDisposable
{
    public const int SectorSize = 512;
    private const long DefaultCacheBytes = 32L << 20;

    private readonly Stream _source;
    private readonly bool _leaveOpen;
    private readonly RunRef[] _runs;
    private readonly RunCache _cache;

    public KolyTrailer Trailer { get; }
    public BlkxTable Blkx { get; }

    /// <summary>Size of the virtual disk in sectors (from the trailer, else from the block map's extent).</summary>
    public long SectorCount { get; }

    /// <summary>The blkx entries as sector ranges; names are free text, see <see cref="PartitionTable"/> for types.</summary>
    public IReadOnlyList<(string Name, long FirstSector, long SectorCount)> Partitions { get; }

    /// <summary>A run resolved to absolute disk sectors and an absolute file offset.</summary>
    private readonly record struct RunRef(
        long FirstSector, long SectorCount, long FileOffset, long CompressedLength,
        BlkxRunType Type, int PartitionIndex, int RunIndex);

    private UdifImage(Stream source, bool leaveOpen, KolyTrailer trailer, BlkxTable blkx, long cacheBytes)
    {
        _source = source;
        _leaveOpen = leaveOpen;
        Trailer = trailer;
        Blkx = blkx;
        _cache = new RunCache(cacheBytes);

        var runs = new List<RunRef>();
        long extent = 0;
        for (int p = 0; p < blkx.Partitions.Count; p++)
        {
            var part = blkx.Partitions[p];
            extent = Math.Max(extent, part.SectorNumber + part.SectorCount);
            for (int r = 0; r < part.Runs.Count; r++)
            {
                var run = part.Runs[r];
                if (!run.CoversSectors)
                    continue;
                long first = part.SectorNumber + run.SectorNumber;
                long fileOffset = trailer.DataForkOffset + part.DataOffset + run.CompressedOffset;
                runs.Add(new RunRef(first, run.SectorCount, fileOffset, run.CompressedLength, run.Type, p, r));
                extent = Math.Max(extent, first + run.SectorCount);
            }
        }
        runs.Sort((a, b) => a.FirstSector.CompareTo(b.FirstSector));
        _runs = runs.ToArray();
        SectorCount = trailer.SectorCount > 0 ? trailer.SectorCount : extent;
        Partitions = blkx.Partitions.Select(p => (p.Name, p.SectorNumber, p.SectorCount)).ToList();
    }

    public static UdifImage Open(string path)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1, FileOptions.RandomAccess);
        try
        {
            return Open(file, leaveOpen: false);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>Opens an image from any seekable stream (a MemoryStream in tests).</summary>
    public static UdifImage Open(Stream source, bool leaveOpen = false, long cacheBytes = DefaultCacheBytes)
    {
        if (!source.CanRead || !source.CanSeek)
            throw new ArgumentException("a readable, seekable stream is required", nameof(source));
        if (source.Length < KolyTrailer.Size)
            throw new InvalidDataException("file too short to be a UDIF image");

        Span<byte> trailerBytes = stackalloc byte[KolyTrailer.Size];
        source.Position = source.Length - KolyTrailer.Size;
        source.ReadExactly(trailerBytes);
        var trailer = KolyTrailer.Parse(trailerBytes);

        // Images older than 10.2 keep the block map in a resource fork only; they have no XML and are not handled.
        if (trailer.XmlLength <= 0 || trailer.XmlLength > (64L << 20) || trailer.XmlOffset + trailer.XmlLength > source.Length)
            throw new InvalidDataException("UDIF trailer has no usable XML block map");
        var xml = new byte[trailer.XmlLength];
        source.Position = trailer.XmlOffset;
        source.ReadExactly(xml);

        return new UdifImage(source, leaveOpen, trailer, BlkxTable.Parse(xml), cacheBytes);
    }

    /// <summary>Reads <paramref name="count"/> consecutive 512-byte sectors into <paramref name="destination"/>.</summary>
    public void ReadSectors(long sector, int count, Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sector);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (sector + count > SectorCount)
            throw new ArgumentOutOfRangeException(nameof(count), $"sectors [{sector}, {sector + count}) exceed the image of {SectorCount} sectors");
        if (destination.Length < (long)count * SectorSize)
            throw new ArgumentException("destination smaller than the requested sectors", nameof(destination));

        long current = sector;
        int left = count, pos = 0;
        while (left > 0)
        {
            int n;
            int index = FindRun(current, out long gapEnd);
            if (index < 0)
            {
                n = (int)Math.Min(left, gapEnd - current);
                destination.Slice(pos, n * SectorSize).Clear();
            }
            else
            {
                ref readonly RunRef run = ref _runs[index];
                long inRun = current - run.FirstSector;
                n = (int)Math.Min(left, run.SectorCount - inRun);
                Fill(in run, index, inRun, destination.Slice(pos, n * SectorSize));
            }
            current += n;
            left -= n;
            pos += n * SectorSize;
        }
    }

    /// <summary>A seekable, read-only stream over a sector range (typically one partition).</summary>
    public Stream OpenSectorStream(long firstSector, long sectorCount)
    {
        if (firstSector < 0 || sectorCount < 0 || firstSector + sectorCount > SectorCount)
            throw new ArgumentOutOfRangeException(nameof(sectorCount), "sector range outside the image");
        return new UdifSectorStream(this, firstSector, sectorCount);
    }

    /// <summary>Index of the run holding <paramref name="sector"/>, or -1 with the first sector of the next run in <paramref name="gapEnd"/>.</summary>
    private int FindRun(long sector, out long gapEnd)
    {
        int lo = 0, hi = _runs.Length - 1, last = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_runs[mid].FirstSector <= sector) { last = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        if (last >= 0 && sector < _runs[last].FirstSector + _runs[last].SectorCount)
        {
            gapEnd = 0;
            return last;
        }
        int next = last + 1;
        gapEnd = next < _runs.Length ? _runs[next].FirstSector : SectorCount;
        return -1;
    }

    private void Fill(in RunRef run, int index, long firstSectorInRun, Span<byte> destination)
    {
        switch (run.Type)
        {
            case BlkxRunType.ZeroFill:
            case BlkxRunType.Ignore:
                destination.Clear();
                break;
            case BlkxRunType.Raw:
                long skip = firstSectorInRun * SectorSize;
                ReadRaw(run.FileOffset + skip, run.CompressedLength - skip, destination);
                break;
            default:
                var data = GetDecompressed(index, in run);
                data.AsSpan(checked((int)(firstSectorInRun * SectorSize)), destination.Length).CopyTo(destination);
                break;
        }
    }

    /// <summary>Raw runs hold exactly their sectors; a short one is zero-padded rather than refused.</summary>
    private void ReadRaw(long fileOffset, long available, Span<byte> destination)
    {
        int n = (int)Math.Clamp(available, 0, destination.Length);
        if (n > 0)
        {
            _source.Position = fileOffset;
            _source.ReadExactly(destination[..n]);
        }
        if (n < destination.Length)
            destination[n..].Clear();
    }

    private byte[] GetDecompressed(int index, in RunRef run)
    {
        if (_cache.TryGet(index, out var cached))
            return cached;
        var data = Decompress(in run);
        _cache.Add(index, data);
        return data;
    }

    private byte[] Decompress(in RunRef run)
    {
        string where = $"run {run.RunIndex} of partition '{Blkx.Partitions[run.PartitionIndex].Name}'";
        long outBytes = run.SectorCount * SectorSize;
        if (outBytes > int.MaxValue || run.CompressedLength > int.MaxValue)
            throw new NotSupportedException($"{where} exceeds 2 GiB");

        var output = new byte[outBytes];
        var input = new byte[run.CompressedLength];
        _source.Position = run.FileOffset;
        _source.ReadExactly(input);

        switch (run.Type)
        {
            case BlkxRunType.Zlib:
                using (var zlib = new ZLibStream(new MemoryStream(input, writable: false), CompressionMode.Decompress))
                    ReadAvailable(zlib, output);
                break;
            case BlkxRunType.Adc:
                AdcDecoder.Decode(input, output);
                break;
            case BlkxRunType.Bzip2:
            case BlkxRunType.Lzfse:
            case BlkxRunType.Lzma:
                throw new NotSupportedException($"{where} uses {run.Type} (0x{(uint)run.Type:X8}), which this reader does not decode");
            default:
                throw new InvalidDataException($"{where} has unknown run type 0x{(uint)run.Type:X8}");
        }
        return output;
    }

    /// <summary>Fills as much as the decoder yields; a short run leaves trailing zeros, extra bytes are dropped.</summary>
    private static void ReadAvailable(Stream decoder, Span<byte> output)
    {
        int total = 0;
        while (total < output.Length)
        {
            int n = decoder.Read(output[total..]);
            if (n <= 0)
                break;
            total += n;
        }
    }

    public void Dispose()
    {
        if (!_leaveOpen)
            _source.Dispose();
    }
}
