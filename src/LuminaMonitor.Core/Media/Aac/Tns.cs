namespace LuminaMonitor.Core.Media.Aac;

/// <summary>
/// Temporal noise shaping: an all-pole filter run along the spectrum.
/// </summary>
/// <remarks>
/// <para><b>What it is.</b> ISO/IEC 14496-3 §4.6.9. The encoder flattened the
/// temporal envelope of the frame by filtering its spectrum with an LPC analysis
/// filter; the decoder puts the envelope back by running the matching synthesis
/// filter, <c>y(n) = x(n) − Σ a(k)·y(n−k)</c>, over the same range of spectral
/// lines. It is the one tool in AAC that treats the spectrum as a signal.</para>
///
/// <para><b>Dequantizing the coefficients.</b> The transmitted numbers are
/// quantized reflection coefficients: <c>sin(c / f)</c>, where <c>f</c> is
/// <c>((1 &lt;&lt; (res−1)) − 0.5)/(π/2)</c> for a non-negative coefficient and
/// <c>((1 &lt;&lt; (res−1)) + 0.5)/(π/2)</c> for a negative one. The asymmetry is
/// in the standard and in <c>tns_decode_coef()</c> of the reference software's
/// <c>tns.c</c>; it is not a rounding artefact and dropping it detunes every
/// filter slightly. The recursion from reflection coefficients to LPC
/// coefficients that follows is Markel and Gray's, which is the citation the
/// reference software itself gives.</para>
///
/// <para><b>Where it runs.</b> Between the stereo tools and the filterbank, on
/// the spectral lines of bands <c>[start, stop)</c> — both clamped to the
/// frequency's TNS band ceiling and to <c>max_sfb</c>, which is what the
/// reference passes as <c>nbands</c>. Upward or downward along the spectrum as
/// the direction bit says; the filter state starts at zero either way.</para>
/// </remarks>
internal static class Tns
{
    /// <summary>
    /// Runs every filter of one channel's <c>tns_data</c> over its own range.
    /// </summary>
    /// <param name="spectrum">The channel's spectral coefficients, filtered in place.</param>
    /// <param name="tns">The filters as the bitstream gave them.</param>
    /// <param name="bandOffsets">Where each band starts; the last entry is the frame length.</param>
    /// <param name="maxSfb">How many bands the channel transmitted.</param>
    /// <param name="maxBands">The frequency's TNS band ceiling, from <c>TnsTables</c>.</param>
    /// <param name="lpc">Scratch for the LPC coefficients, at least <c>MaxOrder + 1</c> long.</param>
    public static void Apply(Span<double> spectrum, TnsFrame tns, ReadOnlySpan<ushort> bandOffsets,
        int maxSfb, int maxBands, Span<double> lpc)
    {
        int ceiling = Math.Min(maxBands, maxSfb);
        for (int index = 0; index < tns.FilterCount; index++)
        {
            var filter = tns.Filters[index];
            if (filter.Order == 0)
                continue;

            int start = bandOffsets[Math.Min(filter.StartBand, ceiling)];
            int stop = bandOffsets[Math.Min(filter.StopBand, ceiling)];
            if (stop <= start)
                continue;

            BuildLpc(filter, tns.CoefficientResolution, lpc);
            Filter(spectrum[start..stop], lpc, filter.Order, filter.Downward);
        }
    }

    /// <summary>
    /// Dequantizes the reflection coefficients and turns them into LPC ones.
    /// </summary>
    private static void BuildLpc(TnsFilter filter, int resolution, Span<double> lpc)
    {
        double positiveScale = ((1 << (resolution - 1)) - 0.5) / (Math.PI / 2.0);
        double negativeScale = ((1 << (resolution - 1)) + 0.5) / (Math.PI / 2.0);

        Span<double> reflection = stackalloc double[TnsFilter.MaxOrder + 1];
        for (int i = 0; i < filter.Order; i++)
        {
            short coefficient = filter.Coefficients[i];
            reflection[i + 1] = Math.Sin(coefficient / (coefficient >= 0 ? positiveScale : negativeScale));
        }

        // Markel and Gray's recursion, one order at a time; `next` holds the
        // order being built so that the previous order can still be read.
        Span<double> next = stackalloc double[TnsFilter.MaxOrder + 1];
        lpc[0] = 1.0;
        for (int order = 1; order <= filter.Order; order++)
        {
            next[0] = lpc[0];
            for (int i = 1; i < order; i++)
                next[i] = lpc[i] + (reflection[order] * lpc[order - i]);
            next[order] = reflection[order];
            for (int i = 0; i <= order; i++)
                lpc[i] = next[i];
        }
    }

    /// <summary>
    /// The all-pole filter itself, over one stretch of spectrum, in place.
    /// </summary>
    private static void Filter(Span<double> lines, ReadOnlySpan<double> lpc, int order, bool downward)
    {
        Span<double> state = stackalloc double[TnsFilter.MaxOrder];
        state.Clear();

        int at = downward ? lines.Length - 1 : 0;
        int step = downward ? -1 : 1;
        for (int n = 0; n < lines.Length; n++, at += step)
        {
            double y = lines[at];
            for (int k = 0; k < order; k++)
                y -= lpc[k + 1] * state[k];
            for (int k = order - 1; k > 0; k--)
                state[k] = state[k - 1];
            state[0] = y;
            lines[at] = y;
        }
    }
}
