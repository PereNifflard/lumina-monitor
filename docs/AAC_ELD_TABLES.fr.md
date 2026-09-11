# Les tables de l'AAC-ELD — ce qu'elles sont, d'où elles viennent, comment elles sont vérifiées

[English](AAC_ELD_TABLES.md) · **Français**

Écrit le 10 septembre 2026. Le sujet, ce sont les *données* dont un décodeur AAC-ELD a besoin et
qu'il ne peut pas inventer : les livres de Huffman, les arêtes des bandes de facteurs d'échelle, les
plafonds TNS, et la fenêtre du banc de filtres basse latence. Le code qui devait s'en servir
n'existait pas encore quand cette page a été écrite pour la première fois ; il existe désormais,
écrit le soir même une fois ces tables réunies — voir [`docs/AUDIO.fr.md`](AUDIO.fr.md), « Le
décodeur maison ». Cette page reste consacrée à la donnée qui le sous-tend, et à la seule décision
de licence que cette donnée imposait (§2).

**La conclusion en une ligne :** toutes les tables nécessaires pour décoder l'AAC-ELD du téléphone
**sans SBR** viennent du **logiciel de référence que l'ISO publie gratuitement**, et la plus
critique — la fenêtre basse latence — est prouvée en faisant passer un signal par l'analyse puis la
synthèse et en le retrouvant à une part sur 10⁸ près. Une contradiction restait dans la
signalisation du téléphone lui-même : son AudioSpecificConfig annonce 512 échantillons par trame, le
fil en montre 480. Les deux jeux de tables sont dans le dépôt pour cette raison, et le §5 ci-dessous
porte désormais le verdict entre les deux, tranché en décodant une capture réelle : 480.

## 1. D'où viennent les nombres

| | |
|---|---|
| Document | **ISO/IEC 14496-5:2001/Amd 43:2018**, *Coding of audio-visual objects — Part 5: Reference software*, insert électronique `14496-5_Amd43_inserts.zip` |
| Téléchargé depuis | <https://standards.iso.org/iso-iec/14496/-5/ed-2/en/amd/43/> — le portail de maintenance des normes ISO, **gratuitement**, sans compte |
| Récupéré le | 10 septembre 2026 |
| Sous-arbre utilisé | `audio/natural/mp4AudVm_Rewrite/src_tf/` — le décodeur de référence MPEG-4 Audio, AAC-(E)LD compris |
| Provenance de l'ELD à l'intérieur | le décodeur ELD est entré dans cet arbre avec **l'ISO/IEC 14496-5:2001/Amd 24:2009, « Reference software for AAC-ELD »** |

La partie 5 de la norme *est* le logiciel de référence, et elle est normative : un décodeur conforme
nourri d'un flux conforme doit sortir ce qu'il sort. L'ISO donne les inserts électroniques, et celui
de l'amendement 43 embarque tout l'arbre audio et pas seulement le sujet de l'amendement — voilà
pourquoi un amendement sur les niveaux ALS et le SBR est le véhicule de la fenêtre ELD.

Deux autres voies ont été examinées puis écartées :

- **L'ISO/IEC 14496-3 lui-même** (la norme audio, où ces données figurent comme tables normatives)
  se vend, il ne se publie pas. Une copie qui circule sur le web a été ouverte : c'est un PDF
  chiffré par mot de passe propriétaire, et elle a été laissée de côté — rien ici n'exigeait de
  contourner cela.
- **FFmpeg, faad2 et compagnie** n'ont pas été consultés, et **FDK-AAC** n'a pas été ouvert du tout.
  Rien dans ce dossier n'est passé par une implémentation logicielle autre que celle de l'ISO.

## 2. La licence — décision prise, 10 septembre 2026

Chaque fichier du logiciel de référence porte la notice de module logiciel MPEG, reproduite mot pour
mot dans `TablesProvenance.MpegSoftwareModuleNotice` et maintenant aussi, intégralement, dans
[`Tables/NOTICE.fr.md`](../src/LuminaMonitor.Core/Media/Aac/Tables/NOTICE.fr.md). En substance :

- l'ISO/IEC accorde une **licence gratuite** sur le module « or modifications thereof » pour un usage
  dans des produits matériels ou logiciels **se déclarant conformes à la norme MPEG-4 Audio** ;
- le droit d'auteur n'est **pas** libéré pour les produits non conformes ;
- la notice **doit accompagner toute copie ou œuvre dérivée** ;
- l'utilisateur est averti que le code **peut enfreindre des brevets existants** (l'AAC-ELD est un
  travail Fraunhofer de 2008 ; les brevets de l'AAC de base sont largement expirés, ceux de l'ELD
  pas nécessairement).

Les tables de `src/LuminaMonitor.Core/Media/Aac/Tables/` ne sont donc **pas sous MIT**. Ce sont des
données tierces sous cette notice, posées dans un dépôt MIT, et la notice voyage avec elles (le
`NOTICE.fr.md` de ce dossier). Une seconde condition tire un peu dans l'autre sens : la page de
téléchargement de l'ISO pour les inserts électroniques parle d'un usage « in their original format
without any modifications », là où la notice de module autorise expressément les modifications — et
transcrire du C en C# est une modification de format. Cette zone grise est énoncée comme un fait
ci-dessous et laissée non tranchée comme question juridique ; ce projet ne donne aucun avis
juridique, ici ni ailleurs.

**La décision (option A) :** les tables restent, isolées dans ce seul dossier, sous la notice
ci-dessus — et non sous les termes MIT de ce dépôt — et le dépôt le dit par écrit, dans
[`Tables/NOTICE.fr.md`](../src/LuminaMonitor.Core/Media/Aac/Tables/NOTICE.fr.md). Les faits qui y ont
conduit :

1. Rien dans ces fichiers ne fait tourner quoi que ce soit d'étranger sur le PC : ce sont des
   constantes numériques lues par index, comme toute autre table d'ici. La règle du projet « aucun
   code tiers ne s'exécute » n'est pas affectée par cette décision — ce qui change, c'est la pureté
   de la licence, pas ce qui s'exécute. Le code propre à ce dépôt reste MIT ; ce seul dossier de
   données normatives porte la notice ISO/MPEG à la place.
2. « Se déclarer conforme à MPEG-4 Audio » est la condition de la licence, et un décodeur du flux
   ELD du téléphone est exactement cette déclaration — faite délibérément, notice jointe, plutôt
   qu'évitée.
3. L'AAC-ELD est couvert par des brevets actifs (Fraunhofer IIS, licenciés via le guichet Via
   Licensing), quelle que soit la provenance des nombres — acheter l'ISO/IEC 14496-3 ou lire les
   tables de FFmpeg n'y aurait rien changé. L'usage de ce projet est gratuit et non commercial, la
   situation de tout décodeur AAC open source ; quiconque voudrait un produit commercial construit
   sur ce code ou ces tables a besoin de sa propre licence de brevets, que rien ici ne fournit.

Les deux solutions de rechange examinées puis écartées étaient pires pour un dépôt MIT public :
acheter l'ISO/IEC 14496-3 (≈ 200 CHF) donne les mêmes nombres sous une licence qui interdit
franchement la redistribution, et les tables de FFmpeg sont en LGPL, une licence sur du *code* dont
le projet s'est privé par principe.

## 3. Ce que contient chaque fichier

Tout est `internal static`, données seules, sans autre logique qu'une recherche d'index.

| Fichier | Contenu | Source dans le logiciel de référence |
|---|---|---|
| `TablesProvenance.cs` | le document, l'URL, la date, et la notice MPEG mot pour mot | — |
| `HuffmanTables.cs` | la forme des douze livres : dimension, LAV, signé ou non, mot le plus long ; l'accès par numéro de livre du flux | `huffinit.c` (appels à `hufftab()`), `interface.h` (`HUFnSGN`) |
| `HuffmanCodebooks1To6.cs` | longueurs et valeurs des mots de code, livres 1 à 6 (81 entrées chacun) | `hufftables.c`, `book1`…`book6` |
| `HuffmanCodebooks7To11.cs` | livres 7-8 (64), 9-10 (169), 11 (289) | `hufftables.c`, `book7`…`book11` |
| `HuffmanScalefactorBook.cs` | le livre des facteurs d'échelle, 121 entrées, l'index − 60 est la différence | `hufftables.c`, `bookscl` |
| `ScalefactorBands.cs` | `swb_offset` pour 480 et 512 lignes dans les trois découpages basse latence, avec un 0 ajouté en tête | `decdata.c`, `sfb_48_480`…`sfb_24_512`, associés par `samp_rate_info` |
| `TnsTables.cs` | les plafonds de bandes TNS basse latence par fréquence et longueur de trame, et le plafond d'ordre | `decdata.c`, `tns_max_bands_tbl_low_delay`, `tns_max_order()` |
| `EldWindow.cs` | quelle fenêtre va avec quelle longueur de trame, et le décalage de synthèse d'un quart de trame | `imdct.c` |
| `EldWindow480.cs` | 1920 coefficients | `win480LD.h`, `WIN480LD` |
| `EldWindow512.cs` | 2048 coefficients | `win512LD.h`, `WIN512LD` |

Ce qu'il faut savoir avant de s'en servir :

- **48 kHz couvre cinq fréquences d'échantillonnage.** Le logiciel de référence fait pointer les
  index 0 à 4 (96, 88,2, 64, 48, 44,1 kHz) sur la même paire de tables de bandes. 32 kHz a la
  sienne, et tous les index de 24 kHz vers le bas partagent une troisième. Les trois paires sont là.
- **Les tables de bandes ont gagné un zéro en tête.** Le logiciel de référence liste les *fins* de
  bande ; ici la bande `b` couvre `[offsets[b], offsets[b+1])` et la dernière entrée est la longueur
  de trame.
- **La précision de la fenêtre est de huit décimales**, telles que le décodeur de référence les
  imprime et les utilise. Le même insert contient aussi `win512LD2.h`, la fenêtre 512 à dix chiffres
  significatifs, que rien dans ce décodeur n'inclut ; elle reconstruit à 4,3 × 10⁻¹¹ au lieu de
  1,5 × 10⁻⁸ — la preuve que les deux fichiers portent une seule fenêtre et que le résidu est
  l'impression, pas la mathématique. 10⁻⁸, c'est 157 dB sous la pleine échelle : la copie retenue est
  celle que le décodeur de référence emploie.

## 4. Comment elles sont vérifiées — `aac-tables-selftest`

    LuminaMonitor.UsbProbe aac-tables-selftest

Hors ligne, sans téléphone, code de sortie 0 seulement si les 28 vérifications passent. Ce que chaque
famille doit démontrer :

- **Livres de Huffman.** Le nombre d'entrées contre la dimension et le LAV — (2·LAV+1)^dim si signé,
  (LAV+1)^dim sinon. Chaque mot de code tient dans sa propre longueur. La somme de Kraft, calculée en
  entiers sur un dénominateur de 2^longueur maximale, doit valoir **exactement 1** (code préfixe
  complet). Aucun mot ne doit en préfixer un autre : chaque code est tronqué à toutes les longueurs
  plus courtes et recherché dans l'ensemble. Enfin chaque mot est décodé bit à bit par une table
  construite depuis les données et doit retomber sur son propre index. Les douze livres passent les
  cinq contrôles, avec Kraft = 1 exactement pour chacun.
- **Tables de bandes.** Départ à 0, strictement croissantes, chaque bande large d'un multiple de
  quatre lignes, dernière arête égale à la longueur de trame, nombre de bandes conforme à l'annonce.
- **Plafonds TNS.** Chaque plafond tient dans la table de bandes à laquelle il s'applique ; le
  plafond d'ordre est sensé. À 32 kHz le plafond égale exactement le nombre de bandes (37 sur 37),
  c'est le cas le plus serré et il passe.
- **La fenêtre ELD — le vrai test.** Le banc de filtres basse latence est implémenté dans la sonde à
  partir de sa définition, en somme directe, sans algorithme rapide :

      analyse   : X[k] = -2 · Σ(i = 0…4M-1) xw[i] · cos(π/M · (i - 2M + (1-M)/2) · (k + ½))
      synthèse  : y[n] = (-1/M) · Σ(k = 0…M-1) X[k] · cos(π/M · (n + (1-M)/2) · (k + ½))

  avec `M` la longueur de trame, `xw` le bloc d'entrée de quatre trames multiplié par la fenêtre
  **retournée**, et `y` fenêtré dans l'ordre naturel puis additionné par recouvrement d'une trame à
  la fois, lu un quart de trame plus loin. Un bruit, un sinus à 997 Hz et un train d'impulsions
  passent par l'analyse puis la synthèse ; le test cherche le retard qui aligne la sortie sur
  l'entrée et rapporte l'erreur maximale en régime établi.

  | trame | retard mesuré | erreur maximale, bruit / sinus / impulsion |
  |---|---|---|
  | 480 | 360 échantillons | 1,33e-8 / 1,15e-8 / 3,96e-9 |
  | 512 | 384 échantillons | 1,46e-8 / 1,05e-8 / 2,94e-9 |

  Le plafond est à 5 × 10⁻⁸, trois fois le plancher. C'est là que le test a des dents : déplacer
  **un seul** coefficient de la fenêtre 512 de 10⁻⁶ — un chiffre faux à la sixième décimale, la plus
  petite erreur de transcription qui mérite ce nom — porte le résidu à 1,5 × 10⁻⁷ et fait échouer la
  passe avec le code 9. Cela a été fait exprès, puis défait ; un test qui ne peut pas échouer ne
  prouve rien.

Le retard de 0,75 · M échantillons est une propriété du découpage que ce test emploie, pas un chiffre
recopié de la norme, et il est vérifié pour qu'un changement de convention se manifeste comme un
échec plutôt que comme un décodeur discrètement en retard.

Au-delà de l'auto-test, chaque tableau du dépôt a été comparé valeur par valeur au fichier du
logiciel de référence dont il sort : 1920 + 2048 coefficients de fenêtre, 2724 nombres de Huffman,
212 arêtes de bandes, 32 plafonds TNS, zéro écart. La transcription est scriptée pour les longues
tables et manuelle pour les seules tables de bandes — c'est justement pourquoi cette comparaison
existe.

## 5. 480 ou 512 échantillons par trame — le verdict

L'AudioSpecificConfig du téléphone tient en quatre octets, `F8 E6 40 00`. Lu bit à bit comme
l'ISO/IEC 14496-3 §1.6.2.1 et l'ELDSpecificConfig les disposent (l'ordre des champs confirmé contre
`advanceELDspecConf()` du logiciel de référence) :

| bits | champ | valeur |
|---|---|---|
| 0–4 | `audioObjectType` | 31 → échappement, six bits de plus |
| 5–10 | `audioObjectTypeExt` | 7 → **objet 39, ER AAC ELD** |
| 11–14 | `samplingFrequencyIndex` | 3 → **48 000 Hz** |
| 15–18 | `channelConfiguration` | 2 → **stéréo** |
| 19 | `frameLengthFlag` | **0** |
| 20–22 | les trois drapeaux de résilience | 0, 0, 0 |
| 23 | `ldSbrPresentFlag` | **0 → pas de SBR** |
| 24–27 | `eldExtType` | 0 → `ELDEXT_TERM`, la config se termine |
| 28–31 | bourrage | 0 |

Le décodage tient : chaque champ tombe sur une valeur légale et le type d'extension termine
exactement à la fin des quatre octets, ce qu'un décalage d'un bit ne produirait pas.

**Le sens de `frameLengthFlag` n'est pas ambigu.** Le logiciel de référence dit la même chose à trois
endroits — `dec_tf.c:300`, `confldsbr.c:212`, `streamfile_diagnose.c:417` — tous
`frameLengthFlag ? 480 : 512`. La configuration demande donc **512**.

**Le fil dit 480.** Remesuré ici sur `audio_default.rtp`, la capture de ce dépôt : 1211 paquets de
type de charge utile 101 sur 12,094 s ; l'horodatage RTP avance de **480 exactement à chaque paquet**
(1210 pas sur 1210), ce qui à 100,05 paquets/s donne 48 023 tops/s — une horloge d'échantillonnage à
48 kHz. Si les trames avaient fait 512 échantillons, le téléphone aurait envoyé 93,75 paquets/s et le
même pas de 480 tops se serait lu comme une horloge à 45 000 Hz. Ce n'est pas le cas.

**Le verdict.** Croire le fil pour la durée : **480 échantillons, 10 ms par trame**, et traiter le
`frameLengthFlag` comme un défaut de l'encodeur d'Apple.

Cette capture silencieuse, seule, ne pouvait pas dire quel *jeu de tables* code le flux. Chacune de
ses 1211 charges utiles fait les mêmes quatre octets, `00 68 34 00`, qui se lisent comme une trame
ELD stéréo complète et **vide** : six bits de `max_sfb` à zéro, deux bits de `ms_mask_present` à
zéro, puis pour chacun des deux canaux un `global_gain` de **104** et un `tns_data_present` à zéro —
26 bits de trame et six bits de bourrage, soit exactement quatre octets. Cette lecture était une
hypothèse quand cette page a été écrite pour la première fois, l'ordre des champs de l'ELD n'ayant
pas encore été lu dans la norme ; elle ne l'est plus — `EldSyntax.cs` met désormais en œuvre cet
ordre, et il confirme la lecture ci-dessus (échanger `max_sfb` et `ms_mask_present` aurait donné les
mêmes valeurs de toute façon, le premier octet étant nul dans les deux cas). Ce qui n'a jamais été
une hypothèse, c'est que les deux canaux tombent sur le même gain global, ce à quoi ressemble une
trame stéréo silencieuse. Et `max_sfb = 0` signifie qu'**aucune bande n'est codée** : la trame ne
touche jamais une table de bandes, elle ne peut donc pas distinguer 35 bandes de 36.

À noter pour qui lira l'analyseur : l'ELD n'a pas d'`ics_info()`. Là où l'AAC-LC en enverrait un, le
décodeur de référence lit un simple **`max_sfb` de six bits** (`LEN_MAX_SFBL`, `huffdec2.c:1658`, la
branche prise quand le flux est de l'ELD), et `max_sfb == 0` met le nombre total de bandes à zéro —
voilà comment une trame transporte du silence en quatre octets.

C'est pourquoi **les deux jeux de tables sont dans le dépôt**, et pourquoi le décodeur écrit par la
suite prend la longueur de trame en paramètre (`AudioSpecificConfig.WithFrameLength`) plutôt que
d'en supposer une.

### Tranché par une capture réelle — 10 septembre 2026, 20h40

Le test proposé plus haut a été exécuté une fois qu'une capture avec du vrai son a existé : 30 s
avec une vidéo qui joue sur le téléphone, 3029 trames de type de charge utile 101, 372 octets par
trame en moyenne, 0,30 Mbit/s, 101 paquets/s, aucune perte — et, contrairement à la capture
silencieuse, pas de pas d'horodatage RTP propre à 480,0 par paquet. En la décodant deux fois, une
fois par jeu de tables :

| longueur de trame | résultat |
|---|---|
| **480** | **3029/3029 décodées, 0 échec.** 8 804 260 bits utilisés sur 8 814 776 (99,9 %), 2 906,7 bits/trame en moyenne, 0 trame finissant trop tôt, trop tard, ou sur un bourrage non nul. |
| **512** | **63 décodées, 2 966 échouées.** Premier échec à la trame 1 : une section réclame 8 bandes depuis la bande 26, au-delà du `max_sfb` 34 — la table de bandes 512 ne correspond pas à ce flux. |

480 est la vérité du fil, pas seulement pour la capture silencieuse mais pour un flux qui porte du
contenu réel. Le `frameLengthFlag` de l'AudioSpecificConfig du téléphone — qui demande 512 — est un
défaut de la signalisation de l'encodeur d'Apple, pas une indication sur les tables à charger.

Ce que ça prouve : le lecteur de bits est exact — une erreur de syntaxe en 512 désynchronise les
données de section presque tout de suite, ce qui est exactement ce qui s'est produit, et rien de
tel ne s'est produit en 480 sur 3029 trames. Ce que ça ne prouve pas à soi seul : que la convention de
phase du banc de filtres est celle qu'a utilisée l'encodeur d'Apple. Seule l'écoute de la sortie
décodée le dit, et elle l'a dit le 10 septembre 2026 : la capture décodée sonne comme la vidéo qui
jouait — voir [`docs/AUDIO.fr.md`](AUDIO.fr.md), « Le décodeur maison ».

## 6. Ce qui manque, et ce qui n'est pas nécessaire

Pas nécessaire — une formule, pas une table :

- **quantification inverse**, `|x|^(4/3)` : calculée ;
- **coefficients du filtre TNS** : déquantifiés par `sin(coef / iqfac)` avec
  `iqfac = ((1 << (coefRes-1)) ∓ 0,5) / (π/2)`, puis la récursion habituelle des coefficients de
  réflexion vers le LPC — la formule est dans `tns.c`, il n'y a pas de table ;
- **gains des facteurs d'échelle**, `2^(0,25 · (sf − 100))` : calculés ;
- **PNS** (substitution de bruit perceptuelle) : le niveau de bruit vient du facteur d'échelle et le
  générateur lui-même n'est pas normatif, donc aucune table ;
- **stéréo d'intensité** : la position est un facteur d'échelle, pas de table ;
- **formes de fenêtre** : l'ELD n'a qu'une fenêtre, pas de bit de forme, pas de blocs courts, pas de
  séquence de fenêtres — c'est pourquoi il n'y a ici ni table sinus ni table KBD.

Réellement manquant :

- **La syntaxe binaire de l'ELD** — l'ordre des champs de `ELDraw_data_block()`, la largeur des
  champs de section, de facteurs d'échelle et de TNS, les règles d'échappement — manquait vraiment
  quand cette page a été écrite ; cette tâche portait sur les tables, pas sur la logique de
  décodeur. Elle ne manque plus : elle a été lue dans le même insert (`decoder_tf.c`, `huffdec2.c`,
  `tns.c`, même licence) et écrite dans `src/LuminaMonitor.Core/Media/Aac/` le soir même. Voir
  [`docs/AUDIO.fr.md`](AUDIO.fr.md), « Le décodeur maison ».
- **Les tables du SBR.** Inutiles tant que `ldSbrPresentFlag` vaut 0, et il vaut 0. Si le téléphone
  proposait un jour de l'ELD avec SBR, c'est un second lot de livres de Huffman et de tables de
  bandes de fréquence à récupérer.
- **Les extensions ELD** (LD-MPS, SAOC). `eldExtType` termine immédiatement : rien à décoder, rien à
  récupérer.
- **Un vecteur de conformité au bit près.** L'ISO vend des flux de conformité (partie 4) ;
  l'auto-test prouve que les tables sont cohérentes entre elles et que le banc de filtres
  reconstruit, ce qui est fort sur les données mais n'est pas une comparaison d'audio décodé contre
  un décodeur de référence.

## 7. Refaire la récupération

Télécharger l'insert depuis l'URL du §1, le décompresser, et les quatre fichiers source sont sous
`audio/natural/mp4AudVm_Rewrite/src_tf/` : `hufftables.c`, `decdata.c`, `win480LD.h`, `win512LD.h`.
Rien d'autre dans ce dépôt ne dépend de ce téléchargement — les tables sont transcrites, l'insert
n'est pas embarqué, et il n'a jamais été placé dans le dépôt.
