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
        List<AnlSection> sections = AnlIndexer.IndexFile(anlPath);
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
                case "PRINT MEMBER FORCES": r.Forces(s); anyForces = true; break;
            }
        }

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
        if (t == "D" && n.StartsWith("L")) return "2L";
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

        public Reading(AnlFrameModel m, string path) { _m = m; _path = path; }

        public List<string> Commands(AnlSection s) { return EchoCommands(Text(s, long.MaxValue)); }

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
                    if (t.Length > 2) _m.PropertyCountry = string.Join(" ", t.Skip(2).ToArray());
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
                if (kind == "TABLE" && i + 2 < t.Length) { table = t[i + 1].ToUpperInvariant(); section = t[i + 2]; }
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
                    mem.Property = spec; mem.Section = section; mem.Family = family;
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
            if (lt >= 0 && lt + 1 < u.Length) p.LoadType = t[lt + 1];
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

        public void Forces(AnlSection s)
        {
            Match mt = UnitsLine.Match(Text(s, 256 * 1024));
            ForceUnit fu; LengthUnit lu;
            if (!mt.Success || !TryForceWord(mt.Groups[1].Value, out fu) || !TryLengthWord(mt.Groups[2].Value, out lu))
            {
                _m.Error("FORCE_UNITS", "a PRINT MEMBER FORCES table without a readable \"ALL UNITS ARE --\" line" + (mt.Success ? " (" + mt.Value + ")" : ""));
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
            }
            if (unknownMembers.Count > 0)
                _m.Error("FORCE_MEMBER_UNKNOWN", "member force rows for members that have no incidence: " + Ids(unknownMembers.Distinct().ToList()));
            if (badJoints.Count > 0)
                _m.Error("FORCE_JOINT", "member force rows at a joint that is neither end of the member: members " + Ids(badJoints.Distinct().ToList()));
            if (dup > 0)
                _m.Warn("FORCE_PRINTED_TWICE", dup + " member end forces were printed more than once; the later print is kept");
        }
    }
}
