using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

// CaesarNeutralWriter  --  PipeModel -> CAESAR II neutral file (.cii), in the 15.01 layout CAESAR itself
// writes (the generic fixture scripts/caesar/testdata, read back by CaesarNeutralReader). C# 5.
//
// Layout (what the fixture shows; every line is reproduced byte for byte by this writer's formatting):
//   numbers   13-character columns after a 2-character indent: reals " d.ddddddE+xx" (a minus takes the
//             blank), integers right-aligned; text rows = a 12-character length, a space, the text.
//   VERSION   "5.00000 15.0100 1252" (CAESAR's neutral file version, CAESAR version, code page), then 60
//             77-character lines: PROJECT / CLIENT / ANALYST / NOTES headings and the generator line.
//   CONTROL   counts: elements nozzles hangers node-names reducers flanges / bends rigids expansion-joints
//             restraint-blocks displacement-blocks force-blocks / uniform wind offsets allowables
//             SIF&TEE-blocks IZUP / equipment.
//   ELEMENTS  per element 9 real rows: FROM TO DX DY DZ OD / WALL INSUL CORR T1 T2 T3 / T4..T9 / P1..P6 /
//             P7 P8 P9 E POISSON PIPE-DENSITY / INSUL-DENSITY FLUID-DENSITY 0 0 0 0 / 0 x6 / 0 0 0 0
//             TEE-ANGLE1 TEE-ANGLE2 (9999.99 = not set) / 0 x5 -- then the element name and line number (text rows), "-1 -1", and
//             the block pointers: BEND RIGID EXPJT RESTRANT DISPLMNT FORCMNT / UNIFORM WIND OFFSETS
//             ALLOWBLS SIF&TEES NODENAME / REDUCERS FLANGES EQUIPMNT (1-based, 0 = none). Properties are
//             written on every element. E, Poisson and the pipe density are 0 when CAESAR takes them from
//             its material database (MISCEL_1 material number); the fixture's job does exactly that.
//   BEND      3 lines: R TYPE ANGLE1 NODE1 ANGLE2 NODE2 / ANGLE3 NODE3 MITER FITTING-THK K 0 / 0 0
//   RIGID     WEIGHT TYPE;  EXPJT  K-AXIAL K-TRANS K-BEND K-TORSION EFF-ID
//   RESTRANT  6 slots of 4 lines: NODE TYPE STIFFNESS GAP MU CNODE / 3 cosines / tag / GUID (texts); CAESAR
//             writes cosines on every type but ANC (its axis; GUI and LIM a dummy 1 0 0), and so does this writer
//   ALLOWBLS  one block, on the first element (carried forward): the fixture's B31.1 block (allowables
//             from the material database), or a seed file's block verbatim.
//   SIF&TEES  2 slots of 7 lines: NODE TYPE SIF-IN SIF-OUT ... (welding tee surface node on line 5)
//   REDUCERS  2 lines: OD2 WALL2 ALPHA R1 R2 / 0 x5
//   MISCEL_1  one material number per element (6 per line), then 4 execution-option lines; the ambient
//             temperature is line 2 field 3.
//   UNITS     CAESAR's English set (in, lb, F, psi, lb/cu.in., COORDS in ft), the fixture's block.
//   COORDS    count, then NODE X Y Z (ft) per segment start.
// Restraints and SIF/tee entries go to an element that has the node as its FROM or TO (6 / 2 per
// element block); a node with more than its elements can hold is an error.

public static class CaesarNeutralWriter
{
    public const double RigidStiffness = 1e12;

    // ---------------- formatting (public: the fixture test re-emits every line through these)

    public static string Real(double v)
    {
        if (v == 0 || double.IsNaN(v)) v = 0.0;                   // no "-0"
        string s = v.ToString("0.000000E+00", CultureInfo.InvariantCulture);
        return s.Length >= 13 ? s : s.PadLeft(13);
    }

    public static string Int(long v) { return v.ToString(CultureInfo.InvariantCulture).PadLeft(13); }

    public static string RealRow(params double[] v)
    {
        var sb = new StringBuilder("  ");
        foreach (double x in v) sb.Append(Real(x));
        return sb.ToString();
    }

    public static string IntRow(params long[] v)
    {
        var sb = new StringBuilder("  ");
        foreach (long x in v) sb.Append(Int(x));
        return sb.ToString();
    }

    // integers (int / long) and reals (double) in one row, as the MISCEL_1 option lines are
    public static string MixedRow(params object[] v)
    {
        var sb = new StringBuilder("  ");
        foreach (object x in v)
        {
            if (x is double) sb.Append(Real((double)x));
            else sb.Append(Int(Convert.ToInt64(x, CultureInfo.InvariantCulture)));
        }
        return sb.ToString();
    }

    public static string TextRow(string text)
    {
        string t = Ascii(text ?? "");
        return t.Length.ToString(CultureInfo.InvariantCulture).PadLeft(12) + " " + t;
    }

    public static string Header(string name) { return "#$ " + name.PadRight(8); }

    static string Ascii(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s) sb.Append(c >= 32 && c < 127 ? c : '?');
        return sb.ToString();
    }

    // ---------------- templates (the generic fixture's own blocks)

    static readonly string VersionLine = "    5.00000      15.0100        1252";
    static readonly string GeneratorLine = "   Data generated by CAESAR II / Neutral file interface Ver 15.01 12/23      ";

    // B31.1 allowables, values from the material database (the fixture's block, 28 lines)
    static readonly double[][] AllowablesB311 =
    {
        new double[] { 0, 0, 0, 0, 1, 1 }, new double[] { 1, 0, 0, 9999.99, 0, 65 }, Zeros(6), new double[] { 1, 1, 1, 1, 1, 1 },
        Zeros(6), Zeros(6), Zeros(6), Zeros(6), Zeros(6), Zeros(6), Zeros(6), Zeros(6), Zeros(6), Zeros(6), Zeros(6), Zeros(6), Zeros(6),
        new double[] { 0, 0, 0, 9999.99, 0, 1 }, Zeros(6), Zeros(6), Zeros(6), Zeros(6), Zeros(6), Zeros(6), Zeros(6), Zeros(2), Zeros(6), Zeros(6)
    };

    static readonly string[] UnitsEnglish =
    {
        "   1.000000E+00 1.000000E+00 1.000000E+00 1.000000E+00 8.333330E-02 1.000000E+00",
        "   1.000000E+00 0.000000E+00 1.000000E+00 1.000000E+00 1.000000E+00 1.000000E+00",
        "   1.000000E+00 1.000000E+00 1.000000E+00 1.000000E+00 1.000000E+00 1.440000E+02",
        "   8.333330E-02 8.333330E-02 1.000000E+00 1.000000E+00",
        "  ENGLISH        ", "  ON ", "  in.", "  lb.", "  lbm", "  in.lb.", "  ft.lb.", "  lb./sq.in.", "  F", "   ",
        "  lb./sq.in.", "  lb./sq.in.", "  lb./cu.in.", "  lb./cu.in.", "  lb./cu.in.", "  lb./in.", "  in.lb./deg", "  lb./in.",
        "  g's", "  lb./sq.ft.", "  ft.", "  ft.", "  in.", "  in."
    };

    static double[] Zeros(int n) { return new double[n]; }

    // ---------------- the file

    public static string ToText(PipeModel m)
    {
        var err = Validate(m);
        if (err.Count > 0) throw new InvalidOperationException("CaesarNeutralWriter: " + string.Join("; ", err.ToArray()));
        int ne = m.Elements.Count;

        // block assignment, in element order
        var restraintsOf = new List<PipeModelRestraint>[ne];
        var sifsOf = new List<PipeModelSif>[ne];
        for (int i = 0; i < ne; i++) { restraintsOf[i] = new List<PipeModelRestraint>(); sifsOf[i] = new List<PipeModelSif>(); }
        Assign(m, m.Restraints, restraintsOf, 6, r => r.Node, "restraints");
        Assign(m, m.Sifs, sifsOf, 2, s => s.Node, "SIF / tee entries");

        int nBend = 0, nRigid = 0, nExp = 0, nRes = 0, nSif = 0, nRed = 0;
        var bendPtr = new int[ne]; var rigidPtr = new int[ne]; var expPtr = new int[ne]; var resPtr = new int[ne]; var sifPtr = new int[ne]; var redPtr = new int[ne];
        for (int i = 0; i < ne; i++)
        {
            var e = m.Elements[i];
            if (e.Bend != null) bendPtr[i] = ++nBend;
            if (e.Rigid != null) rigidPtr[i] = ++nRigid;
            if (e.ExpJoint != null) expPtr[i] = ++nExp;
            if (restraintsOf[i].Count > 0) resPtr[i] = ++nRes;
            if (sifsOf[i].Count > 0) sifPtr[i] = ++nSif;
            if (e.Reducer != null) redPtr[i] = ++nRed;
        }

        var w = new List<string>();
        // VERSION
        w.Add(Header("VERSION"));
        w.Add(VersionLine);
        w.Add(Cut77("    PROJECT: " + Ascii(m.Title ?? "")));
        w.Add(new string(' ', 77));
        w.Add(Pad77("    CLIENT :"));
        w.Add(new string(' ', 77));
        w.Add(Pad77("    ANALYST:"));
        w.Add(new string(' ', 77));
        w.Add(Cut77("    NOTES  : " + Ascii(m.Notes ?? "")));
        for (int k = 0; k < 52; k++) w.Add(new string(' ', 77));
        w.Add(GeneratorLine);
        // CONTROL
        w.Add(Header("CONTROL"));
        w.Add(IntRow(ne, 0, 0, 0, nRed, 0));
        w.Add(IntRow(nBend, nRigid, nExp, nRes, 0, 0));
        w.Add(IntRow(0, 0, 0, 1, nSif, m.ZUp ? 1 : 0));
        w.Add(IntRow(0));
        // ELEMENTS
        w.Add(Header("ELEMENTS"));
        for (int i = 0; i < ne; i++)
        {
            var e = m.Elements[i];
            w.Add(RealRow(e.From, e.To, e.Dx, e.Dy, e.Dz, e.Od));
            w.Add(RealRow(e.Wall, e.Insulation, e.Corrosion, e.T[0], e.T[1], e.T[2]));
            w.Add(RealRow(e.T[3], e.T[4], e.T[5], e.T[6], e.T[7], e.T[8]));
            w.Add(RealRow(e.P[0], e.P[1], e.P[2], e.P[3], e.P[4], e.P[5]));
            w.Add(RealRow(e.P[6], e.P[7], e.P[8], e.Emod, e.Poisson, e.PipeDensity));
            w.Add(RealRow(e.InsulationDensity, e.FluidDensity, 0, 0, 0, 0));
            w.Add(RealRow(0, 0, 0, 0, 0, 0));
            w.Add(RealRow(0, 0, 0, 0, e.TeeAngle1, e.TeeAngle2));
            w.Add(RealRow(0, 0, 0, 0, 0));
            w.Add(TextRow(e.Name));
            w.Add(TextRow(e.LineNumber));
            w.Add(IntRow(-1, -1));
            w.Add(IntRow(bendPtr[i], rigidPtr[i], expPtr[i], resPtr[i], 0, 0));
            w.Add(IntRow(0, 0, 0, i == 0 ? 1 : 0, sifPtr[i], 0));
            w.Add(IntRow(redPtr[i], 0, 0));
        }
        w.Add(Header("AUX_DATA"));
        // BEND
        w.Add(Header("BEND"));
        foreach (var e in m.Elements)
        {
            if (e.Bend == null) continue;
            var b = e.Bend;
            double[] an = new double[6];
            for (int k = 0; k < 3 && k < b.AngleNodes.Count; k++) { an[2 * k] = b.AngleNodes[k][0]; an[2 * k + 1] = b.AngleNodes[k][1]; }
            w.Add(RealRow(b.Radius, b.Type, an[0], an[1], an[2], an[3]));
            w.Add(RealRow(an[4], an[5], b.MiterPoints, b.FittingThickness, b.KFactor, 0));
            w.Add(RealRow(0, 0));
        }
        // RIGID
        w.Add(Header("RIGID"));
        foreach (var e in m.Elements) if (e.Rigid != null) w.Add(RealRow(e.Rigid.Weight, e.Rigid.Type));
        // EXPJT
        w.Add(Header("EXPJT"));
        foreach (var e in m.Elements)
        {
            if (e.ExpJoint == null) continue;
            var x = e.ExpJoint;
            w.Add(RealRow(x.AxialStiffness, x.TransverseStiffness, x.BendingStiffness, x.TorsionStiffness, x.EffectiveId));
        }
        // RESTRANT
        w.Add(Header("RESTRANT"));
        for (int i = 0; i < ne; i++)
        {
            if (restraintsOf[i].Count == 0) continue;
            for (int s = 0; s < 6; s++)
            {
                if (s < restraintsOf[i].Count)
                {
                    var r = restraintsOf[i][s];
                    double k = r.Stiffness > 0 ? r.Stiffness : RigidStiffness;
                    w.Add(RealRow(r.Node, r.Code, k, r.Gap, r.Mu, r.CNode));
                    var c = r.Cosines ?? PipeModelCodes.DefaultCosines(r.Type);
                    w.Add(RealRow(c[0], c[1], c[2]));
                    w.Add(TextRow(r.Tag));
                    w.Add(TextRow(""));
                }
                else
                {
                    w.Add(RealRow(0, 0, 0, 0, 0, 0));
                    w.Add(RealRow(0, 0, 0));
                    w.Add(TextRow(""));
                    w.Add(TextRow(""));
                }
            }
        }
        foreach (string h in new[] { "DISPLMNT", "FORCMNT", "UNIFORM", "WIND", "OFFSETS" }) w.Add(Header(h));
        // ALLOWBLS
        w.Add(Header("ALLOWBLS"));
        if (m.AllowablesBlock != null) w.AddRange(m.AllowablesBlock);
        else foreach (var row in AllowablesB311) w.Add(RealRow(row));
        // SIF&TEES
        w.Add(Header("SIF&TEES"));
        for (int i = 0; i < ne; i++)
        {
            if (sifsOf[i].Count == 0) continue;
            for (int s = 0; s < 2; s++)
            {
                if (s < sifsOf[i].Count)
                {
                    var t = sifsOf[i][s];
                    w.Add(RealRow(t.Node, t.Type, t.SifIn, t.SifOut, 0, 0));
                    w.Add(RealRow(0, 0, 0, 0, 0, 0));
                    w.Add(RealRow(0, 0, 0, 0, 0, 0));
                    w.Add(RealRow(0, 0, 0, 0, 1, 9999.99));
                    w.Add(RealRow(0, 0, t.SurfaceNode, 0, 0, 0));
                    w.Add(RealRow(0, 0, 0, 0, 0, 0));
                    w.Add(RealRow(0, 0, 0, 0, 0, 0));
                }
                else
                {
                    w.Add(RealRow(0, 0, 0, 0, 0, 0));
                    w.Add(RealRow(0, 0, 0, 0, 0, 0));
                    w.Add(RealRow(0, 0, 0, 0, 0, 0));
                    w.Add(RealRow(0, 0, 0, 0, 9999.99, 9999.99));
                    w.Add(RealRow(0, 0, 0, 0, 0, 0));
                    w.Add(RealRow(0, 0, 0, 0, 0, 0));
                    w.Add(RealRow(0, 0, 0, 0, 0, 0));
                }
            }
        }
        // REDUCERS
        w.Add(Header("REDUCERS"));
        foreach (var e in m.Elements)
        {
            if (e.Reducer == null) continue;
            var r = e.Reducer;
            w.Add(RealRow(r.Od2, r.Wall2, r.Alpha, r.R1, r.R2));
            w.Add(RealRow(0, 0, 0, 0, 0));
        }
        w.Add(Header("FLANGES"));
        w.Add(Header("EQUIPMNT"));
        // MISCEL_1: materials, then the execution options (ambient temperature in line 2)
        w.Add(Header("MISCEL_1"));
        for (int i = 0; i < ne; i += 6)
        {
            var row = new List<double>();
            for (int k = i; k < Math.Min(ne, i + 6); k++) row.Add(m.Elements[k].Material);
            w.Add(RealRow(row.ToArray()));
        }
        w.Add(MixedRow(1, 0, 0, 2, 0.0, 1));
        w.Add(MixedRow(0, 0, m.Ambient, 12.0, 0, 0));
        w.Add(MixedRow(0, 0, 0, 0, 0.25, 3));
        w.Add(MixedRow(3, 0));
        // UNITS, COORDS
        w.Add(Header("UNITS"));
        w.AddRange(UnitsEnglish);
        w.Add(Header("COORDS"));
        w.Add(IntRow(m.Coords.Count));
        foreach (var c in m.Coords) w.Add("  " + Int(c.Node) + Real(c.X / 12.0) + Real(c.Y / 12.0) + Real(c.Z / 12.0));
        return string.Join("\r\n", w.ToArray()) + "\r\n";
    }

    public static void Write(PipeModel m, string path)
    {
        File.WriteAllText(path, ToText(m), new UTF8Encoding(false));
    }

    static string Pad77(string s) { return s.Length >= 77 ? s : s.PadRight(77); }

    // a fixed-width line never grows past its width (a long title is cut, not wrapped)
    static string Cut77(string s) { s = Pad77(s); return s.Length > 77 ? s.Substring(0, 77) : s; }

    static void Assign<T>(PipeModel m, List<T> items, List<T>[] slots, int perElement, Func<T, int> nodeOf, string what)
    {
        var touching = new Dictionary<int, List<int>>();
        for (int i = 0; i < m.Elements.Count; i++)
        {
            var e = m.Elements[i];
            var ends = new List<int> { e.To, e.From };
            if (e.Bend != null) foreach (var an in e.Bend.AngleNodes) ends.Add((int)Math.Round(an[1]));   // a bend's own nodes belong to its element
            foreach (int n in ends)
            {
                if (n <= 0) continue;
                List<int> l;
                if (!touching.TryGetValue(n, out l)) { l = new List<int>(); touching[n] = l; }
                if (!l.Contains(i)) l.Add(i);
            }
        }
        foreach (var it in items)
        {
            int n = nodeOf(it);
            List<int> els;
            if (!touching.TryGetValue(n, out els)) throw new InvalidOperationException("CaesarNeutralWriter: " + what + " at node " + n.ToString(CultureInfo.InvariantCulture) + ", which no element has");
            int pick = -1;
            foreach (int i in els) if (slots[i].Count < perElement) { pick = i; break; }
            if (pick < 0) throw new InvalidOperationException("CaesarNeutralWriter: too many " + what + " at node " + n.ToString(CultureInfo.InvariantCulture) +
                " (" + perElement.ToString(CultureInfo.InvariantCulture) + " per element touching it, " + els.Count.ToString(CultureInfo.InvariantCulture) + " element(s))");
            slots[pick].Add(it);
        }
    }

    public static List<string> Validate(PipeModel m)
    {
        var err = new List<string>();
        if (m.Elements.Count == 0) err.Add("no elements");
        int i = 0;
        foreach (var e in m.Elements)
        {
            i++;
            if (e.From <= 0 || e.To <= 0 || e.From == e.To) err.Add("element " + i.ToString(CultureInfo.InvariantCulture) + ": node numbers " + e.From + " -> " + e.To);
            if (!(e.Od > 0) || !(e.Wall > 0) || e.Wall * 2 >= e.Od) err.Add("element " + e.From + "-" + e.To + ": OD " + e.Od + " / wall " + e.Wall);
            if (e.Material <= 0) err.Add("element " + e.From + "-" + e.To + ": no material number");
            if (e.Bend != null && e.Bend.AngleNodes.Count > 3) err.Add("element " + e.From + "-" + e.To + ": more than 3 bend nodes");
            if (Math.Max(Math.Abs(e.From), Math.Abs(e.To)) > 999999) err.Add("element " + i + ": node number above 999999");
        }
        if (m.Coords.Count == 0) err.Add("no COORDS entry (the first node needs a position)");
        return err;
    }
}
