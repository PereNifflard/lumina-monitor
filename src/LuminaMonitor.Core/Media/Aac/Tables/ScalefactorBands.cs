namespace LuminaMonitor.Core.Media.Aac.Tables;

/// <summary>
/// Where each scalefactor band starts and ends, for the low delay frame lengths
/// (480 and 512 spectral lines) an AAC-ELD stream uses.
/// </summary>
/// <remarks>
/// <para><b>Source.</b> ISO/IEC 14496-5:2001/Amd 43:2018 reference software (see
/// <see cref="TablesProvenance"/>), file <c>decdata.c</c>, arrays
/// <c>sfb_48_480</c>, <c>sfb_48_512</c>, <c>sfb_32_480</c>, <c>sfb_32_512</c>,
/// <c>sfb_24_480</c> and <c>sfb_24_512</c>, together with the last two columns
/// of <c>samp_rate_info</c>, which say which array each sampling frequency index
/// uses and how many bands it has. These are the <c>swb_offset_long_window</c>
/// tables of ISO/IEC 14496-3 for the low delay frame lengths; the ELD object
/// type has no short windows, so there is no short table to carry.</para>
///
/// <para><b>Arrangement.</b> The reference software lists band <em>ends</em>
/// only; the arrays here are the same numbers with a leading zero, so that band
/// <c>b</c> covers lines <c>[offsets[b], offsets[b + 1])</c> and
/// <c>offsets.Length - 1</c> is the number of bands. The last entry is the frame
/// length, which is one of the things <c>aac-tables-selftest</c> checks.</para>
///
/// <para><b>Reach.</b> The phone sends 48 kHz, and the 48 kHz tables also serve
/// 96, 88.2, 64 and 44.1 kHz — the reference software points all five sampling
/// frequency indices at the same pair. The 32 kHz and 24 kHz pairs are here
/// because they cost forty numbers and spare a second trip to ISO if the phone
/// ever negotiates something else; every index from 24 kHz down shares the
/// 24 kHz pair.</para>
/// </remarks>
internal static class ScalefactorBands
{
    /// <summary>Band ends for 480-line frames at 96, 88.2, 64, 48 and 44.1 kHz: 35 bands.</summary>
    internal static readonly ushort[] LowDelay48000For480 =
    [
        0, 4, 8, 12, 16, 20, 24, 28, 32, 36, 40, 44, 48, 52, 56, 64, 72, 80,
        88, 96, 108, 120, 132, 144, 156, 172, 188, 212, 240, 272, 304, 336, 368,
        400, 432, 480,
    ];

    /// <summary>Band ends for 512-line frames at 96, 88.2, 64, 48 and 44.1 kHz: 36 bands.</summary>
    internal static readonly ushort[] LowDelay48000For512 =
    [
        0, 4, 8, 12, 16, 20, 24, 28, 32, 36, 40, 44, 48, 52, 56, 60, 68, 76,
        84, 92, 100, 112, 124, 136, 148, 164, 184, 208, 236, 268, 300, 332, 364,
        396, 428, 460, 512,
    ];

    /// <summary>Band ends for 480-line frames at 32 kHz: 37 bands.</summary>
    internal static readonly ushort[] LowDelay32000For480 =
    [
        0, 4, 8, 12, 16, 20, 24, 28, 32, 36, 40, 44, 48, 52, 56, 60, 64, 72,
        80, 88, 96, 104, 112, 124, 136, 148, 164, 180, 200, 224, 256, 288, 320,
        352, 384, 416, 448, 480,
    ];

    /// <summary>Band ends for 512-line frames at 32 kHz: 37 bands.</summary>
    internal static readonly ushort[] LowDelay32000For512 =
    [
        0, 4, 8, 12, 16, 20, 24, 28, 32, 36, 40, 44, 48, 52, 56, 64, 72, 80,
        88, 96, 108, 120, 132, 144, 160, 176, 192, 212, 236, 260, 288, 320, 352,
        384, 416, 448, 480, 512,
    ];

    /// <summary>Band ends for 480-line frames at 24 kHz and below: 30 bands.</summary>
    internal static readonly ushort[] LowDelay24000For480 =
    [
        0, 4, 8, 12, 16, 20, 24, 28, 32, 36, 40, 44, 52, 60, 68, 80, 92, 104,
        120, 140, 164, 192, 224, 256, 288, 320, 352, 384, 416, 448, 480,
    ];

    /// <summary>Band ends for 512-line frames at 24 kHz and below: 31 bands.</summary>
    internal static readonly ushort[] LowDelay24000For512 =
    [
        0, 4, 8, 12, 16, 20, 24, 28, 32, 36, 40, 44, 52, 60, 68, 80, 92, 104,
        120, 140, 164, 192, 224, 256, 288, 320, 352, 384, 416, 448, 480, 512,
    ];

    /// <summary>
    /// The band edges for one sampling frequency index and frame length.
    /// </summary>
    /// <param name="samplingFrequencyIndex">The index the AudioSpecificConfig carries (3 is 48 kHz).</param>
    /// <param name="frameLength">480 or 512 spectral lines.</param>
    /// <exception cref="ArgumentOutOfRangeException">No low delay table covers that pair.</exception>
    public static ReadOnlySpan<ushort> LowDelay(int samplingFrequencyIndex, int frameLength)
    {
        bool shortFrame = frameLength switch
        {
            480 => true,
            512 => false,
            _ => throw new ArgumentOutOfRangeException(nameof(frameLength), frameLength,
                "L'ELD ne connait que 480 ou 512 lignes."),
        };
        return samplingFrequencyIndex switch
        {
            >= 0 and <= 4 => shortFrame ? LowDelay48000For480 : LowDelay48000For512,
            5 => shortFrame ? LowDelay32000For480 : LowDelay32000For512,
            >= 6 and <= 12 => shortFrame ? LowDelay24000For480 : LowDelay24000For512,
            _ => throw new ArgumentOutOfRangeException(nameof(samplingFrequencyIndex), samplingFrequencyIndex,
                "Index de frequence sans table basse latence."),
        };
    }

    /// <summary>How many scalefactor bands that pair has.</summary>
    public static int Count(int samplingFrequencyIndex, int frameLength) =>
        LowDelay(samplingFrequencyIndex, frameLength).Length - 1;
}
