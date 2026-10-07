using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using TaleWorlds.MountAndBlade;

namespace MPManager;

// Console commands from a named pipe instead of the server's own console, whose input never reaches the game on
// the Linux server (typed lines are echoed by the terminal, but not executed).
//
// run-server.sh creates the pipe and passes its path in MPM_CONSOLE_FIFO. A background thread reads lines from
// it; Tick runs them on the main thread through GameNetwork.HandleConsoleCommand, exactly as if they had been
// typed into the console: options, the game's commands and MPManager's mpm_* commands all work.
// Send a command with: echo "list" > ~/bannerlord/console.fifo   (or mpm cmd / mpm console)
internal static class CommandInput
{
    private static readonly ConcurrentQueue<string> Pending = new ConcurrentQueue<string>();

    public static void Start()
    {
        string path = Environment.GetEnvironmentVariable("MPM_CONSOLE_FIFO");
        if (string.IsNullOrEmpty(path))
        {
            MPManagerSubModule.Log("No command pipe (MPM_CONSOLE_FIFO is not set); commands only work through the server console.");
            return;
        }
        Thread thread = new Thread(() => ReadLoop(path)) { IsBackground = true, Name = "MPManager command pipe" };
        thread.Start();
        MPManagerSubModule.Log("Reading commands from " + path + ".");
    }

    private static void ReadLoop(string path)
    {
        while (true)
        {
            try
            {
                // Opening blocks until a writer connects; reading ends when the last writer closes, then we reopen.
                using (StreamReader reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read)))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        line = line.Trim();
                        if (line.Length > 0)
                        {
                            Pending.Enqueue(line);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                MPManagerSubModule.Log("Command pipe failed, retrying in 5 s: " + e.Message);
                Thread.Sleep(5000);
            }
        }
    }

    // Called every frame on the main thread.
    public static void Tick()
    {
        while (Pending.TryDequeue(out string command))
        {
            MPManagerSubModule.Log("> " + command);
            try
            {
                GameNetwork.HandleConsoleCommand(command);
            }
            catch (Exception e)
            {
                MPManagerSubModule.Log("Command failed: " + e);
            }
        }
    }
}
