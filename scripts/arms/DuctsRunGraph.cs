using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

// Runs, segments and size voting over the Revit connectivity (2026-10-12; vault handoff Pluto 2026-10-12).
//   Kind:    end (1 connector), branch (3+: tee, wye, tap), equipment (Mechanical Equipment), and for 2 ends:
//            transition (sizes differ), bend (ends not opposite), straight. Accessories / flex with 2 ends are
//            inline parts like any other.
//   Run:     2-end parts joined end to end at a joint where exactly two connectors meet. Bends and transitions
//            stay in the run; branch fittings, end pieces, equipment, open ends and gaps end it.
//   Segment: a run cut at its transitions (each transition is a segment of its own): the size should be
//            constant. Votes per segment: each Revit connector 1, each connector whose mesh rings confirm its
//            Revit size 1 more, each part's Navisworks stated size 0.5. Sizes compared big x small, 0.5 in grid.
//   Flags:   SIZE_OUTLIER (a part not at its segment's voted size), SIZE_CHANGE_WITHOUT_TRANSITION (two run parts
//            meeting at different sizes, neither a transition), RUNNAME_DEVIATION (Navisworks RunName differs from
//            the run's majority), SYSTEM_CHANGE (Revit system differs from the run's majority).
public partial class DuctsToPluto
{
    class RunSeg
    {
        public int Run, Seg; public string Kind;   // "segment" / "transition"
        public List<RvEl> Els = new List<RvEl>();
        public double LengthFt; public double[] Voted; public string Votes = "", RunName = "", System = "", RunEnds = "";
        public int Outliers, SizeJumps, NameDev, SysDev;
    }

    static List<RunSeg> AnalyzeRuns(List<RvEl> scope, List<Row> rows, Func<RvEl, List<int>> rowsOf, Func<RvConn, int> nodeOf,
        Dictionary<int, Node> nodes, double[] off, double scale, List<RvConn[]> nearPairs)
    {
        foreach (RvEl e in scope) e.Kind = KindOf(e);
        // joints: node -> (element, connector) attached there
        var at = new Dictionary<int, List<KeyValuePair<RvEl, RvConn>>>();
        foreach (RvEl e in scope)
            foreach (RvConn k in e.Conns)
            {
                int n = nodeOf(k); List<KeyValuePair<RvEl, RvConn>> l;
                if (!at.TryGetValue(n, out l)) { l = new List<KeyValuePair<RvEl, RvConn>>(); at[n] = l; }
                l.Add(new KeyValuePair<RvEl, RvConn>(e, k));
            }
        // run ports: both ends of a 2-end part; a branch fitting's THROUGH pair (the main carries on through a tee
        // or a straight with a tap; its other connectors start branch runs). 2026-10-12: branch fittings ended
        // runs, so every tee split its main in two (30+ runs where the test room has ~5).
        var port = new HashSet<RvConn>();
        foreach (RvEl e in scope)
        {
            if (e.Kind == "straight" || e.Kind == "bend" || e.Kind == "transition") foreach (RvConn k in e.Conns) port.Add(k);
            else if (e.Kind == "branch")
            {
                var ends = e.Conns.FindAll(k => !IsTapPoint(k));
                int[] thr = ends.Count == 2 ? new[] { 0, 1 } : ThroughPair(ends.ConvertAll(AsOpening));
                if (thr != null) { port.Add(ends[thr[0]]); port.Add(ends[thr[1]]); }
            }
        }
        Func<RvEl, bool> inRun = e => e.Conns.Exists(k => port.Contains(k));
        Func<RvEl, bool> sized = e => e.Kind != "transition" && !(e.Kind == "branch" && ThroughDiffers(e, port));
        var runOf = new Dictionary<RvEl, RvEl>(); var segOf = new Dictionary<RvEl, RvEl>();
        foreach (RvEl e in scope) if (inRun(e)) { runOf[e] = e; segOf[e] = e; }
        Func<Dictionary<RvEl, RvEl>, RvEl, RvEl> find = null;
        find = (p, e) => { while (p[e] != e) { p[e] = p[p[e]]; e = p[e]; } return e; };
        var ownerOf = new Dictionary<RvConn, RvEl>();
        foreach (RvEl e in scope) foreach (RvConn k in e.Conns) ownerOf[k] = e;
        Action<RvConn, RvConn> join = (ka, kb) =>
        {
            RvEl a = ownerOf[ka], b = ownerOf[kb];
            if (a == b || !port.Contains(ka) || !port.Contains(kb)) return;
            runOf[find(runOf, a)] = find(runOf, b);
            if (sized(a) && sized(b))
            {
                segOf[find(segOf, a)] = find(segOf, b);
                // two run parts meeting at different sizes, neither a transition
                if (ConnSize(ka) != null && ConnSize(kb) != null && !SameSize(ka, kb))
                {
                    AddFlag(a, "SIZE_CHANGE_WITHOUT_TRANSITION", "meets " + SizeText(kb) + " at a joint, own " + SizeText(ka));
                    AddFlag(b, "SIZE_CHANGE_WITHOUT_TRANSITION", "meets " + SizeText(ka) + " at a joint, own " + SizeText(kb));
                }
            }
        };
        foreach (List<KeyValuePair<RvEl, RvConn>> l in at.Values) if (l.Count == 2) join(l[0].Value, l[1].Value);
        // near links (open ends facing each other across a gap / overlap, drawn as 'Link: near'): the run carries on
        // across them; the parts keep NEAR_GAP (2026-10-12: R5 stopped at a 45 across a gap, R10 carried on)
        var nearJoined = new HashSet<RvConn>();
        foreach (RvConn[] p in nearPairs ?? new List<RvConn[]>())
        {
            if (!ownerOf.ContainsKey(p[0]) || !ownerOf.ContainsKey(p[1])) continue;
            join(p[0], p[1]);
            if (port.Contains(p[0]) && port.Contains(p[1])) { nearJoined.Add(p[0]); nearJoined.Add(p[1]); }
        }
        // number runs and segments (bigger runs first)
        var runs = new Dictionary<RvEl, List<RvEl>>();
        foreach (RvEl e in scope) if (inRun(e)) { RvEl r = find(runOf, e); List<RvEl> l; if (!runs.TryGetValue(r, out l)) { l = new List<RvEl>(); runs[r] = l; } l.Add(e); }
        var ordered = runs.Values.ToList(); ordered.Sort((x, y) => y.Count.CompareTo(x.Count));
        var res = new List<RunSeg>(); int segNo = 0;
        for (int ri = 0; ri < ordered.Count; ri++)
        {
            List<RvEl> members = ordered[ri];
            var segs = new Dictionary<RvEl, RunSeg>();
            foreach (RvEl e in members)
            {
                e.Run = ri + 1;
                RvEl key = !sized(e) ? e : find(segOf, e);
                RunSeg s; if (!segs.TryGetValue(key, out s)) { s = new RunSeg { Run = ri + 1, Seg = ++segNo, Kind = !sized(e) ? "transition" : "segment" }; segs[key] = s; res.Add(s); }
                s.Els.Add(e); e.Seg = s.Seg;
                foreach (int[] g in e.Segs) s.LengthFt += Math.Sqrt(Dist2(NodeFt(nodes[g[0]], off, scale), NodeFt(nodes[g[1]], off, scale)));
            }
            // run majorities: Navisworks RunName (matched row) and Revit system
            string runName = Majority(members.Select(e => PartRunName(e, rows, rowsOf)));
            string sys = Majority(members.Select(e => e.System));
            // why the run stops: each run port whose joint does not continue the run (open end, or a joint with
            // n connectors on it: element id + kind, '*' = a run port there)
            var why = new List<string>();
            foreach (RvEl e in members)
                foreach (RvConn k in e.Conns)
                {
                    if (!port.Contains(k) || nearJoined.Contains(k)) continue;
                    List<KeyValuePair<RvEl, RvConn>> l = at[nodeOf(k)];
                    if (l.Count == 2 && l[0].Key != l[1].Key && port.Contains(l[0].Value) && port.Contains(l[1].Value)) continue;
                    if (l.Count == 1) why.Add(e.Id + " open" + (k.To.Count > 0 ? " (connected outside the room)" : ""));
                    else why.Add(e.Id + " joint of " + l.Count + ": " + string.Join(" ", l.Select(p => p.Key.Id + "(" + p.Key.Kind + (port.Contains(p.Value) ? "*" : "") + ")").ToArray()));
                }
            string runEnds = string.Join("; ", why.ToArray());
            foreach (RunSeg s in segs.Values) { s.RunName = runName; s.System = sys; s.RunEnds = runEnds; }
            foreach (RvEl e in members)
            {
                string rn = PartRunName(e, rows, rowsOf);
                if (runName != "" && rn != "" && rn != runName) { AddFlag(e, "RUNNAME_DEVIATION", "RunName " + rn + " in run of " + runName); segs[!sized(e) ? e : find(segOf, e)].NameDev++; }
                if (sys != "" && e.System != "" && e.System != sys) { AddFlag(e, "SYSTEM_CHANGE", "system " + e.System + " in run of " + sys); segs[!sized(e) ? e : find(segOf, e)].SysDev++; }
            }
        }
        // size voting per segment
        foreach (RunSeg s in res)
        {
            if (s.Kind == "transition") continue;
            var votes = new Dictionary<string, double>(StringComparer.Ordinal); var sizeOf = new Dictionary<string, double[]>(StringComparer.Ordinal);
            Action<double[], double> vote = (sz, wt) => { string k = Key(sz); double v; votes.TryGetValue(k, out v); votes[k] = v + wt; sizeOf[k] = sz; };
            foreach (RvEl e in s.Els)
            {
                foreach (RvConn k in e.Conns)
                {
                    if (!port.Contains(k)) continue;   // a branch fitting votes with its through pair only
                    double[] sz = ConnSize(k); if (sz == null) continue;
                    vote(sz, 1);
                    if (k.Mesh != null && MeshConfirms(k)) vote(sz, 1);
                }
                int nr = NavRow(e, rowsOf(e));
                if (nr >= 0) { string shape; double w, h, d; if (NavSize(rows[nr], out shape, out w, out h, out d)) vote(shape == "round" ? new[] { 1.0, d, d } : new[] { 0.0, Math.Max(w, h), Math.Min(w, h) }, 0.5); }
            }
            if (votes.Count == 0) continue;
            string best = votes.OrderByDescending(kv => kv.Value).First().Key;
            s.Voted = sizeOf[best];
            s.Votes = string.Join(" ", votes.OrderByDescending(kv => kv.Value).Select(kv => VotedText(sizeOf[kv.Key]) + ":" + kv.Value.ToString("0.#", Inv)).ToArray());
            foreach (RvEl e in s.Els)
            {
                e.Voted = s.Voted;
                foreach (RvConn k in e.Conns)
                {
                    if (!port.Contains(k)) continue;
                    double[] sz = ConnSize(k);
                    if (sz != null && !Near(sz, s.Voted)) { AddFlag(e, "SIZE_OUTLIER", "own " + VotedText(sz) + " vs segment " + VotedText(s.Voted)); s.Outliers++; break; }
                }
            }
        }
        foreach (RunSeg s in res) foreach (RvEl e in s.Els) if (e.Flags.Contains("SIZE_CHANGE_WITHOUT_TRANSITION")) s.SizeJumps++;
        return res;
    }

    // a branch fitting whose through pair changes size (reducing tee): a transition for segments and voting
    static bool ThroughDiffers(RvEl e, HashSet<RvConn> port)
    {
        var p = e.Conns.FindAll(k => port.Contains(k));
        return p.Count == 2 && ConnSize(p[0]) != null && ConnSize(p[1]) != null && !SameSize(p[0], p[1]);
    }

    static string KindOf(RvEl e)
    {
        if (e.Cat.IndexOf("Equipment", StringComparison.OrdinalIgnoreCase) >= 0) return "equipment";
        if (e.Conns.Count == 0) return "";
        if (e.Conns.Count == 1) return "end";
        if (e.Conns.Count >= 3) return "branch";
        RvConn a = e.Conns[0], b = e.Conns[1];
        if (ConnSize(a) != null && ConnSize(b) != null && !SameSize(a, b)) return "transition";
        return a.D[0] * b.D[0] + a.D[1] * b.D[1] + a.D[2] * b.D[2] > -0.95 ? "bend" : "straight";
    }

    // { round 1/0, big side or dia, small side or dia }, null when unsized
    static double[] ConnSize(RvConn k)
    {
        if (k.Shape == "Round") return double.IsNaN(k.Dia) ? null : new[] { 1.0, k.Dia, k.Dia };
        if (double.IsNaN(k.W) || double.IsNaN(k.H)) return null;
        return new[] { 0.0, Math.Max(k.W, k.H), Math.Min(k.W, k.H) };
    }
    static bool Near(double[] a, double[] b) { return a[0] == b[0] && Math.Abs(a[1] - b[1]) <= 1 && Math.Abs(a[2] - b[2]) <= 1; }
    static bool SameSize(RvConn a, RvConn b) { double[] x = ConnSize(a), y = ConnSize(b); return x != null && y != null && Near(x, y); }
    static string SizeKey(RvConn k) { double[] s = ConnSize(k); return s == null ? null : Key(s); }
    static string SizeText(RvConn k) { double[] s = ConnSize(k); return s == null ? "?" : VotedText(s); }
    static string Key(double[] s) { return string.Format(Inv, "{0}:{1:0.0}:{2:0.0}", s[0], Math.Round(s[1] * 2) / 2, Math.Round(s[2] * 2) / 2); }
    static string VotedText(double[] s) { return s[0] == 1 ? string.Format(Inv, "{0:0.#} dia", s[1]) : string.Format(Inv, "{0:0.#}x{1:0.#}", s[1], s[2]); }

    static bool MeshConfirms(RvConn k)
    {
        double[] sz = ConnSize(k); if (sz == null || k.Mesh == null) return false;
        foreach (double[] c in k.Mesh.Cands ?? new List<double[]>())
        {
            double[] m = c[0] == 1 ? new[] { 1.0, c[3], c[3] } : new[] { 0.0, Math.Max(c[1], c[2]), Math.Min(c[1], c[2]) };
            if (Near(sz, m)) return true;
        }
        return false;
    }

    static string Majority(IEnumerable<string> xs)
    {
        var n = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string x in xs) { if (string.IsNullOrEmpty(x)) continue; int c; n.TryGetValue(x, out c); n[x] = c + 1; }
        return n.Count == 0 ? "" : n.OrderByDescending(kv => kv.Value).First().Key;
    }

    static void AddFlag(RvEl e, string flag, string detail)
    {
        if (!e.Flags.Contains(flag)) e.Flags.Add(flag);
        if (!e.Detail.Contains(detail)) e.Detail.Add(detail);
    }

    // Navisworks RunName vs our runs (2026-10-12, user: check the splitting against the model's own run names):
    // per RunName the runs it spans (parts each), per run the names it mixes. Returns { summary line, detail block }.
    static string[] RunNameCheck(List<RvEl> scope, List<Row> rows, Func<RvEl, List<int>> rowsOf)
    {
        var byName = new SortedDictionary<string, SortedDictionary<int, int>>(StringComparer.Ordinal);
        var byRun = new SortedDictionary<int, SortedDictionary<string, int>>();
        int noName = 0, inRuns = 0;
        foreach (RvEl e in scope)
        {
            if (e.Run < 0) continue;
            inRuns++;
            string rn = PartRunName(e, rows, rowsOf);
            if (rn == "") { noName++; continue; }
            SortedDictionary<int, int> a; if (!byName.TryGetValue(rn, out a)) { a = new SortedDictionary<int, int>(); byName[rn] = a; }
            int c; a.TryGetValue(e.Run, out c); a[e.Run] = c + 1;
            SortedDictionary<string, int> b; if (!byRun.TryGetValue(e.Run, out b)) { b = new SortedDictionary<string, int>(StringComparer.Ordinal); byRun[e.Run] = b; }
            b.TryGetValue(rn, out c); b[rn] = c + 1;
        }
        int oneRun = byName.Values.Count(m => m.Count == 1), mixed = byRun.Values.Count(m => m.Count > 1);
        int rvName = 0, bothDiffer = 0;
        foreach (RvEl e in scope)
        {
            if (e.RunName == "") continue; rvName++;
            int nr = NavRow(e, rowsOf(e)); string nv = nr >= 0 ? rows[nr].RunName : "";
            if (nv != "" && nv != e.RunName) bothDiffer++;
        }
        string line = string.Format(Inv, "RunName check: {0} RunNames over {1} run parts ({2} without a name; Revit RunName on {7} parts, differs from Navisworks on {8}); names in exactly one of our runs {3}, split over several {4}; our runs mixing names {5} of {6}",
            byName.Count, inRuns, noName, oneRun, byName.Count - oneRun, mixed, byRun.Count, rvName, bothDiffer);
        var s = new StringBuilder();
        s.AppendLine("RunName -> our runs (parts):");
        foreach (KeyValuePair<string, SortedDictionary<int, int>> kv in byName)
            s.AppendLine("  " + kv.Key + ": " + string.Join(", ", kv.Value.Select(p => "R" + p.Key + " (" + p.Value + ")").ToArray()));
        s.AppendLine("our runs mixing RunNames (parts):");
        foreach (KeyValuePair<int, SortedDictionary<string, int>> kv in byRun)
            if (kv.Value.Count > 1) s.AppendLine("  R" + kv.Key + ": " + string.Join(", ", kv.Value.Select(p => p.Key + " (" + p.Value + ")").ToArray()));
        return new[] { line, s.ToString() };
    }

    // a part's run name: its own Revit RunName parameter, else the Navisworks RunName of its row ONLY when matched by
    // IfcGUID (2026-10-12: a system A branch matched BY POSITION to the system B duct crossing over it took system B's name);
    // "" -> callers fall back to the Revit system name
    static string PartRunName(RvEl e, List<Row> rows, Func<RvEl, List<int>> rowsOf)
    {
        if (e.RunName != "") return e.RunName;
        if (e.Match == "") return "";
        int nr = NavRow(e, rowsOf(e));
        return nr >= 0 ? rows[nr].RunName : "";
    }

    // where a part's own system name comes from, for the label: "<name> (<source>)", or "none"
    static string NameSource(RvEl e, List<Row> rows, Func<RvEl, List<int>> rowsOf)
    {
        string rn = PartRunName(e, rows, rowsOf);
        string src = e.RunName != "" ? "Revit RunName " + e.RunName : rn != "" ? "Navisworks RunName " + rn + " (GUID match)" : e.System != "" ? "Revit system " + e.System : "";
        string n = SystemBase(rn, e.System);
        return n == "" ? "none" : n + " from " + src;
    }

    // <out>.runs.csv: one row per segment (transitions are segments of their own)
    static void WriteRunsCsv(string path, List<RunSeg> segs)
    {
        var s = new StringBuilder();
        s.AppendLine("SystemComponent,Run,Segment,Kind,Parts,Length_ft,VotedSize,Votes,RunName,System,SizeOutliers,SizeChangesWithoutTransition,RunNameDeviations,SystemChanges,RevitElementIds,RunEnds");
        foreach (RunSeg g in segs)
            s.AppendLine(string.Join(",", new[] { g.Els[0].Sys >= 0 ? g.Els[0].SysName + " C" + g.Els[0].Sys : "", "R" + g.Run, "S" + g.Seg, g.Kind, g.Els.Count.ToString(Inv), g.LengthFt.ToString("0.##", Inv),
                g.Voted != null ? VotedText(g.Voted) : (g.Kind == "transition" && g.Els.Count == 1 ? string.Join(" -> ", g.Els[0].Conns.Select(SizeText).ToArray()) : ""),
                g.Votes, g.RunName, g.System, g.Outliers.ToString(Inv), g.SizeJumps.ToString(Inv), g.NameDev.ToString(Inv), g.SysDev.ToString(Inv),
                string.Join(";", g.Els.Select(e => e.Id).ToArray()), g.RunEnds }.Select(CsvCell).ToArray()));
        File.WriteAllText(path, s.ToString(), new UTF8Encoding(false));
    }
}
