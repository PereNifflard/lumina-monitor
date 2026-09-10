using LuminaMonitor.Core.Usb;

namespace LuminaMonitor.Core.Ddi;

/// <summary>
/// Conversation with <c>mobile_storage_proxy</c>, the daemon that mounts the
/// Developer Disk Image — reached as an ordinary lockdown service over USB,
/// no tunnel needed.
/// </summary>
/// <remarks>
/// Every request is a plist <c>{Command: …}</c> with the usual 4-byte
/// big-endian length prefix, inside the service's own TLS when lockdown asks
/// for it. On iOS 17+ the image is "Personalized": it can only be mounted with
/// a manifest Apple signed for this very device, which the phone may already
/// hold from an earlier Xcode session. Knowing that before doing anything is
/// what the status queries are for.
/// </remarks>
internal sealed partial class ImageMounter : IDisposable
{
    public const string ServiceName = "com.apple.mobile.mobile_image_mounter";
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(60);
    private readonly Stream _stream;

    public ImageMounter(Stream stream) => _stream = stream;

    /// <summary>Optional wire trace: one line per command and per reply, replies truncated.</summary>
    public Action<string>? Trace { get; set; }

    public async Task<Dictionary<string, object>> CommandAsync(string command, Dictionary<string, object>? extra = null)
    {
        var dict = new Dictionary<string, object> { ["Command"] = command };
        if (extra is not null)
            foreach (var (k, v) in extra) dict[k] = v;
        Trace?.Invoke($"-> {command}");
        await PlistService.WriteAsync(_stream, dict);
        var reply = await ReadReplyAsync(command);
        return reply;
    }

    /// <summary>A reply, or a TimeoutException naming the command instead of a silent hang.</summary>
    private async Task<Dictionary<string, object>> ReadReplyAsync(string command)
    {
        Dictionary<string, object> reply;
        try
        {
            reply = await PlistService.ReadAsync(_stream).WaitAsync(ReplyTimeout);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"Le telephone ne repond pas a {command} en {ReplyTimeout.TotalSeconds:N0} s.");
        }
        if (Trace is not null)
        {
            string dump = Plist.Dump(reply).Trim().Replace("\r", "").Replace("\n", " ");
            Trace($"<- {(dump.Length > 240 ? dump[..240] + "…" : dump)}");
        }
        return reply;
    }

    public void Dispose() => _stream.Dispose();

    public Task<Dictionary<string, object>> QueryDeveloperModeStatusAsync() =>
        CommandAsync("QueryDeveloperModeStatus");

    /// <summary>Images currently mounted (EntryList).</summary>
    public Task<Dictionary<string, object>> CopyDevicesAsync() =>
        CommandAsync("CopyDevices");

    /// <summary>Signatures of mounted images of one type — empty when nothing is mounted.</summary>
    public Task<Dictionary<string, object>> LookupImageAsync(string imageType) =>
        CommandAsync("LookupImage", new Dictionary<string, object> { ["ImageType"] = imageType });

    /// <summary>The identifiers Apple's signing server needs (BoardId, ChipID, …).</summary>
    public Task<Dictionary<string, object>> QueryPersonalizationIdentifiersAsync() =>
        CommandAsync("QueryPersonalizationIdentifiers",
            new Dictionary<string, object> { ["PersonalizedImageType"] = "DeveloperDiskImage" });

    /// <summary>The nonce a personalized manifest must be bound to.</summary>
    public Task<Dictionary<string, object>> QueryNonceAsync() =>
        CommandAsync("QueryNonce",
            new Dictionary<string, object> { ["PersonalizedImageType"] = "DeveloperDiskImage" });
}
