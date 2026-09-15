// Expansion-joint release models over the SAP2000 OAPI (HVAC). C# 5, Add-Type under PS 5.1.
// Link model: each candidate joint is disconnected and its pieces rejoined with a stiff zero-length linear
// link; one self-equilibrated unit load pair per link DOF (+1 at the far joint, -1 at the master joint,
// global axes). Direct model: the candidates are simply disconnected. Both are saved as copies of the
// open model; the original file is never written. DuctRelease.cs does the Woodbury release offline.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SAP2000v1;

public class SapRelease
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly string[] Dof = { "F1", "F2", "F3", "M1", "M2", "M3" };
    public const string LinkProp = "EJ_STIFF";

    public SapSurvey S;
    cSapModel M { get { return S.Model; } }
    public List<string> Log = new List<string>();
    public SapRelease(SapSurvey s) { S = s; }
    public string ModelPath() { return M.GetModelFilename(true); }

    class Pair
    {
        public string Candidate, Master, Other, Link;
        public string[] Patterns = new string[6];
    }

    // The joints created by disconnecting `candidate` (the master keeps its name), plus the frames that met there.
    string[] Disconnect(string candidate, out string[] frames)
    {
        int n = 0; int[] types = null; string[] objs = null; int[] pn = null;
        Check(M.PointObj.GetConnectivity(candidate, ref n, ref types, ref objs, ref pn), "GetConnectivity " + candidate);
        List<string> fr = new List<string>();
        for (int i = 0; i < n; i++) if (types[i] == 2) fr.Add(objs[i]);
        frames = fr.ToArray();
        if (fr.Count < 2) throw new Exception("joint " + candidate + " has " + fr.Count + " frame(s): nothing to disconnect");
        bool[] r = new bool[6];
        M.PointObj.GetRestraint(candidate, ref r);
        foreach (bool b in r) if (b) throw new Exception("joint " + candidate + " is restrained: not a candidate");
        Check(M.SelectObj.ClearSelection(), "ClearSelection");
        Check(M.PointObj.SetSelected(candidate, true, eItemType.Objects), "SetSelected " + candidate);
        int np = 0; string[] pts = null;
        Check(M.EditPoint.Disconnect(ref np, ref pts), "EditPoint.Disconnect " + candidate);
        M.SelectObj.ClearSelection();
        // Disconnect reports the points involved; take the joints now on the frames that met here.
        List<string> joints = new List<string>();
        foreach (string f in fr)
        {
            string pi = "", pj = "";
            M.FrameObj.GetPoints(f, ref pi, ref pj);
            foreach (string p in new string[] { pi, pj })
            {
                if (joints.Contains(p)) continue;
                double x = 0, y = 0, z = 0, cx = 0, cy = 0, cz = 0;
                M.PointObj.GetCoordCartesian(p, ref x, ref y, ref z, "Global");
                M.PointObj.GetCoordCartesian(candidate, ref cx, ref cy, ref cz, "Global");
                if (Math.Abs(x - cx) + Math.Abs(y - cy) + Math.Abs(z - cz) < 1e-6) joints.Add(p);
            }
        }
        if (joints.Count != fr.Count) throw new Exception(F("disconnect at {0}: {1} frames but {2} coincident joints ({3}); Disconnect reported {4} points", candidate, fr.Count, joints.Count, string.Join(",", joints.ToArray()), np));
        joints.Remove(candidate); joints.Insert(0, candidate);   // master first, if it kept its name
        Log.Add(F("disconnected {0}: frames {1} -> joints {2}", candidate, string.Join(",", fr), string.Join(",", joints.ToArray())));
        return joints.ToArray();
    }

    // Stiffness of the stiffest frame at the joint: EA/L (translation) and 4EI/L, larger I (rotation).
    void FrameStiffness(string[] frames, out double kt, out double kr)
    {
        kt = 0; kr = 0;
        foreach (string f in frames)
        {
            string sec = "", auto = "", mat = "";
            M.FrameObj.GetSection(f, ref sec, ref auto);
            M.PropFrame.GetMaterial(sec, ref mat);
            double e = 0, u = 0, a = 0, temp = 0;
            M.PropMaterial.GetMPIsotropic(mat, ref e, ref u, ref a, ref temp);
            double A = 0, As2 = 0, As3 = 0, J = 0, I22 = 0, I33 = 0, S22 = 0, S33 = 0, Z22 = 0, Z33 = 0, r22 = 0, r33 = 0;
            M.PropFrame.GetSectProps(sec, ref A, ref As2, ref As3, ref J, ref I22, ref I33, ref S22, ref S33, ref Z22, ref Z33, ref r22, ref r33);
            string pi = "", pj = "";
            M.FrameObj.GetPoints(f, ref pi, ref pj);
            double xi = 0, yi = 0, zi = 0, xj = 0, yj = 0, zj = 0;
            M.PointObj.GetCoordCartesian(pi, ref xi, ref yi, ref zi, "Global");
            M.PointObj.GetCoordCartesian(pj, ref xj, ref yj, ref zj, "Global");
            double L = Math.Sqrt((xj - xi) * (xj - xi) + (yj - yi) * (yj - yi) + (zj - zi) * (zj - zi));
            if (L <= 0) continue;
            kt = Math.Max(kt, e * A / L);
            kr = Math.Max(kr, 4 * e * Math.Max(I22, I33) / L);
        }
        if (kt <= 0 || kr <= 0) throw new Exception("could not size the link stiffness from frames " + string.Join(",", frames));
    }

    // Save-as savePath, disconnect + stiff-link each candidate, add the unit pair patterns / cases, run,
    // export forces.tsv (group, every case and combo) and linkdisp.tsv (both joints of every link, every
    // output case) and links.tsv. kt / kr = factor x the stiffest adjoining frame (global for all links).
    public string BuildLinkModel(string savePath, string[] candidates, double factor, string outDir, string group)
    {
        Directory.CreateDirectory(outDir);
        Check(M.File.Save(savePath), "Save " + savePath);
        Check(M.SetModelIsLocked(false), "unlock");
        Check(M.SetPresentUnits(eUnits.kip_in_F), "SetPresentUnits");
        List<Pair> pairs = new List<Pair>();
        double kt = 0, kr = 0;
        foreach (string c in candidates)
        {
            string[] frames;
            string[] joints = Disconnect(c, out frames);
            double t, r;
            FrameStiffness(frames, out t, out r);
            kt = Math.Max(kt, t); kr = Math.Max(kr, r);
            for (int k = 1; k < joints.Length; k++)
            {
                Pair p = new Pair();
                p.Candidate = c; p.Master = joints[0]; p.Other = joints[k];
                pairs.Add(p);
            }
        }
        kt *= factor; kr *= factor;
        bool[] dof = { true, true, true, true, true, true }, fix = new bool[6];
        double[] ke = { kt, kt, kt, kr, kr, kr }, ce = new double[6];
        Check(M.PropLink.SetLinear(LinkProp, ref dof, ref fix, ref ke, ref ce, 0, 0, false, false, "Pluto stiff expansion-joint link", ""), "PropLink.SetLinear");
        for (int i = 0; i < pairs.Count; i++)
        {
            Pair p = pairs[i];
            string name = "";
            Check(M.LinkObj.AddByPoint(p.Master, p.Other, ref name, false, LinkProp, "EJ" + (i + 1)), "LinkObj.AddByPoint " + p.Master + "-" + p.Other);
            p.Link = name;
            for (int d = 0; d < 6; d++)
            {
                string pat = "EJ" + (i + 1) + "_" + Dof[d];
                p.Patterns[d] = pat;
                Check(M.LoadPatterns.Add(pat, eLoadPatternType.Other, 0, true), "LoadPatterns.Add " + pat);
                double[] plus = new double[6], minus = new double[6];
                plus[d] = 1; minus[d] = -1;
                Check(M.PointObj.SetLoadForce(p.Other, pat, ref plus, true, "Global", eItemType.Objects), "SetLoadForce " + p.Other);
                Check(M.PointObj.SetLoadForce(p.Master, pat, ref minus, true, "Global", eItemType.Objects), "SetLoadForce " + p.Master);
            }
        }
        Check(M.File.Save(savePath), "Save " + savePath);
        RunLinearStaticOnly();

        List<string> rows = new List<string>();
        rows.Add("Link\tCandidate\tMaster\tOther\tkt_kip_in\tkr_kipin_rad\tPatterns");
        foreach (Pair p in pairs) rows.Add(Tab(p.Link, p.Candidate, p.Master, p.Other, R(kt), R(kr), string.Join(",", p.Patterns)));
        File.WriteAllText(Path.Combine(outDir, "links.tsv"), string.Join("\r\n", rows) + "\r\n");
        string msg = S.ExportForces(outDir, group, true);
        int disp = ExportLinkDisp(outDir, pairs);
        StringBuilder sb = new StringBuilder();
        sb.AppendLine(F("link model {0}: {1} candidate(s), {2} link(s), kt = {3} kip/in, kr = {4} kip-in/rad (factor {5})", Path.GetFileName(savePath), candidates.Length, pairs.Count, G(kt), G(kr), G(factor)));
        foreach (string l in Log) sb.AppendLine("  " + l);
        sb.AppendLine(msg);
        sb.AppendLine(F("linkdisp.tsv: {0} rows", disp));
        return sb.ToString();
    }

    // Joint displacements of both joints of every link (joint local axes = global unless rotated) for
    // every case and combo -> linkdisp.tsv. Assumes the output selection ExportForces left behind.
    int ExportLinkDisp(string outDir, List<Pair> pairs)
    {
        int rows = 0;
        using (StreamWriter w = new StreamWriter(Path.Combine(outDir, "linkdisp.tsv")))
        {
            w.Write("Link\tSide\tJoint\tOutputCase\tStepType\tU1\tU2\tU3\tR1\tR2\tR3\r\n");
            foreach (Pair p in pairs)
                foreach (string side in new string[] { "master", "other" })
                {
                    string j = side == "master" ? p.Master : p.Other;
                    double a = 0, b = 0, c = 0; bool adv = false;
                    M.PointObj.GetLocalAxes(j, ref a, ref b, ref c, ref adv);
                    if (adv || Math.Abs(a) + Math.Abs(b) + Math.Abs(c) > 1e-9) throw new Exception("joint " + j + " has rotated local axes: displacements would not be global");
                    int n = 0;
                    string[] obj = null, elm = null, cas = null, step = null;
                    double[] sn = null, u1 = null, u2 = null, u3 = null, r1 = null, r2 = null, r3 = null;
                    Check(M.Results.JointDispl(j, eItemTypeElm.ObjectElm, ref n, ref obj, ref elm, ref cas, ref step, ref sn, ref u1, ref u2, ref u3, ref r1, ref r2, ref r3), "JointDispl " + j);
                    for (int i = 0; i < n; i++) { w.Write(Tab(p.Link, side, j, cas[i], step[i], R(u1[i]), R(u2[i]), R(u3[i]), R(r1[i]), R(r2[i]), R(r3[i])) + "\r\n"); rows++; }
                }
        }
        return rows;
    }

    // Save-as savePath, disconnect the candidates (no links), run, export forces.tsv.
    public string BuildDirectModel(string savePath, string[] candidates, string outDir, string group)
    {
        Directory.CreateDirectory(outDir);
        Check(M.File.Save(savePath), "Save " + savePath);
        Check(M.SetModelIsLocked(false), "unlock");
        Check(M.SetPresentUnits(eUnits.kip_in_F), "SetPresentUnits");
        foreach (string c in candidates) { string[] frames; Disconnect(c, out frames); }
        Check(M.File.Save(savePath), "Save " + savePath);
        RunLinearStaticOnly();
        string msg = S.ExportForces(outDir, group, true);
        StringBuilder sb = new StringBuilder();
        sb.AppendLine(F("direct model {0}: {1} candidate(s) disconnected", Path.GetFileName(savePath), candidates.Length));
        foreach (string l in Log) sb.AppendLine("  " + l);
        sb.AppendLine(msg);
        return sb.ToString();
    }

    // Run every linear static case (the load vectors, the cases behind the combos, the unit pairs) and
    // nothing else: the modal case alone can take longer than all of them on the real model.
    void RunLinearStaticOnly()
    {
        Check(M.Analyze.SetRunCaseFlag("", false, true), "SetRunCaseFlag all off");
        int n = 0; string[] names = null;
        M.LoadCases.GetNameList(ref n, ref names);
        int on = 0;
        for (int i = 0; i < n; i++)
        {
            eLoadCaseType t = eLoadCaseType.LinearStatic; int sub = 0;
            M.LoadCases.GetTypeOAPI(names[i], ref t, ref sub);
            if (t != eLoadCaseType.LinearStatic) continue;
            Check(M.Analyze.SetRunCaseFlag(names[i], true, false), "SetRunCaseFlag " + names[i]);
            on++;
        }
        Log.Add(F("running {0} linear static cases of {1} (modal and other cases skipped)", on, n));
        Check(M.Analyze.RunAnalysis(), "RunAnalysis");
    }

    // ---------------------------------------------------------------- synthetic test model (SAP 26 here)
    // A duct for testing the release: a straight run along X (0..480 in, 60 in frames) into an elbow up
    // (480, 0, 0..240). Fixed at x = 0, U2+U3 at x = 240 and 480, pinned at the top. Box 20 x 10 x 0.06,
    // E 28000. Cases as the HVAC model names them: dead (self weight), Steel_Loading (joint loads),
    // three seismic (uniform frame loads), 7 Thermal (+100 F). Combos: 8 SRSS = SRSS(seismic);
    // 18 = dead + steel + 8 SRSS; 19 = dead + steel + thermal; 20 = dead + steel + 0.7 thermal. Group DUCT_ALL.
    // Candidates: the inline joints at x = 60..180 and 300..420, and z = 60..180 on the riser.
    public string BuildTestModel(string savePath)
    {
        Check(M.InitializeNewModel(eUnits.kip_in_F), "InitializeNewModel");
        Check(M.File.NewBlank(), "NewBlank");
        Check(M.PropMaterial.SetMaterial("S304L", eMatType.Steel, -1, "", ""), "SetMaterial");
        Check(M.PropMaterial.SetMPIsotropic("S304L", 28000, 0.3, 9.6e-6, 0), "SetMPIsotropic");
        Check(M.PropMaterial.SetWeightAndMass("S304L", 1, 2.9e-4, 0), "SetWeightAndMass");   // kip/in3
        Check(M.PropMaterial.SetOSteel_1("S304L", 30, 70, 30, 70, 1, 1, 0.015, 0.11, 0.17, -0.1, 0), "SetOSteel_1");
        Check(M.PropFrame.SetTube("DUCT_20x10", "S304L", 20, 10, 0.06, 0.06, -1, "", ""), "SetTube");
        Check(M.PropFrame.SetTube("DUCT_16x16", "S304L", 16, 16, 0.06, 0.06, -1, "", ""), "SetTube");
        Check(M.GroupDef.SetGroup("DUCT_ALL", -1, true, true, true, true, true, true, true, true, true, true, true), "SetGroup");
        string[] pats = { "1 DEAD", "Steel_Loading", "3 Seismic X", "4 Seismic Z", "5 Seismic Y - vert", "7 Thermal" };
        eLoadPatternType[] pt = { eLoadPatternType.Dead, eLoadPatternType.Dead, eLoadPatternType.Quake, eLoadPatternType.Quake, eLoadPatternType.Quake, eLoadPatternType.Temperature };
        for (int i = 0; i < pats.Length; i++) Check(M.LoadPatterns.Add(pats[i], pt[i], i == 0 ? 1 : 0, true), "LoadPatterns.Add " + pats[i]);
        // Frames: run 0..480 along X, riser 480 up to 240.
        List<string> frames = new List<string>();
        for (int i = 0; i < 8; i++) frames.Add(AddFrame(i * 60, 0, 0, (i + 1) * 60, 0, 0, "DUCT_20x10"));
        for (int i = 0; i < 4; i++) frames.Add(AddFrame(480, 0, i * 60, 480, 0, (i + 1) * 60, "DUCT_16x16"));
        foreach (string f in frames)
        {
            Check(M.FrameObj.SetGroupAssign(f, "DUCT_ALL", false, eItemType.Objects), "SetGroupAssign " + f);
            Check(M.FrameObj.SetLoadDistributed(f, "3 Seismic X", 1, 4, 0, 1, 0.004, 0.004, "Global", true, true, eItemType.Objects), "SetLoadDistributed");
            Check(M.FrameObj.SetLoadDistributed(f, "4 Seismic Z", 1, 6, 0, 1, 0.004, 0.004, "Global", true, true, eItemType.Objects), "SetLoadDistributed");
            Check(M.FrameObj.SetLoadDistributed(f, "5 Seismic Y - vert", 1, 5, 0, 1, 0.003, 0.003, "Global", true, true, eItemType.Objects), "SetLoadDistributed");
            Check(M.FrameObj.SetLoadTemperature(f, "7 Thermal", 1, 100, "", true, eItemType.Objects), "SetLoadTemperature");
        }
        Restrain(0, 0, 0, new bool[] { true, true, true, true, true, true });
        Restrain(240, 0, 0, new bool[] { false, true, true, false, false, false });
        Restrain(480, 0, 0, new bool[] { false, true, true, false, false, false });
        Restrain(480, 0, 240, new bool[] { true, true, true, false, false, false });
        double[] steel = { 0, 0, -0.5, 0, 0, 0 };
        foreach (double x in new double[] { 120, 360 }) Check(M.PointObj.SetLoadForce(JointAt(x, 0, 0), "Steel_Loading", ref steel, true, "Global", eItemType.Objects), "SetLoadForce");
        double[] steelY = { 0, 0.3, 0, 0, 0, 0 };
        Check(M.PointObj.SetLoadForce(JointAt(480, 0, 120), "Steel_Loading", ref steelY, true, "Global", eItemType.Objects), "SetLoadForce");

        Check(M.RespCombo.Add("8 SRSS", 3), "RespCombo.Add 8");
        foreach (string s in new string[] { "3 Seismic X", "4 Seismic Z", "5 Seismic Y - vert" }) AddToCombo("8 SRSS", eCNameType.LoadCase, s, 1);
        Check(M.RespCombo.Add("18 BLC 7B", 0), "RespCombo.Add 18");
        AddToCombo("18 BLC 7B", eCNameType.LoadCase, "1 DEAD", 1); AddToCombo("18 BLC 7B", eCNameType.LoadCase, "Steel_Loading", 1); AddToCombo("18 BLC 7B", eCNameType.LoadCombo, "8 SRSS", 1);
        Check(M.RespCombo.Add("19 BLC 7C", 0), "RespCombo.Add 19");
        AddToCombo("19 BLC 7C", eCNameType.LoadCase, "1 DEAD", 1); AddToCombo("19 BLC 7C", eCNameType.LoadCase, "Steel_Loading", 1); AddToCombo("19 BLC 7C", eCNameType.LoadCase, "7 Thermal", 1);
        Check(M.RespCombo.Add("20 BLC 7D", 0), "RespCombo.Add 20");
        AddToCombo("20 BLC 7D", eCNameType.LoadCase, "1 DEAD", 1); AddToCombo("20 BLC 7D", eCNameType.LoadCase, "Steel_Loading", 1); AddToCombo("20 BLC 7D", eCNameType.LoadCase, "7 Thermal", 0.7);
        Check(M.File.Save(savePath), "Save " + savePath);
        RunLinearStaticOnly();
        return F("test model {0}: {1} frames, candidates at x = 60..180, 300..420 (joints {2} {3}), riser z = 60..180 (joint {4})",
            savePath, frames.Count, JointAt(120, 0, 0), JointAt(360, 0, 0), JointAt(480, 0, 120));
    }

    string AddFrame(double xi, double yi, double zi, double xj, double yj, double zj, string sec)
    {
        string name = "";
        Check(M.FrameObj.AddByCoord(xi, yi, zi, xj, yj, zj, ref name, sec, "", "Global"), "AddByCoord");
        return name;
    }
    string JointAt(double x, double y, double z)
    {
        int n = 0; string[] names = null;
        M.PointObj.GetNameList(ref n, ref names);
        for (int i = 0; i < n; i++)
        {
            double a = 0, b = 0, c = 0;
            M.PointObj.GetCoordCartesian(names[i], ref a, ref b, ref c, "Global");
            if (Math.Abs(a - x) + Math.Abs(b - y) + Math.Abs(c - z) < 1e-6) return names[i];
        }
        throw new Exception(F("no joint at {0},{1},{2}", x, y, z));
    }
    void Restrain(double x, double y, double z, bool[] r) { Check(M.PointObj.SetRestraint(JointAt(x, y, z), ref r, eItemType.Objects), "SetRestraint"); }
    void AddToCombo(string combo, eCNameType t, string name, double sf) { Check(M.RespCombo.SetCaseList(combo, ref t, name, sf), "SetCaseList " + combo + " " + name); }

    static string R(double v) { return v.ToString("R", Inv); }
    static string G(double v) { return v.ToString("G6", Inv); }
    static string F(string fmt, params object[] args) { return string.Format(Inv, fmt, args); }
    static string Tab(params object[] cells)
    {
        string[] s = new string[cells.Length];
        for (int i = 0; i < cells.Length; i++) s[i] = Convert.ToString(cells[i], Inv);
        return string.Join("\t", s);
    }
    static void Check(int ret, string what) { if (ret != 0) throw new Exception(what + " returned " + ret); }
}