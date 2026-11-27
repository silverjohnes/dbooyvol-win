namespace DBooyvol.Core.Models;

/// <summary>One JDBC column description. Values always travel as strings.</summary>
public sealed class ColumnInfo
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string? ClassName { get; set; }
    public int JdbcType { get; set; }
    public int Precision { get; set; }
    public int Scale { get; set; }
    public bool Nullable { get; set; }
    public string? Table { get; set; }
    public string? Schema { get; set; }
    public string? Catalog { get; set; }

    public string QualifiedTable
    {
        get
        {
            var parts = new[] { Catalog, Schema, Table }
                .Where(p => !string.IsNullOrWhiteSpace(p));
            return string.Join(".", parts);
        }
    }
}

/// <summary>
/// A page of query results. Rows are <c>string?[]</c>: a null cell is SQL NULL,
/// an empty string is an empty value. Numbers and timestamps are never parsed.
/// </summary>
public sealed class QueryResult
{
    public string QueryId { get; set; } = "";
    public string QueryName { get; set; } = "";
    public List<ColumnInfo> Columns { get; set; } = new();
    public List<string?[]> Rows { get; set; } = new();
    public List<Guid> RowIds { get; set; } = new();
    public bool Done { get; set; }
    public bool Truncated { get; set; }
    public long DurationMs { get; set; }
    public int? UpdateCount { get; set; }
    public long? TotalCount { get; set; }
    public string? Error { get; set; }
    public string? SqlState { get; set; }
    public int? ErrorCode { get; set; }
    public List<int> VisualColumnOrder { get; set; } = new();
    public List<string> TableNames { get; set; } = new();

    public bool HasRows => Columns.Count > 0;
    public bool IsEditable => Columns.Count > 0 && TableNames.Count == 1;

    public void EnsureRowIds()
    {
        while (RowIds.Count < Rows.Count) RowIds.Add(Guid.NewGuid());
        if (RowIds.Count > Rows.Count) RowIds.RemoveRange(Rows.Count, RowIds.Count - Rows.Count);
        if (VisualColumnOrder.Count != Columns.Count)
            VisualColumnOrder = Enumerable.Range(0, Columns.Count).ToList();
    }

    public string HeaderTitle(int columnIndex, bool showType)
    {
        var column = Columns[columnIndex];
        return showType && !string.IsNullOrWhiteSpace(column.Type)
            ? $"{column.Name}  {column.Type}"
            : column.Name;
    }
}

/// <summary>One row of the vertical single-row view.</summary>
public sealed record SingleRowItem(int ColumnIndex, string Title, string? Value);
