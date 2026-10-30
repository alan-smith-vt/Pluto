using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

// The clean centreline model (2026-10-12; split stage 3 2026-10-30: out of the DuctsToPluto partial class). The
// rules are in the header of scripts/arms/DuctsClean.cs; DuctsToPluto.BuildClean runs the phases in order:
//   FromPieces (1. raw edges) -> MergeJoints (2.) -> CloseGaps (2b.) -> SplitAtLinks (3a.) -> MergeOverlaps (3b.)
//   -> CollapseChains (4.) -> Clean() -> ApplyCuts / ApplyManualDrop (settings by argument)
// Units: feet, the Navisworks frame. P = joint positions (after the merge), keyed by joint id.
public class CleanModel
{
    const double MergeIn = 1, CollinearCos = 0.99939, LatTolIn = 1, GapFt = 2, GapLatIn = 2;   // cos 2 deg; gaps up to 24 in along, 2 in sideways
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public readonly List<CEdge> Edges = new List<CEdge>();
    public readonly Dictionary<int, double[]> P = new Dictionary<int, double[]>();
    public double RawLen, OverlapFt;
    public int GapsClosed, OverlapMerged, OverlapConflict;

    // 1. raw edges: each element's pieces (sized from the overlay beam along it, else rigid), Navisworks-only rows,
    // side links. pos0: an overlay node's position (feet)
    public static CleanModel FromPieces(List<RvEl> scope, Dictionary<int, RawViewerWriter.BeamMember> beams, Dictionary<int, double[]> beamDim,
        Func<int, double[]> pos0, Dictionary<int, int> navBeamRow, List<DuctRow> rows, List<int[]> sideNodes, Func<RvConn, int> nodeOf)
    {
        var cm = new CleanModel();
        cm.AddPieces(scope, beams, beamDim, pos0, navBeamRow, rows, sideNodes, nodeOf);
        return cm;
    }

    void AddPieces(List<RvEl> scope, Dictionary<int, RawViewerWriter.BeamMember> beams, Dictionary<int, double[]> beamDim,
        Func<int, double[]> pos0, Dictionary<int, int> navBeamRow, List<DuctRow> rows, List<int[]> sideNodes, Func<RvConn, int> nodeOf)
    {
        // 1. raw edges from each element's pieces
        foreach (RvEl e in scope)
            foreach (int[] g in e.Segs)
            {
                if (g[0] == g[1]) continue;
                var ce = new CEdge { A = g[0], B = g[1], Thk = e.Thk }; ce.Src.Add(e);
                ce.Source = e.Only && e.LocRow < 0 ? "revit-only" : "revit";
                double[] da = pos0(g[0]), db = pos0(g[1]);
                double[] dir = DuctMath.Unit(DuctMath.Sub3(db, da));
                int bid = -1;
                foreach (int id in e.Beams)
                {
                    RawViewerWriter.BeamMember m = beams[id];
                    if (m.NodeA != g[0] && m.NodeB != g[0] && m.NodeA != g[1] && m.NodeB != g[1]) continue;
                    double[] bd = DuctMath.Unit(DuctMath.Sub3(pos0(m.NodeB), pos0(m.NodeA)));
                    if (Math.Abs(DuctMath.Dot3(bd, dir)) > 0.999) { bid = id; break; }
                }
                double[] dim;
                if (bid < 0 || !beamDim.TryGetValue(bid, out dim)) ce.Rigid = true;
                else
                {
                    ce.Dim = dim; ce.Y = beams[bid].LocalY;
                    if (e.Kind == "transition")
                    {
                        RvConn ka = e.Conns.Find(k => nodeOf(k) == g[0]), kb = e.Conns.Find(k => nodeOf(k) == g[1]);
                        if (ka != null && kb != null && !DuctRunGraph.SameSize(ka, kb)) { ce.Dim = ConnDim(ka) ?? dim; ce.DimB = ConnDim(kb) ?? dim; }
                    }
                    RawLen += Math.Sqrt(DuctMath.Dist2(da, db));
                }
                Edges.Add(ce);
            }
        foreach (KeyValuePair<int, int> kv in navBeamRow)
        {
            RawViewerWriter.BeamMember m = beams[kv.Key]; DuctRow r = rows[kv.Value];
            string shape; double w, h, d;
            var ce = new CEdge { A = m.NodeA, B = m.NodeB, Source = "navisworks-only", NavisId = r.NavisId, Y = m.LocalY, Thk = !double.IsNaN(r.T) && r.T > 0 ? r.T : double.NaN };
            ce.Dim = DuctGeom.NavSize(r, out shape, out w, out h, out d) ? new[] { shape == "round" ? 1.0 : 0.0, w, h, d } : new[] { 0.0, double.NaN, double.NaN, double.NaN };
            ce.Flags.Add("NAVIS_ONLY");
            Edges.Add(ce);
        }
        foreach (int[] p in sideNodes) Edges.Add(new CEdge { A = p[0], B = p[1], Rigid = true, Source = "side-link" });
    }

    public void MergeJoints(Func<int, double[]> pos0, List<RvConn[]> nearPairs, Dictionary<RvConn, RvConn> attach, Func<RvConn, int> nodeOf)
    {
        // 2. joints merged: near pairs, then anything within MergeIn (grid); position = mean of the members
        var par = new Dictionary<int, int>();
        Func<int, int> find = null;
        // iterative (2026-10-15: the recursive form overflowed the stack on a whole-building run)
        find = x =>
        {
            int p; if (!par.TryGetValue(x, out p)) { par[x] = x; return x; }
            int r = x; while (par[r] != r) r = par[r];
            while (x != r) { int nx = par[x]; par[x] = r; x = nx; }
            return r;
        };
        Action<int, int> uni = (a, b) => { int ra = find(a), rb = find(b); if (ra != rb) par[ra] = rb; };
        foreach (CEdge ce in Edges) { find(ce.A); find(ce.B); }
        foreach (RvConn[] p in nearPairs) if (!attach.ContainsKey(p[0])) uni(nodeOf(p[0]), nodeOf(p[1]));
        var all = par.Keys.ToList();
        double cellFt = MergeIn / 12;
        var grid = new Dictionary<string, List<int>>();
        foreach (int n in all)
        {
            double[] q = pos0(n);
            string key = Math.Floor(q[0] / cellFt) + "," + Math.Floor(q[1] / cellFt) + "," + Math.Floor(q[2] / cellFt);
            List<int> l; if (!grid.TryGetValue(key, out l)) { l = new List<int>(); grid[key] = l; } l.Add(n);
        }
        foreach (int n in all)
        {
            double[] q = pos0(n);
            long cx = (long)Math.Floor(q[0] / cellFt), cy = (long)Math.Floor(q[1] / cellFt), cz = (long)Math.Floor(q[2] / cellFt);
            for (int i = -1; i <= 1; i++) for (int j = -1; j <= 1; j++) for (int k = -1; k <= 1; k++)
            {
                List<int> l; if (!grid.TryGetValue((cx + i) + "," + (cy + j) + "," + (cz + k), out l)) continue;
                foreach (int m in l) if (m != n && Math.Sqrt(DuctMath.Dist2(q, pos0(m))) * 12 <= MergeIn) uni(n, m);
            }
        }
        var cnt = new Dictionary<int, int>();
        foreach (int n in all)
        {
            int r = find(n); double[] q = pos0(n); double[] s;
            if (!P.TryGetValue(r, out s)) { s = new double[3]; P[r] = s; cnt[r] = 0; }
            for (int a = 0; a < 3; a++) s[a] += q[a]; cnt[r]++;
        }
        foreach (int r in P.Keys.ToList()) for (int a = 0; a < 3; a++) P[r][a] /= cnt[r];
        foreach (CEdge ce in Edges) { ce.A = find(ce.A); ce.B = find(ce.B); if (ce.A == ce.B) ce.Dead = true; }
    }

    public void CloseGaps()
    {
        // 2b. gaps (2026-10-12, a 4 in gap in a big duct's line stayed open: no near link formed): two same-size
        // collinear duct edges of one system whose FREE ends (nothing else on that joint) face each other across
        // up to GapFt along the axis and GapLatIn sideways are joined at the midpoint
        bool changed = true;
        while (changed)
        {
            changed = false;
            // free = one DUCT edge on the joint (an end-contact rigid link across the gap does not count)
            var deg = new Dictionary<int, int>(); var degAll = new Dictionary<int, int>();
            foreach (CEdge ce in Edges.Where(x => !x.Dead))
                foreach (int n in new[] { ce.A, ce.B })
                {
                    int c; degAll.TryGetValue(n, out c); degAll[n] = c + 1;
                    if (!ce.Rigid) { deg.TryGetValue(n, out c); deg[n] = c + 1; } else if (!deg.ContainsKey(n)) deg[n] = 0;
                }
            var live = Edges.Where(x => !x.Dead && !x.Rigid && x.DimB == null && x.Source != "navisworks-only").ToList();
            for (int i = 0; i < live.Count && !changed; i++)
                for (int j = i + 1; j < live.Count && !changed; j++)
                {
                    CEdge a = live[i], b = live[j];
                    if (a.Sys != b.Sys || !SameDim(a, b)) continue;
                    foreach (int na in new[] { a.A, a.B })
                    {
                        if (changed || deg[na] != 1) continue;
                        double[] ua = DuctMath.Unit(DuctMath.Sub3(P[na], P[na == a.A ? a.B : a.A]));
                        foreach (int nb in new[] { b.A, b.B })
                        {
                            if (changed || nb == na || deg[nb] != 1) continue;
                            double[] ub = DuctMath.Unit(DuctMath.Sub3(P[nb], P[nb == b.A ? b.B : b.A]));
                            if (DuctMath.Dot3(ua, ub) > -CollinearCos) continue;
                            double[] g = DuctMath.Sub3(P[nb], P[na]); double along = DuctMath.Dot3(g, ua);
                            double lat = Math.Sqrt(Math.Max(0, DuctMath.Dot3(g, g) - along * along));
                            if (along < 0 || along > GapFt || lat * 12 > GapLatIn) continue;
                            // rigid links on either end whose far end dangles within the gap: the gap's own link, dropped
                            foreach (CEdge rl in Edges.Where(x => !x.Dead && x.Rigid && (x.A == na || x.B == na || x.A == nb || x.B == nb)).ToList())
                            {
                                int o = rl.A == na || rl.A == nb ? rl.B : rl.A;
                                if (o == na || o == nb || (degAll[o] == 1 && DuctMath.Len(P[o], P[na]) <= GapFt + 0.2)) rl.Dead = true;
                            }
                            P[na] = DuctMath.Lerp(P[na], P[nb], 0.5);
                            foreach (CEdge ce in Edges) { if (ce.A == nb) ce.A = na; if (ce.B == nb) ce.B = na; if (ce.A == ce.B) ce.Dead = true; }
                            GapsClosed++; changed = true;
                        }
                    }
                }
        }
    }

    public void SplitAtLinks()
    {
        // 3a. split a duct edge where a rigid link ends on its line (side-link junctions on a main's centreline)
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (CEdge rl in Edges.Where(x => !x.Dead && x.Rigid).ToList())
                foreach (int n in new[] { rl.A, rl.B })
                    foreach (CEdge de in Edges.Where(x => !x.Dead && !x.Rigid && x.A != n && x.B != n).ToList())
                    {
                        double t, lat; DuctMath.Project(P[de.A], P[de.B], P[n], out t, out lat);
                        if (lat * 12 > LatTolIn || t * DuctMath.Len(P[de.A], P[de.B]) * 12 < MergeIn || (1 - t) * DuctMath.Len(P[de.A], P[de.B]) * 12 < MergeIn) continue;
                        Edges.Add(CopyEdge(de, n, de.B)); de.B = n; changed = true; break;
                    }
        }
    }

    public void MergeOverlaps()
    {
        // 3b. overlapping collinear duct edges
        bool changed = true;
        while (changed)
        {
            changed = false;
            var live = Edges.Where(x => !x.Dead && !x.Rigid).ToList();
            for (int i = 0; i < live.Count && !changed; i++)
                for (int j = i + 1; j < live.Count && !changed; j++)
                {
                    CEdge a = live[i], b = live[j];
                    if (a.Sys != b.Sys) continue;
                    double[] u = DuctMath.Unit(DuctMath.Sub3(P[a.B], P[a.A]));
                    if (Math.Abs(DuctMath.Dot3(u, DuctMath.Unit(DuctMath.Sub3(P[b.B], P[b.A])))) < CollinearCos) continue;
                    double tb0, lb0, tb1, lb1; DuctMath.Project(P[a.A], P[a.B], P[b.A], out tb0, out lb0); DuctMath.Project(P[a.A], P[a.B], P[b.B], out tb1, out lb1);
                    if (lb0 * 12 > LatTolIn || lb1 * 12 > LatTolIn) continue;
                    double La = DuctMath.Len(P[a.A], P[a.B]), lo = Math.Max(0, Math.Min(tb0, tb1)) * La, hi = Math.Min(1, Math.Max(tb0, tb1)) * La;
                    if ((hi - lo) * 12 <= MergeIn) continue;   // end to end or apart: not an overlap
                    if (!SameDim(a, b) || !SameThk(a, b))
                    {
                        if (!a.Flags.Contains("OVERLAP_SIZE_CONFLICT")) OverlapConflict++;
                        a.Flags.Add("OVERLAP_SIZE_CONFLICT"); b.Flags.Add("OVERLAP_SIZE_CONFLICT");
                        continue;
                    }
                    // chain through all four joints in order along a's axis
                    var pts = new List<int> { a.A, a.B, b.A, b.B }.Distinct().OrderBy(n => DuctMath.Dot3(DuctMath.Sub3(P[n], P[a.A]), u)).ToList();
                    OverlapFt += hi - lo; OverlapMerged++;
                    a.Dead = true; b.Dead = true;
                    for (int k = 0; k + 1 < pts.Count; k++)
                    {
                        CEdge ne = CopyEdge(a, pts[k], pts[k + 1]);
                        foreach (RvEl s in b.Src) if (!ne.Src.Contains(s)) ne.Src.Add(s);
                        ne.Flags.UnionWith(b.Flags);
                        Edges.Add(ne);
                    }
                    changed = true;
                }
        }
    }

    public void CollapseChains()
    {
        // 4. collapse straight chains: a joint where exactly two collinear same-size duct edges of one system meet
        bool changed = true;
        while (changed)
        {
            changed = false;
            var inc = new Dictionary<int, List<CEdge>>();
            foreach (CEdge ce in Edges.Where(x => !x.Dead))
                foreach (int n in new[] { ce.A, ce.B }) { List<CEdge> l; if (!inc.TryGetValue(n, out l)) { l = new List<CEdge>(); inc[n] = l; } l.Add(ce); }
            foreach (KeyValuePair<int, List<CEdge>> kv in inc)
            {
                if (kv.Value.Count != 2) continue;
                CEdge a = kv.Value[0], b = kv.Value[1]; int n = kv.Key;
                if (a.Rigid || b.Rigid || a.DimB != null || b.DimB != null || a.Sys != b.Sys || !SameDim(a, b) || !SameThk(a, b) || a.Source == "navisworks-only" || b.Source == "navisworks-only") continue;
                int oa = a.A == n ? a.B : a.A, ob = b.A == n ? b.B : b.A;
                if (oa == ob || DuctMath.Dot3(DuctMath.Unit(DuctMath.Sub3(P[oa], P[n])), DuctMath.Unit(DuctMath.Sub3(P[ob], P[n]))) > -CollinearCos) continue;
                CEdge ne = CopyEdge(a, oa, ob);
                foreach (RvEl s in b.Src) if (!ne.Src.Contains(s)) ne.Src.Add(s);
                ne.Flags.UnionWith(b.Flags);
                a.Dead = true; b.Dead = true; Edges.Add(ne); changed = true; break;
            }
        }
    }

    // the live edges after the phases; each takes its parts' flags
    public List<CEdge> Clean()
    {
        var clean = Edges.Where(x => !x.Dead && x.A != x.B).ToList();
        foreach (CEdge ce in clean) foreach (RvEl s in ce.Src) foreach (string f in s.Flags) ce.Flags.Add(f);
        return clean;
    }

    // Scope cut points (2026-10-22, the SAP reviewer's cut nodes read from the reviewed SAP model: Run-Sap.ps1
    // -Points -> cut-points.csv, DuctCuts in duct-config.psd1): global inches, the Navisworks frame frames.csv uses

    // Each cut point snaps to a clean joint within CutSnapIn, else to a point on a prismatic duct edge (split there).
    // The model is then cut at those joints: per system the largest connected piece (duct length) is kept, and every
    // other piece meeting a cut joint is dropped. Edges of the kept piece ending at a cut get CUT_END.
    // A point named KEEP... is not a cut: the piece it lies on (nearest edge within KeepSnapIn) is kept instead of the
    // largest one; a system with keep points keeps only its marked pieces (for cuts where the shorter side is the scope).
    public static string ApplyCuts(List<CEdge> clean, Dictionary<int, double[]> P, DuctSettings settings)
    {
        if (settings.CutPoints.Count == 0) return "";
        double snap = settings.CutSnapIn / 12;
        var cutNodes = new HashSet<int>(); var report = new List<string>(); var notFound = new List<string>(); var cutName = new Dictionary<int, string>();
        var keepPts = settings.CutPoints.Where(x => x.Key.StartsWith("KEEP", StringComparison.OrdinalIgnoreCase)).ToList();
        int nCuts = settings.CutPoints.Count - keepPts.Count;
        int nextNode = P.Keys.Count > 0 ? P.Keys.Max() + 1 : 1;
        foreach (KeyValuePair<string, double[]> cp in settings.CutPoints.Except(keepPts))
        {
            double[] p = { cp.Value[0] / 12, cp.Value[1] / 12, cp.Value[2] / 12 };
            var used = new HashSet<int>(clean.SelectMany(x => new[] { x.A, x.B }));
            int bestN = -1; double bestD = double.MaxValue;
            foreach (int n in used) { double d = DuctMath.Len(P[n], p); if (d < bestD) { bestD = d; bestN = n; } }
            if (bestD <= snap) { cutNodes.Add(bestN); cutName[bestN] = cp.Key; report.Add(string.Format(Inv, "{0} joint N{1} ({2:0.##} in)", cp.Key, bestN, bestD * 12)); continue; }
            CEdge bestE = null; double bestT = 0, bestLat = double.MaxValue;
            foreach (CEdge ce in clean)
            {
                if (ce.Rigid || ce.DimB != null) continue;
                double t, lat; DuctMath.Project(P[ce.A], P[ce.B], p, out t, out lat);
                if (t > 0 && t < 1 && lat < bestLat) { bestLat = lat; bestT = t; bestE = ce; }
            }
            if (bestE == null || bestLat > snap) { notFound.Add(string.Format(Inv, "{0} (nearest joint {1:0.#} in)", cp.Key, bestD * 12)); continue; }
            int m = nextNode++; P[m] = DuctMath.Lerp(P[bestE.A], P[bestE.B], bestT);
            clean.Remove(bestE); clean.Add(CopyEdge(bestE, bestE.A, m)); clean.Add(CopyEdge(bestE, m, bestE.B));
            cutNodes.Add(m); cutName[m] = cp.Key;
            report.Add(string.Format(Inv, "{0} on a duct, split ({1:0.##} in)", cp.Key, bestLat * 12));
        }
        // pieces: edges joined through joints that are not cuts
        var byNode = new Dictionary<int, List<CEdge>>();
        foreach (CEdge ce in clean) foreach (int n in new[] { ce.A, ce.B }) { List<CEdge> l; if (!byNode.TryGetValue(n, out l)) { l = new List<CEdge>(); byNode[n] = l; } l.Add(ce); }
        var piece = new Dictionary<CEdge, int>(); var pieces = new List<List<CEdge>>();
        foreach (CEdge s0 in clean)
        {
            if (piece.ContainsKey(s0)) continue;
            var l = new List<CEdge>(); var q = new Queue<CEdge>(); q.Enqueue(s0); piece[s0] = pieces.Count;
            while (q.Count > 0)
            {
                CEdge e = q.Dequeue(); l.Add(e);
                foreach (int n in new[] { e.A, e.B })
                {
                    if (cutNodes.Contains(n)) continue;
                    foreach (CEdge o in byNode[n]) if (!piece.ContainsKey(o)) { piece[o] = pieces.Count; q.Enqueue(o); }
                }
            }
            pieces.Add(l);
        }
        Func<List<CEdge>, double> len = l => l.Where(x => !x.Rigid).Sum(x => DuctMath.Len(P[x.A], P[x.B]));
        Func<List<CEdge>, string> sysOf = l => { CEdge e = l.FirstOrDefault(x => x.Src.Count > 0); return e != null ? e.Src[0].SysName : ""; };
        var keep = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal); var byKeep = new HashSet<int>();
        foreach (KeyValuePair<string, double[]> kp in keepPts)
        {
            double[] p = { kp.Value[0] / 12, kp.Value[1] / 12, kp.Value[2] / 12 };
            CEdge bestE = null; double bestD = double.MaxValue;
            foreach (CEdge ce in clean)
            {
                double t, lat; DuctMath.Project(P[ce.A], P[ce.B], p, out t, out lat);
                double d = DuctMath.Len(DuctMath.Lerp(P[ce.A], P[ce.B], Math.Max(0, Math.Min(1, t))), p);
                if (d < bestD) { bestD = d; bestE = ce; }
            }
            if (bestE == null || bestD > settings.KeepSnapIn / 12) { notFound.Add(string.Format(Inv, "{0} (nearest element {1:0.#} in, limit {2:0.#} in)", kp.Key, bestD * 12, settings.KeepSnapIn)); continue; }
            int i = piece[bestE]; string s = sysOf(pieces[i]); HashSet<int> ks;
            if (!keep.TryGetValue(s, out ks)) { ks = new HashSet<int>(); keep[s] = ks; }
            ks.Add(i); byKeep.Add(i);
            report.Add(string.Format(Inv, "{0} keeps a {1:0.#} ft piece of {2} ({3:0.##} in)", kp.Key, len(pieces[i]), s == "" ? "(no system)" : s, bestD * 12));
        }
        var largest = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < pieces.Count; i++) { string s = sysOf(pieces[i]); int k; if (!largest.TryGetValue(s, out k) || len(pieces[i]) > len(pieces[k])) largest[s] = i; }
        foreach (KeyValuePair<string, int> kv in largest) if (!keep.ContainsKey(kv.Key)) keep[kv.Key] = new HashSet<int> { kv.Value };
        var drop = new HashSet<int>();
        foreach (int n in cutNodes)
            foreach (CEdge e in byNode[n]) { int i = piece[e]; if (!keep[sysOf(pieces[i])].Contains(i)) drop.Add(i); }
        // per cut, the pieces meeting it: which was kept and why (keep point / largest)
        var sides = new List<string>();
        foreach (int n in cutNodes)
        {
            var ps = byNode[n].Select(e => piece[e]).Distinct().Select(i => string.Format(Inv, "{0} {1:0.#} ft {2} ({3} el){4}",
                drop.Contains(i) ? "dropped" : "kept", len(pieces[i]), sysOf(pieces[i]), pieces[i].Count,
                drop.Contains(i) ? "" : byKeep.Contains(i) ? " (KEEP)" : " (largest)"));
            sides.Add(cutName[n] + ": " + string.Join(" | ", ps.ToArray()));
        }
        double dropFt = drop.Sum(i => len(pieces[i])); int dropN = drop.Sum(i => pieces[i].Count);
        clean.RemoveAll(e => drop.Contains(piece[e]));
        int ends = 0;
        foreach (CEdge e in clean) if (cutNodes.Contains(e.A) || cutNodes.Contains(e.B)) { e.Flags.Add("CUT_END"); ends++; }
        return string.Format(Inv, "NEW cuts: {0} of {1} placed, {2} elements ({3:0.#} ft of duct) dropped beyond them, {4} kept elements end at a cut (CUT_END){5}\nNEW cut points: {6}\nNEW cut sides: {7}",
            nCuts - notFound.Count(x => !x.StartsWith("KEEP", StringComparison.OrdinalIgnoreCase)), nCuts, dropN, dropFt, ends,
            notFound.Count > 0 ? "; NOT placed (no joint / duct within " + settings.CutSnapIn.ToString("0.#", Inv) + " in): " + string.Join(", ", notFound.ToArray()) : "",
            string.Join("; ", report.ToArray()), string.Join("; ", sides.ToArray()));
    }

    public static string ApplyManualDrop(List<CEdge> clean, Dictionary<int, double[]> P, DuctSettings settings)
    {
        if (settings.ManualDrop.Count == 0) return "";
        Func<CEdge, List<string>> partsOf = e => e.Src.Count > 0 ? e.Src.Select(x => x.Id).ToList()
            : e.NavisId != "" ? new List<string> { "N:" + e.NavisId } : new List<string>();
        var hit = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int dropped = 0, atCut = 0; double ft = 0;
        foreach (CEdge e in clean.ToList())
        {
            List<string> ps = partsOf(e);
            if (ps.Count == 0 || !ps.All(settings.ManualDrop.Contains)) continue;
            foreach (string p in ps) hit.Add(p);
            if (e.Flags.Contains("CUT_END")) { atCut++; continue; }
            if (!e.Rigid) ft += DuctMath.Len(P[e.A], P[e.B]);
            clean.Remove(e); dropped++;
        }
        var unused = settings.ManualDrop.Where(x => !hit.Contains(x)).ToList();
        return string.Format(Inv, "NEW manual drop (DuctDrop): {0} listed, {1} members ({2:0.#} ft) dropped, {3} kept because they end at a cut{4}",
            settings.ManualDrop.Count, dropped, ft, atCut,
            unused.Count > 0 ? string.Format(Inv, "; {0} not in the clean model: {1}", unused.Count, string.Join(" ", unused.Take(10).ToArray()) + (unused.Count > 10 ? " ..." : "")) : "");
    }

    public static CEdge CopyEdge(CEdge s, int a, int b)
    {
        var e = new CEdge { A = a, B = b, Rigid = s.Rigid, Dim = s.Dim, DimB = s.DimB, Y = s.Y, Source = s.Source, NavisId = s.NavisId, Thk = s.Thk };
        e.Src.AddRange(s.Src); e.Flags.UnionWith(s.Flags);
        return e;
    }

    public static double[] ConnDim(RvConn k)
    {
        if (k.Shape == "Round") return double.IsNaN(k.Dia) ? null : new[] { 1.0, double.NaN, double.NaN, k.Dia };
        return double.IsNaN(k.W) || double.IsNaN(k.H) ? null : new[] { 0.0, k.W, k.H, double.NaN };
    }

    public static bool SameDim(CEdge a, CEdge b)
    {
        if (a.Dim == null || b.Dim == null || a.Dim[0] != b.Dim[0]) return false;
        if (a.Dim[0] == 1) return Math.Abs(a.Dim[3] - b.Dim[3]) <= 0.5;
        double a1 = Math.Max(a.Dim[1], a.Dim[2]), a2 = Math.Min(a.Dim[1], a.Dim[2]), b1 = Math.Max(b.Dim[1], b.Dim[2]), b2 = Math.Min(b.Dim[1], b.Dim[2]);
        return Math.Abs(a1 - b1) <= 0.5 && Math.Abs(a2 - b2) <= 0.5;
    }

    // same wall thickness (2026-10-17): chains collapse and overlaps merge only within one thickness, so each SAP
    // frame keeps its parts' gauge; a different thickness on an overlap is an OVERLAP_SIZE_CONFLICT like a size
    public static bool SameThk(CEdge a, CEdge b)
    {
        if (double.IsNaN(a.Thk) || double.IsNaN(b.Thk)) return double.IsNaN(a.Thk) && double.IsNaN(b.Thk);
        return Math.Abs(a.Thk - b.Thk) <= 0.001;
    }

    public static string DimText(double[] d)
    {
        if (d == null) return "?";
        return d[0] == 1 ? string.Format(Inv, "{0:0.#} dia", d[3]) : string.Format(Inv, "{0:0.#}x{1:0.#}", d[1], d[2]);
    }
}
