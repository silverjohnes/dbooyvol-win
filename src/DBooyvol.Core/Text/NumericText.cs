namespace DBooyvol.Core.Text;

public static class NumericText
{
    /// <summary>Both <c>.</c> and <c>,</c> are decimal separators for hints.</summary>
    public static bool IsNumeric(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var s = value.Trim();
        if (s.Length == 0) return false;
        var i = 0;
        if (s[0] is '+' or '-') i++;
        var digits = 0;
        var separators = 0;
        for (; i < s.Length; i++)
        {
            var c = s[i];
            if (char.IsDigit(c)) { digits++; continue; }
            if (c is '.' or ',') { separators++; if (separators > 1) return false; continue; }
            return false;
        }
        return digits > 0;
    }
}
