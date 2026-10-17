# Running the servers

How to run the Bannerlord server fleet day to day: where things are, and how to do each common task. Why it's built
this way is in [fleet.md](fleet.md).

## The fleet

One machine, `7900x`, runs up to 11 server instances, all as the `bannerlord` user (log in as `bannerlord@7900x`;
its traffic can be routed separately, e.g. through WireGuard, by user):

| Instance | Name in the server list | Port | Settings |
|---|---|---|---|
| 0 | Testground | 7210 | test profile: its own passwords, admin-only commands |
| 1–10 | Playground #01 … #10 | 7211–7220 | fleet defaults: fleet passwords, players set up the server |

Each instance is a systemd user service (`bannerlord@<id>`) pinned to its own CPU core. **Nothing starts at boot**:
after a reboot, start what you need.

## Where things live

Three places, each owning different things. Edit in the right one, or your change gets overwritten.

| What | Edit it in | Goes live with |
|---|---|---|
| Fleet defaults: `settings.txt`, `permissions.txt`, `aliases.txt`, `maps.txt`, `modes/` | mpServerConfig `config/` (on your PC) | `./deploy.sh` |
| Profiles (the test server's differences) | mpServerConfig `profiles/<name>/` | `./deploy.sh` |
| One server's own settings (name, profile...) | the server: `mpm <id> config set ...` | immediately (applied) |
| Launcher, systemd unit, auth token | mpServerConfig `server/`, `systemd/` | `./deploy.sh`, then a restart |
| Custom maps | mpServerConfig `maps/<mode>/` | `./deploy-maps.sh`, then a restart |
| MPManager / MPBalancer / MPStats / MPGroupDuel | each repo's code; balance defaults in mpBalancer's `src/MPBalancer/defaults.txt` | the repo's `scripts/deploy.sh`, then a restart |

mpServerConfig and the server's settings hold the passwords: keep the repo private, don't share logs (the server
prints its auth token into them), and don't share `systemctl --user status` output (it shows the token too).

## Everyday commands

On the server (`ssh bannerlord@7900x`). Without an id, `mpm` means the test server; `all` means Playground 1–10.

```sh
mpm list                      # every server: running, uptime, players, state, pending restart
mpm 3 status                  # one server; status --json for its live mode, map, factions, players
mpm all start                 # start Playground 1-10 (10 s apart: the lobby rate-limits registrations)
mpm all stop                  # stop them (never touches the test server)
mpm 0 start / stop / restart  # the test server
mpm 3 logs -f                 # follow a server's log (Ctrl+C to stop following)
mpm 3 perf                    # its latest performance numbers (tick rate, CPU, memory)
mpm 3 cmd setmap lighthouse   # run a chat command as the console (it may run everything)
```

From your PC, run one-off commands with `ssh bannerlord@7900x 'mpm list'`. Quote anything containing `#` or
spaces: `'mpm 3 config set name "Playground #03 EU"'`.
From Windows (cmd or PowerShell), use double quotes instead, as `'...'` isn't quoting there:
`ssh bannerlord@7900x "mpm 0 restart"`.

## Common tasks

### Restart without kicking anyone

```sh
mpm 3 restart --when-empty     # restarts as soon as nobody is on
mpm all restart --when-empty   # every Playground
```

Players on the server are told in chat that a restart is scheduled (and nobody gets kicked), and players who
join before it happens are told on joining. `mpm list` shows "when empty" until it happens; `mpm 3 stop` cancels it by stopping now. Use this after deploying
a mod, a map, or a change marked "pending" in `mpm list`.

### Change one server's settings

```sh
mpm 3 config                          # its settings, and the layer each comes from (config, profile, instance)
mpm 3 config set max_players 32       # override for this server only
mpm 3 config unset max_players        # back to the fleet default
mpm all config set start "skirmish xauna"
```

Settings MPManager uses (timings) apply live; name, passwords, player cap, `start` and `option.` settings need a
restart (`mpm list` shows "pending"). Back up server-side changes into the repo now and then: `./pull.sh` in
mpServerConfig, then commit.

The settings (all in the same `key = value` format, at every layer):

| Key | Meaning |
|---|---|
| `name` | server name; `{id2}` is the id on two digits (`Playground #{id2}`) |
| `password`, `admin_password` | game password (empty: open), admin password |
| `max_players` | player cap |
| `inactivity_kick` | `no`: never kick idle players |
| `start` | how it starts: `<mode> [map] [team1 team2]`, e.g. `skirmish xauna vlandia aserai`; left out = random |
| `option.<Option>` | any other game option, e.g. `option.RoundTimeLimit = 600` |
| `profile` | the server's profile (`test` for instance 0) |
| `intermission_seconds`, `post_match_seconds` | time between missions, scoreboard time |

### Change something for every server

Edit mpServerConfig's `config/` on your PC, then `./deploy.sh`. It replaces `config/` and `profiles/` on the
server and applies them to every instance; live where possible, otherwise "pending". Server-side changes made with
`mpm config set` are kept (they're the instances' own).

### Change who may run a command

Edit `config/permissions.txt` (Playgrounds) or `profiles/test/permissions.txt` (test server), then `./deploy.sh`.
Live, no restart. Rules are `<command> [word ...] = everyone | admin | console`, the most specific wins:

```
setmap = everyone
mpb debug = everyone
mpb * * = admin          # "!b <setting> <value>": changing a balance setting
```

Players' `!help` only lists what they may run. Syntax details: mpManager's README, "Permissions".

### Deploy a mod update

```sh
scripts/deploy.sh                       # in mpManager, mpBalancer, mpStats or mpGroupDuel: builds and installs
ssh bannerlord@7900x '~/bannerlord/mpm 0 restart'                   # test it on the test server first
ssh bannerlord@7900x '~/bannerlord/mpm all restart --when-empty'    # then the Playgrounds
```

Running servers keep the version they loaded until they restart. mpBalancer, mpStats and mpGroupDuel build against
mpManager's shared `link/MPManagerLink.cs`: mpGroupDuel from its `external/mpManager` submodule if checked out, the
others from an mpManager checkout next to them, so keep the repos side by side.

### Try a balance change

Live, on the test server only, until its next restart:

```sh
mpm cmd b thrust_curve_spear 100 90 ...   # or in game: !b thrust_curve_spear ...
mpm cmd b reset                           # back to the defaults
```

To keep it, change mpBalancer's `src/MPBalancer/defaults.txt` (the curve editor, `scripts/curve-editor.py`, saves
curves there), deploy, and restart. Record each played set of defaults in mpBalancer's `docs/settings-history.md`.

### Add a server

Ids 0–10 only (one per CPU core). Instances 1–10 already exist; to recreate one:

```sh
mpm create 4            # named Playground #04 by the fleet default; mpm create 4 <profile> "<name>" to differ
mpm 4 start
```

### Add a custom map

Put it in mpServerConfig's `maps/<mode>/<map id>/`, add it to the mode's `maps =` line in `config/modes/` (and a
name in `config/maps.txt`), then `./deploy-maps.sh` and `./deploy.sh`, and restart when empty: servers only see
new maps after a restart.

### After a reboot

Nothing starts by itself:

```sh
mpm 0 start      # the test server
mpm all start    # the Playgrounds (about 1.5 min: 10 s apart)
```

## When something goes wrong

**A server won't start.** `mpm 3 start` prints the reason when it's the configuration ("no name", "no
admin_password", "no mode 'x'"). Otherwise:

```sh
mpm list                                          # SERVICE "failed"?
journalctl --user -u bannerlord@3 -n 30           # what systemd saw
tail -50 ~/bannerlord/instances/3/server.log      # what the server printed
```

**It's not in the server list / shuts down after a few seconds.** Look for `error code: 1015` or "Couldn't connect
to custom battle server manager" in its `server.log`: the lobby rate-limits registrations when many servers start
at once. The service retries every 15 s; after 5 failures in 5 minutes it gives up ("failed" in `mpm list`). Wait
a few minutes, then `mpm 3 start`.

**It keeps crashing.** The service restarts it after 15 s, at most 5 times in 5 minutes, then leaves it "failed".
The crash is at the end of the previous log, archived in `~/bannerlord/instances/3/logs/server-<time>.log.gz`
(`zless` to read it). A mod deploy is the usual suspect: test with `mpm 3 start --vanilla` (no mods) or
`--no-balancer`; the options stay until the next plain `mpm 3 start`.

**Players can't use a command.** Check the permissions file for that server's profile. `mpm 3 cmd ...` runs as
the console and may run everything, so it doesn't tell you what players can do; join without the admin password to
check.

**Players can't connect but the server is listed.** Ports: each server needs its port (7210 + id) open on UDP and
TCP, in ufw (the `Bannerlord` profile, `sudo ufw status`) and forwarded by the router.

**An empty server shows "standby".** That's normal: after 60 s with nobody on, it ends the mission and waits with
nothing loaded. The next mission starts when someone joins.

## Files on the server

```
~/bannerlord/
  mpm, run-server.sh, token.txt
  config/, profiles/                 from mpServerConfig (replaced by deploy.sh)
  instances/<id>/
    settings.txt                     the server's own settings (mpm config set)
    run/                             the effective configuration, generated: don't edit
    server.log, mpmanager.log        this run's logs (mpm logs reads mpmanager.log)
    logs/                            earlier runs, gzipped, 30 days
    metrics/                         performance and hit CSVs, 30 days
    stats/                           match records (MPStats), kept
  data/player-numids.txt             player numbers (MPManager), shared by every instance
~/.config/systemd/user/bannerlord@.service
```
