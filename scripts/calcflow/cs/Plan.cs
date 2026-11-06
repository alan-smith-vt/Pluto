// Plan.cs -- the dry run: every cell of every calc resolved, every gap named, before Excel opens.
//   calcs    : one per register row with a Type (Disposition Excluded skips it), or one per Group (its connections
//              share one calc on the union of their demands; their other inputs must agree)
//   per step : the template (its file found, its SHA-256 as pinned, every guard label where the library says, every
//              input a constant, every output a formula), the once-only inputs (bindings resolved, units converted),
//              the demand (the slot's printed end forces for the cases in scope, through the step's frame, reduced by the
//              demand mode) and the per-case inputs of every demand row
//   written  : plan.csv (once-only cells with their source), cases.csv (per-case cells), demands.csv (the demand rows
//              in the template's axes, kip / kip-in), calcs.csv, problems.csv
// The Excel stage (next phase) takes the plan as it is. C# 5 (Windows PowerShell 5.1 Add-Type, warnings are errors).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace CalcFlow
{
    public sealed class PlanProblem
    {
        public string Severity = "", Calc = "", Code = "", Message = "";
        public override string ToString() { return Severity + " " + (Calc.Length > 0 ? Calc + " " : "") + Code + ": " + Message; }
    }

    public sealed class PlanCalc
    {
        public string Id = "", Type = "", Group = "", Status = "Ready";
        public readonly List<RegConnection> Connections = new List<RegConnection>();
        public int Cells, Cases, Problems;
    }

    public sealed class PlanCell
    {
        public string Calc = "", Type = "", Template = "", Sheet = "", Cell = "", Input = "", Binding = "", Value = "", Unit = "", Source = "";
        public int Step;
    }

    public sealed class PlanCase
    {
        public string Calc = "", Template = "", Connection = "", Sheet = "", Cell = "", Binding = "", Value = "", Unit = "";
        public int Step, Row, LC;
    }

    public sealed class PlanDemand
    {
        public string Calc = "", Template = "", Mode = "", Connection = "", Note = "";
        public int Step, Row, LC;
        public double[] F = new double[6];     // the template's axes; kip, kip-in
    }

    public sealed class PlanResult
    {
        public readonly List<PlanCalc> Calcs = new List<PlanCalc>();
        public readonly List<PlanCell> Cells = new List<PlanCell>();
        public readonly List<PlanCase> CaseCells = new List<PlanCase>();
        public readonly List<PlanDemand> Demands = new List<PlanDemand>();
        public readonly List<PlanProblem> Problems = new List<PlanProblem>();
        public int NewConnections;

        public int ErrorCount { get { return Problems.Count(p => p.Severity == "Error"); } }
        public int WarningCount { get { return Problems.Count(p => p.Severity == "Warning"); } }

        public string[] Write(string dir)
        {
            Directory.CreateDirectory(dir);
            var files = new List<string>();
            string p;
            p = Path.Combine(dir, "plan.csv"); files.Add(p);
            Csv.Write(p, new[] { "Calc", "Type", "Step", "Template", "Sheet", "Cell", "Input", "Binding", "Value", "Unit", "Source" },
                Cells.Select(c => new object[] { c.Calc, c.Type, c.Step, c.Template, c.Sheet, c.Cell, c.Input, c.Binding, c.Value, c.Unit, c.Source }));
            p = Path.Combine(dir, "cases.csv"); files.Add(p);
            Csv.Write(p, new[] { "Calc", "Step", "Template", "Row", "LC", "Connection", "Sheet", "Cell", "Binding", "Value", "Unit" },
                CaseCells.Select(c => new object[] { c.Calc, c.Step, c.Template, c.Row, c.LC == 0 ? "ENV" : c.LC.ToString(CultureInfo.InvariantCulture), c.Connection, c.Sheet, c.Cell, c.Binding, c.Value, c.Unit }));
            p = Path.Combine(dir, "demands.csv"); files.Add(p);
            Csv.Write(p, new[] { "Calc", "Step", "Template", "Mode", "Row", "LC", "Connection", "Fx (kip)", "Fy (kip)", "Fz (kip)", "Mx (kip-in)", "My (kip-in)", "Mz (kip-in)", "Note" },
                Demands.Select(d => new object[] { d.Calc, d.Step, d.Template, d.Mode, d.Row, d.LC == 0 ? "ENV" : d.LC.ToString(CultureInfo.InvariantCulture), d.Connection,
                    Planner.Fmt(d.F[0]), Planner.Fmt(d.F[1]), Planner.Fmt(d.F[2]), Planner.Fmt(d.F[3]), Planner.Fmt(d.F[4]), Planner.Fmt(d.F[5]), d.Note }));
            p = Path.Combine(dir, "calcs.csv"); files.Add(p);
            Csv.Write(p, new[] { "Calc", "Type", "Group", "Connections", "Cells", "Cases", "Status", "Problems" },
                Calcs.Select(c => new object[] { c.Id, c.Type, c.Group, string.Join(" ", c.Connections.Select(x => x.Connection).ToArray()), c.Cells, c.Cases, c.Status, c.Problems }));
            p = Path.Combine(dir, "problems.csv"); files.Add(p);
            Csv.Write(p, new[] { "Severity", "Calc", "Code", "Message" }, Problems.Select(x => new object[] { x.Severity, x.Calc, x.Code, x.Message }));
            return files.ToArray();
        }
    }

    public static class Planner
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string Fmt(double v)
        {
            return (Math.Abs(v) < 1e-12 ? 0.0 : v).ToString("G12", Inv);
        }

        // STAAD's end force (member local axes, x from end A to end B) in the end's own axes: x along the member pointing
        // away from the joint, y the member's local y, z = x cross y. At end A they are STAAD's axes; at end B STAAD's axes
        // turned 180 deg about y. A frame maps from these, so it holds whichever way the member was drawn.
        public static double[] EndAxes(double[] local, string end)
        {
            if (end != "B") return local;
            return new[] { 0.0 - local[0], local[1], 0.0 - local[2], 0.0 - local[3], local[4], 0.0 - local[5] };
        }

        // templatesDir: where tblTemplates.File is found
        public static PlanResult Run(AnalysisPackage pkg, Library lib, Register reg, string templatesDir)
        {
            var x = new Run1(pkg, lib, reg, templatesDir);
            x.Go();
            return x.Res;
        }

        sealed class DemandRow
        {
            public int LC;
            public RegConnection Conn;
            public double[] F;       // template axes, kip / kip-in
            public string Note = "";
        }

        sealed class Value
        {
            public string Text = "", Source = "";
            public bool IsNumber;
            public double Number;
            public Unit Unit;        // the unit Number is in (null: none known)
        }

        sealed class Run1
        {
            readonly AnalysisPackage _pkg;
            readonly Library _lib;
            readonly Register _reg;
            readonly string _templates;
            public readonly PlanResult Res = new PlanResult();
            readonly Dictionary<string, TemplateBook> _books = new Dictionary<string, TemplateBook>(StringComparer.OrdinalIgnoreCase);
            readonly HashSet<string> _checked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            readonly HashSet<string> _blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<int> _cases = new List<int>();
            PlanCalc _calc;
            int _k = 5;
            bool _basisWarned;

            public Run1(AnalysisPackage pkg, Library lib, Register reg, string templates) { _pkg = pkg; _lib = lib; _reg = reg; _templates = templates; }

            void P(string sev, string calc, string code, string msg)
            {
                var p = new PlanProblem();
                p.Severity = sev; p.Calc = calc; p.Code = code; p.Message = msg;
                Res.Problems.Add(p);
                if (_calc != null && calc == _calc.Id)
                {
                    _calc.Problems++;
                    if (sev == "Error" && _calc.Status == "Ready") _calc.Status = "Gaps";
                }
            }

            public void Go()
            {
                foreach (LibProblem lp in _lib.Problems) P(lp.Severity, "", "LIBRARY", lp.ToString().Substring(lp.Severity.Length + 1));
                foreach (string e in _reg.Errors) P("Error", "", "REGISTER", e);
                int k;
                if (int.TryParse(_reg.Setting("GoverningK", "5"), NumberStyles.Integer, Inv, out k) && k >= 0) _k = k;

                // the gate: member ends the register does not hold yet, rows the package no longer has
                var keys = new HashSet<string>(_reg.Connections.Select(c => c.Key), StringComparer.Ordinal);
                var fresh = _pkg.Ends.Values.Where(e => !keys.Contains(e.Key)).OrderBy(e => e.Node).ThenBy(e => e.Member).ToList();
                Res.NewConnections = fresh.Count;
                if (fresh.Count > 0)
                    P("Warning", "", "NEW_CONNECTIONS", fresh.Count + " member ends are in the package but not in the register (new connections?): " +
                        string.Join(", ", fresh.Take(20).Select(e => RegisterSchema.ConnectionId(e.Node, e.Member, e.End)).ToArray()) + (fresh.Count > 20 ? " ..." : ""));
                foreach (RegConnection c in _reg.Connections.Where(c => !_pkg.Ends.ContainsKey(c.Key) && c.Type.Length > 0))
                    P("Error", c.Connection, "NOT_IN_PACKAGE", "register row " + c.Row + ": member " + c.Member + " end " + c.End + " at joint " + c.Node + " is not in the package (renumbered or removed?)");

                // the cases in scope
                if (_reg.Combos.Count > 0) _cases = _reg.Combos.Values.Where(c => c.Use).Select(c => c.LC).ToList();
                else
                {
                    _cases = _pkg.Cases.Values.Where(c => c.HasForces).Select(c => c.Id).ToList();
                    P("Warning", "", "NO_COMBOS", "the register lists no load cases (tblCombos): every printed case is used");
                }
                foreach (int lc in _cases.Where(lc => !_pkg.Cases.ContainsKey(lc)).ToList())
                    P("Error", "", "CASE_UNKNOWN", "tblCombos uses load case " + lc + ", which the package does not have");
                if (_cases.Count == 0) P("Error", "", "NO_CASES", "no load case is in scope (tblCombos: Use = Y)");
                var unsigned = _cases.Where(lc => { PkgCase c; return _pkg.Cases.TryGetValue(lc, out c) && (c.Method == "SRSS" || c.Method == "ABS"); }).ToList();
                if (unsigned.Count > 0)
                    P("Warning", "", "UNSIGNED_CASES", "case(s) " + string.Join(", ", unsigned.Select(i => i.ToString(Inv)).ToArray()) +
                        " are SRSS / ABS combinations: STAAD forms them from magnitudes, so the signs (uplift, tension, direction) and the concurrency of their end forces are not those of a real load: check what the templates make of them");

                // the calcs: a Group shares one calc
                var active = _reg.Connections.Where(c => c.Type.Length > 0 && !string.Equals(c.Disposition, "Excluded", StringComparison.OrdinalIgnoreCase) && _pkg.Ends.ContainsKey(c.Key)).ToList();
                foreach (var g in active.GroupBy(c => c.Group.Length > 0 ? "G\u0001" + c.Group : "C\u0001" + c.Connection))
                {
                    var calc = new PlanCalc();
                    calc.Connections.AddRange(g);
                    RegConnection first = calc.Connections[0];
                    calc.Group = first.Group;
                    calc.Id = first.Group.Length > 0 ? first.Group : first.Connection;
                    calc.Type = first.Type;
                    Res.Calcs.Add(calc);
                    _calc = calc;
                    var types = calc.Connections.Select(c => c.Type).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    if (types.Count > 1) { P("Error", calc.Id, "GROUP_TYPES", "the group's connections have different types: " + string.Join(", ", types.ToArray())); continue; }
                    LibType t;
                    if (!_lib.Types.TryGetValue(calc.Type, out t)) { P("Error", calc.Id, "TYPE_UNKNOWN", "type '" + calc.Type + "' is not in the calc library"); continue; }
                    foreach (LibStep s in t.Steps) Step(calc, t, s);
                    if (_calc.Status == "Ready" && t.Steps.Any(s => _blocked.Contains(s.Template))) _calc.Status = "Blocked";
                }
                _calc = null;
            }

            // ---- one step of one calc ----

            void Step(PlanCalc calc, LibType type, LibStep step)
            {
                LibTemplate tpl;
                if (!_lib.Templates.TryGetValue(step.Template, out tpl)) { P("Error", calc.Id, "TEMPLATE_UNKNOWN", type.Id + " step " + step.Step + ": no template '" + step.Template + "'"); return; }
                CheckTemplate(tpl);
                if (tpl.Shape == "multi_connection") { P("Error", calc.Id, "SHAPE", tpl.Id + ": the multi_connection shape is not planned yet"); return; }

                // once-only inputs: the same for every case (and every connection of a group)
                foreach (LibInput inp in tpl.Inputs.Where(i => !i.PerCase))
                {
                    if (inp.Binding.Length == 0)
                    {
                        if (inp.Required) P("Error", calc.Id, "UNBOUND", inp.Where + " (" + Label(inp) + ") has no Binding in the calc library");
                        continue;
                    }
                    if (Bindings.Check(inp.Binding, inp.Unit, inp.PerCase) != null) { P("Error", calc.Id, "BAD_BINDING", inp.Where + ": " + Bindings.Check(inp.Binding, inp.Unit, inp.PerCase)); continue; }
                    Value first = null;
                    bool ok = true;
                    foreach (RegConnection c in calc.Connections)
                    {
                        string err;
                        Value v = Resolve(inp, c, type, step, out err);
                        if (v == null)
                        {
                            if (inp.Required || err.StartsWith("!")) P("Error", calc.Id, "UNRESOLVED", inp.Where + " (" + Label(inp) + ", " + inp.Binding + "): " + err.TrimStart('!') + (calc.Connections.Count > 1 ? " [" + c.Connection + "]" : ""));
                            ok = false;
                            break;
                        }
                        if (first == null) first = v;
                        else if (!string.Equals(first.Text, v.Text, StringComparison.Ordinal))
                        {
                            P("Error", calc.Id, "GROUP_DIFFERS", inp.Where + " (" + Label(inp) + "): the group's connections differ (" + calc.Connections[0].Connection + ": " + first.Text + ", " + c.Connection + ": " + v.Text + "); one calc cannot hold both");
                            ok = false;
                            break;
                        }
                    }
                    if (!ok || first == null) continue;
                    var pc = new PlanCell();
                    pc.Calc = calc.Id; pc.Type = type.Id; pc.Step = step.Step; pc.Template = tpl.Id; pc.Sheet = inp.Sheet; pc.Cell = inp.Cell;
                    pc.Input = Label(inp); pc.Binding = inp.Binding; pc.Value = first.Text; pc.Unit = inp.UnitText; pc.Source = first.Source;
                    Res.Cells.Add(pc);
                    calc.Cells++;
                }

                // the demand: the slot's end forces for the cases in scope, in the template's axes
                LibFrame frame;
                if (!_lib.Frames.TryGetValue(step.Frame, out frame)) { P("Error", calc.Id, "FRAME", "no frame '" + step.Frame + "'"); return; }
                var rows = new List<DemandRow>();
                var cases = CasesFor(tpl);
                foreach (RegConnection c in calc.Connections)
                {
                    string err;
                    PkgEnd e = SlotEnd(c, step.Slot, type, out err);
                    if (e == null) { P("Error", calc.Id, "SLOT", type.Id + " step " + step.Step + ": " + err); return; }
                    SortedDictionary<int, double[]> f = _pkg.Forces(e.Member, e.End);
                    var missing = cases.Where(lc => !f.ContainsKey(lc)).ToList();
                    if (missing.Count > 0)
                    {
                        P("Error", calc.Id, "FORCES_MISSING", "member " + e.Member + " end " + e.End + " has no printed forces for case(s) " + string.Join(", ", missing.Take(10).Select(i => i.ToString(Inv)).ToArray()));
                        return;
                    }
                    foreach (int lc in cases)
                    {
                        double[] t = frame.Apply(EndAxes(f[lc], e.End));
                        var d = new DemandRow();
                        d.LC = lc; d.Conn = c;
                        d.F = new double[6];
                        for (int i = 0; i < 6; i++) d.F[i] = Units.Convert(t[i], i < 3 ? _pkg.ForceUnit : _pkg.MomentUnit, Units.Base(i < 3 ? Dimension.Force : Dimension.Moment));
                        rows.Add(d);
                    }
                }
                if (rows.Count == 0) { P("Error", calc.Id, "NO_DEMAND", type.Id + " step " + step.Step + ": no load case in scope for " + tpl.Id); return; }
                List<DemandRow> sel = Select(rows, step.Demand);
                if (tpl.Shape == "case_table" && tpl.MaxCases > 0 && sel.Count > tpl.MaxCases)
                    P("Warning", calc.Id, "CHUNKS", tpl.Id + " takes " + tpl.MaxCases + " cases at a time: " + sel.Count + " cases run in " + ((sel.Count + tpl.MaxCases - 1) / tpl.MaxCases) + " chunks");

                for (int r = 0; r < sel.Count; r++)
                {
                    DemandRow d = sel[r];
                    var pd = new PlanDemand();
                    pd.Calc = calc.Id; pd.Step = step.Step; pd.Template = tpl.Id; pd.Mode = step.Demand; pd.Row = r + 1; pd.LC = d.LC;
                    pd.Connection = d.Conn == null ? "" : d.Conn.Connection; pd.F = d.F; pd.Note = d.Note;
                    Res.Demands.Add(pd);
                    foreach (LibInput inp in tpl.Inputs.Where(i => i.PerCase))
                    {
                        if (inp.Binding.Length == 0)
                        {
                            if (inp.Required && r == 0) P("Error", calc.Id, "UNBOUND", inp.Where + " (" + Label(inp) + ") has no Binding in the calc library");
                            continue;
                        }
                        if (Bindings.Check(inp.Binding, inp.Unit, inp.PerCase) != null) { if (r == 0) P("Error", calc.Id, "BAD_BINDING", inp.Where + ": " + Bindings.Check(inp.Binding, inp.Unit, inp.PerCase)); continue; }
                        string err;
                        string v = PerCase(inp, d, out err);
                        if (v == null) { if (r == 0) P("Error", calc.Id, "UNRESOLVED", inp.Where + " (" + Label(inp) + ", " + inp.Binding + "): " + err); continue; }
                        var pc = new PlanCase();
                        pc.Calc = calc.Id; pc.Step = step.Step; pc.Template = tpl.Id; pc.Row = r + 1; pc.LC = d.LC; pc.Connection = pd.Connection;
                        pc.Sheet = inp.Sheet; pc.Cell = inp.Cell; pc.Binding = inp.Binding; pc.Value = v; pc.Unit = inp.UnitText;
                        Res.CaseCells.Add(pc);
                    }
                }
                calc.Cases += sel.Count;
            }

            static string Label(LibInput i) { return i.GuardText.Length > 0 ? i.GuardText : i.Cell; }



            // the cases a template takes: those in scope, and of its basis when it has one
            List<int> CasesFor(LibTemplate tpl)
            {
                if (tpl.Basis.Length == 0) return _cases;
                var list = new List<int>();
                foreach (int lc in _cases)
                {
                    RegCombo c;
                    string basis = _reg.Combos.TryGetValue(lc, out c) ? c.Basis : "";
                    if (basis.Length == 0)
                    {
                        if (!_basisWarned) { P("Warning", "", "BASIS_NOT_SET", "template " + tpl.Id + " is " + tpl.Basis + " but some cases in scope have no Basis in tblCombos: they are used"); _basisWarned = true; }
                        list.Add(lc);
                    }
                    else if (basis == tpl.Basis) list.Add(lc);
                }
                return list;
            }

            // ---- demand modes ----

            List<DemandRow> Select(List<DemandRow> rows, string mode)
            {
                switch (mode)
                {
                    case "concurrent_envelope": return Concurrent(rows);
                    case "abs_envelope": return new List<DemandRow> { AbsEnvelope(rows) };
                    case "governing_set":
                        var set = Concurrent(rows);
                        var max = new double[6];
                        foreach (DemandRow d in rows) for (int i = 0; i < 6; i++) max[i] = Math.Max(max[i], Math.Abs(d.F[i]));
                        Func<DemandRow, double> proxy = d => { double s = 0; for (int i = 0; i < 6; i++) if (max[i] > 0) s += Math.Abs(d.F[i]) / max[i]; return s; };
                        foreach (DemandRow d in rows.OrderByDescending(proxy).Take(_k)) if (!set.Contains(d)) set.Add(d);
                        return set;
                    default: return rows;
                }
            }

            // the max and the min case of each component, each with its concurrent forces
            static List<DemandRow> Concurrent(List<DemandRow> rows)
            {
                var set = new List<DemandRow>();
                for (int i = 0; i < 6; i++)
                {
                    int ci = i;
                    DemandRow hi = rows.OrderByDescending(d => d.F[ci]).First(), lo = rows.OrderBy(d => d.F[ci]).First();
                    foreach (DemandRow d in new[] { hi, lo })
                    {
                        string tag = (d == hi ? "max " : "min ") + LibrarySchema.Components[i];
                        if (!set.Contains(d)) { d.Note = tag; set.Add(d); }
                        else if (d.Note.IndexOf(tag, StringComparison.Ordinal) < 0) d.Note += ", " + tag;
                    }
                }
                return set;
            }

            // one row of the largest magnitude of each component, with its sign (not concurrent: conservative)
            static DemandRow AbsEnvelope(List<DemandRow> rows)
            {
                var env = new DemandRow();
                env.F = new double[6];
                var notes = new List<string>();
                for (int i = 0; i < 6; i++)
                {
                    int ci = i;
                    DemandRow g = rows.OrderByDescending(d => Math.Abs(d.F[ci])).First();
                    env.F[i] = g.F[i];
                    notes.Add(LibrarySchema.Components[i] + " from LC " + g.LC + (g.Conn != null && rows.Select(r => r.Conn).Distinct().Count() > 1 ? " " + g.Conn.Connection : ""));
                }
                env.Note = "not concurrent: " + string.Join("; ", notes.ToArray());
                return env;
            }

            // ---- the template: found, pinned, guarded ----

            void CheckTemplate(LibTemplate tpl)
            {
                if (!_checked.Add(tpl.Id)) return;
                string path = Path.Combine(_templates, tpl.File.Replace('/', Path.DirectorySeparatorChar));
                TemplateBook b;
                if (!File.Exists(path)) { P("Error", tpl.Id, "TEMPLATE_MISSING", "no template file " + path); _blocked.Add(tpl.Id); return; }
                try { b = TemplateBook.Open(path); }
                catch (Exception ex) { P("Error", tpl.Id, "TEMPLATE_UNREADABLE", path + ": " + ex.Message); _blocked.Add(tpl.Id); return; }
                _books[tpl.Id] = b;
                int bad = 0;
                foreach (LibInput i in tpl.Inputs) bad += Guard(tpl, b, i.Sheet, i.Cell, i.GuardCell, i.GuardText, true);
                foreach (LibOutput o in tpl.Outputs) bad += Guard(tpl, b, o.Sheet, o.Cell, o.GuardCell, o.GuardText, false);
                if (tpl.Sha256.Length > 0 && !string.Equals(tpl.Sha256, b.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    P("Error", tpl.Id, "TEMPLATE_CHANGED", path + " is not the file the library pinned (SHA-256 differs); " +
                        (bad == 0 ? "every guard still holds: if the revision is meant, put its SHA-256 (" + b.Sha256 + ") in tblTemplates" : bad + " guard(s) fail: re-harvest it"));
                    _blocked.Add(tpl.Id);
                }
                if (bad > 0) _blocked.Add(tpl.Id);
                if (tpl.Outputs.Count == 0) P("Warning", tpl.Id, "NO_OUTPUTS", "the library reads nothing back from " + tpl.Id);
            }

            int Guard(LibTemplate tpl, TemplateBook b, string sheet, string cell, string guardCell, string guardText, bool isInput)
            {
                string what = (isInput ? "input " : "output ") + sheet + "!" + cell;
                TSheet s; int r, c; string err;
                if (!b.Locate(sheet, cell, out s, out r, out c, out err)) { P("Error", tpl.Id, "GUARD", what + ": " + err); return 1; }
                TCell x = s.Get(r, c);
                if (isInput && x != null && x.IsFormula) { P("Error", tpl.Id, "INPUT_IS_FORMULA", what + " holds a formula (=" + x.Formula + "): writing it would overwrite the template's logic"); return 1; }
                if (!isInput && (x == null || !x.IsFormula)) { P("Error", tpl.Id, "OUTPUT_NOT_FORMULA", what + " holds no formula: it is not a result"); return 1; }
                if (guardCell.Length == 0) { P("Warning", tpl.Id, "NO_GUARD", what + " has no guard label: a layout change would go unnoticed"); return 0; }
                TSheet gs; int gr, gc;
                if (!b.Locate(s.Name, guardCell, out gs, out gr, out gc, out err)) { P("Error", tpl.Id, "GUARD", what + ": guard " + guardCell + ": " + err); return 1; }
                string text = TemplateBook.TextOf(gs, gr, gc);
                if (!TemplateBook.SameText(text, guardText))
                {
                    P("Error", tpl.Id, "GUARD_MOVED", what + ": " + gs.Name + "!" + guardCell + " reads '" + text + "', the library expects '" + guardText + "' (the template's layout changed?)");
                    return 1;
                }
                return 0;
            }

            // ---- bindings ----

            PkgEnd SlotEnd(RegConnection c, string slot, LibType type, out string err)
            {
                err = "";
                if (slot == "this")
                {
                    PkgEnd e = _pkg.End(c.Node, c.Member, c.End);
                    if (e == null) err = c.Connection + " is not in the package";
                    return e;
                }
                if (!type.Slots.Contains(slot)) { err = "type " + type.Id + " has no slot '" + slot + "'"; return null; }
                int member = 0; string end = "";
                if (string.Equals(slot, "support", StringComparison.OrdinalIgnoreCase) && c.Support > 0) member = c.Support;
                foreach (string part in c.Other.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    Match m = Regex.Match(part, @"^\s*([A-Za-z][A-Za-z0-9_]*)\s*=\s*(\d+)\s*(?::\s*([ABab]))?\s*$");
                    if (!m.Success) { err = c.Connection + ": Other '" + part.Trim() + "' is not slot=member[:end]"; return null; }
                    if (string.Equals(m.Groups[1].Value, slot, StringComparison.OrdinalIgnoreCase))
                    {
                        member = int.Parse(m.Groups[2].Value, Inv);
                        end = m.Groups[3].Success ? m.Groups[3].Value.ToUpperInvariant() : "";
                    }
                }
                if (member == 0) { err = c.Connection + ": the type's slot '" + slot + "' is not given (" + (slot == "support" ? "Support or " : "") + "Other: " + slot + "=member)"; return null; }
                PkgEnd x = end.Length > 0 ? _pkg.End(c.Node, member, end) : _pkg.EndAt(c.Node, member);
                if (x == null) err = c.Connection + ": member " + member + (end.Length > 0 ? " end " + end : "") + " does not end at joint " + c.Node;
                return x;
            }

            // a once-only input's value for one connection; null with err ("!..." = an error even for an optional input)
            Value Resolve(LibInput inp, RegConnection c, LibType type, LibStep step, out string err)
            {
                err = "";
                int colon = inp.Binding.IndexOf(':');
                string kind = inp.Binding.Substring(0, colon).Trim().ToLowerInvariant(), arg = inp.Binding.Substring(colon + 1).Trim();
                var v = new Value();
                switch (kind)
                {
                    case "const":
                        v.Text = arg; v.Source = "constant";
                        return Num(v, inp, inp.Unit, out err);
                    case "param":
                        string sx = step.Step.ToString(Inv);
                        RegOverride o = Over(c.Connection, sx, arg) ?? Over(c.Connection, "", arg);
                        if (o == null && c.Group.Length > 0) o = Over(c.Group, sx, arg) ?? Over(c.Group, "", arg);
                        LibParam p = type.Param(arg, step.Step);
                        Unit pu = null;
                        if (p != null && p.UnitText.Length > 0) Units.TryParse(p.UnitText, out pu);
                        if (o != null) { v.Text = o.Value; v.Source = "override (" + o.Connection + (o.Step.Length > 0 ? " step " + o.Step : "") + ")"; }
                        else if (p != null && p.Default.Length > 0) { v.Text = p.Default; v.Source = "type " + type.Id + " default"; }
                        else { err = "parameter '" + arg + "' has no value: give " + type.Id + " a default (tblParams) or the connection an override (tblOverrides)"; return null; }
                        return Num(v, inp, pu ?? inp.Unit, out err);
                    case "reg":
                        string rv;
                        if (!c.All.TryGetValue(arg, out rv)) { err = "!the register has no column '" + arg + "'"; return null; }
                        if (rv.Length == 0) { err = "register column '" + arg + "' is blank for " + c.Connection; return null; }
                        v.Text = rv; v.Source = "register " + arg;
                        return Num(v, inp, inp.Unit, out err);
                    case "pkg":
                        return Pkg(inp, c, type, arg, out err);
                    case "fn":
                        return Fn(inp, c, type, arg, out err);
                    default:
                        err = "!unknown binding kind '" + kind + "'";
                        return null;
                }
            }

            RegOverride Over(string conn, string step, string param)
            {
                return _reg.Overrides.FirstOrDefault(o => string.Equals(o.Connection, conn, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(o.Step, step, StringComparison.OrdinalIgnoreCase) && string.Equals(o.Param, param, StringComparison.OrdinalIgnoreCase));
            }

            // a number is converted from its unit to the cell's; text passes as it is
            static Value Num(Value v, LibInput inp, Unit from, out string err)
            {
                err = "";
                double d;
                if (!double.TryParse(v.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return v;
                v.IsNumber = true; v.Number = d; v.Unit = from;
                if (from != null && inp.Unit != null && from.Dim != Dimension.None && from.Dim != Dimension.Text)
                {
                    try { v.Number = Units.Convert(d, from, inp.Unit); }
                    catch (InvalidOperationException ex) { err = "!" + ex.Message; return null; }
                    if (from.Name != inp.Unit.Name) v.Source += " (" + Fmt(d) + " " + from + ")";
                }
                v.Text = Fmt(v.Number);
                return v;
            }

            Value Measure(LibInput inp, double value, string unit, string source, out string err)
            {
                var v = new Value();
                v.Text = Fmt(value); v.Source = source;
                Unit u = Units.Parse(unit);
                if (inp.Unit == null) { err = "!the cell has no Unit, so a " + u.Dim.ToString().ToLowerInvariant() + " cannot be written into it safely: give the input its Unit"; return null; }
                return Num(v, inp, u, out err);
            }

            static Value Text(string text, string source)
            {
                var v = new Value();
                v.Text = text; v.Source = source;
                return v;
            }

            Value Pkg(LibInput inp, RegConnection c, LibType type, string arg, out string err)
            {
                err = "";
                int dot = arg.IndexOf('.');
                string slot = arg.Substring(0, dot), field = arg.Substring(dot + 1).ToLowerInvariant();
                if (slot == "node")
                {
                    PkgJoint j;
                    if (!_pkg.Joints.TryGetValue(c.Node, out j)) { err = "!joint " + c.Node + " is not in the package"; return null; }
                    switch (field)
                    {
                        case "id": return Text(j.Node.ToString(Inv), "package: joint");
                        case "x": return Measure(inp, j.X, "in", "package: joint " + j.Node + " X", out err);
                        case "y": return Measure(inp, j.Y, "in", "package: joint " + j.Node + " Y", out err);
                        case "z": return Measure(inp, j.Z, "in", "package: joint " + j.Node + " Z", out err);
                        case "support": return Text(j.Support, "package: joint " + j.Node + " support");
                    }
                    err = "!pkg:node has no field '" + field + "'";
                    return null;
                }
                PkgEnd e = SlotEnd(c, slot, type, out err);
                if (e == null) { err = "!" + err; return null; }
                PkgMember m;
                _pkg.Members.TryGetValue(e.Member, out m);
                string src = "package: member " + e.Member + " end " + e.End;
                switch (field)
                {
                    case "member": return Text(e.Member.ToString(Inv), src);
                    case "end": return Text(e.End, src);
                    case "node": return Text(e.Node.ToString(Inv), src);
                    case "section": return Text(e.Section, src + " section");
                    case "family": return Text(e.Family, src + " family");
                    case "role": return Text(e.Role, src + " role");
                    case "released": return Text(e.Released, src + " releases");
                    case "kind": return Text(e.Kind, src + " kind");
                    case "beta":
                        if (m == null) { err = "!member " + e.Member + " is not in the package"; return null; }
                        if (!m.BetaKnown) { err = "!member " + e.Member + "'s BETA is not known (BETA ANGLE / REFERENCE in STAAD)"; return null; }
                        return Measure(inp, m.Beta, "deg", src + " BETA", out err);
                    case "length":
                        if (m == null) { err = "!member " + e.Member + " is not in the package"; return null; }
                        return Measure(inp, m.Length, "in", src + " length", out err);
                }
                err = "!pkg:" + slot + " has no field '" + field + "'";
                return null;
            }

            Value Fn(LibInput inp, RegConnection c, LibType type, string arg, out string err)
            {
                err = "";
                Match fm = Regex.Match(arg, @"^\s*([A-Za-z]+)\s*\(([^)]*)\)\s*$");
                string name = fm.Groups[1].Value.ToLowerInvariant();
                string[] a = fm.Groups[2].Value.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
                PkgEnd e = SlotEnd(c, a[0], type, out err);
                if (e == null) { err = "!" + err; return null; }
                PkgMember m;
                _pkg.Members.TryGetValue(e.Member, out m);
                string src = "member " + e.Member + " end " + e.End;
                switch (name)
                {
                    case "section": return Text(e.Section, src + " section");
                    case "family": return Text(e.Family, src + " family");
                    case "len":
                        if (m == null) { err = "!member " + e.Member + " is not in the package"; return null; }
                        return Measure(inp, m.Length, "in", src + " length", out err);
                    case "beta":
                        if (m == null || !m.BetaKnown) { err = "!member " + e.Member + "'s BETA is not known"; return null; }
                        return Measure(inp, m.Beta, "deg", src + " BETA", out err);
                    case "angle":
                        PkgEnd e2 = SlotEnd(c, a[1], type, out err);
                        if (e2 == null) { err = "!" + err; return null; }
                        double dot = Math.Max(-1, Math.Min(1, e.Dir[0] * e2.Dir[0] + e.Dir[1] * e2.Dir[1] + e.Dir[2] * e2.Dir[2]));
                        return Measure(inp, Math.Acos(dot) * 180 / Math.PI, "deg", "angle between member " + e.Member + " and member " + e2.Member + " at joint " + c.Node, out err);
                    case "prop":
                        Dictionary<string, string[]> props;
                        if (e.Section.Length == 0 || !_lib.Sections.TryGetValue(e.Section, out props)) { err = "section " + (e.Section.Length == 0 ? "(none)" : e.Section) + " of member " + e.Member + " is not in tblSections"; return null; }
                        string[] pv;
                        if (!props.TryGetValue(a[1], out pv)) { err = "section " + e.Section + " has no property '" + a[1] + "' in tblSections"; return null; }
                        var v = Text(pv[0], "section " + e.Section + " " + a[1]);
                        Unit u = null;
                        if (pv[1].Length > 0) Units.TryParse(pv[1], out u);
                        if (u != null && inp.Unit == null) { err = "!the cell has no Unit, so " + e.Section + " " + a[1] + " (" + u + ") cannot be written into it safely"; return null; }
                        return Num(v, inp, u, out err);
                }
                err = "!no function '" + name + "'";
                return null;
            }

            // a per-case input's value for one demand row
            string PerCase(LibInput inp, DemandRow d, out string err)
            {
                err = "";
                int colon = inp.Binding.IndexOf(':');
                string kind = inp.Binding.Substring(0, colon).Trim().ToLowerInvariant(), arg = inp.Binding.Substring(colon + 1).Trim();
                if (kind == "load")
                {
                    int k = Array.FindIndex(LibrarySchema.Components, x => string.Equals(x, arg, StringComparison.OrdinalIgnoreCase));
                    if (k < 0 || inp.Unit == null) { err = "a load needs its component and the cell's Unit"; return null; }
                    try { return Fmt(Units.Convert(d.F[k], Units.Base(k < 3 ? Dimension.Force : Dimension.Moment), inp.Unit)); }
                    catch (InvalidOperationException ex) { err = ex.Message; return null; }
                }
                if (kind == "case")
                {
                    PkgCase c;
                    _pkg.Cases.TryGetValue(d.LC, out c);
                    if (arg == "id") return d.LC == 0 ? "ENV" : d.LC.ToString(Inv);
                    if (arg == "title") return d.LC == 0 ? "envelope" : (c == null ? "" : c.Title);
                    if (arg == "kind") return d.LC == 0 ? "ENVELOPE" : (c == null ? "" : c.Kind);
                }
                err = "the binding is not a per-case one (load: or case:)";
                return null;
            }
        }
    }
}
