using LuminaMonitor.Core;

namespace LuminaMonitor.App;

/// <summary>
/// The window in French — word for word what it said before it had a second
/// language. Numbers follow the Windows culture, as they always did.
/// </summary>
internal sealed class FrenchTexts : Texts
{
    public override string CountersTooltip => "Compteurs (F3)";

    public override string PillLocked => "verrouillé";
    public override string PillFps(double fps, bool driving) =>
        driving ? $"pilotage · {fps:0} i/s" : $"{fps:0} i/s";
    public override string PillNoKeyboard => "sans clavier";
    public override string PillStreamStopped => "flux arrêté";
    public override string PillWaiting => "en attente";
    public override string PillRestarting => "relance…";
    public override string PillError => "erreur";
    public override string PillOffline => "hors ligne";

    public override string MuteTooltip =>
        "Muet : coupe et rétablit le son du téléphone. Mesuré le 9 septembre : ce qui part est la touche Muet " +
        "du clavier média, pas le bouton Action — celui-ci n'est pas atteignable par ce protocole.";
    public override string SideButtonTooltip =>
        "Bouton latéral : éteint l'écran, et le rallume quand il est éteint. Le déverrouillage lui-même reste " +
        "Face ID ou le code — l'app ne peut taper le code que si tu l'as écrit dans unlockCode.";
    public override string VolumeUp => "Volume +";
    public override string VolumeDown => "Volume −";
    public override string Mute => "Muet";
    public override string NoSessionForButton => "Pas de session : le bouton n'a nulle part où aller.";
    public override string NoSessionForSideButton => "Pas de session : le bouton latéral n'a nulle part où aller.";
    public override string LockingIPhone => "Verrouillage de l'iPhone…";
    public override string SideButtonFailed(string error) => $"bouton latéral : {error}";

    public override string LabelVolumeUp => "Volume +";
    public override string LabelVolumeDown => "Volume −";
    public override string LabelMute => "Muet";
    public override string LabelLock => "Verrouiller";
    public override string LabelWake => "Réveiller l'écran";
    public override string NameVolumeButton => "Bouton de volume";
    public override string NameActionButton => "Bouton Action · touche Muet";
    public override string NameSideButton => "Bouton latéral";
    public override string ButtonNotSentNoSession => "Non envoyé : aucun iPhone connecté";
    public override string ButtonNotSent => "Non envoyé : l'envoi a échoué";
    public override string SideButtonsHint => "Les boutons de la tranche sont cliquables";
    public override string MenuShowSideButtons => "Montrer les boutons de la tranche";

    public override string WaitingTitle => "En attente de l'iPhone";
    public override string StepPlugCable => "Branche le câble USB";
    public override string StepUnlock => "Déverrouille l'écran";
    public override string StepDeveloperMode => "Laisse le mode développeur actif";
    public override string NothingToInstall =>
        "Rien à installer sur le téléphone : l'image développeur est montée par le câble.";

    public override string OpenAppleDevices => "Ouvrir Appareils Apple";
    public override string AppleDevicesOpened => "Appareils Apple demandé — la connexion repart d'elle-même.";
    public override string AppleDevicesFailed(string error) => $"Ouverture d'Appareils Apple impossible : {error}";
    public override string LockedTitle => "iPhone verrouillé";
    public override string LockedWithCode =>
        "L'écran est éteint : le flux vidéo tourne au ralenti, rien n'est cassé. Le réveil balaie et tape ton code.";
    public override string LockedWithoutCode =>
        "L'écran est éteint : le flux vidéo tourne au ralenti, rien n'est cassé. Le réveil rallume l'écran ; " +
        "le déverrouillage reste Face ID ou ton code, sur le téléphone.";
    public override string WakeScreen => "Réveiller l'écran";

    public override string Home => "Accueil";
    public override string HomeTooltip => "Bouton principal (clic droit dans l'image).";
    public override string ToIPhone => "Vers l'iPhone";
    public override string ToIPhoneTooltip =>
        "Presse-papiers Windows → iPhone (F2). Écrit le presse-papiers du téléphone ; si le service refuse, " +
        "le texte est tapé au clavier virtuel.";
    public override string FromIPhone => "Depuis l'iPhone";
    public override string FromIPhoneTooltip =>
        "Presse-papiers iPhone → Windows (F4). Le texte est repris ; une image ou des données sont annoncées, pas transférées.";
    public override string StopPasting => "Arrêter";
    public override string Wheel => "molette";

    public override string DiagnosticRun(int seconds, string journal) => $"Diagnostic : {seconds} s, journal dans {journal}";
    public override string SettingsUnreadable(string keptAs) => $"Réglages illisibles — l'ancien fichier est conservé sous {keptAs}.";
    public override string DdiFoundInRepository(string folder) => $"Image développeur trouvée dans le dépôt : {folder}";
    public override string DiagnosticWithoutDdi =>
        "Aucune image développeur et personne pour en choisir une : diagnostic sans session.";
    public override string DdiDialogTitle =>
        "Image développeur : archive Xcode (.xip), Device Support (.dmg) ou paquet (.pkg)";
    public override string DdiDialogFilter =>
        "Archives Apple (*.xip;*.dmg;*.pkg)|*.xip;*.dmg;*.pkg|Tous les fichiers (*.*)|*.*";
    public override string ChooseDdiArchive => "Aucune image développeur : choisis une archive Xcode ou un Device Support.";
    public override string NoDdiChosen =>
        "Sans image développeur, le téléphone ne peut pas être piloté. Relance pour en choisir une.";
    public override string Extracting(string file) => $"Extraction de {file} — quelques minutes…";
    public override string ArchiveScanned(long gigabytes) => $"Lecture de l'archive Xcode… {gigabytes} Go parcourus";
    public override string NoDdiInArchive(string file) =>
        $"{file} ne contient pas d'image développeur — attendu une archive Xcode 27, un composant Device Support ou XcodeSystemResources.pkg.";
    public override string DdiReady(string? build) => $"Image développeur prête ({build ?? "build inconnue"}).";
    public override string ExtractionFailed(string error) => $"Extraction impossible : {error}";

    public override string ReplayEmpty(string path) => $"Capture vide : {path}";
    public override string ReplayRunning(string file, long datagrams, double loopSeconds) =>
        $"Rejeu : {file}  ·  {datagrams} datagrammes, boucle de {loopSeconds:F1} s";
    public override string ReplayFailed(string error) => $"Rejeu impossible : {error}";

    public override string IPhonePlugged => "iPhone branché.";
    public override string IPhoneUnplugged => "iPhone débranché.";
    public override string MultiplexerTrouble(string error) => $"Multiplexeur : {error}";
    public override string Connecting => "Connexion à l'iPhone…";
    public override string MirrorOpen => "Miroir ouvert.";
    public override string MirrorOpenHowTo => "Miroir ouvert · clic dans l'image pour piloter · Ctrl+Alt pour rendre la souris";
    public override string RetryIn(string error, double seconds) => $"{error} — nouvelle tentative dans {seconds:0} s.";
    public override string Step(SessionState state) => state switch
    {
        SessionState.Detached => "Débranché.",
        SessionState.Attached => "Branché.",
        SessionState.Paired => "Appairé.",
        SessionState.DdiMounted => "Image développeur montée.",
        SessionState.TunnelUp => "Tunnel ouvert.",
        SessionState.MediaUp => "Miroir en cours.",
        SessionState.Resetting => "Relance du miroir…",
        _ => "Erreur.",
    };
    public override string ActionFailed(string action, string error) => $"{action} : {error}";
    public override string InputFault(string error) => $"entrée : {error}";
    public override string InputReopening => "Canal d'entrée bloqué : réouverture…";
    public override string InputRestored => "Canal d'entrée rétabli.";
    public override string InputLost => "Canal d'entrée perdu : la session est reconstruite.";

    public override string IPhoneLocked => "iPhone verrouillé — l'image reprend au réveil.";
    public override string ScreenOn => "Écran rallumé.";
    public override string StreamResumed => "Flux repris.";
    public override string StreamStopped(DateTime at) => $"Flux arrêté à {at:HH:mm:ss}. F3 pour les compteurs.";
    public override string IdleDrivable => "Clic dans l'image pour piloter  ·  Ctrl+Alt pour récupérer la souris";
    public override string WaitingForIPhone => "En attente de l'iPhone.";
    public override string LandscapeIslandLeft => "Téléphone en paysage, île à gauche.";
    public override string LandscapeIslandRight => "Téléphone en paysage, île à droite.";
    public override string LandscapeUnknown =>
        "Paysage — impossible de dire de quel côté est l'île (écran trop sombre). Île et boutons masqués.";
    public override string Portrait => "Téléphone en portrait.";

    public override string WakingScreen => "Réveil de l'écran…";
    public override string WakeFailed(string error) => $"réveil : {error}";
    public override string ScreenOnUnlockYourself =>
        "Écran allumé — le déverrouillage demande ton visage, ou ton code sur le téléphone.";
    public override string PasscodeHint =>
        "iPhone verrouillé. Si Face ID ne le déverrouille pas, tape ton code sur ton clavier — iOS " +
        "n'affiche pas le pavé dans le miroir.";
    public override string Unlocking(int characters) => $"Déverrouillage : balayage puis {characters} caractère(s) de code…";
    public override string CodeSent => "Code envoyé. Si l'écran reste verrouillé, c'est le code ou Face ID qu'il faut.";

    public override string WindowLeft => "Fenêtre quittée.";
    public override string Driving => "Pilotage  ·  Ctrl+Alt gauche pour rendre la souris";
    public override string MouseReturned => "Souris rendue à Windows  ·  clic dans l'image pour reprendre";
    public override string ClickToResume(string reason) => $"{reason}  Clic dans l'image pour reprendre.";

    public override string NoSessionToPaste => "Pas de session — rien à coller.";
    public override string ClipboardUnreadable => "Presse-papiers illisible — une autre application le tient.";
    public override string ClipboardEmpty => "Presse-papiers vide.";
    public override string SendingClipboard(int characters) => $"Envoi de {characters} caractère(s) au presse-papiers de l'iPhone…";
    public override string ClipboardSent(int characters) =>
        $"{characters} caractère(s) dans le presse-papiers de l'iPhone  ·  ⌘V ou appui long pour coller.";
    public override string ClipboardServiceFallback(string error) =>
        $"Service presse-papiers indisponible ({error}) — collage par frappe.";
    public override string NoSessionToFetch => "Pas de session — le presse-papiers du téléphone est hors de portée.";
    public override string ReadingPhoneClipboard => "Lecture du presse-papiers de l'iPhone…";
    public override string FetchedText(int characters) => $"{characters} caractère(s) copié(s) depuis l'iPhone.";
    public override string WindowsClipboardLocked => "Presse-papiers Windows verrouillé par une autre application — rien copié.";
    public override string PhoneClipboardImage(string type, int bytes) =>
        $"Le presse-papiers de l'iPhone contient une image ({type}, {Size(bytes)}) — non transférée.";
    public override string PhoneClipboardData(string type, int bytes) =>
        $"Le presse-papiers de l'iPhone contient des données ({type}, {Size(bytes)}) — non transférées.";
    public override string PhoneClipboardEmpty => "Presse-papiers de l'iPhone vide.";
    public override string PhoneClipboardUnreadable(string error) => $"Presse-papiers de l'iPhone illisible : {error}";
    public override string NothingTypeable => "Rien de saisissable dans le presse-papiers.";
    public override string PasteCancelled(int keystrokes) => $"Collage interrompu après {keystrokes} frappes.";
    public override string PasteSessionLost(int done, int total) => $"Collage interrompu à {done}/{total} — la session est tombée.";
    public override string PasteProgress(int done, int total) => $"Collage… {done}/{total}";
    public override string Pasted(int keystrokes, int skipped, int? cutAt) =>
        $"Collé : {keystrokes} frappes."
        + (skipped > 0 ? $" {skipped} caractère(s) hors de portée du clavier, ignoré(s)." : "")
        + (cutAt is int cut ? $" Coupé à {cut} caractères." : "");
    public override string PasteFailed(string error) => $"Collage interrompu : {error}";

    public override string Audio => "Audio";
    public override string AudioTooltip =>
        "Le son de l'iPhone, par le câble, sur la sortie Windows que tu choisis. Décodé ici — Windows n'a pas " +
        "de décodeur AAC-ELD.";
    public override string AudioSection => "Son de l'iPhone";
    public override string AudioOutput => "Sortie";
    public override string AudioDefaultOutput => "Sortie par défaut de Windows";
    public override string AudioVolume => "Volume";
    public override string AudioDelay => "Retard";
    public override string AudioDelayHint =>
        "Retient le son pour l'aligner sur l'image. À augmenter si le son arrive avant.";
    public override string AudioRetry => "Réessayer";
    public override string AudioMilliseconds(double milliseconds) => $"{milliseconds:0} ms";

    public override string AudioOff => "Son coupé — il joue sur l'iPhone à la place.";
    public override string AudioNoSession => "Pas encore de miroir : le son vient avec lui.";
    public override string AudioOpening => "Ouverture du son…";
    public override string AudioRefused(string reason) => $"Pas de son : {reason}";
    public override string AudioRunning(string device, string level) =>
        $"Lecture sur {device}  ·  {level}";

    public override string DimGestureInvalid => "dimGesture attend cinq nombres — luminosité ignorée.";
    public override string OpeningControlCentre => "Ouverture du centre de contrôle…";
    public override string Dimming => "Baisse de la luminosité…";
    public override string Dimmed =>
        "Luminosité baissée. Si ce n'est pas ce qui s'est passé, ajuste dimGesture dans settings.json.";
    public override string DimFailed(string error) => $"Luminosité : {error}";

    public override string Counters(in CounterSample s) =>
        $"{(s.Driving ? "PILOTAGE" : "libre")}   {s.Width}x{s.Height}->{s.ShownWidth,4:0}   " +
        $"{s.ReceivedFps,4:0} i/s reçues   {s.ShownFps,4:0} i/s affichées   décodage {s.DecodeMs,5:0.0} ms   " +
        $"souris {s.MouseHz,4:0}/s -> {s.SentHz,3:0}/s (-{s.DroppedHz,4:0}/s, file {s.Queue})   " +
        $"état {s.State}   erreurs {s.Errors}";
    public override string CountersNoPicture(bool driving, double mouseHz, SessionState state) =>
        $"{(driving ? "PILOTAGE" : "libre")}   aucune image   souris {mouseHz,4:0}/s   état {state}";

    public override string MenuLanguageAuto => "Langue : celle de Windows";
    public override string LanguageSaved(string setting) => setting switch
    {
        "en" => "Langue de l'interface : anglais (enregistrée).",
        "fr" => "Langue de l'interface : français (enregistrée).",
        _ => "Langue de l'interface : celle de Windows (enregistrée).",
    };

    private static string Size(int bytes) => bytes switch
    {
        < 1024 => $"{bytes} octets",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} Ko",
        _ => $"{bytes / (1024.0 * 1024.0):0.#} Mo",
    };
}
