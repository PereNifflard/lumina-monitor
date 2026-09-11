[English](NOTICE.md) · **Français**

# NOTICE — les données de ce dossier ne sont pas sous la licence MIT du dépôt

Ce dossier, `src/LuminaMonitor.Core/Media/Aac/Tables/`, porte les tables numériques dont un
décodeur AAC-ELD a besoin et qu'il ne peut pas inventer : les douze livres de Huffman, les arêtes
des bandes de facteurs d'échelle, les plafonds TNS, et la fenêtre du banc de filtres basse latence.
**Ces tables ne sont pas couvertes par la licence MIT** placée à la racine du dépôt
([`LICENSE`](../../../../../LICENSE)). Elles sont régies par la notice de module logiciel MPEG
reproduite intégralement ci-dessous, et voyagent avec elle sous les termes de cette même notice.

## D'où viennent les nombres

| | |
|---|---|
| Document | ISO/IEC 14496-5:2001/Amd 43:2018, *Coding of audio-visual objects — Part 5: Reference software* — amendement 43, insert électronique `14496-5_Amd43_inserts.zip` |
| Source | <https://standards.iso.org/iso-iec/14496/-5/ed-2/en/amd/43/> — le portail de maintenance des normes ISO, gratuitement, sans compte |
| Récupéré le | 10 septembre 2026 |
| Sous-arbre | `audio/natural/mp4AudVm_Rewrite/src_tf/` — le décodeur de référence MPEG-4 Audio, AAC-(E)LD compris |
| Provenance de l'ELD | le décodeur ELD est entré dans cet arbre avec l'ISO/IEC 14496-5:2001/Amd 24:2009, « Reference software for AAC-ELD » |

Chaque nombre de ce dossier a été transcrit, par script ou à la main, depuis les sources C de ce
logiciel de référence — `hufftables.c`, `decdata.c`, `win480LD.h`, `win512LD.h` — vers du C#. Rien
de FFmpeg, FDK-AAC ou faad2 n'a été consulté. La façon dont chaque table a été vérifiée contre sa
source est dans [`docs/AAC_ELD_TABLES.fr.md`](../../../../../docs/AAC_ELD_TABLES.fr.md) ; ce
fichier ne parle que de la licence que portent les nombres.

## La notice, mot pour mot

Chaque fichier du logiciel de référence dont ces tables sont tirées porte la notice ci-dessous —
`TablesProvenance.MpegSoftwareModuleNotice` dans le code de ce dépôt, caractère pour caractère.
Seuls les retours à la ligne et la liste des développeurs d'origine par fichier diffèrent entre les
fichiers source :

- `win480LD.h` et `win512LD.h` nomment **Fraunhofer IIS (2006)**.
- `hufftables.c` et `decdata.c` nomment **AT&T, Dolby Laboratories et Fraunhofer Gesellschaft IIS
  (1996)**, édités par **Ali Nowbakht-Irani** (Fraunhofer IIS) et par **Yoshiaki Oikawa** et
  **Mitsuyuki Hatanaka** (Sony Corporation) respectivement.

La notice est un texte juridique : elle est citée ci-dessous dans sa langue d'origine, l'anglais,
sans traduction, et paraphrasée en français juste après.

> "This software module was originally developed by AT&T, Dolby Laboratories, Fraunhofer
> Gesellschaft IIS and edited by [see the per-file credits] in the course of development of
> the MPEG-2 AAC/MPEG-4 Audio standard ISO/IEC 13818-7, 14496-1,2 and 3. This software module is an
> implementation of a part of one or more MPEG-2 AAC/MPEG-4 Audio tools as specified by the MPEG-2
> AAC/MPEG-4 Audio standard. ISO/IEC gives users of the MPEG-2 AAC/MPEG-4 Audio standards free
> license to this software module or modifications thereof for use in hardware or software
> products claiming conformance to the MPEG-2 AAC/MPEG-4 Audio standards. Those intending to use
> this software module in hardware or software products are advised that this use may infringe
> existing patents. The original developer of this software module and his/her company, the
> subsequent editors and their companies, and ISO/IEC have no liability for use of this software
> module or modifications thereof in an implementation. Copyright is not released for non MPEG-2
> AAC/MPEG-4 Audio conforming products. The original developer retains full right to use the code
> for his/her own purpose, assign or donate the code to a third party and to inhibit third party
> from using the code for non MPEG-2 AAC/MPEG-4 Audio conforming products. This copyright notice
> must be included in all copies or derivative works." Copyright(c)1996, Copyright(c)2006.

**Paraphrase française** (n'engage que la compréhension de ce dépôt, le texte anglais ci-dessus
fait foi) : ce module logiciel a été développé à l'origine par AT&T, Dolby Laboratories et
Fraunhofer Gesellschaft IIS, et édité par les personnes citées par fichier ci-dessus, dans le cadre
du développement de la norme MPEG-2 AAC / MPEG-4 Audio (ISO/IEC 13818-7, 14496-1, -2 et -3). Il met
en œuvre une partie d'un ou plusieurs outils de cette norme. L'ISO/IEC accorde aux utilisateurs des
normes MPEG-2 AAC / MPEG-4 Audio une licence gratuite sur ce module « ou ses modifications » pour un
usage dans des produits matériels ou logiciels se déclarant conformes à ces normes. Quiconque
compte utiliser ce module dans un produit matériel ou logiciel est averti que cet usage peut
enfreindre des brevets existants. Ni le développeur d'origine et son entreprise, ni les éditeurs
suivants et leurs entreprises, ni l'ISO/IEC n'engagent leur responsabilité pour l'usage de ce module
ou de ses modifications dans une mise en œuvre. Le droit d'auteur n'est pas libéré pour les produits
non conformes à MPEG-2 AAC / MPEG-4 Audio. Le développeur d'origine garde l'entier droit d'utiliser
le code pour son propre usage, de le céder ou d'en faire don à un tiers, et d'empêcher un tiers de
l'utiliser pour des produits non conformes. Cette notice de droit d'auteur doit accompagner toute
copie ou œuvre dérivée. Copyright(c)1996, Copyright(c)2006.

## Ce que dit la notice, en clair

- La licence est **gratuite**, mais conditionnelle : elle couvre ce module « ou ses modifications »
  pour des produits **se déclarant conformes aux normes MPEG-2 AAC / MPEG-4 Audio**.
- Le droit d'auteur n'est **pas libéré** pour les produits non conformes.
- **La notice doit accompagner toute copie ou œuvre dérivée** — c'est pourquoi elle est reproduite
  intégralement ci-dessus plutôt que résumée.
- L'utilisateur est **averti que l'usage peut enfreindre des brevets existants**. L'AAC-ELD,
  l'extension basse latence utilisée ici, est un travail Fraunhofer de 2008 ; les brevets de l'AAC
  de base sont largement expirés, ceux de l'AAC-ELD pas nécessairement.
- Une seconde condition, distincte, vient de la licence de l'ISO pour ses inserts électroniques :
  la page de téléchargement parle d'un usage « in their original format without any modifications »
  (dans leur format original, sans modification), alors que la notice de module autorise
  expressément les « modifications thereof ». Une transcription du C vers le C# se situe entre ces
  deux textes. C'est énoncé ici comme un fait, pas tranché comme une question juridique.

## Décision prise pour ce projet — 10 septembre 2026

Les tables restent, isolées dans ce seul dossier, sous la notice ci-dessus et non sous les termes
MIT de ce dépôt, et le dépôt le dit par écrit (ce fichier). Les faits derrière cette décision :

- Rien dans ces fichiers ne fait tourner quoi que ce soit d'étranger sur le PC : ce sont des
  constantes numériques — longueurs de mots de code, arêtes de bandes, coefficients de filtre — lues
  par index, exactement comme toute autre table de ce dépôt. La promesse de sécurité du projet par
  ailleurs (« aucun code tiers ne s'exécute ») n'est pas affectée ; ce qui change, c'est la pureté de
  la licence, pas ce qui s'exécute : le code propre à ce dépôt reste MIT, et ce seul dossier de
  données normatives porte la notice ISO/MPEG ci-dessus à la place.
- L'AAC-ELD reste couvert par des brevets actifs (Fraunhofer IIS, licenciés via le guichet Via
  Licensing), quelle que soit la provenance des nombres : les lire dans la norme achetée, ou dans
  FFmpeg, n'aurait rien changé à cela.
- Ce projet est gratuit et non commercial — la situation de tout décodeur AAC open source. Quiconque
  voudrait utiliser ce code ou ces tables dans un produit commercial a besoin de sa propre licence
  de brevets ; rien ici ne l'obtient à sa place.

Aucun avis juridique n'est donné ici, ni ailleurs dans ce dépôt — seulement les faits ci-dessus et
la source dont ils viennent.
