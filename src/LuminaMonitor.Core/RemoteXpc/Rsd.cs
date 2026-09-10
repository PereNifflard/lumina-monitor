using System.Security.Cryptography;
using LuminaMonitor.Core.Tunnel;

namespace LuminaMonitor.Core.RemoteXpc;

/// <summary>
/// The phone's service directory over the tunnel, and the way into any
/// service it lists.
/// </summary>
/// <remarks>
/// RSD answers one handshake with the whole catalogue: name → port, plus
/// whether the service speaks RemoteXPC or the older plist dialect. Reaching
/// a RemoteXPC service is then a fresh TCP connection through the tunnel to
/// that port and a fresh RemoteXPC opening on it — every service gets its own
/// connection and its own message-id sequence.
/// </remarks>
internal sealed class Rsd
{
    private readonly TunnelNet _net;
    private readonly Dictionary<string, (int Port, bool RemoteXpc)> _services = new();

    public Dictionary<string, object?> Properties { get; private set; } = new();
    public IReadOnlyDictionary<string, (int Port, bool RemoteXpc)> Services => _services;

    public Rsd(TunnelNet net) => _net = net;

    public async Task LoadAsync(int rsdPort)
    {
        using var tcp = await _net.ConnectTcpAsync(rsdPort);
        using var xpc = new RemoteXpc(tcp);
        await xpc.ConnectAsync();
        var reply = await xpc.SendReceiveAsync(new Dictionary<string, object?>
        {
            ["MessageType"] = "Handshake",
            ["MessagingProtocolVersion"] = new XpcUInt64(7),
            ["UUID"] = new XpcUuid(RandomNumberGenerator.GetBytes(16)),
            ["Properties"] = new Dictionary<string, object?>
            {
                ["RemoteXPCVersionFlags"] = new XpcUInt64(0x0100000000000006),
                ["SensitivePropertiesVisible"] = true,
            },
            ["Services"] = new Dictionary<string, object?>(),
        });

        if (reply.GetValueOrDefault("Properties") is Dictionary<string, object?> props)
            Properties = props;
        if (reply.GetValueOrDefault("Services") is Dictionary<string, object?> services)
        {
            foreach (var (name, entry) in services)
            {
                if (entry is not Dictionary<string, object?> e) continue;
                int port = e.GetValueOrDefault("Port") switch
                {
                    XpcUInt64 u => (int)u.Value,
                    XpcInt64 i => (int)i.Value,
                    string s => int.Parse(s),
                    _ => -1,
                };
                bool xpcService = e.GetValueOrDefault("Properties") is Dictionary<string, object?> ep
                    && ep.GetValueOrDefault("UsesRemoteXPC") is true;
                _services[name] = (port, xpcService);
            }
        }
    }

    /// <summary>Opens a RemoteXPC service by name: new TCP connection, new XPC opening.</summary>
    /// <param name="writePatience">
    /// How long a write may wait on the phone's receive window before this
    /// service's connection is declared stuck; null waits for as long as it
    /// takes, which is what a transfer wants. The input channels pass a second:
    /// see <see cref="Tunnel.TcpConnection.WritePatience"/>.
    /// </param>
    public async Task<RemoteXpc> OpenAsync(string name, Action<string>? log = null, TimeSpan? writePatience = null)
    {
        if (!_services.TryGetValue(name, out var entry))
            throw new InvalidOperationException($"Service absent de l'annuaire : {name}");
        var tcp = await _net.ConnectTcpAsync(entry.Port, writePatience);
        var xpc = new RemoteXpc(tcp) { Log = log };
        try
        {
            await xpc.ConnectAsync();
        }
        catch (Exception)
        {
            // A daemon that never sent its SETTINGS still has our TCP
            // connection, and this failure is now retried rather than fatal:
            // left alone it would leak one connection per attempt into a tunnel
            // that has to survive the whole recovery.
            xpc.Dispose();
            throw;
        }
        return xpc;
    }
}
