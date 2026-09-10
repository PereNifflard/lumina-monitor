# Installation

Ce guide part d'un PC nu et s'arrête quand l'écran de l'iPhone est dans une
fenêtre Windows et répond à la souris. Compte **dix minutes**, dont la moitié
en téléchargement chez Apple.

Pour compiler soi-même plutôt que télécharger, voir [`BUILD.md`](BUILD.md).

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
| **Boutons dessinés sur le châssis** | volume haut/bas, verrouillage, bouton Action — ce sont les vrais qui sont pressés |
| **F2** | tape le presse-papiers Windows sur le téléphone (F2 et pas Ctrl+V : pendant le pilotage, Ctrl+V partirait au téléphone, qui attend Cmd+V) |
| **F3** | affiche les compteurs : images/s, latence, rapports envoyés, erreurs |

Les réglages (sens de la molette, couleur du châssis, position de la fenêtre)
vivent dans `%APPDATA%\LuminaMonitor\settings.json`. Rien de secret n'y est
écrit : l'appairage appartient à Apple, l'app se contente de le lire.

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

Pour un rapport de dix secondes plutôt qu'une capture d'écran :

```
LuminaMonitor.App.exe --diagnostic 10
```

La fenêtre s'ouvre, tourne dix secondes, écrit une ligne de compteurs par
seconde dans le journal et se ferme seule.
