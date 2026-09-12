using System.Globalization;
using LuminaMonitor.Core;

namespace LuminaMonitor.App;

/// <summary>
/// The window in English. Short on purpose: most of it is read in a status
/// bar at a glance. Numbers are written the English way whatever Windows says.
/// </summary>
internal sealed class EnglishTexts : Texts
{
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    public override string CountersTooltip => "Counters (F3)";

    public override string PillLocked => "locked";
    public override string PillFps(double fps, bool driving) =>
        driving ? string.Create(C, $"driving · {fps:0} fps") : string.Create(C, $"{fps:0} fps");
    public override string PillNoKeyboard => "no keyboard";
    public override string PillStreamStopped => "stream stopped";
    public override string PillWaiting => "waiting";
    public override string PillRestarting => "restarting…";
    public override string PillError => "error";
    public override string PillOffline => "offline";

    public override string MuteTooltip =>
        "Mute: turns the phone's sound off and back on. What this sends is the media keyboard's Mute key, " +
        "not the Action button — that one can't be reached through this protocol (measured 9 September).";
    public override string SideButtonTooltip =>
        "Side button: turns the screen off, and back on when it's off. Unlocking is still Face ID or the " +
        "passcode — the app can only type the passcode if you've put it in unlockCode.";
    public override string VolumeUp => "Volume +";
    public override string VolumeDown => "Volume −";
    public override string Mute => "Mute";
    public override string NoSessionForButton => "No session: the button has nowhere to go.";
    public override string NoSessionForSideButton => "No session: the side button has nowhere to go.";
    public override string LockingIPhone => "Locking the iPhone…";
    public override string SideButtonFailed(string error) => $"side button: {error}";

    public override string LabelVolumeUp => "Volume up";
    public override string LabelVolumeDown => "Volume down";
    public override string LabelMute => "Mute";
    public override string LabelLock => "Lock";
    public override string LabelWake => "Wake screen";
    public override string NameVolumeButton => "Volume button";
    public override string NameActionButton => "Action button · Mute key";
    public override string NameSideButton => "Side button";
    public override string ButtonNotSentNoSession => "Not sent: no iPhone connected";
    public override string ButtonNotSent => "Not sent: sending failed";
    public override string SideButtonsHint => "The side buttons are clickable";
    public override string MenuShowSideButtons => "Show the side buttons";

    public override string WaitingTitle => "Waiting for the iPhone";
    public override string StepPlugCable => "Plug in the USB cable";
    public override string StepUnlock => "Unlock the screen";
    public override string StepDeveloperMode => "Keep Developer Mode on";
    public override string NothingToInstall =>
        "Nothing to install on the phone: the Developer Disk Image is mounted over the cable.";

    public override string OpenAppleDevices => "Open Apple Devices";
    public override string AppleDevicesOpened => "Apple Devices requested — the connection will pick up on its own.";
    public override string AppleDevicesFailed(string error) => $"Couldn't open Apple Devices: {error}";
    public override string LockedTitle => "iPhone locked";
    public override string LockedWithCode =>
        "The screen is off: the video stream is idling, nothing is broken. Waking swipes up and types your passcode.";
    public override string LockedWithoutCode =>
        "The screen is off: the video stream is idling, nothing is broken. Waking turns the screen on; " +
        "unlocking is still Face ID or your passcode, on the phone.";
    public override string WakeScreen => "Wake screen";

    public override string Home => "Home";
    public override string HomeTooltip => "Home button (right-click in the picture).";
    public override string ToIPhone => "To iPhone";
    public override string ToIPhoneTooltip =>
        "Windows clipboard → iPhone (F2). Writes the phone's clipboard; if the service refuses, " +
        "the text is typed on the virtual keyboard instead.";
    public override string FromIPhone => "From iPhone";
    public override string FromIPhoneTooltip =>
        "iPhone clipboard → Windows (F4). Text is copied over; an image or other data is reported, not transferred.";
    public override string StopPasting => "Stop";
    public override string Wheel => "wheel";

    public override string DiagnosticRun(int seconds, string journal) => $"Diagnostic: {seconds} s, log in {journal}";
    public override string SettingsUnreadable(string keptAs) => $"Settings unreadable — the old file is kept as {keptAs}.";
    public override string DdiFoundInRepository(string folder) => $"Developer Disk Image found in the repository: {folder}";
    public override string DiagnosticWithoutDdi =>
        "No Developer Disk Image and nobody to pick one: diagnostic without a session.";
    public override string DdiDialogTitle =>
        "Developer Disk Image: Xcode archive (.xip), Device Support (.dmg) or package (.pkg)";
    public override string DdiDialogFilter =>
        "Apple archives (*.xip;*.dmg;*.pkg)|*.xip;*.dmg;*.pkg|All files (*.*)|*.*";
    public override string ChooseDdiArchive => "No Developer Disk Image: pick an Xcode archive or a Device Support file.";
    public override string NoDdiChosen =>
        "Without a Developer Disk Image the phone can't be driven. Restart the app to pick one.";
    public override string Extracting(string file) => $"Extracting {file} — a few minutes…";
    public override string ArchiveScanned(long gigabytes) => $"Reading the Xcode archive… {gigabytes} GB scanned";
    public override string NoDdiInArchive(string file) =>
        $"{file} has no Developer Disk Image — expected an Xcode 27 archive, a Device Support component or XcodeSystemResources.pkg.";
    public override string DdiReady(string? build) => $"Developer Disk Image ready ({build ?? "unknown build"}).";
    public override string ExtractionFailed(string error) => $"Extraction failed: {error}";

    public override string ReplayEmpty(string path) => $"Empty capture: {path}";
    public override string ReplayRunning(string file, long datagrams, double loopSeconds) =>
        string.Create(C, $"Replay: {file}  ·  {datagrams} datagrams, {loopSeconds:F1} s loop");
    public override string ReplayFailed(string error) => $"Replay failed: {error}";

    public override string IPhonePlugged => "iPhone plugged in.";
    public override string IPhoneUnplugged => "iPhone unplugged.";
    public override string MultiplexerTrouble(string error) => $"Multiplexer: {error}";
    public override string Connecting => "Connecting to the iPhone…";
    public override string MirrorOpen => "Mirror open.";
    public override string MirrorOpenHowTo => "Mirror open · click in the picture to drive · Ctrl+Alt to get the mouse back";
    public override string RetryIn(string error, double seconds) =>
        string.Create(C, $"{error} — retrying in {seconds:0} s.");
    public override string Step(SessionState state) => state switch
    {
        SessionState.Detached => "Unplugged.",
        SessionState.Attached => "Plugged in.",
        SessionState.Paired => "Paired.",
        SessionState.DdiMounted => "Developer Disk Image mounted.",
        SessionState.TunnelUp => "Tunnel open.",
        SessionState.MediaUp => "Mirroring.",
        SessionState.Resetting => "Restarting the mirror…",
        _ => "Error.",
    };
    public override string ActionFailed(string action, string error) => $"{action}: {error}";
    public override string InputFault(string error) => $"input: {error}";
    public override string InputReopening => "Input channel stuck: reopening…";
    public override string InputRestored => "Input channel back.";
    public override string InputLost => "Input channel lost: rebuilding the session.";

    public override string IPhoneLocked => "iPhone locked — the picture comes back when it wakes.";
    public override string ScreenOn => "Screen back on.";
    public override string StreamResumed => "Stream resumed.";
    public override string StreamStopped(DateTime at) => $"Stream stopped at {at:HH:mm:ss}. F3 for the counters.";
    public override string IdleDrivable => "Click in the picture to drive  ·  Ctrl+Alt to get the mouse back";
    public override string WaitingForIPhone => "Waiting for the iPhone.";
    public override string LandscapeIslandLeft => "Phone in landscape, island on the left.";
    public override string LandscapeIslandRight => "Phone in landscape, island on the right.";
    public override string LandscapeUnknown =>
        "Landscape — can't tell which side the island is on (screen too dark). Island and buttons hidden.";
    public override string Portrait => "Phone in portrait.";

    public override string WakingScreen => "Waking the screen…";
    public override string WakeFailed(string error) => $"wake: {error}";
    public override string ScreenOnUnlockYourself =>
        "Screen on — unlocking needs your face, or your passcode on the phone.";
    public override string PasscodeHint =>
        "iPhone locked. If Face ID doesn't unlock it, type your passcode on your keyboard — iOS keeps " +
        "the keypad out of the mirror.";
    public override string Unlocking(int characters) => $"Unlocking: swipe, then {characters} passcode character(s)…";
    public override string CodeSent => "Passcode sent. If the screen stays locked, it's the passcode or Face ID that's needed.";

    public override string WindowLeft => "Window left.";
    public override string Driving => "Driving  ·  left Ctrl+Alt to get the mouse back";
    public override string MouseReturned => "Mouse back to Windows  ·  click in the picture to resume";
    public override string ClickToResume(string reason) => $"{reason}  Click in the picture to resume.";

    public override string NoSessionToPaste => "No session — nothing to paste into.";
    public override string ClipboardUnreadable => "Clipboard unreadable — another app is holding it.";
    public override string ClipboardEmpty => "Clipboard empty.";
    public override string SendingClipboard(int characters) => $"Sending {characters} character(s) to the iPhone's clipboard…";
    public override string ClipboardSent(int characters) =>
        $"{characters} character(s) on the iPhone's clipboard  ·  ⌘V or long-press to paste.";
    public override string ClipboardServiceFallback(string error) =>
        $"Clipboard service unavailable ({error}) — pasting by typing.";
    public override string NoSessionToFetch => "No session — the phone's clipboard is out of reach.";
    public override string ReadingPhoneClipboard => "Reading the iPhone's clipboard…";
    public override string FetchedText(int characters) => $"{characters} character(s) copied from the iPhone.";
    public override string WindowsClipboardLocked => "Windows clipboard locked by another app — nothing copied.";
    public override string PhoneClipboardImage(string type, int bytes) =>
        $"The iPhone's clipboard holds an image ({type}, {Size(bytes)}) — not transferred.";
    public override string PhoneClipboardData(string type, int bytes) =>
        $"The iPhone's clipboard holds data ({type}, {Size(bytes)}) — not transferred.";
    public override string PhoneClipboardEmpty => "The iPhone's clipboard is empty.";
    public override string PhoneClipboardUnreadable(string error) => $"Couldn't read the iPhone's clipboard: {error}";
    public override string NothingTypeable => "Nothing typeable in the clipboard.";
    public override string PasteCancelled(int keystrokes) => $"Paste stopped after {keystrokes} keystrokes.";
    public override string PasteSessionLost(int done, int total) => $"Paste stopped at {done}/{total} — the session dropped.";
    public override string PasteProgress(int done, int total) => $"Pasting… {done}/{total}";
    public override string Pasted(int keystrokes, int skipped, int? cutAt) =>
        $"Pasted: {keystrokes} keystrokes."
        + (skipped > 0 ? $" {skipped} character(s) the keyboard can't type, skipped." : "")
        + (cutAt is int cut ? $" Cut at {cut} characters." : "");
    public override string PasteFailed(string error) => $"Paste stopped: {error}";

    public override string Audio => "Audio";
    public override string AudioTooltip =>
        "The iPhone's sound, over the cable, on the Windows output you pick. Decoded here — Windows has no " +
        "AAC-ELD decoder of its own.";
    public override string AudioSection => "iPhone sound";
    public override string AudioOutput => "Output";
    public override string AudioDefaultOutput => "Windows default output";
    public override string AudioVolume => "Volume";
    public override string AudioDelay => "Delay";
    public override string AudioDelayHint =>
        "Holds the sound back to line it up with the picture. Raise it if the sound arrives first.";
    public override string AudioRetry => "Try again";
    public override string AudioMilliseconds(double milliseconds) => string.Create(C, $"{milliseconds:0} ms");

    public override string AudioOff => "Sound off — it plays on the iPhone instead.";
    public override string AudioNoSession => "No mirror yet: the sound comes with it.";
    public override string AudioOpening => "Opening the sound…";
    public override string AudioRefused(string reason) => $"No sound: {reason}";
    public override string AudioRunning(string device, string level) =>
        $"Playing on {device}  ·  {level}";

    public override string DimGestureInvalid => "dimGesture needs five numbers — brightness left alone.";
    public override string OpeningControlCentre => "Opening Control Center…";
    public override string Dimming => "Lowering the brightness…";
    public override string Dimmed =>
        "Brightness lowered. If that's not what happened, adjust dimGesture in settings.json.";
    public override string DimFailed(string error) => $"Brightness: {error}";

    public override string Counters(in CounterSample s) => string.Create(C,
        $"{(s.Driving ? "DRIVING" : "idle")}   {s.Width}x{s.Height}->{s.ShownWidth,4:0}   " +
        $"{s.ReceivedFps,4:0} fps received   {s.ShownFps,4:0} fps shown   decode {s.DecodeMs,5:0.0} ms   " +
        $"mouse {s.MouseHz,4:0}/s -> {s.SentHz,3:0}/s (-{s.DroppedHz,4:0}/s, queue {s.Queue})   " +
        $"state {s.State}   errors {s.Errors}");
    public override string CountersNoPicture(bool driving, double mouseHz, SessionState state) => string.Create(C,
        $"{(driving ? "DRIVING" : "idle")}   no picture   mouse {mouseHz,4:0}/s   state {state}");

    public override string MenuLanguageAuto => "Language: same as Windows";
    public override string LanguageSaved(string setting) => setting switch
    {
        "en" => "Interface language: English (saved).",
        "fr" => "Interface language: French (saved).",
        _ => "Interface language: same as Windows (saved).",
    };

    private static string Size(int bytes) => bytes switch
    {
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => string.Create(C, $"{bytes / 1024.0:0.#} KB"),
        _ => string.Create(C, $"{bytes / (1024.0 * 1024.0):0.#} MB"),
    };
}
