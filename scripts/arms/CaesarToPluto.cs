using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

// CaesarToPluto  --  CAESAR II neutral file (.cii) -> Pluto v4 viewer model: <base>.bin (one beam
// domain) + <base>.features.json (groups). GEOMETRY ONLY (stage 1): a results workbook is not read yet
// (a non-empty resultsPath gives a warning and the export stays geometry-only).
// C# 5 / Add-Type (PowerShell 5.1) compatible; every type is prefixed Caesar (one global batch).
//
// Chain: CaesarNeutralReader.Read -> CaesarGeometry.Build (scripts/arms/CaesarGeometry.cs) ->
// RawViewerWriter, beam-only, Write(false) (geometry-only profile: no load cases, no field blocks) ->
// FeaturesSidecar (exporter tag "caesar", merged with the previous sidecar).
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
//   one layout-only beam component ("DX", kind displacement, no planes) -- no load cases.
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
// The groups are built first, then MergeFrom(previous sidecar) restyles them by name and appends the
// user's own groups and sections (the SapToPluto order: MergeFrom matches against the groups present).
//
// Usage from PowerShell 5.1 (scripts/caesar/Run-Caesar.ps1 wraps this):
//   . <repo>\scripts\lib\Config.ps1
//   $r = [CaesarToPluto]::Export('<project dir>\job.cii', $null, '<project dir>\viewer\job', 'caesar/job')
//   $r.Summary(); $r.Warnings
// then open job.bin TOGETHER with job.features.json in the viewer (the groups live in the sidecar).

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
    public bool ResultsIgnored;            // a results file was given: not read yet (next stage)
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
            ResultsIgnored ? "geometry only (results file NOT read yet: next stage)" : "geometry only"));
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
        sb.AppendLine("  open the .bin TOGETHER with the .features.json in the viewer: groups and support markers live in the sidecar");
        return sb.ToString();
    }

    // The runner's "RESULT {json}" payload: compact, ASCII (non-ASCII escaped).
    public string ToJson()
    {
        var sb = new StringBuilder("{");
        Prop(sb, "neutral", NeutralPath); Prop(sb, "results", ResultsPath); Prop(sb, "resultsIgnored", ResultsIgnored);
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
        if (resultsPath != null)
        {
            res.ResultsIgnored = true;
            res.Warnings.Add("results not read yet (next stage): " + resultsPath + (File.Exists(resultsPath) ? "" : " (file not found)") +
                             " -- exported geometry only");
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
            foreach (int pi in plist)
            {
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

        // ---- binary ----
        string binPath = outBase + ".bin";
        string dir = Path.GetDirectoryName(Path.GetFullPath(binPath));
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        var units = new Dictionary<string, string>();
        if (lengthUnit.Length > 0) units["length"] = lengthUnit;
        var beamComps = new List<RawViewerWriter.Component>();
        beamComps.Add(new RawViewerWriter.Component("DX", "displacement", lengthUnit.Length > 0 ? lengthUnit : null));   // layout only; no planes written
        var w = new RawViewerWriter(binPath, nodes, null, null, null, members, secs.List, beamComps, modelId, units);
        w.UpAxis = res.UpAxis;
        w.SetBeamLabels(beamLabels);
        if (nodeLabels.Count > 0) w.SetNodeLabels(nodeLabels);
        w.Write(false);
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

        // the previous sidecar is the user's layer: section cuts, predicates, hand-made groups and the
        // colour / enable / id of our own groups survive a re-export (groups first, then merge: SapToPluto)
        string scPath = outBase + ".features.json";
        res.SidecarMerge = sc.MergeFrom(scPath, "caesar");
        res.Groups = sc.Groups.Count;
        File.WriteAllText(scPath, sc.ToJson(), new UTF8Encoding(false));
        res.SidecarPath = scPath;
        return res;
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
