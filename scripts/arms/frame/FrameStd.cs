using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

// FrameStd  --  a SAP frame model as a STAAD.Pro input file (.std), for Build-Frame -Std (2026-11-05; decisions in
// vault/decisions/2026-11-05-frame-staad.md, the mapping in vault/format/frame-to-staad.md). Exact or refused: what
// STAAD holds exactly is written, what it cannot is refused (every refusal at once, each naming its objects), and
// what leaving out cannot change the structure (modal cases, envelopes) is listed as not written.
//   axes        STAAD's Y is up: SAP (X, Y, Z) is written (X, Z, -Y); a BETA per member keeps SAP's local 2 / 3 as
//               STAAD's y / z (STAAD's BETA-0 axes: z horizontal, y up; a vertical member's z along +Z)
//   ids         SapLabels: an integer label keeps its number, any other gets the next free one; members, plates and
//               the truss members of axial links share one sequence; the header lists every label that moved
//   sections    TABLE ST for W shapes (and channels of whole weight) that SAP took from its table; everything else
//               PRIS with SAP's own properties (FrameStdSections) and the frame's modifiers folded in
//   links       linear only (a nonlinear link refuses the model): a grounded link is support springs, a two-joint
//               link acting along its axis only is a truss member with EA / L = KE
//   loads       patterns -> reference loads, cases -> primary loads (a case that continues another holds both cases'
//               loads, from zero), Linear Add / SRSS / Abs Add combinations; ground displacements in the primary load
// C# 5 (prefix Frame).

public sealed class FrameStdRefusal : Exception
{
    public readonly List<string> Items;
    public FrameStdRefusal(List<string> items) : base(Text(items)) { Items = items; }
    static string Text(List<string> items)
    {
        return "STAAD cannot hold this model exactly (" + items.Count.ToString(CultureInfo.InvariantCulture) + " item" + (items.Count == 1 ? "" : "s") + "): " +
               string.Join(" | ", items.ToArray());
    }
}

public static class FrameStd
{
    public static void Write(FrameModel m, string path, string source, List<string> notes)
    {
        File.WriteAllText(path, ToText(m, source, notes), new UTF8Encoding(false));
    }

    // the .std text (CRLF); notes gets every notice; throws FrameStdRefusal with every refusal
    public static string ToText(FrameModel m, string source, List<string> notes)
    {
        return new Writer(m, source, notes).Run();
    }

    // ---------------- vectors (STAAD coordinates unless named)

    internal static double[] R(double[] v) { return new[] { v[0], v[2], -v[1] }; }          // SAP global -> STAAD global
    static double[] Sub(double[] a, double[] b) { return new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] }; }
    static double[] Add(double[] a, double[] b) { return new[] { a[0] + b[0], a[1] + b[1], a[2] + b[2] }; }
    static double[] Mul(double[] a, double s) { return new[] { a[0] * s, a[1] * s, a[2] * s }; }
    static double Dot(double[] a, double[] b) { return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]; }
    static double Len(double[] a) { return Math.Sqrt(Dot(a, a)); }
    static double[] Unit(double[] a) { double l = Len(a); return l < 1e-300 ? new double[3] : Mul(a, 1 / l); }
    static double[] Cross(double[] a, double[] b) { return new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] }; }

    // STAAD's member axes at BETA 0 for local x (Technical Reference G.4.3): a vertical member (x along Y) has z along
    // +Z; any other has z horizontal (x cross Y) and y up
    internal static double[][] StaadAxes(double[] x)
    {
        double[] z;
        if (Math.Abs(x[0]) < 1e-9 && Math.Abs(x[2]) < 1e-9) z = new[] { 0.0, 0, 1 };
        else z = Unit(Cross(x, new[] { 0.0, 1, 0 }));
        return new[] { x, Cross(z, x), z };
    }

    // the BETA turning STAAD's y into `y2` about x (degrees, right-handed), snapped to whole degrees within 1e-7
    internal static double Beta(double[] x, double[] y2)
    {
        var y0 = StaadAxes(x)[1];
        double b = Math.Atan2(Dot(Cross(y0, y2), x), Dot(y0, y2)) * 180 / Math.PI;
        double r = Math.Round(b);
        if (Math.Abs(b - r) < 1e-7) b = r;
        if (b <= -180 + 1e-9) b += 360;
        return b == 0 ? 0 : b;
    }

    // a joint's local axes from SAP's angles A B C (columns of Rz(A) Ry(B) Rx(C)), SAP coordinates
    static double[][] JointAxes(double[] abc)
    {
        double a = abc[0] * Math.PI / 180, b = abc[1] * Math.PI / 180, c0 = abc[2] * Math.PI / 180;
        double ca = Math.Cos(a), sa = Math.Sin(a), cb = Math.Cos(b), sb = Math.Sin(b), cc = Math.Cos(c0), sc = Math.Sin(c0);
        return new[] {
            new[] { ca * cb, sa * cb, -sb },
            new[] { ca * sb * sc - sa * cc, sa * sb * sc + ca * cc, cb * sc },
            new[] { ca * sb * cc + sa * sc, sa * sb * cc - ca * sc, cb * cc } };
    }

    // ---------------- text

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // a number as STAAD reads it: 12 significant digits, -0 and residue below 1e-12 of `scale` as 0
    internal static string N(double v) { return N(v, 0); }
    internal static string N(double v, double scale)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) throw new InvalidOperationException("FrameStd: a number is " + v.ToString(Inv));
        if (v == 0 || Math.Abs(v) <= 1e-12 * Math.Abs(scale)) return "0";
        string s = v.ToString("G12", Inv);
        return s == "-0" ? "0" : s;
    }

    // ids as STAAD lists them: runs of consecutive numbers as "a TO b"
    public static string Condense(IEnumerable<int> ids)
    {
        var nums = new SortedSet<int>(ids);
        var parts = new List<string>();
        int start = 0, end = 0; bool first = true;
        foreach (int n in nums)
        {
            if (first) { start = end = n; first = false; continue; }
            if (n == end + 1) { end = n; continue; }
            parts.Add(Run(start, end)); start = end = n;
        }
        if (!first) parts.Add(Run(start, end));
        return string.Join(" ", parts.ToArray());
    }
    static string Run(int a, int b)
    {
        if (a == b) return a.ToString(Inv);
        if (b == a + 1) return a.ToString(Inv) + " " + b.ToString(Inv);
        return a.ToString(Inv) + " TO " + b.ToString(Inv);
    }

    // a long line as STAAD continuation lines (" -" at the end of each but the last)
    internal static IEnumerable<string> Wrap(string line, int width)
    {
        if (line.Length <= width) { yield return line; yield break; }
        int pos = 0;
        while (line.Length - pos > width)
        {
            int at = line.LastIndexOf(' ', pos + width - 2, width - 2);
            if (at <= pos) at = pos + width - 2;
            yield return line.Substring(pos, at - pos) + " -";
            pos = at;
            while (pos < line.Length && line[pos] == ' ') pos++;
        }
        if (pos < line.Length) yield return line.Substring(pos);
    }

    // free text (a title, a comment) as printable ASCII without STAAD's command separator
    internal static string Plain(string s, int max)
    {
        var sb = new StringBuilder();
        foreach (char c in s ?? "") sb.Append(c >= 0x20 && c < 0x7f && c != ';' ? c : '_');
        string t = sb.ToString().Trim();
        return t.Length > max ? t.Substring(0, max) : t;
    }

    // a STAAD name (group, material): "_" for groups, upper-case letters, digits and "_", at most `max` characters
    internal static string Name(string s, string prefix, int max)
    {
        var sb = new StringBuilder(prefix);
        foreach (char c in (s ?? "").ToUpperInvariant()) sb.Append(c < 0x80 && char.IsLetterOrDigit(c) ? c : '_');
        string t = sb.ToString();
        return t.Length > max ? t.Substring(0, max) : t;
    }

    // ---------------- the writer

    sealed class FrameInfo
    {
        public FrameMember F; public int Id; public double L;
        public double[][] Ax;                    // SAP local 1 2 3 in STAAD coordinates
        public double Beta;
        public FrameSection S; public FrameSectionProps P; public string Table; public string Mat;
        public double[] Mods;
        public double[] OffI, OffJ, Trans;       // STAAD offsets (global) and their common transverse part
        public double ZoneI, ZoneJ;              // end lengths (rigid, factor 1)
        public double WeightPerLength;           // SAP's self weight per length (unit weight x A x WeightMod)
        public string[] Rel = new string[2];     // MEMBER RELEASE items, START / END
    }

    sealed class Support
    {
        public double[][] Ax;                    // the DOFs' axes (STAAD coordinates)
        public bool[] Fix = new bool[6];
        public double[] K = new double[6];       // SAP's stiffness (rotation: per radian)
        public string From = "";
    }

    sealed class PatternLoads
    {
        public double SelfWeight;
        public readonly SortedDictionary<int, double[]> Joint = new SortedDictionary<int, double[]>();
        public readonly List<string> Member = new List<string>();
        public readonly SortedDictionary<int, double[]> Temp = new SortedDictionary<int, double[]>();
        public readonly SortedDictionary<int, double[]> Disp = new SortedDictionary<int, double[]>();   // STAAD DOFs, NaN = none
        public bool Empty { get { return SelfWeight == 0 && Joint.Count == 0 && Member.Count == 0 && Temp.Count == 0; } }
    }

    sealed class Writer
    {
        readonly FrameModel M; readonly string Source; readonly List<string> Notes;
        readonly List<string> Refused = new List<string>();
        S2kDoc D; SapLabels Ids;
        string LenUnit, ForceUnit, StaadLen, StaadForce;
        readonly Dictionary<string, double[]> Pos = new Dictionary<string, double[]>(StringComparer.Ordinal);       // SAP coordinates
        readonly Dictionary<string, int> JointId = new Dictionary<string, int>(StringComparer.Ordinal);
        readonly List<FrameInfo> Frames = new List<FrameInfo>();
        readonly Dictionary<string, FrameInfo> FrameOf = new Dictionary<string, FrameInfo>(StringComparer.Ordinal);
        readonly List<int[]> Plates = new List<int[]>();                 // id, joints...
        readonly Dictionary<string, int> AreaId = new Dictionary<string, int>(StringComparer.Ordinal);
        readonly Dictionary<int, double> PlateThick = new Dictionary<int, double>();
        readonly Dictionary<int, string> PlateMat = new Dictionary<int, string>();
        readonly List<string> PlaneStress = new List<string>();
        readonly Dictionary<string, int> TrussId = new Dictionary<string, int>(StringComparer.Ordinal);
        readonly List<int[]> Trusses = new List<int[]>();                // id, i, j
        readonly Dictionary<int, double> TrussAx = new Dictionary<int, double>();
        readonly Dictionary<string, List<Support>> Supports = new Dictionary<string, List<Support>>(StringComparer.Ordinal);
        readonly Dictionary<string, string> MatName = new Dictionary<string, string>(StringComparer.Ordinal);       // "label|factor" -> STAAD
        readonly List<string[]> MatDefs = new List<string[]>();          // STAAD name, SAP label, density factor
        readonly List<string> LabelLines = new List<string>();
        bool AnySelfWeight;
        int NextId;
        double LinkE = 1;                                                // the truss links' E (the model's largest)
        readonly HashSet<string> Enforced = new HashSet<string>(StringComparer.Ordinal);   // joints with a ground displacement
        HashSet<int> Used;                                               // joints a member, plate or truss uses

        public Writer(FrameModel m, string source, List<string> notes) { M = m; Source = source ?? ""; Notes = notes ?? new List<string>(); }

        void Refuse(string s) { if (!Refused.Contains(s)) Refused.Add(s); }

        // the refusals, those with the same reason as one ("<reason>: frame 1, frame 2 ..."), long lists cut
        List<string> Grouped()
        {
            var order = new List<string>();
            var by = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (string r in Refused)
            {
                int k = r.IndexOf(": ", StringComparison.Ordinal);
                string who = k < 0 ? "" : r.Substring(0, k), what = k < 0 ? r : r.Substring(k + 2);
                List<string> l;
                if (!by.TryGetValue(what, out l)) { by[what] = l = new List<string>(); order.Add(what); }
                l.Add(who);
            }
            return order.Select(w => by[w].Count == 1 ? (by[w][0] == "" ? w : by[w][0] + ": " + w) : w + ": " + Few(by[w])).ToList();
        }
        internal static string Few(List<string> names)
        {
            const int max = 12;
            return string.Join(", ", names.Take(max).ToArray()) + (names.Count > max ? " and " + (names.Count - max).ToString(Inv) + " more" : "");
        }
        void Note(string s) { if (!Notes.Contains(s)) Notes.Add(s); }

        static string Get(Dictionary<string, string> r, string k) { string v; return r.TryGetValue(k, out v) ? v : null; }
        static double Num(Dictionary<string, string> r, string k, double def) { string v; double d; return r.TryGetValue(k, out v) && double.TryParse(v, NumberStyles.Float, Inv, out d) ? d : def; }
        static bool Is(string a, string b) { return string.Equals((a ?? "").Trim(), b, StringComparison.OrdinalIgnoreCase); }

        public string Run()
        {
            D = FrameS2k.ToDoc(M);
            Ids = new SapLabels(D);
            AnySelfWeight = M.Patterns.Any(p => p.SelfWeight != 0);
            Units();
            Dofs();
            Joints();
            FramesIn();
            Areas();
            Links();
            Connected();
            JointSupports();
            AutoMesh();
            Tables();
            var refs = Loads();
            var cases = Cases(refs);
            var combos = Combos(cases);
            if (Refused.Count > 0) throw new FrameStdRefusal(Grouped());
            return Emit(refs, cases, combos);
        }

        // ---------------- units, degrees of freedom

        void Units()
        {
            string temp;
            FrameUnits.Parse(M.Units, out ForceUnit, out LenUnit, out temp);
            var len = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { "in", "INCHES" }, { "ft", "FEET" }, { "mm", "MMS" }, { "cm", "CM" }, { "m", "METER" } };
            var force = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { "kip", "KIP" }, { "kips", "KIP" }, { "lb", "POUND" }, { "lbf", "POUND" }, { "kN", "KNS" }, { "N", "NEWTON" }, { "kgf", "KG" }, { "tonf", "MTON" } };
            if (!len.TryGetValue(LenUnit, out StaadLen)) Refuse("units: STAAD has no length unit for \"" + LenUnit + "\"");
            if (!force.TryGetValue(ForceUnit, out StaadForce)) Refuse("units: STAAD has no force unit for \"" + ForceUnit + "\"");
        }

        void Dofs()
        {
            foreach (var r in D.GetTable("ACTIVE DEGREES OF FREEDOM"))
            {
                var off = new[] { "UX", "UY", "UZ", "RX", "RY", "RZ" }.Where(k => r.ContainsKey(k) && !Is(Get(r, k), "Yes")).ToArray();
                if (off.Length > 0) Refuse("active degrees of freedom: SAP analyses without " + string.Join(" ", off) + "; the STAAD file is a space frame (make all six active)");
            }
        }

        // ---------------- geometry

        void Joints()
        {
            foreach (var j in M.Joints)
            {
                Pos[j.Label] = j.Xyz;
                int id = Ids.Id("joint", j.Label);
                JointId[j.Label] = id;
                if (id.ToString(Inv) != j.Label) LabelLines.Add("joint " + j.Label + " = " + id.ToString(Inv));
            }
        }

        bool Joint(string label, string who, out double[] p)
        {
            if (Pos.TryGetValue(label ?? "", out p)) return true;
            Refuse(who + ": joint \"" + label + "\" is not in the model");
            return false;
        }

        void FramesIn()
        {
            foreach (var f in M.Frames)
            {
                string who = "frame " + f.Label;
                double[] a, b;
                if (!Joint(f.JointI, who, out a) | !Joint(f.JointJ, who, out b)) continue;
                var c = f.Rec(FrameMember.Conn);
                if (c != null && c.Yes("IsCurved")) { Refuse(who + ": a curved frame"); continue; }
                var d = Sub(b, a);
                double len = Len(d);
                if (len < 1e-9) { Refuse(who + ": zero length"); continue; }
                if (f.AdvancedAxes) { Refuse(who + ": advanced local axes (give it an angle instead)"); continue; }
                var fi = new FrameInfo { F = f, Id = Ids.Id("frame", f.Label), L = len };
                if (fi.Id.ToString(Inv) != f.Label) LabelLines.Add("frame " + f.Label + " = member " + fi.Id.ToString(Inv));
                var sap = FrameSidecar.LocalAxes(d, f.Angle);
                fi.Ax = new[] { R(sap[0]), R(sap[1]), R(sap[2]) };
                fi.Beta = Beta(fi.Ax[0], fi.Ax[1]);
                if (!Section(fi, who)) continue;
                Offsets(fi, who);
                Releases(fi);
                Frames.Add(fi);
                FrameOf[f.Label] = fi;
            }
            NextId = Frames.Count == 0 ? 0 : Frames.Max(x => x.Id);
        }

        bool Section(FrameInfo fi, string who)
        {
            var f = fi.F;
            var s = M.SectionNamed(f.Section);
            if (s == null) { Refuse(who + ": section \"" + f.Section + "\" is not in the model"); return false; }
            string why;
            var p = FrameStdSections.Of(s, out why);
            if (p == null) { Refuse(who + ": section " + s.Label + ": " + why); return false; }
            if (p.I23 != 0) { Refuse(who + ": section " + s.Label + " (" + s.Shape + ") has a product of inertia I23; STAAD's prismatic property has none"); return false; }
            fi.S = s; fi.P = p;
            fi.Mods = f.PropertyModifiers;
            for (int k = 0; k < 6; k++)
                if (fi.Mods[k] <= 0) { Refuse(who + ": property modifier " + FrameMember.ModifierKeys[k] + " = " + N(fi.Mods[k]) + " (STAAD needs a positive property)"); return false; }
            bool mods = fi.Mods.Take(6).Any(x => x != 1);
            fi.Table = TableName(s);
            if (fi.Table != null && mods) { Note("frame " + f.Label + ": section " + s.Label + " written as numbers (PRIS), not STAAD's table, for its property modifiers"); fi.Table = null; }
            if (p.EstimatedJ && fi.Table == null) Note("section " + s.Label + " (" + s.Shape + "): its torsion constant is SAP's rectangle sum, which SAP's own differs from by a junction term (2-3 %)");
            // material: the assignment's own when not Default, else the section's
            var asg = f.Rec(FrameMember.SectionAssign);
            string mat = asg == null ? "" : asg.Get("MatProp") ?? "";
            if (mat == "" || Is(mat, "Default")) mat = s.Material;
            double wf = fi.Mods[7] / fi.Mods[0];
            fi.Mat = Material(mat, AnySelfWeight ? wf : 1, who);
            var fm = M.Materials.FirstOrDefault(x => x.Label == mat);
            fi.WeightPerLength = fm == null ? 0 : fm.Mech("UnitWeight") * p.A * fi.Mods[7];
            return fi.Mat != null;
        }

        // STAAD's AISC table name for a section SAP took from its own table: W shapes, and channels of a whole weight
        // (C10X30); every other family is written as numbers (its STAAD names are not confirmed)
        static string TableName(FrameSection s)
        {
            string n = s.Label.Trim().ToUpperInvariant(), shape = (s.Shape ?? "").ToLowerInvariant();
            if (shape == "i/wide flange" && System.Text.RegularExpressions.Regex.IsMatch(n, @"^W\d+X\d+(\.\d+)?$")) return n;
            if (shape == "channel" && System.Text.RegularExpressions.Regex.IsMatch(n, @"^C\d+X\d+$")) return n;
            return null;
        }

        // the STAAD material for a SAP material and a density factor (a frame's WeightMod / AMod)
        string Material(string label, double factor, string who)
        {
            string key = label + "|" + factor.ToString("R", Inv), name;
            if (MatName.TryGetValue(key, out name)) return name;
            var m = M.Materials.FirstOrDefault(x => x.Label == label);
            if (m == null) { Refuse(who + ": material \"" + label + "\" is not in the model"); return null; }
            var g = m.Rec(FrameMaterial.General);
            string sym = g == null ? "Isotropic" : g.Get("SymType") ?? "Isotropic";
            if (!Is(sym, "Isotropic")) { Refuse("material " + label + ": " + sym + " (STAAD's ISOTROPIC only)"); MatName[key] = null; return null; }
            if (g != null && g.Yes("TempDepend")) Note("material " + label + ": SAP's temperature-dependent values are not written (STAAD gets the values at its reference temperature)");
            name = Name(label, "", 20);
            if (name == "") name = "MAT";
            if (factor != 1) name = Name(label, "", 16) + "_W" + (MatDefs.Count(x => x[1] == label) + 1).ToString(Inv);
            while (MatDefs.Any(x => x[0] == name)) name += "X";
            MatName[key] = name;
            MatDefs.Add(new[] { name, label, factor.ToString("R", Inv) });
            if (factor != 1) Note("material " + name + ": " + label + " with its unit weight times " + N(factor) + " (a frame's weight modifier over its area modifier)");
            return name;
        }

        // cardinal point, joint offsets and end lengths as STAAD member offsets (global)
        void Offsets(FrameInfo fi, string who)
        {
            var f = fi.F;
            fi.OffI = new double[3]; fi.OffJ = new double[3]; fi.Trans = new double[3];
            var ins = f.Rec(FrameMember.Insertion);
            bool transform = ins == null || !ins.Has("Transform") || ins.Yes("Transform");
            double[] e1 = fi.Ax[0], e2 = fi.Ax[1], e3 = fi.Ax[2];
            if (transform)
            {
                int cp = f.CardinalPoint;
                if (cp != 10)
                {
                    string shape = (fi.S.Shape ?? "").ToLowerInvariant();
                    bool equalI = shape == "i/wide flange" && (fi.S.Dim("t2b") <= 0 || fi.S.Dim("t2b") == fi.S.Dim("t2")) && (fi.S.Dim("tfb") <= 0 || fi.S.Dim("tfb") == fi.S.Dim("tf"));
                    bool both = shape == "rectangular" || shape == "circle" || shape == "pipe" || shape == "box/tube" || equalI;
                    bool topBottom = both || shape == "channel";      // symmetric about local 3
                    double half = fi.P.Depth / 2;
                    if ((cp == 5 || cp == 11) && both) { }
                    else if (cp == 2 && topBottom && half > 0) fi.Trans = Mul(e2, half);
                    else if (cp == 8 && topBottom && half > 0) fi.Trans = Mul(e2, -half);
                    else Refuse(who + ": cardinal point " + FrameMember.CardinalText(cp) + " on a " + fi.S.Shape + " section (STAAD offsets are written for bottom / middle / top center of sections symmetric about local 3 only)");
                }
                string sys = f.JointOffsetSys;
                if (sys != "")
                {
                    var v = f.JointOffsets;
                    double[] vi, vj;
                    if (Is(sys, "Local")) { vi = Add(Add(Mul(e1, v[0]), Mul(e2, v[1])), Mul(e3, v[2])); vj = Add(Add(Mul(e1, v[3]), Mul(e2, v[4])), Mul(e3, v[5])); }
                    else if (Is(sys, "GLOBAL")) { vi = R(new[] { v[0], v[1], v[2] }); vj = R(new[] { v[3], v[4], v[5] }); }
                    else { Refuse(who + ": joint offsets in coordinate system " + sys); return; }
                    if (Math.Abs(Dot(vi, e1)) > 1e-9 * fi.L || Math.Abs(Dot(vj, e1)) > 1e-9 * fi.L) Refuse(who + ": joint offsets along the frame's axis (give them as end lengths)");
                    if (Len(Sub(vi, vj)) > 1e-9 * fi.L) Refuse(who + ": different joint offsets at its ends (STAAD's offsets are written for a frame that stays parallel to its joints' line)");
                    fi.Trans = Add(fi.Trans, vi);
                }
            }
            if (Len(fi.Trans) > 1e-12 * fi.L) { fi.OffI = fi.Trans; fi.OffJ = fi.Trans; }
            var el = f.EndLengths;
            if (el[0] != 0 || el[1] != 0)
            {
                string type = f.EndOffsetType;
                if (!Is(type, "User")) Refuse(who + ": end lengths SAP works out (" + type + "); give them as user lengths");
                else if (el[2] != 1) Refuse(who + ": end lengths with rigid-zone factor " + N(el[2]) + " (STAAD's offsets are rigid: factor 1 only)");
                else if (el[0] < 0 || el[1] < 0 || el[0] + el[1] >= fi.L) Refuse(who + ": end lengths " + N(el[0]) + " and " + N(el[1]) + " leave no clear length");
                else
                {
                    fi.ZoneI = el[0]; fi.ZoneJ = el[1];
                    fi.OffI = Add(fi.Trans, Mul(e1, el[0]));
                    fi.OffJ = Add(fi.Trans, Mul(e1, -el[1]));
                }
            }
        }

        // SAP's releases (local P V2 V3 T M2 M3) as STAAD's (FX FY FZ MX MY MZ); a partly fixed one as its spring
        void Releases(FrameInfo fi)
        {
            var rel = fi.F.Releases; var pf = fi.F.PartialFixity;
            string[] dof = { "FX", "FY", "FZ", "MX", "MY", "MZ" };
            for (int e = 0; e < 2; e++)
            {
                var items = new List<string>();
                for (int k = 0; k < 6; k++)
                {
                    int i = e * 6 + k;
                    if (!rel[i]) continue;
                    if (pf[i] != 0) items.Add("K" + dof[k] + " " + N(k < 3 ? pf[i] : pf[i] * Math.PI / 180));
                    else items.Add(dof[k]);
                }
                fi.Rel[e] = string.Join(" ", items.ToArray());
            }
        }

        void Areas()
        {
            var sec = new Dictionary<string, string>(StringComparer.Ordinal);
            var matProp = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var r in D.GetTable("AREA SECTION ASSIGNMENTS")) { string a = Get(r, "Area"); if (a != null) { sec[a] = Get(r, "Section") ?? ""; matProp[a] = Get(r, "MatProp"); } }
            var props = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in D.GetTable("AREA SECTION PROPERTIES")) { string s = Get(r, "Section"); if (s != null && !props.ContainsKey(s)) props[s] = r; }
            var rows = D.GetTable("CONNECTIVITY - AREA");
            if (rows.Count == 0) return;
            // members and plates share STAAD's numbers: the areas keep theirs unless one is a member's
            var frameIds = new HashSet<int>(Frames.Select(x => x.Id));
            var mine = rows.Select(r => Ids.Id("area", Get(r, "Area") ?? "")).ToList();
            bool clash = mine.Any(frameIds.Contains);
            int next = NextId;
            for (int k = 0; k < rows.Count; k++)
            {
                var r = rows[k];
                string label = Get(r, "Area") ?? "", who = "area " + label;
                int id = clash ? ++next : mine[k];
                AreaId[label] = id;
                if (id.ToString(Inv) != label) LabelLines.Add("area " + label + " = plate " + id.ToString(Inv));
                int n = (int)Num(r, "NumJoints", 0);
                if (n < 3 || n > 4) { Refuse(who + ": " + n.ToString(Inv) + " joints (STAAD's plates have 3 or 4)"); continue; }
                var pl = new List<int> { id };
                bool ok = true;
                for (int i = 1; i <= n; i++) { string j = Get(r, "Joint" + i.ToString(Inv)); int jid; if (j == null || !JointId.TryGetValue(j, out jid)) { Refuse(who + ": joint \"" + j + "\" is not in the model"); ok = false; break; } pl.Add(jid); }
                if (!ok) continue;
                string s;
                Dictionary<string, string> p;
                if (!sec.TryGetValue(label, out s) || !props.TryGetValue(s, out p)) { Refuse(who + ": no area section"); continue; }
                string type = Get(p, "Type") ?? "", atype = Get(p, "AreaType") ?? "Shell";
                if (!Is(atype, "Shell")) { Refuse(who + ": section " + s + " is a " + atype + " (STAAD's plates are shells)"); continue; }
                double t = Num(p, "Thickness", 0), tb = Num(p, "BendThick", t);
                if (Is(type, "Membrane")) PlaneStress.Add(label);
                else if (!Is(type, "Shell-Thin") && !Is(type, "Shell-Thick")) { Refuse(who + ": section " + s + " is " + type + " (STAAD: shells and membranes)"); continue; }
                if (t <= 0) { Refuse(who + ": section " + s + " has no thickness"); continue; }
                if (tb != t && !Is(type, "Membrane")) { Refuse(who + ": section " + s + " has a bending thickness " + N(tb) + " apart from its thickness " + N(t)); continue; }
                string mat = Get(p, "Material") ?? "", mp;
                if (matProp.TryGetValue(label, out mp) && !string.IsNullOrEmpty(mp) && !Is(mp, "Default")) mat = mp;
                string sm = Material(mat, 1, who);
                if (sm == null) continue;
                var fm = M.Materials.FirstOrDefault(x => x.Label == mat);
                double nu = fm == null ? 0 : fm.Mech("U12");
                if (nu < 0.01 || nu > 0.499) Refuse(who + ": material " + mat + " has Poisson's ratio " + N(nu) + " (STAAD's plates take 0.01 to 0.499)");
                Plates.Add(pl.ToArray());
                PlateThick[id] = t;
                PlateMat[id] = sm;
            }
            NextId = Math.Max(NextId, Plates.Count == 0 ? 0 : Plates.Max(x => x[0]));
            if (Plates.Count > 0) Note("plates: STAAD's plate element is its own formulation (SAP " + string.Join(" / ", props.Values.Select(x => Get(x, "Type") ?? "").Distinct().ToArray()) + "); its results differ from SAP's by the elements, not the model");
            if (PlaneStress.Count > 0) Note("membranes written as STAAD plane-stress plates: " + string.Join(" ", PlaneStress.ToArray()));
        }

        // ---------------- links

        void Links()
        {
            var props = new Dictionary<string, FrameLinkProp>(StringComparer.Ordinal);
            foreach (var p in M.LinkProps) if (!props.ContainsKey(p.Label)) props[p.Label] = p;
            var nonlinear = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            // the truss links' material: the model's largest E (STAAD warns of a soft material otherwise), AX = KE L / E
            foreach (var mt in M.Materials) LinkE = Math.Max(LinkE, mt.Mech("E1"));
            foreach (var l in M.Links)
            {
                string who = "link " + l.Label;
                FrameLinkProp p;
                if (!props.TryGetValue(l.Property, out p)) { Refuse(who + ": property \"" + l.Property + "\" is not in the model"); continue; }
                string type = FrameSidecar.TypeOf(p);
                if (type != "linear")
                {
                    List<string> nl;
                    if (!nonlinear.TryGetValue(p.LinkType, out nl)) nonlinear[p.LinkType] = nl = new List<string>();
                    nl.Add(l.Label);
                    continue;
                }
                var g = p.Rec(FrameLinkProp.General);
                if (AnySelfWeight && g != null && g.Num("Weight", 0) != 0) { Refuse(who + ": property " + p.Label + " has a weight (STAAD's self weight does not reach links)"); continue; }
                var la = l.Rec(FrameLink.LocalAxes);
                if (la != null && la.Yes("AdvanceAxes")) { Refuse(who + ": advanced local axes"); continue; }
                // DOFs: fixed, or a stiffness
                var fix = new bool[6]; var k = new double[6];
                foreach (var r in p.DofRows)
                {
                    int i = Array.IndexOf(FrameJoint.Dofs, (r.Get("DOF") ?? "").ToUpperInvariant());
                    if (i < 0) continue;
                    fix[i] = r.Yes("Fixed");
                    k[i] = r.Num(i < 3 ? "TransKE" : "RotKE", 0);
                }
                double[] a, b;
                if (l.JointJ == "")
                {
                    if (!Joint(l.JointI, who, out a)) continue;
                    if (!fix.Any(x => x) && !k.Any(x => x != 0)) { Note(who + ": a grounded link with no stiffness (not written)"); continue; }
                    var ax = FrameSidecar.LocalAxes(new[] { 0.0, 0, 1 }, l.Angle);
                    AddSupport(l.JointI, new Support { Ax = ax.Select(R).ToArray(), Fix = fix, K = k, From = who });
                    continue;
                }
                if (!Joint(l.JointI, who, out a) | !Joint(l.JointJ, who, out b)) continue;
                double len = Len(Sub(b, a));
                if (len < 1e-9) { Refuse(who + ": a zero-length two-joint link (make it a grounded link or a spring)"); continue; }
                bool axialOnly = !fix[0] && k[0] > 0 && Enumerable.Range(1, 5).All(i => !fix[i] && k[i] == 0);
                if (!axialOnly)
                {
                    if (!fix.Any(x => x) && !k.Any(x => x != 0)) { Note(who + ": a link with no stiffness (not written)"); continue; }
                    Refuse(who + ": a two-joint link acting other than along its axis (" + string.Join(" ", Enumerable.Range(0, 6).Where(i => fix[i] || k[i] != 0).Select(i => FrameJoint.Dofs[i] + (fix[i] ? " fixed" : "")).ToArray()) + "); STAAD gets axial links (U1 with a stiffness) as truss members");
                    continue;
                }
                int id = ++NextId;
                TrussId[l.Label] = id;
                Trusses.Add(new[] { id, JointId[l.JointI], JointId[l.JointJ] });
                TrussAx[id] = k[0] * len / LinkE;
                LabelLines.Add("link " + l.Label + " = member " + id.ToString(Inv) + " (truss, EA/L = " + N(k[0]) + ")");
            }
            if (nonlinear.Count > 0)
                Refuse("nonlinear links (make them linear, or leave them out) -- " + string.Join("; ", nonlinear.Select(kv => kv.Key + " (" + kv.Value.Count.ToString(Inv) + "): " + Few(kv.Value)).ToArray()));
        }

        // joints a member, plate or truss uses; any other is left out (STAAD warns of a joint not connected, and it carries
        // nothing in SAP either), with its supports and loads
        void Connected()
        {
            Used = new HashSet<int>();
            foreach (var f in Frames) { Used.Add(JointId[f.F.JointI]); Used.Add(JointId[f.F.JointJ]); }
            foreach (var p in Plates) for (int i = 1; i < p.Length; i++) Used.Add(p[i]);
            foreach (var t in Trusses) { Used.Add(t[1]); Used.Add(t[2]); }
            var lone = M.Joints.Where(j => !Used.Contains(JointId[j.Label])).Select(j => j.Label).ToList();
            if (lone.Count > 0) Note("joints nothing connects (no frame, area or axial link), left out with their supports and loads: " + Few(lone));
        }

        // ---------------- supports

        void AddSupport(string joint, Support s)
        {
            List<Support> l;
            if (!Supports.TryGetValue(joint, out l)) Supports[joint] = l = new List<Support>();
            l.Add(s);
        }

        void JointSupports()
        {
            foreach (var j in M.Joints)
            {
                var res = j.Restraint; var spr = j.Spring;
                bool r = res.Any(x => x), s = spr.Any(x => x != 0);
                if (!r && !s) continue;
                var la = j.Rec(FrameJoint.LocalAxes);
                if (la != null && la.Yes("AdvanceAxes")) { Refuse("joint " + j.Label + ": advanced local axes on a support"); continue; }
                var local = JointAxes(j.LocalAngles).Select(R).ToArray();
                if (r) AddSupport(j.Label, new Support { Ax = local, Fix = res, From = "restraints" });
                if (s)
                {
                    var row = j.Rec(FrameJoint.Springs);
                    string cs = row == null ? "GLOBAL" : row.Get("CoordSys") ?? "GLOBAL";
                    double[][] ax;
                    if (Is(cs, "Local")) ax = local;
                    else if (Is(cs, "GLOBAL")) ax = new[] { R(new[] { 1.0, 0, 0 }), R(new[] { 0.0, 1, 0 }), R(new[] { 0.0, 0, 1 }) };
                    else { Refuse("joint " + j.Label + ": springs in coordinate system " + cs); continue; }
                    AddSupport(j.Label, new Support { Ax = ax, K = spr, From = "springs" });
                }
            }
        }

        // a DOF state: 0 free, 1 fixed, 2 spring
        static double State(Support s, int i) { return s.Fix[i] ? double.PositiveInfinity : s.K[i]; }

        // the axis of `to` each axis of `from` is parallel to; null when not all are
        static int[] Perm(double[][] from, double[][] to)
        {
            var p = new int[3];
            for (int i = 0; i < 3; i++)
            {
                p[i] = -1;
                for (int k = 0; k < 3; k++) if (Math.Abs(Math.Abs(Dot(from[i], to[k])) - 1) < 1e-9) { p[i] = k; break; }
                if (p[i] < 0) return null;
            }
            return p;
        }

        // the support's DOF states in the frame `to`: along a common axis as they are, and in the plane across it
        // only when both its DOFs there are the same
        static double[] InFrame(Support s, double[][] to)
        {
            var o = new double[6];
            var p = Perm(s.Ax, to);
            if (p != null) { for (int i = 0; i < 3; i++) { o[p[i]] = State(s, i); o[3 + p[i]] = State(s, 3 + i); } return o; }
            for (int i = 0; i < 3; i++)
                for (int k = 0; k < 3; k++)
                {
                    if (Math.Abs(Math.Abs(Dot(s.Ax[i], to[k])) - 1) >= 1e-9) continue;
                    int i1 = (i + 1) % 3, i2 = (i + 2) % 3, k1 = (k + 1) % 3, k2 = (k + 2) % 3;
                    if (State(s, i1) != State(s, i2) || State(s, 3 + i1) != State(s, 3 + i2)) return null;
                    o[k] = State(s, i); o[3 + k] = State(s, 3 + i);
                    o[k1] = o[k2] = State(s, i1); o[3 + k1] = o[3 + k2] = State(s, 3 + i1);
                    return o;
                }
            bool iso = State(s, 0) == State(s, 1) && State(s, 1) == State(s, 2) && State(s, 3) == State(s, 4) && State(s, 4) == State(s, 5);
            if (!iso) return null;
            for (int i = 0; i < 3; i++) { o[i] = State(s, 0); o[3 + i] = State(s, 3); }
            return o;
        }

        // the STAAD support of a joint: in global axes when every part of it reads there, else an inclined support
        // along one of its axes; null (refused) when no STAAD frame holds all its parts
        string SupportText(string joint, List<Support> parts, out bool inclined)
        {
            inclined = false;
            var glob = new[] { new[] { 1.0, 0, 0 }, new[] { 0.0, 1, 0 }, new[] { 0.0, 0, 1 } };
            var frames = new List<double[][]> { glob };
            foreach (var s in parts) for (int i = 0; i < 3; i++) frames.Add(StaadAxes(Unit(s.Ax[i])));
            for (int f = 0; f < frames.Count; f++)
            {
                var sum = new double[6];
                bool ok = true;
                foreach (var s in parts)
                {
                    var o = InFrame(s, frames[f]);
                    if (o == null) { ok = false; break; }
                    for (int i = 0; i < 6; i++) sum[i] = double.IsPositiveInfinity(sum[i]) || double.IsPositiveInfinity(o[i]) ? double.PositiveInfinity : sum[i] + o[i];
                }
                if (!ok) continue;
                inclined = f > 0;
                string head = JointId[joint].ToString(Inv) + (inclined ? " INCLINED " + string.Join(" ", frames[f][0].Select(x => N(x, 1)).ToArray()) : "");
                return head + " " + Spec(sum, Enforced.Contains(joint));
            }
            return null;
        }

        // a joint with a ground displacement is ENFORCED (STAAD takes support displacements in a model with plates, and in
        // more than 4 load cases, only so; Technical Reference and STAAD 2025's own message); it has no springs (refused)
        static string Spec(double[] st, bool enforced)
        {
            string[] dof = { "FX", "FY", "FZ", "MX", "MY", "MZ" };
            if (enforced)
            {
                var free = Enumerable.Range(0, 6).Where(i => !double.IsPositiveInfinity(st[i])).Select(i => dof[i]).ToArray();
                return free.Length == 0 ? "ENFORCED" : "ENFORCED BUT " + string.Join(" ", free);
            }
            if (st.All(double.IsPositiveInfinity)) return "FIXED";
            if (st.Take(3).All(double.IsPositiveInfinity) && st.Skip(3).All(x => x == 0)) return "PINNED";
            var rel = new List<string>(); var spr = new List<string>();
            for (int i = 0; i < 6; i++)
            {
                if (double.IsPositiveInfinity(st[i])) continue;
                if (st[i] == 0) rel.Add(dof[i]);
                else spr.Add("K" + dof[i] + " " + N(i < 3 ? st[i] : st[i] * Math.PI / 180));
            }
            return "FIXED BUT " + string.Join(" ", rel.Concat(spr).ToArray());
        }

        // ---------------- SAP's frame meshing: joints and frames it connects along a frame
        void AutoMesh()
        {
            foreach (var r in D.GetTable("FRAME AUTO MESH ASSIGNMENTS"))
            {
                string label = Get(r, "Frame");
                FrameInfo fi;
                if (label == null || !FrameOf.TryGetValue(label, out fi) || !Is(Get(r, "AutoMesh"), "Yes")) continue;
                double[] a = R(Pos[fi.F.JointI]), b = R(Pos[fi.F.JointJ]);
                if (Is(Get(r, "AtJoints"), "Yes"))
                {
                    var on = new List<string>();
                    foreach (var kv in Pos)
                    {
                        if (kv.Key == fi.F.JointI || kv.Key == fi.F.JointJ) continue;
                        var p = R(kv.Value);
                        double t = Dot(Sub(p, a), fi.Ax[0]);
                        if (t <= 1e-6 * fi.L || t >= fi.L * (1 - 1e-6)) continue;
                        if (Len(Sub(p, Add(a, Mul(fi.Ax[0], t)))) < 1e-6 * fi.L + 1e-9) on.Add(kv.Key);
                    }
                    if (on.Count > 0) Refuse("frame " + label + ": SAP's automatic mesh joins it to joint" + (on.Count > 1 ? "s " : " ") + string.Join(" ", on.ToArray()) + " along its length; split it there");
                }
                if (Is(Get(r, "AtFrames"), "Yes"))
                {
                    var cross = new List<string>();
                    foreach (var o in Frames)
                    {
                        if (o == fi) continue;
                        double[] c = R(Pos[o.F.JointI]), d = R(Pos[o.F.JointJ]);
                        if (Crosses(a, b, c, d, fi.L)) cross.Add(o.F.Label);
                    }
                    if (cross.Count > 0) Refuse("frame " + label + ": SAP's automatic mesh joins it to frame" + (cross.Count > 1 ? "s " : " ") + string.Join(" ", cross.ToArray()) + " where they cross; split them there");
                }
            }
        }

        // two segments meeting inside the first (not at its ends)
        static bool Crosses(double[] a, double[] b, double[] c, double[] d, double scale)
        {
            var u = Sub(b, a); var v = Sub(d, c); var w = Sub(a, c);
            double uu = Dot(u, u), uv = Dot(u, v), vv = Dot(v, v), uw = Dot(u, w), vw = Dot(v, w), den = uu * vv - uv * uv;
            if (den < 1e-12 * uu * vv) return false;
            double s = (uv * vw - vv * uw) / den, t = (uu * vw - uv * uw) / den;
            if (s <= 1e-6 || s >= 1 - 1e-6 || t < -1e-9 || t > 1 + 1e-9) return false;
            return Len(Sub(Add(a, Mul(u, s)), Add(c, Mul(v, t)))) < 1e-6 * scale;
        }

        // ---------------- tables the writer does not read
        static readonly string[] Read = {
            "PROGRAM CONTROL", "ACTIVE DEGREES OF FREEDOM", "JOINT COORDINATES", "JOINT RESTRAINT ASSIGNMENTS", "JOINT SPRING ASSIGNMENTS 1 - UNCOUPLED",
            "JOINT LOCAL AXES ASSIGNMENTS 1 - TYPICAL", "CONNECTIVITY - FRAME", "FRAME SECTION ASSIGNMENTS", "FRAME SECTION PROPERTIES 01 - GENERAL",
            "FRAME LOCAL AXES ASSIGNMENTS 1 - TYPICAL", "FRAME RELEASE ASSIGNMENTS 1 - GENERAL", "FRAME RELEASE ASSIGNMENTS 2 - PARTIAL FIXITY",
            "FRAME INSERTION POINT ASSIGNMENTS", "FRAME PROPERTY MODIFIERS", "FRAME OFFSET ALONG LENGTH ASSIGNMENTS", "FRAME AUTO MESH ASSIGNMENTS",
            "MATERIAL PROPERTIES 01 - GENERAL", "MATERIAL PROPERTIES 02 - BASIC MECHANICAL PROPERTIES", "MATERIAL PROPERTIES 03A - STEEL DATA",
            "CONNECTIVITY - LINK", "LINK PROPERTY ASSIGNMENTS", "LINK LOCAL AXES ASSIGNMENTS 1 - TYPICAL", "CONNECTIVITY - AREA", "AREA SECTION ASSIGNMENTS",
            "AREA SECTION PROPERTIES", "AREA LOCAL AXES ASSIGNMENTS 1 - TYPICAL", "GROUPS 1 - DEFINITIONS", "GROUPS 2 - ASSIGNMENTS", "LOAD PATTERN DEFINITIONS",
            FrameLoad.JointForce, FrameLoad.GroundDisp, FrameLoad.Point, FrameLoad.Distributed, FrameLoad.Temperature, "LOAD CASE DEFINITIONS",
            "CASE - STATIC 1 - LOAD ASSIGNMENTS", "CASE - STATIC 2 - NONLINEAR LOAD APPLICATION", "CASE - STATIC 4 - NONLINEAR PARAMETERS",
            "COMBINATION DEFINITIONS", "COORDINATE SYSTEMS" };
        // tables that change what SAP analyses, which STAAD does not get
        static readonly string[] Structural = {
            "JOINT CONSTRAINT", "CONSTRAINT DEFINITIONS", "CONNECTIVITY - SOLID", "CONNECTIVITY - CABLE", "CONNECTIVITY - TENDON", "FRAME TENSION AND COMPRESSION",
            "FRAME HINGE", "FRAME NONLINEAR", "JOINT SPRING ASSIGNMENTS 2", "JOINT LOCAL AXES ASSIGNMENTS 2", "FRAME LOCAL AXES ASSIGNMENTS 2",
            "LINK LOCAL AXES ASSIGNMENTS 2", "AREA LOADS", "SOLID LOADS", "CABLE LOADS", "TENDON LOADS", "LINK LOADS", "JOINT LOADS", "FRAME LOADS",
            "AREA THICKNESS", "AREA SPRING", "AREA EDGE", "AREA OFFSET", "AREA STIFFNESS MODIFIER ASSIGNMENTS", "AREA AUTO MESH", "FRAME LINE SPRING",
            "FRAME SPRING", "JOINT PANEL ZONE", "CASE - STATIC 5", "CASE - STATIC 6", "AUTO WIND", "AUTO SEISMIC" };
        // tables that only describe design, output, display or other analyses
        static readonly string[] Quiet = {
            "ANALYSIS OPTIONS", "TABLES AUTOMATICALLY", "GRID LINES", "GENERAL GRIDS", "GENERAL REFERENCE LINE", "MATERIAL PROPERTIES", "FRAME SECTION PROPERTIES",
            "FRAME DESIGN", "FRAME OUTPUT STATION", "OVERWRITES", "PREFERENCES", "MASS SOURCE", "JOINT ADDED MASS", "AREA ADDED MASS", "GROUPS 3", "OPTIONS",
            "PROJECT INFORMATION", "DATABASE FORMAT", "FUNCTION", "CASE - MODAL", "CASE - RESPONSE SPECTRUM", "CASE - BUCKLING", "CASE - DIRECT HISTORY",
            "CASE - MODAL HISTORY", "CASE - FREQUENCY", "CASE - STEADY STATE", "CASE - POWER SPECTRAL", "CASE - MULTISTEP", "CASE - HYPERSTATIC",
            "CASE - MOVING LOAD", "CASE - STATIC 3", "AUTO COMBINATION", "AREA SECTION PROPERT", "LINK PROPERTY DEFINITIONS", "LINK FREQUENCY",
            "FRAME PROPERTY MODIFIER DEFINITIONS", "FRAME RELEASE DEFINITIONS", "CABLE PROPERTY", "TENDON PROPERTY", "SOLID PROPERTY",
            "AREA STIFFNESS MODIFIER DEFINITIONS", "NAMED SETS", "SECTION CUTS", "SECTION DESIGNER", "JOINT PATTERN", "JOINT MERGE NUMBER",
            "LOAD PATTERN DEFINITIONS", "LOAD CASE DEFINITIONS", "ACTIVE DEGREES", "COORDINATE SYSTEMS", "SELECTION", "VIEW", "MODEL ALIVE", "DESIGN",
            "BRIDGE", "LANE", "VEHICLE", "REBAR", "AUTO WAVE", "FRAME END SKEW", "FRAME LOAD TRANSFER", "FRAME RATING" };

        void Tables()
        {
            var skipped = new List<string>();
            foreach (string t in D.TableNames())
            {
                if (Read.Any(x => Is(t, x))) continue;
                int n = D.GetTable(t).Count;
                if (n == 0) continue;
                string up = t.ToUpperInvariant();
                if (Structural.Any(x => up.StartsWith(x))) { Refuse("table \"" + t + "\" (" + n.ToString(Inv) + " rows): not translated to STAAD"); continue; }
                if (Quiet.Any(x => up.StartsWith(x))) continue;
                skipped.Add(t + " (" + n.ToString(Inv) + ")");
            }
            if (skipped.Count > 0) Note("SAP tables not written to STAAD: " + string.Join(", ", skipped.ToArray()));
        }

        // ---------------- loads: one reference load per pattern

        List<KeyValuePair<FramePattern, PatternLoads>> Loads()
        {
            var o = new List<KeyValuePair<FramePattern, PatternLoads>>();
            var by = new Dictionary<string, PatternLoads>(StringComparer.Ordinal);
            foreach (var p in M.Patterns)
            {
                if (by.ContainsKey(p.Label)) continue;
                var pl = new PatternLoads { SelfWeight = p.SelfWeight };
                var r = p.Rec(FramePattern.Defs);
                string auto = r == null ? null : r.Get("AutoLoad");
                if (!string.IsNullOrEmpty(auto) && !Is(auto, "None")) Refuse("load pattern " + p.Label + ": SAP's automatic " + auto + " load (not translated)");
                by[p.Label] = pl;
                o.Add(new KeyValuePair<FramePattern, PatternLoads>(p, pl));
                if (pl.SelfWeight != 0) foreach (var fi in Frames) ZoneSelfWeight(fi, pl);
            }
            foreach (var ld in M.Loads)
            {
                var row = ld.Row;
                if (row == null) continue;
                PatternLoads pl;
                string who = ld.Table.Substring(ld.Table.IndexOf('-') + 2).ToLowerInvariant() + " load on " + (ld.KeyField == "Joint" ? "joint " : "frame ") + ld.Target + " (" + ld.Pattern + ")";
                if (!by.TryGetValue(ld.Pattern, out pl)) { Refuse(who + ": pattern \"" + ld.Pattern + "\" is not in the model"); continue; }
                string jp = row.Get("JtPattern") ?? row.Get("JtPtnName");
                if (!string.IsNullOrEmpty(jp) && !Is(jp, "None")) { Refuse(who + ": by joint pattern " + jp); continue; }
                if (Is(ld.Table, FrameLoad.JointForce) || Is(ld.Table, FrameLoad.GroundDisp)) JointLoad(ld, row, pl, who);
                else
                {
                    FrameInfo fi;
                    if (!FrameOf.TryGetValue(ld.Target, out fi)) { Refuse(who + ": frame \"" + ld.Target + "\" is not written"); continue; }
                    if (Is(ld.Table, FrameLoad.Temperature)) TempLoad(fi, row, pl, who);
                    else MemberLoad(fi, ld, row, pl, who);
                }
            }
            return o;
        }

        static double[] Six(PatternLoads pl, SortedDictionary<int, double[]> d, int id, double fill)
        {
            double[] v;
            if (!d.TryGetValue(id, out v)) { v = new double[6]; for (int i = 0; i < 6; i++) v[i] = fill; d[id] = v; }
            return v;
        }

        void JointLoad(FrameLoad ld, FrameRec row, PatternLoads pl, string who)
        {
            int jid;
            if (!JointId.TryGetValue(ld.Target, out jid)) { Refuse(who + ": joint \"" + ld.Target + "\" is not in the model"); return; }
            string cs = row.Get("CoordSys") ?? "GLOBAL";
            if (!Is(cs, "GLOBAL")) { Refuse(who + ": in coordinate system " + cs); return; }
            bool disp = Is(ld.Table, FrameLoad.GroundDisp);
            string[] keys = disp ? FrameJoint.Dofs : new[] { "F1", "F2", "F3", "M1", "M2", "M3" };
            var f = R(new[] { row.Num(keys[0], 0), row.Num(keys[1], 0), row.Num(keys[2], 0) });
            var m = R(new[] { row.Num(keys[3], 0), row.Num(keys[4], 0), row.Num(keys[5], 0) });
            if (!disp)
            {
                var v = Six(pl, pl.Joint, jid, 0);
                for (int i = 0; i < 3; i++) { v[i] += f[i]; v[3 + i] += m[i]; }
                return;
            }
            // a ground displacement moves the restrained DOFs (STAAD's support displacement, rotations in degrees);
            // on a free DOF SAP ignores it; through a spring or a grounded link STAAD cannot
            List<Support> parts;
            Supports.TryGetValue(ld.Target, out parts);
            bool inclined = false;
            string text = parts == null ? null : SupportText(ld.Target, parts, out inclined);
            if (parts == null || text == null) return;
            if (inclined) { Refuse(who + ": a ground displacement at an inclined support"); return; }
            if (parts.Any(q => q.K.Any(k => k != 0))) { Refuse(who + ": a ground displacement at a joint with springs or a grounded link (STAAD's ENFORCED support takes no springs)"); return; }
            var glob = new[] { new[] { 1.0, 0, 0 }, new[] { 0.0, 1, 0 }, new[] { 0.0, 0, 1 } };
            var st = new double[6];
            foreach (var s in parts) { var o = InFrame(s, glob); for (int i = 0; i < 6; i++) st[i] = double.IsPositiveInfinity(st[i]) || double.IsPositiveInfinity(o[i]) ? double.PositiveInfinity : st[i] + o[i]; }
            var d = Six(pl, pl.Disp, jid, double.NaN);
            var val = new[] { f[0], f[1], f[2], m[0] * 180 / Math.PI, m[1] * 180 / Math.PI, m[2] * 180 / Math.PI };
            for (int i = 0; i < 6; i++)
            {
                if (val[i] == 0) continue;
                if (double.IsPositiveInfinity(st[i])) { d[i] = (double.IsNaN(d[i]) ? 0 : d[i]) + val[i]; Enforced.Add(ld.Target); }
                else if (st[i] != 0) Refuse(who + ": a ground displacement through a spring or a grounded link (STAAD moves supports only)");
            }
        }

        void TempLoad(FrameInfo fi, FrameRec row, PatternLoads pl, string who)
        {
            string type = (row.Get("Type") ?? "Temperature").Trim();
            double t = row.Num("Temp", 0);
            if (t == 0) return;
            var v = Six(pl, pl.Temp, fi.Id, 0);
            if (Is(type, "Temperature")) { v[0] += t; return; }
            if (type.IndexOf("Gradient", StringComparison.OrdinalIgnoreCase) < 0) { Refuse(who + ": type " + type); return; }
            bool two = type.EndsWith("2");
            double depth = two ? fi.P.Depth : fi.P.Width;
            if (depth <= 0) { Refuse(who + ": a gradient on section " + fi.S.Label + ", which has no " + (two ? "t3" : "t2")); return; }
            // STAAD: the difference top (+y) minus bottom, and side to side along z, over the section's depth / width
            if (two) v[1] += t * depth; else v[2] += t * depth;
        }

        // a direction SAP names, as STAAD's: its keyword, the sign and the vector it acts along (STAAD coordinates)
        bool Dir(FrameInfo fi, FrameRec row, string who, out string key, out double sign, out double[] vec)
        {
            key = null; sign = 1; vec = null;
            string dir = (row.Get("Dir") ?? "Gravity").Trim(), cs = row.Get("CoordSys") ?? "GLOBAL";
            bool proj = dir.EndsWith("Proj", StringComparison.OrdinalIgnoreCase) || dir.EndsWith("Projected", StringComparison.OrdinalIgnoreCase);
            string d = proj ? dir.Substring(0, dir.IndexOf(' ')).Trim() : dir;
            if (d == "1" || d == "2" || d == "3")
            {
                int k = d[0] - '1';
                key = "XYZ".Substring(k, 1); vec = fi.Ax[k];
                return true;
            }
            if (!Is(cs, "GLOBAL")) { Refuse(who + ": in coordinate system " + cs); return false; }
            string p = proj ? "P" : "G";
            if (Is(d, "Gravity")) { key = p + "Y"; sign = -1; vec = new[] { 0.0, 1, 0 }; return true; }
            if (Is(d, "X")) { key = p + "X"; vec = new[] { 1.0, 0, 0 }; return true; }
            if (Is(d, "Y")) { key = p + "Z"; sign = -1; vec = new[] { 0.0, 0, 1 }; return true; }
            if (Is(d, "Z")) { key = p + "Y"; vec = new[] { 0.0, 1, 0 }; return true; }
            Refuse(who + ": direction " + dir);
            return false;
        }

        void MemberLoad(FrameInfo fi, FrameLoad ld, FrameRec row, PatternLoads pl, string who)
        {
            string key; double sign; double[] vec;
            if (!Dir(fi, row, who, out key, out sign, out vec)) return;
            bool moment = Is(row.Get("Type") ?? "Force", "Moment");
            bool proj = key.StartsWith("P");
            bool rel = (row.Get("DistType") ?? "RelDist").StartsWith("Rel", StringComparison.OrdinalIgnoreCase);
            Func<string, string, double> at = (rk, ak) => rel ? row.Num(rk, 0) * fi.L : row.Num(ak, 0);
            if (Is(ld.Table, FrameLoad.Point))
            {
                double s = at("RelDist", "AbsDist"), v = row.Num(moment ? "Moment" : "Force", 0) * sign;
                if (v == 0) return;
                if (proj) key = "G" + key.Substring(1);     // a point has no length to project: the global direction
                if (s < -1e-9 * fi.L || s > fi.L * (1 + 1e-9)) { Refuse(who + ": at " + N(s) + ", off the frame"); return; }
                if (s < fi.ZoneI || s > fi.L - fi.ZoneJ) { Transfer(fi, pl, s, Mul(vec, v), moment); return; }
                pl.Member.Add(fi.Id.ToString(Inv) + (moment ? " CMOM " : " CON ") + key + " " + N(v) + " " + N(s - fi.ZoneI, fi.L));
                return;
            }
            double a = at("RelDistA", "AbsDistA"), b = at("RelDistB", "AbsDistB");
            double wa = row.Num(moment ? "MOverLA" : "FOverLA", 0) * sign, wb = row.Num(moment ? "MOverLB" : "FOverLB", 0) * sign;
            if (wa == 0 && wb == 0) return;
            if (b < a) { double x = a; a = b; b = x; x = wa; wa = wb; wb = x; }
            if (a < -1e-9 * fi.L || b > fi.L * (1 + 1e-9) || b - a <= 1e-12 * fi.L) { Refuse(who + ": from " + N(a) + " to " + N(b) + ", off the frame"); return; }
            if (moment && (proj || wa != wb)) { Refuse(who + ": a " + (proj ? "projected" : "varying") + " distributed moment"); return; }
            if (proj && wa != wb && (fi.ZoneI > 0 || fi.ZoneJ > 0)) { Refuse(who + ": a varying projected load on a frame with end lengths"); return; }
            Func<double, double> w = s => wa + (wb - wa) * (s - a) / (b - a);
            // the clear length takes its part; the rigid end zones pass theirs to the joints
            double c0 = Math.Max(a, fi.ZoneI), c1 = Math.Min(b, fi.L - fi.ZoneJ);
            if (c1 - c0 > 1e-12 * fi.L)
            {
                double clear = fi.L - fi.ZoneI - fi.ZoneJ, d1 = c0 - fi.ZoneI, d2 = c1 - fi.ZoneI, w1 = w(c0), w2 = w(c1);
                bool full = d1 <= 1e-12 * fi.L && d2 >= clear * (1 - 1e-12);
                string span = full ? "" : " " + N(d1, fi.L) + " " + N(d2, fi.L);
                if (moment) pl.Member.Add(fi.Id.ToString(Inv) + " UMOM " + key + " " + N(w1) + span);
                else if (w1 == w2 || Math.Abs(w1 - w2) <= 1e-12 * Math.Max(Math.Abs(w1), Math.Abs(w2))) pl.Member.Add(fi.Id.ToString(Inv) + " UNI " + key + " " + N(w1) + span);
                else pl.Member.Add(fi.Id.ToString(Inv) + " TRAP " + key + " " + N(w1) + " " + N(w2) + " " + N(d1, fi.L) + " " + N(d2, fi.L));
            }
            foreach (var z in new[] { new[] { a, Math.Min(b, fi.ZoneI) }, new[] { Math.Max(a, fi.L - fi.ZoneJ), b } })
            {
                double z0 = z[0], z1 = z[1], len = z1 - z0;
                if (len <= 1e-12 * fi.L) continue;
                double w0 = w(z0), w1 = w(z1);
                double scale = proj ? Math.Sqrt(Math.Max(0, 1 - Math.Pow(Dot(fi.Ax[0], vec), 2))) : 1;
                // two triangles: w0 at a third of the way, w1 at two thirds
                Transfer(fi, pl, z0 + len / 3, Mul(vec, w0 * len / 2 * scale), moment);
                Transfer(fi, pl, z0 + 2 * len / 3, Mul(vec, w1 * len / 2 * scale), moment);
            }
        }

        // a load inside a rigid end zone, at `s` from joint I, carried to its joint: the force and its moment there
        void Transfer(FrameInfo fi, PatternLoads pl, double s, double[] f, bool moment)
        {
            bool atI = s <= fi.ZoneI;
            int jid = JointId[atI ? fi.F.JointI : fi.F.JointJ];
            var v = Six(pl, pl.Joint, jid, 0);
            if (moment) { for (int i = 0; i < 3; i++) v[3 + i] += f[i]; return; }
            var r = Add(fi.Trans, Mul(fi.Ax[0], atI ? s : s - fi.L));
            var mm = Cross(r, f);
            for (int i = 0; i < 3; i++) { v[i] += f[i]; v[3 + i] += mm[i]; }
        }

        // a frame's self weight on its rigid end zones (STAAD's SELFWEIGHT loads the clear length)
        void ZoneSelfWeight(FrameInfo fi, PatternLoads pl)
        {
            double q = fi.WeightPerLength * pl.SelfWeight;
            if (q == 0) return;
            if (fi.ZoneI > 0) Transfer(fi, pl, fi.ZoneI / 2, new[] { 0, -q * fi.ZoneI, 0 }, false);
            if (fi.ZoneJ > 0) Transfer(fi, pl, fi.L - fi.ZoneJ / 2, new[] { 0, -q * fi.ZoneJ, 0 }, false);
        }

        // ---------------- cases: primary loads

        sealed class Primary
        {
            public FrameCase C; public int Number; public bool PDelta;
            public readonly List<KeyValuePair<int, double>> Refs = new List<KeyValuePair<int, double>>();   // reference load index, factor
        }

        List<Primary> Cases(List<KeyValuePair<FramePattern, PatternLoads>> refs)
        {
            var o = new List<Primary>();
            var index = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < refs.Count; i++) index[refs[i].Key.Label] = i;
            var byName = new Dictionary<string, FrameCase>(StringComparer.Ordinal);
            foreach (var c in M.Cases) if (!byName.ContainsKey(c.Label)) byName[c.Label] = c;
            var notWritten = new List<string>();
            foreach (var c in M.Cases)
            {
                string type = c.Type;
                if (!Is(type, "LinStatic") && !Is(type, "NonStatic")) { notWritten.Add(c.Label + " (" + type + ")"); continue; }
                var pr = new Primary { C = c, Number = o.Count + 1 };
                var factors = new Dictionary<int, double>();
                bool ok = true;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var cur = c;
                string geo = Is(type, "NonStatic") ? c.GeoNonLin : "None";
                while (cur != null && ok)
                {
                    if (!seen.Add(cur.Label)) { Refuse("load case " + c.Label + ": its initial conditions go round in a circle"); ok = false; break; }
                    if (Is(cur.GeoNonLin, "P-Delta plus Large Displacements") && Is(cur.Type, "NonStatic")) { Refuse("load case " + cur.Label + ": large displacements (STAAD: P-Delta at most)"); ok = false; break; }
                    // a nonlinear case continues its initial one (its loads too); a linear one only takes its stiffness
                    if (cur == c || Is(cur.Type, "NonStatic"))
                        foreach (var x in cur.Loads)
                        {
                            if (!Is(x[0], "Load pattern")) { Refuse("load case " + cur.Label + ": a load of type " + x[0] + " (" + x[1] + ")"); ok = false; break; }
                            int k;
                            if (!index.TryGetValue(x[1], out k)) { Refuse("load case " + cur.Label + ": pattern \"" + x[1] + "\" is not in the model"); ok = false; break; }
                            double sf; if (!double.TryParse(x[2], NumberStyles.Float, Inv, out sf)) sf = 1;
                            double have; factors.TryGetValue(k, out have); factors[k] = have + sf;
                        }
                    string init = cur.Initial;
                    if (string.IsNullOrEmpty(init) || Is(init, "Zero")) break;
                    FrameCase next;
                    if (!byName.TryGetValue(init, out next)) { Refuse("load case " + cur.Label + ": initial case \"" + init + "\" is not in the model"); ok = false; break; }
                    if (!Is(next.Type, "NonStatic")) { Refuse("load case " + cur.Label + ": starts from " + next.Label + " (" + next.Type + ")"); ok = false; break; }
                    string g2 = next.GeoNonLin;
                    if (Is(cur.Type, "LinStatic"))
                    {
                        // a linear case on a nonlinear case's stiffness: the same stiffness here (linear links), unless P-Delta
                        if (!Is(g2, "None")) { Refuse("load case " + cur.Label + ": uses the P-Delta stiffness of " + next.Label + " (STAAD has no such case)"); ok = false; }
                        else Note("load case " + cur.Label + ": SAP takes its stiffness from " + next.Label + ", the same as from zero in this model (no nonlinear links)");
                        break;
                    }
                    if (!Is(g2, geo)) { Refuse("load case " + cur.Label + ": " + geo + " continuing " + next.Label + " (" + g2 + ")"); ok = false; break; }
                    if (cur == c) Note("load case " + c.Label + ": continues " + next.Label + "; its STAAD load holds both cases' loads, from zero (the same result for a model with linear links)");
                    cur = next;
                }
                if (!ok) continue;
                pr.PDelta = Is(geo, "P-Delta");
                foreach (var kv in factors.OrderBy(x => x.Key)) if (kv.Value != 0) pr.Refs.Add(kv);
                o.Add(pr);
            }
            if (notWritten.Count > 0) Note("load cases not written (STAAD gets static cases only; the structure is the same): " + string.Join(", ", notWritten.ToArray()));
            if (o.Any(p => p.PDelta) && o.Any(p => !p.PDelta))
                Refuse("P-Delta in load cases " + string.Join(" ", o.Where(p => p.PDelta).Select(p => p.C.Label).ToArray()) + " but not in " + string.Join(" ", o.Where(p => !p.PDelta).Select(p => p.C.Label).ToArray()) +
                       " (STAAD's P-Delta analysis takes every load case: give all or none P-Delta)");
            return o;
        }

        // ---------------- combinations

        sealed class Combination { public string Name, Kind; public int Number; public List<KeyValuePair<int, double>> Items; }

        List<Combination> Combos(List<Primary> cases)
        {
            var o = new List<Combination>();
            var number = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var p in cases) number[p.C.Label] = p.Number;
            var byName = new Dictionary<string, FrameCombo>(StringComparer.Ordinal);
            foreach (var c in M.Combos) if (!byName.ContainsKey(c.Label)) byName[c.Label] = c;
            var notWritten = new List<string>();
            int next = cases.Count;
            foreach (var c in M.Combos)
            {
                string type = c.Type, kind;
                if (Is(type, "Linear Add")) kind = "";
                else if (Is(type, "SRSS")) kind = "SRSS";
                else if (Is(type, "Abs Add")) kind = "ABS";
                else { notWritten.Add(c.Label + " (" + type + ")"); continue; }
                string why;
                var items = kind == "" ? Linear(c, number, byName, new HashSet<string>(), out why) : Direct(c, number, out why);
                if (items == null) { notWritten.Add(c.Label + " (" + why + ")"); continue; }
                var list = items.Where(kv => kv.Value != 0).OrderBy(kv => kv.Key).ToList();
                if (list.Count == 0) { notWritten.Add(c.Label + " (its factors cancel)"); continue; }
                o.Add(new Combination { Name = c.Label, Kind = kind, Number = ++next, Items = list });
            }
            if (notWritten.Count > 0) Note("combinations not written: " + string.Join(", ", notWritten.ToArray()));
            if (o.Count > 0 && cases.Any(p => p.PDelta)) Note("combinations add P-Delta results as SAP's do (STAAD warns that this is not a second-order result)");
            return o;
        }

        // a Linear Add's primary loads and factors, nested Linear Adds expanded; null with the reason when it holds anything else
        static Dictionary<int, double> Linear(FrameCombo c, Dictionary<string, int> number, Dictionary<string, FrameCombo> byName, HashSet<string> seen, out string why)
        {
            why = null;
            var o = new Dictionary<int, double>();
            if (!seen.Add(c.Label)) { why = "it contains itself"; return null; }
            foreach (var it in c.Items)
            {
                double sf; if (!double.TryParse(it[2], NumberStyles.Float, Inv, out sf)) sf = 1;
                int n;
                FrameCombo inner;
                if (Is(it[0], "Response Combo") && byName.TryGetValue(it[1], out inner))
                {
                    if (!Is(inner.Type, "Linear Add")) { why = it[1] + " is " + inner.Type; return null; }
                    var sub = Linear(inner, number, byName, seen, out why);
                    if (sub == null) return null;
                    foreach (var kv in sub) { double h; o.TryGetValue(kv.Key, out h); o[kv.Key] = h + sf * kv.Value; }
                    continue;
                }
                if (!number.TryGetValue(it[1], out n)) { why = it[1] + " is not written"; return null; }
                double have; o.TryGetValue(n, out have); o[n] = have + sf;
            }
            seen.Remove(c.Label);
            return o;
        }

        static Dictionary<int, double> Direct(FrameCombo c, Dictionary<string, int> number, out string why)
        {
            why = null;
            var o = new Dictionary<int, double>();
            foreach (var it in c.Items)
            {
                int n;
                if (Is(it[0], "Response Combo")) { why = "of combination " + it[1]; return null; }
                if (!number.TryGetValue(it[1], out n)) { why = it[1] + " is not written"; return null; }
                double sf; if (!double.TryParse(it[2], NumberStyles.Float, Inv, out sf)) sf = 1;
                if (o.ContainsKey(n)) { why = it[1] + " twice"; return null; }
                o[n] = sf;
            }
            return o;
        }

        // SAP's pattern / case design type as STAAD's LOADTYPE
        static string LoadType(string sap)
        {
            string t = (sap ?? "").Replace(" ", "").ToUpperInvariant();
            if (t == "DEAD" || t == "SUPERDEAD") return "Dead";
            if (t == "LIVE" || t == "REDUCELIVE" || t == "PATTERNLIVE") return "Live";
            if (t == "ROOFLIVE") return "Roof Live";
            if (t == "QUAKE") return "Seismic-H";
            if (t == "WIND") return "Wind";
            if (t == "SNOW") return "Snow";
            if (t == "TEMPERATURE" || t == "TEMPERATUREGRADIENT") return "Temperature";
            return "None";
        }

        // ---------------- the file

        string Emit(List<KeyValuePair<FramePattern, PatternLoads>> refs, List<Primary> cases, List<Combination> combos)
        {
            var o = new List<string>();
            Action<string> line = s => { foreach (var x in Wrap(s, 76)) o.Add(x); };
            Action<string> comment = s => { foreach (var x in Wrap(Plain((s ?? "").Replace(';', ','), 2000), 72)) o.Add("* " + (x.EndsWith(" -") ? x.Substring(0, x.Length - 2) : x)); };
            o.Add("INPUT WIDTH 79");
            o.Add("UNIT " + StaadLen + " " + StaadForce);

            o.Add("JOINT COORDINATES");
            foreach (var j in M.Joints)
            {
                if (!Used.Contains(JointId[j.Label])) continue;
                var p = R(Pos[j.Label]);
                o.Add(JointId[j.Label].ToString(Inv) + " " + N(p[0]) + " " + N(p[1]) + " " + N(p[2]));
            }
            if (Frames.Count + Trusses.Count > 0)
            {
                o.Add("MEMBER INCIDENCES");
                foreach (var f in Frames) o.Add(f.Id.ToString(Inv) + " " + JointId[f.F.JointI].ToString(Inv) + " " + JointId[f.F.JointJ].ToString(Inv));
                foreach (var t in Trusses) o.Add(t[0].ToString(Inv) + " " + t[1].ToString(Inv) + " " + t[2].ToString(Inv));
            }
            if (Plates.Count > 0)
            {
                o.Add("ELEMENT INCIDENCES SHELL");
                foreach (var p in Plates) o.Add(string.Join(" ", p.Select(x => x.ToString(Inv)).ToArray()));
                if (PlaneStress.Count > 0) { o.Add("ELEMENT PLANE STRESS"); line(Condense(PlaneStress.Select(a => AreaId[a]))); }
            }
            Groups(o, line);

            // materials: SAP's own values (G written: STAAD then takes it, not E and Poisson's ratio)
            o.Add("DEFINE MATERIAL START");
            bool links = Trusses.Count > 0;
            foreach (var md in MatDefs)
            {
                var m = M.Materials.First(x => x.Label == md[1]);
                double factor = double.Parse(md[2], Inv), nu = m.Mech("U12");
                o.Add("ISOTROPIC " + md[0]);
                o.Add("E " + N(m.Mech("E1")));
                if (nu >= 0.01 && nu <= 0.499) o.Add("POISSON " + N(nu));
                if (m.Mech("G12") > 0) o.Add("G " + N(m.Mech("G12")));
                o.Add("DENSITY " + N(m.Mech("UnitWeight") * factor));
                o.Add("ALPHA " + N(m.Mech("A1")));
                double fy = m.SteelData("Fy"), fu = m.SteelData("Fu");
                if (Is(m.Type, "Steel"))
                {
                    o.Add("TYPE STEEL");
                    if (!double.IsNaN(fy) && !double.IsNaN(fu) && fy > 0 && fu > 0)
                    {
                        double ey = m.SteelData("EffFy"), eu = m.SteelData("EffFu");
                        string ry = !double.IsNaN(ey) && ey > 0 ? " RY " + N(ey / fy) : "", rt = !double.IsNaN(eu) && eu > 0 ? " RT " + N(eu / fu) : "";
                        o.Add("STRENGTH FY " + N(fy) + " FU " + N(fu) + ry + rt);
                    }
                }
            }
            if (links) { o.Add("ISOTROPIC PL_LINK"); o.Add("E " + N(LinkE)); o.Add("POISSON 0.3"); o.Add("DENSITY 0"); o.Add("ALPHA 0"); }
            o.Add("END DEFINE MATERIAL");

            if (Frames.Count + Trusses.Count > 0)
            {
                o.Add("MEMBER PROPERTY AMERICAN");
                var props = new List<KeyValuePair<string, int>>();
                foreach (var f in Frames) props.Add(new KeyValuePair<string, int>(f.Table != null ? "TABLE ST " + f.Table : Pris(f), f.Id));
                foreach (var t in Trusses) props.Add(new KeyValuePair<string, int>("PRIS AX " + N(TrussAx[t[0]]), t[0]));
                foreach (var g in props.GroupBy(x => x.Key)) line(Condense(g.Select(x => x.Value)) + " " + g.Key);
            }
            if (Plates.Count > 0)
            {
                o.Add("ELEMENT PROPERTY");
                foreach (var g in PlateThick.GroupBy(kv => N(kv.Value))) line(Condense(g.Select(kv => kv.Key)) + " THICKNESS " + g.Key);
            }
            o.Add("CONSTANTS");
            var mats = Frames.Select(f => new KeyValuePair<string, int>(f.Mat, f.Id)).Concat(PlateMat.Select(kv => new KeyValuePair<string, int>(kv.Value, kv.Key)))
                             .Concat(Trusses.Select(t => new KeyValuePair<string, int>("PL_LINK", t[0])));
            foreach (var g in mats.GroupBy(x => x.Key)) line("MATERIAL " + g.Key + " MEMB " + Condense(g.Select(x => x.Value)));
            foreach (var g in Frames.Where(f => f.Beta != 0).GroupBy(f => N(f.Beta))) line("BETA " + g.Key + " MEMB " + Condense(g.Select(f => f.Id)));

            var off = Frames.Where(f => Len(f.OffI) > 0 || Len(f.OffJ) > 0).ToList();
            if (off.Count > 0)
            {
                o.Add("MEMBER OFFSET");
                foreach (var f in off)
                {
                    if (Len(f.OffI) > 0) o.Add(f.Id.ToString(Inv) + " START " + string.Join(" ", f.OffI.Select(x => N(x, f.L)).ToArray()));
                    if (Len(f.OffJ) > 0) o.Add(f.Id.ToString(Inv) + " END " + string.Join(" ", f.OffJ.Select(x => N(x, f.L)).ToArray()));
                }
            }
            var rel = Frames.Where(f => f.Rel[0] != "" || f.Rel[1] != "").ToList();
            if (rel.Count > 0)
            {
                o.Add("MEMBER RELEASE");
                foreach (var f in rel)
                {
                    if (f.Rel[0] == f.Rel[1]) { o.Add(f.Id.ToString(Inv) + " BOTH " + f.Rel[0]); continue; }
                    if (f.Rel[0] != "") o.Add(f.Id.ToString(Inv) + " START " + f.Rel[0]);
                    if (f.Rel[1] != "") o.Add(f.Id.ToString(Inv) + " END " + f.Rel[1]);
                }
            }
            if (Trusses.Count > 0) { o.Add("MEMBER TRUSS"); line(Condense(Trusses.Select(t => t[0]))); }

            // supports
            var sup = new List<string>();
            foreach (var j in M.Joints)
            {
                List<Support> parts;
                if (!Supports.TryGetValue(j.Label, out parts) || !Used.Contains(JointId[j.Label])) continue;
                bool inc;
                string text = SupportText(j.Label, parts, out inc);
                if (text == null) { Refuse("joint " + j.Label + ": its " + string.Join(", ", parts.Select(p => p.From).ToArray()) + " act in axes no STAAD support holds together"); continue; }
                if (text.EndsWith("FIXED BUT FX FY FZ MX MY MZ")) continue;
                sup.Add(text);
            }
            if (Refused.Count > 0) throw new FrameStdRefusal(Grouped());
            if (sup.Count > 0) { o.Add("SUPPORTS"); foreach (var s in sup) line(s); }
            if (sup.Any(s => s.Contains(" INCLINED "))) Note("inclined supports: STAAD prints their reactions in global axes (SET INCLINED REACTION for the inclined ones)");

            // loads
            var refNo = new Dictionary<int, int>();
            var used = refs.Select((kv, i) => i).Where(i => !refs[i].Value.Empty).ToList();
            if (used.Count > 0)
            {
                o.Add("DEFINE REFERENCE LOADS");
                foreach (int i in used)
                {
                    var pat = refs[i].Key; var pl = refs[i].Value;
                    refNo[i] = refNo.Count + 1;
                    o.Add("LOAD R" + refNo[i].ToString(Inv) + " LOADTYPE " + LoadType(pat.DesignType) + " TITLE " + Plain(pat.Label, 60));
                    if (pl.SelfWeight != 0) o.Add("SELFWEIGHT Y " + N(-pl.SelfWeight));
                    var jl = pl.Joint.Where(kv => Used.Contains(kv.Key) && kv.Value.Any(x => Math.Abs(x) > 0)).ToList();
                    if (jl.Count > 0)
                    {
                        o.Add("JOINT LOAD");
                        string[] k = { "FX", "FY", "FZ", "MX", "MY", "MZ" };
                        foreach (var kv in jl) line(kv.Key.ToString(Inv) + " " + string.Join(" ", Enumerable.Range(0, 6).Where(x => kv.Value[x] != 0).Select(x => k[x] + " " + N(kv.Value[x])).ToArray()));
                    }
                    if (pl.Member.Count > 0) { o.Add("MEMBER LOAD"); foreach (var s in pl.Member) line(s); }
                    var tl = pl.Temp.Where(kv => kv.Value.Any(x => x != 0)).ToList();
                    if (tl.Count > 0)
                    {
                        o.Add("TEMPERATURE LOAD");
                        foreach (var g in tl.GroupBy(kv => TempText(kv.Value))) line(Condense(g.Select(kv => kv.Key)) + " " + g.Key);
                    }
                }
                o.Add("END DEFINE REFERENCE LOADS");
            }
            foreach (var p in cases)
            {
                string type = p.C.Rec(FrameCase.Defs) == null ? null : p.C.Rec(FrameCase.Defs).Get("DesignType");
                if (string.IsNullOrEmpty(type) && p.Refs.Count > 0) type = refs[p.Refs[0].Key].Key.DesignType;
                o.Add("LOAD " + p.Number.ToString(Inv) + " LOADTYPE " + LoadType(type) + " TITLE " + Plain(p.C.Label, 60));
                var r = p.Refs.Where(kv => refNo.ContainsKey(kv.Key)).ToList();
                if (r.Count > 0) { o.Add("REFERENCE LOAD"); line(string.Join(" ", r.Select(kv => "R" + refNo[kv.Key].ToString(Inv) + " " + N(kv.Value)).ToArray())); }
                var disp = new SortedDictionary<int, double[]>();
                foreach (var kv in p.Refs)
                    foreach (var d in refs[kv.Key].Value.Disp)
                    {
                        if (!Used.Contains(d.Key)) continue;
                        double[] v;
                        if (!disp.TryGetValue(d.Key, out v)) disp[d.Key] = v = new double[6];
                        for (int i = 0; i < 6; i++) if (!double.IsNaN(d.Value[i])) v[i] += kv.Value * d.Value[i];
                    }
                var dl = disp.Where(kv => kv.Value.Any(x => x != 0)).ToList();
                if (dl.Count > 0)
                {
                    o.Add("SUPPORT DISPLACEMENT LOAD");
                    string[] k = { "FX", "FY", "FZ", "MX", "MY", "MZ" };
                    foreach (var kv in dl) for (int i = 0; i < 6; i++) if (kv.Value[i] != 0) o.Add(kv.Key.ToString(Inv) + " " + k[i] + " " + N(kv.Value[i]));
                }
                if (r.Count == 0 && dl.Count == 0) Note("load case " + p.C.Label + " (LOAD " + p.Number.ToString(Inv) + ") has no loads");
            }
            foreach (var c in combos)
            {
                o.Add("LOAD COMBINATION " + (c.Kind == "" ? "" : c.Kind + " ") + c.Number.ToString(Inv) + " " + Plain(c.Name, 60));
                line(string.Join(" ", c.Items.Select(kv => kv.Key.ToString(Inv) + " " + N(kv.Value)).ToArray()));
            }

            o.Add(cases.Any(p => p.PDelta) ? "PDELTA 30 ANALYSIS SMALLDELTA" : "PERFORM ANALYSIS");
            if (cases.Any(p => p.PDelta)) Note("P-Delta: STAAD's PDELTA 30 ANALYSIS SMALLDELTA (its own iteration, not SAP's)");
            o.Add("PRINT JOINT DISPLACEMENTS LIST ALL");
            if (Frames.Count + Trusses.Count > 0) o.Add("PRINT MEMBER FORCES ALL");
            if (sup.Count > 0) o.Add("PRINT SUPPORT REACTIONS");
            if (Plates.Count > 0) o.Add("PRINT ELEMENT JOINT STRESSES LIST ALL");
            o.Add("FINISH");
            // the head, last: groups, supports and loads add to the labels and notes
            var body = o.ToList();
            o.Clear();
            o.Add("STAAD SPACE");
            comment("Written by Pluto (Build-Frame -Std) from " + Path.GetFileName(Source) + ": SAP2000 " + M.Version + ", units " + M.Units + ".");
            comment("SAP's Z up is STAAD's Y up: SAP (X, Y, Z) = STAAD (X, -Z, Y). Each member's BETA keeps SAP's local 2 / 3 as STAAD's y / z.");
            if (LabelLines.Count > 0) { comment("SAP labels and their STAAD numbers:"); foreach (var l in LabelLines) comment("  " + l); }
            if (cases.Count > 0) { comment("Load cases:"); foreach (var p in cases) comment("  LOAD " + p.Number.ToString(Inv) + " = SAP case " + p.C.Label + (p.PDelta ? " (P-Delta)" : "")); }
            foreach (var c in combos) comment("  LOAD " + c.Number.ToString(Inv) + " = SAP combination " + c.Name);
            if (Notes.Count > 0) { comment("Notes:"); foreach (var n in Notes) comment("  " + n); }
            o.AddRange(body);
            return string.Join("\r\n", o.ToArray()) + "\r\n";
        }

        static string TempText(double[] v)
        {
            if (v[1] == 0 && v[2] == 0) return "TEMP " + N(v[0]);
            if (v[2] == 0) return "TEMP " + N(v[0]) + " " + N(v[1]);
            return "TEMP " + N(v[0]) + " " + N(v[1]) + " " + N(v[2]);
        }

        static string Pris(FrameInfo f)
        {
            var p = f.P; var m = f.Mods;      // AMod AS2Mod AS3Mod JMod I22Mod I33Mod
            var s = new StringBuilder("PRIS AX " + N(p.A * m[0]) + " IX " + N(p.J * m[3]) + " IY " + N(p.I22 * m[4]) + " IZ " + N(p.I33 * m[5]));
            if (p.AS2 > 0) s.Append(" AY " + N(p.AS2 * m[1]));
            if (p.AS3 > 0) s.Append(" AZ " + N(p.AS3 * m[2]));
            if (p.Depth > 0) s.Append(" YD " + N(p.Depth));
            if (p.Width > 0) s.Append(" ZD " + N(p.Width));
            return s.ToString();
        }

        // SAP's groups as STAAD's: _NAME (24 characters at most) per kind; a group of several kinds gets _J / _M / _E;
        // an object STAAD already has in 4 groups is left out of the next
        void Groups(List<string> o, Action<string> line)
        {
            var count = new Dictionary<string, int>(StringComparer.Ordinal);
            var names = new HashSet<string>(StringComparer.Ordinal);
            var blocks = new List<string[]>();     // kind, name, list
            var full = new List<string>();
            foreach (var g in M.Groups)
            {
                if (Is(g.Label, "ALL")) continue;
                var ids = new Dictionary<string, List<int>> { { "JOINT", new List<int>() }, { "MEMBER", new List<int>() }, { "ELEMENT", new List<int>() } };
                foreach (var mm in g.Members)
                {
                    string t = mm[0].ToLowerInvariant(), l = mm[1];
                    int id; FrameInfo fi;
                    if (t == "joint" && JointId.TryGetValue(l, out id) && Used.Contains(id)) ids["JOINT"].Add(id);
                    else if (t == "frame" && FrameOf.TryGetValue(l, out fi)) ids["MEMBER"].Add(fi.Id);
                    else if (t == "link" && TrussId.TryGetValue(l, out id)) ids["MEMBER"].Add(id);
                    else if (t == "area" && AreaId.TryGetValue(l, out id) && Plates.Any(p => p[0] == id)) ids["ELEMENT"].Add(id);
                }
                var kinds = ids.Where(kv => kv.Value.Count > 0).ToList();
                foreach (var kv in kinds)
                {
                    string name = Name(g.Label, "_", kinds.Count > 1 ? 22 : 24) + (kinds.Count > 1 ? "_" + kv.Key.Substring(0, 1) : "");
                    string bare = name; int n = 2;
                    while (!names.Add(name)) { string suf = "_" + n.ToString(Inv); name = (bare.Length + suf.Length > 24 ? bare.Substring(0, 24 - suf.Length) : bare) + suf; n++; }
                    var keep = new List<int>();
                    foreach (int id in kv.Value.Distinct())
                    {
                        string key = kv.Key + id.ToString(Inv); int c; count.TryGetValue(key, out c);
                        if (c >= 4) { full.Add(kv.Key.ToLowerInvariant() + " " + id.ToString(Inv) + " not in " + name); continue; }
                        count[key] = c + 1; keep.Add(id);
                    }
                    if (keep.Count > 0) blocks.Add(new[] { kv.Key, name, Condense(keep) });
                    if (name != Name(g.Label, "_", 24)) LabelLines.Add("group " + g.Label + " = " + name);
                }
            }
            if (full.Count > 0) Note("STAAD keeps an object in 4 groups at most: " + string.Join("; ", full.ToArray()));
            if (blocks.Count == 0) return;
            o.Add("START GROUP DEFINITION");
            foreach (string kind in new[] { "JOINT", "MEMBER", "ELEMENT" })
            {
                var b = blocks.Where(x => x[0] == kind).ToList();
                if (b.Count == 0) continue;
                o.Add(kind);
                foreach (var x in b) line(x[1] + " " + x[2]);
            }
            o.Add("END GROUP DEFINITION");
        }
    }
}
