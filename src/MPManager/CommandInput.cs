using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using TaleWorlds.MountAndBlade;

namespace MPManager;

// Commands from the machine the server runs on, with answers: a Unix domain socket (a local socket file, so
// nothing is reachable from the network) instead of the server's own console, whose input never reaches the game
// on the Linux server.
//
// run-server.sh passes the socket's path in MPM_CONTROL_SOCKET (~/bannerlord/control.sock); only the server's
// user can connect. Protocol: connect, send one command line, read the reply lines until the server closes the
// connection. Commands run on the main thread, in Tick, like everything else; the reply is every line printed on
// the main thread while the command ran (ServerLog capture): a chat command's replies and announcements, or the
// game console's own output. Chat commands (Commands, run as an admin) can be sent with or without their "!" or
// "/" ("help", "/help"; a "!" is awkward in bash); anything else goes to GameNetwork.HandleConsoleCommand,
// exactly as if typed into the console.
//
// From a shell: mpm cmd /setmap lighthouse, or: echo "/maps" | socat -t 30 - UNIX-CONNECT:~/bannerlord/control.sock
internal static class CommandInput
{
    private const int ReplyTimeoutMs = 30000;

    private sealed class Request
    {
        public string Command;
        public List<string> Reply;
        public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
    }

    private static readonly ConcurrentQueue<Request> Pending = new ConcurrentQueue<Request>();

    public static void Start()
    {
        string path = Environment.GetEnvironmentVariable("MPM_CONTROL_SOCKET");
        if (string.IsNullOrEmpty(path))
        {
            MPManagerSubModule.Log("No control socket (MPM_CONTROL_SOCKET is not set).");
            return;
        }
        try
        {
            File.Delete(path); // left over from an earlier run
            Socket listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(8);
            RestrictToOwner(path);
            new Thread(() => AcceptLoop(listener)) { IsBackground = true, Name = "MPManager control socket" }.Start();
            MPManagerSubModule.Log("Taking commands on " + path + ".");
        }
        catch (Exception e)
        {
            MPManagerSubModule.Log("Could not open the control socket " + path + ": " + e.Message);
        }
    }

    // .NET 6 has no API for file modes; chmod does it.
    private static void RestrictToOwner(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("chmod", "600 \"" + path + "\"") { UseShellExecute = false })?.WaitForExit(2000);
        }
        catch (Exception e)
        {
            MPManagerSubModule.Log("Could not restrict " + path + " to its owner: " + e.Message);
        }
    }

    private static void AcceptLoop(Socket listener)
    {
        while (true)
        {
            try
            {
                Socket client = listener.Accept();
                ThreadPool.QueueUserWorkItem(_ => Serve(client));
            }
            catch (Exception e)
            {
                MPManagerSubModule.Log("Control socket failed, retrying in 5 s: " + e.Message);
                Thread.Sleep(5000);
            }
        }
    }

    // One request per connection: a command line in, its reply out, then close.
    private static void Serve(Socket client)
    {
        try
        {
            using (client)
            using (NetworkStream stream = new NetworkStream(client, ownsSocket: false))
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
            using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n" })
            {
                client.ReceiveTimeout = 5000;
                string command = reader.ReadLine()?.Trim();
                if (string.IsNullOrEmpty(command))
                {
                    return;
                }
                Request request = new Request { Command = command };
                Pending.Enqueue(request);
                if (!request.Done.Wait(ReplyTimeoutMs))
                {
                    writer.WriteLine("(no answer within " + ReplyTimeoutMs / 1000 + " s; the server may be loading a map. The command still runs.)");
                    return;
                }
                foreach (string line in request.Reply)
                {
                    writer.WriteLine(line);
                }
            }
        }
        catch (Exception e)
        {
            MPManagerSubModule.Log("Control socket request failed: " + e.Message);
        }
    }

    // Called every frame on the main thread.
    public static void Tick()
    {
        while (Pending.TryDequeue(out Request request))
        {
            MPManagerSubModule.Log("> " + request.Command);
            string chatCommand = ChatInterceptor.IsCommand(request.Command) ? request.Command
                : Commands.IsKnown(request.Command.Split(' ')[0]) ? "!" + request.Command
                : null;
            ServerLog.BeginCapture();
            try
            {
                if (chatCommand != null)
                {
                    Commands.RunFromConsole(chatCommand);
                }
                else
                {
                    GameNetwork.HandleConsoleCommand(request.Command);
                    KeepForNextMissions(request.Command);
                }
            }
            catch (Exception e)
            {
                MPManagerSubModule.Log("Command failed: " + e);
            }
            finally
            {
                // The command's echo ("console: !setmap ...") isn't part of the answer; neither is the log prefix.
                string echo = "[MPManager] console: " + chatCommand;
                request.Reply = ServerLog.EndCapture()
                    .Where(line => line != echo)
                    .Select(line => line.StartsWith("[MPManager] ") ? line.Substring("[MPManager] ".Length) : line)
                    .ToList();
                request.Done.Set();
            }
        }
    }

    // The console sets an option for the current mission only, and each mission starts from the next-map options,
    // so the change was undone at the next map. Copy it there too.
    private static void KeepForNextMissions(string command)
    {
        string name = command.Split(' ')[0];
        if (!MultiplayerOptions.TryGetOptionTypeFromString(name, out var option, out var property))
        {
            return;
        }
        var current = MultiplayerOptions.MultiplayerOptionsAccessMode.CurrentMapOptions;
        var next = MultiplayerOptions.MultiplayerOptionsAccessMode.NextMapOptions;
        switch (property.OptionValueType)
        {
        case MultiplayerOptions.OptionValueType.String:
            option.SetValue(option.GetStrValue(current), next);
            break;
        case MultiplayerOptions.OptionValueType.Bool:
            option.SetValue(option.GetBoolValue(current), next);
            break;
        default:
            option.SetValue(option.GetIntValue(current), next);
            break;
        }
    }
}
