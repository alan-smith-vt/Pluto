using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

// FrameSidecar  --  a SAP2000 frame model's sections of the features sidecar (2026-11-05): what the viewer shows
// besides the geometry, written by SapToPluto for any .s2k it draws (a SAP export, or the frame workbook's preview):
//   supports   one item per joint held by restraints or springs (in its local axes), and per one-joint (grounded)
//              link: restraints[] with the kind the viewer draws (anchor, translation, rotation, spring, oneway with a
//              gap or friction, hook, limit), the direction each acts along, gap / friction / stiffness as SAP has them
//   links      every two-joint link: its joints, type (linear / gap / hook / friction / multilinear / damper / other),
//              property, local axes and its DOFs (fixed, nonlinear, KE, K, opening, friction)
//   loads      version 2: the load patterns, cases (with their loads) and combinations (with their items), and one item
//              per load row: joint forces and ground displacements (kind force / displacement, as the CAESAR items),
//              frame point loads (kind point: where along the frame, relative and absolute), distributed loads (kind
//              distributed: from / to, the value at each end), temperature loads (kind temperature); each names its
//              pattern, direction (Gravity, X, Y, Z, local 1 2 3, projected) and system
//   properties one item per frame (from / to are node ids, fromLabel / toLabel SAP's labels): section, shape and
//              dimensions, material, length, local axis angle, releases, insertion point, property modifiers
// Ids are the viewer's (SapLabels). Units are the file's. vault/format/features-sidecar.md. C# 5 (prefix Frame).

public static class FrameSidecar
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly string[] DofKeys = { "tx", "ty", "tz", "rx", "ry", "rz" };

    public sealed class Counts { public int Supports, Links, Loads, Properties; }

    public static Counts Add(FeaturesSidecar sc, FrameModel fm, SapLabels ids, string forceUnit, string lengthUnit)
    {
        var c = new Counts();
        var pos = new Dictionary<string, double[]>(StringComparer.Ordinal);
        foreach (var j in fm.Joints) pos[j.Label] = j.Xyz;
        Supports(sc, fm, ids, pos, c);
        sc.AddSection("links", Links(fm, ids, pos, c));
        sc.AddSection("loads", Loads(fm, ids, pos, forceUnit, lengthUnit, c));
        sc.AddSection("properties", Properties(fm, ids, pos, forceUnit, lengthUnit, c));
        return c;
    }

    // ---------------- geometry

    static double[] Sub(double[] a, double[] b) { return new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] }; }
    static double Len(double[] a) { return Math.Sqrt(a[0] * a[0] + a[1] * a[1] + a[2] * a[2]); }
    static double[] Unit(double[] a) { double l = Len(a); return l < 1e-12 ? new[] { 0.0, 0, 0 } : new[] { a[0] / l, a[1] / l, a[2] / l }; }
    static double[] Cross(double[] a, double[] b) { return new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] }; }

    // SAP's default local axes for a line object (frame or link) along `axis1`, turned by `angle` (deg) about local 1:
    // local 2 is +Z projected off the member (+X for a vertical one), local 3 = 1 x 2
    public static double[][] LocalAxes(double[] axis1, double angle)
    {
        var x = Unit(axis1);
        double[] l2;
        if (Math.Abs(x[2]) > 1.0 - 1e-6) l2 = new[] { 1.0, 0, 0 };
        else l2 = Unit(new[] { -x[2] * x[0], -x[2] * x[1], 1.0 - x[2] * x[2] });
        var l3 = Cross(x, l2);
        if (angle != 0)
        {
            double t = angle * Math.PI / 180, c = Math.Cos(t), s = Math.Sin(t);
            var n2 = new[] { c * l2[0] + s * l3[0], c * l2[1] + s * l3[1], c * l2[2] + s * l3[2] };
            var n3 = new[] { -s * l2[0] + c * l3[0], -s * l2[1] + c * l3[1], -s * l2[2] + c * l3[2] };
            l2 = n2; l3 = n3;
        }
        return new[] { x, l2, l3 };
    }

    // a joint's local axes (columns of Rz(A) Ry(B) Rx(C)) as three vectors
    static double[][] JointAxes(double[] abc)
    {
        double a = abc[0] * Math.PI / 180, b = abc[1] * Math.PI / 180, cc0 = abc[2] * Math.PI / 180;
        double ca = Math.Cos(a), sa = Math.Sin(a), cb = Math.Cos(b), sb = Math.Sin(b), cc = Math.Cos(cc0), sc = Math.Sin(cc0);
        return new[] {
            new[] { ca * cb, sa * cb, -sb },
            new[] { ca * sb * sc - sa * cc, sa * sb * sc + ca * cc, cb * sc },
            new[] { ca * sb * cc + sa * sc, sa * sb * cc - ca * sc, cb * cc } };
    }

    // the global DOF a direction is closest to (tx ty tz, or rx ry rz for a rotation)
    static int Dominant(double[] d) { int k = 0; for (int i = 1; i < 3; i++) if (Math.Abs(d[i]) > Math.Abs(d[k])) k = i; return k; }

    // ---------------- supports

    static void Supports(FeaturesSidecar sc, FrameModel fm, SapLabels ids, Dictionary<string, double[]> pos, Counts c)
    {
        sc.WriteSupports = true;
        var restrained = new HashSet<uint>(); var others = new List<uint>();   // the springs' and grounded links' joints get a ticked group
        foreach (var j in fm.Joints)
        {
            var res = j.Restraint; var spr = j.Spring;
            if (!res.Any(b => b) && !spr.Any(v => v != 0)) continue;
            var ax = JointAxes(j.LocalAngles);
            var dof = new List<KeyValuePair<string, string>>();
            var entries = new List<string>();
            bool anchor = res.All(b => b);
            if (anchor)
            {
                entries.Add("{\"type\": \"ANC\", \"kind\": \"anchor\"}");
                for (int i = 0; i < 6; i++) dof.Add(new KeyValuePair<string, string>(DofKeys[i], "fixed"));
            }
            else
            {
                for (int i = 0; i < 6; i++)
                {
                    if (!res[i] && spr[i] == 0) continue;
                    var d = ax[i % 3];
                    string kind = res[i] ? (i < 3 ? "translation" : "rotation") : "spring";
                    string e = "{\"type\": " + Q(FrameJoint.Dofs[i] + (res[i] ? "" : " spring")) + ", \"kind\": " + Q(kind) + ", \"direction\": " + V(d);
                    if (!res[i]) e += ", \"stiffness\": " + N(spr[i]);
                    entries.Add(e + "}");
                    dof.Add(new KeyValuePair<string, string>(DofKeys[(i < 3 ? 0 : 3) + Dominant(d)], res[i] ? "fixed" : "spring"));
                }
            }
            uint id = (uint)ids.Id("joint", j.Label);
            if (res.Any(b => b)) restrained.Add(id); else if (!others.Contains(id)) others.Add(id);
            string name = j.Label + " " + (anchor ? "fixed" : string.Join(" ", Enumerable.Range(0, 6).Where(i => res[i] || spr[i] != 0).Select(i => FrameJoint.Dofs[i] + (res[i] ? "" : "~")).ToArray()));
            sc.AddSupport(name, new[] { id }, dof, new[] { "sap", "support" }, "\"restraints\": [" + string.Join(", ", entries.ToArray()) + "], \"source\": {\"exporter\": \"sap\", \"joint\": " + Q(j.Label) + "}");
            c.Supports++;
        }
        // one-joint links: supports of their joint
        var props = fm.LinkProps.ToDictionary(p => p.Label, p => p, StringComparer.Ordinal);
        foreach (var l in fm.Links)
        {
            if (l.JointJ != "") continue;
            FrameLinkProp p;
            props.TryGetValue(l.Property, out p);
            var ax = LocalAxes(new[] { 0.0, 0, 1 }, l.Angle);     // a one-joint link's local 1 is +Z (SAP's default)
            string type = TypeOf(p);
            var entries = new List<string>();
            var dof = new List<KeyValuePair<string, string>>();
            if (p != null)
                foreach (var r in p.DofRows)
                {
                    string dn = (r.Get("DOF") ?? "").ToUpperInvariant();
                    int k = Array.IndexOf(FrameJoint.Dofs, dn);
                    if (k < 0) continue;
                    bool fixd = r.Yes("Fixed"), nl = r.Yes("NonLinear");
                    double ke = r.Num(k < 3 ? "TransKE" : "RotKE", 0), kk = r.Num(k < 3 ? "TransK" : "RotK", ke), open = r.Num("Open", 0);
                    if (!fixd && ke == 0 && kk == 0) continue;
                    string kind; string letter;
                    if (type == "gap") { kind = "oneway"; letter = "gap"; }
                    else if (type == "hook") { kind = "hook"; letter = "gap"; }
                    else if (type == "friction") { if (k != 0) continue; kind = "oneway"; letter = "+"; }
                    else if (type == "multilinear") { kind = "limit"; letter = "limit"; }
                    else if (fixd) { kind = k < 3 ? "translation" : "rotation"; letter = "fixed"; }
                    else { kind = "spring"; letter = "spring"; }
                    var d = ax[k % 3];
                    string e = "{\"type\": " + Q(l.Property + " " + dn) + ", \"kind\": " + Q(kind) + ", \"direction\": " + V(d) + ", \"tag\": " + Q(l.Label);
                    if (!fixd) e += ", \"stiffness\": " + N(nl && kk != 0 ? kk : ke);
                    if (open != 0) e += ", \"gap\": " + N(open);
                    if (type == "friction")
                    {
                        double m2 = 0;
                        foreach (var q in p.DofRows) if ((q.Get("DOF") ?? "").Equals("U2", StringComparison.OrdinalIgnoreCase)) m2 = q.Num("Slow", 0);
                        if (m2 != 0) e += ", \"friction\": " + N(m2);
                    }
                    entries.Add(e + "}");
                    dof.Add(new KeyValuePair<string, string>(DofKeys[(k < 3 ? 0 : 3) + Dominant(d)], letter));
                }
            if (entries.Count == 0) entries.Add("{\"type\": " + Q(l.Property) + ", \"kind\": \"other\", \"tag\": " + Q(l.Label) + "}");
            uint lj = (uint)ids.Id("joint", l.JointI);
            if (!others.Contains(lj)) others.Add(lj);
            sc.AddSupport(l.Label + " " + l.Property, new[] { lj }, dof, new[] { "sap", "support", "link" },
                          "\"restraints\": [" + string.Join(", ", entries.ToArray()) + "], \"source\": {\"exporter\": \"sap\", \"link\": " + Q(l.Label) + "}");
            c.Supports++;
        }
        others.RemoveAll(restrained.Contains);
        // ticked, like the restraint groups: a support whose joint only unticked groups list is hidden (supports.js)
        if (others.Count > 0) sc.AddNodeGroup("Springs and grounded links", null, others, new[] { "sap", "support" }, "_SPRINGS_LINKS").Hidden = false;
    }

    // linear / gap / hook / friction / multilinear / damper / other
    public static string TypeOf(FrameLinkProp p)
    {
        string t = p == null ? "" : p.LinkType.ToLowerInvariant();
        if (t == "linear") return "linear";
        if (t == "gap") return "gap";
        if (t == "hook") return "hook";
        if (t.Contains("friction")) return "friction";
        if (t.Contains("multilinear")) return "multilinear";
        if (t.Contains("damper")) return "damper";
        return "other";
    }

    // ---------------- links

    static string Links(FrameModel fm, SapLabels ids, Dictionary<string, double[]> pos, Counts c)
    {
        var props = fm.LinkProps.ToDictionary(p => p.Label, p => p, StringComparer.Ordinal);
        var sb = new StringBuilder("{\"version\": 1, \"items\": [");
        bool first = true;
        foreach (var l in fm.Links)
        {
            if (l.JointJ == "") continue;
            double[] a, b;
            if (!pos.TryGetValue(l.JointI, out a) || !pos.TryGetValue(l.JointJ, out b)) continue;
            FrameLinkProp p;
            props.TryGetValue(l.Property, out p);
            var d = Sub(b, a);
            double len = Len(d);
            var ax = LocalAxes(len > 1e-9 ? d : new[] { 0.0, 0, 1 }, l.Angle);
            sb.Append(first ? "\n  " : ",\n  ");
            first = false;
            sb.Append("{\"id\": ").Append(Q("sap-link-" + l.Label)).Append(", \"name\": ").Append(Q(l.Label + " " + l.Property));
            sb.Append(", \"label\": ").Append(Q(l.Label)).Append(", \"type\": ").Append(Q(TypeOf(p))).Append(", \"linkType\": ").Append(Q(p == null ? "" : p.LinkType));
            sb.Append(", \"nodeIds\": [").Append(ids.Id("joint", l.JointI)).Append(", ").Append(ids.Id("joint", l.JointJ)).Append(']');
            sb.Append(", \"property\": ").Append(Q(l.Property)).Append(", \"length\": ").Append(G(len));
            sb.Append(", \"axis1\": ").Append(V(ax[0])).Append(", \"axis2\": ").Append(V(ax[1]));
            sb.Append(", \"dofs\": {").Append(Dofs(p)).Append("}, \"tags\": [\"sap\", \"link\"]}");
            c.Links++;
        }
        return sb.Append(first ? "]}" : "\n]}").ToString();
    }

    static string Dofs(FrameLinkProp p)
    {
        if (p == null) return "";
        var parts = new List<string>();
        foreach (var r in p.DofRows)
        {
            string dn = (r.Get("DOF") ?? "").ToUpperInvariant();
            if (Array.IndexOf(FrameJoint.Dofs, dn) < 0) continue;
            bool rot = dn.StartsWith("R");
            var f = new List<string> { "\"fixed\": " + (r.Yes("Fixed") ? "true" : "false"), "\"nonlinear\": " + (r.Yes("NonLinear") ? "true" : "false") };
            Action<string, string> num = (key, field) => { if (r.Has(field)) f.Add("\"" + key + "\": " + N(r.Num(field, 0))); };
            num("ke", rot ? "RotKE" : "TransKE"); num("ce", rot ? "RotCE" : "TransCE"); num("k", rot ? "RotK" : "TransK");
            num("open", "Open"); num("muSlow", "Slow"); num("muFast", "Fast"); num("radius", "Radius");
            parts.Add(Q(dn) + ": {" + string.Join(", ", f.ToArray()) + "}");
        }
        return string.Join(", ", parts.ToArray());
    }

    // ---------------- loads

    static string Loads(FrameModel fm, SapLabels ids, Dictionary<string, double[]> pos, string forceUnit, string lengthUnit, Counts c)
    {
        var sb = new StringBuilder("{\"version\": 2, \"units\": {\"force\": " + Q(forceUnit) + ", \"length\": " + Q(lengthUnit) + ", \"moment\": " + Q(forceUnit + "-" + lengthUnit) +
                                   ", \"uniform\": " + Q(forceUnit + "/" + lengthUnit) + ", \"temperature\": " + Q(TempUnit(fm)) + "}");
        sb.Append(",\n\"patterns\": [").Append(string.Join(", ", fm.Patterns.Select(p => "{\"name\": " + Q(p.Label) + ", \"type\": " + Q(p.DesignType) + ", \"selfWeight\": " + N(p.SelfWeight) + "}").ToArray())).Append(']');
        sb.Append(",\n\"cases\": [").Append(string.Join(", ", fm.Cases.Select(k => "{\"name\": " + Q(k.Label) + ", \"type\": " + Q(k.Type) + ", \"initial\": " + Q(k.Initial) +
            ", \"loads\": [" + string.Join(", ", k.Loads.Select(x => "{\"type\": " + Q(x[0]) + ", \"name\": " + Q(x[1]) + ", \"sf\": " + N(D(x[2], 1)) + "}").ToArray()) + "]}").ToArray())).Append(']');
        sb.Append(",\n\"combinations\": [").Append(string.Join(", ", fm.Combos.Select(k => "{\"name\": " + Q(k.Label) + ", \"type\": " + Q(k.Type) +
            ", \"items\": [" + string.Join(", ", k.Items.Select(x => "{\"type\": " + Q(x[0]) + ", \"name\": " + Q(x[1]) + ", \"sf\": " + N(D(x[2], 1)) + "}").ToArray()) + "]}").ToArray())).Append(']');
        sb.Append(",\n\"items\": [");
        bool first = true;
        var frames = fm.Frames.ToDictionary(f => f.Label, f => f, StringComparer.Ordinal);
        foreach (var ld in fm.Loads)
        {
            var r = ld.Row;
            if (r == null) continue;
            string item = null, pat = ld.Pattern, target = ld.Target;
            string common = "\"id\": " + Q("sap-load-" + ld.Uid.ToString(Inv)) + ", \"pattern\": " + Q(pat) + ", \"coordSys\": " + Q(r.Get("CoordSys") ?? "GLOBAL");
            if (ld.Table.Equals(FrameLoad.JointForce, StringComparison.OrdinalIgnoreCase) || ld.Table.Equals(FrameLoad.GroundDisp, StringComparison.OrdinalIgnoreCase))
            {
                bool disp = ld.Table.Equals(FrameLoad.GroundDisp, StringComparison.OrdinalIgnoreCase);
                string[] keys = disp ? new[] { "U1", "U2", "U3", "R1", "R2", "R3" } : new[] { "F1", "F2", "F3", "M1", "M2", "M3" };
                item = "{" + common + ", \"name\": " + Q(target + " " + pat) + ", \"kind\": " + Q(disp ? "displacement" : "force") + ", \"nodeIds\": [" + ids.Id("joint", target) + "]" +
                       ", \"vectors\": [{\"v\": 1, \"values\": [" + string.Join(", ", keys.Select(k => N(r.Num(k, 0))).ToArray()) + "]}]}";
            }
            else
            {
                FrameMember f;
                if (!frames.TryGetValue(target, out f)) continue;
                double[] a, b;
                double len = pos.TryGetValue(f.JointI, out a) && pos.TryGetValue(f.JointJ, out b) ? Len(Sub(b, a)) : 0;
                string mem = "\"members\": [{\"domain\": \"beams\", \"ids\": [" + ids.Id("frame", target) + "]}], \"frame\": " + Q(target);
                string dir = r.Get("Dir") ?? "Gravity", type = (r.Get("Type") ?? "Force").ToLowerInvariant();
                bool rel = (r.Get("DistType") ?? "RelDist").StartsWith("Rel", StringComparison.OrdinalIgnoreCase);
                Func<string, string, string> at = (relKey, absKey) =>
                {
                    double rv = r.Num(relKey, double.NaN), av = r.Num(absKey, double.NaN);
                    if (rel || double.IsNaN(av)) av = double.IsNaN(rv) ? 0 : rv * len; else rv = len > 0 ? av / len : 0;
                    return "{\"rel\": " + G(rv) + ", \"abs\": " + G(av) + "}";
                };
                if (ld.Table.Equals(FrameLoad.Point, StringComparison.OrdinalIgnoreCase))
                    item = "{" + common + ", \"name\": " + Q(target + " " + pat) + ", \"kind\": \"point\", " + mem + ", \"type\": " + Q(type) + ", \"dir\": " + Q(dir) +
                           ", \"at\": " + at("RelDist", "AbsDist") + ", \"value\": " + N(r.Num("Force", r.Num("Moment", 0))) + "}";
                else if (ld.Table.Equals(FrameLoad.Distributed, StringComparison.OrdinalIgnoreCase))
                    item = "{" + common + ", \"name\": " + Q(target + " " + pat) + ", \"kind\": \"distributed\", " + mem + ", \"type\": " + Q(type) + ", \"dir\": " + Q(dir) +
                           ", \"from\": " + at("RelDistA", "AbsDistA") + ", \"to\": " + at("RelDistB", "AbsDistB") +
                           ", \"valueA\": " + N(r.Num("FOverLA", r.Num("MOverLA", 0))) + ", \"valueB\": " + N(r.Num("FOverLB", r.Num("MOverLB", 0))) + "}";
                else if (ld.Table.Equals(FrameLoad.Temperature, StringComparison.OrdinalIgnoreCase))
                    item = "{" + common + ", \"name\": " + Q(target + " " + pat) + ", \"kind\": \"temperature\", " + mem + ", \"type\": " + Q(r.Get("Type") ?? "Temperature") +
                           ", \"dT\": " + N(r.Num("Temp", 0)) + "}";
            }
            if (item == null) continue;
            sb.Append(first ? "\n  " : ",\n  ").Append(item);
            first = false;
            c.Loads++;
        }
        return sb.Append(first ? "]}" : "\n]}").ToString();
    }

    static string TempUnit(FrameModel fm) { string u = fm.Units; int k = u.LastIndexOf(','); return k < 0 ? "" : u.Substring(k + 1).Trim(); }

    // ---------------- properties

    static string Properties(FrameModel fm, SapLabels ids, Dictionary<string, double[]> pos, string forceUnit, string lengthUnit, Counts c)
    {
        var sb = new StringBuilder("{\"version\": 1, \"units\": {\"length\": " + Q(lengthUnit) + ", \"force\": " + Q(forceUnit) + ", \"diameter\": " + Q(lengthUnit) + "}, \"items\": [");
        var secs = fm.Sections.ToDictionary(s => s.Label, s => s, StringComparer.OrdinalIgnoreCase);
        var kindOf = new Dictionary<string, string>(StringComparer.Ordinal); var lineOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var g in fm.Groups)
        {
            string n = g.Label;
            bool kind = n.StartsWith("PL_KIND_", StringComparison.OrdinalIgnoreCase), line = n.StartsWith("PL_LINE_", StringComparison.OrdinalIgnoreCase);
            if (!kind && !line) continue;
            foreach (var m in g.Members) if (m[0].Equals("Frame", StringComparison.OrdinalIgnoreCase)) (kind ? kindOf : lineOf)[m[1]] = n.Substring(8);
        }
        bool first = true;
        foreach (var f in fm.Frames)
        {
            double[] a, b;
            if (!pos.TryGetValue(f.JointI, out a) || !pos.TryGetValue(f.JointJ, out b)) continue;
            var d = Sub(b, a);
            FrameSection s;
            secs.TryGetValue(f.Section, out s);
            var v = new List<string> { "\"section\": " + Q(f.Section), "\"shape\": " + Q(s == null ? "" : s.Shape), "\"material\": " + Q(s == null ? "" : s.Material),
                                       "\"dx\": " + G(d[0]), "\"dy\": " + G(d[1]), "\"dz\": " + G(d[2]), "\"length\": " + G(Len(d)) };
            if (s != null) foreach (var k in new[] { "t3", "t2", "tf", "tw", "t2b", "tfb" }) { double x = s.Dim(k); if (x != 0) v.Add(Q(k) + ": " + N(x)); }
            if (f.Angle != 0) v.Add("\"angle\": " + N(f.Angle));
            string rel = ReleaseText(f);
            if (rel != "") v.Add("\"releases\": " + Q(rel));
            if (f.CardinalPoint != 10) v.Add("\"cardinal\": " + f.CardinalPoint);
            var mods = f.PropertyModifiers;
            if (mods.Any(x => x != 1)) v.Add("\"modifiers\": [" + string.Join(", ", mods.Select(N).ToArray()) + "]");
            string kind, line;
            kindOf.TryGetValue(f.Label, out kind); lineOf.TryGetValue(f.Label, out line);
            sb.Append(first ? "\n  " : ",\n  ");
            first = false;
            sb.Append("{\"element\": ").Append(ids.Id("frame", f.Label)).Append(", \"label\": ").Append(Q(f.Label));
            sb.Append(", \"from\": ").Append(ids.Id("joint", f.JointI)).Append(", \"to\": ").Append(ids.Id("joint", f.JointJ));
            sb.Append(", \"fromLabel\": ").Append(Q(f.JointI)).Append(", \"toLabel\": ").Append(Q(f.JointJ));
            sb.Append(", \"kind\": ").Append(Q(kind ?? (s == null ? "Frame" : s.Shape))).Append(", \"name\": \"\", \"line\": ").Append(Q(line ?? ""));
            sb.Append(", \"members\": [{\"domain\": \"beams\", \"ids\": [").Append(ids.Id("frame", f.Label)).Append("]}], \"values\": {").Append(string.Join(", ", v.ToArray())).Append("}}");
            c.Properties++;
        }
        return sb.Append(first ? "]}" : "\n]}").ToString();
    }

    // ---------------- review

    // the Checks panel's section: errors first, then warnings, notices, info; nodes and elements are the viewer's ids
    public static string Review(IEnumerable<PipeReviewItem> review)
    {
        var all = review.ToList();
        var order = all.Select((x, k) => new { x, k }).OrderBy(a => PipeReview.Rank(a.x.Level)).ThenBy(a => a.k).Select(a => a.x).ToList();
        var items = new List<string>();
        int id = 1;
        foreach (var r in order)
        {
            var s = new StringBuilder();
            s.Append("{\"id\": ").Append(Q("r" + id++)).Append(", \"level\": ").Append(Q(r.Level)).Append(", \"code\": ").Append(Q(r.Code)).Append(", \"text\": ").Append(Q(r.Text));
            if (r.Nodes.Count > 0) s.Append(", \"nodeIds\": [").Append(string.Join(", ", r.Nodes.Select(n => n.ToString(Inv)).ToArray())).Append("]");
            if (r.Elements.Count > 0)
            {
                string els = string.Join(", ", r.Elements.Select(n => n.ToString(Inv)).ToArray());
                s.Append(", \"elements\": [").Append(els).Append("], \"members\": [{\"domain\": \"beams\", \"ids\": [").Append(els).Append("]}]");
            }
            items.Add(s.Append("}").ToString());
        }
        return "{\"version\": 1, \"summary\": " + Q(PipeReview.Summary(all)) + ", \"items\": [" + (items.Count > 0 ? "\n  " + string.Join(",\n  ", items.ToArray()) + "\n" : "") + "]}";
    }

    // "I: M2 M3; J: M2 M3 (M3 partly fixed)"; "" for none
    public static string ReleaseText(FrameMember f)
    {
        var r = f.Releases; var p = f.PartialFixity;
        if (!r.Any(x => x)) return "";
        string[] names = { "P", "V2", "V3", "T", "M2", "M3" };
        Func<int, string> end = o => string.Join(" ", Enumerable.Range(0, 6).Where(i => r[o + i]).Select(i => names[i] + (p[o + i] != 0 ? "~" : "")).ToArray());
        string i0 = end(0), j0 = end(6);
        return "I: " + (i0 == "" ? "fixed" : i0) + "; J: " + (j0 == "" ? "fixed" : j0);
    }

    // ---------------- JSON

    static string Q(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char ch in s ?? "")
        {
            if (ch == '"' || ch == '\\') sb.Append('\\').Append(ch);
            else if (ch < 0x20 || ch > 0x7e) sb.Append("\\u").Append(((int)ch).ToString("x4"));
            else sb.Append(ch);
        }
        return sb.Append('"').ToString();
    }
    static string N(double v) { return double.IsNaN(v) || double.IsInfinity(v) ? "null" : FrameModel.Fmt(v); }
    // a derived number (a length, a difference of coordinates): 12 digits, so 0.4557673999999992 reads 0.4557674
    static string G(double v) { return double.IsNaN(v) || double.IsInfinity(v) ? "null" : (Math.Abs(v) < 1e-12 ? 0 : v).ToString("G12", Inv); }
    static string V(double[] d) { return "[" + N(Round(d[0])) + ", " + N(Round(d[1])) + ", " + N(Round(d[2])) + "]"; }
    static double Round(double x) { return Math.Abs(x) < 1e-12 ? 0 : Math.Round(x, 12); }
    static double D(string s, double def) { double v; return double.TryParse(s, NumberStyles.Float, Inv, out v) ? v : def; }
}
