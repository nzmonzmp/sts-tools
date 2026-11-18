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

- Sur la carte, au choix de la salle (survol d'une salle), seulement quand le GUI est ouvert : ce qui peut arriver,
  jamais ce qui va vraiment arriver (pas de lecture de la graine ni des tirages déjà faits) ; fourchettes quand une
  valeur varie :
  - combat normal : les ennemis possibles à cet acte et à cet étage ;
  - « ? » : les issues possibles avec leurs probabilités (événement, combat, marchand, trésor…) ;
  - gains possibles de chaque salle : pour un combat (normal, élite, boss), or (fourchette), nombre de reliques et
    leurs raretés possibles, récompense de cartes (nombre de cartes, raretés possibles), chance de potion ; pareil
    pour les autres salles quand elles donnent quelque chose.

## Rapports de bug et de plantage : spam et doublons

- Éviter le spam : limiter le nombre d'envois (par exemple quelques rapports par heure et par joueur, délai minimal
  entre deux « Report a bug »), et ne pas renvoyer un rapport déjà envoyé.
- Éviter les doublons : reconnaître un plantage déjà signalé (même dernière erreur, même aperçu non terminé, même
  carte ou même combat) et, plutôt qu'un nouveau message avec son zip, le compter sur le rapport existant ou envoyer
  un message court qui renvoie au premier.
- Limiter côté serveur si possible : un webhook dans le mod peut être utilisé par n'importe qui qui le récupère ; un
  petit relais (Cloudflare Worker) pourrait filtrer et limiter les envois.

## Options du jeu : réglages du mod

- Dans les options du jeu, onglet Mod (aujourd'hui : seulement les touches), des réglages du mod :
  - GUI : ce qui s'affiche quand on l'ouvre avec sa touche (onglets visibles, onglet ouvert par défaut, panneau
    replié ou non…) et ce qu'il active (aperçu des cartes et des potions, têtes de mort, aperçu des salles sur la
    carte…) ;
  - rapports de plantage : demander, toujours envoyer ou ne jamais envoyer (aujourd'hui seulement modifiable depuis
    l'encadré du menu principal après un plantage) ;
  - rapports de bug : nom proposé par défaut, joindre les logs et la partie ou non par défaut.

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
