using System.Diagnostics;
using System.Runtime.InteropServices;
using LuminaMonitor.Core.Audio;
using LuminaMonitor.Core.Audio.WinRt;

/// <summary>
/// The Bluetooth half of the audio question: the phone's sound played by
/// Windows as an A2DP receiver, and the hands-free endpoints a call would use.
/// No multiplexer, no cable: these commands talk to Windows only.
/// </summary>
internal static class BluetoothAudioTools
{
    /// <summary>
    /// winrt-selftest — the hand-written WinRT interop, checked without a phone:
    /// the parameterized IIDs recomputed from their signatures, both activation
    /// factories reached, the selector read, one FindAllAsync waited for
    /// (which QueryInterfaces for two of the computed IIDs).
    /// </summary>
    public static async Task<int> WinRtSelfTestAsync(Action<string> say)
    {
        int failures = 0;
        void Expect(string what, Guid computed, Guid declared)
        {
            bool ok = computed == declared;
            if (!ok) failures++;
            say($"  {(ok ? "OK    " : "ECHEC ")} {what} : calcule {computed}, declare {declared}");
        }

        say("IID parametres, recalcules depuis leur signature (UUID v5, espace 11f47ad5-…) :");
        Expect("IAsyncOperation<AudioPlaybackConnectionOpenResult>",
            WinRtIid.FromSignature(WinRtIid.OpenResultOperation), typeof(IAsyncOperationOfOpenResult).GUID);
        Expect("IVectorView<DeviceInformation>",
            WinRtIid.FromSignature(WinRtIid.DeviceInformationVector), typeof(IDeviceInformationVectorView).GUID);
        Expect("IAsyncOperation<DeviceInformationCollection>",
            WinRtIid.FromSignature(WinRtIid.DeviceInformationCollectionOperation),
            typeof(IAsyncOperationOfDeviceInformationCollection).GUID);
        // The one value published in the SDK headers, checked against the rule itself.
        Expect("controle SDK (__FIAsyncOperation_1_…DeviceInformationCollection)",
            WinRtIid.FromSignature(WinRtIid.DeviceInformationCollectionOperation),
            new Guid("45180254-082e-5274-b2e7-ac0517f44d07"));

        if (!BluetoothAudio.IsSupported)
        {
            say("AudioPlaybackConnection absent : Windows anterieur a 10 2004.");
            return 7;
        }
        try
        {
            string selector = await Task.Run(BluetoothAudio.DeviceSelector);
            say("Fabrique AudioPlaybackConnection atteinte (IAudioPlaybackConnectionStatics accepte).");
            say($"Selecteur : {selector}");
            var phones = await BluetoothAudio.ListPhonesAsync();
            say($"FindAllAsync termine par scrutation : {phones.Count} source(s) A2DP (IAsyncOperation<…> et IVectorView<…> acceptes).");
        }
        catch (Exception exception)
        {
            failures++;
            say($"ECHEC de l'interop WinRT : {exception.GetType().Name} : {exception.Message}");
        }
        say(failures == 0 ? "winrt-selftest : vert." : $"winrt-selftest : {failures} echec(s).");
        return failures == 0 ? 0 : 7;
    }

    /// <summary>
    /// audio-endpoints [--all] [--props] — every Windows audio endpoint, with
    /// its state, default roles and Bluetooth role; with --all the inactive
    /// ones too (that is where the hands-free endpoints hide outside a call);
    /// with --props every property of the Bluetooth ones and of the active ones.
    /// </summary>
    public static int Endpoints(string[] args, Action<string> say)
    {
        bool all = args.Contains("--all");
        bool props = args.Contains("--props");
        var endpoints = AudioEndpoints.List(null, all);
        foreach (var group in endpoints.GroupBy(e => e.Flow))
        {
            say(group.Key == AudioFlow.Output ? "=== Sorties (rendu) ===" : "=== Entrees (capture) ===");
            foreach (var e in group)
            {
                string flags = (e.IsDefault ? " [defaut]" : "") + (e.IsDefaultForCommunications ? " [communications]" : "");
                string role = e.BluetoothRole == BluetoothAudioRole.None ? "" : $" <Bluetooth {e.BluetoothRole}>";
                say($"  {e.State,-10} {e.Name}{flags}{role}");
                say($"             appareil : {e.DeviceName} — id {e.Id}");
                if (props && (e.BluetoothRole != BluetoothAudioRole.None || e.IsActive))
                    foreach (string line in AudioEndpoints.DumpProperties(e.Id))
                        say($"               {line}");
                if (e.Flow == AudioFlow.Output && e.IsActive)
                    foreach (var s in AudioEndpoints.Sessions(e.Id))
                        say($"             session pid {s.ProcessId} {s.ProcessName} {(s.IsActive ? "ACTIVE" : "inactive")}"
                            + $" crete {s.Peak:F3} « {s.DisplayName} »");
            }
        }
        return 0;
    }

    /// <summary>
    /// phone-audio [secondes=30] [nom] — lists the A2DP sources, opens the
    /// connection to the phone (the first one, or the one whose name contains
    /// <c>nom</c>), then prints once a second the link state, the peak on the
    /// default output and every active session there, for N seconds. The
    /// endpoints are listed before and after opening, to see what Windows adds.
    /// </summary>
    public static async Task<int> PhoneAudioAsync(string[] args, Action<string> say)
    {
        int seconds = args.Length > 1 && int.TryParse(args[1], out int s) ? Math.Clamp(s, 1, 3600) : 30;
        string? wanted = args.Length > 2 ? args[2] : null;

        say($"Selecteur A2DP : {await Task.Run(BluetoothAudio.DeviceSelector)}");
        var phones = await BluetoothAudio.ListPhonesAsync();
        say($"{phones.Count} source(s) audio Bluetooth :");
        foreach (var p in phones)
            say($"  « {p.Name} » {(p.IsEnabled ? "active" : "desactivee")} — {p.Id}");
        var phone = phones.FirstOrDefault(p => wanted is null || p.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase));
        if (phone is null)
        {
            say("Aucun telephone a ouvrir : appairer l'iPhone en Bluetooth avec ce PC.");
            return 4;
        }

        var output = await Task.Run(() => AudioEndpoints.Default(AudioFlow.Output));
        say($"Sortie par defaut avant ouverture : {output?.Name ?? "(aucune)"}");
        var endpoints = await Task.Run(() => AudioEndpoints.List(null, true));
        var sessions = await Task.Run(SessionKeys);
        var initial = sessions.Keys.ToHashSet();
        say($"{endpoints.Count} point(s) de terminaison, {sessions.Count} session(s) de rendu avant ouverture"
            + $" (dont {endpoints.Count(e => e.BluetoothRole != BluetoothAudioRole.None)} Bluetooth).");

        var clock = Stopwatch.StartNew();
        say($"Ouverture vers « {phone.Name} »…");
        using var link = await BluetoothAudio.StartListeningAsync(phone.Id, phone.Name);
        link.StateChanged += (_, _) => say($"  [evenement] etat -> {link.State} — {link.Description}");
        say($"Resultat en {clock.ElapsedMilliseconds} ms : {link.State}, refus {link.Refusal}"
            + $"{(link.ErrorCode is int c ? $", HRESULT 0x{c:X8}" : "")}");
        say($"  texte : {link.Description}");
        if (link.State != PhoneAudioState.Open)
            say("  La connexion reste a l'ecoute : sur l'iPhone, Reglages > Bluetooth > toucher ce PC la fera passer a Open.");

        // Once a second: what appeared or changed among the endpoints and the
        // render sessions since the tick before, and the peak of every output
        // that carries something. New sessions are the telling part: they say
        // which process Windows renders the phone in, and on which output.
        int ownPid = Environment.ProcessId;
        float maxPeak = 0;
        int signalSeconds = 0;
        for (int second = 1; second <= seconds; second++)
        {
            await Task.Delay(1000);
            var now = await Task.Run(() => AudioEndpoints.List(null, true));
            foreach (var e in now)
            {
                var old = endpoints.FirstOrDefault(b => b.Id == e.Id);
                if (old is null) say($"  NOUVEAU point de terminaison : {e.Flow} {e.State} « {e.Name} » <{e.BluetoothRole}> {e.Id}");
                else if (old.State != e.State) say($"  {e.Flow} « {e.Name} » : {old.State} -> {e.State}");
            }
            foreach (var b in endpoints.Where(b => now.All(a => a.Id != b.Id)))
                say($"  DISPARU : {b.Flow} « {b.Name} »");
            endpoints = now;

            var current = await Task.Run(SessionKeys);
            foreach (var (key, (endpoint, session)) in current)
            {
                if (sessions.ContainsKey(key)) continue;
                string whose = session.ProcessId == ownPid ? " <- DANS CE PROCESSUS : le reglage par application s'applique" : "";
                say($"  NOUVELLE session sur « {endpoint.Name} » : pid {session.ProcessId} {session.ProcessName}"
                    + $" {(session.IsActive ? "ACTIVE" : "inactive")} {Tail(session.Identifier)}{whose}");
            }
            sessions = current;

            var peaks = await Task.Run(() => now
                .Where(e => e.Flow == AudioFlow.Output && e.IsActive)
                .Select(e => (e, peak: AudioEndpoints.Peak(e.Id) ?? 0))
                .Where(x => x.peak > 0.0005f)
                .ToList());
            var bluetoothInputs = await Task.Run(() => now
                .Where(e => e.Flow == AudioFlow.Input && e.IsActive && e.BluetoothRole != BluetoothAudioRole.None)
                .Select(e => (e, peak: AudioEndpoints.Peak(e.Id) ?? 0))
                .ToList());
            // The sessions born after the open — this process's, or a service's —
            // are the only ones that can carry the phone; the others were
            // already playing (a browser, a chat) and would only add noise.
            var fresh = current.Where(x => !initial.Contains(x.Key) || x.Value.Session.ProcessId == ownPid)
                .Select(x => x.Value).ToList();

            float defaultPeak = peaks.Where(x => x.e.IsDefault).Select(x => x.peak).DefaultIfEmpty(0).First();
            float freshPeak = fresh.Select(x => x.Session.Peak).DefaultIfEmpty(0).Max();
            if (freshPeak > 0.001f) signalSeconds++;
            maxPeak = Math.Max(maxPeak, freshPeak);
            say($"  t+{second,3} s  etat {link.State,-8} crete sortie par defaut {defaultPeak:F3}"
                + (fresh.Count > 0 ? $", crete des sessions nees apres l'ouverture {freshPeak:F3}" : "")
                + string.Concat(peaks.Where(x => !x.e.IsDefault).Select(x => $", « {x.e.Name} » {x.peak:F3}"))
                + string.Concat(bluetoothInputs.Select(x => $", [entree Bluetooth « {x.e.Name} » {x.peak:F3}]")));
        }

        say($"Bilan : etat final {link.State}, {signalSeconds} seconde(s) sur {seconds} avec du signal dans une"
            + $" session nee apres l'ouverture (crete max {maxPeak:F3}).");
        link.Dispose();
        say($"Connexion fermee : {link.State} — « {link.Description} »");
        return link.Refusal == PhoneAudioRefusal.None ? 0 : 5;
    }

    /// <summary>
    /// bridge-selftest [secondes=3] — the call bridge's two WASAPI pumps
    /// between this PC's default microphone and default output, at gain zero:
    /// every stage runs (format conversion both ways, event-driven capture,
    /// bounded render queue) and nothing is heard. No phone involved.
    /// </summary>
    public static int BridgeSelfTest(string[] args, Action<string> say)
    {
        int seconds = args.Length > 1 && int.TryParse(args[1], out int s) ? Math.Clamp(s, 1, 60) : 3;
        var input = AudioEndpoints.Default(AudioFlow.Input);
        var output = AudioEndpoints.Default(AudioFlow.Output);
        if (input is null || output is null)
        {
            say("Il faut un micro et une sortie par defaut actifs.");
            return 6;
        }
        say($"Pompes : « {input.Name} » -> « {output.Name} », deux fois, gain 0 (silence).");
        try
        {
            using var bridge = CallAudioBridge.Start(input.Id, output.Id, input.Id, output.Id, 0f);
            Thread.Sleep(seconds * 1000);
            var (upMoved, upDropped) = bridge.ToPhoneFrames;
            var (downMoved, downDropped) = bridge.FromPhoneFrames;
            bool ok = bridge.IsRunning && upMoved > 0 && downMoved > 0;
            say($"  sens 1 : {upMoved} trames copiees, {upDropped} jetees ; sens 2 : {downMoved} copiees, {downDropped} jetees"
                + $" (attendu ~{48_000 * seconds} par sens a 48 kHz).");
            say(ok ? "bridge-selftest : vert." : "bridge-selftest : ECHEC (aucune trame, ou une pompe arretee).");
            return ok ? 0 : 6;
        }
        catch (Exception exception)
        {
            say($"bridge-selftest : ECHEC a l'ouverture : {exception.Message}");
            return 6;
        }
    }

    /// <summary>
    /// call-bridge [secondes=30] [micro] [sortie] — the phone's call through
    /// the PC, for real: needs the phone connected over Bluetooth and, to be
    /// meaningful, a call whose audio the iPhone routes to this PC. Without the
    /// hands-free endpoints it says so and stops: it never opens them outside
    /// that request.
    /// </summary>
    public static int CallBridge(string[] args, Action<string> say)
    {
        int seconds = args.Length > 1 && int.TryParse(args[1], out int s) ? Math.Clamp(s, 1, 3600) : 30;
        string? micName = args.Length > 2 ? args[2] : null;
        string? outName = args.Length > 3 ? args[3] : null;

        var phone = CallAudioBridge.FindPhoneEndpoints();
        if (phone is null)
        {
            say("Pas de points de terminaison mains-libres actifs : le telephone n'est pas connecte en Bluetooth.");
            foreach (var e in AudioEndpoints.List(null, true).Where(x => x.BluetoothRole != BluetoothAudioRole.None))
                say($"  (inactif) {e.Flow} {e.State} « {e.Name} » <{e.BluetoothRole}>");
            say("Sur l'iPhone : Reglages > Bluetooth > toucher ce PC, puis relancer pendant un appel.");
            return 4;
        }
        var inputs = AudioEndpoints.List(AudioFlow.Input).Where(e => e.BluetoothRole == BluetoothAudioRole.None).ToList();
        var outputs = AudioEndpoints.List(AudioFlow.Output).Where(e => e.BluetoothRole == BluetoothAudioRole.None).ToList();
        var mic = inputs.FirstOrDefault(e => micName is not null && e.Name.Contains(micName, StringComparison.OrdinalIgnoreCase))
            ?? inputs.FirstOrDefault(e => e.IsDefault);
        var output = outputs.FirstOrDefault(e => outName is not null && e.Name.Contains(outName, StringComparison.OrdinalIgnoreCase))
            ?? outputs.FirstOrDefault(e => e.IsDefault);
        if (mic is null || output is null)
        {
            say("Micro ou sortie introuvable.");
            return 6;
        }
        say($"Voix du telephone : « {phone.FromPhone.Name} » ({phone.FromPhone.State}) -> « {output.Name} »");
        say($"Notre voix        : « {mic.Name} » -> « {phone.ToPhone.Name} » ({phone.ToPhone.State})");
        using var bridge = CallAudioBridge.Start(mic.Id, output.Id, phone);
        bridge.Stopped += (_, _) => say($"  [pont arrete] {bridge.StopReason}");
        for (int second = 1; second <= seconds && bridge.StopReason is null; second++)
        {
            Thread.Sleep(1000);
            float fromPeak = AudioEndpoints.Peak(phone.FromPhone.Id) ?? 0;
            float toPeak = AudioEndpoints.Peak(phone.ToPhone.Id) ?? 0;
            say($"  t+{second,3} s  voix du telephone crete {fromPeak:F3} ({bridge.FromPhoneFrames.Moved} trames),"
                + $" vers le telephone crete {toPeak:F3} ({bridge.ToPhoneFrames.Moved} trames)");
        }
        say("Pont ferme : le telephone reprend son micro.");
        return 0;
    }

    /// <summary>Every render session on every active output, keyed by endpoint and session instance.</summary>
    private static Dictionary<string, (AudioEndpoint Endpoint, AudioSessionInfo Session)> SessionKeys()
    {
        var result = new Dictionary<string, (AudioEndpoint, AudioSessionInfo)>();
        foreach (var e in AudioEndpoints.List(AudioFlow.Output))
            foreach (var s in AudioEndpoints.Sessions(e.Id))
                result[$"{e.Id}|{s.ProcessId}|{s.Identifier}"] = (e, s);
        return result;
    }

    private static string Tail(string text) => text.Length <= 70 ? text : "…" + text[^69..];
}
