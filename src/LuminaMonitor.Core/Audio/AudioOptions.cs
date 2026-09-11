namespace LuminaMonitor.Core.Audio;

/// <summary>
/// Everything a person can decide about the phone's sound.
/// </summary>
/// <remarks>
/// One value rather than five properties on the session, so that a change is a
/// single assignment the receive path cannot catch half done, and so that the
/// window can hand the same object to a live session and to the next one it
/// builds.
///
/// <para>The volume is ours and never Windows's. Turning the system volume down
/// for the phone would turn it down for everything else that endpoint carries,
/// and the endpoint here is very often a virtual cable feeding something else
/// again; so the number below is a gain applied to the samples on their way out,
/// and the mixer is left exactly as the person set it.</para>
/// </remarks>
public sealed record AudioOptions
{
    /// <summary>The delay the sound is held back by, when nobody has said otherwise.</summary>
    /// <remarks>
    /// Fifty milliseconds, and it is a starting point rather than a measurement.
    /// A picture takes about 96 ms to travel from the phone's screen to this one
    /// (measured, <c>clock-test</c>); the sound's own path is shorter — no
    /// decoder queue, no window to present into — so left alone it arrives
    /// early. Fifty is roughly the difference; the last word belongs to the ear,
    /// which is why this is a setting.
    /// </remarks>
    public const int DefaultDelayMs = 50;

    /// <summary>The widest delay the panel offers, in milliseconds.</summary>
    public const int MaxDelayMs = 300;

    /// <summary>Whether to open the audio stream at all. Off, the phone is never asked for sound.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// The Windows endpoint to play on, or null for whatever Windows calls the
    /// default output.
    /// </summary>
    /// <remarks>
    /// The <c>IMMDevice</c> identifier, which survives reboots and renames —
    /// that is what makes it worth storing rather than the name. A stored
    /// identifier that no longer exists is not an error: the output falls back to
    /// the default and says so in the journal.
    /// </remarks>
    public string? DeviceId { get; init; }

    /// <summary>Zero to a hundred, as the slider shows it.</summary>
    public int Volume { get; init; } = 100;

    /// <summary>Silence, without forgetting the volume.</summary>
    public bool Muted { get; init; }

    /// <summary>The jitter buffer's target fill, in milliseconds.</summary>
    public int DelayMs { get; init; } = DefaultDelayMs;

    /// <summary>Apple's own, and this project's: sound on, default output, full volume.</summary>
    public static AudioOptions Default { get; } = new();

    /// <summary>
    /// The factor the samples are multiplied by.
    /// </summary>
    /// <remarks>
    /// The square of the slider's position, not the position itself. Loudness is
    /// not linear in amplitude: a linear slider spends its top half on changes
    /// barely anyone hears and crushes everything quiet into the last
    /// centimetre. Squaring is the cheap approximation that puts half travel at
    /// about −12 dB, which reads as "half as loud".
    /// </remarks>
    public float Gain
    {
        get
        {
            if (Muted)
                return 0f;
            float position = Math.Clamp(Volume, 0, 100) / 100f;
            return position * position;
        }
    }

    /// <summary>The delay, clamped to what the buffer can actually hold.</summary>
    public int ClampedDelayMs => Math.Clamp(DelayMs, 0, MaxDelayMs);
}
