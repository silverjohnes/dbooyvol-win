using System.Collections.ObjectModel;
using System.Data;
using DBooyvol.Core.Data;
using DBooyvol.Core.Db;
using DBooyvol.Core.Models;
using DBooyvol.Core.Text;

namespace DBooyvol.App.ViewModels;

public sealed record GridColumn(string InternalName, ColumnInfo Info);

public sealed class QueryTabViewModel : ObservableObject
{
    private string _text = "";
    private string _title = "Запрос";
    private bool _isDirty;
    private bool _isRunning;
    private bool _isSingleRow;
    private string _status = "";
    private string? _error;
    private long _durationMs;
    private long? _totalCount;
    private int? _updateCount;
    private DataTable? _table;
    private DataView? _rowsView;
    private bool _suppressDirty;
    private TimeZoneInfo? _timeZone;
    private int _caretOffset;
    private int _selectionStart;
    private int _selectionLength;
    private string? _filePath;

    public QueryTabViewModel(string title)
    {
        _title = title;
    }

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string? FilePath
    {
        get => _filePath;
        set => SetProperty(ref _filePath, value);
    }

    public int CaretOffset
    {
        get => _caretOffset;
        set => SetProperty(ref _caretOffset, value);
    }

    public int SelectionStart
    {
        get => _selectionStart;
        set => SetProperty(ref _selectionStart, value);
    }

    public int SelectionLength
    {
        get => _selectionLength;
        set => SetProperty(ref _selectionLength, value);
    }
    public ConnectionNode? Connection { get; set; }
    public QueryResult? RawResult { get; private set; }
    public List<GridColumn> Columns { get; } = new();
    public ObservableCollection<SingleRowItem> SingleRowItems { get; } = new();
    public HashSet<int> SelectedRowIndexes { get; } = new();

    public string Text
    {
        get => _text;
        set
        {
            if (!SetProperty(ref _text, value)) return;
            if (!_suppressDirty) IsDirty = true;
        }
    }

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public bool IsDirty
    {
        get => _isDirty;
        set => SetProperty(ref _isDirty, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        set => SetProperty(ref _isRunning, value);
    }

    public bool IsSingleRow
    {
        get => _isSingleRow;
        set
        {
            if (!SetProperty(ref _isSingleRow, value)) return;
            OnPropertyChanged(nameof(IsGridVisible));
            OnPropertyChanged(nameof(IsSingleRowVisible));
        }
    }

    public bool IsGridVisible => !_isSingleRow;
    public bool IsSingleRowVisible => _isSingleRow;

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public string? Error
    {
        get => _error;
        set
        {
            if (!SetProperty(ref _error, value)) return;
            OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_error);

    public long DurationMs
    {
        get => _durationMs;
        set => SetProperty(ref _durationMs, value);
    }

    public long? TotalCount
    {
        get => _totalCount;
        set => SetProperty(ref _totalCount, value);
    }

    public int? UpdateCount
    {
        get => _updateCount;
        set => SetProperty(ref _updateCount, value);
    }

    public DataTable? Table
    {
        get => _table;
        private set
        {
            if (!SetProperty(ref _table, value)) return;
            OnPropertyChanged(nameof(IsEditable));
        }
    }

    public DataView? RowsView
    {
        get => _rowsView;
        private set => SetProperty(ref _rowsView, value);
    }

    public bool HasResult => _table is not null && Columns.Count > 0;
    public bool IsEditable => RawResult?.IsEditable == true && Connection?.Profile?.ReadOnly == false;

    public TimeZoneInfo? TimeZone
    {
        get => _timeZone;
        set
        {
            if (!SetProperty(ref _timeZone, value)) return;
            RebuildSingleRow();
        }
    }

    public void SetTextSilently(string text)
    {
        _suppressDirty = true;
        try { Text = text; }
        finally { _suppressDirty = false; }
        IsDirty = false;
    }

    public void SetResult(QueryResult result)
    {
        RawResult = result;
        Columns.Clear();
        SingleRowItems.Clear();
        SelectedRowIndexes.Clear();
        DurationMs = result.DurationMs;
        TotalCount = result.TotalCount;
        UpdateCount = result.UpdateCount;
        Error = result.Error;
        IsSingleRow = false;

        if (result.Columns.Count == 0)
        {
            Table = null;
            RowsView = null;
            Status = result.UpdateCount is { } count
                ? $"Затронуто строк: {count}"
                : result.Error is null ? "Запрос выполнен" : "Ошибка";
            OnPropertyChanged(nameof(HasResult));
            OnPropertyChanged(nameof(IsEditable));
            return;
        }

        var table = new DataTable();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < result.Columns.Count; i++)
        {
            var info = result.Columns[i];
            var internalName = UniqueName(info.Name, i, used);
            var column = table.Columns.Add(internalName, typeof(string));
            column.Caption = Header(info);
            Columns.Add(new GridColumn(internalName, info));
        }
        table.ColumnChanged += (_, _) =>
        {
            if (!_suppressDirty) IsDirty = true;
        };
        Table = table;
        AppendRows(result.Rows);
        RowsView = table.DefaultView;
        Status = BuildStatus();
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(IsEditable));
    }

    public void AppendResult(QueryResult result, bool append)
    {
        if (RawResult is null || !append)
        {
            SetResult(result);
            return;
        }
        RawResult.Done = result.Done;
        RawResult.Truncated = result.Truncated;
        RawResult.TotalCount = result.TotalCount ?? RawResult.TotalCount;
        AppendRows(result.Rows);
        TotalCount = RawResult.TotalCount;
        Status = BuildStatus();
    }

    private void AppendRows(IEnumerable<string?[]> rows)
    {
        if (Table is null) return;
        var table = Table;
        _suppressDirty = true;
        try
        {
            foreach (var row in rows)
            {
                var values = new object?[Columns.Count];
                for (var i = 0; i < Columns.Count; i++)
                {
                    var value = i < row.Length ? row[i] : null;
                    values[i] = value is null ? DBNull.Value : value;
                }
                table.Rows.Add(values);
            }
        }
        finally { _suppressDirty = false; }
    }

    public List<string?[]> CurrentRows()
    {
        var rows = new List<string?[]>();
        if (Table is null) return rows;
        foreach (DataRow row in Table.Rows)
        {
            if (row.RowState == DataRowState.Deleted) continue;
            var values = new string?[Columns.Count];
            for (var i = 0; i < Columns.Count; i++)
            {
                var cell = row[Columns[i].InternalName];
                values[i] = cell is DBNull ? null : cell as string;
            }
            rows.Add(values);
        }
        return rows;
    }

    public GridClipboardPayload CopyPayload(bool selectedOnly)
    {
        var payload = new GridClipboardPayload
        {
            Columns = Columns.Select(c => c.Info.Name).ToList(),
            ColumnInfo = Columns.Select(c => c.Info).ToList()
        };
        var rows = CurrentRows();
        for (var i = 0; i < rows.Count; i++)
        {
            if (selectedOnly && SelectedRowIndexes.Count > 0 && !SelectedRowIndexes.Contains(i)) continue;
            payload.Rows.Add(rows[i]);
            payload.RowNumbers.Add(i + 1);
        }
        return payload;
    }

    public void UpdateSelection(IEnumerable<int> indexes)
    {
        SelectedRowIndexes.Clear();
        foreach (var index in indexes) SelectedRowIndexes.Add(index);
        RebuildSingleRow();
    }

    public void UpdateSingleRowFrom(DataRowView? row)
    {
        SingleRowItems.Clear();
        if (row is null || Columns.Count == 0) return;
        for (var i = 0; i < Columns.Count; i++)
        {
            var value = row[Columns[i].InternalName];
            var text = value is DBNull ? null : value as string;
            SingleRowItems.Add(new SingleRowItem(i, Header(Columns[i].Info),
                DisplayValue(text, Columns[i].Info)));
        }
    }

    private void RebuildSingleRow()
    {
        if (!IsSingleRow) return;
        var index = SelectedRowIndexes.Count > 0 ? SelectedRowIndexes.Min() : 0;
        if (RowsView is not null && index < RowsView.Count)
            UpdateSingleRowFrom(RowsView[index]);
    }

    public string? DisplayValue(string? value, ColumnInfo column)
    {
        if (value is null) return null;
        return TimestampDisplay.Formatted(value, column.Type, TimeZone);
    }

    public void RebuildHeaders()
    {
        if (Table is null) return;
        for (var i = 0; i < Columns.Count && i < Table.Columns.Count; i++)
            Table.Columns[i].Caption = Header(Columns[i].Info);
        RebuildSingleRow();
    }

    public string Header(ColumnInfo column)
    {
        if (App.ShowTypesInHeader && !string.IsNullOrWhiteSpace(column.Type))
            return $"{column.Name}  {column.Type}";
        return column.Name;
    }

    /// <summary>Set by MainViewModel so header preferences reach every tab.</summary>
    public TabDisplayPreferences App { get; set; } = new();

    private string BuildStatus()
    {
        var loaded = Table?.Rows.Count ?? 0;
        var total = TotalCount is { } t ? $" из {t}" : "";
        var truncated = RawResult?.Truncated == true ? " (усечено)" : "";
        var time = DurationMs > 0 ? $" · {DurationMs} мс" : "";
        return $"Строк: {loaded}{total}{truncated}{time}";
    }

    private static string UniqueName(string name, int index, HashSet<string> used)
    {
        var baseName = string.IsNullOrWhiteSpace(name) ? $"column{index + 1}" : name;
        var candidate = baseName;
        var suffix = 2;
        while (!used.Add(candidate))
            candidate = $"{baseName}_{suffix++}";
        return candidate;
    }

    /// <summary>Writes changed/added rows back through the sidecar by primary key.</summary>
    public async Task<(int updated, int inserted)> ApplyChangesAsync(Session session,
        CancellationToken ct = default)
    {
        if (Table is null || RawResult is null) return (0, 0);
        var changes = Table.GetChanges();
        if (changes is null) return (0, 0);

        var first = Columns.Select(c => c.Info).FirstOrDefault(c =>
            !string.IsNullOrWhiteSpace(c.Table));
        if (first is null)
            throw new InvalidOperationException("Невозможно определить таблицу результата");

        var primaryKey = await session.PrimaryKeysAsync(first.Catalog ?? "", first.Schema ?? "",
            first.Table!, ct).ConfigureAwait(false);
        if (primaryKey.Count == 0)
            throw new InvalidOperationException("У таблицы нет первичного ключа — редактирование невозможно");

        var updates = new List<RowUpdate>();
        var inserts = new List<Dictionary<string, string?>>();

        foreach (DataRow row in changes.Rows)
        {
            if (row.RowState == DataRowState.Deleted) continue;
            if (row.RowState == DataRowState.Added)
            {
                var values = new Dictionary<string, string?>(StringComparer.Ordinal);
                foreach (var column in Columns)
                {
                    var cell = row[column.InternalName];
                    if (cell is DBNull) continue;
                    values[column.Info.Name] = cell as string;
                }
                if (values.Count > 0) inserts.Add(values);
                continue;
            }

            var keys = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var key in primaryKey)
            {
                var column = Columns.FirstOrDefault(c =>
                    string.Equals(c.Info.Name, key, StringComparison.OrdinalIgnoreCase));
                if (column is null)
                    throw new InvalidOperationException($"Первичный ключ {key} отсутствует в результате");
                var original = row[column.InternalName, DataRowVersion.Original];
                keys[column.Info.Name] = original is DBNull ? null : original as string;
            }

            var changed = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var column in Columns)
            {
                var original = row[column.InternalName, DataRowVersion.Original];
                var current = row[column.InternalName];
                var same = original is DBNull && current is DBNull
                           || original is not DBNull && current is not DBNull
                           && string.Equals(original as string, current as string, StringComparison.Ordinal);
                if (same) continue;
                changed[column.Info.Name] = current is DBNull ? null : current as string;
            }
            if (changed.Count > 0)
                updates.Add(new RowUpdate { Keys = keys, Values = changed });
        }

        var result = await session.UpdateRowsAsync(first.Catalog ?? "", first.Schema ?? "",
            first.Table!, updates, inserts, ct).ConfigureAwait(false);
        Table.AcceptChanges();
        IsDirty = false;
        return result;
    }
}

public sealed class TabDisplayPreferences
{
    public bool ShowTypesInHeader { get; set; }
}
