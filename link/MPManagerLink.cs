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
// A mod with several commands calls TryRegister once per command.
//
// Player numbers: MPManager gives every player a number for good (PlayerNumIds), shared by the fleet's servers, which
// commands take instead of names ("!kick 17"). TryGetNumId gives a mod a player's number, so its own commands can
// take them too; without MPManager there are none (match names only).
//
// adminOnly is the command's level when neither the server's permissions file nor the mod's defaults have a rule
// for it. Default rules use permissions.txt's syntax ("<command> [word ...] = everyone | admin | console", * for any
// one word, the most specific rule wins); the server's file overrides them.
//
// The contract is MPManager's public ModCommands and PlayerNumIds methods, found by name: never change their signatures, only add
// methods (a mod built against a missing one falls back, as without MPManager).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TaleWorlds.MountAndBlade;

namespace MPManagerLinking;

internal static class MPManagerLink
{
    // True once MPManager routes this mod's commands.
    public static bool Routed { get; private set; }

    private static bool _permissionsSent;

    // Commands already handed to MPManager, by their first name: registering one again changes nothing.
    private static readonly HashSet<string> Registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private static MethodInfo _numId;

    // Registers the command with MPManager if it's loaded; false if it isn't (or its contract changed), in which
    // case the mod handles chat itself. Once per command; again for the same command: nothing more happens.
    public static bool TryRegister(string[] names, string usage, string description, bool adminOnly,
        Action<NetworkCommunicator, string[], Action<string>> run)
    {
        if (names == null || names.Length == 0 || Registered.Contains(names[0]))
        {
            return Routed;
        }
        try
        {
            MethodInfo register = Method("Register");
            if (register == null)
            {
                return false;
            }
            register.Invoke(null, new object[] { names, usage, description, adminOnly, run });
            Registered.Add(names[0]);
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

    // The player's number (MPManager's PlayerNumIds); false without MPManager, or if it has none for them. Main
    // thread only (a player's first number is written to a file shared by the servers).
    public static bool TryGetNumId(NetworkCommunicator peer, out int numId)
    {
        numId = -1;
        if (peer == null)
        {
            return false;
        }
        try
        {
            _numId ??= Method("Get", "MPManager.PlayerNumIds");
            if (_numId == null)
            {
                return false;
            }
            numId = (int)_numId.Invoke(null, new object[] { peer });
            return numId > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static MethodInfo Method(string name, string type = "MPManager.ModCommands") => AppDomain.CurrentDomain.GetAssemblies()
        .Where(a => a.GetName().Name == "MPManager")
        .Select(a => a.GetType(type))
        .FirstOrDefault(t => t != null)
        ?.GetMethod(name, BindingFlags.Public | BindingFlags.Static);
}
