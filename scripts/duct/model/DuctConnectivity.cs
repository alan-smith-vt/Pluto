using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

// Main or fragment: ONE definition, the clean model's final connectivity (user 2026-11-25: "the definitions MUST be
// identical in every way"). The early naming (DuctSystems, from the source parts: Revit connections, touching ends,
// proximity) only names the parts; whether a piece is the main or a fragment is decided here, on the clean frames
// after every step that changes what is connected (gap closing, side links, Navisworks bridges, proximity links,
// cuts, manual drops, manual bridges): exactly what SAP is given.
//   Per main name X (a configured primary system): a piece holding a PINNED main of X ("X 2", DuctMains; user
//   2026-11-25: fragments bridged into one stayed fragments) is that pinned main: its X fragments take its name. Among
//   the other pieces, the one holding the most elements of X (X or X fragments) is the main: all of them are X. Every
//   other piece holding X elements is "X fragments". Pinned mains keep their own name. Hangers do not connect.
//   Flags: FRAGMENT_JOINED (named a fragment, connected to its main), SPLIT_FROM_MAIN (named the main, not connected to
//   it: its gap to the main is in match.txt). CEdge.SysFinal carries the result (DuctGeom.SystemOf reads it first).
public static class DuctConnectivity
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    const string Frag = " fragments";

    public static string Resolve(List<CEdge> clean, Dictionary<int, double[]> P, DuctSystems dsys)
    {
        return Resolve(clean, P, dsys, null);
    }

    // bridges: the manual bridges placed this run (ManualBridge.JointA / JointB): each is reported with where its two
    // joints ended up (user 2026-11-25: bridged fragments stayed fragments and nothing said why)
    public static string Resolve(List<CEdge> clean, Dictionary<int, double[]> P, DuctSystems dsys, List<ManualBridge> bridges)
    {
        var par = new Dictionary<int, int>();
        Func<int, int> find = null;
        find = x => { int p; if (!par.TryGetValue(x, out p)) { par[x] = x; return x; } if (p == x) return x; int r = find(p); par[x] = r; return r; };
        foreach (CEdge ce in clean) { if (ce.Hanger) continue; int a = find(ce.A), b = find(ce.B); if (a != b) par[Math.Max(a, b)] = Math.Min(a, b); }
        // base name of an element: a main name X for X / "X fragments"; null for anything else (other:, pinned mains, ...)
        Func<CEdge, string> baseOf = ce =>
        {
            if (ce.Hanger) return null;
            string n = ce.Src.Count > 0 ? ce.Src[0].SysName : ce.SysName;   // the naming's, not an earlier SysFinal
            if (string.IsNullOrEmpty(n)) return null;
            string b = n.EndsWith(Frag) ? n.Substring(0, n.Length - Frag.Length) : n, bn;
            return dsys.SystemRole(b) == "main" && dsys.IsPrimaryName(b, out bn) && bn == b ? b : null;
        };
        var byBase = new Dictionary<string, Dictionary<int, List<CEdge>>>(StringComparer.Ordinal);
        foreach (CEdge ce in clean)
        {
            string b = baseOf(ce); if (b == null) continue;
            Dictionary<int, List<CEdge>> pcs; if (!byBase.TryGetValue(b, out pcs)) byBase[b] = pcs = new Dictionary<int, List<CEdge>>();
            int r = find(ce.A); List<CEdge> l; if (!pcs.TryGetValue(r, out l)) pcs[r] = l = new List<CEdge>(); l.Add(ce);
        }
        // pinned mains ("X 2", DuctMains): per piece, the pinned name with most elements in it
        var pinnedIn = new Dictionary<int, Dictionary<string, int>>();
        foreach (CEdge ce in clean)
        {
            if (ce.Hanger || ce.Rigid) continue;
            string n = NameOf(ce), bn;
            if (string.IsNullOrEmpty(n) || n.EndsWith(Frag) || dsys.SystemRole(n) != "main" || !dsys.IsPrimaryName(n, out bn) || bn == n) continue;
            int r = find(ce.A); Dictionary<string, int> d; if (!pinnedIn.TryGetValue(r, out d)) pinnedIn[r] = d = new Dictionary<string, int>(StringComparer.Ordinal);
            int c; d.TryGetValue(n, out c); d[n] = c + 1;
        }
        Func<int, string, string> pinnedOf = (r, X) =>
        {
            Dictionary<string, int> d; string bn;
            if (!pinnedIn.TryGetValue(r, out d)) return null;
            var m = d.Where(kv => dsys.IsPrimaryName(kv.Key, out bn) && bn == X).OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).ToList();
            return m.Count > 0 ? m[0].Key : null;
        };
        int joined = 0, split = 0, fragPieces = 0;
        var splitLines = new List<string>(); var joinCount = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, Dictionary<int, List<CEdge>>> kv in byBase.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            string X = kv.Key;
            // the main: among the pieces with no pinned main of X, most elements; ties: more named X itself, then the lower root
            var cands = kv.Value.Where(p => pinnedOf(p.Key, X) == null).ToList();
            int mainRoot = cands.Count == 0 ? int.MinValue : cands.OrderByDescending(p => p.Value.Count(ce => !ce.Rigid))
                                   .ThenByDescending(p => p.Value.Count(ce => NameOf(ce) == X)).ThenBy(p => p.Key).First().Key;
            List<double[][]> mainSegs = null;
            foreach (KeyValuePair<int, List<CEdge>> pc in kv.Value)
            {
                string pin = pinnedOf(pc.Key, X);
                bool isMain = pc.Key == mainRoot || pin != null;
                if (!isMain) fragPieces++;
                int movedHere = 0;
                foreach (CEdge ce in pc.Value)
                {
                    string was = NameOf(ce), now = pin != null ? (was == X ? X : pin) : isMain ? X : X + Frag;
                    ce.SysFinal = now;
                    if (was == now) continue;
                    if (isMain) { ce.Flags.Add("FRAGMENT_JOINED"); joined++; int c; joinCount.TryGetValue(now, out c); joinCount[now] = c + 1; }
                    else { ce.Flags.Add("SPLIT_FROM_MAIN"); split++; movedHere++; }
                }
                if (movedHere == 0) continue;
                // a piece the naming called the main but that is not connected to it: its gap to the main
                if (mainSegs == null) mainSegs = clean.Where(ce => !ce.Hanger && find(ce.A) == mainRoot).Select(ce => new[] { P[ce.A], P[ce.B] }).ToList();
                double best = double.MaxValue; int at = -1;
                foreach (int j in pc.Value.SelectMany(ce => new[] { ce.A, ce.B }).Distinct())
                    foreach (double[][] s in mainSegs)
                    {
                        double t, lat; DuctMath.Project(s[0], s[1], P[j], out t, out lat);
                        double d = Math.Sqrt(DuctMath.Dist2(DuctMath.Lerp(s[0], s[1], Math.Max(0, Math.Min(1, t))), P[j]));
                        if (d < best) { best = d; at = j; }
                    }
                double len = pc.Value.Where(ce => !ce.Rigid).Sum(ce => DuctMath.Len(P[ce.A], P[ce.B]));
                splitLines.Add(string.Format(Inv, "  {0}: {1} elements ({2:0.#} ft) not connected to the main, now {0}{3}: {4:0.#} in from it at joint N{5}", X, movedHere, len, Frag, best * 12, at));
            }
        }
        if (byBase.Count == 0) return "fragments (final connectivity): no primary systems configured";
        // per placed bridge: the piece its joints are in, and what that piece is
        var mainOf = new Dictionary<int, string>();   // piece root -> main name, for the main pieces
        foreach (KeyValuePair<string, Dictionary<int, List<CEdge>>> kv in byBase)
        {
            var cs = kv.Value.Where(p => pinnedOf(p.Key, kv.Key) == null).ToList();   // the same choice as above
            if (cs.Count == 0) continue;
            int mr = cs.OrderByDescending(p => p.Value.Count(ce => !ce.Rigid)).ThenByDescending(p => p.Value.Count(ce => NameOf(ce) == kv.Key)).ThenBy(p => p.Key).First().Key;
            mainOf[mr] = kv.Key;
        }
        foreach (KeyValuePair<int, Dictionary<string, int>> kv in pinnedIn)
            if (!mainOf.ContainsKey(kv.Key)) mainOf[kv.Key] = kv.Value.OrderByDescending(x => x.Value).First().Key + " (pinned)";
        var bridgeLines = new List<string>();
        if (bridges != null)
            for (int i = 0; i < bridges.Count; i++)
            {
                ManualBridge b = bridges[i];
                if (b.JointA == 0) continue;
                int ra = find(b.JointA), rb = find(b.JointB);
                Func<int, string> what = r =>
                {
                    string m; if (mainOf.TryGetValue(r, out m)) return "the main " + m;
                    var names = clean.Where(ce => !ce.Hanger && !ce.Rigid && find(ce.A) == r).GroupBy(ce => DuctGeom.SystemOf(ce)).OrderByDescending(g => g.Count())
                                     .Select(g => g.Key + " " + g.Count()).ToList();
                    return "a piece that is not a main (" + (names.Count > 0 ? string.Join(", ", names.ToArray()) : "links only") + ")";
                };
                bridgeLines.Add(string.Format(Inv, "  bridge {0}: joined N{1} and N{2}: {3}", i + 1, b.JointA, b.JointB,
                    ra == rb ? "both in " + what(ra) : "NOT one piece: " + what(ra) + " / " + what(rb)));
            }
        return string.Format(Inv, "fragments (final connectivity, the model SAP gets): {0} pieces are fragments; {1} elements named a fragment are connected to their main and joined it (FRAGMENT_JOINED{2}); {3} elements named the main are not connected to it and are fragments now (SPLIT_FROM_MAIN)",
            fragPieces, joined, joinCount.Count > 0 ? ": " + string.Join(", ", joinCount.Select(k => k.Key + " " + k.Value).ToArray()) : "", split)
            + (splitLines.Count > 0 ? "\n" + string.Join("\n", splitLines.OrderBy(x => x, StringComparer.Ordinal).Take(40).ToArray()) + (splitLines.Count > 40 ? "\n  ..." : "") : "")
            + (bridgeLines.Count > 0 ? "\n" + string.Join("\n", bridgeLines.ToArray()) : "");
    }

    static string NameOf(CEdge ce) { return ce.Src.Count > 0 ? ce.Src[0].SysName : ce.SysName; }
}
