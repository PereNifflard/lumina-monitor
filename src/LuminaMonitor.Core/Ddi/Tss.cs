using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using LuminaMonitor.Core.Usb;

namespace LuminaMonitor.Core.Ddi;

/// <summary>
/// Asks Apple's signing server for the ticket that lets THIS phone mount the
/// Developer Disk Image.
/// </summary>
/// <remarks>
/// On iOS 17+ the image is only mountable with a manifest Apple signed for
/// one device and one nonce. The request is a plist listing the device's
/// identity (board, chip, ECID, nonce) and every trusted entry of the image's
/// build manifest, transformed by the manifest's own "restore request rules";
/// the answer is an IM4M ticket. Nothing here is secret or derived: it is an
/// HTTPS form post whose exact shape is what the server checks, so the shape
/// is reproduced key for key — including the client-version string the
/// server has come to expect.
/// </remarks>
internal static class Tss
{
    private const string Endpoint = "https://gs.apple.com/TSS/controller?action=2";
    private const string ClientVersion = "libauthinstall-1104.0.9";

    /// <summary>
    /// Apple's private root, pinned. The signing server does not chain to a
    /// public CA: gs.apple.com → "Apple Server Authentication CA" → "Apple Root
    /// CA", a root Windows does not ship. Rather than switch verification off
    /// (what the reference tools do), the exact root is embedded and every
    /// connection must chain to it. Source: the file linked from
    /// https://www.apple.com/certificateauthority/ (AppleIncRootCertificate.cer),
    /// downloaded 2026-09-08 over public-CA TLS; its SHA-1 thumbprint matched
    /// the root presented live by gs.apple.com. Valid until 2035-02-09.
    /// </summary>
    private const string AppleRootCaThumbprint = "611E5B662C593A08FF58D14AE22452D198DF6C60";
    private const string AppleRootCaBase64 = """
        MIIEuzCCA6OgAwIBAgIBAjANBgkqhkiG9w0BAQUFADBiMQswCQYDVQQGEwJVUzETMBEGA1UEChMK
        QXBwbGUgSW5jLjEmMCQGA1UECxMdQXBwbGUgQ2VydGlmaWNhdGlvbiBBdXRob3JpdHkxFjAUBgNV
        BAMTDUFwcGxlIFJvb3QgQ0EwHhcNMDYwNDI1MjE0MDM2WhcNMzUwMjA5MjE0MDM2WjBiMQswCQYD
        VQQGEwJVUzETMBEGA1UEChMKQXBwbGUgSW5jLjEmMCQGA1UECxMdQXBwbGUgQ2VydGlmaWNhdGlv
        biBBdXRob3JpdHkxFjAUBgNVBAMTDUFwcGxlIFJvb3QgQ0EwggEiMA0GCSqGSIb3DQEBAQUAA4IB
        DwAwggEKAoIBAQDkkakJH5HbHkdQ6wXtXnmELes2oldMVeyLGYne+Uts9QerIjAC6Bg++FAJ039B
        qJj50cpmnCRrEdCju+QbKsMflZ56DKRHi1vUFjczy8QPTc4UadHJGXL1XQ7Vf1+b8iUDulWPTV0N
        8WQ1IxVLFVkds5T39pyez1C6wVhQZ48ItCD3y6wsIG9wtj8BMIy3Q88PnT3zK0koGsj+zrW5Dtle
        HNbLPbU6rfQPDgCSC7EhFi501TwN22IWq6NxkkdTVcGvL0Gz+PvjcM3mo0xFfh9Ma1CWQYnEdGIL
        EINBhzOKgbEwWOxaBDKMaLOPHd5lc/9nXmW8Sdh2nzMUZaF3lMktAgMBAAGjggF6MIIBdjAOBgNV
        HQ8BAf8EBAMCAQYwDwYDVR0TAQH/BAUwAwEB/zAdBgNVHQ4EFgQUK9BpR5R2Cf70a40uQKb3R01/
        CF4wHwYDVR0jBBgwFoAUK9BpR5R2Cf70a40uQKb3R01/CF4wggERBgNVHSAEggEIMIIBBDCCAQAG
        CSqGSIb3Y2QFATCB8jAqBggrBgEFBQcCARYeaHR0cHM6Ly93d3cuYXBwbGUuY29tL2FwcGxlY2Ev
        MIHDBggrBgEFBQcCAjCBthqBs1JlbGlhbmNlIG9uIHRoaXMgY2VydGlmaWNhdGUgYnkgYW55IHBh
        cnR5IGFzc3VtZXMgYWNjZXB0YW5jZSBvZiB0aGUgdGhlbiBhcHBsaWNhYmxlIHN0YW5kYXJkIHRl
        cm1zIGFuZCBjb25kaXRpb25zIG9mIHVzZSwgY2VydGlmaWNhdGUgcG9saWN5IGFuZCBjZXJ0aWZp
        Y2F0aW9uIHByYWN0aWNlIHN0YXRlbWVudHMuMA0GCSqGSIb3DQEBBQUAA4IBAQBcNplMLXi37Yyb
        3PN3m/J20ncwT8EfhYOFG5k9RzfyqZtAjizUsZAS2L70c5vu0mQPy3lPNNiiPvl4/2vIB+x9OYOL
        UyDTOMSxv5pPCmv/K/xZpwUJfBdAVhEedNO3iyM7R6PVbyTi69G3cN8PReEnyvFteO3ntRcXqNx+
        IjXKJdXZD9Zr1KIkIxH3oayPc4FgxhtbCS+SsvhESPBgOJ4V9T0mZyCKM2r3DYLP3uujL/lTaltk
        wGMzd/c6ByxW69oPIQ7aunMZT7XZNn/Bh1XZp5m5MkL72NVxnn6hUrcbvZNCJBIqxw8dtk2cXmPI
        S4AXUKqK1drk/NAJBzewdXUh
        """;

    private static readonly Lazy<X509Certificate2> AppleRootCa = new(() =>
    {
        var root = new X509Certificate2(Convert.FromBase64String(AppleRootCaBase64));
        if (!string.Equals(root.Thumbprint, AppleRootCaThumbprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("La racine Apple embarquee ne correspond pas a son empreinte attendue.");
        return root;
    });

    /// <summary>
    /// Accepts the server only when its certificate chains to the pinned Apple
    /// root; a host-name mismatch or a missing certificate is never accepted.
    /// Revocation is not checked: Apple's private PKI publishes its lists on
    /// its own hosts, and a failed fetch would otherwise block signing.
    /// </summary>
    private static bool ValidateAppleChain(HttpRequestMessage request, X509Certificate2? certificate, X509Chain? presented, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None)
            return true;
        if (certificate is null || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != 0)
            return false;

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(AppleRootCa.Value);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        if (presented is not null)
            foreach (var element in presented.ChainElements.Skip(1))
                chain.ChainPolicy.ExtraStore.Add(element.Certificate);

        if (!chain.Build(certificate))
            return false;
        var root = chain.ChainElements[^1].Certificate;
        return root.RawData.AsSpan().SequenceEqual(AppleRootCa.Value.RawData);
    }

    /// <summary>Picks the build identity matching the phone's board and chip.</summary>
    public static Dictionary<string, object> SelectBuildIdentity(Dictionary<string, object> buildManifest, long boardId, long chipId)
    {
        var identities = buildManifest["BuildIdentities"] as List<object>
            ?? throw new InvalidDataException("BuildManifest sans BuildIdentities");
        foreach (var candidate in identities.OfType<Dictionary<string, object>>())
        {
            if (ParseNumber(candidate["ApBoardID"]) == boardId && ParseNumber(candidate["ApChipID"]) == chipId)
                return candidate;
        }
        throw new InvalidOperationException(CoreTexts.Current.NoBuildIdentity(boardId, chipId));
    }

    /// <summary>Builds the personalization request for the DeveloperDiskImage.</summary>
    public static Dictionary<string, object> BuildRequest(
        Dictionary<string, object> buildIdentity,
        Dictionary<string, object> personalizationIdentifiers,
        byte[] nonce)
    {
        long boardId = (long)personalizationIdentifiers["BoardId"];
        long chipId = (long)personalizationIdentifiers["ChipID"];
        long ecid = (long)personalizationIdentifiers["UniqueChipID"];

        var request = new Dictionary<string, object>
        {
            ["@HostPlatformInfo"] = "mac",
            ["@VersionInfo"] = ClientVersion,
            ["@UUID"] = Guid.NewGuid().ToString().ToUpperInvariant(),
        };

        foreach (var (key, value) in personalizationIdentifiers)
            if (key.StartsWith("Ap,", StringComparison.Ordinal))
                request[key] = value;

        request["@ApImg4Ticket"] = true;
        request["@BBTicket"] = true;
        request["ApBoardID"] = boardId;
        request["ApChipID"] = chipId;
        request["ApECID"] = ecid;
        request["ApNonce"] = nonce;
        request["ApProductionMode"] = true;
        request["ApSecurityDomain"] = 1L;
        request["ApSecurityMode"] = true;
        request["SepNonce"] = new byte[20];
        request["UID_MODE"] = false;

        var parameters = new Dictionary<string, object>
        {
            ["ApProductionMode"] = true,
            ["ApSecurityMode"] = true,
            ["ApSupportsImg4"] = true,
        };

        var manifest = buildIdentity["Manifest"] as Dictionary<string, object>
            ?? throw new InvalidDataException("BuildIdentity sans Manifest");
        List<object>? rules = null;
        if (manifest.TryGetValue("LoadableTrustCache", out var ltc) && ltc is Dictionary<string, object> ltcDict
            && ltcDict.TryGetValue("Info", out var ltcInfo) && ltcInfo is Dictionary<string, object> infoDict
            && infoDict.TryGetValue("RestoreRequestRules", out var r) && r is List<object> ruleList)
            rules = ruleList;

        foreach (var (key, value) in manifest)
        {
            if (value is not Dictionary<string, object> entry)
                continue;
            if (!entry.TryGetValue("Info", out _))
                continue;
            if (!(entry.TryGetValue("Trusted", out var trusted) && trusted is true))
                continue;

            var tssEntry = new Dictionary<string, object>(entry);
            tssEntry.Remove("Info");
            if (!tssEntry.ContainsKey("Digest"))
                tssEntry["Digest"] = Array.Empty<byte>();
            if (rules is not null)
                ApplyRestoreRequestRules(tssEntry, parameters, rules);
            request[key] = tssEntry;
        }

        return request;
    }

    /// <summary>
    /// The manifest's conditional overrides, reproduced with the reference
    /// implementation's quirk: a condition whose parameter is false never
    /// matches, whatever the rule asked for.
    /// </summary>
    private static void ApplyRestoreRequestRules(Dictionary<string, object> entry, Dictionary<string, object> parameters, List<object> rules)
    {
        foreach (var rule in rules.OfType<Dictionary<string, object>>())
        {
            bool fulfilled = true;
            var conditions = rule["Conditions"] as Dictionary<string, object> ?? [];
            foreach (var (key, value) in conditions)
            {
                if (!fulfilled) break;
                object? actual = key switch
                {
                    "ApRawProductionMode" or "ApCurrentProductionMode" => parameters.GetValueOrDefault("ApProductionMode"),
                    "ApRawSecurityMode" => parameters.GetValueOrDefault("ApSecurityMode"),
                    "ApRequiresImage4" => parameters.GetValueOrDefault("ApSupportsImg4"),
                    "ApDemotionPolicyOverride" => parameters.GetValueOrDefault("DemotionPolicy"),
                    "ApInRomDFU" => parameters.GetValueOrDefault("ApInRomDFU"),
                    _ => null,
                };
                fulfilled = actual is true && Equals(value, actual);
            }
            if (!fulfilled) continue;

            var actions = rule["Actions"] as Dictionary<string, object> ?? [];
            foreach (var (key, value) in actions)
            {
                if (value is long l && l == 255) continue;
                entry[key] = value;
            }
        }
    }

    /// <summary>Posts the request and returns the ApImg4Ticket.</summary>
    public static async Task<byte[]> RequestTicketAsync(Dictionary<string, object> request, Action<string>? log = null)
    {
        byte[] body = Plist.Write(request);
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = ValidateAppleChain };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        using var message = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new ByteArrayContent(body),
        };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("text/xml") { CharSet = "utf-8" };
        message.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        message.Headers.UserAgent.ParseAdd("InetURL/1.0");
        message.Headers.ExpectContinue = true;

        log?.Invoke($"TSS : requete de {body.Length} octets vers Apple…");
        using var response = await http.SendAsync(message);
        byte[] content = await response.Content.ReadAsByteArrayAsync();
        string text = Encoding.UTF8.GetString(content);

        string status = Between(text, "MESSAGE=", "&") ?? "(pas de MESSAGE)";
        if (status != "SUCCESS")
            throw new InvalidOperationException(CoreTexts.Current.AppleSigningServer(status, (int)response.StatusCode));

        int at = text.IndexOf("REQUEST_STRING=", StringComparison.Ordinal);
        if (at < 0)
            throw new InvalidDataException("reponse TSS sans REQUEST_STRING");
        var plist = Plist.Read(Encoding.UTF8.GetBytes(text[(at + "REQUEST_STRING=".Length)..])) as Dictionary<string, object>
            ?? throw new InvalidDataException("reponse TSS illisible");
        return plist["ApImg4Ticket"] as byte[]
            ?? throw new InvalidDataException("reponse TSS sans ApImg4Ticket");
    }

    private static string? Between(string text, string start, string end)
    {
        int a = text.IndexOf(start, StringComparison.Ordinal);
        if (a < 0) return null;
        a += start.Length;
        int b = text.IndexOf(end, a, StringComparison.Ordinal);
        return b < 0 ? text[a..] : text[a..b];
    }

    private static long ParseNumber(object value) => value switch
    {
        long l => l,
        string s when s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) => long.Parse(s[2..], NumberStyles.HexNumber),
        string s => long.Parse(s, CultureInfo.InvariantCulture),
        _ => throw new InvalidDataException($"nombre inattendu : {value}"),
    };
}
