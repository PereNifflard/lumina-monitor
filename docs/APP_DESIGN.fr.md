# Fenêtre de pilotage — conception (étape 3)

[English](APP_DESIGN.md) · **Français**

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
| réglages `VideoPort`, `BridgeHost/Port/Token`, `AbsolutePointer` | supprimés ; ajout `DdiFolder` (dossier « copie de Restore/ »), `LastArchive` (xip ou dmg d'où la DDI a été extraite) et `UnlockCode` (vide par défaut, voir plus bas) |
| `LatencyProbe`, `Mp4Writer` | retirés de l'app (restent dans le dépôt Pi) |

## Entrées → Core
- Coordonnées : position dans le contrôle `Screen` ÷ `PictureScale` → pixels image (1328×2896 = écran entier) → normalisées 0..1.
- Bouton gauche : tap différé conservé ; tap → `TapAsync(x, y, TapPressMs)` ; appui long/glisser → `TouchDownAsync` au point
  d'origine puis `TouchMoveAsync` à chaque mouvement (limiter à ~120 Hz), `TouchUpAsync` au relâchement ou à la perte de capture.
- Molette : balayage = `DragAsync` vertical de 220 px image par cran, 90 ms, au point du pointeur. Un cran vers le bas fait défiler le contenu vers le bas, donc glisse le doigt vers le HAUT ; inversé si `InvertWheel` (faux par défaut).
- Bouton droit : `PressButtonAsync("home")`. Milieu : rien.
- Clavier : `OnPreviewKeyDown/Up` → `HidKeyboard.Translate` → ensemble des usages tenus → `session.Input.KeyboardReportAsync(usages)`
  (rapport 39 octets, surface 512). Ctrl+Alt gauche = quitter le pilotage.

## Boutons du châssis
Les lamelles dessinées font une fraction de millimètre : le clic est porté par quatre rectangles
transparents (`HitVolUp`, `HitVolDn`, `HitAction`, `HitSide`), larges comme la tranche de métal, posés
par `ShapeButtons` en même temps que les lamelles et retournés avec l'orientation.

- `MouseLeftButtonDown` marque l'événement `Handled` **avant tout**, et les rectangles vivent hors de
  la bordure de l'écran : un clic sur le métal n'est jamais un doigt sur le verre — le test de position
  d'`OnMouseDown` le rattraperait de toute façon.
- Retour visuel : `Flash` pose un `SolidColorBrush` blanc à 35 % sur le rectangle cliqué et l'anime
  vers le transparent en 280 ms (`ColorAnimation`, brosse neuve à chaque clic — une brosse figée ne
  s'anime pas ; brosse transparente et non `null` à l'arrivée, sinon le rectangle cesse de prendre les
  clics). Infobulle sur chacun des quatre.
- Volume + / − → `PressButtonAsync("volume-up" / "volume-down")` : la pastille de volume apparaît sur
  le bord gauche, mesuré le 9 septembre (`scratchpad/chassis/10-volume-up.bmp` et `11-…`).
- Le rectangle posé sur le **bouton Action** envoie `"mute"` — et c'est la touche **Muet du clavier
  média** : la pastille de volume tombe à zéro puis revient au second appui. Le bouton Action
  lui-même (bascule sonnerie/silencieux) **n'est pas atteignable** par la page Consumer ; l'infobulle
  le dit, l'étiquette d'état dit « Muet » et non « Silencieux ».
- **Bouton latéral : deux sens, comme sur le téléphone.** Écran allumé → `session.SleepScreenAsync()`
  (`lock`, page Consumer 0x30 maintenu 500 ms) ; écran éteint → `session.WakeScreenAsync()` puis
  `UnlockAsync()`.
- **`WakeScreenAsync` presse `home` (Consumer 0x40), pas 0x30**, et c'est une mesure et non un choix :
  la touche Power n'est pas une bascule. Tapée 40 ms puis remaintenue 500 ms, l'image décodée est
  restée à **0,0/255** de luminance ; `home` rallume en moins d'une seconde et les paquets
  remontent de 2/s à 66/s. Le nom `wake` a été retiré de la table des boutons plutôt que gardé
  comme une commande qui ne fait rien.
- `UnlockAsync` est la limite honnête de la fonction : réveiller est un appui et marche toujours,
  déverrouiller demande Face ID (un visage devant le téléphone) ou le code. Si `Settings.UnlockCode`
  est rempli, la fenêtre balaie vers le haut (`DragAsync(0.5, 0.94 → 0.40, 280 ms)`, un vrai glissement :
  un seul rapport se lit comme une téléportation), attend 900 ms et tape le code au clavier virtuel ;
  un code de 4 ou 6 chiffres est validé par iOS tout seul, tout autre reçoit un retour chariot. Sinon
  elle dit qu'il faut le visage ou le téléphone, et ne prétend rien avoir déverrouillé. **Le code ne
  part jamais dans le journal** : les lignes d'état ne comptent que les caractères.

## Verrouillage et session
**Ce que le verrouillage fait au flux, mesuré le 9 septembre 2026** (sonde `chassis-test`) : le flux
**ne s'arrête pas**. Après `lock`, le débit tombe de 42 paquets/s et 40 images/s à **2 paquets/s et
1 image/s d'images entièrement noires** (luminance 0,0/255), l'état reste `MediaUp`, et **aucun**
échelon de la veille n'est déclenché — 0 demande d'image clé, 0 relance de flux, 0 reset doux — parce
que des paquets continuent d'arriver. Il n'y a donc **rien à remonter** : `home` rallume l'écran et
l'image revient d'elle-même en moins d'une seconde. Le garde-fou ci-dessous existe pour le cas où un
sommeil plus long finirait par tarir complètement le flux.

`DeviceSession.ScreenAsleep` vaut vrai **uniquement** quand c'est cette session qui a endormi l'écran ;
un appui de la main sur le vrai bouton ne se voit pas d'ici, et prétendre le contraire serait pire.
Ce que ça achète : la différence entre « il n'y a plus rien à photographier » et « le miroir est
cassé », indiscernables du compteur de paquets et qui demandent des réponses opposées.

- `DeviceSession.OnStallAsync` ignore les échelons de la veille tant que `ScreenAsleep` (une image clé
  irait à un encodeur sans rien à encoder, une relance de flux dépenserait la patience du service
  d'affichage, un reset doux réclamerait justement le déverrouillage qui n'a pas eu lieu). Les échelons
  ignorés sont comptés, et `WakeScreenAsync` appelle `MediaSession.RearmWatch()` : un échelon ignoré
  laisse la veille à mi-échelle avec une issue due que personne ne rapportera jamais, donc sourde pour
  le reste de la session.
- **`WatchDarkScreen` (une fois par seconde) décide du bandeau, et il voit aussi les verrouillages
  que l'app n'a pas commandés** — c'est-à-dire le cas ordinaire, le téléphone qui se verrouille tout
  seul deux minutes après le dernier toucher. Le discriminant est le débit : un écran allumé, même
  parfaitement immobile, envoie 40 à 60 images/s ; un écran éteint en envoie **une** ; un flux
  vraiment mort n'en envoie **aucune**. Donc « il arrive entre 1 et 3 images/s depuis 4 secondes »
  = écran noir, et le seuil est loin des deux bords.
- Bandeau **`LockBanner`** au centre de l'image (« iPhone verrouillé · l'écran est éteint, le flux
  tourne au ralenti, rien n'est cassé ») avec un bouton « Réveiller l'écran ». Le pilotage est rendu
  (`Disengage`), la pastille passe à « verrouillé », et `WatchStream` **ne dit plus** « Flux arrêté » :
  c'était exactement le moment où une chose normale avait l'air d'une panne. Le bandeau disparaît
  avec la session (`Resetting`, `Faulted`, `Detached`, débranchement) — et, depuis le 11 septembre
  2026, dès que les images reviennent à la cadence d'un écran allumé, **qui que ce soit qui l'ait
  réveillé** : le bouton du bandeau, le bouton accueil du châssis, un pouce sur le téléphone,
  Face ID. `WatchDarkScreen` le dit à la session (`NoticeScreenLit`) pour que son propre drapeau
  de sommeil, seconde source du bandeau, suive l'écran plutôt que le dernier bouton pressé. La
  fenêtre de mesure repart au moment de l'endormissement, pour que les images d'avant l'appui ne
  passent pas pour un réveil.
- Le bouton latéral se décide sur `_screenDark`, pas sur `session.ScreenAsleep` : sinon un clic sur un
  téléphone qui s'est verrouillé seul enverrait `lock` sur un écran déjà éteint. `ScreenSleepChanged`
  ne fait que rendre la décision immédiate quand le sommeil vient d'ici (`DecideDarkScreen`, sans
  toucher au compteur : replier une fraction de seconde dans la fenêtre d'une seconde rendrait
  fictif le débit qu'elle mesure, et ce débit est tout l'instrument).

## Presse-papiers, dans les deux sens
Le service `com.apple.coredevice.pasteboardservice` (dialecte XPC direct, verbes `PULL`/`SET`) est
réimplémenté dans `Core/RemoteXpc/PasteboardService.cs` ; `DeviceSession` l'ouvre à la demande et
raccroche après — un presse-papiers sert quelques fois par heure, un canal tenu ouvert serait une
chose de plus à reconstruire après chaque incident.

| Geste | Chemin |
|---|---|
| **F2** / bouton « Vers l'iPhone » | `session.WritePhoneClipboardAsync(texte)` — le texte arrive entier et instantané, accents et emoji compris. **Repli** si le service refuse : la frappe caractère par caractère d'avant (`HidKeyboard.Compose`), et la barre d'état le dit, pour qu'un collage lent et lossy ne passe pas pour le rapide. |
| **F4** / bouton « Depuis l'iPhone » | `session.ReadPhoneClipboardSnapshotAsync()` → `Clipboard.SetText` ; « 42 caractère(s) copié(s) depuis l'iPhone ». |

- F4 est testé sans Alt : Windows livre Alt+F4 comme `Key.System` avec F4 dessous, et sans ce test le
  seul raccourci que tout le monde connaît pour fermer une fenêtre irait chercher un presse-papiers.
- Le presse-papiers du téléphone tient très souvent une photo : `ClipboardContent` porte le genre
  (`Nothing`, `Text`, `Image`, `Data`), l'UTI et la taille, et la fenêtre annonce « une image
  (public.png, 1,2 Mo) — non transférée » plutôt que de rendre une chaîne vide qui se lirait « il n'y
  avait rien ». Le contenu lui-même ne va **jamais** dans le journal, seulement les comptes.
- **Aucune synchronisation automatique** : rien ne part tout seul, dans aucun sens.
- Nouveau dans Core : `InputInjector.KeyboardReportAsync(IReadOnlyCollection<int> usagesHeld)` (état complet, sans réponse) ;
  `TypeAsync` s'appuie dessus. Sonde : commande `keys <texte>` pour prouver la surface 512 (on ouvre Notes et on regarde).

## Panneau Audio
Le son du téléphone, par le câble, sur la sortie Windows de son choix. `MainWindow.Audio.cs` et le
`Border` `AudioPanel` du XAML ; la chaîne elle-même est dans Core, décrite au § 11 de
[`AUDIO.fr.md`](AUDIO.fr.md). Ni micro ni Bluetooth : le téléphone n'annonce aucune capacité entrante
(§ 5) et la radio a été écartée (§ 9).

| Commande | Réglage | Effet |
|---|---|---|
| interrupteur « Son de l'iPhone » | `audioEnabled` (vrai) | ouvre ou **ferme le flux** : coupé, rien n'est négocié, rien n'est décodé, et iOS ne garde aucune session de capture |
| liste « Sortie » | `audioOutputId` (vide) | « Sortie par défaut de Windows » (rôle **Console**) puis les périphériques de rendu **actifs** ; l'identifiant stocké est celui du point de terminaison, pas son nom |
| curseur « Volume » | `audioVolume` (100) | gain appliqué aux échantillons dans l'app, **jamais** le mélangeur système — ce périphérique est partagé avec le reste de la machine. Pas de bascule Muet : le curseur à zéro en est une, et un panneau de trois réglages se lit d'un coup d'œil |

**Faire taire le téléphone n'a pas d'interrupteur à soi** : le son joue ici ou sur le téléphone,
jamais les deux, et c'est l'interrupteur du haut du panneau qui décide. Activer le son de l'iPhone
presse la touche Muet du téléphone ; le couper lui rend son son (`AudioOptions.SilencePhone` suit
`audioEnabled`). Coût connu, mesuré le 11 septembre 2026 : avec le miroir actif, un événement de
volume/muet Consumer fait que certaines apps — Apple Music au premier chef — cessent d'alimenter la
capture audio définitivement. Ces apps-là sont de toute façon déjà réduites au silence par le miroir
lui-même (voir `AUDIO.fr.md`, « Apple Music »), donc l'interrupteur ne leur coûte rien qu'elles
avaient.
| curseur « Retard » 0–300 ms | `audioDelayMs` (50) | remplissage cible du tampon de gigue ; c'est le réglage qui aligne les lèvres |

- **Les valeurs ne sont écrites qu'à la fermeture du panneau** (plus les deux interrupteurs, qui sont
  rares) : un curseur traîné d'un bout à l'autre lève cent événements, et cent remplacements atomiques
  de fichier pour un geste, c'est un disque puni pour rien. La chaîne vivante, elle, est prévenue
  immédiatement — `DeviceSession.AudioOptions` s'applique à chaud.
- **Les contrôles sont remplis avec les gestionnaires en veilleuse** (`_audioFilling`) : un curseur à
  qui l'on pose sa valeur pendant que son gestionnaire est vif réenregistre par-dessus celle qu'on
  vient de lui donner.
- **La liste des sorties est lue sur un autre fil** (`Task.Run`) : l'énumération MMDevice est du COM et
  ce code tourne sur le fil de la fenêtre. Elle est reconstruite au changement de langue, sa première
  entrée étant une phrase.
- **Ligne d'état, 4 Hz au plus**, dans l'ordre des questions qu'on se pose : coupé → pas de miroir →
  ouverture → refusé (avec le motif du démon et un bouton **Réessayer**, seul endroit où il paraît) →
  lecture sur *périphérique*, file *n* ms, et le nombre de coupures s'il y en a.
- Styles : `ToggleSwitch`, `PanelCombo`, `PanelButton` existaient déjà (reliquats du pont Bluetooth) ;
  `PanelSlider` est neuf dans `App.xaml`, modèle repris en entier comme pour `Button` — Aero2 dessine
  un rail gris clair qu'aucune couleur ne reprend. Les entrées de la liste se présentent par
  `ToString()`, sans `DisplayMemberPath` : rien à faire réfléchir sur un type privé.

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

Chaque ligne porte un bloc **`AUDIO`** — flux ouvert/refusé/coupé, paquets, trames décodées et en
échec, pertes, désordre, sous-alimentations, sauts, remplissage de la file face à sa cible,
périphérique et format actifs, écart son-image. Le format vient de `AudioStats.ToString()`, un seul
endroit partagé avec la sonde. C'est la seule trace du son quand personne n'a ouvert le panneau, et
le diagnostic de synchro ajoute sa propre ligne toutes les cinq secondes.

## Critères d'acceptation
- Build 0 avertissement ; `--diagnostic 20` : atteint `MediaUp`, ≥ 30 images/s présentées, aucune exception dans le journal.
- Sonde `keys bonjour` : le texte apparaît dans Notes (constat de visu).
- Manuel (à l’œil) : tap, appui long, glisser, molette, boutons, frappe ; latence perçue ; orientation paysage.
- Sonde `chassis-test` : une image décodée par bouton, luminance moyenne à côté — un écran éteint et un
  flux arrêté se ressemblent dans un journal et jamais dans une image. Puis deux verrouillages, un court
  et un long, avec les débits seconde par seconde.
- Sonde `clipboard` puis `clipboard <texte>` : lecture, écriture, relecture (l'écriture sans relecture
  ne prouve rien).
