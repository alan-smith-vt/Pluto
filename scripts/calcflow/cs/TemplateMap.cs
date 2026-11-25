// TemplateMap.cs -- what a calc template asks for, read from the cells its author coloured as inputs.
//
// No layout is assumed. The cells with an input fill (the yellow family) that hold no formula are grouped into
// blocks of cells that touch: one cell, cells in a row, a list, or a table. A merged cell counts once. Each block's
// columns are named by the headings above them, read across merged heading rows, and a lone cell by the label to its
// left. A block says nothing about what it means: that is one question a column, asked of the engineer, with a first
// answer proposed here:
//   Typed per mark       a value of the connection design, the same wherever the mark is used
//   Typed per beam end   a value that differs from one beam end to the next
//   From the model       the beam number, the node, the load case, a force of the beam end
// A table whose headings read as forces, a beam or a node is taken as rows the tool writes (one a beam end or load
// case); any other table as a block typed for the mark (anchor coordinates).
// Not inputs: cells of any other fill, formulas (a yellow one is listed as such), and constants a formula reads.
// TemplateLook reads what TemplateBook leaves out: each cell's fill, empty cells too, and the merged ranges.
// C# 5 (Windows PowerShell 5.1 Add-Type, warnings are errors).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace CalcFlow
{
    public sealed class LookSheet
    {
        public string Name = "";
        public readonly Dictionary<long, int> Xf = new Dictionary<long, int>();      // cell -> style index (every cell of the sheet part)
        public readonly List<int[]> Merges = new List<int[]>();                      // r1, c1, r2, c2 (0-based, inclusive)
        public bool Hidden;                                                          // the sheet itself is hidden
        public readonly HashSet<int> HiddenRows = new HashSet<int>();
        public readonly List<int[]> HiddenCols = new List<int[]>();                  // c1, c2 (0-based, inclusive)

        // a cell the engineer cannot see: its row or its column is hidden
        public bool IsHidden(int row, int col) { return HiddenRows.Contains(row) || HiddenCols.Any(h => col >= h[0] && col <= h[1]); }

        // the merged range a cell lies in (null: none)
        public int[] MergeAt(int row, int col)
        {
            foreach (int[] m in Merges) if (row >= m[0] && row <= m[2] && col >= m[1] && col <= m[3]) return m;
            return null;
        }
    }

    public sealed class TemplateLook
    {
        public readonly List<LookSheet> Sheets = new List<LookSheet>();
        public readonly List<string> FillByXf = new List<string>();      // style index -> "RRGGBB" of a solid fill ("" none)

        public LookSheet Sheet(string name) { return Sheets.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)); }

        public string Fill(LookSheet s, int row, int col)
        {
            int xf;
            return s != null && s.Xf.TryGetValue(TSheet.Key(row, col), out xf) && xf >= 0 && xf < FillByXf.Count ? FillByXf[xf] : "";
        }

        public static TemplateLook Read(string path)
        {
            var look = new TemplateLook();
            using (FileStream fs = SharedFile.OpenRead(path))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Read, false))
            {
                string wbPath = "xl/workbook.xml";
                foreach (string[] r in Xlsx.Rels(zip, "_rels/.rels", ""))
                    if (r[1].EndsWith("/officeDocument", StringComparison.OrdinalIgnoreCase) && Xlsx.Entry(zip, r[2]) != null) { wbPath = r[2]; break; }
                if (Xlsx.Entry(zip, wbPath) == null) throw new Exception("No workbook part in " + path + " (an .xlsb or .xls has to be saved as .xlsx first)");
                string wbDir = Xlsx.DirOf(wbPath);
                var idToTarget = new Dictionary<string, string>(StringComparer.Ordinal);
                string stylesPath = null, themePath = null;
                foreach (string[] r in Xlsx.Rels(zip, wbDir + "_rels/" + Xlsx.FileOf(wbPath) + ".rels", wbDir))
                {
                    idToTarget[r[0]] = r[2];
                    if (r[1].EndsWith("/styles", StringComparison.OrdinalIgnoreCase)) stylesPath = r[2];
                    if (r[1].EndsWith("/theme", StringComparison.OrdinalIgnoreCase)) themePath = r[2];
                }
                List<string> theme = ReadTheme(zip, themePath);
                if (stylesPath != null && Xlsx.Entry(zip, stylesPath) != null) look.ReadStyles(Xlsx.Entry(zip, stylesPath), theme);
                var order = new List<string[]>();
                using (XmlReader xr = Xlsx.Xml(Xlsx.Entry(zip, wbPath)))
                    while (xr.Read())
                        if (xr.NodeType == XmlNodeType.Element && xr.LocalName == "sheet")
                        {
                            string name = xr.GetAttribute("name"), rid = null;
                            if (xr.MoveToFirstAttribute())
                            {
                                do { if (xr.LocalName == "id" && xr.NamespaceURI.Length > 0) rid = xr.Value; } while (xr.MoveToNextAttribute());
                                xr.MoveToElement();
                            }
                            order.Add(new[] { Xlsx.DecodeX(name ?? ""), rid ?? "", xr.GetAttribute("state") ?? "" });
                        }
                int k = 0;
                foreach (string[] sh in order)
                {
                    k++;
                    string target;
                    if (!idToTarget.TryGetValue(sh[1], out target)) target = wbDir + "worksheets/sheet" + k.ToString(CultureInfo.InvariantCulture) + ".xml";
                    ZipArchiveEntry e = Xlsx.Entry(zip, target);
                    if (e == null) continue;
                    var s = new LookSheet();
                    s.Name = sh[0];
                    s.Hidden = sh[2].Length > 0 && !string.Equals(sh[2], "visible", StringComparison.OrdinalIgnoreCase);
                    using (XmlReader xr = Xlsx.Xml(e))
                        while (xr.Read())
                        {
                            if (xr.NodeType != XmlNodeType.Element) continue;
                            if (xr.LocalName == "row" && IsOn(xr.GetAttribute("hidden")))
                            {
                                int r;
                                if (int.TryParse(xr.GetAttribute("r") ?? "", NumberStyles.Integer, CultureInfo.InvariantCulture, out r)) s.HiddenRows.Add(r - 1);
                            }
                            else if (xr.LocalName == "col" && IsOn(xr.GetAttribute("hidden")))
                            {
                                int a, b;
                                if (int.TryParse(xr.GetAttribute("min") ?? "", NumberStyles.Integer, CultureInfo.InvariantCulture, out a) && int.TryParse(xr.GetAttribute("max") ?? "", NumberStyles.Integer, CultureInfo.InvariantCulture, out b))
                                    s.HiddenCols.Add(new[] { a - 1, b - 1 });
                            }
                            else if (xr.LocalName == "c")
                            {
                                int col, row, xf;
                                if (!Xlsx.ParseRef(xr.GetAttribute("r") ?? "", out col, out row)) continue;
                                if (!int.TryParse(xr.GetAttribute("s") ?? "0", NumberStyles.Integer, CultureInfo.InvariantCulture, out xf)) xf = 0;
                                s.Xf[TSheet.Key(row, col)] = xf;
                            }
                            else if (xr.LocalName == "mergeCell")
                            {
                                int r1, c1, r2, c2;
                                if (TemplateBook.ParseRange(xr.GetAttribute("ref") ?? "", out r1, out c1, out r2, out c2)) s.Merges.Add(new[] { r1, c1, r2, c2 });
                            }
                        }
                    look.Sheets.Add(s);
                }
            }
            return look;
        }

        static bool IsOn(string v) { return v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase); }

        // ---- colours

        // Excel's own palette for a colour given by index
        static readonly string[] Indexed =
        {
            "000000", "FFFFFF", "FF0000", "00FF00", "0000FF", "FFFF00", "FF00FF", "00FFFF", "000000", "FFFFFF", "FF0000", "00FF00", "0000FF", "FFFF00", "FF00FF", "00FFFF",
            "800000", "008000", "000080", "808000", "800080", "008080", "C0C0C0", "808080", "9999FF", "993366", "FFFFCC", "CCFFFF", "660066", "FF8080", "0066CC", "CCCCFF",
            "000080", "FF00FF", "FFFF00", "00FFFF", "800080", "800000", "008080", "0000FF", "00CCFF", "CCFFFF", "CCFFCC", "FFFF99", "99CCFF", "FF99CC", "CC99FF", "FFCC99",
            "3366FF", "33CCCC", "99CC00", "FFCC00", "FF9900", "FF6600", "666699", "969696", "003366", "339966", "003300", "333300", "993300", "993366", "333399", "333333"
        };

        // the theme's twelve colours in the order a style counts them: lt1, dk1, lt2, dk2, accent1 to 6, hlink, folHlink
        static List<string> ReadTheme(ZipArchive zip, string path)
        {
            var named = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            ZipArchiveEntry e = path == null ? null : Xlsx.Entry(zip, path);
            if (e == null) e = zip.Entries.FirstOrDefault(x => Regex.IsMatch(x.FullName.Replace('\\', '/'), @"^xl/theme/[^/]+\.xml$", RegexOptions.IgnoreCase));
            if (e != null)
                using (XmlReader xr = Xlsx.Xml(e))
                {
                    bool scheme = false;
                    string slot = null;
                    while (xr.Read())
                    {
                        if (xr.NodeType == XmlNodeType.Element)
                        {
                            if (xr.LocalName == "clrScheme") scheme = true;
                            else if (scheme && xr.Depth > 0 && (xr.LocalName == "srgbClr" || xr.LocalName == "sysClr"))
                            {
                                string v = xr.LocalName == "srgbClr" ? xr.GetAttribute("val") : xr.GetAttribute("lastClr");
                                if (slot != null && v != null && !named.ContainsKey(slot)) named[slot] = v.ToUpperInvariant();
                            }
                            else if (scheme) slot = xr.LocalName;
                        }
                        else if (xr.NodeType == XmlNodeType.EndElement && xr.LocalName == "clrScheme") break;
                    }
                }
            var list = new List<string>();
            foreach (string slot in new[] { "lt1", "dk1", "lt2", "dk2", "accent1", "accent2", "accent3", "accent4", "accent5", "accent6", "hlink", "folHlink" })
            {
                string v;
                list.Add(named.TryGetValue(slot, out v) ? v : "");
            }
            return list;
        }

        void ReadStyles(ZipArchiveEntry e, List<string> theme)
        {
            var fills = new List<string>();
            var custom = new List<string>();
            using (XmlReader xr = Xlsx.Xml(e))
            {
                bool inFills = false, inXfs = false, inIndexed = false, solid = false;
                string colour = null;
                while (xr.Read())
                {
                    if (xr.NodeType == XmlNodeType.Element)
                    {
                        string n = xr.LocalName;
                        if (n == "fills") inFills = !xr.IsEmptyElement;
                        else if (n == "cellXfs") inXfs = !xr.IsEmptyElement;
                        else if (n == "indexedColors") inIndexed = !xr.IsEmptyElement;
                        else if (inIndexed && n == "rgbColor") { string v = xr.GetAttribute("rgb") ?? ""; custom.Add(v.Length >= 6 ? v.Substring(v.Length - 6).ToUpperInvariant() : ""); }
                        else if (inFills && n == "fill") { solid = false; colour = null; if (xr.IsEmptyElement) fills.Add(""); }
                        else if (inFills && n == "patternFill") solid = string.Equals(xr.GetAttribute("patternType"), "solid", StringComparison.OrdinalIgnoreCase);
                        else if (inFills && n == "fgColor" && colour == null)
                            colour = Colour(xr.GetAttribute("rgb"), xr.GetAttribute("theme"), xr.GetAttribute("indexed"), xr.GetAttribute("tint"), theme, custom);
                        else if (inXfs && n == "xf")
                        {
                            int f;
                            FillByXf.Add(int.TryParse(xr.GetAttribute("fillId") ?? "0", NumberStyles.Integer, CultureInfo.InvariantCulture, out f) && f >= 0 && f < fills.Count ? fills[f] : "");
                        }
                    }
                    else if (xr.NodeType == XmlNodeType.EndElement)
                    {
                        string n = xr.LocalName;
                        if (n == "fills") inFills = false;
                        else if (n == "cellXfs") inXfs = false;
                        else if (n == "indexedColors") inIndexed = false;
                        else if (inFills && n == "fill") fills.Add(solid && colour != null ? colour : "");
                    }
                }
            }
        }

        static string Colour(string rgb, string themeIndex, string indexed, string tint, List<string> theme, List<string> custom)
        {
            string c = "";
            int i;
            if (!string.IsNullOrEmpty(rgb) && rgb.Length >= 6) c = rgb.Substring(rgb.Length - 6).ToUpperInvariant();
            else if (themeIndex != null && int.TryParse(themeIndex, NumberStyles.Integer, CultureInfo.InvariantCulture, out i) && i >= 0 && i < theme.Count) c = theme[i];
            else if (indexed != null && int.TryParse(indexed, NumberStyles.Integer, CultureInfo.InvariantCulture, out i) && i >= 0)
                c = i < custom.Count && custom[i].Length == 6 ? custom[i] : (i < Indexed.Length ? Indexed[i] : "");
            if (c.Length != 6) return "";
            double t;
            if (tint != null && double.TryParse(tint, NumberStyles.Float, CultureInfo.InvariantCulture, out t) && t != 0) c = Tint(c, t);
            return c;
        }

        static void Hls(string rgb, out double h, out double l, out double s)
        {
            double r = Convert.ToInt32(rgb.Substring(0, 2), 16) / 255.0, g = Convert.ToInt32(rgb.Substring(2, 2), 16) / 255.0, b = Convert.ToInt32(rgb.Substring(4, 2), 16) / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
            l = (max + min) / 2;
            h = 0; s = 0;
            if (d <= 0) return;
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h *= 60;
        }

        static double Channel(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 0.5) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }

        // a theme colour made lighter (tint above 0) or darker, the way Excel does it
        public static string Tint(string rgb, double tint)
        {
            double h, l, s;
            Hls(rgb, out h, out l, out s);
            l = tint < 0 ? l * (1 + tint) : l * (1 - tint) + tint;
            double r = l, g = l, b = l;
            if (s > 0)
            {
                double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q, hk = h / 360;
                r = Channel(p, q, hk + 1.0 / 3); g = Channel(p, q, hk); b = Channel(p, q, hk - 1.0 / 3);
            }
            Func<double, string> hex = v => ((int)Math.Round(Math.Max(0, Math.Min(1, v)) * 255)).ToString("X2", CultureInfo.InvariantCulture);
            return hex(r) + hex(g) + hex(b);
        }

        // The fill of a cell to fill in, where nothing else is said: the yellow family, pale or strong (a gold too).
        // Not grey, green, orange or blue, and not a cream that is all but white (its three colours within 12% of
        // one another).
        public static bool IsInputColour(string rgb)
        {
            if (rgb == null || rgb.Length != 6) return false;
            double h, l, s;
            Hls(rgb, out h, out l, out s);
            int r = Convert.ToInt32(rgb.Substring(0, 2), 16), g = Convert.ToInt32(rgb.Substring(2, 2), 16), b = Convert.ToInt32(rgb.Substring(4, 2), 16);
            double spread = (Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b))) / 255.0;
            return h >= 38 && h <= 72 && spread >= 0.12 && l >= 0.35;
        }

        // the fills that mark inputs in this library when it is not yellow: "EAF3FF", "#eaf3ff", "FFEAF3FF"
        public static HashSet<string> Colours(IEnumerable<string> given)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string one in given ?? new string[0])
                foreach (string part in (one ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string c = part.Trim().TrimStart('#').ToUpperInvariant();
                    if (c.Length == 8) c = c.Substring(2);
                    if (!Regex.IsMatch(c, "^[0-9A-F]{6}$")) throw new Exception("'" + part + "' is not a fill colour: give it as six hex digits, such as FFFF99 (the Fills tab of Template_Map.xlsx lists the ones found)");
                    set.Add(c);
                }
            return set;
        }
    }

    public sealed class MapColumn
    {
        public string Cells = "", Heading = "", Group = "", Unit = "", Example = "", FilledBy = "", Notes = "";
        public string LabelCell = "";       // the cell the heading was read from ("" none)
        public int Col, Width = 1;          // the column of its cells (0-based), and how many columns a merged cell spans
        public string Check = "";           // why the engineer should look at this one ("" nothing to look at)
    }

    public sealed class MapBlock
    {
        public string Sheet = "", Range = "", Shape = "";       // Shape: Cell | Row of cells | List | Table
        public int Top, Left, Bottom, Right, Rows;
        public bool LoadRows;                                    // a table the tool writes a row at a time
        public readonly List<MapColumn> Columns = new List<MapColumn>();
        // beside a table the tool writes: the formula columns of its rows, one result a row (Cells: the first row's cell)
        public readonly List<MapColumn> Results = new List<MapColumn>();
    }

    public sealed class MapFill
    {
        public string Sheet = "", Fill = "", Example = "";
        public int Cells, Formulas;
        public bool Input;
    }

    public sealed class TemplateMapResult
    {
        public readonly List<MapBlock> Blocks = new List<MapBlock>();
        public readonly List<MapFill> Fills = new List<MapFill>();
        public readonly List<string> Notes = new List<string>();
    }

    public static class TemplateMap
    {
        public const string PerMark = "Typed per mark", PerEnd = "Typed per beam end", MarkTable = "Typed per mark (a table)";

        sealed class Unit1 { public int R1, C1, R2, C2; }      // one input cell: a cell, or a merged range

        public static TemplateMapResult Read(string path) { return Read(path, null); }

        // inputFills: the fills that mark the cells to fill in (six hex digits each); none given = the yellow family
        public static TemplateMapResult Read(string path, IEnumerable<string> inputFills)
        {
            return Read(TemplateBook.Open(path), TemplateLook.Read(path), inputFills);
        }

        [ThreadStatic] static HashSet<string> _fills;       // the fills in force while one template is read

        static bool IsInput(string fill) { return _fills != null && _fills.Count > 0 ? _fills.Contains(fill) : TemplateLook.IsInputColour(fill); }

        public static TemplateMapResult Read(TemplateBook book, TemplateLook look, IEnumerable<string> inputFills)
        {
            _fills = TemplateLook.Colours(inputFills);
            var res = new TemplateMapResult();
            foreach (TSheet sheet in book.Sheets)
            {
                LookSheet ls = look.Sheet(sheet.Name);
                if (ls == null) continue;
                // what the engineer cannot see is not for the engineer to fill in: a hidden sheet, row or column
                if (ls.Hidden) { res.Notes.Add(sheet.Name + ": a hidden sheet, not read"); continue; }
                int unseen = 0;
                // every fill of the sheet, and the cells it is on
                var fills = new Dictionary<string, MapFill>(StringComparer.Ordinal);
                var units = new List<Unit1>();
                foreach (long key in ls.Xf.Keys.OrderBy(k => k))
                {
                    int row = (int)(key >> 20), col = (int)(key & 0xFFFFF);
                    string fill = look.Fill(ls, row, col);
                    if (fill.Length == 0) continue;
                    int[] merge = ls.MergeAt(row, col);
                    if (merge != null && (merge[0] != row || merge[1] != col)) continue;       // a merged cell counts once, where it starts
                    TCell cell = sheet.Get(row, col);
                    bool formula = cell != null && cell.IsFormula;
                    MapFill f;
                    if (!fills.TryGetValue(fill, out f)) { f = new MapFill(); f.Sheet = sheet.Name; f.Fill = fill; f.Example = Xlsx.Ref(row, col); f.Input = IsInput(fill); fills[fill] = f; }
                    if (formula) f.Formulas++; else f.Cells++;
                    if (!f.Input || formula) continue;
                    if (ls.IsHidden(row, col)) { unseen++; continue; }
                    var u = new Unit1();
                    u.R1 = row; u.C1 = col; u.R2 = merge == null ? row : merge[2]; u.C2 = merge == null ? col : merge[3];
                    units.Add(u);
                }
                res.Fills.AddRange(fills.Values.OrderByDescending(x => x.Cells));
                if (unseen > 0) res.Notes.Add(sheet.Name + ": " + unseen.ToString(CultureInfo.InvariantCulture) + " cell(s) with the input fill sit in hidden rows or columns and are left out");
                foreach (MapFill f in fills.Values.Where(x => x.Input && x.Formulas > 0))
                    res.Notes.Add(sheet.Name + ": " + f.Formulas.ToString(CultureInfo.InvariantCulture) + " cell(s) with the input fill " + f.Fill + " hold a formula and are not inputs");

                // rows of cells that touch side by side, then the rows that sit under one another with the same columns
                var runs = new List<List<Unit1>>();
                foreach (var byRow in units.GroupBy(u => u.R1).OrderBy(g => g.Key))
                {
                    List<Unit1> run = null;
                    foreach (Unit1 u in byRow.OrderBy(x => x.C1))
                    {
                        if (run != null && run[run.Count - 1].C2 + 1 == u.C1 && run[run.Count - 1].R2 == u.R2) { run.Add(u); continue; }
                        run = new List<Unit1> { u };
                        runs.Add(run);
                    }
                }
                var open = new List<List<List<Unit1>>>();
                foreach (var run in runs)
                {
                    List<List<Unit1>> into = open.FirstOrDefault(b =>
                    {
                        List<Unit1> last = b[b.Count - 1];
                        return last[0].R2 + 1 == run[0].R1 && last.Count == run.Count && last.Zip(run, (a, c) => a.C1 == c.C1 && a.C2 == c.C2).All(x => x);
                    });
                    if (into == null) { into = new List<List<Unit1>>(); open.Add(into); }
                    into.Add(run);
                }
                foreach (var rows in open.OrderBy(b => b[0][0].R1).ThenBy(b => b[0][0].C1))
                    res.Blocks.AddRange(Blocks(sheet, ls, look, rows));
            }
            return res;
        }

        // one stack of rows -> its block, or one block a cell when each cell of a list carries its own label
        static List<MapBlock> Blocks(TSheet sheet, LookSheet ls, TemplateLook look, List<List<Unit1>> rows)
        {
            var list = new List<MapBlock>();
            int m = rows.Count, n = rows[0].Count;
            Unit1 first = rows[0][0], last = rows[m - 1][n - 1];
            if (n == 1 && m > 1)
            {
                // a list under one heading, or values that each have their own label beside them
                // (most of them labelled is enough: a form has the odd cell with no label, and a list numbered down its
                // side has "#1", "#2" for labels, which name nothing)
                var labels = rows.Select(r => LeftLabel(sheet, ls, look, r[0])).ToList();
                int named = labels.Count(l => l.Length > 0 && !Regex.IsMatch(l, @"^\W*(no\.?|row|item)?\s*\d+\W*$", RegexOptions.IgnoreCase));
                bool own = named * 5 >= m * 3;
                if (own)
                {
                    foreach (var r in rows) list.AddRange(Blocks(sheet, ls, look, new List<List<Unit1>> { r }));
                    return list;
                }
            }
            var b = new MapBlock();
            b.Sheet = sheet.Name; b.Top = first.R1; b.Left = first.C1; b.Bottom = last.R2; b.Right = last.C2; b.Rows = m;
            b.Range = Xlsx.Ref(b.Top, b.Left) + (b.Top == b.Bottom && b.Left == b.Right ? "" : ":" + Xlsx.Ref(b.Bottom, b.Right));
            b.Shape = m == 1 ? (n == 1 ? "Cell" : "Row of cells") : (n == 1 ? "List" : "Table");
            for (int j = 0; j < n; j++)
            {
                Unit1 top = rows[0][j], bottom = rows[m - 1][j];
                var c = new MapColumn();
                c.Cells = Xlsx.Ref(top.R1, top.C1) + (m == 1 ? "" : ":" + Xlsx.Ref(bottom.R1, bottom.C1));
                c.Col = top.C1; c.Width = top.C2 - top.C1 + 1;
                string group, at;
                c.Heading = Above(sheet, ls, look, top, out group, out at);
                c.Group = group; c.LabelCell = at;
                if (m == 1 && n == 1 && c.Heading.Length == 0) { c.Heading = LeftLabel(sheet, ls, look, top, out at).TrimEnd(':', ' '); c.LabelCell = at; }
                else if (m == 1 && n == 1)
                {
                    // a lone cell with text above and text beside it: the label beside it is the nearer name
                    string left = LeftLabel(sheet, ls, look, top, out at);
                    if (left.Length > 0) { c.Group = ""; c.Heading = left.TrimEnd(':', ' '); c.LabelCell = at; }
                }
                c.Unit = Units.FromLabel(c.Heading);
                if (c.Unit.Length == 0 && m == 1)
                {
                    TCell right = sheet.Get(top.R1, top.C2 + 1);
                    Unit u;
                    if (right != null && right.IsText && right.Value.Trim().Length <= 8 && Units.TryParse(right.Value.Trim(), out u)) c.Unit = right.Value.Trim();
                }
                for (int i = 0; i < m && c.Example.Length == 0; i++)
                {
                    TCell v = sheet.Get(rows[i][j].R1, rows[i][j].C1);
                    if (v != null && !v.IsEmpty) c.Example = v.Value;
                }
                if (top.R1 != top.R2 || top.C1 != top.C2) c.Notes = "merged cells " + Xlsx.Ref(top.R1, top.C1) + ":" + Xlsx.Ref(top.R2, top.C2);
                b.Columns.Add(c);
            }
            // rows the tool writes: a table or list with a heading that reads as a force, a beam, a node or a load case
            b.LoadRows = m > 1 && b.Columns.Any(c => FromModel(c) != null && FromModel(c) != "Beam section");
            foreach (MapColumn c in b.Columns)
            {
                string model = FromModel(c);
                // "Axial (kip)" among rows the tool writes is most often the member's own axial force: said as a guess
                Unit fu;
                if (b.LoadRows && model == null && Regex.IsMatch(c.Heading, @"^axial\b", RegexOptions.IgnoreCase) && Units.TryParse(c.Unit, out fu) && fu.Dim == Dimension.Force)
                {
                    model = "Fx";
                    c.Notes = (c.Notes.Length > 0 ? c.Notes + "; " : "") + "Fx is a guess from the word axial";
                }
                if (b.LoadRows) c.FilledBy = model ?? PerEnd;
                else if (m > 1) c.FilledBy = MarkTable;
                else c.FilledBy = model ?? PerMark;      // a lone cell headed Fx takes the force of one load case at a time
            }
            // the results of such a table: the formulas along its first row, to its right (and left), a column each
            if (b.LoadRows)
                foreach (int dir in new[] { 1, -1 })
                {
                    int gap = 0;
                    for (int col = dir > 0 ? b.Right + 1 : b.Left - 1; col >= 0 && col <= b.Right + 60 && gap < 3; col += dir)
                    {
                        TCell f = sheet.Get(b.Top, col);
                        if (f == null || !f.IsFormula) { gap++; continue; }
                        gap = 0;
                        if (ls.IsHidden(b.Top, col)) continue;
                        var one = new Unit1();
                        one.R1 = one.R2 = b.Top; one.C1 = one.C2 = col;
                        var r = new MapColumn();
                        string group, at;
                        r.Cells = Xlsx.Ref(b.Top, col); r.Col = col;
                        r.Heading = Above(sheet, ls, look, one, out group, out at);
                        r.Group = group; r.LabelCell = at;
                        r.Example = f.Value;
                        b.Results.Add(r);
                    }
                }
            // what the engineer should look at: the tool's guesses, and an input it could not name
            foreach (MapColumn c in b.Columns)
            {
                if (c.Heading.Length == 0) c.Check = "no heading found: named by its cell";
                else if (c.Notes.IndexOf("a guess", StringComparison.Ordinal) >= 0) c.Check = "a guess";
                else if (!b.LoadRows && c.FilledBy != PerMark && c.FilledBy != MarkTable) c.Check = "taken from the model by its heading";
            }
            list.Add(b);
            return list;
        }

        // what the model can fill, by the heading (null: nothing the heading names)
        static string FromModel(MapColumn c)
        {
            string h = Regex.Replace(c.Heading, @"\s+", " ").Trim();
            string force = ProjectBook.GuessSource(h, "", c.Unit);
            if (force != ProjectBook.Typed) return force;
            // a beam number, a node and a load case are whole numbers: "Beam" over W8X31 names a shape to type
            if (c.Example.Length > 0 && !Regex.IsMatch(c.Example.Trim(), @"^\d+(\.0+)?$")) return null;
            if (Regex.IsMatch(h, @"^(staad )?(beam|member|memb\.?)\s*(#|no\.?|number|id)?$", RegexOptions.IgnoreCase)) return "Beam number";
            if (Regex.IsMatch(h, @"^(staad )?(node|joint|jt\.?)\s*(#|no\.?|number|id)?$", RegexOptions.IgnoreCase)) return "Node";
            if (Regex.IsMatch(h, @"^(l\.?\s*/?\s*c\.?|load\s*(case|comb\w*)?|case|combo\w*)\s*(#|no\.?|number|id)?$", RegexOptions.IgnoreCase)) return "Load case";
            return null;
        }

        static string Text(TCell c)
        {
            if (c == null || c.Value.Length == 0 || c.IsNumber || c.Type == "b" || c.Type == "e") return "";
            return Regex.Replace(c.Value, @"\s+", " ").Trim();
        }

        // the label of a lone cell: the nearest text to its left that is not itself a cell to fill
        static string LeftLabel(TSheet sheet, LookSheet ls, TemplateLook look, Unit1 u) { string at; return LeftLabel(sheet, ls, look, u, out at); }

        static string LeftLabel(TSheet sheet, LookSheet ls, TemplateLook look, Unit1 u, out string at)
        {
            at = "";
            for (int col = u.C1 - 1; col >= Math.Max(0, u.C1 - 4); col--)
            {
                int[] m = ls.MergeAt(u.R1, col);
                int r = m == null ? u.R1 : m[0], c = m == null ? col : m[1];
                if (IsInput(look.Fill(ls, r, c))) return "";
                string t = Text(sheet.Get(r, c));
                if (t.Length > 0) { at = Xlsx.Ref(r, c); return t; }
                TCell any = sheet.Get(r, c);
                if (any != null && !any.IsEmpty) return "";         // a number or a formula beside it: no label
                if (m != null) col = m[1];
            }
            return "";
        }

        // The heading over a column: the text right above it, a merged heading cell counted wherever it spans ("FX"
        // over "(kip)" in two cells is one heading). Its group is the heading above that one, when that is a cell
        // merged across several columns ("End forces from the analysis"): other text higher up belongs to something
        // else, and is left alone.
        static string Above(TSheet sheet, LookSheet ls, TemplateLook look, Unit1 u, out string group, out string at)
        {
            group = ""; at = "";
            string own = "";
            int row = u.R1 - 1;
            for (int step = 0; step < 3 && row >= 0; step++)
            {
                int[] m = ls.MergeAt(row, u.C1);
                int r = m == null ? row : m[0], c = m == null ? u.C1 : m[1];
                if (IsInput(look.Fill(ls, r, c))) break;
                string t = Text(sheet.Get(r, c));
                if (t.Length == 0) break;
                bool across = m != null && m[3] > m[1];
                if (own.Length == 0) { own = t; at = Xlsx.Ref(r, c); if (across) break; }
                else if (Regex.IsMatch(own, @"^\(.{1,12}\)$") && !across) own = t + " " + own;
                else { if (across) group = t; break; }
                row = r - 1;
            }
            return own;
        }

        // ---- the preview workbook: what was found in every template, for the engineer to look over
        public static List<XOutSheet> Sheets(List<KeyValuePair<string, TemplateMapResult>> templates)
        {
            var inputs = new List<Dictionary<string, string>>();
            var fills = new List<Dictionary<string, string>>();
            var summary = new List<Dictionary<string, string>>();
            Func<int, string> n = i => i.ToString(CultureInfo.InvariantCulture);
            foreach (var t in templates)
            {
                foreach (MapBlock b in t.Value.Blocks)
                    foreach (MapColumn c in b.Columns)
                    {
                        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        d["Template"] = t.Key; d["Sheet"] = b.Sheet; d["Block"] = b.Range;
                        d["Shape"] = b.Shape + (b.Rows > 1 ? " of " + n(b.Rows) + " rows" : "") + (b.LoadRows ? ", written by the tool" : "");
                        d["Cells"] = c.Cells; d["Heading"] = c.Heading; d["Under"] = c.Group; d["Unit"] = c.Unit; d["Holds now"] = c.Example; d["Filled by"] = c.FilledBy;
                        d["Look at"] = c.Check; d["Notes"] = c.Notes;
                        inputs.Add(d);
                    }
                foreach (var bySheet in t.Value.Blocks.GroupBy(x => x.Sheet))
                {
                    var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    d["Template"] = t.Key; d["Sheet"] = bySheet.Key;
                    d["Single inputs"] = n(bySheet.Where(x => x.Rows == 1).Sum(x => x.Columns.Count));
                    d["Tables to type"] = n(bySheet.Count(x => x.Rows > 1 && !x.LoadRows));
                    d["Tables the tool writes"] = n(bySheet.Count(x => x.LoadRows));
                    d["To look at"] = n(bySheet.Sum(x => x.Columns.Count(c => c.Check.Length > 0)));
                    d["Notes"] = string.Join("; ", t.Value.Notes.Where(x => x.StartsWith(bySheet.Key + ": ", StringComparison.Ordinal)).Select(x => x.Substring(bySheet.Key.Length + 2)).ToArray());
                    summary.Add(d);
                }
                foreach (string note in t.Value.Notes.Where(x => !t.Value.Blocks.Any(bk => x.StartsWith(bk.Sheet + ": ", StringComparison.Ordinal))))
                {
                    int cut = note.IndexOf(": ", StringComparison.Ordinal);
                    var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    d["Template"] = t.Key; d["Sheet"] = cut > 0 ? note.Substring(0, cut) : ""; d["Single inputs"] = "0"; d["Tables to type"] = "0"; d["Tables the tool writes"] = "0"; d["To look at"] = "0";
                    d["Notes"] = cut > 0 ? note.Substring(cut + 2) : note;
                    summary.Add(d);
                }
                foreach (MapFill f in t.Value.Fills)
                {
                    var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    d["Template"] = t.Key; d["Sheet"] = f.Sheet; d["Fill"] = f.Fill; d["Cells"] = n(f.Cells); d["Formula cells"] = n(f.Formulas);
                    d["Taken as inputs"] = f.Input ? "Y" : "N"; d["First cell"] = f.Example;
                    fills.Add(d);
                }
            }
            var sheets = new List<XOutSheet>();
            string[] scols = { "Template", "Sheet", "Single inputs", "Tables to type", "Tables the tool writes", "To look at", "Notes" };
            XOutSheet ss = WorkbookTables.Sheet("Templates", "tblMapTemplates", scols, summary,
                "One row per template sheet that has inputs: how many, and how many the tool is unsure of (To look at). Start with the rows where that is not 0: on the tab Inputs found, filter the column Look at.",
                c => (c == "Template" || c == "Sheet") ? ColumnKind.Key : ColumnKind.Reference);
            foreach (string c in new[] { "Single inputs", "Tables to type", "Tables the tool writes", "To look at" }) WorkbookTables.Numbers(ss, Array.IndexOf(scols, c), "0");
            ss.Width(Array.IndexOf(scols, "Notes"), 70);
            sheets.Add(ss);
            string[] icols = { "Template", "Sheet", "Block", "Shape", "Cells", "Heading", "Under", "Unit", "Holds now", "Filled by", "Look at", "Notes" };
            XOutSheet si = WorkbookTables.Sheet("Inputs found", "tblMapInputs", icols, inputs,
                "A look at what the tool reads as inputs in each template: the cells filled yellow that hold no formula, in blocks of cells that touch. One row per cell, or per column of a list or table. 'Filled by' is the tool's first answer to the one question it will ask about each. Nothing here is used yet: it is for looking over.",
                c => (c == "Template" || c == "Sheet" || c == "Block") ? ColumnKind.Key : c == "Filled by" ? ColumnKind.Input : ColumnKind.Reference);
            si.Width(Array.IndexOf(icols, "Heading"), 34); si.Width(Array.IndexOf(icols, "Under"), 30); si.Width(Array.IndexOf(icols, "Filled by"), 26); si.Width(Array.IndexOf(icols, "Look at"), 34);
            sheets.Add(si);
            string[] fcols = { "Template", "Sheet", "Fill", "Cells", "Formula cells", "Taken as inputs", "First cell" };
            XOutSheet sf = WorkbookTables.Sheet("Fills", "tblMapFills", fcols, fills,
                "Every fill colour found on each template sheet (red, green, blue as six hex digits), with the cells that carry it. Only the yellow family is taken as inputs. A colour with N here that marks cells you fill in is what the tool is missing.",
                c => (c == "Template" || c == "Sheet") ? ColumnKind.Key : ColumnKind.Reference);
            foreach (string c in new[] { "Cells", "Formula cells" }) WorkbookTables.Numbers(sf, Array.IndexOf(fcols, c), "0");
            sheets.Add(sf);
            return sheets;
        }
    }
}
