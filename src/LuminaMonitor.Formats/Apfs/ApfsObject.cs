using System.Buffers.Binary;

namespace LuminaMonitor.Formats.Apfs;

// Shared plumbing for the APFS reader: little-endian accessors, the common
// object header, object-type constants and a checksum-verifying block reader.
// Reference: Apple File System Reference (2020-06-22), "Objects".
// All on-disk integers are little-endian.

/// <summary>Little-endian field accessors.</summary>
internal static class Le
{
    public static ushort U16(ReadOnlySpan<byte> b, int off) => BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(off, 2));
    public static uint U32(ReadOnlySpan<byte> b, int off) => BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(off, 4));
    public static ulong U64(ReadOnlySpan<byte> b, int off) => BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(off, 8));
}

/// <summary>
/// obj_phys_t — the 32-byte header at the start of every object.
/// Offsets: o_cksum 0 (8 bytes), o_oid 8, o_xid 16, o_type 24 (u32), o_subtype 28 (u32).
/// </summary>
internal readonly struct ObjectHeader
{
    public const int Size = 32;

    public readonly ulong Oid;
    public readonly ulong Xid;
    public readonly uint Type;      // low 16 bits: type; high 16 bits: storage flags
    public readonly uint Subtype;

    public ObjectHeader(ReadOnlySpan<byte> block)
    {
        Oid = Le.U64(block, 8);
        Xid = Le.U64(block, 16);
        Type = Le.U32(block, 24);
        Subtype = Le.U32(block, 28);
    }

    public uint BaseType => Type & ObjectTypes.TypeMask;
    public uint BaseSubtype => Subtype & ObjectTypes.TypeMask;
}

/// <summary>Object type and storage-flag constants ("Object Types" / "Object Type Flags").</summary>
internal static class ObjectTypes
{
    public const uint TypeMask = 0x0000FFFF;
    public const uint FlagsMask = 0xFFFF0000;

    public const uint NxSuperblock = 0x01;
    public const uint Btree = 0x02;
    public const uint BtreeNode = 0x03;
    public const uint Omap = 0x0B;
    public const uint CheckpointMap = 0x0C;
    public const uint Fs = 0x0D;
    public const uint Fstree = 0x0E;

    public const uint Virtual = 0x00000000;
    public const uint Ephemeral = 0x80000000;
    public const uint Physical = 0x40000000;
    public const uint NoHeader = 0x20000000;
    public const uint Encrypted = 0x10000000;
}

/// <summary>
/// Reads container blocks by physical address and verifies their Fletcher-64
/// checksum, so that every structure parsed above this layer is known intact.
/// </summary>
internal sealed class BlockSource
{
    public IBlockDevice Device { get; }
    public int BlockSize { get; }

    public BlockSource(IBlockDevice device, int blockSize)
    {
        Device = device;
        BlockSize = blockSize;
    }

    /// <summary>Reads one block; the checksum is verified unless <paramref name="verify"/> is false.</summary>
    public byte[] ReadBlock(ulong paddr, bool verify = true) => ReadObject(paddr, BlockSize, verify);

    /// <summary>Reads an object spanning <paramref name="length"/> bytes from block <paramref name="paddr"/>.</summary>
    public byte[] ReadObject(ulong paddr, int length, bool verify = true)
    {
        var buffer = new byte[length];
        ReadRaw(paddr, buffer);
        if (verify && !Fletcher64.Verify(buffer))
            throw new InvalidDataException($"somme de contrôle Fletcher-64 invalide pour l'objet au bloc {paddr}");
        return buffer;
    }

    /// <summary>Reads raw bytes starting at block <paramref name="paddr"/>; no checksum (file data).</summary>
    public void ReadRaw(ulong paddr, Span<byte> destination)
    {
        long offset = checked((long)paddr * BlockSize);
        if (offset < 0 || offset + destination.Length > Device.Length)
            throw new InvalidDataException($"bloc {paddr} hors du périphérique");
        Device.Read(offset, destination);
    }
}
