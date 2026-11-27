using System.Collections.ObjectModel;
using System.Windows;
using DBooyvol.Core.Models;
using DBooyvol.Core.Storage;

namespace DBooyvol.App.Views;

public sealed class HistoryRow
{
    public required HistoryEntry Entry { get; init; }
    public string DateText => Entry.Date.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string Profile => Entry.Profile;
    public string SqlPreview
    {
        get
        {
            var text = Entry.Sql.Replace("\r", " ").Replace("\n", " ").Trim();
            return text.Length > 180 ? text[..180] + "…" : text;
        }
    }
    public string Rows => Entry.Error is null ? Entry.RowCount.ToString() : "ошибка";
}

public partial class HistoryWindow : Window
{
    private readonly List<HistoryEntry> _entries;
    private readonly ObservableCollection<HistoryRow> _rows = new();

    public HistoryWindow(List<HistoryEntry> entries)
    {
        InitializeComponent();
        _entries = entries;
        List.ItemsSource = _rows;
        Reload();
    }

    public string? SelectedSql { get; private set; }

    private void Reload()
    {
        var query = FilterBox.Text.Trim();
        _rows.Clear();
        foreach (var entry in _entries.Where(e => e.Matches(query)).Take(1000))
            _rows.Add(new HistoryRow { Entry = entry });
    }

    private void Filter_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e) => Reload();

    private void Insert_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is not HistoryRow row) return;
        SelectedSql = row.Entry.Sql;
        DialogResult = true;
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Очистить всю историю запросов?", "DBooyvol",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        HistoryStore.Rewrite(Array.Empty<HistoryEntry>());
        _entries.Clear();
        Reload();
    }
}
