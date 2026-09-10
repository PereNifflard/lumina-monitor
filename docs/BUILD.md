# Compiler soi-même

Pour installer la version téléchargée plutôt que compiler, voir
[`INSTALLATION.md`](INSTALLATION.md).

## Ce qu'il faut

Le **SDK .NET 8** ([dotnet.microsoft.com](https://dotnet.microsoft.com/download/dotnet/8.0)),
et rien d'autre. Pas de NuGet, pas de restauration de paquets, pas d'outil
externe : le dépôt ne dépend d'aucune bibliothèque tierce, et
`dotnet restore` n'a littéralement rien à télécharger.

Visual Studio n'est pas nécessaire ; la solution s'ouvre dedans si tu en as un.

## Construire

```
dotnet build LuminaMonitor.sln -c Release
```

Le dépôt est tenu à **zéro avertissement** ; l'intégration continue construit
avec `-warnaserror` pour que ça le reste. Fais pareil avant de proposer un
changement :

```
dotnet build LuminaMonitor.sln -c Release -warnaserror
```

L'exécutable atterrit dans
`src\LuminaMonitor.App\bin\Release\net8.0-windows\LuminaMonitor.App.exe`, et la
sonde à côté, dans `src\LuminaMonitor.UsbProbe\bin\Release\net8.0-windows\`.

Pour fabriquer le même exécutable autonome que les Releases — un seul `.exe`,
aucun runtime à installer chez l'utilisateur :

```
dotnet publish src/LuminaMonitor.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

Il pèse une petite centaine de mégaoctets : WPF et le runtime .NET voyagent
dedans.

## Les quatre projets

| Projet | Cible | En une phrase |
|---|---|---|
| `src/LuminaMonitor.Formats` | `net8.0` | Les lecteurs des formats dans lesquels Apple emballe l'image développeur : xar (XIP), pbzx, xz/LZMA2, cpio, UDIF (DMG), HFS+ et APFS — tous écrits ici, à partir des spécifications publiées, parce que Windows n'en lit aucun. |
| `src/LuminaMonitor.Core` | `net8.0`, Windows uniquement | Le protocole Apple lui-même : usbmux, lockdown en TLS mutuel, mode développeur, montage de l'image, tunnel CoreDevice, pile TCP/IPv6 en espace utilisateur, RemoteXPC, flux vidéo et surfaces HID — tout ce que la fenêtre voit se résume à `DeviceSession`. |
| `src/LuminaMonitor.App` | `net8.0-windows`, WPF | La fenêtre : le châssis dessiné, le miroir, la traduction des gestes de la souris en doigt sur le verre, le clavier, les compteurs et le journal. |
| `src/LuminaMonitor.UsbProbe` | `net8.0-windows`, console | La sonde de diagnostic : chaque barreau de l'échelle USB isolé dans une commande, plus les auto-tests hors ligne. |

`Core` et `Formats` exposent leurs briques internes à la sonde par
`InternalsVisibleTo` : c'est elle qui les attaque directement, l'app non.

## Les auto-tests hors ligne

Ils ne demandent **ni iPhone ni réseau**, et rendent un code de sortie non nul
quand un scénario échoue. Ce sont exactement ceux que fait tourner
l'intégration continue.

```
dotnet run --project src/LuminaMonitor.UsbProbe -- formats-check
dotnet run --project src/LuminaMonitor.UsbProbe -- offer-check
dotnet run --project src/LuminaMonitor.UsbProbe -- sps-selftest
dotnet run --project src/LuminaMonitor.UsbProbe -- watchdog-selftest
dotnet run --project src/LuminaMonitor.UsbProbe -- tcp-selftest
dotnet run --project src/LuminaMonitor.UsbProbe -- mf-selftest
```

| Commande | Ce qu'elle prouve |
|---|---|
| `formats-check` | La chaîne xar → pbzx → cpio relit une archive fabriquée pour l'occasion, octet pour octet. |
| `offer-check` | L'offre média construite est identique au gabarit Xcode, et les leviers de réglage partent bien sur le fil. |
| `sps-selftest` | La réécriture du SPS H.264 est relue puis repassée : un bit faux ne donne pas une image fausse, il donne zéro image. |
| `watchdog-selftest` | L'échelle de la veille du flux — image clé, relance, reset doux — jouée sur des instants inventés, puisqu'elle ne tourne en vrai que quand tout est déjà cassé. |
| `tcp-selftest` | La pile TCP du tunnel contre un téléphone de papier : c'est le seul endroit où la fenêtre de réception se referme à volonté. |
| `mf-selftest` | Le décodeur H.264 de Media Foundation s'instancie et accepte les types d'entrée et de sortie : l'interop COM tient. |

Deux autres tournent aussi sans téléphone : `decode-capture <fichier.rtp>`
rejoue une capture et en écrit une image en BMP, `mouse-flood <secondes>`
bombarde la fenêtre de l'app de mouvements de souris de synthèse.

## Les commandes de la sonde, avec l'iPhone branché

**Une seule à la fois, et jamais pendant que l'app tourne** : le service
d'affichage du téléphone ne sert qu'une session.

| Commande | À quoi elle sert |
|---|---|
| `list` | Les appareils vus par le multiplexeur Apple. Le premier réflexe : si rien ici, rien ne marchera plus haut. |
| `info` | Les clés d'identité lues par lockdown, sans appairage. Dit la version d'iOS. |
| `session` | Ouvre la session TLS avec l'enregistrement d'appairage stocké par Apple. Le barreau de l'authentification. |
| `pair <udid>` | Lit cet enregistrement d'appairage, sans rien y écrire. |
| `buid` | L'identifiant d'hôte du multiplexeur. |
| `devmode reveal` / `enable` | Fait apparaître, puis active, le Mode développeur (le téléphone redémarre). |
| `ddi` | Les identifiants de personnalisation et le nonce que réclame le serveur de signature d'Apple. |
| `mount <dossier>` / `unmount` | Monte, ou démonte, l'image développeur. Le remède au miroir figé. |
| `tunnel` | Ouvre le tunnel CoreDevice et attend un premier paquet IPv6. |
| `rsd` | Le service RSD et son catalogue brut. |
| `catalogue` | La liste des services RemoteXPC offerts par le téléphone — utile quand un service change de nom d'une version d'iOS à l'autre. |
| `ping [n]` | ICMPv6 à travers le tunnel : ce qu'il mesure, c'est le câble et le démon, rien au-dessus. |
| `stream-info [codec] [sec]` | Avec quelle banque de codecs le téléphone répond, et ce que le RTP transporte vraiment. |
| `mirror-test [sec] [sortie.bmp]` | Le miroir en direct, écrit en images fixes. |
| `clock-test [sec]` | Le délai absolu, mesuré sur une seconde qui tourne à l'écran du téléphone. |
| `motion-test [sec]` | Est-ce que le miroir décroche quand l'écran bouge ? |
| `latency-test [n]` | La latence de bout en bout, mesurée sur les pixels. |
| `tap <x%> <y%>` | Un vrai toucher, en montant toute l'échelle. |
| `keys <texte>` | Tape une ligne sur le clavier virtuel. |
| `button <home\|lock\|volume-up\|volume-down\|mute\|siri>` | Presse les vrais boutons du châssis. |
| `flood [sec]` | Charge le chemin d'entrée et mesure ce qui passe. |
| `listen [sec]` | Regarde les branchements et débranchements arriver. |

## Extraire l'image développeur en ligne de commande

L'app le fait au premier lancement ; la sonde fait la même chose sans fenêtre :

```
dotnet run --project src/LuminaMonitor.UsbProbe -- extract-devsupport "C:\chemin\vers\Xcode_27_beta_6.xip" ddi27
```

Autour, de quoi regarder dans les archives sans rien extraire :
`list-xip`, `grep-xip <motif>`, `extract-xip <motif>` et `inspect-dmg
<image.dmg> [motif]`.

## L'intégration continue

- `.github/workflows/build.yml` — à chaque poussée et à chaque pull request sur
  `main` : construction en Release avec `-warnaserror`, puis les six auto-tests
  hors ligne, chacun devant rendre 0. L'app compilée part en artefact.
- `.github/workflows/release.yml` — sur un tag `v*` : publication des deux
  exécutables autonomes, auto-tests relancés **depuis le binaire empaqueté**,
  puis `LuminaMonitor-<version>-win-x64.zip` attaché à une Release GitHub créée
  par `gh`. Aucune action tierce n'y entre.
