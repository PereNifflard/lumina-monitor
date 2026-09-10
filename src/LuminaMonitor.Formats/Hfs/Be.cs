// Big-endian field accessors. Every on-disk HFS+ structure (TN1150) is big-endian;
// only the decmpfs payload (see Decmpfs.cs) is little-endian.
using System.Buffers.Binary;

namespace LuminaMonitor.Formats.Hfs;

internal static class Be
{
    public static ushort U16(ReadOnlySpan<byte> s, int offset) => BinaryPrimitives.ReadUInt16BigEndian(s.Slice(offset, 2));
    public static short I16(ReadOnlySpan<byte> s, int offset) => BinaryPrimitives.ReadInt16BigEndian(s.Slice(offset, 2));
    public static uint U32(ReadOnlySpan<byte> s, int offset) => BinaryPrimitives.ReadUInt32BigEndian(s.Slice(offset, 4));
    public static ulong U64(ReadOnlySpan<byte> s, int offset) => BinaryPrimitives.ReadUInt64BigEndian(s.Slice(offset, 8));

    public static void WriteU16(Span<byte> s, int offset, ushort value) => BinaryPrimitives.WriteUInt16BigEndian(s.Slice(offset, 2), value);
    public static void WriteI16(Span<byte> s, int offset, short value) => BinaryPrimitives.WriteInt16BigEndian(s.Slice(offset, 2), value);
    public static void WriteU32(Span<byte> s, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(s.Slice(offset, 4), value);
    public static void WriteU64(Span<byte> s, int offset, ulong value) => BinaryPrimitives.WriteUInt64BigEndian(s.Slice(offset, 8), value);
}
