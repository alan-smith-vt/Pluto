// Run.cs -- the calc stage: the plan's ready calcs calculated by an engine, their results read back and kept.
//   PlanManifest   plan-manifest.txt, written with the plan: the package, library text and register it was made from
//                  (paths and SHA-256s). A run refuses a plan whose sources have changed since ("plan again").
//   CalcJob        one (calc, step): the template copy's cells to write for every evaluation (a single_case template
//                  takes one demand row per evaluation, a case_table one block of rows, MaxCases at a time) and the
//                  outputs to read back. Its input hash comes from the plan rows alone, so the summary can tell a
//                  current result from a stale one without opening a template.
//   CalcRunner     runs due jobs in batches through an engine (Engines.cs):
//                    every case valid: health outputs equal their Expect, DCRs are numbers, statuses are not blank
//                    per check: the largest DCR and the case, row and connection it came from (a status output that
//                    is not OK-like marks its check NG)
//                    records: the calculated copy the governing case was read from, one per check (checks with the
//                    same governing case share it), in records\<rev>\<calc>\<step>-<template>-<checks>.xlsx with a
//                    .calcflow.json stamp beside it (engine and version, template SHA, case, DCRs, rev, input hash)
//                    a job whose inputs have not changed since the last revision carries its record forward instead
//                    of calculating again
//                  data\results\<calc>__s<step>.csv (every case's outputs and validity), ...checks.csv (per check and
//                  connection), data\calc-state.csv (one row per job); each job is saved as it finishes, so a run
//                  that is stopped keeps every job it finished.
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
    // ===================================================================================== the plan's sources

    public static class PlanManifest
    {
        public const string FileName = "plan-manifest.txt";

        // the plan folder's manifest: where each source is and its SHA-256 now
        public static void Write(string planDir, AnalysisPackage pkg, Library lib, Register reg, string templatesDir)
        {
            var sb = new StringBuilder();
            sb.Append("# CalcFlow plan manifest: the sources this plan was made from (a run refuses the plan when one changes)\r\n");
            foreach (string[] kv in Sources(pkg.Folder, lib.Folder, reg.Path, templatesDir)) sb.Append(kv[0]).Append('\t').Append(kv[1]).Append("\r\n");
            File.WriteAllText(Path.Combine(planDir, FileName), sb.ToString(), new UTF8Encoding(false));
        }

        public static Dictionary<string, string> Read(string planDir)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string p = Path.Combine(planDir, FileName);
            if (!File.Exists(p)) return d;
            foreach (string line in File.ReadAllLines(p))
            {
                if (line.StartsWith("#", StringComparison.Ordinal)) continue;
                int t = line.IndexOf('\t');
                if (t > 0) d[line.Substring(0, t)] = line.Substring(t + 1);
            }
            return d;
        }

        // what changed since the plan was made (empty: the plan is current)
        public static List<string> Stale(string planDir)
        {
            var why = new List<string>();
            Dictionary<string, string> m = Read(planDir);
            if (m.Count == 0) { why.Add("the plan folder has no " + FileName + " (made before CalcFlow ran calcs): run plan again"); return why; }
            string pkg = Get(m, "package"), lib = Get(m, "library"), reg = Get(m, "register"), tpl = Get(m, "templates");
            foreach (string[] kv in Sources(pkg, lib, reg, tpl))
            {
                string was = Get(m, kv[0]);
                if (!string.Equals(was, kv[1], StringComparison.OrdinalIgnoreCase)) why.Add(Describe(kv[0], m) + " changed since the plan was made");
            }
            return why;
        }

        static string Describe(string key, Dictionary<string, string> m)
        {
            switch (key)
            {
                case "package.sha256": return "the analysis package (" + Get(m, "package") + ")";
                case "library.sha256": return "the calc library's text (" + Get(m, "library") + ")";
                case "register.sha256": return "the register (" + Get(m, "register") + ")";
                default: return key;
            }
        }

        static string Get(Dictionary<string, string> m, string k) { string v; return m.TryGetValue(k, out v) ? v : ""; }

        static List<string[]> Sources(string pkgFolder, string libFolder, string regPath, string templatesDir)
        {
            var l = new List<string[]>();
            l.Add(new[] { "package", pkgFolder });
            l.Add(new[] { "package.sha256", FolderSha(pkgFolder, "*.*") });
            l.Add(new[] { "library", libFolder });
            l.Add(new[] { "library.sha256", FolderSha(Path.Combine(libFolder, Library.TextFolder), "*.csv") });
            l.Add(new[] { "register", regPath });
            l.Add(new[] { "register.sha256", File.Exists(regPath) ? SharedFile.Sha256(regPath) : "missing" });
            l.Add(new[] { "templates", templatesDir });
            return l;
        }

        // the files of a folder (not its subfolders) as one hash of their names and contents
        public static string FolderSha(string dir, string pattern)
        {
            if (!Directory.Exists(dir)) return "missing";
            var sb = new StringBuilder();
            foreach (string f in Directory.GetFiles(dir, pattern).OrderBy(x => Path.GetFileName(x), StringComparer.Ordinal))
                sb.Append(Path.GetFileName(f)).Append(' ').Append(SharedFile.Sha256(f)).Append('\n');
            return SharedFile.Sha256Text(sb.ToString());
        }
    }

    // ===================================================================================== the plan, read back

    public sealed class PlanSet
    {
        public string Folder = "";
        public readonly List<Dictionary<string, string>> Calcs, Cells, Cases, Demands, Problems;
        public readonly Dictionary<string, string> Manifest;

        PlanSet(string dir)
        {
            Folder = Path.GetFullPath(dir);
            Calcs = Csv.ReadTable(Path.Combine(Folder, "calcs.csv"));
            Cells = Csv.ReadTable(Path.Combine(Folder, "plan.csv"));
            Cases = Csv.ReadTable(Path.Combine(Folder, "cases.csv"));
            Demands = Csv.ReadTable(Path.Combine(Folder, "demands.csv"));
            Problems = Csv.ReadTable(Path.Combine(Folder, "problems.csv"));
            Manifest = PlanManifest.Read(Folder);
        }

        public static PlanSet Load(string dir)
        {
            if (!File.Exists(Path.Combine(dir, "calcs.csv"))) throw new FileNotFoundException("Not a plan folder (no calcs.csv): " + dir);
            return new PlanSet(dir);
        }

        public string Source(string key) { string v; return Manifest.TryGetValue(key, out v) ? v : ""; }

        // the package folder's name: R03 for <project dir>\calcflow\package\MODEL-A\R03
        public string DefaultRev
        {
            get
            {
                string p = Source("package").TrimEnd('\\', '/');
                return p.Length == 0 ? "current" : Path.GetFileName(p);
            }
        }

        public static string V(Dictionary<string, string> d, string k) { string v; return d.TryGetValue(k, out v) ? (v ?? "") : ""; }
        public static int I(Dictionary<string, string> d, string k) { int n; return int.TryParse(V(d, k), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : 0; }

        // the canonical text of everything a (calc, step) consumes: the template as pinned, the outputs read back, and
        // the plan's rows for it. Its SHA-256 is the job's input hash.
        public string Canonical(Library lib, string calc, int step, string template)
        {
            var sb = new StringBuilder();
            LibTemplate t;
            sb.Append("template ").Append(template);
            if (lib.Templates.TryGetValue(template, out t))
            {
                sb.Append(' ').Append(t.File).Append(' ').Append(t.Sha256).Append(' ').Append(t.Shape).Append(' ').Append(t.MaxCases).Append('\n');
                foreach (LibOutput o in t.Outputs)
                    sb.Append("out ").Append(o.Sheet).Append('!').Append(o.Cell).Append(' ').Append(o.Kind).Append(' ').Append(o.Check).Append(' ').Append(o.Expect).Append(' ').Append(o.PerCase ? "Y" : "N").Append('\n');
            }
            else sb.Append(" (not in the library)\n");
            foreach (var c in Cells.Where(x => V(x, "Calc") == calc && I(x, "Step") == step))
                sb.Append("cell ").Append(V(c, "Sheet")).Append('!').Append(V(c, "Cell")).Append('=').Append(V(c, "Value")).Append(' ').Append(V(c, "Unit")).Append('\n');
            foreach (var c in Cases.Where(x => V(x, "Calc") == calc && I(x, "Step") == step))
                sb.Append("case ").Append(V(c, "Row")).Append(' ').Append(V(c, "LC")).Append(' ').Append(V(c, "Connection")).Append(' ').Append(V(c, "Sheet")).Append('!').Append(V(c, "Cell")).Append('=').Append(V(c, "Value")).Append('\n');
            foreach (var d in Demands.Where(x => V(x, "Calc") == calc && I(x, "Step") == step))
                sb.Append("demand ").Append(V(d, "Row")).Append(' ').Append(V(d, "LC")).Append(' ').Append(V(d, "Connection")).Append('\n');
            return sb.ToString();
        }

        public string InputHash(Library lib, string calc, int step, string template) { return SharedFile.Sha256Text(Canonical(lib, calc, step, template)); }

        // (step, template) of a calc's type, in order
        public static List<LibStep> Steps(Library lib, string type)
        {
            LibType t;
            return lib.Types.TryGetValue(type, out t) ? t.Steps.OrderBy(s => s.Step).ToList() : new List<LibStep>();
        }
    }

    // ===================================================================================== jobs

    public sealed class OutputRead
    {
        public LibOutput Out;
        public string Label = "", Sheet = "";
        public int Row, Col;           // in the template (0-based); a per-case output of a case table moves down a row per case
    }

    public sealed class CaseRow
    {
        public int Row;                // the demand row (1-based, as in demands.csv)
        public string LC = "", Connection = "";
    }

    public sealed class CalcUnit
    {
        public string File = "";       // name of the copy in the run's folder
        public string OutPath = "";    // its calculated copy, until the job is finished
        public readonly List<CellWrite> Writes = new List<CellWrite>();
        public readonly List<CaseRow> Rows = new List<CaseRow>();
        public CalcJob Job;
    }

    public sealed class CalcJob
    {
        public string Calc = "", Type = "", Group = "", Template = "", TemplatePath = "", TemplateSha = "", Shape = "", InputHash = "";
        public int Step;
        public readonly List<string> Connections = new List<string>();
        public readonly List<CalcUnit> Units = new List<CalcUnit>();
        public readonly List<OutputRead> Reads = new List<OutputRead>();
        public string Problem = "";    // why it cannot run ("" when it can)
        public string Due = "";        // why it runs now ("" when it is up to date)
        public string Key { get { return Calc + "|" + Step.ToString(CultureInfo.InvariantCulture); } }

        // every ready (calc, step) of the plan; templatesDir holds the library's template files
        public static List<CalcJob> FromPlan(PlanSet plan, Library lib, string templatesDir)
        {
            var jobs = new List<CalcJob>();
            var books = new Dictionary<string, TemplateBook>(StringComparer.OrdinalIgnoreCase);
            var cellsBy = plan.Cells.ToLookup(x => PlanSet.V(x, "Calc") + "|" + PlanSet.V(x, "Step"));
            var casesBy = plan.Cases.ToLookup(x => PlanSet.V(x, "Calc") + "|" + PlanSet.V(x, "Step"));
            var demandsBy = plan.Demands.ToLookup(x => PlanSet.V(x, "Calc") + "|" + PlanSet.V(x, "Step"));
            foreach (var c in plan.Calcs)
            {
                if (PlanSet.V(c, "Status") != "Ready") continue;
                string calc = PlanSet.V(c, "Calc"), type = PlanSet.V(c, "Type");
                foreach (LibStep s in PlanSet.Steps(lib, type))
                {
                    var j = new CalcJob();
                    j.Calc = calc; j.Type = type; j.Group = PlanSet.V(c, "Group"); j.Step = s.Step; j.Template = s.Template;
                    j.Connections.AddRange(PlanSet.V(c, "Connections").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
                    j.InputHash = plan.InputHash(lib, calc, s.Step, s.Template);
                    jobs.Add(j);
                    LibTemplate tpl;
                    if (!lib.Templates.TryGetValue(s.Template, out tpl)) { j.Problem = "template " + s.Template + " is not in the library"; continue; }
                    j.Shape = tpl.Shape; j.TemplateSha = tpl.Sha256;
                    j.TemplatePath = Path.Combine(templatesDir, tpl.File);
                    if (!File.Exists(j.TemplatePath)) { j.Problem = "the template file is missing: " + j.TemplatePath; continue; }
                    TemplateBook book;
                    if (!books.TryGetValue(j.TemplatePath, out book)) { book = TemplateBook.Open(j.TemplatePath); books[j.TemplatePath] = book; }
                    if (tpl.Sha256.Length > 0 && !string.Equals(book.Sha256, tpl.Sha256, StringComparison.OrdinalIgnoreCase)) { j.Problem = "the template file differs from the one the library pinned (" + j.TemplatePath + ")"; continue; }
                    try { j.Build(tpl, book, cellsBy[j.Key].ToList(), casesBy[j.Key].ToList(), demandsBy[j.Key].ToList()); }
                    catch (Exception ex) { j.Problem = ex.Message; j.Units.Clear(); j.Reads.Clear(); }
                }
            }
            return jobs;
        }

        void Build(LibTemplate tpl, TemplateBook book, List<Dictionary<string, string>> cells, List<Dictionary<string, string>> cases, List<Dictionary<string, string>> demands)
        {
            var once = new List<CellWrite>();
            foreach (var c in cells) once.Add(Write(book, PlanSet.V(c, "Sheet"), PlanSet.V(c, "Cell"), PlanSet.V(c, "Value"), 0));
            var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (LibOutput o in tpl.Outputs)
            {
                TSheet s; int r, col; string err;
                if (!book.Locate(o.Sheet, o.Cell, out s, out r, out col, out err)) throw new Exception("output " + o.Where + ": " + err);
                var rd = new OutputRead();
                rd.Out = o; rd.Sheet = s.Name; rd.Row = r; rd.Col = col;
                string label = o.Output.Length > 0 ? o.Output : o.Cell;
                if (!labels.Add(label)) { label = label + " (" + o.Sheet + "!" + o.Cell + ")"; labels.Add(label); }
                rd.Label = label;
                Reads.Add(rd);
            }
            var rows = demands.OrderBy(d => PlanSet.I(d, "Row")).ToList();
            if (rows.Count == 0) throw new Exception("the plan has no demand rows for it");
            var caseCells = cases.ToLookup(x => PlanSet.I(x, "Row"));
            int per = tpl.Shape == "case_table" ? (tpl.MaxCases > 0 ? tpl.MaxCases : rows.Count) : 1;
            for (int k = 0; k < rows.Count; k += per)
            {
                var u = new CalcUnit();
                u.Job = this;
                u.Writes.AddRange(once);
                for (int i = k; i < Math.Min(rows.Count, k + per); i++)
                {
                    int row = PlanSet.I(rows[i], "Row");
                    var cr = new CaseRow();
                    cr.Row = row; cr.LC = PlanSet.V(rows[i], "LC"); cr.Connection = PlanSet.V(rows[i], "Connection");
                    u.Rows.Add(cr);
                    foreach (var c in caseCells[row]) u.Writes.Add(Write(book, PlanSet.V(c, "Sheet"), PlanSet.V(c, "Cell"), PlanSet.V(c, "Value"), i - k));
                }
                Units.Add(u);
            }
        }

        // a plan value as the template's cell holds it: text where the template has text, else a number when it reads
        // as one; blank stays blank. shift: rows down (a case table's later rows)
        static CellWrite Write(TemplateBook book, string sheet, string cell, string value, int shift)
        {
            TSheet s; int r, c; string err;
            if (!book.Locate(sheet, cell, out s, out r, out c, out err)) throw new Exception("input " + sheet + "!" + cell + ": " + err);
            r += shift;
            TCell t = s.Get(r, c);
            object v = value;
            bool text = t != null && (t.Type == "s" || t.Type == "str" || t.Type == "inlineStr");
            double d;
            if (value.Length == 0) v = null;
            else if (!text && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) v = d;
            else if (t != null && t.Type == "b" && (value.Equals("TRUE", StringComparison.OrdinalIgnoreCase) || value.Equals("FALSE", StringComparison.OrdinalIgnoreCase))) v = value.Equals("TRUE", StringComparison.OrdinalIgnoreCase);
            return new CellWrite(s.Name, Xlsx.Ref(r, c), v);
        }

        public int CaseCount { get { return Units.Sum(u => u.Rows.Count); } }
    }

    // ===================================================================================== results and state

    public sealed class CaseResult
    {
        public CaseRow Case;
        public CalcUnit Unit;
        public string[] Values;        // one per OutputRead (as the cell shows it)
        public bool Ran, Valid;
        public string Why = "";
    }

    public sealed class CheckResult
    {
        public string Check = "", Connection = "";     // Connection "" = the whole calc
        public double Max = double.NaN;
        public string LC = "", Status = "";             // Status: OK-like text, NG, or "" when the check has no status output
        public int Row, Valid, Invalid;
        public string Record = "";                      // relative to the work folder
    }

    public sealed class CalcState
    {
        public string Calc = "", Template = "", Rev = "", Engine = "", EngineVersion = "", InputHash = "", TemplateSha = "";
        public string LastRun = "", Result = "", Records = "", Message = "", CarriedFrom = "";
        public int Step, Cases, Invalid, Failed;
        public double Seconds;
        public string Key { get { return Calc + "|" + Step.ToString(CultureInfo.InvariantCulture); } }
    }

    // data\calc-state.csv: the last run of each (calc, step)
    public sealed class CalcStateStore
    {
        public static readonly string[] Header = { "Calc", "Step", "Template", "Rev", "Engine", "Engine Version", "Input Hash", "Template Sha256", "Last Run", "Result", "Cases", "Invalid", "Failed", "Seconds", "Records", "Carried From", "Message" };
        public readonly Dictionary<string, CalcState> ByKey = new Dictionary<string, CalcState>(StringComparer.OrdinalIgnoreCase);

        public static CalcStateStore Load(string path)
        {
            var s = new CalcStateStore();
            foreach (var d in Csv.ReadTable(path))
            {
                var c = new CalcState();
                c.Calc = PlanSet.V(d, "Calc"); c.Step = PlanSet.I(d, "Step"); c.Template = PlanSet.V(d, "Template"); c.Rev = PlanSet.V(d, "Rev");
                c.Engine = PlanSet.V(d, "Engine"); c.EngineVersion = PlanSet.V(d, "Engine Version"); c.InputHash = PlanSet.V(d, "Input Hash");
                c.TemplateSha = PlanSet.V(d, "Template Sha256"); c.LastRun = PlanSet.V(d, "Last Run"); c.Result = PlanSet.V(d, "Result");
                c.Cases = PlanSet.I(d, "Cases"); c.Invalid = PlanSet.I(d, "Invalid"); c.Failed = PlanSet.I(d, "Failed");
                double sec = Csv.ParseNum(PlanSet.V(d, "Seconds")); c.Seconds = double.IsNaN(sec) ? 0 : sec;
                c.Records = PlanSet.V(d, "Records"); c.CarriedFrom = PlanSet.V(d, "Carried From"); c.Message = PlanSet.V(d, "Message");
                if (c.Calc.Length > 0) s.ByKey[c.Key] = c;
            }
            return s;
        }

        public CalcState Get(string calc, int step)
        {
            CalcState c;
            return ByKey.TryGetValue(calc + "|" + step.ToString(CultureInfo.InvariantCulture), out c) ? c : null;
        }

        public void Save(string path)
        {
            Csv.Write(path, Header, ByKey.Values.OrderBy(c => c.Calc, NaturalComparer.Instance).ThenBy(c => c.Step)
                .Select(c => new object[] { c.Calc, c.Step, c.Template, c.Rev, c.Engine, c.EngineVersion, c.InputHash, c.TemplateSha, c.LastRun, c.Result,
                    c.Cases, c.Invalid, c.Failed, Math.Round(c.Seconds, 1), c.Records, c.CarriedFrom, c.Message }));
        }
    }

    public sealed class RunSummary
    {
        public int Jobs, Ok, Invalid, Failed, Carried, Cases, Evaluations;
        public double Seconds;
        public readonly List<string> Messages = new List<string>();
    }

    // ===================================================================================== the runner

    public sealed class CalcRunner
    {
        public static readonly string[] OkStatus = { "OK", "PASS", "PASSED", "Y", "YES", "TRUE", "ADEQUATE", "ACCEPTABLE", "SATISFACTORY" };

        public readonly string Work, Rev, Package;
        public string StatePath { get { return Path.Combine(Path.Combine(Work, "data"), "calc-state.csv"); } }
        public string ResultsDir { get { return Path.Combine(Path.Combine(Work, "data"), "results"); } }
        public bool KeepRunFolder;
        public Action<string> Log = delegate { };

        public CalcRunner(string workDir, string rev, string package)
        {
            Work = Path.GetFullPath(workDir); Rev = rev; Package = package ?? "";
        }

        // marks each job's Due ("" = up to date). all: every job; calcs: only these calc ids (empty = every calc)
        public List<CalcJob> Due(IList<CalcJob> jobs, bool all, ICollection<string> calcs)
        {
            CalcStateStore st = CalcStateStore.Load(StatePath);
            var due = new List<CalcJob>();
            foreach (CalcJob j in jobs)
            {
                j.Due = "";
                if (calcs != null && calcs.Count > 0 && !calcs.Contains(j.Calc, StringComparer.OrdinalIgnoreCase)) continue;
                CalcState s = st.Get(j.Calc, j.Step);
                if (all) j.Due = "asked for (-All)";
                else if (s == null) j.Due = "never run";
                else if (s.Result == "FAILED") j.Due = "failed last time";
                else if (!string.Equals(s.InputHash, j.InputHash, StringComparison.OrdinalIgnoreCase)) j.Due = "inputs changed";
                else if (!string.Equals(s.Rev, Rev, StringComparison.OrdinalIgnoreCase)) j.Due = "new revision " + Rev + " (carried from " + s.Rev + ")";
                else if (RecordsMissing(s)) j.Due = "a record is missing";
                if (j.Due.Length > 0) due.Add(j);
            }
            return due;
        }

        bool RecordsMissing(CalcState s)
        {
            foreach (string r in s.Records.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries))
                if (!File.Exists(Path.Combine(Work, r))) return true;
            return false;
        }

        // runs the jobs (those with a Problem are recorded as FAILED with it); every job is saved as it finishes
        public RunSummary Run(ICalcEngine engine, IList<CalcJob> jobs)
        {
            var sum = new RunSummary();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            CalcStateStore st = CalcStateStore.Load(StatePath);
            string runDir = Path.Combine(Path.Combine(Work, "run"), DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
            string inDir = Path.Combine(runDir, "in");
            Directory.CreateDirectory(inDir);
            var pending = new List<CalcUnit>();
            var results = new Dictionary<CalcJob, List<CaseResult>>();
            var left = new Dictionary<CalcJob, int>();
            var started = new Dictionary<CalcJob, DateTime>();
            int n = 0;
            foreach (CalcJob j in jobs)
            {
                sum.Jobs++;
                if (j.Problem.Length > 0)
                {
                    Save(st, j, engine, new List<CaseResult>(), "FAILED", j.Problem, 0, null);
                    sum.Failed++; sum.Messages.Add(j.Calc + " step " + j.Step + ": " + j.Problem);
                    continue;
                }
                CalcState prev = st.Get(j.Calc, j.Step);
                if (prev != null && prev.Result != "FAILED" && j.Due.StartsWith("new revision", StringComparison.Ordinal) && Carry(j, prev, st))
                {
                    sum.Carried++;
                    Log("carried  " + j.Calc + " step " + j.Step + " (" + j.Template + "): inputs unchanged since " + prev.Rev);
                    continue;
                }
                results[j] = new List<CaseResult>();
                left[j] = j.Units.Count;
                foreach (CalcUnit u in j.Units)
                {
                    n++;
                    u.File = "u" + n.ToString("D6", CultureInfo.InvariantCulture) + Path.GetExtension(j.TemplatePath).ToLowerInvariant();
                    pending.Add(u);
                }
            }
            int batchSize = Math.Max(1, engine.BatchSize), b = 0;
            for (int k = 0; k < pending.Count; k += batchSize)
            {
                b++;
                List<CalcUnit> batch = pending.Skip(k).Take(batchSize).ToList();
                string outDir = Path.Combine(runDir, "out" + b.ToString(CultureInfo.InvariantCulture));
                var files = new List<string>();
                foreach (CalcUnit u in batch)
                {
                    if (!started.ContainsKey(u.Job)) started[u.Job] = DateTime.UtcNow;
                    string f = Path.Combine(inDir, u.File);
                    XlsxPatch.Write(u.Job.TemplatePath, f, u.Writes);
                    files.Add(f);
                }
                string batchError = "";
                Log(string.Format(CultureInfo.InvariantCulture, "batch {0}: {1} workbook(s) by {2}", b, batch.Count, engine.Name));
                try { engine.Recalculate(files, outDir); }
                catch (Exception ex) { batchError = ex.Message; sum.Messages.Add("batch " + b + ": " + ex.Message); }
                sum.Evaluations += batch.Count;
                foreach (CalcUnit u in batch)
                {
                    string outFile = Path.Combine(outDir, u.File);
                    results[u.Job].AddRange(Read(u, outFile, batchError));
                    left[u.Job]--;
                    if (left[u.Job] == 0)
                    {
                        double secs = (DateTime.UtcNow - started[u.Job]).TotalSeconds;
                        string res = Finish(st, u.Job, engine, results[u.Job], secs);
                        if (res == "OK") sum.Ok++; else if (res == "INVALID") sum.Invalid++; else sum.Failed++;
                        sum.Cases += u.Job.CaseCount;
                        results.Remove(u.Job);
                    }
                }
                foreach (string f in files) TryDelete(f);
            }
            if (!KeepRunFolder) TryDeleteDir(runDir);
            sum.Seconds = clock.Elapsed.TotalSeconds;
            return sum;
        }

        // the outputs of one evaluation, per case row
        List<CaseResult> Read(CalcUnit u, string outFile, string batchError)
        {
            var list = new List<CaseResult>();
            TemplateBook book = null;
            string why = "";
            if (!File.Exists(outFile)) why = batchError.Length > 0 ? batchError : "the engine did not calculate it";
            else
            {
                u.OutPath = outFile;
                try { book = TemplateBook.Open(outFile); }
                catch (Exception ex) { why = "its calculated copy cannot be read: " + ex.Message; }
            }
            for (int i = 0; i < u.Rows.Count; i++)
            {
                var cr = new CaseResult();
                cr.Case = u.Rows[i]; cr.Unit = u;
                cr.Values = new string[u.Job.Reads.Count];
                if (book == null) { cr.Ran = false; cr.Why = why; list.Add(cr); continue; }
                cr.Ran = true;
                var bad = new List<string>();
                for (int k = 0; k < u.Job.Reads.Count; k++)
                {
                    OutputRead rd = u.Job.Reads[k];
                    TSheet s = book.Sheet(rd.Sheet);
                    TCell c = s == null ? null : s.Get(rd.Row + (rd.Out.PerCase ? i : 0), rd.Col);
                    string v = c == null ? "" : c.Value;
                    cr.Values[k] = v;
                    string kind = rd.Out.Kind;
                    bool isError = c != null && c.Type == "e";
                    if (kind == "dcr")
                    {
                        double d = Csv.ParseNum(v);
                        if (isError || double.IsNaN(d) || double.IsInfinity(d)) bad.Add(rd.Label + " is " + (v.Length == 0 ? "empty" : v));
                    }
                    else if (kind == "health")
                    {
                        if (!SameValue(v, rd.Out.Expect)) bad.Add(rd.Label + " is " + (v.Length == 0 ? "empty" : v) + ", not " + rd.Out.Expect);
                    }
                    else if (kind == "status")
                    {
                        if (isError || v.Trim().Length == 0) bad.Add(rd.Label + " is " + (v.Length == 0 ? "empty" : v));
                    }
                }
                cr.Valid = bad.Count == 0;
                cr.Why = string.Join("; ", bad.ToArray());
                list.Add(cr);
            }
            return list;
        }

        // TRUE / 1 / Y and the like compare as booleans, numbers as numbers, else text without case
        public static bool SameValue(string actual, string expect)
        {
            string a = (actual ?? "").Trim(), e = (expect ?? "").Trim();
            int ba = Bool(a), be = Bool(e);
            // LibreOffice saves a formula's TRUE / FALSE as 1 / 0: against a boolean, 1 and 0 are booleans
            if (be >= 0 && ba < 0) ba = NumBool(a);
            if (ba >= 0 && be < 0) be = NumBool(e);
            if (ba >= 0 && be >= 0) return ba == be;
            double x = Csv.ParseNum(a), y = Csv.ParseNum(e);
            if (!double.IsNaN(x) && !double.IsNaN(y)) return Math.Abs(x - y) <= 1e-9 * Math.Max(1, Math.Abs(y));
            return string.Equals(a, e, StringComparison.OrdinalIgnoreCase);
        }

        static int NumBool(string s)
        {
            double x = Csv.ParseNum(s);
            return x == 1 ? 1 : x == 0 ? 0 : -1;
        }

        static int Bool(string s)
        {
            string u = s.ToUpperInvariant();
            if (u == "TRUE" || u == "Y" || u == "YES") return 1;
            if (u == "FALSE" || u == "N" || u == "NO") return 0;
            return -1;
        }

        static bool OkLike(string s) { return OkStatus.Contains((s ?? "").Trim().ToUpperInvariant()); }

        // per check and connection: the governing case; records; the results files; the state row. Returns the result.
        string Finish(CalcStateStore st, CalcJob j, ICalcEngine engine, List<CaseResult> cases, double secs)
        {
            var checks = Checks(j, cases);
            int failed = cases.Count(c => !c.Ran), invalid = cases.Count(c => c.Ran && !c.Valid);
            string result = failed > 0 ? "FAILED" : invalid > 0 ? "INVALID" : "OK";
            string message = failed > 0 ? failed + " of " + cases.Count + " case(s) not calculated: " + cases.First(c => !c.Ran).Why
                : invalid > 0 ? invalid + " of " + cases.Count + " case(s) invalid: " + cases.First(c => c.Ran && !c.Valid).Why : "";
            // records: the governing case of each whole-calc check, one file per evaluation
            string recDir = Path.Combine(Path.Combine(Path.Combine(Work, "records"), SafeName(Rev)), SafeName(j.Calc));
            ClearRecords(recDir, j.Step);
            var records = new List<string>();
            var recordOf = new Dictionary<CalcUnit, string>();
            if (failed == 0)
            {
                foreach (var g in checks.Where(c => c.Connection.Length == 0 && c.Row > 0).GroupBy(c => UnitOf(cases, c.Row)))
                {
                    CalcUnit u = g.Key;
                    if (u == null || u.OutPath.Length == 0 || !File.Exists(u.OutPath)) continue;
                    string name = j.Step.ToString(CultureInfo.InvariantCulture) + "-" + SafeName(j.Template) + "-" + SafeName(string.Join("+", g.Select(c => c.Check).ToArray()));
                    string rec = Path.Combine(recDir, name + Path.GetExtension(j.TemplatePath).ToLowerInvariant());
                    Directory.CreateDirectory(recDir);
                    File.Copy(u.OutPath, rec, true);
                    File.WriteAllText(rec + ".calcflow.json", Stamp(j, engine, u, g.ToList(), cases), new UTF8Encoding(false));
                    recordOf[u] = Relative(rec);
                    records.Add(Relative(rec));
                }
                // an invalid step keeps its first invalid case, to be opened and seen
                CaseResult bad = cases.FirstOrDefault(c => c.Ran && !c.Valid);
                if (bad != null && !recordOf.ContainsKey(bad.Unit) && bad.Unit.OutPath.Length > 0 && File.Exists(bad.Unit.OutPath))
                {
                    string rec = Path.Combine(recDir, j.Step.ToString(CultureInfo.InvariantCulture) + "-" + SafeName(j.Template) + "-invalid" + Path.GetExtension(j.TemplatePath).ToLowerInvariant());
                    Directory.CreateDirectory(recDir);
                    File.Copy(bad.Unit.OutPath, rec, true);
                    File.WriteAllText(rec + ".calcflow.json", Stamp(j, engine, bad.Unit, new List<CheckResult>(), cases).TrimEnd().TrimEnd('}').TrimEnd() +
                        ",\n  \"invalid\": " + JsonText.Str(bad.Why) + "\n}\n", new UTF8Encoding(false));
                    records.Add(Relative(rec));
                }
                // a connection of a group points at the record that holds its own governing case, when one does
                foreach (CheckResult c in checks)
                {
                    CalcUnit u = UnitOf(cases, c.Row);
                    string r;
                    if (u != null && recordOf.TryGetValue(u, out r)) c.Record = r;
                }
            }
            foreach (CalcUnit u in j.Units) { if (u.OutPath.Length > 0) TryDelete(u.OutPath); u.OutPath = ""; }
            WriteResults(j, cases, checks);
            Save(st, j, engine, cases, result, message, secs, records);
            Log(string.Format(CultureInfo.InvariantCulture, "{0,-8} {1} step {2} ({3}): {4} case(s){5}{6}", result.ToLowerInvariant(), j.Calc, j.Step, j.Template, cases.Count,
                checks.Where(c => c.Connection.Length == 0 && !double.IsNaN(c.Max)).Select(c => ", " + c.Check + " " + Csv.Num(Math.Round(c.Max, 4)) + " (case " + c.LC + ")").DefaultIfEmpty("").Aggregate((a, x) => a + x),
                message.Length > 0 ? " -- " + message : ""));
            return result;
        }

        static CalcUnit UnitOf(List<CaseResult> cases, int row)
        {
            if (row <= 0) return null;
            CaseResult c = cases.FirstOrDefault(x => x.Case.Row == row);
            return c == null ? null : c.Unit;
        }

        // the checks of a job: each Check (the dcr outputs' Check, else their label) over the valid cases, for the whole
        // calc (Connection "") and for each connection of a group; NG when a status output of the check is not OK-like
        public static List<CheckResult> Checks(CalcJob j, List<CaseResult> cases)
        {
            var list = new List<CheckResult>();
            var names = new List<string>();
            foreach (OutputRead rd in j.Reads)
            {
                if (rd.Out.Kind != "dcr" && rd.Out.Kind != "status") continue;
                string n = CheckName(rd);
                if (!names.Contains(n)) names.Add(n);
            }
            var conns = new List<string> { "" };
            if (j.Connections.Count > 1) conns.AddRange(j.Connections);
            foreach (string conn in conns)
                foreach (string name in names)
                {
                    var c = new CheckResult();
                    c.Check = name; c.Connection = conn;
                    foreach (CaseResult cr in cases)
                    {
                        if (conn.Length > 0 && cr.Case.Connection.Length > 0 && !string.Equals(cr.Case.Connection, conn, StringComparison.OrdinalIgnoreCase)) continue;
                        if (!cr.Ran) continue;
                        if (!cr.Valid) { c.Invalid++; continue; }
                        c.Valid++;
                        for (int k = 0; k < j.Reads.Count; k++)
                        {
                            OutputRead rd = j.Reads[k];
                            if (CheckName(rd) != name) continue;
                            if (rd.Out.Kind == "dcr")
                            {
                                double d = Csv.ParseNum(cr.Values[k]);
                                if (double.IsNaN(c.Max) || d > c.Max) { c.Max = d; c.LC = cr.Case.LC; c.Row = cr.Case.Row; }
                            }
                            else if (rd.Out.Kind == "status")
                            {
                                if (!OkLike(cr.Values[k])) { if (c.Status != "NG") { c.Status = "NG"; if (double.IsNaN(c.Max)) { c.LC = cr.Case.LC; c.Row = cr.Case.Row; } } }
                                else if (c.Status.Length == 0) c.Status = cr.Values[k].Trim();
                            }
                        }
                    }
                    if (c.Row == 0 && c.Valid > 0)
                    {
                        CaseResult first = cases.First(x => x.Ran && x.Valid && (conn.Length == 0 || x.Case.Connection.Length == 0 || string.Equals(x.Case.Connection, conn, StringComparison.OrdinalIgnoreCase)));
                        c.LC = first.Case.LC; c.Row = first.Case.Row;
                    }
                    list.Add(c);
                }
            return list;
        }

        public static string CheckName(OutputRead rd) { return rd.Out.Check.Length > 0 ? rd.Out.Check : rd.Label; }

        void WriteResults(CalcJob j, List<CaseResult> cases, List<CheckResult> checks)
        {
            string stem = Path.Combine(ResultsDir, SafeName(j.Calc) + "__s" + j.Step.ToString(CultureInfo.InvariantCulture));
            var head = new List<string> { "Row", "LC", "Connection", "Calculated", "Valid", "Why" };
            head.AddRange(j.Reads.Select(r => r.Label));
            Csv.Write(stem + ".csv", head, cases.OrderBy(c => c.Case.Row).Select(c =>
            {
                var row = new List<object> { c.Case.Row, c.Case.LC, c.Case.Connection, c.Ran ? "Y" : "N", c.Ran && c.Valid ? "Y" : "N", c.Why };
                row.AddRange(c.Values.Select(v => (object)(v ?? "")));
                return row.ToArray();
            }));
            Csv.Write(stem + ".checks.csv", new[] { "Calc", "Step", "Template", "Check", "Connection", "Max DCR", "Status", "LC", "Row", "Valid Cases", "Invalid Cases", "Record" },
                checks.Select(c => new object[] { j.Calc, j.Step, j.Template, c.Check, c.Connection, double.IsNaN(c.Max) ? "" : Csv.Num(c.Max), c.Status, c.LC, c.Row == 0 ? "" : c.Row.ToString(CultureInfo.InvariantCulture), c.Valid, c.Invalid, c.Record }));
        }

        void Save(CalcStateStore st, CalcJob j, ICalcEngine engine, List<CaseResult> cases, string result, string message, double secs, List<string> records)
        {
            var s = new CalcState();
            s.Calc = j.Calc; s.Step = j.Step; s.Template = j.Template; s.Rev = Rev; s.Engine = engine.Name; s.EngineVersion = engine.Version;
            s.InputHash = j.InputHash; s.TemplateSha = j.TemplateSha; s.LastRun = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            s.Result = result; s.Cases = cases.Count; s.Invalid = cases.Count(c => c.Ran && !c.Valid); s.Failed = cases.Count(c => !c.Ran);
            s.Seconds = secs; s.Records = records == null ? "" : string.Join("|", records.ToArray()); s.Message = message;
            st.ByKey[s.Key] = s;
            st.Save(StatePath);
        }

        // inputs unchanged since an earlier revision: its records copied into this revision's folder, its state moved on
        bool Carry(CalcJob j, CalcState prev, CalcStateStore st)
        {
            var recs = prev.Records.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
            if (recs.Any(r => !File.Exists(Path.Combine(Work, r)))) return false;
            string recDir = Path.Combine(Path.Combine(Path.Combine(Work, "records"), SafeName(Rev)), SafeName(j.Calc));
            ClearRecords(recDir, j.Step);
            var moved = new List<string>();
            foreach (string r in recs)
            {
                string src = Path.Combine(Work, r), dst = Path.Combine(recDir, Path.GetFileName(r));
                Directory.CreateDirectory(recDir);
                File.Copy(src, dst, true);
                if (File.Exists(src + ".calcflow.json"))
                {
                    string json = File.ReadAllText(src + ".calcflow.json");
                    json = json.TrimEnd().TrimEnd('}').TrimEnd() + ",\n  \"carriedFrom\": " + JsonText.Str(prev.Rev) + ",\n  \"carriedTo\": " + JsonText.Str(Rev) + "\n}\n";
                    File.WriteAllText(dst + ".calcflow.json", json, new UTF8Encoding(false));
                }
                moved.Add(Relative(dst));
            }
            // the checks files name the records: point them at this revision's copies
            string checks = Path.Combine(ResultsDir, SafeName(j.Calc) + "__s" + j.Step.ToString(CultureInfo.InvariantCulture) + ".checks.csv");
            if (File.Exists(checks))
            {
                var rows = Csv.ReadAll(checks);
                int rc = Array.IndexOf(rows[0], "Record");
                for (int i = 1; i < rows.Count && rc >= 0; i++)
                    if (rc < rows[i].Length && rows[i][rc].Length > 0)
                    {
                        string name = Path.GetFileName(rows[i][rc].Replace('\\', '/'));
                        string hit = moved.FirstOrDefault(m => Path.GetFileName(m.Replace('\\', '/')) == name);
                        if (hit != null) rows[i][rc] = hit;
                    }
                Csv.Write(checks, rows[0], rows.Skip(1).Select(r => r.Cast<object>().ToArray()));
            }
            var s = new CalcState();
            s.Calc = prev.Calc; s.Step = prev.Step; s.Template = prev.Template; s.Rev = Rev; s.Engine = prev.Engine; s.EngineVersion = prev.EngineVersion;
            s.InputHash = prev.InputHash; s.TemplateSha = prev.TemplateSha; s.LastRun = prev.LastRun; s.Result = prev.Result; s.Cases = prev.Cases;
            s.Invalid = prev.Invalid; s.Failed = prev.Failed; s.Seconds = prev.Seconds; s.Records = string.Join("|", moved.ToArray());
            s.CarriedFrom = prev.CarriedFrom.Length > 0 ? prev.CarriedFrom : prev.Rev; s.Message = prev.Message;
            st.ByKey[s.Key] = s;
            st.Save(StatePath);
            return true;
        }

        static void ClearRecords(string recDir, int step)
        {
            if (!Directory.Exists(recDir)) return;
            string prefix = step.ToString(CultureInfo.InvariantCulture) + "-";
            foreach (string f in Directory.GetFiles(recDir))
                if (Path.GetFileName(f).StartsWith(prefix, StringComparison.Ordinal)) File.Delete(f);
        }

        string Stamp(CalcJob j, ICalcEngine engine, CalcUnit u, List<CheckResult> governs, List<CaseResult> cases)
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"tool\": \"CalcFlow\",\n");
            sb.Append("  \"calc\": ").Append(JsonText.Str(j.Calc)).Append(",\n");
            sb.Append("  \"type\": ").Append(JsonText.Str(j.Type)).Append(",\n");
            sb.Append("  \"step\": ").Append(j.Step.ToString(CultureInfo.InvariantCulture)).Append(",\n");
            sb.Append("  \"connections\": [").Append(string.Join(", ", j.Connections.Select(JsonText.Str).ToArray())).Append("],\n");
            sb.Append("  \"template\": ").Append(JsonText.Str(j.Template)).Append(",\n");
            sb.Append("  \"templateFile\": ").Append(JsonText.Str(Path.GetFileName(j.TemplatePath))).Append(",\n");
            sb.Append("  \"templateSha256\": ").Append(JsonText.Str(j.TemplateSha)).Append(",\n");
            sb.Append("  \"engine\": ").Append(JsonText.Str(engine.Name)).Append(",\n");
            sb.Append("  \"engineVersion\": ").Append(JsonText.Str(engine.Version)).Append(",\n");
            sb.Append("  \"rev\": ").Append(JsonText.Str(Rev)).Append(",\n");
            sb.Append("  \"package\": ").Append(JsonText.Str(Package)).Append(",\n");
            sb.Append("  \"inputHash\": ").Append(JsonText.Str(j.InputHash)).Append(",\n");
            sb.Append("  \"cases\": [").Append(string.Join(", ", u.Rows.Select(r => "{ \"row\": " + r.Row.ToString(CultureInfo.InvariantCulture) + ", \"lc\": " + JsonText.Str(r.LC) + ", \"connection\": " + JsonText.Str(r.Connection) + " }").ToArray())).Append("],\n");
            sb.Append("  \"governs\": [").Append(string.Join(", ", governs.Select(c => "{ \"check\": " + JsonText.Str(c.Check) + ", \"dcr\": " + (double.IsNaN(c.Max) ? "null" : Csv.Num(c.Max)) + ", \"status\": " + JsonText.Str(c.Status) + ", \"lc\": " + JsonText.Str(c.LC) + ", \"row\": " + c.Row.ToString(CultureInfo.InvariantCulture) + " }").ToArray())).Append("],\n");
            sb.Append("  \"casesInJob\": ").Append(cases.Count.ToString(CultureInfo.InvariantCulture)).Append(",\n");
            sb.Append("  \"written\": ").Append(JsonText.Str(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))).Append("\n");
            sb.Append("}\n");
            return sb.ToString();
        }

        string Relative(string full)
        {
            string w = Work.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            return full.StartsWith(w, StringComparison.OrdinalIgnoreCase) ? full.Substring(w.Length) : full;
        }

        public static string SafeName(string s)
        {
            string t = Regex.Replace(s ?? "", @"[\\/:*?""<>|\x00-\x1f]+", "_").Trim().TrimEnd('.');
            return t.Length == 0 ? "_" : t;
        }

        static void TryDelete(string f) { try { if (File.Exists(f)) File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        static void TryDeleteDir(string d) { try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    public static class JsonText
    {
        public static string Str(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s ?? "")
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20 || c > 0x7e) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }
    }
}
