using System.Text;

namespace LuminaMonitor.Core.RemoteXpc;

/// <summary>
/// The phone's pasteboard, read and written over the cable.
/// </summary>
/// <remarks>
/// A different dialect from the HID and display services, and that is the whole
/// difficulty: those wrap every request in a CoreDevice envelope
/// (<c>featureIdentifier</c>, <c>messageType</c>, <c>payload</c>), while this
/// daemon dispatches on a bare <c>command</c> key of the XPC dictionary itself.
/// Eight verbs exist — PULL, PULL_REPLY, SET, SET_REPLY, DATA, PUSH,
/// AUTONOTIFY, RESOLVE; two of them cover copy and paste, and the other six are
/// promise resolution and change notification, which nothing here needs.
///
/// <para>A snapshot is <c>items: [{types: [UTI], data: {UTI: {data: Data}}}]</c>.
/// An item may also be <i>promised</i> — <c>{isPromised: true, isAvailable:
/// false, size: Int64}</c> instead of the bytes — which is why the pull declares
/// an inclusion policy of <c>allResolved</c>: the phone then puts the bytes in
/// the reply and there is no promise to chase.</para>
///
/// <para>Every value is a native XPC field, never base64: the text travels as a
/// DATA object holding UTF-8.</para>
/// </remarks>
internal static class PasteboardService
{
    public const string ServiceName = "com.apple.coredevice.pasteboardservice";

    /// <summary>The pasteboard iOS calls the clipboard; the only one with a name worth having.</summary>
    public const string GeneralPasteboard = "general";

    private const string PullCommand = "PULL";
    private const string PullReply = "PULL_REPLY";
    private const string SetCommand = "SET";
    private const string SetReply = "SET_REPLY";

    private const string Utf8PlainText = "public.utf8-plain-text";
    private const string PlainText = "public.plain-text";
    private const string Text = "public.text";

    /// <summary>The UTIs a text item is written under, and read back from, in order.</summary>
    private static readonly string[] TextTypes = [Utf8PlainText, PlainText, Text];

    /// <summary>Ask for the bytes inline rather than as promises we would have to chase.</summary>
    private static Dictionary<string, object?> AllResolved() => new()
    {
        ["allResolved"] = new Dictionary<string, object?>(),
    };

    /// <summary>
    /// Reads the pasteboard, and says what it holds when that is not text.
    /// </summary>
    /// <remarks>
    /// A photo on the phone's pasteboard is a perfectly ordinary thing to find,
    /// and returning nothing for it would be indistinguishable from an empty
    /// clipboard — so the first item's UTI and size come back instead, and the
    /// caller says so in its own words.
    /// </remarks>
    public static async Task<ClipboardContent> GetAsync(RemoteXpc service, string pasteboard = GeneralPasteboard)
    {
        var reply = await service.SendReceiveAsync(new Dictionary<string, object?>
        {
            ["command"] = PullCommand,
            ["pasteboardName"] = pasteboard,
            ["dataPolicy"] = AllResolved(),
        });
        if (reply.GetValueOrDefault("command") is string answer && answer != PullReply)
            throw new InvalidOperationException($"presse-papiers : reponse {answer} inattendue a un PULL");
        return Describe(Snapshot(reply));
    }

    /// <summary>The pasteboard's text, or null when it holds something else.</summary>
    public static async Task<string?> GetTextAsync(RemoteXpc service, string pasteboard = GeneralPasteboard) =>
        (await GetAsync(service, pasteboard)).Text;

    /// <summary>
    /// Replaces the pasteboard with one UTF-8 text item.
    /// </summary>
    /// <remarks>
    /// Written under all three text UTIs, as iOS itself does: an app pasting
    /// asks for whichever of them it knows about, and an item that only carries
    /// the most precise one is invisible to the ones that ask for the vaguest.
    /// </remarks>
    public static async Task SetTextAsync(RemoteXpc service, string text, string pasteboard = GeneralPasteboard)
    {
        var reply = await service.SendReceiveAsync(new Dictionary<string, object?>
        {
            ["command"] = SetCommand,
            ["pasteboardName"] = pasteboard,
            ["items"] = new List<object?> { TextItem(text) },
            ["sourceMetadata"] = null,
        });
        if (reply.GetValueOrDefault("command") is string answer && answer is not (SetReply or PullReply))
            throw new InvalidOperationException($"presse-papiers : reponse {answer} inattendue a un SET");
        if (reply.GetValueOrDefault("error") is { } refusal)
            throw new InvalidOperationException($"presse-papiers refuse : {Xpc.Dump(refusal).Trim()}");
    }

    /// <summary>One pasteboard item carrying <paramref name="text"/> under the text UTIs.</summary>
    private static Dictionary<string, object?> TextItem(string text)
    {
        byte[] payload = Encoding.UTF8.GetBytes(text);
        var data = new Dictionary<string, object?>();
        foreach (string uti in TextTypes)
            data[uti] = new Dictionary<string, object?> { ["data"] = payload };
        return new Dictionary<string, object?>
        {
            ["types"] = new List<object?>(TextTypes),
            ["data"] = data,
        };
    }

    /// <summary>The reply's snapshot: some daemons nest it under <c>pasteboard</c>, some do not.</summary>
    private static Dictionary<string, object?> Snapshot(Dictionary<string, object?> reply) =>
        reply.GetValueOrDefault("pasteboard") as Dictionary<string, object?> ?? reply;

    /// <summary>
    /// Walks the items in order and reports the first one that says something.
    /// </summary>
    /// <remarks>
    /// Text wins wherever it is found, because that is what a clipboard between
    /// two machines is for. Failing that, the first item with any bytes at all
    /// is named by its UTI and its size, so the caller can tell an image from a
    /// blob from an empty pasteboard.
    /// </remarks>
    private static ClipboardContent Describe(Dictionary<string, object?> snapshot)
    {
        if (snapshot.GetValueOrDefault("items") is not List<object?> items || items.Count == 0)
            return ClipboardContent.Nothing;

        (string Uti, int Bytes)? first = null;
        foreach (object? entry in items)
        {
            if (entry is not Dictionary<string, object?> item) continue;
            if (item.GetValueOrDefault("data") is not Dictionary<string, object?> data) continue;

            foreach (string uti in TextTypes)
                if (Bytes(data.GetValueOrDefault(uti)) is { } utf8 && Decode(utf8) is { } decoded)
                    return ClipboardContent.OfText(decoded);

            if (first is null)
                foreach (var (uti, value) in data)
                    if (Bytes(value) is { Length: > 0 } payload)
                    {
                        first = (uti, payload.Length);
                        break;
                    }

            // A promised item carries no bytes, only its announced size: worth
            // naming, because "an image of 2 Mo that did not travel" is a very
            // different answer from "nothing on the pasteboard".
            if (first is null && item.GetValueOrDefault("types") is List<object?> types && types.Count > 0
                && types[0] is string announced)
                first = (announced, Size(data.GetValueOrDefault(announced)));
        }

        if (first is not { } found)
            return ClipboardContent.Nothing;
        bool image = found.Uti.Contains("image", StringComparison.OrdinalIgnoreCase)
            || found.Uti is "public.png" or "public.jpeg" or "public.heic" or "public.tiff";
        return new ClipboardContent(image ? ClipboardKind.Image : ClipboardKind.Data,
            null, found.Uti, found.Bytes);
    }

    /// <summary>The bytes of one <c>PasteboardItemData</c>, whichever shape it arrived in.</summary>
    private static byte[]? Bytes(object? datum) => datum switch
    {
        byte[] raw => raw,
        Dictionary<string, object?> wrapper => wrapper.GetValueOrDefault("data") switch
        {
            byte[] raw => raw,
            string encoded => Encoding.UTF8.GetBytes(encoded),
            _ => null,
        },
        _ => null,
    };

    /// <summary>What a promised item says it weighs, or zero when it does not say.</summary>
    private static int Size(object? datum) =>
        datum is Dictionary<string, object?> wrapper
            ? wrapper.GetValueOrDefault("size") switch
            {
                XpcInt64 i => (int)Math.Clamp(i.Value, 0, int.MaxValue),
                XpcUInt64 u => (int)Math.Min(u.Value, int.MaxValue),
                _ => 0,
            }
            : 0;

    /// <summary>UTF-8 or nothing: a blob that is not text must not come back as mojibake.</summary>
    private static string? Decode(byte[] payload)
    {
        if (payload.Length == 0)
            return null;
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(payload);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }
}
