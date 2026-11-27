using DBooyvol.Core.Models;

namespace DBooyvol.Core.Data;

public enum GridValueFormat { Display, Editable, DatabaseNative }

public enum GridClipboardFormat { Csv, DbUnit, Html, Json, Markdown, Sql, Source, Txt, Xml }

public sealed class GridCopySettings
{
    public bool CopyHeader { get; set; }
    public bool CopyRowNumbers { get; set; }
    public bool QuoteValues { get; set; } = true;
    public bool AlwaysQuote { get; set; }
    public bool CopyAsHtml { get; set; }
    public GridValueFormat ValueFormat { get; set; } = GridValueFormat.Display;
    public string ColumnDelimiter { get; set; } = "\t";
    public string RowDelimiter { get; set; } = "\n";
    public string QuoteCharacter { get; set; } = "\"";
    public string CustomColumnDelimiter { get; set; } = "";
    public string CustomRowDelimiter { get; set; } = "";
    public string CustomQuoteCharacter { get; set; } = "";
}

public sealed class GridPasteSettings
{
    public bool InsertMultipleRows { get; set; } = true;
    public bool IgnoreQuotes { get; set; }
    public bool InsertNulls { get; set; } = true;
    public string NullMark { get; set; } = "NULL";
    public string CustomNullMark { get; set; } = "";
}

public sealed class GridClipboardPayload
{
    public List<string> Columns { get; set; } = new();
    public List<string?[]> Rows { get; set; } = new();
    public List<int> RowNumbers { get; set; } = new();
    public List<ColumnInfo> ColumnInfo { get; set; } = new();

    public bool IsEmpty => Rows.Count == 0 || Columns.Count == 0;
}

/// <summary>
/// Copy/paste in nine formats. The text formats are byte-compatible with the
/// macOS implementation; escaping rules are intentionally literal.
/// </summary>
public static class GridClipboard
{
    public static string Text(GridClipboardPayload payload, GridCopySettings settings)
    {
        if (payload.IsEmpty) return "";
        var applied = Apply(settings.ValueFormat, payload);
        return RenderText(applied, settings);
    }

    private static string RenderText(GridClipboardPayload payload, GridCopySettings settings)
    {
        if (payload.IsEmpty) return "";
        if (settings.CopyAsHtml)
            return HtmlTable(payload, includeHeader: settings.CopyHeader);

        var lines = new List<string>();
        if (settings.CopyHeader)
        {
            var cells = payload.Columns.Select(c => Quote(c, settings)).ToList();
            if (settings.CopyRowNumbers) cells.Insert(0, "");
            lines.Add(string.Join(settings.ColumnDelimiter, cells));
        }
        for (var index = 0; index < payload.Rows.Count; index++)
        {
            var row = payload.Rows[index];
            var cells = row.Select(v => v is null ? "" : Quote(v, settings)).ToList();
            if (settings.CopyRowNumbers)
            {
                var number = index < payload.RowNumbers.Count ? payload.RowNumbers[index] : index + 1;
                cells.Insert(0, number.ToString());
            }
            lines.Add(string.Join(settings.ColumnDelimiter, cells));
        }
        return string.Join(settings.RowDelimiter, lines);
    }

    public static string Quote(string value, GridCopySettings settings)
    {
        if (!settings.QuoteValues && !settings.AlwaysQuote) return value;
        var q = settings.QuoteCharacter;
        var needsQuotes = settings.AlwaysQuote
            || value.Contains(settings.ColumnDelimiter, StringComparison.Ordinal)
            || value.Contains(settings.RowDelimiter, StringComparison.Ordinal)
            || value.Contains(q, StringComparison.Ordinal)
            || value.Contains('\n') || value.Contains('\r');
        if (!needsQuotes) return value;
        return q + value.Replace(q, q + q) + q;
    }

    public static string Formatted(GridClipboardFormat format, GridClipboardPayload payload,
        string tableName, GridValueFormat valueFormat = GridValueFormat.Display)
    {
        var applied = Apply(valueFormat, payload);
        return format switch
        {
            GridClipboardFormat.Csv => CsvText(applied),
            GridClipboardFormat.Txt => TxtText(applied),
            GridClipboardFormat.Html => HtmlTable(WithHeader(applied), includeHeader: true),
            GridClipboardFormat.Json => Json(applied),
            GridClipboardFormat.Markdown => Markdown(applied),
            GridClipboardFormat.Sql => Sql(applied, tableName),
            GridClipboardFormat.Source => Source(applied),
            GridClipboardFormat.Xml => Xml(applied),
            GridClipboardFormat.DbUnit => DbUnit(applied, tableName),
            _ => ""
        };
    }

    private static GridClipboardPayload WithHeader(GridClipboardPayload payload) => payload;

    private static string CsvText(GridClipboardPayload payload)
    {
        var settings = new GridCopySettings
        {
            CopyHeader = true,
            ColumnDelimiter = ",",
            RowDelimiter = "\r\n"
        };
        return RenderText(payload, settings) + "\r\n";
    }

    private static string TxtText(GridClipboardPayload payload)
    {
        var settings = new GridCopySettings { QuoteValues = false };
        return RenderText(payload, settings);
    }

    public static string HtmlDocument(GridClipboardPayload payload, string tableName,
        GridValueFormat valueFormat = GridValueFormat.Display)
    {
        var applied = Apply(valueFormat, payload);
        var title = EscapeHtml(tableName);
        return "<!DOCTYPE html>\n<html>\n<head>\n<meta charset=\"utf-8\">\n"
            + $"<title>{title}</title>\n"
            + "<style>body{font-family:Segoe UI,Helvetica,Arial,sans-serif;font-size:13px}"
            + "table{border-collapse:collapse}th,td{border:1px solid #ccc;padding:4px 8px;"
            + "text-align:left}th{background:#f2f2f2}</style>\n"
            + "</head>\n<body>\n"
            + HtmlTable(applied, includeHeader: true)
            + "\n</body>\n</html>\n";
    }

    private static GridClipboardPayload Apply(GridValueFormat format, GridClipboardPayload payload)
    {
        if (format == GridValueFormat.Display || payload.ColumnInfo.Count == 0) return payload;
        var result = new GridClipboardPayload
        {
            Columns = payload.Columns,
            RowNumbers = payload.RowNumbers,
            ColumnInfo = payload.ColumnInfo,
            Rows = payload.Rows.Select(row => row.Select((value, index) =>
                index < payload.ColumnInfo.Count
                    ? FormattedValue(value, payload.ColumnInfo[index], format)
                    : value).ToArray()).ToList()
        };
        return result;
    }

    private static string? FormattedValue(string? value, ColumnInfo column, GridValueFormat format)
    {
        if (value is null) return null;
        return format switch
        {
            GridValueFormat.Editable => EditableValue(value, column),
            GridValueFormat.DatabaseNative => NativeValue(value, column),
            _ => value
        };
    }

    private static string EditableValue(string value, ColumnInfo column)
    {
        if (!IsTemporal(column) || value.Length <= 10) return value;
        var t = value.IndexOf('T');
        if (t <= 0 || t >= value.Length - 1) return value;
        return value[..t] + " " + value[(t + 1)..];
    }

    private static string NativeValue(string value, ColumnInfo column)
    {
        var type = column.Type.ToUpperInvariant();
        if (IsBoolean(column))
        {
            var lower = value.ToLowerInvariant();
            var isTrue = lower is "true" or "t" or "1";
            var isFalse = lower is "false" or "f" or "0";
            if (!isTrue && !isFalse) return value;
            if (type == "BOOL") return isTrue ? "t" : "f";
            if (type == "BIT" || type.StartsWith("TINYINT", StringComparison.Ordinal))
                return isTrue ? "1" : "0";
            return value;
        }
        if (IsBinary(column))
        {
            try
            {
                var bytes = Convert.FromBase64String(value);
                var hex = Convert.ToHexString(bytes);
                return (type.Contains("BYTEA", StringComparison.Ordinal) ? "\\x" : "0x") + hex;
            }
            catch (FormatException) { }
        }
        return value;
    }

    private static bool IsTemporal(ColumnInfo column)
    {
        var type = column.Type.ToUpperInvariant();
        return type.Contains("DATE", StringComparison.Ordinal) || type.Contains("TIME", StringComparison.Ordinal);
    }

    private static bool IsBoolean(ColumnInfo column)
    {
        var type = column.Type.ToUpperInvariant();
        return type == "BOOL" || type.Contains("BOOLEAN", StringComparison.Ordinal)
            || type == "BIT" || type.StartsWith("TINYINT", StringComparison.Ordinal)
            || column.ClassName == "java.lang.Boolean";
    }

    private static bool IsBinary(ColumnInfo column)
    {
        var type = column.Type.ToUpperInvariant();
        return column.ClassName == "[B" || type.Contains("BLOB", StringComparison.Ordinal)
            || type.Contains("BINARY", StringComparison.Ordinal)
            || type.Contains("BYTEA", StringComparison.Ordinal)
            || type.Contains("IMAGE", StringComparison.Ordinal) || type == "RAW";
    }

    private static string HtmlTable(GridClipboardPayload payload, bool includeHeader)
    {
        var sb = new System.Text.StringBuilder("<table>\n");
        if (includeHeader)
        {
            sb.Append("  <thead>\n    <tr>");
            foreach (var column in payload.Columns)
                sb.Append("<th>").Append(EscapeHtml(column)).Append("</th>");
            sb.Append("</tr>\n  </thead>\n");
        }
        sb.Append("  <tbody>\n");
        foreach (var row in payload.Rows)
        {
            sb.Append("    <tr>");
            foreach (var value in row)
                sb.Append("<td>").Append(EscapeHtml(value ?? "")).Append("</td>");
            sb.Append("</tr>\n");
        }
        sb.Append("  </tbody>\n</table>");
        return sb.ToString();
    }

    private static string Json(GridClipboardPayload payload)
    {
        var lines = new List<string> { "[" };
        for (var r = 0; r < payload.Rows.Count; r++)
        {
            var row = payload.Rows[r];
            var fields = new List<string>();
            for (var c = 0; c < payload.Columns.Count; c++)
            {
                var value = c < row.Length ? row[c] : null;
                fields.Add("    " + EscapeJson(payload.Columns[c]) + ": " +
                           (value is null ? "null" : EscapeJson(value)));
            }
            lines.Add("  {");
            lines.Add(string.Join(",\n", fields));
            lines.Add(r == payload.Rows.Count - 1 ? "  }" : "  },");
        }
        lines.Add("]");
        return string.Join("\n", lines);
    }

    private static string Markdown(GridClipboardPayload payload)
    {
        var lines = new List<string>
        {
            "| " + string.Join(" | ", payload.Columns.Select(EscapeMarkdown)) + " |",
            "| " + string.Join(" | ", payload.Columns.Select(_ => "---")) + " |"
        };
        foreach (var row in payload.Rows)
            lines.Add("| " + string.Join(" | ", row.Select(v => EscapeMarkdown(v ?? ""))) + " |");
        return string.Join("\n", lines);
    }

    private static string Sql(GridClipboardPayload payload, string tableName)
    {
        var table = SqlIdentifier(tableName);
        var columns = string.Join(", ", payload.Columns.Select(SqlIdentifier));
        var lines = new List<string>();
        foreach (var row in payload.Rows)
        {
            var values = row.Select(v => v is null ? "NULL" : "'" + v.Replace("'", "''") + "'");
            // sast:allow DBY009 — это текст для буфера обмена, приложение его не исполняет.
            lines.Add($"INSERT INTO {table} ({columns}) VALUES ({string.Join(", ", values)});");
        }
        return string.Join("\n", lines);
    }

    private static string Source(GridClipboardPayload payload)
    {
        var lines = new List<string> { "String[][] data = {" };
        foreach (var row in payload.Rows)
            lines.Add("    { " + string.Join(", ", row.Select(v => v is null ? "null" : "\"" + EscapeCode(v) + "\"")) + " },");
        lines.Add("};");
        return string.Join("\n", lines);
    }

    private static string Xml(GridClipboardPayload payload)
    {
        var sb = new System.Text.StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<result>\n");
        foreach (var row in payload.Rows)
        {
            sb.Append("  <row>\n");
            for (var c = 0; c < payload.Columns.Count; c++)
            {
                var value = c < row.Length ? row[c] : null;
                var tag = XmlTag(payload.Columns[c]);
                if (value is null) sb.Append($"    <{tag} null=\"true\" />\n");
                else sb.Append($"    <{tag}>").Append(EscapeXml(value)).Append("</").Append(tag).Append(">\n");
            }
            sb.Append("  </row>\n");
        }
        sb.Append("</result>");
        return sb.ToString();
    }

    private static string DbUnit(GridClipboardPayload payload, string tableName)
    {
        var table = XmlTag(tableName);
        var sb = new System.Text.StringBuilder("<?xml version='1.0' encoding='UTF-8'?>\n<dataset>\n");
        foreach (var row in payload.Rows)
        {
            var attributes = new List<string>();
            for (var c = 0; c < payload.Columns.Count; c++)
            {
                var value = c < row.Length ? row[c] : null;
                attributes.Add(XmlTag(payload.Columns[c]) + "=\"" + EscapeXml(value ?? "") + "\"");
            }
            sb.Append("  <").Append(table).Append(' ').Append(string.Join(" ", attributes)).Append(" />\n");
        }
        sb.Append("</dataset>");
        return sb.ToString();
    }

    public static List<string?[]> Parse(string text, GridPasteSettings settings, GridCopySettings copy)
    {
        var rows = new List<string?[]>();
        if (string.IsNullOrEmpty(text)) return rows;
        var normalized = text.Replace("\r\n", "\n").Replace("\r", "\n");
        var rowSeparator = copy.RowDelimiter.Length == 1 ? copy.RowDelimiter[0] : '\n';
        var columnSeparator = copy.ColumnDelimiter.Length == 1 ? copy.ColumnDelimiter[0] : '\t';
        var quote = copy.QuoteCharacter.Length == 1 && !settings.IgnoreQuotes ? copy.QuoteCharacter[0] : (char?)null;

        var current = new List<string?>();
        var field = new System.Text.StringBuilder();
        var inQuotes = false;
        var index = 0;

        void FinishField()
        {
            var value = field.ToString();
            current.Add(settings.InsertNulls && value == settings.NullMark ? null : value);
            field.Clear();
        }
        void FinishRow()
        {
            FinishField();
            rows.Add(current.ToArray());
            current.Clear();
        }

        while (index < normalized.Length)
        {
            var c = normalized[index];
            if (quote is { } q && c == q)
            {
                if (inQuotes)
                {
                    if (index + 1 < normalized.Length && normalized[index + 1] == q)
                    {
                        field.Append(q);
                        index += 2;
                        continue;
                    }
                    inQuotes = false;
                    index++;
                    continue;
                }
                if (field.Length == 0)
                {
                    inQuotes = true;
                    index++;
                    continue;
                }
            }
            if (!inQuotes && c == columnSeparator) { FinishField(); index++; continue; }
            if (!inQuotes && c == rowSeparator) { FinishRow(); index++; continue; }
            field.Append(c);
            index++;
        }
        if (field.Length > 0 || current.Count > 0) FinishRow();
        return rows;
    }

    public static string DisplayDelimiter(string value) =>
        value.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\n", "\\n").Replace("\r", "\\r");

    public static string DelimiterFromDisplay(string text)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\\') { sb.Append(text[i]); continue; }
            if (i + 1 >= text.Length) { sb.Append('\\'); break; }
            i++;
            sb.Append(text[i] switch
            {
                't' => '\t', 'n' => '\n', 'r' => '\r', '\\' => '\\', _ => text[i]
            });
        }
        return sb.ToString();
    }

    private static string EscapeHtml(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string EscapeJson(string s)
    {
        var sb = new System.Text.StringBuilder("\"");
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append($"\\u{(int)c:x4}");
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    private static string EscapeMarkdown(string s) => s.Replace("|", "\\|").Replace("\n", "<br>");

    private static string EscapeCode(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n")
            .Replace("\r", "\\r").Replace("\t", "\\t");

    public static string EscapeXml(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&apos;"); break;
                default:
                    if (c < 0x20 && c is not '\t' and not '\n' and not '\r')
                        sb.Append($"&#x{(int)c:X};");
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    private static string SqlIdentifier(string name)
    {
        var trimmed = name.Trim();
        return trimmed.Length == 0 ? "table" : trimmed;
    }

    private static string XmlTag(string name)
    {
        var sb = new System.Text.StringBuilder();
        var index = 0;
        foreach (var c in name)
        {
            var allowed = char.IsLetter(c) || c == '_' ||
                          (index > 0 && (char.IsDigit(c) || c == '-' || c == '.'));
            sb.Append(allowed ? c : '_');
            index++;
        }
        var result = sb.ToString();
        if (result.Length == 0) return "column";
        if (char.IsDigit(result[0])) result = "_" + result;
        return result;
    }
}
