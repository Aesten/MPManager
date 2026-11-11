using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.DedicatedCustomServer;
using TaleWorlds.MountAndBlade.Diamond;

namespace MPManager;

// Keeps the game type and map the server is registered with (TaleWorlds' lobby) equal to the mission it runs.
//
// TaleWorlds creates each player's battle stats with the type the server is registered with
// (CustomBattleServer.CustomGameType, MissionCustomGameServerComponent: AddOrUpdatePlayerEntry) and, at mission end,
// casts them to the running mission's type (AddScoresToStats: "as BattlePlayerStatsTeamDeathmatch" / Siege). The
// registered type only changes through UpdateGameProperties, which TaleWorlds' intermission manager calls in some of
// its own flows, not when a mode is applied by setting the GameType option (Modes.Apply). After "registered as
// Skirmish, switched to TDM", the cast gives null and the first mission end with a player on throws a
// NullReferenceException that ends the server (found 8 Nov 2026 on Testground: every !setmap and the end of standby
// crashed). Re-registering at the start of every mission, before anyone can join a team, keeps them equal; it also
// keeps TaleWorlds' server list showing the mode and map actually played.
internal static class RegisteredGame
{
    public static void Sync()
    {
        CustomBattleServer server = DedicatedCustomServerSubModule.Instance?.DedicatedCustomGameServer;
        if (server == null || !server.IsRegistered)
        {
            return;
        }
        var current = MultiplayerOptions.MultiplayerOptionsAccessMode.CurrentMapOptions;
        string gameType = MultiplayerOptions.OptionType.GameType.GetStrValue(current);
        string map = MultiplayerOptions.OptionType.Map.GetStrValue(current);
        if (string.IsNullOrEmpty(gameType) || (server.CustomGameType == gameType && server.CustomGameScene == map))
        {
            return;
        }
        string uniqueMapId = Utilities.TryGetUniqueIdentifiersForScene(map, out UniqueSceneId ids) ? ids.Serialize() : null;
        string was = server.CustomGameType + " on " + server.CustomGameScene;
        // v1.5.7: the streamer slots and password go with it, as the game's own update sends them (its handler keeps the
        // password's MD5 in the option once sent: a 32-hex value is that hash already).
        string streamerPassword = MultiplayerOptions.OptionType.StreamerPassword.GetStrValue(current);
        if (!string.IsNullOrEmpty(streamerPassword) && !System.Text.RegularExpressions.Regex.IsMatch(streamerPassword, "^[0-9a-f]{32}$"))
        {
            streamerPassword = Common.CalculateMD5Hash(streamerPassword);
        }
        server.UpdateGameProperties(gameType, map, uniqueMapId, MultiplayerOptions.OptionType.MaxStreamerCount.GetIntValue(current),
            string.IsNullOrEmpty(streamerPassword) ? null : streamerPassword);
        MPManagerSubModule.Log("Registered game updated: " + was + " -> " + gameType + " on " + map + ".");
    }
}
