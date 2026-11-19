# KKAO — KMS Kaisse À Outils

**Toutes les applis Koesio, une seule icône.** KKAO vit près de l'horloge : clic gauche = sa fenêtre (applis,
réglages, missions), clic droit = vos actions rapides. Chaque appli tourne dans son propre processus et parle à KKAO
par un tube nommé (contrat : [PROTOCOLE.md](PROTOCOLE.md), v2).

## Installer KKAO

1. Téléchargez **[KKAO.exe](https://github.com/Koesio-Managed-Services/KKAO/releases/latest/download/KKAO.exe)**
   (dernière version). Si le navigateur indique que le fichier n'est pas couramment téléchargé : « … » puis **Conserver**.
2. Double-cliquez dessus. Si Windows affiche « Windows a protégé votre ordinateur » : **Informations complémentaires**,
   puis **Exécuter quand même** (KKAO n'est pas encore signé numériquement).
3. Laissez-vous guider : KKAO s'installe pour vous seul, sans droits administrateur, puis vous cochez vos applis.
   Elles se téléchargent et s'installent d'un clic.

Windows 10 ou 11 suffit (.NET Framework 4.8 est déjà présent). Le fichier téléchargé peut ensuite être supprimé :
KKAO vit dans `%LOCALAPPDATA%\Programs\KKAO` (menu Démarrer, Bureau, démarrage avec Windows au choix).

| Appli | En bref |
|---|---|
| **InstruKtion** | Procédures pas à pas : captures annotées, floutage, vidéo, export HTML, PDF, IT Glue |
| **Kalepin** | Notes rapides rangées par jour dans Paper Dropbox, post-it sur le bureau (un guide fait créer son application Dropbox au premier lancement) |

**Mises à jour** : KKAO consulte la dernière release au démarrage puis toutes les 6 heures. Depuis la 2.3, elles
s'installent **automatiquement** (réglage activé par défaut, *Réglages > Mises à jour*). Le téléchargement se fait en
arrière-plan et l'empreinte est vérifiée.

- **KKAO** est mis à jour en premier. Il redémarre sur sa nouvelle version pendant une pause de 30 min ou au démarrage
  suivant, puis relance les applis qui tournaient.
- **Une appli arrêtée** est mise à jour tout de suite.
- **Une appli ouverte** est mise à jour au démarrage de KKAO, quand elle se ferme, ou pendant une pause de 30 min si elle
  n'est pas occupée (`status.busy`, enregistrement).
- **Retour arrière** : si la nouvelle version ne démarre pas, l'ancienne revient et cette version n'est plus retentée.

Sans le réglage, les boutons *Mettre à jour* restent disponibles. Réglages et documents sont conservés.

**Désinstaller** : Paramètres Windows > Applications > KKAO, ou KKAO > Réglages > Désinstaller. Les données
(réglages `%APPDATA%\KKAO`, documents et réglages des applis) restent.

## Comment KKAO trouve les applis

Chaque [release](https://github.com/Koesio-Managed-Services/KKAO/releases) contient :

| Fichier | Rôle |
|---|---|
| `KKAO.exe` | Le lanceur : le seul fichier que l'utilisateur télécharge |
| `catalog.json` | Les applis (version, adresse, empreinte SHA-256, taille) et la version de KKAO |
| `<appli>-<version>.zip`, `<appli>.png` | Paquet de chaque appli (`kkao-app.json` à la racine) et son icône |

KKAO lit `releases/latest/download/catalog.json` et en garde une copie (`%LOCALAPPDATA%\Programs\KKAO\cache`) pour
s'afficher tout de suite, même hors ligne. Un paquet n'est téléchargé que sur *Installer* ou *Mettre à jour* ; sa taille
et son empreinte sont vérifiées avant l'installation. Le proxy de Windows est utilisé avec l'identité de l'utilisateur.
Variable d'environnement `KKAO_CATALOG_URL` : autre catalogue (test, miroir interne), ou `off` pour ne rien chercher en ligne.

**Sans Internet** : `build-bundle.cmd` produit `out\KKAO-<version>.zip` (≈ 140 Mo) avec les applis embarquées ;
on l'extrait, on lance `KKAO.exe`, même guide, installation sans téléchargement.

## Publier une version

Une fois : `gh auth login` (GitHub CLI) avec un compte qui peut écrire dans le dépôt.

1. Monter `<Version>` dans `KKAO.csproj` (la release `v<version>` porte le catalogue : même pour une seule appli
   mise à jour, on publie une nouvelle version de KKAO). Les applis montent leur propre version.
2. `publish-github.cmd` : compile, assemble les paquets (installation d'essai de chacun), prépare `out\release\`
   (`KKAO.exe --release`), crée la release en brouillon, envoie les fichiers, puis la publie.
3. Vérifier comme un collègue : `KKAO.exe --verify-online rapport.txt` télécharge le catalogue, chaque paquet et
   `KKAO.exe`, vérifie les empreintes et installe à blanc dans un dossier temporaire.

## La fenêtre

| Page | Contenu |
|---|---|
| Accueil | Bonjour <prénom>, état des applis, mission suivante, vos applis, vos actions rapides |
| Applications | Catalogue (Toutes / Installées / Disponibles), Installer, Tout installer, progression du téléchargement |
| Fiche d'une appli | Ouvrir (son interface principale), Démarrer / Arrêter / Redémarrer, Démarrer avec KKAO, icône près de l'horloge, ses actions, **ses réglages affichés dans KKAO**, Désinstaller |
| Actions rapides | Le menu du clic droit : renommer, réordonner, séparateurs, ajouter depuis le catalogue de chaque appli |
| Premiers pas | 6 missions, niveau (Apprenti → Maître de la Kaisse), succès débloqués |
| Réglages | Thème (sombre, clair, comme Windows), icônes, démarrage, installer / désinstaller KKAO, mises à jour, guide, journal |

**À l'ouverture de KKAO, seule sa fenêtre s'ouvre** (à l'ouverture de session : seulement l'icône) : une appli démarre
quand on clique *Démarrer*, une de ses actions rapides, ou si sa case *Démarrer avec KKAO* est cochée. Les raccourcis
clavier sont enregistrés **par chaque appli** ; KKAO les affiche et les fait régler.

## Développement

`tools.json` à côté de `KKAO.exe` ou dans le dossier parent = **mode développement** : les applis de tools.json
(dossiers voisins `..\Procedurier`, `..\Kalepin`) l'emportent sur les applis installées du même id ; pas
d'auto-installation ni de mise à jour de KKAO. Une appli renommée garde ses réglages grâce à `aliases`.

```bat
build.cmd                 :: dist\KKAO.exe
build-bundle.cmd          :: out\KKAO-<version>\ et .zip (applis embarquées)
publish-github.cmd        :: release GitHub
```

| Mode | Rôle |
|---|---|
| `KKAO.exe` | Lanceur (fenêtre ouverte, sauf `--autostart` à l'ouverture de session) |
| `KKAO.exe --bundle [<dossier>] [--recipes <dossier>]` | Paquet à partager (recettes `bundle\apps\*.json`) |
| `KKAO.exe --release [<paquet>]` | Fichiers de la release GitHub (`out\release\`, `out\release-notes.md`, `out\release-tag.txt`) |
| `KKAO.exe --verify-online <rapport.txt> [--catalog <url>]` | Ce que vivra un collègue : téléchargement et installation à blanc |
| `KKAO.exe --update` | Lancé par KKAO après le téléchargement de sa nouvelle version : se met en place sans question |
| `KKAO.exe --screenshots <dossier>` | 38 rendus PNG de l'interface (sombre / clair, guide, menu), données de démonstration |
| `KKAO.exe --selftest <rapport.json> [--tools <fichier>]` | Test automatique sans interface visible (54 vérifications) |
| `KKAO.exe --uninstall [--quiet]` | Désinstallation |
| `KKAO.exe --export-icon <fichier.ico>` | Icône de l'exe (`assets\kkao.ico`) |

### Recette d'un paquet (`bundle\apps\<id>.json`)

`id`, `name`, `summary`, `description`, `highlights`, `source` (dossier de l'appli, relatif à la recette),
`version` (`package.json`, `exe:<fichier>` ou un numéro), `include` (fichiers ou dossiers), `exclude` / `keep`
(motifs `*` `?` `**`), `iconStandIn` (dessin de remplacement), `aliases` (anciens id), `app` = le `kkao-app.json`
(exe, args, icon, protocol, autostart).

## Renommer

| Quoi | Où |
|---|---|
| Nom affiché de KKAO (menus, fenêtre, infobulle, bulles, `welcome.host`) | `Core/Branding.cs` (le XAML lit `ui:Brand`) |
| Nom de l'exe, propriétés du fichier | `AssemblyName`, `Product`, `Description` de `KKAO.csproj` |
| Lettre de l'icône | `Tray/IconFactory.cs` (le « K » est dessiné), puis `--export-icon assets\kkao.ico` |
| Nom affiché d'une appli | `name` de tools.json / de sa recette (l'emporte sur celui du `hello`) |
| Dépôt GitHub des releases | `Distribution` dans `Core/Apps/Online.cs` |

Les **identifiants techniques ne changent jamais** : `--kkao`, tubes `kkao-…`, `%APPDATA%\KKAO`,
`%LOCALAPPDATA%\Programs\KKAO`, valeur Run `KKAO`, ids des applis et de leurs actions.

## Fichiers

| Dossier | Contenu |
|---|---|
| `Core` | Protocole (`ToolHost`, `ToolManager`), catalogue d'actions et réglages exposés, menu rapide, missions, réglages |
| `Core\Apps` | Catalogues (embarqué, en ligne), téléchargements, manifeste `kkao-app.json`, installation des applis, paquets et release |
| `Core\Setup` | Installation / désinstallation de KKAO (raccourcis, registre HKCU) |
| `Tray` | Icône, menu (thème), icônes par appli, dessins |
| `Ui` | Fenêtre WPF : thèmes (`Ui\Theme`), pages (`Ui\Views`), modèles, rendus d'écran |
| `bundle` | Recettes des paquets d'applis |
| `tests` | Faux outil Node (`fake-tool.js`, v1 et `--v2`) et son tools.json |

`%APPDATA%\KKAO\settings.json` : démarrage des applis, thème, guide, missions, menu rapide, catalogues gardés,
icônes, fenêtre, versions déjà signalées. `kkao.log` : journal (~1 Mo, jamais le contenu des notes).

## Testé / pas encore testé

**Testé automatiquement** (`--selftest`) : protocole v1 et v2 contre de faux outils, menu rapide, paquets
(construction, installation, mise à jour, désinstallation, refus : chemin interdit, mauvais id, empreinte),
installation de KKAO dans des dossiers temporaires (registre en mémoire), y compris depuis un « KKAO (1).exe » seul ;
catalogue en ligne simulé : lecture, copie locale, téléchargement puis installation, mise à jour annoncée et installée,
empreinte fausse refusée, choix entre paquet embarqué et en ligne, préparation de la release ; mise à jour
automatique (appli occupée jamais fermée, PC inactif, retour arrière, mise à jour au démarrage) ; rendu de toutes les
pages et étapes du guide dans les deux thèmes. `--verify-online` après chaque publication.

**Pas encore testé sur un vrai poste** : la fenêtre et le menu à l'écran (DPI 125/150 %), l'auto-installation et la
relance, la mise à jour de KKAO par lui-même (à la main et automatique), la désinstallation par Windows, le démarrage avec Windows, les icônes par
appli, SmartScreen / antivirus sur l'exe non signé, un proxy d'entreprise.
