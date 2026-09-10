using System.Buffers.Binary;
using System.Text;

namespace LuminaMonitor.Core.RemoteXpc;

/// <summary>An XPC unsigned 64-bit value — distinct from a signed one on the wire.</summary>
internal readonly record struct XpcUInt64(ulong Value);

/// <summary>An XPC signed 64-bit value.</summary>
internal readonly record struct XpcInt64(long Value);

/// <summary>An XPC UUID, kept as raw bytes because the wire is not Guid-ordered.</summary>
internal readonly record struct XpcUuid(byte[] Bytes);

/// <summary>
/// Apple's binary XPC object serialisation, as carried by RemoteXPC.
/// </summary>
/// <remarks>
/// A message is a little-endian wrapper — magic, flags, body length, message
/// id — followed, when there is a body, by a body header (magic, version 5)
/// and one typed object. Every object starts with a 4-byte type tag; strings
/// and data are length-prefixed and padded to four bytes; dictionaries and
/// arrays carry a payload length and an entry count. Signed and unsigned
/// integers are different types on the wire, and the CoreDevice daemons
/// decode with a strict Swift codec that rejects the wrong one — which is why
/// the caller spells the type out with <see cref="XpcUInt64"/> and
/// <see cref="XpcInt64"/> rather than passing a bare number.
/// </remarks>
internal static class Xpc
{
    public const uint WrapperMagic = 0x29B00B92;
    public const uint BodyMagic = 0x42133742;
    public const uint BodyVersion = 5;

    public const uint FlagAlwaysSet = 0x00000001;
    public const uint FlagData = 0x00000100;
    public const uint FlagWantingReply = 0x00010000;
    public const uint FlagReply = 0x00020000;
    public const uint FlagInitHandshake = 0x00400000;

    private const uint TagNull = 0x1000, TagBool = 0x2000, TagInt64 = 0x3000, TagUInt64 = 0x4000,
        TagDouble = 0x5000, TagDate = 0x7000, TagData = 0x8000, TagString = 0x9000, TagUuid = 0xA000,
        TagArray = 0xE000, TagDictionary = 0xF000;

    public sealed record Message(uint Flags, ulong MessageId, object? Body);

    // --- Wrapper ---------------------------------------------------------------

    public static byte[] BuildWrapper(uint flags, ulong messageId, Dictionary<string, object?>? body)
    {
        byte[] encoded = body is null ? [] : EncodeBody(body);
        byte[] wrapper = new byte[24 + encoded.Length];
        var span = wrapper.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, WrapperMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], flags);
        BinaryPrimitives.WriteUInt64LittleEndian(span[8..], (ulong)encoded.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(span[16..], messageId);
        encoded.CopyTo(span[24..]);
        return wrapper;
    }

    public static Message ParseWrapper(ReadOnlySpan<byte> data)
    {
        if (data.Length < 24 || BinaryPrimitives.ReadUInt32LittleEndian(data) != WrapperMagic)
            throw new InvalidDataException("pas une enveloppe XPC");
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        ulong length = BinaryPrimitives.ReadUInt64LittleEndian(data[8..]);
        ulong id = BinaryPrimitives.ReadUInt64LittleEndian(data[16..]);
        object? body = null;
        if (length > 0)
        {
            var bodySpan = data.Slice(24, (int)length);
            if (BinaryPrimitives.ReadUInt32LittleEndian(bodySpan) != BodyMagic)
                throw new InvalidDataException("pas un corps XPC");
            int offset = 8;
            body = Decode(bodySpan, ref offset);
        }
        return new Message(flags, id, body);
    }

    private static byte[] EncodeBody(Dictionary<string, object?> body)
    {
        var buffer = new List<byte>(256);
        WriteU32(buffer, BodyMagic);
        WriteU32(buffer, BodyVersion);
        Encode(buffer, body);
        return [.. buffer];
    }

    // --- Encoding -------------------------------------------------------------

    private static void Encode(List<byte> b, object? value)
    {
        switch (value)
        {
            case null: WriteU32(b, TagNull); break;
            case bool flag: WriteU32(b, TagBool); b.Add(flag ? (byte)1 : (byte)0); b.Add(0); b.Add(0); b.Add(0); break;
            case XpcInt64 i: WriteU32(b, TagInt64); WriteU64(b, unchecked((ulong)i.Value)); break;
            case XpcUInt64 u: WriteU32(b, TagUInt64); WriteU64(b, u.Value); break;
            case double d: WriteU32(b, TagDouble); WriteU64(b, BitConverter.DoubleToUInt64Bits(d)); break;
            case byte[] bytes:
                WriteU32(b, TagData); WriteU32(b, (uint)bytes.Length); b.AddRange(bytes); Pad(b, bytes.Length); break;
            case string s:
            {
                byte[] utf8 = Encoding.UTF8.GetBytes(s);
                WriteU32(b, TagString); WriteU32(b, (uint)(utf8.Length + 1));
                b.AddRange(utf8); b.Add(0); Pad(b, utf8.Length + 1);
                break;
            }
            case XpcUuid uuid:
                if (uuid.Bytes.Length != 16) throw new ArgumentException("un UUID XPC fait 16 octets");
                WriteU32(b, TagUuid); b.AddRange(uuid.Bytes); break;
            case DateTime date:
                WriteU32(b, TagDate);
                WriteU64(b, unchecked((ulong)((date.ToUniversalTime() - DateTime.UnixEpoch).Ticks * 100)));
                break;
            case IDictionary<string, object?> dict:
            {
                var inner = new List<byte>();
                WriteU32(inner, (uint)dict.Count);
                foreach (var (key, item) in dict)
                {
                    byte[] k = Encoding.UTF8.GetBytes(key);
                    inner.AddRange(k); inner.Add(0); Pad(inner, k.Length + 1);
                    Encode(inner, item);
                }
                WriteU32(b, TagDictionary); WriteU32(b, (uint)inner.Count); b.AddRange(inner);
                break;
            }
            // After dictionaries on purpose, and covariant so that a
            // List<Dictionary<…>> — what createService needs — is accepted.
            case IEnumerable<object?> sequence:
            {
                var list = sequence.ToList();
                var inner = new List<byte>();
                WriteU32(inner, (uint)list.Count);
                foreach (var item in list) Encode(inner, item);
                WriteU32(b, TagArray); WriteU32(b, (uint)inner.Count); b.AddRange(inner);
                break;
            }
            case long l: throw new ArgumentException($"entier ambigu ({l}) : utiliser XpcInt64 ou XpcUInt64");
            case int i32: throw new ArgumentException($"entier ambigu ({i32}) : utiliser XpcInt64 ou XpcUInt64");
            default: throw new ArgumentException($"type XPC non gere : {value.GetType().Name}");
        }
    }

    private static void Pad(List<byte> b, int length)
    {
        for (int pad = (4 - length % 4) % 4; pad > 0; pad--) b.Add(0);
    }

    private static void WriteU32(List<byte> b, uint v)
    {
        Span<byte> tmp = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(tmp, v);
        b.AddRange(tmp.ToArray());
    }

    private static void WriteU64(List<byte> b, ulong v)
    {
        Span<byte> tmp = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(tmp, v);
        b.AddRange(tmp.ToArray());
    }

    // --- Decoding -------------------------------------------------------------

    private static object? Decode(ReadOnlySpan<byte> data, ref int offset)
    {
        uint tag = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
        offset += 4;
        switch (tag)
        {
            case TagNull: return null;
            case TagBool: { bool v = data[offset] != 0; offset += 4; return v; }
            case TagInt64: { long v = BinaryPrimitives.ReadInt64LittleEndian(data[offset..]); offset += 8; return new XpcInt64(v); }
            case TagUInt64: { ulong v = BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]); offset += 8; return new XpcUInt64(v); }
            case TagDouble: { double v = BitConverter.UInt64BitsToDouble(BinaryPrimitives.ReadUInt64LittleEndian(data[offset..])); offset += 8; return v; }
            case TagDate: { long ns = BinaryPrimitives.ReadInt64LittleEndian(data[offset..]); offset += 8; return DateTime.UnixEpoch.AddTicks(ns / 100); }
            case TagData:
            {
                int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]); offset += 4;
                byte[] v = data.Slice(offset, length).ToArray();
                offset += length + (4 - length % 4) % 4;
                return v;
            }
            case TagString:
            {
                int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]); offset += 4;
                string v = Encoding.UTF8.GetString(data.Slice(offset, Math.Max(0, length - 1)));
                offset += length + (4 - length % 4) % 4;
                return v;
            }
            case TagUuid: { byte[] v = data.Slice(offset, 16).ToArray(); offset += 16; return new XpcUuid(v); }
            case TagArray:
            {
                offset += 4; // payload length, implied by the walk
                int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]); offset += 4;
                if (count < 0 || count > (data.Length - offset) / 4)
                    throw new InvalidDataException($"tableau XPC de {count} elements pour {data.Length - offset} octets");
                var list = new List<object?>(count);
                for (int i = 0; i < count; i++) list.Add(Decode(data, ref offset));
                return list;
            }
            case TagDictionary:
            {
                offset += 4;
                int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]); offset += 4;
                if (count < 0 || count > (data.Length - offset) / 8)
                    throw new InvalidDataException($"dictionnaire XPC de {count} entrees pour {data.Length - offset} octets");
                var dict = new Dictionary<string, object?>(count);
                for (int i = 0; i < count; i++)
                {
                    int end = data[offset..].IndexOf((byte)0);
                    string key = Encoding.UTF8.GetString(data.Slice(offset, end));
                    offset += end + 1 + (4 - (end + 1) % 4) % 4;
                    dict[key] = Decode(data, ref offset);
                }
                return dict;
            }
            default:
                throw new InvalidDataException($"etiquette XPC inconnue 0x{tag:X} a l'offset {offset - 4}");
        }
    }

    /// <summary>Indented dump for the console.</summary>
    public static string Dump(object? value, int indent = 0)
    {
        var pad = new string(' ', indent * 2);
        switch (value)
        {
            case IDictionary<string, object?> dict:
            {
                var sb = new StringBuilder();
                foreach (var (k, v) in dict)
                    if (v is IDictionary<string, object?> or IList<object?>)
                        sb.Append(pad).Append(k).Append(":\n").Append(Dump(v, indent + 1));
                    else
                        sb.Append(pad).Append(k).Append(" = ").Append(Dump(v)).Append('\n');
                return sb.ToString();
            }
            case IList<object?> list:
            {
                var sb = new StringBuilder();
                for (int i = 0; i < list.Count; i++)
                    sb.Append(pad).Append('[').Append(i).Append("] ").Append(Dump(list[i], indent + 1).TrimStart()).Append(list[i] is IDictionary<string, object?> or IList<object?> ? "" : "\n");
                return sb.ToString();
            }
            case XpcUInt64 u: return u.Value.ToString();
            case XpcInt64 i: return i.Value.ToString();
            case XpcUuid id: return Convert.ToHexString(id.Bytes);
            case byte[] bytes: return $"<{bytes.Length} octets>";
            case null: return "null";
            default: return value.ToString() ?? "";
        }
    }
}
