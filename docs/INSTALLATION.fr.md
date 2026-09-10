[English](INSTALLATION.md) · **Français**

# Installation

Ce guide part d'un PC nu et s'arrête quand l'écran de l'iPhone est dans une
fenêtre Windows et répond à la souris. Compte **dix minutes**, dont la moitié
en téléchargement chez Apple.

Pour compiler soi-même plutôt que télécharger, voir [`BUILD.fr.md`](BUILD.fr.md).

## Prérequis

| | Quoi | Où |
|---|---|---|
| PC | Windows 10 ou 11, **x64** | |
| PC | App **Appareils Apple** — elle apporte le multiplexeur USB et l'appairage, et c'est le **seul** composant Apple installé sur le PC | [Microsoft Store](https://apps.microsoft.com/detail/9np83lwlpz9k) |
| iPhone | **iOS 27 ou plus récent** (sur iOS 26, seuls les boutons du châssis passent) | [Mettre à jour l'iPhone](https://support.apple.com/fr-fr/HT204204) |
| iPhone | **Mode développeur** activé, dans Réglages > Confidentialité et sécurité | [Activer le mode développeur](https://developer.apple.com/documentation/xcode/enabling-developer-mode-on-a-device) |
| Fichier | une archive **Xcode 27** (`Xcode_27*.xip`, ~2 Go) **ou** un composant « Device Support » (`.dmg`, ~100 Mo) | [developer.apple.com/download/all](https://developer.apple.com/download/all/) — compte Apple gratuit |

Aucun runtime .NET à installer : l'exécutable téléchargé contient le sien.

### Pourquoi une archive Apple

L'**image développeur** est un binaire signé par Apple ; c'est elle qui apporte
au téléphone les services de pilotage (HID, affichage). Elle n'est **pas** dans
ce dépôt et n'y sera jamais : chacun l'extrait de son propre téléchargement,
avec l'outil intégré à l'app. Le fichier reste chez toi.

Le `.dmg` « Device Support » est le chemin court quand il est proposé pour ta
version d'iOS : 100 Mo au lieu de 2 Go, et quelques secondes d'extraction au
lieu d'une minute.

## 1. Télécharger la version

Sur la page **Releases** du dépôt, prends
`LuminaMonitor-<version>-win-x64.zip`, décompresse-le où tu veux — un dossier
dans `Documents` fait l'affaire. Deux exécutables dedans :

- `LuminaMonitor.App.exe` — la fenêtre ;
- `LuminaMonitor.UsbProbe.exe` — la sonde de diagnostic, à garder pour les
  mauvais jours.

Rien ne s'installe, rien ne s'écrit dans la base de registre. Pour désinstaller,
supprime le dossier et `%APPDATA%\LuminaMonitor`.

> Au premier lancement, Windows SmartScreen peut afficher « Windows a protégé
> votre ordinateur » : l'exécutable n'est pas signé par un certificat commercial.
> **Informations complémentaires** > **Exécuter quand même**.

## 2. Premier lancement : l'image développeur

La fenêtre s'ouvre et demande l'archive Apple. Choisis ton `.xip` Xcode, ton
`.dmg` Device Support ou le `XcodeSystemResources.pkg` que tu en aurais déjà
tiré. L'app fait le reste toute seule — xar → pbzx/xz → cpio → UDIF → HFS+/APFS
— et pose une copie fidèle de l'arbre `Restore/` dans :

```
%APPDATA%\LuminaMonitor\ddi\<build>
```

Compte **une minute environ** pour un `.xip` (58 s mesurées sur Xcode 27 bêta 6,
4 Go parcourus, progression dans la barre d'état), quelques secondes pour un
`.dmg`. **C'est une fois par version d'iOS** : le chemin est retenu dans les
réglages, les lancements suivants vont droit au miroir.

## 3. Brancher, déverrouiller

1. Branche l'iPhone en USB-C. Au tout premier branchement, le téléphone demande
   « Se fier à cet ordinateur » : réponds **oui**, et tape ton code
   ([aide Apple](https://support.apple.com/fr-fr/102518)).
2. **Déverrouille l'écran** et laisse-le déverrouillé le temps que la session
   s'ouvre : iOS refuse de monter l'image développeur sur un téléphone
   verrouillé.
3. Lance `LuminaMonitor.App.exe`. La barre d'état raconte la montée : image
   montée, tunnel ouvert, flux vidéo. Au bout de quelques secondes, l'écran du
   téléphone apparaît dans un châssis dessiné à ses cotes.

Si l'app dit que le multiplexeur Apple n'est pas lancé, le bouton **Ouvrir
Appareils Apple** du bandeau s'en charge.

## 4. Usage

| Geste | Effet sur le téléphone |
|---|---|
| **Clic dans l'image** | prend la main : la souris appartient au téléphone |
| **Ctrl + Alt gauche** | rend la souris au PC (l'Alt *gauche* : Alt droit + Ctrl fabrique AltGr, dont un clavier français a besoin) |
| **Clic gauche** | tap à la coordonnée exacte — pointeur **absolu**, là où tu cliques le doigt se pose |
| **Maintien** | appui long (le tap est différé de 180 ms, le temps de voir ce que fait la souris) |
| **Cliquer-glisser** | glissement du doigt sur le verre |
| **Clic droit** | bouton principal, retour à l'accueil |
| **Molette** | défilement, un cran = un glissement de doigt ; réglage `invertWheel` pour l'autre sens |
| **Clavier** | tout part sur le clavier virtuel du téléphone, accents et touches mortes compris |
| **Boutons dessinés sur le châssis** | volume haut/bas, muet, bouton latéral — ce sont les vrais boutons du téléphone qui sont pressés ; le bouton cliqué s'illumine un quart de seconde |
| **Bouton à l'emplacement du bouton Action** | coupe et rétablit le **son** (touche Muet). Le bouton Action lui-même — la bascule sonnerie/silencieux — n'est pas atteignable par ce protocole, et l'app ne fait pas semblant |
| **Bouton latéral** | éteint l'écran ; quand l'écran est éteint, le même bouton le rallume (voir « Verrouiller, déverrouiller » plus bas) |
| **F2** | envoie le presse-papiers Windows **dans le presse-papiers de l'iPhone** (⌘V ou appui long pour coller sur le téléphone). F2 et pas Ctrl+V : pendant le pilotage, Ctrl+V partirait au téléphone, qui attend Cmd+V |
| **F4** | récupère le presse-papiers de l'iPhone dans celui de Windows |
| **F3** | affiche les compteurs : images/s, latence, rapports envoyés, erreurs |

Les deux boutons **Vers l'iPhone** et **Depuis l'iPhone** de la barre du bas font
la même chose que F2 et F4.

L'interface suit la langue d'affichage de Windows (anglais ou français) et
peut être forcée dans les réglages.

### Presse-papiers

Le téléphone a un presse-papiers et il s'écrit par le câble : le texte arrive
**entier et instantané**, accents et emoji compris, et se colle ensuite sur le
téléphone comme n'importe quel copier-coller entre appareils Apple. Si le service
refuse (iOS plus ancien, service absent de l'annuaire), l'app retombe sur
l'ancienne méthode — taper le texte au clavier virtuel, caractère par caractère —
et **le dit dans la barre d'état**, pour qu'un collage lent ne passe pas pour le
rapide.

Dans l'autre sens, seul le **texte** revient. Un presse-papiers de téléphone tient
très souvent une photo : l'app l'annonce alors (« une image, public.png, 1,2 Mo —
non transférée ») plutôt que de rendre du vide qui se lirait « il n'y avait rien ».

Rien ne part tout seul : **aucune synchronisation automatique**, dans aucun sens.
Le contenu du presse-papiers n'est jamais écrit dans le journal, seulement le
nombre de caractères.

### Verrouiller, déverrouiller

Un clic sur le bouton latéral dessiné **éteint l'écran** du téléphone. L'app
affiche alors un bandeau « iPhone verrouillé — l'écran est éteint, le flux tourne
au ralenti, rien n'est cassé » et **arrête de traiter ça comme une panne**. Un
second clic (ou le bouton « Réveiller l'écran » du bandeau) rallume l'écran et
**l'image revient d'elle-même en moins d'une seconde**.

Le bandeau apparaît aussi quand c'est le **téléphone** qui se verrouille tout
seul, ou ta main sur le vrai bouton : l'app le reconnaît au débit du flux, pas à
ce qu'elle a commandé.

Mesuré le 9 septembre 2026 : le flux ne meurt pas pendant le verrouillage, il
tombe à deux paquets et une image entièrement noire par seconde. Rien n'est à
remonter, aucun reset d'image n'est déclenché, et la session reste ouverte.

**Ce que l'app ne peut pas faire :** déverrouiller. Réveiller l'écran est un
appui de bouton et marche toujours ; ce qui est derrière est l'écran de
verrouillage, et le franchir demande **Face ID** — donc ton visage devant le
téléphone — ou **le code**. Il n'existe aucun moyen de contourner ça, et l'app
n'essaie pas de faire croire le contraire.

Si tu veux quand même déverrouiller depuis le PC, tu peux écrire ton code dans les
réglages :

```json
"unlockCode": "123456"
```

L'app balaie alors l'écran de verrouillage vers le haut et tape le code au clavier
virtuel. **Le compromis est écrit noir sur blanc :** ce fichier n'est pas chiffré,
donc quiconque peut lire `%APPDATA%\LuminaMonitor\settings.json` peut lire le code
de ton téléphone. Vide par défaut, et vide est le bon choix pour presque tout le
monde. Le code n'apparaît jamais dans le journal — les lignes d'état ne comptent
que le nombre de caractères.

Les réglages (sens de la molette, couleur du châssis, position de la fenêtre,
`unlockCode`) vivent dans `%APPDATA%\LuminaMonitor\settings.json`. À part
`unlockCode` si tu le remplis, rien de secret n'y est écrit : l'appairage
appartient à Apple, l'app se contente de le lire.

### Audio (par Bluetooth)

Par le câble, le son de l'iPhone ne passe pas (AAC-ELD, que Windows ne sait pas
décoder). Il passe par **Bluetooth**, et le bouton **Audio** de la barre du bas
ouvre le panneau qui s'en occupe. Prérequis : l'iPhone est **appairé en
Bluetooth** avec ce PC (Paramètres Windows › Bluetooth et appareils), et le
Bluetooth est activé **dans les Réglages** de l'iPhone — pas seulement dans le
Centre de contrôle.

**Le son de l'iPhone sur ce PC.** Active l'interrupteur « Son de l'iPhone sur
ce PC ». L'app cherche l'iPhone parmi les appareils Bluetooth (celui qui porte
le nom de l'iPhone branché, sinon le premier ; une liste apparaît s'il y en a
plusieurs) et ouvre la connexion. La ligne d'état dit où elle en est :
« Connexion… », « Son actif », « En attente », « Refusé : … ». Un refus n'est pas
définitif : la connexion **reste à l'écoute**, et il suffit de toucher ce PC dans
**Réglages › Bluetooth** de l'iPhone pour qu'elle s'ouvre ; **Réessayer** refait
la demande depuis le PC. Une pastille sur le bouton Audio le dit, panneau
fermé : verte quand le son passe, ambre quand il est attendu. Le choix est
retenu (`phoneAudio`) et rouvert au lancement suivant.

**Choisir haut-parleurs ou casque.** Windows joue ce son sur **sa sortie par
défaut**, celle qu'affiche la ligne « Sortie : … » ; l'interface Windows utilisée
n'a aucun réglage de sortie. **Choisir la sortie…** ouvre la page de Windows
(Mélangeur de volume) : choisis-y la sortie, ou change la sortie par défaut de
Windows. L'app ne change jamais la sortie par défaut elle-même.

**Le micro de ce PC pendant un appel.** Pendant un appel (téléphone, FaceTime,
application de VoIP), choisis **ce PC** comme sortie audio dans l'écran d'appel
de l'iPhone. La liaison mains-libres apparaît, l'interrupteur « Micro de ce PC
pour les appels » devient actif (il reste grisé sinon) : choisis le micro et la
sortie où entendre l'appel, puis active. Le micro de ce PC part vers l'iPhone,
la voix de l'autre bout sort de la sortie choisie. En fin d'appel, le pont
s'arrête seul et l'interrupteur revient à zéro, avec la raison. **Pendant
l'appel, l'iPhone utilise le micro de ce PC à la place du sien** : c'est pour
cela que cet interrupteur n'est jamais activé par défaut ni au lancement. Seuls
les choix de micro et de sortie sont retenus (`callMicrophone`, `callOutput`).

**Limites, dites telles quelles.** Le son par Bluetooth est prouvé côté
logiciel jusqu'à la radio, mais pas encore sur un iPhone : le jour de l'essai,
le téléphone n'a pas répondu à l'appel Bluetooth du PC (`0x8007001F`). Le pont
d'appel est vérifié entre des périphériques du PC, pas encore sur les points de
terminaison du téléphone. Qualité : celle de l'A2DP pour la musique, celle d'un
kit mains-libres (8 ou 16 kHz) pour les appels. Le détail et le protocole de
test sont dans [`BLUETOOTH_AUDIO.fr.md`](BLUETOOTH_AUDIO.fr.md).

## Dépannage

**Le journal d'abord** : `%APPDATA%\LuminaMonitor\lumina.log`. Il est tenu à
chaque exécution, pas seulement en diagnostic — transitions d'état, lignes du
protocole, décisions de reconnexion, et toute exception non gérée avec sa pile.
Rotation à 5 Mo vers `lumina.1.log`. C'est ce fichier qu'il faut joindre à une
issue (extrait uniquement, et **sans l'identifiant de l'appareil**).

| Symptôme | Quoi faire |
|---|---|
| **Image figée** | Le service d'affichage du téléphone s'est tu. L'app démonte et remonte l'image toute seule — c'est le seul remède constaté ; débrancher le câble ne suffit pas. Laisse-la faire, elle dit « Relance du miroir… ». |
| **« Redémarre l'iPhone »** | Trois remontées d'image sans flux rétabli. Là, il faut vraiment redémarrer le téléphone. |
| **« Le multiplexeur Apple ne répond plus »** | Il écoute mais reste muet cinq secondes. **Relance l'app Appareils Apple**, ou rebranche le câble. |
| **« Le multiplexeur Apple n'est pas lancé »** | Rien n'écoute sur `127.0.0.1:27015` : ouvre **Appareils Apple** (bouton du bandeau) ou branche l'iPhone, le multiplexeur démarre à la demande. |
| **« Appareil non appairé »** | Ouvre Appareils Apple et réponds « Se fier à cet ordinateur » sur le téléphone. |
| **« Mode développeur inactif »** | Réglages > Confidentialité et sécurité > Mode développeur. Le téléphone redémarre. |
| **« Déverrouille l'iPhone »** | Le montage attend un écran déverrouillé ; l'app réessaie toute seule. |
| **Après un redémarrage du téléphone** | L'image développeur est à remonter — iOS l'oublie à chaque démarrage. L'app le fait seule, ça coûte quelques secondes. |
| **Rien ne démarre, ou tout se fige** | **Une seule session à la fois** : le service d'affichage du téléphone n'en sert qu'une. Deux fenêtres, ou la sonde pendant que l'app tourne, et c'est le gel. Ferme l'autre. |
| **Le miroir refuse de repartir tout de suite** | Entre deux sessions, le téléphone refuse un nouveau flux **pendant une à deux minutes**, et chaque tentative refusée renouvelle le refus. L'app espace ses essais (5 s, doublés, plafond 30 s). Attends, n'insiste pas. |
| **L'extraction refuse l'archive** | Le message dit lequel des trois cas : fichier inattendu, archive incomplète (retélécharger), ou paquet des ressources absent (ce n'est pas une archive Xcode 27). |
| **Le toucher ne passe pas, les boutons oui** | C'est iOS 26 : `CoreDeviceError 9021`, « Remote control requires iOS 27.0 or later ». Il faut iOS 27. |
| **« Service presse-papiers indisponible »** | Le service `pasteboardservice` n'a pas répondu. L'app est retombée sur la frappe caractère par caractère : le texte arrive quand même, plus lentement, et sans ce que le clavier US ne sait pas épeler. |
| **Audio : « Refusé : l'iPhone ne répond pas en Bluetooth »** | Bluetooth coupé sur l'iPhone, ou seulement « déconnecté » depuis le Centre de contrôle. Active-le dans Réglages › Bluetooth, puis touche ce PC dans la liste : la connexion, restée à l'écoute, s'ouvre. |
| **Audio : « Aucun iPhone appairé en Bluetooth »** | Appaire l'iPhone dans Paramètres Windows › Bluetooth et appareils (bouton **Réglages Bluetooth…** du panneau), puis **Réessayer**. |
| **Audio : son actif mais rien ne s'entend** | Le son sort sur la sortie par défaut de Windows : vérifie-la avec **Choisir la sortie…**, et le volume de l'iPhone. |
| **Micro de l'iPhone muet dans ses autres apps** | Un pont d'appel tient encore la liaison mains-libres : coupe l'interrupteur « Micro de ce PC pour les appels », ou ferme l'app. |
| **Bandeau « iPhone verrouillé » qui reste** | L'écran est éteint — que ce soit l'app, ta main ou le verrouillage automatique du téléphone. Clique **Réveiller l'écran** ; s'il reste verrouillé après ça, c'est Face ID ou le code, sur le téléphone. |

Pour un rapport de dix secondes plutôt qu'une capture d'écran :

```
LuminaMonitor.App.exe --diagnostic 10
```

La fenêtre s'ouvre, tourne dix secondes, écrit une ligne de compteurs par
seconde dans le journal et se ferme seule.
