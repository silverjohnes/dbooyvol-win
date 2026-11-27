using System.Windows;
using DBooyvol.Core.Localization;
using DBooyvol.Core.Shortcuts;
using DBooyvol.Core.Storage;
using DBooyvol.Core.Text;

namespace DBooyvol.App.Views;

public sealed record LanguageOption(string Code, string Name);

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly ShortcutRegistry? _shortcuts;

    public SettingsWindow(AppSettings settings, ShortcutRegistry? shortcuts = null)
    {
        InitializeComponent();
        _settings = settings;
        _shortcuts = shortcuts;
        DataContext = settings;

        var languages = new List<LanguageOption> { new("", "Системный язык") };
        languages.AddRange(L10n.Languages.Select(l => new LanguageOption(l.Code, l.Name)));
        LanguageBox.ItemsSource = languages;
        LanguageBox.SelectedValue = settings.AppLanguage;

        var zones = new List<TimeZoneOption> { new("", "Локальный часовой пояс", TimeZoneCatalog.CurrentCode("")) };
        zones.AddRange(TimeZoneCatalog.All());
        TimeZoneBox.ItemsSource = zones;
        TimeZoneBox.SelectedValue = settings.DisplayTimeZone;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        _settings.AppLanguage = LanguageBox.SelectedValue as string ?? "";
        _settings.DisplayTimeZone = TimeZoneBox.SelectedValue as string ?? "";
        DialogResult = true;
    }

    private void Shortcuts_Click(object sender, RoutedEventArgs e)
    {
        if (_shortcuts is null) return;
        var dialog = new ShortcutsWindow(_shortcuts) { Owner = this };
        dialog.ShowDialog();
    }
}
