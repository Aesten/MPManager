#!/usr/bin/env bash
# Builds MPManager and installs it on the test server, along with everything in scripts/server/ (run-server.sh,
# mpm, aliases.txt) in ~/bannerlord/. mpm is linked as ~/.local/bin/mpm, so it's on the PATH there.
# Custom maps are installed separately, by scripts/deploy-maps.sh.
#
# Usage: scripts/deploy.sh [--no-build] [--restart [-- <run-server.sh options>]]
#   --no-build  copy the existing dist/ output without rebuilding
#   --restart   stop the running server (if any) and start it again, detached;
#               options after -- go to run-server.sh, e.g. --restart -- --tickrate 60
#
# MPM_HOST overrides the ssh target (default: aesten@7900x).

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

HOST="${MPM_HOST:-aesten@7900x}"
# Remote paths, relative to the remote home directory.
REMOTE_DS="Steam/steamapps/common/Mount & Blade II Dedicated Server"
REMOTE_SCRIPTS="bannerlord"

BUILD=1
RESTART=0
SERVER_ARGS=()
while [ $# -gt 0 ]; do
  case "$1" in
    --no-build) BUILD=0 ;;
    --restart) RESTART=1 ;;
    --) shift; SERVER_ARGS=("$@"); break ;;
    *) echo "Unknown option: $1" >&2; exit 1 ;;
  esac
  shift
done

if [ "$BUILD" = 1 ]; then
  dotnet build "$REPO_ROOT/src/MPManager" -c Release
fi

MODULE_DIR="$REPO_ROOT/dist/Modules/MPManager"
[ -f "$MODULE_DIR/bin/Linux64_Shipping_Server/MPManager.dll" ] || { echo "No build output in $MODULE_DIR" >&2; exit 1; }

# tar over ssh rather than scp: it replaces the module folder as a whole (no stale files)
# and sidesteps scp's remote-path quoting, which differs between OpenSSH versions.
echo "Installing module to $HOST:~/$REMOTE_DS/Modules/MPManager"
tar -C "$REPO_ROOT/dist/Modules" -cf - MPManager \
  | ssh "$HOST" "cd \"\$HOME/$REMOTE_DS/Modules\" && rm -rf MPManager && tar -xf -"

for script in "$REPO_ROOT"/scripts/server/*; do
  name="$(basename "$script")"
  echo "Installing $name to $HOST:~/$REMOTE_SCRIPTS/"
  mode=+x
  [[ "$name" == *.txt ]] && mode=-x
  ssh "$HOST" "mkdir -p \"\$HOME/$REMOTE_SCRIPTS\" && cat > \"\$HOME/$REMOTE_SCRIPTS/$name\" && chmod $mode \"\$HOME/$REMOTE_SCRIPTS/$name\"" \
    < "$script"
done

# Remove scripts from earlier versions (console.sh, mpm-console.py, the console history).
ssh "$HOST" "rm -f \"\$HOME/$REMOTE_SCRIPTS/console.sh\" \"\$HOME/$REMOTE_SCRIPTS/mpm-console.py\" \"\$HOME/$REMOTE_SCRIPTS/.console_history\" && mkdir -p \"\$HOME/.local/bin\" && ln -sfn \"\$HOME/$REMOTE_SCRIPTS/mpm\" \"\$HOME/.local/bin/mpm\""

if [ "$RESTART" = 1 ]; then
  # %q-quote the pass-through options so they survive the remote shell.
  QUOTED_ARGS=""
  [ ${#SERVER_ARGS[@]} -gt 0 ] && QUOTED_ARGS="$(printf ' %q' "${SERVER_ARGS[@]}")"
  ssh "$HOST" "\"\$HOME/$REMOTE_SCRIPTS/run-server.sh\" --stop && \"\$HOME/$REMOTE_SCRIPTS/run-server.sh\" -d$QUOTED_ARGS"
fi
