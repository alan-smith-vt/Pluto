using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

// PipeReview  --  the checks a pipe model gets before CAESAR sees it, each with the nodes and elements it is about, for
// the viewer's Checks panel (sidecar section `review`, written by CaesarToPluto through PipeSidecar). C# 5, one global
// batch: every type is prefixed Pipe.
//
// Levels: error (CAESAR's error checker stops on it), warning (look at it), notice (written from CAESAR's documentation,
// no CAESAR-written file has shown it yet: check it after the import), info (worth seeing, often intended).
// Checks on the model (Check):
//   unsupported     a connected piece (elements, CNODE restraints and joints) with no restraint, hanger, imposed
//                   displacement or nozzle to ground: unstable
//   tee-no-sif      three or more elements meet at a node with no SIF / tee entry there
//   free-end        a node on one element end only, held by nothing (a vent, a drain, or a missing support)
//   element-self    an element from a node to itself
//   element-zero    an element with no length
//   element-twice   two elements between the same two nodes
//   off-model       a restraint, SIF, force or imposed displacement at a node on no element
//   cnode-off-model a restraint whose CNODE is on no element (the connection goes nowhere)
//   bend-corner     a bend drawn as a corner (CAESAR's geometry rules leave no room, or no radius)
//   bend-room       a bend's tangent reaches past the start of its element (it overlaps the run before it)
//   bend-code       a bend radius code CAESAR reads as no radius
//   node-twice      a node on a bend's curve that is also on another bend's, or an element end: two points, one number
//   uniform-mixed   the uniform loads mix g's and force per length (CAESAR's error checker warns: 246E, 247E)
// plus CaesarNeutralWriter.NoticeItems (layouts and codes from the documentation) and any messages the caller adds
// (the build's warnings, the preview's own).

public class PipeReviewItem
{
    public string Level = "warning";              // error | warning | notice | info
    public string Code = "";
    public string Text = "";
    public readonly List<int> Nodes = new List<int>();
    public readonly List<int> Elements = new List<int>();   // CAESAR element numbers (1-based, input order)

    public PipeReviewItem() { }
    public PipeReviewItem(string level, string code, string text) { Level = level; Code = code; Text = text; }

    public PipeReviewItem WithNodes(IEnumerable<int> nodes) { foreach (int n in nodes) if (n > 0 && !Nodes.Contains(n)) Nodes.Add(n); return this; }
    public PipeReviewItem WithElements(IEnumerable<int> els) { foreach (int e in els) if (e > 0 && !Elements.Contains(e)) Elements.Add(e); return this; }
}

public static class PipeReview
{
    public const int ListMax = 400;              // nodes / elements kept per item (a piece of a big job lists its first ones)

    // cm: the neutral file's reading of the model, for what the model carries without reading (MISCEL_1 hangers and
    // nozzles: a hanger holds its node up, or ties it to its CNODE; a nozzle ties its node to the vessel node, or to
    // ground without one); may be null. g: the drawn geometry (bend checks); may be null.
    public static List<PipeReviewItem> Check(PipeModel m, CaesarPipeGraph g, CaesarModel cm)
    {
        var items = new List<PipeReviewItem>();
        if (m == null) return items;
        var ci = CultureInfo.InvariantCulture;
        int ne = m.Elements.Count;

        // ---- nodes on elements, ends per node ----
        var ends = new Dictionary<int, int>();
        var onElement = new HashSet<int>();
        var elementsAt = new Dictionary<int, List<int>>();
        for (int i = 0; i < ne; i++)
        {
            var e = m.Elements[i];
            foreach (int n in new[] { e.From, e.To })
            {
                if (n <= 0) continue;
                int c; ends.TryGetValue(n, out c); ends[n] = c + 1;
                onElement.Add(n);
                List<int> l;
                if (!elementsAt.TryGetValue(n, out l)) elementsAt[n] = l = new List<int>();
                if (!l.Contains(i + 1)) l.Add(i + 1);
            }
            if (e.Bend != null) foreach (var an in e.Bend.AngleNodes) if (an.Length > 1 && an[1] > 0) onElement.Add((int)Math.Round(an[1]));
        }

        // ---- connectivity: elements (and their bend nodes), CNODE restraints, joints ----
        var uf = new Dictionary<int, int>();
        Func<int, int> find = null;
        find = x => { int p; if (!uf.TryGetValue(x, out p)) { uf[x] = x; return x; } if (p == x) return x; int r = find(p); uf[x] = r; return r; };
        Action<int, int> join = (a, b) => { int ra = find(a), rb = find(b); if (ra != rb) uf[ra] = rb; };
        foreach (var e in m.Elements)
        {
            if (e.From > 0 && e.To > 0) join(e.From, e.To);
            if (e.Bend != null && e.From > 0) foreach (var an in e.Bend.AngleNodes) if (an.Length > 1 && an[1] > 0) join(e.From, (int)Math.Round(an[1]));
        }
        var ground = new HashSet<int>();
        var connected = new HashSet<int>();          // nodes another node reaches through a CNODE or a joint
        List<PipeModelRestraint> restraints;
        try { restraints = CaesarNeutralWriter.Restraints(m); }
        catch (Exception) { restraints = new List<PipeModelRestraint>(m.Restraints); }
        foreach (var r in restraints)
        {
            if (r.Node <= 0) continue;
            if (r.CNode > 0) { join(r.Node, r.CNode); connected.Add(r.Node); connected.Add(r.CNode); }
            else ground.Add(r.Node);
        }
        foreach (var j in m.Joints) if (j.Node > 0 && j.CNode > 0) { join(j.Node, j.CNode); connected.Add(j.Node); connected.Add(j.CNode); }
        foreach (var d in m.Displacements)
            if (d.Node > 0 && Enumerable.Range(0, 9).Any(v => d.Used(v))) ground.Add(d.Node);
        if (cm != null)
        {
            foreach (var h in cm.Hangers)
            {
                if (h.Node <= 0) continue;
                if (h.CNode > 0) { join(h.Node, h.CNode); connected.Add(h.Node); connected.Add(h.CNode); }
                else ground.Add(h.Node);
            }
            foreach (var z in cm.Nozzles)
            {
                if (z.Node <= 0) continue;
                if (z.VesselNode > 0) { join(z.Node, z.VesselNode); connected.Add(z.Node); connected.Add(z.VesselNode); }
                else ground.Add(z.Node);
            }
        }

        // unsupported pieces
        var pieceNodes = new Dictionary<int, List<int>>();
        foreach (int n in onElement) { int r = find(n); List<int> l; if (!pieceNodes.TryGetValue(r, out l)) pieceNodes[r] = l = new List<int>(); l.Add(n); }
        var groundedRoots = new HashSet<int>(ground.Select(n => find(n)));
        foreach (var kv in pieceNodes.OrderBy(k => k.Value.Min()))
        {
            if (groundedRoots.Contains(kv.Key)) continue;
            var nodes = kv.Value.OrderBy(n => n).ToList();
            var els = new List<int>();
            for (int i = 0; i < ne; i++) { var e = m.Elements[i]; if (e.From > 0 && find(e.From) == kv.Key) els.Add(i + 1); }
            string what = pieceNodes.Count == 1 ? "the model" : "the piece with nodes " + nodes.First() + " .. " + nodes.Last() + " (" + els.Count + " element" + (els.Count == 1 ? "" : "s") + ")";
            items.Add(new PipeReviewItem("error", "unsupported",
                "nothing holds " + what + " to ground: no restraint, hanger or imposed displacement on it; CAESAR stops on it as unstable")
                .WithNodes(nodes.Take(ListMax)).WithElements(els.Take(ListMax)));
        }

        // tees without a SIF entry
        var sifNodes = new HashSet<int>();
        List<PipeModelSif> sifs;
        try { sifs = CaesarNeutralWriter.Sifs(m); }
        catch (Exception) { sifs = new List<PipeModelSif>(m.Sifs); }
        foreach (var s in sifs) if (s.Node > 0) sifNodes.Add(s.Node);
        foreach (var kv in ends.Where(k => k.Value >= 3 && !sifNodes.Contains(k.Key)).OrderBy(k => k.Key))
            items.Add(new PipeReviewItem("warning", "tee-no-sif",
                kv.Value + " elements meet at node " + kv.Key + " with no SIF / tee entry there: CAESAR applies no stress intensification at this branch (a welding tee is SIF type 3)")
                .WithNodes(new[] { kv.Key }).WithElements(elementsAt[kv.Key]));

        // free ends
        var held = new HashSet<int>(ground);
        held.UnionWith(connected);
        var free = ends.Where(k => k.Value == 1 && !held.Contains(k.Key)).Select(k => k.Key).OrderBy(n => n).ToList();
        if (free.Count > 0)
            items.Add(new PipeReviewItem("info", "free-end",
                free.Count + " free end" + (free.Count == 1 ? "" : "s") + " (one element, nothing holding it): node" + (free.Count == 1 ? " " : "s ") +
                string.Join(", ", free.Take(20).Select(n => n.ToString(ci)).ToArray()) + (free.Count > 20 ? ", ..." : "") +
                ". An open end, a vent or a drain is fine; a missing support or a connection to equipment is not")
                .WithNodes(free.Take(ListMax)).WithElements(free.Take(ListMax).Select(n => elementsAt[n][0])));

        // elements
        var pairs = new Dictionary<string, int>();
        for (int i = 0; i < ne; i++)
        {
            var e = m.Elements[i];
            string name = e.From + "-" + e.To;
            if (e.From == e.To)
            {
                items.Add(new PipeReviewItem("error", "element-self", "element " + (i + 1) + " runs from node " + e.From + " to itself").WithNodes(new[] { e.From }).WithElements(new[] { i + 1 }));
                continue;
            }
            if (Math.Sqrt(e.Dx * e.Dx + e.Dy * e.Dy + e.Dz * e.Dz) < 1e-9)
                items.Add(new PipeReviewItem("warning", "element-zero", "element " + name + " has no length (DX = DY = DZ = 0)").WithNodes(new[] { e.From, e.To }).WithElements(new[] { i + 1 }));
            string key = Math.Min(e.From, e.To) + "-" + Math.Max(e.From, e.To);
            int first;
            if (pairs.TryGetValue(key, out first))
                items.Add(new PipeReviewItem("warning", "element-twice", "elements " + first + " and " + (i + 1) + " both run between nodes " + e.From + " and " + e.To)
                    .WithNodes(new[] { e.From, e.To }).WithElements(new[] { first, i + 1 }));
            else pairs[key] = i + 1;
        }

        // things at nodes on no element
        var refsOff = new SortedDictionary<int, List<string>>();
        Action<int, string> off = (n, what) =>
        {
            if (n <= 0 || onElement.Contains(n) || connected.Contains(n)) return;
            List<string> l;
            if (!refsOff.TryGetValue(n, out l)) refsOff[n] = l = new List<string>();
            if (!l.Contains(what)) l.Add(what);
        };
        foreach (var r in m.Restraints) off(r.Node, "a restraint " + r.Type);
        foreach (var s in m.Sifs) off(s.Node, "a SIF / tee entry");
        foreach (var x in m.NodeLoads) off(x.Node, x.IsDisplacement ? "an imposed displacement" : "a force / moment");
        foreach (var kv in refsOff)
            items.Add(new PipeReviewItem("error", "off-model", "node " + kv.Key + " has " + string.Join(", ", kv.Value.ToArray()) + " but is on no element and no connection reaches it").WithNodes(new[] { kv.Key }));
        foreach (var r in restraints.Where(x => x.CNode > 0 && !onElement.Contains(x.CNode)).GroupBy(x => x.CNode))
        {
            if (ground.Contains(r.Key)) continue;            // the CNODE is restrained itself: a support on a frame
            var at = r.Select(x => x.Node).Distinct().OrderBy(n => n).ToList();
            if (at.All(n => !onElement.Contains(n))) continue;
            items.Add(new PipeReviewItem("warning", "cnode-off-model",
                "the restraint" + (at.Count == 1 ? "" : "s") + " at node " + string.Join(", ", at.Select(n => n.ToString(ci)).ToArray()) + " connect" + (at.Count == 1 ? "s" : "") +
                " to node " + r.Key + ", which is on no element and has no restraint of its own: a CNODE tied to nothing else holds nothing, so check it is meant (e.g. a support to be modelled later)")
                .WithNodes(at));
        }

        // bends
        if (g != null)
            foreach (var b in g.Bends)
            {
                if (b.ElementIndex < 0 || b.ElementIndex >= ne) continue;
                var e = m.Elements[b.ElementIndex];
                string at = "the bend at node " + e.To + " (element " + e.From + "-" + e.To + ")";
                if (b.Straight)
                    items.Add(new PipeReviewItem("warning", "bend-corner", at + " is drawn as a corner: " + b.Reason).WithNodes(new[] { e.To }).WithElements(new[] { b.ElementIndex + 1 }));
                else if (b.LeadLength < 0 && -b.LeadLength > 0.01 * Math.Sqrt(e.Dx * e.Dx + e.Dy * e.Dy + e.Dz * e.Dz))   // under 1%: CAESAR's own default
                    items.Add(new PipeReviewItem("warning", "bend-room", string.Format(ci,
                        "{0} needs a straight of {1:0.###} before it (radius x tan(angle / 2)) and its element gives {2:0.###}: the bend reaches back past node {3}",
                        at, b.T, b.T + b.LeadLength, e.From)).WithNodes(new[] { e.From, e.To }).WithElements(new[] { b.ElementIndex + 1 }));
            }
        for (int i = 0; i < ne; i++)
        {
            var e = m.Elements[i];
            if (e.Bend == null) continue;
            int code = PipeModelBend.RadiusCode(e.Bend.Radius);
            if (PipeModelBend.NoRadiusCode(code))
                items.Add(new PipeReviewItem("warning", "bend-code", string.Format(ci,
                    "the bend at node {0} has radius {1}, CAESAR's code {2}, which CAESAR reads as no radius (0): give LR, SR, 3D, 5D or a length", e.To, e.Bend.Radius, code))
                    .WithNodes(new[] { e.To }).WithElements(new[] { i + 1 }));
        }

        // a node on a bend's curve is a point of its own: on two bends, or also an element end, it names two points
        var onBends = new SortedDictionary<int, List<int>>();
        for (int i = 0; i < ne; i++)
        {
            var e = m.Elements[i];
            if (e.Bend == null) continue;
            foreach (var an in e.Bend.AngleNodes)
            {
                if (an.Length < 2 || an[1] <= 0) continue;
                int n = (int)Math.Round(an[1]);
                List<int> l;
                if (!onBends.TryGetValue(n, out l)) onBends[n] = l = new List<int>();
                l.Add(i + 1);
            }
        }
        Func<int, string> en = k => m.Elements[k - 1].From + "-" + m.Elements[k - 1].To;
        foreach (var kv in onBends)
        {
            var bends = kv.Value;
            List<int> endOf;
            elementsAt.TryGetValue(kv.Key, out endOf);
            if (bends.Count < 2 && endOf == null) continue;
            var distinct = bends.Distinct().ToList();
            string where = distinct.Count > 1 ? "on the curves of " + distinct.Count + " bends (elements " + string.Join(", ", distinct.Select(en).ToArray()) + ")"
                         : bends.Count > 1 ? "twice on the curve of the bend on element " + en(bends[0])
                         : "on the curve of the bend on element " + en(bends[0]);
            if (endOf != null) where += " and is also an end of element" + (endOf.Count == 1 ? " " : "s ") + string.Join(", ", endOf.Select(en).ToArray());
            items.Add(new PipeReviewItem("error", "node-twice",
                "node " + kv.Key + " is " + where + ": a node number names one point, so give each a number of its own (CAESAR stops on it: a node on the curvature of two different bends)")
                .WithNodes(new[] { kv.Key }).WithElements(distinct.Concat(endOf ?? new List<int>())));
        }

        // uniform loads of both kinds, g's and force per length: CAESAR's error checker warns for an element holding both
        // (246E) and for a vector used both ways (247E), and it reads every flag, so a model that mixes them warns
        var gUse = new SortedDictionary<int, List<int>>(); var lUse = new SortedDictionary<int, List<int>>();   // vector -> elements
        for (int i = 0; i < ne; i++)
        {
            var u = m.Elements[i].Uniform;
            if (PipeModelUniform.IsNone(u)) continue;
            for (int v = 0; v < 9; v++)
            {
                if (!u.Used(v)) continue;
                var d = u.G[v] ? gUse : lUse;
                List<int> l;
                if (!d.TryGetValue(v, out l)) d[v] = l = new List<int>();
                l.Add(i + 1);
            }
        }
        if (gUse.Count > 0 && lUse.Count > 0)
        {
            Func<SortedDictionary<int, List<int>>, string> list = d => string.Join(", ", d.Select(kv => "U" + (kv.Key + 1) + " on " + kv.Value.Count + " element" + (kv.Value.Count == 1 ? "" : "s")).ToArray());
            bool gFew = gUse.Sum(kv => kv.Value.Count) <= lUse.Sum(kv => kv.Value.Count);
            var few = (gFew ? gUse : lUse).SelectMany(kv => kv.Value).Distinct().OrderBy(k => k).ToList();
            items.Add(new PipeReviewItem("warning", "uniform-mixed",
                "the uniform loads mix g's (" + list(gUse) + ") and force per length (" + list(lUse) + "): CAESAR warns for every element holding both kinds and every vector used both ways, " +
                "as it reads each block's flag for every vector; one kind throughout avoids it (the elements listed carry the " + (gFew ? "g's" : "force per length") + ")")
                .WithElements(few.Take(ListMax)));
        }

        items.AddRange(CaesarNeutralWriter.NoticeItems(m));
        return items;
    }

    // "error" first; then the order they were found
    public static int Rank(string level)
    {
        switch ((level ?? "").ToLowerInvariant())
        {
            case "error": return 0;
            case "warning": return 1;
            case "notice": return 2;
            default: return 3;
        }
    }

    public static string Summary(IEnumerable<PipeReviewItem> items)
    {
        var l = items.ToList();
        Func<string, int> n = lv => l.Count(x => string.Equals(x.Level, lv, StringComparison.OrdinalIgnoreCase));
        return n("error") + " error(s), " + n("warning") + " warning(s), " + n("notice") + " notice(s), " + n("info") + " info";
    }
}
