using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using ICSharpCode.AvalonEdit.Indentation;
using DBooyvol.App.Services;
using DBooyvol.Core.Sql;

namespace DBooyvol.App.Controls;

/// <summary>
/// AvalonEdit wrapper with bindable text, caret and selection. Keyboard
/// handling is deliberately local: the window-level dispatcher only sees
/// gestures that reach it, and Tab stays a normal editor key.
/// </summary>
public sealed class SqlEditor : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(SqlEditor),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnTextChanged));

    public static readonly DependencyProperty CaretOffsetProperty = DependencyProperty.Register(
        nameof(CaretOffset), typeof(int), typeof(SqlEditor),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnCaretChanged));

    public static readonly DependencyProperty SelectionStartProperty = DependencyProperty.Register(
        nameof(SelectionStart), typeof(int), typeof(SqlEditor),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnSelectionChanged));

    public static readonly DependencyProperty SelectionLengthProperty = DependencyProperty.Register(
        nameof(SelectionLength), typeof(int), typeof(SqlEditor),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnSelectionChanged));

    public static readonly DependencyProperty EditorFontSizeProperty = DependencyProperty.Register(
        nameof(EditorFontSize), typeof(double), typeof(SqlEditor),
        new FrameworkPropertyMetadata(14.0, OnFontSizeChanged));

    private readonly TextEditor _editor;
    private bool _syncing;

    public SqlEditor()
    {
        _editor = new TextEditor
        {
            ShowLineNumbers = true,
            WordWrap = false,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas, Courier New"),
            Options =
            {
                ConvertTabsToSpaces = true,
                IndentationSize = 4,
                EnableHyperlinks = false,
                ShowTabs = false
            }
        };
        _editor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("SQL");
        _editor.TextArea.IndentationStrategy = new DefaultIndentationStrategy();
        _editor.TextChanged += (_, _) =>
        {
            if (_syncing) return;
            _syncing = true;
            try { SetCurrentValue(TextProperty, _editor.Text); }
            finally { _syncing = false; }
        };
        _editor.TextArea.Caret.PositionChanged += (_, _) =>
        {
            if (_syncing) return;
            _syncing = true;
            try { SetCurrentValue(CaretOffsetProperty, _editor.CaretOffset); }
            finally { _syncing = false; }
        };
        _editor.TextArea.SelectionChanged += (_, _) =>
        {
            if (_syncing) return;
            _syncing = true;
            try
            {
                SetCurrentValue(SelectionStartProperty, _editor.SelectionStart);
                SetCurrentValue(SelectionLengthProperty, _editor.SelectionLength);
            }
            finally { _syncing = false; }
        };
        _editor.PreviewKeyDown += EditorPreviewKeyDown;
        Content = _editor;
        Loaded += (_, _) =>
        {
            ThemeService.Changed += ApplyTheme;
            ApplyTheme();
        };
        Unloaded += (_, _) => ThemeService.Changed -= ApplyTheme;
        ApplyTheme();
    }

    // ------------------------------------------------------------------
    // Theme
    // ------------------------------------------------------------------

    /// <summary>
    /// AvalonEdit does not consume DynamicResource, so the editor palette is
    /// pushed by hand and the SQL definition is rebuilt on every theme change.
    /// </summary>
    private void ApplyTheme()
    {
        _editor.Background = ThemeService.Brush("Brush.Surface");
        _editor.Foreground = ThemeService.Brush("Brush.EditorText");
        _editor.LineNumbersForeground = ThemeService.Brush("Brush.TextDisabled");
        _editor.TextArea.Caret.CaretBrush = ThemeService.Brush("Brush.Text");
        _editor.TextArea.SelectionBrush = ThemeService.Brush("Brush.EditorSelection");
        _editor.TextArea.SelectionBorder = null;
        _editor.TextArea.TextView.CurrentLineBackground = ThemeService.Brush("Brush.EditorCurrentLine");
        _editor.TextArea.TextView.CurrentLineBorder = null;
        try
        {
            _editor.SyntaxHighlighting = BuildSqlHighlighting();
        }
        catch (Exception error)
        {
            Services.Log.Error("editor highlighting", error);
            _editor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("SQL");
        }
        _editor.TextArea.TextView.Redraw();
    }

    private static IHighlightingDefinition BuildSqlHighlighting()
    {
        string Hex(string key)
        {
            var c = ThemeService.Color(key);
            return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        }

        var words = string.Join("", SqlHighlightWords.Select(w => $"<Word>{w}</Word>"));
        var xshd =
            "<SyntaxDefinition name=\"SQL\" extensions=\".sql\" " +
            "xmlns=\"http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008\">" +
            $"<Color name=\"Comment\" foreground=\"{Hex("Brush.EditorComment")}\" />" +
            $"<Color name=\"String\" foreground=\"{Hex("Brush.EditorString")}\" />" +
            $"<Color name=\"Number\" foreground=\"{Hex("Brush.EditorNumber")}\" />" +
            $"<Color name=\"Keyword\" foreground=\"{Hex("Brush.EditorKeyword")}\" fontWeight=\"bold\" />" +
            $"<Color name=\"Function\" foreground=\"{Hex("Brush.EditorFunction")}\" />" +
            "<RuleSet>" +
            "<Span color=\"Comment\" begin=\"/\\*\" end=\"\\*/\" multiline=\"true\" />" +
            "<Span color=\"String\" begin=\"'\" end=\"'\" />" +
            "<Span color=\"String\" begin=\"&quot;\" end=\"&quot;\" />" +
            "<Rule color=\"Comment\">--.*$</Rule>" +
            "<Rule color=\"Function\">\\b[A-Za-z_][A-Za-z0-9_$]*(?=\\s*\\()</Rule>" +
            $"<Keywords color=\"Keyword\" ignoreCase=\"true\">{words}</Keywords>" +
            "<Rule color=\"Number\">\\b0[xX][0-9a-fA-F]+\\b|\\b\\d+(?:\\.\\d+)?(?:[eE][+-]?\\d+)?\\b</Rule>" +
            "</RuleSet></SyntaxDefinition>";

        using var reader = System.Xml.XmlReader.Create(new System.IO.StringReader(xshd));
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }

    private static readonly string[] SqlHighlightWords =
    {
        "select", "from", "where", "and", "or", "not", "in", "exists", "between", "like", "ilike",
        "is", "null", "as", "on", "join", "left", "right", "full", "inner", "outer", "cross", "natural",
        "group", "by", "order", "having", "limit", "offset", "fetch", "next", "rows", "only",
        "union", "all", "intersect", "except", "minus", "distinct", "into", "values", "set",
        "insert", "update", "delete", "merge", "create", "alter", "drop", "truncate", "table", "view",
        "index", "sequence", "schema", "database", "grant", "revoke", "commit", "rollback", "begin",
        "start", "transaction", "with", "recursive", "case", "when", "then", "else", "end", "cast",
        "asc", "desc", "nulls", "first", "last", "over", "partition", "window", "row", "range",
        "using", "lateral", "returning", "conflict", "do", "nothing", "default", "primary", "key",
        "foreign", "references", "constraint", "unique", "check", "cascade", "restrict",
        "explain", "analyze", "vacuum", "show", "describe", "declare", "cursor", "open", "close",
        "for", "loop", "while", "if", "elsif", "procedure", "function", "trigger", "package", "body",
        "type", "subtype", "record", "exception", "raise", "return", "execute", "immediate",
        "connect", "to", "identified", "password", "session", "role", "tablespace",
        "timestamp", "date", "interval", "time", "zone", "local", "number", "numeric", "decimal",
        "varchar", "varchar2", "char", "nchar", "nvarchar2", "text", "clob", "blob", "raw", "bytea",
        "integer", "int", "smallint", "bigint", "serial", "bigserial", "boolean", "bool", "float",
        "double", "precision", "real", "uuid", "json", "jsonb", "array", "enum", "domain",
        "true", "false", "unknown", "nowait", "wait", "skip", "locked", "share", "mode",
        "parallel", "hint", "materialized", "refresh", "temporary", "temp", "unlogged"
    };

    public Func<IEnumerable<string>>? ExtraCandidates { get; set; }
    public Func<string, IEnumerable<string>>? ValueCandidates { get; set; }

    public event EventHandler? ExecuteStatementRequested;
    public event EventHandler? ExecuteScriptRequested;
    public event EventHandler? FormatRequested;
    public event EventHandler? CommentRequested;
    public event EventHandler? CommentMarkerRequested;
    public event EventHandler? PreviousStatementRequested;
    public event EventHandler? NextStatementRequested;

    public string SelectedText => _editor.SelectedText;

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public int CaretOffset
    {
        get => (int)GetValue(CaretOffsetProperty);
        set => SetValue(CaretOffsetProperty, value);
    }

    public int SelectionStart
    {
        get => (int)GetValue(SelectionStartProperty);
        set => SetValue(SelectionStartProperty, value);
    }

    public int SelectionLength
    {
        get => (int)GetValue(SelectionLengthProperty);
        set => SetValue(SelectionLengthProperty, value);
    }

    public double EditorFontSize
    {
        get => (double)GetValue(EditorFontSizeProperty);
        set => SetValue(EditorFontSizeProperty, value);
    }

    public void FocusEditor() => _editor.Focus();

    public void ShowFind()
    {
        _editor.Focus();
        var panel = ICSharpCode.AvalonEdit.Search.SearchPanel.Install(_editor);
        panel.Open();
    }

    public void ShowCompletion()
    {
        var caret = Math.Clamp(_editor.CaretOffset, 0, _editor.Text.Length);
        var text = _editor.Text;
        var context = SQLCompletion.ContextAt(text, caret);
        var (start, length) = SQLCompletion.WordRange(text, caret);
        var prefix = text.Substring(start, length);

        var candidates = new List<string>();
        if (context is { Mode: SQLCompletion.Kind.Values, Column: { } column })
        {
            if (ValueCandidates is not null)
                candidates.AddRange(ValueCandidates(column));
        }
        else
        {
            if (ExtraCandidates is not null) candidates.AddRange(ExtraCandidates());
            candidates.AddRange(SQLCompletion.SelectListColumns(text));
            candidates.AddRange(SqlKeywords);
        }

        var filtered = SQLCompletion.Filter(candidates, prefix);
        if (filtered.Count == 0) return;

        var window = new CompletionWindow(_editor.TextArea)
        {
            StartOffset = caret - prefix.Length,
            EndOffset = caret,
            Width = 320
        };
        foreach (var item in filtered)
            window.CompletionList.CompletionData.Add(new SqlCompletionData(item));
        window.Show();
    }

    private void EditorPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        if (ctrl && !alt && e.Key == Key.Enter)
        {
            ExecuteStatementRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }
        if (alt && !ctrl && e.Key == Key.X)
        {
            ExecuteScriptRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }
        if (ctrl && !alt && e.Key == Key.Space)
        {
            ShowCompletion();
            e.Handled = true;
            return;
        }
        if (ctrl && shift && !alt && e.Key == Key.F)
        {
            FormatRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }
        if (ctrl && !alt && e.Key == Key.Oem2)
        {
            CommentRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }
        if (ctrl && alt && e.Key == Key.Oem2)
        {
            CommentMarkerRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }
        if (alt && !ctrl && e.Key == Key.Up)
        {
            PreviousStatementRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }
        if (alt && !ctrl && e.Key == Key.Down)
        {
            NextStatementRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not SqlEditor editor || editor._syncing) return;
        var text = e.NewValue as string ?? "";
        if (editor._editor.Text == text) return;
        editor._syncing = true;
        try
        {
            editor._editor.Text = text;
            editor._editor.CaretOffset = Math.Min(editor.CaretOffset, text.Length);
        }
        finally { editor._syncing = false; }
    }

    private static void OnCaretChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not SqlEditor editor || editor._syncing) return;
        var offset = Math.Clamp((int)e.NewValue, 0, editor._editor.Text.Length);
        if (editor._editor.CaretOffset == offset) return;
        editor._syncing = true;
        try { editor._editor.CaretOffset = offset; }
        finally { editor._syncing = false; }
    }

    private static void OnSelectionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not SqlEditor editor || editor._syncing) return;
        var start = Math.Clamp(editor.SelectionStart, 0, editor._editor.Text.Length);
        var length = Math.Clamp(editor.SelectionLength, 0, editor._editor.Text.Length - start);
        editor._syncing = true;
        try { editor._editor.Select(start, length); }
        finally { editor._syncing = false; }
    }

    private static void OnFontSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SqlEditor editor) editor._editor.FontSize = (double)e.NewValue;
    }

    private static readonly string[] SqlKeywords =
    {
        "SELECT", "FROM", "WHERE", "GROUP BY", "ORDER BY", "HAVING", "LIMIT", "OFFSET",
        "JOIN", "LEFT JOIN", "RIGHT JOIN", "INNER JOIN", "FULL JOIN", "CROSS JOIN", "ON",
        "INSERT INTO", "VALUES", "UPDATE", "SET", "DELETE FROM", "RETURNING", "WITH",
        "UNION", "UNION ALL", "EXCEPT", "INTERSECT", "AND", "OR", "NOT", "NULL", "IS",
        "IN", "BETWEEN", "LIKE", "ILIKE", "EXISTS", "CASE", "WHEN", "THEN", "ELSE", "END",
        "DISTINCT", "COUNT", "SUM", "AVG", "MIN", "MAX", "COALESCE", "CAST", "AS",
        "ASC", "DESC", "CREATE", "ALTER", "DROP", "TABLE", "VIEW", "INDEX", "PRIMARY KEY"
    };

    private sealed class SqlCompletionData : ICompletionData
    {
        public SqlCompletionData(string text) => Text = text;

        public System.Windows.Media.ImageSource? Image => null;
        public string Text { get; }
        public object Content => Text;
        public object Description => Text;
        public double Priority => 0;

        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
        {
            textArea.Document.Replace(completionSegment, Text);
        }
    }
}
