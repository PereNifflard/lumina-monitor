using System.Buffers.Binary;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace LuminaMonitor.Core.Usb;

/// <summary>
/// Conversation with lockdownd, the phone's gatekeeper on port 62078: identity
/// in the clear, then a TLS session opened with the pairing record, then the
/// keys and services the phone reserves for trusted hosts.
/// </summary>
/// <remarks>
/// Lockdown frames each plist with a 4-byte big-endian length — the opposite
/// endianness from the multiplexer, which is the kind of detail that costs an
/// afternoon when it is guessed rather than read.
///
/// <para>The session dance is exact: <c>StartSession</c> travels in the clear
/// with the host identity from the pairing record; the phone answers
/// <c>EnableSessionSSL = true</c>, and from the very next byte the same pipe
/// carries TLS, with our host certificate as the client identity and the
/// phone's own certificate on the other side. Nothing is renegotiated at the
/// plist level — the framing continues unchanged inside the tunnel.</para>
///
/// <para>The phone's certificate is signed by the root in the pairing record,
/// not by any public authority, so the usual chain validation is switched
/// off and replaced by nothing: the pairing record itself is the trust
/// anchor, and a phone that lacks it simply refuses the session.</para>
/// </remarks>
internal sealed class LockdownClient
{
    private Stream _stream;
    private readonly string _label;

    public LockdownClient(Stream stream, string label = "luminamonitor")
    {
        _stream = stream;
        _label = label;
    }

    public bool SessionOpen { get; private set; }

    public async Task<Dictionary<string, object>> RequestAsync(string request, Dictionary<string, object>? extra = null)
    {
        var dict = new Dictionary<string, object>
        {
            ["Label"] = _label,
            ["Request"] = request,
        };
        if (extra is not null)
            foreach (var (k, v) in extra) dict[k] = v;

        await PlistService.WriteAsync(_stream, dict);
        return await PlistService.ReadAsync(_stream);
    }

    public Task<Dictionary<string, object>> QueryTypeAsync() => RequestAsync("QueryType");

    /// <summary>Raw reply, so the caller can see the error name when refused.</summary>
    public Task<Dictionary<string, object>> GetValueRawAsync(string key, string? domain = null)
    {
        var extra = new Dictionary<string, object> { ["Key"] = key };
        if (domain is not null)
            extra["Domain"] = domain;
        return RequestAsync("GetValue", extra);
    }

    /// <summary>
    /// Opens the TLS session with the pairing record. Returns the phone's
    /// refusal name when it does not trust this host, null on success.
    /// </summary>
    public async Task<string?> StartSessionAsync(PairRecord record)
    {
        var reply = await RequestAsync("StartSession", new Dictionary<string, object>
        {
            ["HostID"] = record.HostId,
            ["SystemBUID"] = record.SystemBuid,
        });
        if (reply.TryGetValue("Error", out var error))
            return error.ToString();

        bool ssl = reply.TryGetValue("EnableSessionSSL", out var e) && e is true;
        if (ssl)
            _stream = await PlistService.WrapTlsAsync(_stream, record.HostCertificate);
        SessionOpen = true;
        return null;
    }

    /// <summary>Asks lockdown to start a service; returns its port and whether it wants TLS.</summary>
    public async Task<(int Port, bool Ssl)> StartServiceAsync(string name)
    {
        var reply = await RequestAsync("StartService", new Dictionary<string, object> { ["Service"] = name });
        if (reply.TryGetValue("Error", out var error))
            throw new InvalidOperationException($"StartService {name} : {error}");
        int port = (int)(long)reply["Port"];
        bool ssl = reply.TryGetValue("EnableServiceSSL", out var s) && s is true;
        return (port, ssl);
    }
}

/// <summary>
/// The framing every lockdown-started service shares: 4-byte big-endian
/// length, then an XML plist — optionally inside TLS with the host certificate.
/// </summary>
internal static class PlistService
{
    public static async Task WriteAsync(Stream stream, Dictionary<string, object> dict)
    {
        byte[] payload = Plist.Write(dict);
        byte[] length = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)payload.Length);
        await stream.WriteAsync(length);
        await stream.WriteAsync(payload);
        await stream.FlushAsync();
    }

    public static async Task<Dictionary<string, object>> ReadAsync(Stream stream)
    {
        byte[] length = new byte[4];
        await stream.ReadExactlyAsync(length);
        uint size = BinaryPrimitives.ReadUInt32BigEndian(length);
        if (size == 0 || size > 16 * 1024 * 1024)
            throw new InvalidDataException($"longueur absurde : {size}");
        byte[] reply = new byte[size];
        await stream.ReadExactlyAsync(reply);
        return Plist.Read(reply) as Dictionary<string, object>
            ?? throw new InvalidDataException("reponse qui n'est pas un dict");
    }

    /// <summary>TLS on top of an existing pipe, with our host certificate as client identity.</summary>
    public static async Task<SslStream> WrapTlsAsync(Stream inner, X509Certificate2 hostCertificate)
    {
        var tls = new SslStream(inner, leaveInnerStreamOpen: false,
            userCertificateValidationCallback: static (_, _, _, _) => true);
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "lockdown",
            ClientCertificates = new X509CertificateCollection { hostCertificate },
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            // The phone asks for a client certificate; hand ours over without
            // waiting to be asked twice.
            LocalCertificateSelectionCallback = (_, _, _, _, _) => hostCertificate,
        });
        return tls;
    }
}
