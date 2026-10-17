using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TaleWorlds.MountAndBlade;
using NetworkMessages.FromServer;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade.Diamond;

namespace MPManager;

// Chat commands ("!setmap lighthouse vla stur"; "/" only from the socket, clients never send it), from players in chat and from the control socket
// (mpm cmd /setmap lighthouse). They are never shown in chat (see ChatInterceptor). Who may run what, down to
// subcommands, is in the permissions file (Permissions); a command without a rule keeps its level below ("admin":
// players who joined with the admin password). The socket (mpm cmd) may run everything.
//
// Aliases come from MPM_ALIASES_FILE (~/bannerlord/aliases.txt, from mpServerConfig/server/aliases.txt), re-read when it
// changes: "sm = setmap" makes "!sm lighthouse" run "!setmap lighthouse"; an alias can include arguments
// ("ewf = endwarmup --force"), and anything typed after it is appended.
internal static class Commands
{
    private const double KickConfirmSeconds = 30;

    private static readonly ConcurrentQueue<(NetworkCommunicator Peer, string Text)> Pending = new ConcurrentQueue<(NetworkCommunicator, string)>();

    private static readonly List<Command> All = new List<Command>
    {
        new Command("help", "[command]", "List the commands with their aliases, or show one command's usage.", admin: false, Help),
        new Command("maps", "", "Numbered list of the maps for the current mode, variants in brackets.", admin: false, ListMaps),
        new Command("mode", "[mode] [map]", "Show the modes, or switch to one (optionally on a map) and restart.", admin: true, SwitchMode),
        new Command("bots", "[team1 team2]", "Show or set the number of bots per team (modes with bots only, e.g. tdm).", admin: true, Bots),
        new Command("specs", "", "List the spectators.", admin: false, Specs),
        new Command("players", "", "Everyone on the server with their numbers, which commands take instead of names (!kick 17).", admin: false, Players),
        new Command("ready", "", "Skirmish/Battle warmup: your team is ready. When both teams are ready within 60 s, warmup ends (30 s).", admin: false, Ready),
        new Command("notready", "", "Take back your team's !ready, or cancel the countdown it started.", admin: false, NotReady),
        new Command("setmap", "<map> [team1 team2]", "Switch to a map (and cultures) and restart. Partial or misspelled names, numbers from !maps, and current (c) for the same map work.", admin: true, SetMap),
        new Command("resetmap", "", "Restart the mission with the same map and cultures.", admin: true, ResetMap),
        new Command("warmup", "", "Back to warmup: cancels an ending warmup, or restarts the mission if the match has started.", admin: true, Warmup),
        new Command("endwarmup", "[--force]", "End this mission's warmup (infinite until then): 30 s countdown, or 3 s with --force.", admin: true, EndWarmup),
        new Command("kick", "<player> [--confirm]", "Kick the closest-named player; confirm with !confirm or --confirm.", admin: true, Kick),
        new Command("confirm", "", "Confirm a pending !kick.", admin: true, Confirm),
        new Command("tp", "<player> [to-player]", "Move a player to another player's position (!tp p1 p2), or yourself to a player (!tp p2).", admin: true, Teleport),
        new Command("kill", "[player]", "Kill yourself, or the closest-named player.", admin: true, Kill),
    };

    private static readonly Dictionary<string, string> Aliases = new Dictionary<string, string>();
    private static DateTime _aliasesLoadedFor = DateTime.MinValue;

    // Pending kicks, by who asked ("console" for mpm cmd): the player to kick and until when it can be confirmed.
    private static readonly Dictionary<string, (NetworkCommunicator Target, DateTime Until)> PendingKicks = new Dictionary<string, (NetworkCommunicator, DateTime)>();

    public static void Enqueue(NetworkCommunicator peer, string text)
    {
        Pending.Enqueue((peer, text));
    }

    // A chat command from the control socket, run at once as an admin; replies go to the log (and so back to the
    // socket's client, which captures them).
    public static void RunFromConsole(string text)
    {
        Caller caller = new Caller(null);
        try
        {
            Execute(caller, text);
        }
        catch (Exception e)
        {
            MPManagerSubModule.Log("Command failed: " + text + ": " + e);
            caller.Reply("That command failed; the server log has the details.");
        }
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
            ModCommands.Entry modCommand = ModCommands.Find(name);
            if (modCommand == null)
            {
                caller.Reply("Unknown command !" + name + ". !help lists the commands.");
                return;
            }
            if (!Permitted(caller, modCommand.Names, args, modCommand.AdminOnly, name))
            {
                return;
            }
            modCommand.Run(caller.Peer, args, caller.Reply);
            return;
        }
        if (!Permitted(caller, new[] { command.Name }, args, command.Admin, name))
        {
            return;
        }
        command.Run(caller, args);
    }

    private static Permissions.Level Default(bool admin) => admin ? Permissions.Level.Admin : Permissions.Level.Everyone;

    private static bool Allows(Caller caller, IReadOnlyList<string> names, IReadOnlyList<string> args, bool adminByDefault) =>
        Permissions.Allows(Permissions.Required(names, args, Default(adminByDefault)), caller.IsAdmin, caller.IsConsole);

    // Checks the permissions for a command with these arguments; tells the caller when they can't.
    private static bool Permitted(Caller caller, IReadOnlyList<string> names, string[] args, bool adminByDefault, string typed)
    {
        Permissions.Level required = Permissions.Required(names, args, Default(adminByDefault));
        if (Permissions.Allows(required, caller.IsAdmin, caller.IsConsole))
        {
            return true;
        }
        // "!b reset is for admins" when the command itself is open, "!kick is for admins" otherwise.
        string what = "!" + typed + (args.Length > 0 && Allows(caller, names, new string[0], adminByDefault) ? " " + args[0] : "");
        caller.Reply(what + " is " + Permissions.Describe(required) + ".");
        return false;
    }

    // Whether the caller may run the command itself (no arguments), and otherwise the subcommands they may run.
    private static (bool Root, List<string> Subcommands) Usable(Caller caller, IReadOnlyList<string> names, bool adminByDefault)
    {
        bool root = Allows(caller, names, new string[0], adminByDefault);
        List<string> subs = root ? new List<string>() : Permissions.AllowedSubcommands(names, caller.IsAdmin, caller.IsConsole)
            .Where(s => Allows(caller, names, s.Split(' '), adminByDefault))
            .ToList();
        return (root, subs);
    }

    internal static bool IsBuiltIn(string name) => All.Any(c => c.Name == name);

    // A chat command name without its prefix: built-in, alias, or a mod's (so "mpm cmd help" works; the game's
    // console commands are all different: start_game, end_mission...).
    internal static bool IsKnown(string name)
    {
        name = name.ToLowerInvariant();
        LoadAliases();
        return IsBuiltIn(name) || Aliases.ContainsKey(name) || ModCommands.Find(name) != null;
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
            if (All.Any(c => c.Name == alias) || ModCommands.Find(alias) != null)
            {
                MPManagerSubModule.Log("Alias '" + alias + "' ignored: that's a command name.");
                continue;
            }
            Aliases[alias] = target;
        }
        MPManagerSubModule.Log("Loaded " + Aliases.Count + " command alias(es) from " + path + ".");
    }

    // !help: one short line per command the caller can use (by the permissions), with its aliases
    // ("!endwarmup  (!ew, !ewf = --force)"); a command they can only use parts of lists those ("!mpb debug, speedo").
    // !help <command or alias>: that command's usage and description.
    private static void Help(Caller caller, string[] args)
    {
        LoadAliases();
        var builtIn = All.Select(c => (Command: c, Use: Usable(caller, new[] { c.Name }, c.Admin)))
            .Where(x => x.Use.Root || x.Use.Subcommands.Count > 0).ToList();
        var mods = ModCommands.All.Select(c => (Command: c, Use: Usable(caller, c.Names, c.AdminOnly)))
            .Where(x => x.Use.Root || x.Use.Subcommands.Count > 0).ToList();
        List<Command> available = builtIn.Select(x => x.Command).ToList();
        List<ModCommands.Entry> modAvailable = mods.Select(x => x.Command).ToList();
        if (args.Length > 0)
        {
            string name = args[0].TrimStart('!', '/').ToLowerInvariant();
            if (Aliases.TryGetValue(name, out string expansion))
            {
                name = expansion.Split(' ')[0].ToLowerInvariant();
            }
            Command command = available.FirstOrDefault(c => c.Name == name);
            ModCommands.Entry modCommand = modAvailable.FirstOrDefault(c => c.Names.Contains(name));
            if (command != null)
            {
                caller.Reply("!" + command.Name + (command.Usage.Length > 0 ? " " + command.Usage : "") + AliasText(command.Name));
                caller.Reply(command.Description);
                PartsText(caller, command.Name, builtIn.First(x => x.Command == command).Use);
            }
            else if (modCommand != null)
            {
                caller.Reply("!" + modCommand.Name + (modCommand.Usage.Length > 0 ? " " + modCommand.Usage : "") + AliasText(modCommand.Name, modCommand.Names.Skip(1)));
                caller.Reply(modCommand.Description + " (" + modCommand.Mod + ")");
                PartsText(caller, modCommand.Name, mods.First(x => x.Command == modCommand).Use);
            }
            else
            {
                caller.Reply("No command !" + args[0].TrimStart('!', '/') + ". !help lists them.");
            }
            return;
        }
        foreach (var (command, use) in builtIn)
        {
            caller.Reply("!" + command.Name + Only(use) + AliasText(command.Name));
        }
        foreach (var (modCommand, use) in mods)
        {
            caller.Reply("!" + modCommand.Name + Only(use) + AliasText(modCommand.Name, modCommand.Names.Skip(1)) + "  [" + modCommand.Mod + "]");
        }
        caller.Reply("!help <command> for details.");
    }

    // " debug, speedo" for a command the caller can only use parts of; "" otherwise.
    private static string Only((bool Root, List<string> Subcommands) use) => use.Root ? "" : " " + string.Join(", ", use.Subcommands);

    private static void PartsText(Caller caller, string name, (bool Root, List<string> Subcommands) use)
    {
        if (!use.Root)
        {
            caller.Reply("You can use: " + string.Join(", ", use.Subcommands.Select(s => "!" + name + " " + s)) + ".");
        }
    }

    // "  (!ew, !ewf = --force)", or "" without aliases; extra: other names the command has (a mod's "!mpb").
    private static string AliasText(string commandName, IEnumerable<string> extra = null)
    {
        List<string> aliases = (extra ?? Enumerable.Empty<string>()).Select(n => "!" + n).ToList();
        aliases.AddRange(Aliases
            .Where(a => a.Value.Split(' ')[0].Equals(commandName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => a.Value.Length).ThenBy(a => a.Key)
            .Select(a =>
            {
                string rest = a.Value.Substring(a.Value.Split(' ')[0].Length).Trim();
                return "!" + a.Key + (rest.Length > 0 ? " = " + rest : "");
            }));
        return aliases.Count == 0 ? "" : "  (" + string.Join(", ", aliases) + ")";
    }

    private static void ListMaps(Caller caller, string[] args)
    {
        List<MapGroup> groups = MapNames.Group(Modes.CurrentMaps());
        caller.Reply((Modes.Current?.Name ?? MissionFlow.GameType) + " maps (now: " + MapNames.Display(MultiplayerOptions.OptionType.Map.GetStrValue()) + "):");
        for (int i = 0; i < groups.Count; i++)
        {
            caller.Reply((i + 1) + ". " + groups[i]);
        }
    }

    private static void Specs(Caller caller, string[] args)
    {
        List<string> spectators = Peers.All
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
            map = MapNames.Choice(current);
        }
        else
        {
            List<Choice> maps = Modes.CurrentMaps();
            map = Matcher.Resolve(args[0], maps, out error, MapNames.Group(maps).Select(g => g.First).ToList());
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
            caller.Reply(modes.Count == 0 ? "No modes are set up (mpServerConfig/server/modes/)." : error);
            return;
        }
        Mode mode = modes.First(m => m.Name == choice.Id);
        string map = null;
        if (args.Length == 2)
        {
            List<Choice> maps = mode.Maps();
            Choice mapChoice = Matcher.Resolve(args[1], maps, out error, MapNames.Group(maps).Select(g => g.First).ToList());
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
        NetworkCommunicator target = FindPlayer(name, out string error);
        if (target == null)
        {
            caller.Reply(error);
            return;
        }
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

    // The connected player a command names: their number ("17" or "#17", see !players and PlayerNumIds), else the
    // name that matches best (see Matcher); null with the reason in error.
    private static NetworkCommunicator FindPlayer(string name, out string error)
    {
        List<NetworkCommunicator> players = Peers.Players();
        if (players.Count == 0)
        {
            error = "Nobody is on the server.";
            return null;
        }
        string digits = name.StartsWith("#") ? name.Substring(1) : name;
        if (digits.Length > 0 && digits.All(char.IsDigit) && int.TryParse(digits, out int number))
        {
            NetworkCommunicator numbered = players.FirstOrDefault(p => PlayerNumIds.Of(p) == number);
            error = numbered == null ? "Nobody here has number " + number + " (!players lists them)." : null;
            return numbered;
        }
        // Each choice's id is the player's number, so "matches Aesten (17), ..." shows what to type; numbers never
        // pick by position in this list.
        List<Choice> choices = players.Select((p, i) => new Choice(PlayerNumIds.Of(p)?.ToString() ?? "-" + i, p.UserName)).ToList();
        Choice choice = Matcher.Resolve(name, choices, out error, numbered: new List<Choice>());
        return choice == null ? null : players[choices.IndexOf(choice)];
    }

    // !players: everyone with their number, a few per line so long names don't run off the chat box.
    private static void Players(Caller caller, string[] args)
    {
        List<string> names = Peers.Players()
            .Where(p => p.IsSynchronized)
            .Select(p => (Number: PlayerNumIds.Of(p), Name: p.UserName))
            .OrderBy(p => p.Number ?? int.MaxValue)
            .Select(p => p.Number is int n ? "#" + n + " " + p.Name : p.Name)
            .ToList();
        if (names.Count == 0)
        {
            caller.Reply("Nobody is on the server.");
            return;
        }
        for (int i = 0; i < names.Count; i += 6)
        {
            caller.Reply(string.Join(", ", names.Skip(i).Take(6)));
        }
    }

    // The player's agent if they're alive in the mission, or null with the reason in error.
    private static Agent AliveAgent(NetworkCommunicator peer, out string error)
    {
        Agent agent = peer.GetComponent<MissionPeer>()?.ControlledAgent;
        if (agent == null || !agent.IsActive())
        {
            error = peer.UserName + " isn't alive right now.";
            return null;
        }
        error = null;
        return agent;
    }

    // !tp p1 p2: p1 (with their horse) to just behind p2, facing the same way. Clients are told with the game's
    // own teleport message (AgentTeleportToFrame, as UsableMachineAIBase does).
    private static void Teleport(Caller caller, string[] args)
    {
        if (args.Length != 1 && args.Length != 2)
        {
            caller.Reply("Usage: !tp <player> <to-player>, or !tp <to-player> to go there yourself.");
            return;
        }
        string error = null;
        NetworkCommunicator who = args.Length == 2 ? FindPlayer(args[0], out error) : caller.Peer;
        if (who == null)
        {
            caller.Reply(args.Length == 2 ? error : "Name who to move: !tp <player> <to-player>.");
            return;
        }
        NetworkCommunicator to = FindPlayer(args[args.Length - 1], out error);
        if (to == null)
        {
            caller.Reply(error);
            return;
        }
        if (to == who)
        {
            caller.Reply("That's the same player.");
            return;
        }
        Agent agent = AliveAgent(who, out error);
        Agent target = agent == null ? null : AliveAgent(to, out error);
        if (target == null)
        {
            caller.Reply(error);
            return;
        }
        Vec2 facing = target.LookDirection.AsVec2.Normalized();
        Vec3 position = target.Position - new Vec3(facing * 1.5f);
        agent.TeleportToPosition(position);
        agent.SetMovementDirection(in facing);
        GameNetwork.BeginBroadcastModuleEvent();
        GameNetwork.WriteMessage(new AgentTeleportToFrame(agent.Index, position, facing));
        GameNetwork.EndBroadcastModuleEvent(GameNetwork.EventBroadcastFlags.AddToMissionRecord);
        caller.Announce("Moved " + who.UserName + " to " + to.UserName + ".");
    }

    // !kill [player]: the same death the game deals to players who stay out of the map's boundary
    // (MissionBoundaryCrossingHandler); clients see an ordinary death.
    private static void Kill(Caller caller, string[] args)
    {
        string name = string.Join("", args);
        string error = null;
        NetworkCommunicator who = name.Length > 0 ? FindPlayer(name, out error) : caller.Peer;
        if (who == null)
        {
            caller.Reply(name.Length > 0 ? error : "Name who to kill: !kill <player>.");
            return;
        }
        Agent agent = AliveAgent(who, out error);
        if (agent == null)
        {
            caller.Reply(error);
            return;
        }
        Blow blow = new Blow(agent.Index);
        blow.WeaponRecord.FillAsMeleeBlow(null, null, -1, 0);
        blow.DamageType = DamageTypes.Blunt;
        blow.BaseMagnitude = 10000f;
        blow.WeaponRecord.WeaponClass = WeaponClass.Undefined;
        blow.GlobalPosition = agent.Position;
        blow.DamagedPercentage = 1f;
        agent.Die(blow);
        caller.Announce(who == caller.Peer ? "Killed themselves." : "Killed " + who.UserName + ".");
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

    // Who sent a command: a player (replies go to them privately) or mpm cmd (replies go to the log, and from there back to mpm).
    private sealed class Caller
    {
        public Caller(NetworkCommunicator peer)
        {
            Peer = peer;
        }

        public NetworkCommunicator Peer { get; }

        public string Name => Peer?.UserName ?? "console";

        public bool IsAdmin => Peer == null || Peer.IsAdmin;

        public bool IsConsole => Peer == null;

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
