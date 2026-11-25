using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

// Same colour, not connected (user 2026-11-25): an offshoot coloured as its main flew off in SAP while the main stood.
// System names come from the source parts (touching or within the proximity test = one system), SAP connects only
// frames that share a joint. On the final clean model (after cuts, drops, bridges, penetrations, supports): per main
// name, the connected piece (through ducts and links) with the most elements of that name is the main; every other
// element carrying that main's name gets DISCONNECTED_FROM_MAIN, and the piece's gap to the main is reported (its
// joints to the main's centrelines). Fragments ("<name> fragments") are disconnected by definition and not flagged.
// frames.csv Flags -> SAP FLAG_DISCONNECTED_FROM_MAIN; viewer group "Flag: DISCONNECTED_FROM_MAIN".
public static class DuctConnectivity
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public const string Flag = "DISCONNECTED_FROM_MAIN";

    public static string Apply(List<CEdge> clean, Dictionary<int, double[]> P, DuctSystems dsys)
    {
        var par = new Dictionary<int, int>();
        Func<int, int> find = null;
        find = x => { int p; if (!par.TryGetValue(x, out p)) { par[x] = x; return x; } if (p == x) return x; int r = find(p); par[x] = r; return r; };
        foreach (CEdge ce in clean) { if (ce.Hanger) continue; int a = find(ce.A), b = find(ce.B); if (a != b) par[Math.Max(a, b)] = Math.Min(a, b); }
        Func<CEdge, string> sysOf = ce => ce.Src.Count > 0 || ce.SysName != "" ? DuctGeom.SystemOf(ce) : "";
        // per main name: elements per piece
        var byName = new Dictionary<string, Dictionary<int, List<CEdge>>>(StringComparer.Ordinal);
        foreach (CEdge ce in clean)
        {
            if (ce.Hanger || ce.Rigid) continue;
            string n = sysOf(ce);
            if (n == "" || dsys.SystemRole(n) != "main") continue;
            Dictionary<int, List<CEdge>> pcs; if (!byName.TryGetValue(n, out pcs)) byName[n] = pcs = new Dictionary<int, List<CEdge>>();
            int r = find(ce.A); List<CEdge> l; if (!pcs.TryGetValue(r, out l)) pcs[r] = l = new List<CEdge>(); l.Add(ce);
        }
        var lines = new List<string>(); int flagged = 0, pieces = 0;
        foreach (KeyValuePair<string, Dictionary<int, List<CEdge>>> kv in byName.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (kv.Value.Count < 2) continue;
            int mainRoot = kv.Value.OrderByDescending(p => p.Value.Count).First().Key;
            // the main piece's centrelines (every element in it, any name: the trunk may carry links / other parts)
            var mainSegs = clean.Where(ce => !ce.Hanger && find(ce.A) == mainRoot).Select(ce => new[] { P[ce.A], P[ce.B] }).ToList();
            foreach (KeyValuePair<int, List<CEdge>> pc in kv.Value.Where(p => p.Key != mainRoot).OrderByDescending(p => p.Value.Count))
            {
                pieces++;
                // the whole piece (all elements in it), flagged; the gap from its joints to the main's centrelines
                var pieceEdges = clean.Where(ce => !ce.Hanger && find(ce.A) == pc.Key).ToList();
                foreach (CEdge ce in pieceEdges) { if (!ce.Rigid && sysOf(ce) == kv.Key) { ce.Flags.Add(Flag); flagged++; } }
                double best = double.MaxValue; int at = -1;
                foreach (int j in pieceEdges.SelectMany(ce => new[] { ce.A, ce.B }).Distinct())
                    foreach (double[][] s in mainSegs)
                    {
                        double t, lat; DuctMath.Project(s[0], s[1], P[j], out t, out lat);
                        double[] q = DuctMath.Lerp(s[0], s[1], Math.Max(0, Math.Min(1, t)));
                        double d = Math.Sqrt(DuctMath.Dist2(q, P[j]));
                        if (d < best) { best = d; at = j; }
                    }
                double len = pieceEdges.Where(ce => !ce.Rigid).Sum(ce => DuctMath.Len(P[ce.A], P[ce.B]));
                lines.Add(string.Format(Inv, "  {0}: {1} elements ({2:0.#} ft), {3:0.#} in from the main at its joint N{4}", kv.Key, pc.Value.Count, len, best * 12, at));
            }
        }
        if (pieces == 0) return "connectivity: every main is one connected piece (no " + Flag + ")";
        return string.Format(Inv, "NEW disconnected from main ({0}): {1} elements in {2} pieces carry a main's name but share no joint with it (same colour, separate in SAP); nearest gaps below",
            Flag, flagged, pieces) + "\n" + string.Join("\n", lines.OrderBy(x => x, StringComparer.Ordinal).Take(40).ToArray()) + (lines.Count > 40 ? "\n  ..." : "");
    }
}
