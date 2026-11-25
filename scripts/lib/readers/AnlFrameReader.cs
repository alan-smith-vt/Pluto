using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

// AnlFrameReader -- a STAAD.Pro output file (.anl) of a frame model, read for connection design (CalcFlow): joints,
// members (incidences, sections, BETA, releases, offsets, truss / tension / compression / cable), supports, load
// cases (primary, repeat loads, combinations with their factors), the member end forces of every printed case, and
// a joint index (the member ends that meet at each joint). C# 5 (Windows PowerShell 5.1 Add-Type, warnings are
// errors). vault/calcflow/calcflow-staad-frame-reader.md.
//
//   var m = AnlFrameReader.Read(path);      // never throws for the model's content: problems are listed
//   if (m.ErrorCount == 0) FramePackageWriter.Write(m, outDir);
//
// Where each value comes from:
//   - the input echo: STAAD writes every input line back as "  NN. <text>". Only those lines are read (page headers,
//     notes and warnings are not); a line ending in " -" continues on the next, and ";" splits commands.
//   - geometry in inches (Pluto's convention), from the UNIT in effect where each command stands (AnlUnitStamper).
//   - end forces as STAAD printed them (PRINT MEMBER FORCES): member local axes, STAAD's sign (the force on the
//     member at its end), in the units of the print's "ALL UNITS ARE --" line. A later print in other units is
//     converted to the first print's units, with a warning.
//   - local axes: STAAD's BETA-0 rule for Y up -- x from the start joint to the end joint; for a non-vertical member
//     z = x cross Y (horizontal) and y = z cross x (pointing up); for a vertical one z = +Z and y = z cross x -- then
//     turned by BETA about x, right-handed. The same rule FrameStd writes by (vault/format/frame-to-staad.md).
//
// Exact or flagged: what the reader cannot hold exactly is an Error (SET Z UP, generated joints or members, a list it
// cannot expand, force rows for a member or joint it does not know, a print without units), and the package writer
// refuses the model; what it keeps but cannot vouch for is a Warning (BETA ANGLE / RANGLE / REFERENCE, partial or
// spring releases, member offsets, near-vertical members, unit conversion).

public sealed class AnlFrameProblem
{
    public string Severity = "";   // Error | Warning
    public string Code = "";       // a short tag, e.g. NO_FORCES, MEMBER_OFFSET
    public string Message = "";

    public override string ToString() { return Severity + " " + Code + ": " + Message; }
}

public sealed class AnlFrameNode
{
    public int Id;
    public double X, Y, Z;          // inches, global
}

// one member end's releases, member local
public sealed class AnlFrameRelease
{
    public static readonly string[] Dofs = { "FX", "FY", "FZ", "MX", "MY", "MZ" };
    public readonly bool[] Released = new bool[6];
    public string Extra = "";       // partial fixity and spring terms as written ("MP 0.5", "KMZ 1000")

    public bool Any
    {
        get
        {
            for (int i = 0; i < 6; i++) if (Released[i]) return true;
            return Extra.Length > 0;
        }
    }

    // "MY MZ", "MZ KMZ 1000", "" for none
    public string Text
    {
        get
        {
            var parts = new List<string>();
            for (int i = 0; i < 6; i++) if (Released[i]) parts.Add(Dofs[i]);
            if (Extra.Length > 0) parts.Add(Extra);
            return string.Join(" ", parts.ToArray());
        }
    }
}

public sealed class AnlFrameMember
{
    public int Id;
    public int NodeA, NodeB;            // start (A) and end (B) joint, as in MEMBER INCIDENCES
    public string Property = "";        // the MEMBER PROPERTY spec after the member list, e.g. "TABLE ST W14X90"
    public string Section = "";         // W14X90; "PRIS" for prismatic numbers; "" when no property was read
    public string Family = "";          // W S M HP WT C MC L 2L HSS PIPE PRIS ("" unknown)
    public double PropertyInches = 1;   // inches per length unit where its MEMBER PROPERTY stands (PRIS numbers are as written)
    public double Beta;                 // degrees
    public bool BetaKnown = true;       // false: BETA ANGLE / RANGLE / REFERENCE / REFJT (the axes ignore it)
    public string BetaSpec = "";        // the BETA value as written when it is not a number
    public readonly AnlFrameRelease ReleaseA = new AnlFrameRelease();
    public readonly AnlFrameRelease ReleaseB = new AnlFrameRelease();
    public double[] OffsetA, OffsetB;   // inches; null = none
    public bool OffsetALocal, OffsetBLocal;
    public string Kind = "";            // "" | TRUSS | TENSION | COMPRESSION | CABLE
    public double Length;               // inches, joint to joint
    public double[] LocalX, LocalY, LocalZ;   // unit vectors in global axes; null when a joint is missing

    public AnlFrameRelease Release(string end) { return end == "B" ? ReleaseB : ReleaseA; }
    public int NodeAt(string end) { return end == "B" ? NodeB : NodeA; }
}

public sealed class AnlFrameSupport
{
    public int Node;
    public string Type = "";                      // FIXED | PINNED | FIXED BUT | ENFORCED | ENFORCED BUT | INCLINED | as written
    public readonly bool[] Restrained = new bool[6];   // FX FY FZ MX MY MZ, global; a spring is not a restraint
    public string Springs = "";                   // KFX f ... as written
    public string Spec = "";                      // the support spec after the joint list, as written

    public string RestrainedText
    {
        get
        {
            var parts = new List<string>();
            for (int i = 0; i < 6; i++) if (Restrained[i]) parts.Add(AnlFrameRelease.Dofs[i]);
            return string.Join(" ", parts.ToArray());
        }
    }
}

// One output command of the file (PRINT ..., CHECK CODE ...), in file order, with the LOAD LIST in force when it ran.
public sealed class AnlFramePrint
{
    public int Order;               // 1-based, in file order
    public string Command = "";     // as written: "PRINT SUPPORT REACTIONS"
    public bool AllCases = true;    // no LOAD LIST before it (or LOAD LIST ALL): every case
    public readonly List<int> Cases = new List<int>();    // the LOAD LIST in force (empty when AllCases)
    public string LoadList = "ALL"; // the list as written ("101 TO 104 201"), or ALL
    public bool Read;               // its table is read into the model
    public int Rows;                // rows read from its table
}

// One row of a joint table: a support reaction (FX FY FZ MX MY MZ, global, as printed) or a joint displacement
// (X Y Z translations and rotations, global, as printed).
public sealed class AnlFrameJointRow
{
    public int Node, LC;
    public readonly double[] V = new double[6];
}

public sealed class AnlFrameLoadCase
{
    public int Id;
    public string Kind = "";        // PRIMARY | REPEAT | COMBINATION | PRINTED (forces printed, no LOAD command read)
    public string Method = "";      // combinations: "" (algebraic sum) | SRSS | ABS
    public string LoadType = "";    // LOADTYPE as written
    public string Title = "";
    public readonly List<KeyValuePair<int, double>> Factors = new List<KeyValuePair<int, double>>();
    public bool HasForces;

    // "1*1.2 + 2*1.6"
    public string FactorsText
    {
        get
        {
            return string.Join(" + ", Factors.Select(f => f.Key.ToString(CultureInfo.InvariantCulture) + "*" +
                f.Value.ToString("R", CultureInfo.InvariantCulture)).ToArray());
        }
    }
}

// DEFINE ENVELOPE's envelope (TR.40): the cases and combinations it holds (a load listed in two is in the last only)
public sealed class AnlFrameEnvelope
{
    public int Number;
    public string Type = "";        // STRENGTH, SERVICEABILITY ... as StaadInput.EnvelopeType gives it
    public readonly List<int> Loads = new List<int>();
}

public sealed class AnlFrameEndForce
{
    public int Member;
    public string End = "";         // A (start joint) | B (end joint)
    public int Node;
    public int LC;
    public readonly float[] F = new float[6];   // Fx Fy Fz Mx My Mz: member local, STAAD's sign, the model's force units
}

// one member end at a joint
public sealed class AnlFrameMemberEnd
{
    public int Node;
    public int Member;
    public string End = "";         // A | B
    public double[] Dir;            // unit vector along the member, pointing away from the joint
    public string Role = "";        // Column (within 10 deg of vertical) | Beam (within 10 deg of horizontal) | Brace
    public string Section = "";
    public string Family = "";
    public string Kind = "";        // the member's Kind
    public string Released = "";    // this end's releases (AnlFrameRelease.Text)
    public double[] LocalY, LocalZ; // the member's local y and z in global axes
    public int ThroughMember;       // the collinear end of the same section at this joint (a continuous member); 0 = none
    public string ThroughEnd = "";
}

public sealed class AnlFrameJoint
{
    public int Node;
    public readonly List<AnlFrameMemberEnd> Ends = new List<AnlFrameMemberEnd>();
    public int Columns, Beams, Braces;
    public AnlFrameSupport Support;     // null = none
}

public sealed class AnlFrameModel
{
    public string Source = "";
    public string SourceSha256 = "";
    public ForceUnit ForceUnit = ForceUnit.Kip;         // the units of EndForces (the first print's)
    public LengthUnit ForceLength = LengthUnit.Foot;    // the length unit of the end moments
    public bool ForceUnitsKnown;
    public string Analysis = "";                        // PERFORM ANALYSIS / PDELTA ... as written
    public string PropertyCountry = "";                 // MEMBER PROPERTY AMERICAN ...

    public readonly SortedDictionary<int, AnlFrameNode> Nodes = new SortedDictionary<int, AnlFrameNode>();
    public readonly SortedDictionary<int, AnlFrameMember> Members = new SortedDictionary<int, AnlFrameMember>();
    public readonly SortedDictionary<int, AnlFrameSupport> Supports = new SortedDictionary<int, AnlFrameSupport>();
    public readonly SortedDictionary<int, AnlFrameLoadCase> LoadCases = new SortedDictionary<int, AnlFrameLoadCase>();
    public readonly SortedDictionary<int, AnlFrameJoint> Joints = new SortedDictionary<int, AnlFrameJoint>();
    public readonly List<AnlFrameEndForce> EndForces = new List<AnlFrameEndForce>();
    public readonly List<AnlFramePrint> Prints = new List<AnlFramePrint>();
    public readonly SortedDictionary<int, AnlFrameEnvelope> Envelopes = new SortedDictionary<int, AnlFrameEnvelope>();   // DEFINE ENVELOPE   // every output command, in file order
    // PRINT SUPPORT REACTIONS and PRINT JOINT DISPLACEMENTS, as printed (a later print of a joint and case replaces
    // the earlier one), with the units of their table headers ("kip", "kip-in"; "in": rotations are radians)
    public readonly List<AnlFrameJointRow> Reactions = new List<AnlFrameJointRow>();
    public readonly List<AnlFrameJointRow> Displacements = new List<AnlFrameJointRow>();
    public string ReactionForceText = "", ReactionMomentText = "", DisplacementLengthText = "";
    // the other tables, as printed (AnlFramePrints.cs): the code check per member and CHECK CODE command, with the
    // checks behind it where TRACK printed them; member information and properties; section displacements
    public readonly List<AnlFrameCodeCheck> CodeChecks = new List<AnlFrameCodeCheck>();
    public readonly List<AnlFrameCodeCheckDetail> CodeCheckDetails = new List<AnlFrameCodeCheckDetail>();
    public readonly List<AnlFrameMemberInfo> MemberInfo = new List<AnlFrameMemberInfo>();
    public readonly List<AnlFrameMemberProperty> MemberProperties = new List<AnlFrameMemberProperty>();
    public readonly List<AnlFrameSectionDisplacement> SectionDisplacements = new List<AnlFrameSectionDisplacement>();
    public readonly List<AnlFrameSectionMax> SectionMaxima = new List<AnlFrameSectionMax>();
    public string MemberInfoLengthText = "", MemberPropertyLengthText = "", SectionDisplacementLengthText = "";
    public int SectionStations;     // stations per member of SectionDisplacements (13: the ends and the twelfth points)
    public readonly List<AnlFrameProblem> Problems = new List<AnlFrameProblem>();
    readonly Dictionary<long, AnlFrameEndForce> _forceIndex = new Dictionary<long, AnlFrameEndForce>();

    public int ErrorCount { get { return Problems.Count(p => p.Severity == "Error"); } }
    public int WarningCount { get { return Problems.Count(p => p.Severity == "Warning"); } }

    public AnlFrameProblem[] ProblemsOf(string severity)
    {
        return Problems.Where(p => p.Severity == severity).ToArray();
    }

    // the end force of one member end in one case; null when it was not printed
    public AnlFrameEndForce EndForce(int member, string end, int lc)
    {
        AnlFrameEndForce f;
        return _forceIndex.TryGetValue(Key(member, end, lc), out f) ? f : null;
    }

    // "kip" / "kip-ft": the units of the end forces and moments
    public string ForceUnitText { get { return UnitText(ForceUnit); } }
    public string MomentUnitText { get { return UnitText(ForceUnit) + "-" + UnitText(ForceLength); } }

    public static string UnitText(ForceUnit f)
    {
        switch (f)
        {
            case ForceUnit.Pound: return "lbf";
            case ForceUnit.Kip: return "kip";
            case ForceUnit.Newton: return "N";
            case ForceUnit.KiloNewton: return "kN";
            default: return "mton";
        }
    }

    public static string UnitText(LengthUnit l)
    {
        switch (l)
        {
            case LengthUnit.Inch: return "in";
            case LengthUnit.Foot: return "ft";
            case LengthUnit.Meter: return "m";
            case LengthUnit.Millimeter: return "mm";
            default: return "cm";
        }
    }

    internal void Error(string code, string message) { Add("Error", code, message); }
    internal void Warn(string code, string message) { Add("Warning", code, message); }

    void Add(string severity, string code, string message)
    {
        var p = new AnlFrameProblem();
        p.Severity = severity; p.Code = code; p.Message = message;
        Problems.Add(p);
    }

    // a later print of the same member end and case replaces the earlier one; true when it did
    internal bool PutForce(AnlFrameEndForce f)
    {
        long k = Key(f.Member, f.End, f.LC);
        AnlFrameEndForce old;
        if (_forceIndex.TryGetValue(k, out old))
        {
            Array.Copy(f.F, old.F, 6);
            return true;
        }
        _forceIndex[k] = f;
        EndForces.Add(f);
        return false;
    }

    static long Key(int member, string end, int lc)
    {
        unchecked { return ((long)member << 33) | ((long)(end == "B" ? 1 : 0) << 32) | (uint)lc; }
    }
}

// STAAD's member local axes (Y up)
public static class StaadAxes
{
    public const double VerticalTol = 1e-6;       // horizontal projection of x below this: STAAD's vertical-member rule
    public const double NearVerticalTol = 0.01;   // above VerticalTol but below this: flagged (STAAD's axes swing there)

    // x, y, z (unit vectors, global) of a member from a to b, turned by betaDeg about x. False for a zero length.
    public static bool Compute(double[] a, double[] b, double betaDeg, out double[] x, out double[] y, out double[] z, out bool nearVertical)
    {
        x = null; y = null; z = null; nearVertical = false;
        double dx = b[0] - a[0], dy = b[1] - a[1], dz = b[2] - a[2];
        double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (len <= 0) return false;
        double cx = dx / len, cy = dy / len, cz = dz / len;
        double cxz = Math.Sqrt(cx * cx + cz * cz);
        double[] y0, z0;
        if (cxz < VerticalTol)
        {
            double s = cy > 0 ? 1.0 : -1.0;
            cx = 0; cz = 0; cy = s;
            y0 = new double[] { -s, 0, 0 };
            z0 = new double[] { 0, 0, 1 };
        }
        else
        {
            z0 = new double[] { -cz / cxz, 0, cx / cxz };
            y0 = new double[] { -cx * cy / cxz, cxz, -cy * cz / cxz };
            nearVertical = cxz < NearVerticalTol;
        }
        double r = betaDeg * Math.PI / 180.0, cb = Math.Cos(r), sb = Math.Sin(r);
        x = Clean(new double[] { cx, cy, cz });
        y = Clean(new double[] { y0[0] * cb + z0[0] * sb, y0[1] * cb + z0[1] * sb, y0[2] * cb + z0[2] * sb });
        z = Clean(new double[] { -y0[0] * sb + z0[0] * cb, -y0[1] * sb + z0[1] * cb, -y0[2] * sb + z0[2] * cb });
        return true;
    }

    // 6.1e-17 from cos(90) reads as 0, and -0 as 0
    static double[] Clean(double[] v)
    {
        for (int i = 0; i < v.Length; i++) v[i] = Math.Round(v[i], 12) + 0.0;
        return v;
    }
}

public static class AnlFrameReader
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly Regex EchoLine = new Regex(@"^\s*\d+\.(?:\s+|$)(.*)$");
    static readonly Regex UnitsLine = new Regex(@"ALL\s+UNITS\s+ARE\s*-+\s*([A-Z]+)\s+([A-Z]+)", RegexOptions.IgnoreCase);
    const double Cos10 = 0.98480775301220802;     // cos 10 deg: a column is within 10 deg of vertical
    const double Sin10 = 0.17364817766693033;     // sin 10 deg: a beam is within 10 deg of horizontal
    const double Cos5 = 0.99619469809174555;      // through members: collinear within 5 deg

    public static AnlFrameModel Read(string anlPath)
    {
        var m = new AnlFrameModel();
        m.Source = Path.GetFullPath(anlPath);
        m.SourceSha256 = Sha256(anlPath);
        List<AnlSection> sections = CommandSections(anlPath, AnlIndexer.IndexFile(anlPath));
        AnlUnitStamper.StampUnits(anlPath, sections);
        var r = new Reading(m, anlPath);

        // pass 1: what every list refers to (groups, joints, members); pass 2: the rest, in file order
        foreach (AnlSection s in sections)
        {
            switch (s.Name)
            {
                case "SET": r.Set(s); break;
                case "START GROUP DEFINITION": r.Groups(s); break;
                case "JOINT COORDINATES": r.Joints(s); break;
                case "MEMBER INCIDENCES": r.Incidences(s); break;
            }
        }
        bool anyForces = false;
        foreach (AnlSection s in sections)
        {
            switch (s.Name)
            {
                case "MEMBER PROPERTY": r.Properties(s); break;
                case "CONSTANTS": r.Constants(s); break;
                case "MEMBER RELEASE": r.Releases(s); break;
                case "MEMBER OFFSET": r.Offsets(s); break;
                case "MEMBER TRUSS": r.Kinds(s, "TRUSS"); break;
                case "MEMBER TENSION": r.Kinds(s, "TENSION"); break;
                case "MEMBER COMPRESSION": r.Kinds(s, "COMPRESSION"); break;
                case "MEMBER CABLE": r.Kinds(s, "CABLE"); break;
                case "SUPPORTS": r.Supports(s); break;
                case "LOAD": r.Load(s); break;
                case "REPEAT LOAD": r.Repeat(s); break;
                case "PERFORM ANALYSIS":
                case "PDELTA":
                case "PERFORM":
                    r.Analysis(s); break;
                case "CHANGE":
                    if (r.Commands(s).Count > 0)
                        m.Warn("CHANGE", "the file has a CHANGE command (more than one analysis): each member end and case keeps its last print");
                    break;
                case "LOAD LIST": r.LoadList(s); break;
                case "DEFINE": r.Define(s); break;
                case "PRINT MEMBER FORCES": r.Forces(s, r.Print(s, true)); anyForces = true; break;
                case "PRINT JOINT DISPLACEMENTS": r.Displacements(s, r.Print(s, true)); break;
                case "CHECK CODE": r.CodeCheck(s, r.Print(s, false)); break;
                case "PRINT":
                case "PRINT SUPPORT REACTION":
                case "PRINT MEMBER":
                    {
                        // the indexer names a print by its first words: the command itself says which table follows.
                        // A print whose table is not read is recorded with its load list, and named below.
                        AnlFramePrint pr = r.Print(s, false);
                        string cmd = Regex.Replace(pr.Command.ToUpperInvariant(), @"\s+", " ");
                        if (cmd.StartsWith("PRINT SUPPORT REACTION")) { pr.Read = true; r.Reactions(s, pr); }
                        else if (cmd.StartsWith("PRINT JOINT COORD")) AnlFramePrintTables.JointCoordinates(r.Output(s), m, pr);
                        else if (cmd.StartsWith("PRINT MEMBER INFO")) AnlFramePrintTables.MemberInformation(r.Output(s), m, pr);
                        else if (cmd.StartsWith("PRINT MEMBER PROP")) AnlFramePrintTables.MemberProperties(r.Output(s), m, pr);
                        else if (cmd.StartsWith("PRINT SECTION DISP")) AnlFramePrintTables.SectionDisplacements(r.Output(s), m, pr);
                    }
                    break;
                case "PRINT MEMBER SECTION FORCES":
                case "PRINT ELEMENT STRESSES":
                case "PRINT ELEMENT JOINT STRESSES":
                    r.Print(s, false); break;
            }
        }
        r.CheckForces();
        var unread = m.Prints.Where(x => !x.Read).Select(x => x.Command).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (unread.Count > 0)
            m.Warn("PRINT_NOT_READ", "output in the file that is not read yet: " + string.Join("; ", unread.ToArray()));

        if (m.Nodes.Count == 0) m.Error("NO_JOINTS", "no JOINT COORDINATES in the input echo (is the echo off, SET ECHO OFF?)");
        if (m.Members.Count == 0) m.Error("NO_MEMBERS", "no MEMBER INCIDENCES in the input echo");
        if (!anyForces) m.Error("NO_FORCES", "no PRINT MEMBER FORCES in the file: add PRINT MEMBER FORCES ALL after the analysis");
        var noProp = m.Members.Values.Where(x => x.Section.Length == 0).Select(x => x.Id).ToList();
        if (noProp.Count > 0) m.Warn("NO_PROPERTY", "members without a MEMBER PROPERTY: " + Ids(noProp));
        foreach (int lc in m.EndForces.Select(f => f.LC).Distinct())
        {
            AnlFrameLoadCase c;
            if (!m.LoadCases.TryGetValue(lc, out c))
            {
                c = new AnlFrameLoadCase(); c.Id = lc; c.Kind = "PRINTED";
                m.LoadCases[lc] = c;
                m.Warn("CASE_UNKNOWN", "load case " + lc + " has printed forces but no LOAD command was read");
            }
            c.HasForces = true;
        }
        Axes(m);
        BuildJoints(m);
        return m;
    }

    // ---- local axes and the joint index ----

    static void Axes(AnlFrameModel m)
    {
        var near = new List<int>();
        foreach (AnlFrameMember mem in m.Members.Values)
        {
            AnlFrameNode a, b;
            if (!m.Nodes.TryGetValue(mem.NodeA, out a) || !m.Nodes.TryGetValue(mem.NodeB, out b)) continue;
            double[] pa = { a.X, a.Y, a.Z }, pb = { b.X, b.Y, b.Z };
            double[] x, y, z; bool nv;
            if (!StaadAxes.Compute(pa, pb, mem.BetaKnown ? mem.Beta : 0, out x, out y, out z, out nv))
            {
                m.Error("ZERO_LENGTH", "member " + mem.Id + " has zero length (joints " + mem.NodeA + " and " + mem.NodeB + ")");
                continue;
            }
            mem.LocalX = x; mem.LocalY = y; mem.LocalZ = z;
            mem.Length = Math.Sqrt(Sq(pb[0] - pa[0]) + Sq(pb[1] - pa[1]) + Sq(pb[2] - pa[2]));
            if (nv) near.Add(mem.Id);
        }
        if (near.Count > 0)
            m.Warn("NEAR_VERTICAL", "members within 0.6 deg of vertical but not vertical (STAAD's default axes swing there; check them in STAAD): " + Ids(near));
    }

    static void BuildJoints(AnlFrameModel m)
    {
        foreach (AnlFrameMember mem in m.Members.Values)
        {
            if (mem.LocalX == null) continue;
            AddEnd(m, mem, "A", mem.NodeA, mem.LocalX);
            AddEnd(m, mem, "B", mem.NodeB, new double[] { 0.0 - mem.LocalX[0], 0.0 - mem.LocalX[1], 0.0 - mem.LocalX[2] });   // 0 - x: no -0
        }
        foreach (AnlFrameJoint j in m.Joints.Values)
        {
            foreach (AnlFrameMemberEnd e in j.Ends)
            {
                if (e.Role == "Column") j.Columns++; else if (e.Role == "Beam") j.Beams++; else j.Braces++;
                foreach (AnlFrameMemberEnd o in j.Ends)
                {
                    if (ReferenceEquals(o, e) || o.Section != e.Section || e.Section.Length == 0) continue;
                    if (Dot(o.Dir, e.Dir) <= -Cos5) { e.ThroughMember = o.Member; e.ThroughEnd = o.End; break; }
                }
            }
        }
        foreach (AnlFrameSupport s in m.Supports.Values)
        {
            AnlFrameJoint j;
            if (!m.Joints.TryGetValue(s.Node, out j))
            {
                j = new AnlFrameJoint(); j.Node = s.Node;
                m.Joints[s.Node] = j;
            }
            j.Support = s;
        }
    }

    static void AddEnd(AnlFrameModel m, AnlFrameMember mem, string end, int node, double[] dir)
    {
        AnlFrameJoint j;
        if (!m.Joints.TryGetValue(node, out j)) { j = new AnlFrameJoint(); j.Node = node; m.Joints[node] = j; }
        var e = new AnlFrameMemberEnd();
        e.Node = node; e.Member = mem.Id; e.End = end; e.Dir = dir;
        double up = Math.Abs(dir[1]);
        e.Role = up >= Cos10 ? "Column" : (up <= Sin10 ? "Beam" : "Brace");
        e.Section = mem.Section; e.Family = mem.Family; e.Kind = mem.Kind;
        e.Released = mem.Release(end).Text;
        e.LocalY = mem.LocalY; e.LocalZ = mem.LocalZ;
        j.Ends.Add(e);
    }

    // The sections that are commands: the ones whose first line carries STAAD's echo number ("  141. PRINT ..."). The
    // indexer also starts a section at an output heading that reads like a command ("JOINT COORDINATES" over a printed
    // table, "UNITS ARE - INCH" over the section displacements); such a section is the output of the command before
    // it, so it is given back to that command.
    static List<AnlSection> CommandSections(string path, List<AnlSection> sections)
    {
        var commands = new List<AnlSection>();
        var buf = new byte[48];
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            foreach (AnlSection s in sections)
            {
                fs.Seek(s.StartOffset, SeekOrigin.Begin);
                int n = fs.Read(buf, 0, buf.Length);
                string head = Encoding.GetEncoding(28591).GetString(buf, 0, Math.Max(n, 0));
                int eol = head.IndexOf('\n');
                if (eol >= 0) head = head.Substring(0, eol);
                if (Regex.IsMatch(head, @"^\s*\d+\.(\s|$)")) commands.Add(s);
                else if (commands.Count > 0) commands[commands.Count - 1].EndOffset = s.EndOffset;
            }
        }
        return commands;
    }

    // ---- helpers ----

    static double Sq(double v) { return v * v; }
    static double Dot(double[] a, double[] b) { return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]; }

    internal static string Ids(List<int> ids)
    {
        var s = string.Join(", ", ids.Take(20).Select(i => i.ToString(Inv)).ToArray());
        return ids.Count > 20 ? s + " and " + (ids.Count - 20) + " more" : s;
    }

    static string Sha256(string path)
    {
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var sha = SHA256.Create())
        {
            byte[] h = sha.ComputeHash(fs);
            var sb = new StringBuilder();
            foreach (byte b in h) sb.Append(b.ToString("x2", Inv));
            return sb.ToString();
        }
    }

    // The input commands of a section: STAAD's echo lines only ("   12. TEXT"), continuation lines (" -" at the end)
    // joined, ";" splitting commands.
    internal static List<string> EchoCommands(string text)
    {
        var cmds = new List<string>();
        var pending = new StringBuilder();
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.IndexOf('\f') >= 0) continue;
            Match mt = EchoLine.Match(line);
            if (!mt.Success) continue;
            string body = mt.Groups[1].Value.TrimEnd();
            if (body.TrimStart().StartsWith("*", StringComparison.Ordinal)) continue;   // a comment line: no command
            if (body == "-" || body.EndsWith(" -"))
            {
                pending.Append(body.Substring(0, body.Length - 1)).Append(' ');
                continue;
            }
            pending.Append(body);
            SplitCommands(pending.ToString(), cmds);
            pending.Length = 0;
        }
        if (pending.Length > 0) SplitCommands(pending.ToString(), cmds);
        return cmds;
    }

    static void SplitCommands(string line, List<string> cmds)
    {
        foreach (string part in line.Split(';'))
        {
            string c = part.Trim();
            if (c.Length > 0) cmds.Add(c);
        }
    }

    internal static string[] Tokens(string cmd)
    {
        return cmd.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
    }

    // tokens with a quoted name kept whole: TABLE 'W' ST 'W24X55' -> TABLE, 'W', ST, 'W24X55'
    internal static string[] Quoted(string cmd)
    {
        return Regex.Matches(cmd ?? "", @"'[^']*'|\S+").Cast<Match>().Select(x => x.Value).ToArray();
    }

    internal static string Unquote(string t)
    {
        return t.Length >= 2 && t[0] == '\'' && t[t.Length - 1] == '\'' ? t.Substring(1, t.Length - 2).Trim() : t;
    }

    static bool IsInt(string t, out int v)
    {
        return int.TryParse(t, NumberStyles.None, Inv, out v);
    }

    static bool IsNum(string t, out double v)
    {
        return double.TryParse(t, NumberStyles.Float, Inv, out v);
    }

    // the force / length unit words of STAAD's "ALL UNITS ARE -- KIP  FEET" line (STAAD prints four letters)
    internal static bool TryForceWord(string w, out ForceUnit f)
    {
        f = ForceUnit.Kip;
        w = w.ToUpperInvariant();
        if (w.StartsWith("KIP")) { f = ForceUnit.Kip; return true; }
        if (w.StartsWith("POUN") || w == "LB" || w == "LBS") { f = ForceUnit.Pound; return true; }
        if (w == "KN" || w == "KNS") { f = ForceUnit.KiloNewton; return true; }
        if (w.StartsWith("NEWT") || w == "N") { f = ForceUnit.Newton; return true; }
        if (w.StartsWith("MTON")) { f = ForceUnit.MetricTon; return true; }
        return false;
    }

    internal static bool TryLengthWord(string w, out LengthUnit l)
    {
        l = LengthUnit.Foot;
        w = w.ToUpperInvariant();
        if (w.StartsWith("FEE") || w == "FT" || w == "FOOT") { l = LengthUnit.Foot; return true; }
        if (w.StartsWith("INCH") || w == "IN") { l = LengthUnit.Inch; return true; }
        if (w.StartsWith("METE") || w.StartsWith("METR") || w == "M") { l = LengthUnit.Meter; return true; }
        if (w == "MMS" || w == "MM") { l = LengthUnit.Millimeter; return true; }
        if (w == "CM" || w == "CMS") { l = LengthUnit.Centimeter; return true; }
        return false;
    }

    // the family of a STAAD section name ("" when unknown)
    internal static string FamilyOf(string table, string name)
    {
        string t = table.ToUpperInvariant(), n = name.ToUpperInvariant();
        if (t == "PRIS") return "PRIS";
        if ((t == "LD" || t == "SD") && n.StartsWith("L")) return "2L";   // double angles, long or short legs back to back
        if (t == "D" && (n.StartsWith("C") || n.StartsWith("MC"))) return "2C";
        if (t == "T") return "WT";
        if (n.StartsWith("HSS") || n.StartsWith("TUB")) return "HSS";
        if (n.StartsWith("PIP")) return "PIPE";
        if (n.StartsWith("HP")) return "HP";
        if (n.StartsWith("WT") || n.StartsWith("MT")) return "WT";
        if (n.StartsWith("MC")) return "MC";
        string[] single = { "W", "S", "M", "C", "L" };
        foreach (string f in single)
            if (n.Length > f.Length && n.StartsWith(f) && char.IsDigit(n[f.Length])) return f;
        return "";
    }

    // ---- one read: the model, the file, the groups ----

    sealed class Reading
    {
        readonly AnlFrameModel _m;
        readonly string _path;
        readonly Dictionary<string, List<int>> _groups = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        int _currentLoad = -1;
        bool _haveForceUnits;
        List<int> _list;            // the LOAD LIST in force (null: every case)
        string _listText = "ALL";
        ForceUnit _reactionF; LengthUnit _reactionL;     // the first reaction print's units, which the model keeps

        public Reading(AnlFrameModel m, string path) { _m = m; _path = path; }

        public List<string> Commands(AnlSection s) { return EchoCommands(Text(s, long.MaxValue)); }

        // everything a command printed (STAAD writes a NUL into some lines: read as a space)
        public string Output(AnlSection s) { return Text(s, long.MaxValue).Replace('\0', ' '); }

        string Text(AnlSection s, long maxBytes)
        {
            long len = Math.Min(s.EndOffset - s.StartOffset, maxBytes);
            if (len <= 0) return "";
            var buf = new byte[len];
            using (var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                fs.Seek(s.StartOffset, SeekOrigin.Begin);
                int got = 0;
                while (got < len)
                {
                    int n = fs.Read(buf, got, (int)Math.Min(len - got, 1 << 20));
                    if (n <= 0) break;
                    got += n;
                }
            }
            return Encoding.GetEncoding(28591).GetString(buf);   // ISO-8859-1: every byte is one char
        }

        double Inch(double v, AnlSection s) { return UnitConvert.ToInch(v, s.Length); }

        // a STAAD list at t[i]: ids, "a TO b [BY s]", group names, ALL. Stops at the first other token.
        List<int> List(string[] t, ref int i, out bool all, string cmd)
        {
            var ids = new List<int>();
            all = false;
            while (i < t.Length)
            {
                string u = t[i].ToUpperInvariant();
                int n;
                if (u == "ALL") { all = true; i++; continue; }
                if (u.StartsWith("_"))
                {
                    List<int> g;
                    if (_groups.TryGetValue(u, out g)) ids.AddRange(g);
                    else _m.Error("GROUP_UNKNOWN", "group " + t[i] + " is not defined: " + cmd);
                    i++;
                    continue;
                }
                if (u == "TO" && ids.Count > 0 && i + 1 < t.Length && IsInt(t[i + 1], out n))
                {
                    int from = ids[ids.Count - 1], step = 1;
                    i += 2;
                    if (i + 1 < t.Length && t[i].ToUpperInvariant() == "BY" && IsInt(t[i + 1], out step)) i += 2;
                    if (step <= 0) { _m.Error("LIST", "a BY step of 0: " + cmd); step = 1; }
                    for (int k = from + step; k <= n; k += step) ids.Add(k);
                    continue;
                }
                if (IsInt(t[i], out n)) { ids.Add(n); i++; continue; }
                break;
            }
            return ids;
        }

        List<int> AllMembers() { return _m.Members.Keys.ToList(); }

        AnlFrameMember Member(int id, string cmd)
        {
            AnlFrameMember mem;
            if (_m.Members.TryGetValue(id, out mem)) return mem;
            _m.Error("MEMBER_UNKNOWN", "member " + id + " has no incidence: " + cmd);
            return null;
        }

        // ---- pass 1 ----

        public void Set(AnlSection s)
        {
            foreach (string c in Commands(s))
            {
                string u = string.Join(" ", Tokens(c.ToUpperInvariant()));
                if (u.StartsWith("SET Z UP"))
                    _m.Error("Z_UP", "SET Z UP: the reader takes STAAD's Y-up axes only (a Z-up model has other default member axes)");
                else if (u.StartsWith("SET ECHO OFF"))
                    _m.Error("ECHO_OFF", "SET ECHO OFF: the input after it is not in the file, so it cannot be read");
            }
        }

        public void Groups(AnlSection s)
        {
            var gp = new AnlGroupParser(_path);
            gp.ParseGroups(s);
            foreach (var kv in gp.Groups)
            {
                List<int> l;
                if (!_groups.TryGetValue(kv.Key, out l)) { l = new List<int>(); _groups[kv.Key] = l; }
                l.AddRange(kv.Value);
            }
        }

        public void Joints(AnlSection s)
        {
            List<string> cmds = Commands(s);
            for (int k = 0; k < cmds.Count; k++)
            {
                string c = cmds[k];
                string[] t = Tokens(c);
                string u0 = t[0].ToUpperInvariant();
                if (k == 0 && u0 == "JOINT")
                {
                    if (c.ToUpperInvariant().Contains("CYLINDRICAL"))
                        _m.Error("JOINTS_CYLINDRICAL", "cylindrical joint coordinates are not read: " + c);
                    continue;
                }
                if (u0 == "REPEAT")
                {
                    _m.Error("JOINTS_GENERATED", "joints generated with REPEAT are not read (write the joints out): " + c);
                    continue;
                }
                int id;
                if (!IsInt(t[0], out id))
                {
                    _m.Warn("JOINTS_END", "the joint list ends at an unexpected command: " + c);
                    return;
                }
                double x, y, z;
                if (t.Length == 4 && IsNum(t[1], out x) && IsNum(t[2], out y) && IsNum(t[3], out z))
                {
                    var n = new AnlFrameNode();
                    n.Id = id; n.X = Inch(x, s); n.Y = Inch(y, s); n.Z = Inch(z, s);
                    if (_m.Nodes.ContainsKey(id)) _m.Warn("JOINT_TWICE", "joint " + id + " is defined twice (the later one is kept)");
                    _m.Nodes[id] = n;
                }
                else if (t.Length > 4)
                    _m.Error("JOINTS_GENERATED", "joint generation is not read (write the joints out): " + c);
                else
                    _m.Error("JOINT_LINE", "joint line not read: " + c);
            }
        }

        public void Incidences(AnlSection s)
        {
            List<string> cmds = Commands(s);
            for (int k = 0; k < cmds.Count; k++)
            {
                string c = cmds[k];
                string[] t = Tokens(c);
                if (k == 0 && t[0].ToUpperInvariant() == "MEMBER") continue;
                int id, a, b;
                if (!IsInt(t[0], out id))
                {
                    _m.Warn("MEMBERS_END", "the member list ends at an unexpected command: " + c);
                    return;
                }
                if (t.Length == 3 && IsInt(t[1], out a) && IsInt(t[2], out b))
                {
                    if (!_m.Nodes.ContainsKey(a) || !_m.Nodes.ContainsKey(b))
                        _m.Error("JOINT_UNKNOWN", "member " + id + " names a joint that is not defined: " + c);
                    var mem = new AnlFrameMember();
                    mem.Id = id; mem.NodeA = a; mem.NodeB = b;
                    if (_m.Members.ContainsKey(id)) _m.Warn("MEMBER_TWICE", "member " + id + " is defined twice (the later one is kept)");
                    _m.Members[id] = mem;
                }
                else if (t.Length > 3)
                    _m.Error("MEMBERS_GENERATED", "member generation is not read (write the members out): " + c);
                else
                    _m.Error("MEMBER_LINE", "member line not read: " + c);
            }
        }

        // ---- pass 2 ----

        public void Properties(AnlSection s)
        {
            List<string> cmds = Commands(s);
            for (int k = 0; k < cmds.Count; k++)
            {
                string c = cmds[k];
                string[] t = Tokens(c);
                if (k == 0 && t[0].ToUpperInvariant() == "MEMBER")
                {
                    // AMERICAN, or STAAD 2025's database name in quotes (it may hold spaces)
                    Match pm = Regex.Match(c, @"^\s*MEMBER\s+PROPERT\w*\s+(.+?)\s*$", RegexOptions.IgnoreCase);
                    if (pm.Success) _m.PropertyCountry = pm.Groups[1].Value;
                    continue;
                }
                int i = 0; bool all;
                List<int> ids = List(t, ref i, out all, c);
                if (all) ids = AllMembers();
                if (ids.Count == 0 || i >= t.Length)
                {
                    _m.Error("PROPERTY_LINE", "member property line not read: " + c);
                    continue;
                }
                string spec = string.Join(" ", t.Skip(i).ToArray());
                string kind = t[i].ToUpperInvariant();
                string section, table = "";
                string[] q = Quoted(spec);
                if (kind == "TABLE" && q.Length >= 4 && q[1].StartsWith("'", StringComparison.Ordinal))
                {
                    // STAAD 2025's database form: TABLE 'W' ST 'W24X55' (under MEMBER PROPERTY '<database>.db3')
                    table = q[2].ToUpperInvariant(); section = Unquote(q[3]);
                }
                else if (kind == "TABLE" && i + 2 < t.Length) { table = t[i + 1].ToUpperInvariant(); section = Unquote(t[i + 2]); }
                else if (kind == "PRIS" || kind == "PRISMATIC") { table = "PRIS"; section = "PRIS"; }
                else if (kind == "UPTABLE" && i + 2 < t.Length) { table = "UPTABLE " + t[i + 1]; section = t[i + 2]; }
                else
                {
                    section = t[i];
                    _m.Warn("PROPERTY_FORM", "member property form kept as written, its section family not known: " + c);
                }
                string family = FamilyOf(table, section);
                foreach (int id in ids)
                {
                    AnlFrameMember mem = Member(id, c);
                    if (mem == null) continue;
                    mem.Property = spec; mem.Section = section; mem.Family = family; mem.PropertyInches = Inch(1, s);
                }
            }
        }

        public void Constants(AnlSection s)
        {
            foreach (string c in Commands(s))
            {
                string[] t = Tokens(c);
                if (t[0].ToUpperInvariant() != "BETA") continue;
                if (t.Length < 3) { _m.Error("BETA_LINE", "BETA line not read: " + c); continue; }
                int i = 1;
                double beta = 0; bool known = true; string spec = "";
                string v = t[1].ToUpperInvariant();
                if (IsNum(t[1], out beta)) i = 2;
                else if (v == "ANGLE" || v == "RANGLE") { known = false; spec = v; i = 2; }
                else if (v == "REFERENCE" || v == "REF") { known = false; spec = string.Join(" ", t.Skip(1).Take(4).ToArray()); i = 5; }
                else if (v == "REFJT") { known = false; spec = string.Join(" ", t.Skip(1).Take(2).ToArray()); i = 3; }
                else { _m.Error("BETA_LINE", "BETA line not read: " + c); continue; }
                if (i < t.Length && (t[i].ToUpperInvariant() == "MEMB" || t[i].ToUpperInvariant() == "MEMBER")) i++;
                bool all;
                List<int> ids = List(t, ref i, out all, c);
                if (all) ids = AllMembers();
                if (i < t.Length || ids.Count == 0) { _m.Error("BETA_LIST", "BETA member list not read: " + c); continue; }
                foreach (int id in ids)
                {
                    AnlFrameMember mem = Member(id, c);
                    if (mem == null) continue;
                    mem.Beta = known ? beta : 0; mem.BetaKnown = known; mem.BetaSpec = spec;
                }
                if (!known)
                    _m.Warn("BETA_UNKNOWN", "BETA " + spec + " is not worked out by the reader; the axes of these members are taken at BETA 0 and are not to be trusted: " + Ids(ids));
            }
        }

        public void Releases(AnlSection s)
        {
            List<string> cmds = Commands(s);
            var twice = new List<int>();
            var springy = new List<int>();
            for (int k = 0; k < cmds.Count; k++)
            {
                string c = cmds[k];
                string[] t = Tokens(c);
                if (k == 0 && t[0].ToUpperInvariant() == "MEMBER") continue;
                int i = 0; bool all;
                List<int> ids = List(t, ref i, out all, c);
                if (all) ids = AllMembers();
                if (ids.Count == 0 || i >= t.Length) { _m.Error("RELEASE_LINE", "member release line not read: " + c); continue; }
                string where = t[i].ToUpperInvariant();
                if (where != "START" && where != "END" && where != "BOTH") { _m.Error("RELEASE_LINE", "member release line not read: " + c); continue; }
                i++;
                var rel = new AnlFrameRelease();
                var extra = new List<string>();
                bool ok = true;
                while (i < t.Length)
                {
                    string u = t[i].ToUpperInvariant();
                    int dof = Array.IndexOf(AnlFrameRelease.Dofs, u);
                    double val;
                    if (dof >= 0) { rel.Released[dof] = true; i++; continue; }
                    if (u.Length == 3 && u[0] == 'K' && Array.IndexOf(AnlFrameRelease.Dofs, u.Substring(1)) >= 0 && i + 1 < t.Length && IsNum(t[i + 1], out val))
                    {
                        rel.Released[Array.IndexOf(AnlFrameRelease.Dofs, u.Substring(1))] = true;
                        extra.Add(u + " " + t[i + 1]); i += 2; continue;
                    }
                    if ((u == "MP" || u == "MPX" || u == "MPY" || u == "MPZ") && i + 1 < t.Length && IsNum(t[i + 1], out val))
                    {
                        extra.Add(u + " " + t[i + 1]); i += 2; continue;
                    }
                    ok = false; break;
                }
                if (!ok) { _m.Error("RELEASE_LINE", "member release line not read: " + c); continue; }
                rel.Extra = string.Join(" ", extra.ToArray());
                foreach (int id in ids)
                {
                    AnlFrameMember mem = Member(id, c);
                    if (mem == null) continue;
                    if (where == "START" || where == "BOTH") { if (mem.ReleaseA.Any) twice.Add(id); Copy(rel, mem.ReleaseA); }
                    if (where == "END" || where == "BOTH") { if (mem.ReleaseB.Any) twice.Add(id); Copy(rel, mem.ReleaseB); }
                    if (rel.Extra.Length > 0) springy.Add(id);
                }
            }
            if (twice.Count > 0) _m.Warn("RELEASE_TWICE", "a member end released twice keeps the later line: " + Ids(twice.Distinct().ToList()));
            if (springy.Count > 0) _m.Warn("RELEASE_PARTIAL", "partial or spring releases (kept as written; the connection is neither pinned nor fixed): " + Ids(springy.Distinct().ToList()));
        }

        static void Copy(AnlFrameRelease from, AnlFrameRelease to)
        {
            Array.Copy(from.Released, to.Released, 6);
            to.Extra = from.Extra;
        }

        public void Offsets(AnlSection s)
        {
            List<string> cmds = Commands(s);
            var ids = new List<int>();
            for (int k = 0; k < cmds.Count; k++)
            {
                string c = cmds[k];
                string[] t = Tokens(c);
                if (k == 0 && t[0].ToUpperInvariant() == "MEMBER") continue;
                int i = 0; bool all;
                List<int> list = List(t, ref i, out all, c);
                if (all) list = AllMembers();
                string where = i < t.Length ? t[i].ToUpperInvariant() : "";
                if (list.Count == 0 || (where != "START" && where != "END")) { _m.Error("OFFSET_LINE", "member offset line not read: " + c); continue; }
                i++;
                bool local = false;
                if (i < t.Length && t[i].ToUpperInvariant() == "LOCAL") { local = true; i++; }
                var v = new double[3];
                bool ok = true;
                for (int d = 0; d < 3; d++)
                {
                    double x = 0;
                    if (i < t.Length && IsNum(t[i], out x)) { v[d] = Inch(x, s); i++; }
                    else if (d == 0) ok = false;   // STAAD lets trailing zeros go unwritten
                }
                if (i < t.Length && t[i].ToUpperInvariant() == "LOCAL") { local = true; i++; }   // LOCAL after the values
                if (!ok || i < t.Length) { _m.Error("OFFSET_LINE", "member offset line not read: " + c); continue; }
                foreach (int id in list)
                {
                    AnlFrameMember mem = Member(id, c);
                    if (mem == null) continue;
                    if (where == "START") { mem.OffsetA = (double[])v.Clone(); mem.OffsetALocal = local; }
                    else { mem.OffsetB = (double[])v.Clone(); mem.OffsetBLocal = local; }
                    ids.Add(id);
                }
            }
            if (ids.Count > 0)
                _m.Warn("MEMBER_OFFSET", "member offsets: these members' end forces act at the offset point, not at the joint: " + Ids(ids.Distinct().ToList()));
        }

        public void Kinds(AnlSection s, string kind)
        {
            List<string> cmds = Commands(s);
            var ids = new List<int>();
            for (int k = 0; k < cmds.Count; k++)
            {
                string[] t = Tokens(cmds[k]);
                int i = 0;
                if (k == 0) i = 2;   // "MEMBER TRUSS [list]"
                bool all;
                List<int> list = List(t, ref i, out all, cmds[k]);
                if (all) list = AllMembers();
                if (i < t.Length)
                {
                    if (kind == "CABLE") _m.Warn("CABLE_DATA", "cable data kept out of the model: " + cmds[k]);
                    else { _m.Error("MEMBER_KIND_LINE", "member " + kind + " line not read: " + cmds[k]); continue; }
                }
                ids.AddRange(list);
            }
            foreach (int id in ids)
            {
                AnlFrameMember mem = Member(id, "MEMBER " + kind);
                if (mem != null) mem.Kind = kind;
            }
        }

        public void Supports(AnlSection s)
        {
            List<string> cmds = Commands(s);
            var inclined = new List<int>();
            for (int k = 0; k < cmds.Count; k++)
            {
                string c = cmds[k];
                string[] t = Tokens(c);
                if (k == 0 && t[0].ToUpperInvariant().StartsWith("SUPPORT")) continue;
                int i = 0; bool all;
                List<int> nodes = List(t, ref i, out all, c);
                if (all) nodes = _m.Nodes.Keys.ToList();
                if (nodes.Count == 0 || i >= t.Length) { _m.Error("SUPPORT_LINE", "support line not read: " + c); continue; }
                var sup = new AnlFrameSupport();
                sup.Spec = string.Join(" ", t.Skip(i).ToArray());
                string kind = t[i].ToUpperInvariant();
                bool ok = true;
                if (kind == "PINNED")
                {
                    sup.Type = "PINNED";
                    sup.Restrained[0] = sup.Restrained[1] = sup.Restrained[2] = true;
                    if (i + 1 < t.Length) ok = false;
                }
                else if (kind == "FIXED" || kind == "ENFORCED")
                {
                    for (int d = 0; d < 6; d++) sup.Restrained[d] = true;
                    sup.Type = kind;
                    i++;
                    if (i < t.Length)
                    {
                        if (t[i].ToUpperInvariant() != "BUT") ok = false;
                        else
                        {
                            sup.Type = kind + " BUT";
                            i++;
                            var springs = new List<string>();
                            while (i < t.Length && ok)
                            {
                                string u = t[i].ToUpperInvariant();
                                int dof = Array.IndexOf(AnlFrameRelease.Dofs, u);
                                double val;
                                if (dof >= 0) { sup.Restrained[dof] = false; i++; }
                                else if (u.Length == 3 && u[0] == 'K' && Array.IndexOf(AnlFrameRelease.Dofs, u.Substring(1)) >= 0 && i + 1 < t.Length && IsNum(t[i + 1], out val))
                                {
                                    sup.Restrained[Array.IndexOf(AnlFrameRelease.Dofs, u.Substring(1))] = false;
                                    springs.Add(u + " " + t[i + 1]); i += 2;
                                }
                                else ok = false;
                            }
                            sup.Springs = string.Join(" ", springs.ToArray());
                        }
                    }
                }
                else
                {
                    sup.Type = kind;
                    if (kind == "INCLINED") inclined.AddRange(nodes);
                    else _m.Warn("SUPPORT_FORM", "support kind kept as written, its restraints not read: " + c);
                }
                if (!ok) { _m.Error("SUPPORT_LINE", "support line not read: " + c); continue; }
                foreach (int n in nodes)
                {
                    if (!_m.Nodes.ContainsKey(n)) { _m.Error("JOINT_UNKNOWN", "support at joint " + n + ", which is not defined: " + c); continue; }
                    var one = new AnlFrameSupport();
                    one.Node = n; one.Type = sup.Type; one.Spec = sup.Spec; one.Springs = sup.Springs;
                    Array.Copy(sup.Restrained, one.Restrained, 6);
                    _m.Supports[n] = one;
                }
            }
            if (inclined.Count > 0)
                _m.Warn("SUPPORT_INCLINED", "inclined supports kept as written, their restraints not read (member forces are unaffected): " + Ids(inclined));
        }

        public void Load(AnlSection s)
        {
            List<string> cmds = Commands(s);
            if (cmds.Count == 0) return;   // the analysis output's "LOADING n" lines are not echo lines
            string[] t = Tokens(cmds[0]);
            string[] u = Tokens(cmds[0].ToUpperInvariant());
            if (u[0] != "LOAD") return;
            if (u.Length > 1 && (u[1] == "COMBINATION" || u[1] == "COMB"))
            {
                int i = 2;
                string method = "";
                if (i < u.Length && (u[i] == "SRSS" || u[i] == "ABS")) { method = u[i]; i++; }
                int id;
                if (i >= u.Length || !IsInt(u[i], out id)) { _m.Error("LOAD_LINE", "load combination line not read: " + cmds[0]); return; }
                i++;
                var c = NewCase(id, "COMBINATION");
                c.Method = method;
                c.Title = string.Join(" ", t.Skip(i).ToArray());
                for (int k = 1; k < cmds.Count; k++) if (!Factors(cmds[k], c)) break;
                _currentLoad = -1;
                return;
            }
            int lc;
            if (u.Length < 2 || !IsInt(u[1], out lc)) { _currentLoad = -1; return; }   // reference loads (LOAD R1), LOAD GENERATION
            var p = NewCase(lc, "PRIMARY");
            int ti = Array.IndexOf(u, "TITLE");
            int lt = Array.IndexOf(u, "LOADTYPE");
            // "Roof Live", "Seismic-H": every word up to TITLE
            if (lt >= 0 && lt + 1 < u.Length) p.LoadType = string.Join(" ", t.Skip(lt + 1).Take((ti > lt ? ti : t.Length) - lt - 1).ToArray());
            if (ti >= 0) p.Title = string.Join(" ", t.Skip(ti + 1).ToArray());
            else if (lt < 0) p.Title = string.Join(" ", t.Skip(2).ToArray());
            _currentLoad = lc;
        }

        AnlFrameLoadCase NewCase(int id, string kind)
        {
            if (_m.LoadCases.ContainsKey(id)) _m.Warn("CASE_TWICE", "load case " + id + " is defined twice (the later one is kept)");
            var c = new AnlFrameLoadCase();
            c.Id = id; c.Kind = kind;
            _m.LoadCases[id] = c;
            return c;
        }

        // "1 1.2 2 1.6": case / factor pairs; false at a command that is not one (the end of the factor lines)
        bool Factors(string cmd, AnlFrameLoadCase c)
        {
            string[] t = Tokens(cmd);
            int first;
            if (!IsInt(t[0], out first)) return false;
            if (t.Length % 2 != 0) { _m.Error("FACTOR_LINE", "factor line of load case " + c.Id + " not read: " + cmd); return true; }
            for (int i = 0; i < t.Length; i += 2)
            {
                int lc; double f;
                if (!IsInt(t[i], out lc) || !IsNum(t[i + 1], out f)) { _m.Error("FACTOR_LINE", "factor line of load case " + c.Id + " not read: " + cmd); return true; }
                c.Factors.Add(new KeyValuePair<int, double>(lc, f));
            }
            return true;
        }

        public void Repeat(AnlSection s)
        {
            List<string> cmds = Commands(s);
            if (cmds.Count == 0) return;
            AnlFrameLoadCase c;
            if (_currentLoad < 0 || !_m.LoadCases.TryGetValue(_currentLoad, out c))
            {
                _m.Error("REPEAT_LOAD", "REPEAT LOAD outside a primary load case: " + cmds[0]);
                return;
            }
            c.Kind = "REPEAT";
            for (int k = 1; k < cmds.Count; k++) if (!Factors(cmds[k], c)) break;
        }

        public void Analysis(AnlSection s)
        {
            List<string> cmds = Commands(s);
            if (cmds.Count == 0) return;
            string a = cmds[0];
            string u = a.ToUpperInvariant();
            if (!u.Contains("ANALYSIS")) return;
            _m.Analysis = _m.Analysis.Length == 0 ? a : _m.Analysis + "; " + a;
        }

        // LOAD LIST 101 TO 104 201 | LOAD LIST ALL: the cases every later output command covers
        // DEFINE ENVELOPE: "<load list> ENVELOPE i [TYPE t]" lines; another DEFINE is not read here
        public void Define(AnlSection s)
        {
            List<string> cmds = Commands(s);
            if (cmds.Count == 0 || !Regex.IsMatch(cmds[0], @"^\s*DEFINE\s+ENV", RegexOptions.IgnoreCase)) return;
            foreach (string c in cmds.Skip(1))
            {
                string[] t = Tokens(c);
                int i = 0; bool all; int n;
                List<int> ids = List(t, ref i, out all, c);
                if (all || ids.Count == 0 || i + 1 >= t.Length || !t[i].StartsWith("ENV", StringComparison.OrdinalIgnoreCase) || !int.TryParse(t[i + 1], NumberStyles.None, Inv, out n))
                { _m.Warn("ENVELOPE_LINE", "DEFINE ENVELOPE line not read: " + c); continue; }
                AnlFrameEnvelope e;
                if (!_m.Envelopes.TryGetValue(n, out e)) _m.Envelopes[n] = e = new AnlFrameEnvelope { Number = n };
                if (i + 3 < t.Length && t[i + 2].StartsWith("TYP", StringComparison.OrdinalIgnoreCase)) e.Type = StaadInput.EnvelopeType(t[i + 3]);
                // a load stays in every envelope that lists it: STAAD.Pro 2025's LOAD LIST ENV lists it under each (round 4,
                // 2026-11-25; TR.40 says the last only). Its design takes the last envelope's TYPE, which STAAD warns of
                e.Loads.AddRange(ids.Distinct().Where(x => !e.Loads.Contains(x)));
            }
        }

        public void LoadList(AnlSection s)
        {
            List<string> cmds = Commands(s);
            if (cmds.Count == 0) return;
            string[] t = Tokens(cmds[0]);
            // LOAD LIST ENV <envelopes>: their loads (DEFINE ENVELOPE)
            if (t.Length > 2 && t[2].StartsWith("ENV", StringComparison.OrdinalIgnoreCase))
            {
                int j = 3; bool allEnv;
                List<int> envs = List(t, ref j, out allEnv, cmds[0]);
                var loads = new List<int>();
                var missing = new List<int>();
                foreach (int e in allEnv ? _m.Envelopes.Keys.ToList() : envs)
                {
                    AnlFrameEnvelope env;
                    if (_m.Envelopes.TryGetValue(e, out env)) loads.AddRange(env.Loads); else missing.Add(e);
                }
                if (missing.Count > 0) _m.Warn("LOAD_LIST_ENVELOPE", "LOAD LIST ENV names envelopes no DEFINE ENVELOPE gives: " + Ids(missing));
                if (j < t.Length) _m.Warn("LOAD_LIST", "a LOAD LIST was not read to its end (at '" + t[j] + "'): " + cmds[0]);
                _list = loads.Distinct().ToList();
                _listText = string.Join(" ", t.Skip(2).ToArray());
                return;
            }
            int i = 2; bool all;
            List<int> ids = List(t, ref i, out all, cmds[0]);
            if (all || (ids.Count == 0 && i >= t.Length)) { _list = null; _listText = "ALL"; return; }
            if (i < t.Length) _m.Warn("LOAD_LIST", "a LOAD LIST was not read to its end (at '" + t[i] + "'): " + cmds[0]);
            _list = ids.Distinct().ToList();
            _listText = string.Join(" ", t.Skip(2).ToArray());
            var unknown = _list.Where(x => !_m.LoadCases.ContainsKey(x)).ToList();
            if (unknown.Count > 0) _m.Warn("LOAD_LIST_CASE", "LOAD LIST names cases with no LOAD command: " + Ids(unknown));
        }

        // an output command: recorded with the load list in force
        public AnlFramePrint Print(AnlSection s, bool read)
        {
            List<string> cmds = Commands(s);
            var p = new AnlFramePrint();
            p.Order = _m.Prints.Count + 1;
            p.Command = cmds.Count > 0 ? cmds[0] : s.Name;
            p.AllCases = _list == null;
            if (_list != null) p.Cases.AddRange(_list);
            p.LoadList = _listText;
            p.Read = read;
            _m.Prints.Add(p);
            return p;
        }

        // CHECK CODE ALL | CHECK CODE MEMB 101 TO 103 201 | CHECK CODE _GROUP: the members it names, for the ones the
        // output gives no result for
        public void CodeCheck(AnlSection s, AnlFramePrint print)
        {
            List<int> listed = null;
            string[] t = Tokens(print.Command);
            if (t.Length >= 3)
            {
                int i = 2;
                string w = t[i].ToUpperInvariant();
                if (w.StartsWith("MEMB")) i++;
                bool all;
                List<int> ids = List(t, ref i, out all, print.Command);
                if (all) listed = AllMembers();
                else if (i >= t.Length && ids.Count > 0) listed = ids;
            }
            AnlFramePrintTables.CodeCheck(Output(s), _m, print, listed);
        }

        static readonly Regex NumbersOnly = new Regex(@"^[\s\d.+\-Ee]+$");
        static readonly Regex ReactionUnits = new Regex(@"SUPPORT\s+REACTIONS?\s*-\s*UNIT\s+([A-Z]+)\s+([A-Z]+)", RegexOptions.IgnoreCase);
        static readonly Regex DisplacementUnits = new Regex(@"JOINT\s+DISPLACEMENTS?\s*\(\s*([A-Z]+)\s+RADIANS?\s*\)", RegexOptions.IgnoreCase);

        // The rows of a joint table ("JOINT LOAD v1 .. v6"): the joint and the case on a joint's first row, the case
        // alone on its other rows. A line of numbers that is neither is an error: the rows after it would go to the
        // joint before.
        List<AnlFrameJointRow> JointRows(string text, AnlFramePrint print, string code)
        {
            var rows = new List<AnlFrameJointRow>();
            var index = new Dictionary<long, AnlFrameJointRow>();
            int joint = -1, bad = 0;
            string sample = "";
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.IndexOf('\f') >= 0 || EchoLine.IsMatch(line)) continue;
                // a data line holds nothing but numbers; one whose numbers run together ("3.74-123456.67") is not
                // split by guessing: it is counted as unread
                if (!NumbersOnly.IsMatch(line)) continue;
                string[] t = Tokens(line);
                if (t.Length < 4) continue;
                var v = new double[t.Length];
                bool numeric = true;
                for (int i = 0; i < t.Length && numeric; i++) numeric = IsNum(t[i], out v[i]);
                int a = 0, b = 0, at = 0;
                if (numeric && t.Length == 8 && IsInt(t[0], out a) && IsInt(t[1], out b)) { joint = a; at = 2; }
                else if (numeric && t.Length == 7 && IsInt(t[0], out b) && joint >= 0) at = 1;
                else { bad++; if (sample.Length == 0) sample = line.Trim(); continue; }
                long key = ((long)joint << 32) | (uint)b;
                AnlFrameJointRow row;
                if (!index.TryGetValue(key, out row)) { row = new AnlFrameJointRow(); row.Node = joint; row.LC = b; index[key] = row; rows.Add(row); }
                for (int i = 0; i < 6; i++) row.V[i] = v[at + i];
                print.Rows++;
            }
            if (bad > 0)
                _m.Error(code, bad + " line(s) of " + print.Command + " hold numbers but are not a row of 7 or 8 numbers (touching columns? overflow?), e.g. \"" + sample + "\"");
            return rows;
        }

        // rows x listed cases: a table shorter than its load list is said
        void CheckJointTable(List<AnlFrameJointRow> rows, AnlFramePrint print, int joints, string code, string what)
        {
            if (print.AllCases || joints == 0) return;
            int want = joints * print.Cases.Count;
            if (rows.Count != want)
                _m.Warn(code, print.Command + " gave " + rows.Count + " rows; " + joints + " " + what + " x " + print.Cases.Count + " cases of LOAD LIST " + print.LoadList + " would be " + want);
        }

        public void Reactions(AnlSection s, AnlFramePrint print)
        {
            string text = Text(s, long.MaxValue);
            Match mt = ReactionUnits.Match(text);
            ForceUnit fu; LengthUnit lu;
            if (!mt.Success || !TryForceWord(mt.Groups[1].Value, out fu) || !TryLengthWord(mt.Groups[2].Value, out lu))
            {
                _m.Error("REACTION_UNITS", print.Command + ": no readable \"SUPPORT REACTIONS -UNIT\" line in its table");
                return;
            }
            string f = AnlFrameModel.UnitText(fu), m = f + "-" + AnlFrameModel.UnitText(lu);
            // a print in other units than the first (round 4 prints the first case in kN-m, then all in kip-in): converted to
            // the first's units; a case an earlier print gave keeps those rows, not overwritten by a converted copy
            bool other = _m.Reactions.Count > 0 && (f != _m.ReactionForceText || m != _m.ReactionMomentText);
            if (!other) { _m.ReactionForceText = f; _m.ReactionMomentText = m; _reactionF = fu; _reactionL = lu; }
            List<AnlFrameJointRow> rows = JointRows(text, print, "REACTION_ROW_UNREAD");
            var noSupport = rows.Where(x => !_m.Supports.ContainsKey(x.Node)).Select(x => x.Node).Distinct().ToList();
            if (noSupport.Count > 0) _m.Warn("REACTION_JOINT", "support reactions at joints with no SUPPORTS line: " + Ids(noSupport));
            CheckJointTable(rows, print, _m.Supports.Count, "REACTION_ROWS", "supports");
            if (other)
            {
                double kF = UnitConvert.Force(1, fu, _reactionF), kM = kF * UnitConvert.Length(1, lu, _reactionL);
                var held = new HashSet<long>(_m.Reactions.Select(x => ((long)x.Node << 32) | (uint)x.LC));
                int kept = rows.RemoveAll(x => held.Contains(((long)x.Node << 32) | (uint)x.LC));
                foreach (var r in rows) for (int i = 0; i < 6; i++) r.V[i] *= i < 3 ? kF : kM;
                _m.Warn("REACTION_UNITS", print.Command + " (LOAD LIST " + print.LoadList + ") is in " + m + ", the first reaction print in " + _m.ReactionMomentText + ": "
                    + (rows.Count > 0 ? rows.Count + " row(s) converted to " + _m.ReactionMomentText : "no new row")
                    + (kept > 0 ? "; " + kept + " row(s) an earlier print gave are kept from it" : ""));
            }
            Merge(_m.Reactions, rows);
        }

        public void Displacements(AnlSection s, AnlFramePrint print)
        {
            string text = Text(s, long.MaxValue);
            Match mt = DisplacementUnits.Match(text);
            LengthUnit lu;
            if (!mt.Success || !TryLengthWord(mt.Groups[1].Value, out lu))
            {
                _m.Error("DISPLACEMENT_UNITS", print.Command + ": no readable \"JOINT DISPLACEMENT (<length> RADIANS)\" line in its table");
                return;
            }
            string l = AnlFrameModel.UnitText(lu);
            if (_m.Displacements.Count > 0 && l != _m.DisplacementLengthText)
            {
                _m.Error("DISPLACEMENT_UNITS", "joint displacements are printed twice in different units (" + _m.DisplacementLengthText + ", then " + l + "): print them once");
                return;
            }
            _m.DisplacementLengthText = l;
            List<AnlFrameJointRow> rows = JointRows(text, print, "DISPLACEMENT_ROW_UNREAD");
            CheckJointTable(rows, print, _m.Nodes.Count, "DISPLACEMENT_ROWS", "joints");
            Merge(_m.Displacements, rows);
        }

        // a later print of the same joint and case replaces the earlier one
        static void Merge(List<AnlFrameJointRow> all, List<AnlFrameJointRow> rows)
        {
            if (all.Count == 0) { all.AddRange(rows); return; }
            var index = all.ToDictionary(x => ((long)x.Node << 32) | (uint)x.LC);
            foreach (AnlFrameJointRow r in rows)
            {
                AnlFrameJointRow old;
                if (index.TryGetValue(((long)r.Node << 32) | (uint)r.LC, out old)) Array.Copy(r.V, old.V, 6);
                else all.Add(r);
            }
        }

        // every member end with forces has both ends in the same cases; a listed case of a force print has its rows
        public void CheckForces()
        {
            var a = new Dictionary<int, HashSet<int>>();
            var b = new Dictionary<int, HashSet<int>>();
            foreach (AnlFrameEndForce f in _m.EndForces)
            {
                Dictionary<int, HashSet<int>> d = f.End == "A" ? a : b;
                HashSet<int> set;
                if (!d.TryGetValue(f.Member, out set)) { set = new HashSet<int>(); d[f.Member] = set; }
                set.Add(f.LC);
            }
            var oneEnd = new List<int>();
            foreach (int mem in a.Keys.Union(b.Keys).OrderBy(x => x))
            {
                HashSet<int> sa, sb;
                if (!a.TryGetValue(mem, out sa) || !b.TryGetValue(mem, out sb) || !sa.SetEquals(sb)) oneEnd.Add(mem);
            }
            if (oneEnd.Count > 0)
                _m.Error("FORCE_END_MISSING", "members with forces at one end only in some case (a row of the print was not read): " + Ids(oneEnd));
            var printed = new HashSet<int>(_m.EndForces.Select(f => f.LC));
            foreach (AnlFramePrint p in _m.Prints.Where(x => x.Read && !x.AllCases && x.Command.ToUpperInvariant().StartsWith("PRINT MEMBER FORCES")))
            {
                var missing = p.Cases.Where(lc => !printed.Contains(lc)).ToList();
                if (missing.Count > 0)
                    _m.Warn("FORCE_CASE_MISSING", p.Command + " under LOAD LIST " + p.LoadList + " printed no forces for case(s) " + Ids(missing));
            }
        }

        public void Forces(AnlSection s, AnlFramePrint print)
        {
            string head = Text(s, 256 * 1024);
            Match mt = UnitsLine.Match(head);
            ForceUnit fu; LengthUnit lu;
            if (!mt.Success || !TryForceWord(mt.Groups[1].Value, out fu) || !TryLengthWord(mt.Groups[2].Value, out lu))
            {
                _m.Error("FORCE_UNITS", "a PRINT MEMBER FORCES table without a readable \"ALL UNITS ARE --\" line" + (mt.Success ? " (" + mt.Value + ")" : ""));
                return;
            }
            // STAAD prints "(LOCAL )" after the units; a GLOBAL print is not the member's own axes
            if (Regex.IsMatch(head, @"ALL\s+UNITS\s+ARE[^\r\n]*\(\s*GLOBAL", RegexOptions.IgnoreCase) || Regex.IsMatch(print.Command, @"\bGLOBAL\b", RegexOptions.IgnoreCase))
            {
                _m.Error("FORCE_GLOBAL", print.Command + " printed the member forces in GLOBAL axes: print them in the member's local axes (PRINT MEMBER FORCES without GLOBAL)");
                return;
            }
            double ff = 1, mf = 1;
            if (!_haveForceUnits)
            {
                _m.ForceUnit = fu; _m.ForceLength = lu; _m.ForceUnitsKnown = true; _haveForceUnits = true;
            }
            else if (fu != _m.ForceUnit || lu != _m.ForceLength)
            {
                ff = UnitConvert.Force(1, fu, _m.ForceUnit);
                mf = ff * UnitConvert.Length(1, lu, _m.ForceLength);
                _m.Warn("FORCE_UNITS_CONVERTED", "a PRINT MEMBER FORCES table in " + AnlFrameModel.UnitText(fu) + "-" + AnlFrameModel.UnitText(lu) +
                    " was converted to the first table's " + _m.MomentUnitText);
            }
            var p = new AnlForceParser(_path);
            p.UnitsReadByCaller = true;
            p.ParseSection(s, new Dictionary<int, Member>(), CoordFrame.Local);
            var unknownMembers = new List<int>();
            var badJoints = new List<int>();
            int dup = 0;
            foreach (MemForce r in p.Forces)
            {
                AnlFrameMember mem;
                if (!_m.Members.TryGetValue(r.memID, out mem)) { unknownMembers.Add(r.memID); continue; }
                string end = r.nodeID == mem.NodeA ? "A" : (r.nodeID == mem.NodeB ? "B" : "");
                if (end.Length == 0) { badJoints.Add(r.memID); continue; }
                var f = new AnlFrameEndForce();
                f.Member = r.memID; f.End = end; f.Node = r.nodeID; f.LC = r.LC;
                for (int d = 0; d < 6; d++)
                {
                    double k = d < 3 ? ff : mf;
                    // converted from the printed decimal (1.632 kip-in / 12 = 0.136, not 0.13599999 through float arithmetic)
                    f.F[d] = k == 1 ? r.Ff[d] : (float)(double.Parse(r.Ff[d].ToString("R", Inv), Inv) * k);
                }
                if (_m.PutForce(f)) dup++;
                print.Rows++;
            }
            // an error, not a warning: the rows after an unread row are attributed to the member and case before it
            if (p.SuspectRows > 0)
                _m.Error("FORCE_ROW_UNREAD", p.SuspectRows + " line(s) of " + print.Command + " hold numbers but are not a force row of 7, 8 or 9 numbers (touching columns? overflow?), e.g. \"" +
                    string.Join("\" | \"", p.SuspectSamples.ToArray()) + "\"");
            if (unknownMembers.Count > 0)
                _m.Error("FORCE_MEMBER_UNKNOWN", "member force rows for members that have no incidence: " + Ids(unknownMembers.Distinct().ToList()));
            if (badJoints.Count > 0)
                _m.Error("FORCE_JOINT", "member force rows at a joint that is neither end of the member: members " + Ids(badJoints.Distinct().ToList()));
            if (dup > 0)
                _m.Warn("FORCE_PRINTED_TWICE", dup + " member end forces were printed more than once; the later print is kept");
        }
    }
}
