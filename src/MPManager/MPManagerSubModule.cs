using System;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace MPManager;

public class MPManagerSubModule : MBSubModuleBase
{
    private static bool _failed;

    protected override void OnSubModuleLoad()
    {
        base.OnSubModuleLoad();
        ServerLog.Open();
        LogFilter.Install();
        CommandInput.Start();
        // After the server config is applied, before the first mission.
        TaleWorlds.MountAndBlade.ListedServer.InitialListedGameServerState.OnActivated += () =>
        {
            try
            {
                Modes.ApplyDefault();
            }
            catch (Exception e)
            {
                Log("Could not apply the default mode: " + e);
            }
        };
        Log("Loaded.");
    }

    // Runs every frame, in missions and between them, on the main thread.
    protected override void OnApplicationTick(float dt)
    {
        base.OnApplicationTick(dt);
        try
        {
            Metrics.Tick();
        }
        catch (Exception e)
        {
            Metrics.Disable();
            Log("Metrics stopped after an error: " + e);
        }
        CommandInput.Tick();
        if (_failed)
        {
            return;
        }
        try
        {
            ChatInterceptor.Tick();
            Commands.Tick();
            MissionFlow.Tick();
            ReadyCheck.Tick();
            Standby.Tick();
            Horses.Tick();
        }
        catch (Exception e)
        {
            // Never take the server down over our own logic; switch it off instead.
            _failed = true;
            Log("Stopped after an error; chat commands and mission flow are off until restart: " + e);
        }
    }

    internal static void Log(string message)
    {
        Debug.Print("[MPManager] " + message, 0, Debug.DebugColor.Green);
    }
}
