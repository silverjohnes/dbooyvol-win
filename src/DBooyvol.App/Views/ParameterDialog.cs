using System.Windows;
using System.Windows.Controls;

namespace DBooyvol.App.Views;

/// <summary>Prompts for every named parameter in a statement, in order.</summary>
public sealed class ParameterDialog : Window
{
    private readonly Dictionary<string, TextBox> _boxes = new(StringComparer.Ordinal);

    public ParameterDialog(IReadOnlyList<string> names)
    {
        Title = "Параметры запроса";
        Width = 420;
        MaxHeight = 620;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;

        var root = new DockPanel { Margin = new Thickness(16) };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var ok = new Button { Content = "Выполнить", Width = 100, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) =>
        {
            Values = _boxes.ToDictionary(kv => kv.Key, kv => (string?)kv.Value.Text, StringComparer.Ordinal);
            DialogResult = true;
        };
        var cancel = new Button { Content = "Отмена", Width = 90, IsCancel = true };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < names.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock
            {
                Text = ":" + names[i],
                Margin = new Thickness(0, 0, 10, 8),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetRow(label, i);
            grid.Children.Add(label);

            var box = new TextBox { Padding = new Thickness(4, 3, 4, 3), Margin = new Thickness(0, 0, 0, 8) };
            Grid.SetRow(box, i);
            Grid.SetColumn(box, 1);
            grid.Children.Add(box);
            _boxes[names[i]] = box;
        }

        var scroll = new ScrollViewer
        {
            Content = grid,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        root.Children.Add(scroll);
        Content = root;
        Loaded += (_, _) =>
        {
            if (_boxes.Count > 0) _boxes.Values.First().Focus();
        };
    }

    public Dictionary<string, string?> Values { get; private set; } = new(StringComparer.Ordinal);
}
