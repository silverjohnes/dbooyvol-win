using System.Text;

namespace DBooyvol.Core.Sql;

public enum SqlTokenKind
{
    Word,
    QuotedIdentifier,
    String,
    Number,
    LineComment,
    BlockComment,
    Operator,
    Punct,
    Parameter
}

/// <summary>
/// A lexical token. Whitespace is not stored, but <see cref="SpaceBefore"/>
/// remembers whether the token was separated from the previous one, which the
/// formatter needs for <c>E'...'</c> style prefixes.
/// </summary>
public readonly record struct SqlToken(SqlTokenKind Kind, string Text, bool SpaceBefore = false)
{
    public string Upper => Text.ToUpperInvariant();
    public bool IsWord(string upper) => Kind == SqlTokenKind.Word && Upper == upper;
}

public sealed class SqlFormatterOptions
{
    public int IndentSize { get; set; } = 4;
    public bool UppercaseKeywords { get; set; } = true;
    /// <summary>Comma at the start of the next line instead of the end.</summary>
    public bool CommaFirst { get; set; }
    /// <summary>Used for long ON clauses; lists always break at commas.</summary>
    public int MaxWidth { get; set; } = 120;
}

/// <summary>
/// A dialect-neutral SQL formatter built on the same tokenizer the editor
/// already uses for statement splitting. It never rewrites literals, quoted
/// identifiers or comments; it only regenerates whitespace. Because every
/// formatting decision is a function of the token stream alone, formatting
/// already formatted SQL is a no-op (idempotent).
/// </summary>
public static class SqlFormatter
{
    private static readonly HashSet<string> MajorClauses = new(StringComparer.Ordinal)
    {
        "SELECT", "FROM", "WHERE", "GROUP BY", "ORDER BY", "HAVING",
        "LIMIT", "OFFSET", "FETCH", "VALUES", "SET", "RETURNING", "WINDOW",
        "UNION", "UNION ALL", "EXCEPT", "INTERSECT",
        "INSERT INTO", "UPDATE", "DELETE FROM", "MERGE INTO", "WITH",
        "ON CONFLICT", "CREATE", "ALTER", "DROP", "TRUNCATE",
        "GRANT", "REVOKE", "COMMENT", "EXPLAIN", "ANALYZE", "VACUUM",
        "BEGIN", "COMMIT", "ROLLBACK", "START TRANSACTION"
    };

    private static readonly HashSet<string> ListClauses = new(StringComparer.Ordinal)
    {
        "SELECT", "GROUP BY", "ORDER BY", "VALUES", "SET", "RETURNING"
    };

    private static readonly HashSet<string> JoinClauses = new(StringComparer.Ordinal)
    {
        "JOIN", "LEFT JOIN", "LEFT OUTER JOIN", "RIGHT JOIN", "RIGHT OUTER JOIN",
        "FULL JOIN", "FULL OUTER JOIN", "INNER JOIN", "CROSS JOIN", "NATURAL JOIN"
    };

    private static readonly HashSet<string> SetOperators = new(StringComparer.Ordinal)
    {
        "UNION", "UNION ALL", "EXCEPT", "INTERSECT"
    };

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        // Statement / clause words
        "SELECT", "FROM", "WHERE", "GROUP", "BY", "ORDER", "HAVING", "LIMIT",
        "OFFSET", "FETCH", "VALUES", "SET", "RETURNING", "WINDOW", "UNION", "ALL",
        "EXCEPT", "INTERSECT", "INSERT", "INTO", "UPDATE", "DELETE", "MERGE",
        "WITH", "AS", "ON", "USING", "JOIN", "LEFT", "RIGHT", "FULL", "INNER",
        "OUTER", "CROSS", "NATURAL", "CONFLICT", "DO", "NOTHING", "CREATE",
        "ALTER", "DROP", "TRUNCATE", "GRANT", "REVOKE", "COMMENT", "EXPLAIN",
        "ANALYZE", "VACUUM", "BEGIN", "COMMIT", "ROLLBACK", "START", "TRANSACTION",
        "TABLE", "VIEW", "INDEX", "SEQUENCE", "SCHEMA", "DATABASE", "PRIMARY",
        "FOREIGN", "KEY", "REFERENCES", "CONSTRAINT", "UNIQUE", "CHECK", "DEFAULT",
        "CASCADE", "RESTRICT", "IF", "EXISTS", "REPLACE", "TEMP", "TEMPORARY",
        // Expressions
        "AND", "OR", "NOT", "NULL", "IS", "IN", "BETWEEN", "LIKE", "ILIKE",
        "SIMILAR", "TO", "ESCAPE", "EXISTS", "ANY", "SOME", "CASE", "WHEN",
        "THEN", "ELSE", "END", "DISTINCT", "ALL", "OVER", "PARTITION", "ROWS",
        "RANGE", "PRECEDING", "FOLLOWING", "CURRENT", "ROW", "UNBOUNDED",
        "ASC", "DESC", "NULLS", "COLLATE",
        "TRUE", "FALSE", "UNKNOWN", "CAST", "TRY_CAST", "INTERVAL", "AT",
        "ZONE", "EXTRACT", "FILTER", "WITHIN", "GROUPING", "SETS",
        "CUBE", "ROLLUP", "LATERAL", "MATERIALIZED", "RECURSIVE", "ONLY",
        // DML extras
        "USING", "CONFLICT", "TIES"
    };

    private static readonly string[] MultiWordPhrases =
    {
        "LEFT OUTER JOIN", "RIGHT OUTER JOIN", "FULL OUTER JOIN",
        "LEFT JOIN", "RIGHT JOIN", "FULL JOIN", "INNER JOIN", "CROSS JOIN",
        "NATURAL JOIN", "GROUP BY", "ORDER BY", "UNION ALL", "INSERT INTO",
        "DELETE FROM", "MERGE INTO", "ON CONFLICT", "START TRANSACTION"
    };

    public static string Format(string sql, SqlFormatterOptions? options = null)
    {
        options ??= new SqlFormatterOptions();
        var tokens = CombinePhrases(Tokenize(sql));
        if (tokens.Count == 0) return "";
        return new Engine(tokens, options).Run();
    }

    public static bool LooksFormatted(string sql, SqlFormatterOptions? options = null) =>
        Format(sql, options) == sql;

    // ------------------------------------------------------------------
    // Tokenizer
    // ------------------------------------------------------------------

    public static List<SqlToken> Tokenize(string sql)
    {
        var tokens = new List<SqlToken>();
        var i = 0;
        var n = sql.Length;
        var spaceBefore = false;

        while (i < n)
        {
            var c = sql[i];
            if (char.IsWhiteSpace(c)) { spaceBefore = true; i++; continue; }

            var start = i;

            if (c == '-' && i + 1 < n && sql[i + 1] == '-')
            {
                while (i < n && sql[i] != '\n') i++;
                tokens.Add(new SqlToken(SqlTokenKind.LineComment, sql[start..i], spaceBefore));
                spaceBefore = false;
                continue;
            }

            if (c == '/' && i + 1 < n && sql[i + 1] == '*')
            {
                i += 2;
                var depth = 1;
                while (i < n && depth > 0)
                {
                    if (i + 1 < n && sql[i] == '/' && sql[i + 1] == '*') { depth++; i += 2; continue; }
                    if (i + 1 < n && sql[i] == '*' && sql[i + 1] == '/') { depth--; i += 2; continue; }
                    i++;
                }
                tokens.Add(new SqlToken(SqlTokenKind.BlockComment, sql[start..i], spaceBefore));
                spaceBefore = false;
                continue;
            }

            if (c == '$')
            {
                var tagEnd = DollarTagEnd(sql, i);
                if (tagEnd > 0)
                {
                    var tag = sql[i..tagEnd];
                    var close = sql.IndexOf(tag, tagEnd, StringComparison.Ordinal);
                    i = close < 0 ? n : close + tag.Length;
                    tokens.Add(new SqlToken(SqlTokenKind.String, sql[start..i], spaceBefore));
                    spaceBefore = false;
                    continue;
                }
                if (i + 1 < n && char.IsDigit(sql[i + 1]))
                {
                    i++;
                    while (i < n && char.IsDigit(sql[i])) i++;
                    tokens.Add(new SqlToken(SqlTokenKind.Parameter, sql[start..i], spaceBefore));
                    spaceBefore = false;
                    continue;
                }
            }

            if (c == '\'' || c == '"')
            {
                var quote = c;
                var escapeBackslash = quote == '\'' && !spaceBefore &&
                                      tokens.Count > 0 && tokens[^1].Kind == SqlTokenKind.Word &&
                                      (tokens[^1].Upper == "E" || tokens[^1].Upper == "N");
                i++;
                while (i < n)
                {
                    var d = sql[i];
                    if (escapeBackslash && d == '\\' && i + 1 < n) { i += 2; continue; }
                    if (d == quote)
                    {
                        if (i + 1 < n && sql[i + 1] == quote) { i += 2; continue; }
                        i++;
                        break;
                    }
                    i++;
                }
                tokens.Add(new SqlToken(
                    quote == '\'' ? SqlTokenKind.String : SqlTokenKind.QuotedIdentifier,
                    sql[start..i], spaceBefore));
                spaceBefore = false;
                continue;
            }

            if (char.IsDigit(c) || (c == '.' && i + 1 < n && char.IsDigit(sql[i + 1])))
            {
                i++;
                while (i < n && char.IsDigit(sql[i])) i++;
                if (i < n && sql[i] == '.')
                {
                    i++;
                    while (i < n && char.IsDigit(sql[i])) i++;
                }
                if (i < n && (sql[i] == 'e' || sql[i] == 'E'))
                {
                    var save = i;
                    i++;
                    if (i < n && (sql[i] == '+' || sql[i] == '-')) i++;
                    if (i < n && char.IsDigit(sql[i]))
                    {
                        while (i < n && char.IsDigit(sql[i])) i++;
                    }
                    else i = save;
                }
                tokens.Add(new SqlToken(SqlTokenKind.Number, sql[start..i], spaceBefore));
                spaceBefore = false;
                continue;
            }

            if (IsWordStart(c))
            {
                i++;
                while (i < n && IsWordPart(sql[i])) i++;
                tokens.Add(new SqlToken(SqlTokenKind.Word, sql[start..i], spaceBefore));
                spaceBefore = false;
                continue;
            }

            if (c == ':' && i + 1 < n && IsWordStart(sql[i + 1]))
            {
                i += 2;
                while (i < n && IsWordPart(sql[i])) i++;
                tokens.Add(new SqlToken(SqlTokenKind.Parameter, sql[start..i], spaceBefore));
                spaceBefore = false;
                continue;
            }

            var op = MatchOperator(sql, i);
            if (op is not null)
            {
                i += op.Length;
                tokens.Add(new SqlToken(SqlTokenKind.Operator, op, spaceBefore));
                spaceBefore = false;
                continue;
            }

            i++;
            tokens.Add(new SqlToken(SqlTokenKind.Punct, sql[start..i], spaceBefore));
            spaceBefore = false;
        }

        return tokens;
    }

    private static List<SqlToken> CombinePhrases(List<SqlToken> tokens)
    {
        var result = new List<SqlToken>(tokens.Count);
        var i = 0;
        while (i < tokens.Count)
        {
            var matched = false;
            if (tokens[i].Kind == SqlTokenKind.Word)
            {
                foreach (var phrase in MultiWordPhrases)
                {
                    var words = phrase.Split(' ');
                    if (i + words.Length > tokens.Count) continue;
                    var ok = true;
                    for (var k = 0; k < words.Length; k++)
                    {
                        var token = tokens[i + k];
                        if (token.Kind != SqlTokenKind.Word || token.Upper != words[k]) { ok = false; break; }
                    }
                    if (!ok) continue;

                    var text = string.Join(" ", tokens.Skip(i).Take(words.Length).Select(t => t.Text));
                    result.Add(new SqlToken(SqlTokenKind.Word, text, tokens[i].SpaceBefore));
                    i += words.Length;
                    matched = true;
                    break;
                }
            }
            if (matched) continue;
            result.Add(tokens[i]);
            i++;
        }
        return result;
    }

    private static string? MatchOperator(string sql, int index)
    {
        string[] multi =
        {
            "!~~*", "!~~", "~~*", "->>", "#>>", "::", ":=", "<=", ">=", "<>", "!=",
            "||", "&&", "!~*", "!~", "~*", "~~", "<<", ">>", "=>", "@>", "<@",
            "?|", "?&", "->", "#>", "**", "/*", "*/"
        };
        foreach (var candidate in multi)
        {
            if (index + candidate.Length <= sql.Length &&
                string.CompareOrdinal(sql, index, candidate, 0, candidate.Length) == 0)
                return candidate;
        }
        return "+-*/%<>=!~^&|@?#".IndexOf(sql[index]) >= 0 ? sql[index].ToString() : null;
    }

    private static int DollarTagEnd(string sql, int index)
    {
        var j = index + 1;
        while (j < sql.Length)
        {
            var c = sql[j];
            if (c == '$') return j + 1;
            if (!char.IsLetterOrDigit(c) && c != '_') return -1;
            j++;
        }
        return -1;
    }

    private static bool IsWordStart(char c) =>
        char.IsLetter(c) || c is '_' or '@' or '#';

    private static bool IsWordPart(char c) =>
        char.IsLetterOrDigit(c) || c is '_' or '$' or '#' or '@';

    private static bool IsKeyword(string upper) => Keywords.Contains(upper);

    // ------------------------------------------------------------------
    // Layout engine
    // ------------------------------------------------------------------

    private sealed class Engine
    {
        private readonly List<SqlToken> _tokens;
        private readonly SqlFormatterOptions _options;
        private readonly StringBuilder _line = new();
        private readonly List<string> _lines = new();

        private int _indent;
        private int _clauseIndent;
        private int _parenDepth;
        private int _baseDepth;
        private int _conditionIndent = 1;
        private bool _inCondition;
        private bool _betweenPending;
        private bool _afterSetOperator;
        private bool _tightAfterUnary;
        private int _caseDepth;
        private SqlToken? _lastToken;
        private ListState? _list;
        private readonly Stack<Context> _contexts = new();

        private sealed class ListState
        {
            public int Depth { get; init; }
            public int Indent { get; init; }
        }

        private sealed class Context
        {
            public int Indent { get; init; }
            public int ClauseIndent { get; init; }
            public int BaseDepth { get; init; }
            public int ConditionIndent { get; init; }
            public bool InCondition { get; init; }
            public ListState? List { get; init; }
            public int CaseDepth { get; init; }
        }

        private sealed class ParenInfo
        {
            public bool IsSubquery { get; init; }
            public Context Saved { get; init; } = new();
        }

        private readonly Stack<ParenInfo> _parens = new();

        public Engine(List<SqlToken> tokens, SqlFormatterOptions options)
        {
            _tokens = tokens;
            _options = options;
        }

        public string Run()
        {
            for (var i = 0; i < _tokens.Count; i++)
            {
                var token = _tokens[i];
                switch (token.Kind)
                {
                    case SqlTokenKind.LineComment:
                        AppendComment(token.Text);
                        NewLine();
                        continue;
                    case SqlTokenKind.BlockComment:
                        AppendComment(token.Text);
                        if (token.Text.Contains('\n')) NewLine();
                        continue;
                }

                if (token.Kind == SqlTokenKind.Word)
                {
                    var upper = token.Upper;

                    if (MajorClauses.Contains(upper) && _parenDepth == _baseDepth)
                    {
                        HandleMajorClause(token, upper, i);
                        continue;
                    }

                    if (JoinClauses.Contains(upper) && _parenDepth == _baseDepth)
                    {
                        NewLine();
                        SetIndent(_clauseIndent);
                        AppendToken(token, forcedSpace: false);
                        _inCondition = false;
                        _list = null;
                        continue;
                    }

                    if (upper == "ON" && _parenDepth == _baseDepth)
                    {
                        if (_line.Length + 3 > _options.MaxWidth && _line.Length > 0)
                        {
                            NewLine();
                            SetIndent(_clauseIndent + 1);
                        }
                        AppendToken(token, forcedSpace: true);
                        _inCondition = true;
                        _conditionIndent = _clauseIndent + 1;
                        continue;
                    }

                    if ((upper == "AND" || upper == "OR") && _parenDepth == _baseDepth)
                    {
                        if (upper == "AND" && _betweenPending)
                        {
                            _betweenPending = false;
                            AppendToken(token, forcedSpace: true);
                            continue;
                        }
                        if (_inCondition && _caseDepth == 0)
                        {
                            NewLine();
                            SetIndent(_conditionIndent);
                            AppendToken(token, forcedSpace: false);
                            continue;
                        }
                    }

                    if (upper == "BETWEEN") _betweenPending = true;

                    if (upper == "CASE")
                    {
                        AppendToken(token, forcedSpace: true);
                        _caseDepth++;
                        _indent++;
                        NewLine();
                        continue;
                    }
                    if (upper == "WHEN" && _caseDepth > 0)
                    {
                        NewLine();
                        AppendToken(token, forcedSpace: false);
                        continue;
                    }
                    if (upper == "ELSE" && _caseDepth > 0)
                    {
                        NewLine();
                        AppendToken(token, forcedSpace: false);
                        continue;
                    }
                    if (upper == "END" && _caseDepth > 0)
                    {
                        _caseDepth--;
                        _indent = Math.Max(0, _indent - 1);
                        NewLine();
                        AppendToken(token, forcedSpace: false);
                        continue;
                    }
                }

                if (token.Kind == SqlTokenKind.Punct)
                {
                    switch (token.Text)
                    {
                        case "(":
                        {
                            var isSubquery = IsSubqueryStart(i);
                            _parens.Push(new ParenInfo
                            {
                                IsSubquery = isSubquery,
                                Saved = new Context
                                {
                                    Indent = _indent,
                                    ClauseIndent = _clauseIndent,
                                    BaseDepth = _baseDepth,
                                    ConditionIndent = _conditionIndent,
                                    InCondition = _inCondition,
                                    List = _list,
                                    CaseDepth = _caseDepth
                                }
                            });
                            AppendToken(token, forcedSpace: false);
                            _parenDepth++;
                            if (isSubquery)
                            {
                                _indent++;
                                _clauseIndent = _indent;
                                _baseDepth = _parenDepth;
                                _conditionIndent = _indent + 1;
                                _inCondition = false;
                                _list = null;
                                _caseDepth = 0;
                                NewLine();
                            }
                            continue;
                        }
                        case ")":
                        {
                            var info = _parens.Count > 0 ? _parens.Pop() : null;
                            if (info is { IsSubquery: true })
                            {
                                NewLine();
                                var saved = info.Saved;
                                _indent = saved.Indent;
                                _clauseIndent = saved.ClauseIndent;
                                _baseDepth = saved.BaseDepth;
                                _conditionIndent = saved.ConditionIndent;
                                _inCondition = saved.InCondition;
                                _list = saved.List;
                                _caseDepth = saved.CaseDepth;
                            }
                            _parenDepth = Math.Max(0, _parenDepth - 1);
                            AppendToken(token, forcedSpace: false);
                            continue;
                        }
                        case ",":
                            HandleComma();
                            continue;
                        case ";":
                            AppendToken(token, forcedSpace: false);
                            NewLine();
                            _indent = 0;
                            _clauseIndent = 0;
                            _parenDepth = 0;
                            _baseDepth = 0;
                            _conditionIndent = 1;
                            _inCondition = false;
                            _list = null;
                            _caseDepth = 0;
                            _contexts.Clear();
                            _parens.Clear();
                            continue;
                    }
                }

                if (token.Kind == SqlTokenKind.Operator &&
                    (token.Text == "-" || token.Text == "+") &&
                    (IsLineEmpty() || LastIsOpenOrOperator()))
                {
                    AppendRaw(token.Text);
                    _tightAfterUnary = true;
                    continue;
                }

                AppendToken(token, forcedSpace: false);
            }

            Flush();
            while (_lines.Count > 0 && _lines[^1].Length == 0) _lines.RemoveAt(_lines.Count - 1);
            return string.Join("\n", _lines);
        }

        private void HandleMajorClause(SqlToken token, string upper, int index)
        {
            var afterSetOperator = _afterSetOperator && upper == "SELECT";
            if (!afterSetOperator)
            {
                NewLine();
                _indent = _clauseIndent;
                SetIndent(_clauseIndent);
            }
            else
            {
                AppendToken(token, forcedSpace: true);
                _afterSetOperator = false;
                _list = null;
                _inCondition = false;
                return;
            }

            _list = null;
            _inCondition = false;
            _betweenPending = false;

            AppendToken(token, forcedSpace: false);

            if (SetOperators.Contains(upper))
            {
                _afterSetOperator = true;
                return;
            }

            if (ListClauses.Contains(upper))
            {
                var multiline = upper switch
                {
                    "SELECT" => SelectListIsMultiline(index),
                    "VALUES" => TopLevelCommaExists(index),
                    _ => TopLevelCommaExists(index)
                };
                if (multiline)
                {
                    _list = new ListState { Depth = _parenDepth, Indent = _clauseIndent + 1 };
                    NewLine();
                    SetIndent(_clauseIndent + 1);
                }
            }

            if (upper is "WHERE" or "HAVING")
            {
                _inCondition = true;
                _conditionIndent = _clauseIndent + 1;
            }
            else if (upper is "ON CONFLICT")
            {
                _inCondition = false;
            }
        }

        private void HandleComma()
        {
            if (_list is not null && _parenDepth == _list.Depth)
            {
                if (_options.CommaFirst)
                {
                    NewLine();
                    SetIndent(_list.Indent);
                    AppendRaw(",");
                }
                else
                {
                    AppendRaw(",");
                    NewLine();
                    SetIndent(_list.Indent);
                }
                return;
            }
            AppendRaw(",");
        }

        private bool SelectListIsMultiline(int selectIndex)
        {
            var commas = 0;
            var depth = 0;
            for (var j = selectIndex + 1; j < _tokens.Count; j++)
            {
                var token = _tokens[j];
                if (token.Kind == SqlTokenKind.Punct)
                {
                    if (token.Text == "(") depth++;
                    else if (token.Text == ")")
                    {
                        if (depth == 0) break;
                        depth--;
                    }
                    else if (token.Text == "," && depth == 0) commas++;
                    else if (token.Text == ";" && depth == 0) break;
                }
                else if (token.Kind == SqlTokenKind.Word && depth == 0)
                {
                    var upper = token.Upper;
                    if (upper != "SELECT" && MajorClauses.Contains(upper)) break;
                    if (JoinClauses.Contains(upper)) break;
                }
            }
            return commas > 0;
        }

        private bool TopLevelCommaExists(int clauseIndex)
        {
            var depth = 0;
            for (var j = clauseIndex + 1; j < _tokens.Count; j++)
            {
                var token = _tokens[j];
                if (token.Kind == SqlTokenKind.Punct)
                {
                    if (token.Text == "(") depth++;
                    else if (token.Text == ")")
                    {
                        if (depth == 0) break;
                        depth--;
                    }
                    else if (token.Text == "," && depth == 0) return true;
                    else if (token.Text == ";" && depth == 0) break;
                }
                else if (token.Kind == SqlTokenKind.Word && depth == 0)
                {
                    var upper = token.Upper;
                    if (MajorClauses.Contains(upper) && upper != "SELECT") break;
                    if (JoinClauses.Contains(upper)) break;
                }
            }
            return false;
        }

        private bool IsSubqueryStart(int index)
        {
            for (var j = index + 1; j < _tokens.Count; j++)
            {
                var token = _tokens[j];
                if (token.Kind is SqlTokenKind.LineComment or SqlTokenKind.BlockComment) continue;
                return token.Kind == SqlTokenKind.Word &&
                       (token.Upper == "SELECT" || token.Upper == "WITH");
            }
            return false;
        }

        // --------------------------------------------------------------
        // Output primitives
        // --------------------------------------------------------------

        private bool IsLineEmpty() => _line.Length == 0;

        private bool LastIsOpenOrOperator()
        {
            if (_line.Length == 0) return true;
            var last = _line[^1];
            return last is '(' or ',' or '=' or '<' or '>' or '+' or '-' or '*' or '/' or '%' or '|' or '&';
        }

        private void SetIndent(int level) => _indent = Math.Max(0, level);

        private void NewLine()
        {
            if (_line.Length == 0) return;
            _lines.Add(_line.ToString().TrimEnd());
            _line.Clear();
        }

        private void Flush() => NewLine();

        private void AppendRaw(string text)
        {
            if (_line.Length == 0) _line.Append(' ', _indent * _options.IndentSize);
            _line.Append(text);
        }

        private void AppendComment(string text)
        {
            if (_line.Length > 0 && !char.IsWhiteSpace(_line[^1])) _line.Append(' ');
            AppendRaw(text);
        }

        private void AppendToken(SqlToken token, bool forcedSpace)
        {
            var text = token.Text;
            if (token.Kind == SqlTokenKind.Word && _options.UppercaseKeywords &&
                (IsKeyword(token.Upper) || MajorClauses.Contains(token.Upper) || JoinClauses.Contains(token.Upper)))
                text = token.Upper;

            var prev = _line.Length > 0 ? _line[^1] : '\0';
            var space = forcedSpace || (_line.Length > 0 && !_tightAfterUnary &&
                        NeedSpace(prev, token, text));
            _tightAfterUnary = false;

            if (_line.Length == 0) _line.Append(' ', _indent * _options.IndentSize);
            else if (space && !char.IsWhiteSpace(prev)) _line.Append(' ');

            _line.Append(text);
            _lastToken = new SqlToken(token.Kind, text, token.SpaceBefore);
        }

        private bool NeedSpace(char prev, SqlToken token, string text)
        {
            if (prev == '\0') return false;
            if (char.IsWhiteSpace(prev)) return false;
            if (prev is '(' or '[' or '{' or '.') return false;
            if (text is ")" or "]" or "}" or "," or ";" or ".") return false;
            if (text == "::" || prev == ':') return false;
            if (prev == '.' && text == ".") return false;

            if (text == "(")
            {
                if (prev is ')' or ']') return false;
                // Function call: lower(x) / count(*) keeps no space; a keyword
                // such as IN / VALUES / AS / OVER keeps one.
                if (_lastToken is { Kind: SqlTokenKind.Word } last && !IsKeyword(last.Upper))
                    return false;
                return true;
            }

            if (token.Kind == SqlTokenKind.String && !token.SpaceBefore &&
                prev is 'E' or 'e' or 'N' or 'n' or 'X' or 'x' or 'B' or 'b')
                return false;

            return true;
        }
    }
}
