# À faire

## Stats des cartes (panneau Run stats)

Fait : survol d'une carte dans Deck › Cards (total, par combat, par carte), dégâts bonus et dégâts évités
grâce aux pouvoirs donnés, effets partagés entre les cartes qui renforcent un même pouvoir, soins, énergie
et or gagnés ; Deck › Stats d'après le code des cartes (types, mots-clés, effets, pouvoirs, reliques et
total) ; sauvegarde avec la partie.

- Deck › Stats : les totaux restent des minimums (« 38+ ») pour les cartes sans valeur fixe (valeur calculée
  en combat, coût X, effet sans valeur) ; les reliques sont comptées d'après leurs valeurs, pas leur code.

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
les cartes du joueur.

- Ajouter les dégâts subis et les dégâts bloqués.
- Contribution indirecte : dégâts bonus des débuffs et de la Force donnée aux alliés, dégâts évités
  grâce à Weak, soins, survies de justesse.
- Onglet Support : énergie, cartes, block, buffs et pioches donnés aux alliés, PV qui leur ont été
  rendus.

## Sauvegarde et historique

Fait : les stats des cartes sont sauvegardées avec la partie et restaurées quand on la reprend.

- Sauvegarder aussi les stats du combat en cours (onglet Combat).
- Historique des runs passées : filtres, tri, et récapitulatif rouvrable, decks compris.

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
