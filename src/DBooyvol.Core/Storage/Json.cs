using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using DBooyvol.Core.Models;

namespace DBooyvol.Core.Storage;

/// <summary>Shared JSON settings: camelCase, readable Cyrillic, no nulls.</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static readonly JsonSerializerOptions Compact = new(Options)
    {
        WriteIndented = false
    };

    public static T? Read<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path);
            return string.IsNullOrWhiteSpace(text) ? null : JsonSerializer.Deserialize<T>(text, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var text = JsonSerializer.Serialize(value, Options);
        AtomicWrite(path, text);
    }

    /// <summary>
    /// Write through a sibling temp file and rename, so a crash cannot leave a
    /// half-written profiles.json behind.
    /// </summary>
    public static void AtomicWrite(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }
}

public static class ProfileStore
{
    public static List<Profile> Load()
    {
        var list = Json.Read<List<Profile>>(AppPaths.Profiles) ?? new List<Profile>();
        // Older files may omit savePassword; the macOS default is true.
        foreach (var profile in list)
        {
            profile.Ssh ??= new SshProfile();
            if (profile.Port <= 0) profile.Port = profile.DefaultPort;
        }
        return list;
    }

    public static void Save(List<Profile> profiles)
    {
        if (File.Exists(AppPaths.Profiles))
        {
            try { File.Copy(AppPaths.Profiles, AppPaths.ProfilesBackup, overwrite: true); }
            catch (IOException) { }
        }
        Json.Write(AppPaths.Profiles, profiles);
    }
}

public static class TabStore
{
    public static TabsSnapshot Load() => Json.Read<TabsSnapshot>(AppPaths.Tabs) ?? new TabsSnapshot();
    public static void Save(TabsSnapshot snapshot) => Json.Write(AppPaths.Tabs, snapshot);
}

public static class ColumnWidthStore
{
    public static Dictionary<string, Dictionary<string, double>> Load() =>
        Json.Read<Dictionary<string, Dictionary<string, double>>>(AppPaths.ColumnWidths)
        ?? new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);

    public static void Save(Dictionary<string, Dictionary<string, double>> widths) =>
        Json.Write(AppPaths.ColumnWidths, widths);
}

/// <summary>One JSON object per line, as in the macOS build.</summary>
public static class HistoryStore
{
    public static List<HistoryEntry> Load(int retentionDays, int maxEntries = 2000)
    {
        var result = new List<HistoryEntry>();
        if (!File.Exists(AppPaths.History)) return result;
        var cutoff = retentionDays <= 0 ? (DateTimeOffset?)null : DateTimeOffset.UtcNow.AddDays(-retentionDays);
        foreach (var line in File.ReadLines(AppPaths.History))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var entry = JsonSerializer.Deserialize<HistoryEntry>(line, Json.Options);
                if (entry is null) continue;
                if (cutoff is not null && entry.Date < cutoff) continue;
                result.Add(entry);
            }
            catch (JsonException) { }
        }
        return result.OrderByDescending(e => e.Date).Take(maxEntries).ToList();
    }

    public static void Append(HistoryEntry entry)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.History)!);
        var line = JsonSerializer.Serialize(entry, Json.Compact) + "\n";
        File.AppendAllText(AppPaths.History, line);
    }

    /// <summary>Rewrites the file with the given entries (retention change, clear).</summary>
    public static void Rewrite(IEnumerable<HistoryEntry> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.History)!);
        var text = string.Concat(entries.Select(e => JsonSerializer.Serialize(e, Json.Compact) + "\n"));
        Json.AtomicWrite(AppPaths.History, text);
    }
}
