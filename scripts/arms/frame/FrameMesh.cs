using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

// FrameMesh  --  SAP's automatic frame mesh at joints, made explicit for STAAD (2026-11-24). A frame with AutoMesh=Yes and
// AtJoints=Yes is joined, in SAP's analysis, to every joint on its span (a seeded check model's support points under the
// steel that carries them, for one). A frame with no auto mesh row takes SAP's default, which is the same
// (vault/format/sap-s2k-tables.md, "Frames join joints on their span"). The frame is split at those joints, into the
// pieces SAP's analysis meshes it into: when a workbook is written (Build-Frame -Xlsx, so the model says what SAP
// analyses), and before FrameStd writes a model that still has such a frame (STAAD joins a member at its ends only):
//   - the first piece keeps the frame's label, the others take <label>_2, <label>_3 ...; each keeps every row of the
//     frame's (section, local axes, insertion point, property modifiers, auto mesh, SAP's design, its STAAD design
//     values: StaadDesign.TMembers) and its groups. The joint is
//     on the frame's line, so a piece with the frame's insertion point lies as the frame's part does;
//   - I-end releases and partial fixity stay with the first piece, J-end ones with the last; end lengths likewise (a
//     joint inside an end zone is refused);
//   - point loads go to the piece they fall on, distributed loads are cut at the joints (a linear one keeps its line),
//     temperatures go to every piece.
// Not split, with the reason: joint offsets not the same at both ends (a piece's would be in between), end offsets SAP
// works out, a joint inside an end zone, loads in other tables. C# 5 (prefix Frame).
public static class FrameMesh
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // the model with those frames split (a copy; the model itself when none is). split: each split frame's label and its
    // pieces; refused: the frames SAP joins along their length that are not split, each with its reason; held: "frame F
    // at joint J" where the joint the frame is split at is held to ground (restrained): SAP holds the frame there too,
    // which a seeded support point under steel that should carry it would not want
    public static FrameModel SplitAtJoints(FrameModel m, Dictionary<string, List<string>> split, Dictionary<string, string> refused, List<string> held)
    {
        var plan = Plan(m);
        if (plan.Count == 0) return m;
        var c = FrameS2k.Read(FrameS2k.ToDoc(m));
        var frames = c.Frames.ToDictionary(f => f.Label, f => f, StringComparer.Ordinal);
        var labels = new HashSet<string>(c.Frames.Select(f => f.Label), StringComparer.Ordinal);
        foreach (var kv in plan)
        {
            FrameMember f;
            if (!frames.TryGetValue(kv.Key, out f)) continue;
            string why = Splittable(c, f, kv.Value);
            if (why != null) { refused[f.Label] = why; continue; }
            foreach (var p in kv.Value)
            {
                var j = c.JointNamed(p.Value);
                if (j != null && j.Restraint.Any(x => x)) held.Add("frame " + f.Label + " at joint " + p.Value);
            }
            split[f.Label] = Split(c, f, kv.Value, labels);
        }
        return split.Count > 0 ? c : m;
    }

    // whether SAP meshes the frame at joints on its span: its row's AutoMesh and AtJoints, SAP's default (Yes) without one
    public static bool AtJoints(FrameMember f)
    {
        var am = f.Rec(FrameMember.AutoMesh);
        return am == null || (am.Yes("AutoMesh") && am.Yes("AtJoints"));
    }

    // the frames SAP meshes at joints on their span: label -> the joints, by distance from JointI
    static Dictionary<string, List<KeyValuePair<double, string>>> Plan(FrameModel m)
    {
        var plan = new Dictionary<string, List<KeyValuePair<double, string>>>(StringComparer.Ordinal);
        var pos = m.Joints.Where(j => j.Label != "").ToDictionary(j => j.Label, j => j.Xyz, StringComparer.Ordinal);
        foreach (var f in m.Frames)
        {
            if (!AtJoints(f)) continue;
            double[] a, b;
            if (!pos.TryGetValue(f.JointI, out a) || !pos.TryGetValue(f.JointJ, out b)) continue;
            double[] d = { b[0] - a[0], b[1] - a[1], b[2] - a[2] };
            double len = Math.Sqrt(d[0] * d[0] + d[1] * d[1] + d[2] * d[2]);
            if (len < 1e-9) continue;
            var on = new List<KeyValuePair<double, string>>();
            foreach (var kv in pos)
            {
                if (kv.Key == f.JointI || kv.Key == f.JointJ) continue;
                var p = kv.Value;
                double t = ((p[0] - a[0]) * d[0] + (p[1] - a[1]) * d[1] + (p[2] - a[2]) * d[2]) / len;
                if (t <= 1e-6 * len || t >= len * (1 - 1e-6)) continue;
                double ex = p[0] - a[0] - d[0] * t / len, ey = p[1] - a[1] - d[1] * t / len, ez = p[2] - a[2] - d[2] * t / len;
                if (Math.Sqrt(ex * ex + ey * ey + ez * ez) < 1e-6 * len + 1e-9) on.Add(new KeyValuePair<double, string>(t, kv.Key));
            }
            if (on.Count > 0) plan[f.Label] = on.OrderBy(x => x.Key).ToList();
        }
        return plan;
    }

    // why the frame cannot be split exactly at those joints (null: it can)
    static string Splittable(FrameModel m, FrameMember f, List<KeyValuePair<double, string>> at)
    {
        var jo = f.JointOffsets;
        if (jo[0] != jo[3] || jo[1] != jo[4] || jo[2] != jo[5])
            return "its joint offsets are not the same at both ends, so its pieces' would be in between";
        string type = f.EndOffsetType;
        if (type != "")
        {
            var el = f.EndLengths;
            if (!type.Equals("User", StringComparison.OrdinalIgnoreCase) && (el[0] != 0 || el[1] != 0)) return "its end offsets are SAP's own (" + type + ")";
            double len = Length(m, f);
            foreach (var p in at)
                if (p.Key <= el[0] || p.Key >= len - el[1]) return "joint " + p.Value + " is inside an end zone";
        }
        foreach (var ld in m.Loads.Where(x => !x.Table.StartsWith("JOINT", StringComparison.OrdinalIgnoreCase) && x.Target == f.Label))
            if (!Is(ld.Table, FrameLoad.Point) && !Is(ld.Table, FrameLoad.Distributed) && !Is(ld.Table, FrameLoad.Temperature)) return "it has a load in " + ld.Table;
        return null;
    }

    static double Length(FrameModel m, FrameMember f)
    {
        var a = m.JointNamed(f.JointI).Xyz; var b = m.JointNamed(f.JointJ).Xyz;
        return Math.Sqrt((b[0] - a[0]) * (b[0] - a[0]) + (b[1] - a[1]) * (b[1] - a[1]) + (b[2] - a[2]) * (b[2] - a[2]));
    }

    // split it: the pieces' labels, the first its own
    static List<string> Split(FrameModel m, FrameMember f, List<KeyValuePair<double, string>> at, HashSet<string> labels)
    {
        double len = Length(m, f);
        var t = new List<double> { 0 }; t.AddRange(at.Select(p => p.Key)); t.Add(len);
        var ends = new List<string> { f.JointI }; ends.AddRange(at.Select(p => p.Value)); ends.Add(f.JointJ);
        int n = t.Count - 1;
        var rel = f.Releases; var pf = f.PartialFixity; var el = f.EndLengths; bool zones = f.EndOffsetType != "";
        var pieces = new List<FrameMember> { f };
        var names = new List<string> { f.Label };
        for (int k = 1; k < n; k++)
        {
            string name = f.Label + "_" + (k + 1).ToString(Inv);
            for (int q = 2; labels.Contains(name); q++) name = f.Label + "_" + (k + 1).ToString(Inv) + "_" + q.ToString(Inv);
            labels.Add(name);
            var p = new FrameMember { Uid = m.NextUid++ };
            foreach (var r in f.Recs) { var cr = r.Clone(); cr.Row = null; cr.Remove("GUID"); p.Recs.Add(cr); }
            p.Label = name;
            m.Attach(p);
            pieces.Add(p); names.Add(name);
        }
        for (int k = 0; k < n; k++)
        {
            var p = pieces[k];
            p.JointI = ends[k]; p.JointJ = ends[k + 1];
            // the frame's ends' releases on the pieces at its ends
            var r = new bool[12]; var s = new double[12];
            for (int i = 0; i < 6; i++)
            {
                if (k == 0) { r[i] = rel[i]; s[i] = pf[i]; }
                if (k == n - 1) { r[6 + i] = rel[6 + i]; s[6 + i] = pf[6 + i]; }
            }
            p.Releases = r;
            if (r.Any(x => x)) p.PartialFixity = s;
            if (zones)
            {
                double li = k == 0 ? el[0] : 0, lj = k == n - 1 ? el[1] : 0;
                if (li == 0 && lj == 0) p.Drop(FrameMember.EndOffsets); else p.SetEndLengths(li, lj, el[2]);
            }
        }
        // its groups: every piece
        foreach (var g in m.Groups)
        {
            var mem = g.Members.ToList();
            if (!mem.Any(x => Is(x[0], "Frame") && x[1] == f.Label)) continue;
            foreach (string nm in names.Skip(1)) mem.Add(new[] { "Frame", nm });
            g.SetMembers(mem);
        }
        // its loads, by where they fall
        foreach (var ld in m.Loads.Where(x => !x.Table.StartsWith("JOINT", StringComparison.OrdinalIgnoreCase) && x.Target == f.Label).ToList())
        {
            var row = ld.Row;
            if (Is(ld.Table, FrameLoad.Temperature))
            {
                foreach (string nm in names.Skip(1)) Copy(m, ld).Row.Set("Frame", nm);
                continue;
            }
            bool relative = (row.Get("DistType") ?? "RelDist").StartsWith("Rel", StringComparison.OrdinalIgnoreCase);
            if (Is(ld.Table, FrameLoad.Point))
            {
                double x = relative ? row.Num("RelDist", 0) * len : row.Num("AbsDist", 0);
                int k = 0;
                while (k < n - 1 && x > t[k + 1]) k++;
                double lk = t[k + 1] - t[k], u = Math.Max(0, Math.Min(lk, x - t[k]));
                row.Set("Frame", names[k]);
                row.SetNum("RelDist", u / lk).SetNum("AbsDist", u);
                continue;
            }
            // distributed: the part on each piece, its values on the load's line
            bool moment = Is(row.Get("Type") ?? "Force", "Moment");
            string ka = moment ? "MOverLA" : "FOverLA", kb = moment ? "MOverLB" : "FOverLB";
            double xa = relative ? row.Num("RelDistA", 0) * len : row.Num("AbsDistA", 0), xb = relative ? row.Num("RelDistB", 0) * len : row.Num("AbsDistB", 0);
            double wa = row.Num(ka, 0), wb = row.Num(kb, 0);
            if (xb < xa) { double q = xa; xa = xb; xb = q; q = wa; wa = wb; wb = q; }
            Func<double, double> w = x => xb - xa <= 0 ? wa : wa + (wb - wa) * (x - xa) / (xb - xa);
            if (xb - xa <= 1e-12 * len)
            {
                // no length: left on its piece as written, for FrameStd to judge
                int k0 = 0;
                while (k0 < n - 1 && xa > t[k0 + 1]) k0++;
                double l0 = t[k0 + 1] - t[k0];
                row.Set("Frame", names[k0]);
                row.SetNum("RelDistA", (xa - t[k0]) / l0).SetNum("RelDistB", (xb - t[k0]) / l0).SetNum("AbsDistA", xa - t[k0]).SetNum("AbsDistB", xb - t[k0]);
                continue;
            }
            bool first = true;
            for (int k = 0; k < n; k++)
            {
                double lo = Math.Max(xa, t[k]), hi = Math.Min(xb, t[k + 1]);
                if (hi - lo <= 1e-12 * len) continue;
                var part = first ? ld : Copy(m, ld);
                first = false;
                double lk = t[k + 1] - t[k];
                part.Row.Set("Frame", names[k]);
                part.Row.SetNum("RelDistA", (lo - t[k]) / lk).SetNum("RelDistB", (hi - t[k]) / lk).SetNum("AbsDistA", lo - t[k]).SetNum("AbsDistB", hi - t[k]);
                part.Row.SetNum(ka, w(lo)).SetNum(kb, w(hi));
            }
        }
        return names;
    }

    static FrameLoad Copy(FrameModel m, FrameLoad ld)
    {
        var o = m.AddLoad(ld.Table);
        o.Recs.Clear();
        var r = ld.Row.Clone(); r.Row = null; o.Recs.Add(r);
        return o;
    }

    static bool Is(string a, string b) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
}
