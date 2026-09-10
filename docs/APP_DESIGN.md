# Fenêtre de pilotage — conception (étape 3)

Base : l'app WPF d'un projet antérieur du même auteur, qui affichait le même châssis mais passait par un
Raspberry Pi servant de pont Bluetooth. Elle est reprise dans `src/LuminaMonitor.App` et rebranchée sur
`LuminaMonitor.Core`. Tout ce qui parlait au Raspberry Pi disparaît ; la logique d'interface (châssis,
géométrie, rendu, tap différé, réglages) est conservée.

## Ce qui est repris tel quel
- `MainWindow.xaml` (châssis, île, boutons dessinés, panneau de diagnostic), `DeviceGeometry.cs` (géométrie physique,
  `FitPicture`, `Detect` de l'orientation par les pixels), le rendu (`WriteableBitmap` Bgr32, échange `_pending`/`_present`
  sous verrou — le tampon échangé porte du NV12, converti dans le tampon arrière au tick —, `CompositionTarget.Rendering`,
  `PictureScale`), le tap différé (`TapDeferMs` 180, `TapPressMs` 40,
  `TapSlopWindowPx` 5, `FlushPendingPress`, `OnLostMouseCapture`), `Settings.cs` (JSON atomique dans `%APPDATA%\LuminaMonitor`).
- `HidKeyboard.cs` : mapping touches Windows → usages HID physiques, modificateurs, touches mortes, `Compose` pour le collage.

## Ce qui est remplacé
| Avant (Pi) | Après (Core) |
|---|---|
| `VideoReceiver` → `OnFrame(bgra, w, h, stride)` | `DeviceSession.FrameDecoded(VideoFrame)` ; `frame.Bgra` (conversion NV12→BGRA paresseuse, sur le fil de décodage) puis copie dans `_pending` |
| `InputBridgeClient` (TCP 9 octets, 0..32767) | `session.Input` : `TouchDownAsync/MoveAsync/UpAsync`, `TapAsync`, `DragAsync`, `PressButtonAsync`, `KeyboardReportAsync` (nouveau, voir plus bas) |
| mode relatif, ré-ancrage, `Nudge`, `KeepAwake`, `PingBridge`, `KeepBridgeConnected` | supprimés (le pointeur est absolu par nature ; la session Core gère l'état) |
| réglages `VideoPort`, `BridgeHost/Port/Token`, `AbsolutePointer` | supprimés ; ajout `DdiFolder` (dossier « copie de Restore/ ») et `LastArchive` (xip ou dmg d'où la DDI a été extraite) |
| `LatencyProbe`, `Mp4Writer` | retirés de l'app (restent dans le dépôt Pi) |

## Entrées → Core
- Coordonnées : position dans le contrôle `Screen` ÷ `PictureScale` → pixels image (1328×2896 = écran entier) → normalisées 0..1.
- Bouton gauche : tap différé conservé ; tap → `TapAsync(x, y, TapPressMs)` ; appui long/glisser → `TouchDownAsync` au point
  d'origine puis `TouchMoveAsync` à chaque mouvement (limiter à ~120 Hz), `TouchUpAsync` au relâchement ou à la perte de capture.
- Molette : balayage = `DragAsync` vertical de 220 px image par cran, 90 ms, au point du pointeur. Un cran vers le bas fait défiler le contenu vers le bas, donc glisse le doigt vers le HAUT ; inversé si `InvertWheel` (faux par défaut).
- Bouton droit : `PressButtonAsync("home")`. Milieu : rien.
- Boutons dessinés sur le châssis (XAML) : clic = `PressButtonAsync` (`volume-up`, `volume-down`, `lock`, `mute` si présent).
- Clavier : `OnPreviewKeyDown/Up` → `HidKeyboard.Translate` → ensemble des usages tenus → `session.Input.KeyboardReportAsync(usages)`
  (rapport 39 octets, surface 512). Collage (F2) → `HidKeyboard.Compose(texte)` → séquence de rapports. Ctrl+Alt gauche = quitter le pilotage.
- Nouveau dans Core : `InputInjector.KeyboardReportAsync(IReadOnlyCollection<int> usagesHeld)` (état complet, sans réponse) ;
  `TypeAsync` s'appuie dessus. Sonde : commande `keys <texte>` pour prouver la surface 512 (on ouvre Notes et on regarde).

## Session
- Au lancement et à chaque branchement (`UsbmuxClient` Listen : Attached/Detached) : `DeviceSession.ConnectAsync`.
- `StateChanged` → barre d'état (« Branché », « Appairé », « Image développeur… », « Tunnel », « Miroir », « Erreur : … »).
- `UnlockRequired` → bandeau « Déverrouille l'iPhone » sur l'image ; disparaît au changement d'état suivant.
- `Faulted` → message, nouvelle tentative 5 s plus tard (délai doublé à chaque échec, plafond 30 s),
  indéfiniment tant que l'appareil est branché. Tant qu'une session existe et n'est pas `Faulted` —
  y compris pendant un `Resetting` — aucune deuxième session n'est ouverte : un second flux média
  figerait le service d'affichage.
- `Resetting` → « Relance du miroir… » ; l'injecteur est relu à chaque `MediaUp`, car un reset doux
  remonte l'escalier tout seul et en fabrique un nouveau.
- `RestartRequired` → bandeau « Redémarre l'iPhone » (persistant, l'état est `Faulted`).
- Première exécution (pas de `DdiFolder`) : boîte de dialogue « Image développeur » : choisir un `.xip` Xcode,
  un `.dmg` Device Support ou un `.pkg` → `DdiExtractor` vers `%APPDATA%\LuminaMonitor\ddi\<ProductBuildVersion>`
  (progression ligne à ligne dans la barre d'état, ~1 min pour un xip). Le dossier d'étape est vidé avant
  chaque tentative : la moitié d'une image plus ancienne ressemblerait trop à une image entière. En cas
  d'échec, le message dit lequel — fichier inattendu, archive incomplète, paquet absent.
- Multiplexeur Apple absent ou figé : bandeau (le même que « Déverrouille l'iPhone ») avec le texte du refus
  **et** un bouton « Ouvrir Appareils Apple », qui lance `explorer.exe shell:AppsFolder\AppleInc.AppleDevices_nzyj5cx40ttqa!App`.
  Ce bouton n'apparaît que pour cette panne-là (`LuminaException.AppleMultiplexer`), parce que c'est la seule
  qui se répare d'un clic. L'app n'arrête jamais le processus Apple.

## Journal
`%APPDATA%\LuminaMonitor\lumina.log`, **toujours**, pas seulement en diagnostic : écriture asynchrone
(file bornée + fil dédié, une ligne perdue plutôt qu'une fenêtre figée), horodatage à la milliseconde,
rotation à 5 Mo vers `lumina.1.log`. On y trouve toutes les transitions d'état, les lignes de Core, les
décisions de reconnexion, les compteurs média toutes les 10 s quand `MediaUp`, et **toute exception non
gérée** avec sa pile — les trois portes sont surveillées dans `App.xaml.cs` (`AppDomain.UnhandledException`,
`DispatcherUnhandledException`, `TaskScheduler.UnobservedTaskException`), chacune vidant le journal sur
le disque avant de laisser partir le processus. C'est la réponse au symptôme « l'image disparaît sans
trace » et « le processus s'est terminé sans rien dans l'Observateur d'événements ».

## Mode diagnostic
`LuminaMonitor.App.exe --diagnostic <secondes>` : fenêtre normale, même journal mais verbeux (une ligne
par seconde : états, images/s reçues et présentées, latence de présentation, erreurs), fermeture
automatique à l'échéance. Sert aux tests sans personne devant l'écran.

## Critères d'acceptation
- Build 0 avertissement ; `--diagnostic 20` : atteint `MediaUp`, ≥ 30 images/s présentées, aucune exception dans le journal.
- Sonde `keys bonjour` : le texte apparaît dans Notes (constat de visu).
- Manuel (à l’œil) : tap, appui long, glisser, molette, boutons, frappe ; latence perçue ; orientation paysage.
