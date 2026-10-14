using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace PlutoRevit
{
    // Read-only: the triangulated solids of every model element (any model category: the model is HVAC only;
    // the overlay's run / no-run split sets the rest aside) whose bounding box touches one of the boxes in
    // C:\Temp\hvac\revit\mesh-boxes.txt. That file is written by the overlay
    // (Export-DuctsViewer.ps1 -Revit <run> -Room <codes>): one box per room in Revit internal feet, plus the fitted
    // Revit -> Navisworks frame, so this run converts with a plain -Run. Revit draws its views the same way: the
    // model holds solids (faces, edges) and the view triangulates them; Face.Triangulate gives those triangles.
    // No view needed (works from any opening view). Writes C:\Temp\hvac\revit-mesh\<yyyyMMdd-HHmmss>\:
    //   mesh_items.csv  Row, ElementId, UniqueId, IfcGUID, Category, Family, Type, RunName, SystemName, IsFabrication,
    //                   Host (insulation / lining: the duct it covers), Box (first box touched), triangles total / kept
    //   mesh_tri.bin    per element with kept triangles: int32 row (1-based mesh_items.csv data row), int32 nTri,
    //                   nTri * 9 float32 (Revit internal feet); only triangles touching a box
    //   mesh-boxes.txt  the box file used (carries the fit)
    //   summary.txt     NEW block, counts by category, errors
    // C# 5 (built by Build-RevitPlugin.ps1).
    [Transaction(TransactionMode.ReadOnly)]
    public class ExportMesh : IExternalCommand
    {
        const string BoxFile = @"C:\Temp\hvac\revit\mesh-boxes.txt";
        const string OutRoot = @"C:\Temp\hvac\revit-mesh";
        const long MaxTriangles = 10000000;   // stop (flagged) past this: the overlay would be unusable anyway
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        class Box { public string Name; public double[] Lo, Hi; }

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            Document doc = data.Application.ActiveUIDocument.Document;
            if (!File.Exists(BoxFile))
            {
                TaskDialog.Show("Pluto Mesh", "No " + BoxFile + ".\n\nRun the overlay first (Export-DuctsViewer.ps1 -Run <ducts run> -Revit <revit run> -Room <codes>): it writes the room boxes.");
                return Result.Cancelled;
            }
            string[] boxLines = File.ReadAllLines(BoxFile);
            var boxes = new List<Box>();
            foreach (string line in boxLines)
            {
                string[] t = line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (t.Length != 8 || t[0] != "box") continue;
                var b = new Box { Name = t[1], Lo = new double[3], Hi = new double[3] };
                bool ok = true;
                for (int a = 0; a < 3; a++)
                    ok &= double.TryParse(t[2 + a], NumberStyles.Float, Inv, out b.Lo[a]) & double.TryParse(t[5 + a], NumberStyles.Float, Inv, out b.Hi[a]);
                if (ok) boxes.Add(b);
            }
            if (boxes.Count == 0) { TaskDialog.Show("Pluto Mesh", "No 'box' lines in " + BoxFile + "."); return Result.Cancelled; }

            DateTime t0 = DateTime.Now;
            string dir = Path.Combine(OutRoot, t0.ToString("yyyyMMdd-HHmmss", Inv));
            Directory.CreateDirectory(dir);
            File.WriteAllLines(Path.Combine(dir, "mesh-boxes.txt"), boxLines);

            var boxFilters = new List<ElementFilter>();
            foreach (Box b in boxes) boxFilters.Add(new BoundingBoxIntersectsFilter(new Outline(new XYZ(b.Lo[0], b.Lo[1], b.Lo[2]), new XYZ(b.Hi[0], b.Hi[1], b.Hi[2]))));
            ElementFilter inBoxes = boxFilters.Count == 1 ? boxFilters[0] : new LogicalOrFilter(boxFilters);
            // Assemblies are skipped: an assembly instance's geometry is its members' again, so it doubled
            // the members' triangles (most of a multi-million-triangle no-run set in a production run)
            var els = new List<Element>();
            int skippedAssemblies = 0;
            foreach (Element e in new FilteredElementCollector(doc).WhereElementIsNotElementType().WherePasses(inBoxes))
            {
                if (e.Category == null || e.Category.CategoryType != CategoryType.Model) continue;
                if (e.Category.Id.Value == (long)BuiltInCategory.OST_Assemblies || e is AssemblyInstance) { skippedAssemblies++; continue; }
                els.Add(e);
            }

            var opt = new Options();
            opt.DetailLevel = ViewDetailLevel.Fine; opt.ComputeReferences = false; opt.IncludeNonVisibleObjects = false;
            var byCat = new SortedDictionary<string, long[]>();   // elements, with triangles, triangles kept
            var errors = new List<string>();
            int rows = 0, written = 0, noRun = 0, noTri = 0; long tris = 0, trisTotal = 0; bool capped = false;
            var sb = new StringBuilder();
            sb.AppendLine("Row,ElementId,UniqueId,IfcGUID,Category,Family,Type,RunName,SystemName,IsFabrication,Host,Box,TrianglesTotal,TrianglesKept");
            using (var bw = new BinaryWriter(File.Create(Path.Combine(dir, "mesh_tri.bin"))))
            {
                foreach (Element el in els)
                {
                    if (capped) break;
                    try
                    {
                        string cat = el.Category != null ? el.Category.Name : "";
                        bool fab = el is FabricationPart;
                        string fam = "", typ = "";
                        if (fab)
                        {
                            var fp = (FabricationPart)el;
                            fam = ExportConnectors.Safe(delegate { return fp.ProductName; });
                            typ = ExportConnectors.Safe(delegate { return fp.Alias; });
                        }
                        else
                        {
                            var et = doc.GetElement(el.GetTypeId()) as ElementType;
                            if (et != null) { fam = et.FamilyName; typ = et.Name; }
                        }
                        // insulation / lining carry no run name: take the covered duct's
                        Element host = null;
                        var ins = el as InsulationLiningBase;
                        if (ins != null) host = doc.GetElement(ins.HostElementId);
                        string rn = ExportConnectors.RunName(el);
                        if (rn == "" && host != null) rn = ExportConnectors.RunName(host);
                        string sys = ExportConnectors.Param(el, BuiltInParameter.RBS_SYSTEM_NAME_PARAM);
                        if (sys == "" && host != null) sys = ExportConnectors.Param(host, BuiltInParameter.RBS_SYSTEM_NAME_PARAM);

                        var pts = new List<double>();
                        Collect(el.get_Geometry(opt), pts);
                        int total = pts.Count / 9;
                        var kept = new List<float>();
                        string firstBox = "";
                        for (int t = 0; t + 8 < pts.Count; t += 9)
                        {
                            double[] lo = { Math.Min(pts[t], Math.Min(pts[t + 3], pts[t + 6])), Math.Min(pts[t + 1], Math.Min(pts[t + 4], pts[t + 7])), Math.Min(pts[t + 2], Math.Min(pts[t + 5], pts[t + 8])) };
                            double[] hi = { Math.Max(pts[t], Math.Max(pts[t + 3], pts[t + 6])), Math.Max(pts[t + 1], Math.Max(pts[t + 4], pts[t + 7])), Math.Max(pts[t + 2], Math.Max(pts[t + 5], pts[t + 8])) };
                            Box hit = null;
                            foreach (Box b in boxes) if (hi[0] >= b.Lo[0] && lo[0] <= b.Hi[0] && hi[1] >= b.Lo[1] && lo[1] <= b.Hi[1] && hi[2] >= b.Lo[2] && lo[2] <= b.Hi[2]) { hit = b; break; }
                            if (hit == null) continue;
                            if (firstBox == "") firstBox = hit.Name;
                            for (int k = 0; k < 9; k++) kept.Add((float)pts[t + k]);
                        }
                        int nKept = kept.Count / 9;
                        rows++; tris += nKept; trisTotal += total;
                        if (rn == "") noRun++;
                        if (total == 0) noTri++;
                        long[] cc; if (!byCat.TryGetValue(cat, out cc)) { cc = new long[3]; byCat[cat] = cc; }
                        cc[0]++; if (nKept > 0) cc[1]++; cc[2] += nKept;
                        sb.Append(rows.ToString(Inv)).Append(',').Append(el.Id.ToString()).Append(',').Append(ExportConnectors.Q(el.UniqueId)).Append(',')
                          .Append(ExportConnectors.Q(ExportConnectors.IfcGuid(el))).Append(',').Append(ExportConnectors.Q(cat)).Append(',')
                          .Append(ExportConnectors.Q(fam)).Append(',').Append(ExportConnectors.Q(typ)).Append(',').Append(ExportConnectors.Q(rn)).Append(',')
                          .Append(ExportConnectors.Q(sys)).Append(',').Append(fab ? "1" : "0").Append(',').Append(host != null ? host.Id.ToString() : "").Append(',')
                          .Append(ExportConnectors.Q(firstBox)).Append(',').Append(total.ToString(Inv)).Append(',').Append(nKept.ToString(Inv)).AppendLine();
                        if (nKept > 0)
                        {
                            written++;
                            bw.Write(rows); bw.Write(nKept);
                            foreach (float v in kept) bw.Write(v);
                        }
                        if (tris > MaxTriangles) capped = true;
                    }
                    catch (Exception ex) { errors.Add(el.Id.ToString() + ": " + ex.GetType().Name + " " + ex.Message); }
                }
            }
            File.WriteAllText(Path.Combine(dir, "mesh_items.csv"), sb.ToString());

            var s = new StringBuilder();
            s.AppendLine(string.Format(Inv, "NEW mesh el {0} with tri {1} tri {2} norun {3} nogeom {4} err {5} boxes {6}{7}", rows, written, K(tris), noRun, noTri, errors.Count, boxes.Count, capped ? " CAPPED" : ""));
            s.AppendLine();
            s.AppendLine("PlutoRevit ExportMesh  " + t0.ToString("yyyy-MM-dd HH:mm:ss", Inv) + (capped ? "  ** STOPPED at " + MaxTriangles + " triangles: partial, use fewer rooms **" : ""));
            s.AppendLine("document: " + doc.PathName);
            s.AppendLine(string.Format(Inv, "elements touching the boxes {0}, with triangles in a box {1}; triangles kept {2} of {3}; no run name {4}; no geometry {5}; errors {6}; assemblies skipped {8}; {7:0.0} s",
                rows, written, tris, trisTotal, noRun, noTri, errors.Count, (DateTime.Now - t0).TotalSeconds, skippedAssemblies));
            s.AppendLine("boxes:"); foreach (Box b in boxes) s.AppendLine("  " + b.Name);
            s.AppendLine("by category (elements, with triangles, triangles kept):");
            foreach (var kv in byCat) s.AppendLine(string.Format(Inv, "  {0,7} {1,7} {2,9}  {3}", kv.Value[0], kv.Value[1], kv.Value[2], kv.Key));
            if (errors.Count > 0) { s.AppendLine("\nerrors (first 50):"); for (int i = 0; i < errors.Count && i < 50; i++) s.AppendLine("  " + errors[i]); }
            File.WriteAllText(Path.Combine(dir, "summary.txt"), s.ToString());
            TaskDialog.Show("Pluto Mesh", string.Format(Inv, "{0} elements, {1} triangles, {2} errors.\n\n{3}", rows, tris, errors.Count, dir));
            return Result.Succeeded;
        }

        // Solids are triangulated face by face; family instances arrive as GeometryInstance (GetInstanceGeometry is
        // already in model coordinates); a Mesh is taken as is.
        static void Collect(GeometryElement ge, List<double> pts)
        {
            if (ge == null) return;
            foreach (GeometryObject o in ge)
            {
                var gi = o as GeometryInstance;
                if (gi != null) { Collect(gi.GetInstanceGeometry(), pts); continue; }
                var so = o as Solid;
                if (so != null)
                {
                    if (so.Faces.Size == 0) continue;
                    foreach (Face f in so.Faces) { Mesh fm = null; try { fm = f.Triangulate(); } catch { } Add(fm, pts); }
                    continue;
                }
                Add(o as Mesh, pts);
            }
        }

        static void Add(Mesh m, List<double> pts)
        {
            if (m == null) return;
            for (int i = 0; i < m.NumTriangles; i++)
            {
                MeshTriangle t = m.get_Triangle(i);
                for (int v = 0; v < 3; v++) { XYZ p = t.get_Vertex(v); pts.Add(p.X); pts.Add(p.Y); pts.Add(p.Z); }
            }
        }

        static string K(long v) { return v >= 1000000 ? (v / 1000000.0).ToString("0.#", Inv) + "M" : v >= 1000 ? (v / 1000.0).ToString("0.#", Inv) + "k" : v.ToString(Inv); }
    }
}
