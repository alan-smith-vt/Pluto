using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

// Wall and slab penetrations (2026-11-12, user: a joint where a duct passes through a wall or slab, grouped, so
// downstream users can add a support there if appropriate; no guide or anchor is placed).
// For every clean duct element (rigid links and hanger rows left alone) and every wall box of shape wall or slab
// (the walls CSV, whole boxes, drawn or not; skew boxes are not the wall itself and are skipped): the stretch of
// the centreline inside the box. A stretch no longer than three wall thicknesses + 1 ft is a penetration and gets a
// joint: an end joint of the element that already lies inside the box is used as it is; otherwise the element is
// split at the middle of the stretch (the end joint when that is within 1 in, or when the element is a
// transition, which is never split). A longer stretch is a duct running inside the box: left alone, counted.
// Runs after the orientation and before the supports, so everything downstream sees the split elements.
// <out>.penetrations.csv: one row per joint and wall (Joint as in frames.csv). Viewer: node groups
// "Penetration: wall" / "Penetration: slab". SAP: point groups PEN_WALL / PEN_SLAB (SapSteel.cs).
public class Penetration
{
    public int Node; public string Kind = "", WallOid = "", System = "";
    public double ThkIn; public bool Split;
}

public static class DuctPenetrations
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public const double SnapFt = 1.0 / 12;

    public class Result
    {
        public List<Penetration> Items = new List<Penetration>();
        public int Splits, AtJoint, Inside;
        public string Note = "";
    }

    public static Result Apply(List<CEdge> clean, Dictionary<int, double[]> P, List<WallBox> walls)
    {
        var res = new Result();
        // boxes in feet with their kind and thickness
        var boxes = new List<double[]>(); var kinds = new List<string>(); var oids = new List<string>();
        foreach (WallBox w in walls)
        {
            if (w.Shape != "wall" && w.Shape != "slab") continue;
            double[] b = { w.Lo[0] / 0.3048, w.Lo[1] / 0.3048, w.Lo[2] / 0.3048, w.Hi[0] / 0.3048, w.Hi[1] / 0.3048, w.Hi[2] / 0.3048, 0 };
            if (b[3] - b[0] <= 1e-6 || b[4] - b[1] <= 1e-6 || b[5] - b[2] <= 1e-6) continue;
            b[6] = w.Shape == "slab" ? b[5] - b[2] : Math.Min(b[3] - b[0], b[4] - b[1]);   // thickness
            boxes.Add(b); kinds.Add(w.Shape); oids.Add(w.Oid ?? "");
        }
        if (boxes.Count == 0) { res.Note = "penetrations: no wall or slab boxes"; return res; }
        int nextNode = P.Count > 0 ? P.Keys.Max() + 1 : 1;
        var seen = new HashSet<string>(StringComparer.Ordinal);   // joint | box: two elements meeting inside a wall report it once
        foreach (CEdge ce in clean.ToList())
        {
            if (ce.Rigid || ce.Hanger) continue;
            double[] a = P[ce.A], bb = P[ce.B];
            double[] d = { bb[0] - a[0], bb[1] - a[1], bb[2] - a[2] };
            double len = Math.Sqrt(d[0] * d[0] + d[1] * d[1] + d[2] * d[2]);
            if (len < 1e-6) continue;
            var hits = new List<KeyValuePair<double, int>>();   // t of the joint (0 / 1 = an end joint inside the box), box index
            for (int i = 0; i < boxes.Count; i++)
            {
                double[] b = boxes[i];
                double t0 = 0, t1 = 1; bool miss = false;
                for (int ax = 0; ax < 3 && !miss; ax++)
                {
                    if (Math.Abs(d[ax]) < 1e-12) { if (a[ax] < b[ax] || a[ax] > b[ax + 3]) miss = true; continue; }
                    double u0 = (b[ax] - a[ax]) / d[ax], u1 = (b[ax + 3] - a[ax]) / d[ax];
                    if (u0 > u1) { double s = u0; u0 = u1; u1 = s; }
                    t0 = Math.Max(t0, u0); t1 = Math.Min(t1, u1);
                    if (t0 > t1) miss = true;
                }
                if (miss) continue;
                double inside = (t1 - t0) * len;
                if (inside < 0.01) continue;                          // grazes a face
                if (inside > 3 * b[6] + 1) { res.Inside++; continue; }   // runs inside the box: not a penetration
                hits.Add(new KeyValuePair<double, int>(t1 >= 1 - 1e-9 ? 1 : t0 <= 1e-9 ? 0 : (t0 + t1) / 2, i));
            }
            if (hits.Count == 0) continue;
            hits.Sort((x, y) => x.Key.CompareTo(y.Key));
            string sys = ce.Src.Count > 0 || ce.SysName != "" ? DuctGeom.SystemOf(ce) : "";
            var cuts = new List<KeyValuePair<double, int>>();   // t, new node (ascending)
            foreach (KeyValuePair<double, int> h in hits)
            {
                int node; bool split = false;
                if (h.Key * len <= SnapFt) node = ce.A;
                else if ((1 - h.Key) * len <= SnapFt) node = ce.B;
                else if (ce.DimB != null) node = h.Key < 0.5 ? ce.A : ce.B;   // a transition is not split
                else if (cuts.Count > 0 && (h.Key - cuts[cuts.Count - 1].Key) * len <= SnapFt) node = cuts[cuts.Count - 1].Value;
                else
                {
                    node = nextNode++;
                    P[node] = new[] { a[0] + h.Key * d[0], a[1] + h.Key * d[1], a[2] + h.Key * d[2] };
                    cuts.Add(new KeyValuePair<double, int>(h.Key, node));
                    split = true; res.Splits++;
                }
                if (!seen.Add(node + "|" + h.Value)) continue;
                if (!split) res.AtJoint++;
                res.Items.Add(new Penetration { Node = node, Kind = kinds[h.Value], WallOid = oids[h.Value], ThkIn = boxes[h.Value][6] * 12, System = sys, Split = split });
            }
            if (cuts.Count == 0) continue;
            int at = clean.IndexOf(ce), prev = ce.A;
            clean.RemoveAt(at);
            foreach (KeyValuePair<double, int> c in cuts) { clean.Insert(at++, CleanModel.CopyEdge(ce, prev, c.Value)); prev = c.Value; }
            clean.Insert(at, CleanModel.CopyEdge(ce, prev, ce.B));
        }
        int nw = res.Items.Count(x => x.Kind == "wall"), ns = res.Items.Count(x => x.Kind == "slab");
        res.Note = string.Format(Inv, "penetrations: {0} (wall {1}, slab {2}) at {3} joints: {4} new joints (the duct split there), {5} at an existing joint; {6} stretches of duct inside a wall or slab box left alone (<out>.penetrations.csv)",
            res.Items.Count, nw, ns, res.Items.Select(x => x.Node).Distinct().Count(), res.Splits, res.AtJoint, res.Inside);
        return res;
    }

    public static void WriteCsv(string path, Result res, Dictionary<int, double[]> P)
    {
        var s = new StringBuilder();
        s.AppendLine("Joint,Kind,X_in,Y_in,Z_in,Thk_in,System,NewJoint,WallOid");
        foreach (Penetration p in res.Items)
        {
            double[] q = P[p.Node];
            s.AppendLine(string.Join(",", new[] { "N" + p.Node, p.Kind, DuctGeom.R3(q[0] * 12), DuctGeom.R3(q[1] * 12), DuctGeom.R3(q[2] * 12),
                DuctGeom.R3(p.ThkIn), p.System, p.Split ? "1" : "0", p.WallOid }.Select(DuctGeom.CsvCell).ToArray()));
        }
        File.WriteAllText(path, s.ToString(), new UTF8Encoding(false));
    }

    // viewer node groups, one per kind; nodeOf: clean node -> viewer node id (0 = not in the file)
    public static void AddGroups(FeaturesSidecar sc, Result res, Func<int, int> nodeOf)
    {
        if (res == null) return;
        foreach (string kind in new[] { "wall", "slab" })
        {
            var ids = new List<uint>(); var anchors = new List<FeaturesSidecar.Anchor>(); var seen = new HashSet<int>();
            foreach (Penetration p in res.Items.Where(x => x.Kind == kind))
            {
                int id = nodeOf(p.Node);
                if (id <= 0 || !seen.Add(id)) continue;
                ids.Add((uint)id);
                anchors.Add(new FeaturesSidecar.Anchor { Domain = "nodes", Id = (uint)id, Label = string.Format(Inv, "N{0} through a {1:0.#} in {2}{3}", p.Node, p.ThkIn, kind,
                    p.WallOid != "" ? " | " + p.WallOid : "") });
            }
            if (ids.Count == 0) continue;
            FeaturesSidecar.Group g = sc.AddNodeGroup("Penetration: " + kind, null, ids, new[] { "duct", "penetration" }, "PEN_" + kind.ToUpperInvariant());
            g.Look = new FeaturesSidecar.Look { Glyph = kind == "wall" ? "square" : "triangle", Tier = "detail",
                Label = kind == "wall" ? "Duct through a wall" : "Duct through a slab", Section = "Penetrations" };
            g.Anchors.AddRange(anchors);
        }
    }
}
