using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace LuminaMonitor.Core.Usb;

/// <summary>
/// The pairing record the multiplexer keeps for a device once the phone has
/// answered "Se fier a cet ordinateur".
/// </summary>
/// <remarks>
/// Apple's own app creates it, the multiplexer stores it, and lockdown will
/// accept any client that presents the same host identity and certificate.
/// That is what lets this project skip the pairing handshake entirely: the
/// record is read back over the multiplexer (<c>ReadPairRecord</c>) and its
/// PEM material becomes the TLS client certificate of our session.
///
/// <para>The certificate is rebuilt from PEM and then round-tripped through a
/// PFX export. Not decoration: on Windows, a certificate whose private key was
/// attached in memory from PEM is not usable by SChannel for client
/// authentication until it has been re-imported, and the failure mode is a
/// TLS handshake that dies without naming the cause.</para>
/// </remarks>
internal sealed class PairRecord
{
    public required string HostId { get; init; }
    public required string SystemBuid { get; init; }
    public required X509Certificate2 HostCertificate { get; init; }
    public byte[]? EscrowBag { get; init; }

    public static PairRecord Parse(Dictionary<string, object> record)
    {
        string hostId = record["HostID"].ToString()!;
        string buid = record["SystemBUID"].ToString()!;
        string certPem = Encoding.ASCII.GetString((byte[])record["HostCertificate"]);
        string keyPem = Encoding.ASCII.GetString((byte[])record["HostPrivateKey"]);

        // UserKeySet rather than EphemeralKeySet: SChannel refuses to
        // authenticate a client with a key that lives only in memory
        // (SEC_E_UNKNOWN_CREDENTIALS, 0x8009030D). A user-profile key
        // container is created for the lifetime of the certificate object
        // and removed with it — nothing persists after the process exits.
        using var fromPem = X509Certificate2.CreateFromPem(certPem, keyPem);
        var usable = new X509Certificate2(
            fromPem.Export(X509ContentType.Pfx), (string?)null,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);

        return new PairRecord
        {
            HostId = hostId,
            SystemBuid = buid,
            HostCertificate = usable,
            EscrowBag = record.TryGetValue("EscrowBag", out var bag) ? bag as byte[] : null,
        };
    }
}
