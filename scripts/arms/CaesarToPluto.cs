using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

// CaesarToPluto  --  CAESAR II neutral file (.cii) [+ the analysis output written to Excel] -> Pluto v4
// viewer model: <base>.bin (one beam domain; results per load case when a workbook is given) +
// <base>.features.json (groups, supports).
// C# 5 / Add-Type (PowerShell 5.1) compatible; every type is prefixed Caesar (one global batch).
//
// Chain: CaesarNeutralReader.Read -> CaesarGeometry.Build (scripts/arms/CaesarGeometry.cs) ->
// [CaesarResults.Read (scripts/arms/CaesarResults.cs)] -> RawViewerWriter, beam-only: Write(true) + appends
// with results, Write(false) without (geometry-only profile) -> FeaturesSidecar (exporter tag "caesar",
// merged with the previous sidecar).
//
// Results (stage 2). Load cases are CAESAR's, keyed on the case number ("L1 (OPE) W+T1+P1"; a combination
// such as "L4 (EXP) L4=L1-L3" keeps its formula in the name). Beam components, in kind runs (only the runs
// the workbook has data for):
//   displacement  DX DY DZ |D| (model length unit, converted from the report's), RX RY RZ (deg): per node,
//                 fanned to the beam ends; bend points and valve mids interpolated between their two real
//                 nodes; a real node missing from the report is filled from its neighbours along the run
//                 (counted); a case with under 95% of the pipe's real nodes is not written (warning) -- the
//                 viewer draws a missing displacement as zero, a fake kink. displacementVector = DX DY DZ.
//   force         Axial fx, Shear fy, Shear fz, Shear |V|     local element forces, as INTERNAL forces:
//   moment        Torsion mx, Bending my, Bending mz, Bending |M|   end A = -(FROM row), end B = +(TO row), so a
//                 value runs on continuously from one element to the next (CAESAR reports the force ON each
//                 element end). |V| and |M| do not depend on the element's local axes.
//   stress        SLP, F/A, Bending stress, Torsion stress     the code stress report, per end (torsion signed
//   code          Code stress, Allowable, Code ratio           like mx); blank cells -- rigids, cases without
//   sif           SIF in-plane, SIF out-plane, SIF torsion, SIF axial   a code check -- stay NaN (no data)
//   restraint     Restraint FX FY FZ |F| MX MY MZ |M|: the restraint summary (loads ON the restraint, CAESAR's
//                 sign, summed per node) at every beam end touching the node, NaN elsewhere; the viewer hides
//                 this kind from the pipe contour list and colours the support symbols with it.
// Element forces and stresses come as FROM / TO pairs per segment (a bend split at its nodes); a segment is
// matched to the pipe path between its two real nodes and interpolated by path length onto the chords and
// valve halves it covers.
//
// Binary
//   nodes     every piece vertex of the pipe graph -- CAESAR node numbers as ids; synthetic vertices
//             (bend near / arc / miter points, valve mid points) with the ids the graph gives them
//             (from 10,000,000 up, skipping real numbers) -- plus the attached nodes (CNODEs, tee
//             surface nodes on no element) at their host's position. Job LENGTH units and CAESAR's
//             axes as they are; META upAxis "Z" when CONTROL IZUP says so, else "Y". Recentred only
//             when a coordinate passes 1e6 (float32 keeps about 7 digits): the graph's whole-unit
//             SuggestedOffset is subtracted before the float cast and written to the sidecar's
//             model.units.worldOffset ("0 0 0" otherwise).
//   beams     one per piece with a length (zero-length pieces, under 0.001 in, are skipped; their
//             nodes keep their positions). Beam id = element ordinal (1-based position in ELEMENTS,
//             CAESAR's element number) * 100 + piece index within the element, skipped pieces counted
//             and a valve's halves taking two indices: stable while the model is unchanged. An element
//             with more than 100 pieces switches the multiplier to 1000 (warning).
//   sections  SectionDef.Pipe(od, wall), LENGTH units, one per distinct (od, wall) to 1e-6. The neutral
//             file gives no shape for rigids and expansion joints, so they get DISPLAY sections (solid,
//             wall 0, sized from the pipe OD; CaesarOptions):
//               VALVE        bow-tie: a synthetic mid node, two tapered halves 1.5 OD -> 0.6 OD -> 1.5 OD
//               FLANGE, FLANGE_PAIR 1.6 OD   RIGID 1.15 OD   RIGID_LINK 0.25 OD   EXPJOINT 1.3 OD
//             REDUCER: a true taper PIPE(od A, wall A) -> PIPE(od B, wall B) (BeamMember.SectionIndexB,
//             ELEM slot 4). Bend chords use the pipe section. Display factors change SECT, so the hash.
//   labels    beams "FROM-TO" (+ " " element name) (+ " arc k/n" on the n drawn chords of a bend,
//             " valve 1/2" and " valve 2/2" on the bow-tie halves); nodes: NODENAME text; synthetic vertices
//             "arc|near|miter|valve FROM-TO" (their element); attached nodes "at <host>".
//   without results: one layout-only beam component ("DX", kind displacement, no planes) -- no load cases.
//
// Sidecar groups, all tagged "caesar" (MergeFrom keeps their id, colour and hidden flag by name):
//   1 size groups "PIPE <od> x <wall> <unit>" (diameter / wall units as written): every beam exactly
//     once (a reducer by its end A, a display piece by its pipe), ascending OD on PipeToPluto's
//     blue -> red ramp, shown
//   2 component groups, written after the sizes so they paint over them: Valve, Flange, Flange pair,
//     Rigid, Rigid link, Expansion joint, Reducer (shown) and Bend (the chords; hidden)
//   3 line-number groups "Line <text>" (CAESAR's line number, carried forward), hidden
//   4 node groups: "Tee (welding)" / "SIF" / "SIF/tee type <n>" from SIF&TEES (hidden), then LAST (the
//     last enabled node group wins a node) one group per distinct support COMBINATION at a node,
//     e.g. "+Y + GUI", "ANC", "X + Y w/gap": RESTRANT type names in a fixed order (ANC +Y +Z X Y Z GUI
//     LIM RX RY RZ, then OTHER(code)), " w/gap" when the gap > 0, " xN" for repeats, plus "HGR" for a
//     MISCEL_1 hanger and "DISP" for an imposed displacement at the node; shown. Common single types keep
//     a fixed colour across jobs; combinations take a fixed palette in name order.
// Sidecar supports (the viewer's support symbols, supports.js): one item per support node, from the .cii alone,
//   named "<node> <combination>", tags caesar + support, dof letters (tx..rz: fixed, +/-, gap, spring, guide,
//   limit, hanger, imposed), and "restraints": one entry per restraint (RestraintJson / HangerJson /
//   DisplacementJson below: type, kind, direction, axis, gap, friction, stiffness, cnode, tag) plus "source".
//   The node groups above colour and show / hide them. WriteSupports is set, so MergeFrom keeps an exporter
//   item's id and hidden flag by name and the user's own items verbatim.
// The groups are built first, then MergeFrom(previous sidecar) restyles them by name and appends the
// user's own groups and sections (the SapToPluto order: MergeFrom matches against the groups present).
//
// Usage from PowerShell 5.1 (scripts/caesar/Run-Caesar.ps1 wraps this):
//   . <repo>\scripts\lib\Config.ps1
//   $r = [CaesarToPluto]::Export('<project dir>\job.cii', '<project dir>\job.xlsx', '<project dir>\viewer\job', 'caesar/job')
//   $r.Summary(); $r.Warnings
// (results path $null = geometry only) then open job.bin TOGETHER with job.features.json in the viewer (the
// groups and the support symbols live in the sidecar).

public class CaesarOptions
{
    public double ArcStepDeg = CaesarGeometry.DefaultArcStepDeg;   // bend chord step (CaesarGeometry clamps it to 0.5..45)
    // display sizes, multiples of the pipe OD (the neutral file has no component shapes)
    public double ValveEndFactor = 1.5, ValveWaistFactor = 0.6;
    public double FlangeFactor = 1.6, RigidFactor = 1.15, RigidLinkFactor = 0.25, ExpJointFactor = 1.3;
}

public class CaesarExportResult
{
    public string NeutralPath, ResultsPath, BinPath, SidecarPath, SidecarMerge, ModelId, GeometryHash;
    public string VersionLine = "", LengthUnit = "", UpAxis = "";
    public double CaesarVersion = double.NaN, ArcStepDeg, MaxAbsCoord;
    public double[] WorldOffset;           // subtracted from every node before the float cast; null = not recentred
    public bool ResultsRead;               // a workbook was read and gave at least one load case
    public List<string> LoadCaseNames = new List<string>();
    public List<string> TablesRead = new List<string>(), TablesSkipped = new List<string>();
    public int DispCases, DispCasesDropped, DispNodesFilled, DispNodesMissing;
    public int ForceCases, StressCases, RestraintCases, SegmentsMapped, SegmentsUnmapped, RestraintLoadNodes, RestraintLoadNodesOff;
    public int Components, Supports;      // beam components; sidecar support items (one per support node)
    public string DispUnitRead = "";
    public int Elements, Nodes, RealNodes, SyntheticNodes, AttachedNodes;
    public int Beams, Sections, PipeSections, DisplaySections, TaperedBeams, ZeroLengthSkipped, BackwardPieces;
    public int Bends, BendsStraight, BendChords;
    public int Reducers, ReducersStraight;  // reducer beams drawn tapered / drawn straight (same size at both ends)
    public int Valves;                      // valves drawn as bow-ties (2 beams each)
    public int Flanges, FlangePairs, Rigids, RigidLinks, ExpJoints;
    public int Restraints, Hangers, ImposedDisplacements, RestraintNodes, RestraintCombos, SupportNodesOffModel;
    public int Tees, Sifs;                  // welding-tee nodes / other SIF&TEES nodes
    public int Groups, SizeGroups, ComponentGroups, LineGroups, SifTeeGroups;
    public Dictionary<string, int> BeamsByKind = new Dictionary<string, int>();   // CaesarPieceKind -> beams
    public Dictionary<string, int> ComboNodes = new Dictionary<string, int>();    // support combination -> nodes (group order)
    public List<string> Warnings = new List<string>();

    static string F(double v) { return v.ToString("0.####", CultureInfo.InvariantCulture); }

    public string Summary()
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "CaesarToPluto: CAESAR II {0} (VERSION \"{1}\"), {2} elements, length {3}, up {4}, {5}",
            double.IsNaN(CaesarVersion) ? "?" : F(CaesarVersion), VersionLine, Elements, LengthUnit.Length > 0 ? LengthUnit : "?", UpAxis,
            ResultsRead ? LoadCaseNames.Count + " load case(s), " + Components + " beam component(s)" : "geometry only"));
        if (ResultsRead)
        {
            sb.AppendLine("  load cases: " + string.Join(", ", LoadCaseNames.ToArray()));
            sb.AppendLine(string.Format("  results: displacements {0} case(s){1}{2}; element forces {3}, stresses {4} case(s), {5} segment(s) placed{6}; restraint loads {7} case(s) at {8} node(s){9}",
                DispCases, DispCasesDropped > 0 ? " (" + DispCasesDropped + " dropped: under 95% of the nodes)" : "",
                DispNodesFilled > 0 || DispNodesMissing > 0 ? ", " + DispNodesFilled + " node value(s) filled from neighbours, " + DispNodesMissing + " left empty" : "",
                ForceCases, StressCases, SegmentsMapped, SegmentsUnmapped > 0 ? " (" + SegmentsUnmapped + " not on the pipe)" : "",
                RestraintCases, RestraintLoadNodes, RestraintLoadNodesOff > 0 ? " (" + RestraintLoadNodesOff + " node(s) off the pipe: not shown)" : ""));
            foreach (string t in TablesRead) sb.AppendLine("  read    " + t);
            foreach (string t in TablesSkipped) sb.AppendLine("  skipped " + t);
        }
        sb.AppendLine(string.Format("  nodes {0}: {1} real, {2} synthetic (bend points, valve mids), {3} attached (CNODE / tee surface)",
            Nodes, RealNodes, SyntheticNodes, AttachedNodes));
        sb.AppendLine(string.Format("  beams {0}: {1}; {2} tapered; {3} zero-length piece(s) skipped, {4} backward",
            Beams, string.Join(", ", BeamsByKind.Select(kv => kv.Key + " " + kv.Value).ToArray()), TaperedBeams, ZeroLengthSkipped, BackwardPieces));
        sb.AppendLine(string.Format("  sections {0}: {1} pipe size(s), {2} display (valve / flange / rigid / expansion joint: multiples of the OD)",
            Sections, PipeSections, DisplaySections));
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
            "  components: {0} bends ({1} drawn straight, {2} chords of <= {3} deg), {4} reducers tapered ({5} straight), {6} valves, {7} flanges, {8} flange pairs, {9} rigids, {10} rigid links, {11} expansion joints",
            Bends, BendsStraight, BendChords, F(ArcStepDeg), Reducers, ReducersStraight, Valves, Flanges, FlangePairs, Rigids, RigidLinks, ExpJoints));
        sb.AppendLine(string.Format("  supports: {0} restraints, {1} hangers, {2} imposed displacements at {3} nodes in {4} combination(s){5}",
            Restraints, Hangers, ImposedDisplacements, RestraintNodes, RestraintCombos,
            ComboNodes.Count > 0 ? ": " + string.Join(", ", ComboNodes.Select(kv => kv.Key + " (" + kv.Value + ")").ToArray()) : ""));
        sb.AppendLine(string.Format("  sidecar supports: {0} item(s), one per support node (the viewer's support symbols)", Supports));
        if (SupportNodesOffModel > 0) sb.AppendLine(string.Format("  {0} support / SIF node(s) have no position: left out of the node groups", SupportNodesOffModel));
        sb.AppendLine(string.Format("  SIF & tees: {0} welding-tee node(s), {1} other SIF / tee node(s)", Tees, Sifs));
        sb.AppendLine(WorldOffset == null
            ? string.Format(CultureInfo.InvariantCulture, "  coordinates: max |coord| {0} {1}, not recentred", F(MaxAbsCoord), LengthUnit)
            : string.Format(CultureInfo.InvariantCulture, "  coordinates: max |coord| {0} {1}, recentred by ({2} {3} {4}) -> sidecar model.units.worldOffset",
                F(MaxAbsCoord), LengthUnit, F(WorldOffset[0]), F(WorldOffset[1]), F(WorldOffset[2])));
        sb.AppendLine(string.Format("  groups {0}: {1} sizes, {2} components, {3} lines, {4} SIF/tee, {5} support combinations",
            Groups, SizeGroups, ComponentGroups, LineGroups, SifTeeGroups, RestraintCombos));
        if (!string.IsNullOrEmpty(SidecarMerge)) sb.AppendLine("  " + SidecarMerge);
        sb.AppendLine("  bin      -> " + BinPath);
        sb.AppendLine("  features -> " + SidecarPath);
        sb.AppendLine("  geometryHash " + GeometryHash);
        sb.AppendLine(string.Format("  {0} warning(s)", Warnings.Count));
        sb.AppendLine("  open the .bin TOGETHER with the .features.json in the viewer: the groups and the support symbols live in the sidecar");
        return sb.ToString();
    }

    // The runner's "RESULT {json}" payload: compact, ASCII (non-ASCII escaped).
    public string ToJson()
    {
        var sb = new StringBuilder("{");
        Prop(sb, "neutral", NeutralPath); Prop(sb, "results", ResultsPath); Prop(sb, "resultsRead", ResultsRead);
        Key(sb, "loadCases"); sb.Append("[");
        for (int i = 0; i < LoadCaseNames.Count; i++) { if (i > 0) sb.Append(","); Str(sb, LoadCaseNames[i]); }
        sb.Append("]");
        Prop(sb, "bin", BinPath); Prop(sb, "features", SidecarPath); Prop(sb, "modelId", ModelId); Prop(sb, "geometryHash", GeometryHash);
        Prop(sb, "caesarVersion", double.IsNaN(CaesarVersion) ? null : F(CaesarVersion)); Prop(sb, "versionLine", VersionLine);
        Key(sb, "units"); sb.Append("{"); Prop(sb, "length", LengthUnit); sb.Append("}");
        Prop(sb, "upAxis", UpAxis);
        Key(sb, "worldOffset");
        if (WorldOffset == null) sb.Append("null");
        else sb.Append("[").Append(Num(WorldOffset[0])).Append(",").Append(Num(WorldOffset[1])).Append(",").Append(Num(WorldOffset[2])).Append("]");
        Key(sb, "counts"); sb.Append("{");
        Prop(sb, "elements", Elements); Prop(sb, "nodes", Nodes); Prop(sb, "realNodes", RealNodes); Prop(sb, "syntheticNodes", SyntheticNodes);
        Prop(sb, "attachedNodes", AttachedNodes); Prop(sb, "beams", Beams); Prop(sb, "sections", Sections); Prop(sb, "pipeSections", PipeSections);
        Prop(sb, "displaySections", DisplaySections); Prop(sb, "taperedBeams", TaperedBeams); Prop(sb, "zeroLengthSkipped", ZeroLengthSkipped);
        Prop(sb, "backwardPieces", BackwardPieces); Prop(sb, "bends", Bends); Prop(sb, "bendsStraight", BendsStraight); Prop(sb, "bendChords", BendChords);
        Prop(sb, "reducers", Reducers); Prop(sb, "reducersStraight", ReducersStraight); Prop(sb, "valves", Valves); Prop(sb, "flanges", Flanges);
        Prop(sb, "flangePairs", FlangePairs); Prop(sb, "rigids", Rigids); Prop(sb, "rigidLinks", RigidLinks); Prop(sb, "expJoints", ExpJoints);
        Prop(sb, "restraints", Restraints); Prop(sb, "hangers", Hangers); Prop(sb, "imposedDisplacements", ImposedDisplacements);
        Prop(sb, "restraintNodes", RestraintNodes); Prop(sb, "restraintCombos", RestraintCombos); Prop(sb, "supportNodesOffModel", SupportNodesOffModel);
        Prop(sb, "tees", Tees); Prop(sb, "sifs", Sifs); Prop(sb, "groups", Groups); Prop(sb, "sizeGroups", SizeGroups);
        Prop(sb, "componentGroups", ComponentGroups); Prop(sb, "lineGroups", LineGroups); Prop(sb, "sifTeeGroups", SifTeeGroups);
        Prop(sb, "components", Components); Prop(sb, "supports", Supports); Prop(sb, "dispCases", DispCases); Prop(sb, "dispCasesDropped", DispCasesDropped);
        Prop(sb, "dispNodesFilled", DispNodesFilled); Prop(sb, "dispNodesMissing", DispNodesMissing);
        Prop(sb, "forceCases", ForceCases); Prop(sb, "stressCases", StressCases); Prop(sb, "segmentsMapped", SegmentsMapped);
        Prop(sb, "segmentsUnmapped", SegmentsUnmapped); Prop(sb, "restraintCases", RestraintCases);
        Prop(sb, "restraintLoadNodes", RestraintLoadNodes); Prop(sb, "restraintLoadNodesOff", RestraintLoadNodesOff);
        Prop(sb, "warnings", Warnings.Count);
        sb.Append("}");
        Key(sb, "beamsByKind"); sb.Append("{");
        foreach (var kv in BeamsByKind) Prop(sb, kv.Key, kv.Value);
        sb.Append("}");
        Key(sb, "supportCombos"); sb.Append("{");
        foreach (var kv in ComboNodes) Prop(sb, kv.Key, kv.Value);
        sb.Append("}");
        Prop(sb, "sidecarMerge", SidecarMerge);
        Key(sb, "warnings"); sb.Append("[");
        for (int i = 0; i < Warnings.Count; i++) { if (i > 0) sb.Append(","); Str(sb, Warnings[i]); }
        sb.Append("]}");
        return sb.ToString();
    }

    static string Num(double v)
    {
        return double.IsNaN(v) || double.IsInfinity(v) ? "null" : v.ToString("R", CultureInfo.InvariantCulture);
    }
    static void Key(StringBuilder sb, string k)
    {
        char last = sb[sb.Length - 1];
        if (last != '{' && last != '[') sb.Append(",");
        Str(sb, k);
        sb.Append(":");
    }
    static void Prop(StringBuilder sb, string k, string v) { Key(sb, k); Str(sb, v); }
    static void Prop(StringBuilder sb, string k, int v) { Key(sb, k); sb.Append(v.ToString(CultureInfo.InvariantCulture)); }
    static void Prop(StringBuilder sb, string k, bool v) { Key(sb, k); sb.Append(v ? "true" : "false"); }
    static void Str(StringBuilder sb, string s)
    {
        if (s == null) { sb.Append("null"); return; }
        sb.Append('"');
        foreach (char c in s)
        {
            if (c == '"') sb.Append("\\\"");
            else if (c == '\\') sb.Append("\\\\");
            else if (c == '\n') sb.Append("\\n");
            else if (c == '\r') sb.Append("\\r");
            else if (c == '\t') sb.Append("\\t");
            else if (c < 0x20 || c > 0x7e) sb.Append("\\u").Append(((int)c).ToString("x4"));
            else sb.Append(c);
        }
        sb.Append('"');
    }
}

public static class CaesarToPluto
{
    // ---- group naming / colours ----------------------------------------------------------------
    // PipeToPluto's 12-step size ramp (blue -> green -> yellow -> orange -> red)
    static readonly string[] SizeRamp = {
        "#3b4cc0", "#4f7fd8", "#6fa8e6", "#8fd0d8", "#a5dca0", "#c9e35a",
        "#f2e34a", "#f6be2e", "#f39221", "#e8641c", "#d13a1f", "#b40426" };

    // component groups in write order: {kind, group name, colour}; the Bend group is written hidden
    static readonly string[][] ComponentGroupDefs = {
        new[] { CaesarPieceKind.Valve, "Valve", "#7b1fa2" },
        new[] { CaesarPieceKind.Flange, "Flange", "#00838f" },
        new[] { CaesarPieceKind.FlangePair, "Flange pair", "#1b5e20" },
        new[] { CaesarPieceKind.Rigid, "Rigid", "#6d4c41" },
        new[] { CaesarPieceKind.RigidLink, "Rigid link", "#ff80ab" },
        new[] { CaesarPieceKind.ExpJoint, "Expansion joint", "#1a237e" },
        new[] { CaesarPieceKind.Reducer, "Reducer", "#c2185b" },
        new[] { CaesarPieceKind.Bend, "Bend", "#fdd835" } };

    // support types in name order; single-type groups keep these colours in every job
    static readonly string[] TypeOrder = { "ANC", "+Y", "+Z", "X", "Y", "Z", "GUI", "LIM", "RX", "RY", "RZ", "HGR", "DISP" };
    static readonly string[] TypeColor = { "#e53935", "#7cb342", "#26c6da", "#8e24aa", "#43a047", "#00acc1", "#1e88e5", "#fb8c00",
                                           "#5d4037", "#795548", "#a1887f", "#fdd835", "#d81b60" };
    // combinations (and gaps, repeats, OTHER types): this palette in name order, wrapping
    static readonly string[] ComboPalette = { "#3949ab", "#00897b", "#c0ca33", "#f4511e", "#5e35b1", "#039be5",
                                              "#ffb300", "#ad1457", "#827717", "#4e342e", "#ef9a9a", "#80cbc4" };

    public static CaesarExportResult Export(string ciiPath, string resultsPath, string outBase, string modelId)
    {
        return Export(ciiPath, resultsPath, outBase, modelId, null);
    }

    public static CaesarExportResult Export(string ciiPath, string resultsPath, string outBase, string modelId, CaesarOptions options)
    {
        if (string.IsNullOrEmpty(ciiPath) || !File.Exists(ciiPath)) throw new Exception("CaesarToPluto: neutral file not found: " + ciiPath);
        if (string.IsNullOrEmpty(outBase)) throw new Exception("CaesarToPluto: no output base path.");
        if (outBase.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)) outBase = outBase.Substring(0, outBase.Length - 4);
        if (string.IsNullOrEmpty(Path.GetFileName(outBase)))
            throw new Exception("CaesarToPluto: output base '" + outBase + "' names a folder, not a file; add a file name (<folder>\\<name>).");
        options = Checked(options);
        if (string.IsNullOrEmpty(resultsPath)) resultsPath = null;      // PowerShell $null arrives as ""
        if (string.IsNullOrEmpty(modelId)) modelId = "caesar/" + Path.GetFileName(outBase).Replace(' ', '_');

        var res = new CaesarExportResult();
        res.NeutralPath = ciiPath; res.ResultsPath = resultsPath; res.ModelId = modelId;

        // ---- read + geometry ----
        CaesarModel m = CaesarNeutralReader.Read(ciiPath);
        foreach (string w0 in m.Warnings) res.Warnings.Add("reader: " + w0);
        if (m.Elements.Count == 0) throw new Exception("CaesarToPluto: no ELEMENTS in " + ciiPath + " (not a CAESAR II neutral file?)");
        // a file read wrongly (damaged line endings, a cut-off section) is refused, not drawn as nonsense
        if (double.IsNaN(m.Version) || (m.NumElements > 0 && m.NumElements != m.Elements.Count))
            throw new Exception("CaesarToPluto: " + ciiPath + " was not read cleanly (VERSION line \"" + (m.VersionLine ?? "") + "\", " +
                                m.Elements.Count.ToString(CultureInfo.InvariantCulture) + " element records read, CONTROL NUMELT = " +
                                m.NumElements.ToString(CultureInfo.InvariantCulture) + "); nothing written. Re-export the neutral file from CAESAR II.");
        CaesarPipeGraph g = CaesarGeometry.Build(m, options.ArcStepDeg);
        foreach (string w0 in g.Warnings)
        {
            // the graph's "subtract SuggestedOffset before writing" advice: done below, with its own warning
            if (g.SuggestedOffset != null && w0.StartsWith("coordinates reach ", StringComparison.Ordinal)) continue;
            res.Warnings.Add("geometry: " + w0);
        }
        CaesarResults rr = null;
        if (resultsPath != null)
        {
            rr = CaesarResults.Read(resultsPath);       // throws on a missing, unreadable or .xls file
            foreach (string w0 in rr.Warnings) res.Warnings.Add("results: " + w0);
            res.TablesRead.AddRange(rr.TablesRead);
            res.TablesSkipped.AddRange(rr.TablesSkipped);
            if (rr.Cases.Count == 0)
            {
                res.Warnings.Add("results: no load case in " + resultsPath + " (no displacement, restraint, element force or stress report found): exported geometry only");
                rr = null;
            }
        }
        res.VersionLine = m.VersionLine ?? ""; res.CaesarVersion = m.Version; res.Elements = m.Elements.Count;
        res.ArcStepDeg = g.ArcStepDeg; res.MaxAbsCoord = g.MaxAbsCoord;
        res.UpAxis = m.ZUp ? "Z" : "Y";
        res.BackwardPieces = g.BackwardPieces;

        // ---- units ----
        string lengthUnit = m.Units.LengthUnit ?? "";
        if (lengthUnit.Length == 0)
        {
            lengthUnit = (m.Units.LengthLabel ?? "").Trim().TrimEnd('.').Trim();
            res.Warnings.Add("length unit not recognised (UNITS label \"" + (m.Units.LengthLabel ?? "") + "\"): " +
                             (lengthUnit.Length > 0 ? "written as \"" + lengthUnit + "\"" : "no length unit written"));
        }
        res.LengthUnit = lengthUnit;
        string dUnit = CaesarUnits.NormalizeLength(m.Units.Label(CaesarUnits.Diameter), m.Units.Factor(CaesarUnits.Diameter));
        string wUnit = CaesarUnits.NormalizeLength(m.Units.Label(CaesarUnits.Wall), m.Units.Factor(CaesarUnits.Wall));
        if (dUnit.Length == 0) dUnit = lengthUnit;
        if (wUnit.Length == 0) wUnit = dUnit;
        var names = new Namer(m.Units, dUnit, wUnit);

        // ---- float32 safety: whole-unit offset only when a coordinate passes 1e6 ----
        double[] off = { 0, 0, 0 };
        if (g.SuggestedOffset != null)
        {
            off = new[] { g.SuggestedOffset[0], g.SuggestedOffset[1], g.SuggestedOffset[2] };
            res.WorldOffset = off;
            res.Warnings.Add(string.Format(CultureInfo.InvariantCulture,
                "coordinates reach {0}: ({1} {2} {3}) subtracted from every node; it is in the sidecar's model.units.worldOffset",
                F(g.MaxAbsCoord), F(off[0]), F(off[1]), F(off[2])));
        }

        // ---- beam ids: element number * mult + piece index (a valve takes two) ----
        int maxLocal = 0;
        foreach (var kv in g.ElementPieces)
        {
            int n = 0;
            foreach (int pi in kv.Value) n += g.Pieces[pi].Kind == CaesarPieceKind.Valve ? 2 : 1;
            if (n > maxLocal) maxLocal = n;
        }
        int mult = 100;
        while (maxLocal > mult) mult *= 10;
        if (mult != 100) res.Warnings.Add("an element has " + maxLocal + " pieces: beam ids are element number * " + mult + " + piece index");
        if ((long)(m.Elements.Count + 1) * mult >= int.MaxValue)
            throw new Exception("CaesarToPluto: " + m.Elements.Count + " elements x " + mult + " pieces overflow the beam id range.");

        // ---- beams ----
        var secs = new SectionTable();
        var members = new Dictionary<int, RawViewerWriter.BeamMember>();
        var beamLabels = new Dictionary<int, string>();
        var beamOrder = new List<int>();                               // creation order = element order
        var beamKind = new Dictionary<int, string>();
        var beamSize = new Dictionary<int, string>();
        var sizeDims = new Dictionary<string, double[]>();             // size name -> {od, wall} (LENGTH units) for sorting
        var beamElement = new Dictionary<int, int>();
        var place = new Dictionary<int, CaesarPlace>();                // beam -> where it lies along its element's path
        var sameSizeReducers = new List<string>();
        int unsized = 0, nonFinite = 0;
        foreach (string k in new[] { CaesarPieceKind.Pipe, CaesarPieceKind.Bend, CaesarPieceKind.Reducer, CaesarPieceKind.Valve, CaesarPieceKind.Flange,
                                     CaesarPieceKind.FlangePair, CaesarPieceKind.Rigid, CaesarPieceKind.RigidLink, CaesarPieceKind.ExpJoint })
            res.BeamsByKind[k] = 0;
        for (int ei = 0; ei < m.Elements.Count; ei++)
        {
            List<int> plist;
            if (!g.ElementPieces.TryGetValue(ei, out plist)) continue;
            CaesarElement e = m.Elements[ei];
            string elText = I(e.From) + "-" + I(e.To);
            string elName = (e.Name ?? "").Trim();
            string label0 = elText + (elName.Length > 0 ? " " + elName : "");
            // "arc k/n" counts the chords that are drawn (a zero-length chord between two bend nodes at the same angle is not)
            int nChords = plist.Count(pi => g.Pieces[pi].Kind == CaesarPieceKind.Bend && Drawable(g, g.Pieces[pi]));
            int chord = 0, local = 0;
            for (int j = 0; j < plist.Count; j++)
            {
                int pi = plist[j];
                CaesarPiece p = g.Pieces[pi];
                bool valve = p.Kind == CaesarPieceKind.Valve;
                int id0 = (ei + 1) * mult + local;        // the local index counts skipped pieces too: ids stay put
                local += valve ? 2 : 1;
                if (p.Length <= 0) { res.ZeroLengthSkipped++; continue; }
                if (!Drawable(g, p)) { nonFinite++; continue; }
                double[] pa = g.Nodes[p.FromNode], pb = g.Nodes[p.ToNode];
                if (p.Kind == CaesarPieceKind.Bend) chord++;

                double odA = p.OdA, wallA = p.WallA, odB = p.OdB, wallB = p.WallB;
                if (!(odA > 0) || double.IsInfinity(odA)) { odA = 2.0 * m.Units.InchInLength; unsized++; }
                if (!(odB > 0) || double.IsInfinity(odB)) odB = odA;
                if (!(wallA >= 0) || double.IsInfinity(wallA)) wallA = 0;
                if (!(wallB >= 0) || double.IsInfinity(wallB)) wallB = wallA;
                string size = p.OdA > 0 && !double.IsInfinity(p.OdA) ? names.Size(odA, wallA) : "PIPE (no OD)";
                if (!sizeDims.ContainsKey(size)) sizeDims[size] = new[] { p.OdA > 0 ? odA : double.MaxValue, wallA };
                double[] ly = LocalY(pa, pb, m.ZUp);
                res.BeamsByKind[p.Kind] = (res.BeamsByKind.ContainsKey(p.Kind) ? res.BeamsByKind[p.Kind] : 0) + (valve ? 2 : 1);

                if (valve)
                {
                    double[] mid = { (pa[0] + pb[0]) / 2, (pa[1] + pb[1]) / 2, (pa[2] + pb[2]) / 2 };
                    int midId = g.AddSyntheticNode(mid, p.FromNode, p.ToNode, 0.5, p.ElementIndex, "VALVE");
                    int sEnd = secs.Display("VALVE END", names.Display("VALVE", odA, options.ValveEndFactor, " end"), odA * options.ValveEndFactor);
                    int sWaist = secs.Display("VALVE WAIST", names.Display("VALVE", odA, options.ValveWaistFactor, " waist"), odA * options.ValveWaistFactor);
                    AddBeam(members, beamOrder, id0, p.FromNode, midId, sEnd, sWaist, ly);
                    AddBeam(members, beamOrder, id0 + 1, midId, p.ToNode, sWaist, sEnd, ly);
                    foreach (int bid in new[] { id0, id0 + 1 })
                    {
                        beamKind[bid] = p.Kind; beamSize[bid] = size; beamElement[bid] = ei;
                        beamLabels[bid] = label0 + (bid == id0 ? " valve 1/2" : " valve 2/2");
                    }
                    place[id0] = new CaesarPlace(ei, j, 0.0, 0.5);
                    place[id0 + 1] = new CaesarPlace(ei, j, 0.5, 1.0);
                    res.Valves++;
                    continue;
                }

                int sA, sB = -1;
                switch (p.Kind)
                {
                    case CaesarPieceKind.Reducer:
                        sA = secs.Pipe(size, odA, wallA);
                        sB = secs.Pipe(names.Size(odB, wallB), odB, wallB);
                        if (sB == sA) { sB = -1; res.ReducersStraight++; sameSizeReducers.Add(elText); } else res.Reducers++;
                        break;
                    case CaesarPieceKind.Flange:
                        sA = secs.Display("FLANGE", names.Display("FLANGE", odA, options.FlangeFactor, ""), odA * options.FlangeFactor);
                        res.Flanges++;
                        break;
                    case CaesarPieceKind.FlangePair:
                        sA = secs.Display("FLANGE PAIR", names.Display("FLANGE PAIR", odA, options.FlangeFactor, ""), odA * options.FlangeFactor);
                        res.FlangePairs++;
                        break;
                    case CaesarPieceKind.Rigid:
                        sA = secs.Display("RIGID", names.Display("RIGID", odA, options.RigidFactor, ""), odA * options.RigidFactor);
                        res.Rigids++;
                        break;
                    case CaesarPieceKind.RigidLink:
                        sA = secs.Display("RIGID LINK", names.Display("RIGID LINK", odA, options.RigidLinkFactor, ""), odA * options.RigidLinkFactor);
                        res.RigidLinks++;
                        break;
                    case CaesarPieceKind.ExpJoint:
                        sA = secs.Display("EXP JOINT", names.Display("EXP JOINT", odA, options.ExpJointFactor, ""), odA * options.ExpJointFactor);
                        res.ExpJoints++;
                        break;
                    default:                                           // PIPE and BEND chords: the pipe itself
                        sA = secs.Pipe(size, odA, wallA);
                        if (p.Kind == CaesarPieceKind.Bend) res.BendChords++;
                        break;
                }
                AddBeam(members, beamOrder, id0, p.FromNode, p.ToNode, sA, sB, ly);
                beamKind[id0] = p.Kind; beamSize[id0] = size; beamElement[id0] = ei;
                place[id0] = new CaesarPlace(ei, j, 0.0, 1.0);
                beamLabels[id0] = label0 + (p.Kind == CaesarPieceKind.Bend ? " arc " + I(chord) + "/" + I(nChords) : "");
            }
        }
        foreach (var bg in g.Bends) { res.Bends++; if (bg.Straight) res.BendsStraight++; }
        if (sameSizeReducers.Count > 0)
            res.Warnings.Add(sameSizeReducers.Count + " reducer(s) with the same OD and wall at both ends: drawn straight, still in the Reducer group (element " +
                             string.Join(", ", sameSizeReducers.Take(8).ToArray()) + (sameSizeReducers.Count > 8 ? ", ..." : "") + ")");
        if (unsized > 0) res.Warnings.Add(unsized + " piece(s) without a usable OD: drawn 2 in across, in the size group \"PIPE (no OD)\"");
        if (nonFinite > 0) res.Warnings.Add(nonFinite + " piece(s) with a node position that is not a number: left out");
        if (members.Count == 0) throw new Exception("CaesarToPluto: no pipe piece with a length in " + ciiPath + ".");
        foreach (var kv in res.BeamsByKind.Where(x => x.Value == 0).ToList()) res.BeamsByKind.Remove(kv.Key);

        // ---- nodes (after the valve mid points joined the graph) ----
        var nodes = new Dictionary<int, Node>();
        var nodeLabels = new Dictionary<int, string>();
        int badNodes = 0;
        foreach (var kv in g.Nodes)
        {
            if (!Finite(kv.Value)) { badNodes++; continue; }
            nodes[kv.Key] = MakeNode(kv.Key, kv.Value, off);
            CaesarSyntheticNode s;
            if (g.Synthetic.TryGetValue(kv.Key, out s))
            {
                res.SyntheticNodes++;
                string where = s.ElementIndex >= 0 && s.ElementIndex < m.Elements.Count
                    ? " " + I(m.Elements[s.ElementIndex].From) + "-" + I(m.Elements[s.ElementIndex].To) : "";
                nodeLabels[kv.Key] = (s.Kind ?? "").ToLowerInvariant() + where;
            }
            else
            {
                res.RealNodes++;
                string nm;
                if (m.NodeNameByNode.TryGetValue(kv.Key, out nm) && nm != null && nm.Trim().Length > 0) nodeLabels[kv.Key] = nm.Trim();
            }
        }
        foreach (var kv in g.AttachedNodes)
        {
            if (nodes.ContainsKey(kv.Key)) continue;
            if (!Finite(kv.Value)) { badNodes++; continue; }
            nodes[kv.Key] = MakeNode(kv.Key, kv.Value, off);
            res.AttachedNodes++;
            string nm;
            int host;
            if (m.NodeNameByNode.TryGetValue(kv.Key, out nm) && nm != null && nm.Trim().Length > 0) nodeLabels[kv.Key] = nm.Trim();
            else if (g.AttachedHost.TryGetValue(kv.Key, out host)) nodeLabels[kv.Key] = "at " + I(host);
        }
        if (badNodes > 0) res.Warnings.Add(badNodes + " node position(s) are not numbers: left out of the node table");
        res.Nodes = nodes.Count;
        res.Beams = members.Count;
        res.Sections = secs.List.Count; res.PipeSections = secs.PipeCount; res.DisplaySections = secs.List.Count - secs.PipeCount;
        foreach (var bm in members.Values) if (bm.SectionIndexB >= 0 && bm.SectionIndexB != bm.SectionIndex) res.TaperedBeams++;

        // ---- results: load cases, components, values per beam end ----
        CaesarResultSet rs = rr != null ? BuildResults(rr, g, members, beamOrder, place, lengthUnit, res) : null;

        // ---- binary ----
        string binPath = outBase + ".bin";
        string dir = Path.GetDirectoryName(Path.GetFullPath(binPath));
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        var units = new Dictionary<string, string>();
        if (lengthUnit.Length > 0) units["length"] = lengthUnit;
        List<RawViewerWriter.Component> beamComps;
        if (rs != null) beamComps = rs.Components;
        else
        {
            beamComps = new List<RawViewerWriter.Component>();
            beamComps.Add(new RawViewerWriter.Component("DX", "displacement", lengthUnit.Length > 0 ? lengthUnit : null));   // layout only; no planes written
        }
        var w = new RawViewerWriter(binPath, nodes, null, rs != null ? rs.CaseNames : null, null, members, secs.List, beamComps, modelId, units);
        w.UpAxis = res.UpAxis;
        w.SetBeamLabels(beamLabels);
        if (nodeLabels.Count > 0) w.SetNodeLabels(nodeLabels);
        w.Write(rs != null);
        if (rs != null)
        {
            if (rs.Disps.Count > 0) w.AppendDisplacements(rs.Disps);
            foreach (var kv in rs.Records) if (kv.Value.Count > 0) w.AppendBeamForces(kv.Value, kv.Key);
        }
        res.Components = rs != null ? beamComps.Count : 0;
        res.BinPath = binPath;
        res.GeometryHash = w.GeometryHash;

        // ---- sidecar ----
        var sc = new FeaturesSidecar();
        sc.ModelId = modelId;
        sc.GeometryHash = w.GeometryHash;
        if (lengthUnit.Length > 0) sc.Units["length"] = lengthUnit;
        sc.Units["worldOffset"] = string.Format(CultureInfo.InvariantCulture, "{0} {1} {2}", off[0], off[1], off[2]);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1 sizes: every beam once, ascending OD (then wall) on the ramp, shown
        var bySize = new Dictionary<string, List<uint>>();
        foreach (int bid in beamOrder) AddTo(bySize, beamSize[bid], (uint)bid);
        var sizeOrder = bySize.Keys.OrderBy(s => sizeDims[s][0]).ThenBy(s => sizeDims[s][1]).ThenBy(s => s, StringComparer.Ordinal).ToList();
        for (int i = 0; i < sizeOrder.Count; i++)
        {
            var grp = sc.AddGroup(Unique(used, sizeOrder[i]), SizeColor(i, sizeOrder.Count), "beams", bySize[sizeOrder[i]], new[] { "caesar", "size" });
            grp.Hidden = false;
            res.SizeGroups++;
        }

        // 2 components, after the sizes so they paint over them; Bend hidden
        foreach (string[] def in ComponentGroupDefs)
        {
            var ids = beamOrder.Where(b => beamKind[b] == def[0]).Select(b => (uint)b).ToList();
            if (ids.Count == 0) continue;
            var grp = sc.AddGroup(Unique(used, def[1]), def[2], "beams", ids, new[] { "caesar", "component", def[0].ToLowerInvariant() });
            grp.Hidden = def[0] == CaesarPieceKind.Bend;
            res.ComponentGroups++;
        }

        // 3 line numbers (carried forward), hidden
        var byLine = new Dictionary<string, List<uint>>();
        var lineOrder = new List<string>();
        foreach (int bid in beamOrder)
        {
            string ln = (m.Elements[beamElement[bid]].LineNumber ?? "").Trim();
            if (ln.Length == 0) continue;
            string gname = ln.StartsWith("line", StringComparison.OrdinalIgnoreCase) ? ln : "Line " + ln;
            if (!byLine.ContainsKey(gname)) lineOrder.Add(gname);
            AddTo(byLine, gname, (uint)bid);
        }
        foreach (string ln in lineOrder)
        {
            var grp = sc.AddGroup(Unique(used, ln), null, "beams", byLine[ln], new[] { "caesar", "line" });
            grp.Hidden = true;
            res.LineGroups++;
        }

        // 4a SIF & tees (hidden)
        var bySif = new Dictionary<string, List<uint>>();
        var sifRank = new Dictionary<string, int>();
        var sifSeen = new HashSet<string>();
        int offModel = 0;
        foreach (var st in m.SifTees)
        {
            if (st.Node <= 0) continue;
            string gname = st.TypeCode == 3 ? "Tee (welding)" : st.TypeCode == 0 ? "SIF" : "SIF/tee type " + I(st.TypeCode);
            if (!nodes.ContainsKey(st.Node)) { offModel++; continue; }
            if (!sifSeen.Add(gname + "|" + I(st.Node))) continue;
            sifRank[gname] = st.TypeCode == 3 ? -2 : st.TypeCode == 0 ? -1 : st.TypeCode;
            AddTo(bySif, gname, (uint)st.Node);
            if (st.TypeCode == 3) res.Tees++; else res.Sifs++;
        }
        foreach (string gname in bySif.Keys.OrderBy(k => sifRank[k]).ThenBy(k => k, StringComparer.Ordinal))
        {
            string color = gname == "Tee (welding)" ? "#ec407a" : gname == "SIF" ? "#7e57c2" : "#8d6e63";
            var grp = sc.AddNodeGroup(Unique(used, gname), color, bySif[gname].OrderBy(n => n), new[] { "caesar", gname == "Tee (welding)" ? "tee" : "sif" });
            grp.Hidden = true;
            res.SifTeeGroups++;
        }

        // 4b support combinations, LAST so they win a node; shown
        var tokens = new Dictionary<int, List<string>>();
        foreach (var r in m.Restraints)
        {
            if (r.Node <= 0) continue;
            res.Restraints++;
            string t = (r.TypeName ?? "").Trim();
            if (t.Length == 0) t = "OTHER(" + I(r.TypeCode) + ")";
            if (r.Gap > 0) t += " w/gap";
            AddToken(tokens, r.Node, t);
        }
        foreach (var h in m.Hangers) if (h.Node > 0) { res.Hangers++; AddToken(tokens, h.Node, "HGR"); }
        foreach (var d in m.Displacements) if (d.Node > 0 && d.FixedCount > 0) { res.ImposedDisplacements++; AddToken(tokens, d.Node, "DISP"); }
        var byCombo = new Dictionary<string, List<uint>>();
        var comboKey = new Dictionary<string, string>();
        var comboOf = new Dictionary<int, string>();                   // support node -> its combination name
        foreach (int node in tokens.Keys.OrderBy(k => k))
        {
            if (!nodes.ContainsKey(node)) { offModel++; continue; }
            List<string> sorted = tokens[node].OrderBy(t => Rank(t)).ThenBy(t => t, StringComparer.Ordinal).ToList();
            var parts = new List<string>();
            for (int i = 0; i < sorted.Count; )
            {
                int j = i;
                while (j < sorted.Count && sorted[j] == sorted[i]) j++;
                parts.Add(sorted[i] + (j - i > 1 ? " x" + I(j - i) : ""));
                i = j;
            }
            string combo = string.Join(" + ", parts.ToArray());
            comboOf[node] = combo;
            if (!byCombo.ContainsKey(combo))      // sort key: the type ranks ("002.006"), shorter first, then the name
                comboKey[combo] = string.Join(".", sorted.Select(t => Rank(t).ToString("000", CultureInfo.InvariantCulture)).ToArray()) + " |" + combo;
            AddTo(byCombo, combo, (uint)node);
        }
        res.SupportNodesOffModel = offModel;
        if (offModel > 0) res.Warnings.Add(offModel + " support / SIF node(s) have no position (on no element): left out of the node groups");
        int paletteNext = 0;
        foreach (string combo in byCombo.Keys.OrderBy(k => comboKey[k], StringComparer.Ordinal))
        {
            int ti = Array.IndexOf(TypeOrder, combo);
            string color = ti >= 0 ? TypeColor[ti] : ComboPalette[paletteNext++ % ComboPalette.Length];
            var grp = sc.AddNodeGroup(Unique(used, combo), color, byCombo[combo], new[] { "caesar", "support" });
            grp.Hidden = false;
            res.ComboNodes[combo] = byCombo[combo].Count;
            res.RestraintNodes += byCombo[combo].Count;
        }
        res.RestraintCombos = byCombo.Count;

        // 5 supports: one item per support node, from the .cii alone (the viewer draws the symbols from these;
        // the node groups above colour them and show / hide them)
        sc.WriteSupports = true;
        double[] up = m.ZUp ? new double[] { 0, 0, 1 } : new double[] { 0, 1, 0 };
        var restrAt = new Dictionary<int, List<CaesarRestraint>>();
        foreach (var r in m.Restraints) if (r.Node > 0) AddTo(restrAt, r.Node, r);
        var hangAt = new Dictionary<int, List<CaesarHanger>>();
        foreach (var h in m.Hangers) if (h.Node > 0) AddTo(hangAt, h.Node, h);
        var dispAt = new Dictionary<int, List<CaesarDisplacement>>();
        foreach (var d in m.Displacements) if (d.Node > 0 && d.FixedCount > 0) AddTo(dispAt, d.Node, d);
        foreach (int node in comboOf.Keys.OrderBy(k => k))
        {
            var dof = new string[6];
            var entries = new List<string>();
            List<CaesarRestraint> rl;
            if (restrAt.TryGetValue(node, out rl)) foreach (var r in rl) entries.Add(RestraintJson(r, g, up, dof));
            List<CaesarHanger> hl;
            if (hangAt.TryGetValue(node, out hl)) foreach (var h in hl) entries.Add(HangerJson(h, up, dof));
            List<CaesarDisplacement> dl;
            if (dispAt.TryGetValue(node, out dl)) foreach (var d in dl) entries.Add(DisplacementJson(d, dof));
            var dofPairs = new List<KeyValuePair<string, string>>();
            for (int k = 0; k < 6; k++) if (dof[k] != null) dofPairs.Add(new KeyValuePair<string, string>(DofNames[k], dof[k]));
            sc.AddSupport(I(node) + " " + comboOf[node], new[] { (uint)node }, dofPairs, new[] { "caesar", "support" },
                          "\"restraints\": [" + string.Join(", ", entries.ToArray()) + "], \"source\": {\"exporter\": \"caesar\", \"node\": " + I(node) + "}");
        }
        res.Supports = sc.Supports.Count;

        // the previous sidecar is the user's layer: section cuts, predicates, hand-made groups and the
        // colour / enable / id of our own groups survive a re-export (groups first, then merge: SapToPluto)
        string scPath = outBase + ".features.json";
        res.SidecarMerge = sc.MergeFrom(scPath, "caesar");
        res.Groups = sc.Groups.Count;
        File.WriteAllText(scPath, sc.ToJson(), new UTF8Encoding(false));
        res.SidecarPath = scPath;
        return res;
    }

    // ---- results -----------------------------------------------------------------------------

    // where a beam lies along its element's piece path: piece Seq, from fraction TA to TB of that piece
    // (0..1 for a whole piece; 0..0.5 and 0.5..1 for a valve's two halves)
    sealed class CaesarPlace
    {
        public readonly int Elem, Seq;
        public readonly double TA, TB;
        public CaesarPlace(int elem, int seq, double ta, double tb) { Elem = elem; Seq = seq; TA = ta; TB = tb; }
    }

    // one element's piece path: the node at each position, the piece lengths, the path length to each node
    sealed class CaesarPath
    {
        public int Elem;
        public int[] Nodes;
        public double[] Len, Cum;
    }

    // a report segment placed on a path: values at path positions QA (its FROM node) and QB (its TO node)
    sealed class CaesarPlaced
    {
        public CaesarSegmentResult Seg;
        public int QA, QB;
    }

    sealed class CaesarResultSet
    {
        public readonly List<RawViewerWriter.Component> Components = new List<RawViewerWriter.Component>();
        public readonly Dictionary<int, string> CaseNames = new Dictionary<int, string>();
        public readonly List<Disp> Disps = new List<Disp>();
        public readonly List<KeyValuePair<string, List<RawViewerWriter.BeamRecord>>> Records = new List<KeyValuePair<string, List<RawViewerWriter.BeamRecord>>>();
    }

    static CaesarResultSet BuildResults(CaesarResults rr, CaesarPipeGraph g, Dictionary<int, RawViewerWriter.BeamMember> members,
                                        List<int> beamOrder, Dictionary<int, CaesarPlace> place, string lengthUnit, CaesarExportResult res)
    {
        var rs = new CaesarResultSet();
        foreach (CaesarLoadCase c in rr.Cases) { rs.CaseNames[c.Number] = c.Name; res.LoadCaseNames.Add(c.Name); }
        res.ResultsRead = true;
        int[] cases = rr.Cases.Select(c => c.Number).ToArray();

        // element paths, FROM -> TO (ElementPieces is in path order)
        var paths = new Dictionary<int, CaesarPath>();
        foreach (var kv in g.ElementPieces)
        {
            List<int> pl = kv.Value;
            if (pl.Count == 0) continue;
            var cp = new CaesarPath();
            cp.Elem = kv.Key;
            cp.Nodes = new int[pl.Count + 1];
            cp.Len = new double[pl.Count];
            cp.Cum = new double[pl.Count + 1];
            cp.Nodes[0] = g.Pieces[pl[0]].FromNode;
            for (int j = 0; j < pl.Count; j++)
            {
                CaesarPiece p = g.Pieces[pl[j]];
                cp.Len[j] = p.Length > 0 && !double.IsInfinity(p.Length) ? p.Length : 0;
                cp.Nodes[j + 1] = p.ToNode;
                cp.Cum[j + 1] = cp.Cum[j] + cp.Len[j];
            }
            paths[kv.Key] = cp;
        }

        // beam ends per node (displacements fan out through the writer; restraint loads need the ends)
        var nodeEnds = new Dictionary<int, List<int[]>>();
        foreach (int bid in beamOrder)
        {
            var bm = members[bid];
            AddEnd(nodeEnds, bm.NodeA, bid, 0);
            AddEnd(nodeEnds, bm.NodeB, bid, 1);
        }

        BuildDisplacements(rr, g, paths, nodeEnds, cases, lengthUnit, rs, res);
        BuildSegments(rr, g, paths, beamOrder, place, cases, rs, res);
        BuildRestraintLoads(rr, nodeEnds, cases, rs, res);
        return rs;
    }

    static void AddEnd(Dictionary<int, List<int[]>> map, int node, int bid, int end)
    {
        List<int[]> l;
        if (!map.TryGetValue(node, out l)) { l = new List<int[]>(); map[node] = l; }
        l.Add(new[] { bid, end });
    }

    // ---- displacements: per node; synthetic nodes between their real nodes; gaps filled along the run ----
    static void BuildDisplacements(CaesarResults rr, CaesarPipeGraph g, Dictionary<int, CaesarPath> paths, Dictionary<int, List<int[]>> nodeEnds,
                                   int[] cases, string lengthUnit, CaesarResultSet rs, CaesarExportResult res)
    {
        if (rr.Displacements.Count == 0) return;
        res.DispUnitRead = rr.DispUnit;
        string dispLen = NormLen(rr.DispUnit), modelLen = NormLen(lengthUnit);
        double scale = 1.0;
        if (dispLen.Length > 0 && modelLen.Length > 0 && dispLen != modelLen)
        {
            scale = InchesPer(dispLen) / InchesPer(modelLen);
            res.Warnings.Add("results: displacements are in " + dispLen + ", the model in " + modelLen + ": converted (x " + F(scale) + ")");
        }
        else if (dispLen.Length == 0)
            res.Warnings.Add("results: displacement unit \"" + rr.DispUnit + "\" not recognised: written as read (the deformed shape is true to scale only if it is the model's length unit)");

        var realNodes = new List<int>();
        var synthNodes = new List<int>();
        foreach (int n in nodeEnds.Keys) (g.IsSynthetic(n) ? synthNodes : realNodes).Add(n);
        realNodes.Sort(); synthNodes.Sort();
        // real neighbours along each element path (for filling a node the report left out)
        var nbr = new Dictionary<int, HashSet<int>>();
        foreach (var cp in paths.Values)
        {
            int prev = -1;
            foreach (int n in cp.Nodes)
            {
                if (g.IsSynthetic(n)) continue;
                if (prev >= 0 && prev != n) { Link(nbr, prev, n); Link(nbr, n, prev); }
                prev = n;
            }
        }

        int written = 0;
        foreach (int c in cases)
        {
            Dictionary<int, double[]> tab;
            if (!rr.Displacements.TryGetValue(c, out tab)) continue;
            var val = new Dictionary<int, double[]>();
            foreach (int n in realNodes)
            {
                double[] v;
                if (tab.TryGetValue(n, out v) && !double.IsNaN(v[0] + v[1] + v[2]))
                    val[n] = new[] { v[0] * scale, v[1] * scale, v[2] * scale, v[3], v[4], v[5] };
            }
            double cover = realNodes.Count > 0 ? (double)val.Count / realNodes.Count : 0;
            if (cover < 0.95)
            {
                res.DispCasesDropped++;
                res.Warnings.Add(string.Format(CultureInfo.InvariantCulture,
                    "results: {0}: displacements for only {1} of {2} pipe nodes ({3:0.#}%): not written for this case (a missing displacement would draw as zero)",
                    rr.Cases.First(x => x.Number == c).Name, val.Count, realNodes.Count, cover * 100));
                continue;
            }
            var missing = realNodes.Where(n => !val.ContainsKey(n)).ToList();
            for (int pass = 0; pass < 200 && missing.Count > 0; pass++)
            {
                var got = new Dictionary<int, double[]>();
                foreach (int n in missing)
                {
                    HashSet<int> ns;
                    if (!nbr.TryGetValue(n, out ns)) continue;
                    var known = ns.Where(x => val.ContainsKey(x)).ToList();
                    if (known.Count == 0) continue;
                    var avg = new double[6];
                    foreach (int x in known) for (int k = 0; k < 6; k++) avg[k] += val[x][k] / known.Count;
                    got[n] = avg;
                }
                if (got.Count == 0) break;
                foreach (var kv in got) val[kv.Key] = kv.Value;
                res.DispNodesFilled += got.Count;
                missing = missing.Where(n => !val.ContainsKey(n)).ToList();
            }
            res.DispNodesMissing += missing.Count;
            foreach (int n in synthNodes)
            {
                CaesarSyntheticNode s;
                if (!g.Synthetic.TryGetValue(n, out s)) continue;
                double[] a, b;
                bool ha = val.TryGetValue(s.NodeA, out a), hb = val.TryGetValue(s.NodeB, out b);
                if (ha && hb)
                {
                    var v = new double[6];
                    for (int k = 0; k < 6; k++) v[k] = s.Interpolate(a[k], b[k]);
                    val[n] = v;
                }
                else if (ha) val[n] = a;
                else if (hb) val[n] = b;
            }
            foreach (var kv in val)
            {
                double[] v = kv.Value;
                var d = new Disp();
                d.node = kv.Key; d.LC = c;
                d.DR = new[] { (float)v[0], (float)v[1], (float)v[2], (float)Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]),
                               (float)v[3], (float)v[4], (float)v[5] };
                rs.Disps.Add(d);
            }
            written++;
        }
        res.DispCases = written;
        if (res.DispNodesFilled > 0)
            res.Warnings.Add("results: " + res.DispNodesFilled + " pipe node displacement(s) missing from the report: averaged from the neighbouring nodes along the run");
        if (res.DispNodesMissing > 0)
            res.Warnings.Add("results: " + res.DispNodesMissing + " pipe node displacement(s) missing and not fillable: they draw undeformed");
        if (written == 0) return;
        string lu = modelLen.Length > 0 ? modelLen : (dispLen.Length > 0 ? dispLen : CleanUnit(rr.DispUnit));
        string ru = CleanUnit(rr.RotUnit.Length > 0 ? rr.RotUnit : "deg.");
        foreach (string nm in new[] { "DX", "DY", "DZ", "|D|" }) rs.Components.Add(new RawViewerWriter.Component(nm, "displacement", lu));
        foreach (string nm in new[] { "RX", "RY", "RZ" }) rs.Components.Add(new RawViewerWriter.Component(nm, "displacement", ru));
    }

    static void Link(Dictionary<int, HashSet<int>> map, int a, int b)
    {
        HashSet<int> s;
        if (!map.TryGetValue(a, out s)) { s = new HashSet<int>(); map[a] = s; }
        s.Add(b);
    }

    // ---- element forces and code stresses: FROM / TO segments placed on the paths, per beam end ----
    static void BuildSegments(CaesarResults rr, CaesarPipeGraph g, Dictionary<int, CaesarPath> paths, List<int> beamOrder,
                              Dictionary<int, CaesarPlace> place, int[] cases, CaesarResultSet rs, CaesarExportResult res)
    {
        if (rr.LocalForces.Count == 0 && rr.Stresses.Count == 0) return;
        // candidates: (FROM, TO) -> (element, position of FROM, position of TO, real-node span); the nearest first
        var cand = new Dictionary<long, List<int[]>>();
        foreach (var cp in paths.Values)
        {
            var realPos = new List<int>();
            for (int q = 0; q < cp.Nodes.Length; q++) if (!g.IsSynthetic(cp.Nodes[q])) realPos.Add(q);
            for (int i = 0; i < realPos.Count; i++)
                for (int k = i + 1; k < realPos.Count; k++)
                {
                    int a = cp.Nodes[realPos[i]], b = cp.Nodes[realPos[k]];
                    if (a == b) continue;
                    long key = ((long)a << 32) | (uint)b;
                    List<int[]> l;
                    if (!cand.TryGetValue(key, out l)) { l = new List<int[]>(); cand[key] = l; }
                    l.Add(new[] { cp.Elem, realPos[i], realPos[k], k - i });
                }
        }
        foreach (var l in cand.Values) l.Sort((x, y) => x[3] != y[3] ? x[3].CompareTo(y[3]) : x[0] != y[0] ? x[0].CompareTo(y[0]) : x[1].CompareTo(y[1]));

        // which stress columns the workbook has: the stress / code / sif runs keep only those
        int[] stressCols = new[] { 0, 1, 2, 3 }.Where(k => rr.StressHasColumn[k]).ToArray();
        int[] codeCols = new[] { 8, 9, 10 }.Where(k => rr.StressHasColumn[k]).ToArray();
        int[] sifCols = new[] { 4, 5, 6, 7 }.Where(k => rr.StressHasColumn[k]).ToArray();
        var force = new List<RawViewerWriter.BeamRecord>();
        var moment = new List<RawViewerWriter.BeamRecord>();
        var stress = new List<RawViewerWriter.BeamRecord>();
        var code = new List<RawViewerWriter.BeamRecord>();
        var sif = new List<RawViewerWriter.BeamRecord>();
        int maxMapped = 0, maxUnmapped = 0;
        var unmappedExamples = new List<string>();

        foreach (bool isStress in new[] { false, true })
        {
            var source = isStress ? rr.Stresses : rr.LocalForces;
            int casesDone = 0;
            foreach (int c in cases)
            {
                List<CaesarSegmentResult> segs;
                if (!source.TryGetValue(c, out segs)) continue;
                // place every segment of this case
                var at = new Dictionary<int, CaesarPlaced[]>();          // element -> placed segment per piece position
                var used = new HashSet<long>();
                int mapped = 0, unmapped = 0;
                foreach (CaesarSegmentResult sg in segs)
                {
                    // a tee surface node (CAESAR's internal split of a welding tee: header node -> surface node ->
                    // branch) sits on no element: the branch starts at its host, the stub inside the header is skipped
                    int a = OnPipe(g, sg.From), b = OnPipe(g, sg.To);
                    if (a == b) continue;
                    List<int[]> l;
                    int[] hit = null;
                    if (cand.TryGetValue(((long)a << 32) | (uint)b, out l))
                        foreach (int[] x in l)
                        {
                            long u = ((long)x[0] << 32) | (uint)x[1];
                            if (used.Contains(u)) continue;
                            used.Add(u); hit = x; break;
                        }
                    if (hit == null)
                    {
                        unmapped++;
                        if (unmappedExamples.Count < 6) unmappedExamples.Add(sg.From + "-" + sg.To);
                        continue;
                    }
                    mapped++;
                    CaesarPath cp = paths[hit[0]];
                    CaesarPlaced[] arr;
                    if (!at.TryGetValue(hit[0], out arr)) { arr = new CaesarPlaced[cp.Len.Length]; at[hit[0]] = arr; }
                    var placed = new CaesarPlaced();
                    placed.Seg = sg; placed.QA = hit[1]; placed.QB = hit[2];
                    for (int j = hit[1]; j < hit[2]; j++) arr[j] = placed;
                }
                if (mapped > maxMapped) maxMapped = mapped;
                if (unmapped > maxUnmapped) maxUnmapped = unmapped;
                // values at both ends of every beam that lies on a placed segment
                foreach (int bid in beamOrder)
                {
                    CaesarPlace pl;
                    if (!place.TryGetValue(bid, out pl)) continue;
                    CaesarPlaced[] arr;
                    if (!at.TryGetValue(pl.Elem, out arr) || pl.Seq >= arr.Length || arr[pl.Seq] == null) continue;
                    CaesarPlaced ps = arr[pl.Seq];
                    CaesarPath cp = paths[pl.Elem];
                    double span = cp.Cum[ps.QB] - cp.Cum[ps.QA];
                    double sA = cp.Cum[pl.Seq] + pl.TA * cp.Len[pl.Seq], sB = cp.Cum[pl.Seq] + pl.TB * cp.Len[pl.Seq];
                    double fA = span > 0 ? Clamp01((sA - cp.Cum[ps.QA]) / span) : 0, fB = span > 0 ? Clamp01((sB - cp.Cum[ps.QA]) / span) : 1;
                    for (int end = 0; end < 2; end++)
                    {
                        double f = end == 0 ? fA : fB;
                        if (!isStress)
                        {
                            // internal forces: -(FROM row) at the FROM node, +(TO row) at the TO node
                            var v = new double[6];
                            for (int k = 0; k < 6; k++) v[k] = Lerp(-ps.Seg.AtFrom[k], ps.Seg.AtTo[k], f);
                            force.Add(Rec(c, bid, end, new[] { v[0], v[1], v[2], Mag(v[1], v[2]) }));
                            moment.Add(Rec(c, bid, end, new[] { v[3], v[4], v[5], Mag(v[4], v[5]) }));
                        }
                        else
                        {
                            var v = new double[11];
                            for (int k = 0; k < 11; k++)
                                v[k] = k == 3 ? Lerp(-ps.Seg.AtFrom[k], ps.Seg.AtTo[k], f)      // torsion stress: signed like mx
                                              : Lerp(ps.Seg.AtFrom[k], ps.Seg.AtTo[k], f);
                            if (stressCols.Length > 0) stress.Add(Rec(c, bid, end, stressCols.Select(k => v[k]).ToArray()));
                            if (codeCols.Length > 0) code.Add(Rec(c, bid, end, codeCols.Select(k => v[k]).ToArray()));
                            if (sifCols.Length > 0) sif.Add(Rec(c, bid, end, sifCols.Select(k => v[k]).ToArray()));
                        }
                    }
                }
                casesDone++;
            }
            if (isStress) res.StressCases = casesDone; else res.ForceCases = casesDone;
        }
        res.SegmentsMapped = maxMapped;
        res.SegmentsUnmapped = maxUnmapped;
        if (maxUnmapped > 0)
            res.Warnings.Add("results: " + maxUnmapped + " element segment(s) of the force / stress reports match no run of the pipe between their two nodes (e.g. " +
                             string.Join(", ", unmappedExamples.ToArray()) + "): not drawn");

        string fu = CleanUnit(rr.ForceUnit), mu = CleanUnit(rr.MomentUnit), su = CleanUnit(rr.StressUnit);
        if (force.Count > 0)
        {
            foreach (string nm in new[] { "Axial fx", "Shear fy", "Shear fz", "Shear |V|" }) rs.Components.Add(new RawViewerWriter.Component(nm, "force", fu));
            foreach (string nm in new[] { "Torsion mx", "Bending my", "Bending mz", "Bending |M|" }) rs.Components.Add(new RawViewerWriter.Component(nm, "moment", mu));
            rs.Records.Add(new KeyValuePair<string, List<RawViewerWriter.BeamRecord>>("force", force));
            rs.Records.Add(new KeyValuePair<string, List<RawViewerWriter.BeamRecord>>("moment", moment));
        }
        string[] stressNames = { "SLP", "F/A", "Bending stress", "Torsion stress", "SIF in-plane", "SIF out-plane", "SIF torsion", "SIF axial",
                                 "Code stress", "Allowable", "Code ratio" };
        if (stress.Count > 0)
        {
            foreach (int k in stressCols) rs.Components.Add(new RawViewerWriter.Component(stressNames[k], "stress", su));
            rs.Records.Add(new KeyValuePair<string, List<RawViewerWriter.BeamRecord>>("stress", stress));
        }
        if (code.Count > 0)
        {
            foreach (int k in codeCols) rs.Components.Add(new RawViewerWriter.Component(stressNames[k], "code", k == 10 ? "%" : su));
            rs.Records.Add(new KeyValuePair<string, List<RawViewerWriter.BeamRecord>>("code", code));
        }
        if (sif.Count > 0)
        {
            foreach (int k in sifCols) rs.Components.Add(new RawViewerWriter.Component(stressNames[k], "sif", null));
            rs.Records.Add(new KeyValuePair<string, List<RawViewerWriter.BeamRecord>>("sif", sif));
        }
    }

    // ---- restraint loads: node totals at every beam end touching the node ----
    static void BuildRestraintLoads(CaesarResults rr, Dictionary<int, List<int[]>> nodeEnds, int[] cases, CaesarResultSet rs, CaesarExportResult res)
    {
        if (rr.RestraintLoads.Count == 0) return;
        var recs = new List<RawViewerWriter.BeamRecord>();
        var onPipe = new HashSet<int>();
        var off = new HashSet<int>();
        foreach (int c in cases)
        {
            Dictionary<int, double[]> tab;
            if (!rr.RestraintLoads.TryGetValue(c, out tab)) continue;
            res.RestraintCases++;
            foreach (var kv in tab)
            {
                List<int[]> ends;
                if (!nodeEnds.TryGetValue(kv.Key, out ends)) { off.Add(kv.Key); continue; }
                onPipe.Add(kv.Key);
                double[] v = kv.Value;
                double[] vals = { v[0], v[1], v[2], Mag(v[0], v[1], v[2]), v[3], v[4], v[5], Mag(v[3], v[4], v[5]) };
                foreach (int[] e in ends) recs.Add(Rec(c, e[0], e[1], vals));
            }
        }
        res.RestraintLoadNodes = onPipe.Count;
        res.RestraintLoadNodesOff = off.Count;
        if (off.Count > 0)
            res.Warnings.Add("results: restraint loads at " + off.Count + " node(s) that are on no pipe piece (e.g. " +
                             string.Join(", ", off.OrderBy(x => x).Take(6).Select(x => x.ToString(CultureInfo.InvariantCulture)).ToArray()) + "): not shown");
        if (recs.Count == 0) return;
        string fu = CleanUnit(rr.RestraintForceUnit), mu = CleanUnit(rr.RestraintMomentUnit);
        foreach (string nm in new[] { "Restraint FX", "Restraint FY", "Restraint FZ", "Restraint |F|" }) rs.Components.Add(new RawViewerWriter.Component(nm, "restraint", fu));
        foreach (string nm in new[] { "Restraint MX", "Restraint MY", "Restraint MZ", "Restraint |M|" }) rs.Components.Add(new RawViewerWriter.Component(nm, "restraint", mu));
        rs.Records.Add(new KeyValuePair<string, List<RawViewerWriter.BeamRecord>>("restraint", recs));
    }

    // ---- supports ------------------------------------------------------------------------------

    static readonly string[] DofNames = { "tx", "ty", "tz", "rx", "ry", "rz" };

    // one restraint as a sidecar entry: what the viewer needs to draw it
    //   kind      anchor | translation | oneway | guide | limit | rotation | other
    //   direction unit vector the restraint acts along (oneway: the direction it pushes; guide: horizontal across
    //             the pipe -- CAESAR's guide / limit cosines are dummies, so both come from the pipe axis); absent
    //             for an anchor and for a guide on a vertical pipe ("vertical": true, both horizontal directions)
    //   axis      the pipe axis at the node;  gap / friction / stiffness (absent = rigid) / cnode / tag as written
    static string RestraintJson(CaesarRestraint r, CaesarPipeGraph g, double[] up, string[] dof)
    {
        string type = (r.TypeName ?? "").Trim();
        if (type.Length == 0) type = "OTHER(" + I(r.TypeCode) + ")";
        double[] axis = Unit(g.AxisAt(r.ElementIndex, r.Node));
        double[] cos = Unit(r.Cosines);
        string kind = "other", sign = null;
        double[] dir = null;
        bool vertical = false;
        string state = r.Gap > 0 ? "gap" : !r.IsRigid ? "spring" : "fixed";
        switch (type)
        {
            case "ANC":
                kind = "anchor";
                for (int k = 0; k < 6; k++) SetDof(dof, k, "fixed");
                break;
            case "X": case "Y": case "Z":
                kind = "translation";
                dir = cos ?? AxisUnit(type[0]);
                SetDof(dof, AlignedDof(dir, 0), state);
                break;
            case "+X": case "+Y": case "+Z": case "-X": case "-Y": case "-Z":
                kind = "oneway";
                sign = type.Substring(0, 1);
                dir = AxisUnit(type[1]);
                if (sign == "-") dir = new[] { -dir[0], -dir[1], -dir[2] };
                SetDof(dof, AlignedDof(dir, 0), sign);
                break;
            case "RX": case "RY": case "RZ":
                kind = "rotation";
                dir = cos ?? AxisUnit(type[1]);
                SetDof(dof, AlignedDof(dir, 3), state);
                break;
            case "GUI":
                kind = "guide";
                if (axis != null && Math.Abs(Dot(axis, up)) > Math.Cos(5 * Math.PI / 180)) vertical = true;
                else if (axis != null) dir = Unit(Cross(up, axis));
                if (vertical) { for (int k = 0; k < 3; k++) if (up[k] == 0) SetDof(dof, k, "guide"); }
                else SetDof(dof, AlignedDof(dir, 0), "guide");
                break;
            case "LIM":
                kind = "limit";
                dir = axis;
                SetDof(dof, AlignedDof(dir, 0), "limit");
                break;
            default:
                dir = cos;
                break;
        }
        var sb = new StringBuilder("{");
        JProp(sb, "type", type); JProp(sb, "kind", kind);
        if (dir != null) JVec(sb, "direction", dir);
        if (vertical) JRaw(sb, "vertical", "true");
        if (sign != null) JProp(sb, "sign", sign);
        if (axis != null) JVec(sb, "axis", axis);
        if (r.Gap > 0) JNum(sb, "gap", r.Gap);
        if (r.Friction > 0) JNum(sb, "friction", r.Friction);
        if (!r.IsRigid && r.Stiffness > 0) JNum(sb, "stiffness", r.Stiffness);
        if (r.CNode > 0) JRaw(sb, "cnode", I(r.CNode));
        if (!string.IsNullOrEmpty(r.Tag) && r.Tag.Trim().Length > 0) JProp(sb, "tag", r.Tag.Trim());
        return sb.Append("}").ToString();
    }

    static string HangerJson(CaesarHanger h, double[] up, string[] dof)
    {
        SetDof(dof, AlignedDof(up, 0), "hanger");
        var sb = new StringBuilder("{");
        JProp(sb, "type", "HGR"); JProp(sb, "kind", "hanger"); JVec(sb, "direction", up);
        if (h.CNode > 0) JRaw(sb, "cnode", I(h.CNode));
        if (!double.IsNaN(h.ColdLoad) && !double.IsInfinity(h.ColdLoad)) JNum(sb, "coldLoad", h.ColdLoad);
        if (!double.IsNaN(h.HotLoad) && !double.IsInfinity(h.HotLoad)) JNum(sb, "hotLoad", h.HotLoad);
        if (!string.IsNullOrEmpty(h.Tag) && h.Tag.Trim().Length > 0) JProp(sb, "tag", h.Tag.Trim());
        return sb.Append("}").ToString();
    }

    static string DisplacementJson(CaesarDisplacement d, string[] dof)
    {
        var sb = new StringBuilder("{");
        JProp(sb, "type", "DISP"); JProp(sb, "kind", "imposed");
        sb.Append(", \"fixed\": [");
        for (int k = 0; k < 6; k++)
        {
            bool f = d.Fixed != null && k < d.Fixed.Length && d.Fixed[k];
            if (f) SetDof(dof, k, "imposed");
            sb.Append(k > 0 ? ", " : "").Append(f ? "true" : "false");
        }
        sb.Append("]");
        return sb.Append("}").ToString();
    }

    // the first restraint on a dof names it; "fixed" wins over a weaker state
    static void SetDof(string[] dof, int k, string state)
    {
        if (k < 0 || k > 5 || state == null) return;
        if (dof[k] == null || state == "fixed") dof[k] = state;
    }

    // the dof (base 0 = translations, 3 = rotations) of a direction along a global axis; -1 for a skewed one
    static int AlignedDof(double[] d, int baseIndex)
    {
        if (d == null) return -1;
        for (int k = 0; k < 3; k++) if (Math.Abs(d[k]) > 0.999) return baseIndex + k;
        return -1;
    }

    static double[] AxisUnit(char c)
    {
        switch (char.ToUpperInvariant(c))
        {
            case 'X': return new double[] { 1, 0, 0 };
            case 'Y': return new double[] { 0, 1, 0 };
            default: return new double[] { 0, 0, 1 };
        }
    }

    static double[] Unit(double[] v)
    {
        if (v == null || v.Length < 3) return null;
        double l = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
        if (!(l > 1e-12) || double.IsInfinity(l)) return null;
        return new[] { v[0] / l, v[1] / l, v[2] / l };
    }

    static double Dot(double[] a, double[] b) { return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]; }
    static double[] Cross(double[] a, double[] b) { return new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] }; }

    static void JSep(StringBuilder sb) { if (sb.Length > 1) sb.Append(", "); }
    static void JProp(StringBuilder sb, string k, string v) { JSep(sb); sb.Append(JStr(k)).Append(": ").Append(JStr(v)); }
    static void JRaw(StringBuilder sb, string k, string raw) { JSep(sb); sb.Append(JStr(k)).Append(": ").Append(raw); }
    static void JNum(StringBuilder sb, string k, double v) { JRaw(sb, k, v.ToString("R", CultureInfo.InvariantCulture)); }
    static void JVec(StringBuilder sb, string k, double[] v)
    {
        JRaw(sb, k, "[" + string.Join(", ", v.Select(x => Math.Round(x, 9).ToString("R", CultureInfo.InvariantCulture)).ToArray()) + "]");
    }
    static string JStr(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in s ?? "")
        {
            if (c == '"') sb.Append("\\\"");
            else if (c == '\\') sb.Append("\\\\");
            else if (c < 0x20 || c > 0x7e) sb.Append("\\u").Append(((int)c).ToString("x4"));
            else sb.Append(c);
        }
        return sb.Append('"').ToString();
    }

    static void AddTo<T>(Dictionary<int, List<T>> map, int key, T item)
    {
        List<T> l;
        if (!map.TryGetValue(key, out l)) { l = new List<T>(); map[key] = l; }
        l.Add(item);
    }

    // an attached node (tee surface node, CNODE on no element) stands for its host node on the pipe
    static int OnPipe(CaesarPipeGraph g, int node)
    {
        int host;
        if (!g.Nodes.ContainsKey(node) && g.AttachedHost.TryGetValue(node, out host)) return host;
        return node;
    }

    static RawViewerWriter.BeamRecord Rec(int lc, int bid, int end, double[] v)
    {
        var r = new RawViewerWriter.BeamRecord();
        r.LC = lc; r.elemID = bid; r.End = end;
        r.Values = new float[v.Length];
        for (int i = 0; i < v.Length; i++) r.Values[i] = (float)v[i];
        return r;
    }

    static double Lerp(double a, double b, double f) { return a + (b - a) * f; }
    static double Clamp01(double f) { return f < 0 ? 0 : f > 1 ? 1 : f; }

    // magnitude of the finite parts; NaN when none is a number
    static double Mag(params double[] v)
    {
        double s = 0;
        bool any = false;
        foreach (double x in v) if (!double.IsNaN(x)) { s += x * x; any = true; }
        return any ? Math.Sqrt(s) : double.NaN;
    }

    // "in." -> in, "mm." -> mm ...; "" when not a length unit
    static string NormLen(string label)
    {
        string s = (label ?? "").Trim().TrimEnd('.').Trim().ToLowerInvariant();
        switch (s)
        {
            case "in": case "inch": case "inches": return "in";
            case "mm": case "millimeter": case "millimetre": return "mm";
            case "cm": return "cm";
            case "m": case "meter": case "metre": return "m";
            case "ft": case "feet": case "foot": return "ft";
        }
        return "";
    }

    static double InchesPer(string unit)
    {
        switch (unit)
        {
            case "mm": return 1.0 / 25.4;
            case "cm": return 1.0 / 2.54;
            case "m": return 1.0 / 0.0254;
            case "ft": return 12.0;
            default: return 1.0;
        }
    }

    // a report unit made readable: "lb." -> lb, "ft.lb." -> ft-lb, "lb./sq.in." -> psi, "N.m." -> N-m, "deg." -> deg
    static string CleanUnit(string u)
    {
        string s = (u ?? "").Trim();
        switch (s.ToLowerInvariant())
        {
            case "": return null;
            case "lb.": case "lb": case "lbf": return "lb";
            case "kips": case "kip": case "kips.": case "kip.": return "kip";
            case "ft.lb.": case "ft.lb": return "ft-lb";
            case "in.lb.": case "in.lb": return "in-lb";
            case "ft.kip.": case "ft.kips.": return "ft-kip";
            case "lb./sq.in.": case "lb/sq.in.": case "lb./sq.in": case "psi": return "psi";
            case "kips/sq.in.": case "ksi": return "ksi";
            case "n.": case "n": return "N";
            case "kn.": case "kn": return "kN";
            case "n.m.": case "n.m": case "nm.": return "N-m";
            case "kn.m.": case "kn.m": return "kN-m";
            case "n./sq.mm.": case "n/sq.mm.": case "mpa": case "mpa.": return "MPa";
            case "kpa": case "kpa.": return "kPa";
            case "deg.": case "deg": return "deg";
            case "rad.": case "rad": return "rad";
            case "%": return "%";
        }
        return s.TrimEnd('.');
    }

    // ---- helpers -----------------------------------------------------------------------------

    // a copy of the options with every display factor usable (> 0, finite); the arc step is CaesarGeometry's to clamp
    static CaesarOptions Checked(CaesarOptions o)
    {
        var d = new CaesarOptions();
        if (o == null) return d;
        var c = new CaesarOptions();
        c.ArcStepDeg = o.ArcStepDeg;
        c.ValveEndFactor = Factor(o.ValveEndFactor, d.ValveEndFactor);
        c.ValveWaistFactor = Factor(o.ValveWaistFactor, d.ValveWaistFactor);
        c.FlangeFactor = Factor(o.FlangeFactor, d.FlangeFactor);
        c.RigidFactor = Factor(o.RigidFactor, d.RigidFactor);
        c.RigidLinkFactor = Factor(o.RigidLinkFactor, d.RigidLinkFactor);
        c.ExpJointFactor = Factor(o.ExpJointFactor, d.ExpJointFactor);
        if (Math.Abs(c.ValveEndFactor - c.ValveWaistFactor) < 1e-9) c.ValveWaistFactor = c.ValveEndFactor * 0.4;   // a bow-tie needs a waist
        return c;
    }

    static double Factor(double v, double dflt) { return v > 0 && v < 100 ? v : dflt; }

    static void AddBeam(Dictionary<int, RawViewerWriter.BeamMember> members, List<int> order, int id, int a, int b, int secA, int secB, double[] localY)
    {
        if (members.ContainsKey(id)) throw new Exception("CaesarToPluto: duplicate beam id " + id + ".");
        var bm = new RawViewerWriter.BeamMember();
        bm.Id = id; bm.NodeA = a; bm.NodeB = b; bm.SectionIndex = secA; bm.SectionIndexB = secB;
        bm.LocalY = localY;
        members[id] = bm;
        order.Add(id);
    }

    // a section-local y that is not along the pipe (PIPE is axisymmetric): world up, or X when the pipe is vertical
    static double[] LocalY(double[] a, double[] b, bool zUp)
    {
        double dx = b[0] - a[0], dy = b[1] - a[1], dz = b[2] - a[2];
        double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        double up = len > 0 ? (zUp ? dz : dy) / len : 0;
        if (Math.Abs(up) > 0.99) return new double[] { 1, 0, 0 };
        return zUp ? new double[] { 0, 0, 1 } : new double[] { 0, 1, 0 };
    }

    // node position minus the recentre offset, BEFORE the float cast (Node.xyz is float32)
    static Node MakeNode(int id, double[] p, double[] off)
    {
        var n = new Node();
        n.id = id;
        n.xyz.X = (float)(p[0] - off[0]);
        n.xyz.Y = (float)(p[1] - off[1]);
        n.xyz.Z = (float)(p[2] - off[2]);
        return n;
    }

    static bool Finite(double[] p)
    {
        return p != null && p.Length >= 3 && !double.IsNaN(p[0] + p[1] + p[2]) && !double.IsInfinity(p[0] + p[1] + p[2]);
    }

    // a piece that becomes a beam: it has a length and both ends have a position that is a number
    static bool Drawable(CaesarPipeGraph g, CaesarPiece p)
    {
        double[] a, b;
        return p.Length > 0 && g.Nodes.TryGetValue(p.FromNode, out a) && g.Nodes.TryGetValue(p.ToNode, out b) && Finite(a) && Finite(b);
    }

    static void AddTo(Dictionary<string, List<uint>> map, string key, uint id)
    {
        List<uint> l;
        if (!map.TryGetValue(key, out l)) { l = new List<uint>(); map[key] = l; }
        l.Add(id);
    }

    static void AddToken(Dictionary<int, List<string>> map, int node, string token)
    {
        List<string> l;
        if (!map.TryGetValue(node, out l)) { l = new List<string>(); map[node] = l; }
        l.Add(token);
    }

    // order of a support token in a combination name: TypeOrder, then anything else (OTHER(n)) after
    static int Rank(string token)
    {
        string t = token.EndsWith(" w/gap") ? token.Substring(0, token.Length - 6) : token;
        int i = Array.IndexOf(TypeOrder, t);
        return i >= 0 ? i : TypeOrder.Length;
    }

    // group names must be unique (MergeFrom matches by name)
    static string Unique(HashSet<string> used, string name)
    {
        string n = name;
        for (int k = 2; !used.Add(n); k++) n = name + " (" + I(k) + ")";
        return n;
    }

    static string SizeColor(int i, int n)
    {
        if (n <= 1) return SizeRamp[6];
        int idx = (int)Math.Round((double)i * (SizeRamp.Length - 1) / Math.Max(n - 1, 1));
        return SizeRamp[Math.Min(Math.Max(idx, 0), SizeRamp.Length - 1)];
    }

    static string I(int v) { return v.ToString(CultureInfo.InvariantCulture); }
    static string F(double v) { return v.ToString("0.####", CultureInfo.InvariantCulture); }

    // size / section names in the units the job writes diameters and walls in
    sealed class Namer
    {
        readonly CaesarUnits _u;
        readonly string _d, _w;
        public Namer(CaesarUnits u, string dUnit, string wUnit) { _u = u; _d = dUnit; _w = wUnit; }
        string Od(double odLen) { return F(_u.Convert(odLen, CaesarUnits.Length, CaesarUnits.Diameter)); }
        string Wall(double wallLen) { return F(_u.Convert(wallLen, CaesarUnits.Length, CaesarUnits.Wall)); }
        public string Size(double odLen, double wallLen)
        {
            if (_d == _w) return "PIPE " + Od(odLen) + " x " + Wall(wallLen) + (_d.Length > 0 ? " " + _d : "");
            return "PIPE " + Od(odLen) + (_d.Length > 0 ? " " + _d : "") + " x " + Wall(wallLen) + (_w.Length > 0 ? " " + _w : "");
        }
        // "VALVE 16 in end (1.5x OD)"
        public string Display(string what, double odLen, double factor, string role)
        {
            return what + " " + Od(odLen) + (_d.Length > 0 ? " " + _d : "") + role + " (" + F(factor) + "x OD)";
        }
    }

    // SECT table: true pipe sections deduplicated by (od, wall) to 1e-6, display sections by (role, od)
    sealed class SectionTable
    {
        public readonly List<RawViewerWriter.SectionDef> List = new List<RawViewerWriter.SectionDef>();
        public int PipeCount;
        readonly Dictionary<string, int> _index = new Dictionary<string, int>();

        static string Key(string role, double od, double wall)
        {
            return role + "|" + Math.Round(od * 1e6).ToString("R", CultureInfo.InvariantCulture) + "|" +
                   Math.Round(wall * 1e6).ToString("R", CultureInfo.InvariantCulture);
        }

        public int Pipe(string name, double od, double wall)
        {
            int i;
            string k = Key("PIPE", od, wall);
            if (_index.TryGetValue(k, out i)) return i;
            i = List.Count;
            List.Add(RawViewerWriter.SectionDef.Pipe(name, (float)od, (float)wall));
            _index[k] = i;
            PipeCount++;
            return i;
        }

        // solid display section (wall 0): the drawn size of a component, not its analysis size
        public int Display(string role, string name, double od)
        {
            int i;
            string k = Key(role, od, 0);
            if (_index.TryGetValue(k, out i)) return i;
            i = List.Count;
            List.Add(RawViewerWriter.SectionDef.Pipe(name, (float)od, 0f));
            _index[k] = i;
            return i;
        }
    }
}
