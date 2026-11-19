#!/usr/bin/env python3
"""Pilote Slay the Spire 2 à travers le pont de test du mod (build de test : dotnet build -p:TestBridge=true).

    python3 -I mod/tools/sts_test.py <commande> [arguments...]     # « help » pour la liste des commandes
    python3 -I mod/tools/sts_test.py click Max
    python3 -I mod/tools/sts_test.py screenshot carte 0.5          # affiche le chemin du PNG

La commande est déposée dans le dossier de test du jeu, le mod l'exécute et écrit la réponse (première ligne « ok »
ou « error »), affichée ici ; code de sortie 1 si erreur, 2 si le jeu ne répond pas.
"""
import os
import sys
import time
import uuid

TEST_DIR = os.path.expanduser(
    os.environ.get("STS_TEST_DIR", "~/snap/steam/common/.local/share/SlayTheSpire2/StsTools/test"))


def main() -> int:
    line = " ".join(sys.argv[1:]).strip() or "help"
    timeout = float(os.environ.get("STS_TEST_TIMEOUT", "60"))
    if not os.path.isdir(TEST_DIR):
        print(f"no test folder {TEST_DIR}: is the test build installed and the game running?", file=sys.stderr)
        return 2
    cmd_id = f"{time.time_ns()}-{uuid.uuid4().hex[:6]}"
    cmd_path = os.path.join(TEST_DIR, cmd_id + ".cmd")
    out_path = os.path.join(TEST_DIR, cmd_id + ".out")
    tmp_path = cmd_path + ".tmp"
    with open(tmp_path, "w", encoding="utf-8") as f:
        f.write(line)
    os.replace(tmp_path, cmd_path)
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if os.path.exists(out_path):
            with open(out_path, encoding="utf-8") as f:
                status, _, body = f.read().partition("\n")
            os.remove(out_path)
            print(body)
            return 0 if status == "ok" else 1
        time.sleep(0.05)
    if os.path.exists(cmd_path):
        os.remove(cmd_path)
    print(f"no answer after {timeout:.0f} s: is the game running with the test build?", file=sys.stderr)
    return 2


if __name__ == "__main__":
    sys.exit(main())
