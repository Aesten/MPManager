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
// - Short waits (settings.txt, see Settings): 5 s of scoreboard after a mission that ends by itself instead of 15
//   (clients still count down from 15 and are moved on early), none after one ended by a command, and 1 s before
//   the next mission instead of 10. The intermission itself can't be skipped: the next mission can only be started from there
//   (ServerSideIntermissionManager.StartMissionAux), and starting it too early leaves the rotation waiting forever.
// - Infinite warmup: every mission's warmup lasts until !endwarmup, with its countdown frozen.
internal static class MissionFlow
{
    private const double WarmupFreezeIntervalSeconds = 0.5;

    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;

    private static readonly FieldInfo GameStartedField = typeof(ServerSideIntermissionManager).GetField("_gameStarted", Instance);
    private static readonly FieldInfo SwitchingEnabledField = typeof(ServerSideIntermissionManager).GetField("_automatedBattleSwitchingEnabled", Instance);
    private static readonly FieldInfo RemainingBattlesField = typeof(ServerSideIntermissionManager).GetField("_remainedAutomatedBattleCount", Instance);
    private static readonly FieldInfo WaitTimeField = typeof(ServerSideIntermissionManager).GetField("_automatedBattleWaitTimeInSeconds", Instance);
    private static readonly PropertyInfo WarmupStateProperty = typeof(MultiplayerWarmupComponent).GetProperty("WarmupState", Instance);
    private static readonly FieldInfo WarmupStartTimeField = typeof(MultiplayerWarmupComponent).GetField("_currentStateStartTime", Instance);

    private static Mission _postMatchShortened;
    private static Mission _optionsSynced;
    private static bool _intermissionSynced;
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
        // Between missions the current options are already the next mission's; send them now, so clients have them
        // when they set the mission up (the client reads SingleSpawn to pick Battle or Skirmish class prices).
        if (mission == null && manager?.AutomatedBattleState == AutomatedBattleState.CountingForNextBattle && !_intermissionSynced)
        {
            _intermissionSynced = true;
            SyncOptions();
        }
        else if (mission != null)
        {
            _intermissionSynced = false;
        }
        if (mission != null)
        {
            if (_optionsSynced != mission)
            {
                _optionsSynced = mission;
                SyncOptions();
            }
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

        float intermission = Settings.IntermissionSeconds;
        if (WaitTimeField != null && (float)WaitTimeField.GetValue(manager) != intermission)
        {
            WaitTimeField.SetValue(manager, intermission);
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
            float postMatch = Settings.PostMatchSeconds;
            if (timer.GetRemainingTime(isSynched: false) > postMatch)
            {
                timer.StartTimerAsServer(postMatch);
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

    public enum WarmupStatus
    {
        None,     // not in warmup
        Infinite, // waiting for !endwarmup or !ready
        Ending    // counting down to the match
    }

    public static WarmupStatus Warmup()
    {
        MultiplayerWarmupComponent.WarmupStates? state = CurrentWarmupState();
        if (state == null || state == MultiplayerWarmupComponent.WarmupStates.Ended
            || Mission.Current.GetMissionBehavior<MissionLobbyComponent>()?.CurrentMultiplayerState == MissionLobbyComponent.MultiplayerGameState.Ending)
        {
            return WarmupStatus.None;
        }
        return state == MultiplayerWarmupComponent.WarmupStates.Ending || Mission.Current == _warmupEnded ? WarmupStatus.Ending : WarmupStatus.Infinite;
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

    // Official scenes in Modules/Native/SceneObj offered per game type, by name prefix. The game's own lists
    // (Native/ModuleData/Multiplayer/MultiplayerScenes.xml) leave out scenes that still ship with the game and
    // every client: winter versions, mp_skirmish_map_004 to 006, 008, 013, the Battania maps...
    private static readonly Dictionary<string, string[]> OfficialScenePrefixes = new Dictionary<string, string[]>
    {
        ["Skirmish"] = new[] { "mp_skirmish_" },
        ["Battle"] = new[] { "mp_battle_", "mp_skirmish_" },
        ["Captain"] = new[] { "mp_sergeant_" },
        ["Siege"] = new[] { "mp_siege_" },
        ["TeamDeathmatch"] = new[] { "mp_tdm_" },
        ["Duel"] = new[] { "mp_duel_" },
    };

    // The game's maps for a game type: its own list, then the other official scenes with the game type's prefix.
    // Test scenes (mp_skirmish_spawn_test) are left out.
    public static List<string> OfficialMaps(string gameType)
    {
        List<string> ids = new List<string>(MultiplayerGameTypes.GetGameTypeInfo(gameType)?.Scenes ?? new List<string>());
        if (OfficialScenePrefixes.TryGetValue(gameType, out string[] prefixes))
        {
            ids.AddRange(ModuleScenes("Native").Where(id => prefixes.Any(p => id.StartsWith(p, StringComparison.OrdinalIgnoreCase))).OrderBy(id => id));
        }
        return ids.Distinct().Where(id => !id.EndsWith("_test", StringComparison.OrdinalIgnoreCase)).ToList();
    }

    // Custom maps: the scenes in Modules/Multiplayer/SceneObj.
    public static List<string> CustomMaps()
    {
        return ModuleScenes("Multiplayer").OrderBy(id => id).ToList();
    }

    private static string[] ModuleScenes(string module)
    {
        try
        {
            return Utilities.GetSingleModuleScenesOfModule(module) ?? Array.Empty<string>();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    // Bots per team, now and in later missions. Extra bots are faded out; the spawner counts them as removed
    // (SpawningBehaviorBase.OnAgentRemoved) and fills up to the new numbers only.
    public static void SetBots(int team1, int team2)
    {
        foreach (var access in new[] { MultiplayerOptions.MultiplayerOptionsAccessMode.CurrentMapOptions, MultiplayerOptions.MultiplayerOptionsAccessMode.NextMapOptions })
        {
            MultiplayerOptions.OptionType.NumberOfBotsTeam1.SetValue(team1, access);
            MultiplayerOptions.OptionType.NumberOfBotsTeam2.SetValue(team2, access);
        }
        Mission mission = Mission.Current;
        if (mission == null)
        {
            return;
        }
        RemoveExtraBots(mission, TaleWorlds.Core.BattleSideEnum.Attacker, team1);
        RemoveExtraBots(mission, TaleWorlds.Core.BattleSideEnum.Defender, team2);
        SyncOptions();
        RefreshBotRows(mission);
    }

    // Sends the current options to clients. Clients show a team's bot row on the scoreboard by their own copy of
    // NumberOfBotsTeam1/2, which the game only resends on joining and after a vote; with votes off, a mode without
    // bots kept showing the previous mode's bot rows.
    private static void SyncOptions()
    {
        GameNetwork.BeginBroadcastModuleEvent();
        GameNetwork.WriteMessage(new MultiplayerOptionsImmediate());
        GameNetwork.EndBroadcastModuleEvent(GameNetwork.EventBroadcastFlags.IncludeUnsynchronizedClients);
    }

    // Clients only re-check whether to show a bot row when bot scores arrive, so resend them (after SyncOptions).
    private static void RefreshBotRows(Mission mission)
    {
        MissionScoreboardComponent scoreboard = mission.GetMissionBehavior<MissionScoreboardComponent>();
        if (scoreboard == null)
        {
            return;
        }
        foreach (MissionScoreboardComponent.MissionScoreboardSide side in scoreboard.Sides)
        {
            TaleWorlds.MountAndBlade.BotData bots = side?.BotScores;
            if (bots == null)
            {
                continue;
            }
            GameNetwork.BeginBroadcastModuleEvent();
            GameNetwork.WriteMessage(new NetworkMessages.FromServer.BotData(side.Side, bots.KillCount, bots.AssistCount, bots.DeathCount, bots.AliveCount));
            GameNetwork.EndBroadcastModuleEvent(GameNetwork.EventBroadcastFlags.None);
        }
    }

    public static int CountBots(TaleWorlds.Core.BattleSideEnum side)
    {
        return Bots(Mission.Current, side).Count;
    }

    private static List<Agent> Bots(Mission mission, TaleWorlds.Core.BattleSideEnum side)
    {
        if (mission == null)
        {
            return new List<Agent>();
        }
        return mission.Agents
            .Where(a => a.IsHuman && a.IsActive() && a.MissionPeer == null && a.OwningAgentMissionPeer == null && a.Team?.Side == side)
            .ToList();
    }

    private static void RemoveExtraBots(Mission mission, TaleWorlds.Core.BattleSideEnum side, int keep)
    {
        foreach (Agent bot in Bots(mission, side).Skip(Math.Max(0, keep)))
        {
            bot.FadeOut(hideInstantly: true, hideMount: true);
        }
    }

    // The main cultures, each also named by its first letter when no other culture shares it ("v" is Vlandia,
    // "s" Sturgia: an exact name, where a substring "s" would also match aserai).
    public static List<Choice> Cultures()
    {
        List<BasicCultureObject> cultures = MBObjectManager.Instance.GetObjectTypeList<BasicCultureObject>()
            .Where(c => c.IsMainCulture && !string.IsNullOrEmpty(c.StringId))
            .ToList();
        return cultures
            .Select(c =>
            {
                string letter = c.StringId.Substring(0, 1);
                bool unique = cultures.Count(o => o.StringId.StartsWith(letter, StringComparison.OrdinalIgnoreCase)) == 1;
                return unique ? new Choice(c.StringId, c.Name?.ToString(), letter) : new Choice(c.StringId, c.Name?.ToString());
            })
            .ToList();
    }
}
