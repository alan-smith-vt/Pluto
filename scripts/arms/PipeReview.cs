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
// and the rules of CAESAR's error checker (CaesarRules; vault/arms/caesar-checks.md): node-range, corrosion-wall,
// temperature-alpha, displacement-restrained, loop-closure, bend-angle, bend-node-angle, corner-no-bend, intersection,
// sif-below-one, sif-at-bend, thin-wall, reducer-angle, expansion-joint(-thrust), rigid-weightless, uniform-repeated,
// hangers; element-zero is an error unless the element is a zero-length expansion joint
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
            if (Math.Sqrt(e.Dx * e.Dx + e.Dy * e.Dy + e.Dz * e.Dz) < 1e-9 && e.ExpJoint == null)
                items.Add(new PipeReviewItem("error", "element-zero", "element " + name + " has no length (DX = DY = DZ = 0): CAESAR needs a length on every element but a zero-length expansion joint")
                    .WithNodes(new[] { e.From, e.To }).WithElements(new[] { i + 1 }));
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

        CaesarRules(m, g, cm, items, restraints, sifs, ends, elementsAt, onBends);

        items.AddRange(CaesarNeutralWriter.NoticeItems(m));
        return items;
    }

    // ---- CAESAR's error checker: what it stops on, warns or notes about, as its documentation and its own messages
    // describe it (vault/arms/caesar-checks.md); limits are its configuration defaults (Geometry Directives)
    public const double MinBendAngle = 5, MaxBendAngle = 95, MinBendNodeAngle = 5;   // deg
    public const double LoopClosureIn = 1.0;      // in
    public const double AlphaTolerance = 0.05;     // a temperature entry below it is read as an expansion (in/in)
    public const int MaxNode = 8000000;           // node numbers 1 .. 8,000,000 (CAESAR 11.00 on)
    public const double ThinWall = 100;           // D/t above it: the code's SIF equations do not cover it
    public const double MaxReducerAlpha = 60;     // deg: the reducer SIF equation's limit
    public const double CornerTolDeg = 1;         // a change of direction this small is not reported

    static void CaesarRules(PipeModel m, CaesarPipeGraph g, CaesarModel cm, List<PipeReviewItem> items, List<PipeModelRestraint> restraints,
                            List<PipeModelSif> sifs, Dictionary<int, int> ends, Dictionary<int, List<int>> elementsAt, SortedDictionary<int, List<int>> onBends)
    {
        var ci = CultureInfo.InvariantCulture;
        int ne = m.Elements.Count;
        Func<int, string> en = k => m.Elements[k - 1].From + "-" + m.Elements[k - 1].To;
        Func<double, string> f1 = x => x.ToString("0.#", ci);
        double lenIn = m.Factor(0), diaIn = m.Factor(20), wallIn = m.Factor(21);   // job units per inch
        if (lenIn <= 0) lenIn = 1; if (diaIn <= 0) diaIn = 1; if (wallIn <= 0) wallIn = 1;
        Func<PipeModelElement, double> length = e => Math.Sqrt(e.Dx * e.Dx + e.Dy * e.Dy + e.Dz * e.Dz);
        Func<PipeModelElement, double> dt = e => e.Wall > 0 ? (e.Od / diaIn) / (e.Wall / wallIn) : 0;

        // nodes: CAESAR numbers them 1 .. 8,000,000
        var badNodes = new SortedSet<int>();
        Action<int, bool> node = (n, required) => { if (n > MaxNode || (required && n <= 0) || n < 0) badNodes.Add(n); };
        foreach (var e in m.Elements) { node(e.From, true); node(e.To, true); }
        foreach (var kv in onBends) node(kv.Key, true);
        foreach (var r in m.Restraints) { node(r.Node, true); node(r.CNode, false); }
        foreach (var s in m.Sifs) { node(s.Node, true); node(s.SurfaceNode, false); }
        foreach (var x in m.NodeLoads) node(x.Node, true);
        foreach (var j in m.Joints) { node(j.Node, true); node(j.CNode, true); }
        if (badNodes.Count > 0)
            items.Add(new PipeReviewItem("error", "node-range", "node number" + (badNodes.Count == 1 ? " " : "s ") + string.Join(", ", badNodes.Take(20).Select(n => n.ToString(ci)).ToArray()) +
                (badNodes.Count > 20 ? ", ..." : "") + ": CAESAR takes node numbers from 1 to 8,000,000").WithNodes(badNodes.Where(n => n > 0).Take(ListMax)));

        // corrosion as thick as the wall: CAESAR stops (nothing would be left of the pipe)
        var corroded = Enumerable.Range(0, ne).Where(i => m.Elements[i].Corrosion > 0 && m.Elements[i].Wall > 0 && m.Elements[i].Corrosion >= m.Elements[i].Wall).ToList();
        if (corroded.Count > 0)
        {
            var e0 = m.Elements[corroded[0]];
            items.Add(new PipeReviewItem("error", "corrosion-wall", string.Format(ci, "the corrosion allowance is as thick as the wall or thicker on {0} element{1} (from {2}: corrosion {3:0.####}, wall {4:0.####}): CAESAR stops on it",
                corroded.Count, corroded.Count == 1 ? "" : "s", en(corroded[0] + 1), e0.Corrosion, e0.Wall)).WithElements(corroded.Take(ListMax).Select(i => i + 1)));
        }

        // a temperature entry under CAESAR's alpha tolerance is read as a thermal expansion (length / length)
        var alpha = new List<int>(); string alphaWhat = null;
        for (int i = 0; i < ne; i++)
            for (int k = 0; k < 9; k++)
            {
                double t = m.Elements[i].T[k];
                if (t == 0 || Math.Abs(t) >= AlphaTolerance) continue;
                if (alpha.Count == 0 || alpha[alpha.Count - 1] != i + 1) alpha.Add(i + 1);
                if (alphaWhat == null) alphaWhat = "T" + (k + 1) + " = " + t.ToString("0.#####", ci) + " on " + en(i + 1);
            }
        if (alpha.Count > 0)
            items.Add(new PipeReviewItem("warning", "temperature-alpha", "a temperature under 0.05 in magnitude on " + alpha.Count + " element" + (alpha.Count == 1 ? "" : "s") + " (" + alphaWhat +
                "): CAESAR reads such an entry as a thermal expansion (length per length), not a temperature; a temperature this close to zero is better given as 0.1 or more")
                .WithElements(alpha.Take(ListMax)));

        // imposed displacements on a DOF a restraint already holds to ground: overspecified, CAESAR stops
        var heldAt = new Dictionary<int, bool[]>();
        foreach (var r in restraints)
        {
            var dofs = GroundDofs(r);
            if (dofs.Length == 0) continue;
            bool[] h;
            if (!heldAt.TryGetValue(r.Node, out h)) heldAt[r.Node] = h = new bool[6];
            foreach (int k in dofs) h[k] = true;
        }
        foreach (var d in m.Displacements)
        {
            bool[] h;
            if (!heldAt.TryGetValue(d.Node, out h)) continue;
            var both = Enumerable.Range(0, 6).Where(k => h[k] && Enumerable.Range(0, 9).Any(v => !double.IsNaN(d.Vectors[v][k]))).Select(k => PipeModelCodes.DisplacementDofs[k]).ToArray();
            if (both.Length > 0)
                items.Add(new PipeReviewItem("error", "displacement-restrained", "node " + d.Node + " has an imposed displacement in " + string.Join(", ", both) +
                    " where a restraint already holds it to ground: overspecified, CAESAR stops on it (an imposed displacement acts as the restraint in that direction; drop one)")
                    .WithNodes(new[] { d.Node }));
        }

        // loop closure: a node two paths of element deltas place more than 1 in apart is a fatal error
        var pos = new Dictionary<int, double[]>();
        var adj = new Dictionary<int, List<int>>();
        for (int i = 0; i < ne; i++)
            foreach (int n in new[] { m.Elements[i].From, m.Elements[i].To })
            {
                List<int> l;
                if (!adj.TryGetValue(n, out l)) adj[n] = l = new List<int>();
                l.Add(i);
            }
        var used = new bool[ne];
        var misses = new List<KeyValuePair<int, double>>();
        double tol = LoopClosureIn * lenIn;
        for (int s0 = 0; s0 < ne; s0++)
        {
            if (used[s0] || pos.ContainsKey(m.Elements[s0].From) && pos.ContainsKey(m.Elements[s0].To)) continue;
            if (!pos.ContainsKey(m.Elements[s0].From)) pos[m.Elements[s0].From] = new double[3];
            var queue = new Queue<int>();
            queue.Enqueue(m.Elements[s0].From);
            while (queue.Count > 0)
            {
                int n = queue.Dequeue();
                foreach (int i in adj[n])
                {
                    if (used[i]) continue;
                    used[i] = true;
                    var e = m.Elements[i];
                    double sg = e.From == n ? 1 : -1;
                    int other = e.From == n ? e.To : e.From;
                    var p = pos[n];
                    var q = new[] { p[0] + sg * e.Dx, p[1] + sg * e.Dy, p[2] + sg * e.Dz };
                    double[] at;
                    if (pos.TryGetValue(other, out at))
                    {
                        double miss = Math.Sqrt((at[0] - q[0]) * (at[0] - q[0]) + (at[1] - q[1]) * (at[1] - q[1]) + (at[2] - q[2]) * (at[2] - q[2]));
                        if (miss > tol) misses.Add(new KeyValuePair<int, double>(i + 1, miss));
                    }
                    else { pos[other] = q; queue.Enqueue(other); }
                }
            }
        }
        foreach (var kv in misses.Take(20))
            items.Add(new PipeReviewItem("error", "loop-closure", string.Format(ci,
                "element {0} closes a loop {1:0.###} in away from where the other path puts node {2}: CAESAR stops on a loop that misses by more than 1 in (its loop closure tolerance); check the lengths around the loop",
                en(kv.Key), kv.Value / lenIn, m.Elements[kv.Key - 1].To)).WithNodes(new[] { m.Elements[kv.Key - 1].From, m.Elements[kv.Key - 1].To }).WithElements(new[] { kv.Key }));

        // bends: CAESAR's angle limits, and the nodes on the curve kept 5 deg apart and off the ends
        if (g != null)
            foreach (var b in g.Bends)
            {
                if (b.ElementIndex < 0 || b.ElementIndex >= ne || b.LeavingElement < 0 || b.AngleDeg <= 0) continue;
                var e = m.Elements[b.ElementIndex];
                string at = "the bend at node " + e.To + " (element " + e.From + "-" + e.To + ")";
                if (b.AngleDeg < MinBendAngle || b.AngleDeg > MaxBendAngle)
                    items.Add(new PipeReviewItem("error", "bend-angle", string.Format(ci,
                        "{0} turns {1:0.##} deg: CAESAR takes bends of 5 to 95 deg (its configuration's limits){2}", at, b.AngleDeg,
                        b.AngleDeg > MaxBendAngle ? "; split it into two bends, or raise CAESAR's maximum bend angle" : "; a turn this small is better modelled as straight pipe"))
                        .WithNodes(new[] { e.To }).WithElements(new[] { b.ElementIndex + 1 }));
                var na = b.NodeAngles.Where(x => x.Length > 1).OrderBy(x => x[1]).ToList();
                var probs = new List<string>();
                for (int k = 0; k < na.Count; k++)
                {
                    int n = (int)Math.Round(na[k][0]);
                    double a = na[k][1];
                    if (a > 1e-6 && a < MinBendNodeAngle - 1e-9) probs.Add("node " + n + " at " + f1(a) + " deg is within 5 deg of the near end");
                    else if (a > b.AngleDeg + 1e-6) probs.Add("node " + n + " at " + f1(a) + " deg is past the far end (" + f1(b.AngleDeg) + " deg)");
                    else if (a > b.AngleDeg - MinBendNodeAngle + 1e-9) probs.Add("node " + n + " at " + f1(a) + " deg is within 5 deg of the far end (" + f1(b.AngleDeg) + " deg)");
                    if (k > 0 && a - na[k - 1][1] < MinBendNodeAngle - 1e-9 && (int)Math.Round(na[k - 1][0]) != n)
                        probs.Add("nodes " + (int)Math.Round(na[k - 1][0]) + " and " + n + " are " + f1(a - na[k - 1][1]) + " deg apart");
                }
                if (probs.Count > 0)
                    items.Add(new PipeReviewItem("error", "bend-node-angle", at + ": " + string.Join("; ", probs.ToArray()) +
                        ". CAESAR keeps the nodes on a bend at least 5 deg from each other and from the near and far ends (the near point itself, angle 0, and M are fine)")
                        .WithNodes(new[] { e.To }.Concat(na.Select(x => (int)Math.Round(x[0])))).WithElements(new[] { b.ElementIndex + 1 }));
            }

        // a change of direction with no bend or intersection: CAESAR warns
        var sifAt = new HashSet<int>(sifs.Select(s => s.Node));
        var corners = new List<KeyValuePair<int, double>>();
        foreach (var kv in ends.Where(k => k.Value == 2).OrderBy(k => k.Key))
        {
            int n = kv.Key;
            var els = elementsAt[n];
            if (els.Count != 2 || sifAt.Contains(n)) continue;
            var a = m.Elements[els[0] - 1]; var c = m.Elements[els[1] - 1];
            if ((a.Bend != null && a.To == n) || (c.Bend != null && c.To == n)) continue;
            double la = length(a), lc = length(c);
            if (la < 1e-9 || lc < 1e-9) continue;
            double sa = a.From == n ? 1 : -1, sc = c.From == n ? 1 : -1;   // both pointing away from the node
            double dot = (sa * a.Dx * sc * c.Dx + sa * a.Dy * sc * c.Dy + sa * a.Dz * sc * c.Dz) / (la * lc);
            double turn = 180 - Math.Acos(Math.Max(-1, Math.Min(1, dot))) * 180 / Math.PI;
            if (turn > CornerTolDeg) corners.Add(new KeyValuePair<int, double>(n, turn));
        }
        if (corners.Count > 0)
            items.Add(new PipeReviewItem("warning", "corner-no-bend", corners.Count + " change" + (corners.Count == 1 ? "" : "s") + " of direction with no bend or tee: at node" + (corners.Count == 1 ? " " : "s ") +
                string.Join(", ", corners.Take(12).Select(kv => kv.Key.ToString(ci) + " (" + f1(kv.Value) + " deg)").ToArray()) + (corners.Count > 12 ? ", ..." : "") +
                ". CAESAR warns about each; give the element before it a bend radius, unless a mitre or a rigid at equipment is meant")
                .WithNodes(corners.Take(ListMax).Select(kv => kv.Key)).WithElements(corners.Take(ListMax).SelectMany(kv => elementsAt[kv.Key])));

        // intersections (a SIF / tee entry of a tee type): three pipes, two in line
        foreach (var s in sifs.Where(x => IsTeeType(x.Type)).GroupBy(x => x.Node).Select(gr => gr.First()))
        {
            List<int> els;
            int count = elementsAt.TryGetValue(s.Node, out els) ? els.Count : 0;
            string what = null;
            if (count > 0 && count < 3) what = "only " + count + " pipe" + (count == 1 ? "" : "s") + " meet there: CAESAR makes assumptions for a partial intersection (not recommended)";
            else if (count > 3) what = count + " pipes meet there: CAESAR computes no SIF for more than three (it uses 1.0); model two tees a little apart, or give the SIFs";
            else if (count == 3)
            {
                var outs = els.Select(k => { var e = m.Elements[k - 1]; double l = length(e), sg = e.From == s.Node ? 1 : -1; return l > 0 ? new[] { sg * e.Dx / l, sg * e.Dy / l, sg * e.Dz / l } : null; }).ToList();
                bool inline = false;
                for (int x = 0; x < 3; x++) for (int y = x + 1; y < 3; y++)
                    if (outs[x] != null && outs[y] != null && outs[x][0] * outs[y][0] + outs[x][1] * outs[y][1] + outs[x][2] * outs[y][2] < -Math.Cos(CornerTolDeg * Math.PI / 180)) inline = true;
                if (!inline && outs.All(o => o != null)) what = "no two of its three pipes are in line: CAESAR's intersection SIFs assume a run and a branch";
            }
            if (what != null)
                items.Add(new PipeReviewItem("warning", "intersection", "the tee at node " + s.Node + ": " + what).WithNodes(new[] { s.Node }).WithElements(els ?? new List<int>()));
        }

        // user SIFs: below 1.0 CAESAR does not accept them; at a bend they apply only when its configuration allows it
        var bendNodes = new HashSet<int>(onBends.Keys);
        foreach (var e in m.Elements) if (e.Bend != null) bendNodes.Add(e.To);
        foreach (var s in sifs)
        {
            if ((s.SifIn > 0 && s.SifIn < 1) || (s.SifOut > 0 && s.SifOut < 1))
                items.Add(new PipeReviewItem("error", "sif-below-one", string.Format(ci, "the SIF at node {0} (in {1:0.###}, out {2:0.###}) is below 1.0: CAESAR does not accept a user SIF under 1", s.Node, s.SifIn, s.SifOut))
                    .WithNodes(new[] { s.Node }));
            else if (s.Type == 0 && (s.SifIn > 0 || s.SifOut > 0) && bendNodes.Contains(s.Node))
                items.Add(new PipeReviewItem("warning", "sif-at-bend", "a user SIF at node " + s.Node + ", on a bend: CAESAR uses it only when its configuration allows user SIFs at bends; otherwise the bend's code SIF applies")
                    .WithNodes(new[] { s.Node }));
        }

        // thin wall: CAESAR warns at bends and intersections with D/t over 100 (the code's SIFs do not cover them)
        var thin = new SortedDictionary<int, double>();
        for (int i = 0; i < ne; i++)
        {
            var e = m.Elements[i];
            if (e.Bend != null && dt(e) > ThinWall) thin[e.To] = Math.Max(dt(e), thin.ContainsKey(e.To) ? thin[e.To] : 0);
            if (e.Reducer != null && Math.Max(dt(e), e.Reducer.Wall2 > 0 ? (e.Reducer.Od2 / diaIn) / (e.Reducer.Wall2 / wallIn) : 0) > ThinWall)
                thin[e.To] = Math.Max(dt(e), thin.ContainsKey(e.To) ? thin[e.To] : 0);
        }
        foreach (var kv in ends.Where(k => k.Value >= 3 || sifs.Any(s => IsTeeType(s.Type) && s.Node == k.Key)))
        {
            double worst = elementsAt[kv.Key].Max(k => dt(m.Elements[k - 1]));
            if (worst > ThinWall) thin[kv.Key] = Math.Max(worst, thin.ContainsKey(kv.Key) ? thin[kv.Key] : 0);
        }
        if (thin.Count > 0)
            items.Add(new PipeReviewItem("warning", "thin-wall", "D/t over 100 at " + thin.Count + " bend, tee or reducer node" + (thin.Count == 1 ? "" : "s") + " (" +
                string.Join(", ", thin.Take(12).Select(kv => kv.Key.ToString(ci) + ": " + kv.Value.ToString("0", ci)).ToArray()) + (thin.Count > 12 ? ", ..." : "") +
                "): CAESAR warns that the code's SIF equations are not proven for such thin walls").WithNodes(thin.Keys.Take(ListMax)));

        // reducers: the SIF equation holds to a cone angle of 60 deg (CAESAR computes it when not given)
        for (int i = 0; i < ne; i++)
        {
            var e = m.Elements[i];
            if (e.Reducer == null || e.Reducer.Od2 <= 0) continue;
            double l = length(e), a = e.Reducer.Alpha;
            if (a <= 0 && l > 0) a = Math.Atan(0.5 * Math.Abs(e.Od - e.Reducer.Od2) / diaIn * lenIn / (0.6 * l)) * 180 / Math.PI;
            if (a > MaxReducerAlpha)
                items.Add(new PipeReviewItem("warning", "reducer-angle", string.Format(ci, "the reducer {0} has a cone angle of {1:0.#} deg{2}: the code's reducer SIF holds to 60 deg; lengthen it or give the angle",
                    en(i + 1), a, e.Reducer.Alpha > 0 ? "" : " (from its length, as CAESAR computes it)")).WithNodes(new[] { e.From, e.To }).WithElements(new[] { i + 1 }));
        }

        // expansion joints: the stiffnesses CAESAR asks for
        for (int i = 0; i < ne; i++)
        {
            var x = m.Elements[i].ExpJoint;
            if (x == null) continue;
            var e = m.Elements[i];
            bool zero = length(e) < 1e-9;
            var missing = new List<string>();
            if (x.AxialStiffness <= 0) missing.Add("axial");
            if (zero && x.TransverseStiffness <= 0) missing.Add("transverse");
            if (zero && x.BendingStiffness <= 0) missing.Add("bending");
            if (x.TorsionStiffness <= 0) missing.Add("torsion");
            string what = null;
            if (missing.Count > 0) what = "no " + string.Join(", ", missing.ToArray()) + " stiffness (CAESAR asks for every stiffness; for torsion, which vendors rarely give, a large value)";
            if (!zero && x.TransverseStiffness > 0 && x.BendingStiffness > 0) what = (what != null ? what + "; " : "") + "both transverse and bending stiffness: on a joint with length CAESAR computes one from the other, so leave one blank";
            if (!zero && x.TransverseStiffness <= 0 && x.BendingStiffness <= 0) what = (what != null ? what + "; " : "") + "neither transverse nor bending stiffness: give one (CAESAR computes the other)";
            if (what != null)
                items.Add(new PipeReviewItem("warning", "expansion-joint", "the expansion joint " + en(i + 1) + ": " + what).WithNodes(new[] { e.From, e.To }).WithElements(new[] { i + 1 }));
            if (x.EffectiveId <= 0 && e.P.Any(v => v > 0))
                items.Add(new PipeReviewItem("info", "expansion-joint-thrust", "the expansion joint " + en(i + 1) + " has no effective ID: CAESAR applies no pressure thrust (right for a tied joint)")
                    .WithElements(new[] { i + 1 }));
        }

        // a rigid element with no weight: CAESAR treats it as a weightless construction element
        var weightless = Enumerable.Range(0, ne).Where(i => m.Elements[i].Rigid != null && m.Elements[i].ExpJoint == null && m.Elements[i].Rigid.Weight == 0).ToList();
        if (weightless.Count > 0)
            items.Add(new PipeReviewItem("info", "rigid-weightless", weightless.Count + " rigid element" + (weightless.Count == 1 ? "" : "s") + " with no weight (from " + en(weightless[0] + 1) +
                "): CAESAR treats " + (weightless.Count == 1 ? "it as a construction element" : "them as construction elements") + ", adding no fluid or insulation weight; give the component's weight where there is one")
                .WithElements(weightless.Take(ListMax).Select(i => i + 1)));

        // the job's own uniform blocks repeating the load on 4 or more elements in a row: CAESAR notes it (166E)
        int runStart = -1;
        for (int i = 0; i <= ne; i++)
        {
            bool own = i < ne && m.Elements[i].Uniform != null && m.Elements[i].Uniform.Raw != null && !PipeModelUniform.IsNone(m.Elements[i].Uniform);
            bool same = own && i > 0 && runStart >= 0 && PipeModelUniform.Same(m.Elements[i].Uniform, m.Elements[i - 1].Uniform);
            if (same) continue;
            if (runStart >= 0 && i - runStart >= 4)
                items.Add(new PipeReviewItem("info", "uniform-repeated", "elements " + en(runStart + 1) + " to " + en(i) + " each define the same uniform load (as the job was written): CAESAR notes it, since a load carries on to the elements after it")
                    .WithElements(Enumerable.Range(runStart + 1, i - runStart)));
            runStart = own ? i : -1;
        }

        // spring hangers the job carries: CAESAR designs them before the analysis
        if (cm != null && cm.Hangers.Count > 0)
            items.Add(new PipeReviewItem("info", "hangers", cm.Hangers.Count + " spring hanger" + (cm.Hangers.Count == 1 ? "" : "s") + " (carried as written): CAESAR designs them first and asks for its hanger-design load cases (weight, then operating) before the others")
                .WithNodes(cm.Hangers.Select(h => h.Node).Where(n => n > 0).Take(ListMax)));
    }

    // CAESAR's SIF & tee types that are intersections (tees, olets: 1-6, 14-17); 7-13 are joints (welds, threads, flanges)
    public static bool IsTeeType(int t) { return (t >= 1 && t <= 6) || (t >= 14 && t <= 17); }

    // the DOFs (0..5: DX DY DZ RX RY RZ) a restraint holds to ground: anchors all, an axis type its own; skewed ones,
    // guides, limit stops and restraints to a CNODE none (they depend on the pipe or on another node)
    static int[] GroundDofs(PipeModelRestraint r)
    {
        if (r.CNode > 0 || r.Node <= 0) return new int[0];
        string t = (r.Type ?? "").Trim().ToUpperInvariant();
        if (t == "ANC") return new[] { 0, 1, 2, 3, 4, 5 };
        t = t.TrimStart('+', '-');
        int k = -1;
        if (t.Length >= 2 && t[0] == 'R' && "XYZ".IndexOf(t[1]) >= 0) k = 3 + "XYZ".IndexOf(t[1]);
        else if (t.Length >= 1 && "XYZ".IndexOf(t[0]) >= 0) k = "XYZ".IndexOf(t[0]);
        if (k < 0) return new int[0];
        if (r.Cosines != null && Math.Abs(Math.Abs(r.Cosines[k % 3]) - 1) > 1e-6) return new int[0];
        return new[] { k };
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
