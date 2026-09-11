namespace LuminaMonitor.Core.Media.Aac;

/// <summary>
/// Perceptual noise substitution: bands the encoder sent as an energy instead of
/// as values.
/// </summary>
/// <remarks>
/// <para><b>What it is.</b> ISO/IEC 14496-3 §4.6.13. A band coded with codebook
/// 13 carries no spectral values at all, only a target energy; the decoder fills
/// it with noise of that energy. Which noise is not specified and cannot be — the
/// point of the tool is that the exact waveform does not matter — so any generator
/// does, and the one here is the reference software's: a unit-energy vector of
/// pseudo-random numbers, scaled to <c>2^(0.25·energy)</c>. That is
/// <c>gen_rand_vector()</c> and <c>pns()</c> of <c>pns.c</c>, whose generator is
/// the linear congruence of Numerical Recipes.</para>
///
/// <para><b>One thing is specified: correlation.</b> A stereo pair may ask for
/// the <em>same</em> noise in both channels — that is what the mid/side mask bit
/// means over a noise band, which <see cref="Stereo.MapMask"/> turns into
/// <see cref="CorrelatedNoiseCodebook"/>. So the generator's state at the start
/// of each band is remembered while the left channel is filled and restored while
/// the right one is, which makes the two channels' noise identical band by band.
/// Getting this wrong does not sound like noise in the wrong place; it sounds
/// like the stereo image opening up where it should be narrow.</para>
///
/// <para>The left channel of a pair must be filled before the right, because the
/// left is what stores the state the right reads back.</para>
/// </remarks>
internal sealed class Pns
{
    /// <summary>
    /// The codebook number this decoder uses inside itself for "noise, same as
    /// the other channel's".
    /// </summary>
    /// <remarks>
    /// It is not a bitstream value: the bitstream says 13 and sets the mid/side
    /// mask bit, and <see cref="Stereo.MapMask"/> folds the two into this. The
    /// reference software writes <c>NOISE_HCB + 100</c> for the same purpose;
    /// 113 is that number, kept out of the way of every real codebook.
    /// </remarks>
    public const int CorrelatedNoiseCodebook = 113;

    private readonly int[] savedState;
    private int state;

    public Pns(int bandCount) => savedState = new int[bandCount];

    /// <summary>
    /// Fills the noise bands of one channel.
    /// </summary>
    /// <param name="spectrum">The channel's coefficients; only noise bands are touched.</param>
    /// <param name="codebooks">The channel's codebooks, after <see cref="Stereo.MapMask"/>.</param>
    /// <param name="factors">The band energies, as <see cref="ScaleFactors"/> read them.</param>
    /// <param name="bandOffsets">Where each band starts; the last entry is the frame length.</param>
    public void Apply(Span<double> spectrum, ReadOnlySpan<byte> codebooks, ReadOnlySpan<short> factors,
        ReadOnlySpan<ushort> bandOffsets)
    {
        for (int band = 0; band < codebooks.Length; band++)
        {
            int codebook = codebooks[band];
            if (codebook is not (SectionData.NoiseCodebook or CorrelatedNoiseCodebook))
                continue;

            int start = bandOffsets[band];
            int stop = bandOffsets[band + 1];
            if (codebook == SectionData.NoiseCodebook)
            {
                savedState[band] = state;
                Fill(spectrum[start..stop], ref state, factors[band]);
            }
            else
            {
                int correlated = savedState[band];
                Fill(spectrum[start..stop], ref correlated, factors[band]);
            }
        }
    }

    /// <summary>Forgets the generator's history, at a frame this decoder gave up on.</summary>
    public void Reset()
    {
        state = 0;
        Array.Clear(savedState);
    }

    /// <summary>
    /// One band of noise at the energy the bitstream asked for.
    /// </summary>
    /// <remarks>
    /// The vector is normalized to unit energy first and scaled afterwards, so
    /// the band's energy is exactly <c>2^(0.5·energy)</c> whatever its width. A
    /// band whose draw happens to sum to zero — impossible in practice, one line
    /// wide and unlucky in principle — is left silent rather than divided by
    /// zero.
    /// </remarks>
    private static void Fill(Span<double> lines, ref int seed, int energy)
    {
        double sumOfSquares = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            seed = (1664525 * seed) + 1013904223;
            lines[i] = seed;
            sumOfSquares += lines[i] * lines[i];
        }
        if (sumOfSquares <= 0)
        {
            lines.Clear();
            return;
        }
        double scale = Math.Pow(2.0, 0.25 * energy) / Math.Sqrt(sumOfSquares);
        for (int i = 0; i < lines.Length; i++)
            lines[i] *= scale;
    }
}
