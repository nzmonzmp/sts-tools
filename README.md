# sts-tools

Outils en ligne de commande pour analyser l'historique de parties de **Slay the Spire 2** :
taux de victoire par personnage, par carte et par relique, taux de pick, et causes de défaite.

L'outil lit les fichiers `.run` que le jeu écrit à la fin de chaque partie. Il ne modifie jamais
les sauvegardes.

## Prérequis

- Linux, avec le jeu installé via Steam. Les chemins sont fixes pour l'instant :
  - historique des parties : `~/.local/share/SlayTheSpire2/steam/<id Steam>/<profil>/saves/history/*.run`
  - jeu (seulement pour `update-pools`) : `~/.local/share/Steam/steamapps/common/Slay the Spire 2/`
- Python ≥ 3.13 et [uv](https://docs.astral.sh/uv/)

## Installation

```sh
uv tool install git+https://github.com/nzmonzmp/sts-tools
```

Pour le développement, depuis un clone du dépôt :

```sh
uv tool install --editable .
```

En mode éditable, les modifications du code sont prises en compte immédiatement. Il faut
seulement réinstaller (`uv tool install --editable . --reinstall`) après une modification
de `pyproject.toml`. On peut aussi lancer l'outil sans l'installer : `uv run sts-tools ...`.

## Commandes

### `winrate` : victoires par personnage

```sh
sts-tools winrate
```

```
== Solo - A10 ==
DEFECT       6W 75L   (7%)
IRONCLAD     4W 32L  (11%)
...

== Coop - toutes ascensions ==
DEFECT       4W  3L  (57%)
...
```

Les parties solo et coop sont comptées séparément. En coop, c'est le personnage que tu as
joué qui est compté, retrouvé grâce à ton identifiant Steam.

### `cards` : statistiques par carte

```sh
sts-tools cards
sts-tools cards -c silent -s pick
```

Affiche un tableau par personnage, découpé selon le pool de la carte : cartes du personnage,
incolores, cartes des autres personnages, événements, quêtes, tokens, statuts, malédictions.

| Colonne      | Signification                                                    |
|--------------|------------------------------------------------------------------|
| `Parties`    | parties où la carte est dans le deck final                       |
| `Win%`       | taux de victoire de ces parties                                  |
| `Proposé`    | parties où la carte a été proposée en récompense de cartes       |
| `Pick%`      | part de ces parties où elle a été prise                          |
| `Win% pris`  | taux de victoire quand elle a été prise                          |
| `Win% passé` | taux de victoire quand elle a été proposée mais jamais prise     |

Les cartes retirées du jeu, c'est-à-dire absentes des pools actuels, ne sont pas affichées.

### `relics` : statistiques par relique

```sh
sts-tools relics
```

Un tableau par personnage, avec les mêmes colonnes que `cards`. `Parties` compte les reliques possédées en fin de partie.
`Proposé` et `Pick%` portent seulement sur les choix des Anciens : les coffres et les élites
ne laissent pas le choix, et la boutique n'est pas comptée.

### `deaths` : causes de défaite

```sh
sts-tools deaths
```

Affiche les défaites de chaque personnage, découpées par acte. Pour chaque acte, l'outil
liste les ennemis (ou événements) qui t'ont tué et les étages où tu es mort, avec le nombre
d'abandons. Les pourcentages sont calculés sur toutes les défaites du personnage, pour pouvoir
comparer les actes entre eux.

```
-- DEFECT - 81 parties, 6W (7%) --
75 défaites, dont 21 abandons

[Acte 1 - 34 défaites (45%), dont 15 abandons]
Tué par                   Défaites    %  dont abandons
EVENT NEOW                      10  13%             10
WATERFALL_GIANT_BOSS             5   7%              1
...
```

### `update-pools` : pools de cartes

```sh
sts-tools update-pools
```

Les fichiers de partie n'indiquent pas à quel personnage appartient une carte. Ces pools
sont définis dans le code du jeu. Cette commande les extrait de `sts2.dll` et les enregistre
dans le fichier `card_pools.json` du package (`src/sts_tools/` en mode éditable). Il faut la relancer
après une mise à jour du jeu qui ajoute ou retire des cartes. `--dll` permet d'indiquer un
autre chemin vers la DLL.

## Options communes

| Option              | Commandes                          | Effet                                                         |
|---------------------|------------------------------------|---------------------------------------------------------------|
| `-a`, `--ascension` | toutes sauf `update-pools`         | niveau d'ascension, ou `all` pour toutes                      |
| `-p`, `--profile`   | toutes sauf `update-pools`         | profil du jeu (défaut : `profile1`)                           |
| `--coop`            | `cards`, `relics`, `deaths`        | analyser les parties coop au lieu des parties solo            |
| `-c`, `--character` | `cards`, `relics`, `deaths`        | filtrer sur un personnage (`defect`, `ironclad`...)           |
| `-n`, `--min`       | `cards`, `relics`, `deaths`        | nombre minimum de parties pour afficher une ligne             |
| `-s`, `--sort`      | `cards`, `relics`                  | tri : `win` (défaut), `n` (nombre de parties) ou `pick`       |

`-n` vaut 1 par défaut : toutes les lignes sont affichées. Valeurs par défaut de `-a` :

| Commande           | en solo | en coop            |
|--------------------|---------|--------------------|
| `winrate`          | 10      | toutes ascensions  |
| `cards`, `relics`  | 10      | 10                 |
| `deaths`           | 10      | toutes ascensions  |

## À savoir

- **Abandons :** une partie abandonnée compte comme une défaite, y compris un abandon dès
  Neow pour relancer un départ. `deaths` affiche le nombre d'abandons.
- **Petits échantillons :** avec peu de victoires, la plupart des pourcentages reposent sur
  quelques parties seulement. La colonne `Parties` ou `Proposé` donne la taille de
  l'échantillon.
- **Identifiants bruts :** les cartes, reliques et ennemis sont affichés avec leur
  identifiant interne au jeu (`BALL_LIGHTNING`), pas avec leur nom traduit.
