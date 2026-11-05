using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

// FrameModel  --  a SAP2000 frame model (piping, ducting and steel as frames, links, loads, cases) for the frame
// workbook (2026-11-05). It is the .s2k itself, typed: every object (a joint, a frame, a section, a link, a load
// pattern ...) owns the rows that describe it, in every table keyed by its label -- the ones Pluto reads (connectivity,
// sections, releases, local axes, loads, cases ...) and the ones it does not (design overwrites, auto-mesh, output
// stations ...), which then travel with it: a renamed object renames them, a deleted one takes them along.
// Values are held as the file's own TEXT: a value not edited is written back character for character, and the typed
// accessors (X, Section, Releases, ...) read and write through it; a number set is written in the shortest form that
// reads back exactly (Fmt). Rows no object owns (areas, solids, analysis options ...) stay in Source and are written
// as they were (FrameS2k). The model's units are the file's (PROGRAM CONTROL CurrUnits).
// C# 5, one global batch: every type is prefixed Frame.

// one row an object owns: its table and its fields, as text
public sealed class FrameRec
{
    public string Table = "";
    public readonly List<string[]> F = new List<string[]>();   // {key, value}, in order
    public S2kRow Row;                                         // the row it was read from (null = new); FrameS2k matches by it

    public FrameRec() { }
    public FrameRec(string table) { Table = table; }

    int Index(string k)
    {
        for (int i = 0; i < F.Count; i++) if (string.Equals(F[i][0], k, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }
    public bool Has(string k) { return Index(k) >= 0; }
    public string Get(string k) { int i = Index(k); return i < 0 ? null : F[i][1]; }
    public FrameRec Set(string k, string v) { int i = Index(k); if (i < 0) F.Add(new[] { k, v ?? "" }); else F[i][1] = v ?? ""; return this; }
    public void Remove(string k) { int i = Index(k); if (i >= 0) F.RemoveAt(i); }

    public double Num(string k, double def)
    {
        string s = Get(k);
        double v;
        return s != null && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : def;
    }
    // a number, kept as written when the new value is the same number
    public FrameRec SetNum(string k, double v)
    {
        string s = Get(k);
        double old;
        if (s != null && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out old) && old == v) return this;
        return Set(k, FrameModel.Fmt(v));
    }
    public bool Yes(string k) { string s = Get(k); return s != null && (s.Equals("Yes", StringComparison.OrdinalIgnoreCase) || s.Equals("True", StringComparison.OrdinalIgnoreCase)); }
    public FrameRec SetYes(string k, bool v) { if (Has(k) && Yes(k) == v) return this; return Set(k, v ? "Yes" : "No"); }

    public FrameRec Clone() { var r = new FrameRec(Table) { Row = Row }; foreach (var f in F) r.F.Add(new[] { f[0], f[1] }); return r; }
}

// an object: its identity and its rows
public abstract class FrameObj
{
    public int Uid;
    public readonly List<FrameRec> Recs = new List<FrameRec>();

    public abstract string Kind { get; }            // joint frame section material linkprop link group pattern load case combo
    public abstract string KeyField { get; }        // the field that names it in its rows
    public abstract string MainTable { get; }       // the table it is defined in

    public FrameRec Rec(string table) { foreach (var r in Recs) if (string.Equals(r.Table, table, StringComparison.OrdinalIgnoreCase)) return r; return null; }
    public IEnumerable<FrameRec> RecsOf(string table) { foreach (var r in Recs) if (string.Equals(r.Table, table, StringComparison.OrdinalIgnoreCase)) yield return r; }

    // the row in `table`, made when absent: keyed by the label, with the table's other fields at SAP's defaults in
    // SAP's order (FrameModel.Template), so a new row is complete
    public FrameRec Need(string table)
    {
        var r = Rec(table);
        if (r != null) return r;
        r = FrameModel.NewRec(table, KeyField, Label);
        Recs.Add(r);
        return r;
    }
    public void Drop(string table) { Recs.RemoveAll(r => string.Equals(r.Table, table, StringComparison.OrdinalIgnoreCase)); }

    // the label: the key field of its main row; renaming sets it in every row it owns
    public string Label
    {
        get { var r = Rec(MainTable) ?? (Recs.Count > 0 ? Recs[0] : null); return r == null ? "" : r.Get(KeyField) ?? ""; }
        set { foreach (var r in Recs) if (r.Has(KeyField)) r.Set(KeyField, value); }
    }
}

public sealed class FrameJoint : FrameObj
{
    public const string Coords = "JOINT COORDINATES", Restraints = "JOINT RESTRAINT ASSIGNMENTS", Springs = "JOINT SPRING ASSIGNMENTS 1 - UNCOUPLED",
                        LocalAxes = "JOINT LOCAL AXES ASSIGNMENTS 1 - TYPICAL";
    public static readonly string[] Dofs = { "U1", "U2", "U3", "R1", "R2", "R3" };
    public override string Kind { get { return "joint"; } }
    public override string KeyField { get { return "Joint"; } }
    public override string MainTable { get { return Coords; } }
    internal FrameModel M;

    // position in the global system (SAP's GlobalX/Y/Z when the row has them, else worked out from its system)
    public double[] Xyz { get { return M.GlobalOf(Rec(Coords)); } }
    public void SetXyz(double x, double y, double z)
    {
        var r = Need(Coords);
        // a joint given in another system (cylindrical: XorR T Z; spherical: XorR B A) becomes global X Y Z
        if (!IsGlobalCartesian(r))
        {
            r.Set("CoordSys", "GLOBAL"); r.Set("CoordType", "Cartesian");
            // the angle fields become Y (and Z) in their place, SAP's field order
            foreach (var f in r.F)
            {
                if ((f[0] == "T" || f[0] == "B") && !r.Has("Y")) f[0] = "Y";
                else if (f[0] == "A" && !r.Has("Z")) f[0] = "Z";
            }
            r.Remove("T"); r.Remove("B"); r.Remove("A");
        }
        r.SetNum("XorR", x).SetNum("Y", y).SetNum("Z", z);
        if (r.Has("GlobalX")) r.SetNum("GlobalX", x);
        if (r.Has("GlobalY")) r.SetNum("GlobalY", y);
        if (r.Has("GlobalZ")) r.SetNum("GlobalZ", z);
    }
    internal static bool IsGlobalCartesian(FrameRec r)
    {
        string cs = r.Get("CoordSys") ?? "GLOBAL", ct = r.Get("CoordType") ?? "Cartesian";
        return cs.Equals("GLOBAL", StringComparison.OrdinalIgnoreCase) && ct.Equals("Cartesian", StringComparison.OrdinalIgnoreCase);
    }

    // restraints U1..R3; all false = no row
    public bool[] Restraint
    {
        get { var r = Rec(Restraints); var o = new bool[6]; if (r != null) for (int i = 0; i < 6; i++) o[i] = r.Yes(Dofs[i]); return o; }
        set { if (value == null || !value.Any(b => b)) { Drop(Restraints); return; } var r = Need(Restraints); for (int i = 0; i < 6; i++) r.SetYes(Dofs[i], value[i]); }
    }
    // spring stiffness U1..R3 (joint's CoordSys of the row; GLOBAL for a new one); all 0 = no row
    public double[] Spring
    {
        get { var r = Rec(Springs); var o = new double[6]; if (r != null) for (int i = 0; i < 6; i++) o[i] = r.Num(Dofs[i], 0); return o; }
        set
        {
            if (value == null || !value.Any(v => v != 0)) { Drop(Springs); return; }
            var r = Rec(Springs);
            if (r == null) { r = Need(Springs); r.Set("CoordSys", "GLOBAL"); }
            for (int i = 0; i < 6; i++) r.SetNum(Dofs[i], value[i]);
        }
    }
    public double[] LocalAngles
    {
        get { var r = Rec(LocalAxes); return r == null ? new double[3] : new[] { r.Num("AngleA", 0), r.Num("AngleB", 0), r.Num("AngleC", 0) }; }
        set
        {
            if (value == null || value.All(v => v == 0)) { Drop(LocalAxes); return; }
            var r = Rec(LocalAxes);
            if (r == null) { r = Need(LocalAxes); r.Set("AngleA", "0").Set("AngleB", "0").Set("AngleC", "0").Set("AdvanceAxes", "No"); }
            r.SetNum("AngleA", value[0]).SetNum("AngleB", value[1]).SetNum("AngleC", value[2]);
        }
    }
}

public sealed class FrameMember : FrameObj
{
    public const string Conn = "CONNECTIVITY - FRAME", SectionAssign = "FRAME SECTION ASSIGNMENTS", LocalAxes = "FRAME LOCAL AXES ASSIGNMENTS 1 - TYPICAL",
                        Release = "FRAME RELEASE ASSIGNMENTS 1 - GENERAL", Partial = "FRAME RELEASE ASSIGNMENTS 2 - PARTIAL FIXITY",
                        Insertion = "FRAME INSERTION POINT ASSIGNMENTS", Modifiers = "FRAME PROPERTY MODIFIERS", EndOffsets = "FRAME OFFSET ALONG LENGTH ASSIGNMENTS";
    // the 12 end DOFs in SAP's order (I end, then J end); bit i of the viewer's release mask
    public static readonly string[] ReleaseDofs = { "PI", "V2I", "V3I", "TI", "M2I", "M3I", "PJ", "V2J", "V3J", "TJ", "M2J", "M3J" };
    // FRAME PROPERTY MODIFIERS' own names (SAP 24 export, 2026-11-05; the section table's A2Mod ... WMod are not read here)
    public static readonly string[] ModifierKeys = { "AMod", "AS2Mod", "AS3Mod", "JMod", "I22Mod", "I33Mod", "MassMod", "WeightMod" };
    public override string Kind { get { return "frame"; } }
    public override string KeyField { get { return "Frame"; } }
    public override string MainTable { get { return Conn; } }
    internal FrameModel M;

    public string JointI { get { var r = Rec(Conn); return r == null ? "" : r.Get("JointI") ?? ""; } set { Need(Conn).Set("JointI", value); } }
    public string JointJ { get { var r = Rec(Conn); return r == null ? "" : r.Get("JointJ") ?? ""; } set { Need(Conn).Set("JointJ", value); } }

    // the analysis section (AnalSect; DesignSect and SectionType follow it when changed)
    public string Section
    {
        get { var r = Rec(SectionAssign); return r == null ? "" : r.Get("AnalSect") ?? ""; }
        set
        {
            var r = Rec(SectionAssign);
            if (r != null && r.Get("AnalSect") == value) return;
            if (r == null) { r = Need(SectionAssign); r.Set("SectionType", "").Set("AutoSelect", "N.A.").Set("AnalSect", value).Set("DesignSect", value).Set("MatProp", "Default"); }
            r.Set("AnalSect", value);
            r.Set("DesignSect", value);
            var s = M == null ? null : M.SectionNamed(value);
            if (s != null) r.Set("SectionType", s.Shape);
        }
    }

    // SAP's advanced local axes (a reference vector / plane): the angle alone does not give the axes
    public bool AdvancedAxes { get { var r = Rec(LocalAxes); return r != null && r.Yes("AdvanceAxes"); } }

    public double Angle
    {
        get { var r = Rec(LocalAxes); return r == null ? 0 : r.Num("Angle", 0); }
        set
        {
            if (value == 0 && Rec(LocalAxes) == null) return;
            if (value == 0 && !(Rec(LocalAxes).Yes("AdvanceAxes"))) { Drop(LocalAxes); return; }
            var r = Rec(LocalAxes);
            if (r == null) { r = Need(LocalAxes); r.Set("Angle", "0").Set("AdvanceAxes", "No"); }
            r.SetNum("Angle", value);
        }
    }

    // releases: 12 flags (I: P V2 V3 T M2 M3, then J); partial: 12 spring values (0 = fully released where released)
    public bool[] Releases
    {
        get { var r = Rec(Release); var o = new bool[12]; if (r != null) for (int i = 0; i < 12; i++) o[i] = r.Yes(ReleaseDofs[i]); return o; }
        set
        {
            if (value == null || !value.Any(b => b)) { Drop(Release); Drop(Partial); return; }
            var r = Need(Release);
            for (int i = 0; i < 12; i++) r.SetYes(ReleaseDofs[i], value[i]);
            if (!r.Has("PartialFix")) r.Set("PartialFix", "No");
        }
    }
    public double[] PartialFixity
    {
        get { var r = Rec(Partial); var o = new double[12]; if (r != null) for (int i = 0; i < 12; i++) o[i] = r.Num(ReleaseDofs[i], 0); return o; }
        set
        {
            bool any = value != null && value.Any(v => v != 0);
            var g = Rec(Release);
            if (!any) { Drop(Partial); if (g != null) g.SetYes("PartialFix", false); return; }
            var r = Need(Partial);
            // SAP writes the partly fixed DOFs only (Frame=5   M3I=1111   M3J=2222)
            for (int i = 0; i < 12; i++) if (value[i] != 0 || r.Has(ReleaseDofs[i])) r.SetNum(ReleaseDofs[i], value[i]);
            Need(Release).SetYes("PartialFix", true);
        }
    }
    // the viewer's release mask (vault/format/v4-schema.md 4.3): bits 0-11 released, 12-23 partially fixed
    public uint ReleaseMask
    {
        get
        {
            var rel = Releases; var pf = PartialFixity;
            uint m = 0;
            for (int i = 0; i < 12; i++) { if (rel[i]) m |= 1u << i; if (rel[i] && pf[i] != 0) m |= 1u << (12 + i); }
            return m;
        }
    }

    public static readonly string[] CardinalNames = { "", "bottom left", "bottom center", "bottom right", "middle left", "middle center", "middle right",
                                                      "top left", "top center", "top right", "centroid", "shear center" };
    // SAP's text for a cardinal point: "8 (top center)"
    public static string CardinalText(int n) { return n >= 1 && n <= 11 ? n.ToString(CultureInfo.InvariantCulture) + " (" + CardinalNames[n] + ")" : "10 (centroid)"; }
    public int CardinalPoint
    {
        get
        {
            var r = Rec(Insertion);
            if (r == null) return 10;
            string s = (r.Get("CardinalPt") ?? "10").Trim();
            int n = 0, i = 0;
            while (i < s.Length && char.IsDigit(s[i])) { n = n * 10 + (s[i] - '0'); i++; }
            return n == 0 ? 10 : n;
        }
        set
        {
            if (value == CardinalPoint) return;
            Need(Insertion).Set("CardinalPt", CardinalText(value));
        }
    }

    // the end length offsets (rigid end zones; SAP's own names, probe round 1): Type=User with LengthI, LengthJ and the
    // rigid-zone factor. EndOffsetType is "" when the frame has no row, else SAP's Type (User, or one SAP works out)
    public string EndOffsetType { get { var r = Rec(EndOffsets); return r == null ? "" : r.Get("Type") ?? ""; } }
    public double[] EndLengths { get { var r = Rec(EndOffsets); return new[] { r == null ? 0 : r.Num("LengthI", 0), r == null ? 0 : r.Num("LengthJ", 0), r == null ? 0 : r.Num("RigidFactor", 0) }; } }
    // set them as user lengths; all zero on a frame with no row adds none
    public void SetEndLengths(double i, double j, double factor)
    {
        var r = Rec(EndOffsets);
        if (r == null && i == 0 && j == 0 && factor == 0) return;
        if (r != null && EndOffsetType.Equals("User", StringComparison.OrdinalIgnoreCase) && EndLengths.SequenceEqual(new[] { i, j, factor })) return;
        r = Need(EndOffsets);
        r.Set("Type", "User");
        r.SetNum("LengthI", i).SetNum("LengthJ", j).SetNum("RigidFactor", factor);
    }

    // the joint offsets of the insertion point (SAP 24.2's export, probe round 2): CoordSys, then JtOffsetXI YI ZI XJ YJ
    // ZJ after CardinalPt. With CoordSys=Local, X Y Z are local 1 2 3. None: no CoordSys and no JtOffset fields.
    public static readonly string[] JtOffsetKeys = { "JtOffsetXI", "JtOffsetYI", "JtOffsetZI", "JtOffsetXJ", "JtOffsetYJ", "JtOffsetZJ" };
    public string JointOffsetSys { get { var r = Rec(Insertion); return r == null || !JtOffsetKeys.Any(r.Has) ? "" : r.Get("CoordSys") ?? "GLOBAL"; } }
    public double[] JointOffsets { get { var r = Rec(Insertion); var o = new double[6]; for (int i = 0; i < 6; i++) o[i] = r == null ? 0 : r.Num(JtOffsetKeys[i], 0); return o; } }
    // set them in local 1 2 3 (CoordSys=Local); all zero removes them
    public void SetJointOffsets(double[] v)
    {
        var r = Rec(Insertion);
        bool none = v.All(q => q == 0);
        if (none)
        {
            if (r == null) return;
            if (JtOffsetKeys.Any(r.Has)) { r.Remove("CoordSys"); foreach (string k in JtOffsetKeys) r.Remove(k); }
            return;
        }
        if (JointOffsetSys.Equals("Local", StringComparison.OrdinalIgnoreCase) && JointOffsets.SequenceEqual(v)) return;
        r = Need(Insertion);
        // SAP's order: CardinalPt CoordSys JtOffsetXI ... JtOffsetZJ Mirror2 ...
        int at = r.F.FindIndex(f => string.Equals(f[0], "CardinalPt", StringComparison.OrdinalIgnoreCase)) + 1;
        if (!r.Has("CoordSys")) r.F.Insert(at, new[] { "CoordSys", "Local" }); else r.Set("CoordSys", "Local");
        at = r.F.FindIndex(f => string.Equals(f[0], "CoordSys", StringComparison.OrdinalIgnoreCase)) + 1;
        for (int i = 0; i < 6; i++)
        {
            if (r.Has(JtOffsetKeys[i])) r.SetNum(JtOffsetKeys[i], v[i]);
            else r.F.Insert(at, new[] { JtOffsetKeys[i], FrameModel.Fmt(v[i]) });
            at = r.F.FindIndex(f => string.Equals(f[0], JtOffsetKeys[i], StringComparison.OrdinalIgnoreCase)) + 1;
        }
    }

    // property modifiers AMod..WMod (1 = none)
    public double[] PropertyModifiers
    {
        get { var r = Rec(Modifiers); var o = new double[8]; for (int i = 0; i < 8; i++) o[i] = r == null ? 1 : r.Num(ModifierKeys[i], 1); return o; }
        set
        {
            if (value == null || value.All(v => v == 1)) { Drop(Modifiers); return; }
            var r = Need(Modifiers);
            for (int i = 0; i < 8; i++) r.SetNum(ModifierKeys[i], value[i]);
        }
    }
}

public sealed class FrameSection : FrameObj
{
    public const string General = "FRAME SECTION PROPERTIES 01 - GENERAL";
    public override string Kind { get { return "section"; } }
    public override string KeyField { get { return "SectionName"; } }
    public override string MainTable { get { return General; } }
    public string Shape { get { var r = Rec(General); return r == null ? "" : r.Get("Shape") ?? ""; } set { Need(General).Set("Shape", value); } }
    public string Material { get { var r = Rec(General); return r == null ? "" : r.Get("Material") ?? ""; } set { Need(General).Set("Material", value); } }
    public double Dim(string k) { var r = Rec(General); return r == null ? 0 : r.Num(k, 0); }
    public void SetDim(string k, double v) { Need(General).SetNum(k, v); }
}

public sealed class FrameMaterial : FrameObj
{
    public const string General = "MATERIAL PROPERTIES 01 - GENERAL", Mechanical = "MATERIAL PROPERTIES 02 - BASIC MECHANICAL PROPERTIES";
    public override string Kind { get { return "material"; } }
    public override string KeyField { get { return "Material"; } }
    public override string MainTable { get { return General; } }
    public const string Steel = "MATERIAL PROPERTIES 03A - STEEL DATA";
    public string Type { get { var r = Rec(General); return r == null ? "" : r.Get("Type") ?? ""; } set { if (Type != value) Need(General).Set("Type", value); } }
    public double Mech(string k) { var r = Rec(Mechanical); return r == null ? 0 : r.Num(k, 0); }
    public void SetMech(string k, double v) { Need(Mechanical).SetNum(k, v); }
    // steel data (Fy, Fu); NaN = no row
    public double SteelData(string k) { var r = Rec(Steel); return r == null ? double.NaN : r.Num(k, double.NaN); }
    public void SetSteelData(string k, double v) { Need(Steel).SetNum(k, v); }
}

public sealed class FrameLinkProp : FrameObj
{
    public const string General = "LINK PROPERTY DEFINITIONS 01 - GENERAL";
    public override string Kind { get { return "linkprop"; } }
    public override string KeyField { get { return "Link"; } }
    public override string MainTable { get { return General; } }
    public string LinkType { get { var r = Rec(General); return r == null ? "" : r.Get("LinkType") ?? ""; } set { if (LinkType != value) Need(General).Set("LinkType", value); } }
    // the per-DOF rows: every row it owns outside its general table that names a DOF, a multilinear curve's further
    // points aside
    public IEnumerable<FrameRec> DofRows { get { foreach (var r in Recs) if (r.Has("DOF") && !string.Equals(r.Table, General, StringComparison.OrdinalIgnoreCase) && !IsPointRow(r)) yield return r; } }
    public FrameRec DofRow(string dof) { foreach (var r in DofRows) if (string.Equals(r.Get("DOF"), dof, StringComparison.OrdinalIgnoreCase)) return r; return null; }

    // a multilinear curve is one row per point (SAP 24.2's export, probe round 2): Link DOF Point Force Displ; the first
    // point is on the DOF row itself (Link DOF Fixed NonLinear TransKE TransCE DJ Point Force Displ)
    public static bool IsPointRow(FrameRec r) { return r.Has("Point") && !r.Has("Fixed"); }
    IEnumerable<FrameRec> PointRows(string dof)
    {
        foreach (var r in Recs)
            if (r.Has("Point") && string.Equals(r.Get("DOF"), dof, StringComparison.OrdinalIgnoreCase) && !string.Equals(r.Table, General, StringComparison.OrdinalIgnoreCase)) yield return r;
    }
    // a DOF's curve points, {Force, Displ} as written, in order
    public List<string[]> Points(string dof) { return PointRows(dof).Select(r => new[] { r.Get("Force") ?? "0", r.Get("Displ") ?? "0" }).ToList(); }
    // set a DOF's curve ({force, displacement} each): points the same stay as written, further ones are rows after the
    // DOF's last, fewer drop the rows at the end
    public void SetPoints(string dof, IList<double[]> pts)
    {
        var d = DofRow(dof);
        if (d == null) throw new InvalidOperationException("FrameLinkProp: " + Label + " has no " + dof + " row for its points");
        var have = PointRows(dof).ToList();
        if (have.Count > 0 && have[0] != d) throw new InvalidOperationException("FrameLinkProp: " + Label + " " + dof + ": the first point is not on the DOF row");
        for (int i = 0; i < Math.Min(have.Count, pts.Count); i++) have[i].SetNum("Force", pts[i][0]).SetNum("Displ", pts[i][1]);
        for (int i = pts.Count; i < have.Count; i++)
        {
            if (i == 0) { d.Remove("Point"); d.Remove("Force"); d.Remove("Displ"); }
            else Recs.Remove(have[i]);
        }
        var last = have.Count > 0 && pts.Count >= have.Count ? have[have.Count - 1] : d;
        for (int i = have.Count; i < pts.Count; i++)
        {
            string n = (i + 1).ToString(CultureInfo.InvariantCulture);
            if (i == 0) { d.Set("Point", n).Set("Force", FrameModel.Fmt(pts[i][0])).Set("Displ", FrameModel.Fmt(pts[i][1])); continue; }
            var r = new FrameRec(d.Table).Set("Link", Label).Set("DOF", d.Get("DOF")).Set("Point", n).Set("Force", FrameModel.Fmt(pts[i][0])).Set("Displ", FrameModel.Fmt(pts[i][1]));
            Recs.Insert(Recs.IndexOf(last) + 1, r);
            last = r;
        }
    }

    // the table a link type's DOF rows go in: SAP 24's numbering, every one confirmed by SAP 24.2's export (probe rounds
    // 1 and 2, 2026-11-05; vault/format/sap-s2k-tables.md) but multilinear plastic's (11)
    public static string DofTable(string linkType)
    {
        string t = (linkType ?? "").ToLowerInvariant();
        if (t == "gap") return "LINK PROPERTY DEFINITIONS 05 - GAP";
        if (t == "hook") return "LINK PROPERTY DEFINITIONS 06 - HOOK";
        if (t.Contains("damper")) return "LINK PROPERTY DEFINITIONS 04 - DAMPER";
        if (t.Contains("friction") || t.Contains("sliding")) return "LINK PROPERTY DEFINITIONS 08 - SLIDING ISOLATOR";
        if (t.Contains("multilinear")) return t.Contains("plastic") ? "LINK PROPERTY DEFINITIONS 11 - MULTILINEAR PLASTIC" : "LINK PROPERTY DEFINITIONS 03 - MULTILINEAR";
        return "LINK PROPERTY DEFINITIONS 02 - LINEAR";
    }
    public static bool IsMultilinear(string linkType) { return (linkType ?? "").IndexOf("multilinear", StringComparison.OrdinalIgnoreCase) >= 0; }

    // the field a DOF row's opening is in: SAP writes TransOpen / RotOpen; older files have Open (SAP ignores it)
    public static string OpenField(FrameRec d)
    {
        bool rot = (d.Get("DOF") ?? "").StartsWith("R", StringComparison.OrdinalIgnoreCase);
        string mine = rot ? "RotOpen" : "TransOpen";
        return !d.Has(mine) && d.Has("Open") ? "Open" : mine;
    }

    // a new DOF row in its type's table, in SAP's field order: Link, DOF, Fixed[, NonLinear], KE, CE (trans or rot)
    public FrameRec AddDofRow(string dof)
    {
        bool rot = dof.StartsWith("R", StringComparison.OrdinalIgnoreCase), lin = DofTable(LinkType).EndsWith("LINEAR") && !DofTable(LinkType).Contains("MULTI");
        var r = new FrameRec(DofTable(LinkType)).Set("Link", Label).Set("DOF", dof).Set("Fixed", "No");
        if (!lin) r.Set("NonLinear", "No");
        r.Set(rot ? "RotKE" : "TransKE", "0").Set(rot ? "RotCE" : "TransCE", "0");
        Recs.Add(r);
        return r;
    }
}

public sealed class FrameLink : FrameObj
{
    public const string Conn = "CONNECTIVITY - LINK", PropAssign = "LINK PROPERTY ASSIGNMENTS", LocalAxes = "LINK LOCAL AXES ASSIGNMENTS 1 - TYPICAL";
    public override string Kind { get { return "link"; } }
    public override string KeyField { get { return "Link"; } }
    public override string MainTable { get { return Conn; } }
    public string JointI { get { var r = Rec(Conn); return r == null ? "" : r.Get("JointI") ?? ""; } }
    // the second joint; "" for a one-joint (grounded) link
    public string JointJ { get { var r = Rec(Conn); string j = r == null ? null : r.Get("JointJ"); return j == null || j == JointI ? "" : j; } }
    public string Property { get { var r = Rec(PropAssign); return r == null ? "" : r.Get("LinkProp") ?? ""; } set { if (Property != value) Need(PropAssign).Set("LinkProp", value); } }
    public double Angle
    {
        get { var r = Rec(LocalAxes); return r == null ? 0 : r.Num("Angle", 0); }
        set
        {
            var r = Rec(LocalAxes);
            if (value == 0 && (r == null || !r.Yes("AdvanceAxes"))) { Drop(LocalAxes); return; }
            if (r == null) r = Need(LocalAxes);
            r.SetNum("Angle", value);
        }
    }
    // its joints; j "" = a one-joint (grounded) link, written as JointJ = JointI (the probe confirms the form)
    public void SetJoints(string i, string j)
    {
        if (JointI == i && JointJ == j) return;
        var r = Need(Conn);
        r.Set("JointI", i);
        r.Set("JointJ", j == "" ? i : j);
    }
}

public sealed class FrameGroup : FrameObj
{
    public const string Defs = "GROUPS 1 - DEFINITIONS", Assign = "GROUPS 2 - ASSIGNMENTS";
    public override string Kind { get { return "group"; } }
    public override string KeyField { get { return "GroupName"; } }
    public override string MainTable { get { return Defs; } }
    // its members: {ObjectType, ObjectLabel}
    public IEnumerable<string[]> Members { get { foreach (var r in RecsOf(Assign)) yield return new[] { r.Get("ObjectType") ?? "", r.Get("ObjectLabel") ?? "" }; } }
    // set the members ({ObjectType, ObjectLabel}): rows already there stay as written and in place, the others go,
    // new ones follow
    public void SetMembers(IList<string[]> want)
    {
        Func<string, string, string> key = (t, l) => (t ?? "").ToLowerInvariant() + "|" + (l ?? "");
        var keep = new HashSet<string>(want.Select(w => key(w[0], w[1])));
        var have = new HashSet<string>();
        Recs.RemoveAll(r => string.Equals(r.Table, Assign, StringComparison.OrdinalIgnoreCase) && !keep.Contains(key(r.Get("ObjectType"), r.Get("ObjectLabel"))));
        foreach (var r in RecsOf(Assign)) have.Add(key(r.Get("ObjectType"), r.Get("ObjectLabel")));
        foreach (var w in want)
            if (have.Add(key(w[0], w[1]))) Recs.Add(new FrameRec(Assign).Set("GroupName", Label).Set("ObjectType", w[0]).Set("ObjectLabel", w[1]));
    }
}

public sealed class FramePattern : FrameObj
{
    public const string Defs = "LOAD PATTERN DEFINITIONS";
    public override string Kind { get { return "pattern"; } }
    public override string KeyField { get { return "LoadPat"; } }
    public override string MainTable { get { return Defs; } }
    public string DesignType { get { var r = Rec(Defs); return r == null ? "" : r.Get("DesignType") ?? ""; } set { if (DesignType != value) Need(Defs).Set("DesignType", value); } }
    public double SelfWeight { get { var r = Rec(Defs); return r == null ? 0 : r.Num("SelfWtMult", 0); } set { Need(Defs).SetNum("SelfWtMult", value); } }
}

// one load row: a joint force, a ground displacement, a frame point / distributed / temperature load (its table)
public sealed class FrameLoad : FrameObj
{
    public const string JointForce = "JOINT LOADS - FORCE", GroundDisp = "JOINT LOADS - GROUND DISPLACEMENT", Point = "FRAME LOADS - POINT",
                        Distributed = "FRAME LOADS - DISTRIBUTED", Temperature = "FRAME LOADS - TEMPERATURE";
    public string Table = "";
    public override string Kind { get { return "load"; } }
    public override string KeyField { get { return Table.StartsWith("JOINT", StringComparison.OrdinalIgnoreCase) ? "Joint" : "Frame"; } }
    public override string MainTable { get { return Table; } }
    public FrameRec Row { get { return Recs.Count > 0 ? Recs[0] : null; } }
    public string Pattern { get { return Row == null ? "" : Row.Get("LoadPat") ?? ""; } }
    public string Target { get { return Row == null ? "" : Row.Get(KeyField) ?? ""; } }
}

public sealed class FrameCase : FrameObj
{
    public const string Defs = "LOAD CASE DEFINITIONS", StaticLoads = "CASE - STATIC 1 - LOAD ASSIGNMENTS", NonlinParams = "CASE - STATIC 4 - NONLINEAR PARAMETERS";
    public override string Kind { get { return "case"; } }
    // a nonlinear case's geometric nonlinearity: None, P-Delta, or P-Delta plus Large Displacements (SAP's names)
    public string GeoNonLin { get { var r = Rec(NonlinParams); return r == null ? "None" : r.Get("GeoNonLin") ?? "None"; } set { if (GeoNonLin != value) Need(NonlinParams).Set("GeoNonLin", value); } }
    public override string KeyField { get { return "Case"; } }
    public override string MainTable { get { return Defs; } }
    public string Type { get { var r = Rec(Defs); return r == null ? "" : r.Get("Type") ?? ""; } set { if (Type != value) Need(Defs).Set("Type", value); } }
    public string Initial { get { var r = Rec(Defs); return r == null ? "" : r.Get("InitialCond") ?? ""; } set { if (Initial != value) Need(Defs).Set("InitialCond", value); } }
    // its static loads: {LoadType, LoadName, LoadSF}
    public IEnumerable<string[]> Loads { get { foreach (var r in RecsOf(StaticLoads)) yield return new[] { r.Get("LoadType") ?? "", r.Get("LoadName") ?? "", r.Get("LoadSF") ?? "1" }; } }
    // set them (type, name, factor): the k-th row is rewritten only where it differs, extra rows go, new rows follow
    public void SetLoads(IList<object[]> want)
    {
        var rows = RecsOf(StaticLoads).ToList();
        for (int k = 0; k < want.Count; k++)
        {
            var r = k < rows.Count ? rows[k] : null;
            if (r == null) { r = new FrameRec(StaticLoads).Set("Case", Label).Set("LoadType", "").Set("LoadName", "").Set("LoadSF", "1"); Recs.Add(r); }
            if (r.Get("LoadType") != (string)want[k][0]) r.Set("LoadType", (string)want[k][0]);
            if (r.Get("LoadName") != (string)want[k][1]) r.Set("LoadName", (string)want[k][1]);
            r.SetNum("LoadSF", (double)want[k][2]);
        }
        for (int k = want.Count; k < rows.Count; k++) Recs.Remove(rows[k]);
    }
}

public sealed class FrameCombo : FrameObj
{
    public const string Defs = "COMBINATION DEFINITIONS";
    public override string Kind { get { return "combo"; } }
    public override string KeyField { get { return "ComboName"; } }
    public override string MainTable { get { return Defs; } }
    public string Type { get { var r = Rec(Defs); return r == null ? "" : r.Get("ComboType") ?? ""; } set { if (Type != value) Need(Defs).Set("ComboType", value); } }
    // its items: {CaseType, CaseName, ScaleFactor}
    public IEnumerable<string[]> Items { get { foreach (var r in RecsOf(Defs)) yield return new[] { r.Get("CaseType") ?? "", r.Get("CaseName") ?? "", r.Get("ScaleFactor") ?? "1" }; } }
    // set them (case type, name, factor) as SetLoads does; the first row carries the combination's own fields
    public void SetItems(IList<object[]> want)
    {
        var rows = RecsOf(Defs).ToList();
        for (int k = 0; k < want.Count; k++)
        {
            var r = k < rows.Count ? rows[k] : null;
            if (r == null)
            {
                r = new FrameRec(Defs).Set("ComboName", Label);
                if (k == 0) r.Set("ComboType", "Linear Add").Set("AutoDesign", "No");
                r.Set("CaseType", "").Set("CaseName", "").Set("ScaleFactor", "1");
                if (k == 0) r.Set("SteelDesign", "None").Set("ConcDesign", "None").Set("AlumDesign", "None").Set("ColdDesign", "None");
                Recs.Add(r);
            }
            if (r.Get("CaseType") != (string)want[k][0]) r.Set("CaseType", (string)want[k][0]);
            if (r.Get("CaseName") != (string)want[k][1]) r.Set("CaseName", (string)want[k][1]);
            r.SetNum("ScaleFactor", (double)want[k][2]);
        }
        for (int k = Math.Max(1, want.Count); k < rows.Count; k++) Recs.Remove(rows[k]);
    }
}

public sealed class FrameModel
{
    public S2kDoc Source;                                       // the file it was read from (unedited); a new model gets a minimal one
    public FrameRec Control;                                    // PROGRAM CONTROL
    public readonly List<FrameJoint> Joints = new List<FrameJoint>();
    public readonly List<FrameMember> Frames = new List<FrameMember>();
    public readonly List<FrameSection> Sections = new List<FrameSection>();
    public readonly List<FrameMaterial> Materials = new List<FrameMaterial>();
    public readonly List<FrameLinkProp> LinkProps = new List<FrameLinkProp>();
    public readonly List<FrameLink> Links = new List<FrameLink>();
    public readonly List<FrameGroup> Groups = new List<FrameGroup>();
    public readonly List<FramePattern> Patterns = new List<FramePattern>();
    public readonly List<FrameLoad> Loads = new List<FrameLoad>();
    public readonly List<FrameCase> Cases = new List<FrameCase>();
    public readonly List<FrameCombo> Combos = new List<FrameCombo>();
    public readonly List<string> Warnings = new List<string>();
    public int NextUid = 1;

    public string Units { get { return Control == null ? "" : Control.Get("CurrUnits") ?? ""; } }
    public string Version { get { return Control == null ? "" : Control.Get("Version") ?? ""; } }

    public IEnumerable<FrameObj> All()
    {
        foreach (var o in Joints) yield return o;
        foreach (var o in Frames) yield return o;
        foreach (var o in Sections) yield return o;
        foreach (var o in Materials) yield return o;
        foreach (var o in LinkProps) yield return o;
        foreach (var o in Links) yield return o;
        foreach (var o in Groups) yield return o;
        foreach (var o in Patterns) yield return o;
        foreach (var o in Loads) yield return o;
        foreach (var o in Cases) yield return o;
        foreach (var o in Combos) yield return o;
    }

    public FrameSection SectionNamed(string n) { foreach (var s in Sections) if (string.Equals(s.Label, n, StringComparison.OrdinalIgnoreCase)) return s; return null; }
    public FrameJoint JointNamed(string n) { foreach (var j in Joints) if (j.Label == n) return j; return null; }

    // ---------------- numbers: the shortest text that reads back as the same double (G15 when it does, else G17)
    public static string Fmt(double v)
    {
        if (v == 0) return "0";
        string s = v.ToString("G15", CultureInfo.InvariantCulture);
        double back;
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out back) || back != v) s = v.ToString("G17", CultureInfo.InvariantCulture);
        return s;
    }

    // ---------------- coordinate systems (COORDINATE SYSTEMS, cartesian and cylindrical joints)
    Dictionary<string, double[]> _systems;                      // name -> {ox, oy, oz, R row-major 9}

    internal double[] GlobalOf(FrameRec r)
    {
        if (r == null) return new double[3];
        if (r.Has("GlobalX") && r.Has("GlobalY") && r.Has("GlobalZ")) return new[] { r.Num("GlobalX", 0), r.Num("GlobalY", 0), r.Num("GlobalZ", 0) };
        double a = r.Num("XorR", 0), b = r.Num("Y", 0), c = r.Num("Z", 0);
        string ct = r.Get("CoordType") ?? "Cartesian";
        double lx = a, ly = b, lz = c;
        // cylindrical: XorR = R, T = the angle (SAP's field; an older guess put it in Y), Z
        if (ct.StartsWith("Cyl", StringComparison.OrdinalIgnoreCase)) { double t = (r.Has("T") ? r.Num("T", 0) : b) * Math.PI / 180; lx = a * Math.Cos(t); ly = a * Math.Sin(t); }
        string cs = r.Get("CoordSys") ?? "GLOBAL";
        if (cs.StartsWith("GLOBAL", StringComparison.OrdinalIgnoreCase)) return new[] { lx, ly, lz };
        if (_systems == null) _systems = Systems(Source);
        double[] s;
        if (!_systems.TryGetValue(cs, out s)) return new[] { lx, ly, lz };
        return new[] { s[0] + s[3] * lx + s[4] * ly + s[5] * lz, s[1] + s[6] * lx + s[7] * ly + s[8] * lz, s[2] + s[9] * lx + s[10] * ly + s[11] * lz };
    }

    static Dictionary<string, double[]> Systems(IS2kTables doc)
    {
        var o = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
        if (doc == null) return o;
        Func<Dictionary<string, string>, string, double> num = (row, k) => { string s; double v; return row.TryGetValue(k, out s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0; };
        foreach (var row in doc.GetTable("COORDINATE SYSTEMS"))
        {
            string name;
            if (!row.TryGetValue("Name", out name)) continue;
            double az = num(row, "AboutZ") * Math.PI / 180, ay = num(row, "AboutY") * Math.PI / 180, ax = num(row, "AboutX") * Math.PI / 180;
            double cz = Math.Cos(az), sz = Math.Sin(az), cy = Math.Cos(ay), sy = Math.Sin(ay), cx = Math.Cos(ax), sx = Math.Sin(ax);
            // R = Rz * Ry * Rx (SAP's AboutZ, AboutY, AboutX)
            double[,] rz = { { cz, -sz, 0 }, { sz, cz, 0 }, { 0, 0, 1 } }, ry = { { cy, 0, sy }, { 0, 1, 0 }, { -sy, 0, cy } }, rx = { { 1, 0, 0 }, { 0, cx, -sx }, { 0, sx, cx } };
            var m = Mul(Mul(rz, ry), rx);
            o[name] = new[] { num(row, "X"), num(row, "Y"), num(row, "Z"), m[0, 0], m[0, 1], m[0, 2], m[1, 0], m[1, 1], m[1, 2], m[2, 0], m[2, 1], m[2, 2] };
        }
        return o;
    }
    static double[,] Mul(double[,] a, double[,] b)
    {
        var c = new double[3, 3];
        for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) for (int k = 0; k < 3; k++) c[i, j] += a[i, k] * b[k, j];
        return c;
    }

    // ---------------- making objects (new ones: the next uid, a main row keyed by the label)
    public T Add<T>(T o, string label) where T : FrameObj
    {
        o.Uid = NextUid++;
        o.Recs.Add(NewRec(o.MainTable, o.KeyField, label));
        Attach(o);
        return o;
    }

    // a new row: the key, then the table's fields at SAP's defaults, in the order SAP writes them (those SAP's own
    // exports and the proven Pluto writers show; tables not listed get the key only)
    public static FrameRec NewRec(string table, string keyField, string label)
    {
        var r = new FrameRec(table).Set(keyField, label);
        string[] t;
        if (Templates.TryGetValue(table, out t))
            for (int i = 0; i + 1 < t.Length; i += 2) if (!t[i].Equals(keyField, StringComparison.OrdinalIgnoreCase)) r.Set(t[i], t[i + 1]);
        return r;
    }

    static readonly Dictionary<string, string[]> Templates = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        { FrameJoint.Coords, new[] { "CoordSys", "GLOBAL", "CoordType", "Cartesian", "XorR", "0", "Y", "0", "Z", "0" } },
        { FrameJoint.Restraints, new[] { "U1", "No", "U2", "No", "U3", "No", "R1", "No", "R2", "No", "R3", "No" } },
        { FrameJoint.Springs, new[] { "CoordSys", "GLOBAL", "U1", "0", "U2", "0", "U3", "0", "R1", "0", "R2", "0", "R3", "0" } },
        { FrameJoint.LocalAxes, new[] { "AngleA", "0", "AngleB", "0", "AngleC", "0", "AdvanceAxes", "No" } },
        { FrameMember.Conn, new[] { "JointI", "", "JointJ", "", "IsCurved", "No" } },
        { FrameMember.SectionAssign, new[] { "SectionType", "", "AutoSelect", "N.A.", "AnalSect", "", "DesignSect", "", "MatProp", "Default" } },
        { FrameMember.LocalAxes, new[] { "Angle", "0", "AdvanceAxes", "No" } },
        { FrameMember.Release, new[] { "PI", "No", "V2I", "No", "V3I", "No", "TI", "No", "M2I", "No", "M3I", "No", "PJ", "No", "V2J", "No", "V3J", "No", "TJ", "No", "M2J", "No", "M3J", "No", "PartialFix", "No" } },
        { FrameMember.Partial, new string[0] },
        { FrameMember.Insertion, new[] { "CardinalPt", "10 (centroid)", "Mirror2", "No", "Mirror3", "No", "Transform", "Yes" } },
        { FrameMember.Modifiers, new[] { "AMod", "1", "AS2Mod", "1", "AS3Mod", "1", "JMod", "1", "I22Mod", "1", "I33Mod", "1", "MassMod", "1", "WeightMod", "1" } },
        { FrameMember.EndOffsets, new[] { "Type", "User", "LengthI", "0", "LengthJ", "0", "RigidFactor", "0" } },
        { FrameCase.NonlinParams, new[] { "GeoNonLin", "None" } },
        { FrameSection.General, new[] { "Material", "", "Shape", "", "Color", "Green" } },
        { FrameMaterial.General, new[] { "Type", "Steel", "SymType", "Isotropic", "TempDepend", "No", "Color", "Blue" } },
        { FrameMaterial.Mechanical, new[] { "UnitWeight", "0", "UnitMass", "0", "E1", "0", "G12", "0", "U12", "0", "A1", "0" } },
        { FrameLinkProp.General, new[] { "LinkType", "Linear", "Mass", "0", "Weight", "0", "RotInert1", "0", "RotInert2", "0", "RotInert3", "0", "DefLength", "1", "DefArea", "1",
                                         "PDM2I", "0", "PDM2J", "0", "PDM3I", "0", "PDM3J", "0", "Color", "Magenta" } },
        { FrameLink.Conn, new[] { "JointI", "", "JointJ", "" } },
        { FrameLink.PropAssign, new[] { "LinkProp", "", "LinkFDProp", "None" } },
        { FrameLink.LocalAxes, new[] { "Angle", "0", "AdvanceAxes", "No" } },
        { FrameGroup.Defs, new[] { "Selection", "Yes", "SectionCut", "Yes", "Steel", "Yes", "Concrete", "Yes", "Aluminum", "Yes", "ColdFormed", "Yes", "Stage", "Yes", "Bridge", "Yes",
                                   "AutoSeismic", "No", "AutoWind", "No", "SelDesSteel", "No", "SelDesAlum", "No", "SelDesCold", "No", "MassWeight", "Yes", "Color", "Green" } },
        { FramePattern.Defs, new[] { "DesignType", "Other", "SelfWtMult", "0" } },
        { FrameCase.Defs, new[] { "Type", "LinStatic", "InitialCond", "Zero" } },
    };

    internal void Attach(FrameObj o)
    {
        var j = o as FrameJoint; if (j != null) { j.M = this; Joints.Add(j); return; }
        var f = o as FrameMember; if (f != null) { f.M = this; Frames.Add(f); return; }
        var s = o as FrameSection; if (s != null) { Sections.Add(s); return; }
        var mt = o as FrameMaterial; if (mt != null) { Materials.Add(mt); return; }
        var lp = o as FrameLinkProp; if (lp != null) { LinkProps.Add(lp); return; }
        var l = o as FrameLink; if (l != null) { Links.Add(l); return; }
        var g = o as FrameGroup; if (g != null) { Groups.Add(g); return; }
        var p = o as FramePattern; if (p != null) { Patterns.Add(p); return; }
        var ld = o as FrameLoad; if (ld != null) { Loads.Add(ld); return; }
        var c = o as FrameCase; if (c != null) { Cases.Add(c); return; }
        var cb = o as FrameCombo; if (cb != null) { Combos.Add(cb); return; }
        throw new ArgumentException("FrameModel: unknown object " + o.GetType().Name);
    }

    public bool Remove(FrameObj o)
    {
        var j = o as FrameJoint; if (j != null) return Joints.Remove(j);
        var f = o as FrameMember; if (f != null) return Frames.Remove(f);
        var s = o as FrameSection; if (s != null) return Sections.Remove(s);
        var mt = o as FrameMaterial; if (mt != null) return Materials.Remove(mt);
        var lp = o as FrameLinkProp; if (lp != null) return LinkProps.Remove(lp);
        var l = o as FrameLink; if (l != null) return Links.Remove(l);
        var g = o as FrameGroup; if (g != null) return Groups.Remove(g);
        var p = o as FramePattern; if (p != null) return Patterns.Remove(p);
        var ld = o as FrameLoad; if (ld != null) return Loads.Remove(ld);
        var c = o as FrameCase; if (c != null) return Cases.Remove(c);
        var cb = o as FrameCombo; if (cb != null) return Combos.Remove(cb);
        return false;
    }

    public FrameLoad AddLoad(string table)
    {
        var o = new FrameLoad { Table = table, Uid = NextUid++ };
        o.Recs.Add(new FrameRec(table));
        Loads.Add(o);
        return o;
    }
}
