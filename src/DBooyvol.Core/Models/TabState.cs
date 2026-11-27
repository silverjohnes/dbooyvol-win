namespace DBooyvol.Core.Models;

/// <summary>An open editor tab, persisted in <c>tabs.json</c>.</summary>
public sealed class TabState
{
    public string Id { get; set; } = Guid.NewGuid().ToString().ToUpperInvariant();
    public string Title { get; set; } = "";
    public string Text { get; set; } = "";
    public string? FilePath { get; set; }
    public bool CustomTitle { get; set; }
    public bool IsDirty { get; set; }
    public int SelectionLocation { get; set; }
    public int SelectionLength { get; set; }
    public string? ProfileId { get; set; }
}

public sealed class TabsSnapshot
{
    public List<TabState> Tabs { get; set; } = new();
    public string? ActiveId { get; set; }
}

public sealed class HistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString().ToUpperInvariant();
    public DateTimeOffset Date { get; set; } = DateTimeOffset.UtcNow;
    public string Profile { get; set; } = "";
    public string Sql { get; set; } = "";
    public long DurationMs { get; set; }
    public int RowCount { get; set; }
    public string? Error { get; set; }
    public List<string> Columns { get; set; } = new();
    public List<string?[]> Rows { get; set; } = new();

    public bool Matches(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        var q = query.Trim();
        return Sql.Contains(q, StringComparison.OrdinalIgnoreCase)
            || Profile.Contains(q, StringComparison.OrdinalIgnoreCase);
    }
}
