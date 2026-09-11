namespace LuminaMonitor.Core.Media.Aac.Tables;

/// <summary>
/// Where every number in this folder comes from, and under what licence.
/// </summary>
/// <remarks>
/// An AAC-ELD decoder cannot be written from first principles: the Huffman
/// codebooks, the scalefactor band edges, the TNS limits and above all the low
/// delay window are <em>data defined by ISO/IEC 14496-3</em>, not something a
/// formula produces. This project writes every line of its own code, but it
/// cannot invent those numbers, so the question is only ever which copy of them
/// is legitimate to read.
///
/// <para>The copy used here is the one ISO itself gives away: the MPEG-4
/// reference software, part 5 of the same standard. ISO's Standards Maintenance
/// Portal serves the electronic inserts of ISO/IEC 14496-5 free of charge, and
/// the insert of Amendment 43 happens to ship the whole MPEG-4 Audio reference
/// software tree — AAC-(E)LD included, since the ELD decoder entered that tree
/// with Amendment 24:2009, "Reference software for AAC-ELD". Nothing was taken
/// from FFmpeg, FDK-AAC or faad2; no such source was consulted.</para>
///
/// <para><b>What this costs.</b> The reference software is not MIT. Every file
/// of it carries the MPEG software module notice reproduced below, which gives a
/// free licence for use in products claiming conformance to the MPEG-4 Audio
/// standard, disclaims patents, and requires that the notice travel with every
/// copy or derivative work. The tables in this folder are therefore a
/// derivative of that data and carry that notice, not this repository's MIT
/// terms. ISO's own licence for electronic inserts adds a second condition —
/// the inserts may be used "in their original format without any modifications
/// for the purposes specified in their respective ISO standard(s)" — which a
/// transcription into C# arguably steps outside of, while the software module
/// notice expressly allows "modifications thereof". Reconciling the two is the
/// project owner's call, not this file's; <c>docs/AAC_ELD_TABLES.md</c> states
/// the problem plainly so the call can be made with the facts in hand.</para>
/// </remarks>
internal static class TablesProvenance
{
    /// <summary>The exact document the tables were read from.</summary>
    public const string Document =
        "ISO/IEC 14496-5:2001/Amd 43:2018, Information technology — Coding of audio-visual objects — "
        + "Part 5: Reference software — Amendment 43, electronic insert 14496-5_Amd43_inserts.zip";

    /// <summary>Where that insert was downloaded from, free of charge.</summary>
    public const string Source = "https://standards.iso.org/iso-iec/14496/-5/ed-2/en/amd/43/";

    /// <summary>When it was downloaded, so a later reader can check for a newer insert.</summary>
    public const string Retrieved = "2026-09-10";

    /// <summary>The subtree inside the insert that holds the AAC-(E)LD decoder.</summary>
    public const string SourceTree = "audio/natural/mp4AudVm_Rewrite/src_tf/";

    /// <summary>The amendment that first published the ELD reference software.</summary>
    public const string EldAmendment = "ISO/IEC 14496-5:2001/Amd 24:2009, Reference software for AAC-ELD";

    /// <summary>
    /// The MPEG software module notice, verbatim from the reference-software
    /// files these tables were read from.
    /// </summary>
    /// <remarks>
    /// Reproduced because it says so itself: "This copyright notice must be
    /// included in all copies or derivative works." Only the line breaks and the
    /// per-file list of original developers differ between the files used here —
    /// <c>win480LD.h</c> and <c>win512LD.h</c> name Fraunhofer IIS (2006),
    /// <c>hufftables.c</c> and <c>decdata.c</c> name AT&amp;T, Dolby
    /// Laboratories and Fraunhofer Gesellschaft IIS (1996), edited by Ali
    /// Nowbakht-Irani (Fraunhofer IIS) and by Yoshiaki Oikawa and Mitsuyuki
    /// Hatanaka (Sony Corporation) respectively.
    /// </remarks>
    public const string MpegSoftwareModuleNotice =
        "\"This software module was originally developed by AT&T, Dolby Laboratories, "
        + "Fraunhofer Gesellschaft IIS and edited by [see the per-file credits] in the course of "
        + "development of the MPEG-2 AAC/MPEG-4 Audio standard ISO/IEC 13818-7, 14496-1,2 and 3. "
        + "This software module is an implementation of a part of one or more MPEG-2 AAC/MPEG-4 Audio "
        + "tools as specified by the MPEG-2 AAC/MPEG-4 Audio standard. ISO/IEC gives users of the "
        + "MPEG-2 AAC/MPEG-4 Audio standards free license to this software module or modifications "
        + "thereof for use in hardware or software products claiming conformance to the MPEG-2 "
        + "AAC/MPEG-4 Audio standards. Those intending to use this software module in hardware or "
        + "software products are advised that this use may infringe existing patents. The original "
        + "developer of this software module and his/her company, the subsequent editors and their "
        + "companies, and ISO/IEC have no liability for use of this software module or modifications "
        + "thereof in an implementation. Copyright is not released for non MPEG-2 AAC/MPEG-4 Audio "
        + "conforming products. The original developer retains full right to use the code for his/her "
        + "own purpose, assign or donate the code to a third party and to inhibit third party from "
        + "using the code for non MPEG-2 AAC/MPEG-4 Audio conforming products. This copyright notice "
        + "must be included in all copies or derivative works.\" Copyright(c)1996, Copyright(c)2006.";
}
