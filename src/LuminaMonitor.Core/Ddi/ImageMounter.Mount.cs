using LuminaMonitor.Core.Usb;

namespace LuminaMonitor.Core.Ddi;

/// <summary>
/// The upload-and-mount half of the image mounter: pushes the Developer Disk
/// Image to the phone with its Apple-signed manifest, then mounts it.
/// </summary>
/// <remarks>
/// Written before the image is in hand, because the sequence does not depend
/// on where the image comes from: <c>ReceiveBytes</c> announces the size and
/// the manifest, the phone answers <c>ReceiveBytesAck</c>, the raw image bytes
/// follow on the same pipe with no framing, the phone answers
/// <c>Complete</c>; then <c>MountImage</c> with the same manifest and the
/// trust cache brings <c>/System/Developer</c> — and with it the HID daemon —
/// to life until the next reboot.
///
/// <para>The manifest (an IM4M ticket) is what Apple's signing server returns
/// for this exact device and this exact nonce; it is opaque here, passed
/// through as bytes. Mounting without it is refused outright on iOS 17+.</para>
/// </remarks>
internal sealed partial class ImageMounter
{
    /// <summary>Whether the phone already holds a signed manifest for this image (offline path).</summary>
    public async Task<byte[]?> QueryPersonalizationManifestAsync(byte[] imageSha384)
    {
        var reply = await CommandAsync("QueryPersonalizationManifest", new Dictionary<string, object>
        {
            ["PersonalizedImageType"] = "DeveloperDiskImage",
            ["ImageType"] = "DeveloperDiskImage",
            ["ImageSignature"] = imageSha384,
        });
        return reply.TryGetValue("ImageSignature", out var m) && m is byte[] manifest ? manifest : null;
    }

    /// <summary>Uploads the image; the manifest travels in the announcement.</summary>
    public async Task UploadAsync(Stream image, long size, byte[] manifest, Action<long>? progress = null)
    {
        var ack = await CommandAsync("ReceiveBytes", new Dictionary<string, object>
        {
            ["ImageType"] = "Personalized",
            ["ImageSize"] = size,
            ["ImageSignature"] = manifest,
        });
        if (ack.GetValueOrDefault("Status")?.ToString() != "ReceiveBytesAck")
            throw new InvalidOperationException($"ReceiveBytes refuse : {Plist.Dump(ack).Trim()}");

        byte[] buffer = new byte[1 << 20];
        long sent = 0;
        int read;
        while ((read = await image.ReadAsync(buffer)) > 0)
        {
            // A write that stalls means the phone stopped draining: fail loudly
            // with the byte count instead of hanging on a full socket buffer.
            try
            {
                await _stream.WriteAsync(buffer.AsMemory(0, read)).AsTask().WaitAsync(TimeSpan.FromSeconds(30));
            }
            catch (TimeoutException)
            {
                throw new TimeoutException(CoreTexts.Current.ImageUploadStalled(sent, size));
            }
            sent += read;
            progress?.Invoke(sent);
        }
        await _stream.FlushAsync();

        var done = await ReadReplyAsync("ReceiveBytes (fin du transfert)");
        if (done.GetValueOrDefault("Status")?.ToString() != "Complete")
            throw new InvalidOperationException($"Transfert de l'image : {Plist.Dump(done).Trim()}");
    }

    /// <summary>
    /// Images the phone currently has mounted: (ImageType, MountPath, signature)
    /// per entry of <c>CopyDevices</c>. Empty when nothing is mounted.
    /// </summary>
    public async Task<List<(string ImageType, string? MountPath, byte[]? Signature)>> MountedImagesAsync()
    {
        var reply = await CopyDevicesAsync();
        var result = new List<(string, string?, byte[]?)>();
        if (reply.GetValueOrDefault("EntryList") is not List<object> entries)
            return result;
        foreach (var entry in entries.OfType<Dictionary<string, object>>())
        {
            string type = entry.GetValueOrDefault("ImageType")?.ToString()
                ?? entry.GetValueOrDefault("DiskImageType")?.ToString() ?? "?";
            string? path = entry.GetValueOrDefault("MountPath")?.ToString();
            byte[]? signature = entry.GetValueOrDefault("ImageSignature") as byte[];
            result.Add((type, path, signature));
        }
        return result;
    }

    /// <summary>
    /// Unmounts the image at <paramref name="mountPath"/> (personalized images
    /// live under /System/Developer). Null on success, else the phone's error.
    /// A different image can only be mounted once the previous one is gone.
    /// </summary>
    public async Task<string?> UnmountAsync(string mountPath)
    {
        var reply = await CommandAsync("UnmountImage", new Dictionary<string, object> { ["MountPath"] = mountPath });
        if (reply.GetValueOrDefault("Status")?.ToString() == "Complete")
            return null;
        return reply.GetValueOrDefault("DetailedError")?.ToString()
            ?? reply.GetValueOrDefault("Error")?.ToString()
            ?? Plist.Dump(reply).Trim();
    }

    /// <summary>Mounts the uploaded image. Null on success, else the phone's error text.</summary>
    public async Task<string?> MountAsync(byte[] manifest, byte[] trustCache)
    {
        var reply = await CommandAsync("MountImage", new Dictionary<string, object>
        {
            ["ImageType"] = "Personalized",
            ["ImageSignature"] = manifest,
            ["ImageTrustCache"] = trustCache,
        });
        if (reply.GetValueOrDefault("Status")?.ToString() == "Complete")
            return null;
        return reply.GetValueOrDefault("DetailedError")?.ToString()
            ?? reply.GetValueOrDefault("Error")?.ToString()
            ?? Plist.Dump(reply).Trim();
    }
}
