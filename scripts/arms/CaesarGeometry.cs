using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

// CaesarGeometry  --  CaesarModel (CaesarNeutralReader.cs) -> CaesarPipeGraph: node positions and the
// straight pieces (beams) that draw the whole pipe, bends as chords. Doubles throughout; job LENGTH units.
// C# 5 / Add-Type (PowerShell 5.1) compatible; every type is prefixed Caesar (one global batch).
//
// Positions. A breadth-first walk over the element graph in both directions (TO = FROM + delta,
// FROM = TO - delta), seeded from COORDS. When the walk stops, restraint / hanger CNODE links and
// nozzle-to-vessel links carry it on (CAESAR's "connect geometry through CNodes"); a piece still
// unreached starts at the origin, with a warning. An element closing a loop is checked against the
// node it meets (loop-closure warning above 0.05 in). A bend node (mid "M", angle or near node) that
// an element or a CNODE link starts from carries the walk on as well, from its position on the arc,
// once the bend's FROM and TO are placed (a trunnion or dummy leg on an elbow); only then do origin
// seeds start.
// These delta sums are the coordinates CAESAR's own COORDINATE REPORT prints (TipPositions).
//
// Bends (CAESAR convention). The element carrying the BEND pointer has its deltas running to the
// tangent intersection point (TIP). Its TO node sits at the FAR point, FAR = TIP + T*u_out, with
// T = R*tan(theta/2) and u_out the direction of the element leaving the TO node. NEAR = TIP - T*u_in.
// Only the bend TO node moves off the delta sum: every other node, the ones downstream included, keeps
// its delta-sum position. Bend nodes are placed by angle from the NEAR point (code -2.0202 = "M" =
// theta/2; a value >= 0 is degrees; slots with node 0 are unused). The arc is drawn as chords of at most
// arcStepDeg, through every bend node. A mitred bend (miter points n > 0) is a polyline through its cut
// points (n = 1: the TIP itself). Guards: a zero-length bend element, no (or a zero-length) leaving
// element, theta < 1 or > 179 deg, or R <= 0 -> drawn straight with a warning, its bend nodes attached
// at the TO node. A tangent longer than the straight it eats (back-to-back bends, short legs) leaves a
// short piece running backwards (CaesarPiece.Backward), with a warning that says whether the overlap is
// within CAESAR's default 1% bend-length attachment.
//
// Synthetic vertices (bend near points without a node, chord points, miter cuts) get ids from
// CaesarPipeGraph.FirstSyntheticId up; each records the two real nodes that bracket it along the run and
// the path-length fraction between them, so results can be interpolated (CaesarSyntheticNode).
//
// Pieces: PIPE | BEND | REDUCER | VALVE | FLANGE | FLANGE_PAIR | RIGID | RIGID_LINK | EXPJOINT, with
// OD / wall at both ends in LENGTH units (REDUCER: B end from the REDUCERS record). Rigids: collinear with
// the run and type Valve or Flange Valve / Flange / Flange Pair -> VALVE / FLANGE / FLANGE_PAIR, type 0 ->
// RIGID; a weightless rigid that leaves the run or dead-ends (no other element, no CNODE link at an end)
// -> RIGID_LINK (shoe, trunnion, offset stub).
// Zero-length pieces (under 0.001 in, e.g. a bend's FAR point landing on the next node) are kept with
// Length = 0 so every node stays connected; skip them when drawing.

public static class CaesarPieceKind
{
    public const string Pipe = "PIPE", Bend = "BEND", Reducer = "REDUCER", Valve = "VALVE", Flange = "FLANGE",
        FlangePair = "FLANGE_PAIR", Rigid = "RIGID", RigidLink = "RIGID_LINK", ExpJoint = "EXPJOINT";
}

public class CaesarPiece
{
    public int PieceIndex;               // position in CaesarPipeGraph.Pieces
    public int ElementIndex;             // CaesarModel.Elements index (0-based)
    public int SubIndex;                 // order of the piece within its element, FROM -> TO
    public int FromNode, ToNode;         // ids in CaesarPipeGraph.Nodes (real or synthetic)
    public double OdA, WallA, OdB, WallB;   // LENGTH units at FromNode / ToNode
    public double Insulation;            // LENGTH units
    public string Kind = CaesarPieceKind.Pipe;
    public double Length;                // chord length, LENGTH units; 0 when under 0.001 in (zero-length: skip when drawing)
    public int RigidType = -1;           // CaesarRigid.TypeCode for rigid pieces, -1 otherwise
    public double RigidWeight = double.NaN;
    public bool Backward;                // runs against its element: a bend tangent overlaps this straight (short)

    public bool IsTaper { get { return Math.Abs(OdA - OdB) > 1e-9 || Math.Abs(WallA - WallB) > 1e-9; } }
}

public class CaesarSyntheticNode
{
    public int Id;
    public int NodeA, NodeB;             // the real nodes bracketing it along the run
    public double Fraction;              // 0 at NodeA .. 1 at NodeB, by path length
    public int ElementIndex;
    public string Kind = "";             // NEAR | ARC | MITER, or whatever the caller of AddSyntheticNode says

    public double Interpolate(double atA, double atB) { return atA + (atB - atA) * Fraction; }
}

public class CaesarBendGeom
{
    public int ElementIndex, BendBlock, FromNode, ToNode;
    public int LeavingElement = -1;      // element that sets u_out
    public double Radius, AngleDeg, T;   // T = R*tan(theta/2)
    public double LeadLength;            // signed straight from the FROM node to the NEAR point (< 0: overlap)
    public double[] Tip, Near, Far, Centre, UIn, UOut, Normal;   // Normal: unit, in the bend plane, NEAR -> centre
    public bool Straight;                // not drawn as a bend (see Reason)
    public string Reason = "";
    public bool Mitred;
    public int MiterPoints;
    public List<double[]> NodeAngles = new List<double[]>();   // {node, angle deg from NEAR}, ascending
    public List<int> Path = new List<int>();                   // vertex ids FROM .. TO along the element
}

public class CaesarPipeGraph
{
    public const int FirstSyntheticId = 10000000;

    public Dictionary<int, double[]> Nodes = new Dictionary<int, double[]>();          // every piece vertex: real and synthetic
    public Dictionary<int, double[]> TipPositions = new Dictionary<int, double[]>();   // delta sums of element nodes (CAESAR's coordinate report); a bend node that is an element end starts from its arc position
    public Dictionary<int, CaesarSyntheticNode> Synthetic = new Dictionary<int, CaesarSyntheticNode>();
    public List<int> SyntheticIds = new List<int>();                                   // creation order
    public Dictionary<int, double[]> AttachedNodes = new Dictionary<int, double[]>();  // real nodes on no piece (CNODEs, vessel / tee-surface nodes, straight-bend nodes)
    public Dictionary<int, int> AttachedHost = new Dictionary<int, int>();             // attached node -> the node whose position it takes
    public List<CaesarPiece> Pieces = new List<CaesarPiece>();
    public Dictionary<int, List<int>> ElementPieces = new Dictionary<int, List<int>>(); // element index -> piece indices, FROM -> TO
    public List<CaesarBendGeom> Bends = new List<CaesarBendGeom>();
    public Dictionary<int, CaesarBendGeom> BendByElement = new Dictionary<int, CaesarBendGeom>();
    public Dictionary<int, List<int>> NodePieces = new Dictionary<int, List<int>>();   // piece-vertex id -> pieces that end there
    public double[][] ElementDir = new double[0][];   // unit delta per element, null for a zero-length element
    public List<string> Warnings = new List<string>();
    public double ArcStepDeg;
    public int LoopClosures;             // elements that met an already placed node
    public double LoopClosureMax;        // their largest miss, LENGTH units
    public int OriginSeeds;              // pieces with no COORDS entry, started at the origin
    public int CNodeLinksUsed;           // walks carried on through a CNODE / vessel link
    public int BendNodeSeeds;            // walks carried on from a bend node (element end or CNODE link end) placed on its arc
    public int ZeroLengthPieces;
    public int BackwardPieces;           // pieces running against their element (bend tangent overlaps)
    public double MaxAbsCoord;
    public double[] BBoxMin, BBoxMax;
    public double[] SuggestedOffset;     // rounded centre to subtract before a float32 cast; null when MaxAbsCoord <= 1e6
    public int NextSyntheticId = FirstSyntheticId;
    public HashSet<int> RealIds = new HashSet<int>();   // every real CAESAR node id seen (synthetic ids skip them)

    public bool IsSynthetic(int id) { return Synthetic.ContainsKey(id); }

    // real nodes on pieces, ascending
    public List<int> RealNodeIds() { return Nodes.Keys.Where(k => !Synthetic.ContainsKey(k)).OrderBy(k => k).ToList(); }

    // position of any node (piece vertex or attached), null when unknown
    public double[] Position(int id)
    {
        double[] p;
        if (Nodes.TryGetValue(id, out p)) return p;
        if (AttachedNodes.TryGetValue(id, out p)) return p;
        return null;
    }

    // unit pipe axis at `node` on element `elementIndex` (GUI / LIM directions): the arc tangent when the node sits
    // on that element's bend (TO node: u_out), else the element's direction; for a zero-length element the first
    // piece with a length at the node; null when none
    public double[] AxisAt(int elementIndex, int node)
    {
        CaesarBendGeom b;
        if (BendByElement.TryGetValue(elementIndex, out b) && !b.Straight)
        {
            if (node == b.ToNode) return new[] { b.UOut[0], b.UOut[1], b.UOut[2] };
            foreach (var na in b.NodeAngles)
            {
                if ((int)na[0] != node) continue;
                double phi = na[1] * Math.PI / 180.0, c = Math.Cos(phi), s = Math.Sin(phi);
                return new[] { b.Normal[0] * s + b.UIn[0] * c, b.Normal[1] * s + b.UIn[1] * c, b.Normal[2] * s + b.UIn[2] * c };
            }
        }
        if (elementIndex >= 0 && elementIndex < ElementDir.Length && ElementDir[elementIndex] != null)
        {
            var d = ElementDir[elementIndex];
            return new[] { d[0], d[1], d[2] };
        }
        // zero-length element: the first piece with a length that ends at the node
        List<int> ps;
        if (NodePieces.TryGetValue(node, out ps))
            foreach (int pi in ps)
            {
                var p = Pieces[pi];
                if (p.Length <= 0) continue;
                double[] pa = Nodes[p.FromNode], pb = Nodes[p.ToNode];
                return new[] { (pb[0] - pa[0]) / p.Length, (pb[1] - pa[1]) / p.Length, (pb[2] - pa[2]) / p.Length };
            }
        return null;
    }

    // for the arm (e.g. a valve's mid point): a new synthetic vertex, numbered after the existing ones
    public int AddSyntheticNode(double[] pos, int nodeA, int nodeB, double fraction, int elementIndex, string kind)
    {
        while (RealIds.Contains(NextSyntheticId) || Nodes.ContainsKey(NextSyntheticId)) NextSyntheticId++;
        int id = NextSyntheticId++;
        Nodes[id] = new[] { pos[0], pos[1], pos[2] };
        var s = new CaesarSyntheticNode();
        s.Id = id; s.NodeA = nodeA; s.NodeB = nodeB; s.Fraction = fraction; s.ElementIndex = elementIndex; s.Kind = kind ?? "";
        Synthetic[id] = s;
        SyntheticIds.Add(id);
        return id;
    }

    public string Summary()
    {
        int real = Nodes.Count - Synthetic.Count;
        return string.Format(CultureInfo.InvariantCulture,
            "{0} real nodes, {1} synthetic, {2} attached, {3} pieces ({4} zero-length, {5} backward), {6} bends ({7} straight), " +
            "{8} loop closures (max miss {9:0.####}), {10} origin seeds, {11} CNODE links, max |coord| {12:0.###}, {13} warnings",
            real, Synthetic.Count, AttachedNodes.Count, Pieces.Count, ZeroLengthPieces, BackwardPieces, Bends.Count,
            Bends.Count(b => b.Straight), LoopClosures, LoopClosureMax, OriginSeeds, CNodeLinksUsed, MaxAbsCoord, Warnings.Count);
    }
}

public static class CaesarGeometry
{
    public const double DefaultArcStepDeg = 7.5;

    public static CaesarPipeGraph Build(CaesarModel m) { return Build(m, DefaultArcStepDeg); }

    public static CaesarPipeGraph Build(CaesarModel m, double arcStepDeg)
    {
        if (m == null) throw new ArgumentNullException("m");
        var g = new CaesarPipeGraph();
        new Builder(m, g, arcStepDeg).Run();
        return g;
    }

    // ---------------------------------------------------------------- vectors (double[3])

    static double[] V(double x, double y, double z) { return new[] { x, y, z }; }
    static double[] Copy(double[] a) { return new[] { a[0], a[1], a[2] }; }
    static double[] Add(double[] a, double[] b) { return V(a[0] + b[0], a[1] + b[1], a[2] + b[2]); }
    static double[] Sub(double[] a, double[] b) { return V(a[0] - b[0], a[1] - b[1], a[2] - b[2]); }
    static double[] Mul(double[] a, double s) { return V(a[0] * s, a[1] * s, a[2] * s); }
    static double Dot(double[] a, double[] b) { return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]; }
    static double Norm(double[] a) { return Math.Sqrt(Dot(a, a)); }
    static double Dist(double[] a, double[] b) { return Norm(Sub(a, b)); }
    static double[] Unit(double[] a) { double n = Norm(a); return n > 0 && !double.IsInfinity(n) && !double.IsNaN(n) ? Mul(a, 1.0 / n) : null; }
    static bool Finite(double[] a) { return a != null && !double.IsNaN(a[0] + a[1] + a[2]) && !double.IsInfinity(a[0] + a[1] + a[2]); }

    static string I(int v) { return v.ToString(CultureInfo.InvariantCulture); }
    static string F(double v) { return v.ToString("0.####", CultureInfo.InvariantCulture); }

    // ---------------------------------------------------------------- builder

    sealed class Builder
    {
        readonly CaesarModel M;
        readonly CaesarPipeGraph G;
        readonly List<CaesarElement> E;
        readonly double _stepDeg;
        double _inch, _epsLen, _tolLead, _tolLoop;
        readonly Dictionary<int, List<int>> _adj = new Dictionary<int, List<int>>();
        double[][] _dir;
        bool[] _usable;                  // delta finite and below Huge
        string[] _rigidKind;
        const double Huge = 1e12;        // LENGTH units: a delta, coordinate or radius beyond this is garbage
        const int MaxMiter = 50;
        readonly Dictionary<int, int> _bendNodeOwner = new Dictionary<int, int>();
        readonly HashSet<int> _linked = new HashSet<int>();   // nodes on a CNODE / vessel link (the run goes on through them)
        readonly Dictionary<string, List<string>> _agg = new Dictionary<string, List<string>>();
        readonly List<string> _aggOrder = new List<string>();
        readonly Dictionary<string, string> _aggText = new Dictionary<string, string>();
        const double CosParallel = 0.99619469809174555;   // cos 5 deg
        bool _mute;                      // WarnAgg silent (bend geometry computed early, in TipFrame, for its nodes only)
        readonly Dictionary<int, double[]> _arcSeeded = new Dictionary<int, double[]>();   // bend node -> arc position the walk started from

        public Builder(CaesarModel m, CaesarPipeGraph g, double arcStepDeg)
        {
            M = m; G = g; E = m.Elements;
            double s = arcStepDeg;
            if (double.IsNaN(s) || s <= 0) { s = DefaultArcStepDeg; Warn("arc step " + F(arcStepDeg) + " deg not usable; " + F(s) + " used"); }
            if (s < 0.5) { Warn("arc step " + F(s) + " deg raised to 0.5"); s = 0.5; }
            if (s > 45) { Warn("arc step " + F(s) + " deg lowered to 45"); s = 45; }
            _stepDeg = s;
            G.ArcStepDeg = s;
        }

        void Warn(string w) { G.Warnings.Add(w); }

        void WarnAgg(string key, string text, string example)
        {
            if (_mute) return;
            List<string> ex;
            if (!_agg.TryGetValue(key, out ex)) { ex = new List<string>(); _agg[key] = ex; _aggOrder.Add(key); _aggText[key] = text; }
            ex.Add(example);
        }

        public void Run()
        {
            _inch = M.Units.InchInLength;
            _epsLen = 1e-6 * _inch;     // zero length
            _tolLead = 1e-3 * _inch;    // straight remainders smaller than this count as 0 (7-digit file values)
            _tolLoop = 0.05 * _inch;    // loop-closure warning
            foreach (var e in E) { G.RealIds.Add(e.From); G.RealIds.Add(e.To); }
            foreach (var b in M.Bends) foreach (var an in b.AngleNodes) G.RealIds.Add((int)Math.Round(an[1]));
            foreach (var r in M.Restraints) { G.RealIds.Add(r.Node); if (r.CNode > 0) G.RealIds.Add(r.CNode); }
            foreach (var h in M.Hangers) { G.RealIds.Add(h.Node); if (h.CNode > 0) G.RealIds.Add(h.CNode); }
            foreach (var z in M.Nozzles) { G.RealIds.Add(z.Node); G.RealIds.Add(z.VesselNode); }
            foreach (var t in M.SifTees) { G.RealIds.Add(t.Node); if (t.SurfaceNode > 0) G.RealIds.Add(t.SurfaceNode); }
            int bigIds = G.RealIds.Count(id => id >= CaesarPipeGraph.FirstSyntheticId);
            if (bigIds > 0) Warn(I(bigIds) + " node number(s) at or above " + I(CaesarPipeGraph.FirstSyntheticId) + ": synthetic ids skip them; use IsSynthetic, not the id range");
            _dir = new double[E.Count][];
            _usable = new bool[E.Count];
            foreach (var e in E)
            {
                AdjAdd(e.From, e.Index); if (e.To != e.From) AdjAdd(e.To, e.Index);
                double[] d = V(e.Dx, e.Dy, e.Dz);
                _usable[e.Index] = Finite(d) && Math.Abs(e.Dx) < Huge && Math.Abs(e.Dy) < Huge && Math.Abs(e.Dz) < Huge;
                if (!_usable[e.Index]) { WarnAgg("delta", "element delta not usable (not a number or beyond 1e12): the walk does not cross it", "element " + El(e)); _dir[e.Index] = null; continue; }
                _dir[e.Index] = Norm(d) > _epsLen ? Unit(d) : null;
                if (e.From == e.To) WarnAgg("self", "element from a node to itself: ignored by the walk", "element " + I(e.Index + 1) + " node " + I(e.From));
            }
            G.ElementDir = _dir;
            TipFrame();
            BendGeometry();
            Physical();
            ClassifyRigids();
            foreach (var e in E) MakePieces(e);
            Attached();
            Stats();
            foreach (var key in _aggOrder)
            {
                var ex = _agg[key];
                Warn(_aggText[key] + " (" + I(ex.Count) + "x: " + string.Join(", ", ex.Take(8).ToArray()) + (ex.Count > 8 ? ", ..." : "") + ")");
            }
        }

        void AdjAdd(int node, int ei)
        {
            List<int> l;
            if (!_adj.TryGetValue(node, out l)) { l = new List<int>(); _adj[node] = l; }
            l.Add(ei);
        }

        string El(CaesarElement e) { return I(e.From) + "-" + I(e.To); }

        // ---------------- delta sums (TIP frame)

        void TipFrame()
        {
            var tip = G.TipPositions;
            var q = new Queue<int>();
            var tree = new HashSet<int>();
            var closed = new HashSet<int>();
            foreach (var c in M.Coords)
            {
                int node = (int)Math.Round(c[0]);
                if (!_adj.ContainsKey(node)) { WarnAgg("coords-off", "COORDS node on no element: ignored", I(node)); continue; }
                var p = V(c[1], c[2], c[3]);
                if (!Finite(p) || Math.Abs(p[0]) >= Huge || Math.Abs(p[1]) >= Huge || Math.Abs(p[2]) >= Huge) { WarnAgg("coords-nan", "COORDS entry not usable (not a number or beyond 1e12): ignored", I(node)); continue; }
                double[] have;
                if (tip.TryGetValue(node, out have))
                {
                    if (Dist(have, p) > _tolLoop) WarnAgg("coords-dup", "COORDS node given twice with different coordinates: the first kept", I(node));
                    continue;
                }
                tip[node] = p;
                q.Enqueue(node);
            }
            if (M.Coords.Count == 0 && E.Count > 0) Warn("no COORDS entry: the model starts at the origin");
            Walk(q, tree, closed);
            var links = new List<int[]>();
            foreach (var r in M.Restraints) if (r.CNode > 0 && r.CNode != r.Node) links.Add(new[] { r.Node, r.CNode });
            foreach (var h in M.Hangers) if (h.CNode > 0 && h.CNode != h.Node) links.Add(new[] { h.Node, h.CNode });
            foreach (var z in M.Nozzles) if (z.VesselNode > 0 && z.Node > 0 && z.VesselNode != z.Node) links.Add(new[] { z.Node, z.VesselNode });
            foreach (var l in links) { _linked.Add(l[0]); _linked.Add(l[1]); }
            var seeds = new List<string>();
            var linkBendPos = new Dictionary<int, double[]>();   // bend nodes on no element, ends of a link: arc position
            var bendDone = new HashSet<int>();
            while (true)
            {
                bool progress = false;
                foreach (var l in links)
                {
                    double[] pa = Placed(l[0], linkBendPos), pb = Placed(l[1], linkBendPos);
                    if ((pa != null) == (pb != null)) continue;
                    int to = pa != null ? l[1] : l[0];
                    if (!_adj.ContainsKey(to)) continue;   // no element: attached later
                    tip[to] = Copy(pa ?? pb);
                    q.Enqueue(to);
                    G.CNodeLinksUsed++;
                    progress = true;
                }
                if (progress) { Walk(q, tree, closed); continue; }
                if (SeedBendNodes(q, bendDone, linkBendPos)) { Walk(q, tree, closed); continue; }
                int seed = int.MinValue;
                foreach (var e in E)
                {
                    if (!tip.ContainsKey(e.From)) { seed = e.From; break; }
                    if (!tip.ContainsKey(e.To)) { seed = e.To; break; }
                }
                if (seed == int.MinValue) break;
                int before = tip.Count;
                tip[seed] = V(0, 0, 0);
                q.Enqueue(seed);
                G.OriginSeeds++;
                Walk(q, tree, closed);
                seeds.Add("node " + I(seed) + " (" + I(tip.Count - before) + " nodes)");
            }
            if (seeds.Count > 0)
                Warn("no COORDS entry or CNODE link reaches " + I(seeds.Count) + " piece(s) of the model; each starts at the origin: " +
                     string.Join(", ", seeds.Take(8).ToArray()) + (seeds.Count > 8 ? ", ..." : ""));
        }

        double[] Placed(int node, Dictionary<int, double[]> linkBendPos)
        {
            double[] p;
            if (G.TipPositions.TryGetValue(node, out p)) return p;
            if (linkBendPos.TryGetValue(node, out p)) return p;
            return null;
        }

        // Bends whose FROM and TO are placed: their bend nodes that start an element (seeded in TipPositions)
        // or a CNODE link (linkBendPos) get their arc position, the one Physical() gives them. The first bend
        // in element order owns a bend node / a TO node, as in Physical(); Physical() re-checks the result.
        bool SeedBendNodes(Queue<int> q, HashSet<int> done, Dictionary<int, double[]> linkBendPos)
        {
            var tip = G.TipPositions;
            bool progress = false;
            var nodeOwner = new Dictionary<int, int>();
            var toOwner = new Dictionary<int, int>();
            foreach (var e in E)
            {
                if (e.Bend <= 0 || e.Bend > M.Bends.Count) continue;
                if (!toOwner.ContainsKey(e.To)) toOwner[e.To] = e.Index;
                foreach (var an in M.Bends[e.Bend - 1].AngleNodes)
                {
                    int node = (int)Math.Round(an[1]);
                    if (node != e.From && node != e.To && !nodeOwner.ContainsKey(node)) nodeOwner[node] = e.Index;
                }
            }
            foreach (var e in E)
            {
                if (e.Bend <= 0 || e.Bend > M.Bends.Count || done.Contains(e.Index)) continue;
                if (!tip.ContainsKey(e.From) || !tip.ContainsKey(e.To)) continue;
                done.Add(e.Index);
                if (toOwner[e.To] != e.Index) continue;
                var b = M.Bends[e.Bend - 1];
                var bg = new CaesarBendGeom();
                bg.Radius = b.Radius; bg.MiterPoints = b.MiterPoints; bg.Mitred = b.MiterPoints > 0 && b.MiterPoints <= MaxMiter;
                _mute = true;
                string why;
                try { why = Arc(e, b, bg); } finally { _mute = false; }
                if (why != null) continue;
                foreach (var na in bg.NodeAngles)
                {
                    int node = (int)na[0];
                    int owner;
                    if (!nodeOwner.TryGetValue(node, out owner) || owner != e.Index) continue;
                    if (tip.ContainsKey(node) || linkBendPos.ContainsKey(node)) continue;
                    bool starts = _adj.ContainsKey(node);
                    if (!starts && !_linked.Contains(node)) continue;
                    var pos = CurvePoint(bg, na[1]);
                    if (!Finite(pos)) continue;
                    if (starts) { tip[node] = pos; q.Enqueue(node); _arcSeeded[node] = Copy(pos); }
                    else linkBendPos[node] = pos;
                    G.BendNodeSeeds++;
                    progress = true;
                }
            }
            return progress;
        }

        void Walk(Queue<int> q, HashSet<int> tree, HashSet<int> closed)
        {
            var tip = G.TipPositions;
            while (q.Count > 0)
            {
                int u = q.Dequeue();
                List<int> els;
                if (!_adj.TryGetValue(u, out els)) continue;
                double[] pu = tip[u];
                foreach (int ei in els)
                {
                    var e = E[ei];
                    if (e.From == e.To || !_usable[ei]) continue;
                    bool fwd = e.From == u;
                    int v = fwd ? e.To : e.From;
                    double s = fwd ? 1.0 : -1.0;
                    var pv = V(pu[0] + s * e.Dx, pu[1] + s * e.Dy, pu[2] + s * e.Dz);
                    double[] have;
                    if (!tip.TryGetValue(v, out have)) { tip[v] = pv; tree.Add(ei); q.Enqueue(v); continue; }
                    if (tree.Contains(ei) || closed.Contains(ei)) continue;
                    closed.Add(ei);
                    double miss = Dist(have, pv);
                    G.LoopClosures++;
                    if (miss > G.LoopClosureMax) G.LoopClosureMax = miss;
                    if (miss > _tolLoop) WarnAgg("loop", "loop closure: element misses the node it closes on (delta-sum position kept)", "element " + El(e) + " by " + F(miss));
                }
            }
        }

        // ---------------- bends

        void BendGeometry()
        {
            foreach (var e in E)
            {
                if (e.Bend <= 0 || e.Bend > M.Bends.Count) continue;
                var b = M.Bends[e.Bend - 1];
                var bg = new CaesarBendGeom();
                bg.ElementIndex = e.Index; bg.BendBlock = e.Bend; bg.FromNode = e.From; bg.ToNode = e.To;
                bg.Radius = b.Radius; bg.MiterPoints = b.MiterPoints; bg.Mitred = b.MiterPoints > 0 && b.MiterPoints <= MaxMiter;
                if (b.MiterPoints > MaxMiter || b.MiterPoints < 0) WarnAgg("miter", "miter point count not usable: drawn as a smooth bend", "element " + El(e) + " n=" + I(b.MiterPoints));
                G.Bends.Add(bg);
                G.BendByElement[e.Index] = bg;
                string why = Arc(e, b, bg);
                if (why != null) { bg.Straight = true; bg.Reason = why; WarnAgg("bend-straight", "bend drawn straight, its bend nodes attached at the TO node", "element " + El(e) + ": " + why); }
            }
        }

        // null when the arc is good, else why it is drawn straight
        string Arc(CaesarElement e, CaesarBend b, CaesarBendGeom bg)
        {
            double[] tipTo;
            if (!G.TipPositions.TryGetValue(e.To, out tipTo) || !G.TipPositions.ContainsKey(e.From)) return "element not placed";
            var uIn = _dir[e.Index];
            if (uIn == null) return "zero-length bend element";
            int outEl;
            double[] uOut;
            string w = Leaving(e, out outEl, out uOut);
            if (w != null) return w;
            double cosT = Math.Max(-1.0, Math.Min(1.0, Dot(uIn, uOut)));
            double theta = Math.Acos(cosT);
            double deg = theta * 180.0 / Math.PI;
            bg.AngleDeg = deg;
            bg.LeavingElement = outEl;
            if (deg < 1.0 || deg > 179.0) return "bend angle " + F(deg) + " deg outside 1..179";
            if (!(b.Radius > 0) || b.Radius >= Huge) return "radius " + F(b.Radius) + " not usable";
            double T = b.Radius * Math.Tan(theta / 2.0);
            bg.T = T;
            bg.Tip = Copy(tipTo); bg.UIn = Copy(uIn); bg.UOut = Copy(uOut);
            bg.Near = Sub(tipTo, Mul(uIn, T));
            bg.Far = Add(tipTo, Mul(uOut, T));
            bg.Normal = Unit(Sub(uOut, Mul(uIn, cosT)));
            if (bg.Normal == null) return "bend plane undefined";
            bg.Centre = Add(bg.Near, Mul(bg.Normal, b.Radius));
            foreach (var an in b.AngleNodes)
            {
                int node = (int)Math.Round(an[1]);
                double code = an[0], ang;
                if (CaesarBend.IsMid(code)) ang = deg / 2.0;
                else if (code >= 0) ang = code;
                else { WarnAgg("bend-code", "bend node angle code not known: node placed at mid-bend", "node " + I(node) + " code " + F(code)); ang = deg / 2.0; }
                if (ang > deg + 1e-6) { WarnAgg("bend-ang", "bend node angle beyond the bend angle: node placed at the far end", "node " + I(node) + " " + F(ang) + " > " + F(deg)); ang = deg; }
                if (node == e.From || node == e.To) { WarnAgg("bend-self", "bend node is the bend element's own end node: ignored", "node " + I(node)); continue; }
                bg.NodeAngles.Add(new[] { (double)node, ang });
            }
            bg.NodeAngles.Sort((x, y) => x[1].CompareTo(y[1]));
            for (int i = 1; i < bg.NodeAngles.Count; i++)
                if (Math.Abs(bg.NodeAngles[i][1] - bg.NodeAngles[i - 1][1]) < 1e-9)
                    WarnAgg("bend-same", "two bend nodes at the same angle (zero-length piece between them)", "nodes " + I((int)bg.NodeAngles[i - 1][0]) + ", " + I((int)bg.NodeAngles[i][0]));
            return null;
        }

        // direction leaving the bend's TO node: the next element in input order when it starts there, else the
        // first element starting there, else one ending there (reversed)
        string Leaving(CaesarElement e, out int outEl, out double[] uOut)
        {
            outEl = -1; uOut = null;
            var starts = new List<int>();
            var ends = new List<int>();
            int zero = 0;
            foreach (int x in _adj[e.To])
            {
                if (x == e.Index) continue;
                var ex = E[x];
                if (ex.From == ex.To) continue;
                if (_dir[x] == null) { zero++; continue; }
                if (ex.From == e.To) starts.Add(x); else ends.Add(x);
            }
            if (starts.Count + ends.Count == 0) return zero > 0 ? "the element leaving the bend has zero length" : "no element leaves the bend's TO node";
            if (starts.Contains(e.Index + 1)) outEl = e.Index + 1;
            else if (starts.Count > 0) outEl = starts[0];
            else outEl = ends[0];
            uOut = E[outEl].From == e.To ? Copy(_dir[outEl]) : Mul(_dir[outEl], -1.0);
            if (starts.Count + ends.Count > 1)
                WarnAgg("bend-branch", "more than one element leaves a bend's TO node: the outgoing direction is taken from one of them", "node " + I(e.To) + " -> element " + El(E[outEl]));
            return null;
        }

        // point at `ang` degrees from NEAR: on the arc, or on the miter polyline (radial projection)
        double[] CurvePoint(CaesarBendGeom bg, double ang)
        {
            double phi = ang * Math.PI / 180.0;
            var dir = Add(Mul(bg.Normal, -Math.Cos(phi)), Mul(bg.UIn, Math.Sin(phi)));
            double r = bg.Radius;
            if (bg.Mitred)
            {
                int n = bg.MiterPoints;
                double theta = bg.AngleDeg * Math.PI / 180.0;
                // segment j (0..n) spans [a_j, a_j+1] (a_0 = 0, a_k = (2k-1) theta / 2n, a_n+1 = theta) and touches the arc at j theta / n
                int j = (int)Math.Floor(phi * n / theta + 0.5);
                if (j < 0) j = 0;
                if (j > n) j = n;
                r = bg.Radius / Math.Cos(phi - j * theta / n);
            }
            return Add(bg.Centre, Mul(dir, r));
        }

        // ---------------- physical positions

        void Physical()
        {
            var ph = G.Nodes;
            foreach (var kv in G.TipPositions) ph[kv.Key] = Copy(kv.Value);
            var farOwner = new Dictionary<int, int>();
            foreach (var bg in G.Bends)
            {
                if (bg.Straight) continue;
                int prev;
                if (farOwner.TryGetValue(bg.ToNode, out prev))
                {
                    bg.Straight = true;
                    bg.Reason = "its TO node already ends the bend on element " + I(prev + 1);
                    WarnAgg("bend-straight", "bend drawn straight, its bend nodes attached at the TO node", "element " + El(E[bg.ElementIndex]) + ": " + bg.Reason);
                    continue;
                }
                farOwner[bg.ToNode] = bg.ElementIndex;
                ph[bg.ToNode] = Copy(bg.Far);
                var keep = new List<double[]>();
                foreach (var na in bg.NodeAngles)
                {
                    int node = (int)na[0];
                    int owner;
                    if (_bendNodeOwner.TryGetValue(node, out owner)) { WarnAgg("bend-dup", "bend node used by two bends: the first one keeps it", "node " + I(node)); continue; }
                    var cp = CurvePoint(bg, na[1]);
                    double[] tp, seeded;
                    if (_arcSeeded.TryGetValue(node, out seeded))
                    {
                        if (Dist(seeded, cp) > _tolLoop) WarnAgg("bend-seed", "bend node: the walk started from a different arc position than the one drawn (elements from it are off by the difference)", "node " + I(node) + " by " + F(Dist(seeded, cp)));
                    }
                    else if (G.TipPositions.TryGetValue(node, out tp) && Dist(tp, cp) > _tolLoop)
                        WarnAgg("bend-endnode", "bend node is also an element end node and its delta-sum position is off the arc: its bend position is used", "node " + I(node) + " by " + F(Dist(tp, cp)));
                    _bendNodeOwner[node] = bg.ElementIndex;
                    ph[node] = cp;
                    keep.Add(na);
                }
                bg.NodeAngles = keep;
            }
            foreach (var kv in _arcSeeded)
                if (!_bendNodeOwner.ContainsKey(kv.Key))
                    WarnAgg("bend-seed", "bend node: the walk started from its arc position, but that bend is not drawn as a bend here (position kept on the arc)", "node " + I(kv.Key));
        }

        // ---------------- rigid classification

        void ClassifyRigids()
        {
            int n = E.Count;
            _rigidKind = new string[n];
            var isRigid = new bool[n];
            foreach (var e in E) isRigid[e.Index] = e.Rigid > 0 && e.Rigid <= M.Rigids.Count;
            var col = new bool[n];
            foreach (var e in E)
            {
                if (!isRigid[e.Index] || _dir[e.Index] == null) continue;
                foreach (int a in Neighbours(e))
                    if (!isRigid[a] && _dir[a] != null && Math.Abs(Dot(_dir[e.Index], _dir[a])) >= CosParallel) { col[e.Index] = true; break; }
            }
            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (var e in E)
                {
                    if (!isRigid[e.Index] || col[e.Index] || _dir[e.Index] == null) continue;
                    foreach (int a in Neighbours(e))
                        if (isRigid[a] && col[a] && _dir[a] != null && Math.Abs(Dot(_dir[e.Index], _dir[a])) >= CosParallel) { col[e.Index] = true; changed = true; break; }
                }
            }
            foreach (var e in E)
            {
                if (!isRigid[e.Index]) continue;
                var r = M.Rigids[e.Rigid - 1];
                bool weightless = Math.Abs(r.Weight) < 1e-9;
                bool deadEnd = (_adj[e.From].Count == 1 && !_linked.Contains(e.From)) || (_adj[e.To].Count == 1 && !_linked.Contains(e.To));
                string k;
                if (weightless && (!col[e.Index] || deadEnd)) k = CaesarPieceKind.RigidLink;
                else if (r.TypeCode == 1 || r.TypeCode == 4) k = CaesarPieceKind.Valve;
                else if (r.TypeCode == 2) k = CaesarPieceKind.Flange;
                else if (r.TypeCode == 3) k = CaesarPieceKind.FlangePair;
                else k = CaesarPieceKind.Rigid;
                if (!col[e.Index] && k != CaesarPieceKind.RigidLink)
                    WarnAgg("rigid-offline", "rigid with weight off the pipe run: drawn as its type", "element " + El(e) + " " + r.TypeName);
                _rigidKind[e.Index] = k;
            }
        }

        IEnumerable<int> Neighbours(CaesarElement e)
        {
            foreach (int a in _adj[e.From]) if (a != e.Index) yield return a;
            if (e.To != e.From) foreach (int a in _adj[e.To]) if (a != e.Index) yield return a;
        }

        // ---------------- pieces

        double OdOf(CaesarElement e) { return M.Units.Convert(e.Od, CaesarUnits.Diameter, CaesarUnits.Length); }
        double WallOf(CaesarElement e) { return M.Units.Convert(e.Wall, CaesarUnits.Wall, CaesarUnits.Length); }
        double InsOf(CaesarElement e) { return M.Units.Convert(e.Insulation, CaesarUnits.Wall, CaesarUnits.Length); }

        CaesarPiece AddPiece(CaesarElement e, int a, int b, string kind)
        {
            var p = new CaesarPiece();
            p.PieceIndex = G.Pieces.Count;
            p.ElementIndex = e.Index;
            p.FromNode = a; p.ToNode = b;
            p.OdA = p.OdB = OdOf(e);
            p.WallA = p.WallB = WallOf(e);
            p.Insulation = InsOf(e);
            p.Kind = kind;
            p.Length = Dist(G.Nodes[a], G.Nodes[b]);
            if (p.Length <= _tolLead) { p.Length = 0; G.ZeroLengthPieces++; }   // 7-digit file values: under 0.001 in is nothing
            List<int> l;
            if (!G.ElementPieces.TryGetValue(e.Index, out l)) { l = new List<int>(); G.ElementPieces[e.Index] = l; }
            p.SubIndex = l.Count;
            l.Add(p.PieceIndex);
            G.Pieces.Add(p);
            NodePiece(a, p.PieceIndex);
            if (b != a) NodePiece(b, p.PieceIndex);
            return p;
        }

        void NodePiece(int node, int piece)
        {
            List<int> l;
            if (!G.NodePieces.TryGetValue(node, out l)) { l = new List<int>(); G.NodePieces[node] = l; }
            l.Add(piece);
        }

        // a piece running against its element: a bend tangent ate more than the straight it sits on
        void Overlap(CaesarPiece p, CaesarElement e, double overlap)
        {
            p.Backward = true;
            G.BackwardPieces++;
            double len = Math.Sqrt(e.Dx * e.Dx + e.Dy * e.Dy + e.Dz * e.Dz);
            if (overlap <= 0.01 * len)
                WarnAgg("overlap-small", "bend tangent overlaps the straight next to it by under 1% of the element (CAESAR's default bend-length attachment): a short piece runs backwards", "element " + El(e) + " by " + F(overlap));
            else
                WarnAgg("overlap", "bend tangent overlaps the straight next to it by MORE than 1% of the element (check the model): a piece runs backwards", "element " + El(e) + " by " + F(overlap));
        }

        int NewSyn(double[] pos, CaesarElement e, string kind) { return G.AddSyntheticNode(pos, 0, 0, 0, e.Index, kind); }

        void MakePieces(CaesarElement e)
        {
            if (e.From == e.To || !G.Nodes.ContainsKey(e.From) || !G.Nodes.ContainsKey(e.To)) return;
            CaesarBendGeom bg;
            if (G.BendByElement.TryGetValue(e.Index, out bg) && !bg.Straight) { BendPieces(e, bg); return; }
            string kind = CaesarPieceKind.Pipe;
            CaesarRigid rigid = null;
            if (e.Rigid > 0 && e.Rigid <= M.Rigids.Count) { kind = _rigidKind[e.Index]; rigid = M.Rigids[e.Rigid - 1]; }
            else if (e.ExpJt > 0 && e.ExpJt <= M.ExpJoints.Count) kind = CaesarPieceKind.ExpJoint;
            else if (e.Reducer > 0 && e.Reducer <= M.Reducers.Count) kind = CaesarPieceKind.Reducer;
            var p = AddPiece(e, e.From, e.To, kind);
            if (rigid != null) { p.RigidType = rigid.TypeCode; p.RigidWeight = rigid.Weight; }
            if (kind == CaesarPieceKind.Reducer)
            {
                var r = M.Reducers[e.Reducer - 1];
                if (r.Od2 > 0) p.OdB = M.Units.Convert(r.Od2, CaesarUnits.Diameter, CaesarUnits.Length);
                else WarnAgg("red-od", "reducer without a second diameter: drawn straight", "element " + El(e));
                if (r.Wall2 > 0) p.WallB = M.Units.Convert(r.Wall2, CaesarUnits.Wall, CaesarUnits.Length);
            }
            if (_dir[e.Index] != null && p.Length > 0)
            {
                double along = Dot(Sub(G.Nodes[e.To], G.Nodes[e.From]), _dir[e.Index]);
                if (along < -_tolLead) Overlap(p, e, -along);
            }
        }

        void BendPieces(CaesarElement e, CaesarBendGeom bg)
        {
            var path = bg.Path;
            path.Clear();
            path.Add(e.From);
            double lead = Dot(Sub(bg.Near, G.Nodes[e.From]), bg.UIn);
            bg.LeadLength = lead;
            int node0 = 0;
            var rest = new List<double[]>();
            foreach (var na in bg.NodeAngles) { if (na[1] <= 1e-9 && node0 == 0) node0 = (int)na[0]; else rest.Add(na); }
            int cur;
            if (Math.Abs(lead) <= _tolLead)
            {
                if (node0 != 0) { AddPiece(e, e.From, node0, CaesarPieceKind.Pipe); path.Add(node0); cur = node0; }
                else cur = e.From;
            }
            else
            {
                cur = node0 != 0 ? node0 : NewSyn(bg.Near, e, "NEAR");
                var lp = AddPiece(e, e.From, cur, CaesarPieceKind.Pipe);
                path.Add(cur);
                if (lead < 0) Overlap(lp, e, -lead);
            }
            var targets = new List<double[]>(rest);
            targets.Add(new[] { (double)e.To, bg.AngleDeg });
            if (!bg.Mitred)
            {
                double curAng = 0;
                foreach (var t in targets)
                {
                    int node = (int)t[0];
                    double ang = t[1], span = ang - curAng;
                    int n = Math.Max(1, (int)Math.Ceiling(span / _stepDeg - 1e-9));
                    for (int k = 1; k < n; k++)
                    {
                        int v = NewSyn(CurvePoint(bg, curAng + span * k / n), e, "ARC");
                        AddPiece(e, cur, v, CaesarPieceKind.Bend);
                        path.Add(v);
                        cur = v;
                    }
                    AddPiece(e, cur, node, CaesarPieceKind.Bend);
                    path.Add(node);
                    cur = node;
                    curAng = ang;
                }
            }
            else
            {
                int nm = bg.MiterPoints;
                var verts = new List<double[]>();   // {id, angle}; synthetic cuts get ids as they are reached
                for (int k = 1; k <= nm; k++)
                {
                    double ca = (2.0 * k - 1.0) * bg.AngleDeg / (2.0 * nm);
                    if (!targets.Any(t => Math.Abs(t[1] - ca) < 1e-9)) verts.Add(new[] { -1.0, ca });   // a bend node on the cut is the vertex
                }
                foreach (var t in targets) verts.Add(t);
                verts.Sort((x, y) => x[1] != y[1] ? x[1].CompareTo(y[1]) : y[0].CompareTo(x[0]));
                foreach (var v in verts)
                {
                    int id = v[0] < 0 ? NewSyn(CurvePoint(bg, v[1]), e, "MITER") : (int)v[0];
                    AddPiece(e, cur, id, CaesarPieceKind.Bend);
                    path.Add(id);
                    cur = id;
                }
            }
            // bracket every synthetic vertex of this element by the real vertices around it
            var cum = new double[path.Count];
            for (int i = 1; i < path.Count; i++) cum[i] = cum[i - 1] + Dist(G.Nodes[path[i - 1]], G.Nodes[path[i]]);
            int lastReal = 0;
            for (int i = 1; i < path.Count; i++)
            {
                if (!G.Synthetic.ContainsKey(path[i])) { lastReal = i; continue; }
                int next = i + 1;
                while (next < path.Count && G.Synthetic.ContainsKey(path[next])) next++;
                if (next >= path.Count) next = path.Count - 1;
                var s = G.Synthetic[path[i]];
                s.NodeA = path[lastReal];
                s.NodeB = path[next];
                double span = cum[next] - cum[lastReal];
                s.Fraction = span > 0 ? (cum[i] - cum[lastReal]) / span : 0.0;
            }
        }

        // ---------------- nodes on no piece

        void Attached()
        {
            foreach (var r in M.Restraints) if (r.CNode > 0) Attach(r.CNode, r.Node);
            foreach (var h in M.Hangers) if (h.CNode > 0) Attach(h.CNode, h.Node);
            foreach (var z in M.Nozzles) if (z.VesselNode > 0) Attach(z.VesselNode, z.Node);
            foreach (var t in M.SifTees) if (t.SurfaceNode > 0) Attach(t.SurfaceNode, t.Node);
            foreach (var bg in G.Bends)
            {
                if (!bg.Straight) continue;
                foreach (var an in M.Bends[bg.BendBlock - 1].AngleNodes)
                {
                    int node = (int)Math.Round(an[1]);
                    if (node != bg.FromNode && node != bg.ToNode) Attach(node, bg.ToNode);
                }
            }
            foreach (var r in M.Restraints) if (G.Position(r.Node) == null) WarnAgg("off-r", "restraint node with no position (not on any element)", I(r.Node) + " " + r.TypeName);
            foreach (var d in M.Displacements) if (G.Position(d.Node) == null) WarnAgg("off-d", "imposed-displacement node with no position (not on any element)", I(d.Node));
            foreach (var h in M.Hangers) if (h.Node > 0 && G.Position(h.Node) == null) WarnAgg("off-h", "hanger node with no position (not on any element)", I(h.Node));
            foreach (var t in M.SifTees) if (G.Position(t.Node) == null) WarnAgg("off-s", "SIF / tee node with no position (not on any element)", I(t.Node));
        }

        void Attach(int node, int host)
        {
            if (node <= 0 || G.Nodes.ContainsKey(node) || G.AttachedNodes.ContainsKey(node)) return;
            double[] hp = G.Position(host);
            if (hp == null) return;
            G.AttachedNodes[node] = Copy(hp);
            G.AttachedHost[node] = host;
        }

        // ---------------- bounds, precision, sanity

        void Stats()
        {
            int bad = 0;
            double[] mn = null, mx = null;
            double maxAbs = 0;
            foreach (var p in G.Nodes.Values.Concat(G.AttachedNodes.Values))
            {
                if (!Finite(p)) { bad++; continue; }
                if (mn == null) { mn = Copy(p); mx = Copy(p); }
                for (int k = 0; k < 3; k++)
                {
                    mn[k] = Math.Min(mn[k], p[k]); mx[k] = Math.Max(mx[k], p[k]);
                    maxAbs = Math.Max(maxAbs, Math.Abs(p[k]));
                }
            }
            if (bad > 0) Warn(I(bad) + " node position(s) are not numbers");
            foreach (var p in G.Pieces) if (double.IsNaN(p.OdA) || double.IsNaN(p.OdB) || p.OdA <= 0) { WarnAgg("od", "piece without a positive OD", "element " + El(E[p.ElementIndex])); }
            G.BBoxMin = mn ?? new double[3];
            G.BBoxMax = mx ?? new double[3];
            G.MaxAbsCoord = maxAbs;
            if (maxAbs > 1e6 && mn != null)
            {
                G.SuggestedOffset = new double[3];
                for (int k = 0; k < 3; k++) G.SuggestedOffset[k] = Math.Round((mn[k] + mx[k]) / 2.0 / 1000.0) * 1000.0;
                Warn("coordinates reach " + F(maxAbs) + " (float32 keeps about 7 digits): subtract SuggestedOffset (" +
                     F(G.SuggestedOffset[0]) + ", " + F(G.SuggestedOffset[1]) + ", " + F(G.SuggestedOffset[2]) + ") before writing");
            }
        }
    }
}
