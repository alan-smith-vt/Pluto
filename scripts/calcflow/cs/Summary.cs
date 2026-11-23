// Summary.cs -- the structure's connection DCRs in one workbook, written without Excel:
//   reports\Connection_Summary_<date>.xlsx
//     Connections  one row per member end with a Type (and every Excluded one): its calc, status, largest DCR, the
//                  check and case that govern, one column per check, its records. A group's connections each show the
//                  DCRs of their own demand rows.
//     Joints       the worst connection at each joint, and how many are OK / over the limit / invalid / not run
//     Calcs        one row per calc step: its last run (engine, rev, cases, invalid, failed), its largest DCR
//     Problems     the plan's problems and every invalid or failed step
// Status ladder (the first that applies): Excluded -> Gaps / Blocked (the plan) -> Failed -> Not run (never, inputs
// changed since, or last run on another revision) -> Invalid -> Over limit (a DCR above the register's DcrLimit, or a
// status output that is not OK-like) -> OK.
// C# 5 (Windows PowerShell 5.1 Add-Type, warnings are errors).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace CalcFlow
{
    public sealed class StepStatus
    {
        public string Calc = "", Template = "", Status = "", Why = "";
        public int Step;
        public CalcState State;
        public List<Dictionary<string, string>> Checks = new List<Dictionary<string, string>>();
    }

    public sealed class ConnectionStatus
    {
        public RegConnection Conn;
        public string Calc = "", Status = "", Why = "", GoverningCheck = "", GoverningCase = "", Records = "", Engine = "", Rev = "", LastRun = "";
        public int GoverningStep;
        public double Max = double.NaN;
        public readonly Dictionary<string, string> CheckValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class SummaryResult
    {
        public double Limit = 1.0;
        public string Rev = "";
        public readonly List<string> CheckNames = new List<string>();
        public readonly List<ConnectionStatus> Connections = new List<ConnectionStatus>();
        public readonly List<StepStatus> Steps = new List<StepStatus>();
        public readonly List<string[]> Problems = new List<string[]>();     // severity, calc, code, message

        public int Count(string status) { return Connections.Count(c => c.Status == status); }
    }

    public static class Summary
    {
        public static readonly string[] Ladder = { "Excluded", "Gaps", "Blocked", "Failed", "Not run", "Invalid", "Over limit", "OK" };
        // how much a joint's worst connection worries: lower first
        static readonly string[] Worry = { "Over limit", "Invalid", "Failed", "Gaps", "Blocked", "Not run", "OK", "Excluded" };

        public static SummaryResult Build(PlanSet plan, Library lib, Register reg, string workDir, string rev)
        {
            var res = new SummaryResult();
            res.Rev = rev;
            double lim = Csv.ParseNum(reg.Setting("DcrLimit", "1.0"));
            res.Limit = double.IsNaN(lim) ? 1.0 : lim;
            string data = Path.Combine(workDir, "data");
            CalcStateStore st = CalcStateStore.Load(Path.Combine(data, "calc-state.csv"));
            string results = Path.Combine(data, "results");

            // steps
            var calcOf = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            var stepsOf = new Dictionary<string, List<StepStatus>>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in plan.Calcs)
            {
                string calc = PlanSet.V(c, "Calc"), status = PlanSet.V(c, "Status");
                foreach (string conn in PlanSet.V(c, "Connections").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)) calcOf[conn] = c;
                var list = new List<StepStatus>();
                stepsOf[calc] = list;
                List<LibStep> libSteps = PlanSet.Steps(lib, PlanSet.V(c, "Type"));
                if (libSteps.Count == 0)
                {
                    // a type the library does not have: one row that says so
                    var gap = new StepStatus();
                    gap.Calc = calc; gap.Status = status == "Blocked" ? "Blocked" : "Gaps";
                    var p = plan.Problems.FirstOrDefault(x => PlanSet.V(x, "Calc") == calc && PlanSet.V(x, "Severity") == "Error");
                    gap.Why = p != null ? PlanSet.V(p, "Message") : "its type has no steps in the calc library";
                    list.Add(gap);
                    res.Steps.Add(gap);
                }
                foreach (LibStep s in libSteps)
                {
                    var ss = new StepStatus();
                    ss.Calc = calc; ss.Step = s.Step; ss.Template = s.Template;
                    ss.State = st.Get(calc, s.Step);
                    string checks = Path.Combine(results, CalcRunner.SafeName(calc) + "__s" + s.Step.ToString(CultureInfo.InvariantCulture) + ".checks.csv");
                    if (status == "Gaps" || status == "Blocked") { ss.Status = status; ss.Why = "the plan has " + (status == "Gaps" ? "gaps" : "a blocked template"); }
                    else if (ss.State == null) { ss.Status = "Not run"; ss.Why = "never run"; }
                    else if (ss.State.Result == "FAILED") { ss.Status = "Failed"; ss.Why = ss.State.Message; }
                    else if (!string.Equals(ss.State.InputHash, plan.InputHash(lib, calc, s.Step, s.Template), StringComparison.OrdinalIgnoreCase)) { ss.Status = "Not run"; ss.Why = "its inputs changed since it ran (" + ss.State.LastRun + ")"; }
                    else if (!string.Equals(ss.State.Rev, rev, StringComparison.OrdinalIgnoreCase)) { ss.Status = "Not run"; ss.Why = "last run on " + ss.State.Rev; }
                    else
                    {
                        ss.Checks = Csv.ReadTable(checks);
                        if (ss.State.Result == "INVALID") { ss.Status = "Invalid"; ss.Why = ss.State.Message; }
                        else ss.Status = "OK";
                    }
                    list.Add(ss);
                    res.Steps.Add(ss);
                    foreach (var k in ss.Checks)
                    {
                        string n = PlanSet.V(k, "Check");
                        if (n.Length > 0 && !res.CheckNames.Contains(n, StringComparer.OrdinalIgnoreCase)) res.CheckNames.Add(n);
                    }
                }
            }

            // connections
            foreach (RegConnection rc in reg.Connections)
            {
                bool excluded = string.Equals(rc.Disposition, "Excluded", StringComparison.OrdinalIgnoreCase);
                if (rc.Type.Length == 0 && !excluded) continue;
                var cs = new ConnectionStatus();
                cs.Conn = rc;
                res.Connections.Add(cs);
                if (excluded) { cs.Status = "Excluded"; cs.Why = rc.Comment; continue; }
                Dictionary<string, string> calc;
                if (!calcOf.TryGetValue(rc.Connection, out calc))
                {
                    cs.Status = "Gaps";
                    var p = plan.Problems.FirstOrDefault(x => PlanSet.V(x, "Calc") == rc.Connection);
                    cs.Why = p != null ? PlanSet.V(p, "Message") : "not in the plan (plan again)";
                    continue;
                }
                cs.Calc = PlanSet.V(calc, "Calc");
                bool group = PlanSet.V(calc, "Connections").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length > 1;
                List<StepStatus> steps = stepsOf[cs.Calc];
                var statuses = new List<string>();
                var why = new List<string>();
                var records = new List<string>();
                foreach (StepStatus ss in steps)
                {
                    string s = ss.Status;
                    if (s == "OK" || s == "Invalid")
                    {
                        foreach (var k in ss.Checks)
                        {
                            string kc = PlanSet.V(k, "Connection");
                            if (group ? !string.Equals(kc, rc.Connection, StringComparison.OrdinalIgnoreCase) : kc.Length > 0) continue;
                            string name = PlanSet.V(k, "Check");
                            double d = Csv.ParseNum(PlanSet.V(k, "Max DCR"));
                            bool ng = PlanSet.V(k, "Status") == "NG";
                            if (!double.IsNaN(d) && (double.IsNaN(cs.Max) || d > cs.Max))
                            {
                                cs.Max = d; cs.GoverningCheck = name; cs.GoverningCase = PlanSet.V(k, "LC"); cs.GoverningStep = ss.Step;
                            }
                            string prev;
                            double pd = cs.CheckValues.TryGetValue(name, out prev) ? Csv.ParseNum(prev) : double.NaN;
                            if (ng) cs.CheckValues[name] = double.IsNaN(d) ? "NG" : Csv.Num(d);
                            else if (!double.IsNaN(d) && (double.IsNaN(pd) || d > pd) && prev != "NG") cs.CheckValues[name] = Csv.Num(d);
                            if (s == "OK" && ((!double.IsNaN(d) && d > res.Limit) || ng)) { s = "Over limit"; }
                        }
                        // the calc's records (a group's connections share them)
                        foreach (var k in ss.Checks.Where(x => PlanSet.V(x, "Connection").Length == 0))
                        {
                            string rec = PlanSet.V(k, "Record");
                            if (rec.Length > 0 && !records.Contains(rec)) records.Add(rec);
                        }
                        if (ss.State != null) { cs.Engine = ss.State.Engine + (ss.State.EngineVersion.Length > 0 ? " " + ss.State.EngineVersion : ""); cs.Rev = ss.State.Rev; cs.LastRun = ss.State.LastRun; }
                    }
                    statuses.Add(s);
                    if (ss.Why.Length > 0) why.Add(ss.Step > 0 ? "step " + ss.Step + ": " + ss.Why : ss.Why);
                    else if (s == "Over limit") why.Add("step " + ss.Step + ": " + (cs.GoverningCheck.Length > 0 ? cs.GoverningCheck : "a check") + " above " + Csv.Num(res.Limit));
                }
                cs.Status = statuses.Count == 0 ? "Gaps" : Ladder.First(l => statuses.Contains(l) || l == "OK");
                cs.Why = string.Join("; ", why.ToArray());
                cs.Records = string.Join(" | ", records.ToArray());
            }

            // problems
            foreach (var p in plan.Problems) res.Problems.Add(new[] { PlanSet.V(p, "Severity"), PlanSet.V(p, "Calc"), PlanSet.V(p, "Code"), PlanSet.V(p, "Message") });
            foreach (StepStatus ss in res.Steps.Where(x => x.Status == "Failed" || x.Status == "Invalid"))
                res.Problems.Add(new[] { "Error", ss.Calc, ss.Status == "Failed" ? "RUN_FAILED" : "INVALID", "step " + ss.Step + " (" + ss.Template + "): " + ss.Why });
            return res;
        }

        // reports\Connection_Summary_<date>.xlsx (a second one the same day while the first is open: _2, _3 ...)
        public static string Write(SummaryResult r, string reportsDir, string planFolder)
        {
            Directory.CreateDirectory(reportsDir);
            string stem = "Connection_Summary_" + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string path = Path.Combine(reportsDir, stem + ".xlsx");
            for (int i = 2; SharedFile.IsLocked(path); i++) path = Path.Combine(reportsDir, stem + "_" + i.ToString(CultureInfo.InvariantCulture) + ".xlsx");
            XlsxWriter.Write(path, Sheets(r, planFolder), null);
            return path;
        }

        static readonly HashSet<string> KeyCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Connection", "Joint", "Member", "End", "Calc", "Step" };
        static readonly HashSet<string> RefCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Type", "Group", "Disposition", "Template", "Records", "Record", "Engine", "Rev", "Last Run", "Comment", "Severity", "Code", "Message" };

        static ColumnKind Kind(string c)
        {
            if (KeyCols.Contains(c)) return ColumnKind.Key;
            if (RefCols.Contains(c)) return ColumnKind.Reference;
            return ColumnKind.Tool;
        }

        public static List<XOutSheet> Sheets(SummaryResult r, string planFolder)
        {
            var sheets = new List<XOutSheet>();
            string lim = Csv.Num(r.Limit);

            // Connections
            var cols = new List<string> { "Connection", "Joint", "Member", "End", "Type", "Calc", "Group", "Disposition", "Status", "Max DCR", "Governing Check", "Governing Case", "Step" };
            cols.AddRange(r.CheckNames);
            cols.AddRange(new[] { "Why", "Records", "Engine", "Rev", "Last Run" });
            var rows = new List<Dictionary<string, string>>();
            foreach (ConnectionStatus c in r.Connections.OrderBy(x => x.Conn.Node).ThenBy(x => x.Conn.Member).ThenBy(x => x.Conn.End, StringComparer.Ordinal))
            {
                var d = Row();
                d["Connection"] = c.Conn.Connection; d["Joint"] = Int(c.Conn.Node); d["Member"] = Int(c.Conn.Member); d["End"] = c.Conn.End;
                d["Type"] = c.Conn.Type; d["Calc"] = c.Calc; d["Group"] = c.Conn.Group; d["Disposition"] = c.Conn.Disposition; d["Status"] = c.Status;
                d["Max DCR"] = double.IsNaN(c.Max) ? "" : Csv.Num(c.Max); d["Governing Check"] = c.GoverningCheck; d["Governing Case"] = c.GoverningCase;
                d["Step"] = c.GoverningStep > 0 ? Int(c.GoverningStep) : "";
                foreach (string n in r.CheckNames) { string v; d[n] = c.CheckValues.TryGetValue(n, out v) ? v : ""; }
                d["Why"] = c.Why; d["Records"] = c.Records; d["Engine"] = c.Engine; d["Rev"] = c.Rev; d["Last Run"] = c.LastRun;
                rows.Add(d);
            }
            string note = string.Format(CultureInfo.InvariantCulture, "{0} member ends: {1} OK, {2} over the limit ({3}), {4} invalid, {5} failed, {6} not run, {7} with gaps or blocked, {8} excluded. Rev {9}; plan {10}",
                r.Connections.Count, r.Count("OK"), r.Count("Over limit"), lim, r.Count("Invalid"), r.Count("Failed"), r.Count("Not run"), r.Count("Gaps") + r.Count("Blocked"), r.Count("Excluded"), r.Rev, planFolder);
            XOutSheet s = WorkbookTables.Sheet("Connections", "tblSummaryConnections", cols.ToArray(), rows, note, Kind);
            WorkbookTables.Numbers(s, cols.IndexOf("Joint"), "0");
            WorkbookTables.Numbers(s, cols.IndexOf("Member"), "0");
            WorkbookTables.Numbers(s, cols.IndexOf("Step"), "0");
            foreach (string n in new[] { "Max DCR" }.Concat(r.CheckNames)) { int k = cols.IndexOf(n); WorkbookTables.Numbers(s, k, "0.000"); DcrColours(s, k, rows.Count, r.Limit); }
            Statuses(s, cols.IndexOf("Status"), rows.Count);
            s.Width(cols.IndexOf("Why"), 60);
            sheets.Add(s);

            // Joints
            var jcols = new[] { "Joint", "Connections", "Worst Status", "Max DCR", "Governing Connection", "OK", "Over limit", "Invalid", "Failed", "Not run", "Gaps", "Excluded" };
            var jrows = new List<Dictionary<string, string>>();
            foreach (var g in r.Connections.GroupBy(c => c.Conn.Node).OrderBy(g => g.Key))
            {
                var d = Row();
                var worst = g.OrderBy(c => Array.IndexOf(Worry, c.Status)).ThenByDescending(c => double.IsNaN(c.Max) ? -1 : c.Max).First();
                var top = g.Where(c => !double.IsNaN(c.Max)).OrderByDescending(c => c.Max).FirstOrDefault();
                d["Joint"] = Int(g.Key); d["Connections"] = Int(g.Count()); d["Worst Status"] = worst.Status;
                d["Max DCR"] = top == null ? "" : Csv.Num(top.Max); d["Governing Connection"] = top == null ? "" : top.Conn.Connection;
                d["OK"] = Int(g.Count(c => c.Status == "OK")); d["Over limit"] = Int(g.Count(c => c.Status == "Over limit")); d["Invalid"] = Int(g.Count(c => c.Status == "Invalid"));
                d["Failed"] = Int(g.Count(c => c.Status == "Failed")); d["Not run"] = Int(g.Count(c => c.Status == "Not run"));
                d["Gaps"] = Int(g.Count(c => c.Status == "Gaps" || c.Status == "Blocked")); d["Excluded"] = Int(g.Count(c => c.Status == "Excluded"));
                jrows.Add(d);
            }
            XOutSheet js = WorkbookTables.Sheet("Joints", "tblSummaryJoints", jcols, jrows, "the worst connection at each joint (over the limit, then invalid, failed, gaps, not run, OK)",
                c => c == "Joint" ? ColumnKind.Key : c == "Governing Connection" ? ColumnKind.Reference : ColumnKind.Tool);
            foreach (string n in new[] { "Joint", "Connections", "OK", "Over limit", "Invalid", "Failed", "Not run", "Gaps", "Excluded" }) WorkbookTables.Numbers(js, Array.IndexOf(jcols, n), "0");
            WorkbookTables.Numbers(js, Array.IndexOf(jcols, "Max DCR"), "0.000");
            DcrColours(js, Array.IndexOf(jcols, "Max DCR"), jrows.Count, r.Limit);
            Statuses(js, Array.IndexOf(jcols, "Worst Status"), jrows.Count);
            sheets.Add(js);

            // Calcs
            var ccols = new[] { "Calc", "Step", "Template", "Status", "Cases", "Invalid", "Failed", "Max DCR", "Governing Check", "Governing Case", "Why", "Record", "Engine", "Rev", "Last Run" };
            var crows = new List<Dictionary<string, string>>();
            foreach (StepStatus ss in r.Steps)
            {
                var d = Row();
                d["Calc"] = ss.Calc; d["Step"] = ss.Step > 0 ? Int(ss.Step) : ""; d["Template"] = ss.Template;
                var whole = ss.Checks.Where(k => PlanSet.V(k, "Connection").Length == 0).ToList();
                var top = whole.Where(k => !double.IsNaN(Csv.ParseNum(PlanSet.V(k, "Max DCR")))).OrderByDescending(k => Csv.ParseNum(PlanSet.V(k, "Max DCR"))).FirstOrDefault();
                bool over = whole.Any(k => Csv.ParseNum(PlanSet.V(k, "Max DCR")) > r.Limit || PlanSet.V(k, "Status") == "NG");
                d["Status"] = ss.Status == "OK" && over ? "Over limit" : ss.Status;
                if (ss.State != null)
                {
                    d["Cases"] = Int(ss.State.Cases); d["Invalid"] = Int(ss.State.Invalid); d["Failed"] = Int(ss.State.Failed);
                    d["Engine"] = ss.State.Engine + (ss.State.EngineVersion.Length > 0 ? " " + ss.State.EngineVersion : ""); d["Rev"] = ss.State.Rev; d["Last Run"] = ss.State.LastRun;
                    d["Record"] = ss.State.Records.Replace("|", " | ");
                }
                if (top != null) { d["Max DCR"] = PlanSet.V(top, "Max DCR"); d["Governing Check"] = PlanSet.V(top, "Check"); d["Governing Case"] = PlanSet.V(top, "LC"); }
                d["Why"] = ss.Why;
                crows.Add(d);
            }
            XOutSheet cs = WorkbookTables.Sheet("Calcs", "tblSummaryCalcs", ccols, crows, "one row per calc step: its last run", Kind);
            foreach (string n in new[] { "Step", "Cases", "Invalid", "Failed" }) WorkbookTables.Numbers(cs, Array.IndexOf(ccols, n), "0");
            WorkbookTables.Numbers(cs, Array.IndexOf(ccols, "Max DCR"), "0.000");
            DcrColours(cs, Array.IndexOf(ccols, "Max DCR"), crows.Count, r.Limit);
            Statuses(cs, Array.IndexOf(ccols, "Status"), crows.Count);
            cs.Width(Array.IndexOf(ccols, "Why"), 60);
            sheets.Add(cs);

            // Problems
            var pcols = new[] { "Severity", "Calc", "Code", "Message" };
            var prows = r.Problems.Select(p => { var d = Row(); d["Severity"] = p[0]; d["Calc"] = p[1]; d["Code"] = p[2]; d["Message"] = p[3]; return d; }).ToList();
            XOutSheet ps = WorkbookTables.Sheet("Problems", "tblSummaryProblems", pcols, prows, "the plan's problems and every failed or invalid calc step", Kind);
            Palette.Severities(ps, "A3:A" + (2 + Math.Max(1, prows.Count)).ToString(CultureInfo.InvariantCulture));
            ps.Width(3, 100);
            sheets.Add(ps);
            return sheets;
        }

        static Dictionary<string, string> Row() { return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); }
        static string Int(int n) { return n.ToString(CultureInfo.InvariantCulture); }

        static string Range(int col, int rows)
        {
            string c = Xlsx.ColName(col);
            return c + "3:" + c + (2 + Math.Max(1, rows)).ToString(CultureInfo.InvariantCulture);
        }

        static void Statuses(XOutSheet s, int col, int rows) { if (col >= 0) Palette.Statuses(s, Range(col, rows)); }
        static void DcrColours(XOutSheet s, int col, int rows, double limit) { if (col >= 0) Palette.OverLimit(s, Range(col, rows), limit); }
    }
}
