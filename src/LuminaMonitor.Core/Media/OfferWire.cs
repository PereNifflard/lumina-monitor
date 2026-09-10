using System.IO.Compression;

namespace LuminaMonitor.Core.Media;

/// <summary>
/// The wire format both negotiator offers are written in: protobuf by hand,
/// zlib at its best level, and the little host-identity message.
/// </summary>
/// <remarks>
/// Shared by <see cref="MediaOffer"/> (video, negotiator mode 5) and
/// <see cref="AudioOffer"/> (audio, mode 6), because the two offers differ
/// only in their settings message and their tier table — everything around
/// them, down to the padded five-byte session id and the compression level the
/// daemon insists on, is the same. No protobuf library: the schema was never
/// published, so there is nothing to compile, and a field is a tag and a
/// varint.
/// </remarks>
internal static class OfferWire
{
    /// <summary>The decoder identity both offers embed; the daemon matches on the string.</summary>
    public const string DecoderName = "Viceroy 1.7.0";

    public static byte[] Varint(ulong value)
    {
        var o = new List<byte>();
        while (true)
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0) o.Add((byte)(b | 0x80));
            else { o.Add(b); return [.. o]; }
        }
    }

    /// <summary>A varint padded with redundant continuation bytes to a fixed width, as Apple's captures do.</summary>
    /// <remarks>
    /// Protobuf tolerates continuation bytes whose seven bits are zero without
    /// changing the value; Apple's offers use that for the five-byte session-id
    /// slot, presumably so the field can be rewritten in place.
    /// </remarks>
    public static byte[] VarintPadded(ulong value, int width)
    {
        var raw = new List<byte>(Varint(value & ((1UL << (7 * width)) - 1)));
        while (raw.Count < width)
        {
            raw[^1] |= 0x80;
            raw.Add(0x00);
        }
        return [.. raw];
    }

    public static byte[] Tag(int field, int wire) => Varint((ulong)((field << 3) | wire));

    public static byte[] FVarint(int field, long value) => [.. Tag(field, 0), .. Varint((ulong)value)];

    public static byte[] FBytes(int field, byte[] value) => [.. Tag(field, 2), .. Varint((ulong)value.Length), .. value];

    public static byte[] FString(int field, string value) => FBytes(field, System.Text.Encoding.UTF8.GetBytes(value));

    /// <summary>One tier of the top-level repeated table (field 9): kind, value, and an optional buffer cap.</summary>
    public static byte[] Tier(long kind, long value, long? cap)
    {
        var tier = new List<byte>();
        tier.AddRange(FVarint(1, kind));
        tier.AddRange(FVarint(2, value));
        if (cap is long c) tier.AddRange(FVarint(3, c));
        return FBytes(9, [.. tier]);
    }

    /// <summary>The <c>avcMediaStreamOptionRemoteEndpointInfo</c> message: who we say we are.</summary>
    public static byte[] EndpointInfo(string model, string osVersion, string build)
    {
        var b = new List<byte>();
        b.AddRange(FVarint(1, 0));
        b.AddRange(FVarint(2, 1));
        b.AddRange(FString(3, model));
        b.AddRange(FString(4, osVersion));
        b.AddRange(FString(5, build));
        return [.. b];
    }

    /// <summary>
    /// zlib, best level. The daemon answers a bare "Invalid Parameter" to
    /// anything else — the default level produces a different byte stream and
    /// is refused.
    /// </summary>
    public static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            zlib.Write(data);
        return output.ToArray();
    }

    /// <summary>
    /// Inflates a zlib stream, for reading back what the phone answered.
    /// </summary>
    /// <remarks>
    /// The answer's media blob is compressed the same way the offer is. This
    /// only ever runs on the probe's side of a diagnostic, so a stream that is
    /// not zlib at all returns null rather than throwing.
    /// </remarks>
    public static byte[]? Inflate(ReadOnlySpan<byte> data)
    {
        try
        {
            using var input = new MemoryStream(data.ToArray());
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            zlib.CopyTo(output);
            return output.Length == 0 ? null : output.ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
