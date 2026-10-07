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

Console commands (type `list` for all commands, `<command> ?` for a description):

| Command | Effect |
|---|---|
| `mpm_lobby` | Shows the lobby connection state and any reconnect in progress. |
| `mpm_lobby_disconnect` | Drops the lobby connection on purpose, to test reconnecting. |

## Building

```sh
dotnet build src/MPManager -c Release
```

This compiles against the local Dedicated Server install's DLLs (override with `-p:DSDir="..."`)
and writes the module to `dist/Modules/MPManager/`: `SubModule.xml`, plus `MPManager.dll` and
`0Harmony.dll` in both `bin/Linux64_Shipping_Server` and `bin/Win64_Shipping_Server`.

It targets `net6.0`, the runtime the server runs on, because Harmony
([Lib.Harmony](https://github.com/pardeike/Harmony)) has no netstandard2.0 build. `SubModule.xml`
lists `0Harmony.dll` under `<Assemblies>`, so the game loads it from the module folder before
`MPManager.dll`. The module only loads on custom dedicated servers (`DedicatedServerType` `custom`).

## Test server

Same server as mpbalancer (`ssh aesten@7900x`; set `MPM_HOST` to deploy elsewhere):

```sh
scripts/deploy.sh                # build and install the module
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

This build hasn't run on the server yet. To check it:

1. `scripts/deploy.sh --restart` (installs the module and `run-server.sh`, then restarts the server).
2. In the log, look for `[MPManager] Loaded (Harmony ...)`. A `Failed to install patches` line
   means Harmony doesn't work on the server, which needs fixing before anything else.
   The game may also log a failed attempt to load `0Harmony.dll` from the server's own bin folder;
   that's expected as long as the `Loaded` line follows.
3. After `start_game`, `mpm_lobby` should report `RegisteredGame`.
4. Join the server, then run `mpm_lobby_disconnect`. Expected: no shutdown countdown,
   `Reconnecting to the lobby in 5 s`, then `Reconnected to the lobby`. You stay in the game
   throughout, and a second player can join afterwards.

## Planned

- Logging: timestamps, filtering the lobby keepalive lines, a log file.
- Server control commands, from the console and from admin chat: per-mode profiles (Skirmish,
  Battle) with their own map pools, map/mode/faction switching, a per-team player cap with overflow
  to spectators, holding warmup, adding and removing bots, kick and ban.
- A remote control channel and a nicer console client.
- Retrying a `start_game` that failed to register, instead of needing a restart.
