# STS Tools : suivi du mod

Ce qui reste à faire, puis ce qui est fait, par fonctionnalité. Les protocoles de test et les repères techniques sont
dans [PROTOCOLE.md](PROTOCOLE.md).

## À faire

Rien pour l'instant.

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
avant d'agir ; violet : après, Doom) et sur ceux que la carte tue à coup sûr ; tête de mort rouge sur le joueur et sur
Osty quand terminer le tour les tuera (à chaque tirage si le hasard intervient). Têtes de mort sans ZIndex : sous la
main, les écrans de choix et les menus (elles passaient par-dessus les cartes). Lignes « Forge +n » (Regent), « Discard »
et « Sly played » (Survivor, Acrobatics…), « Ends your turn » (Void Form). Cartes créées séparées par type (« Attacks created »,
« Statuses created »…), comme dans les stats des cartes et l'onglet Combat. Réactions aux coups des ennemis qui font
perdre des PV, jouées après leurs attaques (Self-Forming Clay : Block au prochain tour, visible dans End turn et dans
l'effet d'une carte qui évite la perte de PV) ; ce qu'elles font aux ennemis arrive après leur attaque.
Fin de tour : les cartes Ethereal de la main sont épuisées comme dans le jeu (Ascender's Bane…), avant les cartes à
effet de fin de tour (épuisées elles aussi si elles sont Ethereal) : Feel No Pain et les autres réactions à
l'épuisement comptent (Block avant l'attaque des ennemis).

Corrigé : viser la Lagavulin Matriarch endormie fermait le jeu au deuxième aperçu. Le premier arrêtait pour de vrai ses
« Z » (effet libéré, que le champ du monstre remis après la simulation désignait encore) ; les arrêts d'effets visuels
(`Stop` des nœuds `Nodes.Vfx`) sont maintenant bloqués pendant les aperçus, comme les autres animations directes.
Reproduit puis vérifié corrigé en partie de test (rapport de plantage du 2026-11-22).

### GUI

- Déplaçable (en-tête), hauteur réglable (poignée du bas, double-clic : automatique), toujours dans l'écran et sous la
  barre du haut, repliable (flèche, touche Collapse GUI).
- Bouton de la barre du haut (avant la carte) : icône de suivi de stats (barres crème et courbe orange montante,
  contours fins), agrandie autour de son centre au survol.
- Onglet Combat en deux sous-onglets : Stats (le contenu d'avant) et End turn, ce que donnerait la fin du tour
  maintenant, d'après la même simulation que le bouton de fin de tour et les têtes de mort : PV perdus et PV restants
  (mort, mort évitée), Osty, tour supplémentaire, attaque de chaque ennemi au moment d'attaquer (mort avant d'agir, ou
  après), effets de la fin de tour sur chaque créature (Block, Burn, poison, pouvoirs…), moyenne des tirages si le
  hasard intervient (jamais ceux du jeu). Équipe : PV de chaque joueur. Seulement pendant le tour des joueurs.
  Options : « Show the End turn sub-tab (Combat) » (caché, plus de barre de sous-onglets), et nouveau moment
  « When your turn starts » pour changer d'onglet automatiquement. Guide du mod mis à jour.

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
de 300 chemins à égalité, « N+ ». Bouton « Draw » (solo et multijoueur) : dessine les chemins avec l'outil de dessin
du jeu ; ils restent sur la carte GUI fermé et, en multijoueur, les autres joueurs les voient même sans le mod. Choix
gardés dans les réglages. Vérifié contre une énumération brute (76 chemins sans saut, 266 131 avec 3 sauts).

Placement de chaque type du classement, au même rang que son nombre (appliqué juste après) : Early (étage moyen le
plus bas), Late (le plus haut), Grouped (le moins d'étages entre la première et la dernière salle), Spread (le plus
grand écart minimal entre deux salles qui se suivent), sur tout l'acte (salles faites comprises). Un clic sur le choix
le retire ; pas de placement pour les types Any. La recherche garde pour chaque suite de chemin, en plus de sa
composition, ce qu'il faut pour juger les placements choisis (temps noté dans le log au-delà de 200 ms).
Couleurs dans l'onglet : nom de chaque type dans sa couleur (celle du tracé), barre de sa couleur à gauche de ses
rangées « Count » et « Where », choix en surbrillance dans sa couleur (Any en cyan), types Any atténués, noms du tableau
de composition de l'acte colorés (coffres en or).

### Écran Stats (menu principal)

Bouton « Stats » sous Compendium : historique des parties (taux de victoire, cartes, reliques, défaites, rencontres avec
l'image des monstres) ; bouton « ? » aligné sur Reload et Close. Ligne survolée mise en évidence (fond clair), comme dans les tableaux
du GUI (Combat, Support, types de cartes), pour suivre une ligne d'un bout à l'autre.

### Options du jeu : onglet Mod

Onglet Mod des options (aussi depuis le menu principal), sous-onglets Options et Input (touches du mod, par mode de
contrôle). Lignes copiées de celles du jeu, séparées par ses traits ; gardées dans `settings.json`, appliquées tout de
suite :
- GUI : ouvert tout seul au lancement ou à la reprise d'une partie ; onglets visibles ; onglet et état à l'ouverture ;
  onglet choisi tout seul au début d'un combat, à sa fin, chez un marchand (« None » par défaut : rien n'est imposé) ;
- GUI : côté vers lequel il s'élargit quand son contenu est plus large (Left par défaut, bord droit fixe ; Right,
  bord gauche fixe ; None, largeur fixe et défilement horizontal) ;
- GUI ouvert : aperçu des cartes et des potions, têtes de mort, stats des reliques, aperçu des salles ; Path finder
  caché : chemins plus tracés ;
- « Simulations for random effects » : nombre de simulations moyennées quand le hasard intervient (aperçus, têtes de
  mort, dégâts annoncés) : 4, 8, 12 (par défaut), 16 ou 24 ; plus, c'est plus précis mais plus lent ;
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

### Steam Workshop (2026-11-20)

Item public https://steamcommunity.com/sharedfiles/filedetails/?id=3843151947 (id dans `workshop/item_id.txt`), avec
`dist/StsTools`, l'icône `assets/icon.png` et la description `workshop/description.txt`. `workshop/publish.sh
[public|friends|unlisted|private] ["note"]` reconstruit le mod et met l'item à jour par l'API Steam (SteamUGC) du client
connecté, sans identifiants ; Steam en snap : l'outil est lancé dans le snap (`snap run --shell steam`).
Vérifié : abonné à l'item et sans copie dans `mods/`, le jeu charge le mod depuis le dossier du Workshop, sans erreur.
Discord : messages welcome (Install) et help (épinglé) modifiés par le bot : Workshop d'abord, installation à la main
ensuite ; annonce de la 0.1.0 dans announcements (lien du Workshop), publiée aux serveurs qui suivent le salon.
Galerie (2026-11-21) : 7 captures dans `workshop/screenshots` (aperçu en combat, aperçu de salle, stats de relique,
Path finder, écran Stats, aperçu de potion, options), envoyées par `publish.sh` (retire les images déjà publiées).

### Mod donné aux joueurs

- Build normale (`dist/StsTools`) sans aucun outil de test : pont de test, commandes de console du mod (`allrelics`,
  nom d'une relique ou d'une carte pour la gagner) et parties de test (`test_history`) seulement dans la build de test.

### Corrigé

- 2026-11-22 : orbes dans l'aperçu. Évoquer une orbe ne faisait rien (dégâts de Lightning ou de Dark absents) ;
  maintenant évocation (première ou dernière), passif déclenché par une carte, orbe placée qui évoque la première quand
  les places sont prises. Pendant la simulation, la vraie file d'orbes contient les orbes simulées (remise ensuite) :
  Consuming Shadow évoque l'orbe placée par la carte, la fin de tour ne déclenche plus une orbe évoquée.
- 2026-11-22 : pont de test : `fakemp` refuse de démarrer pendant une partie en cours ; son échec laissait les
  sauvegardes de la vraie partie bloquées.
- 2026-11-22 : un ennemi que Doom tue à la fin de son tour attaque quand même. Les attaques des ennemis étaient
  calculées après la fin de leur tour : l'aperçu montrait ses dégâts comme évités, et la tête de mort du joueur ou
  d'Osty pouvait manquer.
- 2026-11-21 : gel au début du tour (Fool avec Simpleton, contre un Wriggler), reproduit puis corrigé. La simulation de
  la fin de tour de l'onglet Combat (Simpleton joue Foolish Shot sur le Wriggler, qui change de coup) mettait à jour
  ses intentions, ce qui comptait comme un changement : redessin de l'onglet, nouvelle simulation… sans fin dans la
  même image (835 redessins par image dans le rapport de gel). Les intentions mises à jour et le redessin du GUI sont
  maintenant ignorés pendant une simulation.
- 2026-11-21 : rapport de gel plus précis : code du mod en cours au moment du gel (patchs et mises à jour, imbriqués),
  dernier fini, nombre d'appels de chacun depuis la dernière image (une boucle s'y voit), tour et camp du combat, action
  du jeu en cours.
- 2026-11-21 : le hasard n'est plus dévoilé. Les simulations (aperçus, fin de tour, têtes de mort, dégâts annoncés)
  utilisaient les vrais tirages à venir du jeu : la tête de mort montrait quel ennemi la carte jouée au hasard par
  Simpleton allait tuer. Elles ont maintenant toujours d'autres tirages que ceux du jeu ; si le hasard intervient en fin
  de tour, plusieurs tirages : tête de mort seulement si la mort arrive à chacun, dégâts moyens sinon.
- 2026-11-21 : aperçu quand la fin de tour sans la carte dépend du hasard (Simpleton, qui joue une carte au hasard
  parmi les moins chères de la main) : chaque tirage était comparé à la moyenne des tirages sans la carte, d'où des
  fourchettes trompeuses (Deep Stretch+ : « Damage prevented −4–12 », un tirage pire que la moyenne n'étant pas la
  faute de la carte). Ces écarts (Damage prevented, Block et pouvoirs de fin de tour, « End of turn: ») ne comptent
  plus que pour la moyenne ; la fourchette ne garde que ce que la carte fait elle-même selon le tirage.
- 2026-11-21 : aperçu de Simpleton+ : « End of turn: Simpleton +0.2 », Block et dégâts en trop. Le code du jeu qui
  parcourt une pile (Simpleton cherche la carte la moins chère de la main) voyait la vraie main, où la carte dont on
  voit l'aperçu est encore : Simpleton pouvait la rejouer en fin de tour. Pendant une simulation, le contenu des piles
  est maintenant celui de la simulation (cartes jouées, défaussées, épuisées retirées ; cartes ajoutées présentes).
- 2026-11-21 : lag au survol des cartes quand la fin de tour dépend du hasard (~100 ms par carte : 12 tirages sans la
  carte, 12 avec). La fin de tour sans la carte est calculée une fois par état du combat (onglet Combat, bouton de fin
  de tour et aperçus la partagent) et chaque aperçu (carte ou potion, cible) est gardé tant que le combat ne change
  pas (PV, Block, pouvoirs, énergie, piles, main, tour, historique, intentions, orbes).
- 2026-11-21 : aperçu des cartes du Fool : les mélodies gagnées (Wah, Cho, Ki jouées pour la première fois ce tour, par
  la carte ou par un effet comme Lost Lute) s'affichent, avec l'icône du jeu (« Melody Wah »…).
- 2026-11-21 : onglet Mod des options en clavier seul (et à la manette) : sous-onglets Options et Input et bouton
  « ? » du guide atteignables (gauche et droite entre eux, haut depuis la première ligne).
- 2026-11-21 : cartes qui invoquent Osty : l'aperçu ne montrait pas les dégâts évités (Osty invoqué ou renforcé
  encaisse les attaques à la place du joueur) ; les PV invoqués sont maintenant donnés à Osty dans la simulation.
- 2026-11-21 : Osty pris en compte dans les dégâts annoncés (onglet Combat, crâne du bouton de fin de tour, aperçu) :
  comme le jeu, après le Block du joueur il encaisse chaque attaque à sa place tant qu'il vit, et le surplus qui le
  tue passe au joueur. Nouvelle ligne « Osty's unblocked incoming damage » (« (dies) » s'il meurt) sous « Unblocked
  incoming damage », et dégâts évités à Osty dans l'aperçu des cartes (encadré à côté de lui).
- 2026-11-21 : cartes qui invoquent Osty (Bodyguard…) : rien dans l'aperçu (maintenant « Summon +N » sur le joueur),
  et leurs stats comptaient « Max HP gained » et « Healed » pour les PV d'Osty (le familier n'est plus compté : c'est
  l'invocation, déjà dans « Summoned »).
- 2026-11-21, rapport de gel d'un joueur (Vine Shambler) : « preview: listener VineShambler » était un reste de la
  prévision de fin de tour, terminée bien avant le gel. Elle est maintenant suivie comme activité du mod (« turn end
  prediction ») et son étape est effacée à la fin. Le gel venait sans doute de Booba-Necrobinder-Mod, qui n'est plus
  compatible avec le jeu (337 MissingMethodException dans la session).
- 2026-11-20 : gel au démarrage avant le menu (1 fois sur 5 environ) : c'est l'overlay Steam, pas le mod. Conseil aux
  joueurs : désactiver l'overlay pour le jeu (Propriétés → Général).
- 2026-11-19, acte 1 joué avec le pont : coup qui tue le dernier ennemi pas compté ; « HP healed » en décimales ; un tour
  de plus compté à la reprise d'une partie ; premier tour de chaque combat pas compté dans « Per turn » ; Path finder :
  nombres sur tout l'acte et classement d'importance pour les nombres choisis.
- 2026-11-19, retours de parties : Kaiser Crab (animations des pinces pendant l'aperçu), Hex (surcouche des cartes
  disparue), Void Form, Forge, cartes qui défaussent, flèches du rapport de bug qui partaient vers le menu pause.
- 2026-11-19 : blocage des sauvegardes des parties de test seulement à moitié posé (progression du profil écrite).
