#!/bin/bash
# Test réseau du mod : deux instances du jeu en multijoueur local (ENet, sans Steam), chacune avec ses propres
# données dans <dossier>/host et <dossier>/client : rien n'est lu ni écrit dans les vraies sauvegardes du joueur.
# Le mod (build de test) doit être installé. Chaque instance se pilote avec le pont de test :
#   STS_TEST_DIR=<dossier>/host/SlayTheSpire2/StsTools/test   python3 -I mod/tools/sts_test.py ...
#   STS_TEST_DIR=<dossier>/client/SlayTheSpire2/StsTools/test python3 -I mod/tools/sts_test.py ...
#
#   sts_mp.sh start <dossier> [jeu]   lance l'hôte (salon, partie standard) puis le client, qui le rejoint
#   sts_mp.sh stop                    arrête ces instances (jamais le jeu lancé par Steam)
set -e
GAME="${3:-/mnt/main/SteamLibrary/steamapps/common/Slay the Spire 2}"

stop() {
    pids() { ps -eo pid=,comm=,args= | awk '$2=="SlayTheSpire2" && /--force-steam/ {print $1}'; }
    for p in $(pids); do kill -TERM "$p"; done
    sleep 5
    for p in $(pids); do kill -KILL "$p"; done
}

# Réglages d'un profil neuf : fenêtré côte à côte, mods acceptés (sinon le mod n'est pas chargé), sans intro
settings() { # <fichier> <x>
    mkdir -p "$(dirname "$1")"
    python3 -I - "$1" "$2" <<'PY'
import json, os, sys
path, x = sys.argv[1], int(sys.argv[2])
d = json.load(open(path)) if os.path.exists(path) else {}
d.update(fullscreen=False, window_size={"X": 960, "Y": 540}, window_position={"X": x, "Y": 60},
         seen_ea_disclaimer=True, skip_intro_logo=True, limit_fps_in_background=False,
         mod_settings={"mod_list": [{"id": "StsTools", "is_enabled": True, "source": "mods_directory"}], "mods_enabled": True})
json.dump(d, open(path, "w"))
PY
}

case "$1" in
start)
    DIR="$(realpath -m "${2:?dossier}")"
    # Profils : default/1 pour l'hôte, default/1001 pour le client (son identifiant réseau)
    settings "$DIR/host/SlayTheSpire2/default/1/settings.save" 0
    settings "$DIR/client/SlayTheSpire2/default/1001/settings.save" 960
    cd "$GAME"
    (XDG_DATA_HOME="$DIR/host" setsid ./SlayTheSpire2 --force-steam off --fastmp host_standard >"$DIR/host.out" 2>&1 &)
    sleep 12
    (XDG_DATA_HOME="$DIR/client" setsid ./SlayTheSpire2 --force-steam off --fastmp join --clientId 1001 >"$DIR/client.out" 2>&1 &)
    sleep 20
    echo "host and client started; first run in a new folder: answer the tutorial prompt (No) then confirm the character on both"
    ;;
stop) stop ;;
*) echo "usage: $0 start <folder> [game dir] | stop" >&2; exit 1 ;;
esac
