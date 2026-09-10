using System.Buffers.Binary;
using System.Text;

namespace LuminaMonitor.Core.Media;

/// <summary>
/// Writer for Apple's binary property list (<c>bplist00</c>), the subset the
/// media negotiator offer needs: dictionaries, ASCII strings, integers, data.
/// </summary>
/// <remarks>
/// The layout follows CFBinaryPList: an 8-byte header, the flattened object
/// table, an offset table, and a 32-byte trailer naming the sizes of the
/// offsets and object references. Dictionary keys are written in sorted
/// order and equal scalars are shared, which is how CPython's plistlib lays
/// the same dictionary out — useful when a daemon compares blobs rather than
/// parsing them.
/// </remarks>
internal static class Bplist
{
    public static byte[] Write(Dictionary<string, object> root)
    {
        var writer = new State();
        int top = writer.Flatten(root);
        return writer.Emit(top);
    }

    private sealed class State
    {
        private readonly List<object> _objects = [];
        private readonly Dictionary<(Type, object), int> _scalars = new();
        private readonly List<int[]> _dictRefs = [];   // parallel to dictionaries in _objects, by object index
        private readonly Dictionary<int, (int[] Keys, int[] Values)> _dicts = new();

        public int Flatten(object value)
        {
            switch (value)
            {
                case Dictionary<string, object> dict:
                {
                    int index = _objects.Count;
                    _objects.Add(dict);
                    var ordered = dict.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
                    int[] keys = new int[ordered.Count];
                    int[] values = new int[ordered.Count];
                    for (int i = 0; i < ordered.Count; i++)
                        keys[i] = Flatten(ordered[i].Key);
                    for (int i = 0; i < ordered.Count; i++)
                        values[i] = Flatten(ordered[i].Value);
                    _dicts[index] = (keys, values);
                    return index;
                }
                case string or long or int or byte[]:
                {
                    object key = value is int i ? (long)i : value is byte[] b ? Convert.ToBase64String(b) : value;
                    var lookup = (value.GetType(), key);
                    if (_scalars.TryGetValue(lookup, out int existing))
                        return existing;
                    int index = _objects.Count;
                    _objects.Add(value is int i2 ? (long)i2 : value);
                    _scalars[lookup] = index;
                    return index;
                }
                default:
                    throw new ArgumentException($"type non gere en bplist : {value.GetType().Name}");
            }
        }

        public byte[] Emit(int top)
        {
            int refSize = _objects.Count < 256 ? 1 : _objects.Count < 65536 ? 2 : 4;
            var body = new MemoryStream();
            body.Write("bplist00"u8);
            var offsets = new long[_objects.Count];

            for (int i = 0; i < _objects.Count; i++)
            {
                offsets[i] = body.Position;
                switch (_objects[i])
                {
                    case Dictionary<string, object>:
                    {
                        var (keys, values) = _dicts[i];
                        WriteMarker(body, 0xD0, keys.Length);
                        foreach (int k in keys) WriteRef(body, k, refSize);
                        foreach (int v in values) WriteRef(body, v, refSize);
                        break;
                    }
                    case string s:
                    {
                        if (s.All(c => c < 128))
                        {
                            WriteMarker(body, 0x50, s.Length);
                            body.Write(Encoding.ASCII.GetBytes(s));
                        }
                        else
                        {
                            WriteMarker(body, 0x60, s.Length);
                            body.Write(Encoding.BigEndianUnicode.GetBytes(s));
                        }
                        break;
                    }
                    case long n:
                        WriteInt(body, n);
                        break;
                    case byte[] data:
                        WriteMarker(body, 0x40, data.Length);
                        body.Write(data);
                        break;
                }
            }

            long tableOffset = body.Position;
            int offsetSize = tableOffset < 256 ? 1 : tableOffset < 65536 ? 2 : 4;
            foreach (long o in offsets)
                WriteSized(body, o, offsetSize);

            Span<byte> trailer = stackalloc byte[32];
            trailer[6] = (byte)offsetSize;
            trailer[7] = (byte)refSize;
            BinaryPrimitives.WriteUInt64BigEndian(trailer[8..], (ulong)_objects.Count);
            BinaryPrimitives.WriteUInt64BigEndian(trailer[16..], (ulong)top);
            BinaryPrimitives.WriteUInt64BigEndian(trailer[24..], (ulong)tableOffset);
            body.Write(trailer);
            return body.ToArray();
        }

        private static void WriteMarker(Stream s, int type, int count)
        {
            if (count < 15)
            {
                s.WriteByte((byte)(type | count));
                return;
            }
            s.WriteByte((byte)(type | 0x0F));
            WriteInt(s, count);
        }

        private static void WriteInt(Stream s, long n)
        {
            Span<byte> buf = stackalloc byte[8];
            if (n >= 0 && n < 256) { s.WriteByte(0x10); s.WriteByte((byte)n); }
            else if (n >= 0 && n < 65536) { s.WriteByte(0x11); BinaryPrimitives.WriteUInt16BigEndian(buf, (ushort)n); s.Write(buf[..2]); }
            else if (n >= 0 && n < (1L << 32)) { s.WriteByte(0x12); BinaryPrimitives.WriteUInt32BigEndian(buf, (uint)n); s.Write(buf[..4]); }
            else { s.WriteByte(0x13); BinaryPrimitives.WriteInt64BigEndian(buf, n); s.Write(buf); }
        }

        private static void WriteRef(Stream s, int index, int size) => WriteSized(s, index, size);

        private static void WriteSized(Stream s, long value, int size)
        {
            Span<byte> buf = stackalloc byte[4];
            switch (size)
            {
                case 1: s.WriteByte((byte)value); break;
                case 2: BinaryPrimitives.WriteUInt16BigEndian(buf, (ushort)value); s.Write(buf[..2]); break;
                default: BinaryPrimitives.WriteUInt32BigEndian(buf, (uint)value); s.Write(buf); break;
            }
        }
    }
}
