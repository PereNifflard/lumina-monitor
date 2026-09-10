# Architecture — pilotage USB d'un iPhone depuis Windows, sans code tiers

Verdict de l'étude de faisabilité du 8 septembre 2026 (15 agents, sources
primaires : pymobiledevice3 HEAD 1ee3dd9, libimobiledevice, go-ios, docs Apple),
recoupé avec ce qui est déjà **prouvé sur la machine du projet**.

## La pile, du câble au tap

| # | Couche | Ce que c'est | État | Difficulté |
|---|---|---|---|---|
| 1 | usbmux | multiplexeur Apple (TCP 127.0.0.1:27015, plist XML, port inversé) | **fait, prouvé** | facile |
| 2 | lockdown | identité, StartSession, TLS mutuel avec l'appairage réutilisé, StartService | **fait, prouvé** | facile |
| 3 | Mode développeur | service `com.apple.amfi.lockdown`, action 0/1/2 | **fait** (reveal prouvé) | facile |
| 4 | DDI | monter l'image développeur personnalisée (`mobile_image_mounter`, TSS Apple) | **fait, prouvé** (17B48 puis Xcode 27 bêta 6 montées) | moyen, fragile |
| 5 | tunnel | `CoreDeviceProxy` par lockdown (iOS 17.4+) : CDTunnel + paquets IPv6 bruts | **fait, prouvé** | moyen |
| 6 | pile IP | TCP/IPv6 en espace utilisateur, client seul, un seul pair | **fait, prouvé** | moyen |
| 7 | RSD + RemoteXPC | annuaire (port 58783) + HTTP/2 minimal + codec XPC | **fait, prouvé** | moyen |
| 8 | porte média | flux DisplayService obligatoire, sinon les rapports HID sont ignorés | écrite, prouvée au dialogue, refusée par iOS 26 → iOS 27 requis | dur, verrouillé (iOS 27) |
| 9 | HID | `universalhidservice` : surface 257, rapport 58 octets, absolu 0..65535 ; `indigo` : boutons, page Consumer 0x0C | boutons PROUVÉS sur iOS 26 ; toucher absolu en attente d'iOS 27 | facile (boutons faits ; toucher : iOS 27) |

## La découverte qui change tout : CoreDeviceProxy

Sur iOS 17.4+, lockdown expose `com.apple.internal.devicecompute.CoreDeviceProxy`.
Le tunnel CoreDevice s'ouvre **dans notre session TLS lockdown existante**, sans
prompt, et **sans** l'appairage distant (SRP, Ed25519, X25519), **sans** QUIC,
**sans** TLS-PSK — précisément les quatre pièces que .NET n'a pas en boîte et
qu'il aurait fallu écrire à la main. Poignée de main : `CDTunnel` + longueur
16 bits BE + JSON `{"type":"clientHandshakeRequest","mtu":16000}` → adresse IPv6
du téléphone + port RSD ; ensuite des paquets IPv6 préfixés par leur longueur.

Tout ce qui est au-dessus est du code pur : une petite pile TCP cliente, un
HTTP/2 réduit à deux flux, un codec XPC entièrement spécifié
(magie 0x29b00b92 / 0x42133742, version 5, étiquettes de type), des dictionnaires.

## RemoteXPC : le canal de réponse n'est pas le flux racine

Leçon apprise ce soir en dialoguant avec les portes média et HID : les
démons CoreDevice répondent sur le **flux HTTP/2 n° 3** (le canal de
réponse), pas sur le flux racine où part la requête. La pompe de lecture
accepte désormais tout dictionnaire XPC non vide quel que soit le numéro de
flux, comme le fait la référence.

## Les deux vrais murs — et ils ne sont pas cryptographiques

**1. L'image développeur (DDI) — mur tombé le 8 septembre.** Le démon HID
(`dtuhidd`) vit *dans* l'image développeur d'Apple : sans image montée, le
service n'existe pas. La chaîne pour l'obtenir et la monter est maintenant
écrite et vérifiée sur un téléphone réel, avec des lecteurs maison de
bout en bout (UDIF, HFS+, APFS, xar, pbzx/xz, cpio) :

- Extraction : les **trois** formes qu'Apple distribue entrent dans
  `extract-devsupport <xip|dmg|pkg> [dossier]` — l'archive Xcode entière
  (`.xip`, ~2 Go), le composant « Device Support » (DMG UDIF/HFS+), ou
  `XcodeSystemResources.pkg` lu en xar directement. Le format est reconnu aux
  quatre premiers octets et à la table des matières de l'archive : `xar!` +
  entrée `Content` = Xcode, `xar!` + entrée `Payload` = paquet, le reste est
  tenté en image disque. Pour un `.xip`, `DdiExtractor.CopyFromXcodeArchive`
  fait un passage xar → pbzx/xz → cpio jusqu'à
  `*/Contents/Resources/Packages/XcodeSystemResources.pkg` (le nom de l'app
  varie : `Xcode.app` ou `Xcode-beta.app`), écrit ce paquet (~145 Mo) dans un
  dossier temporaire — un xar cherche dans son tas, un flux cpio ne revient
  pas en arrière — et la chaîne existante reprend. Environ une minute, ~4 Go
  parcourus, progression au gigaoctet.
- Puis, à partir du paquet : `Payload` (pbzx/xz, cpio) →
  `Library/Developer/CoreDevice/CandidateDDIs/iOS_DDI.dmg` (UDIF/HFS+) → copie
  fidèle de `/Restore/` (`BuildManifest.plist`, `Restore.plist`, deux images
  `.dmg` — une « PersonalizedDMG », une cryptex —, `Firmware/*.dmg.trustcache`
  + `.cryptex_info`/`.root_hash`). `extract-xip` sort une entrée nommée de
  l'archive (même passage, exposé pour l'outillage), `grep-xip` cherche des
  chaînes dans tout le xip en un passage (~2 min pour 9,4 Go).
- Échecs nommés, parce que c'est la première chose que fait un nouvel
  utilisateur : fichier qui n'est aucun des trois formats, archive tronquée
  (les sommes xz le disent), paquet des ressources absent de l'archive.
- Sélection image/trustcache : jamais par extension. `mount` lit la
  BuildIdentity du téléphone (ApBoardID/ApChipID — ex. iPhone 17 Pro Max =
  v54ap, 0x0E/0x8150) et prend `Manifest.PersonalizedDMG.Info.Path` /
  `Manifest.LoadableTrustCache.Info.Path` dans `BuildManifest.plist`.
- Signature TSS épinglée : `gs.apple.com` chaîne vers une racine privée
  (« Apple Root CA », SHA-1 `611E5B662C593A08FF58D14AE22452D198DF6C60`),
  absente des magasins Windows. La racine officielle (apple.com/certificateauthority,
  empreinte vérifiée contre celle du serveur) est embarquée dans `Tss.cs` et
  validée en mode CustomRootTrust — la vérification TLS reste active, seule
  la racine de confiance change. Ticket `ApImg4Ticket` reçu : 3045 octets.
- Démon de montage : `com.apple.mobile.mobile_image_mounter` ne sert qu'un
  client à la fois — la connexion de `QueryPersonalizationManifest` doit être
  fermée avant d'en ouvrir une autre, et nonce + TSS + `ReceiveBytes` +
  `MountImage` tiennent sur la connexion suivante, seule. `ReceiveBytes`
  répond `DeviceLocked` (et coupe) tant que l'écran est verrouillé : `mount`
  réessaie toutes les 3 s pendant 10 min, connexion neuve à chaque essai. Une
  image à la fois : `mount` démonte d'abord celle en place, et attend le
  déverrouillage aussi à cette étape. Fragilité observée : un envoi d'image
  s'est figé une fois juste après un `UnmountImage` sur la même connexion ;
  parade ajoutée (délai d'écriture de 30 s, progression journalisée par
  mégaoctet) et confirmée par une relance sur connexion neuve.

Résultat : image **17B48** montée le 08/09/2026 à 20:32 (première DDI montée
par ce code), annuaire RSD passé de 61 à 72 services. Mais ni 17B48
(Xcode 26.0.x) ni 17F113 (Xcode 26.6, `ddi26/`) n'embarquent `dtuhidd` ni
`com.apple.coredevice.displayservice` : ces démons arrivent avec l'outillage
**Xcode 27 / iOS 27** (DDI de référence 27A5228h, bêta 6 = 27A5252f du
24/08/2026). (Le canal MobileAsset/Pallas, sondé en parallèle, ne référence
aucun type d'asset DDI connu — impasse pour l'instant.)

**Suite, le soir même (21:11) : mur 1 entièrement tombé.** DDI Xcode 27
bêta 6 (27A5252f) montée sur l'iPhone 17 Pro Max (iOS 26.6.1), extraite de
`Xcode_27_beta_6.xip` (paquet imbriqué
`Xcode-beta.app/Contents/Resources/Packages/XcodeSystemResources.pkg`,
145 Mo) vers `ddi27/`. L'annuaire RSD passe à 82 services, dont
`com.apple.coredevice.hid.universalhidservice`, `com.apple.coredevice.hid.indigo`,
`com.apple.coredevice.hid.universalhid`, `com.apple.coredevice.displayservice`
et `com.apple.coredevice.screencaptureservice` — confirmé au `catalogue`.
Le démon HID est joignable : `connectedServices` renvoie les surfaces 257
(écran tactile CoreDevice, Built-In), 512 (clavier CoreDevice déjà
enregistré), 1026 (`mainScreenButtons`, `Authenticated = true`) et 1280
(`avpCustom`). Le rapport tactile 58 octets envoyé vers 257 est accepté sans
erreur (fire-and-forget) — mais accepté ne veut pas dire authentifié : voir
mur 2, juste en dessous.

**2. La porte média — dialoguée, mais verrouillée par iOS.** Avec la DDI
Xcode 27 montée, la porte média répond enfin : `startmediastream` (le service
qui authentifie l'écran tactile) renvoie `CoreDeviceError 9021` : « Remote
control requires iOS 27.0 or later on this device ». Sur iOS 26.6.1, la
surface tactile (257) reste donc non authentifiée et tout rapport HID qui lui
est envoyé est ignoré sans erreur — exactement le mode d'échec silencieux
pressenti. Le toucher absolu exige donc **iOS 27**, dont la sortie publique
est attendue mi-septembre 2026 (on installe en attendant la bêta
développeur).

Correction : l'étude de faisabilité initiale situait ce mécanisme entre
iOS 17 et iOS 26 — à tort : la référence avait en réalité été relevée sur des
bêtas d'**iOS 27**.

Diagnostic : `tap` continue de fonctionner sans flux vidéo si la porte média
est refusée (utile pour observer si des rapports passent malgré tout), et
n'appelle `stopmediastream` que si le flux a effectivement démarré.

Retournement toujours valable dès iOS 27 : ce flux vidéo obligatoire **est**
un miroir d'écran par USB. S'il se décode (l'étude signale un HEVC non
conforme que seul le décodeur d'Apple rend proprement), il remplace AirPlay +
le Pi pour l'image aussi.

## Version d'iOS requise

- **Boutons** (porte Indigo) : iOS 26 et plus — prouvé sur iOS 26.6.1.
- **Toucher absolu** (écran tactile, porte média) : iOS 27 et plus — refusé
  sur iOS 26.6.1 (`CoreDeviceError 9021`).
- **Image développeur (DDI)** apportant `dtuhidd` / `universalhidservice` /
  `displayservice` : Xcode 27 (bêta 6 = 27A5252f) ; les DDI Xcode 26.0.x
  (17B48) et Xcode 26.6 (17F113) en sont dépourvues.

## Latence

Par événement : excellente (rapports XPC sans réponse attendue, RTT du lien
~7 ms, sous les 10-30 ms d'un HID Bluetooth). Le coût est à l'établissement
(tunnel + montage + flux média, secondes) — on garde une session chaude.

## HID : format des rapports et boutons (pymobiledevice3 hid_service.py ; dialogue Indigo en direct)

- **Boutons (porte `com.apple.coredevice.hid.indigo`), PROUVÉ sur iOS 26.6.1** :
  message `{messageType: "IndigoButtonEvent", payload: {state, usagePage,
  usageCode} (UInt64), featureIdentifier:
  "com.apple.coredevice.feature.remote.hid.button"}`, page Consumer `0x0C` :
  home `0x40` (appui 50 ms), lock `0x30` (500 ms), volume-up `0xE9`,
  volume-down `0xEA`, mute `0xE2`, siri `0xCF` (1 s). Sans flux vidéo.
  Confirmé à l’usage (volume monté, bouton principal ramène à
  l'accueil) — première commande réelle du téléphone par le PC en code
  maison. Commande sonde : `button <home|lock|volume-up|volume-down|mute|siri> [...]`.
- Toucher absolu, surface `257` : 58 octets, `[0]=0x09`, `[1]=0x01 [2]=0x05`,
  `[3]=0xC2` contact / `0x02` relâché, `[4..5]` X, `[6..7]` Y (UInt16 LE,
  0..65535 normalisés, origine en haut à gauche), `[44..49]` horodatage 48 bits.
  Accepté par le démon sans erreur (mur 1) mais sans effet tant que la
  surface n'est pas authentifiée (mur 2, iOS 27 requis).
- Clavier : `createService` d'un clavier virtuel (typage Swift-Codable strict,
  la partie la plus fragile), puis rapports bitmap de 30 octets.
- Envoi : `{"featureIdentifier":"com.apple.coredevice.feature.remote.universalhidservice",
  "messageType":"Request","payload":{"send":{"_0":<DATA>,"_1":<UINT64 surface>}}}`
  sans attendre de réponse.

## Taille honnête

Couches 4 à 9 : plusieurs semaines de travail effectif, chaque couche
vérifiable isolément (la 4 est tombée le 8 septembre dans la journée, les
boutons de la couche 9 le soir même — voir plus haut). Fragilité : format
des rapports et porte média relevés par capture (à revalider par version
majeure d'iOS) ; image DDI par version majeure ; signature TSS en ligne.

## Ce qu'on a écarté

- Tunnel QUIC (iOS 17.0-18.1) : `System.Net.Quic` n'expose pas les datagrammes,
  msquic.dll n'est pas en boîte → impossible sans binaire tiers. Inutile : 18.2+
  utilise TCP, et CoreDeviceProxy contourne tout.
- USB brut (WinUSB) : exigerait de **remplacer** le pilote Apple par un pilote
  générique tiers — contraire à la règle du projet, et bien plus dur.
- Voie Bluetooth : voir README (deux murs vérifiés).

## Crédits et sources

Le protocole décrit dans ce document n'a pas été deviné. Apple ne le documente
pas ; ce qu'on en sait vient de la communauté, qui a publié ses relevés :

- [**pymobiledevice3**](https://github.com/doronz88/pymobiledevice3) (HEAD
  1ee3dd9 au moment de l'étude) — source principale pour les couches 2 à 9 :
  `lockdown`, `mobile_image_mounter` et TSS, `CoreDeviceProxy` et le tunnel,
  RSD, RemoteXPC, `hid_service.py` pour le format des rapports HID.
- [**libimobiledevice**](https://libimobiledevice.org/) — usbmux, appairage,
  `lockdownd` : la description la plus ancienne et la mieux éprouvée.
- [**go-ios**](https://github.com/danielpaulus/go-ios) — seconde lecture des
  mêmes couches.
- Documentation Apple publique : mode développeur, plans cotés des appareils,
  note technique TN1150 (HFS+).
- Spécifications de formats : xz, LZMA2/SDK 7-Zip, xar, cpio, UDIF ; RFC 6184
  (FU-A H.264), RFC 7798 (FU HEVC), RFC 9293 (TCP).

Ce que ce dépôt apporte est une **réimplémentation en C#**, sans code tiers, et
les constats faits sur un appareil réel qui sont notés dans les tableaux
ci-dessus (murs iOS 26/27, porte média obligatoire, comportement du service
d'affichage après une coupure). Aucune ligne des projets ci-dessus n'est reprise
ici — et rien de ce qui précède n'aurait été trouvé sans eux.
