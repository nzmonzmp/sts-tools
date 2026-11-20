# STS Tools : suivi du mod

Ce qui reste à faire, puis ce qui est fait, par fonctionnalité. Les protocoles de test et les repères techniques sont
dans [PROTOCOLE.md](PROTOCOLE.md).

## À faire

- Publier la première version dans le salon `announcements` du serveur Discord : dossier `dist/StsTools` (build
  normale) zippé, avec le changelog. Le bot n'a plus les droits d'administrateur : c'est l'auteur qui poste.
- Carte, aperçu des salles : l'or et les combats propres à un événement restent hors de portée (l'événement n'est
  connu qu'en entrant dans la salle).
- Rapports : les salons crash-reports et bug-report sont des salons ordinaires. Pour regrouper les rapports par
  joueur (un fil chacun), les passer en forums et mettre leurs nouveaux webhooks dans les secrets du relais et dans
  `StsTools.local.props`.

## Fait

### Stats des reliques

Sous la description de chaque relique possédée (survol), mises à jour en direct, les mêmes stats que les cartes
(dégâts, Block, énergie, cartes, or, soins, pouvoirs donnés et leurs effets), avec le total, la moyenne par combat, la
moyenne par tour et le combat en cours (part des dégâts et du Block du joueur pour les bonus).

Bonus indirects, pour les cartes comme pour les reliques : Block en plus (Dexterity, Pael's Legion), pioche du début de
tour, énergie (gains augmentés, énergie de chaque tour), or, PV perdus évités (Tungsten Rod), pouvoirs donnés en plus,
cartes rejouées. Effets à la prise d'une relique : PV max, or, cartes ajoutées, retirées, améliorées (deck ou combat),
transformées ou enchantées, potions et emplacements de potion. Aussi : énergie et étoiles économisées sur le coût des
cartes, ennemis étourdis, orbes évoquées, emplacements d'orbe, forge (Sovereign Blade), Block gardé d'un tour à l'autre
(Calipers, Barricade), mort évitée (Lizard Tail, avec ses PV rendus), cartes gardées en main (Runic Pyramid, seulement
celles qui auraient été défaussées), tour en plus, X augmenté (Chemical X), coups et déclenchements d'orbe en plus,
debuffs évités (Artifact), cartes améliorées ou enchantées en entrant dans le deck (œufs, Fresnel Lens), or économisé en
boutique, soins en plus au feu de camp, choix de cartes, récompenses et options de feu de camp en plus, potions
imposées, trésors sautés (Silver Crucible), cartes améliorées ou enchantées en vitrine puis achetées, articles remis en
stock (The Courier), salles inconnues sans combat (Juzu Bracelet, Golden Compass), déplacements libres (Winged Boots),
PV retirés aux ennemis (Fur Coat), valeur des orbes en plus (Focus…), salles du chemin doré (Golden Compass). Lignes
affichées selon le code de la carte ou de la relique, les autres à part.

### Stats de combat

- Onglet Combat (total du combat, ce tour, par tour) avec les mêmes stats que les cartes, pour les cartes du joueur,
  dont les dégâts bonus (Force, debuffs), les dégâts évités grâce à Weak et les soins ; dégâts subis et bloqués.
- Onglet Support (multijoueur seulement) : ce que chaque joueur a donné à ses alliés (énergie, cartes créées, cartes
  piochées par un effet, Block, buffs, PV rendus) par ses cartes, reliques, potions et les pouvoirs qu'il a donnés ;
  combat en cours, partie, moyenne par combat ; sauvegardé avec la partie. Dégâts bonus donnés aux alliés (Vulnerable
  posé sur un ennemi, Force donnée à un allié) et dégâts évités aux alliés (Weak posé sur un ennemi), partagés au
  prorata entre les joueurs qui ont donné le pouvoir. Orbes placées chez un allié (Ignition) et leurs effets. Vérifié
  dans une vraie partie à plusieurs.

### Aperçu des cartes jouées et des potions

GUI ouvert, en visant une créature (ou en entrant dans la zone de jeu pour une carte sans cible), un encadré à côté de
chaque créature concernée : dégâts, part qui passe le Block, coup fatal, étourdi, pouvoirs, Block, PV perdus (épines…),
énergie, pioche, orbes ; dégâts annoncés évités par monstre ; pour le joueur, dégâts évités jusqu'à l'attaque des
ennemis (fin de tour comprise : Plating, Burn, poison…) et bilan si la carte coûte des PV ; en multijoueur, la même
chose pour chaque allié (ennemi tué avant son attaque, Weak, Block donné…), les attaques calculées contre lui. Potions
aussi (au survol de la cible pour celles qui visent, au survol de l'emplacement pour les autres).

Calculé en jouant la carte à blanc avec le code du jeu (`PlaySimulation`) : écouteurs copiés, commandes interceptées,
hasard remis en place ; moyenne si le hasard intervient, fourchette si un choix du joueur intervient, effet combiné des
cartes choisies pendant un écran de choix (Sly, épuisement…).

Aussi : fin de tour séparée (« End of turn: »), cibles au hasard des cartes jouées par un effet (fourchette et
espérance), bouton de fin de tour (tête de mort si terminer le tour tue le joueur, tour supplémentaire de Pael's Eye),
Sandpit, réactions aux pouvoirs donnés (Vicious), tête de mort sur les monstres qui mourront sans rien faire (rouge :
avant d'agir ; violet : après, Doom) et sur ceux que la carte tue à coup sûr. Lignes « Forge +n » (Regent), « Discard »
et « Sly played » (Survivor, Acrobatics…), « Ends your turn » (Void Form).

### GUI

- Déplaçable (en-tête), hauteur réglable (poignée du bas, double-clic : automatique), toujours dans l'écran et sous la
  barre du haut, repliable (flèche, touche Collapse GUI).
- Bouton de la barre du haut (avant la carte) : icône de suivi de stats (barres crème et courbe orange montante,
  contours fins), agrandie autour de son centre au survol.

### Multijoueur

- Totaux par joueur (cartes, dégâts subis et bloqués, Osty, soins), sauvegardés pour chaque joueur ; dans le panneau,
  rangée des joueurs (toi, chaque coéquipier, l'équipe) pour les onglets Deck, Combat et Game ; infobulles des reliques
  des coéquipiers.
- Le mod ne change rien à l'état de la partie et déclare ne pas toucher au gameplay : les autres joueurs n'ont pas
  besoin de l'avoir. Vérifié dans une vraie partie avec un joueur sans aucun mod : pas de désynchronisation, noms des
  joueurs, sauvegarde et reprise.
- Dégâts annoncés par les ennemis pour chaque joueur dans l'onglet Combat (une ligne par joueur pour l'équipe),
  calculés contre ce joueur ; pendant le tour des joueurs, avec la fin de tour simulée.

### Carte : aperçu des salles

GUI ouvert, au survol d'une salle pas encore faite, un encadré dit ce qui peut y arriver, jamais ce qui va vraiment
arriver (rien n'est lu de la graine ni des listes tirées par le jeu en début d'acte) :
- combat, élite, boss : ennemis possibles (rencontres faciles ou difficiles selon le nombre de combats d'ici là, moins
  celles que le sac du jeu ne peut plus tirer), or (fourchette, avec Bowler Hat, Ectoplasm, Amethyst Aubergine,
  Poverty), chance de potion, récompenses de cartes (Prayer Wheel, White Star, Vintage) et rareté, reliques (Black Star,
  Lava Rock) et rareté ;
- « ? » : chance de chaque issue (événement, combat, marchand, trésor, élite) avec les règles du jeu (marchand exclu
  après un marchand ou avant des marchands seulement, Juzu Bracelet, Golden Compass, Lantern Key, Dowsing, première
  partie), puis les ennemis et gains d'un combat et d'un trésor ;
- trésor : or et relique ; feu de camp : soin.

Chances moyennées sur tous les résultats possibles des salles d'ici là ; fourchette selon les chemins ; déplacement
libre (Winged Boots, Flight) pris en compte. Cartes proposées : Lasting Candy, amélioration à partir de l'acte 2,
Molten, Toxic et Frozen Egg, Lava Lamp, Silver Crucible, Glitter, Silken Tress, Fresnel Lens, Wing Charm ; Big Game
Hunter ; Wongo's Mystery Ticket. Petite image fixe des monstres à droite de chaque rencontre (`EncounterVisuals`, même
rendu que l'écran Stats), dans leur animation de départ.

### Carte : chercheur de chemins

Onglet « Path finder » du GUI, seulement quand la carte est ouverte. Classement « Importance » (▲ ▼) des types de salles
(marchands, élites, feux, « ? », combats), appliqué dans l'ordre sur tout l'acte : Max, Min, exactement n ou au moins n
(bouton ≥), respecté strictement (nombre impossible en rouge). Nombres proposés : ceux qui restent possibles une fois
les types au-dessus appliqués. Meilleurs chemins tracés sur la carte (couleur du premier type), tous ensemble (« Show
all ») ou un par un (◀ ▶) ; composition de l'acte en suivant le chemin ; sauts libres marqués d'un trait doré ; au-delà
de 300 chemins à égalité, « N+ ». Bouton « Draw » (multijoueur) : dessine les chemins avec l'outil de dessin du jeu,
visibles des autres joueurs même sans le mod. Choix gardés dans les réglages. Vérifié contre une énumération brute
(76 chemins sans saut, 266 131 avec 3 sauts).

### Écran Stats (menu principal)

Bouton « Stats » sous Compendium : historique des parties (taux de victoire, cartes, reliques, défaites, rencontres avec
l'image des monstres) ; bouton « ? » aligné sur Reload et Close.

### Options du jeu : onglet Mod

Onglet Mod des options (aussi depuis le menu principal), sous-onglets Options et Input (touches du mod, par mode de
contrôle). Lignes copiées de celles du jeu, séparées par ses traits ; gardées dans `settings.json`, appliquées tout de
suite :
- GUI : ouvert tout seul au lancement ou à la reprise d'une partie ; onglets visibles ; onglet et état à l'ouverture ;
- GUI ouvert : aperçu des cartes et des potions, têtes de mort, stats des reliques, aperçu des salles ; Path finder
  caché : chemins plus tracés ;
- rapports de plantage : Ask, Always, Never ; rapports de bug : nom par défaut, logs et partie joints par défaut ;
- boutons « ? » du guide (GUI, Stats, onglet Mod).

### Menu principal

- Recentré : espace libre en hauteur réparti 3/4 au-dessus du logo, 1/8 entre le logo et la liste, 1/8 sous la liste.
- Boutons « Stats » et « Report a bug ».
- Logo Discord dans le coin en haut à droite (le bouton des notes de version et la version du jeu décalés d'un cran à
  gauche) : ouvre l'invitation https://discord.gg/wEB5NVwZPX.

### Guide du mod

Guide plein écran dans le style des options du jeu (pages GUI, Combat, Map, Stats, Settings ; icône du jeu par page ;
flèches de choix ; bouton retour). Ouvert tout seul au premier menu principal avec le mod (`GuideSeen`), puis par les
boutons « ? ». Gauche et droite changent de page, Échap le ferme sans fermer ce qu'il y a derrière.

### Rapports de bug et de plantage

- Journal de session (`user://StsTools/reports/session` : contexte, log du jeu, actions du mod, exceptions, trace de
  l'aperçu en cours, rapport de gel après 10 s sans image). Archivé au lancement suivant si le jeu a planté (pas après
  une fermeture normale ou un SIGTERM, sauf gel), et proposé à l'envoi au menu principal (Send, Always send, Don't send,
  Never ask).
- Bouton « Report a bug » (menu principal, menu pause) : description, nom, journal, log et partie en cours joints.
- Anti-spam (`ReportLimits`, `sent.json`) : 5 rapports de bug par heure, 60 s entre deux, pas deux fois la même
  description en 24 h ; 3 plantages par heure ; un plantage déjà signalé incrémente « Seen again: N times » sur le
  premier message au lieu d'en envoyer un autre.
- Relais (`relay/`, Cloudflare Worker `sts-tools-reports`) entre le mod et Discord : les webhooks ne sont plus dans la
  DLL ; le relais vérifie la forme, limite par joueur et par adresse IP, compte les doublons de tous les joueurs, fait
  un fil par joueur si le salon est un forum. Webhooks recréés depuis (les anciennes adresses étaient dans des DLL déjà
  données).
- Bug réglé : `tools/discord_reports.py delete <id>…` supprime ses rapports.

### Serveur Discord (2026-11-20)

Serveur public, en communauté Discord, invitation permanente https://discord.gg/wEB5NVwZPX, icône
`assets/icon.png` (`tools/make_icon.py`).
- INFO, lecture seule : welcome (règles, installation, overlay Steam, invitation), announcements.
- COMMUNITY : help (message épinglé : problèmes connus), suggestions (forum), discussion (messages système de Discord).
- REPORTS (PRIVATE) : crash-reports, bug-report, mod-updates ; invisibles pour les joueurs, lisibles par le bot.
- Rôle Moderator (messages, exclusions, bannissements, fils, journal d'audit) sans accès aux rapports.
- Vérification moyenne, filtre des médias explicites, notifications sur mentions, AutoMod (spam, mentions en masse).

### Mod donné aux joueurs

- Build normale (`dist/StsTools`) sans aucun outil de test : pont de test, commandes de console du mod (`allrelics`,
  nom d'une relique ou d'une carte pour la gagner) et parties de test (`test_history`) seulement dans la build de test.

### Corrigé

- 2026-11-20 : gel au démarrage avant le menu (1 fois sur 5 environ) : c'est l'overlay Steam, pas le mod. Conseil aux
  joueurs : désactiver l'overlay pour le jeu (Propriétés → Général).
- 2026-11-19, acte 1 joué avec le pont : coup qui tue le dernier ennemi pas compté ; « HP healed » en décimales ; un tour
  de plus compté à la reprise d'une partie ; premier tour de chaque combat pas compté dans « Per turn » ; Path finder :
  nombres sur tout l'acte et classement d'importance pour les nombres choisis.
- 2026-11-19, retours de parties : Kaiser Crab (animations des pinces pendant l'aperçu), Hex (surcouche des cartes
  disparue), Void Form, Forge, cartes qui défaussent, flèches du rapport de bug qui partaient vers le menu pause.
- 2026-11-19 : blocage des sauvegardes des parties de test seulement à moitié posé (progression du profil écrite).
