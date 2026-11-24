// ProjectBook.cs -- the engineer's tabs of CalcFlow.xlsx and what they mean to the engine.
//   Sheets   written once by "new": Model Config (one row per beam, a connection mark at each end), Connection Lib
//            (one row per mark: one template plus one row of values), Templates, a schedule tab per template (one row
//            per mark, one column per input of the template, every input on that one row), Template Cells (where each
//            column goes in the template), Outputs (what is read back), Sign Maps, Load Cases
//   Expand   the saved tabs -> the calc library's eight tables as text and a generated register, which the planner,
//            the run and the summary take as they are (Library.cs, Register.cs, Plan.cs). Problems are said in plain
//            words, by tab and column.
// A schedule tab, top to bottom: a note; Source (where each column's value comes from: typed on the row, a force of
// the member end, the load case, the beam, its section ...); Unit; the template's own value; the header; the rows.
// A mark is a library Type with one step; a typed column is an optional param: input (a blank cell keeps the
// template's value). C# 5 (Windows PowerShell 5.1 Add-Type, warnings are errors).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace CalcFlow
{
    public sealed class ExpandResult
    {
        public string LibraryFolder = "", RegisterPath = "";
        public int Marks, Connections, Templates;
        public readonly List<string[]> Problems = new List<string[]>();      // severity, where (tab), message
        public int ErrorCount { get { return Problems.Count(p => p[0] == "Error"); } }
        internal void Error(string where, string message) { Problems.Add(new[] { "Error", where, message }); }
        internal void Warn(string where, string message) { Problems.Add(new[] { "Warning", where, message }); }
    }

    // A template's table of load rows, as found: a header row of beam / node / case / force names over rows to fill.
    public sealed class LoadTable
    {
        public string Sheet = "", At = "", ForceUnit = "", MomentUnit = "";
        public readonly List<string> Fields = new List<string>();
        public int MaxRows;

        // the field a heading names ("" when none): Fx (kip) -> Fx, MEMBER -> Beam, Joint -> Node, Load Case -> LC
        public static string FieldOf(string heading, out string unit)
        {
            unit = "";
            string h = heading ?? "";
            Match m = Regex.Match(h, @"[\(\[]\s*([^\)\]]+?)\s*[\)\]]");
            if (m.Success) { unit = m.Groups[1].Value; h = h.Substring(0, m.Index); }
            h = Regex.Replace(h, @"[\s._#:]+", " ").Trim().ToUpperInvariant();
            switch (h)
            {
                case "FX": return "Fx"; case "FY": return "Fy"; case "FZ": return "Fz";
                case "MX": return "Mx"; case "MY": return "My"; case "MZ": return "Mz";
                case "BEAM": case "BEAM NO": case "BEAM ID": case "MEMBER": case "MEMBER NO": case "MEMB": case "LINE": return "Beam";
                case "NODE": case "JOINT": case "JT": return "Node";
                case "LC": case "LOAD": case "LOAD CASE": case "CASE": case "COMB": case "LOAD COMB": case "COMBINATION": return "LC";
                case "SECTION": case "SIZE": case "PROFILE": return "Section";
                default: return "";
            }
        }

        // the load table of a template sheet: an Excel table harvest listed, else the first row with three or more force
        // headings side by side; null when there is none
        public static LoadTable Find(HarvestResult h, string template, string sheet)
        {
            var cells = h.Layout.Where(x => Val(x, "Template") == template && Val(x, "Sheet") == sheet).ToList();
            var at = new Dictionary<long, string>();            // (row, col) -> the cell's text
            var rowsUsed = new HashSet<int>();
            var calculated = new HashSet<long>();               // the cells that hold a formula
            foreach (var c in cells)
            {
                int col, row;
                if (!Xlsx.ParseRef(Val(c, "Cell"), out col, out row) || row < 0) continue;
                rowsUsed.Add(row);
                string v = Val(c, "Value");
                if (Val(c, "Formula").Length > 0) calculated.Add(((long)row << 20) | (uint)col);
                else if (v.Length > 0) at[((long)row << 20) | (uint)col] = v;
            }
            foreach (int row in rowsUsed.OrderBy(x => x))
            {
                var heads = at.Where(kv => (int)(kv.Key >> 20) == row).ToDictionary(kv => (int)(kv.Key & 0xFFFFF), kv => kv.Value);
                string u;
                // a run of named headings in adjacent columns that holds three or more forces (Fx | value | Fy | value is
                // a column of inputs with their labels, not a table) over cells that are typed, not calculated (forces
                // the template works out per bolt or per weld segment are its own table)
                int first = -1, last = -1;
                foreach (int c0 in heads.Keys.OrderBy(x => x))
                {
                    if (FieldOf(heads[c0], out u).Length == 0 || (heads.ContainsKey(c0 - 1) && FieldOf(heads[c0 - 1], out u).Length > 0)) continue;
                    int c1 = c0, forces = 0;
                    while (heads.ContainsKey(c1) && FieldOf(heads[c1], out u).Length > 0)
                    {
                        string f0 = FieldOf(heads[c1], out u);
                        if (f0.Length == 2 && (f0[0] == 'F' || f0[0] == 'M'))
                        {
                            if (calculated.Contains(((long)(row + 1) << 20) | (uint)c1)) { forces = -1000; }
                            forces++;
                        }
                        c1++;
                    }
                    if (forces >= 3) { first = c0; last = c1 - 1; break; }
                }
                if (first < 0) continue;
                var t = new LoadTable();
                t.Sheet = sheet; t.At = Xlsx.Ref(row + 1, first);
                for (int c = first; c <= last; c++)
                {
                    string f = heads.ContainsKey(c) ? FieldOf(heads[c], out u) : "";
                    u = ""; if (heads.ContainsKey(c)) FieldOf(heads[c], out u);
                    t.Fields.Add(f.Length == 0 ? "-" : f);
                    Unit parsed;
                    if (f.Length == 2 && u.Length > 0 && Units.TryParse(u, out parsed))
                    {
                        if (f[0] == 'F' && t.ForceUnit.Length == 0) t.ForceUnit = u;
                        if (f[0] == 'M' && t.MomentUnit.Length == 0) t.MomentUnit = u;
                    }
                }
                // its rows: the consecutive rows below the heading that hold anything (sample data, or formulas beside)
                int n = 0;
                while (rowsUsed.Contains(row + 1 + n)) n++;
                t.MaxRows = n;
                return t;
            }
            return null;
        }

        static string Val(Dictionary<string, string> r, string k) { string v; return r.TryGetValue(k, out v) ? (v ?? "").Trim() : ""; }
    }

    public static class ProjectBook
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        public const string ModelConfig = "Model Config", ConnectionLib = "Connection Lib", TemplatesTab = "Templates", CellsTab = "Template Cells",
            OutputsTab = "Outputs", SignMaps = "Sign Maps", LoadCases = "Load Cases";
        public const int SourceRow = 1, UnitRow = 2, ValueRow = 3, HeaderRow = 4, FirstMarkRow = 5;     // of a schedule tab, 0-based

        public const string Typed = "Typed here", Own = "Template's own value";
        public static int MinInputs = 5;     // inputs a sheet needs to get a schedule tab of its own
        // what a schedule column can be fed by -> the library binding it becomes
        public static readonly string[][] Sources =
        {
            new[] { Typed, "" }, new[] { Own, "" },
            new[] { "Fx", "load:Fx" }, new[] { "Fy", "load:Fy" }, new[] { "Fz", "load:Fz" },
            new[] { "Mx", "load:Mx" }, new[] { "My", "load:My" }, new[] { "Mz", "load:Mz" },
            new[] { "Load case", "case:id" }, new[] { "Load case title", "case:title" },
            new[] { "Beam number", "pkg:this.Member" }, new[] { "Node", "pkg:this.Node" }, new[] { "Beam section", "pkg:this.Section" },
            new[] { "Framed-into member", "pkg:support.Member" }, new[] { "Framed-into section", "pkg:support.Section" }
        };
        static readonly string[] ModelCols = { "Beam ID", "Node I", "Node J", "Section", "Shape", "Connection I", "Connection J", "Frames into I", "Frames into J", "Comment" };
        static readonly string[] LibCols = { "Connection", "Template", "Description" };
        static readonly string[] TemplateCols = { "Template", "File", "Sheet", "Sha256", "Loads", "Basis", "Sign map", "Load table at", "Load table columns", "Max rows", "Force unit", "Moment unit", "Notes" };
        // what a column of a load table can hold -> the library binding it becomes ("-": a column the tool leaves alone)
        public static readonly string[][] TableFields =
        {
            new[] { "Beam", "pkg:this.Member" }, new[] { "Node", "pkg:this.Node" }, new[] { "LC", "case:id" }, new[] { "LC title", "case:title" },
            new[] { "Fx", "load:Fx" }, new[] { "Fy", "load:Fy" }, new[] { "Fz", "load:Fz" }, new[] { "Mx", "load:Mx" }, new[] { "My", "load:My" }, new[] { "Mz", "load:Mz" },
            new[] { "Section", "pkg:this.Section" }, new[] { "Connection", "reg:Connection" }, new[] { "Mark", "reg:Type" }, new[] { "-", "" }
        };
        static readonly string[] CellCols = { "Template", "Column", "Sheet", "Cell", "Label cell", "Label", "Unit", "Kind", "List", "Template value" };
        static readonly string[] OutputCols = { "Template", "Use", "Check", "Kind", "Expect", "Per row", "Result", "Sheet", "Cell", "Label cell", "Label" };
        static readonly string[] CaseCols = { "LC", "Title", "Kind", "Use", "Basis" };
        static readonly string[] Reserved = { "Start", ModelConfig, ConnectionLib, TemplatesTab, CellsTab, OutputsTab, SignMaps, LoadCases };

        // ================================================================= writing the tabs

        // pkg and harvest may each be null (no .anl yet, no templates yet): their tabs are written empty
        public static List<XOutSheet> Sheets(AnalysisPackage pkg, HarvestResult harvest)
        {
            var sheets = new List<XOutSheet>();
            var names = new HashSet<string>(Reserved, StringComparer.OrdinalIgnoreCase);
            var templates = new List<Dictionary<string, string>>();
            var cells = new List<Dictionary<string, string>>();
            var outputs = new List<Dictionary<string, string>>();
            var schedules = new List<XOutSheet>();

            if (harvest != null)
                foreach (var h in harvest.Templates)
                {
                    string hid = V(h, "Template");
                    var ins = harvest.Inputs.Where(x => V(x, "Template") == hid && V(x, "Kind") != "table").ToList();
                    var all = ins.GroupBy(x => V(x, "Sheet")).ToList();
                    // a sheet with a load table (a header row of beam / node / case / force names over rows to fill)
                    var tables = new List<LoadTable>();
                    foreach (string sh in harvest.Layout.Where(x => V(x, "Template") == hid).Select(x => V(x, "Sheet")).Distinct())
                    {
                        LoadTable lt = LoadTable.Find(harvest, hid, sh);
                        if (lt != null) tables.Add(lt);
                    }
                    // the calc tabs: a workbook with a load table is one calc per table, and its schedule carries the
                    // inputs of every sheet (the table's sheet first); else each sheet with a handful of inputs is a
                    // calc tab, and one with a constant or two is not, unless it is all the workbook has
                    var plans = new List<KeyValuePair<string, LoadTable>>();       // sheet, its load table (null: none)
                    if (tables.Count > 0) foreach (LoadTable lt in tables) plans.Add(new KeyValuePair<string, LoadTable>(lt.Sheet, lt));
                    else
                    {
                        var bySheet = all.Where(g => g.Count() >= MinInputs).ToList();
                        if (bySheet.Count == 0 && all.Count > 0) bySheet.Add(all.OrderByDescending(g => g.Count()).First());
                        foreach (var g in bySheet) plans.Add(new KeyValuePair<string, LoadTable>(g.Key, null));
                    }
                    var inputSheets = new HashSet<string>(plans.Select(x => x.Key), StringComparer.OrdinalIgnoreCase);
                    foreach (var pl in plans)
                    {
                        string sheet = pl.Key;
                        LoadTable table = pl.Value;
                        // the workbook's name, with the sheet's when it has several calc tabs and both fit
                        string wanted = plans.Count == 1 ? hid : (hid.Length + sheet.Length + 1 <= 31 ? hid + " " + sheet : sheet);
                        string name = TabName(wanted, names);
                        var t = Row("Template", name, "File", V(h, "File"), "Sheet", sheet, "Sha256", V(h, "Sha256"), "Loads", table == null ? "Envelope" : "All cases", "Basis", "", "Sign map", "IDENTITY");
                        if (table != null)
                        {
                            t["Load table at"] = table.At; t["Load table columns"] = string.Join(", ", table.Fields.ToArray());
                            t["Max rows"] = table.MaxRows > 0 ? I(table.MaxRows) : ""; t["Force unit"] = table.ForceUnit; t["Moment unit"] = table.MomentUnit;
                        }
                        templates.Add(t);
                        var cols = new List<Dictionary<string, string>>();
                        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Connection" };
                        IEnumerable<Dictionary<string, string>> mine = table == null ? ins.Where(x => V(x, "Sheet") == sheet)
                            : ins.Where(x => V(x, "Sheet") == sheet).Concat(ins.Where(x => V(x, "Sheet") != sheet));
                        foreach (var i in mine)
                        {
                            string label = Regex.Replace(V(i, "GuardText"), @"\s+", " ").Trim();
                            string col = label.Length == 0 ? V(i, "Cell") : label;
                            if (V(i, "Sheet") != sheet) col = V(i, "Sheet") + ": " + col;      // another sheet of the workbook
                            if (!used.Add(col)) { col = col + " [" + V(i, "Cell") + "]"; used.Add(col); }
                            var c = Row("Template", name, "Column", col, "Sheet", V(i, "Sheet"), "Cell", V(i, "Cell"), "Label cell", V(i, "GuardCell"), "Label", V(i, "GuardText"),
                                "Unit", V(i, "Unit"), "Kind", V(i, "Kind"), "List", V(i, "List"), "Template value", V(i, "Example"));
                            cols.Add(c); cells.Add(c);
                        }
                        schedules.Add(Schedule(name, V(h, "File"), sheet, cols, schedules.Count + 1, table != null));
                        // results: of this sheet, and of the workbook's sheets that take no input (solver flags); of the
                        // whole workbook when it is one calc
                        foreach (var o in harvest.Outputs.Where(x => V(x, "Template") == hid && (table != null || V(x, "Sheet") == sheet || !inputSheets.Contains(V(x, "Sheet")))))
                        {
                            string result = V(o, "Output").Length > 0 ? V(o, "Output") : V(o, "Cell");
                            bool governs = V(o, "Kind") == "dcr" && Regex.IsMatch(result, "govern|overall|max", RegexOptions.IgnoreCase);
                            outputs.Add(Row("Template", name, "Use", governs ? "Y" : "", "Check", governs ? "DCR" : "", "Kind", V(o, "Kind"), "Expect", V(o, "Expect"), "Per row", "",
                                "Result", result, "Sheet", V(o, "Sheet"), "Cell", V(o, "Cell"), "Label cell", V(o, "GuardCell"), "Label", V(o, "GuardText")));
                        }
                    }
                }

            // ---- Model Config
            var beams = new List<Dictionary<string, string>>();
            if (pkg != null)
                foreach (PkgMember m in pkg.Members.Values.OrderBy(x => x.Id))
                {
                    PkgEnd a = pkg.End(m.NodeA, m.Id, "A"), b = pkg.End(m.NodeB, m.Id, "B");
                    int fa = a == null ? 0 : Register.GuessSupport(pkg, a), fb = b == null ? 0 : Register.GuessSupport(pkg, b);
                    beams.Add(Row("Beam ID", I(m.Id), "Node I", I(m.NodeA), "Node J", I(m.NodeB), "Section", m.Section, "Shape", m.Family,
                        "Frames into I", fa == 0 ? "" : I(fa), "Frames into J", fb == 0 ? "" : I(fb)));
                }
            XOutSheet mc = WorkbookTables.Sheet(ModelConfig, "tblModelConfig", ModelCols, beams,
                "One row per beam of the model. Choose the connection at each end (a mark from Connection Lib; blank = no calc there). Frames into: the member that end connects to (a first guess; correct it).",
                c => c == "Beam ID" ? ColumnKind.Key : (c == "Node I" || c == "Node J" || c == "Section" || c == "Shape") ? ColumnKind.Reference : ColumnKind.Input);
            foreach (string c in new[] { "Beam ID", "Node I", "Node J", "Frames into I", "Frames into J" }) WorkbookTables.Numbers(mc, Array.IndexOf(ModelCols, c), "0");
            int last = 2 + Math.Max(beams.Count, 1) + 2000;
            Validate(mc, "F3:G" + last, "'" + ConnectionLib + "'!$A$3:$A$2000", true);
            mc.FreezeCols = 1;
            sheets.Add(mc);

            // ---- Connection Lib
            XOutSheet cl = WorkbookTables.Sheet(ConnectionLib, "tblConnectionLib", LibCols, new List<Dictionary<string, string>>(),
                "One row per connection mark: a name of your own, and the template it uses. A mark is one template plus one row of values on that template's tab; two marks may share a template. Add rows below.",
                c => c == "Connection" ? ColumnKind.Key : ColumnKind.Input);
            Validate(cl, "B3:B2000", "'" + TemplatesTab + "'!$A$3:$A$500", true);
            cl.Width(0, 22); cl.Width(1, 34); cl.Width(2, 60);
            sheets.Add(cl);

            // ---- Templates
            XOutSheet tt = WorkbookTables.Sheet(TemplatesTab, "tblConfigTemplates", TemplateCols, templates,
                "One row per calc template (a tab of a template workbook), each with a schedule tab of the same name. Loads: Envelope (the largest and smallest of each force with what acts with it) or All cases. Basis: LRFD or ASD when the template takes only one. Sign map: a row of Sign Maps (IDENTITY = the member end's forces as they are). Load table at: the first cell of the template's table of load rows (blank = none); its columns in order (Beam, Node, LC, LC title, Fx to Mz, Section, Connection, Mark, or - for a column to leave alone); how many rows it holds; the units it takes.",
                c => c == "Template" ? ColumnKind.Key : (c == "File" || c == "Sheet" || c == "Sha256" || c == "Notes") ? ColumnKind.Reference : ColumnKind.Input);
            tt.Width(Array.IndexOf(TemplateCols, "Load table columns"), 48);
            int tn = 2 + Math.Max(templates.Count, 1);
            Validate(tt, "E3:E" + tn, "\"Envelope,All cases\"", true);
            Validate(tt, "F3:F" + tn, "\"LRFD,ASD\"", true);
            sheets.Add(tt);

            sheets.AddRange(schedules);

            // ---- Template Cells, Outputs, Sign Maps, Load Cases
            sheets.Add(WorkbookTables.Sheet(CellsTab, "tblTemplateCells", CellCols, cells,
                "Where each schedule column goes in its template, as harvest found it. For reference: the tool reads this tab, so leave it as it is.",
                c => (c == "Template" || c == "Column") ? ColumnKind.Key : ColumnKind.Reference));
            XOutSheet os = WorkbookTables.Sheet(OutputsTab, "tblConfigOutputs", OutputCols, outputs,
                "What is read back from each template. Use = Y on the results you want. Check: the summary column it goes in (several results may share one: the largest counts). Kind: dcr (a ratio), status (OK / NG), health (a flag that must equal Expect), value. Per row = Y for a result beside a load table (one per row; give its first cell). A result harvest did not find can be added as a row below.",
                c => c == "Template" ? ColumnKind.Key : (c == "Use" || c == "Check" || c == "Kind" || c == "Expect" || c == "Per row") ? ColumnKind.Input : ColumnKind.Reference);
            int on = 2 + Math.Max(outputs.Count, 1);
            Validate(os, "B3:B" + on, "\"Y,N\"", true);
            Validate(os, "D3:D" + on, "\"" + string.Join(",", LibrarySchema.OutputKinds) + "\"", true);
            Validate(os, "F3:F" + (on + 200), "\"Y,N\"", true);
            sheets.Add(os);
            sheets.Add(WorkbookTables.Sheet(SignMaps, "tblSignMaps", LibrarySchema.FrameColumns, new List<Dictionary<string, string>>(),
                "Only when a template's axes differ from the member end's. A member end's forces are in its own axes: x along the member away from the joint, y the member's local y, z = x cross y, STAAD's sign. A row says which of them each template force is, e.g. Fx = +Fz. Action on_connection reverses all six. Confirmed = Y once checked by hand.",
                c => c == "Frame" ? ColumnKind.Key : ColumnKind.Input));
            var cases = new List<Dictionary<string, string>>();
            if (pkg != null)
            {
                bool combos = pkg.Cases.Values.Any(c => c.HasForces && (c.Kind == "COMBINATION" || c.Kind == "REPEAT"));
                foreach (PkgCase c in pkg.Cases.Values)
                    cases.Add(Row("LC", I(c.Id), "Title", c.Title, "Kind", c.Kind + (c.Method.Length > 0 ? " " + c.Method : ""),
                        "Use", c.HasForces && (!combos || c.Kind == "COMBINATION" || c.Kind == "REPEAT") ? "Y" : "N", "Basis", ""));
            }
            XOutSheet lc = WorkbookTables.Sheet(LoadCases, "tblConfigCases", CaseCols, cases,
                "The load cases the calcs take: Use = Y. Basis: LRFD or ASD, so a template of one basis takes only its cases. SRSS and ABS combinations carry no real signs: think before using them where uplift or tension matters.",
                c => c == "LC" ? ColumnKind.Key : (c == "Use" || c == "Basis") ? ColumnKind.Input : ColumnKind.Reference);
            int cn = 2 + Math.Max(cases.Count, 1);
            WorkbookTables.Numbers(lc, 0, "0");
            Validate(lc, "D3:D" + cn, "\"Y,N\"", true);
            Validate(lc, "E3:E" + cn, "\"LRFD,ASD\"", true);
            sheets.Add(lc);
            return sheets;
        }

        // one template's schedule: Source / Unit / template value rows, the header, then a row per mark
        static XOutSheet Schedule(string name, string file, string sheet, List<Dictionary<string, string>> cols, int index, bool table)
        {
            var s = new XOutSheet(name);
            var label = new XStyle(); label.Bold = true; label.Color = Palette.GreyText;
            var grey = Palette.Data(ColumnKind.Reference);
            var input = Palette.Data(ColumnKind.Input);
            s.Add(new XCell("One row per connection mark that uses " + file + ", sheet " + sheet + ". Every input of the template is a column: fill the ones typed here, leave a cell blank to keep the template's own value. Source says where a column's value comes from; a force needs its Unit." +
                (table ? " This template takes a table of load rows (Templates tab): a mark is ONE copy of it, and its table lists the beam ends that call the mark." : ""), Palette.Note()));
            var src = new List<object> { new XCell("Source", label) };
            var unit = new List<object> { new XCell("Unit", label) };
            var val = new List<object> { new XCell("Template value", label) };
            var head = new List<object> { new XCell("Connection", Palette.Header(ColumnKind.Key)) };
            foreach (var c in cols)
            {
                src.Add(new XCell(GuessSource(V(c, "Label"), V(c, "Cell"), V(c, "Unit")), input));
                unit.Add(new XCell(V(c, "Unit"), input));
                val.Add(new XCell(V(c, "Template value"), grey));
                head.Add(new XCell(V(c, "Column"), Palette.Header(ColumnKind.Input)));
            }
            s.Add(src.ToArray()); s.Add(unit.ToArray()); s.Add(val.ToArray()); s.Add(head.ToArray());
            var first = new object[cols.Count + 1];
            first[0] = new XCell("", Palette.Data(ColumnKind.Key));
            for (int i = 1; i < first.Length; i++) first[i] = new XCell("", input);
            s.Add(first);
            s.RowHeights[HeaderRow] = 30;
            var t = new XOutTable();
            t.Name = "tblSchedule" + index.ToString(Inv); t.Top = HeaderRow; t.Left = 0; t.Right = cols.Count; t.Bottom = FirstMarkRow; t.Style = Palette.TableStyle;
            s.Tables.Add(t);
            string lastCol = Xlsx.ColName(Math.Max(cols.Count, 1));
            Validate(s, "B" + (SourceRow + 1) + ":" + lastCol + (SourceRow + 1), "\"" + string.Join(",", Sources.Select(x => x[0]).ToArray()) + "\"", true);
            Validate(s, "A" + (FirstMarkRow + 1) + ":A2000", "'" + ConnectionLib + "'!$A$3:$A$2000", false);
            for (int i = 0; i < cols.Count; i++)
            {
                string list = V(cols[i], "List");
                if (list.Length > 0 && list.Length < 250 && list.IndexOf('"') < 0)
                    Validate(s, Xlsx.ColName(i + 1) + (FirstMarkRow + 1) + ":" + Xlsx.ColName(i + 1) + "2000", "\"" + list + "\"", false);
                s.Width(i + 1, Math.Min(30, Math.Max(12, V(cols[i], "Column").Length + 3)));
            }
            s.Width(0, 18);
            s.FreezeRows = HeaderRow + 1; s.FreezeCols = 1;
            return s;
        }

        // a column whose label or name is one of the six force names takes that force, unless its unit says it is
        // something else (Fy (ksi) is a yield strength); the rest are typed
        public static string GuessSource(string label, string cell, string unit)
        {
            Unit u = null;
            bool known = !string.IsNullOrEmpty(unit) && Units.TryParse(unit, out u);
            foreach (string t in new[] { (label ?? "").Trim(), (cell ?? "").Trim() })
            {
                Match m = Regex.Match(t, @"^([FM][xyz])(?:[_\s(,].*)?$", RegexOptions.IgnoreCase);
                if (!m.Success) continue;
                bool force = char.ToUpperInvariant(m.Groups[1].Value[0]) == 'F';
                if (known && u.Dim != (force ? Dimension.Force : Dimension.Moment)) return Typed;
                return (force ? "F" : "M") + m.Groups[1].Value.Substring(1).ToLowerInvariant();
            }
            return Typed;
        }

        static void Validate(XOutSheet s, string sqref, string list, bool strict)
        {
            var v = new XValidation();
            v.Sqref = sqref; v.List = list; v.Strict = strict;
            s.Validations.Add(v);
        }

        // a tab name Excel takes (31 characters, none of : \ / ? * [ ]), unique in the workbook
        static string TabName(string wanted, HashSet<string> used)
        {
            string b = Regex.Replace(wanted ?? "", @"[:\\/?*\[\]']", " ").Trim();
            if (b.Length == 0) b = "Template";
            if (b.Length > 31) b = b.Substring(0, 31).Trim();
            string n = b;
            for (int i = 2; !used.Add(n); i++)
            {
                string x = " " + i.ToString(Inv);
                n = (b.Length + x.Length > 31 ? b.Substring(0, 31 - x.Length) : b) + x;
            }
            return n;
        }

        // ================================================================= reading them back

        // the saved workbook -> outDir\library\text\*.csv and outDir\Connection_Register.xlsx (both generated)
        public static ExpandResult Expand(string workbook, AnalysisPackage pkg, string outDir, string dcrLimit) { return Expand(workbook, pkg, outDir, dcrLimit, ""); }

        // templatesDir (optional): where the template files are, to read the heading above each load-table column as its guard
        public static ExpandResult Expand(string workbook, AnalysisPackage pkg, string outDir, string dcrLimit, string templatesDir)
        {
            var res = new ExpandResult();
            XWorkbook wb = Xlsx.Read(workbook, 2048);
            var tableMarks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);     // marks whose template takes a load table: one calc each
            var rows = new Dictionary<string, List<Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);
            foreach (TableDef t in LibrarySchema.Tables) rows[t.Name] = new List<Dictionary<string, string>>();

            List<Dictionary<string, string>> templates = Tab(wb, TemplatesTab, res), cellRows = Tab(wb, CellsTab, res), lib = Tab(wb, ConnectionLib, res),
                model = Tab(wb, ModelConfig, res), outs = Tab(wb, OutputsTab, res), maps = Tab(wb, SignMaps, res), cases = Tab(wb, LoadCases, res);
            var cellOf = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in cellRows) cellOf[V(c, "Template") + "\n" + V(c, "Column")] = c;
            var frames = new HashSet<string>(maps.Select(m => V(m, "Frame")).Where(f => f.Length > 0), StringComparer.OrdinalIgnoreCase) { "IDENTITY" };
            foreach (var m in maps.Where(x => V(x, "Frame").Length > 0)) rows["tblFrames"].Add(Strip(m));

            // ---- marks
            var markTemplate = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in lib)
            {
                string mark = V(r, "Connection"), tpl = V(r, "Template");
                if (mark.Length == 0) { if (tpl.Length > 0) res.Error(ConnectionLib, "row " + Xl(r) + " has a template but no connection name"); continue; }
                if (markTemplate.ContainsKey(mark)) { res.Error(ConnectionLib, "connection " + mark + " is listed twice"); continue; }
                if (tpl.Length == 0) { res.Error(ConnectionLib, "connection " + mark + " has no template: choose one in the Template column"); continue; }
                if (!templates.Any(t => Same(V(t, "Template"), tpl))) { res.Error(ConnectionLib, "connection " + mark + ": no template '" + tpl + "' on the Templates tab"); continue; }
                markTemplate[mark] = tpl;
            }
            res.Marks = markTemplate.Count;

            // ---- templates and their schedules
            foreach (var t in templates)
            {
                string name = V(t, "Template");
                if (name.Length == 0) continue;
                var mine = markTemplate.Where(kv => Same(kv.Value, name)).Select(kv => kv.Key).ToList();
                XSheet s = wb.Sheet(name);
                if (s == null) { if (mine.Count > 0) res.Error(TemplatesTab, "template " + name + " has no schedule tab of that name"); continue; }
                if (mine.Count == 0)
                {
                    // a template no connection uses is left alone; a row on its tab still has to name a known mark
                    for (int r = FirstMarkRow, blanks = 0; r < s.Rows.Count && blanks < 50; r++)
                    {
                        string mark = s.Text(r, 0).Trim();
                        if (mark.Length == 0) { blanks++; continue; }
                        blanks = 0;
                        string of;
                        if (!markTemplate.TryGetValue(mark, out of)) res.Error(name, "row " + (r + 1) + ": connection '" + mark + "' is not on the " + ConnectionLib + " tab");
                        else res.Error(name, "row " + (r + 1) + ": connection " + mark + " uses template " + of + " (" + ConnectionLib + "), so its row belongs on that tab");
                    }
                    continue;
                }
                string frame = V(t, "Sign map").Length == 0 ? "IDENTITY" : V(t, "Sign map");
                if (!frames.Contains(frame)) res.Error(TemplatesTab, "template " + name + ": no sign map '" + frame + "' on the Sign Maps tab");
                string loads = V(t, "Loads").Length == 0 ? "Envelope" : V(t, "Loads");
                if (!Same(loads, "Envelope") && !Same(loads, "All cases")) res.Error(TemplatesTab, "template " + name + ": Loads is Envelope or All cases (got '" + loads + "')");
                var trow = Row("Template", name, "File", V(t, "File"), "Sha256", V(t, "Sha256"), "Shape", "single_case", "Basis", V(t, "Basis"));
                rows["tblTemplates"].Add(trow);
                res.Templates++;
                if (V(t, "Load table at").Length > 0)
                {
                    trow["Shape"] = "case_table";
                    foreach (string mk in mine) tableMarks.Add(mk);
                    LoadTableInputs(t, name, templatesDir, rows["tblInputs"], trow, res);
                }

                // the columns
                var typed = new List<KeyValuePair<int, string[]>>();     // column index -> param name, unit
                bool support = false;
                for (int c = 1; s.Text(HeaderRow, c).Trim().Length > 0; c++)
                {
                    string col = s.Text(HeaderRow, c).Trim(), source = s.Text(SourceRow, c).Trim(), unit = s.Text(UnitRow, c).Trim();
                    Dictionary<string, string> cell;
                    if (!cellOf.TryGetValue(name + "\n" + col, out cell)) { res.Error(name, "column '" + col + "' is not one of the template's inputs (was its heading changed? see " + CellsTab + ")"); continue; }
                    if (source.Length == 0) source = Typed;
                    if (Same(source, Own)) continue;
                    string[] known = Sources.FirstOrDefault(x => Same(x[0], source));
                    if (known == null) { res.Error(name, "column '" + col + "': Source '" + source + "' is not one of the choices"); continue; }
                    var input = Row("Template", name, "Sheet", V(cell, "Sheet"), "Cell", V(cell, "Cell"), "GuardCell", V(cell, "Label cell"), "GuardText", V(cell, "Label"), "Unit", unit);
                    if (Same(source, Typed))
                    {
                        string param = "c_" + V(cell, "Cell");
                        input["Binding"] = "param:" + param; input["PerCase"] = "N"; input["Required"] = "N";
                        typed.Add(new KeyValuePair<int, string[]>(c, new[] { param, unit, col }));
                    }
                    else
                    {
                        input["Binding"] = known[1]; input["Required"] = "Y";
                        input["PerCase"] = known[1].StartsWith("load:", StringComparison.Ordinal) || known[1].StartsWith("case:", StringComparison.Ordinal) ? "Y" : "N";
                        if (known[1].StartsWith("load:", StringComparison.Ordinal) && unit.Length == 0)
                            res.Error(name, "column '" + col + "' takes " + source + ": give its unit in the Unit row (kip, lbf, kN; kip-in, kip-ft, kN-m ...)");
                        if (known[1].StartsWith("pkg:support.", StringComparison.Ordinal)) support = true;
                    }
                    if (unit.Length > 0) { Unit u; if (!Units.TryParse(unit, out u)) res.Error(name, "column '" + col + "': '" + unit + "' is not a unit CalcFlow knows"); }
                    rows["tblInputs"].Add(input);
                }

                // the rows: one per mark
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int blankRun = 0;
                for (int r = FirstMarkRow; r < s.Rows.Count && blankRun < 50; r++)
                {
                    string mark = s.Text(r, 0).Trim();
                    if (mark.Length == 0) { blankRun++; continue; }
                    blankRun = 0;
                    string of;
                    if (!markTemplate.TryGetValue(mark, out of)) { res.Error(name, "row " + (r + 1) + ": connection '" + mark + "' is not on the " + ConnectionLib + " tab"); continue; }
                    if (!Same(of, name)) { res.Error(name, "row " + (r + 1) + ": connection " + mark + " uses template " + of + " (" + ConnectionLib + "), so its row belongs on that tab"); continue; }
                    if (!seen.Add(mark)) { res.Error(name, "connection " + mark + " has two rows: keep one"); continue; }
                    foreach (var tc in typed)
                    {
                        string v = s.Text(r, tc.Key).Trim();
                        if (v.Length > 0) rows["tblParams"].Add(Row("Type", mark, "Step", "", "Param", tc.Value[0], "Default", v, "Unit", tc.Value[1], "Notes", tc.Value[2]));
                    }
                    rows["tblTypes"].Add(Row("Type", mark, "Description", V(lib.First(x => Same(V(x, "Connection"), mark)), "Description"), "Slots", support ? "support" : ""));
                    rows["tblSteps"].Add(Row("Type", mark, "Step", "1", "Template", name, "Demand", Same(loads, "All cases") ? "per_combo" : "concurrent_envelope", "Slot", "this", "Frame", frame));
                }
                foreach (string mark in mine.Where(x => !seen.Contains(x)))
                    res.Error(name, "connection " + mark + " has no row on this tab: add one (" + ConnectionLib + " says it uses this template)");

                // what is read back
                int used = 0;
                foreach (var o in outs.Where(x => Same(V(x, "Template"), name) && V(x, "Use").StartsWith("Y", StringComparison.OrdinalIgnoreCase)))
                {
                    used++;
                    string kind = V(o, "Kind").ToLowerInvariant();
                    if (kind == "health" && V(o, "Expect").Length == 0) res.Error(OutputsTab, name + ", " + V(o, "Result") + ": a health result needs Expect (the value it must have, e.g. TRUE)");
                    rows["tblOutputs"].Add(Row("Template", name, "Sheet", V(o, "Sheet"), "Cell", V(o, "Cell"), "GuardCell", V(o, "Label cell"), "GuardText", V(o, "Label"),
                        "Output", V(o, "Result"), "Kind", kind, "Check", V(o, "Check").Length > 0 ? V(o, "Check") : V(o, "Result"), "Expect", V(o, "Expect"),
                        "PerCase", V(o, "Per row").StartsWith("Y", StringComparison.OrdinalIgnoreCase) ? "Y" : "N"));
                }
                if (used == 0 && mine.Count > 0) res.Error(OutputsTab, "template " + name + " has no result with Use = Y: nothing would be read back");
            }

            // ---- Model Config -> the register: every member end of the model, the ones with a connection carrying its mark
            var marked = new Dictionary<string, string[]>(StringComparer.Ordinal);     // "member|A" -> mark, frames into, comment
            var inConfig = new HashSet<int>();
            foreach (var b in model)
            {
                int id, ni, nj;
                if (!int.TryParse(V(b, "Beam ID"), NumberStyles.Integer, Inv, out id)) { if (V(b, "Connection I").Length + V(b, "Connection J").Length > 0) res.Error(ModelConfig, "row " + Xl(b) + " has a connection but no Beam ID"); continue; }
                if (!inConfig.Add(id)) { res.Error(ModelConfig, "beam " + id + " has two rows: keep one"); continue; }
                PkgMember m;
                bool known = pkg.Members.TryGetValue(id, out m);
                foreach (string e in new[] { "I", "J" })
                {
                    string mark = V(b, "Connection " + e);
                    if (mark.Length == 0) continue;
                    if (!known) { res.Error(ModelConfig, "beam " + id + " is not in the model (renumbered or removed?), but has connection " + mark + " at end " + e); continue; }
                    if (!markTemplate.ContainsKey(mark)) { res.Error(ModelConfig, "beam " + id + ", end " + e + ": connection '" + mark + "' is not on the " + ConnectionLib + " tab"); continue; }
                    int node = e == "I" ? m.NodeA : m.NodeB;
                    string into = V(b, "Frames into " + e);
                    int fi;
                    if (into.Length > 0 && (!int.TryParse(into, NumberStyles.Integer, Inv, out fi) || pkg.EndAt(node, fi) == null))
                        res.Error(ModelConfig, "beam " + id + ", end " + e + ": 'Frames into' " + into + " is not a member at joint " + node);
                    marked[I(id) + "|" + (e == "I" ? "A" : "B")] = new[] { mark, into, V(b, "Comment") };
                }
                if (known && int.TryParse(V(b, "Node I"), NumberStyles.Integer, Inv, out ni) && int.TryParse(V(b, "Node J"), NumberStyles.Integer, Inv, out nj) && (ni != m.NodeA || nj != m.NodeB))
                    res.Warn(ModelConfig, "beam " + id + " runs from joint " + m.NodeA + " to " + m.NodeB + " in the model, not " + ni + " to " + nj + " as on its row (a new revision?): ends I and J follow the model");
            }
            var conns = new List<Dictionary<string, string>>();
            foreach (PkgEnd e in pkg.Ends.Values.OrderBy(x => x.Node).ThenBy(x => x.Member).ThenBy(x => x.End, StringComparer.Ordinal))
            {
                string[] mk;
                bool has = marked.TryGetValue(I(e.Member) + "|" + e.End, out mk);
                conns.Add(Row("Connection", RegisterSchema.ConnectionId(e.Node, e.Member, e.End), "Node", I(e.Node), "Member", I(e.Member), "End", e.End,
                    "Type", has ? mk[0] : "", "Support", has ? mk[1] : "", "Group", has && tableMarks.Contains(mk[0]) ? mk[0] : "", "Comment", has ? mk[2] : ""));
            }
            var missing = pkg.Members.Keys.Where(k => !inConfig.Contains(k)).OrderBy(k => k).ToList();
            if (missing.Count > 0)
                res.Warn(ModelConfig, missing.Count + " member(s) of the model have no row (new in this revision?): " + string.Join(", ", missing.Take(15).Select(I).ToArray()) + (missing.Count > 15 ? " ..." : "") + ". Copy them from Model_Data.xlsx, tab Members");
            res.Connections = marked.Count;
            if (marked.Count == 0) res.Error(ModelConfig, "no beam end has a connection yet: choose a mark in Connection I or Connection J");

            // ---- written: the library text and the register
            res.LibraryFolder = Path.Combine(outDir, "library");
            string text = Path.Combine(res.LibraryFolder, Library.TextFolder);
            Directory.CreateDirectory(text);
            foreach (TableDef t in LibrarySchema.Tables) Library.WriteCsv(Path.Combine(text, t.File), t, rows[t.Name]);
            var combos = cases.Where(c => V(c, "LC").Length > 0)
                .Select(c => Row("LC", V(c, "LC"), "Title", V(c, "Title"), "Kind", V(c, "Kind"), "Use", V(c, "Use").StartsWith("Y", StringComparison.OrdinalIgnoreCase) ? "Y" : "N", "Basis", V(c, "Basis"))).ToList();
            if (!combos.Any(c => c["Use"] == "Y")) res.Error(LoadCases, "no load case has Use = Y");
            var settings = new List<Dictionary<string, string>>
            {
                Row("Setting", "DcrLimit", "Value", string.IsNullOrEmpty(dcrLimit) ? "1.0" : dcrLimit),
                Row("Setting", "GoverningK", "Value", "5"),
                Row("Setting", "Package", "Value", pkg.Folder)
            };
            res.RegisterPath = Path.Combine(outDir, "Connection_Register.xlsx");
            if (File.Exists(res.RegisterPath)) File.Delete(res.RegisterPath);
            Register.WriteTables(res.RegisterPath, conns, new List<Dictionary<string, string>>(), combos, settings, markTemplate.Keys.ToList());
            return res;
        }

        // the load table of a Templates row -> one per-row input for each of its columns
        static void LoadTableInputs(Dictionary<string, string> t, string name, string templatesDir, List<Dictionary<string, string>> inputs, Dictionary<string, string> trow, ExpandResult res)
        {
            string at = V(t, "Load table at"), sheet = V(t, "Sheet");
            int col, row;
            if (!Regex.IsMatch(at, @"^\$?[A-Za-z]{1,3}\$?\d+$") || !Xlsx.ParseRef(at, out col, out row) || row < 0)
            { res.Error(TemplatesTab, "template " + name + ": 'Load table at' is the first cell of the table, like B12 (got '" + at + "')"); return; }
            int max;
            if (!int.TryParse(V(t, "Max rows"), NumberStyles.Integer, Inv, out max) || max <= 0)
                res.Error(TemplatesTab, "template " + name + ": give 'Max rows', the number of load rows its table holds");
            else trow["MaxCases"] = I(max);
            string[] fields = V(t, "Load table columns").Split(',').Select(x => x.Trim()).ToArray();
            if (fields.Length == 0 || fields.All(f => f.Length == 0)) { res.Error(TemplatesTab, "template " + name + ": give 'Load table columns', the table's columns in order (Beam, Node, LC, Fx ...)"); return; }
            TemplateBook book = null;
            if (!string.IsNullOrEmpty(templatesDir))
            {
                string file = Path.Combine(templatesDir, V(t, "File"));
                if (File.Exists(file)) { try { book = TemplateBook.Open(file); } catch (Exception) { book = null; } }
            }
            TSheet ts = book == null ? null : book.Sheet(sheet);
            bool any = false;
            for (int k = 0; k < fields.Length; k++)
            {
                string f = fields[k];
                if (f.Length == 0 || f == "-") continue;
                string[] known = TableFields.FirstOrDefault(x => Same(x[0], f));
                if (known == null) { res.Error(TemplatesTab, "template " + name + ": load table column '" + f + "' is not one of " + string.Join(", ", TableFields.Select(x => x[0]).ToArray())); continue; }
                bool force = known[1].StartsWith("load:F", StringComparison.Ordinal), moment = known[1].StartsWith("load:M", StringComparison.Ordinal);
                string unit = force ? V(t, "Force unit") : moment ? V(t, "Moment unit") : "";
                if ((force || moment) && unit.Length == 0) { res.Error(TemplatesTab, "template " + name + ": its load table takes " + known[0] + ": give the '" + (force ? "Force unit" : "Moment unit") + "' (kip, kN ...; kip-in, kip-ft, kN-m ...)"); continue; }
                if (unit.Length > 0) { Unit u; if (!Units.TryParse(unit, out u)) { res.Error(TemplatesTab, "template " + name + ": '" + unit + "' is not a unit CalcFlow knows"); continue; } }
                var input = Row("Template", name, "Sheet", sheet, "Cell", Xlsx.Ref(row, col + k), "Unit", unit, "Binding", known[1], "PerCase", "Y", "Required", "Y");
                // the heading above the column guards it, when the template is at hand
                if (ts != null && row > 0)
                {
                    string head = TemplateBook.TextOf(ts, row - 1, col + k);
                    if (head.Trim().Length > 0) { input["GuardCell"] = Xlsx.Ref(row - 1, col + k); input["GuardText"] = head; }
                }
                inputs.Add(input);
                any = true;
            }
            if (!any) res.Error(TemplatesTab, "template " + name + ": its load table has no column the tool fills");
        }

        // ---- small helpers

        // a tab's rows by its cells: the header is its second row, the rows run until 50 blank ones. Read this way, a
        // row typed or pasted below the table counts even where Excel did not grow the table over it.
        static List<Dictionary<string, string>> Tab(XWorkbook wb, string tab, ExpandResult res)
        {
            var list = new List<Dictionary<string, string>>();
            XSheet s = wb.Sheet(tab);
            if (s == null) { res.Error(tab, "the " + tab + " tab is missing from the workbook"); return list; }
            var head = new List<string>();
            for (int c = 0; s.Text(1, c).Trim().Length > 0; c++) head.Add(s.Text(1, c).Trim());
            int blank = 0;
            for (int r = 2; r < s.Rows.Count && blank < 50; r++)
            {
                var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                bool any = false;
                for (int c = 0; c < head.Count; c++) { string v = s.Text(r, c); d[head[c]] = v; if (v.Trim().Length > 0) any = true; }
                if (!any) { blank++; continue; }
                blank = 0;
                d["__row"] = (r + 1).ToString(Inv);
                list.Add(d);
            }
            return list;
        }

        static Dictionary<string, string> Strip(Dictionary<string, string> r)
        {
            var d = new Dictionary<string, string>(r, StringComparer.OrdinalIgnoreCase);
            d.Remove("__row");
            return d;
        }

        static string Xl(Dictionary<string, string> r) { string v; return r.TryGetValue("__row", out v) ? v : "?"; }
        static bool Same(string a, string b) { return string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase); }
        static string V(Dictionary<string, string> r, string k) { string v; return r.TryGetValue(k, out v) ? (v ?? "").Trim() : ""; }
        static string I(int n) { return n.ToString(Inv); }

        static Dictionary<string, string> Row(params string[] kv)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i + 1 < kv.Length; i += 2) d[kv[i]] = kv[i + 1];
            return d;
        }
    }
}
