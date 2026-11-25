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
        public readonly List<string[]> Skipped = new List<string[]>();     // file, why (an .xls / .xlsb harvest cannot read)
        // the templates whose inputs were read from the cells coloured as inputs (TemplateMap), and the tables of
        // such a template that the tool writes a row at a time
        public readonly HashSet<string> Coloured = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public readonly List<LoadTable> Tables = new List<LoadTable>();
        // every sheet of every template workbook, in the workbook's order: template, sheet, the sheets its formulas
        // read however far (joined by "|"). Sheets that read one another are parts of one calc.
        public readonly List<string[]> Sheets = new List<string[]>();

        public List<string> SheetsOf(string template)
        {
            return Sheets.Where(x => x[0] == template).Select(x => x[1]).ToList();
        }

        public List<string> SheetsRead(string template, string sheet)
        {
            string[] row = Sheets.FirstOrDefault(x => x[0] == template && string.Equals(x[1], sheet, StringComparison.OrdinalIgnoreCase));
            return row == null || row[2].Length == 0 ? new List<string>() : row[2].Split('|').ToList();
        }
    }

    public static class Harvest
    {
        public static readonly string[] LayoutColumns = { "Template", "Sheet", "Cell", "Role", "Value", "Formula", "Name", "Label", "Locked", "Validation", "Read" };

        static readonly Regex DcrWords = new Regex(@"(?i)(^|[^a-z])(dcr|d/c|ratio|unity|utili[sz]ation|interaction)");
        static readonly Regex HealthWords = new Regex(@"(?i)(converge|singular|valid|warning|error|status|check)");
        static readonly Regex StatusFormula = new Regex("(?i)\"\\s*(OK|PASS|N\\.?G\\.?|FAIL|NOT OK)\\s*\"");

        // templatesDir: the folder File is written relative to ("" = file names only)
        // file formats harvest reads (Office Open XML workbooks and templates) and the old ones it lists as skipped
        public static readonly string[] Readable = { ".xlsx", ".xlsm", ".xltx", ".xltm" };
        public static readonly string[] Unreadable = { ".xls", ".xlsb", ".xlt" };

        // The fills that mark the cells to fill in, six hex digits each (empty: the yellow family). A workbook that has
        // such cells takes its inputs from them and nothing is guessed from its formulas; one that has none is read
        // the old way.
        public static List<string> InputFills = new List<string>();

        public static HarvestResult Run(IEnumerable<string> paths, string templatesDir)
        {
            var res = new HarvestResult();
            var list = new List<string>();
            foreach (string p in paths)
            {
                string ext = Path.GetExtension(p).ToLowerInvariant();
                if (Unreadable.Contains(ext)) { res.Skipped.Add(new[] { Relative(Path.GetFullPath(p), templatesDir), "an " + ext + " workbook: save it as .xlsx (or .xlsm) beside it, then harvest again" }); continue; }
                list.Add(p);
            }
            // a template's id is its file name; where file names repeat (nested folders), its path below the folder
            var byName = list.GroupBy(p => IdOf(p), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string p in list)
            {
                TemplateBook b;
                try { b = TemplateBook.Open(p); }
                catch (Exception ex) { res.Problems.Add(Path.GetFileName(p) + ": " + ex.Message); continue; }
                string rel = Relative(b.Path, templatesDir);
                string id = byName[IdOf(p)] > 1 ? IdOfRelative(rel) : IdOf(p);
                string first = id;
                for (int n = 2; !ids.Add(id); n++) id = first + "-" + n.ToString(CultureInfo.InvariantCulture);
                One(b, id, rel, res);
            }
            return res;
        }

        // "Welds/Group/WG-1.xlsx" -> "Welds-Group-WG-1"
        public static string IdOfRelative(string rel)
        {
            string noExt = rel.Substring(0, rel.Length - Path.GetExtension(rel).Length);
            string s = Regex.Replace(noExt.Replace('\\', '/').Replace('/', '-'), @"[^A-Za-z0-9_\-]+", "-").Trim('-');
            return s.Length == 0 ? "template" : s;
        }

        public static string IdOf(string path)
        {
            string s = Regex.Replace(Path.GetFileNameWithoutExtension(path), @"[^A-Za-z0-9_\-]+", "-").Trim('-');
            return s.Length == 0 ? "template" : s;
        }

        internal static string Relative(string full, string dir)
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
            // the cells the template's author coloured as inputs, when there are any
            TemplateMapResult map = null;
            try { map = TemplateMap.Read(b, TemplateLook.Read(b.Path), InputFills); }
            catch (Exception ex) { res.Problems.Add(file + ": its fills could not be read (" + ex.Message + "); its inputs are guessed from its formulas"); }
            bool coloured = map != null && map.Blocks.Count > 0;
            if (coloured) res.Coloured.Add(id);
            foreach (TSheet s in b.Sheets)
            {
                if (s.Protected) protectedSheets.Add(s.Name);
                var bodies = b.Tables.Where(t => string.Equals(t.Sheet, s.Name, StringComparison.OrdinalIgnoreCase)).ToList();
                var roles = new Dictionary<long, string>();
                var written = coloured ? map.Blocks.Where(x => x.LoadRows && x.Sheet == s.Name).ToList() : new List<MapBlock>();
                foreach (TCell c in s.Cells.Values.OrderBy(x => x.Row).ThenBy(x => x.Col))
                {
                    if (InTable(bodies, c.Row, c.Col)) { roles[TSheet.Key(c.Row, c.Col)] = "table"; continue; }
                    string name = NameAt(named, s, c.Row, c.Col);
                    if (c.IsFormula)
                    {
                        // along the rows of a table the tool writes, a formula is one of the row's results (below)
                        if (written.Any(x => c.Row >= x.Top && c.Row <= x.Bottom)) continue;
                        Dictionary<string, string> o = Output(b, s, c, name, id);
                        if (o != null) { res.Outputs.Add(o); nOut++; roles[TSheet.Key(c.Row, c.Col)] = "output"; }
                        continue;
                    }
                    if (coloured) continue;
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
                if (coloured) Coloured(b, s, id, map, named, res, roles, ref nIn, ref nOut);
                // blank cells a formula reads, with a label beside them: optional inputs ("blank = UNC table")
                foreach (long k in coloured ? new List<long>() : b.ReferencedKeys(s).OrderBy(x => x).ToList())
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
            foreach (TSheet s in b.Sheets) res.Sheets.Add(new[] { id, s.Name, string.Join("|", b.SheetsRead(s.Name).ToArray()) });
            var tr = Row(LibrarySchema.TemplateColumns);
            tr["Template"] = id; tr["File"] = file; tr["Sha256"] = b.Sha256;
            tr["Shape"] = b.Tables.Count > 0 ? "case_table" : "single_case";
            var notes = new List<string>();
            notes.Add(nIn + " inputs and " + nOut + " outputs drafted by harvest; fill in the bindings");
            if (protectedSheets.Count > 0) notes.Add("protected sheets: " + string.Join(", ", protectedSheets.ToArray()));
            if (b.Tables.Count > 0) notes.Add("tables: " + string.Join(", ", b.Tables.Select(t => t.Name).ToArray()) + " (a case table? set Shape and MaxCases)");
            notes.AddRange(b.Notes.Take(20));
            if (b.Notes.Count > 20) notes.Add("and " + (b.Notes.Count - 20) + " more notes like these");
            tr["Notes"] = string.Join("; ", notes.ToArray());
            res.Templates.Add(tr);
        }

        // The inputs of one sheet as its author coloured them (TemplateMap): a cell, or each cell of a row of cells, is
        // an input named by its heading; a list or table to type is one input that holds the whole block; a table the
        // tool writes is the sheet's load table, with the formulas along its rows as results, one a row.
        static void Coloured(TemplateBook b, TSheet s, string id, TemplateMapResult map, Dictionary<string, string> named, HarvestResult res, Dictionary<long, string> roles, ref int nIn, ref int nOut)
        {
            foreach (MapBlock block in map.Blocks.Where(x => x.Sheet == s.Name))
            {
                if (block.LoadRows)
                {
                    var t = new LoadTable();
                    t.Template = id; t.Sheet = s.Name; t.At = Xlsx.Ref(block.Top, block.Left); t.MaxRows = block.Rows;
                    foreach (MapColumn c in block.Columns)
                    {
                        string f = c.FilledBy == "Beam number" ? "Beam" : c.FilledBy == "Load case" ? "LC" : c.FilledBy == TemplateMap.PerEnd ? LoadTable.Typed(c.Heading.Length > 0 ? c.Heading : c.Cells) : c.FilledBy;
                        t.Fields.Add(f);
                        for (int k = 1; k < c.Width; k++) t.Fields.Add("-");
                        Unit u;
                        if (Units.TryParse(c.Unit, out u) && f.Length == 2 && f[0] == 'F' && t.ForceUnit.Length == 0) t.ForceUnit = c.Unit;
                        if (Units.TryParse(c.Unit, out u) && f.Length == 2 && f[0] == 'M' && t.MomentUnit.Length == 0) t.MomentUnit = c.Unit;
                    }
                    res.Tables.Add(t);
                    foreach (MapColumn r in block.Results)
                    {
                        var o = Row(LibrarySchema.OutputColumns);
                        o["Template"] = id; o["Sheet"] = s.Name; o["Cell"] = r.Cells;
                        int lc, lr;
                        if (r.LabelCell.Length > 0 && Xlsx.ParseRef(r.LabelCell, out lc, out lr)) { o["GuardCell"] = r.LabelCell; o["GuardText"] = TemplateBook.TextOf(s, lr, lc).Trim(); }
                        o["Output"] = r.Heading.Length > 0 ? r.Heading : r.Cells;
                        o["Kind"] = DcrWords.IsMatch(r.Heading) || Regex.IsMatch(r.Heading, @"(?i)\b(IR|UC)\b") ? "dcr" : "value";
                        o["PerCase"] = "Y";
                        o["Example"] = r.Example;
                        o["Found"] = "beside a table the tool writes (one a row)";
                        o["PerRow"] = "Y";
                        res.Outputs.Add(o);
                        nOut++;
                    }
                    continue;
                }
                bool table = block.Rows > 1;
                foreach (MapColumn c in table ? block.Columns.Take(1) : block.Columns)
                {
                    int col, row;
                    if (!Xlsx.ParseRef(c.Cells.Split(':')[0], out col, out row)) continue;
                    Dictionary<string, string> d = Input(b, s, row, col, s.Get(row, col), table ? null : NameAt(named, s, row, col), table ? null : s.ValidationAt(row, col), id, table ? "input fill (a block)" : "input fill");
                    int lc, lr;
                    d["GuardCell"] = ""; d["GuardText"] = "";
                    if (c.LabelCell.Length > 0 && Xlsx.ParseRef(c.LabelCell, out lc, out lr)) { d["GuardCell"] = c.LabelCell; d["GuardText"] = TemplateBook.TextOf(s, lr, lc).Trim(); }
                    d["Unit"] = table ? "" : c.Unit;
                    d["Name"] = table ? (c.Group.Length > 0 ? c.Group : string.Join(", ", block.Columns.Select(x => x.Heading).Where(x => x.Length > 0).ToArray())) : c.Heading;
                    d["Source"] = c.FilledBy == TemplateMap.PerMark || c.FilledBy == TemplateMap.MarkTable ? "" : c.FilledBy;
                    if (table)
                    {
                        d["Cell"] = block.Range; d["Kind"] = "table"; d["List"] = ""; d["Required"] = "N";
                        var lines = new List<string>();
                        for (int r = block.Top; r <= block.Bottom; r++)
                        {
                            var vals = block.Columns.Select(x => TemplateBook.TextOf(s, r, x.Col).Trim()).ToList();
                            if (vals.All(v => v.Length == 0)) break;
                            lines.Add(string.Join(", ", vals.ToArray()));
                        }
                        d["Example"] = string.Join("; ", lines.ToArray());
                        d["Description"] = "a block of " + block.Rows.ToString(CultureInfo.InvariantCulture) + " rows: " + string.Join(", ", block.Columns.Select(x => x.Heading).ToArray());
                    }
                    res.Inputs.Add(d);
                    nIn++;
                    roles[TSheet.Key(row, col)] = "input";
                }
            }
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

        // an input the engineer points at: the row harvest would give it (its label, its unit, what it holds)
        public static Dictionary<string, string> InputAt(TemplateBook b, string id, TSheet s, int row, int col)
        {
            return Input(b, s, row, col, s.Get(row, col), null, s.ValidationAt(row, col), id, "given");
        }

        // a result the engineer points at: what harvest makes of it, else a plain value under its label
        public static Dictionary<string, string> OutputAt(TemplateBook b, string id, TSheet s, int row, int col)
        {
            TCell c = s.Get(row, col);
            Dictionary<string, string> o = c != null && c.IsFormula ? Output(b, s, c, null, id) : null;
            if (o != null) return o;
            TCell nearest = LeftLabel(s, row, col, 8), title = RowTitle(s, row, col);
            var d = Row(LibrarySchema.OutputColumns);
            d["Template"] = id; d["Sheet"] = s.Name; d["Cell"] = Xlsx.Ref(row, col);
            if (nearest != null) { d["GuardCell"] = nearest.Ref; d["GuardText"] = nearest.Value.Trim(); }
            d["Output"] = nearest != null ? nearest.Value.Trim() : (title != null ? title.Value.Trim() : Xlsx.Ref(row, col));
            d["Kind"] = "value";
            d["PerCase"] = "N";
            d["Example"] = Tidy(c);
            d["Formula"] = c != null && c.IsFormula ? "=" + c.Formula : "";
            d["Found"] = "given";
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
            // the row's title speaks for the first value after the row's labels, not for every formula further along
            // the row (a sweep or figure table off the print area shares rows with the results)
            else if (DcrWords.IsMatch(name ?? "") || (sameRow != null && DcrWords.IsMatch(tt)) || DcrWords.IsMatch(row) || DcrWords.IsMatch(c.Formula)) kind = "dcr";
            else if (name != null && HealthWords.IsMatch(name)) kind = "health";
            else return null;
            var d = Row(LibrarySchema.OutputColumns);
            d["Template"] = id; d["Sheet"] = s.Name; d["Cell"] = name ?? c.Ref;
            // the guard: the label beside or above the cell, else the row's title (a result at the end of a row of values)
            if (nearest != null) { d["GuardCell"] = nearest.Ref; d["GuardText"] = near; }
            else if (title != null) { d["GuardCell"] = title.Ref; d["GuardText"] = tt; }
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

        // a sheet of a layout workbook holds at most this many cells (Excel holds 1,048,576 rows; the rest are split off)
        public static int LayoutRowsPerSheet = 1000000;

        // harvest.xlsx: the drafts as tables with the calc library's columns (paste them into the library, or start a
        // library from them) and an index of the layouts; every template's layout in harvest-layout\<Template>.xlsx beside
        // it, one sheet per template sheet (split when longer than a sheet holds). Returns the files written.
        public static string[] WriteWorkbook(HarvestResult r, string path)
        {
            var written = new List<string>();
            string layoutDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)), "harvest-layout");
            var index = new List<Dictionary<string, string>>();
            foreach (var t in r.Templates)
            {
                string id = t["Template"];
                var rows = r.Layout.Where(x => x["Template"] == id).ToList();
                var sheets = new List<XOutSheet>();
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int k = 0;
                foreach (var g in rows.GroupBy(x => x["Sheet"]))
                {
                    var all = g.ToList();
                    for (int part = 0; part * LayoutRowsPerSheet < all.Count || (part == 0 && all.Count == 0); part++)
                    {
                        var chunk = all.Skip(part * LayoutRowsPerSheet).Take(LayoutRowsPerSheet).ToList();
                        string suffix = part == 0 ? "" : " (" + (part + 1).ToString(CultureInfo.InvariantCulture) + ")";
                        string name = SheetName(g.Key, suffix, names);
                        k++;
                        XOutSheet s = WorkbookTables.Sheet(name, "tblLayout" + k.ToString(CultureInfo.InvariantCulture), LayoutColumns, chunk,
                            "template " + id + ", sheet " + g.Key + (part > 0 ? " (part " + (part + 1) + ")" : "") + ": every cell as harvest read it",
                            c => Palette.KindOf("tblLayout", c));
                        Library.Highlights(s, "tblLayout", LayoutColumns, chunk.Count);
                        sheets.Add(s);
                    }
                }
                string file = Path.Combine(layoutDir, SafeFile(id) + ".xlsx");
                var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                d["Template"] = id; d["File"] = "harvest-layout/" + SafeFile(id) + ".xlsx";
                d["Sheets"] = string.Join(", ", rows.Select(x => x["Sheet"]).Distinct().ToArray());
                d["Cells"] = rows.Count.ToString(CultureInfo.InvariantCulture);
                d["Inputs"] = r.Inputs.Count(x => x["Template"] == id).ToString(CultureInfo.InvariantCulture);
                d["Outputs"] = r.Outputs.Count(x => x["Template"] == id).ToString(CultureInfo.InvariantCulture);
                index.Add(d);
                if (sheets.Count == 0) continue;
                Directory.CreateDirectory(layoutDir);
                XlsxWriter.Write(file, sheets, null);
                written.Add(file);
            }
            var book = new List<XOutSheet>();
            book.Add(WorkbookTables.Sheet("Templates", "tblHarvestTemplates", LibrarySchema.TemplateColumns, r.Templates, "templates found"));
            XOutSheet ins = WorkbookTables.Sheet("Inputs", "tblHarvestInputs", LibrarySchema.InputColumns, r.Inputs, "fill in Binding for every input the calc needs; delete the rest");
            book.Add(ins);
            XOutSheet outs = WorkbookTables.Sheet("Outputs", "tblHarvestOutputs", LibrarySchema.OutputColumns, r.Outputs, "keep the DCRs and checks to read back; set Check and, for solver checks, Kind health and Expect");
            Library.Highlights(outs, "tblOutputs", LibrarySchema.OutputColumns, r.Outputs.Count);
            book.Add(outs);
            book.Add(WorkbookTables.Sheet("Layouts", "tblHarvestLayouts", new[] { "Template", "File", "Sheets", "Cells", "Inputs", "Outputs" }, index,
                "each template's cells, one workbook per template beside this one (harvest-layout)"));
            if (r.Skipped.Count > 0)
            {
                var sk = r.Skipped.Select(x => { var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); d["File"] = x[0]; d["Why"] = x[1]; return d; }).ToList();
                book.Add(WorkbookTables.Sheet("Skipped", "tblHarvestSkipped", new[] { "File", "Why" }, sk, "files harvest could not read", c => c == "File" ? ColumnKind.Key : ColumnKind.Reference));
            }
            XlsxWriter.Write(path, book, null);
            // a layout left from an earlier harvest of a template no longer there would read as current: it goes
            if (Directory.Exists(layoutDir))
                foreach (string old in Directory.GetFiles(layoutDir, "*.xlsx"))
                    if (!written.Contains(old, StringComparer.OrdinalIgnoreCase)) File.Delete(old);
            written.Insert(0, Path.GetFullPath(path));
            return written.ToArray();
        }

        // a sheet name Excel takes (31 characters, none of : \ / ? * [ ]), unique in the workbook
        static string SheetName(string name, string suffix, HashSet<string> used)
        {
            string b = Regex.Replace(name ?? "", @"[:\\/?*\[\]]", "_").Trim();
            if (b.Length == 0) b = "Sheet";
            string n = (b.Length + suffix.Length > 31 ? b.Substring(0, 31 - suffix.Length) : b) + suffix;
            for (int i = 2; !used.Add(n); i++)
            {
                string x = " " + i.ToString(CultureInfo.InvariantCulture);
                n = (b.Length + suffix.Length + x.Length > 31 ? b.Substring(0, 31 - suffix.Length - x.Length) : b) + suffix + x;
            }
            return n;
        }

        static string SafeFile(string id) { return Regex.Replace(id, @"[^A-Za-z0-9_\-]+", "-"); }
    }
}
