using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

// Stability check from SAP's own results (user 2026-11-25: SAP runs with ill-conditioned / unstable warnings and
// pieces fly off to E+9 under self weight). Called by SapToPluto.Export when the results hold a DEAD case.
// A joint is unstable when it moves more than LimitIn (12 in) under DEAD. Unstable joints joined by a frame or a link
// form one piece; each piece is reported (largest movement first) with what it is made of, as hints to the cause:
// systems and steel, the supports in it (a guide-only straight run slides), steel hinges and free ends (a pendulum).
//   viewer   painted group "Unstable <n>: <max> (<k> frames)" per piece, and the glyph group "Check: UNSTABLE" (one
//            warn glyph per piece at its largest movement)
//   files    <out>.stability.txt (one block per piece); a line in the export summary
public static class SapStability
{
    public const double LimitIn = 12;
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    static double LimitIn_(string unit)
    {
        switch ((unit ?? "").Trim().ToLowerInvariant())
        {
            case "ft": return LimitIn / 12;
            case "m": return LimitIn * 0.0254;
            case "cm": return LimitIn * 2.54;
            case "mm": return LimitIn * 25.4;
            default: return LimitIn;   // in
        }
    }

    // disp: per node, global U1..U3 of the DEAD case; links: joint pairs; frameGroups / jointGroups: SAP group members
    public static string Check(Dictionary<int, double[]> disp, Dictionary<int, Node> nodes, Dictionary<int, RawViewerWriter.BeamMember> beams,
        List<uint[]> links, Dictionary<string, List<uint>> frameGroups, Dictionary<string, List<uint>> jointGroups,
        FeaturesSidecar sc, string outBase, string lengthUnit)
    {
        double lim = LimitIn_(lengthUnit);
        var mag = new Dictionary<int, double>();
        foreach (KeyValuePair<int, double[]> kv in disp)
        {
            double[] u = kv.Value;
            double m = Math.Sqrt(u[0] * u[0] + u[1] * u[1] + u[2] * u[2]);
            if (!double.IsNaN(m)) mag[kv.Key] = m;
        }
        var bad = new HashSet<int>(mag.Where(kv => kv.Value > lim).Select(kv => kv.Key));
        string path = outBase + ".stability.txt";
        if (bad.Count == 0)
        {
            File.WriteAllText(path, string.Format(Inv, "Stability check (DEAD): no joint moves more than {0:0.##} {1}. Largest: {2}\r\n", lim, lengthUnit,
                mag.Count > 0 ? mag.Values.Max().ToString("0.###", Inv) + " " + lengthUnit : "none"), new UTF8Encoding(false));
            return string.Format(Inv, "stability (DEAD): stable, no joint over {0:0.##} {1} (largest {2:0.###})", lim, lengthUnit, mag.Count > 0 ? mag.Values.Max() : 0);
        }
        // pieces: unstable joints joined by a frame or a link
        var adj = new Dictionary<int, List<int>>();
        Action<int, int> join = (a, b) =>
        {
            if (!bad.Contains(a) || !bad.Contains(b) || a == b) return;
            List<int> l; if (!adj.TryGetValue(a, out l)) adj[a] = l = new List<int>(); l.Add(b);
            if (!adj.TryGetValue(b, out l)) adj[b] = l = new List<int>(); l.Add(a);
        };
        foreach (RawViewerWriter.BeamMember b in beams.Values) join((int)b.NodeA, (int)b.NodeB);
        foreach (uint[] lk in links) join((int)lk[0], (int)lk[1]);
        var pieceOf = new Dictionary<int, int>(); var pieces = new List<List<int>>();
        foreach (int n in bad.OrderBy(x => x))
        {
            if (pieceOf.ContainsKey(n)) continue;
            var p = new List<int>(); var q = new Queue<int>(); q.Enqueue(n); pieceOf[n] = pieces.Count;
            while (q.Count > 0)
            {
                int a = q.Dequeue(); p.Add(a); List<int> l;
                if (adj.TryGetValue(a, out l)) foreach (int b in l) if (!pieceOf.ContainsKey(b)) { pieceOf[b] = pieces.Count; q.Enqueue(b); }
            }
            pieces.Add(p);
        }
        // membership lookups for the hints
        Func<string, HashSet<uint>> fset = g => { List<uint> l; return frameGroups.TryGetValue(g, out l) ? new HashSet<uint>(l) : new HashSet<uint>(); };
        Func<string, HashSet<uint>> jset = g => { List<uint> l; return jointGroups.TryGetValue(g, out l) ? new HashSet<uint>(l) : new HashSet<uint>(); };
        var sysOf = new Dictionary<uint, string>();
        foreach (KeyValuePair<string, List<uint>> kv in frameGroups) if (kv.Key.StartsWith("SYS_")) foreach (uint id in kv.Value) sysOf[id] = kv.Key.Substring(4);
        HashSet<uint> steel = fset("STEEL_ALL"), released = fset("STEEL_RELEASED");
        HashSet<uint> guides = jset("SUPPORT_GUIDE"), anchors = jset("SUPPORT_ANCHOR"), openFree = jset("STEEL_OPEN_FREE"), noSlab = jset("FLAG_NO_SLAB");

        var order = Enumerable.Range(0, pieces.Count).OrderByDescending(i => pieces[i].Max(n => mag[n])).ToList();
        var txt = new StringBuilder();
        txt.AppendLine(string.Format(Inv, "Stability check (DEAD): {0} joints move more than {1:0.##} {2}, in {3} pieces (largest movement first).", bad.Count, lim, lengthUnit, pieces.Count));
        txt.AppendLine("Hints: guides all along one line (no turn in plan) = the run slides / twists; steel hinged at one end and free");
        txt.AppendLine("at the other (open-free, NO_SLAB) = a pendulum; a piece with no support at all = it hangs on nothing.");
        txt.AppendLine();
        var glyphs = new List<FeaturesSidecar.Anchor>();
        int shown = 0;
        foreach (int i in order)
        {
            List<int> p = pieces[i]; var pset = new HashSet<int>(p);
            int worst = p.OrderByDescending(n => mag[n]).First();
            var fr = beams.Where(kv => pset.Contains((int)kv.Value.NodeA) && pset.Contains((int)kv.Value.NodeB)).Select(kv => (uint)kv.Key).ToList();
            var bySys = fr.Where(id => sysOf.ContainsKey(id)).GroupBy(id => sysOf[id]).OrderByDescending(g => g.Count()).Select(g => g.Key + " " + g.Count()).ToList();
            int nSteel = fr.Count(steel.Contains), nRel = fr.Count(released.Contains);
            int nGuide = p.Count(n => guides.Contains((uint)n)), nAnchor = p.Count(n => anchors.Contains((uint)n));
            int nFree = p.Count(n => openFree.Contains((uint)n) || noSlab.Contains((uint)n));
            Node wn; nodes.TryGetValue(worst, out wn);
            string head = string.Format(Inv, "Unstable {0}: {1} {2} ({3} frames)", shown + 1, Fmt(mag[worst]), lengthUnit, fr.Count);
            txt.AppendLine(string.Format(Inv, "{0}; largest at joint {1}{2}", head, worst,
                wn != null ? string.Format(Inv, " ({0:0.#}, {1:0.#}, {2:0.#})", wn.xyz.X, wn.xyz.Y, wn.xyz.Z) : ""));
            txt.AppendLine(string.Format(Inv, "  ducts: {0}; steel frames {1} ({2} with a hinged end); supports in it: {3} guides, {4} anchors; free steel ends (open-free / NO_SLAB) {5}",
                bySys.Count > 0 ? string.Join(", ", bySys.ToArray()) : "none", nSteel, nRel, nGuide, nAnchor, nFree));
            string hint = nGuide + nAnchor == 0 && bySys.Count > 0 ? "a duct piece with no support in it: what holds it is outside the piece, through a hinge or a link"
                        : nFree > 0 && nSteel > 0 ? "steel free at one end: check the hinged end at the other (a pendulum)"
                        : nAnchor == 0 && nGuide > 0 ? "guides only: check that they do not all lie along one line"
                        : "";
            if (hint != "") txt.AppendLine("  likely: " + hint);
            if (fr.Count > 0)
            {
                FeaturesSidecar.Group g = sc.AddGroup(head, "#ff3b3b", "beams", fr, new[] { "sap", "check", "unstable" }, "UNSTABLE_" + (shown + 1));
                g.Hidden = false;
            }
            glyphs.Add(new FeaturesSidecar.Anchor { Domain = "nodes", Id = (uint)worst, Label = head });
            shown++;
        }
        sc.AddLookGroup("Check: UNSTABLE", "#ff3b3b",
            new FeaturesSidecar.Look { Glyph = "warn", Tier = "exception", Label = string.Format(Inv, "Unstable under DEAD (> {0:0.##} {1})", lim, lengthUnit), Section = "Checks" },
            glyphs, new[] { "sap", "check" });
        File.WriteAllText(path, txt.ToString(), new UTF8Encoding(false));
        return string.Format(Inv, "stability (DEAD): UNSTABLE, {0} joints over {1:0.##} {2} in {3} pieces (largest {4} {2}) -> {5}",
            bad.Count, lim, lengthUnit, pieces.Count, Fmt(mag[pieces[order[0]].OrderByDescending(n => mag[n]).First()]), Path.GetFileName(path));
    }

    static string Fmt(double v) { return v >= 1e5 ? v.ToString("0.0e+0", Inv) : v.ToString("0.#", Inv); }
}
