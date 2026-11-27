using System.Text;

namespace DBooyvol.Core.Text;

/// <summary>
/// Layout-independent search: a query typed in the wrong keyboard layout
/// matches the same physical keys. Used by Quick Open, script search and
/// result search.
/// </summary>
public static class SearchMatcher
{
    private static readonly Dictionary<char, char> ToLatin = BuildMap();

    private static Dictionary<char, char> BuildMap()
    {
        const string ru = "йцукенгшщзхъфывапролджэячсмитьбю";
        const string en = "qwertyuiop[]asdfghjkl;'zxcvbnm,.";
        var map = new Dictionary<char, char>();
        for (var i = 0; i < ru.Length && i < en.Length; i++)
        {
            map[ru[i]] = en[i];
            map[char.ToUpperInvariant(ru[i])] = char.ToUpperInvariant(en[i]);
        }
        // The macOS map also includes ё on the ` key.
        map['ё'] = '`';
        map['Ё'] = '~';
        return map;
    }

    /// <summary>The query as typed and its other-layout twin.</summary>
    public static (string AsTyped, string Alternate) Variants(string query)
    {
        var alternate = new StringBuilder(query.Length);
        foreach (var c in query)
            alternate.Append(ToLatin.TryGetValue(c, out var latin) ? latin : c);
        return (query, alternate.ToString());
    }

    public static bool Matches(string text, string query)
    {
        if (string.IsNullOrEmpty(query)) return true;
        var (a, b) = Variants(query);
        return text.Contains(a, StringComparison.OrdinalIgnoreCase)
            || (!string.Equals(a, b, StringComparison.Ordinal) &&
                text.Contains(b, StringComparison.OrdinalIgnoreCase));
    }

    public static int IndexOf(string text, string query, int startIndex = 0)
    {
        if (string.IsNullOrEmpty(query)) return -1;
        var (a, b) = Variants(query);
        var ia = text.IndexOf(a, startIndex, StringComparison.OrdinalIgnoreCase);
        if (string.Equals(a, b, StringComparison.Ordinal)) return ia;
        var ib = text.IndexOf(b, startIndex, StringComparison.OrdinalIgnoreCase);
        if (ia < 0) return ib;
        if (ib < 0) return ia;
        return Math.Min(ia, ib);
    }

    public static List<(int Start, int Length)> AllRanges(string text, string query)
    {
        var result = new List<(int, int)>();
        if (string.IsNullOrEmpty(query) || text.Length == 0) return result;
        var (a, b) = Variants(query);
        AddRanges(text, a, result);
        if (!string.Equals(a, b, StringComparison.Ordinal)) AddRanges(text, b, result);
        return result
            .Distinct()
            .OrderBy(r => r.Item1)
            .ToList();
    }

    private static void AddRanges(string text, string query, List<(int, int)> result)
    {
        var index = 0;
        while (index <= text.Length - query.Length)
        {
            var found = text.IndexOf(query, index, StringComparison.OrdinalIgnoreCase);
            if (found < 0) break;
            result.Add((found, query.Length));
            index = found + Math.Max(1, query.Length);
        }
    }

    public enum Language { Other, Cyrillic, Latin }

    public static Language Detect(string text)
    {
        var cyrillic = 0;
        var latin = 0;
        foreach (var c in text)
        {
            if (c is >= 'А' and <= 'я' or 'Ё' or 'ё') cyrillic++;
            else if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z') latin++;
        }
        if (cyrillic == 0 && latin == 0) return Language.Other;
        return cyrillic >= latin ? Language.Cyrillic : Language.Latin;
    }
}
