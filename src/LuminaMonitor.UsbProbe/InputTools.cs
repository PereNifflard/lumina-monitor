using System.Collections.Concurrent;
using System.Diagnostics;
using LuminaMonitor.Core;
using LuminaMonitor.Core.Ddi;
using LuminaMonitor.Core.Hid;

/// <summary>
/// The input half of the probe: load the touch path on purpose and watch what
/// the loading does to everything else.
/// </summary>
/// <remarks>
/// A mouse hands the window five hundred to a thousand positions a second and
/// the phone shows sixty. Whether that difference is thrown away or queued is
/// the whole question behind "the lag grows with the speed of the hand", and
/// the only way to settle it is to produce reports at a mouse's rate while
/// timing something independent — an ICMPv6 echo — through the same tunnel.
/// </remarks>
internal static class InputTools
{
    /// <summary>How the flood hands its positions to the injector.</summary>
    public enum FloodMode
    {
        /// <summary>One report per position, chained: what the window used to do.</summary>
        Chained,

        /// <summary>One pending position, sent by the injector's own pump.</summary>
        Coalesced,
    }

    /// <summary>
    /// flood [secondes] [chained|coalesce] — a continuous circular drag at 1000
    /// positions a second, with a ping every 200 ms alongside it.
    /// </summary>
    public static async Task FloodAsync(int seconds, FloodMode mode, string ddiFolder, Action<string> say)
    {
        await using var session = new DeviceSession(new DdiSource(ddiFolder), new ConsoleLog());
        session.UnlockRequired += message => say(message);
        await session.ConnectAsync();

        var input = session.Input;
        var net = session.Net;
        if (net is null) { say("Tunnel indisponible."); return; }

        // The echo timing runs on its own, so the flood never waits for it.
        ushort identifier = (ushort)Random.Shared.Next(1, 0x10000);
        byte[] echoPayload = new byte[32];
        Random.Shared.NextBytes(echoPayload);
        var pending = new ConcurrentDictionary<ushort, (long Sent, TaskCompletionSource<double> Reply)>();
        net.EchoReply = message =>
        {
            long arrived = Stopwatch.GetTimestamp();
            var span = message.Span;
            if (span.Length < 8) return;
            ushort id = (ushort)((span[4] << 8) | span[5]);
            ushort seq = (ushort)((span[6] << 8) | span[7]);
            if (id != identifier || !pending.TryRemove(seq, out var waiting)) return;
            waiting.Reply.TrySetResult((arrived - waiting.Sent) * 1000.0 / Stopwatch.Frequency);
        };

        // The chained mode reproduces the window's old input chain exactly: the
        // producer never waits, so every position it hands over is a task queued
        // behind the previous one.
        long produced = 0, completed = 0;
        Task chain = Task.CompletedTask;
        object chainGate = new();
        void Chain(Func<Task> work)
        {
            Interlocked.Increment(ref produced);
            lock (chainGate)
                chain = chain.ContinueWith(async _ =>
                {
                    try { await work(); }
                    catch (Exception) { }
                    finally { Interlocked.Increment(ref completed); }
                }, TaskScheduler.Default).Unwrap();
        }

        var start = session.Input.Stats;
        say($"Flot de {seconds} s a 1000 positions/s, mode {(mode == FloodMode.Chained ? "chaine (avant)" : "coalescence (apres)")}…");

        await input.TouchDownAsync(0.5, 0.5);
        var clock = Stopwatch.StartNew();
        var flood = Task.Run(() =>
        {
            double period = Stopwatch.Frequency / 1000.0;              // one position per millisecond
            long next = Stopwatch.GetTimestamp();
            long limit = clock.ElapsedTicks + (long)seconds * Stopwatch.Frequency;
            while (clock.ElapsedTicks < limit)
            {
                double angle = clock.Elapsed.TotalSeconds * 2 * Math.PI * 2;   // two turns a second
                double x = 0.5 + 0.06 * Math.Cos(angle);
                double y = 0.5 + 0.06 * Math.Sin(angle);
                if (mode == FloodMode.Chained)
                    Chain(() => input.TouchMoveAsync(x, y));
                else
                    input.QueueMove(x, y);
                next += (long)period;
                while (Stopwatch.GetTimestamp() < next)
                    Thread.SpinWait(20);
            }
        });

        var trips = new List<double>();
        int lost = 0;
        ushort sequence = 0;
        var samples = new List<string>();
        while (!flood.IsCompleted)
        {
            sequence++;
            var reply = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[sequence] = (Stopwatch.GetTimestamp(), reply);
            await net.SendEchoAsync(identifier, sequence, echoPayload);
            double rtt = -1;
            try { rtt = await reply.Task.WaitAsync(TimeSpan.FromSeconds(1)); trips.Add(rtt); }
            catch (TimeoutException) { pending.TryRemove(sequence, out _); lost++; }

            var now = input.Stats;
            long depth = mode == FloodMode.Chained
                ? Interlocked.Read(ref produced) - Interlocked.Read(ref completed)
                : now.QueueDepth;
            samples.Add($"  t={clock.Elapsed.TotalSeconds,4:F1} s : RTT {(rtt < 0 ? "  perdu" : $"{rtt,6:F2} ms")}"
                + $"  file {depth,6}  envoyes {now.ReportsSent - start.ReportsSent,6}"
                + $"  abandonnes {now.MovesDropped - start.MovesDropped,6}"
                + $"  TCP {now.UnackedSegments,3} seg / {now.UnackedBytes,6} o  attente ecriture {now.SendWaiters}");
            await Task.Delay(200);
        }
        await flood;
        await input.TouchUpAsync(0.5, 0.5);

        // The tail matters as much as the flood: a queue that is still draining
        // after the hand stopped is precisely the lag one sees.
        long drainStart = Stopwatch.GetTimestamp();
        while ((Interlocked.Read(ref produced) - Interlocked.Read(ref completed)) > 0 &&
               (Stopwatch.GetTimestamp() - drainStart) < 10 * Stopwatch.Frequency)
            await Task.Delay(20);
        double drainMs = (Stopwatch.GetTimestamp() - drainStart) * 1000.0 / Stopwatch.Frequency;

        net.EchoReply = null;
        foreach (string line in samples)
            say(line);

        var final = input.Stats;
        long producedTotal = Interlocked.Read(ref produced);
        say($"*** FLOT {clock.Elapsed.TotalSeconds:F1} s, mode {(mode == FloodMode.Chained ? "chaine" : "coalescence")} ***");
        say($"  positions produites : {(mode == FloodMode.Chained ? producedTotal : (long)(clock.Elapsed.TotalSeconds * 1000))} (visee 1000/s)");
        say($"  rapports envoyes : {final.ReportsSent - start.ReportsSent}"
            + $" -> {(final.ReportsSent - start.ReportsSent) / clock.Elapsed.TotalSeconds:F0}/s");
        say($"  positions abandonnees : {final.MovesDropped - start.MovesDropped}"
            + $", fenetre HTTP/2 : {final.WindowDrops - start.WindowDrops}");
        say($"  profondeur d'emission finale : file {(mode == FloodMode.Chained ? producedTotal - Interlocked.Read(ref completed) : final.QueueDepth)},"
            + $" TCP {final.UnackedSegments} segment(s) / {final.UnackedBytes} octet(s), attente ecriture {final.SendWaiters}");
        if (mode == FloodMode.Chained)
            say($"  vidange apres l'arret de la main : {drainMs:F0} ms");
        if (trips.Count == 0)
        {
            say($"  *** AUCUN ECHO PENDANT LE FLOT *** ({lost} perdu(s)) — le tunnel ne repond plus sous charge.");
            return;
        }
        trips.Sort();
        double middle = trips.Count % 2 == 1
            ? trips[trips.Count / 2]
            : (trips[trips.Count / 2 - 1] + trips[trips.Count / 2]) / 2;
        say($"  RTT pendant le flot : {trips.Count} reponse(s), {lost} perte(s) —"
            + $" min {trips[0]:F2} ms, mediane {middle:F2} ms, max {trips[^1]:F2} ms");
    }

    /// <summary>
    /// latency-test [n] [move] — how long the phone takes to show what we
    /// asked for, measured on the pixels rather than guessed at.
    /// </summary>
    /// <remarks>
    /// Every other figure in this project times one link of the chain. This one
    /// times the whole thing, from the moment the report leaves to the moment
    /// the change it caused comes back decoded, and it does so without trusting
    /// anything: the trigger is a hardware button (or a short drag) sent at a
    /// known instant, and the answer is the first decoded picture that differs
    /// from the one on screen when the button went down.
    ///
    /// <para>The threshold is not a constant. Two seconds of a still screen are
    /// watched first to learn what "no change" actually looks like on this
    /// stream — a compressed picture is never bit-identical twice — and the
    /// threshold is four times that noise. A figure below the noise floor would
    /// fire on the encoder's own grain; four times it fires on the volume
    /// banner and nothing else.</para>
    /// </remarks>
    public static async Task LatencyTestAsync(int trials, bool moveMode, string ddiFolder, Action<string> say)
    {
        await using var session = new DeviceSession(new DdiSource(ddiFolder), new ConsoleLog())
        {
            DecodeVideo = true,
        };
        session.UnlockRequired += message => say(message);

        object gate = new();
        byte[]? reference = null;            // subsampled luma of the picture on screen at T0
        int sampleWidth = 0, sampleHeight = 0;
        double lastDifference = 0, lastMean = 0;
        long triggerTicks = 0;               // T0, or 0 while not measuring
        double threshold = double.MaxValue;
        TaskCompletionSource<double>? waiting = null;
        var noise = new List<double>();
        bool calibrating = false;

        session.FrameDecoded += frame =>
        {
            long decoded = frame.DecodedTicks;
            lock (gate)
            {
                byte[] sample = Subsample(frame, out int width, out int height);
                if (reference is null || width != sampleWidth || height != sampleHeight)
                {
                    reference = sample;
                    sampleWidth = width;
                    sampleHeight = height;
                    return;
                }

                var (mean, difference) = Difference(reference, sample, width, height);
                lastDifference = difference;
                lastMean = mean;

                if (calibrating)
                {
                    noise.Add(difference);
                    reference = sample;                       // frame to frame, on a still screen
                    return;
                }

                if (waiting is not null && triggerTicks != 0 && difference > threshold)
                {
                    waiting.TrySetResult((decoded - triggerTicks) * 1000.0 / Stopwatch.Frequency);
                    waiting = null;
                    return;
                }
                if (waiting is null)
                    reference = sample;                       // idle: keep following the screen
            }
        };

        await session.ConnectAsync();
        var media = session.Media;
        if (media is null || !media.Streaming)
        {
            say($"*** FLUX REFUSE *** {media?.Failure ?? "(aucun motif rapporte)"}");
            return;
        }

        say("Stabilisation 2 s : mesure du bruit d'un ecran immobile…");
        lock (gate) { calibrating = true; noise.Clear(); }
        await Task.Delay(2000);
        double meanNoise, worstNoise, ninetieth;
        lock (gate)
        {
            calibrating = false;
            var sorted = noise.Order().ToList();
            meanNoise = sorted.Count > 0 ? sorted.Average() : 0;
            worstNoise = sorted.Count > 0 ? sorted[^1] : 0;
            // Four times the noise, where "the noise" is the ninetieth centile
            // and not the worst of it. The worst was tried and does not work:
            // one stray picture during the two seconds — a clock ticking over,
            // the last banner still fading — raised the bar above the signal
            // being looked for and every trial then timed out. The centile
            // ignores that picture and still sits well above the encoder's own
            // grain.
            ninetieth = sorted.Count > 0 ? sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * 0.9))] : 0;
            threshold = Math.Max(4 * ninetieth, 1.0);
        }
        say($"  bruit mesure sur {noise.Count} image(s), pire tuile : moyenne {meanNoise:F2},"
            + $" 90e centile {ninetieth:F2}, max {worstNoise:F2} niveau(x) de luminance"
            + $" -> seuil de detection {threshold:F2}");

        var results = new List<double>();
        for (int trial = 1; trial <= trials; trial++)
        {
            var answer = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gate)
            {
                reference = null;                             // the next picture becomes the reference
                waiting = null;
            }

            // Wait for the screen to be still before arming. The banner from the
            // previous trial takes a second or two to fade, and a trial armed
            // while it is still fading times that fade, not the next press.
            long quietFrom = Stopwatch.GetTimestamp();
            while ((Stopwatch.GetTimestamp() - quietFrom) < 4 * Stopwatch.Frequency)
            {
                await Task.Delay(100);
                lock (gate)
                    if (reference is not null && lastDifference < threshold)
                        break;
            }
            await Task.Delay(150);                            // let one more picture land as the reference
            lock (gate)
            {
                waiting = answer;
                triggerTicks = Stopwatch.GetTimestamp();
            }

            if (moveMode)
            {
                // Half the screen's height in 100 ms, ten positions: a flick
                // slow enough for iOS to follow and long enough to move a list.
                // This variant only means anything on a screen that actually
                // scrolls — on a still one there is nothing for it to time, and
                // the run says so rather than inventing a figure.
                await session.Input.TouchDownAsync(0.5, 0.75);
                for (int step = 1; step <= 10; step++)
                {
                    await Task.Delay(10);
                    await session.Input.TouchMoveAsync(0.5, 0.75 - 0.05 * step);
                }
                await session.Input.TouchUpAsync(0.5, 0.25);
            }
            else
            {
                // Always the same direction. Alternating was tried and measured
                // worse: a volume-down whose banner is already on screen changes
                // almost nothing, and what the run then times is the previous
                // banner fading out, a second and a half later.
                await session.Input.PressButtonAsync("volume-up");
            }

            try
            {
                double milliseconds = await answer.Task.WaitAsync(TimeSpan.FromSeconds(3));
                results.Add(milliseconds);
                say($"  essai {trial,2} : {milliseconds,7:F1} ms");
            }
            catch (TimeoutException)
            {
                lock (gate) waiting = null;
                say($"  essai {trial,2} : aucune image differente en 3 s"
                    + $" (derniere difference : pire tuile {lastDifference:F3}, image entiere {lastMean:F3};"
                    + $" seuil {threshold:F3})");
            }
            await Task.Delay(3000);
        }

        if (results.Count == 0)
        {
            say("*** AUCUNE MESURE *** — rien n'a change a l'ecran ; verifier que le telephone est deverrouille et l'ecran allume.");
            return;
        }
        results.Sort();
        double median = results.Count % 2 == 1
            ? results[results.Count / 2]
            : (results[results.Count / 2 - 1] + results[results.Count / 2]) / 2;
        say($"*** LATENCE DE BOUT EN BOUT ({(moveMode ? "glisser" : "bouton volume")}) *** {results.Count}/{trials} mesure(s) :"
            + $" min {results[0]:F1} ms, mediane {median:F1} ms, max {results[^1]:F1} ms");
    }

    /// <summary>One luma sample every four pixels each way: enough to see a banner, cheap enough for 60 Hz.</summary>
    private static byte[] Subsample(LuminaMonitor.Core.Media.VideoFrame frame, out int width, out int height)
    {
        const int step = 4;
        width = frame.Width / step;
        height = frame.Height / step;
        byte[] sample = new byte[width * height];
        var luma = frame.Nv12;
        for (int y = 0; y < height; y++)
        {
            int source = y * step * frame.Stride;
            int target = y * width;
            for (int x = 0; x < width; x++)
                sample[target + x] = luma[source + x * step];
        }
        return sample;
    }

    /// <summary>
    /// How much two pictures differ, in luma levels: over the whole picture, and
    /// over the worst of its tiles.
    /// </summary>
    /// <remarks>
    /// The whole-picture mean is the honest headline figure and it is useless as
    /// a trigger: the volume banner is perhaps a three-hundredth of the screen,
    /// so it moves that mean by about a tenth of a luma level — barely above the
    /// encoder grain, and only once its fade-in animation is well under way. The
    /// same banner moves the tile it sits in by tens of levels, and it does so on
    /// the very first picture that shows it. Tiles it is.
    /// </remarks>
    private static (double Mean, double TileMax) Difference(byte[] a, byte[] b, int width, int height)
    {
        // Small tiles on purpose. Coarse ones were tried: the volume banner
        // fills so little of a sixteenth of the screen that it raised its own
        // tile by only eight or nine luma levels, which the calibration noise
        // came within reach of. A tile of about ninety pixels a side is smaller
        // than the banner, so the tile it lands in changes wholesale.
        const int tilesX = 16, tilesY = 32;
        long total = 0;
        double worst = 0;
        for (int ty = 0; ty < tilesY; ty++)
        {
            int top = ty * height / tilesY, bottom = (ty + 1) * height / tilesY;
            for (int tx = 0; tx < tilesX; tx++)
            {
                int left = tx * width / tilesX, right = (tx + 1) * width / tilesX;
                long sum = 0;
                for (int y = top; y < bottom; y++)
                {
                    int row = y * width;
                    for (int x = left; x < right; x++)
                        sum += Math.Abs(a[row + x] - b[row + x]);
                }
                total += sum;
                int count = (bottom - top) * (right - left);
                if (count > 0)
                    worst = Math.Max(worst, (double)sum / count);
            }
        }
        return ((double)total / (width * height), worst);
    }
}
