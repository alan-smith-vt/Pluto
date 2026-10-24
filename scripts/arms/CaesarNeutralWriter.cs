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
//   DISPLMNT, FORCMNT  2 slots of 10 lines: the NODE alone, then one line per load vector (9): DX DY DZ RX RY
//             RZ / FX FY FZ MX MY MZ; 9999.99 = not given (a free DOF). An unused slot: node 0, then 9999.99
//             (displacements, as 11.00 jobs show) or 0 (forces, as a CAESAR 15.01 export shows). CAESAR II's
//             documented layout; FORCMNT confirmed by a 15.01 export, DISPLMNT seen in 11.00 jobs.
//   UNIFORM   6 lines, 36 values in order: 9 vectors x (UX UY UZ, 1 = in g's / 0 = force per length)
//   WIND      1 line: TYPE (0 wind, 1 wave, 2 off) SHAPE-FACTOR (wave: drag) ADDED-MASS LIFT MARINE-GROWTH DENSITY
//             (UNIFORM and WIND confirmed by 15.01 exports). A new block's unused vectors take the g flag its used
//             vectors share, so CAESAR does not read a block of g loads as mixing g's and force per length.
//   REDUCERS  2 lines: OD2 WALL2 ALPHA R1 R2 / 0 x5
//   MISCEL_1  one material number per element (6 per line), then 4 execution-option lines; the ambient
//             temperature is line 2 field 3.
//   UNITS     CAESAR's English set (in, lb, F, psi, lb/cu.in., COORDS in ft), the fixture's block.
//   COORDS    count, then NODE X Y Z (ft) per segment start.
// Restraints, SIF/tee entries, forces and displacements go to an element that has the node as its FROM or TO
// (6 / 2 / 2 / 2 per element block); a node with more than its elements can hold is an error. Uniform loads
// and wind carry forward in CAESAR (a block acts on its element and every later one until another changes it):
// a block where the load starts or changes, a zero block (uniform) or an "off" block (wind) where it stops.
//
// Overlay and carry (a model imported from a .cii, PipeModel.Source): every record, block and slot the model
// carries as written is given back from its own lines, with only the fields that CHANGED rewritten in their
// 13-character columns -- so an unedited import is written back identically, in its own CAESAR version's
// layout, line ending and code page. Blocks keep CAESAR's numbering (each block's Seq); a new block goes after
// them. A restraint / SIF / displacement slot goes back to the element block that held it while that element
// still has its node, otherwise to the first element at the node with room. The blocks the model does not read
// (offsets, allowables, node names, flanges, equipment) travel with their element.
// VERSION, CONTROL (counts recomputed), UNITS and the MISCEL_1 tail (nozzles, hangers, options; the ambient
// overlaid) come from the file. A new model gets the 15.01 English templates throughout.

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

    // ---------------- column overlay: a field rewritten only when its value changed

    static string Column(string line, int col)
    {
        int at = 2 + 13 * col;
        if (line == null || line.Length <= at) return "";
        return line.Substring(at, Math.Min(13, line.Length - at));
    }

    static bool TryNum(string field, out double v)
    {
        string f = (field ?? "").Trim();
        if (f.IndexOf('D') >= 0 || f.IndexOf('d') >= 0) f = f.Replace('D', 'E').Replace('d', 'e');
        return double.TryParse(f, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }

    static bool Same(double a, double b)
    {
        if (a == b) return true;
        if (double.IsNaN(a) || double.IsNaN(b)) return false;
        return Math.Abs(a - b) <= 1e-12 * Math.Max(Math.Abs(a), Math.Abs(b));
    }

    static string Put(string line, int col, string field)
    {
        int at = 2 + 13 * col;
        string l = line ?? "";
        if (l.Length < at) l = l.PadRight(at);
        string tail = l.Length > at + 13 ? l.Substring(at + 13) : "";
        return l.Substring(0, at) + field + tail;
    }

    // a real field: kept as written when it holds the same value
    static string SetReal(string line, int col, double v)
    {
        double old;
        if (TryNum(Column(line, col), out old) && Same(old, v == 0 || double.IsNaN(v) ? 0.0 : v)) return line;
        return Put(line, col, Real(v));
    }

    // an integer field (written as an integer, or as a real where the file writes reals)
    static string SetInt(string line, int col, long v)
    {
        string cur = Column(line, col);
        double old;
        if (TryNum(cur, out old) && Math.Abs(old - v) < 0.5) return line;
        bool real = cur.IndexOf('.') >= 0 || cur.IndexOf('E') >= 0;
        return Put(line, col, real ? Real(v) : Int(v));
    }

    static string SetText(string line, string text)
    {
        string old;
        if (line != null && CaesarNeutralReader.TryText(line, out old) && old == (text ?? "").TrimEnd()) return line;
        return TextRow(text);
    }

    static bool IsReal(string line)
    {
        if (line == null || line.Trim().Length == 0) return false;
        string t;
        if (CaesarNeutralReader.TryText(line, out t)) return false;
        return line.IndexOf('.') >= 0 || line.IndexOf('E') >= 0;
    }

    static bool IsBlank(string line) { return line == null || line.Trim().Length == 0; }

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

    // sections in CAESAR 15.01's order (a new model's file; an imported one keeps its own)
    static readonly string[] DefaultOrder =
    {
        "VERSION", "CONTROL", "ELEMENTS", "AUX_DATA", "BEND", "RIGID", "EXPJT", "RESTRANT", "DISPLMNT", "FORCMNT", "UNIFORM",
        "WIND", "OFFSETS", "ALLOWBLS", "SIF&TEES", "REDUCERS", "FLANGES", "EQUIPMNT", "MISCEL_1", "UNITS", "COORDS"
    };

    // blocks carried verbatim with their element; ALLOWBLS / NODENAME placed where a 15.01 file has them. A
    // FORCMNT / UNIFORM / WIND block in a layout the model does not read is carried too (their own writers).
    public static readonly string[] CarriedSections = { "OFFSETS", "ALLOWBLS", "NODENAME", "FLANGES", "EQUIPMNT" };

    // codes confirmed by CAESAR (1-26: every type the builder writes, read back from a CAESAR 15.01 export); the
    // rest of CAESAR's table comes from its documentation
    static readonly int[] SeenCodes = Enumerable.Range(1, 26).ToArray();

    // the 15 pointer fields: section -> (pointer row, column)
    static readonly string[] PointerOrder =
    {
        "BEND", "RIGID", "EXPJT", "RESTRANT", "DISPLMNT", "FORCMNT",
        "UNIFORM", "WIND", "OFFSETS", "ALLOWBLS", "SIF&TEES", "NODENAME",
        "REDUCERS", "FLANGES", "EQUIPMNT"
    };

    // ---------------- the file

    public static string ToText(PipeModel m)
    {
        var lines = ToLines(m);
        bool carry = m.Source != null && !m.Source.Seed;
        string eol = carry ? m.Source.LineEnding : "\r\n";
        bool final = !carry || m.Source.FinalNewline;
        return string.Join(eol, lines.ToArray()) + (final ? eol : "");
    }

    // bytes in the file's code page (the source's; 1252 for a new model: ASCII text gives ASCII bytes)
    public static byte[] ToBytes(PipeModel m)
    {
        string text = ToText(m);
        bool carry = m.Source != null && !m.Source.Seed;
        int cp = carry ? m.Source.CodePage : 1252;
        if (cp == 65001) { var u = new UTF8Encoding(true); return u.GetPreamble().Concat(u.GetBytes(text)).ToArray(); }
        Encoding enc;
        try { enc = Encoding.GetEncoding(cp); }
        catch (Exception) { enc = Encoding.GetEncoding(28591); }
        return enc.GetBytes(text);
    }

    public static void Write(PipeModel m, string path)
    {
        File.WriteAllBytes(path, ToBytes(m));
    }

    sealed class Plan
    {
        public PipeModel M;
        public PipeModelSource Src;          // null for a new model or a seed
        public PipeModelSource Seed;         // a seed's global sections (UNITS, options, VERSION)
        public List<PipeModelRestraint> Restraints;
        public List<PipeModelSif> Sifs;
        public object[][] ResSlots, SifSlots, DispSlots, ForceSlots;
        public int ResPer = 6, SifPer = 2, DispPer = 2, ForcePer = 2, ResLines = 4, SifLines = 7, DispLines = 10, ForceLines = 10;
        public Dictionary<string, int[]> Ptr = new Dictionary<string, int[]>();
        public Dictionary<string, List<int>> BlockOrder = new Dictionary<string, List<int>>();   // section -> element indices in pointer order
        public List<string> DefaultAllowables;   // written on element 0 when no element carries an allowables block
        public PipeModelUniform[] UniformOut;    // per element: the UNIFORM block it writes (null = none: carried on)
        public PipeModelWind[] WindOut;          // per element: the WIND block it writes
    }

    public static List<string> ToLines(PipeModel m)
    {
        var err = Validate(m);
        if (err.Count > 0) throw new InvalidOperationException("CaesarNeutralWriter: " + string.Join("; ", err.ToArray()));
        var p = new Plan();
        p.M = m;
        if (m.Source != null) { if (m.Source.Seed) p.Seed = m.Source; else p.Src = m.Source; }
        int ne = m.Elements.Count;
        if (p.Src != null)
        {
            int per = p.Src.Lines("RESTRANT", 0), sl = p.Src.Lines("RESTRANT/slot", 0);
            if (per > 0 && sl > 0) { p.ResPer = per / sl; p.ResLines = sl; }
            per = p.Src.Lines("SIF&TEES", 0); sl = p.Src.Lines("SIF&TEES/slot", 0);
            if (per > 0 && sl > 0) { p.SifPer = per / sl; p.SifLines = sl; }
            per = p.Src.Lines("DISPLMNT", 0); sl = p.Src.Lines("DISPLMNT/slot", 0);
            if (per > 0 && sl > 0) { p.DispPer = per / sl; p.DispLines = sl; }
            per = p.Src.Lines("FORCMNT", 0);
            if (per >= 4 && per % 2 == 0) p.ForceLines = per / 2;
        }

        // restraints (joints expanded) and SIFs, assigned to element blocks
        p.Restraints = new List<PipeModelRestraint>(m.Restraints);
        p.Sifs = new List<PipeModelSif>(m.Sifs);
        foreach (var j in m.Joints) ExpandJoint(m, j, p.Restraints, p.Sifs);
        var touching = Touching(m);
        p.ResSlots = AssignSlots(m, p.Restraints.Select(r => new SlotItem { Item = r, Node = r.Node, OwnerUid = r.OwnerUid, Slot = r.Slot }).ToList(), p.ResPer, touching, "restraints");
        p.SifSlots = AssignSlots(m, p.Sifs.Select(s => new SlotItem { Item = s, Node = s.Node, OwnerUid = s.OwnerUid, Slot = s.Slot }).ToList(), p.SifPer, touching, "SIF / tee entries");
        // a load stays with its element even off its ends (CAESAR imposes displacements on CNODEs too)
        p.DispSlots = AssignSlots(m, m.Displacements.Select(x => new SlotItem { Item = x, Node = x.Node, OwnerUid = x.OwnerUid, Slot = x.Slot, AnyNode = true }).ToList(), p.DispPer, touching, "imposed displacements");
        if (m.Forces.Any() && m.Elements.Any(e => e.Carried.ContainsKey("FORCMNT")))
            throw new InvalidOperationException("CaesarNeutralWriter: the source's FORCMNT blocks are in a layout the model does not read, so forces cannot be added: re-export the job from CAESAR 15.01 first");
        p.ForceSlots = AssignSlots(m, m.Forces.Select(x => new SlotItem { Item = x, Node = x.Node, OwnerUid = x.OwnerUid, Slot = x.Slot, AnyNode = true }).ToList(), p.ForcePer, touching, "force / moment entries");

        CarryPlan(p);

        // block numbering per section: CAESAR's own numbers (Seq) first, new blocks after, in element order
        p.DefaultAllowables = DefaultAllowables(m);
        foreach (string s in PointerOrder)
        {
            var present = new List<KeyValuePair<int, int>>();   // element index, seq
            for (int i = 0; i < ne; i++)
            {
                int seq;
                if (HasBlock(p, i, s, out seq)) present.Add(new KeyValuePair<int, int>(i, seq > 0 ? seq : int.MaxValue));
            }
            var ordered = present.OrderBy(kv => kv.Value).ThenBy(kv => kv.Key).Select(kv => kv.Key).ToList();
            var ptr = new int[ne];
            for (int k = 0; k < ordered.Count; k++) ptr[ordered[k]] = k + 1;
            p.Ptr[s] = ptr;
            p.BlockOrder[s] = ordered;
        }
        if (p.Src == null && m.Elements.Any(e => e.Carried.Count > 0))
            throw new InvalidOperationException("CaesarNeutralWriter: carried blocks without their source file");

        var w = new List<string>();
        if (p.Src != null) w.AddRange(p.Src.Prelude);
        foreach (string name in SectionOrder(p))
        {
            w.Add(p.Src != null && p.Src.Headers.ContainsKey(name) ? p.Src.Headers[name] : Header(name));
            switch (name)
            {
                case "VERSION": WriteVersion(p, w); break;
                case "CONTROL": WriteControl(p, w); break;
                case "ELEMENTS": WriteElements(p, w); break;
                case "BEND": WriteBends(p, w); break;
                case "RIGID": WriteRigids(p, w); break;
                case "EXPJT": WriteExpJoints(p, w); break;
                case "RESTRANT": WriteRestraints(p, w); break;
                case "DISPLMNT": WriteNodeLoads(p, w, "DISPLMNT"); break;
                case "FORCMNT": WriteNodeLoads(p, w, "FORCMNT"); break;
                case "UNIFORM": WriteUniform(p, w); break;
                case "WIND": WriteWind(p, w); break;
                case "SIF&TEES": WriteSifs(p, w); break;
                case "REDUCERS": WriteReducers(p, w); break;
                case "MISCEL_1": WriteMiscel(p, w); break;
                case "UNITS": WriteUnits(p, w); break;
                case "COORDS": WriteCoords(p, w); break;
                default:
                    if (CarriedSections.Contains(name)) WriteCarried(p, name, w);
                    else if (p.Src != null && p.Src.Sections.ContainsKey(name)) w.AddRange(p.Src.Sections[name]);   // AUX_DATA, unknown sections
                    break;
            }
            List<string> trailing;
            if (p.Src != null && p.Src.Trailing.TryGetValue(name, out trailing)) w.AddRange(trailing);   // lines past a section's blocks, as written
        }
        return w;
    }

    static bool HasBlock(Plan p, int i, string s, out int seq)
    {
        var e = p.M.Elements[i];
        seq = 0;
        PipeModelBlock b;
        switch (s)
        {
            case "BEND": if (e.Bend == null) return false; seq = e.Bend.Seq; return true;
            case "RIGID": if (e.Rigid == null) return false; seq = e.Rigid.Seq; return true;
            case "EXPJT": if (e.ExpJoint == null) return false; seq = e.ExpJoint.Seq; return true;
            case "REDUCERS": if (e.Reducer == null) return false; seq = e.Reducer.Seq; return true;
            case "RESTRANT": if (p.ResSlots[i] == null) return false; if (e.SlotBlocks.TryGetValue(s, out b)) seq = b.Seq; return true;
            case "SIF&TEES": if (p.SifSlots[i] == null) return false; if (e.SlotBlocks.TryGetValue(s, out b)) seq = b.Seq; return true;
            case "DISPLMNT": if (p.DispSlots[i] == null) return false; if (e.SlotBlocks.TryGetValue(s, out b)) seq = b.Seq; return true;
            case "FORCMNT":
                if (e.Carried.TryGetValue(s, out b)) { seq = b.Seq; return true; }
                if (p.ForceSlots[i] == null) return false; if (e.SlotBlocks.TryGetValue(s, out b)) seq = b.Seq; return true;
            case "UNIFORM":
                if (p.UniformOut[i] != null) { seq = p.UniformOut[i].Seq; return true; }
                if (e.Carried.TryGetValue(s, out b)) { seq = b.Seq; return true; }
                return false;
            case "WIND":
                if (p.WindOut[i] != null) { seq = p.WindOut[i].Seq; return true; }
                if (e.Carried.TryGetValue(s, out b)) { seq = b.Seq; return true; }
                return false;
            case "ALLOWBLS":
                if (e.Carried.TryGetValue(s, out b)) { seq = b.Seq; return true; }
                return i == 0 && p.DefaultAllowables != null;
            default:
                if (e.Carried.TryGetValue(s, out b)) { seq = b.Seq; return true; }
                return false;
        }
    }

    // the file's own section order, with any section it lacks inserted where a 15.01 file has it
    static List<string> SectionOrder(Plan p)
    {
        if (p.Src == null) return DefaultOrder.ToList();
        var order = new List<string>(p.Src.SectionOrder);
        foreach (string s in DefaultOrder)
        {
            if (order.Contains(s)) continue;
            int at = order.Count;
            int k = Array.IndexOf(DefaultOrder, s);
            for (int q = k + 1; q < DefaultOrder.Length; q++) { int pos = order.IndexOf(DefaultOrder[q]); if (pos >= 0) { at = pos; break; } }
            order.Insert(at, s);
        }
        if (!order.Contains("NODENAME") && p.BlockOrder["NODENAME"].Count > 0) order.Insert(order.IndexOf("MISCEL_1"), "NODENAME");
        return order;
    }

    // ---------------- VERSION, CONTROL

    static void WriteVersion(Plan p, List<string> w)
    {
        var m = p.M;
        var src = p.Src ?? p.Seed;
        if (src != null && src.Sections.ContainsKey("VERSION"))
        {
            foreach (string l in src.Sections["VERSION"])
            {
                string t = l.TrimStart();
                if (t.StartsWith("PROJECT:", StringComparison.Ordinal) && (p.Src == null || m.Title != p.Src.OriginalTitle)) w.Add(Cut77("    PROJECT: " + Ascii(m.Title ?? "")));
                else if (t.StartsWith("NOTES  :", StringComparison.Ordinal) && (p.Src == null || m.Notes != p.Src.OriginalNotes)) w.Add(Cut77("    NOTES  : " + Ascii(m.Notes ?? "")));
                else w.Add(l);
            }
            return;
        }
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
    }

    static int Count(Plan p, string s) { return p.BlockOrder[s].Count; }

    static void WriteControl(Plan p, List<string> w)
    {
        int ne = p.M.Elements.Count;
        long[][] rows =
        {
            new long[] { ne, -1, -1, Count(p, "NODENAME"), Count(p, "REDUCERS"), Count(p, "FLANGES") },
            new long[] { Count(p, "BEND"), Count(p, "RIGID"), Count(p, "EXPJT"), Count(p, "RESTRANT"), Count(p, "DISPLMNT"), Count(p, "FORCMNT") },
            new long[] { Count(p, "UNIFORM"), Count(p, "WIND"), Count(p, "OFFSETS"), Count(p, "ALLOWBLS"), Count(p, "SIF&TEES"), p.M.ZUp ? 1 : 0 },
            new long[] { Count(p, "EQUIPMNT") }
        };
        if (p.Src != null && p.Src.Sections.ContainsKey("CONTROL"))
        {
            int r = 0;
            foreach (string l in p.Src.Sections["CONTROL"])
            {
                if (IsBlank(l) || r >= rows.Length) { w.Add(l); continue; }
                string x = l;
                for (int c = 0; c < rows[r].Length; c++) if (rows[r][c] >= 0) x = SetInt(x, c, rows[r][c]);   // -1: nozzles and hangers stay as written
                w.Add(x);
                r++;
            }
            return;
        }
        rows[0][1] = 0; rows[0][2] = 0;
        foreach (var r in rows) w.Add(IntRow(r));
    }

    // ---------------- ELEMENTS

    // a record's rows: the run of real rows, then text rows, integer rows, and the last 3 rows (pointers)
    sealed class RecordLayout
    {
        public List<int> Real = new List<int>(), Text = new List<int>(), Ptr = new List<int>();
    }

    static RecordLayout Layout(List<string> lines)
    {
        var lay = new RecordLayout();
        int i = 0;
        while (i < lines.Count && IsReal(lines[i])) lay.Real.Add(i++);
        var nonBlank = new List<int>();
        for (; i < lines.Count; i++) if (!IsBlank(lines[i])) nonBlank.Add(i);
        if (nonBlank.Count >= 3) lay.Ptr.AddRange(nonBlank.Skip(nonBlank.Count - 3));
        foreach (int k in nonBlank.Take(Math.Max(0, nonBlank.Count - 3)))
        {
            string t;
            if (CaesarNeutralReader.TryText(lines[k], out t)) lay.Text.Add(k);
        }
        return lay;
    }

    static double[][] ElementRows(PipeModelElement e)
    {
        return new[]
        {
            new double[] { e.From, e.To, e.Dx, e.Dy, e.Dz, e.Od },
            new[] { e.Wall, e.Insulation, e.Corrosion, e.T[0], e.T[1], e.T[2] },
            new[] { e.T[3], e.T[4], e.T[5], e.T[6], e.T[7], e.T[8] },
            new[] { e.P[0], e.P[1], e.P[2], e.P[3], e.P[4], e.P[5] },
            new[] { e.P[6], e.P[7], e.P[8], e.Emod, e.Poisson, e.PipeDensity },
            new[] { e.InsulationDensity, e.FluidDensity }
        };
    }

    static void WriteElements(Plan p, List<string> w)
    {
        var m = p.M;
        for (int i = 0; i < m.Elements.Count; i++)
        {
            var e = m.Elements[i];
            var ptr = PointerOrder.Select(s => (long)p.Ptr[s][i]).ToArray();
            var rows = ElementRows(e);
            if (e.Raw != null && p.Src != null)
            {
                var lines = new List<string>(e.Raw);
                var lay = Layout(lines);
                if (lay.Real.Count < 6 || lay.Ptr.Count != 3) throw new InvalidOperationException("CaesarNeutralWriter: element " + e.From + "-" + e.To + ": its record (as imported) has no recognisable rows");
                for (int r = 0; r < rows.Length; r++) for (int c = 0; c < rows[r].Length; c++) lines[lay.Real[r]] = SetReal(lines[lay.Real[r]], c, rows[r][c]);
                if (lay.Real.Count > 7) { lines[lay.Real[7]] = SetReal(lines[lay.Real[7]], 4, e.TeeAngle1); lines[lay.Real[7]] = SetReal(lines[lay.Real[7]], 5, e.TeeAngle2); }
                if (lay.Text.Count > 0) lines[lay.Text[0]] = SetText(lines[lay.Text[0]], e.Name);
                if (lay.Text.Count > 1) lines[lay.Text[1]] = SetText(lines[lay.Text[1]], e.LineNumber);
                for (int k = 0; k < 15; k++) lines[lay.Ptr[k / 6]] = SetInt(lines[lay.Ptr[k / 6]], k % 6, ptr[k]);
                w.AddRange(lines);
                continue;
            }
            if (p.Src != null && p.Src.Lines("ELEMENTS", 15) != 15)
                throw new InvalidOperationException("CaesarNeutralWriter: element " + e.From + "-" + e.To + " is new, and the source file's element records are not CAESAR 15.01's (" + p.Src.Lines("ELEMENTS", 15) + " lines): re-export the job from CAESAR 15.01 first");
            w.Add(RealRow(rows[0]));
            w.Add(RealRow(rows[1]));
            w.Add(RealRow(rows[2]));
            w.Add(RealRow(rows[3]));
            w.Add(RealRow(rows[4]));
            w.Add(RealRow(e.InsulationDensity, e.FluidDensity, 0, 0, 0, 0));
            w.Add(RealRow(0, 0, 0, 0, 0, 0));
            w.Add(RealRow(0, 0, 0, 0, e.TeeAngle1, e.TeeAngle2));
            w.Add(RealRow(0, 0, 0, 0, 0));
            w.Add(TextRow(e.Name));
            w.Add(TextRow(e.LineNumber));
            w.Add(IntRow(-1, -1));
            w.Add(IntRow(ptr[0], ptr[1], ptr[2], ptr[3], ptr[4], ptr[5]));
            w.Add(IntRow(ptr[6], ptr[7], ptr[8], ptr[9], ptr[10], ptr[11]));
            w.Add(IntRow(ptr[12], ptr[13], ptr[14]));
        }
    }

    // ---------------- per-element blocks

    static void WriteBends(Plan p, List<string> w)
    {
        foreach (int i in p.BlockOrder["BEND"])
        {
            var b = p.M.Elements[i].Bend;
            double[] an = new double[6];
            for (int k = 0; k < 3 && k < b.AngleNodes.Count; k++) { an[2 * k] = b.AngleNodes[k][0]; an[2 * k + 1] = b.AngleNodes[k][1]; }
            if (b.Raw != null && b.Raw.Count >= 2)
            {
                var l = new List<string>(b.Raw);
                l[0] = SetReal(l[0], 0, b.Radius); l[0] = SetReal(l[0], 1, b.Type);
                if (!SameAngleNodes(l, b))
                {
                    l[0] = SetReal(SetReal(SetReal(SetReal(l[0], 2, an[0]), 3, an[1]), 4, an[2]), 5, an[3]);
                    l[1] = SetReal(SetReal(l[1], 0, an[4]), 1, an[5]);
                }
                l[1] = SetReal(l[1], 2, b.MiterPoints); l[1] = SetReal(l[1], 3, b.FittingThickness); l[1] = SetReal(l[1], 4, b.KFactor);
                w.AddRange(l);
                continue;
            }
            w.Add(RealRow(b.Radius, b.Type, an[0], an[1], an[2], an[3]));
            w.Add(RealRow(an[4], an[5], b.MiterPoints, b.FittingThickness, b.KFactor, 0));
            w.Add(RealRow(0, 0));
        }
    }

    // the angle / node pairs the raw block holds (slots with a node), against the model's
    static bool SameAngleNodes(List<string> raw, PipeModelBend b)
    {
        var pairs = new List<double[]>();
        int[][] at = { new[] { 0, 2, 0, 3 }, new[] { 0, 4, 0, 5 }, new[] { 1, 0, 1, 1 } };
        foreach (var a in at)
        {
            double ang, node;
            if (!TryNum(Column(raw[a[0]], a[1]), out ang)) ang = 0;
            if (!TryNum(Column(raw[a[2]], a[3]), out node)) node = 0;
            if (Math.Round(node) > 0) pairs.Add(new[] { ang, Math.Round(node) });
        }
        if (pairs.Count != b.AngleNodes.Count) return false;
        for (int k = 0; k < pairs.Count; k++) if (!Same(pairs[k][0], b.AngleNodes[k][0]) || Math.Round(pairs[k][1]) != Math.Round(b.AngleNodes[k][1])) return false;
        return true;
    }

    static void WriteRigids(Plan p, List<string> w)
    {
        foreach (int i in p.BlockOrder["RIGID"])
        {
            var r = p.M.Elements[i].Rigid;
            if (r.Raw != null && r.Raw.Count >= 1)
            {
                var l = new List<string>(r.Raw);
                l[0] = SetReal(SetReal(l[0], 0, r.Weight), 1, r.Type);
                w.AddRange(l);
                continue;
            }
            w.Add(RealRow(r.Weight, r.Type));
        }
    }

    static void WriteExpJoints(Plan p, List<string> w)
    {
        foreach (int i in p.BlockOrder["EXPJT"])
        {
            var x = p.M.Elements[i].ExpJoint;
            double[] v = { x.AxialStiffness, x.TransverseStiffness, x.BendingStiffness, x.TorsionStiffness, x.EffectiveId };
            if (x.Raw != null && x.Raw.Count >= 1)
            {
                var l = new List<string>(x.Raw);
                for (int c = 0; c < 5; c++) l[0] = SetReal(l[0], c, v[c]);
                w.AddRange(l);
                continue;
            }
            w.Add(RealRow(v));
        }
    }

    static void WriteReducers(Plan p, List<string> w)
    {
        foreach (int i in p.BlockOrder["REDUCERS"])
        {
            var r = p.M.Elements[i].Reducer;
            double[] v = { r.Od2, r.Wall2, r.Alpha, r.R1, r.R2 };
            if (r.Raw != null && r.Raw.Count >= 1)
            {
                var l = new List<string>(r.Raw);
                for (int c = 0; c < 5; c++) l[0] = SetReal(l[0], c, v[c]);
                w.AddRange(l);
                continue;
            }
            w.Add(RealRow(v));
            w.Add(RealRow(0, 0, 0, 0, 0));
        }
    }

    static void WriteCarried(Plan p, string s, List<string> w)
    {
        foreach (int i in p.BlockOrder[s])
        {
            PipeModelBlock b;
            if (p.M.Elements[i].Carried.TryGetValue(s, out b)) w.AddRange(b.Lines);
            else if (s == "ALLOWBLS" && p.DefaultAllowables != null) w.AddRange(p.DefaultAllowables);
        }
    }

    // a slot of an element's block as written, when that slot held nothing in the source (so stale values CAESAR
    // left in it come back too); null otherwise
    static List<string> UnusedSourceSlot(PipeModelElement e, string s, int slot, int slotLines)
    {
        PipeModelBlock b;
        if (!e.SlotBlocks.TryGetValue(s, out b) || b.Lines.Count < (slot + 1) * slotLines) return null;
        var lines = b.Lines.GetRange(slot * slotLines, slotLines);
        double node;
        if (TryNum(Column(lines[0], 0), out node) && Math.Round(node) > 0) return null;
        return lines;
    }

    static void WriteRestraints(Plan p, List<string> w)
    {
        var m = p.M;
        foreach (int i in p.BlockOrder["RESTRANT"])
        {
            var e = m.Elements[i];
            var slots = p.ResSlots[i];
            for (int s = 0; s < p.ResPer; s++)
            {
                var r = slots[s] as PipeModelRestraint;
                if (r == null)
                {
                    var old = UnusedSourceSlot(e, "RESTRANT", s, p.ResLines);
                    if (old != null) { w.AddRange(old); continue; }
                    if (p.ResLines != 4) throw new InvalidOperationException("CaesarNeutralWriter: an empty restraint slot in a " + p.ResLines + "-line layout (not CAESAR 15.01's): re-export the job from CAESAR 15.01 first");
                    w.Add(RealRow(0, 0, 0, 0, 0, 0));
                    w.Add(RealRow(0, 0, 0));
                    w.Add(TextRow(""));
                    w.Add(TextRow(""));
                    continue;
                }
                double k = r.Stiffness > 0 ? r.Stiffness : RigidStiffness * m.Factor(PipeModelCodes.IsRotationCode(r.Code) ? 14 : 13);
                var c = r.Cosines ?? PipeModelCodes.DefaultCosines(r.Type);
                if (r.Raw != null && r.Raw.Count >= 2)
                {
                    var l = new List<string>(r.Raw);
                    double[] v = { r.Node, r.Code, k, r.Gap, r.Mu, r.CNode };
                    for (int q = 0; q < 6; q++) l[0] = SetReal(l[0], q, v[q]);
                    for (int q = 0; q < 3; q++) l[1] = SetReal(l[1], q, c[q]);
                    if (l.Count > 2) l[2] = SetText(l[2], r.Tag);
                    if (l.Count > 3) l[3] = SetText(l[3], r.Guid);
                    w.AddRange(l);
                    continue;
                }
                if (p.ResLines != 4) throw new InvalidOperationException("CaesarNeutralWriter: a new restraint at node " + r.Node + " in a " + p.ResLines + "-line slot layout (not CAESAR 15.01's): re-export the job from CAESAR 15.01 first");
                w.Add(RealRow(r.Node, r.Code, k, r.Gap, r.Mu, r.CNode));
                w.Add(RealRow(c[0], c[1], c[2]));
                w.Add(TextRow(r.Tag));
                w.Add(TextRow(r.Guid));
            }
        }
    }

    static void WriteSifs(Plan p, List<string> w)
    {
        var m = p.M;
        foreach (int i in p.BlockOrder["SIF&TEES"])
        {
            var e = m.Elements[i];
            var slots = p.SifSlots[i];
            for (int s = 0; s < p.SifPer; s++)
            {
                var t = slots[s] as PipeModelSif;
                if (t == null)
                {
                    var old = UnusedSourceSlot(e, "SIF&TEES", s, p.SifLines);
                    if (old != null) { w.AddRange(old); continue; }
                    if (p.SifLines != 7) throw new InvalidOperationException("CaesarNeutralWriter: an empty SIF slot in a " + p.SifLines + "-line layout (not CAESAR 15.01's): re-export the job from CAESAR 15.01 first");
                    w.Add(RealRow(0, 0, 0, 0, 0, 0));
                    w.Add(RealRow(0, 0, 0, 0, 0, 0));
                    w.Add(RealRow(0, 0, 0, 0, 0, 0));
                    w.Add(RealRow(0, 0, 0, 0, 9999.99, 9999.99));
                    w.Add(RealRow(0, 0, 0, 0, 0, 0));
                    w.Add(RealRow(0, 0, 0, 0, 0, 0));
                    w.Add(RealRow(0, 0, 0, 0, 0, 0));
                    continue;
                }
                if (t.Raw != null && t.Raw.Count >= 5)
                {
                    var l = new List<string>(t.Raw);
                    l[0] = SetReal(SetReal(SetReal(SetReal(l[0], 0, t.Node), 1, t.Type), 2, t.SifIn), 3, t.SifOut);
                    l[4] = SetReal(l[4], 2, t.SurfaceNode);
                    w.AddRange(l);
                    continue;
                }
                if (p.SifLines != 7) throw new InvalidOperationException("CaesarNeutralWriter: a new SIF at node " + t.Node + " in a " + p.SifLines + "-line layout (not CAESAR 15.01's): re-export the job from CAESAR 15.01 first");
                w.Add(RealRow(t.Node, t.Type, t.SifIn, t.SifOut, 0, 0));
                w.Add(RealRow(0, 0, 0, 0, 0, 0));
                w.Add(RealRow(0, 0, 0, 0, 0, 0));
                w.Add(RealRow(0, 0, 0, 0, 1, 9999.99));
                w.Add(RealRow(0, 0, t.SurfaceNode, 0, 0, 0));
                w.Add(RealRow(0, 0, 0, 0, 0, 0));
                w.Add(RealRow(0, 0, 0, 0, 0, 0));
            }
        }
    }

    // FORCMNT / DISPLMNT: 2 slots per block, each the node and then one line per vector
    static void WriteNodeLoads(Plan p, List<string> w, string s)
    {
        bool disp = s == "DISPLMNT";
        int per = disp ? p.DispPer : p.ForcePer, sl = disp ? p.DispLines : p.ForceLines;
        foreach (int i in p.BlockOrder[s])
        {
            var e = p.M.Elements[i];
            PipeModelBlock carried;
            if (e.Carried.TryGetValue(s, out carried)) { w.AddRange(carried.Lines); continue; }
            var slots = (disp ? p.DispSlots : p.ForceSlots)[i];
            for (int q = 0; q < per; q++)
            {
                var d = slots == null ? null : slots[q] as PipeModelNodeLoad;
                if (d == null)
                {
                    var old = UnusedSourceSlot(e, s, q, sl);
                    if (old != null) { w.AddRange(old); continue; }
                    if (sl != 10) throw new InvalidOperationException("CaesarNeutralWriter: an empty " + s + " slot in a " + sl + "-line layout (10 documented): re-export the job from CAESAR 15.01 first");
                    w.Add(RealRow(0));
                    for (int v = 0; v < 9; v++) w.Add(disp ? RealRow(Free6()) : RealRow(0, 0, 0, 0, 0, 0));
                    continue;
                }
                w.AddRange(NodeLoadLines(d, sl));
            }
        }
    }

    static double[] Free6() { return new[] { PipeModelNodeLoad.Free, PipeModelNodeLoad.Free, PipeModelNodeLoad.Free, PipeModelNodeLoad.Free, PipeModelNodeLoad.Free, PipeModelNodeLoad.Free }; }

    // a load value as written: not given (NaN) = 9999.99
    static double LoadValue(double v) { return double.IsNaN(v) ? PipeModelNodeLoad.Free : v; }

    static List<string> NodeLoadLines(PipeModelNodeLoad d, int sl)
    {
        string what = (d.IsDisplacement ? "imposed displacement" : "force / moment") + " at node " + d.Node;
        if (d.Raw != null && d.Raw.Count >= 2)
        {
            var l = new List<string>(d.Raw);
            l[0] = SetInt(l[0], 0, d.Node);
            for (int v = 0; v < 9; v++)
            {
                if (v + 1 >= l.Count)
                {
                    if (d.Used(v)) throw new InvalidOperationException("CaesarNeutralWriter: " + what + ": vector " + (v + 1) + " does not fit the source's " + l.Count + "-line slot: re-export the job from CAESAR 15.01 first");
                    continue;
                }
                for (int k = 0; k < 6; k++) l[v + 1] = SetReal(l[v + 1], k, LoadValue(d.Vectors[v][k]));
            }
            return l;
        }
        if (sl != 10) throw new InvalidOperationException("CaesarNeutralWriter: a new " + what + " in a " + sl + "-line slot layout (10 documented): re-export the job from CAESAR 15.01 first");
        var n = new List<string> { RealRow(d.Node) };
        for (int v = 0; v < 9; v++) n.Add(RealRow(d.Vectors[v].Select(LoadValue).ToArray()));
        return n;
    }

    // Which elements write a UNIFORM / WIND block, CAESAR carrying each forward: an element's own imported block stays
    // where it was while unedited (or still needed); otherwise a block where the load acting differs from the one
    // before it (in element order), a zero / off block where it stops. After a block in a layout the model does not
    // read, an element with no load of its own writes nothing (what CAESAR carries there is the source's).
    static void CarryPlan(Plan p)
    {
        var m = p.M;
        int ne = m.Elements.Count;
        p.UniformOut = new PipeModelUniform[ne];
        p.WindOut = new PipeModelWind[ne];
        PipeModelUniform pu = null;
        PipeModelWind pw = null;
        bool unknownU = false, unknownW = false;
        for (int i = 0; i < ne; i++)
        {
            var e = m.Elements[i];
            var u = e.Uniform;
            if (u == null && e.Carried.ContainsKey("UNIFORM")) { unknownU = true; pu = null; }
            else if (u != null && u.Raw != null && (!Edited(u) || !PipeModelUniform.Same(u, pu))) { p.UniformOut[i] = u; pu = u; unknownU = false; }
            else if (unknownU && PipeModelUniform.IsNone(u)) { }
            else
            {
                if (!PipeModelUniform.Same(u, pu)) p.UniformOut[i] = PipeModelUniform.IsNone(u) ? StopUniform(pu) : u;
                pu = u; unknownU = false;
            }
            var x = e.Wind;
            if (x == null && e.Carried.ContainsKey("WIND")) { unknownW = true; pw = null; }
            else if (x != null && x.Raw != null && (!Edited(x) || !PipeModelWind.Same(x, pw))) { p.WindOut[i] = x; pw = x; unknownW = false; }
            else if (unknownW && PipeModelWind.IsNone(x)) { }
            else
            {
                if (!PipeModelWind.Same(x, pw)) p.WindOut[i] = PipeModelWind.IsNone(x) ? (x ?? new PipeModelWind { Type = 2 }) : x;
                pw = x; unknownW = false;
            }
        }
    }

    // an imported block whose values the model has changed
    static bool Edited(PipeModelUniform u) { return u.Raw == null || u.Raw.Count != 6 || !u.SameAs(CaesarPipeImport.Uniform(u.Raw)); }
    static bool Edited(PipeModelWind x) { return x.Raw == null || x.Raw.Count != 1 || !x.SameAs(CaesarPipeImport.Wind(x.Raw)); }

    // a zero block ending a carried load: every g flag the one its used vectors share (as written when they mix)
    static PipeModelUniform StopUniform(PipeModelUniform before)
    {
        var z = new PipeModelUniform();
        if (before == null) return z;
        var used = Enumerable.Range(0, 9).Where(before.Used).ToList();
        bool one = used.Count > 0 && used.All(v => before.G[v] == before.G[used[0]]);
        for (int v = 0; v < 9; v++) z.G[v] = one ? before.G[used[0]] : before.G[v];
        return z;
    }

    static void WriteUniform(Plan p, List<string> w)
    {
        foreach (int i in p.BlockOrder["UNIFORM"])
        {
            var e = p.M.Elements[i];
            PipeModelBlock carried;
            var u = p.UniformOut[i];
            if (u == null && e.Carried.TryGetValue("UNIFORM", out carried)) { w.AddRange(carried.Lines); continue; }
            var vals = new double[36];
            var used = Enumerable.Range(0, 9).Where(u.Used).ToList();
            bool fill = Edited(u) && used.Count > 0 && used.All(v => u.G[v] == u.G[used[0]]);   // a new or edited block: unused vectors take the used ones' flag
            for (int v = 0; v < 9; v++)
            {
                for (int k = 0; k < 3; k++) vals[4 * v + k] = u.Vectors[v][k];
                vals[4 * v + 3] = (fill && !u.Used(v) ? u.G[used[0]] : u.G[v]) ? 1 : 0;
            }
            if (u.Raw != null && u.Raw.Count == 6)
            {
                var l = new List<string>(u.Raw);
                for (int k = 0; k < 36; k++) l[k / 6] = k % 4 == 3 ? SetInt(l[k / 6], k % 6, (long)vals[k]) : SetReal(l[k / 6], k % 6, vals[k]);   // the g flags as integers
                w.AddRange(l);
                continue;
            }
            for (int r = 0; r < 6; r++) w.Add(RealRow(vals.Skip(6 * r).Take(6).ToArray()));
        }
    }

    static void WriteWind(Plan p, List<string> w)
    {
        foreach (int i in p.BlockOrder["WIND"])
        {
            var e = p.M.Elements[i];
            PipeModelBlock carried;
            var x = p.WindOut[i];
            if (x == null && e.Carried.TryGetValue("WIND", out carried)) { w.AddRange(carried.Lines); continue; }
            double[] v = { x.Type, x.Shape, x.AddedMass, x.Lift, x.MarineGrowth, x.MarineGrowthDensity };
            if (x.Raw != null && x.Raw.Count == 1)
            {
                string l = SetInt(x.Raw[0], 0, x.Type);
                for (int k = 1; k < 6; k++) l = SetReal(l, k, v[k]);
                w.Add(l);
                continue;
            }
            w.Add(RealRow(v));
        }
    }

    // What a written file holds that no CAESAR-written file has shown yet (from CAESAR's documentation): one
    // line each, for the caller to show -- check these in CAESAR after the import. Every load layout (forces,
    // imposed displacements, uniform loads, wind and wave), skewed restraints and codes 1-26 are confirmed by
    // CAESAR 15.01 exports; what is left is a restraint code beyond 26 that no CAESAR file holds.
    public static List<string> Notices(PipeModel m)
    {
        return NoticeItems(m).Select(x => x.Text).ToList();
    }

    // the same, with the nodes and elements each is about (the viewer's Checks panel, PipeReview)
    public static List<PipeReviewItem> NoticeItems(PipeModel m)
    {
        var n = new List<PipeReviewItem>();
        List<PipeModelRestraint> all;
        try { all = Restraints(m); }
        catch (InvalidOperationException) { all = new List<PipeModelRestraint>(m.Restraints); }
        var unseen = all.Where(r => r.Raw == null && Array.IndexOf(SeenCodes, r.Code) < 0).ToList();
        if (unseen.Count > 0)
            n.Add(new PipeReviewItem("notice", "restraint-code", "restraint type(s) " + string.Join(", ", unseen.Select(r => r.Type + " (" + r.Code + ")").Distinct().ToArray()) +
                " written with the codes of CAESAR's documentation, which no CAESAR-written file here has shown yet: check them in CAESAR after the import")
                .WithNodes(unseen.Select(r => r.Node)));
        return n;
    }

    // ---------------- MISCEL_1, UNITS, COORDS

    static void WriteMiscel(Plan p, List<string> w)
    {
        var m = p.M;
        int ne = m.Elements.Count;
        if (p.Src != null && p.Src.ElementCount == ne && p.Src.MiscelMaterialLines.Count == (ne + 5) / 6)
        {
            for (int k = 0; k < p.Src.MiscelMaterialLines.Count; k++)
            {
                string l = p.Src.MiscelMaterialLines[k];
                for (int c = 0; c < 6 && 6 * k + c < ne; c++) l = SetReal(l, c, m.Elements[6 * k + c].Material);
                w.Add(l);
            }
        }
        else
        {
            for (int i = 0; i < ne; i += 6)
            {
                var row = new List<double>();
                for (int k = i; k < Math.Min(ne, i + 6); k++) row.Add(m.Elements[k].Material);
                w.Add(RealRow(row.ToArray()));
            }
        }
        var src = p.Src ?? p.Seed;
        if (src != null && src.MiscelTail.Count >= 4)
        {
            var tail = new List<string>(src.MiscelTail);
            int at = tail.Count - 3;   // the execution options' line 2: ambient at field 3
            tail[at] = SetReal(tail[at], 2, m.Ambient);
            w.AddRange(tail);
            return;
        }
        w.Add(MixedRow(1, 0, 0, 2, 0.0, 1));
        w.Add(MixedRow(0, 0, m.Ambient, 12.0, 0, 0));
        w.Add(MixedRow(0, 0, 0, 0, 0.25, 3));
        w.Add(MixedRow(3, 0));
    }

    static void WriteUnits(Plan p, List<string> w)
    {
        var src = p.Src ?? p.Seed;
        if (src != null && src.Sections.ContainsKey("UNITS")) { w.AddRange(src.Sections["UNITS"]); return; }
        w.AddRange(UnitsEnglish);
    }

    static void WriteCoords(Plan p, List<string> w)
    {
        var m = p.M;
        w.Add(p.Src != null && p.Src.CoordsCountLine != null ? SetInt(p.Src.CoordsCountLine, 0, m.Coords.Count) : IntRow(m.Coords.Count));
        foreach (var c in m.Coords)
        {
            double[] v = { c.X, c.Y, c.Z };
            if (c.Raw != null)
            {
                string l = SetInt(c.Raw, 0, c.Node);
                for (int k = 0; k < 3; k++)
                    if (c.ImportedXyz == null || c.ImportedXyz[k] != v[k]) l = SetReal(l, k + 1, ToCompound(m, v[k]));   // unchanged: as written
                w.Add(l);
                continue;
            }
            w.Add("  " + Int(c.Node) + Real(ToCompound(m, c.X)) + Real(ToCompound(m, c.Y)) + Real(ToCompound(m, c.Z)));
        }
    }

    // length -> the compound-length unit COORDS are written in: exactly /12 for English (in -> ft; CAESAR's own
    // factor is the 7-digit 0.0833333), the file's factors otherwise
    static double ToCompound(PipeModel m, double v)
    {
        double f0 = m.Factor(0), f19 = m.Factor(19);
        if (Math.Abs(f0 - 1.0) < 1e-9 && Math.Abs(f19 - 1.0 / 12.0) < 1e-5) return v / 12.0;
        return v / f0 * f19;
    }

    static string Pad77(string s) { return s.Length >= 77 ? s : s.PadRight(77); }

    // a fixed-width line never grows past its width (a long title is cut, not wrapped)
    static string Cut77(string s) { s = Pad77(s); return s.Length > 77 ? s.Substring(0, 77) : s; }

    // ---------------- slots: restraints, SIFs, displacements -> element blocks

    sealed class SlotItem
    {
        public object Item;
        public int Node, OwnerUid, Slot;
        public bool AnyNode;                  // kept with its owner even when the owner does not have the node
    }

    // node -> indices of the elements that have it (TO, FROM, a bend node), in element order
    static Dictionary<int, List<int>> Touching(PipeModel m)
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
        return touching;
    }

    // pass 1: an item goes back to the element block that held it (its slot when free) while that element still
    // has the node; pass 2: every other item to the first element at the node with a free slot
    static object[][] AssignSlots(PipeModel m, List<SlotItem> items, int per, Dictionary<int, List<int>> touching, string what)
    {
        var res = new object[m.Elements.Count][];
        var byUid = new Dictionary<int, int>();
        for (int i = 0; i < m.Elements.Count; i++) if (m.Elements[i].Uid > 0 && !byUid.ContainsKey(m.Elements[i].Uid)) byUid[m.Elements[i].Uid] = i;
        var pending = new List<SlotItem>();
        foreach (var it in items)
        {
            int ei;
            List<int> els;
            if (it.OwnerUid > 0 && byUid.TryGetValue(it.OwnerUid, out ei) && (it.AnyNode || (touching.TryGetValue(it.Node, out els) && els.Contains(ei))))
            {
                var arr = res[ei] ?? new object[per];
                int s = it.Slot >= 0 && it.Slot < per && arr[it.Slot] == null ? it.Slot : Array.IndexOf(arr, null);
                if (s >= 0) { arr[s] = it.Item; res[ei] = arr; continue; }
            }
            pending.Add(it);
        }
        foreach (var it in pending)
        {
            List<int> els;
            if (!touching.TryGetValue(it.Node, out els)) throw new InvalidOperationException("CaesarNeutralWriter: " + what + " at node " + it.Node.ToString(CultureInfo.InvariantCulture) + ", which no element has");
            bool placed = false;
            foreach (int ei in els)
            {
                var arr = res[ei] ?? new object[per];
                int s = Array.IndexOf(arr, null);
                if (s < 0) continue;
                arr[s] = it.Item;
                res[ei] = arr;
                placed = true;
                break;
            }
            if (!placed) throw new InvalidOperationException("CaesarNeutralWriter: too many " + what + " at node " + it.Node.ToString(CultureInfo.InvariantCulture) +
                " (" + per.ToString(CultureInfo.InvariantCulture) + " per element touching it, " + els.Count.ToString(CultureInfo.InvariantCulture) + " element(s))");
        }
        return res;
    }

    // ---------------- joints -> CNODE restraints

    // the allowables block a model gets on its first element when it carries none: a new model's (a seed's or
    // the built-in B31.1); null for an imported file, which keeps exactly the blocks it had (none in old files)
    public static List<string> DefaultAllowables(PipeModel m)
    {
        if (m.Elements.Count == 0 || m.Elements.Any(e => e.Carried.ContainsKey("ALLOWBLS"))) return null;
        if (m.Source != null && !m.Source.Seed) return null;
        if (m.AllowablesBlock != null) return m.AllowablesBlock;
        var l = new List<string>();
        foreach (var row in AllowablesB311) l.Add(RealRow(row));
        return l;
    }

    // the restraints / SIF and tee entries as written: the model's own, plus its joints expanded
    public static List<PipeModelRestraint> Restraints(PipeModel m)
    {
        var r = new List<PipeModelRestraint>(m.Restraints);
        var s = new List<PipeModelSif>();
        foreach (var j in m.Joints) ExpandJoint(m, j, r, s);
        return r;
    }

    public static List<PipeModelSif> Sifs(PipeModel m)
    {
        var r = new List<PipeModelRestraint>();
        var s = new List<PipeModelSif>(m.Sifs);
        foreach (var j in m.Joints) ExpandJoint(m, j, r, s);
        return s;
    }

    static void ExpandJoint(PipeModel m, PipeModelJoint j, List<PipeModelRestraint> restraints, List<PipeModelSif> sifs)
    {
        string w = "joint " + j.Node + "-" + j.CNode + " (" + j.Kind + ")";
        bool pipe = !string.Equals(j.Frame, "global", StringComparison.OrdinalIgnoreCase);
        double[] a = null, p1 = null, p2 = null;
        if (pipe)
        {
            var e = m.Elements.FirstOrDefault(x => x.To == j.Node) ?? m.Elements.FirstOrDefault(x => x.From == j.CNode) ?? m.Elements.FirstOrDefault(x => x.From == j.Node || x.To == j.CNode);
            if (e == null) throw new InvalidOperationException("CaesarNeutralWriter: " + w + ": no element gives the pipe axis");
            double l = Math.Sqrt(e.Dx * e.Dx + e.Dy * e.Dy + e.Dz * e.Dz);
            if (!(l > 0)) throw new InvalidOperationException("CaesarNeutralWriter: " + w + ": the element at the joint has no length (no pipe axis)");
            a = new[] { e.Dx / l, e.Dy / l, e.Dz / l };
            Perps(a, out p1, out p2);
        }
        foreach (var d in j.Dofs)
        {
            string dof = (d.Dof ?? "").Trim().ToLowerInvariant();
            var dirs = new List<KeyValuePair<double[], bool>>();   // direction, rotation
            if (pipe)
            {
                if (dof == "axial") dirs.Add(new KeyValuePair<double[], bool>(a, false));
                else if (dof == "shear") { dirs.Add(new KeyValuePair<double[], bool>(p1, false)); dirs.Add(new KeyValuePair<double[], bool>(p2, false)); }
                else if (dof == "torsion") dirs.Add(new KeyValuePair<double[], bool>(a, true));
                else if (dof == "bending") { dirs.Add(new KeyValuePair<double[], bool>(p1, true)); dirs.Add(new KeyValuePair<double[], bool>(p2, true)); }
                else throw new InvalidOperationException("CaesarNeutralWriter: " + w + ": DOF \"" + d.Dof + "\" (pipe frame: axial, shear, torsion, bending)");
            }
            else
            {
                string u = dof.ToUpperInvariant();
                bool rot = u.StartsWith("R", StringComparison.Ordinal);
                string ax = rot ? u.Substring(1) : u;
                int k = ax == "X" ? 0 : ax == "Y" ? 1 : ax == "Z" ? 2 : -1;
                if (k < 0 || u.Length > 2) throw new InvalidOperationException("CaesarNeutralWriter: " + w + ": DOF \"" + d.Dof + "\" (global frame: X Y Z RX RY RZ)");
                var v = new double[3]; v[k] = 1;
                dirs.Add(new KeyValuePair<double[], bool>(v, rot));
            }
            foreach (var kv in dirs)
            {
                var dir = kv.Key;
                int axis = -1;
                for (int q = 0; q < 3; q++) if (Math.Abs(Math.Abs(dir[q]) - 1) < 1e-9) axis = q;
                string type = (kv.Value ? "R" : "") + (axis == 1 ? "Y" : axis == 2 ? "Z" : "X");
                var r = new PipeModelRestraint { Node = j.Node, CNode = j.CNode, Type = type, Code = PipeModelCodes.Restraint[type], Gap = d.Gap, Stiffness = d.Stiffness, Tag = j.Tag ?? "", Source = w };
                if (axis < 0) r.Cosines = new[] { dir[0], dir[1], dir[2] };
                restraints.Add(r);
            }
        }
        if (j.Sif > 0) sifs.Add(new PipeModelSif { Node = j.Node, Type = 0, SifIn = j.Sif, SifOut = j.Sif });
    }

    // two unit vectors square to a (the other two global axes when a is one)
    static void Perps(double[] a, out double[] p1, out double[] p2)
    {
        int ax = -1;
        for (int k = 0; k < 3; k++) if (Math.Abs(Math.Abs(a[k]) - 1) < 1e-9) ax = k;
        if (ax >= 0)
        {
            p1 = new double[3]; p1[ax == 0 ? 1 : 0] = 1;
            p2 = new double[3]; p2[ax == 2 ? 1 : 2] = 1;
            return;
        }
        double[] t = Math.Abs(a[1]) < 0.9 ? new[] { 0.0, 1.0, 0.0 } : new[] { 1.0, 0.0, 0.0 };
        p1 = Unit(Cross(a, t));
        p2 = Unit(Cross(a, p1));
    }

    static double[] Cross(double[] a, double[] b) { return new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] }; }
    static double[] Unit(double[] v) { double l = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]); return new[] { v[0] / l, v[1] / l, v[2] / l }; }

    // ---------------- checks

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
        foreach (var j in m.Joints)
            if (j.Node <= 0 || j.CNode <= 0 || j.Node == j.CNode) err.Add("joint " + j.Node + "-" + j.CNode + ": two different nodes are needed");
        foreach (var d in m.NodeLoads)
        {
            if (d.Node <= 0) err.Add((d.IsDisplacement ? "an imposed displacement" : "a force / moment") + " without a node");
            for (int v = 0; v < 9; v++) foreach (double x in d.Vectors[v])
                    if (double.IsInfinity(x) || (!double.IsNaN(x) && Math.Abs(x - PipeModelNodeLoad.Free) < 0.01 && !d.IsDisplacement))
                    { err.Add("node " + d.Node + ": load vector " + (v + 1) + " holds " + x + ", CAESAR's \"not given\" mark"); break; }
        }
        return err;
    }
}
