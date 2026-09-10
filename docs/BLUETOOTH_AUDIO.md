**English** · [Français](BLUETOOTH_AUDIO.fr.md)

# The phone's sound over Bluetooth — study

Facts measured on 10 September 2026, iPhone 17 Pro Max under iOS 27, Windows 11 25H2 (build 26200),
MediaTek Bluetooth adapter, probe commands `winrt-selftest`, `phone-audio`, `audio-endpoints`,
`bridge-selftest`, `call-bridge`. What could not be measured is said as such.

**In short.** Through the cable, the sound is AAC-ELD and Windows cannot decode it
([AUDIO.md](AUDIO.md)). Over Bluetooth, Windows *can* be the phone's speaker (A2DP), but only while an
application holds a `Windows.Media.Audio.AudioPlaybackConnection` open — nothing on this PC did, which
explains the silence. `Core/Audio` now does it, through hand-written interop. On the test day **the
phone did not answer the PC's Bluetooth connection request**: the software path is proven up to the
radio, the sound itself is not proven yet. The protocol at the end of this note is what remains.

## 1. Why nothing came through

Windows 10 2004 and later can receive A2DP from a phone, under one condition documented by
Microsoft: an application calls `AudioPlaybackConnection.GetDeviceSelector()`, finds the phone with
`DeviceInformation.FindAllAsync(selector)`, then `TryCreateFromId(id)` → `StartAsync()` →
`OpenAsync()`, and keeps the object alive. Windows then plays the sound itself, on the default output.
Pairing alone creates the device (`<iPhone name> A2DP SNK`) but opens nothing.

Checked on this PC:

- The selector Windows builds (`winrt-selftest`) asks for a KSCATEGORY_AUDIO interface carrying the
  Bluetooth **Audio Source** service `0000110A`, enabled. **One** match: the iPhone, interface
  `…\SNK`, driven by **BthA2dp — "Microsoft Bluetooth A2dp Sink"** (Microsoft's inbox driver, not the
  MediaTek one). The prerequisite exists.
- Among all Windows audio endpoints, disabled and absent ones included (`audio-endpoints --all`),
  **there is not a single Bluetooth endpoint** — neither A2DP nor hands-free. Nothing is connected,
  nothing is open.
- The public class is in-process (`Windows.Media.Devices.dll`); it drives a private class,
  `Microsoft.Bluetooth.Profiles.A2dp.Private.A2dpSinkPlaybackConnection`, hosted by the **BthAvctpSvc**
  service (registry, `ActivatableClassId`). So the connection is held by a Windows service on behalf
  of the application.

Diagnosis **confirmed on the software side**: without that call, no A2DP stream can open.

## 2. What the real test gave

`phone-audio 60`, phone within range, paired, on the cable:

| try | result | time |
|---|---|---|
| 1 | `OpenAsync` → **UnknownFailure**, ExtendedError **0x8007001F** (ERROR_GEN_FAILURE) | 5.3 s |
| 2 | same | 12.9 s |
| 3 (after the code review) | same | 12.9 s |

`PhoneAudioLink.Description` turns that code into "the phone did not answer over Bluetooth" plus
what to do, rather than "unknown failure".

Checks around it:

- The PC's Bluetooth radio is **on** (`Windows.Devices.Radios`).
- The Bluetooth address the phone reports through the cable (lockdown `BluetoothAddress`, `session`
  command) is **the one Windows paired**: not a stale pairing of an older phone.
- Windows's `LastConnectedTime` for the phone is **17 August 2026**, unchanged after both tries: the
  Bluetooth Classic link never came up.
- 5.3 s is the Bluetooth *page timeout* (5.12 s): the PC called the phone and **the phone did not
  answer**. The likely causes are on the phone: Bluetooth turned off, or turned "off" from Control
  Center (which drops accessories and refuses new ones until it is turned back on).

The connection stays **listening** after a refusal (`StartAsync` succeeded): if the phone then picks
this PC in its own Bluetooth menu, the link moves to `Open` by itself — the probe polls and prints it.

## 3. The interop, by hand

No NuGet, no WinRT projection: combase (`RoGetActivationFactory`, `WindowsCreateString`,
`WindowsDeleteString`, `WindowsGetStringRawBuffer`) and interfaces declared slot by slot, in
`Core/Audio/WinRt/`.

- **IIDs**: the Windows SDK is not installed here. They were read from the metadata the headers are
  generated from, `C:\Windows\System32\WinMetadata\*.winmd` (GuidAttribute of each interface, methods
  in vtable order), with `System.Reflection.Metadata`. Each one is exercised by `winrt-selftest`: a
  wrong IID fails with E_NOINTERFACE on the first cast.
- **Parameterized interfaces** (`IAsyncOperation<…>`, `IVectorView<…>`) have no GUID of their own;
  theirs is derived from a signature (UUID v5, SHA-1). `WinRtIid.FromSignature` computes it and the
  self-test recomputes the three constants. Control: `IAsyncOperation<DeviceInformationCollection>`
  gives `45180254-082e-5274-b2e7-ac0517f44d07`, the value the SDK publishes.
- **`InterfaceIsIInspectable` does not work on .NET 8**: any call throws
  `PlatformNotSupportedException` ("Marshalling as IInspectable is not supported") — measured. Each
  interface is therefore declared `InterfaceIsIUnknown` and opens with IInspectable's three slots
  (GetIids, GetRuntimeClassName, GetTrustLevel), just as the Media Foundation declarations repeat the
  IMFAttributes slots.
- **Asynchrony**: polled through `IAsyncInfo.Status` every 25 ms; the connection state is polled
  every 500 ms. No completion handler, no event.
- **Apartments**: `CoIncrementMTAUsage` once; every call goes through the thread pool (implicit MTA),
  never the window's STA thread. The class is registered as agile (`Threading = Both`).

The fallback (`net8.0-windows10.0.19041.0` and the downloaded projection) was **not** needed.

## 4. Choosing the output (speakers or headphones)

What is certain: `AudioPlaybackConnection` has **no parameter for the output**; Windows renders on
its default output. What is not yet measured: *which process* renders it, since the connection lives
in the BthAvctpSvc service. That decides everything, and `phone-audio` prints it at the first second
of sound (`NOUVELLE session … pid … <process>`):

- **session in the application's process** → the per-application setting of Windows applies: *Settings
  › System › Sound › Volume mixer* (`ms-settings:apps-volume`), output device of Lumina Monitor. The
  system default does not move.
- **session in a service (svchost)** → the per-application setting probably does not reach it; the only
  honest lever left is the system default output. If Windows also exposes an A2DP *input* endpoint at
  that moment (the probe prints every new endpoint), a second route opens: copy it with `AudioPump`
  to the chosen output — to be written once seen, not before.

What this project does **not** do: change the system's default output without saying so. The window
lists the outputs (`AudioEndpoints.List(AudioFlow.Output)`), says where the sound goes
(`AudioEndpoints.Default`) and opens Windows's page (`BluetoothAudio.OpenSoundSettings()`).

## 5. The PC's microphone to the phone (hands-free, HFP)

In HFP Windows plays the **car kit** (`BthHFAud` driver, `<iPhone name> Hands-Free HF Audio`,
`microsoft_bluetooth_hfp.inf`). Its two endpoints, by the profile's definition:

- **input** (capture) = the far end of the call, the voice to hear;
- **output** (render) = what the far end hears: that is where our microphone goes.

**They do not exist while the phone is not connected**: on the test day, none, not even disabled.
Hence nothing could be verified on the phone. Established conditions:

- the voice channel (SCO) opens **during a call** (phone, FaceTime, VoIP app) whose audio the iPhone
  sends to this PC (audio button of the call › this PC);
- it also opens **as soon as a Windows application opens those endpoints**, call or no call — and iOS
  then hands its microphone to the PC: that is the "the phone's microphone is dead" symptom already
  met ([AUDIO.md](AUDIO.md) § 6).

**Delivered: `CallAudioBridge`, off by default.** Two WASAPI pumps in shared mode (chosen microphone
→ hands-free output; hands-free input → chosen output), a common format (48 kHz stereo float) with
AUTOCONVERTPCM so the engine converts to 8/16 kHz mono, event-driven capture, render queue capped at
60 ms (whatever does not fit is dropped). Verified on this PC's own endpoints at gain 0
(`bridge-selftest`): **144,480 frames in 3 s, none dropped**, conversion accepted both ways. Not
verified: the phone's endpoints themselves. Rules, written in the code: start only on an explicit
request during a call, never at startup; `Dispose` gives the microphone back; the bridge stops by
itself when Windows tears the link down (`AUDCLNT_E_DEVICE_INVALIDATED` → `Stopped`, `StopReason`).
No virtual driver anywhere.

Alternative with no code of ours: Microsoft's **Phone Link** app handles iPhone calls over this very
profile.

## 6. What the user has to test

**A — music (A2DP).** On the iPhone: Settings › Bluetooth on (in Settings, not Control Center). On
the PC: `LuminaMonitor.UsbProbe phone-audio 120`. If the probe says `Refused` / `0x8007001F`, on the
iPhone tap this PC under *My Devices* while the probe is still running. Expected: `[evenement] etat ->
Open`, then play music. Tell: (1) whether it is heard, and on which output; (2) the `NOUVELLE session`
and `NOUVEAU point de terminaison` lines — they settle § 4.

**B — call (HFP).** Phone connected to the PC as in A. Place a call; in the call screen, audio button ›
this PC. Then `LuminaMonitor.UsbProbe audio-endpoints --all` (do the hands-free endpoints appear,
active?) and `LuminaMonitor.UsbProbe call-bridge 30` (the far end should hear the PC's microphone,
and the voice come out of the default output). End the call: the probe must print
`[pont arrete]`. Then check that the iPhone's microphone works in another app (Voice Memos).

## 7. Commands

```
LuminaMonitor.UsbProbe winrt-selftest                  # WinRT interop without a phone (IIDs, selector, FindAllAsync)
LuminaMonitor.UsbProbe phone-audio [sec=30] [name]      # opens A2DP, follows state, sessions, endpoints, peaks
LuminaMonitor.UsbProbe audio-endpoints [--all] [--props]
LuminaMonitor.UsbProbe bridge-selftest [sec=3]          # the call bridge's pumps, local endpoints, gain 0
LuminaMonitor.UsbProbe call-bridge [sec=30] [mic] [output]
```

## 8. The API for the window (`LuminaMonitor.Core.Audio`)

```csharp
static class BluetoothAudio {
    bool IsSupported { get; }
    Task<IReadOnlyList<BluetoothPhone>> ListPhonesAsync(CancellationToken cancel = default);
    Task<PhoneAudioLink> StartListeningAsync(string deviceId, string? name = null, CancellationToken cancel = default);
    void OpenSoundSettings();                           // ms-settings:apps-volume
}
record BluetoothPhone(string Id, string Name, bool IsEnabled);
sealed class PhoneAudioLink : IDisposable {           // sound flows while it lives
    PhoneAudioState State { get; }                     // Opening, Open, Waiting, Refused, Closed
    PhoneAudioRefusal Refusal { get; }                 // None, TimedOut, DeniedBySystem, UnknownFailure, NotAvailable, Error
    int? ErrorCode { get; }  DateTime? OpenedAt { get; }  string Description { get; }   // localized
    event EventHandler? StateChanged;                  // thread-pool thread
    Task<PhoneAudioState> ReopenAsync(CancellationToken cancel = default);
    void Dispose();
}
static class AudioEndpoints {
    IReadOnlyList<AudioEndpoint> List(AudioFlow? flow = null, bool includeInactive = false);
    AudioEndpoint? Default(AudioFlow flow);
    float? Peak(string endpointId);
    IReadOnlyList<AudioSessionInfo> Sessions(string endpointId);
}
record AudioEndpoint(string Id, string Name, string DeviceName, AudioFlow Flow, AudioEndpointState State,
                     bool IsDefault, bool IsDefaultForCommunications, BluetoothAudioRole BluetoothRole);
sealed class CallAudioBridge : IDisposable {          // off unless started, see § 5
    static PhoneCallEndpoints? FindPhoneEndpoints();
    static CallAudioBridge Start(string microphoneId, string outputId, PhoneCallEndpoints phone);
    bool IsRunning { get; }  string? StopReason { get; }  event EventHandler? Stopped;
}
```

`AudioEndpoints` methods are synchronous COM: call them through `Task.Run`, not from the window's
thread. Every user-facing text goes through `CoreTexts` (English, French).
