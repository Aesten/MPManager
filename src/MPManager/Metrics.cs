using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using TaleWorlds.MountAndBlade;

namespace MPManager;

// Performance and resource numbers for the server operator (never shown to players), to judge how the server
// holds up and how many instances a machine can host. Every WindowSeconds, a row goes to
// MPM_METRICS_DIR/server-<start>.csv:
//
//   time            end of the window (UTC)
//   players         connected players; agents: agents in the mission (players, bots, horses); state: mission
//                   state (none = between missions or standing by)
//   target_hz       the /tickrate the server was started with
//   hz              ticks per second actually reached
//   tick_avg_ms, tick_p50_ms, tick_p99_ms, tick_max_ms
//                   time from one tick to the next
//   late_pct        ticks that took over 1.5x the target interval (felt as stutter)
//   cpu_pct         CPU used by the process, in % of one core (200 = two cores busy)
//   mem_mb, heap_mb, gc_gen2
//                   process memory (working set), .NET heap, and full garbage collections in the window
//   threads         process threads
//
// and one row per connected player to MPM_METRICS_DIR/players-<start>.csv: the player's platform id ("2.0.0.<steam
// id>", the same as in MPStats' match records, stable across name changes) and name, ping (the game's averaged
// ping, sampled every second), its min, max and standard deviation over the window (jitter), and packet loss.
// Nothing of this goes to the server log.
internal static class Metrics
{
    private const double WindowSeconds = 10;
    private const double PingSampleSeconds = 1;
    private const double LateFactor = 1.5;

    private static StreamWriter _server;
    private static StreamWriter _players;
    private static bool _disabled;
    private static int _targetHz;

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly List<double> TickIntervalsMs = new List<double>(4096);
    private static double _lastTickMs = -1;
    private static double _windowStartMs;
    private static double _nextPingSampleMs;
    private static TimeSpan _cpuAtWindowStart;
    private static int _gen2AtWindowStart;

    private static readonly Dictionary<NetworkCommunicator, List<double>> PingSamples = new Dictionary<NetworkCommunicator, List<double>>();

    public static void Disable() => _disabled = true;

    public static void Tick()
    {
        if (_disabled)
        {
            return;
        }
        if (_server == null && !Open())
        {
            _disabled = true;
            return;
        }
        double now = Clock.Elapsed.TotalMilliseconds;
        if (_lastTickMs >= 0)
        {
            TickIntervalsMs.Add(now - _lastTickMs);
        }
        _lastTickMs = now;

        if (now >= _nextPingSampleMs)
        {
            _nextPingSampleMs = now + PingSampleSeconds * 1000;
            SamplePings();
        }
        if (now - _windowStartMs >= WindowSeconds * 1000)
        {
            WriteWindow(now);
        }
    }

    private static bool Open()
    {
        string dir = Environment.GetEnvironmentVariable("MPM_METRICS_DIR");
        if (string.IsNullOrEmpty(dir))
        {
            MPManagerSubModule.Log("No metrics (MPM_METRICS_DIR is not set).");
            return false;
        }
        Directory.CreateDirectory(dir);
        string start = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        _server = new StreamWriter(Path.Combine(dir, "server-" + start + ".csv")) { AutoFlush = true };
        _server.WriteLine("time,players,agents,state,target_hz,hz,tick_avg_ms,tick_p50_ms,tick_p99_ms,tick_max_ms,late_pct,cpu_pct,mem_mb,heap_mb,gc_gen2,threads");
        _players = new StreamWriter(Path.Combine(dir, "players-" + start + ".csv")) { AutoFlush = true };
        _players.WriteLine("time,player_id,player,ping_avg_ms,ping_min_ms,ping_max_ms,ping_jitter_ms,loss_pct");
        _targetHz = ReadTickRate();
        _windowStartMs = Clock.Elapsed.TotalMilliseconds;
        _cpuAtWindowStart = Process.GetCurrentProcess().TotalProcessorTime;
        _gen2AtWindowStart = GC.CollectionCount(2);
        MPManagerSubModule.Log("Writing metrics every " + WindowSeconds + " s to " + dir + " (server-" + start + ".csv, players-" + start + ".csv).");
        return true;
    }

    private static int ReadTickRate()
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("/tickrate", StringComparison.OrdinalIgnoreCase) && int.TryParse(args[i + 1], out int hz))
            {
                return hz;
            }
        }
        return 0;
    }

    private static void SamplePings()
    {
        foreach (NetworkCommunicator peer in Peers.All)
        {
            if (peer.IsServerPeer || !peer.IsSynchronized)
            {
                continue;
            }
            if (!PingSamples.TryGetValue(peer, out List<double> samples))
            {
                PingSamples[peer] = samples = new List<double>();
            }
            samples.Add(peer.AveragePingInMilliseconds);
        }
    }

    private static void WriteWindow(double now)
    {
        double windowMs = now - _windowStartMs;
        string time = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

        Process process = Process.GetCurrentProcess();
        TimeSpan cpu = process.TotalProcessorTime;
        double cpuPct = (cpu - _cpuAtWindowStart).TotalMilliseconds / windowMs * 100;
        int gen2 = GC.CollectionCount(2);

        List<double> ticks = TickIntervalsMs.OrderBy(t => t).ToList();
        double targetMs = _targetHz > 0 ? 1000.0 / _targetHz : 0;
        int late = targetMs > 0 ? ticks.Count(t => t > targetMs * LateFactor) : 0;
        int players = Peers.Players().Count;
        Mission mission = Mission.Current;
        string state = mission?.GetMissionBehavior<MissionLobbyComponent>()?.CurrentMultiplayerState.ToString().ToLowerInvariant() ?? "none";

        _server.WriteLine(string.Join(",",
            time,
            players,
            mission?.Agents.Count ?? 0,
            state,
            _targetHz,
            F(ticks.Count / (windowMs / 1000)),
            F(ticks.Count > 0 ? ticks.Average() : 0),
            F(Percentile(ticks, 0.50)),
            F(Percentile(ticks, 0.99)),
            F(ticks.Count > 0 ? ticks[ticks.Count - 1] : 0),
            F(ticks.Count > 0 ? 100.0 * late / ticks.Count : 0),
            F(cpuPct),
            F(process.WorkingSet64 / 1048576.0),
            F(GC.GetTotalMemory(false) / 1048576.0),
            gen2 - _gen2AtWindowStart,
            process.Threads.Count));

        foreach (var entry in PingSamples)
        {
            NetworkCommunicator peer = entry.Key;
            List<double> samples = entry.Value;
            if (samples.Count == 0)
            {
                continue;
            }
            double avg = samples.Average();
            double jitter = Math.Sqrt(samples.Sum(s => (s - avg) * (s - avg)) / samples.Count);
            string loss = peer.IsConnectionActive ? F(peer.AverageLossPercent) : "";
            _players.WriteLine(string.Join(",", time, peer.VirtualPlayer?.Id.ToString() ?? "", Csv(peer.UserName), F(avg), F(samples.Min()), F(samples.Max()), F(jitter), loss));
        }

        PingSamples.Clear();
        TickIntervalsMs.Clear();
        _windowStartMs = now;
        _cpuAtWindowStart = cpu;
        _gen2AtWindowStart = gen2;
    }

    private static double Percentile(List<double> sorted, double p)
    {
        return sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)(p * sorted.Count))];
    }

    private static string F(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Csv(string text) => "\"" + (text ?? "").Replace("\"", "\"\"") + "\"";
}
