// Harvest.cs -- learns a calc template's layout once, without Excel, and drafts its calc-library rows.
//   inputs  : constants that a formula reads, cells with a drop-down or other validation, unlocked cells on a protected
//             sheet, cells a defined name points at, and blank cells a formula reads that carry a label beside them;
//             one row per column of an Excel table (a case table)
//   outputs : formula cells that read as a DCR (their label, row title or name says DCR, D/C, ratio, unity,
//             interaction ...), OK / NG status cells, and named cells that read as a solver check (converged, singular)
//   guard   : each row's nearest text to the left (else above): the label the tool checks before it writes or reads
//   layout  : every cell with its value, formula, name, label and role, for reading the template by eye
// The engineer then fills in the bindings (where each input's value comes from) and trims what is not wanted.
// C# 5 (Windows PowerShell 5.1 Add-Type, warnings are errors).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CalcFlow
{
    public sealed class HarvestResult
    {
        public readonly List<Dictionary<string, string>> Templates = new List<Dictionary<string, string>>();
        public readonly List<Dictionary<string, string>> Inputs = new List<Dictionary<string, string>>();
        public readonly List<Dictionary<string, string>> Outputs = new List<Dictionary<string, string>>();
        public readonly List<Dictionary<string, string>> Layout = new List<Dictionary<string, string>>();
        public readonly List<string> Problems = new List<string>();
    }

    public static class Harvest
    {
        public static readonly string[] LayoutColumns = { "Template", "Sheet", "Cell", "Role", "Value", "Formula", "Name", "Label", "Locked", "Validation", "Read" };

        static readonly Regex DcrWords = new Regex(@"(?i)(^|[^a-z])(dcr|d/c|ratio|unity|utili[sz]ation|interaction)");
        static readonly Regex HealthWords = new Regex(@"(?i)(converge|singular|valid|warning|error|status|check)");
        static readonly Regex StatusFormula = new Regex("(?i)\"\\s*(OK|PASS|N\\.?G\\.?|FAIL|NOT OK)\\s*\"");

        // templatesDir: the folder File is written relative to ("" = file names only)
        public static HarvestResult Run(IEnumerable<string> paths, string templatesDir)
        {
            var res = new HarvestResult();
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string p in paths)
            {
                TemplateBook b;
                try { b = TemplateBook.Open(p); }
                catch (Exception ex) { res.Problems.Add(Path.GetFileName(p) + ": " + ex.Message); continue; }
                string id = IdOf(p);
                for (int n = 2; !ids.Add(id); n++) id = IdOf(p) + "-" + n.ToString(CultureInfo.InvariantCulture);
                One(b, id, Relative(b.Path, templatesDir), res);
            }
            return res;
        }

        public static string IdOf(string path)
        {
            string s = Regex.Replace(Path.GetFileNameWithoutExtension(path), @"[^A-Za-z0-9_\-]+", "-").Trim('-');
            return s.Length == 0 ? "template" : s;
        }

        static string Relative(string full, string dir)
        {
            if (string.IsNullOrEmpty(dir)) return Path.GetFileName(full);
            string d = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(d, StringComparison.OrdinalIgnoreCase) ? full.Substring(d.Length).Replace('\\', '/') : Path.GetFileName(full);
        }

        static void One(TemplateBook b, string id, string file, HarvestResult res)
        {
            var named = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // "sheet!key" -> name
            foreach (TName n in b.Names)
            {
                TSheet s; int r, c; string err;
                if (b.Locate(n.Scope, n.Name, out s, out r, out c, out err))
                {
                    string k = s.Name + "!" + TSheet.Key(r, c).ToString(CultureInfo.InvariantCulture);
                    if (!named.ContainsKey(k)) named[k] = n.Name;
                }
            }
            int nIn = 0, nOut = 0;
            var protectedSheets = new List<string>();
            foreach (TSheet s in b.Sheets)
            {
                if (s.Protected) protectedSheets.Add(s.Name);
                var bodies = b.Tables.Where(t => string.Equals(t.Sheet, s.Name, StringComparison.OrdinalIgnoreCase)).ToList();
                var roles = new Dictionary<long, string>();
                foreach (TCell c in s.Cells.Values.OrderBy(x => x.Row).ThenBy(x => x.Col))
                {
                    if (InTable(bodies, c.Row, c.Col)) { roles[TSheet.Key(c.Row, c.Col)] = "table"; continue; }
                    string name = NameAt(named, s, c.Row, c.Col);
                    if (c.IsFormula)
                    {
                        Dictionary<string, string> o = Output(b, s, c, name, id);
                        if (o != null) { res.Outputs.Add(o); nOut++; roles[TSheet.Key(c.Row, c.Col)] = "output"; }
                        continue;
                    }
                    var why = new List<string>();
                    if (!c.IsEmpty && b.IsReferenced(s, c.Row, c.Col)) why.Add("read by a formula");
                    TValidation v = s.ValidationAt(c.Row, c.Col);
                    if (v != null) why.Add("validation");
                    if (s.Protected && !c.Locked) why.Add("unlocked");
                    if (name != null) why.Add("named");
                    if (why.Count == 0) continue;
                    // read by a formula and nothing else: an input has its own label on its row and is not one value of
                    // a grid of numbers (a lookup table the template reads)
                    if (why.Count == 1 && (LeftLabel(s, c.Row, c.Col, 3, false) == null || InGrid(s, c.Row, c.Col))) continue;
                    res.Inputs.Add(Input(b, s, c.Row, c.Col, c, name, v, id, string.Join(", ", why.ToArray())));
                    nIn++;
                    roles[TSheet.Key(c.Row, c.Col)] = "input";
                }
                // blank cells a formula reads, with a label beside them: optional inputs ("blank = UNC table")
                foreach (long k in b.ReferencedKeys(s).OrderBy(x => x))
                {
                    int row = (int)(k >> 20), col = (int)(k & 0xFFFFF);
                    TCell c = s.Get(row, col);
                    if ((c != null && !c.IsEmpty) || roles.ContainsKey(k)) continue;
                    if (InTable(bodies, row, col)) continue;
                    TCell label = LeftLabel(s, row, col, 2, false);
                    if (label == null) continue;
                    res.Inputs.Add(Input(b, s, row, col, c, NameAt(named, s, row, col), s.ValidationAt(row, col), id, "blank, read by a formula"));
                    nIn++;
                    roles[k] = "input (blank)";
                }
                foreach (XTable t in bodies)
                    for (int k = 0; k < t.Columns.Length; k++)
                    {
                        var d = Row(LibrarySchema.InputColumns);
                        d["Template"] = id; d["Sheet"] = s.Name;
                        d["Cell"] = Xlsx.Ref(t.Top + 1, t.Left + k);
                        d["GuardCell"] = Xlsx.Ref(t.Top, t.Left + k); d["GuardText"] = t.Columns[k];
                        d["Unit"] = Units.FromLabel(t.Columns[k]); d["PerCase"] = "Y";
                        d["Kind"] = "table";
                        d["Description"] = "column " + t.Columns[k] + " of table " + t.Name + " (rows " + (t.Top + 2) + "-" + (t.Bottom - t.TotalsRows + 1) + ")";
                        d["Found"] = "table column (one case per row)";
                        res.Inputs.Add(d);
                        nIn++;
                    }
                foreach (TCell c in s.Cells.Values.OrderBy(x => x.Row).ThenBy(x => x.Col))
                {
                    string role;
                    if (!roles.TryGetValue(TSheet.Key(c.Row, c.Col), out role))
                        role = c.IsFormula ? "formula" : (c.IsText ? "label" : (c.IsEmpty ? "unlocked" : "constant"));
                    TCell lab = LeftLabel(s, c.Row, c.Col, 8);
                    TValidation v = s.ValidationAt(c.Row, c.Col);
                    var d = Row(LayoutColumns);
                    d["Template"] = id; d["Sheet"] = s.Name; d["Cell"] = c.Ref; d["Role"] = role; d["Value"] = c.Value;
                    d["Formula"] = c.IsFormula ? "=" + c.Formula : "";
                    d["Name"] = NameAt(named, s, c.Row, c.Col) ?? "";
                    d["Label"] = lab == null ? "" : lab.Value.Trim();
                    d["Locked"] = c.Locked ? "Y" : "N";
                    d["Validation"] = v == null ? "" : v.Formula1;
                    d["Read"] = b.IsReferenced(s, c.Row, c.Col) ? "Y" : "";
                    res.Layout.Add(d);
                }
            }
            var tr = Row(LibrarySchema.TemplateColumns);
            tr["Template"] = id; tr["File"] = file; tr["Sha256"] = b.Sha256;
            tr["Shape"] = b.Tables.Count > 0 ? "case_table" : "single_case";
            var notes = new List<string>();
            notes.Add(nIn + " inputs and " + nOut + " outputs drafted by harvest; fill in the bindings");
            if (protectedSheets.Count > 0) notes.Add("protected sheets: " + string.Join(", ", protectedSheets.ToArray()));
            if (b.Tables.Count > 0) notes.Add("tables: " + string.Join(", ", b.Tables.Select(t => t.Name).ToArray()) + " (a case table? set Shape and MaxCases)");
            notes.AddRange(b.Notes);
            tr["Notes"] = string.Join("; ", notes.ToArray());
            res.Templates.Add(tr);
        }

        static Dictionary<string, string> Input(TemplateBook b, TSheet s, int row, int col, TCell c, string name, TValidation v, string id, string why)
        {
            var d = Row(LibrarySchema.InputColumns);
            d["Template"] = id; d["Sheet"] = s.Name;
            d["Cell"] = name ?? Xlsx.Ref(row, col);
            TCell label = LeftLabel(s, row, col, 8);
            if (label != null) { d["GuardCell"] = label.Ref; d["GuardText"] = label.Value.Trim(); }
            TCell desc = RightText(s, row, col);
            d["Description"] = desc == null ? "" : desc.Value.Trim();
            string unit = Units.FromLabel(d["GuardText"]);
            Unit u;
            if (unit.Length == 0 && desc != null && Units.TryParse(desc.Value, out u)) unit = u.Name;
            d["Unit"] = unit;
            d["PerCase"] = "N";
            d["Required"] = (c == null || c.IsEmpty) ? "N" : "Y";
            d["Example"] = Tidy(c);
            d["Kind"] = v != null && v.Type == "list" ? "list" : (c == null || c.IsEmpty ? "blank" : (c.IsNumber ? "number" : (c.Type == "b" ? "boolean" : "text")));
            d["List"] = v != null && v.Type == "list" ? v.Formula1.Trim('"') : "";
            d["Found"] = why + (name != null ? " (" + Xlsx.Ref(row, col) + ")" : "");
            return d;
        }

        static Dictionary<string, string> Output(TemplateBook b, TSheet s, TCell c, string name, string id)
        {
            TCell nearest = LeftLabel(s, c.Row, c.Col, 8);          // the guard may be the label above (a column header)
            TCell sameRow = LeftLabel(s, c.Row, c.Col, 8, false);   // what the row says about the cell
            TCell title = RowTitle(s, c.Row, c.Col);
            string near = nearest == null ? "" : nearest.Value.Trim(), tt = title == null ? "" : title.Value.Trim();
            string row = sameRow == null ? "" : sameRow.Value.Trim();
            string kind;
            if (StatusFormula.IsMatch(c.Formula)) kind = "status";
            else if (DcrWords.IsMatch(name ?? "") || DcrWords.IsMatch(tt) || DcrWords.IsMatch(row) || DcrWords.IsMatch(c.Formula)) kind = "dcr";
            else if (name != null && HealthWords.IsMatch(name)) kind = "health";
            else return null;
            var d = Row(LibrarySchema.OutputColumns);
            d["Template"] = id; d["Sheet"] = s.Name; d["Cell"] = name ?? c.Ref;
            if (nearest != null) { d["GuardCell"] = nearest.Ref; d["GuardText"] = near; }
            string label = tt.Length > 0 ? tt : (near.Length > 0 && near.Length <= 40 ? near : (name ?? c.Ref));
            d["Output"] = kind == "status" ? label + " (status)" : label;
            d["Kind"] = kind;
            d["Check"] = kind == "dcr" ? label : "";
            d["PerCase"] = "N";
            d["Example"] = Tidy(c);
            d["Formula"] = "=" + c.Formula;
            d["Found"] = kind + (name != null ? " (named, " + c.Ref + ")" : "");
            return d;
        }

        static Dictionary<string, string> Row(string[] columns)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string c in columns) d[c] = "";
            return d;
        }

        static bool InTable(List<XTable> tables, int row, int col)
        {
            foreach (XTable t in tables)
                if (row > t.Top && row <= t.Bottom - t.TotalsRows && col >= t.Left && col <= t.Right) return true;
            return false;
        }

        static string NameAt(Dictionary<string, string> named, TSheet s, int row, int col)
        {
            string n;
            return named.TryGetValue(s.Name + "!" + TSheet.Key(row, col).ToString(CultureInfo.InvariantCulture), out n) ? n : null;
        }

        // the nearest text to the left in the row (stopping at a number or a formula), else the text just above
        public static TCell LeftLabel(TSheet s, int row, int col, int reach) { return LeftLabel(s, row, col, reach, true); }

        public static TCell LeftLabel(TSheet s, int row, int col, int reach, bool orAbove)
        {
            for (int c = col - 1; c >= 0 && c >= col - reach; c--)
            {
                TCell x = s.Get(row, c);
                if (x == null || x.IsEmpty) continue;
                if (x.IsText) return x;
                break;
            }
            for (int r = row - 1; orAbove && r >= 0 && r >= row - 2; r--)
            {
                TCell x = s.Get(r, col);
                if (x == null || x.IsEmpty) continue;
                if (x.IsText) return x;
                break;
            }
            return null;
        }

        // a number with a number beside it and another above or below it: one value of a table
        static bool InGrid(TSheet s, int row, int col)
        {
            Func<int, int, bool> num = (r, c) => { TCell x = r < 0 || c < 0 ? null : s.Get(r, c); return x != null && x.IsNumber; };
            return (num(row, col - 1) || num(row, col + 1)) && (num(row - 1, col) || num(row + 1, col));
        }

        // a cached number as people write it (0.707, not 0.70699999999999996)
        static string Tidy(TCell c)
        {
            if (c == null) return "";
            double d;
            if (c.IsNumber && double.TryParse(c.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d.ToString("R", CultureInfo.InvariantCulture);
            return c.Value;
        }

        // the first text of the row, left of the cell: the row's title ("Governing DCR")
        static TCell RowTitle(TSheet s, int row, int col)
        {
            for (int c = 0; c < col; c++)
            {
                TCell x = s.Get(row, c);
                if (x != null && x.IsText) return x;
            }
            return null;
        }

        static TCell RightText(TSheet s, int row, int col)
        {
            for (int c = col + 1; c <= col + 2; c++)
            {
                TCell x = s.Get(row, c);
                if (x == null || x.IsEmpty) continue;
                return x.IsText ? x : null;
            }
            return null;
        }

        // harvest.xlsx: the drafts as tables with the calc library's columns (paste them into the library, or start a
        // library from them), and the whole layout
        public static void WriteWorkbook(HarvestResult r, string path)
        {
            var sheets = new List<XOutSheet>();
            sheets.Add(WorkbookTables.Sheet("Templates", "tblHarvestTemplates", LibrarySchema.TemplateColumns, r.Templates, "templates found"));
            sheets.Add(WorkbookTables.Sheet("Inputs", "tblHarvestInputs", LibrarySchema.InputColumns, r.Inputs, "fill in Binding for every input the calc needs; delete the rest"));
            sheets.Add(WorkbookTables.Sheet("Outputs", "tblHarvestOutputs", LibrarySchema.OutputColumns, r.Outputs, "keep the DCRs and checks to read back; set Check and, for solver checks, Kind health and Expect"));
            sheets.Add(WorkbookTables.Sheet("Layout", "tblHarvestLayout", LayoutColumns, r.Layout, "every cell of every template, as harvest read it"));
            XlsxWriter.Write(path, sheets, null);
        }
    }
}
