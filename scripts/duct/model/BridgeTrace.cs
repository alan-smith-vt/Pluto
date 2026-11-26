using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

// Bridge trace (user 2026-11-25: bridged fragments still showed as fragments; "stop guessing"). Runs at the very end
// of the clean model, after every step that can change a name (resolve, penetrations, supports), on the elements that
// are written to the viewer and frames.csv. Per manual bridge, one line:
//   both sides: the elements holding the stored parts, their EARLY name (from the source parts) and their FINAL name
//   (what the viewer and SAP get); whether the two sides are one connected piece now; and a verdict naming the rule:
//     OK                    both sides end in the main
//     NOT CONNECTED         the two sides are not one piece (the bridge did not join them)
//     PIECE IS NOT THE MAIN one piece, but another piece holds more of that system (what was bridged to is not the main)
//     DIFFERENT SYSTEMS     the sides carry different system names: a fragment joins only a main of its own name
//     NAME LOST             one piece, it is the main, yet a side's final name is a fragment (a step after the resolve)
public static class BridgeTrace
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    static List<string> PartsOf(CEdge e)
    {
        return e.Src.Count > 0 ? e.Src.Select(x => x.Id).ToList() : e.NavisId != "" ? new List<string> { "N:" + e.NavisId } : new List<string>();
    }
    static string Early(CEdge ce) { return ce.Src.Count > 0 ? ce.Src[0].SysName : ce.SysName; }
    static string Base(string n) { return n != null && n.EndsWith(" fragments") ? n.Substring(0, n.Length - 10) : n ?? ""; }

    public static string Run(List<CEdge> clean, List<ManualBridge> bridges)
    {
        if (bridges == null || bridges.Count == 0) return "";
        var par = new Dictionary<int, int>();
        Func<int, int> find = null;
        find = x => { int p; if (!par.TryGetValue(x, out p)) { par[x] = x; return x; } if (p == x) return x; int r = find(p); par[x] = r; return r; };
        foreach (CEdge ce in clean) { if (ce.Hanger) continue; int a = find(ce.A), b = find(ce.B); if (a != b) par[Math.Max(a, b)] = Math.Min(a, b); }
        // per base name: element count per piece -> the piece with most is the main (the resolve's rule)
        var count = new Dictionary<string, Dictionary<int, int>>(StringComparer.Ordinal);
        foreach (CEdge ce in clean)
        {
            if (ce.Hanger || ce.Rigid) continue;
            string b = Base(Early(ce)); if (b == "") continue;
            Dictionary<int, int> d; if (!count.TryGetValue(b, out d)) count[b] = d = new Dictionary<int, int>();
            int r = find(ce.A); int c; d.TryGetValue(r, out c); d[r] = c + 1;
        }
        var lines = new List<string>();
        for (int i = 0; i < bridges.Count; i++)
        {
            ManualBridge br = bridges[i];
            var want = new Func<List<string>, List<CEdge>>(parts =>
            {
                var s = new HashSet<string>(parts, StringComparer.OrdinalIgnoreCase);
                return clean.Where(ce => !ce.Rigid && !ce.Hanger && PartsOf(ce).Any(s.Contains)).ToList();
            });
            List<CEdge> sa = want(br.A), sb = want(br.B);
            Func<List<CEdge>, string> desc = l => l.Count == 0 ? "not in the output"
                : string.Format(Inv, "{0} el, early {1}, final {2}", l.Count,
                    string.Join("/", l.Select(Early).Distinct().ToArray()), string.Join("/", l.Select(DuctGeom.SystemOf).Distinct().ToArray()));
            string verdict;
            if (sa.Count == 0 || sb.Count == 0) verdict = "A SIDE IS NOT IN THE OUTPUT";
            else
            {
                var ra = sa.Select(ce => find(ce.A)).Distinct().ToList(); var rb = sb.Select(ce => find(ce.A)).Distinct().ToList();
                bool one = ra.Intersect(rb).Any();
                string ba = Base(Early(sa[0])), bb = Base(Early(sb[0]));
                bool finalMainA = sa.All(ce => !DuctGeom.SystemOf(ce).EndsWith(" fragments")), finalMainB = sb.All(ce => !DuctGeom.SystemOf(ce).EndsWith(" fragments"));
                if (!one) verdict = "NOT CONNECTED: the two sides are separate pieces in the output (the bridge did not join them)";
                else if (ba != bb) verdict = string.Format(Inv, "DIFFERENT SYSTEMS: A is {0}, B is {1}; a fragment joins only a main of its own name", ba, bb);
                else
                {
                    int r = ra.Intersect(rb).First();
                    Dictionary<int, int> d; count.TryGetValue(ba, out d);
                    int here = d != null && d.ContainsKey(r) ? d[r] : 0, most = d != null ? d.Values.Max() : 0;
                    if (here < most) verdict = string.Format(Inv, "PIECE IS NOT THE MAIN: this piece holds {0} {1} elements, the main {2}: what was bridged to is not connected to the main", here, ba, most);
                    else if (!finalMainA || !finalMainB) verdict = "NAME LOST: one piece and it is the main, yet a side's final name is a fragment (a step after the resolve renamed it)";
                    else verdict = "OK: both sides end in the main " + ba;
                }
            }
            lines.Add(string.Format(Inv, "  bridge {0} ({1}): A {2} | B {3} -> {4}", i + 1, br.Mode, desc(sa), desc(sb), verdict));
        }
        return "bridge trace (the output as written: viewer, frames.csv, SAP):\n" + string.Join("\n", lines.ToArray());
    }
}
