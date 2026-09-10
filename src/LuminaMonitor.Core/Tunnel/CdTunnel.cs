using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace LuminaMonitor.Core.Tunnel;

/// <summary>
/// The CoreDevice tunnel as opened through lockdown's CoreDeviceProxy service.
/// </summary>
/// <remarks>
/// This is the door that makes the whole project tractable on iOS 17.4+. The
/// tunnel every developer service hides behind can be reached by asking
/// lockdownd for <c>com.apple.internal.devicecompute.CoreDeviceProxy</c> — a
/// service like any other, opened inside the TLS session our pairing record
/// already earns. No remote pairing, no SRP, no Curve25519, no QUIC, no
/// pre-shared-key TLS: none of the pieces .NET lacks are on this path.
///
/// <para>The handshake is eight magic bytes, a 16-bit big-endian length and a
/// JSON body, in both directions. The reply carries the phone's tunnel
/// address, the address it assigns to us, the MTU and the port of the service
/// directory (RSD). From the next byte on, the pipe carries raw IPv6 packets
/// with no framing at all: each one is delimited by the payload length in its
/// own header, which is why the reader below parses IPv6 rather than a
/// length prefix.</para>
/// </remarks>
internal sealed class CdTunnel
{
    public const string ServiceName = "com.apple.internal.devicecompute.CoreDeviceProxy";
    private static readonly byte[] Magic = "CDTunnel"u8.ToArray();
    private const int RequestedMtu = 16000;
    private const int Ipv6HeaderSize = 40;

    private readonly Stream _pipe;

    public CdTunnel(Stream pipe) => _pipe = pipe;

    public sealed record Handshake(string ClientAddress, int Mtu, string ServerAddress, int ServerRsdPort);

    public async Task<Handshake> EstablishAsync()
    {
        var request = JsonSerializer.SerializeToUtf8Bytes(new { type = "clientHandshakeRequest", mtu = RequestedMtu });
        await WriteFrameAsync(request);

        var reply = await ReadFrameAsync();
        using var doc = JsonDocument.Parse(reply);
        var root = doc.RootElement;
        var client = root.GetProperty("clientParameters");
        return new Handshake(
            client.GetProperty("address").GetString()!,
            client.GetProperty("mtu").GetInt32(),
            root.GetProperty("serverAddress").GetString()!,
            root.GetProperty("serverRSDPort").GetInt32());
    }

    /// <summary>Raw IPv6 packet out — no framing, the header carries the length.</summary>
    public async Task SendPacketAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellation = default)
    {
        await _pipe.WriteAsync(packet, cancellation).ConfigureAwait(false);
        await _pipe.FlushAsync(cancellation).ConfigureAwait(false);
    }

    /// <summary>
    /// Raw IPv6 packet in, delimited by the payload length at bytes 4..5.
    /// The version nibble is checked first: once this stream slips by a byte,
    /// every length after it is noise, and failing loudly here beats letting
    /// every connection die of "retransmissions" a few seconds later.
    /// </summary>
    public async Task<byte[]> ReceivePacketAsync(CancellationToken cancellation = default)
    {
        byte[] header = new byte[Ipv6HeaderSize];
        await _pipe.ReadExactlyAsync(header, cancellation).ConfigureAwait(false);
        if (header[0] >> 4 != 6)
            throw new InvalidDataException($"flux tunnel desynchronise : version {header[0] >> 4}");
        int payloadLength = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4, 2));
        byte[] packet = new byte[Ipv6HeaderSize + payloadLength];
        header.CopyTo(packet, 0);
        await _pipe.ReadExactlyAsync(packet.AsMemory(Ipv6HeaderSize, payloadLength), cancellation).ConfigureAwait(false);
        return packet;
    }

    private async Task WriteFrameAsync(byte[] body)
    {
        byte[] frame = new byte[Magic.Length + 2 + body.Length];
        Magic.CopyTo(frame, 0);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(Magic.Length, 2), (ushort)body.Length);
        body.CopyTo(frame, Magic.Length + 2);
        await _pipe.WriteAsync(frame);
        await _pipe.FlushAsync();
    }

    private async Task<byte[]> ReadFrameAsync()
    {
        byte[] head = new byte[Magic.Length + 2];
        await _pipe.ReadExactlyAsync(head);
        if (!head.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException(
                $"pas de magie CDTunnel en tete de reponse : {Encoding.ASCII.GetString(head)}");
        int length = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(Magic.Length, 2));
        byte[] body = new byte[length];
        await _pipe.ReadExactlyAsync(body);
        return body;
    }
}
