using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

// Navisworks hanger check (2026-10-30, approved on the board: a check only, no model change, nothing to SAP; split
// stage 4: a step of SupportPlacer). Hangers = the room's Navisworks rows whose category contains "Hanger". Each
// belongs to the clean duct frame whose centreline its bbox centre lies over in plan (within half the duct width +
// 6 in, bbox bottom from 1 ft below the duct bottom to 6 ft above its top; nearest frame wins) and is matched to a
// steel support within matchIn (DuctHangerMatchIn, 24 in) of its point on the duct.
//   HANGER_NO_STEEL_SUPPORT  a hanger with no support within matchIn (flags.csv, its Navisworks row)
//   HANGER_OFF_DUCT          a hanger over no duct frame (flags.csv)
//   SUPPORT_NO_HANGER        a support with no hanger within matchIn (supports.csv), only on systems that
//                            have at least one hanger
public class HangerHit { public int Row; public int Edge = -1; public double T; public double[] At; public string System = ""; public int Support = -1; public double Dist = double.NaN; }

public class HangerRun
{
    public List<HangerHit> Hits = new List<HangerHit>();
    public int NoSupport, OffDuct, SupportsNoHanger;
    public string NewLine = "", Note = "";
    public readonly Dictionary<int, List<string>> NavRowFlags = new Dictionary<int, List<string>>();   // Navisworks row -> extra flags (flags.csv)
}

public static class HangerCheck
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // after the supports are placed, before supports.csv is written; scope = the room's Navisworks row indices
    public static HangerRun Check(List<CEdge> clean, Dictionary<int, double[]> P, List<DuctRow> rows, List<int> scope, SupportRun run, double matchIn)
    {
        var hr = new HangerRun();
        foreach (int ri in scope ?? new List<int>())
        {
            DuctRow r = rows[ri];
            if (r.Min == null || r.Max == null || r.Cat == null || r.Cat.IndexOf("Hanger", StringComparison.OrdinalIgnoreCase) < 0) continue;
            var h = new HangerHit { Row = ri };
            double[] c = { (r.Min[0] + r.Max[0]) / 2, (r.Min[1] + r.Max[1]) / 2, (r.Min[2] + r.Max[2]) / 2 };
            double best = double.MaxValue;
            for (int i = 0; i < clean.Count; i++)
            {
                CEdge ce = clean[i];
                if (ce.Rigid || ce.Dim == null) continue;
                double[] a = P[ce.A], b = P[ce.B];
                double dx = b[0] - a[0], dy = b[1] - a[1], l2 = dx * dx + dy * dy;
                if (l2 < 1e-9) continue;   // vertical frame: no plan footprint along it
                double t = ((c[0] - a[0]) * dx + (c[1] - a[1]) * dy) / l2;
                if (t < 0 || t > 1) continue;
                double[] q = DuctMath.Lerp(a, b, t);
                double lat = Math.Sqrt((c[0] - q[0]) * (c[0] - q[0]) + (c[1] - q[1]) * (c[1] - q[1]));
                double half, depth;   // ft: half the plan width and half the depth of the section
                if (ce.Dim[0] == 1) { half = depth = ce.Dim[3] / 24; }
                else
                {
                    double[] ax = DuctMath.Unit(DuctMath.Sub3(b, a)), u = DuctGeom.WidthDir(ce, P) ?? DuctMath.Unit(new[] { -ax[1], ax[0], 0 });
                    double vert, hor; DuctOrient.Extents(ax, u, ce.Dim[1], ce.Dim[2], out vert, out hor);
                    half = hor / 24; depth = vert / 24;
                }
                if (double.IsNaN(half) || lat > half + 0.5) continue;
                if (r.Min[2] < q[2] - depth - 1 || r.Min[2] > q[2] + depth + 6) continue;
                double score = lat + Math.Abs(r.Min[2] - (q[2] + depth));
                if (score < best) { best = score; h.Edge = i; h.T = t; h.At = q; h.System = ce.Src.Count > 0 ? ce.Src[0].SysName : ce.Source; }
            }
            if (h.Edge < 0) { hr.OffDuct++; AddRowFlag(hr, ri, "HANGER_OFF_DUCT"); hr.Hits.Add(h); continue; }
            if (run != null)
                for (int k = 0; k < run.Ds.Supports.Count; k++)
                {
                    double d = DuctMath.Len(P[run.DuctJoint[k]], h.At) * 12;
                    if (d <= matchIn && (double.IsNaN(h.Dist) || d < h.Dist)) { h.Dist = d; h.Support = k; }
                }
            if (h.Support < 0) { hr.NoSupport++; AddRowFlag(hr, ri, "HANGER_NO_STEEL_SUPPORT"); }
            hr.Hits.Add(h);
        }
        if (run != null)
        {
            var withHangers = new HashSet<string>(hr.Hits.Where(x => x.Edge >= 0).Select(x => x.System), StringComparer.Ordinal);
            for (int k = 0; k < run.Ds.Supports.Count; k++)
            {
                DuctSupports.Support sp = run.Ds.Supports[k];
                if (!withHangers.Contains(sp.System)) continue;
                bool near = hr.Hits.Any(x => x.Edge >= 0 && DuctMath.Len(P[run.DuctJoint[k]], x.At) * 12 <= matchIn);
                if (!near) { sp.Flags.Add("SUPPORT_NO_HANGER"); hr.SupportsNoHanger++; }
            }
        }
        int n = hr.Hits.Count;
        hr.NewLine = n == 0 ? "" : string.Format(Inv, "NEW hangers: {0} Navisworks hangers, matched {1}, no steel support {2} (HANGER_NO_STEEL_SUPPORT), off any duct {3} (HANGER_OFF_DUCT); supports without a hanger {4} (SUPPORT_NO_HANGER)",
            n, n - hr.NoSupport - hr.OffDuct, hr.NoSupport, hr.OffDuct, hr.SupportsNoHanger);
        var lines = new List<string>();
        lines.Add(string.Format(Inv, "hangers (Navisworks, category *Hanger*): {0}, matched to a support within {1:0.#} in: {2}, no steel support {3}, off any duct {4}; supports without a hanger {5} (systems with hangers only)",
            n, matchIn, n - hr.NoSupport - hr.OffDuct, hr.NoSupport, hr.OffDuct, hr.SupportsNoHanger));
        foreach (HangerHit h in hr.Hits)
            lines.Add(string.Format(Inv, "  #{0} {1}: {2}", rows[h.Row].NavisId, h.Edge < 0 ? "-" : h.System,
                h.Edge < 0 ? "off any duct (HANGER_OFF_DUCT)"
                : h.Support < 0 ? string.Format(Inv, "on F{0}, no support within {1:0.#} in (HANGER_NO_STEEL_SUPPORT)", h.Edge + 1, matchIn)
                : string.Format(Inv, "on F{0}, support {1} at {2:0.#} in", h.Edge + 1, run.Ds.Supports[h.Support].Id, h.Dist)));
        hr.Note = n == 0 ? "hangers (Navisworks, category *Hanger*): none in the room" : string.Join("\n", lines.ToArray());
        return hr;
    }

    static void AddRowFlag(HangerRun hr, int row, string flag)
    {
        List<string> l; if (!hr.NavRowFlags.TryGetValue(row, out l)) { l = new List<string>(); hr.NavRowFlags[row] = l; }
        if (!l.Contains(flag)) l.Add(flag);
    }

    // flags.csv Flags cell for a Navisworks row: NAVIS_ONLY plus the hanger flags (hr null = no check ran)
    public static string NavRowFlagText(HangerRun hr, int row)
    {
        List<string> l;
        return hr != null && hr.NavRowFlags.TryGetValue(row, out l) && l.Count > 0 ? "NAVIS_ONLY " + string.Join(" ", l.ToArray()) : "NAVIS_ONLY";
    }
}
