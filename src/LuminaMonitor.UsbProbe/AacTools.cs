using LuminaMonitor.Core.Media.Aac;
using LuminaMonitor.Core.Media.Aac.Tables;

/// <summary>
/// The AAC-ELD tables, checked against mathematics rather than against a second
/// copy of themselves.
/// </summary>
/// <remarks>
/// <para>Everything in <c>Core/Media/Aac/Tables</c> is data read out of the ISO
/// reference software, and a transcription error there does not look like an
/// error: it looks like a decoder that produces noise, three weeks later, for
/// reasons that appear to be in the bitstream parser. So each family of tables is
/// checked here against a property it cannot accidentally satisfy.</para>
///
/// <list type="bullet">
/// <item><description>A Huffman book must be a prefix code — no codeword the
/// start of another — and a complete one, its Kraft sum exactly 1. It must hold
/// as many codewords as its dimension and LAV demand, and decoding each codeword
/// bit by bit must give back its own index.</description></item>
/// <item><description>A scalefactor band table must be strictly increasing, start
/// at zero and end at the frame length.</description></item>
/// <item><description>A TNS ceiling must fit inside the band table it applies
/// to.</description></item>
/// <item><description>The ELD window must reconstruct. This is the real test:
/// the low delay filterbank is implemented here from its definition, a test
/// signal goes through analysis and synthesis, and the output must come back as
/// the input to rounding error, at whatever delay the framing imposes. One wrong
/// coefficient and it does not.</description></item>
/// </list>
///
/// <para>The analysis below is written from the transform's definition — the
/// direct sum, no fast algorithm — because its only job is to be obviously the
/// formula. The synthesis it is checked against is the decoder's own,
/// <c>Core/Media/Aac/EldFilterBank.cs</c>, which folds the same definition onto
/// one DCT-IV: so this test checks the window table <em>and</em> the fast
/// filterbank the decoder actually uses, and the two cannot drift apart.</para>
/// </remarks>
internal static partial class AacTools
{
    /// <summary>The residual an eight-decimal window leaves; anything above this is a defect.</summary>
    /// <remarks>
    /// Measured at 1.5e-8 for the 512 window and 1.3e-8 for the 480 one, which is
    /// the printing precision of the reference-software headers and nothing else:
    /// the same window to ten significant digits reconstructs to 4.3e-11. The
    /// ceiling sits a factor of three above that, which is where the test has
    /// teeth: moving one coefficient of the 512 window by 1e-6 — a single wrong
    /// digit in the sixth decimal, the smallest transcription error worth calling
    /// one — takes the residual to 1.5e-7 and fails. Rounding differences between
    /// machines are fifteen decades below the margin.
    /// </remarks>
    private const double ReconstructionCeiling = 5e-8;

    /// <summary>Runs every check and reports each one; zero only when all of them pass.</summary>
    public static int TablesSelfTest(Action<string> say)
    {
        var report = new Report(say);
        say("=== Provenance ===");
        say($"  document : {TablesProvenance.Document}");
        say($"  source   : {TablesProvenance.Source} (recupere le {TablesProvenance.Retrieved})");
        say($"  arbre    : {TablesProvenance.SourceTree}");
        say($"  ELD      : {TablesProvenance.EldAmendment}");
        say("  licence  : notice de module logiciel MPEG, PAS le MIT du depot"
            + " — voir docs/AAC_ELD_TABLES.md");

        say("");
        say("=== Livres de Huffman (prefixe, Kraft, dimension, aller-retour) ===");
        for (int number = 1; number <= 11; number++)
            CheckCodebook(report, HuffmanTables.Spectral[number]);
        CheckCodebook(report, HuffmanTables.Scalefactor);

        say("");
        say("=== Bandes de facteurs d'echelle (swb_offset) ===");
        foreach ((int index, string label) in new[] { (3, "48 kHz"), (5, "32 kHz"), (6, "24 kHz") })
            foreach (int frameLength in EldWindow.FrameLengths)
                CheckScalefactorBands(report, index, frameLength, label);

        say("");
        say("=== Plafonds TNS basse latence ===");
        foreach ((int index, string label) in new[] { (3, "48 kHz"), (4, "44,1 kHz"), (5, "32 kHz"), (6, "24 kHz") })
            CheckTns(report, index, label);

        say("");
        say("=== Fenetre du banc de filtres basse latence : analyse puis synthese ===");
        foreach (int frameLength in EldWindow.FrameLengths)
            CheckEldWindow(report, frameLength);

        say("");
        return report.Finish();
    }

    /// <summary>Checks one Huffman book four ways.</summary>
    private static void CheckCodebook(Report report, AacCodebook book)
    {
        string name = book.Index == 12 ? "livre des facteurs" : $"livre spectral {book.Index}";
        string shape = $"dim {book.Dimension}, LAV {book.Lav}, {(book.Signed ? "signe" : "non signe")}";

        if (book.Lengths.Length != book.Codewords.Length)
        {
            report.Fail(name, $"{book.Lengths.Length} longueurs pour {book.Codewords.Length} mots de code");
            return;
        }
        if (book.Entries != book.ExpectedEntries)
        {
            report.Fail(name, $"{book.Entries} entrees, {book.ExpectedEntries} attendues pour {shape}");
            return;
        }

        // A codeword that does not fit in its own length would decode as another.
        int longest = 0;
        foreach (byte length in book.Lengths)
            longest = Math.Max(longest, length);
        for (int i = 0; i < book.Entries; i++)
        {
            int length = book.Lengths[i];
            if (length is < 1 or > 32)
            {
                report.Fail(name, $"entree {i} : longueur {length}");
                return;
            }
            if (book.Codewords[i] >> length != 0)
            {
                report.Fail(name, $"entree {i} : mot de code {book.Codewords[i]} au-dela de {length} bits");
                return;
            }
        }
        if (longest != book.MaxCodewordBits)
        {
            report.Fail(name, $"mot le plus long {longest} bits, {book.MaxCodewordBits} annonces");
            return;
        }

        // Kraft: exactly 1 for a complete prefix code. Summed as integers over a
        // common denominator of 2^longest, so the equality is exact.
        long units = 0;
        for (int i = 0; i < book.Entries; i++)
            units += 1L << (longest - book.Lengths[i]);
        long full = 1L << longest;
        if (units != full)
        {
            report.Fail(name, $"somme de Kraft {units}/{full} — code {(units < full ? "incomplet" : "surcharge")}");
            return;
        }

        // Prefix property: a code is a prefix of another exactly when truncating
        // the longer one to the shorter length reproduces it. Checking every
        // (length, prefix) pair against the set of codes settles it.
        var codes = new HashSet<(int Length, uint Code)>();
        for (int i = 0; i < book.Entries; i++)
        {
            if (!codes.Add((book.Lengths[i], book.Codewords[i])))
            {
                report.Fail(name, $"entree {i} : mot de code en double ({book.Lengths[i]} bits, {book.Codewords[i]})");
                return;
            }
        }
        for (int i = 0; i < book.Entries; i++)
        {
            int length = book.Lengths[i];
            for (int shorter = 1; shorter < length; shorter++)
            {
                if (codes.Contains((shorter, book.Codewords[i] >> (length - shorter))))
                {
                    report.Fail(name, $"entree {i} a pour prefixe un autre mot de code de {shorter} bits");
                    return;
                }
            }
        }

        // Round trip: read each codeword bit by bit through a decoder built from
        // the table and check it lands on its own index. A book that passes Kraft
        // and the prefix test can still be mis-transcribed into a book of a
        // different shape; this catches that.
        var decoder = new Dictionary<(int Length, uint Code), int>();
        for (int i = 0; i < book.Entries; i++)
            decoder[(book.Lengths[i], book.Codewords[i])] = i;
        for (int i = 0; i < book.Entries; i++)
        {
            uint accumulator = 0;
            int decoded = -1;
            for (int bit = 0; bit < book.Lengths[i]; bit++)
            {
                accumulator = (accumulator << 1) | ((book.Codewords[i] >> (book.Lengths[i] - 1 - bit)) & 1);
                if (decoder.TryGetValue((bit + 1, accumulator), out int candidate))
                {
                    decoded = candidate;
                    break;
                }
            }
            if (decoded != i)
            {
                report.Fail(name, $"entree {i} se decode en {decoded}");
                return;
            }
        }

        report.Pass(name, $"{book.Entries} entrees ({shape}), Kraft = 1, prefixe libre,"
            + $" aller-retour complet, {longest} bits au plus");
    }

    /// <summary>Checks one band table's shape against the frame length it serves.</summary>
    private static void CheckScalefactorBands(Report report, int samplingFrequencyIndex, int frameLength, string label)
    {
        string name = $"swb_offset {label} / {frameLength}";
        ReadOnlySpan<ushort> offsets = ScalefactorBands.LowDelay(samplingFrequencyIndex, frameLength);
        if (offsets.Length < 2)
        {
            report.Fail(name, "table vide");
            return;
        }
        if (offsets[0] != 0)
        {
            report.Fail(name, $"commence a {offsets[0]} et non a 0");
            return;
        }
        for (int band = 1; band < offsets.Length; band++)
        {
            if (offsets[band] <= offsets[band - 1])
            {
                report.Fail(name, $"bande {band - 1} : {offsets[band - 1]} puis {offsets[band]}, pas strictement croissant");
                return;
            }
            if ((offsets[band] - offsets[band - 1]) % 4 != 0)
            {
                report.Fail(name, $"bande {band - 1} large de {offsets[band] - offsets[band - 1]} lignes, pas un multiple de 4");
                return;
            }
        }
        if (offsets[^1] != frameLength)
        {
            report.Fail(name, $"finit a {offsets[^1]} et non a {frameLength}");
            return;
        }
        report.Pass(name, $"{ScalefactorBands.Count(samplingFrequencyIndex, frameLength)} bandes,"
            + $" croissantes, derniere arete = {frameLength}");
    }

    /// <summary>Checks that a TNS ceiling fits the band table under it.</summary>
    private static void CheckTns(Report report, int samplingFrequencyIndex, string label)
    {
        string name = $"TNS {label}";
        foreach (int frameLength in EldWindow.FrameLengths)
        {
            int maxBands = TnsTables.MaxBands(samplingFrequencyIndex, frameLength);
            int bands = ScalefactorBands.Count(samplingFrequencyIndex, frameLength);
            if (maxBands < 1 || maxBands > bands)
            {
                report.Fail(name, $"{frameLength} : plafond {maxBands} pour {bands} bandes");
                return;
            }
        }
        int order = TnsTables.MaxOrder(samplingFrequencyIndex);
        if (order is < 1 or > 31)
        {
            report.Fail(name, $"ordre maximal {order}");
            return;
        }
        report.Pass(name, $"plafonds {TnsTables.MaxBands(samplingFrequencyIndex, 480)} (480)"
            + $" et {TnsTables.MaxBands(samplingFrequencyIndex, 512)} (512) bandes, ordre {order} au plus");
    }

    /// <summary>
    /// The one check that cannot be faked: the window must reconstruct through
    /// the filterbank it belongs to.
    /// </summary>
    private static void CheckEldWindow(Report report, int frameLength)
    {
        string name = $"fenetre ELD {frameLength}";
        ReadOnlySpan<double> window = EldWindow.For(frameLength);
        if (window.Length != 4 * frameLength)
        {
            report.Fail(name, $"{window.Length} coefficients, {4 * frameLength} attendus");
            return;
        }
        double peak = 0;
        foreach (double coefficient in window)
            peak = Math.Max(peak, Math.Abs(coefficient));
        if (peak < 0.5 || peak > 2)
        {
            report.Fail(name, $"amplitude maximale {peak:F6}, invraisemblable pour une fenetre");
            return;
        }

        var bank = new LowDelayAnalysis(frameLength);
        int expectedLag = frameLength - EldWindow.DelayShift(frameLength);
        var signals = new (string Label, Func<int, double> Sample)[]
        {
            ("bruit", Noise(frameLength)),
            ("sinus", n => 0.8 * Math.Sin(2 * Math.PI * 997.0 * n / 48000.0)),
            ("impulsion", n => n % (3 * frameLength) == frameLength / 3 ? 1.0 : 0.0),
        };
        bool ok = true;
        foreach ((string label, Func<int, double> sample) in signals)
        {
            (int lag, double error) = bank.RoundTrip(sample, frames: 14);
            bool good = error <= ReconstructionCeiling && lag == expectedLag;
            report.Line(good, $"{name} / {label}", $"retard {lag} echantillons, erreur maximale {error:E3}"
                + (lag == expectedLag ? "" : $" — retard attendu {expectedLag}")
                + (error <= ReconstructionCeiling ? "" : $" — plafond {ReconstructionCeiling:E0}"));
            ok &= good;
        }
        if (ok)
            report.Note($"{name} : reconstruction parfaite aux trois signaux, retard {expectedLag} echantillons"
                + $" (decalage de synthese {EldWindow.DelayShift(frameLength)})");
    }

    /// <summary>A repeatable pseudo-random signal, so a failure can be looked at twice.</summary>
    private static Func<int, double> Noise(int seed)
    {
        var random = new Random(seed);
        var drawn = new Dictionary<int, double>();
        return n =>
        {
            if (!drawn.TryGetValue(n, out double value))
            {
                value = random.NextDouble() * 2 - 1;
                drawn[n] = value;
            }
            return value;
        };
    }

    /// <summary>
    /// The analysis half of the ELD filterbank, written from its definition, and
    /// the round trip through the decoder's own synthesis.
    /// </summary>
    /// <remarks>
    /// <para>With <c>M</c> the frame length, analysis works on <c>4M</c> samples
    /// at a time and produces <c>M</c> spectral lines. Writing
    /// <c>phi = pi / M</c> and <c>no = (1 - M) / 2</c>:</para>
    /// <code>
    /// X[k] = -2 * sum(i = 0 .. 4M-1) xw[i] * cos(phi * (i - 2M + no) * (k + 0.5))
    /// </code>
    /// <para>where <c>xw</c> is the input block times the window <em>reversed</em>.
    /// The buffer holds three frames of history and the new frame. Those are the
    /// conventions of the reference encoder's <c>LDFB</c> and <c>buffer2freq</c>;
    /// the cosine kernel is built once here because the direct sum is otherwise
    /// slow enough to be annoying.</para>
    ///
    /// <para>The synthesis is not written again: it is
    /// <see cref="EldFilterBank"/>, the one the decoder runs. So a round trip
    /// that comes back to its input proves both that the window table is right
    /// and that the decoder's folded DCT-IV is the transform this direct sum
    /// inverts.</para>
    /// </remarks>
    private sealed class LowDelayAnalysis
    {
        private readonly int frameLength;
        private readonly int blockLength;
        private readonly double[] window;
        private readonly double[] analysisKernel;   // [k * blockLength + i]

        public LowDelayAnalysis(int frameLength)
        {
            this.frameLength = frameLength;
            blockLength = 4 * frameLength;
            window = EldWindow.For(frameLength).ToArray();

            double phi = Math.PI / frameLength;
            double offset = (1.0 - frameLength) / 2.0;
            analysisKernel = new double[frameLength * blockLength];
            for (int k = 0; k < frameLength; k++)
            {
                for (int i = 0; i < blockLength; i++)
                    analysisKernel[(k * blockLength) + i] =
                        Math.Cos(phi * (i - (2 * frameLength) + offset) * (k + 0.5));
            }
        }

        /// <summary>
        /// Pushes a signal through analysis then the decoder's synthesis and
        /// reports how far the output lags the input and how far off it is once
        /// lined up.
        /// </summary>
        public (int Lag, double MaxError) RoundTrip(Func<int, double> signal, int frames)
        {
            int m = frameLength;
            var input = new double[frames * m];
            for (int i = 0; i < input.Length; i++)
                input[i] = signal(i);

            var history = new double[blockLength];
            var windowed = new double[blockLength];
            var spectrum = new double[m];
            var output = new double[frames * m];
            var synthesis = new EldFilterBank(m);

            for (int frame = 0; frame < frames; frame++)
            {
                Array.Copy(history, m, history, 0, blockLength - m);
                Array.Copy(input, frame * m, history, blockLength - m, m);

                for (int i = 0; i < blockLength; i++)
                    windowed[i] = history[i] * window[blockLength - 1 - i];
                for (int k = 0; k < m; k++)
                {
                    double sum = 0;
                    int row = k * blockLength;
                    for (int i = 0; i < blockLength; i++)
                        sum += windowed[i] * analysisKernel[row + i];
                    spectrum[k] = -2 * sum;
                }
                synthesis.Synthesize(spectrum, output.AsSpan(frame * m, m));
            }

            // The first frames are the filterbank filling up; compare only once it
            // is running, and find the lag rather than assume it.
            int from = 5 * m, to = output.Length;
            int bestLag = -1;
            double bestError = double.MaxValue;
            for (int lag = 0; lag <= 2 * m; lag++)
            {
                double worst = 0;
                for (int i = from; i < to && worst < bestError; i++)
                    worst = Math.Max(worst, Math.Abs(output[i] - input[i - lag]));
                if (worst < bestError)
                {
                    bestError = worst;
                    bestLag = lag;
                }
            }
            return (bestLag, bestError);
        }
    }

    /// <summary>Keeps the tally so the exit code can mean something.</summary>
    private sealed class Report(Action<string> say)
    {
        private int passed;
        private int failed;

        public void Pass(string name, string detail) => Line(true, name, detail);

        public void Fail(string name, string detail) => Line(false, name, detail);

        public void Line(bool ok, string name, string detail)
        {
            if (ok) passed++; else failed++;
            say($"  {(ok ? "OK    " : "ECHEC ")}{name,-28} {detail}");
        }

        public void Note(string text) => say($"        {text}");

        public int Finish()
        {
            say(failed == 0
                ? $"*** {passed} verification(s) passee(s), aucun echec ***"
                : $"*** {failed} ECHEC(S) sur {passed + failed} verification(s) ***");
            return failed == 0 ? 0 : 9;
        }
    }
}
