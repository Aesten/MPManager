#!/usr/bin/env bash
# Installs custom multiplayer maps on the test server, in the Dedicated Server's Modules/Multiplayer/SceneObj.
# From there the server's map download helper sends them to players who don't have them yet, and MPManager's
# !maps / !setmap list them.
#
# Usage: scripts/deploy-maps.sh [map-folder...]
#   With no arguments: every map in this PC's game install, Modules/Multiplayer/SceneObj, which is where the
#   game keeps maps downloaded from servers. Otherwise the given scene folders (each with a scene.xscene).
#   Maps of the same name on the server are replaced; other maps there are kept. ShaderCache folders (a
#   Windows graphics cache) are left out. The server only sees new maps after a restart.
#
# GAME_FOLDER overrides the game install; MPM_HOST the ssh target (default: aesten@7900x).

set -euo pipefail

GAME_FOLDER="${GAME_FOLDER:-C:/Program Files (x86)/Steam/steamapps/common/Mount & Blade II Bannerlord}"
HOST="${MPM_HOST:-aesten@7900x}"
REMOTE_SCENES="Steam/steamapps/common/Mount & Blade II Dedicated Server/Modules/Multiplayer/SceneObj"

if [ $# -eq 0 ]; then
  set -- "$GAME_FOLDER/Modules/Multiplayer/SceneObj"/*/
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
