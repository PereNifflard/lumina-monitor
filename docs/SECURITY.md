# Sécurité et vie privée

Ce que Lumina Monitor envoie, ce qu'il écrit, ce qu'il exige — et ce qu'il ne
fait pas. Tout ce qui suit est vérifiable dans les sources : les points d'entrée
réseau et disque sont énumérés nommément plus bas.

## Ce qui sort de votre PC

**Une seule destination sur Internet**, et seulement au moment de monter
l'image développeur :

| Quoi | Où | Quand |
|---|---|---|
| Requête de personnalisation TSS | `https://gs.apple.com/TSS/controller?action=2` | à chaque montage de l'image développeur (donc après chaque redémarrage du téléphone) |

C'est le mécanisme d'Apple lui-même : depuis iOS 17, l'image développeur n'est
montable qu'avec un billet (`ApImg4Ticket`) qu'Apple signe **pour un appareil et
un aléa donnés**. Sans cet appel, pas de montage, donc pas de pilotage.

**Ce que la requête contient** (`src/LuminaMonitor.Core/Ddi/Tss.cs`,
`BuildRequest`) :

- `ApECID` — l'identifiant unique de la puce de votre iPhone. C'est **un
  identifiant matériel permanent** ; c'est aussi la seconde moitié de l'UDID.
- `ApBoardID`, `ApChipID`, et les clés `Ap,*` de personnalisation lues sur le
  téléphone — modèle de carte, génération de puce.
- `ApNonce` — un aléa tiré par le téléphone pour ce montage-là.
- Les empreintes (`Digest`) des composants marqués « Trusted » du
  `BuildManifest.plist` de l'image : cela dit à Apple **quelle version d'image
  développeur** vous montez.
- Un `@UUID` aléatoire tiré à chaque requête, `@HostPlatformInfo = "mac"` et une
  chaîne de version client (`libauthinstall-1104.0.9`).

**Ce que la requête ne contient pas** : aucun nom, aucune adresse e-mail, aucun
nom d'appareil, aucun nom de machine ou d'utilisateur Windows, aucun numéro de
série, rien du contenu du téléphone.

Cette requête est la même que celle qu'Xcode ou l'app « Appareils Apple »
émettent pour la même opération. Si vous ne voulez pas qu'elle parte, il n'y a
pas de contournement : le montage de l'image développeur en dépend.

**La connexion TLS est épinglée.** `gs.apple.com` chaîne vers « Apple Root CA »,
une racine que Windows ne fournit pas. Plutôt que de désactiver la validation
(ce que font des outils de référence), la racine publique d'Apple est intégrée
au code, vérifiée par son empreinte SHA-1, et la chaîne doit y aboutir ; le
reste de la validation TLS (nom d'hôte compris) est intact. La révocation n'est
pas contrôlée : les listes d'Apple sont publiées sur ses propres hôtes et un
échec de récupération bloquerait la signature.

**Le reste du trafic ne quitte pas la machine ni le câble :**

- `127.0.0.1:27015` — le multiplexeur USB d'Apple, en boucle locale.
- Une pile TCP/IPv6 en espace utilisateur qui parle au téléphone **dans le
  tunnel CoreDevice**, lui-même encapsulé dans la connexion USB. Les adresses
  `fdd0::1`/`fdd0::2` que vous verrez dans le journal sont internes à ce tunnel
  et n'existent sur aucun réseau réel.

Il n'y a **aucun autre client HTTP, aucun autre socket sortant, aucune
résolution DNS** dans le projet : `HttpClient` n'est instancié qu'une fois, dans
`Tss.cs`, et `TcpClient` qu'une fois, dans `UsbmuxClient.cs`.

## Ce qui est écrit sur votre disque

L'application n'écrit que sous `%APPDATA%\LuminaMonitor` :

| Fichier | Contenu |
|---|---|
| `settings.json` | dossier de l'image développeur, chemin de la dernière archive, position de la fenêtre, sens de la molette, couleur du châssis. **Aucun secret** : l'appairage appartient à Apple et n'est jamais copié ici. |
| `lumina.log`, `lumina.1.log` | le journal, tenu à chaque exécution, rotation à 5 Mo. |
| `ddi\<build>\` | votre copie de l'arbre `Restore/` extraite de **votre** téléchargement Apple. |
| `ddi\extraction\` | le chantier temporaire de l'extraction. |

### ⚠️ Le journal contient des identifiants d'appareil

`lumina.log` porte, en clair :

- l'**UDID** de l'iPhone (`Appareil usbmux #N, UDID …`) — dont la seconde moitié
  **est l'ECID**, l'identifiant matériel permanent de la puce ;
- le **nom de l'appareil** tel qu'il est réglé dans iOS (souvent un prénom) ;
- le modèle, la version d'iOS et le numéro de build ;
- la ligne de commande complète du processus, donc le chemin de l'exécutable,
  qui contient en général votre nom d'utilisateur Windows.

Le journal ne part nulle part tout seul — mais **relisez-le avant de le joindre
à un rapport de bogue ou de le publier**. Il n'y a pas de troncature
aujourd'hui : le compromis est assumé, un UDID entier étant ce qui permet de
rattacher un journal à un téléphone quand plusieurs sont branchés.

### La sonde, elle, écrit des images de votre écran

`LuminaMonitor.UsbProbe` (`mirror-test`, `clock-test`, `motion-test`,
`latency-test`) écrit des `.bmp`, des `.rtp` et des dossiers de vignettes **dans
le répertoire courant** : ce sont des captures de l'écran du téléphone. Le
`.gitignore` bloque `*.bmp` et `*.rtp`, mais un dossier de vignettes passé en
`--out=` n'est couvert par rien. Ne les commettez pas, ne les partagez pas sans
les regarder.

## Ce que l'application exige du téléphone

- **Le téléphone doit être appairé** au PC (« Se fier à cet ordinateur »).
  L'appairage est celui négocié par l'app « Appareils Apple » ; ce projet le
  **lit** pour ouvrir la session TLS, il n'en crée pas, n'en stocke pas et ne
  demande aucun code.
- **Le mode développeur doit être activé** (Réglages → Confidentialité et
  sécurité). C'est un réglage qui abaisse délibérément une protection d'iOS et
  qui exige un redémarrage : ne l'activez que sur un appareil dont vous êtes
  propriétaire et que vous acceptez d'ouvrir au débogage.
- **L'image développeur doit être montée**, et elle l'est à chaque redémarrage
  du téléphone. Elle apporte les services de pilotage (HID, affichage).
- **L'écran doit être déverrouillé** au moment du montage : iOS répond
  `DeviceLocked` sinon.
- **iOS 27** pour le toucher ; sur iOS 26 seuls les boutons passent.

Autrement dit : l'application ne contourne aucune protection d'iOS. Elle
emprunte des portes qu'Apple ouvre, à condition que le propriétaire de
l'appareil les ait ouvertes lui-même, physiquement, sur l'appareil.

## Ce que l'application ne fait pas

- **Aucune télémétrie**, aucune statistique d'usage, aucun rapport de plantage
  distant, aucune vérification de mise à jour. Le seul appel réseau du projet
  est la requête TSS décrite plus haut.
- **Aucun code tiers.** Zéro `PackageReference` dans les quatre `.csproj`, zéro
  binaire téléchargé, zéro pilote installé. Les lecteurs xar, pbzx, xz/LZMA2,
  cpio, UDIF, HFS+ et APFS sont écrits ici, d'après les spécifications
  publiées.
- **Aucune redistribution de binaire Apple.** Ni image développeur, ni firmware,
  ni composant d'Xcode ne se trouve dans ce dépôt, et le `.gitignore` bloque
  `/ddi*/`, `*.xip`, `*.dmg` et `*.pkg`. Chacun extrait l'image de son propre
  téléchargement, avec son propre compte Apple.
- **N'arrête jamais le processus Apple.** `AppleMobileDeviceProcess.exe` détient
  l'interface USB et les enregistrements d'appairage : le tuer casserait
  l'appairage de l'utilisateur.
- **Ne lit rien du contenu du téléphone.** L'application reçoit un flux vidéo de
  l'écran et envoie des rapports HID. Elle n'ouvre aucun système de fichiers du
  téléphone, ne liste aucune application, n'extrait aucune donnée.

### Deux exceptions à « rien d'Apple dans le dépôt », assumées

1. **Le certificat racine public d'Apple**, dans
   `src/LuminaMonitor.Core/Ddi/Tss.cs`. C'est le fichier
   `AppleIncRootCertificate.cer` publié par Apple sur
   <https://www.apple.com/certificateauthority/>, destiné par nature à être
   distribué et vérifié : un certificat racine n'a de sens que public. Il ne
   contient aucun secret — la clé privée reste chez Apple — et son empreinte est
   vérifiée au chargement.
2. **197 octets de gabarit de négociation média**, dans
   `src/LuminaMonitor.Core/Media/MediaOffer.cs` (`SelfCheck`). Ce sont les
   octets d'**un message de protocole** relevé sur le fil, conservés uniquement
   comme vecteur de test : le constructeur d'offre doit les reproduire à
   l'octet près. Ce n'est pas du code d'Apple, ni un extrait de binaire : c'est
   une donnée d'interopérabilité, du même ordre qu'un numéro de port ou qu'un
   en-tête de format.

## Surface d'attaque, en clair

- L'application ouvre une **pile TCP/IPv6 écrite à la main** qui parse ce que le
  téléphone envoie, ainsi que des lecteurs de formats (UDIF, HFS+, APFS, xar,
  xz) qui parsent des fichiers que l'utilisateur choisit. Ce sont du code C#
  géré, mais qui utilise `AllowUnsafeBlocks` pour la conversion d'image ; un
  fichier d'archive malveillant ou un pair hostile est un vecteur plausible.
  **N'ouvrez que des archives Apple téléchargées depuis votre propre compte
  développeur.**
- Le décodage vidéo passe par **Media Foundation**, le décodeur H.264 de
  Windows.
- L'application **n'a pas besoin de droits administrateur** et ne doit pas être
  lancée avec.

## Signaler une faille

Ouvrez une **issue GitHub** décrivant le problème, ou — s'il permet d'attaquer
un appareil ou un utilisateur — utilisez le **rapport de vulnérabilité privé**
de GitHub (onglet *Security* → *Report a vulnerability*) plutôt qu'une issue
publique, et laissez un délai raisonnable avant divulgation.

Merci de **ne pas joindre `lumina.log` tel quel** : relisez-le d'abord, ou
retirez-en la ligne `UDID …` et le nom de l'appareil.
