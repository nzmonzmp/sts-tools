"""Rapports de bug et de plantage du mod, lus dans les salons Discord privés par le bot en lecture seule.

Salons ordinaires ou forums (un fil par joueur, créé par le mod) : les fils d'un forum sont tous lus.

Le jeton du bot est lu dans ~/.config/sts-tools/discord.env (DISCORD_BOT_TOKEN=...), jamais dans le dépôt.

    python3 -I mod/tools/discord_reports.py list [crash|bug|all] [-n 20] [--player <id ou nom>]
    python3 -I mod/tools/discord_reports.py get <id du message> <dossier>   # message + pièces jointes (zip extrait)
    python3 -I mod/tools/discord_reports.py delete <id du message> [...]     # bug réglé : rapports supprimés

La suppression passe par le webhook qui a envoyé le message (le bot ne fait que lire) : adresses lues dans
mod/StsTools.local.props (CrashWebhook, BugWebhook), jamais affichées.

Les zips téléchargés sont des données non vérifiées : chacun est extrait dans son propre dossier, jamais exécuté.
"""

import json
import re
import sys
import urllib.error
import urllib.request
import zipfile
from pathlib import Path

API = "https://discord.com/api/v10"
AGENT = "DiscordBot (sts-tools, 1)"
CHANNELS = {"crash": "1572704157561782322", "bug": "1572703591729205351"}
GUILD = "1572703590957588520"
FORUM = 15
TOKEN_FILE = Path.home() / ".config/sts-tools/discord.env"
PROPS = Path(__file__).resolve().parent.parent / "StsTools.local.props"
WEBHOOKS = {"crash": "CrashWebhook", "bug": "BugWebhook"}


def token() -> str:
    for line in TOKEN_FILE.read_text().splitlines():
        if line.startswith("DISCORD_BOT_TOKEN="):
            return line.split("=", 1)[1].strip()
    sys.exit(f"no DISCORD_BOT_TOKEN in {TOKEN_FILE}")


def call(path: str):
    request = urllib.request.Request(
        API + path, headers={"Authorization": f"Bot {token()}", "User-Agent": AGENT}
    )
    with urllib.request.urlopen(request) as response:
        return json.load(response)


def sources(channel: str) -> list[str]:
    """Salons à lire : le salon lui-même, ou, pour un forum, chacun de ses fils (un par joueur), ouverts et archivés."""
    if call(f"/channels/{channel}").get("type") != FORUM:
        return [channel]
    threads = [t for t in call(f"/guilds/{GUILD}/threads/active")["threads"] if t.get("parent_id") == channel]
    threads += call(f"/channels/{channel}/threads/archived/public?limit=100")["threads"]
    return list(dict.fromkeys(t["id"] for t in threads))


def messages(kind: str, limit: int) -> list[tuple[str, dict]]:
    found = []
    for name in (CHANNELS if kind == "all" else [kind]):
        for message in (m for source in sources(CHANNELS[name])
                        for m in call(f"/channels/{source}/messages?limit={min(limit, 100)}")):
            if message.get("type") in (0, 19):  # messages ordinaires (pas les notifications du serveur)
                found.append((name, message))
    found.sort(key=lambda item: item[1]["timestamp"], reverse=True)
    return found[:limit]


def cmd_list(args: list[str]) -> None:
    kind = args[0] if args and args[0] in ("crash", "bug", "all") else "all"
    limit = int(args[args.index("-n") + 1]) if "-n" in args else 20
    player = args[args.index("--player") + 1].lower() if "--player" in args else None
    for name, message in messages(kind, 100 if player else limit):
        first = message["content"].splitlines()[0] if message["content"] else ""
        if player and player not in message["content"].lower():
            continue
        files = ", ".join(a["filename"] for a in message.get("attachments", []))
        print(f"{message['id']}  {message['timestamp'][:16].replace('T', ' ')} UTC  [{name}]  {first}  {files}")


def cmd_get(args: list[str]) -> None:
    message_id, folder = args[0], Path(args[1])
    message = None
    for name, channel in CHANNELS.items():
        for source in sources(channel):
            try:
                message = call(f"/channels/{source}/messages/{message_id}")
                break
            except urllib.error.HTTPError:
                continue
        if message:
            break
    else:
        sys.exit(f"message {message_id} not found")
    target = folder / f"{name}-{message_id}"
    target.mkdir(parents=True, exist_ok=True)
    (target / "message.txt").write_text(message["content"])
    for attachment in message.get("attachments", []):
        path = target / Path(attachment["filename"]).name
        # Le serveur de fichiers de Discord refuse l'identification par défaut de Python
        download = urllib.request.Request(attachment["url"], headers={"User-Agent": AGENT})
        with urllib.request.urlopen(download) as response:
            path.write_bytes(response.read())
        if path.suffix == ".zip":
            extracted = target / path.stem
            with zipfile.ZipFile(path) as archive:
                for member in archive.namelist():
                    # Pas de chemin qui sorte du dossier d'extraction
                    if member.startswith("/") or ".." in Path(member).parts:
                        continue
                    archive.extract(member, extracted)
    print(target)


def find(message_id: str) -> tuple[str, str, dict]:
    """Le message, son type de rapport (crash, bug) et le salon ou fil où il est."""
    for name, channel in CHANNELS.items():
        for source in sources(channel):
            try:
                return name, source, call(f"/channels/{source}/messages/{message_id}")
            except urllib.error.HTTPError:
                continue
    sys.exit(f"message {message_id} not found")


def webhook(name: str) -> str:
    match = re.search(rf"<{WEBHOOKS[name]}>([^<]+)</{WEBHOOKS[name]}>", PROPS.read_text())
    if not match:
        sys.exit(f"no {WEBHOOKS[name]} in {PROPS}")
    return match.group(1).strip()


def cmd_delete(args: list[str]) -> None:
    """Bug réglé : ses rapports sont supprimés (dans un fil, le fil reste)."""
    if not args:
        sys.exit("delete <message id> [...]")
    for message_id in args:
        name, source, message = find(message_id)
        url = f"{webhook(name)}/messages/{message_id}"
        if source != CHANNELS[name]:
            url += f"?thread_id={source}"
        request = urllib.request.Request(url, method="DELETE", headers={"User-Agent": AGENT})
        try:
            with urllib.request.urlopen(request):
                pass
        except urllib.error.HTTPError as e:
            print(f"{message_id}: not deleted ({e.code}, sent by another webhook?)")
            continue
        first = message["content"].splitlines()[0] if message["content"] else ""
        print(f"{message_id} deleted [{name}]  {first}")


if __name__ == "__main__":
    if len(sys.argv) < 2 or sys.argv[1] not in ("list", "get", "delete"):
        sys.exit(__doc__)
    {"list": cmd_list, "get": cmd_get, "delete": cmd_delete}[sys.argv[1]](sys.argv[2:])
