using System.IO.Compression;
using System.Text;
using DBooyvol.Core.Localization;
using DBooyvol.Core.Models;

namespace DBooyvol.Core.Data;

/// <summary>
/// CSV/XML/XLSX export. CSV is RFC 4180 with CRLF; XML has the shape from the
/// handoff; XLSX is a minimal OOXML package where every cell is an inline
/// string, so leading zeros and 20-digit ids survive.
/// </summary>
public static class Exporter
{
    public static string Csv(IReadOnlyList<ColumnInfo> columns, IReadOnlyList<string?[]> rows,
        string delimiter = ",", string nullValue = "")
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(delimiter, columns.Select(c => EscapeCsv(c.Name, delimiter))));
        sb.Append("\r\n");
        foreach (var row in rows)
        {
            sb.Append(string.Join(delimiter, row.Select(v => v is null ? nullValue : EscapeCsv(v, delimiter))));
            sb.Append("\r\n");
        }
        return sb.ToString();
    }

    private static string EscapeCsv(string s, string delimiter)
    {
        var needsQuotes = s.Contains(delimiter, StringComparison.Ordinal) || s.Contains('"')
            || s.Contains('\n') || s.Contains('\r');
        if (!needsQuotes) return s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    public static string Xml(IReadOnlyList<ColumnInfo> columns, IReadOnlyList<string?[]> rows)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append($"<result rows=\"{rows.Count}\">\n");
        sb.Append("  <columns>\n");
        foreach (var c in columns)
            sb.Append($"    <column name=\"{GridClipboard.EscapeXml(c.Name)}\" type=\"{GridClipboard.EscapeXml(c.Type)}\" />\n");
        sb.Append("  </columns>\n");
        sb.Append("  <rows>\n");
        foreach (var row in rows)
        {
            sb.Append("    <row>\n");
            for (var i = 0; i < columns.Count; i++)
            {
                var c = columns[i];
                var value = i < row.Length ? row[i] : null;
                if (value is null)
                    sb.Append($"      <col name=\"{GridClipboard.EscapeXml(c.Name)}\" type=\"{GridClipboard.EscapeXml(c.Type)}\" null=\"true\" />\n");
                else
                    sb.Append($"      <col name=\"{GridClipboard.EscapeXml(c.Name)}\" type=\"{GridClipboard.EscapeXml(c.Type)}\">")
                      .Append(GridClipboard.EscapeXml(value)).Append("</col>\n");
            }
            sb.Append("    </row>\n");
        }
        sb.Append("  </rows>\n");
        sb.Append("</result>\n");
        return sb.ToString();
    }

    public static byte[] Xlsx(IReadOnlyList<ColumnInfo> columns, IReadOnlyList<string?[]> rows,
        string? sheetName = null)
    {
        var name = sheetName ?? L10n.Tr("Результат");
        foreach (var invalid in "[]:*?/\\") name = name.Replace(invalid.ToString(), "");
        if (name.Length == 0) name = "Sheet1";
        name = name[..Math.Min(31, name.Length)];
        name = GridClipboard.EscapeXml(name);

        var contentTypes = """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
  <Default Extension="xml" ContentType="application/xml"/>
  <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
  <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
  <Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>
</Types>
""";

        var rootRels = """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
</Relationships>
""";

        var workbook = $"""
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
  <sheets><sheet name="{name}" sheetId="1" r:id="rId1"/></sheets>
</workbook>
""";

        var workbookRels = """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
  <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
</Relationships>
""";

        var styles = """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
  <fonts count="2">
    <font><sz val="11"/><name val="Calibri"/></font>
    <font><b/><sz val="11"/><name val="Calibri"/></font>
  </fonts>
  <fills count="2">
    <fill><patternFill patternType="none"/></fill>
    <fill><patternFill patternType="gray125"/></fill>
  </fills>
  <borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders>
  <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
  <cellXfs count="2">
    <xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>
    <xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1"/>
  </cellXfs>
  <cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>
</styleSheet>
""";

        var sheet = SheetXml(columns, rows);

        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "[Content_Types].xml", contentTypes);
            Add(zip, "_rels/.rels", rootRels);
            Add(zip, "xl/workbook.xml", workbook);
            Add(zip, "xl/_rels/workbook.xml.rels", workbookRels);
            Add(zip, "xl/styles.xml", styles);
            Add(zip, "xl/worksheets/sheet1.xml", sheet);
        }
        return stream.ToArray();
    }

    private static void Add(ZipArchive zip, string name, string text)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    private static string SheetXml(IReadOnlyList<ColumnInfo> columns, IReadOnlyList<string?[]> rows)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n");
        sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">\n");
        sb.Append("<sheetViews><sheetView workbookViewId=\"0\">");
        sb.Append("<pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/>");
        sb.Append("</sheetView></sheetViews>\n");

        var widths = columns.Select(c => Math.Max(8, Math.Min(60, c.Name.Length + 2))).ToArray();
        foreach (var row in rows.Take(200))
        {
            for (var i = 0; i < row.Length && i < widths.Length; i++)
            {
                if (row[i] is null) continue;
                widths[i] = Math.Max(widths[i], Math.Min(60, row[i]!.Length + 2));
            }
        }
        if (widths.Length > 0)
        {
            sb.Append("<cols>\n");
            for (var i = 0; i < widths.Length; i++)
                sb.Append($"<col min=\"{i + 1}\" max=\"{i + 1}\" width=\"{widths[i]}\" customWidth=\"1\"/>\n");
            sb.Append("</cols>\n");
        }

        sb.Append("<sheetData>\n");
        if (columns.Count > 0)
        {
            sb.Append("<row r=\"1\">");
            for (var i = 0; i < columns.Count; i++)
                sb.Append(CellXml($"{ColumnName(i)}1", columns[i].Name, 1));
            sb.Append("</row>\n");
        }
        for (var r = 0; r < rows.Count; r++)
        {
            sb.Append($"<row r=\"{r + 2}\">");
            for (var i = 0; i < columns.Count; i++)
            {
                var value = i < rows[r].Length ? rows[r][i] : null;
                sb.Append(CellXml($"{ColumnName(i)}{r + 2}", value, null));
            }
            sb.Append("</row>\n");
        }
        sb.Append("</sheetData>\n</worksheet>\n");
        return sb.ToString();
    }

    private static string CellXml(string reference, string? value, int? style)
    {
        if (value is null) return $"<c r=\"{reference}\"/>";
        var text = value.Length > 32767 ? value[..32767] : value;
        var clean = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c < 0x20 && c is not '\t' and not '\n' and not '\r') continue;
            clean.Append(c);
        }
        var styleAttribute = style is null ? "" : $" s=\"{style}\"";
        return $"<c r=\"{reference}\"{styleAttribute} t=\"inlineStr\"><is><t xml:space=\"preserve\">"
               + GridClipboard.EscapeXml(clean.ToString()) + "</t></is></c>";
    }

    private static string ColumnName(int index)
    {
        var n = index + 1;
        var name = "";
        while (n > 0)
        {
            var rem = (n - 1) % 26;
            name = (char)('A' + rem) + name;
            n = (n - 1) / 26;
        }
        return name;
    }

    public static string SuggestedFileName(string queryName, string extension)
    {
        var stamp = DateTime.Now.ToString("yyyy-MM-ddTHH-mm-ss");
        var safe = string.IsNullOrWhiteSpace(queryName) ? "query" : queryName.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars()) safe = safe.Replace(invalid, '_');
        return $"{safe}-{stamp}.{extension}";
    }
}
