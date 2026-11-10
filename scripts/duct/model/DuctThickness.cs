using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

// Wall thickness from a table [DuctThickness in duct-config.psd1] (user 2026-11-10). When a table is configured it is
// the ONLY thickness source: Navisworks WallThk_in is ignored, and a frame no row matches gets the unrealistic
// DuctGeom.ThkPlaceholder (0.001 in), so it has to be fixed in SAP or added to the table.
//
// CSV, header Run,Size,Thk_in:
//   Run    the system short name shown in the viewer groups (e.g. SYSA), case-insensitive. A frame of "SYSA
//          fragments" or "other: SYSA" matches SYSA.
//   Size   rectangular  WxH, each side a value or a range lo-hi (inclusive):  24x12   13-24x0-60   36x*
//                       either orientation matches (24x12 also matches a 12x24 duct)
//          round        D dia or lo-hi dia:  18 dia   8-36 dia
//          *            any size of that run
//   Thk_in wall thickness, inches
// A transition takes its larger end, as its SAP section does. Several rows matching one frame: an exact size beats a
// range, a range beats *; among equals the first row in the file wins (counted as an overlap in the note).
public class DuctThickness
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public class Row
    {
        public int Line; public string Run, SizeText; public double Thk;
        public bool Any, Round;
        public double[] A, B;            // value ranges [lo, hi]: rect A = one side, B = the other; round A = diameter
        public int Rank;                 // 0 exact, 1 range (any side a range or *), 2 '*'
        public int Hits;
    }

    public readonly List<Row> Rows = new List<Row>();
    public readonly string Path;
    public int Config, Placeholder, Overlaps;
    public readonly SortedDictionary<string, int> Unmatched = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public DuctThickness(string path)
    {
        Path = path;
        string[] lines = File.ReadAllLines(path);
        if (lines.Length == 0) throw new Exception("thickness table empty: " + path);
        string[] head = lines[0].Split(',').Select(s => s.Trim()).ToArray();
        if (head.Length < 3 || !head[0].Equals("Run", StringComparison.OrdinalIgnoreCase) || !head[1].Equals("Size", StringComparison.OrdinalIgnoreCase)
            || !head[2].Equals("Thk_in", StringComparison.OrdinalIgnoreCase))
            throw new Exception("thickness table header must be Run,Size,Thk_in: " + path);
        var bad = new List<string>();
        for (int i = 1; i < lines.Length; i++)
        {
            string ln = lines[i].Trim();
            if (ln == "" || ln.StartsWith("#")) continue;
            string[] c = ln.Split(',').Select(s => s.Trim()).ToArray();
            if (c.Length < 3) { bad.Add("line " + (i + 1) + ": 3 columns needed"); continue; }
            string why;
            Row r = ParseRow(i + 1, c[0], c[1], c[2], out why);
            if (r == null) { bad.Add("line " + (i + 1) + ": " + why); continue; }
            Rows.Add(r);
        }
        if (bad.Count > 0) throw new Exception("thickness table " + path + ": " + string.Join("; ", bad.Take(10).ToArray()));
    }

    static Row ParseRow(int line, string run, string size, string thk, out string why)
    {
        why = "";
        double t;
        if (run == "") { why = "Run empty"; return null; }
        if (!double.TryParse(thk, NumberStyles.Float, Inv, out t) || t <= 0) { why = "Thk_in '" + thk + "' is not a positive number"; return null; }
        var r = new Row { Line = line, Run = run, SizeText = size, Thk = t };
        string s = size.Trim().ToLowerInvariant();
        if (s == "*") { r.Any = true; r.Rank = 2; return r; }
        if (s.EndsWith("dia"))
        {
            r.Round = true;
            if ((r.A = Range(s.Substring(0, s.Length - 3).Trim())) == null) { why = "size '" + size + "' (round: D dia or lo-hi dia)"; return null; }
            r.Rank = r.A[0] == r.A[1] ? 0 : 1;
            return r;
        }
        string[] p = s.Split('x');
        if (p.Length != 2 || (r.A = Range(p[0].Trim())) == null || (r.B = Range(p[1].Trim())) == null)
        { why = "size '" + size + "' (rect: WxH with values or ranges lo-hi, round: D dia, or *)"; return null; }
        r.Rank = r.A[0] == r.A[1] && r.B[0] == r.B[1] ? 0 : 1;
        return r;
    }

    // "24" -> [24,24], "13-24" -> [13,24], "*" -> [0, inf]
    static double[] Range(string s)
    {
        if (s == "*") return new[] { 0.0, double.PositiveInfinity };
        double a, b; int k = s.IndexOf('-', 1);
        if (k < 0) return double.TryParse(s, NumberStyles.Float, Inv, out a) && a > 0 ? new[] { a, a } : null;
        if (!double.TryParse(s.Substring(0, k), NumberStyles.Float, Inv, out a) || !double.TryParse(s.Substring(k + 1), NumberStyles.Float, Inv, out b) || a < 0 || b < a) return null;
        return new[] { a, b };
    }

    // system name as the viewer shows it -> the run it matches ("SYSA fragments", "other: SYSA" -> SYSA)
    public static string RunOf(string system)
    {
        string s = (system ?? "").Trim();
        if (s.StartsWith("other: ", StringComparison.OrdinalIgnoreCase)) s = s.Substring(7);
        if (s.EndsWith(" fragments", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 10);
        return s.Trim();
    }

    const double Tol = 0.01;   // in: sizes are stated to 1/100 in at most
    static bool In(double v, double[] r) { return v >= r[0] - Tol && v <= r[1] + Tol; }

    // thickness for a run and a size (round: d; rect: w, h), NaN when no row matches
    public double Lookup(string system, bool round, double w, double h, double d)
    {
        string run = RunOf(system);
        Row best = null; int nBest = 0;
        foreach (Row r in Rows)
        {
            if (!r.Run.Equals(run, StringComparison.OrdinalIgnoreCase)) continue;
            bool hit = r.Any ? true
                     : r.Round ? round && !double.IsNaN(d) && In(d, r.A)
                     : !round && !double.IsNaN(w) && !double.IsNaN(h) && ((In(w, r.A) && In(h, r.B)) || (In(h, r.A) && In(w, r.B)));
            if (!hit) continue;
            if (best == null || r.Rank < best.Rank) { best = r; nBest = 1; }
            else if (r.Rank == best.Rank) nBest++;
        }
        if (best == null) return double.NaN;
        best.Hits++;
        if (nBest > 1) Overlaps++;
        return best.Thk;
    }

    // every sized, non-rigid frame: its thickness from the table, else NaN (-> the placeholder at output).
    // count = true on the final pass only (the report counts frames once)
    public void Apply(IEnumerable<CEdge> edges, bool count)
    {
        Config = Placeholder = Overlaps = 0; Unmatched.Clear(); foreach (Row r in Rows) r.Hits = 0;
        foreach (CEdge ce in edges)
        {
            if (ce.Rigid || ce.Dim == null) continue;
            double[] d = ce.Dim;
            if (ce.DimB != null && ce.DimB[0] == d[0] && (d[0] == 1 ? ce.DimB[3] > d[3] : ce.DimB[1] * ce.DimB[2] > d[1] * d[2])) d = ce.DimB;   // larger end
            string sys = DuctGeom.SystemOf(ce);
            double t = Lookup(sys, d[0] == 1, d[1], d[2], d[3]);
            ce.Thk = t;
            if (!count) continue;
            if (double.IsNaN(t))
            {
                Placeholder++;
                string key = RunOf(sys) + " " + (double.IsNaN(d[0] == 1 ? d[3] : d[1]) ? "unsized" : CleanModel.DimText(d));
                Unmatched[key] = (Unmatched.ContainsKey(key) ? Unmatched[key] : 0) + 1;
            }
            else Config++;
        }
    }

    public string Note()
    {
        var unused = Rows.Where(r => r.Hits == 0).Select(r => "line " + r.Line + " " + r.Run + " " + r.SizeText).ToList();
        return string.Format(Inv, "thickness from {0} ({1} rows; Navisworks thickness not used): {2} frames from the table, {3} frames PLACEHOLDER {4} in (no row){5}{6}{7}",
            System.IO.Path.GetFileName(Path), Rows.Count, Config, Placeholder, DuctGeom.ThkPlaceholder,
            Unmatched.Count > 0 ? "; no row for: " + string.Join(", ", Unmatched.Take(20).Select(kv => kv.Key + " (" + kv.Value + ")").ToArray()) + (Unmatched.Count > 20 ? " ..." : "") : "",
            Overlaps > 0 ? "; " + Overlaps + " frames matched more than one equal row (first in the file used)" : "",
            unused.Count > 0 ? "; rows matching nothing: " + string.Join(", ", unused.Take(10).ToArray()) + (unused.Count > 10 ? " ..." : "") : "");
    }
}
