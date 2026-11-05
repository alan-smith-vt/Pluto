using System;
using System.Collections.Generic;
using System.Linq;

// PipeJoints  --  joints (couplings, hinges, slip and ball joints) as the pipe model keeps them (2026-11-05). CAESAR
// stores a joint as CNODE restraints between two coincident nodes, one per DOF, and that is how one reaches Pluto from
// a .cii (and from the TOML builder, which writes a coupling the same way). Recognize turns each such group into a
// PipeModelJoint that the workbook's Joints sheet shows. The restraints stay where they are in the model's list, each
// marked with the joint's uid and DOF (the joint is Backed), so the .cii is written from them exactly as before, and
// everything that reads restraints (the viewer, the review) still sees them. Sync applies a joint's edits to its
// restraints: values in place; a DOF added becomes new restraints after the joint's others; a DOF removed takes its
// restraints away; another frame replaces them all. A joint's user SIF is its node's SIF entry, marked the same way.
//
// A group is a joint when its two nodes are both on elements and at one point, it has three or more restraints, all
// single-DOF types (X Y Z RX RY RZ, skewed by cosines or not) with no friction, no two along one direction, one tag, and
// they fit a frame: the pipe's (axial and torsion along the pipe, shear and bending each as two square to it with one
// gap and stiffness) or the global one (each along a global axis). Anything else stays a plain restraint (a strut, a
// guide between two lines, a one-DOF tie). C# 5 (prefix Pipe).

public static class PipeJoints
{
    const double Tol = 1e-6;
    static readonly string[] PipeOrder = { "axial", "shear", "torsion", "bending" }, GlobalOrder = { "X", "Y", "Z", "RX", "RY", "RZ" };

    sealed class Item { public PipeModelRestraint R; public bool Rot; public double[] Dir; public string Name; }

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
            if (r.Mu != 0 || (r.Tag ?? "").Trim() != tag || Array.IndexOf(GlobalOrder, t) < 0) return null;
            var c = r.Cosines ?? PipeModelCodes.DefaultCosines(t);
            double l = Math.Sqrt(c[0] * c[0] + c[1] * c[1] + c[2] * c[2]);
            if (!(l > 1e-9)) return null;
            list.Add(new Item { R = r, Rot = t[0] == 'R', Dir = new[] { c[0] / l, c[1] / l, c[2] / l } });
        }
        for (int a = 0; a < list.Count; a++)
            for (int b = a + 1; b < list.Count; b++)
                if (list[a].Rot == list[b].Rot && Math.Abs(Math.Abs(Dot(list[a].Dir, list[b].Dir)) - 1) < Tol) return null;
        var j = new PipeModelJoint { Node = node, CNode = cnode, Tag = tag, Backed = true };
        string[] order;
        if (PipeFrame(m, j, list)) { j.Frame = "pipe"; order = PipeOrder; }
        else if (GlobalFrame(list)) { j.Frame = "global"; order = GlobalOrder; }
        else return null;
        foreach (string n in order)
        {
            var it = list.FirstOrDefault(x => x.Name == n);
            if (it != null) j.Dofs.Add(new PipeModelJointDof { Dof = n, Gap = it.R.Gap, Stiffness = Rigid(m, it.R) ? 0 : it.R.Stiffness });
        }
        var names = new HashSet<string>(list.Select(x => x.Name));
        j.Kind = Kind(j.Frame, names);
        items = list;
        return j;
    }

    static bool PipeFrame(PipeModel m, PipeModelJoint j, List<Item> list)
    {
        double[] a;
        try { a = CaesarNeutralWriter.JointAxis(m, j, "joint"); }
        catch (InvalidOperationException) { return false; }
        foreach (var it in list)
        {
            double d = Math.Abs(Dot(it.Dir, a));
            if (Math.Abs(d - 1) < Tol) it.Name = it.Rot ? "torsion" : "axial";
            else if (d < Tol) it.Name = it.Rot ? "bending" : "shear";
            else return false;
        }
        foreach (string n in PipeOrder)
        {
            var g = list.Where(x => x.Name == n).ToList();
            bool pair = n == "shear" || n == "bending";
            if (g.Count == 0) continue;
            if (pair ? g.Count != 2 : g.Count != 1) return false;
            if (pair && (Math.Abs(Dot(g[0].Dir, g[1].Dir)) > Tol || g[0].R.Gap != g[1].R.Gap || g[0].R.Stiffness != g[1].R.Stiffness)) return false;
        }
        return true;
    }

    static bool GlobalFrame(List<Item> list)
    {
        foreach (var it in list)
        {
            int k = -1;
            for (int q = 0; q < 3; q++) if (Math.Abs(Math.Abs(it.Dir[q]) - 1) < Tol) k = q;
            if (k < 0) return false;
            it.Name = (it.Rot ? "R" : "") + "XYZ"[k];
        }
        return true;
    }

    // a label for what the joint frees: coupling (every DOF held, maybe with gaps and stiffness), ball (rotations free),
    // slip (pipe frame: axial free), hinge (global frame: one rotation free), else custom
    static string Kind(string frame, HashSet<string> n)
    {
        if (frame == "pipe")
        {
            if (n.Count == 4) return "coupling";
            if (n.SetEquals(new[] { "axial", "shear" })) return "ball";
            if (n.SetEquals(new[] { "shear", "torsion", "bending" })) return "slip";
            return "custom";
        }
        if (n.Count == 6) return "coupling";
        if (n.SetEquals(new[] { "X", "Y", "Z" })) return "ball";
        if (n.Count == 5 && n.Contains("X") && n.Contains("Y") && n.Contains("Z")) return "hinge";
        return "custom";
    }

    static double Dot(double[] a, double[] b) { return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]; }

    // rigid: no stiffness, or CAESAR's 1e12 (in the job's units)
    public static bool Rigid(PipeModel m, PipeModelRestraint r)
    {
        double kf = m.Factor(PipeModelCodes.IsRotationCode(r.Code) ? 14 : 13);
        return r.Stiffness <= 0 || r.Stiffness >= 1e11 * kf;
    }

    // a backed joint's edits onto its restraints and SIF (the joint as the workbook read it). Restraints whose joint is
    // gone are removed by the caller (Prune).
    public static void Sync(PipeModel m, PipeModelJoint j)
    {
        var mine = m.Restraints.Where(r => r.Joint == j.Uid).ToList();
        bool frameSame = mine.Count == 0 || mine.All(r => j.Dofs.Count == 0 || IsName(j.Frame, r.JointDof));
        int at = mine.Count > 0 ? m.Restraints.IndexOf(mine[mine.Count - 1]) + 1 : m.Restraints.Count;
        if (!frameSame)
        {
            at = m.Restraints.IndexOf(mine[0]);
            foreach (var r in mine) m.Restraints.Remove(r);
            mine.Clear();
        }
        var keep = new HashSet<PipeModelRestraint>();
        foreach (var d in j.Dofs)
        {
            string name = Canon(j.Frame, d.Dof);
            var have = mine.Where(r => string.Equals(r.JointDof, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (have.Count == 0)
            {
                var fresh = CaesarNeutralWriter.JointDofRestraints(m, j, d);
                foreach (var r in fresh) { r.Joint = j.Uid; r.JointDof = name; r.Source = "workbook joint"; }
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

    // the restraints and SIFs of joints the model no longer has go with them
    public static void Prune(PipeModel m)
    {
        var uids = new HashSet<int>(m.Joints.Where(j => j.Backed).Select(j => j.Uid));
        m.Restraints.RemoveAll(r => r.Joint > 0 && !uids.Contains(r.Joint));
        m.Sifs.RemoveAll(s => s.Joint > 0 && !uids.Contains(s.Joint));
    }

    static bool IsName(string frame, string dof)
    {
        return string.Equals(frame, "global", StringComparison.OrdinalIgnoreCase) ? GlobalOrder.Contains((dof ?? "").ToUpperInvariant()) : PipeOrder.Contains((dof ?? "").ToLowerInvariant());
    }
    static string Canon(string frame, string dof) { return string.Equals(frame, "global", StringComparison.OrdinalIgnoreCase) ? (dof ?? "").Trim().ToUpperInvariant() : (dof ?? "").Trim().ToLowerInvariant(); }
}
