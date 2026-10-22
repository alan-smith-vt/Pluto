using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

// XlsxBook  --  a small .xlsx (Office Open XML) writer: no Excel, no package. C# 5 / Add-Type (PowerShell 5.1).
//
//   var b = new XlsxBook();
//   var s = b.Add("Elements");            s.Set(0, 0, "Id", XlsxBook.Header); s.Set(1, 0, 12.5);
//   s.FreezeRows = 1; s.Width(0, 10); s.Group(20, 30); s.List("G2:G2000", "Lists!$A$2:$A$9");
//   b.Save(path);
//
// What it writes: numbers (round-trip precision), inline strings (xml:space="preserve"; text that would read as
// an OOXML "_xHHHH_" escape is escaped itself), a few fixed styles (Header, ReadOnly, Note, Bold, Wrap), column
// widths, frozen panes, collapsed column groups (outline level 1), list validations (a warning, not a stop), and
// hidden / veryHidden sheets. No document properties are written (no author, no company), and every zip entry
// carries the same fixed time, so the same content always gives the same bytes.

public sealed class XlsxBookCell
{
    public string Text;                       // null: a number
    public double Number;
    public int Style;
}

public sealed class XlsxBookSheet
{
    public string Name = "";
    public string State = "visible";          // visible | hidden | veryHidden
    public int FreezeRows, FreezeCols;
    public string TabColor;                   // "FF7F7F7F" or null
    readonly SortedDictionary<int, SortedDictionary<int, XlsxBookCell>> rows = new SortedDictionary<int, SortedDictionary<int, XlsxBookCell>>();
    readonly SortedDictionary<int, double> widths = new SortedDictionary<int, double>();
    readonly List<int[]> groups = new List<int[]>();          // {first, last} 0-based columns, collapsed
    readonly List<string[]> lists = new List<string[]>();     // {sqref, formula}

    public int RowCount { get { return rows.Count == 0 ? 0 : rows.Keys.Max() + 1; } }

    XlsxBookCell Cell(int row, int col)
    {
        if (row < 0 || col < 0 || col > 16383 || row > 1048575) throw new ArgumentOutOfRangeException("XlsxBook: cell " + row + "," + col);
        SortedDictionary<int, XlsxBookCell> r;
        if (!rows.TryGetValue(row, out r)) { r = new SortedDictionary<int, XlsxBookCell>(); rows[row] = r; }
        XlsxBookCell c;
        if (!r.TryGetValue(col, out c)) { c = new XlsxBookCell(); r[col] = c; }
        return c;
    }

    // text; null or "" writes nothing unless a style is given
    public void Set(int row, int col, string text, int style)
    {
        if (string.IsNullOrEmpty(text) && style == 0) return;
        var c = Cell(row, col);
        c.Text = text ?? ""; c.Style = style;
    }

    public void Set(int row, int col, string text) { Set(row, col, text, 0); }

    // a number; NaN / infinity write nothing (an empty cell)
    public void Set(int row, int col, double v, int style)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) { if (style != 0) Set(row, col, "", style); return; }
        var c = Cell(row, col);
        c.Text = null; c.Number = v; c.Style = style;
    }

    public void Set(int row, int col, double v) { Set(row, col, v, 0); }

    public void Width(int col, double chars) { widths[col] = chars; }
    public void Group(int firstCol, int lastCol) { groups.Add(new[] { firstCol, lastCol }); }
    public void List(string sqref, string formula) { lists.Add(new[] { sqref, formula }); }

    internal string Xml(bool selected)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n");
        sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">");
        if (groups.Count > 0 || TabColor != null)
        {
            sb.Append("<sheetPr>");
            if (TabColor != null) sb.Append("<tabColor rgb=\"" + TabColor + "\"/>");
            if (groups.Count > 0) sb.Append("<outlinePr summaryRight=\"0\"/>");
            sb.Append("</sheetPr>");
        }
        int maxRow = 0, maxCol = 0;
        foreach (var r in rows) { maxRow = Math.Max(maxRow, r.Key); if (r.Value.Count > 0) maxCol = Math.Max(maxCol, r.Value.Keys.Max()); }
        sb.Append("<dimension ref=\"A1" + (rows.Count > 0 ? ":" + XlsxBook.Ref(maxRow, maxCol) : "") + "\"/>");
        sb.Append("<sheetViews><sheetView workbookViewId=\"0\"" + (selected ? " tabSelected=\"1\"" : "") + ">");
        if (FreezeRows > 0 || FreezeCols > 0)
        {
            string pane = FreezeRows > 0 && FreezeCols > 0 ? "bottomRight" : FreezeRows > 0 ? "bottomLeft" : "topRight";
            sb.Append("<pane" + (FreezeCols > 0 ? " xSplit=\"" + FreezeCols + "\"" : "") + (FreezeRows > 0 ? " ySplit=\"" + FreezeRows + "\"" : "") +
                      " topLeftCell=\"" + XlsxBook.Ref(FreezeRows, FreezeCols) + "\" activePane=\"" + pane + "\" state=\"frozen\"/>");
            sb.Append("<selection pane=\"" + pane + "\"/>");
        }
        sb.Append("</sheetView></sheetViews>");
        sb.Append("<sheetFormatPr defaultRowHeight=\"15\"" + (groups.Count > 0 ? " outlineLevelCol=\"1\"" : "") + "/>");
        // columns: widths, the grouped (hidden, outline level 1) ones, and the collapsed mark on the column left of a group
        var colSet = new SortedSet<int>(widths.Keys);
        foreach (var g in groups) { for (int c = g[0]; c <= g[1]; c++) colSet.Add(c); if (g[0] > 0) colSet.Add(g[0] - 1); }
        if (colSet.Count > 0)
        {
            sb.Append("<cols>");
            foreach (int c in colSet)
            {
                bool inGroup = groups.Any(g => c >= g[0] && c <= g[1]);
                bool before = groups.Any(g => c == g[0] - 1) && !inGroup;
                double w;
                bool hasW = widths.TryGetValue(c, out w);
                sb.Append("<col min=\"" + (c + 1) + "\" max=\"" + (c + 1) + "\" width=\"" + (hasW ? w : 9.14).ToString("0.##", CultureInfo.InvariantCulture) + "\"" +
                          (hasW ? " customWidth=\"1\"" : "") + (inGroup ? " hidden=\"1\" outlineLevel=\"1\"" : "") + (before ? " collapsed=\"1\"" : "") + "/>");
            }
            sb.Append("</cols>");
        }
        sb.Append("<sheetData>");
        foreach (var r in rows)
        {
            sb.Append("<row r=\"" + (r.Key + 1) + "\">");
            foreach (var kv in r.Value)
            {
                var c = kv.Value;
                string at = " r=\"" + XlsxBook.Ref(r.Key, kv.Key) + "\"" + (c.Style != 0 ? " s=\"" + c.Style + "\"" : "");
                if (c.Text == null) sb.Append("<c" + at + "><v>" + c.Number.ToString("R", CultureInfo.InvariantCulture) + "</v></c>");
                else if (c.Text.Length == 0) sb.Append("<c" + at + "/>");
                else sb.Append("<c" + at + " t=\"inlineStr\"><is><t xml:space=\"preserve\">" + XlsxBook.Escape(c.Text) + "</t></is></c>");
            }
            sb.Append("</row>");
        }
        sb.Append("</sheetData>");
        if (lists.Count > 0)
        {
            sb.Append("<dataValidations count=\"" + lists.Count + "\">");
            foreach (var l in lists)
                sb.Append("<dataValidation type=\"list\" errorStyle=\"warning\" allowBlank=\"1\" showErrorMessage=\"1\" sqref=\"" + l[0] + "\"><formula1>" + XlsxBook.Escape(l[1]) + "</formula1></dataValidation>");
            sb.Append("</dataValidations>");
        }
        sb.Append("<pageMargins left=\"0.7\" right=\"0.7\" top=\"0.75\" bottom=\"0.75\" header=\"0.3\" footer=\"0.3\"/>");
        sb.Append("</worksheet>");
        return sb.ToString();
    }
}

public sealed class XlsxBook
{
    // cell styles (fixed)
    public const int Plain = 0, Header = 1, ReadOnly = 2, Note = 3, Bold = 4, Wrap = 5;

    public readonly List<XlsxBookSheet> Sheets = new List<XlsxBookSheet>();

    public XlsxBookSheet Add(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 31 || name.IndexOfAny(new[] { '[', ']', ':', '*', '?', '/', '\\' }) >= 0)
            throw new ArgumentException("XlsxBook: sheet name \"" + name + "\" (1-31 characters, none of [ ] : * ? / \\)");
        if (Sheets.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("XlsxBook: two sheets named \"" + name + "\"");
        var sh = new XlsxBookSheet { Name = name };
        Sheets.Add(sh);
        return sh;
    }

    public void Save(string path) { File.WriteAllBytes(path, ToBytes()); }

    public byte[] ToBytes()
    {
        if (Sheets.Count == 0 || Sheets[0].State != "visible") throw new InvalidOperationException("XlsxBook: the first sheet must be a visible one");
        using (var ms = new MemoryStream())
        {
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            {
                var ct = new StringBuilder();
                ct.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
                ct.Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>");
                ct.Append("<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>");
                for (int i = 0; i < Sheets.Count; i++)
                    ct.Append("<Override PartName=\"/xl/worksheets/sheet" + (i + 1) + ".xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
                ct.Append("<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/></Types>");
                Put(zip, "[Content_Types].xml", ct.ToString());
                Put(zip, "_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                    "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
                var wb = new StringBuilder();
                wb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">");
                wb.Append("<bookViews><workbookView activeTab=\"0\"/></bookViews><sheets>");
                var rels = new StringBuilder();
                rels.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
                for (int i = 0; i < Sheets.Count; i++)
                {
                    var s = Sheets[i];
                    wb.Append("<sheet name=\"" + Escape(s.Name) + "\" sheetId=\"" + (i + 1) + "\"" + (s.State != "visible" ? " state=\"" + s.State + "\"" : "") + " r:id=\"rId" + (i + 1) + "\"/>");
                    rels.Append("<Relationship Id=\"rId" + (i + 1) + "\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet" + (i + 1) + ".xml\"/>");
                    Put(zip, "xl/worksheets/sheet" + (i + 1) + ".xml", s.Xml(i == 0));
                }
                wb.Append("</sheets></workbook>");
                rels.Append("<Relationship Id=\"rId" + (Sheets.Count + 1) + "\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>");
                Put(zip, "xl/workbook.xml", wb.ToString());
                Put(zip, "xl/_rels/workbook.xml.rels", rels.ToString());
                Put(zip, "xl/styles.xml", Styles);
            }
            return ms.ToArray();
        }
    }

    static readonly DateTimeOffset Stamp = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    static void Put(ZipArchive zip, string name, string text)
    {
        var e = zip.CreateEntry(name, CompressionLevel.Optimal);
        e.LastWriteTime = Stamp;
        using (var s = e.Open())
        {
            var b = new UTF8Encoding(false).GetBytes(text);
            s.Write(b, 0, b.Length);
        }
    }

    // a font's parts in the schema's order: b, i, sz, color, name, family
    static string Font(string flags, string color)
    {
        return "<font>" + flags + "<sz val=\"11\"/>" + (color != null ? "<color rgb=\"" + color + "\"/>" : "") + "<name val=\"Calibri\"/><family val=\"2\"/></font>";
    }

    static readonly string Styles =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
        "<fonts count=\"4\">" + Font("", null) + Font("<b/>", null) + Font("", "FF595959") + Font("<i/>", "FF7F7F7F") + "</fonts>" +
        "<fills count=\"4\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill>" +
        "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFD9E1F2\"/><bgColor indexed=\"64\"/></patternFill></fill>" +
        "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFF2F2F2\"/><bgColor indexed=\"64\"/></patternFill></fill></fills>" +
        "<borders count=\"2\"><border><left/><right/><top/><bottom/><diagonal/></border>" +
        "<border><left/><right/><top/><bottom style=\"thin\"><color rgb=\"FF8EA9DB\"/></bottom><diagonal/></border></borders>" +
        "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
        "<cellXfs count=\"6\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
        "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\"/>" +
        "<xf numFmtId=\"0\" fontId=\"2\" fillId=\"3\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\"/>" +
        "<xf numFmtId=\"0\" fontId=\"3\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>" +
        "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>" +
        "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyAlignment=\"1\"><alignment vertical=\"top\" wrapText=\"1\"/></xf></cellXfs>" +
        "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>";

    // A1-style reference of a 0-based row / column
    public static string Ref(int row, int col) { return Column(col) + (row + 1).ToString(CultureInfo.InvariantCulture); }

    public static string Column(int col)
    {
        string s = "";
        int c = col + 1;
        while (c > 0) { int m = (c - 1) % 26; s = (char)('A' + m) + s; c = (c - 1) / 26; }
        return s;
    }

    // XML text: & < > " escaped; characters XML 1.0 cannot hold, and text that reads as an "_xHHHH_" escape,
    // written as OOXML escapes
    public static string Escape(string s)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '_' && i + 6 < s.Length && s[i + 1] == 'x' && IsHex4(s, i + 2) && s[i + 6] == '_') { sb.Append("_x005F_"); continue; }
            if (c == '&') sb.Append("&amp;");
            else if (c == '<') sb.Append("&lt;");
            else if (c == '>') sb.Append("&gt;");
            else if (c == '"') sb.Append("&quot;");
            else if ((c < 0x20 && c != '\t' && c != '\n' && c != '\r') || c == 0xFFFE || c == 0xFFFF) sb.Append("_x" + ((int)c).ToString("X4") + "_");
            else sb.Append(c);
        }
        return sb.ToString();
    }

    static bool IsHex4(string s, int at)
    {
        for (int k = 0; k < 4; k++) { char h = s[at + k]; if (!((h >= '0' && h <= '9') || (h >= 'a' && h <= 'f') || (h >= 'A' && h <= 'F'))) return false; }
        return true;
    }

    // the reverse of Escape's "_xHHHH_" (how Excel stores characters XML cannot hold)
    public static string Unescape(string s)
    {
        if (s == null || s.IndexOf("_x", StringComparison.Ordinal) < 0) return s;
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '_' && i + 6 < s.Length && s[i + 1] == 'x' && IsHex4(s, i + 2) && s[i + 6] == '_')
            {
                sb.Append((char)int.Parse(s.Substring(i + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                i += 6;
                continue;
            }
            sb.Append(s[i]);
        }
        return sb.ToString();
    }
}
