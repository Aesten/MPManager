using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MPManager;

// Names for maps, from MPM_MAP_NAMES_FILE (~/bannerlord/maps.txt, from mpServerConfig/server/maps.txt), re-read when it
// changes. Lines are "map id | display name | aliases | short aliases": the display name is how the map is shown
// in chat, the aliases and short aliases (separated by spaces) are what !setmap and !mode match. A variant of a map is named "base:variant"
// (xauna:winter), matched in two parts (see Matcher). Maps without a line are shown and matched by id. Which maps
// a mode offers is set in its mode file.
internal static class MapNames
{
    private sealed class Entry
    {
        public string Display;
        public string[] Aliases;
    }

    private static readonly Dictionary<string, Entry> Entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
    private static DateTime _loadedFor = DateTime.MinValue;

    public static Choice Choice(string id)
    {
        Load();
        return Entries.TryGetValue(id, out Entry entry)
            ? new Choice(id, entry.Aliases) { DisplayName = entry.Display }
            : new Choice(id);
    }

    // "Xauna", or the first alias, or the id.
    public static string Display(string id) => Choice(id).Name;

    // Maps grouped with their variants, in the order of their first map: xauna, xauna:rain and xauna:winter make
    // one group. A map's group is its first alias without the variant part; maps without aliases are alone.
    public static List<MapGroup> Group(IEnumerable<Choice> maps)
    {
        List<MapGroup> groups = new List<MapGroup>();
        foreach (Choice map in maps)
        {
            string first = map.Names.FirstOrDefault(n => !n.Contains(':')) ?? map.Variants.Select(v => v.Base).FirstOrDefault() ?? map.Id;
            string variant = map.Names.FirstOrDefault()?.Contains(':') == true ? map.Variants.First().Variant : null;
            MapGroup group = groups.FirstOrDefault(g => g.Key.Equals(first, StringComparison.OrdinalIgnoreCase));
            if (group == null)
            {
                groups.Add(group = new MapGroup(first));
            }
            if (variant == null && group.Default == null)
            {
                group.Default = map;
            }
            else
            {
                group.Variants.Add((variant ?? map.Id, map));
            }
        }
        return groups;
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
        Entries.Clear();
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#"))
            {
                continue;
            }
            string[] columns = line.Split('|').Select(c => c.Trim()).ToArray();
            string id = columns[0];
            string display = columns.Length > 1 && columns[1].Length > 0 ? columns[1] : null;
            // Aliases, then short aliases (fourth column); both are matched the same way.
            string[] aliases = columns.Skip(2).Take(2)
                .SelectMany(c => c.Split((char[])null, StringSplitOptions.RemoveEmptyEntries))
                .ToArray();
            if (id.Length == 0 || (display == null && aliases.Length == 0))
            {
                continue;
            }
            Entries[id] = new Entry { Display = display, Aliases = aliases };
        }
        MPManagerSubModule.Log("Loaded names for " + Entries.Count + " map(s) from " + path + ".");
    }
}

internal sealed class MapGroup
{
    public MapGroup(string key)
    {
        Key = key;
    }

    public string Key { get; }

    // The map with the plain alias (xauna), if any.
    public Choice Default { get; set; }

    public List<(string Name, Choice Map)> Variants { get; } = new List<(string, Choice)>();

    // What a number from !maps picks: the default version, or else the first variant.
    public Choice First => Default ?? Variants[0].Map;

    // "Xauna [default|rain|winter]", "Lighthouse".
    public override string ToString()
    {
        string name = Default?.Name ?? Key;
        if (Variants.Count == 0)
        {
            return name;
        }
        IEnumerable<string> versions = Variants.Select(v => v.Name);
        if (Default != null)
        {
            versions = versions.Prepend("default");
        }
        return name + " [" + string.Join("|", versions) + "]";
    }
}
