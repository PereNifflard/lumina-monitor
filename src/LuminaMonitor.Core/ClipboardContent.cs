namespace LuminaMonitor.Core;

/// <summary>What the phone's pasteboard turned out to be holding.</summary>
public enum ClipboardKind
{
    /// <summary>Nothing on it, or nothing it would hand over.</summary>
    Nothing,

    /// <summary>UTF-8 text, in <see cref="ClipboardContent.Text"/>.</summary>
    Text,

    /// <summary>A picture: named and weighed, not transferred.</summary>
    Image,

    /// <summary>Anything else with bytes in it: named and weighed, not transferred.</summary>
    Data,
}

/// <summary>
/// One reading of the phone's pasteboard.
/// </summary>
/// <remarks>
/// Deliberately more than a string. Text is what a clipboard shared between two
/// machines is for, but a phone's pasteboard very often holds a photo, and
/// answering that with an empty string would say "there is nothing there" about
/// something that is very much there. So the kind is carried, with the UTI and
/// the size for the two cases that are not text, and the words are left to
/// whoever shows them — the window says them with accents, the probe without.
/// </remarks>
/// <param name="Kind">Text, a picture, other bytes, or nothing.</param>
/// <param name="Text">The text, and only when <see cref="Kind"/> says so.</param>
/// <param name="Type">The uniform type identifier the phone named it with.</param>
/// <param name="Bytes">How many bytes it carries, as read or as announced.</param>
public readonly record struct ClipboardContent(ClipboardKind Kind, string? Text, string? Type, int Bytes)
{
    /// <summary>An empty pasteboard.</summary>
    public static readonly ClipboardContent Nothing = new(ClipboardKind.Nothing, null, null, 0);

    /// <summary>A text reading, weighed as it travels: UTF-8 bytes, not characters.</summary>
    public static ClipboardContent OfText(string text) =>
        new(ClipboardKind.Text, text, "public.utf8-plain-text",
            System.Text.Encoding.UTF8.GetByteCount(text));
}
