#!/usr/bin/env python3
"""Interactive console for the running server; started by "mpm console".

Shows MPManager's log (mpmanager.log) as it is written and keeps the prompt, with whatever you are typing,
on the bottom line: new log lines are printed above it instead of over it. Each line you enter is sent to
the server through MPManager's command pipe (console.fifo).

Line editing and history (arrow keys) come from readline; history is kept in .console_history.
Leave with Ctrl-D, Ctrl-C or "exit"; the server keeps running.
"""

import errno
import os
import readline
import sys
import threading
import time

DIR = os.path.dirname(os.path.realpath(__file__))
LOG_FILE = os.path.join(DIR, "mpmanager.log")
COMMAND_FIFO = os.path.join(DIR, "console.fifo")
HISTORY_FILE = os.path.join(DIR, ".console_history")
PROMPT = "> "
BACKLOG_BYTES = 8192  # how much of the existing log to show on start

output_lock = threading.Lock()
stop = threading.Event()


def show(text):
    """Prints a line above the prompt, then redraws the prompt and the line being typed."""
    with output_lock:
        sys.stdout.write("\r\033[K" + text + "\n")
        sys.stdout.flush()
        readline.redisplay()


def follow_log():
    """Shows new log lines as they are written. A new server run starts a new file, which is picked up."""
    log = None
    inode = None
    pending = b""
    first = True
    while not stop.is_set():
        try:
            stat = os.stat(LOG_FILE)
        except FileNotFoundError:
            time.sleep(0.5)
            continue
        if log is None or stat.st_ino != inode or stat.st_size < log.tell():
            if log is not None:
                log.close()
                show("--- server restarted, new log ---")
            log = open(LOG_FILE, "rb")
            inode = stat.st_ino
            pending = b""
            if first and stat.st_size > BACKLOG_BYTES:
                log.seek(stat.st_size - BACKLOG_BYTES)
                log.readline()  # skip the partial first line
            first = False
        chunk = log.read()
        if not chunk:
            time.sleep(0.1)
            continue
        pending += chunk
        *lines, pending = pending.split(b"\n")
        for line in lines:
            show(line.decode("utf-8", "replace").rstrip())


def send(command):
    """Writes one command to the pipe. Opening without a reader fails at once instead of blocking."""
    for _ in range(5):
        try:
            fd = os.open(COMMAND_FIFO, os.O_WRONLY | os.O_NONBLOCK)
        except OSError as e:
            if e.errno == errno.ENXIO:  # no reader right now; MPManager may be reopening the pipe
                time.sleep(0.1)
                continue
            if e.errno == errno.ENOENT:
                break
            raise
        try:
            os.write(fd, (command + "\n").encode())
        finally:
            os.close(fd)
        return
    show("!! The server is not reading commands (not running, or started without MPManager).")


def main():
    if not sys.stdin.isatty():
        sys.exit("mpm console needs a terminal (with ssh, use ssh -t).")
    try:
        readline.read_history_file(HISTORY_FILE)
    except OSError:
        pass
    readline.set_history_length(1000)

    print('mpm console: type commands (list, mpm_lobby, ...). Ctrl-D, Ctrl-C or "exit" to leave; the server keeps running.')
    threading.Thread(target=follow_log, daemon=True).start()
    try:
        while True:
            try:
                line = input(PROMPT).strip()
            except (EOFError, KeyboardInterrupt):
                print()
                break
            # Remove the typed line; the server log echoes the command with a timestamp.
            with output_lock:
                sys.stdout.write("\033[A\r\033[K")
                sys.stdout.flush()
            if not line:
                continue
            if line in ("exit", "quit"):
                break
            send(line)
    finally:
        stop.set()
        try:
            readline.write_history_file(HISTORY_FILE)
        except OSError:
            pass


if __name__ == "__main__":
    main()
