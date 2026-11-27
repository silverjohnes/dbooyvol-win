using System.Text.RegularExpressions;

namespace DBooyvol.Core.Sql;

/// <summary>
/// DBeaver-style completion context: columns after WHERE/AND/ON/ORDER BY and
/// values after a comparison operator. Candidates come from the loaded result
/// and the SELECT list, never from the server.
/// </summary>
public static class SQLCompletion
{
    public enum Kind { Columns, Values }

    public sealed record Context(int Start, int Length, string Prefix, Kind Mode, string? Column);

    private static readonly (string Needle, string Title)[] ColumnKeywords =
    {
        ("order by", "order by"), ("group by", "group by"),
        ("where", "where"), ("having", "having"), ("on", "on"),
        ("and", "and"), ("or", "or"), ("set", "set")
    };

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "select", "from", "join", "insert", "update", "delete",
        "values", "returning", "union", "limit", "offset", "fetch"
    };

    public static Context? ContextAt(string text, int caret)
    {
        if (caret < 0 || caret > text.Length) return null;
        if (IsInsideLiteralOrComment(text, caret)) return null;

        var (wordStart, wordLength) = WordRange(text, caret);
        var prefix = text.Substring(wordStart, wordLength);
        var before = text[..wordStart];

        var semicolon = before.LastIndexOf(';');
        var statementStart = semicolon < 0 ? 0 : semicolon + 1;
        var statement = before[statementStart..];
        var lower = statement.ToLowerInvariant();

        var keyword = LastColumnKeyword(lower);
        if (keyword is null) return null;

        var afterKeyword = lower[keyword.Value.End..];
        var column = ComparisonColumn(afterKeyword);
        if (column is not null)
            return new Context(wordStart, wordLength, prefix, Kind.Values, column);

        var tail = afterKeyword;
        var tailWithoutPrefix = tail[..Math.Max(0, tail.Length - prefix.Length)];
        if (prefix.Length == 0 && Words(tailWithoutPrefix).Count > 0) return null;
        return new Context(wordStart, wordLength, prefix, Kind.Columns, null);
    }

    public static bool IsInsideLiteralOrComment(string text, int caret)
    {
        var lineStart = caret <= 0 ? 0 : text.LastIndexOf('\n', caret - 1) + 1;
        var linePrefix = text[lineStart..caret];
        var dash = linePrefix.LastIndexOf("--", StringComparison.Ordinal);
        if (dash >= 0 && !linePrefix[..dash].Contains('\'')) return true;

        var insideString = false;
        var index = Math.Max(0, caret - 20_000);
        while (index < caret)
        {
            if (text[index] == '\'')
            {
                if (insideString && index + 1 < caret && text[index + 1] == '\'') index++;
                else insideString = !insideString;
            }
            index++;
        }
        return insideString;
    }

    public static (int Start, int Length) WordRange(string text, int caret)
    {
        var start = caret;
        while (start > 0 && IsWordCharacter(text[start - 1])) start--;
        return (start, caret - start);
    }

    private static bool IsWordCharacter(char c) =>
        char.IsLetterOrDigit(c) || c is '_' or '.' or '$' or '"';

    private static (string Keyword, int End)? LastColumnKeyword(string lower)
    {
        (string Keyword, int End)? best = null;
        foreach (var (needle, _) in ColumnKeywords)
        {
            var searchEnd = lower.Length;
            while (searchEnd >= needle.Length)
            {
                var found = lower.LastIndexOf(needle, searchEnd - 1, StringComparison.Ordinal);
                if (found < 0) break;
                var beforeOk = found == 0 || !IsLetter(lower[found - 1]);
                var afterPos = found + needle.Length;
                var afterOk = afterPos >= lower.Length || !IsLetter(lower[afterPos]);
                if (beforeOk && afterOk)
                {
                    if (best is null || afterPos > best.Value.End)
                        best = (needle, afterPos);
                    break;
                }
                if (found == 0) break;
                searchEnd = found;
            }
        }
        if (best is null) return null;

        var tail = lower[best.Value.End..];
        var tailWords = Words(tail);
        if (tailWords.Count > 0 && StopWords.Contains(tailWords[0])) return null;
        if (tailWords.Any(StopWords.Contains)) return null;
        return best;
    }

    private static bool IsLetter(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static List<string> Words(string text) =>
        Regex.Split(text.ToLowerInvariant(), @"[^\p{L}\p{N}_]+")
            .Where(s => s.Length > 0)
            .ToList();

    private static readonly Regex[] ComparisonPatterns =
    {
        new(@"(?<col>[A-Za-z_][A-Za-z0-9_\.]*)\s*(?:=|!=|<>|>=|<=|>|<|like|ilike)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"(?<col>[A-Za-z_][A-Za-z0-9_\.]*)\s+(?:not\s+)?in\s*\(\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled)
    };

    private static readonly HashSet<string> ColumnStop = new(StringComparer.Ordinal)
    {
        "and", "or", "where", "not", "is", "null", "on", "having"
    };

    private static string? ComparisonColumn(string tail)
    {
        var text = tail.Trim();
        if (text.Length == 0) return null;
        foreach (var pattern in ComparisonPatterns)
        {
            var match = pattern.Match(text);
            if (!match.Success) continue;
            var candidate = match.Groups["col"].Value;
            if (!ColumnStop.Contains(candidate.ToLowerInvariant())) return candidate;
        }
        return null;
    }

    public static List<string> SelectListColumns(string sql)
    {
        var selectIndex = sql.IndexOf("select", StringComparison.OrdinalIgnoreCase);
        if (selectIndex < 0) return new List<string>();
        var rest = sql[(selectIndex + 6)..];
        var fromIndex = rest.IndexOf("from", StringComparison.OrdinalIgnoreCase);
        var list = fromIndex < 0 ? rest : rest[..fromIndex];

        var items = new List<string>();
        var current = new System.Text.StringBuilder();
        var depth = 0;
        foreach (var c in list)
        {
            if (c == '(') depth++;
            if (c == ')') depth--;
            if (c == ',' && depth == 0) { items.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }
        if (!string.IsNullOrWhiteSpace(current.ToString())) items.Add(current.ToString());

        var names = new List<string>();
        foreach (var item in items)
        {
            var trimmed = item.Trim();
            if (trimmed.Length == 0 || trimmed == "*" || trimmed.EndsWith(".*", StringComparison.Ordinal)) continue;
            var lower = trimmed.ToLowerInvariant();
            var asIndex = lower.LastIndexOf(" as ", StringComparison.Ordinal);
            if (asIndex >= 0)
            {
                var alias = trimmed[(asIndex + 4)..].Trim();
                if (alias.Length > 0) names.Add(alias);
                continue;
            }
            var parts = Regex.Split(trimmed, @"[^\p{L}\p{N}_.]+")
                .Where(s => s.Length > 0).ToList();
            if (parts.Count > 0)
            {
                var last = parts[^1].Split('.').Last();
                if (last.Length > 0) names.Add(last);
            }
        }
        return names;
    }

    public static List<string> Filter(IEnumerable<string> candidates, string prefix)
    {
        var needle = prefix.Trim('"').ToLowerInvariant();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var candidate in candidates)
        {
            var key = candidate.ToLowerInvariant();
            if (!seen.Add(key)) continue;
            if (needle.Length == 0 || key.StartsWith(needle, StringComparison.Ordinal) ||
                key.Contains(needle, StringComparison.Ordinal))
                result.Add(candidate);
            if (result.Count >= 60) break;
        }
        return result;
    }

    /// <summary>Values from the loaded result: at most 500 distinct, numbers
    /// unquoted, strings in single quotes with doubled quotes.</summary>
    public static List<string> ValueCandidates(string column, IReadOnlyList<string> columns,
        IReadOnlyList<string?[]> rows, int max = 500)
    {
        var name = column.Split('.').Last();
        var index = -1;
        for (var i = 0; i < columns.Count; i++)
        {
            if (string.Equals(columns[i], name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(columns[i], column, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }
        if (index < 0) return new List<string>();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var row in rows)
        {
            if (index >= row.Length) continue;
            var value = row[index];
            if (string.IsNullOrEmpty(value)) continue;
            var rendered = DBooyvol.Core.Text.NumericText.IsNumeric(value)
                ? value
                : "'" + value.Replace("'", "''") + "'";
            if (!seen.Add(rendered)) continue;
            result.Add(rendered);
            if (result.Count >= max) break;
        }
        return result;
    }
}
