using System.Diagnostics;
using LuminaMonitor.Core;
using LuminaMonitor.Core.Ddi;
using LuminaMonitor.Core.Media;

/// <summary>
/// The four controls drawn on the window's chassis, tried on the real phone,
/// and the one question the drawing cannot answer: what a locked screen does to
/// the session that is mirroring it.
/// </summary>
/// <remarks>
/// Every claim here is settled by a picture. A press on volume raises a banner,
/// a press on the Action button raises the silent-mode banner, and a locked
/// screen either stops the stream or turns it black — three outcomes that look
/// identical in a log and cannot be confused once the decoded frame is on disk
/// with its mean luminance beside it.
///
/// <para>The lock is measured twice on purpose. A four-second lock stays inside
/// the stream watch's first rung, so what is seen is the phone's own behaviour;
/// a twelve-second one goes past the key-frame request and the stream restart,
/// which is what the window will actually live through when somebody locks the
/// phone and walks away.</para>
/// </remarks>
internal static class ChassisTools
{
    /// <param name="lockSeconds">
    /// How long the second, longer lock lasts; zero skips it. It is a parameter
    /// rather than a constant because the stream watch's own ladder runs on the
    /// same clock — a key frame at three seconds, a stream restart at five, and
    /// a soft reset after two failed restarts, which on a locked phone waits for
    /// somebody to unlock it. A run that has to end on its own stays under that
    /// last rung.
    /// </param>
    public static async Task ChassisTestAsync(string ddiFolder, Action<string> say, string outFolder,
        int lockSeconds = 9)
    {
        Directory.CreateDirectory(outFolder);
        await using var session = new DeviceSession(new DdiSource(ddiFolder), new ConsoleLog())
        {
            VideoCodecs = VideoCodecs.AvcOnly,
            DecodeVideo = true,
        };
        session.UnlockRequired += message => say("!! " + message);
        session.RestartRequired += message => say("!! " + message);

        object gate = new();
        long frames = 0;
        bool grab = false;
        VideoFrame? shot = null;
        session.FrameDecoded += frame =>
        {
            Interlocked.Increment(ref frames);
            lock (gate)
                if (grab) { shot = frame.Copy(); grab = false; }
        };

        await session.ConnectAsync();
        var media = session.Media;
        if (media is null || !media.Streaming)
        {
            say($"*** FLUX REFUSE *** {media?.Failure ?? "(aucun motif rapporte)"}");
            return;
        }
        var input = session.Input;

        // --- The two measuring instruments ----------------------------------------

        async Task<double> ShotAsync(string name)
        {
            lock (gate) { shot = null; grab = true; }
            long deadline = Stopwatch.GetTimestamp() + 2 * Stopwatch.Frequency;
            VideoFrame? picture = null;
            while (Stopwatch.GetTimestamp() < deadline)
            {
                await Task.Delay(50);
                lock (gate) picture = shot;
                if (picture is not null) break;
            }
            if (picture is null)
            {
                say($"  {name,-16} : AUCUNE IMAGE en 2 s — le flux ne produit plus rien.");
                return -1;
            }
            double luma = MeanLuma(picture);
            VideoTools.WriteBmp(Path.Combine(outFolder, name + ".bmp"), picture);
            say($"  {name,-16} : {picture.Width}x{picture.Height}, luminance moyenne {luma,5:F1}/255"
                + $" -> {name}.bmp");
            return luma;
        }

        async Task RateAsync(string label, double seconds)
        {
            var before = media.Stats;
            long framesBefore = Interlocked.Read(ref frames);
            var clock = Stopwatch.StartNew();
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            var after = media.Stats;
            double window = Math.Max(0.001, clock.Elapsed.TotalSeconds);
            say($"  {label,-22} : {(after.Packets - before.Packets) / window,7:F0} paquets/s,"
                + $" {(Interlocked.Read(ref frames) - framesBefore) / window,5:F1} images/s,"
                + $" etat {session.State}");
        }

        // --- The buttons ------------------------------------------------------------

        say("=== BOUTONS DU CHASSIS =========================================");
        await Task.Delay(1500);
        await ShotAsync("00-depart");

        // The Action button is pressed twice: once to see the banner, once to
        // put the phone back the way it was found.
        var presses = new (string Button, string Name)[]
        {
            ("volume-up", "10-volume-up"),
            ("volume-down", "11-volume-down"),
            ("mute", "12-mute"),
            ("mute", "13-mute-retour"),
        };
        foreach (var (button, name) in presses)
        {
            say($"Bouton « {button} »…");
            await input.PressButtonAsync(button);
            await Task.Delay(900);
            await ShotAsync(name);
            await Task.Delay(1800);           // let the banner fade before the next one
        }

        // --- The lock, twice --------------------------------------------------------

        say("=== VERROUILLAGE COURT (4 s) ===================================");
        await RateAsync("avant verrouillage", 3);
        say("Bouton « lock » (0x30 maintenu 500 ms)…");
        await input.PressButtonAsync("lock");
        for (int second = 1; second <= 4; second++)
            await RateAsync($"t+{second} s verrouille", 1);
        await ShotAsync("30-verrouille-court");

        // The wake ladder. Consumer 0x30 puts the screen out and does not bring
        // it back, whatever it is held for — so the thing that does has to be
        // found rather than assumed, and one session is all the display service
        // will give us before it wants a minute to itself. Each rung is tried,
        // photographed and weighed; the first frame that is not pure black wins
        // and the rest are skipped.
        // The rung that is no longer here: Consumer 0x30 tapped for 40 ms. It was
        // tried on 9 September 2026 and left the frame at luminance 0.0/255, so
        // the name it had in the button table was removed rather than kept as a
        // control that does nothing.
        var ladder = new (string Label, Func<Task> Act)[]
        {
            ("home — Consumer 0x40", () => input.PressButtonAsync("home")),
            ("tap au centre de l'ecran", () => input.TapAsync(0.5, 0.5)),
            ("volume-up — Consumer 0xE9", () => input.PressButtonAsync("volume-up")),
        };
        string? woke = null;
        for (int rung = 0; rung < ladder.Length && woke is null; rung++)
        {
            say($"Reveil, essai {rung + 1}/{ladder.Length} : {ladder[rung].Label}…");
            await ladder[rung].Act();
            await Task.Delay(1500);
            double luma = await ShotAsync($"3{rung + 1}-reveil-{rung + 1}");
            if (luma > 1.0)
                woke = ladder[rung].Label;
        }
        say(woke is null
            ? "*** AUCUN DES ESSAIS N'A RALLUME L'ECRAN ***"
            : $"*** ECRAN RALLUME PAR : {woke} ***");
        for (int second = 1; second <= 3; second++)
            await RateAsync($"t+{second} s apres reveil", 1);

        if (lockSeconds > 0)
        {
            say($"=== VERROUILLAGE LONG ({lockSeconds} s) ================================");
            say("Bouton « lock »…");
            await input.PressButtonAsync("lock");
            for (int second = 1; second <= lockSeconds; second++)
                await RateAsync($"t+{second} s verrouille", 1);
            await ShotAsync("40-verrouille-long");

            say("Bouton « wake »…");
            await input.PressButtonAsync("wake");
            for (int second = 1; second <= 8; second++)
                await RateAsync($"t+{second} s reveille", 1);
            await ShotAsync("41-reveille-long");
        }

        var final = media.Stats;
        say($"*** FIN *** etat {session.State}, {final.Packets} paquets, {final.FramesDecoded} images decodees,"
            + $" {final.KeyFrames} image(s) cle (PLI {final.KeyFrameRequests}), {session.Resets} reset(s) doux.");
        say($"Images dans {Path.GetFullPath(outFolder)}");
    }

    /// <summary>The mean of the luma plane: the one number that tells a black screen from no screen.</summary>
    private static double MeanLuma(VideoFrame frame)
    {
        long sum = 0;
        var luma = frame.Nv12;
        for (int y = 0; y < frame.Height; y++)
        {
            int row = y * frame.Stride;
            for (int x = 0; x < frame.Width; x++)
                sum += luma[row + x];
        }
        return (double)sum / (frame.Width * frame.Height);
    }
}
