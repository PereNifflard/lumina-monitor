namespace LuminaMonitor.Core;

/// <summary>
/// Something the phone, the multiplexer or Apple's signing server refused.
/// </summary>
/// <remarks>
/// The message is written for the person in front of the screen, in the same
/// plain French the probe prints: it usually names the thing to do (unlock the
/// phone, trust the computer, turn Developer Mode on) rather than the layer
/// that complained.
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
}
