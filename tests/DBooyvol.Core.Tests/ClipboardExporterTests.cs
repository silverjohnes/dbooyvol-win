using System.IO.Compression;
using System.Text;
using DBooyvol.Core.Data;
using DBooyvol.Core.Models;
using Xunit;

namespace DBooyvol.Core.Tests;

public class GridClipboardTests
{
    private static GridClipboardPayload Payload() => new()
    {
        Columns = { "id", "name" },
        ColumnInfo =
        {
            new ColumnInfo { Name = "id", Type = "int4" },
            new ColumnInfo { Name = "name", Type = "text" }
        },
        Rows =
        {
            new string?[] { "1", "Ann" },
            new string?[] { "2", null },
            new string?[] { "3", "a\tb" }
        },
        RowNumbers = { 1, 2, 3 }
    };

    [Fact]
    public void TsvQuotesOnlyWhenNeeded()
    {
        var text = GridClipboard.Text(Payload(), new GridCopySettings());
        Assert.Equal("1\tAnn\n2\t\n3\t\"a\tb\"", text);
    }

    [Fact]
    public void HeaderAndRowNumbers()
    {
        var text = GridClipboard.Text(Payload(), new GridCopySettings
        {
            CopyHeader = true,
            CopyRowNumbers = true,
            QuoteValues = false
        });
        Assert.Equal("\tid\tname\n1\t1\tAnn\n2\t2\t\n3\t3\ta\tb", text);
    }

    [Fact]
    public void JsonFormatUsesNull()
    {
        var json = GridClipboard.Formatted(GridClipboardFormat.Json, Payload(), "t");
        Assert.Contains("\"id\": \"1\"", json);
        Assert.Contains("\"name\": null", json);
    }

    [Fact]
    public void MarkdownEscapesPipes()
    {
        var payload = new GridClipboardPayload
        {
            Columns = { "a" },
            Rows = { new string?[] { "x|y" } }
        };
        var md = GridClipboard.Formatted(GridClipboardFormat.Markdown, payload, "t");
        Assert.Contains("x\\|y", md);
    }

    [Fact]
    public void SqlFormatQuotesAndEscapes()
    {
        var payload = new GridClipboardPayload
        {
            Columns = { "name" },
            Rows = { new string?[] { "O'Brien" } }
        };
        var sql = GridClipboard.Formatted(GridClipboardFormat.Sql, payload, "users");
        Assert.Equal("INSERT INTO users (name) VALUES ('O''Brien');", sql);
    }

    [Fact]
    public void XmlFormatMarksNull()
    {
        var xml = GridClipboard.Formatted(GridClipboardFormat.Xml, Payload(), "t");
        Assert.Contains("<name null=\"true\" />", xml);
        Assert.Contains("<name>Ann</name>", xml);
    }

    [Fact]
    public void NativeFormatConvertsBooleanAndBinary()
    {
        var payload = new GridClipboardPayload
        {
            Columns = { "flag", "data" },
            ColumnInfo =
            {
                new ColumnInfo { Name = "flag", Type = "bool" },
                new ColumnInfo { Name = "data", Type = "bytea", ClassName = "[B" }
            },
            Rows = { new string?[] { "true", Convert.ToBase64String(new byte[] { 1, 2 }) } }
        };
        var native = GridClipboard.Formatted(GridClipboardFormat.Txt, payload, "t", GridValueFormat.DatabaseNative);
        Assert.Contains("t", native);
        Assert.Contains("\\x0102", native);
    }

    [Fact]
    public void PasteParsesQuotedFieldsAndNulls()
    {
        var rows = GridClipboard.Parse("1\t\"a\tb\"\n2\tNULL", new GridPasteSettings(), new GridCopySettings());
        Assert.Equal(2, rows.Count);
        Assert.Equal(new string?[] { "1", "a\tb" }, rows[0]);
        Assert.Equal(new string?[] { "2", null }, rows[1]);
    }

    [Fact]
    public void PasteHandlesDoubledQuotes()
    {
        var rows = GridClipboard.Parse("\"a\"\"b\"", new GridPasteSettings(), new GridCopySettings());
        Assert.Single(rows);
        Assert.Equal("a\"b", rows[0][0]);
    }

    [Fact]
    public void DelimiterDisplayRoundTrip()
    {
        Assert.Equal("\\t", GridClipboard.DisplayDelimiter("\t"));
        Assert.Equal("\t", GridClipboard.DelimiterFromDisplay("\\t"));
        Assert.Equal("\\", GridClipboard.DelimiterFromDisplay("\\\\"));
    }
}

public class ExporterTests
{
    private static List<ColumnInfo> Columns() => new()
    {
        new ColumnInfo { Name = "id", Type = "int4" },
        new ColumnInfo { Name = "name", Type = "text" }
    };

    private static List<string?[]> Rows() => new()
    {
        new string?[] { "00123", "a,b" },
        new string?[] { "2", null }
    };

    [Fact]
    public void CsvUsesCrlfAndRfc4180Quoting()
    {
        var csv = Exporter.Csv(Columns(), Rows());
        Assert.Equal("id,name\r\n00123,\"a,b\"\r\n2,\r\n", csv);
    }

    [Fact]
    public void CsvCustomDelimiterAndNullValue()
    {
        var csv = Exporter.Csv(Columns(), Rows(), ";", "NULL");
        Assert.Equal("id;name\r\n00123;a,b\r\n2;NULL\r\n", csv);
    }

    [Fact]
    public void XmlHasColumnsAndNullAttribute()
    {
        var xml = Exporter.Xml(Columns(), Rows());
        Assert.Contains("<result rows=\"2\">", xml);
        Assert.Contains("null=\"true\"", xml);
        Assert.Contains(">a,b</col>", xml);
    }

    [Fact]
    public void XlsxIsValidZipWithInlineStrings()
    {
        var bytes = Exporter.Xlsx(Columns(), Rows(), "Result");
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        Assert.NotNull(zip.GetEntry("[Content_Types].xml"));
        var sheet = zip.GetEntry("xl/worksheets/sheet1.xml")!;
        using var reader = new StreamReader(sheet.Open(), Encoding.UTF8);
        var xml = reader.ReadToEnd();
        Assert.Contains("t=\"inlineStr\"", xml);
        Assert.Contains(">00123<", xml);
        Assert.Contains("xml:space=\"preserve\"", xml);
    }

    [Fact]
    public void XlsxSheetNameIsSanitized()
    {
        var bytes = Exporter.Xlsx(Columns(), Rows(), "a/b:c*?");
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var workbook = zip.GetEntry("xl/workbook.xml")!;
        using var reader = new StreamReader(workbook.Open(), Encoding.UTF8);
        Assert.Contains("name=\"abc\"", reader.ReadToEnd());
    }
}
