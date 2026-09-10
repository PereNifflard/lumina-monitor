[English](README.md) · **Français**

# Lumina Monitor Desktop

Piloter son iPhone depuis Windows, **par le câble USB-C**, avec la souris et le
clavier du PC — sans Raspberry Pi, sans machine virtuelle, et **sans une seule
bibliothèque tierce sur le PC**.

L'écran du téléphone s'affiche dans une fenêtre, dans un châssis dessiné à ses
cotes. Le pointeur est **absolu** : là où tu cliques dans l'image, le doigt se
pose sur le verre. Le clavier du PC tape sur le téléphone, les boutons du
châssis pressent les vrais boutons.

> **État : préversion.** Le pilotage complet demande **iOS 27**. Sur iOS 26, les
> boutons du châssis fonctionnent mais le toucher est refusé par le téléphone
> (`CoreDeviceError 9021`, « Remote control requires iOS 27.0 or later »).

## La règle du projet

Tout le code qui tourne sur le PC est le nôtre. La seule pièce Apple admise est
celle déjà installée avec l'app « Appareils Apple » : le multiplexeur USB
(`AppleMobileDeviceProcess.exe`), l'équivalent d'un pilote de périphérique.
Tout ce qui est au-dessus — appairage, image développeur, tunnel, RemoteXPC,
flux vidéo, injection d'entrée, et jusqu'aux lecteurs xar, pbzx, xz, cpio, UDIF,
HFS+ et APFS — est réimplémenté à partir du protocole, pas importé.

## Ce qu'il faut

| | Quoi | Où |
|---|---|---|
| PC | Windows 10 ou 11, x64 | |
| PC | App **Appareils Apple** (Microsoft Store) — elle fournit le multiplexeur USB et l'appairage | [apps.microsoft.com](https://apps.microsoft.com/detail/9np83lwlpz9k) |
| PC | **.NET 8 SDK**, seulement pour compiler soi-même | [dotnet.microsoft.com](https://dotnet.microsoft.com/download/dotnet/8.0) |
| iPhone | **iOS 27** (iOS 26 : boutons seulement), **Mode développeur** activé | [Réglages > Confidentialité et sécurité > Mode développeur](https://developer.apple.com/documentation/xcode/enabling-developer-mode-on-a-device) |
| iPhone | appairé au PC : brancher, puis « Se fier à cet ordinateur » | |
| Fichier | **une archive Xcode 27** (`Xcode_27_beta_N.xip`, ~2 Go) **ou** un composant « Device Support » (`.dmg`, ~100 Mo) | [developer.apple.com/download/all](https://developer.apple.com/download/all/) (compte Apple gratuit) |

Cette dernière ligne mérite une phrase. L'**image développeur** est un binaire
signé par Apple ; c'est elle qui apporte au téléphone les services de pilotage
(HID, affichage). Elle n'est **pas** dans ce dépôt et n'y sera jamais : chacun
l'extrait de son propre téléchargement, avec l'outil ci-dessous.

## Installation

```
git clone <ce dépôt>          # ou télécharge et décompresse l'archive du dépôt
cd "LuminaMonitor Desktop"
dotnet build -c Release
```

L'exécutable atterrit dans
`src\LuminaMonitor.App\bin\Release\net8.0-windows\LuminaMonitor.App.exe`.

## Première exécution

Au premier lancement, la fenêtre demande l'archive Apple : choisis ton
`Xcode_27_beta_N.xip`, ton `.dmg` Device Support ou ton
`XcodeSystemResources.pkg`. Elle en tire l'image développeur toute seule —
xar → pbzx/xz → cpio → UDIF → HFS+/APFS — et pose une copie fidèle de l'arbre
`Restore/` dans `%APPDATA%\LuminaMonitor\ddi\<build>`.

Compte **une à deux minutes** pour un `.xip` — 58 s mesurées sur Xcode 27
bêta 6, 4 Go parcourus, progression affichée dans la barre d'état — et quelques
secondes pour un `.dmg`. C'est une fois par version d'iOS ; le chemin est
retenu dans les réglages.

En ligne de commande, la même chose :

```
dotnet run --project src/LuminaMonitor.UsbProbe -- extract-devsupport "C:\...\Xcode_27_beta_6.xip" ddi27
```

## Usage

Branche l'iPhone, déverrouille-le, lance l'app. Elle monte l'image, ouvre le
tunnel et le miroir toute seule ; la barre d'état dit où elle en est.

- **Prendre la main** : un clic dans l'image. La souris appartient alors au
  téléphone.
- **Rendre la souris** : **Ctrl + Alt gauche**. (L'Alt *gauche* : Windows
  fabrique AltGr avec Alt droit + Ctrl, et un clavier français y perdrait sa
  touche la plus utile.)
- **Clic gauche** : tap à la coordonnée exacte. Le tap est **différé de 180 ms**
  — le temps de voir si le doigt glisse : un tap sec reste un tap, un mouvement
  devient un glissement, et un maintien devient un appui long.
- **Clic droit** : bouton principal (retour à l'accueil).
- **Molette** : défilement, un cran = un glissement de doigt. Réglage
  `invertWheel` pour l'autre sens.
- **Boutons dessinés sur le châssis** : volume haut/bas, verrouillage, bouton
  Action. Ils pressent les vrais.
- **Clavier** : tout ce que tu tapes part sur le clavier virtuel du téléphone,
  accents et touches mortes compris.
- **F2** : taper le presse-papiers Windows sur le téléphone. (F2 et pas Ctrl+V :
  pendant le pilotage, Ctrl+V partirait au téléphone, qui attend Cmd+V.)
- **F3** : compteurs (images/s, latence, rapports envoyés, erreurs).

L'interface suit la langue d'affichage de Windows (anglais ou français) et
peut être forcée dans les réglages.

## Limites connues

- **iOS 27 minimum** pour le toucher. Sur iOS 26, seuls les boutons passent.
- L'**image développeur est à remonter après chaque redémarrage** du téléphone.
  L'app le fait toute seule, ce qui coûte une poignée de secondes au démarrage.
- Le montage de l'image exige un **écran déverrouillé** : iOS répond
  `DeviceLocked` et coupe tant que le téléphone est verrouillé. L'app le dit et
  réessaie.
- Le **flux vidéo est obligatoire pendant le pilotage** : c'est lui qui ouvre la
  porte HID. Pas de miroir, pas de tap (les boutons, eux, passent sans flux).
- **Une seule session à la fois.** Le service d'affichage du téléphone n'en sert
  qu'une ; deux fenêtres, ou une sonde pendant que l'app tourne, le figent.
- Entre deux sessions, le téléphone **refuse un nouveau flux pendant une à deux
  minutes**, et chaque tentative refusée renouvelle le refus. L'app espace donc
  ses essais (5 s, doublés, plafond 30 s) au lieu d'insister.
- L'app **Appareils Apple** doit tourner, ou au moins avoir été lancée une fois
  depuis le branchement : c'est elle qui porte le multiplexeur.

## Dépannage

**Le journal d'abord** : `%APPDATA%\LuminaMonitor\lumina.log`. Il est tenu à
chaque exécution, pas seulement en diagnostic — toutes les transitions d'état,
les lignes du protocole, les décisions de reconnexion, et **toute exception non
gérée** avec sa pile. Rotation à 5 Mo vers `lumina.1.log`.

| Symptôme | Ce que ça veut dire |
|---|---|
| « Le multiplexeur Apple n'est pas lancé » | Rien n'écoute sur 127.0.0.1:27015 : ouvre l'app **Appareils Apple**, ou branche l'iPhone. Le bouton du bandeau le fait. |
| « Le multiplexeur Apple ne répond plus » | Il écoute mais ne répond plus (5 s sans un mot) : **relance** Appareils Apple, ou rebranche le câble. |
| « Appareil non appairé » | Ouvre Appareils Apple, réponds « Se fier à cet ordinateur » sur le téléphone. |
| « Mode développeur inactif » | Réglages > Confidentialité et sécurité > Mode développeur (le téléphone redémarre). |
| « Déverrouille l'iPhone » | Le montage de l'image attend l'écran déverrouillé ; il réessaie tout seul. |
| Image figée, puis « Relance du miroir… » | Le service d'affichage s'est tu. L'app démonte et remonte l'image seule — c'est le seul remède constaté ; débrancher le câble ne suffit pas. |
| « Redémarre l'iPhone » | Trois remontées d'image sans flux rétabli. Là, il faut redémarrer le téléphone. |
| L'extraction refuse l'archive | Le message dit lequel des trois cas : fichier inattendu, archive incomplète (retéléchargement), ou paquet des ressources absent (ce n'est pas une archive Xcode 27). |

Sondes hors ligne, sans iPhone :

```
dotnet run --project src/LuminaMonitor.UsbProbe -- offer-check         # l'offre média, octet pour octet
dotnet run --project src/LuminaMonitor.UsbProbe -- watchdog-selftest   # la veille du flux
dotnet run --project src/LuminaMonitor.UsbProbe -- tcp-selftest        # la pile TCP contre un téléphone en papier
```

Sondes avec l'iPhone branché : `list`, `info`, `session`, `devmode`, `mount`,
`catalogue`, `tap`, `keys`, `button`, `mirror-test`. **Une à la fois, et jamais
pendant que l'app tourne.** Celles qui ouvrent une session cherchent l'image
développeur dans le dossier nommé par la variable d'environnement `LUMINA_DDI`,
ou dans `ddi27` à défaut — contrairement à l'app, qui retient le sien.
Attention : `mirror-test`, `clock-test` et `motion-test` écrivent des captures
de l'écran du téléphone dans le répertoire courant.

## Sécurité

Le détail complet — ce qui part, ce qui est écrit, ce qui est exigé du
téléphone — est dans [`docs/SECURITY.fr.md`](docs/SECURITY.fr.md). L'essentiel :

- **Aucune télémétrie.** Le projet n'a qu'un seul appel réseau, décrit
  ci-dessous, et il ne part qu'au moment de monter l'image développeur.
- **Une requête, vers Apple.** Depuis iOS 17, l'image développeur n'est montable
  qu'avec un billet signé par Apple pour un appareil donné. Le projet poste donc
  vers `gs.apple.com` l'**ECID** de votre iPhone (son identifiant matériel
  permanent), son modèle de carte et de puce, un aléa tiré par le téléphone, et
  les empreintes des composants de l'image. Aucun nom, aucune adresse, aucun
  contenu du téléphone. C'est la requête que fait Xcode pour la même opération ;
  sans elle, pas de montage.
- **Racine Apple épinglée.** `gs.apple.com` chaîne vers une racine absente des
  magasins Windows. Plutôt que de désactiver la validation, la racine **publique**
  d'Apple est embarquée et vérifiée par empreinte ; le reste de la validation
  TLS reste entier.
- **Le journal contient l'UDID et le nom de l'appareil.**
  `%APPDATA%\LuminaMonitor\lumina.log` est tenu à chaque exécution ; il ne part
  nulle part tout seul, mais relisez-le avant de le joindre à un rapport de
  bogue. La sonde, elle, écrit des captures de votre écran dans le répertoire
  courant.
- **Aucun code tiers sur le PC.** Pas de NuGet, pas de binaire téléchargé, pas
  de pilote installé. Le seul composant Apple est celui que l'app « Appareils
  Apple » du Store a posé.
- **Aucune image Apple dans le dépôt.** L'image développeur est un binaire signé
  par Apple : chacun l'extrait de son propre téléchargement, elle ne circule
  jamais par ici. Le `.gitignore` bloque `/ddi*/`, `*.xip`, `*.dmg` et `*.pkg`.
- **L'appairage est celui d'Apple.** Le projet lit l'enregistrement que l'app
  Apple a négocié ; il n'en crée pas, n'en stocke pas, et ne demande aucun
  secret. Rien de sensible ne vit dans `settings.json`.
- **Le processus Apple n'est jamais arrêté** par cette application, quoi qu'il
  arrive : il détient l'interface USB et les enregistrements d'appairage.
- **Rien du contenu du téléphone n'est lu.** L'app reçoit une image de l'écran
  et envoie des rapports HID ; elle n'ouvre aucun fichier du téléphone.

## Comment c'est fait

Neuf couches, du câble au tap : multiplexeur usbmux → lockdown (TLS mutuel) →
Mode développeur → image développeur (signature TSS, montage) → tunnel
CoreDevice → pile TCP/IPv6 en espace utilisateur → RSD + RemoteXPC (HTTP/2
minimal) → flux vidéo DisplayService → HID absolu et boutons.

Le détail est dans [`docs/ARCHITECTURE.fr.md`](docs/ARCHITECTURE.fr.md) (la pile et
les murs rencontrés), [`docs/CORE_DESIGN.fr.md`](docs/CORE_DESIGN.fr.md) (la
bibliothèque), [`docs/VIDEO_DESIGN.fr.md`](docs/VIDEO_DESIGN.fr.md) (le flux),
[`docs/APP_DESIGN.fr.md`](docs/APP_DESIGN.fr.md) (la fenêtre) et
[`docs/SECURITY.fr.md`](docs/SECURITY.fr.md) (ce qui sort, ce qui est écrit).

### Pourquoi pas le Bluetooth ?

Windows 11 *peut* publier un clavier/souris Bluetooth LE en mode utilisateur,
mais deux murs ferment cette voie, vérifiés sur la machine du projet :
l'appairage dual-mode est instable (iOS fusionne les identités Classic et LE et
démolit le lien HID neuf), et **iOS n'accepte le pointeur absolu qu'en Bluetooth
Classic**, rôle que Windows n'offre pas sans pilote noyau. La sonde qui l'a
démontré est conservée dans
[`experiments/BleProbe`](experiments/BleProbe/README.fr.md), résultat négatif
compris : elle est hors de la solution et ne se compile pas avec l'app.

## Crédits et sources

**Ce projet ne découvre rien tout seul.** Le protocole d'Apple n'est pas
documenté publiquement : ce qu'on en sait a été arraché sur des années par des
gens qui ont publié leurs notes, et ce dépôt existe parce qu'eux l'ont fait.

- [**pymobiledevice3**](https://github.com/doronz88/pymobiledevice3) — la source
  la plus complète, et de loin, sur `lockdown`, le tunnel CoreDevice, RemoteXPC,
  RSD, le montage d'image et les services HID. Notes de rétro-ingénierie
  comprises.
- [**libimobiledevice**](https://libimobiledevice.org/) — la référence
  historique sur usbmux, l'appairage et `lockdownd`.
- [**go-ios**](https://github.com/danielpaulus/go-ios) — une seconde lecture des
  mêmes couches, précieuse pour lever les ambiguïtés.
- Les spécifications publiées des formats lus ici : la note technique
  [TN1150](https://developer.apple.com/library/archive/technotes/tn/tn1150.html)
  (HFS+), la [spécification du format xz](https://tukaani.org/xz/xz-file-format.txt),
  le [SDK LZMA](https://7-zip.org/sdk.html), les RFC 6184, 7798 et 9293.
- Les plans cotés publiés par Apple, pour la géométrie du châssis.

**Aucune ligne de leur code n'est reprise ici.** Tout est réécrit en C# à partir
du protocole et des spécifications — ce qui est une contrainte du projet, pas
une prétention à l'antériorité. Là où le comportement d'une implémentation de
référence a été délibérément reproduit (une bizarrerie des règles de
personnalisation TSS, par exemple), le commentaire le dit sur place.

## Licence

MIT — voir [`LICENSE`](LICENSE). La licence couvre le code de ce dépôt et rien
d'autre : ni l'image développeur d'Apple, ni le multiplexeur de l'app
« Appareils Apple », qui restent soumis aux conditions d'Apple.
