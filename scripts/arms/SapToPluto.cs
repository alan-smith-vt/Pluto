using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

// SapToPluto  --  SAP2000 .s2k / .$2k text export -> Pluto v4 shell binary +
// features sidecar. C# 5 / Add-Type (PowerShell 5.1) compatible.
//
// Replaces the Python s2k_to_bin.py from the SapViewer repo (archived at
// pythonTools/sap/), which wrote the v3 format. This arm reuses the lib:
//   scripts/lib/readers/SapReader.cs   Sap2kParser (tables) + Sap2kReader (geometry)
//   scripts/lib/writers/RawViewerWriter.cs   v4 writer (AppendShellValues / AppendDisplacements)
//   scripts/lib/sidecar/FeaturesSidecar.cs   groups
//
// Inputs
//   modelS2k     geometry export (File > Export > SAP2000 .s2k), always required.
//   resultsS2k   OPTIONAL results export with "Element Forces - Area Shells"
//                (output at joints, not just element centres) and
//                "Joint Displacements". May be the same file as modelS2k
//                (pass the same path, or null to look in modelS2k).
//
// Mapping
//   JOINT COORDINATES          -> nodes, SAP global axes kept AS-IS (no STAAD
//                                 rotation; tick "Z up" in the viewer). Local
//                                 coordinate systems are resolved by Sap2kReader.
//   CONNECTIVITY - AREA        -> shell elements (tri / quad). Joint and Area
//                                 labels must be integers (SAP default); the
//                                 label text is also written to the LABL block.
//   CONNECTIVITY - FRAME       -> beam domain (2026-09-03). Section from FRAME
//   FRAME SECTION ASSIGNMENTS     SECTION ASSIGNMENTS (AnalSect) + FRAME SECTION
//   FRAME SECTION PROPERTIES 01   PROPERTIES 01 - GENERAL (Shape, t3 t2 tf tw):
//                                 Rectangular / Pipe / Box / I / Angle / Channel /
//                                 Tee map to the writer's parametric sections;
//                                 Double Angle draws as a Tee (stem = 2 tw);
//                                 unknown shapes get a small RECT placeholder.
//   <model>.outlines.txt         OPTIONAL, beside the model .s2k: "NAME: y,z y,z ..."
//                                 per section (section-local, centroid origin,
//                                 file units) -> POLY outline, wins over Shape.
//                                 The tank builder writes it for its Z eave ring.
//                                 Local 2 = SAP default (vertical plane; +X for
//                                 vertical members); roll angles are NOT read.
//                                 Viewer frame: z = x cross y carries the depth
//                                 (schema 4.3), so LocalY = local2 cross local1
//                                 = MINUS SAP local 3.
//                                 Beam ends get the joint displacements; frame
//                                 forces are not exported yet.
//   ELEMENT FORCES - AREA SHELLS -> per-corner "stress" components, SAP order
//                                 F11 F22 F12 M11 M22 M12 V13 V23, raw file units
//                                 (forces per unit length -- continuous across a
//                                 thickness change), then derived MEMBRANE stresses
//                                 S11 S22 S12 = F / section thickness, which do
//                                 step at a change. ksi when the file is Kip/ft
//                                 or Kip/in, else force/length^2. Bending faces
//                                 (+/- 6M/t^2) are not derived.
//   JOINT DISPLACEMENTS        -> "displacement" U1..R3 rotated from the joint's
//                                 LOCAL axes (JOINT LOCAL AXES ASSIGNMENTS 1 -
//                                 TYPICAL, Rz(A)Ry(B)Rx(C)) into global, fanned
//                                 to every corner of every element on the joint.
//                                 Optional cylindrical pair (Translation R / T
//                                 about the plan centroid of all nodes) for tanks.
//   OutputCase                 -> load cases, first-seen order, ids 1..N.
//   PROGRAM CONTROL CurrUnits  -> units["force"], units["length"] ("Kip, ft, F").
//
// Sidecar groups (everything a user might filter/colour by; nothing goes in
// the binary that is not a result):
//   one per area SECTION name, thickness in the name ("WALL_T1 (0.500 in)")
//                                      tags sap, section, thickness  (shells)
//   one per distinct restraint pattern tags sap, restraint      (nodes)
//   "Local axes assigned"              tags sap, localAxes      (nodes)
//   one per FRAME section name         tags sap, frameSection   (beams)
//   SAP GROUPS 2 - ASSIGNMENTS         tags sap, group          (areas / joints / frames)
// Hydrostatic head (JOINT PATTERN ASSIGNMENTS) is deliberately NOT exported;
// it is a continuous scalar, not a category -- add it as a "model" component
// if it is ever wanted.
//
// Usage from PowerShell 5.1:
//   . <repo>\scripts\lib\Config.ps1
//   $r = [SapToPluto]::Export('C:\Temp\tank.s2k', 'C:\Temp\results.s2k', 'C:\Temp\tank', 'sap/tank')
//   $r.Summary()
// then drop tank.bin + tank.features.json on the viewer together.

public class SapExportResult
{
    public string BinPath, SidecarPath;
    public int Nodes, Elements, LoadCases, ForceRows, ForceRowsUsed, DispRows, DispRowsUsed, Groups;
    public int Frames, FrameSections, FramesUnknownShape;
    public int NodesWithoutDisp;      // model joints with no JOINT DISPLACEMENTS row: results older than the model?
    public string ForceUnit, LengthUnit;
    public List<string> LoadCaseNames = new List<string>();
    public List<string> Warnings = new List<string>();

    public string Summary()
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Format("SapToPluto: {0} nodes, {1} shells, {2} load cases [{3}], units {4}/{5}",
            Nodes, Elements, LoadCases, string.Join(", ", LoadCaseNames), ForceUnit, LengthUnit));
        sb.AppendLine(string.Format("  shell force rows {0} (used {1}), joint displacement rows {2} (used {3})",
            ForceRows, ForceRowsUsed, DispRows, DispRowsUsed));
        if (NodesWithoutDisp > 0)
            sb.AppendLine(string.Format("  WARNING: {0} of {1} joints have no displacement row (they will not move) -- results file older than the model?",
                NodesWithoutDisp, Nodes));
        if (Frames > 0)
            sb.AppendLine(string.Format("  {0} frames -> beams, {1} section(s){2}", Frames, FrameSections,
                FramesUnknownShape > 0 ? string.Format(", {0} with an unknown shape (RECT placeholder)", FramesUnknownShape) : ""));
        sb.AppendLine(string.Format("  {0} groups -> {1}", Groups, SidecarPath));
        sb.AppendLine("  bin -> " + BinPath);
        foreach (string w in Warnings) sb.AppendLine("  WARN " + w);
        return sb.ToString();
    }
}

public class SapToPluto
{
    // SAP column -> component name. Force units are per unit length.
    static readonly string[] ForceKeys = { "F11", "F22", "F12", "M11", "M22", "M12", "V13", "V23" };
    static readonly string[] DispKeys = { "U1", "U2", "U3", "R1", "R2", "R3" };
    static readonly string[] DispNames = {
        "Translation X", "Translation Y", "Translation Z", "Rotation X", "Rotation Y", "Rotation Z" };

    public static SapExportResult Export(string modelS2k, string resultsS2k, string outBase, string modelId)
    {
        return Export(modelS2k, resultsS2k, outBase, modelId, false);
    }

    // cylindrical: add "Translation R" / "Translation T" displacement components
    // resolved about the plan (X,Y) centroid of all nodes -- tanks and silos.
    public static SapExportResult Export(string modelS2k, string resultsS2k, string outBase, string modelId, bool cylindrical)
    {
        if (modelS2k == null || !File.Exists(modelS2k)) throw new Exception("SapToPluto: model file not found: " + modelS2k);
        var res = new SapExportResult();

        // ---- parse ----
        var model = new Sap2kParser(modelS2k);
        Sap2kParser results = model;
        if (string.IsNullOrEmpty(resultsS2k)) resultsS2k = null;   // PowerShell $null arrives as ""
        if (resultsS2k != null && !string.Equals(Path.GetFullPath(resultsS2k), Path.GetFullPath(modelS2k), StringComparison.OrdinalIgnoreCase))
        {
            if (!File.Exists(resultsS2k)) throw new Exception("SapToPluto: results file not found: " + resultsS2k);
            results = new Sap2kParser(resultsS2k);
        }

        // ---- units ----
        string forceUnit = "kip", lengthUnit = "ft";
        ParseUnits(model, ref forceUnit, ref lengthUnit);
        res.ForceUnit = forceUnit; res.LengthUnit = lengthUnit;

        // ---- geometry (SAP axes, no STAAD rotation) ----
        CheckIntegerLabels(model, res);
        GeometryResult geo = Sap2kReader.ExtractGeometry(model, false);
        Dictionary<int, Node> nodes = geo.NodeDict;
        Dictionary<int, Element> elements = geo.ElementDict;
        if (nodes.Count == 0) throw new Exception("SapToPluto: no JOINT COORDINATES rows in " + modelS2k);
        if (elements.Count == 0) throw new Exception("SapToPluto: no CONNECTIVITY - AREA rows in " + modelS2k);
        res.Nodes = nodes.Count; res.Elements = elements.Count;

        // ---- frames -> beam domain ----
        var frameSecOf = new Dictionary<int, string>();
        foreach (var r in model.GetTable("FRAME SECTION ASSIGNMENTS"))
        {
            string f, s;
            if (r.TryGetValue("Frame", out f) && r.TryGetValue("AnalSect", out s)) frameSecOf[Int(f)] = s;
        }
        var frameSecProps = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in model.GetTable("FRAME SECTION PROPERTIES 01 - GENERAL"))
        {
            string s;
            if (r.TryGetValue("SectionName", out s)) frameSecProps[s] = r;
        }
        Dictionary<string, float[][]> outlines = ReadOutlines(Path.ChangeExtension(modelS2k, ".outlines.txt"));
        var beams = new Dictionary<int, RawViewerWriter.BeamMember>();
        var beamLabels = new Dictionary<int, string>();
        var sectionDefs = new List<RawViewerWriter.SectionDef>();
        var sectionIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var beamsBySection = new Dictionary<string, List<uint>>(StringComparer.OrdinalIgnoreCase);
        var beamSectionOrder = new List<string>();
        foreach (var r in model.GetTable("CONNECTIVITY - FRAME"))
        {
            string f, ji, jj;
            if (!r.TryGetValue("Frame", out f) || !r.TryGetValue("JointI", out ji) || !r.TryGetValue("JointJ", out jj)) continue;
            int fid = Int(f), a = Int(ji), b = Int(jj);
            Node na, nb;
            if (!nodes.TryGetValue(a, out na) || !nodes.TryGetValue(b, out nb) || a == b) continue;
            string secName;
            if (!frameSecOf.TryGetValue(fid, out secName)) secName = "UNSECTIONED";
            int si;
            if (!sectionIndex.TryGetValue(secName, out si))
            {
                Dictionary<string, string> props;
                bool known = true;
                float[][] outline;
                RawViewerWriter.SectionDef def;
                if (outlines.TryGetValue(secName, out outline)) def = RawViewerWriter.SectionDef.Poly(secName, outline);
                else def = FrameSection(secName, frameSecProps.TryGetValue(secName, out props) ? props : null, out known);
                if (!known) res.FramesUnknownShape++;
                si = sectionDefs.Count;
                sectionDefs.Add(def);
                sectionIndex[secName] = si;
            }
            var m = new RawViewerWriter.BeamMember();
            m.Id = fid; m.NodeA = a; m.NodeB = b; m.SectionIndex = si;
            m.LocalY = DefaultLocalY(na, nb);
            beams[fid] = m;
            beamLabels[fid] = f;
            Add(beamsBySection, beamSectionOrder, secName, (uint)fid);
        }
        res.Frames = beams.Count; res.FrameSections = sectionDefs.Count;

        // ---- load cases (first-seen order across both result tables) ----
        List<Dictionary<string, string>> forceRows = results.GetTable("ELEMENT FORCES - AREA SHELLS");
        List<Dictionary<string, string>> dispRows = results.GetTable("JOINT DISPLACEMENTS");
        res.ForceRows = forceRows.Count; res.DispRows = dispRows.Count;
        var lcNames = new Dictionary<int, string>();
        var lcByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in forceRows) LcId(CaseOf(r), lcByName, lcNames);
        foreach (var r in dispRows) LcId(CaseOf(r), lcByName, lcNames);
        res.LoadCases = lcNames.Count;
        res.LoadCaseNames.AddRange(lcNames.OrderBy(kv => kv.Key).Select(kv => kv.Value));
        bool hasResults = lcNames.Count > 0;

        // ---- components ----
        string[] forceKeysPresent = ForceKeys;
        if (forceRows.Count > 0) forceKeysPresent = ForceKeys.Where(k => forceRows[0].ContainsKey(k)).ToArray();
        bool hasForces = forceRows.Count > 0 && forceKeysPresent.Length > 0;
        bool hasDisp = dispRows.Count > 0;
        int nDisp = hasDisp ? (cylindrical ? 8 : 6) : 0;

        // area sections: element -> section name, section -> thickness (file length units)
        var sectionOf = new Dictionary<int, string>();
        foreach (var r in model.GetTable("AREA SECTION ASSIGNMENTS"))
        {
            string a, s;
            if (r.TryGetValue("Area", out a) && r.TryGetValue("Section", out s)) sectionOf[Int(a)] = s;
        }
        var thickOf = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in model.GetTable("AREA SECTION PROPERTIES"))
        {
            string s; double t = Num(r, "Thickness", double.NaN);
            if (r.TryGetValue("Section", out s) && !double.IsNaN(t) && t > 0) thickOf[s] = t;
        }
        double stressScale; string stressUnit;
        StressUnits(forceUnit, lengthUnit, out stressScale, out stressUnit);
        // derived membrane stresses: which force columns have a stress twin
        var stressFrom = new List<int>();
        var stressNames = new List<string>();
        if (hasForces && thickOf.Count > 0)
        {
            for (int c = 0; c < forceKeysPresent.Length; c++)
            {
                string k = forceKeysPresent[c];
                if (k == "F11" || k == "F22" || k == "F12") { stressFrom.Add(c); stressNames.Add("S" + k.Substring(1)); }
            }
        }

        var comps = new List<RawViewerWriter.Component>();
        if (hasForces)
        {
            foreach (string k in forceKeysPresent)
                comps.Add(new RawViewerWriter.Component(k, "stress", k[0] == 'M' ? forceUnit + "-" + lengthUnit + "/" + lengthUnit : forceUnit + "/" + lengthUnit));
            foreach (string k in stressNames)
                comps.Add(new RawViewerWriter.Component(k, "stress", stressUnit));
        }
        if (hasDisp)
        {
            for (int i = 0; i < 6; i++) comps.Add(new RawViewerWriter.Component(DispNames[i], "displacement", i < 3 ? lengthUnit : "rad"));
            if (cylindrical)
            {
                comps.Add(new RawViewerWriter.Component("Translation R", "displacement", lengthUnit));
                comps.Add(new RawViewerWriter.Component("Translation T", "displacement", lengthUnit));
            }
        }
        if (comps.Count == 0) comps.Add(new RawViewerWriter.Component("F11", "stress", forceUnit + "/" + lengthUnit));   // layout only; geometry-only file

        // ---- labels ----
        var nodeLabels = new Dictionary<int, string>();
        foreach (var r in model.GetTable("JOINT COORDINATES")) { string s; if (r.TryGetValue("Joint", out s)) nodeLabels[Int(s)] = s; }
        var shellLabels = new Dictionary<int, string>();
        foreach (var r in model.GetTable("CONNECTIVITY - AREA")) { string s; if (r.TryGetValue("Area", out s)) shellLabels[Int(s)] = s; }

        // ---- binary ----
        var units = new Dictionary<string, string>();
        units["length"] = lengthUnit;
        units["force"] = forceUnit;
        string binPath = outBase + ".bin";
        List<RawViewerWriter.Component> beamComps = null;
        if (beams.Count > 0)
        {
            // beam ends carry the joint displacements (AppendDisplacements fans
            // them out); no frame forces yet, so a layout-only force component
            // when there are no displacements at all.
            beamComps = new List<RawViewerWriter.Component>();
            if (hasDisp)
                for (int i = 0; i < 6; i++) beamComps.Add(new RawViewerWriter.Component(DispNames[i], "displacement", i < 3 ? lengthUnit : "rad"));
            else
                beamComps.Add(new RawViewerWriter.Component("Axial N", "force", forceUnit));
        }
        var w = new RawViewerWriter(binPath, nodes, elements, hasResults ? lcNames : null, comps,
                                    beams.Count > 0 ? beams : null, beams.Count > 0 ? sectionDefs : null, beamComps,
                                    modelId, units);
        w.SetNodeLabels(nodeLabels);
        w.SetShellLabels(shellLabels);
        if (beams.Count > 0) w.SetBeamLabels(beamLabels);
        w.Write(hasResults);
        res.BinPath = binPath;

        if (hasForces)
        {
            var recs = new List<RawViewerWriter.CornerRecord>();
            foreach (var r in forceRows)
            {
                string a, j;
                if (!r.TryGetValue("Area", out a) && !r.TryGetValue("AreaElem", out a)) continue;
                if (!r.TryGetValue("Joint", out j)) continue;   // element-centre row
                int eid = Int(a), nid = Int(j);
                if (!elements.ContainsKey(eid)) continue;
                var rec = new RawViewerWriter.CornerRecord();
                rec.LC = lcByName[CaseOf(r)];
                rec.elemID = eid; rec.node = nid;
                rec.Values = new float[forceKeysPresent.Length + stressFrom.Count];
                for (int c = 0; c < forceKeysPresent.Length; c++) rec.Values[c] = (float)Num(r, forceKeysPresent[c], double.NaN);
                string sec; double thick = double.NaN;
                if (sectionOf.TryGetValue(eid, out sec)) thickOf.TryGetValue(sec, out thick);
                for (int c = 0; c < stressFrom.Count; c++)
                    rec.Values[forceKeysPresent.Length + c] = double.IsNaN(thick) ? float.NaN
                        : (float)(rec.Values[stressFrom[c]] / thick * stressScale);
                recs.Add(rec);
            }
            res.ForceRowsUsed = recs.Count;
            w.AppendShellValues(recs, "stress");
        }

        if (hasDisp)
        {
            Dictionary<int, double[]> frames = JointLocalFrames(model);
            double cx = 0, cy = 0;
            if (cylindrical)
            {
                foreach (Node n in nodes.Values) { cx += n.xyz.X; cy += n.xyz.Y; }
                cx /= nodes.Count; cy /= nodes.Count;
            }
            var disps = new List<Disp>();
            var seenNodes = new HashSet<int>();
            foreach (var r in dispRows)
            {
                string j;
                if (!r.TryGetValue("Joint", out j)) continue;
                int nid = Int(j);
                Node node;
                if (!nodes.TryGetValue(nid, out node)) continue;
                seenNodes.Add(nid);
                var d = new Disp();
                d.LC = lcByName[CaseOf(r)];
                d.node = nid;
                d.DR = new float[nDisp];
                double[] v = new double[6];
                for (int c = 0; c < 6; c++) v[c] = Num(r, DispKeys[c], double.NaN);
                double[] m;
                if (frames.TryGetValue(nid, out m))
                {
                    v = Rotate(m, v);
                }
                for (int c = 0; c < 6; c++) d.DR[c] = (float)v[c];
                if (cylindrical)
                {
                    double dx = node.xyz.X - cx, dy = node.xyz.Y - cy;
                    double rad = Math.Sqrt(dx * dx + dy * dy);
                    if (rad > 1e-9 && !double.IsNaN(v[0]) && !double.IsNaN(v[1]))
                    {
                        double ct = dx / rad, st = dy / rad;
                        d.DR[6] = (float)(v[0] * ct + v[1] * st);      // radial, + outward
                        d.DR[7] = (float)(-v[0] * st + v[1] * ct);     // tangential, + CCW about +Z
                    }
                    else { d.DR[6] = float.NaN; d.DR[7] = float.NaN; }
                }
                disps.Add(d);
            }
            res.DispRowsUsed = disps.Count;
            res.NodesWithoutDisp = nodes.Count - seenNodes.Count;
            w.AppendDisplacements(disps);
        }

        // ---- sidecar ----
        var sc = new FeaturesSidecar();
        sc.ModelId = modelId;
        sc.GeometryHash = w.GeometryHash;
        sc.Units["length"] = lengthUnit;
        sc.Units["force"] = forceUnit;
        sc.Units["worldOffset"] = "0 0 0";

        // sections: one group each, thickness in the name (inches when the file
        // is in ft or in, else file units). A separate per-thickness family was
        // dropped 2026-09-03 -- the tank builder writes one section per thickness,
        // so it only duplicated these.
        var bySection = new Dictionary<string, List<uint>>(StringComparer.OrdinalIgnoreCase);
        var sectionOrder = new List<string>();
        foreach (int eid in elements.Keys.OrderBy(k => k))
        {
            string s;
            if (!sectionOf.TryGetValue(eid, out s)) continue;
            Add(bySection, sectionOrder, s, (uint)eid);
        }
        foreach (string s in sectionOrder)
        {
            double t;
            string name = thickOf.TryGetValue(s, out t) ? s + " (" + ThicknessText(t, lengthUnit) + ")" : s;
            sc.AddGroup(name, null, "shells", bySection[s], new[] { "sap", "section", "thickness" }, StaadName(s));
        }

        // frame sections: one beam group each
        foreach (string s in beamSectionOrder)
            sc.AddGroup(s, null, "beams", beamsBySection[s], new[] { "sap", "frameSection" }, StaadName(s));

        // restraints (one node group per distinct pattern)
        var byRestraint = new Dictionary<string, List<uint>>();
        var restraintOrder = new List<string>();
        foreach (var r in model.GetTable("JOINT RESTRAINT ASSIGNMENTS"))
        {
            string j;
            if (!r.TryGetValue("Joint", out j)) continue;
            var fixedDofs = new List<string>();
            foreach (string k in DispKeys)
            {
                string v;
                if (r.TryGetValue(k, out v) && v.StartsWith("y", StringComparison.OrdinalIgnoreCase)) fixedDofs.Add(k);
            }
            if (fixedDofs.Count == 0) continue;
            Add(byRestraint, restraintOrder, string.Join(" ", fixedDofs), (uint)Int(j));
        }
        foreach (string p in restraintOrder)
            sc.AddNodeGroup("Restrained " + p, null, byRestraint[p], new[] { "sap", "restraint" }, StaadName("RESTR_" + p.Replace(' ', '_')));

        // joint local axes
        var axesNodes = new List<uint>();
        foreach (var r in model.GetTable("JOINT LOCAL AXES ASSIGNMENTS 1 - TYPICAL"))
        {
            string j;
            if (r.TryGetValue("Joint", out j)) axesNodes.Add((uint)Int(j));
        }
        if (axesNodes.Count > 0)
            sc.AddNodeGroup("Local axes assigned", null, axesNodes, new[] { "sap", "localAxes" }, "LOCAL_AXES");

        // SAP groups (GROUPS 2 - ASSIGNMENTS: GroupName, ObjectType, ObjectLabel)
        var grpAreas = new Dictionary<string, List<uint>>(StringComparer.OrdinalIgnoreCase);
        var grpJoints = new Dictionary<string, List<uint>>(StringComparer.OrdinalIgnoreCase);
        var grpFrames = new Dictionary<string, List<uint>>(StringComparer.OrdinalIgnoreCase);
        var grpOrder = new List<string>();
        foreach (var r in model.GetTable("GROUPS 2 - ASSIGNMENTS"))
        {
            string g, ty, lab;
            if (!r.TryGetValue("GroupName", out g) || !r.TryGetValue("ObjectType", out ty) || !r.TryGetValue("ObjectLabel", out lab)) continue;
            if (string.Equals(ty, "Area", StringComparison.OrdinalIgnoreCase)) Add(grpAreas, grpOrder, g, (uint)Int(lab));
            else if (string.Equals(ty, "Joint", StringComparison.OrdinalIgnoreCase)) Add(grpJoints, grpOrder, g, (uint)Int(lab));
            else if (string.Equals(ty, "Frame", StringComparison.OrdinalIgnoreCase) && beams.ContainsKey(Int(lab))) Add(grpFrames, grpOrder, g, (uint)Int(lab));
        }
        foreach (string g in grpOrder)
        {
            if (string.Equals(g, "ALL", StringComparison.OrdinalIgnoreCase)) continue;
            FeaturesSidecar.Group grp = null;
            List<uint> ids;
            if (grpAreas.TryGetValue(g, out ids)) grp = sc.AddGroup(g, null, "shells", ids, new[] { "sap", "group" }, StaadName(g));
            if (grpFrames.TryGetValue(g, out ids))
            {
                if (grp == null) grp = sc.AddGroup(g, null, "beams", ids, new[] { "sap", "group" }, StaadName(g));
                else grp.Members.Add(new FeaturesSidecar.Member { Domain = "beams", Ids = ids });
            }
            if (grpJoints.TryGetValue(g, out ids))
            {
                if (grp == null) sc.AddNodeGroup(g, null, ids, new[] { "sap", "group" }, StaadName(g));
                else grp.Members.Add(new FeaturesSidecar.Member { Domain = "nodes", NodeIds = ids });
            }
        }

        res.Groups = sc.Groups.Count;
        string scPath = outBase + ".features.json";
        File.WriteAllText(scPath, sc.ToJson(), new UTF8Encoding(false));
        res.SidecarPath = scPath;
        return res;
    }

    // ---- helpers ----------------------------------------------------------

    static void ParseUnits(Sap2kParser model, ref string force, ref string length)
    {
        var rows = model.GetTable("PROGRAM CONTROL");
        if (rows.Count == 0) return;
        string cu;
        if (!rows[0].TryGetValue("CurrUnits", out cu)) return;
        string[] parts = cu.Split(',');
        if (parts.Length >= 2)
        {
            force = parts[0].Trim().ToLowerInvariant();
            length = parts[1].Trim().ToLowerInvariant();
        }
    }

    static void CheckIntegerLabels(Sap2kParser model, SapExportResult res)
    {
        int bad = 0; string first = null;
        foreach (var r in model.GetTable("JOINT COORDINATES"))
        {
            string s; int v;
            if (r.TryGetValue("Joint", out s) && !int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) { bad++; if (first == null) first = "Joint " + s; }
        }
        foreach (var r in model.GetTable("CONNECTIVITY - AREA"))
        {
            string s; int v;
            if (r.TryGetValue("Area", out s) && !int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) { bad++; if (first == null) first = "Area " + s; }
        }
        if (bad > 0)
            throw new Exception(string.Format("SapToPluto: {0} non-integer SAP labels (first: {1}). Renumber joints/areas in SAP (Edit > Change Labels) before exporting.", bad, first));
    }

    static string CaseOf(Dictionary<string, string> r)
    {
        string c;
        if (r.TryGetValue("OutputCase", out c) || r.TryGetValue("Case", out c)) return c;
        return "UNNAMED";
    }

    static int LcId(string name, Dictionary<string, int> byName, Dictionary<int, string> names)
    {
        int id;
        if (byName.TryGetValue(name, out id)) return id;
        id = byName.Count + 1;
        byName[name] = id;
        names[id] = name;
        return id;
    }

    static int Int(string s)
    {
        int v;
        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return v;
        double d;
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return (int)d;
        throw new Exception("SapToPluto: non-integer label '" + s + "'.");
    }

    static double Num(Dictionary<string, string> r, string key, double dflt)
    {
        string s; double v;
        if (r.TryGetValue(key, out s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return v;
        return dflt;
    }

    static void Add(Dictionary<string, List<uint>> map, List<string> order, string key, uint id)
    {
        List<uint> ids;
        if (!map.TryGetValue(key, out ids)) { ids = new List<uint>(); map[key] = ids; order.Add(key); }
        ids.Add(id);
    }

    // SAP frame section (Shape + t3 t2 tf tw, file length units) -> writer
    // SectionDef. t3 = depth (along local 2), t2 = width (along local 3).
    // known = false when the shape is not mapped and a placeholder is returned.
    static RawViewerWriter.SectionDef FrameSection(string name, Dictionary<string, string> p, out bool known)
    {
        known = true;
        string shape = "";
        if (p != null) { string s; if (p.TryGetValue("Shape", out s)) shape = s; }
        shape = shape.Trim().ToLowerInvariant();
        float t3 = p == null ? 0f : (float)Num(p, "t3", 0), t2 = p == null ? 0f : (float)Num(p, "t2", 0);
        float tf = p == null ? 0f : (float)Num(p, "tf", 0), tw = p == null ? 0f : (float)Num(p, "tw", 0);
        bool dims = t3 > 0 && t2 > 0;
        if (shape == "rectangular" && dims) return RawViewerWriter.SectionDef.Rect(name, t2, t3);
        if (shape == "pipe" && t3 > 0 && tw > 0) return RawViewerWriter.SectionDef.Pipe(name, t3, tw);
        if ((shape == "box/tube" || shape == "box" || shape == "tube") && dims && tf > 0) return RawViewerWriter.SectionDef.Box(name, t2, t3, tf);
        if ((shape == "i/wide flange" || shape == "i" || shape == "wide flange") && dims && tf > 0 && tw > 0)
            return RawViewerWriter.SectionDef.IShape(name, t3, t2, tf, t2, tf, tw);
        if (shape == "angle" && dims && tf > 0) return RawViewerWriter.SectionDef.Angle(name, t2, t3, tf);
        if (shape == "channel" && dims && tf > 0 && tw > 0) return RawViewerWriter.SectionDef.Channel(name, t3, t2, tf, tw);
        if (shape == "tee" && dims && tf > 0 && tw > 0) return RawViewerWriter.SectionDef.Tee(name, t3, t2, tf, tw);
        if (shape == "double angle" && dims && tf > 0 && tw > 0) return RawViewerWriter.SectionDef.Tee(name, t3, t2, tf, 2f * tw);
        known = false;
        float d = dims ? Math.Max(t3, t2) : 0.5f;       // something visible, flagged in the summary
        return RawViewerWriter.SectionDef.Rect(name, d, d);
    }

    // "<NAME>: y,z y,z ..." per line; blank lines and lines without ':' skipped.
    static Dictionary<string, float[][]> ReadOutlines(string path)
    {
        var map = new Dictionary<string, float[][]>(StringComparer.OrdinalIgnoreCase);
        if (path == null || !File.Exists(path)) return map;
        foreach (string raw in File.ReadAllLines(path))
        {
            int colon = raw.IndexOf(':');
            if (colon <= 0) continue;
            string name = raw.Substring(0, colon).Trim();
            string[] pairs = raw.Substring(colon + 1).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var pts = new List<float[]>();
            foreach (string pr in pairs)
            {
                string[] yz = pr.Split(',');
                if (yz.Length != 2) continue;
                pts.Add(new[] { float.Parse(yz[0], CultureInfo.InvariantCulture), float.Parse(yz[1], CultureInfo.InvariantCulture) });
            }
            if (pts.Count >= 3) map[name] = pts.ToArray();
        }
        return map;
    }

    // Viewer local y for a SAP frame with default axes. SAP local 2 (the depth
    // direction) is the projection of +Z perpendicular to the member, or +X for
    // a vertical member. The viewer puts the depth along z = x cross y, so
    // y = local2 cross local1 (= minus SAP local 3).
    static double[] DefaultLocalY(Node a, Node b)
    {
        double dx = b.xyz.X - a.xyz.X, dy = b.xyz.Y - a.xyz.Y, dz = b.xyz.Z - a.xyz.Z;
        double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (len < 1e-12) return new double[] { 0, 1, 0 };
        double tx = dx / len, ty = dy / len, tz = dz / len;
        double ux, uy, uz;                                           // desired local 2
        if (Math.Abs(tz) > 1.0 - 1e-6) { ux = 1; uy = 0; uz = 0; }
        else { ux = -tz * tx; uy = -tz * ty; uz = 1.0 - tz * tz; }
        double yx = uy * tz - uz * ty, yy = uz * tx - ux * tz, yz = ux * ty - uy * tx;   // u cross t
        double n = Math.Sqrt(yx * yx + yy * yy + yz * yz);
        return new double[] { yx / n, yy / n, yz / n };
    }

    // Stress = force/length / thickness, in file units force/length^2; report ksi
    // when the file is Kip with ft or in (144 ksf = 1 ksi), else leave the file units.
    static void StressUnits(string forceUnit, string lengthUnit, out double scale, out string unit)
    {
        string f = (forceUnit ?? "").Trim().ToLowerInvariant(), l = (lengthUnit ?? "").Trim().ToLowerInvariant();
        if (f == "kip" && l == "ft") { scale = 1.0 / 144.0; unit = "ksi"; return; }
        if (f == "kip" && l == "in") { scale = 1.0; unit = "ksi"; return; }
        scale = 1.0; unit = forceUnit + "/" + lengthUnit + "^2";
    }

    // "0.500 in" for a thickness in file units when those are ft or in, else "0.0417 ft".
    static string ThicknessText(double t, string lengthUnit)
    {
        string l = (lengthUnit ?? "").Trim().ToLowerInvariant();
        if (l == "ft") return (t * 12.0).ToString("0.000", CultureInfo.InvariantCulture) + " in";
        if (l == "in") return t.ToString("0.000", CultureInfo.InvariantCulture) + " in";
        return t.ToString("0.####", CultureInfo.InvariantCulture) + " " + lengthUnit;
    }

    static string StaadName(string s)
    {
        var sb = new StringBuilder("_");
        foreach (char c in s.ToUpperInvariant()) sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        return sb.ToString();
    }

    // Joint local frames: SAP builds local axes from global by rotating A about
    // Z(3), then B about the new Y(2), then C about the new X(1). The matrix
    // whose COLUMNS are the local axes in global coordinates is Rz(A)Ry(B)Rx(C);
    // global = M . local. Stored row-major [9].
    static Dictionary<int, double[]> JointLocalFrames(Sap2kParser model)
    {
        var frames = new Dictionary<int, double[]>();
        foreach (var r in model.GetTable("JOINT LOCAL AXES ASSIGNMENTS 1 - TYPICAL"))
        {
            string j;
            if (!r.TryGetValue("Joint", out j)) continue;
            double a = Num(r, "AngleA", 0) * Math.PI / 180.0;
            double b = Num(r, "AngleB", 0) * Math.PI / 180.0;
            double c = Num(r, "AngleC", 0) * Math.PI / 180.0;
            double ca = Math.Cos(a), sa = Math.Sin(a), cb = Math.Cos(b), sb = Math.Sin(b), cc = Math.Cos(c), scc = Math.Sin(c);
            frames[Int(j)] = new double[] {
                ca * cb, ca * sb * scc - sa * cc, ca * sb * cc + sa * scc,
                sa * cb, sa * sb * scc + ca * cc, sa * sb * cc - ca * scc,
                -sb,     cb * scc,                cb * cc };
        }
        return frames;
    }

    // Rotate translations (0..2) and rotations (3..5) out of the local frame.
    static double[] Rotate(double[] m, double[] v)
    {
        double[] o = (double[])v.Clone();
        for (int b = 0; b < 6; b += 3)
        {
            if (double.IsNaN(v[b]) || double.IsNaN(v[b + 1]) || double.IsNaN(v[b + 2])) continue;
            for (int i = 0; i < 3; i++)
                o[b + i] = m[i * 3 + 0] * v[b] + m[i * 3 + 1] * v[b + 1] + m[i * 3 + 2] * v[b + 2];
        }
        return o;
    }
}
