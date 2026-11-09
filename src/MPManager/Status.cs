using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace MPManager;

// The server's state as one line of JSON, for programs (mpm status --json, mpm list --json, the desktop app): the
// control socket answers "@status" with it. Built on the main thread (CommandInput.Tick).
//
//   name, mode, gametype, map (id), map_name (display name), team1, team2 (culture ids), max_players,
//   state: starting (the game has no options yet: every other field null) | standby (empty, nothing loaded) |
//          intermission (between missions) | warmup | warmup_ending (counting down to the match) | playing |
//          ending (scoreboard),
//   players: [{ name, id (platform id, "2.0.0.<steam id>"), team: attacker | defender | spectator | none, admin }]
internal static class Status
{
    public const string Request = "@status";

    // MultiplayerOptions' singleton is created by the game once its game types are known; asking for it earlier (an
    // "@status" while the server starts) throws a NullReferenceException in its constructor, which ends a server
    // without sigkeep (TaleWorlds' signal handlers). Until the game has made it: state "starting", nothing else read.
    private static readonly System.Reflection.FieldInfo OptionsInstance = typeof(MultiplayerOptions)
        .GetField("_instance", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);

    internal static bool OptionsReady => OptionsInstance == null || OptionsInstance.GetValue(null) != null;

    public static string Json()
    {
        if (!OptionsReady)
        {
            return JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["name"] = null, ["mode"] = null, ["gametype"] = null, ["map"] = null, ["map_name"] = null,
                ["team1"] = null, ["team2"] = null, ["max_players"] = null, ["state"] = "starting",
                ["players"] = new List<object>(),
            });
        }
        string map = MultiplayerOptions.OptionType.Map.GetStrValue();
        var status = new Dictionary<string, object>
        {
            ["name"] = MultiplayerOptions.OptionType.ServerName.GetStrValue(),
            ["mode"] = Modes.Current?.Name,
            ["gametype"] = MissionFlow.GameType,
            ["map"] = map,
            ["map_name"] = string.IsNullOrEmpty(map) ? null : MapNames.Display(map),
            ["team1"] = MultiplayerOptions.OptionType.CultureTeam1.GetStrValue(),
            ["team2"] = MultiplayerOptions.OptionType.CultureTeam2.GetStrValue(),
            ["max_players"] = MultiplayerOptions.OptionType.MaxNumberOfPlayers.GetIntValue(),
            ["state"] = State(),
            ["players"] = Peers.Players().Select(Player).ToList(),
        };
        return JsonSerializer.Serialize(status);
    }

    // Also the state column of the server metrics (Metrics), so mpm list shows the same.
    internal static string State()
    {
        if (Standby.IsStandingBy)
        {
            return "standby";
        }
        Mission mission = Mission.Current;
        if (mission == null)
        {
            return "intermission";
        }
        if (mission.GetMissionBehavior<MissionLobbyComponent>()?.CurrentMultiplayerState == MissionLobbyComponent.MultiplayerGameState.Ending)
        {
            return "ending";
        }
        return MissionFlow.Warmup() switch
        {
            MissionFlow.WarmupStatus.Infinite => "warmup",
            MissionFlow.WarmupStatus.Ending => "warmup_ending",
            _ => "playing",
        };
    }

    private static Dictionary<string, object> Player(NetworkCommunicator peer)
    {
        Team team = peer.GetComponent<MissionPeer>()?.Team;
        string side = team == null ? "none"
            : team.Side == BattleSideEnum.Attacker ? "attacker"
            : team.Side == BattleSideEnum.Defender ? "defender"
            : "spectator";
        return new Dictionary<string, object>
        {
            ["name"] = peer.UserName,
            ["id"] = peer.VirtualPlayer?.Id.ToString(),
            ["team"] = side,
            ["admin"] = peer.IsAdmin,
        };
    }
}
