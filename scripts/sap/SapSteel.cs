// HVAC duct model, part 2: the utility rack steel and the duct-to-steel supports (vault handoff Pluto 2026-10-30).
// C# 5, Add-Type under PS 5.1. Called from SapDucts.Build when SteelFramesCsv / SupportsCsv / FlagsCsv / PenetrationsCsv are set.
// Inputs (inches, the frames.csv frame; vault/arms/sap-controller.md):
//   steel joints  <out>.steel_joints.csv (SteelModel.cs): Joint, X_in, Y_in, Z_in, Restraint (PINNED = U1-3,
//                 FIXED = all 6, empty), Condition (open-pinned | open-fixed | framed | corner | collinear | empty), Flags
//   steel frames  <out>.steel_frames.csv: Frame, JointI, JointJ, XI_in..ZJ_in, Section, D_in, Bf_in, Tf_in, Tw_in,
//                 WebX..WebZ (local 2), CP (SAP cardinal point; empty / 5 = centred), ReleaseI / ReleaseJ (M2M3 or
//                 empty), MemberOid, Flags, OffIX_in..OffJZ_in (joint -> the member's real end, global: SAP joint
//                 offsets; the steel is never moved, group STEEL_JOINT_OFFSET)
//   supports      <base>.supports.csv (duct supports): SupportId, Type, DuctJoint, DX..DZ, SteelJoint, SX..SZ,
//                 SteelMember, Gap_in, System, Flags
//   flags         Frame, Flag: extra FLAG_ groups on duct or steel frames (e.g. UNSUPPORTED_SPAN)
// Joint names are shared by all files: ducts N<n>, steel S<n>, support joints on steel SS<n>. A support joint not in
// the steel files is made at SX..SZ and its SteelMember auto-meshed at intermediate joints so it is connected.
using System;
using System.Collections.Generic;
using System.Linq;
using SAP2000v1;

public partial class SapDucts
{
    public const string SteelMat = "STEEL_A992", SteelNoSize = "STEEL_NOSIZE", PlantSys = "PLANT";
    public string SteelFramesCsv, SteelJointsCsv, SupportsCsv, FlagsCsv, PenetrationsCsv;
    // plant axes: a coordinate system turned about global Z (2026-10-22: the old model is turned 90 deg); NaN = none (default, 2026-10-30 user: opt-in)
    public double PlantRotZ = double.NaN;

    // point groups, filled here, assigned with the frame groups in Build
    readonly Dictionary<string, List<string>> pointGroups = new Dictionary<string, List<string>>(StringComparer.Ordinal);

    string AddSteelAndSupports(Dictionary<string, string> joints, HashSet<string> sections, Action<string, string> addTo, Dictionary<string, string> frameName)
    {
        var notes = new List<string>();
        if (!double.IsNaN(PlantRotZ))
        {
            Check(M.CoordSys.SetCoordSys(PlantSys, 0, 0, 0, PlantRotZ, 0, 0), "SetCoordSys " + PlantSys);
            notes.Add(F("NEW plant axes: coordinate system {0}, global turned {1:0.###} deg about Z", PlantSys, PlantRotZ));
        }
        var steelFrames = new Dictionary<string, string>(StringComparer.Ordinal);   // steel csv Frame -> SAP frame
        if (!string.IsNullOrEmpty(SteelFramesCsv)) notes.Add(AddSteel(joints, sections, addTo, steelFrames));
        if (!string.IsNullOrEmpty(SupportsCsv)) notes.Add(AddSupports(ReadCsv(SupportsCsv), joints, sections, addTo, steelFrames));
        if (!string.IsNullOrEmpty(FlagsCsv))
        {
            int n = 0; var missing = new List<string>();
            foreach (Dictionary<string, string> r in ReadCsv(FlagsCsv))
            {
                string f, fr = Val(r, "Frame");
                if (frameName.TryGetValue(fr, out f) || steelFrames.TryGetValue(fr, out f)) { addTo("FLAG_" + Clean(Val(r, "Flag")), f); n++; }
                else missing.Add(fr);
            }
            notes.Add(F("NEW flags csv: {0} assigned{1}", n, missing.Count > 0 ? F(", {0} frames not in the model ({1})", missing.Count, string.Join(" ", missing.Take(10).ToArray())) : ""));
        }
        // penetrations (duct\model\DuctPenetrations.cs): the joints where a duct passes through a wall or slab, as point
        // groups PEN_WALL / PEN_SLAB. No restraint is set. A joint whose frames were left out of the model is skipped.
        if (!string.IsNullOrEmpty(PenetrationsCsv))
        {
            int n = 0, missing = 0; var done = new HashSet<string>(StringComparer.Ordinal);
            foreach (Dictionary<string, string> r in ReadCsv(PenetrationsCsv))
            {
                string pt, g = "PEN_" + Clean(Val(r, "Kind")).ToUpperInvariant();
                if (!joints.TryGetValue(Val(r, "Joint"), out pt)) { missing++; continue; }
                if (!done.Add(g + "|" + pt)) continue;
                AddPoint(g, pt); n++;
            }
            notes.Add(F("NEW penetrations: {0} joints in PEN_ groups{1}", n, missing > 0 ? F(", {0} rows on joints not in the model", missing) : ""));
        }
        return string.Join("\n", notes.ToArray());
    }

    string AddSteel(Dictionary<string, string> joints, HashSet<string> sections, Action<string, string> addTo, Dictionary<string, string> steelFrames)
    {
        // A992: E 29000 ksi, 0.2836 lb/in3
        Check(M.PropMaterial.SetMaterial(SteelMat, eMatType.Steel, -1, "", ""), "SetMaterial " + SteelMat);
        Check(M.PropMaterial.SetMPIsotropic(SteelMat, 29000, 0.3, 6.5e-6, 0), "SetMPIsotropic " + SteelMat);
        Check(M.PropMaterial.SetWeightAndMass(SteelMat, 1, 2.836e-4, 0), "SetWeightAndMass " + SteelMat);
        int pinned = 0, fixd = 0, pinnedT = 0;
        string jointsCsv = string.IsNullOrEmpty(SteelJointsCsv) ? SteelFramesCsv.Replace("steel_frames", "steel_joints") : SteelJointsCsv;
        foreach (Dictionary<string, string> r in ReadCsv(jointsCsv))
        {
            string pt = Joint(joints, r["Joint"], Num(r, "X_in"), Num(r, "Y_in"), Num(r, "Z_in"));
            string kind = Val(r, "Restraint").Trim().ToUpperInvariant();
            if (kind == "PINNED" || kind == "FIXED")
            {
                bool fx = kind == "FIXED";
                bool[] dof = { true, true, true, fx, fx, fx };
                Check(M.PointObj.SetRestraint(pt, ref dof), "SetRestraint " + pt);
                if (fx) fixd++; else pinned++;
            }
            else if (kind == "PINNED+T") { PinTorsion(pt, r); pinnedT++; }
            else if (kind != "") throw new Exception("Restraint '" + kind + "' at " + r["Joint"] + ": PINNED, PINNED+T, FIXED or empty");
            string cond = Val(r, "Condition");
            if (cond != "") AddPoint("STEEL_" + Clean(cond), pt);
            foreach (string f in Val(r, "Flags").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)) AddPoint("FLAG_" + Clean(f), pt);
        }
        int released = 0, ends = 0, noSize = 0, noWeb = 0, offsetFrames = 0; var failed = new List<string>();
        foreach (Dictionary<string, string> r in ReadCsv(SteelFramesCsv))
        {
            string ji, jj;
            if (!joints.TryGetValue(r["JointI"], out ji) || !joints.TryGetValue(r["JointJ"], out jj)) { failed.Add(r["Frame"] + " (joint not in steel_joints)"); continue; }
            string sec = SteelSection(sections, r), name = "";
            if (ji == jj || M.FrameObj.AddByPoint(ji, jj, ref name, sec, r["Frame"]) != 0) { failed.Add(r["Frame"]); continue; }
            steelFrames[r["Frame"]] = name;
            // web = local 2, so local 3 = local 1 x web
            double xi = Num(r, "XI_in"), yi = Num(r, "YI_in"), zi = Num(r, "ZI_in"), xj = Num(r, "XJ_in"), yj = Num(r, "YJ_in"), zj = Num(r, "ZJ_in");
            // joint offsets (steel model 2026-11-12: members are not moved; joint -> the member's real end, global inches).
            // The member's axis and its web angle are those of the real line.
            double[] o1 = { Off(r, "OffIX_in"), Off(r, "OffIY_in"), Off(r, "OffIZ_in") }, o2 = { Off(r, "OffJX_in"), Off(r, "OffJY_in"), Off(r, "OffJZ_in") };
            bool hasOff = Math.Abs(o1[0]) + Math.Abs(o1[1]) + Math.Abs(o1[2]) + Math.Abs(o2[0]) + Math.Abs(o2[1]) + Math.Abs(o2[2]) > 1e-6;
            xi += o1[0]; yi += o1[1]; zi += o1[2]; xj += o2[0]; yj += o2[1]; zj += o2[2];
            double wx = Num(r, "WebX"), wy = Num(r, "WebY"), wz = Num(r, "WebZ"), ex = xj - xi, ey = yj - yi, ez = zj - zi, ang;
            if (LocalAngle(xi, yi, zi, xj, yj, zj, ey * wz - ez * wy, ez * wx - ex * wz, ex * wy - ey * wx, out ang))
            { if (Math.Abs(ang) > 1e-6) Check(M.FrameObj.SetLocalAxes(name, ang), "SetLocalAxes " + name); }
            else { noWeb++; addTo("FLAG_NO_ORIENTATION", name); }
            // cardinal point: the steel model's CP numbering is SAP's (8 = top centre); empty = centroid
            double cp = Num(r, "CP");
            bool cpSet = !double.IsNaN(cp) && cp >= 1 && cp <= 11 && cp != 10;
            if (cpSet || hasOff)
            {
                // offsets in global directions, stiffness transformed (the offset is rigid); 10 = centroid when no CP is given
                Check(M.FrameObj.SetInsertionPoint(name, cpSet ? (int)cp : 10, false, true, ref o1, ref o2, "Global"), "SetInsertionPoint " + name);
                if (hasOff) { offsetFrames++; addTo("STEEL_JOINT_OFFSET", name); }
            }
            bool hi = Hinged(Val(r, "ReleaseI")), hj = Hinged(Val(r, "ReleaseJ"));
            if (hi || hj)
            {
                // M2, M3 released (indices 4, 5); torsion kept (user rule 2026-10-30)
                bool[] ii = { false, false, false, false, hi, hi }, jr = { false, false, false, false, hj, hj };
                double[] si = new double[6], sj = new double[6];
                Check(M.FrameObj.SetReleases(name, ref ii, ref jr, ref si, ref sj), "SetReleases " + name);
                released++; ends += (hi ? 1 : 0) + (hj ? 1 : 0);
                addTo("STEEL_RELEASED", name);
            }
            addTo("STEEL_ALL", name);
            if (sec == SteelNoSize) { addTo("FLAG_NO_SIZE", name); noSize++; }
            foreach (string f in Val(r, "Flags").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)) addTo("FLAG_" + Clean(f), name);
        }
        return F("NEW steel: {0} frames, {1} with releases ({2} hinged ends, M2+M3), restraints {3} pinned + {4} fixed + {8} pinned+T (torsion), {5} without a size, {6} without a web direction, {9} with joint offsets (STEEL_JOINT_OFFSET){7}",
            steelFrames.Count, released, ends, pinned, fixd, noSize, noWeb,
            failed.Count > 0 ? F("; SAP refused {0} ({1})", failed.Count, string.Join(" ", failed.Take(10).ToArray())) : "", pinnedT, offsetFrames);
    }

    string AddSupports(List<Dictionary<string, string>> rows, Dictionary<string, string> joints, HashSet<string> sections, Action<string, string> addTo, Dictionary<string, string> steelFrames)
    {
        var byType = new SortedDictionary<string, int>(StringComparer.Ordinal); var bad = new List<string>();
        var meshed = new HashSet<string>(StringComparer.Ordinal);
        foreach (Dictionary<string, string> r in rows)
        {
            string id = Val(r, "SupportId"), dj, sj;
            if (!joints.TryGetValue(Val(r, "DuctJoint"), out dj)) { bad.Add(id + " (duct joint " + Val(r, "DuctJoint") + " not in the model)"); continue; }
            string sk = Val(r, "SteelJoint");
            if (!joints.TryGetValue(sk, out sj))
            {
                double sx = Num(r, "SX"), sy = Num(r, "SY"), sz = Num(r, "SZ");
                if (double.IsNaN(sx) || double.IsNaN(sy) || double.IsNaN(sz)) { bad.Add(id + " (no steel joint)"); continue; }
                sj = Joint(joints, sk != "" ? sk : "SS_" + id, sx, sy, sz);
                // a new joint inside the steel member's span: mesh the member there (else it is not connected)
                string member;
                if (steelFrames.TryGetValue(Val(r, "SteelMember"), out member) && meshed.Add(member))
                    Check(M.FrameObj.SetAutoMesh(member, true, true, false, 0, 0), "SetAutoMesh " + member);
                else if (member == null) bad.Add(id + " (new steel joint, steel member " + Val(r, "SteelMember") + " not in the model: stub hangs free)");
            }
            string sec = Section(sections, new Dictionary<string, string> { { "Shape", "rigid" } }), name = "";
            if (M.FrameObj.AddByPoint(dj, sj, ref name, sec, "SUP_" + id) != 0) { bad.Add(id + " (SAP refused the stub)"); continue; }
            string type = Clean(Val(r, "Type") == "" ? "guide" : Val(r, "Type"));
            addTo("SUPPORT_ALL", name); addTo("SUPPORT_" + type, name); addTo("TAP_STUBS", name);
            foreach (string f in Val(r, "Flags").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)) addTo("FLAG_" + Clean(f), name);
            int c; byType.TryGetValue(type, out c); byType[type] = c + 1;
        }
        return F("NEW supports: {0} TAP_STUB links ({1}), {2} steel frames meshed at new support joints{3}",
            byType.Values.Sum(), byType.Count == 0 ? "none" : string.Join(", ", byType.Select(kv => kv.Key + " " + kv.Value).ToArray()), meshed.Count,
            bad.Count > 0 ? F("; {0} problems: {1}", bad.Count, string.Join("; ", bad.Take(10).ToArray())) : "");
    }

    // PINNED+T (steel interface v2, stability rule 4, 2026-10-30): U1-U3 plus the rotation about the member's own axis
    // (TAxisX..Z), bending free. Axis within 1 deg of a global axis: that global rotation. Otherwise the joint's local
    // axes are turned so local 1 = the axis (SAP point angles a about 3, then b about the new 2: local 1 =
    // (cos a cos b, sin a cos b, -sin b)) and local R1 is restrained (SAP restraints are in the joint's local axes).
    void PinTorsion(string pt, Dictionary<string, string> r)
    {
        double[] u = Unit(new[] { Num(r, "TAxisX"), Num(r, "TAxisY"), Num(r, "TAxisZ") });
        if (u == null || double.IsNaN(u[0]) || double.IsNaN(u[1]) || double.IsNaN(u[2])) throw new Exception("Restraint PINNED+T at " + r["Joint"] + " needs TAxisX/Y/Z");
        bool[] dof = { true, true, true, false, false, false };
        double cos1 = Math.Cos(Math.PI / 180);
        int g = -1;
        for (int k = 0; k < 3; k++) if (Math.Abs(u[k]) >= cos1) g = k;
        if (g >= 0) dof[3 + g] = true;
        else
        {
            double a = Math.Atan2(u[1], u[0]) * 180 / Math.PI, b = -Math.Asin(Math.Max(-1, Math.Min(1, u[2]))) * 180 / Math.PI;
            Check(M.PointObj.SetLocalAxes(pt, a, b, 0), "SetLocalAxes " + pt);
            dof[3] = true;
        }
        Check(M.PointObj.SetRestraint(pt, ref dof), "SetRestraint " + pt);
    }

    public static bool Hinged(string rel)
    {
        string s = rel.Trim().ToUpperInvariant().Replace("+", "").Replace(" ", "");
        if (s == "") return false;
        if (s == "M2M3") return true;
        throw new Exception("release '" + rel + "': M2M3 (hinged) or empty (continuous)");
    }

    // a joint offset column of steel_frames.csv; absent (older files) or empty = 0
    static double Off(Dictionary<string, string> r, string col)
    {
        string v; double d;
        return r.TryGetValue(col, out v) && double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d) ? d : 0;
    }

    void AddPoint(string g, string pt)
    {
        List<string> l; if (!pointGroups.TryGetValue(g, out l)) { l = new List<string>(); pointGroups[g] = l; }
        l.Add(pt);
    }

    // the steel model's section name (W12X26) with D_in / Bf_in / Tf_in / Tw_in -> SetISection; no dims -> STEEL_NOSIZE
    string SteelSection(HashSet<string> made, Dictionary<string, string> r)
    {
        double d = Num(r, "D_in"), bf = Num(r, "Bf_in"), tf = Num(r, "Tf_in"), tw = Num(r, "Tw_in");
        string name = Clean(Val(r, "Section")).Replace('/', '_');
        if (name == "" || !(d > 0 && bf > 0 && tf > 0 && tw > 0))
        {
            if (made.Add(SteelNoSize)) Check(M.PropFrame.SetISection(SteelNoSize, SteelMat, 12, 8, 0.5, 0.3, 8, 0.5, -1, "", ""), "SetISection " + SteelNoSize);
            return SteelNoSize;
        }
        if (made.Add(name)) Check(M.PropFrame.SetISection(name, SteelMat, d, bf, tf, tw, bf, tf, -1, "", ""), "SetISection " + name);
        return name;
    }
}
