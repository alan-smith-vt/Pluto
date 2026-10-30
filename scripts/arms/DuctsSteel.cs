using System;
using System.Collections.Generic;
using System.Linq;

// Steel in the clean duct model (2026-10-30; rules: vault handoff Pluto 2026-10-30 "Steel boundary conditions").
// Export-DuctsViewer.ps1 -Revit fills Steel from DuctSteel in duct-config.psd1 (-Steel overrides; SteelToPluto CSV,
// meters, the frame of the duct model). BuildClean turns it into a SteelModel (joints, frames, releases,
// restraints), writes <out>.steel_joints.csv / .steel_frames.csv and draws it in <out>_clean.bin:
//   frames as I sections (CP offsets, web orientation), one group per section ("Steel: <section>")
//   end markers: a collar (short tube around the section) near each end, one group per end condition:
//     "Steel end: open-pinned" / "open-fixed" / "hinged" (M2M3, framed or corner) / "collinear-hinged"
//   "Flag: OPEN_NEAR": node group, open ends within NearIn of other steel (a probably missed connection)
public partial class DuctsToPluto
{
    public static readonly List<SteelMember> Steel = new List<SteelMember>();
    public static SteelModel.Settings SteelSettings = new SteelModel.Settings();

    static readonly string[] SteelEndGroups = { "open-pinned", "open-fixed", "hinged", "collinear-hinged" };
    static readonly string[] SteelEndColors = { "#3fa7ff", "#ff3b3b", "#ffd23f", "#c06bff" };

    class SteelView
    {
        public SortedDictionary<string, List<uint>> BySection = new SortedDictionary<string, List<uint>>(StringComparer.Ordinal);
        public Dictionary<string, List<uint>> ByEnd = new Dictionary<string, List<uint>>(StringComparer.Ordinal);
        public List<uint> OpenNear = new List<uint>();
    }

    // frames + collars into the clean model's tables; joints in inches -> the clean model's feet
    static SteelView AddSteel(SteelModel sm, Dictionary<int, Node> nodes, ref int next, List<RawViewerWriter.SectionDef> sections,
        Dictionary<int, RawViewerWriter.BeamMember> beams, Dictionary<int, string> labels, ref int nb, double[] off, double scale)
    {
        var v = new SteelView();
        double perM = scale / 0.3048;   // file units per meter (scale = per foot)
        var jointNode = new Dictionary<int, int>();
        foreach (SteelModel.Joint j in sm.Joints) jointNode[j.Id] = MakeNode(nodes, ref next, new[] { j.X / 12, j.Y / 12, j.Z / 12 }, off, scale);
        var secIx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var collarIx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (SteelModel.Frame f in sm.Frames)
        {
            SteelMember b = sm.Members[f.Source];
            bool sized = SteelGeom.Sized(b);
            double d = sized ? b.D : SteelGeom.UD, bf = sized ? b.Bf : SteelGeom.UBf, tf = sized ? b.Tf : SteelGeom.UTf, tw = sized ? b.Tw : SteelGeom.UTw;
            string name = sized ? (string.IsNullOrEmpty(b.SectionName) ? "UNNAMED" : b.SectionName) : "UNSIZED STEEL";
            int s;
            if (!secIx.TryGetValue(name, out s))
            {
                sections.Add(RawViewerWriter.SectionDef.IShape(name, (float)(d * perM), (float)(bf * perM), (float)(tf * perM), (float)(bf * perM), (float)(tf * perM), (float)(tw * perM)));
                s = sections.Count - 1; secIx[name] = s;
            }
            int dummy = 0;
            double[] ly = SteelGeom.ResolveLocalY(b, ref dummy);
            double oy, oz; SteelGeom.MemberOffsets(b, perM, out oy, out oz);
            var m = Beam(nb, jointNode[f.I], jointNode[f.J], s, nodes);
            m.LocalY = ly; m.OffsetAy = oy; m.OffsetBy = oy; m.OffsetAz = oz; m.OffsetBz = oz;
            beams[nb] = m;
            double[] pi = sm.JointPos(f.I), pj = sm.JointPos(f.J);
            double L = Math.Sqrt(Dist2(pi, pj));
            labels[nb] = string.Format(Inv, "steel | {0} | {1} | {2:0.##} ft | {3} {4} {5} - {6} {7} {8}{9}", f.Name, name, L / 12,
                sm.Joints[f.I - 1].Name, f.EndI, f.ReleaseI, sm.Joints[f.J - 1].Name, f.EndJ, f.ReleaseJ,
                string.IsNullOrEmpty(b.MemberOid) ? "" : " | " + b.MemberOid);
            List<uint> l; if (!v.BySection.TryGetValue(name, out l)) { l = new List<uint>(); v.BySection[name] = l; } l.Add((uint)nb);
            nb++;

            // collars: 2 in long, from 3 in to 5 in in from the end (less on a short frame), around the section
            double cl = Math.Min(2, L / 6), c0 = Math.Min(3, L / 4);
            string cname = "end marker " + name;
            int cs;
            if (!collarIx.TryGetValue(cname, out cs))
            {
                double od = Math.Sqrt(d * d + bf * bf) * 1.1 * perM;
                sections.Add(RawViewerWriter.SectionDef.Pipe(cname, (float)od, (float)(od * 0.08)));
                cs = sections.Count - 1; collarIx[cname] = cs;
            }
            for (int k = 0; k < 2; k++)
            {
                string g = SteelModel.EndGroup(k == 0 ? f.EndI : f.EndJ, k == 0 ? f.ReleaseI : f.ReleaseJ);
                if (g == null) continue;
                double[] a = k == 0 ? pi : pj, z = k == 0 ? pj : pi;
                double[] u = { (z[0] - a[0]) / L, (z[1] - a[1]) / L, (z[2] - a[2]) / L };
                int n0 = MakeNode(nodes, ref next, new[] { (a[0] + u[0] * c0) / 12, (a[1] + u[1] * c0) / 12, (a[2] + u[2] * c0) / 12 }, off, scale);
                int n1 = MakeNode(nodes, ref next, new[] { (a[0] + u[0] * (c0 + cl)) / 12, (a[1] + u[1] * (c0 + cl)) / 12, (a[2] + u[2] * (c0 + cl)) / 12 }, off, scale);
                var c = Beam(nb, n0, n1, cs, nodes);
                c.LocalY = ly; c.OffsetAy = oy; c.OffsetBy = oy; c.OffsetAz = oz; c.OffsetBz = oz;
                beams[nb] = c;
                labels[nb] = string.Format(Inv, "steel end | {0} | {1} at {2}", g, f.Name, sm.Joints[(k == 0 ? f.I : f.J) - 1].Name);
                if (!v.ByEnd.TryGetValue(g, out l)) { l = new List<uint>(); v.ByEnd[g] = l; } l.Add((uint)nb);
                nb++;
            }
        }
        foreach (SteelModel.Joint j in sm.Joints) if (j.Flags.Contains("OPEN_NEAR")) v.OpenNear.Add((uint)jointNode[j.Id]);
        return v;
    }

    // after ApplySystemView: steel stays painted (sections white, end markers coloured)
    static void AddSteelGroups(FeaturesSidecar sc, SteelView v)
    {
        if (v == null) return;
        foreach (KeyValuePair<string, List<uint>> kv in v.BySection)
            sc.AddGroup("Steel: " + kv.Key, kv.Key == "UNSIZED STEEL" ? "#ff7a3b" : "#f2f2f5", "beams", kv.Value, new[] { "steel", "section" }, "STEEL_" + SteelGeom.StaadName(kv.Key));
        for (int i = 0; i < SteelEndGroups.Length; i++)
        {
            List<uint> l;
            if (v.ByEnd.TryGetValue(SteelEndGroups[i], out l))
                sc.AddGroup("Steel end: " + SteelEndGroups[i], SteelEndColors[i], "beams", l, new[] { "steel", "end" }, null);
        }
        if (v.OpenNear.Count > 0) sc.AddNodeGroup("Flag: OPEN_NEAR", "#ff5fa2", v.OpenNear, new[] { "steel", "flag" }, "FLAG_OPEN_NEAR");
    }
}
