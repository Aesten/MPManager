using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.Diamond;

namespace MPManager;

// Chat commands ("!setmap lighthouse vla stur", or with "/"), from players in chat and from the command pipe
// (mpm cmd /setmap lighthouse). They are never shown in chat (see ChatInterceptor). Commands that change the
// server are for admins (players who joined with the admin password); the pipe counts as an admin.
//
// Aliases come from MPM_ALIASES_FILE (~/bannerlord/aliases.txt, from scripts/server/aliases.txt), re-read when it
// changes: "sm = setmap" makes "!sm lighthouse" run "!setmap lighthouse"; an alias can include arguments
// ("ewf = endwarmup --force"), and anything typed after it is appended.
internal static class Commands
{
    // Commands of other server mods: not answered with "unknown command" (they get the message too).
    private static readonly HashSet<string> OtherModsCommands = new HashSet<string> { "b", "mpb" };

    private const double KickConfirmSeconds = 30;

    private static readonly ConcurrentQueue<(NetworkCommunicator Peer, string Text)> Pending = new ConcurrentQueue<(NetworkCommunicator, string)>();

    private static readonly List<Command> All = new List<Command>
    {
        new Command("help", "[command]", "List the commands with their aliases, or show one command's usage.", admin: false, Help),
        new Command("maps", "", "Numbered list of the maps for the current mode.", admin: false, ListMaps),
        new Command("mode", "[mode] [map]", "Show the modes, or switch to one (optionally on a map) and restart.", admin: true, SwitchMode),
        new Command("bots", "[team1 team2]", "Show or set the number of bots per team (modes with bots only, e.g. tdm).", admin: true, Bots),
        new Command("specs", "", "List the spectators.", admin: false, Specs),
        new Command("ready", "", "Skirmish/Battle warmup: your team is ready. When both teams are ready within 60 s, warmup ends (30 s).", admin: false, Ready),
        new Command("notready", "", "Take back your team's !ready, or cancel the countdown it started.", admin: false, NotReady),
        new Command("setmap", "<map> [team1 team2]", "Switch to a map (and cultures) and restart. Partial or misspelled names, numbers from !maps, and current (c) for the same map work.", admin: true, SetMap),
        new Command("resetmap", "", "Restart the mission with the same map and cultures.", admin: true, ResetMap),
        new Command("warmup", "", "Back to warmup: cancels an ending warmup, or restarts the mission if the match has started.", admin: true, Warmup),
        new Command("endwarmup", "[--force]", "End this mission's warmup (infinite until then): 30 s countdown, or 3 s with --force.", admin: true, EndWarmup),
        new Command("kick", "<player> [--confirm]", "Kick the closest-named player; confirm with !confirm or --confirm.", admin: true, Kick),
        new Command("confirm", "", "Confirm a pending !kick.", admin: true, Confirm),
    };

    private static readonly Dictionary<string, string> Aliases = new Dictionary<string, string>();
    private static DateTime _aliasesLoadedFor = DateTime.MinValue;

    // Pending kicks, by who asked ("console" for the pipe): the player to kick and until when it can be confirmed.
    private static readonly Dictionary<string, (NetworkCommunicator Target, DateTime Until)> PendingKicks = new Dictionary<string, (NetworkCommunicator, DateTime)>();

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
            Caller caller = new Caller(item.Peer);
            try
            {
                Execute(caller, item.Text);
            }
            catch (Exception e)
            {
                MPManagerSubModule.Log("Command failed: " + item.Text + ": " + e);
                caller.Reply("That command failed; the server log has the details.");
            }
        }
    }

    private static void Execute(Caller caller, string text)
    {
        List<string> words = text.Substring(1).Split((char[])null, StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Count == 0)
        {
            return;
        }
        MPManagerSubModule.Log(caller.Name + ": " + text);

        string name = words[0].ToLowerInvariant();
        LoadAliases();
        if (Aliases.TryGetValue(name, out string expansion))
        {
            words.RemoveAt(0);
            words.InsertRange(0, expansion.Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
            name = words[0].ToLowerInvariant();
        }
        string[] args = words.Skip(1).ToArray();

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

    private static void LoadAliases()
    {
        string path = Environment.GetEnvironmentVariable("MPM_ALIASES_FILE");
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return;
        }
        DateTime modified = File.GetLastWriteTimeUtc(path);
        if (modified == _aliasesLoadedFor)
        {
            return;
        }
        _aliasesLoadedFor = modified;
        Aliases.Clear();
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            int equals = line.IndexOf('=');
            if (line.Length == 0 || line.StartsWith("#") || equals <= 0)
            {
                continue;
            }
            string alias = line.Substring(0, equals).Trim().TrimStart('!', '/').ToLowerInvariant();
            string target = line.Substring(equals + 1).Trim().TrimStart('!', '/');
            if (alias.Length == 0 || target.Length == 0 || alias.Contains(' '))
            {
                continue;
            }
            if (All.Any(c => c.Name == alias))
            {
                MPManagerSubModule.Log("Alias '" + alias + "' ignored: that's a command name.");
                continue;
            }
            Aliases[alias] = target;
        }
        MPManagerSubModule.Log("Loaded " + Aliases.Count + " command alias(es) from " + path + ".");
    }

    // !help: one short line per command the caller can use, with its aliases ("!endwarmup  (!ew, !ewf = --force)").
    // !help <command or alias>: that command's usage and description.
    private static void Help(Caller caller, string[] args)
    {
        LoadAliases();
        List<Command> available = All.Where(c => caller.IsAdmin || !c.Admin).ToList();
        if (args.Length > 0)
        {
            string name = args[0].TrimStart('!', '/').ToLowerInvariant();
            if (Aliases.TryGetValue(name, out string expansion))
            {
                name = expansion.Split(' ')[0].ToLowerInvariant();
            }
            Command command = available.FirstOrDefault(c => c.Name == name);
            if (command == null)
            {
                caller.Reply("No command !" + args[0].TrimStart('!', '/') + ". !help lists them.");
                return;
            }
            caller.Reply("!" + command.Name + (command.Usage.Length > 0 ? " " + command.Usage : "") + AliasText(command));
            caller.Reply(command.Description);
            return;
        }
        foreach (Command command in available)
        {
            caller.Reply("!" + command.Name + AliasText(command));
        }
        caller.Reply("!help <command> for details.");
    }

    // "  (!ew, !ewf = --force)", or "" without aliases.
    private static string AliasText(Command command)
    {
        List<string> aliases = Aliases
            .Where(a => a.Value.Split(' ')[0].Equals(command.Name, StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => a.Value.Length).ThenBy(a => a.Key)
            .Select(a =>
            {
                string extra = a.Value.Substring(a.Value.Split(' ')[0].Length).Trim();
                return "!" + a.Key + (extra.Length > 0 ? " = " + extra : "");
            })
            .ToList();
        return aliases.Count == 0 ? "" : "  (" + string.Join(", ", aliases) + ")";
    }

    private static void ListMaps(Caller caller, string[] args)
    {
        List<Choice> maps = Modes.CurrentMaps();
        caller.Reply((Modes.Current?.Name ?? MissionFlow.GameType) + " maps (now: " + MapNames.Display(MultiplayerOptions.OptionType.Map.GetStrValue()) + "):");
        for (int i = 0; i < maps.Count; i++)
        {
            caller.Reply((i + 1) + ". " + maps[i]);
        }
    }

    private static void Specs(Caller caller, string[] args)
    {
        List<string> spectators = GameNetwork.NetworkPeers
            .Where(p => p.IsSynchronized && !p.IsServerPeer)
            .Where(p =>
            {
                Team team = p.GetComponent<MissionPeer>()?.Team;
                return team == null || team == Mission.Current?.SpectatorTeam;
            })
            .Select(p => p.UserName)
            .ToList();
        caller.Reply(spectators.Count == 0 ? "No spectators." : spectators.Count + " spectator(s): " + string.Join(", ", spectators));
    }

    private static void SetMap(Caller caller, string[] args)
    {
        if (args.Length != 1 && args.Length != 3)
        {
            caller.Reply("Usage: !setmap <map> [team1 team2]   (map names without spaces; !maps lists them)");
            return;
        }
        string error = null;
        Choice map;
        if (args[0].Equals("current", StringComparison.OrdinalIgnoreCase) || args[0].Equals("c", StringComparison.OrdinalIgnoreCase))
        {
            string current = MultiplayerOptions.OptionType.Map.GetStrValue();
            map = new Choice(current, MapNames.For(current));
        }
        else
        {
            map = Matcher.Resolve(args[0], Modes.CurrentMaps(), out error);
        }
        if (map == null)
        {
            caller.Reply(error);
            return;
        }
        Choice team1 = null, team2 = null;
        if (args.Length == 3)
        {
            List<Choice> cultures = MissionFlow.Cultures();
            team1 = Matcher.Resolve(args[1], cultures, out error);
            team2 = team1 == null ? null : Matcher.Resolve(args[2], cultures, out error);
            if (team2 == null)
            {
                caller.Reply(error);
                return;
            }
        }
        MissionFlow.Change(map.Id, team1?.Id, team2?.Id);
        caller.Announce("Switching to " + map.Name + (team1 != null ? ", " + team1.Name + " vs " + team2.Name : "") + ".");
    }

    private static void Ready(Caller caller, string[] args)
    {
        string reply = ReadyCheck.SetReady(caller.Peer, ready: true);
        if (reply != null)
        {
            caller.Reply(reply);
        }
    }

    private static void NotReady(Caller caller, string[] args)
    {
        string reply = ReadyCheck.SetReady(caller.Peer, ready: false);
        if (reply != null)
        {
            caller.Reply(reply);
        }
    }

    private static void SwitchMode(Caller caller, string[] args)
    {
        IReadOnlyList<Mode> modes = Modes.List();
        if (args.Length == 0)
        {
            caller.Reply("Mode: " + (Modes.Current?.Name ?? MissionFlow.GameType + " (no mode file)") + ".");
            foreach (Mode m in modes)
            {
                caller.Reply("!mode " + m.Name + (m.Aliases.Count > 0 ? "  (" + string.Join(", ", m.Aliases) + ")" : "") + ": " + m.GameType + (m.BotsAllowed ? ", bots" : ""));
            }
            return;
        }
        if (args.Length > 2)
        {
            caller.Reply("Usage: !mode <mode> [map]");
            return;
        }
        Choice choice = Matcher.Resolve(args[0], modes.Select(m => m.ToChoice()).ToList(), out string error);
        if (choice == null)
        {
            caller.Reply(modes.Count == 0 ? "No modes are set up (scripts/server/modes/)." : error);
            return;
        }
        Mode mode = modes.First(m => m.Name == choice.Id);
        string map = null;
        if (args.Length == 2)
        {
            Choice mapChoice = Matcher.Resolve(args[1], mode.Maps(), out error);
            if (mapChoice == null)
            {
                caller.Reply(error);
                return;
            }
            map = mapChoice.Id;
        }
        string failure = Modes.Apply(mode, map, current: Mission.Current == null);
        if (failure != null)
        {
            caller.Reply(failure);
            return;
        }
        MissionFlow.Restart();
        caller.Announce("Switching to " + mode.Name + " on " + MapNames.Display(MissionFlow.Next(MultiplayerOptions.OptionType.Map)) + ".");
    }

    private static void Bots(Caller caller, string[] args)
    {
        if (Modes.Current?.BotsAllowed != true)
        {
            caller.Reply("No bots in this mode" + (Modes.List().Any(m => m.BotsAllowed) ? "; switch to " + string.Join(" or ", Modes.List().Where(m => m.BotsAllowed).Select(m => m.Name)) + "." : "."));
            return;
        }
        if (args.Length == 0)
        {
            caller.Reply("Bots: " + MultiplayerOptions.OptionType.NumberOfBotsTeam1.GetIntValue() + " (team 1) vs "
                + MultiplayerOptions.OptionType.NumberOfBotsTeam2.GetIntValue() + " (team 2); in the game now: "
                + MissionFlow.CountBots(TaleWorlds.Core.BattleSideEnum.Attacker) + " vs " + MissionFlow.CountBots(TaleWorlds.Core.BattleSideEnum.Defender) + ".");
            return;
        }
        if (args.Length != 2 || !int.TryParse(args[0], out int team1) || !int.TryParse(args[1], out int team2) || team1 < 0 || team2 < 0 || team1 > 255 || team2 > 255)
        {
            caller.Reply("Usage: !bots <team1> <team2>, 0 to 255 each.");
            return;
        }
        MissionFlow.SetBots(team1, team2);
        caller.Announce("Bots: " + team1 + " vs " + team2 + ".");
    }

    private static void ResetMap(Caller caller, string[] args)
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
        caller.Announce(MissionFlow.StartHeldWarmup());
    }

    private static void EndWarmup(Caller caller, string[] args)
    {
        bool force = args.Any(a => a.Equals("--force", StringComparison.OrdinalIgnoreCase));
        string result = MissionFlow.EndWarmup(force, out bool ended);
        if (ended)
        {
            caller.Announce(result);
        }
        else
        {
            caller.Reply(result);
        }
    }

    private static void Kick(Caller caller, string[] args)
    {
        bool confirmed = args.Any(a => a.Equals("--confirm", StringComparison.OrdinalIgnoreCase));
        string name = string.Join("", args.Where(a => !a.StartsWith("--")));
        if (name.Length == 0)
        {
            caller.Reply("Usage: !kick <player> [--confirm]");
            return;
        }
        List<NetworkCommunicator> players = GameNetwork.NetworkPeers.Where(p => !p.IsServerPeer).ToList();
        List<Choice> choices = players.Select((p, i) => new Choice(i.ToString(), p.UserName)).ToList();
        Choice choice = Matcher.Resolve(name, choices, out string error);
        if (choice == null)
        {
            caller.Reply(players.Count == 0 ? "Nobody to kick." : error);
            return;
        }
        NetworkCommunicator target = players[int.Parse(choice.Id)];
        if (target == caller.Peer)
        {
            caller.Reply("That's you.");
            return;
        }
        if (confirmed)
        {
            KickNow(caller, target);
            return;
        }
        PendingKicks[caller.Name] = (target, DateTime.UtcNow.AddSeconds(KickConfirmSeconds));
        caller.Reply("Kick " + target.UserName + "? Type !confirm within " + KickConfirmSeconds + " s.");
    }

    private static void Confirm(Caller caller, string[] args)
    {
        if (!PendingKicks.TryGetValue(caller.Name, out var pending) || DateTime.UtcNow > pending.Until)
        {
            PendingKicks.Remove(caller.Name);
            caller.Reply("Nothing to confirm.");
            return;
        }
        PendingKicks.Remove(caller.Name);
        if (!pending.Target.IsConnectionActive)
        {
            caller.Reply(pending.Target.UserName + " already left.");
            return;
        }
        KickNow(caller, pending.Target);
    }

    // What the game does for an admin-panel kick (MultiplayerAdminComponent.KickPlayer), minus its "not an admin" rule.
    private static void KickNow(Caller caller, NetworkCommunicator target)
    {
        DisconnectInfo info = target.PlayerConnectionInfo.GetParameter<DisconnectInfo>("DisconnectInfo") ?? new DisconnectInfo();
        info.Type = DisconnectType.KickedByHost;
        target.PlayerConnectionInfo.AddParameter("DisconnectInfo", info);
        GameNetwork.AddNetworkPeerToDisconnectAsServer(target);
        caller.Announce("Kicked " + target.UserName + ".");
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
        public Caller(NetworkCommunicator peer)
        {
            Peer = peer;
        }

        public NetworkCommunicator Peer { get; }

        public string Name => Peer?.UserName ?? "console";

        public bool IsAdmin => Peer == null || Peer.IsAdmin;

        public void Reply(string text)
        {
            if (Peer == null)
            {
                MPManagerSubModule.Log(text);
            }
            else
            {
                ServerChat.Send(Peer, text);
            }
        }

        // Changes are shown to everyone, so all players know why the mission restarts.
        public void Announce(string text)
        {
            ServerChat.Broadcast(Name + ": " + text);
        }
    }
}
