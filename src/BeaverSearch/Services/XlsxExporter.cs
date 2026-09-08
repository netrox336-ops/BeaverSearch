using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using BeaverSearch.Models;

namespace BeaverSearch.Services;

public static class XlsxExporter
{
    public static void Export(string path, IReadOnlyList<PlayerResult> rows)
    {
        if (File.Exists(path)) File.Delete(path);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        Write(archive, "[Content_Types].xml", ContentTypes);
        Write(archive, "_rels/.rels", RootRels);
        Write(archive, "xl/workbook.xml", Workbook);
        Write(archive, "xl/_rels/workbook.xml.rels", WorkbookRels);
        Write(archive, "xl/styles.xml", Styles);
        Write(archive, "xl/worksheets/sheet1.xml", BuildSheet(rows));
    }

    private static string BuildSheet(IReadOnlyList<PlayerResult> rows)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><cols>");
        var widths = new[] { 6, 24, 20, 48, 16, 16, 16, 18, 24, 36, 22, 22, 18 };
        for (var i = 0; i < widths.Length; i++)
            sb.Append($"<col min=\"{i + 1}\" max=\"{i + 1}\" width=\"{widths[i]}\" customWidth=\"1\"/>");
        sb.Append("</cols><sheetData>");

        var headers = new[] { "№", "Ник", "SteamID64", "Steam", "CS2 ₽", "Dota 2 ₽", "Rust ₽", "Итого ₽", "Прошёл фильтр", "Серверы", "Первый раз найден", "Последняя проверка", "Статус" };
        sb.Append("<row r=\"1\">");
        for (var i = 0; i < headers.Length; i++) sb.Append(CellText(i + 1, 1, headers[i], 1));
        sb.Append("</row>");

        for (var r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            var er = r + 2;
            sb.Append($"<row r=\"{er}\">");
            sb.Append(CellNumber(1, er, r + 1));
            sb.Append(CellText(2, er, row.Nickname));
            sb.Append(CellText(3, er, row.SteamId64));
            sb.Append(CellText(4, er, row.SteamUrl));
            sb.Append(CellNumber(5, er, row.Cs2Rub, 2));
            sb.Append(CellNumber(6, er, row.DotaRub, 2));
            sb.Append(CellNumber(7, er, row.RustRub, 2));
            sb.Append(CellNumber(8, er, row.TotalRub, 2));
            sb.Append(CellText(9, er, row.MatchedBy));
            sb.Append(CellText(10, er, row.Servers));
            sb.Append(CellText(11, er, row.FirstSeenUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")));
            sb.Append(CellText(12, er, row.LastCheckedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")));
            sb.Append(CellText(13, er, row.Status));
            sb.Append("</row>");
        }

        sb.Append("</sheetData><autoFilter ref=\"A1:M1\"/>");
        sb.Append("</worksheet>");
        return sb.ToString();
    }

    private static string CellText(int col, int row, string? value, int style = 0) =>
        $"<c r=\"{Column(col)}{row}\" t=\"inlineStr\" s=\"{style}\"><is><t>{Escape(value ?? string.Empty)}</t></is></c>";

    private static string CellNumber(int col, int row, decimal value, int style = 0) =>
        $"<c r=\"{Column(col)}{row}\" s=\"{style}\"><v>{value.ToString(CultureInfo.InvariantCulture)}</v></c>";

    private static string CellNumber(int col, int row, int value, int style = 0) =>
        $"<c r=\"{Column(col)}{row}\" s=\"{style}\"><v>{value}</v></c>";

    private static string Column(int n)
    {
        var s = string.Empty;
        while (n > 0) { n--; s = (char)('A' + n % 26) + s; n /= 26; }
        return s;
    }

    private static string Escape(string s) => SecurityElement.Escape(s) ?? string.Empty;
    private static void Write(ZipArchive archive, string name, string text)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(text);
    }

    private const string ContentTypes = """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
<Default Extension="xml" ContentType="application/xml"/>
<Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
<Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
<Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>
</Types>
""";

    private const string RootRels = """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
</Relationships>
""";

    private const string Workbook = """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
<sheets><sheet name="BeaverSearch Results" sheetId="1" r:id="rId1"/></sheets>
</workbook>
""";

    private const string WorkbookRels = """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
<Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
</Relationships>
""";

    private const string Styles = """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
<fonts count="2"><font><sz val="11"/><name val="Segoe UI"/></font><font><b/><color rgb="FFFFFFFF"/><sz val="11"/><name val="Segoe UI"/></font></fonts>
<fills count="3"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FFD58A43"/><bgColor indexed="64"/></patternFill></fill></fills>
<borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders>
<cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
<cellXfs count="3"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="1" fillId="2" borderId="0" xfId="0" applyFill="1" applyFont="1"/><xf numFmtId="4" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/></cellXfs>
</styleSheet>
""";
}
