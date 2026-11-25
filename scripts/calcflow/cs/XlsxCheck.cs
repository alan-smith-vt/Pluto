// XlsxCheck.cs -- Excel's rules for the tables of a workbook, read from the file as it was written. Excel opens a
// workbook that breaks one with "Repaired Records: Table from /xl/tables/tableN.xml part (Table)", and neither the
// Open XML schema nor LibreOffice checks any of them. The tests run this over every workbook they write; it reads
// the parts itself and shares only the comparison of headings with the writer.
//   - a heading is a text cell (not a number, not a formula), and its text is the column's name in the table part;
//   - a name has 1 to 255 characters and no line break or tab;
//   - no two columns of a table are alike (Xlsx.HeadingKey);
//   - a table has a data row, as many columns as its range is wide, and no merged cells or other table inside it;
//   - a table's name is a name Excel takes, once in the workbook.
// And for a data validation (a drop-down, or the note shown beside a selected cell), which Excel removes when it
// breaks one ("Removed Feature: Data validation"): a title of 32 characters at most, a text of 255, a formula of 255,
// and no cell under two validations.
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
    public static class XlsxCheck
    {
        sealed class HeadCell
        {
            public string Type = "n";
            public bool Formula;
            public readonly StringBuilder Raw = new StringBuilder();
        }

        // what Excel would repair in the tables of this workbook: one line each, none when the tables are sound
        public static List<string> Tables(string path)
        {
            var bad = new List<string>();
            string file = Path.GetFileName(path);
            using (ZipArchive zip = ZipFile.OpenRead(path))
            {
                List<string> sst = null;
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (ZipArchiveEntry rel in zip.Entries.Where(e => e.FullName.Replace('\\', '/').StartsWith("xl/worksheets/_rels/", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(e => e.FullName, StringComparer.Ordinal).ToList())
                {
                    var targets = new List<string>();
                    using (XmlReader xr = Xlsx.Xml(rel))
                        while (xr.Read())
                            if (xr.NodeType == XmlNodeType.Element && xr.LocalName == "Relationship" && (xr.GetAttribute("Type") ?? "").EndsWith("/table", StringComparison.Ordinal))
                                targets.Add(xr.GetAttribute("Target") ?? "");
                    if (targets.Count == 0) continue;
                    string sheetFile = Path.GetFileName(rel.FullName);
                    sheetFile = sheetFile.Substring(0, sheetFile.Length - ".rels".Length);
                    var tables = new List<XTable>();
                    var parts = new List<string>();
                    foreach (string t in targets)
                    {
                        string part = "xl/tables/" + t.Substring(t.LastIndexOf('/') + 1);
                        XTable x = Xlsx.ReadTable(zip, part, sheetFile);
                        if (x == null) { bad.Add(file + " " + part + ": the table part is missing or has no range"); continue; }
                        tables.Add(x); parts.Add(part);
                    }
                    var merges = new List<string>();
                    Dictionary<string, HeadCell> cells = HeaderCells(zip, "xl/worksheets/" + sheetFile, new HashSet<int>(tables.Select(t => t.Top)), merges);
                    if (sst == null) sst = Xlsx.SharedStrings(zip, "xl/sharedStrings.xml");
                    for (int i = 0; i < tables.Count; i++)
                    {
                        XTable t = tables[i];
                        string where = file + " " + parts[i] + " (" + t.Name + ", " + sheetFile + " " + Xlsx.Ref(t.Top, t.Left) + ":" + Xlsx.Ref(t.Bottom, t.Right) + "): ";
                        if (!Regex.IsMatch(t.Name, @"^[A-Za-z_\\][A-Za-z0-9_.\\]{0,254}$") || Regex.IsMatch(t.Name, "^[A-Za-z]{1,3}[0-9]+$") || Regex.IsMatch(t.Name, "^[Rr][0-9]*[Cc]?[0-9]*$") || Regex.IsMatch(t.Name, "^[Cc][0-9]*$"))
                            bad.Add(where + "the table's name is not one Excel takes");
                        if (!names.Add(t.Name)) bad.Add(where + "the table's name is used twice in the workbook");
                        if (t.Bottom - t.TotalsRows <= t.Top) bad.Add(where + "no data row under the headings");
                        if (t.Columns.Length != t.Right - t.Left + 1) bad.Add(where + t.Columns.Length.ToString(CultureInfo.InvariantCulture) + " columns named for a range " + (t.Right - t.Left + 1).ToString(CultureInfo.InvariantCulture) + " wide");
                        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
                        for (int c = 0; c < t.Columns.Length && c <= t.Right - t.Left; c++)
                        {
                            string name = t.Columns[c], at = Xlsx.Ref(t.Top, t.Left + c);
                            if (name.Length == 0) bad.Add(where + at + " has no column name");
                            if (name.Length > Xlsx.MaxTableHeading) bad.Add(where + at + " has a name of " + name.Length.ToString(CultureInfo.InvariantCulture) + " characters (255 at most): " + name.Substring(0, 60) + " ...");
                            if (name.Any(ch => ch < ' ')) bad.Add(where + at + " has a line break or a tab in its name");
                            HeadCell h;
                            if (!cells.TryGetValue(at, out h)) bad.Add(where + at + " has no heading cell for '" + Short(name) + "'");
                            else if (h.Formula) bad.Add(where + at + " is a formula: a heading is typed text");
                            else if (h.Type != "s" && h.Type != "inlineStr") bad.Add(where + at + " is not a text cell (" + h.Type + " " + Short(h.Raw.ToString()) + "): a heading '" + Short(name) + "' must be text");
                            else
                            {
                                string text = h.Raw.ToString();
                                int k;
                                if (h.Type == "s") text = int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out k) && k >= 0 && k < sst.Count ? sst[k] : "";
                                else text = Xlsx.DecodeX(text);
                                if (!string.Equals(text, name, StringComparison.Ordinal)) bad.Add(where + at + " reads '" + Short(text) + "' but the column is named '" + Short(name) + "'");
                            }
                            string key = Xlsx.HeadingKey(name), twin;
                            if (seen.TryGetValue(key, out twin)) bad.Add(where + at + " '" + Short(name) + "' is the same name as '" + Short(twin) + "'");
                            else seen[key] = name;
                        }
                        foreach (string m in merges)
                        {
                            string[] p = m.Split(':');
                            int c1, r1, c2, r2;
                            if (!Xlsx.ParseRef(p[0], out c1, out r1)) continue;
                            if (p.Length < 2 || !Xlsx.ParseRef(p[1], out c2, out r2)) { c2 = c1; r2 = r1; }
                            if (c1 <= t.Right && c2 >= t.Left && r1 <= t.Bottom && r2 >= t.Top) bad.Add(where + "merged cells " + m + " inside the table");
                        }
                        for (int j = 0; j < i; j++)
                            if (tables[j].Left <= t.Right && tables[j].Right >= t.Left && tables[j].Top <= t.Bottom && tables[j].Bottom >= t.Top) bad.Add(where + "overlaps table " + tables[j].Name);
                    }
                }
            }
            return bad;
        }

        // What Excel changed when it repaired a workbook: the tables of the workbook as it was written, against the copy
        // saved from Excel after the repair. A heading Excel renamed, cut or numbered names the rule that was broken.
        public static List<string> Changed(string written, string repaired)
        {
            var o = new List<string>();
            XWorkbook a = Xlsx.Read(written), b = Xlsx.Read(repaired);
            foreach (XTable t in a.Tables)
            {
                string where = "tab " + t.Sheet + ", table " + t.Name + ": ";
                XTable u = b.Tables.FirstOrDefault(x => string.Equals(x.Name, t.Name, StringComparison.OrdinalIgnoreCase))
                    ?? b.Tables.FirstOrDefault(x => string.Equals(x.Sheet, t.Sheet, StringComparison.OrdinalIgnoreCase) && x.Top == t.Top && x.Left == t.Left);
                XSheet before = a.Sheet(t.Sheet), after = b.Sheet(t.Sheet);
                if (u == null) o.Add(where + "the table is gone" + (after == null ? ", with its tab" : " (its cells are plain cells now)"));
                else
                {
                    if (!string.Equals(u.Name, t.Name, StringComparison.OrdinalIgnoreCase)) o.Add(where + "renamed to " + u.Name);
                    if (u.Top != t.Top || u.Left != t.Left || u.Bottom != t.Bottom || u.Right != t.Right)
                        o.Add(where + "its range " + Xlsx.Ref(t.Top, t.Left) + ":" + Xlsx.Ref(t.Bottom, t.Right) + " became " + Xlsx.Ref(u.Top, u.Left) + ":" + Xlsx.Ref(u.Bottom, u.Right));
                    for (int c = 0; c < t.Columns.Length; c++)
                    {
                        string was = t.Columns[c], now = c < u.Columns.Length ? u.Columns[c] : null;
                        if (now == null) o.Add(where + Xlsx.Ref(t.Top, t.Left + c) + " '" + Short(was) + "' is no longer a column");
                        else if (!string.Equals(was, now, StringComparison.Ordinal))
                            o.Add(where + Xlsx.Ref(t.Top, t.Left + c) + " '" + Short(was) + "' (" + was.Length.ToString(CultureInfo.InvariantCulture) + " characters) became '" + Short(now) + "'");
                    }
                }
                // the heading cells themselves, table or not
                if (before == null || after == null) continue;
                for (int c = t.Left; c <= t.Right; c++)
                {
                    string was = before.Cell(t.Top, c) ?? "", now = after.Cell(t.Top, c) ?? "";
                    if (!string.Equals(was, now, StringComparison.Ordinal))
                        o.Add(where + "the cell " + Xlsx.Ref(t.Top, c) + " read '" + Short(was) + "' and reads '" + Short(now) + "'");
                }
            }
            return o;
        }

        // what Excel would remove of the data validations of this workbook: one line each
        public static List<string> Validations(string path)
        {
            var bad = new List<string>();
            string file = Path.GetFileName(path);
            using (ZipArchive zip = ZipFile.OpenRead(path))
                foreach (ZipArchiveEntry e in zip.Entries.Where(x => Regex.IsMatch(x.FullName.Replace('\\', '/'), @"^xl/worksheets/[^/]+\.xml$", RegexOptions.IgnoreCase)).OrderBy(x => x.FullName, StringComparer.Ordinal).ToList())
                {
                    string where = file + " " + e.FullName + ": ";
                    var rects = new List<int[]>();
                    var refs = new List<string>();
                    using (XmlReader xr = Xlsx.Xml(e))
                    {
                        string sqref = null;
                        bool formula = false;
                        var text = new StringBuilder();
                        while (xr.Read())
                        {
                            if (xr.NodeType == XmlNodeType.Element && xr.LocalName == "dataValidation")
                            {
                                sqref = xr.GetAttribute("sqref") ?? "";
                                foreach (string[] lim in new[] { new[] { "promptTitle", "32" }, new[] { "errorTitle", "32" }, new[] { "prompt", "255" }, new[] { "error", "255" } })
                                {
                                    string v = xr.GetAttribute(lim[0]) ?? "";
                                    if (v.Length > int.Parse(lim[1], CultureInfo.InvariantCulture))
                                        bad.Add(where + "validation " + sqref + " has a " + lim[0] + " of " + v.Length.ToString(CultureInfo.InvariantCulture) + " characters (" + lim[1] + " at most)");
                                    if (v.Any(ch => ch < ' ')) bad.Add(where + "validation " + sqref + " has a line break or a tab in its " + lim[0]);
                                }
                                foreach (string one in sqref.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                                {
                                    string[] p = one.Split(':');
                                    int c1, r1, c2, r2;
                                    if (!Xlsx.ParseRef(p[0], out c1, out r1)) { bad.Add(where + "validation with a range that is not one: " + one); continue; }
                                    if (p.Length < 2 || !Xlsx.ParseRef(p[1], out c2, out r2)) { c2 = c1; r2 = r1; }
                                    rects.Add(new[] { r1, c1, r2, c2 });
                                    refs.Add(one);
                                }
                            }
                            else if (xr.NodeType == XmlNodeType.Element && (xr.LocalName == "formula1" || xr.LocalName == "formula2")) { formula = !xr.IsEmptyElement; text.Length = 0; }
                            else if (xr.NodeType == XmlNodeType.EndElement && (xr.LocalName == "formula1" || xr.LocalName == "formula2"))
                            {
                                if (text.Length > 255) bad.Add(where + "validation " + sqref + " has a formula of " + text.Length.ToString(CultureInfo.InvariantCulture) + " characters (255 at most)");
                                formula = false;
                            }
                            else if (formula && (xr.NodeType == XmlNodeType.Text || xr.NodeType == XmlNodeType.CDATA || xr.NodeType == XmlNodeType.SignificantWhitespace || xr.NodeType == XmlNodeType.Whitespace)) text.Append(xr.Value);
                        }
                    }
                    int said = 0;
                    for (int i = 0; i < rects.Count && said < 5; i++)
                        for (int j = 0; j < i && said < 5; j++)
                            if (rects[i][0] <= rects[j][2] && rects[i][2] >= rects[j][0] && rects[i][1] <= rects[j][3] && rects[i][3] >= rects[j][1])
                            {
                                bad.Add(where + "the validations " + refs[j] + " and " + refs[i] + " share a cell");
                                said++;
                            }
                }
            return bad;
        }

        static string Short(string s) { return s.Length <= 60 ? s : s.Substring(0, 57) + "..."; }

        // the cells of the heading rows (0-based row numbers), as the sheet part holds them, and the sheet's merged ranges
        static Dictionary<string, HeadCell> HeaderCells(ZipArchive zip, string part, HashSet<int> rows, List<string> merges)
        {
            var cells = new Dictionary<string, HeadCell>(StringComparer.Ordinal);
            ZipArchiveEntry e = Xlsx.Entry(zip, part);
            if (e == null) return cells;
            using (XmlReader xr = Xlsx.Xml(e))
            {
                bool wanted = false;
                HeadCell cur = null;
                string el = "";
                while (xr.Read())
                {
                    if (xr.NodeType == XmlNodeType.Element)
                    {
                        string n = xr.LocalName;
                        if (n == "row")
                        {
                            int r;
                            wanted = int.TryParse(xr.GetAttribute("r") ?? "", NumberStyles.Integer, CultureInfo.InvariantCulture, out r) && rows.Contains(r - 1);
                            cur = null;
                        }
                        else if (n == "c")
                        {
                            cur = null;
                            if (wanted)
                            {
                                cur = new HeadCell();
                                cur.Type = xr.GetAttribute("t") ?? "n";
                                cells[xr.GetAttribute("r") ?? ""] = cur;
                                if (xr.IsEmptyElement) cur = null;
                            }
                        }
                        else if (n == "f" && cur != null) cur.Formula = true;
                        else if (n == "mergeCell") merges.Add(xr.GetAttribute("ref") ?? "");
                        el = xr.IsEmptyElement ? "" : n;
                    }
                    else if (xr.NodeType == XmlNodeType.EndElement)
                    {
                        if (xr.LocalName == "c") cur = null;
                        else if (xr.LocalName == "row") wanted = false;
                        el = "";
                    }
                    else if (cur != null && (el == "v" || el == "t") && (xr.NodeType == XmlNodeType.Text || xr.NodeType == XmlNodeType.CDATA || xr.NodeType == XmlNodeType.Whitespace || xr.NodeType == XmlNodeType.SignificantWhitespace))
                        cur.Raw.Append(xr.Value);
                }
            }
            return cells;
        }
    }
}
