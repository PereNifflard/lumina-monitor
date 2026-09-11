using System.Numerics;
using LuminaMonitor.Core.Media.Aac.Tables;

namespace LuminaMonitor.Core.Media.Aac;

/// <summary>
/// The ELD low delay filterbank, synthesis side: <c>M</c> spectral lines in,
/// <c>M</c> samples out.
/// </summary>
/// <remarks>
/// <para><b>The transform.</b> ISO/IEC 14496-3 §4.6.20.2, and <c>LDFB()</c> of
/// the reference software's <c>transfo.c</c> as <c>ildfb()</c> calls it
/// (<c>imdct.c</c>). With <c>M</c> the frame length, <c>φ = π/M</c> and
/// <c>no = (1−M)/2</c>:</para>
/// <code>
/// y[n] = (−1/M) · sum(k = 0 .. M−1) X[k] · cos(φ · (n + no) · (k + 0.5))
/// </code>
/// <para>for <c>n = 0 .. 4M−1</c>. Four frames of output per frame of input: the
/// window is four frames long, which is what buys the low delay. The result is
/// multiplied by the window in its own order and overlap-added with the frame
/// length as hop, and the accumulator is read a quarter frame in — the
/// <c>delay_shift</c> of <c>freq2buffer()</c>, without which the filterbank still
/// reconstructs perfectly and is a quarter frame late.</para>
///
/// <para><b>Written as one DCT-IV.</b> The sum above is 4M outputs by M inputs,
/// nearly two million multiplications a frame, which is more than the rest of the
/// decoder put together. It does not need to be: writing <c>C</c> for the DCT-IV
/// of the same input, <c>C[m] = Σ X[k]·cos(φ(m+½)(k+½))</c>, the definition above
/// is exactly <c>y[n] = (−1/M)·C[n − M/2]</c>, and <c>C</c> extends outside
/// <c>0 .. M−1</c> by two identities its own cosine gives: <c>C[−1−m] = C[m]</c>
/// and <c>C[m+2M] = −C[m]</c>. So all 4M outputs are three folds of one M-point
/// DCT-IV — a quarter of the arithmetic and, more to the point, the same
/// transform written once.</para>
///
/// <para><b>Not proof of agreement with the encoder.</b> That the analysis and
/// synthesis here reconstruct is proof they match <em>each other</em>: a
/// different phase convention, or a time reversal, reconstructs just as
/// perfectly and turns a real stream into noise. Only decoding a real sound
/// settles it, which is what <c>decode-audio</c> is for. What the reconstruction
/// does prove is that the window table is right, and that is worth having on its
/// own.</para>
///
/// <para>One instance per channel: the overlap accumulator is the channel's
/// state. The tables are shared between instances of the same frame length.</para>
/// </remarks>
internal sealed class EldFilterBank
{
    private readonly Plan plan;
    private readonly double[] overlap;
    private readonly double[] folded;
    private readonly double[] transformed;

    public EldFilterBank(int frameLength)
    {
        plan = Plan.For(frameLength);
        FrameLength = frameLength;
        overlap = new double[5 * frameLength];
        folded = new double[4 * frameLength];
        transformed = new double[frameLength];
    }

    /// <summary>How many samples one frame produces, and how many lines it takes.</summary>
    public int FrameLength { get; }

    /// <summary>The quarter frame the overlap-add is shifted by.</summary>
    public int DelayShift => plan.DelayShift;

    /// <summary>Empties the overlap, for a stream that restarts.</summary>
    public void Reset() => Array.Clear(overlap);

    /// <summary>
    /// Drains one frame of overlap without adding anything to it, for a frame
    /// that could not be decoded.
    /// </summary>
    /// <remarks>
    /// Three frames of the overlap were written by frames that <em>were</em>
    /// decoded, so what comes out here is the real tail of the sound, fading as
    /// the window runs out. Clearing the accumulator instead would put a step in
    /// the waveform — an audible click at every lost packet — which is why
    /// <see cref="Reset"/> is for a restart and this is for a loss.
    /// </remarks>
    public void Flush(Span<double> samples)
    {
        int m = FrameLength;
        overlap.AsSpan(0, m).CopyTo(samples);
        Array.Copy(overlap, m, overlap, 0, overlap.Length - m);
        Array.Clear(overlap, overlap.Length - m, m);
    }

    /// <summary>
    /// Turns one frame's spectral lines into one frame of samples.
    /// </summary>
    /// <param name="spectrum">Exactly <see cref="FrameLength"/> spectral lines.</param>
    /// <param name="samples">Where the frame's samples go; at least <see cref="FrameLength"/> long.</param>
    public void Synthesize(ReadOnlySpan<double> spectrum, Span<double> samples)
    {
        int m = FrameLength;
        if (spectrum.Length != m)
            throw new ArgumentException($"{spectrum.Length} lignes spectrales pour une trame de {m}.", nameof(spectrum));
        if (samples.Length < m)
            throw new ArgumentException($"{samples.Length} echantillons de sortie pour une trame de {m}.", nameof(samples));

        Transform(spectrum, transformed);
        Fold();

        // Overlap-add, the accumulator read a quarter frame in. The accumulator
        // is a frame longer than the block so that the shift never reads past
        // what the previous frames wrote.
        int shift = plan.DelayShift;
        int length = (4 * m) - shift;
        for (int j = 0; j < length; j++)
            overlap[j] += folded[j + shift];

        overlap.AsSpan(0, m).CopyTo(samples);
        Array.Copy(overlap, m, overlap, 0, overlap.Length - m);
        Array.Clear(overlap, overlap.Length - m, m);
    }

    /// <summary>
    /// The M-point DCT-IV of the spectrum, from a table of its own cosines.
    /// </summary>
    /// <remarks>
    /// A direct matrix product, vectorized: for a 480-line frame that is 230 400
    /// multiply-adds and a 1.8 MB table, which at a hundred frames a second is
    /// tens of microseconds a channel. A fast DCT-IV would need a 240-point
    /// mixed-radix FFT — 240 is not a power of two — and would be a good deal more
    /// code to be wrong in; if this ever becomes the bottleneck, this is where to
    /// look.
    /// </remarks>
    private void Transform(ReadOnlySpan<double> spectrum, Span<double> coefficients)
    {
        int m = FrameLength;
        double[] kernel = plan.Cosines;
        int width = Vector<double>.Count;
        for (int row = 0; row < m; row++)
        {
            var kernelRow = kernel.AsSpan(row * m, m);
            var accumulator = Vector<double>.Zero;
            int k = 0;
            for (; k + width <= m; k += width)
                accumulator += new Vector<double>(kernelRow.Slice(k, width)) * new Vector<double>(spectrum.Slice(k, width));
            double sum = Vector.Sum(accumulator);
            for (; k < m; k++)
                sum += kernelRow[k] * spectrum[k];
            coefficients[row] = sum;
        }
    }

    /// <summary>
    /// Spreads the M transform coefficients over the 4M windowed output samples.
    /// </summary>
    /// <remarks>
    /// The two halves of the block are the same folded coefficients against two
    /// different stretches of window, because <c>y[n + 2M] = −y[n]</c> — the
    /// antiperiodicity of the transform's own cosine. Both window factors carry
    /// the fold's sign and the <c>−1/M</c> of the definition, so this loop is one
    /// multiplication per output sample.
    /// </remarks>
    private void Fold()
    {
        int half = 2 * FrameLength;
        int[] source = plan.Source;
        double[] first = plan.FirstHalfWindow;
        double[] second = plan.SecondHalfWindow;
        for (int n = 0; n < half; n++)
        {
            double value = transformed[source[n]];
            folded[n] = value * first[n];
            folded[half + n] = value * second[n];
        }
    }

    /// <summary>
    /// Everything about one frame length that does not depend on the channel.
    /// </summary>
    private sealed class Plan
    {
        private static readonly Plan?[] Built = new Plan?[EldWindow.FrameLengths.Length];
        private static readonly object Gate = new();

        private Plan(int frameLength)
        {
            int m = frameLength;
            DelayShift = EldWindow.DelayShift(m);
            ReadOnlySpan<double> window = EldWindow.For(m);

            Cosines = new double[m * m];
            double phi = Math.PI / m;
            for (int row = 0; row < m; row++)
            {
                for (int k = 0; k < m; k++)
                    Cosines[(row * m) + k] = Math.Cos(phi * (row + 0.5) * (k + 0.5));
            }

            // The three folds of C onto the first half of the block: reflected
            // below M/2, straight in the middle, reflected and negated above
            // 3M/2. Beyond 2M the whole thing repeats negated, which is what the
            // second window carries.
            Source = new int[2 * m];
            FirstHalfWindow = new double[2 * m];
            SecondHalfWindow = new double[2 * m];
            for (int n = 0; n < 2 * m; n++)
            {
                int index;
                double sign;
                if (n < m / 2)
                {
                    index = (m / 2) - 1 - n;
                    sign = 1;
                }
                else if (n < 3 * m / 2)
                {
                    index = n - (m / 2);
                    sign = 1;
                }
                else
                {
                    index = (5 * m / 2) - 1 - n;
                    sign = -1;
                }
                Source[n] = index;
                FirstHalfWindow[n] = -sign * window[n] / m;
                SecondHalfWindow[n] = sign * window[(2 * m) + n] / m;
            }
        }

        public int DelayShift { get; }

        /// <summary>The DCT-IV matrix, row major.</summary>
        public double[] Cosines { get; }

        /// <summary>Which transform coefficient each output sample of the first half comes from.</summary>
        public int[] Source { get; }

        /// <summary>Window, sign and scale for output samples 0 to 2M−1.</summary>
        public double[] FirstHalfWindow { get; }

        /// <summary>Window, sign and scale for output samples 2M to 4M−1.</summary>
        public double[] SecondHalfWindow { get; }

        public static Plan For(int frameLength)
        {
            int slot = Array.IndexOf(EldWindow.FrameLengths, frameLength);
            if (slot < 0)
                throw new ArgumentOutOfRangeException(nameof(frameLength), frameLength,
                    "L'ELD ne connait que 480 ou 512 echantillons par trame.");
            lock (Gate)
                return Built[slot] ??= new Plan(frameLength);
        }
    }
}
