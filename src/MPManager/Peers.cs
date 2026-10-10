using System.Collections.Generic;
using System.Linq;
using TaleWorlds.MountAndBlade;

namespace MPManager;

// GameNetwork.NetworkPeers, safely: it's null until the server's networking starts (MPManager ticks during
// loading too). On the Linux server a null dereference doesn't throw: the game's own signal handler turns it
// into a crash, so it must never happen.
internal static class Peers
{
    private static readonly List<NetworkCommunicator> None = new List<NetworkCommunicator>();

    public static List<NetworkCommunicator> All => GameNetwork.NetworkPeersValid ? GameNetwork.NetworkPeers : None;

    // Connected players (everyone but the server itself).
    public static List<NetworkCommunicator> Players() => All.Where(p => !p.IsServerPeer).ToList();
}
