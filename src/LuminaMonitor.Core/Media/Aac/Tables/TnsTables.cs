namespace LuminaMonitor.Core.Media.Aac.Tables;

/// <summary>
/// The limits temporal noise shaping runs under in a low delay frame.
/// </summary>
/// <remarks>
/// <para><b>Source.</b> ISO/IEC 14496-5:2001/Amd 43:2018 reference software (see
/// <see cref="TablesProvenance"/>), file <c>decdata.c</c>, array
/// <c>tns_max_bands_tbl_low_delay</c> and function <c>tns_max_order</c> — the
/// low delay columns of the TNS tables of ISO/IEC 14496-3.</para>
///
/// <para><b>What is a table and what is not.</b> Only the band and order ceilings
/// are data. The filter coefficients are not: the standard dequantizes them with
/// a formula, <c>sin(coef / iqfac)</c> where <c>iqfac</c> is
/// <c>((1 &lt;&lt; (coefRes - 1)) - 0.5) / (pi / 2)</c> for a non-negative
/// coefficient and <c>((1 &lt;&lt; (coefRes - 1)) + 0.5) / (pi / 2)</c> for a
/// negative one, followed by the usual reflection-coefficient to LPC recursion.
/// That is decoder arithmetic and belongs with the decoder, not in this
/// folder.</para>
///
/// <para><b>Reading the ceilings.</b> A filter whose bitstream says it reaches
/// past <see cref="MaxBands"/> is clamped there, and an order past
/// <see cref="MaxOrder"/> is a stream this decoder should refuse rather than
/// guess at.</para>
/// </remarks>
internal static class TnsTables
{
    /// <summary>
    /// The highest scalefactor band a low delay TNS filter may reach, per
    /// sampling frequency index: column 0 for 480-line frames, column 1 for 512.
    /// </summary>
    /// <remarks>
    /// Sixteen rows because the AudioSpecificConfig's index is four bits; the
    /// last three are the reserved indices and stay zero, exactly as the
    /// reference software leaves them.
    /// </remarks>
    internal static readonly byte[,] LowDelayMaxBands =
    {
        { 31, 31 },  // 0: 96000
        { 31, 31 },  // 1: 88200
        { 31, 31 },  // 2: 64000
        { 31, 31 },  // 3: 48000  <- what the phone sends
        { 32, 32 },  // 4: 44100
        { 37, 37 },  // 5: 32000
        { 30, 31 },  // 6: 24000
        { 30, 31 },  // 7: 22050
        { 30, 31 },  // 8: 16000
        { 30, 31 },  // 9: 12000
        { 30, 31 },  // 10: 11025
        { 30, 31 },  // 11: 8000
        { 30, 31 },  // 12: 7350
        { 0, 0 },    // 13: reserved
        { 0, 0 },    // 14: reserved
        { 0, 0 },    // 15: escape (frequency given explicitly)
    };

    /// <summary>The band ceiling for one sampling frequency index and frame length.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The pair is not a low delay one.</exception>
    public static int MaxBands(int samplingFrequencyIndex, int frameLength)
    {
        if (samplingFrequencyIndex is < 0 or > 15)
            throw new ArgumentOutOfRangeException(nameof(samplingFrequencyIndex), samplingFrequencyIndex,
                "Index de frequence hors des quatre bits de l'ASC.");
        int column = frameLength switch
        {
            480 => 0,
            512 => 1,
            _ => throw new ArgumentOutOfRangeException(nameof(frameLength), frameLength,
                "L'ELD ne connait que 480 ou 512 lignes."),
        };
        return LowDelayMaxBands[samplingFrequencyIndex, column];
    }

    /// <summary>
    /// The highest filter order a low delay frame allows: 20 up to 48 kHz, 12
    /// from 32 kHz down.
    /// </summary>
    /// <remarks>
    /// The reference software has no low delay case in <c>tns_max_order</c>: the
    /// ELD profiles fall into its default branch, which for a long window returns
    /// 12 when the sampling frequency index is 5 or more (32 kHz and below) and
    /// 20 otherwise. An ELD frame is always a long window, so the short-window
    /// answer of 7 never applies here.
    /// </remarks>
    public static int MaxOrder(int samplingFrequencyIndex) => samplingFrequencyIndex >= 5 ? 12 : 20;
}
