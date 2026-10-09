using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MPManager;

// Names for maps, from MPM_MAP_NAMES_FILE (~/bannerlord/maps.txt, from scripts/server/maps.txt), re-read when it
// changes. Lines are "name = map id"; a map can have several names, and the first one is how it's shown.
// Names are used by !maps and matched by !setmap; maps without a name are shown and matched by id. A map named
// here is offered in every mode, even if !maps wouldn't list it otherwise.
internal static class MapNames
{
    private static readonly Dictionary<string, List<string>> NamesById = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
    private static DateTime _loadedFor = DateTime.MinValue;

    public static IReadOnlyList<string> For(string id)
    {
        Load();
        return NamesById.TryGetValue(id, out List<string> names) ? names : (IReadOnlyList<string>)Array.Empty<string>();
    }

    // "lighthouse (mp_bnl_lighthouse)", or the id.
    public static string Display(string id)
    {
        IReadOnlyList<string> names = For(id);
        return names.Count == 0 ? id : names[0] + " (" + id + ")";
    }

    public static IEnumerable<string> Ids()
    {
        Load();
        return NamesById.Keys.ToList();
    }

    private static void Load()
    {
        string path = Environment.GetEnvironmentVariable("MPM_MAP_NAMES_FILE");
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
        NamesById.Clear();
        int count = 0;
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            int equals = line.IndexOf('=');
            if (line.Length == 0 || line.StartsWith("#") || equals <= 0)
            {
                continue;
            }
            string name = line.Substring(0, equals).Trim();
            string id = line.Substring(equals + 1).Trim();
            if (name.Length == 0 || id.Length == 0)
            {
                continue;
            }
            if (!NamesById.TryGetValue(id, out List<string> names))
            {
                NamesById[id] = names = new List<string>();
            }
            names.Add(name);
            count++;
        }
        MPManagerSubModule.Log("Loaded " + count + " map name(s) for " + NamesById.Count + " map(s) from " + path + ".");
    }
}
