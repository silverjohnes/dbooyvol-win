using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DBooyvol.Core.Localization;

/// <summary>
/// Loads the macOS <c>Localizable.strings</c> packs unchanged: the key is the
/// Russian literal, a missing translation falls back to the key. Ukrainian
/// intentionally keeps Russian texts.
/// </summary>
public static class L10n
{
    private static readonly Regex LinePattern = new(
        "\\\"(?<key>(?:[^\\\"\\\\]|\\\\.)*)\\\"\\s*=\\s*\\\"(?<value>(?:[^\\\"\\\\]|\\\\.)*)\\\"\\s*;",
        RegexOptions.Compiled);

    private static Dictionary<string, string> _current = new(StringComparer.Ordinal);
    private static Dictionary<string, string> _fallback = new(StringComparer.Ordinal);

    public static string Language { get; private set; } = "ru";

    public static readonly (string Code, string Name)[] Languages =
    {
        ("en", "English"), ("es", "Español"), ("pt", "Português"), ("fr", "Français"),
        ("de", "Deutsch"), ("it", "Italiano"), ("nl", "Nederlands"), ("pl", "Polski"),
        ("tr", "Türkçe"), ("ru", "Русский"), ("uk", "Українська"), ("ar", "العربية"),
        ("hi", "हिन्दी"), ("zh-Hans", "简体中文"), ("zh-Hant", "繁體中文"), ("ja", "日本語"),
        ("ko", "한국어"), ("id", "Bahasa Indonesia"), ("vi", "Tiếng Việt"), ("th", "ไทย"),
        ("sv", "Svenska"), ("cs", "Čeština")
    };

    public static void Load(string localesDirectory, string? code)
    {
        Language = Resolve(code);
        _fallback = LoadPack(localesDirectory, "ru");
        _current = Language == "ru" ? _fallback : LoadPack(localesDirectory, Language);
    }

    public static string Resolve(string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested)) return requested;
        var culture = CultureInfo.CurrentUICulture;
        if (culture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            return culture.Name.Contains("Hant", StringComparison.OrdinalIgnoreCase) ? "zh-Hant" : "zh-Hans";
        var two = culture.TwoLetterISOLanguageName;
        return Languages.Any(l => l.Code == two) ? two : "en";
    }

    private static Dictionary<string, string> LoadPack(string directory, string code)
    {
        var path = Path.Combine(directory, code + ".lproj", "Localizable.strings");
        if (!File.Exists(path))
            path = Path.Combine(directory, "locales", code + ".lproj", "Localizable.strings");
        if (!File.Exists(path)) return new Dictionary<string, string>(StringComparer.Ordinal);

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in LinePattern.Matches(File.ReadAllText(path)))
            result[Unescape(match.Groups["key"].Value)] = Unescape(match.Groups["value"].Value);
        return result;
    }

    public static string Tr(string key)
    {
        if (key.Length == 0) return key;
        if (_current.TryGetValue(key, out var value)) return value;
        if (_fallback.TryGetValue(key, out var ru)) return ru;
        return key;
    }

    /// <summary>Sequential <c>%@</c> substitution; argument order is preserved.</summary>
    public static string Fmt(string key, params object?[] args)
    {
        var format = Tr(key);
        var result = new StringBuilder();
        var argIndex = 0;
        for (var i = 0; i < format.Length; i++)
        {
            if (format[i] == '%' && i + 1 < format.Length)
            {
                if (format[i + 1] == '%') { result.Append('%'); i++; continue; }
                if (format[i + 1] == '@' && argIndex < args.Length)
                {
                    result.Append(args[argIndex++]?.ToString() ?? "");
                    i++;
                    continue;
                }
            }
            result.Append(format[i]);
        }
        return result.ToString();
    }

    private static string Unescape(string value)
    {
        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\' || i + 1 >= value.Length) { sb.Append(value[i]); continue; }
            i++;
            sb.Append(value[i] switch
            {
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                '"' => '"',
                '\\' => '\\',
                _ => value[i]
            });
        }
        return sb.ToString();
    }
}
