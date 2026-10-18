using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

// Systems (2026-10-12, user: "a run is something like <SYS>, not <SYS>-<number>; ~5 in the building, each a single
// connected entity"): flood fill over everything that joins two parts, as a person tracing the overlay would:
//   Revit connections, touching ends, near links (gap / overlap facing), side-branch links, and PROXIMITY: a
//   point of one part's centreline (every 3 in) inside another part's drawn box grown by ProxTolFt on every side
//   (overlaps of any length, gaps up to 6 in, a branch butting into a side). Side-by-side ducts stay apart.
// Each connected component is a system, named by the majority service of its parts (Navisworks / Revit RunName
// "A-<bldg>-<service>-DUCT-<n>" -> service, else the Revit system name without its trailing number). Joins made
// only by proximity are flagged JOINED_BY_PROXIMITY on both parts. Lines / segments (DuctsRunGraph) sit inside.
public partial class DuctsToPluto
{
    const double ProxTolFt = 0.5;
    static readonly HashSet<RvConn> FreeEnds = new HashSet<RvConn>();   // set by the overlay before AnalyzeSystems
    static readonly List<Tuple<RvConn, double[]>> EndContacts = new List<Tuple<RvConn, double[]>>();   // free end -> point on the other centreline
    static readonly List<string> Blocked = new List<string>();   // weak joins refused across two system names (last AnalyzeSystems)

    class DuctSystem
    {
        public int Id; public string Name = ""; public List<RvEl> Els = new List<RvEl>();
        public SortedDictionary<string, int> Names = new SortedDictionary<string, int>(StringComparer.Ordinal);
        public int ProxJoins; public bool Primary;
    }

    class PartBox
    {
        public RvEl Owner; public double[] A, X, Y, Z; public double L, HW, HH, R; public bool Round;
        public double[] Lo, Hi;
    }

    static List<DuctSystem> AnalyzeSystems(List<RvEl> scope, Dictionary<string, RvConn> conns, HashSet<RvEl> inScope,
        Dictionary<RvConn, RvConn> touch, List<RvConn[]> nearPairs, List<RvEl[]> sidePairs,
        Dictionary<int, RawViewerWriter.BeamMember> beams, Dictionary<int, double[]> beamDim, Dictionary<int, Node> nodes,
        double[] off, double scale, List<Row> rows, Func<RvEl, List<int>> rowsOf)
    {
        var parent = new Dictionary<RvEl, RvEl>();
        foreach (RvEl e in scope) parent[e] = e;
        Func<RvEl, RvEl> find = null;
        find = e => { while (parent[e] != e) { parent[e] = parent[parent[e]]; e = parent[e]; } return e; };
        // names first (2026-10-12: a weak join bridged system A into the system B run next to it in a test room). STRONG joins
        // (Revit connection, touching ends, near links, side-branch links: an end butting into or facing another
        // part is a connection; user 2026-10-12: two systems never connect into each other) always join. Only
        // PROXIMITY is weak: it joins when the two GROUPS' majority names agree or one group has none, else it is
        // BLOCKED and listed in match.txt (a duct passing over another is not a connection). Afterwards the
        // majority name wins per system and every part whose own name differs is flagged SYSTEM_NAME_CONFLICT
        // (test room: one branch piece with Revit RunName system B in a system A branch).
        var nameOf = new Dictionary<RvEl, string>();
        foreach (RvEl e in scope) nameOf[e] = SystemNameOf(e, rows, rowsOf);
        // group root -> name counts; a group's name is its MAJORITY (one mislabelled part must not let a weak
        // join through: user, 2026-10-12, suspects a single mislabelled segment in a test room)
        var names = new Dictionary<RvEl, Dictionary<string, int>>();
        foreach (RvEl e in scope) { names[e] = new Dictionary<string, int>(StringComparer.Ordinal); if (nameOf[e] != "") names[e][nameOf[e]] = 1; }
        Func<RvEl, string> major = r => names[r].Count == 0 ? "" : names[r].OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).First().Key;
        Action<RvEl, RvEl> union = (a, b) =>
        {
            if (a == null || b == null || !parent.ContainsKey(a) || !parent.ContainsKey(b)) return;
            RvEl ra = find(a), rb = find(b); if (ra == rb) return;
            parent[ra] = rb;
            foreach (KeyValuePair<string, int> kv in names[ra]) { int c; names[rb].TryGetValue(kv.Key, out c); names[rb][kv.Key] = c + kv.Value; }
        };
        Blocked.Clear(); EndContacts.Clear();
        Action<RvEl, RvEl> strong = (a, b) =>
        {
            if (a == b || !parent.ContainsKey(a) || !parent.ContainsKey(b)) return;
            union(a, b);
        };
        Func<RvEl, RvEl, string, bool> weak = (a, b, kind) =>
        {
            if (a == b || !parent.ContainsKey(a) || !parent.ContainsKey(b)) return false;
            string na = major(find(a)), nb = major(find(b));
            if (na != "" && nb != "" && na != nb)
            {
                Blocked.Add(string.Format(Inv, "{0}: {1} ({2}) - {3} ({4})", kind, a.Id, na, b.Id, nb));
                return false;
            }
            union(a, b); return true;
        };
        foreach (RvEl e in scope)
            foreach (RvConn k in e.Conns)
                foreach (string t in k.To) { RvConn q; if (conns.TryGetValue(t, out q) && inScope.Contains(q.Owner)) strong(e, q.Owner); }
        foreach (KeyValuePair<RvConn, RvConn> kv in touch) strong(kv.Key.Owner, kv.Value.Owner);
        foreach (RvConn[] p in nearPairs) strong(p[0].Owner, p[1].Owner);
        foreach (RvEl[] p in sidePairs) strong(p[0], p[1]);
        // proximity candidates, applied in two passes (below)
        var cands = new List<Tuple<RvEl, RvEl, string>>();

        // proximity over the drawn boxes
        var boxes = new List<PartBox>();
        foreach (RvEl e in scope)
            foreach (int id in e.Beams)
            {
                double[] dim; if (!beamDim.TryGetValue(id, out dim)) continue;
                RawViewerWriter.BeamMember m = beams[id];
                double[] a = NodeFt(nodes[m.NodeA], off, scale), b = NodeFt(nodes[m.NodeB], off, scale);
                double L = Math.Sqrt(Dist2(a, b)); if (L <= 1e-6) continue;
                var bx = new PartBox { Owner = e, A = a, L = L, Round = dim[0] == 1 };
                bx.X = new[] { (b[0] - a[0]) / L, (b[1] - a[1]) / L, (b[2] - a[2]) / L };
                double[] y = m.LocalY ?? new double[] { 0, 0, 1 };
                double dot = y[0] * bx.X[0] + y[1] * bx.X[1] + y[2] * bx.X[2];
                y = new[] { y[0] - dot * bx.X[0], y[1] - dot * bx.X[1], y[2] - dot * bx.X[2] };
                double yl = Math.Sqrt(y[0] * y[0] + y[1] * y[1] + y[2] * y[2]);
                if (yl < 1e-6) y = Math.Abs(bx.X[2]) < 0.9 ? new double[] { 0, 0, 1 } : new double[] { 1, 0, 0 }; else y = new[] { y[0] / yl, y[1] / yl, y[2] / yl };
                bx.Y = y; bx.Z = new[] { bx.X[1] * y[2] - bx.X[2] * y[1], bx.X[2] * y[0] - bx.X[0] * y[2], bx.X[0] * y[1] - bx.X[1] * y[0] };
                if (bx.Round) bx.R = (double.IsNaN(dim[3]) ? 12 : dim[3]) / 24;
                else { bx.HW = (double.IsNaN(dim[1]) ? 12 : dim[1]) / 24; bx.HH = (double.IsNaN(dim[2]) ? 12 : dim[2]) / 24; }
                double rad = (bx.Round ? bx.R : Math.Sqrt(bx.HW * bx.HW + bx.HH * bx.HH)) + ProxTolFt;
                bx.Lo = new double[3]; bx.Hi = new double[3];
                for (int i = 0; i < 3; i++) { bx.Lo[i] = Math.Min(a[i], b[i]) - rad; bx.Hi[i] = Math.Max(a[i], b[i]) + rad; }
                boxes.Add(bx);
            }
        for (int i = 0; i < boxes.Count; i++)
            for (int j = i + 1; j < boxes.Count; j++)
            {
                PartBox p = boxes[i], q = boxes[j];
                if (p.Owner == q.Owner || find(p.Owner) == find(q.Owner)) continue;
                bool overlap = true;
                for (int k = 0; k < 3 && overlap; k++) if (p.Hi[k] < q.Lo[k] || q.Hi[k] < p.Lo[k]) overlap = false;
                if (!overlap || !(LineInBox(p, q) || LineInBox(q, p))) continue;
                // an OPEN END of one part inside / against the other's box = an end butting into it: a connection,
                // joined regardless of names (2026-10-12, test room: a system B-labelled S-bend hanging off a system A tap);
                // only centrelines crossing / running close stay name-checked
                if (OpenEndInBox(p, q.Owner) || OpenEndInBox(q, p.Owner))
                {
                    strong(p.Owner, q.Owner);
                    AddFlag(p.Owner, "JOINED_BY_PROXIMITY", "end contact with " + q.Owner.Id);
                    AddFlag(q.Owner, "JOINED_BY_PROXIMITY", "end contact with " + p.Owner.Id);
                    continue;
                }
                cands.Add(Tuple.Create(p.Owner, q.Owner, "proximity"));
            }
        // pass 1: proximity between groups that both carry a name and agree; pass 2: the rest (a group without a name)
        Action<Tuple<RvEl, RvEl, string>> apply = c =>
        {
            if (find(c.Item1) == find(c.Item2) || !weak(c.Item1, c.Item2, c.Item3) || c.Item3 != "proximity") return;
            AddFlag(c.Item1, "JOINED_BY_PROXIMITY", "joined to " + c.Item2.Id + " by proximity");
            AddFlag(c.Item2, "JOINED_BY_PROXIMITY", "joined to " + c.Item1.Id + " by proximity");
        };
        foreach (Tuple<RvEl, RvEl, string> c in cands)
        {
            string na = major(find(c.Item1)), nb = major(find(c.Item2));
            if (na != "" && na == nb) apply(c);
        }
        foreach (Tuple<RvEl, RvEl, string> c in cands) apply(c);

        // components -> systems, biggest first, named by majority service
        var comp = new Dictionary<RvEl, List<RvEl>>();
        foreach (RvEl e in scope) { RvEl r = find(e); List<RvEl> l; if (!comp.TryGetValue(r, out l)) { l = new List<RvEl>(); comp[r] = l; } l.Add(e); }
        var res = new List<DuctSystem>();
        foreach (List<RvEl> l in comp.Values.OrderByDescending(x => x.Count))
        {
            var s = new DuctSystem { Id = res.Count + 1 }; s.Els = l;
            foreach (RvEl e in l)
            {
                string n = nameOf[e];
                if (n == "") continue; int c; s.Names.TryGetValue(n, out c); s.Names[n] = c + 1;
            }
            s.Name = s.Names.Count > 0 ? s.Names.OrderByDescending(kv => kv.Value).First().Key : "unnamed";
            foreach (RvEl e in l)
            {
                e.Sys = s.Id; e.SysName = s.Name;
                if (e.Flags.Contains("JOINED_BY_PROXIMITY")) s.ProxJoins++;
                // majority rules: a part whose own name differs from its system's is flagged (the data mislabels it)
                if (nameOf[e] != "" && nameOf[e] != s.Name) AddFlag(e, "SYSTEM_NAME_CONFLICT", "own name " + nameOf[e] + " in system " + s.Name);
            }
            res.Add(s);
        }
        // primary systems (2026-10-15, user): per configured name the largest connected component IS that system;
        // every other component with that majority name is "<name> fragments" (a split at a tee shows up here and
        // is fixed in the joining, not hidden); components of any other name are "other: <name>"
        if (PrimarySystems.Count > 0)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (DuctSystem s in res)   // biggest first
            {
                if (PrimarySystems.Contains(s.Name)) { s.Primary = true; if (!seen.Add(s.Name)) { s.Name += " fragments"; s.Primary = false; } }
                else s.Name = "other: " + s.Name;
                foreach (RvEl e in s.Els) e.SysName = s.Name;
            }
            // pinned mains (2026-10-17, user: a room boundary cut one run in two; the smaller piece is a second main, not
            // a fragment): DuctMains "<name>=<Revit id>" promotes the fragment holding that part to "<name> 2", "<name> 3", ...
            PinNotes.Clear();
            foreach (KeyValuePair<string, string> pin in PrimaryPins)
            {
                DuctSystem s = res.FirstOrDefault(x => x.Els.Exists(e => e.Id == pin.Value));
                if (s == null) { PinNotes.Add(pin.Key + "=" + pin.Value + ": part not in scope"); continue; }
                if (s.Name == pin.Key) { PinNotes.Add(pin.Key + "=" + pin.Value + ": already in the main"); continue; }
                if (s.Name != pin.Key + " fragments")
                {
                    PinNotes.Add(pin.Key + "=" + pin.Value + (s.Primary ? ": already a pinned main (" + s.Name + ")" : ": the part is in '" + s.Name + "', not a " + pin.Key + " fragment"));
                    continue;
                }
                int k = 2; while (res.Exists(x => x.Name == pin.Key + " " + k)) k++;
                s.Name = pin.Key + " " + k; s.Primary = true;
                foreach (RvEl e in s.Els) e.SysName = s.Name;
                PinNotes.Add(string.Format(Inv, "{0}={1}: C{2} ({3} parts) -> {4}", pin.Key, pin.Value, s.Id, s.Els.Count, s.Name));
            }
        }
        return res;
    }

    // set from the duct config (DuctSystems) by Export-DuctsViewer.ps1; empty = name systems as found.
    // Colours: "<name>=#rrggbb" entries; the fragments group gets a desaturated variant
    public static readonly List<string> PrimarySystems = new List<string>();
    public static readonly Dictionary<string, string> PrimaryColors = new Dictionary<string, string>(StringComparer.Ordinal);
    // DuctMains: (system name, Revit id) pins, set by Export-DuctsViewer.ps1; PinNotes: what each did (match.txt)
    public static readonly List<KeyValuePair<string, string>> PrimaryPins = new List<KeyValuePair<string, string>>();
    static readonly List<string> PinNotes = new List<string>();

    // a configured primary name, or a pinned main "<name> <n>"; base = the configured name (colour)
    static bool IsPrimaryName(string n, out string baseName)
    {
        baseName = n;
        if (PrimarySystems.Contains(n)) return true;
        Match m = Regex.Match(n ?? "", @"^(.+) (\d+)$");
        if (m.Success && PrimarySystems.Contains(m.Groups[1].Value)) { baseName = m.Groups[1].Value; return true; }
        return false;
    }

    // the system view (2026-10-15, user): with primary systems configured, each system group in its configured colour
    // (fragments desaturated), "other: <name>" groups not drawn, every other group (categories, links, joints, flags)
    // unpainted; the user turns them back on in the Groups tab. Group names: "System <name> (n parts)" / "Clean: <name>"
    static void ApplySystemView(FeaturesSidecar sc)
    {
        if (PrimarySystems.Count == 0) return;
        foreach (FeaturesSidecar.Group g in sc.Groups)
        {
            if (!g.Tags.Contains("system")) { g.Hidden = true; continue; }
            string n = Regex.Replace(Regex.Replace(g.Name ?? "", @"^(System |Clean: )", ""), @" \(\d+ parts\)$", "");
            string bn; bool prim = IsPrimaryName(n, out bn);
            if (n.StartsWith("other: ") || !(prim || n.EndsWith(" fragments"))) { g.Hidden = true; g.Invisible = true; continue; }
            bool frag = n.EndsWith(" fragments"); string c;
            if (PrimaryColors.TryGetValue(frag ? n.Substring(0, n.Length - 10) : bn, out c)) g.Color = frag ? Desaturate(c) : c;
        }
    }

    // the configured colour of a system name as the viewer paints it (mains and pinned mains as configured, fragments
    // desaturated), "" for anything else; frames.csv SystemColor -> SAP group colours (2026-10-17)
    static string SystemColor(string n)
    {
        string bn, c; bool frag = (n ?? "").EndsWith(" fragments");
        if (frag) bn = n.Substring(0, n.Length - 10); else if (!IsPrimaryName(n, out bn)) return "";
        return PrimaryColors.TryGetValue(bn, out c) ? (frag ? Desaturate(c) : c) : "";
    }

    // frames.csv SystemRole (2026-10-17, the SAP export keeps mains by default): "main" (configured or pinned),
    // "fragment", "other"; with no primary systems configured every system is a "main"
    static string SystemRole(string n)
    {
        string bn;
        if (PrimarySystems.Count == 0) return "main";
        if ((n ?? "").EndsWith(" fragments")) return "fragment";
        return IsPrimaryName(n, out bn) ? "main" : "other";
    }

    // "#rrggbb" -> same hue, saturation x 0.35, lightness pulled toward 0.65
    static string Desaturate(string hex)
    {
        int v; if (hex == null || !int.TryParse(hex.TrimStart('#'), NumberStyles.HexNumber, Inv, out v)) return hex;
        double r = (v >> 16 & 255) / 255.0, g = (v >> 8 & 255) / 255.0, b = (v & 255) / 255.0;
        double mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b)), l = (mx + mn) / 2, d = mx - mn, h = 0, s = 0;
        if (d > 0)
        {
            s = d / (1 - Math.Abs(2 * l - 1));
            h = mx == r ? ((g - b) / d + 6) % 6 : mx == g ? (b - r) / d + 2 : (r - g) / d + 4;
        }
        s *= 0.35; l = l + (0.65 - l) * 0.5;
        double c2 = (1 - Math.Abs(2 * l - 1)) * s, x = c2 * (1 - Math.Abs(h % 2 - 1)), m = l - c2 / 2;
        double[] rgb = h < 1 ? new[] { c2, x, 0.0 } : h < 2 ? new[] { x, c2, 0.0 } : h < 3 ? new[] { 0.0, c2, x } : h < 4 ? new[] { 0.0, x, c2 } : h < 5 ? new[] { x, 0.0, c2 } : new[] { c2, 0.0, x };
        return "#" + string.Concat(rgb.Select(q => ((int)Math.Round((q + m) * 255)).ToString("x2", Inv)).ToArray());
    }

    // a part's system name: the first configured primary name (longest first) contained in its RunName or Revit
    // system name, ignoring case ("<SYS> 123", "A-x-<SYS>-DUCT-4" -> <SYS>); else SystemBase as before
    static string SystemNameOf(RvEl e, List<Row> rows, Func<RvEl, List<int>> rowsOf)
    {
        string rn = PartRunName(e, rows, rowsOf);
        foreach (string p in PrimarySystems.OrderByDescending(x => x.Length))
            if (rn.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0 || (e.System ?? "").IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0) return p;
        return SystemBase(rn, e.System);
    }

    // a point every 3 in along q's centreline inside p's box grown by ProxTolFt (along its axis too)
    static bool LineInBox(PartBox p, PartBox q)
    {
        int n = Math.Max(1, (int)Math.Ceiling(q.L / 0.25));
        for (int s = 0; s <= n; s++)
        {
            double t = q.L * s / n;
            if (PointInBox(p, new[] { q.A[0] + q.X[0] * t, q.A[1] + q.X[1] * t, q.A[2] + q.X[2] * t })) return true;
        }
        return false;
    }

    // a FREE end of part e inside p's box + ProxTolFt; each one found is recorded in EndContacts with the nearest
    // point on p's centreline (the clean model makes it a rigid link)
    static bool OpenEndInBox(PartBox p, RvEl e)
    {
        bool hit = false;
        foreach (RvConn k in e.Conns)
        {
            if (!FreeEnds.Contains(k) || !PointInBox(p, k.P)) continue;
            double t = Math.Max(0, Math.Min(p.L, (k.P[0] - p.A[0]) * p.X[0] + (k.P[1] - p.A[1]) * p.X[1] + (k.P[2] - p.A[2]) * p.X[2]));
            EndContacts.Add(Tuple.Create(k, new[] { p.A[0] + p.X[0] * t, p.A[1] + p.X[1] * t, p.A[2] + p.X[2] * t }));
            hit = true;
        }
        return hit;
    }

    static bool PointInBox(PartBox p, double[] pt)
    {
        double[] v = { pt[0] - p.A[0], pt[1] - p.A[1], pt[2] - p.A[2] };
        double ax = v[0] * p.X[0] + v[1] * p.X[1] + v[2] * p.X[2];
        if (ax < -ProxTolFt || ax > p.L + ProxTolFt) return false;
        double y = v[0] * p.Y[0] + v[1] * p.Y[1] + v[2] * p.Y[2], z = v[0] * p.Z[0] + v[1] * p.Z[1] + v[2] * p.Z[2];
        return p.Round ? Math.Sqrt(y * y + z * z) <= p.R + ProxTolFt : Math.Abs(y) <= p.HW + ProxTolFt && Math.Abs(z) <= p.HH + ProxTolFt;
    }

    // "A-<bldg>-<service>-DUCT-<n>" -> service; else the system name without a trailing number ("<SYS>-<number>" -> <SYS>)
    static string SystemBase(string runName, string system)
    {
        string sv = ServiceOf(runName);
        if (sv != "") return sv;
        return Regex.Replace(system ?? "", @"[\s\-_]*\d+$", "").Trim();
    }

    // systems line for match.txt + detail: per name the components it spans (parts), per component the names it mixes
    static string[] SystemsNote(List<DuctSystem> systems)
    {
        var byName = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (DuctSystem s in systems)
            foreach (KeyValuePair<string, int> kv in s.Names)
            {
                List<string> l; if (!byName.TryGetValue(kv.Key, out l)) { l = new List<string>(); byName[kv.Key] = l; }
                l.Add(string.Format(Inv, "C{0} ({1})", s.Id, kv.Value));
            }
        int mixed = systems.Count(s => s.Names.Count > 1), single = systems.Count(s => s.Els.Count == 1);
        string line = string.Format(Inv, "systems: {0} connected components ({1} single parts), {2} names [{3}]; names over several components {4}; components mixing names {5}; joins by proximity only {6} parts",
            systems.Count, single, byName.Count, string.Join(" ", byName.Select(kv => kv.Key + ":" + kv.Value.Count).ToArray()),
            byName.Count(kv => kv.Value.Count > 1), mixed, systems.Sum(s => s.ProxJoins))
            + string.Format(Inv, "; weak joins blocked across two names {0}; parts connected across two names (SYSTEM_NAME_CONFLICT) {1}",
                Blocked.Count, systems.Sum(s => s.Els.Count(e => e.Flags.Contains("SYSTEM_NAME_CONFLICT"))));
        if (PrimarySystems.Count > 0)
            line = string.Format(Inv, "primary systems: {0}; other: {1} components ({2} parts)\n",
                string.Join("; ", PrimarySystems.Select(p =>
                {
                    DuctSystem m = systems.FirstOrDefault(s => s.Primary && s.Name == p);
                    var fr = systems.Where(s => s.Name == p + " fragments").ToList();
                    var pinned = systems.Where(s => s.Primary && s.Name != p && s.Name.StartsWith(p + " ")).ToList();
                    return string.Format(Inv, "{0} {1} parts{4} + {2} fragments ({3} parts)", p, m != null ? m.Els.Count : 0, fr.Count, fr.Sum(s => s.Els.Count),
                        string.Concat(pinned.Select(s => string.Format(Inv, " + {0} {1} parts", s.Name, s.Els.Count)).ToArray()));
                }).ToArray()),
                systems.Count(s => s.Name.StartsWith("other: ")), systems.Where(s => s.Name.StartsWith("other: ")).Sum(s => s.Els.Count))
                + (PinNotes.Count > 0 ? "pinned mains (DuctMains): " + string.Join("; ", PinNotes.ToArray()) + "\n" : "") + line;
        var sb = new StringBuilder();
        sb.AppendLine("system name -> components (parts):");
        foreach (KeyValuePair<string, List<string>> kv in byName) sb.AppendLine("  " + kv.Key + ": " + string.Join(", ", kv.Value.ToArray()));
        sb.AppendLine("components mixing names (parts):");
        foreach (DuctSystem s in systems) if (s.Names.Count > 1) sb.AppendLine("  C" + s.Id + ": " + string.Join(", ", s.Names.Select(kv => kv.Key + " (" + kv.Value + ")").ToArray()));
        sb.AppendLine("weak joins blocked across two system names (kind: Revit id (name) - Revit id (name)):");
        foreach (string b in Blocked.Distinct()) sb.AppendLine("  " + b);
        return new[] { line, sb.ToString() };
    }
}
