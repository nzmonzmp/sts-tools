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

- Multijoueur : dégâts bonus de la Force et des debuffs donnés aux alliés.
- Onglet Support : énergie, cartes, block, buffs et pioches donnés aux alliés, PV qui leur ont été
  rendus.

## Aperçu des cartes jouées

Fait : GUI ouvert, en visant une créature (ou en entrant dans la zone de jeu pour une carte sans cible), un encadré
à côté de chaque créature concernée : dégâts, part qui passe le Block, coup fatal, étourdi, pouvoirs, Block, PV
perdus (épines…), énergie, pioche, orbes ; dégâts annoncés évités par monstre ; pour le joueur, dégâts évités
jusqu'à l'attaque des ennemis (fin de tour comprise : Plating, Burn, poison…) et bilan si la carte coûte des PV.
Calculé en jouant la carte à blanc avec le code du jeu (PlaySimulation) : écouteurs copiés, commandes
interceptées, hasard remis en place ; moyenne si le hasard intervient, fourchette si un choix du joueur
intervient, effet combiné des cartes choisies pendant un écran de choix (Sly, épuisement…).

- Les réactions des monstres eux-mêmes (MonsterModel) ne sont pas simulées.
- Un objet nul déréférencé par le code du jeu ferme le jeu sans message : les commandes interceptées doivent
  toujours renvoyer un objet utilisable (créatures détachées, résultats vides).

## Multijoueur

Fait : totaux par joueur (cartes, dégâts subis et bloqués, Osty, soins), sauvegardés pour chaque joueur ; dans
le panneau, rangée des joueurs (toi, chaque coéquipier, l'équipe) pour les onglets Deck, Combat et Game ;
infobulles des reliques des coéquipiers. Le mod ne change rien à l'état de la partie (outils de test bloqués en
multijoueur) et déclare ne pas toucher au gameplay : les autres joueurs n'ont pas besoin de l'avoir.

- À vérifier dans une vraie partie à plusieurs : rejoindre sans que l'autre ait le mod, pas de désynchronisation,
  noms des joueurs, sauvegarde et reprise.
- Dégâts annoncés par les ennemis pour chaque joueur (aujourd'hui : le joueur local seulement).

## Repères dans le code du jeu (utilisés par le mod)

- `CombatHistory.Add` (privée) : chaque événement d'un combat (carte jouée, piochée, dégâts, Block,
  pouvoirs…), patchée par `CardRunStats`.
- `CardModel.DeckVersion` : relie une carte de combat à sa carte du deck.
- `CombatState.IterateHookListeners` et `RunState.IterateHookListeners` : le jeu exécute chaque effet
  (pouvoir, relique, carte) en parcourant ces listes ; les envelopper dit qui agit à chaque instant.
  Même méthode possible pour les stats des reliques (écouteur `RelicModel`).
- `SaveManager.SaveRun` : moment où le jeu sauvegarde la partie.
- Démarrage : poser trop de patchs pendant l'initialisation bloque parfois le jeu avant le menu (écran noir) ; seuls
  les patchs `[EarlyPatch]` sont posés au démarrage, les autres à l'affichage du menu principal.
