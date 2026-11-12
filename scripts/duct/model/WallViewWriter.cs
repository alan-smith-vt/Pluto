using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

// Walls in the clean duct model (2026-11-12). Export-DuctsViewer.ps1 -Revit fills Walls from DuctWalls in
// duct-config.psd1 (-Walls overrides): the CSV of scripts\voyager\Export-Walls.ps1, one axis-aligned box per wall
// object (WallOid, Xmin..Zmax in meters, the frame of the steel CSV).
// Drawing only: nothing here reaches frames.csv or the SAP model. Each box is six quad shells on eight nodes
// (the clean file's shell domain; ducts, steel and supports stay beams).
// Scope: a wall is drawn when its box comes within DuctWallMarginFt of the model's box (ducts, steel, supports);
// a negative margin draws every wall.
// Shape, from the box alone (ShapeOf; the CSV's Shape column is not used, so an older CSV follows the same rule):
//   wall        tall (over ThinM) and thin in plan (a plan side under ThinM)
//   slab        flat: not tall, or its height under a quarter of its smaller plan side (a thick slab stays a slab)
//   skew slab   tall, not thin in plan, lower than its smaller plan side: a sloped or stepped slab, or a wall far
//               off the axes (the box cannot tell these apart)
//   skew wall   tall, not thin in plan, at least as high as its smaller plan side: a wall off the axes, an L or a shaft
//   small       everything else (curbs, short pieces)
// Levels: the floors are the top elevations where slabs (slab + skew slab) cover the most plan area: tops within
// FloorTolFt are one elevation, and an elevation is a floor when its area is at least FloorAreaShare of the
// largest one. Every box then falls in one band: below the lowest floor, between two floors, above the highest.
// A slab belongs to the band it is the floor of (by its top); anything else goes by its mid height.
// Groups: "Walls: <shape>" (painted, one colour each) and "Walls level: <band>" (not painted; its eye hides the
// band). A box sits in one of each; either eye off hides it.
public class WallBox
{
    public string Oid;
    public double[] Lo = new double[3], Hi = new double[3];   // meters
    public string Shape;                                      // WallViewWriter.Shapes
}

public static class WallViewWriter
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public const double ThinM = 1.5;            // same rule as Export-Walls.ps1
    public const double FloorTolFt = 1.5;       // slab tops this close are one floor elevation
    public const double FloorAreaShare = 0.25;  // a floor covers at least this share of the largest floor's area
    public static readonly string[] Shapes = { "wall", "slab", "skew wall", "skew slab", "small" };
    static readonly string[] Colors = { "#b9a58c", "#7d8a99", "#ff7a3b", "#d9a441", "#c9c9c9" };

    // box corners by bit (x = 1, y = 2, z = 4) and the six faces, counter-clockwise seen from outside
    static readonly int[][] Faces = {
        new[] { 0, 2, 3, 1 }, new[] { 4, 5, 7, 6 },    // bottom, top
        new[] { 0, 1, 5, 4 }, new[] { 2, 6, 7, 3 },    // y low, y high
        new[] { 0, 4, 6, 2 }, new[] { 1, 3, 7, 5 } };  // x low, x high

    public class View
    {
        public Dictionary<int, Element> Elements = new Dictionary<int, Element>();
        public Dictionary<int, string> Labels = new Dictionary<int, string>();
        public Dictionary<string, List<uint>> ByShape = new Dictionary<string, List<uint>>(StringComparer.Ordinal);
        public List<double> FloorsFt = new List<double>();                 // floor elevations (top of slab), low to high
        public List<string> Levels = new List<string>();                   // band names, low to high (empty = no floors found)
        public List<List<uint>> ByLevel = new List<List<uint>>();
        public List<int> LevelBoxes = new List<int>();
        public int Total, Drawn;
        public string Note = "";
    }

    // extents in meters
    public static string ShapeOf(double dx, double dy, double dz)
    {
        double plan = Math.Min(dx, dy);
        if (dz > ThinM && plan < ThinM) return "wall";
        if (plan > ThinM && (dz <= ThinM || dz < plan / 4)) return "slab";
        if (dz > ThinM) return dz < plan ? "skew slab" : "skew wall";
        return "small";
    }

    // the Export-Walls CSV (every field may be quoted; no commas inside fields)
    public static List<WallBox> Read(string path)
    {
        var res = new List<WallBox>();
        string[] lines = File.ReadAllLines(path);
        if (lines.Length == 0) return res;
        string[] head = Cells(lines[0]);
        var ix = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < head.Length; i++) ix[head[i]] = i;
        string[] need = { "Xmin", "Xmax", "Ymin", "Ymax", "Zmin", "Zmax" };
        foreach (string n in need) if (!ix.ContainsKey(n)) throw new Exception("walls CSV " + path + ": no column " + n + " (expected the Export-Walls.ps1 CSV)");
        for (int r = 1; r < lines.Length; r++)
        {
            if (lines[r].Trim().Length == 0) continue;
            string[] c = Cells(lines[r]);
            var w = new WallBox();
            try
            {
                for (int a = 0; a < 3; a++)
                {
                    double v0 = double.Parse(c[ix[need[2 * a]]], Inv), v1 = double.Parse(c[ix[need[2 * a + 1]]], Inv);
                    w.Lo[a] = Math.Min(v0, v1); w.Hi[a] = Math.Max(v0, v1);
                }
            }
            catch (Exception ex) { throw new Exception("walls CSV " + path + " line " + (r + 1) + ": " + ex.Message); }
            w.Oid = ix.ContainsKey("WallOid") && ix["WallOid"] < c.Length ? c[ix["WallOid"]] : "";
            w.Shape = ShapeOf(w.Hi[0] - w.Lo[0], w.Hi[1] - w.Lo[1], w.Hi[2] - w.Lo[2]);
            res.Add(w);
        }
        return res;
    }

    static string[] Cells(string line)
    {
        string[] c = line.Split(',');
        for (int i = 0; i < c.Length; i++) c[i] = c[i].Trim().Trim('"');
        return c;
    }

    static bool IsSlab(WallBox w) { return w.Shape == "slab" || w.Shape == "skew slab"; }

    // floor elevations (ft, top of slab) from the slabs among the boxes: tops within FloorTolFt of the cluster's
    // lowest are one elevation (area-weighted mean); kept when the area reaches FloorAreaShare of the largest
    public static List<double> Floors(List<WallBox> boxes)
    {
        var slabs = boxes.Where(IsSlab).OrderBy(w => w.Hi[2]).ToList();
        var tops = new List<double>(); var areas = new List<double>();
        double first = 0, sumA = 0, sumAz = 0; bool open = false;
        foreach (WallBox w in slabs)
        {
            double top = w.Hi[2] / 0.3048, area = (w.Hi[0] - w.Lo[0]) * (w.Hi[1] - w.Lo[1]);
            if (open && top - first > FloorTolFt) { tops.Add(sumAz / sumA); areas.Add(sumA); open = false; }
            if (!open) { first = top; sumA = 0; sumAz = 0; open = true; }
            sumA += area; sumAz += area * top;
        }
        if (open) { tops.Add(sumAz / sumA); areas.Add(sumA); }
        var floors = new List<double>();
        if (areas.Count == 0) return floors;
        double max = areas.Max();
        for (int i = 0; i < tops.Count; i++) if (areas[i] >= FloorAreaShare * max) floors.Add(tops[i]);
        return floors;
    }

    // band index of a box: floors at or below its reference height (a slab: its top; anything else: its mid height)
    public static int LevelOf(WallBox w, List<double> floorsFt)
    {
        double z = IsSlab(w) ? w.Hi[2] / 0.3048 + FloorTolFt : (w.Lo[2] + w.Hi[2]) / 2 / 0.3048;
        int k = 0;
        foreach (double f in floorsFt) if (f <= z) k++;
        return k;
    }

    public static string LevelName(int k, List<double> floorsFt)
    {
        int n = floorsFt.Count;
        if (k == 0) return string.Format(Inv, "below {0:0.#} ft", floorsFt[0]);
        if (k == n) return string.Format(Inv, "above {0:0.#} ft", floorsFt[n - 1]);
        return string.Format(Inv, "{0:0.#} to {1:0.#} ft", floorsFt[k - 1], floorsFt[k]);
    }

    // boxes as shells, after the ducts, steel and supports (their nodes give the model's box); null = no walls given
    public static View Add(List<WallBox> walls, double marginFt, Dictionary<int, Node> nodes, ref int next, double[] off, double scale)
    {
        if (walls == null || walls.Count == 0) return null;
        var v = new View { Total = walls.Count };
        // the model's box, feet, from the nodes placed so far
        double[] lo = { double.MaxValue, double.MaxValue, double.MaxValue }, hi = { double.MinValue, double.MinValue, double.MinValue };
        foreach (Node n in nodes.Values)
        {
            double[] p = DuctGeom.NodeFt(n, off, scale);
            for (int a = 0; a < 3; a++) { lo[a] = Math.Min(lo[a], p[a]); hi[a] = Math.Max(hi[a], p[a]); }
        }
        bool all = marginFt < 0 || nodes.Count == 0;
        int flat = 0, nextElem = 1;
        var kept = new List<WallBox>();
        foreach (WallBox w in walls)
        {
            bool near = true, zero = false;
            for (int a = 0; a < 3; a++)
            {
                double a0 = w.Lo[a] / 0.3048, a1 = w.Hi[a] / 0.3048;
                if (!all && (a1 < lo[a] - marginFt || a0 > hi[a] + marginFt)) near = false;
                if (a1 - a0 <= 1e-6) zero = true;
            }
            if (!near) continue;
            if (zero) { flat++; continue; }
            kept.Add(w);
        }

        v.FloorsFt = Floors(kept);
        if (v.FloorsFt.Count > 0)
            for (int k = 0; k <= v.FloorsFt.Count; k++) { v.Levels.Add(LevelName(k, v.FloorsFt)); v.ByLevel.Add(new List<uint>()); v.LevelBoxes.Add(0); }

        foreach (WallBox w in kept)
        {
            double[] a0 = new double[3], a1 = new double[3];   // feet
            for (int a = 0; a < 3; a++) { a0[a] = w.Lo[a] / 0.3048; a1[a] = w.Hi[a] / 0.3048; }
            double dx = a1[0] - a0[0], dy = a1[1] - a0[1], dz = a1[2] - a0[2];
            var corner = new Node[8];
            for (int k = 0; k < 8; k++)
            {
                double[] p = { (k & 1) != 0 ? a1[0] : a0[0], (k & 2) != 0 ? a1[1] : a0[1], (k & 4) != 0 ? a1[2] : a0[2] };
                corner[k] = nodes[DuctGeom.MakeNode(nodes, ref next, p, off, scale)];
            }
            int lv = v.FloorsFt.Count > 0 ? LevelOf(w, v.FloorsFt) : -1;
            double thk = IsSlab(w) ? dz : Math.Min(dx, dy);
            string label = string.Format(Inv, "wall | {0} | thk {1:0.#} in | {2:0.#} x {3:0.#} x {4:0.#} ft | z {5:0.#} to {6:0.#} ft{7}{8}", w.Shape, thk * 12, dx, dy, dz, a0[2], a1[2],
                lv >= 0 ? " | level " + v.Levels[lv] : "", string.IsNullOrEmpty(w.Oid) ? "" : " | " + w.Oid);
            List<uint> l; if (!v.ByShape.TryGetValue(w.Shape, out l)) { l = new List<uint>(); v.ByShape[w.Shape] = l; }
            foreach (int[] f in Faces)
            {
                var el = new Element(); el.id = nextElem++; el.nNodes = 4; el.n = new Node[4];
                for (int c = 0; c < 4; c++) el.n[c] = corner[f[c]];
                v.Elements[el.id] = el; v.Labels[el.id] = label; l.Add((uint)el.id);
                if (lv >= 0) v.ByLevel[lv].Add((uint)el.id);
            }
            if (lv >= 0) v.LevelBoxes[lv]++;
            v.Drawn++;
        }
        v.Note = string.Format(Inv, "walls: {0} of {1} drawn ({2}): {3}{4}", v.Drawn, v.Total,
            all ? "every wall" : string.Format(Inv, "within {0:0.#} ft of the model", marginFt),
            string.Join(", ", Shapes.Select(x => x + " " + (v.ByShape.ContainsKey(x) ? v.ByShape[x].Count / 6 : 0)).ToArray()),
            flat > 0 ? "; " + flat + " with a zero extent left out" : "")
            + "\n" + (v.FloorsFt.Count == 0 ? "wall levels: no slab found, no level groups"
                : string.Format(Inv, "wall levels: {0} floor{1} (top of slab) at {2} ft; boxes per level: {3}", v.FloorsFt.Count, v.FloorsFt.Count == 1 ? "" : "s",
                    string.Join(", ", v.FloorsFt.Select(x => x.ToString("0.#", Inv)).ToArray()),
                    string.Join(", ", Enumerable.Range(0, v.Levels.Count).Select(i => v.Levels[i] + ": " + v.LevelBoxes[i]).ToArray())));
        return v;
    }

    // shape groups: plain painted groups (not context: each shape keeps its own legend row and eye);
    // level groups: unticked so they never paint, there for their eye (low to high; empty bands left out)
    public static void AddGroups(FeaturesSidecar sc, View v)
    {
        if (v == null) return;
        for (int i = 0; i < Shapes.Length; i++)
        {
            List<uint> ids;
            if (!v.ByShape.TryGetValue(Shapes[i], out ids)) continue;
            FeaturesSidecar.Group g = sc.AddGroup("Walls: " + Shapes[i], Colors[i], "shells", ids, new[] { "wall", "shape" }, null);
            g.Hidden = false;
        }
        for (int i = 0; i < v.Levels.Count; i++)
            if (v.ByLevel[i].Count > 0)
            {
                // unticked (a ticked group paints, and a later group paints over the shape colours); the eye still hides it
                FeaturesSidecar.Group g = sc.AddGroup("Walls level: " + v.Levels[i], "#8b919c", "shells", v.ByLevel[i], new[] { "wall", "level" }, null);
                g.Hidden = true;
            }
    }
}
