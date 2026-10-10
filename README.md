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
| `!setmap <map> [team1 team2]` | `!sm` | admin | Switches the map (and optionally the cultures) and restarts the mission. `current` or `c` keeps the map: `!setmap c v s` (Vlandia vs Sturgia). |
| `!resetmap` | `!rm` | admin | Restarts the mission with the same map and cultures. |
| `!mode [mode] [map]` | | admin | Lists the modes, or switches to one (`skirmish`/`skirm`, `groupfight`/`gf`, `teamdeathmatch`/`tdm`), optionally on a map, and restarts. |
| `!bots [team1 team2]` | | admin | Shows or sets the bots per team, e.g. `!bots 0 10`. Only in modes with bots (`tdm`); lowering removes bots at once. |
| `!warmup` | `!wu` | admin | Back to warmup: cancels an ending warmup, or restarts the mission if the match has started. |
| `!endwarmup [--force]` | `!ew`, `!ewf` (= `--force`) | admin | Ends this mission's warmup with the game's 30 s countdown, or with `--force` the 3 s "battle starting" one. |
| `!kick <player> [--confirm]` | | admin | Kicks the closest-named player after `!confirm` (within 30 s), or at once with `--confirm`. |
| `!confirm` | | admin | Confirms a pending `!kick`. |
| `!tp <player> [to-player]` | | admin | Moves a player (with their horse) to just behind another player: `!tp p1 p2`; `!tp p2` moves you. Both must be alive. |
| `!kill [player]` | | admin | Kills you, or the closest-named player (the same death as staying out of the map's boundary). |
| `!ready` | `!r` | everyone | Skirmish/Battle warmup: your team is ready. When a player of the other team readies within 60 s, warmup ends with the 30 s countdown. A ready expires after 60 s. |
| `!notready` | `!nr` | everyone | Takes back your team's ready. During a countdown started by `!ready`, cancels it and goes back to warmup (players on either team can). |
| `!specs` | | everyone | Lists the spectators. |
| `!maps` | | everyone | Numbered list of the maps for the current mode, one line per map with its variants in brackets: `1. Xauna [default\|rain\|winter]`. A number picks that map's default version (`!setmap 1`). |
| `!help [command]` | | everyone | Lists the commands, one per line with their aliases; `!help <command>` shows its usage and description. |

Other mods' commands are hidden from chat the same way. Mods that register with MPManager (MPBalancer's `!b`,
see below) also get the `/` prefix, aliases, `!help` and `mpm cmd`; others still receive their commands
through their own chat hook.

**Commands from other mods.** A server mod can route its chat commands through MPManager without
referencing it, and still work alone. Copy `docs/MPManagerLink.cs` into the mod (change its namespace), and
where the mod would hook chat, call:

```csharp
if (MPManagerLink.TryRegister(new[] { "b", "mpb" }, "<setting> [value]", "Show or change settings.",
    adminOnly: true, Run))
{
    return; // routed by MPManager: don't hook chat, or commands run twice
}
// ...otherwise hook ChatBox.OnMessageReceivedAtDedicatedServer as before

static void Run(NetworkCommunicator peer, string[] args, Action<string> reply) { ... }
```

`TryRegister` finds `MPManager.ModCommands.Register` among the loaded assemblies by name and calls it; without
MPManager it returns false. `Run` gets the words after the command name; `peer` is null when the command comes
from the server console; `reply` answers the caller privately. MPManager checks admin rights for `adminOnly`
commands and calls `Run` on the main thread, outside packet handling, so it can send network messages
directly. Names taken by MPManager's own commands or another mod are refused (logged). `!help` lists
registered commands with their mod's name, and aliases can target them (`hk = b horse_knee`).

**Matching names.** Maps, cultures and players are typed without spaces and matched loosely: by number
(from `!maps`; not with a leading zero, so `004` is a map name), by id or name, by any unique part
(`lighthouse`, `vla`, `emp`), by the end of an id when several contain it (`008` is `mp_skirmish_map_008`,
not `..._008_skin`), or misspelled (`lighthose`, `vatenborg`). One mistake is allowed per four letters,
compared with the closest part of the name. When it's ambiguous (`fort`), the candidates are listed instead.
Cultures can also be typed as their first letter: `a` Aserai, `b` Battania, `e` Empire, `k` Khuzait,
`s` Sturgia, `v` Vlandia.

**Map names** are defined in `scripts/server/maps.txt` (deployed to `~/bannerlord/maps.txt` and re-read when
it changes, no restart), one line per map: `map id | display name | aliases`. The display name is how chat
shows the map (`Xauna`, spaces allowed); the aliases, separated by spaces, are what `!setmap` and `!mode`
match. Other versions of a map are named `base:variant`: with `mp_skirmish_map_007 | Xauna | xauna` and
`mp_skirmish_map_007_winter | Xauna (Winter) | xauna:winter`, `!setmap xauna` picks the default version and
`!setmap xauna:winter` (or `x:w`) the winter one; an unknown variant lists the map's variants. Maps without a
display name or alias are shown and matched by their id. The game's own display names aren't used.

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
switching to the mode (unless `!mode` names one); cultures and bots come from the mode's option lines. An option
that another mode sets but this one doesn't (groupfight's `SingleSpawn True`) goes back to its value from when
the server started, so nothing carries over from the previous mode. (Options added to mode files only get this
after a restart, which is when the starting values are recorded.)

| Mode | Starts on | Maps | Bots |
|---|---|---|---|
| `teamdeathmatch` (`tdm`) | `mp_tdm_map_001`, Aserai vs Khuzait, 5 bots each | official TDM maps | yes |
| `skirmish` (`skirm`), default | Xauna (`mp_skirmish_map_007`), Vlandia vs Battania | official Skirmish maps and custom maps, without the two groupfight maps | no |
| `groupfight` (`gf`) | `mp_groupfight`, Vlandia vs Vlandia | `mp_groupfight`, `mp_ie_performancearena` | no |

The base config on the server (`~/bannerlord/configs/base-config.txt`, not in this repo; edit it there and
restart) only sets the server name, the passwords, the player cap (20, for
every mode), `DisableInactivityKick True`, the default mode's game type and map, and `start_game`. MPManager doesn't read anything else in `~/bannerlord/configs/`. Everything else comes from the modes.

**Joins and leaves** are announced in chat ("Aesten joined.", "Aesten left.").

**Standby.** Once nobody has been on the server for 60 s, the mission ends and the server waits between
missions with nothing loaded (no bots fighting each other), by holding the intermission countdown. When
someone joins, the next mission starts a second later, in warmup. A fresh server starts in Skirmish warmup and
goes to standby after 60 s without players.

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

Both waits are set in `scripts/server/settings.txt` (`intermission_seconds`, `post_match_seconds`; deployed to
`~/bannerlord/settings.txt` and re-read when it changes, no restart). If players ever crash on map changes,
raise `intermission_seconds` first (e.g. to 5): a slow PC may still be unloading the previous map when the
next one starts. Not seen so far; vanilla waits 10 s and has no setting for it.

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
mpm perf [-n 30] [--players]   # recent performance numbers, or per-player ping/jitter/loss
mpm start | stop | restart | status
```

`mpm cmd` prints what the command logged. `mpm logs` hides routine lines listed in `NOISE` at the top of
`scripts/server/mpm`. `server.log` is screen's raw copy of the console. Both logs are archived to
`~/bannerlord/logs/` (20 of each kept) when the server starts. The server runs detached in screen; nothing
prints to your terminal unless you ask for it.

An option set with `mpm cmd` is also kept for later missions (the console alone only changes the current one).

**Metrics** (operator only, never shown to players): every 10 s MPManager writes a row to
`~/bannerlord/metrics/server-<start>.csv`, with the players and agents on the server, the tick rate actually
reached against the `/tickrate` target, tick-to-tick times (average, median, 99th percentile, worst), the share
of late ticks (over 1.5x the target interval, felt as stutter), CPU in % of one core, memory, .NET heap, full
garbage collections and threads. `players-<start>.csv` has one row per player per 10 s: the game's averaged
ping (min, max, and its standard deviation as jitter) and packet loss. 20 runs of each are kept. Columns are
described in `src/MPManager/Metrics.cs`. Network bandwidth per server can't be told apart from inside one
process on a shared host; under Docker it's per container.

## Test server

Same server as mpbalancer (`ssh aesten@7900x`; set `MPM_HOST` to deploy elsewhere):

```sh
scripts/deploy.sh                # build and install the module and scripts/server/ (run-server.sh, mpm, aliases.txt, maps.txt)
scripts/deploy.sh --restart      # ...then restart the server
scripts/deploy-maps.sh [dirs]    # install custom maps (default: the custom maps named in maps.txt)
```

`deploy-maps.sh` copies maps into the server's `Modules/Multiplayer/SceneObj`, replacing maps of the same
name and keeping the rest. By default it installs the custom maps named in `scripts/server/maps.txt`, from
this PC's game's `Modules/Multiplayer/SceneObj` (where the game keeps maps downloaded from servers); official
maps in maps.txt ship with the game and are skipped. From the server, the map download
helper sends them to players who don't have them. The server only sees new maps after a restart.

`deploy.sh` also installs the server launcher, `~/bannerlord/run-server.sh` (source: `scripts/server/run-server.sh`). It starts the server with MPBalancer (installed by mpbalancer's own `deploy.sh`), plus MPManager whenever `Modules/MPManager` is installed:

```sh
./run-server.sh                  # start in screen session "bannerlord" and attach
./run-server.sh -d               # start detached
./run-server.sh --vanilla        # without MPBalancer, for A/B comparisons
./run-server.sh --no-manager     # without MPManager
./run-server.sh --config x.txt   # config file in ~/bannerlord/configs/, or a path (default: base-config.txt)
./run-server.sh --tickrate 60    # server tick rate in Hz (default: 140)
./run-server.sh --stop           # stop the server and wait for it to exit
```

- It reads the auth token from `token.txt` and `cd`s into `bin/Linux64_Shipping_Server` before starting (that folder's name decides where module DLLs are loaded from).
- The server name gets the start time (Paris time) appended, e.g. `Neko - 22:32`, so a restarted server can be told apart from the stale entry the server list keeps for a while. The server runs from a copy of the config with that name, `Modules/Native/.run-config.txt`, rewritten at each start; edit `~/bannerlord/configs/base-config.txt`, never the copy.
- Output goes to `~/bannerlord/server.log`; logs of earlier runs are moved to `~/bannerlord/logs/server-<time>.log` (20 kept). The server prints its full command line, auth token included, into its logs, so don't share them.
- `--stop` sends SIGTERM: the server ignores SIGHUP, so closing the screen session alone leaves it running.

## Planned

- A per-team player cap with overflow to spectators (6 per team for Skirmish).
