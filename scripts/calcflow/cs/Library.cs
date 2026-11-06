// Library.cs -- the calc library: what every locked calc template takes and gives (its cell map with label guards),
// and the connection types that use the templates (their steps, slots, parameters, sign maps), plus the section
// properties the bindings may look up.
//   edited  : CalcFlow_Library.xlsx, in Excel (one table per sheet: tblTemplates, tblInputs, tblOutputs, tblFrames,
//             tblTypes, tblSteps, tblParams, tblSections)
//   canonical: text\<table>.csv, written by Export (sorted, one CSV per table) with text\_source.txt naming the
//             workbook and its SHA-256; this is what the tool runs from and what a review diffs. A workbook that
//             differs from its last export is refused ("export the library first").
// Bindings (Inputs.Binding) say where an input's value comes from:
//   const:<value>       a literal                      param:<name>     the connection's override, else the type's
//   reg:<column>        a register column              pkg:<slot>.<field>  a package fact (slot: this, node, a type slot)
//   load:<Fx..Mz>       the demand, per case           case:<id|title|kind>  the case, per case
//   fn:<name>(args)     prop(slot,prop) len(slot) angle(slotA,slotB) section(slot) family(slot) beta(slot)
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
    public sealed class TableDef
    {
        public string Name = "", Sheet = "", File = "", Note = "";
        public string[] Columns = new string[0];
        public string[] Keys = new string[0];
    }

    public static class LibrarySchema
    {
        public static readonly string[] TemplateColumns = { "Template", "File", "Sha256", "Shape", "MaxCases", "Basis", "Calculation", "Notes" };
        public static readonly string[] InputColumns = { "Template", "Sheet", "Cell", "GuardCell", "GuardText", "Unit", "PerCase", "Binding", "Required", "Notes", "Example", "Kind", "List", "Description", "Found" };
        public static readonly string[] OutputColumns = { "Template", "Sheet", "Cell", "GuardCell", "GuardText", "Output", "Kind", "Check", "Expect", "PerCase", "Notes", "Example", "Formula", "Found" };
        public static readonly string[] FrameColumns = { "Frame", "Fx", "Fy", "Fz", "Mx", "My", "Mz", "Mirror", "Action", "Confirmed", "Notes" };
        public static readonly string[] TypeColumns = { "Type", "Description", "Slots", "Notes" };
        public static readonly string[] StepColumns = { "Type", "Step", "Template", "Demand", "Slot", "Frame", "Notes" };
        public static readonly string[] ParamColumns = { "Type", "Step", "Param", "Default", "Unit", "Notes" };
        public static readonly string[] SectionColumns = { "Section", "Prop", "Value", "Unit", "Notes" };

        public static readonly TableDef[] Tables =
        {
            Def("tblTemplates", "Templates", "templates.csv", TemplateColumns, new[] { "Template" },
                "one row per locked calc workbook: its file (relative to the templates folder), its SHA-256 (the tool refuses a different file), its shape (single_case: one case in fixed cells; case_table: a block of case rows; multi_connection: many connections in rows)"),
            Def("tblInputs", "Inputs", "inputs.csv", InputColumns, new[] { "Template", "Sheet", "Cell" },
                "one row per input cell (or defined name). GuardCell / GuardText: the label the tool checks before it writes. Binding: where the value comes from (see Guide)"),
            Def("tblOutputs", "Outputs", "outputs.csv", OutputColumns, new[] { "Template", "Sheet", "Cell" },
                "one row per result read back. Kind: dcr (a demand / capacity ratio, summarised by Check), status (OK / NG), health (a solver flag that must equal Expect), value"),
            Def("tblFrames", "Frames", "frames.csv", FrameColumns, new[] { "Frame" },
                "sign maps from a member end's forces (end axes: x along the member away from the joint, y the member's local y, z = x cross y; STAAD's sign) to a template's axes, e.g. Fx = +Fz. Action on_connection negates (the force on the connection). Confirmed = Y once an engineer has checked it"),
            Def("tblTypes", "Types", "types.csv", TypeColumns, new[] { "Type" },
                "one row per connection type. Slots: the member ends besides this one that its bindings name (e.g. support)"),
            Def("tblSteps", "Steps", "steps.csv", StepColumns, new[] { "Type", "Step" },
                "the calcs a type runs, in order: a template each, its demand mode, whose end forces (Slot, default this) and through which Frame"),
            Def("tblParams", "Params", "params.csv", ParamColumns, new[] { "Type", "Step", "Param" },
                "parameter defaults per type (Step blank = every step); a connection's override in the register wins"),
            Def("tblSections", "Sections", "sections.csv", SectionColumns, new[] { "Section", "Prop" },
                "section properties for fn:prop(slot, prop), one row per section and property")
        };

        public static readonly string[] Shapes = { "single_case", "case_table", "multi_connection" };
        public static readonly string[] Demands = { "per_combo", "concurrent_envelope", "abs_envelope", "governing_set" };
        public static readonly string[] OutputKinds = { "dcr", "status", "health", "value" };
        public static readonly string[] Components = { "Fx", "Fy", "Fz", "Mx", "My", "Mz" };

        static TableDef Def(string name, string sheet, string file, string[] cols, string[] keys, string note)
        {
            var t = new TableDef();
            t.Name = name; t.Sheet = sheet; t.File = file; t.Columns = cols; t.Keys = keys; t.Note = note;
            return t;
        }

        public static TableDef Table(string name)
        {
            foreach (TableDef t in Tables) if (string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) return t;
            return null;
        }
    }

    // ---- typed rows ----

    public sealed class LibTemplate
    {
        public string Id = "", File = "", Sha256 = "", Shape = "single_case", Basis = "", Calculation = "";
        public int MaxCases;
        public readonly List<LibInput> Inputs = new List<LibInput>();
        public readonly List<LibOutput> Outputs = new List<LibOutput>();
    }

    public sealed class LibInput
    {
        public string Template = "", Sheet = "", Cell = "", GuardCell = "", GuardText = "", UnitText = "", Binding = "";
        public Unit Unit;              // null when UnitText is blank (the value is written as resolved)
        public bool PerCase, Required = true;
        public string Where { get { return Template + " " + Sheet + "!" + Cell; } }
    }

    public sealed class LibOutput
    {
        public string Template = "", Sheet = "", Cell = "", GuardCell = "", GuardText = "", Output = "", Kind = "", Check = "", Expect = "";
        public bool PerCase;
        public string Where { get { return Template + " " + Sheet + "!" + Cell; } }
    }

    public sealed class LibFrame
    {
        public string Id = "";
        public readonly int[] From = new int[6];        // template component i takes member-local component From[i] ...
        public readonly double[] Sign = new double[6];  // ... times Sign[i]
        public bool Mirror, OnConnection, Confirmed;

        public static LibFrame Identity()
        {
            var f = new LibFrame();
            f.Id = "IDENTITY"; f.Confirmed = true;
            for (int i = 0; i < 6; i++) { f.From[i] = i; f.Sign[i] = 1; }
            return f;
        }

        // member-local (STAAD) end force -> the template's axes
        public double[] Apply(double[] local)
        {
            var v = new double[6];
            double a = OnConnection ? -1 : 1;
            for (int i = 0; i < 6; i++) v[i] = a * Sign[i] * local[From[i]] + 0.0;
            return v;
        }
    }

    public sealed class LibStep
    {
        public string Type = "", Template = "", Demand = "per_combo", Slot = "this", Frame = "IDENTITY";
        public int Step;
    }

    public sealed class LibParam
    {
        public string Type = "", Step = "", Param = "", Default = "", UnitText = "";
    }

    public sealed class LibType
    {
        public string Id = "", Description = "";
        public readonly List<string> Slots = new List<string>();
        public readonly List<LibStep> Steps = new List<LibStep>();
        public readonly List<LibParam> Params = new List<LibParam>();

        // the type default of a parameter for a step: the step's own row first, else the all-steps row
        public LibParam Param(string name, int step)
        {
            string s = step.ToString(CultureInfo.InvariantCulture);
            LibParam all = null;
            foreach (LibParam p in Params)
            {
                if (!string.Equals(p.Param, name, StringComparison.OrdinalIgnoreCase)) continue;
                if (p.Step == s) return p;
                if (p.Step.Length == 0) all = p;
            }
            return all;
        }
    }

    public sealed class LibProblem
    {
        public string Severity = "", Table = "", Row = "", Message = "";
        public override string ToString() { return Severity + " " + Table + (Row.Length > 0 ? " row " + Row : "") + ": " + Message; }
    }

    public sealed class Library
    {
        public string Folder = "";      // the library folder (holds CalcFlow_Library.xlsx and text\)
        public string Source = "";      // what was loaded: the text folder or a workbook
        public readonly Dictionary<string, List<Dictionary<string, string>>> Rows = new Dictionary<string, List<Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, LibTemplate> Templates = new Dictionary<string, LibTemplate>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, LibFrame> Frames = new Dictionary<string, LibFrame>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, LibType> Types = new Dictionary<string, LibType>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, Dictionary<string, string[]>> Sections = new Dictionary<string, Dictionary<string, string[]>>(StringComparer.OrdinalIgnoreCase);
        public readonly List<LibProblem> Problems = new List<LibProblem>();

        public const string WorkbookName = "CalcFlow_Library.xlsx";
        public const string TextFolder = "text";
        public const string StampFile = "_source.txt";

        public int ErrorCount { get { return Problems.Count(p => p.Severity == "Error"); } }

        // ---- loading ----

        // the library folder's canonical text, refusing a workbook edited since its export
        public static Library Load(string folder)
        {
            string dir = Path.GetFullPath(folder);
            if (File.Exists(dir) && dir.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) dir = Path.GetDirectoryName(dir);
            string text = Path.Combine(dir, TextFolder);
            var lib = new Library();
            lib.Folder = dir; lib.Source = text;
            if (!Directory.Exists(text))
            {
                lib.Problem("Error", "", "", "no exported library in " + text + " (run: CalcFlow.ps1 library-export -Library \"" + dir + "\")");
                return lib;
            }
            foreach (TableDef t in LibrarySchema.Tables) lib.Rows[t.Name] = Csv.ReadTable(Path.Combine(text, t.File));
            string wb = Path.Combine(dir, WorkbookName), stamp = Path.Combine(text, StampFile);
            if (File.Exists(wb))
            {
                string sha = SharedFile.Sha256(wb);
                string stamped = File.Exists(stamp) ? StampedSha(File.ReadAllText(stamp)) : "";
                if (!string.Equals(sha, stamped, StringComparison.OrdinalIgnoreCase))
                    lib.Problem("Error", "", "", WorkbookName + " was changed after its last export: run library-export so the text the tool runs from is the workbook you edited");
            }
            lib.Build();
            return lib;
        }

        // straight from a workbook (for export and checks)
        public static Library FromWorkbook(string path)
        {
            XWorkbook wb = Xlsx.Read(path);
            var lib = new Library();
            lib.Folder = Path.GetDirectoryName(Path.GetFullPath(path)); lib.Source = Path.GetFullPath(path);
            foreach (TableDef t in LibrarySchema.Tables)
            {
                List<Dictionary<string, string>> rows = wb.TableRows(t.Name);
                if (rows == null) { lib.Problem("Error", t.Name, "", "the workbook has no table " + t.Name + " (sheet " + t.Sheet + ")"); rows = new List<Dictionary<string, string>>(); }
                foreach (var r in rows) r.Remove("__row");
                lib.Rows[t.Name] = rows;
            }
            lib.Build();
            return lib;
        }

        static string StampedSha(string stamp)
        {
            Match m = Regex.Match(stamp, @"(?im)^sha256\s*[:=]\s*([0-9a-f]{64})\s*$");
            return m.Success ? m.Groups[1].Value : "";
        }

        // ---- export: workbook -> canonical text ----

        public static Library Export(string workbookPath)
        {
            Library lib = FromWorkbook(workbookPath);
            string text = Path.Combine(lib.Folder, TextFolder);
            Directory.CreateDirectory(text);
            foreach (TableDef t in LibrarySchema.Tables) WriteCsv(Path.Combine(text, t.File), t, lib.Rows[t.Name]);
            var sb = new StringBuilder();
            sb.Append("workbook: ").Append(Path.GetFileName(workbookPath)).Append("\r\n");
            sb.Append("sha256: ").Append(SharedFile.Sha256(workbookPath)).Append("\r\n");
            sb.Append("exported: ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append("\r\n");
            SharedFile.WriteAllText(Path.Combine(text, StampFile), sb.ToString());
            return lib;
        }

        // the schema's columns first, then any extra columns in the rows (kept, alphabetical); rows sorted by key
        public static void WriteCsv(string path, TableDef t, List<Dictionary<string, string>> rows)
        {
            var cols = new List<string>(t.Columns);
            foreach (var r in rows)
                foreach (string k in r.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    if (k != "__row" && !cols.Any(c => string.Equals(c, k, StringComparison.OrdinalIgnoreCase))) cols.Add(k);
            IEnumerable<Dictionary<string, string>> sorted = rows;
            IOrderedEnumerable<Dictionary<string, string>> o = null;
            foreach (string key in t.Keys)
            {
                string k = key;
                o = o == null ? sorted.OrderBy(r => Get(r, k), NaturalComparer.Instance) : o.ThenBy(r => Get(r, k), NaturalComparer.Instance);
            }
            if (o != null) sorted = o;
            Csv.Write(path, cols, sorted.Select(r => cols.Select(c => (object)Get(r, c)).ToArray()));
        }

        static string Get(Dictionary<string, string> r, string k) { string v; return r.TryGetValue(k, out v) ? (v ?? "").Trim() : ""; }

        // ---- a new workbook: empty, from canonical text, or seeded with harvest drafts ----

        public static void WriteWorkbook(string path, Dictionary<string, List<Dictionary<string, string>>> rows)
        {
            var sheets = new List<XOutSheet>();
            sheets.Add(GuideSheet());
            foreach (TableDef t in LibrarySchema.Tables)
            {
                List<Dictionary<string, string>> r;
                if (rows == null || !rows.TryGetValue(t.Name, out r)) r = new List<Dictionary<string, string>>();
                if (t.Name == "tblFrames" && !r.Any(x => string.Equals(Get(x, "Frame"), "IDENTITY", StringComparison.OrdinalIgnoreCase)))
                {
                    var id = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    id["Frame"] = "IDENTITY"; id["Fx"] = "+Fx"; id["Fy"] = "+Fy"; id["Fz"] = "+Fz"; id["Mx"] = "+Mx"; id["My"] = "+My"; id["Mz"] = "+Mz";
                    id["Mirror"] = "N"; id["Action"] = "on_member"; id["Confirmed"] = "Y"; id["Notes"] = "STAAD's member-local end forces as printed (built in)";
                    r = new[] { id }.Concat(r).ToList();
                }
                XOutSheet s = WorkbookTables.Sheet(t.Sheet, t.Name, t.Columns, r, t.Note);
                Validations(s, t, r.Count);
                sheets.Add(s);
            }
            XlsxWriter.Write(path, sheets, null);
        }

        static void Validations(XOutSheet s, TableDef t, int n)
        {
            int last = Math.Max(n + 2, 3) + 500;   // rows of room below the data
            Action<string, string> list = (col, values) =>
            {
                int k = Array.IndexOf(t.Columns, col);
                if (k < 0) return;
                var v = new XValidation();
                string c = Xlsx.ColName(k);
                v.Sqref = c + "3:" + c + last.ToString(CultureInfo.InvariantCulture);
                v.List = values; v.Strict = false;
                s.Validations.Add(v);
            };
            switch (t.Name)
            {
                case "tblTemplates":
                    list("Shape", "\"" + string.Join(",", LibrarySchema.Shapes) + "\"");
                    list("Basis", "\"LRFD,ASD\""); list("Calculation", "\"normal,full\"");
                    break;
                case "tblInputs":
                    list("Template", "Templates!$A$3:$A$1000"); list("PerCase", "\"Y,N\""); list("Required", "\"Y,N\"");
                    break;
                case "tblOutputs":
                    list("Template", "Templates!$A$3:$A$1000"); list("Kind", "\"" + string.Join(",", LibrarySchema.OutputKinds) + "\""); list("PerCase", "\"Y,N\"");
                    break;
                case "tblFrames":
                    list("Mirror", "\"Y,N\""); list("Action", "\"on_member,on_connection\""); list("Confirmed", "\"Y,N\"");
                    break;
                case "tblSteps":
                    list("Type", "Types!$A$3:$A$1000"); list("Template", "Templates!$A$3:$A$1000");
                    list("Demand", "\"" + string.Join(",", LibrarySchema.Demands) + "\""); list("Frame", "Frames!$A$3:$A$1000");
                    break;
                case "tblParams":
                    list("Type", "Types!$A$3:$A$1000");
                    break;
            }
        }

        static XOutSheet GuideSheet()
        {
            var s = new XOutSheet("Guide");
            var h = new XStyle(); h.Bold = true; h.Size = 13;
            var b = new XStyle(); b.Bold = true;
            s.Add(new XCell("CalcFlow calc library", h));
            s.Add("Edit the tables in Excel, save, then run: CalcFlow.ps1 library-export -Library <this folder>. The tool runs from the exported text (text\\*.csv) and refuses this workbook while it differs from its last export.");
            s.Add();
            s.Add(new XCell("Binding (tblInputs)", b), new XCell("Value written into the cell", b));
            s.Add("const:<value>", "a literal, e.g. const:LRFD, const:0.75");
            s.Add("param:<name>", "the connection's override (register tblOverrides), else the type's default (tblParams; a Step row wins over a blank-Step row)");
            s.Add("reg:<column>", "a column of the connection's register row");
            s.Add("pkg:<slot>.<field>", "a fact from the analysis package. Slots: this (the connection's member end), node (its joint), any slot the type names. Fields: Member, End, Node, Section, Family, Role, Released, Kind, Beta, Length; node.X / Y / Z / Support");
            s.Add("load:<Fx|Fy|Fz|Mx|My|Mz>", "the demand component in the template's axes (the step's Frame), per case (PerCase = Y)");
            s.Add("case:<id|title|kind>", "the load case, per case");
            s.Add("fn:prop(slot,prop)", "a section property from tblSections (d, bf, tf, tw ...), in its Unit");
            s.Add("fn:len(slot)  fn:angle(a,b)  fn:section(slot)  fn:family(slot)  fn:beta(slot)", "member length, the angle between two member ends at the joint (deg), section name, family, BETA");
            s.Add();
            s.Add(new XCell("Units", b), new XCell("in ft mm cm m | in2 in4 | kip lbf kN N | kip-in kip-ft lbf-in lbf-ft kN-m N-mm | ksi psi MPa | deg rad. A value converts only within its dimension: a moment never goes into a force cell.", null));
            s.Add(new XCell("Demand", b), new XCell("per_combo: every case in scope; concurrent_envelope: the max and min case of each component, with its concurrent forces (at most 12); abs_envelope: one row of the largest magnitudes (not concurrent, conservative); governing_set: the concurrent envelope plus the top cases by a normalised sum", null));
            s.Add(new XCell("End axes", b), new XCell("A member end's forces are taken in the end's own axes: x along the member pointing away from the joint, y the member's local y (BETA included), z = x cross y. At a start joint (end A) these are STAAD's member axes; at an end joint (end B) STAAD's axes turned 180 deg about y, so Fx, Fz, Mx and Mz change sign. The sign stays STAAD's: the force on the member at that end. A frame therefore holds whichever way a member was drawn.", null));
            s.Add(new XCell("Frames", b), new XCell("Fx = +Fz means the template's Fx is the end's Fz. The forces must be a signed permutation; the moments follow it (a mirror, Mirror = Y, negates them). Action on_connection negates all six: the force on the connection instead of on the member.", null));
            s.Width(0, 34); s.Width(1, 140);
            return s;
        }

        // ---- typed view and checks ----

        void Problem(string severity, string table, string row, string message)
        {
            var p = new LibProblem();
            p.Severity = severity; p.Table = table; p.Row = row; p.Message = message;
            Problems.Add(p);
        }

        List<Dictionary<string, string>> T(string name)
        {
            List<Dictionary<string, string>> r;
            return Rows.TryGetValue(name, out r) ? r : new List<Dictionary<string, string>>();
        }

        static string RowNo(int i) { return (i + 1).ToString(CultureInfo.InvariantCulture); }

        static bool YesNo(string v, bool dflt, out bool ok)
        {
            ok = true;
            if (v.Length == 0) return dflt;
            if (string.Equals(v, "Y", StringComparison.OrdinalIgnoreCase) || string.Equals(v, "YES", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(v, "N", StringComparison.OrdinalIgnoreCase) || string.Equals(v, "NO", StringComparison.OrdinalIgnoreCase)) return false;
            ok = false;
            return dflt;
        }

        void Build()
        {
            bool ok;
            Frames["IDENTITY"] = LibFrame.Identity();
            var rows = T("tblTemplates");
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                var t = new LibTemplate();
                t.Id = Get(r, "Template"); t.File = Get(r, "File"); t.Sha256 = Get(r, "Sha256"); t.Basis = Get(r, "Basis"); t.Calculation = Get(r, "Calculation");
                string shape = Get(r, "Shape");
                if (t.Id.Length == 0) { Problem("Error", "tblTemplates", RowNo(i), "a template without an id"); continue; }
                if (Templates.ContainsKey(t.Id)) { Problem("Error", "tblTemplates", RowNo(i), "template " + t.Id + " is listed twice"); continue; }
                if (shape.Length > 0 && !LibrarySchema.Shapes.Contains(shape)) Problem("Error", "tblTemplates", RowNo(i), t.Id + ": unknown Shape '" + shape + "'");
                if (shape.Length > 0) t.Shape = shape;
                string mc = Get(r, "MaxCases");
                if (mc.Length > 0 && !int.TryParse(mc, NumberStyles.Integer, CultureInfo.InvariantCulture, out t.MaxCases)) Problem("Error", "tblTemplates", RowNo(i), t.Id + ": MaxCases is not a whole number");
                if (t.Basis.Length > 0 && t.Basis != "LRFD" && t.Basis != "ASD") Problem("Error", "tblTemplates", RowNo(i), t.Id + ": Basis must be LRFD or ASD");
                if (t.File.Length == 0) Problem("Error", "tblTemplates", RowNo(i), t.Id + ": no File");
                if (t.Sha256.Length == 0) Problem("Warning", "tblTemplates", RowNo(i), t.Id + ": no Sha256 pinned (harvest fills it; plan pins nothing without it)");
                Templates[t.Id] = t;
            }
            rows = T("tblInputs");
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                var x = new LibInput();
                x.Template = Get(r, "Template"); x.Sheet = Get(r, "Sheet"); x.Cell = Get(r, "Cell"); x.GuardCell = Get(r, "GuardCell"); x.GuardText = Get(r, "GuardText");
                x.UnitText = Get(r, "Unit"); x.Binding = Get(r, "Binding");
                x.PerCase = YesNo(Get(r, "PerCase"), false, out ok);
                if (!ok) Problem("Error", "tblInputs", RowNo(i), x.Where + ": PerCase must be Y or N");
                x.Required = YesNo(Get(r, "Required"), true, out ok);
                if (!ok) Problem("Error", "tblInputs", RowNo(i), x.Where + ": Required must be Y or N");
                LibTemplate t;
                if (!Templates.TryGetValue(x.Template, out t)) { Problem("Error", "tblInputs", RowNo(i), x.Where + ": no such template"); continue; }
                if (x.Cell.Length == 0) { Problem("Error", "tblInputs", RowNo(i), x.Where + ": no Cell"); continue; }
                if (x.UnitText.Length > 0 && !Units.TryParse(x.UnitText, out x.Unit)) Problem("Error", "tblInputs", RowNo(i), x.Where + ": unknown unit '" + x.UnitText + "'");
                string err = Bindings.Check(x.Binding, x.Unit, x.PerCase);
                if (err != null) Problem(x.Binding.Length == 0 ? "Warning" : "Error", "tblInputs", RowNo(i), x.Where + ": " + err);
                t.Inputs.Add(x);
            }
            rows = T("tblOutputs");
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                var x = new LibOutput();
                x.Template = Get(r, "Template"); x.Sheet = Get(r, "Sheet"); x.Cell = Get(r, "Cell"); x.GuardCell = Get(r, "GuardCell"); x.GuardText = Get(r, "GuardText");
                x.Output = Get(r, "Output"); x.Kind = Get(r, "Kind").ToLowerInvariant(); x.Check = Get(r, "Check"); x.Expect = Get(r, "Expect");
                x.PerCase = YesNo(Get(r, "PerCase"), false, out ok);
                LibTemplate t;
                if (!Templates.TryGetValue(x.Template, out t)) { Problem("Error", "tblOutputs", RowNo(i), x.Where + ": no such template"); continue; }
                if (!LibrarySchema.OutputKinds.Contains(x.Kind)) Problem("Error", "tblOutputs", RowNo(i), x.Where + ": Kind must be one of " + string.Join(", ", LibrarySchema.OutputKinds));
                if (x.Kind == "health" && x.Expect.Length == 0) Problem("Error", "tblOutputs", RowNo(i), x.Where + ": a health output needs Expect (the value it must have, e.g. TRUE)");
                if (x.Kind == "dcr" && x.Check.Length == 0) Problem("Warning", "tblOutputs", RowNo(i), x.Where + ": a DCR without a Check name (the summary column it goes in)");
                if (x.Output.Length == 0) x.Output = x.Cell;
                t.Outputs.Add(x);
            }
            rows = T("tblFrames");
            for (int i = 0; i < rows.Count; i++)
            {
                LibFrame f = ParseFrame(rows[i], RowNo(i));
                if (f == null) continue;
                if (string.Equals(f.Id, "IDENTITY", StringComparison.OrdinalIgnoreCase)) continue;   // built in
                if (Frames.ContainsKey(f.Id)) { Problem("Error", "tblFrames", RowNo(i), "frame " + f.Id + " is listed twice"); continue; }
                Frames[f.Id] = f;
            }
            rows = T("tblTypes");
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                var t = new LibType();
                t.Id = Get(r, "Type"); t.Description = Get(r, "Description");
                if (t.Id.Length == 0) { Problem("Error", "tblTypes", RowNo(i), "a type without an id"); continue; }
                if (Types.ContainsKey(t.Id)) { Problem("Error", "tblTypes", RowNo(i), "type " + t.Id + " is listed twice"); continue; }
                foreach (string sl in Get(r, "Slots").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!Regex.IsMatch(sl, "^[A-Za-z][A-Za-z0-9_]*$") || sl == "this" || sl == "node") Problem("Error", "tblTypes", RowNo(i), t.Id + ": slot name '" + sl + "' (letters and digits; this and node are built in)");
                    else t.Slots.Add(sl);
                }
                Types[t.Id] = t;
            }
            rows = T("tblSteps");
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                var s = new LibStep();
                s.Type = Get(r, "Type"); s.Template = Get(r, "Template");
                string d = Get(r, "Demand"), sl = Get(r, "Slot"), fr = Get(r, "Frame"), st = Get(r, "Step");
                LibType t;
                if (!Types.TryGetValue(s.Type, out t)) { Problem("Error", "tblSteps", RowNo(i), "step of an unknown type '" + s.Type + "'"); continue; }
                if (!int.TryParse(st, NumberStyles.Integer, CultureInfo.InvariantCulture, out s.Step) || s.Step < 1) { Problem("Error", "tblSteps", RowNo(i), s.Type + ": Step must be 1, 2, ..."); continue; }
                if (t.Steps.Any(x => x.Step == s.Step)) { Problem("Error", "tblSteps", RowNo(i), s.Type + ": step " + s.Step + " is listed twice"); continue; }
                if (!Templates.ContainsKey(s.Template)) Problem("Error", "tblSteps", RowNo(i), s.Type + " step " + s.Step + ": no such template '" + s.Template + "'");
                if (d.Length > 0) { if (!LibrarySchema.Demands.Contains(d)) Problem("Error", "tblSteps", RowNo(i), s.Type + " step " + s.Step + ": Demand must be one of " + string.Join(", ", LibrarySchema.Demands)); else s.Demand = d; }
                if (sl.Length > 0) { if (sl != "this" && !t.Slots.Contains(sl)) Problem("Error", "tblSteps", RowNo(i), s.Type + " step " + s.Step + ": the type has no slot '" + sl + "'"); else s.Slot = sl; }
                if (fr.Length > 0) s.Frame = fr;
                t.Steps.Add(s);
            }
            var unconfirmed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (LibType t in Types.Values)
            {
                t.Steps.Sort((a, b) => a.Step.CompareTo(b.Step));
                if (t.Steps.Count == 0) Problem("Warning", "tblTypes", "", "type " + t.Id + " has no steps");
                foreach (LibStep s in t.Steps)
                {
                    LibFrame f;
                    if (!Frames.TryGetValue(s.Frame, out f)) Problem("Error", "tblSteps", "", s.Type + " step " + s.Step + ": no frame '" + s.Frame + "'");
                    else if (!f.Confirmed && unconfirmed.Add(f.Id)) Problem("Warning", "tblFrames", "", "frame " + f.Id + " (used by " + s.Type + " step " + s.Step + ") is not confirmed by an engineer");
                }
            }
            rows = T("tblParams");
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                var p = new LibParam();
                p.Type = Get(r, "Type"); p.Step = Get(r, "Step"); p.Param = Get(r, "Param"); p.Default = Get(r, "Default"); p.UnitText = Get(r, "Unit");
                LibType t;
                if (!Types.TryGetValue(p.Type, out t)) { Problem("Error", "tblParams", RowNo(i), "parameter of an unknown type '" + p.Type + "'"); continue; }
                int st;
                if (p.Step.Length > 0 && (!int.TryParse(p.Step, NumberStyles.Integer, CultureInfo.InvariantCulture, out st) || !t.Steps.Any(x => x.Step == st)))
                    Problem("Error", "tblParams", RowNo(i), p.Type + ": no step '" + p.Step + "' for parameter " + p.Param);
                Unit u;
                if (p.UnitText.Length > 0 && !Units.TryParse(p.UnitText, out u)) Problem("Error", "tblParams", RowNo(i), p.Type + "." + p.Param + ": unknown unit '" + p.UnitText + "'");
                t.Params.Add(p);
            }
            rows = T("tblSections");
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                string sec = Get(r, "Section"), prop = Get(r, "Prop"), val = Get(r, "Value"), unit = Get(r, "Unit");
                double v; Unit u;
                if (sec.Length == 0 || prop.Length == 0) { Problem("Error", "tblSections", RowNo(i), "Section and Prop are needed"); continue; }
                if (!double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) { Problem("Error", "tblSections", RowNo(i), sec + "." + prop + ": Value is not a number"); continue; }
                if (unit.Length > 0 && !Units.TryParse(unit, out u)) { Problem("Error", "tblSections", RowNo(i), sec + "." + prop + ": unknown unit '" + unit + "'"); continue; }
                Dictionary<string, string[]> d;
                if (!Sections.TryGetValue(sec, out d)) { d = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase); Sections[sec] = d; }
                d[prop] = new[] { val, unit };
            }
        }

        static readonly Regex Comp = new Regex(@"^\s*([+-]?)\s*(Fx|Fy|Fz|Mx|My|Mz)\s*$", RegexOptions.IgnoreCase);

        LibFrame ParseFrame(Dictionary<string, string> r, string row)
        {
            var f = new LibFrame();
            f.Id = Get(r, "Frame");
            if (f.Id.Length == 0) { Problem("Error", "tblFrames", row, "a frame without an id"); return null; }
            for (int i = 0; i < 6; i++)
            {
                string v = Get(r, LibrarySchema.Components[i]);
                Match m = Comp.Match(v);
                if (!m.Success) { Problem("Error", "tblFrames", row, f.Id + "." + LibrarySchema.Components[i] + ": '" + v + "' is not +/- one of Fx Fy Fz Mx My Mz"); return null; }
                int from = Array.FindIndex(LibrarySchema.Components, c => string.Equals(c, m.Groups[2].Value, StringComparison.OrdinalIgnoreCase));
                if ((i < 3) != (from < 3)) { Problem("Error", "tblFrames", row, f.Id + "." + LibrarySchema.Components[i] + ": a force takes a force, a moment a moment"); return null; }
                f.From[i] = from; f.Sign[i] = m.Groups[1].Value == "-" ? -1 : 1;
            }
            bool ok;
            f.Mirror = YesNo(Get(r, "Mirror"), false, out ok);
            f.Confirmed = YesNo(Get(r, "Confirmed"), false, out ok);
            string action = Get(r, "Action");
            if (action.Length > 0 && action != "on_member" && action != "on_connection") { Problem("Error", "tblFrames", row, f.Id + ": Action must be on_member or on_connection"); return null; }
            f.OnConnection = action == "on_connection";
            // the forces: a signed permutation; the moments: the same permutation, times its determinant (a mirror)
            var seen = new bool[3];
            var p = new double[3, 3];
            for (int i = 0; i < 3; i++)
            {
                if (seen[f.From[i]]) { Problem("Error", "tblFrames", row, f.Id + ": the forces must each take a different component"); return null; }
                seen[f.From[i]] = true;
                p[i, f.From[i]] = f.Sign[i];
            }
            double det = p[0, 0] * (p[1, 1] * p[2, 2] - p[1, 2] * p[2, 1]) - p[0, 1] * (p[1, 0] * p[2, 2] - p[1, 2] * p[2, 0]) + p[0, 2] * (p[1, 0] * p[2, 1] - p[1, 1] * p[2, 0]);
            if (det < 0 && !f.Mirror) { Problem("Error", "tblFrames", row, f.Id + ": the force map is a mirror image (determinant -1); set Mirror = Y if that is meant"); return null; }
            if (det > 0 && f.Mirror) { Problem("Error", "tblFrames", row, f.Id + ": Mirror = Y but the force map is a rotation"); return null; }
            for (int i = 0; i < 3; i++)
            {
                bool same = f.From[i + 3] == f.From[i] + 3 && f.Sign[i + 3] == f.Sign[i] * det;
                if (!same)
                {
                    string want = (f.Sign[i] * det > 0 ? "+" : "-") + LibrarySchema.Components[f.From[i] + 3];
                    Problem("Error", "tblFrames", row, f.Id + "." + LibrarySchema.Components[i + 3] + " must be " + want + " to follow " + LibrarySchema.Components[i] + " = " +
                        (f.Sign[i] > 0 ? "+" : "-") + LibrarySchema.Components[f.From[i]] + (det < 0 ? " (a mirror negates the moments)" : ""));
                    return null;
                }
            }
            return f;
        }
    }

    // the binding grammar (the resolution itself is in Plan.cs)
    public static class Bindings
    {
        public static readonly string[] Functions = { "prop", "len", "angle", "section", "family", "beta" };
        public static readonly string[] SlotFields = { "Member", "End", "Node", "Section", "Family", "Role", "Released", "Kind", "Beta", "Length" };
        public static readonly string[] NodeFields = { "Id", "X", "Y", "Z", "Support" };
        static readonly Regex Fn = new Regex(@"^\s*([A-Za-z]+)\s*\(([^)]*)\)\s*$");

        public static Dimension FieldDimension(string field)
        {
            switch (field.ToLowerInvariant())
            {
                case "length": case "x": case "y": case "z": return Dimension.Length;
                case "beta": return Dimension.Angle;
                case "member": case "node": case "id": return Dimension.None;
                default: return Dimension.Text;
            }
        }

        // null when the binding is well formed and fits the input's unit and PerCase; else what is wrong
        public static string Check(string binding, Unit unit, bool perCase)
        {
            if (string.IsNullOrEmpty(binding)) return "no Binding yet";
            int c = binding.IndexOf(':');
            if (c <= 0) return "Binding '" + binding + "' is not kind:argument";
            string kind = binding.Substring(0, c).Trim().ToLowerInvariant(), arg = binding.Substring(c + 1).Trim();
            switch (kind)
            {
                case "const":
                case "param":
                case "reg":
                    if (arg.Length == 0 && kind != "const") return kind + ": needs a name";
                    if (perCase) return "a " + kind + ": binding is the same for every case: set PerCase = N";
                    return null;
                case "load":
                    int k = Array.FindIndex(LibrarySchema.Components, x => string.Equals(x, arg, StringComparison.OrdinalIgnoreCase));
                    if (k < 0) return "load: takes Fx, Fy, Fz, Mx, My or Mz";
                    if (!perCase) return "a load: binding changes with the case: set PerCase = Y";
                    if (unit == null) return "a load needs its Unit (the template's force or moment unit)";
                    Dimension want = k < 3 ? Dimension.Force : Dimension.Moment;
                    if (unit.Dim != want) return "load:" + arg + " is a " + want.ToString().ToLowerInvariant() + ", the cell's unit " + unit + " is not";
                    return null;
                case "case":
                    if (arg != "id" && arg != "title" && arg != "kind") return "case: takes id, title or kind";
                    if (!perCase) return "a case: binding changes with the case: set PerCase = Y";
                    return null;
                case "pkg":
                    if (perCase) return "a pkg: binding is the same for every case: set PerCase = N";
                    int dot = arg.IndexOf('.');
                    if (dot <= 0) return "pkg: takes slot.field (e.g. pkg:this.Section)";
                    string slot = arg.Substring(0, dot), field = arg.Substring(dot + 1);
                    string[] fields = slot == "node" ? NodeFields : SlotFields;
                    if (!fields.Any(f => string.Equals(f, field, StringComparison.OrdinalIgnoreCase))) return "pkg:" + slot + " has no field '" + field + "' (" + string.Join(", ", fields) + ")";
                    return DimOk(FieldDimension(field), unit, binding);
                case "fn":
                    if (perCase) return "an fn: binding is the same for every case: set PerCase = N";
                    Match m = Fn.Match(arg);
                    if (!m.Success) return "fn: takes name(args), e.g. fn:prop(this,d)";
                    string name = m.Groups[1].Value.ToLowerInvariant();
                    string[] args = m.Groups[2].Value.Split(',').Select(a => a.Trim()).Where(a => a.Length > 0).ToArray();
                    if (!Functions.Contains(name)) return "no function '" + name + "' (" + string.Join(", ", Functions) + ")";
                    int need = name == "prop" || name == "angle" ? 2 : 1;
                    if (args.Length != need) return "fn:" + name + " takes " + need + " argument" + (need > 1 ? "s" : "");
                    if (name == "len") return DimOk(Dimension.Length, unit, binding);
                    if (name == "angle" || name == "beta") return DimOk(Dimension.Angle, unit, binding);
                    if (name == "section" || name == "family") return DimOk(Dimension.Text, unit, binding);
                    return null;   // prop: its unit comes from tblSections
                default:
                    return "unknown binding kind '" + kind + "' (const, param, reg, pkg, load, case, fn)";
            }
        }

        static string DimOk(Dimension have, Unit unit, string binding)
        {
            if (have == Dimension.Text || have == Dimension.None) return unit != null && unit.Dim != Dimension.Text && unit.Dim != Dimension.None ? binding + " is text, the cell's unit " + unit + " is not" : null;
            if (unit == null) return binding + " is a " + have.ToString().ToLowerInvariant() + ": give the cell's Unit";
            return unit.Dim == have ? null : binding + " is a " + have.ToString().ToLowerInvariant() + ", the cell's unit " + unit + " is not";
        }
    }

    // a sheet holding one Excel table: a note row, a header row, the rows (all written as text). Headers of the columns
    // a person fills in are blue; the columns the tool writes or harvest notes are grey.
    public static class WorkbookTables
    {
        static readonly HashSet<string> InfoColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Example", "Kind?", "List", "Description", "Found", "Formula" };

        public static XOutSheet Sheet(string sheetName, string tableName, string[] columns, List<Dictionary<string, string>> rows, string note)
        {
            return Sheet(sheetName, tableName, columns, rows, note, c => !InfoColumns.Contains(c));
        }

        public static XOutSheet Sheet(string sheetName, string tableName, string[] columns, List<Dictionary<string, string>> rows, string note, Func<string, bool> isInput)
        {
            var s = new XOutSheet(sheetName);
            var noteStyle = new XStyle(); noteStyle.Italic = true; noteStyle.Color = "FF595959";
            s.Add(new XCell(note ?? "", noteStyle));
            var cols = new List<string>(columns);
            foreach (var r in rows)
                foreach (string k in r.Keys)
                    if (k != "__row" && !cols.Any(c => string.Equals(c, k, StringComparison.OrdinalIgnoreCase))) cols.Add(k);
            var blue = new XStyle(); blue.Bold = true; blue.Fill = "FFDDEBF7"; blue.Border = true;
            var grey = new XStyle(); grey.Bold = true; grey.Fill = "FFE7E6E6"; grey.Border = true;
            s.Add(cols.Select(c => (object)new XCell(c, isInput(c) ? blue : grey)).ToArray());
            foreach (var r in rows)
            {
                var cells = new object[cols.Count];
                for (int i = 0; i < cols.Count; i++) { string v; cells[i] = r.TryGetValue(cols[i], out v) ? (v ?? "") : ""; }
                s.Add(cells);
            }
            var t = new XOutTable();
            t.Name = tableName; t.Top = 1; t.Left = 0; t.Right = cols.Count - 1; t.Bottom = 1 + Math.Max(rows.Count, 1);
            s.Tables.Add(t);
            s.FreezeRows = 2;
            for (int i = 0; i < cols.Count; i++) s.Width(i, Math.Min(48, Math.Max(10, cols[i].Length + 4)));
            return s;
        }
    }
}
