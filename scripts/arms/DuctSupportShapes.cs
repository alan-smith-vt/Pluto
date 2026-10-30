using System;
using System.Collections.Generic;

// Support shapes as triangles (shell overlay geometry), one shape per support type.
// C# 5 / Add-Type (PowerShell 5.1) compatible.
//
// Frame of a support: a = duct axis (unit), u = duct width direction (unit, square to a), v = a x u (up for a
// horizontal duct). c = duct centreline point over the steel, W along u, H along v. steelTop = the steel top
// elevation as a distance below c along -v (>= H/2: the duct bottom is at c - v*H/2).
public static class DuctSupportShapes
{
    public class Settings
    {
        public double GuideLen = 6;      // in, along the duct
        public double Clear = 0.25;      // in, plate to duct face
        public double Ear = 2;           // in, base plate beyond the side plates
    }

    // Guide: an open box of plate around the duct (base plate on the steel, two side plates, top plate),
    // short along the duct and open at both ends. Returns triangles, 9 doubles each.
    public static List<double[]> Guide(double[] c, double[] a, double[] u, double W, double H, double steelBelow, Settings s)
    {
        double[] v = Cross(a, u);
        double hl = s.GuideLen / 2, hw = W / 2 + s.Clear, top = H / 2 + s.Clear, bot = -Math.Max(steelBelow, H / 2 + s.Clear);
        var t = new List<double[]>();
        // base plate (sits on the steel top), side plates (base to top plate), top plate
        Quad(t, c, a, u, v, -hl, hl, -(hw + s.Ear), hw + s.Ear, bot, bot, false);
        Quad(t, c, a, u, v, -hl, hl, -hw, -hw, bot, top, true);
        Quad(t, c, a, u, v, -hl, hl, hw, hw, bot, top, true);
        Quad(t, c, a, u, v, -hl, hl, -hw, hw, top, top, false);
        return t;
    }

    // a quad in the support frame: a from a0 to a1; either u fixed (u0 = u1) spanning v0..v1, or v fixed spanning u0..u1
    static void Quad(List<double[]> t, double[] c, double[] a, double[] u, double[] v,
        double a0, double a1, double u0, double u1, double v0, double v1, bool uFixed)
    {
        double[] p00, p01, p10, p11;
        if (uFixed) { p00 = P(c, a, u, v, a0, u0, v0); p01 = P(c, a, u, v, a0, u0, v1); p10 = P(c, a, u, v, a1, u0, v0); p11 = P(c, a, u, v, a1, u0, v1); }
        else { p00 = P(c, a, u, v, a0, u0, v0); p01 = P(c, a, u, v, a0, u1, v0); p10 = P(c, a, u, v, a1, u0, v0); p11 = P(c, a, u, v, a1, u1, v0); }
        t.Add(Tri(p00, p10, p11)); t.Add(Tri(p00, p11, p01));
    }

    static double[] P(double[] c, double[] a, double[] u, double[] v, double ka, double ku, double kv)
    {
        return new[] { c[0] + a[0] * ka + u[0] * ku + v[0] * kv, c[1] + a[1] * ka + u[1] * ku + v[1] * kv, c[2] + a[2] * ka + u[2] * ku + v[2] * kv };
    }

    static double[] Tri(double[] p, double[] q, double[] r) { return new[] { p[0], p[1], p[2], q[0], q[1], q[2], r[0], r[1], r[2] }; }

    public static double[] Cross(double[] x, double[] y)
    {
        return new[] { x[1] * y[2] - x[2] * y[1], x[2] * y[0] - x[0] * y[2], x[0] * y[1] - x[1] * y[0] };
    }
}
