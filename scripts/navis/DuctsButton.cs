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
        const string OutRoot = @"C:\Temp\hvac\ducts", FabOutRoot = @"C:\Temp\hvac\fab";
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        // Revit categories kept (matched ignoring case; written in this spelling). Fabrication parts are their
        // own categories: straights and fittings in MEP Fabrication Ductwork (treated like Ducts: triangles,
        // fit, centrelines), supports in MEP Fabrication Hangers (like accessories: properties + bbox).
        const string Fab = "MEP Fabrication Ductwork", FabHangers = "MEP Fabrication Hangers";
        static readonly string[] Keep = { "Ducts", "Duct Fittings", "Duct Accessories", Fab, FabHangers };
        static string KeepCat(string c)
        {
            foreach (string k in Keep) if (string.Equals(k, (c ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) return k;
            return null;
        }
        // Accessory geometry is heavy (finely tessellated parts; the triangles must be generated to reach the
        // centreline): a run with it did ~15 % of the items per hour (2026-10-05). Off until accessories matter.
        const bool AccessoryGeometry = false;

        public override int Execute(params string[] parameters) { return Run(false); }

        // fabOnly: only elements whose Element/Category contains "Fabrication" (the Pluto Fab button; seconds
        // instead of minutes), written under C:\Temp\hvac\fab\<run>.
        // Fabrication parts (2026-10-05): the Revit element node carries Element / Custom tabs, IfcGUID and
        // Category "MEP Fabrication Ductwork"; under it a composite object with only an Item tab (what a click
        // selects), then the mesh. So the IfcGUID search finds them like any other element. Their IfcGUID is
        // NOT unique (parts of one fabrication assembly share it): rows are keyed by NavisId.
        internal static int Run(bool fabOnly)
        {
            string title = fabOnly ? "Pluto Fab" : "Pluto Ducts";
            Document doc = Autodesk.Navisworks.Api.Application.ActiveDocument;
            if (doc == null || doc.Models.Count == 0) { MessageBox.Show("Open a model first.", title); return 0; }
            DateTime t0 = DateTime.Now;
            ForeignFragments = 0;
            string dir = Path.Combine(fabOnly ? FabOutRoot : OutRoot, t0.ToString("yyyyMMdd-HHmmss", Inv));
            Directory.CreateDirectory(dir);
            string log = Path.Combine(dir, "started.txt");
            File.WriteAllText(log, t0.ToString("yyyy-MM-dd HH:mm:ss", Inv) + "\r\n");

            int total = 0, seen = 0, rows = 0, ducts = 0, fitted = 0, triErrors = 0;
            var byCat = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var catSec = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);   // time per category (to the next kept item)
            var clock = new System.Diagnostics.Stopwatch(); string lastCat = null, lastGuid = "", lastFam = ""; int lastTri = 0;
            var lenErr = new List<double>(); var bbErr = new List<double>();
            var segs = new List<Seg>();
            var guidUse = new Dictionary<string, int>(StringComparer.Ordinal);   // IfcGUID -> rows carrying it
            var propCensus = new SortedDictionary<string, string[]>(StringComparer.Ordinal);   // "cat|tab|prop" -> {count, sample}
            var cl = new List<ClSeg>();                                       // centreline segments, all kept categories
            var clNone = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);   // elements without a line, by category
            var lineVsFit = new List<double>();
            var hiddenByCat = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);       // element hidden (self / ancestor)
            var partHiddenByCat = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);   // element visible, some own geometry hidden
            int hiddenSelf = 0, geomTotal = 0, geomHidden = 0;
            double searchSec = 0; bool cancelled = false; string firstTriError = null;
            Progress progress = Autodesk.Navisworks.Api.Application.BeginProgress(title + ": searching");
            try
            {
                progress.Update(0);
                Search search = new Search();
                search.Selection.SelectAll();
                search.Locations = SearchLocations.DescendantsAndSelf;
                search.SearchConditions.Add(fabOnly
                    ? SearchCondition.HasPropertyByDisplayName("Element", "Category").DisplayStringContains("Fabrication")
                    : SearchCondition.HasPropertyByDisplayName("Element", "IfcGUID"));
                ModelItemCollection found = search.FindAll(doc, false);
                total = found.Count;
                searchSec = (DateTime.Now - t0).TotalSeconds;
                File.AppendAllText(log, string.Format(Inv, "search {0}: {1} items in {2:0}s\r\n", fabOnly ? "Element/Category ~ Fabrication" : "IfcGUID", total, searchSec));

                using (var w = new StreamWriter(Path.Combine(dir, "ducts.csv"), false, new UTF8Encoding(false)))
                using (var pw = new StreamWriter(Path.Combine(dir, "duct_parts.csv"), false, new UTF8Encoding(false)))
                using (var tri = new BinaryWriter(File.Create(Path.Combine(dir, "ducts_tri.bin"))))
                {
                    pw.WriteLine("DuctIfcGUID,DuctNavisId,Used,Hidden,DisplayName,ClassDisplayName,PartIfcGUID,PartCategory,Triangles,FitLength_ft,FitA_in,FitB_in,X1,Y1,Z1,X2,Y2,Z2,Lines,LineTotal_ft,LX1,LY1,LZ1,LX2,LY2,LZ2");
                    w.WriteLine(string.Join(",", new[] {
                        "IfcGUID", "NavisId", "Category", "Family", "Type", "Name", "SystemName", "SystemType", "Shape", "Material",
                        "Width_in", "Height_in", "Diameter_in", "WallThk_in", "InsulThk_in", "Length_ft", "Size", "Location",
                        "MinX", "MinY", "MinZ", "MaxX", "MaxY", "MaxZ", "OwnGeom", "SkippedGeom", "Triangles",
                        "X1", "Y1", "Z1", "X2", "Y2", "Z2", "FitLength_ft", "FitA_in", "FitB_in",
                        "LenErr_ft", "SizeErr_in", "TriVsBbox_ft", "Flag", "Hidden", "HiddenGeom" }));
                    foreach (ModelItem it in found)
                    {
                        seen++;
                        if (seen % 200 == 0 && !progress.Update(total > 0 ? (double)seen / total : 0)) { cancelled = true; break; }
                        if (seen % 1000 == 0)   // live log: open started.txt during a run to see where the time goes
                            File.AppendAllText(log, string.Format(Inv, "{0:HH:mm:ss}  {1}/{2}  last category {3}  rows {4}  ducts {5}\r\n",
                                DateTime.Now, seen, total, lastCat ?? "-", rows, ducts));
                        // category first (one lookup); every property only for the items kept (insulation and
                        // the rest would otherwise each pay a full property read)
                        DataProperty cp = it.PropertyCategories.FindPropertyByDisplayName("Element", "Category");
                        string cat0 = "";
                        try { if (cp != null && cp.Value != null) cat0 = cp.Value.ToDisplayString(); } catch (Exception) { }
                        string cat = KeepCat(cat0);
                        if (cat == null) continue;
                        Dictionary<string, string> p = Props(it);
                        foreach (KeyValuePair<string, string> kv in p)   // property census per category
                        {
                            string key = cat + "|" + kv.Key;
                            string[] st;
                            if (!propCensus.TryGetValue(key, out st)) { st = new[] { "0", "" }; propCensus[key] = st; }
                            st[0] = (int.Parse(st[0], Inv) + 1).ToString(Inv);
                            if (st[1] == "" && !string.IsNullOrEmpty(kv.Value)) st[1] = kv.Value;
                        }
                        rows++;
                        int n; byCat.TryGetValue(cat, out n); byCat[cat] = n + 1;
                        if (lastCat != null)
                        {
                            double el = clock.Elapsed.TotalSeconds;
                            double sec; catSec.TryGetValue(lastCat, out sec); catSec[lastCat] = sec + el;
                            if (el > 2)   // slow element (plus any skipped items after it): name it
                                File.AppendAllText(log, string.Format(Inv, "{0:HH:mm:ss}  SLOW {1:0.0}s  {2}  {3}  family '{4}'  triangles {5}\r\n",
                                    DateTime.Now, el, lastCat, lastGuid, lastFam, lastTri));
                        }
                        clock.Reset(); clock.Start(); lastCat = cat; lastTri = 0;

                        double wIn, hIn;
                        string size = Ifc(p, "Size");
                        ParseSize(size, out wIn, out hIn);
                        double dia = Len(Ifc(p, "DuctDiameter"), 12);
                        double lenFt = Len(Ifc(p, "Length"), 1);
                        if (double.IsNaN(lenFt)) lenFt = Len(Ifc(p, "DuctLength"), 1);
                        BoundingBox3D bb = it.BoundingBox();
                        bool hasBb = bb != null && !bb.IsEmpty;

                        string guid = Get(p, "Element|IfcGUID");
                        // unique key: Navisworks' item id (stable until the file is re-exported); IfcGUID is not
                        // unique (fabrication assembly parts share one)
                        string navisId = it.InstanceGuid != Guid.Empty ? it.InstanceGuid.ToString("N") : "row" + rows.ToString(Inv);
                        if (guid != "") { int gu; guidUse.TryGetValue(guid, out gu); guidUse[guid] = gu + 1; }
                        lastGuid = guid + " (" + navisId + ")"; lastFam = Get(p, "Element|Family");
                        string material = Ifc(p, "Material");
                        if (material == "") material = Get(p, "Item|Material");
                        var geoms = new List<ModelItem>(); var skipped = new List<ModelItem>();
                        Collect(it, guid, true, geoms, skipped);
                        // hidden state (the search includes hidden items): the element itself / an ancestor, and
                        // how many of its own geometry items are hidden (2026-10-06: suspect for the 8 in round
                        // "Straight" that cannot be seen or selected in Navisworks)
                        string hid = it.IsHidden ? "self" : AncestorHidden(it) ? "ancestor" : "";
                        int hidGeom = 0;
                        foreach (ModelItem g in geoms) if (hid != "" || HiddenBelow(g, it)) hidGeom++;
                        if (hid != "") { int hc; hiddenByCat.TryGetValue(cat, out hc); hiddenByCat[cat] = hc + 1; if (hid == "self") hiddenSelf++; }
                        else if (hidGeom > 0) { int hc; partHiddenByCat.TryGetValue(cat, out hc); partHiddenByCat[cat] = hc + 1; }
                        geomTotal += geoms.Count; geomHidden += hidGeom;
                        string hidCell = hid, hidGeomCell = hidGeom.ToString(Inv);
                        Action<List<string>> emit = delegate(List<string> c) { c.Add(hidCell); c.Add(hidGeomCell); w.WriteLine(Csv(c)); };

                        var cells = new List<string> {
                            guid, navisId, cat, Get(p, "Element|Family"), Get(p, "Element|Type"), it.DisplayName,
                            Get(p, "Element|System Name"), Get(p, "Element|System Type"), Ifc(p, "Shape"), material,
                            N(wIn), N(hIn), N(dia), N(Len(Ifc(p, "Wall Thickness"), 12)), N(Len(Ifc(p, "Insulation Thickness"), 12)),
                            N(lenFt), size, Ifc(p, "Location") };
                        if (hasBb) cells.AddRange(new[] { N(bb.Min.X), N(bb.Min.Y), N(bb.Min.Z), N(bb.Max.X), N(bb.Max.Y), N(bb.Max.Z) });
                        else cells.AddRange(new[] { "", "", "", "", "", "" });
                        cells.Add(geoms.Count.ToString(Inv)); cells.Add(skipped.Count.ToString(Inv));

                        if ((cat == "Duct Accessories" && !AccessoryGeometry) || cat == FabHangers) { cells.Add(""); for (int k = 0; k < 13; k++) cells.Add(""); emit(cells); continue; }
                        if (cat != "Ducts" && cat != Fab)
                        {
                            var fl = new List<double>();
                            var ft = new List<double>();
                            try { foreach (ModelItem g in geoms) { ft.Clear(); Triangles(g, ft, fl); lastTri += ft.Count / 9; } }
                            catch (Exception ex) { triErrors++; if (firstTriError == null) firstTriError = ex.GetType().Name + ": " + ex.Message; }
                            AddCl(cl, clNone, rows, guid, cat, fl);
                            cells.Add(""); for (int k = 0; k < 13; k++) cells.Add(""); emit(cells); continue;
                        }
                        ducts++;
                        var pts = new List<double>();   // x y z per corner, world: the duct's own geometry only
                        var dl = new List<double>();    // its centreline segments
                        try
                        {
                            foreach (ModelItem g in geoms) pts.AddRange(Part(pw, guid, navisId, g, true, hid != "" || HiddenBelow(g, it), dl));
                            foreach (ModelItem g in skipped) SkippedPart(pw, guid, navisId, g, hid != "" || HiddenBelow(g, it));   // listed only, no triangles (cost)
                        }
                        catch (Exception ex) { triErrors++; if (firstTriError == null) firstTriError = ex.GetType().Name + ": " + ex.Message; }
                        AddCl(cl, clNone, rows, guid, cat, dl);
                        int nTri = pts.Count / 9;
                        lastTri = nTri;
                        cells.Add(nTri.ToString(Inv));
                        string flag = "";
                        if (nTri == 0) { for (int k = 0; k < 12; k++) cells.Add(""); cells.Add("no triangles"); emit(cells); continue; }

                        tri.Write(rows); tri.Write(nTri);
                        foreach (double v in pts) tri.Write((float)v);

                        double[] e1, e2, a; double fa, fb;
                        Fit(pts, out e1, out e2, out a, out fa, out fb);
                        double fitLen = Dist(e1, e2);
                        fitted++;
                        segs.Add(new Seg { Row = rows, Guid = guid, E1 = e1, E2 = e2, A = Sub(e2, e1, fitLen), Len = fitLen, LenProp = lenFt, Half = Math.Max(fa, fb) / 2 });
                        if (dl.Count >= 6) lineVsFit.Add(LineVsFit(dl, e1, e2));
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
                        emit(cells);
                    }
                }
            }
            finally { Autodesk.Navisworks.Api.Application.EndProgress(); }

            List<double> endVals, trimErr; int contained;
            Ends(segs, Path.Combine(dir, "duct_ends.csv"), out endVals, out trimErr, out contained);
            string graph = Graph(cl, dir);
            using (var pw2 = new StreamWriter(Path.Combine(dir, "property-names-by-category.csv"), false, new UTF8Encoding(false)))
            {
                pw2.WriteLine("Category,Tab,Property,Count,Sample");
                foreach (KeyValuePair<string, string[]> kv in propCensus)
                {
                    string[] k = kv.Key.Split(new[] { '|' }, 3);
                    pw2.WriteLine(Csv(new List<string> { k[0], k[1], k.Length > 2 ? k[2] : "", kv.Value[0], kv.Value[1] }));
                }
            }
            lineVsFit.Sort();
            lenErr.Sort(); bbErr.Sort(); endVals.Sort(); trimErr.Sort();
            var s = new StringBuilder();
            s.AppendLine(title + " " + t0.ToString("yyyy-MM-dd HH:mm:ss", Inv) + (cancelled ? "  ** CANCELLED: partial **" : ""));
            s.AppendLine("Document: " + (string.IsNullOrEmpty(doc.FileName) ? "(unsaved)" : doc.FileName) + "   units " + doc.Units);
            s.AppendLine(string.Format(Inv, "Search {0} IfcGUID items in {1:0}s; read {2}; rows {3}; total {4:0}s", total, searchSec, seen, rows, (DateTime.Now - t0).TotalSeconds));
            if (lastCat != null) { double sec; catSec.TryGetValue(lastCat, out sec); catSec[lastCat] = sec + clock.Elapsed.TotalSeconds; }
            foreach (KeyValuePair<string, int> kv in byCat)
            {
                double sec; catSec.TryGetValue(kv.Key, out sec);
                s.AppendLine(string.Format(Inv, "  {0,8}  {1}   {2:0}s", kv.Value, kv.Key, sec));
            }
            if (!AccessoryGeometry) s.AppendLine("  (Duct Accessories: properties + bbox only, no geometry / centrelines)");
            s.AppendLine("  (" + FabHangers + ": properties + bbox only; " + Fab + ": like Ducts)");
            s.AppendLine(string.Format(Inv, "Geometry fragments of other instances skipped (shared geometry): {0}", ForeignFragments));
            s.AppendLine(string.Format(Inv, "Ducts {0}, fitted from triangles {1}, triangle errors {2}{3}", ducts, fitted, triErrors,
                firstTriError == null ? "" : " (first: " + firstTriError + ")"));
            s.AppendLine("|fit length - .Length| ft:   " + Stats(lenErr));
            s.AppendLine("triangles vs bbox gap ft:    " + Stats(bbErr) + "   (> 0 where another element's geometry was skipped)");
            s.AppendLine("duct-duct end overlap ft:    " + Stats(endVals) + "   (+ overlap, - gap; collinear neighbours only)");
            s.AppendLine("|half-trimmed len - .Length|:" + Stats(trimErr) + "   (ducts with a neighbour at both ends; ~0 = joints at overlap midpoints)");
            if (contained > 0) s.AppendLine(string.Format(Inv, "ducts lying inside another duct: {0}", contained));
            s.AppendLine("duct centreline vs fit ends ft:" + Stats(lineVsFit) + "   (~0 = Revit centreline = fitted solid)");
            {
                int sharedIds = 0, sharedRows = 0;
                foreach (int u in guidUse.Values) if (u > 1) { sharedIds++; sharedRows += u; }
                s.AppendLine(string.Format(Inv, "IfcGUIDs shared by more than one row: {0} ({1} rows); rows are keyed by NavisId", sharedIds, sharedRows));
            }
            {
                var hb = new StringBuilder();
                foreach (KeyValuePair<string, int> kv in hiddenByCat) hb.Append(string.Format(Inv, "  {0} {1}", kv.Key, kv.Value));
                var pb = new StringBuilder();
                foreach (KeyValuePair<string, int> kv in partHiddenByCat) pb.Append(string.Format(Inv, "  {0} {1}", kv.Key, kv.Value));
                int hiddenAll = 0; foreach (int v in hiddenByCat.Values) hiddenAll += v;
                s.AppendLine(string.Format(Inv, "hidden elements {0} (self {1}, under a hidden ancestor {2}):{3}", hiddenAll, hiddenSelf, hiddenAll - hiddenSelf, hb.Length > 0 ? hb.ToString() : "  none"));
                s.AppendLine(string.Format(Inv, "visible elements with hidden own geometry:{0}", pb.Length > 0 ? pb.ToString() : "  none"));
                s.AppendLine(string.Format(Inv, "own geometry items hidden {0} of {1}   (ducts.csv Hidden / HiddenGeom, duct_parts.csv Hidden)", geomHidden, geomTotal));
            }
            s.Append(graph);
            if (clNone.Count > 0)
            {
                s.Append("elements without a centreline:");
                foreach (KeyValuePair<string, int> kv in clNone) s.Append(string.Format(Inv, "  {0} {1}", kv.Key, kv.Value));
                s.AppendLine();
            }
            File.WriteAllText(Path.Combine(dir, "summary.txt"), s.ToString(), new UTF8Encoding(false));
            MessageBox.Show(s.ToString() + "\nWritten to:\n" + dir, title);
            return 0;
        }

        // ---- centreline graph (Revit centrelines come through as line primitives on a 0-triangle geometry
        // item: one line per straight duct, = the duct's length; 2026-10-05) ----
        class ClSeg { public int Row; public string Guid, Cat; public double[] P, Q; }

        static void AddCl(List<ClSeg> cl, SortedDictionary<string, int> none, int row, string guid, string cat, List<double> lines)
        {
            int n = 0;
            for (int i = 0; i + 5 < lines.Count; i += 6)
            {
                double[] p = { lines[i], lines[i + 1], lines[i + 2] }, q = { lines[i + 3], lines[i + 4], lines[i + 5] };
                if (Dist(p, q) < 1e-6) continue;
                cl.Add(new ClSeg { Row = row, Guid = guid, Cat = cat, P = p, Q = q });
                n++;
            }
            if (n == 0) { int c; none.TryGetValue(cat, out c); none[cat] = c + 1; }
        }

        // max distance between the fitted ends and the extreme centreline points along the fitted axis
        static double LineVsFit(List<double> lines, double[] e1, double[] e2)
        {
            double len = Dist(e1, e2);
            double[] a = Sub(e2, e1, len);
            double tmin = double.MaxValue, tmax = double.MinValue; double[] pmin = null, pmax = null;
            for (int i = 0; i + 2 < lines.Count; i += 3)
            {
                double[] p = { lines[i], lines[i + 1], lines[i + 2] };
                double t = Dot(new[] { p[0] - e1[0], p[1] - e1[1], p[2] - e1[2] }, a);
                if (t < tmin) { tmin = t; pmin = p; }
                if (t > tmax) { tmax = t; pmax = p; }
            }
            return Math.Max(Dist(pmin, e1), Dist(pmax, e2));
        }

        // Segment ends become nodes. Within one element, ends merge only when they coincide (1e-6 ft): an
        // elbow's arc is many short segments, and a 0.02 ft tolerance there collapsed whole arcs into one node
        // (degree up to 90, 2026-10-05). Across elements, ends within Tol merge (the nearest node that holds
        // no end of this element). Writes cl_nodes.csv (Node, X, Y, Z, Degree, Categories) and
        // cl_segments.csv (Seg, Row, IfcGUID, Category, Node1, Node2, Length_ft); returns summary lines.
        static string Graph(List<ClSeg> cl, string dir)
        {
            const double Tol = 0.02, Same = 1e-6;
            var nodes = new List<double[]>(); var degree = new List<int>(); var cats = new List<SortedSet<string>>();
            var rowsAt = new List<HashSet<int>>();
            var grid = new Dictionary<long, List<int>>();
            var segNodes = new int[cl.Count, 2];
            for (int i = 0; i < cl.Count; i++)
                for (int e = 0; e < 2; e++)
                {
                    double[] p = e == 0 ? cl[i].P : cl[i].Q;
                    int hit = -1; double best = double.MaxValue;
                    for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) for (int dz = -1; dz <= 1; dz++)
                    {
                        List<int> l;
                        if (!grid.TryGetValue(Key(p[0] + dx * Tol, p[1] + dy * Tol, p[2] + dz * Tol, Tol), out l)) continue;
                        foreach (int k in l)
                        {
                            double d = Dist(nodes[k], p);
                            bool ok = d <= Same || (d <= Tol && !rowsAt[k].Contains(cl[i].Row));
                            if (ok && d < best) { best = d; hit = k; }
                        }
                    }
                    if (hit < 0)
                    {
                        hit = nodes.Count; nodes.Add(p); degree.Add(0); cats.Add(new SortedSet<string>()); rowsAt.Add(new HashSet<int>());
                        long key = Key(p[0], p[1], p[2], Tol);
                        List<int> l; if (!grid.TryGetValue(key, out l)) { l = new List<int>(); grid[key] = l; } l.Add(hit);
                    }
                    degree[hit]++; cats[hit].Add(cl[i].Cat); rowsAt[hit].Add(cl[i].Row);
                    segNodes[i, e] = hit;
                }
            var segByCat = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            using (var w = new StreamWriter(Path.Combine(dir, "cl_segments.csv"), false, new UTF8Encoding(false)))
            {
                w.WriteLine("Seg,Row,IfcGUID,Category,Node1,Node2,Length_ft");
                for (int i = 0; i < cl.Count; i++)
                {
                    int c; segByCat.TryGetValue(cl[i].Cat, out c); segByCat[cl[i].Cat] = c + 1;
                    w.WriteLine(Csv(new List<string> { (i + 1).ToString(Inv), cl[i].Row.ToString(Inv), cl[i].Guid, cl[i].Cat,
                        (segNodes[i, 0] + 1).ToString(Inv), (segNodes[i, 1] + 1).ToString(Inv), N(Dist(cl[i].P, cl[i].Q)) }));
                }
            }
            var byDeg = new SortedDictionary<int, int>();
            using (var w = new StreamWriter(Path.Combine(dir, "cl_nodes.csv"), false, new UTF8Encoding(false)))
            {
                w.WriteLine("Node,X,Y,Z,Degree,Categories");
                for (int k = 0; k < nodes.Count; k++)
                {
                    int c; byDeg.TryGetValue(degree[k], out c); byDeg[degree[k]] = c + 1;
                    w.WriteLine(Csv(new List<string> { (k + 1).ToString(Inv), N(nodes[k][0]), N(nodes[k][1]), N(nodes[k][2]),
                        degree[k].ToString(Inv), string.Join(";", new List<string>(cats[k]).ToArray()) }));
                }
            }
            var sb = new StringBuilder();
            sb.Append(string.Format(Inv, "centreline segments {0}:", cl.Count));
            foreach (KeyValuePair<string, int> kv in segByCat) sb.Append(string.Format(Inv, "  {0} {1}", kv.Key, kv.Value));
            sb.AppendLine();
            sb.Append(string.Format(Inv, "centreline nodes {0} (tol {1} ft) by degree:", nodes.Count, Tol));
            foreach (KeyValuePair<int, int> kv in byDeg) sb.Append(string.Format(Inv, "  {0}:{1}", kv.Key, kv.Value));
            sb.AppendLine("   (1 = loose end, 2 = run-through / bend, 3+ = branch)");
            return sb.ToString();
        }

        class Seg { public int Row; public string Guid; public double[] E1, E2, A; public double Len, LenProp, Half; }

        static double[] Sub(double[] q, double[] p, double len)
        {
            return len > 0 ? new double[] { (q[0] - p[0]) / len, (q[1] - p[1]) / len, (q[2] - p[2]) / len } : new double[] { 1, 0, 0 };
        }

        // Duct-to-duct end relations from the fitted solids: for each end, the collinear neighbour (axes
        // within 2 deg, lateral offset < 0.1 ft) that reaches past it. Value at the end: + overlap (how far
        // the neighbour runs into this duct), - gap (up to 0.5 ft). Trimmed length = fitted length minus
        // half of each end's overlap (both ends having a neighbour), compared with .Length.
        // Writes duct_ends.csv: Row, IfcGUID, End1_ft, End1Nbr, End2_ft, End2Nbr, Fit_ft, Trim_ft, Length_ft.
        static void Ends(List<Seg> segs, string path, out List<double> endVals, out List<double> trimErr, out int contained)
        {
            endVals = new List<double>(); trimErr = new List<double>(); contained = 0;
            const double cell = 4.0, cosTol = 0.99939, latTol = 0.1, gapTol = 0.5;
            var grid = new Dictionary<long, List<int>>();
            for (int i = 0; i < segs.Count; i++)
            {
                Seg sg = segs[i];
                int steps = (int)Math.Ceiling(sg.Len / (cell / 2)) + 1;
                var cellsOf = new HashSet<long>();
                for (int k = 0; k <= steps; k++)
                {
                    double t = sg.Len * k / steps;
                    cellsOf.Add(Key(sg.E1[0] + sg.A[0] * t, sg.E1[1] + sg.A[1] * t, sg.E1[2] + sg.A[2] * t, cell));
                }
                foreach (long c in cellsOf) { List<int> l; if (!grid.TryGetValue(c, out l)) { l = new List<int>(); grid[c] = l; } l.Add(i); }
            }
            using (var w = new StreamWriter(path, false, new UTF8Encoding(false)))
            {
                w.WriteLine("Row,IfcGUID,End1_ft,End1Nbr,End2_ft,End2Nbr,Fit_ft,Trim_ft,Length_ft");
                for (int i = 0; i < segs.Count; i++)
                {
                    Seg s = segs[i];
                    var cand = new HashSet<int>();
                    foreach (double[] e in new[] { s.E1, s.E2 })
                        for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) for (int dz = -1; dz <= 1; dz++)
                        {
                            List<int> l;
                            if (grid.TryGetValue(Key(e[0] + dx * cell, e[1] + dy * cell, e[2] + dz * cell, cell), out l)) foreach (int j in l) if (j != i) cand.Add(j);
                        }
                    double v1 = double.NaN, v2 = double.NaN; string n1 = "", n2 = "";
                    bool inside = false;
                    foreach (int j in cand)
                    {
                        Seg o = segs[j];
                        if (Math.Abs(Dot(s.A, o.A)) < cosTol) continue;
                        // lateral offset of o's midpoint from s's axis
                        double[] mid = { (o.E1[0] + o.E2[0]) / 2 - s.E1[0], (o.E1[1] + o.E2[1]) / 2 - s.E1[1], (o.E1[2] + o.E2[2]) / 2 - s.E1[2] };
                        double tm = Dot(mid, s.A);
                        double lx = mid[0] - s.A[0] * tm, ly = mid[1] - s.A[1] * tm, lz = mid[2] - s.A[2] * tm;
                        if (Math.Sqrt(lx * lx + ly * ly + lz * lz) > latTol) continue;
                        double ta = Dot(new[] { o.E1[0] - s.E1[0], o.E1[1] - s.E1[1], o.E1[2] - s.E1[2] }, s.A);
                        double tb = Dot(new[] { o.E2[0] - s.E1[0], o.E2[1] - s.E1[1], o.E2[2] - s.E1[2] }, s.A);
                        double lo = Math.Min(ta, tb), hi = Math.Max(ta, tb);
                        if (lo <= 1e-6 && hi >= s.Len - 1e-6) { inside = true; continue; }
                        if (lo < 0 && hi > -gapTol && hi < s.Len && (double.IsNaN(v1) || hi > v1)) { v1 = hi; n1 = o.Guid; }
                        if (hi > s.Len && lo < s.Len + gapTol && lo > 0 && (double.IsNaN(v2) || s.Len - lo > v2)) { v2 = s.Len - lo; n2 = o.Guid; }
                    }
                    if (inside) contained++;
                    if (!double.IsNaN(v1)) endVals.Add(v1);
                    if (!double.IsNaN(v2)) endVals.Add(v2);
                    double trim = double.NaN;
                    if (!double.IsNaN(v1) && !double.IsNaN(v2))
                    {
                        trim = s.Len - Math.Max(v1, 0) / 2 - Math.Max(v2, 0) / 2;
                        if (!double.IsNaN(s.LenProp)) trimErr.Add(Math.Abs(trim - s.LenProp));
                    }
                    w.WriteLine(Csv(new List<string> { s.Row.ToString(Inv), s.Guid, N(v1), n1, N(v2), n2, N(s.Len), N(trim), N(s.LenProp) }));
                }
            }
        }

        static long Key(double x, double y, double z, double cell)
        {
            long ix = (long)Math.Floor(x / cell), iy = (long)Math.Floor(y / cell), iz = (long)Math.Floor(z / cell);
            return (ix * 73856093L) ^ (iy * 19349663L) ^ (iz * 83492791L);
        }

        static double Dot(double[] p, double[] q) { return p[0] * q[0] + p[1] * q[1] + p[2] * q[2]; }

        // ---- properties: every tab, keyed "Tab|Property" (display names) ----
        internal static Dictionary<string, string> Props(ModelItem it)
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

        internal static string Get(Dictionary<string, string> p, string key) { string v; return p.TryGetValue(key, out v) ? v : ""; }

        // IfcObjectProperties.<name>, wherever it sits: a property "IfcObjectProperties.<name>" on any tab
        // (seen under Custom), or a tab "IfcObjectProperties" with property "<name>".
        internal static string Ifc(Dictionary<string, string> p, string name)
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

        // Geometry under an element that belongs to it: the walk stops at a descendant carrying its own IfcGUID,
        // even the same one (it is a found item with its own row; IfcGUIDs are shared between fabrication
        // assembly parts); that subtree's geometry goes to `skipped`.
        static void Collect(ModelItem node, string guid, bool isRoot, List<ModelItem> own, List<ModelItem> skipped)
        {
            if (!isRoot)
            {
                DataProperty dp = node.PropertyCategories.FindPropertyByDisplayName("Element", "IfcGUID");
                string g = "";
                try { if (dp != null && dp.Value != null) g = dp.Value.ToDisplayString(); } catch (Exception) { }
                if (g != "")
                {
                    foreach (ModelItem d in node.DescendantsAndSelf) if (d.HasGeometry) skipped.Add(d);
                    return;
                }
            }
            if (node.HasGeometry) own.Add(node);
            foreach (ModelItem c in node.Children) Collect(c, guid, false, own, skipped);
        }

        // Any ancestor hidden (the item then is not drawn, though its own flag is clear).
        static bool AncestorHidden(ModelItem it)
        {
            for (ModelItem a = it.Parent; a != null; a = a.Parent) if (a.IsHidden) return true;
            return false;
        }

        // Hidden at or below `top` on the way down to g (g itself included, `top` excluded).
        static bool HiddenBelow(ModelItem g, ModelItem top)
        {
            for (ModelItem a = g; a != null && !a.Equals(top); a = a.Parent) if (a.IsHidden) return true;
            return false;
        }

        // duct_parts.csv row for another element's geometry under a duct: name / class / its IfcGUID and category
        static void SkippedPart(StreamWriter pw, string ductGuid, string ductId, ModelItem g, bool hidden)
        {
            string pg = "", pc = "";
            DataProperty dp = g.PropertyCategories.FindPropertyByDisplayName("Element", "IfcGUID");
            DataProperty dc = g.PropertyCategories.FindPropertyByDisplayName("Element", "Category");
            try { if (dp != null && dp.Value != null) pg = dp.Value.ToDisplayString(); if (dc != null && dc.Value != null) pc = dc.Value.ToDisplayString(); } catch (Exception) { }
            pw.WriteLine(Csv(new List<string> { ductGuid, ductId, "0", hidden ? "1" : "0", g.DisplayName, g.ClassDisplayName, pg, pc }));
        }

        // One duct_parts.csv row per geometry item under a duct (its own fit); returns its triangles and
        // appends its line segments to linesOut when given.
        static List<double> Part(StreamWriter pw, string ductGuid, string ductId, ModelItem g, bool used, bool hidden, List<double> linesOut)
        {
            var pts = new List<double>(); var lines = new List<double>();
            Triangles(g, pts, lines);
            if (linesOut != null) linesOut.AddRange(lines);
            string pg = "", pc = "";
            DataProperty dp = g.PropertyCategories.FindPropertyByDisplayName("Element", "IfcGUID");
            DataProperty dc = g.PropertyCategories.FindPropertyByDisplayName("Element", "Category");
            try { if (dp != null && dp.Value != null) pg = dp.Value.ToDisplayString(); if (dc != null && dc.Value != null) pc = dc.Value.ToDisplayString(); } catch (Exception) { }
            var cells = new List<string> { ductGuid, ductId, used ? "1" : "0", hidden ? "1" : "0", g.DisplayName, g.ClassDisplayName, pg, pc, (pts.Count / 9).ToString(Inv) };
            if (pts.Count >= 9)
            {
                double[] e1, e2, a; double fa, fb;
                Fit(pts, out e1, out e2, out a, out fa, out fb);
                cells.AddRange(new[] { N(Dist(e1, e2)), N(fa * 12), N(fb * 12), N(e1[0]), N(e1[1]), N(e1[2]), N(e2[0]), N(e2[1]), N(e2[2]) });
            }
            else for (int k = 0; k < 9; k++) cells.Add("");
            // line segments (0-triangle parts may be Revit centrelines): count, total length, the longest one
            int nl = lines.Count / 6; double tot = 0, best = -1; int bi = -1;
            for (int i = 0; i < nl; i++)
            {
                double l = Dist(new[] { lines[6 * i], lines[6 * i + 1], lines[6 * i + 2] }, new[] { lines[6 * i + 3], lines[6 * i + 4], lines[6 * i + 5] });
                tot += l; if (l > best) { best = l; bi = i; }
            }
            cells.Add(nl.ToString(Inv)); cells.Add(nl > 0 ? N(tot) : "");
            if (bi >= 0) for (int k = 0; k < 6; k++) cells.Add(N(lines[6 * bi + k]));
            pw.WriteLine(Csv(cells));
            return pts;
        }

        // ---- triangles (COM API), world coordinates ----
        static void Triangles(ModelItem g, List<double> pts) { Triangles(g, pts, null); }

        // triangles into pts; line segments (two corners each) into lines when given.
        // Instanced geometry (2026-10-07): identical parts (fabrication straights of one size and length)
        // share one geometry, and path.Fragments() returns the fragments of EVERY instance, each with its
        // own instance's transform. Only the fragments whose own path is this item's path are its geometry;
        // without the filter a straight got its twins' triangles (one 12.05 ft along the run), measured as
        // the wrong size and drawn on top of them.
        internal static void Triangles(ModelItem g, List<double> pts, List<double> lines)
        {
            ComApi.InwOaPath path = ComBridge.ToInwOaPath(g);
            Array own = (Array)path.ArrayData;
            foreach (ComApi.InwOaFragment3 frag in path.Fragments())
            {
                if (!SamePath((Array)frag.path.ArrayData, own)) { ForeignFragments++; continue; }
                var m = new double[16];
                Array ma = (Array)((ComApi.InwLTransform3f3)frag.GetLocalToWorldMatrix()).Matrix;
                int lo = ma.GetLowerBound(0);
                for (int k = 0; k < 16; k++) m[k] = Convert.ToDouble(ma.GetValue(lo + k));
                var cb = new TriCollector(m, pts, lines);
                frag.GenerateSimplePrimitives(ComApi.nwEVertexProperty.eNORMAL, cb);
            }
        }

        internal static int ForeignFragments;   // other instances' fragments skipped (summary)

        static bool SamePath(Array a, Array b)
        {
            if (a.Length != b.Length) return false;
            int la = a.GetLowerBound(0), lb = b.GetLowerBound(0);
            for (int i = 0; i < a.Length; i++)
                if (Convert.ToInt64(a.GetValue(la + i)) != Convert.ToInt64(b.GetValue(lb + i))) return false;
            return true;
        }

        // Row-vector convention: world = [x y z 1] * M, M row-major (translation in m[12..14]).
        class TriCollector : ComApi.InwSimplePrimitivesCB
        {
            readonly double[] m; readonly List<double> pts, lines;
            public TriCollector(double[] m, List<double> pts, List<double> lines) { this.m = m; this.pts = pts; this.lines = lines; }
            void Add(List<double> to, ComApi.InwSimpleVertex v)
            {
                Array c = (Array)v.coord;
                int lo = c.GetLowerBound(0);
                double x = Convert.ToDouble(c.GetValue(lo)), y = Convert.ToDouble(c.GetValue(lo + 1)), z = Convert.ToDouble(c.GetValue(lo + 2));
                to.Add(x * m[0] + y * m[4] + z * m[8] + m[12]);
                to.Add(x * m[1] + y * m[5] + z * m[9] + m[13]);
                to.Add(x * m[2] + y * m[6] + z * m[10] + m[14]);
            }
            public void Triangle(ComApi.InwSimpleVertex v1, ComApi.InwSimpleVertex v2, ComApi.InwSimpleVertex v3) { Add(pts, v1); Add(pts, v2); Add(pts, v3); }
            public void Line(ComApi.InwSimpleVertex v1, ComApi.InwSimpleVertex v2) { if (lines != null) { Add(lines, v1); Add(lines, v2); } }
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

        internal static string N(double v) { return double.IsNaN(v) ? "" : v.ToString("R", Inv); }

        internal static string Csv(List<string> cells)
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
