namespace LuminaMonitor.Core.Media.Aac.Tables;

/// <summary>
/// The window of the ELD low delay filterbank — the one table in this folder
/// that no formula produces.
/// </summary>
/// <remarks>
/// <para>AAC-ELD does not use the MDCT of plain AAC. Its filterbank is a low
/// delay MDCT whose window is four frames long — 1920 coefficients for a
/// 480-sample frame, 2048 for a 512-sample one — against the two frames of an
/// ordinary MDCT window. A sine or KBD window can be computed from its
/// definition; this one is a numerically designed prototype and exists only as a
/// table. Get one coefficient wrong and the filterbank stops reconstructing:
/// that is why <c>aac-tables-selftest</c> runs a real analysis and synthesis
/// pass over it rather than merely counting the entries.</para>
///
/// <para><b>Source and licence.</b> <see cref="TablesProvenance"/>; the values
/// are in <see cref="EldWindow480"/> and <see cref="EldWindow512"/>, each taken
/// from the reference-software header that <c>imdct.c</c> includes.</para>
///
/// <para><b>Precision.</b> Eight decimals, as the reference software prints
/// them. The same insert also carries <c>win512LD2.h</c>, the 512 window to ten
/// significant digits, which nothing in the reference decoder includes; feeding
/// it through the self-test's analysis and synthesis pass drops the
/// reconstruction residual from about 1.4e-8 to about 4.3e-11, which is the
/// clearest evidence that the two files hold the same window and that the
/// residual is the printing, not the mathematics. Eight decimals is 157 dB below
/// full scale, so the copy used here is the one the reference decoder uses.</para>
///
/// <para><b>How it is applied.</b> Analysis multiplies the four-frame input
/// buffer by the window <em>reversed</em>; synthesis multiplies the filterbank's
/// four-frame output by the window in its own order, and overlap-adds with the
/// frame length as hop and a further shift of a quarter frame
/// (<see cref="DelayShift"/>). Both conventions are the reference decoder's, and
/// the self-test measures the reconstruction delay they produce rather than
/// asserting it from the page.</para>
/// </remarks>
internal static class EldWindow
{
    /// <summary>The two frame lengths the ELD object type allows.</summary>
    public static readonly int[] FrameLengths = [480, 512];

    /// <summary>The window for that frame length: four times as many coefficients as the frame is long.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The ELD has no window for that frame length.</exception>
    public static ReadOnlySpan<double> For(int frameLength) => frameLength switch
    {
        480 => EldWindow480.Coefficients,
        512 => EldWindow512.Coefficients,
        _ => throw new ArgumentOutOfRangeException(nameof(frameLength), frameLength,
            "L'ELD ne connait que 480 ou 512 echantillons par trame."),
    };

    /// <summary>
    /// The quarter frame the synthesis overlap-add is shifted by, which is what
    /// makes the filterbank a low delay one.
    /// </summary>
    public static int DelayShift(int frameLength) => frameLength / 4;
}
