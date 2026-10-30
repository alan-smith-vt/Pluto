using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

// Duct-to-steel supports and the unsupported span check, on plain records (inches, one frame for ducts and steel).
// C# 5 / Add-Type (PowerShell 5.1) compatible.
//
// A support = a horizontal steel member crossing UNDER a horizontal duct in plan, with the vertical gap duct bottom ->
// steel top in [-ClashIn, GapMaxIn]. Gap below -ClashIn = the steel runs through the duct (STEEL_CLASH, no support).
// Each support: a point on the duct centreline (DuctIx, S along it) and one on the steel joint line (SteelIx, T along
// it); the caller splits both members there and links them with a TAP_STUB rigid link.
// Span check: horizontal duct frames joined at shared joints, cut at support joints and at non-horizontal frames;
// a stretch longer than SpanFt is UNSUPPORTED_SPAN.
public class DuctSupports
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public class Settings
    {
        public double GapMaxIn = 4.5;      // duct bottom to steel top, a support at or below this (demo histogram: 0..4 in, then 5+)
        public double ClashIn = 1;         // steel top above the duct bottom by more than this = STEEL_CLASH
        public double HorizDeg = 10;       // a duct / steel frame within this of horizontal counts as horizontal
        public double CrossMinDeg = 20;    // plan angle duct vs steel; below this they are parallel (not a crossing)
        public double SpanFt = 10;         // unsupported span limit
        public double HistMinIn = -12, HistMaxIn = 48, HistBinIn = 1;   // gap histogram range (all crossings)
        public string DefaultType = "guide";
    }

    public class Duct
    {
        public int JointA, JointB;         // caller's joint ids (shared joints = connected)
        public double[] A, B;              // centreline end points, in
        public bool Rigid, Round;
        public double W, H, Dia;           // in
        public double[] WidthDir;          // unit, square to the axis; null = horizontal, square to the axis
        public string System = "";
    }

    public class Steel
    {
        public double[] I, J;              // joint line end points, in
        public double TopI, TopJ;          // top of steel elevation (Z, in) at I and J
        public string Name = "", Section = "";
    }

    public class Support
    {
        public string Id, Type, System = "", Section = "";
        public int DuctIx, SteelIx;
        public double S, T;                // 0..1 along the duct A->B, the steel I->J
        public double[] PD, PS, Axis;      // duct centreline point, steel joint line point, duct direction (unit)
        public double Gap;                 // in
        public List<string> Flags = new List<string>();
    }

    public class Span
    {
        public List<int> Ducts = new List<int>();
        public double LengthIn;
        public int Supports;               // support joints bounding the stretch
        public string System = "";
    }

    public readonly Settings Set;
    public readonly List<Support> Supports = new List<Support>();
    public readonly List<double> Gaps = new List<double>();          // every crossing in the histogram range
    public int Clashes, Parallel;

    public DuctSupports(Settings s) { Set = s ?? new Settings(); }

    public void Find(List<Duct> ducts, List<Steel> steel)
    {
        double sinH = Math.Sin(Set.HorizDeg * Math.PI / 180), sinX = Math.Sin(Set.CrossMinDeg * Math.PI / 180);
        for (int di = 0; di < ducts.Count; di++)
        {
            Duct d = ducts[di];
            if (d.Rigid || !Horizontal(d.A, d.B, sinH)) continue;
            double[] ax = Unit(Sub(d.B, d.A));
            double half = HalfDepth(d, ax);
            for (int si = 0; si < steel.Count; si++)
            {
                Steel st = steel[si];
                if (!Horizontal(st.I, st.J, sinH)) continue;
                double s, t, sinA;
                if (PlanSin(d.A, d.B, st.I, st.J) < sinX)
                {
                    // steel along the duct, under its footprint (counted only; a parallel support is not modelled yet)
                    double[] m = Lerp(st.I, st.J, 0.5); double tm, lat;
                    PlanProject(d.A, d.B, m, out tm, out lat);
                    double g = d.A[2] + (d.B[2] - d.A[2]) * tm - half - (st.TopI + st.TopJ) / 2;
                    if (tm >= 0 && tm <= 1 && lat <= HalfWidth(d, ax) && g >= -Set.ClashIn && g <= Set.GapMaxIn) Parallel++;
                    continue;
                }
                if (!PlanCross(d.A, d.B, st.I, st.J, out s, out t, out sinA)) continue;
                double[] pd = Lerp(d.A, d.B, s);
                double gap = pd[2] - half - (st.TopI + (st.TopJ - st.TopI) * t);
                if (gap >= Set.HistMinIn && gap <= Set.HistMaxIn) Gaps.Add(gap);
                if (gap < -Set.ClashIn) { if (gap >= -2 * half) Clashes++; continue; }   // steel top inside the duct depth
                if (gap > Set.GapMaxIn) continue;
                var sp = new Support();
                sp.Id = "SP" + (Supports.Count + 1); sp.Type = Set.DefaultType; sp.System = d.System; sp.Section = st.Section;
                sp.DuctIx = di; sp.SteelIx = si; sp.S = s; sp.T = t; sp.PD = pd; sp.PS = Lerp(st.I, st.J, t); sp.Axis = ax; sp.Gap = gap;
                if (gap < 0) sp.Flags.Add("STEEL_OVERLAP");
                Supports.Add(sp);
            }
        }
    }

    // duct half depth along Z at its centreline (rect: both faces' Z extents; round: Dia / 2)
    static double HalfDepth(Duct d, double[] ax)
    {
        if (d.Round) return d.Dia / 2;
        double[] u = d.WidthDir ?? Unit(new[] { -ax[1], ax[0], 0 });
        double[] v = Cross(ax, u);
        return (Math.Abs(u[2]) * d.W + Math.Abs(v[2]) * d.H) / 2;
    }

    // duct half width in plan, square to its axis
    static double HalfWidth(Duct d, double[] ax)
    {
        if (d.Round) return d.Dia / 2;
        double[] u = d.WidthDir ?? Unit(new[] { -ax[1], ax[0], 0 });
        double[] v = Cross(ax, u);
        return (Math.Sqrt(u[0] * u[0] + u[1] * u[1]) * d.W + Math.Sqrt(v[0] * v[0] + v[1] * v[1]) * d.H) / 2;
    }

    static double PlanSin(double[] a, double[] b, double[] c, double[] d)
    {
        double rx = b[0] - a[0], ry = b[1] - a[1], qx = d[0] - c[0], qy = d[1] - c[1];
        double lr = Math.Sqrt(rx * rx + ry * ry), lq = Math.Sqrt(qx * qx + qy * qy);
        return lr > 0 && lq > 0 ? Math.Abs(rx * qy - ry * qx) / (lr * lq) : 0;
    }

    static void PlanProject(double[] a, double[] b, double[] p, out double t, out double lat)
    {
        double rx = b[0] - a[0], ry = b[1] - a[1], l2 = rx * rx + ry * ry;
        t = l2 > 0 ? ((p[0] - a[0]) * rx + (p[1] - a[1]) * ry) / l2 : 0;
        double x = a[0] + rx * t - p[0], y = a[1] + ry * t - p[1];
        lat = Math.Sqrt(x * x + y * y);
    }

    // plan (XY) intersection of segments a-b and c-d: s along a-b, t along c-d, both in [0, 1]; sinA = |sin| of the plan angle
    static bool PlanCross(double[] a, double[] b, double[] c, double[] d, out double s, out double t, out double sinA)
    {
        double rx = b[0] - a[0], ry = b[1] - a[1], qx = d[0] - c[0], qy = d[1] - c[1];
        double den = rx * qy - ry * qx, lr = Math.Sqrt(rx * rx + ry * ry), lq = Math.Sqrt(qx * qx + qy * qy);
        s = t = 0; sinA = lr > 0 && lq > 0 ? Math.Abs(den) / (lr * lq) : 0;
        if (sinA < 1e-6) return false;
        double wx = c[0] - a[0], wy = c[1] - a[1];
        s = (wx * qy - wy * qx) / den; t = (wx * ry - wy * rx) / den;
        const double e = 1e-9;
        return s >= -e && s <= 1 + e && t >= -e && t <= 1 + e;
    }

    // unsupported stretches: horizontal, non-rigid or rigid duct frames joined at shared joints except support joints
    public List<Span> Spans(List<Duct> ducts, HashSet<int> supportJoints)
    {
        double sinH = Math.Sin(Set.HorizDeg * Math.PI / 180);
        var parent = new int[ducts.Count]; for (int i = 0; i < parent.Length; i++) parent[i] = i;
        Func<int, int> root = null; root = i => parent[i] == i ? i : (parent[i] = root(parent[i]));
        var byJoint = new Dictionary<int, int>(); var use = new bool[ducts.Count];
        for (int i = 0; i < ducts.Count; i++)
        {
            Duct d = ducts[i];
            if (!d.Rigid && !Horizontal(d.A, d.B, sinH)) continue;
            use[i] = true;
            foreach (int j in new[] { d.JointA, d.JointB })
            {
                if (supportJoints.Contains(j)) continue;
                int o; if (byJoint.TryGetValue(j, out o)) parent[root(i)] = root(o); else byJoint[j] = i;
            }
        }
        var comps = new Dictionary<int, Span>();
        for (int i = 0; i < ducts.Count; i++)
        {
            if (!use[i]) continue;
            Span sp; int r = root(i);
            if (!comps.TryGetValue(r, out sp)) { sp = new Span(); comps[r] = sp; }
            sp.Ducts.Add(i); sp.LengthIn += Math.Sqrt(Dist2(ducts[i].A, ducts[i].B));
            if (sp.System == "" && !ducts[i].Rigid) sp.System = ducts[i].System;
        }
        var res = new List<Span>();
        foreach (Span sp in comps.Values)
        {
            // a stretch of only rigid links is not a duct span
            if (sp.Ducts.All(i => ducts[i].Rigid)) continue;
            var bound = new HashSet<int>();
            foreach (int i in sp.Ducts) foreach (int j in new[] { ducts[i].JointA, ducts[i].JointB }) if (supportJoints.Contains(j)) bound.Add(j);
            sp.Supports = bound.Count;
            if (sp.LengthIn > Set.SpanFt * 12 + 0.01) res.Add(sp);   // 0.01 in: a 10 ft bay drawn slightly skew is not over
        }
        res.Sort((x, y) => y.LengthIn.CompareTo(x.LengthIn));
        return res;
    }

    // text histogram of the crossing gaps (to choose GapMaxIn)
    public string Histogram()
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Format(Inv, "gap histogram (duct bottom -> steel top, in; {0} crossings in [{1}, {2}], bin {3} in; support <= {4}):",
            Gaps.Count, Set.HistMinIn, Set.HistMaxIn, Set.HistBinIn, Set.GapMaxIn));
        if (Gaps.Count == 0) return sb.ToString();
        int nb = (int)Math.Ceiling((Set.HistMaxIn - Set.HistMinIn) / Set.HistBinIn);
        var cnt = new int[nb];
        foreach (double g in Gaps) cnt[Math.Min(nb - 1, Math.Max(0, (int)Math.Floor((g - Set.HistMinIn) / Set.HistBinIn)))]++;
        int max = cnt.Max();
        for (int b = 0; b < nb; b++)
        {
            if (cnt[b] == 0) continue;
            double lo = Set.HistMinIn + b * Set.HistBinIn;
            sb.AppendLine(string.Format(Inv, "  [{0,6:0.#}, {1,6:0.#}) {2,5} {3}", lo, lo + Set.HistBinIn, cnt[b], new string('#', (int)Math.Ceiling(40.0 * cnt[b] / max))));
        }
        return sb.ToString();
    }

    static bool Horizontal(double[] a, double[] b, double sinH)
    {
        double l = Math.Sqrt(Dist2(a, b));
        return l > 1e-9 && Math.Abs(b[2] - a[2]) / l <= sinH;
    }
    static double[] Sub(double[] a, double[] b) { return new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] }; }
    static double Dist2(double[] a, double[] b) { double x = a[0] - b[0], y = a[1] - b[1], z = a[2] - b[2]; return x * x + y * y + z * z; }
    static double[] Unit(double[] a) { double l = Math.Sqrt(a[0] * a[0] + a[1] * a[1] + a[2] * a[2]); return l < 1e-12 ? new double[] { 0, 0, 0 } : new[] { a[0] / l, a[1] / l, a[2] / l }; }
    static double[] Lerp(double[] a, double[] b, double t) { return new[] { a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, a[2] + (b[2] - a[2]) * t }; }
    static double[] Cross(double[] x, double[] y) { return new[] { x[1] * y[2] - x[2] * y[1], x[2] * y[0] - x[0] * y[2], x[0] * y[1] - x[1] * y[0] }; }
}
