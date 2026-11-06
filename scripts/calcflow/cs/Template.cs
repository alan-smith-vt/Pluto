// Template.cs -- a calc template (.xlsx) read without Excel, the way CalcFlow needs to see it: every cell's cached
// value and its formula (shared formulas expanded, array and data-table ranges marked), whether its style locks it,
// whether its sheet is protected, the defined names, the data validations, the tables, and which cells the formulas
// read. Used by harvest (to learn a template's layout once) and by plan (to check a template's guards before Excel
// opens). Never writes a template. C# 5 (Windows PowerShell 5.1 Add-Type, warnings are errors).

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
    public sealed class TCell
    {
        public string Sheet = "";
        public int Row, Col;                 // 0-based
        public string Value = "";            // cached value as text ("" none); shared strings resolved, booleans TRUE / FALSE
        public string Type = "";             // the cell's t attribute: "" / n (number), s, str, inlineStr, b, e
        public string Formula;               // null: a constant; the formula text without "=" (a shared one shifted to this cell)
        public string FormulaKind = "";      // "" | shared | array | dataTable
        public bool Locked = true;           // the style's protection (Excel's default is locked)

        public string Ref { get { return Xlsx.Ref(Row, Col); } }
        public bool IsFormula { get { return Formula != null; } }
        public bool IsEmpty { get { return Formula == null && Value.Length == 0; } }

        public bool IsNumber
        {
            get
            {
                if (Formula != null || Value.Length == 0) return false;
                if (Type == "s" || Type == "str" || Type == "inlineStr" || Type == "b" || Type == "e") return false;
                double d;
                return double.TryParse(Value, NumberStyles.Float, CultureInfo.InvariantCulture, out d);
            }
        }

        public bool IsText { get { return Formula == null && Value.Length > 0 && !IsNumber && Type != "b" && Type != "e"; } }
    }

    public sealed class TValidation
    {
        public string Sqref = "";
        public string Type = "";             // list | decimal | whole | custom ...
        public string Formula1 = "";
    }

    public sealed class TSheet
    {
        public string Name = "";
        public bool Protected;
        public readonly Dictionary<long, TCell> Cells = new Dictionary<long, TCell>();
        public readonly List<TValidation> Validations = new List<TValidation>();

        public static long Key(int row, int col) { return ((long)row << 20) | (uint)col; }

        public TCell Get(int row, int col)
        {
            TCell c;
            return Cells.TryGetValue(Key(row, col), out c) ? c : null;
        }

        public TCell Ensure(int row, int col)
        {
            TCell c = Get(row, col);
            if (c != null) return c;
            c = new TCell(); c.Sheet = Name; c.Row = row; c.Col = col;
            Cells[Key(row, col)] = c;
            return c;
        }

        // the validation whose sqref covers a cell (null: none)
        public TValidation ValidationAt(int row, int col)
        {
            foreach (TValidation v in Validations)
                foreach (string part in v.Sqref.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int r1, c1, r2, c2;
                    if (TemplateBook.ParseRange(part, out r1, out c1, out r2, out c2) && row >= r1 && row <= r2 && col >= c1 && col <= c2) return v;
                }
            return null;
        }
    }

    public sealed class TName
    {
        public string Name = "";
        public string Scope = "";            // "" workbook-level, else the sheet it is local to
        public string RefersTo = "";
    }

    public sealed class TemplateBook
    {
        public string Path = "";
        public string Sha256 = "";
        public readonly List<TSheet> Sheets = new List<TSheet>();
        public readonly List<TName> Names = new List<TName>();
        public readonly List<XTable> Tables = new List<XTable>();
        public readonly List<string> Notes = new List<string>();     // what could not be read exactly (huge ranges, external refs)
        HashSet<long>[] _refs;          // per sheet index: cells some formula reads
        Dictionary<string, int> _sheetIndex;

        const int MaxRangeCells = 4000;  // a range larger than this is not expanded cell by cell (it is noted)

        // ---- opening ----

        public static TemplateBook Open(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("Template not found: " + path, path);
            var b = new TemplateBook();
            b.Path = System.IO.Path.GetFullPath(path);
            b.Sha256 = SharedFile.Sha256(path);
            using (FileStream fs = SharedFile.OpenRead(path))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Read, false))
                b.Read(zip);
            return b;
        }

        void Read(ZipArchive zip)
        {
            string wbPath = null;
            foreach (string[] r in Xlsx.Rels(zip, "_rels/.rels", ""))
                if (r[1].EndsWith("/officeDocument", StringComparison.OrdinalIgnoreCase)) { wbPath = r[2]; break; }
            if (wbPath == null || Xlsx.Entry(zip, wbPath) == null) wbPath = "xl/workbook.xml";
            if (Xlsx.Entry(zip, wbPath) == null)
                throw new Exception("No workbook part in " + Path + " (an .xlsb or .xls has to be saved as .xlsx first)");
            string wbDir = Xlsx.DirOf(wbPath);
            var idToTarget = new Dictionary<string, string>(StringComparer.Ordinal);
            string ssPath = null, stylesPath = null;
            foreach (string[] r in Xlsx.Rels(zip, wbDir + "_rels/" + Xlsx.FileOf(wbPath) + ".rels", wbDir))
            {
                idToTarget[r[0]] = r[2];
                if (r[1].EndsWith("/sharedStrings", StringComparison.OrdinalIgnoreCase)) ssPath = r[2];
                if (r[1].EndsWith("/styles", StringComparison.OrdinalIgnoreCase)) stylesPath = r[2];
            }
            List<string> shared = ssPath != null ? Xlsx.SharedStrings(zip, ssPath) : new List<string>();
            List<bool> lockedByXf = stylesPath != null ? ReadLocks(zip, stylesPath) : new List<bool>();

            var order = new List<string[]>();
            var localIds = new List<string[]>();     // name, localSheetId, text
            using (XmlReader xr = Xlsx.Xml(Xlsx.Entry(zip, wbPath)))
            {
                string dn = null, local = null;
                var text = new StringBuilder();
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
                        order.Add(new[] { Xlsx.DecodeX(name ?? ""), rid ?? "" });
                    }
                    else if (xr.NodeType == XmlNodeType.Element && xr.LocalName == "definedName")
                    {
                        dn = xr.GetAttribute("name"); local = xr.GetAttribute("localSheetId"); text.Length = 0;
                        if (xr.IsEmptyElement) dn = null;
                    }
                    else if (dn != null && (xr.NodeType == XmlNodeType.Text || xr.NodeType == XmlNodeType.CDATA))
                        text.Append(xr.Value);
                    else if (xr.NodeType == XmlNodeType.EndElement && xr.LocalName == "definedName")
                    {
                        if (dn != null && !dn.StartsWith("_xlnm.", StringComparison.OrdinalIgnoreCase)) localIds.Add(new[] { dn, local ?? "", text.ToString() });
                        dn = null;
                    }
                }
            }
            int k = 0;
            foreach (string[] sh in order)
            {
                k++;
                string target;
                if (!idToTarget.TryGetValue(sh[1], out target)) target = wbDir + "worksheets/sheet" + k.ToString(CultureInfo.InvariantCulture) + ".xml";
                ZipArchiveEntry e = Xlsx.Entry(zip, target);
                if (e == null) continue;
                var sheet = new TSheet();
                sheet.Name = sh[0];
                ReadSheet(e, shared, lockedByXf, sheet);
                Sheets.Add(sheet);
                foreach (string[] r in Xlsx.Rels(zip, Xlsx.DirOf(target) + "_rels/" + Xlsx.FileOf(target) + ".rels", Xlsx.DirOf(target)))
                    if (r[1].EndsWith("/table", StringComparison.OrdinalIgnoreCase))
                    {
                        XTable t = Xlsx.ReadTable(zip, r[2], sheet.Name);
                        if (t != null) Tables.Add(t);
                    }
            }
            foreach (string[] n in localIds)
            {
                var tn = new TName();
                tn.Name = n[0]; tn.RefersTo = n[2];
                int li;
                if (n[1].Length > 0 && int.TryParse(n[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out li) && li >= 0 && li < Sheets.Count)
                    tn.Scope = Sheets[li].Name;
                Names.Add(tn);
            }
        }

        static List<bool> ReadLocks(ZipArchive zip, string path)
        {
            var list = new List<bool>();
            ZipArchiveEntry e = Xlsx.Entry(zip, path);
            if (e == null) return list;
            bool inXfs = false;
            using (XmlReader xr = Xlsx.Xml(e))
                while (xr.Read())
                {
                    if (xr.NodeType == XmlNodeType.Element && xr.LocalName == "cellXfs") inXfs = !xr.IsEmptyElement;
                    else if (xr.NodeType == XmlNodeType.EndElement && xr.LocalName == "cellXfs") inXfs = false;
                    else if (inXfs && xr.NodeType == XmlNodeType.Element && xr.LocalName == "xf") list.Add(true);
                    else if (inXfs && xr.NodeType == XmlNodeType.Element && xr.LocalName == "protection" && list.Count > 0)
                    {
                        string l = xr.GetAttribute("locked");
                        list[list.Count - 1] = !(l == "0" || string.Equals(l, "false", StringComparison.OrdinalIgnoreCase));
                    }
                }
            return list;
        }

        sealed class SharedMaster { public string Formula; public int Row, Col; }

        static void ReadSheet(ZipArchiveEntry e, List<string> shared, List<bool> lockedByXf, TSheet sheet)
        {
            var masters = new Dictionary<string, SharedMaster>(StringComparer.Ordinal);
            var areas = new List<string[]>();   // kind, ref range, master cell ref
            int row = -1, col = -1;
            string type = null, fKind = null, fSi = null, fRef = null;
            int style = 0;
            bool inCell = false, inV = false, inT = false, inF = false, hasF = false, inSheetData = false, inValidation = false, inFormula1 = false;
            var v = new StringBuilder(); var t = new StringBuilder(); var f = new StringBuilder(); var f1 = new StringBuilder();
            TValidation curVal = null;
            using (XmlReader xr = Xlsx.Xml(e))
                while (xr.Read())
                {
                    if (xr.NodeType == XmlNodeType.Element)
                    {
                        switch (xr.LocalName)
                        {
                            case "sheetData": inSheetData = !xr.IsEmptyElement; break;
                            case "sheetProtection":
                                string sp = xr.GetAttribute("sheet");
                                sheet.Protected = sp == "1" || string.Equals(sp, "true", StringComparison.OrdinalIgnoreCase);
                                break;
                            case "row":
                                if (!inSheetData) break;
                                int rn;
                                string ra = xr.GetAttribute("r");
                                row = ra != null && int.TryParse(ra, NumberStyles.Integer, CultureInfo.InvariantCulture, out rn) && rn > 0 ? rn - 1 : row + 1;
                                col = -1;
                                break;
                            case "c":
                                if (!inSheetData) break;
                                int cc, crow;
                                if (Xlsx.ParseRef(xr.GetAttribute("r"), out cc, out crow)) { col = cc; if (crow >= 0) row = crow; } else col++;
                                if (row < 0) row = 0;
                                type = xr.GetAttribute("t") ?? "";
                                int si;
                                style = int.TryParse(xr.GetAttribute("s") ?? "0", NumberStyles.Integer, CultureInfo.InvariantCulture, out si) ? si : 0;
                                v.Length = 0; t.Length = 0; f.Length = 0; hasF = false; fKind = null; fSi = null; fRef = null;
                                inCell = !xr.IsEmptyElement;
                                if (!inCell) Finish(sheet, row, col, type, style, null, null, null, null, "", lockedByXf, masters, areas);
                                break;
                            case "v": if (inCell && !xr.IsEmptyElement) inV = true; break;
                            case "t": if (inCell && !xr.IsEmptyElement) inT = true; break;
                            case "f":
                                if (!inCell) break;
                                hasF = true; fKind = xr.GetAttribute("t") ?? ""; fSi = xr.GetAttribute("si"); fRef = xr.GetAttribute("ref");
                                if (fKind == "dataTable") fRef = (fRef ?? "") + "|" + (xr.GetAttribute("r1") ?? "") + "|" + (xr.GetAttribute("r2") ?? "");
                                inF = !xr.IsEmptyElement;
                                break;
                            case "dataValidation":
                                curVal = new TValidation();
                                curVal.Sqref = xr.GetAttribute("sqref") ?? ""; curVal.Type = xr.GetAttribute("type") ?? "";
                                inValidation = !xr.IsEmptyElement;
                                if (!inValidation) sheet.Validations.Add(curVal);
                                break;
                            case "formula1": if (inValidation && !xr.IsEmptyElement) { inFormula1 = true; f1.Length = 0; } break;
                        }
                    }
                    else if (xr.NodeType == XmlNodeType.Text || xr.NodeType == XmlNodeType.CDATA || xr.NodeType == XmlNodeType.Whitespace || xr.NodeType == XmlNodeType.SignificantWhitespace)
                    {
                        if (inV) v.Append(xr.Value);
                        else if (inT) t.Append(xr.Value);
                        else if (inF) f.Append(xr.Value);
                        else if (inFormula1) f1.Append(xr.Value);
                    }
                    else if (xr.NodeType == XmlNodeType.EndElement)
                    {
                        switch (xr.LocalName)
                        {
                            case "sheetData": inSheetData = false; break;
                            case "v": inV = false; break;
                            case "t": inT = false; break;
                            case "f": inF = false; break;
                            case "formula1": inFormula1 = false; if (curVal != null) curVal.Formula1 = f1.ToString(); break;
                            case "dataValidation": inValidation = false; if (curVal != null) sheet.Validations.Add(curVal); curVal = null; break;
                            case "c":
                                if (inCell)
                                {
                                    string val = "";
                                    if (type == "s")
                                    {
                                        int sidx;
                                        if (int.TryParse(v.ToString().Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out sidx) && sidx >= 0 && sidx < shared.Count) val = shared[sidx];
                                    }
                                    else if (type == "inlineStr") val = Xlsx.DecodeX(t.ToString());
                                    else if (type == "b") val = v.ToString().Trim() == "1" ? "TRUE" : "FALSE";
                                    else if (type == "str") val = Xlsx.DecodeX(v.ToString());
                                    else val = v.ToString().Trim();
                                    Finish(sheet, row, col, type, style, hasF ? f.ToString() : null, fKind, fSi, fRef, val, lockedByXf, masters, areas);
                                }
                                inCell = false;
                                break;
                        }
                    }
                }
            // array and data-table ranges: every cell in them is calculated, even those without an f element
            foreach (string[] a in areas)
            {
                int r1, c1, r2, c2;
                if (!ParseRange(a[1], out r1, out c1, out r2, out c2)) continue;
                for (int r = r1; r <= r2; r++)
                    for (int c = c1; c <= c2; c++)
                    {
                        TCell cell = sheet.Ensure(r, c);
                        if (cell.Formula == null) { cell.Formula = a[2]; cell.FormulaKind = a[0]; }
                    }
            }
        }

        static void Finish(TSheet sheet, int row, int col, string type, int style, string formula, string fKind, string fSi, string fRef,
            string value, List<bool> lockedByXf, Dictionary<string, SharedMaster> masters, List<string[]> areas)
        {
            bool locked = style < lockedByXf.Count ? lockedByXf[style] : true;
            if (formula == null && value.Length == 0 && locked) return;   // an empty, locked cell: nothing to know
            TCell c = sheet.Ensure(row, col);
            c.Type = type; c.Value = value; c.Locked = locked;
            if (formula == null) return;
            c.FormulaKind = fKind ?? "";
            if (c.FormulaKind == "shared" && fSi != null)
            {
                SharedMaster m;
                if (formula.Length > 0 && !masters.ContainsKey(fSi))
                {
                    m = new SharedMaster(); m.Formula = formula; m.Row = row; m.Col = col;
                    masters[fSi] = m;
                    c.Formula = formula;
                }
                else if (masters.TryGetValue(fSi, out m)) c.Formula = FormulaRefs.Shift(m.Formula, row - m.Row, col - m.Col);
                else c.Formula = formula;
            }
            else if (c.FormulaKind == "dataTable")
            {
                // ref|r1|r2: the table's range and its input cells; the cells read are r1 / r2
                string[] p = (fRef ?? "").Split('|');
                string inputs = string.Join(",", p.Skip(1).Where(x => x.Length > 0).ToArray());
                c.Formula = "TABLE(" + inputs + ")";
                if (p[0].Length > 0) areas.Add(new[] { "dataTable", p[0], c.Formula });
            }
            else
            {
                c.Formula = formula;
                if (c.FormulaKind == "array" && !string.IsNullOrEmpty(fRef)) areas.Add(new[] { "array", fRef, formula });
            }
        }

        // ---- lookups ----

        public TSheet Sheet(string name)
        {
            foreach (TSheet s in Sheets) if (string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) return s;
            return null;
        }

        // the name visible from a sheet: its local name first, else the workbook-level one
        public TName Name(string name, string fromSheet)
        {
            TName wbLevel = null;
            foreach (TName n in Names)
            {
                if (!string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                if (n.Scope.Length == 0) wbLevel = n;
                else if (string.Equals(n.Scope, fromSheet ?? "", StringComparison.OrdinalIgnoreCase)) return n;
            }
            return wbLevel;
        }

        // a cell given as "B14" on a sheet, or as a defined name that points at one cell; false otherwise
        public bool Locate(string sheet, string cellOrName, out TSheet s, out int row, out int col, out string error)
        {
            s = null; row = -1; col = -1; error = "";
            string target = (cellOrName ?? "").Trim();
            if (target.Length == 0) { error = "no cell given"; return false; }
            if (Regex.IsMatch(target, @"^\$?[A-Za-z]{1,3}\$?\d+$"))
            {
                s = Sheet(sheet);
                if (s == null) { error = "no sheet '" + sheet + "' in the template"; return false; }
                Xlsx.ParseRef(target, out col, out row);
                return true;
            }
            TName n = Name(target, sheet);
            if (n == null) { error = "no cell or defined name '" + target + "'"; return false; }
            Match m = Regex.Match(n.RefersTo, @"^\s*=?\s*(?:'((?:[^']|'')+)'|([^!]+))!\$?([A-Za-z]{1,3})\$?(\d+)\s*$");
            if (!m.Success) { error = "the name '" + target + "' is not one cell (" + n.RefersTo + ")"; return false; }
            string sn = m.Groups[1].Success ? m.Groups[1].Value.Replace("''", "'") : m.Groups[2].Value;
            s = Sheet(sn);
            if (s == null) { error = "the name '" + target + "' points at a missing sheet '" + sn + "'"; return false; }
            Xlsx.ParseRef(m.Groups[3].Value + m.Groups[4].Value, out col, out row);
            return true;
        }

        // a cell's text as a person reads it ("" for empty or missing)
        public static string TextOf(TSheet s, int row, int col)
        {
            TCell c = s == null ? null : s.Get(row, col);
            return c == null ? "" : c.Value.Trim();
        }

        // guard texts compare trimmed, whitespace runs as one, ignoring case
        public static bool SameText(string a, string b)
        {
            return string.Equals(Regex.Replace((a ?? "").Trim(), @"\s+", " "), Regex.Replace((b ?? "").Trim(), @"\s+", " "), StringComparison.OrdinalIgnoreCase);
        }

        // ---- what the formulas read ----

        public bool IsReferenced(TSheet s, int row, int col)
        {
            BuildRefs();
            return _refs[_sheetIndex[s.Name]].Contains(TSheet.Key(row, col));
        }

        // every referenced cell of a sheet (row, col), for the blank inputs harvest looks for
        public IEnumerable<long> ReferencedKeys(TSheet s)
        {
            BuildRefs();
            return _refs[_sheetIndex[s.Name]];
        }

        void BuildRefs()
        {
            if (_refs != null) return;
            _sheetIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            _refs = new HashSet<long>[Sheets.Count];
            for (int i = 0; i < Sheets.Count; i++) { _sheetIndex[Sheets[i].Name] = i; _refs[i] = new HashSet<long>(); }
            var noted = new HashSet<string>(StringComparer.Ordinal);
            foreach (TSheet s in Sheets)
                foreach (TCell c in s.Cells.Values)
                {
                    if (c.Formula == null || c.Formula.Length == 0) continue;
                    foreach (FormulaRef r in FormulaRefs.Extract(c.Formula))
                        Mark(r, s.Name, noted, 0);
                }
            foreach (TSheet s in Sheets)
                foreach (TValidation v in s.Validations)
                    foreach (FormulaRef r in FormulaRefs.Extract(v.Formula1))
                        Mark(r, s.Name, noted, 0);
        }

        void Mark(FormulaRef r, string fromSheet, HashSet<string> noted, int depth)
        {
            if (depth > 8) return;
            if (r.Name != null)
            {
                TName n = Name(r.Name, fromSheet);
                if (n != null)
                {
                    foreach (FormulaRef inner in FormulaRefs.Extract(n.RefersTo)) Mark(inner, n.Scope.Length > 0 ? n.Scope : fromSheet, noted, depth + 1);
                    return;
                }
                XTable t = Tables.FirstOrDefault(x => string.Equals(x.Name, r.Name, StringComparison.OrdinalIgnoreCase));
                if (t != null)
                {
                    int c1 = t.Left, c2 = t.Right;
                    if (r.TableColumn != null)
                    {
                        int k = Array.FindIndex(t.Columns, x => string.Equals(x, r.TableColumn, StringComparison.OrdinalIgnoreCase));
                        if (k >= 0) { c1 = t.Left + k; c2 = c1; }
                    }
                    MarkRange(t.Sheet, t.Top + 1, c1, t.Bottom - t.TotalsRows, c2, noted);
                }
                return;
            }
            MarkRange(r.Sheet ?? fromSheet, r.Row1, r.Col1, r.Row2, r.Col2, noted);
        }

        void MarkRange(string sheet, int r1, int c1, int r2, int c2, HashSet<string> noted)
        {
            int si;
            if (!_sheetIndex.TryGetValue(sheet, out si))
            {
                if (noted.Add("sheet:" + sheet)) Notes.Add("a formula reads sheet '" + sheet + "', which is not in the workbook (an external link?)");
                return;
            }
            long cells = (long)(r2 - r1 + 1) * (c2 - c1 + 1);
            if (cells > MaxRangeCells)
            {
                string key = sheet + "!" + Xlsx.Ref(r1, c1) + ":" + Xlsx.Ref(r2, c2);
                if (noted.Add(key)) Notes.Add("the range " + key + " (" + cells + " cells) is read by a formula but not expanded cell by cell");
                return;
            }
            for (int r = r1; r <= r2; r++)
                for (int c = c1; c <= c2; c++) _refs[si].Add(TSheet.Key(r, c));
        }

        // "B5", "$B$5:$D$9" -> 0-based inclusive bounds
        public static bool ParseRange(string text, out int r1, out int c1, out int r2, out int c2)
        {
            r1 = c1 = r2 = c2 = -1;
            string[] p = (text ?? "").Split(':');
            if (!Xlsx.ParseRef(p[0], out c1, out r1) || r1 < 0) return false;
            if (p.Length < 2) { r2 = r1; c2 = c1; return true; }
            if (!Xlsx.ParseRef(p[1], out c2, out r2) || r2 < 0) return false;
            if (r2 < r1) { int x = r1; r1 = r2; r2 = x; }
            if (c2 < c1) { int x = c1; c1 = c2; c2 = x; }
            return true;
        }
    }

    // one reference in a formula: a cell or range (Sheet may be null = the formula's own sheet), or a name / table
    public sealed class FormulaRef
    {
        public string Sheet;
        public int Row1, Col1, Row2, Col2;
        public string Name;          // a defined name or a table name
        public string TableColumn;   // Table[Column]
    }

    public static class FormulaRefs
    {
        static readonly Regex Strings = new Regex("\"(?:[^\"]|\"\")*\"");
        static readonly Regex QuotedSheet = new Regex("'(?:[^']|'')+'!");
        static readonly Regex CellRef = new Regex(
            @"(?<![A-Za-z0-9_.\$])(?:(?<sheet>'(?:[^']|'')+'|[A-Za-z_][A-Za-z0-9_.]*)!)?(?<c1>\$?[A-Za-z]{1,3})(?<r1>\$?\d+)(?::(?<c2>\$?[A-Za-z]{1,3})(?<r2>\$?\d+))?(?![A-Za-z0-9_(!\[])");
        static readonly Regex Ident = new Regex(@"(?<![A-Za-z0-9_.\$'!\]])(?<name>[A-Za-z_\\][A-Za-z0-9_.]*)(?<tail>\[(?:[^\[\]]|\[[^\[\]]*\])*\])?(?![A-Za-z0-9_.(!])");

        // the references a formula reads (string literals ignored)
        public static List<FormulaRef> Extract(string formula)
        {
            var list = new List<FormulaRef>();
            if (string.IsNullOrEmpty(formula)) return list;
            string f = Strings.Replace(formula, m => new string(' ', m.Length));
            var cellSpans = new List<int[]>();
            foreach (Match m in CellRef.Matches(f))
            {
                int c1, r1, c2, r2;
                if (!Xlsx.ParseRef(m.Groups["c1"].Value + m.Groups["r1"].Value, out c1, out r1) || r1 < 0) continue;
                if (m.Groups["c2"].Success) { if (!Xlsx.ParseRef(m.Groups["c2"].Value + m.Groups["r2"].Value, out c2, out r2) || r2 < 0) continue; }
                else { c2 = c1; r2 = r1; }
                var r = new FormulaRef();
                if (m.Groups["sheet"].Success)
                {
                    string sn = m.Groups["sheet"].Value;
                    r.Sheet = sn.StartsWith("'") ? sn.Substring(1, sn.Length - 2).Replace("''", "'") : sn;
                }
                r.Row1 = Math.Min(r1, r2); r.Row2 = Math.Max(r1, r2); r.Col1 = Math.Min(c1, c2); r.Col2 = Math.Max(c1, c2);
                list.Add(r);
                cellSpans.Add(new[] { m.Index, m.Index + m.Length });
            }
            string fi = QuotedSheet.Replace(f, m => new string(' ', m.Length));   // 'My Sheet'!C4: no name "Sheet"
            foreach (Match m in Ident.Matches(fi))
            {
                if (cellSpans.Any(sp => m.Index >= sp[0] && m.Index < sp[1])) continue;
                string name = m.Groups["name"].Value;
                if (name.Equals("TRUE", StringComparison.OrdinalIgnoreCase) || name.Equals("FALSE", StringComparison.OrdinalIgnoreCase)) continue;
                var r = new FormulaRef();
                r.Name = name;
                if (m.Groups["tail"].Success)
                {
                    // Table[Col], Table[[#This Row],[Col]]: the last plain [Col] names the column
                    MatchCollection cols = Regex.Matches(m.Groups["tail"].Value, @"\[([^\[\]#@][^\[\]]*)\]");
                    if (cols.Count > 0) r.TableColumn = cols[cols.Count - 1].Groups[1].Value.Trim();
                }
                list.Add(r);
            }
            return list;
        }

        // a shared formula's text moved to another cell: relative row / column parts shift, $-anchored ones do not
        public static string Shift(string formula, int dRow, int dCol)
        {
            if (string.IsNullOrEmpty(formula) || (dRow == 0 && dCol == 0)) return formula;
            var outp = new StringBuilder();
            int last = 0;
            foreach (Match s in Strings.Matches(formula))
            {
                outp.Append(ShiftPart(formula.Substring(last, s.Index - last), dRow, dCol));
                outp.Append(s.Value);
                last = s.Index + s.Length;
            }
            outp.Append(ShiftPart(formula.Substring(last), dRow, dCol));
            return outp.ToString();
        }

        static readonly Regex OneRef = new Regex(@"(?<![A-Za-z0-9_.])(?<cd>\$?)(?<c>[A-Za-z]{1,3})(?<rd>\$?)(?<r>\d+)(?![A-Za-z0-9_(!\[])");

        static string ShiftPart(string part, int dRow, int dCol)
        {
            return OneRef.Replace(part, m =>
            {
                int col, row;
                if (!Xlsx.ParseRef(m.Groups["c"].Value + m.Groups["r"].Value, out col, out row) || row < 0) return m.Value;
                if (m.Groups["cd"].Value.Length == 0) col += dCol;
                if (m.Groups["rd"].Value.Length == 0) row += dRow;
                if (col < 0 || row < 0) return m.Value;
                return m.Groups["cd"].Value + Xlsx.ColName(col) + m.Groups["rd"].Value + (row + 1).ToString(CultureInfo.InvariantCulture);
            });
        }
    }
}
