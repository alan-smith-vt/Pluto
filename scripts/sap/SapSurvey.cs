// Read-only survey of an open SAP2000 model, plus an axis probe on a known cantilever.
// C# 5, compiled by Add-Type under Windows PowerShell 5.1 against SAP2000v1.dll.
// Survey never edits, runs or saves the attached model. AxisProbe builds its own model.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using SAP2000v1;

public class SapSurvey
{
    const string ProgId = "CSI.SAP2000.API.SapObject";
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public cOAPI Sap;
    public cSapModel Model;
    public bool Started;
    public List<string> Warnings = new List<string>();

    public static SapSurvey AttachOrStart(string exePath, bool attachOnly)
    {
        SapSurvey s = new SapSurvey();
        try { s.Sap = (cOAPI)Marshal.GetActiveObject(ProgId); }
        catch (COMException)
        {
            if (attachOnly) throw new Exception("No running SAP2000 to attach to: open the model in SAP first.");
            cHelper helper = new Helper();
            s.Sap = helper.CreateObject(exePath);
            Check(s.Sap.ApplicationStart(), "ApplicationStart");
            s.Sap.Visible();
            s.Started = true;
        }
        s.Model = s.Sap.SapModel;
        return s;
    }

    // Open a saved model (PowerShell cannot reach SapModel.File itself). run = analyse it first,
    // which writes analysis files next to the model; the survey itself never needs that.
    public void OpenModel(string path, bool run)
    {
        Check(Model.File.OpenFile(path), "OpenFile " + path);
        if (run) Check(Model.Analyze.RunAnalysis(), "RunAnalysis");
    }

    public string Version() { string v = ""; double n = 0; Check(Model.GetVersion(ref v, ref n), "GetVersion"); return v; }

    // ---------------------------------------------------------------- survey

    // Writes summary.txt, materials.tsv, sections.tsv, frames.tsv, links.tsv, patterns.tsv,
    // cases.tsv, combos.tsv, groups.tsv, and (if results exist) forces-sample.tsv.
    public void Survey(string outDir, int sampleFrames)
    {
        Directory.CreateDirectory(outDir);
        // Report in kip-in so section dimensions match the Mathcad inputs directly.
        eUnits units = Model.GetPresentUnits();
        Check(Model.SetPresentUnits(eUnits.kip_in_F), "SetPresentUnits");
        try
        {
            StringBuilder sum = new StringBuilder();
            sum.AppendLine("SapSurvey " + DateTime.Now.ToString("yyyy-MM-dd HH:mm", Inv));
            sum.AppendLine("SAP version      " + Version());
            sum.AppendLine("model file       " + Path.GetFileName(Model.GetModelFilename(true)));
            sum.AppendLine("model units      " + units + "   (tables below are kip, in)");
            sum.AppendLine("model locked     " + Model.GetModelIsLocked() + "   (true = analysed results may exist)");

            sum.AppendLine(Materials(Path.Combine(outDir, "materials.tsv")));
            sum.AppendLine(Sections(Path.Combine(outDir, "sections.tsv")));
            sum.AppendLine(Frames(Path.Combine(outDir, "frames.tsv")));
            sum.AppendLine(Links(Path.Combine(outDir, "links.tsv")));
            sum.AppendLine(Joints());
            sum.AppendLine(Patterns(Path.Combine(outDir, "patterns.tsv")));
            sum.AppendLine(Cases(Path.Combine(outDir, "cases.tsv")));
            sum.AppendLine(Combos(Path.Combine(outDir, "combos.tsv")));
            sum.AppendLine(Groups(Path.Combine(outDir, "groups.tsv")));
            sum.AppendLine(ForceSample(Path.Combine(outDir, "forces-sample.tsv"), sampleFrames));
            sum.AppendLine();
            sum.AppendLine("warnings: " + Warnings.Count);
            foreach (string w in Warnings) sum.AppendLine("  " + w);
            File.WriteAllText(Path.Combine(outDir, "summary.txt"), sum.ToString());
        }
        finally { Model.SetPresentUnits(units); }
    }

    string Materials(string path)
    {
        int n = 0; string[] names = null;
        Model.PropMaterial.GetNameList(ref n, ref names);
        List<string> rows = new List<string>();
        rows.Add("Material\tType\tE_ksi\tmu\tAlpha\tFy_ksi\tFu_ksi");
        for (int i = 0; i < n; i++)
        {
            eMatType type = eMatType.Steel; int color = 0; string notes = "", guid = "";
            Model.PropMaterial.GetMaterial(names[i], ref type, ref color, ref notes, ref guid);
            double e = 0, u = 0, a = 0, temp = 0;
            string fy = "", fu = "";
            if (Model.PropMaterial.GetMPIsotropic(names[i], ref e, ref u, ref a, ref temp) != 0) Warn("GetMPIsotropic " + names[i]);
            if (type == eMatType.Steel)
            {
                double dfy = 0, dfu = 0, efy = 0, efu = 0, h = 0, sm = 0, sr = 0, fs = 0;
                int sst = 0, hys = 0;
                if (Model.PropMaterial.GetOSteel_1(names[i], ref dfy, ref dfu, ref efy, ref efu, ref sst, ref hys, ref h, ref sm, ref sr, ref fs, 0) == 0)
                { fy = G(dfy); fu = G(dfu); }
            }
            rows.Add(Tab(names[i], type, G(e), G(u), G(a), fy, fu));
        }
        Write(path, rows);
        return "materials        " + n;
    }

    string Sections(string path)
    {
        int n = 0; string[] names = null;
        Model.PropFrame.GetNameList(ref n, ref names);
        List<string> rows = new List<string>();
        rows.Add("Section\tType\tMaterial\tt3_in\tt2_in\ttf_in\ttw_in\tA_in2\tAs2_in2\tAs3_in2\tJ_in4\tI22_in4\tI33_in4\tS22_in3\tS33_in3\tr22_in\tr33_in");
        Dictionary<string, int> byType = new Dictionary<string, int>();
        for (int i = 0; i < n; i++)
        {
            eFramePropType type = eFramePropType.I;
            Model.PropFrame.GetTypeOAPI(names[i], ref type);
            string key = type.ToString();
            byType[key] = (byType.ContainsKey(key) ? byType[key] : 0) + 1;
            string mat = "";
            Model.PropFrame.GetMaterial(names[i], ref mat);
            string t3 = "", t2 = "", tf = "", tw = "";
            string file = "", m = "", notes = "", guid = ""; int color = 0;
            double d3 = 0, d2 = 0, df = 0, dw = 0;
            if (type == eFramePropType.Box && Model.PropFrame.GetTube(names[i], ref file, ref m, ref d3, ref d2, ref df, ref dw, ref color, ref notes, ref guid) == 0)
            { t3 = G(d3); t2 = G(d2); tf = G(df); tw = G(dw); }
            else if (type == eFramePropType.Rectangular && Model.PropFrame.GetRectangle(names[i], ref file, ref m, ref d3, ref d2, ref color, ref notes, ref guid) == 0)
            { t3 = G(d3); t2 = G(d2); }
            double A = 0, As2 = 0, As3 = 0, J = 0, I22 = 0, I33 = 0, S22 = 0, S33 = 0, Z22 = 0, Z33 = 0, r22 = 0, r33 = 0;
            if (Model.PropFrame.GetSectProps(names[i], ref A, ref As2, ref As3, ref J, ref I22, ref I33, ref S22, ref S33, ref Z22, ref Z33, ref r22, ref r33) != 0)
                Warn("GetSectProps " + names[i]);
            rows.Add(Tab(names[i], type, mat, t3, t2, tf, tw, G(A), G(As2), G(As3), G(J), G(I22), G(I33), G(S22), G(S33), G(r22), G(r33)));
        }
        Write(path, rows);
        List<string> parts = new List<string>();
        foreach (KeyValuePair<string, int> kv in byType) parts.Add(kv.Key + "=" + kv.Value);
        return "frame sections   " + n + "   by type: " + string.Join(", ", parts);
    }

    // One row per frame object: ends, length, section, local axes as global unit vectors, releases.
    string Frames(string path)
    {
        int n = 0; string[] names = null;
        Model.FrameObj.GetNameList(ref n, ref names);
        List<string> rows = new List<string>();
        rows.Add("Frame\tJointI\tJointJ\tLength_in\tSection\tAngle_deg\tAdvancedAxes\tL1x\tL1y\tL1z\tL2x\tL2y\tL2z\tL3x\tL3y\tL3z\tReleaseI\tReleaseJ");
        int advanced = 0, rotated = 0, released = 0, vertical = 0;
        for (int i = 0; i < n; i++)
        {
            string pi = "", pj = "";
            Model.FrameObj.GetPoints(names[i], ref pi, ref pj);
            double xi = 0, yi = 0, zi = 0, xj = 0, yj = 0, zj = 0;
            Model.PointObj.GetCoordCartesian(pi, ref xi, ref yi, ref zi, "Global");
            Model.PointObj.GetCoordCartesian(pj, ref xj, ref yj, ref zj, "Global");
            double len = Math.Sqrt((xj - xi) * (xj - xi) + (yj - yi) * (yj - yi) + (zj - zi) * (zj - zi));
            string sec = "", auto = "";
            Model.FrameObj.GetSection(names[i], ref sec, ref auto);
            double ang = 0; bool adv = false;
            Model.FrameObj.GetLocalAxes(names[i], ref ang, ref adv);
            if (adv) advanced++;
            if (Math.Abs(ang) > 1e-9) rotated++;
            // Transformation matrix is row-major (global = T * local): its COLUMNS are local 1/2/3 in global.
            double[] tm = new double[9];
            if (Model.FrameObj.GetTransformationMatrix(names[i], ref tm, true) != 0) Warn("GetTransformationMatrix " + names[i]);
            if (len > 0 && Math.Abs(zj - zi) / len > 0.999) vertical++;
            bool[] ii = new bool[6], jj = new bool[6]; double[] si = new double[6], sj = new double[6];
            Model.FrameObj.GetReleases(names[i], ref ii, ref jj, ref si, ref sj);
            string ri = Rel(ii), rj = Rel(jj);
            if (ri != "-" || rj != "-") released++;
            rows.Add(Tab(names[i], pi, pj, G(len), sec, G(ang), adv,
                G(tm[0]), G(tm[3]), G(tm[6]), G(tm[1]), G(tm[4]), G(tm[7]), G(tm[2]), G(tm[5]), G(tm[8]), ri, rj));
        }
        Write(path, rows);
        return F("frames           {0}   angle<>0: {1}   advanced axes: {2}   with end releases: {3}   vertical: {4}", n, rotated, advanced, released, vertical);
    }

    static string Rel(bool[] r)
    {
        string[] dof = { "P", "V2", "V3", "T", "M2", "M3" };
        List<string> on = new List<string>();
        for (int k = 0; k < 6 && k < r.Length; k++) if (r[k]) on.Add(dof[k]);
        return on.Count == 0 ? "-" : string.Join("+", on);
    }

    string Links(string path)
    {
        int n = 0; string[] names = null;
        Model.LinkObj.GetNameList(ref n, ref names);
        List<string> rows = new List<string>();
        rows.Add("Link\tJointI\tJointJ\tProperty");
        Dictionary<string, int> byProp = new Dictionary<string, int>();
        for (int i = 0; i < n; i++)
        {
            string pi = "", pj = "", prop = "";
            Model.LinkObj.GetPoints(names[i], ref pi, ref pj);
            Model.LinkObj.GetProperty(names[i], ref prop);
            byProp[prop] = (byProp.ContainsKey(prop) ? byProp[prop] : 0) + 1;
            rows.Add(Tab(names[i], pi, pj, prop));
        }
        Write(path, rows);
        List<string> parts = new List<string>();
        foreach (KeyValuePair<string, int> kv in byProp) parts.Add(kv.Key + "=" + kv.Value);
        return "links            " + n + (parts.Count > 0 ? "   by property: " + string.Join(", ", parts) : "");
    }

    string Joints()
    {
        int n = 0; string[] names = null;
        Model.PointObj.GetNameList(ref n, ref names);
        int restrained = 0, springs = 0, constrained = 0;
        for (int i = 0; i < n; i++)
        {
            bool[] r = new bool[6];
            Model.PointObj.GetRestraint(names[i], ref r);
            foreach (bool b in r) if (b) { restrained++; break; }
            double[] k = new double[6];
            if (Model.PointObj.GetSpring(names[i], ref k) == 0) foreach (double v in k) if (v != 0) { springs++; break; }
            int nc = 0; string[] pn = null, cn = null;
            if (Model.PointObj.GetConstraint(names[i], ref nc, ref pn, ref cn, eItemType.Objects) == 0 && nc > 0) constrained++;
        }
        return F("joints           {0}   restrained: {1}   with springs: {2}   in constraints: {3}", n, restrained, springs, constrained);
    }

    string Patterns(string path)
    {
        int n = 0; string[] names = null;
        Model.LoadPatterns.GetNameList(ref n, ref names);
        List<string> rows = new List<string>();
        rows.Add("Pattern\tType\tSelfWtMult");
        for (int i = 0; i < n; i++)
        {
            eLoadPatternType t = eLoadPatternType.Dead; double sw = 0;
            Model.LoadPatterns.GetLoadType(names[i], ref t);
            Model.LoadPatterns.GetSelfWTMultiplier(names[i], ref sw);
            rows.Add(Tab(names[i], t, G(sw)));
        }
        Write(path, rows);
        return "load patterns    " + n;
    }

    string Cases(string path)
    {
        int n = 0; string[] names = null;
        Model.LoadCases.GetNameList(ref n, ref names);
        List<string> rows = new List<string>();
        rows.Add("Case\tType\tSubType\tRunFlag");
        int nonlinear = 0;
        for (int i = 0; i < n; i++)
        {
            eLoadCaseType t = eLoadCaseType.LinearStatic; int sub = 0;
            Model.LoadCases.GetTypeOAPI(names[i], ref t, ref sub);
            string tn = t.ToString();
            if (tn.IndexOf("Nonlinear", StringComparison.OrdinalIgnoreCase) >= 0) nonlinear++;
            rows.Add(Tab(names[i], tn, sub, ""));
        }
        Write(path, rows);
        return "load cases       " + n + "   nonlinear: " + nonlinear;
    }

    string Combos(string path)
    {
        int n = 0; string[] names = null;
        Model.RespCombo.GetNameList(ref n, ref names);
        List<string> rows = new List<string>();
        rows.Add("Combo\tComboType\tItemType\tItem\tScale");
        string[] ctype = { "LinearAdditive", "Envelope", "AbsoluteAdditive", "SRSS", "RangeAdditive" };
        for (int i = 0; i < n; i++)
        {
            int t = 0;
            Model.RespCombo.GetTypeOAPI(names[i], ref t);
            int m = 0; eCNameType[] it = null; string[] items = null; double[] sf = null;
            Model.RespCombo.GetCaseList(names[i], ref m, ref it, ref items, ref sf);
            string tn = t >= 0 && t < ctype.Length ? ctype[t] : t.ToString(Inv);
            if (m == 0) rows.Add(Tab(names[i], tn, "", "", ""));
            for (int k = 0; k < m; k++) rows.Add(Tab(names[i], tn, it[k], items[k], G(sf[k])));
        }
        Write(path, rows);
        return "combinations     " + n;
    }

    string Groups(string path)
    {
        int n = 0; string[] names = null;
        Model.GroupDef.GetNameList(ref n, ref names);
        List<string> rows = new List<string>();
        rows.Add("Group\tFrames\tJoints\tLinks");
        for (int i = 0; i < n; i++)
        {
            int m = 0; int[] types = null; string[] objs = null;
            Model.GroupDef.GetAssignments(names[i], ref m, ref types, ref objs);
            int fr = 0, jo = 0, li = 0;
            for (int k = 0; k < m; k++) { if (types[k] == 2) fr++; else if (types[k] == 1) jo++; else if (types[k] == 7) li++; }
            rows.Add(Tab(names[i], fr, jo, li));
        }
        Write(path, rows);
        return "groups           " + n;
    }

    // Frame forces for the first few frames, every case and combo, if the model has results.
    string ForceSample(string path, int sampleFrames)
    {
        int n = 0; string[] frames = null;
        Model.FrameObj.GetNameList(ref n, ref frames);
        if (!Model.GetModelIsLocked() || n == 0 || sampleFrames <= 0) return "force sample     skipped (model not analysed or -SampleFrames 0)";
        Model.Results.Setup.DeselectAllCasesAndCombosForOutput();
        int nc = 0; string[] cases = null;
        Model.LoadCases.GetNameList(ref nc, ref cases);
        for (int i = 0; i < nc; i++) Model.Results.Setup.SetCaseSelectedForOutput(cases[i]);
        int nb = 0; string[] combos = null;
        Model.RespCombo.GetNameList(ref nb, ref combos);
        for (int i = 0; i < nb; i++) Model.Results.Setup.SetComboSelectedForOutput(combos[i]);
        List<string> rows = new List<string>();
        rows.Add("Frame\tStation_in\tOutputCase\tStepType\tP_kip\tV2_kip\tV3_kip\tT_kipin\tM2_kipin\tM3_kipin");
        for (int f = 0; f < Math.Min(sampleFrames, n); f++)
        {
            int r = 0;
            string[] obj = null, elm = null, cas = null, step = null;
            double[] objSta = null, elmSta = null, stepNum = null, p = null, v2 = null, v3 = null, t = null, m2 = null, m3 = null;
            if (Model.Results.FrameForce(frames[f], eItemTypeElm.ObjectElm, ref r, ref obj, ref objSta, ref elm, ref elmSta,
                  ref cas, ref step, ref stepNum, ref p, ref v2, ref v3, ref t, ref m2, ref m3) != 0) { Warn("FrameForce " + frames[f]); continue; }
            for (int i = 0; i < r; i++)
                rows.Add(Tab(obj[i], G(objSta[i]), cas[i], step[i], G(p[i]), G(v2[i]), G(v3[i]), G(t[i]), G(m2[i]), G(m3[i])));
        }
        Write(path, rows);
        return "force sample     " + Math.Min(sampleFrames, n) + " frames, " + (rows.Count - 1) + " rows";
    }

    // ---------------------------------------------------------------- forces export

    // Frame forces of every frame in `group` for every response combination (and load case if includeCases),
    // kip-in, one row per frame / station / output case / step -> forces.tsv, the input of Run-DuctDcr.ps1 -Evaluate.
    // Reads existing results only: the model must already be analysed.
    public string ExportForces(string outDir, string group, bool includeCases)
    {
        Directory.CreateDirectory(outDir);
        if (!Model.GetModelIsLocked()) throw new Exception("model has no results: run the analysis in SAP first");
        eUnits units = Model.GetPresentUnits();
        Check(Model.SetPresentUnits(eUnits.kip_in_F), "SetPresentUnits");
        try
        {
            int m = 0; int[] types = null; string[] objs = null;
            Check(Model.GroupDef.GetAssignments(group, ref m, ref types, ref objs), "GroupDef.GetAssignments " + group);
            List<string> frames = new List<string>();
            for (int k = 0; k < m; k++) if (types[k] == 2) frames.Add(objs[k]);
            if (frames.Count == 0) throw new Exception("group " + group + " has no frames");

            Check(Model.Results.Setup.DeselectAllCasesAndCombosForOutput(), "Deselect");
            int nb = 0; string[] combos = null;
            Model.RespCombo.GetNameList(ref nb, ref combos);
            for (int i = 0; i < nb; i++) Model.Results.Setup.SetComboSelectedForOutput(combos[i]);
            int nc = 0; string[] cases = null;
            if (includeCases)
            {
                Model.LoadCases.GetNameList(ref nc, ref cases);
                for (int i = 0; i < nc; i++) Model.Results.Setup.SetCaseSelectedForOutput(cases[i]);
            }

            int rows = 0;
            string path = Path.Combine(outDir, "forces.tsv");
            using (StreamWriter w = new StreamWriter(path))
            {
                w.Write("Frame\tSection\tStation_in\tOutputCase\tStepType\tP_kip\tV2_kip\tV3_kip\tT_kipin\tM2_kipin\tM3_kipin\r\n");
                foreach (string fr in frames)
                {
                    string sec = "", auto = "";
                    Model.FrameObj.GetSection(fr, ref sec, ref auto);
                    int r = 0;
                    string[] obj = null, elm = null, cas = null, step = null;
                    double[] objSta = null, elmSta = null, stepNum = null, p = null, v2 = null, v3 = null, t = null, m2 = null, m3 = null;
                    if (Model.Results.FrameForce(fr, eItemTypeElm.ObjectElm, ref r, ref obj, ref objSta, ref elm, ref elmSta,
                          ref cas, ref step, ref stepNum, ref p, ref v2, ref v3, ref t, ref m2, ref m3) != 0) { Warn("FrameForce " + fr); continue; }
                    for (int i = 0; i < r; i++)
                        w.Write(Tab(fr, sec, R(objSta[i]), cas[i], step[i], R(p[i]), R(v2[i]), R(v3[i]), R(t[i]), R(m2[i]), R(m3[i])) + "\r\n");
                    rows += r;
                }
            }
            string msg = F("forces.tsv: group {0}, {1} frames, {2} combos{3}, {4} rows", group, frames.Count, nb, includeCases ? F(" + {0} cases", nc) : "", rows);
            if (Warnings.Count > 0) msg += "\r\nwarnings: " + string.Join("; ", Warnings.ToArray());
            return msg;
        }
        finally { Model.SetPresentUnits(units); }
    }

    static string R(double v) { return v.ToString("R", Inv); }   // round-trip precision for force export

    // ---------------------------------------------------------------- candidate joints

    // First-pass expansion-joint candidates from connectivity alone. Classifies every joint on a
    // frame of `group`:
    //   end      1 group frame
    //   tee      3+ group frames
    //   support  restrained, or a link, or touched by non-group frames that lead to a restraint (support steel,
    //            "lollipops"). Sub-classed by what it restrains at the duct joint (column Support): the lollipop's
    //            end releases at the duct end are read, its unreleased local DOFs (+ joint restraints) give the
    //            restrained directions. anchor = 3 translations + 3 rotations, pinned = 3 translations,
    //            guide = fewer than 3 translations (a direction left free). Links count as anchors.
    //   attach   touched only by non-group frames that reach no restraint (mass stubs, hangers-on);
    //            brace if that non-group piece touches 2+ group joints. Excluded, but does not split spans.
    //   elbow    2 group frames meeting at more than angleTolDeg
    //   inline   2 group frames, straight within angleTolDeg  -> candidate
    // Spans: group frames joined through non-support joints and guides; a span is one piece between anchors /
    // pinned supports (a guide does not stop movement along its free direction, so it does not end a span).
    // Writes duct-joints.tsv (every group joint), candidates.tsv (inline, not loaded), spans.tsv.
    public string Candidates(string outDir, string group, double angleTolDeg)
    {
        Directory.CreateDirectory(outDir);
        eUnits units = Model.GetPresentUnits();
        Check(Model.SetPresentUnits(eUnits.kip_in_F), "SetPresentUnits");
        try { return CandidatesIn(outDir, group, angleTolDeg); }
        finally { Model.SetPresentUnits(units); }
    }

    class JointInfo
    {
        public string Name;
        public double X, Y, Z;
        public List<int> Duct = new List<int>();   // indices into the group frame list
        public int Other, Links;
        public bool OtherGrounded, OtherBrace;
        public string OtherSections = "", GroundedVia = "";
        public bool Restrained, Loaded;
        public string Class = "", Sections = "";
        public double Angle;
        public int Span = -1;
        public List<double[]> TDirs = new List<double[]>(), RDirs = new List<double[]>();   // restrained directions (global unit vectors)
        public string Support = "", Restrains = "";
        public bool SplitsSpan { get { return Class == "support" && Support != "guide"; } }
    }

    string CandidatesIn(string outDir, string group, double angleTolDeg)
    {
        int m = 0; int[] types = null; string[] objs = null;
        Check(Model.GroupDef.GetAssignments(group, ref m, ref types, ref objs), "GroupDef.GetAssignments " + group);
        HashSet<string> inGroup = new HashSet<string>();
        for (int k = 0; k < m; k++) if (types[k] == 2) inGroup.Add(objs[k]);
        if (inGroup.Count == 0) throw new Exception("group " + group + " has no frames");

        int n = 0; string[] frames = null;
        Model.FrameObj.GetNameList(ref n, ref frames);
        List<string> gFrame = new List<string>(), gI = new List<string>(), gJ = new List<string>(), gSec = new List<string>();
        Dictionary<string, JointInfo> joints = new Dictionary<string, JointInfo>();
        List<string> otherI = new List<string>(), otherJ = new List<string>(), otherSec = new List<string>(), frameNameOther = new List<string>();
        for (int i = 0; i < n; i++)
        {
            string pi = "", pj = "";
            Model.FrameObj.GetPoints(frames[i], ref pi, ref pj);
            string sec = "", auto = "";
            Model.FrameObj.GetSection(frames[i], ref sec, ref auto);
            if (!inGroup.Contains(frames[i])) { otherI.Add(pi); otherJ.Add(pj); otherSec.Add(sec); frameNameOther.Add(frames[i]); continue; }
            int idx = gFrame.Count;
            gFrame.Add(frames[i]); gI.Add(pi); gJ.Add(pj); gSec.Add(sec);
            foreach (string p in new string[] { pi, pj })
            {
                JointInfo ji;
                if (!joints.TryGetValue(p, out ji)) { ji = new JointInfo(); ji.Name = p; joints[p] = ji; }
                ji.Duct.Add(idx);
            }
        }
        // Non-group frames: connected pieces over their joints, never through a group joint (each frame end
        // at a group joint is its own node), so support found through the duct itself does not count.
        // A piece is grounded if one of its own (non-group) joints is restrained.
        Dictionary<string, int> oj = new Dictionary<string, int>();
        string[] nodeI = new string[otherI.Count], nodeJ = new string[otherI.Count];
        for (int i = 0; i < otherI.Count; i++)
        {
            nodeI[i] = joints.ContainsKey(otherI[i]) ? otherI[i] + "#" + i + "#I" : otherI[i];
            nodeJ[i] = joints.ContainsKey(otherJ[i]) ? otherJ[i] + "#" + i + "#J" : otherJ[i];
            if (!oj.ContainsKey(nodeI[i])) oj[nodeI[i]] = oj.Count;
            if (!oj.ContainsKey(nodeJ[i])) oj[nodeJ[i]] = oj.Count;
        }
        int[] op = new int[oj.Count];
        for (int i = 0; i < op.Length; i++) op[i] = i;
        for (int i = 0; i < otherI.Count; i++) Union(op, oj[nodeI[i]], oj[nodeJ[i]]);
        HashSet<int> grounded = new HashSet<int>();
        Dictionary<int, string> groundJoint = new Dictionary<int, string>();   // one restrained joint per grounded piece, with its DOFs
        foreach (KeyValuePair<string, int> kv in oj)
        {
            if (kv.Key.IndexOf('#') >= 0) continue;
            bool[] r = new bool[6];
            Model.PointObj.GetRestraint(kv.Key, ref r);
            string dofs = Rel(r).Replace("P", "U1").Replace("V2", "U2").Replace("V3", "U3").Replace("T", "R1").Replace("M2", "R2").Replace("M3", "R3");
            if (dofs == "-") continue;
            int root = Find(op, kv.Value);
            grounded.Add(root);
            if (!groundJoint.ContainsKey(root)) groundJoint[root] = kv.Key + "[" + dofs + "]";
        }
        Dictionary<int, int> pieceFrames = new Dictionary<int, int>();
        for (int i = 0; i < otherI.Count; i++)
        {
            int root = Find(op, oj[nodeI[i]]);
            pieceFrames[root] = (pieceFrames.ContainsKey(root) ? pieceFrames[root] : 0) + 1;
        }
        Dictionary<int, HashSet<string>> ductTouches = new Dictionary<int, HashSet<string>>();
        for (int i = 0; i < otherI.Count; i++)
        {
            int root = Find(op, oj[nodeI[i]]);
            if (!ductTouches.ContainsKey(root)) ductTouches[root] = new HashSet<string>();
            if (joints.ContainsKey(otherI[i])) ductTouches[root].Add(otherI[i]);
            if (joints.ContainsKey(otherJ[i])) ductTouches[root].Add(otherJ[i]);
        }
        for (int i = 0; i < otherI.Count; i++)
            for (int e = 0; e < 2; e++)
            {
                string p = e == 0 ? otherI[i] : otherJ[i];
                JointInfo ji;
                if (!joints.TryGetValue(p, out ji)) continue;
                int root = Find(op, oj[e == 0 ? nodeI[i] : nodeJ[i]]);
                bool g = grounded.Contains(root);
                ji.Other++;
                if (g)
                {
                    ji.OtherGrounded = true;
                    // what this lollipop holds at the duct joint: its unreleased local DOFs at this end
                    bool[] ri = new bool[6], rj = new bool[6]; double[] si = new double[6], sj = new double[6];
                    Model.FrameObj.GetReleases(frameNameOther[i], ref ri, ref rj, ref si, ref sj);
                    bool[] rel = e == 0 ? ri : rj;
                    double[] tm = new double[9];
                    if (Model.FrameObj.GetTransformationMatrix(frameNameOther[i], ref tm, true) != 0) Warn("GetTransformationMatrix " + frameNameOther[i]);
                    for (int k = 0; k < 3; k++)
                    {
                        double[] axis = { tm[k], tm[3 + k], tm[6 + k] };   // column k = local k+1 in global
                        if (!rel[k]) ji.TDirs.Add(axis);
                        if (!rel[3 + k]) ji.RDirs.Add(axis);
                    }
                }
                if (ductTouches[root].Count >= 2) ji.OtherBrace = true;
                string tag = otherSec[i] + (g ? "(grounded)" : ductTouches[root].Count >= 2 ? "(brace)" : "(free)");
                if (g)
                {
                    string via = otherSec[i] + ": frame " + frameNameOther[i] + " -> restrained joint " + groundJoint[root] + " (" + pieceFrames[root] + " frames in piece)";
                    if (ji.GroundedVia.IndexOf(via, StringComparison.Ordinal) < 0) ji.GroundedVia = ji.GroundedVia.Length == 0 ? via : ji.GroundedVia + " | " + via;
                }
                if (("|" + ji.OtherSections + "|").IndexOf("|" + tag + "|", StringComparison.Ordinal) < 0)
                    ji.OtherSections = ji.OtherSections.Length == 0 ? tag : ji.OtherSections + "|" + tag;
            }
        int nl = 0; string[] links = null;
        Model.LinkObj.GetNameList(ref nl, ref links);
        for (int i = 0; i < nl; i++)
        {
            string pi = "", pj = "";
            Model.LinkObj.GetPoints(links[i], ref pi, ref pj);
            JointInfo ji;
            if (joints.TryGetValue(pi, out ji)) ji.Links++;
            if (pj != pi && joints.TryGetValue(pj, out ji)) ji.Links++;
        }

        foreach (JointInfo ji in joints.Values)
        {
            Model.PointObj.GetCoordCartesian(ji.Name, ref ji.X, ref ji.Y, ref ji.Z, "Global");
            bool[] r = new bool[6];
            Model.PointObj.GetRestraint(ji.Name, ref r);
            foreach (bool b in r) if (b) ji.Restrained = true;
            for (int k = 0; k < 3; k++)
            {
                double[] axis = { k == 0 ? 1 : 0, k == 1 ? 1 : 0, k == 2 ? 1 : 0 };
                if (r[k]) ji.TDirs.Add(axis);
                if (r[3 + k]) ji.RDirs.Add(axis);
            }
            int nf = 0; string[] pn = null, lp = null, cs = null; int[] st = null;
            double[] f1 = null, f2 = null, f3 = null, m1 = null, m2 = null, m3 = null;
            if (Model.PointObj.GetLoadForce(ji.Name, ref nf, ref pn, ref lp, ref st, ref cs, ref f1, ref f2, ref f3, ref m1, ref m2, ref m3, eItemType.Objects) == 0 && nf > 0)
                ji.Loaded = true;
        }

        // Angle between the two frames at degree-2 joints, and the sections either side.
        foreach (JointInfo ji in joints.Values)
        {
            List<string> secs = new List<string>();
            foreach (int f in ji.Duct) if (!secs.Contains(gSec[f])) secs.Add(gSec[f]);
            ji.Sections = string.Join("|", secs.ToArray());
            if (ji.Duct.Count == 2)
            {
                double[] u = Away(joints, gI, gJ, ji, ji.Duct[0]), v = Away(joints, gI, gJ, ji, ji.Duct[1]);
                double dot = -(u[0] * v[0] + u[1] * v[1] + u[2] * v[2]);   // straight run: the two away-vectors are opposite
                ji.Angle = Math.Acos(Math.Max(-1, Math.Min(1, dot))) * 180 / Math.PI;
            }
            if (ji.Restrained || ji.OtherGrounded || ji.Links > 0)
            {
                ji.Class = "support";
                List<double[]> tb = Basis(ji.TDirs), rb = Basis(ji.RDirs);
                if (ji.Links > 0) { ji.Support = "anchor"; ji.Restrains = "link (taken as fixed)"; }
                else
                {
                    ji.Support = tb.Count < 3 ? "guide" : rb.Count < 3 ? "pinned" : "anchor";
                    ji.Restrains = "U " + Held(tb) + "; R " + Held(rb);
                }
            }
            else if (ji.Other > 0) ji.Class = ji.OtherBrace ? "brace" : "attach";
            else if (ji.Duct.Count == 1) ji.Class = "end";
            else if (ji.Duct.Count >= 3) ji.Class = "tee";
            else if (ji.Angle > angleTolDeg) ji.Class = "elbow";
            else ji.Class = "inline";
        }

        // Spans: union group frames across every non-support joint.
        int[] parent = new int[gFrame.Count];
        for (int i = 0; i < parent.Length; i++) parent[i] = i;
        foreach (JointInfo ji in joints.Values)
            if (!ji.SplitsSpan)
                for (int k = 1; k < ji.Duct.Count; k++) Union(parent, ji.Duct[0], ji.Duct[k]);
        Dictionary<int, int> spanId = new Dictionary<int, int>();
        int[] frameSpan = new int[gFrame.Count];
        for (int i = 0; i < gFrame.Count; i++)
        {
            int root = Find(parent, i);
            if (!spanId.ContainsKey(root)) spanId[root] = spanId.Count + 1;
            frameSpan[i] = spanId[root];
        }
        int ns = spanId.Count;
        int[] spFrames = new int[ns + 1], spInline = new int[ns + 1], spElbow = new int[ns + 1], spTee = new int[ns + 1], spEnd = new int[ns + 1], spSupport = new int[ns + 1], spGuide = new int[ns + 1], spAttach = new int[ns + 1];
        double[] spLen = new double[ns + 1];
        for (int i = 0; i < gFrame.Count; i++)
        {
            spFrames[frameSpan[i]]++;
            JointInfo a = joints[gI[i]], b = joints[gJ[i]];
            spLen[frameSpan[i]] += Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));
        }
        foreach (JointInfo ji in joints.Values)
        {
            if (ji.SplitsSpan)
            {
                HashSet<int> touched = new HashSet<int>();
                foreach (int f in ji.Duct) touched.Add(frameSpan[f]);
                foreach (int s in touched) spSupport[s]++;
                continue;
            }
            ji.Span = frameSpan[ji.Duct[0]];
            if (ji.Class == "support") { spGuide[ji.Span]++; continue; }
            if (ji.Class == "inline") spInline[ji.Span]++;
            else if (ji.Class == "elbow") spElbow[ji.Span]++;
            else if (ji.Class == "tee") spTee[ji.Span]++;
            else if (ji.Class == "end") spEnd[ji.Span]++;
            else spAttach[ji.Span]++;
        }

        List<JointInfo> sorted = new List<JointInfo>(joints.Values);
        sorted.Sort(delegate(JointInfo p, JointInfo q) { return p.Span != q.Span ? p.Span.CompareTo(q.Span) : string.CompareOrdinal(p.Name, q.Name); });
        List<string> all = new List<string>(), cand = new List<string>();
        string head = "Joint\tClass\tSpan\tX_in\tY_in\tZ_in\tDuctFrames\tOtherFrames\tLinks\tRestrained\tJointLoad\tAngle_deg\tSections\tOtherSections\tGroundedVia\tSupport\tRestrains";
        all.Add(head); cand.Add(head);
        string[] classes = { "inline", "elbow", "tee", "end", "support", "attach", "brace" };
        int[] count = new int[classes.Length];
        int anchors = 0, pinned = 0, guides = 0;
        foreach (JointInfo ji in sorted)
        {
            string row = Tab(ji.Name, ji.Class, ji.Span < 0 ? "" : ji.Span.ToString(Inv), G(ji.X), G(ji.Y), G(ji.Z), ji.Duct.Count, ji.Other, ji.Links,
                ji.Restrained, ji.Loaded, ji.Duct.Count == 2 ? ji.Angle.ToString("0.#", Inv) : "", ji.Sections, ji.OtherSections, ji.GroundedVia, ji.Support, ji.Restrains);
            all.Add(row);
            if (ji.Class == "inline" && !ji.Loaded) cand.Add(row);
            count[Array.IndexOf(classes, ji.Class)]++;
            if (ji.Support == "anchor") anchors++; else if (ji.Support == "pinned") pinned++; else if (ji.Support == "guide") guides++;
        }
        Write(Path.Combine(outDir, "duct-joints.tsv"), all);
        Write(Path.Combine(outDir, "candidates.tsv"), cand);

        List<string> sp = new List<string>();
        sp.Add("Span\tFrames\tLength_ft\tSupportJoints\tGuides\tInline\tElbows\tTees\tEnds\tAttachOrBrace");
        int free = 0, oneSupport = 0;
        for (int s = 1; s <= ns; s++)
        {
            sp.Add(Tab(s, spFrames[s], G(spLen[s] / 12), spSupport[s], spGuide[s], spInline[s], spElbow[s], spTee[s], spEnd[s], spAttach[s]));
            if (spSupport[s] == 0) free++; else if (spSupport[s] == 1) oneSupport++;
        }
        Write(Path.Combine(outDir, "spans.tsv"), sp);

        StringBuilder sb = new StringBuilder();
        sb.AppendLine(F("candidates for group {0}: {1} frames, {2} joints   (angle tolerance {3} deg)", group, gFrame.Count, joints.Count, angleTolDeg));
        sb.AppendLine(F("  inline {0}   elbow {1}   tee {2}   end {3}   support {4}   attach {5}   brace {6}", count[0], count[1], count[2], count[3], count[4], count[5], count[6]));
        sb.AppendLine(F("  supports: anchor {0}   pinned {1}   guide {2}   (spans end at anchors and pinned supports only)", anchors, pinned, guides));
        sb.AppendLine(F("  candidates.tsv: {0} (inline, no joint load)", cand.Count - 1));
        sb.AppendLine(F("  spans {0}   with no support joint {1}   with one support joint {2}", ns, free, oneSupport));
        File.WriteAllText(Path.Combine(outDir, "candidates-summary.txt"), sb.ToString());
        return sb.ToString();
    }

    // Unit vector from joint ji along group frame f.
    static double[] Away(Dictionary<string, JointInfo> joints, List<string> gI, List<string> gJ, JointInfo ji, int f)
    {
        JointInfo o = joints[gI[f] == ji.Name ? gJ[f] : gI[f]];
        double dx = o.X - ji.X, dy = o.Y - ji.Y, dz = o.Z - ji.Z, l = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        return l > 0 ? new double[] { dx / l, dy / l, dz / l } : new double[] { 0, 0, 0 };
    }

    // Orthonormal basis of the span of dirs (Gram-Schmidt; a direction within ~3 deg of the span adds nothing).
    static List<double[]> Basis(List<double[]> dirs)
    {
        List<double[]> b = new List<double[]>();
        foreach (double[] d in dirs)
        {
            double[] v = (double[])d.Clone();
            foreach (double[] u in b) { double dot = v[0] * u[0] + v[1] * u[1] + v[2] * u[2]; for (int k = 0; k < 3; k++) v[k] -= dot * u[k]; }
            double len = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
            if (len > 0.05) { for (int k = 0; k < 3; k++) v[k] /= len; b.Add(v); }
            if (b.Count == 3) break;
        }
        return b;
    }
    // "all", "none", "X" (the one held direction) or "all but Z" (one free direction).
    static string Held(List<double[]> b)
    {
        if (b.Count == 3) return "all";
        if (b.Count == 0) return "none";
        if (b.Count == 1) return Dir(b[0]);
        double[] u = b[0], v = b[1];
        return "all but " + Dir(new double[] { u[1] * v[2] - u[2] * v[1], u[2] * v[0] - u[0] * v[2], u[0] * v[1] - u[1] * v[0] });
    }
    static string Dir(double[] v)
    {
        string[] ax = { "X", "Y", "Z" };
        for (int k = 0; k < 3; k++) if (Math.Abs(v[k]) > 0.995) return ax[k];
        return F("({0:0.##},{1:0.##},{2:0.##})", v[0], v[1], v[2]);
    }

    static int Find(int[] p, int i) { while (p[i] != i) { p[i] = p[p[i]]; i = p[i]; } return i; }
    static void Union(int[] p, int a, int b) { a = Find(p, a); b = Find(p, b); if (a != b) p[a] = b; }

    // ---------------------------------------------------------------- axis probe

    // Two 120 in cantilevers along global X, fixed at x = 0, Box/Tube 24 in (t3) x 12 in (t2), 0.25 in walls.
    // Frame 1: default local axes. Frame 2: local axis angle 90. Four load patterns at each tip:
    // AX = +10 kip global X, GY = +1 kip global Y, GZ = +1 kip global Z, TX = +10 kip-in about global X.
    // Output: fixed-end forces per frame and case, tip deflection vs P L^3 / 3 E I22 and I33.
    public static void WriteProbeS2k(string path, string version)
    {
        List<string> L = new List<string>();
        L.Add("File generated by SapSurvey.cs (Pluto) - axis probe"); L.Add("");
        L.Add("TABLE:  \"PROGRAM CONTROL\"");
        L.Add("   ProgramName=SAP2000   Version=" + version + "   CurrUnits=\"Kip, in, F\"   MergeTol=0.001"); L.Add("");
        L.Add("TABLE:  \"MATERIAL PROPERTIES 01 - GENERAL\"");
        L.Add("   Material=STEEL   Type=Steel   SymType=Isotropic   TempDepend=No   Color=Cyan"); L.Add("");
        L.Add("TABLE:  \"MATERIAL PROPERTIES 02 - BASIC MECHANICAL PROPERTIES\"");
        L.Add("   Material=STEEL   UnitWeight=0   UnitMass=0   E1=29000   G12=11153.85   U12=0.3   A1=6.5e-06"); L.Add("");
        L.Add("TABLE:  \"FRAME SECTION PROPERTIES 01 - GENERAL\"");
        L.Add("   SectionName=DUCT   Material=STEEL   Shape=Box/Tube   t3=24   t2=12   tf=0.25   tw=0.25   Color=Yellow"); L.Add("");
        L.Add("TABLE:  \"JOINT COORDINATES\"");
        L.Add("   Joint=1   CoordSys=GLOBAL   CoordType=Cartesian   XorR=0     Y=0     Z=0");
        L.Add("   Joint=2   CoordSys=GLOBAL   CoordType=Cartesian   XorR=120   Y=0     Z=0");
        L.Add("   Joint=3   CoordSys=GLOBAL   CoordType=Cartesian   XorR=0     Y=100   Z=0");
        L.Add("   Joint=4   CoordSys=GLOBAL   CoordType=Cartesian   XorR=120   Y=100   Z=0"); L.Add("");
        L.Add("TABLE:  \"CONNECTIVITY - FRAME\"");
        L.Add("   Frame=1   JointI=1   JointJ=2   IsCurved=No");
        L.Add("   Frame=2   JointI=3   JointJ=4   IsCurved=No"); L.Add("");
        L.Add("TABLE:  \"FRAME SECTION ASSIGNMENTS\"");
        L.Add("   Frame=1   SectionType=Box/Tube   AutoSelect=N.A.   AnalSect=DUCT   DesignSect=DUCT   MatProp=Default");
        L.Add("   Frame=2   SectionType=Box/Tube   AutoSelect=N.A.   AnalSect=DUCT   DesignSect=DUCT   MatProp=Default"); L.Add("");
        L.Add("TABLE:  \"FRAME LOCAL AXES ASSIGNMENTS 1 - TYPICAL\"");
        L.Add("   Frame=2   Angle=90   AdvanceAxes=No"); L.Add("");
        L.Add("TABLE:  \"JOINT RESTRAINT ASSIGNMENTS\"");
        L.Add("   Joint=1   U1=Yes   U2=Yes   U3=Yes   R1=Yes   R2=Yes   R3=Yes");
        L.Add("   Joint=3   U1=Yes   U2=Yes   U3=Yes   R1=Yes   R2=Yes   R3=Yes"); L.Add("");
        L.Add("TABLE:  \"LOAD PATTERN DEFINITIONS\"");
        foreach (string p in new string[] { "AX", "GY", "GZ", "TX" })
            L.Add("   LoadPat=" + p + "   DesignType=Other   SelfWtMult=0");
        L.Add("");
        L.Add("TABLE:  \"JOINT LOADS - FORCE\"");
        foreach (string j in new string[] { "2", "4" })
        {
            L.Add("   Joint=" + j + "   LoadPat=AX   CoordSys=GLOBAL   F1=10   F2=0   F3=0   M1=0    M2=0   M3=0");
            L.Add("   Joint=" + j + "   LoadPat=GY   CoordSys=GLOBAL   F1=0    F2=1   F3=0   M1=0    M2=0   M3=0");
            L.Add("   Joint=" + j + "   LoadPat=GZ   CoordSys=GLOBAL   F1=0    F2=0   F3=1   M1=0    M2=0   M3=0");
            L.Add("   Joint=" + j + "   LoadPat=TX   CoordSys=GLOBAL   F1=0    F2=0   F3=0   M1=10   M2=0   M3=0");
        }
        L.Add("");
        L.Add("TABLE:  \"LOAD CASE DEFINITIONS\"");
        foreach (string p in new string[] { "AX", "GY", "GZ", "TX" })
            L.Add("   Case=" + p + "   Type=LinStatic   InitialCond=Zero");
        L.Add("");
        L.Add("TABLE:  \"CASE - STATIC 1 - LOAD ASSIGNMENTS\"");
        foreach (string p in new string[] { "AX", "GY", "GZ", "TX" })
            L.Add("   Case=" + p + "   LoadType=\"Load pattern\"   LoadName=" + p + "   LoadSF=1");
        L.Add("");
        L.Add("END TABLE DATA"); L.Add("");
        File.WriteAllText(path, string.Join("\r\n", L));
    }

    public string RunProbe(string s2k)
    {
        Check(Model.File.OpenFile(s2k), "OpenFile " + s2k);
        Check(Model.SetPresentUnits(eUnits.kip_in_F), "SetPresentUnits");
        Check(Model.File.Save(Path.ChangeExtension(s2k, ".sdb")), "Save");
        Check(Model.Analyze.RunAnalysis(), "RunAnalysis");

        double A = 0, As2 = 0, As3 = 0, J = 0, I22 = 0, I33 = 0, S22 = 0, S33 = 0, Z22 = 0, Z33 = 0, r22 = 0, r33 = 0;
        Check(Model.PropFrame.GetSectProps("DUCT", ref A, ref As2, ref As3, ref J, ref I22, ref I33, ref S22, ref S33, ref Z22, ref Z33, ref r22, ref r33), "GetSectProps");
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("AXIS PROBE   SAP " + Version() + "   units kip, in");
        sb.AppendLine("section DUCT  Box/Tube t3=24 t2=12 tf=tw=0.25");
        sb.AppendLine(F("  A={0:0.###}  I22={1:0.#}  I33={2:0.#}  S22={3:0.#}  S33={4:0.#}  As2={5:0.###}  As3={6:0.###}", A, I22, I33, S22, S33, As2, As3));
        sb.AppendLine("  (I33 > I22 means t3 = depth measured along local 2, bending M3 about local 3)");
        sb.AppendLine();

        foreach (string fr in new string[] { "1", "2" })
        {
            double[] tm = new double[9];
            Model.FrameObj.GetTransformationMatrix(fr, ref tm, true);
            sb.AppendLine(F("frame {0}: local1=({1:0},{2:0},{3:0}) local2=({4:0},{5:0},{6:0}) local3=({7:0},{8:0},{9:0})  [global X,Y,Z]",
                fr, tm[0], tm[3], tm[6], tm[1], tm[4], tm[7], tm[2], tm[5], tm[8]));
        }
        sb.AppendLine();
        sb.AppendLine("frame  case   station    P        V2       V3       T        M2        M3      tipU1    tipU2    tipU3");
        string[] tips = { "2", "4" };
        string[] frs = { "1", "2" };
        foreach (string cs in new string[] { "AX", "GY", "GZ", "TX" })
        {
            Model.Results.Setup.DeselectAllCasesAndCombosForOutput();
            Model.Results.Setup.SetCaseSelectedForOutput(cs);
            for (int f = 0; f < 2; f++)
            {
                int r = 0;
                string[] obj = null, elm = null, cas = null, step = null;
                double[] objSta = null, elmSta = null, stepNum = null, p = null, v2 = null, v3 = null, t = null, m2 = null, m3 = null;
                Check(Model.Results.FrameForce(frs[f], eItemTypeElm.ObjectElm, ref r, ref obj, ref objSta, ref elm, ref elmSta,
                      ref cas, ref step, ref stepNum, ref p, ref v2, ref v3, ref t, ref m2, ref m3), "FrameForce");
                int k = 0;   // station 0 = fixed end
                for (int i = 0; i < r; i++) if (objSta[i] < objSta[k]) k = i;
                int nj = 0;
                string[] jo = null, je = null, jc = null, js = null;
                double[] jsn = null, u1 = null, u2 = null, u3 = null, q1 = null, q2 = null, q3 = null;
                Model.Results.JointDispl(tips[f], eItemTypeElm.ObjectElm, ref nj, ref jo, ref je, ref jc, ref js, ref jsn, ref u1, ref u2, ref u3, ref q1, ref q2, ref q3);
                sb.AppendLine(F("{0,-6} {1,-5} {2,7:0.0} {3,8:0.###} {4,8:0.###} {5,8:0.###} {6,8:0.###} {7,9:0.###} {8,9:0.###} {9,8:0.#####} {10,8:0.#####} {11,8:0.#####}",
                    frs[f], cs, objSta[k], p[k], v2[k], v3[k], t[k], m2[k], m3[k],
                    nj > 0 ? u1[0] : double.NaN, nj > 0 ? u2[0] : double.NaN, nj > 0 ? u3[0] : double.NaN));
            }
        }
        sb.AppendLine();
        sb.AppendLine(F("hand check, 1 kip tip load, L = 120 in, E = 29000:  PL^3/3EI22 = {0:0.#####} in   PL^3/3EI33 = {1:0.#####} in   (plus shear deformation)",
            1728000.0 / (3 * 29000 * I22), 1728000.0 / (3 * 29000 * I33)));
        sb.AppendLine("joint displacements are in joint local axes = global unless the joint was rotated.");
        sb.AppendLine("expected (SAP 26.3.0, 2026-09-14): frame 1 local2=(0,0,1) local3=(0,-1,0); GZ on frame 1 -> V2=+1, M3=+120, tipU3 ~0.0152 (I33);");
        sb.AppendLine("  GY on frame 1 -> V3=-1, M2=-120, tipU2 ~0.0434 (I22); AX -> P=+10 (tension positive); TX -> T=+10.");
        return sb.ToString();
    }

    public void Close() { if (Started) Sap.ApplicationExit(false); }

    // ---------------------------------------------------------------- helpers

    void Warn(string w) { if (Warnings.Count < 50) Warnings.Add(w); }
    static string G(double v) { return v.ToString("G6", Inv); }
    static string F(string fmt, params object[] args) { return string.Format(Inv, fmt, args); }
    static string Tab(params object[] cells)
    {
        string[] s = new string[cells.Length];
        for (int i = 0; i < cells.Length; i++) s[i] = Convert.ToString(cells[i], Inv);
        return string.Join("\t", s);
    }
    static void Write(string path, List<string> rows) { File.WriteAllText(path, string.Join("\r\n", rows) + "\r\n"); }
    static void Check(int ret, string what) { if (ret != 0) throw new Exception(what + " returned " + ret); }
}
