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
//   Cull (RoomRegions.Plan, 2026-11-12; user rule: keep the rooms with ducts, remove everything else):
//          a region is kept when a duct the viewer draws runs through it (the caller passes those only).
//          Steel stays when its midpoint lies in a kept region; steel in a region without ducts or in no region
//          is left out of the model (viewer and SAP steel). Walls and slabs are CUT to the kept regions
//          (CutWalls): a wall keeps the stretch that bounds or stands in a kept region, a slab the part over a
//          kept region's floor; the rest is not drawn. Ducts are never removed: the length of drawn duct outside
//          every kept region is reported (a leaking room shows there). When no region holds a duct nothing is
//          culled.
//   Box (user, 2026-11-12: hallways open onto the rooms, so one region runs through the building): the kept
//          regions are trimmed to the plan bounding box of the drawn ducts plus boxPadFt [DuctRoomBoxPadFt]. Steel,
//          walls, slabs and the room floors follow the trimmed regions. A negative pad = no box.
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
    public bool HasDucts;             // a duct centreline runs through it (RoomRegions.Plan)
    public double AreaSqFt, FloorFt, CeilFt;   // CeilFt: the next floor, or the top of the walls in the highest band
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
            double floorZ = double.MaxValue, ceilZ = double.MinValue;
            foreach (WallBox w in walls)
            {
                if (w.Shape != "wall" && w.Shape != "skew wall") continue;
                double z0 = w.Lo[2] / 0.3048, z1 = w.Hi[2] / 0.3048;
                if (Math.Min(z1, bandHi) - Math.Max(z0, bandLo) < need) continue;
                double x0 = w.Lo[0] / 0.3048, x1 = w.Hi[0] / 0.3048, y0 = w.Lo[1] / 0.3048, y1 = w.Hi[1] / 0.3048;
                if (x1 + gapFt < wx0 || x0 - gapFt > wx1 || y1 + gapFt < wy0 || y0 - gapFt > wy1) continue;
                raw.Add(new[] { x0, y0, x1, y1 });
                floorZ = Math.Min(floorZ, Math.Max(z0, bandLo));
                ceilZ = Math.Max(ceilZ, Math.Min(z1, bandHi));
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
                byLabel[lab] = new RoomRegion { Level = k, LevelName = levelName, AreaSqFt = area[lab - 1], FloorFt = floorZ, CeilFt = k < n ? bandHi : ceilZ, BandLo = bandLo, BandHi = bandHi,
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

    public static RoomRegion At(List<RoomRegion> regions, double x, double y, double z)
    {
        foreach (RoomRegion r in regions) if (z >= r.BandLo && z < r.BandHi && r.Contains(x, y)) return r;
        return null;
    }

    public class RoomPlan
    {
        public List<double> FloorsFt = new List<double>();
        public List<RoomRegion> All = new List<RoomRegion>(), Kept = new List<RoomRegion>();
        public List<SteelMember> Steel = new List<SteelMember>();   // the steel after the cull
        public int SteelDropped, SteelNoRegion;   // in a region without ducts / in no region
        public double DuctOutsideFt;              // drawn duct length outside every kept region
        public bool Culled;          // regions with ducts were found: walls go by the kept regions, steel was culled if asked
        public double PadFt;         // a wall this close to a kept region (plan) is drawn
        public string Note = "";
    }

    // The regions and what survives: ducts = centreline segments (x0, y0, z0, x1, y1, z1 feet); steel in meters.
    // Floors come from the walls within marginFt of the ducts' box; the search window covers ducts and steel.
    public static RoomPlan Plan(List<WallBox> walls, List<double[]> ducts, List<SteelMember> steel, List<RoomVoter> voters,
        double marginFt, double gapFt, bool cullSteel)
    {
        return Plan(walls, ducts, steel, voters, marginFt, gapFt, cullSteel, -1);
    }

    public static RoomPlan Plan(List<WallBox> walls, List<double[]> ducts, List<SteelMember> steel, List<RoomVoter> voters,
        double marginFt, double gapFt, bool cullSteel, double boxPadFt)
    {
        var plan = new RoomPlan { PadFt = gapFt + 0.5 };
        var outside = new List<double[]>();   // per duct: length, samples, samples in no kept region (filled below)
        if (steel != null) plan.Steel.AddRange(steel);
        if (walls == null || walls.Count == 0 || ducts == null || ducts.Count == 0) { plan.Note = "rooms: no walls or no ducts, nothing culled"; return plan; }
        double[] lo = { double.MaxValue, double.MaxValue, double.MaxValue }, hi = { double.MinValue, double.MinValue, double.MinValue };
        foreach (double[] d in ducts)
            for (int a = 0; a < 3; a++) { lo[a] = Math.Min(lo[a], Math.Min(d[a], d[a + 3])); hi[a] = Math.Max(hi[a], Math.Max(d[a], d[a + 3])); }
        var near = new List<WallBox>();
        foreach (WallBox w in walls)
        {
            bool ok = true;
            for (int a = 0; a < 3; a++)
            {
                double a0 = w.Lo[a] / 0.3048, a1 = w.Hi[a] / 0.3048;
                if (a1 - a0 <= 1e-6 || (marginFt >= 0 && (a1 < lo[a] - marginFt || a0 > hi[a] + marginFt))) ok = false;
            }
            if (ok) near.Add(w);
        }
        plan.FloorsFt = WallViewWriter.Floors(near);
        // window: ducts and steel (the steel of rooms without ducts has to fall in a region to be culled)
        double[] wlo = { lo[0], lo[1] }, whi = { hi[0], hi[1] };
        if (steel != null)
            foreach (SteelMember b in steel)
            {
                double mx = (b.P0.X + b.P1.X) / 2 / 0.3048, my = (b.P0.Y + b.P1.Y) / 2 / 0.3048;
                wlo[0] = Math.Min(wlo[0], mx); whi[0] = Math.Max(whi[0], mx); wlo[1] = Math.Min(wlo[1], my); whi[1] = Math.Max(whi[1], my);
            }
        Result res = Find(walls, plan.FloorsFt, wlo, whi, gapFt, -1);
        plan.All = res.Regions;
        foreach (double[] d in ducts)
        {
            double len = Math.Sqrt((d[3] - d[0]) * (d[3] - d[0]) + (d[4] - d[1]) * (d[4] - d[1]) + (d[5] - d[2]) * (d[5] - d[2]));
            int steps = Math.Max(1, (int)Math.Ceiling(len / 3));
            var hit = new RoomRegion[steps + 1];
            for (int i = 0; i <= steps; i++)
            {
                double t = (double)i / steps;
                RoomRegion r = At(plan.All, d[0] + t * (d[3] - d[0]), d[1] + t * (d[4] - d[1]), d[2] + t * (d[5] - d[2]));
                if (r != null) r.HasDucts = true;
                hit[i] = r;
            }
            int none = 0; foreach (RoomRegion r in hit) if (r == null) none++;
            plan.DuctOutsideFt += len * none / (steps + 1);
        }
        plan.Kept = plan.All.Where(r => r.HasDucts).ToList();
        plan.Culled = plan.Kept.Count > 0;
        // the box: every kept region trimmed to the drawn ducts' plan box + pad
        string boxNote = "";
        if (plan.Culled && boxPadFt >= 0)
        {
            double bx0 = lo[0] - boxPadFt, by0 = lo[1] - boxPadFt, bx1 = hi[0] + boxPadFt, by1 = hi[1] + boxPadFt;
            double before = 0, after = 0; int trimmed = 0;
            foreach (RoomRegion r in plan.Kept)
            {
                before += r.AreaSqFt;
                var rects = new List<double[]>(); double area = 0;
                double[] nlo = { double.MaxValue, double.MaxValue }, nhi = { double.MinValue, double.MinValue };
                foreach (double[] q in r.Rects)
                {
                    double x0 = Math.Max(q[0], bx0), y0 = Math.Max(q[1], by0), x1 = Math.Min(q[2], bx1), y1 = Math.Min(q[3], by1);
                    if (x1 - x0 <= 1e-6 || y1 - y0 <= 1e-6) continue;
                    rects.Add(new[] { x0, y0, x1, y1 }); area += (x1 - x0) * (y1 - y0);
                    nlo[0] = Math.Min(nlo[0], x0); nlo[1] = Math.Min(nlo[1], y0); nhi[0] = Math.Max(nhi[0], x1); nhi[1] = Math.Max(nhi[1], y1);
                }
                if (area < r.AreaSqFt - 0.5) trimmed++;
                r.Rects = rects; r.AreaSqFt = area; r.Lo = nlo; r.Hi = nhi;
                after += area;
            }
            plan.Kept = plan.Kept.Where(r => r.Rects.Count > 0).ToList();
            boxNote = string.Format(Inv, "\nroom box: the drawn ducts' plan box + {0:0.#} ft ({1:0} x {2:0} ft): {3} of {4} kept regions trimmed, floor kept {5:0} of {6:0} sq ft",
                boxPadFt, bx1 - bx0, by1 - by0, trimmed, plan.Kept.Count, after, before);
        }
        string steelNote = "";
        if (plan.Culled && steel != null && steel.Count > 0)
        {
            var keep = new List<SteelMember>();
            int inRoom = 0;
            foreach (SteelMember b in steel)
            {
                RoomRegion r = At(plan.All, (b.P0.X + b.P1.X) / 2 / 0.3048, (b.P0.Y + b.P1.Y) / 2 / 0.3048, (b.P0.Z + b.P1.Z) / 2 / 0.3048);
                if (r != null && r.HasDucts) { keep.Add(b); inRoom++; continue; }
                if (r == null) plan.SteelNoRegion++; else plan.SteelDropped++;
                if (!cullSteel) keep.Add(b);
            }
            plan.Steel = keep;
            steelNote = string.Format(Inv, "; steel: {0} of {1} members in a room with ducts kept, {2} in a room without ducts and {3} in no room {4}", inRoom, steel.Count,
                plan.SteelDropped, plan.SteelNoRegion, cullSteel ? "left out" : "kept (DuctRoomCull off)");
        }
        string nameNote = plan.Kept.Count > 0 ? "\n" + Name(plan.Kept, voters ?? new List<RoomVoter>()) + " (<out>_clean.rooms.csv)" : "";
        plan.Note = res.Note + "\n" + (plan.Culled
            ? string.Format(Inv, "room cull: {0} of {1} regions hold drawn ducts (areas {2} sq ft){3}; drawn duct outside every room: {4:0.#} ft", plan.Kept.Count, plan.All.Count,
                string.Join(", ", plan.Kept.OrderByDescending(r => r.AreaSqFt).Take(8).Select(r => r.AreaSqFt.ToString("0", Inv)).ToArray()) + (plan.Kept.Count > 8 ? ", ..." : ""),
                steelNote, plan.DuctOutsideFt) + boxNote
            : string.Format(Inv, "room cull: none of {0} regions holds a duct: nothing culled, walls by the {1} ft margin", plan.All.Count, marginFt.ToString("0.#", Inv)))
            + nameNote;
        return plan;
    }

    public class WallPiece { public double[] Lo = new double[3], Hi = new double[3]; public int Level = -1; }   // feet; Level: the band it was cut for (-1: by the box)

    // The walls cut to the kept regions. A wall without an entry is not drawn.
    //   slab / skew slab: the part over each rectangle of a kept region whose floor-to-ceiling range it comes within
    //     3 ft of (its floor, its ceiling);
    //   anything else, per level: the stretches of its length that have a kept region beside or under them (a
    //     bounding wall: the region touches its face; a low wall or curb: it stands in the region), at full
    //     thickness, between the region's floor and ceiling. A stretch is extended over the thickness of a kept
    //     wall that meets it at its end (a corner), never past the wall's own end.
    public static Dictionary<WallBox, List<WallPiece>> CutWalls(List<WallBox> walls, RoomPlan plan)
    {
        const double tol = 0.1, corner = 0.5;
        var res = new Dictionary<WallBox, List<WallPiece>>();
        var box = new Dictionary<WallBox, double[]>();                          // x0, y0, z0, x1, y1, z1 feet
        var raw = new Dictionary<WallBox, Dictionary<int, List<double[]>>>();   // level -> stretches { lo, hi, z0, z1 } along the length
        foreach (WallBox w in walls)
        {
            double[] b = { w.Lo[0] / 0.3048, w.Lo[1] / 0.3048, w.Lo[2] / 0.3048, w.Hi[0] / 0.3048, w.Hi[1] / 0.3048, w.Hi[2] / 0.3048 };
            if (b[3] - b[0] <= 1e-6 || b[4] - b[1] <= 1e-6 || b[5] - b[2] <= 1e-6) continue;
            box[w] = b;
            if (WallViewWriter.IsSlab(w))
            {
                var pcs = new List<WallPiece>(); var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (RoomRegion r in plan.Kept)
                {
                    if (b[5] < r.FloorFt - 3 || b[2] > r.CeilFt + 3) continue;
                    foreach (double[] q in r.Rects)
                    {
                        double x0 = Math.Max(b[0], q[0]), y0 = Math.Max(b[1], q[1]), x1 = Math.Min(b[3], q[2]), y1 = Math.Min(b[4], q[3]);
                        if (x1 - x0 <= 1e-6 || y1 - y0 <= 1e-6) continue;
                        if (!seen.Add(string.Format(Inv, "{0:0.###}|{1:0.###}|{2:0.###}|{3:0.###}", x0, y0, x1, y1))) continue;   // floor of one room, ceiling of the one below
                        pcs.Add(new WallPiece { Lo = new[] { x0, y0, b[2] }, Hi = new[] { x1, y1, b[5] } });
                    }
                }
                if (pcs.Count > 0) res[w] = pcs;
                continue;
            }
            int a = b[3] - b[0] >= b[4] - b[1] ? 0 : 1, c = 1 - a;   // along, across
            var byLevel = new Dictionary<int, List<double[]>>();
            foreach (RoomRegion r in plan.Kept)
            {
                if (b[5] < r.FloorFt + tol || b[2] > r.CeilFt - tol) continue;   // not in this room's height (a wall on the ceiling slab is the level above)
                foreach (double[] q in r.Rects)
                {
                    if (q[c + 2] < b[c] - tol || q[c] > b[c + 3] + tol) continue;   // the region is not beside (or under) the wall
                    double lo = Math.Max(q[a], b[a]), hi = Math.Min(q[a + 2], b[a + 3]);
                    if (hi - lo <= 1e-6) continue;
                    List<double[]> l; if (!byLevel.TryGetValue(r.Level, out l)) { l = new List<double[]>(); byLevel[r.Level] = l; }
                    l.Add(new[] { lo, hi, Math.Max(b[2], r.FloorFt), Math.Min(b[5], r.CeilFt) });
                }
            }
            if (byLevel.Count == 0) continue;
            foreach (int k in byLevel.Keys.ToList()) byLevel[k] = Merge(byLevel[k]);
            raw[w] = byLevel;
        }
        // corners: a stretch ending where a kept wall crosses its line runs on over that wall's thickness
        foreach (KeyValuePair<WallBox, Dictionary<int, List<double[]>>> kv in raw)
        {
            double[] b = box[kv.Key]; int a = b[3] - b[0] >= b[4] - b[1] ? 0 : 1, c = 1 - a;
            foreach (KeyValuePair<int, List<double[]>> lv in kv.Value)
                foreach (double[] iv in lv.Value)
                    foreach (KeyValuePair<WallBox, Dictionary<int, List<double[]>>> o in raw)
                    {
                        if (o.Key == kv.Key) continue;
                        double[] b2 = box[o.Key]; int a2 = b2[3] - b2[0] >= b2[4] - b2[1] ? 0 : 1;
                        List<double[]> its;
                        if (a2 == a || !o.Value.TryGetValue(lv.Key, out its)) continue;
                        if (b2[c] > b[c + 3] + corner || b2[c + 3] < b[c] - corner) continue;                       // does not reach this wall
                        if (!its.Any(i2 => i2[0] <= b[c + 3] + corner && i2[1] >= b[c] - corner)) continue;      // its kept stretch does not
                        if (b2[a] <= iv[1] + corner && b2[a + 3] > iv[1]) iv[1] = Math.Min(b[a + 3], Math.Max(iv[1], b2[a + 3]));
                        if (b2[a + 3] >= iv[0] - corner && b2[a] < iv[0]) iv[0] = Math.Max(b[a], Math.Min(iv[0], b2[a]));
                    }
        }
        foreach (KeyValuePair<WallBox, Dictionary<int, List<double[]>>> kv in raw)
        {
            double[] b = box[kv.Key]; int a = b[3] - b[0] >= b[4] - b[1] ? 0 : 1, c = 1 - a;
            var pcs = new List<WallPiece>();
            foreach (KeyValuePair<int, List<double[]>> lv in kv.Value.OrderBy(x => x.Key))
                foreach (double[] iv in Merge(lv.Value))
                {
                    if (iv[1] - iv[0] < 0.05 || iv[3] - iv[2] < 0.05) continue;
                    var pc = new WallPiece { Level = lv.Key };
                    pc.Lo[a] = iv[0]; pc.Hi[a] = iv[1]; pc.Lo[c] = b[c]; pc.Hi[c] = b[c + 3]; pc.Lo[2] = iv[2]; pc.Hi[2] = iv[3];
                    pcs.Add(pc);
                }
            if (pcs.Count > 0) res[kv.Key] = pcs;
        }
        return res;
    }

    // stretches { lo, hi, z0, z1 } merged where they touch or overlap (the height range widens)
    static List<double[]> Merge(List<double[]> ivs)
    {
        var res = new List<double[]>();
        foreach (double[] iv in ivs.OrderBy(x => x[0]))
        {
            double[] last = res.Count > 0 ? res[res.Count - 1] : null;
            if (last != null && iv[0] <= last[1] + 0.01) { last[1] = Math.Max(last[1], iv[1]); last[2] = Math.Min(last[2], iv[2]); last[3] = Math.Max(last[3], iv[3]); }
            else res.Add((double[])iv.Clone());
        }
        return res;
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
        s.AppendLine("Level,Room,Ducts,Named,Mixed,Area_sqft,Floor_ft,Xmin_ft,Ymin_ft,Xmax_ft,Ymax_ft,Rects,Votes");
        foreach (RoomRegion r in regions)
            s.AppendLine(string.Join(",", new[] { r.LevelName, r.Name, r.HasDucts ? "1" : "0", r.Named ? "1" : "0", r.Mixed ? "1" : "0", r.AreaSqFt.ToString("0.#", Inv), r.FloorFt.ToString("0.##", Inv),
                r.Lo[0].ToString("0.##", Inv), r.Lo[1].ToString("0.##", Inv), r.Hi[0].ToString("0.##", Inv), r.Hi[1].ToString("0.##", Inv),
                r.Rects.Count.ToString(Inv), r.VoteText() }.Select(DuctGeom.CsvCell).ToArray()));
        File.WriteAllText(path, s.ToString(), new UTF8Encoding(false));
    }
}
