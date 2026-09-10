// Clean-room implementation written from Apple Technical Note TN1150 "HFS Plus Volume Format"
// (https://developer.apple.com/library/archive/technotes/tn/tn1150.html), sections "Volume Header"
// and "Fork Data Structure". No code was taken from any existing implementation.
//
// HFSPlusVolumeHeader - 512 bytes at byte offset 1024 from the start of the volume, big-endian:
//   +0   UInt16 signature          'H+' = 0x482B (HFS Plus), 'HX' = 0x4858 (HFSX)
//   +2   UInt16 version            4 = HFS Plus, 5 = HFSX
//   +4   UInt32 attributes
//   +8   UInt32 lastMountedVersion
//   +12  UInt32 journalInfoBlock
//   +16  UInt32 createDate         +20 modifyDate  +24 backupDate  +28 checkedDate
//   +32  UInt32 fileCount          +36 folderCount
//   +40  UInt32 blockSize          allocation block size, power of two >= 512
//   +44  UInt32 totalBlocks        +48 freeBlocks   +52 nextAllocation
//   +56  UInt32 rsrcClumpSize      +60 dataClumpSize
//   +64  UInt32 nextCatalogID      +68 writeCount
//   +72  UInt64 encodingsBitmap
//   +80  UInt32 finderInfo[8]                (32 bytes)
//   +112 HFSPlusForkData allocationFile      (80 bytes)
//   +192 HFSPlusForkData extentsFile
//   +272 HFSPlusForkData catalogFile
//   +352 HFSPlusForkData attributesFile
//   +432 HFSPlusForkData startupFile        (ends at +512)
//
// HFSPlusForkData - 80 bytes:
//   +0   UInt64 logicalSize
//   +8   UInt32 clumpSize
//   +12  UInt32 totalBlocks
//   +16  HFSPlusExtentRecord extents = 8 x HFSPlusExtentDescriptor { UInt32 startBlock; UInt32 blockCount } (64 bytes)
namespace LuminaMonitor.Formats.Hfs;

internal readonly record struct ExtentDescriptor(uint StartBlock, uint BlockCount)
{
    public const int Size = 8;
    public const int RecordSize = 64;

    public bool IsEmpty => BlockCount == 0;

    public static ExtentDescriptor Parse(ReadOnlySpan<byte> s) => new(Be.U32(s, 0), Be.U32(s, 4));

    /// <summary>Parses an HFSPlusExtentRecord (eight descriptors, unused ones are all zero).</summary>
    public static ExtentDescriptor[] ParseRecord(ReadOnlySpan<byte> s)
    {
        if (s.Length < RecordSize) throw new InvalidDataException("Extent record shorter than 64 bytes.");
        var extents = new ExtentDescriptor[8];
        for (int i = 0; i < 8; i++) extents[i] = Parse(s.Slice(i * Size, Size));
        return extents;
    }
}

internal sealed class ForkData
{
    public const int Size = 80;
    public static readonly ForkData Empty = new(0, 0, 0, new ExtentDescriptor[8]);

    public long LogicalSize { get; }
    public uint ClumpSize { get; }
    public uint TotalBlocks { get; }
    public ExtentDescriptor[] Extents { get; }

    private ForkData(long logicalSize, uint clumpSize, uint totalBlocks, ExtentDescriptor[] extents)
    {
        LogicalSize = logicalSize;
        ClumpSize = clumpSize;
        TotalBlocks = totalBlocks;
        Extents = extents;
    }

    public static ForkData Parse(ReadOnlySpan<byte> s)
    {
        if (s.Length < Size) throw new InvalidDataException("Fork data shorter than 80 bytes.");
        ulong size = Be.U64(s, 0);
        if (size > long.MaxValue) throw new InvalidDataException("Fork logical size out of range.");
        return new ForkData((long)size, Be.U32(s, 8), Be.U32(s, 12), ExtentDescriptor.ParseRecord(s.Slice(16, ExtentDescriptor.RecordSize)));
    }
}

internal sealed class VolumeHeader
{
    public const int Offset = 1024;
    public const int Size = 512;
    public const ushort SignatureHfsPlus = 0x482B; // 'H+'
    public const ushort SignatureHfsx = 0x4858;    // 'HX'
    public const ushort SignatureHfs = 0x4244;     // 'BD' - classic HFS, possibly wrapping an HFS+ volume

    // Reserved catalog node IDs (TN1150 "Catalog Node ID").
    public const uint ExtentsFileId = 3, CatalogFileId = 4, AllocationFileId = 6, StartupFileId = 7, AttributesFileId = 8;

    public ushort Signature { get; private init; }
    public ushort Version { get; private init; }
    public uint Attributes { get; private init; }
    public uint LastMountedVersion { get; private init; }
    public uint CreateDate { get; private init; }
    public uint ModifyDate { get; private init; }
    public uint FileCount { get; private init; }
    public uint FolderCount { get; private init; }
    public uint BlockSize { get; private init; }
    public uint TotalBlocks { get; private init; }
    public uint FreeBlocks { get; private init; }
    public uint NextCatalogId { get; private init; }
    public ForkData AllocationFile { get; private init; } = ForkData.Empty;
    public ForkData ExtentsFile { get; private init; } = ForkData.Empty;
    public ForkData CatalogFile { get; private init; } = ForkData.Empty;
    public ForkData AttributesFile { get; private init; } = ForkData.Empty;
    public ForkData StartupFile { get; private init; } = ForkData.Empty;

    public bool IsHfsx => Signature == SignatureHfsx;

    /// <summary>Parses the 512 bytes found at <see cref="Offset"/>.</summary>
    public static VolumeHeader Parse(ReadOnlySpan<byte> s)
    {
        if (s.Length < Size) throw new ArgumentException("Volume header needs 512 bytes.", nameof(s));
        ushort signature = Be.U16(s, 0);
        if (signature != SignatureHfsPlus && signature != SignatureHfsx)
            throw new InvalidDataException($"Not an HFS+ volume header (signature 0x{signature:X4}).");
        uint blockSize = Be.U32(s, 40);
        if (blockSize < 512 || blockSize > (1u << 30) || (blockSize & (blockSize - 1)) != 0)
            throw new InvalidDataException($"Invalid HFS+ allocation block size {blockSize}.");

        return new VolumeHeader
        {
            Signature = signature,
            Version = Be.U16(s, 2),
            Attributes = Be.U32(s, 4),
            LastMountedVersion = Be.U32(s, 8),
            CreateDate = Be.U32(s, 16),
            ModifyDate = Be.U32(s, 20),
            FileCount = Be.U32(s, 32),
            FolderCount = Be.U32(s, 36),
            BlockSize = blockSize,
            TotalBlocks = Be.U32(s, 44),
            FreeBlocks = Be.U32(s, 48),
            NextCatalogId = Be.U32(s, 64),
            AllocationFile = ForkData.Parse(s.Slice(112, ForkData.Size)),
            ExtentsFile = ForkData.Parse(s.Slice(192, ForkData.Size)),
            CatalogFile = ForkData.Parse(s.Slice(272, ForkData.Size)),
            AttributesFile = ForkData.Parse(s.Slice(352, ForkData.Size)),
            StartupFile = ForkData.Parse(s.Slice(432, ForkData.Size)),
        };
    }
}
