// Copied from pipe-review (src/cs/Xlsx.cs at d4b33aa, 2026-11-06) for CalcFlow; namespace PipeReview -> CalcFlow,
// otherwise unchanged. The writer overlaps Pluto's XlsxBook (scripts/lib/writers/XlsxWriter.cs): to be merged later.
// Xlsx.cs -- reads .xlsx / .csv / .txt without Excel and writes .xlsx workbooks (values and styles, no formulas).
// C# 5 (Windows PowerShell 5.1 Add-Type, warnings are errors). The reader is a port of Pluto's CaesarWorkbook
// (scripts/arms/CaesarResults.cs) extended with tables, defined names and cell types.
//
// Reader. An .xlsx is a zip of XML: the officeDocument part from _rels/.rels, sheets in workbook order through the
// workbook relationships, shared strings (rich-text runs joined, phonetic runs skipped), inline strings, every cell at
// its true row/column (the r reference), Excel's _xHHHH_ escapes decoded, booleans as TRUE/FALSE. Files are opened
// with FileShare.ReadWrite|Delete so a workbook that is open in Excel still reads (as last saved). Cells beyond
// MaxCols are ignored so a stray value far to the right cannot blow up memory. Tables (ListObjects) are read from
// the table parts; defined names from workbook.xml. A .csv reads as one sheet (separator , ; or tab by count); a
// .txt / .out / .prn reads as one sheet with each text line in column A. An .xls / .xlsb is refused with a message:
// the PowerShell side converts it through Excel first.
//
// Writer. XlsxWriter.Write(path, sheets, names) writes a fresh package: shared strings, numbers, booleans, dates,
// a style per distinct XStyle, column widths, frozen panes, merged cells, auto filters, tables, list validations,
// "cell equals" conditional formats, tab colours and print setup. Written to a temp file, then swapped in.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace CalcFlow
{
    // ===================================================================================== reading

    public sealed class XSheet
    {
        public string Name = "";
        public readonly List<string[]> Rows = new List<string[]>();   // Rows[r] = cells of spreadsheet row r+1 (null = none)
        public int MaxCols = 256;

        public string Cell(int row, int col)
        {
            if (row < 0 || row >= Rows.Count) return null;
            string[] r = Rows[row];
            return r == null || col < 0 || col >= r.Length ? null : r[col];
        }

        public string Text(int row, int col)
        {
            string s = Cell(row, col);
            return s == null ? "" : s.Trim();
        }

        public int Width(int row)
        {
            if (row < 0 || row >= Rows.Count || Rows[row] == null) return 0;
            string[] r = Rows[row];
            for (int c = r.Length - 1; c >= 0; c--) if (!string.IsNullOrEmpty(r[c])) return c + 1;
            return 0;
        }

        public void Set(int row, int col, string value)
        {
            if (col >= MaxCols || row < 0 || col < 0) return;
            while (Rows.Count <= row) Rows.Add(null);
            string[] r = Rows[row];
            if (r == null || r.Length <= col)
            {
                var n = new string[Math.Min(MaxCols, Math.Max(col + 1, r == null ? 8 : r.Length * 2))];
                if (r != null) Array.Copy(r, n, r.Length);
                r = n;
                Rows[row] = r;
            }
            r[col] = value;
        }

        // One text line per row: the row's non-empty cells joined by two spaces (a pasted printout reads the same
        // whether it sits in one column or is split across columns).
        public List<string> Lines()
        {
            var lines = new List<string>(Rows.Count);
            var parts = new List<string>();
            foreach (string[] r in Rows)
            {
                parts.Clear();
                if (r != null)
                    foreach (string c in r)
                        if (c != null && c.Trim().Length > 0) parts.Add(c);
                lines.Add(string.Join("  ", parts.ToArray()));
            }
            return lines;
        }
    }

    public sealed class XTable
    {
        public string Name = "";
        public string Sheet = "";
        public int Top, Left, Bottom, Right;      // 0-based, inclusive; Top = header row
        public int TotalsRows;
        public string[] Columns = new string[0];
    }

    public sealed class XWorkbook
    {
        public string Path = "";
        public readonly List<XSheet> Sheets = new List<XSheet>();
        public readonly List<XTable> Tables = new List<XTable>();
        public readonly Dictionary<string, string> Names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public XSheet Sheet(string name)
        {
            foreach (XSheet s in Sheets) if (string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) return s;
            return null;
        }

        public XTable Table(string name)
        {
            foreach (XTable t in Tables) if (string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) return t;
            return null;
        }

        // Rows of a table as header -> trimmed text (case-insensitive keys). "__row" holds the 1-based sheet row.
        // Rows whose cells are all blank are skipped.
        public List<Dictionary<string, string>> TableRows(string name)
        {
            XTable t = Table(name);
            if (t == null) return null;
            XSheet s = Sheet(t.Sheet);
            var rows = new List<Dictionary<string, string>>();
            if (s == null) return rows;
            int last = t.Bottom - t.TotalsRows;
            for (int r = t.Top + 1; r <= last; r++)
            {
                var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                bool any = false;
                for (int c = t.Left; c <= t.Right; c++)
                {
                    int k = c - t.Left;
                    string col = k < t.Columns.Length ? t.Columns[k] : ("Column" + (k + 1).ToString(CultureInfo.InvariantCulture));
                    string v = s.Text(r, c);
                    if (v.Length > 0) any = true;
                    d[col] = v;
                }
                if (!any) continue;
                d["__row"] = (r + 1).ToString(CultureInfo.InvariantCulture);
                rows.Add(d);
            }
            return rows;
        }

        // A header-row table found by its column names (fallback when a workbook carries no table parts):
        // the first row within the first 20 of the sheet that contains every required name.
        public XTable FindHeaderTable(string sheetName, string[] required)
        {
            XSheet s = Sheet(sheetName);
            if (s == null) return null;
            for (int r = 0; r < Math.Min(20, s.Rows.Count); r++)
            {
                int w = s.Width(r);
                var names = new List<string>();
                for (int c = 0; c < w; c++) names.Add(s.Text(r, c));
                bool all = required.All(q => names.Any(n => string.Equals(n, q, StringComparison.OrdinalIgnoreCase)));
                if (!all) continue;
                int left = 0;
                while (left < w && names[left].Length == 0) left++;
                int right = w - 1;
                int bottom = r;
                for (int rr = r + 1; rr < s.Rows.Count; rr++) if (s.Width(rr) > 0) bottom = rr;
                var t = new XTable();
                t.Name = sheetName; t.Sheet = sheetName; t.Top = r; t.Left = left; t.Right = right; t.Bottom = Math.Max(bottom, r);
                t.Columns = names.Skip(left).ToArray();
                return t;
            }
            return null;
        }

        // Value of a defined name that points at one cell ('Sheet name'!$B$5); null when it does not.
        public string NameValue(string name)
        {
            string refText;
            if (!Names.TryGetValue(name, out refText)) return null;
            Match m = Regex.Match(refText, @"^\s*=?\s*(?:'((?:[^']|'')+)'|([^!]+))!\$?([A-Za-z]{1,3})\$?(\d+)\s*$");
            if (!m.Success) return null;
            string sheet = m.Groups[1].Success ? m.Groups[1].Value.Replace("''", "'") : m.Groups[2].Value;
            XSheet s = Sheet(sheet);
            if (s == null) return null;
            int col, row;
            if (!Xlsx.ParseRef(m.Groups[3].Value + m.Groups[4].Value, out col, out row)) return null;
            return s.Text(row, col);
        }
    }

    public static class Xlsx
    {
        public static XWorkbook Read(string path) { return Read(path, 256); }

        public static XWorkbook Read(string path, int maxCols)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) throw new Exception("File not found: " + path);
            var wb = new XWorkbook();
            wb.Path = path;
            byte[] head = new byte[8];
            int n;
            using (var fs = SharedFile.OpenRead(path)) n = fs.Read(head, 0, head.Length);
            if (n >= 4 && head[0] == 0xD0 && head[1] == 0xCF && head[2] == 0x11 && head[3] == 0xE0)
                throw new Exception(path + " is an Excel 97-2003 workbook (.xls): it has to be converted to .xlsx first.");
            if (n >= 2 && head[0] == (byte)'P' && head[1] == (byte)'K')
            {
                using (var fs = SharedFile.OpenRead(path)) ReadXlsx(fs, wb, maxCols);
                return wb;
            }
            string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".csv") wb.Sheets.Add(ReadCsv(path, maxCols));
            else wb.Sheets.Add(ReadText(path));
            return wb;
        }

        // ---- xlsx --------------------------------------------------------------------------------
        static void ReadXlsx(Stream s, XWorkbook wb, int maxCols)
        {
            using (var zip = new ZipArchive(s, ZipArchiveMode.Read, true))
            {
                string wbPath = null;
                foreach (string[] r in Rels(zip, "_rels/.rels", ""))
                    if (r[1].EndsWith("/officeDocument", StringComparison.OrdinalIgnoreCase)) { wbPath = r[2]; break; }
                if (wbPath == null || Entry(zip, wbPath) == null) wbPath = "xl/workbook.xml";
                if (wbPath.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) || (Entry(zip, wbPath) == null && Entry(zip, "xl/workbook.bin") != null))
                    throw new Exception("A binary workbook (.xlsb): it has to be converted to .xlsx first.");
                if (Entry(zip, wbPath) == null) throw new Exception("No workbook part in the .xlsx (not an Excel workbook?)");
                string wbDir = DirOf(wbPath);
                var idToTarget = new Dictionary<string, string>(StringComparer.Ordinal);
                string ssPath = null;
                foreach (string[] r in Rels(zip, wbDir + "_rels/" + FileOf(wbPath) + ".rels", wbDir))
                {
                    idToTarget[r[0]] = r[2];
                    if (r[1].EndsWith("/sharedStrings", StringComparison.OrdinalIgnoreCase)) ssPath = r[2];
                }
                if (ssPath == null && Entry(zip, wbDir + "sharedStrings.xml") != null) ssPath = wbDir + "sharedStrings.xml";
                List<string> shared = ssPath != null ? SharedStrings(zip, ssPath) : new List<string>();

                var order = new List<string[]>();
                string dnName = null;
                var dnText = new StringBuilder();
                using (XmlReader xr = Xml(Entry(zip, wbPath)))
                    while (xr.Read())
                    {
                        if (xr.NodeType == XmlNodeType.Element && xr.LocalName == "sheet")
                        {
                            string name = xr.GetAttribute("name"), rid = null;
                            if (xr.MoveToFirstAttribute())
                            {
                                do { if (xr.LocalName == "id" && xr.NamespaceURI.Length > 0) rid = xr.Value; } while (xr.MoveToNextAttribute());
                                xr.MoveToElement();
                            }
                            order.Add(new[] { DecodeX(name ?? ""), rid ?? "" });
                        }
                        else if (xr.NodeType == XmlNodeType.Element && xr.LocalName == "definedName")
                        {
                            // workbook-level names only (sheet-local names such as print titles are skipped)
                            dnName = xr.GetAttribute("localSheetId") == null ? xr.GetAttribute("name") : null;
                            dnText.Length = 0;
                            if (xr.IsEmptyElement) dnName = null;
                        }
                        else if (dnName != null && (xr.NodeType == XmlNodeType.Text || xr.NodeType == XmlNodeType.CDATA || xr.NodeType == XmlNodeType.Whitespace || xr.NodeType == XmlNodeType.SignificantWhitespace))
                            dnText.Append(xr.Value);
                        else if (xr.NodeType == XmlNodeType.EndElement && xr.LocalName == "definedName")
                        {
                            if (!string.IsNullOrEmpty(dnName) && !wb.Names.ContainsKey(dnName)) wb.Names[dnName] = dnText.ToString();
                            dnName = null;
                        }
                    }
                int k = 0;
                foreach (string[] sh in order)
                {
                    k++;
                    string target;
                    if (!idToTarget.TryGetValue(sh[1], out target)) target = wbDir + "worksheets/sheet" + k.ToString(CultureInfo.InvariantCulture) + ".xml";
                    ZipArchiveEntry e = Entry(zip, target);
                    if (e == null) continue;
                    var sheet = new XSheet();
                    sheet.Name = sh[0];
                    sheet.MaxCols = maxCols;
                    ReadSheet(e, shared, sheet);
                    wb.Sheets.Add(sheet);
                    foreach (string[] r in Rels(zip, DirOf(target) + "_rels/" + FileOf(target) + ".rels", DirOf(target)))
                        if (r[1].EndsWith("/table", StringComparison.OrdinalIgnoreCase))
                        {
                            XTable t = ReadTable(zip, r[2], sheet.Name);
                            if (t != null) wb.Tables.Add(t);
                        }
                }
            }
        }

        static XTable ReadTable(ZipArchive zip, string path, string sheet)
        {
            ZipArchiveEntry e = Entry(zip, path);
            if (e == null) return null;
            var t = new XTable();
            t.Sheet = sheet;
            var cols = new List<string>();
            using (XmlReader xr = Xml(e))
                while (xr.Read())
                {
                    if (xr.NodeType != XmlNodeType.Element) continue;
                    if (xr.LocalName == "table")
                    {
                        t.Name = xr.GetAttribute("displayName") ?? xr.GetAttribute("name") ?? "";
                        string rf = xr.GetAttribute("ref") ?? "";
                        string[] p = rf.Split(':');
                        int c1, r1, c2, r2;
                        if (!ParseRef(p[0], out c1, out r1)) return null;
                        if (p.Length < 2 || !ParseRef(p[1], out c2, out r2)) { c2 = c1; r2 = r1; }
                        t.Top = r1; t.Left = c1; t.Bottom = r2; t.Right = c2;
                        int tr;
                        if (int.TryParse(xr.GetAttribute("totalsRowCount") ?? "0", NumberStyles.Integer, CultureInfo.InvariantCulture, out tr)) t.TotalsRows = tr;
                    }
                    else if (xr.LocalName == "tableColumn") cols.Add(DecodeX(xr.GetAttribute("name") ?? ""));
                }
            t.Columns = cols.ToArray();
            return t;
        }

        static XmlReader Xml(ZipArchiveEntry e)
        {
            var st = new XmlReaderSettings();
            st.DtdProcessing = DtdProcessing.Prohibit;
            st.XmlResolver = null;
            st.IgnoreComments = true;
            st.IgnoreProcessingInstructions = true;
            st.CloseInput = true;
            return XmlReader.Create(e.Open(), st);
        }

        static ZipArchiveEntry Entry(ZipArchive zip, string path)
        {
            if (path == null) return null;
            string p = path.Replace('\\', '/').TrimStart('/');
            ZipArchiveEntry e = zip.GetEntry(p);
            if (e != null) return e;
            foreach (ZipArchiveEntry x in zip.Entries)
                if (string.Equals(x.FullName.Replace('\\', '/').TrimStart('/'), p, StringComparison.OrdinalIgnoreCase)) return x;
            return null;
        }

        static string DirOf(string p)
        {
            int i = p.LastIndexOf('/');
            return i < 0 ? "" : p.Substring(0, i + 1);
        }

        static string FileOf(string p)
        {
            int i = p.LastIndexOf('/');
            return i < 0 ? p : p.Substring(i + 1);
        }

        static List<string[]> Rels(ZipArchive zip, string relsPath, string baseDir)
        {
            var list = new List<string[]>();
            ZipArchiveEntry e = Entry(zip, relsPath);
            if (e == null) return list;
            using (XmlReader xr = Xml(e))
                while (xr.Read())
                {
                    if (xr.NodeType != XmlNodeType.Element || xr.LocalName != "Relationship") continue;
                    if (string.Equals(xr.GetAttribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase)) continue;
                    string t = xr.GetAttribute("Target") ?? "";
                    list.Add(new[] { xr.GetAttribute("Id") ?? "", xr.GetAttribute("Type") ?? "", Resolve(baseDir, t) });
                }
            return list;
        }

        static string Resolve(string baseDir, string target)
        {
            string t = target.Replace('\\', '/');
            if (t.StartsWith("/", StringComparison.Ordinal)) return t.TrimStart('/');
            var outp = new List<string>();
            foreach (string p in (baseDir + t).Split('/'))
            {
                if (p.Length == 0 || p == ".") continue;
                if (p == "..") { if (outp.Count > 0) outp.RemoveAt(outp.Count - 1); continue; }
                outp.Add(p);
            }
            return string.Join("/", outp.ToArray());
        }

        static List<string> SharedStrings(ZipArchive zip, string path)
        {
            var list = new List<string>();
            ZipArchiveEntry e = Entry(zip, path);
            if (e == null) return list;
            var sb = new StringBuilder();
            bool inSi = false, inT = false;
            int phonetic = 0;
            using (XmlReader xr = Xml(e))
                while (xr.Read())
                {
                    switch (xr.NodeType)
                    {
                        case XmlNodeType.Element:
                            if (xr.LocalName == "si") { inSi = true; sb.Length = 0; if (xr.IsEmptyElement) { list.Add(""); inSi = false; } }
                            else if (xr.LocalName == "rPh" && !xr.IsEmptyElement) phonetic++;
                            else if (xr.LocalName == "t" && inSi && !xr.IsEmptyElement) inT = true;
                            break;
                        case XmlNodeType.Text:
                        case XmlNodeType.CDATA:
                        case XmlNodeType.Whitespace:
                        case XmlNodeType.SignificantWhitespace:
                            if (inT && phonetic == 0) sb.Append(xr.Value);
                            break;
                        case XmlNodeType.EndElement:
                            if (xr.LocalName == "t") inT = false;
                            else if (xr.LocalName == "rPh") phonetic = Math.Max(0, phonetic - 1);
                            else if (xr.LocalName == "si") { list.Add(DecodeX(sb.ToString())); inSi = false; }
                            break;
                    }
                }
            return list;
        }

        static void ReadSheet(ZipArchiveEntry e, List<string> shared, XSheet sheet)
        {
            int row = -1, col = -1;
            string type = null;
            bool inCell = false, inV = false, inT = false, inSheetData = false;
            int phonetic = 0;
            var v = new StringBuilder();
            var t = new StringBuilder();
            using (XmlReader xr = Xml(e))
                while (xr.Read())
                {
                    switch (xr.NodeType)
                    {
                        case XmlNodeType.Element:
                            switch (xr.LocalName)
                            {
                                case "sheetData": inSheetData = !xr.IsEmptyElement; break;
                                case "row":
                                    if (!inSheetData) break;
                                    int rn;
                                    string ra = xr.GetAttribute("r");
                                    row = ra != null && int.TryParse(ra, NumberStyles.Integer, CultureInfo.InvariantCulture, out rn) && rn > 0 ? rn - 1 : row + 1;
                                    col = -1;
                                    break;
                                case "c":
                                    if (!inSheetData) break;
                                    string cr = xr.GetAttribute("r");
                                    int cc, crow;
                                    if (ParseRef(cr, out cc, out crow)) { col = cc; if (crow >= 0) row = crow; } else col++;
                                    if (row < 0) row = 0;
                                    type = xr.GetAttribute("t");
                                    v.Length = 0; t.Length = 0;
                                    inCell = !xr.IsEmptyElement;
                                    break;
                                case "v": if (inCell && !xr.IsEmptyElement) inV = true; break;
                                case "t": if (inCell && !xr.IsEmptyElement) inT = true; break;
                                case "rPh": if (inCell && !xr.IsEmptyElement) phonetic++; break;
                            }
                            break;
                        case XmlNodeType.Text:
                        case XmlNodeType.CDATA:
                        case XmlNodeType.Whitespace:
                        case XmlNodeType.SignificantWhitespace:
                            if (inV) v.Append(xr.Value);
                            else if (inT && phonetic == 0) t.Append(xr.Value);
                            break;
                        case XmlNodeType.EndElement:
                            switch (xr.LocalName)
                            {
                                case "sheetData": inSheetData = false; break;
                                case "v": inV = false; break;
                                case "t": inT = false; break;
                                case "rPh": phonetic = Math.Max(0, phonetic - 1); break;
                                case "c":
                                    if (inCell)
                                    {
                                        string val = null;
                                        if (type == "s")
                                        {
                                            int si;
                                            if (int.TryParse(v.ToString().Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out si) && si >= 0 && si < shared.Count) val = shared[si];
                                        }
                                        else if (type == "inlineStr") val = DecodeX(t.ToString());
                                        else if (type == "b") val = v.ToString().Trim() == "1" ? "TRUE" : "FALSE";
                                        else if (type == "str") val = DecodeX(v.ToString());
                                        else if (v.Length > 0) val = v.ToString();
                                        else if (t.Length > 0) val = DecodeX(t.ToString());
                                        if (val != null) sheet.Set(row, col, val);
                                    }
                                    inCell = false;
                                    break;
                            }
                            break;
                    }
                }
        }

        // "AB12" -> col 27, row 11 (0-based); a reference without digits gives row -1
        public static bool ParseRef(string r, out int col, out int row)
        {
            col = -1; row = -1;
            if (string.IsNullOrEmpty(r)) return false;
            string s = r.Replace("$", "");
            int i = 0, c = 0;
            while (i < s.Length && char.IsLetter(s[i])) { c = c * 26 + (char.ToUpperInvariant(s[i]) - 'A' + 1); i++; }
            if (i == 0 || c <= 0) return false;
            col = c - 1;
            int rn;
            if (i < s.Length && int.TryParse(s.Substring(i), NumberStyles.Integer, CultureInfo.InvariantCulture, out rn) && rn > 0) row = rn - 1;
            return true;
        }

        // Excel escapes characters that XML cannot carry as _xHHHH_ (e.g. _x000D_ for a carriage return).
        public static string DecodeX(string s)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOf("_x", StringComparison.Ordinal) < 0) return s;
            return Regex.Replace(s, "_x([0-9A-Fa-f]{4})_", m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
        }

        // ---- csv / text -------------------------------------------------------------------------------
        public static XSheet ReadCsv(string path, int maxCols)
        {
            string text;
            using (var fs = SharedFile.OpenRead(path))
            using (var sr = new StreamReader(fs, Encoding.UTF8, true)) text = sr.ReadToEnd();
            var sheet = new XSheet();
            sheet.Name = System.IO.Path.GetFileNameWithoutExtension(path);
            sheet.MaxCols = maxCols;
            char sep = Separator(text);
            int row = 0, col = 0;
            var cell = new StringBuilder();
            bool quoted = false, any = false;
            for (int i = 0; i <= text.Length; i++)
            {
                char ch = i < text.Length ? text[i] : '\n';
                if (quoted)
                {
                    if (ch == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                        else quoted = false;
                    }
                    else cell.Append(ch);
                    continue;
                }
                if (ch == '"') { quoted = true; any = true; continue; }
                if (ch == sep) { if (cell.Length > 0 || any) sheet.Set(row, col, cell.ToString()); cell.Length = 0; any = false; col++; continue; }
                if (ch == '\r') continue;
                if (ch == '\n')
                {
                    if (cell.Length > 0 || any) sheet.Set(row, col, cell.ToString());
                    cell.Length = 0; any = false; col = 0; row++;
                    continue;
                }
                cell.Append(ch);
            }
            return sheet;
        }

        static char Separator(string text)
        {
            int n = 0, commas = 0, semis = 0, tabs = 0;
            bool quoted = false;
            foreach (char ch in text)
            {
                if (ch == '"') quoted = !quoted;
                else if (quoted) continue;
                else if (ch == ',') commas++;
                else if (ch == ';') semis++;
                else if (ch == '\t') tabs++;
                else if (ch == '\n' && ++n > 200) break;
            }
            if (tabs > commas && tabs > semis) return '\t';
            return semis > commas ? ';' : ',';
        }

        public static XSheet ReadText(string path)
        {
            var sheet = new XSheet();
            sheet.Name = System.IO.Path.GetFileNameWithoutExtension(path);
            string text;
            using (var fs = SharedFile.OpenRead(path))
            using (var sr = new StreamReader(fs, Encoding.UTF8, true)) text = sr.ReadToEnd();
            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int i = 0; i < lines.Length; i++)
                if (lines[i].Length > 0) sheet.Set(i, 0, lines[i].Replace('\t', ' ').Replace('\f', ' '));
            return sheet;
        }

        public static string ColName(int col)
        {
            int c = col + 1;
            var sb = new StringBuilder();
            while (c > 0) { int m = (c - 1) % 26; sb.Insert(0, (char)('A' + m)); c = (c - 1) / 26; }
            return sb.ToString();
        }

        public static string Ref(int row, int col) { return ColName(col) + (row + 1).ToString(CultureInfo.InvariantCulture); }
    }

    // ===================================================================================== files

    public static class SharedFile
    {
        // FileShare.ReadWrite|Delete: Excel holds a workbook it has open; reading the last saved copy still works
        public static FileStream OpenRead(string path)
        {
            try { return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
            catch (IOException ex)
            {
                throw new Exception("Cannot open " + path + " (" + ex.Message + "). Close it and run again.");
            }
        }

        public static bool IsLocked(string path)
        {
            if (!File.Exists(path)) return false;
            try { using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } return false; }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
        }

        public static string Sha256(string path)
        {
            using (var fs = OpenRead(path))
            using (var h = SHA256.Create()) return Hex(h.ComputeHash(fs));
        }

        public static string Sha256Text(string text)
        {
            using (var h = SHA256.Create()) return Hex(h.ComputeHash(Encoding.UTF8.GetBytes(text ?? "")));
        }

        static string Hex(byte[] b)
        {
            var sb = new StringBuilder(b.Length * 2);
            foreach (byte x in b) sb.Append(x.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        // Write to a temp file beside the target, then swap it in (retrying briefly: antivirus and sync tools
        // hold freshly written files for a moment).
        public static void ReplaceWith(string tempPath, string path)
        {
            Exception last = null;
            for (int i = 0; i < 20; i++)
            {
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                    File.Move(tempPath, path);
                    return;
                }
                catch (IOException ex) { last = ex; }
                catch (UnauthorizedAccessException ex) { last = ex; }
                System.Threading.Thread.Sleep(250);
            }
            throw new Exception("Cannot replace " + path + " (" + (last == null ? "" : last.Message) + "). Is it open in Excel?");
        }

        public static string TempBeside(string path)
        {
            string dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            return System.IO.Path.Combine(dir ?? ".", "~pr-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp");
        }

        public static void WriteAllText(string path, string text)
        {
            string tmp = TempBeside(path);
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            ReplaceWith(tmp, path);
        }
    }

    // ===================================================================================== writing

    public sealed class XStyle
    {
        public bool Bold, Italic, Underline;
        public double Size;                // 0 = default 11
        public string Color;               // font colour, ARGB hex "FF1F3864"
        public string Fill;                // solid fill ARGB hex
        public string NumFmt;              // "0.000", "0%", "yyyy-mm-dd", "@"; null = General
        public bool Wrap;
        public bool Border;                // thin grey box
        public string HAlign;              // left | center | right
        public string VAlign;              // top | center | bottom

        public XStyle Copy()
        {
            return (XStyle)MemberwiseClone();
        }

        internal string FontKey() { return (Bold ? "b" : "") + (Italic ? "i" : "") + (Underline ? "u" : "") + "|" + Size.ToString("R", CultureInfo.InvariantCulture) + "|" + (Color ?? ""); }
        internal string Key()
        {
            return FontKey() + "|" + (Fill ?? "") + "|" + (NumFmt ?? "") + "|" + (Wrap ? "w" : "") + "|" + (Border ? "B" : "") + "|" + (HAlign ?? "") + "|" + (VAlign ?? "");
        }
    }

    public sealed class XCell
    {
        public object Value;
        public XStyle Style;
        public XCell(object value, XStyle style) { Value = value; Style = style; }
    }

    public sealed class XOutTable
    {
        public string Name = "";
        public int Top, Left, Bottom, Right;     // 0-based inclusive; Top = header row (must hold the column names)
        public string Style = "TableStyleLight9";
    }

    public sealed class XValidation
    {
        public string Sqref = "";              // "D5:D2000"
        public string List = "";               // "\"Y,N\"" (literal) or a defined name / range formula
        public bool Strict = true;             // false = dropdown only, any value accepted
        public string PromptTitle, Prompt;
    }

    public sealed class XCondFormat
    {
        public string Sqref = "";
        public string EqualsText = "";         // cell value equals this text
        public XStyle Style;                   // only Fill, Color, Bold are used
    }

    public sealed class XOutSheet
    {
        public string Name = "Sheet1";
        public string TabColor;
        public readonly List<object[]> Rows = new List<object[]>();
        public readonly Dictionary<int, double> ColWidths = new Dictionary<int, double>();
        public readonly Dictionary<int, double> RowHeights = new Dictionary<int, double>();   // row index -> points
        public readonly HashSet<int> HiddenCols = new HashSet<int>();
        public int FreezeRows, FreezeCols;
        public string AutoFilter;
        public readonly List<string> Merges = new List<string>();
        public readonly List<XOutTable> Tables = new List<XOutTable>();
        public readonly List<XValidation> Validations = new List<XValidation>();
        public readonly List<XCondFormat> CondFormats = new List<XCondFormat>();
        public bool Landscape, FitToWidth;
        public int PrintTitleFirstRow, PrintTitleLastRow;   // 1-based; 0 = none

        public XOutSheet() { }
        public XOutSheet(string name) { Name = name; }

        public void Add(params object[] cells) { Rows.Add(cells ?? new object[0]); }

        public void Set(int row, int col, object value)
        {
            while (Rows.Count <= row) Rows.Add(new object[0]);
            object[] r = Rows[row];
            if (r.Length <= col)
            {
                var n = new object[col + 1];
                Array.Copy(r, n, r.Length);
                r = n;
                Rows[row] = r;
            }
            r[col] = value;
        }

        public void Width(int col, double width) { ColWidths[col] = width; }
    }

    public static class XlsxWriter
    {
        const string NsMain = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        const string NsRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        const string NsPkg = "http://schemas.openxmlformats.org/package/2006/relationships";

        sealed class Styles
        {
            public readonly List<string> Fonts = new List<string>();
            public readonly List<XStyle> FontStyles = new List<XStyle>();
            public readonly List<string> Fills = new List<string>();
            public readonly List<string> NumFmts = new List<string>();
            public readonly List<string> XfKeys = new List<string>();
            public readonly List<XStyle> Xfs = new List<XStyle>();
            public readonly List<XStyle> Dxfs = new List<XStyle>();
            public Styles()
            {
                var d = new XStyle();
                Fonts.Add(d.FontKey()); FontStyles.Add(d);
                XfKeys.Add(d.Key()); Xfs.Add(d);
            }
            public int Xf(XStyle s)
            {
                if (s == null) return 0;
                string k = s.Key();
                int i = XfKeys.IndexOf(k);
                if (i >= 0) return i;
                if (Fonts.IndexOf(s.FontKey()) < 0) { Fonts.Add(s.FontKey()); FontStyles.Add(s); }
                if (s.Fill != null && Fills.IndexOf(s.Fill) < 0) Fills.Add(s.Fill);
                if (s.NumFmt != null && BuiltIn(s.NumFmt) < 0 && NumFmts.IndexOf(s.NumFmt) < 0) NumFmts.Add(s.NumFmt);
                XfKeys.Add(k); Xfs.Add(s);
                return Xfs.Count - 1;
            }
            public int Dxf(XStyle s) { Dxfs.Add(s); return Dxfs.Count - 1; }
            public int FontId(XStyle s) { return Math.Max(0, Fonts.IndexOf(s.FontKey())); }
            public int FillId(XStyle s) { return s.Fill == null ? 0 : Fills.IndexOf(s.Fill) + 2; }   // 0 none, 1 gray125 reserved
            public int NumFmtId(XStyle s)
            {
                if (s.NumFmt == null) return 0;
                int b = BuiltIn(s.NumFmt);
                return b >= 0 ? b : 164 + NumFmts.IndexOf(s.NumFmt);
            }
            static int BuiltIn(string f)
            {
                switch (f)
                {
                    case "General": return 0;
                    case "0": return 1;
                    case "0.00": return 2;
                    case "#,##0": return 3;
                    case "#,##0.00": return 4;
                    case "0%": return 9;
                    case "0.00%": return 10;
                    case "@": return 49;
                }
                return -1;
            }
        }

        public static void Write(string path, IList<XOutSheet> sheets, IDictionary<string, string> names)
        {
            if (sheets == null || sheets.Count == 0) throw new Exception("XlsxWriter: no sheets");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (XOutSheet s in sheets)
            {
                if (string.IsNullOrEmpty(s.Name) || s.Name.Length > 31 || s.Name.IndexOfAny(new[] { ':', '\\', '/', '?', '*', '[', ']' }) >= 0)
                    throw new Exception("XlsxWriter: invalid sheet name '" + s.Name + "'");
                if (!seen.Add(s.Name)) throw new Exception("XlsxWriter: duplicate sheet name '" + s.Name + "'");
            }
            var styles = new Styles();
            var sst = new List<string>();
            var sstIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            int sstCount = 0;
            var sheetXml = new List<byte[]>();
            var tableXml = new List<KeyValuePair<int, byte[]>>();   // sheet index -> table part
            int tableId = 0;
            foreach (XOutSheet s in sheets)
            {
                int sheetIndex = sheetXml.Count;
                var parts = new List<byte[]>();
                foreach (XOutTable t in s.Tables)
                {
                    tableId++;
                    parts.Add(TablePart(s, t, tableId));
                }
                sheetXml.Add(SheetPart(s, styles, sst, sstIndex, ref sstCount, sheetIndex == 0, parts.Count));
                foreach (byte[] p in parts) tableXml.Add(new KeyValuePair<int, byte[]>(sheetIndex, p));
            }

            string tmp = SharedFile.TempBeside(path);
            try
            {
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
                {
                    Put(zip, "[Content_Types].xml", ContentTypes(sheets.Count, tableXml.Count));
                    Put(zip, "_rels/.rels", RootRels());
                    Put(zip, "xl/workbook.xml", WorkbookPart(sheets, names));
                    Put(zip, "xl/_rels/workbook.xml.rels", WorkbookRels(sheets.Count));
                    Put(zip, "xl/styles.xml", StylesPart(styles));
                    Put(zip, "xl/sharedStrings.xml", SharedStringsPart(sst, sstCount));
                    int t = 0;
                    for (int i = 0; i < sheetXml.Count; i++)
                    {
                        Put(zip, "xl/worksheets/sheet" + (i + 1).ToString(CultureInfo.InvariantCulture) + ".xml", sheetXml[i]);
                        var mine = tableXml.Where(kv => kv.Key == i).ToList();
                        if (mine.Count == 0) continue;
                        var rels = new StringBuilder();
                        rels.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
                        rels.Append("<Relationships xmlns=\"").Append(NsPkg).Append("\">");
                        for (int j = 0; j < mine.Count; j++)
                        {
                            t++;
                            rels.Append("<Relationship Id=\"rId").Append(j + 1).Append("\" Type=\"").Append(NsRel).Append("/table\" Target=\"../tables/table").Append(t).Append(".xml\"/>");
                            Put(zip, "xl/tables/table" + t.ToString(CultureInfo.InvariantCulture) + ".xml", mine[j].Value);
                        }
                        rels.Append("</Relationships>");
                        Put(zip, "xl/worksheets/_rels/sheet" + (i + 1).ToString(CultureInfo.InvariantCulture) + ".xml.rels", Encoding.UTF8.GetBytes(rels.ToString()));
                    }
                }
                SharedFile.ReplaceWith(tmp, path);
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { }
                throw;
            }
        }

        static void Put(ZipArchive zip, string name, byte[] data)
        {
            ZipArchiveEntry e = zip.CreateEntry(name, CompressionLevel.Optimal);
            using (Stream s = e.Open()) s.Write(data, 0, data.Length);
        }

        static byte[] ContentTypes(int sheets, int tables)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
            sb.Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>");
            sb.Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>");
            sb.Append("<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>");
            sb.Append("<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
            sb.Append("<Override PartName=\"/xl/sharedStrings.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml\"/>");
            for (int i = 1; i <= sheets; i++)
                sb.Append("<Override PartName=\"/xl/worksheets/sheet").Append(i).Append(".xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
            for (int i = 1; i <= tables; i++)
                sb.Append("<Override PartName=\"/xl/tables/table").Append(i).Append(".xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.table+xml\"/>");
            sb.Append("</Types>");
            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        static byte[] RootRels()
        {
            return Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"" + NsPkg + "\"><Relationship Id=\"rId1\" Type=\"" + NsRel + "/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
        }

        static byte[] WorkbookRels(int sheets)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<Relationships xmlns=\"").Append(NsPkg).Append("\">");
            for (int i = 1; i <= sheets; i++)
                sb.Append("<Relationship Id=\"rId").Append(i).Append("\" Type=\"").Append(NsRel).Append("/worksheet\" Target=\"worksheets/sheet").Append(i).Append(".xml\"/>");
            sb.Append("<Relationship Id=\"rId").Append(sheets + 1).Append("\" Type=\"").Append(NsRel).Append("/styles\" Target=\"styles.xml\"/>");
            sb.Append("<Relationship Id=\"rId").Append(sheets + 2).Append("\" Type=\"").Append(NsRel).Append("/sharedStrings\" Target=\"sharedStrings.xml\"/>");
            sb.Append("</Relationships>");
            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        static byte[] WorkbookPart(IList<XOutSheet> sheets, IDictionary<string, string> names)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<workbook xmlns=\"").Append(NsMain).Append("\" xmlns:r=\"").Append(NsRel).Append("\">");
            sb.Append("<workbookPr/><bookViews><workbookView activeTab=\"0\"/></bookViews><sheets>");
            for (int i = 0; i < sheets.Count; i++)
                sb.Append("<sheet name=\"").Append(Esc(sheets[i].Name)).Append("\" sheetId=\"").Append(i + 1).Append("\" r:id=\"rId").Append(i + 1).Append("\"/>");
            sb.Append("</sheets>");
            var defs = new List<string>();
            for (int i = 0; i < sheets.Count; i++)
            {
                XOutSheet s = sheets[i];
                if (s.AutoFilter != null)
                    defs.Add("<definedName name=\"_xlnm._FilterDatabase\" localSheetId=\"" + i.ToString(CultureInfo.InvariantCulture) + "\" hidden=\"1\">" + Esc(SheetRef(s.Name) + "!" + Absolute(s.AutoFilter)) + "</definedName>");
                if (s.PrintTitleFirstRow > 0)
                    defs.Add("<definedName name=\"_xlnm.Print_Titles\" localSheetId=\"" + i.ToString(CultureInfo.InvariantCulture) + "\">" + Esc(SheetRef(s.Name) + "!$" + s.PrintTitleFirstRow.ToString(CultureInfo.InvariantCulture) + ":$" + Math.Max(s.PrintTitleFirstRow, s.PrintTitleLastRow).ToString(CultureInfo.InvariantCulture)) + "</definedName>");
            }
            if (names != null)
                foreach (var kv in names.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                    defs.Add("<definedName name=\"" + Esc(kv.Key) + "\">" + Esc(kv.Value.TrimStart('=')) + "</definedName>");
            if (defs.Count > 0) sb.Append("<definedNames>").Append(string.Join("", defs.ToArray())).Append("</definedNames>");
            sb.Append("<calcPr calcId=\"191029\" fullCalcOnLoad=\"1\"/>");
            sb.Append("</workbook>");
            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        public static string SheetRef(string name)
        {
            return Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_.]*$") && !Regex.IsMatch(name, @"^[A-Za-z]{1,3}\d+$") ? name : "'" + name.Replace("'", "''") + "'";
        }

        static string Absolute(string range)
        {
            return string.Join(":", range.Split(':').Select(p =>
            {
                int c, r;
                if (!Xlsx.ParseRef(p, out c, out r)) return p;
                return "$" + Xlsx.ColName(c) + (r >= 0 ? "$" + (r + 1).ToString(CultureInfo.InvariantCulture) : "");
            }).ToArray());
        }

        static byte[] StylesPart(Styles st)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<styleSheet xmlns=\"").Append(NsMain).Append("\">");
            if (st.NumFmts.Count > 0)
            {
                sb.Append("<numFmts count=\"").Append(st.NumFmts.Count).Append("\">");
                for (int i = 0; i < st.NumFmts.Count; i++)
                    sb.Append("<numFmt numFmtId=\"").Append(164 + i).Append("\" formatCode=\"").Append(Esc(st.NumFmts[i])).Append("\"/>");
                sb.Append("</numFmts>");
            }
            sb.Append("<fonts count=\"").Append(st.FontStyles.Count).Append("\">");
            foreach (XStyle f in st.FontStyles) sb.Append(FontXml(f));
            sb.Append("</fonts>");
            sb.Append("<fills count=\"").Append(st.Fills.Count + 2).Append("\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill>");
            foreach (string f in st.Fills) sb.Append("<fill><patternFill patternType=\"solid\"><fgColor rgb=\"").Append(f).Append("\"/><bgColor indexed=\"64\"/></patternFill></fill>");
            sb.Append("</fills>");
            sb.Append("<borders count=\"2\"><border><left/><right/><top/><bottom/><diagonal/></border>");
            sb.Append("<border><left style=\"thin\"><color rgb=\"FFBFBFBF\"/></left><right style=\"thin\"><color rgb=\"FFBFBFBF\"/></right><top style=\"thin\"><color rgb=\"FFBFBFBF\"/></top><bottom style=\"thin\"><color rgb=\"FFBFBFBF\"/></bottom><diagonal/></border></borders>");
            sb.Append("<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>");
            sb.Append("<cellXfs count=\"").Append(st.Xfs.Count).Append("\">");
            for (int i = 0; i < st.Xfs.Count; i++)
            {
                XStyle s = st.Xfs[i];
                int numFmt = st.NumFmtId(s), font = st.FontId(s), fill = st.FillId(s), border = s.Border ? 1 : 0;
                sb.Append("<xf numFmtId=\"").Append(numFmt).Append("\" fontId=\"").Append(font).Append("\" fillId=\"").Append(fill).Append("\" borderId=\"").Append(border).Append("\" xfId=\"0\"");
                if (numFmt != 0) sb.Append(" applyNumberFormat=\"1\"");
                if (font != 0) sb.Append(" applyFont=\"1\"");
                if (fill != 0) sb.Append(" applyFill=\"1\"");
                if (border != 0) sb.Append(" applyBorder=\"1\"");
                bool align = s.Wrap || s.HAlign != null || s.VAlign != null;
                if (align)
                {
                    sb.Append(" applyAlignment=\"1\"><alignment");
                    if (s.HAlign != null) sb.Append(" horizontal=\"").Append(s.HAlign).Append("\"");
                    if (s.VAlign != null) sb.Append(" vertical=\"").Append(s.VAlign).Append("\"");
                    if (s.Wrap) sb.Append(" wrapText=\"1\"");
                    sb.Append("/></xf>");
                }
                else sb.Append("/>");
            }
            sb.Append("</cellXfs>");
            sb.Append("<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>");
            sb.Append("<dxfs count=\"").Append(st.Dxfs.Count).Append("\">");
            foreach (XStyle d in st.Dxfs)
            {
                sb.Append("<dxf>");
                if (d.Bold || d.Color != null)
                {
                    sb.Append("<font>");
                    if (d.Bold) sb.Append("<b/>");
                    if (d.Color != null) sb.Append("<color rgb=\"").Append(d.Color).Append("\"/>");
                    sb.Append("</font>");
                }
                if (d.Fill != null) sb.Append("<fill><patternFill patternType=\"solid\"><fgColor rgb=\"").Append(d.Fill).Append("\"/><bgColor rgb=\"").Append(d.Fill).Append("\"/></patternFill></fill>");
                sb.Append("</dxf>");
            }
            sb.Append("</dxfs>");
            sb.Append("<tableStyles count=\"0\" defaultTableStyle=\"TableStyleMedium2\" defaultPivotStyle=\"PivotStyleLight16\"/>");
            sb.Append("</styleSheet>");
            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        static string FontXml(XStyle f)
        {
            var sb = new StringBuilder("<font>");
            if (f.Bold) sb.Append("<b/>");
            if (f.Italic) sb.Append("<i/>");
            if (f.Underline) sb.Append("<u/>");
            sb.Append("<sz val=\"").Append((f.Size > 0 ? f.Size : 11).ToString("R", CultureInfo.InvariantCulture)).Append("\"/>");
            if (f.Color != null) sb.Append("<color rgb=\"").Append(f.Color).Append("\"/>"); else sb.Append("<color theme=\"1\"/>");
            sb.Append("<name val=\"Calibri\"/><family val=\"2\"/><scheme val=\"minor\"/></font>");
            return sb.ToString();
        }

        static byte[] SharedStringsPart(List<string> sst, int count)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<sst xmlns=\"").Append(NsMain).Append("\" count=\"").Append(count).Append("\" uniqueCount=\"").Append(sst.Count).Append("\">");
            foreach (string s in sst) sb.Append("<si><t xml:space=\"preserve\">").Append(Esc(s)).Append("</t></si>");
            sb.Append("</sst>");
            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        static byte[] SheetPart(XOutSheet s, Styles styles, List<string> sst, Dictionary<string, int> sstIndex, ref int sstCount, bool first, int tableCount)
        {
            int maxRow = 0, maxCol = 0;
            for (int r = 0; r < s.Rows.Count; r++)
                if (s.Rows[r] != null && s.Rows[r].Length > 0) { maxRow = r; maxCol = Math.Max(maxCol, s.Rows[r].Length - 1); }
            foreach (XOutTable t in s.Tables) { maxRow = Math.Max(maxRow, t.Bottom); maxCol = Math.Max(maxCol, t.Right); }

            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<worksheet xmlns=\"").Append(NsMain).Append("\" xmlns:r=\"").Append(NsRel).Append("\">");
            if (s.TabColor != null || s.FitToWidth)
            {
                sb.Append("<sheetPr>");
                if (s.TabColor != null) sb.Append("<tabColor rgb=\"").Append(s.TabColor).Append("\"/>");
                if (s.FitToWidth) sb.Append("<pageSetUpPr fitToPage=\"1\"/>");
                sb.Append("</sheetPr>");
            }
            sb.Append("<dimension ref=\"A1:").Append(Xlsx.Ref(maxRow, maxCol)).Append("\"/>");
            sb.Append("<sheetViews><sheetView workbookViewId=\"0\"");
            if (first) sb.Append(" tabSelected=\"1\"");
            sb.Append(">");
            if (s.FreezeRows > 0 || s.FreezeCols > 0)
            {
                string tl = Xlsx.Ref(s.FreezeRows, s.FreezeCols);
                string pane = s.FreezeRows > 0 && s.FreezeCols > 0 ? "bottomRight" : (s.FreezeRows > 0 ? "bottomLeft" : "topRight");
                sb.Append("<pane");
                if (s.FreezeCols > 0) sb.Append(" xSplit=\"").Append(s.FreezeCols).Append("\"");
                if (s.FreezeRows > 0) sb.Append(" ySplit=\"").Append(s.FreezeRows).Append("\"");
                sb.Append(" topLeftCell=\"").Append(tl).Append("\" activePane=\"").Append(pane).Append("\" state=\"frozen\"/>");
                sb.Append("<selection pane=\"").Append(pane).Append("\" activeCell=\"").Append(tl).Append("\" sqref=\"").Append(tl).Append("\"/>");
            }
            sb.Append("</sheetView></sheetViews>");
            sb.Append("<sheetFormatPr defaultRowHeight=\"15\"/>");
            if (s.ColWidths.Count > 0 || s.HiddenCols.Count > 0)
            {
                sb.Append("<cols>");
                var cols = new SortedSet<int>(s.ColWidths.Keys);
                foreach (int h in s.HiddenCols) cols.Add(h);
                foreach (int c in cols)
                {
                    double w;
                    if (!s.ColWidths.TryGetValue(c, out w)) w = 9.140625;
                    sb.Append("<col min=\"").Append(c + 1).Append("\" max=\"").Append(c + 1).Append("\" width=\"").Append(w.ToString("R", CultureInfo.InvariantCulture)).Append("\" customWidth=\"1\"");
                    if (s.HiddenCols.Contains(c)) sb.Append(" hidden=\"1\"");
                    sb.Append("/>");
                }
                sb.Append("</cols>");
            }
            sb.Append("<sheetData>");
            for (int r = 0; r < s.Rows.Count; r++)
            {
                object[] row = s.Rows[r];
                if (row == null || row.Length == 0) continue;
                var cells = new StringBuilder();
                for (int c = 0; c < row.Length; c++)
                {
                    object o = row[c];
                    if (o == null) continue;
                    XStyle style = null;
                    var xc = o as XCell;
                    if (xc != null) { style = xc.Style; o = xc.Value; }
                    int xf = styles.Xf(style);
                    string rf = Xlsx.Ref(r, c);
                    if (o == null || (o is string && ((string)o).Length == 0))
                    {
                        if (xf != 0) cells.Append("<c r=\"").Append(rf).Append("\" s=\"").Append(xf).Append("\"/>");
                        continue;
                    }
                    cells.Append("<c r=\"").Append(rf).Append("\"");
                    if (xf != 0) cells.Append(" s=\"").Append(xf).Append("\"");
                    if (o is string)
                    {
                        string text = Clean((string)o);
                        int idx;
                        if (!sstIndex.TryGetValue(text, out idx)) { idx = sst.Count; sst.Add(text); sstIndex[text] = idx; }
                        sstCount++;
                        cells.Append(" t=\"s\"><v>").Append(idx).Append("</v></c>");
                    }
                    else if (o is bool) cells.Append(" t=\"b\"><v>").Append((bool)o ? "1" : "0").Append("</v></c>");
                    else if (o is DateTime) cells.Append("><v>").Append(((DateTime)o).ToOADate().ToString("R", CultureInfo.InvariantCulture)).Append("</v></c>");
                    else if (!(o is IConvertible))
                    {
                        // anything that is not a plain value (e.g. a PowerShell-wrapped object) is written as its text
                        string text = Clean(o.ToString());
                        int idx;
                        if (!sstIndex.TryGetValue(text, out idx)) { idx = sst.Count; sst.Add(text); sstIndex[text] = idx; }
                        sstCount++;
                        cells.Append(" t=\"s\"><v>").Append(idx).Append("</v></c>");
                    }
                    else
                    {
                        double d = Convert.ToDouble(o, CultureInfo.InvariantCulture);
                        if (double.IsNaN(d) || double.IsInfinity(d)) throw new Exception("XlsxWriter: " + s.Name + "!" + rf + " is not a finite number");
                        cells.Append("><v>").Append(Num(d)).Append("</v></c>");
                    }
                }
                if (cells.Length == 0) continue;
                double ht;
                sb.Append("<row r=\"").Append(r + 1).Append("\"");
                if (s.RowHeights.TryGetValue(r, out ht)) sb.Append(" ht=\"").Append(ht.ToString("R", CultureInfo.InvariantCulture)).Append("\" customHeight=\"1\"");
                sb.Append(">").Append(cells).Append("</row>");
            }
            sb.Append("</sheetData>");
            if (s.AutoFilter != null) sb.Append("<autoFilter ref=\"").Append(s.AutoFilter).Append("\"/>");
            if (s.Merges.Count > 0)
            {
                sb.Append("<mergeCells count=\"").Append(s.Merges.Count).Append("\">");
                foreach (string m in s.Merges) sb.Append("<mergeCell ref=\"").Append(m).Append("\"/>");
                sb.Append("</mergeCells>");
            }
            int prio = 1;
            foreach (XCondFormat cf in s.CondFormats)
            {
                int dxf = styles.Dxf(cf.Style ?? new XStyle());
                sb.Append("<conditionalFormatting sqref=\"").Append(cf.Sqref).Append("\"><cfRule type=\"cellIs\" dxfId=\"").Append(dxf).Append("\" priority=\"").Append(prio++).Append("\" operator=\"equal\"><formula>\"").Append(Esc(cf.EqualsText.Replace("\"", "\"\""))).Append("\"</formula></cfRule></conditionalFormatting>");
            }
            if (s.Validations.Count > 0)
            {
                sb.Append("<dataValidations count=\"").Append(s.Validations.Count).Append("\">");
                foreach (XValidation v in s.Validations)
                {
                    sb.Append("<dataValidation type=\"list\" allowBlank=\"1\" showInputMessage=\"1\" showErrorMessage=\"").Append(v.Strict ? "1" : "0").Append("\"");
                    if (v.PromptTitle != null) sb.Append(" promptTitle=\"").Append(Esc(v.PromptTitle)).Append("\"");
                    if (v.Prompt != null) sb.Append(" prompt=\"").Append(Esc(v.Prompt)).Append("\"");
                    sb.Append(" sqref=\"").Append(v.Sqref).Append("\"><formula1>").Append(Esc(v.List.TrimStart('='))).Append("</formula1></dataValidation>");
                }
                sb.Append("</dataValidations>");
            }
            sb.Append("<pageMargins left=\"0.5\" right=\"0.5\" top=\"0.6\" bottom=\"0.6\" header=\"0.3\" footer=\"0.3\"/>");
            if (s.Landscape || s.FitToWidth)
            {
                sb.Append("<pageSetup");
                if (s.Landscape) sb.Append(" orientation=\"landscape\"");
                if (s.FitToWidth) sb.Append(" fitToWidth=\"1\" fitToHeight=\"0\"");
                sb.Append("/>");
            }
            if (tableCount > 0)
            {
                sb.Append("<tableParts count=\"").Append(tableCount).Append("\">");
                for (int i = 1; i <= tableCount; i++) sb.Append("<tablePart r:id=\"rId").Append(i).Append("\"/>");
                sb.Append("</tableParts>");
            }
            sb.Append("</worksheet>");
            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        static byte[] TablePart(XOutSheet s, XOutTable t, int id)
        {
            if (string.IsNullOrEmpty(t.Name) || !Regex.IsMatch(t.Name, "^[A-Za-z_][A-Za-z0-9_.]*$"))
                throw new Exception("XlsxWriter: invalid table name '" + t.Name + "'");
            int bottom = Math.Max(t.Bottom, t.Top + 1);
            string rf = Xlsx.Ref(t.Top, t.Left) + ":" + Xlsx.Ref(bottom, t.Right);
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<table xmlns=\"").Append(NsMain).Append("\" id=\"").Append(id).Append("\" name=\"").Append(t.Name).Append("\" displayName=\"").Append(t.Name).Append("\" ref=\"").Append(rf).Append("\" totalsRowShown=\"0\">");
            sb.Append("<autoFilter ref=\"").Append(rf).Append("\"/>");
            int n = t.Right - t.Left + 1;
            sb.Append("<tableColumns count=\"").Append(n).Append("\">");
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            object[] header = t.Top < s.Rows.Count ? s.Rows[t.Top] : new object[0];
            for (int c = 0; c < n; c++)
            {
                object h = t.Left + c < header.Length ? header[t.Left + c] : null;
                var xc = h as XCell;
                if (xc != null) h = xc.Value;
                string name = h == null ? "" : Convert.ToString(h, CultureInfo.InvariantCulture).Trim();
                if (name.Length == 0 || !used.Add(name))
                    throw new Exception("XlsxWriter: table " + t.Name + " needs a unique text header in " + Xlsx.Ref(t.Top, t.Left + c));
                sb.Append("<tableColumn id=\"").Append(c + 1).Append("\" name=\"").Append(Esc(name)).Append("\"/>");
            }
            sb.Append("</tableColumns>");
            sb.Append("<tableStyleInfo name=\"").Append(t.Style).Append("\" showFirstColumn=\"0\" showLastColumn=\"0\" showRowStripes=\"1\" showColumnStripes=\"0\"/>");
            sb.Append("</table>");
            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        static string Num(double d)
        {
            if (d == Math.Floor(d) && Math.Abs(d) < 1e15) return d.ToString("0", CultureInfo.InvariantCulture);
            return d.ToString("R", CultureInfo.InvariantCulture);
        }

        // XML 1.0 cannot carry most control characters: drop them (tabs and line breaks stay)
        static string Clean(string s)
        {
            if (s == null) return "";
            StringBuilder sb = null;
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                bool ok = ch == '\t' || ch == '\n' || ch == '\r' || (ch >= 0x20 && ch != 0xFFFE && ch != 0xFFFF);
                if (!ok && sb == null) { sb = new StringBuilder(s.Length); sb.Append(s, 0, i); }
                if (sb != null && ok) sb.Append(ch);
            }
            return sb == null ? s : sb.ToString();
        }

        static string Esc(string s)
        {
            if (s == null) return "";
            return Clean(s).Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }
    }
}
