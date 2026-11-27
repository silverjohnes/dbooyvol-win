using System.IO;
using System.Windows;
using System.Windows.Controls;
using DBooyvol.Core.Models;
using Microsoft.Win32;

namespace DBooyvol.App.Views;

public partial class ConnectionDialog : Window
{
    private string _lastKind = "postgres";
    private readonly Func<Profile, Task<(bool Ok, string Message)>>? _tester;

    public ConnectionDialog(Profile profile, bool isNew,
        Func<Profile, Task<(bool Ok, string Message)>>? tester = null)
    {
        InitializeComponent();
        Profile = profile;
        _tester = tester;
        TestButton.Visibility = tester is null ? Visibility.Collapsed : Visibility.Visible;
        Title = isNew ? "Новое подключение" : "Изменить подключение";

        NameBox.Text = profile.Name;
        SelectTag(KindBox, profile.Kind);
        HostBox.Text = profile.Host;
        PortBox.Text = profile.Port > 0 ? profile.Port.ToString() : profile.DefaultPort.ToString();
        DatabaseBox.Text = profile.Database;
        UserBox.Text = profile.User;
        UrlBox.Text = profile.JdbcUrl;
        DriverBox.Text = profile.DriverPath;
        SidBox.IsChecked = profile.OracleSid;

        SqliteBox.Text = profile.Kind == "sqlite" ? profile.Database : "";

        SelectTag(OracleModeBox, profile.OracleMode);
        AliasBox.Text = profile.OracleAlias;
        TnsAdminBox.Text = profile.TnsAdmin;
        LdapHostBox.Text = profile.LdapHost;
        LdapPortBox.Text = profile.LdapPort.ToString();
        LdapContextBox.Text = profile.LdapContext;
        LdapSslBox.IsChecked = profile.LdapSsl;

        SshEnabledBox.IsChecked = profile.Ssh.Enabled;
        SshHostBox.Text = profile.Ssh.Host;
        SshPortBox.Text = profile.Ssh.Port.ToString();
        SshUserBox.Text = profile.Ssh.User;
        SshKeyBox.Text = profile.Ssh.KeyPath;

        ReadOnlyBox.IsChecked = profile.ReadOnly;
        SavePasswordBox.IsChecked = profile.SavePassword;

        _lastKind = profile.Kind;
        UpdatePanels();
    }

    public Profile Profile { get; }

    private static void SelectTag(ComboBox box, string tag)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = item;
                return;
            }
        }
        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }

    private static string SelectedTag(ComboBox box) =>
        (box.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    private string CurrentKind => SelectedTag(KindBox);

    private void Kind_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        var kind = CurrentKind;
        if (string.IsNullOrEmpty(kind)) return;

        var defaults = new Dictionary<string, int>
        {
            ["postgres"] = 5432,
            ["oracle"] = 1521,
            ["clickhouse"] = 8123,
            ["sqlite"] = 0
        };
        var currentPort = PortBox.Text.Trim();
        var oldDefault = defaults.GetValueOrDefault(_lastKind).ToString();
        if (currentPort.Length == 0 || currentPort == oldDefault)
            PortBox.Text = defaults.GetValueOrDefault(kind).ToString();
        _lastKind = kind;
        UpdatePanels();
    }

    private void OracleMode_Changed(object sender, SelectionChangedEventArgs e) => UpdatePanels();

    private void Ssh_Changed(object sender, RoutedEventArgs e) => UpdatePanels();

    private void UpdatePanels()
    {
        if (NetworkPanel is null) return;
        var kind = CurrentKind;
        var isSqlite = kind == "sqlite";
        NetworkPanel.Visibility = isSqlite ? Visibility.Collapsed : Visibility.Visible;
        SqlitePanel.Visibility = isSqlite ? Visibility.Visible : Visibility.Collapsed;
        OraclePanel.Visibility = kind == "oracle" ? Visibility.Visible : Visibility.Collapsed;
        SshPanel.Visibility = isSqlite ? Visibility.Collapsed : Visibility.Visible;

        var mode = SelectedTag(OracleModeBox);
        AliasLabel.Visibility = mode is "tns" or "ldap" ? Visibility.Visible : Visibility.Collapsed;
        AliasBox.Visibility = AliasLabel.Visibility;
        AliasLabel.Text = mode == "ldap" ? "Сервис LDAP" : "TNS-алиас";

        var sshOn = SshEnabledBox.IsChecked == true;
        SshFields.IsEnabled = sshOn;
    }

    private void BrowseDriver_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "JDBC (*.jar)|*.jar|Все файлы (*.*)|*.*" };
        if (dialog.ShowDialog() == true) DriverBox.Text = dialog.FileName;
    }

    private void BrowseSqlite_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "SQLite (*.db;*.sqlite;*.sqlite3)|*.db;*.sqlite;*.sqlite3|Все файлы (*.*)|*.*",
            CheckFileExists = false
        };
        if (dialog.ShowDialog() == true) SqliteBox.Text = dialog.FileName;
    }

    private void BrowseTns_Click(object sender, RoutedEventArgs e) =>
        BrowseFolder(TnsAdminBox);

    private void BrowseKey_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Ключи (*.*)|*.*",
            InitialDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh")
        };
        if (dialog.ShowDialog() == true) SshKeyBox.Text = dialog.FileName;
    }

    private static void BrowseFolder(TextBox target)
    {
        var dialog = new OpenFolderDialog();
        if (dialog.ShowDialog() == true) target.Text = dialog.FolderName;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!Collect()) return;
        DialogResult = true;
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (_tester is null || !Collect()) return;

        TestButton.IsEnabled = false;
        var original = TestButton.Content;
        TestButton.Content = "Проверка…";
        try
        {
            var (ok, message) = await _tester(Profile.Clone()).ConfigureAwait(true);
            MessageBox.Show(this, message, "Тест подключения", MessageBoxButton.OK,
                ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "Тест подключения",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            TestButton.Content = original;
            TestButton.IsEnabled = true;
        }
    }

    /// <summary>Reads the form into <see cref="Profile"/>. Returns false on a validation error.</summary>
    private bool Collect()
    {
        var kind = CurrentKind;
        if (string.IsNullOrEmpty(kind))
        {
            MessageBox.Show(this, "Выберите тип базы данных.", "DBooyvol");
            return false;
        }
        if (kind == "sqlite" && string.IsNullOrWhiteSpace(SqliteBox.Text))
        {
            MessageBox.Show(this, "Укажите файл базы SQLite.", "DBooyvol");
            return false;
        }

        Profile.Name = NameBox.Text.Trim();
        Profile.Kind = kind;
        Profile.Host = HostBox.Text.Trim();
        Profile.Port = int.TryParse(PortBox.Text.Trim(), out var port) ? port : 0;
        Profile.Database = kind == "sqlite" ? SqliteBox.Text.Trim() : DatabaseBox.Text.Trim();
        Profile.User = UserBox.Text.Trim();
        Profile.JdbcUrl = UrlBox.Text.Trim();
        Profile.DriverPath = DriverBox.Text.Trim();
        Profile.OracleSid = SidBox.IsChecked == true;

        Profile.OracleMode = SelectedTag(OracleModeBox);
        Profile.OracleAlias = AliasBox.Text.Trim();
        Profile.TnsAdmin = TnsAdminBox.Text.Trim();
        Profile.LdapHost = LdapHostBox.Text.Trim();
        Profile.LdapPort = int.TryParse(LdapPortBox.Text.Trim(), out var ldapPort) ? ldapPort : 389;
        Profile.LdapContext = LdapContextBox.Text.Trim();
        Profile.LdapSsl = LdapSslBox.IsChecked == true;

        Profile.Ssh.Enabled = SshEnabledBox.IsChecked == true;
        Profile.Ssh.Host = SshHostBox.Text.Trim();
        Profile.Ssh.Port = int.TryParse(SshPortBox.Text.Trim(), out var sshPort) ? sshPort : 22;
        Profile.Ssh.User = SshUserBox.Text.Trim();
        Profile.Ssh.KeyPath = SshKeyBox.Text.Trim();

        Profile.ReadOnly = ReadOnlyBox.IsChecked == true;
        Profile.SavePassword = SavePasswordBox.IsChecked == true;

        if (string.IsNullOrWhiteSpace(Profile.Name))
            Profile.Name = string.IsNullOrWhiteSpace(Profile.Database) ? kind : Profile.Database;
        return true;
    }
}
