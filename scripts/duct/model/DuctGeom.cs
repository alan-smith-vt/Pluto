using System;
using System.Collections.Generic;

// Small shared helpers of the duct converters over DuctModel records (split stage 2: out of the DuctsToPluto
// partial). Tap points, openings and the through pair of a fitting, Navisworks row sizes, node coordinates, CSV cells.
public static class DuctGeom
{
    public static bool IsTapPoint(RvConn k) { return string.Equals(k.Type, "Curve", StringComparison.OrdinalIgnoreCase); }
    // As a probe DuctOpening, to reuse ThroughPair / BranchJunction (round = "round", else A x B, A the larger).
    public static DuctOpening AsOpening(RvConn k)
    {
        var o = new DuctOpening(); o.C = k.P; o.N = k.D;
        if (k.Shape == "Round") { o.Shape = "round"; o.Dia = k.Dia; }
        else { o.Shape = "rect"; o.A = Math.Max(k.W, k.H); o.B = Math.Min(k.W, k.H); }
        return o;
    }
    // The Navisworks row that stands for element e: the one its mesh connectors matched (an IfcGUID can be shared
    // by several rows of a fabrication assembly, of other sizes), else the first; -1 = none
    public static int NavRow(RvEl e, List<int> pr)
    {
        foreach (RvConn k in e.Conns) if (k.MeshRow >= 0 && pr.Contains(k.MeshRow)) return k.MeshRow;
        return pr.Count > 0 ? pr[0] : -1;
    }
    // Navisworks stated size: Width / Height / Diameter, else read from the Name
    public static bool NavSize(DuctRow r, out string shape, out double w, out double h, out double d)
    {
        w = !double.IsNaN(r.W) ? r.W : r.NameW; h = !double.IsNaN(r.H) ? r.H : r.NameH; d = !double.IsNaN(r.D) ? r.D : r.NameD;
        if (!double.IsNaN(w) && !double.IsNaN(h)) { shape = "rect"; d = double.NaN; return true; }
        if (!double.IsNaN(d)) { shape = "round"; w = h = double.NaN; return true; }
        shape = "rect"; return false;
    }
    public static double[] NodeFt(Node n, double[] off, double scale) { return new[] { n.xyz.X / scale + off[0], n.xyz.Y / scale + off[1], n.xyz.Z / scale + off[2] }; }
    // The main run through a part with 3+ connectors: the pair facing opposite ways (dot < -0.9), on one
    // axis (each centre within max(3 in, a quarter of its size) of the other's axis) and the same size
    // (within 1 in); the farthest-apart such pair. null = none (drawn as a star).
    public static int[] ThroughPair(List<DuctOpening> c)
    {
        int[] best = null; double bestD = -1;
        for (int i = 0; i < c.Count; i++)
            for (int j = i + 1; j < c.Count; j++)
            {
                DuctOpening a = c[i], b = c[j];
                if (a.N[0] * b.N[0] + a.N[1] * b.N[1] + a.N[2] * b.N[2] > -0.9) continue;
                double sa = a.Shape == "round" ? a.Dia : a.A, sb = b.Shape == "round" ? b.Dia : b.A;
                double ta = a.Shape == "round" ? a.Dia : a.B, tb = b.Shape == "round" ? b.Dia : b.B;
                if (a.Shape != b.Shape || Math.Abs(sa - sb) > 1 || Math.Abs(ta - tb) > 1) continue;
                double dx = b.C[0] - a.C[0], dy = b.C[1] - a.C[1], dz = b.C[2] - a.C[2];
                double d = Math.Sqrt(dx * dx + dy * dy + dz * dz) * 12, along = Math.Abs(dx * a.N[0] + dy * a.N[1] + dz * a.N[2]) * 12;
                double lateral = Math.Sqrt(Math.Max(0, d * d - along * along));
                if (lateral > Math.Max(3, 0.25 * Math.Min(sa, ta))) continue;
                if (d > bestD) { bestD = d; best = new[] { i, j }; }
            }
        return best;
    }
    public static string CsvCell(string v) { v = v ?? ""; return v.IndexOfAny(new[] { ',', '"', '\n' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v; }
    // The service field of a RunName ("A-<bldg>-<service>-DUCT-<n>" -> "<service>"), "" when it has no such field.
    public static string ServiceOf(string runName)
    {
        string[] f = (runName ?? "").Split('-');
        return f.Length >= 4 ? f[2].Trim() : "";
    }

    // a beam a -> b with a horizontal local y (vertical member: y = X); overlay / clean viewer files
    public static RawViewerWriter.BeamMember Beam(int id, int a, int b, int sec, Dictionary<int, Node> nodes)
    {
        var m = new RawViewerWriter.BeamMember();
        m.Id = id; m.NodeA = a; m.NodeB = b; m.SectionIndex = sec;
        Node na = nodes[a], nb = nodes[b];
        double dx = nb.xyz.X - na.xyz.X, dy = nb.xyz.Y - na.xyz.Y, dz = nb.xyz.Z - na.xyz.Z;
        double l = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (l > 0) { dx /= l; dy /= l; dz /= l; }
        // y = up x axis (horizontal, so z = x cross y is as vertical as possible); vertical member: y = X
        double yx = -dy, yy = dx, yz = 0;
        double yl = Math.Sqrt(yx * yx + yy * yy);
        m.LocalY = yl < 1e-6 ? new double[] { 1, 0, 0 } : new double[] { yx / yl, yy / yl, yz };
        return m;
    }

    // a viewer node at p (feet) in the file's frame (offset, scale)
    public static int MakeNode(Dictionary<int, Node> nodes, ref int next, double[] p, double[] off, double scale)
    {
        var n = new Node();
        n.id = next++;
        n.xyz.X = (float)((p[0] - off[0]) * scale);
        n.xyz.Y = (float)((p[1] - off[1]) * scale);
        n.xyz.Z = (float)((p[2] - off[2]) * scale);
        nodes[n.id] = n;
        return n.id;
    }
}
