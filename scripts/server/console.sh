#!/usr/bin/env bash
# Interactive console for the running server: shows the server log as it's written (blank lines removed)
# and sends each line you type to the server through MPManager's command pipe.
# Line editing and history (arrow keys) work; history is kept in .console_history.
# Leave with Ctrl-D or "exit"; the server keeps running.
#
# Lives in ~/bannerlord/ next to run-server.sh; mpManager's scripts/deploy.sh copies it there.

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
LOG_FILE="$SCRIPT_DIR/server.log"
COMMAND_FIFO="$SCRIPT_DIR/console.fifo"
HISTFILE="$SCRIPT_DIR/.console_history"

# The engine ends every line with \r\r\n plus an empty line; strip both. sed -u keeps it unbuffered.
tail -n 30 -F "$LOG_FILE" 2>/dev/null | sed -u -e 's/\r//g' -e '/^[[:space:]]*$/d' &
TAIL_PID=$!
trap 'kill $TAIL_PID 2>/dev/null' EXIT

history -r "$HISTFILE" 2>/dev/null
echo "Console attached. Type commands (list, mpm_lobby, ...), Ctrl-D or 'exit' to leave."
while IFS= read -e -r line; do
  [ "$line" = exit ] && break
  [ -z "${line// }" ] && continue
  history -s "$line"
  history -a "$HISTFILE"
  # Writing blocks until the server reads the pipe; give up if it isn't running.
  if ! timeout 3 bash -c 'printf "%s\n" "$1" > "$2"' _ "$line" "$COMMAND_FIFO"; then
    echo "!! Server is not reading commands (not running, or started without MPManager)."
  fi
done
