using LuminaMonitor.Core.Audio;
using LuminaMonitor.Core;
using LuminaMonitor.Core.Ddi;
using LuminaMonitor.Core.Hid;
using LuminaMonitor.Core.Media;
using LuminaMonitor.Core.RemoteXpc;
using LuminaMonitor.Core.Tunnel;
using LuminaMonitor.Core.Usb;

/// <summary>
/// The audio half of the probe: what the phone answers when asked for sound,
/// and what Windows would be able to do with it.
/// </summary>
/// <remarks>
/// Two commands, and they answer two halves of one question. <c>audio-info</c>
/// opens a real audio stream and reports the negotiation whole — the answer
/// verbatim, the payload types on the wire, the first bytes of the first
/// payloads — because the codec the phone picks is not ours to choose and the
/// only way to know it is to look. <c>aac-selftest</c> asks Windows's own AAC
/// decoder whether it will take that codec's AudioSpecificConfig, which is the
/// other half: a stream nothing on this machine can decode is a stream that
/// does not exist.
///
/// <para>The climb here is the probe's own rather than <see cref="DeviceSession"/>'s,
/// because that one always starts a <em>video</em> stream and half the point is
/// to see what the phone does with an audio stream that has no video beside
/// it. The rungs are the same ones, in the same order, and the developer image
/// is mounted the same way.</para>
/// </remarks>
internal static class AudioTools
{
    private const int LockdownPort = 62078;

    /// <summary>
    /// The pause given to the display service before a stream is opened, and
    /// between the video stream and the audio stream that joins it.
    /// </summary>
    /// <remarks>
    /// None by default. Six seconds is what the first successful runs used, not
    /// what the daemon was shown to need: on 11 September 2026
    /// <c>--settle=0</c> had the phone accept the audio offer the moment the video
    /// stream was in place, and the application no longer waits either. The
    /// option stays so that the pause can be measured again on another phone.
    /// </remarks>
    public const int DefaultSettleMs = 0;

    /// <summary>The offer variants <c>audio-info</c> understands.</summary>
    public const string VariantUsage =
        "  variantes : default | f4:<n> | f2:<n> | f3:<n> | f5:<n> | f6:<n>\n"
        + "              paliers-sans-codec | paliers-codec-seuls\n"
        + "  options   : --video (video et audio dans la meme session, comme Xcode)\n"
        + "              --settle=<ms> (pause avant chaque flux, defaut 0)\n"
        + "              --press=<bouton>@<s> (appuie un bouton du chassis a la seconde s, repetable)\n"
        + "              --stop=<session|bye> (arret audio : stopmediastream, ou BYE seul ; defaut session)\n"
        + "              --hold=<s> (avec --video : garde la video s secondes apres l'arret audio et compte)\n"
        + "              --direction=<output|input>  (input : ce que le telephone repond au micro)";

    public static bool ParseVariant(string text, out AudioOfferOptions options, out string label)
    {
        options = AudioOfferOptions.Default;
        label = text;
        foreach (string piece in text.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            string token = piece.Trim().ToLowerInvariant();
            string[] parts = token.Split(':');
            switch (parts[0])
            {
                case "default": break;
                case "paliers-sans-codec": options = options with { DropCodecTiers = true }; break;
                case "paliers-codec-seuls": options = options with { CodecTiersOnly = true }; break;
                case "f2" when parts.Length == 2 && long.TryParse(parts[1], out long f2):
                    options = options with { SettingsF2 = f2 }; break;
                case "f3" when parts.Length == 2 && long.TryParse(parts[1], out long f3):
                    options = options with { SettingsF3 = f3 }; break;
                case "f4" when parts.Length == 2 && long.TryParse(parts[1], out long f4):
                    options = options with { SettingsF4 = f4 }; break;
                case "f5" when parts.Length == 2 && long.TryParse(parts[1], out long f5):
                    options = options with { SettingsF5 = f5 }; break;
                case "f6" when parts.Length == 2 && long.TryParse(parts[1], out long f6):
                    options = options with { SettingsF6 = f6 }; break;
                default: return false;
            }
        }
        return true;
    }

    /// <summary>
    /// aac-selftest — does Windows decode what the phone sends? Offline.
    /// </summary>
    /// <remarks>
    /// Green means the measurement was made, not that the answer was the one
    /// hoped for: the test fails only if the AAC-LC control case is refused
    /// too, which would mean the interop is broken rather than the codec
    /// unsupported.
    /// </remarks>
    public static int AacSelfTest(Action<string> say) => VideoTools.OnMta(() =>
    {
        bool phoneAccepted, controlAccepted;
        try
        {
            say("Types d'entree que le decodeur AAC de Windows annonce lui-meme :");
            foreach (string line in AacProbe.DescribeInputTypes())
                say($"  {line}");

            // The verdict rests on this and on nothing else: one type object,
            // handed over by the decoder itself, walked towards the type we
            // want one attribute at a time, twice — once with an AAC-LC config
            // and once with the phone's. Everything else printed here is
            // context; only the step that adds the AudioSpecificConfig changes
            // one single thing between the two runs.
            say("Bissection depuis le type que le decodeur accepte, temoin AAC-LC :");
            controlAccepted = Bisect(AacProbe.AacLcConfig, say);
            say("Bissection identique, avec la configuration du telephone (AAC-ELD) :");
            phoneAccepted = Bisect(AacProbe.PhoneConfig, say);

            say("Pour memoire, les autres formes de type essayees :");
            var attempts = AacProbe.TryAdvertisedWithConfig(
                ("telephone (AAC-ELD)", AacProbe.PhoneConfig),
                ("temoin (AAC-LC)", AacProbe.AacLcConfig));
            attempts.AddRange(AacProbe.TryInputTypes(
                ("telephone (AAC-ELD)", AacProbe.PhoneConfig),
                ("temoin (AAC-LC)", AacProbe.AacLcConfig)));
            string current = "";
            foreach (var attempt in attempts)
            {
                if (attempt.Label != current)
                {
                    current = attempt.Label;
                    say($"  {attempt.Label} : AudioSpecificConfig {Convert.ToHexString(attempt.Config)}");
                    say($"    {attempt.Parsed}");
                }
                say($"    forme « {attempt.Shape} » -> HRESULT 0x{attempt.HResult:X8} ({attempt.HResultName})"
                    + $" — {(attempt.Accepted ? "ACCEPTE" : "REFUSE")}");
            }
            say("  (le « type annonce 4 » a le sous-type 0x1600, ADTS, qui ne lit pas le USER_DATA :"
                + " son acceptation ne dit rien du codec, et la forme sans AudioSpecificConfig non plus.)");
        }
        catch (Exception exception)
        {
            say($"Decodeur AAC de Media Foundation inutilisable : {exception.Message}");
            return 8;
        }

        if (!controlAccepted)
        {
            say("*** AAC-SELFTEST : le temoin AAC-LC est refuse — c'est l'interop ou la forme du type"
                + " qui est en cause, pas le codec. ***");
            return 7;
        }
        say(phoneAccepted
            ? "*** AAC-SELFTEST VERT *** Windows accepte l'AAC-ELD du telephone : un decodeur maison est inutile."
            : "*** AAC-SELFTEST VERT *** mesure faite : dans un type identique, Windows accepte l'AAC-LC"
              + " (objet 2) et REFUSE l'AAC-ELD du telephone (objet 39).");
        return 0;
    });

    /// <summary>Prints one bisection and returns what its decisive step said.</summary>
    private static bool Bisect(byte[] config, Action<string> say)
    {
        bool accepted = false;
        foreach (var (step, hr, decisive) in AacProbe.Bisect(config))
        {
            say($"  {(decisive ? ">>" : "  ")} {step,-52} -> HRESULT 0x{hr:X8} {(hr >= 0 ? "ACCEPTE" : "REFUSE")}");
            if (decisive) accepted = hr >= 0;
        }
        return accepted;
    }

    /// <summary>
    /// audio-info [secondes] [capture.rtp] [--variant=…] [--video] [--settle=…] [--direction=…]
    /// — ouvre un flux audio et rapporte tout ce que le telephone en dit.
    /// </summary>
    public static async Task<int> RunAsync(int seconds, string? capturePath, string variant, bool withVideo,
        string direction, int settleMs, IReadOnlyList<(string Button, int AtSecond)> presses,
        string stopMode, int holdSeconds, int recycleAt, bool unpaired, string ddiFolder, Action<string> say)
    {
        if (!ParseVariant(variant, out var offer, out string label))
        {
            say($"usage : audio-info [secondes] [capture.rtp] [--variant=<variante>] [--video] [--settle=<ms>] [--direction=<output|input>]"
                + $"{Environment.NewLine}{VariantUsage}");
            return 2;
        }
        say($"Pause avant chaque flux : {settleMs} ms.");

        AudioOffer.SelfCheck();
        say("Blob audio par defaut identique au gabarit Xcode (SelfCheck OK).");
        say($"Variante « {label} » : {offer}");

        var log = new ConsoleLog();
        await using var climb = await ClimbAsync(ddiFolder, say, log);
        if (climb is null)
            return 6;

        // What the phone says it can stream, before anything is asked of it.
        // The one place a direction or a stream type would be declared if the
        // daemon declared them at all.
        await ShowAsync(climb, "getmediasupportinfo", DisplayService.GetMediaSupportInfoAsync, say);
        await ShowAsync(climb, "mediastreamstatus (avant)", DisplayService.GetMediaStreamServerStatusAsync, say);

        MediaSession? video = null;
        AudioSession? audio = null;
        using var tap = new RtpTap(capturePath);
        int code = 0;
        try
        {
            if (withVideo)
            {
                await Task.Delay(settleMs);
                video = new MediaSession(climb.Net, climb.Rsd) { Codecs = VideoCodecs.AvcOnly };
                say("Ouverture du flux video d'abord, comme le miroir de Xcode…");
                try
                {
                    await video.StartAsync(log);
                }
                catch (Exception exception)
                {
                    say($"*** SERVICE D'AFFICHAGE INDISPONIBLE *** {exception.Message}");
                    say("Remede connu : LuminaMonitor.UsbProbe unmount, puis relancer — le demontage"
                        + " de l'image developpeur redemarre les demons qu'elle porte.");
                    return 6;
                }
                if (!video.Streaming)
                {
                    say($"*** FLUX VIDEO REFUSE *** {video.Failure ?? "(aucun motif rapporte)"}");
                    return 5;
                }
                say($"Flux video en place, session client {Convert.ToHexString(video.SessionId.Bytes)} — l'audio va la partager.");
            }

            await Task.Delay(settleMs);
            audio = new AudioSession(climb.Net, climb.Rsd)
            {
                Offer = offer,
                Direction = direction,
                // Unpaired on demand: its own session id rather than the video's,
                // so stopmediastream on the audio never touches the picture, and
                // iOS may not treat it as a screen recording's audio track. The
                // video's session is still spared from the hygiene sweep, or the
                // guard would take the picture down with nothing told to keep.
                PairedSessionId = unpaired ? null : video?.SessionId,
                SpareSessionId = video?.SessionId,
            };
            if (unpaired) say("Audio NON apparie : session propre, video epargnee de la garde.");
            audio.RtpPacket += tap.Add;
            // Armed before the call: the phone sends the moment it answers, and
            // the first packets are the ones that say what the stream carries.
            tap.Arm();
            try
            {
                await audio.StartAsync(log);
            }
            catch (Exception exception)
            {
                // A display service that has stopped answering is a fault of
                // its own, not a refused offer, and it is not this command's
                // business to climb the recovery ladder — but it must not come
                // out as a stack trace either, because the stop below is what
                // hands the phone's microphone back.
                say($"*** SERVICE D'AFFICHAGE INDISPONIBLE *** {exception.Message}");
                return 6;
            }

            if (!audio.Streaming)
            {
                say($"*** OFFRE AUDIO REFUSEE (variante « {label} », direction « {direction} ») *** — erreur CoreDevice, verbatim :");
                Console.WriteLine(audio.Failure ?? "(aucun motif rapporte)");
                code = 5;
            }
            else
            {
                say($"*** OFFRE AUDIO ACCEPTEE (variante « {label} ») *** — reponse du daemon, complete :");
                Console.Write(Xpc.Dump(audio.Answer, 1));
                DumpBlobs(audio.Answer, "", say);
                DumpAnswerBlob(audio.Answer, say);
                say($"  RxPayloadType = {audio.ConfigText("RxPayloadType") ?? "(absent)"},"
                    + $" AudioStreamMode = {audio.ConfigText("AudioStreamMode") ?? "(absent)"},"
                    + $" SourcePort = {audio.ConfigText("SourcePort") ?? "(absent)"}");

                say($"Ecoute pendant {seconds} s — JOUE UN SON SUR LE TELEPHONE MAINTENANT.");
                // Chassis buttons pressed at a given second of the listening
                // window, through the same Indigo door the window uses. What
                // this measures: whether the phone's own volume — mute above
                // all — is applied before or after the point where the stream
                // is tapped. The decoded capture answers, second by second.
                RemoteXpc? indigo = null;
                for (int elapsed = 1; elapsed <= seconds; elapsed++)
                {
                    await Task.Delay(1000);
                    foreach (var (button, at) in presses)
                    {
                        if (at != elapsed) continue;
                        indigo ??= await climb.Rsd.OpenAsync(IndigoHid.ServiceName, writePatience: TimeSpan.FromSeconds(1));
                        await IndigoHid.PressAsync(indigo, button);
                        say($"  t={elapsed,3} s : bouton « {button} » presse.");
                    }
                    // The window's toggle, reproduced: at this second the audio
                    // is stopped by BYE only (its stream lingers on the phone) and
                    // reopened on the SAME session id the video holds — exactly
                    // what turning the sound off then on does. If the reopened
                    // stream carries silence while the phone plays on, the shared
                    // session is where the sound is lost.
                    if (recycleAt == elapsed)
                    {
                        say($"  t={elapsed,3} s : RECYCLAGE — arret BYE puis reouverture sur la meme session.");
                        audio.RtpPacket -= tap.Add;
                        try { await audio.StopAsync(tellDaemon: false); }
                        catch (Exception e) { say($"    arret incomplet : {e.Message}"); }
                        audio = new AudioSession(climb.Net, climb.Rsd)
                        {
                            Offer = offer,
                            Direction = direction,
                            PairedSessionId = video?.SessionId,
                        };
                        audio.RtpPacket += tap.Add;
                        try { await audio.StartAsync(log); }
                        catch (Exception e) { say($"    reouverture impossible : {e.Message}"); }
                        say($"    reouvert : {(audio.Streaming ? "flux accepte" : "REFUSE " + audio.Failure)}"
                            + $" (SourcePort {audio.ConfigText("SourcePort") ?? "?"}).");
                    }
                    var (packets, bytes, rtcp) = audio.Counts;
                    var (sent, heard) = audio.Reports;
                    if (elapsed % 5 == 0 || elapsed == seconds)
                        say($"  t={elapsed,3} s : {packets} datagramme(s) dont {rtcp} RTCP, {bytes:N0} octets,"
                            + $" RR envoyes {sent}, SR recus {heard}");
                }
                tap.Report(say);
                say($"  pas d'horodatage RTP moyen : {audio.MeanTimestampStep:F1} unites par paquet"
                    + " (480 = trame de 10 ms a 48 kHz, 512 = 10,67 ms).");
            }
        }
        finally
        {
            // The stop belongs in a finally and nowhere else: a media session
            // the phone is never told to end stays open on its side, and an
            // audio session left open is a capture session iOS does not hand
            // back — the phone's own microphone stops working for every other
            // app until something closes it.
            if (audio is not null)
            {
                say(stopMode == "bye"
                    ? "Arret du flux audio par BYE seul, sans stopmediastream (la session est partagee avec la video)…"
                    : "Arret du flux audio…");
                try { await audio.StopAsync(tellDaemon: stopMode != "bye", keepPort: holdSeconds > 0); }
                catch (Exception e) { say($"arret audio incomplet : {e.Message}"); }
            }
            // What the two streams do once the audio one has been stopped: does
            // the picture survive, does the sound really stop. The video packet
            // count and the audio port answer, second by second.
            if (video is not null && audio is not null && holdSeconds > 0)
            {
                long videoBefore = video.Stats.Packets;
                long audioBefore = audio.Counts.Packets;
                for (int held = 1; held <= holdSeconds; held++)
                {
                    await Task.Delay(1000);
                    long videoNow = video.Stats.Packets, audioNow = audio.Counts.Packets;
                    say($"  +{held,2} s : video {videoNow - videoBefore} paquet(s), audio {audioNow - audioBefore} paquet(s)");
                    videoBefore = videoNow; audioBefore = audioNow;
                }
                await ShowAsync(climb, "mediastreamstatus (audio arrete, video en place)",
                    DisplayService.GetMediaStreamServerStatusAsync, say);
            }
            if (video is not null)
            {
                await Task.Delay(500);
                say("Arret du flux video…");
                try { await video.StopAsync(); } catch (Exception e) { say($"arret video incomplet : {e.Message}"); }
            }
            await ShowAsync(climb, "mediastreamstatus (apres)", DisplayService.GetMediaStreamServerStatusAsync, say);
        }
        return code;
    }

    /// <summary>
    /// media-status [secondes] — what the phone's media-stream server believes
    /// is running; with a duration, one line a second until it changes or the
    /// time is up.
    /// </summary>
    /// <remarks>
    /// The watch form is what measures the life of a session nobody closed. A
    /// media session outlives the process that opened it, and the question that
    /// decides how bad that is — for how long — cannot be answered by one
    /// reading, nor by repeated commands: each of those costs ten seconds of
    /// climbing, which is most of the interval being measured.
    /// </remarks>
    public static async Task<int> MediaStatusAsync(string ddiFolder, Action<string> say, int watchSeconds = 0)
    {
        await using var climb = await ClimbAsync(ddiFolder, say, new ConsoleLog());
        if (climb is null)
            return 6;
        if (watchSeconds <= 0)
        {
            await ShowAsync(climb, "mediastreamstatus", DisplayService.GetMediaStreamServerStatusAsync, say);
            return 0;
        }

        say($"Surveillance de l'etat du serveur media pendant {watchSeconds} s, une ligne par seconde.");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        string previous = "";
        while (clock.Elapsed.TotalSeconds < watchSeconds)
        {
            string line;
            try
            {
                var service = await climb.Rsd.OpenAsync(DisplayService.ServiceName);
                try
                {
                    var status = await DisplayService.GetMediaStreamServerStatusAsync(service);
                    line = Summarise(status);
                }
                finally
                {
                    await service.CloseAsync(TimeSpan.FromSeconds(1));
                }
            }
            catch (Exception exception)
            {
                line = $"illisible ({exception.Message})";
            }
            if (line != previous)
            {
                say($"  t={clock.Elapsed.TotalSeconds,5:F1} s : {line}   <-- changement");
                previous = line;
            }
            else
            {
                say($"  t={clock.Elapsed.TotalSeconds,5:F1} s : {line}");
            }
            await Task.Delay(1000);
        }
        return 0;
    }

    /// <summary>The status answer in one line: is anything running, since when, and of what type.</summary>
    private static string Summarise(Dictionary<string, object?> status)
    {
        string running = Text(status, "running") ?? "?";
        string duration = Text(status, "runDurationSeconds") ?? "?";
        var types = new List<string>();
        if (status.GetValueOrDefault("sessions") is System.Collections.IEnumerable sessions and not string)
            foreach (object? session in sessions)
                if (session is IDictionary<string, object?> map)
                    types.Add(Find(map, "type")?.ToString() ?? "?");
        return $"running={running}, duree={duration} s, sessions=[{string.Join(", ", types)}]";
    }

    private static string? Text(IDictionary<string, object?> map, string key) =>
        Find(map, key) switch
        {
            null => null,
            bool b => b ? "true" : "false",
            LuminaMonitor.Core.RemoteXpc.XpcUInt64 u => u.Value.ToString(),
            LuminaMonitor.Core.RemoteXpc.XpcInt64 i => i.Value.ToString(),
            { } other => other.ToString(),
        };

    private static object? Find(object? node, string key)
    {
        switch (node)
        {
            case IDictionary<string, object?> map:
                if (map.TryGetValue(key, out object? found))
                    return found;
                foreach (object? value in map.Values)
                    if (Find(value, key) is { } hit)
                        return hit;
                return null;
            case System.Collections.IEnumerable list and not string:
                foreach (object? item in list)
                    if (Find(item, key) is { } hit)
                        return hit;
                return null;
            default:
                return null;
        }
    }

    /// <summary>
    /// media-release — closes every media session the phone still believes is
    /// running, ours or a previous run's.
    /// </summary>
    /// <remarks>
    /// The repair for the symptom that made this exist: after a run killed
    /// rather than closed — <c>Stop-Process</c>, a crash, a cable pulled — the
    /// phone keeps the session, and with an audio stream in it iOS keeps the
    /// capture session that goes with it. The microphone is then unavailable to
    /// every other app on the phone until something ends the session.
    /// </remarks>
    public static async Task<int> MediaReleaseAsync(string ddiFolder, Action<string> say)
    {
        await using var climb = await ClimbAsync(ddiFolder, say, new ConsoleLog());
        if (climb is null)
            return 6;
        await ShowAsync(climb, "mediastreamstatus (avant)", DisplayService.GetMediaStreamServerStatusAsync, say);
        var display = await climb.Rsd.OpenAsync(DisplayService.ServiceName);
        int released;
        try
        {
            released = await MediaHygiene.ReleaseOrphansAsync(display, line => say(line));
        }
        finally
        {
            await display.CloseAsync(TimeSpan.FromSeconds(1));
        }
        say(released == 0 ? "Aucune session media a liberer." : $"*** {released} SESSION(S) MEDIA LIBEREE(S) ***");
        await Task.Delay(1000);
        await ShowAsync(climb, "mediastreamstatus (apres)", DisplayService.GetMediaStreamServerStatusAsync, say);
        return 0;
    }

    /// <summary>
    /// audio-leak-test — opens an audio stream and deliberately does not close
    /// it, so that the ghost session can be observed.
    /// </summary>
    /// <remarks>
    /// The only honest way to reproduce what a killed run leaves on the phone.
    /// Launch it, kill it — the probe process, not the application someone is using — and
    /// then read <c>media-status</c>: whatever is still listed is what a crash
    /// costs, and on the audio side it is the microphone the person cannot use
    /// afterwards. The stream is left open on purpose, so this command is a
    /// tool for exactly one measurement and nothing else.
    /// </remarks>
    public static async Task<int> LeakAsync(string ddiFolder, Action<string> say)
    {
        var log = new ConsoleLog();
        var climb = await ClimbAsync(ddiFolder, say, log);
        if (climb is null)
            return 6;
        var audio = new AudioSession(climb.Net, climb.Rsd);
        await audio.StartAsync(log);
        if (!audio.Streaming)
        {
            say($"*** OFFRE AUDIO REFUSEE *** {audio.Failure ?? "(aucun motif rapporte)"}");
            await climb.DisposeAsync();
            return 5;
        }
        say("Flux audio ouvert et VOLONTAIREMENT NON FERME.");
        say("Tue ce processus maintenant (Stop-Process sur LuminaMonitor.UsbProbe), puis lance media-status.");
        say("Sans intervention, il s'arrete de lui-meme dans 120 s — proprement, pour ne rien laisser.");
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(120));
        }
        finally
        {
            // Reached only if nobody killed the process: the point of the
            // exercise is the kill, but a command that leaks by accident when
            // the measurement was forgotten would be the very bug being hunted.
            say("Personne n'a tue le processus : arret propre du flux audio.");
            try { await audio.StopAsync(); } catch (Exception) { }
            await climb.DisposeAsync();
        }
        return 0;
    }

    /// <summary>Runs one read-only display-service call on its own channel and prints the answer whole.</summary>
    private static async Task ShowAsync(Climb climb, string what,
        Func<LuminaMonitor.Core.RemoteXpc.RemoteXpc, Task<Dictionary<string, object?>>> call, Action<string> say)
    {
        try
        {
            var service = await climb.Rsd.OpenAsync(DisplayService.ServiceName);
            try
            {
                var answer = await call(service);
                say($"=== {what}, reponse complete ===");
                Console.Write(Xpc.Dump(answer, 1));
                DumpBlobs(answer, "", say);
            }
            finally
            {
                await service.CloseAsync(TimeSpan.FromSeconds(1));
            }
        }
        catch (Exception exception)
        {
            say($"{what} indisponible : {exception.Message}");
        }
    }

    // --- The climb, once ----------------------------------------------------------

    /// <summary>Everything the audio commands need above the cable, and the pipes that hold it up.</summary>
    private sealed class Climb : IAsyncDisposable
    {
        public required UsbmuxClient Mux { get; init; }
        public required UsbmuxClient LockdownPipe { get; init; }
        public required UsbmuxClient ServicePipe { get; init; }
        public required TunnelNet Net { get; init; }
        public required Rsd Rsd { get; init; }

        public async ValueTask DisposeAsync()
        {
            try { await Net.DisposeAsync(); } catch (Exception) { }
            ServicePipe.Dispose();
            LockdownPipe.Dispose();
            Mux.Dispose();
        }
    }

    /// <summary>
    /// Climbs to the service directory: the probe's own copy of the rungs
    /// <see cref="DeviceSession"/> climbs, minus the video stream.
    /// </summary>
    /// <remarks>
    /// Not <see cref="DeviceSession"/> because that one always starts a video
    /// stream and opens the HID channel, and half of what these commands
    /// measure is what the phone does with an audio stream that has no video
    /// beside it. Returns null, having said why, when a rung refuses.
    /// </remarks>
    /// <summary>
    /// audio-listen [secondes] — the phone's sound in the headphones, live, with
    /// no mirror.
    /// </summary>
    /// <remarks>
    /// The one configuration measured to carry content a display stream makes the
    /// phone withhold. With the mirror up, iOS treats the media session as a
    /// screen recording and a protected app — Apple Music above all — stops
    /// feeding the capture; without it, the very same CoreDevice audio stream
    /// carries that app in full (measured 11 September 2026, and the decoded
    /// capture plays). So this opens the audio stream <b>alone</b>, on its own
    /// session, and renders it to the Windows output: proof that the sound does
    /// cross the cable on Apple's own path, and a usable way to listen to the
    /// phone on this machine's speakers when the picture is not wanted.
    /// </remarks>
    public static async Task<int> ListenAsync(int seconds, string? deviceId, int mirrorAt,
        string ddiFolder, Action<string> say)
    {
        var log = new ConsoleLog();
        await using var climb = await ClimbAsync(ddiFolder, say, log);
        if (climb is null)
            return 6;

        var options = AudioOptions.Default with { DeviceId = deviceId };
        MediaSession? video = null;
        // Its own session id, and no video open yet: the sound is established
        // first, which is the whole point of the command.
        var stream = new AudioStream(climb.Net, climb.Rsd,
            new XpcUuid(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)),
            options, log, () => 0);
        try
        {
            await stream.StartAsync(new ConsoleProgress(say));
            if (!stream.Streaming)
            {
                say($"*** FLUX AUDIO REFUSE *** {stream.Failure ?? "(aucun motif rapporte)"}");
                return 5;
            }
            say($"*** ECOUTE {seconds} s *** joue ce que tu veux sur le telephone — Apple Music compris."
                + (mirrorAt > 0 ? $" Le MIROIR s'ouvrira a t={mirrorAt} s, l'audio deja etabli." : " Sans miroir."));
            for (int elapsed = 1; elapsed <= seconds; elapsed++)
            {
                await Task.Delay(1000);
                // The order that has never been tried: the sound is already
                // flowing, and only then is the picture asked for — with the
                // audio session named as one to spare, so the video's own orphan
                // guard does not close it the way it did on 11 September 2026.
                if (mirrorAt == elapsed)
                {
                    say($"  t={elapsed,3} s : OUVERTURE DU MIROIR (audio deja etabli, sa session epargnee)…");
                    video = new MediaSession(climb.Net, climb.Rsd)
                    {
                        Codecs = VideoCodecs.AvcOnly,
                        SpareSessionId = stream.SessionId,
                    };
                    try { await video.StartAsync(log); }
                    catch (Exception e) { say($"    miroir impossible : {e.Message}"); }
                    say($"    miroir : {(video.Streaming ? "flux video en place" : "REFUSE " + video.Failure)}");
                }
                var stats = stream.Stats;
                if (elapsed % 2 == 0 || elapsed == seconds)
                    say($"  t={elapsed,3} s : {stats.Packets} trames, niveau {stats.LevelText},"
                        + $" file {stats.QueuedMs:F0} ms, sous-alim {stats.Underruns}"
                        + (video is { Streaming: true } ? $", video {video.Stats.Packets} paq" : ""));
            }
        }
        finally
        {
            say("Arret du flux audio…");
            try { await stream.StopAsync(); } catch (Exception e) { say($"arret incomplet : {e.Message}"); }
            if (video is not null)
            {
                say("Arret du flux video…");
                try { await video.StopAsync(); } catch (Exception e) { say($"arret video incomplet : {e.Message}"); }
            }
        }
        return 0;
    }

    /// <summary>Reports a stream's progress lines to the console.</summary>
    private sealed class ConsoleProgress(Action<string> say) : IProgress<string>
    {
        public void Report(string value) => say(value);
    }

    private static async Task<Climb?> ClimbAsync(string ddiFolder, Action<string> say, ConsoleLog log)
    {
        UsbmuxClient mux;
        try
        {
            mux = await UsbmuxClient.ConnectAsync();
        }
        catch (LuminaException exception)
        {
            // The multiplexer's two ways of being out of reach are told apart in
            // the message itself; a stack trace on top of it says nothing more.
            say($"*** MULTIPLEXEUR APPLE *** {exception.Message}");
            return null;
        }
        var lockdownPipe = await UsbmuxClient.ConnectAsync();
        UsbmuxClient? servicePipe = null;
        TunnelNet? net = null;
        bool handedOver = false;
        try
        {
            long deviceId;
            string udid;
            try
            {
                (deviceId, udid) = await FirstDeviceAsync(mux, say);
            }
            catch (InvalidOperationException exception)
            {
                // An unplugged cable is the ordinary case, not a fault: the
                // multiplexer answers, it simply has nothing to offer. A stack
                // trace for that tells nobody to plug the phone back in.
                say($"{exception.Message} Branche l'iPhone en USB et deverrouille-le.");
                return null;
            }
            var record = await ReadPairRecordAsync(mux, udid, say);
            var lockdown = new LockdownClient(await lockdownPipe.ConnectToDeviceAsync(deviceId, LockdownPort));
            string? refusal = await lockdown.StartSessionAsync(record);
            if (refusal is not null)
            {
                say($"StartSession refuse : {refusal}");
                return null;
            }

            var manager = new DdiManager(deviceId, record);
            manager.UnlockRequired += message => say(message);
            var status = await manager.EnsureMountedAsync(new DdiSource(ddiFolder), log);
            if (status is not (DdiStatus.AlreadyMounted or DdiStatus.Mounted))
            {
                say($"Image developpeur indisponible : {status}.");
                return null;
            }

            var (port, ssl) = await lockdown.StartServiceAsync(CdTunnel.ServiceName);
            servicePipe = await UsbmuxClient.ConnectAsync();
            Stream service = await servicePipe.ConnectToDeviceAsync(deviceId, port);
            if (ssl) service = await PlistService.WrapTlsAsync(service, record.HostCertificate);
            var tunnel = new CdTunnel(service);
            var handshake = await tunnel.EstablishAsync();
            var pipe = servicePipe;
            net = new TunnelNet(tunnel, handshake) { Log = say, SocketAvailable = () => pipe.Available };
            net.Start();
            var rsd = new Rsd(net);
            await rsd.LoadAsync(handshake.ServerRsdPort);
            say($"Annuaire : {rsd.Services.Count} services.");
            if (!rsd.Services.ContainsKey(DisplayService.ServiceName))
            {
                say($"Service d'affichage absent de l'annuaire : {DisplayService.ServiceName}");
                return null;
            }
            handedOver = true;
            return new Climb
            {
                Mux = mux, LockdownPipe = lockdownPipe, ServicePipe = servicePipe, Net = net, Rsd = rsd,
            };
        }
        finally
        {
            // Either the Climb owns them all, or none of them survives this
            // method: a half-built climb that leaves a tunnel running is a
            // media session nobody can stop afterwards.
            if (!handedOver)
            {
                if (net is not null) { try { await net.DisposeAsync(); } catch (Exception) { } }
                servicePipe?.Dispose();
                lockdownPipe.Dispose();
                mux.Dispose();
            }
        }
    }

    // --- The rungs below the tunnel, the probe's own copy -------------------------

    private static async Task<(long DeviceId, string Udid)> FirstDeviceAsync(UsbmuxClient mux, Action<string> say)
    {
        var reply = await mux.RequestAsync("ListDevices");
        if (reply.GetValueOrDefault("DeviceList") is not List<object> devices || devices.Count == 0)
            throw new InvalidOperationException("Aucun appareil attache.");
        var first = (Dictionary<string, object>)devices[0];
        long deviceId = (long)first["DeviceID"];
        var properties = first.GetValueOrDefault("Properties") as Dictionary<string, object>;
        string udid = properties?.GetValueOrDefault("SerialNumber")?.ToString()
            ?? throw new InvalidOperationException("Appareil sans UDID.");
        say($"Appareil usbmux #{deviceId}, UDID {DeviceInfo.Mask(udid)}.");
        return (deviceId, udid);
    }

    private static async Task<PairRecord> ReadPairRecordAsync(UsbmuxClient mux, string udid, Action<string> say)
    {
        var reply = await mux.RequestAsync("ReadPairRecord",
            new Dictionary<string, object> { ["PairRecordID"] = udid });
        if (reply.GetValueOrDefault("PairRecordData") is not byte[] bytes)
            throw new InvalidOperationException("Pas d'enregistrement d'appairage pour cet appareil.");
        var dict = Plist.Read(bytes) as Dictionary<string, object>
            ?? throw new InvalidDataException("enregistrement d'appairage illisible");
        say($"Enregistrement d'appairage charge (HostID {dict["HostID"]}).");
        return PairRecord.Parse(dict);
    }

    // --- Reading the answer back ---------------------------------------------------

    /// <summary>Every byte[] in a reply, in full hex: the codec is decided inside one of them.</summary>
    private static void DumpBlobs(object? value, string path, Action<string> say)
    {
        const int MaxHexBytes = 4096;
        switch (value)
        {
            case IDictionary<string, object?> dict:
                foreach (var (key, item) in dict)
                    DumpBlobs(item, path.Length == 0 ? key : path + "." + key, say);
                break;
            case IList<object?> list:
                for (int i = 0; i < list.Count; i++)
                    DumpBlobs(list[i], $"{path}[{i}]", say);
                break;
            case byte[] blob:
                say($"  blob {path} : {blob.Length} octets"
                    + (blob.Length > MaxHexBytes ? $" (premiers {MaxHexBytes} en hexadecimal)" : " en hexadecimal"));
                int shown = Math.Min(blob.Length, MaxHexBytes);
                for (int at = 0; at < shown; at += 32)
                    say($"    {at:X4}  {Convert.ToHexString(blob, at, Math.Min(32, shown - at))}");
                break;
        }
    }

    /// <summary>
    /// The phone's own media blob, inflated and read as protobuf.
    /// </summary>
    /// <remarks>
    /// The answer carries a <c>negotiatorAnswer</c> built the way our offer is:
    /// a binary plist holding a zlib-compressed protobuf. There is no bplist
    /// reader in this project and writing one to read a diagnostic would be a
    /// detour, so the compressed stream is found by its zlib header and
    /// inflated; the fields are then printed by number, because the audio
    /// settings message's field names have never been recovered and inventing
    /// them would be worse than printing numbers.
    /// </remarks>
    private static void DumpAnswerBlob(object? answer, Action<string> say)
    {
        foreach (byte[] blob in Blobs(answer))
        {
            for (int at = 0; at + 1 < blob.Length; at++)
            {
                if (blob[at] != 0x78 || blob[at + 1] is not (0x01 or 0x9C or 0xDA))
                    continue;
                byte[]? inflated = OfferWire.Inflate(blob.AsSpan(at));
                if (inflated is null)
                    continue;
                say($"  blob compresse trouve a l'octet {at} : {inflated.Length} octets une fois decompresses");
                say($"    {Convert.ToHexString(inflated)}");
                DumpProtobuf(inflated, "    ", say);
                break;
            }
        }
    }

    private static IEnumerable<byte[]> Blobs(object? value)
    {
        switch (value)
        {
            case IDictionary<string, object?> dict:
                foreach (object? item in dict.Values)
                    foreach (byte[] found in Blobs(item))
                        yield return found;
                break;
            case IList<object?> list:
                foreach (object? item in list)
                    foreach (byte[] found in Blobs(item))
                        yield return found;
                break;
            case byte[] blob:
                yield return blob;
                break;
        }
    }

    /// <summary>Prints protobuf fields by number, one line each, nesting into what parses.</summary>
    private static void DumpProtobuf(ReadOnlySpan<byte> data, string indent, Action<string> say, int depth = 0)
    {
        int at = 0;
        while (at < data.Length)
        {
            if (!Varint(data, ref at, out ulong tag)) return;
            int field = (int)(tag >> 3);
            int wire = (int)(tag & 7);
            switch (wire)
            {
                case 0:
                    if (!Varint(data, ref at, out ulong value)) return;
                    say($"{indent}f{field} = {value}");
                    break;
                case 2:
                    if (!Varint(data, ref at, out ulong length) || at + (int)length > data.Length) return;
                    var body = data.Slice(at, (int)length);
                    at += (int)length;
                    bool printable = body.Length > 0 && body.ToArray().All(b => b >= 0x20 && b < 0x7F);
                    if (printable)
                        say($"{indent}f{field} = \"{System.Text.Encoding.ASCII.GetString(body)}\"");
                    else
                    {
                        say($"{indent}f{field} : {body.Length} octets");
                        if (depth < 4) DumpProtobuf(body, indent + "  ", say, depth + 1);
                    }
                    break;
                case 5: at += 4; break;
                case 1: at += 8; break;
                default: return;
            }
        }
    }

    private static bool Varint(ReadOnlySpan<byte> data, ref int at, out ulong value)
    {
        value = 0;
        int shift = 0;
        while (at < data.Length && shift < 64)
        {
            byte b = data[at++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;
            shift += 7;
        }
        return false;
    }
}
