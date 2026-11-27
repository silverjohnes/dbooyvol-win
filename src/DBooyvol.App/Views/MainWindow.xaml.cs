using System.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DBooyvol.App.Controls;
using DBooyvol.App.ViewModels;
using DBooyvol.Core.Shortcuts;

namespace DBooyvol.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly ShortcutRegistry _shortcuts;
    private SqlEditor? _lastEditor;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        _shortcuts = viewModel.Shortcuts;
        DataContext = viewModel;

        Width = viewModel.Settings.WindowWidth;
        Height = viewModel.Settings.WindowHeight;
        if (viewModel.Settings.WindowMaximized) WindowState = WindowState.Maximized;

        viewModel.AddRowRequested = AddRow;
        viewModel.DeleteRowRequested = DeleteRow;
        viewModel.DuplicateRowRequested = DuplicateRow;
        viewModel.CopyAdjacentRequested = CopyAdjacentCell;
        viewModel.ResetCellRequested = ResetCell;
        viewModel.FocusFindRequested = () => _lastEditor?.ShowFind();
        viewModel.FocusCompletionRequested = () => _lastEditor?.ShowCompletion();
        viewModel.FocusResultsRequested = () => ResultsGrid?.Focus();
        viewModel.ToggleFullscreenRequested = ToggleFullscreen;

        ApplyGestures(TreeMenu);
        Closing += OnClosing;
    }

    /// <summary>Shows the current binding (including user overrides) in menus.</summary>
    private void ApplyGestures(ItemsControl menu)
    {
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            if (item.Tag is string id && id.Length > 0
                && _shortcuts.Action(id) is not null)
            {
                var spec = _shortcuts.Get(id);
                item.InputGestureText = spec.IsEmpty ? "" : spec.Display;
            }
            ApplyGestures(item);
        }
    }

    private void Tree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        if (FindAncestor<TreeViewItem>(source) is not { } item) return;
        item.IsSelected = true;
        item.Focus();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        try
        {
            _vm.Settings.WindowWidth = RestoreBounds.Width;
            _vm.Settings.WindowHeight = RestoreBounds.Height;
            _vm.Settings.WindowMaximized = WindowState == WindowState.Maximized;
            _vm.SaveState();
        }
        catch (Exception error)
        {
            Services.Log.Error("save layout", error);
        }
    }

    // ------------------------------------------------------------------
    // Menu, toolbar and context menus
    // ------------------------------------------------------------------

    private void Action_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string tag || tag.Length == 0) return;
        switch (tag)
        {
            case "toggleRow": _vm.ToggleSingleRow(); break;
            case "grid.null": SetCellNull(); break;
            case "grid.reset": ResetCell(); break;
            case "grid.add": AddRow(); break;
            case "grid.duplicate": DuplicateRow(); break;
            case "grid.delete": DeleteRow(); break;
            default: _vm.ExecuteAction(tag); break;
        }
    }

    private void GridAction_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string tag) return;
        switch (tag)
        {
            case "null": SetCellNull(); break;
            case "reset": ResetCell(); break;
            case "add": AddRow(); break;
            case "duplicate": DuplicateRow(); break;
            case "delete": DeleteRow(); break;
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string format) _vm.Copy(format);
    }

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is QueryTabViewModel tab) _vm.CloseTab(tab);
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    // ------------------------------------------------------------------
    // Tree
    // ------------------------------------------------------------------

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        _vm.SelectedConnection = e.NewValue as ConnectionNode;
    }

    private async void Tree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm.SelectedConnection is not { } node) return;
        if (node.IsObject)
            await _vm.OpenObjectAsync(node);
        else if (node.IsConnection || node.IsFolder)
            node.IsExpanded = true;
    }

    // ------------------------------------------------------------------
    // Editor
    // ------------------------------------------------------------------

    private void Editor_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not SqlEditor editor) return;
        _lastEditor = editor;
        editor.ExtraCandidates = () =>
        {
            var tab = editor.DataContext as QueryTabViewModel ?? _vm.SelectedTab;
            return tab?.Columns.Select(c => c.Info.Name) ?? Enumerable.Empty<string>();
        };
        editor.ValueCandidates = _ => Enumerable.Empty<string>();
    }

    private void Editor_ExecuteStatement(object sender, EventArgs e) =>
        _ = _vm.ExecuteSelectedAsync(false);

    private void Editor_ExecuteScript(object sender, EventArgs e) =>
        _ = _vm.ExecuteSelectedAsync(true);

    private void Editor_Format(object sender, EventArgs e) => _vm.FormatSelected();
    private void Editor_Comment(object sender, EventArgs e) => _vm.ToggleCommentSelected();
    private void Editor_CommentMarker(object sender, EventArgs e) => _vm.ExecuteAction("edit.commentMarker");
    private void Editor_PrevStatement(object sender, EventArgs e) => _vm.SelectAdjacentStatement(-1);
    private void Editor_NextStatement(object sender, EventArgs e) => _vm.SelectAdjacentStatement(1);

    // ------------------------------------------------------------------
    // Results grid
    // ------------------------------------------------------------------

    private void ResultsGrid_AutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        var tab = _vm.SelectedTab;
        if (tab is null) return;
        var index = tab.Columns.FindIndex(c => c.InternalName == e.PropertyName);
        if (index >= 0)
        {
            e.Column.Header = tab.Header(tab.Columns[index].Info);
            e.Column.MinWidth = 48;
            e.Column.MaxWidth = _vm.Settings.MaxAutoColumnWidth;
        }
        if (_vm.Settings.AutoColumnWidths)
            e.Column.Width = new DataGridLength(1, DataGridLengthUnitType.Auto);
    }

    private void ResultsGrid_LoadingRow(object sender, DataGridRowEventArgs e)
    {
        e.Row.Header = (e.Row.GetIndex() + 1).ToString();
    }

    private void ResultsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var tab = _vm.SelectedTab;
        if (tab?.Table is null || sender is not DataGrid grid) return;
        var indexes = new List<int>();
        foreach (var item in grid.SelectedItems)
        {
            if (item is DataRowView view)
            {
                var index = tab.Table.Rows.IndexOf(view.Row);
                if (index >= 0) indexes.Add(index);
            }
        }
        tab.UpdateSelection(indexes);
        tab.UpdateSingleRowFrom(grid.SelectedItem as DataRowView);
    }

    private void ResultsGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None)
        {
            _vm.ToggleSingleRow();
            e.Handled = true;
        }
    }

    // ------------------------------------------------------------------
    // Grid editing
    // ------------------------------------------------------------------

    private DataGrid? ResultsGrid
    {
        get
        {
            if (MainTabs.SelectedItem is null) return null;
            var container = MainTabs.ItemContainerGenerator
                .ContainerFromItem(MainTabs.SelectedItem) as DependencyObject;
            return container is null ? null : FindDescendant<DataGrid>(container);
        }
    }

    private (QueryTabViewModel Tab, DataRow Row, string Column)? CurrentCell()
    {
        var tab = _vm.SelectedTab;
        var grid = ResultsGrid;
        if (tab?.Table is null || grid is null) return null;
        var cell = grid.CurrentCell;
        if (!cell.IsValid) return null;
        var columnIndex = grid.Columns.IndexOf(cell.Column);
        if (columnIndex < 0 || columnIndex >= tab.Columns.Count) return null;
        if (cell.Item is not DataRowView view) return null;
        return (tab, view.Row, tab.Columns[columnIndex].InternalName);
    }

    private void AddRow()
    {
        var tab = _vm.SelectedTab;
        if (tab?.Table is null) return;
        var row = tab.Table.NewRow();
        tab.Table.Rows.Add(row);
        tab.IsDirty = true;
        ResultsGrid?.ScrollIntoView(row);
    }

    private void DeleteRow()
    {
        var tab = _vm.SelectedTab;
        var grid = ResultsGrid;
        if (tab?.Table is null || grid is null) return;
        var rows = grid.SelectedItems.OfType<DataRowView>()
            .Select(v => v.Row).Distinct().ToList();
        foreach (var row in rows) row.Delete();
        if (rows.Count > 0) tab.IsDirty = true;
    }

    private void DuplicateRow()
    {
        var tab = _vm.SelectedTab;
        var grid = ResultsGrid;
        if (tab?.Table is null || grid is null) return;
        var source = grid.SelectedItems.OfType<DataRowView>().FirstOrDefault();
        if (source is null) return;
        var copy = tab.Table.NewRow();
        foreach (var column in tab.Columns)
            copy[column.InternalName] = source[column.InternalName];
        tab.Table.Rows.Add(copy);
        tab.IsDirty = true;
        ResultsGrid?.ScrollIntoView(copy);
    }

    private void SetCellNull()
    {
        if (CurrentCell() is not { } cell) return;
        cell.Row[cell.Column] = DBNull.Value;
        cell.Tab.IsDirty = true;
    }

    private void ResetCell()
    {
        if (CurrentCell() is not { } cell) return;
        if (cell.Row.RowState == DataRowState.Added) return;
        cell.Row[cell.Column] = cell.Row[cell.Column, DataRowVersion.Original];
    }

    private void CopyAdjacentCell(int delta)
    {
        if (CurrentCell() is not { } cell) return;
        var view = cell.Tab.RowsView;
        if (view is null) return;
        var index = -1;
        for (var i = 0; i < view.Count; i++)
        {
            if (view[i].Row != cell.Row) continue;
            index = i;
            break;
        }
        var target = index + delta;
        if (index < 0 || target < 0 || target >= view.Count) return;
        var source = view[target][cell.Column];
        cell.Row[cell.Column] = source is DBNull ? DBNull.Value : source;
        cell.Tab.IsDirty = true;
    }

    private void ToggleFullscreen()
    {
        if (WindowStyle == WindowStyle.None)
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = WindowState.Normal;
        }
        else
        {
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
        }
    }

    // ------------------------------------------------------------------
    // Keyboard dispatch by Windows virtual key
    // ------------------------------------------------------------------

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;

        var vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return;

        var modifiers = ShortcutModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= ShortcutModifiers.Control;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= ShortcutModifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= ShortcutModifiers.Shift;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) modifiers |= ShortcutModifiers.Win;

        var spec = new KeyGestureSpec(vk, modifiers);
        var scopes = ActiveScopes();

        if (vk == Vk.Tab && modifiers == ShortcutModifiers.None && scopes.Contains(ShortcutScope.Grid))
        {
            _vm.ToggleSingleRow();
            e.Handled = true;
            return;
        }

        var matches = _shortcuts.Actions
            .Where(a => !a.Fixed && _shortcuts.Get(a.Id).Equals(spec))
            .Where(a => a.Scope == ShortcutScope.Global || scopes.Contains(a.Scope))
            .ToList();
        var action = matches.FirstOrDefault(a => a.Scope != ShortcutScope.Global)
                     ?? matches.FirstOrDefault();
        if (action is null) return;

        _vm.ExecuteAction(action.Id);
        e.Handled = true;
    }

    private static IReadOnlyList<ShortcutScope> ActiveScopes()
    {
        var focused = Keyboard.FocusedElement as DependencyObject;
        if (focused is null) return new[] { ShortcutScope.Global };
        if (FindAncestor<SqlEditor>(focused) is not null)
            return new[] { ShortcutScope.Editor };
        if (FindAncestor<DataGrid>(focused) is not null || FindAncestor<ListView>(focused) is not null)
            return new[] { ShortcutScope.Grid, ShortcutScope.Result };
        if (FindAncestor<TreeView>(focused) is not null)
            return new[] { ShortcutScope.Tree };
        return new[] { ShortcutScope.Global };
    }

    private static T? FindDescendant<T>(DependencyObject? node) where T : DependencyObject
    {
        if (node is null) return null;
        if (node is T match) return match;
        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
        {
            var found = FindDescendant<T>(VisualTreeHelper.GetChild(node, i));
            if (found is not null) return found;
        }
        return null;
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match) return match;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }
}
