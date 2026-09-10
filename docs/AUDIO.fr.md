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

1. **Le son réel** : `audio-info 10 son.rtp` avec une musique qui joue sur le téléphone (débit, taille
   des trames, contenu des charges utiles).
2. **Audio et vidéo dans la même session**, avec le même `avcMediaStreamOptionClientSessionID`, comme
   le miroir de Xcode : `audio-info 5 --video`. Le code est écrit et la garde épargne la session
   vidéo ; les trois essais du 9 septembre sont tombés sur un service d'affichage devenu muet
   (« pas de SETTINGS du téléphone en 3 s »), dont le remède connu — démonter l'image développeur —
   exige un téléphone **déverrouillé**, ce qu'il n'était plus.
3. Les variantes de paliers (`paliers-sans-codec`, `paliers-codec-seuls`), pour finir d'éliminer la
   table de paliers comme lieu du choix de codec.
4. Un flux **vidéo** laissé orphelin gêne-t-il aussi le micro ? C'est la seule hypothèse qui
   expliquerait le symptôme avant l'existence du chemin audio.

## 8. Les commandes

```
LuminaMonitor.UsbProbe aac-selftest                     # hors ligne : Windows décode-t-il l'AAC-ELD ?
LuminaMonitor.UsbProbe audio-info [sec] [capture.rtp] [--variant=…] [--video] [--direction=…]
LuminaMonitor.UsbProbe media-status [sec]               # ce que le téléphone croit en cours
LuminaMonitor.UsbProbe media-release                    # ferme les sessions orphelines
LuminaMonitor.UsbProbe audio-leak-test                  # ouvre un flux et ne le ferme PAS (mesure)
```

Variantes de `audio-info` : `default`, `f2:<n>`, `f3:<n>`, `f4:<n>`, `f5:<n>`, `f6:<n>`,
`paliers-sans-codec`, `paliers-codec-seuls`, combinables avec `+`.
