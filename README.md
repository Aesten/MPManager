# mpManager

A **server-side management module** for the Mount & Blade II: Bannerlord Dedicated Server: stability,
logging and runtime control for custom (listed) servers. It runs on the server only; players don't
need to install anything.

Background on how the vanilla server behaves (console, log spam, what shuts it down, what can change
at runtime) is in `../BannerlordDecompiled/notes/dedicated-server-runtime.md`.

## What it does

**Chat commands.** Messages starting with `!` are never shown in chat; the server handles them and
answers privately, and changes are announced to everyone. Who may run what is set per server in a permissions
file (see **Permissions** below); admin commands need the server's admin password (join with it), and `mpm cmd`
on the server may run everything. In game only `!` works: the game client keeps
every message starting with `/` to itself (its own `/a`, `/t`, `/ab`, `/ac`; anything else is dropped and turns
chat off until `/a` or `/t`). `/` is accepted from `mpm cmd`.

The "Who" column is each command's built-in level, used when the permissions file has no rule for it. The test
server keeps these; production servers open more (mpServerConfig's `config/permissions.txt`).

| Command | Aliases | Who | Effect |
|---|---|---|---|
| `!setmap <map> [team1 team2]` | `!sm` | admin | Switches the map (and optionally the cultures) and restarts the mission. `current` or `c` keeps the map: `!setmap c v s` (Vlandia vs Sturgia). |
| `!resetmap` | `!rm` | admin | Restarts the mission with the same map and cultures. |
| `!mode [mode] [map]` | | admin | Lists the modes, or switches to one (`skirmish`/`skirm`, `groupfight`/`gf`, `groupduel`/`gd`, `teamdeathmatch`/`tdm`), optionally on a map, and restarts. |
| `!bots [team1 team2]` | | admin | Shows or sets the bots per team, e.g. `!bots 0 10`. Only in modes with bots (`tdm`); lowering removes bots at once. |
| `!warmup` | `!wu` | admin | Back to warmup: cancels an ending warmup, or restarts the mission if the match has started. |
| `!endwarmup [--force]` | `!ew`, `!ewf` (= `--force`) | admin | Ends this mission's warmup with the game's 30 s countdown, or with `--force` the 3 s "battle starting" one. |
| `!kick <player> [--confirm]` | `!kc` (= `--confirm`) | admin | Kicks the closest-named player after `!confirm` (within 30 s), or at once with `--confirm`. |
| `!confirm` | | admin | Confirms a pending `!kick`. |
| `!tp <player> [to-player]` | | admin | Moves a player (with their horse) to just behind another player: `!tp p1 p2`; `!tp p2` moves you. Both must be alive. |
| `!kill [player]` | | admin | Kills you, or the closest-named player (the same death as staying out of the map's boundary). |
| `!ready` | `!r` | everyone | Skirmish/Battle warmup: your team is ready. When a player of the other team readies within 60 s, warmup ends with the 30 s countdown. A ready expires after 60 s. |
| `!notready` | `!nr` | everyone | Takes back your team's ready. During a countdown started by `!ready`, cancels it and goes back to warmup (players on either team can). |
| `!specs` | | everyone | Lists the spectators. |
| `!players` | | everyone | Everyone on the server with their player numbers (`#17 Aesten`), which commands take instead of names. |
| `!maps` | | everyone | Numbered list of the maps for the current mode, one line per map with its variants in brackets: `1. Xauna [default\|rain\|winter]`. A number picks that map's default version (`!setmap 1`). |
| `!help [command]` | | everyone | Lists the commands you may use, one per line with their aliases (a command you may only use parts of lists those: `!mpb debug`); `!help <command>` shows its usage and description. |

**Permissions.** `permissions.txt` (`MPM_PERMISSIONS_FILE`, in each instance's `run/`, from mpServerConfig's
`config/` or a profile; re-read when it changes) says who may run which command, down to subcommands:

```
* = admin               # every command without a more specific rule
setmap = everyone       # a command, by its name (or any of a mod command's names: b = mpb)
mpb debug = everyone    # a subcommand: the first words after the command
mpb * = everyone        # * matches any one word ("!b thrust_curve": show a setting)
mpb * * = admin         # ...and two or more ("!b thrust_curve 100": change it)
```

A rule applies to commands starting with its words; when several apply, the most specific wins (word by word, a
named word beats `*`; then the longer rule). Levels: `everyone`, `admin`, `console` (`mpm cmd` only). Aliases are
expanded first, so `!ewf` follows the `endwarmup` rules. Mods can declare default rules for their commands; the
order is: the file's rules naming the command, the mod's defaults, the file's `*` rules, the command's built-in
level. A player who may not run something is told who may (`!b reset is for admins`), and `!help` only lists what
they may use.

Other mods' commands are hidden from chat the same way. Mods that register with MPManager (MPBalancer's `!b`, MPStats' `!stats`,
see below) also get aliases, `!help` and `mpm cmd`; others still receive their commands
through their own chat hook.

**Commands from other mods.** A server mod can route its chat commands through MPManager without
referencing it, and still work alone. Every mod compiles in the same `link/MPManagerLink.cs` from this repo (one
copy, namespace `MPManagerLinking`):

```xml
<MPManagerDir Condition="'$(MPManagerDir)' == ''">$(MSBuildThisFileDirectory)..\..\..\mpManager</MPManagerDir>
...
<Compile Include="$(MPManagerDir)\link\MPManagerLink.cs" Link="MPManagerLink.cs" />
```

(a shared source file rather than a shared DLL: each mod ships its own DLLs, and two mods carrying different
versions of one assembly would clash). Where the mod would hook chat, call:

```csharp
if (MPManagerLink.TryRegister(new[] { "mpb", "b" }, "<setting> [value]", "Show or change settings.",
    adminOnly: true, Run))
{
    // optional default permissions, in permissions.txt's syntax (the server's file overrides them)
    MPManagerLink.TryRegisterPermissions("mpb * = everyone", "mpb * * = admin", "mpb debug = everyone");
    return; // routed by MPManager: don't hook chat, or commands run twice
}
// ...otherwise hook ChatBox.OnMessageReceivedAtDedicatedServer as before

static void Run(NetworkCommunicator peer, string[] args, Action<string> reply) { ... }
```

`TryRegister` finds `MPManager.ModCommands.Register` among the loaded assemblies by name and calls it; without
MPManager it returns false. `Run` gets the words after the command name; `peer` is null when the command comes
from the server console; `reply` answers the caller privately. MPManager checks the caller's rights for the
command and its arguments before calling `Run` (so the mod doesn't check them again; `adminOnly` is the level when
no rule applies), on the main thread, outside packet handling, so it can send network messages
directly. Names taken by MPManager's own commands or another mod are refused (logged). `!help` lists
registered commands with their mod's name, and aliases can target them (`hk = b horse_knee`). A mod with several
commands calls `TryRegister` once per command (again for the same one: nothing happens).

**Player numbers.** Every player gets a number the first time a server sees them, for good (`PlayerNumIds`): one
line per player, appended to `MPM_PLAYER_NUMIDS_FILE` (`player-numids.txt` in the working directory if unset; on
the fleet `~/bannerlord/data/player-numids.txt`, shared by every instance, so a number is the same on every
server). Commands that name a player take the number instead (`!kick 17`, `!tp 17 4`, `#17` works too), `!players`
lists them, and the "matches several players" lists show them. Mods get them with
`MPManagerLink.TryGetNumId(peer, out int numId)` (false without MPManager), e.g. MPGroupDuel's `!invite 17`.

**Matching names.** Maps, cultures and players are typed without spaces and matched loosely: by number
(maps: from `!maps`, not with a leading zero, so `004` is a map name; players: their player number), by id or name, by any unique part
(`lighthouse`, `vla`, `emp`), by the end of an id when several contain it (`008` is `mp_skirmish_map_008`,
not `..._008_skin`), or misspelled (`lighthose`, `vatenborg`). One mistake is allowed per four letters,
compared with the closest part of the name. When it's ambiguous (`fort`), the candidates are listed instead.
Cultures can also be typed as their first letter: `a` Aserai, `b` Battania, `e` Empire, `k` Khuzait,
`s` Sturgia, `v` Vlandia.

**Map names** are defined in `maps.txt` (`config/` in mpServerConfig; re-read when it changes, no restart), one line per map: `map id | display name | aliases`. The display name is how chat
shows the map (`Xauna`, spaces allowed); the aliases, separated by spaces, are what `!setmap` and `!mode`
match. Other versions of a map are named `base:variant`: with `mp_skirmish_map_007 | Xauna | xauna` and
`mp_skirmish_map_007_winter | Xauna (Winter) | xauna:winter`, `!setmap xauna` picks the default version and
`!setmap xauna:winter` (or `x:w`) the winter one; an unknown variant lists the map's variants. Maps without a
display name or alias are shown and matched by their id. The game's own display names aren't used.

**Modes** are defined in `modes/` (`config/modes/` in mpServerConfig), one file per mode (re-read when they change, no restart). The file name is the mode's name. A mode file has settings and option
lines (like a server config) that are applied to the next mission when switching to the mode:

```
aliases = gf                    # other names for !mode
gametype = Battle               # the game's game type
maps = mp_groupfight mp_ie_performancearena
bots = no                       # whether !bots works; otherwise bots are kept at 0
factions = fixed                # start with the CultureTeam options below, not random factions
default = yes                   # the start mode when there's no start setting (only one mode)
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

The server's settings (`settings.txt` in mpServerConfig's `config/`, a profile, and each instance's own; see
[docs/fleet.md](docs/fleet.md)) set its name, passwords, player cap (20, for every mode), inactivity kick, and how
it starts: `start = skirmish xauna vlandia aserai`, or `start = skirmish` for a random map from the mode's pool and
two random factions (groupfight, with `factions = fixed`, keeps its own). They apply at the next start; everything
else comes from the modes.

**Joins and leaves** are announced in chat ("Aesten joined.", "Aesten left.").

**Standby.** Once nobody has been on the server for 60 s, the mission ends and the server waits between
missions with nothing loaded (no bots fighting each other), by holding the intermission countdown. When
someone joins, the next mission starts a second later, in warmup. A fresh server starts in Skirmish warmup and
goes to standby after 60 s without players.

**Aliases** are defined in `aliases.txt` (re-read when it changes, no restart): `sm = setmap` makes `!sm lighthouse` run `!setmap lighthouse`, and an alias
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

Both waits are set in `settings.txt` (`intermission_seconds`, `post_match_seconds`; re-read when it
changes, no restart). If players ever crash on map changes,
raise `intermission_seconds` first (e.g. to 5): a slow PC may still be unloading the previous map when the
next one starts. Not seen so far; vanilla waits 10 s and has no setting for it.

**Logs and server commands.** MPManager writes its own log, `mpmanager.log` in the instance's folder: every line as
it's printed, dated, without the engine's blank lines or the lobby keepalive (four lines every 2 s).
Commands go to a local control socket, `control.sock` in the instance's folder (`~/bannerlord/instances/<id>/`; the server's own console input doesn't
work on Linux). It's a Unix domain socket: only programs on the server, run by the server's user, can connect;
nothing is open to the network. One command per connection: the server runs it on its main thread and
answers with everything it printed meanwhile (a chat command's replies and announcements, or the console's
own output), then closes. `mpm cmd` is the client; by hand:
`echo "/maps" | socat -t 30 - UNIX-CONNECT:$HOME/bannerlord/instances/0/control.sock`. Chat commands run as an admin.

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

Day-to-day tasks for the whole fleet (restarts, settings, permissions, deploys, troubleshooting) are in
[docs/operations.md](docs/operations.md).

On the server, `mpm` (installed as `~/bannerlord/mpm`, linked from `~/.local/bin`) controls the server
instances (0 = the test server, 1-10 production; see [docs/fleet.md](docs/fleet.md)). The first argument picks
the instance: none or `0` is the test server, `3` instance 3, `all` the production instances 1-10 (never 0).

```sh
mpm cmd setmap lighthouse   # a chat command, as an admin: with "/" or no prefix ("!" trips bash's history expansion)
mpm 3 cmd MaxNumberOfPlayers 12   # a server option or console command, on instance 3
mpm all cmd endwarmup       # on every production instance
mpm logs [-f] [-n 100]     # recent log lines without noise, -f to follow; --all for everything
mpm perf [-n 30] [--players]   # recent performance numbers, or per-player ping/jitter/loss
mpm start | stop | status        # instances run as systemd services (bannerlord@<id>): a crash restarts them
mpm restart [--when-empty]       # now, or once nobody is on (nobody gets kicked)
mpm status --json          # MPManager's live state: mode, map, factions, warmup/playing, players
mpm apply                  # rebuild the instance's configuration from mpServerConfig's layers
mpm 3 config               # instance 3's settings and the layer each comes from (--json for programs)
mpm 3 config set max_players 32   # change an instance's own setting (config unset <key> to remove it)
mpm list [--json]          # every instance: running, players, state, port, name, restart pending
mpm create 4 [profile]     # a new instance (instances/4/settings.txt), named Playground #4
```

`mpm cmd` prints the server's answer to the command. `mpmanager.log` leaves out routine game lines that say nothing
about how the server is doing (per-player sync chatter, lobby bookkeeping, startup file loading; the list is
in `src/MPManager/LogFilter.cs`), and `mpm logs` hides a few more (`NOISE` at the top of `scripts/server/mpm`).
`server.log` is screen's raw copy of the console, with everything: it's the only place native crash output
appears. When the server starts, both logs of the previous run are gzipped into the instance's `logs/`
(read them with `zless`/`zgrep`), and logs and metrics older than 30 days are deleted. The server runs detached in screen; nothing
prints to your terminal unless you ask for it.

An option set with `mpm cmd` is also kept for later missions (the console alone only changes the current one).

**Metrics** (operator only, never shown to players): every 10 s MPManager writes a row to
`~/bannerlord/instances/<id>/metrics/server-<start>.csv`, with the players and agents on the server, the tick rate actually
reached against the `/tickrate` target, tick-to-tick times (average, median, 99th percentile, worst), the share
of late ticks (over 1.5x the target interval, felt as stutter), CPU in % of one core, memory, .NET heap, full
garbage collections and threads. `players-<start>.csv` has one row per player per 10 s: the player's platform id
(`2.0.0.<steam id>`, as in MPStats' match records, so the two can be matched), name, the game's averaged ping
(min, max, and its standard deviation as jitter) and packet loss. Metrics go only to these files, never to the
server log. Earlier runs' files are gzipped and kept 30 days. Columns are described in
`src/MPManager/Metrics.cs`. Network bandwidth per server can't be told apart from inside one
process on a shared host; under Docker it's per container.

## Test server

Same server as mpBalancer (`ssh bannerlord@7900x`; set `MPM_HOST` to deploy elsewhere):

```sh
scripts/deploy.sh                # build and install the module and mpm (scripts/server/)
scripts/deploy.sh --restart      # ...then restart the test server (instance 0)
```

The server's configuration lives in the sibling **mpServerConfig** repo and is deployed from there: the launcher
(`~/bannerlord/run-server.sh`: one instance's configuration layers, which mods load, where files go, its port and
CPU core), the fleet defaults in `config/` (`settings.txt`, `aliases.txt`, `maps.txt`, `modes/`),
profiles, and the custom maps (`deploy-maps.sh`). The examples in this
README come from those files; that repo's README has the launcher's options. MPManager finds its files through
the environment variables the launcher sets (`MPM_*`).

## Planned

See [docs/roadmap.md](docs/roadmap.md): performance rules for passive mod code (stats), publishing (working without our
launcher, Windows tooling), and hosting several servers (layout, auto-restart, resource limits,
orchestration, DoS protection).
