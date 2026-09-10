# The phone's audio — study

**English** · [Français](AUDIO.fr.md)

Facts measured on 9 September 2026, iPhone 17 Pro Max (iPhone18,4) under iOS 27, probe `audio-info`,
`aac-selftest`, `media-status`. Everything below was read off the wire or from an HRESULT; whatever
could not be measured is stated as such.

**The conclusion in one line:** the phone will send its audio, it sends it as **AAC-ELD**, and
**Windows cannot decode AAC-ELD**. There is no lever anywhere in the negotiation to ask it for
anything else.

## 1. The audio negotiation

Same call as video — `com.apple.coredevice.feature.startmediastream`, action
`…mediastreamstart` — with `type: "audio"`, without the two display options
(`CoreDeviceVideoDisplayMode`, `VideoStreamForDisplayID`), and an offer built in **negotiator mode
6** (video is in 5). See `Media/AudioOffer.cs` and `DisplayService.StartAudioStreamAsync`.

### No codec bank — the central point

The video offer carries, in its `VideoSettings` message, a repeated field 3:
`videoPayloadCollections`, one bank per codec, each naming a payload type and a capability string,
offered in order of preference. That field is what lets this project get H.264 instead of HEVC.

**The audio message has nothing like it.** In Apple's captured offer it is made of six varints,
and nothing else:

```
f1 = session id (padded to five bytes, same as on the video side)
f2 = 0
f3 = 0
f4 = 24191        (0x5E7F)
f5 = 0
f6 = 0
```

No named payload type, no capability string, no sample rate, no channel count, no bank. The field
names of `VCMediaNegotiationBlobAudioSettings` have not been recovered (the video ones were, from
the `__objc_methname` table of the framework): they are therefore referred to here by number, and
**no field is invented**.

### What the phone's answer reveals

The response carries a `negotiatorAnswer` built the same way as our offer: a binary plist
containing a zlib-compressed protobuf. `audio-info` decompresses it and prints it field by field.
Its audio message:

```
f1 = 578882765   (the phone's SSRC)
f2 = 0
f3 = 0
f4 = 5632        (0x1600)
f5 = 1536        (0x0600)
f6 = 1
```

`0x1600 & 0x5E7F == 0x1600`, and `0x0600 ⊂ 0x1600`: **f4 is a capability mask**, and the phone
answers with the subset it keeps. Measurements on this mask:

| offer | result |
|---|---|
| `f4 = 24191` (Apple default) | accepted; response `f4 = 5632`, `f5 = 1536`, `RxPayloadType = 101`, `AudioStreamMode = 8` |
| `f4 = 65535` (everything offered) | accepted; **identical** response — offering more changes nothing |
| `f4 = 0` | **refused**: code **32033**, domain **`GKVoiceChatServiceErrorDomain`**, `NSErrorUserInfoDetailedError = 4` |

The field is therefore load-bearing — zeroing it gets the offer refused — but it is not used to
pick a codec: whatever mask is accepted, the phone keeps `0x1600` and announces the same
`RxPayloadType = 101`. **There is no AAC-LC, no PCM, no Opus to ask for.** The error domain's name
is itself informative: to iOS, these streams are *voice chat*.

The other candidate was the top-level tier table (field 9), shared with the video offer, where
three kinds of entry are not bitrate caps: kind 16 (4100), kind 4 (6500), kind 1 (299) — the
reference reads them as codec markers (CELT-NB, SILK, Opus?). The `paliers-sans-codec` and
`paliers-codec-seuls` variants exist in the probe for this reason; they could not be measured
before the phone's display service went deaf (see §6).

## 2. What the phone sends

`streamConfig` of the response, the values that matter:

```
RxPayloadType = 101      TxPayloadType = 101      AudioStreamMode = 8
Direction = 1            CaptureSource = 0        SRTPCipherSuite = 0   (stream in the clear)
RTCPEnabled = True       RTCPSendInterval = 1     RTCPTimeoutInterval = 20
source: { audioSystemOutput: {} }
```

On the wire, silent screen, 12 s of measurement:

- **101.1 packets/s**, 16 bytes per datagram: a 12-byte RTP header and **4 bytes of payload**
  (`00 68 34 00`). About ≈ 13 kbit/s. This is a **silence stream**: the phone transmits
  continuously, even when nothing is playing.
- **No RTP timestamp step: 480 units per packet.** At 48 kHz a unit is one sample, so **480
  samples per frame, 10 ms**. This is a measurement, and it contradicts the configuration: the
  `frameLengthFlag` of the AudioSpecificConfig is 0, which the standard associates with 512. The
  wire wins.
- No loss, no duplicate, no reordering; a single marker bit across the whole session.
- The RTP clock is the sample clock (48 kHz), not the video's 24 kHz: `RtcpSession` now takes its
  jitter clock as a parameter.

**Not measured: the actual sound.** Nobody managed to get a sound playing on the phone during the
measurement. What precedes therefore describes silence — which is already informative (the
stream exists and is continuous), but the bitrate and frame size with real content still need to
be measured. **Request to the user: play some music on the phone during the 10 s of
`audio-info 10 son.rtp`.**

The AudioSpecificConfig the phone advertises is `F8 E6 40 00`, read bit by bit (ISO/IEC 14496-3
§1.6.2.1): **object type 39 = ER AAC ELD**, sample-rate index 3 = **48,000 Hz**, channel
configuration **2 (stereo)**.

## 3. What Windows can decode: `aac-selftest`

Windows' only AAC decoder is the *Microsoft AAC Audio Decoder MFT*
(`CLSID {32D186A7-218F-4C75-8876-DD77273A8999}`, `C:\Windows\System32\MSAudDecMFT.dll`). The
question "can Windows decode what the phone sends" is settled with `SetInputType` and an
HRESULT.

The MFT refuses a whole type with a single `MF_E_INVALIDMEDIATYPE` and never says which attribute
it disliked. The measurement is therefore a **bisection**: start from its own advertised input
type — which it accepts as-is — and move toward the target type one attribute at a time, twice,
once with an AAC-LC AudioSpecificConfig and once with the phone's.

```
witness AAC-LC (11 90)                        phone AAC-ELD (F8 E6 40 00)
type announced as-is               ACCEPTED   type announced as-is               ACCEPTED
+ SAMPLES_PER_SECOND rewritten      ACCEPTED   + SAMPLES_PER_SECOND rewritten     ACCEPTED
+ USER_DATA rewritten (12 zeros)    ACCEPTED   + USER_DATA rewritten (12 zeros)   ACCEPTED
+ AudioSpecificConfig added        ACCEPTED >> + AudioSpecificConfig added        REFUSED  0xC00D36B4
+ two channels                      ACCEPTED   …
+ 16 bits, 4-byte alignment         ACCEPTED
+ average bitrate 16000 B/s         ACCEPTED
```

A single byte differs between the two columns at the marked step: **Windows' decoder accepts
object type 2 (AAC-LC) and refuses object type 39 (ER AAC ELD)**, with
`MF_E_INVALIDMEDIATYPE (0xC00D36B4)`. The documentation said so; it is now measured.

> **Interop trap, half an hour lost.** `IMFAttributes::SetBlob` declared with a `byte[]` parameter
> returns **S_OK and writes nothing**: in COM interop, the default marshalling for an array is
> `UnmanagedType.SafeArray`, and the MFT receives a SAFEARRAY header where it expects raw bytes.
> The symptom is a decoder that seems to refuse every codec, AAC-LC included. `SetBlob` and
> `GetBlob` therefore take an `IntPtr`, with the buffer allocated by hand (`AacProbe.WriteBlob`).
> The proof is in the probe: a pattern written then read back.

## 4. Possible next steps, costed out

**a) Write an AAC-ELD decoder in C#.** The only path that stays inside our own stack. What it
takes, and nothing less: a bit reader; `ELDSpecificConfig`; the ER AAC ELD syntax (section data,
scale factors, spectral data); the eleven Huffman books plus escape coding; inverse quantisation
in `x^(4/3)`; TNS; the stereo tools (M/S, intensity); the **low-delay MDCT** with its own window;
the scale-factor band tables at 48 kHz; and, if the phone enables LD-SBR (`f5` in the response
might be there for that), the whole of SBR on top. Estimate: **3,000 to 5,000 lines, tables
included, two to four weeks** for a correct version. The real risk is not the size: it is that a
subtle bug produces noise rather than silence, and there is **no reference decoder on this
machine** to compare sample by sample against (no NuGet, no ffmpeg). Possible split: (1) bit
reader and syntax walk, consuming exactly each AU with no error — verifiable on a capture, with no
sound produced; (2) inverse quantisation and MDCT, checked on a silence frame whose output is
known (zero); (3) the rest, by ear.

**b) A fallback codec.** Ruled out by the §1 measurement: the offer has no bank, and the only
existing lever (`f4`) does not change the phone's choice.

**c) Not going through our own stack at all.** This PC is **already** an audio receiver for this
iPhone: the phone is paired to it over Bluetooth and Windows has mounted both profiles
(`<iPhone name> A2DP SNK` — the phone's sound to the PC — and `<iPhone name> Hands-Free HF Audio`
— both ways, microphone included). Zero lines of code, available right now, and it is the honest
answer to "hear the phone's sound on the PC" until (a) is written. The known downside: Bluetooth is
not synchronised with the mirror's picture, and its delay is not measured here.

Recommendation: **(c) right away, (a) only if the user wants the sound *inside* the app, knowing
it is two to four weeks for a component whose failure shows up as noise.**

## 5. The PC's microphone into the phone

There is **nothing** of the sort in this service, and this is not a deduction:

- the `direction` field of `startmediastream` accepts `"input"` — the offer is accepted and the
  response echoes back `direction = input` — but **nothing changes**: the source stays
  `audioSystemOutput`, `streamConfig.Direction` stays 1, and the phone keeps *sending* us its
  100 packets/s. The field is echoed back, not honoured;
- `getmediasupportinfo` enumerates what the device can do, and the list is exhaustive:
  "Primary video display mirrored output stream, System audio output stream, Virtual external
  video output stream, Video output stream by display ID, Display information, Screenshot
  capture" (`supportedFeatures = 972`). **No incoming stream, no microphone capture**;
- the reference implementation has no other path either: `direction` is hard-coded to `"output"`
  in both places it appears, and there is nowhere an `audioSystemInput` or an audio-capture
  service.

So: **the PC's microphone cannot be sent to the phone through CoreDevice.** What already exists
and does it is the Bluetooth hands-free profile already mounted on this PC (§4c): in that mode,
the phone hears the PC's microphone. No virtual driver is proposed here.

## 6. Orphaned media sessions — and the phone's microphone

Reported symptom: **after a mirroring session, the phone's microphone seems disabled for its
other apps.**

### What is measured

A media session lives on the phone, not here. `startmediastream` creates it, `stopmediastream`
ends it, and nothing else does. Measurement, with the `audio-leak-test` command (opens an audio
stream and does not close it) then `Stop-Process` on the probe, observed by `media-status 90`
from a second process:

- after a **clean stop**: `sessions: []`, `running = False`;
- after a **killed process**: the session stays, whole — `running = True`, `type = audio`,
  `source: audioSystemOutput`, its own `avcMediaStreamOptionClientSessionID`, and a
  `runDurationSeconds` that keeps climbing. Still alive 36 s after the kill;
- it **disappears on its own about 20 s after the last receiver report**: that is
  `RTCPTimeoutInterval = 20`. Measurement: session killed at 22:01:52, `running = False` at
  22:02:12;
- `media-release` closes it **immediately** (`stopmediastream` on the identifier found in the
  server's state);
- the startup safeguard works: leak created, probe killed, `audio-info` relaunched right away →
  "orphaned media session released: … / 1 orphaned media session released before opening the
  stream", then the stream opens normally.

### What is not demonstrated

**That this is the cause of the symptom.** Two caveats, stated honestly:

1. the orphaned session closes itself in ~20 s, so it does not explain a microphone unavailable
   several minutes later;
2. **until 9 September 2026 this software had never opened an audio stream** — the audio path was
   born that day. A microphone glitch observed *before* that cannot come from an audio-capture
   session. It could come from a video stream left open (the same session mechanism, `type =
   video`), which remains to be checked with the user.

**A competing cause, present and verifiable:** this iPhone is paired to this PC over Bluetooth and
Windows has mounted its **hands-free** profile (`<iPhone name> Hands-Free HF Audio`, HFP
`0000111F-…`, state OK). When a Windows application opens this microphone entry, iOS **routes the
phone's microphone to the PC** and the phone's own apps no longer hear anything — exactly the
symptom described, with our software having nothing to do with it. **Test to run:** when the
microphone seems dead, disconnect the iPhone's Bluetooth (Settings > Bluetooth > this PC >
Disconnect); if it comes back, that is the cause.

### Troubleshooting (to copy into `docs/INSTALLATION.md`)

> **The iPhone's microphone stops working after a mirroring session.**
>
> *Possible cause 1 — a media session the phone still thinks is open.* It survives when the
> process is killed (Task Manager, `Stop-Process`, a crash, the cable yanked out) instead of being
> closed properly. The phone reclaims it on its own after about 20 s, but while it is there iOS
> keeps the audio capture reserved.
> - Immediate fix, without the app: wait a minute, or **restart the iPhone**.
> - Fix with the app: just relaunching it is enough — at the start of every session it asks the
>   phone what it thinks is still running and closes anything left over (log: "orphaned media
>   session released").
> - Command-line fix: `LuminaMonitor.UsbProbe media-status` to see, `LuminaMonitor.UsbProbe
>   media-release` to release.
>
> *Possible cause 2 — the PC's Bluetooth is holding the microphone.* If the iPhone is paired to
> this PC over Bluetooth, Windows mounts a "hands-free" profile that **takes the phone's
> microphone** as soon as a Windows application uses it. Disconnecting the iPhone's Bluetooth
> (Settings > Bluetooth > this PC > Disconnect) gives the microphone back to the phone. This has
> nothing to do with the USB cable or with the mirror.

### What the code now does

- `MediaHygiene` (new): reads `getmediastreamserverstatus`, finds the session identifiers in it
  (any UUID in the response), and closes each one with `stopmediastream`. A `keep` lets it spare
  the session that was just opened — without it, the audio half of a paired session would close
  the video half too.
- `MediaSession.StartAsync` and `AudioSession.StartAsync` call this guard **before** opening their
  own stream, on a channel of their own: the daemon hangs up the channel a `stop` was sent on.
- The guard sits at the **entry** and not at the exit, and that is a deliberate choice: a
  `Stop-Process` runs neither a finalizer, nor `ProcessExit`, nor a `SafeHandle`. No exit-side
  cleanup is robust against a host that vanishes; only the "check before opening" order is.
- In the probe, every stream stop is inside a `finally`, including when the offer is refused or
  the display service does not respond.
- **Still to do on the app side (other session):** show the "orphaned media session released"
  line in the status bar; it already arrives via `IProgress<string>` and goes into the log.

## 7. What remains to be measured

1. **The actual sound**: `audio-info 10 son.rtp` with music playing on the phone (bitrate, frame
   size, payload content).
2. **Audio and video in the same session**, with the same
   `avcMediaStreamOptionClientSessionID`, as Xcode's mirror does: `audio-info 5 --video`. The code
   is written and the guard spares the video session; all three attempts on 9 September ran into a
   display service that had gone deaf ("no SETTINGS from the phone in 3 s"), whose known
   remedy — unmounting the developer image — requires an **unlocked** phone, which it no longer
   was.
3. The tier variants (`paliers-sans-codec`, `paliers-codec-seuls`), to finish ruling out the tier
   table as the place where the codec choice happens.
4. Does an orphaned **video** stream also affect the microphone? That is the only hypothesis that
   would explain the symptom from before the audio path even existed.

## 8. The commands

```
LuminaMonitor.UsbProbe aac-selftest                     # offline: can Windows decode AAC-ELD?
LuminaMonitor.UsbProbe audio-info [sec] [capture.rtp] [--variant=…] [--video] [--direction=…]
LuminaMonitor.UsbProbe media-status [sec]               # what the phone thinks is running
LuminaMonitor.UsbProbe media-release                    # closes orphaned sessions
LuminaMonitor.UsbProbe audio-leak-test                  # opens a stream and does NOT close it (measurement)
```

`audio-info` variants: `default`, `f2:<n>`, `f3:<n>`, `f4:<n>`, `f5:<n>`, `f6:<n>`,
`paliers-sans-codec`, `paliers-codec-seuls`, combinable with `+`.

## 9. Decision, September 10 2026

Bluetooth is off the table, for both halves of this document: not because it was never tried, but
because it was tried and found unreliable — the radio link between the iPhone and the project's own
test PC did not hold up. That closes §4c (Bluetooth as the sound source) and the Bluetooth
hands-free stand-in §5 pointed to for the microphone.

The iPhone's sound will come over the cable instead, decoded by an AAC-ELD decoder written into
this project — §4a, now underway.

The PC's microphone into the phone stays impossible, cable included, and Bluetooth going away
changes nothing there: §5 already showed the phone advertises no incoming capability over
CoreDevice (`direction: "input"` is accepted and echoed back but changes nothing;
`getmediasupportinfo` lists no capture feature), and that was true before this decision and stays
true after it.
