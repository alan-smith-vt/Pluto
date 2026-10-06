using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

// DuctsToPluto  --  a Navisworks "Pluto Ducts" run folder (scripts/navis/DuctsButton.cs) -> Pluto v4
// geometry-only binary + features sidecar, to load as an overlay beside the SP3D plant file.
// C# 5 / Add-Type (PowerShell 5.1) compatible; compiled with the arms by scripts/lib/Config.ps1.
//
// Inputs (feet, plant frame: Navisworks X/Y/Z = plant E/N/EL):
//   ducts.csv        one row per Ducts / Duct Fittings / Duct Accessories element: IfcGUID, Category,
//                    SystemName, Width_in, Height_in, Diameter_in, WallThk_in, bbox MinX..MaxZ, fitted
//                    ends X1..Z2 (ducts)
//   cl_segments.csv  Revit centreline segments: Row (1-based ducts.csv data row), Node1, Node2
//   cl_nodes.csv     Node, X, Y, Z, Degree (ends merged by the button)
// Mapping:
//   duct with a centreline      -> its centreline segment(s), box (W x H, wall) or pipe (D, wall) section
//   duct without one            -> fitted ends X1..Z2
//   fitting centreline segments -> beams with the fitting's size (unsized: small pipe placeholder);
//                                  segments touching a node of degree >= FanDegree are symbol linework
//                                  (a fitting's lines fanning from one point), dropped and counted
//   fitting without centreline, -> "bbox block": a beam along the bbox's longest axis with a RECT of
//   accessory                      the other two extents (shows where they are, not their shape)
//   cl node of degree 1         -> node group "Loose ends" (red)
// Section orientation: local y horizontal, so the section's z (= x cross y, the depth / height) is as
// vertical as the member allows; vertical members: y = global X.
// Groups (one per element, the viewer resolves one group per element): Ducts, Fittings (centreline),
// Fittings (bbox only), Accessories (bbox); tags carry the category. Labels: IfcGUID | system | size.
// Written in lengthUnit ("in" to match the plant export), recentred on the bbox centre (whole feet),
// worldOffset in the sidecar.
public class DuctsToPluto
{
    public class Result
    {
        public string BinPath, SidecarPath, GeometryHash;
        public int Nodes, Beams, DuctBeams, DuctFallback, FittingBeams, FittingBlocks, AccessoryBlocks, FanDropped, LooseEnds, Unsized, Skipped;
        public int FabBeams, FabBlocks, HangerBlocks, FabFitRejected;
        public int FabFromBbox, FabPhantom, FabBridged, FittingsBridged;
        public string Summary()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "nodes={0} beams={1}: ducts {2} (fitted-end fallback {3}), fitting centreline {4}, fitting blocks {5}, accessory blocks {6}\n" +
                "fabrication: beams {13} (straights from bbox + size {17}), blocks {14} (fit rejected {16}); hanger blocks {15}\n" +
                "fabrication straights not matching their stated size (still drawn; <out>.fab-mismatch.txt) {18}; fittings bridged {19} (fabrication {20})\n" +
                "symbol-fan segments dropped {7}, loose ends {8}, unsized {9}, rows skipped (no geometry) {10}\n{11}\n{12}",
                Nodes, Beams, DuctBeams, DuctFallback, FittingBeams, FittingBlocks, AccessoryBlocks, FanDropped, LooseEnds, Unsized, Skipped,
                BinPath, SidecarPath, FabBeams, FabBlocks, HangerBlocks, FabFitRejected, FabFromBbox, FabPhantom, FittingsBridged, FabBridged);
        }
    }

    const int FanDegree = 5;
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    const string Fab = "MEP Fabrication Ductwork", FabHangers = "MEP Fabrication Hangers";

    class Row
    {
        public string Guid, NavisId, Cat, System, SizeText, Name, Shape;
        public string Hidden = "";               // "self" / "ancestor" / "" (runs from 2026-10-06 on)
        public double HiddenGeom;                 // own geometry items hidden (NaN on older runs)
        public double W, H, D, T;                 // inches; NaN = unknown
        public double FitA, FitB;                 // fitted cross-section, inches (fallback when no size)
        public double FitLen;                     // fitted length, feet
        public double NameW = double.NaN, NameH = double.NaN, NameD = double.NaN;   // size read from the Name (fabrication parts)
        public double[] Min, Max, E1, E2;         // feet; null = absent
    }

    public static Result Export(string runDir, string outBase, string modelId, string lengthUnit)
    {
        double scale = LengthScale(lengthUnit);   // feet -> file units
        var rows = ReadRows(Path.Combine(runDir, "ducts.csv"));
        var nodeXyz = new Dictionary<int, double[]>(); var nodeDeg = new Dictionary<int, int>();
        foreach (Dictionary<string, string> r in ReadCsv(Path.Combine(runDir, "cl_nodes.csv")))
        {
            int id = int.Parse(r["Node"], Inv);
            nodeXyz[id] = new[] { Num(r, "X"), Num(r, "Y"), Num(r, "Z") };
            nodeDeg[id] = int.Parse(r["Degree"], Inv);
        }
        var segsByRow = new Dictionary<int, List<int[]>>();
        foreach (Dictionary<string, string> r in ReadCsv(Path.Combine(runDir, "cl_segments.csv")))
        {
            int row = int.Parse(r["Row"], Inv);
            List<int[]> l; if (!segsByRow.TryGetValue(row, out l)) { l = new List<int[]>(); segsByRow[row] = l; }
            l.Add(new[] { int.Parse(r["Node1"], Inv), int.Parse(r["Node2"], Inv) });
        }

        // recentre on the bbox of everything (whole feet), as the steel / pipe bridges do
        double[] lo = { double.MaxValue, double.MaxValue, double.MaxValue }, hi = { double.MinValue, double.MinValue, double.MinValue };
        foreach (Row r in rows) if (r.Min != null) for (int k = 0; k < 3; k++) { lo[k] = Math.Min(lo[k], r.Min[k]); hi[k] = Math.Max(hi[k], r.Max[k]); }
        foreach (double[] p in nodeXyz.Values) for (int k = 0; k < 3; k++) { lo[k] = Math.Min(lo[k], p[k]); hi[k] = Math.Max(hi[k], p[k]); }
        if (lo[0] == double.MaxValue) throw new Exception("DuctsToPluto: nothing with coordinates in " + runDir);
        double[] off = { Math.Round((lo[0] + hi[0]) / 2), Math.Round((lo[1] + hi[1]) / 2), Math.Round((lo[2] + hi[2]) / 2) };

        var res = new Result();
        var nodes = new Dictionary<int, Node>();
        var clToFile = new Dictionary<int, int>();          // cl node id -> file node id
        var keyToFile = new Dictionary<string, int>();      // fallback / block ends, deduped at 1e-4 ft
        int nextNode = 1, nextBeam = 1;
        var sections = new List<RawViewerWriter.SectionDef>();
        var sectionIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var beams = new Dictionary<int, RawViewerWriter.BeamMember>();
        var labels = new Dictionary<int, string>();
        var gDucts = new List<uint>(); var gFitCl = new List<uint>(); var gFitBox = new List<uint>(); var gAcc = new List<uint>(); var gUnsized = new List<uint>();
        var gFab = new List<uint>(); var gFabBox = new List<uint>(); var gHang = new List<uint>(); var gFabRej = new List<uint>();
        var gFitBridge = new List<uint>(); var gFabBridge = new List<uint>();

        Func<int, int> clNode = delegate(int id)
        {
            int f;
            if (clToFile.TryGetValue(id, out f)) return f;
            f = MakeNode(nodes, ref nextNode, nodeXyz[id], off, scale);
            clToFile[id] = f;
            return f;
        };
        Func<double[], int> ptNode = delegate(double[] p)
        {
            string key = string.Format(Inv, "{0:F4}|{1:F4}|{2:F4}", p[0], p[1], p[2]);
            int f;
            if (keyToFile.TryGetValue(key, out f)) return f;
            f = MakeNode(nodes, ref nextNode, p, off, scale);
            keyToFile[key] = f;
            return f;
        };

        var network = new HashSet<int>();          // beams of the duct network (not blocks): loose ends and bridging use them
        var pending = new List<int>();             // fittings without a centreline: bridged after the loop
        var phantoms = new List<string>();
        Func<Row, string> labelOf = delegate(Row r)
        {
            return r.Guid + (r.NavisId != "" ? " #" + r.NavisId.Substring(0, Math.Min(8, r.NavisId.Length)) : "")
                + " | " + r.System + " | " + (r.SizeText != "" ? r.SizeText : r.Name);   // IfcGUID is not unique: short NavisId too
        };
        // bbox block: a beam along the bbox's longest axis with a RECT of the other two extents; false when no bbox
        Func<Row, List<uint>, bool> addBlock = delegate(Row r, List<uint> grp)
        {
            if (r.Min == null) return false;
            double[] ext = { r.Max[0] - r.Min[0], r.Max[1] - r.Min[1], r.Max[2] - r.Min[2] };
            int ax = ext[0] >= ext[1] && ext[0] >= ext[2] ? 0 : ext[1] >= ext[2] ? 1 : 2;
            if (ext[ax] < 1e-6) return false;
            double[] c = { (r.Min[0] + r.Max[0]) / 2, (r.Min[1] + r.Max[1]) / 2, (r.Min[2] + r.Max[2]) / 2 };
            double[] pa = (double[])c.Clone(), pb = (double[])c.Clone();
            pa[ax] = r.Min[ax]; pb[ax] = r.Max[ax];
            // section: local y horizontal -> for a horizontal block y spans the other horizontal extent, z the height
            double bY, hZ;
            if (ax == 2) { bY = ext[0]; hZ = ext[1]; }                  // vertical: y = X, z = Z x X = Y
            else { bY = ext[ax == 0 ? 1 : 0]; hZ = ext[2]; }            // horizontal: y = the other horizontal, z = up
            string bname = string.Format(Inv, "BLOCK {0:0.##}x{1:0.##}", bY * 12, hZ * 12);
            int bs;
            if (!sectionIndex.TryGetValue(bname, out bs))
            {
                bs = sections.Count; sectionIndex[bname] = bs;
                sections.Add(RawViewerWriter.SectionDef.Rect(bname, (float)Math.Max(bY * scale, 1e-3), (float)Math.Max(hZ * scale, 1e-3)));
            }
            int bid = nextBeam++;
            beams[bid] = Beam(bid, ptNode(pa), ptNode(pb), bs, nodes);
            labels[bid] = labelOf(r);
            grp.Add((uint)bid);
            return true;
        };

        for (int i = 0; i < rows.Count; i++)
        {
            Row r = rows[i]; int rowNo = i + 1;
            List<int[]> segs; segsByRow.TryGetValue(rowNo, out segs);
            string label = labelOf(r);
            bool isDuct = r.Cat == "Ducts", isFit = r.Cat == "Duct Fittings";
            bool fab = string.Equals(r.Cat, Fab, StringComparison.OrdinalIgnoreCase);
            // a straight: named "Straight…", or named by its size (" 74 in x 28 in") with a fit that matches it
            // (an elbow / transition does not match its own size, and stays a block)
            bool nameSized = !double.IsNaN(r.NameW) || !double.IsNaN(r.NameD);
            bool fabStraight = fab && (r.Name.TrimStart().StartsWith("Straight", StringComparison.OrdinalIgnoreCase) || (nameSized && FitTrusted(r)));
            bool linear = isDuct || fabStraight;     // drawn along fitted ends when there is no centreline
            if (linear || ((isFit || fab) && segs != null))
            {
                bool sized;
                int sec = SectionFor(r, scale, sections, sectionIndex, out sized);
                var drawn = new List<int[]>();
                if (segs != null)
                {
                    // symbol fan = THIS element's own segment ends piling up at one node (its degree there,
                    // not the node's total: a duct end can sit on another element's fan centre and must stay)
                    var own = new Dictionary<int, int>();
                    foreach (int[] s in segs) for (int e = 0; e < 2; e++) { int oc; own.TryGetValue(s[e], out oc); own[s[e]] = oc + 1; }
                    foreach (int[] s in segs)
                    {
                        if (own[s[0]] >= FanDegree || own[s[1]] >= FanDegree) { res.FanDropped++; continue; }
                        drawn.Add(new[] { clNode(s[0]), clNode(s[1]) });
                    }
                }
                if (drawn.Count == 0 && fabStraight)
                {
                    // fabrication straight: the run direction from the bbox and the stated size (two extents match
                    // W x H or D x D, allowing flanges, the third is the run); else the fit, if its section matches.
                    // When neither matches, the element is LISTED (<out>.fab-mismatch.txt) and drawn as before
                    // (fit if plausible, else a flagged block) -- never dropped: a skip rule with a 2 in tolerance
                    // removed almost every good straight (flanges make real parts bigger than nominal, 2026-10-05).
                    double sa, sb; double[] b1, b2;
                    bool stated = StatedSize(r, out sa, out sb);
                    if (stated && BboxAxis(r, sa, sb, out b1, out b2)) { drawn.Add(new[] { ptNode(b1), ptNode(b2) }); res.FabFromBbox++; }
                    else if (stated && r.E1 != null && FitMatches(r, sa, sb)) { drawn.Add(new[] { ptNode(r.E1), ptNode(r.E2) }); res.DuctFallback++; }
                    else
                    {
                        if (stated)
                        {
                            res.FabPhantom++;
                            string bx = r.Min == null ? "-" : string.Format(Inv, "{0:0.#}x{1:0.#}x{2:0.#}", (r.Max[0] - r.Min[0]) * 12, (r.Max[1] - r.Min[1]) * 12, (r.Max[2] - r.Min[2]) * 12);
                            phantoms.Add(label + string.Format(Inv, " | stated {0:0.##}x{1:0.##} | bbox in {2} | fit {3:0.#}x{4:0.#} len {5:0.##} ft",
                                sa, sb, bx, r.FitA, r.FitB, r.FitLen));
                        }
                        if (!FitTrusted(r))
                        {
                            res.FabFitRejected++;
                            if (addBlock(r, gFabRej)) res.FabBlocks++; else res.Skipped++;
                            continue;
                        }
                    }
                }
                if (drawn.Count == 0 && linear && r.E1 != null) { drawn.Add(new[] { ptNode(r.E1), ptNode(r.E2) }); res.DuctFallback++; }
                if (drawn.Count == 0 && linear) { res.Skipped++; continue; }
                foreach (int[] d in drawn)
                {
                    if (d[0] == d[1]) continue;
                    int id = nextBeam++;
                    beams[id] = Beam(id, d[0], d[1], sec, nodes);
                    labels[id] = label;
                    network.Add(id);
                    if (!sized) gUnsized.Add((uint)id);
                    else if (isDuct) gDucts.Add((uint)id); else if (fab) gFab.Add((uint)id); else gFitCl.Add((uint)id);
                    if (isDuct) res.DuctBeams++; else if (fab) res.FabBeams++; else res.FittingBeams++;
                }
                if (drawn.Count > 0) continue;
                // a fitting whose lines were all symbol linework: bridged below like one without lines
            }
            if (isFit || fab) { pending.Add(i); continue; }   // fittings without a centreline: bridged after the loop
            // bbox block: accessories, hangers
            bool hanger = string.Equals(r.Cat, FabHangers, StringComparison.OrdinalIgnoreCase);
            if (!addBlock(r, hanger ? gHang : gAcc)) { res.Skipped++; continue; }
            if (hanger) res.HangerBlocks++; else res.AccessoryBlocks++;
        }

        // ---- bridging: a fitting without a centreline joins the loose network ends inside its bbox (+0.25 ft)
        // at the point where their axes meet (elbow corner, tee centre; parallel ends: their midpoint) ----
        {
            var deg = new Dictionary<int, int>(); var dirOf = new Dictionary<int, double[]>(); var secOf = new Dictionary<int, int>();
            foreach (int id in network)
            {
                RawViewerWriter.BeamMember m = beams[id];
                int dA; deg.TryGetValue(m.NodeA, out dA); deg[m.NodeA] = dA + 1;
                int dB; deg.TryGetValue(m.NodeB, out dB); deg[m.NodeB] = dB + 1;
            }
            var looseEnds = new List<int>();
            foreach (int id in network)
            {
                RawViewerWriter.BeamMember m = beams[id];
                foreach (int e in new[] { m.NodeA, m.NodeB })
                {
                    if (deg[e] != 1) continue;
                    int o = e == m.NodeA ? m.NodeB : m.NodeA;
                    double[] d = { nodes[e].xyz.X - nodes[o].xyz.X, nodes[e].xyz.Y - nodes[o].xyz.Y, nodes[e].xyz.Z - nodes[o].xyz.Z };
                    double l = Math.Sqrt(d[0] * d[0] + d[1] * d[1] + d[2] * d[2]);
                    if (l < 1e-9) continue;
                    dirOf[e] = new[] { d[0] / l, d[1] / l, d[2] / l };   // outward: from the duct into the fitting
                    secOf[e] = m.SectionIndex;
                    looseEnds.Add(e);
                }
            }
            var claimed = new HashSet<int>();
            double tol = 0.25 * scale;
            foreach (int i in pending)
            {
                Row r = rows[i];
                bool fab = string.Equals(r.Cat, Fab, StringComparison.OrdinalIgnoreCase);
                var ends = new List<int>();
                if (r.Min != null)
                {
                    double[] lo2 = { (r.Min[0] - off[0]) * scale - tol, (r.Min[1] - off[1]) * scale - tol, (r.Min[2] - off[2]) * scale - tol };
                    double[] hi2 = { (r.Max[0] - off[0]) * scale + tol, (r.Max[1] - off[1]) * scale + tol, (r.Max[2] - off[2]) * scale + tol };
                    foreach (int e in looseEnds)
                    {
                        if (claimed.Contains(e)) continue;
                        Node n = nodes[e];
                        if (n.xyz.X >= lo2[0] && n.xyz.X <= hi2[0] && n.xyz.Y >= lo2[1] && n.xyz.Y <= hi2[1] && n.xyz.Z >= lo2[2] && n.xyz.Z <= hi2[2]) ends.Add(e);
                    }
                }
                if (ends.Count < 2)
                {
                    if (addBlock(r, fab ? gFabBox : gFitBox)) { if (fab) res.FabBlocks++; else res.FittingBlocks++; } else res.Skipped++;
                    continue;
                }
                double[] wp = WorkPoint(ends, dirOf, nodes);
                if (r.Min != null)   // a meeting point far outside the fitting (near-parallel axes): its bbox centre instead
                {
                    double[] cc = { ((r.Min[0] + r.Max[0]) / 2 - off[0]) * scale, ((r.Min[1] + r.Max[1]) / 2 - off[1]) * scale, ((r.Min[2] + r.Max[2]) / 2 - off[2]) * scale };
                    double reach = 0;
                    for (int k = 0; k < 3; k++) reach = Math.Max(reach, (r.Max[k] - r.Min[k]) * scale);
                    double dx = wp[0] - cc[0], dy = wp[1] - cc[1], dz = wp[2] - cc[2];
                    if (Math.Sqrt(dx * dx + dy * dy + dz * dz) > reach) wp = cc;
                }
                int wn = nextNode++;
                var nn = new Node(); nn.id = wn; nn.xyz.X = (float)wp[0]; nn.xyz.Y = (float)wp[1]; nn.xyz.Z = (float)wp[2];
                nodes[wn] = nn;
                bool sized;
                int sec = SectionFor(r, scale, sections, sectionIndex, out sized);
                foreach (int e in ends)
                {
                    claimed.Add(e);
                    int id = nextBeam++;
                    beams[id] = Beam(id, e, wn, sized ? sec : secOf[e], nodes);   // unsized fitting: its duct's section
                    labels[id] = labelOf(r);
                    network.Add(id);
                    (fab ? gFabBridge : gFitBridge).Add((uint)id);
                }
                if (fab) res.FabBridged++; else res.FittingsBridged++;
            }
        }
        if (beams.Count == 0) throw new Exception("DuctsToPluto: no beams built from " + runDir);

        var comps = new List<RawViewerWriter.Component>();
        comps.Add(new RawViewerWriter.Component("Axial N", "force", "kip"));   // layout only; no planes written
        var units = new Dictionary<string, string>(); units["length"] = lengthUnit;
        string binPath = outBase + ".bin";
        var w = new RawViewerWriter(binPath, nodes, null, null, null, beams, sections, comps, modelId, units);
        w.SetBeamLabels(labels);
        w.Write(false);

        var sc = new FeaturesSidecar();
        sc.ModelId = modelId; sc.GeometryHash = w.GeometryHash;
        sc.Units["length"] = lengthUnit;
        sc.Units["worldOffset"] = string.Format(Inv, "{0} {1} {2}", off[0] * scale, off[1] * scale, off[2] * scale);
        if (gDucts.Count > 0) sc.AddGroup("Ducts", "#7fb3ff", "beams", gDucts, new[] { "duct", "Ducts" }, "DUCTS");
        if (gFitCl.Count > 0) sc.AddGroup("Fittings (centreline)", "#3fc1a5", "beams", gFitCl, new[] { "duct", "Duct Fittings" }, "DUCT_FITTINGS");
        if (gFitBox.Count > 0) sc.AddGroup("Fittings (bbox only)", "#ff9f40", "beams", gFitBox, new[] { "duct", "Duct Fittings", "bbox" }, "DUCT_FITTINGS_BBOX");
        if (gAcc.Count > 0) sc.AddGroup("Accessories (bbox)", "#b0b0b8", "beams", gAcc, new[] { "duct", "Duct Accessories", "bbox" }, "DUCT_ACCESSORIES");
        if (gFab.Count > 0) sc.AddGroup("Fabrication ductwork", "#5a8fe0", "beams", gFab, new[] { "duct", Fab }, "DUCT_FAB");
        if (gFabBox.Count > 0) sc.AddGroup("Fabrication (bbox)", "#ffc04d", "beams", gFabBox, new[] { "duct", Fab, "bbox" }, "DUCT_FAB_BBOX");
        if (gFabRej.Count > 0) sc.AddGroup("Fabrication (fit rejected)", "#ff7a00", "beams", gFabRej, new[] { "duct", Fab, "bbox", "fitRejected" }, "DUCT_FAB_FIT_REJECTED");
        if (gFitBridge.Count > 0) sc.AddGroup("Fittings (bridged)", "#2fa88f", "beams", gFitBridge, new[] { "duct", "Duct Fittings", "bridged" }, "DUCT_FITTINGS_BRIDGED");
        if (gFabBridge.Count > 0) sc.AddGroup("Fabrication fittings (bridged)", "#4a7fd0", "beams", gFabBridge, new[] { "duct", Fab, "bridged" }, "DUCT_FAB_BRIDGED");
        if (gHang.Count > 0) sc.AddGroup("Fabrication hangers", "#e040c0", "beams", gHang, new[] { "duct", FabHangers, "bbox" }, "DUCT_FAB_HANGERS");
        if (gUnsized.Count > 0) sc.AddGroup("Unsized", "#ff3b3b", "beams", gUnsized, new[] { "duct", "unsized" }, "DUCT_UNSIZED");
        // loose ends: network nodes (ducts, centrelines, bridges; not blocks) with one member after bridging
        var loose = new List<uint>();
        {
            var deg = new Dictionary<int, int>();
            foreach (int id in network)
            {
                int a; deg.TryGetValue(beams[id].NodeA, out a); deg[beams[id].NodeA] = a + 1;
                int b; deg.TryGetValue(beams[id].NodeB, out b); deg[beams[id].NodeB] = b + 1;
            }
            foreach (KeyValuePair<int, int> kv in deg) if (kv.Value == 1) loose.Add((uint)kv.Key);
        }
        File.WriteAllLines(outBase + ".fab-mismatch.txt", phantoms.ToArray());
        if (loose.Count > 0) sc.AddNodeGroup("Loose ends", "#ff3b3b", loose, new[] { "duct", "looseEnd" }, "DUCT_LOOSE_ENDS");
        string scPath = outBase + ".features.json";
        File.WriteAllText(scPath, sc.ToJson(), new UTF8Encoding(false));

        res.BinPath = binPath; res.SidecarPath = scPath; res.GeometryHash = w.GeometryHash;
        res.Nodes = nodes.Count; res.Beams = beams.Count; res.LooseEnds = loose.Count; res.Unsized = gUnsized.Count;
        return res;
    }

    // Side pass: the raw triangles of ducts_tri.bin (Ducts and MEP Fabrication Ductwork, world coordinates, feet)
    // as a shell mesh overlay, to see each element's actual geometry independent of any fit / size logic.
    // Record: int32 row (1-based ducts.csv data row), int32 nTri, nTri * 9 float32. One shell group per
    // category; every triangle labelled like the beams (IfcGUID #NavisId | system | size).
    public static Result ExportMesh(string runDir, string outBase, string modelId, string lengthUnit)
    {
        double scale = LengthScale(lengthUnit);
        var rows = ReadRows(Path.Combine(runDir, "ducts.csv"));
        string triPath = Path.Combine(runDir, "ducts_tri.bin");
        if (!File.Exists(triPath)) throw new Exception("DuctsToPluto: missing " + triPath);
        var recs = new List<KeyValuePair<int, float[]>>();
        double[] lo = { double.MaxValue, double.MaxValue, double.MaxValue }, hi = { double.MinValue, double.MinValue, double.MinValue };
        using (var br = new BinaryReader(File.OpenRead(triPath)))
        {
            while (br.BaseStream.Position + 8 <= br.BaseStream.Length)
            {
                int row = br.ReadInt32(), n = br.ReadInt32();
                var f = new float[n * 9];
                for (int k = 0; k < f.Length; k++) { f[k] = br.ReadSingle(); lo[k % 3] = Math.Min(lo[k % 3], f[k]); hi[k % 3] = Math.Max(hi[k % 3], f[k]); }
                recs.Add(new KeyValuePair<int, float[]>(row, f));
            }
        }
        if (recs.Count == 0) throw new Exception("DuctsToPluto: no triangles in " + triPath);
        double[] off = { Math.Round((lo[0] + hi[0]) / 2), Math.Round((lo[1] + hi[1]) / 2), Math.Round((lo[2] + hi[2]) / 2) };
        var nodes = new Dictionary<int, Node>(); var elems = new Dictionary<int, Element>(); var labels = new Dictionary<int, string>();
        var keyToNode = new Dictionary<string, int>(); var byCat = new SortedDictionary<string, List<uint>>(StringComparer.OrdinalIgnoreCase);
        int nextNode = 1, nextElem = 1;
        foreach (KeyValuePair<int, float[]> rec in recs)
        {
            Row r = rec.Key >= 1 && rec.Key <= rows.Count ? rows[rec.Key - 1] : null;
            string label = r == null ? "row " + rec.Key : r.Guid + (r.NavisId != "" ? " #" + r.NavisId.Substring(0, Math.Min(8, r.NavisId.Length)) : "")
                + " | " + r.System + " | " + (r.SizeText != "" ? r.SizeText : r.Name);
            // hidden in Navisworks: own groups, so they can be toggled apart from what the model shows
            string hid = r == null ? "" : r.Hidden != "" ? "hidden" : r.HiddenGeom > 0 ? "partly hidden" : "";
            if (hid != "") label += " | " + hid.ToUpperInvariant() + (r.Hidden != "" ? " (" + r.Hidden + ")" : "");
            string cat = (r == null ? "?" : r.Cat) + (hid != "" ? ", " + hid : "");
            List<uint> g; if (!byCat.TryGetValue(cat, out g)) { g = new List<uint>(); byCat[cat] = g; }
            float[] f = rec.Value;
            for (int t = 0; t + 8 < f.Length; t += 9)
            {
                var el = new Element(); el.id = nextElem++; el.nNodes = 3; el.n = new Node[4];
                for (int c = 0; c < 3; c++)
                {
                    double[] p = { f[t + 3 * c], f[t + 3 * c + 1], f[t + 3 * c + 2] };
                    string key = string.Format(Inv, "{0:F4}|{1:F4}|{2:F4}", p[0], p[1], p[2]);
                    int nid;
                    if (!keyToNode.TryGetValue(key, out nid)) { nid = MakeNode(nodes, ref nextNode, p, off, scale); keyToNode[key] = nid; }
                    el.n[c] = nodes[nid];
                }
                elems[el.id] = el; labels[el.id] = label; g.Add((uint)el.id);
            }
        }
        var units = new Dictionary<string, string>(); units["length"] = lengthUnit;
        var comps = new List<RawViewerWriter.Component>();
        comps.Add(new RawViewerWriter.Component("Axial N", "force", "kip"));   // layout only; no planes written
        string binPath = outBase + ".bin";
        var w = new RawViewerWriter(binPath, nodes, elems, null, comps, null, null, null, modelId, units);
        w.SetShellLabels(labels);
        w.Write(false);
        var sc = new FeaturesSidecar();
        sc.ModelId = modelId; sc.GeometryHash = w.GeometryHash;
        sc.Units["length"] = lengthUnit;
        sc.Units["worldOffset"] = string.Format(Inv, "{0} {1} {2}", off[0] * scale, off[1] * scale, off[2] * scale);
        string[] palette = { "#7fb3ff", "#5a8fe0", "#3fc1a5", "#ffc04d" };
        int pi = 0;
        foreach (KeyValuePair<string, List<uint>> kv in byCat)
            sc.AddGroup(kv.Key + " (mesh)", palette[pi++ % palette.Length], "shells", kv.Value, new[] { "duct", "mesh", kv.Key }, null);
        string scPath = outBase + ".features.json";
        File.WriteAllText(scPath, sc.ToJson(), new UTF8Encoding(false));
        var res = new Result();
        res.BinPath = binPath; res.SidecarPath = scPath; res.GeometryHash = w.GeometryHash;
        res.Nodes = nodes.Count; res.Beams = 0; res.Skipped = rows.Count - recs.Count;
        return res;
    }

    // A "Pluto Box" run (scripts/navis/BoxButton.cs: every geometry item inside a box) -> shell overlay.
    // box_tri.bin: int32 row (1-based box_items.csv data row), int32 nTri, nTri * 9 float32 (world feet).
    // One shell group per element category (the nearest ancestor with an IfcGUID; "(no element)" when
    // none), hidden items in their own red groups; every triangle labelled with its item and where it hangs:
    // DisplayName [Class] | category: element name size | IfcGUID #id (n up) | item #id | path | HIDDEN.
    public static Result ExportBox(string runDir, string outBase, string modelId, string lengthUnit)
    {
        double scale = LengthScale(lengthUnit);
        var items = new List<Dictionary<string, string>>(ReadCsv(Path.Combine(runDir, "box_items.csv")));
        string triPath = Path.Combine(runDir, "box_tri.bin");
        if (!File.Exists(triPath)) throw new Exception("DuctsToPluto: missing " + triPath);
        var recs = new List<KeyValuePair<int, float[]>>();
        double[] lo = { double.MaxValue, double.MaxValue, double.MaxValue }, hi = { double.MinValue, double.MinValue, double.MinValue };
        using (var br = new BinaryReader(File.OpenRead(triPath)))
        {
            while (br.BaseStream.Position + 8 <= br.BaseStream.Length)
            {
                int row = br.ReadInt32(), n = br.ReadInt32();
                var f = new float[n * 9];
                for (int k = 0; k < f.Length; k++) { f[k] = br.ReadSingle(); lo[k % 3] = Math.Min(lo[k % 3], f[k]); hi[k % 3] = Math.Max(hi[k % 3], f[k]); }
                recs.Add(new KeyValuePair<int, float[]>(row, f));
            }
        }
        if (recs.Count == 0) throw new Exception("DuctsToPluto: no triangles in " + triPath + " (box empty or misplaced? see summary.txt)");
        double[] off = { Math.Round((lo[0] + hi[0]) / 2), Math.Round((lo[1] + hi[1]) / 2), Math.Round((lo[2] + hi[2]) / 2) };
        var nodes = new Dictionary<int, Node>(); var elems = new Dictionary<int, Element>(); var labels = new Dictionary<int, string>();
        var keyToNode = new Dictionary<string, int>(); var byCat = new SortedDictionary<string, List<uint>>(StringComparer.OrdinalIgnoreCase);
        int nextNode = 1, nextElem = 1;
        foreach (KeyValuePair<int, float[]> rec in recs)
        {
            Dictionary<string, string> it = rec.Key >= 1 && rec.Key <= items.Count ? items[rec.Key - 1] : null;
            string label, cat;
            if (it == null) { label = "row " + rec.Key; cat = "?"; }
            else
            {
                bool hidden = Get(it, "Hidden") == "1";
                string ac = Get(it, "AncCategory"), id = Get(it, "NavisId"), aid = Get(it, "AncNavisId");
                var sb = new StringBuilder();
                sb.Append(Get(it, "DisplayName")).Append(" [").Append(Get(it, "ClassDisplayName")).Append("]");
                if (Get(it, "AncIfcGUID") != "")
                    sb.Append(" | ").Append(ac).Append(": ").Append(Get(it, "AncName"))
                      .Append(Get(it, "AncSize") != "" ? " " + Get(it, "AncSize") : "")
                      .Append(" | ").Append(Get(it, "AncIfcGUID")).Append(aid != "" ? " #" + aid.Substring(0, Math.Min(8, aid.Length)) : "")
                      .Append(" (").Append(Get(it, "AncLevelsUp")).Append(" up)");
                if (id != "") sb.Append(" | item #").Append(id.Substring(0, Math.Min(8, id.Length)));
                sb.Append(" | ").Append(Get(it, "Path"));
                if (hidden) sb.Append(Get(it, "HiddenSelf") == "1" ? " | HIDDEN (self)" : " | HIDDEN (ancestor)");
                label = sb.ToString();
                cat = (ac != "" ? ac : "(no element)") + (hidden ? ", hidden" : "");
            }
            List<uint> g; if (!byCat.TryGetValue(cat, out g)) { g = new List<uint>(); byCat[cat] = g; }
            float[] f = rec.Value;
            for (int t = 0; t + 8 < f.Length; t += 9)
            {
                var el = new Element(); el.id = nextElem++; el.nNodes = 3; el.n = new Node[4];
                for (int c = 0; c < 3; c++)
                {
                    double[] p = { f[t + 3 * c], f[t + 3 * c + 1], f[t + 3 * c + 2] };
                    string key = string.Format(Inv, "{0:F4}|{1:F4}|{2:F4}", p[0], p[1], p[2]);
                    int nid;
                    if (!keyToNode.TryGetValue(key, out nid)) { nid = MakeNode(nodes, ref nextNode, p, off, scale); keyToNode[key] = nid; }
                    el.n[c] = nodes[nid];
                }
                elems[el.id] = el; labels[el.id] = label; g.Add((uint)el.id);
            }
        }
        var units = new Dictionary<string, string>(); units["length"] = lengthUnit;
        var comps = new List<RawViewerWriter.Component>();
        comps.Add(new RawViewerWriter.Component("Axial N", "force", "kip"));   // layout only; no planes written
        string binPath = outBase + ".bin";
        var w = new RawViewerWriter(binPath, nodes, elems, null, comps, null, null, null, modelId, units);
        w.SetShellLabels(labels);
        w.Write(false);
        var sc = new FeaturesSidecar();
        sc.ModelId = modelId; sc.GeometryHash = w.GeometryHash;
        sc.Units["length"] = lengthUnit;
        sc.Units["worldOffset"] = string.Format(Inv, "{0} {1} {2}", off[0] * scale, off[1] * scale, off[2] * scale);
        string[] palette = { "#7fb3ff", "#3fc1a5", "#ffc04d", "#b0b0b8", "#e040c0", "#5a8fe0", "#ff9f40", "#9ad04d" };
        int pi = 0;
        foreach (KeyValuePair<string, List<uint>> kv in byCat)
        {
            bool hid = kv.Key.EndsWith(", hidden", StringComparison.Ordinal);
            sc.AddGroup(kv.Key, hid ? "#ff3b3b" : palette[pi++ % palette.Length], "shells", kv.Value,
                hid ? new[] { "box", "mesh", kv.Key, "hidden" } : new[] { "box", "mesh", kv.Key }, null);
        }
        string scPath = outBase + ".features.json";
        File.WriteAllText(scPath, sc.ToJson(), new UTF8Encoding(false));
        var res = new Result();
        res.BinPath = binPath; res.SidecarPath = scPath; res.GeometryHash = w.GeometryHash;
        res.Nodes = nodes.Count; res.Beams = 0; res.Skipped = items.Count - recs.Count;
        return res;
    }

    // Box (W x H, wall) or pipe (D, wall) per distinct size; unsized -> a 2 in pipe placeholder.
    static int SectionFor(Row r, double scale, List<RawViewerWriter.SectionDef> sections, Dictionary<string, int> index, out bool sized)
    {
        double ft = scale / 12.0;                                   // inches -> file units
        double t = double.IsNaN(r.T) || r.T <= 0 ? 0.05 : r.T;      // wall: 0.05 in when absent (drawing only)
        string name; RawViewerWriter.SectionDef def;
        sized = true;
        // Precedence: a rectangular W x H (Size property, then Name) beats a diameter unless the shape says
        // Round: a 74 x 28 part also carrying a diameter of 12 was drawn as "DUCT 12 dia" (2026-10-05).
        bool round = r.Shape.IndexOf("round", StringComparison.OrdinalIgnoreCase) >= 0;
        bool rect = !double.IsNaN(r.W) && !double.IsNaN(r.H) && r.W > 0 && r.H > 0;
        bool nameRect = !double.IsNaN(r.NameW) && !double.IsNaN(r.NameH) && r.NameW > 0 && r.NameH > 0;
        bool dia = !double.IsNaN(r.D) && r.D > 0, nameDia = !double.IsNaN(r.NameD) && r.NameD > 0;
        if (round && (dia || nameDia))
        {
            double d = dia ? r.D : r.NameD;
            name = string.Format(Inv, "DUCT {0:0.##} dia", d);
            def = RawViewerWriter.SectionDef.Pipe(name, (float)(d * ft), (float)(Math.Min(t, d / 2) * ft));
        }
        else if (rect)
        {
            name = string.Format(Inv, "DUCT {0:0.##}x{1:0.##}", r.W, r.H);
            def = RawViewerWriter.SectionDef.Box(name, (float)(r.W * ft), (float)(r.H * ft), (float)(Math.Min(t, Math.Min(r.W, r.H) / 2) * ft));
        }
        else if (nameRect)
        {
            name = string.Format(Inv, "DUCT {0:0.##}x{1:0.##} (name)", r.NameW, r.NameH);
            def = RawViewerWriter.SectionDef.Box(name, (float)(r.NameW * ft), (float)(r.NameH * ft), (float)(Math.Min(t, Math.Min(r.NameW, r.NameH) / 2) * ft));
        }
        else if (dia || nameDia)
        {
            double d = dia ? r.D : r.NameD;
            name = string.Format(Inv, "DUCT {0:0.##} dia{1}", d, dia ? "" : " (name)");
            def = RawViewerWriter.SectionDef.Pipe(name, (float)(d * ft), (float)(Math.Min(t, d / 2) * ft));
        }
        else if (!double.IsNaN(r.FitA) && !double.IsNaN(r.FitB) && r.FitA > 0 && r.FitB > 0)
        {
            // no size property (fabrication parts): the fitted cross-section, A horizontal, B the other
            double a = Math.Round(r.FitA, 2), b = Math.Round(r.FitB, 2);
            name = string.Format(Inv, "DUCT {0:0.##}x{1:0.##} (fit)", a, b);
            def = RawViewerWriter.SectionDef.Box(name, (float)(a * ft), (float)(b * ft), (float)(Math.Min(t, Math.Min(a, b) / 2) * ft));
        }
        else
        {
            sized = false;
            name = "UNSIZED";
            def = RawViewerWriter.SectionDef.Pipe(name, (float)(2 * ft), (float)(0.1 * ft));
        }
        int i;
        if (!index.TryGetValue(name, out i)) { i = sections.Count; index[name] = i; sections.Add(def); }
        return i;
    }

    // Size in a fabrication part's Name: "74x28", 74"x28", "74 in x 28 in" (W x H, inches) or 12"ø, 12 dia (round).
    static readonly System.Text.RegularExpressions.Regex RectRx = new System.Text.RegularExpressions.Regex(
        @"(\d+(?:\.\d+)?)\s*(?:""|in)?\s*[xX×]\s*(\d+(?:\.\d+)?)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    static readonly System.Text.RegularExpressions.Regex RoundRx = new System.Text.RegularExpressions.Regex(
        @"(?:(\d+(?:\.\d+)?)\s*(?:""|in)?\s*(?:ø|Ø|dia\b|diam\b))|(?:(?:ø|Ø)\s*(\d+(?:\.\d+)?))", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    static void NameSize(string name, out double w, out double h, out double d)
    {
        w = h = d = double.NaN;
        if (string.IsNullOrEmpty(name)) return;
        var m = RectRx.Match(name);
        if (m.Success) { w = double.Parse(m.Groups[1].Value, Inv); h = double.Parse(m.Groups[2].Value, Inv); return; }
        m = RoundRx.Match(name);
        if (m.Success) d = double.Parse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value, Inv);
    }

    // Stated cross-section (inches), same precedence as SectionFor: round shape -> D x D; rectangular W x H from
    // the Size property, then the Name; a diameter. False when none.
    static bool StatedSize(Row r, out double a, out double b)
    {
        a = b = double.NaN;
        bool round = r.Shape.IndexOf("round", StringComparison.OrdinalIgnoreCase) >= 0;
        double d = !double.IsNaN(r.D) && r.D > 0 ? r.D : r.NameD;
        if (round && !double.IsNaN(d) && d > 0) { a = b = d; return true; }
        if (!double.IsNaN(r.W) && !double.IsNaN(r.H) && r.W > 0 && r.H > 0) { a = r.W; b = r.H; return true; }
        if (!double.IsNaN(r.NameW) && !double.IsNaN(r.NameH) && r.NameW > 0 && r.NameH > 0) { a = r.NameW; b = r.NameH; return true; }
        if (!double.IsNaN(d) && d > 0) { a = b = d; return true; }
        return false;
    }

    // Axis-aligned straight from its bbox: the axis whose two OTHER extents match the stated section (either
    // order, 2 in); ends at the bbox faces on that axis, through the section centre. Several matches (a part
    // as long as it is wide): the longest. False when none (skewed, or not this size at all).
    static bool BboxAxis(Row r, double a, double b, out double[] e1, out double[] e2)
    {
        e1 = e2 = null;
        if (r.Min == null) return false;
        double[] ext = { (r.Max[0] - r.Min[0]) * 12, (r.Max[1] - r.Min[1]) * 12, (r.Max[2] - r.Min[2]) * 12 };
        int best = -1;
        for (int k = 0; k < 3; k++)
        {
            double o1 = ext[(k + 1) % 3], o2 = ext[(k + 2) % 3];
            bool ok = (Near(o1, a) && Near(o2, b)) || (Near(o1, b) && Near(o2, a));
            if (ok && (best < 0 || ext[k] > ext[best])) best = k;
        }
        if (best < 0 || ext[best] < 1e-3) return false;
        double[] c = { (r.Min[0] + r.Max[0]) / 2, (r.Min[1] + r.Max[1]) / 2, (r.Min[2] + r.Max[2]) / 2 };
        e1 = (double[])c.Clone(); e2 = (double[])c.Clone();
        e1[best] = r.Min[best]; e2[best] = r.Max[best];
        return true;
    }

    // A measured extent (bbox / fit, inches) against a stated size: real fabrication parts carry flanges /
    // connectors, so the measure may exceed the nominal by up to 8 in, but not fall short by more than 1 in.
    static bool Near(double measured, double stated) { return measured >= stated - 1 && measured <= stated + 8; }

    // the fitted section matches the stated one (either orientation, flange allowance) and the fit has length
    static bool FitMatches(Row r, double a, double b)
    {
        if (double.IsNaN(r.FitA) || double.IsNaN(r.FitB) || double.IsNaN(r.FitLen)) return false;
        bool sec = (Near(r.FitA, a) && Near(r.FitB, b)) || (Near(r.FitA, b) && Near(r.FitB, a));
        return sec && r.FitLen * 12 > 0.5 * Math.Min(a, b);
    }

    // Meeting point of the duct axes entering a fitting (file units): for each pair of ends, the midpoint of
    // the closest points of their lines (end + s * outward direction); parallel pairs give the ends' midpoint.
    // The average over pairs.
    static double[] WorkPoint(List<int> ends, Dictionary<int, double[]> dirOf, Dictionary<int, Node> nodes)
    {
        double sx = 0, sy = 0, sz = 0; int n = 0;
        for (int i = 0; i < ends.Count; i++)
            for (int j = i + 1; j < ends.Count; j++)
            {
                Node pi = nodes[ends[i]], pj = nodes[ends[j]];
                double[] p = { pi.xyz.X, pi.xyz.Y, pi.xyz.Z }, q = { pj.xyz.X, pj.xyz.Y, pj.xyz.Z };
                double[] u = dirOf[ends[i]], v = dirOf[ends[j]];
                double[] w0 = { p[0] - q[0], p[1] - q[1], p[2] - q[2] };
                double a = u[0] * u[0] + u[1] * u[1] + u[2] * u[2], b = u[0] * v[0] + u[1] * v[1] + u[2] * v[2], c = v[0] * v[0] + v[1] * v[1] + v[2] * v[2];
                double d = u[0] * w0[0] + u[1] * w0[1] + u[2] * w0[2], e = v[0] * w0[0] + v[1] * w0[1] + v[2] * w0[2];
                double den = a * c - b * b;
                double mx, my, mz;
                if (Math.Abs(den) < 1e-6) { mx = (p[0] + q[0]) / 2; my = (p[1] + q[1]) / 2; mz = (p[2] + q[2]) / 2; }
                else
                {
                    double s = (b * e - c * d) / den, t = (a * e - b * d) / den;
                    mx = (p[0] + u[0] * s + q[0] + v[0] * t) / 2; my = (p[1] + u[1] * s + q[1] + v[1] * t) / 2; mz = (p[2] + u[2] * s + q[2] + v[2] * t) / 2;
                }
                sx += mx; sy += my; sz += mz; n++;
            }
        return new[] { sx / n, sy / n, sz / n };
    }

    // A fabrication straight's fit is trusted when its section matches the Name size (either orientation,
    // 2 in); with no size in the Name, when it is longer than its section (a short wide piece's fit takes
    // the width as its axis: the "142.78 x 15.91 (fit)" planes, 2026-10-05).
    static bool FitTrusted(Row r)
    {
        if (double.IsNaN(r.FitA) || double.IsNaN(r.FitB) || double.IsNaN(r.FitLen) || r.E1 == null) return false;
        double sw = !double.IsNaN(r.NameD) ? r.NameD : r.NameW, sh = !double.IsNaN(r.NameD) ? r.NameD : r.NameH;
        if (double.IsNaN(sw) || double.IsNaN(sh)) return r.FitLen * 12 > Math.Max(r.FitA, r.FitB);
        return (Near(r.FitA, sw) && Near(r.FitB, sh)) || (Near(r.FitA, sh) && Near(r.FitB, sw));
    }

    static RawViewerWriter.BeamMember Beam(int id, int a, int b, int sec, Dictionary<int, Node> nodes)
    {
        var m = new RawViewerWriter.BeamMember();
        m.Id = id; m.NodeA = a; m.NodeB = b; m.SectionIndex = sec;
        Node na = nodes[a], nb = nodes[b];
        double dx = nb.xyz.X - na.xyz.X, dy = nb.xyz.Y - na.xyz.Y, dz = nb.xyz.Z - na.xyz.Z;
        double l = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (l > 0) { dx /= l; dy /= l; dz /= l; }
        // y = up x axis (horizontal, so z = x cross y is as vertical as possible); vertical member: y = X
        double yx = -dy, yy = dx, yz = 0;
        double yl = Math.Sqrt(yx * yx + yy * yy);
        m.LocalY = yl < 1e-6 ? new double[] { 1, 0, 0 } : new double[] { yx / yl, yy / yl, yz };
        return m;
    }

    static int MakeNode(Dictionary<int, Node> nodes, ref int next, double[] p, double[] off, double scale)
    {
        var n = new Node();
        n.id = next++;
        n.xyz.X = (float)((p[0] - off[0]) * scale);
        n.xyz.Y = (float)((p[1] - off[1]) * scale);
        n.xyz.Z = (float)((p[2] - off[2]) * scale);
        nodes[n.id] = n;
        return n.id;
    }

    static List<Row> ReadRows(string path)
    {
        var rows = new List<Row>();
        foreach (Dictionary<string, string> c in ReadCsv(path))
        {
            var r = new Row();
            r.Guid = Get(c, "IfcGUID"); r.NavisId = Get(c, "NavisId"); r.Cat = Get(c, "Category"); r.System = Get(c, "SystemName"); r.SizeText = Get(c, "Size");
            r.Name = Get(c, "Name"); r.Shape = Get(c, "Shape"); r.FitA = Num(c, "FitA_in"); r.FitB = Num(c, "FitB_in"); r.FitLen = Num(c, "FitLength_ft");
            NameSize(r.Name, out r.NameW, out r.NameH, out r.NameD);
            r.Hidden = Get(c, "Hidden"); r.HiddenGeom = Num(c, "HiddenGeom");
            r.W = Num(c, "Width_in"); r.H = Num(c, "Height_in"); r.D = Num(c, "Diameter_in"); r.T = Num(c, "WallThk_in");
            double[] mn = { Num(c, "MinX"), Num(c, "MinY"), Num(c, "MinZ") }, mx = { Num(c, "MaxX"), Num(c, "MaxY"), Num(c, "MaxZ") };
            if (!double.IsNaN(mn[0]) && !double.IsNaN(mx[0])) { r.Min = mn; r.Max = mx; }
            double[] e1 = { Num(c, "X1"), Num(c, "Y1"), Num(c, "Z1") }, e2 = { Num(c, "X2"), Num(c, "Y2"), Num(c, "Z2") };
            if (!double.IsNaN(e1[0]) && !double.IsNaN(e2[0])) { r.E1 = e1; r.E2 = e2; }
            rows.Add(r);
        }
        return rows;
    }

    // RFC 4180-ish: quoted fields with "" escapes; no newlines inside fields (the button never writes them
    // unquoted, and quoted ones only hold commas / quotes in practice).
    static IEnumerable<Dictionary<string, string>> ReadCsv(string path)
    {
        if (!File.Exists(path)) throw new Exception("DuctsToPluto: missing " + path);
        using (var rd = new StreamReader(path, Encoding.UTF8))
        {
            string head = rd.ReadLine();
            if (head == null) yield break;
            List<string> cols = Split(head);
            string line;
            while ((line = rd.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                List<string> f = Split(line);
                var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < cols.Count; i++) d[cols[i]] = i < f.Count ? f[i] : "";
                yield return d;
            }
        }
    }

    static List<string> Split(string line)
    {
        var res = new List<string>(); var sb = new StringBuilder(); bool q = false;
        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (q)
            {
                if (ch == '"') { if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } else q = false; }
                else sb.Append(ch);
            }
            else if (ch == '"') q = true;
            else if (ch == ',') { res.Add(sb.ToString()); sb.Length = 0; }
            else sb.Append(ch);
        }
        res.Add(sb.ToString());
        return res;
    }

    static string Get(Dictionary<string, string> r, string k) { string v; return r.TryGetValue(k, out v) ? v : ""; }

    static double Num(Dictionary<string, string> r, string k)
    {
        double v; string s = Get(r, k);
        return s.Length > 0 && double.TryParse(s, NumberStyles.Float, Inv, out v) ? v : double.NaN;
    }

    static double LengthScale(string unit)
    {
        switch ((unit ?? "in").ToLowerInvariant())
        {
            case "ft": return 1.0;
            case "in": return 12.0;
            case "m": return 0.3048;
            case "mm": return 304.8;
            default: throw new Exception("DuctsToPluto: unknown length unit '" + unit + "'.");
        }
    }
}
