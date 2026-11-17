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
d'orbe, forge (Sovereign Blade). Lignes affichées selon le code de la carte ou de la relique, les autres à part.

- Énergie max ajoutée une fois pour toutes à la prise d'une relique (sans passer par les écouteurs).
- Effets pas encore comptés (écouteurs du jeu redéfinis par des reliques ou des pouvoirs) :
  - Block gardé d'un tour à l'autre (`ShouldClearBlock` : Calipers, Barricade) ; cartes gardées en main
    (`ShouldFlush` : Runic Pyramid) ; mort évitée (`ShouldDieLate` : Lizard Tail).
  - Cartes modifiées en entrant dans le deck (`ModifyCardBeingAddedToDeck` : les œufs) ; X augmenté
    (`ModifyXValue` : Chemical X) ; coups en plus (`ModifyAttackHitCount`) ; valeur des orbes
    (`ModifyOrbValue`) et déclenchements en plus (`ModifyOrbPassiveTriggerCounts`) ; tour en plus
    (`ShouldTakeExtraTurn`) ; debuffs évités (`TryModifyPowerAmountReceived` : Artifact).
  - Hors combat : or économisé en boutique (`ModifyMerchantPrice` : Membership Card), soins en plus au feu de
    camp (`ModifyRestSiteHealAmount`), choix de cartes en plus (`TryModifyCardRewardOptions…`,
    `ModifyCardRewardCreationOptions`), récompenses en plus (`TryModifyRewards…`, `ShouldForcePotionReward`),
    options de feu de camp (`TryModifyRestSiteOptions`), trésors (`ShouldGenerateTreasure`).
- Multijoueur : dans l'onglet Combat, la part des bonus n'est calculée que pour les reliques du joueur local.

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
