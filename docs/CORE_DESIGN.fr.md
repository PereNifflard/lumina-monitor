# LuminaMonitor.Core — conception (étape 1)

[English](CORE_DESIGN.md) · **Français**

Objectif : sortir de la sonde (`src/LuminaMonitor.UsbProbe`) une bibliothèque réutilisable
par l'app de bureau, sans rien changer au comportement prouvé sur le téléphone le 8 septembre 2026.
Règle du projet inchangée : aucun code tiers, protocole Apple réimplémenté en C#.

## Projets

| Projet | Cible | Rôle |
|---|---|---|
| `LuminaMonitor.Formats` | net8.0 | lecteurs xar / pbzx / cpio / UDIF / HFS+ / APFS (existant) |
| `LuminaMonitor.Core` | net8.0 | **nouveau** : tout le protocole + façade `DeviceSession` |
| `LuminaMonitor.UsbProbe` | net8.0-windows | console de diagnostic, ne fait plus que des appels à Core |

`Core` référence `Formats`. La sonde référence `Core`. `InternalsVisibleTo("LuminaMonitor.UsbProbe")`
sur Core pour que les commandes bas niveau (`session`, `catalogue`, `tunnel`…) gardent accès aux briques.

## Découpage de Core (dossiers = namespaces)

- `Usb/` : `Plist`, `UsbmuxClient`, `LockdownClient` + `PlistService`, `PairRecord` — déplacés tels quels.
- `Tunnel/` : `CdTunnel`, `Ipv6`, `TunnelNet`, `TcpConnection` — déplacés tels quels.
- `RemoteXpc/` : `Xpc`, `RemoteXpc`, `Rsd`, `CoreDevice` (+ `DisplayService`) — déplacés tels quels.
- `Ddi/` : `ImageMounter` (+ `.Mount`), `Tss`, et **nouveau** `DdiManager`.
- `Media/` : `MediaOffer`, `Bplist`, et **nouveau** `MediaSession`.
- `Hid/` : `Hid`, `IndigoHid`, et **nouveau** `InputInjector`.
- racine : **nouveau** `DeviceSession` (façade), `SessionState`, `DeviceInfo`, `LuminaException`.

Déplacer = simple déplacement de fichiers (le dépôt n'a pas encore de commit),
namespace `LuminaMonitor.Core.<Dossier>`, visibilité `internal` conservée sauf pour les types listés
dans « Surface publique ». Aucune réécriture des briques : elles sont prouvées.

## Surface publique (ce que l'app de bureau verra)

```csharp
public sealed class DeviceSession : IAsyncDisposable
{
    public DeviceSession(DdiSource ddi, ILog? log = null);
    public SessionState State { get; }                 // Detached, Attached, Paired, TunnelUp, DdiMounted, MediaUp, Resetting, Faulted
    public DeviceInfo? Device { get; }                 // Udid, Name, ProductType, ProductVersion, BuildVersion
    public event Action<SessionState>? StateChanged;
    public event Action<string>? UnlockRequired;       // le téléphone est verrouillé : demander à la personne
    public event Action<string>? RestartRequired;      // trois resets doux sans succès : « Redémarre l'iPhone »
    public event Action<ReadOnlyMemory<byte>>? RtpPacket;   // relayé depuis MediaSession (étape 2 s'en servira)
    public Task ConnectAsync(CancellationToken ct);    // enchaîne : usbmux → lockdown TLS → tunnel → RSD → DDI → média
    public Task DisconnectAsync();
    public InputInjector Input { get; }                // valide quand State >= MediaUp (boutons dès TunnelUp+DDI)
    public Task<bool> RebuildInputAsync();             // rouvre le seul canal HID ; false ⇒ la session passe Faulted

    // Écran : « endormi » n'est connu que si c'est cette session qui l'a endormi.
    public bool ScreenAsleep { get; }
    public event Action<bool>? ScreenSleepChanged;
    public Task SleepScreenAsync();                    // « lock » (Consumer 0x30, 500 ms) ; la veille du flux est suspendue
    public Task WakeScreenAsync();                     // « home » (Consumer 0x40) — 0x30 n'est PAS une bascule, mesuré ; réarme la veille.
                                                       // Le DÉVERROUILLAGE reste Face ID ou le code

    // Presse-papiers du téléphone (com.apple.coredevice.pasteboardservice) : service ouvert à la demande, raccroché après.
    public Task<string?> ReadPhoneClipboardAsync();
    public Task<ClipboardContent> ReadPhoneClipboardSnapshotAsync();   // genre + UTI + taille : une image n'est pas « vide »
    public Task WritePhoneClipboardAsync(string text);
}

public enum ClipboardKind { Nothing, Text, Image, Data }
public readonly record struct ClipboardContent(ClipboardKind Kind, string? Text, string? Type, int Bytes);

public sealed record DdiSource(string Folder);         // dossier « copie de Restore/ » (ddi27) ; l'extraction reste une commande à part

public sealed class InputInjector
{
    public Task TapAsync(double xNorm, double yNorm, int holdMs = 60);        // 0..1, absolu
    public Task TouchDownAsync(double xNorm, double yNorm);
    public Task TouchMoveAsync(double xNorm, double yNorm);
    public Task TouchUpAsync(double xNorm, double yNorm);
    public Task DragAsync(double x1, double y1, double x2, double y2, int durationMs, int steps = 20);
    public Task PressButtonAsync(string name);                                 // home, lock, volume-up, volume-down, mute, siri
    public Task TypeAsync(string text);                                        // surface clavier 512 : rapport 39 octets par touche (Hid.KeyboardReport)
}
```

`ILog` : `void Info(string)`, `void Warn(string)` ; implémentation console dans la sonde.

## DdiManager (Ddi/)

- `Task<DdiStatus> EnsureMountedAsync(DdiSource, LockdownClient/pair record, IProgress<string>?, CancellationToken)`.
- Logique = la commande `mount` actuelle, à l'identique, avec un ajout : lire d'abord `CopyDevices` ;
  si une image `Personalized` est montée et que son `ImageSignature` (48 octets) **égale le SHA-384 de
  notre image**, ne rien faire (déjà montée). Sinon démonter puis monter (nonce, TSS épinglé, envoi, MountImage).
- Attente du déverrouillage (`DeviceLocked`) : conservée, mais rendue observable : appel de `UnlockRequired`
  puis nouvelle tentative toutes les 3 s, borne 10 min, connexion neuve à chaque essai.
- Résolution image/trustcache via `BuildManifest.plist` (BuildIdentity du téléphone) : inchangée.

## MediaSession (Media/)

- Ouvre `com.apple.coredevice.displayservice`, écoute UDP sur la pile tunnel, envoie `startmediastream`
  (offre vidéo actuelle), expose `RtpPacket`, `Codec`/`StreamConfig` (le dictionnaire de réponse, brut, pour l'étape 2).
- `StopAsync` : hygiène de fin, dans cet ordre — arrêt de la boucle de rapports, **RTCP BYE** (PT 203),
  `stopmediastream`, puis on laisse au téléphone **1 s pour raccrocher** avant de fermer nous-mêmes
  (FIN TCP, jamais RST). Durée journalisée. Hypothèse : la fermeture abrupte est ce qui finissait par
  figer le démon d'affichage au bout de quelques sessions. Même traitement pour les canaux HID
  (`InputInjector.CloseAsync`).
- Doit rester ouverte pendant toute l'injection tactile (porte média Apple).

## Échéance d'écriture de la pile TCP (Tunnel/)

- `TcpConnection.WritePatience` : le temps qu'une écriture a le droit de passer garée sur la fenêtre
  de réception du téléphone. **Illimitée par défaut** (annuaire RSD, offre média, invocations CoreDevice :
  un transfert qui abandonne est une session qui n'ouvre pas) ; **1 s** pour les canaux d'entrée
  (`InputInjector.ChannelPatience`, passé par `Rsd.OpenAsync(…, writePatience:)` sur HID universel et
  Indigo). L'attente se fait **hors** de `_gate` et hors du verrou d'émission du tunnel, que la vidéo
  partage. Au-delà : `TimeoutException`, connexion en défaut, et le `_writeLock` de `RemoteXpc` rendu
  par son `finally`. C'est ce dernier point qui manquait : le plafond de 500 ms de la pompe d'entrée
  (`InputInjector.SendPatience`) abandonne le rapport mais ne libérait pas le verrou en dessous, donc
  tout ce qui suivait restait garé — le retard de plusieurs secondes ressenti au clic.
- Fenêtre plus petite que ce qui est déjà en vol : la comparaison précède la soustraction (les deux
  membres sont non signés, la différence repassait sinon par quatre milliards d'octets de place libre).
- **Aucune exception n'est jamais poussée dans un `TaskCompletionSource`** (`TcpConnection`, `RemoteXpc`) :
  celui sur lequel personne n'est garé emporte sa faute jusqu'au finaliseur, qui la relève en « exception
  non observée » du processus des minutes plus tard (`RST recu du port 64006`, `Unable to read beyond the
  end of the stream` dans le journal). La raison est retenue dans un champ, les attentes sont réveillées
  à vide, et l'appel suivant (`ReadAsync`, `WriteAsync`, `SendDataAsync`) la lève dans la main de
  quelqu'un. Seul le canal `_inbound` garde sa raison : le runtime observe lui-même la faute de complétion
  d'un `Channel`. `TunnelNet.FireAndForget` lit la faute **avant** de décider s'il a quelque chose à dire.
- Rejoué hors ligne : `LuminaMonitor.UsbProbe tcp-selftest` (`TunnelTools`) monte la pile contre un
  téléphone de papier — deux tuyaux en mémoire, un pair minimal — et vérifie l'abandon en ~1 s, la
  libération du verrou (envoi suivant rendu immédiatement), et qu'aucune exception non observée n'atteint
  le finaliseur (`GC.Collect` + `WaitForPendingFinalizers`).

## Veille du flux et reset doux

- `StreamWatchdog` (Media/) : l'échelle, sans horloge — chaque entrée prend l'instant en argument, donc
  elle se rejoue hors ligne (`LuminaMonitor.UsbProbe watchdog-selftest`). **3 s** de silence → demande
  d'image clé (PLI) ; **2 s** de sursis → relance de la session média (sans refaire tunnel ni annuaire) ;
  **2 relances ratées d'affilée** → reset doux.
- `MediaSession` tient la veille (battement 500 ms sur le compteur de paquets) et publie `StreamStalled` ;
  `DeviceSession` décide et exécute. `StartWatch()` n'est armé qu'une fois `MediaUp` atteint.
- Reset doux (`DeviceSession`) : `UnmountAllAsync` puis remontée complète — le démontage redémarre les
  démons portés par l'image, seul remède constaté à un service d'affichage muet (débrancher le câble ne
  suffit pas : l'image reste montée). Refusé écran verrouillé → `UnlockRequired` et nouvel essai toutes
  les 3 s, borne 10 min. État `Resetting` pendant l'opération.
- Même reset après **deux ouvertures muettes d'affilée** du service d'affichage (« pas de SETTINGS en 3 s »,
  ou `IOException` immédiate). Au-delà de **3 resets** sans flux rétabli : `RestartRequired`.

## DeviceSession.ConnectAsync — séquence

1. `UsbmuxClient` : premier appareil ; `ReadPairRecord` (Apple a déjà appairé). Sinon `LuminaException("Appareil non appairé…")`.
2. `LockdownClient` + `StartSessionAsync(record)` ; `DeviceInfo` par `GetValue`.
3. Mode développeur : si l'annuaire RSD (étape 5) n'a pas `CoreDeviceProxy`… non : vérifier via `amfi` comme la commande `devmode status`. Refus → `Faulted` avec message clair.
4. `DdiManager.EnsureMountedAsync` (avant le tunnel : l'annuaire dépend de l'image montée).
5. `CdTunnel` → `TunnelNet` → `Rsd.LoadAsync`. Vérifier `Hid.ServiceName` présent, sinon `Faulted`.
6. `MediaSession.StartAsync` → `MediaUp`. `InputInjector` prêt.
Chaque étape publie `StateChanged`. Toute exception → `Faulted` + libération propre (tunnel, sockets).

## Sonde après restructuration

`Program.cs` garde toutes ses commandes ; `mount`, `tap`, `button` sont réécrites sur `DeviceSession`/`DdiManager`
(même sortie visible). Les autres commandes utilisent les briques internes déplacées (via InternalsVisibleTo).
`XipGrep`, `OpenPayload` et les commandes d'extraction restent dans la sonde (outillage), sauf `extract-devsupport`
dont le cœur devient `Ddi/DdiExtractor` (Core) appelé par la sonde — l'app de bureau en aura besoin.

## Critères d'acceptation

- `dotnet build` : 0 avertissement, 0 erreur sur les trois projets.
- Régression sur le téléphone : `catalogue` (annuaire), `mount ddi27` (doit répondre « déjà montée » sans rien
  renvoyer, image identique), `tap 39 95` (ouvre la 2e app du dock), `button home`.
- Aucun changement d'octet sur le fil : mêmes messages, mêmes ordres, mêmes délais.

## Écarts constatés à la réalisation (8 septembre 2026)

- `LuminaMonitor.Core.Hid` / `LuminaMonitor.Core.RemoteXpc` portent le même nom que les classes
  `Hid` et `RemoteXpc` : hors de leur propre dossier le nom résout vers l'espace de noms, d'où
  les alias `HidReports` et `XpcService` dans les fichiers qui les traversent.
- `DdiManager` : le constructeur prend `(long deviceId, PairRecord record)` — il rouvre une
  connexion neuve à chaque essai — et `EnsureMountedAsync(DdiSource, IProgress<string>?, CancellationToken)`
  rend un `DdiStatus` (`AlreadyMounted`, `Mounted`, plus les refus, que la sonde traduit en codes de sortie).
- `MediaSession` n'expose pas de `Codec` séparé : le dictionnaire de réponse brut est `StreamConfig`,
  le codec en sera lu à l'étape 2.
- `InputInjector` reçoit l'annuaire RSD et le canal HID ; le canal Indigo n'est ouvert qu'au premier bouton.
- `DeviceSession.ConnectAsync` publie `DdiMounted` avant `TunnelUp` (l'annuaire dépend de l'image montée),
  donc `SessionState` n'est pas monotone dans l'ordre de déclaration.
- La surface clavier vaut bien 512 (`_ServiceID` du service « CoreDevice keyboard », vérifié sur le
  téléphone) et la signature d'une entrée montée s'appelle bien `ImageSignature` : `ImageMounter` inchangé.
- `tap` et `button` passent désormais par tout l'escalier (contrôle du mode développeur, `CopyDevices`,
  flux média) ; `button` gagne le flux média qu'il n'ouvrait pas. Leur dossier DDI par défaut est `ddi27`,
  `tap` accepte un troisième argument pour en changer.
- `LuminaMonitor.Formats.csproj` gagne `InternalsVisibleTo("LuminaMonitor.Core")` : `DdiExtractor` a besoin
  de `UdifImage`, `UdifBlockDevice`, `HfsVolume` et `ApfsVolume`, qui sont internes.
- `DdiExtractor.Extract` rend un `Result` (dossier, présence et lecture du manifeste, version, nombre de
  BuildIdentities) ; les trois dernières lignes de `extract-devsupport` restent imprimées par la sonde.

## Écarts constatés à la réalisation (9 septembre 2026)

- `DdiExtractor` accepte aussi le `.xip` brut : la forme est reconnue à la magie `xar!` **et** à la
  table des matières (`Content` = archive Xcode, `Payload` = paquet), plus l'image disque en dernier
  recours. `CopyFromXcodeArchive(xip, suffixe, cible, IProgress<string>)` est public : c'est le passage
  xar → pbzx/xz → cpio qu'`extract-xip` utilisait, remonté dans Core pour ne pas l'écrire deux fois.
  Le paquet des ressources sort dans `%TEMP%\LuminaMonitor-xcode-<8 hex>` et est supprimé dans tous les
  cas — un flux cpio ne se rembobine pas, un xar cherche dans son tas, il faut donc un fichier.
- Les échecs d'extraction lèvent `LuminaException` avec le remède dedans : fichier inattendu, archive
  incomplète ou abîmée (les sommes xz), `XcodeSystemResources.pkg` absent, fichier trop court.
- `UsbmuxClient.ConnectAsync` distingue les deux façons dont le multiplexeur Apple manque : connexion
  **refusée** (rien n'écoute → « ouvre l'app Appareils Apple ou branche l'iPhone ») et connexion qui
  **expire** au bout de 5 s (le SYN part, rien ne revient → « relance l'app Appareils Apple »). Sans
  cette échéance, Windows retransmettrait le SYN une vingtaine de secondes. `LuminaException` porte
  `AppleMultiplexer` pour que la fenêtre sache offrir le bouton « Ouvrir Appareils Apple ». Le processus
  Apple n'est jamais arrêté : il détient l'interface USB et les enregistrements d'appairage.
