using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using DBooyvol.App.Services;
using DBooyvol.App.Views;
using DBooyvol.Core.Data;
using DBooyvol.Core.Db;
using DBooyvol.Core.Localization;
using DBooyvol.Core.Models;
using DBooyvol.Core.Protocol;
using DBooyvol.Core.Shortcuts;
using DBooyvol.Core.Sql;
using DBooyvol.Core.Storage;
using DBooyvol.Core.Text;
using Microsoft.Win32;

namespace DBooyvol.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly HelloGate _hello;
    private QueryTabViewModel? _selectedTab;
    private ConnectionNode? _selectedConnection;
    private string _statusText = "Готово";
    private string _connectionStatus = "Нет подключения";
    private bool _isBusy;
    private int _tabCounter = 1;
    private readonly TabDisplayPreferences _display = new();

    public MainViewModel(AppSettings settings, ShortcutRegistry shortcuts,
        SecretStore secrets, SidecarClient sidecar, HelloGate hello)
    {
        Settings = settings;
        Shortcuts = shortcuts;
        Secrets = secrets;
        Sidecar = sidecar;
        _hello = hello;
        _display.ShowTypesInHeader = settings.ShowTypeInColumnHeader;

        NewTabCommand = new RelayCommand(_ => NewTab());
        OpenCommand = new RelayCommand(_ => OpenFile());
        SaveCommand = new RelayCommand(_ => SaveFile());
        SaveAsCommand = new RelayCommand(_ => SaveFileAs());
        CloseTabCommand = new RelayCommand(p => CloseTab(p as QueryTabViewModel ?? SelectedTab));
        CloseOtherTabsCommand = new RelayCommand(_ => CloseOtherTabs());
        RunCommand = new AsyncRelayCommand(_ => ExecuteSelectedAsync(false), _ => SelectedTab is not null);
        RunScriptCommand = new AsyncRelayCommand(_ => ExecuteSelectedAsync(true), _ => SelectedTab is not null);
        CancelCommand = new AsyncRelayCommand(_ => CancelAsync(), _ => SelectedTab is { IsRunning: true });
        FormatCommand = new RelayCommand(_ => FormatSelected());
        CommentCommand = new RelayCommand(_ => ToggleCommentSelected());
        FetchMoreCommand = new AsyncRelayCommand(_ => FetchMoreAsync(false), _ => SelectedTab is { IsRunning: false });
        FetchAllCommand = new AsyncRelayCommand(_ => FetchMoreAsync(true), _ => SelectedTab is { IsRunning: false });
        ApplyChangesCommand = new AsyncRelayCommand(_ => ApplyChangesAsync(), _ => SelectedTab is { IsDirty: true });
        ToggleSingleRowCommand = new RelayCommand(_ => ToggleSingleRow());
        ExportCsvCommand = new RelayCommand(_ => Export(ExportKind.Csv));
        ExportXmlCommand = new RelayCommand(_ => Export(ExportKind.Xml));
        ExportXlsxCommand = new RelayCommand(_ => Export(ExportKind.Xlsx));
        ExportMenuCommand = new RelayCommand(_ => ExportMenu());
        CopyCommand = new RelayCommand(p => Copy(p as string ?? "txt"));
        ConnectCommand = new AsyncRelayCommand(_ => ConnectSelectedAsync());
        DisconnectCommand = new AsyncRelayCommand(_ => DisconnectSelectedAsync());
        NewConnectionCommand = new RelayCommand(_ => NewConnection());
        EditConnectionCommand = new RelayCommand(_ => EditConnection());
        DeleteConnectionCommand = new RelayCommand(_ => DeleteConnection());
        RefreshTreeCommand = new AsyncRelayCommand(_ => RefreshTreeAsync());
        SettingsCommand = new RelayCommand(_ => ShowSettings());
        ShortcutsCommand = new RelayCommand(_ => ShowShortcuts());
        HistoryCommand = new RelayCommand(_ => ShowHistory());
        IncreaseFontCommand = new RelayCommand(_ => ChangeFont(1));
        DecreaseFontCommand = new RelayCommand(_ => ChangeFont(-1));
        ResetFontCommand = new RelayCommand(_ => ResetFont());

        ReloadProfiles();
    }

    public AppSettings Settings { get; }
    public ShortcutRegistry Shortcuts { get; }
    public SecretStore Secrets { get; }
    public SidecarClient Sidecar { get; }

    public ObservableCollection<ConnectionNode> Connections { get; } = new();
    public ObservableCollection<QueryTabViewModel> Tabs { get; } = new();

    public QueryTabViewModel? SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (!SetProperty(ref _selectedTab, value)) return;
            RefreshCommandStates();
        }
    }

    public ConnectionNode? SelectedConnection
    {
        get => _selectedConnection;
        set => SetProperty(ref _selectedConnection, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public string ConnectionStatus
    {
        get => _connectionStatus;
        set => SetProperty(ref _connectionStatus, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set => SetProperty(ref _isBusy, value);
    }

    public double EditorFontSize
    {
        get => Settings.EditorFontSize;
        set
        {
            Settings.EditorFontSize = value;
            OnPropertyChanged();
        }
    }

    public double ResultFontSize
    {
        get => Settings.ResultFontSize;
        set
        {
            Settings.ResultFontSize = value;
            OnPropertyChanged();
        }
    }

    public string TimeZoneCode => TimeZoneCatalog.CurrentCode(Settings.DisplayTimeZone);

    public RelayCommand NewTabCommand { get; }
    public RelayCommand OpenCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand SaveAsCommand { get; }
    public RelayCommand CloseTabCommand { get; }
    public RelayCommand CloseOtherTabsCommand { get; }
    public AsyncRelayCommand RunCommand { get; }
    public AsyncRelayCommand RunScriptCommand { get; }
    public AsyncRelayCommand CancelCommand { get; }
    public RelayCommand FormatCommand { get; }
    public RelayCommand CommentCommand { get; }
    public AsyncRelayCommand FetchMoreCommand { get; }
    public AsyncRelayCommand FetchAllCommand { get; }
    public AsyncRelayCommand ApplyChangesCommand { get; }
    public RelayCommand ToggleSingleRowCommand { get; }
    public RelayCommand ExportCsvCommand { get; }
    public RelayCommand ExportXmlCommand { get; }
    public RelayCommand ExportXlsxCommand { get; }
    public RelayCommand ExportMenuCommand { get; }
    public RelayCommand CopyCommand { get; }
    public AsyncRelayCommand ConnectCommand { get; }
    public AsyncRelayCommand DisconnectCommand { get; }
    public RelayCommand NewConnectionCommand { get; }
    public RelayCommand EditConnectionCommand { get; }
    public RelayCommand DeleteConnectionCommand { get; }
    public AsyncRelayCommand RefreshTreeCommand { get; }
    public RelayCommand SettingsCommand { get; }
    public RelayCommand ShortcutsCommand { get; }
    public RelayCommand HistoryCommand { get; }
    public RelayCommand IncreaseFontCommand { get; }
    public RelayCommand DecreaseFontCommand { get; }
    public RelayCommand ResetFontCommand { get; }

    // ------------------------------------------------------------------
    // Tabs
    // ------------------------------------------------------------------

    public QueryTabViewModel NewTab(string? text = null, ConnectionNode? connection = null)
    {
        var tab = new QueryTabViewModel($"Запрос {_tabCounter++}")
        {
            App = _display,
            TimeZone = TimeZoneCatalog.Find(Settings.DisplayTimeZone),
            Connection = connection ?? SelectedConnection?.Root
        };
        if (text is not null) tab.SetTextSilently(text);
        tab.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(QueryTabViewModel.IsDirty) or nameof(QueryTabViewModel.IsRunning))
                RefreshCommandStates();
        };
        Tabs.Add(tab);
        SelectedTab = tab;
        RefreshCommandStates();
        return tab;
    }

    public void CloseTab(QueryTabViewModel? tab)
    {
        if (tab is null) return;
        if (tab.IsDirty)
        {
            var answer = MessageBox.Show(
                $"Закрыть «{tab.Title}» без сохранения?",
                "DBooyvol", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
        }
        var index = Tabs.IndexOf(tab);
        Tabs.Remove(tab);
        if (SelectedTab == tab)
            SelectedTab = Tabs.Count == 0 ? null : Tabs[Math.Clamp(index, 0, Tabs.Count - 1)];
        RefreshCommandStates();
    }

    private void CloseOtherTabs()
    {
        if (SelectedTab is null) return;
        foreach (var tab in Tabs.Where(t => t != SelectedTab).ToList()) CloseTab(tab);
    }

    public void ToggleSingleRow()
    {
        if (SelectedTab is null) return;
        SelectedTab.IsSingleRow = !SelectedTab.IsSingleRow;
    }

    public void FormatSelected()
    {
        var tab = SelectedTab;
        if (tab is null) return;
        var options = new SqlFormatterOptions
        {
            IndentSize = Settings.FormatterIndent,
            UppercaseKeywords = Settings.FormatterUppercaseKeywords,
            CommaFirst = Settings.FormatterCommaFirst,
            MaxWidth = Settings.FormatterMaxWidth
        };
        if (tab.SelectionLength > 0 && tab.SelectionStart >= 0 &&
            tab.SelectionStart + tab.SelectionLength <= tab.Text.Length)
        {
            var selected = tab.Text.Substring(tab.SelectionStart, tab.SelectionLength);
            var formatted = SqlFormatter.Format(selected, options);
            tab.Text = tab.Text[..tab.SelectionStart] + formatted +
                       tab.Text[(tab.SelectionStart + tab.SelectionLength)..];
            tab.SelectionLength = formatted.Length;
        }
        else
        {
            var formatted = SqlFormatter.Format(tab.Text, options);
            var caret = tab.CaretOffset;
            tab.Text = formatted;
            tab.CaretOffset = Math.Min(caret, formatted.Length);
        }
        StatusText = "SQL отформатирован";
    }

    public void ToggleCommentSelected()
    {
        var tab = SelectedTab;
        if (tab is null) return;
        var (text, start, length) = SQLText.ToggleComment(tab.Text, tab.SelectionStart, tab.SelectionLength);
        tab.Text = text;
        tab.SelectionStart = start;
        tab.SelectionLength = length;
    }

    private void ToggleMarkedComment()
    {
        var tab = SelectedTab;
        if (tab is null) return;
        var (text, start, length) = SQLText.ToggleMarkedComment(tab.Text,
            Settings.CommentMarkerSuffix, tab.SelectionStart, tab.SelectionLength);
        tab.Text = text;
        tab.SelectionStart = start;
        tab.SelectionLength = length;
    }

    public void SelectAdjacentStatement(int direction)
    {
        var tab = SelectedTab;
        if (tab is null) return;
        var statements = SQLText.Statements(tab.Text);
        if (statements.Count == 0) return;
        var index = statements.FindIndex(s =>
            tab.CaretOffset >= s.Start && tab.CaretOffset <= s.Start + s.Length);
        if (index < 0) index = 0;
        index = Math.Clamp(index + direction, 0, statements.Count - 1);
        tab.SelectionStart = statements[index].Start;
        tab.SelectionLength = statements[index].Length;
        tab.CaretOffset = statements[index].Start + statements[index].Length;
    }

    // ------------------------------------------------------------------
    // Execution
    // ------------------------------------------------------------------

    public async Task ExecuteSelectedAsync(bool wholeScript)
    {
        var tab = SelectedTab;
        if (tab is null || tab.IsRunning) return;

        string sql;
        if (wholeScript)
        {
            sql = tab.Text;
        }
        else if (tab.SelectionLength > 0 && tab.SelectionStart >= 0 &&
                 tab.SelectionStart + tab.SelectionLength <= tab.Text.Length)
        {
            sql = tab.Text.Substring(tab.SelectionStart, tab.SelectionLength);
        }
        else
        {
            sql = SQLText.StatementAt(tab.Text, tab.CaretOffset) ?? "";
        }

        if (string.IsNullOrWhiteSpace(sql))
        {
            StatusText = "Нет SQL для выполнения";
            return;
        }
        await ExecuteTextAsync(tab, sql).ConfigureAwait(true);
    }

    public async Task ExecuteTextAsync(QueryTabViewModel tab, string sql)
    {
        if (tab.IsRunning) return;
        var connection = (tab.Connection ?? SelectedConnection)?.Root;
        if (connection is null)
        {
            MessageBox.Show("Сначала подключитесь к базе данных.", "DBooyvol",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!await connection.EnsureConnectedAsync().ConfigureAwait(true)) return;
        tab.Connection = connection;
        var session = connection.Session;
        if (session is null) return;

        var statements = SQLText.Statements(sql);
        if (statements.Count == 0) return;

        tab.IsRunning = true;
        tab.Error = null;
        IsBusy = true;
        RefreshCommandStates();
        StatusText = "Выполняется…";
        try
        {
            QueryResult? last = null;
            foreach (var statement in statements)
            {
                var text = statement.Text.Trim();
                if (text.Length == 0) continue;
                var parameters = await ResolveParametersAsync(text).ConfigureAwait(true);
                if (parameters is null) { StatusText = "Выполнение отменено"; return; }

                last = await session.QueryAsync(text, Settings.PageSize, parameters,
                    tab.Title).ConfigureAwait(true);
                AddHistory(tab, text, last);
                if (last.Error is not null) break;
            }

            if (last is null) return;
            tab.SetResult(last);
            StatusText = last.Error is null ? tab.Status : last.Error;

            if (last.Error is null && last.Columns.Count > 0 && !last.Done)
            {
                var queryText = statements[^1].Text.Trim();
                _ = CountInBackgroundAsync(tab, session, queryText);
            }
        }
        catch (SidecarException error)
        {
            var message = Describe(error);
            tab.Error = message;
            StatusText = message;
        }
        catch (Exception error)
        {
            tab.Error = error.Message;
            StatusText = error.Message;
            Log.Error("query", error);
        }
        finally
        {
            tab.IsRunning = false;
            IsBusy = false;
            RefreshCommandStates();
        }
    }

    private Task<Dictionary<string, string?>?> ResolveParametersAsync(string sql)
    {
        var names = SQLText.Parameters(sql);
        if (names.Count == 0)
            return Task.FromResult<Dictionary<string, string?>?>(new Dictionary<string, string?>());
        var dialog = new ParameterDialog(names) { Owner = Application.Current.MainWindow };
        return Task.FromResult<Dictionary<string, string?>?>(
            dialog.ShowDialog() == true ? dialog.Values : null);
    }

    private async Task CountInBackgroundAsync(QueryTabViewModel tab, Session session, string sql)
    {
        try
        {
            var count = await session.CountAsync(sql).ConfigureAwait(true);
            tab.TotalCount = count;
        }
        catch (Exception)
        {
            // A failed count is not an error of the query itself.
        }
    }

    public async Task CancelAsync()
    {
        var tab = SelectedTab;
        if (tab?.RawResult is null || tab.Connection?.Session is null) return;
        await tab.Connection.Session.CancelAsync(tab.RawResult.QueryId).ConfigureAwait(true);
        StatusText = "Запрос отменён";
    }

    public async Task FetchMoreAsync(bool all)
    {
        var tab = SelectedTab;
        if (tab?.RawResult is null || tab.Connection?.Session is null) return;
        if (tab.RawResult.Done)
        {
            StatusText = "Все строки уже загружены";
            return;
        }
        tab.IsRunning = true;
        RefreshCommandStates();
        try
        {
            var session = tab.Connection.Session;
            var page = Math.Max(1, Settings.PageSize);
            var guard = 0;
            do
            {
                var before = tab.RawResult.Rows.Count;
                var fetched = await session.FetchAsync(tab.RawResult, page).ConfigureAwait(true);
                tab.AppendResult(new QueryResult
                {
                    QueryId = tab.RawResult.QueryId,
                    Columns = tab.RawResult.Columns,
                    Rows = tab.RawResult.Rows.Skip(before).ToList(),
                    Done = tab.RawResult.Done,
                    Truncated = tab.RawResult.Truncated
                }, append: true);
                if (!fetched) break;
            } while (all && !tab.RawResult.Done && ++guard < 10_000);

            StatusText = tab.Status;
        }
        catch (Exception error)
        {
            ReportError("Не удалось загрузить строки", error);
        }
        finally
        {
            tab.IsRunning = false;
            RefreshCommandStates();
        }
    }

    public async Task ApplyChangesAsync()
    {
        var tab = SelectedTab;
        if (tab?.Connection?.Session is null) return;
        try
        {
            var (updated, inserted) = await tab.ApplyChangesAsync(tab.Connection.Session).ConfigureAwait(true);
            StatusText = $"Сохранено: изменено {updated}, добавлено {inserted}";
        }
        catch (Exception error)
        {
            ReportError("Не удалось сохранить изменения", error);
        }
    }

    // ------------------------------------------------------------------
    // Copy / export
    // ------------------------------------------------------------------

    public void Copy(string format)
    {
        var tab = SelectedTab;
        if (tab is null || !tab.HasResult)
        {
            StatusText = "Нет результата для копирования";
            return;
        }
        var selectedOnly = tab.SelectedRowIndexes.Count > 0;
        var payload = tab.CopyPayload(selectedOnly);
        var settings = new GridCopySettings
        {
            CopyHeader = Settings.ShowTypeInColumnHeader,
            ValueFormat = GridValueFormat.Display
        };
        string text;
        if (format is "txt" or "text" or "display")
        {
            text = GridClipboard.Text(payload, settings);
            Clipboard.SetText(text);
        }
        else
        {
            var parsed = format switch
            {
                "csv" => GridClipboardFormat.Csv,
                "html" => GridClipboardFormat.Html,
                "json" => GridClipboardFormat.Json,
                "markdown" => GridClipboardFormat.Markdown,
                "sql" => GridClipboardFormat.Sql,
                "source" => GridClipboardFormat.Source,
                "xml" => GridClipboardFormat.Xml,
                "dbunit" => GridClipboardFormat.DbUnit,
                _ => GridClipboardFormat.Txt
            };
            var table = tab.Connection?.Profile?.Database ?? "table";
            text = GridClipboard.Formatted(parsed, payload, table);
            if (parsed == GridClipboardFormat.Html)
            {
                var html = GridClipboard.HtmlDocument(payload, table);
                var data = new DataObject();
                data.SetData(DataFormats.UnicodeText, text);
                data.SetData(DataFormats.Html, html);
                Clipboard.SetDataObject(data);
                StatusText = "Скопировано как HTML";
                return;
            }
            Clipboard.SetText(text);
        }
        StatusText = $"Скопировано строк: {payload.Rows.Count}";
    }

    private enum ExportKind { Csv, Xml, Xlsx }

    private void ExportMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu();
        foreach (var (title, kind) in new (string, ExportKind)[]
                 {
                     ("CSV…", ExportKind.Csv), ("XML…", ExportKind.Xml), ("XLSX…", ExportKind.Xlsx)
                 })
        {
            var item = new System.Windows.Controls.MenuItem { Header = title };
            item.Click += (_, _) => Export(kind);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void Export(ExportKind kind)
    {
        var tab = SelectedTab;
        if (tab is null || !tab.HasResult)
        {
            StatusText = "Нет результата для экспорта";
            return;
        }
        var extension = kind switch
        {
            ExportKind.Csv => "csv",
            ExportKind.Xml => "xml",
            _ => "xlsx"
        };
        var dialog = new SaveFileDialog
        {
            Filter = kind switch
            {
                ExportKind.Csv => "CSV (*.csv)|*.csv",
                ExportKind.Xml => "XML (*.xml)|*.xml",
                _ => "Excel (*.xlsx)|*.xlsx"
            },
            FileName = Exporter.SuggestedFileName(tab.Title, extension)
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var columns = tab.Columns.Select(c => c.Info).ToList();
            var rows = tab.CurrentRows();
            if (kind == ExportKind.Csv)
                File.WriteAllText(dialog.FileName, Exporter.Csv(columns, rows), new UTF8Encoding(true));
            else if (kind == ExportKind.Xml)
                File.WriteAllText(dialog.FileName, Exporter.Xml(columns, rows), new UTF8Encoding(false));
            else
                File.WriteAllBytes(dialog.FileName, Exporter.Xlsx(columns, rows, tab.Title));
            StatusText = $"Экспортировано: {dialog.FileName}";
        }
        catch (Exception error)
        {
            ReportError("Не удалось экспортировать", error);
        }
    }

    // ------------------------------------------------------------------
    // Files
    // ------------------------------------------------------------------

    private void OpenFile()
    {
        var dialog = new OpenFileDialog { Filter = "SQL (*.sql)|*.sql|Все файлы (*.*)|*.*" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var text = File.ReadAllText(dialog.FileName);
            var tab = NewTab(text);
            tab.Title = Path.GetFileName(dialog.FileName);
            tab.FilePath = dialog.FileName;
            tab.IsDirty = false;
        }
        catch (Exception error)
        {
            ReportError("Не удалось открыть файл", error);
        }
    }

    private void SaveFile(QueryTabViewModel? tab = null)
    {
        tab ??= SelectedTab;
        if (tab is null) return;
        if (string.IsNullOrEmpty(tab.FilePath)) { SaveFileAs(tab); return; }
        try
        {
            File.WriteAllText(tab.FilePath, tab.Text, new UTF8Encoding(false));
            tab.IsDirty = false;
            tab.Title = Path.GetFileName(tab.FilePath);
            StatusText = $"Сохранено: {tab.FilePath}";
        }
        catch (Exception error)
        {
            ReportError("Не удалось сохранить файл", error);
        }
    }

    private void SaveFileAs(QueryTabViewModel? tab = null)
    {
        tab ??= SelectedTab;
        if (tab is null) return;
        var dialog = new SaveFileDialog
        {
            Filter = "SQL (*.sql)|*.sql|Все файлы (*.*)|*.*",
            FileName = tab.FilePath is null ? tab.Title + ".sql" : Path.GetFileName(tab.FilePath)
        };
        if (dialog.ShowDialog() != true) return;
        tab.FilePath = dialog.FileName;
        SaveFile(tab);
    }

    // ------------------------------------------------------------------
    // Connections
    // ------------------------------------------------------------------

    public void ReloadProfiles()
    {
        var profiles = ProfileStore.Load();
        Connections.Clear();
        foreach (var profile in profiles)
        {
            var node = new ConnectionNode(this, profile, profile.DisplayName, "connection",
                profile.Id, true, false);
            Connections.Add(node);
        }
    }

    public async Task<Session> CreateSessionAsync(Profile profile)
    {
        await Secrets.AuthenticateOnceAsync().ConfigureAwait(true);
        var password = await ResolvePasswordAsync(profile, prompt: true).ConfigureAwait(true);
        var session = await Session.ConnectAsync(Sidecar, profile, password,
            _ => GetStoredPasswordAsync(profile)).ConfigureAwait(true);
        ConnectionStatus = $"{profile.DisplayName}: {session.ServerVersion}";
        StatusText = $"Подключено: {profile.DisplayName}";
        return session;
    }

    private Task<string?> ResolvePasswordAsync(Profile profile, bool prompt)
    {
        if (profile.SavePassword)
        {
            try
            {
                var stored = Secrets.Get(profile.Id);
                if (!string.IsNullOrEmpty(stored)) return Task.FromResult<string?>(stored);
            }
            catch (SecretStoreException error)
            {
                ReportError("Хранилище паролей", error);
            }
        }
        if (!prompt) return Task.FromResult<string?>(null);

        var dialog = new PasswordDialog(profile.DisplayName) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true) return Task.FromResult<string?>(null);
        if (profile.SavePassword && !string.IsNullOrEmpty(dialog.Password))
        {
            try { Secrets.Save(profile.Id, dialog.Password); }
            catch (Exception error) { ReportError("Не удалось сохранить пароль", error); }
        }
        return Task.FromResult<string?>(dialog.Password);
    }

    private Task<string?> GetStoredPasswordAsync(Profile profile)
    {
        try
        {
            return Task.FromResult(profile.SavePassword ? Secrets.Get(profile.Id) : null);
        }
        catch (Exception)
        {
            return Task.FromResult<string?>(null);
        }
    }

    private async Task ConnectSelectedAsync()
    {
        var node = (SelectedConnection ?? Connections.FirstOrDefault())?.Root;
        if (node is null) return;
        if (await node.EnsureConnectedAsync().ConfigureAwait(true))
        {
            node.IsExpanded = true;
            await node.LoadChildrenAsync().ConfigureAwait(true);
        }
    }

    private async Task DisconnectSelectedAsync()
    {
        var node = SelectedConnection?.Root;
        if (node is null) return;
        await node.DisconnectAsync().ConfigureAwait(true);
        ConnectionStatus = "Нет подключения";
        StatusText = "Отключено";
    }

    private void NewConnection()
    {
        var profile = new Profile { Name = "Новое подключение", Kind = "postgres" };
        var dialog = new ConnectionDialog(profile, isNew: true) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true) return;
        var profiles = ProfileStore.Load();
        profiles.Add(dialog.Profile);
        ProfileStore.Save(profiles);
        ReloadProfiles();
        StatusText = $"Создано подключение: {dialog.Profile.DisplayName}";
    }

    private void EditConnection()
    {
        var node = SelectedConnection?.Root;
        if (node?.Profile is null) return;
        var dialog = new ConnectionDialog(node.Profile.Clone(), isNew: false)
        {
            Owner = Application.Current.MainWindow
        };
        if (dialog.ShowDialog() != true) return;
        var profiles = ProfileStore.Load();
        var index = profiles.FindIndex(p => p.Id == node.Profile.Id);
        if (index >= 0) profiles[index] = dialog.Profile;
        ProfileStore.Save(profiles);
        ReloadProfiles();
        StatusText = "Подключение сохранено";
    }

    private void DeleteConnection()
    {
        var node = SelectedConnection?.Root;
        if (node?.Profile is null) return;
        if (MessageBox.Show($"Удалить подключение «{node.Profile.DisplayName}»?",
                "DBooyvol", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        var profiles = ProfileStore.Load();
        profiles.RemoveAll(p => p.Id == node.Profile.Id);
        ProfileStore.Save(profiles);
        try { Secrets.Delete(node.Profile.Id); } catch (Exception) { }
        ReloadProfiles();
        StatusText = "Подключение удалено";
    }

    private async Task RefreshTreeAsync()
    {
        var node = SelectedConnection ?? Connections.FirstOrDefault();
        if (node is null) return;
        await node.RefreshAsync().ConfigureAwait(true);
    }

    public async Task OpenObjectAsync(ConnectionNode node)
    {
        if (!node.IsObject) return;
        var root = node.Root;
        if (!await root.EnsureConnectedAsync().ConfigureAwait(true)) return;
        var qualified = node.Path.Count >= 3 ? $"{node.Path[0]}.{node.Path[2]}" : node.Label;
        var sql = $"SELECT * FROM {qualified}";
        var tab = NewTab(sql, root);
        await ExecuteTextAsync(tab, sql).ConfigureAwait(true);
    }

    // ------------------------------------------------------------------
    // Dialogs
    // ------------------------------------------------------------------

    private void ShowSettings()
    {
        var dialog = new SettingsWindow(Settings, Shortcuts) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true) return;
        Settings.Save();
        L10n.Load(AppPaths.LocalesDirectory, Settings.AppLanguage);
        _display.ShowTypesInHeader = Settings.ShowTypeInColumnHeader;
        foreach (var tab in Tabs)
        {
            tab.TimeZone = TimeZoneCatalog.Find(Settings.DisplayTimeZone);
            tab.RebuildHeaders();
        }
        OnPropertyChanged(nameof(EditorFontSize));
        OnPropertyChanged(nameof(ResultFontSize));
        OnPropertyChanged(nameof(TimeZoneCode));
        StatusText = "Настройки сохранены";
    }

    private void ShowShortcuts()
    {
        var dialog = new ShortcutsWindow(Shortcuts) { Owner = Application.Current.MainWindow };
        dialog.ShowDialog();
        Settings.ShortcutOverrides = Shortcuts.ToOverrides();
        Settings.Save();
    }

    private void ShowHistory()
    {
        var entries = HistoryStore.Load(Settings.HistoryRetentionDays);
        var dialog = new HistoryWindow(entries) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.SelectedSql)) return;
        var tab = NewTab(dialog.SelectedSql);
        tab.Title = "Из истории";
        tab.IsDirty = false;
    }

    private void ChangeFont(int delta)
    {
        EditorFontSize = Math.Clamp(Settings.EditorFontSize + delta, 9, 32);
        ResultFontSize = Math.Clamp(Settings.ResultFontSize + delta, 8, 32);
    }

    private void ResetFont()
    {
        EditorFontSize = 14;
        ResultFontSize = 12;
    }

    // ------------------------------------------------------------------
    // Keyboard dispatch
    // ------------------------------------------------------------------

    public void ExecuteAction(string actionId)
    {
        switch (actionId)
        {
            case "file.newScript":
            case "tab.newQuery": NewTab(); break;
            case "file.newConnection": NewConnection(); break;
            case "file.open":
            case "file.openScript": OpenFile(); break;
            case "file.save": SaveFile(); break;
            case "file.saveAs": SaveFileAs(); break;
            case "tab.close": CloseTab(SelectedTab); break;
            case "tab.closeOthers": CloseOtherTabs(); break;
            case "tab.closeAll":
                foreach (var tab in Tabs.ToList()) CloseTab(tab);
                break;
            case "tab.duplicate":
                if (SelectedTab is { } source)
                {
                    var copy = NewTab(source.Text, source.Connection);
                    copy.Title = source.Title + " (копия)";
                }
                break;
            case "tab.quickOpen":
                ShowHistory(); break;
            case "tab.next":
                SelectTab(1); break;
            case "tab.prev":
                SelectTab(-1); break;
            case "edit.find":
                FocusFind(); break;
            case "edit.comment": ToggleCommentSelected(); break;
            case "edit.commentMarker": ToggleMarkedComment(); break;
            case "editor.clear":
                if (SelectedTab is { } clear) clear.Text = "";
                break;
            case "sql.executeStatement": _ = ExecuteSelectedAsync(false); break;
            case "sql.executeScript": _ = ExecuteSelectedAsync(true); break;
            case "sql.cancel": _ = CancelAsync(); break;
            case "sql.format": FormatSelected(); break;
            case "sql.completion": FocusCompletion(); break;
            case "sql.prevStatement": SelectAdjacentStatement(-1); break;
            case "sql.nextStatement": SelectAdjacentStatement(1); break;
            case "result.fetchMore": _ = FetchMoreAsync(false); break;
            case "result.fetchAll": _ = FetchMoreAsync(true); break;
            case "result.exportMenu": ExportMenu(); break;
            case "result.exportCsv": Export(ExportKind.Csv); break;
            case "result.exportXml": Export(ExportKind.Xml); break;
            case "result.exportXlsx": Export(ExportKind.Xlsx); break;
            case "data.apply": _ = ApplyChangesAsync(); break;
            case "grid.addRow": AddRow(); break;
            case "grid.deleteRow": DeleteRow(); break;
            case "grid.duplicateRow": DuplicateRow(); break;
            case "grid.copyAbove": CopyAdjacentCell(-1); break;
            case "grid.copyBelow": CopyAdjacentCell(1); break;
            case "grid.resetCell": ResetCell(); break;
            case "connection.connect": _ = ConnectSelectedAsync(); break;
            case "connection.disconnect": _ = DisconnectSelectedAsync(); break;
            case "connection.change": EditConnection(); break;
            case "connection.rename": EditConnection(); break;
            case "connection.delete": DeleteConnection(); break;
            case "connection.duplicate": DuplicateConnection(); break;
            case "tree.refresh": _ = RefreshTreeAsync(); break;
            case "object.open":
                if (SelectedConnection is { } node) _ = OpenObjectAsync(node);
                break;
            case "view.results": FocusResults(); break;
            case "view.history": ShowHistory(); break;
            case "font.increase": ChangeFont(1); break;
            case "font.decrease": ChangeFont(-1); break;
            case "font.reset": ResetFont(); break;
            case "view.fullscreen": ToggleFullscreen(); break;
            case "help.shortcuts": ShowShortcuts(); break;
            case "settings": ShowSettings(); break;
        }
    }

    private void SelectTab(int delta)
    {
        if (Tabs.Count == 0 || SelectedTab is null) return;
        var index = Tabs.IndexOf(SelectedTab);
        SelectedTab = Tabs[(index + delta + Tabs.Count) % Tabs.Count];
    }

    private void DuplicateConnection()
    {
        var node = SelectedConnection?.Root;
        if (node?.Profile is null) return;
        var profiles = ProfileStore.Load();
        var copy = node.Profile.Clone(newId: true);
        copy.Name = node.Profile.DisplayName + " (копия)";
        profiles.Add(copy);
        ProfileStore.Save(profiles);
        ReloadProfiles();
    }

    // Grid helpers implemented in the window (need DataGrid focus).
    public Action? AddRowRequested;
    public Action? DeleteRowRequested;
    public Action? DuplicateRowRequested;
    public Action<int>? CopyAdjacentRequested;
    public Action? ResetCellRequested;
    public Action? FocusFindRequested;
    public Action? FocusCompletionRequested;
    public Action? FocusResultsRequested;
    public Action? ToggleFullscreenRequested;

    private void AddRow() => AddRowRequested?.Invoke();
    private void DeleteRow() => DeleteRowRequested?.Invoke();
    private void DuplicateRow() => DuplicateRowRequested?.Invoke();
    private void CopyAdjacentCell(int delta) => CopyAdjacentRequested?.Invoke(delta);
    private void ResetCell() => ResetCellRequested?.Invoke();
    private void FocusFind() => FocusFindRequested?.Invoke();
    private void FocusCompletion() => FocusCompletionRequested?.Invoke();
    private void FocusResults() => FocusResultsRequested?.Invoke();
    private void ToggleFullscreen() => ToggleFullscreenRequested?.Invoke();

    // ------------------------------------------------------------------
    // Persistence
    // ------------------------------------------------------------------

    private void AddHistory(QueryTabViewModel tab, string sql, QueryResult result)
    {
        try
        {
            HistoryStore.Append(new HistoryEntry
            {
                Profile = tab.Connection?.Profile?.DisplayName ?? "",
                Sql = sql,
                DurationMs = result.DurationMs,
                RowCount = result.Rows.Count,
                Error = result.Error,
                Columns = result.Columns.Select(c => c.Name).ToList(),
                Rows = result.Rows.Take(200).ToList()
            });
        }
        catch (Exception error)
        {
            Log.Error("history", error);
        }
    }

    public void RestoreTabs()
    {
        var snapshot = TabStore.Load();
        foreach (var state in snapshot.Tabs)
        {
            var tab = NewTab(state.Text);
            tab.Id = state.Id;
            tab.Title = string.IsNullOrWhiteSpace(state.Title) ? tab.Title : state.Title;
            tab.FilePath = state.FilePath;
            tab.IsDirty = false;
            if (!string.IsNullOrEmpty(state.ProfileId))
                tab.Connection = Connections.FirstOrDefault(c => c.Profile?.Id == state.ProfileId);
            if (state.SelectionLocation > 0 || state.SelectionLength > 0)
            {
                tab.SelectionStart = state.SelectionLocation;
                tab.SelectionLength = state.SelectionLength;
            }
        }
        if (snapshot.ActiveId is not null)
            SelectedTab = Tabs.FirstOrDefault(t => t.Id == snapshot.ActiveId) ?? Tabs.FirstOrDefault();
    }

    public void SaveState()
    {
        try
        {
            var snapshot = new TabsSnapshot
            {
                Tabs = Tabs.Select(t => new TabState
                {
                    Id = t.Id,
                    Title = t.Title,
                    Text = t.Text,
                    FilePath = t.FilePath,
                    IsDirty = t.IsDirty,
                    SelectionLocation = t.SelectionStart,
                    SelectionLength = t.SelectionLength,
                    ProfileId = t.Connection?.Profile?.Id
                }).ToList(),
                ActiveId = SelectedTab?.Id
            };
            if (Settings.AutosaveTabs) TabStore.Save(snapshot);
            Settings.SelectedProfileId = SelectedConnection?.Profile?.Id;
            Settings.Save();
        }
        catch (Exception error)
        {
            Log.Error("save state", error);
        }
    }

    public void ReportError(string title, Exception error)
    {
        var message = error is SidecarException sidecar ? Describe(sidecar) : error.Message;
        StatusText = message;
        Log.Error(title, error);
        MessageBox.Show(message, title, MessageBoxButton.OK,
            error is SidecarException { IsConnectionLost: true }
                ? MessageBoxImage.Warning
                : MessageBoxImage.Error);
    }

    public static string Describe(SidecarException error)
    {
        var details = new List<string>();
        if (!string.IsNullOrEmpty(error.SqlState)) details.Add($"SQLSTATE {error.SqlState}");
        if (error.Code is not null) details.Add($"код {error.Code}");
        var suffix = details.Count > 0 ? $" ({string.Join(", ", details)})" : "";
        return error.Message + suffix;
    }

    private void RefreshCommandStates()
    {
        RunCommand.RaiseCanExecuteChanged();
        RunScriptCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        FetchMoreCommand.RaiseCanExecuteChanged();
        FetchAllCommand.RaiseCanExecuteChanged();
        ApplyChangesCommand.RaiseCanExecuteChanged();
        CloseTabCommand.RaiseCanExecuteChanged();
    }
}
