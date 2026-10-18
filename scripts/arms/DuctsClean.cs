using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

// Clean centreline model (2026-10-12; approach agreed with the user, vault handoff Pluto 2026-10-12 "Next:
// geometry simplification"). From the part-level pieces (each element's Segs, sized / oriented as drawn):
//   1. joints merged: near-link pairs (gap / overlap facing) and any joints within MergeIn
//   2. side links (a branch end butting into a duct's side) = rigid link from the branch end (the duct's face) to
//      the main's centreline, the main split there; tap stubs / eccentric offsets stay rigid links
//   3. overlapping collinear pieces (axes within 2 deg, 1 in) merged into one span (overlap length reported);
//      different sizes: both kept, flagged OVERLAP_SIZE_CONFLICT
//   4. straight chains collapsed: a joint where exactly two collinear same-size duct edges meet is removed
//   5. transitions keep their two end sizes (drawn at the larger; SAP: non-prismatic, later)
// Attachments (flanges / supports) have no pieces and drop out. Each clean edge lists its source Revit ids.
// Writes <out>_clean.bin / .features.json, <out>.frames.csv (the SAP input; the part-level one is
// <out>.parts_frames.csv) and returns the checks for match.txt.
public partial class DuctsToPluto
{
    const double MergeIn = 1, CollinearCos = 0.99939, LatTolIn = 1, GapFt = 1, GapLatIn = 2;   // cos 2 deg; gaps up to 12 in along, 2 in sideways

    class CEdge
    {
        public int A, B; public bool Rigid; public double[] Dim, DimB, Y;   // Dim / DimB: { round 1/0, W, H, Dia } inches
        public List<RvEl> Src = new List<RvEl>(); public HashSet<string> Flags = new HashSet<string>(StringComparer.Ordinal);
        public string Source = "revit"; public string NavisId = ""; public bool Dead;
        public double Thk = double.NaN;   // wall thickness (in) from Navisworks; NaN = not stated (frames.csv: 0.06 default)
        public int Sys { get { return Src.Count > 0 ? Src[0].Sys : -1; } }
    }

    static string BuildClean(string outBase, string modelId, string lengthUnit, List<RvEl> scope, Dictionary<int, RawViewerWriter.BeamMember> beams,
        Dictionary<int, double[]> beamDim, Dictionary<int, Node> nodes, double[] off, double scale, List<RvConn[]> nearPairs, List<int[]> sideNodes,
        Dictionary<int, int> navBeamRow, List<Row> rows, Func<RvConn, int> nodeOf, Dictionary<RvConn, RvConn> attach)
    {
        Func<int, double[]> pos0 = id => NodeFt(nodes[id], off, scale);
        var edges = new List<CEdge>();
        double rawLen = 0;
        // 1. raw edges from each element's pieces
        foreach (RvEl e in scope)
            foreach (int[] g in e.Segs)
            {
                if (g[0] == g[1]) continue;
                var ce = new CEdge { A = g[0], B = g[1], Thk = e.Thk }; ce.Src.Add(e);
                ce.Source = e.Only && e.LocRow < 0 ? "revit-only" : "revit";
                double[] da = pos0(g[0]), db = pos0(g[1]);
                double[] dir = Unit(Sub3(db, da));
                int bid = -1;
                foreach (int id in e.Beams)
                {
                    RawViewerWriter.BeamMember m = beams[id];
                    if (m.NodeA != g[0] && m.NodeB != g[0] && m.NodeA != g[1] && m.NodeB != g[1]) continue;
                    double[] bd = Unit(Sub3(pos0(m.NodeB), pos0(m.NodeA)));
                    if (Math.Abs(Dot3(bd, dir)) > 0.999) { bid = id; break; }
                }
                double[] dim;
                if (bid < 0 || !beamDim.TryGetValue(bid, out dim)) ce.Rigid = true;
                else
                {
                    ce.Dim = dim; ce.Y = beams[bid].LocalY;
                    if (e.Kind == "transition")
                    {
                        RvConn ka = e.Conns.Find(k => nodeOf(k) == g[0]), kb = e.Conns.Find(k => nodeOf(k) == g[1]);
                        if (ka != null && kb != null && !SameSize(ka, kb)) { ce.Dim = ConnDim(ka) ?? dim; ce.DimB = ConnDim(kb) ?? dim; }
                    }
                    rawLen += Math.Sqrt(Dist2(da, db));
                }
                edges.Add(ce);
            }
        foreach (KeyValuePair<int, int> kv in navBeamRow)
        {
            RawViewerWriter.BeamMember m = beams[kv.Key]; Row r = rows[kv.Value];
            string shape; double w, h, d;
            var ce = new CEdge { A = m.NodeA, B = m.NodeB, Source = "navisworks-only", NavisId = r.NavisId, Y = m.LocalY, Thk = !double.IsNaN(r.T) && r.T > 0 ? r.T : double.NaN };
            ce.Dim = NavSize(r, out shape, out w, out h, out d) ? new[] { shape == "round" ? 1.0 : 0.0, w, h, d } : new[] { 0.0, double.NaN, double.NaN, double.NaN };
            ce.Flags.Add("NAVIS_ONLY");
            edges.Add(ce);
        }
        foreach (int[] p in sideNodes) edges.Add(new CEdge { A = p[0], B = p[1], Rigid = true, Source = "side-link" });

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
        foreach (CEdge ce in edges) { find(ce.A); find(ce.B); }
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
                foreach (int m in l) if (m != n && Math.Sqrt(Dist2(q, pos0(m))) * 12 <= MergeIn) uni(n, m);
            }
        }
        var P = new Dictionary<int, double[]>(); var cnt = new Dictionary<int, int>();
        foreach (int n in all)
        {
            int r = find(n); double[] q = pos0(n); double[] s;
            if (!P.TryGetValue(r, out s)) { s = new double[3]; P[r] = s; cnt[r] = 0; }
            for (int a = 0; a < 3; a++) s[a] += q[a]; cnt[r]++;
        }
        foreach (int r in P.Keys.ToList()) for (int a = 0; a < 3; a++) P[r][a] /= cnt[r];
        foreach (CEdge ce in edges) { ce.A = find(ce.A); ce.B = find(ce.B); if (ce.A == ce.B) ce.Dead = true; }
        int nextId = P.Keys.Count > 0 ? P.Keys.Max() + 1 : 1;

        // 2b. gaps (2026-10-12, a 4 in gap in a big duct's line stayed open: no near link formed): two same-size
        // collinear duct edges of one system whose FREE ends (nothing else on that joint) face each other across
        // up to GapFt along the axis and GapLatIn sideways are joined at the midpoint
        int gapsClosed = 0;
        bool changed = true;
        while (changed)
        {
            changed = false;
            // free = one DUCT edge on the joint (an end-contact rigid link across the gap does not count)
            var deg = new Dictionary<int, int>(); var degAll = new Dictionary<int, int>();
            foreach (CEdge ce in edges.Where(x => !x.Dead))
                foreach (int n in new[] { ce.A, ce.B })
                {
                    int c; degAll.TryGetValue(n, out c); degAll[n] = c + 1;
                    if (!ce.Rigid) { deg.TryGetValue(n, out c); deg[n] = c + 1; } else if (!deg.ContainsKey(n)) deg[n] = 0;
                }
            var live = edges.Where(x => !x.Dead && !x.Rigid && x.DimB == null && x.Source != "navisworks-only").ToList();
            for (int i = 0; i < live.Count && !changed; i++)
                for (int j = i + 1; j < live.Count && !changed; j++)
                {
                    CEdge a = live[i], b = live[j];
                    if (a.Sys != b.Sys || !SameDim(a, b)) continue;
                    foreach (int na in new[] { a.A, a.B })
                    {
                        if (changed || deg[na] != 1) continue;
                        double[] ua = Unit(Sub3(P[na], P[na == a.A ? a.B : a.A]));
                        foreach (int nb in new[] { b.A, b.B })
                        {
                            if (changed || nb == na || deg[nb] != 1) continue;
                            double[] ub = Unit(Sub3(P[nb], P[nb == b.A ? b.B : b.A]));
                            if (Dot3(ua, ub) > -CollinearCos) continue;
                            double[] g = Sub3(P[nb], P[na]); double along = Dot3(g, ua);
                            double lat = Math.Sqrt(Math.Max(0, Dot3(g, g) - along * along));
                            if (along < 0 || along > GapFt || lat * 12 > GapLatIn) continue;
                            // rigid links on either end whose far end dangles within the gap: the gap's own link, dropped
                            foreach (CEdge rl in edges.Where(x => !x.Dead && x.Rigid && (x.A == na || x.B == na || x.A == nb || x.B == nb)).ToList())
                            {
                                int o = rl.A == na || rl.A == nb ? rl.B : rl.A;
                                if (o == na || o == nb || (degAll[o] == 1 && Len(P[o], P[na]) <= GapFt + 0.2)) rl.Dead = true;
                            }
                            P[na] = Lerp(P[na], P[nb], 0.5);
                            foreach (CEdge ce in edges) { if (ce.A == nb) ce.A = na; if (ce.B == nb) ce.B = na; if (ce.A == ce.B) ce.Dead = true; }
                            gapsClosed++; changed = true;
                        }
                    }
                }
        }

        // 3a. split a duct edge where a rigid link ends on its line (side-link junctions on a main's centreline)
        changed = true;
        while (changed)
        {
            changed = false;
            foreach (CEdge rl in edges.Where(x => !x.Dead && x.Rigid).ToList())
                foreach (int n in new[] { rl.A, rl.B })
                    foreach (CEdge de in edges.Where(x => !x.Dead && !x.Rigid && x.A != n && x.B != n).ToList())
                    {
                        double t, lat; Project(P[de.A], P[de.B], P[n], out t, out lat);
                        if (lat * 12 > LatTolIn || t * Len(P[de.A], P[de.B]) * 12 < MergeIn || (1 - t) * Len(P[de.A], P[de.B]) * 12 < MergeIn) continue;
                        edges.Add(CopyEdge(de, n, de.B)); de.B = n; changed = true; break;
                    }
        }

        // 3b. overlapping collinear duct edges
        double overlapFt = 0; int overlapMerged = 0, overlapConflict = 0;
        changed = true;
        while (changed)
        {
            changed = false;
            var live = edges.Where(x => !x.Dead && !x.Rigid).ToList();
            for (int i = 0; i < live.Count && !changed; i++)
                for (int j = i + 1; j < live.Count && !changed; j++)
                {
                    CEdge a = live[i], b = live[j];
                    if (a.Sys != b.Sys) continue;
                    double[] u = Unit(Sub3(P[a.B], P[a.A]));
                    if (Math.Abs(Dot3(u, Unit(Sub3(P[b.B], P[b.A])))) < CollinearCos) continue;
                    double tb0, lb0, tb1, lb1; Project(P[a.A], P[a.B], P[b.A], out tb0, out lb0); Project(P[a.A], P[a.B], P[b.B], out tb1, out lb1);
                    if (lb0 * 12 > LatTolIn || lb1 * 12 > LatTolIn) continue;
                    double La = Len(P[a.A], P[a.B]), lo = Math.Max(0, Math.Min(tb0, tb1)) * La, hi = Math.Min(1, Math.Max(tb0, tb1)) * La;
                    if ((hi - lo) * 12 <= MergeIn) continue;   // end to end or apart: not an overlap
                    if (!SameDim(a, b) || !SameThk(a, b))
                    {
                        if (!a.Flags.Contains("OVERLAP_SIZE_CONFLICT")) overlapConflict++;
                        a.Flags.Add("OVERLAP_SIZE_CONFLICT"); b.Flags.Add("OVERLAP_SIZE_CONFLICT");
                        continue;
                    }
                    // chain through all four joints in order along a's axis
                    var pts = new List<int> { a.A, a.B, b.A, b.B }.Distinct().OrderBy(n => Dot3(Sub3(P[n], P[a.A]), u)).ToList();
                    overlapFt += hi - lo; overlapMerged++;
                    a.Dead = true; b.Dead = true;
                    for (int k = 0; k + 1 < pts.Count; k++)
                    {
                        CEdge ne = CopyEdge(a, pts[k], pts[k + 1]);
                        foreach (RvEl s in b.Src) if (!ne.Src.Contains(s)) ne.Src.Add(s);
                        ne.Flags.UnionWith(b.Flags);
                        edges.Add(ne);
                    }
                    changed = true;
                }
        }

        // 4. collapse straight chains: a joint where exactly two collinear same-size duct edges of one system meet
        changed = true;
        while (changed)
        {
            changed = false;
            var inc = new Dictionary<int, List<CEdge>>();
            foreach (CEdge ce in edges.Where(x => !x.Dead))
                foreach (int n in new[] { ce.A, ce.B }) { List<CEdge> l; if (!inc.TryGetValue(n, out l)) { l = new List<CEdge>(); inc[n] = l; } l.Add(ce); }
            foreach (KeyValuePair<int, List<CEdge>> kv in inc)
            {
                if (kv.Value.Count != 2) continue;
                CEdge a = kv.Value[0], b = kv.Value[1]; int n = kv.Key;
                if (a.Rigid || b.Rigid || a.DimB != null || b.DimB != null || a.Sys != b.Sys || !SameDim(a, b) || !SameThk(a, b) || a.Source == "navisworks-only" || b.Source == "navisworks-only") continue;
                int oa = a.A == n ? a.B : a.A, ob = b.A == n ? b.B : b.A;
                if (oa == ob || Dot3(Unit(Sub3(P[oa], P[n])), Unit(Sub3(P[ob], P[n]))) > -CollinearCos) continue;
                CEdge ne = CopyEdge(a, oa, ob);
                foreach (RvEl s in b.Src) if (!ne.Src.Contains(s)) ne.Src.Add(s);
                ne.Flags.UnionWith(b.Flags);
                a.Dead = true; b.Dead = true; edges.Add(ne); changed = true; break;
            }
        }
        var clean = edges.Where(x => !x.Dead && x.A != x.B).ToList();
        foreach (CEdge ce in clean) foreach (RvEl s in ce.Src) foreach (string f in s.Flags) ce.Flags.Add(f);

        // checks: length, deviation of the original connectors from the clean centreline
        double cleanLen = clean.Where(x => !x.Rigid && x.Source != "navisworks-only").Sum(x => Len(P[x.A], P[x.B]));
        var dev = new List<KeyValuePair<double, string>>();
        foreach (RvEl e in scope)
            foreach (RvConn k in e.Conns)
            {
                if (attach.ContainsKey(k) || IsTapPoint(k)) continue;
                double best = double.MaxValue;
                foreach (CEdge ce in clean) { double t, lat; Project(P[ce.A], P[ce.B], k.P, out t, out lat); double[] q = Lerp(P[ce.A], P[ce.B], Math.Max(0, Math.Min(1, t))); best = Math.Min(best, Math.Sqrt(Dist2(q, k.P))); }
                if (best * 12 > 1) dev.Add(new KeyValuePair<double, string>(best * 12, e.Id + ":" + k.Key.Substring(k.Key.IndexOf(':') + 1)));
            }
        dev.Sort((x, y) => y.Key.CompareTo(x.Key));

        WriteCleanViewer(outBase + "_clean", modelId + "/clean", lengthUnit, clean, P, off, scale);
        WriteCleanFrames(outBase + ".frames.csv", clean, P);

        return string.Format(Inv, "clean model (<out>_clean.bin, <out>.frames.csv): {0} elements ({1} rigid links) from {2} pieces; duct length {3:0.#} ft -> {4:0.#} ft, overlaps merged {5} ({6:0.#} ft), different-size overlaps {7} (OVERLAP_SIZE_CONFLICT), gaps closed {10}; connectors > 1 in from the clean line {8}{9}",
            clean.Count, clean.Count(x => x.Rigid), edges.Count(x => x.Src.Count > 0 || x.Source != "revit"), rawLen, cleanLen, overlapMerged, overlapFt, overlapConflict, dev.Count,
            dev.Count > 0 ? " (worst: " + string.Join(", ", dev.Take(5).Select(x => x.Value + " " + x.Key.ToString("0.#", Inv) + " in").ToArray()) + ")" : "", gapsClosed);
    }

    static CEdge CopyEdge(CEdge s, int a, int b)
    {
        var e = new CEdge { A = a, B = b, Rigid = s.Rigid, Dim = s.Dim, DimB = s.DimB, Y = s.Y, Source = s.Source, NavisId = s.NavisId, Thk = s.Thk };
        e.Src.AddRange(s.Src); e.Flags.UnionWith(s.Flags);
        return e;
    }

    static double[] ConnDim(RvConn k)
    {
        if (k.Shape == "Round") return double.IsNaN(k.Dia) ? null : new[] { 1.0, double.NaN, double.NaN, k.Dia };
        return double.IsNaN(k.W) || double.IsNaN(k.H) ? null : new[] { 0.0, k.W, k.H, double.NaN };
    }

    static bool SameDim(CEdge a, CEdge b)
    {
        if (a.Dim == null || b.Dim == null || a.Dim[0] != b.Dim[0]) return false;
        if (a.Dim[0] == 1) return Math.Abs(a.Dim[3] - b.Dim[3]) <= 0.5;
        double a1 = Math.Max(a.Dim[1], a.Dim[2]), a2 = Math.Min(a.Dim[1], a.Dim[2]), b1 = Math.Max(b.Dim[1], b.Dim[2]), b2 = Math.Min(b.Dim[1], b.Dim[2]);
        return Math.Abs(a1 - b1) <= 0.5 && Math.Abs(a2 - b2) <= 0.5;
    }

    // same wall thickness (2026-10-17): chains collapse and overlaps merge only within one thickness, so each SAP
    // frame keeps its parts' gauge; a different thickness on an overlap is an OVERLAP_SIZE_CONFLICT like a size
    static bool SameThk(CEdge a, CEdge b)
    {
        if (double.IsNaN(a.Thk) || double.IsNaN(b.Thk)) return double.IsNaN(a.Thk) && double.IsNaN(b.Thk);
        return Math.Abs(a.Thk - b.Thk) <= 0.001;
    }

    static double Dot3(double[] a, double[] b) { return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]; }
    static double[] Unit(double[] a) { double l = Math.Sqrt(Dot3(a, a)); return l < 1e-12 ? new double[] { 0, 0, 0 } : new[] { a[0] / l, a[1] / l, a[2] / l }; }
    static double Len(double[] a, double[] b) { return Math.Sqrt(Dist2(a, b)); }
    static double[] Lerp(double[] a, double[] b, double t) { return new[] { a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, a[2] + (b[2] - a[2]) * t }; }

    // t along a->b (0..1 inside), lat = distance from the line (feet)
    static void Project(double[] a, double[] b, double[] p, out double t, out double lat)
    {
        double[] ab = Sub3(b, a); double L2 = Dot3(ab, ab);
        t = L2 < 1e-12 ? 0 : Dot3(Sub3(p, a), ab) / L2;
        lat = Math.Sqrt(Dist2(Lerp(a, b, t), p));
    }

    static string DimText(double[] d)
    {
        if (d == null) return "?";
        return d[0] == 1 ? string.Format(Inv, "{0:0.#} dia", d[3]) : string.Format(Inv, "{0:0.#}x{1:0.#}", d[1], d[2]);
    }

    static void WriteCleanViewer(string outBase, string modelId, string lengthUnit, List<CEdge> clean, Dictionary<int, double[]> P, double[] off, double scale)
    {
        var nodes = new Dictionary<int, Node>(); int next = 1; var map = new Dictionary<int, int>();
        Func<int, int> nd = n => { int id; if (!map.TryGetValue(n, out id)) { id = MakeNode(nodes, ref next, P[n], off, scale); map[n] = id; } return id; };
        var sections = new List<RawViewerWriter.SectionDef>();
        sections.Add(RawViewerWriter.SectionDef.Pipe("rigid link", (float)(1.5 * scale / 12), (float)(0.375 * scale / 12)));
        var secIx = new Dictionary<string, int>(StringComparer.Ordinal);
        var beams = new Dictionary<int, RawViewerWriter.BeamMember>(); var labels = new Dictionary<int, string>();
        var bySys = new SortedDictionary<string, List<uint>>(StringComparer.Ordinal); var gRigid = new List<uint>(); var gTrans = new List<uint>(); var gConf = new List<uint>();
        int nb = 1; double ft = scale / 12;
        foreach (CEdge ce in clean)
        {
            int s = 0;
            if (!ce.Rigid && ce.Dim != null)
            {
                double[] d = ce.Dim;
                if (ce.DimB != null && ce.DimB[0] == d[0] && (d[0] == 1 ? ce.DimB[3] > d[3] : ce.DimB[1] * ce.DimB[2] > d[1] * d[2])) d = ce.DimB;   // larger end
                bool round = d[0] == 1 && !double.IsNaN(d[3]), rect = d[0] == 0 && !double.IsNaN(d[1]) && !double.IsNaN(d[2]);
                if (round || rect)
                {
                    double t = double.IsNaN(ce.Thk) ? 0.06 : ce.Thk;
                    string name = DimText(d) + string.Format(Inv, " t{0:0.###}", t);
                    if (!secIx.TryGetValue(name, out s))
                    {
                        sections.Add(round ? RawViewerWriter.SectionDef.Pipe(name, (float)(d[3] * ft), (float)(t * ft))
                                           : RawViewerWriter.SectionDef.Box(name, (float)(d[1] * ft), (float)(d[2] * ft), (float)(t * ft)));
                        s = sections.Count - 1; secIx[name] = s;
                    }
                }
            }
            int a = nd(ce.A), b = nd(ce.B); if (a == b) continue;
            var m = Beam(nb, a, b, s, nodes);
            m.LocalY = WidthDir(ce, P);
            beams[nb] = m;
            string sys = ce.Src.Count > 0 ? ce.Src[0].SysName : (ce.Source == "navisworks-only" ? "navisworks-only" : "?");
            labels[nb] = string.Format(Inv, "clean | system {0} | {1}{2} | {3:0.##} ft | parts {4}{5}",
                sys, ce.Rigid ? "rigid link" : DimText(ce.Dim), ce.DimB != null ? " -> " + DimText(ce.DimB) : "", Len(P[ce.A], P[ce.B]),
                ce.Src.Count > 0 ? string.Join(" ", ce.Src.Select(x => x.Id).ToArray()) : (ce.NavisId != "" ? "Navisworks #" + ce.NavisId : "-"),
                ce.Flags.Count > 0 ? " | FLAGS " + string.Join(" ", ce.Flags.OrderBy(x => x, StringComparer.Ordinal).ToArray()) : "");
            List<uint> l; if (!bySys.TryGetValue(sys, out l)) { l = new List<uint>(); bySys[sys] = l; } l.Add((uint)nb);
            if (ce.Rigid) gRigid.Add((uint)nb);
            if (ce.DimB != null) gTrans.Add((uint)nb);
            if (ce.Flags.Contains("OVERLAP_SIZE_CONFLICT")) gConf.Add((uint)nb);
            nb++;
        }
        if (beams.Count == 0) return;
        var comps = new List<RawViewerWriter.Component>(); comps.Add(new RawViewerWriter.Component("Axial N", "force", "kip"));
        var units = new Dictionary<string, string>(); units["length"] = lengthUnit;
        var w = new RawViewerWriter(outBase + ".bin", nodes, null, null, null, beams, sections, comps, modelId, units);
        w.SetBeamLabels(labels); w.Write(false);
        var sc = new FeaturesSidecar(); sc.ModelId = modelId; sc.GeometryHash = w.GeometryHash;
        sc.Units["length"] = lengthUnit; sc.Units["worldOffset"] = string.Format(Inv, "{0} {1} {2}", off[0] * scale, off[1] * scale, off[2] * scale);
        string[] pal = { "#4f8cff", "#ff8a3d", "#3fe0e0", "#b07cff", "#8ae04f", "#ffd23f", "#ff5fa2", "#f0f0f0" }; int pi = 0;
        foreach (KeyValuePair<string, List<uint>> kv in bySys) sc.AddGroup("Clean: " + kv.Key, pal[pi++ % pal.Length], "beams", kv.Value, new[] { "duct", "clean", "system" }, null);
        if (gRigid.Count > 0) sc.AddGroup("Clean: rigid links", "#ff3b3b", "beams", gRigid, new[] { "duct", "clean", "link" }, "RIGID");
        if (gTrans.Count > 0) sc.AddGroup("Clean: transitions", "#ffd23f", "beams", gTrans, new[] { "duct", "clean", "transition" }, null);
        if (gConf.Count > 0) sc.AddGroup("Flag: OVERLAP_SIZE_CONFLICT", "#ff5fa2", "beams", gConf, new[] { "duct", "clean", "flag" }, "FLAG_OVERLAP_SIZE_CONFLICT");
        ApplySystemView(sc);
            File.WriteAllText(outBase + ".features.json", sc.ToJson(), new UTF8Encoding(false));
    }

    // a rectangular edge's width direction (W_in lies along it), unit and square to the edge; null = not known (rigid,
    // round, or the run had no connector width directions)
    static double[] WidthDir(CEdge ce, Dictionary<int, double[]> P)
    {
        if (ce.Y == null || ce.Rigid || (ce.Dim != null && ce.Dim[0] == 1)) return null;
        double[] y = ce.Y, x = Unit(Sub3(P[ce.B], P[ce.A])); double dt = Dot3(y, x);
        double[] yy = Unit(new[] { y[0] - dt * x[0], y[1] - dt * x[1], y[2] - dt * x[2] });
        return Dot3(yy, yy) > 0.5 ? yy : null;
    }

    // the SAP input: one frame per clean element, inches, Navisworks frame; W2 / H2 / Dia2 = a transition's other end;
    // WX / WY / WZ = the width direction (SapDucts sets the local axis angle from it); Thk_in = the parts' Navisworks
    // wall thickness, else 0.06 (ThkSource "default")
    static void WriteCleanFrames(string path, List<CEdge> clean, Dictionary<int, double[]> P)
    {
        var s = new StringBuilder();
        s.AppendLine("Frame,JointI,JointJ,XI_in,YI_in,ZI_in,XJ_in,YJ_in,ZJ_in,Shape,W_in,H_in,Dia_in,W2_in,H2_in,Dia2_in,Thk_in,ThkSource,SizeSource,Source,RevitElementId,NavisId,Flags,System,WX,WY,WZ,SystemColor");
        int f = 0;
        foreach (CEdge ce in clean)
        {
            double[] a = P[ce.A], b = P[ce.B]; double[] d = ce.Dim ?? new[] { 0.0, double.NaN, double.NaN, double.NaN }, d2 = ce.DimB;
            string shape = ce.Rigid ? "rigid" : d[0] == 1 ? "round" : "rect";
            double[] wd = WidthDir(ce, P);
            s.AppendLine(string.Join(",", new[] { "F" + (++f), "N" + ce.A, "N" + ce.B,
                R3(a[0] * 12), R3(a[1] * 12), R3(a[2] * 12), R3(b[0] * 12), R3(b[1] * 12), R3(b[2] * 12), shape,
                ce.Rigid ? "" : R3(d[1]), ce.Rigid ? "" : R3(d[2]), ce.Rigid ? "" : R3(d[3]),
                d2 != null ? R3(d2[1]) : "", d2 != null ? R3(d2[2]) : "", d2 != null ? R3(d2[3]) : "",
                ce.Rigid ? "" : double.IsNaN(ce.Thk) ? "0.06" : R3(ce.Thk), ce.Rigid ? "" : double.IsNaN(ce.Thk) ? "default" : "navisworks",
                ce.Rigid ? "rigid" : d2 != null ? "transition" : "revit", ce.Source,
                string.Join(";", ce.Src.Select(x => x.Id).ToArray()), ce.NavisId,
                string.Join(" ", ce.Flags.OrderBy(x => x, StringComparer.Ordinal).ToArray()),
                ce.Src.Count > 0 ? ce.Src[0].SysName : ce.Source,
                wd != null ? wd[0].ToString("0.######", Inv) : "", wd != null ? wd[1].ToString("0.######", Inv) : "", wd != null ? wd[2].ToString("0.######", Inv) : "",
                SystemColor(ce.Src.Count > 0 ? ce.Src[0].SysName : "") }.Select(CsvCell).ToArray()));
        }
        File.WriteAllText(path, s.ToString(), new UTF8Encoding(false));
    }
}
