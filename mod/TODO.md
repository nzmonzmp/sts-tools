# À faire

## Stats des reliques

Fait : sous la description de chaque relique possédée (survol), mises à jour en direct, les mêmes stats que
les cartes (dégâts, Block, énergie, cartes, or, soins, pouvoirs donnés et leurs effets), avec le total, la
moyenne par combat, la moyenne par tour et le combat en cours (part des dégâts et du Block du joueur pour les
bonus). Bonus indirects, pour les cartes comme pour les reliques : Block en plus (Dexterity, Pael's Legion),
pioche du début de tour, énergie (gains augmentés, énergie de chaque tour), or, PV perdus évités (Tungsten
Rod), pouvoirs donnés en plus, cartes rejouées. Effets à la prise d'une relique : PV max, or, cartes
ajoutées, retirées, améliorées (deck ou combat), transformées ou enchantées, potions et emplacements de potion.
Aussi : énergie et étoiles économisées sur le coût des cartes, ennemis étourdis, orbes évoquées, emplacements
d'orbe, forge (Sovereign Blade), Block gardé d'un tour à l'autre (Calipers, Barricade), mort évitée (Lizard
Tail, avec ses PV rendus), cartes gardées en main (Runic Pyramid, seulement celles qui auraient été défaussées),
tour en plus, X augmenté (Chemical X), coups et déclenchements d'orbe en plus, debuffs évités (Artifact), cartes
améliorées ou enchantées en entrant dans le deck (œufs, Fresnel Lens), or économisé en boutique, soins en plus
au feu de camp, choix de cartes, récompenses et options de feu de camp en plus, potions imposées, trésors sautés
(Silver Crucible), cartes améliorées ou enchantées en vitrine puis achetées (œufs, Fresnel Lens), articles remis
en stock (The Courier), salles inconnues sans combat (Juzu Bracelet, Golden Compass), déplacements libres
(Winged Boots), PV retirés aux ennemis (Fur Coat), valeur des orbes en plus (Focus…), salles du chemin doré
(Golden Compass). Lignes affichées selon le code de la carte ou de la relique, les autres à part.


## Stats de combat

Fait : onglet Combat (total du combat, ce tour, par tour) avec les mêmes stats que les cartes, pour
les cartes du joueur, dont les dégâts bonus (Force, debuffs), les dégâts évités grâce à Weak et les soins ;
dégâts subis et bloqués par le joueur.

Fait : onglet Support (multijoueur seulement) : ce que chaque joueur a donné à ses alliés (énergie, cartes créées,
cartes piochées par un effet, Block, buffs, PV rendus) par ses cartes, reliques, potions et les pouvoirs qu'il a
donnés ; combat en cours, partie, moyenne par combat ; sauvegardé avec la partie. Vérifié dans une vraie partie à
plusieurs.

Fait : dégâts bonus donnés aux alliés (Vulnerable posé sur un ennemi, Force donnée à un allié) et dégâts évités aux
alliés (Weak posé sur un ennemi), dans l'onglet Support, partagés au prorata entre les joueurs qui ont donné le
pouvoir. Orbes placées chez un allié (Ignition) et leurs effets. Vérifié dans une vraie partie à plusieurs, avec les
encadrés des alliés dans l'aperçu.

## Aperçu des cartes jouées et des potions

Fait : potions en combat aussi (au survol de la cible pour celles qui visent, au survol de l'emplacement pour les
autres). GUI ouvert, en visant une créature (ou en entrant dans la zone de jeu pour une carte sans cible), un encadré
à côté de chaque créature concernée : dégâts, part qui passe le Block, coup fatal, étourdi, pouvoirs, Block, PV
perdus (épines…), énergie, pioche, orbes ; dégâts annoncés évités par monstre ; pour le joueur, dégâts évités
jusqu'à l'attaque des ennemis (fin de tour comprise : Plating, Burn, poison…) et bilan si la carte coûte des PV ;
en multijoueur, la même chose pour chaque allié dans un encadré à côté de lui (ennemi tué avant son attaque, Weak,
Block donné…), les attaques calculées contre lui.
Calculé en jouant la carte à blanc avec le code du jeu (PlaySimulation) : écouteurs copiés, commandes
interceptées, hasard remis en place ; moyenne si le hasard intervient, fourchette si un choix du joueur
intervient, effet combiné des cartes choisies pendant un écran de choix (Sly, épuisement…).

Aussi : fin de tour séparée (« End of turn: »), cibles au hasard des cartes jouées par un effet (fourchette et
espérance), bouton de fin de tour (tête de mort si terminer le tour tue le joueur, tour supplémentaire de
Pael's Eye), Sandpit, réactions aux pouvoirs donnés (Vicious), tête de mort sur les monstres qui mourront sans rien
faire (rouge : avant d'agir ; violet : après, Doom) et sur ceux que la carte tue à coup sûr.

- Les réactions des monstres eux-mêmes (MonsterModel) sont simulées sur le vrai monstre, dont l'état (champs,
  machine à états des coups) est sauvegardé puis remis : tout nouvel état d'un monstre modifié directement doit y
  être couvert.
- Un objet nul déréférencé par le code du jeu ferme le jeu sans message : les commandes interceptées doivent
  toujours renvoyer un objet utilisable (créatures détachées, résultats vides).

## Rapports de bug

Fait : journal de session écrit au fil de l'eau dans `user://StsTools/reports/session` (contexte, lignes du log du
jeu, actions du mod, exceptions), trace pas à pas de l'aperçu en cours (`preview.log`), rapport de gel si le jeu
n'affiche plus d'image pendant 10 s (`freeze.txt`). Effacé à la fin d'une partie ou à la fermeture normale ; sinon
archivé au lancement suivant dans `reports/crash-…` avec le log complet du jeu et les sauvegardes de la partie, et
signalé au menu principal. Une fermeture sans plantage (menu du jeu, Alt+F4, arrêt depuis Steam ou kill : SIGTERM) n'en crée pas,
sauf si le jeu était figé ; un plantage ou un SIGKILL, si.

Fait : envoi du rapport dans un salon Discord (webhook) : au menu principal, Send / Always send / Don't send / Never
ask ; message de résumé et rapport en zip (moins de 10 Mo : sans les sauvegardes puis fin des logs seulement s'il le
faut) ; envoi raté retenté au lancement suivant. L'adresse du webhook est dans `StsTools.local.props`
(`CrashWebhook`, jamais commitée) et mise dans la DLL à la compilation : un mod compilé sans elle n'envoie rien.

Fait : bouton « Report a bug » (menu principal, menu pause) : le joueur décrit le bug, donne son nom s'il veut, et
peut joindre le journal, le log du jeu et sa partie en cours ; envoyé dans le salon Discord privé bug-report par un
second webhook (`BugWebhook` dans `StsTools.local.props`). Sans ce webhook, le bouton n'apparaît pas.

- Le mod compilé contient les adresses des webhooks (crash-reports, bug-report) : ne le donner qu'aux joueurs de
  confiance ; si une adresse fuit, supprimer le webhook et en créer un autre.

## GUI

Fait : déplaçable (en-tête), hauteur réglable (poignée du bas, double-clic : automatique), toujours dans l'écran et
sous la barre du haut, repliable (flèche, touche Collapse GUI).

## Multijoueur

Fait : totaux par joueur (cartes, dégâts subis et bloqués, Osty, soins), sauvegardés pour chaque joueur ; dans
le panneau, rangée des joueurs (toi, chaque coéquipier, l'équipe) pour les onglets Deck, Combat et Game ;
infobulles des reliques des coéquipiers. Le mod ne change rien à l'état de la partie (outils de test bloqués en
multijoueur) et déclare ne pas toucher au gameplay : les autres joueurs n'ont pas besoin de l'avoir. Vérifié dans
une vraie partie à plusieurs avec un joueur sans aucun mod : pas de désynchronisation, noms des joueurs, sauvegarde
et reprise.

Fait : dégâts annoncés par les ennemis pour chaque joueur dans l'onglet Combat (une ligne par joueur pour l'équipe),
calculés contre ce joueur ; pendant le tour des joueurs, avec la fin de tour simulée. Vérifié dans une vraie partie
à plusieurs.

## Carte : aperçu des salles

Fait : sur la carte, GUI ouvert, au survol d'une salle pas encore faite, un encadré à côté d'elle dit ce qui peut y
arriver, jamais ce qui va vraiment arriver (rien n'est lu de la graine ni des listes de rencontres et d'événements
tirées par le jeu en début d'acte) :
- combat, élite, boss : ennemis possibles (rencontres faciles ou difficiles de l'acte selon le nombre de combats d'ici
  là, moins celles que le sac du jeu ne peut plus tirer d'après les combats déjà faits et la famille du combat d'avant),
  or (fourchette, avec Bowler Hat, Ectoplasm, Amethyst Aubergine, Poverty), chance de potion, récompenses de cartes
  (Prayer Wheel, White Star, Vintage) et rareté des cartes, reliques (Black Star, Lava Rock) et leur rareté ;
- « ? » : chance de chaque issue (événement, combat, marchand, trésor, élite), avec les règles du jeu (marchand exclu
  après un marchand ou avant des marchands seulement, Juzu Bracelet, Golden Compass, Lantern Key, Dowsing, première
  partie), puis les ennemis et gains d'un combat et d'un trésor ;
- trésor : or et relique ; feu de camp : soin.
Les chances de potion, de rareté et des « ? » partent des valeurs courantes du jeu (déduites des tirages passés) et sont
moyennées sur tous les résultats possibles des salles d'ici là ; fourchette selon les chemins. Les méthodes des
reliques sont appelées directement, sans les hooks du jeu que le mod compte pour ses stats. Vérifié avec le pont :
rencontres déjà faites retirées, chances de potion et de rareté après des combats, « ? » après un événement et avant un
marchand, encadré caché GUI fermé.

Aussi : déplacement libre (Winged Boots : sauts restants ; Flight : sans limite) dans les chemins d'ici là ; cartes
proposées : Lasting Candy (+1 Pouvoir un combat sur deux), amélioration du jeu à partir de l'acte 2, Molten, Toxic et
Frozen Egg, Lava Lamp, Silver Crucible (ses premières récompenses), Glitter, Silken Tress, Fresnel Lens, Wing Charm ;
Big Game Hunter (cartes rares aux élites) ; Wongo's Mystery Ticket (reliques au 5e combat). Vérifié avec le pont.

Aussi : petite image fixe des monstres à droite du nom de chaque rencontre (même rendu que le survol des rencontres de
l'écran Stats, partagé dans `EncounterVisuals`), plus petite si la liste est longue ; faite une fois par rencontre, les
rencontres de l'acte préparées une à une dès l'ouverture de la carte GUI ouvert. Monstres dans leur animation de départ,
comme en combat (Cubex Construct enterré).

- Hors de portée : l'or et les combats propres à un événement (l'événement n'est connu qu'en entrant dans la salle).

## Carte : chercheur de chemins

Fait : onglet « Path finder » du GUI, seulement quand la carte est ouverte (l'onglet revient tout seul à la
réouverture de la carte s'il était le dernier choisi). Classement « Importance » (▲ ▼) des types de salles (marchands,
élites, feux, « ? », combats), appliqué dans l'ordre sur tout l'acte (salles déjà faites comprises) : pour chacun, Max
(le plus de salles), Min (le moins), exactement n ou au moins n (bouton ≥), respecté strictement (un nombre impossible
reste affiché en rouge, aucun chemin). Nombres proposés pour un type : ceux qui restent possibles une fois les types
au-dessus appliqués. Les types Any ne départagent pas les chemins. GUI ouvert, les meilleurs chemins de la salle
actuelle au boss sont tracés sur la carte (couleur du premier type) : tous ceux à égalité (case « Show all », cochée
par défaut, cachée s'il n'y a qu'un chemin) avec la même opacité, ou un par un avec ◀ ▶. Composition de l'acte en
suivant le chemin affiché (fourchette s'ils sont tous affichés). Choix gardés dans les réglages.

Fait (vérifié avec le pont, en multijoueur factice puis en réseau réel : dessin de l'hôte vu par le client) : bouton
« Draw » (multijoueur seulement) qui dessine le ou les
chemins affichés avec l'outil de dessin de la carte du jeu (couleur du personnage) : les autres joueurs les voient,
même sans le mod ; effaçables avec les outils du jeu. Simple dessin, rien d'autre ne change dans la partie.

- Activer ou non le tracé des chemins : option à mettre avec les réglages du mod (voir plus bas).

Fait : déplacement libre pris en compte à chaque étape (Winged Boots : sauts restants ; Flight : sans limite), sauts
marqués d'un trait doré ; règles Min (le moins de salles) et « au moins n » (bouton ≥) à côté de Max et du nombre
exact. Recherche sur les compositions possibles des chemins (plus d'énumération) ; au-delà de 300 chemins à égalité,
« N+ ». Vérifié avec le pont contre une énumération brute (76 chemins sans saut, 266 131 avec 3 sauts) : chemins et
nombres proposés identiques dans tous les scénarios ; nombres sur tout l'acte vérifiés seulement en début d'acte.

## Rapports de bug et de plantage : spam et doublons

- Éviter le spam : limiter le nombre d'envois (par exemple quelques rapports par heure et par joueur, délai minimal
  entre deux « Report a bug »), et ne pas renvoyer un rapport déjà envoyé.
- Éviter les doublons : reconnaître un plantage déjà signalé (même dernière erreur, même aperçu non terminé, même
  carte ou même combat) et, plutôt qu'un nouveau message avec son zip, le compter sur le rapport existant ou envoyer
  un message court qui renvoie au premier.
- Regrouper dans Discord les rapports d'une même personne (identifiée par son id Steam, déjà dans chaque rapport) :
  par exemple un fil par joueur (salon forum, ou paramètre thread_id/thread_name du webhook), où arrivent tous ses
  rapports de bug et de plantage.
- Limiter côté serveur si possible : un webhook dans le mod peut être utilisé par n'importe qui qui le récupère ; un
  petit relais (Cloudflare Worker) pourrait filtrer et limiter les envois.

## Serveur Discord : finaliser

- Finaliser le serveur Discord « StsTools » : aujourd'hui seulement les salons privés crash-reports et bug-report
  (webhooks du mod) et le bot de lecture « StsTool reader ».
  - à préciser : salons publics (annonces, versions du mod, aide), rôles et permissions, invitation des joueurs ;
  - garder crash-reports et bug-report privés (journaux, parties et ids Steam des joueurs).

## Options du jeu : réglages du mod

- Dans les options du jeu, onglet Mod (aujourd'hui : seulement les touches), des réglages du mod :
  - GUI : ce qui s'affiche quand on l'ouvre avec sa touche (onglets visibles, onglet ouvert par défaut, panneau
    replié ou non…) et ce qu'il active (aperçu des cartes et des potions, têtes de mort, aperçu des salles sur la
    carte, tracé des chemins du Path finder…) ;
  - rapports de plantage : demander, toujours envoyer ou ne jamais envoyer (aujourd'hui seulement modifiable depuis
    l'encadré du menu principal après un plantage) ;
  - rapports de bug : nom proposé par défaut, joindre les logs et la partie ou non par défaut.

## Vu en jouant l'acte 1 avec le pont (2026-11-19)

Corrigé :
- Coup qui tue le dernier ennemi pas compté (dégâts, ennemi tué) : le jeu ne l'inscrit pas dans l'historique une fois
  le combat en train de finir (`CombatManager.IsEnding`) ; noté depuis `Hook.AfterDamageGiven` (FinalBlowPatch).
- « HP healed » en décimales (repos : 26,1 au lieu de 26) : PV vraiment rendus, tronqués comme le jeu.
- Reprendre une partie comptait un tour de plus (onglet Game) : tours comptés seulement en combat, et la reprise
  repart du tour en cours.
- Infobulle des reliques, « Per turn » : le premier tour de chaque combat n'était pas compté (le jeu commence au tour 1
  sans IncrementTurnNumber), d'où « - » au premier tour et des moyennes par tour trop hautes.
- Path finder : nombres comptés sur tout l'acte (salles déjà faites comprises) ; un nombre choisi reste dans le
  classement d'importance, appliqué à sa place (Max ou nombre, dans l'ordre), les nombres proposés tenant compte des
  types au-dessus ; seuls les types Any restent à part. Anciens réglages : les nombres passent en tête du classement.
  Vérifié sur la carte de l'acte 2.

Pas un bug :
- « HP healed » : les 64 PV rendus par l'Ancien (Neow) au début de l'acte ; le jeu les compte aussi (`hp_healed` de
  l'étage 1 dans l'historique de la partie).

## Retours de parties (2026-11-19)

Corrigé et vérifié avec le pont :
- Kaiser Crab : les pinces jouaient leur animation de dégâts (et de mort) en visant une carte. Les réactions du
  monstre (AfterCurrentHpChanged, BeforeDeath) appellent directement le décor animé du boss : bloqué pendant les
  aperçus, avec les autres animations directes (Spine, flou, taille, retrait du nœud d'une créature).
- Hex (élite de l'acte 3) : la surcouche des cartes disparaissait dès l'ouverture du GUI. Les copies de cartes de
  l'aperçu (MutableClone → DeepCloneFields → AfflictInternal) prévenaient la vraie carte, qui rechargeait sa surcouche
  pendant l'aperçu, où l'ajout est bloqué : événements des copies vidés avant.
- Void Form (carte qui termine le tour) : « Ends your turn », et la tête de mort sur le joueur si la fin du tour qui
  suit le tue (avant : seulement l'écart avec un tour terminé sans la carte, nul s'il mourait dans les deux cas).
- Forge (Regent) : ligne « Forge +n » dans l'aperçu.
- Cartes qui défaussent (Survivor, Acrobatics…) : lignes « Discard » et « Sly played ».
- Rapport de bug : les flèches déplaçaient le focus vers le menu pause derrière (passage en clavier seul du jeu) au
  lieu du curseur du texte.

## Pont de test

Fait : build de test (`dotnet build -t:Install -p:TestBridge=true`, dans `dist/StsTools-test`) avec un pont qui laisse
Claude piloter le jeu : commandes déposées dans `user://StsTools/test/` par `tools/sts_test.py` (clics par texte,
touches, souris, console, carte, GUI, état du Path finder, captures d'écran). Jamais dans le mod donné aux joueurs
(`dist/StsTools`, build normale).

- Commandes du pont : `combat`, `play`, `endturn`, `cards`, `pickcard` (récompenses, choix) ; les cartes de la
  boutique ne sont pas listées par `cards` (clic par position) ; souris simulée pour le jeu (GetMousePosition) tant
  que la vraie souris ne bouge pas.
- Fait : test réseau réel avec `tools/sts_mp.sh start <dossier>` : deux instances du jeu lancées sans Steam
  (`--force-steam off --fastmp host_standard` / `join --clientId 1001`, multijoueur local ENet du jeu), chacune avec
  ses données isolées (`XDG_DATA_HOME`), pilotées chacune par le pont (`STS_TEST_DIR`). Vérifié : « Draw » de l'hôte
  visible chez le client. Profils neufs : seul l'Ironclad est débloqué.
- Fait : `fakemp <perso> [<perso>…]` lance une partie de test, seul ou à plusieurs (le premier joueur est le nôtre,
  les autres n'agissent pas), comme le « fake multiplayer » du jeu ; toutes les sauvegardes du jeu et du mod sont
  bloquées jusqu'au retour au menu principal. Aussi `aim` (viser sans jouer), `focus`, `overlays`, `graph`. Les coéquipiers n'y jouent pas : pour le réseau, `sts_mp.sh`.
- `click` amène d'abord le contrôle dans la partie visible d'une zone de défilement ; `tree` marque les autres.
- `roompreview <col> <row>` : texte de l'aperçu d'une salle de la carte, sans la survoler ; `encounterimage <ID>` :
  image de ses monstres en PNG.
- Corrigé (2026-11-19) : le blocage des sauvegardes des parties de test ne se posait qu'à moitié depuis f599837
  (Harmony refuse `__result` sur une méthode void) : la partie était protégée, mais pas la progression du profil
  (`progress.save`), l'historique ni les réglages. Vérifié : fichiers identiques après une partie de test avec combat.

## Repères dans le code du jeu (utilisés par le mod)

- `CombatHistory.Add` (privée) : chaque événement d'un combat (carte jouée, piochée, dégâts, Block,
  pouvoirs…), patchée par `CardRunStats`.
- `CardModel.DeckVersion` : relie une carte de combat à sa carte du deck.
- `CombatState.IterateHookListeners` et `RunState.IterateHookListeners` : le jeu exécute chaque effet
  (pouvoir, relique, carte) en parcourant ces listes ; les envelopper dit qui agit à chaque instant.
  Même méthode possible pour les stats des reliques (écouteur `RelicModel`).
- `SaveManager.SaveRun` : moment où le jeu sauvegarde la partie.
- Démarrage : poser trop de patchs pendant l'initialisation bloque parfois le jeu avant le menu (écran noir) ; seuls
  les patchs `[EarlyPatch]` sont posés au démarrage, les autres à l'affichage du menu principal. Encore vu le
  2026-11-19 (1 démarrage sur 5) : figé juste avant « Preloading 'MainMenuEssentials' », sans erreur dans le log,
  insensible à SIGTERM (arrêt forcé nécessaire) ; le détecteur de gel du mod ne tourne pas encore à ce moment-là.
