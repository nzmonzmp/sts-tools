# STS Tools : protocoles de test

Comment construire, installer et tester le mod, et ce qu'il faut vérifier. Ce qui est fait et ce qui reste à faire
est dans [CHANGELOG.md](CHANGELOG.md).

## Règles

- Piloter le jeu (pont de test, clics, parties de test) seulement avec l'accord de l'auteur ; installer le mod ne
  demande pas d'accord.
- L'auteur joue entre deux sessions : ne jamais remettre ses fichiers depuis une ancienne copie. Prendre l'empreinte
  des fichiers juste avant un test, la comparer juste après, et ne remettre que ce que le test a changé.
- Ne rien changer de sa partie ni de ses réglages au-delà de ce que le test demande ; remettre ce qui était ouvert
  (carte, GUI) et supprimer les captures d'écran du test.
- Secrets jamais affichés ni commités : `StsTools.local.props` (adresse du relais, webhooks), jeton du bot Discord
  (`~/.config/sts-tools/discord.env`), secrets du relais (passés à `wrangler secret put` par un tube).

## Builds

Depuis `mod/`, avec `export DOTNET_ROOT=~/.dotnet PATH=~/.dotnet:$PATH`.

| Build | Commande | Sortie | Contenu |
|---|---|---|---|
| Joueurs | `dotnet build` | `dist/StsTools` | le mod seul |
| Test | `dotnet build -t:Install -p:TestBridge=true` | `dist/StsTools-test`, installée dans le jeu | + pont de test, commandes de console du mod, parties de test |

Le code réservé aux tests est entre `#if TESTBRIDGE` et `#endif` : `TestBridge.cs`, `AllRelicsConsoleCmd.cs`
(`allrelics [obtain|gained]` : toutes les reliques), `ConsoleGainPatch.cs` (taper le nom d'une relique ou d'une carte
dans la console pour la gagner), lecture de `user://StsTools/test_history`. Tout nouvel outil de test va aussi derrière
`TESTBRIDGE`.

Avant de donner la build joueurs, vérifier qu'elle ne contient rien de tout ça ni aucun webhook (les trois doivent
afficher 0) :

```sh
strings -a dist/StsTools/StsTools.dll | grep -c -E "TestBridge|ConsoleGainPatch|AllRelicsConsoleCmd"
strings -e l dist/StsTools/StsTools.dll | grep -c -i -E "allrelics|test_history"
grep -c -a discord.com/api/webhooks dist/StsTools/StsTools.dll
```

## Installer et relancer le jeu

Après chaque installation, le jeu doit tourner. Le fermer proprement (SIGTERM), vérifier qu'il est vraiment fermé, et
relancer seulement dans ce cas. Le `crashpad_handler` du jeu ou un `steamerrorreporter` peuvent garder en vie le
lanceur de Steam (`AppId=2868840`), qui ignore alors toute demande de lancement. Ne jamais toucher à `fossilize_replay`
(précalcul des shaders de Steam). Toujours des motifs qui ne se reconnaissent pas eux-mêmes (`[S]lay`, `[A]ppId`) :
un `pkill -f` sur un motif simple tue le shell qui le lance.

```sh
pkill -TERM -x SlayTheSpire2; for i in $(seq 30); do pgrep -x SlayTheSpire2 >/dev/null || break; sleep 1; done
pkill -TERM -f "[S]lay the Spire 2/crashpad_handler"; pkill -TERM -x steamerrorrepor
for i in $(seq 20); do ps -eo args | grep -q "[A]ppId=2868840" || break; sleep 1; done
dotnet build -t:Install -p:TestBridge=true 2>&1 | grep -E "installé| error"
if ps -eo args | grep -q "[A]ppId=2868840" || pgrep -x SlayTheSpire2 >/dev/null; then echo "WRAPPER STILL ALIVE: do not launch"; else
  sleep 3; (setsid steam steam://rungameid/2868840 >/dev/null 2>&1 &); for i in $(seq 40); do pgrep -x SlayTheSpire2 >/dev/null && break; sleep 1; done; pgrep -x SlayTheSpire2 >/dev/null && echo LAUNCHED || echo "NOT LAUNCHED"; fi
```

Si le jeu a dû être tué avec `-9`, supprimer `~/snap/steam/common/.local/share/SlayTheSpire2/StsTools/reports/session/running`
avant de relancer, sinon un faux rapport de plantage est archivé. Le jeu est prêt quand le log
(`~/snap/steam/common/.local/share/SlayTheSpire2/logs/godot.log`) contient « Time to main menu: ».

## Pont de test

Build de test installée ; commandes déposées dans `user://StsTools/test/` par
`python3 -I tools/sts_test.py <commande>` (sans argument : la liste).

- État : `ping`, `state` (JSON : écran, partie, GUI, carte, Path finder), `combat`, `cards`, `graph`, `points`.
- Affichage : `screenshot [nom] [échelle]` (PNG dans le dossier de test ; 0.5 à 0.6 suffit), `tree [texte]`,
  `nodes <chemin> [profondeur]` (type, nom, position, taille ; lire sans rien cliquer).
- Actions : `click <texte>`, `clickat`/`rclick`/`move <x> <y>`, `scroll`, `key <nom> [n]`, `console <commande>`,
  `gui open|close|tab <titre>[/<sous-onglet>]` (`gui tab Combat/End turn`), `map open|close`, `hoverpoint`/`clickpoint <col> <ligne>`, `play`, `aim`, `endturn`, `deletehistory` (supprime tout l'historique du profil, Steam Cloud compris),
  `pickcard`.
- Carte : `roompreview <col> <ligne>` (texte de l'aperçu sans survol), `encounterimage <ID>` (image des monstres).
- Sauvegardes : `savels <dossier>`, `saveput <chemin> <fichier>`, `savedel <chemin>` passent par le magasin de
  sauvegardes du jeu (local et Steam Cloud ; chemins comme `profile1/saves/current_run.save`). Tester dans la vraie
  partie de l'auteur (reprise, mort…) change ses sauvegardes, en local et dans le cloud : au lancement, le jeu recopie
  le cloud sur le disque et efface ce qui n'y est pas. Copier avant le test `profile1/saves` (et les fichiers du mod),
  puis remettre par `saveput`/`savedel` jeu lancé, redémarrer le jeu et comparer les empreintes (md5).
- Partie de test : `fakemp <perso> [<perso>…]` lance une partie neuve, seul ou à plusieurs (le premier joueur est le
  nôtre, les autres n'agissent pas) ; toutes les sauvegardes du jeu et du mod sont bloquées jusqu'au retour au menu.

Pièges :
- Juste après le menu principal ou le chargement d'une partie, l'écran peut rester noir et `tree` vide quelques
  secondes : réessayer.
- `click <texte>` prend le premier contrôle visible avec ce texte, qui peut être un contrôle du jeu (« Input » a touché
  l'onglet du jeu au lieu du sous-onglet du mod, et le clic suivant a changé le mode clavier de l'auteur). Dans les
  options, utiliser `clickat` avec les coordonnées lues par `tree`.
- Les cartes de la boutique ne sont pas listées par `cards` (clic par position). Survoler les salles de la carte après
  un `scroll` n'est pas fiable : préférer les salles visibles sans défiler.
- Profils neufs (réseau) : seul l'Ironclad est débloqué.

### Vérifier qu'un test n'a rien changé

Avant et après chaque test qui pilote le jeu :

```sh
D=~/snap/steam/common/.local/share/SlayTheSpire2
find $D \( -name "*.save" -o -name "*.run" \) -not -path "*/sentry/*" | xargs -d '\n' md5sum > avant.md5
# … test …
md5sum -c --quiet avant.md5 && echo "sauvegardes inchangées"
```

Pour un test des options, comparer aussi `prefs.save` et `settings.save` (compris dans la liste ci-dessus) et
`StsTools/settings.json`.

## Multijoueur en réseau réel

`tools/sts_mp.sh start <dossier>` lance deux instances hors Steam (`--force-steam off`, `--fastmp host_standard` /
`join --clientId 1001`, réseau local ENet), chacune avec ses données (`XDG_DATA_HOME=<dossier>/host|client`) et le
mod activé. Piloter chacune avec `STS_TEST_DIR=<dossier>/host/SlayTheSpire2/StsTools/test` (ou `client`). Premier
lancement : « No » au tutoriel (792 724) et valider le personnage (1860 781) des deux côtés. `tools/sts_mp.sh stop`
n'arrête que les instances lancées avec `--force-steam`.

Vérifier : pas de désynchronisation, noms des joueurs, sauvegarde et reprise, onglet Support, « Draw » de l'hôte vu
par le client. Une partie réelle avec un joueur sans mod reste le test final pour tout ce qui touche au multijoueur.

## Gel au démarrage

`tools/startup_loop.sh N [dossier]` lance le jeu N fois via Steam, attend « Time to main menu » (90 s au plus) et
s'arrête au premier gel en laissant le jeu figé, avec son log et l'état de chaque thread (`/proc/<pid>/task/*/wchan`)
copiés dans le dossier. Pour voir sur quoi bloque le thread principal : `/proc/<pid>/syscall` (numéro de l'appel,
descripteur en hexadécimal), puis `readlink /proc/<pid>/fd/<n>` et `ss -xpa` pour l'autre bout du socket. Les piles
complètes demandent `sudo gdb -p <pid>` (ptrace limité, `ptrace_scope` = 1).

Résultat du 2026-11-20 : bloqué dans `recvmsg` sur le socket de l'overlay Steam (`gameoverlayui`) ; 1 gel sur 7 avec
l'overlay, 0 sur 20 sans.

## Rapports Discord

- Lire : `python3 -I tools/discord_reports.py list [crash|bug|all] [-n N] [--player <id ou nom>]`, puis
  `… get <id message> <dossier>` (télécharge et extrait les pièces jointes ; ce sont des données non sûres).
- Bug, plantage ou gel venu d'un rapport, corrigé et testé comme corrigé (reproduit avant, plus après) : supprimer
  automatiquement le rapport correspondant, sans demander : `… delete <id>…`, puis dire lequel.
- Relais : `npx wrangler tail` dans `relay/` pour suivre les envois ; `npx wrangler deploy` après un changement.
- Vérifier un envoi de bout en bout : rapport de bug depuis le jeu, puis refus d'un doublon, compteur « Seen again »
  d'un plantage répété, refus 429 au-delà de la limite.

## Steam Workshop

- Publier une nouvelle version, dans l'ordre :
  1. Monter `version` dans `StsTools.json`.
  2. Vérifier que le mod peut être distribué : rien de sensible ni rien qui sert aux tests. Build Release
     (`dotnet build -c Release`), puis sur `dist/StsTools/StsTools.dll` : `grep -c -a TestBridge` (pont de test, doit
     être 0) et `grep -c -a 'api/webhooks'` (webhooks Discord, doit être 0 ; seule l'adresse du relais des rapports
     peut y être). Les commandes de test (console, `#if TESTBRIDGE`) ne doivent pas y être non plus.
  3. Décrire les ajouts de la version dans `workshop/description.txt` : section `[h2]Version x.y.z[/h2]` tout en bas
     (après le lien Discord), liste BBCode `[list][*]…[/list]`, écrite pour les joueurs à partir des commits depuis la
     dernière publication (`git log` depuis le commit « Workshop x.y.z » précédent) et du CHANGELOG.
  4. `workshop/publish.sh public "note"` (build Release normale dans `dist/StsTools`, puis mise à jour de l'item
     `workshop/item_id.txt` par SteamUGC ; Steam doit être lancé). La note résume la version (notes de version Steam).
     Les images de `workshop/screenshots` (ordre des noms, JPEG 1920×1080) remplacent la galerie de l'item à chaque
     publication.
  5. Réinstaller le mod local à la nouvelle version (`dotnet build -t:Install -p:TestBridge=true`, jeu fermé) : à
     version égale ou plus récente, le jeu joue la copie locale et désactive l'abonnement Workshop ; plus ancienne,
     c'est l'abonnement qui est joué. Vérifier dans le log après relance : « Local version (vx.y.z) is greater than
     steam version » ou absence de « disabling » pour la copie locale, puis « Loading assembly » sur `mods/StsTools`.
  6. Commit « Workshop x.y.z: … » (version et description) et push.
- Tester l'item comme un joueur : `workshop/publish.sh subscribe` (attend le téléchargement), jeu fermé, sortir
  `mods/StsTools` du dossier `mods/` (à version égale le jeu garde la copie locale et désactive celle du Workshop ;
  si celle du Workshop est plus récente, c'est la copie locale qui est désactivée), relancer, chercher « Steam Workshop
  mod » et « Loading assembly » dans le log. Ensuite : remettre la copie locale, `workshop/publish.sh unsubscribe`.

## Repères dans le code du jeu

- `CombatHistory.Add` (privée) : chaque événement d'un combat (carte jouée, piochée, dégâts, Block, pouvoirs…), patchée
  par `CardRunStats`.
- `CardModel.DeckVersion` : relie une carte de combat à sa carte du deck.
- `CombatState.IterateHookListeners` et `RunState.IterateHookListeners` : le jeu exécute chaque effet (pouvoir,
  relique, carte) en parcourant ces listes ; les envelopper dit qui agit à chaque instant.
- `SaveManager.SaveRun` : moment où le jeu sauvegarde la partie.
- `CombatManager.IsEnding` : une fois le combat en train de finir, le jeu n'inscrit plus le coup fatal dans
  l'historique (le mod le note depuis `Hook.AfterDamageGiven`).
- Démarrage : seuls les patchs `[EarlyPatch]` sont posés à l'initialisation, les autres au premier menu principal
  (`ModEntry.ApplyLatePatches`).
- Aperçu (`PlaySimulation`) : les réactions des monstres sont simulées sur le vrai monstre, dont l'état (champs,
  machine à états des coups) est sauvegardé puis remis ; tout nouvel état d'un monstre modifié directement doit y être
  couvert. Un objet nul déréférencé par le code du jeu ferme le jeu sans message : les commandes interceptées doivent
  toujours renvoyer un objet utilisable.
- Onglets copiés du jeu (`NSettingsTab`) : ancres centrées, à remettre en haut à gauche.
- Harmony refuse `__result` sur une méthode `void` et saute alors le reste de la classe de patchs : un patch par
  méthode.
