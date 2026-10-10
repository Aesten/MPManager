#!/usr/bin/env bash
# Installs custom multiplayer maps on the test server, in the Dedicated Server's Modules/Multiplayer/SceneObj.
# From there the server's map download helper sends them to players who don't have them yet; a mode offers them
# once its mode file lists them.
#
# Usage: scripts/deploy-maps.sh [map-folder...]
#   With no arguments: the custom maps named in scripts/server/maps.txt, taken from this PC's game install,
#   Modules/Multiplayer/SceneObj (where the game keeps maps downloaded from servers); maps.txt lines for
#   official maps are skipped (they ship with the game). Otherwise the given scene folders (each with a
#   scene.xscene).
#   Maps of the same name on the server are replaced; other maps there are kept. ShaderCache folders (a
#   Windows graphics cache) are left out. The server only sees new maps after a restart.
#
# GAME_FOLDER overrides the game install; MPM_HOST the ssh target (default: aesten@7900x).

set -euo pipefail

GAME_FOLDER="${GAME_FOLDER:-C:/Program Files (x86)/Steam/steamapps/common/Mount & Blade II Bannerlord}"
HOST="${MPM_HOST:-aesten@7900x}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MAP_NAMES="$SCRIPT_DIR/server/maps.txt"
REMOTE_SCENES="Steam/steamapps/common/Mount & Blade II Dedicated Server/Modules/Multiplayer/SceneObj"

if [ $# -eq 0 ]; then
  downloaded="$GAME_FOLDER/Modules/Multiplayer/SceneObj"
  # Map ids: the first column of maps.txt's non-comment lines.
  while read -r id; do
    if [ -d "$downloaded/$id" ]; then
      set -- "$@" "$downloaded/$id"
    elif [ ! -d "$GAME_FOLDER/Modules/Native/SceneObj/$id" ]; then
      echo "Not on this PC: $id (join a server that has it, or give its folder)" >&2
    fi
  done < <(grep -v '^[[:space:]]*#' "$MAP_NAMES" | grep '|' | cut -d'|' -f1 | sed 's/[[:space:]]//g' | grep -v '^$')
fi

maps=()
for dir in "$@"; do
  dir="${dir%/}"
  if [ -f "$dir/scene.xscene" ]; then
    maps+=("$dir")
  else
    echo "Skipping $dir (no scene.xscene)" >&2
  fi
done
[ ${#maps[@]} -gt 0 ] || { echo "No maps to install." >&2; exit 1; }

# One tar stream per parent folder, so maps from different places can be given.
for map in "${maps[@]}"; do
  name="$(basename "$map")"
  echo "Installing $name"
  tar -C "$(dirname "$map")" --exclude=ShaderCache -cf - "$name" \
    | ssh "$HOST" "mkdir -p \"\$HOME/$REMOTE_SCENES\" && cd \"\$HOME/$REMOTE_SCENES\" && rm -rf \"$name\" && tar -xf -"
done
echo "Installed ${#maps[@]} map(s). Restart the server (mpm restart) for it to offer them."
