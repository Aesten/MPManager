# mpManager

A **server-side management module** for the Mount & Blade II: Bannerlord Dedicated Server: stability,
logging and runtime control for custom (listed) servers. It runs on the server only; players don't
need to install anything.

Background on how the vanilla server behaves (console, log spam, what shuts it down, what can change
at runtime) is in `../BannerlordDecompiled/notes/dedicated-server-runtime.md`.

## What it does

**Survives losing the lobby connection.** Vanilla, the server shuts itself down 20 s after its
connection to TaleWorlds' lobby drops, which happens whenever a keepalive request fails 5 retries,
even mid-match. Once a game has been registered, MPManager skips that shutdown and reconnects in the
background (5 s, then doubling up to 60 s between attempts). It then registers the game again with
the current game type, map and player cap. Players in the game keep playing; only new joins wait
for the reconnect. If the automated rotation tried to start a battle during the outage, that battle
is started once the server is registered again. Before the first registration (`start_game`), the
vanilla shutdown still applies.

**A working console.** On the Linux server, lines typed into the server's own console are echoed but
never executed. MPManager reads commands from a named pipe instead (`~/bannerlord/console.fifo`, created
by `run-server.sh`) and runs them exactly like console input, so options, the game's commands and the
`mpm_*` commands all work. See [Controlling the server](#controlling-the-server).

**Less log spam.** The lobby keepalive (four lines every 2 s, which was about a third of the log) is
filtered out. Lobby retries and failures still show.

MPManager's console commands (`list` shows all commands, `<command> ?` describes one):

| Command | Effect |
|---|---|
| `mpm_lobby` | Shows the lobby connection state and any reconnect in progress. |
| `mpm_lobby_disconnect` | Drops the lobby connection on purpose, to test reconnecting. |

## Building

```sh
dotnet build src/MPManager -c Release
```

This compiles against the local Dedicated Server install's DLLs (override with `-p:DSDir="..."`)
and writes the module to `dist/Modules/MPManager/`: `SubModule.xml`, plus `MPManager.dll` in both
`bin/Linux64_Shipping_Server` and `bin/Win64_Shipping_Server`. It targets `net6.0`, the runtime the
server runs on. The module only loads on custom dedicated servers (`DedicatedServerType` `custom`).

**No Harmony.** Harmony 2.3.6 crashed the Linux server on its first patch: its MonoMod backend
segfaults while detecting the platform (`DynDll.OpenLibrary` in `PlatformDetection`), and the
engine's signal handler takes the process down. So MPManager changes game behaviour through public
extension points and reflection instead, for example by swapping in its own lobby session handler.

## Controlling the server

On the server, `mpm` (installed as `~/bannerlord/mpm`, linked from `~/.local/bin`):

```sh
mpm cmd <command>        # send a console command and print what it logged, e.g. mpm cmd mpm_lobby
mpm logs [-f] [-n 100]   # recent log lines without noise, -f to follow; --all for everything
mpm start | stop | restart | status
```

Commands go through `~/bannerlord/console.fifo` (`echo "<command>" > ~/bannerlord/console.fifo` works
too). Logs come from `~/bannerlord/mpmanager.log`, which MPManager writes as each line is printed: dated,
no blank lines, no keepalive noise. `mpm logs` also hides routine lines listed in `NOISE` at the top of
`scripts/server/mpm`. `server.log` is screen's raw copy of the console. Both logs are archived to
`~/bannerlord/logs/` when the server starts. The server runs detached in screen; nothing prints to your
terminal unless you ask for it.

## Test server

Same server as mpbalancer (`ssh aesten@7900x`; set `MPM_HOST` to deploy elsewhere):

```sh
scripts/deploy.sh                # build and install the module and the server scripts (run-server.sh, mpm)
scripts/deploy.sh --restart      # ...then restart the server
```

`deploy.sh` also installs the server launcher, `~/bannerlord/run-server.sh` (source: `scripts/server/run-server.sh`). It starts the server with MPBalancer (installed by mpbalancer's own `deploy.sh`), plus MPManager whenever `Modules/MPManager` is installed:

```sh
./run-server.sh                  # start in screen session "bannerlord" and attach
./run-server.sh -d               # start detached
./run-server.sh --vanilla        # without MPBalancer, for A/B comparisons
./run-server.sh --no-manager     # without MPManager
./run-server.sh --config x.txt   # config file in Modules/Native (default: config.txt)
./run-server.sh --tickrate 60    # server tick rate in Hz (default: 240)
./run-server.sh --stop           # stop the server and wait for it to exit
```

- It reads the auth token from `token.txt` and `cd`s into `bin/Linux64_Shipping_Server` before starting (that folder's name decides where module DLLs are loaded from).
- Output goes to `~/bannerlord/server.log`; logs of earlier runs are moved to `~/bannerlord/logs/server-<time>.log` (20 kept). The server prints its full command line, auth token included, into its logs, so don't share them.
- `--stop` sends SIGTERM: the server ignores SIGHUP, so closing the screen session alone leaves it running.

## First test

To check a build:

1. `scripts/deploy.sh --restart` (installs the module and `run-server.sh`, then restarts the server).
2. In the log, look for `[MPManager] Loaded.`, then `[MPManager] Watching the lobby connection.`
3. After `start_game`: `[MPManager] Game registered with the lobby...`, and `mpm_lobby` reports
   `RegisteredGame`.
4. Join the server, then run `mpm cmd mpm_lobby_disconnect` on the server. Expected: no shutdown countdown,
   `Reconnecting to the lobby in 5 s`, then `Reconnected to the lobby`. You stay in the game
   throughout, and a second player can join afterwards.

Steps 1-3, and step 4 without a player connected, passed on 2026-10-07: the server re-registered
about 6 s after the disconnect. Staying in the game, and joining afterwards, still need a player.

## Planned

- Server control commands, from the console and from admin chat: per-mode profiles (Skirmish,
  Battle) with their own map pools, map/mode/faction switching, a per-team player cap with overflow
  to spectators, holding warmup, adding and removing bots, kick and ban.
- A remote control channel and a nicer console client.
- Retrying a `start_game` that failed to register, instead of needing a restart.
