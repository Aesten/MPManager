using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.MountAndBlade;

namespace MPManager;

// Chat commands of other mods, routed through MPManager: they get hiding from chat, aliases,
// the permission check (Permissions: before the mod's handler runs, subcommands included) and !help like MPManager's
// own. A mod stays standalone without MPManager; with it, it registers
// here and turns its own chat hook off.
//
// The contract is by name only, so neither mod references the other: the mod looks for the type
// "MPManager.ModCommands" among the loaded assemblies and calls Register by reflection. The parameter types are
// all from System and TaleWorlds, so they're the same types in both mods. A ready-made helper for mods is
// link/MPManagerLink.cs, which mods compile in from mpManager (one copy for every mod). Don't change these
// signatures (only add methods): mods built against them would fall back to standalone.
public static class ModCommands
{
    internal sealed class Entry
    {
        public string[] Names;
        public string Usage;
        public string Description;
        public bool AdminOnly;
        public Action<NetworkCommunicator, string[], Action<string>> Run;
        public string Mod;

        public string Name => Names[0];
    }

    private static readonly List<Entry> Registered = new List<Entry>();

    // names: the command, then its other names (shown as its aliases), without prefix ("mpb", "b").
    // run(peer, args, reply): the arguments after the command name; peer is null for the server console (mpm cmd);
    // reply answers the caller privately (or in the log for the console). Called on the main thread, outside packet
    // handling, so the mod can send network messages directly.
    public static void Register(string[] names, string usage, string description, bool adminOnly,
        Action<NetworkCommunicator, string[], Action<string>> run)
    {
        if (names == null || names.Length == 0 || run == null)
        {
            return;
        }
        string mod = run.Method.DeclaringType?.Assembly.GetName().Name ?? "?";
        string[] clean = names.Select(n => n.TrimStart('!', '/').ToLowerInvariant()).Where(n => n.Length > 0).Distinct().ToArray();
        string[] taken = clean.Where(n => Commands.IsBuiltIn(n) || Find(n) != null).ToArray();
        if (taken.Length > 0)
        {
            MPManagerSubModule.Log(mod + " command !" + string.Join(", !", taken) + " not registered: the name is taken.");
            clean = clean.Except(taken).ToArray();
            if (clean.Length == 0)
            {
                return;
            }
        }
        Registered.Add(new Entry { Names = clean, Usage = usage ?? "", Description = description ?? "", AdminOnly = adminOnly, Run = run, Mod = mod });
        MPManagerSubModule.Log("Routing " + mod + "'s command !" + string.Join(", !", clean) + ".");
    }

    // Default permission rules for the mod's commands, in permissions.txt's syntax ("b debug = everyone",
    // "b * * = admin"): used where the server's permissions file has no rule naming the command. Optional; a mod
    // registers its commands first. mod: its name, for the log.
    public static void RegisterPermissions(string mod, string[] rules)
    {
        mod ??= "?";
        int added = Permissions.AddDefaults(mod, rules);
        MPManagerSubModule.Log("Loaded " + added + " default permission rule(s) from " + mod + ".");
    }

    internal static IReadOnlyList<Entry> All => Registered;

    internal static Entry Find(string name) => Registered.FirstOrDefault(e => e.Names.Contains(name));
}
