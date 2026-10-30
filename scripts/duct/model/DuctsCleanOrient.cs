using System;
using System.Collections.Generic;
using System.Linq;

// Duct orientation in the clean duct model (2026-10-30, task approved on the board: flag + rotate about the
// centreline + carry along the size run). BuildClean runs it before the supports, so the support gap uses the
// rotated depth.
//   LARGE_SIDE_VERTICAL: a horizontal rect frame (within OrientHorizDeg) whose vertical extent beats its horizontal
//     extent by more than OrientTolIn (a transition only when both its ends are).
//   Rotation: the width direction turns 90 deg about the frame's own axis (Y -> axis x Y); W / H, joints and the
//     centreline stay. It spreads from a flagged frame through its size run: rect frames of the same W x H (in order) joined
//     end to end (a joint with exactly these two clean frames), through bends and risers; it stops at transitions
//     (a transition is a run of its own), branch joints, rigid links and open ends. Rotated frames: ROTATED.
//   A run that also holds a horizontal frame with its larger side already horizontal is not rotated:
//     ROTATION_CONFLICT on all its frames.
// DuctRotateLargeVertical = $false in duct-config.psd1: flag only.
public partial class DuctsToPluto
{
    const double OrientHorizDeg = 10, OrientTolIn = 1;

    static string ApplyOrientation(List<CEdge> clean, Dictionary<int, double[]> P)
    {
        double sinH = Math.Sin(OrientHorizDeg * Math.PI / 180);
        // state per frame: +1 larger side vertical, -1 larger side horizontal, 0 neither / not horizontal / unknown
        var state = new Dictionary<CEdge, int>();
        foreach (CEdge ce in clean) state[ce] = OrientState(ce, P, sinH);
        var inc = new Dictionary<int, List<CEdge>>();
        foreach (CEdge ce in clean)
            foreach (int n in new[] { ce.A, ce.B }) { List<CEdge> l; if (!inc.TryGetValue(n, out l)) { l = new List<CEdge>(); inc[n] = l; } l.Add(ce); }

        var done = new HashSet<CEdge>(); int flagged = 0, rotated = 0, runs = 0, conflicts = 0; var list = new List<string>();
        foreach (CEdge seed in clean)
        {
            if (state[seed] != 1 || done.Contains(seed)) continue;
            var run = SizeRun(seed, inc); foreach (CEdge e in run) done.Add(e);
            foreach (CEdge e in run) if (state[e] == 1) { e.Flags.Add("LARGE_SIDE_VERTICAL"); flagged++; }
            string sys = seed.Src.Count > 0 ? seed.Src[0].SysName : seed.Source;
            double len = run.Sum(e => DuctMath.Len(P[e.A], P[e.B]));
            string what = string.Format(Inv, "  {0} {1} run, {2} frame(s), {3:0.#} ft ({4} with the larger side vertical)", sys, DimText(seed.Dim), run.Count, len, run.Count(e => state[e] == 1));
            if (run.Any(e => state[e] == -1))
            {
                foreach (CEdge e in run) e.Flags.Add("ROTATION_CONFLICT");
                conflicts++; list.Add(what + ": ROTATION_CONFLICT, a horizontal frame of the run already has its larger side horizontal");
                continue;
            }
            runs++;
            if (!Cfg.RotateLargeVertical) { list.Add(what + ": flagged only (DuctRotateLargeVertical off)"); continue; }
            foreach (CEdge e in run)
            {
                double[] ax = DuctMath.Unit(DuctMath.Sub3(P[e.B], P[e.A]));
                e.Y = DuctOrient.Rotate(ax, e.Y); e.Flags.Add("ROTATED"); rotated++;
            }
            list.Add(what + ": rotated 90 deg about the centreline");
        }
        string head = string.Format(Inv, "orientation: {0} frame(s) with the larger side vertical (LARGE_SIDE_VERTICAL), {1} run(s), {2} frame(s) rotated{3}, conflicts {4} (ROTATION_CONFLICT)",
            flagged, runs, rotated, Cfg.RotateLargeVertical ? "" : " (rotation off)", conflicts);
        OrientNewLine = flagged + conflicts == 0 ? "" : string.Format(Inv, "NEW orientation: {0} frame(s) LARGE_SIDE_VERTICAL, {1} rotated in {2} run(s), {3} conflict(s)", flagged, rotated, runs, conflicts);
        return head + (list.Count > 0 ? "\n" + string.Join("\n", list.ToArray()) : "");
    }

    static string OrientNewLine = "";

    // same W and same H in order (SameDim ignores order: a 16x28 -> 28x16 twist is one size there, two runs here)
    static bool SameWH(CEdge a, CEdge b) { return Math.Abs(a.Dim[1] - b.Dim[1]) <= 0.5 && Math.Abs(a.Dim[2] - b.Dim[2]) <= 0.5; }

    // +1 / -1 / 0, see ApplyOrientation
    static int OrientState(CEdge ce, Dictionary<int, double[]> P, double sinH)
    {
        if (ce.Rigid || ce.Dim == null || ce.Dim[0] == 1 || ce.Y == null) return 0;
        double[] d = ce.Dim;
        if (double.IsNaN(d[1]) || double.IsNaN(d[2])) return 0;
        double[] ab = DuctMath.Sub3(P[ce.B], P[ce.A]); double L = Math.Sqrt(DuctMath.Dot3(ab, ab));
        if (L < 1e-9 || Math.Abs(ab[2]) / L > sinH) return 0;
        double[] ax = DuctMath.Unit(ab), u = WidthDir(ce, P);
        if (u == null) return 0;
        int st = DuctOrient.State(ax, u, d[1], d[2], OrientTolIn);
        if (ce.DimB == null) return st;
        // a transition: both ends must agree (e.g. 16x28 -> 28x16 turns the duct itself and is left alone)
        if (ce.DimB[0] != 0 || double.IsNaN(ce.DimB[1]) || double.IsNaN(ce.DimB[2])) return 0;
        int sb = DuctOrient.State(ax, u, ce.DimB[1], ce.DimB[2], OrientTolIn);
        return st == sb ? st : 0;
    }

    // the size run of a frame: same-size rect frames joined at joints holding exactly the two of them
    static List<CEdge> SizeRun(CEdge seed, Dictionary<int, List<CEdge>> inc)
    {
        var run = new List<CEdge> { seed };
        if (seed.DimB != null) return run;   // a transition is a run of its own
        var seen = new HashSet<CEdge> { seed }; var stack = new Stack<CEdge>(); stack.Push(seed);
        while (stack.Count > 0)
        {
            CEdge e = stack.Pop();
            foreach (int n in new[] { e.A, e.B })
            {
                List<CEdge> at = inc[n];
                if (at.Count != 2) continue;   // open end or branch joint
                CEdge o = at[0] == e ? at[1] : at[0];
                if (seen.Contains(o) || o.Rigid || o.DimB != null || o.Dim == null || o.Dim[0] != 0 || o.Y == null || !SameWH(e, o)) continue;
                seen.Add(o); run.Add(o); stack.Push(o);
            }
        }
        return run;
    }

}

// the orientation geometry, on plain vectors (tests: scripts\duct	ests\Test-DuctOrient.ps1)
public static class DuctOrient
{
    // vertical and horizontal extents of a W (along u) x H (along axis x u) section
    public static void Extents(double[] ax, double[] u, double W, double H, out double vert, out double hor)
    {
        double[] v = { ax[1] * u[2] - ax[2] * u[1], ax[2] * u[0] - ax[0] * u[2], ax[0] * u[1] - ax[1] * u[0] };
        vert = Math.Abs(u[2]) * W + Math.Abs(v[2]) * H;
        hor = Math.Sqrt(u[0] * u[0] + u[1] * u[1]) * W + Math.Sqrt(v[0] * v[0] + v[1] * v[1]) * H;
    }

    // +1 larger side vertical, -1 larger side horizontal, 0 within tol
    public static int State(double[] ax, double[] u, double W, double H, double tol)
    {
        double vert, hor; Extents(ax, u, W, H, out vert, out hor);
        return vert > hor + tol ? 1 : hor > vert + tol ? -1 : 0;
    }

    // the width direction turned 90 deg about the axis (unit)
    public static double[] Rotate(double[] ax, double[] u)
    {
        double[] r = { ax[1] * u[2] - ax[2] * u[1], ax[2] * u[0] - ax[0] * u[2], ax[0] * u[1] - ax[1] * u[0] };
        double l = Math.Sqrt(r[0] * r[0] + r[1] * r[1] + r[2] * r[2]);
        return l < 1e-12 ? u : new[] { r[0] / l, r[1] / l, r[2] / l };
    }
}
