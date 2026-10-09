using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
        public string Id, Uid, Guid, GuidCalc, Cat, Fam, Type, Size, System, Level, Match = "";
        public bool Fab, Only;
        public List<RvConn> Conns = new List<RvConn>();
        public double[] C, P;     // centre: Revit internal / Navisworks frame (feet)
    }

    class RvConn
    {
        public RvEl Owner;
        public string Key, Type;
        public double[] P, D;     // Navisworks frame after the fit; D = outward (BasisZ)
        public string Shape;      // Rectangular / Round / Oval
        public double W, H, Dia;  // inches, NaN = n/a
        public List<string> To = new List<string>();
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
            e.System = Get(c, "SystemName"); e.Level = Get(c, "Level"); e.Fab = Get(c, "IsFabrication") == "1";
            els[e.Id] = e;
        }
        var conns = new Dictionary<string, RvConn>(StringComparer.Ordinal);
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
            k.Shape = Get(c, "Shape"); k.W = Num(c, "Width_in"); k.H = Num(c, "Height_in"); k.Dia = Num(c, "Diameter_in");
            foreach (string t in Get(c, "ConnectedTo").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)) k.To.Add(t);
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
        foreach (RvConn k in conns.Values) { k.P = xf(k.P); k.D = new[] { T[0] * k.D[0] - T[1] * k.D[1], T[1] * k.D[0] + T[0] * k.D[1], k.D[2] }; }

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

        // nodes: a connector shares its node with the connectors it is connected (or touching) to
        double[] off = { Math.Round((lo[0] + hi[0]) / 2), Math.Round((lo[1] + hi[1]) / 2), Math.Round((lo[2] + hi[2]) / 2) };
        var nodes = new Dictionary<int, Node>(); int nextNode = 1;
        var nodeOf = new Dictionary<string, int>(StringComparer.Ordinal);
        var gConn = new List<uint>(); var gOut = new List<uint>(); var gTouch = new List<uint>(); var gOpen = new List<uint>();
        Func<RvConn, int> node = delegate(RvConn k)
        {
            int id; if (nodeOf.TryGetValue(k.Key, out id)) return id;
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
                ids.Add(RvBeam(beams, labels, ref nb, node(e.Conns[0]), node(e.Conns[1]), nodes, label));
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
                    for (int q = 0; q < e.Conns.Count; q++)
                        if (q != thr[0] && q != thr[1])
                            ids.Add(RvBeam(beams, labels, ref nb, MakeNode(nodes, ref nextNode, BranchJunction(a.P, b.P, ops[q]), off, scale), node(e.Conns[q]), nodes, label));
                }
                else
                {
                    int hub = MakeNode(nodes, ref nextNode, e.P, off, scale);
                    foreach (RvConn k in e.Conns) ids.Add(RvBeam(beams, labels, ref nb, hub, node(k), nodes, label));
                }
            }
            else node(e.Conns[0]);
            string g = "Revit " + (e.Fab ? "fabrication" : e.Cat);
            List<uint> gl; if (!byCat.TryGetValue(g, out gl)) { gl = new List<uint>(); byCat[g] = gl; }
            foreach (int id in ids) if (id > 0) { gl.Add((uint)id); if (e.Only) gOnly.Add((uint)id); }
        }
        // Links between open ends that are neither connected nor touching (as the probe's):
        //   near (gap / overlap): another element's open end facing it (directions opposite, dot < -0.9), on its
        //     axis (lateral <= 1 in), within 1 ft; labelled with the signed gap
        //   side branch: the end's axis meets another part's main line inside the segment (not within 1 in of
        //     its ends), the end lying within that part's half size + 3 in of the line
        var gNear = new List<uint>(); var gSide = new List<uint>();
        var linked = new HashSet<RvConn>(); var nearSet = new HashSet<RvConn>();
        var loose = new List<RvConn>();
        foreach (RvConn k in open) if (!touch.ContainsKey(k)) loose.Add(k);
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
            linked.Add(k); linked.Add(best); nearSet.Add(k); nearSet.Add(best);
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
            linked.Add(k);
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
                else if (nearSet.Contains(k)) nNear++;
                else if (linked.Contains(k)) nSide++;
                else if (k.Type == "End") { gOpen.Add((uint)id); nOpen++; }
            }

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
            if (gConn.Count > 0) sc.AddNodeGroup("Revit joint: connected", "#3fc1a5", gConn, new[] { "duct", "revit", "joint" });
            if (gTouch.Count > 0) sc.AddNodeGroup("Revit joint: touching, not connected", "#ff9f1c", gTouch, new[] { "duct", "revit", "touching" });
            if (gOut.Count > 0) sc.AddNodeGroup("Revit joint: connected outside the room", "#4f8cff", gOut, new[] { "duct", "revit", "outside" });
            if (gOpen.Count > 0) sc.AddNodeGroup("Revit open end", "#ff3b3b", gOpen, new[] { "duct", "revit", "open" }, "REVIT_OPEN");
            File.WriteAllText(outBase + ".features.json", sc.ToJson(), new UTF8Encoding(false));
            res.BinPath = outBase + ".bin"; res.SidecarPath = outBase + ".features.json"; res.GeometryHash = w.GeometryHash;
        }
        res.Nodes = nodes.Count; res.Beams = beams.Count;

        // match report
        var navMissing = new List<int>();
        foreach (int i in inRoom) if (rows[i].Guid == "" || !matchedGuids.Contains(rows[i].Guid)) navMissing.Add(i);
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
        var s = new StringBuilder();
        res.Note = string.Format(Inv,
            "NEW fit {0:0.00} ft median ({1} pairs{2}, rot {3:0.###} deg, scale check {4:0.###})\nNEW room: Navisworks {5} rows ({6} fab), Revit {7} el ({8} fab), Navisworks without Revit {9} ({10} fab), Revit only {11} ({12} fab), category differs {13}\nNEW joints: connected {14} touching {15} near {16} side {17} outside {18} open {19}",
            medRes, used, means ? " by GUID group" : " 1:1", Math.Atan2(T[1], T[0]) * 180 / Math.PI, spread,
            inRoom.Count, navFab, scope.Count, rvFab, navMissing.Count, navFabMissing, rvOnly, rvOnlyFab, catDiff.Count,
            nConnected, nTouch, nNear, nSide, nOutside, nOpen);
        s.AppendLine(res.Note);
        s.AppendLine();
        s.AppendLine("Navisworks run: " + navisRun);
        s.AppendLine("Revit run:      " + revitRun);
        s.AppendLine("room(s):        " + (byRoom ? room : "(all)"));
        s.AppendLine(string.Format(Inv, "Revit elements {0} (no connectors {1}); matched by IfcGUID parameter {2}, by GUID computed from UniqueId {3}; shared GUIDs {4}",
            els.Count, noConn, byParam, byCalc, rvByGuid.Count));
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
        File.WriteAllText(outBase + ".match.txt", s.ToString(), new UTF8Encoding(false));
        return res;
    }

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
