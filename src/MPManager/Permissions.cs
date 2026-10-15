using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MPManager;

// Who may run which chat command, from MPM_PERMISSIONS_FILE (instances/<id>/run/permissions.txt, from mpServerConfig's
// config/ or a profile), re-read when it changes. One rule per line, "<command> [word ...] = <level>":
//
//   * = admin                 every command without a more specific rule
//   setmap = everyone         a command (by its name or any of its names: b = mpb)
//   b debug = everyone        a subcommand: the first words after the command
//   b * = everyone            * matches any one word ("!b thrust_curve": show a setting)
//   b * * = admin             ...and two or more ("!b thrust_curve 100 ...": change it)
//
// A rule applies to commands that start with its words. When several apply, the most specific wins: compared word
// by word, a named word beats *, and a rule that goes on beats one that stops (so "b speedo" beats "b * *" for
// "!b speedo stats", and "b * *" beats "b *" for "!b thrust_curve 100"). Levels: everyone, admin (joined with the
// admin password), console (mpm cmd only). Without a matching rule (or without the file), a command keeps the level
// it's registered with. Aliases (aliases.txt) are expanded first, so their rules are the commands'. The console
// (mpm cmd) may run everything.
//
// Mods' commands are checked here before MPManager hands them over, with their arguments, so mods routed through
// MPManager don't check rights themselves. A mod can declare default rules for its commands (ModCommands
// .RegisterPermissions, same syntax: "b debug = everyone"). Order: the file's rules naming a command, then the mods'
// defaults, then the file's "*" rules, then the command's registered level.
internal static class Permissions
{
    public enum Level
    {
        Everyone,
        Admin,
        Console,
    }

    private sealed class Rule
    {
        public string[] Words;
        public Level Level;
    }

    private static readonly List<Rule> Rules = new List<Rule>();
    private static readonly List<Rule> ModDefaults = new List<Rule>();
    private static DateTime _loadedFor = DateTime.MinValue;
    private static DateTime _nextCheck = DateTime.MinValue;

    // The level a command needs: names are the command's names (the first is its own), args the words after it,
    // fallback its registered level.
    public static Level Required(IReadOnlyList<string> names, IReadOnlyList<string> args, Level fallback)
    {
        Load();
        return (Best(Rules.Where(r => r.Words[0] != "*"), names, args)
            ?? Best(ModDefaults, names, args)
            ?? Best(Rules.Where(r => r.Words[0] == "*"), names, args))?.Level ?? fallback;
    }

    private static Rule Best(IEnumerable<Rule> rules, IReadOnlyList<string> names, IReadOnlyList<string> args)
    {
        Rule best = null;
        foreach (Rule rule in rules)
        {
            if (Matches(rule, names, args) && (best == null || MoreSpecific(rule, best)))
            {
                best = rule;
            }
        }
        return best;
    }

    // A mod's default rules ("<command> [word ...] = <level>"); returns how many were valid.
    public static int AddDefaults(string mod, IEnumerable<string> lines)
    {
        int added = 0;
        foreach (string line in lines ?? Enumerable.Empty<string>())
        {
            Rule rule = Parse(line, mod + " default");
            if (rule != null)
            {
                ModDefaults.Add(rule);
                added++;
            }
        }
        return added;
    }

    // "b debug = everyone" -> a rule; null (logged) if it isn't one.
    private static Rule Parse(string raw, string source)
    {
        string line = raw.Trim();
        int equals = line.IndexOf('=');
        if (line.Length == 0 || line.StartsWith("#") || equals <= 0)
        {
            return null;
        }
        string[] words = line.Substring(0, equals).ToLowerInvariant().Split((char[])null, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.TrimStart('!', '/')).ToArray();
        string levelText = line.Substring(equals + 1).Split('#')[0].Trim();
        if (words.Length == 0 || !Enum.TryParse(levelText, ignoreCase: true, out Level level))
        {
            MPManagerSubModule.Log("Permissions (" + source + "): ignored '" + line + "' (levels: everyone, admin, console).");
            return null;
        }
        return new Rule { Words = words, Level = level };
    }

    public static bool Allows(Level required, bool isAdmin, bool isConsole) => required switch
    {
        Level.Everyone => true,
        Level.Admin => isAdmin || isConsole,
        _ => isConsole,
    };

    // The rules about a command's subcommands that this caller passes (for !help): "b debug", "b speedo"...
    public static List<string> AllowedSubcommands(IReadOnlyList<string> names, bool isAdmin, bool isConsole)
    {
        Load();
        return Rules.Concat(ModDefaults)
            .Where(r => r.Words.Length > 1 && names.Contains(r.Words[0]) && Allows(r.Level, isAdmin, isConsole))
            .Select(r => string.Join(" ", r.Words.Skip(1)))
            .Where(s => !s.StartsWith("*"))
            .Distinct()
            .ToList();
    }

    public static string Describe(Level level) => level switch
    {
        Level.Admin => "for admins (join with the admin password)",
        Level.Console => "only for the server console",
        _ => "open to everyone",
    };

    private static bool Matches(Rule rule, IReadOnlyList<string> names, IReadOnlyList<string> args)
    {
        if (rule.Words[0] != "*" && !names.Contains(rule.Words[0]))
        {
            return false;
        }
        if (rule.Words.Length - 1 > args.Count)
        {
            return false;
        }
        for (int i = 1; i < rule.Words.Length; i++)
        {
            if (rule.Words[i] != "*" && !rule.Words[i].Equals(args[i - 1], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }

    private static bool MoreSpecific(Rule a, Rule b)
    {
        int n = Math.Min(a.Words.Length, b.Words.Length);
        for (int i = 0; i < n; i++)
        {
            bool aNamed = a.Words[i] != "*", bNamed = b.Words[i] != "*";
            if (aNamed != bNamed)
            {
                return aNamed;
            }
        }
        return a.Words.Length > b.Words.Length;
    }

    private static void Load()
    {
        if (DateTime.UtcNow < _nextCheck)
        {
            return;
        }
        _nextCheck = DateTime.UtcNow.AddSeconds(1);
        string path = Environment.GetEnvironmentVariable("MPM_PERMISSIONS_FILE");
        DateTime modified = !string.IsNullOrEmpty(path) && File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        if (modified == _loadedFor)
        {
            return;
        }
        _loadedFor = modified;
        Rules.Clear();
        if (modified == DateTime.MinValue)
        {
            MPManagerSubModule.Log("No permissions file (MPM_PERMISSIONS_FILE); commands keep their built-in levels.");
            return;
        }
        foreach (string raw in File.ReadAllLines(path))
        {
            Rule rule = Parse(raw, Path.GetFileName(path));
            if (rule != null)
            {
                Rules.Add(rule);
            }
        }
        MPManagerSubModule.Log("Loaded " + Rules.Count + " permission rule(s) from " + path + ".");
    }
}
