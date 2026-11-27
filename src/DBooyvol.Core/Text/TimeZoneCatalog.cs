using System.Globalization;

namespace DBooyvol.Core.Text;

public sealed record TimeZoneOption(string Id, string Title, string Code);

/// <summary>
/// IANA identifiers with short display codes. .NET 6+ resolves IANA ids on
/// Windows through ICU, so the same settings file works on both platforms.
/// </summary>
public static class TimeZoneCatalog
{
    private static readonly (string Id, string Code)[] Known =
    {
        ("UTC", "UTC"),
        ("Europe/Kaliningrad", "КЛГ"),
        ("Europe/Moscow", "МСК"),
        ("Europe/Minsk", "МСК"),
        ("Europe/Kyiv", "КИВ"),
        ("Europe/Samara", "СМР"),
        ("Asia/Yekaterinburg", "ЕКБ"),
        ("Asia/Omsk", "ОМС"),
        ("Asia/Novosibirsk", "НСК"),
        ("Asia/Krasnoyarsk", "КРСК"),
        ("Asia/Irkutsk", "ИРК"),
        ("Asia/Yakutsk", "ЯКТ"),
        ("Asia/Vladivostok", "ВЛД"),
        ("Asia/Magadan", "МГД"),
        ("Asia/Kamchatka", "ПКТ"),
        ("Europe/London", "LON"),
        ("Europe/Berlin", "BER"),
        ("Europe/Paris", "PAR"),
        ("America/New_York", "NYC"),
        ("America/Los_Angeles", "LAX"),
        ("Asia/Shanghai", "SHA"),
        ("Asia/Tokyo", "TYO"),
        ("Asia/Seoul", "SEL"),
        ("Asia/Dubai", "DXB"),
        ("Asia/Kolkata", "DEL"),
        ("Australia/Sydney", "SYD")
    };

    public static IReadOnlyList<TimeZoneOption> All()
    {
        var list = new List<TimeZoneOption>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, code) in Known)
        {
            if (seen.Add(id))
                list.Add(new TimeZoneOption(id, Title(id), code));
        }
        try
        {
            foreach (var tz in TimeZoneInfo.GetSystemTimeZones())
            {
                if (!seen.Add(tz.Id)) continue;
                list.Add(new TimeZoneOption(tz.Id, tz.DisplayName, ShortCode(tz.Id)));
            }
        }
        catch (Exception) { }
        return list;
    }

    public static TimeZoneInfo? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception)
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var win) ? win : id); }
            catch (Exception) { return null; }
        }
    }

    public static string ShortCode(string? id, DateTimeOffset? at = null)
    {
        if (string.IsNullOrWhiteSpace(id))
            return LocalCode(at ?? DateTimeOffset.Now);
        var known = Known.FirstOrDefault(k => string.Equals(k.Id, id, StringComparison.OrdinalIgnoreCase));
        if (known.Code is not null) return known.Code;
        var tz = Find(id);
        return tz is null ? id : LocalCode(DateTimeOffset.UtcNow, tz);
    }

    private static string LocalCode(DateTimeOffset at, TimeZoneInfo? tz = null)
    {
        tz ??= TimeZoneInfo.Local;
        var offset = tz.GetUtcOffset(at);
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var abs = offset.Duration();
        return $"UTC{sign}{abs.Hours:00}:{abs.Minutes:00}";
    }

    private static string Title(string id)
    {
        var known = Known.FirstOrDefault(k => k.Id == id);
        var tz = Find(id);
        var name = tz?.DisplayName ?? id;
        return known.Code is null ? name : $"{name} ({known.Code})";
    }

    public static string FormatOffset(TimeZoneInfo tz, DateTimeOffset at)
    {
        var offset = tz.GetUtcOffset(at);
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        return $"UTC{sign}{offset.Duration():hh\\:mm}";
    }

    public static string CurrentCode(string? id) =>
        ShortCode(string.IsNullOrWhiteSpace(id) ? null : id, DateTimeOffset.Now);
}
