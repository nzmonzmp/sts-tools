# À faire

## Stats du deck en temps réel, pendant une partie

Afficher, pendant une partie en cours, les statistiques des cartes du deck actuel.

- Composition du deck par catégorie (nombre et pourcentage, Block mis en avant), visible dans la vue
  du deck, la pioche et la défausse ; un clic sur la barre de titre l'affiche ou le masque.

## Stats des reliques

- Dans le tooltip de chaque relique, la valeur qu'elle a apportée : dégâts, block, énergie, cartes,
  or, soins.
- Suivre plusieurs valeurs pour les reliques complexes (Snecko Eye, Pen Nib…).
- Découper chaque valeur : total de la run, moyenne par tour, moyenne par combat (ou combat en cours
  puis total).
- Afficher des valeurs réelles, corrigées de Weak, Vulnerable, etc., et les bonus en pourcentage en
  direct.

## Stats de combat

- Suivi en temps réel : dégâts infligés, subis et bloqués, tours joués, cartes jouées, ennemis tués.
- Contribution indirecte : dégâts bonus des débuffs et de la Force donnée aux alliés, dégâts évités
  grâce à Weak, soins, cartes créées, survies de justesse.
- Onglet Support : énergie, cartes, block, buffs et pioches donnés aux alliés, PV qui leur ont été
  rendus.

## Sauvegarde et historique

- Sauvegarder les stats avec la run et les restaurer quand on la reprend.
- Historique des runs passées : filtres, tri, et récapitulatif rouvrable, decks compris.

## Multijoueur

- Afficher les mêmes stats en temps réel pour les decks des coéquipiers.
- Ajouter les dégâts subis et infligés par chaque joueur.
- Séparer toutes les stats par joueur et afficher les totaux de l'équipe.
- Fonctionner côté client uniquement : un seul joueur a besoin du mod en coop.

## Pistes dans le code du jeu (non vérifiées)

- `MegaCrit.Sts2.Core.Modding.ModHelper` : `SubscribeForRunStateHooks` et `SubscribeForCombatStateHooks`,
  pour suivre la partie et les combats en cours.
- `MegaCrit.Sts2.Core.Combat.History` et ses `Entries` : historique d'un combat, peut-être les dégâts.
- L'écran actuel s'ouvre déjà depuis le menu pause (`PauseMenuPatch`) ; un affichage en partie pourrait
  réutiliser `StatsScreen` ou un panneau plus léger.
