using System.Globalization;

namespace LuminaMonitor.Core;

/// <summary>Core's sentences in English. Numbers are written the English way, whatever Windows says.</summary>
internal sealed class EnglishCoreTexts : CoreTexts
{
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    public override string MultiplexerNotResponding =>
        "Apple multiplexer stopped responding: restart the Apple Devices app (or replug the cable).";
    public override string MultiplexerNotRunning =>
        "Apple multiplexer not running: open the Apple Devices app, or plug in the iPhone.";

    public override string NotConnected => "Session not connected: call ConnectAsync first.";
    public override string NoDeviceAttached => "No device attached.";
    public override string DeviceWithoutUdid => "Device without a UDID.";
    public override string NotPaired =>
        "Device not paired: open the Apple Devices app, then tap “Trust This Computer” on the iPhone.";
    public override string PairRecordUnreadable => "Pairing record unreadable.";
    public override string StartSessionRefused(string refusal) => $"StartSession refused: {refusal}";
    public override string DeveloperModeOff(object status) =>
        $"Developer Mode disabled (DeveloperModeStatus = {status}) — Settings > Privacy & Security > Developer Mode.";
    public override string DdiUnavailable(object status) => $"Developer Disk Image unavailable: {status}.";
    public override string HidServiceMissing =>
        "HID service missing from the directory: the Developer Disk Image isn't mounted.";
    public override string ServiceMissing(string name) => $"Service missing from the directory: {name}";
    public override string TcpNoAnswer(int port) => $"TCP connection to port {port}: no answer.";
    public override string RemoteXpcNoSettings => "RemoteXPC: no SETTINGS from the phone within 3 s";
    public override string RemoteXpcNoReply => "RemoteXPC: the phone didn't reply in time.";

    public override string CallInProgress =>
        "Call in progress on the iPhone: iOS doesn't allow mirroring during a call. The picture comes back when the call ends.";
    public override string RemoteControlNeedsIos27 => "Remote control requires iOS 27 or later on the iPhone.";
    public override string StreamRefused(int? code) =>
        $"The phone refused the video stream{(code is int c ? $" (code {c})" : "")}.";

    public override string UnlockForUnmount =>
        "iPhone locked: removing the old Developer Disk Image waits until it's unlocked.";
    public override string UnlockForUpload =>
        "iPhone locked: sending the Developer Disk Image waits until it's unlocked.";
    public override string UnlockToRestartMirror => "Unlock the iPhone to restart the mirror.";
    public override string RestartIPhone => "Restart the iPhone.";

    public override string BuildManifestUnreadable => "BuildManifest unreadable";
    public override string NoPersonalizationIdentifiers => "no personalization identifiers";
    public override string NoNonce => "no nonce";
    public override string NoBuildIdentity(long boardId, long chipId) =>
        string.Create(C, $"No BuildIdentity for BoardId {boardId} / ChipID 0x{chipId:X}.");
    public override string AppleSigningServer(string status, int httpStatus) =>
        $"Apple's signing server answered: {status} (HTTP {httpStatus})";
    public override string PhoneNoAnswer(string command, double seconds) =>
        string.Create(C, $"The phone didn't answer {command} within {seconds:N0} s.");
    public override string ImageUploadStalled(long sent, long size) =>
        string.Create(C, $"Image upload stuck after {sent:N0} of {size:N0} bytes.");

    public override string FileNotFound(string file) => $"{file}: file not found.";
    public override string FileTooShort(string file, long bytes) =>
        string.Create(C, $"{file}: too short to be an Apple archive ({bytes:N0} bytes) — incomplete download?");
    public override string XarWithoutContent(string file) =>
        $"{file}: xar archive with neither Content nor Payload — neither an Xcode archive nor an Apple package.";
    public override string UnexpectedFile(string file, string detail) =>
        $"{file}: unexpected file — expected an Xcode archive (.xip), a Device Support component (.dmg) or a package (.pkg). Detail: {detail}";
    public override string NoContentEntry(string file) => $"{file}: no Content entry — not an Xcode archive.";
    public override string ArchiveDamaged(string file, double gigabytes, string detail) =>
        string.Create(C, $"{file}: archive incomplete or damaged after {gigabytes:N1} GB — download it again from Apple. Detail: {detail}");
    public override string SystemResourcesMissing(string file) =>
        $"{file}: XcodeSystemResources.pkg not found — this archive doesn't carry the developer disk images (Xcode 27 expected).";

    public override string UnknownButton(string name, string expected) => $"Unknown button: {name} (expected: {expected})";
    public override string HidChannelStuck(double milliseconds) =>
        string.Create(C, $"HID channel stuck: a report didn't leave within {milliseconds:0} ms");
    public override string ClipboardNeedsTunnel => "Session not connected: the clipboard goes through the tunnel.";
    public override string ClipboardServiceMissing => "Clipboard service missing from the phone's directory.";

    private static string Phone(string name) => string.IsNullOrWhiteSpace(name) ? "the phone" : name;
    private static string Code(int? code) => code is int c ? $" (0x{c:X8})" : "";

    public override string BluetoothAudioNeedsWindows2004 =>
        "Receiving the phone's sound over Bluetooth requires Windows 10 version 2004 or later.";
    public override string PhoneAudioOpening(string phone) => $"Connecting to {Phone(phone)} over Bluetooth…";
    public override string PhoneAudioOpen(string phone) =>
        $"Connected: what {Phone(phone)} plays comes out of this PC (Windows's default output).";
    public override string PhoneAudioWaiting(string phone) =>
        $"Connection with {Phone(phone)} lost. Pick this PC in the phone's audio menu, or reconnect.";
    public override string PhoneAudioClosed => "Phone sound on this PC: off.";
    public override string PhoneAudioTimedOut(string phone) =>
        $"{Phone(phone)} didn't answer over Bluetooth: is Bluetooth on in the iPhone's Settings (not only Control Center), "
        + "and the phone within range? You can also tap this PC in the iPhone's Settings › Bluetooth.";
    public override string PhoneAudioDenied(int? code) =>
        $"Windows refused the Bluetooth audio connection{Code(code)}. Is Bluetooth on on this PC?";
    public override string PhoneAudioNotAvailable =>
        "This phone is no longer a Bluetooth audio source for Windows: pair it again in Bluetooth settings.";
    public override string PhoneAudioUnknownFailure(int? code) =>
        $"The Bluetooth audio connection failed{Code(code)}.";
    public override string PhoneAudioError(int? code) =>
        $"The Bluetooth audio connection could not be set up{Code(code)}.";
    public override string CallBridgeEndpointGone =>
        "Call through the PC stopped: the phone closed its hands-free audio link (call ended?).";
    public override string CallBridgeFailed(int code) =>
        $"Call through the PC stopped on an audio error (0x{code:X8}).";
}
