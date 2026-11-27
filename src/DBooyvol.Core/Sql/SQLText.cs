namespace DBooyvol.Core.Sql;

public readonly record struct TextSpan(int Start, int Length, string Text);

/// <summary>
/// Statement splitting, named parameters, smart selection and comment
/// toggling. Ported from the macOS <c>SQLText</c>; the rules (dollar quotes,
/// doubled quotes, <c>::</c>, <c>:=</c>, LIKE masks) are intentionally identical.
/// </summary>
public static class SQLText
{
    private static readonly HashSet<string> ClauseEndKeywords = new(StringComparer.Ordinal)
    {
        "GROUP", "ORDER", "HAVING", "LIMIT", "OFFSET", "FETCH", "UNION",
        "EXCEPT", "INTERSECT", "RETURNING", "WINDOW", "CONNECT", "START", "MODEL"
    };

    private static readonly HashSet<string> OperatorResetsMask = new(StringComparer.Ordinal)
    {
        "IN", "BETWEEN", "IS", "AND", "OR", "NOT", "SIMILAR", "REGEXP", "RLIKE"
    };

    public static List<TextSpan> Statements(string sql)
    {
        var result = new List<TextSpan>();
        var start = 0;
        var i = 0;
        var n = sql.Length;

        void Flush(int end)
        {
            var text = sql.Substring(start, end - start);
            if (!string.IsNullOrWhiteSpace(text))
                result.Add(new TextSpan(start, end - start, text));
        }

        while (i < n)
        {
            var c = sql[i];
            if (c == '-' && i + 1 < n && sql[i + 1] == '-')
            {
                while (i < n && sql[i] != '\n') i++;
                continue;
            }
            if (c == '/' && i + 1 < n && sql[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < n && !(sql[i] == '*' && sql[i + 1] == '/')) i++;
                i = Math.Min(i + 2, n);
                continue;
            }
            if (c == '$' && DollarTag(sql, i) is { } tag)
            {
                var close = "$" + tag + "$";
                var found = sql.IndexOf(close, i + close.Length, StringComparison.Ordinal);
                i = found < 0 ? n : found + close.Length;
                continue;
            }
            if (c is '\'' or '"')
            {
                var quote = c;
                i++;
                while (i < n)
                {
                    var d = sql[i];
                    if (d == quote)
                    {
                        if (i + 1 < n && sql[i + 1] == quote) { i += 2; continue; }
                        i++;
                        break;
                    }
                    i++;
                }
                continue;
            }
            if (c == ';')
            {
                Flush(i);
                i++;
                start = i;
                continue;
            }
            i++;
        }
        Flush(n);
        return result;
    }

    public static string? StatementAt(string sql, int caret)
    {
        var list = Statements(sql);
        var pos = Math.Min(Math.Max(caret, 0), Math.Max(0, sql.Length - 1));
        foreach (var item in list)
        {
            if (pos >= item.Start && pos < item.Start + Math.Max(item.Length, 1))
                return item.Text.Trim();
        }
        return list.Count > 0 ? list[^1].Text.Trim() : null;
    }

    public static List<string> Parameters(string sql)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        var n = sql.Length;

        while (i < n)
        {
            var c = sql[i];
            if (c == '-' && i + 1 < n && sql[i + 1] == '-')
            {
                while (i < n && sql[i] != '\n') i++;
                continue;
            }
            if (c == '/' && i + 1 < n && sql[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < n && !(sql[i] == '*' && sql[i + 1] == '/')) i++;
                i = Math.Min(i + 2, n);
                continue;
            }
            if (c == '$' && DollarTag(sql, i) is { } tag)
            {
                var close = "$" + tag + "$";
                var found = sql.IndexOf(close, i + close.Length, StringComparison.Ordinal);
                i = found < 0 ? n : found + close.Length;
                continue;
            }
            if (c is '\'' or '"')
            {
                var quote = c;
                i++;
                while (i < n)
                {
                    var d = sql[i];
                    if (d == quote)
                    {
                        if (i + 1 < n && sql[i + 1] == quote) { i += 2; continue; }
                        i++;
                        break;
                    }
                    i++;
                }
                continue;
            }
            if (c == ':' && i + 1 < n)
            {
                var next = sql[i + 1];
                if (next is ':' or '=') { i += 2; continue; }
                if (IsWordStart(next))
                {
                    var j = i + 2;
                    while (j < n && IsWord(sql[j])) j++;
                    var name = sql.Substring(i + 1, j - i - 1);
                    if (seen.Add(name)) names.Add(name);
                    i = j;
                    continue;
                }
            }
            i++;
        }
        return names;
    }

    public static bool IsRowReturning(string sql)
    {
        var stripped = StripLeadingComments(sql).TrimStart();
        if (stripped.Length == 0) return false;
        var word = ReadWord(stripped, 0).ToUpperInvariant();
        return word is "SELECT" or "WITH" or "VALUES" or "TABLE";
    }

    public static string? FirstRowReturningStatement(string sql)
    {
        foreach (var statement in Statements(sql))
        {
            if (IsRowReturning(statement.Text)) return statement.Text.Trim();
        }
        return null;
    }

    /// <summary>Range of the first non-empty quoted value in a WHERE clause.</summary>
    public static (int Start, int Length)? FirstQuotedFilterValue(string sql)
    {
        var n = sql.Length;
        var i = 0;
        while (i < n)
        {
            var c = sql[i];
            if (c == '-' && i + 1 < n && sql[i + 1] == '-')
            {
                while (i < n && sql[i] != '\n') i++;
                continue;
            }
            if (c == '/' && i + 1 < n && sql[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < n && !(sql[i] == '*' && sql[i + 1] == '/')) i++;
                i = Math.Min(i + 2, n);
                continue;
            }
            if (c == '$' && DollarTag(sql, i) is { } tag)
            {
                var close = "$" + tag + "$";
                var found = sql.IndexOf(close, i + close.Length, StringComparison.Ordinal);
                i = found < 0 ? n : found + close.Length;
                continue;
            }
            if (c is '\'' or '"')
            {
                i = SkipQuoted(sql, i, c, n);
                continue;
            }
            if (IsWordStart(c))
            {
                var start = i;
                var j = i + 1;
                while (j < n && IsWord(sql[j])) j++;
                var word = sql.Substring(start, j - start).ToUpperInvariant();
                if (word == "WHERE" && QuotedValueInClause(sql, j) is { } value)
                    return value;
                i = j;
                continue;
            }
            i++;
        }
        return null;
    }

    private static (int Start, int Length)? QuotedValueInClause(string sql, int index)
    {
        var n = sql.Length;
        var i = index;
        var depth = 0;
        var isLike = false;

        while (i < n)
        {
            var c = sql[i];
            if (c == '-' && i + 1 < n && sql[i + 1] == '-')
            {
                while (i < n && sql[i] != '\n') i++;
                continue;
            }
            if (c == '/' && i + 1 < n && sql[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < n && !(sql[i] == '*' && sql[i + 1] == '/')) i++;
                i = Math.Min(i + 2, n);
                continue;
            }
            if (c == '$' && DollarTag(sql, i) is { } tag)
            {
                var close = "$" + tag + "$";
                var found = sql.IndexOf(close, i + close.Length, StringComparison.Ordinal);
                i = found < 0 ? n : found + close.Length;
                continue;
            }
            if (c == '\'')
            {
                var j = i + 1;
                while (j < n)
                {
                    var d = sql[j];
                    if (d == '\'')
                    {
                        if (j + 1 < n && sql[j + 1] == '\'') { j += 2; continue; }
                        break;
                    }
                    j++;
                }
                var end = Math.Min(j, n);
                if (end > i + 1)
                {
                    var start = i + 1;
                    var length = end - i - 1;
                    if (isLike)
                    {
                        var trimmed = TrimMask(sql, start, length);
                        start = trimmed.Start;
                        length = trimmed.Length;
                    }
                    if (length > 0) return (start, length);
                }
                i = Math.Min(end + 1, n);
                continue;
            }
            if (c == '"')
            {
                i = SkipQuoted(sql, i, c, n);
                continue;
            }
            if (c == '(') { depth++; i++; continue; }
            if (c == ')')
            {
                if (depth == 0) return null;
                depth--;
                i++;
                continue;
            }
            if (c == ';' && depth == 0) return null;
            if (IsWordStart(c))
            {
                var start = i;
                var j = i + 1;
                while (j < n && IsWord(sql[j])) j++;
                var word = sql.Substring(start, j - start).ToUpperInvariant();
                if (depth == 0 && ClauseEndKeywords.Contains(word)) return null;
                if (word is "LIKE" or "ILIKE") isLike = true;
                else if (OperatorResetsMask.Contains(word)) isLike = false;
                i = j;
                continue;
            }
            if (c is '=' or '<' or '>' or '!' or '~') isLike = false;
            i++;
        }
        return null;
    }

    /// <summary>Removes LIKE wildcards from a triple-click selection.</summary>
    public static (int Start, int Length) MaskTrimmedRange(string sql, int start, int length)
    {
        if (length <= 0 || start <= 0 || start > sql.Length) return (start, length);
        if (sql[start - 1] != '\'') return (start, length);
        if (!IsLikeOperatorBefore(sql, start - 1)) return (start, length);
        var trimmed = TrimMask(sql, start, length);
        return trimmed.Length > 0 ? trimmed : (start, length);
    }

    private static bool IsLikeOperatorBefore(string sql, int index)
    {
        var i = 0;
        var isLike = false;
        while (i < index)
        {
            var c = sql[i];
            if (c == '-' && i + 1 < index && sql[i + 1] == '-')
            {
                while (i < index && sql[i] != '\n') i++;
                continue;
            }
            if (c == '/' && i + 1 < index && sql[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < index && !(sql[i] == '*' && sql[i + 1] == '/')) i++;
                i = Math.Min(i + 2, index);
                continue;
            }
            if (c is '\'' or '"')
            {
                i = SkipQuoted(sql, i, c, index);
                continue;
            }
            if (IsWordStart(c))
            {
                var start = i;
                var j = i + 1;
                while (j < index && IsWord(sql[j])) j++;
                var word = sql.Substring(start, j - start).ToUpperInvariant();
                if (word is "LIKE" or "ILIKE") isLike = true;
                else if (OperatorResetsMask.Contains(word)) isLike = false;
                i = j;
                continue;
            }
            if (c is '=' or '<' or '>' or '!' or '~') isLike = false;
            i++;
        }
        return isLike;
    }

    private static (int Start, int Length) TrimMask(string sql, int start, int length)
    {
        var end = start + length;
        while (start < end && (sql[start] == '%' || sql[start] == '_')) start++;
        while (end > start && (sql[end - 1] == '%' || sql[end - 1] == '_')) end--;
        return (start, end - start);
    }

    /// <summary>Ctrl+/: prefixes affected lines with <c>--</c> or removes it.</summary>
    public static (string Text, int SelectionStart, int SelectionLength) ToggleComment(
        string text, int selectionStart, int selectionLength)
    {
        var start = Math.Clamp(selectionStart, 0, text.Length);
        var length = Math.Clamp(selectionLength, 0, text.Length - start);
        if (length > 0)
        {
            var end = start + length;
            if (end > start && end <= text.Length && (end == 0 || text[end - 1] == '\n'))
                length--;
        }

        var (lineStart, lineEnd) = LineRange(text, start, length);
        var block = text.Substring(lineStart, lineEnd - lineStart);
        if (block.Length == 0) return (text, selectionStart, selectionLength);

        var hasTrailingNewline = block.EndsWith('\n');
        var lines = block.Split('\n').ToList();
        if (hasTrailingNewline && lines.Count > 0) lines.RemoveAt(lines.Count - 1);

        var meaningful = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (meaningful.Count == 0) return (text, selectionStart, selectionLength);
        var allCommented = meaningful.All(IsLineCommented);

        var updated = lines.Select(line =>
            string.IsNullOrWhiteSpace(line) ? line : (allCommented ? Uncomment(line) : Comment(line))).ToList();

        var replacement = string.Join("\n", updated) + (hasTrailingNewline ? "\n" : "");
        if (replacement == block) return (text, selectionStart, selectionLength);

        var newText = text[..lineStart] + replacement + text[lineEnd..];
        var caret = MapPosition(lines, updated, lineStart, start);
        if (length == 0) return (newText, caret, 0);
        var endMapped = MapPosition(lines, updated, lineStart, start + length);
        return (newText, caret, Math.Max(0, endMapped - caret));
    }

    /// <summary>Ctrl+Alt+/: toggles every line ending with the marker.</summary>
    public static (string Text, int SelectionStart, int SelectionLength) ToggleMarkedComment(
        string text, string marker, int selectionStart, int selectionLength)
    {
        marker = marker.Trim();
        if (marker.Length < 2 || text.Length == 0) return (text, selectionStart, selectionLength);

        var hasTrailingNewline = text.EndsWith('\n');
        var lines = text.Split('\n').ToList();
        if (hasTrailingNewline && lines.Count > 0) lines.RemoveAt(lines.Count - 1);

        var marked = new List<int>();
        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();
            if (!trimmed.EndsWith(marker, StringComparison.Ordinal)) continue;
            var body = trimmed[..^marker.Length];
            if (!string.IsNullOrWhiteSpace(body)) marked.Add(i);
        }
        if (marked.Count == 0) return (text, selectionStart, selectionLength);

        var allCommented = marked.All(i => IsLineCommented(lines[i]));
        var updated = lines.ToList();
        foreach (var i in marked)
            updated[i] = allCommented ? Uncomment(lines[i]) : Comment(lines[i]);

        var replacement = string.Join("\n", updated) + (hasTrailingNewline ? "\n" : "");
        if (replacement == text) return (text, selectionStart, selectionLength);

        var caret = MapPosition(lines, updated, 0, Math.Min(selectionStart, text.Length));
        if (selectionLength == 0) return (replacement, caret, 0);
        var endMapped = MapPosition(lines, updated, 0, Math.Min(selectionStart + selectionLength, text.Length));
        return (replacement, caret, Math.Max(0, endMapped - caret));
    }

    public static bool IsLineCommented(string line) => line.TrimStart(' ', '\t').StartsWith("--", StringComparison.Ordinal);

    private static string Comment(string line)
    {
        var indent = IndentPrefix(line);
        var rest = line[indent.Length..];
        return rest.StartsWith("--", StringComparison.Ordinal) ? line : indent + "--" + rest;
    }

    private static string Uncomment(string line)
    {
        var indent = IndentPrefix(line);
        var rest = line[indent.Length..];
        if (!rest.StartsWith("--", StringComparison.Ordinal)) return line;
        rest = rest[2..];
        if (rest.StartsWith(' ')) rest = rest[1..];
        return indent + rest;
    }

    private static string IndentPrefix(string line)
    {
        var i = 0;
        while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
        return line[..i];
    }

    private static int MapPosition(List<string> oldLines, List<string> newLines, int blockOffset, int location)
    {
        var rel = Math.Max(0, location - blockOffset);
        if (oldLines.Count == 0) return blockOffset;

        var oldStarts = new int[oldLines.Count];
        var newStarts = new int[newLines.Count];
        var newLens = new int[newLines.Count];
        var prefixLens = new int[oldLines.Count];
        var deltas = new int[oldLines.Count];
        var oldOffset = 0;
        var newOffset = 0;
        for (var i = 0; i < oldLines.Count; i++)
        {
            oldStarts[i] = oldOffset;
            newStarts[i] = newOffset;
            newLens[i] = newLines[i].Length;
            prefixLens[i] = IndentPrefix(oldLines[i]).Length;
            deltas[i] = newLines[i].Length - oldLines[i].Length;
            oldOffset += oldLines[i].Length + 1;
            newOffset += newLines[i].Length + 1;
        }

        var index = oldLines.Count - 1;
        while (index > 0 && rel < oldStarts[index]) index--;
        var inLine = rel - oldStarts[index];
        if (inLine <= prefixLens[index])
            return blockOffset + newStarts[index] + inLine;
        var shifted = inLine + deltas[index];
        return blockOffset + newStarts[index] + Math.Min(Math.Max(prefixLens[index], shifted), newLens[index]);
    }

    private static (int Start, int End) LineRange(string text, int start, int length)
    {
        var lineStart = start <= 0 ? 0 : text.LastIndexOf('\n', start - 1) + 1;
        var pos = start + length;
        var lineEnd = pos >= text.Length ? text.Length : text.IndexOf('\n', pos);
        if (lineEnd < 0) lineEnd = text.Length; else lineEnd++;
        return (lineStart, lineEnd);
    }

    private static string StripLeadingComments(string sql)
    {
        var i = 0;
        while (i < sql.Length)
        {
            while (i < sql.Length && char.IsWhiteSpace(sql[i])) i++;
            if (i + 1 < sql.Length && sql[i] == '-' && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n') i++;
                continue;
            }
            if (i + 1 < sql.Length && sql[i] == '/' && sql[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < sql.Length && !(sql[i] == '*' && sql[i + 1] == '/')) i++;
                i = Math.Min(i + 2, sql.Length);
                continue;
            }
            break;
        }
        return sql[i..];
    }

    private static string ReadWord(string s, int index)
    {
        var j = index;
        while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] == '_')) j++;
        return s[index..j];
    }

    private static int SkipQuoted(string s, int index, char quote, int limit)
    {
        var i = index + 1;
        while (i < limit)
        {
            var d = s[i];
            if (d == quote)
            {
                if (i + 1 < limit && s[i + 1] == quote) { i += 2; continue; }
                i++;
                break;
            }
            i++;
        }
        return Math.Min(i, limit);
    }

    private static string? DollarTag(string s, int index)
    {
        var j = index + 1;
        var tag = "";
        while (j < s.Length)
        {
            var c = s[j];
            if (c == '$') return tag;
            if (char.IsLetterOrDigit(c) || c == '_') { tag += c; j++; }
            else return null;
        }
        return null;
    }

    private static bool IsWordStart(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_';
    private static bool IsWord(char c) => IsWordStart(c) || char.IsDigit(c) || c is '$' or '#';
}
