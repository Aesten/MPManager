using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using NetworkMessages.FromServer;
using TaleWorlds.MountAndBlade;

namespace MPManager;

// Chat commands ("!map village", or "/map village"), from players in chat and from the command pipe
// (mpm cmd "!map village"). They are never shown in chat (see ChatInterceptor). Commands that change the server
// are for admins (players who joined with the admin password); the pipe counts as an admin.
internal static class Commands
{
    // Commands of other server mods: not answered with "unknown command" (they get the message too).
    private static readonly HashSet<string> OtherModsCommands = new HashSet<string> { "b", "mpb" };

    private static readonly ConcurrentQueue<(NetworkCommunicator Peer, string Text)> Pending = new ConcurrentQueue<(NetworkCommunicator, string)>();

    private static readonly List<Command> All = new List<Command>
    {
        new Command("help", "", "List the commands.", admin: false, Help),
        new Command("maps", "", "Numbered list of the maps for the current mode.", admin: false, ListMaps),
        new Command("map", "[map] [team1 team2]", "Show the map, or switch to another (and optionally the cultures) and restart. Typos, partial names and numbers from !maps work.", admin: true, Map),
        new Command("factions", "[team1 team2]", "Show the cultures, or switch them and restart. Short names work: emp, vla, khu...", admin: true, Factions),
        new Command("restart", "", "Restart the mission (same map and cultures).", admin: true, Restart),
        new Command("warmup", "end | hold", "End warmup now, or hold it until !warmup end.", admin: true, Warmup),
    };

    public static void Enqueue(NetworkCommunicator peer, string text)
    {
        Pending.Enqueue((peer, text));
    }

    // Called every frame on the main thread.
    public static void Tick()
    {
        while (Pending.TryDequeue(out var item))
        {
            if (item.Peer != null && !item.Peer.IsConnectionActive)
            {
                continue;
            }
            try
            {
                Execute(new Caller(item.Peer), item.Text);
            }
            catch (Exception e)
            {
                MPManagerSubModule.Log("Command failed: " + item.Text + ": " + e);
                new Caller(item.Peer).Reply("That command failed; the server log has the details.");
            }
        }
    }

    private static void Execute(Caller caller, string text)
    {
        string[] parts = text.Substring(1).Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return;
        }
        string name = parts[0].ToLowerInvariant();
        string[] args = parts.Skip(1).ToArray();
        MPManagerSubModule.Log(caller.Name + ": " + text);

        Command command = All.FirstOrDefault(c => c.Name == name);
        if (command == null)
        {
            if (!OtherModsCommands.Contains(name))
            {
                caller.Reply("Unknown command !" + name + ". !help lists the commands.");
            }
            return;
        }
        if (command.Admin && !caller.IsAdmin)
        {
            caller.Reply("!" + name + " is for admins (join with the admin password).");
            return;
        }
        command.Run(caller, args);
    }

    private static void Help(Caller caller, string[] args)
    {
        foreach (Command c in All.Where(c => caller.IsAdmin || !c.Admin))
        {
            caller.Reply("!" + c.Name + (c.Usage.Length > 0 ? " " + c.Usage : "") + ": " + c.Description);
        }
    }

    private static void ListMaps(Caller caller, string[] args)
    {
        List<Choice> maps = MissionFlow.Maps(MissionFlow.GameType);
        caller.Reply(MissionFlow.GameType + " maps:");
        for (int i = 0; i < maps.Count; i++)
        {
            caller.Reply((i + 1) + ". " + maps[i]);
        }
    }

    private static void Map(Caller caller, string[] args)
    {
        if (args.Length == 0)
        {
            caller.Reply(Describe());
            return;
        }
        // Map names can have spaces ("!map osrac insurrection"); the last two words are cultures only if both are.
        Choice team1 = null, team2 = null;
        int mapWords = args.Length;
        if (args.Length >= 3)
        {
            List<Choice> cultures = MissionFlow.Cultures();
            team1 = Matcher.Resolve(args[args.Length - 2], cultures, out _);
            team2 = Matcher.Resolve(args[args.Length - 1], cultures, out _);
            if (team1 != null && team2 != null)
            {
                mapWords -= 2;
            }
            else
            {
                team1 = team2 = null;
            }
        }
        Choice map = Matcher.Resolve(string.Join(" ", args, 0, mapWords), MissionFlow.Maps(MissionFlow.GameType), out string error);
        if (map == null)
        {
            caller.Reply(error);
            return;
        }
        MissionFlow.Change(map.Id, team1?.Id, team2?.Id);
        caller.Announce("Switched to " + map.Name + (team1 != null ? ", " + team1.Name + " vs " + team2.Name : "") + ".");
    }

    private static void Factions(Caller caller, string[] args)
    {
        if (args.Length == 0)
        {
            caller.Reply(Describe());
            caller.Reply("Cultures: " + string.Join(", ", MissionFlow.Cultures().Select((c, i) => (i + 1) + ". " + c.Name)));
            return;
        }
        if (args.Length != 2)
        {
            caller.Reply("Usage: !factions <team1> <team2>");
            return;
        }
        if (ResolveCultures(caller, args[0], args[1], out Choice team1, out Choice team2))
        {
            MissionFlow.Change(null, team1.Id, team2.Id);
            caller.Announce("Switched to " + team1.Name + " vs " + team2.Name + ".");
        }
    }

    private static bool ResolveCultures(Caller caller, string input1, string input2, out Choice team1, out Choice team2)
    {
        List<Choice> cultures = MissionFlow.Cultures();
        team2 = null;
        team1 = Matcher.Resolve(input1, cultures, out string error);
        if (team1 != null)
        {
            team2 = Matcher.Resolve(input2, cultures, out error);
        }
        if (team2 == null)
        {
            caller.Reply(error);
            return false;
        }
        return true;
    }

    private static void Restart(Caller caller, string[] args)
    {
        if (MissionFlow.Restart())
        {
            caller.Announce("Restarting the mission.");
        }
        else
        {
            caller.Reply("No mission to restart right now.");
        }
    }

    private static void Warmup(Caller caller, string[] args)
    {
        string action = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        switch (action)
        {
        case "end":
            string result = MissionFlow.EndWarmup(out bool ended);
            if (ended)
            {
                caller.Announce(result);
            }
            else
            {
                caller.Reply(result);
            }
            return;
        case "hold":
            caller.Announce(MissionFlow.HoldWarmup(true));
            return;
        default:
            caller.Reply("Usage: !warmup end | hold. Warmup hold is " + (MissionFlow.WarmupHeld ? "on." : "off."));
            return;
        }
    }

    // Current and next mission setup, in one line.
    private static string Describe()
    {
        string map = MultiplayerOptions.OptionType.Map.GetStrValue();
        string team1 = MultiplayerOptions.OptionType.CultureTeam1.GetStrValue();
        string team2 = MultiplayerOptions.OptionType.CultureTeam2.GetStrValue();
        string text = MissionFlow.GameType + " on " + MissionFlow.MapName(map) + " (" + map + "), " + team1 + " vs " + team2 + ".";
        string nextMap = MissionFlow.Next(MultiplayerOptions.OptionType.Map);
        string nextTeam1 = MissionFlow.Next(MultiplayerOptions.OptionType.CultureTeam1);
        string nextTeam2 = MissionFlow.Next(MultiplayerOptions.OptionType.CultureTeam2);
        if (nextMap != map || nextTeam1 != team1 || nextTeam2 != team2)
        {
            text += " Next: " + MissionFlow.MapName(nextMap) + ", " + nextTeam1 + " vs " + nextTeam2 + ".";
        }
        return text + (MissionFlow.WarmupHeld ? " Warmup is held." : "");
    }

    private sealed class Command
    {
        public Command(string name, string usage, string description, bool admin, Action<Caller, string[]> run)
        {
            Name = name;
            Usage = usage;
            Description = description;
            Admin = admin;
            Run = run;
        }

        public string Name { get; }
        public string Usage { get; }
        public string Description { get; }
        public bool Admin { get; }
        public Action<Caller, string[]> Run { get; }
    }

    // Who sent a command: a player (replies go to them privately) or the command pipe (replies go to the log).
    private sealed class Caller
    {
        private readonly NetworkCommunicator _peer;

        public Caller(NetworkCommunicator peer)
        {
            _peer = peer;
        }

        public string Name => _peer?.UserName ?? "console";

        public bool IsAdmin => _peer == null || _peer.IsAdmin;

        public void Reply(string text)
        {
            if (_peer == null)
            {
                MPManagerSubModule.Log(text);
            }
            else
            {
                Send(_peer, text);
            }
        }

        // Changes are shown to everyone, so all players know why the mission restarts.
        public void Announce(string text)
        {
            string line = Name + ": " + text;
            MPManagerSubModule.Log(line);
            foreach (NetworkCommunicator peer in GameNetwork.NetworkPeers)
            {
                Send(peer, line);
            }
        }

        private static void Send(NetworkCommunicator peer, string text)
        {
            if (peer.IsServerPeer || !peer.IsSynchronized)
            {
                return;
            }
            GameNetwork.BeginModuleEventAsServer(peer);
            GameNetwork.WriteMessage(new ServerMessage(text));
            GameNetwork.EndModuleEventAsServer();
        }
    }
}
