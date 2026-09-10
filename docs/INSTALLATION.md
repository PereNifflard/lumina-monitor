**English** · [Français](INSTALLATION.fr.md)

# Installation

This guide starts from a bare PC and ends when the iPhone's screen sits in a
Windows window and responds to the mouse. Budget **ten minutes**, half of it
downloading from Apple.

To build it yourself instead of downloading, see [`BUILD.md`](BUILD.md).

## Prerequisites

| | What | Where |
|---|---|---|
| PC | Windows 10 or 11, **x64** | |
| PC | **Apple Devices** app — it brings the USB multiplexer and pairing, and it's the **only** Apple component installed on the PC | [Microsoft Store](https://apps.microsoft.com/detail/9np83lwlpz9k) |
| iPhone | **iOS 27 or newer** (on iOS 26, only the chassis buttons work) | [Update your iPhone](https://support.apple.com/en-us/HT204204) |
| iPhone | **Developer Mode** enabled, under Settings > Privacy & Security | [Enable Developer Mode](https://developer.apple.com/documentation/xcode/enabling-developer-mode-on-a-device) |
| File | an **Xcode 27** archive (`Xcode_27*.xip`, ~2 GB) **or** a "Device Support" component (`.dmg`, ~100 MB) | [developer.apple.com/download/all](https://developer.apple.com/download/all/) — free Apple account |

No .NET runtime to install: the downloaded executable carries its own.

### Why an Apple archive

The **Developer Disk Image** is a binary signed by Apple; it's what brings
the control services (HID, display) to the phone. It is **not** in this
repository and never will be: everyone extracts it from their own download,
with the tool built into the app. The file stays on your machine.

The Device Support `.dmg` is the shortcut when it's offered for your iOS
version: 100 MB instead of 2 GB, and a few seconds of extraction instead of a
minute.

## 1. Download the release

On the repository's **Releases** page, grab
`LuminaMonitor-<version>-win-x64.zip` and unzip it wherever you like — a
folder under `Documents` works fine. Two executables inside:

- `LuminaMonitor.App.exe` — the window;
- `LuminaMonitor.UsbProbe.exe` — the diagnostic probe, worth keeping for bad
  days.

Nothing installs, nothing is written to the registry. To uninstall, delete
the folder and `%APPDATA%\LuminaMonitor`.

> On first launch, Windows SmartScreen may show "Windows protected your PC":
> the executable isn't signed with a commercial certificate. **More info** >
> **Run anyway**.

## 2. First launch: the Developer Disk Image

The window opens and asks for the Apple archive. Pick your Xcode `.xip`,
your Device Support `.dmg`, or the `XcodeSystemResources.pkg` you already
pulled from one. The app does the rest on its own — xar → pbzx/xz → cpio →
UDIF → HFS+/APFS — and lays down a faithful copy of the `Restore/` tree in:

```
%APPDATA%\LuminaMonitor\ddi\<build>
```

Budget **about a minute** for a `.xip` (58 s measured on Xcode 27 beta 6,
4 GB scanned, progress shown in the status bar), a few seconds for a `.dmg`.
**This happens once per iOS version**: the path is remembered in settings,
and later launches go straight to the mirror.

## 3. Plug in, unlock

1. Plug the iPhone in over USB-C. On the very first connection, the phone
   asks to "Trust This Computer": answer **yes**, and enter your passcode
   ([Apple support](https://support.apple.com/en-us/102518)).
2. **Unlock the screen** and leave it unlocked while the session opens: iOS
   refuses to mount the Developer Disk Image on a locked phone.
3. Launch `LuminaMonitor.App.exe`. The status bar narrates the setup: image
   mounted, tunnel open, video stream. Within a few seconds, the phone's
   screen appears inside a chassis drawn to its exact dimensions.

If the app says the Apple multiplexer isn't running, the banner's **Open
Apple Devices** button takes care of it.

## 4. Usage

| Gesture | Effect on the phone |
|---|---|
| **Click in the image** | takes control: the mouse now belongs to the phone |
| **Left Ctrl + Alt** | returns the mouse to the PC (the *left* Alt: right Alt + Ctrl builds AltGr, which a French keyboard needs) |
| **Left click** | taps the exact coordinate — **absolute** pointer, the finger lands where you click |
| **Hold** | long press (the tap is delayed by 180 ms, long enough to see what the mouse is doing) |
| **Click and drag** | a finger swipe on the glass |
| **Right click** | the Home/main button, back to the home screen |
| **Wheel** | scrolling, one notch = one finger swipe; `invertWheel` setting for the other direction |
| **Keyboard** | everything goes to the phone's virtual keyboard, accents and dead keys included |
| **Buttons drawn on the chassis** | volume up/down, mute, side button — the real phone buttons get pressed; the clicked button lights up for a quarter second |
| **Button at the Action button's position** | toggles **mute** (the Mute switch). The Action button itself — the ringer/silent toggle — isn't reachable through this protocol, and the app doesn't pretend otherwise |
| **Side button** | turns the screen off; while the screen is off, the same button turns it back on (see "Lock, unlock" below) |
| **F2** | sends the Windows clipboard **into the iPhone's clipboard** (⌘V or a long press to paste on the phone). F2, not Ctrl+V: while in control, Ctrl+V would go to the phone, which expects Cmd+V |
| **F4** | pulls the iPhone's clipboard into Windows's |
| **F3** | shows the counters: frames/s, latency, reports sent, errors |

The **Paste** and **Clipboard** buttons in the bottom bar do the same thing
as F2 and F4.

The interface follows the Windows display language (English or French) and
can be forced in the settings.

### Clipboard

The phone has a clipboard, and it's written over the cable: text arrives
**whole and instantly**, accents and emoji included, and then pastes on the
phone like any copy-paste between Apple devices. If the service refuses
(older iOS, service missing from the directory), the app falls back to the
old method — typing the text on the virtual keyboard, character by
character — and **says so in the status bar**, so a slow paste doesn't pass
for a fast one.

In the other direction, only **text** comes back. A phone's clipboard very
often holds a photo: the app then announces it ("an image, public.png,
1.2 MB — not transferred") rather than returning emptiness that would read as
"there was nothing."

Nothing happens on its own: **no automatic sync**, in either direction. The
clipboard's content is never written to the log, only the character count.

### Lock, unlock

Clicking the drawn side button **turns the phone's screen off**. The app
then shows a banner, "iPhone locked — the screen is off, the stream idles,
nothing is broken," and **stops treating that as a failure**. A second click
(or the banner's **Wake screen** button) turns the screen back on, and
**the image comes back on its own in under a second**.

The banner also shows up when it's the **phone** locking itself, or your
hand on the real button: the app recognizes it from the stream's bitrate,
not from what it commanded.

Measured on September 9, 2026: the stream doesn't die during lock, it drops
to two packets and one fully black frame per second. Nothing needs
remounting, no image reset fires, and the session stays open.

**What the app cannot do: unlock.** Waking the screen is a button press and
always works; what's behind it is the lock screen, and getting past it
requires **Face ID** — so your face in front of the phone — or **the
passcode**. There's no way around that, and the app doesn't pretend
otherwise.

If you still want to unlock from the PC, you can write your passcode into
settings:

```json
"unlockCode": "123456"
```

The app then swipes the lock screen up and types the passcode on the virtual
keyboard. **The trade-off is spelled out plainly:** this file isn't
encrypted, so anyone who can read `%APPDATA%\LuminaMonitor\settings.json` can
read your phone's passcode. Empty by default, and empty is the right choice
for almost everyone. The passcode never appears in the log — status lines
only count the number of characters.

Settings (wheel direction, chassis color, window position, `unlockCode`)
live in `%APPDATA%\LuminaMonitor\settings.json`. Aside from `unlockCode` if
you fill it in, nothing secret is written there: pairing belongs to Apple,
the app only reads it.

### Audio (over Bluetooth)

Over the cable, the iPhone's sound doesn't come through (AAC-ELD, which
Windows can't decode). It comes through **Bluetooth**, and the **Audio**
button in the bottom bar opens the panel that handles it. Prerequisites: the
iPhone is **paired over Bluetooth** with this PC (Windows Settings ›
Bluetooth & devices), and Bluetooth is on **in the iPhone's Settings** — not
only in Control Center.

**iPhone sound on this PC.** Turn on the "iPhone sound on this PC" switch. The
app looks for the iPhone among the Bluetooth devices (the one named like the
iPhone on the cable, otherwise the first; a list appears when there are
several) and opens the connection. The state line says where it stands:
"Connecting…", "Sound active", "Waiting", "Refused: …". A refusal isn't final:
the connection **keeps listening**, and tapping this PC in the iPhone's
**Settings › Bluetooth** opens it; **Retry** asks again from the PC. A dot on
the Audio button tells you with the panel closed: green while the sound flows,
amber while it's expected. The choice is remembered (`phoneAudio`) and reopened
at the next launch.

**Choosing speakers or headphones.** Windows plays this sound on **its default
output**, the one the "Output: …" line shows; the Windows interface in use has
no output setting of its own. **Choose output…** opens Windows's page (Volume
mixer): pick the output there, or change Windows's default output. The app
never changes the default output itself.

**This PC's microphone during a call.** During a call (phone, FaceTime, a VoIP
app), pick **this PC** as the audio output on the iPhone's call screen. The
hands-free link appears and the "PC microphone for calls" switch becomes
available (it stays greyed out otherwise): pick the microphone and the output
to hear the call on, then turn it on. This PC's microphone goes to the iPhone,
the other end's voice plays on the chosen output. When the call ends, the
bridge stops by itself and the switch goes back to off, with the reason.
**During the call, the iPhone uses this PC's microphone instead of its own**:
that is why this switch is never on by default nor at launch. Only the
microphone and output choices are remembered (`callMicrophone`, `callOutput`).

**Limits, stated plainly.** Sound over Bluetooth is proven in software up to
the radio, but not yet on an iPhone: on the day of the test, the phone didn't
answer the PC's Bluetooth call (`0x8007001F`). The call bridge is verified
between the PC's own devices, not yet on the phone's endpoints. Quality: A2DP
for music, a hands-free kit's (8 or 16 kHz) for calls. Details and the test
protocol are in [`BLUETOOTH_AUDIO.md`](BLUETOOTH_AUDIO.md).

## Troubleshooting

**The log first**: `%APPDATA%\LuminaMonitor\lumina.log`. It's kept on every
run, not just when diagnosing — state transitions, protocol lines,
reconnection decisions, and every unhandled exception with its stack trace.
Rotates at 5 MB to `lumina.1.log`. This is the file to attach to an issue
(an excerpt only, and **without the device identifier**).

| Symptom | What to do |
|---|---|
| **Frozen image** | The phone's display service went quiet. The app unmounts and remounts the image on its own — the only remedy observed; unplugging the cable isn't enough. Let it work, it says "Restarting the mirror…" |
| **"Restart the iPhone"** | Three image remounts without the stream recovering. At that point, the phone really needs a restart. |
| **"Apple multiplexer stopped responding"** | It's listening but stays silent for five seconds. **Restart the Apple Devices app**, or replug the cable. |
| **"Apple multiplexer not running"** | Nothing is listening on `127.0.0.1:27015`: open **Apple Devices** (banner button) or plug in the iPhone, the multiplexer starts on demand. |
| **"Device not paired"** | Open Apple Devices and tap "Trust This Computer" on the phone. |
| **"Developer Mode disabled"** | Settings > Privacy & Security > Developer Mode. The phone reboots. |
| **"Unlock the iPhone"** | Mounting is waiting for an unlocked screen; the app retries on its own. |
| **After restarting the phone** | The Developer Disk Image needs remounting — iOS forgets it on every boot. The app does this on its own, costing a few seconds. |
| **Nothing starts, or everything freezes** | **One session at a time**: the phone's display service serves only one. Two windows, or the probe while the app is running, and it's a freeze. Close the other one. |
| **The mirror refuses to come back right away** | Between two sessions, the phone refuses a new stream **for one to two minutes**, and every refused attempt renews the refusal. The app spaces out its retries (5 s, doubling, capped at 30 s). Wait, don't push. |
| **Extraction refuses the archive** | The message says which of three cases: unexpected file, incomplete archive (redownload), or the resources package missing (this isn't an Xcode 27 archive). |
| **Touch doesn't work, buttons do** | That's iOS 26: `CoreDeviceError 9021`, "Remote control requires iOS 27.0 or later." You need iOS 27. |
| **"Clipboard service unavailable"** | The `pasteboardservice` didn't answer. The app fell back to character-by-character typing: the text still arrives, more slowly, and without anything the US keyboard layout can't spell. |
| **Audio: "Refused: the iPhone doesn't answer over Bluetooth"** | Bluetooth is off on the iPhone, or only "disconnected" from Control Center. Turn it on in Settings › Bluetooth, then tap this PC in the list: the connection, still listening, opens. |
| **Audio: "No iPhone paired with this PC over Bluetooth"** | Pair the iPhone in Windows Settings › Bluetooth & devices (the panel's **Bluetooth settings…** button), then **Retry**. |
| **Audio: sound active but nothing heard** | The sound plays on Windows's default output: check it with **Choose output…**, and the iPhone's volume. |
| **The iPhone's microphone is dead in its other apps** | A call bridge still holds the hands-free link: turn off the "PC microphone for calls" switch, or close the app. |
| **"iPhone locked" banner won't go away** | The screen is off — whether from the app, your hand, or the phone's own auto-lock. Click **Wake screen**; if it's still locked afterward, that's Face ID or the passcode, on the phone. |

For a ten-second report instead of a screenshot:

```
LuminaMonitor.App.exe --diagnostic 10
```

The window opens, runs for ten seconds, writes one line of counters per
second to the log, and closes itself.
