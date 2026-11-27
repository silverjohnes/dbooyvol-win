using System.Globalization;
using System.Text.RegularExpressions;

namespace DBooyvol.Core.Text;

/// <summary>
/// Timestamps are converted for display only. A value without an offset is
/// treated as UTC (the target databases store UTC); the stored string itself is
/// never rewritten.
/// </summary>
public static class TimestampDisplay
{
    private static readonly Regex IsoPattern = new(
        @"^(?<y>\d{4})-(?<mo>\d{2})-(?<d>\d{2})[T ](?<h>\d{2}):(?<mi>\d{2}):(?<s>\d{2})(?:\.(?<f>\d{1,9}))?(?<off>Z|[+-]\d{2}:?\d{2})?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool IsTimestampType(string? columnType)
    {
        if (string.IsNullOrEmpty(columnType)) return false;
        var t = columnType.ToUpperInvariant();
        return t.Contains("TIMESTAMP") || t.Contains("DATETIME");
    }

    public static string? Formatted(string? value, string? columnType, TimeZoneInfo? timeZone)
    {
        if (value is null) return null;
        if (!IsTimestampType(columnType)) return value;
        var match = IsoPattern.Match(value);
        if (!match.Success) return value;

        try
        {
            var y = int.Parse(match.Groups["y"].Value, CultureInfo.InvariantCulture);
            var mo = int.Parse(match.Groups["mo"].Value, CultureInfo.InvariantCulture);
            var d = int.Parse(match.Groups["d"].Value, CultureInfo.InvariantCulture);
            var h = int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture);
            var mi = int.Parse(match.Groups["mi"].Value, CultureInfo.InvariantCulture);
            var s = int.Parse(match.Groups["s"].Value, CultureInfo.InvariantCulture);
            var fraction = match.Groups["f"].Success ? match.Groups["f"].Value : "";
            var millis = fraction.Length == 0
                ? 0
                : int.Parse(fraction.Length >= 3 ? fraction[..3] : fraction.PadRight(3, '0'),
                    CultureInfo.InvariantCulture);

            DateTimeOffset instant;
            var off = match.Groups["off"].Value;
            if (string.IsNullOrEmpty(off))
            {
                instant = new DateTimeOffset(y, mo, d, h, mi, s, millis, TimeSpan.Zero);
            }
            else
            {
                TimeSpan offset;
                if (off == "Z") offset = TimeSpan.Zero;
                else
                {
                    var sign = off[0] == '-' ? -1 : 1;
                    var digits = off[1..].Replace(":", "");
                    var hours = int.Parse(digits[..2], CultureInfo.InvariantCulture);
                    var minutes = int.Parse(digits[2..], CultureInfo.InvariantCulture);
                    offset = TimeSpan.FromMinutes(sign * (hours * 60 + minutes));
                }
                instant = new DateTimeOffset(y, mo, d, h, mi, s, millis, offset);
            }

            var tz = timeZone ?? TimeZoneInfo.Local;
            var converted = TimeZoneInfo.ConvertTime(instant, tz);
            var baseText = converted.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            return millis != 0 ? $"{baseText}.{millis:000}" : baseText;
        }
        catch (Exception)
        {
            return value;
        }
    }

    public static string? ShortZoneCode(string? id, DateTimeOffset? at = null)
    {
        return TimeZoneCatalog.ShortCode(id, at ?? DateTimeOffset.UtcNow);
    }
}
