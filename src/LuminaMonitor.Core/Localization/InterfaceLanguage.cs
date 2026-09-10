using System.Globalization;

namespace LuminaMonitor.Core;

/// <summary>The two languages the interface speaks.</summary>
public enum DisplayLanguage
{
    English,
    French,
}

/// <summary>
/// Which language the words that reach the screen are written in — the
/// window's own and the ones Core hands up in its exceptions and events.
/// </summary>
/// <remarks>
/// One switch for the whole process, held here because Core is the lower of
/// the two assemblies: the window sets it from its settings at start-up and
/// again when the person picks another language, and both tables
/// (<c>CoreTexts</c> here, <c>Texts</c> in the window) read it at the moment a
/// message is composed. Nothing is cached, so a change applies to the next
/// message rather than at the next start.
///
/// <para>What it does <b>not</b> touch: the diagnostic journal and Core's own
/// log lines (<see cref="ILog"/>, the progress lines), which stay French
/// whatever the interface speaks — they are the maintainer's instrument, not
/// the person's screen.</para>
///
/// <para>Left alone, it follows the Windows display language: French when
/// Windows is in French, English for everybody else. The probe never sets it,
/// and gets exactly that.</para>
/// </remarks>
public static class InterfaceLanguage
{
    private static volatile int _current = (int)FromWindows();

    /// <summary>The language messages are composed in, from now on.</summary>
    public static DisplayLanguage Current
    {
        get => (DisplayLanguage)_current;
        set => _current = (int)value;
    }

    /// <summary>French if the Windows display language is French, English otherwise.</summary>
    public static DisplayLanguage FromWindows() =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fr"
            ? DisplayLanguage.French
            : DisplayLanguage.English;

    /// <summary>
    /// The language a <c>language</c> setting asks for: <c>en</c>, <c>fr</c>,
    /// or anything else — <c>auto</c> included — for the Windows one.
    /// </summary>
    /// <remarks>
    /// Read by its first two letters, so <c>english</c>, <c>en-GB</c>,
    /// <c>français</c> and <c>fr-FR</c> all mean what they look like.
    /// </remarks>
    public static DisplayLanguage Resolve(string? setting)
    {
        string wanted = (setting ?? "").Trim().ToLowerInvariant();
        if (wanted.StartsWith("en", StringComparison.Ordinal)) return DisplayLanguage.English;
        if (wanted.StartsWith("fr", StringComparison.Ordinal)) return DisplayLanguage.French;
        return FromWindows();
    }
}
