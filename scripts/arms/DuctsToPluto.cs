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
        public string Summary()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "nodes={0} beams={1}: ducts {2} (fitted-end fallback {3}), fitting centreline {4}, fitting blocks {5}, accessory blocks {6}\n" +
                "symbol-fan segments dropped {7}, loose ends {8}, unsized {9}, rows skipped (no geometry) {10}\n{11}\n{12}",
                Nodes, Beams, DuctBeams, DuctFallback, FittingBeams, FittingBlocks, AccessoryBlocks, FanDropped, LooseEnds, Unsized, Skipped,
                BinPath, SidecarPath);
        }
    }

    const int FanDegree = 5;
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    class Row
    {
        public string Guid, Cat, System, SizeText;
        public double W, H, D, T;                 // inches; NaN = unknown
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

        for (int i = 0; i < rows.Count; i++)
        {
            Row r = rows[i]; int rowNo = i + 1;
            List<int[]> segs; segsByRow.TryGetValue(rowNo, out segs);
            string label = r.Guid + " | " + r.System + " | " + r.SizeText;
            bool isDuct = r.Cat == "Ducts", isFit = r.Cat == "Duct Fittings";
            if (isDuct || (isFit && segs != null))
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
                if (drawn.Count == 0 && isDuct && r.E1 != null) { drawn.Add(new[] { ptNode(r.E1), ptNode(r.E2) }); res.DuctFallback++; }
                if (drawn.Count == 0 && isDuct) { res.Skipped++; continue; }
                foreach (int[] d in drawn)
                {
                    if (d[0] == d[1]) continue;
                    int id = nextBeam++;
                    beams[id] = Beam(id, d[0], d[1], sec, nodes);
                    labels[id] = label;
                    if (!sized) gUnsized.Add((uint)id);
                    else if (isDuct) gDucts.Add((uint)id); else gFitCl.Add((uint)id);
                    if (isDuct) res.DuctBeams++; else res.FittingBeams++;
                }
                if (drawn.Count > 0) continue;
                // a fitting whose lines were all symbol linework: falls through to a bbox block
            }
            // bbox block: fittings without a centreline, accessories
            if (r.Min == null) { res.Skipped++; continue; }
            double[] ext = { r.Max[0] - r.Min[0], r.Max[1] - r.Min[1], r.Max[2] - r.Min[2] };
            int ax = ext[0] >= ext[1] && ext[0] >= ext[2] ? 0 : ext[1] >= ext[2] ? 1 : 2;
            if (ext[ax] < 1e-6) { res.Skipped++; continue; }
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
            labels[bid] = label;
            if (isFit) { gFitBox.Add((uint)bid); res.FittingBlocks++; } else { gAcc.Add((uint)bid); res.AccessoryBlocks++; }
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
        if (gUnsized.Count > 0) sc.AddGroup("Unsized", "#ff3b3b", "beams", gUnsized, new[] { "duct", "unsized" }, "DUCT_UNSIZED");
        var loose = new List<uint>();
        foreach (KeyValuePair<int, int> kv in clToFile) if (nodeDeg[kv.Key] == 1) loose.Add((uint)kv.Value);
        if (loose.Count > 0) sc.AddNodeGroup("Loose ends", "#ff3b3b", loose, new[] { "duct", "looseEnd" }, "DUCT_LOOSE_ENDS");
        string scPath = outBase + ".features.json";
        File.WriteAllText(scPath, sc.ToJson(), new UTF8Encoding(false));

        res.BinPath = binPath; res.SidecarPath = scPath; res.GeometryHash = w.GeometryHash;
        res.Nodes = nodes.Count; res.Beams = beams.Count; res.LooseEnds = loose.Count; res.Unsized = gUnsized.Count;
        return res;
    }

    // Box (W x H, wall) or pipe (D, wall) per distinct size; unsized -> a 2 in pipe placeholder.
    static int SectionFor(Row r, double scale, List<RawViewerWriter.SectionDef> sections, Dictionary<string, int> index, out bool sized)
    {
        double ft = scale / 12.0;                                   // inches -> file units
        double t = double.IsNaN(r.T) || r.T <= 0 ? 0.05 : r.T;      // wall: 0.05 in when absent (drawing only)
        string name; RawViewerWriter.SectionDef def;
        sized = true;
        if (!double.IsNaN(r.D) && r.D > 0)
        {
            name = string.Format(Inv, "DUCT {0:0.##} dia", r.D);
            def = RawViewerWriter.SectionDef.Pipe(name, (float)(r.D * ft), (float)(Math.Min(t, r.D / 2) * ft));
        }
        else if (!double.IsNaN(r.W) && !double.IsNaN(r.H) && r.W > 0 && r.H > 0)
        {
            name = string.Format(Inv, "DUCT {0:0.##}x{1:0.##}", r.W, r.H);
            def = RawViewerWriter.SectionDef.Box(name, (float)(r.W * ft), (float)(r.H * ft), (float)(Math.Min(t, Math.Min(r.W, r.H) / 2) * ft));
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
            r.Guid = Get(c, "IfcGUID"); r.Cat = Get(c, "Category"); r.System = Get(c, "SystemName"); r.SizeText = Get(c, "Size");
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
