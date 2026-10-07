using JetBrains.Annotations;
using TaleWorlds.MountAndBlade;

namespace MPManager;

// Server console commands, registered with DedicatedServerConsoleCommandManager so they also show up in "list".
// They run on the main thread. Names start with "mpm_" to keep them apart from the game's own commands.
internal static class ConsoleCommands
{
    public static void Register()
    {
        DedicatedServerConsoleCommandManager.AddType(typeof(ConsoleCommands));
    }

    [UsedImplicitly]
    [ConsoleCommandMethod("mpm_lobby", "Shows the state of the connection to TaleWorlds' lobby and of MPManager's reconnecting.")]
    private static void Lobby()
    {
        MPManagerSubModule.Log(LobbyReconnect.Status());
    }

    [UsedImplicitly]
    [ConsoleCommandMethod("mpm_lobby_disconnect", "Drops the lobby connection on purpose, to test that MPManager reconnects without a restart. Players in the game are returned to the lobby about two minutes later.")]
    private static void LobbyDisconnect()
    {
        MPManagerSubModule.Log(LobbyReconnect.SimulateDisconnect());
    }
}
