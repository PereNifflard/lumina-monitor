**English** · [Français](SECURITY.fr.md)

# Security and privacy

What Lumina Monitor sends, what it writes, what it requires — and what it
doesn't do. Everything below is verifiable in the source: the network and
disk entry points are named explicitly further down.

## What leaves your PC

**One single destination on the Internet**, and only when mounting the
Developer Disk Image:

| What | Where | When |
|---|---|---|
| TSS personalization request | `https://gs.apple.com/TSS/controller?action=2` | on every mount of the Developer Disk Image (so after every phone reboot) |

This is Apple's own mechanism: since iOS 17, the Developer Disk Image can
only be mounted with a ticket (`ApImg4Ticket`) that Apple signs **for a
given device and nonce**. Without this call, no mount, so no control.

**What the request contains** (`src/LuminaMonitor.Core/Ddi/Tss.cs`,
`BuildRequest`):

- `ApECID` — the unique identifier of your iPhone's chip. This is **a
  permanent hardware identifier**; it's also the second half of the UDID.
- `ApBoardID`, `ApChipID`, and the `Ap,*` personalization keys read from the
  phone — board model, chip generation.
- `ApNonce` — a nonce drawn by the phone for this specific mount.
- The fingerprints (`Digest`) of the components marked "Trusted" in the
  image's `BuildManifest.plist`: this tells Apple **which version of the
  Developer Disk Image** you're mounting.
- A random `@UUID` drawn on every request, `@HostPlatformInfo = "mac"`, and a
  client version string (`libauthinstall-1104.0.9`).

**What the request does not contain**: no name, no email address, no device
name, no Windows machine or user name, no serial number, nothing from the
phone's content.

This is the same request Xcode or the Apple Devices app makes for the same
operation. If you don't want it to go out, there is no workaround: mounting
the Developer Disk Image depends on it.

**The TLS connection is pinned.** `gs.apple.com` chains to "Apple Root CA," a
root Windows doesn't ship. Rather than disabling validation (as some
reference tools do), Apple's public root is embedded in the code, checked by
its SHA-1 fingerprint, and the chain must reach it; the rest of TLS
validation (hostname included) stays intact. Revocation isn't checked:
Apple's lists are published on its own hosts, and a failed fetch would block
signing.

**The rest of the traffic never leaves the machine or the cable:**

- `127.0.0.1:27015` — Apple's USB multiplexer, on loopback.
- A user-space TCP/IPv6 stack that talks to the phone **inside the
  CoreDevice tunnel**, itself encapsulated in the USB connection. The
  `fdd0::1`/`fdd0::2` addresses you'll see in the log are internal to that
  tunnel and don't exist on any real network.

There is **no other HTTP client, no other outbound socket, no DNS
resolution** in the project: `HttpClient` is instantiated exactly once, in
`Tss.cs`, and `TcpClient` exactly once, in `UsbmuxClient.cs`.

## What is written to your disk

The application writes only under `%APPDATA%\LuminaMonitor`:

| File | Content |
|---|---|
| `settings.json` | the Developer Disk Image folder, path to the last archive, window position, wheel direction, chassis color. **No secret**: pairing belongs to Apple and is never copied here. |
| `lumina.log`, `lumina.1.log` | the log, kept on every run, rotating at 5 MB. |
| `ddi\<build>\` | your copy of the `Restore/` tree extracted from **your** Apple download. |
| `ddi\extraction\` | the extraction's temporary work area. |

### ⚠️ The log contains device identifiers

`lumina.log` carries, in plain text:

- the iPhone's **UDID** (`usbmux device #N, UDID …`) — whose second half
  **is the ECID**, the chip's permanent hardware identifier;
- the **device name** as set in iOS (often a first name);
- the model, iOS version and build number;
- the process's full command line, so the executable's path, which usually
  contains your Windows username.

The log never leaves on its own — but **read it before attaching it to a bug
report or publishing it**. There's no truncation today: the trade-off is
deliberate, a full UDID being what lets a log be matched to a phone when
several are plugged in.

### The probe, on the other hand, writes images of your screen

`LuminaMonitor.UsbProbe` (`mirror-test`, `clock-test`, `motion-test`,
`latency-test`) writes `.bmp`, `.rtp` and thumbnail folders **to the current
directory**: these are captures of the phone's screen. `.gitignore` blocks
`*.bmp` and `*.rtp`, but a thumbnail folder passed via `--out=` isn't
covered by anything. Don't commit them, don't share them without looking at
them first.

## What the application requires from the phone

- **The phone must be paired** with the PC ("Trust This Computer"). Pairing
  is the one negotiated by the Apple Devices app; this project **reads** it
  to open the TLS session, it doesn't create one, doesn't store one, and
  asks for no passcode.
- **Developer Mode must be enabled** (Settings → Privacy & Security). This
  is a setting that deliberately lowers an iOS protection and requires a
  reboot: only enable it on a device you own and are willing to open up to
  debugging.
- **The Developer Disk Image must be mounted**, and it is, on every phone
  reboot. It brings the control services (HID, display).
- **The screen must be unlocked** at the time of mounting: iOS answers
  `DeviceLocked` otherwise.
- **iOS 27** for touch; on iOS 26 only the buttons work.

In other words: the application bypasses no iOS protection. It walks
through doors that Apple opens, provided the device's owner opened them
themselves, physically, on the device.

## What the application does not do

- **No telemetry**, no usage statistics, no remote crash reporting, no
  update check. The project's only network call is the TSS request
  described above.
- **No third-party code.** Zero `PackageReference` across the four
  `.csproj` files, zero downloaded binary, zero installed driver. The xar,
  pbzx, xz/LZMA2, cpio, UDIF, HFS+ and APFS readers — and the AAC-ELD decoder
  — are written here, from published specifications. The one exception to
  "no third-party code" is not code but data: the AAC-ELD normative tables in
  `Media/Aac/Tables/`, held under the MPEG software module notice rather than
  this repository's MIT terms (see
  [`NOTICE.md`](../src/LuminaMonitor.Core/Media/Aac/Tables/NOTICE.md)).
- **No redistribution of Apple binaries.** No Developer Disk Image, no
  firmware, no Xcode component lives in this repository, and `.gitignore`
  blocks `/ddi*/`, `*.xip`, `*.dmg` and `*.pkg`. Everyone extracts the image
  from their own download, with their own Apple account.
- **Never stops the Apple process.** `AppleMobileDeviceProcess.exe` holds
  the USB interface and the pairing records: killing it would break the
  user's pairing.
- **Reads nothing from the phone's content.** The application receives a
  video stream of the screen and sends HID reports. It opens no file system
  on the phone, lists no app, extracts no data.

### Two acknowledged exceptions to "nothing Apple in the repository"

1. **Apple's public root certificate**, in
   `src/LuminaMonitor.Core/Ddi/Tss.cs`. This is the
   `AppleIncRootCertificate.cer` file published by Apple at
   <https://www.apple.com/certificateauthority/>, meant by its very nature
   to be distributed and verified: a root certificate only makes sense
   public. It contains no secret — the private key stays with Apple — and
   its fingerprint is checked on load.
2. **197 bytes of media negotiation template**, in
   `src/LuminaMonitor.Core/Media/MediaOffer.cs` (`SelfCheck`). These are the
   bytes of **a protocol message** captured off the wire, kept purely as a
   test fixture: the offer builder must reproduce them byte for byte. This
   isn't Apple's code, nor an extract of a binary: it's interoperability
   data, of the same kind as a port number or a format header.

## Attack surface, plainly

- The application opens a **hand-written TCP/IPv6 stack** that parses what
  the phone sends, as well as format readers (UDIF, HFS+, APFS, xar, xz)
  that parse files the user chooses. This is managed C# code, but it uses
  `AllowUnsafeBlocks` for image conversion; a malicious archive file or a
  hostile peer is a plausible attack vector. **Only open Apple archives
  downloaded from your own developer account.**
- Video decoding goes through **Media Foundation**, Windows's H.264
  decoder.
- The application **does not need administrator rights** and should not be
  launched with them.

## Reporting a vulnerability

Open a **GitHub issue** describing the problem, or — if it allows attacking
a device or a user — use GitHub's **private vulnerability report** (the
*Security* tab → *Report a vulnerability*) instead of a public issue, and
allow a reasonable delay before disclosure.

Please **do not attach `lumina.log` as-is**: read it first, or strip out the
`UDID …` line and the device name.
