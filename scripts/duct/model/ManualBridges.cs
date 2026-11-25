using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

// Add-on (2026-11-25): hand-picked bridges between duct fragments. The viewer's Bridge picker
// (viewer/scripts/modules/bridgePicker.js) writes bridges.json; DuctBridges in duct-config.psd1 names it (Get-DuctSettings
// reads it into DuctSettings.Bridges). Self-contained: empty DuctBridges = off; delete this file, its call in BuildClean
// and the DuctBridges lines in Get-DuctSettings.ps1 to remove it.
//
// Each bridge names two clean members by their source parts (Revit ElementId, N:<NavisId>), A and B. Each side is every
// clean duct element made of exactly those parts (a member later split at a cut or a link is several elements). The
// two nearest ends of the two sides (open ends first) are the bridge ends.
//   rigid    a rigid link between the two ends (source "manual-bridge", the system of A)
//   extend   duct A runs on along its own axis: its end moves to the point of its axis nearest B's centreline. When B's
//            line passes within ExtendJoinIn of that point, A ends ON B (at B's end when it is within ExtendJoinIn of
//            it, else B is split there); otherwise a short rigid link closes the offset. A's end must be open (nothing
//            else at it) and B must lie ahead of it; if not, a rigid link is used and the note says why.
// Flags MANUAL_BRIDGE on the new / changed elements; match.txt line "NEW manual bridges".
public class ManualBridge
{
    public string Mode = "rigid";
    public List<string> A = new List<string>(), B = new List<string>();
    public string LabelA = "", LabelB = "";
}

public static class ManualBridges
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public const double ExtendJoinIn = 2;   // extend: B's line this close to A's extended end = A ends on B, no link

    static List<string> PartsOf(CEdge e)
    {
        return e.Src.Count > 0 ? e.Src.Select(x => x.Id).ToList() : e.NavisId != "" ? new List<string> { "N:" + e.NavisId } : new List<string>();
    }
    static bool SameParts(List<string> a, List<string> b)
    {
        return a.Count == b.Count && new HashSet<string>(a, StringComparer.OrdinalIgnoreCase).SetEquals(b);
    }

    public static string Apply(List<CEdge> clean, Dictionary<int, double[]> P, List<ManualBridge> bridges)
    {
        if (bridges == null || bridges.Count == 0) return "";
        int nextNode = P.Count > 0 ? P.Keys.Max() + 1 : 1;
        int rigid = 0, extended = 0, notFound = 0, joined = 0, fallback = 0;
        var lines = new List<string>();
        for (int bi = 0; bi < bridges.Count; bi++)
        {
            ManualBridge br = bridges[bi];
            string tag = "  bridge " + (bi + 1) + " (" + br.Mode + ")";
            var sa = clean.Where(e => !e.Rigid && !e.Hanger && SameParts(PartsOf(e), br.A)).ToList();
            var sb = clean.Where(e => !e.Rigid && !e.Hanger && SameParts(PartsOf(e), br.B)).ToList();
            if (sa.Count == 0 || sb.Count == 0)
            {
                notFound++;
                lines.Add(tag + ": NOT placed, " + (sa.Count == 0 ? "A (" + string.Join(" ", br.A.ToArray()) + ")" : "B (" + string.Join(" ", br.B.ToArray()) + ")") + " not in the clean model");
                continue;
            }
            var deg = new Dictionary<int, int>();
            foreach (CEdge ce in clean) foreach (int j in new[] { ce.A, ce.B }) { int n; deg.TryGetValue(j, out n); deg[j] = n + 1; }
            // the nearest pair of ends; open ends (nothing else at them) first
            CEdge ea = null, eb = null; int ja = 0, jb = 0; double best = double.MaxValue; bool bestOpen = false;
            foreach (CEdge x in sa) foreach (int a in new[] { x.A, x.B })
                foreach (CEdge y in sb) foreach (int b in new[] { y.A, y.B })
                {
                    bool open = deg[a] == 1 && deg[b] == 1; double d = DuctMath.Len(P[a], P[b]);
                    if ((open && !bestOpen) || (open == bestOpen && d < best)) { best = d; bestOpen = open; ea = x; eb = y; ja = a; jb = b; }
                }
            if (ja == jb) { joined++; lines.Add(tag + ": A and B already share a joint, nothing to do"); continue; }
            string sys = ea.Src.Count > 0 || ea.SysName != "" ? DuctGeom.SystemOf(ea) : "";
            string why = "";
            if (br.Mode == "extend")
            {
                int jo = ea.A == ja ? ea.B : ea.A;
                double[] u = DuctMath.Unit(DuctMath.Sub3(P[ja], P[jo]));
                // the point of B's segment nearest A's axis line, then its foot on A's axis
                double tb; double[] r = NearestOnSegmentToLine(P[eb.A], P[eb.B], P[ja], u, out tb);
                double s = DuctMath.Dot3(DuctMath.Sub3(r, P[ja]), u);
                double[] q = { P[ja][0] + u[0] * s, P[ja][1] + u[1] * s, P[ja][2] + u[2] * s };
                if (deg[ja] != 1) why = "A's end is not open";
                else if (s <= 1e-6) why = "B is not ahead of A's end";
                if (why == "")
                {
                    double off = DuctMath.Len(q, r) * 12, lb = DuctMath.Len(P[eb.A], P[eb.B]);
                    int target;
                    if (off <= ExtendJoinIn)
                    {
                        // A ends on B: B's end, or B split at r
                        if (tb * lb * 12 <= ExtendJoinIn) target = eb.A;
                        else if ((1 - tb) * lb * 12 <= ExtendJoinIn) target = eb.B;
                        else { target = nextNode++; P[target] = r; Split(clean, eb, target); }
                        Retarget(ea, ja, target);
                    }
                    else
                    {
                        int qn = nextNode++; P[qn] = q;
                        int rn;
                        if (tb * lb * 12 <= ExtendJoinIn) rn = eb.A;
                        else if ((1 - tb) * lb * 12 <= ExtendJoinIn) rn = eb.B;
                        else { rn = nextNode++; P[rn] = r; Split(clean, eb, rn); }
                        Retarget(ea, ja, qn);
                        clean.Add(Link(qn, rn, sys));
                    }
                    ea.Flags.Add("MANUAL_BRIDGE"); extended++;
                    lines.Add(string.Format(Inv, "{0}: A extended {1:0.##} ft along its axis to B{2}", tag, s,
                        off <= ExtendJoinIn ? "" : string.Format(Inv, ", rigid link over the {0:0.#} in offset", off)));
                    continue;
                }
                fallback++;
            }
            clean.Add(Link(ja, jb, sys)); rigid++;
            lines.Add(string.Format(Inv, "{0}: rigid link {1:0.##} ft between the nearest {2}ends{3}", tag, best,
                bestOpen ? "open " : "", why != "" ? " (extend not possible: " + why + ")" : ""));
        }
        return string.Format(Inv, "NEW manual bridges (DuctBridges): {0} listed, {1} rigid links, {2} extended, {3} not placed (a side not in the clean model), {4} already joined{5}",
            bridges.Count, rigid, extended, notFound, joined, fallback > 0 ? string.Format(Inv, "; {0} extend fell back to a rigid link", fallback) : "")
            + (lines.Count > 0 ? "\n" + string.Join("\n", lines.ToArray()) : "");
    }

    static CEdge Link(int a, int b, string sys)
    {
        var e = new CEdge { A = a, B = b, Rigid = true, Source = "manual-bridge", SysName = sys };
        e.Flags.Add("MANUAL_BRIDGE");
        return e;
    }
    static void Retarget(CEdge e, int from, int to) { if (e.A == from) e.A = to; else e.B = to; }
    static void Split(List<CEdge> clean, CEdge e, int node)
    {
        int at = clean.IndexOf(e);
        clean.RemoveAt(at);
        clean.Insert(at, CleanModel.CopyEdge(e, e.A, node));
        clean.Insert(at + 1, CleanModel.CopyEdge(e, node, e.B));
    }
    // the point of segment a-b nearest the infinite line p + s u (u unit); t = its parameter on a-b (0..1)
    static double[] NearestOnSegmentToLine(double[] a, double[] b, double[] p, double[] u, out double t)
    {
        double[] d = DuctMath.Sub3(b, a), w = DuctMath.Sub3(a, p);
        double dd = DuctMath.Dot3(d, d), du = DuctMath.Dot3(d, u), dw = DuctMath.Dot3(d, w), uw = DuctMath.Dot3(u, w);
        double den = dd - du * du;   // |d|^2 (1 - cos^2)
        t = den < 1e-12 * Math.Max(dd, 1e-12) ? 0 : (du * uw - dw) / den;
        if (den < 1e-12 * Math.Max(dd, 1e-12))   // parallel: B's end nearer A's end
            t = DuctMath.Len(a, p) <= DuctMath.Len(b, p) ? 0 : 1;
        t = Math.Max(0, Math.Min(1, t));
        return new[] { a[0] + d[0] * t, a[1] + d[1] * t, a[2] + d[2] * t };
    }
}
