using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.ListedServer;

namespace MPManager;

// Players coming and going: their joins and leaves are announced in chat, and an empty server stands by.
//
// Standby: once nobody has been on for EmptyGraceSeconds (time to reconnect after a crash), the mission ends and
// the server waits between missions, with nothing loaded (no bots fighting, no scene), by holding the
// intermission countdown (ServerSideIntermissionManager counts down to the next mission in CountingForNextBattle).
// When someone joins, the countdown is let go and the next mission starts after the usual intermission
// (settings.txt), in warmup.
internal static class Standby
{
    private const double EmptyGraceSeconds = 60;

    private static readonly FieldInfo RemainingTimeField = typeof(ServerSideIntermissionManager)
        .GetField("_currentAutomatedBattleRemainingTime", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly HashSet<NetworkCommunicator> Known = new HashSet<NetworkCommunicator>();
    private static DateTime? _emptySince;
    private static bool _standingBy;

    public static bool IsStandingBy => _standingBy;

    public static void Tick()
    {
        List<NetworkCommunicator> players = Peers.Players();
        AnnounceJoinsAndLeaves(players);

        if (players.Count > 0)
        {
            _emptySince = null;
            if (_standingBy)
            {
                _standingBy = false;
                MPManagerSubModule.Log("Someone joined: leaving standby, the next mission starts.");
            }
            return;
        }
        _emptySince ??= DateTime.UtcNow;
        ServerSideIntermissionManager manager = ServerSideIntermissionManager.Instance;
        if (manager == null || RemainingTimeField == null)
        {
            return;
        }
        if (Mission.Current != null)
        {
            if (!_standingBy && (DateTime.UtcNow - _emptySince.Value).TotalSeconds >= EmptyGraceSeconds && MissionFlow.Restart())
            {
                _standingBy = true;
                MPManagerSubModule.Log("Nobody on for " + EmptyGraceSeconds + " s: ending the mission and standing by until someone joins.");
            }
            return;
        }
        if (manager.AutomatedBattleState == AutomatedBattleState.CountingForNextBattle)
        {
            if (!_standingBy)
            {
                _standingBy = true;
                MPManagerSubModule.Log("Nobody on: standing by between missions until someone joins.");
            }
            RemainingTimeField.SetValue(manager, Settings.IntermissionSeconds);
        }
    }

    private static void AnnounceJoinsAndLeaves(List<NetworkCommunicator> players)
    {
        foreach (NetworkCommunicator peer in players.Where(p => !Known.Contains(p)).ToList())
        {
            Known.Add(peer);
            PlayerNumIds.Of(peer); // their number from now on, for !players and other mods
            ServerChat.Broadcast(peer.UserName + " joined.");
        }
        foreach (NetworkCommunicator peer in Known.Where(p => !players.Contains(p)).ToList())
        {
            Known.Remove(peer);
            ServerChat.Broadcast(peer.UserName + " left.");
        }
    }
}
