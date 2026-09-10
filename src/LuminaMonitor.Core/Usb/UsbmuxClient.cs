using System.Buffers.Binary;
using System.Net.Sockets;

namespace LuminaMonitor.Core.Usb;

/// <summary>
/// Talks to Apple's USB multiplexer on Windows: the process behind the
/// "Appareils Apple" app that owns the iPhone's USB interface and hands out
/// virtual connections to on-device services.
/// </summary>
/// <remarks>
/// On Windows the multiplexer listens on TCP 127.0.0.1:27015 (a Unix socket
/// on macOS/Linux). Every message is a 16-byte little-endian header — total
/// length, protocol version (1 = plist), message type (8 = plist), a tag the
/// reply echoes — followed by an XML property list. That is the whole
/// protocol: three integers and a plist, which is why the client fits in a
/// single file and owes nothing to any library.
///
/// <para>A <c>Connect</c> request is special: once the multiplexer answers
/// <c>Number = 0</c>, the very same TCP connection stops being a control
/// channel and becomes a raw byte pipe to the requested port on the phone.
/// <see cref="ConnectToDeviceAsync"/> hands that pipe over as a stream and
/// the client must not be used for control messages afterwards.</para>
/// </remarks>
internal sealed class UsbmuxClient : IDisposable
{
    private const int HeaderSize = 16;
    private const uint VersionPlist = 1;
    private const uint TypePlist = 8;

    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private uint _tag;

    private UsbmuxClient(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
    }

    /// <summary>How long a loopback socket is given before the door counts as jammed.</summary>
    /// <remarks>
    /// Loopback answers in microseconds when it answers at all; five seconds is
    /// there for the case where nothing comes back, which is a different
    /// failure with a different remedy. Windows would otherwise spend about
    /// twenty seconds retransmitting the SYN before giving up.
    /// </remarks>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The two ways the multiplexer can be out of reach, told apart.</summary>
    /// <remarks>
    /// A refused connection means nothing is listening: the port is closed, the
    /// service is not running, and the answer came back instantly. A connection
    /// that expires means something <i>is</i> listening and stopped answering —
    /// the SYN goes out and nothing returns — which the Apple process does when
    /// it wedges. Only the second one is fixed by restarting it, and telling
    /// someone to restart a program that is not running wastes their time.
    ///
    /// <para>Neither case ever kills the Apple process: it owns the USB
    /// interface and the pairing records, and this project's whole claim is
    /// that it leaves Apple's own component alone.</para>
    /// </remarks>
    public static async Task<UsbmuxClient> ConnectAsync(string host = "127.0.0.1", int port = 27015)
    {
        // Every byte to the phone rides this loopback socket: input reports are
        // tiny writes sent back to back, and Nagle plus delayed ACK would hold
        // the second one for up to 200 ms. Send at once.
        var client = new TcpClient { NoDelay = true };
        try
        {
            using var deadline = new CancellationTokenSource(ConnectTimeout);
            await client.ConnectAsync(host, port, deadline.Token);
        }
        catch (OperationCanceledException)
        {
            client.Dispose();
            throw new LuminaException("Le multiplexeur Apple ne répond plus : relance l'app Appareils Apple (ou rebranche le câble).")
                { AppleMultiplexer = true };
        }
        catch (SocketException exception)
        {
            client.Dispose();
            throw new LuminaException("Le multiplexeur Apple n'est pas lancé : ouvre l'app Appareils Apple ou branche l'iPhone.", exception)
                { AppleMultiplexer = true };
        }
        return new UsbmuxClient(client);
    }

    /// <summary>
    /// Bytes already delivered by Windows and still unread on this socket.
    /// </summary>
    /// <remarks>
    /// The one number that tells a slow reader from a slow sender. Everything
    /// the phone sends crosses this socket, so a backlog sitting here is delay
    /// we are adding ourselves: the bytes are in the machine, decoded or not,
    /// and nobody has picked them up. It stays at a few kilobytes when the
    /// pump keeps up, whatever the bitrate. Zero once the socket is closed —
    /// asking a disposed <see cref="TcpClient"/> throws, and a counter is not
    /// worth an exception.
    /// </remarks>
    public int Available
    {
        get
        {
            try { return _client.Client is null ? 0 : _client.Available; }
            catch (Exception) { return 0; }
        }
    }

    /// <summary>Sends one plist request and returns the plist reply.</summary>
    public async Task<Dictionary<string, object>> RequestAsync(string messageType, Dictionary<string, object>? extra = null)
    {
        await SendAsync(messageType, extra);
        return await ReceiveAsync();
    }

    /// <summary>
    /// Turns this connection into a pipe to <paramref name="port"/> on the
    /// device. The port travels in network byte order inside a 16-bit field,
    /// which is why 62078 (lockdown) goes on the wire as 32498.
    /// </summary>
    public async Task<Stream> ConnectToDeviceAsync(long deviceId, int port)
    {
        int swapped = ((port & 0xFF) << 8) | ((port >> 8) & 0xFF);
        var reply = await RequestAsync("Connect", new Dictionary<string, object>
        {
            ["DeviceID"] = deviceId,
            ["PortNumber"] = (long)swapped,
        });
        long number = reply.TryGetValue("Number", out var n) && n is long l ? l : -1;
        if (number != 0)
            throw new IOException($"Connect vers le port {port} refuse (Number={number})");
        return _stream;
    }

    public async Task SendAsync(string messageType, Dictionary<string, object>? extra = null)
    {
        var dict = new Dictionary<string, object>
        {
            ["MessageType"] = messageType,
            ["ClientVersionString"] = "luminamonitor-usbprobe",
            ["ProgName"] = "luminamonitor",
            ["kLibUSBMuxVersion"] = 3L,
        };
        if (extra is not null)
            foreach (var (k, v) in extra) dict[k] = v;

        byte[] payload = Plist.Write(dict);
        byte[] header = new byte[HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0), (uint)(HeaderSize + payload.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), VersionPlist);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), TypePlist);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), ++_tag);

        await _stream.WriteAsync(header);
        await _stream.WriteAsync(payload);
    }

    public async Task<Dictionary<string, object>> ReceiveAsync()
    {
        byte[] header = new byte[HeaderSize];
        await _stream.ReadExactlyAsync(header);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0));
        uint type = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
        if (length < HeaderSize)
            throw new InvalidDataException($"longueur usbmux absurde : {length}");
        if (type != TypePlist)
            throw new InvalidDataException($"message usbmux non-plist : type {type}");

        byte[] payload = new byte[length - HeaderSize];
        await _stream.ReadExactlyAsync(payload);
        return Plist.Read(payload) as Dictionary<string, object>
            ?? throw new InvalidDataException("reponse usbmux qui n'est pas un dict");
    }

    public void Dispose()
    {
        _stream.Dispose();
        _client.Dispose();
    }
}
