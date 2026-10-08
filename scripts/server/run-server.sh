#!/usr/bin/env bash
# Starts the Bannerlord Dedicated Server (Linux) with MPBalancer and MPManager inside a screen session.
# MPManager is added to the module list when it's installed (Modules/MPManager); MPBalancer is required
# unless --vanilla is given.
# Lives next to token.txt on the server (~/bannerlord/); mpManager's scripts/deploy.sh copies it there.
#
# Usage: run-server.sh [-d] [--vanilla] [--no-manager] [--config <file>] [--tickrate <n>]
#        run-server.sh --stop
#   -d, --detach     start the screen session detached (for use over ssh)
#   --vanilla        start without MPBalancer, for A/B comparisons
#   --no-manager     start without MPManager
#   --config <file>  server config, relative to Modules/Native (default: config.txt)
#   --tickrate <n>   server tick rate in Hz (default: 240)
#   --stop           stop a running server and wait for it to exit
#
# Server output is also written to server.log next to this script; logs of earlier runs are
# moved to logs/server-<time>.log.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SELF="$SCRIPT_DIR/$(basename "${BASH_SOURCE[0]}")"
DS_DIR="${DS_DIR:-$HOME/Steam/steamapps/common/Mount & Blade II Dedicated Server}"
SERVER_BIN="$DS_DIR/bin/Linux64_Shipping_Server"
TOKEN_FILE="$SCRIPT_DIR/token.txt"
LOG_FILE="$SCRIPT_DIR/server.log"
# MPManager's own log (dated, filtered, written immediately); mpm console and mpm logs read it.
MPM_LOG_FILE="$SCRIPT_DIR/mpmanager.log"
LOG_ARCHIVE="$SCRIPT_DIR/logs"
# Commands written to this named pipe are run by MPManager (the server's own console input doesn't work on Linux).
COMMAND_FIFO="$SCRIPT_DIR/console.fifo"
SESSION="bannerlord"
PORT=7210
TICKRATE=240

VANILLA=0
MANAGER=1
CONFIG="config.txt"
DETACH=0
INNER=0
STOP=0

usage () {
  sed -n '7,15p' "$SELF" | sed 's/^# \{0,1\}//'
}

while [ $# -gt 0 ]; do
  case "$1" in
    -d|--detach) DETACH=1 ;;
    --vanilla) VANILLA=1 ;;
    --no-manager) MANAGER=0 ;;
    --config) CONFIG="${2:?--config needs a file name}"; shift ;;
    --tickrate) TICKRATE="${2:?--tickrate needs a number}"; shift ;;
    --stop) STOP=1 ;;
    --inner) INNER=1 ;; # internal: running inside the screen session
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown option: $1" >&2; usage >&2; exit 1 ;;
  esac
  shift
done

MANAGER_DLL="$DS_DIR/Modules/MPManager/bin/Linux64_Shipping_Server/MPManager.dll"
MODULES="Native*Multiplayer"
[ "$VANILLA" = 0 ] && MODULES="$MODULES*MPBalancer"
[ "$MANAGER" = 1 ] && [ -f "$MANAGER_DLL" ] && MODULES="$MODULES*MPManager"

session_running () {
  screen -ls "$SESSION" 2>/dev/null | grep -q "[0-9]\.$SESSION[[:space:]]"
}

# Anchored so it doesn't match shells whose command line merely mentions the dll.
SERVER_PATTERN="^dotnet TaleWorlds.Starter.DotNetCore.Linux.dll"

server_process_running () {
  pgrep -f "$SERVER_PATTERN" >/dev/null
}

# The server ignores SIGHUP, so closing the screen session alone leaves it running orphaned.
# It exits promptly on SIGTERM.
if [ "$STOP" = 1 ]; then
  pkill -TERM -f "$SERVER_PATTERN" || true
  for _ in $(seq 1 30); do
    server_process_running || break
    sleep 0.5
  done
  if server_process_running; then
    echo "Server did not exit within 15s of SIGTERM; killing it." >&2
    pkill -KILL -f "$SERVER_PATTERN" || true
    sleep 1
  fi
  if session_running; then
    screen -S "$SESSION" -X quit || true
  fi
  echo "Server stopped."
  exit 0
fi

if [ "$INNER" = 1 ]; then
  TOKEN="$(tr -d '[:space:]' < "$TOKEN_FILE")"
  # The server picks each module's bin/<folder> from its working directory's name,
  # so it has to run from inside Linux64_Shipping_Server.
  cd "$SERVER_BIN"
  export MPM_CONSOLE_FIFO="$COMMAND_FIFO"
  export MPM_LOG_FILE
  export MPM_ALIASES_FILE="$SCRIPT_DIR/aliases.txt"
  exec dotnet TaleWorlds.Starter.DotNetCore.Linux.dll \
    "_MODULES_*${MODULES}*_MODULES_" \
    /dedicatedcustomserverconfigfile "$CONFIG" \
    /tickrate "$TICKRATE" \
    /dedicatedcustomserverauthtoken "$TOKEN" \
    /dedicatedcustomserver "$PORT" USER 0 \
    /playerhosteddedicatedserver
fi

# Check everything before starting screen, so errors show up here instead of in a dead session.
[[ "$TICKRATE" =~ ^[1-9][0-9]*$ ]] || { echo "Invalid tick rate: $TICKRATE (expected a positive integer)" >&2; exit 1; }
[ -s "$TOKEN_FILE" ] || { echo "Missing or empty token file: $TOKEN_FILE" >&2; exit 1; }
[ -f "$SERVER_BIN/TaleWorlds.Starter.DotNetCore.Linux.dll" ] || { echo "Server not found in: $SERVER_BIN" >&2; exit 1; }
[ -f "$DS_DIR/Modules/Native/$CONFIG" ] || { echo "Config not found: $DS_DIR/Modules/Native/$CONFIG" >&2; exit 1; }
if [ "$VANILLA" = 0 ] && [ ! -f "$DS_DIR/Modules/MPBalancer/bin/Linux64_Shipping_Server/MPBalancer.dll" ]; then
  echo "MPBalancer is not installed in $DS_DIR/Modules (run mpbalancer's scripts/deploy.sh, or use --vanilla)." >&2
  exit 1
fi
if [ ! -p "$COMMAND_FIFO" ]; then
  rm -f "$COMMAND_FIFO"
  mkfifo "$COMMAND_FIFO"
fi
if session_running || server_process_running; then
  echo "A server is already running (screen -r $SESSION to attach, or $0 --stop)." >&2
  exit 1
fi

INNER_ARGS=(--inner --config "$CONFIG" --tickrate "$TICKRATE")
[ "$VANILLA" = 1 ] && INNER_ARGS+=(--vanilla)
[ "$MANAGER" = 0 ] && INNER_ARGS+=(--no-manager)

# Keep previous logs (named after their last write time) instead of overwriting them; the 20 newest are kept.
archive_log () {
  local file="$1" prefix="$2"
  if [ -f "$file" ]; then
    mkdir -p "$LOG_ARCHIVE"
    mv "$file" "$LOG_ARCHIVE/$prefix-$(date -r "$file" +%Y%m%d-%H%M%S).log"
    ls -1t "$LOG_ARCHIVE"/"$prefix"-*.log | tail -n +21 | xargs -r rm -f
  fi
}
archive_log "$LOG_FILE" server
archive_log "$MPM_LOG_FILE" mpmanager
# screen flushes its log file only every 10 s by default; flush every second so the log (and mpm console)
# keep up with the server. -c replaces ~/.screenrc for this session only.
SCREENRC="$SCRIPT_DIR/.screenrc"
printf 'logfile flush 1
' > "$SCREENRC"
if [ "$DETACH" = 1 ]; then
  screen -c "$SCREENRC" -L -Logfile "$LOG_FILE" -dmS "$SESSION" bash "$SELF" "${INNER_ARGS[@]}"
  echo "Server starting in screen session '$SESSION' (modules: $MODULES, config: $CONFIG, tickrate: $TICKRATE). Log: $LOG_FILE"
else
  screen -c "$SCREENRC" -L -Logfile "$LOG_FILE" -S "$SESSION" bash "$SELF" "${INNER_ARGS[@]}"
fi
