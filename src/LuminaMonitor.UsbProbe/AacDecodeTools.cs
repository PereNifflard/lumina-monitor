using System.Buffers.Binary;
using System.Diagnostics;
using LuminaMonitor.Core.Media;
using LuminaMonitor.Core.Media.Aac;
using AacConfig = LuminaMonitor.Core.Media.Aac.AudioSpecificConfig;

/// <summary>
/// A recorded audio stream decoded offline, and every measurement that says
/// whether the decoder is right.
/// </summary>
/// <remarks>
/// <para>A decoder that produces finite samples is not a decoder that works: the
/// two ways to be wrong that no amount of staring at the output finds are a
/// syntax that reads the right number of fields in the wrong order, and a
/// filterbank whose phase convention does not match the encoder's. So this
/// command measures both directly.</para>
///
/// <list type="bullet">
/// <item><description><b>Bit consumption.</b> An AAC frame is padded to a byte
/// boundary and nothing else follows, so a correct parse always ends within seven
/// bits of the end of the packet, on zeroes. A frame that ends earlier read a
/// field too few; one that ends later read a field too many; one that ends on a
/// non-zero bit read the right number of the wrong fields. All three are counted,
/// and all three are defects even when the samples look plausible.</description></item>
/// <item><description><b>Continuity.</b> The mean absolute first difference at
/// frame boundaries against the mean everywhere else. The filterbank's whole job
/// is to make those joins invisible: a ratio near one means the overlap-add is
/// right, and a large one is the sound of a filterbank whose window, phase or
/// delay shift is wrong — a click every ten milliseconds.</description></item>
/// <item><description>RMS second by second, peak, and whether any sample came out
/// as NaN or infinity, which is what a dequantizer or a TNS filter that has gone
/// unstable produces.</description></item>
/// </list>
///
/// <para>The WAV is written so the sound can be listened to, which is the only
/// judge that matters. Everything above is what can be checked without
/// ears.</para>
/// </remarks>
internal static class AacDecodeTools
{
    /// <summary>The payload type the phone's audio stream uses.</summary>
    private const int AudioPayloadType = 101;

    /// <summary>
    /// Decodes a capture into a WAV and reports every measurement.
    /// </summary>
    /// <returns>Zero when every frame decoded and every check passed.</returns>
    public static int DecodeAudio(string capturePath, string wavPath, int frameLength, Action<string> say)
    {
        if (!File.Exists(capturePath))
        {
            say($"Capture introuvable : {Path.GetFullPath(capturePath)}");
            return 2;
        }

        var config = AacConfig.Parse(AacProbe.PhoneConfig).WithFrameLength(frameLength);
        say($"Capture       : {Path.GetFullPath(capturePath)}");
        say($"Configuration : {config}");

        var decoder = new AacEldDecoder(config);
        int channels = decoder.ChannelCount;
        var pcm = new float[frameLength * channels];

        // The sample list is sized up front from the file: growing a list of four
        // million floats mid-run triggers a collection, and the collection lands
        // inside one of the Decode calls being timed. The smallest record the
        // capture format can hold is 12 bytes of header, 12 of RTP and one of
        // payload, so the file cannot hold more packets than its length over 25.
        int atMostPackets = (int)(new FileInfo(capturePath).Length / 25) + 16;
        var samples = new List<float>(atMostPackets * frameLength * channels);
        var timings = new List<double>(atMostPackets);
        int packets = 0, early = 0, late = 0, dirtyPadding = 0;
        long bitsRead = 0, bitsOffered = 0;
        string firstFailure = "";

        var clock = new Stopwatch();
        foreach ((byte[] datagram, long _) in RtpCapture.Read(capturePath))
        {
            if (RtpPacket.LooksLikeRtcp(datagram))
                continue;
            var packet = RtpPacket.Parse(datagram);
            if (!packet.Valid || packet.PayloadType != AudioPayloadType || packet.Payload.Length == 0)
                continue;
            packets++;

            clock.Restart();
            bool ok = decoder.Decode(packet.Payload, pcm, out int perChannel);
            clock.Stop();
            timings.Add(clock.Elapsed.TotalMilliseconds);

            if (!ok && firstFailure.Length == 0)
                firstFailure = $"paquet {packets} (seq {packet.Sequence}) : {decoder.LastFailure?.Message}";
            if (ok)
            {
                int slack = decoder.BitsAvailable - decoder.BitsConsumed;
                bitsRead += decoder.BitsConsumed;
                bitsOffered += decoder.BitsAvailable;
                if (slack >= 8)
                    late++;
                else if (slack < 0)
                    early++;
                if (!decoder.PaddingIsZero)
                    dirtyPadding++;
            }
            for (int i = 0; i < perChannel * channels; i++)
                samples.Add(pcm[i]);
        }

        if (packets == 0)
        {
            say($"Aucun paquet de type {AudioPayloadType} dans la capture.");
            return 3;
        }

        say($"Paquets audio : {packets}, trames decodees {decoder.FramesDecoded},"
            + $" en echec {decoder.FramesFailed}");
        if (firstFailure.Length > 0)
            say($"  premier echec : {firstFailure}");
        say($"Bits           : {bitsRead} lus sur {bitsOffered} offerts"
            + $" ({(bitsOffered == 0 ? 0 : 100.0 * bitsRead / bitsOffered):F1} %),"
            + $" {(double)bitsRead / Math.Max(1, decoder.FramesDecoded):F1} bits par trame");
        say($"  trames finissant trop tot {early}, trop tard {late},"
            + $" sur un bourrage non nul {dirtyPadding}");

        var measured = Measure(samples, channels, frameLength);
        say($"Echantillons  : {samples.Count / channels} par canal,"
            + $" pic {measured.Peak:F6} ({Decibels(measured.Peak)}), RMS global {measured.Rms:F6}"
            + $" ({Decibels(measured.Rms)})");
        say($"  non finis (NaN ou infini) : {measured.NotFinite}");
        say(measured.ContinuityDefined
            ? $"  continuite aux jointures : {measured.BoundaryStep:E3} contre {measured.InteriorStep:E3}"
              + $" ailleurs, rapport {measured.ContinuityRatio:F3}"
            : "  continuite aux jointures : signal nul, rapport non defini");
        say($"  RMS par seconde : {string.Join(" ", measured.RmsPerSecond.Select(value => value.ToString("F5")))}");

        // The first frame pays for the just-in-time compiler and for building the
        // filterbank's cosine table, so it is reported apart: quoting it inside
        // the average would say more about .NET's startup than about the decoder.
        var steady = timings.Skip(1).Order().ToArray();
        double mean = steady.Length == 0 ? 0 : steady.Sum() / steady.Length;
        say($"Temps de decodage : {mean:F3} ms par trame en moyenne,"
            + $" mediane {(steady.Length == 0 ? 0 : steady[steady.Length / 2]):F3} ms,"
            + $" {(steady.Length == 0 ? 0 : steady[^1]):F3} ms au pire"
            + $" — premiere trame {timings[0]:F3} ms (JIT et tables)"
            + $" ; budget 10 ms pour une trame de {frameLength}");

        WriteWav(wavPath, samples, channels, config.SamplingFrequency);
        say($"WAV ecrit     : {Path.GetFullPath(wavPath)}"
            + $" — {config.SamplingFrequency} Hz, {channels} canaux, 16 bits");

        bool clean = decoder.FramesFailed == 0 && early == 0 && late == 0
            && dirtyPadding == 0 && measured.NotFinite == 0;
        say(clean
            ? "*** Toutes les trames lues jusqu'au bourrage, aucun echantillon non fini ***"
            : "*** DEFAUTS : voir les compteurs ci-dessus ***");
        return clean ? 0 : 9;
    }

    /// <summary>What the decoded samples say about themselves.</summary>
    private readonly record struct Measurements(double Peak, double Rms, int NotFinite,
        bool ContinuityDefined, double BoundaryStep, double InteriorStep, double ContinuityRatio,
        double[] RmsPerSecond);

    /// <summary>
    /// Peak, RMS, NaN count and the continuity index.
    /// </summary>
    /// <remarks>
    /// The first difference is taken within a channel, not across the interleave:
    /// the step between a left and a right sample means nothing. A boundary is a
    /// sample whose index within its channel is a multiple of the frame length,
    /// compared with the sample before it — the one join the overlap-add is
    /// responsible for.
    /// </remarks>
    private static Measurements Measure(List<float> interleaved, int channels, int frameLength)
    {
        int perChannel = interleaved.Count / channels;
        double peak = 0, squares = 0, boundary = 0, interior = 0;
        int boundaries = 0, interiors = 0, notFinite = 0;
        var secondSquares = new List<double>();
        var secondCounts = new List<int>();

        for (int index = 0; index < interleaved.Count; index++)
        {
            float sample = interleaved[index];
            if (!float.IsFinite(sample))
            {
                notFinite++;
                continue;
            }
            peak = Math.Max(peak, Math.Abs(sample));
            squares += (double)sample * sample;

            int second = index / channels / 48000;
            while (secondSquares.Count <= second)
            {
                secondSquares.Add(0);
                secondCounts.Add(0);
            }
            secondSquares[second] += (double)sample * sample;
            secondCounts[second]++;
        }

        for (int channel = 0; channel < channels; channel++)
        {
            for (int sample = 1; sample < perChannel; sample++)
            {
                float previous = interleaved[((sample - 1) * channels) + channel];
                float current = interleaved[(sample * channels) + channel];
                if (!float.IsFinite(previous) || !float.IsFinite(current))
                    continue;
                double step = Math.Abs(current - previous);
                if (sample % frameLength == 0)
                {
                    boundary += step;
                    boundaries++;
                }
                else
                {
                    interior += step;
                    interiors++;
                }
            }
        }

        double boundaryStep = boundaries == 0 ? 0 : boundary / boundaries;
        double interiorStep = interiors == 0 ? 0 : interior / interiors;
        var rmsPerSecond = new double[secondSquares.Count];
        for (int second = 0; second < rmsPerSecond.Length; second++)
        {
            rmsPerSecond[second] = secondCounts[second] == 0
                ? 0
                : Math.Sqrt(secondSquares[second] / secondCounts[second]);
        }

        return new Measurements(peak,
            interleaved.Count == 0 ? 0 : Math.Sqrt(squares / interleaved.Count),
            notFinite,
            interiorStep > 0,
            boundaryStep, interiorStep,
            interiorStep > 0 ? boundaryStep / interiorStep : 0,
            rmsPerSecond);
    }

    /// <summary>A level as decibels below full scale, or the floor when it is zero.</summary>
    private static string Decibels(double level) =>
        level <= 0 ? "silence" : $"{20 * Math.Log10(level):F1} dBFS";

    /// <summary>
    /// Writes a 16-bit PCM WAV, header included, by hand.
    /// </summary>
    /// <remarks>
    /// Forty-four bytes of RIFF: the container is simple enough that pulling in
    /// anything to write it would be the more surprising choice. The samples are
    /// the decoder's floats times full scale, rounded and clamped — a stream that
    /// exceeds ±1 is a stream whose peak is reported above, and clipping it here
    /// keeps the WAV listenable instead of wrapping it into noise.
    /// </remarks>
    private static void WriteWav(string path, List<float> interleaved, int channels, int sampleRate)
    {
        string? folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (folder is { Length: > 0 })
            Directory.CreateDirectory(folder);

        int dataBytes = interleaved.Count * 2;
        var header = new byte[44];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)(36 + dataBytes));
        "WAVEfmt "u8.CopyTo(header.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), 16);          // fmt chunk size
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(20), 1);           // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(22), (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), (uint)(sampleRate * channels * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(32), (ushort)(channels * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(34), 16);          // bits per sample
        "data"u8.CopyTo(header.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40), (uint)dataBytes);

        using var file = File.Create(path);
        file.Write(header);
        var buffer = new byte[1 << 16];
        int at = 0;
        foreach (float sample in interleaved)
        {
            double scaled = float.IsFinite(sample) ? sample * 32767.0 : 0;
            short value = (short)Math.Clamp(Math.Round(scaled), short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(at), value);
            at += 2;
            if (at == buffer.Length)
            {
                file.Write(buffer, 0, at);
                at = 0;
            }
        }
        if (at > 0)
            file.Write(buffer, 0, at);
    }
}
