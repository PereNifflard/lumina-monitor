using System.Globalization;
using LuminaMonitor.Core.Media;

/// <summary>
/// The stream watch, walked end to end without a phone.
/// </summary>
/// <remarks>
/// The ladder this exercises — three seconds of silence buys a key frame
/// request, two more buys a fresh media session, two failed sessions buy a soft
/// reset — is by nature the code that runs when everything else has already
/// gone wrong, which is the worst possible place to find out it was written
/// backwards. <see cref="StreamWatchdog"/> takes every instant as an argument
/// precisely so the whole thing can be played here on invented seconds, at the
/// same half-second beat the media session really uses.
/// </remarks>
internal static class WatchdogTools
{
    /// <summary>The beat the media session's watch really runs at.</summary>
    private const double Beat = 0.5;

    public static int SelfTest(Action<string> say)
    {
        say($"Seuils : silence {StreamWatchdog.StallSeconds:0} s, sursis image cle "
            + $"{StreamWatchdog.KeyFrameGraceSeconds:0} s, {StreamWatchdog.RestartFailuresBeforeReset} relances avant reset.");

        int failures = 0;

        // A stream that never stops is never touched.
        failures += Check(say, "flux sain",
            arriving: _ => true, until: 10, restarts: [],
            expected: []);

        // A gap shorter than the threshold is not a stall: no false alarm on
        // the half second the phone takes to hand over a bigger picture.
        failures += Check(say, "trou de 2,5 s",
            arriving: now => now <= 2.0 || now >= 4.5, until: 8, restarts: [],
            expected: []);

        // Three seconds of silence: one key frame request, and only one.
        // Pictures come back on the strength of it, and the watch says so once.
        failures += Check(say, "image cle suffisante",
            arriving: now => now <= 1.0 || now >= 4.5, until: 8, restarts: [],
            expected: ["4.0 RequestKeyFrame", "4.5 Recovered"]);

        // The key frame changed nothing: a new media session two seconds later,
        // and the pictures that follow it end the alarm.
        failures += Check(say, "relance du flux reussie",
            arriving: now => now <= 1.0 || now >= 6.5, until: 9, restarts: [true],
            expected: ["4.0 RequestKeyFrame", "6.0 RestartStream", "6.5 Recovered"]);

        // Neither session opened: the ladder is climbed once more from the
        // bottom, and the second failure asks for the developer image to be
        // cycled rather than for a third session.
        failures += Check(say, "deux relances ratees",
            arriving: now => now <= 1.0, until: 13, restarts: [false, false],
            expected: ["4.0 RequestKeyFrame", "6.0 RestartStream",
                       "9.0 RequestKeyFrame", "11.0 RestartStream", "11.0 SoftReset"]);

        // A restart that works resets the count: one failure long ago plus one
        // failure now is not two in a row.
        failures += Check(say, "echec, retablissement, echec : pas de reset",
            arriving: now => now <= 1.0 || (now >= 6.5 && now <= 9.0), until: 16, restarts: [false, false],
            expected: ["4.0 RequestKeyFrame", "6.0 RestartStream",
                       "6.5 Recovered",
                       "12.0 RequestKeyFrame", "14.0 RestartStream"]);

        say(failures == 0
            ? "Veille du flux : tous les scenarios passent."
            : $"Veille du flux : {failures} scenario(s) en echec.");
        return failures == 0 ? 0 : 9;
    }

    /// <summary>
    /// Plays one scenario and compares the decisions, in order, to the expected
    /// ones.
    /// </summary>
    /// <remarks>
    /// The rig is the media session's watch loop and nothing else: every half
    /// second, a packet counter that either moved or did not, and the outcome of
    /// a restart handed back the moment it is asked for. Real restarts take
    /// seconds; the watch is stopped for the whole of one, so the instant here
    /// costs nothing but the reading.
    /// </remarks>
    private static int Check(Action<string> say, string name,
        Func<double, bool> arriving, double until, bool[] restarts, string[] expected)
    {
        var watchdog = new StreamWatchdog(0);
        var outcomes = new Queue<bool>(restarts);
        var decisions = new List<string>();

        for (double now = Beat; now <= until + Beat / 2; now += Beat)
        {
            var action = arriving(now) ? watchdog.OnPacket(now) : watchdog.Tick(now);
            if (action is StallAction.None)
                continue;
            decisions.Add($"{Instant(now)} {action}");
            if (action is not StallAction.RestartStream)
                continue;
            var next = watchdog.OnRestart(outcomes.Count > 0 && outcomes.Dequeue(), now);
            if (next is not StallAction.None)
                decisions.Add($"{Instant(now)} {next}");
        }

        bool ok = decisions.SequenceEqual(expected);
        say($"  {(ok ? "OK   " : "ECHEC")} {name} : {Show(decisions)}");
        if (!ok)
            say($"        attendu : {Show(expected)}");
        return ok ? 0 : 1;
    }

    /// <summary>One instant, written the same way on every machine's locale.</summary>
    private static string Instant(double seconds) => seconds.ToString("F1", CultureInfo.InvariantCulture);

    private static string Show(IEnumerable<string> decisions)
    {
        string joined = string.Join(" | ", decisions);
        return joined.Length == 0 ? "(aucune decision)" : joined;
    }
}
