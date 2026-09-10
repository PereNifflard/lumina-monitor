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
/// the developer image was unpacked, which way up the window was left, the
/// audio choices, and a few cosmetic ones.
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

    /// <summary>
    /// Play the iPhone's sound on this PC, over Bluetooth: the switch in the
    /// Audio panel, remembered.
    /// </summary>
    /// <remarks>
    /// Off by default. When on, the window opens the connection again at
    /// start-up — a timed <c>--diagnostic</c> run excepted — and closes it on
    /// the way out. See docs/BLUETOOTH_AUDIO.md.
    /// </remarks>
    [JsonPropertyName("phoneAudio")]
    public bool PhoneAudio { get; set; } = false;

    /// <summary>
    /// Which paired phone the sound comes from, when Windows knows more than
    /// one. Empty means the one named like the iPhone on the cable, else the
    /// first.
    /// </summary>
    [JsonPropertyName("phoneAudioDevice")]
    public string PhoneAudioDevice { get; set; } = "";

    /// <summary>
    /// The PC microphone sent to the iPhone during a call routed to this PC,
    /// as a Windows endpoint id. Empty means Windows's default for calls.
    /// </summary>
    /// <remarks>
    /// Only the choice is remembered, never the switch itself: the call bridge
    /// takes over the phone's microphone, so it starts on an explicit click
    /// during a call and at no other time.
    /// </remarks>
    [JsonPropertyName("callMicrophone")]
    public string CallMicrophone { get; set; } = "";

    /// <summary>
    /// Where the other end of such a call is heard, as a Windows endpoint id.
    /// Empty means Windows's default for calls.
    /// </summary>
    [JsonPropertyName("callOutput")]
    public string CallOutput { get; set; } = "";

    /// <summary>Where the window was when it was last closed. Zero means centre it.</summary>
    [JsonPropertyName("windowBounds")]
    public double[] WindowBounds { get; set; } = [];

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
