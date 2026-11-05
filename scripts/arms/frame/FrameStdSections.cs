using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

// FrameStdSections  --  a SAP frame section's analysis properties for the STAAD export (2026-11-05). SAP's own export
// gives every section's computed properties (Area TorsConst I33 I22 I23 AS2 AS3); those are taken as they are. A section
// made in the workbook has only its dimensions, so they are worked out here the way SAP does (checked against SAP 24.2's
// own values for every shape of the probe, vault/format/frame-to-staad.md):
//   Rectangular  A = t2 t3; I33 = t2 t3^3 / 12; J by the rectangle series; AS2 = AS3 = 5/6 A
//   Circle       A = pi d^2 / 4; I = pi d^4 / 64; J = 2 I; AS = 0.9 A
//   Pipe         A, I of the annulus; J = 2 I; AS2 = AS3 = A / 2
//   Box/Tube     A, I of the hollow rectangle; J by Bredt's thin-wall formula; AS2 = 2 t3 tw, AS3 = 2 t2 tf
//   I            unequal flanges (t2 tf top, t2b tfb bottom); J = sum of rectangles (each b t^3 [1/3 - 0.21 t/b ...]),
//                web between the flanges; AS2 = t3 tw, AS3 = 5/6 (t2 tf + t2b tfb)
//   Channel      J as for I; AS2 = t3 tw, AS3 = 2 t2 tf
//   Tee, Angle, Double Angle   A and I exact; J by the same rectangles, which SAP's own differs from by a junction term
//                (a notice says so); AS as SAP: tee 5/6 t2 tf, angle t2 tf, double angle 2 x the legs
// Angles keep their I23 (geometric axes): STAAD's prismatic property has none, so an angle goes by its table name or is
// refused (FrameStd). C# 5 (prefix Frame).

public sealed class FrameSectionProps
{
    public double A, J, I33, I22, I23, AS2, AS3;
    public double Depth, Width;              // along local 2 (t3) and 3 (t2)
    public bool Computed;                    // false: SAP's own values
    public bool EstimatedJ;                  // J by rectangles where SAP adds a junction term
}

public static class FrameStdSections
{

    static double Num(FrameSection s, string k) { double v = s.Dim(k); return double.IsNaN(v) ? 0 : v; }

    // SAP's computed values when the section row has them all, else from the dimensions; null when neither can be had
    public static FrameSectionProps Of(FrameSection s, out string why)
    {
        why = null;
        var r = s.Rec(FrameSection.General);
        string shape = (s.Shape ?? "").Trim().ToLowerInvariant();
        var p = new FrameSectionProps { Depth = Num(s, "t3"), Width = Num(s, "t2") };
        if (r != null && r.Has("Area") && r.Has("I33") && r.Has("I22") && r.Has("TorsConst") && (Num(s, "Area") > 0 || shape == "general"))
        {
            p.A = Num(s, "Area"); p.J = Num(s, "TorsConst"); p.I33 = Num(s, "I33"); p.I22 = Num(s, "I22"); p.I23 = Num(s, "I23");
            p.AS2 = Num(s, "AS2"); p.AS3 = Num(s, "AS3");
            if (!r.Has("AS2") && !r.Has("AS3")) { double as2, as3; Shear(shape, s, p.A, out as2, out as3); p.AS2 = as2; p.AS3 = as3; }
            return p;
        }
        p.Computed = true;
        double d = Num(s, "t3"), b = Num(s, "t2"), tf = Num(s, "tf"), tw = Num(s, "tw");
        switch (shape)
        {
            case "rectangular":
                if (d <= 0 || b <= 0) { why = "no t3 / t2"; return null; }
                p.A = b * d; p.I33 = b * d * d * d / 12; p.I22 = d * b * b * b / 12; p.J = Rect(Math.Max(b, d), Math.Min(b, d), true);
                p.AS2 = p.AS3 = 5.0 / 6 * p.A;
                return p;
            case "circle":
                if (d <= 0) { why = "no t3"; return null; }
                p.A = Math.PI * d * d / 4; p.I33 = p.I22 = Math.PI * Math.Pow(d, 4) / 64; p.J = 2 * p.I33; p.AS2 = p.AS3 = 0.9 * p.A; p.Width = d;
                return p;
            case "pipe":
            {
                if (d <= 0 || tw <= 0 || 2 * tw > d) { why = "no t3 / tw"; return null; }
                double di = d - 2 * tw;
                p.A = Math.PI * (d * d - di * di) / 4; p.I33 = p.I22 = Math.PI * (Math.Pow(d, 4) - Math.Pow(di, 4)) / 64; p.J = 2 * p.I33;
                p.AS2 = p.AS3 = p.A / 2; p.Width = d;
                return p;
            }
            case "box/tube":
            {
                if (d <= 0 || b <= 0 || tf <= 0 || tw <= 0) { why = "no t3 / t2 / tf / tw"; return null; }
                double bi = b - 2 * tw, hi = d - 2 * tf;
                p.A = b * d - bi * hi; p.I33 = (b * Math.Pow(d, 3) - bi * Math.Pow(hi, 3)) / 12; p.I22 = (d * Math.Pow(b, 3) - hi * Math.Pow(bi, 3)) / 12;
                p.J = 2 * tf * tw * Math.Pow(b - tw, 2) * Math.Pow(d - tf, 2) / (b * tw + d * tf - tw * tw - tf * tf);
                p.AS2 = 2 * d * tw; p.AS3 = 2 * b * tf;
                return p;
            }
            case "i/wide flange":
            {
                double b2 = Num(s, "t2b"), tf2 = Num(s, "tfb");
                if (b2 <= 0) b2 = b; if (tf2 <= 0) tf2 = tf;
                if (d <= 0 || b <= 0 || tf <= 0 || tw <= 0) { why = "no t3 / t2 / tf / tw"; return null; }
                double hw = d - tf - tf2;
                var parts = new[] { Part(b, tf, d - tf / 2, 0), Part(b2, tf2, tf2 / 2, 0), Part(tw, hw, tf2 + hw / 2, 0) };
                Sum(p, parts);
                p.J = Rect(b, tf, true) + Rect(b2, tf2, true) + Rect(hw, tw, true);
                p.AS2 = d * tw; p.AS3 = 5.0 / 6 * (b * tf + b2 * tf2);
                return p;
            }
            case "channel":
            {
                if (d <= 0 || b <= 0 || tf <= 0 || tw <= 0) { why = "no t3 / t2 / tf / tw"; return null; }
                double hw = d - 2 * tf;
                Sum(p, new[] { Part(b, tf, d - tf / 2, b / 2), Part(b, tf, tf / 2, b / 2), Part(tw, hw, d / 2, tw / 2) });
                p.J = 2 * Rect(b, tf, true) + Rect(hw, tw, true);
                p.AS2 = d * tw; p.AS3 = 2 * b * tf;
                return p;
            }
            case "tee":
            {
                if (d <= 0 || b <= 0 || tf <= 0 || tw <= 0) { why = "no t3 / t2 / tf / tw"; return null; }
                double hw = d - tf;
                Sum(p, new[] { Part(b, tf, d - tf / 2, 0), Part(tw, hw, hw / 2, 0) });
                p.J = Rect(b, tf, true) + Rect(hw, tw, true); p.EstimatedJ = true;
                p.AS2 = d * tw; p.AS3 = 5.0 / 6 * b * tf;
                return p;
            }
            case "angle":
            {
                if (d <= 0 || b <= 0 || tf <= 0 || tw <= 0) { why = "no t3 / t2 / tf / tw"; return null; }
                double hv = d - tf;
                // the horizontal leg (t2 x tf) at the bottom, toward -3; the vertical leg (tw x the rest) at the heel (SAP's
                // orientation: its I23 is positive)
                Sum(p, new[] { Part(b, tf, tf / 2, -b / 2), Part(tw, hv, tf + hv / 2, -tw / 2) });
                p.J = Rect(b, tf, true) + Rect(hv, tw, true); p.EstimatedJ = true;
                p.AS2 = d * tw; p.AS3 = b * tf;
                return p;
            }
            case "double angle":
            {
                double dis = Num(s, "dis");
                if (d <= 0 || b <= 0 || tf <= 0 || tw <= 0) { why = "no t3 / t2 / tf / tw / dis"; return null; }
                double w = (b - dis) / 2, hv = d - tf;
                Sum(p, new[] { Part(w, tf, tf / 2, -dis / 2 - w / 2), Part(tw, hv, tf + hv / 2, -dis / 2 - tw / 2),
                               Part(w, tf, tf / 2, dis / 2 + w / 2), Part(tw, hv, tf + hv / 2, dis / 2 + tw / 2) });
                p.I23 = 0;
                p.J = 2 * (Rect(w, tf, true) + Rect(hv, tw, true)); p.EstimatedJ = true;
                p.AS2 = 2 * d * tw; p.AS3 = 2 * w * tf;
                return p;
            }
        }
        why = "a " + (s.Shape ?? "(blank)") + " section with no computed properties (Area, I33, I22, TorsConst)";
        return null;
    }

    static void Shear(string shape, FrameSection s, double a, out double as2, out double as3)
    {
        as2 = as3 = 0;
        if (shape == "rectangular") { as2 = as3 = 5.0 / 6 * a; }
        else if (shape == "circle") { as2 = as3 = 0.9 * a; }
        else if (shape == "pipe") { as2 = as3 = a / 2; }
    }

    // a rectangle's torsion constant: the series for a solid rectangle (long side a, short side t)
    static double Rect(double a, double t, bool reduce)
    {
        if (a < t) { double x = a; a = t; t = x; }
        if (!reduce) return a * t * t * t / 3;
        return a * t * t * t * (1.0 / 3 - 0.21 * (t / a) * (1 - Math.Pow(t / a, 4) / 12));
    }

    // a rectangle of width w (along 3) and height h (along 2) whose centre is at (y along 2, z along 3)
    static double[] Part(double w, double h, double y, double z) { return new[] { w, h, y, z }; }

    // area, centroidal I33 (about 3: y distances), I22 (about 2: z distances) and I23 of rectangles
    static void Sum(FrameSectionProps p, double[][] parts)
    {
        double a = 0, ay = 0, az = 0;
        foreach (var q in parts) { double ar = q[0] * q[1]; a += ar; ay += ar * q[2]; az += ar * q[3]; }
        double yc = ay / a, zc = az / a, i33 = 0, i22 = 0, i23 = 0;
        foreach (var q in parts)
        {
            double ar = q[0] * q[1], dy = q[2] - yc, dz = q[3] - zc;
            i33 += q[0] * Math.Pow(q[1], 3) / 12 + ar * dy * dy;
            i22 += q[1] * Math.Pow(q[0], 3) / 12 + ar * dz * dz;
            i23 += ar * dy * dz;
        }
        p.A = a; p.I33 = i33; p.I22 = i22; p.I23 = Math.Abs(i23) < 1e-9 * (i33 + i22) ? 0 : i23;
    }
}
