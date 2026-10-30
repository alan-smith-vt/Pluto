using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

// Gap bridging (2026-10-30, user decision on the board: a Navisworks-only part in a Revit gap continues the Revit duct).
// Runs on the clean frames before the cuts. A Navisworks-only straight (both ends free) whose axis lines up with an
// open Revit end at one or both of its ends (end within BridgeAlongFt along the axis and BridgeSideIn sideways, the
// Revit frame within BridgeDeg of the axis and facing it) becomes a bridge:
//   joints  merged with the matched Revit end(s): the duct is connected (before: a floating SAP frame)
//   size    the neighbours': same on both sides = that size; different = a transition from one to the other; one
//           matched side = that side's size. Width direction from the neighbour, system from the neighbour.
//   flags   NAVIS_BRIDGE, and SIZE_FROM_NEIGHBOURS when the size differs from the Navisworks stated one (kept in the
//           match.txt list for comparison). Source "navisworks-bridge" (it stays in the SAP model as a system frame).
// Navisworks-only parts that line up with no Revit end stay as they are (NAVIS_ONLY, floating).
public static class NavBridge
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    const double BridgeAlongFt = 1.0, BridgeSideIn = 2, BridgeDeg = 10;

    class OpenEnd { public int Joint; public CEdge Edge; public double[] Out; }   // Out: unit, from the frame's other end to this end

    public static string Apply(List<CEdge> clean, Dictionary<int, double[]> P, out string newLine)
    {
        var deg = new Dictionary<int, int>();
        foreach (CEdge ce in clean) foreach (int j in new[] { ce.A, ce.B }) { int n; deg.TryGetValue(j, out n); deg[j] = n + 1; }
        var ends = new List<OpenEnd>();
        foreach (CEdge ce in clean)
        {
            if (ce.Rigid || ce.Src.Count == 0 || ce.Dim == null) continue;   // Revit duct frames only
            if (deg[ce.A] == 1) ends.Add(new OpenEnd { Joint = ce.A, Edge = ce, Out = DuctMath.Unit(DuctMath.Sub3(P[ce.A], P[ce.B])) });
            if (deg[ce.B] == 1) ends.Add(new OpenEnd { Joint = ce.B, Edge = ce, Out = DuctMath.Unit(DuctMath.Sub3(P[ce.B], P[ce.A])) });
        }
        var used = new HashSet<int>(); var list = new List<string>(); int two = 0, one = 0, resized = 0;
        foreach (CEdge ne in clean)
        {
            if (ne.Source != "navisworks-only" || ne.Rigid || deg[ne.A] != 1 || deg[ne.B] != 1) continue;
            double[] ax = DuctMath.Unit(DuctMath.Sub3(P[ne.B], P[ne.A]));
            if (DuctMath.Dot3(ax, ax) < 0.5) continue;
            double[] mid = DuctMath.Lerp(P[ne.A], P[ne.B], 0.5);
            OpenEnd ra = Match(ends, used, P, P[ne.A], ax, mid, null), rb = Match(ends, used, P, P[ne.B], ax, mid, ra);
            if (ra == null && rb == null) continue;
            double[] stated = ne.Dim, da = ra != null ? SizeAt(ra) : null, db = rb != null ? SizeAt(rb) : null;
            if (da != null && db != null) { ne.Dim = da; ne.DimB = SameSize(da, db) ? null : db; two++; }
            else { ne.Dim = da ?? db; ne.DimB = null; one++; }
            OpenEnd src = ra ?? rb;
            ne.Y = src.Edge.Y; ne.SysName = src.Edge.Src[0].SysName;
            if (ra != null) { ne.A = ra.Joint; used.Add(ra.Joint); }
            if (rb != null) { ne.B = rb.Joint; used.Add(rb.Joint); }
            ne.Source = "navisworks-bridge"; ne.Flags.Add("NAVIS_BRIDGE");
            bool differs = stated == null || !SameSize(stated, ne.Dim) || (ne.DimB != null && !SameSize(stated, ne.DimB));
            if (differs) { ne.Flags.Add("SIZE_FROM_NEIGHBOURS"); resized++; }
            list.Add(string.Format(Inv, "  Navisworks #{0} ({1}): {2}, {3:0.#} ft, size {4} (stated {5}){6}", ne.NavisId, ne.SysName,
                ra != null && rb != null ? "bridges two Revit ends" : "continues one Revit end", DuctMath.Len(P[ne.A], P[ne.B]),
                SizeText(ne.Dim) + (ne.DimB != null ? " -> " + SizeText(ne.DimB) : ""), stated != null ? SizeText(stated) : "?", differs ? " SIZE_FROM_NEIGHBOURS" : ""));
        }
        int nb = two + one;
        newLine = nb == 0 ? "" : string.Format(Inv, "NEW bridges: {0} Navisworks-only part{1} joined to the Revit duct (NAVIS_BRIDGE): {2} between two ends, {3} at one end; resized from the neighbours {4} (SIZE_FROM_NEIGHBOURS)",
            nb, nb == 1 ? "" : "s", two, one, resized);
        return string.Format(Inv, "gap bridges (NAVIS_BRIDGE, Navisworks-only parts lined up with open Revit ends): {0}{1}", nb, list.Count > 0 ? "\n" + string.Join("\n", list.ToArray()) : "");
    }

    // the nearest unused open Revit end that lines up with the bridge end p (axis ax) and faces the bridge
    static OpenEnd Match(List<OpenEnd> ends, HashSet<int> used, Dictionary<int, double[]> P, double[] p, double[] ax, double[] mid, OpenEnd not)
    {
        double cosMin = Math.Cos(BridgeDeg * Math.PI / 180), best = double.MaxValue; OpenEnd res = null;
        foreach (OpenEnd r in ends)
        {
            if (r == not || used.Contains(r.Joint) || Math.Abs(DuctMath.Dot3(r.Out, ax)) < cosMin) continue;
            double[] q = P[r.Joint];
            if (DuctMath.Dot3(r.Out, DuctMath.Sub3(mid, q)) <= 0) continue;   // the Revit frame must point at the bridge
            double[] v = DuctMath.Sub3(p, q); double along = Math.Abs(DuctMath.Dot3(v, ax));
            double side = Math.Sqrt(Math.Max(0, DuctMath.Dot3(v, v) - along * along)) * 12;
            if (along > BridgeAlongFt || side > BridgeSideIn) continue;
            if (along + side / 12 < best) { best = along + side / 12; res = r; }
        }
        return res;
    }

    // the size of a Revit frame at the matched end (Dim at A, DimB at B on a transition)
    static double[] SizeAt(OpenEnd r) { return r.Joint == r.Edge.B && r.Edge.DimB != null ? r.Edge.DimB : r.Edge.Dim; }

    static bool SameSize(double[] a, double[] b)
    {
        if (a == null || b == null || a[0] != b[0]) return false;
        if (a[0] == 1) return Math.Abs(a[3] - b[3]) <= 0.5;
        return Math.Abs(a[1] - b[1]) <= 0.5 && Math.Abs(a[2] - b[2]) <= 0.5;
    }

    static string SizeText(double[] d)
    {
        if (d == null) return "?";
        if (d[0] == 1) return double.IsNaN(d[3]) ? "?" : string.Format(Inv, "{0:0.#} dia", d[3]);
        return double.IsNaN(d[1]) || double.IsNaN(d[2]) ? "?" : string.Format(Inv, "{0:0.#}x{1:0.#}", d[1], d[2]);
    }
}
