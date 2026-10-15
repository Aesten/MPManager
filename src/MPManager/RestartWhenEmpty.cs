using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace MPManager;

// Restart when empty: "mpm restart --when-empty" creates the flag file run-server.sh names in MPM_RESTART_FLAG
// (instances/<id>/restart-when-empty). Every few seconds, if the flag is there and no player is connected, the
// server exits with ExitCode; the instance's systemd service restarts it on that code (RestartForceExitStatus), with
// the current configuration, mods and game. Nobody is kicked: a busy server waits until its last player leaves.
internal static class RestartWhenEmpty
{
    public const int ExitCode = 75;
    private const double CheckSeconds = 3;

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly string Flag = Environment.GetEnvironmentVariable("MPM_RESTART_FLAG");
    private static double _nextCheck;

    public static void Tick()
    {
        if (string.IsNullOrEmpty(Flag) || Clock.Elapsed.TotalSeconds < _nextCheck)
        {
            return;
        }
        _nextCheck = Clock.Elapsed.TotalSeconds + CheckSeconds;
        if (!File.Exists(Flag) || Peers.Players().Count > 0)
        {
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

    [DllImport("libc", EntryPoint = "_exit")]
    private static extern void _exit(int status);
}
