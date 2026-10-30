using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

// SteelGeom  --  the steel member geometry rules shared by SteelToPluto, CombinedToPluto and SteelModel
// (one copy: cardinal-point offsets, section local axes, node dedupe, size ramp). C# 5 / Add-Type compatible.
public static class SteelGeom
{
    // UNSIZED placeholder: a generic W8x24-ish shape (meters) so the member is visible
    public const double UD = 0.20, UBf = 0.165, UTf = 0.010, UTw = 0.006;

    public static bool Sized(SteelMember b)
    {
        return !(double.IsNaN(b.D) || double.IsNaN(b.Bf) || double.IsNaN(b.Tf) || double.IsNaN(b.Tw));
    }

    // SP3D 15-point cardinal code -> section-local (y=flange, z=web) offset of the
    // ring center relative to the routed line, in FILE units. The line passes
    // THROUGH the cardinal point, so the ring shifts the opposite way:
    //   cols: 1,4,7 left (-y) | 2,5,8 center | 3,6,9 right (+y)
    //   rows: 1,2,3 bottom (-z) | 4,5,6 middle | 7,8,9 top (+z)
    //   e.g. CP 8 (top-center, CONFIRMED vs Navisworks) -> ring drops by halfD.
    // 10 = centroid; 11..15 (shear-center codes) = centroid for doubly-symmetric W.
    public static void CpOffsets(int cp, double halfBf, double halfD, out double oy, out double oz)
    {
        oy = 0; oz = 0;
        if (cp < 1 || cp > 9) return;
        int col = (cp - 1) % 3;          // 0 left, 1 center, 2 right
        int row = (cp - 1) / 3;          // 0 bottom, 1 middle, 2 top
        oy = col == 0 ? halfBf : col == 2 ? -halfBf : 0;
        oz = row == 0 ? halfD : row == 2 ? -halfD : 0;
    }

    // CpOffsets for a member (UNSIZED placeholder dims when unsized), scale = file units per meter
    public static void MemberOffsets(SteelMember b, double scale, out double oy, out double oz)
    {
        bool sized = Sized(b);
        CpOffsets(b.Cp, (sized ? b.Bf : UBf) * scale / 2, (sized ? b.D : UD) * scale / 2, out oy, out oz);
    }

    // The web direction (bottom -> top flange), unit and square to the axis: the CSV YDir, or the
    // fallback (missing or parallel-to-axis vector) global Z, or global X for a vertical member.
    public static double[] WebDir(SteelMember b, ref int defaulted)
    {
        double ax = b.P1.X - b.P0.X, ay = b.P1.Y - b.P0.Y, az = b.P1.Z - b.P0.Z;
        double al = Math.Sqrt(ax * ax + ay * ay + az * az);
        if (al > 1e-12) { ax /= al; ay /= al; az /= al; }

        double yx = 0, yy = 0, yz = 1;
        bool haveCsv = b.ValidY;
        if (haveCsv) { yx = b.LocalY.X; yy = b.LocalY.Y; yz = b.LocalY.Z; }
        double dot = yx * ax + yy * ay + yz * az;
        if (!haveCsv || Math.Abs(dot) > 0.99)
        {
            defaulted++;
            // default up = global Z, or global X for a near-vertical member
            if (Math.Abs(az) > 0.99) { yx = 1; yy = 0; yz = 0; } else { yx = 0; yy = 0; yz = 1; }
            dot = yx * ax + yy * ay + yz * az;
        }
        // project the web off the axis and renormalize
        yx -= dot * ax; yy -= dot * ay; yz -= dot * az;
        double l = Math.Sqrt(yx * yx + yy * yy + yz * yz);
        return new double[] { yx / l, yy / l, yz / l, ax, ay, az };   // [3..5] = the unit axis
    }

    // The CSV YDir is the WEB direction (bottom -> top flange). The viewer's
    // section outline runs the depth along local *z* (beamGeometry.js iShape),
    // so the writer's LocalY must be the FLANGE direction: LocalY = web x axis
    // (then local z = axis x LocalY = web).
    public static double[] ResolveLocalY(SteelMember b, ref int defaulted)
    {
        double[] w = WebDir(b, ref defaulted);
        double yx = w[0], yy = w[1], yz = w[2], ax = w[3], ay = w[4], az = w[5];
        return new double[] { yy * az - yz * ay, yz * ax - yx * az, yx * ay - yy * ax };
    }

    // 12-step ramp (blue -> green -> yellow -> orange -> red); same as the pipe bridge.
    static readonly string[] Ramp = {
        "#3b4cc0", "#4f7fd8", "#6fa8e6", "#8fd0d8", "#a5dca0", "#c9e35a",
        "#f2e34a", "#f6be2e", "#f39221", "#e8641c", "#d13a1f", "#b40426" };
    public static string SizeColor(int i, int n)
    {
        if (n <= 1) return Ramp[6];
        int idx = (int)Math.Round((double)i * (Ramp.Length - 1) / Math.Max(n - 1, 1));
        return Ramp[Math.Min(Math.Max(idx, 0), Ramp.Length - 1)];
    }

    public static string StaadName(string sectionName)
    {
        var chars = sectionName.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
        return new string(chars).ToUpperInvariant();
    }

    // file units per meter
    public static double LengthScale(string unit)
    {
        switch ((unit ?? "m").ToLowerInvariant())
        {
            case "m": return 1.0;
            case "mm": return 1000.0;
            case "in": return 1.0 / 0.0254;
            case "ft": return 1.0 / 0.3048;
            default: throw new Exception("unknown length unit '" + unit + "'.");
        }
    }

    public static string CoordKey(Vec3 p, double round)
    {
        return string.Format(CultureInfo.InvariantCulture, "{0}|{1}|{2}",
            Math.Round(p.X / round), Math.Round(p.Y / round), Math.Round(p.Z / round));
    }

    public static int NodeFor(Vec3 p, Vec3 off, double scale, double round, Dictionary<string, int> byKey,
                              Dictionary<int, Node> nodes, ref int next)
    {
        string key = CoordKey(p, round);
        int id;
        if (byKey.TryGetValue(key, out id)) return id;
        id = next++;
        byKey[key] = id;
        var n = new Node();
        n.id = id;
        n.xyz.X = (float)((p.X - off.X) * scale);
        n.xyz.Y = (float)((p.Y - off.Y) * scale);
        n.xyz.Z = (float)((p.Z - off.Z) * scale);
        nodes[id] = n;
        return id;
    }

    public static void AddTo<K>(Dictionary<K, List<uint>> map, K key, uint id)
    {
        List<uint> list;
        if (!map.TryGetValue(key, out list)) { list = new List<uint>(); map[key] = list; }
        list.Add(id);
    }
}
