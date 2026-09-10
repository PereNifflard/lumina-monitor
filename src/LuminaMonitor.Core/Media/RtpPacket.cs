using System.Buffers.Binary;

namespace LuminaMonitor.Core.Media;

/// <summary>
/// One datagram of the display stream, read as RTP (RFC 3550).
/// </summary>
/// <remarks>
/// The phone multiplexes RTCP on the same UDP port as the RTP, so the first
/// thing to decide about a datagram is which of the two it is: payload types
/// 200 to 206 are RTCP, everything else is media. The header itself holds no
/// surprise except the extension the phone always sends (profile 0x9001, one
/// 32-bit word) — it carries nothing this project needs and is skipped, but a
/// reader that forgets it feeds four stray bytes to the depacketizer and
/// every NAL comes out wrong.
/// </remarks>
internal readonly ref struct RtpPacket
{
    private RtpPacket(bool valid, byte payloadType, byte rawType, bool marker, ushort sequence, uint timestamp, uint ssrc,
        ReadOnlySpan<byte> payload)
    {
        Valid = valid;
        PayloadType = payloadType;
        RawType = rawType;
        Marker = marker;
        Sequence = sequence;
        Timestamp = timestamp;
        Ssrc = ssrc;
        Payload = payload;
    }

    public bool Valid { get; }
    public byte PayloadType { get; }

    /// <summary>The second header byte whole: RTCP packet types live there, unmasked.</summary>
    public byte RawType { get; }
    public bool Marker { get; }
    public ushort Sequence { get; }
    public uint Timestamp { get; }
    public uint Ssrc { get; }
    public ReadOnlySpan<byte> Payload { get; }

    /// <summary>True for the control packets the phone multiplexes on the media port.</summary>
    public bool IsRtcp => RawType is >= 200 and <= 206;

    /// <summary>
    /// True for the control payload types, read straight off a datagram.
    /// </summary>
    /// <remarks>
    /// The whole second byte, not the low seven bits: RTCP has no marker bit,
    /// so its packet type owns all eight. Masking first — the reflex from the
    /// RTP header — turns a sender report's 200 into 72 and hands a control
    /// packet to the depacketizer, where its length field reads as a wild
    /// sequence number and every following packet looks lost.
    /// </remarks>
    public static bool LooksLikeRtcp(ReadOnlySpan<byte> datagram) =>
        datagram.Length >= 2 && datagram[1] is >= 200 and <= 206;

    /// <summary>Reads a datagram; <see cref="Valid"/> is false when it is too short or not version 2.</summary>
    public static RtpPacket Parse(ReadOnlySpan<byte> datagram)
    {
        if (datagram.Length < 12 || datagram[0] >> 6 != 2)
            return default;

        bool padding = (datagram[0] & 0x20) != 0;
        bool extension = (datagram[0] & 0x10) != 0;
        int csrcCount = datagram[0] & 0x0F;
        byte payloadType = (byte)(datagram[1] & 0x7F);
        bool marker = (datagram[1] & 0x80) != 0;
        ushort sequence = BinaryPrimitives.ReadUInt16BigEndian(datagram[2..]);
        uint timestamp = BinaryPrimitives.ReadUInt32BigEndian(datagram[4..]);
        uint ssrc = BinaryPrimitives.ReadUInt32BigEndian(datagram[8..]);

        int start = 12 + 4 * csrcCount;
        if (extension)
        {
            if (start + 4 > datagram.Length)
                return default;
            start += 4 + 4 * BinaryPrimitives.ReadUInt16BigEndian(datagram[(start + 2)..]);
        }
        int end = datagram.Length;
        if (padding && end > start && datagram[end - 1] > 0 && end - datagram[end - 1] >= start)
            end -= datagram[end - 1];
        if (start > end || end > datagram.Length)
            return default;

        return new RtpPacket(true, payloadType, datagram[1], marker, sequence, timestamp, ssrc, datagram[start..end]);
    }
}
