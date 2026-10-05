using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Plugins;
using ComApi = Autodesk.Navisworks.Api.Interop.ComApi;
using ComBridge = Autodesk.Navisworks.Api.ComApi.ComApiBridge;

namespace PlutoNavis
{
    // Read-only duct extraction from the open model. One native Search for Element/IfcGUID (~8-9 min on the
    // full federation), then every Ducts / Duct Fittings / Duct Accessories element -> one row (Duct
    // Insulations skipped: they wrap the ducts). Writes <OutRoot>\<yyyyMMdd-HHmmss>\:
    //   ducts.csv      id, Revit category / family / type / system, IfcObjectProperties parsed to numbers
    //                  (sizes in, lengths ft), bbox, own / skipped geometry counts; for Ducts also the
    //                  triangle fit of the OWN geometry (descendants with a different IfcGUID are another
    //                  element's): endpoints (best-fit axis, extent along it), fitted length and
    //                  cross-section, and checks against .Length / .Size / the bbox
    //   duct_parts.csv one row per geometry item under each duct: used or skipped, its name / class /
    //                  own IfcGUID / category, and its own fit (what the pieces under a duct are)
    //   ducts_tri.bin  own triangles of the Ducts, world coordinates (document units): per element
    //                  int32 row (1-based ducts.csv data row), int32 nTri, nTri * 9 float32 (x y z per corner)
    //   summary.txt    counts, timings, check statistics
    // The triangles come from the COM API (ComApiBridge -> fragments -> GenerateSimplePrimitives, local
    // vertices times the fragment's local-to-world matrix). TriVsBbox_ft: own triangles' world extent vs
    // the element bbox (0 on the first run with all geometry: transform right; > 0 now when geometry was
    // skipped, since the bbox includes it). C# 5 (built by Build-NavisPlugin.ps1).
    [Plugin("DuctsButton", "Pluto",
        DisplayName = "Pluto Ducts",
        ToolTip = "Read-only: ducts, fittings, accessories + duct triangles to C:\\Temp\\hvac\\ducts")]
    [AddInPlugin(AddInLocation.AddIn)]
    public class DuctsButton : AddInPlugin
    {
        const string OutRoot = @"C:\Temp\hvac\ducts";
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static readonly string[] Keep = { "Ducts", "Duct Fittings", "Duct Accessories" };

        public override int Execute(params string[] parameters)
        {
            Document doc = Autodesk.Navisworks.Api.Application.ActiveDocument;
            if (doc == null || doc.Models.Count == 0) { MessageBox.Show("Open a model first.", "Pluto Ducts"); return 0; }
            DateTime t0 = DateTime.Now;
            string dir = Path.Combine(OutRoot, t0.ToString("yyyyMMdd-HHmmss", Inv));
            Directory.CreateDirectory(dir);
            string log = Path.Combine(dir, "started.txt");
            File.WriteAllText(log, t0.ToString("yyyy-MM-dd HH:mm:ss", Inv) + "\r\n");

            int total = 0, seen = 0, rows = 0, ducts = 0, fitted = 0, triErrors = 0;
            var byCat = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var lenErr = new List<double>(); var bbErr = new List<double>();
            double searchSec = 0; bool cancelled = false; string firstTriError = null;
            Progress progress = Autodesk.Navisworks.Api.Application.BeginProgress("Pluto ducts: searching for IfcGUID items");
            try
            {
                progress.Update(0);
                Search search = new Search();
                search.Selection.SelectAll();
                search.Locations = SearchLocations.DescendantsAndSelf;
                search.SearchConditions.Add(SearchCondition.HasPropertyByDisplayName("Element", "IfcGUID"));
                ModelItemCollection found = search.FindAll(doc, false);
                total = found.Count;
                searchSec = (DateTime.Now - t0).TotalSeconds;
                File.AppendAllText(log, string.Format(Inv, "search: {0} items in {1:0}s\r\n", total, searchSec));

                using (var w = new StreamWriter(Path.Combine(dir, "ducts.csv"), false, new UTF8Encoding(false)))
                using (var pw = new StreamWriter(Path.Combine(dir, "duct_parts.csv"), false, new UTF8Encoding(false)))
                using (var tri = new BinaryWriter(File.Create(Path.Combine(dir, "ducts_tri.bin"))))
                {
                    pw.WriteLine("DuctIfcGUID,Used,DisplayName,ClassDisplayName,PartIfcGUID,PartCategory,Triangles,FitLength_ft,FitA_in,FitB_in,X1,Y1,Z1,X2,Y2,Z2");
                    w.WriteLine(string.Join(",", new[] {
                        "IfcGUID", "Category", "Family", "Type", "SystemName", "SystemType", "Shape", "Material",
                        "Width_in", "Height_in", "Diameter_in", "WallThk_in", "InsulThk_in", "Length_ft", "Size", "Location",
                        "MinX", "MinY", "MinZ", "MaxX", "MaxY", "MaxZ", "OwnGeom", "SkippedGeom", "Triangles",
                        "X1", "Y1", "Z1", "X2", "Y2", "Z2", "FitLength_ft", "FitA_in", "FitB_in",
                        "LenErr_ft", "SizeErr_in", "TriVsBbox_ft", "Flag" }));
                    foreach (ModelItem it in found)
                    {
                        seen++;
                        if (seen % 200 == 0 && !progress.Update(total > 0 ? (double)seen / total : 0)) { cancelled = true; break; }
                        Dictionary<string, string> p = Props(it);
                        string cat = Get(p, "Element|Category");
                        if (Array.IndexOf(Keep, cat) < 0) continue;
                        rows++;
                        int n; byCat.TryGetValue(cat, out n); byCat[cat] = n + 1;

                        double wIn, hIn;
                        string size = Ifc(p, "Size");
                        ParseSize(size, out wIn, out hIn);
                        double dia = Len(Ifc(p, "DuctDiameter"), 12);
                        double lenFt = Len(Ifc(p, "Length"), 1);
                        if (double.IsNaN(lenFt)) lenFt = Len(Ifc(p, "DuctLength"), 1);
                        BoundingBox3D bb = it.BoundingBox();
                        bool hasBb = bb != null && !bb.IsEmpty;

                        string guid = Get(p, "Element|IfcGUID");
                        var geoms = new List<ModelItem>(); var skipped = new List<ModelItem>();
                        Collect(it, guid, true, geoms, skipped);

                        var cells = new List<string> {
                            guid, cat, Get(p, "Element|Family"), Get(p, "Element|Type"),
                            Get(p, "Element|System Name"), Get(p, "Element|System Type"), Ifc(p, "Shape"), Ifc(p, "Material"),
                            N(wIn), N(hIn), N(dia), N(Len(Ifc(p, "Wall Thickness"), 12)), N(Len(Ifc(p, "Insulation Thickness"), 12)),
                            N(lenFt), size, Ifc(p, "Location") };
                        if (hasBb) cells.AddRange(new[] { N(bb.Min.X), N(bb.Min.Y), N(bb.Min.Z), N(bb.Max.X), N(bb.Max.Y), N(bb.Max.Z) });
                        else cells.AddRange(new[] { "", "", "", "", "", "" });
                        cells.Add(geoms.Count.ToString(Inv)); cells.Add(skipped.Count.ToString(Inv));

                        if (cat != "Ducts") { cells.Add(""); for (int k = 0; k < 13; k++) cells.Add(""); w.WriteLine(Csv(cells)); continue; }
                        ducts++;
                        var pts = new List<double>();   // x y z per corner, world: the duct's own geometry only
                        try
                        {
                            foreach (ModelItem g in geoms) pts.AddRange(Part(pw, guid, g, true));
                            foreach (ModelItem g in skipped) Part(pw, guid, g, false);
                        }
                        catch (Exception ex) { triErrors++; if (firstTriError == null) firstTriError = ex.GetType().Name + ": " + ex.Message; }
                        int nTri = pts.Count / 9;
                        cells.Add(nTri.ToString(Inv));
                        string flag = "";
                        if (nTri == 0) { for (int k = 0; k < 12; k++) cells.Add(""); cells.Add("no triangles"); w.WriteLine(Csv(cells)); continue; }

                        tri.Write(rows); tri.Write(nTri);
                        foreach (double v in pts) tri.Write((float)v);

                        double[] e1, e2, a; double fa, fb;
                        Fit(pts, out e1, out e2, out a, out fa, out fb);
                        double fitLen = Dist(e1, e2);
                        fitted++;
                        double le = double.IsNaN(lenFt) ? double.NaN : fitLen - lenFt;
                        if (!double.IsNaN(le)) lenErr.Add(Math.Abs(le));
                        double se = double.NaN;
                        double sA = dia > 0 ? dia : wIn, sB = dia > 0 ? dia : hIn;
                        if (!double.IsNaN(sA) && !double.IsNaN(sB))
                            se = Math.Min(Math.Max(Math.Abs(fa * 12 - sA), Math.Abs(fb * 12 - sB)), Math.Max(Math.Abs(fa * 12 - sB), Math.Abs(fb * 12 - sA)));
                        double be = double.NaN;
                        if (hasBb) { be = BboxGap(pts, bb); bbErr.Add(be); }
                        if (!double.IsNaN(le) && Math.Abs(le) > 0.05) flag = "length";
                        if (!double.IsNaN(se) && se > 0.5) flag += (flag == "" ? "" : "+") + "section";
                        if (fitLen < Math.Max(fa, fb)) flag += (flag == "" ? "" : "+") + "short";
                        cells.AddRange(new[] { N(e1[0]), N(e1[1]), N(e1[2]), N(e2[0]), N(e2[1]), N(e2[2]), N(fitLen), N(fa * 12), N(fb * 12),
                            N(le), N(se), N(be), flag });
                        w.WriteLine(Csv(cells));
                    }
                }
            }
            finally { Autodesk.Navisworks.Api.Application.EndProgress(); }

            lenErr.Sort(); bbErr.Sort();
            var s = new StringBuilder();
            s.AppendLine("Pluto ducts " + t0.ToString("yyyy-MM-dd HH:mm:ss", Inv) + (cancelled ? "  ** CANCELLED: partial **" : ""));
            s.AppendLine("Document: " + (string.IsNullOrEmpty(doc.FileName) ? "(unsaved)" : doc.FileName) + "   units " + doc.Units);
            s.AppendLine(string.Format(Inv, "Search {0} IfcGUID items in {1:0}s; read {2}; rows {3}; total {4:0}s", total, searchSec, seen, rows, (DateTime.Now - t0).TotalSeconds));
            foreach (KeyValuePair<string, int> kv in byCat) s.AppendLine(string.Format(Inv, "  {0,8}  {1}", kv.Value, kv.Key));
            s.AppendLine(string.Format(Inv, "Ducts {0}, fitted from triangles {1}, triangle errors {2}{3}", ducts, fitted, triErrors,
                firstTriError == null ? "" : " (first: " + firstTriError + ")"));
            s.AppendLine("|fit length - .Length| ft:   " + Stats(lenErr));
            s.AppendLine("triangles vs bbox gap ft:    " + Stats(bbErr) + "   (~0 = transform right)");
            File.WriteAllText(Path.Combine(dir, "summary.txt"), s.ToString(), new UTF8Encoding(false));
            MessageBox.Show(s.ToString() + "\nWritten to:\n" + dir, "Pluto Ducts");
            return 0;
        }

        // ---- properties: every tab, keyed "Tab|Property" (display names) ----
        static Dictionary<string, string> Props(ModelItem it)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (PropertyCategory pc in it.PropertyCategories)
                foreach (DataProperty dp in pc.Properties)
                {
                    string v;
                    try { v = dp.Value == null ? "" : dp.Value.ToDisplayString(); } catch (Exception) { v = ""; }
                    d[pc.DisplayName + "|" + dp.DisplayName] = v;
                }
            return d;
        }

        static string Get(Dictionary<string, string> p, string key) { string v; return p.TryGetValue(key, out v) ? v : ""; }

        // IfcObjectProperties.<name>, wherever it sits: a property "IfcObjectProperties.<name>" on any tab
        // (seen under Custom), or a tab "IfcObjectProperties" with property "<name>".
        static string Ifc(Dictionary<string, string> p, string name)
        {
            string v;
            if (p.TryGetValue("IfcObjectProperties|" + name, out v)) return v;
            string suffix = "|IfcObjectProperties." + name;
            foreach (KeyValuePair<string, string> kv in p) if (kv.Key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            return "";
        }

        // "1,234.5 ft" / ".25 in" / "6 in" / "150 mm" -> feet times perFt (1 = feet, 12 = inches); no unit = feet.
        // NaN when there is no number.
        static readonly Regex NumUnit = new Regex(@"([-+]?[\d,]*\.?\d+)\s*(ft|feet|'|in|inch|inches|""|mm|cm|m)?", RegexOptions.IgnoreCase);
        static double Len(string s, double perFt)
        {
            if (string.IsNullOrEmpty(s)) return double.NaN;
            Match m = NumUnit.Match(s);
            if (!m.Success) return double.NaN;
            double v;
            if (!double.TryParse(m.Groups[1].Value.Replace(",", ""), NumberStyles.Float, Inv, out v)) return double.NaN;
            string u = m.Groups[2].Value.ToLowerInvariant();
            double ft = u == "in" || u == "inch" || u == "inches" || u == "\"" ? v / 12 : u == "mm" ? v / 304.8 : u == "cm" ? v / 30.48 : u == "m" ? v / 0.3048 : v;
            return ft * perFt;
        }

        static void ParseSize(string s, out double wIn, out double hIn)
        {
            wIn = hIn = double.NaN;
            if (string.IsNullOrEmpty(s)) return;
            string[] parts = s.Split(new[] { 'x', 'X', '×' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2) { wIn = Len(parts[0], 12); hIn = Len(parts[1], 12); }
        }

        // Geometry under an element that belongs to it: the walk stops at a descendant carrying a different
        // IfcGUID (another element, with its own row); that subtree's geometry goes to `skipped`.
        // (2026-10-05: a 5.147 ft duct had 2 geometry items, 7.92 ft together, both 10x8.)
        static void Collect(ModelItem node, string guid, bool isRoot, List<ModelItem> own, List<ModelItem> skipped)
        {
            if (!isRoot)
            {
                DataProperty dp = node.PropertyCategories.FindPropertyByDisplayName("Element", "IfcGUID");
                string g = "";
                try { if (dp != null && dp.Value != null) g = dp.Value.ToDisplayString(); } catch (Exception) { }
                if (g != "" && g != guid)
                {
                    foreach (ModelItem d in node.DescendantsAndSelf) if (d.HasGeometry) skipped.Add(d);
                    return;
                }
            }
            if (node.HasGeometry) own.Add(node);
            foreach (ModelItem c in node.Children) Collect(c, guid, false, own, skipped);
        }

        // One duct_parts.csv row per geometry item under a duct (its own fit); returns its triangles.
        static List<double> Part(StreamWriter pw, string ductGuid, ModelItem g, bool used)
        {
            var pts = new List<double>();
            Triangles(g, pts);
            string pg = "", pc = "";
            DataProperty dp = g.PropertyCategories.FindPropertyByDisplayName("Element", "IfcGUID");
            DataProperty dc = g.PropertyCategories.FindPropertyByDisplayName("Element", "Category");
            try { if (dp != null && dp.Value != null) pg = dp.Value.ToDisplayString(); if (dc != null && dc.Value != null) pc = dc.Value.ToDisplayString(); } catch (Exception) { }
            var cells = new List<string> { ductGuid, used ? "1" : "0", g.DisplayName, g.ClassDisplayName, pg, pc, (pts.Count / 9).ToString(Inv) };
            if (pts.Count >= 9)
            {
                double[] e1, e2, a; double fa, fb;
                Fit(pts, out e1, out e2, out a, out fa, out fb);
                cells.AddRange(new[] { N(Dist(e1, e2)), N(fa * 12), N(fb * 12), N(e1[0]), N(e1[1]), N(e1[2]), N(e2[0]), N(e2[1]), N(e2[2]) });
            }
            pw.WriteLine(Csv(cells));
            return pts;
        }

        // ---- triangles (COM API), world coordinates ----
        static void Triangles(ModelItem g, List<double> pts)
        {
            ComApi.InwOaPath path = ComBridge.ToInwOaPath(g);
            foreach (ComApi.InwOaFragment3 frag in path.Fragments())
            {
                var m = new double[16];
                Array ma = (Array)((ComApi.InwLTransform3f3)frag.GetLocalToWorldMatrix()).Matrix;
                int lo = ma.GetLowerBound(0);
                for (int k = 0; k < 16; k++) m[k] = Convert.ToDouble(ma.GetValue(lo + k));
                var cb = new TriCollector(m, pts);
                frag.GenerateSimplePrimitives(ComApi.nwEVertexProperty.eNORMAL, cb);
            }
        }

        // Row-vector convention: world = [x y z 1] * M, M row-major (translation in m[12..14]).
        class TriCollector : ComApi.InwSimplePrimitivesCB
        {
            readonly double[] m; readonly List<double> pts;
            public TriCollector(double[] m, List<double> pts) { this.m = m; this.pts = pts; }
            void Add(ComApi.InwSimpleVertex v)
            {
                Array c = (Array)v.coord;
                int lo = c.GetLowerBound(0);
                double x = Convert.ToDouble(c.GetValue(lo)), y = Convert.ToDouble(c.GetValue(lo + 1)), z = Convert.ToDouble(c.GetValue(lo + 2));
                pts.Add(x * m[0] + y * m[4] + z * m[8] + m[12]);
                pts.Add(x * m[1] + y * m[5] + z * m[9] + m[13]);
                pts.Add(x * m[2] + y * m[6] + z * m[10] + m[14]);
            }
            public void Triangle(ComApi.InwSimpleVertex v1, ComApi.InwSimpleVertex v2, ComApi.InwSimpleVertex v3) { Add(v1); Add(v2); Add(v3); }
            public void Line(ComApi.InwSimpleVertex v1, ComApi.InwSimpleVertex v2) { }
            public void Point(ComApi.InwSimpleVertex v1) { }
            public void SnapPoint(ComApi.InwSimpleVertex v1) { }
        }

        // ---- straight-member fit: principal axis of the vertices (power iteration on the covariance);
        // endpoints = centroid + axis * (min, max projection); cross-section extents along two
        // perpendiculars (horizontal one first, i.e. width / height for a horizontal duct), in doc units.
        static void Fit(List<double> tri, out double[] e1, out double[] e2, out double[] a, out double fa, out double fb)
        {
            // unique vertices first: a corner is shared by an uneven number of triangles, which weights it
            // unevenly and tilts the axis (a 30 deg box duct came out 5.171 ft / 11.1 x 8.9 in for 5.147 / 10 x 8)
            var pts = new List<double>();
            var seen = new HashSet<string>();
            for (int i = 0; i + 2 < tri.Count; i += 3)
                if (seen.Add(string.Format(Inv, "{0:F5},{1:F5},{2:F5}", tri[i], tri[i + 1], tri[i + 2])))
                { pts.Add(tri[i]); pts.Add(tri[i + 1]); pts.Add(tri[i + 2]); }
            int n = pts.Count / 3;
            double cx = 0, cy = 0, cz = 0;
            for (int i = 0; i < n; i++) { cx += pts[3 * i]; cy += pts[3 * i + 1]; cz += pts[3 * i + 2]; }
            cx /= n; cy /= n; cz /= n;
            double xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
            for (int i = 0; i < n; i++)
            {
                double dx = pts[3 * i] - cx, dy = pts[3 * i + 1] - cy, dz = pts[3 * i + 2] - cz;
                xx += dx * dx; xy += dx * dy; xz += dx * dz; yy += dy * dy; yz += dy * dz; zz += dz * dz;
            }
            a = new double[] { 1, 1, 1 };
            if (xx >= yy && xx >= zz) a = new double[] { 1, 0.01, 0.01 }; else if (yy >= zz) a = new double[] { 0.01, 1, 0.01 }; else a = new double[] { 0.01, 0.01, 1 };
            for (int it = 0; it < 100; it++)
            {
                double[] b = { xx * a[0] + xy * a[1] + xz * a[2], xy * a[0] + yy * a[1] + yz * a[2], xz * a[0] + yz * a[1] + zz * a[2] };
                double l = Math.Sqrt(b[0] * b[0] + b[1] * b[1] + b[2] * b[2]);
                if (l < 1e-30) break;
                a = new double[] { b[0] / l, b[1] / l, b[2] / l };
            }
            double[] u = Math.Abs(a[2]) > 0.99 ? Cross(a, new double[] { 1, 0, 0 }) : Cross(new double[] { 0, 0, 1 }, a);   // horizontal perpendicular
            Norm(u);
            double[] v = Cross(a, u); Norm(v);
            double tmin = double.MaxValue, tmax = double.MinValue, umin = double.MaxValue, umax = double.MinValue, vmin = double.MaxValue, vmax = double.MinValue;
            for (int i = 0; i < n; i++)
            {
                double dx = pts[3 * i] - cx, dy = pts[3 * i + 1] - cy, dz = pts[3 * i + 2] - cz;
                double t = dx * a[0] + dy * a[1] + dz * a[2], pu = dx * u[0] + dy * u[1] + dz * u[2], pv = dx * v[0] + dy * v[1] + dz * v[2];
                if (t < tmin) tmin = t; if (t > tmax) tmax = t;
                if (pu < umin) umin = pu; if (pu > umax) umax = pu;
                if (pv < vmin) vmin = pv; if (pv > vmax) vmax = pv;
            }
            double uc = (umin + umax) / 2, vc = (vmin + vmax) / 2;   // section centre (centroid of a box's vertices can sit off it)
            double ox = cx + u[0] * uc + v[0] * vc, oy = cy + u[1] * uc + v[1] * vc, oz = cz + u[2] * uc + v[2] * vc;
            e1 = new double[] { ox + a[0] * tmin, oy + a[1] * tmin, oz + a[2] * tmin };
            e2 = new double[] { ox + a[0] * tmax, oy + a[1] * tmax, oz + a[2] * tmax };
            fa = umax - umin; fb = vmax - vmin;
        }

        static double BboxGap(List<double> pts, BoundingBox3D bb)
        {
            double[] lo = { double.MaxValue, double.MaxValue, double.MaxValue }, hi = { double.MinValue, double.MinValue, double.MinValue };
            for (int i = 0; i < pts.Count; i++) { int k = i % 3; if (pts[i] < lo[k]) lo[k] = pts[i]; if (pts[i] > hi[k]) hi[k] = pts[i]; }
            double g = Math.Abs(lo[0] - bb.Min.X);
            g = Math.Max(g, Math.Abs(lo[1] - bb.Min.Y)); g = Math.Max(g, Math.Abs(lo[2] - bb.Min.Z));
            g = Math.Max(g, Math.Abs(hi[0] - bb.Max.X)); g = Math.Max(g, Math.Abs(hi[1] - bb.Max.Y)); g = Math.Max(g, Math.Abs(hi[2] - bb.Max.Z));
            return g;
        }

        static double[] Cross(double[] p, double[] q) { return new double[] { p[1] * q[2] - p[2] * q[1], p[2] * q[0] - p[0] * q[2], p[0] * q[1] - p[1] * q[0] }; }
        static void Norm(double[] p) { double l = Math.Sqrt(p[0] * p[0] + p[1] * p[1] + p[2] * p[2]); if (l > 0) { p[0] /= l; p[1] /= l; p[2] /= l; } }
        static double Dist(double[] p, double[] q) { double dx = p[0] - q[0], dy = p[1] - q[1], dz = p[2] - q[2]; return Math.Sqrt(dx * dx + dy * dy + dz * dz); }

        static string Stats(List<double> v)
        {
            if (v.Count == 0) return "n/a";
            return string.Format(Inv, "n {0}, median {1:0.####}, p95 {2:0.####}, max {3:0.####}", v.Count, v[v.Count / 2], v[(int)(0.95 * (v.Count - 1))], v[v.Count - 1]);
        }

        static string N(double v) { return double.IsNaN(v) ? "" : v.ToString("R", Inv); }

        static string Csv(List<string> cells)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < cells.Count; i++)
            {
                if (i > 0) sb.Append(',');
                string c = cells[i] ?? "";
                if (c.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0) sb.Append('"').Append(c.Replace("\"", "\"\"")).Append('"');
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
