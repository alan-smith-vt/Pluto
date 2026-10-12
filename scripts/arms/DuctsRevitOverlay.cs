using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

// Revit connectivity overlay (2026-10-09): a "Pluto Connectors" run from the Revit add-in (scripts/revit:
// elements.csv + connectors.csv, Revit internal feet) drawn in the Navisworks frame beside a Pluto Fab / Ducts
// run, for one room (or a list), to compare Revit's own connectivity with the Navisworks fabrication parts.
//   Match:  Revit IfcGUID (the parameter, else computed from UniqueId as the IFC / NWC exporters do) = Navisworks
//           IfcGUID. IfcGUIDs can be shared (fabrication assemblies), so the frame is fitted on 1:1 GUIDs only
//           (all matched GUIDs, by group means, when fewer than 20).
//   Frame:  rotation about Z + translation fitted on the matched element centres (Revit: mean of connector
//           origins; Navisworks: bbox centre), trimmed twice (residual > max(0.5 ft, 3 x median) dropped).
//   Scope:  Revit elements whose GUID is on a Navisworks row in the room and whose centre lies inside the room's
//           box (+3 ft), plus Revit elements inside the room's box (+1 ft) with no Navisworks match ("Revit only").
//   Drawing: one beam per element between its connectors (2), else a star from their mean; connector nodes
//           shared across Revit connections and touching ends (within 0.02 ft). Node groups: connected,
//           connected to an element outside the scope, touching (drawn end to end, not connected), open.
// Writes <out>.bin + .features.json + <out>.match.txt (fit, counts, Navisworks rows with no Revit element,
// Revit-only elements).
public partial class DuctsToPluto
{
    class RvEl
    {
        public string Id, Uid, Guid, GuidCalc, Cat, Fam, Type, Size, System, Level, Match = "", RunName = "";   // RunName: Revit parameter (runs from 2026-10-12 on)
        public bool Fab, Only;
        public int LocRow = -1;   // Revit-only: the Navisworks-only row it lies inside (same part, other IfcGUID)
        public List<RvConn> Conns = new List<RvConn>();
        public List<int> Beams = new List<int>();
        public List<int[]> Segs = new List<int[]>();   // SAP frames: { node i, node j, connector whose size it takes (-1 = the element's) }
        public List<string> Flags = new List<string>(), Detail = new List<string>();
        public double[] C, P;     // centre: Revit internal / Navisworks frame (feet)
        public string Kind = "";  // DuctsRunGraph: straight / bend / transition / branch / end / equipment
        public int Run = -1, Seg = -1;   // DuctsRunGraph run and segment ids (-1 = none)
        public int Sys = -1; public string SysName = "";   // DuctsSystems: connected component and its name
        public double[] Voted;    // the segment's voted size { round 1/0, big side or dia, small side } (null = none)
    }

    class RvConn
    {
        public RvEl Owner;
        public string Key, Type;
        public double[] P, D;     // Navisworks frame after the fit; D = outward (BasisZ)
        public double[] WX;       // Width direction (BasisX; runs from 2026-10-12 on, else null), after the fit
        public string Shape;      // Rectangular / Round / Oval
        public double W, H, Dia;  // inches, NaN = n/a
        public List<string> To = new List<string>();
        public Opening Mesh;      // the probe's connector on the matched Navisworks row at this end (mesh-measured size)
        public int MeshRow = -1;  // the row (0-based) that mesh connector belongs to
    }

    // As a probe Opening, to reuse ThroughPair / BranchJunction (round = "round", else A x B, A the larger).
    static Opening AsOpening(RvConn k)
    {
        var o = new Opening(); o.C = k.P; o.N = k.D;
        if (k.Shape == "Round") { o.Shape = "round"; o.Dia = k.Dia; }
        else { o.Shape = "rect"; o.A = Math.Max(k.W, k.H); o.B = Math.Min(k.W, k.H); }
        return o;
    }

    static double HalfSizeFt(RvConn k)
    {
        double s = k.Shape == "Round" ? k.Dia : Math.Max(k.W, k.H);
        return double.IsNaN(s) ? 0.5 : s / 24;
    }

    const double RvTouch = 0.02;   // ft, as the add-in's "touching"

    public static Result ExportRevit(string navisRun, string revitRun, string outBase, string modelId, string lengthUnit, string room)
    {
        return ExportRevit(navisRun, revitRun, outBase, modelId, lengthUnit, room, null);
    }

    public static Result ExportRevit(string navisRun, string revitRun, string outBase, string modelId, string lengthUnit, string room, string part)
    {
        double scale = LengthScale(lengthUnit);
        var rows = ReadRows(Path.Combine(navisRun, "ducts.csv"));
        bool byRoom = !string.IsNullOrEmpty(room);
        if (byRoom && !rows.Exists(r => r.Room != ""))
            throw new Exception("DuctsToPluto: ducts.csv has no Room values (run Pluto Fab / Ducts with the 2026-10-08 build)");

        // Navisworks rows by IfcGUID (rows with a bbox)
        var navByGuid = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int i = 0; i < rows.Count; i++)
        {
            Row r = rows[i]; if (r.Guid == "" || r.Min == null || r.Max == null) continue;
            List<int> l; if (!navByGuid.TryGetValue(r.Guid, out l)) { l = new List<int>(); navByGuid[r.Guid] = l; } l.Add(i);
        }

        // Revit elements + connectors
        var els = new Dictionary<string, RvEl>(StringComparer.Ordinal);
        foreach (Dictionary<string, string> c in ReadCsv(Path.Combine(revitRun, "elements.csv")))
        {
            var e = new RvEl();
            e.Id = Get(c, "ElementId"); e.Uid = Get(c, "UniqueId"); e.Guid = Get(c, "IfcGUID"); e.GuidCalc = IfcGuidFromUniqueId(e.Uid);
            e.Cat = Get(c, "Category"); e.Fam = Get(c, "Family"); e.Type = Get(c, "Type"); e.Size = Get(c, "Size");
            e.System = Get(c, "SystemName"); e.Level = Get(c, "Level"); e.Fab = Get(c, "IsFabrication") == "1"; e.RunName = Get(c, "RunName");
            els[e.Id] = e;
        }
        var conns = new Dictionary<string, RvConn>(StringComparer.Ordinal); int droppedCurve = 0;
        foreach (Dictionary<string, string> c in ReadCsv(Path.Combine(revitRun, "connectors.csv")))
        {
            RvEl e; if (!els.TryGetValue(Get(c, "ElementId"), out e)) continue;
            double x = Num(c, "X_ft"), y = Num(c, "Y_ft"), z = Num(c, "Z_ft");
            if (double.IsNaN(x) || double.IsNaN(y) || double.IsNaN(z)) continue;
            var k = new RvConn();
            k.Owner = e; k.Key = e.Id + ":" + Get(c, "ConnectorId"); k.Type = Get(c, "ConnType");
            k.P = new[] { x, y, z };
            double dx = Num(c, "DirX"), dy = Num(c, "DirY"), dz = Num(c, "DirZ");
            k.D = double.IsNaN(dx) ? new double[] { 0, 0, 0 } : new[] { dx, dy, dz };
            double wx = Num(c, "WidthDirX"), wy = Num(c, "WidthDirY"), wz = Num(c, "WidthDirZ");
            if (!double.IsNaN(wx) && !double.IsNaN(wy) && !double.IsNaN(wz)) k.WX = new[] { wx, wy, wz };
            k.Shape = Get(c, "Shape"); k.W = Num(c, "Width_in"); k.H = Num(c, "Height_in"); k.Dia = Num(c, "Diameter_in");
            foreach (string t in Get(c, "ConnectedTo").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)) k.To.Add(t);
            // a Curve connector (point on a part's side) connected to nothing: dropped, the part stays a plain
            // straight (test room: fab straights carry them at transitions; drawn as stubs to nothing, 2026-10-12)
            if (IsTapPoint(k) && k.To.Count == 0) { droppedCurve++; continue; }
            e.Conns.Add(k); conns[k.Key] = k;
        }
        int noConn = 0;
        foreach (RvEl e in els.Values)
        {
            if (e.Conns.Count == 0) { noConn++; continue; }
            e.C = new double[3];
            foreach (RvConn k in e.Conns) for (int a = 0; a < 3; a++) e.C[a] += k.P[a] / e.Conns.Count;
            if (navByGuid.ContainsKey(e.Guid)) e.Match = e.Guid;
            else if (navByGuid.ContainsKey(e.GuidCalc)) e.Match = e.GuidCalc;
        }
        int byParam = 0, byCalc = 0;
        var rvByGuid = new Dictionary<string, List<RvEl>>(StringComparer.Ordinal);
        foreach (RvEl e in els.Values)
        {
            if (e.Match == "" || e.C == null) continue;
            if (e.Match == e.Guid) byParam++; else byCalc++;
            List<RvEl> l; if (!rvByGuid.TryGetValue(e.Match, out l)) { l = new List<RvEl>(); rvByGuid[e.Match] = l; } l.Add(e);
        }
        if (rvByGuid.Count < 3)
            throw new Exception(string.Format(Inv, "DuctsToPluto: only {0} IfcGUIDs shared by the Revit and Navisworks runs (Revit elements {1}, Navisworks GUIDs {2}): is the Revit model the source of this Navisworks model?",
                rvByGuid.Count, els.Count, navByGuid.Count));

        // fit pairs: 1:1 GUIDs, else group means
        var pr = new List<double[]>(); var pn = new List<double[]>();
        Func<int, double[]> bc = i => new[] { (rows[i].Min[0] + rows[i].Max[0]) / 2, (rows[i].Min[1] + rows[i].Max[1]) / 2, (rows[i].Min[2] + rows[i].Max[2]) / 2 };
        foreach (KeyValuePair<string, List<RvEl>> kv in rvByGuid)
            if (kv.Value.Count == 1 && navByGuid[kv.Key].Count == 1) { pr.Add(kv.Value[0].C); pn.Add(bc(navByGuid[kv.Key][0])); }
        bool means = pr.Count < 20;
        if (means)
        {
            pr.Clear(); pn.Clear();
            foreach (KeyValuePair<string, List<RvEl>> kv in rvByGuid)
            {
                double[] a = new double[3], b = new double[3];
                foreach (RvEl e in kv.Value) for (int q = 0; q < 3; q++) a[q] += e.C[q] / kv.Value.Count;
                foreach (int i in navByGuid[kv.Key]) { double[] m = bc(i); for (int q = 0; q < 3; q++) b[q] += m[q] / navByGuid[kv.Key].Count; }
                pr.Add(a); pn.Add(b);
            }
        }
        double medRes, spread; int used;
        double[] T = FitRigidXY(pr, pn, out medRes, out used, out spread);
        Func<double[], double[]> xf = p => new[] { T[0] * p[0] - T[1] * p[1] + T[2], T[1] * p[0] + T[0] * p[1] + T[3], p[2] + T[4] };
        foreach (RvEl e in els.Values) if (e.C != null) e.P = xf(e.C);
        foreach (RvConn k in conns.Values)
        {
            k.P = xf(k.P); k.D = new[] { T[0] * k.D[0] - T[1] * k.D[1], T[1] * k.D[0] + T[0] * k.D[1], k.D[2] };
            if (k.WX != null) k.WX = new[] { T[0] * k.WX[0] - T[1] * k.WX[1], T[1] * k.WX[0] + T[0] * k.WX[1], k.WX[2] };
        }

        // scope
        var inRoom = new List<int>();
        for (int i = 0; i < rows.Count; i++) if (rows[i].Min != null && rows[i].Max != null && (!byRoom || InRooms(rows[i].Room, room))) inRoom.Add(i);
        if (inRoom.Count == 0) throw new Exception("DuctsToPluto: no Navisworks rows with a bbox" + (byRoom ? " in room(s) " + room : ""));
        double[] lo = { double.MaxValue, double.MaxValue, double.MaxValue }, hi = { double.MinValue, double.MinValue, double.MinValue };
        var roomGuids = new HashSet<string>(StringComparer.Ordinal);
        foreach (int i in inRoom)
        {
            for (int a = 0; a < 3; a++) { lo[a] = Math.Min(lo[a], rows[i].Min[a]); hi[a] = Math.Max(hi[a], rows[i].Max[a]); }
            if (rows[i].Guid != "") roomGuids.Add(rows[i].Guid);
        }
        Func<double[], double, bool> inBox = (p, m) => p[0] >= lo[0] - m && p[0] <= hi[0] + m && p[1] >= lo[1] - m && p[1] <= hi[1] + m && p[2] >= lo[2] - m && p[2] <= hi[2] + m;
        var scope = new List<RvEl>(); int guidOutside = 0;
        foreach (RvEl e in els.Values)
        {
            if (e.P == null) continue;
            if (e.Match != "" && roomGuids.Contains(e.Match)) { if (inBox(e.P, 3)) scope.Add(e); else guidOutside++; }
            else if (e.Match == "" && inBox(e.P, 1)) { e.Only = true; scope.Add(e); }
        }
        var inScope = new HashSet<RvEl>(scope);
        var matchedGuids = new HashSet<string>(StringComparer.Ordinal);
        foreach (RvEl e in scope) if (e.Match != "") matchedGuids.Add(e.Match);
        var navMissing = new List<int>();
        foreach (int i in inRoom) if (rows[i].Guid == "" || !matchedGuids.Contains(rows[i].Guid)) navMissing.Add(i);
        // Revit-only elements located in a Navisworks-only row: its centre inside that row's bbox (+0.1 ft), the
        // smallest such box. Same part, different IfcGUID.
        foreach (RvEl e in scope)
        {
            if (!e.Only) continue;
            double bv = double.MaxValue;
            foreach (int i in navMissing)
            {
                Row r = rows[i]; bool inside = true;
                for (int a = 0; a < 3; a++) if (e.P[a] < r.Min[a] - 0.1 || e.P[a] > r.Max[a] + 0.1) { inside = false; break; }
                if (!inside) continue;
                double v = (r.Max[0] - r.Min[0] + 0.1) * (r.Max[1] - r.Min[1] + 0.1) * (r.Max[2] - r.Min[2] + 0.1);
                if (v < bv) { bv = v; e.LocRow = i; }
            }
        }

        // touching open ends (in scope)
        var touch = new Dictionary<RvConn, RvConn>();
        var grid = new Dictionary<string, List<RvConn>>();
        Func<double, int, long> cell = (v, o) => (long)Math.Floor(v / RvTouch) + o;
        var open = new List<RvConn>();
        foreach (RvEl e in scope) foreach (RvConn k in e.Conns) if (k.To.Count == 0 && k.Type == "End") open.Add(k);
        foreach (RvConn k in open)
        {
            string key = cell(k.P[0], 0) + "," + cell(k.P[1], 0) + "," + cell(k.P[2], 0);
            List<RvConn> l; if (!grid.TryGetValue(key, out l)) { l = new List<RvConn>(); grid[key] = l; } l.Add(k);
        }
        foreach (RvConn k in open)
        {
            RvConn best = null; double bd = RvTouch * RvTouch;
            for (int i = -1; i <= 1; i++) for (int j = -1; j <= 1; j++) for (int m = -1; m <= 1; m++)
            {
                List<RvConn> l; if (!grid.TryGetValue(cell(k.P[0], i) + "," + cell(k.P[1], j) + "," + cell(k.P[2], m), out l)) continue;
                foreach (RvConn q in l)
                {
                    if (q.Owner == k.Owner) continue;
                    double d = Dist2(k.P, q.P); if (d <= bd) { bd = d; best = q; }
                }
            }
            if (best != null) touch[k] = best;
        }
        // attachments (2026-10-12, test room: an angle flange / support accessory with one connector
        // sat 0.1 in from a bend's end, unconnected, and drew a side-branch link to the main): a ONE-connector part
        // whose open end lies within AttachIn of any other part's connector shares that connector's joint
        const double AttachIn = 1;
        var attach = new Dictionary<RvConn, RvConn>();
        foreach (RvConn k in open)
        {
            if (k.Owner.Conns.Count != 1 || touch.ContainsKey(k)) continue;
            RvConn best = null; double bd = AttachIn / 12;
            foreach (RvEl e2 in scope)
            {
                if (e2 == k.Owner) continue;
                foreach (RvConn q in e2.Conns) { double d = Math.Sqrt(Dist2(k.P, q.P)); if (d < bd) { bd = d; best = q; } }
            }
            if (best != null) attach[k] = best;
        }

        // nodes: a connector shares its node with the connectors it is connected (or touching) to
        double[] off = { Math.Round((lo[0] + hi[0]) / 2), Math.Round((lo[1] + hi[1]) / 2), Math.Round((lo[2] + hi[2]) / 2) };
        var nodes = new Dictionary<int, Node>(); int nextNode = 1;
        var nodeOf = new Dictionary<string, int>(StringComparer.Ordinal);
        var gConn = new List<uint>(); var gOut = new List<uint>(); var gTouch = new List<uint>(); var gOpen = new List<uint>();
        Func<RvConn, int> node = null;
        node = delegate(RvConn k)
        {
            int id; if (nodeOf.TryGetValue(k.Key, out id)) return id;
            RvConn aq; if (attach.TryGetValue(k, out aq)) { id = node(aq); nodeOf[k.Key] = id; return id; }
            id = MakeNode(nodes, ref nextNode, k.P, off, scale); nodeOf[k.Key] = id;
            foreach (string t in k.To) { RvConn q; if (conns.TryGetValue(t, out q) && inScope.Contains(q.Owner) && !nodeOf.ContainsKey(t)) nodeOf[t] = id; }
            RvConn tq; if (touch.TryGetValue(k, out tq) && !nodeOf.ContainsKey(tq.Key)) nodeOf[tq.Key] = id;
            return id;
        };
        var sections = new List<RawViewerWriter.SectionDef>();
        sections.Add(RawViewerWriter.SectionDef.Pipe("revit part", (float)(3 * scale / 12), (float)(0.5 * scale / 12)));
        var beams = new Dictionary<int, RawViewerWriter.BeamMember>(); var labels = new Dictionary<int, string>(); int nb = 1;
        var byCat = new SortedDictionary<string, List<uint>>(); var gOnly = new List<uint>();
        var mains = new List<RvConn[]>();   // each part's main line (its 2 connectors / through pair), for side branches
        var offsetStubs = new HashSet<int>();   // beams of eccentric parts' sideways offset (seg code -3): thin, SAP rigid
        var navName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (int i in inRoom) if (rows[i].Guid != "" && !navName.ContainsKey(rows[i].Guid)) navName[rows[i].Guid] = "#" + rows[i].NavisId;
        foreach (RvEl e in scope)
        {
            string nav; navName.TryGetValue(e.Match, out nav);
            string label = "Revit " + e.Id + " | " + e.Cat + " | " + (e.Fam + " " + e.Type).Trim() + " | " + e.Size + " | " + e.System
                + " | " + e.Conns.Count + " conn | " + (e.Only ? "NO NAVISWORKS MATCH" : "IfcGUID " + e.Match + " Navisworks " + (nav ?? "(other room)"));
            var ids = new List<int>();
            if (e.Conns.Count == 2)
            {
                // a bend: two legs meeting at its corner (where the two connector axes meet), not the chord across it
                // (2026-10-12, test room: tap - 90 - 90 - straight drew as a zig-zag of chords)
                double[] corner = BendCorner(e.Conns[0], e.Conns[1]);
                int ecc = EccentricOutlet(e.Conns[0], e.Conns[1]);
                if (corner == null && ecc >= 0)
                {
                    // ends facing opposite ways but offset sideways (a shoe tap's inlet sits at its footprint's
                    // centre; eccentric reducer): the part along the OUTLET's axis back to the inlet's plane, the
                    // sideways offset to the inlet a rigid stub (2026-10-12, test room: the tap drew tilted into the main)
                    RvConn o = e.Conns[ecc], i = e.Conns[1 - ecc];
                    double back = (o.P[0] - i.P[0]) * o.D[0] + (o.P[1] - i.P[1]) * o.D[1] + (o.P[2] - i.P[2]) * o.D[2];
                    double[] foot = { o.P[0] - back * o.D[0], o.P[1] - back * o.D[1], o.P[2] - back * o.D[2] };
                    int fn = MakeNode(nodes, ref nextNode, foot, off, scale);
                    ids.Add(RvBeam(beams, labels, ref nb, fn, node(o), nodes, label));
                    e.Segs.Add(new[] { fn, node(o), -1 });
                    int sid = RvBeam(beams, labels, ref nb, node(i), fn, nodes, label + " | offset stub");
                    ids.Add(sid); offsetStubs.Add(sid);
                    e.Segs.Add(new[] { node(i), fn, -3 });
                }
                else if (corner != null)
                {
                    int cn = MakeNode(nodes, ref nextNode, corner, off, scale);
                    ids.Add(RvBeam(beams, labels, ref nb, node(e.Conns[0]), cn, nodes, label));
                    ids.Add(RvBeam(beams, labels, ref nb, cn, node(e.Conns[1]), nodes, label));
                    e.Segs.Add(new[] { node(e.Conns[0]), cn, -1 });
                    e.Segs.Add(new[] { cn, node(e.Conns[1]), -1 });
                }
                else
                {
                    ids.Add(RvBeam(beams, labels, ref nb, node(e.Conns[0]), node(e.Conns[1]), nodes, label));
                    e.Segs.Add(new[] { node(e.Conns[0]), node(e.Conns[1]), -1 });
                }
                mains.Add(new[] { e.Conns[0], e.Conns[1] });
            }
            else if (e.Conns.Count > 2)
            {
                // as the probe: the through pair as one line, each other connector along its own axis to where
                // it meets that line (a star from the mean zig-zagged tees and laterals)
                var ops = e.Conns.ConvertAll(AsOpening);
                int[] thr = ThroughPair(ops);
                if (thr != null)
                {
                    RvConn a = e.Conns[thr[0]], b = e.Conns[thr[1]];
                    ids.Add(RvBeam(beams, labels, ref nb, node(a), node(b), nodes, label));
                    mains.Add(new[] { a, b });
                    // SAP: the through line split at each branch junction (sorted along it), branches to their connector
                    var junc = new List<KeyValuePair<double, int>>();
                    for (int q = 0; q < e.Conns.Count; q++)
                        if (q != thr[0] && q != thr[1])
                        {
                            double[] f = BranchJunction(a.P, b.P, ops[q]);
                            int jn = MakeNode(nodes, ref nextNode, f, off, scale);
                            ids.Add(RvBeam(beams, labels, ref nb, jn, node(e.Conns[q]), nodes, label));
                            e.Segs.Add(new[] { jn, node(e.Conns[q]), q });
                            junc.Add(new KeyValuePair<double, int>(Dist2(a.P, f), jn));
                        }
                    junc.Sort((u, v) => u.Key.CompareTo(v.Key));
                    int prev = node(a);
                    foreach (KeyValuePair<double, int> j in junc) { e.Segs.Add(new[] { prev, j.Value, -1 }); prev = j.Value; }
                    e.Segs.Add(new[] { prev, node(b), -1 });
                }
                else
                {
                    int hub = MakeNode(nodes, ref nextNode, e.P, off, scale);
                    for (int q = 0; q < e.Conns.Count; q++)
                    {
                        ids.Add(RvBeam(beams, labels, ref nb, hub, node(e.Conns[q]), nodes, label));
                        e.Segs.Add(new[] { hub, node(e.Conns[q]), q });
                    }
                }
            }
            else node(e.Conns[0]);
            string g = "Revit " + (e.Fab ? "fabrication" : e.Cat);
            List<uint> gl; if (!byCat.TryGetValue(g, out gl)) { gl = new List<uint>(); byCat[g] = gl; }
            foreach (int id in ids) if (id > 0) { gl.Add((uint)id); e.Beams.Add(id); if (e.Only) gOnly.Add((uint)id); }
        }
        // Links between open ends that are neither connected nor touching (as the probe's):
        //   near (gap / overlap): another element's open end facing it (directions opposite, dot < -0.9), on its
        //     axis (lateral <= 1 in), within 1 ft; labelled with the signed gap
        //   side branch: the end's axis meets another part's main line inside the segment (not within 1 in of
        //     its ends), the end lying within that part's half size + 3 in of the line
        var gNear = new List<uint>(); var gSide = new List<uint>();
        var linked = new HashSet<RvConn>(); var nearSet = new HashSet<RvConn>(); var nearPairs = new List<RvConn[]>(); var sidePairs = new List<RvEl[]>();
        foreach (KeyValuePair<RvConn, RvConn> kv in attach) nearPairs.Add(new[] { kv.Key, kv.Value });   // systems: attached = joined
        var loose = new List<RvConn>();
        foreach (RvConn k in open) if (!touch.ContainsKey(k) && !attach.ContainsKey(k)) loose.Add(k);
        foreach (RvConn k in loose)
        {
            if (linked.Contains(k)) continue;
            RvConn best = null; double bd = 1.0, bAlong = 0;
            foreach (RvConn q in loose)
            {
                if (q == k || q.Owner == k.Owner || linked.Contains(q)) continue;
                if (k.D[0] * q.D[0] + k.D[1] * q.D[1] + k.D[2] * q.D[2] > -0.9) continue;
                double d = Math.Sqrt(Dist2(k.P, q.P)); if (d > bd) continue;
                double along = (q.P[0] - k.P[0]) * k.D[0] + (q.P[1] - k.P[1]) * k.D[1] + (q.P[2] - k.P[2]) * k.D[2];
                if (Math.Sqrt(Math.Max(0, d * d - along * along)) * 12 > 1) continue;
                bd = d; best = q; bAlong = along;
            }
            if (best == null) continue;
            linked.Add(k); linked.Add(best); nearSet.Add(k); nearSet.Add(best); nearPairs.Add(new[] { k, best });
            int id = RvBeam(beams, labels, ref nb, node(k), node(best), nodes, string.Format(Inv, "near: {0} {1:0.#} in | Revit {2} -> {3}",
                bAlong >= 0 ? "gap" : "overlap", Math.Abs(bAlong) * 12, k.Owner.Id, best.Owner.Id));
            if (id > 0) gNear.Add((uint)id);
        }
        foreach (RvConn k in loose)
        {
            if (linked.Contains(k)) continue;
            double[] bestF = null; RvConn[] bestM = null; double bd = double.MaxValue;
            foreach (RvConn[] m in mains)
            {
                if (m[0].Owner == k.Owner) continue;
                double[] f = BranchJunction(m[0].P, m[1].P, AsOpening(k));
                if (Math.Sqrt(Dist2(f, m[0].P)) * 12 < 1 || Math.Sqrt(Dist2(f, m[1].P)) * 12 < 1) continue;
                double d = Math.Sqrt(Dist2(f, k.P));
                if (d > HalfSizeFt(m[0]) + 0.25 || d >= bd) continue;
                bd = d; bestF = f; bestM = m;
            }
            if (bestF == null) continue;
            linked.Add(k); sidePairs.Add(new[] { k.Owner, bestM[0].Owner });
            int id = RvBeam(beams, labels, ref nb, node(k), MakeNode(nodes, ref nextNode, bestF, off, scale), nodes,
                string.Format(Inv, "side branch: Revit {0} -> side of {1} | {2:0.#} in from its line", k.Owner.Id, bestM[0].Owner.Id, bd * 12));
            if (id > 0) gSide.Add((uint)id);
        }

        int nConnected = 0, nOutside = 0, nTouch = 0, nOpen = 0, nNear = 0, nSide = 0;
        var seen = new HashSet<int>();
        foreach (RvEl e in scope)
            foreach (RvConn k in e.Conns)
            {
                int id = node(k); if (!seen.Add(id)) continue;
                bool inside = false, outside = false;
                foreach (string t in k.To) { RvConn q; if (conns.TryGetValue(t, out q) && inScope.Contains(q.Owner)) inside = true; else outside = true; }
                if (inside) { gConn.Add((uint)id); nConnected++; }
                else if (outside) { gOut.Add((uint)id); nOutside++; }
                else if (touch.ContainsKey(k)) { gTouch.Add((uint)id); nTouch++; }
                else if (attach.ContainsKey(k)) { }   // shares another part's joint, counted there
                else if (nearSet.Contains(k)) nNear++;
                else if (linked.Contains(k)) nSide++;
                else if (k.Type == "End") { gOpen.Add((uint)id); nOpen++; }
            }

        // Discrepancy flags (2026-10-12, vault handoff Pluto 2026-10-12): computed facts only, per Revit element in
        // scope; Navisworks-only rows nobody lies inside get NAVIS_ONLY in flags.csv (not drawn here).
        var partners = new Dictionary<int, int>();   // Navisworks row -> Revit elements on it (GUID or position)
        Func<RvEl, List<int>> rowsOf = e => e.Match != "" ? navByGuid[e.Match] : e.LocRow >= 0 ? new List<int> { e.LocRow } : new List<int>();
        foreach (RvEl e in scope) foreach (int i in rowsOf(e)) { int n; partners.TryGetValue(i, out n); partners[i] = n + 1; }
        string meshNote = MatchMeshConnectors(navisRun, rows, scope, rowsOf);
        foreach (RvEl e in scope)
        {
            List<int> pr2 = rowsOf(e);
            if (e.Only && e.LocRow < 0) e.Flags.Add("REVIT_ONLY");
            if (e.Only && e.LocRow >= 0) { e.Flags.Add("GUID_MISMATCH"); e.Detail.Add("Navisworks GUID " + rows[e.LocRow].Guid); }
            if (pr2.Count > 0)
            {
                bool navFabAny = pr2.Exists(i => rows[i].Cat == Fab);
                if (e.Fab != navFabAny) { e.Flags.Add("FAB_CATEGORY_MISMATCH"); e.Detail.Add("Revit " + (e.Fab ? "fabrication" : e.Cat) + " vs Navisworks " + rows[pr2[0]].Cat); }
                if (pr2.Count > 1 || partners[pr2[0]] > 1) { e.Flags.Add("SPLIT"); e.Detail.Add(string.Format(Inv, "{0} Navisworks : {1} Revit", pr2.Count, partners[pr2[0]])); }
                // 2026-10-12: Revit connector = duct (free) size = mesh rim in a test room; the Navisworks stated size
                // differs on most fab parts (16x14 vs free 14x12). SIZE_DIFFERS = Revit vs mesh (geometry);
                // NAVIS_SIZE_DIFFERS = Navisworks stated vs Revit (documented, not a geometry problem).
                string md = MeshDiff(e);
                if (md != null) { e.Flags.Add("SIZE_DIFFERS"); e.Detail.Add(md); }
                string sd = SizeDiff(e, rows[NavRow(e, pr2)]);
                if (sd != null) { e.Flags.Add("NAVIS_SIZE_DIFFERS"); e.Detail.Add(sd); }
            }
            bool fOpen = false, fTouch = false, fNear = false;
            foreach (RvConn k in e.Conns)
            {
                if (k.To.Count > 0) continue;
                if (touch.ContainsKey(k)) fTouch = true;
                else if (nearSet.Contains(k)) fNear = true;
                else if (!linked.Contains(k) && !attach.ContainsKey(k) && k.Type == "End") fOpen = true;
            }
            if (fOpen) e.Flags.Add("OPEN_END");
            if (fTouch) e.Flags.Add("TOUCHING_NOT_CONNECTED");
            if (fNear) e.Flags.Add("NEAR_GAP");
        }
        // runs, segments and size voting (DuctsRunGraph.cs): adds SIZE_OUTLIER, SIZE_CHANGE_WITHOUT_TRANSITION,
        // RUNNAME_DEVIATION, SYSTEM_CHANGE and sets each part's Kind / Run / Seg / Voted
        List<RunSeg> runSegs = AnalyzeRuns(scope, rows, rowsOf, k => node(k), nodes, off, scale, nearPairs);
        // Navisworks-only rows a Revit part runs through (a point every 0.25 ft along its frames inside the row's
        // bbox + 0.1 ft) are duplicates of Revit parts (bends, reducers, accessories whose centre missed the box):
        // listed in flags.csv as NAVIS_ONLY with "covered by Revit <id>", no frame. The rest are frames.
        var navOnly = new List<int>(); var navCovered = new Dictionary<int, string>();
        foreach (int i in navMissing)
        {
            if (partners.ContainsKey(i)) continue;
            Row r = rows[i]; string by = null;
            foreach (RvEl e in scope)
            {
                foreach (int[] g in e.Segs)
                {
                    double[] a = NodeFt(nodes[g[0]], off, scale), b = NodeFt(nodes[g[1]], off, scale);
                    int n = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(Dist2(a, b)) / 0.25));
                    for (int s2 = 0; s2 <= n && by == null; s2++)
                    {
                        double t = (double)s2 / n; bool inside = true;
                        for (int ax = 0; ax < 3; ax++) { double v = a[ax] + (b[ax] - a[ax]) * t; if (v < r.Min[ax] - 0.1 || v > r.Max[ax] + 0.1) { inside = false; break; } }
                        if (inside) by = e.Id;
                    }
                    if (by != null) break;
                }
                if (by != null) break;
            }
            if (by != null) navCovered[i] = by; else navOnly.Add(i);
        }
        // Navisworks-only rows are SAP frames too (own joints, not connected): drawn so the overlay shows the SAP model
        var gNavOnly = new List<uint>(); var navBeamRow = new Dictionary<int, int>();
        foreach (int i in navOnly)
        {
            double[] p, q; NavAxis(rows[i], out p, out q);
            int id = RvBeam(beams, labels, ref nb, MakeNode(nodes, ref nextNode, p, off, scale), MakeNode(nodes, ref nextNode, q, off, scale), nodes,
                "Navisworks #" + rows[i].NavisId + " | " + rows[i].Cat + " | " + rows[i].Name + " | " + rows[i].SizeText + " | NO REVIT MATCH | FLAGS NAVIS_ONLY");
            if (id > 0) { gNavOnly.Add((uint)id); navBeamRow[id] = i; }
        }

        // Cross sections: each part's beams get the size its SAP frame gets (SegSize); a tee's through beam is
        // drawn whole (SAP splits it at the branch, same size). Links keep the thin "revit part" pipe.
        var secOf = new Dictionary<string, int>(StringComparer.Ordinal); int nOriented = 0; var gTap = new List<uint>();
        var beamDim = new Dictionary<int, double[]>();   // part beam -> { round 1/0, W, H, Dia } inches (DuctsSystems box test)
        Func<string, double, double, double, double, int> sec = (shape, w, h, d, t) =>
        {
            bool round = shape == "round" && !double.IsNaN(d);
            if (!round && (double.IsNaN(w) || double.IsNaN(h))) return 0;
            string name = round ? string.Format(Inv, "{0:0.##} dia", d) : string.Format(Inv, "{0:0.##}x{1:0.##}", w, h);
            int ix; if (secOf.TryGetValue(name, out ix)) return ix;
            double ft = scale / 12;
            sections.Add(round ? RawViewerWriter.SectionDef.Pipe(name, (float)(d * ft), (float)(Math.Min(t, d / 2) * ft))
                               : RawViewerWriter.SectionDef.Box(name, (float)(w * ft), (float)(h * ft), (float)(Math.Min(t, Math.Min(w, h) / 2) * ft)));
            ix = sections.Count - 1; secOf[name] = ix; return ix;
        };
        foreach (RvEl e in scope)
        {
            int nr = NavRow(e, rowsOf(e)); Row r = nr >= 0 ? rows[nr] : null;
            double t = r != null && !double.IsNaN(r.T) && r.T > 0 ? r.T : 0.06;
            var connOf = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (int[] g in e.Segs) if (g[2] >= 0) { connOf[g[0] + "-" + g[1]] = g[2]; connOf[g[1] + "-" + g[0]] = g[2]; }
            foreach (int id in e.Beams)
            {
                int q; if (!connOf.TryGetValue(beams[id].NodeA + "-" + beams[id].NodeB, out q)) q = -1;
                // tap point on a straight's side (Curve connector, 2026-10-12): the stub from the centreline is a
                // thin link (a box at the tap's size stood out of the duct as a plate); SAP: rigid stub
                if ((q >= 0 && IsTapPoint(e.Conns[q])) || offsetStubs.Contains(id)) { beams[id].SectionIndex = 0; gTap.Add((uint)id); continue; }
                string shape, ssrc; double w, h, d; RvConn src;
                SegSize(e, q, r, out shape, out w, out h, out d, out ssrc, out src);
                beams[id].SectionIndex = sec(shape, w, h, d, t);
                beamDim[id] = new[] { shape == "round" ? 1.0 : 0.0, w, h, d };
                // box width along the connector's width direction (Revit BasisX) where the run exported it
                if (src != null && src.WX != null && shape == "rect")
                {
                    double[] y = WidthAxis(nodes[beams[id].NodeA], nodes[beams[id].NodeB], src.WX);
                    if (y != null) { beams[id].LocalY = y; nOriented++; }
                }
            }
        }
        foreach (KeyValuePair<int, int> kv in navBeamRow)
        {
            Row r = rows[kv.Value]; string shape; double w, h, d;
            if (NavSize(r, out shape, out w, out h, out d))
                beams[kv.Key].SectionIndex = sec(shape, w, h, d, !double.IsNaN(r.T) && r.T > 0 ? r.T : 0.06);
        }

        // systems (DuctsSystems.cs): flood fill over connections, touching ends, near / side links and the drawn
        // boxes (a centreline inside another part's box + 6 in: overlaps, gaps, branches into a side); adds
        // JOINED_BY_PROXIMITY, sets e.Sys / e.SysName
        List<DuctSystem> systems = AnalyzeSystems(scope, conns, inScope, touch, nearPairs, sidePairs, beams, beamDim, nodes, off, scale, rows, rowsOf);
        WriteRunsCsv(outBase + ".runs.csv", runSegs);
        foreach (RvEl e in scope)
        {
            string extra = " | " + (e.Kind != "" ? e.Kind : "?")
                + (e.Sys >= 0 ? string.Format(Inv, " | system {0} (C{1}), own name {2}", e.SysName, e.Sys, NameSource(e, rows, rowsOf)) : "")
                + (e.Run >= 0 ? string.Format(Inv, " | line R{0} seg S{1}", e.Run, e.Seg) : "")
                + (e.Voted != null ? " voted " + VotedText(e.Voted) : "");
            foreach (int id in e.Beams) labels[id] += extra;
            if (e.Flags.Count > 0) foreach (int id in e.Beams) labels[id] += " | FLAGS " + string.Join(" ", e.Flags.ToArray());
        }
        var flagGroups = new SortedDictionary<string, List<uint>>(StringComparer.Ordinal);
        foreach (RvEl e in scope)
            foreach (string f in e.Flags)
            {
                List<uint> l; if (!flagGroups.TryGetValue(f, out l)) { l = new List<uint>(); flagGroups[f] = l; }
                foreach (int id in e.Beams) l.Add((uint)id);
            }
        if (gNavOnly.Count > 0) flagGroups["NAVIS_ONLY"] = gNavOnly;
        WriteFlagsCsv(outBase + ".flags.csv", scope, rows, rowsOf, navOnly, navCovered);
        string framesNote = WriteFramesCsv(outBase + ".frames.csv", scope, rows, rowsOf, navOnly, nodes, off, scale);

        var res = new Result();
        if (beams.Count > 0)
        {
            var comps = new List<RawViewerWriter.Component>();
            comps.Add(new RawViewerWriter.Component("Axial N", "force", "kip"));   // layout only; no planes written
            var units = new Dictionary<string, string>(); units["length"] = lengthUnit;
            var w = new RawViewerWriter(outBase + ".bin", nodes, null, null, null, beams, sections, comps, modelId, units);
            w.SetBeamLabels(labels);
            w.Write(false);
            var sc = new FeaturesSidecar();
            sc.ModelId = modelId; sc.GeometryHash = w.GeometryHash;
            sc.Units["length"] = lengthUnit;
            sc.Units["worldOffset"] = string.Format(Inv, "{0} {1} {2}", off[0] * scale, off[1] * scale, off[2] * scale);
            string[] pal = { "#ff8a3d", "#b07cff", "#3fe0e0", "#8ae04f", "#f0f0f0", "#ffd23f" };
            int pi = 0;
            foreach (KeyValuePair<string, List<uint>> kv in byCat)
                sc.AddGroup(kv.Key, kv.Key == "Revit fabrication" ? "#ff8a3d" : pal[1 + pi++ % (pal.Length - 1)], "beams", kv.Value, new[] { "duct", "revit", "category" }, null);
            if (gOnly.Count > 0) sc.AddGroup("Revit only (no Navisworks match)", "#ff3b3b", "beams", gOnly, new[] { "duct", "revit", "unmatched" }, "REVIT_ONLY");
            if (gNear.Count > 0) sc.AddGroup("Link: near (gap / overlap)", "#ffd23f", "beams", gNear, new[] { "duct", "revit", "link" }, null);
            if (gSide.Count > 0) sc.AddGroup("Link: side branch", "#8ae04f", "beams", gSide, new[] { "duct", "revit", "link" }, null);
            if (gTap.Count > 0) sc.AddGroup("Link: rigid stub (tap point / offset)", "#b07cff", "beams", gTap, new[] { "duct", "revit", "link" }, null);
            if (gConn.Count > 0) sc.AddNodeGroup("Revit joint: connected", "#3fc1a5", gConn, new[] { "duct", "revit", "joint" });
            if (gTouch.Count > 0) sc.AddNodeGroup("Revit joint: touching, not connected", "#ff9f1c", gTouch, new[] { "duct", "revit", "touching" });
            if (gOut.Count > 0) sc.AddNodeGroup("Revit joint: connected outside the room", "#4f8cff", gOut, new[] { "duct", "revit", "outside" });
            if (gOpen.Count > 0) sc.AddNodeGroup("Revit open end", "#ff3b3b", gOpen, new[] { "duct", "revit", "open" }, "REVIT_OPEN");
            // one group per run (hide all but one to follow it)
            string[] rpal = { "#4f8cff", "#ff8a3d", "#3fe0e0", "#b07cff", "#8ae04f", "#ffd23f", "#ff5fa2", "#f0f0f0" };
            // one group per system (connected component); lines / segments are in the label and runs.csv.
            // Single loose parts go together in one group so the list stays short.
            var looseParts = new List<uint>();
            foreach (DuctSystem g in systems)
            {
                var ids = new List<uint>(); foreach (RvEl e in g.Els) foreach (int id in e.Beams) ids.Add((uint)id);
                if (ids.Count == 0) continue;
                if (g.Els.Count == 1) { looseParts.AddRange(ids); continue; }
                sc.AddGroup(string.Format(Inv, "System {0} (C{1}, {2} parts)", g.Name, g.Id, g.Els.Count), rpal[(g.Id - 1) % rpal.Length], "beams", ids, new[] { "duct", "revit", "system" }, null);
            }
            if (looseParts.Count > 0) sc.AddGroup("System: single loose parts", "#ff3b3b", "beams", looseParts, new[] { "duct", "revit", "system" }, null);
            string[] fpal ={ "#ff5fa2", "#ffd23f", "#3fe0e0", "#b07cff", "#ff8a3d", "#8ae04f", "#4f8cff", "#ff3b3b", "#f0f0f0" };
            int fi = 0;
            foreach (KeyValuePair<string, List<uint>> kv in flagGroups)
                sc.AddGroup("Flag: " + kv.Key, fpal[fi++ % fpal.Length], "beams", kv.Value, new[] { "duct", "revit", "flag" }, "FLAG_" + kv.Key);
            File.WriteAllText(outBase + ".features.json", sc.ToJson(), new UTF8Encoding(false));
            res.BinPath = outBase + ".bin"; res.SidecarPath = outBase + ".features.json"; res.GeometryHash = w.GeometryHash;
        }
        res.Nodes = nodes.Count; res.Beams = beams.Count;

        // match report
        int navFab = 0, navFabMissing = 0;
        foreach (int i in inRoom) if (rows[i].Cat == Fab) { navFab++; if (navMissing.Contains(i)) navFabMissing++; }
        int rvFab = 0, rvOnly = 0, rvOnlyFab = 0; foreach (RvEl e in scope) { if (e.Fab) rvFab++; if (e.Only) { rvOnly++; if (e.Fab) rvOnlyFab++; } }
        // matched elements whose category differs (Revit fabrication vs a Navisworks Ducts / Fittings row, or back)
        var catDiff = new List<string>();
        foreach (RvEl e in scope)
        {
            if (e.Match == "") continue;
            foreach (int i in navByGuid[e.Match])
                if ((rows[i].Cat == Fab) != e.Fab || (!e.Fab && !string.Equals(rows[i].Cat, e.Cat, StringComparison.OrdinalIgnoreCase)))
                { catDiff.Add("  Revit " + e.Id + " " + e.Cat + " (" + (e.Fam + " " + e.Type).Trim() + ")  vs  Navisworks #" + rows[i].NavisId + " " + rows[i].Cat + " (" + rows[i].Name + ")"); break; }
        }
        // Revit-only elements located in a Navisworks-only row (LocRow), tallied by (Revit category -> Navisworks category)
        var located = new List<string>(); var pairTally = new SortedDictionary<string, int>(); var usedRow = new HashSet<int>();
        foreach (RvEl e in scope)
        {
            if (!e.Only || e.LocRow < 0) continue;
            int bi = e.LocRow;
            usedRow.Add(bi);
            string key = (e.Fab ? "Revit fabrication" : "Revit " + e.Cat) + " -> Navisworks " + rows[bi].Cat;
            int n; pairTally.TryGetValue(key, out n); pairTally[key] = n + 1;
            located.Add("  Revit " + e.Id + " " + (e.Fam + " " + e.Type).Trim() + " " + e.Size + " IfcGUID " + (e.Guid != "" ? e.Guid : e.GuidCalc)
                + "  in  Navisworks #" + rows[bi].NavisId + " " + rows[bi].Cat + " (" + rows[bi].Name + ") " + rows[bi].Guid);
        }
        var s = new StringBuilder();
        res.Note = string.Format(Inv,
            "NEW fit {0:0.00} ft median ({1} pairs{2}, rot {3:0.###} deg, scale check {4:0.###})\nNEW room: Navisworks {5} rows ({6} fab), Revit {7} el ({8} fab), Navisworks without Revit {9} ({10} fab), Revit only {11} ({12} fab), category differs {13}, Revit only located in a Navisworks-only part {20} (Navisworks parts {21})\nNEW joints: connected {14} touching {15} near {16} side {17} outside {18} open {19}",
            medRes, used, means ? " by GUID group" : " 1:1", Math.Atan2(T[1], T[0]) * 180 / Math.PI, spread,
            inRoom.Count, navFab, scope.Count, rvFab, navMissing.Count, navFabMissing, rvOnly, rvOnlyFab, catDiff.Count,
            nConnected, nTouch, nNear, nSide, nOutside, nOpen, located.Count, usedRow.Count);
        if (!string.IsNullOrEmpty(part))
        {
            // -Part <Revit ElementId> (2026-10-12): that part's connectors and what lies near each, as the NEW block
            RvEl pe = scope.Find(x => x.Id == part.Trim());
            res.Note = pe == null ? "NEW part " + part + ": not among the room's Revit elements" : PartReport(pe, scope, conns, inScope, touch, nearSet, linked, attach);
        }
        s.AppendLine(res.Note);
        var fc = new List<string>();
        foreach (KeyValuePair<string, List<uint>> kv in flagGroups)
        {
            if (kv.Key == "NAVIS_ONLY") continue;   // rows, not Revit elements: counted below
            int n = 0; foreach (RvEl e in scope) if (e.Flags.Contains(kv.Key)) n++;
            fc.Add(kv.Key + " " + n);
        }
        fc.Add(string.Format(Inv, "NAVIS_ONLY {0} (+ {1} covered by a Revit part, no frame)", navOnly.Count, navCovered.Count));
        s.AppendLine("flags (parts; <out>.flags.csv): " + string.Join(", ", fc.ToArray()));
        s.AppendLine(framesNote);
        s.AppendLine(meshNote);
        {
            int nRuns = runSegs.Select(g => g.Run).Distinct().Count(), nSeg = runSegs.Count(g => g.Kind == "segment"), nTr = runSegs.Count(g => g.Kind == "transition");
            var kinds = scope.GroupBy(e => e.Kind == "" ? "none" : e.Kind).OrderByDescending(g => g.Count()).Select(g => g.Key + " " + g.Count());
            s.AppendLine(string.Format(Inv, "runs (<out>.runs.csv): {0} runs, {1} segments + {2} transitions; parts: {3}", nRuns, nSeg, nTr, string.Join(", ", kinds.ToArray())));
        }
        string[] rnCheck = RunNameCheck(scope, rows, rowsOf);
        s.AppendLine(rnCheck[0]);
        string[] sysNote = SystemsNote(systems);
        s.AppendLine(sysNote[0]);
        s.AppendLine(string.Format(Inv, "boxes turned to the Revit connector's width direction: {0} beams{1}", nOriented,
            nOriented == 0 ? " (connectors.csv has no WidthDirX: a Pluto Connectors run from before 2026-10-12; boxes keep width horizontal)" : ""));
        s.AppendLine();
        s.AppendLine("Navisworks run: " + navisRun);
        s.AppendLine("Revit run:      " + revitRun);
        s.AppendLine("room(s):        " + (byRoom ? room : "(all)"));
        s.AppendLine(string.Format(Inv, "Revit elements {0} (no connectors {1}); matched by IfcGUID parameter {2}, by GUID computed from UniqueId {3}; shared GUIDs {4}",
            els.Count, noConn, byParam, byCalc, rvByGuid.Count));
        s.AppendLine(string.Format(Inv, "Curve connectors (point on a part's side) connected to nothing, dropped (whole model): {0}", droppedCurve));
        s.AppendLine(string.Format(Inv, "frame (Revit internal -> Navisworks): x' = {0:0.######} x - {1:0.######} y + {2:0.###}; y' = {1:0.######} x + {0:0.######} y + {3:0.###}; z' = z + {4:0.###}",
            T[0], T[1], T[2], T[3], T[4]));
        s.AppendLine("scale check = spread of Navisworks centres / spread of Revit centres (1 = same units)");
        s.AppendLine(string.Format(Inv, "Revit elements with a room GUID but centre outside the room box (+3 ft, dropped): {0}", guidOutside));
        s.AppendLine();
        s.AppendLine("Navisworks rows in the room with no Revit element (NavisId, category, name, IfcGUID):");
        foreach (int i in navMissing) s.AppendLine("  #" + rows[i].NavisId + "  " + rows[i].Cat + "  " + rows[i].Name + "  " + rows[i].Guid);
        s.AppendLine();
        s.AppendLine("Revit elements in the room box with no Navisworks row (ElementId, category, family type, size):");
        foreach (RvEl e in scope) if (e.Only) s.AppendLine("  " + e.Id + "  " + e.Cat + "  " + (e.Fam + " " + e.Type).Trim() + "  " + e.Size);
        s.AppendLine();
        s.AppendLine("Matched by IfcGUID, category differs (a Pluto Fab run holds only fabrication rows: use a Pluto Ducts run to see these):");
        foreach (string line in catDiff) s.AppendLine(line);
        s.AppendLine();
        s.AppendLine("Revit-only elements located inside a Navisworks-only part (same part, different IfcGUID?):");
        foreach (KeyValuePair<string, int> kv in pairTally) s.AppendLine(string.Format(Inv, "  {0,5}  {1}", kv.Value, kv.Key));
        foreach (string line in located) s.AppendLine(line);
        s.AppendLine();
        s.Append(rnCheck[1]); s.AppendLine(); s.Append(sysNote[1]);
        File.WriteAllText(outBase + ".match.txt", s.ToString(), new UTF8Encoding(false));
        return res;
    }

    // Revit connector sizes against the mesh-measured connector at the same end (MatchMeshConnectors), within
    // 1 in per side: null when every connector with a mesh size agrees (or none has one), else
    // "Revit <size> vs mesh <size>" per disagreeing end.
    static string MeshDiff(RvEl e)
    {
        var bad = new List<string>();
        foreach (RvConn k in e.Conns)
        {
            Opening o = k.Mesh; if (o == null || IsTapPoint(k)) continue;   // a tap point has no mesh end of its own
            // confirmed when ANY ring centred at that end matches (flange edge, duct rim further back, ...)
            bool ok = false;
            var rings = new List<string>();
            foreach (double[] c in o.Cands ?? new List<double[]> { new[] { o.Shape == "round" ? 1.0 : 0.0, o.A, o.B, o.Dia } })
            {
                if (k.Shape == "Round") ok |= c[0] == 1 && !double.IsNaN(k.Dia) && Math.Abs(k.Dia - c[3]) <= 1;
                else ok |= c[0] == 0 && !double.IsNaN(k.W) && !double.IsNaN(k.H)
                    && Math.Abs(Math.Max(k.W, k.H) - Math.Max(c[1], c[2])) <= 1 && Math.Abs(Math.Min(k.W, k.H) - Math.Min(c[1], c[2])) <= 1;
                rings.Add(c[0] == 1 ? string.Format(Inv, "{0:0.#} dia", c[3]) : string.Format(Inv, "{0:0.#}x{1:0.#}", c[1], c[2]));
            }
            if (!ok) bad.Add(string.Format(Inv, "Revit {0} vs mesh rings {1}", k.Shape == "Round" ? string.Format(Inv, "{0:0.#} dia", k.Dia) : string.Format(Inv, "{0:0.#}x{1:0.#}", k.W, k.H),
                string.Join(" ", rings.Distinct().ToArray())));
        }
        return bad.Count > 0 ? string.Join(" / ", bad.ToArray()) : null;
    }

    // Stated size of the Navisworks row (Width / Height / Diameter, else read from the Name) against the element's
    // Revit connector sizes: null when any connector matches within 0.5 in (eccentric transitions and tees carry
    // several sizes) or either side is unknown, else "Navisworks <size> vs Revit <sizes>".
    static string SizeDiff(RvEl e, Row r)
    {
        double w = !double.IsNaN(r.W) ? r.W : r.NameW, h = !double.IsNaN(r.H) ? r.H : r.NameH, d = !double.IsNaN(r.D) ? r.D : r.NameD;
        bool rect = !double.IsNaN(w) && !double.IsNaN(h), round = !rect && !double.IsNaN(d);
        if (!rect && !round) return null;
        var seen = new List<string>();
        foreach (RvConn k in e.Conns)
        {
            if (k.Shape == "Round")
            {
                if (double.IsNaN(k.Dia)) continue;
                if (round && Math.Abs(k.Dia - d) <= 0.5) return null;
                seen.Add(string.Format(Inv, "{0:0.#} dia", k.Dia));
            }
            else
            {
                if (double.IsNaN(k.W) || double.IsNaN(k.H)) continue;
                if (rect && Math.Abs(Math.Max(k.W, k.H) - Math.Max(w, h)) <= 0.5 && Math.Abs(Math.Min(k.W, k.H) - Math.Min(w, h)) <= 0.5) return null;
                seen.Add(string.Format(Inv, "{0:0.#}x{1:0.#}", k.W, k.H));
            }
        }
        if (seen.Count == 0) return null;
        var mesh = new List<string>(); foreach (RvConn k in e.Conns) mesh.Add(MeshSize(k.Mesh));
        return string.Format(Inv, "Navisworks {0} vs Revit {1} (mesh ends {2})", rect ? string.Format(Inv, "{0:0.#}x{1:0.#}", w, h) : string.Format(Inv, "{0:0.#} dia", d),
            string.Join("/", seen.Distinct().ToArray()), string.Join(" / ", mesh.ToArray()));
    }

    // <out>.flags.csv: one row per flagged part, Revit elements in scope, then Navisworks-only rows. Regenerated
    // every run; flags clear themselves when the upstream models are fixed.
    static void WriteFlagsCsv(string path, List<RvEl> scope, List<Row> rows, Func<RvEl, List<int>> rowsOf, List<int> navOnly, Dictionary<int, string> navCovered)
    {
        var s = new StringBuilder();
        s.AppendLine("Source,RevitElementId,NavisId,RevitIfcGUID,NavisIfcGUID,RevitCategory,RevitFab,NavisCategory,Match,RevitSize,NavisSize,Flags,Detail");
        foreach (RvEl e in scope)
        {
            if (e.Flags.Count == 0) continue;
            List<int> pr = rowsOf(e); int nr = NavRow(e, pr); Row r = nr >= 0 ? rows[nr] : null;
            string how = e.Match != "" ? "guid" : e.LocRow >= 0 ? "position" : "none";
            s.AppendLine(string.Join(",", new[] { "Revit", e.Id, r != null ? r.NavisId : "", e.Guid != "" ? e.Guid : e.GuidCalc, r != null ? r.Guid : "",
                e.Cat, e.Fab ? "1" : "0", r != null ? r.Cat : "", how, e.Size, r != null ? r.SizeText : "",
                string.Join(" ", e.Flags.ToArray()), string.Join("; ", e.Detail.ToArray()) }.Select(CsvCell).ToArray()));
        }
        foreach (int i in navOnly)
        {
            Row r = rows[i];
            s.AppendLine(string.Join(",", new[] { "Navisworks", "", r.NavisId, "", r.Guid, "", "", r.Cat, "none", "", r.SizeText, "NAVIS_ONLY", r.Name + "; SAP frame" }.Select(CsvCell).ToArray()));
        }
        foreach (KeyValuePair<int, string> kv in navCovered)
        {
            Row r = rows[kv.Key];
            s.AppendLine(string.Join(",", new[] { "Navisworks", kv.Value, r.NavisId, "", r.Guid, "", "", r.Cat, "covered", "", r.SizeText, "NAVIS_ONLY",
                r.Name + "; covered by Revit " + kv.Value + ", no frame" }.Select(CsvCell).ToArray()));
        }
        File.WriteAllText(path, s.ToString(), new UTF8Encoding(false));
    }

    // <out>.frames.csv, the SAP model's input (scripts\sap\Run-SapDucts.ps1): one frame per straight piece, inches,
    // Navisworks frame (absolute). Topology from Revit: joints are the Revit connectors (fitted onto Navisworks,
    // connected and touching ends share a joint), tees split at the branch junction; near / side links are NOT
    // frames (the gap stays open, flagged). Size: the Navisworks stated size of the matched row, else Revit's
    // connector; branch pieces take their own connector's size. Navisworks-only rows: their axis (X1..Z2, else
    // the bbox's long axis) between joints of their own (not connected). Elements with one connector (caps,
    // terminals) have no frame. Thickness: the row's WallThk_in, else 0.06 in (ThkSource "default").
    static string WriteFramesCsv(string path, List<RvEl> scope, List<Row> rows, Func<RvEl, List<int>> rowsOf, List<int> navOnly,
        Dictionary<int, Node> nodes, double[] off, double scale)
    {
        var s = new StringBuilder();
        s.AppendLine("Frame,JointI,JointJ,XI_in,YI_in,ZI_in,XJ_in,YJ_in,ZJ_in,Shape,W_in,H_in,Dia_in,Thk_in,ThkSource,SizeSource,Source,RevitElementId,NavisId,Flags");
        Func<int, double[]> at = id => new[] { (nodes[id].xyz.X / scale + off[0]) * 12, (nodes[id].xyz.Y / scale + off[1]) * 12, (nodes[id].xyz.Z / scale + off[2]) * 12 };
        int nf = 0, noFrame = 0, noSize = 0, nav = 0, nStub = 0;
        Action<string, string, double[], double[], string, double, double, double, double, string, string, string, string, string, string> line =
            (ji, jj, p, q, shape, w, h, d, t, tsrc, ssrc, src, rid, nid, flags) =>
            {
                nf++;
                s.AppendLine(string.Join(",", new[] { "F" + nf, ji, jj, R3(p[0]), R3(p[1]), R3(p[2]), R3(q[0]), R3(q[1]), R3(q[2]), shape,
                    R3(w), R3(h), R3(d), R3(t), tsrc, ssrc, src, rid, nid, flags }.Select(CsvCell).ToArray()));
            };
        foreach (RvEl e in scope)
        {
            if (e.Segs.Count == 0) { noFrame++; continue; }
            List<int> pr = rowsOf(e); int nr = NavRow(e, pr); Row r = nr >= 0 ? rows[nr] : null;
            double t = r != null && !double.IsNaN(r.T) && r.T > 0 ? r.T : 0.06;
            string flags = string.Join(" ", e.Flags.ToArray());
            foreach (int[] g in e.Segs)
            {
                if (g[0] == g[1]) continue;
                string shape, ssrc; double w, h, d;
                if ((g[2] >= 0 && IsTapPoint(e.Conns[g[2]])) || g[2] == -3) { shape = "rigid"; w = h = d = double.NaN; ssrc = "tap-stub"; nStub++; }
                else SegSize(e, g[2], r, out shape, out w, out h, out d, out ssrc);
                if (ssrc == "none") noSize++;
                line("N" + g[0], "N" + g[1], at(g[0]), at(g[1]), shape, w, h, d, t, t == 0.06 ? "default" : "navisworks", ssrc, e.Only && e.LocRow < 0 ? "revit-only" : "revit",
                    e.Id, r != null ? r.NavisId : "", flags);
            }
        }
        foreach (int i in navOnly)
        {
            Row r = rows[i]; double[] p, q;
            NavAxis(r, out p, out q);
            string shape; double w, h, d;
            string ssrc = NavSize(r, out shape, out w, out h, out d) ? "navisworks" : "none";
            if (ssrc == "none") noSize++;
            double t = !double.IsNaN(r.T) && r.T > 0 ? r.T : 0.06;
            nav++;
            line("V" + i + "a", "V" + i + "b", new[] { p[0] * 12, p[1] * 12, p[2] * 12 }, new[] { q[0] * 12, q[1] * 12, q[2] * 12 }, shape, w, h, d, t,
                t == 0.06 ? "default" : "navisworks", ssrc, "navisworks-only", "", r.NavisId, "NAVIS_ONLY");
        }
        File.WriteAllText(path, s.ToString(), new UTF8Encoding(false));
        return string.Format(Inv, "SAP frames (<out>.frames.csv): {0} ({1} from Navisworks-only rows), Revit elements without a frame (1 connector) {2}, frames without a size {3}, rigid tap stubs {4}",
            nf, nav, noFrame, noSize, nStub);
    }

    // Mesh-measured connector sizes (2026-10-12): the probe's connectors (MeshConnectors) on each matched
    // Navisworks row, any category (Revit fab parts are often Ducts / Fittings in Navisworks); each Revit connector
    // takes the row's mesh connector nearest it within MeshMatchIn, facing the same way (dot > 0.7).
    const double MeshMatchIn = 4;
    static string MatchMeshConnectors(string navisRun, List<Row> rows, List<RvEl> scope, Func<RvEl, List<int>> rowsOf)
    {
        var want = new HashSet<int>();
        foreach (RvEl e in scope) foreach (int i in rowsOf(e)) want.Add(i + 1);
        string triPath = Path.Combine(navisRun, "ducts_tri.bin");
        if (!File.Exists(triPath)) return "mesh connector sizes: no ducts_tri.bin in the Navisworks run";
        var tri = ReadTriangles(triPath, want);
        var meshOf = new Dictionary<int, List<Opening>>();
        int noTri = 0, dummy = 0;
        foreach (int row in want)
        {
            float[] f;
            if (!tri.TryGetValue(row, out f) || f.Length < 9) { noTri++; continue; }
            int small, interior, nEnds;
            meshOf[row - 1] = MeshConnectors(f, rows[row - 1], row, false, null, null, ref dummy, out small, out interior, out nEnds);
        }
        int total = 0, got = 0;
        foreach (RvEl e in scope)
            foreach (RvConn k in e.Conns)
            {
                total++;
                double bd = MeshMatchIn / 12;
                foreach (int i in rowsOf(e))
                {
                    List<Opening> ol; if (!meshOf.TryGetValue(i, out ol)) continue;
                    foreach (Opening o in ol)
                    {
                        if (o.N[0] * k.D[0] + o.N[1] * k.D[1] + o.N[2] * k.D[2] <= 0.7) continue;
                        double d = Math.Sqrt(Dist2(o.C, k.P)); if (d < bd) { bd = d; k.Mesh = o; k.MeshRow = i; }
                    }
                }
                if (k.Mesh != null) got++;
            }
        return string.Format(Inv, "mesh connector sizes: {0} of {1} Revit connectors matched a mesh connector within {2} in; matched Navisworks rows {3} ({4} without triangles)",
            got, total, MeshMatchIn, want.Count, noTri);
    }

    // The Navisworks row that stands for element e: the one its mesh connectors matched (an IfcGUID can be shared
    // by several rows of a fabrication assembly, of other sizes), else the first; -1 = none
    static int NavRow(RvEl e, List<int> pr)
    {
        foreach (RvConn k in e.Conns) if (k.MeshRow >= 0 && pr.Contains(k.MeshRow)) return k.MeshRow;
        return pr.Count > 0 ? pr[0] : -1;
    }

    static string MeshSize(Opening o)
    {
        if (o == null) return "-";
        return o.Shape == "round" ? string.Format(Inv, "{0:0.#} dia", o.Dia) : string.Format(Inv, "{0:0.#}x{1:0.#}", o.A, o.B);
    }

    // Size of one piece of a Revit element, from the Revit connectors (the duct's free size; agrees with the
    // mesh rim in a test room, 2026-10-12): a branch piece (q >= 0) its own connector; a main piece the larger of the
    // element's non-branch connectors (ssrc "revit-transition" when they differ: a reducer drawn at its big
    // end); the Navisworks stated size of row r only when Revit has none. ssrc = revit / revit-transition /
    // navisworks / none.
    static void SegSize(RvEl e, int q, Row r, out string shape, out double w, out double h, out double d, out string ssrc)
    {
        RvConn used; SegSize(e, q, r, out shape, out w, out h, out d, out ssrc, out used);
    }

    // as above; used = the Revit connector the size came from (null for navisworks / none)
    static void SegSize(RvEl e, int q, Row r, out string shape, out double w, out double h, out double d, out string ssrc, out RvConn used)
    {
        RvConn k = null; bool differ = false; used = null;
        if (q >= 0) k = e.Conns[q];
        else
        {
            var branch = new HashSet<int>(); foreach (int[] g in e.Segs) if (g[2] >= 0) branch.Add(g[2]);
            for (int i = 0; i < e.Conns.Count; i++)
            {
                if (branch.Contains(i) || ConnPerim(e.Conns[i]) <= 0) continue;
                if (k != null && Math.Abs(ConnPerim(e.Conns[i]) - ConnPerim(k)) > 1) differ = true;
                if (k == null || ConnPerim(e.Conns[i]) > ConnPerim(k)) k = e.Conns[i];
            }
        }
        if (k != null && ConnPerim(k) > 0)
        {
            shape = k.Shape == "Round" ? "round" : "rect"; w = k.W; h = k.H; d = k.Dia; ssrc = differ ? "revit-transition" : "revit";
            used = k;
            // a run segment's voted size (DuctsRunGraph) for main pieces, kept in the connector's W / H order
            if (q < 0 && e.Voted != null && e.Kind != "transition" && (e.Voted[0] == 1) == (shape == "round"))
            {
                if (shape == "round") d = e.Voted[1];
                else if (w >= h) { w = e.Voted[1]; h = e.Voted[2]; }
                else { w = e.Voted[2]; h = e.Voted[1]; }
                ssrc = "voted";
            }
            return;
        }
        if (r != null && NavSize(r, out shape, out w, out h, out d)) { ssrc = "navisworks"; return; }
        shape = "rect"; w = h = d = double.NaN; ssrc = "none";
    }

    // The connector's width direction made perpendicular to the beam (a bend's chord is not along either end's
    // axis); null when it lies (nearly) along the beam
    static double[] WidthAxis(Node a, Node b, double[] wx)
    {
        double[] x = { b.xyz.X - a.xyz.X, b.xyz.Y - a.xyz.Y, b.xyz.Z - a.xyz.Z };
        double l = Math.Sqrt(x[0] * x[0] + x[1] * x[1] + x[2] * x[2]); if (l <= 0) return null;
        for (int i = 0; i < 3; i++) x[i] /= l;
        double dot = wx[0] * x[0] + wx[1] * x[1] + wx[2] * x[2];
        double[] y = { wx[0] - dot * x[0], wx[1] - dot * x[1], wx[2] - dot * x[2] };
        double yl = Math.Sqrt(y[0] * y[0] + y[1] * y[1] + y[2] * y[2]);
        if (yl < 0.2) return null;
        return new[] { y[0] / yl, y[1] / yl, y[2] / yl };
    }

    // -Part report, 3 lines: the part (kind, run / segment, category, family); each connector (id, type, size, state:
    // connected <ids> / outside / touching <id> / near / side / open); for each connector not connected, the 2 nearest
    // other connectors in the room: owner id + kind, distance, along (+ = in front of it) and off-axis inches, and
    // the angle between the two directions (180 = facing each other; near links need <= 12 in, off-axis <= 1 in,
    // angle >= 154 deg)
    static string PartReport(RvEl pe, List<RvEl> scope, Dictionary<string, RvConn> conns, HashSet<RvEl> inScope,
        Dictionary<RvConn, RvConn> touch, HashSet<RvConn> nearSet, HashSet<RvConn> linked, Dictionary<RvConn, RvConn> attach)
    {
        var l1 = string.Format(Inv, "NEW part {0}: {1}{2} | {3} | {4} | {5} conn", pe.Id, pe.Kind == "" ? "?" : pe.Kind,
            pe.Run >= 0 ? string.Format(Inv, " R{0} S{1}", pe.Run, pe.Seg) : " no run", pe.Cat, (pe.Fam + " " + pe.Type).Trim(), pe.Conns.Count);
        var cs = new List<string>(); var ns = new List<string>();
        foreach (RvConn k in pe.Conns)
        {
            string id = k.Key.Substring(k.Key.IndexOf(':') + 1), state;
            var inside = k.To.FindAll(t => { RvConn q; return conns.TryGetValue(t, out q) && inScope.Contains(q.Owner); });
            if (inside.Count > 0) state = "connected " + string.Join(" ", inside.ConvertAll(t => t.Substring(0, t.IndexOf(':'))).ToArray());
            else if (k.To.Count > 0) state = "outside";
            else if (touch.ContainsKey(k)) state = "touching " + touch[k].Owner.Id;
            else if (attach.ContainsKey(k)) state = "attached " + attach[k].Owner.Id;
            else if (nearSet.Contains(k)) state = "near";
            else if (linked.Contains(k)) state = "side";
            else state = "open";
            cs.Add(string.Format(Inv, "{0} {1} {2} {3}", id, k.Type, SizeText(k), state));
            if (inside.Count > 0) continue;
            var cand = new List<KeyValuePair<double, string>>();
            foreach (RvEl e in scope)
            {
                if (e == pe) continue;
                foreach (RvConn q in e.Conns)
                {
                    double d = Math.Sqrt(Dist2(k.P, q.P)) * 12; if (d > 36) continue;
                    double along = ((q.P[0] - k.P[0]) * k.D[0] + (q.P[1] - k.P[1]) * k.D[1] + (q.P[2] - k.P[2]) * k.D[2]) * 12;
                    double lat = Math.Sqrt(Math.Max(0, d * d - along * along));
                    double ang = Math.Acos(Math.Max(-1, Math.Min(1, k.D[0] * q.D[0] + k.D[1] * q.D[1] + k.D[2] * q.D[2]))) * 180 / Math.PI;
                    cand.Add(new KeyValuePair<double, string>(d, string.Format(Inv, "{0}({1}) {2:0.#}in along {3:0.#} off {4:0.#} ang {5:0}", e.Id, e.Kind, d, along, lat, ang)));
                }
            }
            cand.Sort((x, y) => x.Key.CompareTo(y.Key));
            ns.Add(id + ": " + (cand.Count == 0 ? "nothing within 36 in" : string.Join(", ", cand.Take(2).Select(x => x.Value).ToArray())));
        }
        return l1 + "\nNEW conns: " + string.Join("; ", cs.ToArray()) + "\nNEW nearest: " + (ns.Count > 0 ? string.Join(" | ", ns.ToArray()) : "all connected");
    }

    // A 2-connector part whose ends face opposite ways (within ~18 deg) but whose centres are offset sideways by
    // more than 1 in: the index of the OUTLET (the smaller end; connector 1 when equal) to draw along; else -1
    static int EccentricOutlet(RvConn a, RvConn b)
    {
        if (a.D[0] * b.D[0] + a.D[1] * b.D[1] + a.D[2] * b.D[2] > -0.95) return -1;
        double[] w = { b.P[0] - a.P[0], b.P[1] - a.P[1], b.P[2] - a.P[2] };
        double along = w[0] * b.D[0] + w[1] * b.D[1] + w[2] * b.D[2];
        double lat = Math.Sqrt(Math.Max(0, Dist2(a.P, b.P) - along * along)) * 12;
        if (lat <= 1) return -1;
        // same size at both ends = an S-offset: drawn as its diagonal (the chord), not outlet axis + stub
        // (2026-10-12, test room: an S-bend got a sideways bar); taps / eccentric reducers change size
        if (SameSize(a, b)) return -1;
        return ConnPerim(a) > 0 && ConnPerim(a) < ConnPerim(b) ? 0 : 1;
    }

    // Corner of a 2-connector part that turns: the closest point between the two connector axes, going INTO the part
    // from each end (feet). Null when it does not turn (ends opposite within ~18 deg), the axes are parallel, the
    // corner lies behind a connector, or it is further from either end than 3 x the distance between the ends.
    static double[] BendCorner(RvConn a, RvConn b)
    {
        double[] u = { -a.D[0], -a.D[1], -a.D[2] }, v = { -b.D[0], -b.D[1], -b.D[2] };   // inward directions
        if (a.D[0] * b.D[0] + a.D[1] * b.D[1] + a.D[2] * b.D[2] < -0.95) return null;
        double[] w = { a.P[0] - b.P[0], a.P[1] - b.P[1], a.P[2] - b.P[2] };
        double uu = u[0] * u[0] + u[1] * u[1] + u[2] * u[2], vv = v[0] * v[0] + v[1] * v[1] + v[2] * v[2], uv = u[0] * v[0] + u[1] * v[1] + u[2] * v[2];
        double uw = u[0] * w[0] + u[1] * w[1] + u[2] * w[2], vw = v[0] * w[0] + v[1] * w[1] + v[2] * w[2];
        double den = uu * vv - uv * uv;
        if (den < 1e-6) return null;
        double s = (uv * vw - vv * uw) / den, t = (uu * vw - uv * uw) / den;
        double span = Math.Sqrt(Dist2(a.P, b.P));
        if (s <= 0 || t <= 0 || s > 3 * span || t > 3 * span) return null;
        double[] pa = { a.P[0] + s * u[0], a.P[1] + s * u[1], a.P[2] + s * u[2] }, pb = { b.P[0] + t * v[0], b.P[1] + t * v[1], b.P[2] + t * v[2] };
        return new[] { (pa[0] + pb[0]) / 2, (pa[1] + pb[1]) / 2, (pa[2] + pb[2]) / 2 };
    }

    // Revit "Curve" connector: where a tap meets a duct's side (not an end of the part)
    static bool IsTapPoint(RvConn k) { return string.Equals(k.Type, "Curve", StringComparison.OrdinalIgnoreCase); }

    static double ConnPerim(RvConn k)
    {
        if (k.Shape == "Round") return double.IsNaN(k.Dia) ? 0 : Math.PI * k.Dia;
        return double.IsNaN(k.W) || double.IsNaN(k.H) ? 0 : 2 * (k.W + k.H);
    }

    // A Navisworks row's axis (feet): X1..Z2, else the bbox's long axis through its centre
    static void NavAxis(Row r, out double[] p, out double[] q)
    {
        if (r.E1 != null) { p = r.E1; q = r.E2; return; }
        int ax = 0; for (int a = 1; a < 3; a++) if (r.Max[a] - r.Min[a] > r.Max[ax] - r.Min[ax]) ax = a;
        p = new double[3]; q = new double[3];
        for (int a = 0; a < 3; a++) { p[a] = q[a] = (r.Min[a] + r.Max[a]) / 2; }
        p[ax] = r.Min[ax]; q[ax] = r.Max[ax];
    }

    // Navisworks stated size: Width / Height / Diameter, else read from the Name
    static bool NavSize(Row r, out string shape, out double w, out double h, out double d)
    {
        w = !double.IsNaN(r.W) ? r.W : r.NameW; h = !double.IsNaN(r.H) ? r.H : r.NameH; d = !double.IsNaN(r.D) ? r.D : r.NameD;
        if (!double.IsNaN(w) && !double.IsNaN(h)) { shape = "rect"; d = double.NaN; return true; }
        if (!double.IsNaN(d)) { shape = "round"; w = h = double.NaN; return true; }
        shape = "rect"; return false;
    }

    static double[] NodeFt(Node n, double[] off, double scale) { return new[] { n.xyz.X / scale + off[0], n.xyz.Y / scale + off[1], n.xyz.Z / scale + off[2] }; }

    static string R3(double v) { return double.IsNaN(v) ? "" : v.ToString("0.###", Inv); }

    static int RvBeam(Dictionary<int, RawViewerWriter.BeamMember> beams, Dictionary<int, string> labels, ref int nb, int a, int b, Dictionary<int, Node> nodes, string label)
    {
        if (a == b) return 0;
        beams[nb] = Beam(nb, a, b, 0, nodes); labels[nb] = label; return nb++;
    }

    static double Dist2(double[] p, double[] q) { double dx = p[0] - q[0], dy = p[1] - q[1], dz = p[2] - q[2]; return dx * dx + dy * dy + dz * dz; }

    // Rotation about Z + translation taking p onto q (least squares in XY, median in Z), refitted twice without
    // pairs whose residual exceeds max(0.5 ft, 3 x median). Returns { cos, sin, tx, ty, tz }; spread = RMS
    // distance of q from its centroid / the same for p (1 when both are in the same unit).
    static double[] FitRigidXY(List<double[]> p, List<double[]> q, out double medRes, out int used, out double spread)
    {
        var idx = new List<int>(); for (int i = 0; i < p.Count; i++) idx.Add(i);
        double[] T = null; medRes = double.NaN; spread = double.NaN;
        for (int round = 0; round < 3; round++)
        {
            double pcx = 0, pcy = 0, qcx = 0, qcy = 0;
            foreach (int i in idx) { pcx += p[i][0]; pcy += p[i][1]; qcx += q[i][0]; qcy += q[i][1]; }
            pcx /= idx.Count; pcy /= idx.Count; qcx /= idx.Count; qcy /= idx.Count;
            double sc = 0, ss = 0, sp = 0, sq = 0;
            var dz = new List<double>();
            foreach (int i in idx)
            {
                double ax = p[i][0] - pcx, ay = p[i][1] - pcy, bx = q[i][0] - qcx, by = q[i][1] - qcy;
                sc += ax * bx + ay * by; ss += ax * by - ay * bx; sp += ax * ax + ay * ay; sq += bx * bx + by * by;
                dz.Add(q[i][2] - p[i][2]);
            }
            double th = Math.Atan2(ss, sc), c = Math.Cos(th), s = Math.Sin(th);
            T = new[] { c, s, qcx - (c * pcx - s * pcy), qcy - (s * pcx + c * pcy), Median(dz) };
            spread = sp > 0 ? Math.Sqrt(sq / sp) : double.NaN;
            var res = new double[p.Count]; var kept = new List<double>();
            for (int i = 0; i < p.Count; i++)
            {
                double x = T[0] * p[i][0] - T[1] * p[i][1] + T[2], y = T[1] * p[i][0] + T[0] * p[i][1] + T[3], z = p[i][2] + T[4];
                res[i] = Math.Sqrt((x - q[i][0]) * (x - q[i][0]) + (y - q[i][1]) * (y - q[i][1]) + (z - q[i][2]) * (z - q[i][2]));
            }
            foreach (int i in idx) kept.Add(res[i]);
            medRes = Median(kept);
            if (round == 2) break;
            double lim = Math.Max(0.5, 3 * medRes);
            var next = new List<int>(); for (int i = 0; i < p.Count; i++) if (res[i] <= lim) next.Add(i);
            if (next.Count < 3) break;
            idx = next;
        }
        used = idx.Count;
        return T;
    }

    // IfcGUID from a Revit UniqueId ("<episode guid>-<8 hex element id>"): the export GUID is the episode GUID with
    // its last 8 hex digits XOR the element id, then IFC's 22-character base-64 compression. "" if malformed.
    static string IfcGuidFromUniqueId(string uid)
    {
        if (uid == null || uid.Length != 45 || uid[36] != '-') return "";
        try
        {
            uint eid = uint.Parse(uid.Substring(37, 8), NumberStyles.HexNumber, Inv);
            uint last = uint.Parse(uid.Substring(28, 8), NumberStyles.HexNumber, Inv) ^ eid;
            string hex = (uid.Substring(0, 8) + uid.Substring(9, 4) + uid.Substring(14, 4) + uid.Substring(19, 4) + uid.Substring(24, 4)) + last.ToString("x8", Inv);
            var b = new byte[16];
            for (int i = 0; i < 16; i++) b[i] = byte.Parse(hex.Substring(2 * i, 2), NumberStyles.HexNumber, Inv);
            // 128 bits -> 22 chars: first char 2 bits, then 21 x 6 bits, big-endian
            const string A = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz_$";
            var o = new char[22];
            o[0] = A[b[0] >> 6];
            int bit = 2;
            for (int n = 1; n < 22; n++)
            {
                int v = 0;
                for (int k = 0; k < 6; k++, bit++) v = (v << 1) | ((b[bit >> 3] >> (7 - (bit & 7))) & 1);
                o[n] = A[v];
            }
            return new string(o);
        }
        catch { return ""; }
    }
}
