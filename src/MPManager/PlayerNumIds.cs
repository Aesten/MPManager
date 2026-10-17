using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using TaleWorlds.MountAndBlade;

namespace MPManager;

// Player numbers ("num ids"): every Bannerlord player id gets a number the first time a server sees it, and keeps it
// for good, on every server that shares the file. Players type the number instead of a name that may be hard to type
// ("!kick 17", "!tp 17", and mods' commands such as group duels' "!invite 17"); !players lists them.
//
// MPManager owns the numbers. Other mods ask for them through link/MPManagerLink.cs (TryGetNumId), which calls Get
// below by reflection: it's part of the contract with mods, so its signature never changes.
//
// The file is MPM_PLAYER_NUMIDS_FILE (on the fleet: ~/bannerlord/data/player-numids.txt, shared by all instances),
// else player-numids.txt in the server's working directory. One player per line, tab-separated:
//
//   17	2.0.0.76561198000000000	Aesten
//
// number, player id, and the name the player had when they got the number (for people reading the file; never used
// for lookups). Lines are only ever appended, so numbers are never reused. Several servers use the file at once: a
// new number is only given out while the file is open exclusively, after reading what the others have added.
public static class PlayerNumIds
{
    private static readonly Dictionary<string, int> ByPlayer = new Dictionary<string, int>();

    private static string _path;

    internal static string FilePath => _path ??= Environment.GetEnvironmentVariable("MPM_PLAYER_NUMIDS_FILE") is string path && path.Length > 0
        ? path
        : Path.GetFullPath("player-numids.txt");

    // The player's number, given out now if they have none yet; -1 if the file can't be used (logged). For mods,
    // through MPManagerLink. Main thread only.
    public static int Get(NetworkCommunicator peer) => peer?.VirtualPlayer == null ? -1 : Get(peer.VirtualPlayer.Id.ToString(), peer.UserName);

    private static int Get(string playerId, string name)
    {
        if (string.IsNullOrEmpty(playerId))
        {
            return -1;
        }
        if (ByPlayer.TryGetValue(playerId, out int known))
        {
            return known;
        }
        try
        {
            return Assign(playerId, name);
        }
        catch (Exception e)
        {
            MPManagerSubModule.Log("No player number for " + name + " (" + FilePath + "): " + e.Message);
            return -1;
        }
    }

    internal static int? Of(NetworkCommunicator peer)
    {
        int number = Get(peer);
        return number > 0 ? number : null;
    }

    // "Aesten (#17)", or just the name without a number.
    internal static string Describe(NetworkCommunicator peer) => Of(peer) is int number ? peer.UserName + " (#" + number + ")" : peer.UserName;

    private static int Assign(string playerId, string name)
    {
        string dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        using FileStream file = OpenExclusive();
        int highest = 0;
        using (StreamReader reader = new StreamReader(file, Encoding.UTF8, false, 4096, leaveOpen: true))
        {
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                string[] parts = line.Split('\t');
                if (parts.Length >= 2 && int.TryParse(parts[0], out int number))
                {
                    ByPlayer[parts[1]] = number;
                    highest = Math.Max(highest, number);
                }
            }
        }
        if (ByPlayer.TryGetValue(playerId, out int existing))
        {
            return existing;
        }
        int next = highest + 1;
        // A file whose last line has no line end (cut off by a crash) gets one first, so the new line stays separate.
        if (file.Length > 0)
        {
            file.Seek(-1, SeekOrigin.End);
            bool endsWithNewline = file.ReadByte() == '\n';
            file.Seek(0, SeekOrigin.End);
            if (!endsWithNewline)
            {
                file.WriteByte((byte)'\n');
            }
        }
        string clean = (name ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
        byte[] bytes = Encoding.UTF8.GetBytes(next + "\t" + playerId + "\t" + clean + "\n");
        file.Write(bytes, 0, bytes.Length);
        file.Flush(flushToDisk: true);
        ByPlayer[playerId] = next;
        return next;
    }

    // Another server may hold the file for a moment: retries for up to a second.
    private static FileStream OpenExclusive()
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return new FileStream(FilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(50);
            }
        }
    }
}
