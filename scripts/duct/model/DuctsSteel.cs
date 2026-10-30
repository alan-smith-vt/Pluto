using System;
using System.Collections.Generic;
using System.Linq;

// Steel in the clean duct model (2026-10-30; rules: vault handoff Pluto 2026-10-30 "Steel boundary conditions").
// Export-DuctsViewer.ps1 -Revit fills Steel from DuctSteel in duct-config.psd1 (-Steel overrides; SteelToPluto CSV,
// meters, the frame of the duct model). BuildClean turns it into a SteelModel (joints, frames, releases,
// restraints), writes <out>.steel_joints.csv / .steel_frames.csv and draws it in <out>_clean.bin:
//   frames as I sections (CP offsets, web orientation), one context group per section ("Steel: <section>", grey)
//   end conditions as viewer glyphs (sidecar look + anchors, approved 2026-10-30 #hvac-2 laptop:697), one glyph per
//   frame end at t = 0 / 1, legend section "Member ends": "End: open-pinned" (support-pinned) / "End: open-fixed" (support-fixed),
//   always shown; "End: hinged" (half) / "End: collinear-hinged" (diamond), shown zoomed in
//   "Flag: OPEN_NEAR": warn glyph on the joint, open ends within NearIn of other steel (a probably missed connection)
public partial class DuctsToPluto
{

    // end group (SteelModel.EndGroup) -> glyph, tier, legend label
    static readonly string[][] SteelEndLooks = {
        new[] { "open-pinned", "support-pinned", "exception", "Pinned (open end)" },
        new[] { "open-fixed", "support-fixed", "exception", "Fixed (open end)" },
        new[] { "hinged", "half", "detail", "Hinged (M2+M3 released)" },
        new[] { "collinear-hinged", "diamond", "detail", "Collinear splice, hinged" } };

    class SteelView
    {
        public SortedDictionary<string, List<uint>> BySection = new SortedDictionary<string, List<uint>>(StringComparer.Ordinal);
        public Dictionary<string, List<FeaturesSidecar.Anchor>> ByEnd = new Dictionary<string, List<FeaturesSidecar.Anchor>>(StringComparer.Ordinal);
        public Dictionary<string, List<uint>> ByEndBeams = new Dictionary<string, List<uint>>(StringComparer.Ordinal);
        public List<FeaturesSidecar.Anchor> OpenNear = new List<FeaturesSidecar.Anchor>();
        public List<uint> OpenNearNodes = new List<uint>();
    }

    // frames into the clean model's tables; joints in inches -> the clean model's feet
    static SteelView AddSteel(SteelModel sm, Dictionary<int, Node> nodes, ref int next, List<RawViewerWriter.SectionDef> sections,
        Dictionary<int, RawViewerWriter.BeamMember> beams, Dictionary<int, string> labels, ref int nb, double[] off, double scale)
    {
        var v = new SteelView();
        double perM = scale / 0.3048;   // file units per meter (scale = per foot)
        var jointNode = new Dictionary<int, int>();
        foreach (SteelModel.Joint j in sm.Joints) jointNode[j.Id] = MakeNode(nodes, ref next, new[] { j.X / 12, j.Y / 12, j.Z / 12 }, off, scale);
        var secIx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
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
            double oy, oz; SteelGeom.MemberOffsets(b, perM, out oy, out oz);
            var m = Beam(nb, jointNode[f.I], jointNode[f.J], s, nodes);
            m.LocalY = SteelGeom.ResolveLocalY(b, ref dummy); m.OffsetAy = oy; m.OffsetBy = oy; m.OffsetAz = oz; m.OffsetBz = oz;
            beams[nb] = m;
            double L = Math.Sqrt(DuctMath.Dist2(sm.JointPos(f.I), sm.JointPos(f.J)));
            labels[nb] = string.Format(Inv, "steel | {0} | {1} | {2:0.##} ft | {3} {4} {5} - {6} {7} {8}{9}", f.Name, name, L / 12,
                sm.Joints[f.I - 1].Name, f.EndI, f.ReleaseI, sm.Joints[f.J - 1].Name, f.EndJ, f.ReleaseJ,
                string.IsNullOrEmpty(b.MemberOid) ? "" : " | " + b.MemberOid);
            List<uint> l; if (!v.BySection.TryGetValue(name, out l)) { l = new List<uint>(); v.BySection[name] = l; } l.Add((uint)nb);

            // one end glyph per frame end with a condition: t = 0 at I, 1 at J
            for (int k = 0; k < 2; k++)
            {
                string g = SteelModel.EndGroup(k == 0 ? f.EndI : f.EndJ, k == 0 ? f.ReleaseI : f.ReleaseJ);
                if (g == null) continue;
                List<FeaturesSidecar.Anchor> a;
                if (!v.ByEnd.TryGetValue(g, out a)) { a = new List<FeaturesSidecar.Anchor>(); v.ByEnd[g] = a; v.ByEndBeams[g] = new List<uint>(); }
                a.Add(new FeaturesSidecar.Anchor { Domain = "beams", Id = (uint)nb, T = k, Label = f.Name + " at " + sm.Joints[(k == 0 ? f.I : f.J) - 1].Name });
                if (!v.ByEndBeams[g].Contains((uint)nb)) v.ByEndBeams[g].Add((uint)nb);
            }
            nb++;
        }
        foreach (SteelModel.Joint j in sm.Joints)
            if (j.Flags.Contains("OPEN_NEAR"))
            {
                v.OpenNearNodes.Add((uint)jointNode[j.Id]);
                v.OpenNear.Add(new FeaturesSidecar.Anchor { Domain = "nodes", Id = (uint)jointNode[j.Id], Label = j.Name + " " + j.Condition });
            }
        return v;
    }

    // after ApplySystemView: steel sections are context (grey, one legend row; UNSIZED stays a plain group so it
    // shows); ends and flags are glyph groups, which never paint their members
    static void AddSteelGroups(FeaturesSidecar sc, SteelView v)
    {
        if (v == null) return;
        foreach (KeyValuePair<string, List<uint>> kv in v.BySection)
        {
            bool unsized = kv.Key == "UNSIZED STEEL";
            sc.AddGroup("Steel: " + kv.Key, unsized ? "#ff7a3b" : "#8b919c", "beams", kv.Value,
                        unsized ? new[] { "steel", "section" } : new[] { "steel", "section", "context" }, "STEEL_" + SteelGeom.StaadName(kv.Key));
        }
        foreach (string[] e in SteelEndLooks)
        {
            List<FeaturesSidecar.Anchor> a;
            if (!v.ByEnd.TryGetValue(e[0], out a)) continue;
            FeaturesSidecar.Group g = sc.AddGroup("End: " + e[0], null, "beams", v.ByEndBeams[e[0]], new[] { "steel", "end" }, null);
            g.Look = new FeaturesSidecar.Look { Glyph = e[1], Tier = e[2], Label = e[3], Section = "Member ends" };
            g.Anchors.AddRange(a);
        }
        if (v.OpenNear.Count > 0)
        {
            FeaturesSidecar.Group g = sc.AddNodeGroup("Flag: OPEN_NEAR", null, v.OpenNearNodes, new[] { "steel", "flag" }, "FLAG_OPEN_NEAR");
            g.Look = new FeaturesSidecar.Look { Glyph = "warn", Tier = "exception", Label = "Open end near steel (missed connection?)", Section = "Checks" };
            g.Anchors.AddRange(v.OpenNear);
        }
    }
}
