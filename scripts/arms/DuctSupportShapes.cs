using System;
using System.Collections.Generic;

// Support icons as triangles (shell geometry): a solid 3D icon per support type, placed above the duct at the
// support and scaled to the duct. C# 5 / Add-Type (PowerShell 5.1) compatible.
//
// Frame of a support: a = duct axis (unit), u = duct width direction (horizontal, unit, square to a), v = a x u (up).
// c = duct centreline point at the support, W across (along u), H up (along v), inches.
//   guide:  a square ring square to the duct with a double-headed arrow along the duct through it (slides axially,
//           held laterally and vertically)
//   anchor: a solid cube on a downward pyramid (held in every direction)
//   other:  a solid octahedron (unknown type)
public static class DuctSupportShapes
{
    public class Settings
    {
        public double SizeOfWidth = 0.6;    // icon size = this x the duct width, clamped to [MinIn, MaxIn]
        public double MinIn = 10, MaxIn = 30;
        public double LiftIn = 3;           // icon bottom above the duct top
        public int Segments = 16;           // round parts (arrow shaft and heads)
    }

    public static double IconSize(double W, Settings s) { return Math.Max(s.MinIn, Math.Min(s.MaxIn, W * s.SizeOfWidth)); }

    public static List<double[]> Icon(string type, double[] c, double[] a, double[] u, double W, double H, Settings s)
    {
        double[] v = Cross(a, u);
        double z = IconSize(W, s);
        double[] o = Add(c, v, H / 2 + s.LiftIn + z / 2);   // icon centre
        var t = new List<double[]>();
        if (type == "guide") Guide(t, o, a, u, v, z, s);
        else if (type == "anchor") Anchor(t, o, a, u, v, z);
        else Octahedron(t, o, a, u, v, z / 2);
        return t;
    }

    // ring: four bars (0.15 z thick, 0.3 z deep along the duct); arrow: 2.2 z long, cone heads
    static void Guide(List<double[]> t, double[] o, double[] a, double[] u, double[] v, double z, Settings s)
    {
        double h = z / 2, b = 0.15 * z, d = 0.15 * z;
        Box(t, Add(o, v, h - b / 2), a, u, v, d, h, b / 2);
        Box(t, Add(o, v, -h + b / 2), a, u, v, d, h, b / 2);
        Box(t, Add(o, u, h - b / 2), a, u, v, d, b / 2, h - b);
        Box(t, Add(o, u, -h + b / 2), a, u, v, d, b / 2, h - b);
        double L = 1.1 * z, head = 0.35 * z, rs = 0.06 * z, rh = 0.16 * z;
        Cylinder(t, Add(o, a, -(L - head)), Add(o, a, L - head), u, v, rs, s.Segments);
        Cone(t, Add(o, a, L - head), Add(o, a, L), u, v, rh, s.Segments);
        Cone(t, Add(o, a, -(L - head)), Add(o, a, -L), u, v, rh, s.Segments);
    }

    static void Anchor(List<double[]> t, double[] o, double[] a, double[] u, double[] v, double z)
    {
        double h = 0.35 * z;
        Box(t, Add(o, v, 0.15 * z), a, u, v, h, h, h);
        double[] b = Add(o, v, 0.15 * z - h), tip = Add(o, v, -z / 2);
        double[][] q = { Add(Add(b, a, -h), u, -h), Add(Add(b, a, h), u, -h), Add(Add(b, a, h), u, h), Add(Add(b, a, -h), u, h) };
        for (int i = 0; i < 4; i++) t.Add(Tri(q[i], q[(i + 1) % 4], tip));
    }

    static void Octahedron(List<double[]> t, double[] o, double[] a, double[] u, double[] v, double r)
    {
        double[] top = Add(o, v, r), bot = Add(o, v, -r);
        double[][] q = { Add(o, a, r), Add(o, u, r), Add(o, a, -r), Add(o, u, -r) };
        for (int i = 0; i < 4; i++) { t.Add(Tri(q[i], q[(i + 1) % 4], top)); t.Add(Tri(q[(i + 1) % 4], q[i], bot)); }
    }

    // closed box: centre o, half sizes ha / hu / hv along a / u / v (12 triangles)
    public static void Box(List<double[]> t, double[] o, double[] a, double[] u, double[] v, double ha, double hu, double hv)
    {
        var p = new double[8][];
        for (int i = 0; i < 8; i++)
            p[i] = Add(Add(Add(o, a, (i & 1) != 0 ? ha : -ha), u, (i & 2) != 0 ? hu : -hu), v, (i & 4) != 0 ? hv : -hv);
        int[][] f = { new[] { 0, 2, 3, 1 }, new[] { 4, 5, 7, 6 }, new[] { 0, 1, 5, 4 }, new[] { 2, 6, 7, 3 }, new[] { 0, 4, 6, 2 }, new[] { 1, 3, 7, 5 } };
        foreach (int[] q in f) { t.Add(Tri(p[q[0]], p[q[1]], p[q[2]])); t.Add(Tri(p[q[0]], p[q[2]], p[q[3]])); }
    }

    // closed cylinder p0 -> p1, radius r, n sides (x, y = two unit vectors square to the axis)
    public static void Cylinder(List<double[]> t, double[] p0, double[] p1, double[] x, double[] y, double r, int n)
    {
        for (int i = 0; i < n; i++)
        {
            double[] r0 = Ring(x, y, r, i, n), r1 = Ring(x, y, r, i + 1, n);
            double[] a0 = Plus(p0, r0), a1 = Plus(p0, r1), b0 = Plus(p1, r0), b1 = Plus(p1, r1);
            t.Add(Tri(a0, a1, b1)); t.Add(Tri(a0, b1, b0));
            t.Add(Tri(p0, a1, a0)); t.Add(Tri(p1, b0, b1));
        }
    }

    // closed cone: base centre p0 (radius r) -> tip p1
    public static void Cone(List<double[]> t, double[] p0, double[] p1, double[] x, double[] y, double r, int n)
    {
        for (int i = 0; i < n; i++)
        {
            double[] a0 = Plus(p0, Ring(x, y, r, i, n)), a1 = Plus(p0, Ring(x, y, r, i + 1, n));
            t.Add(Tri(a0, a1, p1)); t.Add(Tri(p0, a1, a0));
        }
    }

    static double[] Ring(double[] x, double[] y, double r, int i, int n)
    {
        double f = 2 * Math.PI * i / n, cs = Math.Cos(f) * r, sn = Math.Sin(f) * r;
        return new[] { x[0] * cs + y[0] * sn, x[1] * cs + y[1] * sn, x[2] * cs + y[2] * sn };
    }

    static double[] Add(double[] p, double[] d, double k) { return new[] { p[0] + d[0] * k, p[1] + d[1] * k, p[2] + d[2] * k }; }
    static double[] Plus(double[] p, double[] q) { return new[] { p[0] + q[0], p[1] + q[1], p[2] + q[2] }; }
    static double[] Tri(double[] p, double[] q, double[] r) { return new[] { p[0], p[1], p[2], q[0], q[1], q[2], r[0], r[1], r[2] }; }

    public static double[] Cross(double[] x, double[] y)
    {
        return new[] { x[1] * y[2] - x[2] * y[1], x[2] * y[0] - x[0] * y[2], x[0] * y[1] - x[1] * y[0] };
    }
}
