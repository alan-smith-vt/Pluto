using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

// Room regions from the walls (2026-11-12): per level band (WallViewWriter floors), the plan is cut into a grid on
// every wall face, the cells inside a wall are blocked, and the free cells are flood-filled. Each connected free
// area that does not reach the edge of the search window is a region (a room). Drawing only.
//   Blockers: boxes of shape wall / skew wall that overlap the band by half its height (6 ft at most).
//   Gaps: a wall end that stops up to GapFt short of another wall (columns, modelled doorways) is stretched along
//         its length to that wall's face; an end with nothing ahead is stretched GapFt / 2. A wall box has no
//         door openings.
//   Window: the model's box plus ReachFt, so the rooms at the model's edge are closed by walls beyond it.
//   Names: every object with a room code votes for the region it lies in (Navisworks duct rows by their box
//          centre, steel members by their midpoint; "A/B" gives half a vote to each). A region takes the code
//          with the most votes; under MixedShare of the votes it is marked mixed; no votes = unnamed.
//          The same code on several regions: "<code>", "<code> (2)", ...
// Viewer: one painted group "Room: <name>" per region, its floor as flat shells at the band's floor elevation;
// the shells are also in their band's "Walls level:" group, so hiding a level hides its room floors.
// <out>_clean.rooms.csv lists every region (level, name, area, box, votes).
public class RoomRegion
{
    public int Level;                 // band index (WallViewWriter.LevelOf)
    public string LevelName = "";
    public string Name = "";          // code, "code (2)", or "unnamed <n>"
    public bool Mixed, Named;
    public double AreaSqFt, FloorFt;
    public double[] Lo = new double[2], Hi = new double[2];   // plan box, feet
    public List<double[]> Rects = new List<double[]>();       // x0, y0, x1, y1 feet
    public Dictionary<string, double> Votes = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
    public double BandLo, BandHi;     // elevation range of its band (infinite at the ends)

    public bool Contains(double x, double y)
    {
        if (x < Lo[0] || x > Hi[0] || y < Lo[1] || y > Hi[1]) return false;
        foreach (double[] r in Rects) if (x >= r[0] && x <= r[2] && y >= r[1] && y <= r[3]) return true;
        return false;
    }
    public string VoteText()
    {
        return string.Join(", ", Votes.OrderByDescending(v => v.Value).ThenBy(v => v.Key, StringComparer.OrdinalIgnoreCase)
            .Select(v => v.Key + " " + v.Value.ToString("0.#", CultureInfo.InvariantCulture)).ToArray());
    }
}

public class RoomVoter { public double X, Y, Z; public string Room; }   // feet

public static class RoomRegions
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public const double ReachFt = 150;        // search window past the model's box
    public const double MinAreaSqFt = 20;     // smaller free areas are slivers between walls
    public const double MixedShare = 0.6;     // the winning code needs this share of the votes
    public const long MaxCells = 30000000;
    static readonly string[] Palette = { "#5fa8d3", "#e9c46a", "#90be6d", "#f4a261", "#b5838d", "#4d908e", "#c77dff", "#e76f51", "#8ecae6", "#a7c957" };

    public class Result
    {
        public List<RoomRegion> Regions = new List<RoomRegion>();
        public string Note = "";
    }

    // walls in meters; floors, the model box (lo / hi) and the distances in feet. keepFt: regions whose box comes
    // within it of the model's box are kept (negative = all)
    public static Result Find(List<WallBox> walls, List<double> floorsFt, double[] lo, double[] hi, double gapFt, double keepFt)
    {
        var res = new Result();
        var notes = new List<string>();
        double wx0 = lo[0] - ReachFt, wx1 = hi[0] + ReachFt, wy0 = lo[1] - ReachFt, wy1 = hi[1] + ReachFt;
        int n = floorsFt.Count;
        for (int k = 0; k <= n; k++)
        {
            double bandLo = k == 0 ? double.NegativeInfinity : floorsFt[k - 1], bandHi = k == n ? double.PositiveInfinity : floorsFt[k];
            double need = Math.Min(0.5 * (bandHi - bandLo), 6);
            var raw = new List<double[]>();  // blockers, plan boxes in feet, as modelled
            double floorZ = double.MaxValue;
            foreach (WallBox w in walls)
            {
                if (w.Shape != "wall" && w.Shape != "skew wall") continue;
                double z0 = w.Lo[2] / 0.3048, z1 = w.Hi[2] / 0.3048;
                if (Math.Min(z1, bandHi) - Math.Max(z0, bandLo) < need) continue;
                double x0 = w.Lo[0] / 0.3048, x1 = w.Hi[0] / 0.3048, y0 = w.Lo[1] / 0.3048, y1 = w.Hi[1] / 0.3048;
                if (x1 + gapFt < wx0 || x0 - gapFt > wx1 || y1 + gapFt < wy0 || y0 - gapFt > wy1) continue;
                raw.Add(new[] { x0, y0, x1, y1 });
                floorZ = Math.Min(floorZ, Math.Max(z0, bandLo));
            }
            var bl = new List<double[]>();   // the same, each end stretched along the wall's length, clipped to the window
            foreach (double[] b in raw)
            {
                int ax = b[2] - b[0] >= b[3] - b[1] ? 0 : 1, cr = 1 - ax;   // along, across
                double[] s = (double[])b.Clone();
                for (int end = 0; end < 2; end++)
                {
                    double e = end == 0 ? b[ax] : b[ax + 2], reach = gapFt / 2;   // nothing ahead: half the gap
                    double best = double.MaxValue;
                    foreach (double[] o in raw)
                    {
                        if (o == b || o[cr + 2] <= b[cr] || o[cr] >= b[cr + 2]) continue;   // not ahead of this end's width
                        double d = end == 0 ? e - o[ax + 2] : o[ax] - e;                  // gap to its near face
                        double past = end == 0 ? e - o[ax] : o[ax + 2] - e;               // this end to its far face
                        if (past <= 0) continue;                                           // behind this end
                        if (d < best) best = d;
                    }
                    if (best <= 0) reach = 0; else if (best <= gapFt) reach = best;
                    if (end == 0) s[ax] -= reach; else s[ax + 2] += reach;
                }
                if (s[2] < wx0 || s[0] > wx1 || s[3] < wy0 || s[1] > wy1) continue;
                bl.Add(new[] { Math.Max(s[0], wx0), Math.Max(s[1], wy0), Math.Min(s[2], wx1), Math.Min(s[3], wy1) });
            }
            if (bl.Count == 0) continue;
            if (k > 0) floorZ = bandLo;
            string levelName = n > 0 ? WallViewWriter.LevelName(k, floorsFt) : "all";

            double[] xs = Breaks(bl, 0, wx0, wx1), ys = Breaks(bl, 1, wy0, wy1);
            int nx = xs.Length - 1, ny = ys.Length - 1;
            if ((long)nx * ny > MaxCells) { notes.Add(string.Format(Inv, "level {0}: {1} x {2} cells, too many: skipped", levelName, nx, ny)); continue; }
            var cell = new int[nx * ny];   // -1 wall, 0 free and unvisited, > 0 region label
            foreach (double[] b in bl)
            {
                int i0 = Index(xs, b[0]), i1 = Index(xs, b[2]), j0 = Index(ys, b[1]), j1 = Index(ys, b[3]);
                for (int j = j0; j < j1; j++) for (int i = i0; i < i1; i++) cell[j * nx + i] = -1;
            }
            // flood fill, 4-connected
            int labels = 0; var area = new List<double>(); var open = new List<bool>();
            var q = new Queue<int>();
            for (int s = 0; s < cell.Length; s++)
            {
                if (cell[s] != 0) continue;
                int lab = ++labels; double a = 0; bool edge = false;
                cell[s] = lab; q.Enqueue(s);
                while (q.Count > 0)
                {
                    int c = q.Dequeue(); int i = c % nx, j = c / nx;
                    a += (xs[i + 1] - xs[i]) * (ys[j + 1] - ys[j]);
                    if (i == 0 || j == 0 || i == nx - 1 || j == ny - 1) edge = true;
                    if (i > 0 && cell[c - 1] == 0) { cell[c - 1] = lab; q.Enqueue(c - 1); }
                    if (i < nx - 1 && cell[c + 1] == 0) { cell[c + 1] = lab; q.Enqueue(c + 1); }
                    if (j > 0 && cell[c - nx] == 0) { cell[c - nx] = lab; q.Enqueue(c - nx); }
                    if (j < ny - 1 && cell[c + nx] == 0) { cell[c + nx] = lab; q.Enqueue(c + nx); }
                }
                area.Add(a); open.Add(edge);
            }
            // regions: closed, not slivers; rectangles = runs along x, merged down the rows when the span repeats
            var byLabel = new Dictionary<int, RoomRegion>();
            var lastRect = new Dictionary<long, double[]>();   // (label, i0, i1) -> the rectangle still growing in y
            int slivers = 0, far = 0;
            for (int lab = 1; lab <= labels; lab++)
            {
                if (open[lab - 1]) continue;
                if (area[lab - 1] < MinAreaSqFt) { slivers++; continue; }
                byLabel[lab] = new RoomRegion { Level = k, LevelName = levelName, AreaSqFt = area[lab - 1], FloorFt = floorZ, BandLo = bandLo, BandHi = bandHi,
                    Lo = new[] { double.MaxValue, double.MaxValue }, Hi = new[] { double.MinValue, double.MinValue } };
            }
            for (int j = 0; j < ny; j++)
            {
                int i = 0;
                while (i < nx)
                {
                    int lab = cell[j * nx + i];
                    int i0 = i; while (i < nx && cell[j * nx + i] == lab) i++;
                    RoomRegion r;
                    if (lab <= 0 || !byLabel.TryGetValue(lab, out r)) continue;
                    long key = ((long)lab << 42) | ((long)i0 << 21) | (long)(uint)(i - 1);
                    double[] rect;
                    if (lastRect.TryGetValue(key, out rect) && rect[3] == ys[j]) rect[3] = ys[j + 1];
                    else { rect = new[] { xs[i0], ys[j], xs[i], ys[j + 1] }; r.Rects.Add(rect); lastRect[key] = rect; }
                    r.Lo[0] = Math.Min(r.Lo[0], xs[i0]); r.Hi[0] = Math.Max(r.Hi[0], xs[i]);
                    r.Lo[1] = Math.Min(r.Lo[1], ys[j]); r.Hi[1] = Math.Max(r.Hi[1], ys[j + 1]);
                }
            }
            int kept = 0;
            foreach (RoomRegion r in byLabel.Values.OrderBy(x => x.Lo[1]).ThenBy(x => x.Lo[0]))
            {
                bool near = keepFt < 0 || !(r.Hi[0] < lo[0] - keepFt || r.Lo[0] > hi[0] + keepFt || r.Hi[1] < lo[1] - keepFt || r.Lo[1] > hi[1] + keepFt);
                if (!near) { far++; continue; }
                res.Regions.Add(r); kept++;
            }
            notes.Add(string.Format(Inv, "level {0}: {1} regions from {2} walls{3}{4}", levelName, kept, bl.Count,
                far > 0 ? ", " + far + " beyond the model left out" : "", slivers > 0 ? ", " + slivers + " slivers under " + MinAreaSqFt.ToString("0", Inv) + " sq ft dropped" : ""));
        }
        res.Note = "rooms (walls, gaps up to " + gapFt.ToString("0.#", Inv) + " ft closed): " + (notes.Count > 0 ? string.Join("; ", notes.ToArray()) : "no walls to bound a region");
        return res;
    }

    static double[] Breaks(List<double[]> boxes, int axis, double w0, double w1)
    {
        var v = new List<double> { w0, w1 };
        foreach (double[] b in boxes) { v.Add(b[axis]); v.Add(b[axis + 2]); }
        v.Sort();
        var u = new List<double>();
        foreach (double x in v) if (u.Count == 0 || x - u[u.Count - 1] > 1e-6) u.Add(x);
        return u.ToArray();
    }

    // index of the break at x (the breaks hold every box coordinate)
    static int Index(double[] a, double x)
    {
        int lo = 0, hi = a.Length - 1;
        while (lo < hi) { int mid = (lo + hi) / 2; if (a[mid] < x - 1e-6) lo = mid + 1; else hi = mid; }
        return lo;
    }

    // votes, then names; returns the summary line
    public static string Name(List<RoomRegion> regions, List<RoomVoter> voters)
    {
        int cast = 0;
        foreach (RoomVoter v in voters)
        {
            if (string.IsNullOrEmpty(v.Room)) continue;
            string[] tokens = v.Room.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()).Where(t => t != "").ToArray();
            if (tokens.Length == 0) continue;
            foreach (RoomRegion r in regions)
            {
                if (v.Z < r.BandLo || v.Z >= r.BandHi || !r.Contains(v.X, v.Y)) continue;
                foreach (string t in tokens) { double c; r.Votes.TryGetValue(t, out c); r.Votes[t] = c + 1.0 / tokens.Length; }
                cast++;
                break;
            }
        }
        var used = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int unnamed = 0, mixed = 0, named = 0;
        foreach (RoomRegion r in regions)
        {
            if (r.Votes.Count == 0) { r.Name = "unnamed " + (++unnamed); continue; }
            KeyValuePair<string, double> best = r.Votes.OrderByDescending(v => v.Value).ThenBy(v => v.Key, StringComparer.OrdinalIgnoreCase).First();
            r.Named = true; named++;
            r.Mixed = best.Value < MixedShare * r.Votes.Values.Sum();
            if (r.Mixed) mixed++;
            int c; used.TryGetValue(best.Key, out c); used[best.Key] = c + 1;
            r.Name = best.Key + (c > 0 ? " (" + (c + 1) + ")" : "");
        }
        string twice = string.Join(", ", used.Where(u => u.Value > 1).Select(u => u.Key + " x" + u.Value).ToArray());
        return string.Format(Inv, "room names: {0} regions named by {1} votes ({2} mixed), {3} unnamed{4}", named, cast, mixed, unnamed,
            twice != "" ? "; a code on several regions: " + twice : "");
    }

    // floor patches into the wall view's shells (ids continue after the wall boxes); one group per region
    public static void AddToView(WallViewWriter.View v, List<RoomRegion> regions, Dictionary<int, Node> nodes, ref int next, double[] off, double scale)
    {
        int nextElem = v.Elements.Count > 0 ? v.Elements.Keys.Max() + 1 : 1;
        foreach (RoomRegion r in regions)
        {
            var ids = new List<uint>();
            double z = r.FloorFt + 0.05;   // just above the slab, so the patch is not hidden in it
            string label = string.Format(Inv, "room | {0}{1} | level {2} | {3:0} sq ft | votes: {4}", r.Name, r.Mixed ? " (mixed)" : "", r.LevelName, r.AreaSqFt,
                r.Votes.Count > 0 ? r.VoteText() : "none");
            foreach (double[] q in r.Rects)
            {
                var el = new Element(); el.id = nextElem++; el.nNodes = 4; el.n = new Node[4];
                double[][] p = { new[] { q[0], q[1], z }, new[] { q[2], q[1], z }, new[] { q[2], q[3], z }, new[] { q[0], q[3], z } };
                for (int c = 0; c < 4; c++) el.n[c] = nodes[DuctGeom.MakeNode(nodes, ref next, p[c], off, scale)];
                v.Elements[el.id] = el; v.Labels[el.id] = label; ids.Add((uint)el.id);
            }
            v.RoomGroups.Add(new KeyValuePair<string, List<uint>>("Room: " + r.Name + (r.Mixed ? " (mixed)" : ""), ids));
            if (r.Level < v.ByLevel.Count) v.ByLevel[r.Level].AddRange(ids);   // the level's eye hides its room floors too
        }
    }

    public static void AddGroups(FeaturesSidecar sc, WallViewWriter.View v)
    {
        if (v == null) return;
        int i = 0;
        foreach (KeyValuePair<string, List<uint>> g in v.RoomGroups)
        {
            bool unnamed = g.Key.StartsWith("Room: unnamed ", StringComparison.Ordinal);
            FeaturesSidecar.Group grp = sc.AddGroup(g.Key, unnamed ? "#5a5f68" : Palette[i++ % Palette.Length], "shells", g.Value, new[] { "room" }, null);
            grp.Hidden = false;
        }
    }

    public static void WriteCsv(string path, List<RoomRegion> regions)
    {
        var s = new StringBuilder();
        s.AppendLine("Level,Room,Named,Mixed,Area_sqft,Floor_ft,Xmin_ft,Ymin_ft,Xmax_ft,Ymax_ft,Rects,Votes");
        foreach (RoomRegion r in regions)
            s.AppendLine(string.Join(",", new[] { r.LevelName, r.Name, r.Named ? "1" : "0", r.Mixed ? "1" : "0", r.AreaSqFt.ToString("0.#", Inv), r.FloorFt.ToString("0.##", Inv),
                r.Lo[0].ToString("0.##", Inv), r.Lo[1].ToString("0.##", Inv), r.Hi[0].ToString("0.##", Inv), r.Hi[1].ToString("0.##", Inv),
                r.Rects.Count.ToString(Inv), r.VoteText() }.Select(DuctGeom.CsvCell).ToArray()));
        File.WriteAllText(path, s.ToString(), new UTF8Encoding(false));
    }
}
