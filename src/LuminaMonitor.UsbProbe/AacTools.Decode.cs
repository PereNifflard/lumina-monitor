using LuminaMonitor.Core.Media.Aac;
using LuminaMonitor.Core.Media.Aac.Tables;
using AacConfig = LuminaMonitor.Core.Media.Aac.AudioSpecificConfig;

/// <summary>
/// The AAC-ELD decoder itself, against frames whose answer is known before the
/// decoder is asked.
/// </summary>
/// <remarks>
/// <para>The other half of <c>AacTools</c> checks the normative tables. This half
/// checks the decoder that reads them, and it does so without a reference decoder
/// to compare against: every frame here is built bit by bit in this file and its
/// expected output is computed a second way — by hand for the spectrum, then
/// through the filterbank the decoder itself uses.</para>
///
/// <para><b>What that proves and what it does not.</b> It proves the decoder
/// reads back exactly what these bits mean, which catches a field in the wrong
/// order, a channel crossed with the other, an escape read before its sign bit, a
/// scalefactor applied to the wrong band, an overlap that does not accumulate.
/// It cannot prove that this file's idea of the syntax is the standard's: writer
/// and reader are the same pair of eyes. Only a real sound from the phone settles
/// that, which is what <c>decode-audio</c> is for.</para>
/// </remarks>
internal static partial class AacTools
{

    /// <summary>
    /// The decoder itself, against the two frames whose answer is known exactly.
    /// </summary>
    /// <remarks>
    /// <para>The first is the phone's own silent frame, <c>00 68 34 00</c>, which
    /// every packet of a silent session is: stereo, <c>max_sfb</c> zero, no
    /// mid/side, no TNS, a <c>global_gain</c> of 104 that applies to nothing. Its
    /// whole syntax is 26 bits and its output is 480 zeroes twice over, so the
    /// check is exact in both directions — a decoder that read one field wrong
    /// would consume a different number of bits even though its output would look
    /// just as silent.</para>
    ///
    /// <para>The second is built here, bit by bit, with four known spectral values
    /// per channel and a different gain in each: its expected output is computed
    /// independently by feeding the spectrum straight into a filterbank of its
    /// own. That catches what the silent frame cannot — a field read in the wrong
    /// order, the two channels crossed, the scalefactor applied to the wrong
    /// band — because all of those move the values and none of them changes the
    /// bit count.</para>
    ///
    /// <para>The third is a truncated frame, which must be refused at the bit it
    /// runs out on rather than quietly read as zeroes.</para>
    /// </remarks>
    public static int SelfTestDecode(Action<string> say)
    {
        var report = new Report(say);
        var config = AacConfig.Parse(LuminaMonitor.Core.Media.AacProbe.PhoneConfig);
        say("=== AudioSpecificConfig du telephone ===");
        say($"  F8 E6 40 00 : {config}");
        report.Line(config.ObjectType == 39 && config.SamplingFrequencyIndex == 3 && config.ChannelConfiguration == 2,
            "ASC du telephone", $"objet {config.ObjectType}, index {config.SamplingFrequencyIndex},"
            + $" {config.ChannelConfiguration} canaux, {config.ConfigBits} bits lus");

        say("");
        say("=== Trame silencieuse 00 68 34 00 (celle que le telephone envoie) ===");
        foreach (int frameLength in EldWindow.FrameLengths)
            CheckSilentFrame(report, config, frameLength);

        say("");
        say("=== Trames construites ici, spectre connu ligne par ligne ===");
        foreach (int frameLength in EldWindow.FrameLengths)
        {
            CheckQuadrupleFrame(report, config, frameLength);
            CheckMonoFrame(report, config, frameLength);
            CheckEscapeFrame(report, config, frameLength);
            CheckMiddleSideFrame(report, config, frameLength);
            CheckTnsFrame(report, config, frameLength);
        }

        say("");
        say("=== Trame tronquee ===");
        CheckTruncatedFrame(report, config);

        say("");
        say("=== Allocation par trame ===");
        foreach (int frameLength in EldWindow.FrameLengths)
            CheckNoAllocation(report, config, frameLength);

        say("");
        say("=== Trames aleatoires : tous les livres, PNS, intensite, TNS ===");
        foreach (int frameLength in EldWindow.FrameLengths)
            CheckRandomFrames(report, config, frameLength);

        say("");
        return report.Finish();
    }

    /// <summary>The phone's silent frame: 26 bits of syntax and nothing but zeroes out.</summary>
    private static void CheckSilentFrame(Report report, AacConfig config, int frameLength)
    {
        // max_sfb (6, zero), ms_mask_present (2, zero), then each channel's
        // global_gain (8) and tns_data_present (1): 6 + 2 + 9 + 9 = 26.
        const int ExpectedBits = 26;
        byte[] frame = [0x00, 0x68, 0x34, 0x00];
        string name = $"silence / {frameLength}";

        var decoder = new AacEldDecoder(config.WithFrameLength(frameLength));
        var pcm = new float[frameLength * 2];
        bool ok = decoder.Decode(frame, pcm, out int samples);
        if (!ok)
        {
            report.Fail(name, $"refusee : {decoder.LastFailure?.Message}");
            return;
        }

        double peak = 0;
        foreach (float sample in pcm)
            peak = Math.Max(peak, Math.Abs(sample));
        bool good = decoder.BitsConsumed == ExpectedBits && decoder.PaddingIsZero
            && samples == frameLength && peak == 0;
        report.Line(good, name, $"{decoder.BitsConsumed} bits utiles sur {decoder.BitsAvailable},"
            + $" bourrage {(decoder.PaddingIsZero ? "nul" : "NON NUL")},"
            + $" {samples} x 2 echantillons, pic {peak:E1}"
            + (decoder.BitsConsumed == ExpectedBits ? "" : $" — {ExpectedBits} bits attendus"));
    }

    /// <summary>
    /// Four known values per channel, two different gains: the spectral path and
    /// the scalefactors, with nothing else in the way.
    /// </summary>
    /// <remarks>
    /// Two different quadruples and two different gains, so that crossing the
    /// channels or sharing one channel's scalefactor with the other cannot pass.
    /// </remarks>
    private static void CheckQuadrupleFrame(Report report, AacConfig config, int frameLength)
    {
        int[] left = [1, -1, 0, 1];
        int[] right = [0, 1, 1, -1];
        var writer = new BitWriter();
        writer.Write(1, 6);                    // max_sfb: one band, four lines
        writer.Write(0, 2);                    // ms_mask_present: none
        WriteChannel(writer, 110, left);
        WriteChannel(writer, 102, right);

        double[][] spectra = [Spectrum(frameLength, left, 110), Spectrum(frameLength, right, 102)];
        CheckAgainstSpectra(report, config, frameLength, "quadruplet", writer, spectra);
    }

    /// <summary>
    /// A mono frame, which is the only shape where <c>max_sfb</c> lives inside the
    /// channel stream.
    /// </summary>
    /// <remarks>
    /// A stereo pair is always a common window, so its <c>max_sfb</c> is read once
    /// at the element level; a single channel element has no common window and
    /// carries its own, after <c>global_gain</c> rather than before it. The phone
    /// sends stereo, so this path would otherwise never run — and an unexercised
    /// branch in a bitstream parser is a branch that does not work.
    /// </remarks>
    private static void CheckMonoFrame(Report report, AacConfig config, int frameLength)
    {
        const int Gain = 112;
        int[] values = [1, 1, -1, 0];
        var writer = new BitWriter();
        writer.Write(Gain, 8);                 // global_gain comes first here
        writer.Write(1, 6);                    // max_sfb, inside the stream
        writer.Write(1, 4);                    // section_data: codebook 1
        writer.Write(1, 5);                    // one band long
        WriteCodeword(writer, HuffmanTables.Scalefactor, 60);
        writer.Write(0, 1);                    // tns_data_present
        WriteCodeword(writer, HuffmanTables.Spectral[1], Book1Index(values));

        var mono = config with { ChannelConfiguration = 1 };
        CheckAgainstSpectra(report, mono, frameLength, "mono", writer,
            [Spectrum(frameLength, values, Gain)]);
    }

    /// <summary>
    /// Codebook 11 and its escape: the path every loud frame of real music takes.
    /// </summary>
    /// <remarks>
    /// Book 11 is the only book whose values may exceed its own LAV, and the three
    /// things that follow one of its codewords have to be read in one order and no
    /// other: the codeword, then a sign bit for each value that is not zero, then
    /// an escape for each value that came out at exactly ±16. Reading the escape
    /// before the sign gives a decoder that works on every quiet frame and turns
    /// every loud one into noise, which is the worst possible failure mode because
    /// it looks like a filterbank problem. Here the pair (16, 0) is escaped to
    /// (20, 0): the sign bit belongs to the 16, the escape belongs to the 16, and
    /// the zero takes neither.
    /// </remarks>
    private static void CheckEscapeFrame(Report report, AacConfig config, int frameLength)
    {
        const int Gain = 108;
        const int Escaped = 20;           // 4 + 2^4, the shortest escape the syntax has
        var writer = new BitWriter();
        writer.Write(1, 6);                    // max_sfb: one band, four lines
        writer.Write(0, 2);                    // ms_mask_present: none
        for (int channel = 0; channel < 2; channel++)
        {
            writer.Write(Gain, 8);
            writer.Write(11, 4);               // section_data: the escape book
            writer.Write(1, 5);                // one band long
            WriteCodeword(writer, HuffmanTables.Scalefactor, 60);
            writer.Write(0, 1);                // tns_data_present
            WriteCodeword(writer, HuffmanTables.Spectral[11], 16 * 17);  // the pair (16, 0)
            writer.Write(0, 1);                // sign of the 16: positive
            writer.Write(0, 1);                // escape prefix: no ones, so four bits follow
            writer.Write(4, 4);                // 4 + 2^4 = 20
            WriteCodeword(writer, HuffmanTables.Spectral[11], 0);        // the pair (0, 0)
        }

        var spectrum = new double[frameLength];
        spectrum[0] = Math.Pow(Escaped, 4.0 / 3.0) * Math.Pow(2.0, 0.25 * (Gain - 100));
        CheckAgainstSpectra(report, config, frameLength, "echappement livre 11", writer,
            [spectrum, spectrum]);
    }

    /// <summary>
    /// Mid/side over the whole spectrum: the mask state that transmits no mask.
    /// </summary>
    /// <remarks>
    /// <c>ms_mask_present</c> is two bits and its third value, 2, means mid/side
    /// everywhere with no per-band bits at all. A reader that treats the field as
    /// a flag reads state 2 as "on" and then reads <c>max_sfb</c> mask bits that
    /// were never written, so every field after it is off by that many bits. This
    /// is the check for that, and the arithmetic it verifies is
    /// <c>L = M + S</c>, <c>R = M − S</c>.
    /// </remarks>
    private static void CheckMiddleSideFrame(Report report, AacConfig config, int frameLength)
    {
        int[] mid = [1, 0, 0, 0];
        int[] side = [0, 1, 0, 0];
        var writer = new BitWriter();
        writer.Write(1, 6);                    // max_sfb: one band
        writer.Write(2, 2);                    // ms_mask_present: the whole spectrum, no bits
        WriteChannel(writer, 110, mid);
        WriteChannel(writer, 106, side);

        double[] midSpectrum = Spectrum(frameLength, mid, 110);
        double[] sideSpectrum = Spectrum(frameLength, side, 106);
        var leftSpectrum = new double[frameLength];
        var rightSpectrum = new double[frameLength];
        for (int line = 0; line < frameLength; line++)
        {
            leftSpectrum[line] = midSpectrum[line] + sideSpectrum[line];
            rightSpectrum[line] = midSpectrum[line] - sideSpectrum[line];
        }
        CheckAgainstSpectra(report, config, frameLength, "mid/side (masque 2)", writer,
            [leftSpectrum, rightSpectrum]);
    }

    /// <summary>
    /// One TNS filter of order one over an impulse, which has a closed form.
    /// </summary>
    /// <remarks>
    /// An all-pole filter of order one, <c>y[n] = x[n] − r·y[n−1]</c>, fed a single
    /// non-zero line at the start of its range answers <c>x[0]·(−r)^n</c> — a
    /// geometric decay that can be written down here in one line and that depends
    /// on every part of the TNS path being right: the field order of
    /// <c>tns_data</c>, the two's complement of the coefficient, the asymmetric
    /// dequantization <c>sin(c/f)</c>, the direction bit, and the band range the
    /// filter covers. The second channel carries no TNS at all, so the same frame
    /// also checks that the tool stays inside the channel that asked for it.
    ///
    /// <para>The filter's length is written as the <em>whole</em> band count, not
    /// as <c>max_sfb</c>: the lengths of <c>tns_data</c> count down from the top
    /// of the full spectrum and are only afterwards clamped to <c>max_sfb</c> and
    /// to the frequency's TNS ceiling. A test that wrote <c>max_sfb</c> here would
    /// produce a filter of zero width and pass whatever the decoder did.</para>
    /// </remarks>
    private static void CheckTnsFrame(Report report, AacConfig config, int frameLength)
    {
        const int Bands = 4;              // bands 0 to 3, which are lines 0 to 15
        const int Lines = 4 * Bands;
        const int Gain = 110;
        const int Coefficient = 3;        // four-bit two's complement, resolution 4
        int[] impulse = [1, 0, 0, 0];
        int[] silent = [0, 0, 0, 0];
        int allBands = ScalefactorBands.Count(config.SamplingFrequencyIndex, frameLength);

        var writer = new BitWriter();
        writer.Write(Bands, 6);                // max_sfb
        writer.Write(0, 2);                    // ms_mask_present: none
        WriteChannel(writer, Gain, Bands, allBands, impulse, silent, silent, silent);
        WriteChannel(writer, Gain, Bands, 0, silent, silent, silent, silent);

        // sin(c / f) with f the positive-coefficient scale of the standard; the
        // whole filter is [1, r] at order one.
        double scale = ((1 << (4 - 1)) - 0.5) / (Math.PI / 2.0);
        double reflection = Math.Sin(Coefficient / scale);

        var filtered = new double[frameLength];
        double amplitude = Math.Pow(2.0, 0.25 * (Gain - 100));
        for (int line = 0; line < Lines; line++)
            filtered[line] = amplitude * Math.Pow(-reflection, line);
        CheckAgainstSpectra(report, config, frameLength, "TNS ordre 1", writer,
            [filtered, new double[frameLength]]);
    }

    /// <summary>
    /// Decodes a built frame several times over and compares every sample with a
    /// filterbank fed the expected spectrum by hand.
    /// </summary>
    /// <remarks>
    /// Several times because the filterbank carries three frames of overlap: a
    /// decoder that got the overlap wrong would still match on the first frame.
    /// </remarks>
    private static void CheckAgainstSpectra(Report report, AacConfig config, int frameLength,
        string label, BitWriter writer, double[][] spectra)
    {
        const int Frames = 6;
        string name = $"{label} / {frameLength}";
        byte[] frame = writer.ToArray();
        int writtenBits = writer.Bits;
        int count = spectra.Length;

        var decoder = new AacEldDecoder(config.WithFrameLength(frameLength));
        var pcm = new float[frameLength * count];
        var expected = new EldFilterBank[count];
        for (int channel = 0; channel < count; channel++)
            expected[channel] = new EldFilterBank(frameLength);
        var reference = new double[frameLength];

        double worst = 0;
        int bitsConsumed = 0;
        for (int frameIndex = 0; frameIndex < Frames; frameIndex++)
        {
            if (!decoder.Decode(frame, pcm, out _))
            {
                report.Fail(name, $"trame {frameIndex} refusee : {decoder.LastFailure?.Message}");
                return;
            }
            bitsConsumed = decoder.BitsConsumed;
            for (int channel = 0; channel < count; channel++)
            {
                expected[channel].Synthesize(spectra[channel], reference);
                for (int sample = 0; sample < frameLength; sample++)
                {
                    double got = pcm[(sample * count) + channel] * AacEldDecoder.FullScale;
                    worst = Math.Max(worst, Math.Abs(got - reference[sample]));
                }
            }
        }

        bool good = bitsConsumed == writtenBits && decoder.PaddingIsZero && worst <= 1e-3;
        report.Line(good, name, $"{bitsConsumed} bits lus pour {writtenBits} ecrits,"
            + $" bourrage {(decoder.PaddingIsZero ? "nul" : "NON NUL")},"
            + $" {Frames} trames, ecart maximal {worst:E2} (plein echelle {AacEldDecoder.FullScale:F0})");
    }

    /// <summary>One channel over one band: gain, section, scalefactor, no TNS, one codeword.</summary>
    private static void WriteChannel(BitWriter writer, int globalGain, int[] values) =>
        WriteChannel(writer, globalGain, 1, 0, values);

    /// <summary>
    /// One channel over several bands, optionally with a first-order TNS filter of
    /// the given length in bands (zero for no TNS at all).
    /// </summary>
    private static void WriteChannel(BitWriter writer, int globalGain, int bands, int tnsLength,
        params int[][] quadruples)
    {
        writer.Write((uint)globalGain, 8);
        writer.Write(1, 4);                    // section_data: codebook 1
        writer.Write((uint)bands, 5);          // over every band
        for (int band = 0; band < bands; band++)
            WriteCodeword(writer, HuffmanTables.Scalefactor, 60);   // scalefactor: no change
        writer.Write(tnsLength > 0 ? 1u : 0u, 1);   // tns_data_present
        if (tnsLength > 0)
        {
            writer.Write(1, 2);                // n_filt
            writer.Write(1, 1);                // coef_res - 3, so resolution 4
            writer.Write((uint)tnsLength, 6);  // length, counted down from the top band
            writer.Write(1, 5);                // order
            writer.Write(0, 1);                // direction: up the spectrum
            writer.Write(0, 1);                // coef_compress
            writer.Write(3, 4);                // the coefficient itself
        }
        for (int band = 0; band < bands; band++)
            WriteCodeword(writer, HuffmanTables.Spectral[1], Book1Index(quadruples[band]));
    }

    /// <summary>Where a signed quadruple sits in book 1's table: base 3, offset by its LAV.</summary>
    private static int Book1Index(int[] values)
    {
        int index = 0;
        foreach (int value in values)
            index = (index * 3) + value + 1;
        return index;
    }

    /// <summary>The spectrum the built frame's channel must dequantize to.</summary>
    private static double[] Spectrum(int frameLength, int[] values, int scaleFactor)
    {
        var spectrum = new double[frameLength];
        double gain = Math.Pow(2.0, 0.25 * (scaleFactor - 100));
        for (int line = 0; line < values.Length; line++)
            spectrum[line] = Math.Sign(values[line]) * Math.Pow(Math.Abs(values[line]), 4.0 / 3.0) * gain;
        return spectrum;
    }

    /// <summary>Writes one codeword of a book, most significant bit first.</summary>
    private static void WriteCodeword(BitWriter writer, AacCodebook book, int index) =>
        writer.Write(book.Codewords[index], book.Lengths[index]);

    /// <summary>
    /// Nothing allocated per frame once the decoder is warm.
    /// </summary>
    /// <remarks>
    /// A hundred frames a second forever is not a load, but a hundred allocations
    /// a second forever is a collection every few minutes, and a collection in the
    /// middle of an audio callback is a gap in the sound. So the decoder owns every
    /// buffer it uses and reads the access unit where it lies; this asserts that
    /// rather than hoping for it. The frame used here carries sections,
    /// scalefactors, spectral data and a TNS filter, so every stage runs.
    /// </remarks>
    private static void CheckNoAllocation(Report report, AacConfig config, int frameLength)
    {
        const int Frames = 1000;
        int allBands = ScalefactorBands.Count(config.SamplingFrequencyIndex, frameLength);
        int[] values = [1, -1, 0, 1];
        var writer = new BitWriter();
        writer.Write(4, 6);                    // max_sfb: four bands
        writer.Write(1, 2);                    // ms_mask_present: a transmitted mask
        for (int band = 0; band < 4; band++)
            writer.Write(1, 1);                // mid/side on every transmitted band
        WriteChannel(writer, 110, 4, allBands, values, values, values, values);
        WriteChannel(writer, 104, 4, 0, values, values, values, values);
        byte[] frame = writer.ToArray();

        var decoder = new AacEldDecoder(config.WithFrameLength(frameLength));
        var pcm = new float[frameLength * 2];
        for (int warm = 0; warm < 4; warm++)
            decoder.Decode(frame, pcm, out _);   // the tables and the jitted code

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int frameIndex = 0; frameIndex < Frames; frameIndex++)
            decoder.Decode(frame, pcm, out _);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        report.Line(allocated == 0 && decoder.FramesFailed == 0, $"allocation / {frameLength}",
            $"{allocated} octet(s) alloue(s) sur {Frames} trames de {frame.Length} octets");
    }

    /// <summary>
    /// Legal frames drawn at random: every codebook, noise substitution, intensity
    /// stereo, TNS, many sections, deep scalefactor chains.
    /// </summary>
    /// <remarks>
    /// <para>The frames above are each aimed at one thing. This one is aimed at
    /// coverage: nothing in the silent stream and nothing built by hand ever
    /// reaches codebooks 2 to 10, a noise band, an intensity band, a frame of
    /// twenty sections or a scalefactor chain that wanders. A decoder can be wrong
    /// there in ways that do not show up until the phone plays music, and then it
    /// shows up as noise with no clue attached.</para>
    ///
    /// <para><b>What is checked.</b> That the decoder consumes exactly the bits
    /// that were written — the one invariant that a generator and a parser cannot
    /// both get wrong by accident, because any disagreement about a field's width
    /// or position shifts the count — and that no sample comes out as NaN or
    /// infinity. The values themselves are not checked against a second
    /// computation: that would be a second decoder. What this catches is a crash,
    /// an index out of range, an unstable filter, and above all a field this parser
    /// reads at the wrong width.</para>
    /// </remarks>
    private static void CheckRandomFrames(Report report, AacConfig config, int frameLength)
    {
        const int Frames = 400;
        string name = $"aleatoire / {frameLength}";
        var random = new Random(frameLength);   // repeatable: a failure can be looked at twice
        ushort[] offsets = ScalefactorBands.LowDelay(config.SamplingFrequencyIndex, frameLength).ToArray();
        int bands = offsets.Length - 1;
        var decoder = new AacEldDecoder(config.WithFrameLength(frameLength));
        var pcm = new float[frameLength * 2];
        var used = new HashSet<int>();
        int mismatched = 0, notFinite = 0, noise = 0, intensity = 0, tns = 0, sections = 0, bytes = 0;
        double peak = 0;
        var clock = new System.Diagnostics.Stopwatch();
        var elapsed = new List<double>(Frames);

        for (int frameIndex = 0; frameIndex < Frames; frameIndex++)
        {
            var writer = new BitWriter();
            int maxSfb = random.Next(bands + 1);
            writer.Write((uint)maxSfb, 6);
            int maskState = random.Next(3);
            writer.Write((uint)maskState, 2);
            if (maskState == 1)
            {
                for (int band = 0; band < maxSfb; band++)
                    writer.Write((uint)random.Next(2), 1);
            }
            var codebooks = new int[bands];
            for (int channel = 0; channel < 2; channel++)
            {
                WriteRandomChannel(writer, random, maxSfb, codebooks, offsets, ref tns);
                sections += CountSections(codebooks, maxSfb);
                for (int band = 0; band < maxSfb; band++)
                {
                    used.Add(codebooks[band]);
                    if (codebooks[band] == 13) noise++;
                    if (codebooks[band] is 14 or 15) intensity++;
                }
            }

            byte[] frame = writer.ToArray();
            clock.Restart();
            bool ok = decoder.Decode(frame, pcm, out _);
            clock.Stop();
            if (frameIndex > 0)
                elapsed.Add(clock.Elapsed.TotalMilliseconds);
            if (!ok)
            {
                report.Fail(name, $"trame {frameIndex} refusee : {decoder.LastFailure?.Message}");
                return;
            }
            bytes += frame.Length;
            if (decoder.BitsConsumed != writer.Bits || !decoder.PaddingIsZero)
                mismatched++;
            foreach (float sample in pcm)
            {
                if (float.IsFinite(sample))
                    peak = Math.Max(peak, Math.Abs(sample));
                else
                    notFinite++;
            }
        }

        bool good = mismatched == 0 && notFinite == 0;
        elapsed.Sort();
        report.Line(good, name, $"{Frames} trames de {bytes / Frames} octets en moyenne,"
            + $" {sections} sections, livres vus {used.Count}, bandes de bruit {noise},"
            + $" d'intensite {intensity}, filtres TNS {tns},"
            + $" desaccords de bits {mismatched}, non finis {notFinite}, pic {peak:E2}");
        report.Note($"{name} : {elapsed.Sum() / elapsed.Count:F3} ms par trame en moyenne,"
            + $" mediane {elapsed[elapsed.Count / 2]:F3} ms, {elapsed[^1]:F3} ms au pire"
            + $" (trames pleines, budget 10 ms)");
    }

    /// <summary>One random channel: sections, scalefactors, TNS and spectral values.</summary>
    private static void WriteRandomChannel(BitWriter writer, Random random, int maxSfb,
        int[] codebooks, ReadOnlySpan<ushort> offsets, ref int tnsFilters)
    {
        int bands = offsets.Length - 1;
        int globalGain = random.Next(60, 200);
        writer.Write((uint)globalGain, 8);

        // Sections: a codebook and a run, the run written in five-bit instalments
        // with 31 as the escape, exactly as the reader unwinds it.
        Array.Clear(codebooks);
        int at = 0;
        while (at < maxSfb)
        {
            int codebook = random.Next(16);
            if (codebook == 12)
                codebook = 11;                  // 12 is the scalefactor book, not a section book
            int run = 1 + random.Next(maxSfb - at);
            writer.Write((uint)codebook, 4);
            for (int left = run; left >= 31; left -= 31)
                writer.Write(31, 5);
            writer.Write((uint)(run % 31), 5);
            for (int band = at; band < at + run; band++)
                codebooks[band] = codebook;
            at += run;
        }

        // Scalefactors: three independent chains, each kept inside its own range.
        int scaleFactor = globalGain;
        bool firstNoise = true;
        for (int band = 0; band < maxSfb; band++)
        {
            int codebook = codebooks[band];
            if (codebook == 0)
                continue;
            if (codebook is >= 1 and <= 11)
            {
                int delta = Math.Clamp(random.Next(-8, 9), -scaleFactor, 255 - scaleFactor);
                scaleFactor += delta;
                WriteCodeword(writer, HuffmanTables.Scalefactor, 60 + delta);
            }
            else if (codebook == 13 && firstNoise)
            {
                firstNoise = false;
                writer.Write((uint)random.Next(512), 9);
            }
            else
            {
                WriteCodeword(writer, HuffmanTables.Scalefactor, 60 + random.Next(-8, 9));
            }
        }

        // TNS: filters whose lengths count down from the top band, so their total
        // must not pass it.
        int filters = random.Next(4);
        writer.Write(filters > 0 ? 1u : 0u, 1);
        if (filters > 0)
        {
            tnsFilters += filters;
            writer.Write((uint)filters, 2);
            int resolution = random.Next(2);
            writer.Write((uint)resolution, 1);
            int top = bands;
            for (int filter = 0; filter < filters; filter++)
            {
                int length = random.Next(top + 1);
                top -= length;
                writer.Write((uint)length, 6);
                int order = random.Next(21);     // the 48 kHz ceiling
                writer.Write((uint)order, 5);
                if (order == 0)
                    continue;
                writer.Write((uint)random.Next(2), 1);      // direction
                int compress = random.Next(2);
                writer.Write((uint)compress, 1);
                int width = resolution + 3 - compress;
                for (int coefficient = 0; coefficient < order; coefficient++)
                    writer.Write((uint)random.Next(1 << width), width);
            }
        }

        WriteRandomSpectrum(writer, random, codebooks, maxSfb, offsets);
    }

    /// <summary>
    /// The spectral values of one random channel, book by book.
    /// </summary>
    /// <remarks>
    /// One tuple per four or two lines of the band, and the bands are not all four
    /// lines wide: the low delay tables run from four lines at the bottom to
    /// thirty-two at the top. A writer that assumed one tuple per band would leave
    /// the reader asking for codewords nobody wrote, which is the failure this
    /// whole test is looking for and would rather not manufacture itself.
    /// </remarks>
    private static void WriteRandomSpectrum(BitWriter writer, Random random, int[] codebooks, int maxSfb,
        ReadOnlySpan<ushort> offsets)
    {
        Span<int> tuple = stackalloc int[4];
        for (int band = 0; band < maxSfb; band++)
        {
            int number = codebooks[band];
            if (number is < 1 or > 11)
                continue;
            var book = HuffmanTables.Spectral[number];
            int step = book.Dimension;
            int lines = offsets[band + 1] - offsets[band];
            for (int tupleIndex = 0; tupleIndex < lines / step; tupleIndex++)
            {
                int index = 0;
                for (int value = 0; value < step; value++)
                {
                    tuple[value] = random.Next(-book.Lav, book.Lav + 1);
                    int digit = book.Signed ? tuple[value] + book.Lav : Math.Abs(tuple[value]);
                    index = (index * (book.Signed ? (2 * book.Lav) + 1 : book.Lav + 1)) + digit;
                }
                WriteCodeword(writer, book, index);
                if (!book.Signed)
                {
                    for (int value = 0; value < step; value++)
                    {
                        if (tuple[value] != 0)
                            writer.Write(tuple[value] < 0 ? 1u : 0u, 1);
                    }
                }
                if (number == 11)
                {
                    for (int value = 0; value < step; value++)
                    {
                        if (Math.Abs(tuple[value]) != book.Lav)
                            continue;
                        writer.Write(0, 1);                         // escape prefix: four bits follow
                        writer.Write((uint)random.Next(16), 4);     // 16 to 31
                    }
                }
            }
        }
    }

    /// <summary>How many sections a codebook map describes, for the coverage line.</summary>
    private static int CountSections(int[] codebooks, int maxSfb)
    {
        int sections = 0;
        for (int band = 0; band < maxSfb; band++)
        {
            if (band == 0 || codebooks[band] != codebooks[band - 1])
                sections++;
        }
        return sections;
    }

    /// <summary>A frame cut in half must be refused where it stops, not read as zeroes.</summary>
    private static void CheckTruncatedFrame(Report report, AacConfig config)
    {
        byte[] frame = [0x00, 0x68];
        var decoder = new AacEldDecoder(config.WithFrameLength(480));
        var pcm = new float[480 * 2];
        bool refused = !decoder.Decode(frame, pcm, out _);
        var failure = decoder.LastFailure;
        bool good = refused && failure is not null && failure.BitPosition == 16 && decoder.FramesFailed == 1;
        report.Line(good, "trame tronquee", refused
            ? $"refusee au bit {failure?.BitPosition} sur {failure?.BitsAvailable} : {failure?.Reason}"
            : "acceptee, ce qui est le defaut a eviter");
    }

    /// <summary>A bitstream writer, only so that the decoder can be handed a frame nobody else wrote.</summary>
    private sealed class BitWriter
    {
        private readonly List<byte> bytes = [];
        private int partial;
        private int filled;

        /// <summary>How many bits have been written, padding excluded.</summary>
        public int Bits { get; private set; }

        public void Write(uint value, int count)
        {
            for (int bit = count - 1; bit >= 0; bit--)
            {
                partial = (partial << 1) | (int)((value >> bit) & 1);
                if (++filled == 8)
                {
                    bytes.Add((byte)partial);
                    partial = 0;
                    filled = 0;
                }
                Bits++;
            }
        }

        /// <summary>The frame, padded to a byte boundary with zeroes as the standard asks.</summary>
        public byte[] ToArray()
        {
            var all = new List<byte>(bytes);
            if (filled > 0)
                all.Add((byte)(partial << (8 - filled)));
            return [.. all];
        }
    }

}
