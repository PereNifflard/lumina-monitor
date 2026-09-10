# USB video mirror — design (stage 2)

**English** · [Français](VIDEO_DESIGN.fr.md)

The phone already sends its screen as RTP through our UDP/IPv6 stack, as soon as the media gate is
open. This stage turns those packets into decoded frames, using only Windows' own tools (Media
Foundation), with no third-party library. Facts measured on 9 September 2026 (probe `stream-info`,
captures `scratchpad/ref/*.rtp`).

## Established facts

- **`MediaOffer`'s two codec banks are named backwards.** Bank payload type **123** (features
  `FLS;SW:1`) = **H.264/AVC** (RTP payload `3C 81 …` = FU-A type 28, RFC 6184). Bank **100**
  (`VRAE:0;SW:1;FLS`) = **HEVC** (payload `62 01 81 …` = FU type 49, RFC 7798). The phone picks
  bank 100 (HEVC) when both are offered.
- Offer "bank 123 only" → **H.264**, ~3.3 Mbit/s on a static screen, negotiated cap 6 Mbit/s,
  1328×2880, 60 fps advertised, NV12, SRTP disabled (stream in the clear), `KeyFrameInterval = 0`:
  **a single key frame, right at the start**.
- RTP header: version 2, extension present (profile 0x9001, one 32-bit word) — to be skipped before
  the payload.
- **RTCP**: the phone sends an SR (PT 200) every second, multiplexed on the same UDP port as the
  RTP (`RTCPSendInterval = 1`, `RTCPTimeoutInterval = 20`). **With no receiver report from our
  side, the stream stops after 20 s** (measured: 7095 packets in 35 s ≈ 19.4 s of stream, 20 SR
  received). An RR therefore has to be sent every second.
- HEVC carries a proprietary suffix on every slice NAL (`04 f0 0a c0 00 00 03 00 00 04 ec 0a b0
  03`, reference: pymobiledevice3); check whether H.264 carries one too and strip it if so.

## Decisions

1. **H.264 only**: the Media Foundation decoder is present on every Windows 10/11 install; HEVC
   depends on a Store extension. `DeviceSession` therefore offers bank 123 alone. Rename in
   `MediaOffer`: `AvcFeatures = "FLS;SW:1;"` (PT 123), `HevcFeatures = "FLS;VRAE:0;SW:1;"` (PT 100),
   fixed `VideoCodecs` enum; `SelfCheck` (Xcode template, banks 123 then 100) must stay identical
   byte for byte.
2. **Pipeline** under `LuminaMonitor.Core/Media/`:
   - `RtpPacket` (parses header, CSRC, extension, marker, seq, timestamp, SSRC, payload); RTCP is
     recognised by PT 200–206.
   - `H264Depacketizer` (RFC 6184): single NAL (1–23), STAP-A (24), FU-A (28); reassembly by seq;
     one **access unit** per marker; Annex B format (`00 00 00 01` + NAL); SPS/PPS kept and
     re-injected before every IDR; everything discarded up to the first IDR; loss detection (seq
     gap) → mark the access unit corrupt and request a key frame.
   - `RtcpSession`: parses SR/SDES/BYE; sends an RR every second (PT 201, sender's SSRC, fraction
     lost, jitter, LSR/DLSR) + SDES CNAME, to the address/port the SRs come from (mux). Sends a
     **PLI** (PT 206, FMT 1) or **FIR** (FMT 4) on demand (loss, or a late subscriber). Verify on
     the phone that the stream lasts beyond 20 s with RRs, and that a PLI does trigger a new key
     frame (SPS/PPS/IDR).
   - `H264Decoder`: Media Foundation, `CLSID_CMSH264DecoderMFT`, hand-written COM interop
     (`IMFTransform`, `IMFMediaType`, `IMFSample`, `IMFMediaBuffer`, `MFStartup`), input
     `MFVideoFormat_H264` Annex B, output **NV12**, `CODECAPI_AVLowLatencyMode = 1`, handling of
     `MF_E_TRANSFORM_STREAM_CHANGE` (size); one decode thread, bounded queue (late units dropped).
   - `Nv12ToBgra` (SIMD `System.Numerics`/`Vector`) → `VideoFrame(width, height, stride, byte[]
     Bgra, timestamp)`.
   - `MediaSession`: arms reception **before** `startmediastream` (no initial packet lost),
     exposes `FrameDecoded` (VideoFrame), `RtpPacket` (raw), statistics (pps, fps, losses, decode
     lag), `RequestKeyFrameAsync()`.
3. **Probe tools**: `stream-info` captures from the moment it is armed (before the start);
   `decode-capture <file.rtp> <n> <output.bmp>` replays a capture offline, decodes it, writes the
   n-th frame as a 24-bit BMP (written by hand, no `System.Drawing`) and reports decode throughput
   (frames/s); `mirror-test [seconds]` on the phone: a full session, live decode, counts the frames
   and writes the last one as a BMP.

## What stage 2 measured (9 September 2026, capture `capture_h264_start.rtp`)

- **Parameter sets are not in-band.** The very first RTP packet (138 bytes, *forbidden* bit set to
  1, hence never a NAL) is an ISO `avc1` sample entry containing an `avcC` box: profile 0x64,
  level 51, **SPS** `27 64 00 33 4B 04 C5 14 05 30 16 BA 6E 04 04 04 04` and **PPS**
  `28 4A E3 CB`. The entry also gives the geometry: **1328 × 2896** (not 2880). A receiver that
  ignores this packet gets an undecodable IDR.
- The next packet opens the IDR in FU-A (`3C 85`), 33 packets, 37,645 bytes; a P frame per access
  unit follows.
- **Proprietary H.264 suffix: 10 bytes, `00 00 03 00 00 05 28 0B 34 02`**, present on *every*
  slice NAL (IDR included), identical from session to session — the counterpart to the 14-byte
  HEVC suffix, though not the same pattern.
- **RTCP: the PT occupies all 8 bits of the second byte.** Masking with 0x7F turns an SR (200)
  into PT 72 and hands the control packet to the depacketizer, whose length field then reads as an
  insane sequence number.
- With an RR + SDES sent every second to the response's `SourcePort`, the stream holds: 60 s, 4143
  packets, 60 SR received, 0 loss, 3520 frames decoded (58.6 fps), last-packet-to-decoded-frame
  latency ≈ 0.4 ms.
- The MFT decoder reuses our output buffer: **`SetCurrentLength(0)` must be called before every
  `ProcessOutput`**, or the second call returns a bare E_FAIL.

## Stage 3 — lag proportional to motion (tooling, 9 September 2026)

Hypothesis: the mirror falls behind **in proportion to how much moves on screen**, because the
phone's encoder, capped around 6 Mbit/s, queues up frames when the scene changes a lot. Transport
(1 ms RTT) and decoding (< 1 ms) are already ruled out, so the measurement targets the one thing
they cannot explain: the gap between the instant a frame was captured — its RTP timestamp says —
and the instant it arrives.

- **`motion-test [seconds] [default|half|bitrate:N|rctl]`** (probe): a full session with decode,
  3 s at rest, N s of continuous vertical dragging at the centre (600 px in 500 ms, back and forth
  with no pause, 120 positions/s from the injector's queue), 3 s at rest. One line per second:
  packets/s, kbit/s, frames received and decoded, average and maximum frame size, **drift**
  (elapsed time on our side minus elapsed time per the timestamps, both counted from the first
  frame), jitter, losses, and the gap between the last frame's timestamp and now. Result: maximum
  drift at rest versus under motion. Drift climbing by several hundred ms under motion and falling
  back at rest = a queue inside the phone's encoder.
- **What the offer actually controls.** The `VCMediaNegotiationBlobVideoSettings` schema only has
  two resolution fields, `f4 customVideoWidth` and `f5 customVideoHeight`, both **absent from the
  Xcode capture**: `VideoOfferOptions.MaxWidth/MaxHeight` do send them, the phone's reaction is
  unknown. The codec banks' `ResEntry` entries are **not** a resolution table (same capability id
  50115 everywhere). There is **no** maximum-bitrate field at all: only the tier table `f9`, whose
  type-0 entries are network caps from 6 to 100 Mbit/s; `MaxBitrateKbps` trims this table. There
  is **no** frame-rate field either — hence no `FramerateCap`.
- **RCTL** (`RctlFeedback`, in `RtcpSession.cs`, disabled by default): the private channel used by
  Xcode's mirror, two RTCP APP packets (PT 204) — a 16-byte one received per frame, sent on the
  *marker* bit, and a 32-byte "RCTL" report twenty times a second whose last word's low half-word
  carries the bitrate ceiling in kbit/s. Format and cadences taken from the capture; the reading of
  the four words and the identification of the ceiling are documented, with their confidence level,
  in the file.
- The default offer stays **identical, byte for byte**, to the Xcode template (`offer-check`).


## Stage 4 — absolute latency: measurement and tuning (9 September 2026)

Drift only sees **variations** in the lag; it stays flat at ±30 ms while the mirror runs more than
a second behind in absolute terms. Two instruments were added to see the lag itself.

- **`clock-test [seconds] [--variant=…] [--out=<folder>]`** (probe): the phone displays an accurate
  clock (Safari on time.is, NTP), the probe decimates every decoded frame to a quarter, spends
  2.5 s looking for the **most-changing horizontal band** — the big digits are the only thing that
  changes once a second on an otherwise still page — then triggers on every second change, writes
  the frame as BMP and the band as PNG, named by the **PC time of that frame's last packet**. When
  the display flips to `X`, the phone's true time is `X`.000: **latency = PC time of the
  transition − X.000**. Built-in check: the intervals between transitions must equal 1000 ms.
- **`RtcpSession.PipelineMs`**: the RTCP sender report publishes the pair (phone clock, matching
  media timestamp), which gives the phone's time for any RTP timestamp and therefore the
  **timestamp-to-arrival** lag. The clock the phone puts there is not wall time (it is offset by a
  constant, ~4 h 31 in this campaign), so the absolute value is meaningless; its **differences**
  are accurate to the millisecond, and that is what is used to compare variants.

### What the measurement says

| Lever tried | Effect on latency |
| --- | --- |
| `default` (Xcode negotiation, no RCTL) | 1.55 – 1.78 s (5 sessions) |
| RCTL loop, cap at 2000 / 4000 / 6000 / 60001 kbit/s | 1.71 – 1.75 s — no effect |
| w4 on the media clock (OWRD ≈ 0) vs. on our own clock | no effect |
| RCTL reports at 60 Hz instead of 20 Hz | no effect |
| `AVCMediaStreamNegotiatorAccessNetworkType` 0, 1, 2, 3 | no effect (1.72 s at 0 as at 1) |
| `AVCMediaStreamNegotiatorTransportProtocolType` 0, 1, 2, 3 | no effect |
| `clientSupportedFeatures` 0, 140, 141, 255 | no effect (0 and 255 accepted like 140) |
| `ltrpEnabled`, `fecEnabled=0`, `allowRTCPFB`, `tilesPerFrame=4` | accepted, no effect |
| `endpoint:Mac16,11` instead of `Mac15,9` | accepted, no effect |
| `customVideoWidth/Height` (664×1448), tiers ≤ 2000 kbit/s | no effect (1.68 / 1.71 s) |
| **a tap on the phone's screen** | **1.70 s → 1.00 s**, then climbing back over ~2 min |

The returned `streamConfig` is **identical** across all these variants: `JitterBufferMode = 1`,
`VideoStreamMode = 4`, `TXMinBitrate = 333000`, `TXMaxBitrate = 6000000`, `KeyFrameInterval = 0`,
`RateAdaptationEnabled = true`, `RTCPTimeoutInterval = 20`, `RxPayloadType = 123`. No lever in the
offer or in the `startmediastream` envelope moves it by a single field.

### Where the second is hiding

`PipelineMs` — the **timestamp-to-arrival** lag, which covers transport and everything the phone
does after timestamping a frame — sits within **40 ms across twenty sessions**, while the latency
read off the pixels ranges from 1.00 s to 1.78 s in those same sessions and **does not correlate**
with it. The lag is therefore neither in the tunnel, nor in the encoder after timestamping, nor in
our own pipeline (the PC timestamp is that of the last packet, before any decoding): it sits
**upstream of the timestamp**, between the pixels shown on the phone and the capture that stamps
them.

The tap confirms it: 1.00 s measured twice, on two separate occasions right after a tap (once on
the Safari icon, once on an empty area of the page), 1.22 s ninety seconds later, 1.55 – 1.78 s
across ten sessions with no tap. This is the signature of an idle slowdown on the iOS side that
touch wakes up. **It is not a lever in the protocol** — nothing in the negotiation reaches it.

### Decision

**The default does not change**: `StreamTuning.Default` stays the Xcode negotiation, byte for
byte, with no RCTL loop, because none of the twenty variants measured goes lower, and the one that
does (touching the screen) is not a setting. `offer-check` therefore stays green, untouched. All
the levers remain available via `--variant=`, combinable with `+`, for the next campaign.

### What remains unexplained

- **No variant goes below 1 s**, and the 200 ms target is not met: the measured floor is 1.00 s,
  right after touching the screen.
- This one-second floor has not been located. It sits upstream of the RTP timestamp, but the
  measurement cannot tell apart *the page being repainted late on the phone's own screen* from
  *the capture stamping an already-displayed frame late*. A clock iOS cannot slow down (a native
  animated view, not a web page) would be needed to settle it.
- The pixel measurements carry an unknown constant: the PC's clock was **154 ms ahead** of NTP
  during the campaign (`w32tm /stripchart`), so the true latencies are the figures above **minus
  154 ms**. The table gives them raw, as read.
- The display service goes deaf ("no SETTINGS from the phone in 3 s") after six to eight media
  sessions in close succession; an `unmount` is enough to wake it back up. Unrelated to the
  variants: it happens on the default negotiation just as well.

## Stage 5 — the decoder's buffer (9 September 2026)

Windows' software decoder was holding frames back before rendering one: the mirror was lagging by
that much, and **no internal counter could see it** — every frame carries the arrival time of its
own packets, so the "wire / queue / decode" stages stay a few milliseconds while the screen shows
half a second of the past. `H264Decoder.InFlight` (units in − frames out) is the counter that
reveals it.

### What was holding the frames back

1. **Low-latency mode was only set through one door.** `CODECAPI_AVLowLatencyMode` on `ICodecAPI`
   and `MF_LOW_LATENCY` on `IMFTransform.GetAttributes()` are **the same GUID** `9c27891a-…` via
   two paths, and this decoder does not open both. Both are set, HRESULT checked
   (`LowLatencySet`): **48 → 12 frames held**.
2. **The phone's SPS says nothing about reordering.** Profile 100, level 5.1, 1328×2896 =
   83 × 181 = 15,023 macroblocks; `MaxDpbMbs(5.1) = 184,320`, so DPB = min(184320 / 15023, 16) =
   **12**. The VUI exists (colour description present) but **`bitstream_restriction_flag = 0`**:
   for lack of a declaration, the decoder assumes the worst the level allows and waits for twelve
   frames. Yet the stream has **no B frames at all** — measured: 1 I slice, 440 P slices, 0 B
   across a 441-unit capture (`decode-capture`, "slice types" line).

### The fix: SPS rewriting

`Media\SpsRewriter.cs` reads the SPS bit by bit (exp-Golomb ue/se, stripping and re-inserting the
anti-emulation `00 00 03` bytes), copies each field unchanged up to
`bitstream_restriction_flag` — the last field of a VUI, so from there on the tail is ours to write
— and writes the restriction: `motion_vectors_over_pic_boundaries_flag = 1`,
`max_bytes_per_pic_denom = 0`, `max_bits_per_mb_denom = 0`,
`log2_max_mv_length_horizontal = vertical = 16`, **`max_num_reorder_frames = 0`**,
`max_dec_frame_buffering = max_num_ref_frames` (4 here). Both branches are handled: no VUI (one is
written, eight flags at zero then the restriction) and a VUI present with or without the
restriction.

`H264Depacketizer` rewrites the SPS **at the moment it reads it** — inside the `avcC` of the first
packet, the only place this stream puts one — and it is the rewritten SPS that gets prefixed to
every IDR. An unreadable SPS passes through unchanged (`SpsRewriteFailures`) rather than being
dropped.

Phone's SPS: `27 64 00 33 4B 04 C5 14 05 30 16 BA 6E 04 04 04 04` (17 B) → rewritten to
`27 64 00 33 4B 04 C5 14 05 30 16 BA 6E 04 04 04 0F 08 84 65 80` (21 B).

`sps-selftest` (probe, offline) checks against three cases — the real SPS, the same with the VUI
stripped, and an already-rewritten SPS — that profile, level, chroma, dimensions,
`num_ref_frames`, `frame_mbs_only_flag` and the crop are unchanged, that the restriction is there
with zero reordering, and that a **second pass changes not a single byte** (proof that the reader
and the writer agree). A PPS presented as an SPS is rejected.

### Measurements

| Stage | Frames held | Absolute latency |
| --- | --- | --- |
| Low-latency through a single door | 48 | ~2 s (eyeballed) |
| `MF_LOW_LATENCY` + `CODECAPI_AVLowLatencyMode` | 12 | **484 ms** (468 / 481 / 492 / 494) |
| `CODECAPI_AVDecNumWorkerThreads = 1` on top | 12 | not measured — abandoned |
| **+ SPS rewriting** | **0** | **96 ms** (90 / 96 / 97 / 100) |

Absolute latency measured against the **native stopwatch of the Clock app**: a `tap` starts it
(instant T0 = PC time of the tap + 65 ms hold), then `mirror-test 12 --suite=<folder>` writes one
frame per second named by the PC time of its last packet; latency = PC time of the frame − T0 −
value read on the stopwatch. This measurement **depends on no clock synchronisation** (the
stopwatch is a duration, not a time), unlike `clock-test` which compares PC time against the
phone's NTP time. Cross-check: the before/after gap equals 388 ms, exactly 12 frames at 31 fps —
the two measurements agree.

**Consequence for stage 4**: the unexplained second in the previous table was measured on
**time.is in Safari**. On a native view, the floor is 96 ms. That is the check the "What remains
unexplained" section was asking for: the lag was in a web page's slowed-down repaint, not in the
pipeline.

### Paths ruled out

- **`CODECAPI_AVDecNumWorkerThreads = 1`**: the MFT supports it (`IsSupported` = S_OK) but
  **refuses the VT_UI4 the documentation announces** (`E_INVALIDARG`) and only accepts **VT_I4**.
  Once set, it leaves the 12 held frames untouched and drops decode throughput from ~450 to ~271
  frames/s. No gain, half the throughput: not kept.
- **Hardware decoder via `MFTEnumEx`** and **`MFT_MESSAGE_COMMAND_DRAIN` after every unit**: not
  tried, since the SPS rewrite already brought the buffer to zero. The per-unit drain would in any
  case have discarded the reference frames of a stream that has only one key frame for the whole
  session.

### What the probe and the app display

- `mirror-test`: `decoder: N frame(s) held, low latency YES|NO`.
- `decode-capture`: the same line before flushing, plus `slice types: I … P … B …`.
- App, VIDEO block of the statistics line: `decoder holds N  low latency yes|NO`
  (`MediaStats.DecoderInFlight`, `MediaStats.DecoderLowLatency`).
## Acceptance criteria

- A fresh capture with SPS/PPS/IDR at the start; `decode-capture` produces a readable BMP frame (open it to check).
- `mirror-test 60`: continuous stream beyond 20 s thanks to the RRs; ≥ 30 frames/s decoded at 1328×2880; decode
  latency (last packet of a frame arrives → frame decoded) measured and displayed, target < 30 ms.
- No change to the CoreDevice messages other than the choice of banks; `offer-check` still green.
