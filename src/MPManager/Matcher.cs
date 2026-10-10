using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace MPManager;

// One thing a command argument can name: a map, a culture, a player. Id is what the game uses; Names are what
// people type for it; it's shown by its DisplayName if set, else its first name. Matching looks at the id and every
// name; variant names ("xauna:winter") only match the base:variant syntax.
internal sealed class Choice
{
    public Choice(string id, params string[] names)
        : this(id, (IReadOnlyList<string>)names)
    {
    }

    public Choice(string id, IReadOnlyList<string> names)
    {
        Id = id;
        Names = names.Where(n => !string.IsNullOrEmpty(n)).ToList();
    }

    public string Id { get; }

    public IReadOnlyList<string> Names { get; }

    public string DisplayName { get; init; }

    public string Name => DisplayName ?? (Names.Count > 0 ? Names[0] : Id);

    // What to type for it: with a display name, the first name (or the id); otherwise the id.
    private string Typed => DisplayName != null && Names.Count > 0 ? Names[0] : Id;

    // The id and the names, minus variant names.
    internal IEnumerable<string> Keys => Names.Where(n => !n.Contains(':')).Prepend(Id);

    // Variant names split into base and variant: "xauna:winter" is ("xauna", "winter").
    internal IEnumerable<(string Base, string Variant)> Variants => Names
        .Where(n => n.Contains(':'))
        .Select(n => (n.Substring(0, n.IndexOf(':')), n.Substring(n.IndexOf(':') + 1)));

    // "Xauna (xauna)", "Vlandia (vlandia)", "mp_tdm_map_001".
    public override string ToString() => string.Equals(Name, Typed, StringComparison.OrdinalIgnoreCase) ? Name : Name + " (" + Typed + ")";
}

// Resolves what someone typed in chat (no autocomplete there) to one of a list of choices:
// 1. a number: the choice at that position in the list (as printed by !maps); not with a leading zero or past the
//    end of the list, so "004" or "013" match map names instead;
// 2. the exact id or name, ignoring case, spaces and punctuation;
// 3. the only choice whose id or name contains what was typed ("2f" for mp_skirmish_map_002f, "emp" for empire),
//    or of several, the only one whose id ends with it ("008": mp_skirmish_map_008, not mp_skirmish_map_008_skin);
// 4. the closest spelling, if it's clearly the best and close enough: what was typed is compared with the
//    best-matching part of each id and name, so "lighthose" finds mp_bnl_lighthouse (one letter off) despite
//    the prefix. One mistake is allowed per 4 letters typed; 3 letters or fewer must match exactly (step 3).
internal static class Matcher
{
    // numbered: what numbers pick (the list as !maps prints it), if not the choices themselves.
    public static Choice Resolve(string input, IList<Choice> choices, out string error, IList<Choice> numbered = null)
    {
        error = null;
        if (choices.Count == 0)
        {
            error = "Nothing to choose from.";
            return null;
        }
        int colon = input.IndexOf(':');
        if (colon >= 0)
        {
            return ResolveVariant(input.Substring(0, colon), input.Substring(colon + 1), choices, out error);
        }
        numbered ??= choices;
        if (int.TryParse(input, out int number) && !input.StartsWith("0") && number >= 1 && number <= numbered.Count)
        {
            return numbered[number - 1];
        }

        string key = Normalize(input);
        if (key.Length == 0)
        {
            error = "Nothing typed.";
            return null;
        }
        Choice exact = choices.FirstOrDefault(c => c.Keys.Any(k => Normalize(k) == key));
        if (exact != null)
        {
            return exact;
        }

        List<Choice> containing = choices.Where(c => c.Keys.Any(k => Normalize(k).Contains(key))).ToList();
        if (containing.Count == 1)
        {
            return containing[0];
        }
        List<Choice> ending = containing.Where(c => Normalize(c.Id).EndsWith(key)).ToList();
        if (containing.Count > 1 && ending.Count == 1)
        {
            return ending[0];
        }
        if (containing.Count > 1)
        {
            error = "'" + input + "' matches " + Describe(containing) + ". Be more specific, or use the number.";
            return null;
        }

        var ranked = choices
            .Select(c => (Choice: c, Distance: c.Keys.Min(k => PartDistance(key, Normalize(k)))))
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

    // "base:variant": the base is matched among the bases of all variant names, then the variant among that base's
    // variants, each the same way as a whole name, so "xuana:wintr" still finds xauna:winter.
    private static Choice ResolveVariant(string baseInput, string variantInput, IList<Choice> choices, out string error)
    {
        List<Choice> bases = choices.SelectMany(c => c.Variants.Select(v => v.Base))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(b => new Choice(b))
            .ToList();
        if (bases.Count == 0)
        {
            error = "No map has variants.";
            return null;
        }
        Choice chosenBase = Resolve(baseInput, bases, out error);
        if (chosenBase == null)
        {
            return null;
        }
        List<Choice> variants = choices
            .Select(c => new Choice(c.Id, c.Variants.Where(v => v.Base.Equals(chosenBase.Id, StringComparison.OrdinalIgnoreCase)).Select(v => v.Variant).ToArray()))
            .Where(c => c.Names.Count > 0)
            .ToList();
        Choice chosen = Resolve(variantInput, variants, out error);
        if (chosen == null)
        {
            error = chosenBase.Id + " variants: " + string.Join(", ", variants.Select(v => v.Names[0])) + ".";
            return null;
        }
        return choices.First(c => c.Id == chosen.Id);
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
