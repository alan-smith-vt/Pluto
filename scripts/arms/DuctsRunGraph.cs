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
        public double LengthFt; public double[] Voted; public string Votes = "", RunName = "", System = "";
        public int Outliers, SizeJumps, NameDev, SysDev;
    }

    static List<RunSeg> AnalyzeRuns(List<RvEl> scope, List<Row> rows, Func<RvEl, List<int>> rowsOf, Func<RvConn, int> nodeOf,
        Dictionary<int, Node> nodes, double[] off, double scale)
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
        Func<RvEl, bool> inRun = e => e.Kind == "straight" || e.Kind == "bend" || e.Kind == "transition";
        var runOf = new Dictionary<RvEl, RvEl>(); var segOf = new Dictionary<RvEl, RvEl>();
        foreach (RvEl e in scope) if (inRun(e)) { runOf[e] = e; segOf[e] = e; }
        Func<Dictionary<RvEl, RvEl>, RvEl, RvEl> find = null;
        find = (p, e) => { while (p[e] != e) { p[e] = p[p[e]]; e = p[e]; } return e; };
        foreach (List<KeyValuePair<RvEl, RvConn>> l in at.Values)
        {
            if (l.Count != 2) continue;
            RvEl a = l[0].Key, b = l[1].Key;
            if (a == b || !inRun(a) || !inRun(b)) continue;
            runOf[find(runOf, a)] = find(runOf, b);
            if (a.Kind != "transition" && b.Kind != "transition")
            {
                segOf[find(segOf, a)] = find(segOf, b);
                // two run parts meeting at different sizes, neither a transition
                string ka = SizeKey(l[0].Value), kb = SizeKey(l[1].Value);
                if (ka != null && kb != null && !SameSize(l[0].Value, l[1].Value))
                    foreach (KeyValuePair<RvEl, RvConn> p in l)
                        AddFlag(p.Key, "SIZE_CHANGE_WITHOUT_TRANSITION", "meets " + (p.Key == a ? SizeText(l[1].Value) : SizeText(l[0].Value)) + " at a joint, own " + SizeText(p.Value));
            }
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
                RvEl key = e.Kind == "transition" ? e : find(segOf, e);
                RunSeg s; if (!segs.TryGetValue(key, out s)) { s = new RunSeg { Run = ri + 1, Seg = ++segNo, Kind = e.Kind == "transition" ? "transition" : "segment" }; segs[key] = s; res.Add(s); }
                s.Els.Add(e); e.Seg = s.Seg;
                foreach (int[] g in e.Segs) s.LengthFt += Math.Sqrt(Dist2(NodeFt(nodes[g[0]], off, scale), NodeFt(nodes[g[1]], off, scale)));
            }
            // run majorities: Navisworks RunName (matched row) and Revit system
            string runName = Majority(members.Select(e => { int nr = NavRow(e, rowsOf(e)); return nr >= 0 ? rows[nr].RunName : ""; }));
            string sys = Majority(members.Select(e => e.System));
            foreach (RunSeg s in segs.Values) { s.RunName = runName; s.System = sys; }
            foreach (RvEl e in members)
            {
                int nr = NavRow(e, rowsOf(e)); string rn = nr >= 0 ? rows[nr].RunName : "";
                if (runName != "" && rn != "" && rn != runName) { AddFlag(e, "RUNNAME_DEVIATION", "RunName " + rn + " in run of " + runName); segs[e.Kind == "transition" ? e : find(segOf, e)].NameDev++; }
                if (sys != "" && e.System != "" && e.System != sys) { AddFlag(e, "SYSTEM_CHANGE", "system " + e.System + " in run of " + sys); segs[e.Kind == "transition" ? e : find(segOf, e)].SysDev++; }
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
                    if (IsTapPoint(k)) continue;
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
                    if (IsTapPoint(k)) continue;
                    double[] sz = ConnSize(k);
                    if (sz != null && !Near(sz, s.Voted)) { AddFlag(e, "SIZE_OUTLIER", "own " + VotedText(sz) + " vs segment " + VotedText(s.Voted)); s.Outliers++; break; }
                }
            }
        }
        foreach (RunSeg s in res) foreach (RvEl e in s.Els) if (e.Flags.Contains("SIZE_CHANGE_WITHOUT_TRANSITION")) s.SizeJumps++;
        return res;
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

    // <out>.runs.csv: one row per segment (transitions are segments of their own)
    static void WriteRunsCsv(string path, List<RunSeg> segs)
    {
        var s = new StringBuilder();
        s.AppendLine("Run,Segment,Kind,Parts,Length_ft,VotedSize,Votes,RunName,System,SizeOutliers,SizeChangesWithoutTransition,RunNameDeviations,SystemChanges,RevitElementIds");
        foreach (RunSeg g in segs)
            s.AppendLine(string.Join(",", new[] { "R" + g.Run, "S" + g.Seg, g.Kind, g.Els.Count.ToString(Inv), g.LengthFt.ToString("0.##", Inv),
                g.Voted != null ? VotedText(g.Voted) : (g.Kind == "transition" && g.Els.Count == 1 ? string.Join(" -> ", g.Els[0].Conns.Select(SizeText).ToArray()) : ""),
                g.Votes, g.RunName, g.System, g.Outliers.ToString(Inv), g.SizeJumps.ToString(Inv), g.NameDev.ToString(Inv), g.SysDev.ToString(Inv),
                string.Join(";", g.Els.Select(e => e.Id).ToArray()) }.Select(CsvCell).ToArray()));
        File.WriteAllText(path, s.ToString(), new UTF8Encoding(false));
    }
}
