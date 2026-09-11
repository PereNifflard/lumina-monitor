**English** · [Français](NOTICE.fr.md)

# NOTICE — the data in this folder is not under this repository's MIT license

This folder, `src/LuminaMonitor.Core/Media/Aac/Tables/`, holds the numeric tables an AAC-ELD
decoder needs and cannot invent: the twelve Huffman codebooks, the scalefactor band edges, the TNS
ceilings, and the low-delay filterbank window. **These tables are not covered by the MIT license**
at the repository's root ([`LICENSE`](../../../../../LICENSE)). They are governed by the MPEG
software module notice reproduced in full below, which travels with them under the terms of that
same notice.

## Where the numbers come from

| | |
|---|---|
| Document | ISO/IEC 14496-5:2001/Amd 43:2018, *Coding of audio-visual objects — Part 5: Reference software* — Amendment 43, electronic insert `14496-5_Amd43_inserts.zip` |
| Source | <https://standards.iso.org/iso-iec/14496/-5/ed-2/en/amd/43/> — the ISO Standards Maintenance Portal, free of charge, no account required |
| Retrieved | 10 September 2026 |
| Subtree | `audio/natural/mp4AudVm_Rewrite/src_tf/` — the MPEG-4 Audio reference decoder, AAC-(E)LD included |
| ELD provenance | the ELD decoder entered that tree with ISO/IEC 14496-5:2001/Amd 24:2009, "Reference software for AAC-ELD" |

Every number in this folder was transcribed, by script or by hand, from that reference software's C
source — `hufftables.c`, `decdata.c`, `win480LD.h`, `win512LD.h` — into C#. Nothing from FFmpeg,
FDK-AAC or faad2 was consulted. How each table was checked against its source is in
[`docs/AAC_ELD_TABLES.md`](../../../../../docs/AAC_ELD_TABLES.md); this file is only about the
licence the numbers carry.

## The notice, verbatim

Every reference-software file these tables were read from carries the notice below —
`TablesProvenance.MpegSoftwareModuleNotice` in this repository's code, character for character.
Only the line breaks and the per-file list of original developers differ between the source files:

- `win480LD.h` and `win512LD.h` name **Fraunhofer IIS (2006)**.
- `hufftables.c` and `decdata.c` name **AT&T, Dolby Laboratories and Fraunhofer Gesellschaft IIS
  (1996)**, edited by **Ali Nowbakht-Irani** (Fraunhofer IIS) and by **Yoshiaki Oikawa** and
  **Mitsuyuki Hatanaka** (Sony Corporation) respectively.

> "This software module was originally developed by AT&T, Dolby Laboratories, Fraunhofer
> Gesellschaft IIS and edited by [see the per-file credits] in the course of development of
> the MPEG-2 AAC/MPEG-4 Audio standard ISO/IEC 13818-7, 14496-1,2 and 3. This software module is an
> implementation of a part of one or more MPEG-2 AAC/MPEG-4 Audio tools as specified by the MPEG-2
> AAC/MPEG-4 Audio standard. ISO/IEC gives users of the MPEG-2 AAC/MPEG-4 Audio standards free
> license to this software module or modifications thereof for use in hardware or software
> products claiming conformance to the MPEG-2 AAC/MPEG-4 Audio standards. Those intending to use
> this software module in hardware or software products are advised that this use may infringe
> existing patents. The original developer of this software module and his/her company, the
> subsequent editors and their companies, and ISO/IEC have no liability for use of this software
> module or modifications thereof in an implementation. Copyright is not released for non MPEG-2
> AAC/MPEG-4 Audio conforming products. The original developer retains full right to use the code
> for his/her own purpose, assign or donate the code to a third party and to inhibit third party
> from using the code for non MPEG-2 AAC/MPEG-4 Audio conforming products. This copyright notice
> must be included in all copies or derivative works." Copyright(c)1996, Copyright(c)2006.

## What the notice says, stated plainly

- The licence is **free**, but conditional: it covers this module "or modifications thereof" for
  products **claiming conformance to the MPEG-2 AAC/MPEG-4 Audio standards**.
- Copyright is **not released** for non-conforming products.
- **The notice must travel with every copy or derivative work** — which is why it is reproduced
  above in full rather than summarized.
- Users are **warned that use may infringe existing patents**. AAC-ELD, the low-delay extension
  used here, is 2008 Fraunhofer work; the base AAC patents have largely expired, AAC-ELD's have not
  necessarily.
- A second, separate condition comes from ISO's own licence for its electronic inserts, which the
  download page states as use "in their original format without any modifications" — while this
  module notice expressly allows "modifications thereof." A transcription from C to C# sits between
  those two texts. This is stated here as a fact, not resolved as a legal question.

## Decision taken for this project — 10 September 2026

The tables stay, isolated in this one folder, under the notice above rather than under this
repository's MIT terms, and the repository says so in writing (this file). The facts behind that
decision:

- Nothing in these files runs anything foreign on your PC: they are numeric constants — codeword
  lengths, band edges, filter coefficients — read by index, exactly like every other table in this
  repository. The project's security promise elsewhere ("no third-party code runs") is unaffected;
  what changes is licence purity, not what executes: this repository's own code stays MIT, and this
  one folder of normative data carries the ISO/MPEG notice above instead.
- AAC-ELD remains covered by active patents (Fraunhofer IIS, licensed through the Via Licensing
  pool) regardless of where the numbers were read from: reading them out of the paid standard, or
  out of FFmpeg, would not have made that go away.
- This project is free and non-commercial — the situation of any open-source AAC decoder. Anyone
  wanting to use this code or these tables in a commercial product needs their own patent licence;
  nothing here obtains one on their behalf.

No legal advice is offered here, or anywhere else in this repository — only the facts above and the
source they were read from.
