namespace DBooyvol.Core.Text;

/// <summary>
/// Double/triple click rules shared by the editor and the value inspector:
/// a word keeps dots and underscores, triple click selects the contents of a
/// quoted literal, the innermost parentheses, or between <c>%</c> wildcards.
/// </summary>
public static class TextSelectionRules
{
    public static bool IsWordChar(char c) => c is '_' or '$' or '.' || char.IsLetterOrDigit(c);

    public static (int Start, int Length)? WordRange(string s, int location)
    {
        if (s.Length == 0) return null;
        var pos = Math.Clamp(location, 0, s.Length - 1);
        var a = pos;
        var b = pos;
        if (!IsWordChar(s[pos]))
        {
            if (pos > 0 && IsWordChar(s[pos - 1])) { a = pos - 1; b = pos - 1; }
            else return null;
        }
        while (a > 0 && IsWordChar(s[a - 1])) a--;
        while (b < s.Length - 1 && IsWordChar(s[b + 1])) b++;
        return b >= a ? (a, b - a + 1) : null;
    }

    public static (int Start, int Length)? QuotedContentsRange(string s, int location)
    {
        if (s.Length == 0) return null;
        var pos = Math.Clamp(location, 0, s.Length - 1);
        var (lineStart, contentsEnd) = LineBounds(s, pos);

        var quoteStart = -1;
        var quoteChar = '\0';
        var i = lineStart;
        while (i < contentsEnd)
        {
            var c = s[i];
            if (c is '\'' or '"')
            {
                if (quoteStart < 0) { quoteStart = i; quoteChar = c; }
                else if (c == quoteChar)
                {
                    if (i + 1 < contentsEnd && s[i + 1] == quoteChar) { i += 2; continue; }
                    if (pos >= quoteStart && pos <= i)
                        return (quoteStart + 1, i - quoteStart - 1);
                    quoteStart = -1;
                }
            }
            i++;
        }
        return null;
    }

    public static (int Start, int Length)? ParenContentsRange(string s, int location)
    {
        if (s.Length == 0) return null;
        var pos = Math.Clamp(location, 0, s.Length - 1);

        var stack = new Stack<int>();
        (int Open, int Close)? best = null;
        var quote = '\0';
        var lineComment = false;
        var blockComment = false;
        var i = 0;

        while (i < s.Length)
        {
            var c = s[i];
            var next = i + 1 < s.Length ? s[i + 1] : '\0';

            if (lineComment)
            {
                if (c is '\n' or '\r') lineComment = false;
                i++;
                continue;
            }
            if (blockComment)
            {
                if (c == '*' && next == '/') { blockComment = false; i += 2; continue; }
                i++;
                continue;
            }
            if (quote != '\0')
            {
                if (c == quote)
                {
                    if (next == quote) { i += 2; continue; }
                    quote = '\0';
                }
                i++;
                continue;
            }
            if (c == '-' && next == '-') { lineComment = true; i += 2; continue; }
            if (c == '/' && next == '*') { blockComment = true; i += 2; continue; }
            if (c is '\'' or '"') { quote = c; i++; continue; }

            if (c == '(') stack.Push(i);
            else if (c == ')' && stack.Count > 0)
            {
                var open = stack.Pop();
                if (pos >= open && pos <= i && (best is null || open > best.Value.Open))
                    best = (open, i);
            }
            i++;
        }

        return best is { } b ? (b.Open + 1, b.Close - b.Open - 1) : null;
    }

    public static (int Start, int Length)? LiteralContentsRange(string s, int location)
    {
        if (QuotedContentsRange(s, location) is { } quoted)
            return TrimPercent(s, quoted);
        if (ParenContentsRange(s, location) is { } paren)
            return TrimPercent(s, paren);
        return PercentContentsRange(s, location);
    }

    public static (int Start, int Length) TrimPercent(string s, (int Start, int Length) range)
    {
        var start = range.Start;
        var end = range.Start + range.Length;
        while (start < end && s[start] == '%') start++;
        while (end > start && s[end - 1] == '%') end--;
        return end - start > 0 ? (start, end - start) : range;
    }

    public static (int Start, int Length)? PercentContentsRange(string s, int location)
    {
        if (s.Length == 0) return null;
        var pos = Math.Clamp(location, 0, s.Length - 1);
        var (lineStart, contentsEnd) = LineBounds(s, pos);

        var masks = new List<int>();
        for (var i = lineStart; i < contentsEnd; i++)
            if (s[i] == '%') masks.Add(i);
        if (masks.Count < 2) return null;

        int? open = null;
        int? close = null;
        if (masks.Contains(pos))
        {
            var next = masks.FirstOrDefault(m => m > pos, -1);
            if (next >= 0) { open = pos; close = next; }
            else
            {
                var prev = masks.LastOrDefault(m => m < pos, -1);
                if (prev >= 0) { open = prev; close = pos; }
            }
        }
        else
        {
            open = masks.LastOrDefault(m => m < pos, -1);
            close = masks.FirstOrDefault(m => m > pos, -1);
            if (open < 0) open = null;
            if (close < 0) close = null;
        }
        if (open is null || close is null || close <= open) return null;

        var trimmed = TrimPercent(s, (open.Value + 1, close.Value - open.Value - 1));
        return trimmed.Length > 0 ? trimmed : null;
    }

    private static (int LineStart, int ContentsEnd) LineBounds(string s, int pos)
    {
        var lineStart = pos <= 0 ? 0 : s.LastIndexOf('\n', pos - 1) + 1;
        var newline = s.IndexOf('\n', pos);
        var contentsEnd = newline < 0 ? s.Length : newline;
        if (contentsEnd > lineStart && s[contentsEnd - 1] == '\r') contentsEnd--;
        return (lineStart, contentsEnd);
    }
}
