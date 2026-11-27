using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using DBooyvol.Core.Shortcuts;

namespace DBooyvol.App.Views;

public sealed class ShortcutRow
{
    public required ShortcutAction Action { get; init; }
    public required string Title { get; init; }
    public required string Gesture { get; init; }
    public required string Scope { get; init; }
    public required string Note { get; init; }
}

public partial class ShortcutsWindow : Window
{
    private readonly ShortcutRegistry _registry;
    private readonly ObservableCollection<ShortcutRow> _rows = new();
    private ShortcutRow? _capturing;

    public ShortcutsWindow(ShortcutRegistry registry)
    {
        InitializeComponent();
        _registry = registry;
        List.ItemsSource = _rows;
        PreviewKeyDown += CaptureKey;
        Reload();
    }

    private void Reload()
    {
        var filter = FilterBox.Text.Trim();
        _rows.Clear();
        foreach (var action in _registry.Actions.OrderBy(a => a.Title, StringComparer.CurrentCulture))
        {
            var gesture = _registry.Get(action.Id).Display;
            if (filter.Length > 0 &&
                !action.Title.Contains(filter, StringComparison.CurrentCultureIgnoreCase) &&
                !gesture.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;
            _rows.Add(new ShortcutRow
            {
                Action = action,
                Title = action.Title,
                Gesture = gesture,
                Scope = action.Scope switch
                {
                    ShortcutScope.Editor => "Редактор",
                    ShortcutScope.Grid => "Таблица",
                    ShortcutScope.Result => "Результат",
                    ShortcutScope.Tree => "Дерево",
                    _ => "Везде"
                },
                Note = action.Fixed
                    ? "фиксировано"
                    : _registry.IsOverridden(action.Id) ? "изменено" : ""
            });
        }
    }

    private void Filter_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e) => Reload();

    private void Change_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is not ShortcutRow row)
        {
            MessageBox.Show("Выберите действие в списке.", "DBooyvol");
            return;
        }
        if (row.Action.Fixed)
        {
            MessageBox.Show("Это сочетание фиксировано: Tab переключает таблицу и строку.",
                "DBooyvol", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _capturing = row;
        CaptureText.Text = $"Нажмите новое сочетание для «{row.Title}» (Esc — отмена)";
    }

    private void CaptureKey(object sender, KeyEventArgs e)
    {
        if (_capturing is null) return;
        e.Handled = true;

        if (e.Key == Key.Escape)
        {
            _capturing = null;
            CaptureText.Text = "";
            return;
        }
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;

        var vk = KeyInterop.VirtualKeyFromKey(e.Key);
        if (vk == 0) return;
        var modifiers = ShortcutModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= ShortcutModifiers.Control;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= ShortcutModifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= ShortcutModifiers.Shift;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) modifiers |= ShortcutModifiers.Win;

        try
        {
            _registry.Set(_capturing.Action.Id, new KeyGestureSpec(vk, modifiers));
            CaptureText.Text = "";
            _capturing = null;
            Reload();
        }
        catch (ShortcutConflictException conflict)
        {
            MessageBox.Show(conflict.Message, "Конфликт сочетаний",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "DBooyvol", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is not ShortcutRow row) return;
        _registry.Reset(row.Action.Id);
        Reload();
    }

    private void ResetAll_Click(object sender, RoutedEventArgs e)
    {
        _registry.ResetAll();
        Reload();
    }
}
