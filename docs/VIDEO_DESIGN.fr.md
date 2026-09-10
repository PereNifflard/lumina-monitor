# Miroir vidéo USB — conception (étape 2)

[English](VIDEO_DESIGN.md) · **Français**

Le téléphone envoie déjà son écran en RTP à travers notre pile UDP/IPv6, dès que la porte média est ouverte.
Cette étape transforme ces paquets en images décodées, avec les seuls moyens de Windows (Media Foundation),
sans bibliothèque tierce. Faits mesurés le 9 septembre 2026 (sonde `stream-info`, captures `scratchpad/ref/*.rtp`).

## Faits établis

- **Les deux banques codec de `MediaOffer` sont nommées à l'envers.** Banque payload type **123** (features `FLS;SW:1`)
  = **H.264/AVC** (charge RTP `3C 81 …` = FU-A type 28, RFC 6184). Banque **100** (`VRAE:0;SW:1;FLS`) = **HEVC**
  (charge `62 01 81 …` = FU type 49, RFC 7798). Le téléphone prend la banque 100 (HEVC) quand les deux sont offertes.
- Offre « banque 123 seule » → **H.264**, ~3,3 Mbit/s écran statique, plafond négocié 6 Mbit/s, 1328×2880, 60 fps annoncés,
  NV12, SRTP désactivé (flux en clair), `KeyFrameInterval = 0` : **une seule image clé, au tout début**.
- En-tête RTP : version 2, extension présente (profil 0x9001, 1 mot de 32 bits) — à sauter avant la charge.
- **RTCP** : le téléphone envoie un SR (PT 200) chaque seconde, multiplexé sur le même port UDP que le RTP
  (`RTCPSendInterval = 1`, `RTCPTimeoutInterval = 20`). **Sans rapport de réception de notre part, le flux s'arrête
  après 20 s** (mesuré : 7095 paquets en 35 s ≈ 19,4 s de flux, 20 SR reçus). Il faut donc émettre un RR toutes les secondes.
- HEVC porte un suffixe propriétaire par NAL de tranche (`04 f0 0a c0 00 00 03 00 00 04 ec 0a b0 03`, référence pymobiledevice3) ;
  vérifier si H.264 en porte un et le retirer le cas échéant.

## Décisions

1. **H.264 uniquement** : décodeur Media Foundation présent sur tout Windows 10/11 ; HEVC dépend d'une extension Store.
   `DeviceSession` offre donc la banque 123 seule. Renommer dans `MediaOffer` : `AvcFeatures = "FLS;SW:1;"` (PT 123),
   `HevcFeatures = "FLS;VRAE:0;SW:1;"` (PT 100), enum `VideoCodecs` corrigée ; `SelfCheck` (gabarit Xcode, banques 123 puis 100)
   doit rester identique octet pour octet.
2. **Pipeline** dans `LuminaMonitor.Core/Media/` :
   - `RtpPacket` (parse en-tête, CSRC, extension, marker, seq, timestamp, SSRC, charge) ; RTCP reconnu par PT 200–206.
   - `H264Depacketizer` (RFC 6184) : NAL simple (1–23), STAP-A (24), FU-A (28) ; réassemblage par seq ; une **unité d'accès**
     par marker ; format Annex B (`00 00 00 01` + NAL) ; SPS/PPS gardés et réinjectés devant chaque IDR ; tout jeter jusqu'au
     premier IDR ; détecter perte (saut de seq) → marquer l'unité d'accès corrompue et demander une image clé.
   - `RtcpSession` : parse SR/SDES/BYE ; envoie chaque seconde un RR (PT 201, SSRC de l'émetteur, fraction perdue, jitter,
     LSR/DLSR) + SDES CNAME, vers l'adresse/port d'où viennent les SR (mux). Envoie un **PLI** (PT 206, FMT 1) ou **FIR**
     (FMT 4) à la demande (perte, ou abonné tardif). Vérifier sur le téléphone que le flux dépasse 20 s avec RR, et qu'un PLI
     provoque bien une nouvelle image clé (SPS/PPS/IDR).
   - `H264Decoder` : Media Foundation, `CLSID_CMSH264DecoderMFT`, interop COM écrit à la main (`IMFTransform`, `IMFMediaType`,
     `IMFSample`, `IMFMediaBuffer`, `MFStartup`), entrée `MFVideoFormat_H264` Annex B, sortie **NV12**, `CODECAPI_AVLowLatencyMode = 1`,
     gestion de `MF_E_TRANSFORM_STREAM_CHANGE` (taille) ; un thread de décodage, file bornée (abandon des unités en retard).
   - `Nv12ToBgra` (SIMD `System.Numerics`/`Vector`) → `VideoFrame(width, height, stride, byte[] Bgra, timestamp)`.
   - `MediaSession` : arme la réception **avant** `startmediastream` (aucun paquet initial perdu), expose `FrameDecoded`
     (VideoFrame), `RtpPacket` (brut), statistiques (pps, fps, pertes, retard de décodage), `RequestKeyFrameAsync()`.
3. **Outils de sonde** : `stream-info` capture dès l'armement (avant le démarrage) ; `decode-capture <fichier.rtp> <n> <sortie.bmp>`
   rejoue une capture hors ligne, décode, écrit la n-ième image en BMP 24 bits (écrit à la main, sans System.Drawing) et affiche
   le débit de décodage (images/s) ; `mirror-test [secondes]` sur le téléphone : session complète, décodage en direct,
   compte les images et écrit la dernière en BMP.

## Ce que l'étape 2 a mesuré (9 septembre 2026, capture `capture_h264_start.rtp`)

- **Les jeux de paramètres ne sont pas en bande.** Le tout premier paquet RTP (138 octets, bit *forbidden* à 1,
  donc jamais une NAL) est une entrée d'échantillon ISO `avc1` contenant une boîte `avcC` : profil 0x64, niveau 51,
  **SPS** `27 64 00 33 4B 04 C5 14 05 30 16 BA 6E 04 04 04 04` et **PPS** `28 4A E3 CB`. L'entrée donne aussi la
  géométrie : **1328 × 2896** (et non 2880). Un récepteur qui ignore ce paquet a une IDR indécodable.
- Le paquet suivant ouvre l'IDR en FU-A (`3C 85`), 33 paquets, 37 645 octets ; ensuite une image P par unité d'accès.
- **Suffixe propriétaire en H.264 : 10 octets, `00 00 03 00 00 05 28 0B 34 02`**, présent sur *toutes* les NAL de
  tranche (IDR comprise), identique d'une session à l'autre — l'analogue du suffixe HEVC de 14 octets, pas le même motif.
- **RTCP : le PT occupe les 8 bits du deuxième octet.** Masquer par 0x7F transforme un SR (200) en PT 72 et livre le
  paquet de contrôle au dépaquetiseur, dont le champ longueur se lit comme un numéro de séquence délirant.
- Avec un RR + SDES par seconde vers le `SourcePort` de la réponse, le flux tient : 60 s, 4143 paquets, 60 SR reçus,
  0 perte, 3520 images décodées (58,6 img/s), latence dernier paquet → image décodée ≈ 0,4 ms.
- Le décodeur MFT réutilise notre tampon de sortie : **il faut remettre `SetCurrentLength(0)` avant chaque
  `ProcessOutput`**, sinon le deuxième appel répond un E_FAIL nu.

## Étape 3 — le retard proportionnel au mouvement (outillage, 9 septembre 2026)

Hypothèse : le miroir prend du retard **en proportion de ce qui bouge à l'écran**, parce que l'encodeur du
téléphone, plafonné vers 6 Mbit/s, met les images en file quand la scène change beaucoup. Le transport (RTT
1 ms) et le décodage (< 1 ms) sont déjà hors de cause, donc la mesure porte sur la seule chose qu'ils
n'expliquent pas : l'écart entre l'instant où une image a été prise, que dit son horodatage RTP, et celui où
elle arrive.

- **`motion-test [secondes] [default|half|bitrate:N|rctl]`** (sonde) : session complète avec décodage, 3 s de
  repos, N s de glisser vertical continu au centre (600 px en 500 ms, aller-retour sans pause, 120 positions/s
  par la file de l'injecteur), 3 s de repos. Une ligne par seconde : paquets/s, kbit/s, images reçues et
  décodées, taille moyenne et maximale d'une image, **dérive** (temps écoulé chez nous moins temps écoulé selon
  les horodatages, tous deux comptés depuis la première image), gigue, pertes, et l'écart entre l'horodatage de
  la dernière image et maintenant. Bilan : dérive maximale au repos contre sous mouvement. Une dérive qui monte
  de plusieurs centaines de ms sous mouvement et retombe au repos = file dans l'encodeur du téléphone.
- **Ce que l'offre permet vraiment.** Le schéma `VCMediaNegotiationBlobVideoSettings` ne contient que deux
  champs de résolution, `f4 customVideoWidth` et `f5 customVideoHeight`, tous deux **absents de la capture
  Xcode** : `VideoOfferOptions.MaxWidth/MaxHeight` les émet, la réaction du téléphone est inconnue. Les
  `ResEntry` des banques de codec ne sont **pas** une table de résolutions (même identifiant de capacité 50115
  partout). Il n'existe **aucun** champ de débit maximal : seulement la table de paliers `f9`, dont les entrées
  de type 0 sont des plafonds réseau de 6 à 100 Mbit/s ; `MaxBitrateKbps` élague cette table. Il n'existe
  **aucun** champ de cadence d'images — d'où l'absence de `FramerateCap`.
- **RCTL** (`RctlFeedback`, dans `RtcpSession.cs`, désactivé par défaut) : le canal privé qu'utilise le miroir
  de Xcode, deux paquets RTCP APP (PT 204) — un reçu de 16 octets par image, envoyé sur le bit *marker*, et un
  rapport « RCTL » de 32 octets vingt fois par seconde dont le demi-mot bas du dernier mot porte la borne de
  débit en kbit/s. Format et cadences repris de la capture ; la lecture des quatre mots et l'identification de
  la borne sont documentées avec leur degré de certitude dans le fichier.
- L'offre par défaut reste **identique octet pour octet** au gabarit Xcode (`offer-check`).


## Étape 4 — la latence absolue : mesure et réglages (9 septembre 2026)

La dérive ne voit que les **variations** du retard ; elle est plate à ±30 ms alors que le miroir accuse
plus d'une seconde de retard absolu. Deux instruments ont été ajoutés pour voir le retard lui-même.

- **`clock-test [secondes] [--variant=…] [--out=<dossier>]`** (sonde) : le téléphone affiche une horloge
  juste (Safari sur time.is, NTP), la sonde décime chaque image décodée au quart, cherche pendant 2,5 s la
  **bande horizontale la plus changeante** — les grands chiffres sont la seule chose qui change une fois par
  seconde sur une page immobile — puis déclenche sur chaque changement de seconde, écrit l'image en BMP et
  la bande en PNG, nommées par l'**heure PC du dernier paquet** de cette image. Quand l'affichage bascule
  sur `X`, l'heure vraie du téléphone est `X`,000 : **latence = heure PC de la transition − X,000**. Contrôle
  intégré : les intervalles entre transitions doivent valoir 1000 ms.
- **`RtcpSession.PipelineMs`** : le rapport d'émission RTCP publie le couple (horloge du téléphone, horodatage
  média correspondant), ce qui donne l'heure du téléphone pour n'importe quel horodatage RTP et donc le retard
  **horodatage → arrivée**. L'horloge que le téléphone y met n'est pas l'heure murale (elle est décalée d'une
  constante, ~4 h 31 dans cette campagne), donc la valeur absolue ne veut rien dire ; ses **différences** sont
  justes au millième et c'est ce qui sert à comparer les variantes.

### Ce que la mesure dit

| Levier essayé | Effet sur la latence |
| --- | --- |
| `default` (négociation Xcode, sans RCTL) | 1,55 – 1,78 s (5 sessions) |
| boucle RCTL, borne 2000 / 4000 / 6000 / 60001 kbit/s | 1,71 – 1,75 s — aucun effet |
| w4 sur l'horloge média (OWRD ≈ 0) vs sur une montre à nous | aucun effet |
| rapports RCTL à 60 Hz au lieu de 20 Hz | aucun effet |
| `AVCMediaStreamNegotiatorAccessNetworkType` 0, 1, 2, 3 | aucun effet (1,72 s à 0 comme à 1) |
| `AVCMediaStreamNegotiatorTransportProtocolType` 0, 1, 2, 3 | aucun effet |
| `clientSupportedFeatures` 0, 140, 141, 255 | aucun effet (0 et 255 acceptés comme 140) |
| `ltrpEnabled`, `fecEnabled=0`, `allowRTCPFB`, `tilesPerFrame=4` | acceptés, aucun effet |
| `endpoint:Mac16,11` au lieu de `Mac15,9` | accepté, aucun effet |
| `customVideoWidth/Height` (664×1448), paliers ≤ 2000 kbit/s | aucun effet (1,68 / 1,71 s) |
| **un tap sur l'écran du téléphone** | **1,70 s → 1,00 s**, puis remontée en ~2 min |

Le `streamConfig` renvoyé est **identique** pour toutes ces variantes : `JitterBufferMode = 1`,
`VideoStreamMode = 4`, `TXMinBitrate = 333000`, `TXMaxBitrate = 6000000`, `KeyFrameInterval = 0`,
`RateAdaptationEnabled = true`, `RTCPTimeoutInterval = 20`, `RxPayloadType = 123`. Aucun levier de l'offre ni
de l'enveloppe `startmediastream` ne le fait bouger d'un champ.

### Où est la seconde

`PipelineMs` — le retard **horodatage → arrivée**, qui couvre le transport et tout ce que le téléphone fait
après avoir horodaté une image — tient dans **40 ms d'écart sur vingt sessions**, alors que la latence lue sur
les pixels varie de 1,00 s à 1,78 s dans les mêmes sessions et **ne corrèle pas** avec elle. Le retard n'est
donc ni dans le tunnel, ni dans l'encodeur après horodatage, ni dans notre chaîne (l'horodatage PC est celui
du dernier paquet, avant tout décodage) : il est **en amont de l'horodatage**, entre les pixels affichés sur
le téléphone et la capture qui les estampille.

Le tap le confirme : 1,00 s mesuré deux fois sur deux juste après un tap (une fois sur l'icône Safari, une
fois sur une zone vide de la page), 1,22 s quatre-vingt-dix secondes plus tard, 1,55 – 1,78 s sur dix sessions
sans tap. C'est la signature d'un ralenti d'inactivité côté iOS que le toucher réveille. **Ce n'est pas un
levier du protocole** — rien dans la négociation ne l'atteint.

### Décision

**Le défaut ne change pas** : `StreamTuning.Default` reste la négociation de Xcode octet pour octet, sans
boucle RCTL, parce qu'aucune des vingt variantes mesurées ne descend en dessous, et que la seule qui descende
(un toucher sur l'écran) n'est pas un réglage. `offer-check` reste donc vert sans être touché. Les leviers
restent tous accessibles par `--variant=`, combinables avec `+`, pour la campagne suivante.

### Ce qui reste inexpliqué

- **Aucune variante ne descend sous 1 s**, et l'objectif de 200 ms n'est pas atteint : le plancher mesuré est
  1,00 s, écran fraîchement touché.
- Ce plancher d'une seconde n'a pas été localisé. Il est en amont de l'horodatage RTP, mais la mesure ne
  distingue pas *la page repeinte en retard sur l'écran du téléphone* de *la capture qui estampille en retard
  une image déjà affichée*. Il faudrait une horloge que iOS ne peut pas ralentir (une vue native animée, pas
  une page web) pour trancher.
- Les mesures pixel portent une constante inconnue : l'horloge du PC était **154 ms en avance** sur NTP
  pendant la campagne (`w32tm /stripchart`), donc les latences vraies valent les chiffres ci-dessus **moins
  154 ms**. Le tableau les donne bruts, comme lus.
- Le service d'affichage devient sourd (« pas de SETTINGS du téléphone en 3 s ») après six à huit sessions
  média rapprochées ; un `unmount` suffit à le réveiller. Non lié aux variantes : il tombe aussi bien sur la
  négociation par défaut.

## Étape 5 — le tampon du décodeur (9 septembre 2026)

Le décodeur logiciel de Windows retenait des images avant d'en rendre une : le miroir était en retard d'autant,
et **aucun compteur interne ne pouvait le voir** — chaque image porte l'heure d'arrivée de ses propres paquets,
donc les étapes « fil / attente / décodage » restent à quelques millisecondes pendant que l'écran affiche une
demi-seconde de passé. `H264Decoder.InFlight` (unités entrées − images sorties) est le compteur qui le montre.

### Ce qui retenait les images

1. **Le mode faible latence n'était posé que par une porte.** `CODECAPI_AVLowLatencyMode` sur `ICodecAPI` et
   `MF_LOW_LATENCY` sur `IMFTransform.GetAttributes()` sont **le même GUID** `9c27891a-…` par deux chemins, et
   ce décodeur n'ouvre pas les deux. Les deux sont posées, HRESULT vérifié (`LowLatencySet`) : **48 → 12 images
   retenues**.
2. **Le SPS du téléphone ne dit rien du réordonnancement.** Profil 100, niveau 5.1, 1328×2896 = 83 × 181 = 15 023
   macroblocs ; `MaxDpbMbs(5.1) = 184 320`, donc DPB = min(184320 / 15023, 16) = **12**. Le VUI existe (description
   couleur présente) mais **`bitstream_restriction_flag = 0`** : faute de déclaration, le décodeur suppose le pire
   que le niveau autorise et attend douze images. Or le flux n'a **aucune image B** — mesuré : 1 tranche I,
   440 tranches P, 0 B sur une capture de 441 unités (`decode-capture`, ligne « types de tranche »).

### Le correctif : réécriture du SPS

`Media\SpsRewriter.cs` relit le SPS bit à bit (exp-Golomb ue/se, retrait puis réinsertion des octets
anti-émulation `00 00 03`), recopie chaque champ à l'identique jusqu'au `bitstream_restriction_flag` — dernier
champ d'un VUI, donc la queue est à nous à partir de là — et écrit la restriction :
`motion_vectors_over_pic_boundaries_flag = 1`, `max_bytes_per_pic_denom = 0`, `max_bits_per_mb_denom = 0`,
`log2_max_mv_length_horizontal = vertical = 16`, **`max_num_reorder_frames = 0`**,
`max_dec_frame_buffering = max_num_ref_frames` (4 ici). Les deux branches sont traitées : VUI absent (on en écrit
un, huit drapeaux à zéro puis la restriction) et VUI présent avec ou sans restriction.

`H264Depacketizer` réécrit le SPS **au moment où il le lit** — dans l'`avcC` du premier paquet, seul endroit où
ce flux en met un — et c'est le SPS réécrit qui est préfixé à chaque IDR. Un SPS illisible passerait tel quel
(`SpsRewriteFailures`) plutôt que d'être perdu.

SPS du téléphone : `27 64 00 33 4B 04 C5 14 05 30 16 BA 6E 04 04 04 04` (17 o) → réécrit
`27 64 00 33 4B 04 C5 14 05 30 16 BA 6E 04 04 04 0F 08 84 65 80` (21 o).

`sps-selftest` (sonde, hors ligne) vérifie sur trois cas — le SPS réel, le même avec le VUI retiré, et le SPS déjà
réécrit — que profil, niveau, chroma, dimensions, `num_ref_frames`, `frame_mbs_only_flag` et le rognage sont
inchangés, que la restriction est là avec un réordonnancement nul, et qu'une **seconde passe ne change pas un
octet** (ce qui prouve que le lecteur et l'écrivain sont d'accord). Un PPS présenté comme SPS est refusé.

### Mesures

| Étape | Images retenues | Latence absolue |
| --- | --- | --- |
| Faible latence par une seule porte | 48 | ~2 s (jugé à l'œil) |
| `MF_LOW_LATENCY` + `CODECAPI_AVLowLatencyMode` | 12 | **484 ms** (468 / 481 / 492 / 494) |
| `CODECAPI_AVDecNumWorkerThreads = 1` en plus | 12 | non mesurée — abandonné |
| **+ réécriture du SPS** | **0** | **96 ms** (90 / 96 / 97 / 100) |

Latence absolue mesurée contre le **chronomètre natif de l'app Horloge** : un `tap` le démarre (instant T0 = heure
PC du tap + 65 ms de maintien), puis `mirror-test 12 --suite=<dossier>` écrit une image par seconde nommée par
l'heure PC de son dernier paquet ; latence = heure PC de l'image − T0 − valeur lue au chronomètre. Cette mesure
**ne dépend d'aucune synchronisation d'horloge** (le chronomètre est une durée, pas une heure), contrairement à
`clock-test` qui compare l'heure PC à l'heure NTP du téléphone. Vérification : l'écart avant/après vaut 388 ms,
soit exactement 12 images à 31 i/s — les deux mesures se recoupent.

**Conséquence pour l'étape 4** : la seconde inexpliquée du tableau précédent était mesurée sur **time.is dans
Safari**. Sur une vue native, le plancher est de 96 ms. C'est le contrôle que la section « Ce qui reste
inexpliqué » réclamait : le retard était dans le repeint ralenti d'une page web, pas dans la chaîne.

### Pistes écartées

- **`CODECAPI_AVDecNumWorkerThreads = 1`** : le MFT le supporte (`IsSupported` = S_OK) mais **refuse le VT_UI4 que
  la documentation annonce** (`E_INVALIDARG`) et n'accepte que **VT_I4**. Posé, il laisse les 12 images retenues
  intactes et fait tomber le décodage de ~450 à ~271 images/s. Aucun gain, moitié du débit : non retenu.
- **Décodeur matériel par `MFTEnumEx`** et **`MFT_MESSAGE_COMMAND_DRAIN` après chaque unité** : non tentés, la
  réécriture du SPS ayant amené le tampon à zéro. Le drain par unité aurait de toute façon jeté les images de
  référence d'un flux qui n'a qu'une seule image clé pour toute la session.

### Ce que la sonde et l'app affichent

- `mirror-test` : `decodeur : N image(s) retenue(s), faible latence OUI|NON`.
- `decode-capture` : la même ligne avant la vidange, plus `types de tranche : I … P … B …`.
- App, bloc VIDEO de la ligne de statistiques : `decodeur retient N  faible latence oui|NON`
  (`MediaStats.DecoderInFlight`, `MediaStats.DecoderLowLatency`).
## Critères d'acceptation

- Capture neuve avec SPS/PPS/IDR au début ; `decode-capture` produit une image BMP lisible (on l’ouvre pour la regarder).
- `mirror-test 60` : flux continu au-delà de 20 s grâce aux RR ; ≥ 30 images/s décodées à 1328×2880 ; latence de décodage
  mesurée (arrivée du dernier paquet d'une image → image décodée) affichée, objectif < 30 ms.
- Aucun changement des messages CoreDevice hors le choix des banques ; `offer-check` toujours vert.
