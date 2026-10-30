using System;
using System.Collections.Generic;

// Vector helpers of the duct converter (feet or inches, whatever the caller uses): one copy for every piece.
public static class DuctMath
{
    public static double Dot3(double[] a, double[] b) { return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]; }
    public static double[] Unit(double[] a) { double l = Math.Sqrt(Dot3(a, a)); return l < 1e-12 ? new double[] { 0, 0, 0 } : new[] { a[0] / l, a[1] / l, a[2] / l }; }
    public static double Len(double[] a, double[] b) { return Math.Sqrt(Dist2(a, b)); }
    public static double[] Lerp(double[] a, double[] b, double t) { return new[] { a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, a[2] + (b[2] - a[2]) * t }; }
    public static double Dist2(double[] p, double[] q) { double dx = p[0] - q[0], dy = p[1] - q[1], dz = p[2] - q[2]; return dx * dx + dy * dy + dz * dz; }
    public static double[] Sub3(double[] p, double[] q) { return new[] { p[0] - q[0], p[1] - q[1], p[2] - q[2] }; }
    public static double[] Add3(double[] p, double[] q) { return new[] { p[0] + q[0], p[1] + q[1], p[2] + q[2] }; }
    public static double[] Scale3(double[] p, double s) { return new[] { p[0] * s, p[1] * s, p[2] * s }; }
    public static double[] Cross3(double[] p, double[] q) { return new[] { p[1] * q[2] - p[2] * q[1], p[2] * q[0] - p[0] * q[2], p[0] * q[1] - p[1] * q[0] }; }

    // t along a->b (0..1 inside), lat = distance from the line
    public static void Project(double[] a, double[] b, double[] p, out double t, out double lat)
    {
        double[] ab = Sub3(b, a); double L2 = Dot3(ab, ab);
        t = L2 < 1e-12 ? 0 : Dot3(Sub3(p, a), ab) / L2;
        lat = Math.Sqrt(Dist2(Lerp(a, b, t), p));
    }
}
