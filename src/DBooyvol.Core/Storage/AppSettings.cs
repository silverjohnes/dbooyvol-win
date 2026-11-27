using DBooyvol.Core.Models;

namespace DBooyvol.Core.Storage;

/// <summary>
/// User settings. Keys and defaults match the macOS build (§4.7 of the
/// handoff); values are clamped to the documented ranges on load and save.
/// </summary>
public sealed class AppSettings
{
    public string SshDirectory { get; set; } = "";
    /// <summary>Empty = bundled jlink runtime, then PATH.</summary>
    public string JavaPath { get; set; } = "";
    public bool AutosaveTabs { get; set; } = true;
    public double EditorFontSize { get; set; } = 14;
    public string ResultFontName { get; set; } = "";
    public double ResultFontSize { get; set; } = 12;
    public bool AutoColumnWidths { get; set; } = true;
    public double MaxAutoColumnWidth { get; set; } = 280;
    public int PageSize { get; set; } = 200;
    public int HistoryRetentionDays { get; set; } = 30;
    public bool ShowTypeInColumnHeader { get; set; }
    public bool ShowTypeOnHeaderHover { get; set; } = true;
    public bool SmartSelection { get; set; }
    public bool SmartSelectionOnlyInSearch { get; set; }
    public bool CommentByMarker { get; set; } = true;
    public string CommentMarkerSuffix { get; set; } = "!#";
    public string DisplayTimeZone { get; set; } = "";
    public bool CheckExternalChanges { get; set; }
    public string? SelectedProfileId { get; set; }
    public string AppLanguage { get; set; } = "";

    public Dictionary<string, List<string>> TreeExpanded { get; set; } = new();
    public List<string> CollapsedConnections { get; set; } = new();
    /// <summary>Action id → "virtualKey,modifiers". Only deviations are stored.</summary>
    public Dictionary<string, string> ShortcutOverrides { get; set; } = new();

    // Window layout
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 820;
    public bool WindowMaximized { get; set; }
    public double SidebarWidth { get; set; } = 260;
    public double EditorHeightRatio { get; set; } = 0.52;
    public double ValuePanelWidth { get; set; } = 340;

    // SQL formatter (Ctrl+Shift+F)
    public int FormatterIndent { get; set; } = 4;
    public bool FormatterUppercaseKeywords { get; set; } = true;
    public bool FormatterCommaFirst { get; set; }
    public int FormatterMaxWidth { get; set; } = 120;

    public static AppSettings Load()
    {
        var settings = Json.Read<AppSettings>(AppPaths.Settings) ?? new AppSettings();
        settings.Normalize();
        return settings;
    }

    public void Save()
    {
        Normalize();
        Json.Write(AppPaths.Settings, this);
    }

    public void Normalize()
    {
        EditorFontSize = Math.Clamp(EditorFontSize, 9, 32);
        ResultFontSize = Math.Clamp(ResultFontSize, 8, 32);
        MaxAutoColumnWidth = Math.Clamp(MaxAutoColumnWidth, 60, 1000);
        PageSize = Math.Clamp(PageSize, 1, 100000);
        HistoryRetentionDays = Math.Max(0, HistoryRetentionDays);
        FormatterIndent = Math.Clamp(FormatterIndent, 2, 8);
        FormatterMaxWidth = Math.Clamp(FormatterMaxWidth, 60, 400);
        WindowWidth = Math.Max(980, WindowWidth);
        WindowHeight = Math.Max(620, WindowHeight);
        SidebarWidth = Math.Clamp(SidebarWidth, 190, 420);
        EditorHeightRatio = Math.Clamp(EditorHeightRatio, 0.2, 0.8);
        ValuePanelWidth = Math.Clamp(ValuePanelWidth, 240, 620);
        SshDirectory ??= "";
        ResultFontName ??= "";
        CommentMarkerSuffix ??= "!#";
        DisplayTimeZone ??= "";
        AppLanguage ??= "";
        TreeExpanded ??= new Dictionary<string, List<string>>();
        CollapsedConnections ??= new List<string>();
        ShortcutOverrides ??= new Dictionary<string, string>();
        if (HistoryRetentionDays == 0) { /* unlimited */ }
    }

    public string EffectiveSshDirectory =>
        string.IsNullOrWhiteSpace(SshDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh")
            : Expand(SshDirectory);

    public static string Expand(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        var expanded = Environment.ExpandEnvironmentVariables(path);
        if (expanded.StartsWith("~/") || expanded.StartsWith("~\\"))
            expanded = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), expanded[2..]);
        return expanded;
    }
}
