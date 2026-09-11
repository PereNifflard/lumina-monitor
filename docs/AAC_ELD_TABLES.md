# The AAC-ELD tables — what they are, where they come from, how they are checked

**English** · [Français](AAC_ELD_TABLES.fr.md)

Written 10 September 2026. The subject is the *data* an AAC-ELD decoder needs and cannot invent:
Huffman codebooks, scalefactor band edges, TNS ceilings, and the window of the low delay
filterbank. The code that would use them did not exist yet when this page was first written; it
does now, written the same evening once these tables were assembled — see
[`docs/AUDIO.md`](AUDIO.md), "The home-grown decoder." This page stays about the data underneath
it, and about the one licensing decision that data required (§2).

**The conclusion in one line:** every table needed to decode the phone's AAC-ELD **without SBR**
was obtained from **ISO's own freely downloadable reference software**, and the critical one — the
low delay window — is proven by putting a signal through analysis and synthesis and getting it back
to one part in 10⁸. One contradiction remained in the phone's own signalling: its
AudioSpecificConfig says 512 samples per frame, the wire says 480. Both table sets are in the
repository for that reason, and §5 below now carries the verdict between them, settled by decoding
a real capture: 480.

## 1. Where the numbers come from

| | |
|---|---|
| Document | **ISO/IEC 14496-5:2001/Amd 43:2018**, *Coding of audio-visual objects — Part 5: Reference software*, electronic insert `14496-5_Amd43_inserts.zip` |
| Downloaded from | <https://standards.iso.org/iso-iec/14496/-5/ed-2/en/amd/43/> — the ISO Standards Maintenance Portal, **free of charge**, no account |
| Retrieved | 10 September 2026 |
| Subtree used | `audio/natural/mp4AudVm_Rewrite/src_tf/` — the MPEG-4 Audio reference decoder, AAC-(E)LD included |
| ELD provenance inside it | the ELD decoder entered that tree as **ISO/IEC 14496-5:2001/Amd 24:2009, "Reference software for AAC-ELD"** |

Part 5 of the standard *is* the reference software, and it is normative: a conforming decoder fed a
conforming bitstream must produce what it produces. ISO gives the electronic inserts away, and the
insert of Amendment 43 happens to carry the whole audio tree rather than only the amendment's own
subject, which is why an amendment about ALS levels and SBR is the vehicle for the ELD window.

Two other roads were checked and rejected:

- **ISO/IEC 14496-3 itself** (the audio standard, where these appear as normative tables) is sold,
  not published. A copy circulating on the web was opened, found to be an encrypted PDF with an
  owner password, and left alone: nothing here needed circumventing it.
- **FFmpeg, faad2 and similar** were not consulted, and **FDK-AAC** was not opened at all. Nothing
  in this folder passed through a software implementation other than ISO's own.

## 2. The licence — decision taken, 10 September 2026

Every reference-software file carries the MPEG software module notice, reproduced verbatim in
`TablesProvenance.MpegSoftwareModuleNotice` and now also, in full, in
[`Tables/NOTICE.md`](../src/LuminaMonitor.Core/Media/Aac/Tables/NOTICE.md). In substance:

- ISO/IEC gives a **free licence** to the module "or modifications thereof" for use in hardware or
  software products **claiming conformance to the MPEG-4 Audio standard**;
- copyright is **not** released for non-conforming products;
- the notice **must travel with every copy or derivative work**;
- users are warned the code **may infringe existing patents** (AAC-ELD is 2008 Fraunhofer work;
  base AAC patents have largely expired, ELD's have not necessarily).

So the tables in `src/LuminaMonitor.Core/Media/Aac/Tables/` are **not MIT**. They are third-party
data under that notice, sitting in an MIT repository, and the notice ships with them (that folder's
`NOTICE.md`). A second condition pulls slightly the other way: ISO's download page for the
electronic inserts states use "in their original format without any modifications", while the
module notice expressly allows modifications — and a transcription from C to C# is a modification
of format. This grey area is stated as a fact below and left unresolved as a legal question; this
project offers no legal advice, here or elsewhere.

**The decision (option A):** the tables stay, isolated in this one folder, under the notice above —
not under this repository's MIT terms — and the repository says so in writing, in
[`Tables/NOTICE.md`](../src/LuminaMonitor.Core/Media/Aac/Tables/NOTICE.md). The facts that went into
it:

1. Nothing in these files runs anything foreign on the PC: they are numeric constants read by
   index, like every other table here. The project's rule "no third-party code runs" is unaffected
   by this decision — what changes is licence purity, not what executes. This repository's own code
   stays MIT; this one folder of normative data carries the ISO/MPEG notice instead.
2. "Claiming conformance to MPEG-4 Audio" is the licence's condition, and a decoder for the phone's
   own ELD stream is exactly that claim — made deliberately, with the notice attached, rather than
   avoided.
3. AAC-ELD is covered by active patents (Fraunhofer IIS, licensed through the Via Licensing pool),
   regardless of where the numbers were read from — buying ISO/IEC 14496-3 or reading FFmpeg's
   tables would not have made that go away. This project's use is free and non-commercial, the
   situation of any open-source AAC decoder; anyone wanting a commercial product built on this code
   or these tables needs their own patent licence, which nothing here supplies.

Both alternatives considered and set aside were worse for a public MIT repo: buying ISO/IEC 14496-3
(≈ CHF 200) gives the same numbers under a licence that forbids redistribution outright, and
FFmpeg's tables are LGPL, a licence on *code* the project has sworn off.

## 3. What each file holds

All of it is `internal static`, data only, no logic beyond an index lookup.

| File | Contents | Reference-software source |
|---|---|---|
| `TablesProvenance.cs` | the document, URL, date, and the MPEG notice verbatim | — |
| `HuffmanTables.cs` | the twelve books' shape: dimension, LAV, signed or not, longest codeword; the lookup by bitstream codebook number | `huffinit.c` (`hufftab()` calls), `interface.h` (`HUFnSGN`) |
| `HuffmanCodebooks1To6.cs` | codeword lengths and values, books 1–6 (81 entries each) | `hufftables.c`, `book1`…`book6` |
| `HuffmanCodebooks7To11.cs` | books 7–8 (64), 9–10 (169), 11 (289) | `hufftables.c`, `book7`…`book11` |
| `HuffmanScalefactorBook.cs` | the scalefactor book, 121 entries, index − 60 is the difference | `hufftables.c`, `bookscl` |
| `ScalefactorBands.cs` | `swb_offset` for 480 and 512 lines at the three low delay band layouts, with a leading 0 added | `decdata.c`, `sfb_48_480`…`sfb_24_512`, mapped by `samp_rate_info` |
| `TnsTables.cs` | the low delay TNS band ceilings per sampling frequency and frame length, and the order ceiling | `decdata.c`, `tns_max_bands_tbl_low_delay`, `tns_max_order()` |
| `EldWindow.cs` | which window goes with which frame length, and the quarter-frame synthesis shift | `imdct.c` |
| `EldWindow480.cs` | 1920 coefficients | `win480LD.h`, `WIN480LD` |
| `EldWindow512.cs` | 2048 coefficients | `win512LD.h`, `WIN512LD` |

Notes worth knowing before using them:

- **48 kHz covers five sampling frequencies.** The reference software points indices 0–4 (96, 88.2,
  64, 48, 44.1 kHz) at the same pair of band tables. 32 kHz has its own, and every index from
  24 kHz down shares another. All three pairs are here.
- **The band tables gained a leading zero.** The reference software lists band *ends*; here band `b`
  is `[offsets[b], offsets[b+1])` and the last entry is the frame length.
- **Precision of the window is eight decimals**, as the reference decoder prints and uses them. The
  same insert also holds `win512LD2.h`, the 512 window to ten significant digits, which nothing in
  that decoder includes; it reconstructs to 4.3 × 10⁻¹¹ instead of 1.5 × 10⁻⁸ — proof that the two
  files hold one window and that the residual is the printing, not the mathematics. 10⁻⁸ is 157 dB
  down, so the copy the reference decoder uses is the copy here.

## 4. How they are checked — `aac-tables-selftest`

    LuminaMonitor.UsbProbe aac-tables-selftest

Offline, no phone, exit code 0 only if all 28 checks pass. What each family is checked against:

- **Huffman books.** Entry count against dimension and LAV — (2·LAV+1)^dim signed, (LAV+1)^dim
  unsigned. Every codeword fits its own length. Kraft's sum computed in integers over 2^longest and
  required to be **exactly 1** (a complete prefix code). No codeword a prefix of another, tested by
  truncating every code to every shorter length and looking it up. Finally each codeword is decoded
  bit by bit through a table built from the data and must land on its own index. All twelve books
  pass all five, Kraft = 1 exactly for every one of them.
- **Band tables.** Start at 0, strictly increasing, every band a multiple of four lines wide, last
  edge equal to the frame length, band count as announced.
- **TNS ceilings.** Every ceiling fits inside the band table it applies to; the order ceiling is
  sane. At 32 kHz the ceiling equals the band count exactly (37 of 37), which is the tightest case
  and passes.
- **The ELD window — the real test.** The low delay filterbank is implemented in the probe from its
  definition, as a direct sum with no fast algorithm:

      analysis:   X[k] = -2 · Σ(i = 0…4M-1) xw[i] · cos(π/M · (i - 2M + (1-M)/2) · (k + ½))
      synthesis:  y[n] = (-1/M) · Σ(k = 0…M-1) X[k] · cos(π/M · (n + (1-M)/2) · (k + ½))

  with `M` the frame length, `xw` the four-frame input block times the window **reversed**, and `y`
  windowed in the window's own order then overlap-added one frame at a time, read a quarter frame in.
  A noise, a 997 Hz sine and an impulse train go through analysis then synthesis; the test searches
  for the lag that lines the output up with the input and reports the worst error over the
  steady-state region.

  | frame | measured delay | worst error, noise / sine / impulse |
  |---|---|---|
  | 480 | 360 samples | 1.33e-8 / 1.15e-8 / 3.96e-9 |
  | 512 | 384 samples | 1.46e-8 / 1.05e-8 / 2.94e-9 |

  The ceiling is 5 × 10⁻⁸, three times the floor. That is where the test has teeth: moving **one**
  coefficient of the 512 window by 10⁻⁶ — one wrong digit in the sixth decimal, the smallest
  transcription error worth the name — takes the residual to 1.5 × 10⁻⁷ and fails the run with exit
  code 9. This was done on purpose and then undone; a test that cannot fail proves nothing.

The delay of 0.75 · M samples is a property of the framing this test uses, not a figure copied from
the standard, and it is asserted so that a change of convention shows up as a failure rather than as
a decoder that is quietly late.

On top of the self-test, every array in the repository was compared value by value against the
reference-software file it came from — 1920 + 2048 window coefficients, 2724 Huffman numbers, 212
band edges, 32 TNS ceilings, zero differences. The transcription was done by script for the long
tables and by hand only for the band tables, which is why that comparison exists.

## 5. 480 or 512 samples per frame — the verdict

The phone's AudioSpecificConfig is four bytes, `F8 E6 40 00`. Read bit by bit as
ISO/IEC 14496-3 §1.6.2.1 and the ELDSpecificConfig lay them out (the field order confirmed against
`advanceELDspecConf()` in the reference software):

| bits | field | value |
|---|---|---|
| 0–4 | `audioObjectType` | 31 → escape, read six more |
| 5–10 | `audioObjectTypeExt` | 7 → **object type 39, ER AAC ELD** |
| 11–14 | `samplingFrequencyIndex` | 3 → **48 000 Hz** |
| 15–18 | `channelConfiguration` | 2 → **stereo** |
| 19 | `frameLengthFlag` | **0** |
| 20–22 | the three resilience flags | 0, 0, 0 |
| 23 | `ldSbrPresentFlag` | **0 → no SBR** |
| 24–27 | `eldExtType` | 0 → `ELDEXT_TERM`, config ends |
| 28–31 | padding | 0 |

The parse is sound: every field lands on a legal value and the extension type terminates exactly at
the end of the four bytes, which a misalignment by one bit would not do.

**What `frameLengthFlag` means is not ambiguous.** The reference software says the same thing in
three places — `dec_tf.c:300`, `confldsbr.c:212`, `streamfile_diagnose.c:417` — all of them
`frameLengthFlag ? 480 : 512`. So the config asks for **512**.

**The wire says 480.** Re-measured here on `audio_default.rtp`, the capture in this repository:
1211 packets of payload type 101 over 12.094 s; the RTP timestamp advances by **exactly 480 every
single packet** (1210 of 1210 steps), which at 100.05 packets/s gives 48 023 ticks/s — a 48 kHz
sample clock. Had the frames been 512 samples, the phone would have sent 93.75 packets/s and the
same 480-tick step would have read as a 45 000 Hz clock. It does not.

**The verdict.** Believe the wire for the duration: **480 samples, 10 ms per frame**, and treat the
`frameLengthFlag` as a bug in Apple's encoder.

That silent capture alone could not say which *table set* the bitstream is coded with. Every one of
its 1211 payloads is the same four bytes, `00 68 34 00`, which read as a complete and **empty** ELD
stereo frame: six bits of `max_sfb` at zero, two bits of `ms_mask_present` at zero, then for each of
the two channels a `global_gain` of **104** and a `tns_data_present` at zero — 26 bits of frame and
six bits of padding, exactly four bytes. That reading was a hypothesis when this page was first
written, the ELD field order not having been read out of the standard yet; it no longer is one —
`EldSyntax.cs` now implements that order, and it agrees with the reading above (swapping `max_sfb`
with `ms_mask_present` would have given the same values regardless, the first byte being zero either
way). What was never a hypothesis is that both channels land on the same global gain, which is what
a silent stereo frame looks like. And `max_sfb = 0` means **no band is coded at all** — the frame
never touches a band table, so it cannot tell 35 bands from 36.

Worth recording for whoever reads the parser: ELD has no `ics_info()`. Where AAC-LC would send one,
the reference decoder reads a bare **six-bit `max_sfb`** (`LEN_MAX_SFBL`, `huffdec2.c:1658`, the
branch taken when the stream is ELD), and `max_sfb == 0` sets the total band count to zero, which is
how a frame carries silence in four bytes.

That is why **both table sets are in the repository**, and why the decoder that was subsequently
written takes the frame length as a parameter (`AudioSpecificConfig.WithFrameLength`) rather than
assuming one.

### Settled by a real capture — 10 September 2026, 20:40

The test proposed above was run once a capture with real sound existed: 30 s with a video playing on
the phone, 3029 frames of payload type 101, 372 bytes per frame on average, 0.30 Mbit/s, 101
packets/s, no loss — and, unlike the silent capture, no clean 480.0-per-packet RTP timestamp step.
Decoding it twice, once per table set:

| frame length | result |
|---|---|
| **480** | **3029/3029 decoded, 0 failures.** 8,804,260 of 8,814,776 bits used (99.9%), 2,906.7 bits/frame on average, 0 frame ending early, late, or on non-zero padding. |
| **512** | **63 decoded, 2,966 failed.** First failure at frame 1: a section claims 8 bands from band 26, past `max_sfb` 34 — the 512 band table does not match this stream. |

480 is the wire's truth, not only for the silent capture but for a stream carrying real content. The
`frameLengthFlag` in the phone's AudioSpecificConfig — which asks for 512 — is a bug in Apple's
encoder's signalling, not a hint about which tables to load.

What this proves: the bit reader is exact — a syntax error at 512 would desynchronise the section
data almost immediately, which is exactly what happened, and at 480 nothing did across 3029 frames.
What it does not prove by itself: that the filterbank's phase convention is the one Apple's encoder
used. Only listening to the decoded output says that, and it did, on 10 September 2026: the decoded
capture sounds like the video that was playing — see [`docs/AUDIO.md`](AUDIO.md), "The home-grown
decoder."

## 6. What is missing, and what is not needed

Not needed — a formula, not a table:

- **inverse quantisation**, `|x|^(4/3)`: computed;
- **TNS filter coefficients**: dequantised by `sin(coef / iqfac)` with
  `iqfac = ((1 << (coefRes-1)) ∓ 0.5) / (π/2)`, then the usual reflection-to-LPC recursion — the
  formula is in `tns.c`, there is no table;
- **scalefactor gains**, `2^(0.25 · (sf − 100))`: computed;
- **PNS** (perceptual noise substitution): the noise level comes from the scalefactor and the
  generator itself is not normative, so no table;
- **intensity stereo**: the position is a scalefactor, no table;
- **window shapes**: ELD has one window, no shape bit, no short blocks, no window sequence — which
  is why no sine or KBD table is here.

Genuinely missing:

- **The ELD bitstream syntax** — the order of the fields in `ELDraw_data_block()`, the widths of the
  section, scalefactor and TNS fields, the escape rules — was genuinely missing when this page was
  written; this task was tables, not decoder logic. It no longer is missing: it was read out of the
  same insert (`decoder_tf.c`, `huffdec2.c`, `tns.c`, same licence) and written into
  `src/LuminaMonitor.Core/Media/Aac/` the same evening. See
  [`docs/AUDIO.md`](AUDIO.md), "The home-grown decoder."
- **SBR tables.** Not needed while `ldSbrPresentFlag` is 0, and it is 0. If the phone ever offers
  ELD with SBR, that is a second set of Huffman books and frequency band tables to fetch.
- **ELD extensions** (LD-MPS, SAOC). `eldExtType` terminates immediately, so there is nothing to
  decode and nothing to fetch.
- **A bit-exact conformance vector.** ISO sells conformance streams (part 4); the self-test proves
  the tables are internally consistent and that the filterbank reconstructs, which is a strong
  statement about the data but not a decoded-audio comparison against a reference decoder.

## 7. Repeating the retrieval

Download the insert from the URL in §1, unzip it, and the four source files are under
`audio/natural/mp4AudVm_Rewrite/src_tf/`: `hufftables.c`, `decdata.c`, `win480LD.h`, `win512LD.h`.
Nothing else in this repository depends on that download — the tables are transcribed, the insert is
not vendored, and it was never placed inside the repository.
