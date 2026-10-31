using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

// SteelModel  --  steel members (SteelToPluto's List<SteelMember>) -> an analysis model: joints, frames split at
// every joint on them, end releases and open-end restraints. C# 5 / Add-Type compatible. Units: INCHES, the same
// frame as the input (meters x 39.37; no other transform).
//
// Connectivity: a member end within SnapIn of another member's centreline snaps onto it. Near that member's end
// (within SnapIn) it joins that end; otherwise the end lands on the centreline and that member is split there
// (it stays continuous). Ends that snap nowhere and are not snapped onto are OPEN.
// Boundary conditions (rules agreed with the user; vault: steel model):
//   open end      member below SlopePinnedDeg from horizontal -> PINNED (U1-3), else FIXED (all 6); farther than
//                 within NearIn of other steel (but beyond SnapIn) -> flag OPEN_NEAR, a probably missed connection
//   framed        an end landing on a continuous member -> M2M3 released (torsion kept)
//   corner        ends meeting ends -> M2M3 on all of them, except: three or more ends where exactly one is vertical
//                 (within VerticalDeg) and the rest are not -> the vertical keeps its moments
//   collinear     exactly two ends meeting, axes within CollinearDeg -> M2M3 on both, condition 'collinear' (own
//                 viewer group so the rule can change)
// Stability rules (user decision 2026-10-30, the demo SAP model could not run MODAL):
//   column-base   the bottom end of a vertical member with nothing below it (a brace landing there too) -> FIXED,
//                 the column keeps its moments, the others M2M3
//   kept moments  a joint where every end would be M2M3 (corner, collinear): one member keeps its moments (vertical,
//                 deepest, longest; Frame.KeptI/J) so the joint rotation has stiffness; statics unchanged
//   PINNED+T      a lone member pinned at both ends: its I joint also holds rotation about the member axis (TAxis)
public class SteelModel
{
    public class Settings
    {
        public double SnapIn = 6, NearIn = 24, CollinearDeg = 5, SlopePinnedDeg = 45, VerticalDeg = 10;
        public double MergeIn = 1;   // joints landing on one member within this distance along it are one joint
    }

    public class Joint
    {
        public int Id; public double X, Y, Z;
        public string Restraint = "";    // PINNED | PINNED+T (also torsion about TAxis) | FIXED | ""
        public string Condition = "";    // open-pinned | open-fixed | column-base | framed | corner | collinear | ""
        public double[] TAxis;           // PINNED+T: the unit member axis the rotation is held about
        public List<string> Flags = new List<string>();
        public string Name { get { return "S" + Id; } }
    }

    public class Frame
    {
        public int Id, I, J, Source;          // Source = index into Members
        public string ReleaseI = "", ReleaseJ = "", EndI = "interior", EndJ = "interior";
        public bool KeptI, KeptJ;             // the end keeps its moments only for stability (rule 3): a hinge all the same
        public string Name { get { return "SF" + Id; } }
    }

    public readonly Settings S;
    public readonly List<SteelMember> Members = new List<SteelMember>();
    public readonly List<Joint> Joints = new List<Joint>();
    public readonly List<Frame> Frames = new List<Frame>();
    public int Snapped, OnSide, Open, OpenNear, ZeroLength, ColumnBases, KeptMoments, TorsionHeld;

    public SteelModel(Settings s) { S = s ?? new Settings(); }

    const double InPerM = 1 / 0.0254;
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static double[] In(Vec3 p) { return new[] { p.X * InPerM, p.Y * InPerM, p.Z * InPerM }; }

    // ---------- build ----------

    public static SteelModel Build(List<SteelMember> steel, Settings s)
    {
        var sm = new SteelModel(s);
        sm.Run(steel ?? new List<SteelMember>());
        return sm;
    }

    // end k of member m = id 2m+k; union-find over end ids
    int[] _par;
    int Find(int x) { while (_par[x] != x) { _par[x] = _par[_par[x]]; x = _par[x]; } return x; }
    void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) _par[Math.Max(a, b)] = Math.Min(a, b); }

    void Run(List<SteelMember> steel)
    {
        foreach (SteelMember b in steel)
        {
            if (Dist(In(b.P0), In(b.P1)) < 1e-3) { ZeroLength++; continue; }
            Members.Add(b);
        }
        int n = Members.Count;
        var A = new double[n][]; var B = new double[n][];
        for (int i = 0; i < n; i++) { A[i] = In(Members[i].P0); B[i] = In(Members[i].P1); }
        Func<int, double[]> endPos = e => (e & 1) == 0 ? A[e >> 1] : B[e >> 1];
        _par = new int[2 * n]; for (int i = 0; i < 2 * n; i++) _par[i] = i;

        // 1. each end: nearest other centreline within SnapIn
        var onSide = new Dictionary<int, KeyValuePair<int, double>>();   // end -> (member, t) landing inside it
        for (int e = 0; e < 2 * n; e++)
        {
            int mi = e >> 1; double[] p = endPos(e);
            int best = -1; double bestD = double.MaxValue, bestT = 0;
            for (int j = 0; j < n; j++)
            {
                if (j == mi) continue;
                double t, d; Proj(A[j], B[j], p, out t, out d);
                if (d < bestD) { bestD = d; best = j; bestT = t; }
            }
            if (best < 0 || bestD > S.SnapIn) continue;
            Snapped++;
            double L = Dist(A[best], B[best]);
            if (bestT * L <= S.SnapIn) Union(e, 2 * best);
            else if ((1 - bestT) * L <= S.SnapIn) Union(e, 2 * best + 1);
            else onSide[e] = new KeyValuePair<int, double>(best, bestT);
        }

        // 2. side landings: a group landing on a member's side; per member, landings within MergeIn are one joint
        var sideOf = new Dictionary<int, KeyValuePair<int, double>>();   // group root -> (member, t)
        foreach (KeyValuePair<int, KeyValuePair<int, double>> kv in onSide.OrderBy(x => x.Key))
        {
            int r = Find(kv.Key);
            if (!sideOf.ContainsKey(r)) sideOf[r] = kv.Value;
        }
        var byMember = new Dictionary<int, List<KeyValuePair<double, int>>>();   // member -> (t, root)
        foreach (KeyValuePair<int, KeyValuePair<int, double>> kv in sideOf)
        {
            List<KeyValuePair<double, int>> l;
            if (!byMember.TryGetValue(kv.Value.Key, out l)) { l = new List<KeyValuePair<double, int>>(); byMember[kv.Value.Key] = l; }
            l.Add(new KeyValuePair<double, int>(kv.Value.Value, kv.Key));
        }
        foreach (KeyValuePair<int, List<KeyValuePair<double, int>>> kv in byMember)
        {
            double L = Dist(A[kv.Key], B[kv.Key]);
            var l = kv.Value.OrderBy(x => x.Key).ToList();
            for (int i = 1; i < l.Count; i++)
                if ((l[i].Key - l[i - 1].Key) * L <= S.MergeIn) Union(l[i].Value, l[i - 1].Value);
        }
        // re-key side landings by final root (first landing wins its position)
        var side = new Dictionary<int, KeyValuePair<int, double>>();
        foreach (KeyValuePair<int, KeyValuePair<int, double>> kv in sideOf.OrderBy(x => x.Key))
        {
            int r = Find(kv.Key);
            if (!side.ContainsKey(r)) side[r] = kv.Value;
        }

        // 3. joints, one per group; position: on the side member's centreline, else the lowest end's point
        var jointOf = new Dictionary<int, Joint>();
        for (int e = 0; e < 2 * n; e++)
        {
            int r = Find(e);
            if (jointOf.ContainsKey(r)) continue;
            double[] p;
            KeyValuePair<int, double> sd;
            if (side.TryGetValue(r, out sd)) p = Lerp(A[sd.Key], B[sd.Key], sd.Value);
            else p = endPos(r);
            var jt = new Joint { Id = Joints.Count + 1, X = p[0], Y = p[1], Z = p[2] };
            Joints.Add(jt); jointOf[r] = jt;
        }
        OnSide = side.Count;

        // 4. frames: each member split at the side joints on it
        var through = new HashSet<Joint>();
        var memberSplits = new Dictionary<int, List<KeyValuePair<double, Joint>>>();
        foreach (KeyValuePair<int, KeyValuePair<int, double>> kv in side)
        {
            List<KeyValuePair<double, Joint>> l;
            if (!memberSplits.TryGetValue(kv.Value.Key, out l)) { l = new List<KeyValuePair<double, Joint>>(); memberSplits[kv.Value.Key] = l; }
            l.Add(new KeyValuePair<double, Joint>(kv.Value.Value, jointOf[kv.Key]));
            through.Add(jointOf[kv.Key]);
        }
        var endFrame = new Dictionary<int, KeyValuePair<Frame, bool>>();   // end id -> (frame, isI)
        for (int m = 0; m < n; m++)
        {
            var chain = new List<Joint>();
            chain.Add(jointOf[Find(2 * m)]);
            List<KeyValuePair<double, Joint>> sp;
            if (memberSplits.TryGetValue(m, out sp)) foreach (KeyValuePair<double, Joint> x in sp.OrderBy(y => y.Key)) chain.Add(x.Value);
            chain.Add(jointOf[Find(2 * m + 1)]);
            Frame first = null, last = null;
            for (int i = 0; i + 1 < chain.Count; i++)
            {
                if (chain[i] == chain[i + 1]) continue;
                var f = new Frame { Id = Frames.Count + 1, I = chain[i].Id, J = chain[i + 1].Id, Source = m };
                Frames.Add(f);
                if (first == null) first = f; last = f;
            }
            if (first == null) continue;   // collapsed onto one joint
            endFrame[2 * m] = new KeyValuePair<Frame, bool>(first, true);
            endFrame[2 * m + 1] = new KeyValuePair<Frame, bool>(last, false);
        }

        // 5. conditions per joint
        var endsAt = new Dictionary<int, List<int>>();
        foreach (int e in endFrame.Keys)
        {
            int jid = jointOf[Find(e)].Id; List<int> l;
            if (!endsAt.TryGetValue(jid, out l)) { l = new List<int>(); endsAt[jid] = l; }
            l.Add(e);
        }
        foreach (KeyValuePair<int, List<int>> kv in endsAt)
            ClassifyJoint(Joints[kv.Key - 1], kv.Value, through.Contains(Joints[kv.Key - 1]), endFrame, A, B);
        HoldLoneTorsion();
    }

    // the condition, restraint and end releases at one joint from the member ends there (ids 2m+k)
    void ClassifyJoint(Joint jt, List<int> ends, bool onThrough, Dictionary<int, KeyValuePair<Frame, bool>> endFrame,
                       double[][] A, double[][] B)
    {
        if (onThrough)
        {
            jt.Condition = "framed";
            foreach (int e in ends) SetEnd(endFrame[e], "framed", "M2M3");
            return;
        }
        if (ends.Count == 1)
        {
            int e = ends[0], m = e >> 1;
            bool pinned = SlopeDeg(A[m], B[m]) < S.SlopePinnedDeg;
            jt.Condition = pinned ? "open-pinned" : "open-fixed";
            jt.Restraint = pinned ? "PINNED" : "FIXED";
            SetEnd(endFrame[e], jt.Condition, "");
            Open++;
            double near = double.MaxValue, t, d; double[] p = { jt.X, jt.Y, jt.Z };
            for (int j = 0; j < A.Length; j++) { if (j == m) continue; Proj(A[j], B[j], p, out t, out d); near = Math.Min(near, d); }
            if (near <= S.NearIn) { jt.Flags.Add("OPEN_NEAR"); OpenNear++; }
            return;
        }
        // column base (rule 2, 2026-10-30): every member at the joint rises from it (by more than SnapIn) and one of them
        // is vertical, e.g. a brace landing on the column foot; a hung-frame knee (beam level with the joint) is not a
        // base. Two verticals: the one KeepOne prefers (deepest, longest) is the column.
        double z = jt.Z;
        if (ends.All(e => OtherEnd(A, B, e)[2] > z + S.SnapIn) && ends.Any(e => IsVertical(A, B, e)))
        {
            int col = KeepOne(ends.Where(e => IsVertical(A, B, e)).ToList(), A, B);
            jt.Condition = "column-base"; jt.Restraint = "FIXED"; ColumnBases++;
            foreach (int e in ends) SetEnd(endFrame[e], e == col ? "column-base" : "corner", e == col ? "" : "M2M3");
            return;
        }
        string cond = "corner";
        if (ends.Count == 2 && Math.Abs(Dot(Axis(A, B, ends[0] >> 1), Axis(A, B, ends[1] >> 1))) >= Math.Cos(S.CollinearDeg * Math.PI / 180))
            cond = "collinear";
        jt.Condition = cond;
        var vert = ends.Where(e => IsVertical(A, B, e)).ToList();
        int keep = cond == "corner" && ends.Count >= 3 && vert.Count == 1 ? vert[0] : -1;
        // every end hinged would leave the joint's rotation with no stiffness (rule 3, 2026-10-30): one member keeps
        // its moments; joint equilibrium still makes its end moment zero, so the statics are those of the hinges
        bool kept = keep < 0;
        if (kept) { keep = KeepOne(ends, A, B); KeptMoments++; }
        foreach (int e in ends) SetEnd(endFrame[e], cond, e == keep ? "" : "M2M3");
        if (kept) { var fe = endFrame[keep]; if (fe.Value) fe.Key.KeptI = true; else fe.Key.KeptJ = true; }
    }

    // a lone member pinned at both ends spins about its own axis (rule 4, 2026-10-30): its I joint also holds the
    // rotation about the member axis (PINNED+T, TAxis); bending stays free
    void HoldLoneTorsion()
    {
        var degree = new int[Joints.Count + 1];   // frame ends per joint id
        foreach (Frame f in Frames) { degree[f.I]++; degree[f.J]++; }
        foreach (Frame f in Frames)
        {
            Joint ji = Joints[f.I - 1], jj = Joints[f.J - 1];
            if (ji.Restraint != "PINNED" || jj.Restraint != "PINNED" || degree[f.I] != 1 || degree[f.J] != 1) continue;
            double[] a = JointPos(f.I), b = JointPos(f.J); double L = Dist(a, b);
            ji.Restraint = "PINNED+T"; ji.TAxis = new[] { (b[0] - a[0]) / L, (b[1] - a[1]) / L, (b[2] - a[2]) / L };
            TorsionHeld++;
        }
    }

    bool IsVertical(double[][] A, double[][] B, int e) { return SlopeDeg(A[e >> 1], B[e >> 1]) >= 90 - S.VerticalDeg; }

    static double[] OtherEnd(double[][] A, double[][] B, int e) { return (e & 1) == 0 ? B[e >> 1] : A[e >> 1]; }

    // the end that keeps its moments at an all-hinged joint: a vertical member, then the deepest section, then the longest
    int KeepOne(List<int> ends, double[][] A, double[][] B)
    {
        return ends.OrderByDescending(e => IsVertical(A, B, e) ? 1 : 0)
                   .ThenByDescending(e => double.IsNaN(Members[e >> 1].D) ? 0 : Members[e >> 1].D)
                   .ThenByDescending(e => Dist(A[e >> 1], B[e >> 1])).ThenBy(e => e).First();
    }

    static void SetEnd(KeyValuePair<Frame, bool> fe, string cond, string release)
    {
        if (fe.Value) { fe.Key.EndI = cond; fe.Key.ReleaseI = release; }
        else { fe.Key.EndJ = cond; fe.Key.ReleaseJ = release; }
    }

    // ---------- queries ----------

    public double[] JointPos(int id) { Joint j = Joints[id - 1]; return new[] { j.X, j.Y, j.Z }; }

    // the frame nearest to a point (inches), its parameter t (0..1, I -> J) and distance; null when there are no frames
    public Frame NearestFrame(double[] p, out double t, out double dist)
    {
        Frame best = null; t = 0; dist = double.MaxValue;
        foreach (Frame f in Frames)
        {
            double tt, d; Proj(JointPos(f.I), JointPos(f.J), p, out tt, out d);
            if (d < dist) { dist = d; t = tt; best = f; }
        }
        return best;
    }

    // a new joint on frame f at t (0..1, I -> J): the frame is split in two, continuous (the new joint has no
    // condition); the J part keeps f's J end. Returns the new joint, or the end joint when t lands within MergeIn
    // of an end. For other lanes (duct supports): add joints on the steel after Build, before WriteCsv.
    public Joint SplitAt(Frame f, double t, string flag)
    {
        double[] a = JointPos(f.I), b = JointPos(f.J);
        double L = Dist(a, b);
        if (t * L <= S.MergeIn) return Joints[f.I - 1];
        if ((1 - t) * L <= S.MergeIn) return Joints[f.J - 1];
        double[] p = Lerp(a, b, t);
        var jt = new Joint { Id = Joints.Count + 1, X = p[0], Y = p[1], Z = p[2] };
        if (!string.IsNullOrEmpty(flag)) jt.Flags.Add(flag);
        Joints.Add(jt);
        var g = new Frame { Id = Frames.Count + 1, I = jt.Id, J = f.J, Source = f.Source, EndJ = f.EndJ, ReleaseJ = f.ReleaseJ };
        f.J = jt.Id; f.EndJ = "interior"; f.ReleaseJ = "";
        Frames.Insert(Frames.IndexOf(f) + 1, g);
        return jt;
    }

    // top of steel above a point of frame f at t (inches): the routed line + the cardinal-point offset (OffY along the
    // flange direction, OffZ along the web, as in steel_frames.csv) + web x D/2. The top flange's centre, outer face.
    public double[] TopAt(Frame f, double t)
    {
        SteelMember b = Members[f.Source];
        int dummy = 0;
        double[] w = SteelGeom.WebDir(b, ref dummy), ly = SteelGeom.ResolveLocalY(b, ref dummy);
        double oy, oz; SteelGeom.MemberOffsets(b, InPerM, out oy, out oz);
        double hd = (SteelGeom.Sized(b) ? b.D : SteelGeom.UD) * InPerM / 2;
        double[] p = Lerp(JointPos(f.I), JointPos(f.J), t);
        for (int i = 0; i < 3; i++) p[i] += ly[i] * oy + w[i] * (oz + hd);
        return p;
    }

    // the viewer group an end belongs to (null = none: continuous, or a corner vertical kept)
    public static string EndGroup(string cond, string release)
    {
        return EndGroup(cond, release, false);
    }

    // kept: the end keeps its moments only for stability (Frame.KeptI/J), drawn as the hinge it is
    public static string EndGroup(string cond, string release, bool kept)
    {
        if (cond == "open-pinned" || cond == "open-fixed") return cond;
        if (cond == "column-base") return "open-fixed";
        if (release != "M2M3" && !kept) return null;
        return cond == "collinear" ? "collinear-hinged" : "hinged";
    }

    public string Summary()
    {
        var c = Joints.GroupBy(j => j.Condition == "" ? "interior" : j.Condition).OrderBy(g => g.Key, StringComparer.Ordinal)
                      .Select(g => g.Key + " " + g.Count()).ToArray();
        int rel = Frames.Count(f => f.ReleaseI != "") + Frames.Count(f => f.ReleaseJ != "");
        return string.Format(Inv, "steel model: {0} members -> {1} frames, {2} joints ({3}); ends snapped {4} ({5} onto a member's side); " +
            "open {6} ({7} OPEN_NEAR, other steel within {8} in); released ends (M2M3) {9}; zero length dropped {10}; " +
            "stability: column bases {11}, all-hinged joints with one member kept {12}, lone members with torsion held {13}",
            Members.Count, Frames.Count, Joints.Count, string.Join(", ", c), Snapped, OnSide, Open, OpenNear, S.NearIn, rel, ZeroLength,
            ColumnBases, KeptMoments, TorsionHeld);
    }

    // ---------- files (inches) ----------

    public void WriteCsv(string outBase)
    {
        var s = new StringBuilder();
        s.AppendLine("Joint,X_in,Y_in,Z_in,Restraint,Condition,Flags,TAxisX,TAxisY,TAxisZ");
        foreach (Joint j in Joints)
            s.AppendLine(string.Join(",", new[] { j.Name, R3(j.X), R3(j.Y), R3(j.Z), j.Restraint, j.Condition, string.Join(" ", j.Flags.ToArray()),
                j.TAxis != null ? j.TAxis[0].ToString("0.######", Inv) : "", j.TAxis != null ? j.TAxis[1].ToString("0.######", Inv) : "",
                j.TAxis != null ? j.TAxis[2].ToString("0.######", Inv) : "" }));
        File.WriteAllText(outBase + ".steel_joints.csv", s.ToString(), new UTF8Encoding(false));

        s = new StringBuilder();
        s.AppendLine("Frame,JointI,JointJ,XI_in,YI_in,ZI_in,XJ_in,YJ_in,ZJ_in,Section,D_in,Bf_in,Tf_in,Tw_in,WebX,WebY,WebZ,CP,OffY_in,OffZ_in,ReleaseI,ReleaseJ,EndI,EndJ,MemberOid,Flags,StabilityI,StabilityJ");
        foreach (Frame f in Frames)
        {
            SteelMember b = Members[f.Source]; Joint ji = Joints[f.I - 1], jj = Joints[f.J - 1];
            int dummy = 0; double[] web = SteelGeom.WebDir(b, ref dummy);
            double oy, oz; SteelGeom.MemberOffsets(b, InPerM, out oy, out oz);
            bool sized = SteelGeom.Sized(b);
            s.AppendLine(string.Join(",", new[] { f.Name, ji.Name, jj.Name, R3(ji.X), R3(ji.Y), R3(ji.Z), R3(jj.X), R3(jj.Y), R3(jj.Z),
                Csv(sized ? b.SectionName : "UNSIZED"), sized ? R3(b.D * InPerM) : "", sized ? R3(b.Bf * InPerM) : "", sized ? R3(b.Tf * InPerM) : "", sized ? R3(b.Tw * InPerM) : "",
                web[0].ToString("0.######", Inv), web[1].ToString("0.######", Inv), web[2].ToString("0.######", Inv),
                b.Cp.ToString(Inv), R3(oy), R3(oz), f.ReleaseI, f.ReleaseJ, f.EndI, f.EndJ, Csv(b.MemberOid ?? ""), sized ? "" : "UNSIZED", f.KeptI ? "kept" : "", f.KeptJ ? "kept" : "" }));
        }
        File.WriteAllText(outBase + ".steel_frames.csv", s.ToString(), new UTF8Encoding(false));
    }

    // ---------- math (inches) ----------

    static string R3(double v) { return Math.Round(v, 3).ToString("0.###", Inv); }
    static string Csv(string v) { return v.IndexOfAny(new[] { ',', '"' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v; }
    static double Dot(double[] a, double[] b) { return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]; }
    static double Dist(double[] a, double[] b) { double x = a[0] - b[0], y = a[1] - b[1], z = a[2] - b[2]; return Math.Sqrt(x * x + y * y + z * z); }
    static double[] Lerp(double[] a, double[] b, double t) { return new[] { a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, a[2] + (b[2] - a[2]) * t }; }
    static double[] Axis(double[][] A, double[][] B, int m)
    {
        double L = Dist(A[m], B[m]);
        return new[] { (B[m][0] - A[m][0]) / L, (B[m][1] - A[m][1]) / L, (B[m][2] - A[m][2]) / L };
    }
    // angle of a member from horizontal, degrees
    static double SlopeDeg(double[] a, double[] b)
    {
        double h = Math.Sqrt((b[0] - a[0]) * (b[0] - a[0]) + (b[1] - a[1]) * (b[1] - a[1]));
        return Math.Atan2(Math.Abs(b[2] - a[2]), h) * 180 / Math.PI;
    }
    // closest point on segment a-b (t clamped 0..1) and the distance to it
    static void Proj(double[] a, double[] b, double[] p, out double t, out double d)
    {
        double[] ab = { b[0] - a[0], b[1] - a[1], b[2] - a[2] };
        double L2 = Dot(ab, ab);
        t = L2 < 1e-12 ? 0 : ((p[0] - a[0]) * ab[0] + (p[1] - a[1]) * ab[1] + (p[2] - a[2]) * ab[2]) / L2;
        t = Math.Max(0, Math.Min(1, t));
        d = Dist(Lerp(a, b, t), p);
    }
}
