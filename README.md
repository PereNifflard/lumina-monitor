**English** · [Français](README.fr.md)

# Lumina Monitor Desktop

Drive your iPhone from Windows, **over the USB-C cable**, with your PC's mouse
and keyboard — no Raspberry Pi, no virtual machine, and **not a single
third-party library on the PC**.

The phone's screen appears in a window, inside a chassis drawn to its exact
dimensions. The pointer is **absolute**: wherever you click in the image, the
finger lands on the glass. The PC keyboard types on the phone, and the
chassis buttons press the real buttons.

> **Status: preview.** Full control needs **iOS 27**. On iOS 26, the chassis
> buttons work but touch is refused by the phone
> (`CoreDeviceError 9021`, "Remote control requires iOS 27.0 or later").

## Project rule

All the code that runs on the PC is ours. The only Apple component allowed is
the one already installed with the **Apple Devices** app: the USB multiplexer
(`AppleMobileDeviceProcess.exe`), the equivalent of a device driver.
Everything above that — pairing, the developer disk image, the tunnel,
RemoteXPC, the video stream, input injection, and even the xar, pbzx, xz,
cpio, UDIF, HFS+ and APFS readers — is reimplemented from the protocol, not
imported.

## What you need

| | What | Where |
|---|---|---|
| PC | Windows 10 or 11, x64 | |
| PC | **Apple Devices** app (Microsoft Store) — it provides the USB multiplexer and pairing | [apps.microsoft.com](https://apps.microsoft.com/detail/9np83lwlpz9k) |
| PC | **.NET 8 SDK**, only needed to build it yourself | [dotnet.microsoft.com](https://dotnet.microsoft.com/download/dotnet/8.0) |
| iPhone | **iOS 27** (iOS 26: buttons only), **Developer Mode** enabled | [Settings > Privacy & Security > Developer Mode](https://developer.apple.com/documentation/xcode/enabling-developer-mode-on-a-device) |
| iPhone | paired with the PC: plug it in, then tap "Trust This Computer" | |
| File | **an Xcode 27 archive** (`Xcode_27_beta_N.xip`, ~2 GB) **or** a "Device Support" component (`.dmg`, ~100 MB) | [developer.apple.com/download/all](https://developer.apple.com/download/all/) (free Apple account) |

That last row deserves a sentence. The **Developer Disk Image** is a binary
signed by Apple; it's what brings the control services (HID, display) to the
phone. It is **not** in this repository and never will be: everyone extracts
it from their own download, with the tool below.

## Installation

```
git clone <this repo>          # or download and unzip the repository archive
cd "LuminaMonitor Desktop"
dotnet build -c Release
```

The executable lands at
`src\LuminaMonitor.App\bin\Release\net8.0-windows\LuminaMonitor.App.exe`.

## First run

On first launch, the window asks for the Apple archive: pick your
`Xcode_27_beta_N.xip`, your Device Support `.dmg`, or your
`XcodeSystemResources.pkg`. It pulls the Developer Disk Image out on its
own — xar → pbzx/xz → cpio → UDIF → HFS+/APFS — and lays down a faithful copy
of the `Restore/` tree in `%APPDATA%\LuminaMonitor\ddi\<build>`.

Budget **one to two minutes** for a `.xip` — 58 s measured on Xcode 27 beta 6,
4 GB scanned, progress shown in the status bar — and a few seconds for a
`.dmg`. This happens once per iOS version; the path is remembered in
settings.

From the command line, the same thing:

```
dotnet run --project src/LuminaMonitor.UsbProbe -- extract-devsupport "C:\...\Xcode_27_beta_6.xip" ddi27
```

## Usage

Plug in the iPhone, unlock it, launch the app. It mounts the image, opens the
tunnel and the mirror on its own; the status bar reports where it stands.

- **Take control**: click inside the image. The mouse then belongs to the
  phone.
- **Give the mouse back**: **left Ctrl + Alt**. (The *left* Alt: Windows
  builds AltGr out of right Alt + Ctrl, and a French keyboard would lose its
  most-used key otherwise.)
- **Left click**: taps the exact coordinate. The tap is **delayed by 180 ms**
  — long enough to see whether the finger slides: a crisp tap stays a tap, a
  movement becomes a drag, and a hold becomes a long press.
- **Right click**: the Home/main button (back to the home screen).
- **Wheel**: scrolling, one notch = one finger swipe. `invertWheel` setting
  for the other direction.
- **Buttons drawn on the chassis**: volume up/down, lock, Action button. They
  press the real ones.
- **Keyboard**: everything you type goes to the phone's virtual keyboard,
  accents and dead keys included.
- **F2**: types the Windows clipboard onto the phone. (F2, not Ctrl+V: while
  in control, Ctrl+V would go to the phone, which expects Cmd+V.)
- **F3**: counters (frames/s, latency, reports sent, errors).
- **Audio** (bottom bar button): the phone's sound, over the cable, on the
  Windows output you pick — decoded by an AAC-ELD decoder written into this
  project, because Windows ships none. Volume, mute and a delay to line the
  sound up with the picture; the iPhone itself is muted meanwhile, so the
  room stays quiet.

The interface follows the Windows display language (English or French) and
can be forced in the settings.

## Known limitations

- **iOS 27 minimum** for touch. On iOS 26, only the buttons work.
- The **Developer Disk Image must be remounted after every reboot** of the
  phone. The app does this on its own, costing a handful of seconds at
  startup.
- Mounting the image requires an **unlocked screen**: iOS answers
  `DeviceLocked` and refuses while the phone is locked. The app says so and
  retries.
- The **video stream is mandatory while in control**: it's what opens the HID
  door. No mirror, no tap (the buttons, though, work without a stream).
- **One session at a time.** The phone's display service serves only one;
  two windows, or a probe while the app is running, will freeze both.
- Between two sessions, the phone **refuses a new stream for one to two
  minutes**, and every refused attempt renews the refusal. The app spaces out
  its retries (5 s, doubling, capped at 30 s) instead of hammering.
- The **Apple Devices** app must be running, or at least have been launched
  once since the phone was plugged in: it's the one carrying the
  multiplexer.
- The sound is held back by a **fixed delay you set by ear** (50 ms by
  default): the gap between sound and picture is measured and written to the
  log every five seconds, but nothing corrects it on its own yet — see
  [`docs/AUDIO.md`](docs/AUDIO.md).
- The **PC's microphone can't serve as the iPhone's microphone**: the cable
  carries no such capability, Bluetooth or not.

## Troubleshooting

**The log first**: `%APPDATA%\LuminaMonitor\lumina.log`. It's kept on every
run, not just when diagnosing — every state transition, protocol lines,
reconnection decisions, and **every unhandled exception** with its stack
trace. Rotates at 5 MB to `lumina.1.log`.

| Symptom | What it means |
|---|---|
| "Apple multiplexer not running" | Nothing is listening on 127.0.0.1:27015: open the **Apple Devices** app, or plug in the iPhone. The banner's **Open Apple Devices** button does this. |
| "Apple multiplexer stopped responding" | It's listening but silent (5 s without a word): **restart** Apple Devices, or replug the cable. |
| "Device not paired" | Open Apple Devices, then tap "Trust This Computer" on the phone. |
| "Developer Mode disabled" | Settings > Privacy & Security > Developer Mode (the phone reboots). |
| "Unlock the iPhone" | Mounting the image is waiting for an unlocked screen; it retries on its own. |
| Frozen image, then "Restarting the mirror…" | The display service went quiet. The app unmounts and remounts the image on its own — the only remedy observed; unplugging the cable is not enough. |
| "Restart the iPhone" | Three image remounts without the stream recovering. At that point, the phone needs a real restart. |
| Extraction refuses the archive | The message says which of three cases: unexpected file, incomplete archive (redownload), or the resources package missing (this isn't an Xcode 27 archive). |

Offline probes, no iPhone needed:

```
dotnet run --project src/LuminaMonitor.UsbProbe -- offer-check         # the media offer, byte for byte
dotnet run --project src/LuminaMonitor.UsbProbe -- watchdog-selftest   # the stream watchdog
dotnet run --project src/LuminaMonitor.UsbProbe -- tcp-selftest        # the TCP stack against a paper phone
```

Probes with the iPhone plugged in: `list`, `info`, `session`, `devmode`,
`mount`, `catalogue`, `tap`, `keys`, `button`, `mirror-test`. **One at a time,
and never while the app is running.** Those that open a session look for the
Developer Disk Image in the folder named by the `LUMINA_DDI` environment
variable, or in `ddi27` by default — unlike the app, which remembers its own.
Careful: `mirror-test`, `clock-test` and `motion-test` write captures of the
phone's screen to the current directory.

## Security

The full detail — what goes out, what gets written, what's required from the
phone — is in [`docs/SECURITY.md`](docs/SECURITY.md). The essentials:

- **No telemetry.** The project has exactly one network call, described
  below, and it only fires when mounting the Developer Disk Image.
- **One request, to Apple.** Since iOS 17, the Developer Disk Image can only
  be mounted with a ticket Apple signs for a given device. The project
  therefore posts to `gs.apple.com` your iPhone's **ECID** (its permanent
  hardware identifier), its board and chip model, a nonce drawn by the phone,
  and the fingerprints of the image's components. No name, no address, no
  content from the phone. This is the same request Xcode makes for the same
  operation; without it, there is no mount.
- **Apple root pinned.** `gs.apple.com` chains to a root absent from
  Windows's stores. Rather than disabling validation, Apple's **public**
  root is embedded and checked by fingerprint; the rest of TLS validation
  stays intact.
- **The log contains the UDID and the device name.**
  `%APPDATA%\LuminaMonitor\lumina.log` is kept on every run; it never leaves
  on its own, but read it before attaching it to a bug report. The probe, for
  its part, writes captures of your screen to the current directory.
- **No third-party code on the PC.** No NuGet, no downloaded binary, no
  installed driver. The only Apple component is the one the Store's Apple
  Devices app put there. No third-party code runs — the one exception is
  data, not code: the AAC-ELD tables under
  [`Media/Aac/Tables/`](src/LuminaMonitor.Core/Media/Aac/Tables/NOTICE.md),
  numbers imposed by the MPEG-4 Audio standard rather than a library.
- **No Apple image in the repository.** The Developer Disk Image is a binary
  signed by Apple: everyone extracts it from their own download, it never
  travels through here. `.gitignore` blocks `/ddi*/`, `*.xip`, `*.dmg` and
  `*.pkg`.
- **Pairing is Apple's own.** The project reads the record the Apple app
  negotiated; it doesn't create one, doesn't store one, and asks for no
  secret. Nothing sensitive lives in `settings.json`.
- **The Apple process is never stopped** by this application, whatever
  happens: it holds the USB interface and the pairing records.
- **Nothing on the phone's content is read.** The app receives a screen image
  and sends HID reports; it opens no file on the phone.

## How it's built

Nine layers, from the cable to the tap: usbmux multiplexer → lockdown (mutual
TLS) → Developer Mode → Developer Disk Image (TSS signature, mount) →
CoreDevice tunnel → user-space TCP/IPv6 stack → RSD + RemoteXPC (minimal
HTTP/2) → DisplayService video stream → absolute HID and buttons.

The detail is in [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) (the stack and
the walls hit along the way), [`docs/CORE_DESIGN.md`](docs/CORE_DESIGN.md)
(the library), [`docs/VIDEO_DESIGN.md`](docs/VIDEO_DESIGN.md) (the stream),
[`docs/APP_DESIGN.md`](docs/APP_DESIGN.md) (the window) and
[`docs/SECURITY.md`](docs/SECURITY.md) (what goes out, what gets written).

### Why not Bluetooth?

Windows 11 *can* publish a Bluetooth LE keyboard/mouse in user mode, but two
walls close off that path, verified on the project's own machine: dual-mode
pairing is unstable (iOS merges the Classic and LE identities and wrecks the
fresh HID link), and **iOS only accepts an absolute pointer over Bluetooth
Classic**, a role Windows doesn't offer without a kernel driver. The probe
that demonstrated this is kept in
[`experiments/BleProbe`](experiments/BleProbe/README.md), negative result
included: it sits outside the solution and doesn't build with the app.

## Credits and sources

**This project discovers nothing on its own.** Apple's protocol isn't
publicly documented: what's known about it was wrested out over years by
people who published their notes, and this repository exists because they
did.

- [**pymobiledevice3**](https://github.com/doronz88/pymobiledevice3) — by far
  the most complete source on `lockdown`, the CoreDevice tunnel, RemoteXPC,
  RSD, image mounting and HID services. Reverse-engineering notes included.
- [**libimobiledevice**](https://libimobiledevice.org/) — the historical
  reference on usbmux, pairing and `lockdownd`.
- [**go-ios**](https://github.com/danielpaulus/go-ios) — a second reading of
  the same layers, invaluable for resolving ambiguities.
- The published specifications of the formats read here: technical note
  [TN1150](https://developer.apple.com/library/archive/technotes/tn/tn1150.html)
  (HFS+), the [xz file format specification](https://tukaani.org/xz/xz-file-format.txt),
  the [LZMA SDK](https://7-zip.org/sdk.html), RFCs 6184, 7798 and 9293.
- Apple's published dimensional drawings, for the chassis geometry.

**No line of their code is reused here.** Everything is rewritten in C# from
the protocol and the specifications — a constraint of the project, not a
claim of priority. Where a reference implementation's behavior was
deliberately reproduced (a quirk of TSS personalization rules, for example),
the comment says so on the spot.

## License

MIT — see [`LICENSE`](LICENSE). The license covers this repository's code and
nothing else: neither Apple's Developer Disk Image nor the Apple Devices
app's multiplexer, which remain subject to Apple's own terms.

**One exception, and it is data, not code**: the AAC-ELD tables in
[`src/LuminaMonitor.Core/Media/Aac/Tables/`](src/LuminaMonitor.Core/Media/Aac/Tables/)
are transcribed from ISO's own freely downloadable reference software and
carry the MPEG software module notice reproduced in
[`NOTICE.md`](src/LuminaMonitor.Core/Media/Aac/Tables/NOTICE.md), not this
repository's MIT terms. AAC-ELD remains covered by active patents (Fraunhofer
IIS, licensed through Via Licensing); this project's use is free and
non-commercial, the situation of any open-source AAC decoder — a commercial
use would need its own patent license.
