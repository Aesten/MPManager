#!/usr/bin/env bash
# Starts the Bannerlord Dedicated Server (Linux) with MPBalancer, MPManager and MPStats inside a screen session.
# MPManager and MPStats are added to the module list when they're installed (Modules/MPManager, Modules/MPStats);
# MPBalancer is required unless --vanilla is given.
# Lives next to token.txt on the server (~/bannerlord/); mpManager's scripts/deploy.sh copies it there.
#
# Usage: run-server.sh [-d] [--vanilla] [--no-manager] [--no-stats] [--config <file>] [--tickrate <n>]
#        run-server.sh --stop
#   -d, --detach     start the screen session detached (for use over ssh)
#   --vanilla        start without MPBalancer, for A/B comparisons
#   --no-manager     start without MPManager
#   --no-stats       start without MPStats
#   --config <file>  server config, in ~/bannerlord/configs/ or a path (default: base-config.txt)
#   --tickrate <n>   server tick rate in Hz (default: 140)
#   --stop           stop a running server and wait for it to exit
#
# The config lives in ~/bannerlord/configs/ (base-config.txt: server name, passwords...). At each start it's copied
# to the server's Modules/Native/.run-config.txt with the start time appended to the server name (see RUN_CONFIG
# below); edit the one in configs/, not the copy.
# Server output is also written to server.log next to this script; logs of earlier runs are
# moved to logs/server-<time>.log.gz (kept 30 days, like metrics).

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
# MPManager takes commands on this local socket and answers them (mpm cmd); the server's own console input
# doesn't work on Linux.
CONTROL_SOCKET="$SCRIPT_DIR/control.sock"
SESSION="bannerlord"
PORT=7210
TICKRATE=140

VANILLA=0
MANAGER=1
STATS=1
CONFIG="base-config.txt"
CONFIG_DIR="$SCRIPT_DIR/configs"
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
    --no-stats) STATS=0 ;;
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
STATS_DLL="$DS_DIR/Modules/MPStats/bin/Linux64_Shipping_Server/MPStats.dll"
[ "$STATS" = 1 ] && [ -f "$STATS_DLL" ] && MODULES="$MODULES*MPStats"

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
  export MPM_CONTROL_SOCKET="$CONTROL_SOCKET"
  export MPM_LOG_FILE
  export MPM_ALIASES_FILE="$SCRIPT_DIR/aliases.txt"
  export MPM_MAP_NAMES_FILE="$SCRIPT_DIR/maps.txt"
  export MPM_MODES_DIR="$SCRIPT_DIR/modes"
  export MPM_SETTINGS_FILE="$SCRIPT_DIR/settings.txt"
  # Server and per-player performance numbers, a pair of CSV files per run (see Metrics.cs, mpm perf).
  export MPM_METRICS_DIR="$SCRIPT_DIR/metrics"
  # MPBalancer's hit CSVs go with the metrics (same retention).
  export MPB_DATA_DIR="$SCRIPT_DIR/metrics"
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
# A bare name is a file in configs/; anything with a slash is a path.
[[ "$CONFIG" == */* ]] && CONFIG_FILE="$CONFIG" || CONFIG_FILE="$CONFIG_DIR/$CONFIG"
[ -f "$CONFIG_FILE" ] || { echo "Config not found: $CONFIG_FILE" >&2; exit 1; }
if [ "$VANILLA" = 0 ] && [ ! -f "$DS_DIR/Modules/MPBalancer/bin/Linux64_Shipping_Server/MPBalancer.dll" ]; then
  echo "MPBalancer is not installed in $DS_DIR/Modules (run mpbalancer's scripts/deploy.sh, or use --vanilla)." >&2
  exit 1
fi
rm -f "$SCRIPT_DIR/console.fifo" # the command pipe of earlier versions
if session_running || server_process_running; then
  echo "A server is already running (screen -r $SESSION to attach, or $0 --stop)." >&2
  exit 1
fi

# The server list is slow to drop a stopped server's entry, so a restarted server would show up twice under the
# same name: run from a copy of the config whose ServerName ends with the start time (Paris time), e.g. "Neko - 22:32".
STARTED="- $(TZ=Europe/Paris date +'%H:%M')"
RUN_CONFIG=".run-config.txt"
sed -E "s/^(ServerName[[:space:]]+.*[^[:space:]])[[:space:]]*\$/\1 $STARTED/" \
  "$CONFIG_FILE" > "$DS_DIR/Modules/Native/$RUN_CONFIG"

INNER_ARGS=(--inner --config "$RUN_CONFIG" --tickrate "$TICKRATE")
[ "$VANILLA" = 1 ] && INNER_ARGS+=(--vanilla)
[ "$MANAGER" = 0 ] && INNER_ARGS+=(--no-manager)
[ "$STATS" = 0 ] && INNER_ARGS+=(--no-stats)

# Keep previous logs (named after their last write time) instead of overwriting them, gzipped; logs and metrics
# older than KEEP_DAYS are deleted. Metrics files are named after their run's start; the ones there now are from
# earlier runs (this run's are created once the server is up).
KEEP_DAYS=30
archive_log () {
  local file="$1" prefix="$2"
  if [ -f "$file" ]; then
    mkdir -p "$LOG_ARCHIVE"
    local archived="$LOG_ARCHIVE/$prefix-$(date -r "$file" +%Y%m%d-%H%M%S).log"
    mv "$file" "$archived"
    gzip -f "$archived"
  fi
}
archive_log "$LOG_FILE" server
archive_log "$MPM_LOG_FILE" mpmanager
mkdir -p "$LOG_ARCHIVE" "$SCRIPT_DIR/metrics"
find "$LOG_ARCHIVE" -name '*.log' -exec gzip -f {} +
find "$SCRIPT_DIR/metrics" -name '*.csv' -exec gzip -f {} +
find "$LOG_ARCHIVE" "$SCRIPT_DIR/metrics" -name '*.gz' -mtime +"$KEEP_DAYS" -delete
# screen flushes its log file only every 10 s by default; flush every second so the log (and mpm console)
# keep up with the server. -c replaces ~/.screenrc for this session only.
SCREENRC="$SCRIPT_DIR/.screenrc"
printf 'logfile flush 1
' > "$SCREENRC"
if [ "$DETACH" = 1 ]; then
  screen -c "$SCREENRC" -L -Logfile "$LOG_FILE" -dmS "$SESSION" bash "$SELF" "${INNER_ARGS[@]}"
  echo "Server starting in screen session '$SESSION' (modules: $MODULES, config: $CONFIG_FILE, tickrate: $TICKRATE). Log: $LOG_FILE"
else
  screen -c "$SCREENRC" -L -Logfile "$LOG_FILE" -S "$SESSION" bash "$SELF" "${INNER_ARGS[@]}"
fi
