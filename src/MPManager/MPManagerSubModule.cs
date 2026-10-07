using System;
using HarmonyLib;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace MPManager;

public class MPManagerSubModule : MBSubModuleBase
{
    private static Harmony _harmony;

    protected override void OnSubModuleLoad()
    {
        base.OnSubModuleLoad();
        try
        {
            _harmony = new Harmony("mpmanager");
            LobbyReconnect.Install(_harmony);
            ConsoleCommands.Register();
            Log("Loaded (Harmony " + typeof(Harmony).Assembly.GetName().Version + ").");
        }
        catch (Exception e)
        {
            // Never take the server down over our own setup; it just runs without our patches.
            Log("Failed to install patches, running without them: " + e);
        }
    }

    // Runs every frame, in missions and between them, on the main thread.
    protected override void OnApplicationTick(float dt)
    {
        base.OnApplicationTick(dt);
        LobbyReconnect.Tick();
    }

    internal static void Log(string message)
    {
        Debug.Print("[MPManager] " + message, 0, Debug.DebugColor.Green);
    }
}
