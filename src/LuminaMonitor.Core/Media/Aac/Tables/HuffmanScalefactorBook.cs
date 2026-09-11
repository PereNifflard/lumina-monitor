// Generated from the ISO/IEC 14496-5 reference software; see TablesProvenance.
namespace LuminaMonitor.Core.Media.Aac.Tables;

/// <summary>
/// The scalefactor Huffman codebook: one differential scalefactor per codeword.
/// </summary>
/// <remarks>
/// <para><b>Source.</b> ISO/IEC 14496-5:2001/Amd 43:2018 (MPEG-4 part 5,
/// reference software), electronic insert <c>14496-5_Amd43_inserts.zip</c>,
/// downloaded free of charge from the ISO Standards Maintenance Portal
/// (https://standards.iso.org/iso-iec/14496/-5/ed-2/en/amd/43/) on
/// 10 September 2026. That insert ships the whole MPEG-4 Audio reference
/// software tree, AAC-(E)LD included; the file named below sits in
/// <c>audio/natural/mp4AudVm_Rewrite/src_tf/</c>. The ELD parts of that tree
/// entered the standard as ISO/IEC 14496-5:2001/Amd 24:2009, "Reference
/// software for AAC-ELD". The numbers were transcribed by script, not by hand,
/// and are checked mathematically by the probe's <c>aac-tables-selftest</c>.</para>
///
/// <para><b>This file.</b> Reference-software file <c>hufftables.c</c>, array <c>bookscl</c>, the
/// scalefactor codebook of ISO/IEC 14496-3 Annex 4.A. One dimension, LAV 60:
/// the decoded difference is the table index minus 60.</para>
///
/// <para><b>Licence.</b> Not this project's MIT licence: the reference software
/// carries the MPEG software module notice reproduced verbatim in
/// <see cref="TablesProvenance.MpegSoftwareModuleNotice"/>, which grants a free
/// licence for use in products claiming conformance to the MPEG-4 Audio
/// standard and requires that the notice travel with every copy.</para>
/// </remarks>
internal static class HuffmanScalefactorBook
{
    /// <summary>Codeword lengths of Scalefactor, in bits, indexed by table index.</summary>
    internal static readonly byte[] ScalefactorLengths =
    [
        18, 18, 18, 18, 19, 19, 19, 19, 19, 19, 19, 19, 19, 19, 19, 19, 19, 19, 19, 18,
        19, 18, 17, 17, 16, 17, 16, 16, 16, 16, 15, 15, 14, 14, 14, 14, 14, 14, 13, 13,
        12, 12, 12, 11, 12, 11, 10, 10, 10, 9, 9, 8, 8, 8, 7, 6, 6, 5, 4, 3,
        1, 4, 4, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 10, 11, 11, 11, 11, 12,
        12, 13, 13, 13, 14, 14, 16, 15, 16, 15, 18, 19, 19, 19, 19, 19, 19, 19, 19, 19,
        19, 19, 19, 19, 19, 19, 19, 19, 19, 19, 19, 19, 19, 19, 19, 19, 19, 19, 19, 19,
        19
    ];

    /// <summary>Codewords of Scalefactor, right aligned in ScalefactorLengths bits.</summary>
    internal static readonly uint[] ScalefactorCodewords =
    [
        262120, 262118, 262119, 262117, 524277, 524273, 524269, 524278, 524270, 524271, 524272, 524284,
        524285, 524287, 524286, 524279, 524280, 524283, 524281, 262116, 524282, 262115, 131055, 131056,
        65525, 131054, 65522, 65523, 65524, 65521, 32758, 32759, 16377, 16373, 16375, 16371,
        16374, 16370, 8183, 8181, 4089, 4087, 4086, 2041, 4084, 2040, 1017, 1015,
        1013, 504, 503, 250, 248, 246, 121, 58, 56, 26, 11, 4,
        0, 10, 12, 27, 57, 59, 120, 122, 247, 249, 502, 505,
        1012, 1014, 1016, 2037, 2036, 2038, 2039, 4085, 4088, 8180, 8182, 8184,
        16376, 16372, 65520, 32756, 65526, 32757, 262114, 524249, 524250, 524251, 524252, 524253,
        524254, 524248, 524242, 524243, 524244, 524245, 524246, 524274, 524255, 524263, 524264, 524265,
        524266, 524267, 524262, 524256, 524257, 524258, 524259, 524260, 524261, 524247, 524268, 524276,
        524275
    ];
}
