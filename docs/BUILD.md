**English** · [Français](BUILD.fr.md)

# Building it yourself

To install the downloaded release instead of building, see
[`INSTALLATION.md`](INSTALLATION.md).

## What you need

The **.NET 8 SDK** ([dotnet.microsoft.com](https://dotnet.microsoft.com/download/dotnet/8.0)),
and nothing else. No NuGet, no package restore, no external tool: the
repository depends on no third-party library, and `dotnet restore` has
literally nothing to download.

Visual Studio isn't required; the solution opens in it if you have one.

## Building

```
dotnet build LuminaMonitor.sln -c Release
```

The repository is held to **zero warnings**; continuous integration builds
with `-warnaserror` to keep it that way. Do the same before proposing a
change:

```
dotnet build LuminaMonitor.sln -c Release -warnaserror
```

The executable lands at
`src\LuminaMonitor.App\bin\Release\net8.0-windows\LuminaMonitor.App.exe`, and
the probe next to it, in
`src\LuminaMonitor.UsbProbe\bin\Release\net8.0-windows\`.

To produce the same self-contained executable as the Releases — a single
`.exe`, no runtime for the user to install:

```
dotnet publish src/LuminaMonitor.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

It weighs in at around a hundred megabytes: WPF and the .NET runtime travel
inside it.

## The four projects

| Project | Target | In one sentence |
|---|---|---|
| `src/LuminaMonitor.Formats` | `net8.0` | Readers for the formats Apple packs the Developer Disk Image into: xar (XIP), pbzx, xz/LZMA2, cpio, UDIF (DMG), HFS+ and APFS — all written here, from the published specifications, because Windows reads none of them natively. |
| `src/LuminaMonitor.Core` | `net8.0`, Windows only | Apple's protocol itself: usbmux, lockdown over mutual TLS, Developer Mode, image mounting, the CoreDevice tunnel, a user-space TCP/IPv6 stack, RemoteXPC, the video stream and HID surfaces — everything the window sees boils down to `DeviceSession`. |
| `src/LuminaMonitor.App` | `net8.0-windows`, WPF | The window: the drawn chassis, the mirror, translating mouse gestures into a finger on the glass, the keyboard, the counters and the log. |
| `src/LuminaMonitor.UsbProbe` | `net8.0-windows`, console | The diagnostic probe: every rung of the USB ladder isolated into a command, plus the offline self-tests. |

`Core` and `Formats` expose their internal building blocks to the probe via
`InternalsVisibleTo`: it's the probe that attacks them directly, not the app.

## The offline self-tests

They need **neither an iPhone nor a network**, and return a non-zero exit
code when a scenario fails. These are exactly the ones continuous
integration runs.

```
dotnet run --project src/LuminaMonitor.UsbProbe -- formats-check
dotnet run --project src/LuminaMonitor.UsbProbe -- offer-check
dotnet run --project src/LuminaMonitor.UsbProbe -- sps-selftest
dotnet run --project src/LuminaMonitor.UsbProbe -- watchdog-selftest
dotnet run --project src/LuminaMonitor.UsbProbe -- tcp-selftest
dotnet run --project src/LuminaMonitor.UsbProbe -- mf-selftest
```

| Command | What it proves |
|---|---|
| `formats-check` | The xar → pbzx → cpio chain reads back an archive built for the occasion, byte for byte. |
| `offer-check` | The media offer built by the app is identical to the Xcode template, and the tuning knobs really do land on the wire. |
| `sps-selftest` | The H.264 SPS rewrite is read back and replayed: a wrong bit doesn't give a wrong image, it gives zero image. |
| `watchdog-selftest` | The stream watchdog's ladder — keyframe, restart, soft reset — exercised on invented timestamps, since it only runs for real once everything is already broken. |
| `tcp-selftest` | The tunnel's TCP stack against a paper phone: the only place where the receive window closes on demand. |
| `mf-selftest` | Media Foundation's H.264 decoder instantiates and accepts the input and output media types: the COM interop holds. |

Two more also run without a phone: `decode-capture <file.rtp>` replays a
capture and writes one frame as a BMP, `mouse-flood <seconds>` floods the
app's window with synthetic mouse movement.

## Probe commands, with the iPhone plugged in

**One at a time, and never while the app is running**: the phone's display
service serves only one session.

| Command | What it's for |
|---|---|
| `list` | Devices seen by the Apple multiplexer. The first thing to check: if nothing shows up here, nothing higher up will work either. |
| `info` | Identity keys read by lockdown, without pairing. Tells you the iOS version. |
| `session` | Opens the TLS session with the pairing record Apple stored. The authentication rung. |
| `pair <udid>` | Reads that pairing record, without writing anything to it. |
| `buid` | The multiplexer's host identifier. |
| `devmode reveal` / `enable` | Reveals, then enables, Developer Mode (the phone reboots). |
| `ddi` | The personalization identifiers and nonce Apple's signing server asks for. |
| `mount <folder>` / `unmount` | Mounts, or unmounts, the Developer Disk Image. The fix for a frozen mirror. |
| `tunnel` | Opens the CoreDevice tunnel and waits for a first IPv6 packet. |
| `rsd` | The RSD service and its raw catalog. |
| `catalogue` | The list of RemoteXPC services the phone offers — useful when a service changes name between iOS versions. |
| `ping [n]` | ICMPv6 through the tunnel: what it measures is the cable and the daemon, nothing above. |
| `stream-info [codec] [sec]` | Which codec bank the phone answers with, and what the RTP actually carries. |
| `mirror-test [sec] [output.bmp]` | The live mirror, written out as still frames. |
| `clock-test [sec]` | Absolute delay, measured against a clock running on the phone's screen. |
| `motion-test [sec]` | Does the mirror drop frames when the screen is moving? |
| `latency-test [n]` | End-to-end latency, measured on the pixels. |
| `tap <x%> <y%>` | A real touch, exercising the whole stack. |
| `keys <text>` | Types a line on the virtual keyboard. |
| `button <home\|lock\|volume-up\|volume-down\|mute\|siri>` | Presses the real chassis buttons. |
| `flood [sec]` | Loads the input path and measures what gets through. |
| `listen [sec]` | Watches connects and disconnects as they happen. |

## Extracting the Developer Disk Image from the command line

The app does this on first launch; the probe does the same thing without a
window:

```
dotnet run --project src/LuminaMonitor.UsbProbe -- extract-devsupport "C:\path\to\Xcode_27_beta_6.xip" ddi27
```

Around it, tools to look inside archives without extracting anything:
`list-xip`, `grep-xip <pattern>`, `extract-xip <pattern>` and `inspect-dmg
<image.dmg> [pattern]`.

## Continuous integration

- `.github/workflows/build.yml` — on every push and every pull request
  against `main`: a Release build with `-warnaserror`, then the six offline
  self-tests, each expected to return 0. The compiled app is uploaded as an
  artifact.
- `.github/workflows/release.yml` — on a `v*` tag: publishes the two
  self-contained executables, reruns the self-tests **from the packaged
  binary**, then attaches `LuminaMonitor-<version>-win-x64.zip` to a GitHub
  Release created with `gh`. No third-party action is involved.
