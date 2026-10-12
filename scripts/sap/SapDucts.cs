// HVAC duct SAP model from a Revit overlay run's <out>.frames.csv (scripts\arms\DuctsRevitOverlay.cs). C# 5,
// Add-Type under PS 5.1. Geometry and connectivity only: a new blank model (kip, in), one frame per row between
// named joints (JointI / JointJ; MergeOff, so only joints the data shares are connected), box / pipe sections per
// stated size and thickness, groups DUCT_ALL, SRC_<source> and FLAG_<flag> (a frame can be in several; see vault
// handoff Pluto 2026-10-12). No supports, loads or cases yet. Coordinates: Navisworks frame, absolute inches.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SAP2000v1;

public class SapDucts
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public const string Mat = "DUCT_STEEL", NoSize = "DUCT_NOSIZE";

    public SapSurvey S;
    cSapModel M { get { return S.Model; } }
    public SapDucts(SapSurvey s) { S = s; }
    public string ModelPath() { return M.GetModelFilename(true); }

    public string Build(string framesCsv, string savePath)
    {
        List<Dictionary<string, string>> rows = ReadCsv(framesCsv);
        if (rows.Count == 0) throw new Exception("no frames in " + framesCsv);
        Check(M.InitializeNewModel(eUnits.kip_in_F), "InitializeNewModel");
        Check(M.File.NewBlank(), "NewBlank");
        // galvanized sheet steel: E 29000 ksi, alpha 6.5e-6 /F, 0.2836 lb/in3
        Check(M.PropMaterial.SetMaterial(Mat, eMatType.Steel, -1, "", ""), "SetMaterial");
        Check(M.PropMaterial.SetMPIsotropic(Mat, 29000, 0.3, 6.5e-6, 0), "SetMPIsotropic");
        Check(M.PropMaterial.SetWeightAndMass(Mat, 1, 2.836e-4, 0), "SetWeightAndMass");
        Check(M.PropFrame.SetTube(NoSize, Mat, 12, 12, 0.06, 0.06, -1, "", ""), "SetTube " + NoSize);

        var sections = new HashSet<string>(StringComparer.Ordinal);
        var joints = new Dictionary<string, string>(StringComparer.Ordinal);
        var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        Action<string, string> addTo = (g, f) => { List<string> l; if (!groups.TryGetValue(g, out l)) { l = new List<string>(); groups[g] = l; } l.Add(f); };
        int skipped = 0;
        foreach (Dictionary<string, string> r in rows)
        {
            string ji = Joint(joints, r["JointI"], Num(r, "XI_in"), Num(r, "YI_in"), Num(r, "ZI_in"));
            string jj = Joint(joints, r["JointJ"], Num(r, "XJ_in"), Num(r, "YJ_in"), Num(r, "ZJ_in"));
            if (ji == jj) { skipped++; continue; }
            string sec = Section(sections, r);
            string name = "";
            Check(M.FrameObj.AddByPoint(ji, jj, ref name, sec, r["Frame"]), "FrameObj.AddByPoint " + r["Frame"]);
            addTo("DUCT_ALL", name);
            addTo("SRC_" + Clean(r["Source"]), name);
            if (sec == NoSize) addTo("FLAG_NO_SIZE", name);
            foreach (string f in (r["Flags"] ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)) addTo("FLAG_" + Clean(f), name);
        }
        foreach (KeyValuePair<string, List<string>> kv in groups)
        {
            Check(M.GroupDef.SetGroup(kv.Key, -1, true, true, true, true, true, true, true, true, true, true, true), "SetGroup " + kv.Key);
            foreach (string f in kv.Value) Check(M.FrameObj.SetGroupAssign(f, kv.Key, false, eItemType.Objects), "SetGroupAssign " + f + " " + kv.Key);
        }
        Check(M.File.Save(savePath), "Save " + savePath);
        var parts = new List<string>();
        foreach (KeyValuePair<string, List<string>> kv in groups) if (kv.Key.StartsWith("FLAG_")) parts.Add(kv.Key.Substring(5) + " " + kv.Value.Count);
        return F("NEW SAP model {0}: {1} frames, {2} joints, {3} sections{4}\nNEW flag groups (frames): {5}",
            Path.GetFileName(savePath), groups["DUCT_ALL"].Count, joints.Count, sections.Count + 1,
            skipped > 0 ? F(", {0} zero-length rows skipped", skipped) : "", parts.Count > 0 ? string.Join(", ", parts.ToArray()) : "none");
    }

    string Joint(Dictionary<string, string> joints, string key, double x, double y, double z)
    {
        string name;
        if (joints.TryGetValue(key, out name)) return name;
        name = "";
        Check(M.PointObj.AddCartesian(x, y, z, ref name, key, "Global", true, 0), "PointObj.AddCartesian " + key);
        joints[key] = name;
        return name;
    }

    // DUCT_<W>x<H>_t<thk> (SetTube: depth = H, width = W) or DUCT_<D>dia_t<thk> (SetPipe); none -> DUCT_NOSIZE
    string Section(HashSet<string> made, Dictionary<string, string> r)
    {
        double w = Num(r, "W_in"), h = Num(r, "H_in"), d = Num(r, "Dia_in"), t = Num(r, "Thk_in");
        if (double.IsNaN(t) || t <= 0) t = 0.06;
        bool round = r["Shape"] == "round" && !double.IsNaN(d);
        if (!round && (double.IsNaN(w) || double.IsNaN(h))) return NoSize;
        string name = round ? F("DUCT_{0:0.##}dia_t{1:0.###}", d, t) : F("DUCT_{0:0.##}x{1:0.##}_t{2:0.###}", w, h, t);
        if (made.Add(name))
        {
            if (round) Check(M.PropFrame.SetPipe(name, Mat, d, t, -1, "", ""), "SetPipe " + name);
            else Check(M.PropFrame.SetTube(name, Mat, h, w, t, t, -1, "", ""), "SetTube " + name);
        }
        return name;
    }

    static string Clean(string s) { return (s ?? "").Replace('-', '_').Replace(' ', '_').ToUpperInvariant(); }

    static double Num(Dictionary<string, string> r, string k)
    {
        string v; double x;
        return r.TryGetValue(k, out v) && double.TryParse(v, NumberStyles.Float, Inv, out x) ? x : double.NaN;
    }

    static List<Dictionary<string, string>> ReadCsv(string path)
    {
        var res = new List<Dictionary<string, string>>();
        string[] lines = File.ReadAllLines(path);
        if (lines.Length == 0) return res;
        List<string> cols = Split(lines[0]);
        for (int i = 1; i < lines.Length; i++)
        {
            if (lines[i].Length == 0) continue;
            List<string> f = Split(lines[i]);
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int c = 0; c < cols.Count; c++) d[cols[c]] = c < f.Count ? f[c] : "";
            res.Add(d);
        }
        return res;
    }

    static List<string> Split(string line)
    {
        var f = new List<string>(); var cur = new System.Text.StringBuilder(); bool q = false;
        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (q) { if (ch == '"') { if (i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; } else q = false; } else cur.Append(ch); }
            else if (ch == '"') q = true;
            else if (ch == ',') { f.Add(cur.ToString()); cur.Length = 0; }
            else cur.Append(ch);
        }
        f.Add(cur.ToString());
        return f;
    }

    static string F(string fmt, params object[] args) { return string.Format(Inv, fmt, args); }
    static void Check(int ret, string what) { if (ret != 0) throw new Exception(what + " returned " + ret); }
}
