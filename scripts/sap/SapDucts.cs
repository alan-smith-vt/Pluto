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

public partial class SapDucts
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    // duct materials (user 2026-11-25): one carbon and one stainless material, both always in the model (an engineer can
    // reassign a section). frames.csv Material = carbon | stainless (DuctMaterials per system; absent column = carbon).
    //   DUCT_CS carbon sheet (A1011):  E 29000 ksi, nu 0.3, alpha 6.5e-6 /F, 0.2836 lb/in3, Fy 33, Fu 52 ksi
    //   DUCT_SS stainless (304L):      E 28000 ksi, nu 0.3, alpha 9.6e-6 /F, 0.29 lb/in3,   Fy 30, Fu 70 ksi
    // Sections carry the material in their name: DUCT_24x12_t0.0625_CS / _SS.
    // wall thickness where none is stated: the same deliberately unrealistic value as DuctGeom.ThkPlaceholder (0.001 in),
    // so a section carrying it is unmistakable and must be fixed in SAP (user 2026-11-10)
    public const double ThkPlaceholder = 0.001;
    public const string MatCS = "DUCT_CS", MatSS = "DUCT_SS", NoSize = "DUCT_NOSIZE", Stub = "TAP_STUB", StubMat = "RIGID_STUB";

    public SapSurvey S;
    cSapModel M { get { return S.Model; } }
    public SapDucts(SapSurvey s) { S = s; }
    public string ModelPath() { return M.GetModelFilename(true); }

    public string Build(string framesCsv, string savePath) { return Build(framesCsv, savePath, ""); }

    // include: extra SystemRole values to keep besides "main" ("fragment", "other", "navisworks-only"; comma separated)
    public string Build(string framesCsv, string savePath, string include)
    {
        List<Dictionary<string, string>> rows = ReadCsv(framesCsv);
        if (rows.Count == 0) throw new Exception("no frames in " + framesCsv);
        string floatNote = DropFloating(rows);
        if (rows.Count == 0) throw new Exception("every frame in " + framesCsv + " is FLOATING (no support): nothing left for SAP");
        string filterNote = FilterRoles(rows, include) + floatNote;
        Check(M.InitializeNewModel(eUnits.kip_in_F), "InitializeNewModel");
        Check(M.File.NewBlank(), "NewBlank");
        DuctMaterial(MatCS, 29000, 6.5e-6, 2.836e-4, 33, 52);
        DuctMaterial(MatSS, 28000, 9.6e-6, 2.9e-4, 30, 70);
        Check(M.PropFrame.SetTube(NoSize, MatCS, 12, 12, ThkPlaceholder, ThkPlaceholder, -1, "", ""), "SetTube " + NoSize);

        var sections = new HashSet<string>(StringComparer.Ordinal);
        var joints = new Dictionary<string, string>(StringComparer.Ordinal);
        var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        Action<string, string> addTo = (g, f) => { List<string> l; if (!groups.TryGetValue(g, out l)) { l = new List<string>(); groups[g] = l; } l.Add(f); };
        int skipped = 0, oriented = 0, noDir = 0;
        var zeroRows = new List<string>(); var failed = new List<string>();
        var frameName = new Dictionary<string, string>(StringComparer.Ordinal);   // frames.csv Frame -> SAP frame
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
            if (M.FrameObj.AddByPoint(ji, jj, ref name, sec, ObjectName(r)) != 0)
            {
                failed.Add(F("{0} ({1} -> {2}, {3:0.###} in, parts {4})", r["Frame"], r["JointI"], r["JointJ"], Math.Sqrt(dx * dx + dy * dy + dz * dz), r["RevitElementId"]));
                continue;
            }
            frameName[r["Frame"]] = name;
            // orientation (2026-10-17): the box's width (W_in) along local 3, from the frame's width direction
            if (r["Shape"] == "rect" && sec != NoSize)
            {
                double ang;
                if (LocalAngle(Num(r, "XI_in"), Num(r, "YI_in"), Num(r, "ZI_in"), Num(r, "XJ_in"), Num(r, "YJ_in"), Num(r, "ZJ_in"), Num(r, "WX"), Num(r, "WY"), Num(r, "WZ"), out ang))
                { if (Math.Abs(ang) > 1e-6) Check(M.FrameObj.SetLocalAxes(name, ang), "SetLocalAxes " + name); oriented++; }
                else { noDir++; addTo("FLAG_NO_ORIENTATION", name); }
            }
            // where the frame came from, readable in SAP's object information (2026-10-17, user: finding the Revit id
            // meant looking the frame up in frames.csv)
            string rid = Val(r, "RevitElementId"), nid = Val(r, "NavisId");
            string src = (rid != "" ? "Revit " + rid : "") + (rid != "" && nid != "" ? " | " : "") + (nid != "" ? "Navisworks #" + nid : "");
            if (src != "") M.FrameObj.SetGUID(name, src.Length > 250 ? src.Substring(0, 247) + "..." : src);
            addTo("DUCT_ALL", name);
            addTo("SRC_" + Clean(r["Source"]), name);
            string sys; if (r.TryGetValue("System", out sys) && !string.IsNullOrEmpty(sys))
            {
                string g = "SYS_" + Clean(sys.Replace(":", "")); addTo(g, name);
                string hex; int col; if (!groupColor.ContainsKey(g) && r.TryGetValue("SystemColor", out hex) && OleColor(hex, out col)) groupColor[g] = col;
            }
            if (sec == NoSize) addTo("FLAG_NO_SIZE", name);
            // no thickness given (user 2026-11-25): the 0.001 in placeholder, findable as a group
            else if (r["Shape"] != "rigid" && (!(Num(r, "Thk_in") > 0) || Num(r, "Thk_in") <= ThkPlaceholder || Val(r, "ThkSource") == "default")) addTo("FLAG_NO_THICKNESS", name);
            if (r["Shape"] != "rigid") addTo(IsStainless(r) ? "MAT_STAINLESS" : "MAT_CARBON", name);
            if (sec == Stub) addTo("TAP_STUBS", name);
            foreach (string f in (r["Flags"] ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)) addTo("FLAG_" + Clean(f), name);
            // one group per unsupported span (frames.csv Span = US<n>, longest first; 2026-10-30 board laptop:936)
            string span = Val(r, "Span");
            if (span != "") addTo("SPAN_" + Clean(span), name);
        }
        // steel, supports, extra flags, plant axes (SapSteel.cs); nothing when none of their inputs is set
        string steelNote = AddSteelAndSupports(joints, sections, addTo, frameName);
        // groups in reading order with colours, unused template properties deleted, the legend beside the .sdb (SapUx.cs)
        SortedDictionary<string, int> groupCount = DefineGroups(groups, groupColor);
        string propNote = CleanProperties();
        Check(M.File.Save(savePath), "Save " + savePath);
        WriteLegend(savePath, groupCount, propNote);
        var parts = new List<string>();
        foreach (KeyValuePair<string, List<string>> kv in groups) if (kv.Key.StartsWith("FLAG_")) parts.Add(kv.Key.Substring(5) + " " + kv.Value.Count);
        var spans = groups.Where(g => g.Key.StartsWith("SPAN_")).ToList();
        string spanNote = spans.Count == 0 ? "" : F("\nNEW unsupported spans: {0} spans ({1} frames), groups SPAN_<id>", spans.Count, spans.Sum(g => g.Value.Count));
        int defThk = rows.Count(r => r["Shape"] != "rigid" && (r.ContainsKey("ThkSource") ? r["ThkSource"] : "default") == "default");
        return F("NEW kept: " + filterNote + "\nNEW SAP model {0}: {1} frames, {2} joints, {3} sections{4}; materials {5}\nNEW orientation: {6} rect frames oriented, {7} without a width direction (FLAG_NO_ORIENTATION); thickness PLACEHOLDER {11} in (none stated) on {8} frames\nNEW flag groups (frames): {9}{10}",
            Path.GetFileName(savePath), groups["DUCT_ALL"].Count, joints.Count, sections.Count + 1,
            skipped > 0 ? F(", {0} zero-length rows skipped ({1})", skipped, string.Join(" ", zeroRows.Take(10).ToArray()) + (zeroRows.Count > 10 ? " ..." : "")) : "",
            F("carbon {0} frames ({1}), stainless {2} ({3})", groups.ContainsKey("MAT_CARBON") ? groups["MAT_CARBON"].Count : 0, MatCS, groups.ContainsKey("MAT_STAINLESS") ? groups["MAT_STAINLESS"].Count : 0, MatSS), oriented, noDir, defThk, parts.Count > 0 ? string.Join(", ", parts.ToArray()) : "none",
            failed.Count > 0 ? F("\nNEW SAP refused {0} frames (not in the model): {1}", failed.Count, string.Join("; ", failed.Take(10).ToArray()) + (failed.Count > 10 ? " ..." : "")) : "", ThkPlaceholder)
            + spanNote + (steelNote != "" ? "\n" + steelNote : "") + "\n" + propNote + "\nNEW legend: " + Path.GetFileName(LegendPath);
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

    // Keeps the frames whose SystemRole is "main" or listed in include (2026-10-17, user: one-frame fragments and
    // Navisworks-only accessories speared the real ducts all over the SAP model). Frames with no role (rigid / side
    // links) stay when both their joints lie on kept frames. frames.csv without SystemRole: everything kept.
    static string FilterRoles(List<Dictionary<string, string>> rows, string include)
    {
        if (rows.Count == 0 || !rows[0].ContainsKey("SystemRole")) return "all (frames.csv has no SystemRole column: rerun the overlay)";
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "main" };
        foreach (string s in (include ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)) keep.Add(s.Trim());
        var drop = new Dictionary<string, int>(StringComparer.Ordinal);
        var kept = new List<Dictionary<string, string>>(); var noRole = new List<Dictionary<string, string>>();
        foreach (Dictionary<string, string> r in rows)
        {
            string role = Val(r, "SystemRole");
            if (role == "") noRole.Add(r);
            else if (keep.Contains(role)) kept.Add(r);
            else { int c; drop.TryGetValue(role, out c); drop[role] = c + 1; }
        }
        // links: repeat, a link can hang off another link (tap stub -> side link)
        var on = new HashSet<string>(StringComparer.Ordinal);
        foreach (Dictionary<string, string> r in kept) { on.Add(r["JointI"]); on.Add(r["JointJ"]); }
        int links = 0; bool more = true;
        while (more)
        {
            more = false;
            foreach (Dictionary<string, string> r in noRole.ToList())
                if (on.Contains(r["JointI"]) && on.Contains(r["JointJ"])) { kept.Add(r); noRole.Remove(r); links++; more = true; }
        }
        if (noRole.Count > 0) drop["links to dropped frames"] = noRole.Count;
        rows.Clear(); rows.AddRange(kept);
        return F("{0} frames ({1}{2}); left out: {3}", kept.Count, string.Join(" + ", keep.ToArray()), links > 0 ? F(", {0} links", links) : "",
            drop.Count == 0 ? "none" : string.Join(", ", drop.Select(kv => kv.Key + " " + kv.Value).ToArray()));
    }

    // Stability rule 1 (user, 2026-10-30, board laptop:1172): a duct piece with no support at all (flag FLOATING on all
    // its frames, rigid links included) is left out of the SAP model; its free rigid-body modes made MODAL hang.

    static string DropFloating(List<Dictionary<string, string>> rows)
    {
        var gone = rows.Where(r => (" " + Val(r, "Flags") + " ").Contains(" FLOATING ")).ToList();
        if (gone.Count == 0) return "";
        foreach (Dictionary<string, string> r in gone) rows.Remove(r);
        return F("; left out (FLOATING): {0} frames ({1})", gone.Count, string.Join(" ", gone.Take(10).Select(r => r["Frame"]).ToArray()) + (gone.Count > 10 ? " ..." : ""));
    }

    static string Val(Dictionary<string, string> r, string k) { string v; return r.TryGetValue(k, out v) && v != null ? v : ""; }

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
        if (double.IsNaN(t) || t <= 0) t = ThkPlaceholder;
        bool round = r["Shape"] == "round" && !double.IsNaN(d);
        if (!round && (double.IsNaN(w) || double.IsNaN(h))) return NoSize;
        bool ss = IsStainless(r); string mat = ss ? MatSS : MatCS;
        string name = (round ? F("DUCT_{0:0.##}dia_t{1:0.####}", d, t) : F("DUCT_{0:0.##}x{1:0.##}_t{2:0.####}", w, h, t)) + (ss ? "_SS" : "_CS");
        if (made.Add(name))
        {
            if (round) Check(M.PropFrame.SetPipe(name, mat, d, t, -1, "", ""), "SetPipe " + name);
            else Check(M.PropFrame.SetTube(name, mat, h, w, t, t, -1, "", ""), "SetTube " + name);
        }
        return name;
    }

    // readable object names (user 2026-11-25): the system in front of the frames.csv id, so a selected frame says what
    // it is: SYSA_F12, SYSA_FRAG_F7 (fragments), OTHER_EXH_F79; rigid tap stubs LNK_F80. Joints keep their names.
    static string ObjectName(Dictionary<string, string> r)
    {
        if (r["Shape"] == "rigid") return "LNK_" + r["Frame"];
        string sys = Val(r, "System").Trim();
        if (sys == "") return r["Frame"];
        sys = sys.Replace(" fragments", "_FRAG").Replace("other: ", "OTHER_").Replace(":", "");
        return Clean(sys) + "_" + r["Frame"];
    }

    static bool IsStainless(Dictionary<string, string> r) { return string.Equals(Val(r, "Material"), "stainless", StringComparison.OrdinalIgnoreCase); }

    // a steel sheet material: E, alpha, unit weight (kip/in3), Fy / Fu (ksi; expected = minimum)
    void DuctMaterial(string name, double e, double alpha, double w, double fy, double fu)
    {
        Check(M.PropMaterial.SetMaterial(name, eMatType.Steel, -1, "", ""), "SetMaterial " + name);
        Check(M.PropMaterial.SetMPIsotropic(name, e, 0.3, alpha, 0), "SetMPIsotropic " + name);
        Check(M.PropMaterial.SetWeightAndMass(name, 1, w, 0), "SetWeightAndMass " + name);
        Check(M.PropMaterial.SetOSteel_1(name, fy, fu, fy, fu, 1, 1, 0.015, 0.11, 0.17, -0.1, 0), "SetOSteel_1 " + name);
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
