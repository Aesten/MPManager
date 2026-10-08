# mpManager

A **server-side management module** for the Mount & Blade II: Bannerlord Dedicated Server: stability,
logging and runtime control for custom (listed) servers. It runs on the server only; players don't
need to install anything.

Background on how the vanilla server behaves (console, log spam, what shuts it down, what can change
at runtime) is in `../BannerlordDecompiled/notes/dedicated-server-runtime.md`.

## What it does

**Chat commands for admins** (players who joined with the server's admin password). Messages starting
with `!` or `/` are never shown in chat; the server handles them and answers privately, and changes are
announced to everyone.

| Command | Effect |
|---|---|
| `!setmap <map> [team1 team2]` | Switches the map (and optionally the cultures) and restarts the mission. |
| `!resetmap` | Restarts the mission with the same map and cultures. |
| `!warmup` | Back to warmup: cancels an ending warmup, or restarts the mission if the match has started. |
| `!endwarmup [--force]` | Ends this mission's warmup with the game's 30 s countdown, or with `--force` the 3 s "battle starting" one. |
| `!kick <player> [--confirm]` | Kicks the closest-named player after `!confirm` (within 30 s), or at once with `--confirm`. |
| `!confirm` | Confirms a pending `!kick`. |
| `!specs` | Lists the spectators. |
| `!maps` | Numbered list of the maps for the current mode. |
| `!help` | Lists the commands and aliases. |

`!specs`, `!maps` and `!help` work for everyone. Other mods' commands (MPBalancer's `!b`) are hidden from
chat the same way and still reach their mod.

**Matching names.** Maps, cultures and players are typed without spaces and matched loosely: by number
(from `!maps`), by id or display name, by any unique part (`lighthouse`, `vla`, `emp`), or misspelled
(`lighthose`, `vatenborg`, `osracinsurection`). One mistake is allowed per four letters, compared with the
closest part of the name. When it's ambiguous (`fort`), the candidates are listed instead. Official maps
also have display names (`Osrac Insurrection` for `mp_battle_map_002`); custom maps only have their scene
folder name. `!maps` lists the mode's official maps, maps added with `add_map_to_usable_maps` /
`add_map_to_automated_battle_pool`, and every custom map in the server's `Modules/Multiplayer/SceneObj`.

**Aliases** are defined in `scripts/server/aliases.txt` (deployed to `~/bannerlord/aliases.txt` and re-read
when it changes, no restart): `sm = setmap` makes `!sm lighthouse` run `!setmap lighthouse`, and an alias
can carry arguments (`ewf = endwarmup --force`).

**Infinite warmup.** Every mission starts in a warmup that lasts until `!endwarmup`, with its timer
frozen: twice a second the server resends the warmup start time, slightly ahead, so clients keep showing
the full warmup time. If the server fills up, which normally ends warmup, the warmup is put back.

**The same mission again, quickly.** There are no map or culture votes. When a mission ends, the next
one uses the same mode, map and cultures, unless a command changed them. The scoreboard after a mission
lasts 5 s instead of 15 (clients still count down from 15 and are moved on early), and the wait before the
next one is 3 s instead of 10. If the config doesn't turn on the automated rotation, MPManager does, and
the server never stops after a set number of battles.

**Logs and server commands.** MPManager writes its own log, `~/bannerlord/mpmanager.log`: every line as
it's printed, dated, without the engine's blank lines or the lobby keepalive (four lines every 2 s).
Commands are sent through a named pipe (`~/bannerlord/console.fifo`), because the server's own console
input doesn't work on Linux. Chat commands work there too, as an admin.

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
extension points and reflection instead, for example by taking the place of ChatBox's chat handlers.

## Controlling the server

On the server, `mpm` (installed as `~/bannerlord/mpm`, linked from `~/.local/bin`):

```sh
mpm cmd /setmap lighthouse  # a chat command, as an admin ("/" avoids bash's "!" history expansion)
mpm cmd MaxNumberOfPlayers 12   # a server option or console command
mpm logs [-f] [-n 100]     # recent log lines without noise, -f to follow; --all for everything
mpm start | stop | restart | status
```

`mpm cmd` prints what the command logged. `mpm logs` hides routine lines listed in `NOISE` at the top of
`scripts/server/mpm`. `server.log` is screen's raw copy of the console. Both logs are archived to
`~/bannerlord/logs/` (20 of each kept) when the server starts. The server runs detached in screen; nothing
prints to your terminal unless you ask for it.

## Test server

Same server as mpbalancer (`ssh aesten@7900x`; set `MPM_HOST` to deploy elsewhere):

```sh
scripts/deploy.sh                # build and install the module and scripts/server/ (run-server.sh, mpm, aliases.txt)
scripts/deploy.sh --restart      # ...then restart the server
scripts/deploy-maps.sh [dirs]    # install custom maps (default: every map this PC's game has downloaded)
```

`deploy-maps.sh` copies maps into the server's `Modules/Multiplayer/SceneObj`, replacing maps of the same
name and keeping the rest. The game keeps maps downloaded from servers in its own
`Modules/Multiplayer/SceneObj`, so by default that's what it installs. From the server, the map download
helper sends them to players who don't have them. The server only sees new maps after a restart.

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

## Planned

- Profiles: hot-swappable presets (bot debugging, Skirmish matches, Battle training) applied with a chat
  command, on top of a base config on the server that holds the name, passwords and `start_game`.
- `!mode` to switch between Skirmish and Battle, with each mode's map pool.
- A per-team player cap with overflow to spectators (6 per team for Skirmish).
- Adding and removing bots.
