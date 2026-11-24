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
        static readonly string[] TemplateCols = { "Template", "File", "Sheet", "Sha256", "Loads", "Basis", "Sign map", "Load table at", "Load table columns", "Max rows", "Force unit", "Moment unit", "Input cells", "Result cells", "Notes" };
        // what a refresh keeps of a Templates row, of a Model Config row and of a Load Cases row is everything but these
        static readonly string[] TemplateKept = { "Loads", "Basis", "Sign map", "Load table at", "Load table columns", "Max rows", "Force unit", "Moment unit", "Input cells", "Result cells", "Notes" };
        static readonly string[] ModelFixed = { "Beam ID", "Node I", "Node J", "Section", "Shape" };
        static readonly string[] CaseFixed = { "LC", "Title", "Kind" };
        public const string Blank = "(blank)";      // typed in a schedule cell: empty the template's cell
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
        public static List<XOutSheet> Sheets(AnalysisPackage pkg, HarvestResult harvest) { return Sheets(pkg, harvest, null, "", null); }

        // old: the saved workbook being refreshed (null: a new workbook). What was filled in is carried over: the marks,
        // the connection at each beam end, every schedule row with its Source and Unit rows, the Templates settings, the
        // results chosen on Outputs, the sign maps and the load-case choices. A template is known again by its file and
        // sheet, a beam by its number, a schedule column by its heading. templatesDir: where the template files are, for
        // a template whose input or result cells the engineer gave. notes: what a refresh has to say.
        public static List<XOutSheet> Sheets(AnalysisPackage pkg, HarvestResult harvest, XWorkbook old, string templatesDir, List<string> notes)
        {
            if (notes == null) notes = new List<string>();
            var sheets = new List<XOutSheet>();
            var names = new HashSet<string>(Reserved, StringComparer.OrdinalIgnoreCase);
            var templates = new List<Dictionary<string, string>>();
            var cells = new List<Dictionary<string, string>>();
            var outputs = new List<Dictionary<string, string>>();
            var schedules = new List<XOutSheet>();
            List<Dictionary<string, string>> oldTemplates = OldTab(old, TemplatesTab), oldCells = OldTab(old, CellsTab), oldLib = OldTab(old, ConnectionLib),
                oldModel = OldTab(old, ModelConfig), oldOuts = OldTab(old, OutputsTab), oldMaps = OldTab(old, SignMaps), oldCases = OldTab(old, LoadCases);
            foreach (var t in oldTemplates) if (V(t, "Template").Length > 0) names.Add(V(t, "Template"));
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);        // the old templates found again
            var books = new Dictionary<string, TemplateBook>(StringComparer.OrdinalIgnoreCase);

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
                    // a sheet the saved workbook holds as a template stays one, whatever is found on it now
                    foreach (var was in oldTemplates.Where(x => Same(V(x, "File"), V(h, "File")) && V(x, "Template").Length > 0))
                        if (!plans.Any(p => Same(p.Key, V(was, "Sheet"))) && harvest.Layout.Any(x => V(x, "Template") == hid && Same(V(x, "Sheet"), V(was, "Sheet"))))
                            plans.Add(new KeyValuePair<string, LoadTable>(harvest.Layout.First(x => V(x, "Template") == hid && Same(V(x, "Sheet"), V(was, "Sheet")))["Sheet"], null));
                    var inputSheets = new HashSet<string>(plans.Select(x => x.Key), StringComparer.OrdinalIgnoreCase);
                    foreach (var pl in plans)
                    {
                        string sheet = pl.Key;
                        LoadTable table = pl.Value;
                        Dictionary<string, string> kept = oldTemplates.FirstOrDefault(x => Same(V(x, "File"), V(h, "File")) && Same(V(x, "Sheet"), sheet) && V(x, "Template").Length > 0 && !found.Contains(V(x, "Template")));
                        // the workbook's name, with the sheet's when it has several calc tabs and both fit
                        string wanted = plans.Count == 1 ? hid : (hid.Length + sheet.Length + 1 <= 31 ? hid + " " + sheet : sheet);
                        string name;
                        if (kept != null) { name = V(kept, "Template"); found.Add(name); }
                        else
                        {
                            name = TabName(wanted, names);
                            found.Add(name);
                            if (old != null) notes.Add("new template: " + name + " (" + V(h, "File") + ", sheet " + sheet + "): its tab is added");
                        }
                        var t = Row("Template", name, "File", V(h, "File"), "Sheet", sheet, "Sha256", V(h, "Sha256"), "Loads", table == null ? "Envelope" : "All cases", "Basis", "", "Sign map", "IDENTITY");
                        if (table != null)
                        {
                            t["Load table at"] = table.At; t["Load table columns"] = string.Join(", ", table.Fields.ToArray());
                            t["Max rows"] = table.MaxRows > 0 ? I(table.MaxRows) : ""; t["Force unit"] = table.ForceUnit; t["Moment unit"] = table.MomentUnit;
                        }
                        if (kept != null)
                        {
                            foreach (string k in TemplateKept) if (kept.ContainsKey(k)) t[k] = V(kept, k);
                            foreach (var kv in kept) if (kv.Key != "__row" && !TemplateCols.Any(c => Same(c, kv.Key))) t[kv.Key] = kv.Value ?? "";     // a column of the engineer's own
                            if (V(kept, "Sha256").Length > 0 && !Same(V(kept, "Sha256"), V(h, "Sha256")))
                                notes.Add("template " + name + ": " + V(h, "File") + " has changed since the workbook was set up; its cells were read again");
                        }
                        templates.Add(t);
                        bool oneCalc = table != null;

                        // the inputs: the cells the engineer gave on the Templates tab, else what harvest found
                        IEnumerable<Dictionary<string, string>> mine = !oneCalc ? ins.Where(x => V(x, "Sheet") == sheet)
                            : ins.Where(x => V(x, "Sheet") == sheet).Concat(ins.Where(x => V(x, "Sheet") != sheet));
                        string why;
                        if (V(t, "Input cells").Length > 0)
                        {
                            List<Dictionary<string, string>> given = GivenInputs(Book(books, templatesDir, V(h, "File")), hid, sheet, V(t, "Input cells"), out why);
                            if (given == null) notes.Add("template " + name + ": its 'Input cells' are not used (" + why + "); the inputs found in the template are kept");
                            else mine = given;
                        }
                        var cols = new List<Dictionary<string, string>>();
                        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Connection" };
                        foreach (var i in mine)
                        {
                            string label = Regex.Replace(V(i, "GuardText"), @"\s+", " ").Trim();
                            string col = label.Length == 0 ? (V(i, "Kind") == "table" ? "Table " + V(i, "Cell") : V(i, "Cell")) : label;
                            if (V(i, "Sheet") != sheet) col = V(i, "Sheet") + ": " + col;      // another sheet of the workbook
                            if (V(i, "Name").Length > 0) col = V(i, "Name");                    // the name the engineer gave it
                            if (!used.Add(col)) { col = col + " [" + V(i, "Cell") + "]"; used.Add(col); }
                            var c = Row("Template", name, "Column", col, "Sheet", V(i, "Sheet"), "Cell", V(i, "Cell"), "Label cell", V(i, "GuardCell"), "Label", V(i, "GuardText"),
                                "Unit", V(i, "Unit"), "Kind", V(i, "Kind"), "List", V(i, "List"), "Template value", V(i, "Example"));
                            cols.Add(c); cells.Add(c);
                        }
                        schedules.Add(Schedule(name, V(h, "File"), sheet, cols, schedules.Count + 1, V(t, "Load table at").Length > 0, kept == null || old == null ? null : ScheduleCarry.Read(old.Sheet(name)), notes));

                        // the results: the rows the saved workbook holds, then the cells the engineer gave, else what
                        // harvest found: of this sheet, and of the workbook's sheets that take no input (solver flags);
                        // of the whole workbook when it is one calc
                        var before = oldOuts.Where(x => kept != null && Same(V(x, "Template"), name)).Select(Strip).ToList();
                        outputs.AddRange(before);
                        List<Dictionary<string, string>> results = null;
                        if (V(t, "Result cells").Length > 0)
                        {
                            results = GivenOutputs(Book(books, templatesDir, V(h, "File")), hid, sheet, V(t, "Result cells"), out why);
                            if (results == null) notes.Add("template " + name + ": its 'Result cells' are not used (" + why + "); the results found in the template are kept");
                        }
                        bool chosen = results != null;
                        foreach (var o in results ?? harvest.Outputs.Where(x => V(x, "Template") == hid && (oneCalc || V(x, "Sheet") == sheet || !inputSheets.Contains(V(x, "Sheet")))).ToList())
                        {
                            Dictionary<string, string> same = before.FirstOrDefault(x => Same(V(x, "Sheet"), V(o, "Sheet")) && Same(V(x, "Cell"), V(o, "Cell")));
                            if (same != null) { if (chosen && V(same, "Use").Length == 0) same["Use"] = "Y"; continue; }
                            string result = V(o, "Output").Length > 0 ? V(o, "Output") : V(o, "Cell");
                            bool governs = chosen || (V(o, "Kind") == "dcr" && Regex.IsMatch(result, "govern|overall|max", RegexOptions.IgnoreCase));
                            outputs.Add(Row("Template", name, "Use", governs ? "Y" : "", "Check", governs ? (chosen ? result : "DCR") : "", "Kind", V(o, "Kind"), "Expect", V(o, "Expect"), "Per row", "",
                                "Result", result, "Sheet", V(o, "Sheet"), "Cell", V(o, "Cell"), "Label cell", V(o, "GuardCell"), "Label", V(o, "GuardText")));
                        }
                    }
                }

            // the templates of the saved workbook that were not found again: kept as they were, tab and all
            foreach (var was in oldTemplates)
            {
                string name = V(was, "Template");
                if (name.Length == 0 || found.Contains(name)) continue;
                found.Add(name);
                templates.Add(Strip(was));
                var cols = oldCells.Where(x => Same(V(x, "Template"), name)).Select(Strip).ToList();
                cells.AddRange(cols);
                outputs.AddRange(oldOuts.Where(x => Same(V(x, "Template"), name)).Select(Strip));
                schedules.Add(Schedule(name, V(was, "File"), V(was, "Sheet"), cols, schedules.Count + 1, V(was, "Load table at").Length > 0, ScheduleCarry.Read(old.Sheet(name)), notes));
                if (harvest != null) notes.Add("template " + name + ": " + V(was, "File") + ", sheet " + V(was, "Sheet") + ", was not found in the templates folder; its tab is kept as it was");
            }
            // a result row that names no template of the workbook is the engineer's: kept
            outputs.AddRange(oldOuts.Where(x => !found.Contains(V(x, "Template"))).Select(Strip));

            // ---- Model Config
            var beams = new List<Dictionary<string, string>>();
            var oldBeam = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var b in oldModel) if (V(b, "Beam ID").Length > 0 && !oldBeam.ContainsKey(V(b, "Beam ID"))) oldBeam[V(b, "Beam ID")] = b;
            if (pkg != null)
            {
                var added = new List<string>();
                foreach (PkgMember m in pkg.Members.Values.OrderBy(x => x.Id))
                {
                    PkgEnd a = pkg.End(m.NodeA, m.Id, "A"), b = pkg.End(m.NodeB, m.Id, "B");
                    int fa = a == null ? 0 : Register.GuessSupport(pkg, a), fb = b == null ? 0 : Register.GuessSupport(pkg, b);
                    var row = Row("Beam ID", I(m.Id), "Node I", I(m.NodeA), "Node J", I(m.NodeB), "Section", m.Section, "Shape", m.Family,
                        "Frames into I", fa == 0 ? "" : I(fa), "Frames into J", fb == 0 ? "" : I(fb));
                    Dictionary<string, string> was;
                    if (oldBeam.TryGetValue(I(m.Id), out was))
                    {
                        foreach (var kv in was) if (kv.Key != "__row" && !ModelFixed.Any(c => Same(c, kv.Key))) row[kv.Key] = (kv.Value ?? "").Trim();
                    }
                    else if (old != null) added.Add(I(m.Id));
                    beams.Add(row);
                }
                if (added.Count > 0) notes.Add(added.Count + " beam(s) of the model are new on Model Config: " + string.Join(", ", added.Take(15).ToArray()) + (added.Count > 15 ? " ..." : ""));
                foreach (var b in oldModel)
                {
                    int id;
                    if (!int.TryParse(V(b, "Beam ID"), NumberStyles.Integer, Inv, out id) || pkg.Members.ContainsKey(id)) continue;
                    string had = string.Join(", ", new[] { "I", "J" }.Where(e => V(b, "Connection " + e).Length > 0).Select(e => V(b, "Connection " + e) + " at end " + e).ToArray());
                    notes.Add("beam " + id + " is no longer in the model: its row is dropped" + (had.Length > 0 ? " (it had " + had + ")" : ""));
                }
            }
            else beams.AddRange(oldModel.Select(Strip));
            XOutSheet mc = WorkbookTables.Sheet(ModelConfig, "tblModelConfig", ModelCols, beams,
                "One row per beam of the model. Choose the connection at each end (a mark from Connection Lib; blank = no calc there). Frames into: the member that end connects to (a first guess; correct it).",
                c => c == "Beam ID" ? ColumnKind.Key : (c == "Node I" || c == "Node J" || c == "Section" || c == "Shape") ? ColumnKind.Reference : ColumnKind.Input);
            foreach (string c in new[] { "Beam ID", "Node I", "Node J", "Frames into I", "Frames into J" }) WorkbookTables.Numbers(mc, Array.IndexOf(ModelCols, c), "0");
            int last = 2 + Math.Max(beams.Count, 1) + 2000;
            Validate(mc, "F3:G" + last, "'" + ConnectionLib + "'!$A$3:$A$2000", true);
            mc.FreezeCols = 1;
            sheets.Add(mc);

            // ---- Connection Lib
            XOutSheet cl = WorkbookTables.Sheet(ConnectionLib, "tblConnectionLib", LibCols, oldLib.Select(Strip).ToList(),
                "One row per connection mark: a name of your own, and the template it uses. A mark is one template plus one row of values on that template's tab; two marks may share a template. Add rows below.",
                c => c == "Connection" ? ColumnKind.Key : ColumnKind.Input);
            Validate(cl, "B3:B2000", "'" + TemplatesTab + "'!$A$3:$A$500", true);
            cl.Width(0, 22); cl.Width(1, 34); cl.Width(2, 60);
            sheets.Add(cl);

            // ---- Templates
            XOutSheet tt = WorkbookTables.Sheet(TemplatesTab, "tblConfigTemplates", TemplateCols, templates,
                "One row per calc template (a tab of a template workbook), each with a schedule tab of the same name. Loads: Envelope (the largest and smallest of each force with what acts with it) or All cases. Basis: LRFD or ASD when the template takes only one. Sign map: a row of Sign Maps (IDENTITY = the member end's forces as they are). Load table at: the first cell of the template's table of load rows (blank = none); its columns in order (Beam, Node, LC, LC title, Fx to Mz, Section, Connection, Mark, or - for a column to leave alone); how many rows it holds; the units it takes. Input cells / Result cells: only when the columns found for the template are not the ones you want: list its input cells and result cells yourself (B4, B6:B9, Sheet2!C3; 'table B5:C12' for a block typed as one table; 'Anchors = table B5:C12' to name the column), then run Refresh config.",
                c => c == "Template" ? ColumnKind.Key : (c == "File" || c == "Sheet" || c == "Sha256") ? ColumnKind.Reference : ColumnKind.Input);
            tt.Width(Array.IndexOf(TemplateCols, "Load table columns"), 48);
            tt.Width(Array.IndexOf(TemplateCols, "Input cells"), 40); tt.Width(Array.IndexOf(TemplateCols, "Result cells"), 30);
            WorkbookTables.Numbers(tt, Array.IndexOf(TemplateCols, "Max rows"), "0");
            int tn = 2 + Math.Max(templates.Count, 1);
            Validate(tt, "E3:E" + tn, "\"Envelope,All cases\"", true);
            Validate(tt, "F3:F" + tn, "\"LRFD,ASD\"", true);
            sheets.Add(tt);

            sheets.AddRange(schedules);

            // ---- Template Cells, Outputs, Sign Maps, Load Cases
            sheets.Add(WorkbookTables.Sheet(CellsTab, "tblTemplateCells", CellCols, cells,
                "Where each schedule column goes in its template, as harvest found it or as the Templates tab gives it. For reference: the tool reads this tab, so leave it as it is.",
                c => (c == "Template" || c == "Column") ? ColumnKind.Key : ColumnKind.Reference));
            XOutSheet os = WorkbookTables.Sheet(OutputsTab, "tblConfigOutputs", OutputCols, outputs,
                "What is read back from each template. Use = Y on the results you want. Check: the summary column it goes in (several results may share one: the largest counts). Kind: dcr (a ratio, judged against the DCR limit), status (OK / NG), health (a flag that must equal Expect), value (reported as it is). Per row = Y for a result beside a load table (one per row; give its first cell). A result harvest did not find can be added as a row below.",
                c => c == "Template" ? ColumnKind.Key : (c == "Use" || c == "Check" || c == "Kind" || c == "Expect" || c == "Per row") ? ColumnKind.Input : ColumnKind.Reference);
            int on = 2 + Math.Max(outputs.Count, 1);
            Validate(os, "B3:B" + (on + 200), "\"Y,N\"", true);
            Validate(os, "D3:D" + (on + 200), "\"" + string.Join(",", LibrarySchema.OutputKinds) + "\"", true);
            Validate(os, "F3:F" + (on + 200), "\"Y,N\"", true);
            sheets.Add(os);
            sheets.Add(WorkbookTables.Sheet(SignMaps, "tblSignMaps", LibrarySchema.FrameColumns, oldMaps.Select(Strip).ToList(),
                "Only when a template's axes differ from the member end's. A member end's forces are in its own axes: x along the member away from the joint, y the member's local y, z = x cross y, STAAD's sign. A row says which of them each template force is, e.g. Fx = +Fz. Action on_connection reverses all six. Confirmed = Y once checked by hand.",
                c => c == "Frame" ? ColumnKind.Key : ColumnKind.Input));
            var cases = new List<Dictionary<string, string>>();
            if (pkg != null)
            {
                bool combos = pkg.Cases.Values.Any(c => c.HasForces && (c.Kind == "COMBINATION" || c.Kind == "REPEAT"));
                var newCases = new List<string>();
                foreach (PkgCase c in pkg.Cases.Values)
                {
                    var row = Row("LC", I(c.Id), "Title", c.Title, "Kind", c.Kind + (c.Method.Length > 0 ? " " + c.Method : ""),
                        "Use", c.HasForces && (!combos || c.Kind == "COMBINATION" || c.Kind == "REPEAT") ? "Y" : "N", "Basis", "");
                    Dictionary<string, string> was = oldCases.FirstOrDefault(x => V(x, "LC") == I(c.Id));
                    if (was != null) { foreach (var kv in was) if (kv.Key != "__row" && !CaseFixed.Any(k => Same(k, kv.Key))) row[kv.Key] = (kv.Value ?? "").Trim(); }
                    else if (old != null && old.Sheet(LoadCases) != null) newCases.Add(I(c.Id));
                    cases.Add(row);
                }
                if (newCases.Count > 0) notes.Add(newCases.Count + " load case(s) of the model are new on Load Cases (look at their Use): " + string.Join(", ", newCases.Take(20).ToArray()) + (newCases.Count > 20 ? " ..." : ""));
            }
            else cases.AddRange(oldCases.Select(Strip));
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

        // a tab of the saved workbook as rows by heading (none when there is no saved workbook, or no such tab)
        static List<Dictionary<string, string>> OldTab(XWorkbook old, string tab)
        {
            return old == null || old.Sheet(tab) == null ? new List<Dictionary<string, string>>() : Tab(old, tab, new ExpandResult());
        }

        // a template file, opened once (null when it is not there or cannot be read)
        static TemplateBook Book(Dictionary<string, TemplateBook> books, string templatesDir, string file)
        {
            TemplateBook b;
            if (books.TryGetValue(file, out b)) return b;
            b = null;
            if (!string.IsNullOrEmpty(templatesDir) && file.Length > 0)
            {
                string path = Path.Combine(templatesDir, file);
                if (File.Exists(path)) { try { b = TemplateBook.Open(path); } catch (Exception) { b = null; } }
            }
            books[file] = b;
            return b;
        }

        // "B4, B6:B9, Anchors!C3, Anchors = table Anchors!B5:C12": the cells an engineer gives for a template. Each
        // entry: the sheet, the reference (a cell; a range kept whole when it is a table), "table" or "", and the name
        // given before an = sign ("" when none: the cell's label names it). A range that is not a table is its cells,
        // row by row. null with a reason when an entry is not a cell or a range.
        public static List<string[]> ParseCells(string text, string defaultSheet, out string why)
        {
            why = "";
            var list = new List<string[]>();
            foreach (string raw in (text ?? "").Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string item = raw.Trim(), given = "";
                if (item.Length == 0) continue;
                int eq = item.IndexOf('=');
                if (eq > 0) { given = item.Substring(0, eq).Trim(); item = item.Substring(eq + 1).Trim(); }
                bool table = Regex.IsMatch(item, @"\btable\b", RegexOptions.IgnoreCase);
                if (table) item = Regex.Replace(item, @"\(?\btable\b\)?", "", RegexOptions.IgnoreCase).Trim();
                Match m = Regex.Match(item, @"^(?:(?:'((?:[^']|'')+)'|([^'!]+))!)?\$?([A-Za-z]{1,3})\$?(\d+)(?::\$?([A-Za-z]{1,3})\$?(\d+))?$");
                int c0, r0, c1, r1;
                if (!m.Success || !Xlsx.ParseRef(m.Groups[3].Value + m.Groups[4].Value, out c0, out r0) || r0 < 0)
                { why = "'" + raw.Trim() + "' is not a cell or a range (write B4, B6:B9 or Sheet2!C3)"; return null; }
                string sheet = m.Groups[1].Success ? m.Groups[1].Value.Replace("''", "'") : (m.Groups[2].Success ? m.Groups[2].Value.Trim() : defaultSheet);
                c1 = c0; r1 = r0;
                if (m.Groups[5].Success && (!Xlsx.ParseRef(m.Groups[5].Value + m.Groups[6].Value, out c1, out r1) || r1 < r0 || c1 < c0))
                { why = "'" + raw.Trim() + "' is not a range from its top left cell to its bottom right"; return null; }
                if (table && (r1 > r0 || c1 > c0)) { list.Add(new[] { sheet, Xlsx.Ref(r0, c0) + ":" + Xlsx.Ref(r1, c1), "table", given }); continue; }
                if ((long)(r1 - r0 + 1) * (c1 - c0 + 1) > 500) { why = "'" + raw.Trim() + "' is more than 500 cells: a block of that size is a table (write 'table " + item + "')"; return null; }
                for (int r = r0; r <= r1; r++)
                    for (int c = c0; c <= c1; c++) list.Add(new[] { sheet, Xlsx.Ref(r, c), "", r1 > r0 || c1 > c0 ? "" : given });
            }
            if (list.Count == 0) { why = "no cell is given"; return null; }
            return list;
        }

        // the inputs an engineer gave for a template, as harvest would list them; null with a reason
        static List<Dictionary<string, string>> GivenInputs(TemplateBook b, string id, string sheet, string text, out string why)
        {
            if (b == null) { why = "the template file could not be read"; return null; }
            List<string[]> items = ParseCells(text, sheet, out why);
            if (items == null) return null;
            var list = new List<Dictionary<string, string>>();
            foreach (string[] it in items)
            {
                TSheet s = b.Sheet(it[0]);
                if (s == null) { why = "the template has no sheet '" + it[0] + "'"; return null; }
                int c0, r0, c1, r1;
                string[] ends = it[1].Split(':');
                Xlsx.ParseRef(ends[0], out c0, out r0);
                Xlsx.ParseRef(ends[ends.Length - 1], out c1, out r1);
                for (int r = r0; r <= r1; r++)
                    for (int c = c0; c <= c1; c++)
                    {
                        TCell x = s.Get(r, c);
                        if (x != null && x.IsFormula) { why = s.Name + "!" + Xlsx.Ref(r, c) + " holds a formula: an input is a cell that is typed"; return null; }
                    }
                Dictionary<string, string> d = Harvest.InputAt(b, id, s, r0, c0);      // a table: the label and the unit of its first cell
                d["Name"] = it[3];
                if (it[2] != "table") { list.Add(d); continue; }
                d["Cell"] = it[1]; d["Kind"] = "table"; d["List"] = "";
                var lines = new List<string>();
                for (int r = r0; r <= r1; r++)
                {
                    var vals = new List<string>();
                    for (int c = c0; c <= c1; c++) vals.Add(TemplateBook.TextOf(s, r, c).Trim());
                    if (vals.All(v => v.Length == 0)) break;
                    lines.Add(string.Join(", ", vals.ToArray()));
                }
                d["Example"] = string.Join("; ", lines.ToArray());
                list.Add(d);
            }
            return list;
        }

        // the results an engineer gave for a template; null with a reason
        static List<Dictionary<string, string>> GivenOutputs(TemplateBook b, string id, string sheet, string text, out string why)
        {
            if (b == null) { why = "the template file could not be read"; return null; }
            List<string[]> items = ParseCells(text, sheet, out why);
            if (items == null) return null;
            var list = new List<Dictionary<string, string>>();
            foreach (string[] it in items)
            {
                TSheet s = b.Sheet(it[0]);
                if (s == null) { why = "the template has no sheet '" + it[0] + "'"; return null; }
                if (it[2] == "table") { why = "a result is one cell (" + it[1] + " is a block)"; return null; }
                int c0, r0;
                Xlsx.ParseRef(it[1], out c0, out r0);
                Dictionary<string, string> o = Harvest.OutputAt(b, id, s, r0, c0);
                if (it[3].Length > 0) o["Output"] = it[3];
                list.Add(o);
            }
            return list;
        }

        // a saved schedule tab: its headings with their Source and Unit, and the rows typed on it
        sealed class ScheduleCarry
        {
            public readonly List<string> Heads = new List<string>();
            public readonly Dictionary<string, string> Source = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, string> Unit = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public readonly List<Dictionary<string, string>> Rows = new List<Dictionary<string, string>>();

            public static ScheduleCarry Read(XSheet s)
            {
                if (s == null) return null;
                var c = new ScheduleCarry();
                var at = new List<int>();
                for (int k = 1; s.Text(HeaderRow, k).Length > 0; k++)
                {
                    string h = s.Text(HeaderRow, k);
                    if (c.Source.ContainsKey(h)) continue;
                    c.Heads.Add(h); at.Add(k);
                    c.Source[h] = s.Text(SourceRow, k); c.Unit[h] = s.Text(UnitRow, k);
                }
                int blank = 0;
                for (int r = FirstMarkRow; r < s.Rows.Count && blank < 50; r++)
                {
                    var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    d["Connection"] = s.Text(r, 0);
                    bool any = d["Connection"].Length > 0;
                    for (int k = 0; k < at.Count; k++) { string v = s.Text(r, at[k]); d[c.Heads[k]] = v; if (v.Length > 0) any = true; }
                    if (!any) { blank++; continue; }
                    blank = 0;
                    c.Rows.Add(d);
                }
                return c;
            }
        }

        // a typed value as a cell holds it: a number when it reads as one
        static object CellValue(string v)
        {
            double d;
            if (Regex.IsMatch(v ?? "", @"^-?(\d+\.?\d*|\.\d+)([eE][+\-]?\d+)?$") && double.TryParse(v, NumberStyles.Float, Inv, out d)) return d;
            return v ?? "";
        }

        // one template's schedule: Source / Unit / template value rows, the header, then a row per mark. carry: the
        // tab as it was saved (a refresh): its rows, and the Source and Unit of the headings it shares with cols
        static XOutSheet Schedule(string name, string file, string sheet, List<Dictionary<string, string>> cols, int index, bool table, ScheduleCarry carry, List<string> notes)
        {
            cols = new List<Dictionary<string, string>>(cols);
            if (carry != null)
            {
                var have = new HashSet<string>(cols.Select(c => V(c, "Column")), StringComparer.OrdinalIgnoreCase);
                foreach (string h in carry.Heads)
                    if (!have.Contains(h) && carry.Rows.Any(r => V(r, h).Length > 0))
                    {
                        cols.Add(Row("Column", h));
                        notes.Add("tab " + name + ": column '" + h + "' is no longer an input of the template (renamed or moved in the template?). It is kept at the end with its values: move them to the right column, then delete it");
                    }
            }
            var s = new XOutSheet(name);
            var label = new XStyle(); label.Bold = true; label.Color = Palette.GreyText;
            var grey = Palette.Data(ColumnKind.Reference);
            var input = Palette.Data(ColumnKind.Input);
            s.Add(new XCell("One row per connection mark that uses " + file + ", sheet " + sheet + ". Every input of the template is a column: fill the ones typed here, leave a cell blank to keep the template's own value (" + Blank + " empties the cell). Source says where a column's value comes from; a force needs its Unit. A table column holds a block of the template in one cell: its rows separated by ; and the values of a row by , (1.5, 1.5; 1.5, 10.5)." +
                (table ? " This template takes a table of load rows (Templates tab): a mark is ONE copy of it, and its table lists the beam ends that call the mark." : ""), Palette.Note()));
            var src = new List<object> { new XCell("Source", label) };
            var unit = new List<object> { new XCell("Unit", label) };
            var val = new List<object> { new XCell("Template value", label) };
            var head = new List<object> { new XCell("Connection", Palette.Header(ColumnKind.Key)) };
            foreach (var c in cols)
            {
                string h = V(c, "Column");
                string so = carry != null && carry.Source.ContainsKey(h) ? carry.Source[h] : "", un = carry != null && carry.Unit.ContainsKey(h) ? carry.Unit[h] : "";
                src.Add(new XCell(so.Length > 0 ? so : GuessSource(V(c, "Label"), V(c, "Cell"), V(c, "Unit")), input));
                unit.Add(new XCell(un.Length > 0 ? un : V(c, "Unit"), input));
                val.Add(new XCell(V(c, "Template value"), grey));
                head.Add(new XCell(h, Palette.Header(ColumnKind.Input)));
            }
            s.Add(src.ToArray()); s.Add(unit.ToArray()); s.Add(val.ToArray()); s.Add(head.ToArray());
            int rows = 0;
            if (carry != null)
                foreach (var r in carry.Rows)
                {
                    var cells = new object[cols.Count + 1];
                    cells[0] = new XCell(V(r, "Connection"), Palette.Data(ColumnKind.Key));
                    for (int i = 0; i < cols.Count; i++) cells[i + 1] = new XCell(CellValue(V(r, V(cols[i], "Column"))), input);
                    s.Add(cells);
                    rows++;
                }
            if (rows == 0)
            {
                var first = new object[cols.Count + 1];
                first[0] = new XCell("", Palette.Data(ColumnKind.Key));
                for (int i = 1; i < first.Length; i++) first[i] = new XCell("", input);
                s.Add(first);
                rows = 1;
            }
            s.RowHeights[HeaderRow] = 30;
            var t = new XOutTable();
            t.Name = "tblSchedule" + index.ToString(Inv); t.Top = HeaderRow; t.Left = 0; t.Right = cols.Count; t.Bottom = HeaderRow + rows; t.Style = Palette.TableStyle;
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

        // ---- the live check of the force units, for the Start tab: one row per template tab. Formula counts the force
        // columns of the tab whose Unit is blank, and the missing units of its load table on the Templates tab
        public sealed class UnitCheck
        {
            public string Tab = "", Formula = "";
            public int Missing;
            public bool Used;       // a mark on Connection Lib uses the template
        }

        public static List<UnitCheck> UnitChecks(List<XOutSheet> sheets)
        {
            var list = new List<UnitCheck>();
            XOutSheet tt = sheets.FirstOrDefault(s => s.Name == TemplatesTab);
            if (tt == null) return list;
            int at = Array.IndexOf(TemplateCols, "Load table at"), fields = Array.IndexOf(TemplateCols, "Load table columns"),
                fu = Array.IndexOf(TemplateCols, "Force unit"), mu = Array.IndexOf(TemplateCols, "Moment unit");
            for (int r = 2; r < tt.Rows.Count; r++)
            {
                string name = Shown(tt.Rows[r], 0);
                XOutSheet sch = name.Length == 0 ? null : sheets.FirstOrDefault(s => s.Name == name);
                if (sch == null) continue;
                var u = new UnitCheck();
                u.Tab = name;
                XOutSheet lib = sheets.FirstOrDefault(s => s.Name == ConnectionLib);
                for (int k = 2; lib != null && k < lib.Rows.Count && !u.Used; k++) u.Used = Same(Shown(lib.Rows[k], 1), name) && Shown(lib.Rows[k], 0).Length > 0;
                string q = "'" + name.Replace("'", "''") + "'!", t = "'" + TemplatesTab + "'!", row = (r + 1).ToString(Inv);
                string forces = "{\"Fx\",\"Fy\",\"Fz\",\"Mx\",\"My\",\"Mz\"}";
                u.Formula = "SUMPRODUCT(ISNUMBER(MATCH(" + q + "$B$" + (SourceRow + 1) + ":$XFD$" + (SourceRow + 1) + "," + forces + ",0))*(" + q + "$B$" + (UnitRow + 1) + ":$XFD$" + (UnitRow + 1) + "=\"\"))" +
                    "+IF(" + t + "$" + Xlsx.ColName(at) + "$" + row + "=\"\",0," +
                    "IF(AND(" + t + "$" + Xlsx.ColName(fu) + "$" + row + "=\"\",SUMPRODUCT(--ISNUMBER(SEARCH({\"Fx\",\"Fy\",\"Fz\"}," + t + "$" + Xlsx.ColName(fields) + "$" + row + ")))>0),1,0)+" +
                    "IF(AND(" + t + "$" + Xlsx.ColName(mu) + "$" + row + "=\"\",SUMPRODUCT(--ISNUMBER(SEARCH({\"Mx\",\"My\",\"Mz\"}," + t + "$" + Xlsx.ColName(fields) + "$" + row + ")))>0),1,0))";
                // the same count, from what is being written (what the cell shows before Excel calculates it)
                object[] src = sch.Rows[SourceRow], un = sch.Rows[UnitRow];
                for (int c = 1; c < src.Length; c++)
                    if (Regex.IsMatch(Shown(src, c), "^[FM][xyz]$") && Shown(un, c).Length == 0) u.Missing++;
                if (Shown(tt.Rows[r], at).Length > 0)
                {
                    if (Shown(tt.Rows[r], fu).Length == 0 && Regex.IsMatch(Shown(tt.Rows[r], fields), "F[xyz]", RegexOptions.IgnoreCase)) u.Missing++;
                    if (Shown(tt.Rows[r], mu).Length == 0 && Regex.IsMatch(Shown(tt.Rows[r], fields), "M[xyz]", RegexOptions.IgnoreCase)) u.Missing++;
                }
                list.Add(u);
            }
            return list;
        }

        // the text of a cell being written
        static string Shown(object[] row, int col)
        {
            if (row == null || col < 0 || col >= row.Length || row[col] == null) return "";
            XCell x = row[col] as XCell;
            object v = x != null ? x.Value : row[col];
            return v == null ? "" : Convert.ToString(v, Inv).Trim();
        }

        // ================================================================= what changed since the workbook was set up

        // Said before it bites: template files the Templates tab does not list, lists with another content or no longer
        // finds; beams and load cases of the model the tabs lack; cells given on the Templates tab that the tabs do not
        // hold yet. Each is put right by a refresh. files: the template workbooks of the templates folder.
        public static List<string> Drift(string workbook, AnalysisPackage pkg, string templatesDir, IEnumerable<string> files)
        {
            var notes = new List<string>();
            XWorkbook wb = Xlsx.Read(workbook, 64);
            if (wb.Sheet(TemplatesTab) == null) return notes;
            List<Dictionary<string, string>> templates = OldTab(wb, TemplatesTab), cellRows = OldTab(wb, CellsTab), outs = OldTab(wb, OutputsTab);
            if (files != null && !string.IsNullOrEmpty(templatesDir))
            {
                var there = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string f in files)
                {
                    if (!Harvest.Readable.Contains(Path.GetExtension(f).ToLowerInvariant())) continue;
                    string rel = Harvest.Relative(f, templatesDir);
                    there.Add(rel);
                    var rows = templates.Where(t => Same(V(t, "File"), rel)).ToList();
                    if (rows.Count == 0) { notes.Add("the templates folder has a template the workbook does not know: " + rel); continue; }
                    string sha = null;
                    try { sha = SharedFile.Sha256(f); } catch (Exception) { sha = null; }
                    if (sha != null && rows.Any(t => V(t, "Sha256").Length > 0 && !Same(V(t, "Sha256"), sha)))
                        notes.Add("template " + rel + " has changed since the workbook was set up (its cells may have moved)");
                }
                foreach (string rel in templates.Select(t => V(t, "File")).Where(f => f.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
                    if (!there.Contains(rel)) notes.Add("template " + rel + " is on the Templates tab but not in the templates folder");
            }
            if (pkg != null)
            {
                var have = new HashSet<string>(OldTab(wb, ModelConfig).Select(b => V(b, "Beam ID")));
                var beams = pkg.Members.Keys.OrderBy(k => k).Select(I).Where(k => !have.Contains(k)).ToList();
                if (beams.Count > 0) notes.Add(beams.Count + " beam(s) of the model have no row on " + ModelConfig + ": " + string.Join(", ", beams.Take(15).ToArray()) + (beams.Count > 15 ? " ..." : ""));
                var known = new HashSet<string>(OldTab(wb, LoadCases).Select(c => V(c, "LC")));
                var cases = pkg.Cases.Values.Where(c => c.HasForces).Select(c => I(c.Id)).Where(k => !known.Contains(k)).ToList();
                if (cases.Count > 0) notes.Add(cases.Count + " load case(s) of the model are not on " + LoadCases + ": " + string.Join(", ", cases.Take(20).ToArray()) + (cases.Count > 20 ? " ..." : ""));
            }
            foreach (var t in templates)
            {
                string name = V(t, "Template"), why;
                if (name.Length == 0) continue;
                if (V(t, "Input cells").Length > 0)
                {
                    List<string[]> given = ParseCells(V(t, "Input cells"), V(t, "Sheet"), out why);
                    var held = new HashSet<string>(cellRows.Where(c => Same(V(c, "Template"), name)).Select(c => V(c, "Sheet") + "!" + V(c, "Cell")), StringComparer.OrdinalIgnoreCase);
                    if (given == null) notes.Add("template " + name + ": 'Input cells': " + why);
                    else if (given.Count != held.Count || given.Any(g => !held.Contains(g[0] + "!" + g[1]))) notes.Add("template " + name + ": its 'Input cells' are not the columns of its tab yet");
                }
                if (V(t, "Result cells").Length > 0)
                {
                    List<string[]> given = ParseCells(V(t, "Result cells"), V(t, "Sheet"), out why);
                    var held = new HashSet<string>(outs.Where(c => Same(V(c, "Template"), name)).Select(c => V(c, "Sheet") + "!" + V(c, "Cell")), StringComparer.OrdinalIgnoreCase);
                    if (given == null) notes.Add("template " + name + ": 'Result cells': " + why);
                    else if (given.Any(g => !held.Contains(g[0] + "!" + g[1]))) notes.Add("template " + name + ": its 'Result cells' are not on the " + OutputsTab + " tab yet");
                }
            }
            return notes;
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
                var blocks = new List<KeyValuePair<int, string[]>>();    // a table column: column index -> sheet, range, unit, heading
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
                    if (unit.Length > 0) { Unit u0; if (!Units.TryParse(unit, out u0)) { res.Error(name, "column '" + col + "': '" + unit + "' is not a unit CalcFlow knows"); continue; } }
                    if (V(cell, "Cell").IndexOf(':') >= 0)
                    {
                        // a block of the template typed as one table: an input per cell, each mark's table spread over them
                        if (!Same(source, Typed)) { res.Error(name, "column '" + col + "' is a table (" + V(cell, "Cell") + "): its Source is '" + Typed + "'"); continue; }
                        int bc0, br0, bc1, br1;
                        string[] ends = V(cell, "Cell").Split(':');
                        if (ends.Length != 2 || !Xlsx.ParseRef(ends[0], out bc0, out br0) || !Xlsx.ParseRef(ends[1], out bc1, out br1) || br0 < 0 || br1 < br0 || bc1 < bc0)
                        { res.Error(CellsTab, "template " + name + ", column '" + col + "': '" + V(cell, "Cell") + "' is not a range"); continue; }
                        for (int br = br0; br <= br1; br++)
                            for (int bc = bc0; bc <= bc1; bc++)
                            {
                                var bi = Row("Template", name, "Sheet", V(cell, "Sheet"), "Cell", Xlsx.Ref(br, bc), "Unit", unit, "Binding", "param:" + ParamName(V(t, "Sheet"), V(cell, "Sheet"), Xlsx.Ref(br, bc)), "PerCase", "N", "Required", "N");
                                if (br == br0 && bc == bc0) { bi["GuardCell"] = V(cell, "Label cell"); bi["GuardText"] = V(cell, "Label"); }
                                rows["tblInputs"].Add(bi);
                            }
                        blocks.Add(new KeyValuePair<int, string[]>(c, new[] { V(cell, "Sheet"), V(cell, "Cell"), unit, col }));
                        continue;
                    }
                    var input = Row("Template", name, "Sheet", V(cell, "Sheet"), "Cell", V(cell, "Cell"), "GuardCell", V(cell, "Label cell"), "GuardText", V(cell, "Label"), "Unit", unit);
                    if (Same(source, Typed))
                    {
                        string param = ParamName(V(t, "Sheet"), V(cell, "Sheet"), V(cell, "Cell"));
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
                    foreach (var bk in blocks) TableParams(s.Text(r, bk.Key), bk.Value, V(t, "Sheet"), mark, name, r + 1, rows["tblParams"], res);
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

        // the name of a typed cell's parameter: c_B4, and c_Sheet2_B4 on another sheet of the workbook
        static string ParamName(string templateSheet, string sheet, string cell)
        {
            return "c_" + (sheet.Length == 0 || Same(sheet, templateSheet) ? "" : Regex.Replace(sheet, @"[^A-Za-z0-9]+", "_") + "_") + cell;
        }

        // a table typed in one cell ("1.5, 1.5; 1.5, 10.5": rows separated by ; or a line break, values by ,) -> a value
        // for every cell of its block; the cells it does not reach are emptied. block: sheet, range, unit, heading
        static void TableParams(string text, string[] block, string templateSheet, string mark, string tab, int xlRow, List<Dictionary<string, string>> pars, ExpandResult res)
        {
            if (text.Trim().Length == 0) return;      // nothing typed: the template's own block stays
            int c0, r0, c1, r1;
            string[] ends = block[1].Split(':');
            Xlsx.ParseRef(ends[0], out c0, out r0);
            Xlsx.ParseRef(ends[1], out c1, out r1);
            int wide = c1 - c0 + 1, high = r1 - r0 + 1;
            var lines = new List<string[]>();
            if (!Same(text, Blank))
                foreach (string line in text.Split(new[] { ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line.Trim().Length == 0) continue;
                    string[] vals = line.Split(',', '\t').Select(x => x.Trim()).ToArray();
                    if (vals.Length != wide)
                    { res.Error(tab, "row " + xlRow + ", column '" + block[3] + "': a row of its table holds " + vals.Length + " value(s) (" + line.Trim() + "); the template's block " + block[1] + " is " + wide + " wide"); return; }
                    lines.Add(vals);
                }
            if (lines.Count > high)
            { res.Error(tab, "row " + xlRow + ", column '" + block[3] + "': its table has " + lines.Count + " rows; the template's block " + block[1] + " holds " + high); return; }
            for (int r = 0; r < high; r++)
                for (int c = 0; c < wide; c++)
                {
                    string v = r < lines.Count && lines[r][c].Length > 0 ? lines[r][c] : Blank;
                    pars.Add(Row("Type", mark, "Step", "", "Param", ParamName(templateSheet, block[0], Xlsx.Ref(r0 + r, c0 + c)), "Default", v, "Unit", v == Blank ? "" : block[2], "Notes", block[3]));
                }
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
