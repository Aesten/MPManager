using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace MPManager;

// Server settings that can change without a restart, from MPM_SETTINGS_FILE (~/bannerlord/settings.txt, from
// mpServerConfig/server/settings.txt), re-read when it changes. Lines are "name = value"; missing or invalid values use
// the defaults below.
internal static class Settings
{
    private static readonly Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private static DateTime _loadedFor = DateTime.MinValue;
    // Settings are read every tick; look at the file at most once a second.
    private static DateTime _nextCheck = DateTime.MinValue;

    // Seconds between the end of a mission (or its scoreboard) and the next one; the game's own is 10.
    public static float IntermissionSeconds => Number("intermission_seconds", 1f, 0.5f, 60f);

    // Seconds of scoreboard after a mission that ends by itself; the game's own is 15. Missions ended by a command
    // skip the scoreboard.
    public static float PostMatchSeconds => Number("post_match_seconds", 5f, 0f, 15f);

    private static float Number(string name, float fallback, float min, float max)
    {
        Load();
        if (Values.TryGetValue(name, out string text) && float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
        {
            return Math.Max(min, Math.Min(max, value));
        }
        return fallback;
    }

    private static void Load()
    {
        if (DateTime.UtcNow < _nextCheck)
        {
            return;
        }
        _nextCheck = DateTime.UtcNow.AddSeconds(1);
        string path = Environment.GetEnvironmentVariable("MPM_SETTINGS_FILE");
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return;
        }
        DateTime modified = File.GetLastWriteTimeUtc(path);
        if (modified == _loadedFor)
        {
            return;
        }
        _loadedFor = modified;
        Values.Clear();
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            int equals = line.IndexOf('=');
            if (line.Length == 0 || line.StartsWith("#") || equals <= 0)
            {
                continue;
            }
            Values[line.Substring(0, equals).Trim()] = line.Substring(equals + 1).Split('#')[0].Trim();
        }
        MPManagerSubModule.Log("Loaded settings from " + path + ": " + string.Join(", ", Values) + ".");
    }
}
