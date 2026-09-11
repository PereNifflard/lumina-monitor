using System.Buffers.Binary;
using System.Diagnostics;
using LuminaMonitor.Core.Audio;
using LuminaMonitor.Core.Media;

/// <summary>
/// A recorded audio stream played back through the application's own chain, and
/// the list of outputs it can be played on.
/// </summary>
/// <remarks>
/// <para>The point is that it is the <em>same</em> chain: the datagrams go into
/// <see cref="AudioRenderer"/> exactly as the tunnel's receive path hands them
/// over — RTP and RTCP mixed, sequence numbers and all — and come out of the same
/// jitter buffer into the same sink. A second implementation for testing would
/// agree with the first one right up to the point where it mattered.</para>
///
/// <para><c>--dry</c> opens no endpoint at all: the frames are pulled on a
/// stopwatch at 48 kHz and dropped. That is what the continuous integration runs,
/// and what any measurement on a machine somebody is working at runs — nothing
/// here makes a noise unless it is asked to.</para>
///
/// <para>What is measured is what a listener cannot report: whether every frame
/// decoded, whether the queue held the delay it was asked for, and whether the
/// output ever ran dry. A single underrun is a click; a queue that drifts is a
/// click every few minutes; neither has a name a person could give it.</para>
/// </remarks>
internal static class AudioPlayTools
{
    /// <summary>The silent frame the phone sends when nothing is playing.</summary>
    private static readonly byte[] SilentFrame = [0x00, 0x68, 0x34, 0x00];

    /// <summary>Frames of the synthetic capture, when no recording is at hand.</summary>
    private const int SyntheticFrames = 200;

    public const string Usage =
        "usage : audio-play <capture.rtp|--silence[=trames]> [--device=<id|default>] [--delay=<ms>] [--dry]";

    /// <summary>
    /// audio-play — replays a capture through the whole chain, at its own pace.
    /// </summary>
    /// <returns>Zero when every frame decoded and nothing ran dry.</returns>
    public static int Play(string source, string? deviceId, int delayMs, bool dry, Action<string> say)
    {
        var records = source.StartsWith("--silence", StringComparison.OrdinalIgnoreCase)
            ? Synthetic(Frames(source), say)
            : Recorded(source, say);
        if (records is null)
            return 2;
        if (records.Count == 0)
        {
            say($"Capture vide : {source}");
            return 3;
        }

        var options = new AudioOptions { DeviceId = deviceId, DelayMs = delayMs };
        say($"Chaine audio : retard cible {options.ClampedDelayMs} ms,"
            + $" sortie {(dry ? "a sec (aucun peripherique ouvert)" : deviceId ?? "par defaut de Windows")}.");
        using var renderer = new AudioRenderer(options, line => say("  " + line), dry);
        var dryFill = renderer.Sink as DryAudioSink;
        say($"  peripherique : {renderer.Sink.Device}");
        say($"  format       : {renderer.Sink.Format}");
        if (renderer.Sink.Failure is { } failure)
        {
            say($"*** SORTIE AUDIO INDISPONIBLE *** {failure}");
            return 8;
        }

        double span = (records[^1].Microseconds - records[0].Microseconds) / 1_000_000.0;
        say($"Rejeu de {records.Count} datagramme(s), {span:F1} s a la cadence de la capture…");

        // The fine timer, for the same reason the video replay holds it: a
        // hundred packets a second cannot be paced on a fifteen-millisecond tick.
        using var fineTimer = LuminaMonitor.Core.Hid.TimerResolution.Hold();
        var cpu = Process.GetCurrentProcess();
        TimeSpan cpuBefore = cpu.TotalProcessorTime;
        var clock = Stopwatch.StartNew();
        long origin = Stopwatch.GetTimestamp();
        long first = records[0].Microseconds;
        long allocatedAt = 0;
        int fed = 0;
        foreach (var (datagram, microseconds) in records)
        {
            WaitUntil(origin + ((microseconds - first) * Stopwatch.Frequency / 1_000_000));
            // The first tenth of the run pays for the just-in-time compiler and
            // for the filterbank's tables; the allocation measurement starts
            // after it, where "no allocation per frame" is the claim being tested.
            if (++fed == records.Count / 10)
                allocatedAt = GC.GetAllocatedBytesForCurrentThread();
            renderer.Accept(datagram);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedAt;
        int steady = Math.Max(1, records.Count - (records.Count / 10));

        // The counters are read here and not a moment later, and the sink is shut
        // down before anything is printed. What is still in the queue is the target
        // delay, by construction; letting the sink drain it would hand back one
        // underrun for the end of the file, which is not a defect and would hide
        // the ones that are.
        var stats = renderer.Stats;
        var dryMeasured = dryFill?.Fill;
        clock.Stop();
        TimeSpan cpuAfter = Process.GetCurrentProcess().TotalProcessorTime;
        renderer.Dispose();
        say($"Paquets       : {stats.Packets} audio, {records.Count - stats.Packets - renderer.Ignored} RTCP,"
            + $" {renderer.Ignored} d'un autre type");
        say($"Trames        : {stats.FramesDecoded} decodees, {stats.FramesFailed} en echec,"
            + $" {stats.FramesLost} silences pour pertes, {stats.PacketsOutOfOrder} paquet(s) en desordre");
        if (renderer.LastDecodeFailure is { } why)
            say($"  dernier echec de decodage : {why}");
        say($"File de gigue : sous-alimentations {stats.Underruns}, sauts {stats.FramesSkipped},"
            + $" cible {stats.TargetMs:F0} ms");
        if (dryMeasured is { } measured)
        {
            say($"  remplissage : moyenne {measured.MeanMs:F1} ms, min {measured.MinMs:F1} ms,"
                + $" max {measured.MaxMs:F1} ms sur {measured.Pulls} lecture(s) de {renderer.Sink.LatencyMs:F1} ms"
                + " (amorcage exclu)");
        }
        say($"Processeur    : {(cpuAfter - cpuBefore).TotalMilliseconds:F0} ms de calcul"
            + $" pour {clock.Elapsed.TotalSeconds:F1} s de son"
            + $" ({(cpuAfter - cpuBefore).TotalMilliseconds / Math.Max(0.001, clock.Elapsed.TotalMilliseconds) * 100:F1} %)");
        say($"Allocations   : {allocated} octet(s) sur {steady} trames en regime etabli"
            + $" ({(double)allocated / steady:F1} par trame)");

        bool clean = stats.FramesFailed == 0 && stats.Underruns == 0 && allocated == 0;
        say(clean
            ? "*** AUDIO-PLAY VERT *** toutes les trames decodees, la file n'a jamais manque, aucune allocation."
            : "*** DEFAUTS : voir les compteurs ci-dessus ***");
        return clean ? 0 : 9;
    }

    /// <summary>audio-devices — the outputs the Audio panel can offer, and their mix formats.</summary>
    public static int Devices(Action<string> say)
    {
        var endpoints = AudioEndpoints.List(includeInactive: true);
        if (endpoints.Count == 0)
        {
            say("Aucune sortie audio sur cette machine.");
            return 3;
        }
        say($"{endpoints.Count} sortie(s) audio. « defaut » est le role Console — celui d'un lecteur de musique,"
            + " jamais celui des communications.");
        foreach (var endpoint in endpoints)
        {
            say($"  {(endpoint.IsDefault ? "* " : "  ")}{endpoint.Name}"
                + $"{(endpoint.IsActive ? "" : $"   [{endpoint.State}]")}");
            say($"      id     : {endpoint.Id}");
            if (endpoint.DeviceName.Length > 0)
                say($"      appareil : {endpoint.DeviceName}");
            if (endpoint.IsActive)
                say($"      melange : {AudioEndpoints.MixFormat(endpoint.Id) ?? "illisible"}");
        }
        return 0;
    }

    /// <summary>How many frames a <c>--silence[=n]</c> argument asks for.</summary>
    private static int Frames(string argument)
    {
        int equals = argument.IndexOf('=');
        return equals > 0 && int.TryParse(argument.AsSpan(equals + 1), out int frames) && frames is > 0 and <= 100_000
            ? frames
            : SyntheticFrames;
    }

    private static List<(byte[] Datagram, long Microseconds)>? Recorded(string path, Action<string> say)
    {
        if (!File.Exists(path))
        {
            say($"Capture introuvable : {Path.GetFullPath(path)}");
            say(Usage);
            return null;
        }
        say($"Capture : {Path.GetFullPath(path)}");
        return [.. RtpCapture.Read(path)];
    }

    /// <summary>
    /// A capture built here rather than recorded: the phone's own silent frame,
    /// repeated, with the sequence numbers and timestamps it would have had.
    /// </summary>
    /// <remarks>
    /// What the continuous integration plays. The real recordings are ignored by
    /// git — a minute of somebody's phone audio is not a thing to commit — so a
    /// runner has none, and a chain that is never exercised on a runner is a chain
    /// that breaks between two commits. Four bytes of payload, 480 timestamp units
    /// and ten milliseconds per frame: the numbers measured on the wire, so the
    /// jitter buffer sees exactly the cadence it was built for.
    /// </remarks>
    private static List<(byte[] Datagram, long Microseconds)> Synthetic(int frames, Action<string> say)
    {
        say($"Capture synthetique : {frames} trames silencieuses du telephone (00 68 34 00),"
            + " 480 unites d'horodatage et 10 ms par trame.");
        var records = new List<(byte[], long)>(frames);
        uint timestamp = 1000;
        for (int frame = 0; frame < frames; frame++)
        {
            byte[] datagram = new byte[12 + SilentFrame.Length];
            datagram[0] = 0x80;                                     // version 2, no padding, no extension
            datagram[1] = (byte)AudioRenderer.PayloadType;
            BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(2), (ushort)(frame & 0xFFFF));
            BinaryPrimitives.WriteUInt32BigEndian(datagram.AsSpan(4), timestamp);
            BinaryPrimitives.WriteUInt32BigEndian(datagram.AsSpan(8), 0x2274AB0D);
            SilentFrame.CopyTo(datagram, 12);
            records.Add((datagram, frame * 10_000L));
            timestamp += 480;
        }
        return records;
    }

    /// <summary>
    /// Waits for a due time, asleep the whole way.
    /// </summary>
    /// <remarks>
    /// The fine timer makes <c>Thread.Sleep(1)</c> mean about a millisecond, and
    /// the due times are absolute, so a packet fed a millisecond late costs that
    /// packet a millisecond and nothing after it. That is less jitter than the
    /// tunnel itself puts in, and it leaves the processor free — which matters,
    /// because one of the things being reported is how much processor the chain
    /// costs.
    /// </remarks>
    private static void WaitUntil(long dueTicks)
    {
        while (true)
        {
            long left = dueTicks - Stopwatch.GetTimestamp();
            if (left <= 0)
                return;
            double milliseconds = left * 1000.0 / Stopwatch.Frequency;
            Thread.Sleep(milliseconds > 1.5 ? (int)(milliseconds - 0.5) : 1);
        }
    }
}
