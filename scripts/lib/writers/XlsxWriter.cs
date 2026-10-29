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
//   int red = b.Dxf("9C0006", "FFC7CE", false); s.Conditional("D2:D2000", "AND(D2<>\"\",NOT(ISNUMBER(D2)))", red);
//   s.Validate("D2:D2000", new XlsxValidation { Type = "decimal", PromptTitle = "DX", Prompt = "length along X" });
//   s.Note(0, 3, "the element's length along X"); s.SetFormula(1, 5, "SUM(D2:D9)", XlsxBook.Plain);
//   b.Save(path);
//
// What it writes: numbers (round-trip precision), inline strings (xml:space="preserve"; text that would read as
// an OOXML "_xHHHH_" escape is escaped itself), formulas (with no saved value, or with a number or text saved as
// their value, so a reader that does not calculate sees it; the workbook asks for a full calculation on load), styles (a few fixed ones -- Header, ReadOnly, Note, Bold, Wrap -- and any others built
// with Style(XlsxStyle)), column widths, row heights, merged cells, frozen panes, collapsed column groups,
// auto-filters, data validations (lists, custom rules, input hints), conditional formatting (formula rules),
// cell notes (comments), links to places in the workbook, scatter charts, and hidden / veryHidden sheets.
// No document properties are written (no author, no company), and every zip entry carries the same fixed
// time, so the same content always gives the same bytes. Colours are "RRGGBB".

public sealed class XlsxBookCell
{
    public string Text = "";                  // "" an empty cell; null: a number (or a formula)
    public double Number;
    public string Formula;                    // without the "="; null: a value
    public bool Saved;                        // a formula with a saved value: Number, or Text when Text is not null
    public int Style;
}

// a cell style; Style() gives its index (the same style twice gives the same index)
public sealed class XlsxStyle
{
    public bool Bold, Italic, Underline;
    public string Color;                      // font colour
    public double Size;                       // points (0 = 11)
    public string Fill;                       // solid fill (null = none)
    public string Border;                     // thin border all round (null = none)
    public string Bottom;                     // a bottom rule only (null = none; ignored with Border)
    public bool BottomMedium;
    public string Align, VAlign;              // left | center | right; top | center | bottom
    public bool Wrap;
    public int Indent;
    public string Format;                     // number format code (null = General)

    internal string FontXml()
    {
        return "<font>" + (Bold ? "<b/>" : "") + (Italic ? "<i/>" : "") + (Underline ? "<u/>" : "") +
               "<sz val=\"" + (Size > 0 ? Size : 11).ToString("0.##", CultureInfo.InvariantCulture) + "\"/>" +
               (Color != null ? "<color rgb=\"FF" + Color + "\"/>" : "") + "<name val=\"Calibri\"/><family val=\"2\"/></font>";
    }

    internal string FillXml()
    {
        return Fill == null ? null : "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF" + Fill + "\"/><bgColor indexed=\"64\"/></patternFill></fill>";
    }

    internal string BorderXml()
    {
        if (Border != null)
        {
            string c = "<color rgb=\"FF" + Border + "\"/>";
            return "<border><left style=\"thin\">" + c + "</left><right style=\"thin\">" + c + "</right><top style=\"thin\">" + c + "</top><bottom style=\"thin\">" + c + "</bottom><diagonal/></border>";
        }
        if (Bottom != null) return "<border><left/><right/><top/><bottom style=\"" + (BottomMedium ? "medium" : "thin") + "\"><color rgb=\"FF" + Bottom + "\"/></bottom><diagonal/></border>";
        return null;
    }

    internal string AlignXml()
    {
        if (Align == null && VAlign == null && !Wrap && Indent == 0) return null;
        return "<alignment" + (Align != null ? " horizontal=\"" + Align + "\"" : "") + (VAlign != null ? " vertical=\"" + VAlign + "\"" : "") +
               (Wrap ? " wrapText=\"1\"" : "") + (Indent > 0 ? " indent=\"" + Indent + "\"" : "") + "/>";
    }
}

// a data validation: a list (a dropdown), a rule, or just an input hint (Type "none")
public sealed class XlsxValidation
{
    public string Type = "none";              // none | list | custom | decimal | whole | textLength
    public string Operator;                   // decimal / whole: between, greaterThan, greaterThanOrEqual, ...
    public string Formula1, Formula2;         // without "="; a list: a range or "\"a,b,c\""
    public string ErrorStyle = "warning";     // stop | warning | information
    public bool ShowError = true;             // false: a list offers its values but takes anything
    public string PromptTitle, Prompt;        // the hint shown when a cell is selected (title 32, text 255 chars)
    public string ErrorTitle, Error;          // the message when a value breaks the rule (title 32, text 225 chars)
}

public sealed class XlsxChartSeries
{
    public string Name = "";
    public string X, Y;                       // ranges, e.g. "'Plot pts'!$A$2:$A$700"
    public string Line;                       // line colour (null: no line)
    public double LineWidth = 1.5;            // points
    public bool Dashed;
    public string Marker = "none";            // none | circle | square | triangle | diamond | x | plus | dash | dot
    public int MarkerSize = 7;
    public string MarkerFill, MarkerLine;
    public string Labels;                     // a range whose text labels each point (Excel 2013 and later)
    public string LabelColor = "404040";
    public double LabelSize = 8;
    public bool InLegend = true;
}

// an XY scatter chart on a sheet: one oneCellAnchor at (Col, Row), Width x Height (cm)
public sealed class XlsxChart
{
    public int Col, Row;
    public double WidthCm = 16, HeightCm = 16;
    public string Title;
    public bool Axes;                         // false: axes deleted (no ticks, no labels), scaling kept
    public bool Legend = true;
    public double PlotX = double.NaN, PlotY, PlotW, PlotH;   // the inner plot area (fractions of the chart); NaN = automatic
    public readonly List<XlsxChartSeries> Series = new List<XlsxChartSeries>();

    static string Cm(double cm) { return ((long)Math.Round(cm * 360000)).ToString(CultureInfo.InvariantCulture); }
    static string D(double v) { return v.ToString("0.####", CultureInfo.InvariantCulture); }

    static string Text(string text, double size, bool bold, string color)
    {
        return "<c:tx><c:rich><a:bodyPr/><a:lstStyle/><a:p><a:pPr><a:defRPr sz=\"" + (int)(size * 100) + "\" b=\"" + (bold ? 1 : 0) + "\"/></a:pPr><a:r><a:rPr lang=\"en-US\" sz=\"" +
               (int)(size * 100) + "\" b=\"" + (bold ? 1 : 0) + "\"><a:solidFill><a:srgbClr val=\"" + color + "\"/></a:solidFill></a:rPr><a:t>" + XlsxBook.Escape(text) + "</a:t></a:r></a:p></c:rich></c:tx>";
    }

    internal string DrawingXml(int chartRel, int id)
    {
        return "<xdr:oneCellAnchor><xdr:from><xdr:col>" + Col + "</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>" + Row + "</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:from>" +
               "<xdr:ext cx=\"" + Cm(WidthCm) + "\" cy=\"" + Cm(HeightCm) + "\"/><xdr:graphicFrame macro=\"\"><xdr:nvGraphicFramePr><xdr:cNvPr id=\"" + id + "\" name=\"Chart " + id + "\"/>" +
               "<xdr:cNvGraphicFramePr/></xdr:nvGraphicFramePr><xdr:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/></xdr:xfrm><a:graphic>" +
               "<a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/chart\"><c:chart xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\" r:id=\"rId" + chartRel + "\"/>" +
               "</a:graphicData></a:graphic></xdr:graphicFrame><xdr:clientData/></xdr:oneCellAnchor>";
    }

    internal string Xml()
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n<c:chartSpace xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\" " +
                  "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">");
        sb.Append("<c:roundedCorners val=\"0\"/><c:chart>");
        if (Title != null) sb.Append("<c:title>" + Text(Title, 12, true, "1F2937") + "<c:overlay val=\"0\"/></c:title><c:autoTitleDeleted val=\"0\"/>");
        else sb.Append("<c:autoTitleDeleted val=\"1\"/>");
        sb.Append("<c:plotArea>");
        if (!double.IsNaN(PlotX))
            sb.Append("<c:layout><c:manualLayout><c:layoutTarget val=\"inner\"/><c:xMode val=\"edge\"/><c:yMode val=\"edge\"/><c:x val=\"" + D(PlotX) + "\"/><c:y val=\"" + D(PlotY) +
                      "\"/><c:w val=\"" + D(PlotW) + "\"/><c:h val=\"" + D(PlotH) + "\"/></c:manualLayout></c:layout>");
        else sb.Append("<c:layout/>");
        sb.Append("<c:scatterChart><c:scatterStyle val=\"lineMarker\"/><c:varyColors val=\"0\"/>");
        for (int i = 0; i < Series.Count; i++)
        {
            var s = Series[i];
            sb.Append("<c:ser><c:idx val=\"" + i + "\"/><c:order val=\"" + i + "\"/><c:tx><c:v>" + XlsxBook.Escape(s.Name) + "</c:v></c:tx>");
            sb.Append("<c:spPr>" + (s.Line != null
                ? "<a:ln w=\"" + (long)Math.Round(s.LineWidth * 12700) + "\" cap=\"rnd\"><a:solidFill><a:srgbClr val=\"" + s.Line + "\"/></a:solidFill>" + (s.Dashed ? "<a:prstDash val=\"dash\"/>" : "") + "<a:round/></a:ln>"
                : "<a:ln><a:noFill/></a:ln>") + "</c:spPr>");
            sb.Append("<c:marker><c:symbol val=\"" + s.Marker + "\"/>");
            if (s.Marker != "none")
            {
                sb.Append("<c:size val=\"" + Math.Max(2, Math.Min(72, s.MarkerSize)) + "\"/><c:spPr>" + (s.MarkerFill != null ? "<a:solidFill><a:srgbClr val=\"" + s.MarkerFill + "\"/></a:solidFill>" : "<a:noFill/>") +
                          "<a:ln w=\"9525\">" + (s.MarkerLine != null ? "<a:solidFill><a:srgbClr val=\"" + s.MarkerLine + "\"/></a:solidFill>" : "<a:noFill/>") + "</a:ln></c:spPr>");
            }
            sb.Append("</c:marker>");
            if (s.Labels != null)
            {
                int sz = (int)(s.LabelSize * 100);
                sb.Append("<c:dLbls><c:spPr><a:noFill/><a:ln><a:noFill/></a:ln></c:spPr><c:txPr><a:bodyPr/><a:lstStyle/><a:p><a:pPr><a:defRPr sz=\"" + sz + "\"><a:solidFill><a:srgbClr val=\"" + s.LabelColor +
                          "\"/></a:solidFill></a:defRPr></a:pPr><a:endParaRPr lang=\"en-US\"/></a:p></c:txPr><c:dLblPos val=\"r\"/><c:showLegendKey val=\"0\"/><c:showVal val=\"0\"/><c:showCatName val=\"0\"/>" +
                          "<c:showSerName val=\"0\"/><c:showPercent val=\"0\"/><c:showBubbleSize val=\"0\"/><c:extLst><c:ext uri=\"{CE6537A1-D6FC-4f65-9D91-7224C49458BB}\" " +
                          "xmlns:c15=\"http://schemas.microsoft.com/office/drawing/2012/chart\"><c15:showDataLabelsRange val=\"1\"/></c:ext></c:extLst></c:dLbls>");
            }
            sb.Append("<c:xVal><c:numRef><c:f>" + XlsxBook.Escape(s.X) + "</c:f></c:numRef></c:xVal><c:yVal><c:numRef><c:f>" + XlsxBook.Escape(s.Y) + "</c:f></c:numRef></c:yVal><c:smooth val=\"0\"/>");
            if (s.Labels != null)
                sb.Append("<c:extLst><c:ext uri=\"{02D57815-91ED-43cb-92C2-25804820EDAC}\" xmlns:c15=\"http://schemas.microsoft.com/office/drawing/2012/chart\"><c15:datalabelsRange><c15:f>" +
                          XlsxBook.Escape(s.Labels) + "</c15:f></c15:datalabelsRange></c:ext></c:extLst>");
            sb.Append("</c:ser>");
        }
        sb.Append("<c:axId val=\"50010001\"/><c:axId val=\"50010002\"/></c:scatterChart>");
        foreach (var ax in new[] { new[] { "50010001", "b", "50010002" }, new[] { "50010002", "l", "50010001" } })
        {
            sb.Append("<c:valAx><c:axId val=\"" + ax[0] + "\"/><c:scaling><c:orientation val=\"minMax\"/></c:scaling><c:delete val=\"" + (Axes ? 0 : 1) + "\"/><c:axPos val=\"" + ax[1] + "\"/>" +
                      "<c:numFmt formatCode=\"General\" sourceLinked=\"0\"/><c:majorTickMark val=\"none\"/><c:minorTickMark val=\"none\"/><c:tickLblPos val=\"" + (Axes ? "low" : "none") + "\"/>" +
                      "<c:spPr><a:ln><a:noFill/></a:ln></c:spPr><c:crossAx val=\"" + ax[2] + "\"/><c:crosses val=\"autoZero\"/><c:crossBetween val=\"midCat\"/></c:valAx>");
        }
        sb.Append("<c:spPr><a:noFill/><a:ln><a:noFill/></a:ln></c:spPr></c:plotArea>");
        if (Legend)
        {
            sb.Append("<c:legend><c:legendPos val=\"b\"/>");
            for (int i = 0; i < Series.Count; i++) if (!Series[i].InLegend) sb.Append("<c:legendEntry><c:idx val=\"" + i + "\"/><c:delete val=\"1\"/></c:legendEntry>");
            sb.Append("<c:overlay val=\"0\"/><c:txPr><a:bodyPr/><a:lstStyle/><a:p><a:pPr><a:defRPr sz=\"900\"><a:solidFill><a:srgbClr val=\"404040\"/></a:solidFill></a:defRPr></a:pPr>" +
                      "<a:endParaRPr lang=\"en-US\"/></a:p></c:txPr></c:legend>");
        }
        sb.Append("<c:plotVisOnly val=\"1\"/><c:dispBlanksAs val=\"gap\"/></c:chart>");
        sb.Append("<c:spPr><a:solidFill><a:srgbClr val=\"FFFFFF\"/></a:solidFill><a:ln w=\"9525\"><a:solidFill><a:srgbClr val=\"D0D7DE\"/></a:solidFill></a:ln></c:spPr>");
        sb.Append("<c:txPr><a:bodyPr/><a:lstStyle/><a:p><a:pPr><a:defRPr sz=\"900\"/></a:pPr><a:endParaRPr lang=\"en-US\"/></a:p></c:txPr></c:chartSpace>");
        return sb.ToString();
    }
}

public sealed class XlsxBookSheet
{
    public string Name = "";
    public string State = "visible";          // visible | hidden | veryHidden
    public int FreezeRows, FreezeCols;
    public string TabColor;                   // "FF7F7F7F" (ARGB) or "7F7F7F", or null
    public bool ShowGrid = true;
    public int Zoom = 100;
    public string AutoFilter;                 // the filtered range, its first row the headers ("A1:K400")
    public readonly List<XlsxChart> Charts = new List<XlsxChart>();
    readonly SortedDictionary<int, SortedDictionary<int, XlsxBookCell>> rows = new SortedDictionary<int, SortedDictionary<int, XlsxBookCell>>();
    readonly SortedDictionary<int, double> widths = new SortedDictionary<int, double>();
    readonly SortedDictionary<int, double> heights = new SortedDictionary<int, double>();
    readonly List<int[]> groups = new List<int[]>();          // {first, last} 0-based columns, collapsed
    readonly List<KeyValuePair<string, XlsxValidation>> validations = new List<KeyValuePair<string, XlsxValidation>>();
    readonly List<string[]> conditions = new List<string[]>();   // {sqref, formula, dxf, stopIfTrue}
    readonly List<string> merges = new List<string>();
    readonly List<object[]> notes = new List<object[]>();        // {row, col, text}
    readonly List<string[]> links = new List<string[]>();        // {ref, location, tooltip}
    internal bool HasFormula;

    public int RowCount { get { return rows.Count == 0 ? 0 : rows.Keys.Max() + 1; } }
    internal bool HasNotes { get { return notes.Count > 0; } }

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
        c.Text = text ?? ""; c.Formula = null; c.Style = style;
    }

    public void Set(int row, int col, string text) { Set(row, col, text, 0); }

    // a number; NaN / infinity write nothing (an empty cell)
    public void Set(int row, int col, double v, int style)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) { if (style != 0) Set(row, col, "", style); return; }
        var c = Cell(row, col);
        c.Text = null; c.Formula = null; c.Number = v; c.Style = style;
    }

    public void Set(int row, int col, double v) { Set(row, col, v, 0); }

    // a formula, without the "=" (Excel's English function names, comma separators)
    public void SetFormula(int row, int col, string formula, int style)
    {
        var c = Cell(row, col);
        c.Text = null; c.Formula = formula; c.Style = style; c.Saved = false;
        HasFormula = true;
    }

    // a formula with the number it gives saved as its value (read as that value by a reader that does not calculate)
    public void SetFormula(int row, int col, string formula, double value, int style)
    {
        var c = Cell(row, col);
        c.Text = null; c.Formula = formula; c.Number = value; c.Style = style; c.Saved = true;
        HasFormula = true;
    }

    // a formula with the text it gives saved as its value
    public void SetFormulaText(int row, int col, string formula, string value, int style)
    {
        var c = Cell(row, col);
        c.Text = value ?? ""; c.Formula = formula; c.Style = style; c.Saved = true;
        HasFormula = true;
    }

    // a style on a cell, keeping its value (an empty cell gets one)
    public void Style(int row, int col, int style) { Cell(row, col).Style = style; }

    public void Width(int col, double chars) { widths[col] = chars; }
    public void Height(int row, double points) { heights[row] = points; }
    public void Group(int firstCol, int lastCol) { groups.Add(new[] { firstCol, lastCol }); }
    public void Merge(string range) { merges.Add(range); }
    public void List(string sqref, string formula) { Validate(sqref, new XlsxValidation { Type = "list", Formula1 = formula }); }
    public void Validate(string sqref, XlsxValidation v) { validations.Add(new KeyValuePair<string, XlsxValidation>(sqref, v)); }

    // a formula rule: the cells where it is true take the format dxf (XlsxBook.Dxf); the formula is written for the
    // range's top-left cell, as in Excel. Earlier rules win (stopIfTrue).
    public void Conditional(string sqref, string formula, int dxf) { conditions.Add(new[] { sqref, formula, dxf.ToString(CultureInfo.InvariantCulture), "1" }); }

    // a note (comment) on a cell, shown on hover
    public void Note(int row, int col, string text) { notes.Add(new object[] { row, col, text }); }

    // a link from a cell to a place in the workbook ("'Elements'!A1")
    public void Link(int row, int col, string location, string tooltip) { links.Add(new[] { XlsxBook.Ref(row, col), location, tooltip ?? "" }); }

    static string Clip(string s, int n) { return s == null ? null : s.Length <= n ? s : s.Substring(0, n - 3) + "..."; }

    internal string Xml(bool selected, int drawingRel, int vmlRel)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n");
        sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">");
        if (groups.Count > 0 || TabColor != null)
        {
            sb.Append("<sheetPr>");
            if (TabColor != null) sb.Append("<tabColor rgb=\"" + (TabColor.Length == 6 ? "FF" + TabColor : TabColor) + "\"/>");
            if (groups.Count > 0) sb.Append("<outlinePr summaryRight=\"0\"/>");
            sb.Append("</sheetPr>");
        }
        int maxRow = 0, maxCol = 0;
        foreach (var r in rows) { maxRow = Math.Max(maxRow, r.Key); if (r.Value.Count > 0) maxCol = Math.Max(maxCol, r.Value.Keys.Max()); }
        sb.Append("<dimension ref=\"A1" + (rows.Count > 0 ? ":" + XlsxBook.Ref(maxRow, maxCol) : "") + "\"/>");
        sb.Append("<sheetViews><sheetView" + (ShowGrid ? "" : " showGridLines=\"0\"") + (selected ? " tabSelected=\"1\"" : "") + (Zoom != 100 ? " zoomScale=\"" + Zoom + "\" zoomScaleNormal=\"" + Zoom + "\"" : "") + " workbookViewId=\"0\">");
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
        var rowKeys = new SortedSet<int>(rows.Keys);
        foreach (int h in heights.Keys) rowKeys.Add(h);
        foreach (int rk in rowKeys)
        {
            double ht;
            bool hasH = heights.TryGetValue(rk, out ht);
            SortedDictionary<int, XlsxBookCell> cells;
            rows.TryGetValue(rk, out cells);
            sb.Append("<row r=\"" + (rk + 1) + "\"" + (hasH ? " ht=\"" + ht.ToString("0.##", CultureInfo.InvariantCulture) + "\" customHeight=\"1\"" : "") + ">");
            if (cells != null)
                foreach (var kv in cells)
                {
                    var c = kv.Value;
                    string at = " r=\"" + XlsxBook.Ref(rk, kv.Key) + "\"" + (c.Style != 0 ? " s=\"" + c.Style + "\"" : "");
                    if (c.Formula != null && c.Saved && c.Text != null)
                        sb.Append("<c" + at + " t=\"str\"><f>" + XlsxBook.Escape(c.Formula) + "</f><v>" + XlsxBook.Escape(c.Text) + "</v></c>");
                    else if (c.Formula != null && c.Saved)
                        sb.Append("<c" + at + "><f>" + XlsxBook.Escape(c.Formula) + "</f><v>" + c.Number.ToString("R", CultureInfo.InvariantCulture) + "</v></c>");
                    else if (c.Formula != null) sb.Append("<c" + at + "><f>" + XlsxBook.Escape(c.Formula) + "</f></c>");
                    else if (c.Text == null) sb.Append("<c" + at + "><v>" + c.Number.ToString("R", CultureInfo.InvariantCulture) + "</v></c>");
                    else if (c.Text.Length == 0) sb.Append("<c" + at + "/>");
                    else sb.Append("<c" + at + " t=\"inlineStr\"><is><t xml:space=\"preserve\">" + XlsxBook.Escape(c.Text) + "</t></is></c>");
                }
            sb.Append("</row>");
        }
        sb.Append("</sheetData>");
        if (AutoFilter != null) sb.Append("<autoFilter ref=\"" + AutoFilter + "\"/>");
        if (merges.Count > 0)
        {
            sb.Append("<mergeCells count=\"" + merges.Count + "\">");
            foreach (string m in merges) sb.Append("<mergeCell ref=\"" + m + "\"/>");
            sb.Append("</mergeCells>");
        }
        int priority = 1;
        foreach (var cf in conditions)
            sb.Append("<conditionalFormatting sqref=\"" + cf[0] + "\"><cfRule type=\"expression\" dxfId=\"" + cf[2] + "\" priority=\"" + (priority++) + "\"" + (cf[3] == "1" ? " stopIfTrue=\"1\"" : "") +
                      "><formula>" + XlsxBook.Escape(cf[1]) + "</formula></cfRule></conditionalFormatting>");
        if (validations.Count > 0)
        {
            sb.Append("<dataValidations count=\"" + validations.Count + "\">");
            foreach (var kv in validations)
            {
                var v = kv.Value;
                bool hint = v.Prompt != null;
                sb.Append("<dataValidation" + (v.Type != "none" ? " type=\"" + v.Type + "\"" : "") + (v.Type != "none" ? " errorStyle=\"" + v.ErrorStyle + "\"" : "") +
                          (v.Operator != null ? " operator=\"" + v.Operator + "\"" : "") + " allowBlank=\"1\"" + (hint ? " showInputMessage=\"1\"" : "") +
                          (v.Type != "none" && v.ShowError ? " showErrorMessage=\"1\"" : "") +
                          (v.ErrorTitle != null ? " errorTitle=\"" + XlsxBook.Escape(Clip(v.ErrorTitle, 32)) + "\"" : "") + (v.Error != null ? " error=\"" + XlsxBook.Escape(Clip(v.Error, 225)) + "\"" : "") +
                          (v.PromptTitle != null ? " promptTitle=\"" + XlsxBook.Escape(Clip(v.PromptTitle, 32)) + "\"" : "") + (hint ? " prompt=\"" + XlsxBook.Escape(Clip(v.Prompt, 255)) + "\"" : "") +
                          " sqref=\"" + kv.Key + "\">");
                if (v.Formula1 != null) sb.Append("<formula1>" + XlsxBook.Escape(v.Formula1) + "</formula1>");
                if (v.Formula2 != null) sb.Append("<formula2>" + XlsxBook.Escape(v.Formula2) + "</formula2>");
                sb.Append("</dataValidation>");
            }
            sb.Append("</dataValidations>");
        }
        if (links.Count > 0)
        {
            sb.Append("<hyperlinks>");
            foreach (var l in links) sb.Append("<hyperlink ref=\"" + l[0] + "\" location=\"" + XlsxBook.Escape(l[1]) + "\"" + (l[2].Length > 0 ? " tooltip=\"" + XlsxBook.Escape(Clip(l[2], 255)) + "\"" : "") + "/>");
            sb.Append("</hyperlinks>");
        }
        sb.Append("<pageMargins left=\"0.7\" right=\"0.7\" top=\"0.75\" bottom=\"0.75\" header=\"0.3\" footer=\"0.3\"/>");
        if (drawingRel > 0) sb.Append("<drawing r:id=\"rId" + drawingRel + "\"/>");
        if (vmlRel > 0) sb.Append("<legacyDrawing r:id=\"rId" + vmlRel + "\"/>");
        sb.Append("</worksheet>");
        return sb.ToString();
    }

    internal string CommentsXml()
    {
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n<comments xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><authors><author>Pluto</author></authors><commentList>");
        foreach (var n in notes)
            sb.Append("<comment ref=\"" + XlsxBook.Ref((int)n[0], (int)n[1]) + "\" authorId=\"0\"><text><r><rPr><sz val=\"9\"/><color indexed=\"81\"/><rFont val=\"Tahoma\"/><family val=\"2\"/></rPr>" +
                      "<t xml:space=\"preserve\">" + XlsxBook.Escape((string)n[2]) + "</t></r></text></comment>");
        sb.Append("</commentList></comments>");
        return sb.ToString();
    }

    // the legacy (VML) drawing Excel shows notes with: one hidden text box per note, sized to its text
    internal string VmlXml(int sheetNo)
    {
        var sb = new StringBuilder("<xml xmlns:v=\"urn:schemas-microsoft-com:vml\" xmlns:o=\"urn:schemas-microsoft-com:office:office\" xmlns:x=\"urn:schemas-microsoft-com:office:excel\">");
        sb.Append("<o:shapelayout v:ext=\"edit\"><o:idmap v:ext=\"edit\" data=\"" + sheetNo + "\"/></o:shapelayout>");
        sb.Append("<v:shapetype id=\"_x0000_t202\" coordsize=\"21600,21600\" o:spt=\"202\" path=\"m,l,21600r21600,l21600,xe\"><v:stroke joinstyle=\"miter\"/><v:path gradientshapeok=\"t\" o:connecttype=\"rect\"/></v:shapetype>");
        int k = 0;
        foreach (var n in notes)
        {
            int row = (int)n[0], col = (int)n[1];
            int len = ((string)n[2]).Length;
            int wCols = len > 300 ? 5 : len > 120 ? 4 : 3, hRows = Math.Max(3, Math.Min(16, len / (wCols * 11) + 2));
            sb.Append("<v:shape id=\"_x0000_s" + (sheetNo * 1024 + 1 + k++) + "\" type=\"#_x0000_t202\" style=\"position:absolute;margin-left:80pt;margin-top:2pt;width:" + (wCols * 64) +
                      "pt;height:" + (hRows * 15) + "pt;z-index:" + k + ";visibility:hidden\" fillcolor=\"#ffffe1\" o:insetmode=\"auto\"><v:fill color2=\"#ffffe1\"/>" +
                      "<v:shadow on=\"t\" color=\"black\" obscured=\"t\"/><v:path o:connecttype=\"none\"/><v:textbox style=\"mso-direction-alt:auto\"><div style=\"text-align:left\"></div></v:textbox>" +
                      "<x:ClientData ObjectType=\"Note\"><x:MoveWithCells/><x:SizeWithCells/><x:Anchor>" + (col + 1) + ", 15, " + row + ", 2, " + (col + 1 + wCols) + ", 15, " + (row + hRows) + ", 2</x:Anchor>" +
                      "<x:AutoFill>False</x:AutoFill><x:Row>" + row + "</x:Row><x:Column>" + col + "</x:Column></x:ClientData></v:shape>");
        }
        sb.Append("</xml>");
        return sb.ToString();
    }
}

public sealed class XlsxBook
{
    // cell styles (fixed indices; more with Style())
    public const int Plain = 0, Header = 1, ReadOnly = 2, Note = 3, Bold = 4, Wrap = 5;

    public readonly List<XlsxBookSheet> Sheets = new List<XlsxBookSheet>();
    public readonly SortedDictionary<string, string> Names = new SortedDictionary<string, string>(StringComparer.Ordinal);   // defined names: name -> reference

    readonly List<string> fonts = new List<string>(), fills = new List<string>(), borders = new List<string>(), xfs = new List<string>(), dxfs = new List<string>();
    readonly List<string> numFmts = new List<string>();
    readonly Dictionary<string, int> xfIndex = new Dictionary<string, int>(StringComparer.Ordinal), dxfIndex = new Dictionary<string, int>(StringComparer.Ordinal);

    public XlsxBook()
    {
        fills.Add("<fill><patternFill patternType=\"none\"/></fill>");
        fills.Add("<fill><patternFill patternType=\"gray125\"/></fill>");
        borders.Add("<border><left/><right/><top/><bottom/><diagonal/></border>");
        Style(new XlsxStyle());                                                                 // Plain
        Style(new XlsxStyle { Bold = true, Fill = "D9E1F2", Bottom = "8EA9DB" });              // Header
        Style(new XlsxStyle { Color = "595959", Fill = "F2F2F2" });                            // ReadOnly
        Style(new XlsxStyle { Italic = true, Color = "7F7F7F" });                              // Note
        Style(new XlsxStyle { Bold = true });                                                  // Bold
        Style(new XlsxStyle { VAlign = "top", Wrap = true });                                  // Wrap
    }

    static int IndexOf(List<string> list, string item)
    {
        int i = list.IndexOf(item);
        if (i < 0) { list.Add(item); i = list.Count - 1; }
        return i;
    }

    public int Style(XlsxStyle s)
    {
        int font = IndexOf(fonts, s.FontXml());
        string fx = s.FillXml(), bx = s.BorderXml(), ax = s.AlignXml();
        int fill = fx == null ? 0 : IndexOf(fills, fx), border = bx == null ? 0 : IndexOf(borders, bx);
        int fmt = 0;
        if (s.Format != null) fmt = 164 + IndexOf(numFmts, s.Format);
        string key = font + "|" + fill + "|" + border + "|" + fmt + "|" + ax;
        int x;
        if (xfIndex.TryGetValue(key, out x)) return x;
        xfs.Add("<xf numFmtId=\"" + fmt + "\" fontId=\"" + font + "\" fillId=\"" + fill + "\" borderId=\"" + border + "\" xfId=\"0\"" + (fmt != 0 ? " applyNumberFormat=\"1\"" : "") +
                (font != 0 ? " applyFont=\"1\"" : "") + (fill != 0 ? " applyFill=\"1\"" : "") + (border != 0 ? " applyBorder=\"1\"" : "") +
                (ax != null ? " applyAlignment=\"1\">" + ax + "</xf>" : "/>"));
        xfIndex[key] = xfs.Count - 1;
        return xfs.Count - 1;
    }

    // a conditional format: font colour, fill (either may be null), bold
    public int Dxf(string fontColor, string fill, bool bold)
    {
        string x = "<dxf>" + (fontColor != null || bold ? "<font>" + (bold ? "<b/>" : "") + (fontColor != null ? "<color rgb=\"FF" + fontColor + "\"/>" : "") + "</font>" : "") +
                   (fill != null ? "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF" + fill + "\"/><bgColor rgb=\"FF" + fill + "\"/></patternFill></fill>" : "") + "</dxf>";
        int i;
        if (dxfIndex.TryGetValue(x, out i)) return i;
        dxfs.Add(x);
        dxfIndex[x] = dxfs.Count - 1;
        return dxfs.Count - 1;
    }

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

    // a sheet name as a formula writes it: quoted when it needs to be
    public static string SheetRef(string name)
    {
        bool plain = name.Length > 0 && (char.IsLetter(name[0]) || name[0] == '_') && name.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '.') && !LooksLikeRef(name);
        return plain ? name : "'" + name.Replace("'", "''") + "'";
    }

    static bool LooksLikeRef(string n)
    {
        int i = 0;
        while (i < n.Length && char.IsLetter(n[i])) i++;
        return i > 0 && i <= 3 && i < n.Length && n.Skip(i).All(char.IsDigit);
    }

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
                if (Sheets.Any(s => s.HasNotes)) ct.Append("<Default Extension=\"vml\" ContentType=\"application/vnd.openxmlformats-officedocument.vmlDrawing\"/>");
                ct.Append("<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>");
                Put(zip, "_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                    "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
                var wb = new StringBuilder();
                wb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">");
                wb.Append("<bookViews><workbookView activeTab=\"0\"/></bookViews><sheets>");
                var rels = new StringBuilder();
                rels.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
                int chartNo = 0, drawingNo = 0, commentNo = 0;
                bool anyFormula = false;
                var names = new List<string>();
                for (int i = 0; i < Sheets.Count; i++)
                {
                    var s = Sheets[i];
                    anyFormula |= s.HasFormula;
                    wb.Append("<sheet name=\"" + Escape(s.Name) + "\" sheetId=\"" + (i + 1) + "\"" + (s.State != "visible" ? " state=\"" + s.State + "\"" : "") + " r:id=\"rId" + (i + 1) + "\"/>");
                    rels.Append("<Relationship Id=\"rId" + (i + 1) + "\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet" + (i + 1) + ".xml\"/>");
                    ct.Append("<Override PartName=\"/xl/worksheets/sheet" + (i + 1) + ".xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
                    if (s.AutoFilter != null)
                        names.Add("<definedName name=\"_xlnm._FilterDatabase\" localSheetId=\"" + i + "\" hidden=\"1\">" + Escape(SheetRef(s.Name) + "!" + Abs(s.AutoFilter)) + "</definedName>");
                    // the sheet's own parts: a drawing with its charts, notes (comments + their VML drawing)
                    var srels = new StringBuilder();
                    int drawingRel = 0, vmlRel = 0, next = 1;
                    if (s.Charts.Count > 0)
                    {
                        drawingNo++;
                        drawingRel = next++;
                        srels.Append("<Relationship Id=\"rId" + drawingRel + "\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/drawing\" Target=\"../drawings/drawing" + drawingNo + ".xml\"/>");
                        var dr = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n<xdr:wsDr xmlns:xdr=\"http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing\" " +
                                                   "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">");
                        var drels = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
                        for (int k = 0; k < s.Charts.Count; k++)
                        {
                            chartNo++;
                            dr.Append(s.Charts[k].DrawingXml(k + 1, k + 2));
                            drels.Append("<Relationship Id=\"rId" + (k + 1) + "\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/chart\" Target=\"../charts/chart" + chartNo + ".xml\"/>");
                            Put(zip, "xl/charts/chart" + chartNo + ".xml", s.Charts[k].Xml());
                            ct.Append("<Override PartName=\"/xl/charts/chart" + chartNo + ".xml\" ContentType=\"application/vnd.openxmlformats-officedocument.drawingml.chart+xml\"/>");
                        }
                        dr.Append("</xdr:wsDr>");
                        drels.Append("</Relationships>");
                        Put(zip, "xl/drawings/drawing" + drawingNo + ".xml", dr.ToString());
                        Put(zip, "xl/drawings/_rels/drawing" + drawingNo + ".xml.rels", drels.ToString());
                        ct.Append("<Override PartName=\"/xl/drawings/drawing" + drawingNo + ".xml\" ContentType=\"application/vnd.openxmlformats-officedocument.drawing+xml\"/>");
                    }
                    if (s.HasNotes)
                    {
                        commentNo++;
                        vmlRel = next++;
                        int cRel = next++;
                        srels.Append("<Relationship Id=\"rId" + vmlRel + "\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/vmlDrawing\" Target=\"../drawings/vmlDrawing" + commentNo + ".vml\"/>");
                        srels.Append("<Relationship Id=\"rId" + cRel + "\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/comments\" Target=\"../comments" + commentNo + ".xml\"/>");
                        Put(zip, "xl/drawings/vmlDrawing" + commentNo + ".vml", s.VmlXml(i + 1));
                        Put(zip, "xl/comments" + commentNo + ".xml", s.CommentsXml());
                        ct.Append("<Override PartName=\"/xl/comments" + commentNo + ".xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.comments+xml\"/>");
                    }
                    if (srels.Length > 0)
                        Put(zip, "xl/worksheets/_rels/sheet" + (i + 1) + ".xml.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" + srels + "</Relationships>");
                    Put(zip, "xl/worksheets/sheet" + (i + 1) + ".xml", s.Xml(i == 0, drawingRel, vmlRel));
                }
                wb.Append("</sheets>");
                foreach (var kv in Names) names.Add("<definedName name=\"" + Escape(kv.Key) + "\">" + Escape(kv.Value) + "</definedName>");
                if (names.Count > 0) wb.Append("<definedNames>" + string.Join("", names.ToArray()) + "</definedNames>");
                if (anyFormula) wb.Append("<calcPr calcId=\"191029\" fullCalcOnLoad=\"1\"/>");
                wb.Append("</workbook>");
                rels.Append("<Relationship Id=\"rId" + (Sheets.Count + 1) + "\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>");
                ct.Append("<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/></Types>");
                Put(zip, "[Content_Types].xml", ct.ToString());
                Put(zip, "xl/workbook.xml", wb.ToString());
                Put(zip, "xl/_rels/workbook.xml.rels", rels.ToString());
                Put(zip, "xl/styles.xml", StylesXml());
            }
            return ms.ToArray();
        }
    }

    // "A1:K1" -> "$A$1:$K$1"
    static string Abs(string range)
    {
        return string.Join(":", range.Split(':').Select(p =>
        {
            int i = 0;
            while (i < p.Length && char.IsLetter(p[i])) i++;
            return "$" + p.Substring(0, i) + "$" + p.Substring(i);
        }).ToArray());
    }

    string StylesXml()
    {
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
        if (numFmts.Count > 0)
        {
            sb.Append("<numFmts count=\"" + numFmts.Count + "\">");
            for (int i = 0; i < numFmts.Count; i++) sb.Append("<numFmt numFmtId=\"" + (164 + i) + "\" formatCode=\"" + Escape(numFmts[i]) + "\"/>");
            sb.Append("</numFmts>");
        }
        sb.Append("<fonts count=\"" + fonts.Count + "\">" + string.Join("", fonts.ToArray()) + "</fonts>");
        sb.Append("<fills count=\"" + fills.Count + "\">" + string.Join("", fills.ToArray()) + "</fills>");
        sb.Append("<borders count=\"" + borders.Count + "\">" + string.Join("", borders.ToArray()) + "</borders>");
        sb.Append("<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>");
        sb.Append("<cellXfs count=\"" + xfs.Count + "\">" + string.Join("", xfs.ToArray()) + "</cellXfs>");
        sb.Append("<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>");
        sb.Append("<dxfs count=\"" + dxfs.Count + "\">" + string.Join("", dxfs.ToArray()) + "</dxfs>");
        sb.Append("</styleSheet>");
        return sb.ToString();
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
