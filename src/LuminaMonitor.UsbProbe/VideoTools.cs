using System.Buffers.Binary;
using System.Diagnostics;
using LuminaMonitor.Core;
using LuminaMonitor.Core.Ddi;
using LuminaMonitor.Core.Hid;
using LuminaMonitor.Core.Media;

/// <summary>
/// The video half of the probe: replay a capture, mirror the live phone,
/// write a picture out.
/// </summary>
/// <remarks>
/// Both tools decode on a thread of their own, COM-initialised as MTA — the
/// Media Foundation decoder is a free-threaded object and the console's main
/// thread has no business pumping messages for it. The bitmap writer is
/// twenty lines rather than a dependency: a 24-bit BMP is a header, a second
/// header and the rows bottom-up, and System.Drawing is not on this project's
/// menu.
/// </remarks>
internal static class VideoTools
{
    /// <summary>Runs the work on a fresh MTA thread and returns its exit code.</summary>
    public static int OnMta(Func<int> work)
    {
        int result = 9;
        var thread = new Thread(() => result = work(), 8 * 1024 * 1024);
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        thread.Join();
        return result;
    }

    // --- The parameter set rewriter ----------------------------------------------

    /// <summary>The sequence parameter set this phone really sends, read off the wire.</summary>
    private static readonly byte[] PhoneSps = Convert.FromHexString("276400334B04C5140530 16BA6E04040404".Replace(" ", ""));

    /// <summary>
    /// The same set with its <c>vui_parameters_present_flag</c> cleared and the
    /// RBSP closed right after: the "no VUI at all" branch, on real geometry.
    /// </summary>
    private static readonly byte[] PhoneSpsWithoutVui = Convert.FromHexString("276400334B04C5140530 16B9".Replace(" ", ""));

    /// <summary>
    /// sps-selftest — rewrites parameter sets and reads them back.
    /// </summary>
    /// <remarks>
    /// The rewrite is the one part of the video path that edits the bitstream
    /// itself, and a wrong bit in a parameter set is not a wrong picture but no
    /// picture at all. So it is checked three ways on every case: everything
    /// that describes the pictures comes back identical, the restriction is
    /// there afterwards with a reorder depth of zero, and a second pass changes
    /// nothing — which is what proves the writer and the reader agree.
    /// </remarks>
    public static int SpsSelfTest(Action<string> say)
    {
        int failures = 0;
        failures += CheckRewrite("SPS du telephone (VUI sans restriction)", PhoneSps, say);
        failures += CheckRewrite("SPS du telephone sans VUI", PhoneSpsWithoutVui, say);
        // The rewritten set is itself a case: a VUI that already carries a
        // restriction, which is the branch the first two never reach.
        failures += CheckRewrite("SPS deja restreint", SpsRewriter.Rewrite(PhoneSps, out _), say);

        try
        {
            SpsRewriter.Rewrite(Convert.FromHexString("2841E3CB"), out _);   // a PPS, not an SPS
            say("  REFUS ATTENDU : un PPS a ete accepte comme SPS.");
            failures++;
        }
        catch (InvalidDataException)
        {
            say("  un PPS presente comme SPS est refuse : correct.");
        }

        say(failures == 0 ? "*** SPS-SELFTEST VERT ***" : $"*** SPS-SELFTEST : {failures} ECHEC(S) ***");
        return failures == 0 ? 0 : 7;
    }

    private static int CheckRewrite(string label, byte[] sps, Action<string> say)
    {
        SpsInfo before = SpsRewriter.Parse(sps);
        byte[] after = SpsRewriter.Rewrite(sps, out _);
        SpsInfo now = SpsRewriter.Parse(after);
        byte[] again = SpsRewriter.Rewrite(after, out _);

        var faults = new List<string>();
        if (now.ProfileIdc != before.ProfileIdc || now.LevelIdc != before.LevelIdc
            || now.ChromaFormatIdc != before.ChromaFormatIdc || now.MaxNumRefFrames != before.MaxNumRefFrames
            || now.WidthMbs != before.WidthMbs || now.HeightMapUnits != before.HeightMapUnits
            || now.FrameMbsOnly != before.FrameMbsOnly || now.CropLeft != before.CropLeft
            || now.CropRight != before.CropRight || now.CropTop != before.CropTop
            || now.CropBottom != before.CropBottom)
            faults.Add("un champ d'origine a change");
        if (!now.HasVui) faults.Add("pas de VUI en sortie");
        if (!now.HasRestriction) faults.Add("pas de restriction en sortie");
        if (now.MaxNumReorderFrames != 0) faults.Add($"max_num_reorder_frames = {now.MaxNumReorderFrames}");
        if (now.MaxDecFrameBuffering != before.MaxNumRefFrames)
            faults.Add($"max_dec_frame_buffering = {now.MaxDecFrameBuffering}, attendu {before.MaxNumRefFrames}");
        if (!again.AsSpan().SequenceEqual(after)) faults.Add("deuxieme passe non identique");

        say($"  {label} : {sps.Length} -> {after.Length} octets");
        say($"    avant : {before}");
        say($"    apres : {now}");
        say($"    hexa  : {Convert.ToHexString(after)}");
        if (faults.Count == 0)
            return 0;
        foreach (string fault in faults)
            say($"    *** {fault} ***");
        return 1;
    }

    // --- Offline replay ----------------------------------------------------------

    public static int DecodeCapture(string capturePath, int wanted, string bmpPath, Action<string> say)
    {
        if (!File.Exists(capturePath)) { say($"Capture introuvable : {capturePath}"); return 3; }
        return OnMta(() =>
        {
            var depacketizer = new H264Depacketizer();
            var clock = new Stopwatch();
            long decodeTicks = 0;
            int decoded = 0, videoPackets = 0, rtcpPackets = 0, records = 0;
            string firstPayload = "";
            var shape = new List<string>();
            VideoFrame? kept = null;
            H264Decoder? decoder = null;
            long heldAtEnd = 0;
            bool lowLatency = false;
            int[] slices = new int[4];

            try
            {
                decoder = new H264Decoder();
                var local = decoder;
                depacketizer.Completed += unit =>
                {
                    if (shape.Count < 3)
                        shape.Add(Describe(unit));
                    CountSlices(unit, slices);
                    clock.Restart();
                    local.Decode(unit, frame =>
                    {
                        decoded++;
                        if (decoded == wanted)
                            kept = frame.Copy();
                    });
                    decodeTicks += clock.ElapsedTicks;
                };

                foreach (var (datagram, microseconds) in ReadCapture(capturePath))
                {
                    records++;
                    if (RtpPacket.LooksLikeRtcp(datagram)) { rtcpPackets++; continue; }
                    var packet = RtpPacket.Parse(datagram);
                    if (!packet.Valid) continue;
                    if (videoPackets == 0)
                        firstPayload = Convert.ToHexString(packet.Payload[..Math.Min(16, packet.Payload.Length)]);
                    videoPackets++;
                    depacketizer.Add(packet, microseconds * Stopwatch.Frequency / 1_000_000);
                }
                // Read before the drain: afterwards the decoder holds nothing by
                // construction, and it is exactly what it was holding while the
                // stream ran that the mirror sees as delay.
                heldAtEnd = decoder.InFlight;
                lowLatency = decoder.LowLatencySet;
                clock.Restart();
                decoder.Finish(frame =>
                {
                    decoded++;
                    if (decoded == wanted) kept = frame.Copy();
                });
                decodeTicks += clock.ElapsedTicks;
            }
            catch (Exception exception)
            {
                say($"Echec du decodage : {exception.Message}");
                return 8;
            }
            finally
            {
                decoder?.Dispose();
            }

            double seconds = (double)decodeTicks / Stopwatch.Frequency;
            say($"Capture {capturePath} : {records} enregistrement(s), {videoPackets} paquet(s) video, {rtcpPackets} RTCP.");
            say($"  premiere charge utile video : {firstPayload}");
            foreach (string line in shape)
                say($"  {line}");
            say($"  descriptions avc1 lues : {depacketizer.Descriptions}, jeux de parametres :"
                + $" {(depacketizer.HaveParameterSets ? "SPS et PPS presents" : "ABSENTS")}");
            say($"  unites d'acces : {depacketizer.AccessUnits} dont {depacketizer.KeyFrames} image(s) cle,"
                + $" {depacketizer.Dropped} abandonnee(s), {depacketizer.Lost} paquet(s) perdu(s),"
                + $" {depacketizer.Trailers} suffixe(s) proprietaire(s) retire(s).");
            say($"  images decodees : {decoded} en {seconds:F2} s de decodage -> {(seconds > 0 ? decoded / seconds : 0):F1} images/s");
            say($"  decodeur : {heldAtEnd} image(s) retenue(s) avant la vidange, faible latence"
                + $" {(lowLatency ? "OUI" : "NON")}.");
            say($"  types de tranche : I {slices[0]}, P {slices[1]}, B {slices[2]}, SI/SP {slices[3]}"
                + $" — reordonnancement {(slices[2] == 0 ? "impossible (aucune image B)" : "POSSIBLE")}.");
            if (kept is null)
            {
                say($"  image {wanted} absente : la capture n'en contient que {decoded}.");
                return decoded > 0 ? 0 : 4;
            }
            WriteBmp(bmpPath, kept);
            say($"  image {wanted} : {kept.Width}x{kept.Height}, stride {kept.Stride} -> {Path.GetFullPath(bmpPath)}");
            return 0;
        });
    }

    /// <summary>
    /// Tallies the slice types of one access unit into I, P, B, SI/SP.
    /// </summary>
    /// <remarks>
    /// The claim that this stream never reorders pictures rests on there being
    /// no B slices in it, and a claim worth putting in a sequence header is
    /// worth counting first.
    /// </remarks>
    private static void CountSlices(AccessUnit unit, int[] tally)
    {
        var data = unit.Data;
        for (int at = 0; at + 5 < data.Length; at++)
        {
            if (data[at] != 0 || data[at + 1] != 0 || data[at + 2] != 0 || data[at + 3] != 1)
                continue;
            int length = Math.Min(16, data.Length - at - 4);
            int slot = SpsRewriter.SliceType(data.AsSpan(at + 4, length)) switch
            {
                2 => 0,          // I
                0 => 1,          // P
                1 => 2,          // B
                3 or 4 => 3,     // SP, SI
                _ => -1,
            };
            if (slot >= 0) tally[slot]++;
            at += 3;
        }
    }

    /// <summary>The NAL types of one access unit, which is how a key frame proves itself.</summary>
    private static string Describe(AccessUnit unit)
    {
        var types = new List<string>();
        var data = unit.Data;
        for (int at = 0; at + 4 < data.Length; at++)
        {
            if (data[at] != 0 || data[at + 1] != 0 || data[at + 2] != 0 || data[at + 3] != 1)
                continue;
            int type = data[at + 4] & 0x1F;
            types.Add(type switch
            {
                7 => "SPS(7)", 8 => "PPS(8)", 5 => "IDR(5)", 1 => "P(1)", 6 => "SEI(6)", 9 => "AUD(9)",
                _ => $"NAL({type})",
            });
            at += 3;
        }
        return $"unite d'acces ts={unit.Timestamp} {unit.Data.Length} octets, {unit.Packets} paquet(s) :"
            + $" {string.Join(' ', types)}";
    }

    /// <summary>The probe's capture format: [u32 taille][u64 microsecondes][datagramme].</summary>
    private static IEnumerable<(byte[] Datagram, long Microseconds)> ReadCapture(string path)
    {
        using var file = File.OpenRead(path);
        byte[] header = new byte[12];
        while (true)
        {
            if (!Fill(file, header, 12)) yield break;
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(header);
            long microseconds = (long)BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(4));
            if (size is <= 0 or > 65535) yield break;
            byte[] datagram = new byte[size];
            if (!Fill(file, datagram, size)) yield break;
            yield return (datagram, microseconds);
        }
    }

    private static bool Fill(Stream stream, byte[] buffer, int count)
    {
        int at = 0;
        while (at < count)
        {
            int read = stream.Read(buffer, at, count - at);
            if (read <= 0) return false;
            at += read;
        }
        return true;
    }

    // --- Live mirror -------------------------------------------------------------

    /// <summary>
    /// A still a second, named by the wall clock of the last packet that built it.
    /// </summary>
    /// <remarks>
    /// The one measurement no counter inside this process can make. Everything
    /// else here compares our clock with the phone's timestamps, which says how
    /// the delay <i>changes</i> and never what it is; a picture of a running
    /// stopwatch, stamped with the PC time at which its last packet was read,
    /// carries both readings in one file. Two of them a second apart settle the
    /// buffering question on their own — a stopwatch that advanced by one
    /// second between two files one second apart has nothing variable in front
    /// of it — and the absolute offset is read against the phone itself, by
    /// someone holding it.
    ///
    /// <para>The pictures are copied during the run and written after it. A
    /// 1328x2896 BMP is eleven megabytes and takes tens of milliseconds to put
    /// on a disk; doing that on the decode thread once a second would be an
    /// instrument that changes what it measures.</para>
    /// </remarks>
    private sealed record Still(VideoFrame Frame, DateTime Read);

    public static async Task MirrorTestAsync(int seconds, string bmpPath, string ddiFolder, Action<string> say,
        string? stillFolder = null, string variant = "default")
    {
        // The same offer variants as motion-test, so the stills can tell one
        // negotiation from another against a clock on the phone's screen.
        if (!ParseVariant(variant, out var tuning, out string label))
        {
            say($"usage : mirror-test [secondes] [sortie.bmp] [--suite=<dossier>] [--variant=<variante>]{Environment.NewLine}{VariantUsage}");
            return;
        }
        say($"Variante « {label} » : {tuning}");
        await using var session = new DeviceSession(new DdiSource(ddiFolder), new ConsoleLog())
        {
            VideoCodecs = VideoCodecs.AvcOnly,
            DecodeVideo = true,
            VideoTuning = tuning,
        };
        session.UnlockRequired += message => say(message);

        object gate = new();
        long frames = 0;
        double latencySum = 0;
        long latencyCount = 0;
        bool grab = true;
        VideoFrame? last = null;

        // The anchor that turns a Stopwatch tick into a time of day: taken once,
        // so every still is stamped from the same pair and the differences
        // between them are the stopwatch's own, not two clocks drifting apart.
        var stills = new List<Still>();
        long anchorTicks = Stopwatch.GetTimestamp();
        DateTime anchorTime = DateTime.Now;
        long stillEveryTicks = Stopwatch.Frequency;
        long nextStillTicks = long.MaxValue;
        if (stillFolder is not null)
        {
            Directory.CreateDirectory(stillFolder);
            nextStillTicks = anchorTicks;
        }

        session.FrameDecoded += frame =>
        {
            Interlocked.Increment(ref frames);
            lock (gate)
            {
                latencySum += frame.LatencyMs;
                latencyCount++;
                if (grab) { last = frame.Copy(); grab = false; }
                if (frame.ArrivalTicks >= nextStillTicks)
                {
                    nextStillTicks = frame.ArrivalTicks + stillEveryTicks;
                    stills.Add(new Still(frame.Copy(),
                        anchorTime.AddTicks((frame.ArrivalTicks - anchorTicks) * TimeSpan.TicksPerSecond / Stopwatch.Frequency)));
                }
            }
        };

        await session.ConnectAsync();
        var media = session.Media;
        if (media is null || !media.Streaming)
        {
            say($"*** FLUX REFUSE *** {media?.Failure ?? "(aucun motif rapporte)"}");
            return;
        }

        var clock = Stopwatch.StartNew();
        var previous = media.Stats;
        long previousFrames = 0;
        double previousElapsed = 0;
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(5, seconds - clock.Elapsed.TotalSeconds + 0.01)));
            var now = media.Stats;
            long nowFrames = Interlocked.Read(ref frames);
            double elapsed = clock.Elapsed.TotalSeconds;
            double window = Math.Max(0.001, elapsed - previousElapsed);
            double latency;
            lock (gate)
            {
                latency = latencyCount > 0 ? latencySum / latencyCount : 0;
                latencySum = 0; latencyCount = 0;
                grab = true;
            }
            var pump = session.TunnelStatistics;
            say($"t={elapsed,5:F1} s : {(now.Packets - previous.Packets) / window,7:F0} paquets/s,"
                + $" {(nowFrames - previousFrames) / window,5:F1} images/s,"
                + $" pertes {now.PacketsLost}, unites abandonnees {now.UnitsDropped + now.LateDrops},"
                + $" latence {latency,5:F1} ms, RR {now.ReportsSent}, SR {now.SenderReports},"
                + $" images cle {now.KeyFrames} (PLI {now.KeyFrameRequests}),"
                + $" derive moy {now.DriftMeanMs,6:F0} max {now.DriftMaxMs,6:F0} ms"
                + (pump is { } tunnel
                    ? $", socket-dispo {tunnel.SocketAvailable} o, tour {tunnel.TurnMeanMs:F2}/{tunnel.TurnMaxMs:F1} ms,"
                      + $" handlers {tunnel.HandlerMeanMs:F2}/{tunnel.HandlerMaxMs:F1} ms"
                    : ""));
            session.ResetMediaPeaks();
            previous = now;
            previousFrames = nowFrames;
            previousElapsed = elapsed;
        }

        var final = media.Stats;
        say($"*** MIROIR {clock.Elapsed.TotalSeconds:F1} s *** {final.Packets} paquets ({final.RtcpPackets} RTCP),"
            + $" {final.AccessUnits} unites d'acces, {final.FramesDecoded} images decodees"
            + $" -> {final.FramesDecoded / clock.Elapsed.TotalSeconds:F1} images/s de moyenne.");
        say($"  suffixes proprietaires retires : {final.Trailers} ; rapports de reception envoyes : {final.ReportsSent} ;"
            + $" rapports d'emission recus : {final.SenderReports}.");
        // The mean and the max belong to the window still open when the loop
        // ended; the last one is the session's own figure and the one to read.
        say($"  derive RTP en fin de course : {final.DriftLastMs:F0} ms"
            + $" ; horloge RTP mesuree {final.RtpClockKhz:F2} kHz.");
        // The decoder's own backlog: units fed less pictures returned. Everything
        // above measures the wire; this is the only figure that names how much of
        // the delay the decoder itself is holding.
        say($"  decodeur : {final.DecoderInFlight} image(s) retenue(s), faible latence"
            + $" {(final.DecoderLowLatency ? "OUI" : "NON")}.");

        Still[] shots;
        lock (gate) shots = [.. stills];
        if (stillFolder is not null)
        {
            say($"  {shots.Length} image(s) a une seconde d'intervalle dans {Path.GetFullPath(stillFolder)} :");
            for (int index = 0; index < shots.Length; index++)
            {
                var (frame, read) = (shots[index].Frame, shots[index].Read);
                string name = $"stopwatch_{index:D2}_{read:HH-mm-ss.fff}.bmp";
                WriteBmp(Path.Combine(stillFolder, name), frame);
                say($"    {name}  (dernier paquet lu a {read:HH:mm:ss.fff}, ts RTP {frame.Timestamp})");
            }
        }

        VideoFrame? picture;
        lock (gate) picture = last;
        if (picture is null) { say("  aucune image decodee : pas de BMP."); return; }
        WriteBmp(bmpPath, picture);
        say($"  derniere image : {picture.Width}x{picture.Height} -> {Path.GetFullPath(bmpPath)}");
    }

    // --- Motion test -------------------------------------------------------------
    // The hypothesis this tool exists to settle: the mirror falls behind in
    // proportion to how much of the screen is moving, because the phone's
    // encoder — capped around 6 Mbit/s — queues pictures it cannot compress
    // fast enough. Transport and decode are already known to be innocent (a
    // millisecond of round trip, a few of decode), so the measurement has to be
    // of the one thing neither of them explains: the gap between when a picture
    // was taken, which its RTP timestamp says, and when it arrived.

    /// <summary>The quiet stretches on either side of the movement, in seconds.</summary>
    private const int RestSeconds = 3;

    /// <summary>How far the finger travels in one stroke, in pixels of the picture.</summary>
    private const double StrokePixels = 600;

    /// <summary>How long one stroke takes: 600 px in half a second, a brisk scroll.</summary>
    private const int StrokeMs = 500;

    /// <summary>Positions produced per second, matching the injector's own send pump.</summary>
    private const double TouchHz = 120.0;

    /// <summary>The picture height assumed until the first decoded picture says otherwise.</summary>
    private const int AssumedPictureHeight = 2896;

    /// <summary>Half of the 1328x2896 this stream carries, for the <c>half</c> variant.</summary>
    private const int HalfWidth = 664;
    private const int HalfHeight = 1448;

    /// <summary>The ceiling the <c>rctl</c> variant advertises through the feedback loop, in kbit/s.</summary>
    private const int RctlVariantKbps = 4000;

    /// <summary>The report cadence the <c>rctl:fast</c> variant runs at, three times the capture's.</summary>
    private const double FastReportHz = 60.0;

    /// <summary>The variant grammar, printed by every tool that takes one.</summary>
    public const string VariantUsage =
        "  variantes (combinables avec +) : default | half | bitrate:<kbit/s>\n"
        + "    rctl | rctl:<kbit/s> | rctl:owrd | rctl:wall | rctl:fast\n"
        + "    net:<n> | proto:<n> | feat:<n>\n"
        + "    blob:ltrp | blob:nofec | blob:rtcpfb | blob:tiles:<n> | endpoint:<modele>";

    /// <summary>How much the drift has to climb under motion before it is a queue and not noise.</summary>
    private const double EncoderQueueMs = 100;

    /// <summary>
    /// motion-test [secondes] [default|half|bitrate:N|rctl] — the mirror under
    /// a finger that never stops scrolling.
    /// </summary>
    /// <remarks>
    /// Three s of quiet, the movement, three s of quiet again, one line a
    /// second throughout. The line to read is the drift: a stream that keeps up
    /// holds it flat whatever the screen is doing, while an encoder that queues
    /// makes it climb for as long as the finger moves and gives it back the
    /// moment the screen settles. Everything else on the line is there to rule
    /// out the other explanations — losses for the wire, jitter for the tunnel,
    /// pictures received against pictures decoded for our own decoder.
    /// </remarks>
    public static async Task<int> MotionTestAsync(int seconds, string variant, string ddiFolder, Action<string> say)
    {
        if (!ParseVariant(variant, out var tuning, out string label))
        {
            say($"usage : motion-test [secondes] [variante]{Environment.NewLine}{VariantUsage}");
            return 2;
        }

        say($"Variante « {label} » : {tuning}");
        say($"Ouvre Reglages (ou toute liste qui defile) sur le telephone : le glisser la fera defiler.");

        await using var session = new DeviceSession(new DdiSource(ddiFolder), new ConsoleLog())
        {
            VideoCodecs = VideoCodecs.AvcOnly,
            DecodeVideo = true,
            VideoTuning = tuning,
        };
        session.UnlockRequired += message => say(message);

        var meter = new MotionMeter();
        session.RtpPacket += meter.Add;
        long decoded = 0;
        int pictureWidth = 0, pictureHeight = 0;
        session.FrameDecoded += frame =>
        {
            Interlocked.Increment(ref decoded);
            Volatile.Write(ref pictureWidth, frame.Width);
            Volatile.Write(ref pictureHeight, frame.Height);
        };

        await session.ConnectAsync();
        if (session.Media is not { Streaming: true } stream)
        {
            say($"*** FLUX REFUSE *** {session.Media?.Failure ?? "(aucun motif rapporte)"}");
            return 5;
        }

        var wall = Stopwatch.StartNew();
        meter.Take();                                        // the climb's own packets belong to no window

        async Task<double> PhaseAsync(string phase, int length)
        {
            double worst = 0;
            for (int second = 0; second < length; second++)
            {
                long before = Interlocked.Read(ref decoded);
                await Task.Delay(1000);
                var sample = meter.Take();
                double decodedPerSecond = (Interlocked.Read(ref decoded) - before) / sample.WindowSeconds;
                var counters = stream.Stats;
                say($"t={wall.Elapsed.TotalSeconds,5:F1} s {phase,-10} :"
                    + $" {sample.PacketsPerSecond,6:F0} paquets/s, {sample.KilobitsPerSecond,6:F0} kbit/s,"
                    + $" {sample.FramesPerSecond,5:F1} i/s recues / {decodedPerSecond,5:F1} decodees,"
                    + $" image {sample.MeanFrameKilobytes,6:F1} ko (max {sample.MaxFrameKilobytes,6:F1}),"
                    + $" derive moy {sample.DriftMeanMs,6:F0} max {sample.DriftMaxMs,6:F0} fin {sample.DriftLastMs,6:F0} ms,"
                    + $" gigue {sample.JitterMs,5:F1} ms, pertes {counters.PacketsLost},"
                    + $" retard RTP {sample.LagMs,6:F0} ms");
                worst = Math.Max(worst, sample.DriftMaxMs);
            }
            return worst;
        }

        double restBefore = await PhaseAsync("repos", RestSeconds);

        int height = Volatile.Read(ref pictureHeight);
        double amplitude = StrokePixels / (height > 0 ? height : AssumedPictureHeight);
        say($"Glisser vertical continu au centre : {StrokePixels:F0} px en {StrokeMs} ms, aller-retour sans pause,"
            + $" {TouchHz:F0} positions/s (amplitude {amplitude * 100:F1} % de l'ecran).");

        using var stopScrolling = new CancellationTokenSource();
        var scrolling = ScrollAsync(session.Input, amplitude, stopScrolling.Token);
        double motion = await PhaseAsync("mouvement", seconds);
        await stopScrolling.CancelAsync();
        await scrolling;

        double restAfter = await PhaseAsync("repos", RestSeconds);

        var final = stream.Stats;
        var (receipts, reports) = stream.RctlCounts;
        say($"*** MOUVEMENT {seconds} s ({label}) *** derive maximale : {restBefore:F0} ms au repos,"
            + $" {motion:F0} ms sous mouvement, {restAfter:F0} ms au repos ensuite.");
        double rise = motion - restBefore;
        if (rise < EncoderQueueMs)
            say($"  la derive ne monte que de {rise:F0} ms : sur cette variante le retard ne suit pas le mouvement.");
        else if (restAfter < motion / 2)
            say($"  la derive monte de {rise:F0} ms sous mouvement et retombe a {restAfter:F0} ms au repos :"
                + " file dans l'encodeur du telephone.");
        else
            say($"  la derive monte de {rise:F0} ms sous mouvement et ne retombe pas ({restAfter:F0} ms) :"
                + " il y a bien une file, mais rien ne la vide — relire les pertes et la gigue avant de conclure.");
        say($"  {final.Packets} paquets ({final.RtcpPackets} RTCP), {final.AccessUnits} unites d'acces dont"
            + $" {final.KeyFrames} image(s) cle, {final.FramesDecoded} images decodees,"
            + $" {final.UnitsDropped + final.LateDrops} unite(s) abandonnee(s), {final.PacketsLost} paquet(s) perdu(s).");
        say($"  image decodee : {Volatile.Read(ref pictureWidth)}x{Volatile.Read(ref pictureHeight)}"
            + (tuning.Rctl ? $" ; RCTL : {receipts} recus, {reports} rapports." : "."));
        return 0;
    }

    /// <summary>
    /// Reads the variant argument into the whole negotiation.
    /// </summary>
    /// <remarks>
    /// One name, one lever, and several joined by <c>+</c> applied left to
    /// right — <c>rctl:2000+net:0</c> is the two together — because the answer
    /// to "which combination is fastest" is a combination and testing it one
    /// lever at a time never reaches it. The names:
    /// <list type="bullet">
    /// <item><description><c>default</c> — Xcode's negotiation untouched, no feedback loop.</description></item>
    /// <item><description><c>half</c>, <c>bitrate:N</c> — the offer's resolution and tier levers.</description></item>
    /// <item><description><c>rctl</c> — the feedback loop, ceiling 4000 kbit/s; <c>rctl:N</c> for another
    /// ceiling in kbit/s, <c>rctl:owrd</c> for the captured ceiling, <c>rctl:wall</c> to fill w4's arrival
    /// half from a clock of our own instead of the media's, <c>rctl:fast</c> for reports at 60 Hz.</description></item>
    /// <item><description><c>net:N</c>, <c>proto:N</c>, <c>feat:N</c> — the three numbers of the
    /// <c>startmediastream</c> envelope, none of which any capture ever varied.</description></item>
    /// <item><description><c>blob:ltrp</c>, <c>blob:nofec</c>, <c>blob:rtcpfb</c>, <c>blob:tiles:N</c> —
    /// the protobuf flags.</description></item>
    /// <item><description><c>endpoint:&lt;modele&gt;</c> — the host model announced to the encoder.</description></item>
    /// </list>
    /// </remarks>
    public static bool ParseVariant(string variant, out StreamTuning tuning, out string label)
    {
        tuning = StreamTuning.Default;
        label = string.IsNullOrEmpty(variant) ? "default" : variant;
        foreach (string piece in label.Split('+', StringSplitOptions.RemoveEmptyEntries))
            if (!ApplyVariant(piece, ref tuning))
                return false;
        return true;
    }

    private static bool ApplyVariant(string piece, ref StreamTuning tuning)
    {
        // The model of endpoint: is the one value whose case reaches the phone,
        // so the piece keeps its own and only the name is folded.
        switch (piece.ToLowerInvariant())
        {
            case "default" or "defaut":
                return true;
            case "half" or "moitie":
                // The offer's only resolution fields, which no capture ever
                // exercised: the phone may honour them, ignore them, or refuse
                // the offer outright. The decoded picture size printed at the
                // end of the run is what says which.
                tuning = tuning with { Offer = tuning.Offer with { MaxWidth = HalfWidth, MaxHeight = HalfHeight } };
                return true;
            case "rctl":
                tuning = tuning with { Rctl = true, RctlMaxBitrateKbps = RctlVariantKbps };
                return true;
            case "rctl:owrd":
                // The arrival clock the reference settled on, spelled out: the
                // media's own 24 kHz base, so the one-way delay comes out at
                // zero. Already the default of RctlFeedback — this name exists
                // so the run that proves it has a name of its own, and it keeps
                // the captured ceiling rather than rctl's 4000.
                tuning = tuning with
                {
                    Rctl = true,
                    RctlArrival = RctlArrivalClock.MediaClock,
                    RctlMaxBitrateKbps = RctlFeedback.CapturedMaxBitrateKbps,
                };
                return true;
            case "rctl:wall":
                tuning = tuning with { Rctl = true, RctlArrival = RctlArrivalClock.WallClock };
                return true;
            case "rctl:fast":
                tuning = tuning with { Rctl = true, RctlMaxBitrateKbps = RctlVariantKbps, RctlReportHz = FastReportHz };
                return true;
            case "blob:ltrp":
                tuning = tuning with { LtrpEnabled = true };
                return true;
            case "blob:nofec":
                tuning = tuning with { FecEnabled = false };
                return true;
            case "blob:rtcpfb":
                tuning = tuning with { AllowRtcpFb = true };
                return true;
        }

        if (Number(piece, "rctl:") is int ceiling and >= 100 and <= 100_000)
        {
            tuning = tuning with { Rctl = true, RctlMaxBitrateKbps = ceiling };
            return true;
        }
        if (Number(piece, "bitrate:") is int kbps and >= 100 and <= 100_000)
        {
            tuning = tuning with { Offer = tuning.Offer with { MaxBitrateKbps = kbps } };
            return true;
        }
        if (Number(piece, "net:") is int network and >= 0 and <= 16)
        {
            tuning = tuning with { AccessNetworkType = network };
            return true;
        }
        if (Number(piece, "proto:") is int protocol and >= 0 and <= 16)
        {
            tuning = tuning with { TransportProtocolType = protocol };
            return true;
        }
        if (Number(piece, "feat:") is int features and >= 0)
        {
            tuning = tuning with { ClientSupportedFeatures = (ulong)features };
            return true;
        }
        if (Number(piece, "blob:tiles:") is int tiles and >= 1 and <= 16)
        {
            tuning = tuning with { TilesPerFrame = tiles };
            return true;
        }
        if (piece.StartsWith("endpoint:", StringComparison.OrdinalIgnoreCase) && piece.Length > 9)
        {
            tuning = tuning with { HostModel = piece[9..] };
            return true;
        }
        return false;
    }

    /// <summary>The number after a prefix, or null when the piece is not that lever.</summary>
    private static int? Number(string piece, string prefix) =>
        piece.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
        && int.TryParse(piece[prefix.Length..], out int value) ? value : null;

    /// <summary>
    /// A finger that scrolls and never stops, until the token says so.
    /// </summary>
    /// <remarks>
    /// Through the injector's queue rather than its awaited calls, because that
    /// is the path a real hand takes and the two must never be mixed on one
    /// contact. The producer's own cadence is the pump's, 120 Hz: the pump
    /// holds the platform timer at its fine resolution for as long as it runs,
    /// so the periodic timer below is accurate from the first stroke on.
    /// </remarks>
    private static async Task ScrollAsync(InputInjector input, double amplitude, CancellationToken token)
    {
        double top = 0.5 - amplitude / 2, bottom = 0.5 + amplitude / 2;
        int steps = Math.Max(1, (int)Math.Round(StrokeMs * TouchHz / 1000.0));
        bool downwards = true;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000.0 / TouchHz));
        try
        {
            while (!token.IsCancellationRequested)
            {
                double from = downwards ? top : bottom;
                double to = downwards ? bottom : top;
                input.QueueTouchDown(0.5, from);
                for (int step = 1; step <= steps; step++)
                {
                    await timer.WaitForNextTickAsync(token);
                    input.QueueMove(0.5, from + (to - from) * step / steps);
                }
                input.QueueTouchUp(0.5, to);
                downwards = !downwards;
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            // Whatever happened, the finger comes off the screen.
            input.QueueTouchUp(0.5, 0.5);
        }
    }

    /// <summary>One second of the stream, as the motion test reads it.</summary>
    private readonly record struct MotionSample(
        double WindowSeconds, double PacketsPerSecond, double KilobitsPerSecond, double FramesPerSecond,
        double MeanFrameKilobytes, double MaxFrameKilobytes,
        double DriftMeanMs, double DriftMaxMs, double DriftLastMs, double JitterMs, double LagMs);

    /// <summary>
    /// The stream measured straight off the wire, one second at a time.
    /// </summary>
    /// <remarks>
    /// Deliberately not the media session's own counters: what this test needs
    /// is per-picture, and the figure it turns on — the drift — has no meaning
    /// as a running total. Every datagram is read here, the marker bit closes a
    /// picture, and the drift is the difference between two elapsed times, the
    /// one on our clock and the one the phone's own timestamps describe, both
    /// counted from the first picture of the session. Zero while the phone
    /// keeps up; a few hundred milliseconds when it is a few hundred
    /// milliseconds behind, whatever the cause.
    /// </remarks>
    private sealed class MotionMeter
    {
        /// <summary>This stream's RTP clock, measured at 24 kHz — 400 units per picture at sixty a second.</summary>
        private const double MediaClockKhz = 24.0;

        private readonly object _gate = new();
        private readonly Stopwatch _window = Stopwatch.StartNew();

        private bool _haveOrigin;
        private uint _originTimestamp;
        private long _originTicks;
        private uint _lastTimestamp;

        private long _frameBytes;
        private double _jitter;
        private double _previousTransit;
        private bool _haveTransit;

        private long _packets, _bytes, _frames, _framesBytes, _maxFrameBytes;
        private double _driftSum, _driftMax, _driftLast;
        private bool _haveDrift;

        /// <summary>Reads one datagram of the video port; control packets are not the subject here.</summary>
        public void Add(ReadOnlyMemory<byte> datagram)
        {
            var span = datagram.Span;
            if (RtpPacket.LooksLikeRtcp(span))
                return;
            var packet = RtpPacket.Parse(span);
            if (!packet.Valid)
                return;
            long ticks = Stopwatch.GetTimestamp();

            lock (_gate)
            {
                _packets++;
                _bytes += datagram.Length;
                _frameBytes += datagram.Length;
                _lastTimestamp = packet.Timestamp;

                // RFC 3550 interarrival jitter, in the stream's own units: the
                // smoothed difference between the spacing on the wire and the
                // spacing in the timestamps. Origin-independent, so the two
                // clocks never have to be reconciled.
                double transit = ticks * (MediaClockKhz * 1000.0 / Stopwatch.Frequency) - packet.Timestamp;
                if (_haveTransit)
                    _jitter += (Math.Abs(transit - _previousTransit) - _jitter) / 16.0;
                _previousTransit = transit;
                _haveTransit = true;

                if (!packet.Marker)
                    return;                                  // a picture ends on the marker bit, and only there

                if (!_haveOrigin)
                {
                    _haveOrigin = true;
                    _originTimestamp = packet.Timestamp;
                    _originTicks = ticks;
                }
                double drift = Elapsed(ticks) - Media(packet.Timestamp);
                _frames++;
                _framesBytes += _frameBytes;
                _maxFrameBytes = Math.Max(_maxFrameBytes, _frameBytes);
                _frameBytes = 0;
                _driftSum += drift;
                _driftMax = _haveDrift ? Math.Max(_driftMax, drift) : drift;
                _driftLast = drift;
                _haveDrift = true;
            }
        }

        /// <summary>Closes the window, reports it and starts the next.</summary>
        public MotionSample Take()
        {
            lock (_gate)
            {
                double window = Math.Max(0.001, _window.Elapsed.TotalSeconds);
                double lag = _haveOrigin ? Elapsed(Stopwatch.GetTimestamp()) - Media(_lastTimestamp) : 0;
                var sample = new MotionSample(
                    window,
                    _packets / window,
                    _bytes * 8 / 1000.0 / window,
                    _frames / window,
                    _frames > 0 ? _framesBytes / (double)_frames / 1024.0 : 0,
                    _maxFrameBytes / 1024.0,
                    _frames > 0 ? _driftSum / _frames : 0,
                    _haveDrift ? _driftMax : 0,
                    _driftLast,
                    _jitter / MediaClockKhz,
                    lag);

                _packets = _bytes = _frames = _framesBytes = _maxFrameBytes = 0;
                _driftSum = _driftMax = 0;
                _haveDrift = false;
                _window.Restart();
                return sample;
            }
        }

        /// <summary>Milliseconds on our clock since the first picture of the session.</summary>
        private double Elapsed(long ticks) => (ticks - _originTicks) * 1000.0 / Stopwatch.Frequency;

        /// <summary>Milliseconds the phone's own timestamps put between that picture and this one.</summary>
        private double Media(uint timestamp) => unchecked((int)(timestamp - _originTimestamp)) / MediaClockKhz;
    }

    // --- Bitmap ------------------------------------------------------------------

    /// <summary>Writes a 24-bit BMP by hand: header, DIB header, rows bottom-up.</summary>
    public static void WriteBmp(string path, VideoFrame frame)
    {
        string? folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

        int width = frame.Width, height = frame.Height;
        int rowSize = (width * 3 + 3) & ~3;
        int pixels = rowSize * height;
        byte[] bgra = frame.Bgra;

        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        Span<byte> header = stackalloc byte[54];
        header[0] = (byte)'B'; header[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(header[2..], (uint)(54 + pixels));
        BinaryPrimitives.WriteUInt32LittleEndian(header[10..], 54);
        BinaryPrimitives.WriteUInt32LittleEndian(header[14..], 40);
        BinaryPrimitives.WriteInt32LittleEndian(header[18..], width);
        BinaryPrimitives.WriteInt32LittleEndian(header[22..], height);
        BinaryPrimitives.WriteUInt16LittleEndian(header[26..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header[28..], 24);
        BinaryPrimitives.WriteUInt32LittleEndian(header[34..], (uint)pixels);
        BinaryPrimitives.WriteInt32LittleEndian(header[38..], 2835);
        BinaryPrimitives.WriteInt32LittleEndian(header[42..], 2835);
        file.Write(header);

        byte[] row = new byte[rowSize];
        for (int y = height - 1; y >= 0; y--)
        {
            int source = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                row[x * 3] = bgra[source + x * 4];
                row[x * 3 + 1] = bgra[source + x * 4 + 1];
                row[x * 3 + 2] = bgra[source + x * 4 + 2];
            }
            file.Write(row);
        }
    }
}
