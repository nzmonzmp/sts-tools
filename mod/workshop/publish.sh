#!/usr/bin/env bash
# Publie le mod sur le Steam Workshop (Steam doit être lancé et connecté).
# Usage : workshop/publish.sh [public|friends|unlisted|private] ["note de version"]
#         workshop/publish.sh subscribe|unsubscribe|status   (abonnement du compte connecté, pour tester)
# La première fois l'item est créé et son id gardé dans workshop/item_id.txt ; ensuite il est mis à jour.
set -euo pipefail
cd "$(dirname "$0")/.."
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}" PATH="$HOME/.dotnet:$PATH"
vis="${1:-public}"
note="${2:-}"

sts_dir=$(sed -n 's|.*<StsDir>\(.*\)</StsDir>.*|\1|p' StsTools.local.props 2>/dev/null || true)
sts_dir="${sts_dir:-$HOME/.local/share/Steam/steamapps/common/Slay the Spire 2}"

dotnet build -c Release -v q -nologo
dotnet build workshop/Workshop.csproj -c Release -v q -nologo -o workshop/bin/out

# libsteam_api.so du jeu
lib="$sts_dir/data_sts2_linuxbsd_x86_64"
export SteamAppId=2868840 SteamGameId=2868840
case "$vis" in
  subscribe|unsubscribe|status) cmd=(workshop/bin/out/Workshop "$vis" workshop/item_id.txt) ;;
  *) cmd=(workshop/bin/out/Workshop dist/StsTools assets/icon.png "STS Tools" workshop/description.txt
  workshop/item_id.txt "$vis" "$note") ;;
esac

# Steam en snap : son client n'accepte que les programmes lancés dans le snap
if [ ! -e "$HOME/.steam/steam.pid" ] && [ -e "$HOME/snap/steam/common/.steam/steam.pid" ]; then
  exec snap run --shell steam -c 'cd "$1" && shift && LD_LIBRARY_PATH="$1" DOTNET_ROOT="$2" SteamAppId=2868840 SteamGameId=2868840 exec "${@:3}"' \
    _ "$PWD" "$lib" "$DOTNET_ROOT" "${cmd[@]}"
fi
LD_LIBRARY_PATH="$lib${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}" exec "${cmd[@]}"
