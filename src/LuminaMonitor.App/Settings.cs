using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LuminaMonitor.App;

/// <summary>
/// The few things the window has to remember between runs.
/// </summary>
/// <remarks>
/// Stored under <c>%APPDATA%\LuminaMonitor</c> rather than beside the
/// executable. Nothing secret is <i>needed</i> here — the phone is reached over
/// the cable with Apple's own pairing record, so there is no token to lose —
/// and the one field that can hold a secret, <see cref="UnlockCode"/>, is empty
/// unless somebody puts their passcode in it on purpose. What remains is where
/// the developer image was unpacked, which way up the window was left, and a
/// few cosmetic ones.
///
/// <para>An older settings file can still carry <c>phoneAudio</c>,
/// <c>phoneAudioDevice</c>, <c>callMicrophone</c> or <c>callOutput</c> — the
/// Bluetooth audio switches, dropped September 10 2026 (see docs/AUDIO.md).
/// <c>JsonSerializer.Deserialize</c> ignores unknown properties by default,
/// so such a file still loads without error; those keys are simply never
/// written back. The sound that came back over the cable has its own four keys
/// below, and none of them is one of those.</para>
/// </remarks>
public sealed class Settings
{
    /// <summary>
    /// The folder holding a faithful copy of the developer image's
    /// <c>Restore/</c> tree, which is what <c>DeviceSession</c> mounts.
    /// </summary>
    /// <remarks>
    /// Empty on a first run: the window then asks for an Xcode <c>.xip</c> or a
    /// Device Support <c>.dmg</c> and unpacks it into
    /// <c>%APPDATA%\LuminaMonitor\ddi\&lt;build&gt;</c>, which takes about two
    /// minutes and happens once per iOS build.
    /// </remarks>
    [JsonPropertyName("ddiFolder")]
    public string DdiFolder { get; set; } = "";

    /// <summary>The archive the folder above was extracted from, for the record.</summary>
    /// <remarks>
    /// Kept so the dialog can open where the last one was found, and so the
    /// question "which Xcode did this image come from" has an answer that does
    /// not depend on anyone's memory.
    /// </remarks>
    [JsonPropertyName("lastArchive")]
    public string LastArchive { get; set; } = "";

    /// <summary>
    /// Turn the mouse wheel around, for anyone who wants it the other way.
    /// </summary>
    /// <remarks>
    /// Off, and off is the ordinary PC behaviour: a notch down scrolls the
    /// content down, which the injector spells as a finger drag upwards on the
    /// glass.
    ///
    /// <para>This replaces <c>phoneNaturalScrolling</c>, which was a description
    /// of an iOS trackpad setting and got the sign backwards — the phone is not
    /// being driven by a trackpad here, it is being driven by a finger. The old
    /// key is simply ignored when an existing settings file still carries it, so
    /// a file saying <c>"phoneNaturalScrolling": true</c> gets the corrected
    /// behaviour without anyone having to edit it.</para>
    /// </remarks>
    [JsonPropertyName("invertWheel")]
    public bool InvertWheel { get; set; } = false;

    /// <summary>
    /// Pull the phone's brightness down when the mirror starts.
    /// </summary>
    /// <remarks>
    /// <b>Off by default, and deliberately.</b> The phone's screen is lighting a
    /// room nobody is looking at while its picture is on the PC, so dimming it is
    /// worth having — but there is no way to ask iOS for it, so what is left is
    /// driving Control Centre with a finger, which depends on where iOS chooses
    /// to put its sliders. Turn it on once with the phone in view, check it lands
    /// on the brightness slider and not on aeroplane mode, and adjust
    /// <see cref="DimGesture"/> if it does not.
    /// </remarks>
    [JsonPropertyName("dimOnConnect")]
    public bool DimOnConnect { get; set; } = false;

    /// <summary>
    /// Where the brightness slider is, as fractions of the screen:
    /// <c>[openX, openY, sliderX, fromY, toY]</c>.
    /// </summary>
    /// <remarks>
    /// Exposed rather than hard-coded precisely because it is the part most
    /// likely to be wrong. The first two open Control Centre — a drag downwards
    /// from the top right corner. The last three drag the slider: its column,
    /// and how far down to pull it.
    /// </remarks>
    [JsonPropertyName("dimGesture")]
    public double[] DimGesture { get; set; } = [0.93, 0.005, 0.28, 0.30, 0.46];

    /// <summary>
    /// Which colour of iPhone 17 Pro Max to draw around the picture.
    /// </summary>
    /// <remarks>
    /// <c>orange</c> (Cosmic Orange), <c>blue</c> (Deep Blue) or <c>silver</c>.
    /// Purely cosmetic, but seeing your own phone rather than a generic model is
    /// most of the point of drawing the chassis at all.
    /// </remarks>
    [JsonPropertyName("chassisColour")]
    public string ChassisColour { get; set; } = "orange";

    /// <summary>
    /// The phone's passcode, for unlocking it from the window. Empty, and empty
    /// is the right answer for almost everybody.
    /// </summary>
    /// <remarks>
    /// <b>Written in clear in this file.</b> There is no way around that: the
    /// keypad on the lock screen wants the digits themselves, and a passcode
    /// this app cannot read is a passcode it cannot type. So the trade is stated
    /// rather than hidden — anybody who can read
    /// <c>%APPDATA%\LuminaMonitor\settings.json</c> can read the phone's
    /// passcode, which on a machine where the phone is already trusted and
    /// plugged in is a smaller step than it sounds, and still a step. Leave it
    /// empty and use Face ID; fill it only if unlocking without touching the
    /// phone is worth that to you.
    ///
    /// <para>It never reaches the journal. The window types it on the virtual
    /// keyboard and says how many digits went out, never which ones, and no
    /// status line, counter or log line in this project carries the value.</para>
    /// </remarks>
    [JsonPropertyName("unlockCode")]
    public string UnlockCode { get; set; } = "";

    /// <summary>
    /// The interface's language: <c>auto</c>, <c>en</c> or <c>fr</c>.
    /// </summary>
    /// <remarks>
    /// <c>auto</c> — the default, and what a missing or unknown value means —
    /// is French when Windows displays in French and English everywhere else.
    /// Read at start-up, and written by the window's system menu (right-click
    /// the title bar), which also applies the change on the spot. The journal
    /// stays French either way.
    /// </remarks>
    [JsonPropertyName("language")]
    public string Language { get; set; } = "auto";

    /// <summary>
    /// Whether the window has already shown that the drawn side buttons can be
    /// clicked.
    /// </summary>
    /// <remarks>
    /// False on a first run — and on any older settings file, which has no
    /// such key — so the four buttons pulse once and a bubble says what they
    /// are. Set as soon as that has been shown. The window's system menu shows
    /// it again whenever asked; a <c>--diagnostic</c> run never spends it.
    /// </remarks>
    [JsonPropertyName("chassisHintShown")]
    public bool ChassisHintShown { get; set; } = false;

    /// <summary>Where the window was when it was last closed. Zero means centre it.</summary>
    [JsonPropertyName("windowBounds")]
    public double[] WindowBounds { get; set; } = [];

    // --- The phone's sound ---------------------------------------------------------
    // Over the cable, decoded here: docs/AUDIO.md, "In the application". On by
    // default, because a mirror without sound is half a mirror and the stream
    // costs 0.3 Mbit/s on a link that is already carrying video.

    /// <summary>
    /// Whether to ask the phone for its sound at all.
    /// </summary>
    /// <remarks>
    /// Off means the audio stream is never opened: nothing is negotiated, nothing
    /// is decoded and iOS is never asked for a capture session. That is a different
    /// thing from <see cref="AudioMuted"/>, which keeps the stream and drops the
    /// samples.
    /// </remarks>
    [JsonPropertyName("audioEnabled")]
    public bool AudioEnabled { get; set; } = true;

    /// <summary>
    /// Which Windows output to play on; empty means whatever Windows calls the
    /// default output.
    /// </summary>
    /// <remarks>
    /// The endpoint identifier rather than its name, because that is what survives
    /// a reboot and a rename — <c>LuminaMonitor.UsbProbe audio-devices</c> prints
    /// both. An identifier that no longer resolves falls back to the default, with
    /// a line in the journal saying so.
    /// </remarks>
    [JsonPropertyName("audioOutputId")]
    public string AudioOutputId { get; set; } = "";

    /// <summary>Zero to a hundred. The application's own gain, never the system mixer's.</summary>
    [JsonPropertyName("audioVolume")]
    public int AudioVolume { get; set; } = 100;

    /// <summary>Silence without forgetting the volume.</summary>
    [JsonPropertyName("audioMuted")]
    public bool AudioMuted { get; set; } = false;

    /// <summary>
    /// Whether the phone is muted (its own Mute key) while its sound plays here.
    /// </summary>
    /// <remarks>
    /// Off by default: pressing the phone's Mute key while the mirror is up makes
    /// some apps (Apple Music) stop feeding the audio capture for good, so the
    /// safe default is to leave the phone alone and keep the sound — see
    /// <c>LuminaMonitor.Core.Audio.AudioOptions.SilencePhone</c>.
    /// </remarks>
    [JsonPropertyName("audioSilencePhone")]
    public bool AudioSilencePhone { get; set; }

    /// <summary>
    /// How long the sound is held back, in milliseconds, to line it up with the
    /// picture.
    /// </summary>
    /// <remarks>
    /// Fifty by default and it is a starting point, not a measurement: the picture
    /// takes about 96 ms to cross from the phone's screen to this one, the sound's
    /// path is shorter, and this closes roughly the difference. The ear has the
    /// last word, which is why it is a slider — see
    /// <c>LuminaMonitor.Core.Audio.AudioOptions.DefaultDelayMs</c>.
    /// </remarks>
    [JsonPropertyName("audioDelayMs")]
    public int AudioDelayMs { get; set; } = 50;

    /// <summary>The application's own corner of <c>%APPDATA%</c>.</summary>
    public static string Folder { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LuminaMonitor");

    public static string Path { get; } = System.IO.Path.Combine(Folder, "settings.json");

    /// <summary>
    /// Reads the settings, and never destroys them.
    /// </summary>
    /// <remarks>
    /// An earlier version caught the parse failure, built defaults and then saved
    /// them — over the file it had just failed to read. Now the unreadable file
    /// is moved aside rather than overwritten, and where it went is said out
    /// loud: it is very often recoverable by opening it in a text editor.
    /// </remarks>
    public static Settings Load()
    {
        if (!File.Exists(Path))
        {
            var fresh = new Settings();
            fresh.Save();
            return fresh;
        }

        try
        {
            return JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path)) ?? new Settings();
        }
        catch (Exception)
        {
            SetAside();
            return new Settings();
        }
    }

    /// <summary>Where an unreadable settings file was moved, for the status line.</summary>
    public static string? RescuedTo { get; private set; }

    private static void SetAside()
    {
        try
        {
            // Stamped, so a second bad start does not overwrite the first
            // casualty — which would be the same mistake one level up.
            string aside = $"{Path}.corrompu-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(Path, aside);
            RescuedTo = aside;
        }
        catch (Exception)
        {
            // If it cannot even be moved, leave it exactly where it is: a file
            // that cannot be read is still better than no file at all.
        }
    }

    /// <summary>
    /// Writes the settings, atomically.
    /// </summary>
    /// <remarks>
    /// Through a temporary file and a replace, so that the moment of danger is a
    /// rename rather than a truncate-and-write. A crash now leaves either the old
    /// file or the new one, never half of either.
    /// </remarks>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);

            string temporary = Path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(
                this, new JsonSerializerOptions { WriteIndented = true }));

            // Move overwrites on Windows only when told to; File.Replace would
            // also do, but it fails outright when there is nothing to replace.
            File.Move(temporary, Path, overwrite: true);
        }
        catch (Exception)
        {
            // Not worth interrupting the session over.
        }
    }
}
