using System;
using System.Collections.Generic;
using System.Linq;

// PipeJoints  --  joints (couplings, hinges, slip and ball joints, expansion joints with control rods) as the pipe model
// keeps them (2026-11-05; DOFs by axis and direction, 2026-11-07). CAESAR stores a joint as CNODE restraints between two
// coincident nodes, one per DOF, and that is how one reaches Pluto from a .cii (and from the TOML builder, which writes a
// coupling the same way). Recognize turns each such group into a PipeModelJoint that the workbook's Joints sheet shows.
// The restraints stay where they are in the model's list, each marked with the joint's uid and DOF (the joint is
// Backed), so the .cii is written from them exactly as before, and everything that reads restraints (the viewer, the
// review) still sees them. Sync applies a joint's edits to its restraints: values in place; a DOF added becomes a new
// restraint after the joint's others; a DOF removed takes its restraint away; another frame replaces them all. A joint's
// user SIF is its node's SIF entry, marked the same way.
//
// A DOF is one CAESAR restraint: X Y Z RX RY RZ, + or - for one direction (CAESAR's own: +Y acts in +Y), with a gap
// and a stiffness (none: rigid), in the joint's frame: global (CAESAR's X Y Z) or pipe (X along the pipe at the joint,
// Y and Z square to it, CaesarNeutralWriter.Perps). The older pipe-frame names read as theirs: axial = X, shear = Y and
// Z, torsion = RX, bending = RY and RZ.
//
// A group is a joint when its two nodes are both on elements and at one point, it has three or more restraints, all
// single-DOF types (X ... RZ, +X ... -RZ, skewed by cosines or not) with no friction, no two along one direction but a
// + and a - pair, one tag, and they fit a frame: the global one (each along a global axis), else the pipe's (each along
// the pipe or one of its two square axes; two both-ways restraints square to the pipe and to each other with one gap
// and stiffness count as its Y and Z whichever way they point). Anything else stays a plain restraint (a strut, a guide
// between two lines, a one-DOF tie). C# 5 (prefix Pipe).

public static class PipeJoints
{
    const double Tol = 1e-6;
    static readonly string[] Axes = { "X", "Y", "Z", "RX", "RY", "RZ" };

    sealed class Item { public PipeModelRestraint R; public bool Rot; public int Sign; public double[] Dir; public string Name; }

    // ---------------- DOF names

    // [+|-] X Y Z RX RY RZ -> the sign (0: both ways), rotation or not, the axis 0..2; false when it names none
    public static bool ParseDof(string name, out int sign, out bool rot, out int axis)
    {
        sign = 0; rot = false; axis = -1;
        string t = (name ?? "").Trim().ToUpperInvariant();
        if (t.StartsWith("+", StringComparison.Ordinal)) { sign = 1; t = t.Substring(1); }
        else if (t.StartsWith("-", StringComparison.Ordinal)) { sign = -1; t = t.Substring(1); }
        if (t.Length == 2 && t[0] == 'R') { rot = true; t = t.Substring(1); }
        if (t.Length != 1) return false;
        axis = "XYZ".IndexOf(t[0]);
        return axis >= 0;
    }
    public static string DofName(int sign, bool rot, int axis) { return (sign > 0 ? "+" : sign < 0 ? "-" : "") + (rot ? "R" : "") + "XYZ"[axis]; }

    // the older pipe-frame names as today's: axial = X, shear = Y and Z, torsion = RX, bending = RY and RZ
    public static bool IsLegacy(string dof) { string n = (dof ?? "").Trim().ToLowerInvariant(); return n == "axial" || n == "shear" || n == "torsion" || n == "bending"; }
    public static List<PipeModelJointDof> Expand(PipeModelJointDof d)
    {
        string n = (d.Dof ?? "").Trim().ToLowerInvariant();
        string[] to = n == "axial" ? new[] { "X" } : n == "shear" ? new[] { "Y", "Z" } : n == "torsion" ? new[] { "RX" } : n == "bending" ? new[] { "RY", "RZ" } : null;
        if (to == null) return new List<PipeModelJointDof> { new PipeModelJointDof { Dof = (d.Dof ?? "").Trim().ToUpperInvariant(), Gap = d.Gap, Stiffness = d.Stiffness } };
        return to.Select(x => new PipeModelJointDof { Dof = x, Gap = d.Gap, Stiffness = d.Stiffness }).ToList();
    }

    static bool Global(PipeModelJoint j) { return string.Equals(j.Frame, "global", StringComparison.OrdinalIgnoreCase); }

    // a DOF axis's unit vector in the joint's frame
    public static double[] AxisOf(PipeModel m, PipeModelJoint j, int axis, string w)
    {
        if (Global(j)) { var v = new double[3]; v[axis] = 1; return v; }
        var a = CaesarNeutralWriter.JointAxis(m, j, w);
        if (axis == 0) return a;
        double[] p1, p2;
        CaesarNeutralWriter.Perps(a, out p1, out p2);
        return axis == 1 ? p1 : p2;
    }

    // one DOF as its CNODE restraint: along a global axis by its type ("-Y" when the frame's +X points down Y), else
    // skewed by cosines (a one-way one as "+X" along the way it acts)
    public static PipeModelRestraint RestraintFor(PipeModelJoint j, double[] dir, int sign, bool rot, double gap, double stiffness, string w)
    {
        int axis = -1;
        for (int q = 0; q < 3; q++) if (Math.Abs(Math.Abs(dir[q]) - 1) < 1e-9) axis = q;
        string type; double[] cos = null;
        if (axis >= 0)
        {
            int s = sign == 0 ? 0 : sign * Math.Sign(dir[axis]);
            type = (s > 0 ? "+" : s < 0 ? "-" : "") + (rot ? "R" : "") + "XYZ"[axis];
        }
        else
        {
            type = (sign == 0 ? "" : "+") + (rot ? "R" : "") + "X";
            cos = sign < 0 ? new[] { -dir[0], -dir[1], -dir[2] } : new[] { dir[0], dir[1], dir[2] };
        }
        var r = new PipeModelRestraint { Node = j.Node, CNode = j.CNode, Type = type, Code = PipeModelCodes.Restraint[type], Gap = gap, Stiffness = stiffness, Tag = j.Tag ?? "", Source = w };
        if (cos != null) r.Cosines = cos;
        return r;
    }

    // a restraint's sign (0 both ways) and the unit vector of its axis
    static void Read(PipeModelRestraint r, out int sign, out bool rot, out double[] dir)
    {
        string t = (r.Type ?? "").Trim().ToUpperInvariant();
        sign = t.StartsWith("+", StringComparison.Ordinal) ? 1 : t.StartsWith("-", StringComparison.Ordinal) ? -1 : 0;
        rot = t.TrimStart('+', '-').StartsWith("R", StringComparison.Ordinal);
        var c = r.Cosines ?? PipeModelCodes.DefaultCosines(t);
        double l = Math.Sqrt(c[0] * c[0] + c[1] * c[1] + c[2] * c[2]);
        dir = l > 1e-9 ? new[] { c[0] / l, c[1] / l, c[2] / l } : null;
    }

    // ---------------- recognizing joints in a model

    // turn the model's joint-shaped CNODE restraint groups into backed joints; returns how many
    public static int Recognize(PipeModel m)
    {
        var onElement = new HashSet<int>();
        foreach (var e in m.Elements) { onElement.Add(e.From); onElement.Add(e.To); }
        var groups = new List<List<PipeModelRestraint>>();
        var byPair = new Dictionary<long, List<PipeModelRestraint>>();
        foreach (var r in m.Restraints)
        {
            if (r.Joint > 0 || r.CNode <= 0 || r.Node <= 0 || r.CNode == r.Node) continue;
            long key = (long)r.Node * 100000000L + r.CNode;
            List<PipeModelRestraint> g;
            if (!byPair.TryGetValue(key, out g)) { byPair[key] = g = new List<PipeModelRestraint>(); groups.Add(g); }
            g.Add(r);
        }
        int next = m.Joints.Count == 0 ? 1 : m.Joints.Max(j => j.Uid) + 1, made = 0;
        foreach (var g in groups)
        {
            List<Item> items;
            var j = Shape(m, g, onElement, out items);
            if (j == null) continue;
            j.Uid = next++;
            foreach (var it in items) { it.R.Joint = j.Uid; it.R.JointDof = it.Name; }
            // its user SIF: the node's one SIF entry when it holds nothing but a user SIF
            var sifs = m.Sifs.Where(s => s.Node == j.Node && s.Joint == 0).ToList();
            if (sifs.Count == 1 && UserOnly(sifs[0])) { j.Sif = sifs[0].SifIn; sifs[0].Joint = j.Uid; }
            m.Joints.Add(j);
            made++;
        }
        return made;
    }

    static bool UserOnly(PipeModelSif s)
    {
        return s.Type == 0 && s.SifIn > 0 && (s.SifOut == s.SifIn) && s.SurfaceNode == 0 && s.Pad == 0 && s.FittingRo == 0 && s.CrotchRadius == 0 && s.WeldMismatch == 0 && s.Item6 == 0;
    }

    static double[] Act(Item it) { return it.Sign == 0 ? it.Dir : new[] { it.Dir[0] * it.Sign, it.Dir[1] * it.Sign, it.Dir[2] * it.Sign }; }

    static PipeModelJoint Shape(PipeModel m, List<PipeModelRestraint> g, HashSet<int> onElement, out List<Item> items)
    {
        items = null;
        int node = g[0].Node, cnode = g[0].CNode;
        if (g.Count < 3 || !onElement.Contains(node) || !onElement.Contains(cnode)) return null;
        double[] pn, pc;
        if (!m.Positions.TryGetValue(node, out pn) || !m.Positions.TryGetValue(cnode, out pc)) return null;
        double scale = Math.Max(1, pn.Select(v => Math.Abs(v)).Max());
        if (Math.Sqrt((pn[0] - pc[0]) * (pn[0] - pc[0]) + (pn[1] - pc[1]) * (pn[1] - pc[1]) + (pn[2] - pc[2]) * (pn[2] - pc[2])) > 1e-6 * scale) return null;
        string tag = (g[0].Tag ?? "").Trim();
        var list = new List<Item>();
        foreach (var r in g)
        {
            string t = (r.Type ?? "").Trim().ToUpperInvariant();
            if (r.Mu != 0 || (r.Tag ?? "").Trim() != tag || Array.IndexOf(Axes, t.TrimStart('+', '-')) < 0 || t.TrimStart('+', '-').Length + 1 < t.Length) return null;
            int sign; bool rot; double[] dir;
            Read(r, out sign, out rot, out dir);
            if (dir == null) return null;
            list.Add(new Item { R = r, Rot = rot, Sign = sign, Dir = dir });
        }
        // no two along one direction, but a + and a - one acting opposite ways
        for (int a = 0; a < list.Count; a++)
            for (int b = a + 1; b < list.Count; b++)
                if (list[a].Rot == list[b].Rot && Math.Abs(Math.Abs(Dot(list[a].Dir, list[b].Dir)) - 1) < Tol)
                    if (list[a].Sign == 0 || list[b].Sign == 0 || Dot(Act(list[a]), Act(list[b])) > 0) return null;
        var j = new PipeModelJoint { Node = node, CNode = cnode, Tag = tag, Backed = true };
        if (GlobalFrame(list)) j.Frame = "global";
        else if (PipeFrame(m, j, list)) j.Frame = "pipe";
        else return null;
        if (list.Select(x => x.Name).Distinct().Count() != list.Count) return null;
        // CAESAR's order: X Y Z RX RY RZ, each both ways, then +, then -
        foreach (var it in list.OrderBy(x => Order(x.Name)))
            j.Dofs.Add(new PipeModelJointDof { Dof = it.Name, Gap = it.R.Gap, Stiffness = Rigid(m, it.R) ? 0 : it.R.Stiffness });
        j.Kind = Kind(j.Frame, j.Dofs.Select(d => d.Dof).ToList());
        items = list;
        return j;
    }

    static int Order(string name)
    {
        int s; bool rot; int ax;
        ParseDof(name, out s, out rot, out ax);
        return ((rot ? 3 : 0) + ax) * 3 + (s == 0 ? 0 : s > 0 ? 1 : 2);
    }

    static bool GlobalFrame(List<Item> list)
    {
        foreach (var it in list)
        {
            int k = -1;
            for (int q = 0; q < 3; q++) if (Math.Abs(Math.Abs(it.Dir[q]) - 1) < Tol) k = q;
            if (k < 0) return false;
            it.Name = DofName(it.Sign == 0 ? 0 : it.Sign * Math.Sign(it.Dir[k]), it.Rot, k);
        }
        return true;
    }

    static bool PipeFrame(PipeModel m, PipeModelJoint j, List<Item> list)
    {
        double[] a;
        try { a = CaesarNeutralWriter.JointAxis(m, j, "joint"); }
        catch (InvalidOperationException) { return false; }
        double[] p1, p2;
        CaesarNeutralWriter.Perps(a, out p1, out p2);
        var axes = new[] { a, p1, p2 };
        var loose = new List<Item>();
        foreach (var it in list)
        {
            it.Name = null;
            for (int k = 0; k < 3; k++)
            {
                double d = Dot(it.Dir, axes[k]);
                if (Math.Abs(Math.Abs(d) - 1) < Tol) { it.Name = DofName(it.Sign == 0 ? 0 : it.Sign * Math.Sign(d), it.Rot, k); break; }
            }
            if (it.Name != null) continue;
            if (Math.Abs(Dot(it.Dir, a)) > Tol) return false;
            loose.Add(it);
        }
        // square to the pipe but not along its Y or Z: a both-ways pair, square to each other, alike
        foreach (bool rot in new[] { false, true })
        {
            var g = loose.Where(x => x.Rot == rot).ToList();
            if (g.Count == 0) continue;
            if (g.Count != 2 || g.Any(x => x.Sign != 0) || Math.Abs(Dot(g[0].Dir, g[1].Dir)) > Tol || g[0].R.Gap != g[1].R.Gap || g[0].R.Stiffness != g[1].R.Stiffness) return false;
            if (list.Any(x => x.Rot == rot && x.Name != null && x.Name.TrimStart('+', '-') != (rot ? "RX" : "X"))) return false;
            g[0].Name = rot ? "RY" : "Y"; g[1].Name = rot ? "RZ" : "Z";
        }
        return true;
    }

    // a label for what the joint frees (every DOF both ways): coupling (all held), ball (rotations free), slip (pipe
    // frame: X free), hinge (global frame: one rotation free), else custom
    static string Kind(string frame, List<string> names)
    {
        if (names.Any(q => q.StartsWith("+", StringComparison.Ordinal) || q.StartsWith("-", StringComparison.Ordinal))) return "custom";
        var n = new HashSet<string>(names);
        if (n.Count == 6) return "coupling";
        if (n.SetEquals(new[] { "X", "Y", "Z" })) return "ball";
        if (frame == "pipe" && n.SetEquals(new[] { "Y", "Z", "RX", "RY", "RZ" })) return "slip";
        if (frame != "pipe" && n.Count == 5 && n.Contains("X") && n.Contains("Y") && n.Contains("Z")) return "hinge";
        return "custom";
    }

    static double Dot(double[] a, double[] b) { return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]; }

    // rigid: no stiffness, or CAESAR's 1e12 (in the job's units)
    public static bool Rigid(PipeModel m, PipeModelRestraint r)
    {
        double kf = m.Factor(PipeModelCodes.IsRotationCode(r.Code) ? 14 : 13);
        return r.Stiffness <= 0 || r.Stiffness >= 1e11 * kf;
    }

    // ---------------- a joint's edits onto its restraints

    // a backed joint's edits onto its restraints and SIF (the joint as the workbook read it). Restraints whose joint is
    // gone are removed by the caller (Prune).
    public static void Sync(PipeModel m, PipeModelJoint j)
    {
        string w = "joint " + j.Node + "-" + j.CNode + " (" + j.Kind + ")";
        var dofs = j.Dofs.SelectMany(Expand).ToList();
        j.Dofs.Clear(); j.Dofs.AddRange(dofs);
        var mine = m.Restraints.Where(r => r.Joint == j.Uid).ToList();
        Rename(m, j, mine, w);
        int at = mine.Count > 0 ? m.Restraints.IndexOf(mine[mine.Count - 1]) + 1 : m.Restraints.Count;
        if (mine.Count > 0 && !Fits(m, j, mine, dofs, w))
        {
            // another frame, or a pair edited out of what its directions allow: every restraint anew, in the first's place
            at = m.Restraints.IndexOf(mine[0]);
            foreach (var r in mine) m.Restraints.Remove(r);
            mine.Clear();
        }
        var keep = new HashSet<PipeModelRestraint>();
        foreach (var d in dofs)
        {
            var have = mine.Where(r => string.Equals(r.JointDof, d.Dof, StringComparison.OrdinalIgnoreCase)).ToList();
            if (have.Count == 0)
            {
                var fresh = CaesarNeutralWriter.JointDofRestraints(m, j, d);
                foreach (var r in fresh) { r.Joint = j.Uid; r.JointDof = d.Dof; r.Source = "workbook joint"; }
                at = Math.Min(at, m.Restraints.Count);
                m.Restraints.InsertRange(at, fresh);
                at += fresh.Count;
                foreach (var r in fresh) keep.Add(r);
                continue;
            }
            foreach (var r in have)
            {
                keep.Add(r);
                if (r.Node != j.Node) { r.Node = j.Node; r.OwnerUid = 0; r.Slot = -1; }
                if (r.CNode != j.CNode) r.CNode = j.CNode;
                if (r.Gap != d.Gap) r.Gap = d.Gap;
                bool rigidNow = Rigid(m, r);
                if (d.Stiffness == 0 ? !rigidNow : r.Stiffness != d.Stiffness) r.Stiffness = d.Stiffness;
                if ((j.Tag ?? "").Trim() != (r.Tag ?? "").Trim()) r.Tag = j.Tag ?? "";
            }
        }
        foreach (var r in mine) if (!keep.Contains(r)) m.Restraints.Remove(r);
        // its user SIF
        var sif = m.Sifs.FirstOrDefault(s => s.Joint == j.Uid);
        if (j.Sif > 0)
        {
            if (sif == null) m.Sifs.Add(new PipeModelSif { Node = j.Node, Type = 0, SifIn = j.Sif, SifOut = j.Sif, Joint = j.Uid });
            else
            {
                if (sif.Node != j.Node) { sif.Node = j.Node; sif.OwnerUid = 0; sif.Slot = -1; }
                if (sif.SifIn != j.Sif) sif.SifIn = j.Sif;
                if (sif.SifOut != j.Sif) sif.SifOut = j.Sif;
            }
        }
        else if (sif != null) m.Sifs.Remove(sif);
        j.Backed = true;
    }

    // restraints a workbook of an older version marked axial / shear / torsion / bending get today's names, by the way
    // each points (a shear or bending pair not along the frame's Y and Z: Y then Z)
    static void Rename(PipeModel m, PipeModelJoint j, List<PipeModelRestraint> mine, string w)
    {
        if (!mine.Any(r => IsLegacy(r.JointDof))) return;
        var a = CaesarNeutralWriter.JointAxis(m, j, w);
        double[] p1, p2;
        CaesarNeutralWriter.Perps(a, out p1, out p2);
        foreach (string pair in new[] { "shear", "bending" })
        {
            var g = mine.Where(r => string.Equals(r.JointDof, pair, StringComparison.OrdinalIgnoreCase)).ToList();
            string ry = pair == "shear" ? "Y" : "RY", rz = pair == "shear" ? "Z" : "RZ";
            var named = new List<PipeModelRestraint>();
            foreach (var r in g)
            {
                int s; bool rot; double[] dir;
                Read(r, out s, out rot, out dir);
                if (dir != null && Math.Abs(Math.Abs(Dot(dir, p1)) - 1) < Tol) { r.JointDof = ry; named.Add(r); }
                else if (dir != null && Math.Abs(Math.Abs(Dot(dir, p2)) - 1) < Tol) { r.JointDof = rz; named.Add(r); }
            }
            var rest = g.Except(named).ToList();
            for (int k = 0; k < rest.Count; k++) rest[k].JointDof = mine.Any(r => r.JointDof == ry) ? rz : ry;
        }
        foreach (var r in mine)
        {
            if (string.Equals(r.JointDof, "axial", StringComparison.OrdinalIgnoreCase)) r.JointDof = "X";
            else if (string.Equals(r.JointDof, "torsion", StringComparison.OrdinalIgnoreCase)) r.JointDof = "RX";
        }
    }

    // whether the joint's restraints point the ways its DOFs name, in its frame: each along its axis (one-way ones the
    // way they act); in the pipe frame a both-ways Y / Z (RY / RZ) pair square to the pipe and to each other, whichever
    // way, while the joint still gives the two one gap and stiffness
    static bool Fits(PipeModel m, PipeModelJoint j, List<PipeModelRestraint> mine, List<PipeModelJointDof> dofs, string w)
    {
        bool pipe = !Global(j);
        double[] a = pipe ? CaesarNeutralWriter.JointAxis(m, j, w) : null;
        foreach (var r in mine)
        {
            int ns, rs; bool nrot, rrot; int ax; double[] dir;
            if (!ParseDof(r.JointDof, out ns, out nrot, out ax)) return false;
            Read(r, out rs, out rrot, out dir);
            if (dir == null || rrot != nrot || (ns == 0) != (rs == 0)) return false;
            var want = AxisOf(m, j, ax, w);
            double d = Dot(dir, want);
            if (ns == 0 && Math.Abs(Math.Abs(d) - 1) < Tol) continue;
            if (ns != 0 && Math.Abs(Math.Abs(d) - 1) < Tol && Math.Sign(d) * rs == ns) continue;
            // the pipe frame's loose pair
            if (!pipe || ns != 0 || ax == 0 || Math.Abs(Dot(dir, a)) > Tol) return false;
            string ny = nrot ? "RY" : "Y", nz = nrot ? "RZ" : "Z";
            var dy = dofs.FirstOrDefault(x => x.Dof == ny); var dz = dofs.FirstOrDefault(x => x.Dof == nz);
            var ry = mine.FirstOrDefault(x => x.JointDof == ny); var rz = mine.FirstOrDefault(x => x.JointDof == nz);
            if (dy == null || dz == null || ry == null || rz == null || dy.Gap != dz.Gap || dy.Stiffness != dz.Stiffness) return false;
            int s1, s2; bool o1, o2; double[] v1, v2;
            Read(ry, out s1, out o1, out v1); Read(rz, out s2, out o2, out v2);
            if (v1 == null || v2 == null || Math.Abs(Dot(v1, v2)) > Tol) return false;
        }
        return true;
    }

    // the restraints and SIFs of joints the model no longer has go with them
    public static void Prune(PipeModel m)
    {
        var uids = new HashSet<int>(m.Joints.Where(j => j.Backed).Select(j => j.Uid));
        m.Restraints.RemoveAll(r => r.Joint > 0 && !uids.Contains(r.Joint));
        m.Sifs.RemoveAll(s => s.Joint > 0 && !uids.Contains(s.Joint));
    }
}
