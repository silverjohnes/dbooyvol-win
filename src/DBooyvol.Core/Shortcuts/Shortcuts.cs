namespace DBooyvol.Core.Shortcuts;

[Flags]
public enum ShortcutModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,
    Win = 8
}

/// <summary>
/// A binding stored by Windows virtual-key code, not by character, so
/// Ctrl+Enter and Ctrl+/ keep working on the Russian layout.
/// </summary>
public readonly record struct KeyGestureSpec(int VirtualKey, ShortcutModifiers Modifiers)
{
    public bool IsEmpty => VirtualKey == 0;

    public string Display
    {
        get
        {
            var parts = new List<string>();
            if (Modifiers.HasFlag(ShortcutModifiers.Control)) parts.Add("Ctrl");
            if (Modifiers.HasFlag(ShortcutModifiers.Alt)) parts.Add("Alt");
            if (Modifiers.HasFlag(ShortcutModifiers.Shift)) parts.Add("Shift");
            if (Modifiers.HasFlag(ShortcutModifiers.Win)) parts.Add("Win");
            parts.Add(KeyName(VirtualKey, Modifiers.HasFlag(ShortcutModifiers.Shift)));
            return string.Join("+", parts);
        }
    }

    public string Serialize() => $"{VirtualKey},{(int)Modifiers}";

    public static KeyGestureSpec? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Split(',');
        if (parts.Length != 2) return null;
        if (!int.TryParse(parts[0], out var vk) || !int.TryParse(parts[1], out var mods)) return null;
        return new KeyGestureSpec(vk, (ShortcutModifiers)mods);
    }

    public static string KeyName(int vk, bool shift = false) => vk switch
    {
        Vk.Enter => "Enter",
        Vk.Tab => "Tab",
        Vk.Escape => "Esc",
        Vk.Space => "Space",
        Vk.Back => "Backspace",
        Vk.Delete => "Delete",
        Vk.Insert => "Insert",
        Vk.PageUp => "PageUp",
        Vk.PageDown => "PageDown",
        Vk.Home => "Home",
        Vk.End => "End",
        Vk.Left => "Left",
        Vk.Up => "Up",
        Vk.Right => "Right",
        Vk.Down => "Down",
        >= Vk.F1 and <= Vk.F24 => $"F{vk - Vk.F1 + 1}",
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        Vk.OemPlus => shift ? "=" : "+",
        Vk.OemMinus => "-",
        Vk.OemComma => ",",
        Vk.OemPeriod => ".",
        Vk.Oem2 => "/",
        Vk.Oem1 => ";",
        Vk.OemOpenBrackets => "[",
        Vk.Oem6 => "]",
        Vk.Oem5 => "\\",
        Vk.Oem3 => "`",
        Vk.Oem7 => "'",
        _ => $"VK{vk:X2}"
    };
}

public static class Vk
{
    public const int Back = 0x08;
    public const int Tab = 0x09;
    public const int Enter = 0x0D;
    public const int Escape = 0x1B;
    public const int Space = 0x20;
    public const int PageUp = 0x21;
    public const int PageDown = 0x22;
    public const int End = 0x23;
    public const int Home = 0x24;
    public const int Left = 0x25;
    public const int Up = 0x26;
    public const int Right = 0x27;
    public const int Down = 0x28;
    public const int Insert = 0x2D;
    public const int Delete = 0x2E;
    public const int F1 = 0x70;
    public const int F2 = 0x71;
    public const int F3 = 0x72;
    public const int F4 = 0x73;
    public const int F5 = 0x74;
    public const int F6 = 0x75;
    public const int F7 = 0x76;
    public const int F8 = 0x77;
    public const int F9 = 0x78;
    public const int F10 = 0x79;
    public const int F11 = 0x7A;
    public const int F12 = 0x7B;
    public const int F24 = 0x87;
    public const int Oem1 = 0xBA;          // ;
    public const int OemPlus = 0xBB;       // = / +
    public const int OemComma = 0xBC;
    public const int OemMinus = 0xBD;
    public const int OemPeriod = 0xBE;
    public const int Oem2 = 0xBF;          // /
    public const int Oem3 = 0xC0;          // `
    public const int OemOpenBrackets = 0xDB;
    public const int Oem5 = 0xDC;          // backslash
    public const int Oem6 = 0xDD;          // ]
    public const int Oem7 = 0xDE;          // '
    public static int Letter(char c) => char.ToUpperInvariant(c);
    public static int Digit(char c) => c;
}

public enum ShortcutScope
{
    Global,
    Editor,
    Grid,
    Result,
    Tree
}

public sealed record ShortcutAction(
    string Id,
    string Title,
    KeyGestureSpec Default,
    ShortcutScope Scope = ShortcutScope.Global,
    bool Fixed = false);

public sealed class ShortcutConflictException : Exception
{
    public string ActionId { get; }
    public string OtherActionId { get; }
    public ShortcutConflictException(string actionId, string otherActionId, string gesture,
        string? otherTitle = null)
        : base($"Сочетание {gesture} уже занято: {otherTitle ?? otherActionId}")
    {
        ActionId = actionId;
        OtherActionId = otherActionId;
    }
}

/// <summary>
/// Defaults follow DBeaver's Windows/Linux keymap. Deviations and the reasons
/// are documented in docs/WINDOWS-PORT-OPTIONS.md §6.
/// </summary>
public static class ShortcutCatalog
{
    public static readonly IReadOnlyList<ShortcutAction> Actions = new List<ShortcutAction>
    {
        // File / script
        A("file.newScript", "Новый скрипт", Ctrl(Vk.Oem6)),
        A("file.newConnection", "Новое подключение", CtrlShift('N')),
        A("file.open", "Открыть файл", Ctrl('O')),
        A("file.openScript", "Открыть существующий скрипт", new(Vk.F3, ShortcutModifiers.None)),
        A("file.newConsole", "Новая SQL-консоль", new(Vk.Enter, ShortcutModifiers.Control | ShortcutModifiers.Alt)),
        A("file.save", "Сохранить", Ctrl('S')),
        A("file.saveAs", "Сохранить как", CtrlShift('S')),
        A("file.reveal", "Показать в Проводнике", new(Vk.Oem1, ShortcutModifiers.Control | ShortcutModifiers.Alt)),

        // Tabs
        A("tab.close", "Закрыть вкладку", Ctrl(Vk.F4)),
        A("tab.closeOthers", "Закрыть остальные", new(Vk.F4, ShortcutModifiers.Control | ShortcutModifiers.Alt)),
        A("tab.closeAll", "Закрыть все", new(Vk.F4, ShortcutModifiers.Control | ShortcutModifiers.Shift)),
        A("tab.newQuery", "Новый запрос", Ctrl('T')),
        A("tab.duplicate", "Дублировать вкладку", new('D', ShortcutModifiers.Control | ShortcutModifiers.Shift | ShortcutModifiers.Alt)),
        A("tab.rename", "Переименовать вкладку", new(Vk.F2, ShortcutModifiers.Control)),
        A("tab.next", "Следующая вкладка", new(Vk.PageDown, ShortcutModifiers.Control)),
        A("tab.prev", "Предыдущая вкладка", new(Vk.PageUp, ShortcutModifiers.Control)),
        A("tab.previous", "Предыдущая открытая", new(Vk.F6, ShortcutModifiers.Control)),
        A("tab.quickOpen", "Найти запрос (Quick Open)", CtrlShift('R')),

        // Editor
        A("edit.find", "Найти", Ctrl('F')),
        A("edit.comment", "Комментировать", Ctrl(Vk.Oem2), ShortcutScope.Editor),
        A("edit.commentMarker", "Комментировать по ключу", new(Vk.Oem2, ShortcutModifiers.Control | ShortcutModifiers.Alt), ShortcutScope.Editor),
        A("editor.clear", "Очистить редактор", CtrlShift('K'), ShortcutScope.Editor),
        A("sql.executeStatement", "Выполнить выделение / statement", Ctrl(Vk.Enter), ShortcutScope.Editor),
        A("sql.executeScript", "Выполнить весь скрипт", new('X', ShortcutModifiers.Alt), ShortcutScope.Editor),
        A("sql.executeNewTab", "Выполнить в новой вкладке", Ctrl(Vk.Oem5), ShortcutScope.Editor),
        A("sql.cancel", "Отменить запрос", CtrlShift('C'), ShortcutScope.Editor),
        A("sql.completion", "Автодополнение", Ctrl(Vk.Space), ShortcutScope.Editor),
        A("sql.prevStatement", "Предыдущий запрос", new(Vk.Up, ShortcutModifiers.Alt), ShortcutScope.Editor),
        A("sql.nextStatement", "Следующий запрос", new(Vk.Down, ShortcutModifiers.Alt), ShortcutScope.Editor),
        A("sql.format", "Форматировать SQL", CtrlShift('F'), ShortcutScope.Editor),

        // Results
        A("result.fetchMore", "Загрузить ещё", new('N', ShortcutModifiers.Control | ShortcutModifiers.Alt), ShortcutScope.Result),
        A("result.fetchAll", "Загрузить все", new(Vk.OemPlus, ShortcutModifiers.Control | ShortcutModifiers.Shift), ShortcutScope.Result),
        A("result.exportMenu", "Экспорт…", new('E', ShortcutModifiers.Control | ShortcutModifiers.Alt), ShortcutScope.Result),
        A("result.exportCsv", "Экспорт CSV", new('1', ShortcutModifiers.Control | ShortcutModifiers.Alt), ShortcutScope.Result),
        A("result.exportXml", "Экспорт XML", new('2', ShortcutModifiers.Control | ShortcutModifiers.Alt), ShortcutScope.Result),
        A("result.exportXlsx", "Экспорт XLSX", new('3', ShortcutModifiers.Control | ShortcutModifiers.Alt), ShortcutScope.Result),
        A("data.apply", "Применить изменения данных", Ctrl('S'), ShortcutScope.Result),
        A("grid.addRow", "Добавить строку", new(Vk.Insert, ShortcutModifiers.Alt), ShortcutScope.Grid),
        A("grid.deleteRow", "Удалить строку", new(Vk.Delete, ShortcutModifiers.Alt), ShortcutScope.Grid),
        A("grid.duplicateRow", "Дублировать строку", new(Vk.Insert, ShortcutModifiers.Control | ShortcutModifiers.Alt), ShortcutScope.Grid),
        A("grid.copyAbove", "Копировать значение сверху", Ctrl('D'), ShortcutScope.Grid),
        A("grid.copyBelow", "Копировать значение снизу", new('D', ShortcutModifiers.Control | ShortcutModifiers.Alt), ShortcutScope.Grid),
        A("grid.resetCell", "Сбросить ячейку", new(Vk.Escape, ShortcutModifiers.None), ShortcutScope.Grid),

        // Connections / tree
        A("connection.change", "Изменить подключение", new('9', ShortcutModifiers.Control), ShortcutScope.Tree),
        A("connection.rename", "Переименовать подключение", new(Vk.F2, ShortcutModifiers.None), ShortcutScope.Tree),
        A("connection.duplicate", "Дублировать подключение", new('D', ShortcutModifiers.Control | ShortcutModifiers.Alt | ShortcutModifiers.Shift), ShortcutScope.Tree),
        A("connection.connect", "Подключиться", new('C', ShortcutModifiers.Alt), ShortcutScope.Tree),
        A("connection.disconnect", "Отключиться", new('D', ShortcutModifiers.Alt), ShortcutScope.Tree),
        A("connection.delete", "Удалить подключение", new(Vk.Delete, ShortcutModifiers.None), ShortcutScope.Tree),
        A("tree.refresh", "Обновить дерево", new(Vk.F5, ShortcutModifiers.None), ShortcutScope.Tree),
        A("object.open", "Открыть объект", new(Vk.F4, ShortcutModifiers.None), ShortcutScope.Tree),

        // View / app
        A("view.results", "Результат", Ctrl('6')),
        A("view.history", "История", new('2', ShortcutModifiers.Alt)),
        A("font.increase", "Увеличить шрифт", Ctrl(Vk.OemPlus)),
        A("font.decrease", "Уменьшить шрифт", Ctrl(Vk.OemMinus)),
        A("font.reset", "Шрифт по умолчанию", CtrlShift('0')),
        A("view.fullscreen", "Во весь экран", new(Vk.F11, ShortcutModifiers.None)),
        A("help.shortcuts", "Горячие клавиши", new(Vk.F1, ShortcutModifiers.None)),

        // Fixed, not reassignable: Tab toggles grid / single-row view.
        new("result.toggleRow", "Строка вертикально / таблица",
            new(Vk.Tab, ShortcutModifiers.None), ShortcutScope.Result, Fixed: true)
    };

    private static ShortcutAction A(string id, string title, KeyGestureSpec gesture,
        ShortcutScope scope = ShortcutScope.Global) => new(id, title, gesture, scope);

    private static KeyGestureSpec Ctrl(int vk) => new(vk, ShortcutModifiers.Control);
    private static KeyGestureSpec CtrlShift(int vk) => new(vk, ShortcutModifiers.Control | ShortcutModifiers.Shift);
    private static KeyGestureSpec Ctrl(char c) => Ctrl(Vk.Letter(c));
    private static KeyGestureSpec CtrlShift(char c) => CtrlShift(Vk.Letter(c));

    public static ShortcutAction? Find(string id) => Actions.FirstOrDefault(a => a.Id == id);
}

/// <summary>Defaults plus user overrides; conflicts are rejected.</summary>
public sealed class ShortcutRegistry
{
    private readonly Dictionary<string, KeyGestureSpec> _overrides = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ShortcutAction> _actions;

    public ShortcutRegistry(IReadOnlyDictionary<string, string>? overrides = null)
    {
        _actions = ShortcutCatalog.Actions.ToDictionary(a => a.Id, StringComparer.Ordinal);
        if (overrides is null) return;
        foreach (var (id, value) in overrides)
        {
            if (!_actions.ContainsKey(id)) continue;
            if (KeyGestureSpec.Parse(value) is { } spec) _overrides[id] = spec;
        }
    }

    public IReadOnlyCollection<ShortcutAction> Actions => _actions.Values;

    public KeyGestureSpec Get(string actionId)
    {
        if (_overrides.TryGetValue(actionId, out var spec)) return spec;
        return _actions.TryGetValue(actionId, out var action)
            ? action.Default
            : new KeyGestureSpec(0, ShortcutModifiers.None);
    }

    public ShortcutAction? Action(string actionId) =>
        _actions.TryGetValue(actionId, out var action) ? action : null;

    public void Set(string actionId, KeyGestureSpec spec)
    {
        if (!_actions.TryGetValue(actionId, out var action))
            throw new ArgumentException($"Unknown action {actionId}", nameof(actionId));
        if (action.Fixed) throw new InvalidOperationException($"Действие «{action.Title}» нельзя переназначить");

        foreach (var other in _actions.Values)
        {
            if (other.Id == actionId) continue;
            if (!Get(other.Id).Equals(spec)) continue;
            if (!ScopesOverlap(action.Scope, other.Scope)) continue;
            throw new ShortcutConflictException(actionId, other.Id, spec.Display, other.Title);
        }
        _overrides[actionId] = spec;
    }

    public void Reset(string actionId) => _overrides.Remove(actionId);
    public void ResetAll() => _overrides.Clear();

    public Dictionary<string, string> ToOverrides() =>
        _overrides.ToDictionary(kv => kv.Key, kv => kv.Value.Serialize(), StringComparer.Ordinal);

    public bool IsOverridden(string actionId) => _overrides.ContainsKey(actionId);

    public static bool ScopesOverlap(ShortcutScope a, ShortcutScope b)
    {
        if (a == ShortcutScope.Global || b == ShortcutScope.Global) return true;
        return a == b;
    }

    /// <summary>Actions whose gesture matches, restricted to an active scope.</summary>
    public List<ShortcutAction> Match(KeyGestureSpec spec, ShortcutScope activeScope)
    {
        return _actions.Values
            .Where(a => !a.Fixed && Get(a.Id).Equals(spec))
            .Where(a => a.Scope == ShortcutScope.Global || a.Scope == activeScope)
            .ToList();
    }
}
