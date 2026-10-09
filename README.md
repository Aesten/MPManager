# mpManager

A **server-side management module** for the Mount & Blade II: Bannerlord Dedicated Server: stability,
logging and runtime control for custom (listed) servers. It runs on the server only; players don't
need to install anything.

Background on how the vanilla server behaves (console, log spam, what shuts it down, what can change
at runtime) is in `../BannerlordDecompiled/notes/dedicated-server-runtime.md`.

## What it does

**Chat commands.** Messages starting with `!` or `/` are never shown in chat; the server handles them and
answers privately, and changes are announced to everyone. Admin commands need the server's admin password
(join with it); the command pipe (`mpm cmd`) counts as an admin.

| Command | Aliases | Who | Effect |
|---|---|---|---|
| `!setmap <map> [team1 team2]` | `!sm` | admin | Switches the map (and optionally the cultures) and restarts the mission. `current` or `c` keeps the map: `!setmap c vla stur`. |
| `!resetmap` | `!rm` | admin | Restarts the mission with the same map and cultures. |
| `!mode [mode] [map]` | | admin | Lists the modes, or switches to one (`skirmish`/`skirm`, `groupfight`/`gf`, `teamdeathmatch`/`tdm`), optionally on a map, and restarts. |
| `!bots [team1 team2]` | | admin | Shows or sets the bots per team, e.g. `!bots 0 10`. Only in modes with bots (`tdm`); lowering removes bots at once. |
| `!warmup` | `!wu` | admin | Back to warmup: cancels an ending warmup, or restarts the mission if the match has started. |
| `!endwarmup [--force]` | `!ew`, `!ewf` (= `--force`) | admin | Ends this mission's warmup with the game's 30 s countdown, or with `--force` the 3 s "battle starting" one. |
| `!kick <player> [--confirm]` | | admin | Kicks the closest-named player after `!confirm` (within 30 s), or at once with `--confirm`. |
| `!confirm` | | admin | Confirms a pending `!kick`. |
| `!ready` | `!r` | everyone | Skirmish/Battle warmup: your team is ready. When a player of the other team readies within 60 s, warmup ends with the 30 s countdown. A ready expires after 60 s. |
| `!notready` | `!nr` | everyone | Takes back your team's ready. During a countdown started by `!ready`, cancels it and goes back to warmup (players on either team can). |
| `!specs` | | everyone | Lists the spectators. |
| `!maps` | | everyone | Numbered list of the maps for the current mode. |
| `!help [command]` | | everyone | Lists the commands, one per line with their aliases; `!help <command>` shows its usage and description. |

Other mods' commands (MPBalancer's `!b`) are hidden from chat the same way and still reach their mod.

**Matching names.** Maps, cultures and players are typed without spaces and matched loosely: by number
(from `!maps`; not with a leading zero, so `004` is a map name), by id or name, by any unique part
(`lighthouse`, `vla`, `emp`), by the end of an id when several contain it (`008` is `mp_skirmish_map_008`,
not `..._008_skin`), or misspelled (`lighthose`, `vatenborg`). One mistake is allowed per four letters,
compared with the closest part of the name. When it's ambiguous (`fort`), the candidates are listed instead.

**Map names** are defined in `scripts/server/maps.txt` (deployed to `~/bannerlord/maps.txt` and re-read when
it changes, no restart): `lighthouse = mp_bnl_lighthouse` names a map, a map can have several names, and
`!maps` shows the first one. Maps without a name are shown and matched by their id. The game's own display
names aren't used.

**Modes** are defined in `scripts/server/modes/`, one file per mode (deployed to `~/bannerlord/modes/` and
re-read when they change, no restart). The file name is the mode's name. A mode file has settings and option
lines (like a server config) that are applied to the next mission when switching to the mode:

```
aliases = gf                    # other names for !mode
gametype = Battle               # the game's game type
maps = mp_groupfight mp_ie_performancearena
bots = no                       # whether !bots works; otherwise bots are kept at 0
default = yes                   # applied when the server starts (only one mode)
RoundTotal 19                   # options, one per line
```

`maps` is the mode's map pool for `!maps` and `!setmap`: map ids, `official` (the game's maps for the game
type: its own list in `Native/ModuleData/Multiplayer/MultiplayerScenes.xml`, then the other scenes in
`Native/SceneObj` with the game type's prefix, such as `mp_skirmish_`, without test scenes), `custom` (the
maps in the server's `Modules/Multiplayer/SceneObj`), and `-id` to leave one out. The first map is used when
switching to the mode, unless the current map is in its pool.

| Mode | Game type | Maps | Bots |
|---|---|---|---|
| `teamdeathmatch` (`tdm`), default | TeamDeathmatch | `mp_tdm_map_001`, then the official TDM maps | yes |
| `skirmish` (`skirm`) | Skirmish | official Skirmish maps and custom maps, without the two groupfight maps | no |
| `groupfight` (`gf`) | Battle | `mp_groupfight`, `mp_ie_performancearena` | no |

The base config on the server (`Modules/Native/config.txt`, not in this repo; a copy is in
`~/bannerlord/configs/base-config.txt`) only sets the server name, the passwords, the default mode's game
type and map, and `start_game`. Everything else comes from the modes.

**Aliases** are defined in `scripts/server/aliases.txt` (deployed to `~/bannerlord/aliases.txt` and re-read
when it changes, no restart): `sm = setmap` makes `!sm lighthouse` run `!setmap lighthouse`, and an alias
can carry arguments (`ewf = endwarmup --force`). The Aliases column above lists the defaults from that file;
update it when the file changes.

**Infinite warmup.** Every mission starts in a warmup that lasts until `!endwarmup`, with its timer
frozen: twice a second the server resends the warmup start time, slightly ahead, so clients keep showing
the full warmup time. If the server fills up, which normally ends warmup, the warmup is put back.

**The same mission again, quickly.** There are no map or culture votes. When a mission ends, the next
one uses the same mode, map and cultures, unless a command changed them. After a mission that ends by
itself, the scoreboard shows for 5 s instead of 15 (clients still count down from 15 and are moved on
early). A mission ended by `!setmap` or `!resetmap` closes at once, with no scoreboard. The intermission
before the next mission lasts 1 s instead of 10; it can't be skipped, because the next mission can only
be started from there. A switch by command takes about a second on the server, plus the map loading on
clients. If the config doesn't turn on the automated rotation, MPManager does, and the server never stops
after a set number of battles.

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
scripts/deploy.sh                # build and install the module and scripts/server/ (run-server.sh, mpm, aliases.txt, maps.txt)
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
./run-server.sh --tickrate 60    # server tick rate in Hz (default: 140)
./run-server.sh --stop           # stop the server and wait for it to exit
```

- It reads the auth token from `token.txt` and `cd`s into `bin/Linux64_Shipping_Server` before starting (that folder's name decides where module DLLs are loaded from).
- Output goes to `~/bannerlord/server.log`; logs of earlier runs are moved to `~/bannerlord/logs/server-<time>.log` (20 kept). The server prints its full command line, auth token included, into its logs, so don't share them.
- `--stop` sends SIGTERM: the server ignores SIGHUP, so closing the screen session alone leaves it running.

## Planned

- A per-team player cap with overflow to spectators (6 per team for Skirmish).
