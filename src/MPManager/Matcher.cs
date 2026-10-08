using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace MPManager;

// One thing a command argument can name: a map, a culture. Id is what the game uses; Name is the display name.
internal sealed class Choice
{
    public Choice(string id, string name)
    {
        Id = id;
        Name = string.IsNullOrEmpty(name) ? id : name;
    }

    public string Id { get; }

    public string Name { get; }

    public override string ToString() => string.Equals(Name, Id, StringComparison.OrdinalIgnoreCase) ? Name : Name + " (" + Id + ")";
}

// Resolves what someone typed in chat (no autocomplete there) to one of a list of choices:
// 1. a number: the choice at that position in the list (as printed by !maps and !factions);
// 2. the exact id or name, ignoring case, spaces and punctuation;
// 3. the only choice whose id or name contains what was typed ("2f" for mp_skirmish_map_002f, "emp" for empire);
// 4. the closest spelling, if it's clearly the best and close enough: what was typed is compared with the
//    best-matching part of each id and name, so "lighthose" finds mp_bnl_lighthouse (one letter off) despite
//    the prefix. One mistake is allowed per 4 letters typed; 3 letters or fewer must match exactly (step 3).
internal static class Matcher
{
    public static Choice Resolve(string input, IList<Choice> choices, out string error)
    {
        error = null;
        if (choices.Count == 0)
        {
            error = "Nothing to choose from.";
            return null;
        }
        if (int.TryParse(input, out int number))
        {
            if (number >= 1 && number <= choices.Count)
            {
                return choices[number - 1];
            }
            error = "Pick a number from 1 to " + choices.Count + ".";
            return null;
        }

        string key = Normalize(input);
        if (key.Length == 0)
        {
            error = "Nothing typed.";
            return null;
        }
        Choice exact = choices.FirstOrDefault(c => Normalize(c.Id) == key || Normalize(c.Name) == key);
        if (exact != null)
        {
            return exact;
        }

        List<Choice> containing = choices.Where(c => Normalize(c.Id).Contains(key) || Normalize(c.Name).Contains(key)).ToList();
        if (containing.Count == 1)
        {
            return containing[0];
        }
        if (containing.Count > 1)
        {
            error = "'" + input + "' matches " + Describe(containing) + ". Be more specific, or use the number.";
            return null;
        }

        var ranked = choices
            .Select(c => (Choice: c, Distance: Math.Min(PartDistance(key, Normalize(c.Id)), PartDistance(key, Normalize(c.Name)))))
            .OrderBy(r => r.Distance)
            .ToList();
        int tolerance = key.Length / 4;
        if (ranked[0].Distance <= tolerance && (ranked.Count == 1 || ranked[1].Distance > ranked[0].Distance))
        {
            return ranked[0].Choice;
        }
        error = "Nothing matches '" + input + "'. Closest: " + Describe(ranked.Take(3).Select(r => r.Choice).ToList()) + ".";
        return null;
    }

    private static string Describe(List<Choice> choices)
    {
        string text = string.Join(", ", choices.Take(5));
        return choices.Count > 5 ? text + " and " + (choices.Count - 5) + " more" : text;
    }

    private static string Normalize(string text)
    {
        StringBuilder sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }
        return sb.ToString();
    }

    // Edit distance between what was typed and its best-matching part of text (approximate substring match:
    // skipping text before and after the match is free).
    private static int PartDistance(string typed, string text)
    {
        int[] previous = new int[text.Length + 1]; // all zeros: a match may start anywhere in text
        int[] current = new int[text.Length + 1];
        for (int i = 1; i <= typed.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= text.Length; j++)
            {
                int cost = typed[i - 1] == text[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }
        return previous.Min(); // a match may end anywhere in text
    }
}
