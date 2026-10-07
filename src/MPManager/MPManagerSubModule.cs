using System;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace MPManager;

public class MPManagerSubModule : MBSubModuleBase
{
    private static bool _lobbyReconnectFailed;

    protected override void OnSubModuleLoad()
    {
        base.OnSubModuleLoad();
        ConsoleCommands.Register();
        Log("Loaded.");
    }

    // Runs every frame, in missions and between them, on the main thread.
    protected override void OnApplicationTick(float dt)
    {
        base.OnApplicationTick(dt);
        if (_lobbyReconnectFailed)
        {
            return;
        }
        try
        {
            LobbyReconnect.Tick();
        }
        catch (Exception e)
        {
            // Never take the server down over our own logic; switch the feature off instead.
            _lobbyReconnectFailed = true;
            Log("Lobby reconnect failed and is now off: " + e);
        }
    }

    internal static void Log(string message)
    {
        Debug.Print("[MPManager] " + message, 0, Debug.DebugColor.Green);
    }
}
