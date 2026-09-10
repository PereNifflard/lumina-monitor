namespace LuminaMonitor.Core;

/// <summary>
/// Core's sentences in French — the wording they always had, with the accents
/// the older ones were written without now that they reach the screen.
/// </summary>
internal sealed class FrenchCoreTexts : CoreTexts
{
    public override string MultiplexerNotResponding =>
        "Le multiplexeur Apple ne répond plus : relance l'app Appareils Apple (ou rebranche le câble).";
    public override string MultiplexerNotRunning =>
        "Le multiplexeur Apple n'est pas lancé : ouvre l'app Appareils Apple ou branche l'iPhone.";

    public override string NotConnected => "Session non connectée : appeler ConnectAsync d'abord.";
    public override string NoDeviceAttached => "Aucun appareil attaché.";
    public override string DeviceWithoutUdid => "Appareil sans UDID.";
    public override string NotPaired =>
        "Appareil non appairé : ouvrir l'app Appareils Apple et répondre « Se fier à cet ordinateur ».";
    public override string PairRecordUnreadable => "Enregistrement d'appairage illisible.";
    public override string StartSessionRefused(string refusal) => $"StartSession refusé : {refusal}";
    public override string DeveloperModeOff(object status) =>
        $"Mode développeur inactif (DeveloperModeStatus = {status}) — Réglages > Confidentialité et sécurité > Mode développeur.";
    public override string DdiUnavailable(object status) => $"Image développeur indisponible : {status}.";
    public override string HidServiceMissing =>
        "Service HID absent de l'annuaire : l'image développeur n'est pas montée.";
    public override string ServiceMissing(string name) => $"Service absent de l'annuaire : {name}";
    public override string TcpNoAnswer(int port) => $"Connexion TCP vers le port {port} : pas de réponse.";
    public override string RemoteXpcNoSettings => "RemoteXPC : pas de SETTINGS du téléphone en 3 s";
    public override string RemoteXpcNoReply => "RemoteXPC : pas de réponse du téléphone dans le délai.";

    public override string CallInProgress =>
        "Appel en cours sur l'iPhone : iOS interdit le miroir pendant un appel. L'image revient seule à la fin de l'appel.";
    public override string RemoteControlNeedsIos27 => "Le pilotage à distance demande iOS 27 ou plus sur l'iPhone.";
    public override string StreamRefused(int? code) =>
        $"Le téléphone a refusé le flux vidéo{(code is int c ? $" (code {c})" : "")}.";

    public override string UnlockForUnmount =>
        "iPhone verrouillé : le démontage de l'image développeur attend le déverrouillage.";
    public override string UnlockForUpload =>
        "iPhone verrouillé : l'envoi de l'image développeur attend le déverrouillage.";
    public override string UnlockToRestartMirror => "Déverrouille l'iPhone pour relancer le miroir";
    public override string RestartIPhone => "Redémarre l'iPhone.";

    public override string BuildManifestUnreadable => "BuildManifest illisible";
    public override string NoPersonalizationIdentifiers => "pas d'identifiants de personnalisation";
    public override string NoNonce => "pas de nonce";
    public override string NoBuildIdentity(long boardId, long chipId) =>
        $"Aucune BuildIdentity pour BoardId {boardId} / ChipID 0x{chipId:X}.";
    public override string AppleSigningServer(string status, int httpStatus) =>
        $"Le serveur de signature Apple répond : {status} (HTTP {httpStatus})";
    public override string PhoneNoAnswer(string command, double seconds) =>
        $"Le téléphone ne répond pas à {command} en {seconds:N0} s.";
    public override string ImageUploadStalled(long sent, long size) =>
        $"Envoi de l'image bloqué après {sent:N0} octets sur {size:N0}.";

    public override string FileNotFound(string file) => $"{file} : fichier introuvable.";
    public override string FileTooShort(string file, long bytes) =>
        $"{file} : fichier trop court pour être une archive Apple ({bytes:N0} octets) — téléchargement incomplet ?";
    public override string XarWithoutContent(string file) =>
        $"{file} : archive xar sans Content ni Payload — ce n'est ni une archive Xcode ni un paquet Apple.";
    public override string UnexpectedFile(string file, string detail) =>
        $"{file} : fichier inattendu — attendu une archive Xcode (.xip), un composant Device Support (.dmg) ou un paquet (.pkg). Détail : {detail}";
    public override string NoContentEntry(string file) => $"{file} : pas d'entrée Content — ce n'est pas une archive Xcode.";
    public override string ArchiveDamaged(string file, double gigabytes, string detail) =>
        $"{file} : archive incomplète ou abîmée après {gigabytes:N1} Go — retélécharge-la chez Apple. Détail : {detail}";
    public override string SystemResourcesMissing(string file) =>
        $"{file} : XcodeSystemResources.pkg introuvable — cette archive n'est pas celle qui porte les images développeur (attendu Xcode 27).";

    public override string UnknownButton(string name, string expected) => $"Bouton inconnu : {name} (attendu : {expected})";
    public override string HidChannelStuck(double milliseconds) =>
        $"canal HID bloqué : un rapport n'est pas parti en {milliseconds:0} ms";
    public override string ClipboardNeedsTunnel => "Session non connectée : le presse-papiers passe par le tunnel.";
    public override string ClipboardServiceMissing => "Service presse-papiers absent de l'annuaire du téléphone.";

    private static string Phone(string name) => string.IsNullOrWhiteSpace(name) ? "le téléphone" : name;
    private static string Code(int? code) => code is int c ? $" (0x{c:X8})" : "";

    public override string BluetoothAudioNeedsWindows2004 =>
        "Recevoir le son du téléphone en Bluetooth demande Windows 10 version 2004 ou plus récent.";
    public override string PhoneAudioOpening(string phone) => $"Connexion Bluetooth à {Phone(phone)}…";
    public override string PhoneAudioOpen(string phone) =>
        $"Connecté : ce que joue {Phone(phone)} sort de ce PC (sortie par défaut de Windows).";
    public override string PhoneAudioWaiting(string phone) =>
        $"Liaison avec {Phone(phone)} perdue. Choisis ce PC dans le menu audio du téléphone, ou reconnecte.";
    public override string PhoneAudioClosed => "Son du téléphone sur ce PC : coupé.";
    public override string PhoneAudioTimedOut(string phone) =>
        $"{Phone(phone)} ne répond pas en Bluetooth : Bluetooth activé dans les Réglages de l'iPhone (pas seulement "
        + "le Centre de contrôle) et téléphone à portée ? Tu peux aussi toucher ce PC dans Réglages › Bluetooth de l'iPhone.";
    public override string PhoneAudioDenied(int? code) =>
        $"Windows a refusé la connexion audio Bluetooth{Code(code)}. Le Bluetooth de ce PC est-il activé ?";
    public override string PhoneAudioNotAvailable =>
        "Ce téléphone n'est plus une source audio Bluetooth pour Windows : appaire-le de nouveau dans les réglages Bluetooth.";
    public override string PhoneAudioUnknownFailure(int? code) =>
        $"La connexion audio Bluetooth a échoué{Code(code)}.";
    public override string PhoneAudioError(int? code) =>
        $"Impossible d'établir la connexion audio Bluetooth{Code(code)}.";
    public override string CallBridgeEndpointGone =>
        "Appel par le PC arrêté : le téléphone a fermé sa liaison audio mains-libres (fin d'appel ?).";
    public override string CallBridgeFailed(int code) =>
        $"Appel par le PC arrêté sur une erreur audio (0x{code:X8}).";
}
