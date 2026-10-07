using System;
using System.Reflection;
using System.Threading.Tasks;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.DedicatedCustomServer;
using TaleWorlds.MountAndBlade.Diamond;
using TaleWorlds.MountAndBlade.ListedServer;

namespace MPManager;

// Keeps a registered custom server running when its connection to TaleWorlds' lobby drops.
//
// Vanilla, CustomBattleServer is built with autoReconnect: false, and its session handler
// (DedicatedCustomGameServerHandler) answers OnDisconnected/OnCantConnect with Module.ShutDownWithDelay(20).
// A keepalive request failing 5 retries is enough to shut the server down mid-match. Players already in the
// game talk to the server directly; only new joins go through the lobby.
//
// CustomBattleServer only talks to its handler through ICustomBattleServerSessionHandler, so we swap in a
// SessionHandlerWrapper that passes everything through except those two callbacks. Once a game has been
// registered, a lost connection is handled here instead: reconnect with the arguments the game's own Connect
// call stored, then register the game again the way DedicatedCustomServerIntermissionManagerHandler.OnGameStart
// does, with the current game type, map and player cap. Before the first registration, the vanilla shutdown
// still applies.
//
// No Harmony: its MonoMod backend segfaults inside the Linux server process while detecting the platform.
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

    private static readonly FieldInfo HandlerField = typeof(CustomBattleServer).GetField("_handler", BindingFlags.Instance | BindingFlags.NonPublic);

    private static CustomBattleServer _server;
    private static volatile bool _gameRegistered;
    private static volatile bool _connectionLost;

    private static Phase _phase;
    private static DateTime _deadline;
    private static double _retryDelay = FirstRetryDelaySeconds;
    private static int _attempt;
    private static Task _registerTask;

    // Called by SessionHandlerWrapper, possibly off the main thread. True: handled here, skip the vanilla shutdown.
    public static bool OnConnectionLost()
    {
        if (!_gameRegistered)
        {
            return false;
        }
        _connectionLost = true;
        return true;
    }

    public static void Tick()
    {
        if (_server == null && !TryAttach())
        {
            return;
        }
        if (!_gameRegistered && _server.CurrentState == CustomBattleServer.State.RegisteredGame)
        {
            _gameRegistered = true;
            MPManagerSubModule.Log("Game registered with the lobby; a lost lobby connection will now be reconnected instead of shutting the server down.");
        }

        if (_connectionLost)
        {
            _connectionLost = false;
            HandleConnectionLost();
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

    // Wraps the lobby client's session handler once the game has created both.
    private static bool TryAttach()
    {
        CustomBattleServer server = DedicatedCustomServerSubModule.Instance?.DedicatedCustomGameServer;
        if (server == null || HandlerField == null)
        {
            return false;
        }
        if (!(HandlerField.GetValue(server) is ICustomBattleServerSessionHandler handler))
        {
            return false; // Connect hasn't run yet
        }
        if (!(handler is SessionHandlerWrapper))
        {
            HandlerField.SetValue(server, new SessionHandlerWrapper(handler));
        }
        _server = server;
        MPManagerSubModule.Log("Watching the lobby connection.");
        return true;
    }

    private static void HandleConnectionLost()
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
            // The same arguments the game passed at startup, as stored by CustomBattleServer.Connect.
            _server.Connect(
                (ICustomBattleServerSessionHandler)HandlerField.GetValue(_server),
                GetField<string>("_authToken"),
                GetField<bool>("_isSinglePlatformServer"),
                Utilities.GetModulesNames(),
                GetField<bool>("_allowsOptionalModules"),
                !GetField<bool>("_useTimeoutTimer"));
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
        // Mirrors ServerSideIntermissionManager.StartGameAux + DedicatedCustomServerIntermissionManagerHandler.OnGameStart.
        // The passwords were already hashed by start_game and are passed on as stored.
        GameStartupInfo startup = TaleWorlds.MountAndBlade.Module.CurrentModule.StartupInfo;
        string gameType = MultiplayerOptions.OptionType.GameType.GetStrValue();
        string map = MultiplayerOptions.OptionType.Map.GetStrValue();
        string uniqueSceneId = Utilities.TryGetUniqueIdentifiersForScene(map, out UniqueSceneId ids) ? ids.Serialize() : null;
        string region = startup.PlayerHostedDedicatedServer ? "USER" : startup.ServerRegion;

        MPManagerSubModule.Log("Registering the game again (" + gameType + " on " + map + ").");
        try
        {
            _registerTask = _server.RegisterGame(
                MultiplayerOptions.OptionType.GameDefinitionId.GetIntValue(),
                MultiplayerGameTypes.GetGameTypeInfo(gameType).GameModule,
                gameType,
                MultiplayerOptions.OptionType.ServerName.GetStrValue(),
                MultiplayerOptions.OptionType.MaxNumberOfPlayers.GetIntValue(),
                map,
                uniqueSceneId,
                _server.Port, // set by the first RegisterGame
                region,
                MultiplayerOptions.OptionType.GamePassword.GetStrValue(),
                MultiplayerOptions.OptionType.AdminPassword.GetStrValue(),
                startup.Permission,
                startup.CustomServerHostIP);
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
            return "Not attached to the lobby client yet.";
        }
        string status = "Lobby state: " + _server.CurrentState + ", reconnect: " + _phase;
        if (_phase != Phase.Idle)
        {
            status += ", attempt " + _attempt;
        }
        return status + (_gameRegistered ? "." : ", shutdown on disconnect not yet suppressed (no game registered).");
    }

    // For the console: drops the lobby connection the way a failed keepalive would, to test reconnecting.
    public static string SimulateDisconnect()
    {
        if (_server == null || !_gameRegistered)
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
            typeof(CustomBattleServer).GetMethod("BeginDisconnect", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.FlattenHierarchy)
                .Invoke(_server, null);
        }
        catch (Exception e)
        {
            MPManagerSubModule.Log("Could not reset the lobby connection: " + e.Message);
        }
    }

    private static T GetField<T>(string name)
    {
        return (T)typeof(CustomBattleServer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_server);
    }
}
