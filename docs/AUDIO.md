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

> **Correction, 10 September 2026, the same evening.** The estimate above was wrong on both counts.
> Once the tables in `docs/AAC_ELD_TABLES.md` were assembled, the decoder itself was written in one
> sitting: about 1,900 lines, not 3,000–5,000, and it exists the same day rather than in two to four
> weeks. See §10, "The home-grown decoder," below.

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

1. ~~**The actual sound**~~ — **measured, 10 September 2026, 20:40.** Thirty seconds captured with a
   video playing on the phone: 3,029 frames, 372 bytes per frame on average, 0.30 Mbit/s, 101
   packets/s, no loss. Decoded whole and listened to; the numbers are in §10.
2. ~~**Audio and video in the same session**~~ — **proven, 10 September 2026, 21:12.**
   `audio-info 5 --video`: the video stream opened first, as Xcode's mirror does, then the audio
   stream with `PairedSessionId = video.SessionId` — the same
   `avcMediaStreamOptionClientSessionID` for both — **503 audio packets in 5 s, no loss**, and both
   streams closed cleanly with the phone hanging up on each service. That order is now what
   `DeviceSession` does; see §11.
3. The tier variants (`paliers-sans-codec`, `paliers-codec-seuls`), to finish ruling out the tier
   table as the place where the codec choice happens.
4. Does an orphaned **video** stream also affect the microphone? That is the only hypothesis that
   would explain the symptom from before the audio path even existed.
5. **The delay is not aligned automatically.** §11 measures the gap between sound and picture every
   five seconds and does nothing with it. What is missing to close the loop is not the measurement
   but the decision: how fast to move a delay without making the movement itself audible.

## 8. The commands

```
LuminaMonitor.UsbProbe aac-selftest                     # offline: can Windows decode AAC-ELD?
LuminaMonitor.UsbProbe audio-info [sec] [capture.rtp] [--variant=…] [--video] [--direction=…]
LuminaMonitor.UsbProbe media-status [sec]               # what the phone thinks is running
LuminaMonitor.UsbProbe media-release                    # closes orphaned sessions
LuminaMonitor.UsbProbe audio-leak-test                  # opens a stream and does NOT close it (measurement)
LuminaMonitor.UsbProbe audio-play <capture.rtp|--silence[=frames]> [--device=<id|default>] [--delay=<ms>] [--dry]
LuminaMonitor.UsbProbe audio-devices                    # offline: the Windows outputs and their mix formats
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

(Continued the same evening: §10, "The home-grown decoder," below.)

## 10. The home-grown decoder

Written the same evening as the decision above, once the tables of
[`docs/AAC_ELD_TABLES.md`](AAC_ELD_TABLES.md) were assembled: about 1,900 lines across 14 files in
`src/LuminaMonitor.Core/Media/Aac/`, no package referenced, nothing of FFmpeg, FDK-AAC or faad2
read. This section records what it does, what it does not, what was measured, and what is not
settled yet.

### What each file does

| File | Role |
|---|---|
| `BitReader.cs` | MSB-first, no-copy bit reader, plus `AacBitstreamException` (a reason and a bit position) |
| `AudioSpecificConfig.cs` | the ASC and `ELDSpecificConfig`, extension chain included, with `Validate()` |
| `Huffman.cs` | the twelve books, as binary trees |
| `SectionData.cs` | the ER variant: `sect_len_incr` on five bits, escape at 31 |
| `ScaleFactors.cs` | the three chains — scalefactors, noise energy, intensity position |
| `TnsData.cs` | the TNS filter data |
| `SpectralData.cs` | quadruples and pairs, signs then book 11's escape |
| `Dequantizer.cs` | `|q|^(4/3)` and `2^(0.25·(sf−100))`, tabulated |
| `Tns.cs` | the all-pole filter |
| `Stereo.cs` | M/S and intensity |
| `Pns.cs` | unit-energy noise, left/right correlation |
| `EldFilterBank.cs` | the low-delay synthesis filterbank, folded onto a DCT-IV, overlap of the three previous blocks |
| `EldSyntax.cs` | the element sequence with no identifiers — CPE/SCE |
| `AacEldDecoder.cs` | the public API and the counters |

Not implemented, each refused with a typed reason rather than silently ignored: low-delay SBR, ELD
extensions (SAOC, MPEG Surround), HCR/RVLC resilience, more than two channels. No allocation per
frame in steady state — see the measurements below.

### The API

```csharp
var decoder = new AacEldDecoder(audioSpecificConfig);
bool ok = decoder.Decode(accessUnit, pcmInterleaved, out int samplesPerChannel);
```

`Decode(ReadOnlySpan<byte> accessUnit, Span<float> pcmInterleaved, out int samplesPerChannel)`
decodes one access unit into interleaved float PCM at ±1 full scale. It returns `false` on a frame
it could not read rather than throwing, so a caller reading a live stream moves on to the next
frame; the counters say why and how much:

- `FramesDecoded`, `FramesFailed`
- `BitsConsumed`, `BitsAvailable` — the last frame's syntax length against what it was given
- `PaddingIsZero` — whether everything after the syntax was the zero padding a well-formed frame
  ends on
- `LastFailure` — the `AacBitstreamException` of the last refused frame, or null

### The probe's commands

```
LuminaMonitor.UsbProbe aac-selftest-decode
LuminaMonitor.UsbProbe decode-audio <capture.rtp> <output.wav> [--frame=480|512]
```

`aac-selftest-decode` runs 18 checks offline, no phone needed: the phone's own silent frame
(`00 68 34 00`) against its known bit count and all-zero output; frames built by hand with known
spectral values per channel, checked against a filterbank fed the same spectrum directly; a
truncated frame, which must be refused at the bit it runs out on; no-allocation over 1,000 frames;
800 random legal frames exercising every codebook, noise substitution, intensity stereo and TNS.

`decode-audio` replays a capture through the decoder end to end and writes a WAV file — 48 kHz,
stereo, 16-bit, the 44-byte RIFF header written by hand, no library — while measuring what does not
show up in a listen: bits consumed per frame, continuity across frame boundaries, RMS and peak,
non-finite samples, decode time.

### What was measured, 10 September 2026

- **`aac-tables-selftest`: 28/28.** Perfect filterbank reconstruction, worst residual 1.3e-8 (see
  `docs/AAC_ELD_TABLES.md` §4).
- **`aac-selftest-decode`: 18/18.** The silent frame consumes 26 of its 32 bits, zero padding, and
  produces 480×2 then 512×2 zero samples; frames built at 480 and at 512 read back exactly what was
  written, with a gap ≤ 1.5e-8 against full scale (32768) over six consecutive frames; 800 random
  legal frames: 0 bit disagreements, 0 non-finite samples, 0.16–0.19 ms per frame.
- **The silent capture, `audio_default.rtp`** (1,211 frames, payload type 101): 1,211/1,211 decoded,
  26.0 bits per frame, 0 frame ending early, late, or on non-zero padding, output strictly zero,
  0.145 ms per frame against a 10 ms budget.
- **A real capture with music** (30 s, 10 September 2026, 20:40, a video playing on the phone):
  3,029 frames, 372 bytes per frame on average, 0.30 Mbit/s, 101 packets/s, no loss. Decoded at
  **480**: 3,029/3,029 decoded, 0 failures, 8,804,260 of 8,814,776 bits used (99.9%), 2,906.7
  bits/frame on average, 0 frame ending early, late, or on non-zero padding, peak +3.4 dBFS (238
  samples clipped out of 2,907,840, 0.008% — a normal overshoot on transients), RMS −16.2 dBFS, 0
  non-finite value, boundary continuity 1.398e-2 against 1.374e-2 elsewhere (ratio 1.017, so no
  click at frame boundaries), 0.140 ms per frame on average, 0.825 ms at worst. Decoded at **512**:
  63 decoded, 2,966 failed, the first failure at frame 1 ("section claims 8 bands from band 26,
  past `max_sfb` 34") — full detail and the conclusion (480) in
  [`docs/AAC_ELD_TABLES.md`](AAC_ELD_TABLES.md) §5.

This proves the bit reader is exact: a syntax error would end a frame somewhere other than its
padding, which is exactly what the 512 run shows and the 480 run does not, across 3,029 frames. It
does not yet prove that the filterbank's phase convention is the one Apple's encoder used — only
listening to the decoded output says that.

### Choices made without certainty

Seven places where the reference software's own text was not enough on its own, and a choice had to
be made and recorded rather than left implicit:

1. **TNS filter lengths** are counted down from the total band count, then clamped to
   `min(max_sfb, ceiling)` — read off `get_tns()` in `huffdec2.c` and `tns.c` of the reference
   software.
2. **Output full scale is ±1** through a `FullScale = 32768` constant; the reference decoder writes
   `time_sample_vector` straight out as 16-bit integers with no scaling of its own.
3. **480 samples per frame by default**, despite `frameLengthFlag = 0` meaning 512 in the reference
   software. The silent capture's RTP timestamp step (exactly 480 every packet) pointed to 480
   first; the real capture's own timestamp step is not that clean, but decoding it settles the
   question directly — 480 goes through end to end, 512 fails at frame 1 (§5 of
   `docs/AAC_ELD_TABLES.md`).
4. **A frame with an out-of-range value is refused, not clamped** — the reference software only
   tolerates that under its error-protection flags, which are 0 here.
5. **A failed frame's overlap is drained (`Flush`), not zeroed**, so the tail fades out instead of
   clicking.
6. **`tns_data` is read right after its own flag**, with resilience at 0 and nothing between the
   two.
7. **PNS energy is `2^(0.25·energy)` over a unit-energy noise** from a congruential generator, with
   left/right correlation honoured.

### Limits

No low-delay SBR, no ELD extensions, no HCR/RVLC resilience, no more than two channels — each
refused with a typed reason rather than silently mishandled. No bit-exact conformance vector exists
to check against (ISO sells those separately); the self-tests prove internal consistency and exact
bit consumption, not a sample-by-sample match against a reference decoder, because none is
available on this machine.

### State

**Confirmed by ear on 10 September 2026.** The 30-second capture, decoded with the 480-sample
tables and copied to a WAV, was listened to against the video that had been playing on the phone:
same music, no noise, normal level. The numbers above (exact bit consumption, no clipping beyond
normal transients, no click at frame boundaries, no non-finite sample) were necessary; the ear was
the sufficient test, and the filterbank's phase convention is the one Apple's encoder uses. Next
step: rendering inside the app — a WASAPI output to choose, volume, and synchronisation with the
picture.

## 11. In the application

Written on 10 September 2026, the same evening as the decoder, once §7's first two questions had
answers. What this section describes is code, not a plan: the chain runs, the self-tests measure it,
and the one thing it has not had yet is a pair of ears on the live stream.

### The chain, end to end

```
phone ─ RTP/UDP over the tunnel ─▶ AudioSession ─▶ AudioRenderer ─▶ AudioJitterBuffer ─▶ WasapiOutput ─▶ endpoint
        one access unit per packet   RTCP RR 1/s    demux, sequence,   target fill,         shared mode,
        PT 101, +480 ts, 10 ms       BYE on stop    AAC-ELD decode     drop / silence       event-driven
```

Six files in `src/LuminaMonitor.Core/Audio/` and one in `Media/`, and each of them does one thing:

| File | Role |
|---|---|
| `Media/AudioStream.cs` | the session and the chain together, plus the synchronisation diagnostic |
| `Audio/AudioRenderer.cs` | the chain in one object: demux, loss, decode, queue — shared with the probe |
| `Audio/AudioJitterBuffer.cs` | the ring of frames between the phone's clock and the sound card's |
| `Audio/WasapiOutput.cs` | one shared-mode render stream, driven by the endpoint's own event |
| `Audio/AudioSink.cs` | the sink interface, and the dry sink that opens no endpoint at all |
| `Audio/AudioFormat.cs` | `WAVEFORMATEX` by hand, and the one conversion this project does itself |
| `Audio/AudioOptions.cs`, `AudioStats.cs` | what a person decides, and what the chain counted |

### Where it sits in the ladder

`DeviceSession` opens the sound **after** the picture and **attached to it** — the audio offer
carries the video stream's own client session id, which is what §7.2 proved works. Three properties
of that order are deliberate:

- **the mirror is declared up first.** The sound opens on a task of its own, the moment the picture
  is in place. The first version waited six seconds (`AudioSettleMs`), copying the pause
  `audio-info --video` used in the run that proved the two streams could share a session; on
  11 September 2026 `audio-info --video --settle=0` showed the phone accepting the audio offer with
  no pause at all (400 packets in four seconds, both streams closing cleanly), and with one second.
  The display service's known refusals are between two *sessions*; a second stream joining the one
  it already runs is not one. The constant stays, at zero, so that a wait has a name should a phone
  ever need one. Nobody waits for a sound; everybody waits for a picture;
- **a refusal is not fatal.** The picture stays, the panel says why, and `OpenAudioAsync` can be
  called again from the panel's own button. The audio rung is the only one in this ladder that cannot
  fail the climb;
- **the guard stays at the entry.** `AudioSession.StartAsync` still releases orphaned media sessions
  before opening its own, sparing the video session it is joining (§6). That matters more for audio
  than for video: an audio session iOS was never told to end is a system-audio capture it has not
  handed back, and while it stands the phone's own microphone is unavailable to its other apps.

### The jitter buffer, and the delay

The phone produces one frame every ten milliseconds; the endpoint asks for a period's worth whenever
it feels like it. The queue between them is primed to a **target fill** — `audioDelayMs`, 50 ms by
default — and that fill *is* the delay setting. Three rules, all of them counted:

- **under-run**: the output asks for samples that are not there. It gets silence, the count goes up
  by one, and the queue goes back to priming — one longer gap rather than a series of short ones;
- **drift**: the two clocks are not the same clock, so over minutes one of them wins. Past the target
  plus three frames (30 ms of margin), the **oldest** frame is dropped and counted. Nothing
  accumulates without bound;
- **loss**: a gap in the RTP sequence is filled with that many silent frames, up to 200 ms, so the
  timeline does not shorten. A frame the decoder refuses is queued all the same — what it holds is
  the fading tail of the overlap, which is quieter than a click. Skipping instead would play
  everything after it early, for ever.

Why 50 ms by default: a picture takes about 96 ms from the phone's screen to this one (measured,
`clock-test`), the sound's path is shorter — no decoder queue, no window to present into — so left
alone it arrives first. Fifty is roughly the difference. The ear has the last word, which is why it
is a slider from 0 to 300 ms.

### The output

Shared mode, event-driven, 48 kHz stereo 32-bit float — exactly what the decoder produces — with
`AUDCLNT_STREAMFLAGS_EVENTCALLBACK | AUTOCONVERTPCM | SRC_DEFAULT_QUALITY`. The audio engine does the
resampling and the remixing to whatever the endpoint runs at, which is why **this project ships no
resampler**. A driver that refuses those flags is answered by the engine's own mix format, read with
`GetMixFormat`, said out loud in the log, and accepted only when the samples can be laid out in it —
a mix format at another sample rate is refused rather than resampled badly.

The endpoint is the one named in the settings, or Windows's default output — **the Console role,
never Communications**: Windows keeps two defaults and on this project's own test PC the
communications one is a virtual cable feeding something else. A chosen endpoint that disappears is
answered by the default one, once, with a line in the journal. Volume and mute are a gain applied to
the samples on the way out, never the system mixer: that endpoint is shared with everything else on
the machine.

### What it counts, and where to read it

`AudioStats` — frames received, decoded, failed; packets lost and out of order; under-runs; frames
skipped; queue fill against its target; the endpoint and format in use; the sound-minus-picture skew.
Three places read it:

- the **Audio panel**, at four hertz, as one sentence: playing on *device*, buffer *n* ms, and the
  gap count if there is one;
- the **journal**, in the counters line — an `AUDIO` block on every line of a `--diagnostic` run and
  every ten seconds otherwise. That is the one to read after an unattended run;
- the **probe**, `audio-play`, which replays a capture through this very chain.

### The synchronisation diagnostic

Both streams publish RTCP sender reports carrying the phone's own NTP clock, which is the only clock
they have in common. Every five seconds the journal gets one line: the sound's total delay (the
sender report's end-to-end figure, plus the queue, plus the endpoint's own latency) against the
picture's (its own end-to-end figure, plus the decoder's queue and the decoder). The difference is
what a listener hears as lip sync, and its sign is the useful half — positive means the sound is
behind the picture, so the delay should come down.

**It corrects nothing.** The picture's last stage — the window presenting it — is on the
application's side and is not in the figure, so the skew is measured up to the decoder's output and
the picture's real delay is that much larger. The measurement is what a later version would need in
order to replace the fixed delay; this one only writes it down.

### Measured, 10 September 2026

`audio-play … --dry` — the chain in full, the sink pulling on a stopwatch at 48 kHz instead of on a
driver's event:

| capture | frames | failed | under-runs | skips | queue ms, target 50 (mean/min/max) | CPU | allocation |
|---|---|---|---|---|---|---|---|
| synthetic, 200 silent frames | 200 | 0 | 0 | 0 | 50.0 / 50.0 / 50.0 | 3.1 % | **0 B/frame** |
| `audio_default.rtp`, silent, 12.1 s | 1,211 | 0 | 0 | 0 | 43.5 / 20.0 / 70.0 | 1.4 % | **0 B/frame** |
| the 30 s music capture | 3,029 | 0 | 0 | 0 | 45.4 / 10.0 / 70.0 | 1.1 % | **0 B/frame** |

The swing in the queue column is the harness, not the chain: both ends of a dry run are paced by
`Thread.Sleep`, which is worth about a millisecond each way, and forty of those in the same direction
is the 10 ms minimum on the music capture. Live, the feed is the tunnel's own timing and the pull is
the endpoint's own event, and both are steadier than that. What the table does prove is the part no
amount of listening would show: every frame read, nothing dropped, nothing allocated per frame, and
about one per cent of a core for real-time stereo at 48 kHz.

The continuous integration runs the synthetic capture on every push, with `--delay=150` rather than
50: a shared runner can stall for longer than a 50 ms cushion, and a test that goes red for the
runner's scheduling says nothing about the chain.

### Limits

- **No automatic alignment.** See above, and §7.5.
- **No resampler.** If `AUTOCONVERTPCM` is refused *and* the engine mixes at something other than
  48 kHz, there is no sound and the log says exactly that. Not seen on this machine: the default
  output mixes at 48 kHz stereo float, so the samples are copied straight through.
- **No microphone, no Bluetooth**, here or anywhere else, and neither is coming back: §5 and §9.
- **The ear has not judged the live stream yet.** The decoder was confirmed by ear on a capture
  (§10); the chain around it has been measured but not listened to, because the application is not
  launched from the session that wrote it.
