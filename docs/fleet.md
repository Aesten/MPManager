# Server fleet: layout, configuration and tooling

How several Bannerlord dedicated servers run on one machine (bannerlord@7900x) and how they're configured and
controlled. Decided on 14 October 2026; parts marked *planned* aren't built yet.

## Instances

An **instance** is one dedicated server process. Instances have fixed numeric ids:

| Id | Use | Game port | CPU core | CPU threads |
|---|---|---|---|---|
| – | OS, steamcmd, log compression, tools | – | 0 (CCD1) | 0, 12 |
| 1, 3, 5, 7, 9 | production | 7211, 7213... | 1–5 (CCD1) | id 1: 1, 13; 3: 2, 14; ... 9: 5, 17 |
| 0 | testing (balance experiments, live `!b` changes) | 7210 | 6 (CCD2) | 6, 18 |
| 2, 4, 6, 8, 10 | production | 7212, 7214... | 7–11 (CCD2) | id 2: 7, 19; ... 10: 11, 23 |

- The id is internal and never changes; it decides the folder, port, CPU core, screen session or service name and
  control socket. What players see (server name, passwords, modes...) is configuration and can change any time.
- **At most 10 production instances.** Each server uses about 1.2–1.6 threads at 140 Hz during a match (0.2 in
  standby) and 0.5–0.9 GB of memory. Each instance is pinned to both threads of its own physical core (on the
  7900X, core N is threads N and N+12), so no two servers share a core; core 0 is left to everything else. Odd
  ids are on CCD1 (cores 1-5, with the OS on 0), even ids on CCD2 (cores 6-11), so the fleet is split evenly
  across the two dies.
  Tooling only accepts ids 0–10. The tick rate stays 140.
- Pinning is done with `taskset` in `run-server.sh`: systemd user services can't set `AllowedCPUs` here (the user
  cgroup only delegates memory and pids), and there's no root.

## Folders on the server

Everything lives under `~/bannerlord/` (no root access, so not `/srv`). The game install is shared:
`~/Steam/steamapps/common/Mount & Blade II Dedicated Server/`, with our mods in its `Modules/`.

```
~/bannerlord/
  run-server.sh, mpm, token.txt     shared scripts and the lobby token
  config/                           fleet defaults: settings.txt, aliases.txt, maps.txt, modes/
  profiles/<name>/                  a group of instances: only the files that differ from config/
  instances/<id>/
    settings.txt                    this instance's settings: its name, profile, anything it changes
    config/                         optional: whole files only this instance has (same layout as config/)
    run/                            the effective configuration, generated (don't edit)
    control.sock                    MPManager's command socket
    server.log, mpmanager.log       this run's logs; logs/ has earlier runs, gzipped
    metrics/, stats/                MPManager metrics and MPBalancer hits; MPStats match records
    restart-pending                 present when a change needs a restart to apply
```

## Configuration layers

An instance's effective configuration (`instances/<id>/run/`) is built from three layers, each overriding the one
before:

1. **Fleet defaults** (`config/`).
2. **Profile** (`profiles/<name>/`), if the instance's settings name one (`profile = test`).
3. **Instance** (`instances/<id>/settings.txt`, and files in `instances/<id>/config/`).

**Settings** (`settings.txt` at each layer: `config/`, `profiles/<name>/`, and the instance's own
`instances/<id>/settings.txt`) are `key = value` lines, merged key by key, so a layer only lists what it changes:

| Key | Meaning |
|---|---|
| `name` | server name: `Playground #{id}` by default (Playground #1...); the test server's is Testground |
| `admin_password` | admin password (required): fleet-wide in `config/`, the test profile has its own |
| `password` | game password; empty = open |
| `max_players` | player cap (20) |
| `inactivity_kick` | `no`: the game never kicks idle players |
| `start` | `<mode> [map] [team1 team2]`, names as in `!mode`/`!setmap`; a map or factions not given are random (a map from the mode's pool, two different factions), except for modes with `factions = fixed` (groupfight, groupduel) |
| `option.<Option>` | any other game option, as is (`option.RoundTimeLimit = 600`) |
| `profile` | the instance's profile (in its own settings) |
| `intermission_seconds`, `post_match_seconds` | MPManager timing (live) |

`{id}` in a value is replaced by the instance id. The game's config (`run/server.txt`: `ServerName`, the passwords,
the player cap, the start mode's game type and a map to boot on, `option.` lines, `start_game`) is generated from
these; MPManager reads the merged settings without the passwords (`run/settings.txt`) and applies `start` before the
first mission. At each start the name also gets the start time appended (the server list is slow to drop a
restarted server's old entry).

Permissions (`permissions.txt`, who may run which chat command, down to subcommands; MPManager's README) differ
by profile: production lets players set the server up (modes, maps, warmup), keeping bots, kick, tp and kill for
admins and only information from MPBalancer; the test profile keeps the old levels.

Aliases, maps, permissions and modes are data files: **whole files override** (a profile's or instance's `modes/skirmish.txt`
replaces the default one entirely; the instance's go in `instances/<id>/config/`). Files starting with a dot are
ignored.

**Applying a change** (`mpm <target> apply`, also run by the deploy) re-renders `run/` and updates only the files
that changed, in place:

| Change | When it takes effect |
|---|---|
| modes, maps, aliases, MPManager settings | live: MPManager re-reads them when they change |
| name, passwords, player cap, `start`, `option.` | at the next start: `restart-pending` is set |
| mods, the game, `run-server.sh` | at the next start |
| `!b` balancer settings | live, until the restart; defaults are built into MPBalancer (per-profile overrides: *planned*) |

## Who owns which files

- **mpServerConfig (repo) owns** `server/` (scripts, token), `config/` and `profiles/`; its `deploy.sh` replaces
  them on the server, then applies to every instance.
- **The server owns** `instances/` (`settings.txt`, instance files): edited with `mpm config set` or the app,
  never overwritten by a deploy. The repo's `instances/` holds a backup, refreshed with mpServerConfig's `pull.sh`; a deploy
  only creates an instance's `settings.txt` from it when the server has none.
- *Planned:* the app can also edit profiles and defaults on the server; then the server becomes the live copy of
  everything, `pull.sh` brings it back to the repo, and `deploy.sh` warns when the server changed since the last
  pull.

## mpm

The one tool everything goes through (the terminal, scripts on the PC, the app):

```
mpm [<id> | all] <command> [args]
```

- No id, or `0`: the test instance. `all`: production instances 1–10 that exist (never 0).
- Commands: `cmd`, `logs`, `perf`, `start`, `stop`, `restart [--when-empty]`, `status`,
  `apply`; `list` (every instance, one line each; `--json` for programs); `create <id> [profile]`.
- `restart --when-empty` leaves `instances/<id>/restart-when-empty`; MPManager checks it every 3 s and, once no
  player is connected, exits at once with code 75 (`_exit`: .NET's own exit hangs on the game's threads); the
  service restarts it. Nobody is kicked; `mpm <id> stop` cancels it.
- `config [--json]` shows an instance's merged settings with the layer each comes from (config, profile:<name>,
  instance; passwords only as set or not); `config set <key> <value...>` / `config unset <key>` change the
  instance's own settings and apply them (`run-server.sh --settings` does the merging).
- `status --json` and `list --json` ("live") give MPManager's state: the control socket answers `@status` with one
  JSON line (Status.cs): name, mode, game type, map, factions, player cap, state (standby, intermission, warmup,
  warmup_ending, playing, ending), and the players (name, platform id, team, admin). `@status` isn't logged.

## Running instances

- Each instance is a systemd **user** service, `bannerlord@<id>` (template in mpServerConfig's
  `systemd/bannerlord@.service`, installed in `~/.config/systemd/user/` by its deploy), running
  `run-server.sh -i <id> --foreground`; lingering is on, so services run without a login. No instance starts
  at boot (the unit has no `[Install]` section): start them with `mpm`.
  - Any exit but a requested stop restarts it after 15 s (`Restart=always`; at most 5 times in 5 minutes, then it
    stays failed): a crash, restart when empty (exit code 75), or a failed lobby registration (the server exits
    cleanly). A requested stop stays stopped (SIGTERM, status 15).
  - The lobby rate-limits registrations (error 1015 in server.log, then "Couldn't connect to custom battle server
    manager"): 11 servers starting within seconds, twice in a few minutes, hit it. `mpm all start/restart` spaces
    starts 10 s apart.
  - `mpm start --vanilla` etc. options go to `instances/<id>/start-options`: crash restarts keep them, the next
    plain start clears them.
  - `systemctl --user status bannerlord@<id>` shows the server's command line, auth token included: don't share it.
- Screen (`run-server.sh -i <id> -d`) is still there for running an instance by hand; `mpm` then only stops it.
- Game updates (*planned*): install the new version into a new folder, switch a symlink, restart instances when
  empty, one at a time.

## The app (*planned*)

A desktop app (WPF, a single self-contained `.exe`) on any of our PCs, connecting over SSH (SSH.NET, one
persistent connection; each PC uses its own key). No web service and no open port on the server.

- It's a front end over `mpm --json`: the instance list (polled every few seconds), log streaming, commands,
  start/stop/restart (when empty), and configuration editing by layer. All server logic stays in `mpm`, so the
  terminal and the app behave the same.
- It also takes over the Python tools: the curve editor, the thrust curve plots, hit and match reports.
- For admins without full SSH access, later: a key whose `authorized_keys` entry only allows `mpm` (forced command).
- Windows-only with WPF; Avalonia if a non-Windows PC needs it.

## Build order

1. Instances: `run-server.sh -i <id>` (render, ports, pinning), `mpm` with ids and `list`/`apply`/`create`,
   mpServerConfig in layers, the current server moved to instance 0. **Done.**
2. systemd user services, restart when empty. **Done.**
3. `config` commands, MPManager's JSON status, `pull.sh`. **Done.** The operator guide: [operations.md](operations.md).
4. The app, then the Python tools moved into it.
