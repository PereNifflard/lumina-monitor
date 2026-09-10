using System.Security.Cryptography;
using LuminaMonitor.Core.Usb;

namespace LuminaMonitor.Core.Ddi;

/// <summary>How <see cref="DdiManager.EnsureMountedAsync"/> ended.</summary>
internal enum DdiStatus
{
    /// <summary>The phone already carries this very image: nothing was sent.</summary>
    AlreadyMounted,
    /// <summary>The image was personalized, uploaded and mounted.</summary>
    Mounted,
    /// <summary>No BuildManifest.plist in the folder.</summary>
    ManifestMissing,
    /// <summary>BuildManifest.plist is a binary plist, which we do not read yet.</summary>
    ManifestBinary,
    /// <summary>The manifest names an image or a trust cache the folder does not hold.</summary>
    FilesMissing,
    /// <summary>The phone refused to drop the image it already had up.</summary>
    UnmountRefused,
    /// <summary>The phone refused MountImage.</summary>
    MountRefused,
    /// <summary>Still locked after ten minutes of waiting.</summary>
    StillLocked,
}

/// <summary>
/// Puts the Developer Disk Image up on the phone, and knows when it is
/// already up.
/// </summary>
/// <remarks>
/// The sequence is the one the phone is known to accept, and it is kept to
/// the byte: the mounter daemon serves one client at a time, so the
/// connection that asked for a cached manifest must be gone before the next
/// one is opened, and nonce, signing, upload and mount all happen on that
/// single second connection.
///
/// <para>The one thing done before anything else is a <c>CopyDevices</c>: if
/// a Personalized image is mounted whose signature is the SHA-384 of the
/// image we were about to send, the phone already has exactly this image and
/// the whole ceremony is skipped.</para>
///
/// <para>Uploading and unmounting are both refused while the screen is
/// locked, and the daemon drops the connection when it refuses: each retry
/// opens a fresh one, every 3 s, for at most ten minutes.</para>
/// </remarks>
internal sealed class DdiManager
{
    private const int LockdownPort = 62078;
    private const int UnlockRetryMs = 3000;
    private const int UnlockAttempts = 200;      // 200 x 3 s = 10 min

    private readonly long _deviceId;
    private readonly PairRecord _record;

    public DdiManager(long deviceId, PairRecord record)
    {
        _deviceId = deviceId;
        _record = record;
    }

    /// <summary>The phone is locked and the person has to unlock it; raised once per wait.</summary>
    public event Action<string>? UnlockRequired;

    /// <summary>The mount path of the image found or put up, when there is one.</summary>
    public string? MountPath { get; private set; }

    public async Task<DdiStatus> EnsureMountedAsync(DdiSource source, IProgress<string>? progress, CancellationToken cancellation = default)
    {
        void Say(string line) => progress?.Report(line);

        string dir = source.Folder;
        string manifestPath = Path.Combine(dir, "BuildManifest.plist");
        if (!File.Exists(manifestPath)) { Say($"Fichier manquant : {manifestPath}"); return DdiStatus.ManifestMissing; }

        byte[] manifestBytes = await File.ReadAllBytesAsync(manifestPath, cancellation);
        if (manifestBytes.Length > 8 && manifestBytes.AsSpan(0, 8).SequenceEqual("bplist00"u8))
        { Say("BuildManifest.plist est en plist BINAIRE — lecteur bplist a ecrire."); return DdiStatus.ManifestBinary; }
        var buildManifest = Plist.Read(manifestBytes) as Dictionary<string, object>
            ?? throw new LuminaException("BuildManifest illisible");

        var mounter = await OpenMounterAsync(Say);

        // Before anything is sent: what the phone already has up. A Personalized
        // entry whose signature is our image's SHA-384 means this exact image is
        // mounted, and there is nothing to do at all.
        var mounted = await mounter.MountedImagesAsync();

        var ids = (await mounter.QueryPersonalizationIdentifiersAsync())["PersonalizationIdentifiers"] as Dictionary<string, object>
            ?? throw new LuminaException("pas d'identifiants de personnalisation");
        var identity = Tss.SelectBuildIdentity(buildManifest, (long)ids["BoardId"], (long)ids["ChipID"]);

        string? imageEntry = ManifestPath(identity, "PersonalizedDMG");
        string? trustEntry = ManifestPath(identity, "LoadableTrustCache");
        string deviceClass = identity.GetValueOrDefault("Info") is Dictionary<string, object> identityInfo
            ? identityInfo.GetValueOrDefault("DeviceClass") as string ?? "?"
            : "?";
        Say($"BuildIdentity {deviceClass} (ApBoardID {identity.GetValueOrDefault("ApBoardID")} / ApChipID {identity.GetValueOrDefault("ApChipID")}) :"
            + $" image {imageEntry ?? "(absente du manifeste)"}, trustcache {trustEntry ?? "(absent du manifeste)"}");

        string? imagePath = ResolveFile(dir, imageEntry, "Image.dmg");
        string? trustPath = ResolveFile(dir, trustEntry, "Image.dmg.trustcache");
        if (imagePath is null || trustPath is null)
        {
            Say($"Introuvable dans {Path.GetFullPath(dir)} : "
                + $"{(imagePath is null ? imageEntry ?? "PersonalizedDMG" : trustEntry ?? "LoadableTrustCache")}"
                + " — relancer extract-devsupport sur ce dossier.");
            mounter.Dispose();
            return DdiStatus.FilesMissing;
        }

        byte[] image = await File.ReadAllBytesAsync(imagePath, cancellation);
        byte[] trustCache = await File.ReadAllBytesAsync(trustPath, cancellation);
        byte[] sha = SHA384.HashData(image);
        Say($"Image {image.Length} octets, SHA-384 {Convert.ToHexString(sha)[..16]}…, manifeste {buildManifest.GetValueOrDefault("ProductBuildVersion")}.");

        foreach (var (type, path, signature) in mounted)
        {
            if (!type.Contains("Personalized", StringComparison.OrdinalIgnoreCase)) continue;
            if (signature is null || signature.Length != sha.Length) continue;
            if (!CryptographicOperations.FixedTimeEquals(signature, sha)) continue;
            MountPath = path;
            Say($"Image deja montee : {type} sur {path ?? "?"}, signature identique — rien a envoyer.");
            mounter.Dispose();
            return DdiStatus.AlreadyMounted;
        }

        byte[]? ticket = null;
        try
        {
            ticket = await mounter.QueryPersonalizationManifestAsync(sha);
            Say(ticket is null ? "Aucun manifeste signe en cache sur le telephone." : $"Manifeste signe trouve en cache ({ticket.Length} octets).");
        }
        catch (Exception exception)
        {
            // The phone may close the connection on a cache miss; expected.
            Say($"Pas de manifeste en cache ({exception.GetType().Name}).");
        }

        // The mounter daemon serves one client at a time: the connection that
        // asked for the manifest must be gone before the next one is opened,
        // and nonce, signing, upload and mount all happen on that single
        // second connection — the sequence the phone is known to accept.
        mounter.Dispose();
        mounter = await OpenMounterAsync(Say);

        // One developer image at a time: the phone refuses a second mount
        // while an earlier build is still up, so drop that one first. Like
        // the upload, unmounting is refused (and the connection dropped)
        // while the screen is locked: wait for the person, reconnecting.
        for (int attempt = 0; ; attempt++)
        {
            bool locked = false;
            foreach (var (type, path, signature) in await mounter.MountedImagesAsync())
            {
                if (path is null) continue;
                if (attempt == 0) Say($"Image deja montee : {type} sur {path}{(signature is null ? "" : $" (signature {signature.Length} octets)")}");
                string? unmountError = await mounter.UnmountAsync(path);
                if (unmountError is null) { Say($"  demontee ({path})."); continue; }
                if (unmountError.Contains("DeviceLocked", StringComparison.Ordinal)) { locked = true; break; }
                Say($"  demontage refuse : {unmountError}");
                mounter.Dispose();
                return DdiStatus.UnmountRefused;
            }
            if (!locked) break;
            if (attempt == 0)
            {
                Say("iPhone VERROUILLE : deverrouille-le, le demontage reprend tout seul (10 min max).");
                UnlockRequired?.Invoke("iPhone verrouille : le demontage de l'image developpeur attend le deverrouillage.");
            }
            else if (attempt % 20 == 0) Say($"  toujours verrouille ({attempt * 3} s)…");
            if (attempt >= UnlockAttempts) { Say("Toujours verrouille apres 10 min — relancer mount une fois l'iPhone deverrouille."); mounter.Dispose(); return DdiStatus.StillLocked; }
            mounter.Dispose();
            await Task.Delay(UnlockRetryMs, cancellation);
            mounter = await OpenMounterAsync(Say, quiet: true);
        }
        mounter.Trace = line => Say("    " + line);

        if (ticket is null)
        {
            var nonce = (await mounter.QueryNonceAsync())["PersonalizationNonce"] as byte[]
                ?? throw new LuminaException("pas de nonce");
            var request = Tss.BuildRequest(identity, ids, nonce);
            ticket = await Tss.RequestTicketAsync(request, Say);
            Say($"*** TICKET APPLE RECU ({ticket.Length} octets) ***");
        }

        Say("Envoi de l'image…");
        // The phone refuses the upload while its screen is locked; give the
        // person ten minutes to unlock it rather than failing outright.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await mounter.UploadAsync(new MemoryStream(image), image.Length, ticket,
                    sent => Say($"  {sent * 100 / image.Length}% ({sent:N0} octets)"));
                break;
            }
            catch (InvalidOperationException exception) when (exception.Message.Contains("DeviceLocked", StringComparison.Ordinal))
            {
                // The daemon answers DeviceLocked and drops the connection:
                // each retry needs a fresh one (the ticket stays valid).
                if (attempt == 0)
                {
                    Say("iPhone VERROUILLE : deverrouille-le, le transfert reprend tout seul (10 min max).");
                    UnlockRequired?.Invoke("iPhone verrouille : l'envoi de l'image developpeur attend le deverrouillage.");
                }
                else if (attempt % 20 == 0) Say($"  toujours verrouille ({attempt * 3} s)…");
                if (attempt >= UnlockAttempts) { Say("Toujours verrouille apres 10 min — relancer mount une fois l'iPhone deverrouille."); mounter.Dispose(); return DdiStatus.StillLocked; }
                mounter.Dispose();
                await Task.Delay(UnlockRetryMs, cancellation);
                mounter = await OpenMounterAsync(Say, quiet: true);
            }
        }
        Say("Image transferee. Montage…");
        string? error = await mounter.MountAsync(ticket, trustCache);
        mounter.Dispose();
        if (error is not null) { Say($"Montage refuse : {error}"); return DdiStatus.MountRefused; }
        return DdiStatus.Mounted;
    }

    /// <summary>
    /// Takes down every mounted developer image. Unmounting stops the image's
    /// daemons (display, HID…), so a remount is the soft reset for a display
    /// service that stopped answering. Refused while the phone is locked, and
    /// the refusal is handed back whole: only the caller knows whether a locked
    /// screen is worth waiting for.
    /// </summary>
    public async Task<(int Unmounted, string? Refusal)> UnmountAllAsync(IProgress<string>? progress)
    {
        void Say(string line) => progress?.Report(line);
        using var mounter = await OpenMounterAsync(Say);
        int count = 0;
        foreach (var (type, path, signature) in await mounter.MountedImagesAsync())
        {
            if (path is null) continue;
            string? error = await mounter.UnmountAsync(path);
            if (error is not null) { Say($"Demontage refuse ({type} sur {path}) : {error}"); return (count, error); }
            Say($"Image {type} demontee ({path}).");
            count++;
        }
        return (count, null);
    }

    /// <summary>A fresh lockdown session and a fresh mounter service on it.</summary>
    private async Task<ImageMounter> OpenMounterAsync(Action<string> say, bool quiet = false)
    {
        var pipe = await UsbmuxClient.ConnectAsync();
        var lockdown = new LockdownClient(await pipe.ConnectToDeviceAsync(_deviceId, LockdownPort));
        string? refusal = await lockdown.StartSessionAsync(_record);
        if (refusal is not null) throw new LuminaException($"StartSession refuse : {refusal}");
        var (port, ssl) = await lockdown.StartServiceAsync(ImageMounter.ServiceName);
        var servicePipe = await UsbmuxClient.ConnectAsync();
        Stream service = await servicePipe.ConnectToDeviceAsync(_deviceId, port);
        if (ssl) service = await PlistService.WrapTlsAsync(service, _record.HostCertificate);
        return new ImageMounter(service) { Trace = quiet ? null : line => say("    " + line) };
    }

    /// <summary>The path a BuildIdentity's manifest gives for one entry, if any.</summary>
    private static string? ManifestPath(Dictionary<string, object> identity, string entry)
    {
        if (identity.GetValueOrDefault("Manifest") is not Dictionary<string, object> entries) return null;
        if (entries.GetValueOrDefault(entry) is not Dictionary<string, object> item) return null;
        if (item.GetValueOrDefault("Info") is not Dictionary<string, object> info) return null;
        return info.GetValueOrDefault("Path") as string;
    }

    /// <summary>The named file inside the copy of Restore/, or the legacy flat name.</summary>
    private static string? ResolveFile(string root, string? entryPath, string legacyName)
    {
        if (entryPath is not null)
        {
            string named = Path.Combine(root, entryPath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(named)) return named;
        }
        string legacy = Path.Combine(root, legacyName);
        return File.Exists(legacy) ? legacy : null;
    }
}
