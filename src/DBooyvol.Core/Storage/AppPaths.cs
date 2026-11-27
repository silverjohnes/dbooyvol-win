namespace DBooyvol.Core.Storage;

/// <summary>
/// All writable state lives in one directory. On Windows that is
/// <c>%APPDATA%\DBooyvol</c>; <c>DBOOYVOL_SUPPORT_DIR</c> overrides it for
/// isolated test runs (same variable the macOS build uses).
/// </summary>
public static class AppPaths
{
    private static string? _support;

    public static string Support
    {
        get
        {
            if (_support is not null) return _support;
            var overrideDir = Environment.GetEnvironmentVariable("DBOOYVOL_SUPPORT_DIR");
            if (!string.IsNullOrWhiteSpace(overrideDir))
            {
                _support = Path.GetFullPath(overrideDir);
            }
            else
            {
                var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                _support = Path.Combine(roaming, "DBooyvol");
            }
            Directory.CreateDirectory(_support);
            return _support;
        }
    }

    public static string Local
    {
        get
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dir = Path.Combine(local, "DBooyvol");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string Profiles => Path.Combine(Support, "profiles.json");
    public static string ProfilesBackup => Path.Combine(Support, "profiles.bak.json");
    public static string Tabs => Path.Combine(Support, "tabs.json");
    public static string ColumnWidths => Path.Combine(Support, "columnWidths.json");
    public static string History => Path.Combine(Support, "history.jsonl");
    public static string Secrets => Path.Combine(Support, "secrets.json");
    public static string MasterKey => Path.Combine(Support, "master.key.dpapi");
    public static string Settings => Path.Combine(Support, "settings.json");
    public static string Exports => Path.Combine(Support, "exports");
    public static string Logs => Path.Combine(Local, "logs");

    /// <summary>Directory of the running executable.</summary>
    public static string AppDirectory => AppContext.BaseDirectory;

    public static string SidecarJar => Path.Combine(AppDirectory, "sidecar.jar");
    public static string DriversDirectory => Path.Combine(AppDirectory, "drivers");
    public static string RuntimeJava => Path.Combine(AppDirectory, "runtime", "bin", "java.exe");
    public static string LocalesDirectory => Path.Combine(AppDirectory, "locales");

    /// <summary>Only for tests: reset the cached override.</summary>
    public static void ResetForTests(string? supportDir = null)
    {
        _support = supportDir;
        if (supportDir is not null) Directory.CreateDirectory(supportDir);
    }
}
