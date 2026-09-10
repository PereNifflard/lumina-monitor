[English](BLUETOOTH_AUDIO.md) · **Français**

# Le son du téléphone par Bluetooth — étude

Faits mesurés le 10 septembre 2026, iPhone 17 Pro Max sous iOS 27, Windows 11 25H2 (build 26200),
adaptateur Bluetooth MediaTek, commandes de sonde `winrt-selftest`, `phone-audio`, `audio-endpoints`,
`bridge-selftest`, `call-bridge`. Ce qui n'a pas pu être mesuré est dit tel quel.

**En bref.** Par le câble, le son est de l'AAC-ELD et Windows ne sait pas le décoder
([AUDIO.fr.md](AUDIO.fr.md)). Par Bluetooth, Windows *peut* servir de haut-parleur au téléphone
(A2DP), mais seulement tant qu'une application tient ouverte une
`Windows.Media.Audio.AudioPlaybackConnection` — aucune ne le faisait sur ce PC, d'où le silence.
`Core/Audio` le fait désormais, en interop écrite à la main. Le jour de l'essai, **le téléphone n'a
pas répondu à l'appel Bluetooth du PC** : le chemin logiciel est prouvé jusqu'à la radio, le son
lui-même ne l'est pas encore. Le protocole en fin de note est ce qui reste.

## 1. Pourquoi rien ne passait

Windows 10 2004 et suivants savent recevoir l'A2DP d'un téléphone, à une condition documentée par
Microsoft : une application appelle `AudioPlaybackConnection.GetDeviceSelector()`, trouve le téléphone
par `DeviceInformation.FindAllAsync(selector)`, puis `TryCreateFromId(id)` → `StartAsync()` →
`OpenAsync()`, et garde l'objet vivant. Windows joue alors le son lui-même, sur la sortie par défaut.
L'appairage seul crée le périphérique (`<nom de l'iPhone> A2DP SNK`) mais n'ouvre rien.

Vérifié sur ce PC :

- Le sélecteur que construit Windows (`winrt-selftest`) demande une interface KSCATEGORY_AUDIO portant
  le service Bluetooth **Audio Source** `0000110A`, activée. **Une** réponse : l'iPhone, interface
  `…\SNK`, pilotée par **BthA2dp — « Microsoft Bluetooth A2dp Sink »** (le pilote de Microsoft livré
  avec Windows, pas celui de MediaTek). Le prérequis existe.
- Parmi tous les points de terminaison audio de Windows, désactivés et absents compris
  (`audio-endpoints --all`), **il n'y a aucun point de terminaison Bluetooth** — ni A2DP ni
  mains-libres. Rien n'est connecté, rien n'est ouvert.
- La classe publique est dans le processus (`Windows.Media.Devices.dll`) ; elle pilote une classe
  privée, `Microsoft.Bluetooth.Profiles.A2dp.Private.A2dpSinkPlaybackConnection`, hébergée par le
  service **BthAvctpSvc** (registre, `ActivatableClassId`). La connexion est donc tenue par un service
  Windows pour le compte de l'application.

Diagnostic **confirmé côté logiciel** : sans cet appel, aucun flux A2DP ne peut s'ouvrir.

## 2. Ce qu'a donné l'essai réel

`phone-audio 60`, téléphone à portée, appairé, sur le câble :

| essai | résultat | durée |
|---|---|---|
| 1 | `OpenAsync` → **UnknownFailure**, ExtendedError **0x8007001F** (ERROR_GEN_FAILURE) | 5,3 s |
| 2 | idem | 12,9 s |
| 3 (après relecture du code) | idem | 12,9 s |

`PhoneAudioLink.Description` traduit ce code en « le téléphone ne répond pas en Bluetooth » et la
marche à suivre, plutôt qu'en « échec inconnu ».

Contrôles autour :

- La radio Bluetooth du PC est **allumée** (`Windows.Devices.Radios`).
- L'adresse Bluetooth que le téléphone annonce par le câble (lockdown `BluetoothAddress`, commande
  `session`) est **celle que Windows a appairée** : ce n'est pas l'appairage périmé d'un ancien
  téléphone.
- Le `LastConnectedTime` que Windows garde pour le téléphone est le **17 août 2026**, inchangé après
  les deux essais : la liaison Bluetooth Classic ne s'est jamais établie.
- 5,3 s, c'est le *page timeout* Bluetooth (5,12 s) : le PC a appelé le téléphone et **le téléphone
  n'a pas répondu**. Les causes probables sont du côté du téléphone : Bluetooth coupé, ou « coupé »
  depuis le Centre de contrôle (qui déconnecte les accessoires et refuse les nouveaux jusqu'à ce qu'on
  le rallume).

La connexion reste **à l'écoute** après un refus (`StartAsync` a réussi) : si le téléphone choisit
ensuite ce PC dans son propre menu Bluetooth, le lien passe seul à `Open` — la sonde le lit par
scrutation et l'affiche.

## 3. L'interop, à la main

Aucun NuGet, aucune projection WinRT : combase (`RoGetActivationFactory`, `WindowsCreateString`,
`WindowsDeleteString`, `WindowsGetStringRawBuffer`) et des interfaces déclarées emplacement par
emplacement, dans `Core/Audio/WinRt/`.

- **IID** : le SDK Windows n'est pas installé ici. Ils ont été lus dans les métadonnées d'où les
  en-têtes sont générés, `C:\Windows\System32\WinMetadata\*.winmd` (GuidAttribute de chaque interface,
  méthodes dans l'ordre de la vtable), avec `System.Reflection.Metadata`. Chacun est exercé par
  `winrt-selftest` : un mauvais IID échoue en E_NOINTERFACE au premier transtypage.
- **Interfaces paramétrées** (`IAsyncOperation<…>`, `IVectorView<…>`) : pas de GUID propre ; le leur se
  déduit d'une signature (UUID v5, SHA-1). `WinRtIid.FromSignature` le calcule et l'auto-test recalcule
  les trois constantes. Contrôle : `IAsyncOperation<DeviceInformationCollection>` donne
  `45180254-082e-5274-b2e7-ac0517f44d07`, la valeur que publie le SDK.
- **`InterfaceIsIInspectable` ne fonctionne pas en .NET 8** : tout appel lève
  `PlatformNotSupportedException` (« Marshalling as IInspectable is not supported ») — mesuré. Chaque
  interface est donc déclarée `InterfaceIsIUnknown` et commence par les trois emplacements
  d'IInspectable (GetIids, GetRuntimeClassName, GetTrustLevel), comme les déclarations Media Foundation
  répètent ceux d'IMFAttributes.
- **Asynchrone** : scrutation de `IAsyncInfo.Status` toutes les 25 ms ; l'état de la connexion est lu
  toutes les 500 ms. Ni gestionnaire de complétion, ni événement.
- **Appartements** : `CoIncrementMTAUsage` une fois ; tout appel passe par le pool de threads (MTA
  implicite), jamais par le thread STA de la fenêtre. La classe est enregistrée agile
  (`Threading = Both`).

Le repli (`net8.0-windows10.0.19041.0` et la projection téléchargée) n'a **pas** été nécessaire.

## 4. Le choix de la sortie (haut-parleurs ou casque)

Ce qui est sûr : `AudioPlaybackConnection` n'a **aucun paramètre de sortie** ; Windows rend le son sur
sa sortie par défaut. Ce qui n'est pas encore mesuré : *quel processus* le rend, puisque la connexion
vit dans le service BthAvctpSvc. C'est ce qui décide de tout, et `phone-audio` l'affiche dès la
première seconde de son (`NOUVELLE session … pid … <processus>`) :

- **session dans le processus de l'application** → le réglage par application de Windows s'applique :
  *Paramètres › Système › Son › Mélangeur de volume* (`ms-settings:apps-volume`), périphérique de sortie
  de Lumina Monitor. La sortie par défaut du système ne bouge pas.
- **session dans un service (svchost)** → le réglage par application ne l'atteint probablement pas ; le
  seul levier honnête qui reste est la sortie par défaut du système. Si Windows expose en même temps un
  point de terminaison A2DP en *entrée* (la sonde affiche tout nouveau point de terminaison), une
  seconde voie s'ouvre : le recopier avec `AudioPump` vers la sortie choisie — à écrire une fois vu, pas
  avant.

Ce que ce projet ne fait **pas** : changer la sortie par défaut du système sans le dire. La fenêtre
liste les sorties (`AudioEndpoints.List(AudioFlow.Output)`), dit où part le son
(`AudioEndpoints.Default`) et ouvre la page de Windows (`BluetoothAudio.OpenSoundSettings()`).

## 5. Le micro du PC vers le téléphone (mains-libres, HFP)

En HFP, Windows joue le **kit mains-libres de voiture** (pilote `BthHFAud`,
`<nom de l'iPhone> Hands-Free HF Audio`, `microsoft_bluetooth_hfp.inf`). Ses deux points de
terminaison, par définition du profil :

- **entrée** (capture) = l'autre bout de l'appel, la voix à entendre ;
- **sortie** (rendu) = ce qu'entend l'autre bout : c'est là que va notre micro.

**Ils n'existent pas tant que le téléphone n'est pas connecté** : le jour de l'essai, aucun, même
désactivé. Rien n'a donc pu être vérifié sur le téléphone. Conditions établies :

- le canal voix (SCO) s'ouvre **pendant un appel** (téléphone, FaceTime, application de VoIP) dont
  l'iPhone envoie l'audio vers ce PC (bouton audio de l'appel › ce PC) ;
- il s'ouvre aussi **dès qu'une application Windows ouvre ces points de terminaison**, appel ou pas — et
  iOS confie alors son micro au PC : c'est le symptôme « le micro du téléphone est muet » déjà vécu
  ([AUDIO.fr.md](AUDIO.fr.md) § 6).

**Livré : `CallAudioBridge`, désactivé par défaut.** Deux pompes WASAPI en mode partagé (micro choisi →
sortie mains-libres ; entrée mains-libres → sortie choisie), un format commun (48 kHz stéréo flottant)
avec AUTOCONVERTPCM pour que le moteur convertisse en 8/16 kHz mono, capture pilotée par événement,
file de rendu plafonnée à 60 ms (ce qui ne tient pas est jeté). Vérifié sur les points de terminaison
de ce PC à gain nul (`bridge-selftest`) : **144 480 trames en 3 s, aucune jetée**, conversion acceptée
dans les deux sens. Non vérifié : les points de terminaison du téléphone eux-mêmes. Règles, écrites
dans le code : démarrer seulement sur demande explicite pendant un appel, jamais au lancement ;
`Dispose` rend le micro ; le pont s'arrête seul quand Windows démonte la liaison
(`AUDCLNT_E_DEVICE_INVALIDATED` → `Stopped`, `StopReason`). Aucun pilote virtuel nulle part.

Alternative sans code de notre part : l'application **Lien avec votre téléphone** (Phone Link) de
Microsoft gère les appels de l'iPhone par ce même profil.

## 6. Ce que l'utilisateur doit tester

**A — musique (A2DP).** Sur l'iPhone : Réglages › Bluetooth activé (dans Réglages, pas dans le Centre de
contrôle). Sur le PC : `LuminaMonitor.UsbProbe phone-audio 120`. Si la sonde dit `Refused` /
`0x8007001F`, toucher ce PC sous *Mes appareils* sur l'iPhone pendant que la sonde tourne encore.
Attendu : `[evenement] etat -> Open`, puis lancer une musique. Dire : (1) si on l'entend, et sur quelle
sortie ; (2) les lignes `NOUVELLE session` et `NOUVEAU point de terminaison` — elles tranchent le § 4.

**B — appel (HFP).** Téléphone connecté au PC comme en A. Passer un appel ; dans l'écran d'appel, bouton
audio › ce PC. Puis `LuminaMonitor.UsbProbe audio-endpoints --all` (les points de terminaison
mains-libres apparaissent-ils, actifs ?) et `LuminaMonitor.UsbProbe call-bridge 30` (l'autre bout doit
entendre le micro du PC, et sa voix sortir de la sortie par défaut). Raccrocher : la sonde doit afficher
`[pont arrete]`. Vérifier ensuite que le micro de l'iPhone fonctionne dans une autre app (Dictaphone).

## 7. Les commandes

```
LuminaMonitor.UsbProbe winrt-selftest                  # interop WinRT sans téléphone (IID, sélecteur, FindAllAsync)
LuminaMonitor.UsbProbe phone-audio [sec=30] [nom]       # ouvre l'A2DP, suit état, sessions, points de terminaison, crêtes
LuminaMonitor.UsbProbe audio-endpoints [--all] [--props]
LuminaMonitor.UsbProbe bridge-selftest [sec=3]          # les pompes du pont d'appel, points locaux, gain nul
LuminaMonitor.UsbProbe call-bridge [sec=30] [micro] [sortie]
```

## 8. L'API pour la fenêtre (`LuminaMonitor.Core.Audio`)

```csharp
static class BluetoothAudio {
    bool IsSupported { get; }
    Task<IReadOnlyList<BluetoothPhone>> ListPhonesAsync(CancellationToken cancel = default);
    Task<PhoneAudioLink> StartListeningAsync(string deviceId, string? name = null, CancellationToken cancel = default);
    void OpenSoundSettings();                           // ms-settings:apps-volume
}
record BluetoothPhone(string Id, string Name, bool IsEnabled);
sealed class PhoneAudioLink : IDisposable {           // le son passe tant qu'il vit
    PhoneAudioState State { get; }                     // Opening, Open, Waiting, Refused, Closed
    PhoneAudioRefusal Refusal { get; }                 // None, TimedOut, DeniedBySystem, UnknownFailure, NotAvailable, Error
    int? ErrorCode { get; }  DateTime? OpenedAt { get; }  string Description { get; }   // traduit
    event EventHandler? StateChanged;                  // thread du pool
    Task<PhoneAudioState> ReopenAsync(CancellationToken cancel = default);
    void Dispose();
}
static class AudioEndpoints {
    IReadOnlyList<AudioEndpoint> List(AudioFlow? flow = null, bool includeInactive = false);
    AudioEndpoint? Default(AudioFlow flow);
    float? Peak(string endpointId);
    IReadOnlyList<AudioSessionInfo> Sessions(string endpointId);
}
record AudioEndpoint(string Id, string Name, string DeviceName, AudioFlow Flow, AudioEndpointState State,
                     bool IsDefault, bool IsDefaultForCommunications, BluetoothAudioRole BluetoothRole);
sealed class CallAudioBridge : IDisposable {          // désactivé tant qu'on ne le démarre pas, voir § 5
    static PhoneCallEndpoints? FindPhoneEndpoints();
    static CallAudioBridge Start(string microphoneId, string outputId, PhoneCallEndpoints phone);
    bool IsRunning { get; }  string? StopReason { get; }  event EventHandler? Stopped;
}
```

Les méthodes d'`AudioEndpoints` sont du COM
synchrone : les appeler par `Task.Run`, pas depuis le thread de la fenêtre. Tout texte destiné à
l'utilisateur passe par `CoreTexts` (anglais, français) ; `PhoneAudioLink.Description` et
`CallAudioBridge.StopReason` sont déjà dans la langue de l'interface.
