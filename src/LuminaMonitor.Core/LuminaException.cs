namespace LuminaMonitor.Core;

/// <summary>
/// Something the phone, the multiplexer or Apple's signing server refused.
/// </summary>
/// <remarks>
/// The message is written for the person in front of the screen, in the
/// interface's language (<see cref="InterfaceLanguage"/>; the sentences themselves
/// are in <see cref="CoreTexts"/>): it usually names the thing to do (unlock
/// the phone, trust the computer, turn Developer Mode on) rather than the
/// layer that complained.
/// </remarks>
public sealed class LuminaException : Exception
{
    public LuminaException(string message) : base(message) { }

    public LuminaException(string message, Exception inner) : base(message, inner) { }

    /// <summary>
    /// True when what refused was Apple's multiplexer, not the phone.
    /// </summary>
    /// <remarks>
    /// The only fault the window can offer a button for: the remedy is one
    /// piece of Apple software, always in the same place. Everything else is
    /// said in the message and left at that.
    /// </remarks>
    public bool AppleMultiplexer { get; init; }

    /// <summary>
    /// A refusal that ends on its own, soon: try again after this many seconds
    /// instead of backing off.
    /// </summary>
    /// <remarks>
    /// The window's retry doubles its wait up to half a minute, because a
    /// display service that refuses a stream renews its refusal each time it is
    /// asked. That rule is for a daemon that needs rest. A phone call is not
    /// one: iOS refuses to mirror a screen while a call is up, the refusal is
    /// immediate and cheap, and the person hangs up whenever they like — so the
    /// mirror should come back within seconds of it, not thirty.
    /// </remarks>
    public int? RetryAfterSeconds { get; init; }
}
