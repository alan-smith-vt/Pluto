// HVAC duct SAP model from a Revit overlay run's <out>.frames.csv (scripts\arms\DuctsRevitOverlay.cs). C# 5,
// Add-Type under PS 5.1. Geometry and connectivity only: a new blank model (kip, in), one frame per row between
// named joints (JointI / JointJ; MergeOff, so only joints the data shares are connected), box / pipe sections per
// stated size and thickness, groups DUCT_ALL, SRC_<source> and FLAG_<flag> (a frame can be in several; see vault
// handoff Pluto 2026-10-12). No supports, loads or cases yet. Coordinates: Navisworks frame, absolute inches.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using SAP2000v1;

public class SapDucts
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    // Mat: PLACEHOLDER (2026-10-17, user: material is a major open TODO): galvanized-steel values under a name that
    // says so; replace with the engineers' material before the model goes out
    public const string Mat = "DUCT_MAT_TBD", NoSize = "DUCT_NOSIZE", Stub = "TAP_STUB", StubMat = "RIGID_STUB";

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
        int skipped = 0, oriented = 0, noDir = 0;
        var zeroRows = new List<string>(); var failed = new List<string>();
        var groupColor = new Dictionary<string, int>(StringComparer.Ordinal);   // SYS_ group -> the run colour (frames.csv SystemColor)
        foreach (Dictionary<string, string> r in rows)
        {
            double dx = Num(r, "XJ_in") - Num(r, "XI_in"), dy = Num(r, "YJ_in") - Num(r, "YI_in"), dz = Num(r, "ZJ_in") - Num(r, "ZI_in");
            // zero length (2026-10-17, production: two joint ids at one position after gap closing; SAP refused it);
            // checked before the joints are made so no loose points are left
            if (r["JointI"] == r["JointJ"] || !(Math.Sqrt(dx * dx + dy * dy + dz * dz) >= 0.01)) { skipped++; zeroRows.Add(r["Frame"]); continue; }
            string ji = Joint(joints, r["JointI"], Num(r, "XI_in"), Num(r, "YI_in"), Num(r, "ZI_in"));
            string jj = Joint(joints, r["JointJ"], Num(r, "XJ_in"), Num(r, "YJ_in"), Num(r, "ZJ_in"));
            string sec = Section(sections, r);
            string name = "";
            if (M.FrameObj.AddByPoint(ji, jj, ref name, sec, r["Frame"]) != 0)
            {
                failed.Add(F("{0} ({1} -> {2}, {3:0.###} in, parts {4})", r["Frame"], r["JointI"], r["JointJ"], Math.Sqrt(dx * dx + dy * dy + dz * dz), r["RevitElementId"]));
                continue;
            }
            // orientation (2026-10-17): the box's width (W_in) along local 3, from the frame's width direction
            if (r["Shape"] == "rect" && sec != NoSize)
            {
                double ang;
                if (LocalAngle(Num(r, "XI_in"), Num(r, "YI_in"), Num(r, "ZI_in"), Num(r, "XJ_in"), Num(r, "YJ_in"), Num(r, "ZJ_in"), Num(r, "WX"), Num(r, "WY"), Num(r, "WZ"), out ang))
                { if (Math.Abs(ang) > 1e-6) Check(M.FrameObj.SetLocalAxes(name, ang), "SetLocalAxes " + name); oriented++; }
                else { noDir++; addTo("FLAG_NO_ORIENTATION", name); }
            }
            addTo("DUCT_ALL", name);
            addTo("SRC_" + Clean(r["Source"]), name);
            string sys; if (r.TryGetValue("System", out sys) && !string.IsNullOrEmpty(sys))
            {
                string g = "SYS_" + Clean(sys.Replace(":", "")); addTo(g, name);
                string hex; int col; if (!groupColor.ContainsKey(g) && r.TryGetValue("SystemColor", out hex) && OleColor(hex, out col)) groupColor[g] = col;
            }
            if (sec == NoSize) addTo("FLAG_NO_SIZE", name);
            if (sec == Stub) addTo("TAP_STUBS", name);
            foreach (string f in (r["Flags"] ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)) addTo("FLAG_" + Clean(f), name);
        }
        // SYS_ groups defined last and the only coloured ones (Display Options > Color By: Groups)
        foreach (KeyValuePair<string, List<string>> kv in groups.OrderBy(g => g.Key.StartsWith("SYS_") ? 1 : 0).ToList())
        {
            int col; if (!groupColor.TryGetValue(kv.Key, out col)) col = -1;
            Check(M.GroupDef.SetGroup(kv.Key, col, true, true, true, true, true, true, true, true, true, true, true), "SetGroup " + kv.Key);
            foreach (string f in kv.Value) Check(M.FrameObj.SetGroupAssign(f, kv.Key, false, eItemType.Objects), "SetGroupAssign " + f + " " + kv.Key);
        }
        Check(M.File.Save(savePath), "Save " + savePath);
        var parts = new List<string>();
        foreach (KeyValuePair<string, List<string>> kv in groups) if (kv.Key.StartsWith("FLAG_")) parts.Add(kv.Key.Substring(5) + " " + kv.Value.Count);
        int defThk = rows.Count(r => r["Shape"] != "rigid" && (r.ContainsKey("ThkSource") ? r["ThkSource"] : "default") == "default");
        return F("NEW SAP model {0}: {1} frames, {2} joints, {3} sections{4}; material {5} (PLACEHOLDER)\nNEW orientation: {6} rect frames oriented, {7} without a width direction (FLAG_NO_ORIENTATION); thickness default 0.06 in on {8} frames\nNEW flag groups (frames): {9}{10}",
            Path.GetFileName(savePath), groups["DUCT_ALL"].Count, joints.Count, sections.Count + 1,
            skipped > 0 ? F(", {0} zero-length rows skipped ({1})", skipped, string.Join(" ", zeroRows.Take(10).ToArray()) + (zeroRows.Count > 10 ? " ..." : "")) : "",
            Mat, oriented, noDir, defThk, parts.Count > 0 ? string.Join(", ", parts.ToArray()) : "none",
            failed.Count > 0 ? F("\nNEW SAP refused {0} frames (not in the model): {1}", failed.Count, string.Join("; ", failed.Take(10).ToArray()) + (failed.Count > 10 ? " ..." : "")) : "");
    }

    // SAP's local-axis angle (degrees) that puts local 3 along the width direction w. Default axes (SAP): local 1 = I->J;
    // local 2 = the upward (+Z) direction square to it, or global +X when the frame is vertical; local 3 = 1 x 2. The
    // angle turns 2 and 3 about +1, right-handed: 3' = -sin(a) 2 + cos(a) 3, so a = atan2(-w.2, w.3).
    public static bool LocalAngle(double xi, double yi, double zi, double xj, double yj, double zj, double wx, double wy, double wz, out double ang)
    {
        ang = 0;
        double[] x1 = Unit(new[] { xj - xi, yj - yi, zj - zi }), w = { wx, wy, wz };
        if (x1 == null || double.IsNaN(wx) || double.IsNaN(wy) || double.IsNaN(wz)) return false;
        double dw = Dot(w, x1); w = Unit(new[] { w[0] - dw * x1[0], w[1] - dw * x1[1], w[2] - dw * x1[2] });
        if (w == null) return false;
        bool vertical = Math.Sqrt(x1[0] * x1[0] + x1[1] * x1[1]) < 1e-3;
        double[] l2 = vertical ? new double[] { 1, 0, 0 } : new[] { 0.0, 0.0, 1.0 };
        double d2 = Dot(l2, x1); l2 = Unit(new[] { l2[0] - d2 * x1[0], l2[1] - d2 * x1[1], l2[2] - d2 * x1[2] });
        double[] l3 = { x1[1] * l2[2] - x1[2] * l2[1], x1[2] * l2[0] - x1[0] * l2[2], x1[0] * l2[1] - x1[1] * l2[0] };
        ang = Math.Atan2(-Dot(w, l2), Dot(w, l3)) * 180 / Math.PI;
        return true;
    }

    // "#rrggbb" -> SAP / OLE colour (red in the low byte)
    static bool OleColor(string hex, out int col)
    {
        col = -1; int v;
        if (string.IsNullOrEmpty(hex) || !int.TryParse(hex.TrimStart('#'), NumberStyles.HexNumber, Inv, out v)) return false;
        col = (v >> 16 & 255) | (v & 0xff00) | (v & 255) << 16;
        return true;
    }

    static double Dot(double[] a, double[] b) { return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]; }
    static double[] Unit(double[] a) { double l = Math.Sqrt(Dot(a, a)); return l < 1e-9 ? null : new[] { a[0] / l, a[1] / l, a[2] / l }; }

    string Joint(Dictionary<string, string> joints, string key, double x, double y, double z)
    {
        string name;
        if (joints.TryGetValue(key, out name)) return name;
        name = "";
        Check(M.PointObj.AddCartesian(x, y, z, ref name, key, "Global", true, 0), "PointObj.AddCartesian " + key);
        joints[key] = name;
        return name;
    }

    // DUCT_<W>x<H>_t<thk> (SetTube: depth = H, width = W) or DUCT_<D>dia_t<thk> (SetPipe); none -> DUCT_NOSIZE;
    // Shape "rigid" (tap stub: duct centreline to the tap point on its side) -> TAP_STUB, stiff and weightless
    string Section(HashSet<string> made, Dictionary<string, string> r)
    {
        if (r["Shape"] == "rigid")
        {
            if (made.Add(Stub))
            {
                Check(M.PropMaterial.SetMaterial(StubMat, eMatType.Steel, -1, "", ""), "SetMaterial " + StubMat);
                Check(M.PropMaterial.SetMPIsotropic(StubMat, 29000 * 100, 0.3, 0, 0), "SetMPIsotropic " + StubMat);
                Check(M.PropMaterial.SetWeightAndMass(StubMat, 1, 0, 0), "SetWeightAndMass " + StubMat);
                Check(M.PropFrame.SetTube(Stub, StubMat, 12, 12, 1, 1, -1, "", ""), "SetTube " + Stub);
            }
            return Stub;
        }
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
