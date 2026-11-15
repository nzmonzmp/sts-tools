# À faire

## Stats du deck en temps réel, pendant une partie

Afficher, pendant une partie en cours, les statistiques des cartes du deck actuel.

## Multijoueur

- Afficher les mêmes stats en temps réel pour les decks des coéquipiers.
- Ajouter les dégâts subis et infligés par chaque joueur.

## Pistes dans le code du jeu (non vérifiées)

- `MegaCrit.Sts2.Core.Modding.ModHelper` : `SubscribeForRunStateHooks` et `SubscribeForCombatStateHooks`,
  pour suivre la partie et les combats en cours.
- `MegaCrit.Sts2.Core.Combat.History` et ses `Entries` : historique d'un combat, peut-être les dégâts.
- L'écran actuel s'ouvre déjà depuis le menu pause (`PauseMenuPatch`) ; un affichage en partie pourrait
  réutiliser `StatsScreen` ou un panneau plus léger.
