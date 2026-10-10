# mpManager roadmap

Ideas and designs discussed but not built yet, with what was found while looking into them. Nothing here is
urgent while there is one test server, watched while it runs.

## Performance of passive mod code (hit logging, stats)

Code reacting to game events (every hit, every death) runs on the server's main thread, inside the tick
(7.14 ms at 140 Hz). Engine work (physics, AI, networking) dominates that budget; mod code costs
microseconds per event if it follows these rules:

- **Hot path = record numbers only.** On a hit: update counters in memory (dictionaries keyed by player or
  agent, preallocated where possible). No string building, no LINQ, no reflection, no logging per event.
- **No disk I/O on the main thread per event.** Queue rows in memory and write them from a background thread
  (mpbalancer's `HitLog.cs` does this for its hit CSV: combat code only queues a struct of values).
- **Aggregate, write at the end.** Groupfight stats per round, skirmish stats per mission: keep totals in
  memory, write or print them when the round or mission ends (a moment players don't fight).
- **Avoid per-tick allocations** (new lists every frame): they turn into garbage collections, and full ones
  are tick hitches. So far full collections happen only at map loads.
- **Never dereference null:** on the Linux server a null access is a native crash that takes the server down,
  not an exception (see `Peers.cs`). Stats code touching agents and peers must check for removed agents and
  null components; read only agents still in the mission's active list.

Measured so far: MPManager plus mpbalancer (with per-hit logging) at 140 Hz show a steady 140 Hz, 99% of ticks
within 7.2–7.45 ms, and no late ticks outside map loads. The metrics would show a regression (`tick_p99_ms`,
`late_pct`, `gc_gen2`), so compare a session before and after adding mpStats. If finer numbers are needed,
time each mod's tick and event handlers with a Stopwatch and add their totals to the metrics rows.

## Publishing

**Make MPManager work without our launcher (do this first).** MPManager finds every file it uses (modes,
maps.txt, aliases.txt, settings.txt, its log, metrics, the control socket) through environment variables set by
`scripts/server/run-server.sh`. A server owner who just installs the module gets almost nothing. Instead:

- default to a folder that ships with the module (e.g. `Modules/MPManager/config/` for modes, maps, aliases,
  settings; logs, metrics and the socket in a writable folder such as `Modules/MPManager/data/`), and keep the
  environment variables as overrides;
- ship example modes, maps.txt and settings.txt;
- document installation for someone who isn't us.

**Windows.** Many community servers run on Windows.

- The module mostly works already: the build produces `Win64_Shipping_Server`, and Unix domain sockets exist on
  Windows 10 and later (`UnixDomainSocketEndPoint` works there). Only the `chmod` that restricts the socket to
  its owner is Linux-only; it fails with a logged warning.
- The tooling doesn't: `run-server.sh` (bash, screen) and `mpm` (bash, socat). Rather than maintaining bash and
  PowerShell twins, rewrite `mpm` as a small .NET console app (one codebase, built for linux-x64 and win-x64):
  `cmd`, `logs`, `perf`, `status`, talking to the control socket and reading the log and metrics files.
- Starting and keeping servers running is per OS: systemd on Linux, a Windows service on Windows (see the
  fleet section; both restart a crashed server unless it was stopped on purpose).
- On Windows the server's own console input works, but the control socket is still the way to get answers.

## Hosting several servers

### Layout: one install, many instances

Install the dedicated server once (steamcmd) and treat it as read-only; each instance only has a small folder of
its own:

```
/srv/bannerlord/game/              one install: game, shared mods, custom maps (read-only for instances)
/srv/bannerlord/instances/neko/    config, modes, aliases, settings, maps.txt, logs, metrics, control socket
/srv/bannerlord/instances/cup-a/
```

Needed in our scripts:

- per-instance generated config: `run-server.sh` writes `Modules/Native/.run-config.txt`, the same file for
  every instance; it needs to be per instance (e.g. `.run-<instance>.txt`);
- a different game port per instance (7210, 7211, ...); check with `ss -ulpn` which other ports an instance
  opens (the map download helper, the web panel if enabled);
- `mpm <instance> ...` and `mpm all ...`.

Game updates: install the new version into a new folder and switch a symlink, then restart instances one at a
time, so a running server never sees its files change underneath it.

### Running, restarting and limiting instances

On one machine, systemd is simpler than Docker:

- a template unit `bannerlord@.service` (one service per instance: `systemctl start bannerlord@cup-a`), started
  without screen; output to the journal and our log files;
- `Restart=on-failure`: a crashed server comes back within seconds, a stopped one stays stopped;
- `AllowedCPUs=` to pin each instance to its own cores: the most important limit for tick stability, two
  servers sharing a core make each other stutter; avoid putting two instances on the two threads of one core;
- `MemoryMax=`; a separate unprivileged user per instance;
- a `bannerlord.target` grouping all instances, to start or stop the fleet at once.

Measured on the 7900X (12 cores, 24 threads): about 1.4–1.6 cores per server at 140 Hz with a mission loaded
(little more with 20+ agents), about 0.2 cores in standby, 500–900 MB of memory (growing with each distinct map
loaded, levelling off). So roughly 8–10 instances with 2 dedicated threads each and headroom. The tick rate
stays at 140. Check each instance's metrics (`late_pct`, `tick_p99_ms`) once several run at the same time.

Docker does the same (`--cpuset-cpus`, `--memory`, `--restart unless-stopped`, the install mounted read-only)
and is worth it for packaging, separate networks, or per-container bandwidth figures (network use per server
can't be told apart from inside one process on a shared host).

### Orchestration

1. **CLI (one machine, up to about 10 instances):** `systemctl start bannerlord.target`; `mpm all cmd
   /endwarmup`, `mpm all restart`; `mpm list`: one line per instance with running or not, players, mode and
   map, tick health from the latest metrics row.
2. **Control API, then a dashboard.** The control socket already answers commands; add requests that return
   structured data (`status`: players, mode, map, warmup state, tick health; recent log lines). `mpm` and a
   dashboard both use it. The dashboard is a small web app on the host listing every instance, with status,
   metrics graphs, and a command box for one, several or all instances; behind a login or a VPN (Tailscale),
   so tournament admins can use it without SSH. Check first what the server's own web panel
   (`DedicatedCustomServer.WebPanel` in the decompiled sources) offers and whether it runs on Linux; it won't
   know about modes, maps or our metrics.
3. **Several machines:** Nomad, or Agones (game-server orchestration on Kubernetes). Only once one machine is
   not enough.

### Protection (tournaments)

In order of importance:

1. **Hosting.** Volumetric DDoS saturates the line before the machine sees anything; nothing on the server
   stops it. For a tournament, host on a provider with game-traffic DDoS filtering (OVH Game, Path.net and the
   like), not a home connection, which also exposes a home IP to every player.
2. **Host firewall (nftables):** open only the instances' game ports; limit new connections and packets per
   second per source IP; SSH key-only and not public (VPN only).
3. **Server side:** passwords on tournament servers, a strong admin password per instance, logs and metrics
   showing who joined, from where (the log has join IPs) and when tick health dropped. MPManager could add join
   rate limits or a ban list.
4. **Monitoring:** a check per instance that alerts when a server is down or its tick health degrades, from
   the metrics files.

Logs contain player IPs and the server prints its auth token into `server.log`: keep logs private.
