using NetworkMessages.FromServer;
using TaleWorlds.MountAndBlade;

namespace MPManager;

// Server messages in chat. Only call from the main thread outside packet handling (a tick or a queued command):
// sending from inside the server's packet handler crashes it.
internal static class ServerChat
{
    public static void Send(NetworkCommunicator peer, string text)
    {
        if (peer.IsServerPeer || !peer.IsSynchronized)
        {
            return;
        }
        GameNetwork.BeginModuleEventAsServer(peer);
        GameNetwork.WriteMessage(new ServerMessage(text));
        GameNetwork.EndModuleEventAsServer();
    }

    // To everyone, and to the log.
    public static void Broadcast(string text)
    {
        MPManagerSubModule.Log(text);
        foreach (NetworkCommunicator peer in Peers.All)
        {
            Send(peer, text);
        }
    }
}
