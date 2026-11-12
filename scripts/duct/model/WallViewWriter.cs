using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

// Walls in the clean duct model (2026-11-12). Export-DuctsViewer.ps1 -Revit fills Walls from DuctWalls in
// duct-config.psd1 (-Walls overrides): the CSV of scripts\voyager\Export-Walls.ps1, one axis-aligned box per wall
// object (WallOid, Xmin..Zmax in meters, the frame of the steel CSV; Shape optional).
// Drawing only: nothing here reaches frames.csv or the SAP model. Each box is six quad shells on eight nodes
// (the clean file's shell domain; ducts, steel and supports stay beams).
// Scope: a wall is drawn when its box comes within DuctWallMarginFt of the model's box (ducts, steel, supports);
// a negative margin draws every wall.
// Groups by shape, each with its own colour and eye: "Walls: wall", "Walls: slab", "Walls: skew", "Walls: small".
public class WallBox
{
    public string Oid;
    public double[] Lo = new double[3], Hi = new double[3];   // meters
    public string Shape;                                      // wall | slab | skew | small
}

public static class WallViewWriter
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public const double ThinM = 1.5;   // the shape rule of Export-Walls.ps1, used when the CSV has no Shape column
    public static readonly string[] Shapes = { "wall", "slab", "skew", "small" };
    static readonly string[] Colors = { "#b9a58c", "#7d8a99", "#ff7a3b", "#c9c9c9" };

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
        public int Total, Drawn;
        public string Note = "";
    }

    public static string ShapeOf(double dx, double dy, double dz)
    {
        if (dz > ThinM && (dx < ThinM || dy < ThinM)) return "wall";
        if (dz <= ThinM && dx > ThinM && dy > ThinM) return "slab";
        return dz > ThinM ? "skew" : "small";
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
            string s = ix.ContainsKey("Shape") && ix["Shape"] < c.Length ? c[ix["Shape"]].Trim().ToLowerInvariant() : "";
            w.Shape = Array.IndexOf(Shapes, s) >= 0 ? s : ShapeOf(w.Hi[0] - w.Lo[0], w.Hi[1] - w.Lo[1], w.Hi[2] - w.Lo[2]);
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
        foreach (WallBox w in walls)
        {
            double[] a0 = new double[3], a1 = new double[3];   // feet
            bool near = true;
            for (int a = 0; a < 3; a++)
            {
                a0[a] = w.Lo[a] / 0.3048; a1[a] = w.Hi[a] / 0.3048;
                if (!all && (a1[a] < lo[a] - marginFt || a0[a] > hi[a] + marginFt)) near = false;
            }
            if (!near) continue;
            double dx = a1[0] - a0[0], dy = a1[1] - a0[1], dz = a1[2] - a0[2];
            if (dx <= 1e-6 || dy <= 1e-6 || dz <= 1e-6) { flat++; continue; }
            var corner = new Node[8];
            for (int k = 0; k < 8; k++)
            {
                double[] p = { (k & 1) != 0 ? a1[0] : a0[0], (k & 2) != 0 ? a1[1] : a0[1], (k & 4) != 0 ? a1[2] : a0[2] };
                corner[k] = nodes[DuctGeom.MakeNode(nodes, ref next, p, off, scale)];
            }
            double thk = w.Shape == "slab" ? dz : Math.Min(dx, dy);
            string label = string.Format(Inv, "wall | {0} | thk {1:0.#} in | {2:0.#} x {3:0.#} x {4:0.#} ft{5}", w.Shape, thk * 12, dx, dy, dz,
                string.IsNullOrEmpty(w.Oid) ? "" : " | " + w.Oid);
            List<uint> l; if (!v.ByShape.TryGetValue(w.Shape, out l)) { l = new List<uint>(); v.ByShape[w.Shape] = l; }
            foreach (int[] f in Faces)
            {
                var el = new Element(); el.id = nextElem++; el.nNodes = 4; el.n = new Node[4];
                for (int c = 0; c < 4; c++) el.n[c] = corner[f[c]];
                v.Elements[el.id] = el; v.Labels[el.id] = label; l.Add((uint)el.id);
            }
            v.Drawn++;
        }
        v.Note = string.Format(Inv, "walls: {0} of {1} drawn ({2}): {3}{4}", v.Drawn, v.Total,
            all ? "every wall" : string.Format(Inv, "within {0:0.#} ft of the model", marginFt),
            string.Join(", ", Shapes.Select(x => x + " " + (v.ByShape.ContainsKey(x) ? v.ByShape[x].Count / 6 : 0)).ToArray()),
            flat > 0 ? "; " + flat + " with a zero extent left out" : "");
        return v;
    }

    // plain painted groups (not context: each shape keeps its own legend row and eye)
    public static void AddGroups(FeaturesSidecar sc, View v)
    {
        if (v == null) return;
        for (int i = 0; i < Shapes.Length; i++)
        {
            List<uint> ids;
            if (!v.ByShape.TryGetValue(Shapes[i], out ids)) continue;
            FeaturesSidecar.Group g = sc.AddGroup("Walls: " + Shapes[i], Colors[i], "shells", ids, new[] { "wall", Shapes[i] }, null);
            g.Hidden = false;
        }
    }
}
