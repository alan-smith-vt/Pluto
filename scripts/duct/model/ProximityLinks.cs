using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

// The clean model connects what the system naming joined (user 2026-11-25: "the connectivity needs to match what we've
// been calling a connected system"). DuctSystems names parts one system when one part's centreline comes within
// ProxTolIn (6 in) of another part's drawn box (its SURFACE: a branch modelled touching the face of a 7 ft wide duct is
// joined). The clean model used to join only Revit connections, ends within 1 in and same-size collinear gaps, so such
// a branch stayed open in SAP while coloured as its main. Now, on the clean frames (after NavBridge, before the cuts):
// every OPEN duct end (nothing else on its joint) within ProxTolIn of the box of a duct of the SAME system name is
// connected to it:
//   at the nearest point of that duct's centreline (its end joint when within MergeIn of it, else the duct is split
//   there); the open end joins that point directly when they are within MergeIn, otherwise by a rigid link (source
//   "proximity-link"). Nothing is moved. Flag JOINED_BY_PROXIMITY on the link and the duct whose end it was.
// Nearest box first; a duct that already shares a joint with the end is skipped. Other system names never join.
public static class ProximityLinks
{
    public const double ProxTolIn = 6, MergeIn = 1;
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // distance (in) from point p (ft) to the drawn box of duct edge e (ft, sizes in inches); +inf when it has no size
    static double SurfaceIn(CEdge e, Dictionary<int, double[]> P, double[] p)
    {
        if (e.Dim == null) return double.PositiveInfinity;
        double[] a = P[e.A], b = P[e.B];
        double L = DuctMath.Len(a, b); if (L < 1e-9) return double.PositiveInfinity;
        double[] x = DuctMath.Scale3(DuctMath.Sub3(b, a), 1 / L), r = DuctMath.Sub3(p, a);
        double s = DuctMath.Dot3(r, x);
        double ds = Math.Max(0, Math.Max(-s, s - L)) * 12;
        double[] perp = DuctMath.Sub3(r, DuctMath.Scale3(x, s));
        double[] d = e.Dim; if (e.DimB != null && Larger(e.DimB, d)) d = e.DimB;   // a transition: its larger end
        if (d[0] == 1)
        {
            if (double.IsNaN(d[3])) return double.PositiveInfinity;
            double rr = Math.Sqrt(DuctMath.Dot3(perp, perp)) * 12;
            double dr = Math.Max(0, rr - d[3] / 2);
            return Math.Sqrt(ds * ds + dr * dr);
        }
        if (double.IsNaN(d[1]) || double.IsNaN(d[2])) return double.PositiveInfinity;
        double[] w = DuctGeom.WidthDir(e, P);
        if (w == null || DuctMath.Dot3(w, w) < 0.5)
        {   // no width direction: a round of the larger side (generous, never misses a touching branch)
            double rr = Math.Sqrt(DuctMath.Dot3(perp, perp)) * 12, dr = Math.Max(0, rr - Math.Max(d[1], d[2]) / 2);
            return Math.Sqrt(ds * ds + dr * dr);
        }
        double[] h = DuctMath.Cross3(x, w);
        double dy = Math.Max(0, Math.Abs(DuctMath.Dot3(perp, w)) * 12 - d[1] / 2), dz = Math.Max(0, Math.Abs(DuctMath.Dot3(perp, h)) * 12 - d[2] / 2);
        return Math.Sqrt(ds * ds + dy * dy + dz * dz);
    }
    static bool Larger(double[] a, double[] b) { return a[0] == 1 ? a[3] > b[3] : a[1] * a[2] > b[1] * b[2]; }

    public static string Apply(List<CEdge> clean, Dictionary<int, double[]> P)
    {
        int nextNode = P.Count > 0 ? P.Keys.Max() + 1 : 1;
        Func<CEdge, string> sysOf = ce => ce.Src.Count > 0 || ce.SysName != "" ? DuctGeom.SystemOf(ce) : "";
        int links = 0, direct = 0, splits = 0; double longest = 0;
        var bySys = new SortedDictionary<string, int>(StringComparer.Ordinal);
        // open ends, in a fixed order; degrees are recounted as connections are made
        var deg = new Dictionary<int, int>();
        foreach (CEdge ce in clean) { if (ce.Hanger) continue; foreach (int j in new[] { ce.A, ce.B }) { int n; deg.TryGetValue(j, out n); deg[j] = n + 1; } }
        var ends = new List<KeyValuePair<CEdge, int>>();
        foreach (CEdge ce in clean)
        {
            if (ce.Hanger || ce.Rigid || ce.Dim == null) continue;
            if (deg[ce.A] == 1) ends.Add(new KeyValuePair<CEdge, int>(ce, ce.A));
            if (deg[ce.B] == 1) ends.Add(new KeyValuePair<CEdge, int>(ce, ce.B));
        }
        foreach (KeyValuePair<CEdge, int> en in ends)
        {
            CEdge e = en.Key; int j = en.Value;
            if (deg[j] != 1) continue;   // joined meanwhile (another end linked onto it)
            string sys = sysOf(e); if (sys == "") continue;
            double[] p = P[j];
            CEdge best = null; double bestD = double.MaxValue;
            foreach (CEdge f in clean)
            {
                if (f == e || f.Hanger || f.Rigid || f.Dead || sysOf(f) != sys) continue;
                if (f.A == e.A || f.A == e.B || f.B == e.A || f.B == e.B) continue;   // already shares a joint with e
                double d = SurfaceIn(f, P, p);
                if (d <= ProxTolIn && d < bestD) { bestD = d; best = f; }
            }
            if (best == null) continue;
            // the nearest point of best's centreline
            double t, lat; DuctMath.Project(P[best.A], P[best.B], p, out t, out lat);
            t = Math.Max(0, Math.Min(1, t));
            double lf = DuctMath.Len(P[best.A], P[best.B]);
            int target;
            if (t * lf * 12 <= MergeIn) target = best.A;
            else if ((1 - t) * lf * 12 <= MergeIn) target = best.B;
            else
            {
                target = nextNode++; P[target] = DuctMath.Lerp(P[best.A], P[best.B], t);
                int at = clean.IndexOf(best);
                clean.RemoveAt(at);
                clean.Insert(at, CleanModel.CopyEdge(best, best.A, target));
                clean.Insert(at + 1, CleanModel.CopyEdge(best, target, best.B));
                deg[target] = 2; splits++;
            }
            double gapIn = DuctMath.Len(p, P[target]) * 12;
            if (gapIn <= MergeIn)
            {
                if (e.A == j) e.A = target; else e.B = target;
                deg[j] = 0; deg[target]++; direct++;
            }
            else
            {
                var lk = new CEdge { A = j, B = target, Rigid = true, Source = "proximity-link", SysName = sys };
                lk.Flags.Add("JOINED_BY_PROXIMITY");
                clean.Add(lk);
                deg[j]++; deg[target]++; links++;
                longest = Math.Max(longest, gapIn);
            }
            e.Flags.Add("JOINED_BY_PROXIMITY");
            int c; bySys.TryGetValue(sys, out c); bySys[sys] = c + 1;
        }
        int total = links + direct;
        if (total == 0) return string.Format(Inv, "proximity joins: no open duct end within {0:0.#} in of a duct of its own system", ProxTolIn);
        return string.Format(Inv, "NEW proximity joins (JOINED_BY_PROXIMITY): {0} open duct ends within {1:0.#} in of a duct of their own system connected: {2} by a rigid link (longest {3:0.#} in, end to centreline), {4} directly; {5} ducts split where a branch lands on their side; by system: {6}",
            total, ProxTolIn, links, longest, direct, splits, string.Join(", ", bySys.Select(kv => kv.Key + " " + kv.Value).ToArray()));
    }
}
