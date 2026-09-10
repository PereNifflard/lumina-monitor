using System.Buffers.Binary;
using System.Net;

namespace LuminaMonitor.Core.Tunnel;

/// <summary>
/// Just enough IPv6 to originate TCP connections through the CoreDevice tunnel.
/// </summary>
/// <remarks>
/// The tunnel hands us raw IPv6 packets and expects raw IPv6 packets back.
/// Windows never sees them, so nothing in the OS network stack can help: the
/// headers, the checksums and the TCP state machine are all built here, in
/// managed code, with no driver and no elevation. The scope keeps it small —
/// one peer, a handful of client connections, a link that never loses packets
/// (USB is reliable) — so this is bookkeeping rather than a real network stack.
/// </remarks>
internal static class Ipv6
{
    public const int HeaderSize = 40;
    public const byte ProtoHopByHop = 0;
    public const byte ProtoTcp = 6;
    public const byte ProtoIcmpv6 = 58;
    public const byte ProtoUdp = 17;

    /// <param name="hopLimit">
    /// 64 for ordinary traffic; Neighbour Discovery insists on 255 and a
    /// receiver silently drops anything less.
    /// </param>
    public static byte[] Build(IPAddress src, IPAddress dst, byte nextHeader, ReadOnlySpan<byte> payload, byte hopLimit = 64)
    {
        byte[] packet = new byte[HeaderSize + payload.Length];
        var span = packet.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(span, 0x60000000);            // version 6, no traffic class / flow label
        BinaryPrimitives.WriteUInt16BigEndian(span[4..], (ushort)payload.Length);
        span[6] = nextHeader;
        span[7] = hopLimit;
        src.TryWriteBytes(span[8..24], out _);
        dst.TryWriteBytes(span[24..40], out _);
        payload.CopyTo(span[HeaderSize..]);
        return packet;
    }

    public static IPAddress Source(ReadOnlySpan<byte> packet) => new(packet.Slice(8, 16));
    public static IPAddress Destination(ReadOnlySpan<byte> packet) => new(packet.Slice(24, 16));

    /// <summary>
    /// Walks past extension headers (the phone sends Hop-by-Hop on its
    /// multicast chatter) and returns the upper-layer protocol and its offset.
    /// </summary>
    public static (byte Protocol, int Offset) UpperLayer(ReadOnlySpan<byte> packet)
    {
        byte next = packet[6];
        int offset = HeaderSize;
        while (next is 0 or 43 or 60)                                       // hop-by-hop, routing, destination options
        {
            if (offset + 2 > packet.Length)
                return (next, packet.Length);
            byte following = packet[offset];
            int length = (packet[offset + 1] + 1) * 8;
            next = following;
            offset += length;
        }
        return (next, offset);
    }

    /// <summary>Internet checksum over the IPv6 pseudo-header plus the upper-layer bytes.</summary>
    public static ushort UpperLayerChecksum(IPAddress src, IPAddress dst, byte protocol, ReadOnlySpan<byte> upper)
    {
        Span<byte> pseudo = stackalloc byte[40];
        src.TryWriteBytes(pseudo[..16], out _);
        dst.TryWriteBytes(pseudo[16..32], out _);
        BinaryPrimitives.WriteUInt32BigEndian(pseudo[32..], (uint)upper.Length);
        pseudo[36] = 0; pseudo[37] = 0; pseudo[38] = 0; pseudo[39] = protocol;

        uint sum = 0;
        sum = Accumulate(sum, pseudo);
        sum = Accumulate(sum, upper);
        while (sum >> 16 != 0)
            sum = (sum & 0xFFFF) + (sum >> 16);
        return (ushort)~sum;
    }

    private static uint Accumulate(uint sum, ReadOnlySpan<byte> data)
    {
        int i = 0;
        for (; i + 1 < data.Length; i += 2)
            sum += (uint)((data[i] << 8) | data[i + 1]);
        if (i < data.Length)
            sum += (uint)(data[i] << 8);
        return sum;
    }
}
