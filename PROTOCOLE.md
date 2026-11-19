# Protocole KKAO v2

KKAO (« KMS Kaisse À Outils ») est le lanceur des applis Koesio : **une icône** dans la zone de notification,
plus, si l'utilisateur le choisit, **une icône par appli dessinée par KKAO**.
Chaque appli tourne dans son propre processus, **n'affiche jamais d'icône à elle** quand KKAO l'héberge,
et publie à KKAO son menu, son état, ses actions et ses notifications.

> **État au 06/11/2026** : contrat v2 figé (ajouts du 06/11 : *Réglages exposés*, *Paquet d'appli*).
> KKAO 2.0 implémente la v2, comme InstruKtion 0.2.0 et Kalepin 2.1.0 ; une appli v2 marche aussi avec KKAO 1.0,
> qui ignore ce qu'il ne connaît pas (voir *Versions*). Les ajouts de la v2 sont marqués **(v2)**.

## Ce qui ne change jamais

Les noms **affichés** (KKAO, InstruKtion, Kalepin…) vont changer. Les **identifiants techniques** ci-dessous, jamais :

| Identifiant | Valeur |
|---|---|
| Argument de lancement | `--kkao <chemin du tube>` |
| Préfixe des tubes | `\\.\pipe\kkao-` |
| Id des applis (`tool` du `hello`, `id` de tools.json) | `instruktion` (InstruKtion), `kalepin` (Kalepin), … |
| Id des actions | ceux que chaque appli publie dans `actions` |

Ils sont enregistrés dans les réglages des utilisateurs et dans les réglages de déploiement : en changer un casse
le menu rapide de tout le monde. Seule exception, avec **`aliases`** (tools.json, `kkao-app.json`, catalogue) : l'ancien id
y est déclaré, KKAO reprend ses réglages, son catalogue gardé et ses entrées du menu rapide, et une appli installée sous
l'ancien id est proposée à la mise à jour puis remplacée. Fait avant tout déploiement : `papernotes` → `kalepin` (10/11/2026),
`procedurier` → `instruktion` (13/11/2026). Une appli ne teste **jamais** un nom affiché (`welcome.host`, son propre `name`).

## Versions

| | KKAO 1.0 (protocole 1) | KKAO 2.0 (protocole 2) |
|---|---|---|
| **Appli v1** | ✔ | ✔ — pas de catalogue : seuls ses éléments `pin` vont dans le menu rapide, et seulement quand elle tourne |
| **Appli v2** | ✔ — `actions` et les champs v2 sont ignorés ; `welcome` dit `protocol: 1`, sans `features` | ✔ |

- L'appli annonce sa version dans `hello.protocol`, KKAO la sienne dans `welcome.protocol` + la liste `features`.
- Chaque côté n'utilise que ce que l'autre a annoncé. Les champs et types inconnus sont ignorés des deux côtés.

## Transport

- KKAO crée **un tube nommé par appli** (serveur, `InOut`, une seule connexion) :
  `\\.\pipe\kkao-<idAppli>-<pidKKAO>`
- KKAO lance l'appli avec les arguments de `tools.json` **plus** `--kkao <chemin complet du tube>`
  (ex. `--kkao \\.\pipe\kkao-kalepin-1234` ; si ce nom est encore pris, KKAO ajoute `-<n>`). Le tube n'est accessible qu'à l'utilisateur courant.
- L'appli se connecte au tube en client (nouvel essai toutes les 200 ms pendant 10 s maximum ; échec : elle fonctionne seule, avec sa propre icône).
  Node/Electron : `net.connect(chemin)`. .NET : `NamedPipeClientStream(".", nom sans le préfixe \\.\pipe\, InOut)`,
  avec `Connect(0)` + pause de 200 ms en boucle (`Connect(délai)` tourne à vide sur .NET Framework tant que le tube n'existe pas).
- Messages : objets JSON en UTF-8 **sans BOM**, **un par ligne** (séparateur `\n`), 8 Mo au plus par ligne.
  Une ligne illisible est ignorée (journalisée).
- Tube fermé par KKAO (KKAO quitte ou plante) : l'appli **quitte proprement** (comme `quit`).

Pourquoi un tube et pas stdin/stdout : le processus principal d'Electron sous Windows voit stdin fermé
dès le démarrage (vérifié avec Electron 45). Le tube marche dans les deux sens pour Node/Electron et .NET.

## Appli → KKAO

| Message | Rôle |
|---|---|
| `{"type":"hello","protocol":2,"tool":"kalepin","name":"Kalepin","version":"2.1.0","icon":"iVBORw0KGgo…"}` | Premier message, obligatoire. |
| `{"type":"actions","items":[…]}` **(v2)** | Catalogue des actions (remplace le précédent). |
| `{"type":"settings","sections":[…]}` **(v2)** | Réglages de l'appli affichés et modifiables dans KKAO (remplace les précédents). |
| `{"type":"menu","items":[…]}` | Menu complet de l'appli (remplace le précédent). |
| `{"type":"status","state":"idle","tooltip":"Kalepin — 2 notes en attente"}` | État affiché sur l'icône. |
| `{"type":"notify","title":"…","text":"…","kind":"info"}` | Bulle de notification (`info`, `warn`, `error`). |
| `{"type":"retry","after":3000,"reason":"…"}` | L'appli ne peut pas démarrer maintenant et va quitter : KKAO la relance après `after` ms (3 fois de suite au maximum). |
| `{"type":"log","text":"…"}` | Ligne dans le journal de KKAO. |

Ordre conseillé après `hello` : `actions`, `settings`, `menu`, `status`. Puis chacun est renvoyé **à chaque changement**.

### `hello`

- `tool` : id technique de l'appli (le même que dans tools.json).
- `name` : nom affiché proposé par l'appli. Le `name` de tools.json l'emporte s'il existe (renommage sans toucher à l'appli).
- `version` : version de l'appli (journal, fenêtre Réglages).
- `icon` **(v2, facultatif)** : image PNG en base64 (sans préfixe `data:`), carrée, 64×64 conseillé (32×32 minimum),
  256 Ko de texte au plus. Sert à l'icône propre de l'appli et à la fenêtre Réglages. Le champ `icon` de tools.json
  l'emporte s'il existe. Sans image : KKAO dessine un carré avec l'initiale du nom.

### Catalogue d'actions `actions` (v2)

```json
{"type":"actions","items":[
  {"id":"open","label":"Ouvrir Kalepin"},
  {"id":"quickNote","label":"Nouvelle note…","shortcut":"Ctrl+Alt+F1","default":true,"pin":true},
  {"id":"newPostIt","label":"Nouveau post-it"},
  {"id":"showPostIts","label":"Afficher les post-it","check":true,"checked":false},
  {"id":"settings","label":"Réglages…","description":"Compte Dropbox, Paper cible, raccourcis"}
]}
```

Le **menu** est ce que l'utilisateur voit en ce moment (il peut changer : liste des procédures, notes en attente…).
Le **catalogue** est la liste durable de ce que l'appli sait faire sur commande : c'est là que l'utilisateur
(ou le déploiement) choisit les entrées du menu rapide de KKAO.

| Champ | Rôle |
|---|---|
| `id` | **Stable pour toujours** : jamais déduit du libellé, jamais traduit, inchangé si l'appli est renommée. `A-Z a-z 0-9 . _ -`, 1 à 64 caractères, unique dans le catalogue. |
| `label` | Libellé par défaut (obligatoire). L'utilisateur peut renommer l'entrée dans KKAO. |
| `shortcut` | Raccourci global **actuellement enregistré par l'appli** pour cette action, en texte affiché (`Ctrl+Alt+F1` ; modificateurs `Ctrl`, `Alt`, `Maj`, `Win`). Absent ou vide : pas de raccourci. |
| `shortcutError` | Pourquoi le raccourci choisi n'est pas actif (ex. « déjà utilisé par une autre application »). Affiché dans la fenêtre Réglages de KKAO. |
| `enabled` | Défaut `true`. `false` : impossible pour l'instant (ex. « Terminer » sans procédure en cours) → grisé dans le menu rapide. |
| `check` / `checked` | Action bascule (case cochée dans le menu rapide). |
| `default` | `true` sur **une** action au plus : un clic gauche sur l'icône propre de l'appli l'exécute. Sans `default`, le clic gauche ouvre le menu. |
| `pin` | Proposée par défaut dans le menu rapide, tant que l'utilisateur ne l'a pas personnalisé. |
| `description` | Aide affichée dans la fenêtre Réglages de KKAO. |

Règles :
- Catalogue **complet** à chaque envoi, 200 actions au plus.
- Id réservé `settings` : ouvre la fenêtre de réglages de l'appli. KKAO s'en sert pour « Réglages de *appli*… »
  (notamment pour changer un raccourci).
- Id réservé `open` **(KKAO 2.2)** : affiche l'**interface principale** de l'appli (la restaure si elle est réduite,
  la met au premier plan). Libellé conseillé « Ouvrir *appli* », sans raccourci imposé. KKAO s'en sert pour le bouton
  **Ouvrir** de l'accueil, de la page Applications et de la fiche de l'appli ; appli arrêtée : démarrage à la demande
  puis `open`. Sans `open` dans le catalogue, le bouton démarre simplement l'appli (et, si elle tourne déjà, ouvre sa
  fiche dans KKAO). `open` n'est jamais l'action `default` d'office : chaque appli garde la sienne pour son icône.
- Un même id peut figurer dans `actions` et dans `menu` : c'est la même `command`.
- **L'appli exécute tout id de son catalogue reçu dans `command`, même s'il n'est pas dans son menu du moment**,
  et même juste après `welcome` (voir *Démarrage à la demande*).
- KKAO garde le dernier catalogue reçu (réglages de l'utilisateur) : les actions du menu rapide restent visibles
  quand l'appli est arrêtée ; un clic la démarre.
- Une appli v2 qui publie un catalogue : KKAO prend les `pin` du **catalogue** pour le menu rapide par défaut
  et ignore ceux du menu (pas de doublon). Sans catalogue (appli v1) : les `pin` du menu, comme en v1.

### Réglages exposés `settings` (v2)

L'appli publie ses réglages **utiles à l'utilisateur** : KKAO les affiche dans sa fenêtre (page de l'appli, thème de KKAO)
et renvoie chaque modification. L'appli garde ses propres fenêtres de réglages ; ceci en est le reflet dans KKAO.

```json
{"type":"settings","sections":[
  {"title":"Raccourcis","items":[
    {"id":"hotkey.quickNote","label":"Nouvelle note","type":"hotkey","value":"Ctrl+Alt+F1"},
    {"id":"hotkey.newPostIt","label":"Nouveau post-it","type":"hotkey","value":"","description":"Vide : pas de raccourci"}
  ]},
  {"title":"Notes","items":[
    {"id":"order","label":"Nouvelles notes","type":"choice","value":"bottom","options":[{"value":"bottom","label":"En bas"},{"value":"top","label":"En haut"}]},
    {"id":"account","label":"Compte Dropbox","type":"info","value":"camille.martin@koesio.com"},
    {"id":"connect","label":"Changer de compte…","type":"button","action":"settings"}
  ]}
]}
```

| Champ | Rôle |
|---|---|
| `id` | **Stable** (`A-Z a-z 0-9 . _ -`, 1 à 64 caractères), unique dans l'appli. |
| `label` | Libellé (obligatoire). `description` : aide sous le libellé. |
| `type` | `toggle` (booléen), `text`, `number` (`min`, `max`, `step`), `choice` (`options` : `value` + `label`), `hotkey` (texte `Ctrl+Alt+F1`, vide = aucun), `folder` (chemin, KKAO ouvre un sélecteur de dossier), `info` (lecture seule), `button` (lance l'action `action` du catalogue). Type inconnu : affiché en lecture seule. |
| `value` | Valeur actuelle (booléen pour `toggle`, nombre pour `number`, texte sinon). |
| `enabled` | Défaut `true`. `false` : affiché grisé. |
| `error` | Message si la dernière valeur demandée a été refusée (ex. « Ctrl+Alt+N est déjà utilisé »). |
| `placeholder` | Texte grisé d'un champ `text` vide. |

Règles :
- Réglages **complets** à chaque envoi (300 au plus) ; renvoyés après chaque changement, qu'il vienne de KKAO ou de l'appli.
- **Jamais de secret** (mot de passe, jeton, clé) dans `settings` : KKAO l'afficherait et le journaliserait.
- Les réglages ne sont modifiables que pendant que l'appli tourne (KKAO propose de la démarrer).

KKAO → appli : `{"type":"setSetting","id":"hotkey.quickNote","value":"Ctrl+Alt+F1"}`.
L'appli applique, enregistre, puis **republie `settings`** : la nouvelle valeur, ou l'ancienne + `error`.
KKAO affiche « enregistrement… » jusqu'à ce `settings` (3 s au plus).

### Éléments de menu

```json
{"id":"newNote","label":"Nouvelle note…","shortcut":"Ctrl+Alt+N","enabled":true,"pin":true}
{"id":"showPostIts","label":"Afficher les post-it","check":true,"checked":false}
{"id":"zone-2","label":"Zone 1280×720","radio":true,"checked":true}
{"type":"separator"}
{"label":"Reprendre une procédure","items":[ … ]}
```

- `id` : chaîne opaque choisie par l'appli (4096 caractères max), renvoyée telle quelle dans `command`.
  L'appli peut republier son menu pendant que celui de KKAO est ouvert : elle résout l'id reçu dans son dernier menu.
  Pour une entrée qui figure aussi au catalogue : **le même id stable** que dans `actions`.
- `enabled` (défaut `true`), `checked` (défaut `false`), `check` / `radio` : case à cocher / bouton radio.
- `shortcut` : texte affiché à droite (le raccourci reste géré par l'appli elle-même).
- `pin: true` : l'élément est **aussi** affiché en haut du menu KKAO (actions rapides) — voir la règle des `pin` ci-dessus.
- `bold: true` : élément en gras (action principale).
- Sous-menu : `label` + `items`, sans `id`.
- Hébergée par KKAO, l'appli retire de son menu ce que KKAO fournit déjà : *Quitter*, *Lancer au démarrage de Windows*
  (KKAO affiche *Arrêter*, *Redémarrer*, *Démarrer avec KKAO*).

### États (`status.state`)

`idle` (au repos), `active` (travail en cours), `pause`, `rec` (enregistrement vidéo), `warn` (ex. notes en attente), `error`.
L'icône KKAO montre l'état le plus important de toutes les applis : `rec` > `error` > `pause` > `warn` > `active` > `idle`.
L'icône propre d'une appli **(v2)** montre son état à elle ; son infobulle est `status.tooltip` (63 caractères affichés au plus).

### Occupée : `status.busy` (KKAO 2.3)

`{"type":"status","state":"active","busy":true}` : l'appli **ne doit pas être fermée maintenant**. Par exemple, du travail
non enregistré, un enregistrement en cours ou un envoi en cours. KKAO ne la ferme alors jamais pour une mise à jour automatique.

- `busy` absent = `false`. L'état `rec` compte toujours comme occupé.
- L'appli renvoie `status` avec `busy:false` dès que le travail est enregistré.
- `busy` ne change rien à l'icône ni aux clics. *Arrêter* et *Quitter* restent possibles : c'est l'utilisateur qui décide.

## KKAO → appli

| Message | Rôle |
|---|---|
| `{"type":"welcome","host":"KKAO","version":"2.0.0","protocol":2,"features":["actions","settings","icons","startOnDemand"]}` | Réponse au `hello`. `host` = nom affiché de KKAO (il changera : ne jamais le tester). `features` **(v2)**. |
| `{"type":"command","id":"quickNote","source":"quick"}` | Clic sur un élément de menu ou une action. `source` **(v2)** : voir ci-dessous. |
| `{"type":"setSetting","id":"order","value":"top"}` **(v2)** | Modification d'un réglage exposé (voir *Réglages exposés*). |
| `{"type":"quit"}` | Demande d'arrêt propre (l'appli a 10 s). |

`source` **(v2)**, pour information seulement (l'appli traite toutes les commandes de la même façon, sauf bonne raison) :
`menu` (menu de l'appli, dans le menu KKAO ou sur son icône propre), `quick` (menu rapide), `icon` (clic gauche sur
son icône propre → action `default`), `settings` (fenêtre Réglages de KKAO). Valeur inconnue = `menu`.

Avant chaque `command`, KKAO autorise l'appli à passer au premier plan (`AllowSetForegroundWindow(pid)`) :
une fenêtre ouverte en réponse peut prendre le focus.

## Démarrage à la demande (v2)

Clic sur une action du menu rapide d'une appli **arrêtée** :

1. KKAO la démarre (comme *Démarrer*) et met la commande en attente (5 au plus, gardées 30 s, y compris à travers un `retry`).
2. Juste après `welcome`, KKAO envoie les commandes en attente, dans l'ordre.
3. L'appli **accepte une `command` reçue juste après `welcome`**, même si son interface n'est pas prête
   (elle la met en file de son côté) et l'exécute comme un clic normal.
   Après un démarrage, Windows peut refuser le premier plan malgré `AllowSetForegroundWindow` : si l'action ouvre une
   fenêtre, l'appli utilise au besoin ses moyens habituels pour la montrer.
4. Pas de `hello` dans les 30 s, ou l'appli quitte : commandes abandonnées, bulle KKAO « *appli* n'a pas démarré ».

## Icônes (v2)

- L'utilisateur (ou le déploiement) choisit : icône KKAO seule, ou KKAO **+ une icône par appli**, avec des exceptions par appli.
- **KKAO dessine les icônes des applis** : image de l'appli + pastille de son état, infobulle `status.tooltip`,
  clic droit = menu de l'appli + *Démarrer / Arrêter / Redémarrer*, clic gauche = action `default` (sinon le menu),
  bulles `notify` de l'appli sur son icône. Visible tant que l'appli tourne.
- Hébergée, l'appli **n'affiche jamais sa propre icône**, quel que soit ce choix. Elle n'a rien à coder pour les icônes,
  à part fournir une image (`hello.icon` ou `icon` de tools.json).

## Raccourcis globaux (v2)

- Chaque appli enregistre **elle-même** ses raccourcis globaux (`RegisterHotKey`, `globalShortcut`…), permet de les changer
  dans **ses** réglages (pour n'importe quelle action de son catalogue, raccourci vide permis), et publie le raccourci
  actif dans `actions[].shortcut` (et `menu[].shortcut`) ; elle republie `actions` après chaque changement.
- Raccourci impossible à enregistrer (déjà pris) : publié avec `shortcutError`, et une `notify` `warn` au moment du choix.
- KKAO n'enregistre **jamais** de raccourci pour une appli. Un raccourci ne marche donc que si son appli tourne :
  KKAO le signale et propose *Démarrer avec KKAO*.

## Paquet d'appli (v2)

KKAO se partage aux collègues avec les applis **embarquées mais pas installées** : l'utilisateur clique *Installer*
dans KKAO (plus tard : *Télécharger et installer*). Une appli livrée ainsi est un **zip** dont la racine contient
ses fichiers + `kkao-app.json` :

```json
{
  "format": 1,
  "id": "instruktion", "name": "InstruKtion", "version": "0.2.0",
  "exe": "node_modules\\electron\\dist\\electron.exe", "args": ["."],
  "icon": "assets\\icon.png", "protocol": true, "autostart": true
}
```

- Installée dans `%LOCALAPPDATA%\Programs\KKAO\apps\<id>\` (par utilisateur, sans droits admin). Chemins de `exe`,
  `cwd`, `icon` et arguments `.`/`.\…` relatifs à ce dossier. KKAO la lance comme une appli de tools.json (+ `--kkao`).
- L'appli doit marcher **depuis n'importe quel dossier** : aucun chemin de développement, rien d'écrit dans son
  propre dossier (remplacé à chaque mise à jour) ; données dans `%APPDATA%`, `%LOCALAPPDATA%` ou `Documents`.
- Mise à jour : KKAO arrête l'appli (`quit`), remplace son dossier, la relance. Désinstallation : son dossier est
  supprimé, **ses données utilisateur sont gardées**.
- Le zip est construit par KKAO (`KKAO.exe --bundle`, recettes dans `KKAO\bundle\apps\`) à partir du dossier de l'appli.
  Une appli qui a besoin d'autres fichiers à l'exécution le signale à la session KKAO.

## Réservé (non implémenté)

Appels d'une appli à une autre, routés par KKAO : les types `call`, `result`, `event` et les champs `to`, `from`,
`callId`, `args` sont réservés. Ne pas les envoyer : KKAO 2.0 les ignore (journalisés comme types inconnus).

## Cycle de vie

1. KKAO crée le tube, puis lance l'appli (`UseShellExecute=false`, sans fenêtre console ; stdout/stderr lus en continu vers le journal).
2. L'appli se connecte et envoie `hello` ; KKAO répond `welcome` (puis les commandes en attente **(v2)**).
   L'appli envoie `actions` **(v2)**, `menu` et `status`, et les renvoie à chaque changement.
3. Arrêt demandé (menu *Arrêter*, ou KKAO quitte) : `quit`. KKAO attend 10 s au maximum et **ne tue jamais** une appli
   (une vidéo en cours doit pouvoir se terminer) ; l'appli quitte de toute façon quand le tube se ferme.
4. Fin inattendue (code ≠ 0 sans `quit` ni `retry`) : notification « X s'est arrêté » ; le menu propose *Démarrer*.
5. Appli déjà ouverte **hors de KKAO** (instance unique) : la nouvelle instance demande à l'ancienne de se fermer
   (mécanisme propre à l'appli), envoie `hello` puis `retry` (`after` ≈ 3000) et quitte avec le code 75.

## Mises à jour automatiques (KKAO 2.3)

Réglage *Installer les mises à jour automatiquement* (activé par défaut). KKAO télécharge en arrière-plan,
pendant que l'ancienne version continue de tourner, et vérifie l'empreinte. Ensuite :

1. **KKAO d'abord** : si KKAO a une nouvelle version, les applis attendent qu'il ait redémarré dessus.
   Au redémarrage, KKAO relance les applis qui tournaient.
2. **Une appli arrêtée** est mise à jour tout de suite. **Une appli ouverte** l'est dès qu'elle se ferme, au prochain
   démarrage de KKAO (avant qu'il la lance), ou quand le PC est inactif depuis 30 min et qu'aucune appli n'est `busy`.
   Elle reçoit alors un `quit` normal, puis elle est relancée.
3. **Retour arrière** : l'ancienne version est gardée. Si la nouvelle n'envoie pas `hello` dans les 30 s ou s'arrête
   d'elle-même, KKAO remet l'ancienne et ne retente plus automatiquement cette version.

Pour l'appli, cela demande seulement deux choses : publier `busy` quand il le faut, et accepter un `quit` à tout moment
où elle n'est pas `busy`.

## tools.json

À côté de `KKAO.exe` ou dans le dossier parent. Chemins relatifs = relatifs au dossier de `tools.json`
(pour `exe`, `cwd`, `icon` et les arguments qui commencent par `.\` ou `..\`).

```json
{
  "tools": [
    {
      "id": "instruktion", "name": "InstruKtion", "aliases": ["procedurier"],
      "exe": "..\\InstruKtion\\node_modules\\electron\\dist\\electron.exe",
      "args": ["..\\InstruKtion"], "cwd": "..\\InstruKtion",
      "icon": "..\\InstruKtion\\assets\\icon.png",
      "autostart": true, "protocol": true
    },
    {
      "id": "kalepin", "name": "Kalepin", "aliases": ["papernotes"],
      "exe": "..\\Kalepin\\dist\\Kalepin.exe",
      "args": [], "autostart": true, "protocol": true
    }
  ]
}
```

| Champ | Rôle |
|---|---|
| `id` | Identifiant technique (`A-Z a-z 0-9 . _ -`, 64 max) : nom du tube, réglages. **Ne change jamais** (sinon : `aliases`). |
| `name` | Nom affiché. Facultatif **(v2)** : sinon le `name` du `hello`, sinon l'`id`. |
| `exe` | Programme à lancer. Sans chemin : cherché à côté de tools.json puis dans le PATH. `%VARIABLE%` développé. |
| `args` | Arguments (KKAO ajoute `--kkao <tube>` à la fin pour une appli qui parle le protocole). |
| `cwd` | Dossier de travail (par défaut : celui de `exe`). |
| `icon` **(v2)** | Image de l'appli, `.ico` ou `.png` (l'emporte sur `hello.icon`). |
| `aliases` **(v2)** | Anciens `id` de l'appli : ses réglages, son catalogue et ses entrées du menu rapide sont repris. |
| `autostart` | Démarrage avec KKAO par défaut (`false` si absent) ; le choix de l'utilisateur (*Démarrer avec KKAO*) l'emporte. |
| `protocol` | `true` (défaut) : appli hébergée (tube, menu, état). `false` : simple lanceur, l'élément de menu démarre le programme. |

## Ce qu'une appli doit faire (liste de contrôle v2)

1. Accepter `--kkao <tube>` (chemin complet) ; se connecter (200 ms × 10 s) ; sinon fonctionner seule avec sa propre icône.
2. Envoyer `hello` (`protocol: 2`, `tool`, `name`, `version`, `icon`) en premier, puis `actions`, `menu`, `status` ;
   les renvoyer à chaque changement.
3. Donner un **id stable** à chaque action du catalogue et l'utiliser aussi dans le menu.
4. Exécuter tout id du catalogue reçu dans `command`, y compris juste après `welcome`.
5. Ne plus afficher d'icône ; passer ses bulles par `notify` ; retirer *Quitter* et *Lancer au démarrage de Windows* de son menu.
6. Quitter proprement sur `quit` **ou** quand le tube se ferme.
7. Instance unique déjà prise hors de KKAO : demander à l'autre instance de se fermer, `hello` + `retry`, code 75.
8. Gérer ses raccourcis globaux elle-même et les publier dans `actions[].shortcut` (+ `shortcutError`).
9. Publier ses réglages utiles dans `settings` (jamais de secret), appliquer `setSetting` et republier.
10. Fonctionner depuis son dossier d'installation (`%LOCALAPPDATA%\Programs\KKAO\apps\<id>\`) sans y écrire.
11. **(2.3)** Publier `status.busy:true` tant qu'un `quit` ferait perdre quelque chose, puis `busy:false`.
