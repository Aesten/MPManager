using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace MPManager;

// Riderless horses leave the map instead of wandering around it for the rest of the round: once a horse's rider
// is dead (or it never had one), it's sent running to the nearest edge of the map with the game's own retreat
// order; what happens there is up to the game. A horse whose rider dismounted and is still alive stays, so
// players can get back on; it leaves once that rider dies.
//
// Removed agents are never touched (their native data may be gone, and on the Linux server a bad access is a
// crash): a rider counts as alive while it's in the mission's list of active agents.
internal static class Horses
{
    private const double CheckIntervalSeconds = 0.5;

    private sealed class State
    {
        public Agent LastRider;
        public bool SentAway;
    }

    private static readonly Dictionary<Agent, State> Mounts = new Dictionary<Agent, State>();
    private static Mission _mission;
    private static DateTime _nextCheck;

    public static void Tick()
    {
        Mission mission = Mission.Current;
        if (mission != _mission)
        {
            _mission = mission;
            Mounts.Clear();
        }
        if (mission?.Scene == null || DateTime.UtcNow < _nextCheck)
        {
            return;
        }
        _nextCheck = DateTime.UtcNow.AddSeconds(CheckIntervalSeconds);

        HashSet<Agent> active = new HashSet<Agent>(mission.Agents.Where(a => a != null));
        foreach (Agent gone in Mounts.Keys.Where(m => !active.Contains(m)).ToList())
        {
            Mounts.Remove(gone);
        }
        foreach (Agent mount in active.Where(a => a.IsMount).ToList())
        {
            if (!Mounts.TryGetValue(mount, out State state))
            {
                Mounts[mount] = state = new State();
            }
            Agent rider = mount.RiderAgent;
            if (rider != null)
            {
                state.LastRider = rider;
                state.SentAway = false;
                continue;
            }
            if (state.LastRider != null && active.Contains(state.LastRider))
            {
                continue; // dismounted, rider alive: the horse waits
            }
            if (state.SentAway)
            {
                continue;
            }
            state.SentAway = true;
            Vec3 position = mount.Position;
            Vec2 edge = mission.GetClosestBoundaryPosition(position.AsVec2);
            mount.Retreat(new WorldPosition(mission.Scene, new Vec3(edge, position.z)));
        }
    }
}
