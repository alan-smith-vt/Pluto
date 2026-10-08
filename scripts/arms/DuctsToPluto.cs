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
        public int FabFromBbox, FabPhantom, FabBridged, FittingsBridged, FabBodyAgrees, FabBodyDiffers, FabFromBody;
        public int NoRunName, ServiceOut;
        public string ServiceFilter = "";
        public string Note = "";                  // one-line summary of a side pass (centrelines)
        public SortedDictionary<string, int> BeamsByService = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public string Summary()
        {
            var sv = new StringBuilder();
            foreach (KeyValuePair<string, int> kv in BeamsByService) sv.Append(sv.Length > 0 ? ", " : "").Append(kv.Key).Append(' ').Append(kv.Value);
            return string.Format(CultureInfo.InvariantCulture,
                "services: {0}; rows dropped by service {1}; rows without RunName {2} (drawn, own group); beams by service: {3}\n",
                ServiceFilter == "" ? "all (no filter)" : ServiceFilter, ServiceOut, NoRunName, sv.Length > 0 ? sv.ToString() : "-") +
                string.Format(CultureInfo.InvariantCulture,
                "nodes={0} beams={1}: ducts {2} (fitted-end fallback {3}), fitting centreline {4}, fitting blocks {5}, accessory blocks {6}\n" +
                "fabrication: beams {13} (straights from bbox + size {17}), blocks {14} (fit rejected {16}); hanger blocks {15}\n" +
                "fabrication straights measured from the mesh body (walls, flanges excluded): agrees with stated size {21}, differs (drawn from the mesh, own group) {22}; placed from the mesh {23}\n" +
                "fabrication straights not measurable and not matching their stated size (still drawn; <out>.fab-mismatch.txt) {18}; fittings bridged {19} (fabrication {20})\n" +
                "symbol-fan segments dropped {7}, loose ends {8}, unsized {9}, rows skipped (no geometry) {10}\n{11}\n{12}",
                Nodes, Beams, DuctBeams, DuctFallback, FittingBeams, FittingBlocks, AccessoryBlocks, FanDropped, LooseEnds, Unsized, Skipped,
                BinPath, SidecarPath, FabBeams, FabBlocks, HangerBlocks, FabFitRejected, FabFromBbox, FabPhantom, FittingsBridged, FabBridged,
                FabBodyAgrees, FabBodyDiffers, FabFromBody);
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
        public string Room = "", RefLevel = "", Oid = "", Cwp = "";   // Custom room number etc. (runs from 2026-10-08 on)
        public string RunName = "", Service = ""; // IfcObjectProperties.RunName "A-<bldg>-<service>-DUCT-<n>" (runs from 2026-10-07 on)
        public bool Out;                          // service not in the requested list: not drawn (row kept so row numbers stay aligned)
    }

    // The service field of a RunName ("A-<bldg>-<service>-DUCT-<n>" -> "<service>"), "" when it has no such field.
    static string ServiceOf(string runName)
    {
        string[] f = (runName ?? "").Split('-');
        return f.Length >= 4 ? f[2].Trim() : "";
    }

    public static Result Export(string runDir, string outBase, string modelId, string lengthUnit)
    {
        return Export(runDir, outBase, modelId, lengthUnit, null);
    }

    // services: draw only rows whose RunName service is in the list (ignoring case); rows without a RunName are
    // kept and grouped as such. null / empty = every row. One group per service (colour by service) on top of
    // the category groups; labels carry the RunName.
    public static Result Export(string runDir, string outBase, string modelId, string lengthUnit, string[] services)
    {
        double scale = LengthScale(lengthUnit);   // feet -> file units
        var rows = ReadRows(Path.Combine(runDir, "ducts.csv"));
        var res = new Result();
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (services != null) foreach (string s in services) if (!string.IsNullOrEmpty(s) && s.Trim().Length > 0) keep.Add(s.Trim());
        foreach (Row r in rows)
        {
            if (r.RunName == "") res.NoRunName++;
            else if (keep.Count > 0 && !keep.Contains(r.Service)) { r.Out = true; res.ServiceOut++; }
        }
        res.ServiceFilter = keep.Count > 0 ? string.Join(", ", new List<string>(keep).ToArray()) : "";
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

        var nodes = new Dictionary<int, Node>();
        var beamRow = new Dictionary<int, Row>();          // beam id -> its element row (service groups, run labels)
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
        // fabrication triangles (ducts_tri.bin) for the body measurement of fabrication straights
        var fabRows = new HashSet<int>();
        for (int i = 0; i < rows.Count; i++) if (string.Equals(rows[i].Cat, Fab, StringComparison.OrdinalIgnoreCase)) fabRows.Add(i + 1);
        var fabTri = ReadTriangles(Path.Combine(runDir, "ducts_tri.bin"), fabRows);
        var gFabMesh = new List<uint>();
        Func<Row, string> labelOf = delegate(Row r)
        {
            return r.Guid + (r.NavisId != "" ? " #" + r.NavisId : "")
                + " | " + r.System + " | " + (r.SizeText != "" ? r.SizeText : r.Name)   // IfcGUID is not unique: short NavisId too
                + (r.RunName != "" ? " | run " + r.RunName : "");
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
            labels[bid] = labelOf(r); beamRow[bid] = r;
            grp.Add((uint)bid);
            return true;
        };

        for (int i = 0; i < rows.Count; i++)
        {
            Row r = rows[i]; int rowNo = i + 1;
            if (r.Out) continue;                       // service not requested (also never pending for bridging)
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
                // fabrication straight: the body measured from its own walls (flanges excluded) is the section;
                // the stated size is only a cross-check (it can be junk: "Round 8 in" on a 74 x 26 part)
                Body body = null; bool meshDiffers = false;
                float[] ftri;
                if (fabStraight && fabTri.TryGetValue(rowNo, out ftri)) body = MeasureBody(ftri);
                if (body != null)
                {
                    double sa0, sb0;
                    bool st0 = StatedSize(r, out sa0, out sb0);
                    bool agrees = st0 && ((Math.Abs(body.W - sa0) <= 1 && Math.Abs(body.H - sb0) <= 1) || (Math.Abs(body.W - sb0) <= 1 && Math.Abs(body.H - sa0) <= 1));
                    meshDiffers = !agrees;
                    if (agrees) res.FabBodyAgrees++;
                    else
                    {
                        res.FabBodyDiffers++;
                        double bw = Math.Round(body.W, 1), bh = Math.Round(body.H, 1);
                        string mname = string.Format(Inv, "DUCT {0:0.#}x{1:0.#} (mesh)", bw, bh);
                        if (!sectionIndex.TryGetValue(mname, out sec))
                        {
                            sec = sections.Count; sectionIndex[mname] = sec;
                            double t = double.IsNaN(r.T) || r.T <= 0 ? 0.05 : r.T;
                            sections.Add(RawViewerWriter.SectionDef.Box(mname, (float)(bw * scale / 12), (float)(bh * scale / 12), (float)(Math.Min(t, Math.Min(bw, bh) / 2) * scale / 12)));
                        }
                        sized = true;
                    }
                    label += string.Format(Inv, " | mesh body {0:0.#}x{1:0.#}, {2:0.##} ft{3}", body.W, body.H, body.Len,
                        agrees ? "" : st0 ? string.Format(Inv, " (stated {0:0.##}x{1:0.##}: drawn from the mesh)", sa0, sb0) : " (no stated size: drawn from the mesh)");
                    if (segs == null) { drawn.Add(new[] { ptNode(body.E1), ptNode(body.E2) }); res.FabFromBody++; }
                }
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
                    labels[id] = label; beamRow[id] = r;
                    network.Add(id);
                    if (!sized) gUnsized.Add((uint)id);
                    else if (isDuct) gDucts.Add((uint)id); else if (fab) (meshDiffers ? gFabMesh : gFab).Add((uint)id); else gFitCl.Add((uint)id);
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
                    labels[id] = labelOf(r); beamRow[id] = r;
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
        if (gFabMesh.Count > 0) sc.AddGroup("Fabrication (size from mesh, not as stated)", "#ff5ad0", "beams", gFabMesh, new[] { "duct", Fab, "meshSize" }, "DUCT_FAB_MESH_SIZE");
        if (gFabBox.Count > 0) sc.AddGroup("Fabrication (bbox)", "#ffc04d", "beams", gFabBox, new[] { "duct", Fab, "bbox" }, "DUCT_FAB_BBOX");
        if (gFabRej.Count > 0) sc.AddGroup("Fabrication (fit rejected)", "#ff7a00", "beams", gFabRej, new[] { "duct", Fab, "bbox", "fitRejected" }, "DUCT_FAB_FIT_REJECTED");
        if (gFitBridge.Count > 0) sc.AddGroup("Fittings (bridged)", "#2fa88f", "beams", gFitBridge, new[] { "duct", "Duct Fittings", "bridged" }, "DUCT_FITTINGS_BRIDGED");
        if (gFabBridge.Count > 0) sc.AddGroup("Fabrication fittings (bridged)", "#4a7fd0", "beams", gFabBridge, new[] { "duct", Fab, "bridged" }, "DUCT_FAB_BRIDGED");
        if (gHang.Count > 0) sc.AddGroup("Fabrication hangers", "#e040c0", "beams", gHang, new[] { "duct", FabHangers, "bbox" }, "DUCT_FAB_HANGERS");
        if (gUnsized.Count > 0) sc.AddGroup("Unsized", "#ff3b3b", "beams", gUnsized, new[] { "duct", "unsized" }, "DUCT_UNSIZED");
        // service groups last (the viewer paints an element with the last enabled group that lists it): colour by
        // service, in the order of the requested list, then any other service seen; recolour in the Groups tab
        {
            var bySvc = new Dictionary<string, List<uint>>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            if (services != null) foreach (string s in services) if (s != null && s.Trim().Length > 0 && !order.Contains(s.Trim())) order.Add(s.Trim());
            var noRun = new List<uint>();
            foreach (KeyValuePair<int, Row> kv in beamRow)
            {
                string svc = kv.Value.Service;
                if (kv.Value.RunName == "") { noRun.Add((uint)kv.Key); continue; }
                if (svc == "") svc = "(RunName without service field)";
                List<uint> l; if (!bySvc.TryGetValue(svc, out l)) { l = new List<uint>(); bySvc[svc] = l; }
                l.Add((uint)kv.Key);
            }
            var rest = new List<string>();
            foreach (string k in bySvc.Keys) if (!order.Exists(o => o.Equals(k, StringComparison.OrdinalIgnoreCase))) rest.Add(k);
            rest.Sort(StringComparer.OrdinalIgnoreCase);
            order.AddRange(rest);
            string[] pal = { "#ff4fd8", "#ffd23f", "#4f8cff", "#3fe0e0", "#ff8a3d", "#8ae04f", "#b07cff", "#f0f0f0" };
            int pi = 0;
            foreach (string svc in order)
            {
                List<uint> l;
                if (!bySvc.TryGetValue(svc, out l)) continue;
                l.Sort();
                sc.AddGroup("Service " + svc, pal[pi++ % pal.Length], "beams", l, new[] { "duct", "service", svc }, null);
                res.BeamsByService[svc] = l.Count;
            }
            if (noRun.Count > 0)
            {
                noRun.Sort();
                sc.AddGroup("No RunName", "#ff3b3b", "beams", noRun, new[] { "duct", "service", "noRunName" }, "DUCT_NO_RUNNAME");
                res.BeamsByService["(no RunName)"] = noRun.Count;
            }
        }
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
            string label = r == null ? "row " + rec.Key : r.Guid + (r.NavisId != "" ? " #" + r.NavisId : "")
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

    // Side pass (2026-10-08): the Revit centrelines (cl_segments.csv / cl_nodes.csv, nodes as merged by the
    // button) as thin beams in their own file, one group per RunName (sorted by service), rows without a
    // RunName in their own red group; fabrication straights without a centreline placed as Export places
    // them (mesh body, else the bbox axis matching the stated size; own group; the rest listed in
    // <out>.cl-unoriented.txt). Node groups to review connectivity: free ends (degree 1), junctions
    // (degree >= 3) and RunName changes (segments of different RunNames meet). Labels: run | IfcGUID #NavisId | category | size.
    public static Result ExportCentrelines(string runDir, string outBase, string modelId, string lengthUnit)
    {
        const double OdIn = 2;   // drawn pipe OD, inches
        double scale = LengthScale(lengthUnit);
        var rows = ReadRows(Path.Combine(runDir, "ducts.csv"));
        var nodeXyz = new Dictionary<int, double[]>();
        foreach (Dictionary<string, string> r in ReadCsv(Path.Combine(runDir, "cl_nodes.csv")))
            nodeXyz[int.Parse(r["Node"], Inv)] = new[] { Num(r, "X"), Num(r, "Y"), Num(r, "Z") };
        var segs = new List<int[]>();   // row, cl node 1, cl node 2
        var rowsWithCl = new HashSet<int>();
        foreach (Dictionary<string, string> r in ReadCsv(Path.Combine(runDir, "cl_segments.csv")))
        {
            int row = int.Parse(r["Row"], Inv);
            segs.Add(new[] { row, int.Parse(r["Node1"], Inv), int.Parse(r["Node2"], Inv) });
            rowsWithCl.Add(row);
        }
        // the same recentring as Export (bbox of rows and centreline nodes, whole feet)
        double[] lo = { double.MaxValue, double.MaxValue, double.MaxValue }, hi = { double.MinValue, double.MinValue, double.MinValue };
        foreach (Row r in rows) if (r.Min != null) for (int k = 0; k < 3; k++) { lo[k] = Math.Min(lo[k], r.Min[k]); hi[k] = Math.Max(hi[k], r.Max[k]); }
        foreach (double[] p in nodeXyz.Values) for (int k = 0; k < 3; k++) { lo[k] = Math.Min(lo[k], p[k]); hi[k] = Math.Max(hi[k], p[k]); }
        if (lo[0] == double.MaxValue) throw new Exception("DuctsToPluto: nothing with coordinates in " + runDir);
        double[] off = { Math.Round((lo[0] + hi[0]) / 2), Math.Round((lo[1] + hi[1]) / 2), Math.Round((lo[2] + hi[2]) / 2) };

        var nodes = new Dictionary<int, Node>(); int nextNode = 1;
        var clToFile = new Dictionary<int, int>(); var keyToFile = new Dictionary<string, int>(StringComparer.Ordinal);
        Func<int, int> clNode = delegate(int id)
        {
            int f;
            if (!clToFile.TryGetValue(id, out f)) { f = MakeNode(nodes, ref nextNode, nodeXyz[id], off, scale); clToFile[id] = f; }
            return f;
        };
        Func<double[], int> ptNode = delegate(double[] p)
        {
            string key = string.Format(Inv, "{0:F4}|{1:F4}|{2:F4}", p[0], p[1], p[2]);
            int f;
            if (!keyToFile.TryGetValue(key, out f)) { f = MakeNode(nodes, ref nextNode, p, off, scale); keyToFile[key] = f; }
            return f;
        };
        var sections = new List<RawViewerWriter.SectionDef>();
        sections.Add(RawViewerWriter.SectionDef.Pipe("centreline", (float)(OdIn * scale / 12), (float)(OdIn / 4 * scale / 12)));
        var beams = new Dictionary<int, RawViewerWriter.BeamMember>(); var labels = new Dictionary<int, string>();
        var byRun = new Dictionary<string, List<uint>>(StringComparer.Ordinal); var fitted = new List<uint>(); var unoriented = new List<string>();
        var runsAt = new Dictionary<int, HashSet<string>>(); var degree = new Dictionary<int, int>();
        int nextBeam = 1, skipped = 0;
        Func<Row, string> labelOf = r => (r.RunName != "" ? r.RunName : "(no RunName)") + " | " + r.Guid + (r.NavisId != "" ? " #" + r.NavisId : "")
            + " | " + r.Cat + " | " + (r.SizeText != "" ? r.SizeText : r.Name);
        Func<Row, int, int, int> add = delegate(Row r, int a, int b)
        {
            if (a == b) return 0;
            int id = nextBeam++;
            beams[id] = Beam(id, a, b, 0, nodes); labels[id] = labelOf(r);
            List<uint> l; if (!byRun.TryGetValue(r.RunName, out l)) { l = new List<uint>(); byRun[r.RunName] = l; }
            l.Add((uint)id);
            foreach (int n in new[] { a, b })
            {
                int d; degree.TryGetValue(n, out d); degree[n] = d + 1;
                HashSet<string> s; if (!runsAt.TryGetValue(n, out s)) { s = new HashSet<string>(StringComparer.Ordinal); runsAt[n] = s; }
                s.Add(r.RunName);
            }
            return id;
        };
        foreach (int[] s in segs)
        {
            if (s[0] < 1 || s[0] > rows.Count || !nodeXyz.ContainsKey(s[1]) || !nodeXyz.ContainsKey(s[2])) { skipped++; continue; }
            add(rows[s[0] - 1], clNode(s[1]), clNode(s[2]));
        }
        var fabRows = new HashSet<int>();
        for (int i = 0; i < rows.Count; i++) if (string.Equals(rows[i].Cat, Fab, StringComparison.OrdinalIgnoreCase)) fabRows.Add(i + 1);
        var fabTri = ReadTriangles(Path.Combine(runDir, "ducts_tri.bin"), fabRows);
        for (int i = 0; i < rows.Count; i++)
        {
            Row r = rows[i];
            if (rowsWithCl.Contains(i + 1) || !fabRows.Contains(i + 1)) continue;
            bool nameSized = !double.IsNaN(r.NameW) || !double.IsNaN(r.NameD);
            if (!r.Name.TrimStart().StartsWith("Straight", StringComparison.OrdinalIgnoreCase) && !(nameSized && FitTrusted(r))) continue;   // as Export
            // placed as Export places it: the body measured from its own walls (stiffeners, flanges excluded),
            // else the bbox axis whose other two extents match the stated size. Never the fit's principal axis:
            // a 74 x 28 straight 24 in long (stiffener spacing) is longest crosswise.
            double[] b1 = null, b2 = null; float[] ftri; double sa, sb;
            Body body = fabTri.TryGetValue(i + 1, out ftri) ? MeasureBody(ftri) : null;
            if (body != null) { b1 = body.E1; b2 = body.E2; }
            else if (!StatedSize(r, out sa, out sb) || !BboxAxis(r, sa, sb, out b1, out b2))
            {
                unoriented.Add(labelOf(r) + (ftri == null ? " | no triangles" : " | body not measurable") + (StatedSize(r, out sa, out sb)
                    ? string.Format(Inv, " | stated {0:0.##}x{1:0.##}, bbox {2}", sa, sb, r.Min == null ? "-" : string.Format(Inv, "{0:0.#}x{1:0.#}x{2:0.#} in",
                        (r.Max[0] - r.Min[0]) * 12, (r.Max[1] - r.Min[1]) * 12, (r.Max[2] - r.Min[2]) * 12)) : " | no stated size"));
                continue;
            }
            int id = add(r, ptNode(b1), ptNode(b2));
            if (id > 0) fitted.Add((uint)id);
        }
        if (beams.Count == 0) throw new Exception("DuctsToPluto: no centrelines in " + runDir);

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
        string[] pal = { "#ff4fd8", "#ffd23f", "#4f8cff", "#3fe0e0", "#ff8a3d", "#8ae04f", "#b07cff", "#f0f0f0" };
        var runs = new List<string>(byRun.Keys);
        runs.Sort((x, y) => { int c = string.Compare(ServiceOf(x), ServiceOf(y), StringComparison.OrdinalIgnoreCase); return c != 0 ? c : string.Compare(x, y, StringComparison.OrdinalIgnoreCase); });
        int pi = 0;
        foreach (string run in runs)
        {
            List<uint> l = byRun[run]; l.Sort();
            if (run == "") sc.AddGroup("No RunName", "#ff3b3b", "beams", l, new[] { "duct", "centreline", "noRunName" }, "CL_NO_RUNNAME");
            else sc.AddGroup("Run " + run, pal[pi++ % pal.Length], "beams", l, new[] { "duct", "centreline", "run", ServiceOf(run) }, null);
        }
        fitted.Sort();
        if (fitted.Count > 0) sc.AddGroup("Fabrication straights (no centreline: mesh body or bbox axis)", "#ffc04d", "beams", fitted, new[] { "duct", "centreline", "fitted" }, "CL_FITTED");
        var ends = new List<uint>(); var junctions = new List<uint>(); var changes = new List<uint>();
        foreach (KeyValuePair<int, int> kv in degree)
        {
            if (kv.Value == 1) ends.Add((uint)kv.Key);
            else if (kv.Value >= 3) junctions.Add((uint)kv.Key);
            if (runsAt[kv.Key].Count > 1) changes.Add((uint)kv.Key);
        }
        ends.Sort(); junctions.Sort(); changes.Sort();
        if (ends.Count > 0) sc.AddNodeGroup("Free ends (degree 1)", "#ff3b3b", ends, new[] { "duct", "centreline", "freeEnd" }, "CL_FREE_ENDS");
        if (junctions.Count > 0) sc.AddNodeGroup("Junctions (degree 3+)", "#3fe0e0", junctions, new[] { "duct", "centreline", "junction" }, "CL_JUNCTIONS");
        if (changes.Count > 0) sc.AddNodeGroup("RunName changes", "#ffd23f", changes, new[] { "duct", "centreline", "runChange" }, "CL_RUN_CHANGES");
        string scPath = outBase + ".features.json";
        File.WriteAllText(scPath, sc.ToJson(), new UTF8Encoding(false));
        File.WriteAllLines(outBase + ".cl-unoriented.txt", unoriented.ToArray());

        var res = new Result();
        res.BinPath = binPath; res.SidecarPath = scPath; res.GeometryHash = w.GeometryHash;
        res.Nodes = nodes.Count; res.Beams = beams.Count; res.Skipped = skipped; res.LooseEnds = ends.Count;
        res.Note = string.Format(Inv, "centrelines: {0} beams ({1} fabrication straights placed from mesh body or bbox, {9} not placed: <out>.cl-unoriented.txt), {2} run groups{3}; nodes {4}: free ends {5}, junctions {6}, RunName changes {7}; segments skipped {8}",
            beams.Count, fitted.Count, runs.Count, byRun.ContainsKey("") ? " incl. No RunName" : "", nodes.Count, ends.Count, junctions.Count, changes.Count, skipped, unoriented.Count);
        return res;
    }

    // ---- Opening probe (2026-10-08) ------------------------------------------------------------------
    // Fabrication ductwork has no connector data (no Revit, SP3D models it as equipment), so connectors
    // must come from the mesh. This probe asks whether they can: per fabrication part (optionally one
    // Custom room number), weld its own triangles, chain the boundary edges (edges on one triangle) into
    // loops, and describe each loop: centre, plane normal, round (diameter) or rectangular (a x b), planarity.
    // An open-ended skin should give 2 loops on a straight / elbow, 3 on a tee; a closed solid gives none.
    // Each opening is then matched to the nearest opening of another part: coincident and facing (joint),
    // coaxial within 6 in (slip joint / gap), or unmatched. Nothing here builds the graph yet.
    // Writes <out>.openings.csv, <out>.probe.txt and a viewer overlay: one 6 in stub per opening along its
    // normal, groups by shape and by match status.
    class Opening
    {
        public int Row; public string Shape; public double A, B, Dia;   // inches
        public double[] C, N; public double Planar, Perim;              // C feet; Planar / Perim inches
        public bool SizeOk; public string SizeBand = "no stated size"; public double Near = double.NaN, Dot = double.NaN; public int NearRow; public string Status = "";
        public int Rings = 1; public string Outer = "";                // rings merged into this connector; largest ring's size
    }

    public static Result ExportProbe(string runDir, string outBase, string modelId, string lengthUnit, string room)
    {
        const double WeldFt = 1e-3, BigPerimIn = 12, MatchIn = 1, SlipIn = 6, EndTolIn = 1;
        double scale = LengthScale(lengthUnit);
        var rows = ReadRows(Path.Combine(runDir, "ducts.csv"));
        bool hasRoom = false; foreach (Row r in rows) if (r.Room != "") { hasRoom = true; break; }
        if (!string.IsNullOrEmpty(room) && !hasRoom)
            throw new Exception("DuctsToPluto: ducts.csv has no Room values (run Pluto Fab / Ducts with the 2026-10-08 build)");
        var want = new HashSet<int>();
        for (int i = 0; i < rows.Count; i++)
            if (string.Equals(rows[i].Cat, Fab, StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrEmpty(room) || rows[i].Room.Trim().Equals(room.Trim(), StringComparison.OrdinalIgnoreCase))) want.Add(i + 1);
        if (want.Count == 0) throw new Exception("DuctsToPluto: no fabrication rows" + (string.IsNullOrEmpty(room) ? "" : " in room " + room));
        // connectors are found on EVERY fabrication part, so a connector at the room edge finds its neighbour
        // in the next room; counts and outputs cover the room's parts only
        var allFab = new HashSet<int>();
        for (int i = 0; i < rows.Count; i++) if (string.Equals(rows[i].Cat, Fab, StringComparison.OrdinalIgnoreCase)) allFab.Add(i + 1);
        var tri = ReadTriangles(Path.Combine(runDir, "ducts_tri.bin"), allFab);

        var all = new List<Opening>(); var loopsOf = new Dictionary<int, int[]>();   // row -> {connectors, small, interior, end rings}
        int noTri = 0;
        foreach (int row in allFab)
        {
            float[] f;
            if (!tri.TryGetValue(row, out f) || f.Length < 9) { if (want.Contains(row)) noTri++; continue; }
            int small = 0, interior = 0;
            var ends = new List<Opening>();
            foreach (List<double[]> loop in BoundaryLoops(f, WeldFt))
            {
                Opening o = Describe(loop);
                if (o == null || o.Perim < BigPerimIn) { small++; continue; }
                // an end has all of the part on one side of its plane (EndTolIn): the normal is then turned
                // to point out of the part; a ring with mesh on both sides (stiffener band, seam) is interior
                double dLo = double.MaxValue, dHi = double.MinValue;
                for (int k = 0; k + 2 < f.Length; k += 3)
                {
                    double d = ((f[k] - o.C[0]) * o.N[0] + (f[k + 1] - o.C[1]) * o.N[1] + (f[k + 2] - o.C[2]) * o.N[2]) * 12;
                    if (d < dLo) dLo = d; if (d > dHi) dHi = d;
                }
                if (dHi <= EndTolIn) { }
                else if (dLo >= -EndTolIn) { for (int k = 0; k < 3; k++) o.N[k] = -o.N[k]; }
                else { interior++; continue; }
                o.Row = row; ends.Add(o);
            }
            // rings at one end (same plane within EndTolIn, centres within 2 in, same outward direction) -> one
            // connector, sized by its smallest ring (the duct; the larger ones are flange / collar / other skin)
            ends.Sort((x, y) => x.Perim.CompareTo(y.Perim));
            var conns = new List<Opening>();
            foreach (Opening o in ends)
            {
                Opening hit = null;
                foreach (Opening c in conns)
                {
                    double dx = o.C[0] - c.C[0], dy = o.C[1] - c.C[1], dz = o.C[2] - c.C[2];
                    double along = Math.Abs(dx * c.N[0] + dy * c.N[1] + dz * c.N[2]) * 12, sep = Math.Sqrt(dx * dx + dy * dy + dz * dz) * 12;
                    if (o.N[0] * c.N[0] + o.N[1] * c.N[1] + o.N[2] * c.N[2] > 0.95 && along <= EndTolIn && sep <= 2 + EndTolIn) { hit = c; break; }
                }
                if (hit != null) { hit.Rings++; hit.Outer = o.Shape == "round" ? string.Format(Inv, "round {0:0.#}", o.Dia) : string.Format(Inv, "{0:0.#}x{1:0.#}", o.A, o.B); continue; }
                double sa, sb;
                if (StatedSize(rows[row - 1], out sa, out sb))
                {
                    // agrees within 1 in; "flange" = 1..8 in larger (the end loop is the flange's outer edge;
                    // same allowance as Near()); else differs
                    double e1, e2;
                    if (o.Shape == "round") { e1 = e2 = o.Dia - Math.Max(sa, sb); }
                    else
                    {
                        double hiS = Math.Max(sa, sb), loS = Math.Min(sa, sb);
                        e1 = o.A - hiS; e2 = o.B - loS;
                    }
                    o.SizeOk = Math.Abs(e1) <= 1 && Math.Abs(e2) <= 1;
                    o.SizeBand = o.SizeOk ? "agrees" : e1 >= -1 && e2 >= -1 && e1 <= 8 && e2 <= 8 ? "flange (1-8 in larger)" : "differs";
                }
                conns.Add(o);
            }
            all.AddRange(conns);
            if (want.Contains(row)) loopsOf[row] = new[] { conns.Count, small, interior, ends.Count };
        }
        var ops = all.FindAll(o => want.Contains(o.Row));
        // nearest opening on another part
        for (int i = 0; i < ops.Count; i++)
        {
            Opening o = ops[i]; double best = double.MaxValue; int bj = -1;
            for (int j = 0; j < all.Count; j++)
            {
                if (all[j].Row == o.Row) continue;
                double dx = all[j].C[0] - o.C[0], dy = all[j].C[1] - o.C[1], dz = all[j].C[2] - o.C[2];
                double d = dx * dx + dy * dy + dz * dz;
                if (d < best) { best = d; bj = j; }
            }
            if (bj < 0) { o.Status = "unmatched"; continue; }
            o.Near = Math.Sqrt(best) * 12; o.NearRow = all[bj].Row;
            o.Dot = o.N[0] * all[bj].N[0] + o.N[1] * all[bj].N[1] + o.N[2] * all[bj].N[2];
            o.Status = o.Near <= MatchIn && o.Dot < -0.9 ? "joint" : o.Near <= SlipIn && Math.Abs(o.Dot) > 0.9 ? "near (slip / gap)" : "unmatched";
            if (o.Status != "unmatched" && !want.Contains(o.NearRow)) o.Status += ", other room";
        }

        // CSV
        var csv = new List<string>();
        csv.Add("Row,NavisId,IfcGUID,Name,Type,StatedSize,Room,RunName,Shape,A_in,B_in,Dia_in,SizeOk,SizeBand,Rings,OuterRing,Cx_ft,Cy_ft,Cz_ft,Nx,Ny,Nz,Planarity_in,Perimeter_in,Nearest_in,NearestRow,NormalDot,Status");
        foreach (Opening o in ops)
        {
            Row r = rows[o.Row - 1];
            csv.Add(string.Join(",", new[] { o.Row.ToString(Inv), r.NavisId, r.Guid, CsvCell(r.Name), PartType(r.Name), CsvCell(r.SizeText), CsvCell(r.Room), CsvCell(r.RunName), o.Shape,
                F1(o.A), F1(o.B), F1(o.Dia), o.SizeOk ? "1" : "0", o.SizeBand, o.Rings.ToString(Inv), CsvCell(o.Outer), F3(o.C[0]), F3(o.C[1]), F3(o.C[2]), F3(o.N[0]), F3(o.N[1]), F3(o.N[2]),
                F1(o.Planar), F1(o.Perim), F1(o.Near), o.NearRow > 0 ? o.NearRow.ToString(Inv) : "", F3(o.Dot), o.Status }));
        }
        File.WriteAllLines(outBase + ".openings.csv", csv.ToArray());

        // summary: per part type, how many big openings, shapes, size agreement, match status
        // probe.txt: header, then NEW (what the latest change adds), then the sections already reviewed
        var head = new StringBuilder(); var fresh = new StringBuilder(); var sum = new StringBuilder();
        head.AppendLine(string.Format(Inv, "Opening probe {0}  room {1}", runDir, string.IsNullOrEmpty(room) ? "(all)" : room));
        int rEnd = 0, rInt = 0, rSmall = 0; foreach (int[] v in loopsOf.Values) { rEnd += v[3]; rInt += v[2]; rSmall += v[1]; }
        sum.AppendLine(string.Format(Inv, "fabrication parts {0}; without triangles {1}; rings (perimeter >= {2} in): at ends {3}, interior {4}; smaller loops {5}", want.Count, noTri, BigPerimIn, rEnd, rInt, rSmall));
        sum.AppendLine(string.Format(Inv, "connectors (end rings merged per end) {0}", ops.Count));
        var byType = new SortedDictionary<string, SortedDictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<int, int[]> kv in loopsOf)
        {
            string t = PartType(rows[kv.Key - 1].Name) + (IsRound(rows[kv.Key - 1]) ? " (round)" : "");
            SortedDictionary<string, int> h; if (!byType.TryGetValue(t, out h)) { h = new SortedDictionary<string, int>(); byType[t] = h; }
            string k = kv.Value[0] + " connectors" + (kv.Value[2] > 0 ? " +interior" : ""); int c; h.TryGetValue(k, out c); h[k] = c + 1;
        }
        sum.AppendLine("parts by type: count by number of connectors (end rings merged per end; \"+interior\" = also rings with the part on both sides: stiffeners, seams)");
        foreach (KeyValuePair<string, SortedDictionary<string, int>> t in byType)
        {
            var parts = new List<string>(); foreach (KeyValuePair<string, int> h in t.Value) parts.Add(h.Key + ": " + h.Value);
            sum.AppendLine("  " + t.Key + " -> " + string.Join("; ", parts.ToArray()));
        }
        var tally = new SortedDictionary<string, int>();
        foreach (Opening o in ops)
        {
            foreach (string k in new[] { "shape " + o.Shape, "size vs stated: " + o.SizeBand,
                "rings per connector " + o.Rings, "planarity " + (o.Planar <= 0.25 ? "<= 1/4 in" : o.Planar <= 1 ? "<= 1 in" : "> 1 in (not a flat opening)") })
            { int c; tally.TryGetValue(k, out c); tally[k] = c + 1; }
        }
        sum.AppendLine("openings:"); foreach (KeyValuePair<string, int> kv in tally) sum.AppendLine("  " + kv.Key + ": " + kv.Value);
        // NEW: at most 3 short lines, meant to be typed back by hand
        // 1. status totals; 2. size problems by type (differs / no stated size); 3. unmatched by type
        int nJ = 0, nJo = 0, nN = 0, nNo = 0, nU = 0;
        var badSize = new SortedDictionary<string, int[]>(StringComparer.OrdinalIgnoreCase); var unm = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (Opening o in ops)
        {
            string t = PartType(rows[o.Row - 1].Name) + (o.Shape == "round" ? " (r)" : "");
            bool other = o.Status.EndsWith(", other room");
            if (o.Status.StartsWith("joint")) { nJ++; if (other) nJo++; }
            else if (o.Status.StartsWith("near")) { nN++; if (other) nNo++; }
            else { nU++; int c; unm.TryGetValue(t, out c); unm[t] = c + 1; }
            if (o.SizeBand == "differs" || o.SizeBand == "no stated size")
            {
                int[] b; if (!badSize.TryGetValue(t, out b)) { b = new int[2]; badSize[t] = b; }
                b[o.SizeBand == "differs" ? 0 : 1]++;
            }
        }
        fresh.AppendLine(string.Format(Inv, "status: joint {0} ({1} other room), near {2} ({3} other room), unmatched {4}", nJ, nJo, nN, nNo, nU));
        var sb1 = new List<string>(); foreach (KeyValuePair<string, int[]> kv in badSize) sb1.Add(kv.Key + " " + kv.Value[0] + "/" + kv.Value[1]);
        fresh.AppendLine("size differs/none by type: " + (sb1.Count > 0 ? string.Join(", ", sb1.ToArray()) : "-"));
        var sb2 = new List<string>(); foreach (KeyValuePair<string, int> kv in unm) sb2.Add(kv.Key + " " + kv.Value);
        fresh.AppendLine("unmatched by type: " + (sb2.Count > 0 ? string.Join(", ", sb2.ToArray()) : "-"));
        var dist = new[] { 0, 0, 0, 0, 0 };
        foreach (Opening o in ops) { double d = o.Near; dist[double.IsNaN(d) ? 4 : d <= 0.25 ? 0 : d <= 1 ? 1 : d <= 6 ? 2 : 3]++; }
        sum.AppendLine(string.Format(Inv, "nearest other opening: <= 1/4 in {0}; <= 1 in {1}; <= 6 in {2}; farther {3}; none {4}", dist[0], dist[1], dist[2], dist[3], dist[4]));
        string report = head.ToString() + Environment.NewLine + "=== NEW ===" + Environment.NewLine + fresh.ToString()
            + Environment.NewLine + "=== Reviewed before ===" + Environment.NewLine + sum.ToString();
        File.WriteAllText(outBase + ".probe.txt", report);

        // overlay: 6 in stub per opening along its normal
        var nodes = new Dictionary<int, Node>(); int nextNode = 1;
        double[] lo = { double.MaxValue, double.MaxValue, double.MaxValue }, hi = { double.MinValue, double.MinValue, double.MinValue };
        foreach (Opening o in ops) for (int k = 0; k < 3; k++) { lo[k] = Math.Min(lo[k], o.C[k]); hi[k] = Math.Max(hi[k], o.C[k]); }
        if (ops.Count == 0) { var r0 = new Result(); r0.Note = report; return r0; }
        double[] off = { Math.Round((lo[0] + hi[0]) / 2), Math.Round((lo[1] + hi[1]) / 2), Math.Round((lo[2] + hi[2]) / 2) };
        var sections = new List<RawViewerWriter.SectionDef>();
        sections.Add(RawViewerWriter.SectionDef.Pipe("opening", (float)(1.5 * scale / 12), (float)(0.375 * scale / 12)));
        var beams = new Dictionary<int, RawViewerWriter.BeamMember>(); var labels = new Dictionary<int, string>();
        var gShape = new SortedDictionary<string, List<uint>>(); var gStatus = new SortedDictionary<string, List<uint>>(); var gSize = new List<uint>();
        int nb = 1;
        foreach (Opening o in ops)
        {
            Row r = rows[o.Row - 1];
            int a = MakeNode(nodes, ref nextNode, o.C, off, scale);
            int b = MakeNode(nodes, ref nextNode, new[] { o.C[0] + o.N[0] * 0.5, o.C[1] + o.N[1] * 0.5, o.C[2] + o.N[2] * 0.5 }, off, scale);
            beams[nb] = Beam(nb, a, b, 0, nodes);
            labels[nb] = string.Format(Inv, "{0} #{1} | {2} | stated {3} | opening {4} | {5} | nearest {6} in", r.Guid, r.NavisId, r.Name, r.SizeText,
                o.Shape == "round" ? string.Format(Inv, "round {0:0.#}", o.Dia) : string.Format(Inv, "{0:0.#}x{1:0.#}", o.A, o.B), o.Status, F1(o.Near));
            List<uint> l;
            if (!gShape.TryGetValue(o.Shape, out l)) { l = new List<uint>(); gShape[o.Shape] = l; } l.Add((uint)nb);
            if (!gStatus.TryGetValue(o.Status, out l)) { l = new List<uint>(); gStatus[o.Status] = l; } l.Add((uint)nb);
            if (o.SizeBand == "differs") gSize.Add((uint)nb);
            nb++;
        }
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
        foreach (KeyValuePair<string, List<uint>> kv in gShape) sc.AddGroup("Opening " + kv.Key, kv.Key == "round" ? "#3fe0e0" : "#4f8cff", "beams", kv.Value, new[] { "duct", "opening", kv.Key }, null);
        if (gSize.Count > 0) sc.AddGroup("Opening size differs from stated", "#ff8a3d", "beams", gSize, new[] { "duct", "opening", "sizeDiffers" }, null);
        foreach (KeyValuePair<string, List<uint>> kv in gStatus)
            sc.AddGroup("Match: " + kv.Key, kv.Key == "joint" ? "#8ae04f" : kv.Key == "unmatched" ? "#ff3b3b" : "#ffd23f", "beams", kv.Value, new[] { "duct", "opening", "match" }, null);
        string scPath = outBase + ".features.json";
        File.WriteAllText(scPath, sc.ToJson(), new UTF8Encoding(false));
        var res = new Result();
        res.BinPath = binPath; res.SidecarPath = scPath; res.GeometryHash = w.GeometryHash;
        res.Nodes = nodes.Count; res.Beams = beams.Count; res.Note = report;
        return res;
    }

    // Boundary loops of a triangle soup (feet): vertices welded on a tol grid, edges used by exactly one
    // triangle chained into closed loops (a vertex with more than two boundary edges is left on whichever
    // walk reaches it first; such loops come out irregular and show in the planarity / size checks).
    static List<List<double[]>> BoundaryLoops(float[] f, double tol)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal); var pts = new List<double[]>();
        int nt = f.Length / 9; var tv = new int[nt * 3];
        for (int i = 0; i < nt * 3; i++)
        {
            double x = f[i * 3], y = f[i * 3 + 1], z = f[i * 3 + 2];
            string k = Math.Round(x / tol).ToString(Inv) + "|" + Math.Round(y / tol).ToString(Inv) + "|" + Math.Round(z / tol).ToString(Inv);
            int id; if (!index.TryGetValue(k, out id)) { id = pts.Count; pts.Add(new[] { x, y, z }); index[k] = id; }
            tv[i] = id;
        }
        var edges = new Dictionary<long, int>();
        for (int t = 0; t < nt; t++)
            for (int e = 0; e < 3; e++)
            {
                int a = tv[t * 3 + e], b = tv[t * 3 + (e + 1) % 3];
                if (a == b) continue;
                long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                int c; edges.TryGetValue(key, out c); edges[key] = c + 1;
            }
        var adj = new Dictionary<int, List<int>>();
        foreach (KeyValuePair<long, int> kv in edges)
        {
            if (kv.Value != 1) continue;
            int a = (int)(kv.Key >> 32), b = (int)(kv.Key & 0xffffffff);
            List<int> l; if (!adj.TryGetValue(a, out l)) { l = new List<int>(); adj[a] = l; } l.Add(b);
            if (!adj.TryGetValue(b, out l)) { l = new List<int>(); adj[b] = l; } l.Add(a);
        }
        var used = new HashSet<long>(); var loops = new List<List<double[]>>();
        foreach (int start in adj.Keys)
        {
            foreach (int first in adj[start])
            {
                long k0 = start < first ? ((long)start << 32) | (uint)first : ((long)first << 32) | (uint)start;
                if (used.Contains(k0)) continue;
                var loop = new List<double[]>(); loop.Add(pts[start]);
                int prev = start, cur = first; used.Add(k0);
                while (cur != start)
                {
                    loop.Add(pts[cur]);
                    int next = -1;
                    foreach (int n in adj[cur])
                    {
                        long kn = cur < n ? ((long)cur << 32) | (uint)n : ((long)n << 32) | (uint)cur;
                        if (!used.Contains(kn)) { next = n; used.Add(kn); break; }
                    }
                    if (next < 0) break;   // open chain (non-manifold): keep what was walked
                    prev = cur; cur = next;
                }
                if (loop.Count >= 3) loops.Add(loop);
            }
        }
        return loops;
    }

    // Centre, Newell normal, planarity, perimeter; round when the radius varies < 6 % about the centre and the loop is circle-full,
    // else a x b along the in-plane principal axes (inches).
    static Opening Describe(List<double[]> p)
    {
        int n = p.Count; double[] c = new double[3]; double per = 0; double[] nn = new double[3];
        for (int i = 0; i < n; i++)
        {
            double[] a = p[i], b = p[(i + 1) % n];
            double l = Math.Sqrt((b[0] - a[0]) * (b[0] - a[0]) + (b[1] - a[1]) * (b[1] - a[1]) + (b[2] - a[2]) * (b[2] - a[2]));
            per += l; for (int k = 0; k < 3; k++) c[k] += (a[k] + b[k]) / 2 * l;
            nn[0] += (a[1] - b[1]) * (a[2] + b[2]); nn[1] += (a[2] - b[2]) * (a[0] + b[0]); nn[2] += (a[0] - b[0]) * (a[1] + b[1]);
        }
        double nl = Math.Sqrt(nn[0] * nn[0] + nn[1] * nn[1] + nn[2] * nn[2]);
        if (per <= 0 || nl < 1e-12) return null;
        for (int k = 0; k < 3; k++) { c[k] /= per; nn[k] /= nl; }
        // in-plane basis
        double[] u = Math.Abs(nn[0]) < 0.9 ? new[] { 1.0, 0, 0 } : new[] { 0, 1.0, 0 };
        double ud = u[0] * nn[0] + u[1] * nn[1] + u[2] * nn[2];
        for (int k = 0; k < 3; k++) u[k] -= ud * nn[k];
        double ul = Math.Sqrt(u[0] * u[0] + u[1] * u[1] + u[2] * u[2]); for (int k = 0; k < 3; k++) u[k] /= ul;
        double[] v = { nn[1] * u[2] - nn[2] * u[1], nn[2] * u[0] - nn[0] * u[2], nn[0] * u[1] - nn[1] * u[0] };
        var xs = new double[n]; var ys = new double[n]; double planar = 0, rs = 0, rs2 = 0, sxx = 0, syy = 0, sxy = 0;
        for (int i = 0; i < n; i++)
        {
            double[] d = { p[i][0] - c[0], p[i][1] - c[1], p[i][2] - c[2] };
            xs[i] = d[0] * u[0] + d[1] * u[1] + d[2] * u[2]; ys[i] = d[0] * v[0] + d[1] * v[1] + d[2] * v[2];
            planar = Math.Max(planar, Math.Abs(d[0] * nn[0] + d[1] * nn[1] + d[2] * nn[2]));
            double r = Math.Sqrt(xs[i] * xs[i] + ys[i] * ys[i]); rs += r; rs2 += r * r;
            sxx += xs[i] * xs[i]; syy += ys[i] * ys[i]; sxy += xs[i] * ys[i];
        }
        double rm = rs / n, cv = rm > 0 ? Math.Sqrt(Math.Max(0, rs2 / n - rm * rm)) / rm : 1;
        var o = new Opening(); o.C = c; o.N = nn; o.Planar = planar * 12; o.Perim = per * 12;
        // round needs both: radius nearly constant AND circle-like fullness 4 pi A / P^2 (circle 1, 24-gon 0.99,
        // square 0.79, 74 x 28 rectangle 0.57); the corners of any rectangle alone are equidistant from its centre
        double fullness = 4 * Math.PI * (nl / 2) / (per * per);
        if (cv < 0.06 && fullness > 0.95) { o.Shape = "round"; o.Dia = 2 * rm * 12; o.A = o.B = double.NaN; return o; }
        double ang = 0.5 * Math.Atan2(2 * sxy, sxx - syy), ca = Math.Cos(ang), sa = Math.Sin(ang);
        double a0 = double.MaxValue, a1 = double.MinValue, b0 = double.MaxValue, b1 = double.MinValue;
        for (int i = 0; i < n; i++)
        {
            double pa = xs[i] * ca + ys[i] * sa, pb = -xs[i] * sa + ys[i] * ca;
            a0 = Math.Min(a0, pa); a1 = Math.Max(a1, pa); b0 = Math.Min(b0, pb); b1 = Math.Max(b1, pb);
        }
        o.Shape = "rect"; o.A = Math.Max(a1 - a0, b1 - b0) * 12; o.B = Math.Min(a1 - a0, b1 - b0) * 12; o.Dia = double.NaN;
        return o;
    }

    static string PartType(string name)
    {
        string s = (name ?? "").Trim(); int i = 0;
        while (i < s.Length && (char.IsLetter(s[i]) || s[i] == ' ' || s[i] == '-') && !(s[i] == ' ' && i + 1 < s.Length && char.IsDigit(s[i + 1]))) i++;
        s = s.Substring(0, i).Trim();
        return s == "" ? "(unnamed)" : s;
    }
    static bool IsRound(Row r) { return r.Shape.IndexOf("round", StringComparison.OrdinalIgnoreCase) >= 0 || (!double.IsNaN(r.NameD) && double.IsNaN(r.NameW)); }
    static string F1(double v) { return double.IsNaN(v) ? "" : v.ToString("0.##", Inv); }
    static string F3(double v) { return double.IsNaN(v) ? "" : v.ToString("0.####", Inv); }
    static string CsvCell(string v) { v = v ?? ""; return v.IndexOfAny(new[] { ',', '"', '\n' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v; }

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
                      .Append(" | ").Append(Get(it, "AncIfcGUID")).Append(aid != "" ? " #" + aid : "")
                      .Append(" (").Append(Get(it, "AncLevelsUp")).Append(" up)");
                if (id != "") sb.Append(" | item #").Append(id);
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

    // ---- fabrication straight body from its own triangles (2026-10-06) ----
    // A fabrication straight's mesh is the duct body plus its end connectors (the sheet formed out into flanges),
    // so its bbox / principal-axis fit measure the flange outline (a 74 x 26 duct came out ~79-81 x 33), and a part
    // wider than long fools the fit into taking the width as the run. Instead: the body's four walls are the
    // largest flat faces. Triangle normals are grouped into directions; the three largest by area (nearly
    // perpendicular) are the part's frame. In each direction the triangles are grouped into planes by offset;
    // the two largest planes are the body walls, their distance the body size (flange rims are narrow strips).
    // The run is the direction whose two wall families have the areas section x length predicts (top / bottom
    // = W x L, sides = H x L); the length is the full extent along it (flanges included, so neighbours meet).
    // Not boxy (round, fittings): null, and the stated-size logic applies.
    class Body { public double W, H, Len; public double[] E1, E2; }

    static Body MeasureBody(float[] f)
    {
        int nt = f.Length / 9;
        if (nt < 8) return null;
        var dirs = new List<double[]>(); var dirArea = new List<double>();
        var tn = new double[nt][]; var ta = new double[nt]; var td = new int[nt];
        double total = 0;
        for (int t = 0; t < nt; t++)
        {
            int o = t * 9;
            double ux = f[o + 3] - f[o], uy = f[o + 4] - f[o + 1], uz = f[o + 5] - f[o + 2];
            double vx = f[o + 6] - f[o], vy = f[o + 7] - f[o + 1], vz = f[o + 8] - f[o + 2];
            double[] n = { uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx };
            double l = Math.Sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2]);
            td[t] = -1;
            if (l < 1e-12) continue;
            n[0] /= l; n[1] /= l; n[2] /= l;
            double area = l / 2; tn[t] = n; ta[t] = area; total += area;
            int hit = -1;
            for (int d = 0; d < dirs.Count; d++) if (Math.Abs(Dot(dirs[d], n)) > 0.999) { hit = d; break; }   // ± same direction, ~2.5 deg
            if (hit < 0) { hit = dirs.Count; dirs.Add(n); dirArea.Add(0); }
            dirArea[hit] += area; td[t] = hit;
        }
        if (dirs.Count < 3 || total <= 0) return null;
        var order = new List<int>(); for (int d = 0; d < dirs.Count; d++) order.Add(d);
        order.Sort((a, b) => dirArea[b].CompareTo(dirArea[a]));
        // the frame: the largest direction, then the largest perpendicular to it, then the largest perpendicular to both
        var fam = new List<int> { order[0] };
        foreach (int d in order) if (fam.Count == 1 && Math.Abs(Dot(dirs[d], dirs[fam[0]])) < 0.03) fam.Add(d);
        if (fam.Count < 2) return null;
        foreach (int d in order) if (fam.Count == 2 && Math.Abs(Dot(dirs[d], dirs[fam[0]])) < 0.03 && Math.Abs(Dot(dirs[d], dirs[fam[1]])) < 0.03) fam.Add(d);
        if (fam.Count < 3) return null;
        if (dirArea[fam[0]] + dirArea[fam[1]] + dirArea[fam[2]] < 0.85 * total) return null;   // not a box (round, curved)
        // orthonormal frame from the three directions
        double[] a0 = (double[])dirs[fam[0]].Clone();
        double[] a1 = Sub3(dirs[fam[1]], Scale3(a0, Dot(dirs[fam[1]], a0))); Normalize(a1);
        double[] a2 = Cross3(a0, a1);
        double[][] ax = { a0, a1, a2 };
        // per axis: planes by offset (0.03 ft bins), the two largest -> wall pair (size, smaller wall area); extent
        var size = new double[3]; var wall = new double[3]; var mid = new double[3]; var lo = new double[3]; var hi = new double[3];
        for (int k = 0; k < 3; k++)
        {
            lo[k] = double.MaxValue; hi[k] = double.MinValue;
            for (int i = 0; i + 2 < f.Length; i += 3)
            {
                double p = f[i] * ax[k][0] + f[i + 1] * ax[k][1] + f[i + 2] * ax[k][2];
                if (p < lo[k]) lo[k] = p; if (p > hi[k]) hi[k] = p;
            }
            var planes = new Dictionary<long, double>(); var planeOff = new Dictionary<long, double>();
            for (int t = 0; t < nt; t++)
            {
                if (td[t] < 0 || Math.Abs(Dot(tn[t], ax[k])) < 0.999) continue;
                int o = t * 9;
                double p = ((f[o] + f[o + 3] + f[o + 6]) * ax[k][0] + (f[o + 1] + f[o + 4] + f[o + 7]) * ax[k][1] + (f[o + 2] + f[o + 5] + f[o + 8]) * ax[k][2]) / 3;
                long key = (long)Math.Round(p / 0.03);   // float32 world coordinates near 1e5 ft are good to ~0.008 ft
                double s; planes.TryGetValue(key, out s); planes[key] = s + ta[t]; planeOff[key] = p;
            }
            long k1 = 0, k2 = 0; double s1 = -1, s2 = -1;
            foreach (KeyValuePair<long, double> kv in planes) if (kv.Value > s1) { s1 = kv.Value; k1 = kv.Key; }
            foreach (KeyValuePair<long, double> kv in planes)
                if (Math.Abs(planeOff[kv.Key] - planeOff[k1]) > 0.1 && kv.Value > s2) { s2 = kv.Value; k2 = kv.Key; }
            if (s2 <= 0) { size[k] = double.NaN; continue; }
            size[k] = Math.Abs(planeOff[k1] - planeOff[k2]); wall[k] = Math.Min(s1, s2); mid[k] = (planeOff[k1] + planeOff[k2]) / 2;
        }
        // run axis: walls along the run have area ~ (other section side) x length
        int run = -1; double best = double.MaxValue;
        for (int r = 0; r < 3; r++)
        {
            int p = (r + 1) % 3, q = (r + 2) % 3;
            if (double.IsNaN(size[p]) || double.IsNaN(size[q])) continue;
            double len = hi[r] - lo[r];
            if (len <= 0 || wall[p] <= 0 || wall[q] <= 0) continue;
            double score = Math.Abs(Math.Log(wall[p] / (size[q] * len))) + Math.Abs(Math.Log(wall[q] / (size[p] * len)));
            if (score < best) { best = score; run = r; }
        }
        if (run < 0 || best > 2 * Math.Log(1.35)) return null;   // walls do not span the run: not a plain straight
        int pa = (run + 1) % 3, pb = (run + 2) % 3;
        // W = the side along the more horizontal cross axis (the beam's local y is horizontal), H the other
        if (Math.Abs(ax[pa][2]) > Math.Abs(ax[pb][2])) { int tmp = pa; pa = pb; pb = tmp; }
        var body = new Body { W = size[pa] * 12, H = size[pb] * 12, Len = hi[run] - lo[run] };
        double[] c = Add3(Scale3(ax[pa], mid[pa]), Scale3(ax[pb], mid[pb]));
        body.E1 = Add3(c, Scale3(ax[run], lo[run])); body.E2 = Add3(c, Scale3(ax[run], hi[run]));
        return body;
    }

    static double Dot(double[] p, double[] q) { return p[0] * q[0] + p[1] * q[1] + p[2] * q[2]; }
    static double[] Sub3(double[] p, double[] q) { return new[] { p[0] - q[0], p[1] - q[1], p[2] - q[2] }; }
    static double[] Add3(double[] p, double[] q) { return new[] { p[0] + q[0], p[1] + q[1], p[2] + q[2] }; }
    static double[] Scale3(double[] p, double s) { return new[] { p[0] * s, p[1] * s, p[2] * s }; }
    static double[] Cross3(double[] p, double[] q) { return new[] { p[1] * q[2] - p[2] * q[1], p[2] * q[0] - p[0] * q[2], p[0] * q[1] - p[1] * q[0] }; }
    static void Normalize(double[] p) { double l = Math.Sqrt(Dot(p, p)); if (l > 0) { p[0] /= l; p[1] /= l; p[2] /= l; } }

    // ducts_tri.bin records of the wanted rows (int32 row, int32 nTri, nTri * 9 float32); others skipped
    static Dictionary<int, float[]> ReadTriangles(string path, HashSet<int> want)
    {
        var res = new Dictionary<int, float[]>();
        if (!File.Exists(path)) return res;
        using (var br = new BinaryReader(File.OpenRead(path)))
        {
            while (br.BaseStream.Position + 8 <= br.BaseStream.Length)
            {
                int row = br.ReadInt32(), n = br.ReadInt32();
                if (!want.Contains(row)) { br.BaseStream.Seek((long)n * 36, SeekOrigin.Current); continue; }
                var f = new float[n * 9];
                for (int k = 0; k < f.Length; k++) f[k] = br.ReadSingle();
                res[row] = f;
            }
        }
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
            r.RunName = Get(c, "RunName"); r.Service = ServiceOf(r.RunName);
            r.Room = Get(c, "Room"); r.RefLevel = Get(c, "RefLevel"); r.Oid = Get(c, "OID"); r.Cwp = Get(c, "CWP");
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
