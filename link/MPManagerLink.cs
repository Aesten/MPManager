// Optional link from a server mod to MPManager: routes the mod's chat commands through it and declares their default
// permissions. The one copy every mod compiles in, from here (see "Commands from other mods" in mpManager's README):
//
//   <Compile Include="$(MPManagerDir)\link\MPManagerLink.cs" Link="MPManagerLink.cs" />
//
// with MPManagerDir pointing at the mpManager checkout (a sibling folder by default). The mod needs no reference to
// MPManager and still works without it.
//
// With MPManager on the server, the mod's commands get MPManager's handling: hidden from chat, command aliases
// (aliases.txt), the permission check (permissions.txt, subcommands included, before the mod's handler runs), and
// !help. Without it, the mod keeps using its own chat hook and its own rights check.
//
// Usage, when the mod would attach its own chat hook (e.g. once per mission):
//
//   if (MPManagerLink.TryRegister(new[] { "mpb", "b" }, "<setting> [value]", "Show or change settings.",
//       adminOnly: true, Run))
//   {
//       MPManagerLink.TryRegisterPermissions("b * = everyone", "b * * = admin");  // optional defaults
//       return; // routed: don't hook chat (or unhook it), or commands would run twice
//   }
//   ... attach the mod's own chat hook ...
//
//   static void Run(NetworkCommunicator peer, string[] args, Action<string> reply)
//   // args: the words after the command name; peer: null when run from the server console (mpm cmd);
//   // reply: answers the caller privately. Runs on the main thread, outside packet handling. MPManager has already
//   // checked the caller's rights for these arguments: don't check them again.
//
// adminOnly is the command's level when neither the server's permissions file nor the mod's defaults have a rule
// for it. Default rules use permissions.txt's syntax ("<command> [word ...] = everyone | admin | console", * for any
// one word, the most specific rule wins); the server's file overrides them.
//
// The contract is MPManager's public ModCommands methods, found by name: never change their signatures, only add
// methods (a mod built against a missing one falls back, as without MPManager).

using System;
using System.Linq;
using System.Reflection;
using TaleWorlds.MountAndBlade;

namespace MPManagerLinking;

internal static class MPManagerLink
{
    // True once MPManager routes this mod's commands.
    public static bool Routed { get; private set; }

    private static bool _permissionsSent;

    // Registers the command with MPManager if it's loaded; false if it isn't (or its contract changed), in which
    // case the mod handles chat itself.
    public static bool TryRegister(string[] names, string usage, string description, bool adminOnly,
        Action<NetworkCommunicator, string[], Action<string>> run)
    {
        if (Routed)
        {
            return true;
        }
        try
        {
            MethodInfo register = Method("Register");
            if (register == null)
            {
                return false;
            }
            register.Invoke(null, new object[] { names, usage, description, adminOnly, run });
            Routed = true;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // Default permission rules for this mod's commands (once; after TryRegister). False without MPManager, or with
    // an MPManager too old to take them (then the command's adminOnly level applies).
    public static bool TryRegisterPermissions(params string[] rules)
    {
        if (_permissionsSent)
        {
            return true;
        }
        try
        {
            MethodInfo register = Method("RegisterPermissions");
            if (register == null)
            {
                return false;
            }
            register.Invoke(null, new object[] { Assembly.GetExecutingAssembly().GetName().Name, rules });
            _permissionsSent = true;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static MethodInfo Method(string name) => AppDomain.CurrentDomain.GetAssemblies()
        .Where(a => a.GetName().Name == "MPManager")
        .Select(a => a.GetType("MPManager.ModCommands"))
        .FirstOrDefault(t => t != null)
        ?.GetMethod(name, BindingFlags.Public | BindingFlags.Static);
}
