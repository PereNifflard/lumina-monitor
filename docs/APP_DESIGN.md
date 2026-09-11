# Control window — design (stage 3)

**English** · [Français](APP_DESIGN.fr.md)

Base: the WPF app from an earlier project by the same author, which showed the same chassis but
went through a Raspberry Pi acting as a Bluetooth bridge. It has been carried over into
`src/LuminaMonitor.App` and rewired onto `LuminaMonitor.Core`. Everything that talked to the
Raspberry Pi is gone; the interface logic (chassis, geometry, rendering, deferred tap, settings) is
kept.

## What is carried over as-is
- `MainWindow.xaml` (chassis, island, drawn buttons, diagnostic panel), `DeviceGeometry.cs`
  (physical geometry, `FitPicture`, orientation `Detect` from the pixels), the rendering
  (`WriteableBitmap` Bgr32, `_pending`/`_present` swap under a lock — the swapped buffer carries
  NV12, converted into the back buffer on each tick —, `CompositionTarget.Rendering`,
  `PictureScale`), the deferred tap (`TapDeferMs` 180, `TapPressMs` 40, `TapSlopWindowPx` 5,
  `FlushPendingPress`, `OnLostMouseCapture`), `Settings.cs` (atomic JSON in
  `%APPDATA%\LuminaMonitor`).
- `HidKeyboard.cs`: Windows-key-to-physical-HID-usage mapping, modifiers, dead keys, `Compose` for
  pasting.

## What is replaced
| Before (Pi) | After (Core) |
|---|---|
| `VideoReceiver` → `OnFrame(bgra, w, h, stride)` | `DeviceSession.FrameDecoded(VideoFrame)`; `frame.Bgra` (lazy NV12→BGRA conversion, on the decode thread) then copied into `_pending` |
| `InputBridgeClient` (9-byte TCP, 0..32767) | `session.Input`: `TouchDownAsync/MoveAsync/UpAsync`, `TapAsync`, `DragAsync`, `PressButtonAsync`, `KeyboardReportAsync` (new, see below) |
| relative mode, re-anchoring, `Nudge`, `KeepAwake`, `PingBridge`, `KeepBridgeConnected` | removed (the pointer is absolute by nature; the Core session manages state) |
| settings `VideoPort`, `BridgeHost/Port/Token`, `AbsolutePointer` | removed; `DdiFolder` added ("copy of Restore/" folder), `LastArchive` (the xip or dmg the DDI was extracted from) and `UnlockCode` (empty by default, see below) |
| `LatencyProbe`, `Mp4Writer` | removed from the app (they stay in the Pi repository) |

## Inputs → Core
- Coordinates: position within the `Screen` control ÷ `PictureScale` → image pixels (1328×2896 =
  whole screen) → normalised 0..1.
- Left button: deferred tap kept; tap → `TapAsync(x, y, TapPressMs)`; long press/drag →
  `TouchDownAsync` at the origin point then `TouchMoveAsync` on every movement (capped at ~120 Hz),
  `TouchUpAsync` on release or on capture loss.
- Wheel: scroll = `DragAsync` vertical, 220 image px per notch, 90 ms, at the pointer's position. A
  notch downward scrolls the content downward, so the finger drags UPWARD; reversed if
  `InvertWheel` (false by default).
- Right button: `PressButtonAsync("home")`. Middle: nothing.
- Keyboard: `OnPreviewKeyDown/Up` → `HidKeyboard.Translate` → set of held usages →
  `session.Input.KeyboardReportAsync(usages)` (39-byte report, surface 512). Left Ctrl+Alt =
  release control.

## Chassis buttons
The drawn side ridges are a fraction of a millimetre wide: the click is carried by four
transparent rectangles (`HitVolUp`, `HitVolDn`, `HitAction`, `HitSide`), as wide as the metal
edge, placed by `ShapeButtons` alongside the ridges and rotated along with the orientation.

- `MouseLeftButtonDown` marks the event `Handled` **before anything else**, and the rectangles
  live outside the screen border: a click on the metal is never a finger on the glass —
  `OnMouseDown`'s position check would catch it anyway.
- Visual feedback: `Flash` lays a white `SolidColorBrush` at 35% on the clicked rectangle and
  animates it to transparent over 280 ms (`ColorAnimation`, a fresh brush per click — a frozen
  brush does not animate; the brush must be transparent, not `null`, on arrival, or the rectangle
  stops accepting clicks). A tooltip on each of the four.
- Volume + / − → `PressButtonAsync("volume-up" / "volume-down")`: the volume HUD appears on the
  left edge, measured on 9 September (`scratchpad/chassis/10-volume-up.bmp` and `11-…`).
- The rectangle placed on the **Action button** sends `"mute"` — which is the media keyboard's
  **Mute** key: the volume HUD drops to zero then comes back on the second press. The Action
  button itself (ring/silent toggle) **cannot be reached** through the Consumer page; the tooltip
  says so, and the status label reads "Muted", not "Silent".
- **Side button: two-way, just like on the phone.** Screen on → `session.SleepScreenAsync()`
  (`lock`, Consumer page 0x30 held 500 ms); screen off → `session.WakeScreenAsync()` then
  `UnlockAsync()`.
- **`WakeScreenAsync` presses `home` (Consumer 0x40), not 0x30**, and that is a measurement, not a
  choice: the Power key is not a toggle. Tapped for 40 ms then held for 500 ms, the decoded frame
  stayed at **0.0/255** luminance; `home` wakes the screen in under a second and the packet rate
  climbs back from 2/s to 66/s. The name `wake` was removed from the button table rather than kept
  as a command that does nothing.
- `UnlockAsync` is the function's honest limit: waking the screen is a button press and always
  works, unlocking it needs Face ID (a face in front of the phone) or the passcode. If
  `Settings.UnlockCode` is filled in, the window swipes up (`DragAsync(0.5, 0.94 → 0.40, 280 ms)`,
  a real drag: a single report would read as a teleport), waits 900 ms and types the code on the
  virtual keyboard; a 4- or 6-digit code is validated by iOS on its own, any other length gets a
  carriage return. Otherwise it says a face or the phone is needed, and does not claim to have
  unlocked anything. **The code never goes into the log**: the status lines only count the
  characters.

## Locking and the session
**What locking does to the stream, measured on 9 September 2026** (probe `chassis-test`): the
stream **does not stop**. After `lock`, the rate drops from 42 packets/s and 40 frames/s to
**2 packets/s and 1 frame/s of fully black frames** (luminance 0.0/255), the state stays
`MediaUp`, and **no** watchdog rung fires — 0 key-frame requests, 0 stream restarts, 0 soft
resets — because packets keep arriving. There is therefore **nothing to escalate**: `home` wakes
the screen and the picture comes back on its own within a second. The safeguard below exists in
case a longer sleep were to eventually dry up the stream completely.

`DeviceSession.ScreenAsleep` is true **only** when this session is the one that put the screen to
sleep; pressing the real button by hand is invisible from here, and claiming otherwise would be
worse. What that buys: the difference between "there is nothing left to photograph" and "the
mirror is broken", indistinguishable from the packet counter alone and demanding opposite
responses.

- `DeviceSession.OnStallAsync` ignores the watchdog rungs while `ScreenAsleep` (a key frame would
  go to an encoder with nothing to encode, a stream restart would spend the display service's
  patience, a soft reset would demand exactly the unlock that has not happened). The ignored rungs
  are counted, and `WakeScreenAsync` calls `MediaSession.RearmWatch()`: an ignored rung would leave
  the watchdog half-armed with a debt nobody will ever report, deaf for the rest of the session.
- **`WatchDarkScreen` (once a second) decides the banner, and it also sees the locks the app did
  not order** — that is, the ordinary case, the phone locking itself two minutes after the last
  touch. The discriminant is the packet rate: a screen that is on, even perfectly still, sends
  40 to 60 frames/s; a screen that is off sends **one**; a truly dead stream sends **none**. So
  "between 1 and 3 frames/s for the last 4 seconds" = dark screen, and the threshold sits well
  away from both edges.
- **`LockBanner`** at the centre of the frame ("iPhone locked · the screen is off, the stream is
  idling, nothing is broken") with a "Wake the screen" button. Control is released (`Disengage`),
  the status dot switches to "locked", and `WatchStream` **stops saying** "Stream stalled": that
  was exactly the moment a normal state looked like a failure. The banner disappears with the
  session (`Resetting`, `Faulted`, `Detached`, unplugged).
- The side button decides based on `_screenDark`, not on `session.ScreenAsleep`: otherwise
  clicking on a phone that locked itself would send `lock` to an already-off screen.
  `ScreenSleepChanged` only makes the decision immediate when the sleep came from here
  (`DecideDarkScreen`, without touching the counter: folding a fraction of a second into the
  one-second window would make the rate it measures fictitious, and that rate is the whole
  instrument).

## Clipboard, both ways
The `com.apple.coredevice.pasteboardservice` service (direct XPC dialect, verbs `PULL`/`SET`) is
reimplemented in `Core/RemoteXpc/PasteboardService.cs`; `DeviceSession` opens it on demand and
closes it afterwards — a clipboard is used a few times an hour, a channel held open would be one
more thing to rebuild after every incident.

| Gesture | Path |
|---|---|
| **F2** / "To iPhone" button | `session.WritePhoneClipboardAsync(text)` — the text arrives whole and instantly, accents and emoji included. **Fallback** if the service refuses: the old character-by-character typing (`HidKeyboard.Compose`), and the status bar says so, so a slow, lossy paste is not mistaken for the fast one. |
| **F4** / "From iPhone" button | `session.ReadPhoneClipboardSnapshotAsync()` → `Clipboard.SetText`; "42 character(s) copied from the iPhone." |

- F4 is tested without Alt: Windows delivers Alt+F4 as `Key.System` with F4 underneath, and
  without this check the one shortcut everyone knows for closing a window would go fetch a
  clipboard instead.
- The phone's clipboard very often holds a photo: `ClipboardContent` carries the kind
  (`Nothing`, `Text`, `Image`, `Data`), the UTI and the size, and the window announces "an image
  (public.png, 1.2 MB) — not transferred" rather than returning an empty string that would read
  as "there was nothing." The content itself **never** goes into the log, only the counts.
- **No automatic sync**: nothing leaves on its own, in either direction.
- New in Core: `InputInjector.KeyboardReportAsync(IReadOnlyCollection<int> usagesHeld)` (full
  state, no reply expected); `TypeAsync` builds on it. Probe: `keys <text>` command to prove
  surface 512 (open Notes and look).

## Audio panel
The phone's sound, over the cable, on the Windows output of one's choice. `MainWindow.Audio.cs` and
the XAML's `AudioPanel` border; the chain itself is in Core and described in §11 of
[`AUDIO.md`](AUDIO.md). No microphone and no Bluetooth: the phone advertises no incoming capability
(§5) and the radio was ruled out (§9).

| Control | Setting | What it does |
|---|---|---|
| "iPhone sound" switch | `audioEnabled` (true) | opens or **closes the stream**: off, nothing is negotiated, nothing is decoded, and iOS holds no capture session |
| "Output" list | `audioOutputId` (empty) | "Windows default output" (the **Console** role) then the **active** render endpoints; what is stored is the endpoint identifier, not its name |
| "Volume" slider + "Mute" | `audioVolume` (100), `audioMuted` (false) | a gain applied to the samples inside the app, **never** the system mixer — that endpoint is shared with the rest of the machine |
| "Delay" slider, 0–300 ms | `audioDelayMs` (50) | the jitter buffer's target fill; this is the setting that lines the lips up |

- **The values are written when the panel closes** (plus the two switches, which are rare): a slider
  dragged across its travel raises a hundred events, and a hundred atomic file replacements for one
  gesture is a disk being punished for nothing. The live chain is told at once —
  `DeviceSession.AudioOptions` applies while the sound runs.
- **The controls are filled with the handlers held off** (`_audioFilling`): a slider given its value
  while its handler is live saves that value back over the one it was just given.
- **The output list is read on another thread** (`Task.Run`): the MMDevice enumeration is COM and this
  code runs on the window's thread. It is rebuilt when the language changes, its first entry being a
  sentence.
- **Status line, at four hertz at most**, in the order the questions come: off → no mirror → opening →
  refused (with the daemon's reason and a **Try again** button, the one place it appears) → playing on
  *device*, buffer *n* ms, and the gap count if there is one.
- Styles: `ToggleSwitch`, `PanelCombo` and `PanelButton` already existed (leftovers of the Bluetooth
  bridge); `PanelSlider` is new in `App.xaml`, its template rewritten whole as `Button`'s is — Aero2
  draws a light grey rail that no colour overrides. The list's entries present themselves through
  `ToString()`, with no `DisplayMemberPath`: nothing for the binding engine to reflect over on a
  private type.

## Session
- On launch and on every plug-in (`UsbmuxClient` Listen: Attached/Detached):
  `DeviceSession.ConnectAsync`.
- `StateChanged` → status bar ("Connected", "Paired", "Developer image…", "Tunnel", "Mirror",
  "Error: …").
- `UnlockRequired` → "Unlock the iPhone" banner over the picture; disappears on the next state
  change.
- `Faulted` → message, retried 5 s later (delay doubled on each failure, capped at 30 s),
  indefinitely as long as the device is plugged in. As long as a session exists and is not
  `Faulted` — including during a `Resetting` — no second session is opened: a second media stream
  would freeze the display service.
- `Resetting` → "Restarting the mirror…"; the injector is re-read on every `MediaUp`, since a soft
  reset climbs the whole staircase on its own and builds a new one.
- `RestartRequired` → "Restart the iPhone" banner (persistent, the state is `Faulted`).
- First run (no `DdiFolder`): "Developer image" dialog: pick a Xcode `.xip`, a Device Support
  `.dmg` or a `.pkg` → `DdiExtractor` into
  `%APPDATA%\LuminaMonitor\ddi\<ProductBuildVersion>` (line-by-line progress in the status bar,
  ~1 min for a xip). The staging folder is emptied before each attempt: half of an older image
  would look too much like a complete one. On failure, the message says which — unexpected file,
  incomplete archive, missing package.
- Apple's multiplexer missing or stuck: banner (the same as "Unlock the iPhone") with the refusal
  text **and** an "Open Apple Devices" button, which launches
  `explorer.exe shell:AppsFolder\AppleInc.AppleDevices_nzyj5cx40ttqa!App`. This button only
  appears for that one failure (`LuminaException.AppleMultiplexer`), because it is the only one
  that is fixed with a single click. The app never stops the Apple process.

## Log
`%APPDATA%\LuminaMonitor\lumina.log`, **always**, not only in diagnostic mode: asynchronous
writing (bounded queue + dedicated thread, a lost line rather than a frozen window), millisecond
timestamps, rotated at 5 MB into `lumina.1.log`. It holds every state transition, Core's log
lines, reconnection decisions, media counters every 10 s while `MediaUp`, and **every unhandled
exception** with its stack — the three doors are watched in `App.xaml.cs`
(`AppDomain.UnhandledException`, `DispatcherUnhandledException`, `TaskScheduler.UnobservedTaskException`),
each one flushing the log to disk before letting the process go. This is the answer to the "the
picture disappears without a trace" and "the process ended with nothing in Event Viewer"
symptoms.

## Diagnostic mode
`LuminaMonitor.App.exe --diagnostic <seconds>`: normal window, same log but verbose (one line
per second: states, frames/s received and presented, presentation latency, errors), closes
itself automatically at the deadline. Used for testing with nobody in front of the screen.

Every line carries an **`AUDIO`** block — stream open/refused/off, packets, frames decoded and
failed, losses, out-of-order packets, under-runs, skips, queue fill against its target, endpoint and
format in use, sound-minus-picture skew. The format comes from `AudioStats.ToString()`, one place
shared with the probe. It is the only trace of the sound when nobody has opened the panel, and the
synchronisation diagnostic adds a line of its own every five seconds.

## Acceptance criteria
- Build 0 warnings; `--diagnostic 20`: reaches `MediaUp`, ≥ 30 frames/s presented, no exception in
  the log.
- Probe `keys hello`: the text appears in Notes (visual check).
- Manual (by eye): tap, long press, drag, wheel, buttons, typing; perceived latency; landscape
  orientation.
- Probe `chassis-test`: one decoded frame per button, average luminance next to it — a screen that
  is off and a stream that has stopped look the same in a log and never in a frame. Then two
  locks, one short and one long, with the packet rates second by second.
- Probe `clipboard` then `clipboard <text>`: read, write, read back (a write with no read-back
  proves nothing).
