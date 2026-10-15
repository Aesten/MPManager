using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using TaleWorlds.MountAndBlade;

namespace MPManager;

// Restart when empty: "mpm restart --when-empty" creates the flag file run-server.sh names in MPM_RESTART_FLAG
// (instances/<id>/restart-when-empty). Every few seconds, if the flag is there and no player is connected, the
// server exits with ExitCode; the instance's systemd service restarts it on that code (RestartForceExitStatus), with
// the current configuration, mods and game. Nobody is kicked: a busy server waits until its last player leaves.
// When the flag appears, the players on are told in chat; players who join while it's pending are told on joining.
internal static class RestartWhenEmpty
{
    public const int ExitCode = 75;
    private const double CheckSeconds = 3;

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly string Flag = Environment.GetEnvironmentVariable("MPM_RESTART_FLAG");
    private const string Notice = "A server restart is scheduled: it happens as soon as everyone has left (nobody gets kicked).";

    private static double _nextCheck;
    private static readonly HashSet<string> Told = new HashSet<string>();

    public static void Tick()
    {
        if (string.IsNullOrEmpty(Flag) || Clock.Elapsed.TotalSeconds < _nextCheck)
        {
            return;
        }
        _nextCheck = Clock.Elapsed.TotalSeconds + CheckSeconds;
        if (!File.Exists(Flag))
        {
            Told.Clear(); // none pending, or cancelled: announce the next one afresh
            return;
        }
        var players = Peers.Players();
        if (players.Count > 0)
        {
            Announce(players);
            return;
        }
        File.Delete(Flag);
        MPManagerSubModule.Log("Restart when empty: nobody on, exiting with code " + ExitCode + " for the service to restart the server.");
        // Environment.Exit hangs here (the game's shutdown waits on its own threads), so leave at once, as a crash
        // would: our logs, metrics and match records are written as they go. Nobody is on, so nothing is lost.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            _exit(ExitCode);
        }
        Environment.Exit(ExitCode);
    }

    // Everyone on hears it once when it's scheduled; later joiners on their own.
    private static void Announce(List<NetworkCommunicator> players)
    {
        if (Told.Count == 0)
        {
            ServerChat.Broadcast(Notice);
            foreach (NetworkCommunicator peer in players)
            {
                Told.Add(peer.VirtualPlayer?.Id.ToString() ?? peer.UserName);
            }
            return;
        }
        foreach (NetworkCommunicator peer in players)
        {
            if (Told.Add(peer.VirtualPlayer?.Id.ToString() ?? peer.UserName))
            {
                ServerChat.Send(peer, Notice);
            }
        }
    }

    [DllImport("libc", EntryPoint = "_exit")]
    private static extern void _exit(int status);
}
