// Units.cs -- the unit words CalcFlow reads (a template's input units, the package's force units, a section table's
// units) and conversions through one base set: length in, force kip, moment kip-in, stress ksi, angle deg.
// A value only ever converts within its dimension; asking for another is refused, so a moment can never be written
// into a force cell. C# 5 (Windows PowerShell 5.1 Add-Type, warnings are errors).

using System;
using System.Collections.Generic;
using System.Globalization;

namespace CalcFlow
{
    public enum Dimension { None, Length, Area, Inertia, Force, Moment, Stress, Angle, Text }

    public sealed class Unit
    {
        public string Name = "";
        public Dimension Dim = Dimension.None;
        public double ToBase = 1;      // value * ToBase = value in the base unit of its dimension

        public override string ToString() { return Name.Length == 0 ? "(none)" : Name; }
    }

    public static class Units
    {
        static readonly Dictionary<string, Unit> Known = new Dictionary<string, Unit>(StringComparer.OrdinalIgnoreCase);

        static Units()
        {
            const double Kn = 0.224808943099710;      // kip per kN
            const double M = 39.3700787401575;        // in per m
            Add(Dimension.None, 1, "", "-", "none", "1");
            Add(Dimension.Text, 1, "text");
            Add(Dimension.Length, 1, "in", "in.", "inch", "inches");
            Add(Dimension.Length, 12, "ft", "ft.", "feet", "foot");
            Add(Dimension.Length, 1 / 25.4, "mm");
            Add(Dimension.Length, 1 / 2.54, "cm");
            Add(Dimension.Length, M, "m");
            Add(Dimension.Area, 1, "in2", "in^2", "in\u00b2");
            Add(Dimension.Area, 1 / (25.4 * 25.4), "mm2", "mm^2", "mm\u00b2");
            Add(Dimension.Inertia, 1, "in4", "in^4", "in\u2074");
            Add(Dimension.Inertia, 1 / Math.Pow(25.4, 4), "mm4", "mm^4", "mm\u2074");
            Add(Dimension.Force, 1, "kip", "kips", "k");
            Add(Dimension.Force, 0.001, "lbf", "lb", "lbs");
            Add(Dimension.Force, Kn, "kN");
            Add(Dimension.Force, Kn / 1000, "N");
            Add(Dimension.Force, 2.20462262184878, "mton");      // metric ton-force (STAAD's MTON) in kip
            Add(Dimension.Moment, 1, "kip-in", "k-in", "kip.in", "kip*in", "in-kip");
            Add(Dimension.Moment, 12, "kip-ft", "k-ft", "kip.ft", "kip*ft", "ft-kip");
            Add(Dimension.Moment, 0.001, "lbf-in", "lb-in", "in-lb");
            Add(Dimension.Moment, 0.012, "lbf-ft", "lb-ft", "ft-lb");
            Add(Dimension.Moment, Kn * M, "kN-m", "kNm", "kN.m");
            Add(Dimension.Moment, Kn / 1000 * M, "N-m", "Nm");
            Add(Dimension.Moment, Kn / 1000 / 25.4, "N-mm", "Nmm");
            Add(Dimension.Stress, 1, "ksi");
            Add(Dimension.Stress, 0.001, "psi");
            Add(Dimension.Stress, 0.145037737730209, "MPa", "N/mm2", "N/mm^2");
            Add(Dimension.Angle, 1, "deg", "degree", "degrees", "\u00b0");
            Add(Dimension.Angle, 180 / Math.PI, "rad");
        }

        static void Add(Dimension d, double toBase, params string[] names)
        {
            foreach (string n in names)
            {
                var u = new Unit();
                u.Name = names[0]; u.Dim = d; u.ToBase = toBase;
                Known[n] = u;
            }
        }

        // a unit word as written ("kip-ft", "in.", "(ksi)"); false for one CalcFlow does not know
        public static bool TryParse(string text, out Unit unit)
        {
            string t = (text ?? "").Trim();
            if (t.StartsWith("(") && t.EndsWith(")")) t = t.Substring(1, t.Length - 2).Trim();
            return Known.TryGetValue(t, out unit);
        }

        public static Unit Parse(string text)
        {
            Unit u;
            if (!TryParse(text, out u)) throw new FormatException("unknown unit '" + text + "'");
            return u;
        }

        public static Unit Base(Dimension d)
        {
            switch (d)
            {
                case Dimension.Length: return Parse("in");
                case Dimension.Area: return Parse("in2");
                case Dimension.Inertia: return Parse("in4");
                case Dimension.Force: return Parse("kip");
                case Dimension.Moment: return Parse("kip-in");
                case Dimension.Stress: return Parse("ksi");
                case Dimension.Angle: return Parse("deg");
                case Dimension.Text: return Parse("text");
                default: return Parse("");
            }
        }

        public static double Convert(double value, Unit from, Unit to)
        {
            if (from.Dim != to.Dim)
                throw new InvalidOperationException("a " + from.Dim.ToString().ToLowerInvariant() + " (" + from + ") cannot go into a " +
                    to.Dim.ToString().ToLowerInvariant() + " cell (" + to + ")");
            return value * from.ToBase / to.ToBase;
        }

        public static string Num(double v)
        {
            return (v + 0.0).ToString("R", CultureInfo.InvariantCulture);
        }

        // the unit a label carries in brackets: "wleg (in.)" -> in, "Fu,Base (ksi)" -> ksi; "" when none
        public static string FromLabel(string label)
        {
            if (string.IsNullOrEmpty(label)) return "";
            int close = label.LastIndexOf(')');
            int open = close > 0 ? label.LastIndexOf('(', close) : -1;
            if (open < 0) return "";
            Unit u;
            return TryParse(label.Substring(open + 1, close - open - 1), out u) ? u.Name : "";
        }
    }
}
