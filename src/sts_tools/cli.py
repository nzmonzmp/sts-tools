import argparse
import glob
import json
import os
import signal
from collections import Counter, defaultdict
from collections.abc import Callable, Iterator
from dataclasses import dataclass

from sts_tools import pools

STEAM = "~/.local/share/SlayTheSpire2/steam"


def short(id_: str) -> str:
    """CARD.CLAW -> CLAW"""
    return id_.split(".", 1)[-1]


def pct(w: int, n: int) -> str:
    return f"{w / n:.0%}" if n else "-"


@dataclass
class Run:
    data: dict
    me: dict  # entrée de `players` correspondant au joueur local

    @property
    def coop(self) -> bool:
        return len(self.data["players"]) > 1

    @property
    def win(self) -> bool:
        return bool(self.data.get("win"))

    @property
    def abandoned(self) -> bool:
        return bool(self.data.get("was_abandoned"))

    @property
    def character(self) -> str:
        return short(self.me.get("character", "?"))

    @property
    def floor(self) -> int:
        return sum(len(act) for act in self.data.get("map_point_history", []))

    @property
    def act(self) -> int:
        return len(self.data.get("map_point_history", []))

    def stats(self) -> Iterator[dict]:
        """Stats du joueur local pour chaque étage visité"""
        for act in self.data.get("map_point_history", []):
            for point in act:
                for ps in point.get("player_stats", []):
                    if ps.get("player_id") == self.me.get("id"):
                        yield ps


def load_runs(profile: str) -> Iterator[Run]:
    for steam_dir in glob.glob(os.path.join(os.path.expanduser(STEAM), "*")):
        steam_id = int(os.path.basename(steam_dir))
        for f in glob.glob(os.path.join(steam_dir, profile, "saves/history/*.run")):
            with open(f, encoding="utf-8") as fh:
                r = json.load(fh)
            players = r.get("players", [])
            # En solo l'id vaut 1 ; en coop on retrouve son personnage via l'id Steam
            me = players[0] if len(players) == 1 else next((p for p in players if p.get("id") == steam_id), None)
            if me is not None:
                yield Run(r, me)


def ascension_arg(value: str) -> int | None:
    # None signifie toutes les ascensions
    return None if value == "all" else int(value)


def default_ascension(args: argparse.Namespace, coop: bool) -> int | None:
    # Sans -a : A10 en solo ; en coop, défaut propre à la commande (toutes ascensions sauf indication)
    if args.ascension is False:
        return getattr(args, "coop_ascension", None) if coop else 10
    return args.ascension


def ascension_label(asc: int | None) -> str:
    return "toutes ascensions" if asc is None else f"A{asc}"


def selected_runs(args: argparse.Namespace) -> tuple[list[Run], str]:
    """Parties filtrées selon --coop, -a et -c, avec un libellé décrivant le filtre."""
    asc = default_ascension(args, args.coop)
    runs = [
        r for r in load_runs(args.profile)
        if r.coop == args.coop
        and asc in (None, r.data.get("ascension"))
        and (args.character is None or r.character.lower() == args.character.lower())
    ]
    label = ", ".join(filter(None, [
        "Coop" if args.coop else "Solo",
        ascension_label(asc),
        args.character and args.character.upper(),
    ]))
    return runs, f"{label} - {summary(runs)}"


def summary(runs: list[Run]) -> str:
    wins = sum(r.win for r in runs)
    return f"{len(runs)} parties, {wins}W ({pct(wins, len(runs))})"


def column_widths(headers: list[str], rows: list[list]) -> list[int]:
    return [max(len(str(x)) for x in col) for col in zip(headers, *rows)]


def print_table(headers: list[str], rows: list[list], widths: list[int] | None = None) -> None:
    if not rows:
        print("(aucune ligne, essayer un -n plus petit)")
        return
    widths = widths or column_widths(headers, rows)
    fmt = "  ".join(f"{{:<{w}}}" if i == 0 else f"{{:>{w}}}" for i, w in enumerate(widths))
    print(fmt.format(*headers))
    for row in rows:
        print(fmt.format(*row))


class Choices:
    """Par élément (carte, relique) : [victoires, parties] en fin de partie, quand proposé, quand pris."""

    HEADERS = ["Parties", "Win%", "Proposé", "Pick%", "Win% pris", "Win% passé"]

    def __init__(self) -> None:
        self.final: defaultdict[str, list[int]] = defaultdict(lambda: [0, 0])
        self.offered: defaultdict[str, list[int]] = defaultdict(lambda: [0, 0])
        self.picked: defaultdict[str, list[int]] = defaultdict(lambda: [0, 0])

    def add_run(self, run: Run, final: set[str], offered: set[str], picked: set[str]) -> None:
        for stats, items in ((self.final, final), (self.offered, offered), (self.picked, picked)):
            for item in items:
                stats[item][0] += run.win
                stats[item][1] += 1

    def rows(self, min_runs: int, sort: str) -> list[list]:
        rows = []
        for item in set(self.final) | set(self.offered):
            fw, fn = self.final[item]
            ow, on = self.offered[item]
            pw, pn = self.picked[item]
            # Le seuil s'applique à l'échantillon sur lequel porte le tri
            if {"win": fn, "n": max(fn, on), "pick": on}[sort] < min_runs:
                continue
            key = {
                "win": (fw / fn if fn else -1, fn),
                "n": (fn, on),
                "pick": (pn / on if on else -1, on),
            }[sort]
            rows.append((key, [item, fn, pct(fw, fn), on, pct(pn, on), pct(pw, pn), pct(ow - pw, on - pn)]))
        rows.sort(key=lambda r: r[0], reverse=True)
        return [row for _, row in rows]


def winrate(args: argparse.Namespace) -> None:
    stats = {False: (Counter(), Counter()), True: (Counter(), Counter())}
    asc = {coop: default_ascension(args, coop) for coop in stats}
    for r in load_runs(args.profile):
        if asc[r.coop] in (None, r.data.get("ascension")):
            w, l = stats[r.coop]
            (w if r.win else l)[r.character] += 1
    # Largeurs communes aux deux blocs pour aligner les colonnes
    chars = {c for w, l in stats.values() for c in w | l}
    cw = max(map(len, chars), default=0)
    nw = len(str(max((n[c] for w, l in stats.values() for n in (w, l) for c in chars), default=0)))
    for coop, (w, l) in stats.items():
        if coop:
            print()
        print(f"== {'Coop' if coop else 'Solo'} - {ascension_label(asc[coop])} ==")
        if not w and not l:
            print("(aucune partie)")
        for c in sorted(set(w) | set(l)):
            print(f"{c:<{cw}} {w[c]:>{nw}}W {l[c]:>{nw}}L {'(' + pct(w[c], w[c] + l[c]) + ')':>6}")


def print_by_character(
    runs: list[Run], args: argparse.Namespace, column: str,
    choices: Callable[[Run], Iterator[tuple[str, bool]]], final: Callable[[Run], set[str]],
    group: Callable[[str, str], tuple[str, str] | None] | None = None,
) -> None:
    """Un tableau Choices par personnage ; `choices` donne les (élément, pris) proposés pendant la partie.

    `group(élément, personnage)` donne éventuellement (titre du sous-tableau, nom affiché),
    ou None pour masquer l'élément ; les sous-tableaux sont affichés par ordre de titre.
    """
    by_character: defaultdict[str, list[Run]] = defaultdict(list)
    for run in runs:
        by_character[run.character].append(run)
    for character, char_runs in sorted(by_character.items()):
        stats = Choices()
        for run in char_runs:
            offered, picked = set(), set()
            for item, was_picked in choices(run):
                offered.add(item)
                if was_picked:
                    picked.add(item)
            stats.add_run(run, final(run), offered, picked)
        print(f"\n-- {character} - {summary(char_runs)} --")
        rows = stats.rows(args.min, args.sort)
        if group is None:
            print_table([column, *Choices.HEADERS], rows)
            continue
        groups: defaultdict[str, list[list]] = defaultdict(list)
        for row in rows:
            if (g := group(row[0], character)) is None:
                continue
            title, row[0] = g
            groups[title].append(row)
        # Largeurs communes à tous les sous-tableaux du personnage
        widths = column_widths([column, *Choices.HEADERS], [r for rs in groups.values() for r in rs])
        if not groups:
            print("(aucune ligne, essayer un -n plus petit)")
        for i, (title, group_rows) in enumerate(sorted(groups.items())):
            if i:
                print()
            print(f"[{title.split(' ', 1)[1]}]")
            print_table([column, *Choices.HEADERS], group_rows, widths)


def card_choices(run: Run) -> Iterator[tuple[str, bool]]:
    for ps in run.stats():
        for choice in ps.get("card_choices", []):
            yield short(choice["card"]["id"]), bool(choice.get("was_picked"))


def ancient_choices(run: Run) -> Iterator[tuple[str, bool]]:
    for ps in run.stats():
        for choice in ps.get("ancient_choice", []):
            yield choice["TextKey"], bool(choice.get("was_chosen"))


CHARACTERS = {"DEFECT", "IRONCLAD", "NECROBINDER", "REGENT", "SILENT"}
# Le chiffre en tête fixe l'ordre d'affichage des groupes ; il est retiré du titre
POOL_GROUPS = {
    "COLORLESS": "2 Incolores",
    "EVENT": "4 Événements",
    "QUEST": "5 Quêtes",
    "TOKEN": "6 Tokens",
    "STATUS": "7 Statuts",
    "CURSE": "8 Malédictions",
}


def card_group(card: str, character: str) -> tuple[str, str] | None:
    pool = pools.load().get(card)
    if pool is None:
        return None  # carte retirée du jeu (absente de sts2.dll)
    if pool == character:
        return f"1 Cartes {character}", card
    if pool in CHARACTERS:
        return "3 Autres personnages", f"{card} ({pool})"
    return POOL_GROUPS.get(pool, f"9 {pool}"), card


def cards(args: argparse.Namespace) -> None:
    runs, label = selected_runs(args)
    print(f"== Cartes - {label} ==")
    print("Parties : dans le deck final ; Proposé/Pick% : récompenses de cartes ; passé : proposée mais jamais prise")
    if not pools.load():
        print("(pools de cartes inconnus, lancer `sts-tools update-pools` pour les extraire du jeu)")
    print_by_character(
        runs, args, "Carte", card_choices, lambda r: {short(c["id"]) for c in r.me.get("deck", [])},
        group=card_group if pools.load() else None,
    )


def relics(args: argparse.Namespace) -> None:
    runs, label = selected_runs(args)
    print(f"== Reliques - {label} ==")
    print("Parties : possédée en fin de partie ; Proposé/Pick% : choix des Anciens ; passé : proposée mais jamais prise")
    print_by_character(runs, args, "Relique", ancient_choices, lambda r: {short(x["id"]) for x in r.me.get("relics", [])})


def killer(run: Run) -> str:
    d = run.data
    if d.get("killed_by_encounter", "NONE.NONE") != "NONE.NONE":
        return short(d["killed_by_encounter"])
    if d.get("killed_by_event", "NONE.NONE") != "NONE.NONE":
        return "EVENT " + short(d["killed_by_event"])
    return "(abandon hors combat)"


def deaths(args: argparse.Namespace) -> None:
    runs, label = selected_runs(args)
    print(f"== Défaites - {label} ==")
    by_character: defaultdict[str, list[Run]] = defaultdict(list)
    for run in runs:
        by_character[run.character].append(run)
    headers = ["Défaites", "%", "dont abandons"]
    for character, char_runs in sorted(by_character.items()):
        losses = [r for r in char_runs if not r.win]
        print(f"\n-- {character} - {summary(char_runs)} --")
        print(f"{len(losses)} défaites, dont {sum(r.abandoned for r in losses)} abandons")
        by_act: defaultdict[int, list[Run]] = defaultdict(list)
        for run in losses:
            by_act[run.act].append(run)
        for act, act_losses in sorted(by_act.items()):
            by_killer: defaultdict[str, list[int]] = defaultdict(lambda: [0, 0])
            by_floor: defaultdict[int, list[int]] = defaultdict(lambda: [0, 0])
            for run in act_losses:
                for stats, key in ((by_killer, killer(run)), (by_floor, run.floor)):
                    stats[key][0] += 1
                    stats[key][1] += run.abandoned

            def rows(stats: dict, by_count: bool) -> list[list]:
                items = sorted(stats.items(), key=(lambda kv: -kv[1][0]) if by_count else (lambda kv: kv[0]))
                return [[k, n, pct(n, len(losses)), ab] for k, (n, ab) in items if n >= args.min]

            abandons = sum(r.abandoned for r in act_losses)
            print(f"\n[Acte {act} - {len(act_losses)} défaites ({pct(len(act_losses), len(losses))}), dont {abandons} abandons]")
            print_table(["Tué par", *headers], rows(by_killer, True))
            print()
            print_table(["Étage", *headers], rows(by_floor, False))


def update_pools(args: argparse.Namespace) -> None:
    card_pools = pools.update(args.dll)
    print(f"{len(card_pools)} cartes enregistrées dans {pools.POOLS_JSON}")
    for pool, n in sorted(Counter(card_pools.values()).items()):
        print(f"  {pool}: {n}")


def main() -> None:
    parser = argparse.ArgumentParser(prog="sts-tools", description="Outils d'analyse Slay the Spire 2")
    sub = parser.add_subparsers(required=True)

    common = argparse.ArgumentParser(add_help=False)
    common.add_argument(
        "-a", "--ascension", type=ascension_arg, default=False,
        help="niveau d'ascension ou 'all' (défaut : 10 ; all en coop pour winrate et deaths)",
    )
    common.add_argument("-p", "--profile", default="profile1")

    filters = argparse.ArgumentParser(add_help=False, parents=[common])
    filters.add_argument("--coop", action="store_true", help="analyser les parties coop au lieu des parties solo")
    filters.add_argument("-c", "--character", help="filtrer sur un personnage (ex : defect)")
    filters.add_argument(
        "-n", "--min", type=int, default=1,
        help="nombre minimum de parties pour afficher une ligne (défaut : 1)",
    )

    sorting = argparse.ArgumentParser(add_help=False)
    sorting.add_argument(
        "-s", "--sort", choices=["win", "n", "pick"], default="win",
        help="tri : win%% en fin de partie (défaut), nombre de parties, ou pick%%",
    )

    p = sub.add_parser("winrate", parents=[common], help="victoires/défaites par personnage (solo et coop)")
    p.set_defaults(func=winrate)

    p = sub.add_parser("cards", parents=[filters, sorting], help="win%% par carte et taux de pick")
    p.set_defaults(func=cards, coop_ascension=10)

    p = sub.add_parser("relics", parents=[filters, sorting], help="win%% par relique et choix des Anciens")
    p.set_defaults(func=relics, coop_ascension=10)

    p = sub.add_parser("deaths", parents=[filters], help="défaites par ennemi, acte et étage")
    p.set_defaults(func=deaths)

    p = sub.add_parser("update-pools", help="extraire les pools de cartes de sts2.dll (après une mise à jour du jeu)")
    p.add_argument("--dll", default=pools.GAME_DLL, help="chemin de sts2.dll")
    p.set_defaults(func=update_pools)

    args = parser.parse_args()
    # Sortie propre quand la sortie est coupée (ex : `| head`)
    signal.signal(signal.SIGPIPE, signal.SIG_DFL)
    args.func(args)
