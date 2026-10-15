using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TaleWorlds.MountAndBlade;

namespace MPManager;

// Server modes (!mode), one file each in MPM_MODES_DIR (~/bannerlord/modes/, from mpServerConfig/server/modes/), re-read
// when a file changes. The file name is the mode's name. A file holds settings ("key = value") and option lines
// ("Option value", like a server config) applied to the next mission when switching to the mode:
//
//   aliases = gf            other names for !mode
//   gametype = Battle       the game's game type
//   maps = mp_groupfight mp_ie_performancearena
//                           the map pool: map ids, "official" (the game's maps for the game type), "custom" (maps in
//                           Modules/Multiplayer/SceneObj), "-id" to leave one out; the first map is the one the
//                           mode starts on
//   bots = yes              whether !bots works (otherwise bots are kept at 0)
//   factions = fixed        the server starts with the mode's own CultureTeam1/2 (below) rather than random ones,
//                           when the start setting names no factions (groupfight: the same culture on both sides)
//   default = yes           the mode the server starts with when there's no start setting
internal sealed class Mode
{
    public string Name { get; set; }
    public List<string> Aliases { get; } = new List<string>();
    public string GameType { get; set; }
    public List<string> MapRules { get; } = new List<string>();
    public bool BotsAllowed { get; set; }
    public bool IsDefault { get; set; }
    public bool FixedFactions { get; set; }
    public List<(string Option, string Value)> Options { get; } = new List<(string, string)>();

    public Choice ToChoice() => new Choice(Name, Aliases.Prepend(Name).ToArray());

    public List<Choice> Maps()
    {
        List<string> ids = new List<string>();
        foreach (string rule in MapRules)
        {
            if (rule.Equals("official", StringComparison.OrdinalIgnoreCase))
            {
                ids.AddRange(MissionFlow.OfficialMaps(GameType));
            }
            else if (rule.Equals("custom", StringComparison.OrdinalIgnoreCase))
            {
                ids.AddRange(MissionFlow.CustomMaps());
            }
            else if (rule.StartsWith("-"))
            {
                ids.RemoveAll(id => id.Equals(rule.Substring(1), StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                ids.Add(rule);
            }
        }
        return ids.Distinct(StringComparer.OrdinalIgnoreCase).Select(id => MapNames.Choice(id)).ToList();
    }
}

internal static class Modes
{
    private static readonly List<Mode> All = new List<Mode>();
    private static DateTime _loadedFor = DateTime.MinValue;
    // By name: the mode files may be reloaded since.
    private static string _currentName;

    public static IReadOnlyList<Mode> List()
    {
        Load();
        return All;
    }

    // The mode last switched to, or else the first (default first) whose game type is the server's.
    public static Mode Current
    {
        get
        {
            Load();
            Mode current = All.FirstOrDefault(m => m.Name == _currentName);
            if (current != null && current.GameType == MissionFlow.GameType)
            {
                return current;
            }
            return All.OrderByDescending(m => m.IsDefault).FirstOrDefault(m => m.GameType == MissionFlow.GameType);
        }
    }

    // The current mode's maps; without mode files, the official and custom maps for the game type.
    public static List<Choice> CurrentMaps()
    {
        Mode mode = Current;
        if (mode != null)
        {
            return mode.Maps();
        }
        return MissionFlow.OfficialMaps(MissionFlow.GameType).Concat(MissionFlow.CustomMaps()).Distinct()
            .Select(id => MapNames.Choice(id)).ToList();
    }

    private static readonly Random Random = new Random();

    // At startup, once the config has been applied: the start setting ("<mode> [map] [team1 team2]", names as in
    // !mode and !setmap), or else the mode marked default. A map or factions it doesn't name are random: a map from
    // the mode's pool, two different main cultures (unless the mode has factions = fixed: its own).
    public static void ApplyDefault()
    {
        Load();
        string[] words = (Settings.Start ?? "").Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        Mode mode = null;
        string error;
        if (words.Length > 0)
        {
            Choice chosen = Matcher.Resolve(words[0], All.Select(m => m.ToChoice()).ToList(), out error);
            mode = chosen == null ? null : All.First(m => m.Name == chosen.Id);
            if (mode == null)
            {
                MPManagerSubModule.Log("start = " + Settings.Start + ": " + error + " Starting with the default mode.");
                words = new string[0];
            }
        }
        mode ??= All.FirstOrDefault(m => m.IsDefault) ?? All.FirstOrDefault();
        if (mode == null)
        {
            return;
        }

        List<Choice> maps = mode.Maps();
        Choice map = null;
        if (words.Length >= 2)
        {
            map = Matcher.Resolve(words[1], maps, out error);
            if (map == null)
            {
                MPManagerSubModule.Log("start = " + Settings.Start + ": " + error + " Starting on a random map.");
            }
        }
        if (map == null && maps.Count > 0)
        {
            map = words.Length == 0 && mode.IsDefault ? maps[0] : maps[Random.Next(maps.Count)];
        }

        string team1 = null, team2 = null;
        List<Choice> cultures = MissionFlow.Cultures();
        if (words.Length >= 4)
        {
            team1 = Matcher.Resolve(words[2], cultures, out error)?.Id;
            team2 = team1 == null ? null : Matcher.Resolve(words[3], cultures, out error)?.Id;
            if (team2 == null)
            {
                MPManagerSubModule.Log("start = " + Settings.Start + ": " + error + " Starting with random factions.");
                team1 = null;
            }
        }
        else if (words.Length == 3)
        {
            MPManagerSubModule.Log("start = " + Settings.Start + ": name both factions (team1 team2). Starting with random factions.");
        }
        if (team1 == null && !mode.FixedFactions && words.Length > 0 && cultures.Count >= 2)
        {
            int first = Random.Next(cultures.Count);
            int second = (first + 1 + Random.Next(cultures.Count - 1)) % cultures.Count;
            team1 = cultures[first].Id;
            team2 = cultures[second].Id;
        }

        Apply(mode, map?.Id, current: true);
        if (team1 != null)
        {
            foreach (var access in new[] { MultiplayerOptions.MultiplayerOptionsAccessMode.NextMapOptions, MultiplayerOptions.MultiplayerOptionsAccessMode.CurrentMapOptions })
            {
                MultiplayerOptions.OptionType.CultureTeam1.SetValue(team1, access);
                MultiplayerOptions.OptionType.CultureTeam2.SetValue(team2, access);
            }
        }
        MPManagerSubModule.Log("Starting with " + mode.Name + " (" + mode.GameType + ") on " + MultiplayerOptions.OptionType.Map.GetStrValue()
            + ", " + MultiplayerOptions.OptionType.CultureTeam1.GetStrValue() + " vs " + MultiplayerOptions.OptionType.CultureTeam2.GetStrValue()
            + (words.Length > 0 ? " (start = " + Settings.Start + ")." : " (default mode)."));
    }

    // Sets the mode, its options, and the map (null: the mode's first map), for the next mission (and the current
    // options too, when current is set: at startup, or between missions).
    public static string Apply(Mode mode, string map, bool current)
    {
        map ??= mode.Maps().FirstOrDefault()?.Id;
        if (map == null)
        {
            return "Mode " + mode.Name + " has no maps.";
        }
        var modes = current
            ? new[] { MultiplayerOptions.MultiplayerOptionsAccessMode.NextMapOptions, MultiplayerOptions.MultiplayerOptionsAccessMode.CurrentMapOptions }
            : new[] { MultiplayerOptions.MultiplayerOptionsAccessMode.NextMapOptions };
        Baseline ??= Snapshot();
        foreach (var access in modes)
        {
            // Options another mode changed (groupfight's SingleSpawn) go back to how the server started, unless
            // this mode sets them too.
            foreach (var (option, value) in Baseline)
            {
                if (!mode.Options.Any(o => o.Option.Equals(option, StringComparison.OrdinalIgnoreCase)))
                {
                    SetOption(option, value, access);
                }
            }
            MultiplayerOptions.OptionType.GameType.SetValue(mode.GameType, access);
            MultiplayerOptions.OptionType.Map.SetValue(map, access);
            foreach (var (option, value) in mode.Options)
            {
                SetOption(option, value, access);
            }
            if (!mode.BotsAllowed)
            {
                MultiplayerOptions.OptionType.NumberOfBotsTeam1.SetValue(0, access);
                MultiplayerOptions.OptionType.NumberOfBotsTeam2.SetValue(0, access);
            }
        }
        _currentName = mode.Name;
        return null;
    }

    // Every option mode files set, as the server had it before the first mode was applied (the config's values).
    private static List<(string Option, string Value)> Baseline;

    private static List<(string, string)> Snapshot()
    {
        var current = MultiplayerOptions.MultiplayerOptionsAccessMode.CurrentMapOptions;
        return All.SelectMany(m => m.Options.Select(o => o.Option))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(name => MultiplayerOptions.TryGetOptionTypeFromString(name, out _, out _))
            .Select(name =>
            {
                MultiplayerOptions.TryGetOptionTypeFromString(name, out var option, out var property);
                string value = property.OptionValueType switch
                {
                    MultiplayerOptions.OptionValueType.String => option.GetStrValue(current),
                    MultiplayerOptions.OptionValueType.Bool => option.GetBoolValue(current).ToString(),
                    _ => option.GetIntValue(current).ToString(),
                };
                return (name, value);
            })
            .ToList();
    }

    // Parses a value the way the server console does (DedicatedServerConsoleCommandManager.HandleConsoleCommand).
    private static void SetOption(string name, string value, MultiplayerOptions.MultiplayerOptionsAccessMode access)
    {
        if (!MultiplayerOptions.TryGetOptionTypeFromString(name, out var option, out var property))
        {
            MPManagerSubModule.Log("Mode option '" + name + "' doesn't exist.");
            return;
        }
        switch (property.OptionValueType)
        {
        case MultiplayerOptions.OptionValueType.String:
            option.SetValue(value, access);
            break;
        case MultiplayerOptions.OptionValueType.Bool:
            if (bool.TryParse(value, out bool flag))
            {
                option.SetValue(flag, access);
                return;
            }
            MPManagerSubModule.Log("Mode option " + name + ": '" + value + "' is not true or false.");
            break;
        default:
            if (int.TryParse(value, out int number))
            {
                option.SetValue(number, access);
                return;
            }
            MPManagerSubModule.Log("Mode option " + name + ": '" + value + "' is not a number.");
            break;
        }
    }

    private static void Load()
    {
        string dir = Environment.GetEnvironmentVariable("MPM_MODES_DIR");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            return;
        }
        string[] files = Directory.GetFiles(dir, "*.txt");
        DateTime modified = files.Length == 0 ? DateTime.MinValue : files.Max(File.GetLastWriteTimeUtc);
        if (modified == _loadedFor && files.Length == All.Count)
        {
            return;
        }
        _loadedFor = modified;
        All.Clear();
        foreach (string file in files.OrderBy(f => f))
        {
            Mode mode = Parse(Path.GetFileNameWithoutExtension(file), File.ReadAllLines(file));
            if (mode.GameType == null)
            {
                MPManagerSubModule.Log("Mode file " + file + " has no gametype; skipped.");
                continue;
            }
            All.Add(mode);
        }
        MPManagerSubModule.Log("Loaded " + All.Count + " mode(s): " + string.Join(", ", All.Select(m => m.Name)) + ".");
    }

    private static Mode Parse(string name, string[] lines)
    {
        Mode mode = new Mode { Name = name.ToLowerInvariant() };
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#"))
            {
                continue;
            }
            int equals = line.IndexOf('=');
            if (equals > 0)
            {
                string key = line.Substring(0, equals).Trim().ToLowerInvariant();
                string value = line.Substring(equals + 1).Trim();
                string[] words = value.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                switch (key)
                {
                case "aliases":
                    mode.Aliases.AddRange(words.Select(w => w.ToLowerInvariant()));
                    break;
                case "gametype":
                    mode.GameType = value;
                    break;
                case "maps":
                    mode.MapRules.AddRange(words);
                    break;
                case "bots":
                    mode.BotsAllowed = value.Equals("yes", StringComparison.OrdinalIgnoreCase) || value.Equals("true", StringComparison.OrdinalIgnoreCase);
                    break;
                case "factions":
                    mode.FixedFactions = value.Equals("fixed", StringComparison.OrdinalIgnoreCase);
                    break;
                case "default":
                    mode.IsDefault = value.Equals("yes", StringComparison.OrdinalIgnoreCase) || value.Equals("true", StringComparison.OrdinalIgnoreCase);
                    break;
                }
                continue;
            }
            int space = line.IndexOfAny(new[] { ' ', '\t' });
            if (space > 0)
            {
                mode.Options.Add((line.Substring(0, space), line.Substring(space + 1).Trim()));
            }
        }
        return mode;
    }
}
