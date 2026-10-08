using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using NetworkMessages.FromServer;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.ListedServer;
using TaleWorlds.ObjectSystem;

namespace MPManager;

// How the server moves from one mission to the next, and the map/faction changes commands make.
//
// - No votes: map and culture voting are kept off, and the map counts as "selected by admin", so the automated
//   rotation copies the next-map options into the next mission instead of picking a map at random. Unless a
//   command changes them, the next-map options are the current mission's, so the same mission is played again.
// - Endless: the rotation is switched on if the config didn't, and never runs out (vanilla shuts the server down
//   after set_automated_battle_count battles).
// - Short waits: 5 s of scoreboard after a mission that ends by itself instead of 15 (clients still count down
//   from 15 and are moved on early), none after one ended by a command, and 1 s before the next mission instead
//   of 10. The intermission itself can't be skipped: the next mission can only be started from there
//   (ServerSideIntermissionManager.StartMissionAux), and starting it too early leaves the rotation waiting forever.
// - Infinite warmup: every mission's warmup lasts until !endwarmup, with its countdown frozen.
internal static class MissionFlow
{
    private const float PostMatchSeconds = 5f;
    private const float IntermissionSeconds = 1f;
    private const double WarmupFreezeIntervalSeconds = 0.5;

    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;

    private static readonly FieldInfo GameStartedField = typeof(ServerSideIntermissionManager).GetField("_gameStarted", Instance);
    private static readonly FieldInfo SwitchingEnabledField = typeof(ServerSideIntermissionManager).GetField("_automatedBattleSwitchingEnabled", Instance);
    private static readonly FieldInfo RemainingBattlesField = typeof(ServerSideIntermissionManager).GetField("_remainedAutomatedBattleCount", Instance);
    private static readonly FieldInfo WaitTimeField = typeof(ServerSideIntermissionManager).GetField("_automatedBattleWaitTimeInSeconds", Instance);
    private static readonly PropertyInfo WarmupStateProperty = typeof(MultiplayerWarmupComponent).GetProperty("WarmupState", Instance);
    private static readonly FieldInfo WarmupStartTimeField = typeof(MultiplayerWarmupComponent).GetField("_currentStateStartTime", Instance);

    private static Mission _postMatchShortened;
    private static bool _rotationLogged;

    // The mission whose warmup !endwarmup ended; every other mission's warmup is infinite.
    private static Mission _warmupEnded;
    private static DateTime _nextWarmupFreeze;

    public static string GameType => MultiplayerOptions.OptionType.GameType.GetStrValue();

    public static void Tick()
    {
        ServerSideIntermissionManager manager = ServerSideIntermissionManager.Instance;
        if (manager != null)
        {
            KeepRotationGoing(manager);
        }
        Mission mission = Mission.Current;
        if (mission != null)
        {
            ShortenPostMatch(mission);
            if (mission != _warmupEnded)
            {
                HoldWarmup(mission);
            }
        }
    }

    private static void KeepRotationGoing(ServerSideIntermissionManager manager)
    {
        MultiplayerIntermissionVotingManager voting = MultiplayerIntermissionVotingManager.Instance;
        voting.IsMapVoteEnabled = false;
        voting.IsCultureVoteEnabled = false;
        voting.IsMapSelectedByAdmin = true;

        if (WaitTimeField != null && (float)WaitTimeField.GetValue(manager) > IntermissionSeconds)
        {
            WaitTimeField.SetValue(manager, IntermissionSeconds);
        }

        if (GameStartedField == null || !(bool)GameStartedField.GetValue(manager))
        {
            return;
        }
        if (SwitchingEnabledField != null && !(bool)SwitchingEnabledField.GetValue(manager))
        {
            string map = MultiplayerOptions.OptionType.Map.GetStrValue();
            if (!manager.AutomatedMapPool.Contains(map))
            {
                manager.AddMapToAutomatedBattlePool(map);
            }
            manager.SetAutomatedBattleCount(-1);
            manager.EnableAutomatedBattleSwitching();
            MPManagerSubModule.Log("Turned on the automated rotation (the config didn't), so missions follow each other.");
        }
        if (RemainingBattlesField != null && (int)RemainingBattlesField.GetValue(manager) < int.MaxValue / 2)
        {
            RemainingBattlesField.SetValue(manager, int.MaxValue);
            if (!_rotationLogged)
            {
                _rotationLogged = true;
                MPManagerSubModule.Log("The rotation is endless; the server no longer stops after a set number of battles.");
            }
        }
    }

    private static void ShortenPostMatch(Mission mission)
    {
        if (_postMatchShortened == mission)
        {
            return;
        }
        MissionLobbyComponent lobby = mission.GetMissionBehavior<MissionLobbyComponent>();
        MultiplayerTimerComponent timer = mission.GetMissionBehavior<MultiplayerTimerComponent>();
        if (lobby?.CurrentMultiplayerState == MissionLobbyComponent.MultiplayerGameState.Ending && timer != null)
        {
            if (timer.GetRemainingTime(isSynched: false) > PostMatchSeconds)
            {
                timer.StartTimerAsServer(PostMatchSeconds);
            }
            _postMatchShortened = mission;
        }
    }

    // Keeps warmup going with its countdown frozen. Twice a second the server timer restarts (so warmup never
    // reaches its 30 s end) and clients get a warmup start time half a second ahead, so they keep showing the full
    // warmup time; a "warmup in progress" message only restarts the client's timer. The start time is also kept
    // for players who join later. If warmup started ending by itself (a full server), it's put back.
    private static void HoldWarmup(Mission mission)
    {
        MultiplayerWarmupComponent warmup = mission.GetMissionBehavior<MultiplayerWarmupComponent>();
        MultiplayerTimerComponent timer = mission.GetMissionBehavior<MultiplayerTimerComponent>();
        if (warmup == null || timer == null || WarmupStateProperty == null || WarmupStartTimeField == null)
        {
            return;
        }
        // Once the mission is ending (!setmap, !resetmap), the shared mission timer counts down the end screen:
        // restarting it as warmup kept the mission from ever ending.
        if (mission.GetMissionBehavior<MissionLobbyComponent>()?.CurrentMultiplayerState == MissionLobbyComponent.MultiplayerGameState.Ending)
        {
            return;
        }
        var state = (MultiplayerWarmupComponent.WarmupStates)WarmupStateProperty.GetValue(warmup);
        if (state == MultiplayerWarmupComponent.WarmupStates.Ending)
        {
            WarmupStateProperty.SetValue(warmup, MultiplayerWarmupComponent.WarmupStates.InProgress);
            MPManagerSubModule.Log("Warmup is infinite: cancelled its end (!endwarmup ends it).");
        }
        else if (state != MultiplayerWarmupComponent.WarmupStates.InProgress || DateTime.UtcNow < _nextWarmupFreeze)
        {
            return;
        }
        _nextWarmupFreeze = DateTime.UtcNow.AddSeconds(WarmupFreezeIntervalSeconds);
        timer.StartTimerAsServer(MultiplayerWarmupComponent.TotalWarmupDuration);
        MissionTime start = MissionTime.SecondsFromNow((float)WarmupFreezeIntervalSeconds);
        WarmupStartTimeField.SetValue(warmup, start);
        GameNetwork.BeginBroadcastModuleEvent();
        GameNetwork.WriteMessage(new WarmupStateChange(MultiplayerWarmupComponent.WarmupStates.InProgress, start.NumberOfTicks));
        GameNetwork.EndBroadcastModuleEvent(GameNetwork.EventBroadcastFlags.None);
    }

    // !warmup: back to an infinite warmup. In warmup, an ending warmup is put back; during the match, the mission
    // restarts (it starts in warmup).
    public static string StartHeldWarmup()
    {
        Mission mission = Mission.Current;
        if (CurrentWarmupState() is MultiplayerWarmupComponent.WarmupStates state && state != MultiplayerWarmupComponent.WarmupStates.Ended)
        {
            _warmupEnded = null;
            return "Warmup is infinite again, until !endwarmup.";
        }
        _warmupEnded = null;
        return Restart()
            ? "Restarting into warmup."
            : "The next mission starts in warmup.";
    }

    // !endwarmup: ends this mission's warmup with the game's 30 s countdown, or with force at once (the game's
    // 3 s "battle starting" countdown follows). The next mission's warmup is infinite again.
    public static string EndWarmup(bool force, out bool ended)
    {
        ended = false;
        MultiplayerWarmupComponent warmup = Mission.Current?.GetMissionBehavior<MultiplayerWarmupComponent>();
        MultiplayerWarmupComponent.WarmupStates? state = CurrentWarmupState();
        if (warmup == null || state == null || state == MultiplayerWarmupComponent.WarmupStates.Ended)
        {
            return "Not in warmup.";
        }
        _warmupEnded = Mission.Current;
        if (state == MultiplayerWarmupComponent.WarmupStates.InProgress)
        {
            warmup.EndWarmupProgress();
            ended = true;
        }
        if (force)
        {
            // The ending countdown runs on the mission timer; when it has passed, warmup ends on the next tick.
            Mission.Current.GetMissionBehavior<MultiplayerTimerComponent>()?.StartTimerAsServer(0f);
            ended = true;
            return "Warmup ends now.";
        }
        return ended ? "Warmup ends in 30 s." : "Warmup is already ending.";
    }

    private static MultiplayerWarmupComponent.WarmupStates? CurrentWarmupState()
    {
        MultiplayerWarmupComponent warmup = Mission.Current?.GetMissionBehavior<MultiplayerWarmupComponent>();
        if (warmup == null || WarmupStateProperty == null)
        {
            return null;
        }
        return (MultiplayerWarmupComponent.WarmupStates)WarmupStateProperty.GetValue(warmup);
    }

    // Sets the map and/or cultures (null: unchanged) and restarts the mission with them.
    public static void Change(string map, string cultureTeam1, string cultureTeam2)
    {
        // Between missions, the next mission has already been set up from the next-map options; change both.
        bool inMission = Mission.Current != null;
        Set(MultiplayerOptions.OptionType.Map, map, inMission);
        Set(MultiplayerOptions.OptionType.CultureTeam1, cultureTeam1, inMission);
        Set(MultiplayerOptions.OptionType.CultureTeam2, cultureTeam2, inMission);
        Restart();
    }

    private static void Set(MultiplayerOptions.OptionType option, string value, bool inMission)
    {
        if (value == null)
        {
            return;
        }
        option.SetValue(value, MultiplayerOptions.MultiplayerOptionsAccessMode.NextMapOptions);
        if (!inMission)
        {
            option.SetValue(value, MultiplayerOptions.MultiplayerOptionsAccessMode.CurrentMapOptions);
        }
    }

    // Ends the current mission without the scoreboard; the rotation then starts the next one from the next-map options.
    public static bool Restart()
    {
        Mission mission = Mission.Current;
        MissionLobbyComponent lobby = mission?.GetMissionBehavior<MissionLobbyComponent>();
        if (lobby == null || lobby.CurrentMultiplayerState == MissionLobbyComponent.MultiplayerGameState.Ending)
        {
            return false;
        }
        lobby.SetStateEndingAsServer();
        // The end screen runs on the mission timer; when it has passed, the mission closes on the next tick.
        mission.GetMissionBehavior<MultiplayerTimerComponent>()?.StartTimerAsServer(0f);
        _postMatchShortened = mission;
        return true;
    }

    public static string Next(MultiplayerOptions.OptionType option) => option.GetStrValue(MultiplayerOptions.MultiplayerOptionsAccessMode.NextMapOptions);

    // Maps for a game type, in a stable order: the game type's official maps, then maps added with
    // add_map_to_usable_maps / add_map_to_automated_battle_pool, then custom scenes in Modules/Multiplayer/SceneObj.
    public static List<Choice> Maps(string gameType)
    {
        List<string> ids = new List<string>();
        void Add(IEnumerable<string> source)
        {
            if (source == null)
            {
                return;
            }
            foreach (string id in source)
            {
                if (!string.IsNullOrEmpty(id) && !ids.Contains(id))
                {
                    ids.Add(id);
                }
            }
        }
        Add(MultiplayerGameTypes.GetGameTypeInfo(gameType)?.Scenes);
        Add(MultiplayerIntermissionVotingManager.Instance.GetUsableMaps(gameType));
        Add(ServerSideIntermissionManager.Instance?.AutomatedMapPool);
        try
        {
            Add(Utilities.GetSingleModuleScenesOfModule("Multiplayer"));
        }
        catch (Exception)
        {
            // custom scenes are optional
        }
        return ids.Select(id => new Choice(id, MapName(id))).ToList();
    }

    public static string MapName(string id)
    {
        try
        {
            return GameTexts.TryGetText("str_multiplayer_scene_name", out var text, id) ? text.ToString() : id;
        }
        catch (Exception)
        {
            return id;
        }
    }

    public static List<Choice> Cultures()
    {
        return MBObjectManager.Instance.GetObjectTypeList<BasicCultureObject>()
            .Where(c => c.IsMainCulture)
            .Select(c => new Choice(c.StringId, c.Name?.ToString()))
            .ToList();
    }
}
