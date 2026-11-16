# À faire

## Stats des reliques

- Afficher les stats à côté de la description de chaque relique.
- Dans le tooltip de chaque relique, la valeur qu'elle a apportée : dégâts, block, énergie, cartes,
  or, soins.
- Suivre plusieurs valeurs pour les reliques complexes (Snecko Eye, Pen Nib…).
- Découper chaque valeur : total de la run, moyenne par tour, moyenne par combat (ou combat en cours
  puis total).
- Afficher des valeurs réelles, corrigées de Weak, Vulnerable, etc., et les bonus en pourcentage en
  direct.

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
