# Relais des rapports du mod

Petit Cloudflare Worker entre le mod et Discord : le mod n'a plus les adresses des webhooks Discord (qu'on pouvait
lire dans la DLL et utiliser pour poster, modifier ou supprimer n'importe quel message de ces salons). Il envoie ses
rapports ici ; le relais les vérifie puis les transmet.

- **Forme** : type `crash` ou `bug`, message de 2000 caractères au plus, zip de 9,5 Mo au plus, aucune mention.
- **Limites** par heure, par joueur (id Steam) et par adresse IP : plantages 3 et 10, rapports de bug 5 et 15. Elles
  tiennent même si quelqu'un contourne le mod (réponse 429).
- **Doublons entre joueurs** : un plantage déjà signalé (même signature, calculée par le mod) ne crée pas de nouveau
  message ; le premier reçoit « Seen again: N times by M player(s) » (30 jours ; message supprimé : nouveau rapport).
- **Regroupement** : si le salon est un forum, un fil par joueur ; sinon messages ordinaires.

L'adresse du relais reste lisible dans la DLL : on peut encore envoyer de faux rapports, mais au rythme permis, et
plus toucher aux messages existants.

## Déploiement

Il faut Node.js (`sudo snap install node --classic`) et un compte Cloudflare (gratuit).

```sh
cd relay
npx wrangler login                       # ouvre le navigateur, une fois
npx wrangler kv namespace create REPORTS # copier l'id affiché dans wrangler.toml
npx wrangler secret put CRASH_WEBHOOK    # coller l'adresse du webhook crash-reports
npx wrangler secret put BUG_WEBHOOK      # coller l'adresse du webhook bug-report
npx wrangler deploy                      # affiche https://sts-tools-reports.<compte>.workers.dev
```

Puis, dans `mod/StsTools.local.props` : `<ReportRelay>https://sts-tools-reports.<compte>.workers.dev/report</ReportRelay>`.
Avec `ReportRelay`, les webhooks ne sont plus mis dans la DLL (ils restent dans `StsTools.local.props` pour
`mod/tools/discord_reports.py delete`). Si une adresse de webhook a fui dans une ancienne DLL, recréer le webhook dans
Discord et mettre la nouvelle adresse dans les secrets du relais et dans `StsTools.local.props`.

Suivre les envois : `npx wrangler tail`.
