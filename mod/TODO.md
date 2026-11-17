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
(Winged Boots), PV retirés aux ennemis (Fur Coat). Lignes affichées selon le code de la carte ou de la relique, les autres à part.

- Effets pas encore comptés :
  - Valeur des orbes (`ModifyOrbValue` : Focus et reliques du même genre) : le jeu la calcule aussi pour les
    textes, il faut ne compter que les vrais déclenchements.
  - Golden Compass : la carte « chemin doré » qu'il donne pour un acte (`ModifyGeneratedMap`) ; ses salles
    inconnues sans combat sont comptées.
- Multijoueur : dans l'infobulle d'une relique, la part des bonus n'est calculée que pour les reliques du joueur local.

## Stats de combat

Fait : onglet Combat (total du combat, ce tour, par tour) avec les mêmes stats que les cartes, pour
les cartes du joueur, dont les dégâts bonus (Force, debuffs), les dégâts évités grâce à Weak et les soins ;
dégâts subis et bloqués par le joueur.

- Multijoueur : dégâts bonus de la Force et des debuffs donnés aux alliés.
- Onglet Support : énergie, cartes, block, buffs et pioches donnés aux alliés, PV qui leur ont été
  rendus.

## Multijoueur

- Afficher les mêmes stats en temps réel pour les decks des coéquipiers.
- Ajouter les dégâts subis et infligés par chaque joueur.
- Séparer toutes les stats par joueur et afficher les totaux de l'équipe (l'onglet Combat ne compte
  que les cartes du joueur local).
- Fonctionner côté client uniquement : un seul joueur a besoin du mod en coop.

## Repères dans le code du jeu (utilisés par le mod)

- `CombatHistory.Add` (privée) : chaque événement d'un combat (carte jouée, piochée, dégâts, Block,
  pouvoirs…), patchée par `CardRunStats`.
- `CardModel.DeckVersion` : relie une carte de combat à sa carte du deck.
- `CombatState.IterateHookListeners` et `RunState.IterateHookListeners` : le jeu exécute chaque effet
  (pouvoir, relique, carte) en parcourant ces listes ; les envelopper dit qui agit à chaque instant.
  Même méthode possible pour les stats des reliques (écouteur `RelicModel`).
- `SaveManager.SaveRun` : moment où le jeu sauvegarde la partie.
