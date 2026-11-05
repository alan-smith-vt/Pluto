using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

// FrameWorkbook  --  a SAP2000 frame model as an Excel workbook you edit (2026-11-05), the frame counterpart of the
// CAESAR pipe workbook, built on the shared kit (vault/arms/workbook-kit.md). Written from a model (an .s2k export, a
// .frame.json, a new one) and read back into it by Build-Frame.ps1 -In x.xlsx:
//   Joints        X Y Z (global; formulas welcome: a pipe run is =C12+24), restraints ("fixed", "pin", or DOFs
//                 "U1 U2 U3 R3"), joint local axes
//   Frames        between two joints on Joints (every element, steel or pipe: SAP is joint-based), the length (live),
//                 section, Kind and Line (the PL_KIND_ / PL_LINE_ groups), a release preset, local axis angle,
//                 insertion point, a modifiers preset
//   Sections, Materials, Releases (named presets of the 12 end DOFs: yes or a partial-fixity stiffness), Modifiers
//   Links, Link props (one row per DOF), Springs (per joint)
//   Load patterns, Joint loads, Ground displacements, Point loads, Distributed loads, Temperature (a load row names
//                 its frames by a selector: FrameSelect), Load cases (one row per load), Combinations (one row per item)
//   Groups        SAP's groups, one row per member (the PL_ groups are the Kind / Line columns)
//   Carried       read-only: the tables the workbook does not show, which ride along unchanged
// Rows tie to the model by their Id (J12, F7, S3 ... the object's uid); the model the workbook was written from is
// stored whole in the hidden _pluto sheet, so a value not edited is written back exactly (SetNum keeps its text), and
// everything the sheets do not show is carried. A blank Id is a new object; a row gone deletes its object (refused,
// naming the rows, while something still uses it); a new label renames it everywhere, and references still naming
// the old label follow. Headers show the model's units; a header in another unit converts its column (FrameUnits).
// C# 5 (prefix Frame).

public sealed class FrameWorkbookResult
{
    public string Path = "", SourceName = "", BaseHash = "", ModelHash = "";
    public FrameModel Model, Base;
    public readonly List<string> Warnings = new List<string>();
    public string Summary()
    {
        var m = Model;
        return string.Format("{0} joints, {1} frames, {2} sections, {3} links, {4} loads, {5} cases, {6} combinations{7}", m.Joints.Count, m.Frames.Count, m.Sections.Count,
                             m.Links.Count, m.Loads.Count, m.Cases.Count, m.Combos.Count, Warnings.Count > 0 ? "; " + Warnings.Count + " warning(s)" : "");
    }
}

// the launchers of a frame workbook (Build-Frame.ps1)
public sealed class FrameWorkbookRun : WbRun
{
    public string PreviewCmd { get { return Name + " - preview.cmd"; } }
    public string S2kCmd { get { return Name + " - s2k.cmd"; } }
    public string OpenCmd { get { return Name + " - open in SAP.cmd"; } }

    public static FrameWorkbookRun For(string xlsxPath, string repoRoot, string sourcePath)
    {
        var r = new FrameWorkbookRun();
        r.Place(xlsxPath, repoRoot, Path.Combine("scripts", "frame", "Build-Frame.ps1"), sourcePath, ".s2k");
        return r;
    }

    public List<string> WriteLaunchers()
    {
        var done = new List<string>();
        done.Add(WriteLauncher(PreviewCmd, LauncherText("Build-Frame.ps1", "the live viewer preview", "live preview",
            new[] { "Every save of " + Echo(Name) + ".xlsx rebuilds " + Echo(Out) + ".bin and " + Echo(Out) + ".features.json beside it.",
                    "Open them in Pluto's viewer (Choose files..., both together) and tick Auto-reload. Close this window to stop." }, "-Preview -Watch", new string[0])));
        done.Add(WriteLauncher(S2kCmd, LauncherText("Build-Frame.ps1", "the SAP2000 .s2k", "write the .s2k",
            new[] { "Writing " + Echo(Out) + ".s2k (and the viewer preview) from " + Echo(Name) + ".xlsx ..." }, "-S2k -Preview",
            new[] { "In SAP2000: File, Import, SAP2000 .s2k, then " + Echo(Out) + ".s2k" })));
        done.Add(WriteLauncher(OpenCmd, LauncherText("Build-Frame.ps1", "the model opened in SAP2000", "open in SAP",
            new[] { "Writing " + Echo(Out) + ".s2k from " + Echo(Name) + ".xlsx and importing it in a SAP2000 of its own (stamped for its version, saved as " + Echo(Out) + ".sdb) ..." },
            "-S2k -Preview -Open", new string[0])));
        return done;
    }
}

public static class FrameWorkbook
{
    public const string Format = "pluto-frame-workbook";
    public const int Version = 1;
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // the input sheets, in order (also the formula evaluator's and refresh's)
    public const string SJoints = "Joints", SFrames = "Frames", SSections = "Sections", SMaterials = "Materials", SReleases = "Releases", SModifiers = "Modifiers",
                        SLinks = "Links", SLinkProps = "Link props", SSprings = "Springs", SPatterns = "Load patterns", SJointLoads = "Joint loads",
                        SGround = "Ground displacements", SPoint = "Point loads", SDist = "Distributed loads", STemp = "Temperature", SCases = "Load cases",
                        SCombos = "Combinations", SGroups = "Groups";
    static readonly string[] Inputs = { "Settings", SJoints, SFrames, SSections, SMaterials, SReleases, SModifiers, SLinks, SLinkProps, SSprings, SPatterns,
                                        SJointLoads, SGround, SPoint, SDist, STemp, SCases, SCombos, SGroups };

    static readonly string[][] Groups =
    {
        new[] { "key", "37474F", "Ids and labels: what ties a row to the model" },
        new[] { "geom", "1565C0", "Geometry: coordinates, joints, local axes" },
        new[] { "prop", "00796B", "Properties: section, material, releases, modifiers" },
        new[] { "bc", "B45309", "Supports and links: restraints, springs, link properties" },
        new[] { "load", "9A3412", "Loads, cases and combinations" },
        new[] { "text", "546E7A", "Names, kinds and lines" },
        new[] { "ro", "9E9E9E", "Read-only: rebuilt on every export, never read" },
    };

    // ======================================================================== ids

    static string Prefix(FrameObj o)
    {
        if (o is FrameJoint) return "J";
        if (o is FrameMember) return "F";
        if (o is FrameSection) return "S";
        if (o is FrameMaterial) return "M";
        if (o is FrameLinkProp) return "LP";
        if (o is FrameLink) return "K";
        if (o is FramePattern) return "PAT";
        if (o is FrameCase) return "CASE";
        if (o is FrameCombo) return "CMB";
        var l = o as FrameLoad;
        if (l != null) return LoadPrefix(l.Table);
        return "G";
    }
    static string LoadPrefix(string table)
    {
        if (table == FrameLoad.JointForce) return "JL";
        if (table == FrameLoad.GroundDisp) return "GD";
        if (table == FrameLoad.Point) return "PL";
        if (table == FrameLoad.Distributed) return "DL";
        return "TL";
    }
    static string Id(FrameObj o) { return Prefix(o) + o.Uid.ToString(Inv); }

    // ======================================================================== model helpers

    // a release pattern: 12 flags and 12 partial values -> its key
    static string RelKey(bool[] r, double[] p)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 12; i++) sb.Append(r[i] ? (p[i] != 0 ? "~" + FrameModel.Fmt(p[i]) : "y") : "-").Append('|');
        return sb.ToString();
    }

    static readonly string[] RelHeads = { "P I", "V2 I", "V3 I", "T I", "M2 I", "M3 I", "P J", "V2 J", "V3 J", "T J", "M2 J", "M3 J" };
    // built-in release presets: name, released DOF indexes
    static readonly object[][] RelBuiltIn =
    {
        new object[] { "FIXED", new int[0] }, new object[] { "PIN-PIN", new[] { 4, 5, 10, 11 } }, new object[] { "PIN-I", new[] { 4, 5 } },
        new object[] { "PIN-J", new[] { 10, 11 } }, new object[] { "SLIP-J", new[] { 6 } },
    };
    static readonly string[] ModHeads = { "A", "A2", "A3", "J", "I2", "I3", "Mass", "Weight" };

    sealed class Preset { public string Name; public bool[] Rel = new bool[12]; public double[] Val = new double[12]; }

    // "I M3~ J M3~": what a preset releases at each end (~ partly fixed)
    static string PresetText(bool[] r, double[] p)
    {
        string[] n = { "P", "V2", "V3", "T", "M2", "M3" };
        Func<int, string> end = o => string.Join(" ", Enumerable.Range(0, 6).Where(i => r[o + i]).Select(i => n[i] + (p[o + i] != 0 ? "~" : "")).ToArray());
        string a = end(0), b = end(6);
        return (a.Length > 0 ? "I " + a : "") + (a.Length > 0 && b.Length > 0 ? " " : "") + (b.Length > 0 ? "J " + b : "");
    }

    static string RestraintText(bool[] r)
    {
        if (!r.Any(b => b)) return "";
        if (r.All(b => b)) return "fixed";
        if (r[0] && r[1] && r[2] && !r[3] && !r[4] && !r[5]) return "pin";
        return string.Join(" ", Enumerable.Range(0, 6).Where(i => r[i]).Select(i => FrameJoint.Dofs[i]).ToArray());
    }

    static bool IsIntLabel(string s)
    {
        if (string.IsNullOrEmpty(s) || s.Length > 9 || s[0] == '0') return false;
        foreach (char c in s) if (c < '0' || c > '9') return false;
        return true;
    }
    // a label in a cell: a whole number as a number (so typed numbers match it), anything else as text
    static void Label(XlsxBookSheet s, int r, int c, string label, int style)
    {
        if (IsIntLabel(label)) s.Set(r, c, double.Parse(label, Inv), style); else s.Set(r, c, label ?? "", style);
    }
    static void Num(XlsxBookSheet s, int r, int c, string text)
    {
        double v;
        if (text != null && double.TryParse(text, NumberStyles.Float, Inv, out v)) s.Set(r, c, v); else if (!string.IsNullOrEmpty(text)) s.Set(r, c, text);
    }
    static void Num(XlsxBookSheet s, int r, int c, double v) { if (!double.IsNaN(v)) s.Set(r, c, v); }

    // ======================================================================== write

    public static FrameModel Template(string units, string version)
    {
        return FrameS2k.NewModel(string.IsNullOrEmpty(units) ? "Kip, in, F" : units, string.IsNullOrEmpty(version) ? "22.0.0" : version);
    }

    public static void Write(FrameModel m, string path, string sourceName, FrameWorkbookRun run) { File.WriteAllBytes(path, ToBytes(m, sourceName, run)); }
    public static byte[] ToBytes(FrameModel m, string sourceName, FrameWorkbookRun run) { return Book(m, sourceName, run).ToBytes(); }

    static WbCol C(string name, string group, string kind, string hint) { return new WbCol(name, group, kind, null, hint, null); }
    static WbCol L(string name, string group, string kind, string list, string hint) { return new WbCol(name, group, kind, list, hint, null); }
    static string H(string name, string dim, string units) { string u = FrameUnits.Label(dim, units); return u.Length > 0 ? name + " (" + u + ")" : name; }

    // the Lists sheet's columns: name -> values
    static readonly string[] Shapes = { "Pipe", "Box/Tube", "I/Wide Flange", "Channel", "Angle", "Tee", "Double Angle", "Rectangular", "Circle", "General" };
    static readonly string[] DofNames = { "U1", "U2", "U3", "R1", "R2", "R3" };
    static readonly string[] LinkTypes = { "Linear", "Gap", "Hook", "Friction Isolator", "MultiLinear Elastic", "Damper - Exponential" };
    static readonly string[] PatternTypes = { "Dead", "Super Dead", "Live", "Temperature", "Wind", "Quake", "Snow", "Other" };
    static readonly string[] CaseTypes = { "LinStatic", "NonStatic", "LinModal" };
    static readonly string[] ComboTypes = { "Linear Add", "Envelope", "Abs Add", "SRSS", "Range Add" };
    static readonly string[] LoadTypes = { "Force", "Moment" };
    static readonly string[] Dirs = { "Gravity", "X", "Y", "Z", "1", "2", "3", "X Proj", "Y Proj", "Z Proj", "Gravity Proj" };
    static readonly string[] DistTypes = { "Rel", "Abs" };
    static readonly string[] CoordSyss = { "GLOBAL", "Local" };
    static readonly string[] TempTypes = { "Temperature", "Temp Gradient 2", "Temp Gradient 3" };
    static readonly string[] RestraintPresets = { "fixed", "pin", "U3", "U1 U2", "U1 U3", "U2 U3" };
    static readonly string[] ObjectTypes = { "Joint", "Frame", "Link", "Area" };
    static readonly string[] CaseLoadTypes = { "Load pattern", "Accel" };
    static readonly string[] MaterialTypes = { "Steel", "Concrete", "Aluminum", "ColdFormed", "Other" };
    static string[][] ListCols()
    {
        return new[] { Shapes, FrameMember.CardinalNames.Skip(1).Select((n, k) => FrameMember.CardinalText(k + 1)).ToArray(), new[] { "yes", "no" }, DofNames, LinkTypes,
                       PatternTypes, CaseTypes, ComboTypes, LoadTypes, Dirs, DistTypes, CoordSyss, TempTypes, RestraintPresets, ObjectTypes, CaseLoadTypes, MaterialTypes };
    }
    static readonly string[] ListNames = { "Shapes", "Insertion points", "Yes / no", "DOFs", "Link types", "Pattern types", "Case types", "Combination types", "Load types",
                                           "Directions", "Rel / Abs", "Coordinate systems", "Temperature types", "Restraints", "Object types", "Case load types", "Material types" };
    static string ListRange(int col)
    {
        string c = XlsxBook.Column(col);
        return "Lists!$" + c + "$2:$" + c + "$" + (1 + ListCols()[col].Length).ToString(Inv);
    }

    public static XlsxBook Book(FrameModel m, string sourceName, FrameWorkbookRun run)
    {
        string units = m.Units;
        string json = FrameModelJson.Write(m);
        string hash = WbBase.Sha(json);
        var b = new XlsxBook();
        var t = WbTheme.Make(b, Groups);
        int most = new[] { m.Joints.Count, m.Frames.Count, m.Loads.Count, m.Links.Count, m.Sections.Count, m.Groups.Sum(g => g.Members.Count()) }.Max();
        int lim = Math.Max(2000, most + 1500);
        t.Form = new WbForm { RefRows = lim };
        Func<string, string, string> col = (sheet, letter) => XlsxBook.SheetRef(sheet) + "!$" + letter + "$2:$" + letter + "$" + lim.ToString(Inv);
        string jointIds = col(SJoints, "B"), secNames = col(SSections, "B"), matNames = col(SMaterials, "B"), relNames = col(SReleases, "A"), modNames = col(SModifiers, "A"),
               lpNames = col(SLinkProps, "B"), patNames = col(SPatterns, "B");
        bool light = m.Frames.Count > 5000;
        // a joint not on Joints (red); at = a cell, or a whole column in SUMPRODUCT
        Func<string, string> noJoint = at => "(" + at + "<>\"\")*(COUNTIF(" + jointIds + "," + at + ")=0)";
        string frameIds = col(SFrames, "B");
        // a load's Frames cell naming one frame no sheet has (selectors, lists and ranges are checked by the build)
        Func<string, string> noFrame = at => "(" + at + "<>\"\")*ISERROR(FIND(\":\"," + at + "))*ISERROR(FIND(\",\"," + at + "))*(LOWER(" + at + ")<>\"all\")*(COUNTIF(" + frameIds + "," + at + ")=0)";

        var start = b.Add("Start");
        var st = b.Add("Settings");

        // ---- presets from the model: releases and modifiers
        var rel = new List<Preset>();
        foreach (var bi in RelBuiltIn) { var p = new Preset { Name = (string)bi[0] }; foreach (int i in (int[])bi[1]) p.Rel[i] = true; rel.Add(p); }
        var relOf = new Dictionary<FrameMember, string>();
        foreach (var f in m.Frames)
        {
            bool[] r = f.Releases; double[] pv = f.PartialFixity;
            string k = RelKey(r, pv);
            var hit = rel.FirstOrDefault(p => RelKey(p.Rel, p.Val) == k);
            if (hit == null) { hit = new Preset { Name = "R" + (rel.Count - RelBuiltIn.Length + 1).ToString(Inv) + " " + PresetText(r, pv), Rel = r, Val = pv }; rel.Add(hit); }
            relOf[f] = hit.Name;
        }
        var mods = new List<double[]>(); var modOf = new Dictionary<FrameMember, string>();
        foreach (var f in m.Frames)
        {
            var mv = f.PropertyModifiers;
            if (mv.All(v => v == 1)) { modOf[f] = ""; continue; }
            int k = mods.FindIndex(x => x.SequenceEqual(mv));
            if (k < 0) { mods.Add(mv); k = mods.Count - 1; }
            modOf[f] = "M" + (k + 1).ToString(Inv);
        }
        var kindOf = new Dictionary<string, string>(StringComparer.Ordinal); var lineOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var g in m.Groups)
        {
            bool kind = g.Label.StartsWith("PL_KIND_", StringComparison.OrdinalIgnoreCase), line = g.Label.StartsWith("PL_LINE_", StringComparison.OrdinalIgnoreCase);
            if (!kind && !line) continue;
            foreach (var mm in g.Members) if (mm[0].Equals("Frame", StringComparison.OrdinalIgnoreCase)) (kind ? kindOf : lineOf)[mm[1]] = g.Label.Substring(8);
        }

        // ---- Joints
        var js = b.Add(SJoints);
        var jh = new[] { "Id", "Joint", H("X", "L", units), H("Y", "L", units), H("Z", "L", units), "Restraint", H("Local A", "deg", ""), H("Local B", "deg", ""), H("Local C", "deg", "") };
        js.FreezeCols = 2;
        int r0 = 1;
        foreach (var j in m.Joints)
        {
            js.Set(r0, 0, Id(j)); Label(js, r0, 1, j.Label, 0);
            var p = j.Xyz; js.Set(r0, 2, p[0]); js.Set(r0, 3, p[1]); js.Set(r0, 4, p[2]);
            string rt = RestraintText(j.Restraint); if (rt.Length > 0) js.Set(r0, 5, rt);
            var a = j.LocalAngles; for (int k = 0; k < 3; k++) if (a[k] != 0) js.Set(r0, 6 + k, a[k]);
            r0++;
        }
        t.Form.Table(js, t, jh, new[] {
            C("Id", "key", "id", "J and the joint's number in the model: keep it. Blank on a new joint."),
            C("Joint", "key", "text", "SAP's label: a number or text (A1). A new label renames the joint; Frames, Links and loads naming the old label follow."),
            C("X", "geom", "num", "Global X. A formula is fine: a pipe run's next joint is =C12+24 (the joint in row 12, 24 along X)."),
            C("Y", "geom", "num", "Global Y (a formula is fine)."), C("Z", "geom", "num", "Global Z (up; a formula is fine)."),
            L("Restraint", "bc", "list?", ListRange(13), "fixed (all six), pin (U1 U2 U3), or the DOFs held: U1 U2 U3 R1 R2 R3 (global, or the joint's local axes when it has them). Blank = free."),
            C("Local A", "geom", "num", "Joint local axes: angle A about Z, then B about the new Y, then C about the new X (degrees)."), C("Local B", "geom", "num", "Joint local axes, angle B."), C("Local C", "geom", "num", "Joint local axes, angle C."),
        }, m.Joints.Count, lim);
        js.Conditional("B2:B" + lim, "AND(B2<>\"\",COUNTIF($B$2:$B$" + lim + ",B2)>1)", t.Red);
        js.Conditional("B2:B" + lim, "AND(B2<>\"\",COUNTIF(" + col(SFrames, "C") + ",B2)=0,COUNTIF(" + col(SFrames, "D") + ",B2)=0,COUNTIF(" + col(SLinks, "C") + ",B2)=0,COUNTIF(" + col(SLinks, "D") + ",B2)=0)", t.Amber);
        js.Width(0, 8); js.Width(1, 12); for (int k = 2; k < 9; k++) js.Width(k, 12); js.Width(5, 18);

        // ---- Frames
        var fs = b.Add(SFrames);
        var fh = new[] { "Id", "Frame", "Joint I", "Joint J", H("Length", "L", units), "Section", "Kind", "Line", "Release", H("Angle", "deg", ""), "Insertion", "Modifiers",
                         H("Offset I", "L", units), H("Offset J", "L", units) };
        fs.FreezeCols = 2;
        var pos = new Dictionary<string, double[]>(StringComparer.Ordinal);
        foreach (var j in m.Joints) pos[j.Label] = j.Xyz;
        r0 = 1;
        foreach (var f in m.Frames)
        {
            fs.Set(r0, 0, Id(f)); Label(fs, r0, 1, f.Label, 0); Label(fs, r0, 2, f.JointI, 0); Label(fs, r0, 3, f.JointJ, 0);
            if (f.Section.Length > 0) fs.Set(r0, 5, f.Section);
            string kd, ln; if (kindOf.TryGetValue(f.Label, out kd)) fs.Set(r0, 6, kd); if (lineOf.TryGetValue(f.Label, out ln)) fs.Set(r0, 7, ln);
            fs.Set(r0, 8, relOf[f]);
            if (f.Angle != 0) fs.Set(r0, 9, f.Angle);
            if (f.CardinalPoint != 10) fs.Set(r0, 10, FrameMember.CardinalText(f.CardinalPoint));
            if (modOf[f].Length > 0) fs.Set(r0, 11, modOf[f]);
            for (int e = 0; e < 2; e++) { string ot = OffsetText(f, e); if (ot.Length > 0) fs.Set(r0, 12 + e, ot); }
            r0++;
        }
        for (int r = 1; r < m.Frames.Count + 201; r++)
        {
            string R = (r + 1).ToString(Inv);
            Func<string, string, string> at = (c, jc) => "INDEX(" + XlsxBook.SheetRef(SJoints) + "!$" + c + ":$" + c + ",MATCH(" + jc + R + "," + XlsxBook.SheetRef(SJoints) + "!$B:$B,0))";
            string len = "IFERROR(SQRT((" + at("C", "D") + "-" + at("C", "C") + ")^2+(" + at("D", "D") + "-" + at("D", "C") + ")^2+(" + at("E", "D") + "-" + at("E", "C") + ")^2),\"\")";
            if (r <= m.Frames.Count)
            {
                var f = m.Frames[r - 1];
                double[] a, c2;
                if (pos.TryGetValue(f.JointI, out a) && pos.TryGetValue(f.JointJ, out c2))
                    fs.SetFormula(r, 4, len, Math.Sqrt((c2[0] - a[0]) * (c2[0] - a[0]) + (c2[1] - a[1]) * (c2[1] - a[1]) + (c2[2] - a[2]) * (c2[2] - a[2])), XlsxBook.ReadOnly);
                else fs.SetFormula(r, 4, len, XlsxBook.ReadOnly);
            }
            else fs.SetFormula(r, 4, len, XlsxBook.ReadOnly);
        }
        t.Form.Table(fs, t, fh, new[] {
            C("Id", "key", "id", "F and the frame's number in the model: keep it. Blank on a new frame."),
            C("Frame", "key", "text", "SAP's label. A new label renames the frame; loads and groups naming it follow."),
            C("Joint I", "geom", "text", "The joint at end I: a label on Joints."),
            C("Joint J", "geom", "text", "The joint at end J: a label on Joints. A frame meeting another names the same joint."),
            L("Section", "prop", "list!", secNames, "A section on the Sections sheet."),
            C("Kind", "text", "text", "What the frame is (PIPE, DUCT, STEEL ...): SAP group PL_KIND_<kind>. Selectors use it (kind:PIPE)."),
            C("Line", "text", "text", "The line it belongs to: SAP group PL_LINE_<line>. Selectors use it (line:L-101)."),
            L("Release", "prop", "list!", relNames, "End releases: a preset on the Releases sheet (FIXED, PIN-PIN, PIN-I, PIN-J, SLIP-J, or your own)."),
            C("Angle", "geom", "num", "Local axis angle (degrees): local 2 and 3 turned about local 1 from SAP's default."),
            L("Insertion", "prop", "list!", ListRange(1), "Cardinal point: where the joint sits on the section (10 = centroid, 8 = top center ...)."),
            L("Modifiers", "prop", "list!", modNames, "Property modifiers: a preset on the Modifiers sheet. Blank = none."),
            C("Offset I", "geom", "text", "Joint offset of the insertion point at end I: three numbers along local 1 2 3 (\"0 0 6\"). Blank = none. An offset SAP holds in another system shows it (\"GLOBAL: 0 6 0\"): change it in SAP, or give local values."),
            C("Offset J", "geom", "text", "Joint offset at end J, as Offset I."),
        }, m.Frames.Count, lim);
        fs.Conditional("C2:D" + lim, "AND(C2<>\"\",COUNTIF(" + jointIds + ",C2)=0)", t.Red);
        fs.Conditional("C2:D" + lim, "AND($C2<>\"\",$C2=$D2)", t.Red);
        fs.Conditional("B2:B" + lim, "AND(B2<>\"\",COUNTIF($B$2:$B$" + lim + ",B2)>1)", t.Red);
        fs.Conditional("E2:E" + lim, "AND(ISNUMBER(E2),E2=0)", t.Amber);
        fs.Width(0, 8); fs.Width(1, 10); fs.Width(2, 9); fs.Width(3, 9); fs.Width(4, 11); fs.Width(5, 16); fs.Width(6, 10); fs.Width(7, 10);
        fs.Width(8, 10); fs.Width(9, 10); fs.Width(10, 16); fs.Width(11, 11); fs.Width(12, 14); fs.Width(13, 14);

        // ---- Sections
        var ss = b.Add(SSections);
        var sh = new[] { "Id", "Name", "Shape", "Material", H("t3", "L", units), H("t2", "L", units), H("tf", "L", units), H("tw", "L", units), H("t2b", "L", units), H("tfb", "L", units),
                         H("dis", "L", units), H("Area", "L2", units), H("I33", "L4", units), H("I22", "L4", units), H("J", "L4", units), "Catalog", "Section modifiers" };
        string[] dimKeys = { "t3", "t2", "tf", "tw", "t2b", "tfb", "dis", "Area", "I33", "I22", "TorsConst" };
        string[] secMods = { "AMod", "A2Mod", "A3Mod", "JMod", "I2Mod", "I3Mod", "MMod", "WMod" }, secModNames = { "A", "A2", "A3", "J", "I2", "I3", "mass", "weight" };
        r0 = 1;
        foreach (var s in m.Sections)
        {
            var g = s.Rec(FrameSection.General);
            ss.Set(r0, 0, Id(s)); ss.Set(r0, 1, s.Label); ss.Set(r0, 2, s.Shape); ss.Set(r0, 3, s.Material);
            for (int k = 0; k < dimKeys.Length; k++) if (g != null && g.Has(dimKeys[k])) Num(ss, r0, 4 + k, g.Get(dimKeys[k]));
            if (g != null && g.Yes("FromFile")) ss.Set(r0, 15, (g.Get("FileName") ?? "") + ": " + (g.Get("SectInFile") ?? ""), XlsxBook.ReadOnly);
            string sm = g == null ? "" : string.Join(", ", Enumerable.Range(0, 8).Where(k => g.Has(secMods[k]) && g.Num(secMods[k], 1) != 1).Select(k => secModNames[k] + " x" + g.Get(secMods[k])).ToArray());
            if (sm.Length > 0) ss.Set(r0, 16, sm, XlsxBook.ReadOnly);
            r0++;
        }
        t.Form.Table(ss, t, sh, new[] {
            C("Id", "key", "id", "S and the section's number: keep it. Blank on a new section."), C("Name", "key", "text", "SAP's section name. A new name renames it; Frames naming the old one follow."),
            L("Shape", "prop", "list?", ListRange(0), "SAP's shape: Pipe (t3 = OD, tw = wall), Box/Tube, I/Wide Flange, Channel, Angle, Tee, Double Angle, Rectangular, General ..."),
            L("Material", "prop", "list!", matNames, "A material on the Materials sheet."),
            C("t3", "prop", "pos", "Depth (OD for a pipe)."), C("t2", "prop", "pos", "Width."), C("tf", "prop", "pos", "Flange thickness."), C("tw", "prop", "pos", "Web (wall) thickness."),
            C("t2b", "prop", "pos", "Bottom flange width (blank on a new I section: t2)."), C("tfb", "prop", "pos", "Bottom flange thickness (blank on a new I section: tf)."),
            C("dis", "prop", "nonneg", "Double Angle: the back-to-back spacing."),
            C("Area", "prop", "pos", "Area: a General section's (SAP works it out for the others)."), C("I33", "prop", "pos", "Moment of inertia about 3 (General)."),
            C("I22", "prop", "pos", "Moment of inertia about 2 (General)."), C("J", "prop", "pos", "Torsion constant (General)."),
        }, m.Sections.Count, lim);
        ss.Conditional("B2:B" + lim, "AND(B2<>\"\",COUNTIF($B$2:$B$" + lim + ",B2)>1)", t.Red);
        ss.Width(1, 16); ss.Width(2, 15); ss.Width(3, 12); ss.Width(15, 24); ss.Width(16, 22);

        // ---- Materials
        var ms = b.Add(SMaterials);
        var mh = new[] { "Id", "Name", "Type", H("E", "F/L2", units), "Poisson", H("G", "F/L2", units), H("Unit weight", "F/L3", units), H("Unit mass", "F-s2/L4", units), H("Alpha", "1/T", units),
                         H("Fy", "F/L2", units), H("Fu", "F/L2", units) };
        r0 = 1;
        foreach (var mt in m.Materials)
        {
            var mech = mt.Rec(FrameMaterial.Mechanical);
            ms.Set(r0, 0, Id(mt)); ms.Set(r0, 1, mt.Label); ms.Set(r0, 2, mt.Type);
            string[] mk = { "E1", "U12", "G12", "UnitWeight", "UnitMass", "A1" };
            for (int k = 0; k < mk.Length; k++) if (mech != null && mech.Has(mk[k])) Num(ms, r0, 3 + k, mech.Get(mk[k]));
            Num(ms, r0, 9, mt.SteelData("Fy")); Num(ms, r0, 10, mt.SteelData("Fu"));
            r0++;
        }
        t.Form.Table(ms, t, mh, new[] {
            C("Id", "key", "id", "M and the material's number: keep it."), C("Name", "key", "text", "SAP's material name."), L("Type", "prop", "list?", ListRange(16), "Steel, Concrete, Other ..."),
            C("E", "prop", "pos", "Modulus of elasticity."), C("Poisson", "prop", "num", "Poisson's ratio (0 to 0.5)."), C("G", "prop", "pos", "Shear modulus."),
            C("Unit weight", "prop", "nonneg", "Weight per volume (self weight)."), C("Unit mass", "prop", "nonneg", "Mass per volume."), C("Alpha", "prop", "num", "Thermal expansion per degree."),
            C("Fy", "prop", "pos", "Yield (steel)."), C("Fu", "prop", "pos", "Tensile strength (steel)."),
        }, m.Materials.Count, lim);
        ms.Conditional("E2:E" + lim, "IF(ISNUMBER(E2),OR(E2<0,E2>0.5),E2<>\"\")", t.Red);
        ms.Width(1, 16); ms.Width(2, 10);

        // ---- Releases (presets)
        var rs = b.Add(SReleases);
        var rh = new[] { "Name" }.Concat(RelHeads).ToArray();
        r0 = 1;
        foreach (var p in rel)
        {
            rs.Set(r0, 0, p.Name);
            for (int i = 0; i < 12; i++) if (p.Rel[i]) { if (p.Val[i] != 0) rs.Set(r0, 1 + i, p.Val[i]); else rs.Set(r0, 1 + i, "yes"); }
            r0++;
        }
        t.Form.Table(rs, t, rh, new[] { C("Name", "key", "text", "The preset's name, as Frames' Release column gives it.") }
            .Concat(RelHeads.Select(h => C(h, "prop", "text", "Released at this end: yes; partly fixed: its spring stiffness; blank = fixed."))).ToArray(), rel.Count, 400);
        rs.Width(0, 12);

        // ---- Modifiers (presets)
        var mo = b.Add(SModifiers);
        r0 = 1;
        for (int k = 0; k < mods.Count; k++) { mo.Set(r0, 0, "M" + (k + 1).ToString(Inv)); for (int i = 0; i < 8; i++) mo.Set(r0, 1 + i, mods[k][i]); r0++; }
        t.Form.Table(mo, t, new[] { "Name" }.Concat(ModHeads).ToArray(), new[] { C("Name", "key", "text", "The preset's name, as Frames' Modifiers column gives it.") }
            .Concat(ModHeads.Select(h => C(h, "prop", "nonneg", "Factor on the section's " + h + " (1 = none)."))).ToArray(), mods.Count, 400);

        // ---- Links
        var ls = b.Add(SLinks);
        var lh = new[] { "Id", "Link", "Joint I", "Joint J", "Property", H("Angle", "deg", "") };
        r0 = 1;
        foreach (var l in m.Links)
        {
            ls.Set(r0, 0, Id(l)); Label(ls, r0, 1, l.Label, 0); Label(ls, r0, 2, l.JointI, 0); if (l.JointJ.Length > 0) Label(ls, r0, 3, l.JointJ, 0);
            ls.Set(r0, 4, l.Property); if (l.Angle != 0) ls.Set(r0, 5, l.Angle);
            r0++;
        }
        t.Form.Table(ls, t, lh, new[] {
            C("Id", "key", "id", "K and the link's number: keep it."), C("Link", "key", "text", "SAP's label."), C("Joint I", "bc", "text", "Its first joint."),
            C("Joint J", "bc", "text", "Its second joint; blank = a one-joint (grounded) link."), L("Property", "bc", "list!", lpNames, "A property on Link props."),
            C("Angle", "geom", "num", "Local axis angle (degrees), about local 1. SAP's default axes: local 1 from I to J (+Z for a one-joint or zero-length link); local 2 in the vertical plane through 1 (+X when 1 is vertical); local 3 = 1 x 2. A gap's U1 acts along local 1."),
        }, m.Links.Count, lim);
        ls.Conditional("C2:D" + lim, noJoint("C2") + ">0", t.Red);

        // ---- Link props (one row per DOF)
        var lp = b.Add(SLinkProps);
        var lph = new[] { "Id", "Name", "Type", "DOF", "Fixed", "NonLinear", "KE", "CE", "K", "C", "CExp", "Open", "Mu slow", "Mu fast", "Rate", "Radius", "Points" };
        string[] lpk = { "KE", "CE", "K", "C", "CExp", "Open", "Slow", "Fast", "Rate", "Radius" };
        r0 = 1;
        int lpRows = 0;
        foreach (var p in m.LinkProps)
        {
            lp.Set(r0, 0, Id(p)); lp.Set(r0, 1, p.Label); lp.Set(r0, 2, p.LinkType);
            var dofs = p.DofRows.ToList();
            if (dofs.Count == 0) { r0++; lpRows++; continue; }
            foreach (var d in dofs)
            {
                bool rot = (d.Get("DOF") ?? "").StartsWith("R", StringComparison.OrdinalIgnoreCase);
                lp.Set(r0, 3, d.Get("DOF") ?? ""); lp.Set(r0, 4, d.Yes("Fixed") ? "yes" : "no"); if (d.Has("NonLinear")) lp.Set(r0, 5, d.Yes("NonLinear") ? "yes" : "no");
                for (int k = 0; k < lpk.Length; k++)
                {
                    string fld = LinkField(lpk[k], rot, d);
                    if (d.Has(fld)) Num(lp, r0, 6 + k, d.Get(fld));
                }
                var pts = p.Points(d.Get("DOF") ?? "");
                if (pts.Count > 0) lp.Set(r0, 16, string.Join("; ", pts.Select(q => q[0] + ", " + q[1]).ToArray()));
                r0++; lpRows++;
            }
        }
        t.Form.Table(lp, t, lph, new[] {
            C("Id", "key", "id", "LP and the property's number, on its first row: keep it."), C("Name", "key", "text", "The property's name, on its first row; a blank Name continues the property above."),
            L("Type", "bc", "list?", ListRange(4), "Linear, Gap, Hook, Friction Isolator, MultiLinear Elastic, Damper (first row)."),
            L("DOF", "bc", "list!", ListRange(3), "U1 (along local 1, axial) ... R3."), L("Fixed", "bc", "yesno", ListRange(2), "yes: rigid in this DOF."),
            L("NonLinear", "bc", "yesno", ListRange(2), "yes: its nonlinear behaviour (gap, hook, friction ...) in nonlinear cases."),
            C("KE", "bc", "num", "Effective stiffness: what linear cases see (force / length, or moment / radian for R1-R3)."), C("CE", "bc", "num", "Effective damping."),
            C("K", "bc", "num", "Nonlinear stiffness (gap, hook, friction: the contact / elastic stiffness)."), C("C", "bc", "num", "Damper coefficient."), C("CExp", "bc", "num", "Damper exponent."),
            C("Open", "bc", "num", "Gap / hook opening (length)."), C("Mu slow", "bc", "num", "Friction coefficient, slow."), C("Mu fast", "bc", "num", "Friction coefficient, fast."),
            C("Rate", "bc", "num", "Friction rate parameter."), C("Radius", "bc", "num", "Friction isolator radius (0 = flat)."),
            C("Points", "bc", "text", "MultiLinear: the force-deformation curve as force, displacement pairs (\"-12, -2; 0, 0; 12, 2\"): at least three, displacements increasing, through 0, 0."),
        }, lpRows, lim);

        // ---- Springs (per joint)
        var sp = b.Add(SSprings);
        var sph = new[] { "Joint", "CoordSys", H("U1", "F/L", units), H("U2", "F/L", units), H("U3", "F/L", units), H("R1", "F-L/rad", units), H("R2", "F-L/rad", units), H("R3", "F-L/rad", units) };
        r0 = 1;
        foreach (var j in m.Joints)
        {
            var sr = j.Rec(FrameJoint.Springs);
            if (sr == null) continue;
            Label(sp, r0, 0, j.Label, 0); sp.Set(r0, 1, sr.Get("CoordSys") ?? "GLOBAL");
            for (int i = 0; i < 6; i++) Num(sp, r0, 2 + i, sr.Get(FrameJoint.Dofs[i]));
            r0++;
        }
        t.Form.Table(sp, t, sph, new[] { C("Joint", "key", "text", "The joint, once."), L("CoordSys", "bc", "list?", ListRange(11), "GLOBAL or Local (the joint's local axes).") }
            .Concat(FrameJoint.Dofs.Select(d => C(d, "bc", "nonneg", "Spring stiffness in " + d + "."))).ToArray(), r0 - 1, lim);
        sp.Conditional("A2:A" + lim, noJoint("A2") + ">0", t.Red);

        // ---- Load patterns
        var ps = b.Add(SPatterns);
        r0 = 1;
        foreach (var p in m.Patterns) { ps.Set(r0, 0, Id(p)); ps.Set(r0, 1, p.Label); ps.Set(r0, 2, p.DesignType); ps.Set(r0, 3, p.SelfWeight); r0++; }
        t.Form.Table(ps, t, new[] { "Id", "Name", "Type", "Self weight" }, new[] {
            C("Id", "key", "id", "PAT and the pattern's number: keep it."), C("Name", "key", "text", "The pattern's name."), L("Type", "load", "list?", ListRange(5), "Dead, Live, Temperature, Wind, Other ..."),
            C("Self weight", "load", "num", "Self-weight multiplier (1 on one dead pattern, 0 elsewhere)."),
        }, m.Patterns.Count, 400);

        // ---- loads
        Func<string, List<FrameLoad>> loadsOf = tb => m.Loads.Where(x => x.Table == tb).ToList();
        // joint loads, ground displacements
        foreach (var kind in new[] { FrameLoad.JointForce, FrameLoad.GroundDisp })
        {
            bool disp = kind == FrameLoad.GroundDisp;
            var ws = b.Add(disp ? SGround : SJointLoads);
            string[] keys = disp ? new[] { "U1", "U2", "U3", "R1", "R2", "R3" } : new[] { "F1", "F2", "F3", "M1", "M2", "M3" };
            string[] dims = disp ? new[] { "L", "L", "L", "rad", "rad", "rad" } : new[] { "F", "F", "F", "F-L", "F-L", "F-L" };
            var list = loadsOf(kind);
            r0 = 1;
            foreach (var ld in list)
            {
                var row = ld.Row;
                ws.Set(r0, 0, Id(ld)); Label(ws, r0, 1, ld.Target, 0); ws.Set(r0, 2, ld.Pattern); ws.Set(r0, 3, row.Get("CoordSys") ?? "GLOBAL");
                for (int k = 0; k < 6; k++) Num(ws, r0, 4 + k, row.Get(keys[k]));
                r0++;
            }
            t.Form.Table(ws, t, new[] { "Id", "Joint", "Pattern", "CoordSys" }.Concat(keys.Select((k, i) => H(k, dims[i], units))).ToArray(),
                new[] { C("Id", "key", "id", (disp ? "GD" : "JL") + " and the load's number: keep it."), C("Joint", "key", "text", "The joint."),
                        L("Pattern", "load", "list!", patNames, "Its load pattern."), L("CoordSys", "load", "list?", ListRange(11), "GLOBAL or Local.") }
                .Concat(keys.Select(k => C(k, "load", "num", (disp ? "Imposed " : "") + k + "."))).ToArray(), list.Count, lim);
            ws.Conditional("B2:B" + lim, noJoint("B2") + ">0", t.Red);
        }
        // frame point loads
        {
            var ws = b.Add(SPoint);
            var list = loadsOf(FrameLoad.Point);
            r0 = 1;
            foreach (var ld in list)
            {
                var row = ld.Row;
                bool isRel = (row.Get("DistType") ?? "RelDist").StartsWith("Rel", StringComparison.OrdinalIgnoreCase);
                ws.Set(r0, 0, Id(ld)); Label(ws, r0, 1, ld.Target, 0); ws.Set(r0, 2, ld.Pattern); ws.Set(r0, 3, row.Get("CoordSys") ?? "GLOBAL"); ws.Set(r0, 4, row.Get("Type") ?? "Force");
                Label(ws, r0, 5, row.Get("Dir") ?? "Gravity", 0); ws.Set(r0, 6, isRel ? "Rel" : "Abs"); Num(ws, r0, 7, row.Get(isRel ? "RelDist" : "AbsDist"));
                Num(ws, r0, 8, row.Get(row.Has("Force") ? "Force" : "Moment"));
                r0++;
            }
            t.Form.Table(ws, t, new[] { "Id", "Frames", "Pattern", "CoordSys", "Type", "Dir", "Rel / Abs", H("Distance", "L", units), "Value (" + FrameUnits.Label("F", units) + " | " + FrameUnits.Label("F-L", units) + ")" }, LoadCols("PL", patNames).Concat(new[] {
                L("Rel / Abs", "load", "list!", ListRange(10), "Rel: Distance is a fraction of the length (0 to 1); Abs: a length from end I."),
                C("Distance", "load", "nonneg", "Where along the frame, from end I: a fraction on a Rel row, a length (the header's unit) on an Abs row."),
                C("Value", "load", "num", "The force (first unit) or, on a Moment row, the moment (second unit); a positive Gravity load points down."),
            }).ToArray(), list.Count, lim);
            ws.Conditional("H2:H" + lim, "AND(G2=\"Rel\",ISNUMBER(H2),OR(H2<0,H2>1))", t.Red);
            ws.Conditional("B2:B" + lim, noFrame("B2") + ">0", t.Red);
        }
        // frame distributed loads
        {
            var ws = b.Add(SDist);
            var list = loadsOf(FrameLoad.Distributed);
            r0 = 1;
            foreach (var ld in list)
            {
                var row = ld.Row;
                bool isRel = (row.Get("DistType") ?? "RelDist").StartsWith("Rel", StringComparison.OrdinalIgnoreCase);
                bool mom = row.Has("MOverLA");
                ws.Set(r0, 0, Id(ld)); Label(ws, r0, 1, ld.Target, 0); ws.Set(r0, 2, ld.Pattern); ws.Set(r0, 3, row.Get("CoordSys") ?? "GLOBAL"); ws.Set(r0, 4, row.Get("Type") ?? "Force");
                Label(ws, r0, 5, row.Get("Dir") ?? "Gravity", 0); ws.Set(r0, 6, isRel ? "Rel" : "Abs");
                Num(ws, r0, 7, row.Get(isRel ? "RelDistA" : "AbsDistA")); Num(ws, r0, 8, row.Get(isRel ? "RelDistB" : "AbsDistB"));
                Num(ws, r0, 9, row.Get(mom ? "MOverLA" : "FOverLA")); Num(ws, r0, 10, row.Get(mom ? "MOverLB" : "FOverLB"));
                r0++;
            }
            string perL = "(" + FrameUnits.Label("F/L", units) + " | " + FrameUnits.Label("F-L/L", units) + ")";
            t.Form.Table(ws, t, new[] { "Id", "Frames", "Pattern", "CoordSys", "Type", "Dir", "Rel / Abs", H("Start", "L", units), H("End", "L", units), "Value A " + perL, "Value B " + perL }, LoadCols("DL", patNames).Concat(new[] {
                L("Rel / Abs", "load", "list!", ListRange(10), "Rel: Start / End are fractions of the length; Abs: lengths from end I."),
                C("Start", "load", "nonneg", "Where it starts (blank = 0): a fraction on a Rel row, a length (the header's unit) on an Abs row."),
                C("End", "load", "nonneg", "Where it ends (blank = the whole length)."),
                C("Value A", "load", "num", "Load per length at Start: a force per length, or on a Moment row a moment per length (the second unit)."),
                C("Value B", "load", "num", "Load per length at End (blank = Value A, uniform)."),
            }).ToArray(), list.Count, lim);
            ws.Conditional("H2:I" + lim, "AND($G2=\"Rel\",ISNUMBER(H2),OR(H2<0,H2>1))", t.Red);
            ws.Conditional("B2:B" + lim, noFrame("B2") + ">0", t.Red);
        }
        // frame temperature loads
        {
            var ws = b.Add(STemp);
            var list = loadsOf(FrameLoad.Temperature);
            r0 = 1;
            foreach (var ld in list)
            {
                var row = ld.Row;
                ws.Set(r0, 0, Id(ld)); Label(ws, r0, 1, ld.Target, 0); ws.Set(r0, 2, ld.Pattern); ws.Set(r0, 3, row.Get("Type") ?? "Temperature"); Num(ws, r0, 4, row.Get("Temp"));
                r0++;
            }
            t.Form.Table(ws, t, new[] { "Id", "Frames", "Pattern", "Type", H("Value", "T", units) }, new[] {
                C("Id", "key", "id", "TL and the load's number: keep it."), C("Frames", "key", "text", FramesHint), L("Pattern", "load", "list!", patNames, "Its load pattern."),
                L("Type", "load", "list?", ListRange(12), "Temperature (a uniform change), or a gradient."), C("Value", "load", "num", "The temperature change."),
            }, list.Count, lim);
            ws.Conditional("B2:B" + lim, noFrame("B2") + ">0", t.Red);
        }

        // ---- Load cases (one row per load)
        var cs = b.Add(SCases);
        r0 = 1;
        foreach (var c in m.Cases)
        {
            var lds = c.Loads.ToList();
            cs.Set(r0, 0, Id(c)); cs.Set(r0, 1, c.Label); cs.Set(r0, 2, c.Type); cs.Set(r0, 3, c.Initial);
            if (lds.Count == 0) { r0++; continue; }
            foreach (var x in lds) { cs.Set(r0, 4, x[0]); cs.Set(r0, 5, x[1]); Num(cs, r0, 6, x[2]); r0++; }
        }
        t.Form.Table(cs, t, new[] { "Id", "Case", "Type", "Initial", "Load type", "Load name", "SF" }, new[] {
            C("Id", "key", "id", "CASE and the case's number, on its first row: keep it."), C("Case", "key", "text", "The case's name on its first row; a blank Case continues the case above."),
            L("Type", "load", "list?", ListRange(6), "LinStatic, NonStatic (gaps, hooks, friction need it) ..."),
            C("Initial", "load", "text", "Zero, or the nonlinear case it continues from (OPE from SUS)."),
            L("Load type", "load", "list?", ListRange(15), "Load pattern (usually)."), L("Load name", "load", "text", null, "The pattern."), C("SF", "load", "num", "Scale factor."),
        }, r0 - 1, lim);
        cs.Conditional("F2:F" + lim, "AND(F2<>\"\",E2=\"Load pattern\",COUNTIF(" + patNames + ",F2)=0)", t.Red);

        // ---- Combinations (one row per item)
        var cb = b.Add(SCombos);
        r0 = 1;
        foreach (var c in m.Combos)
        {
            var items = c.Items.ToList();
            cb.Set(r0, 0, Id(c)); cb.Set(r0, 1, c.Label); cb.Set(r0, 2, c.Type);
            if (items.Count == 0) { r0++; continue; }
            foreach (var x in items) { cb.Set(r0, 3, x[1]); Num(cb, r0, 4, x[2]); r0++; }
        }
        t.Form.Table(cb, t, new[] { "Id", "Combination", "Type", "Item", "SF" }, new[] {
            C("Id", "key", "id", "CMB and the combination's number, on its first row: keep it."),
            C("Combination", "key", "text", "Its name on its first row; a blank name continues the combination above."),
            L("Type", "load", "list?", ListRange(7), "Linear Add, Envelope, Abs Add, SRSS, Range Add."), C("Item", "load", "text", "A load case or another combination."), C("SF", "load", "num", "Scale factor."),
        }, r0 - 1, lim);
        cb.Conditional("D2:D" + lim, "AND(D2<>\"\",COUNTIF(" + col(SCases, "B") + ",D2)=0,COUNTIF($B$2:$B$" + lim + ",D2)=0)", t.Red);

        // ---- Groups (one row per member; the PL_ groups are Kind / Line)
        var gs = b.Add(SGroups);
        r0 = 1;
        foreach (var g in m.Groups)
        {
            if (g.Label.StartsWith("PL_KIND_", StringComparison.OrdinalIgnoreCase) || g.Label.StartsWith("PL_LINE_", StringComparison.OrdinalIgnoreCase)) continue;
            var mm = g.Members.ToList();
            if (mm.Count == 0) { gs.Set(r0, 0, g.Label); r0++; continue; }
            foreach (var x in mm) { gs.Set(r0, 0, g.Label); gs.Set(r0, 1, x[0]); Label(gs, r0, 2, x[1], 0); r0++; }
        }
        t.Form.Table(gs, t, new[] { "Group", "Object type", "Label" }, new[] {
            C("Group", "key", "text", "The group, on every row of it."), L("Object type", "text", "list?", ListRange(14), "Joint, Frame, Link, Area."), C("Label", "text", "text", "The object's label."),
        }, r0 - 1, lim);
        gs.Width(0, 16); gs.Width(1, 12); gs.Width(2, 12);

        // ---- Carried (read-only)
        var ca = b.Add("Carried");
        ca.Set(0, 0, "Table", t.Head["ro"]); ca.Set(0, 1, "Rows carried", t.Head["ro"]); ca.Width(0, 50); ca.Width(1, 14);
        r0 = 1;
        foreach (var tb in m.Source.Tables)
        {
            int n = 0;
            foreach (var row in tb.Rows) { string kind; bool main; if (!FrameS2k.OwnerOf(tb.Name, row, out kind, out main)) n++; }
            if (n == 0) continue;
            ca.Set(r0, 0, tb.Name, XlsxBook.ReadOnly); ca.Set(r0, 1, n, XlsxBook.ReadOnly); r0++;
        }
        if (r0 == 1) { ca.Set(1, 0, "(no table rides along on its own)", XlsxBook.Note); r0++; }
        var shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            FrameJoint.Coords, FrameJoint.Restraints, FrameJoint.Springs, FrameJoint.LocalAxes, FrameMember.Conn, FrameMember.SectionAssign, FrameMember.LocalAxes,
            FrameMember.Release, FrameMember.Partial, FrameMember.Insertion, FrameMember.Modifiers, FrameSection.General, FrameMaterial.General, FrameMaterial.Mechanical,
            FrameMaterial.Steel, FrameLinkProp.General, FrameLink.Conn, FrameLink.PropAssign, FrameLink.LocalAxes, FrameGroup.Defs, FrameGroup.Assign, FramePattern.Defs,
            FrameLoad.JointForce, FrameLoad.GroundDisp, FrameLoad.Point, FrameLoad.Distributed, FrameLoad.Temperature, FrameCase.Defs, FrameCase.StaticLoads, FrameCombo.Defs,
        };
        var kept = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var o in m.All())
            foreach (var rr in o.Recs)
            {
                if (shown.Contains(rr.Table) || o is FrameLinkProp && rr.Has("DOF")) continue;
                int n0; kept.TryGetValue(rr.Table, out n0); kept[rr.Table] = n0 + 1;
            }
        r0++;
        ca.Set(r0, 0, "Kept with their objects, shown on no sheet", t.Head["ro"]); ca.Set(r0, 1, "Rows", t.Head["ro"]); r0++;
        if (kept.Count == 0) { ca.Set(r0, 0, "(none)", XlsxBook.Note); r0++; }
        foreach (var kv in kept) { ca.Set(r0, 0, kv.Key, XlsxBook.ReadOnly); ca.Set(r0, 1, kv.Value, XlsxBook.ReadOnly); r0++; }
        ca.Set(r0 + 1, 0, "These follow their object: a renamed frame renames them, a deleted one takes them away. Edit them in SAP.", XlsxBook.Note);

        // ---- Lists (hidden), _pluto (veryHidden)
        var li = b.Add("Lists");
        li.State = "hidden";
        var lcols = ListCols();
        for (int k = 0; k < lcols.Length; k++)
        {
            li.Set(0, k, ListNames[k], XlsxBook.Header);
            for (int i = 0; i < lcols[k].Length; i++) li.Set(1 + i, k, lcols[k][i]);
        }
        WbBase.Write(b, Format, Version, hash, sourceName, json);

        // ---- Settings
        st.Width(0, 22); st.Width(1, 40); st.Width(2, 70);
        st.Set(0, 0, "Setting", XlsxBook.Header); st.Set(0, 1, "Value", XlsxBook.Header); st.Set(0, 2, "", XlsxBook.Header);
        SetRow(st, t, 1, "SAP version", m.Version, "The Version stamped on the .s2k's PROGRAM CONTROL line: SAP imports nothing from a file stamped for another version. Help > About in SAP shows yours.");
        SetRowRo(st, 2, "Units", units, "The model's units (SAP's CurrUnits). Headers show them; a header in another unit (\"X (ft)\") converts its column.");
        SetRowRo(st, 3, "Source", sourceName ?? "", "Where the model came from.");
        SetRowRo(st, 4, "Fingerprint", hash, "SHA-256 of the model this workbook was written from (the hidden base).");
        SetRowRo(st, 5, "Format", Format + " " + Version.ToString(Inv), "This workbook's layout.");

        // ---- Start
        Func<string, string, string> rowsOf = (sheet, c) => "COUNTA(" + col(sheet, c) + ")";
        var sheets = new List<WbSheetInfo>
        {
            new WbSheetInfo("Settings", "SAP version stamp, units, where the model came from", null),
            new WbSheetInfo(SJoints, "Joints: X Y Z, restraints, local axes", rowsOf(SJoints, "B")),
            new WbSheetInfo(SFrames, "Frames: two joints, section, kind, line, release preset, angle, insertion, modifiers", rowsOf(SFrames, "B")),
            new WbSheetInfo(SSections, "Sections: shape, material, dimensions", rowsOf(SSections, "B")),
            new WbSheetInfo(SMaterials, "Materials: E, Poisson, unit weight, alpha, Fy, Fu", rowsOf(SMaterials, "B")),
            new WbSheetInfo(SReleases, "End release presets", rowsOf(SReleases, "A")),
            new WbSheetInfo(SModifiers, "Property modifier presets", rowsOf(SModifiers, "A")),
            new WbSheetInfo(SLinks, "Links: joints, property, angle", rowsOf(SLinks, "B")),
            new WbSheetInfo(SLinkProps, "Link properties: type, then one row per DOF", rowsOf(SLinkProps, "B")),
            new WbSheetInfo(SSprings, "Joint springs", rowsOf(SSprings, "A")),
            new WbSheetInfo(SPatterns, "Load patterns", rowsOf(SPatterns, "B")),
            new WbSheetInfo(SJointLoads, "Joint forces", rowsOf(SJointLoads, "B")),
            new WbSheetInfo(SGround, "Ground displacements", rowsOf(SGround, "B")),
            new WbSheetInfo(SPoint, "Frame point loads", rowsOf(SPoint, "B")),
            new WbSheetInfo(SDist, "Frame distributed loads", rowsOf(SDist, "B")),
            new WbSheetInfo(STemp, "Frame temperature loads", rowsOf(STemp, "B")),
            new WbSheetInfo(SCases, "Load cases: type, initial case, then one row per load", rowsOf(SCases, "B")),
            new WbSheetInfo(SCombos, "Combinations: type, then one row per item", rowsOf(SCombos, "B")),
            new WbSheetInfo(SGroups, "SAP groups, one row per member", rowsOf(SGroups, "A")),
            new WbSheetInfo("Carried", "Tables the workbook does not show: they ride along unchanged (read-only)", null),
        };
        string fr = col(SFrames, "C"), frJ = col(SFrames, "D");
        var checks = new List<WbCheck>
        {
            new WbCheck("Frames with a joint no sheet has", light ? "0" : "SUMPRODUCT(" + noJoint(fr) + ")+SUMPRODUCT(" + noJoint(frJ) + ")", false),
            new WbCheck("Frames from a joint to itself", "SUMPRODUCT((" + fr + "<>\"\")*(" + fr + "=" + frJ + "))", false),
            new WbCheck("Frames with a section, release or modifiers preset no sheet has", light ? "0" : "SUMPRODUCT((" + col(SFrames, "F") + "<>\"\")*(COUNTIF(" + secNames + "," + col(SFrames, "F") + ")=0))+SUMPRODUCT((" +
                        col(SFrames, "I") + "<>\"\")*(COUNTIF(" + relNames + "," + col(SFrames, "I") + ")=0))+SUMPRODUCT((" + col(SFrames, "L") + "<>\"\")*(COUNTIF(" + modNames + "," + col(SFrames, "L") + ")=0))", false),
            new WbCheck("Joint or frame labels used twice", light ? "0" : "SUMPRODUCT((" + jointIds + "<>\"\")*(COUNTIF(" + jointIds + "," + jointIds + ")>1))+SUMPRODUCT((" + col(SFrames, "B") + "<>\"\")*(COUNTIF(" + col(SFrames, "B") + "," + col(SFrames, "B") + ")>1))", false),
            new WbCheck("Links with a joint or property no sheet has", "SUMPRODUCT(" + noJoint(col(SLinks, "C")) + ")+SUMPRODUCT(" + noJoint(col(SLinks, "D")) + ")+SUMPRODUCT((" +
                        col(SLinks, "E") + "<>\"\")*(COUNTIF(" + lpNames + "," + col(SLinks, "E") + ")=0))", false),
            new WbCheck("Loads naming a pattern no sheet has", string.Join("+", new[] { SJointLoads, SGround, SPoint, SDist, STemp }.Select(sn => "SUMPRODUCT((" + col(sn, "C") + "<>\"\")*(COUNTIF(" + patNames + "," + col(sn, "C") + ")=0))").ToArray()), false),
            new WbCheck("Loads on a frame no sheet has", string.Join("+", new[] { SPoint, SDist, STemp }.Select(sn => "SUMPRODUCT(" + noFrame(col(sn, "B")) + ")").ToArray()), false),
            new WbCheck("Joints no frame or link uses", light ? "0" : "SUMPRODUCT((" + jointIds + "<>\"\")*(COUNTIF(" + fr + "," + jointIds + ")+COUNTIF(" + frJ + "," + jointIds + ")+COUNTIF(" + col(SLinks, "C") + "," + jointIds + ")+COUNTIF(" + col(SLinks, "D") + "," + jointIds + ")=0))", true),
            new WbCheck("Ids used twice (a copied row: read as new)", string.Join("+", new[] { SJoints, SFrames, SSections, SLinks, SPoint, SDist }.Select(sn => "SUMPRODUCT((" + col(sn, "A") + "<>\"\")*(COUNTIF(" + col(sn, "A") + "," + col(sn, "A") + ")>1))").ToArray()), true),
        };
        var x0 = new WbStart.Texts
        {
            Heading = "SAP2000 frame model workbook",
            Subtitle = "made from " + (string.IsNullOrEmpty(sourceName) ? "a new model" : sourceName) + "   |   units: " + units + (light ? "   |   live checks: light (over 5,000 frames)" : ""),
            Steps = new[]
            {
                "1   Edit the white cells. A dropdown offers the allowed values; selecting a cell shows a hint; the red corner on a header holds its note.",
                "2   Red cells will stop the build; amber ones are worth a look. The checks below count them as you type.",
                "3   Frames take a release preset (Releases sheet), a section (Sections) and optional Kind / Line; loads name their frames by label, range (C1:C9), group:, line:, kind:, section: or all.",
                "4   Save, then build: the links under Build it from here, or Build-Frame.ps1 -In <this>.xlsx -Preview (the viewer) or -S2k (SAP2000). -Watch rebuilds on every save.",
                "5   Keep the Id column. It ties each row to the model this workbook was written from; leave it blank on a new row.",
            },
            CheckNote = "A check counts cells or rows. Red counts stop the build; amber ones (joints nothing uses, copied Ids) may be fine.",
            IdExample = "F12",
            RedText = "Will stop the build: text where a number belongs, a name no sheet has, a frame from a joint to itself ...",
            AmberText = "Worth a look: a joint nothing uses, a copied Id, a zero-length frame",
            Reference = new[]
            {
                "Labels: SAP's own (numbers or text). Frames' joints, links' joints, loads' frames and joints name them; a renamed object takes its references along.",
                "Releases: P V2 V3 T M2 M3 at end I and end J (SAP's local axes); yes = released, a number = partly fixed (its spring); M2 + M3 released at an end = a pin there.",
                "Loads: Dir Gravity (down, positive), X Y Z (global), 1 2 3 (the frame's local axes), X Proj ... (projected). Rel distances are fractions of the length.",
                "Cases: a nonlinear case (NonStatic) is what gaps, hooks and friction need; OPE continues from SUS (Initial = SUS); EXP = OPE - SUS as a Linear Add combination.",
                "Carried: everything the sheets do not show (design data, areas, analysis options ...) is kept from the source and written back unchanged.",
            },
        };
        WbStart.StartSheet(start, t, x0, run == null ? (Func<int, int>)null : r => RunBlock(start, t, r, run), sheets, checks);
        return b;
    }

    const string FramesHint = "The frames it loads: a label, a range in Frames' order (C1:C9), group:NAME, line:NAME, kind:NAME, section:NAME or all; several, separated by commas.";

    static WbCol[] LoadCols(string prefix, string patNames)
    {
        return new[] {
            C("Id", "key", "id", prefix + " and the load's number: keep it. A row naming several frames gives one load per frame."), C("Frames", "key", "text", FramesHint),
            L("Pattern", "load", "list!", patNames, "Its load pattern."), L("CoordSys", "load", "list?", ListRange(11), "GLOBAL, or Local for directions 1 2 3."),
            L("Type", "load", "list?", ListRange(8), "Force or Moment."), L("Dir", "load", "list?", ListRange(9), "Gravity, X, Y, Z, 1, 2, 3, X Proj ..."),
        };
    }

    static void SetRow(XlsxBookSheet s, WbTheme t, int r, string k, string v, string note)
    {
        s.Set(r, 0, k, t.Label); s.Set(r, 1, v ?? "", t.Value); s.Set(r, 2, note, XlsxBook.Note);
        s.Validate(XlsxBook.Ref(r, 1), new XlsxValidation { PromptTitle = k, Prompt = note.Length > 250 ? note.Substring(0, 250) : note });
    }
    static void SetRowRo(XlsxBookSheet s, int r, string k, string v, string note) { s.Set(r, 0, k, XlsxBook.Bold); s.Set(r, 1, v ?? "", XlsxBook.ReadOnly); s.Set(r, 2, note, XlsxBook.Note); }

    // blank while the workbook keeps the name it was written under; a warning once it is saved as another
    static string NameCheck(string file)
    {
        const string cf = "CELL(\"filename\",$A$1)";
        return "IF(" + cf + "=\"\",\"\",IF(AND(ISERROR(FIND(" + WbRun.Lit("[" + file + "]") + "," + cf + ")),ISERROR(FIND(" + WbRun.Lit("/" + file + "'") + "," + cf + "))),\"Saved under another name: the links and commands above still build \"&" +
               WbRun.Lit(file) + "&\". Run the Refresh command on this file.\",\"OK: this is the workbook they build\"))";
    }

    static int RunBlock(XlsxBookSheet s, WbTheme t, int r, FrameWorkbookRun run)
    {
        var links = new List<WbLink>
        {
            new WbLink(WbRun.Lit(run.PreviewCmd), "\u25B6  Live preview",
                       "Runs \"" + run.PreviewCmd + "\" beside this workbook: every save (Ctrl+S) rebuilds " + run.Out + ".bin and " + run.Out +
                       ".features.json for Pluto's viewer (tick Auto-reload there). Close its window to stop."),
            new WbLink(WbRun.Lit(run.S2kCmd), "\u25B6  Write the .s2k",
                       "Runs \"" + run.S2kCmd + "\": " + run.Out + ".s2k for SAP2000 (File, Import, SAP2000 .s2k) and the preview with it."),
            new WbLink(WbRun.Lit(run.OpenCmd), "\u25B6  Open in SAP2000",
                       "Runs \"" + run.OpenCmd + "\": writes " + run.Out + ".s2k, then imports it in a SAP2000 of its own, stamped for its version, and saves " + run.Out + ".sdb. Its report compares SAP's joint, frame and link counts with the model's."),
        };
        if (run.Viewer != null)
            links.Add(new WbLink(WbRun.Lit(run.Viewer), "Open Pluto's viewer", "Then Choose files... and pick " + run.Out + ".bin and " + run.Out + ".features.json together (Chrome or Edge)."));
        links.Add(new WbLink(run.FolderFormula(), "Open this folder", "The workbook, the three launchers and what they write."));
        var commands = new List<WbCommand>
        {
            new WbCommand("Preview command", run.CommandFormula("-Preview -Watch"), null),
            new WbCommand(".s2k command", run.CommandFormula("-S2k -Preview"), null),
            new WbCommand("Open in SAP command", run.CommandFormula("-S2k -Preview -Open"), null),
            new WbCommand("Refresh command", run.RefreshFormula(),
                          "Brings this workbook to the newest Pluto's layout, over itself, edits kept; the previous file stays beside it as .xlsx.bak. Close the workbook first: Excel holds it while it is open."),
            new WbCommand("Name check", NameCheck(run.Name + ".xlsx"),
                          "Saved under another name (Save As), the links and commands above still build the workbook they were written for. Run the Refresh command on this file to point them here."),
        };
        return WbStart.RunBlock(s, t, r, links,
            "Excel asks before it follows a link to a .cmd file; where it will not, double-click the file in the folder. Or paste a command into PowerShell:", commands);
    }

    // ======================================================================== refresh

    // The workbook in this version's layout, every typed cell back where it was (the kit's WbRefresh). The book is
    // written from the workbook's own base, not from what was read: rows typed since (blank Ids) stay new against it,
    // and a row's Id keeps naming the same object, so the next read gives the same model.
    public static byte[] RefreshBytes(string path, FrameWorkbookResult read, string sourceName, FrameWorkbookRun run, List<string> notes)
    {
        var b = Book(read.Base, sourceName, run);
        return WbRefresh.Bytes(b, path, Inputs.Where(n => n != "Settings").ToList(), "Settings", new HashSet<string> { "SAP version" }, s => s, notes);
    }

    // ======================================================================== read

    sealed class Ctx : WbReadCtx
    {
        public FrameModel M, Base;
        public string Units;
        public readonly Dictionary<string, FrameObj> ById = new Dictionary<string, FrameObj>(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<FrameObj> Seen = new HashSet<FrameObj>();
        public readonly Dictionary<string, Dictionary<string, string>> Renamed = new Dictionary<string, Dictionary<string, string>>();   // kind -> old -> new
        public readonly Dictionary<string, HashSet<string>> Present = new Dictionary<string, HashSet<string>>();   // kind -> labels the sheets give

        // a label the base had and the sheets dropped: what named it goes with it (a deleted frame's loads, group rows)
        public bool Gone(string kind, string label)
        {
            if (Labels(kind).Contains(label)) return false;
            foreach (var o in Base.All()) if (o.Kind == kind && o.Label == label) return true;
            return false;
        }
        public HashSet<string> Labels(string kind) { HashSet<string> h; if (!Present.TryGetValue(kind, out h)) Present[kind] = h = new HashSet<string>(StringComparer.Ordinal); return h; }
        // a reference: the label, or the new one when the sheets renamed it
        public string Ref(string kind, string label)
        {
            Dictionary<string, string> m; string n;
            if (Labels(kind).Contains(label)) return label;
            return Renamed.TryGetValue(kind, out m) && m.TryGetValue(label, out n) ? n : label;
        }
    }

    public static FrameWorkbookResult Read(string path)
    {
        var res = new FrameWorkbookResult { Path = path ?? "" };
        var sheets = CaesarWorkbook.Read(path);
        Func<string, CaesarSheet> sheet = n => sheets.FirstOrDefault(s => string.Equals(s.Name, n, StringComparison.OrdinalIgnoreCase));
        WbFormula.Resolve(sheets, Inputs);
        var x = new Ctx();

        var pl = WbBase.Read(sheet("_pluto"), Format);
        if (pl == null) throw new InvalidOperationException("FrameWorkbook: " + Path.GetFileName(path) + " has no hidden base model (_pluto): make it with Build-Frame.ps1 -NewWorkbook, or -Xlsx from a model");
        if (pl.Version > Version) throw new InvalidOperationException("FrameWorkbook: " + Path.GetFileName(path) + " is workbook version " + pl.Version + ", newer than this reader (" + Version + ")");
        try { res.Base = FrameModelJson.Read(pl.Json); }
        catch (Exception ex) { throw new InvalidOperationException("FrameWorkbook: the hidden base model (_pluto) does not read (" + ex.Message + "): make the workbook again from its source"); }
        res.BaseHash = pl.Hash; res.SourceName = pl.Source;
        if (WbBase.Sha(FrameModelJson.Write(res.Base)) != pl.Hash) res.Warnings.Add("the hidden base model does not match its fingerprint: it was changed outside Pluto");
        var m = FrameModelJson.Read(pl.Json);
        x.M = m; x.Base = res.Base; x.Units = m.Units;
        foreach (var o in m.All()) x.ById[Id(o)] = o;

        ReadSettings(sheet("Settings"), x);
        ReadMaterials(sheet(SMaterials), x);
        ReadSections(sheet(SSections), x);
        ReadJoints(sheet(SJoints), x);
        var rel = ReadPresets(sheet(SReleases), x, true);
        var mod = ReadPresets(sheet(SModifiers), x, false);
        var kindOf = new Dictionary<string, string>(StringComparer.Ordinal); var lineOf = new Dictionary<string, string>(StringComparer.Ordinal);
        var order = ReadFrames(sheet(SFrames), x, rel, mod, kindOf, lineOf);
        ReadLinkProps(sheet(SLinkProps), x);
        ReadLinks(sheet(SLinks), x);
        ReadSprings(sheet(SSprings), x);
        ReadPatterns(sheet(SPatterns), x);
        ReadGroups(sheet(SGroups), x, kindOf, lineOf);
        ReadJointLoads(sheet(SJointLoads), x, FrameLoad.JointForce);
        ReadJointLoads(sheet(SGround), x, FrameLoad.GroundDisp);
        ReadFrameLoads(sheet(SPoint), x, FrameLoad.Point, order, kindOf, lineOf);
        ReadFrameLoads(sheet(SDist), x, FrameLoad.Distributed, order, kindOf, lineOf);
        ReadFrameLoads(sheet(STemp), x, FrameLoad.Temperature, order, kindOf, lineOf);
        ReadCases(sheet(SCases), x);
        ReadCombos(sheet(SCombos), x);

        Refit(m, res.Base);

        // what the sheets no longer have: deleted, users first (a refusal names what still uses it)
        Func<FrameObj, int> rank = o => o is FrameLoad ? 0 : o is FrameLink ? 1 : o is FrameMember ? 2 : o is FrameCombo ? 3 : o is FrameCase ? 4 : o is FrameJoint ? 5 :
                                        o is FrameLinkProp ? 6 : o is FrameSection ? 7 : o is FrameMaterial ? 8 : o is FramePattern ? 9 : 10;
        foreach (var o in m.All().Where(o => !(o is FrameGroup) && !x.Seen.Contains(o) && SheetOf(o, sheet) != null).OrderBy(rank).ToList())
        {
            try { FrameS2k.Delete(m, o); }
            catch (InvalidOperationException ex) { x.Err(SheetOf(o, sheet) + ": " + Id(o) + " " + o.Label + " is gone from the sheet, but " + ex.Message.Replace("FrameS2k: ", "")); }
        }
        if (x.Errors.Count > 0)
            throw new InvalidOperationException("FrameWorkbook: " + Path.GetFileName(path) + ": " + x.Errors.Count + " problem(s): " + string.Join("; ", x.Errors.Take(40).ToArray()));
        res.Warnings.AddRange(x.Warnings);
        res.Model = m;
        res.ModelHash = FrameModelJson.Hash(m);
        return res;
    }

    // SAP's export gives frames and links their Length and centroid: where an object's joints moved (or it got others),
    // they are worked out again, so nobody reading the .s2k is misled (SAP recomputes them anyway); untouched rows keep
    // their text
    static void Refit(FrameModel m, FrameModel b)
    {
        var now = new Dictionary<string, double[]>(StringComparer.Ordinal); var was = new Dictionary<string, double[]>(StringComparer.Ordinal);
        foreach (var j in m.Joints) now[j.Label] = j.Xyz;
        foreach (var j in b.Joints) was[j.Label] = j.Xyz;
        var old = new Dictionary<int, string[]>();
        foreach (var f in b.Frames) old[f.Uid] = new[] { f.JointI, f.JointJ };
        foreach (var l in b.Links) old[l.Uid] = new[] { l.JointI, l.JointJ == "" ? l.JointI : l.JointJ };
        Action<FrameObj, FrameRec, string, string> fit = (o, rec, ji, jj) =>
        {
            if (rec == null || !(rec.Has("Length") || rec.Has("CentroidX"))) return;
            double[] a, z, a0, z0; string[] o0;
            if (!now.TryGetValue(ji, out a) || !now.TryGetValue(jj, out z)) return;
            if (old.TryGetValue(o.Uid, out o0) && o0[0] == ji && o0[1] == jj && was.TryGetValue(ji, out a0) && was.TryGetValue(jj, out z0) &&
                Enumerable.Range(0, 3).All(k => WbBase.Same(a[k], a0[k]) && WbBase.Same(z[k], z0[k]))) return;
            double len = Math.Sqrt((z[0] - a[0]) * (z[0] - a[0]) + (z[1] - a[1]) * (z[1] - a[1]) + (z[2] - a[2]) * (z[2] - a[2]));
            Func<double, double> r9 = v => Math.Round(v, 9);
            if (rec.Has("Length")) rec.SetNum("Length", r9(len));
            string[] cn = { "CentroidX", "CentroidY", "CentroidZ" };
            for (int k = 0; k < 3; k++) if (rec.Has(cn[k])) rec.SetNum(cn[k], r9((a[k] + z[k]) / 2));
        };
        foreach (var f in m.Frames) fit(f, f.Rec(FrameMember.Conn), f.JointI, f.JointJ);
        foreach (var l in m.Links) fit(l, l.Rec(FrameLink.Conn), l.JointI, l.JointJ == "" ? l.JointI : l.JointJ);
    }

    // the sheet an object's rows are on (null: a sheet the workbook does not have, so nothing is deleted)
    static string SheetOf(FrameObj o, Func<string, CaesarSheet> sheet)
    {
        string n = o is FrameJoint ? SJoints : o is FrameMember ? SFrames : o is FrameSection ? SSections : o is FrameMaterial ? SMaterials : o is FrameLinkProp ? SLinkProps :
                   o is FrameLink ? SLinks : o is FramePattern ? SPatterns : o is FrameCase ? SCases : o is FrameCombo ? SCombos : null;
        var l = o as FrameLoad;
        if (l != null) n = l.Table == FrameLoad.JointForce ? SJointLoads : l.Table == FrameLoad.GroundDisp ? SGround : l.Table == FrameLoad.Point ? SPoint : l.Table == FrameLoad.Distributed ? SDist : STemp;
        return n != null && sheet(n) != null ? n : null;
    }

    // ---- cells

    static string Text(WbCols c, int r, string col, Ctx x) { return WbRead.Text(c, r, col, x).Trim(); }
    static string At(WbCols c, int r, string col) { int k; return c.S.Name + "!" + (c.At.TryGetValue(col, out k) ? XlsxBook.Ref(r, k) : "row " + (r + 1)); }

    // a number in the model's units (the header's unit converted for dim; NaN when blank)
    static double Num(WbCols c, int r, string col, Ctx x, string dim)
    {
        string unit;
        double v = WbRead.Raw(c, r, col, x, out unit);
        if (double.IsNaN(v) || unit == null) return v;
        if (dim == null) { x.Err(c.S.Name + "!" + XlsxBook.Ref(0, c.At[col]) + ": this column takes no unit (\"" + unit + "\"): the values are as SAP has them"); return double.NaN; }
        double f = FrameUnits.Factor(unit, dim, x.Units);
        if (double.IsNaN(f)) { x.Err(c.S.Name + "!" + XlsxBook.Ref(0, c.At[col]) + ": the unit \"" + unit + "\" (this column takes " + FrameUnits.Label(dim, x.Units) + " or another unit of that kind)"); return double.NaN; }
        return FrameUnits.Converted(v, f);
    }

    // a column whose unit depends on the row: "Value (kip | kip-in)" (a force row, a moment row), "Distance (in)" (an Abs
    // row; a Rel row is a fraction). which: 0 the first unit, 1 the second; dim: that row's dimension (null: no unit)
    static double NumBy(WbCols c, int r, string col, Ctx x, string dim, int which)
    {
        string unit;
        double v = WbRead.Raw(c, r, col, x, out unit);
        if (double.IsNaN(v) || unit == null || dim == null) return v;
        string[] parts = unit.Split('|');
        string u = (parts.Length > 1 ? parts[Math.Min(which, parts.Length - 1)] : parts[0]).Trim();
        double f = FrameUnits.Factor(u, dim, x.Units);
        if (double.IsNaN(f)) { x.Err(At(c, r, col) + ": the header's unit \"" + u + "\" is not a " + FrameUnits.Label(dim, x.Units) + " unit, which this row needs"); return double.NaN; }
        return FrameUnits.Converted(v, f);
    }

    // " (W10X33?)": the closest name, when one is close (case aside, or two letters off)
    static string Hint(string v, IEnumerable<string> names)
    {
        string best = null; int bd = 3;
        foreach (string n in names)
        {
            if (string.Equals(n, v, StringComparison.OrdinalIgnoreCase)) return " (" + n + "?)";
            int d = Distance(n.ToUpperInvariant(), v.ToUpperInvariant());
            if (d < bd) { bd = d; best = n; }
        }
        return best != null ? " (" + best + "?)" : "";
    }
    static int Distance(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 2) return 99;
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }

    static bool YesNo(WbCols c, int r, string col, Ctx x, bool dflt)
    {
        string s = Text(c, r, col, x).ToLowerInvariant();
        if (s.Length == 0) return dflt;
        if (s == "yes" || s == "y" || s == "true" || s == "1") return true;
        if (s == "no" || s == "n" || s == "false" || s == "0") return false;
        x.Err(At(c, r, col) + ": yes or no (\"" + s + "\")");
        return dflt;
    }

    // the object a row's Id names (of type T, not taken by an earlier row); a copied Id is read as new, with a warning
    static T Match<T>(WbCols c, int r, Ctx x) where T : FrameObj
    {
        string id = Text(c, r, "Id", x);
        if (id.Length == 0) return null;
        FrameObj o;
        if (!x.ById.TryGetValue(id, out o) || !(o is T)) { x.Warnings.Add(At(c, r, "Id") + ": " + id + " is no object of this sheet's: read as new"); return null; }
        if (x.Seen.Contains(o)) { x.Warnings.Add(At(c, r, "Id") + ": " + id + " is on an earlier row too (a copied row): read as new"); return null; }
        return (T)o;
    }

    // the row's object: the Id's, renamed to the row's label, or a new one
    static T Take<T>(WbCols c, int r, Ctx x, string kind, string label, string labelCol, Func<T> make) where T : FrameObj
    {
        var o = Match<T>(c, r, x);
        if (!x.Labels(kind).Add(label)) x.Err(At(c, r, labelCol) + ": the " + kind + " " + label + " is on an earlier row too");
        if (o == null) { o = make(); x.M.Add(o, label); }
        else if (o.Label != label)
        {
            Dictionary<string, string> rn;
            if (!x.Renamed.TryGetValue(kind, out rn)) x.Renamed[kind] = rn = new Dictionary<string, string>(StringComparer.Ordinal);
            rn[o.Label] = label;
            FrameS2k.Rename(x.M, o, label);
        }
        x.Seen.Add(o);
        return o;
    }

    static IEnumerable<int> Rows(WbCols c) { for (int r = 1; r < c.Rows; r++) if (!WbRead.Blank(c.S, r)) yield return r; }

    // ---- sheets

    static void ReadSettings(CaesarSheet s, Ctx x)
    {
        if (s == null) return;
        for (int r = 1; r < s.Rows.Count; r++)
        {
            if (s.Text(r, 0).Trim() != "SAP version") continue;
            string v = s.Text(r, 1).Trim();
            if (v.Length > 0 && x.M.Control != null && x.M.Control.Get("Version") != v) x.M.Control.Set("Version", v);
        }
    }

    static void ReadMaterials(CaesarSheet s, Ctx x)
    {
        if (s == null) return;
        var c = WbRead.ColsOf(s, x, null);
        string[] mk = { "E1", "U12", "G12", "UnitWeight", "UnitMass", "A1" }, heads = { "E", "Poisson", "G", "Unit weight", "Unit mass", "Alpha" }, dims = { "F/L2", null, "F/L2", "F/L3", "F-s2/L4", "1/T" };
        foreach (int r in Rows(c))
        {
            string name = Text(c, r, "Name", x);
            if (name.Length == 0) { x.Err(At(c, r, "Name") + ": a material with no name"); continue; }
            var mt = Take<FrameMaterial>(c, r, x, "material", name, "Name", () => new FrameMaterial());
            string ty = Text(c, r, "Type", x); if (ty.Length > 0) mt.Type = ty;
            for (int k = 0; k < mk.Length; k++) { double v = Num(c, r, heads[k], x, dims[k]); if (!double.IsNaN(v)) mt.SetMech(mk[k], v); }
            double p = Num(c, r, "Poisson", x, null); if (!double.IsNaN(p) && (p < 0 || p > 0.5)) x.Err(At(c, r, "Poisson") + ": Poisson's ratio is 0 to 0.5");
            foreach (var fy in new[] { "Fy", "Fu" }) { double v = Num(c, r, fy, x, "F/L2"); if (!double.IsNaN(v)) mt.SetSteelData(fy, v); }
            // what a new material leaves blank and SAP needs: G from E and Poisson, the mass from the weight (never on a
            // material from the model: SAP's uniaxial ones, rebar and tendon, have no G)
            if (x.Base.Materials.Any(q => q.Uid == mt.Uid)) continue;
            double e = mt.Mech("E1"), nu = mt.Mech("U12"), w = mt.Mech("UnitWeight");
            if (mt.Mech("G12") == 0 && double.IsNaN(Num(c, r, "G", x, "F/L2")) && e > 0) mt.SetMech("G12", e / (2 * (1 + nu)));
            if (mt.Mech("UnitMass") == 0 && double.IsNaN(Num(c, r, "Unit mass", x, "F-s2/L4")) && w > 0) mt.SetMech("UnitMass", w / Gravity(x.Units));
        }
    }

    // g in the model's length unit per second squared
    static double Gravity(string units)
    {
        string f, l, t;
        FrameUnits.Parse(units, out f, out l, out t);
        double[] u = FrameUnits.Of(l);
        return u == null ? 9.80665 : 9.80665 / u[0];
    }

    static void ReadSections(CaesarSheet s, Ctx x)
    {
        if (s == null) return;
        var c = WbRead.ColsOf(s, x, null);
        string[] keys = { "t3", "t2", "tf", "tw", "t2b", "tfb", "dis", "Area", "I33", "I22", "TorsConst" }, heads = { "t3", "t2", "tf", "tw", "t2b", "tfb", "dis", "Area", "I33", "I22", "J" },
                 dims = { "L", "L", "L", "L", "L", "L", "L", "L2", "L4", "L4", "L4" };
        foreach (int r in Rows(c))
        {
            string name = Text(c, r, "Name", x);
            if (name.Length == 0) { x.Err(At(c, r, "Name") + ": a section with no name"); continue; }
            var sec = Take<FrameSection>(c, r, x, "section", name, "Name", () => new FrameSection());
            string shape = Text(c, r, "Shape", x), mat = x.Ref("material", Text(c, r, "Material", x));
            if (shape.Length > 0 && sec.Shape != shape) sec.Shape = shape;
            if (mat.Length > 0 && sec.Material != mat) sec.Material = mat;
            if (mat.Length > 0 && !x.Labels("material").Contains(mat)) x.Err(At(c, r, "Material") + ": no material " + mat + " on Materials" + Hint(mat, x.Labels("material")));
            for (int k = 0; k < keys.Length; k++) { double v = Num(c, r, heads[k], x, dims[k]); if (!double.IsNaN(v)) sec.SetDim(keys[k], v); }
            var g = sec.Rec(FrameSection.General);
            if (g != null && g.Yes("FromFile")) continue;          // a catalog section from SAP: SAP has its sizes
            bool isNew = !x.Base.Sections.Any(q => q.Uid == sec.Uid);
            if (isNew && Text(c, r, "Catalog", x).Length > 0) { x.Err(At(c, r, "Catalog") + ": a new section from a catalog is not written yet: give its dimensions (SAP 24 did not find AISC16.xml for the probe)"); continue; }
            string sk = sec.Shape.ToLowerInvariant();
            if (isNew && sk.StartsWith("i/") && !g.Has("t2b") && g.Has("t2")) sec.SetDim("t2b", sec.Dim("t2"));
            if (isNew && sk.StartsWith("i/") && !g.Has("tfb") && g.Has("tf")) sec.SetDim("tfb", sec.Dim("tf"));
            string[] need = sk == "pipe" ? new[] { "t3", "tw" } : sk == "rectangular" ? new[] { "t3", "t2" } : sk == "circle" ? new[] { "t3" } :
                            sk == "general" ? new[] { "Area", "I33", "I22", "TorsConst" } : sk == "double angle" ? new[] { "t3", "t2", "tf", "tw", "dis" } :
                            sk.Length == 0 ? new string[0] : new[] { "t3", "t2", "tf", "tw" };
            if (sk.Length == 0) { x.Err(At(c, r, "Shape") + ": the section " + name + " has no shape"); continue; }
            var missing = need.Where(k => g == null || g.Num(k, 0) <= 0).ToList();
            if (missing.Count > 0) x.Err(At(c, r, "Name") + ": a " + sec.Shape + " section needs " + string.Join(", ", missing.Select(k => k == "TorsConst" ? "J" : k).ToArray()) + " (above 0)");
        }
    }

    static void ReadJoints(CaesarSheet s, Ctx x)
    {
        if (s == null) return;
        var c = WbRead.ColsOf(s, x, null);
        foreach (int r in Rows(c))
        {
            string label = Text(c, r, "Joint", x);
            if (label.Length == 0) { x.Err(At(c, r, "Joint") + ": a joint with no label"); continue; }
            var j = Take<FrameJoint>(c, r, x, "joint", label, "Joint", () => new FrameJoint());
            double X = Num(c, r, "X", x, "L"), Y = Num(c, r, "Y", x, "L"), Z = Num(c, r, "Z", x, "L");
            var p = j.Xyz;
            double nx = double.IsNaN(X) ? 0 : X, ny = double.IsNaN(Y) ? 0 : Y, nz = double.IsNaN(Z) ? 0 : Z;
            if (!WbBase.Same(p[0], nx) || !WbBase.Same(p[1], ny) || !WbBase.Same(p[2], nz)) j.SetXyz(nx, ny, nz);
            j.Restraint = ParseRestraint(Text(c, r, "Restraint", x), At(c, r, "Restraint"), x);
            var a = new[] { Num(c, r, "Local A", x, "deg"), Num(c, r, "Local B", x, "deg"), Num(c, r, "Local C", x, "deg") }.Select(v => double.IsNaN(v) ? 0 : v).ToArray();
            var old = j.LocalAngles;
            if (!(WbBase.Same(old[0], a[0]) && WbBase.Same(old[1], a[1]) && WbBase.Same(old[2], a[2]))) j.LocalAngles = a;
        }
    }

    static bool[] ParseRestraint(string t, string at, Ctx x)
    {
        var o = new bool[6];
        string s = t.ToLowerInvariant();
        if (s.Length == 0) return o;
        if (s == "fixed" || s == "anchor") { for (int i = 0; i < 6; i++) o[i] = true; return o; }
        if (s == "pin" || s == "pinned") { o[0] = o[1] = o[2] = true; return o; }
        foreach (string tok in t.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            int k = Array.FindIndex(FrameJoint.Dofs, d => d.Equals(tok, StringComparison.OrdinalIgnoreCase));
            if (k < 0) { x.Err(at + ": \"" + tok + "\" (fixed, pin, or U1 U2 U3 R1 R2 R3)"); continue; }
            o[k] = true;
        }
        return o;
    }

    // Releases / Modifiers: name -> {flags, values}
    static Dictionary<string, Preset> ReadPresets(CaesarSheet s, Ctx x, bool releases)
    {
        var o = new Dictionary<string, Preset>(StringComparer.OrdinalIgnoreCase);
        if (releases) foreach (var bi in RelBuiltIn) { var p = new Preset { Name = (string)bi[0] }; foreach (int i in (int[])bi[1]) p.Rel[i] = true; o[p.Name] = p; }
        if (s == null) return o;
        var c = WbRead.ColsOf(s, x, null);
        string[] heads = releases ? RelHeads : ModHeads;
        foreach (int r in Rows(c))
        {
            string name = Text(c, r, "Name", x);
            if (name.Length == 0) { x.Err(At(c, r, "Name") + ": a preset with no name"); continue; }
            var p = new Preset { Name = name, Val = new double[releases ? 12 : 8] };
            for (int i = 0; i < heads.Length; i++)
            {
                string tx = Text(c, r, heads[i], x);
                if (!releases) { double v = Num(c, r, heads[i], x, null); p.Val[i] = double.IsNaN(v) ? 1 : v; continue; }
                if (tx.Length == 0) continue;
                double d;
                if (double.TryParse(tx, NumberStyles.Float, Inv, out d)) { p.Rel[i] = true; p.Val[i] = d; }
                else if (tx.Equals("yes", StringComparison.OrdinalIgnoreCase) || tx.Equals("y", StringComparison.OrdinalIgnoreCase) || tx.Equals("released", StringComparison.OrdinalIgnoreCase)) p.Rel[i] = true;
                else if (!tx.Equals("no", StringComparison.OrdinalIgnoreCase)) x.Err(At(c, r, heads[i]) + ": yes, a number (partly fixed) or blank (\"" + tx + "\")");
            }
            if (releases && (p.Rel[0] && p.Rel[6] || p.Rel[3] && p.Rel[9] || p.Rel[1] && p.Rel[7] || p.Rel[2] && p.Rel[8]) && !p.Val.Any(v => v != 0))
                x.Warnings.Add(At(c, r, "Name") + ": " + name + " releases P, V2, V3 or T at both ends: a mechanism in SAP");
            o[name] = p;
        }
        return o;
    }

    static List<string> ReadFrames(CaesarSheet s, Ctx x, Dictionary<string, Preset> rel, Dictionary<string, Preset> mod, Dictionary<string, string> kindOf, Dictionary<string, string> lineOf)
    {
        var order = new List<string>();
        if (s == null) return order;
        var c = WbRead.ColsOf(s, x, null);
        var m = x.M;
        // DX DY DZ were a delta entry (until 2026-11-05): a workbook still holding deltas would have them ignored
        foreach (string h in new[] { "DX", "DY", "DZ" })
        {
            int k;
            if (!c.At.TryGetValue(h, out k)) continue;
            var used = Rows(c).Where(r => s.Text(r, k).Trim().Length > 0).ToList();
            if (used.Count > 0)
                x.Err(s.Name + "!" + XlsxBook.Ref(used[0], k) + ": " + h + " is no longer read (" + used.Count + " row(s)): frames go between joints on Joints; give joint J its own row there (X = joint I's X + DX, as a formula if you like)");
        }
        foreach (int r in Rows(c))
        {
            string label = Text(c, r, "Frame", x);
            if (label.Length == 0) { x.Err(At(c, r, "Frame") + ": a frame with no label"); continue; }
            var f = Take<FrameMember>(c, r, x, "frame", label, "Frame", () => new FrameMember());
            order.Add(label);
            string ji = x.Ref("joint", Text(c, r, "Joint I", x)), jj = x.Ref("joint", Text(c, r, "Joint J", x));
            bool bad = false;
            foreach (var e in new[] { new[] { "Joint I", ji }, new[] { "Joint J", jj } })
                if (m.JointNamed(e[1]) == null || !x.Labels("joint").Contains(e[1])) { x.Err(At(c, r, e[0]) + ": no joint " + (e[1].Length > 0 ? e[1] : "(blank)") + " on Joints" + Hint(e[1], x.Labels("joint"))); bad = true; }
            if (bad) continue;
            if (ji == jj) { x.Err(At(c, r, "Joint J") + ": a frame from joint " + ji + " to itself"); continue; }
            if (f.JointI != ji) f.JointI = ji;
            if (f.JointJ != jj) f.JointJ = jj;
            string sec = x.Ref("section", Text(c, r, "Section", x));
            if (sec.Length == 0 || !x.Labels("section").Contains(sec)) x.Err(At(c, r, "Section") + ": no section " + (sec.Length > 0 ? sec : "(blank)") + " on Sections" + Hint(sec, x.Labels("section")));
            else f.Section = sec;
            string kd = Text(c, r, "Kind", x), ln = Text(c, r, "Line", x);
            if (kd.Length > 0) kindOf[label] = kd;
            if (ln.Length > 0) lineOf[label] = ln;
            string rn = Text(c, r, "Release", x);
            Preset p0;
            if (rn.Length == 0) rn = "FIXED";
            if (!rel.TryGetValue(rn, out p0)) x.Err(At(c, r, "Release") + ": no release preset " + rn + " on Releases" + Hint(rn, rel.Keys));
            else
            {
                var r12 = p0.Rel; var v12 = p0.Val;
                if (!f.Releases.SequenceEqual(r12)) f.Releases = r12;
                if (!f.PartialFixity.SequenceEqual(v12)) f.PartialFixity = v12;
            }
            double ang = Num(c, r, "Angle", x, "deg");
            if (!WbBase.Same(f.Angle, double.IsNaN(ang) ? 0 : ang)) f.Angle = double.IsNaN(ang) ? 0 : ang;
            string ins = Text(c, r, "Insertion", x);
            int cp = ins.Length == 0 ? 10 : LeadNumber(ins);
            if (cp < 1 || cp > 11) x.Err(At(c, r, "Insertion") + ": a cardinal point 1 to 11 (\"" + ins + "\")"); else f.CardinalPoint = cp;
            string mn = Text(c, r, "Modifiers", x);
            if (mn.Length == 0) { if (!f.PropertyModifiers.All(v => v == 1)) f.PropertyModifiers = null; }
            else if (!mod.TryGetValue(mn, out p0)) x.Err(At(c, r, "Modifiers") + ": no modifiers preset " + mn + " on Modifiers" + Hint(mn, mod.Keys));
            else if (!f.PropertyModifiers.SequenceEqual(p0.Val)) f.PropertyModifiers = p0.Val;
            ReadOffsets(c, r, x, f);
        }
        return order;
    }

    static int LeadNumber(string s) { int n = 0, i = 0; while (i < s.Length && char.IsDigit(s[i])) { n = n * 10 + (s[i] - '0'); i++; } return i == 0 ? -1 : n; }

    // a frame's joint offsets as the Frames sheet shows them, end 0 = I, 1 = J: "1 2 3" in local 1 2 3, as written; an
    // offset SAP holds in another system with that system first ("GLOBAL: 1 2 3"); "" for none
    static string OffsetText(FrameMember f, int end)
    {
        string sys = f.JointOffsetSys;
        if (sys.Length == 0) return "";
        var r = f.Rec(FrameMember.Insertion);
        var v = Enumerable.Range(0, 3).Select(i => r.Get(FrameMember.JtOffsetKeys[end * 3 + i]) ?? "0").ToArray();
        double d;
        if (v.All(q => double.TryParse(q, NumberStyles.Float, Inv, out d) && d == 0)) return "";
        string t = string.Join(" ", v);
        return sys.Equals("Local", StringComparison.OrdinalIgnoreCase) ? t : sys + ": " + t;
    }

    static void ReadOffsets(WbCols c, int r, Ctx x, FrameMember f)
    {
        if (!c.At.ContainsKey("Offset I") && !c.At.ContainsKey("Offset J")) return;
        string[] cell = { Text(c, r, "Offset I", x).Trim(), Text(c, r, "Offset J", x).Trim() };
        string sys = f.JointOffsetSys;
        bool local = sys.Length == 0 || sys.Equals("Local", StringComparison.OrdinalIgnoreCase);
        // an offset in another system, shown as it is: left as SAP has it
        if (!local && cell[0] == OffsetText(f, 0) && cell[1] == OffsetText(f, 1)) return;
        var want = new double[6];
        for (int e = 0; e < 2; e++)
        {
            string col = e == 0 ? "Offset I" : "Offset J", t = cell[e];
            if (t.Length == 0) continue;
            if (t.IndexOf(':') >= 0) { x.Err(At(c, r, col) + ": \"" + t + "\": only offsets along local 1 2 3 are edited here (three numbers); one in another system stays as SAP has it, so change it in SAP"); return; }
            var parts = t.Split(new[] { ' ', ',', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            double f0 = 1;
            string unit;
            if (c.Unit.TryGetValue(c.At[col], out unit)) f0 = FrameUnits.Factor(unit, "L", x.Units);
            if (double.IsNaN(f0)) { x.Err(c.S.Name + "!" + XlsxBook.Ref(0, c.At[col]) + ": the unit \"" + unit + "\" (this column takes " + FrameUnits.Label("L", x.Units) + " or another length)"); return; }
            for (int i = 0; i < 3; i++)
            {
                double v;
                if (parts.Length != 3 || !double.TryParse(parts[i], NumberStyles.Float, Inv, out v)) { x.Err(At(c, r, col) + ": three numbers along local 1 2 3 (\"" + t + "\")"); return; }
                want[e * 3 + i] = FrameUnits.Converted(v, f0);
            }
        }
        if (local && f.JointOffsets.SequenceEqual(want)) return;
        f.SetJointOffsets(want);
    }

    static void ReadLinkProps(CaesarSheet s, Ctx x)
    {
        if (s == null) return;
        var c = WbRead.ColsOf(s, x, null);
        string[] keys = { "KE", "CE", "K", "C", "CExp", "Open", "Mu slow", "Mu fast", "Rate", "Radius" }, flds = { "KE", "CE", "K", "C", "CExp", "Open", "Slow", "Fast", "Rate", "Radius" };
        FrameLinkProp cur = null;
        var done = new HashSet<FrameRec>();
        // the DOF rows the sheet no longer has go, with their curve points
        Action finish = () =>
        {
            if (cur == null) return;
            var kept = new HashSet<string>(done.Select(d => (d.Get("DOF") ?? "").ToUpperInvariant()));
            cur.Recs.RemoveAll(q => q.Has("DOF") && q.Table != FrameLinkProp.General && !done.Contains(q) && !(FrameLinkProp.IsPointRow(q) && kept.Contains((q.Get("DOF") ?? "").ToUpperInvariant())));
        };
        foreach (int r in Rows(c))
        {
            string name = Text(c, r, "Name", x);
            if (name.Length > 0)
            {
                finish();
                cur = Take<FrameLinkProp>(c, r, x, "linkprop", name, "Name", () => new FrameLinkProp());
                string ty = Text(c, r, "Type", x);
                if (ty.Length > 0 && ty != cur.LinkType)
                {
                    bool had = cur.LinkType.Length > 0;
                    cur.LinkType = ty;
                    // a type changed: its DOF rows (and points) move to the new type's table
                    if (had) foreach (var q in cur.Recs) if (q.Has("DOF") && q.Table != FrameLinkProp.General) q.Table = FrameLinkProp.DofTable(ty);
                }
            }
            else if (cur == null) { x.Err(At(c, r, "Name") + ": a DOF row with no property above it"); continue; }
            string dof = Text(c, r, "DOF", x).ToUpperInvariant();
            if (dof.Length == 0) continue;
            if (Array.IndexOf(DofNames, dof) < 0) { x.Err(At(c, r, "DOF") + ": U1 U2 U3 R1 R2 R3 (\"" + dof + "\")"); continue; }
            var d = cur.DofRow(dof) ?? cur.AddDofRow(dof);
            if (done.Contains(d)) { x.Err(At(c, r, "DOF") + ": " + dof + " twice in " + cur.Label); continue; }
            done.Add(d);
            bool rot = dof.StartsWith("R");
            d.SetYes("Fixed", YesNo(c, r, "Fixed", x, false));
            if (c.At.ContainsKey("NonLinear") && (d.Has("NonLinear") || Text(c, r, "NonLinear", x).Length > 0))
            {
                // SAP's order: Link DOF Fixed NonLinear ...
                if (!d.Has("NonLinear")) d.F.Insert(d.F.FindIndex(q => q[0] == "Fixed") + 1, new[] { "NonLinear", "No" });
                d.SetYes("NonLinear", YesNo(c, r, "NonLinear", x, false));
            }
            for (int k = 0; k < keys.Length; k++)
            {
                double v = Num(c, r, keys[k], x, null);
                string fld = LinkField(flds[k], rot, d);
                if (double.IsNaN(v)) continue;
                // an opening in the old Open field (SAP ignores it) moves to TransOpen / RotOpen when it changes
                if (fld == "Open" && d.Num("Open", double.NaN) != v) { d.Remove("Open"); fld = rot ? "RotOpen" : "TransOpen"; }
                d.SetNum(fld, v);
            }
            ReadPoints(c, r, x, cur, d, dof);
        }
        finish();
    }

    // a Link props column's field on a DOF row: KE CE K C carry Trans / Rot; CExp is plain (SAP 24.2, probe round 2); the
    // opening is TransOpen / RotOpen (or an older file's Open); the friction fields are plain
    static string LinkField(string key, bool rot, FrameRec d)
    {
        if (key == "KE" || key == "CE" || key == "K" || key == "C") return (rot ? "Rot" : "Trans") + key;
        return key == "Open" ? FrameLinkProp.OpenField(d) : key;
    }

    // a multilinear DOF's curve: "force, displacement; ..." (SAP: at least three points, else its import makes the DOF
    // linear; displacements increasing; through the origin)
    static void ReadPoints(WbCols c, int r, Ctx x, FrameLinkProp p, FrameRec d, string dof)
    {
        if (!c.At.ContainsKey("Points")) return;
        string t = Text(c, r, "Points", x).Trim();
        var pts = new List<double[]>();
        foreach (string pair in t.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var q = pair.Split(new[] { ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            double fo, di;
            if (q.Length != 2 || !double.TryParse(q[0], NumberStyles.Float, Inv, out fo) || !double.TryParse(q[1], NumberStyles.Float, Inv, out di))
            { x.Err(At(c, r, "Points") + ": force, displacement pairs separated by ';' (\"" + pair.Trim() + "\")"); return; }
            pts.Add(new[] { fo, di });
        }
        bool multi = FrameLinkProp.IsMultilinear(p.LinkType);
        if (pts.Count > 0 && !multi) { x.Err(At(c, r, "Points") + ": a curve is for a MultiLinear property (" + p.Label + " is " + p.LinkType + ")"); return; }
        if (multi && d.Yes("NonLinear") && !d.Yes("Fixed"))
        {
            if (pts.Count < 3) { x.Err(At(c, r, "Points") + ": " + p.Label + " " + dof + " is nonlinear: SAP needs at least three force, displacement points (with fewer its import makes the DOF linear)"); return; }
            for (int i = 1; i < pts.Count; i++)
                if (pts[i][1] <= pts[i - 1][1]) { x.Err(At(c, r, "Points") + ": the displacements must increase (" + FrameModel.Fmt(pts[i - 1][1]) + " then " + FrameModel.Fmt(pts[i][1]) + ")"); return; }
            if (!pts.Any(q => q[0] == 0 && q[1] == 0)) x.Warnings.Add(At(c, r, "Points") + ": " + p.Label + " " + dof + "'s curve does not pass through 0, 0, which SAP's multilinear elastic link expects");
        }
        var have = p.Points(dof);
        double hv;
        bool same = have.Count == pts.Count && have.Select((q, i) => double.TryParse(q[0], NumberStyles.Float, Inv, out hv) && hv == pts[i][0] &&
                                                                     double.TryParse(q[1], NumberStyles.Float, Inv, out hv) && hv == pts[i][1]).All(b => b);
        if (!same) p.SetPoints(dof, pts);
    }

    static void ReadLinks(CaesarSheet s, Ctx x)
    {
        if (s == null) return;
        var c = WbRead.ColsOf(s, x, null);
        foreach (int r in Rows(c))
        {
            string label = Text(c, r, "Link", x);
            if (label.Length == 0) { x.Err(At(c, r, "Link") + ": a link with no label"); continue; }
            var l = Take<FrameLink>(c, r, x, "link", label, "Link", () => new FrameLink());
            string ji = x.Ref("joint", Text(c, r, "Joint I", x)), jj = x.Ref("joint", Text(c, r, "Joint J", x)), pr = x.Ref("linkprop", Text(c, r, "Property", x));
            if (!x.Labels("joint").Contains(ji)) { x.Err(At(c, r, "Joint I") + ": no joint " + ji); continue; }
            if (jj.Length > 0 && !x.Labels("joint").Contains(jj)) { x.Err(At(c, r, "Joint J") + ": no joint " + jj); continue; }
            l.SetJoints(ji, jj);
            if (!x.Labels("linkprop").Contains(pr)) x.Err(At(c, r, "Property") + ": no link property " + pr + " on Link props" + Hint(pr, x.Labels("linkprop"))); else l.Property = pr;
            double a = Num(c, r, "Angle", x, "deg");
            if (!WbBase.Same(l.Angle, double.IsNaN(a) ? 0 : a)) l.Angle = double.IsNaN(a) ? 0 : a;
        }
    }

    static void ReadSprings(CaesarSheet s, Ctx x)
    {
        if (s == null) return;
        var c = WbRead.ColsOf(s, x, null);
        var had = new HashSet<FrameJoint>();
        foreach (int r in Rows(c))
        {
            string jl = x.Ref("joint", Text(c, r, "Joint", x));
            var j = x.M.JointNamed(jl);
            if (x.Gone("joint", jl)) { x.Warnings.Add(At(c, r, "Joint") + ": joint " + jl + " is deleted: its spring goes with it"); continue; }
            if (j == null || !x.Labels("joint").Contains(jl)) { x.Err(At(c, r, "Joint") + ": no joint " + jl); continue; }
            if (!had.Add(j)) { x.Err(At(c, r, "Joint") + ": joint " + jl + " on an earlier row too"); continue; }
            var v = new double[6];
            for (int i = 0; i < 6; i++) { double d = Num(c, r, FrameJoint.Dofs[i], x, i < 3 ? "F/L" : "F-L/rad"); v[i] = double.IsNaN(d) ? 0 : d; }
            j.Spring = v;
            string cs = Text(c, r, "CoordSys", x);
            var sr = j.Rec(FrameJoint.Springs);
            if (sr != null && cs.Length > 0 && sr.Get("CoordSys") != cs) sr.Set("CoordSys", cs);
        }
        foreach (var j in x.M.Joints) if (!had.Contains(j) && j.Rec(FrameJoint.Springs) != null) j.Spring = null;
    }

    static void ReadPatterns(CaesarSheet s, Ctx x)
    {
        if (s == null) return;
        var c = WbRead.ColsOf(s, x, null);
        foreach (int r in Rows(c))
        {
            string name = Text(c, r, "Name", x);
            if (name.Length == 0) { x.Err(At(c, r, "Name") + ": a pattern with no name"); continue; }
            var p = Take<FramePattern>(c, r, x, "pattern", name, "Name", () => new FramePattern());
            string ty = Text(c, r, "Type", x); if (ty.Length > 0) p.DesignType = ty;
            double sw = Num(c, r, "Self weight", x, null);
            if (!WbBase.Same(p.SelfWeight, double.IsNaN(sw) ? 0 : sw)) p.SelfWeight = double.IsNaN(sw) ? 0 : sw;
        }
    }

    static void ReadGroups(CaesarSheet s, Ctx x, Dictionary<string, string> kindOf, Dictionary<string, string> lineOf)
    {
        var m = x.M;
        var want = new Dictionary<string, List<string[]>>(StringComparer.OrdinalIgnoreCase);
        var orderG = new List<string>();
        if (s != null)
        {
            var c = WbRead.ColsOf(s, x, null);
            foreach (int r in Rows(c))
            {
                string g = Text(c, r, "Group", x), ty = Text(c, r, "Object type", x), lb = Text(c, r, "Label", x);
                if (g.Length == 0) { x.Err(At(c, r, "Group") + ": a member with no group"); continue; }
                if (!want.ContainsKey(g)) { want[g] = new List<string[]>(); orderG.Add(g); }
                if (ty.Length == 0 && lb.Length == 0) continue;
                string kind = ty.ToLowerInvariant();
                if (kind != "joint" && kind != "frame" && kind != "link" && kind != "area") { x.Err(At(c, r, "Object type") + ": Joint, Frame, Link or Area (\"" + ty + "\")"); continue; }
                if (kind != "area") lb = x.Ref(kind, lb);
                if (kind != "area" && x.Gone(kind, lb)) { x.Warnings.Add(At(c, r, "Label") + ": the " + kind + " " + lb + " is deleted: dropped from " + g); continue; }
                if (kind != "area" && !x.Labels(kind).Contains(lb)) { x.Err(At(c, r, "Label") + ": no " + kind + " " + lb); continue; }
                want[g].Add(new[] { char.ToUpperInvariant(ty[0]) + ty.Substring(1).ToLowerInvariant(), lb });
            }
        }
        // the PL_ groups from Kind / Line
        foreach (var pair in new[] { new KeyValuePair<string, Dictionary<string, string>>("PL_KIND_", kindOf), new KeyValuePair<string, Dictionary<string, string>>("PL_LINE_", lineOf) })
        {
            foreach (var g in m.Groups.Where(q => q.Label.StartsWith(pair.Key, StringComparison.OrdinalIgnoreCase))) if (!want.ContainsKey(g.Label)) { want[g.Label] = new List<string[]>(); orderG.Add(g.Label); }
            foreach (var f in m.Frames)
            {
                string v;
                if (!pair.Value.TryGetValue(f.Label, out v)) continue;
                string g = pair.Key + v;
                if (!want.ContainsKey(g)) { want[g] = new List<string[]>(); orderG.Add(g); }
                want[g].Add(new[] { "Frame", f.Label });
            }
        }
        foreach (string g in orderG)
        {
            var grp = m.Groups.FirstOrDefault(q => string.Equals(q.Label, g, StringComparison.OrdinalIgnoreCase));
            bool pl = g.StartsWith("PL_", StringComparison.OrdinalIgnoreCase);
            if (grp == null) { if (pl && want[g].Count == 0) continue; grp = m.Add(new FrameGroup(), g); }
            if (pl && want[g].Count == 0) { FrameS2k.Delete(m, grp); continue; }
            grp.SetMembers(want[g]);
        }
        if (s != null)
            foreach (var g in m.Groups.Where(q => !want.ContainsKey(q.Label) && !q.Label.StartsWith("PL_", StringComparison.OrdinalIgnoreCase) && !q.Label.Equals("ALL", StringComparison.OrdinalIgnoreCase)).ToList())
                FrameS2k.Delete(m, g);
    }

    static void ReadJointLoads(CaesarSheet s, Ctx x, string table)
    {
        if (s == null) return;
        var c = WbRead.ColsOf(s, x, null);
        bool disp = table == FrameLoad.GroundDisp;
        string[] keys = disp ? new[] { "U1", "U2", "U3", "R1", "R2", "R3" } : new[] { "F1", "F2", "F3", "M1", "M2", "M3" };
        string[] dims = disp ? new[] { "L", "L", "L", "rad", "rad", "rad" } : new[] { "F", "F", "F", "F-L", "F-L", "F-L" };
        foreach (int r in Rows(c))
        {
            string jl = x.Ref("joint", Text(c, r, "Joint", x)), pat = x.Ref("pattern", Text(c, r, "Pattern", x));
            if (x.Gone("joint", jl)) { x.Warnings.Add(At(c, r, "Joint") + ": joint " + jl + " is deleted: its load goes with it"); continue; }
            if (!x.Labels("joint").Contains(jl)) { x.Err(At(c, r, "Joint") + ": no joint " + jl); continue; }
            if (!x.Labels("pattern").Contains(pat)) { x.Err(At(c, r, "Pattern") + ": no load pattern " + pat + Hint(pat, x.Labels("pattern"))); continue; }
            var ld = Match<FrameLoad>(c, r, x);
            if (ld != null && ld.Table != table) ld = null;
            if (ld == null) { ld = x.M.AddLoad(table); var row = ld.Row; row.Set("Joint", jl).Set("LoadPat", pat).Set("CoordSys", "GLOBAL"); foreach (var k in keys) row.Set(k, "0"); }
            x.Seen.Add(ld);
            var rw = ld.Row;
            if (rw.Get("Joint") != jl) rw.Set("Joint", jl);
            if (rw.Get("LoadPat") != pat) rw.Set("LoadPat", pat);
            string cs = Text(c, r, "CoordSys", x); if (cs.Length > 0 && rw.Get("CoordSys") != cs) rw.Set("CoordSys", cs);
            for (int k = 0; k < 6; k++) { double v = Num(c, r, keys[k], x, dims[k]); rw.SetNum(keys[k], double.IsNaN(v) ? 0 : v); }
        }
    }

    static void ReadFrameLoads(CaesarSheet s, Ctx x, string table, List<string> order, Dictionary<string, string> kindOf, Dictionary<string, string> lineOf)
    {
        if (s == null) return;
        var c = WbRead.ColsOf(s, x, null);
        var m = x.M;
        var pos = new Dictionary<string, double[]>(StringComparer.Ordinal);
        foreach (var j in m.Joints) pos[j.Label] = j.Xyz;
        Func<FrameMember, double> len = f => { double[] a, b; return pos.TryGetValue(f.JointI, out a) && pos.TryGetValue(f.JointJ, out b) ? Math.Sqrt((b[0] - a[0]) * (b[0] - a[0]) + (b[1] - a[1]) * (b[1] - a[1]) + (b[2] - a[2]) * (b[2] - a[2])) : 0; };
        foreach (int r in Rows(c))
        {
            string sel = Text(c, r, "Frames", x), pat = x.Ref("pattern", Text(c, r, "Pattern", x));
            // a single frame renamed on Frames: follow it
            string one = x.Ref("frame", sel);
            string err;
            var frames = FrameSelect.Frames(m, one, order, kindOf, lineOf, out err);
            if (err.Length > 0) { x.Err(At(c, r, "Frames") + ": " + err + (one.IndexOf(':') < 0 && one.IndexOf(',') < 0 ? Hint(one, x.Labels("frame")) : "")); continue; }
            // only frames the Frames sheet still has: one named outright and gone is an error, a group's are dropped
            var gone = frames.Where(f => !x.Labels("frame").Contains(f.Label)).ToList();
            if (gone.Count > 0 && frames.Count == 1)
            {
                if (x.Gone("frame", gone[0].Label)) x.Warnings.Add(At(c, r, "Frames") + ": frame " + gone[0].Label + " is deleted: its load goes with it");
                else x.Err(At(c, r, "Frames") + ": frame " + gone[0].Label + " is on no Frames row");
                continue;
            }
            frames = frames.Except(gone).ToList();
            if (frames.Count == 0) { x.Err(At(c, r, "Frames") + ": no frames"); continue; }
            if (!x.Labels("pattern").Contains(pat)) { x.Err(At(c, r, "Pattern") + ": no load pattern " + pat + Hint(pat, x.Labels("pattern"))); continue; }
            var first = Match<FrameLoad>(c, r, x);
            if (first != null && first.Table != table) first = null;
            for (int i = 0; i < frames.Count; i++)
            {
                var f = frames[i];
                var ld = i == 0 ? first : null;
                if (ld == null) { ld = m.AddLoad(table); NewLoadRow(ld.Row, table, f.Label, pat); }
                x.Seen.Add(ld);
                var rw = ld.Row;
                if (rw.Get("Frame") != f.Label) rw.Set("Frame", f.Label);
                if (rw.Get("LoadPat") != pat) rw.Set("LoadPat", pat);
                if (table == FrameLoad.Temperature)
                {
                    string ty = Text(c, r, "Type", x); if (ty.Length > 0 && rw.Get("Type") != ty) rw.Set("Type", ty);
                    double v = Num(c, r, "Value", x, "T"); rw.SetNum("Temp", double.IsNaN(v) ? 0 : v);
                    continue;
                }
                string cs = Text(c, r, "CoordSys", x), type = Text(c, r, "Type", x), dir = Text(c, r, "Dir", x), dt = Text(c, r, "Rel / Abs", x);
                if (cs.Length > 0 && rw.Get("CoordSys") != cs) rw.Set("CoordSys", cs);
                if (type.Length > 0 && rw.Get("Type") != type) rw.Set("Type", type);
                if (dir.Length > 0 && rw.Get("Dir") != dir) rw.Set("Dir", dir);
                bool rel = !dt.StartsWith("Abs", StringComparison.OrdinalIgnoreCase);
                string distType = rel ? "RelDist" : "AbsDist";
                if (rw.Get("DistType") != distType) rw.Set("DistType", distType);
                double L = len(f);
                Action<string, string, double, double> place = (relKey, absKey, v, dflt) =>
                {
                    double d = double.IsNaN(v) ? dflt : v;
                    if (rel) { if (d < 0 || d > 1) x.Err(At(c, r, "Rel / Abs") + ": a relative distance is 0 to 1 (" + FrameModel.Fmt(d) + ")"); rw.SetNum(relKey, d); rw.SetNum(absKey, d * L); }
                    else { rw.SetNum(absKey, d); rw.SetNum(relKey, L > 0 ? d / L : 0); }
                };
                bool moment = type.Equals("Moment", StringComparison.OrdinalIgnoreCase);
                if (table == FrameLoad.Point)
                {
                    place("RelDist", "AbsDist", NumBy(c, r, "Distance", x, rel ? null : "L", 0), 0);
                    double v = NumBy(c, r, "Value", x, moment ? "F-L" : "F", moment ? 1 : 0);
                    string vk = moment ? "Moment" : "Force", other = moment ? "Force" : "Moment";
                    if (rw.Has(other)) rw.Remove(other);
                    rw.SetNum(vk, double.IsNaN(v) ? 0 : v);
                }
                else
                {
                    place("RelDistA", "AbsDistA", NumBy(c, r, "Start", x, rel ? null : "L", 0), 0);
                    place("RelDistB", "AbsDistB", NumBy(c, r, "End", x, rel ? null : "L", 0), rel ? 1 : L);
                    double a = NumBy(c, r, "Value A", x, moment ? "F-L/L" : "F/L", moment ? 1 : 0), b2 = NumBy(c, r, "Value B", x, moment ? "F-L/L" : "F/L", moment ? 1 : 0);
                    if (double.IsNaN(b2)) b2 = a;
                    string pa = moment ? "MOverLA" : "FOverLA", pb = moment ? "MOverLB" : "FOverLB";
                    foreach (var o in moment ? new[] { "FOverLA", "FOverLB" } : new[] { "MOverLA", "MOverLB" }) if (rw.Has(o)) rw.Remove(o);
                    rw.SetNum(pa, double.IsNaN(a) ? 0 : a); rw.SetNum(pb, double.IsNaN(b2) ? 0 : b2);
                }
            }
        }
    }

    // a new load row in SAP's field order
    static void NewLoadRow(FrameRec row, string table, string frame, string pat)
    {
        row.Set("Frame", frame).Set("LoadPat", pat);
        if (table == FrameLoad.Temperature) { row.Set("Type", "Temperature").Set("Temp", "0"); return; }
        row.Set("CoordSys", "GLOBAL").Set("Type", "Force").Set("Dir", "Gravity").Set("DistType", "RelDist");
        if (table == FrameLoad.Point) row.Set("RelDist", "0").Set("AbsDist", "0").Set("Force", "0");
        else row.Set("RelDistA", "0").Set("RelDistB", "1").Set("AbsDistA", "0").Set("AbsDistB", "0").Set("FOverLA", "0").Set("FOverLB", "0");
    }

    static void ReadCases(CaesarSheet s, Ctx x)
    {
        if (s == null) return;
        var c = WbRead.ColsOf(s, x, null);
        FrameCase cur = null;
        var loads = new List<object[]>();
        Action finish = () => { if (cur != null) cur.SetLoads(loads); loads = new List<object[]>(); };
        var initials = new List<object[]>();
        foreach (int r in Rows(c))
        {
            string name = Text(c, r, "Case", x);
            if (name.Length > 0)
            {
                finish();
                cur = Take<FrameCase>(c, r, x, "case", name, "Case", () => new FrameCase());
                string ty = Text(c, r, "Type", x), ini = Text(c, r, "Initial", x);
                if (ty.Length > 0) cur.Type = ty;
                if (ini.Length == 0) ini = "Zero";
                initials.Add(new object[] { cur, ini, At(c, r, "Initial") });
            }
            else if (cur == null) { x.Err(At(c, r, "Case") + ": a load row with no case above it"); continue; }
            string lt = Text(c, r, "Load type", x), ln = Text(c, r, "Load name", x);
            if (ln.Length == 0) continue;
            if (lt.Length == 0) lt = "Load pattern";
            if (lt.Equals("Load pattern", StringComparison.OrdinalIgnoreCase)) { ln = x.Ref("pattern", ln); if (!x.Labels("pattern").Contains(ln)) { x.Err(At(c, r, "Load name") + ": no load pattern " + ln); continue; } }
            double sf = Num(c, r, "SF", x, null);
            loads.Add(new object[] { lt, ln, double.IsNaN(sf) ? 1.0 : sf });
        }
        finish();
        var byName = x.M.Cases.ToDictionary(k => k.Label, k => k, StringComparer.Ordinal);
        foreach (var it in initials)
        {
            var cs = (FrameCase)it[0];
            string ini = x.Ref("case", (string)it[1]);
            if (ini.Equals("Zero", StringComparison.OrdinalIgnoreCase)) { cs.Initial = ini; continue; }
            FrameCase from;
            if (!x.Labels("case").Contains(ini) || !byName.TryGetValue(ini, out from)) { x.Err((string)it[2] + ": no case " + ini + " (Zero, or a nonlinear case's name)" + Hint(ini, x.Labels("case"))); continue; }
            // SAP continues only from the end of a nonlinear case (a linear case takes its stiffness, a nonlinear one its state)
            if (!from.Type.StartsWith("Non", StringComparison.OrdinalIgnoreCase)) { x.Err((string)it[2] + ": " + cs.Label + " starts from " + ini + ", a " + from.Type + " case: SAP continues only from a nonlinear (NonStatic) case"); continue; }
            cs.Initial = ini;
        }
        // no loops (SUS from OPE, OPE from SUS), each said once
        var loops = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cs in x.M.Cases)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var at = cs;
            while (at != null && !at.Initial.Equals("Zero", StringComparison.OrdinalIgnoreCase) && at.Initial.Length > 0)
            {
                if (!seen.Add(at.Label)) { if (loops.Add(string.Join("|", seen.OrderBy(q => q, StringComparer.Ordinal).ToArray()))) x.Err(SCases + ": the cases " + string.Join(" -> ", seen.ToArray()) + " start from each other in a loop"); break; }
                FrameCase nx; at = byName.TryGetValue(at.Initial, out nx) ? nx : null;
            }
        }
    }

    static void ReadCombos(CaesarSheet s, Ctx x)
    {
        if (s == null) return;
        var c = WbRead.ColsOf(s, x, null);
        var m = x.M;
        var rows = new List<object[]>();   // combo, item, sf, at
        FrameCombo cur = null;
        foreach (int r in Rows(c))
        {
            string name = Text(c, r, "Combination", x);
            if (name.Length > 0)
            {
                cur = Take<FrameCombo>(c, r, x, "combo", name, "Combination", () => new FrameCombo());
                string ty = Text(c, r, "Type", x); if (ty.Length > 0) cur.Type = ty;
            }
            else if (cur == null) { x.Err(At(c, r, "Combination") + ": an item with no combination above it"); continue; }
            string it = Text(c, r, "Item", x);
            if (it.Length == 0) continue;
            double sf = Num(c, r, "SF", x, null);
            rows.Add(new object[] { cur, it, double.IsNaN(sf) ? 1.0 : sf, At(c, r, "Item") });
        }
        // items: a case (its type names the combination's case type) or a combination (Response Combo)
        foreach (var grp in rows.GroupBy(q => (FrameCombo)q[0]))
        {
            var items = new List<object[]>();
            var old = grp.Key.Items.ToList();
            foreach (var q in grp)
            {
                string it = (string)q[1], at = (string)q[3];
                string asCase = x.Ref("case", it), asCombo = x.Ref("combo", it);
                string type;
                if (x.Labels("case").Contains(asCase)) { var cs = m.Cases.First(k => k.Label == asCase); type = CaseTypeName(cs.Type); it = asCase; }
                else if (x.Labels("combo").Contains(asCombo)) { if (asCombo == grp.Key.Label) { x.Err(at + ": the combination names itself"); continue; } type = "Response Combo"; it = asCombo; }
                else { x.Err(at + ": no case or combination " + it); continue; }
                // keep SAP's own case type text when the item did not change
                var same = old.FirstOrDefault(o => o[1] == it);
                if (same != null && same[0].Length > 0 && (type != "Response Combo") == (same[0] != "Response Combo")) type = same[0];
                items.Add(new object[] { type, it, (double)q[2] });
            }
            if (items.Count == 0) { x.Err(SCombos + ": the combination " + grp.Key.Label + " has no items"); continue; }
            grp.Key.SetItems(items);
        }
    }

    static string CaseTypeName(string t)
    {
        switch ((t ?? "").ToLowerInvariant())
        {
            case "linstatic": return "Linear Static";
            case "nonstatic": return "NonLin Static";     // SAP's own text (its export); it reads "Nonlinear Static" too
            case "linmodal": return "Linear Modal";
            case "linresspec": return "Response Spectrum";
            default: return t;
        }
    }
}
