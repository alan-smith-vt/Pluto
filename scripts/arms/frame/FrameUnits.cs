using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

// FrameUnits  --  the frame workbook's units (2026-11-05). A model holds its values in its own unit set (SAP's
// CurrUnits, "Kip, in, F"). A workbook header shows a column's unit in brackets ("X (in)", "E (kip/in2)"), made from the
// column's dimension; a header the user changed to another unit ("X (ft)", "E (ksi)") converts its column on reading.
//   dimensions  F force, L length, T temperature (a difference: dT, alpha), s time, rad angle; products with '-', one '/',
//               powers as digits ("F/L2", "F-s2/L4", "F-L/rad", "1/T")
//   units       lb lbf kip kips N kN kgf tonf | in ft mm cm m | F C | s | rad deg; ksi psi ksf psf pcf kcf Pa kPa MPa GPa
// Absolute temperatures do not appear in the SAP layer (only dT and alpha), so F <-> C converts by the factor alone.
// C# 5 (prefix Frame).

public static class FrameUnits
{
    // per unit: SI scale and exponents of {F, L, T, s, rad}
    static readonly Dictionary<string, double[]> Known = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase)
    {
        { "lb", D(4.4482216152605, 1, 0, 0, 0, 0) }, { "lbf", D(4.4482216152605, 1, 0, 0, 0, 0) },
        { "kip", D(4448.2216152605, 1, 0, 0, 0, 0) }, { "kips", D(4448.2216152605, 1, 0, 0, 0, 0) },
        { "N", D(1, 1, 0, 0, 0, 0) }, { "kN", D(1000, 1, 0, 0, 0, 0) }, { "kgf", D(9.80665, 1, 0, 0, 0, 0) }, { "tonf", D(9806.65, 1, 0, 0, 0, 0) }, { "ton", D(9806.65, 1, 0, 0, 0, 0) },
        { "in", D(0.0254, 0, 1, 0, 0, 0) }, { "ft", D(0.3048, 0, 1, 0, 0, 0) }, { "mm", D(0.001, 0, 1, 0, 0, 0) }, { "cm", D(0.01, 0, 1, 0, 0, 0) }, { "m", D(1, 0, 1, 0, 0, 0) },
        { "F", D(5.0 / 9.0, 0, 0, 1, 0, 0) }, { "C", D(1, 0, 0, 1, 0, 0) },
        { "s", D(1, 0, 0, 0, 1, 0) }, { "sec", D(1, 0, 0, 0, 1, 0) },
        { "rad", D(1, 0, 0, 0, 0, 1) }, { "deg", D(Math.PI / 180, 0, 0, 0, 0, 1) },
        { "ksi", D(4448.2216152605 / (0.0254 * 0.0254), 1, -2, 0, 0, 0) }, { "psi", D(4.4482216152605 / (0.0254 * 0.0254), 1, -2, 0, 0, 0) },
        { "ksf", D(4448.2216152605 / (0.3048 * 0.3048), 1, -2, 0, 0, 0) }, { "psf", D(4.4482216152605 / (0.3048 * 0.3048), 1, -2, 0, 0, 0) },
        { "pcf", D(4.4482216152605 / (0.3048 * 0.3048 * 0.3048), 1, -3, 0, 0, 0) }, { "kcf", D(4448.2216152605 / (0.3048 * 0.3048 * 0.3048), 1, -3, 0, 0, 0) },
        { "Pa", D(1, 1, -2, 0, 0, 0) }, { "kPa", D(1e3, 1, -2, 0, 0, 0) }, { "MPa", D(1e6, 1, -2, 0, 0, 0) }, { "GPa", D(1e9, 1, -2, 0, 0, 0) },
    };
    static double[] D(double scale, double f, double l, double t, double s, double r) { return new[] { scale, f, l, t, s, r }; }

    // the model's unit set -> its force, length and temperature units ("Kip, in, F" -> kip, in, F)
    public static void Parse(string units, out string force, out string length, out string temp)
    {
        var p = (units ?? "").Split(',').Select(x => x.Trim()).ToArray();
        force = p.Length > 0 && p[0].Length > 0 ? p[0].ToLowerInvariant() : "kip";
        if (force == "kgf" || force == "kn" || force == "n" || force == "tonf") force = force == "kn" ? "kN" : force == "n" ? "N" : force;
        length = p.Length > 1 && p[1].Length > 0 ? p[1] : "in";
        temp = p.Length > 2 && p[2].Length > 0 ? p[2].ToUpperInvariant() : "F";
    }

    // the header unit of a dimension in the model's unit set ("F/L2", "Kip, in, F" -> "kip/in2"); "" for none
    public static string Label(string dim, string units)
    {
        if (string.IsNullOrEmpty(dim)) return "";
        string f, l, t;
        Parse(units, out f, out l, out t);
        var sb = new System.Text.StringBuilder();
        foreach (char c in dim)
        {
            if (c == 'F') sb.Append(f);
            else if (c == 'L') sb.Append(l);
            else if (c == 'T') sb.Append(t);
            else sb.Append(c);
        }
        return sb.ToString();
    }

    // the factor turning a value in `unit` into the model's unit for `dim`; NaN when the unit is not of that dimension
    // or does not parse. A unit equal to the model's label is 1 exactly.
    public static double Factor(string unit, string dim, string units)
    {
        string mine = Label(dim, units);
        if (string.Equals((unit ?? "").Replace(" ", ""), mine.Replace(" ", ""), StringComparison.OrdinalIgnoreCase)) return 1;
        double[] a = Of(unit), b = Of(mine);
        if (a == null || b == null) return double.NaN;
        for (int i = 1; i < 6; i++) if (Math.Abs(a[i] - b[i]) > 1e-12) return double.NaN;
        // 15 significant digits: 0.3048 / 0.0254 is 12.000000000000002 in doubles, and 0.5 ft must read 6 in exactly
        return double.Parse((a[0] / b[0]).ToString("G15", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }

    // a value converted from another unit, to 12 significant digits (50.4 lb/ft is 0.0042 kip/in, not 0.0041999999999999983)
    public static double Converted(double v, double factor)
    {
        if (factor == 1) return v;
        double x = v * factor;
        return x == 0 || double.IsNaN(x) || double.IsInfinity(x) ? x : double.Parse(x.ToString("G12", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }

    // a unit expression -> {scale, F, L, T, s, rad}; null when it does not parse
    public static double[] Of(string unit)
    {
        string u = (unit ?? "").Trim().Replace("^", "").Replace("*", "-").Replace(" ", "");
        if (u.Length == 0) return null;
        var o = D(1, 0, 0, 0, 0, 0);
        string[] parts = u.Split('/');
        if (parts.Length > 2) return null;
        for (int side = 0; side < parts.Length; side++)
            foreach (string tok in parts[side].Split(new[] { '-' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (tok == "1") continue;
                int k = tok.Length;
                while (k > 0 && char.IsDigit(tok[k - 1])) k--;
                string name = tok.Substring(0, k);
                double pw = k < tok.Length ? double.Parse(tok.Substring(k), CultureInfo.InvariantCulture) : 1;
                double[] d;
                if (!Known.TryGetValue(name, out d)) return null;
                double sg = side == 0 ? pw : -pw;
                o[0] *= Math.Pow(d[0], sg);
                for (int i = 1; i < 6; i++) o[i] += d[i] * sg;
            }
        return o;
    }
}
