"""Pools de cartes du jeu (personnage, incolore, malédiction...), extraits de sts2.dll.

Le jeu ne stocke pas le pool d'une carte dans les fichiers de partie : il est défini dans le code,
par les classes `MegaCrit.Sts2.Core.Models.CardPools.*CardPool`. On lit le bytecode de leurs méthodes
pour retrouver les cartes qu'elles référencent, et on enregistre le résultat dans card_pools.json.
"""

import json
import os
import re
import struct
from functools import cache
from importlib.resources import files

GAME_DLL = "~/.local/share/Steam/steamapps/common/Slay the Spire 2/data_sts2_linuxbsd_x86_64/sts2.dll"
POOLS_JSON = files("sts_tools") / "card_pools.json"
CARD_POOLS_NS = "MegaCrit.Sts2.Core.Models.CardPools"
# Pools sans intérêt pour l'analyse (cartes de test, obsolètes)
IGNORED_POOLS = {"DEPRECATED", "DEPRIVED", "MOCK"}


def snake(name: str) -> str:
    """BallLightning -> BALL_LIGHTNING, comme les identifiants des fichiers de partie"""
    return re.sub(r"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", "_", name).upper()


def _uncompress(b: bytes, i: int) -> tuple[int, int]:
    # Entier compressé ECMA-335 (II.23.2)
    x = b[i]
    if x & 0x80 == 0:
        return x, i + 1
    if x & 0xC0 == 0x80:
        return ((x & 0x3F) << 8) | b[i + 1], i + 2
    return ((x & 0x1F) << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3], i + 4


def extract(dll: str = GAME_DLL) -> dict[str, str]:
    """{CARD_ID: POOL} à partir de sts2.dll. POOL vaut IRONCLAD, COLORLESS, CURSE, ..."""
    import dnfile

    pe = dnfile.dnPE(os.path.expanduser(dll))
    t = pe.net.mdtables
    typedefs, typerefs, specs = list(t.TypeDef), list(t.TypeRef), list(t.MethodSpec)

    def type_name(coded: int) -> str | None:
        # TypeDefOrRef coded index : 2 bits de tag, puis l'index
        tag, idx = coded & 3, coded >> 2
        rows = {0: typedefs, 1: typerefs}.get(tag)
        return str(rows[idx - 1].TypeName) if rows and 0 < idx <= len(rows) else None

    def generic_args(idx: int) -> list[str]:
        # Instanciation d'une méthode générique, ex : ModelDb.Card<BallLightning>()
        b = bytes(specs[idx - 1].Instantiation.value)
        if b[0] != 0x0A:
            return []
        n, i = _uncompress(b, 1)
        out = []
        for _ in range(n):
            if b[i] not in (0x11, 0x12):  # VALUETYPE / CLASS
                break
            coded, i = _uncompress(b, i + 1)
            if name := type_name(coded):
                out.append(name)
        return out

    pools: dict[str, str] = {}
    for ty in typedefs:
        if str(ty.TypeNamespace) != CARD_POOLS_NS or not str(ty.TypeName).endswith("CardPool"):
            continue
        pool = str(ty.TypeName).removesuffix("CardPool").upper()
        if pool in IGNORED_POOLS:
            continue
        for mref in ty.MethodList:
            m = mref.row
            if not m.Rva:
                continue
            header = pe.get_data(m.Rva, 12)
            if header[0] & 3 == 2:  # en-tête tiny
                size, header_size = header[0] >> 2, 1
            else:  # en-tête fat
                size, header_size = struct.unpack_from("<I", header, 4)[0], (header[1] >> 4) * 4
            code = pe.get_data(m.Rva + header_size, size)
            # Balayage simple : `call` (0x28) vers une MethodSpec (table 0x2B)
            for i in range(len(code) - 4):
                if code[i] != 0x28:
                    continue
                token = struct.unpack_from("<I", code, i + 1)[0]
                if token >> 24 == 0x2B and 0 < (idx := token & 0xFFFFFF) <= len(specs):
                    for name in generic_args(idx):
                        if name != "CardModel":
                            pools.setdefault(snake(name), pool)
    return dict(sorted(pools.items()))


def update(dll: str = GAME_DLL) -> dict[str, str]:
    pools = extract(dll)
    with open(str(POOLS_JSON), "w", encoding="utf-8") as fh:
        json.dump(pools, fh, indent=1)
        fh.write("\n")
    load.cache_clear()
    return pools


@cache
def load() -> dict[str, str]:
    try:
        return json.loads(POOLS_JSON.read_text(encoding="utf-8"))
    except FileNotFoundError:
        return {}
