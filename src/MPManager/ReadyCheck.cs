using System;
using System.Collections.Generic;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace MPManager;

// !ready / !notready: when a player of each team is ready within ReadyWindowSeconds of each other, warmup ends
// with the game's 30 s countdown. Skirmish and Battle only, during warmup. A ready expires after the window
// (announced), and every mission starts with nobody ready. During a countdown started by !ready, !notready from
// either team cancels it and puts warmup back (like !warmup); a countdown started by !endwarmup is left alone.
internal static class ReadyCheck
{
    private const double ReadyWindowSeconds = 60;

    private static readonly HashSet<string> Modes = new HashSet<string> { "Skirmish", "Battle" };

    private static readonly Dictionary<BattleSideEnum, (string Player, DateTime At)> Ready = new Dictionary<BattleSideEnum, (string, DateTime)>();
    private static Mission _mission;
    private static Mission _countdownStartedByReady;

    public static void Tick()
    {
        if (Mission.Current != _mission)
        {
            _mission = Mission.Current;
            Ready.Clear();
            return;
        }
        foreach (BattleSideEnum side in new List<BattleSideEnum>(Ready.Keys))
        {
            if (DateTime.UtcNow > Ready[side].At.AddSeconds(ReadyWindowSeconds))
            {
                Ready.Remove(side);
                ServerChat.Broadcast(SideName(side) + " are no longer ready (nobody on the other team readied within " + ReadyWindowSeconds + " s).");
            }
        }
    }

    // Returns a private reply, or null when the result was announced to everyone.
    public static string SetReady(NetworkCommunicator peer, bool ready)
    {
        if (peer == null)
        {
            return "Only players on a team can ready.";
        }
        if (!Modes.Contains(MissionFlow.GameType))
        {
            return "!ready is for Skirmish and Battle.";
        }
        Team team = peer.GetComponent<MissionPeer>()?.Team;
        if (team == null || (team.Side != BattleSideEnum.Attacker && team.Side != BattleSideEnum.Defender))
        {
            return "Join a team first; spectators can't ready.";
        }
        BattleSideEnum side = team.Side;
        BattleSideEnum other = side == BattleSideEnum.Attacker ? BattleSideEnum.Defender : BattleSideEnum.Attacker;

        MissionFlow.WarmupStatus warmup = MissionFlow.Warmup();
        if (warmup == MissionFlow.WarmupStatus.Ending)
        {
            if (ready)
            {
                return "Warmup is already ending.";
            }
            if (_countdownStartedByReady != Mission.Current)
            {
                return "Warmup was ended by an admin; only !warmup can bring it back.";
            }
            _countdownStartedByReady = null;
            MissionFlow.StartHeldWarmup();
            ServerChat.Broadcast(peer.UserName + ": " + SideName(side) + " are not ready after all. Back to warmup; !ready again when set.");
            return null;
        }
        if (warmup != MissionFlow.WarmupStatus.Infinite)
        {
            return "Not in warmup.";
        }

        if (!ready)
        {
            if (!Ready.Remove(side))
            {
                return SideName(side) + " aren't ready.";
            }
            ServerChat.Broadcast(peer.UserName + ": " + SideName(side) + " are not ready after all.");
            return null;
        }

        Ready[side] = (peer.UserName, DateTime.UtcNow);
        if (Ready.ContainsKey(other))
        {
            Ready.Clear();
            _countdownStartedByReady = Mission.Current;
            ServerChat.Broadcast(peer.UserName + ": " + SideName(side) + " are ready too. " + MissionFlow.EndWarmup(force: false, out _)
                + " Changed your mind? !notready cancels the countdown.");
            return null;
        }
        ServerChat.Broadcast(peer.UserName + ": " + SideName(side) + " are ready. " + SideName(other) + ": type !ready within " + ReadyWindowSeconds + " s to start.");
        return null;
    }

    // "Vlandia (attackers)".
    private static string SideName(BattleSideEnum side)
    {
        string culture = (side == BattleSideEnum.Attacker ? MultiplayerOptions.OptionType.CultureTeam1 : MultiplayerOptions.OptionType.CultureTeam2).GetStrValue();
        string name = MissionFlow.Cultures().Find(c => c.Id == culture)?.Name ?? culture;
        return name + " (" + (side == BattleSideEnum.Attacker ? "attackers" : "defenders") + ")";
    }
}
