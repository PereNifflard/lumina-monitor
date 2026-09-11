// Generated from the ISO/IEC 14496-5 reference software; see TablesProvenance.
namespace LuminaMonitor.Core.Media.Aac.Tables;

/// <summary>
/// Spectral codebooks 1 to 6 of the AAC Huffman set.
/// Codewords and lengths only; the dimension, LAV and sign of each book
/// live in <see cref="HuffmanTables"/>.
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
/// <para><b>This file.</b> Reference-software file <c>hufftables.c</c>, arrays <c>book1</c> to
/// <c>book11</c>, which are the Huffman codebooks of ISO/IEC 14496-3 Annex 4.A.
/// The index column of that file is the array position and is not stored again.</para>
///
/// <para><b>Licence.</b> Not this project's MIT licence: the reference software
/// carries the MPEG software module notice reproduced verbatim in
/// <see cref="TablesProvenance.MpegSoftwareModuleNotice"/>, which grants a free
/// licence for use in products claiming conformance to the MPEG-4 Audio
/// standard and requires that the notice travel with every copy.</para>
/// </remarks>
internal static class HuffmanCodebooks1To6
{
    /// <summary>Codeword lengths of Book1, in bits, indexed by table index.</summary>
    internal static readonly byte[] Book1Lengths =
    [
        11, 9, 11, 10, 7, 10, 11, 9, 11, 10, 7, 10, 7, 5, 7, 9, 7, 10, 11, 9,
        11, 9, 7, 9, 11, 9, 11, 9, 7, 9, 7, 5, 7, 9, 7, 9, 7, 5, 7, 5,
        1, 5, 7, 5, 7, 9, 7, 9, 7, 5, 7, 9, 7, 9, 11, 9, 11, 9, 7, 9,
        11, 9, 11, 10, 7, 9, 7, 5, 7, 9, 7, 10, 11, 9, 11, 10, 7, 9, 11, 9,
        11
    ];

    /// <summary>Codewords of Book1, right aligned in Book1Lengths bits.</summary>
    internal static readonly uint[] Book1Codewords =
    [
        2040, 497, 2045, 1013, 104, 1008, 2039, 492, 2037, 1009, 114, 1012,
        116, 17, 118, 491, 108, 1014, 2044, 481, 2033, 496, 97, 502,
        2034, 490, 2043, 498, 105, 493, 119, 23, 111, 486, 100, 485,
        103, 21, 98, 18, 0, 20, 101, 22, 109, 489, 99, 484,
        107, 19, 113, 483, 112, 499, 2046, 487, 2035, 495, 96, 494,
        2032, 482, 2042, 1011, 106, 488, 117, 16, 115, 500, 110, 1015,
        2038, 480, 2041, 1010, 102, 501, 2047, 503, 2036
    ];

    /// <summary>Codeword lengths of Book2, in bits, indexed by table index.</summary>
    internal static readonly byte[] Book2Lengths =
    [
        9, 7, 9, 8, 6, 8, 9, 8, 9, 8, 6, 7, 6, 5, 6, 7, 6, 8, 9, 7,
        8, 8, 6, 8, 9, 7, 9, 8, 6, 7, 6, 5, 6, 7, 6, 8, 6, 5, 6, 5,
        3, 5, 6, 5, 6, 8, 6, 7, 6, 5, 6, 8, 6, 8, 9, 7, 9, 8, 6, 8,
        8, 7, 9, 8, 6, 7, 6, 4, 6, 8, 6, 7, 9, 7, 9, 7, 6, 8, 9, 7,
        9
    ];

    /// <summary>Codewords of Book2, right aligned in Book2Lengths bits.</summary>
    internal static readonly uint[] Book2Codewords =
    [
        499, 111, 509, 235, 35, 234, 503, 232, 506, 242, 45, 112,
        32, 6, 43, 110, 40, 233, 505, 102, 248, 231, 27, 241,
        500, 107, 501, 236, 42, 108, 44, 10, 39, 103, 26, 245,
        36, 8, 31, 9, 0, 7, 29, 11, 48, 239, 28, 100,
        30, 12, 41, 243, 47, 240, 508, 113, 498, 244, 33, 230,
        247, 104, 504, 238, 34, 101, 49, 2, 38, 237, 37, 106,
        507, 114, 510, 105, 46, 246, 511, 109, 502
    ];

    /// <summary>Codeword lengths of Book3, in bits, indexed by table index.</summary>
    internal static readonly byte[] Book3Lengths =
    [
        1, 4, 8, 4, 5, 8, 9, 9, 10, 4, 6, 9, 6, 6, 9, 9, 9, 10, 9, 10,
        13, 9, 9, 11, 11, 10, 12, 4, 6, 10, 6, 7, 10, 10, 10, 12, 5, 7, 11, 6,
        7, 10, 9, 9, 11, 9, 10, 13, 8, 9, 12, 10, 11, 12, 8, 10, 15, 9, 11, 15,
        13, 14, 16, 8, 10, 14, 9, 10, 14, 12, 12, 15, 11, 12, 16, 10, 11, 15, 12, 12,
        15
    ];

    /// <summary>Codewords of Book3, right aligned in Book3Lengths bits.</summary>
    internal static readonly uint[] Book3Codewords =
    [
        0, 9, 239, 11, 25, 240, 491, 486, 1010, 10, 53, 495,
        52, 55, 489, 493, 487, 1011, 494, 1005, 8186, 492, 498, 2041,
        2040, 1016, 4088, 8, 56, 1014, 54, 117, 1009, 1003, 1004, 4084,
        24, 118, 2036, 57, 116, 1007, 499, 500, 2038, 488, 1002, 8188,
        242, 497, 4091, 1013, 2035, 4092, 238, 1015, 32766, 496, 2037, 32765,
        8187, 16378, 65535, 241, 1008, 16380, 490, 1006, 16379, 4086, 4090, 32764,
        2034, 4085, 65534, 1012, 2039, 32763, 4087, 4089, 32762
    ];

    /// <summary>Codeword lengths of Book4, in bits, indexed by table index.</summary>
    internal static readonly byte[] Book4Lengths =
    [
        4, 5, 8, 5, 4, 8, 9, 8, 11, 5, 5, 8, 5, 4, 8, 8, 7, 10, 9, 8,
        11, 8, 8, 10, 11, 10, 11, 4, 5, 8, 4, 4, 8, 8, 8, 10, 4, 4, 8, 4,
        4, 7, 8, 7, 9, 8, 8, 10, 7, 7, 9, 10, 9, 10, 8, 8, 11, 8, 7, 10,
        11, 10, 12, 8, 7, 10, 7, 7, 9, 10, 9, 11, 11, 10, 12, 10, 9, 11, 11, 10,
        11
    ];

    /// <summary>Codewords of Book4, right aligned in Book4Lengths bits.</summary>
    internal static readonly uint[] Book4Codewords =
    [
        7, 22, 246, 24, 8, 239, 495, 243, 2040, 25, 23, 237,
        21, 1, 226, 240, 112, 1008, 494, 241, 2042, 238, 228, 1010,
        2038, 1007, 2045, 5, 20, 242, 9, 4, 229, 244, 232, 1012,
        6, 2, 231, 3, 0, 107, 227, 105, 499, 235, 230, 1014,
        110, 106, 500, 1004, 496, 1017, 245, 236, 2043, 234, 111, 1015,
        2041, 1011, 4095, 233, 109, 1016, 108, 104, 501, 1006, 498, 2036,
        2039, 1009, 4094, 1005, 497, 2037, 2046, 1013, 2044
    ];

    /// <summary>Codeword lengths of Book5, in bits, indexed by table index.</summary>
    internal static readonly byte[] Book5Lengths =
    [
        13, 12, 11, 11, 10, 11, 11, 12, 13, 12, 11, 10, 9, 8, 9, 10, 11, 12, 12, 10,
        9, 8, 7, 8, 9, 10, 11, 11, 9, 8, 5, 4, 5, 8, 9, 11, 10, 8, 7, 4,
        1, 4, 7, 8, 11, 11, 9, 8, 5, 4, 5, 8, 9, 11, 11, 10, 9, 8, 7, 8,
        9, 10, 11, 12, 11, 10, 9, 8, 9, 10, 11, 12, 13, 12, 12, 11, 10, 10, 11, 12,
        13
    ];

    /// <summary>Codewords of Book5, right aligned in Book5Lengths bits.</summary>
    internal static readonly uint[] Book5Codewords =
    [
        8191, 4087, 2036, 2024, 1009, 2030, 2041, 4088, 8189, 4093, 2033, 1000,
        488, 240, 492, 1006, 2034, 4090, 4084, 1007, 498, 232, 112, 236,
        496, 1002, 2035, 2027, 491, 234, 26, 8, 25, 238, 495, 2029,
        1008, 242, 115, 11, 0, 10, 113, 243, 2025, 2031, 494, 239,
        24, 9, 27, 235, 489, 2028, 2038, 1003, 499, 237, 114, 233,
        497, 1005, 2039, 4086, 2032, 1001, 493, 241, 490, 1004, 2040, 4089,
        8188, 4092, 4085, 2026, 1011, 1010, 2037, 4091, 8190
    ];

    /// <summary>Codeword lengths of Book6, in bits, indexed by table index.</summary>
    internal static readonly byte[] Book6Lengths =
    [
        11, 10, 9, 9, 9, 9, 9, 10, 11, 10, 9, 8, 7, 7, 7, 8, 9, 10, 9, 8,
        6, 6, 6, 6, 6, 8, 9, 9, 7, 6, 4, 4, 4, 6, 7, 9, 9, 7, 6, 4,
        4, 4, 6, 7, 9, 9, 7, 6, 4, 4, 4, 6, 7, 9, 9, 8, 6, 6, 6, 6,
        6, 8, 9, 10, 9, 8, 7, 7, 7, 7, 8, 10, 11, 10, 9, 9, 9, 9, 9, 10,
        11
    ];

    /// <summary>Codewords of Book6, right aligned in Book6Lengths bits.</summary>
    internal static readonly uint[] Book6Codewords =
    [
        2046, 1021, 497, 491, 500, 490, 496, 1020, 2045, 1014, 485, 234,
        108, 113, 104, 240, 486, 1015, 499, 239, 50, 39, 40, 38,
        49, 235, 503, 488, 111, 46, 8, 4, 6, 41, 107, 494,
        495, 114, 45, 2, 0, 3, 47, 115, 506, 487, 110, 43,
        7, 1, 5, 44, 109, 492, 505, 238, 48, 36, 42, 37,
        51, 236, 498, 1016, 484, 237, 106, 112, 105, 116, 241, 1018,
        2047, 1017, 502, 493, 504, 489, 501, 1019, 2044
    ];
}
