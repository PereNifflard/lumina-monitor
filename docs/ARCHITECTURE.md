# Architecture — driving an iPhone over USB from Windows, without third-party code

**English** · [Français](ARCHITECTURE.fr.md)

Verdict of the feasibility study from 8 September 2026 (15 agents, primary sources:
pymobiledevice3 HEAD 1ee3dd9, libimobiledevice, go-ios, Apple docs), cross-checked
with what is already **proven on the project machine**.

## The stack, from cable to tap

| # | Layer | What it is | Status | Difficulty |
|---|---|---|---|---|
| 1 | usbmux | Apple's multiplexer (TCP 127.0.0.1:27015, XML plist, reversed port) | **done, proven** | easy |
| 2 | lockdown | identity, StartSession, mutual TLS with the reused pairing, StartService | **done, proven** | easy |
| 3 | Developer Mode | service `com.apple.amfi.lockdown`, action 0/1/2 | **done** (reveal proven) | easy |
| 4 | DDI | mounting the personalised developer image (`mobile_image_mounter`, Apple TSS) | **done, proven** (17B48, then Xcode 27 beta 6 mounted) | medium, fragile |
| 5 | tunnel | `CoreDeviceProxy` via lockdown (iOS 17.4+): CDTunnel + raw IPv6 packets | **done, proven** | medium |
| 6 | IP stack | user-space TCP/IPv6, client-only, single peer | **done, proven** | medium |
| 7 | RSD + RemoteXPC | directory (port 58783) + minimal HTTP/2 + XPC codec | **done, proven** | medium |
| 8 | media gate | mandatory DisplayService stream, otherwise HID reports are ignored | written, proven in dialogue, refused by iOS 26 → iOS 27 required | hard, locked (iOS 27) |
| 9 | HID | `universalhidservice`: surface 257, 58-byte report, absolute 0..65535; `indigo`: buttons, Consumer page 0x0C | buttons PROVEN on iOS 26; absolute touch pending iOS 27 | easy (buttons done; touch: iOS 27) |

## The discovery that changes everything: CoreDeviceProxy

On iOS 17.4+, lockdown exposes `com.apple.internal.devicecompute.CoreDeviceProxy`.
The CoreDevice tunnel opens **inside our existing lockdown TLS session**, with no
prompt, and **without** remote pairing (SRP, Ed25519, X25519), **without** QUIC,
**without** TLS-PSK — precisely the four pieces .NET does not ship out of the box
and that would otherwise have had to be written by hand. Handshake: `CDTunnel` +
16-bit BE length + JSON `{"type":"clientHandshakeRequest","mtu":16000}` → the
phone's IPv6 address + RSD port; then length-prefixed IPv6 packets.

Everything above that is pure code: a small client TCP stack, an HTTP/2 reduced to
two streams, a fully specified XPC codec (magic 0x29b00b92 / 0x42133742, version 5,
type tags), dictionaries.

## RemoteXPC: the reply channel is not the root stream

Lesson learned tonight while talking to the media and HID gates: the CoreDevice
daemons reply on **HTTP/2 stream #3** (the reply channel), not on the root stream
the request went out on. The read pump now accepts any non-empty XPC dictionary
regardless of stream number, the way the reference implementation does.

## The two real walls — and they are not cryptographic

**1. The developer image (DDI) — wall fell on 8 September.** The HID daemon
(`dtuhidd`) lives *inside* Apple's developer image: with no image mounted, the
service does not exist. The chain to obtain and mount it is now written and
verified on a real phone, with home-grown readers end to end (UDIF, HFS+, APFS,
xar, pbzx/xz, cpio):

- Extraction: the **three** forms Apple distributes all go into
  `extract-devsupport <xip|dmg|pkg> [folder]` — the full Xcode archive
  (`.xip`, ~2 GB), the "Device Support" component (UDIF/HFS+ DMG), or
  `XcodeSystemResources.pkg` read directly as xar. The format is recognised from
  the first four bytes and the archive's table of contents: `xar!` + a `Content`
  entry = Xcode, `xar!` + a `Payload` entry = package, anything else is tried as a
  disk image. For a `.xip`, `DdiExtractor.CopyFromXcodeArchive` does an xar →
  pbzx/xz → cpio pass down to
  `*/Contents/Resources/Packages/XcodeSystemResources.pkg` (the app name varies:
  `Xcode.app` or `Xcode-beta.app`), writes that package (~145 MB) to a temporary
  folder — a xar seeks into its heap, a cpio stream cannot rewind — and the
  existing chain resumes from there. About a minute, ~4 GB scanned, progress
  reported per gigabyte.
- Then, from the package: `Payload` (pbzx/xz, cpio) →
  `Library/Developer/CoreDevice/CandidateDDIs/iOS_DDI.dmg` (UDIF/HFS+) → a
  faithful copy of `/Restore/` (`BuildManifest.plist`, `Restore.plist`, two `.dmg`
  images — one "PersonalizedDMG", one a cryptex —, `Firmware/*.dmg.trustcache`
  + `.cryptex_info`/`.root_hash`). `extract-xip` pulls a named entry out of the
  archive (same pass, exposed for tooling), `grep-xip` searches for strings across
  the whole xip in one pass (~2 min for 9.4 GB).
- Named failures, because that's the first thing a new user hits: a file that is
  none of the three formats, a truncated archive (the xz checksums say so), the
  resources package missing from the archive.
- Image/trustcache selection: never by extension. `mount` reads the phone's
  BuildIdentity (ApBoardID/ApChipID — e.g. iPhone 17 Pro Max = v54ap, 0x0E/0x8150)
  and takes `Manifest.PersonalizedDMG.Info.Path` /
  `Manifest.LoadableTrustCache.Info.Path` from `BuildManifest.plist`.
- Pinned TSS signature: `gs.apple.com` chains to a private root ("Apple Root CA",
  SHA-1 `611E5B662C593A08FF58D14AE22452D198DF6C60`), absent from Windows'
  certificate stores. The official root (apple.com/certificateauthority,
  fingerprint checked against the server's) is embedded in `Tss.cs` and validated
  in CustomRootTrust mode — TLS validation stays active, only the trusted root
  changes. `ApImg4Ticket` received: 3045 bytes.
- Mount daemon: `com.apple.mobile.mobile_image_mounter` serves only one client at
  a time — the `QueryPersonalizationManifest` connection must be closed before
  opening another one, and nonce + TSS + `ReceiveBytes` + `MountImage` all have to
  fit on the next connection, alone. `ReceiveBytes` replies `DeviceLocked` (and
  hangs up) while the screen is locked: `mount` retries every 3 s for up to
  10 min, a fresh connection on each attempt. One image at a time: `mount`
  unmounts whatever is already there first, and waits for the unlock at that step
  too. Fragility observed: an image upload froze once right after an
  `UnmountImage` on the same connection; mitigation added (30 s write timeout,
  progress logged per megabyte) and confirmed by retrying on a fresh connection.

Result: image **17B48** mounted on 08/09/2026 at 20:32 (the first DDI mounted by
this code), RSD directory grew from 61 to 72 services. But neither 17B48
(Xcode 26.0.x) nor 17F113 (Xcode 26.6, `ddi26/`) ship `dtuhidd` or
`com.apple.coredevice.displayservice`: those daemons arrive with the
**Xcode 27 / iOS 27** tooling (reference DDI 27A5228h, beta 6 = 27A5252f from
24/08/2026). (The MobileAsset/Pallas channel, probed in parallel, does not
reference any known DDI asset type — a dead end for now.)

**Continued, that same evening (21:11): wall 1 completely fell.** Xcode 27 beta 6
(27A5252f) DDI mounted on the iPhone 17 Pro Max (iOS 26.6.1), extracted from
`Xcode_27_beta_6.xip` (nested package
`Xcode-beta.app/Contents/Resources/Packages/XcodeSystemResources.pkg`, 145 MB)
into `ddi27/`. The RSD directory grows to 82 services, including
`com.apple.coredevice.hid.universalhidservice`, `com.apple.coredevice.hid.indigo`,
`com.apple.coredevice.hid.universalhid`, `com.apple.coredevice.displayservice`
and `com.apple.coredevice.screencaptureservice` — confirmed via `catalogue`.
The HID daemon is reachable: `connectedServices` returns surfaces 257
(CoreDevice touchscreen, Built-In), 512 (CoreDevice keyboard, already
registered), 1026 (`mainScreenButtons`, `Authenticated = true`) and 1280
(`avpCustom`). The 58-byte touch report sent to 257 is accepted with no error
(fire-and-forget) — but accepted does not mean authenticated: see wall 2, just
below.

**2. The media gate — talking, but locked by iOS.** With the Xcode 27 DDI
mounted, the media gate finally responds: `startmediastream` (the service that
authenticates the touchscreen) returns `CoreDeviceError 9021`: "Remote
control requires iOS 27.0 or later on this device." On iOS 26.6.1, the touch
surface (257) therefore stays unauthenticated and any HID report sent to it is
ignored with no error — exactly the silent-failure mode we anticipated. Absolute
touch therefore requires **iOS 27**, whose public release is expected mid-September
2026 (installing the developer beta in the meantime).

Correction: the initial feasibility study placed this mechanism between iOS 17
and iOS 26 — wrongly: the reference had actually been recorded from **iOS 27**
betas.

Diagnostic: `tap` keeps working with no video stream if the media gate is
refused (useful to observe whether reports get through anyway), and only calls
`stopmediastream` if the stream actually started.

Reversal that still holds from iOS 27 on: this mandatory video stream **is** a
USB screen mirror. If it decodes (the study flags non-conformant HEVC that only
Apple's own decoder renders cleanly), it replaces AirPlay + the Pi for the picture
too.

## Required iOS version

- **Buttons** (Indigo gate): iOS 26 and up — proven on iOS 26.6.1.
- **Absolute touch** (touchscreen, media gate): iOS 27 and up — refused on
  iOS 26.6.1 (`CoreDeviceError 9021`).
- **Developer image (DDI)** carrying `dtuhidd` / `universalhidservice` /
  `displayservice`: Xcode 27 (beta 6 = 27A5252f); the Xcode 26.0.x (17B48) and
  Xcode 26.6 (17F113) DDIs lack them.

## Latency

Per event: excellent (XPC reports with no reply expected, link RTT ~7 ms, below
the 10-30 ms of a Bluetooth HID). The cost is at setup (tunnel + mount + media
stream, seconds) — we keep a warm session.

## HID: report format and buttons (pymobiledevice3 hid_service.py; live Indigo dialogue)

- **Buttons (`com.apple.coredevice.hid.indigo` gate), PROVEN on iOS 26.6.1**:
  message `{messageType: "IndigoButtonEvent", payload: {state, usagePage,
  usageCode} (UInt64), featureIdentifier:
  "com.apple.coredevice.feature.remote.hid.button"}`, Consumer page `0x0C`:
  home `0x40` (50 ms press), lock `0x30` (500 ms), volume-up `0xE9`,
  volume-down `0xEA`, mute `0xE2`, siri `0xCF` (1 s). No video stream needed.
  Confirmed in practice (volume went up, the home button returns to the home
  screen) — the first real command sent to the phone from the PC in home-grown
  code. Probe command: `button <home|lock|volume-up|volume-down|mute|siri> [...]`.
- Absolute touch, surface `257`: 58 bytes, `[0]=0x09`, `[1]=0x01 [2]=0x05`,
  `[3]=0xC2` down / `0x02` up, `[4..5]` X, `[6..7]` Y (UInt16 LE, 0..65535
  normalised, origin top-left), `[44..49]` 48-bit timestamp. Accepted by the
  daemon with no error (wall 1) but with no effect while the surface is not
  authenticated (wall 2, iOS 27 required).
- Keyboard: `createService` for a virtual keyboard (strict Swift-Codable typing,
  the most fragile part), then 30-byte bitmap reports.
- Send: `{"featureIdentifier":"com.apple.coredevice.feature.remote.universalhidservice",
  "messageType":"Request","payload":{"send":{"_0":<DATA>,"_1":<UINT64 surface>}}}`
  with no reply expected.

## Honest sizing

Layers 4 to 9: several weeks of actual work, each layer verifiable on its own
(layer 4 fell on 8 September within the day, the layer 9 buttons that same
evening — see above). Fragility: report format and media gate recorded by
capture (to be re-checked with each major iOS version); DDI image per major
version; TSS signature online.

## What was ruled out

- QUIC tunnel (iOS 17.0-18.1): `System.Net.Quic` does not expose datagrams,
  msquic.dll is not built in → impossible without a third-party binary. Moot:
  18.2+ uses TCP, and CoreDeviceProxy bypasses all of it.
- Raw USB (WinUSB): would require **replacing** Apple's driver with a generic
  third-party one — against the project's rule, and far harder anyway.
- The Bluetooth route: see README (two walls confirmed).

## Credits and sources

The protocol described in this document was not guessed. Apple does not
document it; what is known here comes from the community, which published its
findings:

- [**pymobiledevice3**](https://github.com/doronz88/pymobiledevice3) (HEAD
  1ee3dd9 at the time of the study) — the main source for layers 2 to 9:
  `lockdown`, `mobile_image_mounter` and TSS, `CoreDeviceProxy` and the tunnel,
  RSD, RemoteXPC, `hid_service.py` for the HID report format.
- [**libimobiledevice**](https://libimobiledevice.org/) — usbmux, pairing,
  `lockdownd`: the oldest and best-proven description.
- [**go-ios**](https://github.com/danielpaulus/go-ios) — a second reading of
  the same layers.
- Public Apple documentation: Developer Mode, dimensioned device drawings,
  technical note TN1150 (HFS+).
- Format specifications: xz, LZMA2/7-Zip SDK, xar, cpio, UDIF; RFC 6184
  (H.264 FU-A), RFC 7798 (HEVC FU), RFC 9293 (TCP).

What this repository contributes is a **C# reimplementation**, with no
third-party code, and the findings made on a real device that are recorded in
the tables above (the iOS 26/27 walls, the mandatory media gate, the display
service's behaviour after a drop). No line from the projects above is reused
here — and none of the above would have been found without them.
