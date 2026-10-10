using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.MountAndBlade;

namespace MPManager;

// Chat commands of other mods, routed through MPManager: they get the "!"/"/" prefixes, hiding from chat, aliases,
// the admin check and !help like MPManager's own. A mod stays standalone without MPManager; with it, it registers
// here and turns its own chat hook off.
//
// The contract is by name only, so neither mod references the other: the mod looks for the type
// "MPManager.ModCommands" among the loaded assemblies and calls Register by reflection. The parameter types are
// all from System and TaleWorlds, so they're the same types in both mods. A ready-made helper for mods is
// docs/MPManagerLink.cs (copy it into the mod). Don't change Register's signature: mods built against it would
// fall back to standalone.
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

    // names: the command and its other names, without prefix ("b", "mpb").
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

    internal static IReadOnlyList<Entry> All => Registered;

    internal static Entry Find(string name) => Registered.FirstOrDefault(e => e.Names.Contains(name));
}
