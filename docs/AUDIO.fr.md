# Le son du téléphone — étude

[English](AUDIO.md) · **Français**

Faits mesurés le 9 septembre 2026, iPhone 17 Pro Max (iPhone18,4) sous iOS 27, sonde `audio-info`,
`aac-selftest`, `media-status`. Tout ce qui suit a été lu sur le fil ou dans un HRESULT ; ce qui
n'a pas pu être mesuré est dit tel quel.

**La conclusion en une ligne :** le téléphone accepte d'envoyer son son, il l'envoie en **AAC-ELD**,
et **Windows ne sait pas décoder l'AAC-ELD**. Il n'existe aucun levier dans la négociation pour lui
demander autre chose.

## 1. La négociation audio

Même appel que la vidéo — `com.apple.coredevice.feature.startmediastream`, action
`…mediastreamstart` — avec `type: "audio"`, sans les deux options d'affichage
(`CoreDeviceVideoDisplayMode`, `VideoStreamForDisplayID`), et une offre construite en **mode
négociateur 6** (la vidéo est en 5). Voir `Media/AudioOffer.cs` et
`DisplayService.StartAudioStreamAsync`.

### Pas de banque de codec — le point central

L'offre vidéo porte, dans son message `VideoSettings`, un champ 3 répété : `videoPayloadCollections`,
une banque par codec, chacune nommant un type de charge utile et une chaîne de capacités, offertes
dans l'ordre de préférence. C'est ce champ qui permet à ce projet d'obtenir du H.264 au lieu du HEVC.

**Le message audio n'a rien de tel.** Dans l'offre capturée d'Apple il est fait de six varints, et
de rien d'autre :

```
f1 = identifiant de session (bourré à cinq octets, comme côté vidéo)
f2 = 0
f3 = 0
f4 = 24191        (0x5E7F)
f5 = 0
f6 = 0
```

Aucun type de charge utile nommé, aucune chaîne de capacités, aucune fréquence, aucun nombre de
canaux, aucune banque. Les noms de champs de `VCMediaNegotiationBlobAudioSettings` n'ont pas été
retrouvés (ceux de la vidéo l'ont été dans la table `__objc_methname` du framework) : ils sont donc
désignés ici par leur numéro, et **aucun champ n'est inventé**.

### Ce que la réponse du téléphone révèle

La réponse porte un `negotiatorAnswer` construit comme notre offre : un plist binaire contenant un
protobuf compressé en zlib. `audio-info` le décompresse et l'affiche champ par champ. Son message
audio :

```
f1 = 578882765   (le SSRC du téléphone)
f2 = 0
f3 = 0
f4 = 5632        (0x1600)
f5 = 1536        (0x0600)
f6 = 1
```

`0x1600 & 0x5E7F == 0x1600`, et `0x0600 ⊂ 0x1600` : **f4 est un masque de capacités**, et le
téléphone répond avec le sous-ensemble qu'il retient. Mesures sur ce masque :

| offre | résultat |
|---|---|
| `f4 = 24191` (défaut Apple) | accepté ; réponse `f4 = 5632`, `f5 = 1536`, `RxPayloadType = 101`, `AudioStreamMode = 8` |
| `f4 = 65535` (tout offert) | accepté ; réponse **identique** — offrir plus ne change rien |
| `f4 = 0` | **refusé** : code **32033**, domaine **`GKVoiceChatServiceErrorDomain`**, `NSErrorUserInfoDetailedError = 4` |

Le champ est donc porteur — le mettre à zéro fait refuser l'offre — mais il ne sert pas à choisir un
codec : quel que soit le masque accepté, le téléphone retient `0x1600` et annonce le même
`RxPayloadType = 101`. **Il n'y a pas d'AAC-LC, pas de PCM, pas d'Opus à demander.** Le nom du
domaine d'erreur est en soi une information : pour iOS, ces flux sont du *voice chat*.

L'autre candidat était la table de paliers du haut niveau (champ 9), partagée avec l'offre vidéo, où
trois genres d'entrée ne sont pas des plafonds de débit : genre 16 (4100), genre 4 (6500), genre 1
(299) — la référence les lit comme des marqueurs de codec (CELT-NB, SILK, Opus ?). Les variantes
`paliers-sans-codec` et `paliers-codec-seuls` existent dans la sonde pour cela ; elles n'ont pas pu
être mesurées avant que le service d'affichage du téléphone devienne muet (voir § 6).

## 2. Ce que le téléphone envoie

`streamConfig` de la réponse, valeurs qui comptent :

```
RxPayloadType = 101      TxPayloadType = 101      AudioStreamMode = 8
Direction = 1            CaptureSource = 0        SRTPCipherSuite = 0   (flux en clair)
RTCPEnabled = True       RTCPSendInterval = 1     RTCPTimeoutInterval = 20
source: { audioSystemOutput: {} }
```

Sur le fil, écran silencieux, 12 s de mesure :

- **101,1 paquets/s**, 16 octets par datagramme : en-tête RTP de 12 octets et **4 octets de charge
  utile** (`00 68 34 00`). Soit ≈ 13 kbit/s. C'est un **flux de silence** : le téléphone émet en
  permanence, même quand rien ne joue.
- **Pas d'horodatage RTP : 480 unités par paquet.** À 48 kHz une unité est un échantillon, donc
  **480 échantillons par trame, 10 ms**. C'est une mesure, et elle contredit la configuration : le
  `frameLengthFlag` de l'AudioSpecificConfig vaut 0, ce que la norme associe à 512. Le fil gagne.
- Aucune perte, aucun doublon, aucun désordre ; un seul bit *marker* dans toute la session.
- L'horloge RTP est celle des échantillons (48 kHz), pas les 24 kHz de la vidéo : `RtcpSession` prend
  désormais son horloge de gigue en paramètre.

**Non mesuré : le son réel.** Personne n'a pu faire jouer un son sur le téléphone pendant la mesure.
Ce qui précède décrit donc le silence — ce qui est déjà une information (le flux existe et il est
continu), mais le débit et la taille des trames avec du contenu restent à mesurer. **Demande à
l’utilisateur : lancer une musique sur le téléphone pendant les 10 s de `audio-info 10 son.rtp`.**

L'AudioSpecificConfig que le téléphone annonce est `F8 E6 40 00`, lu bit à bit (ISO/IEC 14496-3
§1.6.2.1) : **objet 39 = ER AAC ELD**, index de fréquence 3 = **48 000 Hz**, configuration de canaux
**2 (stéréo)**.

## 3. Ce que Windows sait décoder : `aac-selftest`

Le seul décodeur AAC de Windows est le *Microsoft AAC Audio Decoder MFT*
(`CLSID {32D186A7-218F-4C75-8876-DD77273A8999}`, `C:\Windows\System32\MSAudDecMFT.dll`). La question
« Windows peut-il décoder ce que le téléphone envoie » se tranche avec `SetInputType` et un HRESULT.

Le MFT refuse un type entier avec un seul `MF_E_INVALIDMEDIATYPE` et ne dit jamais quel attribut lui
a déplu. La mesure est donc une **bissection** : partir de son propre type d'entrée annoncé — qu'il
accepte rendu tel quel — et avancer vers le type voulu un attribut à la fois, deux fois, une fois
avec un AudioSpecificConfig AAC-LC et une fois avec celui du téléphone.

```
temoin AAC-LC (11 90)                         telephone AAC-ELD (F8 E6 40 00)
type annonce 0 tel quel            ACCEPTE    type annonce 0 tel quel            ACCEPTE
+ SAMPLES_PER_SECOND reecrit       ACCEPTE    + SAMPLES_PER_SECOND reecrit       ACCEPTE
+ USER_DATA reecrit (12 zeros)     ACCEPTE    + USER_DATA reecrit (12 zeros)     ACCEPTE
+ AudioSpecificConfig ajoute       ACCEPTE >> + AudioSpecificConfig ajoute       REFUSE   0xC00D36B4
+ deux canaux                      ACCEPTE    …
+ 16 bits, alignement 4            ACCEPTE
+ debit moyen 16000 o/s            ACCEPTE
```

Un seul octet de différence entre les deux colonnes à l'étape marquée : **le décodeur de Windows
accepte l'objet 2 (AAC-LC) et refuse l'objet 39 (ER AAC ELD)**, avec
`MF_E_INVALIDMEDIATYPE (0xC00D36B4)`. La documentation le disait ; c'est maintenant mesuré.

> **Piège d'interop, une demi-heure perdue.** `IMFAttributes::SetBlob` déclaré avec un paramètre
> `byte[]` renvoie **S_OK et n'écrit rien** : en interop COM, le marshalling par défaut d'un tableau
> est `UnmanagedType.SafeArray`, et le MFT reçoit un en-tête de SAFEARRAY là où il attend des octets.
> Le symptôme est un décodeur qui semble refuser tous les codecs, AAC-LC compris. `SetBlob` et
> `GetBlob` prennent donc un `IntPtr` et le tampon est alloué à la main (`AacProbe.WriteBlob`). La
> preuve est dans la sonde : un motif écrit puis relu.

## 4. Les suites possibles, chiffrées

**a) Écrire un décodeur AAC-ELD en C#.** C'est la seule voie qui passe par notre pile. Ce qu'il faut,
et rien de moins : lecteur de bits ; `ELDSpecificConfig` ; syntaxe ER AAC ELD (données de section,
facteurs d'échelle, données spectrales) ; les onze livres de Huffman plus l'échappement ; la
quantification inverse en `x^(4/3)` ; TNS ; les outils stéréo (M/S, intensité) ; la **MDCT à faible
retard** avec sa fenêtre propre ; les tables de bandes de facteurs d'échelle à 48 kHz ; et, si le
téléphone active LD-SBR (`f5` de la réponse est peut-être là pour ça), tout le SBR par-dessus.
Estimation : **3 000 à 5 000 lignes, tables comprises, deux à quatre semaines** pour une version
correcte. Le vrai risque n'est pas la quantité : c'est qu'une erreur subtile donne du bruit et non du
silence, et qu'il n'existe **aucun décodeur de référence sur cette machine** pour comparer
échantillon par échantillon (pas de NuGet, pas de ffmpeg). Découpage possible : (1) lecteur de bits
et parcours de la syntaxe jusqu'à consommer exactement chaque AU sans erreur — vérifiable sur une
capture, sans produire un son ; (2) quantification inverse et MDCT, vérifiées sur une trame de
silence dont on connaît la sortie (zéro) ; (3) le reste, à l'oreille.

> **Correction, 10 septembre 2026, le soir même.** L'estimation ci-dessus était fausse sur les deux
> chiffres. Une fois les tables de `docs/AAC_ELD_TABLES.fr.md` réunies, le décodeur lui-même a été
> écrit d'une traite : environ 1 900 lignes, pas 3 000 à 5 000, et il existe le jour même plutôt
> qu'en deux à quatre semaines. Voir le § 10, « Le décodeur maison », plus bas.

**b) Un codec de rechange.** Écarté par la mesure du § 1 : l'offre n'a pas de banque, et le seul
levier existant (`f4`) ne change pas le choix du téléphone.

**c) Ne pas passer par notre pile du tout.** Ce PC est **déjà** un récepteur audio de cet iPhone : le
téléphone y est appairé en Bluetooth et Windows y a monté les deux profils
(`<nom de l’iPhone> A2DP SNK` — le son du téléphone vers le PC — et `<nom de l’iPhone> Hands-Free HF Audio` —
dans les deux sens, micro compris). Zéro ligne de code, disponible tout de suite, et c'est la réponse
honnête à « entendre le son du téléphone sur le PC » tant que (a) n'est pas écrit. Le défaut est
connu : le Bluetooth n'est pas synchronisé avec l'image du miroir et son délai n'est pas mesuré ici.

Recommandation : **(c) tout de suite, (a) seulement si l’utilisateur veut le son *dans* l'app, en
sachant que c'est deux à quatre semaines pour un composant dont l'échec se manifeste par du bruit.**

## 5. Le micro du PC vers le téléphone

Il n'existe **rien** de tel dans ce service, et ce n'est pas une déduction :

- le champ `direction` de `startmediastream` accepte `"input"` — l'offre est acceptée et la réponse
  renvoie `direction = input` — mais **rien ne change** : la source reste `audioSystemOutput`,
  `streamConfig.Direction` reste 1, et le téléphone continue de nous *envoyer* ses 100 paquets/s. Le
  champ est recopié, pas honoré ;
- `getmediasupportinfo` énumère ce que l'appareil sait faire, et la liste est complète :
  « Primary video display mirrored output stream, System audio output stream, Virtual external video
  output stream, Video output stream by display ID, Display information, Screenshot capture »
  (`supportedFeatures = 972`). **Aucun flux entrant, aucune capture micro** ;
- l'implémentation de référence n'a pas d'autre voie : `direction` y est écrit `"output"` en dur aux
  deux endroits, et il n'y a nulle part de `audioSystemInput` ni de service de capture audio.

Donc : **on ne peut pas envoyer le micro du PC vers le téléphone par CoreDevice.** Ce qui existe déjà
et le fait, c'est le profil mains-libres Bluetooth déjà monté sur ce PC (§ 4c) : dans ce mode, le
téléphone entend le micro du PC. Aucun pilote virtuel n'est proposé ici.

**Re-mesuré le 11 septembre 2026**, parce que la question a été reposée et qu'une note d'hier n'est
pas une preuve. `audio-info --direction=input` sous iOS 27 : l'offre est **acceptée** et la réponse
renvoie bien `direction = input` — puis configure le même flux sortant que d'habitude,
`source: audioSystemOutput`, `RxPayloadType = 101`, un port pour que le téléphone *émette*. Le mot
est pris et ignoré. La liste des capacités est inchangée et toujours exhaustive : six fonctions,
toutes sortantes ou en lecture. C'est un refus d'une autre nature que celui d'Apple Music, et il
vaut la peine de les distinguer : là, le téléphone *sait* envoyer le son et s'y refuse tant qu'un
flux d'affichage est ouvert — une porte de politique ; ici, la capacité n'existe tout simplement pas
dans le démon. Rien sur ce chemin ne portera un micro, quelle que soit la façon de le demander.

## 6. Sessions média orphelines — et le micro du téléphone

Symptôme rapporté : **après une session de miroir, le micro du téléphone semble désactivé pour ses
autres applications.**

### Ce qui est mesuré

Une session média vit sur le téléphone, pas ici. `startmediastream` la crée, `stopmediastream` la
termine, et rien d'autre. Mesure, avec la commande `audio-leak-test` (ouvre un flux audio et ne le
ferme pas) puis `Stop-Process` sur la sonde, observée par `media-status 90` depuis un second
processus :

- après un **arrêt propre** : `sessions: []`, `running = False` ;
- après un **processus tué** : la session reste, entière — `running = True`, `type = audio`,
  `source: audioSystemOutput`, son `avcMediaStreamOptionClientSessionID`, et un
  `runDurationSeconds` qui continue de monter. Vue vivante 36 s après le kill ;
- elle **disparaît d'elle-même environ 20 s après le dernier rapport de réception** : c'est
  `RTCPTimeoutInterval = 20`. Mesure : session tuée à 22:01:52, `running = False` à 22:02:12 ;
- `media-release` la ferme **immédiatement** (`stopmediastream` sur l'identifiant retrouvé dans
  l'état du serveur) ;
- la parade au démarrage fonctionne : fuite créée, sonde tuée, `audio-info` relancé aussitôt →
  « session media orpheline liberee : … / 1 session media orpheline liberee avant l'ouverture du
  flux », puis le flux s'ouvre normalement.

### Ce qui n'est pas démontré

**Que ce soit la cause du symptôme.** Deux réserves, dites honnêtement :

1. la session orpheline se referme seule en ~20 s, donc elle n'explique pas un micro indisponible
   plusieurs minutes après ;
2. **jusqu'au 9 septembre 2026 ce logiciel n'avait jamais ouvert de flux audio** — le chemin audio
   est né ce jour-là. Une gêne du micro observée *avant* ne peut pas venir d'une session de capture
   audio. Elle pourrait venir d'un flux vidéo laissé ouvert (même mécanisme de session, `type =
   video`), ce qui reste à vérifier avec l’utilisateur.

**Une cause concurrente, présente et vérifiable :** cet iPhone est appairé à ce PC en Bluetooth et
Windows a monté son profil **mains-libres** (`<nom de l’iPhone> Hands-Free HF Audio`, HFP
`0000111F-…`, état OK). Quand une application Windows ouvre cette entrée microphone, iOS **route le
micro du téléphone vers le PC** et les applications du téléphone n'entendent plus rien — exactement
le symptôme décrit, sans que notre logiciel y soit pour quoi que ce soit. **Test à faire :** quand le
micro semble mort, déconnecter le Bluetooth de l'iPhone (Réglages > Bluetooth > ce PC >
Déconnecter) ; s'il revient, la cause est là.

### Dépannage (à recopier dans `docs/INSTALLATION.fr.md`)

> **Le micro de l'iPhone ne fonctionne plus après une session de miroir.**
>
> *Cause possible 1 — une session média que le téléphone croit encore ouverte.* Elle survit quand le
> processus est tué (gestionnaire des tâches, `Stop-Process`, plantage, câble arraché) au lieu d'être
> fermé. Le téléphone la récupère seul environ 20 s après, mais tant qu'elle est là, iOS garde la
> capture audio réservée.
> - Remède immédiat, sans l'app : attendre une minute, ou **redémarrer l'iPhone**.
> - Remède avec l'app : la relancer suffit — au démarrage de chaque session, elle demande au
>   téléphone ce qu'il croit en cours et ferme ce qui traîne (journal : « session média orpheline
>   libérée »).
> - Remède en ligne de commande : `LuminaMonitor.UsbProbe media-status` pour voir,
>   `LuminaMonitor.UsbProbe media-release` pour libérer.
>
> *Cause possible 2 — le Bluetooth du PC tient le micro.* Si l'iPhone est appairé à ce PC en
> Bluetooth, Windows monte un profil « mains-libres » qui **prend le micro du téléphone** dès qu'une
> application Windows l'utilise. Déconnecter l'iPhone du Bluetooth (Réglages > Bluetooth > ce PC >
> Déconnecter) rend le micro au téléphone. Cela n'a aucun rapport avec le câble USB ni avec le
> miroir.

### Ce que le code fait désormais

- `MediaHygiene` (nouveau) : lit `getmediastreamserverstatus`, y trouve les identifiants de session
  (toute UUID de la réponse), et ferme chacun par `stopmediastream`. Un `keep` permet d'épargner la
  session que l'on vient d'ouvrir — sans quoi la moitié audio d'une session appairée fermerait la
  moitié vidéo.
- `MediaSession.StartAsync` et `AudioSession.StartAsync` appellent cette garde **avant** d'ouvrir
  leur flux, sur un canal à elles : le démon raccroche le canal sur lequel un `stop` est envoyé.
- La garde est à l'**entrée** et pas à la sortie, et c'est un choix : un `Stop-Process` n'exécute
  ni finaliseur, ni `ProcessExit`, ni `SafeHandle`. Aucun nettoyage de sortie n'est robuste contre un
  hôte qui disparaît ; seul l'ordre « vérifier avant d'ouvrir » l'est.
- Dans la sonde, tous les arrêts de flux sont dans des `finally`, y compris quand l'offre est
  refusée ou que le service d'affichage ne répond pas.
- **À faire côté app (autre session) :** afficher la ligne « session média orpheline libérée » dans
  la barre d'état ; elle arrive déjà par `IProgress<string>` et va dans le journal.

## 7. Ce qui reste à mesurer

1. ~~**Le son réel**~~ — **mesuré le 10 septembre 2026 à 20 h 40.** Trente secondes capturées avec une
   vidéo qui jouait sur le téléphone : 3 029 trames, 372 octets par trame en moyenne, 0,30 Mbit/s,
   101 paquets/s, aucune perte. Décodé en entier et écouté ; les nombres sont au §10.
2. ~~**Audio et vidéo dans la même session**~~ — **prouvé le 10 septembre 2026 à 21 h 12.**
   `audio-info 5 --video` : le flux vidéo ouvert d'abord, comme le miroir de Xcode, puis le flux
   audio avec `PairedSessionId = video.SessionId` — le même
   `avcMediaStreamOptionClientSessionID` pour les deux — **503 paquets audio en 5 s, aucune perte**,
   et les deux flux fermés proprement, le téléphone raccrochant sur chaque service. Cet ordre est
   désormais celui de `DeviceSession` ; voir §11.
3. Les variantes de paliers (`paliers-sans-codec`, `paliers-codec-seuls`), pour finir d'éliminer la
   table de paliers comme lieu du choix de codec.
4. Un flux **vidéo** laissé orphelin gêne-t-il aussi le micro ? C'est la seule hypothèse qui
   expliquerait le symptôme avant l'existence du chemin audio.
5. **Le retard n'est pas aligné automatiquement.** Le §11 mesure l'écart entre le son et l'image
   toutes les cinq secondes et n'en fait rien. Ce qui manque pour fermer la boucle n'est pas la
   mesure mais la décision : à quelle vitesse déplacer un retard sans que le déplacement s'entende.

## 8. Les commandes

```
LuminaMonitor.UsbProbe aac-selftest                     # hors ligne : Windows décode-t-il l'AAC-ELD ?
LuminaMonitor.UsbProbe audio-info [sec] [capture.rtp] [--variant=…] [--video] [--direction=…]
LuminaMonitor.UsbProbe media-status [sec]               # ce que le téléphone croit en cours
LuminaMonitor.UsbProbe media-release                    # ferme les sessions orphelines
LuminaMonitor.UsbProbe audio-leak-test                  # ouvre un flux et ne le ferme PAS (mesure)
LuminaMonitor.UsbProbe audio-play <capture.rtp|--silence[=trames]> [--device=<id|default>] [--delay=<ms>] [--dry]
LuminaMonitor.UsbProbe audio-devices                    # hors ligne : les sorties Windows et leur format de mixage
```

Variantes de `audio-info` : `default`, `f2:<n>`, `f3:<n>`, `f4:<n>`, `f5:<n>`, `f6:<n>`,
`paliers-sans-codec`, `paliers-codec-seuls`, combinables avec `+`.

## 9. Décision, 10 septembre 2026

Le Bluetooth est écarté, pour les deux moitiés de ce document : pas parce qu'il n'a jamais été
essayé, mais parce qu'il l'a été et s'est montré peu fiable — la liaison radio entre l'iPhone et le
PC d'essai du projet n'a pas tenu. Cela ferme le § 4c (le Bluetooth comme source du son) et le
recours au mains-libres Bluetooth que le § 5 indiquait pour le micro.

Le son de l'iPhone passera par le câble à la place, décodé par un décodeur AAC-ELD écrit dans ce
projet — § 4a, maintenant en chantier.

Le micro du PC vers le téléphone reste impossible, câble compris, et la disparition du Bluetooth
n'y change rien : le § 5 montrait déjà que le téléphone n'annonce aucune capacité entrante par
CoreDevice (`direction : "input"` est acceptée et renvoyée telle quelle mais ne change rien ;
`getmediasupportinfo` ne liste aucune fonction de capture), et cela restait vrai avant cette
décision comme après elle.

(Suite le soir même : § 10, « Le décodeur maison », plus bas.)

## 10. Le décodeur maison

Écrit le soir même de la décision ci-dessus, une fois les tables de
[`docs/AAC_ELD_TABLES.fr.md`](AAC_ELD_TABLES.fr.md) réunies : environ 1 900 lignes sur 14 fichiers
dans `src/LuminaMonitor.Core/Media/Aac/`, aucun paquet référencé, rien de FFmpeg, FDK-AAC ou faad2
consulté. Cette section note ce qu'il fait, ce qu'il ne fait pas, ce qui a été mesuré, et ce qui
n'est pas encore tranché.

### Le rôle de chaque fichier

| Fichier | Rôle |
|---|---|
| `BitReader.cs` | lecteur de bits MSB-first sans copie, plus `AacBitstreamException` (une raison et une position en bits) |
| `AudioSpecificConfig.cs` | l'ASC et l'`ELDSpecificConfig`, chaîne d'extensions comprise, avec `Validate()` |
| `Huffman.cs` | les douze livres, en arbres binaires |
| `SectionData.cs` | la variante ER : `sect_len_incr` sur cinq bits, échappement à 31 |
| `ScaleFactors.cs` | les trois chaînes — facteurs d'échelle, énergie de bruit, position d'intensité |
| `TnsData.cs` | les données du filtre TNS |
| `SpectralData.cs` | quadruplets et paires, signes puis échappement du livre 11 |
| `Dequantizer.cs` | `|q|^(4/3)` et `2^(0,25·(sf−100))`, tabulés |
| `Tns.cs` | le filtre tout-pôle |
| `Stereo.cs` | M/S et intensité |
| `Pns.cs` | bruit à énergie unité, corrélation gauche/droite |
| `EldFilterBank.cs` | le banc de filtres de synthèse basse latence, replié sur une DCT-IV, recouvrement des trois blocs précédents |
| `EldSyntax.cs` | la séquence d'éléments sans identifiants — CPE/SCE |
| `AacEldDecoder.cs` | l'API publique et les compteurs |

Non implémenté, chacun refusé avec un motif typé plutôt qu'ignoré en silence : SBR basse latence,
extensions ELD (SAOC, MPEG Surround), résilience HCR/RVLC, plus de deux canaux. Aucune allocation
par trame en régime établi — voir les mesures plus bas.

### L'API

```csharp
var decoder = new AacEldDecoder(audioSpecificConfig);
bool ok = decoder.Decode(accessUnit, pcmInterleaved, out int samplesPerChannel);
```

`Decode(ReadOnlySpan<byte> accessUnit, Span<float> pcmInterleaved, out int samplesPerChannel)`
décode une unité d'accès en PCM flottant entrelacé à pleine échelle ±1. Elle renvoie `false` sur une
trame qu'elle n'a pas pu lire plutôt que de lever une exception, pour qu'un appelant sur un flux en
direct passe à la trame suivante ; les compteurs disent pourquoi et combien :

- `FramesDecoded`, `FramesFailed`
- `BitsConsumed`, `BitsAvailable` — la longueur de syntaxe de la dernière trame contre ce qui lui a
  été donné
- `PaddingIsZero` — si tout ce qui suit la syntaxe était le bourrage nul sur lequel une trame bien
  formée se termine
- `LastFailure` — l'`AacBitstreamException` de la dernière trame refusée, ou nul

### Les commandes de la sonde

```
LuminaMonitor.UsbProbe aac-selftest-decode
LuminaMonitor.UsbProbe decode-audio <capture.rtp> <sortie.wav> [--frame=480|512]
```

`aac-selftest-decode` fait tourner 18 contrôles hors ligne, sans téléphone : la trame silencieuse du
téléphone (`00 68 34 00`) contre son nombre de bits connu et sa sortie tout à zéro ; des trames
construites à la main avec des valeurs spectrales connues par canal, vérifiées contre un banc de
filtres nourri du même spectre directement ; une trame tronquée, qui doit être refusée au bit où
elle s'arrête ; aucune allocation sur 1 000 trames ; 800 trames légales aléatoires qui font passer
tous les livres, la substitution de bruit, la stéréo d'intensité et le TNS.

`decode-audio` rejoue une capture à travers le décodeur de bout en bout et écrit un fichier WAV —
48 kHz, stéréo, 16 bits, l'en-tête RIFF de 44 octets écrit à la main, sans bibliothèque — en mesurant
ce qui ne se voit pas à l'écoute : bits consommés par trame, continuité aux jointures de trames, RMS
et pic, échantillons non finis, temps de décodage.

### Ce qui a été mesuré, le 10 septembre 2026

- **`aac-tables-selftest` : 28/28.** Reconstruction parfaite du banc de filtres, résidu maximal
  1,3e-8 (voir `docs/AAC_ELD_TABLES.fr.md` § 4).
- **`aac-selftest-decode` : 18/18.** La trame silencieuse consomme 26 de ses 32 bits, bourrage nul,
  et produit 480×2 puis 512×2 échantillons nuls ; les trames construites en 480 et en 512 relisent
  exactement ce qui a été écrit, avec un écart ≤ 1,5e-8 contre la pleine échelle (32768) sur six
  trames consécutives ; 800 trames légales aléatoires : 0 désaccord de bits, 0 échantillon non fini,
  0,16–0,19 ms par trame.
- **La capture silencieuse `audio_default.rtp`** (1 211 trames, type de charge utile 101) :
  1 211/1 211 décodées, 26,0 bits par trame, 0 trame finissant trop tôt, trop tard, ou sur un
  bourrage non nul, sortie strictement nulle, 0,145 ms par trame contre un budget de 10 ms.
- **Une capture réelle avec musique** (30 s, 10 septembre 2026, 20h40, une vidéo qui joue sur le
  téléphone) : 3 029 trames, 372 octets par trame en moyenne, 0,30 Mbit/s, 101 paquets/s, aucune
  perte. Décodée en **480** : 3 029/3 029 décodées, 0 échec, 8 804 260 bits utilisés sur 8 814 776
  (99,9 %), 2 906,7 bits/trame en moyenne, 0 trame finissant trop tôt, trop tard, ou sur un bourrage
  non nul, pic +3,4 dBFS (238 échantillons écrêtés sur 2 907 840, 0,008 % — un dépassement normal sur
  les transitoires), RMS −16,2 dBFS, 0 valeur non finie, continuité aux jointures 1,398e-2 contre
  1,374e-2 ailleurs (rapport 1,017, donc pas de clic aux frontières de trames), 0,140 ms par trame en
  moyenne, 0,825 ms au pire. Décodée en **512** : 63 décodées, 2 966 échouées, premier échec à la
  trame 1 (« une section réclame 8 bandes depuis la bande 26, au-delà du `max_sfb` 34 ») — détail
  complet et conclusion (480) dans [`docs/AAC_ELD_TABLES.fr.md`](AAC_ELD_TABLES.fr.md) § 5.

Cela prouve que le lecteur de bits est exact : une erreur de syntaxe ferait finir une trame ailleurs
que sur son bourrage, ce qu'on observe exactement sur la passe 512 et jamais sur la passe 480, sur
3 029 trames. Cela ne prouve pas encore que la convention de phase du banc de filtres est celle
qu'a utilisée l'encodeur d'Apple — seule l'écoute de la sortie décodée le dit.

### Choix faits faute de certitude

Sept endroits où le texte du logiciel de référence ne suffisait pas seul, et où un choix a dû être
fait et noté plutôt que laissé implicite :

1. **Les longueurs TNS** sont comptées depuis le nombre total de bandes, puis bornées à
   `min(max_sfb, plafond)` — d'après `get_tns()` dans `huffdec2.c` et `tns.c` du logiciel de
   référence.
2. **L'échelle de sortie est ±1** via une constante `FullScale = 32768` ; le décodeur de référence
   écrit `time_sample_vector` tel quel en entiers 16 bits, sans mise à l'échelle propre.
3. **480 échantillons par trame par défaut**, malgré un `frameLengthFlag = 0` qui signifie 512 dans
   le logiciel de référence. Le pas d'horodatage RTP de la capture silencieuse (exactement 480 à
   chaque paquet) pointait déjà vers 480 ; celui de la capture réelle est moins net, mais la décoder
   tranche directement la question — le 480 passe de bout en bout, le 512 échoue dès la trame 1
   (§ 5 de `docs/AAC_ELD_TABLES.fr.md`).
4. **Une trame avec une valeur hors bornes est refusée, pas écrêtée** — le logiciel de référence ne
   tolère cela que sous ses drapeaux de protection d'erreur, à 0 ici.
5. **Le recouvrement d'une trame en échec est vidé (`Flush`), pas remis à zéro**, pour que la queue
   s'éteigne au lieu de claquer.
6. **`tns_data` est lu juste après son propre drapeau**, résilience à 0 et rien entre les deux.
7. **L'énergie PNS est `2^(0,25·énergie)` sur un bruit à énergie unité** issu d'un générateur
   congruentiel, avec la corrélation gauche/droite respectée.

### Limites

Pas de SBR basse latence, pas d'extensions ELD, pas de résilience HCR/RVLC, pas plus de deux
canaux — chacun refusé avec un motif typé plutôt que mal géré en silence. Aucun vecteur de
conformité au bit près n'existe pour comparer (l'ISO les vend séparément) ; les auto-tests prouvent
la cohérence interne et une consommation de bits exacte, pas une correspondance échantillon par
échantillon contre un décodeur de référence, faute d'en avoir un sur cette machine.

### État

**Confirmé à l'oreille le 10 septembre 2026.** La capture de 30 secondes, décodée avec les tables
de 480 échantillons et copiée dans un WAV, a été écoutée en regard de la vidéo qui jouait sur le
téléphone : même musique, aucun bruit, niveau normal. Les nombres ci-dessus (consommation de bits
exacte, pas d'écrêtage au-delà des transitoires normaux, pas de clic aux frontières de trames, pas
d'échantillon non fini) étaient nécessaires ; l'oreille était le test suffisant, et la convention
de phase du banc de filtres est bien celle de l'encodeur d'Apple. Prochaine étape : le rendu dans
l'app — une sortie WASAPI à choisir, le volume, et la synchronisation avec l'image.

## 11. Dans l'app

Écrit le 10 septembre 2026, le même soir que le décodeur, une fois répondues les deux premières
questions du §7. Cette section décrit du code, pas un projet : la chaîne tourne, les auto-tests la
mesurent, et la seule chose qui lui manque encore est une oreille sur le flux en direct.

### La chaîne, de bout en bout

```
téléphone ─ RTP/UDP dans le tunnel ─▶ AudioSession ─▶ AudioRenderer ─▶ AudioJitterBuffer ─▶ WasapiOutput ─▶ sortie
            une unité d'accès/paquet   RR RTCP 1/s     démux, séquence,   remplissage cible,   mode partagé,
            PT 101, +480 ts, 10 ms     BYE à l'arrêt   décodage AAC-ELD   saut / silence       événementiel
```

Six fichiers dans `src/LuminaMonitor.Core/Audio/` et un dans `Media/`, chacun avec un seul rôle :

| Fichier | Rôle |
|---|---|
| `Media/AudioStream.cs` | la session et la chaîne ensemble, plus le diagnostic de synchro |
| `Audio/AudioRenderer.cs` | la chaîne en un objet : démux, pertes, décodage, file — partagé avec la sonde |
| `Audio/AudioJitterBuffer.cs` | l'anneau de trames entre l'horloge du téléphone et celle de la carte son |
| `Audio/WasapiOutput.cs` | un flux de rendu en mode partagé, mené par l'événement du périphérique |
| `Audio/AudioSink.cs` | l'interface de sortie, et la sortie à sec qui n'ouvre aucun périphérique |
| `Audio/AudioFormat.cs` | le `WAVEFORMATEX` à la main, et la seule conversion que ce projet fait lui-même |
| `Audio/AudioOptions.cs`, `AudioStats.cs` | ce qu'on décide, et ce que la chaîne a compté |

### Sa place dans l'échelle

`DeviceSession` ouvre le son **après** l'image et **rattaché à elle** — l'offre audio porte
l'identifiant de session client du flux vidéo, ce que le §7.2 a prouvé fonctionnel. Trois propriétés
de cet ordre sont voulues :

- **le miroir est annoncé ouvert d'abord.** Le son s'ouvre sur une tâche à lui, dès que l'image est
  en place. La première version attendait six secondes (`AudioSettleMs`), en copiant la pause
  qu'`audio-info --video` avait utilisée dans l'essai qui a prouvé que les deux flux pouvaient
  partager une session ; le 11 septembre 2026, `audio-info --video --settle=0` a montré le téléphone
  acceptant l'offre audio sans aucune pause (400 paquets en quatre secondes, les deux flux fermés
  proprement), et avec une seconde. Les refus connus du service d'affichage sont entre deux
  *sessions* ; un second flux qui rejoint celle déjà ouverte n'en est pas une. La constante reste, à
  zéro, pour qu'une attente ait un nom si un téléphone en exigeait une un jour. Personne n'attend un
  son ; tout le monde attend une image ;
- **un refus n'est pas fatal.** L'image reste, le panneau dit pourquoi, et `OpenAudioAsync` peut être
  rappelé depuis le bouton du panneau. Le barreau audio est le seul de cette échelle qui ne peut pas
  faire échouer la montée ;
- **le son s'arrête sans `stopmediastream`.** Cet appel nomme une *session*, le flux audio partage
  celle de la vidéo, et la première version l'envoyait quand on coupait le son — l'image partait avec
  une seconde plus tard (11 septembre 2026, journal de la fenêtre). Mesuré dans l'autre sens le même
  jour avec `audio-info --video --stop=bye --hold=25` : rapports de réception arrêtés et BYE envoyé,
  l'image garde ses 117 paquets/s, le téléphone continue d'envoyer l'audio pendant les vingt secondes
  de son délai RTCP puis termine ce flux seul, et l'état d'après ne liste plus que la vidéo. Donc
  `AudioStream.StopAsync` ne dit rien au démon ; vingt secondes d'une capture que personne ne décode
  sont le prix d'une image qui reste. La fin de la session, elle, arrête toujours la session entière,
  audio compris, par l'arrêt de la vidéo ;
- **une relance vidéo rattache le son.** La relance arrête la session partagée, donc le flux audio
  meurt avec elle : `DeviceSession` le lâche d'abord (sans toucher à la touche Muet — le compte tient,
  le téléphone reste en sourdine pendant le trou) et en ouvre un nouveau sur la nouvelle session dès
  que l'image est revenue ;
- **la garde reste à l'entrée.** `AudioSession.StartAsync` libère toujours les sessions média
  orphelines avant d'ouvrir la sienne, en épargnant la session vidéo qu'elle rejoint (§6). Cela compte
  plus pour l'audio que pour la vidéo : une session audio qu'iOS n'a jamais eu l'ordre de terminer est
  une capture du son système qu'il n'a pas rendue, et tant qu'elle tient, le micro du téléphone est
  indisponible pour ses autres apps.

### Le tampon de gigue, et le retard

Le téléphone produit une trame toutes les dix millisecondes ; le périphérique réclame une période
quand ça lui convient. La file entre les deux est amorcée jusqu'à un **remplissage cible** —
`audioDelayMs`, 50 ms par défaut — et ce remplissage *est* le réglage de retard. Trois règles, toutes
comptées :

- **sous-alimentation** : la sortie réclame des échantillons qui ne sont pas là. Elle reçoit du
  silence, le compteur avance d'un, et la file repart en amorçage — un trou plus long plutôt qu'une
  série de courts ;
- **dérive** : les deux horloges ne sont pas la même, donc au bout de quelques minutes l'une gagne.
  Au-delà de la cible plus trois trames (30 ms de marge), les trames **les plus anciennes** sont
  jetées, jusqu'à revenir à la cible en une seule coupure, et comptées. Une coupure, pas une par
  arrivée : la première version ne rognait que jusqu'à la marge puis coupait une fois par trame tant
  que durait la rafale du téléphone — quinze coupures en dix secondes autour d'un verrouillage
  d'écran, le 11 septembre 2026. Rien ne s'accumule sans borne ;
- **perte** : un trou dans les numéros de séquence RTP est comblé par autant de trames silencieuses,
  jusqu'à 200 ms, pour que la ligne du temps ne raccourcisse pas. Une trame que le décodeur refuse est
  mise en file quand même — ce qu'elle contient est la queue du recouvrement qui s'éteint, plus
  discrète qu'un clic. Sauter, à l'inverse, jouerait tout ce qui suit en avance, définitivement.

Pourquoi 50 ms par défaut : une image met environ 96 ms de l'écran du téléphone à celui-ci (mesuré,
`clock-test`), le chemin du son est plus court — pas de file de décodeur, pas de fenêtre où présenter
— donc laissé seul il arrive avant. Cinquante, c'est à peu près la différence. L'oreille a le dernier
mot, et c'est pourquoi c'est un curseur de 0 à 300 ms.

### La sortie

Mode partagé, événementiel, 48 kHz stéréo flottant 32 bits — exactement ce que produit le décodeur —
avec `AUDCLNT_STREAMFLAGS_EVENTCALLBACK | AUTOCONVERTPCM | SRC_DEFAULT_QUALITY`. Le moteur audio fait
lui-même le rééchantillonnage et le remixage vers ce que tourne le périphérique, et c'est pourquoi **ce
projet n'embarque aucun rééchantillonneur**. Un pilote qui refuse ces drapeaux se voit répondre par le
format de mixage du moteur, lu avec `GetMixFormat`, annoncé dans le journal, et accepté seulement si
les échantillons peuvent s'y poser — un format de mixage à une autre fréquence est refusé plutôt que
mal rééchantillonné.

Le périphérique est celui nommé dans les réglages, ou la sortie par défaut de Windows — **le rôle
Console, jamais Communications** : Windows garde deux défauts et, sur le PC d'essai de ce projet, celui
des communications est un câble virtuel qui alimente autre chose. Un périphérique choisi qui disparaît
se voit remplacé par le défaut, une fois, avec une ligne au journal. Volume et sourdine sont un gain
appliqué aux échantillons à la sortie, jamais le mélangeur système : ce périphérique est partagé avec
tout le reste de la machine.

### Le haut-parleur du téléphone

Le flux est une prise, pas un détournement : le téléphone continue de jouer dans la pièce pendant que
le même son joue dans le casque cent millisecondes plus tard. L'idée était de presser la touche Muet
du téléphone pour garder la pièce silencieuse, et une première mesure semblait la bénir — `audio-info
12 --press=mute@4 --press=mute@8`, **flux audio seul**, gardait le RMS de la capture plein pendant le
mute (0,26 · 0,22 · 0,22), ce qui se lit « la prise est avant le volume ».

**Cette mesure était sans le miroir, et elle était fausse.** Refaite avec le flux d'affichage actif —
`audio-info 15 --video --press=mute@4 --press=mute@10` — le RMS par seconde vaut 0,08 · 0,07 · 0,06
puis **0,000 dès la quatrième seconde**, à l'instant de l'appui Muet, et le second appui censé le
défaire n'y change rien ; volume-baisse (`--press=volume-down`) fait exactement pareil. Avec un flux
d'affichage actif, un événement de volume/muet Consumer fait que certaines apps — Apple Music au
premier chef — cessent d'alimenter la capture du son système **définitivement**, tout en jouant au
haut-parleur. Le téléphone est entendu, le casque muet : l'opposé du but, et les captures de flux
neuf qui sonnaient toujours bien étaient celles sans vidéo à côté. Faire taire le téléphone et
capturer son son sont incompatibles pour ces apps, donc `AudioOptions.SilencePhone` est **désactivé
par défaut** (l'interrupteur « Faire taire l'iPhone pendant ce temps » du panneau, avec un
avertissement) ; laissé désactivé, rien ne touche le téléphone et la capture reste entière. Le seul
chiffre qui l'attrape dans l'app est le niveau crête décodé, sur chaque ligne de compteurs en
`niveau …` : `niveau silence` alors que le téléphone joue de toute évidence, c'est ce défaut.

### Apple Music, et la protection de contenu

Il y a une source que la chaîne audio ne peut pas transporter, et ce n'est pas de notre fait :
**Apple Music, quand le miroir est actif.** Mesuré le 11 septembre 2026, un flux unique et propre,
aucun Muet pressé, rien touché — `audio-info 40 --video` sur un morceau Apple Music en lecture — le
RMS par seconde décodé vaut 0,20 · 0,21 · 0,14 puis **0,000 pendant les trente-sept secondes
restantes** : le téléphone continue d'envoyer ses cent trames par seconde, toutes décodées sans
erreur, toutes silencieuses, pendant que le morceau joue au haut-parleur du téléphone. Environ trois
secondes de son, puis plus rien.

Le déclencheur est le **flux d'affichage**. Sans lui — `audio-info` sans `--video` — le même morceau
se capture plein tant qu'on l'écoute. Avec lui, iOS traite le flux média comme un enregistrement
d'écran, et Apple Music est protégé par DRM contre l'enregistrement d'écran : après quelques secondes
de grâce, il cesse d'alimenter la route captée et ne joue plus qu'au haut-parleur. C'est la même
raison qui rend muet un enregistrement d'écran d'iPhone sous Apple Music. Il n'existe aucun champ
dans la négociation pour désactiver la protection de contenu, et il ne doit pas en exister ; rien ici
ne tentera de le faire. Les sources non protégées — un navigateur, un jeu, la plupart des apps — ne
sont pas concernées, et c'est pourquoi toutes les autres sources marchent. `niveau silence` sous un
morceau Apple Music en lecture, c'est cela : un mur du côté d'Apple, pas un défaut du nôtre.

### Ce qu'elle compte, et où le lire

`AudioStats` — trames reçues, décodées, en échec ; paquets perdus et en désordre ;
sous-alimentations ; trames sautées ; remplissage de la file face à sa cible ; périphérique et format
actifs ; l'écart son-image. Trois endroits le lisent :

- le **panneau Audio**, à quatre hertz, en une phrase : lecture sur *périphérique*, file *n* ms, et le
  nombre de coupures s'il y en a ;
- le **journal**, dans la ligne de compteurs — un bloc `AUDIO` à chaque ligne d'une exécution
  `--diagnostic`, et toutes les dix secondes sinon. C'est celui qu'on lit après une séance sans
  témoin ;
- la **sonde**, `audio-play`, qui rejoue une capture à travers cette chaîne même.

### Le diagnostic de synchro

Les deux flux publient des rapports d'émetteur RTCP portant l'horloge NTP du téléphone, la seule qu'ils
aient en commun. Toutes les cinq secondes, une ligne au journal : le retard total du son (le délai de
bout en bout du rapport d'émetteur, plus la file, plus la latence annoncée du périphérique) face à
celui de l'image (son propre délai de bout en bout, plus la file du décodeur et le décodeur). La
différence est ce qu'un auditeur entend comme synchro labiale, et c'est son signe qui sert — positif
veut dire que le son est derrière l'image, donc que le retard doit baisser.

**Elle ne corrige rien.** Le dernier étage de l'image — la fenêtre qui l'affiche — est du côté de
l'application et n'entre pas dans le chiffre : l'écart est donc mesuré jusqu'à la sortie du décodeur, et
le retard réel de l'image est d'autant plus grand. La mesure est ce dont une version ultérieure aurait
besoin pour remplacer le retard fixe ; celle-ci se contente de l'écrire.

### Mesuré le 10 septembre 2026

`audio-play … --dry` — la chaîne entière, la sortie tirant sur un chronomètre à 48 kHz au lieu de
l'événement d'un pilote :

| capture | trames | échecs | sous-alim. | sauts | file en ms, cible 50 (moy/min/max) | CPU | allocation |
|---|---|---|---|---|---|---|---|
| synthétique, 200 trames silencieuses | 200 | 0 | 0 | 0 | 50,0 / 50,0 / 50,0 | 3,1 % | **0 o/trame** |
| `audio_default.rtp`, silencieuse, 12,1 s | 1 211 | 0 | 0 | 0 | 43,5 / 20,0 / 70,0 | 1,4 % | **0 o/trame** |
| la capture musicale de 30 s | 3 029 | 0 | 0 | 0 | 45,4 / 10,0 / 70,0 | 1,1 % | **0 o/trame** |

L'amplitude de la colonne « file » vient du banc d'essai, pas de la chaîne : les deux bouts d'une
exécution à sec sont cadencés par `Thread.Sleep`, ce qui vaut environ une milliseconde de chaque côté,
et quarante de ces millisecondes dans le même sens font le minimum de 10 ms de la capture musicale. En
direct, l'alimentation est le rythme propre du tunnel et la lecture l'événement propre du périphérique,
tous deux plus réguliers. Ce que le tableau prouve, en revanche, est la part qu'aucune écoute ne
montrerait : chaque trame lue, rien de jeté, rien d'alloué par trame, et environ un pour cent d'un cœur
pour du stéréo 48 kHz en temps réel.

L'intégration continue joue la capture synthétique à chaque poussée, avec `--delay=150` plutôt que 50 :
un runner partagé peut se figer plus longtemps qu'un coussin de 50 ms, et un test qui passe au rouge
pour l'ordonnancement du runner ne dit rien de la chaîne.

### Limites

- **Aucun alignement automatique.** Voir plus haut, et §7.5.
- **Aucun rééchantillonneur.** Si `AUTOCONVERTPCM` est refusé *et* que le moteur mélange ailleurs qu'à
  48 kHz, il n'y a pas de son et le journal le dit exactement. Jamais vu sur cette machine : la sortie
  par défaut mélange en 48 kHz stéréo flottant, les échantillons sont donc copiés tels quels.
- **Ni micro ni Bluetooth**, ici ni ailleurs, et ni l'un ni l'autre ne revient : §5 et §9.
- **L'oreille n'a pas encore jugé le flux en direct.** Le décodeur a été confirmé à l'oreille sur une
  capture (§10) ; la chaîne autour de lui a été mesurée mais pas écoutée, parce que l'application n'est
  pas lancée par la session qui l'a écrite.
