# LuminaMonitor.Core — design (stage 1)

**English** · [Français](CORE_DESIGN.fr.md)

Goal: pull out of the probe (`src/LuminaMonitor.UsbProbe`) a library reusable by
the desktop app, without changing anything in the behaviour proven on the phone
on 8 September 2026. Project rule unchanged: no third-party code, Apple's
protocol reimplemented in C#.

## Projects

| Project | Target | Role |
|---|---|---|
| `LuminaMonitor.Formats` | net8.0 | xar / pbzx / cpio / UDIF / HFS+ / APFS readers (existing) |
| `LuminaMonitor.Core` | net8.0 | **new**: the entire protocol + the `DeviceSession` facade |
| `LuminaMonitor.UsbProbe` | net8.0-windows | diagnostic console, now only makes calls into Core |

`Core` references `Formats`. The probe references `Core`. `InternalsVisibleTo("LuminaMonitor.UsbProbe")`
on Core so the low-level commands (`session`, `catalogue`, `tunnel`…) keep access to the building blocks.

## Breakdown of Core (folders = namespaces)

- `Usb/`: `Plist`, `UsbmuxClient`, `LockdownClient` + `PlistService`, `PairRecord` — moved as-is.
- `Tunnel/`: `CdTunnel`, `Ipv6`, `TunnelNet`, `TcpConnection` — moved as-is.
- `RemoteXpc/`: `Xpc`, `RemoteXpc`, `Rsd`, `CoreDevice` (+ `DisplayService`) — moved as-is.
- `Ddi/`: `ImageMounter` (+ `.Mount`), `Tss`, and **new** `DdiManager`.
- `Media/`: `MediaOffer`, `Bplist`, and **new** `MediaSession`.
- `Hid/`: `Hid`, `IndigoHid`, and **new** `InputInjector`.
- root: **new** `DeviceSession` (facade), `SessionState`, `DeviceInfo`, `LuminaException`.

Moving = plain file moves (the repository has no commit yet),
namespace `LuminaMonitor.Core.<Folder>`, `internal` visibility kept except for the types listed
under "Public surface". No rewriting of the building blocks: they are proven.

## Public surface (what the desktop app will see)

```csharp
public sealed class DeviceSession : IAsyncDisposable
{
    public DeviceSession(DdiSource ddi, ILog? log = null);
    public SessionState State { get; }                 // Detached, Attached, Paired, TunnelUp, DdiMounted, MediaUp, Resetting, Faulted
    public DeviceInfo? Device { get; }                 // Udid, Name, ProductType, ProductVersion, BuildVersion
    public event Action<SessionState>? StateChanged;
    public event Action<string>? UnlockRequired;       // the phone is locked: ask the person
    public event Action<string>? RestartRequired;      // three soft resets with no success: "Restart the iPhone"
    public event Action<ReadOnlyMemory<byte>>? RtpPacket;   // relayed from MediaSession (stage 2 will use it)
    public Task ConnectAsync(CancellationToken ct);    // chains: usbmux → lockdown TLS → tunnel → RSD → DDI → media
    public Task DisconnectAsync();
    public InputInjector Input { get; }                // valid once State >= MediaUp (buttons as soon as TunnelUp+DDI)
    public Task<bool> RebuildInputAsync();             // reopens the sole HID channel; false ⇒ the session goes Faulted

    // Screen: "asleep" is only known if this session is the one that put it to sleep.
    public bool ScreenAsleep { get; }
    public event Action<bool>? ScreenSleepChanged;
    public Task SleepScreenAsync();                    // "lock" (Consumer 0x30, 500 ms); the stream watchdog is suspended
    public Task WakeScreenAsync();                     // "home" (Consumer 0x40) — 0x30 is NOT a toggle, measured; rearms the watchdog.
                                                       // UNLOCKING remains Face ID or the passcode

    // Phone clipboard (com.apple.coredevice.pasteboardservice): service opened on demand, closed afterwards.
    public Task<string?> ReadPhoneClipboardAsync();
    public Task<ClipboardContent> ReadPhoneClipboardSnapshotAsync();   // kind + UTI + size: an image is not "empty"
    public Task WritePhoneClipboardAsync(string text);
}

public enum ClipboardKind { Nothing, Text, Image, Data }
public readonly record struct ClipboardContent(ClipboardKind Kind, string? Text, string? Type, int Bytes);

public sealed record DdiSource(string Folder);         // "copy of Restore/" folder (ddi27); extraction stays a separate command

public sealed class InputInjector
{
    public Task TapAsync(double xNorm, double yNorm, int holdMs = 60);        // 0..1, absolute
    public Task TouchDownAsync(double xNorm, double yNorm);
    public Task TouchMoveAsync(double xNorm, double yNorm);
    public Task TouchUpAsync(double xNorm, double yNorm);
    public Task DragAsync(double x1, double y1, double x2, double y2, int durationMs, int steps = 20);
    public Task PressButtonAsync(string name);                                 // home, lock, volume-up, volume-down, mute, siri
    public Task TypeAsync(string text);                                        // keyboard surface 512: 39-byte report per keystroke (Hid.KeyboardReport)
}
```

`ILog`: `void Info(string)`, `void Warn(string)`; console implementation in the probe.

## DdiManager (Ddi/)

- `Task<DdiStatus> EnsureMountedAsync(DdiSource, LockdownClient/pair record, IProgress<string>?, CancellationToken)`.
- Logic = the current `mount` command, unchanged, with one addition: first read `CopyDevices`;
  if a `Personalized` image is mounted and its `ImageSignature` (48 bytes) **equals the SHA-384 of
  our image**, do nothing (already mounted). Otherwise unmount then mount (nonce, pinned TSS, upload, MountImage).
- Waiting for the unlock (`DeviceLocked`): kept, but made observable: fires `UnlockRequired`
  then retries every 3 s, capped at 10 min, a fresh connection on every attempt.
- Image/trustcache resolution via `BuildManifest.plist` (the phone's BuildIdentity): unchanged.

## MediaSession (Media/)

- Opens `com.apple.coredevice.displayservice`, listens on UDP over the tunnel stack, sends
  `startmediastream` (current video offer), exposes `RtpPacket`, `Codec`/`StreamConfig` (the raw
  reply dictionary, for stage 2).
- `StopAsync`: shutdown hygiene, in this order — stop the report loop, **RTCP BYE** (PT 203),
  `stopmediastream`, then give the phone **1 s to hang up** before we close ourselves
  (TCP FIN, never RST). Duration logged. Hypothesis: the abrupt close was what eventually froze
  the display daemon after a handful of sessions. Same treatment for the HID channels
  (`InputInjector.CloseAsync`).
- Must stay open for the whole duration of touch injection (Apple's media gate).

## Write deadline for the TCP stack (Tunnel/)

- `TcpConnection.WritePatience`: how long a write is allowed to sit parked on the phone's
  receive window. **Unlimited by default** (RSD directory, media offer, CoreDevice invocations:
  a transfer that gives up is a session that fails to open); **1 s** for the input channels
  (`InputInjector.ChannelPatience`, passed through `Rsd.OpenAsync(…, writePatience:)` on the
  universal HID and Indigo channels). The wait happens **outside** `_gate` and outside the tunnel's
  send lock, which the video stream shares. Beyond that: `TimeoutException`, the connection is
  marked faulted, and `RemoteXpc`'s `_writeLock` is released by its `finally`. That last point was
  the missing piece: the 500 ms ceiling on the input pump
  (`InputInjector.SendPatience`) drops the report but was not releasing the lock underneath it,
  so everything queued behind it stayed parked — the multi-second delay felt on click.
- A window smaller than what is already in flight: the comparison happens **before** the
  subtraction (both operands are unsigned; otherwise the difference wrapped around to four
  billion bytes of "free room").
- **No exception is ever pushed into a `TaskCompletionSource`** (`TcpConnection`, `RemoteXpc`):
  the one nobody is waiting on carries its fault all the way to the finalizer, which raises it
  as an "unobserved exception" of the process minutes later (`RST received from port 64006`,
  `Unable to read beyond the end of the stream` in the log). The reason is kept in a field, the
  waiters are woken up empty-handed, and the next call (`ReadAsync`, `WriteAsync`, `SendDataAsync`)
  raises it in someone's hand. Only the `_inbound` channel keeps its own reason: the runtime itself
  observes a `Channel`'s completion fault. `TunnelNet.FireAndForget` reads the fault **before**
  deciding whether it has anything to say.
- Replayed offline: `LuminaMonitor.UsbProbe tcp-selftest` (`TunnelTools`) runs the stack against a
  paper phone — two in-memory pipes, a minimal peer — and checks the ~1 s give-up, the lock release
  (the next send goes through immediately), and that no unobserved exception reaches the finalizer
  (`GC.Collect` + `WaitForPendingFinalizers`).

## Stream watchdog and soft reset

- `StreamWatchdog` (Media/): the ladder, with no clock — each entry takes the instant as an
  argument, so it replays offline (`LuminaMonitor.UsbProbe watchdog-selftest`). **3 s** of silence
  → request a key frame (PLI); **2 s** grace → restart the media session (without redoing the
  tunnel or the directory); **2 failed restarts in a row** → soft reset.
- `MediaSession` holds the watchdog (500 ms heartbeat on the packet counter) and publishes
  `StreamStalled`; `DeviceSession` decides and executes. `StartWatch()` is only armed once
  `MediaUp` is reached.
- Soft reset (`DeviceSession`): `UnmountAllAsync` then a full remount — unmounting restarts the
  daemons carried by the image, the only remedy found for a mute display service (unplugging the
  cable is not enough: the image stays mounted). Refused with the screen locked → `UnlockRequired`
  and a new attempt every 3 s, capped at 10 min. State `Resetting` during the operation.
- Same reset after **two mute openings in a row** of the display service ("no SETTINGS in 3 s",
  or an immediate `IOException`). Beyond **3 resets** with the stream not restored: `RestartRequired`.

## DeviceSession.ConnectAsync — sequence

1. `UsbmuxClient`: first device; `ReadPairRecord` (Apple already paired it). Otherwise `LuminaException("Device not paired…")`.
2. `LockdownClient` + `StartSessionAsync(record)`; `DeviceInfo` via `GetValue`.
3. Developer Mode: if the RSD directory (step 5) does not have `CoreDeviceProxy`… no: check via `amfi` like the `devmode status` command does. Refused → `Faulted` with a clear message.
4. `DdiManager.EnsureMountedAsync` (before the tunnel: the directory depends on the mounted image).
5. `CdTunnel` → `TunnelNet` → `Rsd.LoadAsync`. Check `Hid.ServiceName` is present, otherwise `Faulted`.
6. `MediaSession.StartAsync` → `MediaUp`. `InputInjector` ready.
Each step publishes `StateChanged`. Any exception → `Faulted` + clean release (tunnel, sockets).

## The probe after restructuring

`Program.cs` keeps all its commands; `mount`, `tap`, `button` are rewritten on top of
`DeviceSession`/`DdiManager` (same visible output). The other commands use the relocated internal
building blocks (via InternalsVisibleTo). `XipGrep`, `OpenPayload` and the extraction commands stay
in the probe (tooling), except `extract-devsupport` whose core becomes `Ddi/DdiExtractor` (Core)
called by the probe — the desktop app will need it.

## Acceptance criteria

- `dotnet build`: 0 warnings, 0 errors across the three projects.
- Regression on the phone: `catalogue` (directory), `mount ddi27` (must answer "already mounted"
  with nothing sent back, identical image), `tap 39 95` (opens the 2nd dock app), `button home`.
- No change on the wire, byte for byte: same messages, same order, same delays.

## Gaps found during implementation (8 September 2026)

- `LuminaMonitor.Core.Hid` / `LuminaMonitor.Core.RemoteXpc` share the name of the classes
  `Hid` and `RemoteXpc`: outside their own folder the name resolves to the namespace, hence
  the aliases `HidReports` and `XpcService` in the files that cross them.
- `DdiManager`: the constructor takes `(long deviceId, PairRecord record)` — it reopens a
  fresh connection on every attempt — and `EnsureMountedAsync(DdiSource, IProgress<string>?, CancellationToken)`
  returns a `DdiStatus` (`AlreadyMounted`, `Mounted`, plus the refusals, which the probe translates
  into exit codes).
- `MediaSession` does not expose a separate `Codec`: the raw reply dictionary is `StreamConfig`,
  the codec will be read from it at stage 2.
- `InputInjector` receives the RSD directory and the HID channel; the Indigo channel is only opened
  on the first button press.
- `DeviceSession.ConnectAsync` publishes `DdiMounted` before `TunnelUp` (the directory depends on
  the mounted image), so `SessionState` is not monotonic in declaration order.
- The keyboard surface is indeed 512 (`_ServiceID` of the "CoreDevice keyboard" service, checked on
  the phone), and a mounted entry's signature is indeed called `ImageSignature`: `ImageMounter`
  unchanged.
- `tap` and `button` now go through the whole staircase (Developer Mode check, `CopyDevices`,
  media stream); `button` gains the media stream it did not use to open. Their default DDI folder
  is `ddi27`, `tap` accepts a third argument to change it.
- `LuminaMonitor.Formats.csproj` gains `InternalsVisibleTo("LuminaMonitor.Core")`: `DdiExtractor`
  needs `UdifImage`, `UdifBlockDevice`, `HfsVolume` and `ApfsVolume`, which are internal.
- `DdiExtractor.Extract` returns a `Result` (folder, whether the manifest is present and readable,
  version, number of BuildIdentities); the last three lines of `extract-devsupport` are still
  printed by the probe.

## Gaps found during implementation (9 September 2026)

- `DdiExtractor` also accepts the raw `.xip`: the form is recognised from the `xar!` magic **and**
  the table of contents (`Content` = Xcode archive, `Payload` = package), plus the disk image as a
  last resort. `CopyFromXcodeArchive(xip, suffix, target, IProgress<string>)` is public: it is the
  xar → pbzx/xz → cpio pass that `extract-xip` used, hoisted into Core so it is not written twice.
  The resources package lands in `%TEMP%\LuminaMonitor-xcode-<8 hex>` and is deleted in all cases —
  a cpio stream cannot rewind, a xar seeks into its heap, so a file is required.
- Extraction failures raise `LuminaException` with the remedy inside: unexpected file, incomplete
  or corrupted archive (the xz checksums), missing `XcodeSystemResources.pkg`, file too short.
- `UsbmuxClient.ConnectAsync` distinguishes the two ways Apple's multiplexer can be missing:
  connection **refused** (nothing is listening → "open the Apple Devices app or plug in the
  iPhone") and a connection that **times out** after 5 s (the SYN goes out, nothing comes back →
  "restart the Apple Devices app"). Without this deadline, Windows would retransmit the SYN for
  about twenty seconds. `LuminaException` carries `AppleMultiplexer` so the window can offer the
  "Open Apple Devices" button. The Apple process is never stopped: it holds the USB interface and
  the pairing records.
