using System;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.DedicatedCustomServer;
using TaleWorlds.MountAndBlade.Diamond;
using TaleWorlds.MountAndBlade.ListedServer;

namespace MPManager;

// Keeps a registered custom server running when its connection to TaleWorlds' lobby drops.
//
// Vanilla, CustomBattleServer is built with autoReconnect: false, and DedicatedCustomGameServerHandler
// answers OnDisconnected/OnCantConnect with Module.ShutDownWithDelay(20). A keepalive request failing
// 5 retries is enough to shut the server down mid-match. Players already in the game talk to the server
// directly; only new joins go through the lobby.
//
// Once a game has been registered, this skips that shutdown, reconnects with the arguments of the original
// Connect call, and registers the game again with the arguments of the last RegisterGame call (updated to the
// current game type, map and player cap). Before the first registration, the vanilla shutdown still applies.
//
// The lobby callbacks only raise a flag; all work happens in Tick, on the main thread.
internal static class LobbyReconnect
{
    private enum Phase
    {
        Idle,       // connected, or never registered
        Waiting,    // until _deadline, then connect (or just register, if already logged in)
        Connecting, // until logged in (RegisteredServer), or give up at _deadline
        Registering // RegisterGame in flight
    }

    private const double FirstRetryDelaySeconds = 5;
    private const double MaxRetryDelaySeconds = 60;
    private const double LoginTimeoutSeconds = 60;

    private static CustomBattleServer _server;

    // Arguments of the latest RegisterGame call, and of the latest one that succeeded.
    private static object[] _pendingRegisterArgs;
    private static object[] _registerArgs;

    private static Phase _phase;
    private static DateTime _deadline;
    private static double _retryDelay = FirstRetryDelaySeconds;
    private static int _attempt;

    private static volatile bool _connectionLost;
    private static Task _registerTask;

    public static void Install(Harmony harmony)
    {
        InterfaceMapping map = typeof(DedicatedCustomGameServerHandler).GetInterfaceMap(typeof(ICustomBattleServerSessionHandler));
        HarmonyMethod skipShutdown = new HarmonyMethod(typeof(LobbyReconnect), nameof(SkipShutdownPrefix));
        int patched = 0;
        for (int i = 0; i < map.InterfaceMethods.Length; i++)
        {
            string name = map.InterfaceMethods[i].Name;
            if (name == nameof(ICustomBattleServerSessionHandler.OnDisconnected) || name == nameof(ICustomBattleServerSessionHandler.OnCantConnect))
            {
                harmony.Patch(map.TargetMethods[i], prefix: skipShutdown);
                patched++;
            }
        }
        if (patched != 2)
        {
            throw new InvalidOperationException("Expected 2 lobby shutdown handlers, found " + patched + ".");
        }

        MethodInfo registerGame = AccessTools.Method(typeof(CustomBattleServer), nameof(CustomBattleServer.RegisterGame), new[]
        {
            typeof(int), typeof(string), typeof(string), typeof(string), typeof(int), typeof(string), typeof(string),
            typeof(int), typeof(string), typeof(string), typeof(string), typeof(int), typeof(string)
        }) ?? throw new InvalidOperationException("CustomBattleServer.RegisterGame not found.");
        harmony.Patch(registerGame, prefix: new HarmonyMethod(typeof(LobbyReconnect), nameof(CaptureRegisterGamePrefix)));
    }

    // Prefix of DedicatedCustomGameServerHandler's OnDisconnected and OnCantConnect.
    private static bool SkipShutdownPrefix()
    {
        if (_registerArgs == null)
        {
            return true; // never registered a game: vanilla shutdown
        }
        _connectionLost = true;
        return false;
    }

    // Prefix of CustomBattleServer.RegisterGame (the full overload, which the short one calls).
    private static void CaptureRegisterGamePrefix(CustomBattleServer __instance, object[] __args)
    {
        _server = __instance;
        _pendingRegisterArgs = (object[])__args.Clone();
    }

    public static void Tick()
    {
        if (_server == null)
        {
            return;
        }
        if (_phase == Phase.Idle && _pendingRegisterArgs != null && _server.CurrentState == CustomBattleServer.State.RegisteredGame)
        {
            _registerArgs = _pendingRegisterArgs;
        }

        if (_connectionLost)
        {
            _connectionLost = false;
            OnConnectionLost();
        }

        switch (_phase)
        {
        case Phase.Waiting:
            if (DateTime.UtcNow >= _deadline)
            {
                if (_server.CurrentState == CustomBattleServer.State.RegisteredServer)
                {
                    StartRegister();
                }
                else
                {
                    StartConnect();
                }
            }
            break;
        case Phase.Connecting:
            if (_server.CurrentState == CustomBattleServer.State.RegisteredServer)
            {
                StartRegister();
            }
            else if (DateTime.UtcNow >= _deadline)
            {
                // A rejected login leaves the client waiting forever (CustomBattleServer.DoLogin only prints).
                MPManagerSubModule.Log("Lobby login did not complete within " + LoginTimeoutSeconds + " s.");
                ForceDisconnect();
                ScheduleRetry();
            }
            break;
        case Phase.Registering:
            if (_registerTask.IsCompleted)
            {
                OnRegisterFinished();
            }
            break;
        }
    }

    private static void OnConnectionLost()
    {
        switch (_phase)
        {
        case Phase.Idle:
            _attempt = 0;
            _retryDelay = FirstRetryDelaySeconds;
            MPManagerSubModule.Log("Lost the connection to the lobby. Keeping the server running; players in the game are not affected, but nobody new can join until it reconnects.");
            ScheduleRetry();
            break;
        case Phase.Connecting:
        case Phase.Registering:
            MPManagerSubModule.Log("Reconnect attempt " + _attempt + " failed.");
            ScheduleRetry();
            break;
        case Phase.Waiting:
            break; // already scheduled
        }
    }

    private static void ScheduleRetry()
    {
        _phase = Phase.Waiting;
        _deadline = DateTime.UtcNow.AddSeconds(_retryDelay);
        MPManagerSubModule.Log("Reconnecting to the lobby in " + _retryDelay + " s.");
        _retryDelay = Math.Min(_retryDelay * 2, MaxRetryDelaySeconds);
    }

    private static void StartConnect()
    {
        _attempt++;
        MPManagerSubModule.Log("Reconnect attempt " + _attempt + ": connecting to the lobby.");
        try
        {
            // The same arguments DedicatedCustomServerIntermissionManagerHandler passed at startup, as stored by Connect.
            Traverse server = Traverse.Create(_server);
            _server.Connect(
                server.Field("_handler").GetValue<ICustomBattleServerSessionHandler>(),
                server.Field("_authToken").GetValue<string>(),
                server.Field("_isSinglePlatformServer").GetValue<bool>(),
                Utilities.GetModulesNames(),
                server.Field("_allowsOptionalModules").GetValue<bool>(),
                !server.Field("_useTimeoutTimer").GetValue<bool>());
            _phase = Phase.Connecting;
            _deadline = DateTime.UtcNow.AddSeconds(LoginTimeoutSeconds);
        }
        catch (Exception e)
        {
            MPManagerSubModule.Log("Reconnect attempt " + _attempt + " failed: " + e.Message);
            ScheduleRetry();
        }
    }

    private static void StartRegister()
    {
        // Parameters: gameDefinitionId, gameModule, gameType, serverName, maxPlayerCount, scene, uniqueSceneId,
        // port, region, gamePassword, adminPassword, permission, overriddenIP. Passwords were already hashed by
        // start_game, so they are passed on as stored.
        object[] a = (object[])_registerArgs.Clone();
        string gameType = MultiplayerOptions.OptionType.GameType.GetStrValue();
        string map = MultiplayerOptions.OptionType.Map.GetStrValue();
        MultiplayerGameTypeInfo gameTypeInfo = MultiplayerGameTypes.GetGameTypeInfo(gameType);
        if (gameTypeInfo != null)
        {
            a[1] = gameTypeInfo.GameModule;
            a[2] = gameType;
        }
        a[4] = MultiplayerOptions.OptionType.MaxNumberOfPlayers.GetIntValue();
        a[5] = map;
        a[6] = Utilities.TryGetUniqueIdentifiersForScene(map, out UniqueSceneId ids) ? ids.Serialize() : null;

        MPManagerSubModule.Log("Registering the game again (" + a[2] + " on " + a[5] + ").");
        try
        {
            _registerTask = _server.RegisterGame((int)a[0], (string)a[1], (string)a[2], (string)a[3], (int)a[4], (string)a[5], (string)a[6],
                (int)a[7], (string)a[8], (string)a[9], (string)a[10], (int)a[11], (string)a[12]);
            _phase = Phase.Registering;
        }
        catch (Exception e)
        {
            MPManagerSubModule.Log("Registering the game failed: " + e.Message);
            ScheduleRetry();
        }
    }

    private static void OnRegisterFinished()
    {
        Task task = _registerTask;
        _registerTask = null;
        if (task.IsFaulted || _server.CurrentState != CustomBattleServer.State.RegisteredGame)
        {
            string reason = task.Exception?.GetBaseException().Message ?? "state is " + _server.CurrentState;
            MPManagerSubModule.Log("Registering the game failed: " + reason);
            ScheduleRetry();
            return;
        }

        MPManagerSubModule.Log("Reconnected to the lobby after " + _attempt + " attempt(s); new players can join again.");
        _phase = Phase.Idle;
        _attempt = 0;
        _retryDelay = FirstRetryDelaySeconds;
        ResumeAutomatedBattles();
    }

    // If the rotation tried to start the next battle while we were disconnected, StartMission refused
    // ("not registered") and the rotation is left waiting for a mission that never starts.
    private static void ResumeAutomatedBattles()
    {
        ServerSideIntermissionManager manager = ServerSideIntermissionManager.Instance;
        if (manager != null && manager.AutomatedBattleState == AutomatedBattleState.StartingNextBattle && Mission.Current == null)
        {
            MPManagerSubModule.Log("Starting the next battle, which was due while disconnected.");
            manager.StartMission();
        }
    }

    // For the console: one line describing the lobby connection.
    public static string Status()
    {
        if (_server == null)
        {
            return "No game registered yet (start_game has not run).";
        }
        string status = "Lobby state: " + _server.CurrentState + ", reconnect: " + _phase;
        if (_phase != Phase.Idle)
        {
            status += ", attempt " + _attempt;
        }
        return status + (_registerArgs == null ? ", shutdown on disconnect not yet suppressed (no game registered)." : ".");
    }

    // For the console: drops the lobby connection the way a failed keepalive would, to test reconnecting.
    public static string SimulateDisconnect()
    {
        if (_server == null || _registerArgs == null)
        {
            return "Refused: no game registered yet, so a disconnect would shut the server down.";
        }
        if (_phase != Phase.Idle)
        {
            return "Refused: already reconnecting.";
        }
        ForceDisconnect();
        return "Disconnecting from the lobby.";
    }

    private static void ForceDisconnect()
    {
        try
        {
            AccessTools.Method(typeof(CustomBattleServer), "BeginDisconnect").Invoke(_server, null);
        }
        catch (Exception e)
        {
            MPManagerSubModule.Log("Could not reset the lobby connection: " + e.Message);
        }
    }
}
