using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Plugins;

namespace PlutoNavis
{
    // Read-only mesh dump of EVERY geometry item inside a box (not only the duct / fabrication rows), to find
    // where in the tree a part's mesh hangs (2026-10-06: rectangular fabrication parts missing where an 8 in
    // round is drawn; too congested to navigate in Navisworks). No property search: the tree is walked from
    // the model roots and any subtree whose bounding box (hidden items included) misses the box is skipped,
    // so the cost scales with what is inside the box, not with the federation.
    // Box: a centre point and a half-size. The centre is typed as the Pluto viewer readout shows it (plant
    // E, N, EL in inches: the readout already adds the overlay's worldOffset) or in feet; Navisworks is
    // plant feet, so inches / 12 is the only conversion. Writes C:\Temp\hvac\box\<yyyyMMdd-HHmmss>\:
    //   box_items.csv  one row per geometry item: NavisId, DisplayName, ClassDisplayName, ClassName, hidden
    //                  (self / effective), the nearest ancestor-or-self with an IfcGUID (its GUID, category,
    //                  family, type, size, name, NavisId, levels up), the tree path from that element (or the
    //                  model root) down, triangles total / kept, line count, bbox (feet)
    //   box_tri.bin    per item with kept triangles: int32 row (1-based box_items.csv data row), int32 nTri,
    //                  nTri * 9 float32 (world feet); only triangles whose own bbox touches the box (a large
    //                  slab crossing the box does not bring all of itself)
    //   summary.txt    box, counts by element category, hidden counts, timings
    // Export-DuctsViewer.ps1 -Run <that folder> makes the viewer overlay (one shell group per category,
    // hidden ones separate; each triangle labelled with its item and element).
    [Plugin("BoxButton", "Pluto",
        DisplayName = "Pluto Box",
        ToolTip = "Read-only: every mesh inside a box (centre from the Pluto viewer readout) to C:\\Temp\\hvac\\box")]
    [AddInPlugin(AddInLocation.AddIn)]
    public class BoxButton : AddInPlugin
    {
        const string OutRoot = @"C:\Temp\hvac\box";
        const int MaxTriangles = 5000000;   // stop (flagged) past this: the overlay would be unusable anyway
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        class Anc { public string Guid = "", Cat = "", Fam = "", Type = "", Size = "", Name = "", Id = ""; public int Depth; }

        class State
        {
            public double[] Lo, Hi;           // box, document units
            public bool DuctsOnly;
            public StreamWriter W; public BinaryWriter Tri;
            public int Visited, Pruned, Rows, Written, HiddenRows, Errors; public long Tris, TrisTotal;
            public double Frac; public bool Cancelled, Capped; public string FirstError;
            public SortedDictionary<string, int[]> ByCat = new SortedDictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);   // rows, hidden rows, triangles
            public Progress Progress;
        }

        public override int Execute(params string[] parameters)
        {
            Document doc = Autodesk.Navisworks.Api.Application.ActiveDocument;
            if (doc == null || doc.Models.Count == 0) { MessageBox.Show("Open a model first.", "Pluto Box"); return 0; }
            double perFt = PerFoot(doc.Units);
            if (double.IsNaN(perFt)) { MessageBox.Show("Unsupported document units: " + doc.Units, "Pluto Box"); return 0; }

            double[] c; double halfFt; bool inches, ductsOnly;
            if (!Ask(out c, out inches, out halfFt, out ductsOnly)) return 0;
            double toFt = inches ? 1.0 / 12 : 1.0;
            double[] cDoc = { c[0] * toFt * perFt, c[1] * toFt * perFt, c[2] * toFt * perFt };
            double h = halfFt * perFt;

            DateTime t0 = DateTime.Now;
            string dir = Path.Combine(OutRoot, t0.ToString("yyyyMMdd-HHmmss", Inv));
            Directory.CreateDirectory(dir);
            var st = new State();
            st.Lo = new[] { cDoc[0] - h, cDoc[1] - h, cDoc[2] - h };
            st.Hi = new[] { cDoc[0] + h, cDoc[1] + h, cDoc[2] + h };
            st.DuctsOnly = ductsOnly;
            st.Progress = Autodesk.Navisworks.Api.Application.BeginProgress("Pluto Box");
            try
            {
                using (st.W = new StreamWriter(Path.Combine(dir, "box_items.csv"), false, new UTF8Encoding(false)))
                using (st.Tri = new BinaryWriter(File.Create(Path.Combine(dir, "box_tri.bin"))))
                {
                    st.W.WriteLine("Row,NavisId,DisplayName,ClassDisplayName,ClassName,HiddenSelf,Hidden,AncIfcGUID,AncCategory,AncFamily,AncType,AncSize,AncName,AncNavisId,AncLevelsUp,Path,TrianglesTotal,TrianglesKept,Lines,MinX,MinY,MinZ,MaxX,MaxY,MaxZ");
                    var roots = new List<ModelItem>();
                    foreach (ModelItem r in doc.Models.RootItems) roots.Add(r);
                    for (int i = 0; i < roots.Count && !st.Cancelled && !st.Capped; i++)
                    {
                        st.Frac = (double)i / roots.Count;
                        if (!st.Progress.Update(st.Frac)) { st.Cancelled = true; break; }
                        Walk(st, roots[i], new Anc(), false, roots[i].DisplayName, 0);
                    }
                }
            }
            finally { Autodesk.Navisworks.Api.Application.EndProgress(); }

            var s = new StringBuilder();
            s.AppendLine("Pluto Box " + t0.ToString("yyyy-MM-dd HH:mm:ss", Inv)
                + (st.Cancelled ? "  ** CANCELLED: partial **" : "") + (st.Capped ? "  ** STOPPED at " + MaxTriangles + " triangles: partial, use a smaller box **" : ""));
            s.AppendLine("Document: " + (string.IsNullOrEmpty(doc.FileName) ? "(unsaved)" : doc.FileName) + "   units " + doc.Units);
            s.AppendLine(string.Format(Inv, "Centre {0} {1} {2} {3}; half-size {4} ft{5}", N(c[0]), N(c[1]), N(c[2]), inches ? "in" : "ft", N(halfFt), ductsOnly ? "; only under duct / fabrication elements" : ""));
            s.AppendLine(string.Format(Inv, "Box (document units) min {0} {1} {2}  max {3} {4} {5}", N(st.Lo[0]), N(st.Lo[1]), N(st.Lo[2]), N(st.Hi[0]), N(st.Hi[1]), N(st.Hi[2])));
            s.AppendLine(string.Format(Inv, "Items visited {0}, subtrees pruned by bbox {1}; geometry items {2} (with triangles in the box {3}); hidden {4}; triangles kept {5} of {6}; {7:0}s",
                st.Visited, st.Pruned, st.Rows, st.Written, st.HiddenRows, st.Tris, st.TrisTotal, (DateTime.Now - t0).TotalSeconds));
            if (st.Errors > 0) s.AppendLine(string.Format(Inv, "triangle errors {0} (first: {1})", st.Errors, st.FirstError));
            s.AppendLine("By element category (items, hidden, triangles kept):");
            foreach (KeyValuePair<string, int[]> kv in st.ByCat)
                s.AppendLine(string.Format(Inv, "  {0,6} {1,5} {2,9}  {3}", kv.Value[0], kv.Value[1], kv.Value[2], kv.Key));
            File.WriteAllText(Path.Combine(dir, "summary.txt"), s.ToString(), new UTF8Encoding(false));
            MessageBox.Show(s.ToString() + "\nWritten to:\n" + dir, "Pluto Box");
            return 0;
        }

        // Depth-first; a subtree is skipped when its bbox (hidden items included) misses the box. The nearest
        // element (IfcGUID), hidden state and the path are carried down instead of walking up per item.
        static void Walk(State st, ModelItem it, Anc anc, bool hiddenAbove, string path, int depth)
        {
            if (st.Cancelled || st.Capped) return;
            st.Visited++;
            if (st.Visited % 2000 == 0 && !st.Progress.Update(st.Frac)) { st.Cancelled = true; return; }
            BoundingBox3D bb = it.BoundingBox(false);
            if (bb == null || bb.IsEmpty || !Touches(st, bb.Min.X, bb.Min.Y, bb.Min.Z, bb.Max.X, bb.Max.Y, bb.Max.Z)) { st.Pruned++; return; }

            DataProperty gp = it.PropertyCategories.FindPropertyByDisplayName("Element", "IfcGUID");
            string g = "";
            try { if (gp != null && gp.Value != null) g = gp.Value.ToDisplayString(); } catch (Exception) { }
            if (g != "")
            {
                Dictionary<string, string> p = DuctsButton.Props(it);
                anc = new Anc
                {
                    Guid = g, Cat = DuctsButton.Get(p, "Element|Category"), Fam = DuctsButton.Get(p, "Element|Family"),
                    Type = DuctsButton.Get(p, "Element|Type"), Size = DuctsButton.Ifc(p, "Size"), Name = it.DisplayName,
                    Id = NavisId(it), Depth = depth
                };
                path = it.DisplayName;
            }
            else if (depth > 0) path = path + " > " + it.DisplayName;
            bool hidden = hiddenAbove || it.IsHidden;

            if (it.HasGeometry && (!st.DuctsOnly || anc.Cat.IndexOf("Duct", StringComparison.OrdinalIgnoreCase) >= 0
                                                 || anc.Cat.IndexOf("Fabrication", StringComparison.OrdinalIgnoreCase) >= 0))
                Emit(st, it, anc, hidden, path, depth, bb);

            foreach (ModelItem ch in it.Children) Walk(st, ch, anc, hidden, path, depth + 1);
        }

        static void Emit(State st, ModelItem it, Anc anc, bool hidden, string path, int depth, BoundingBox3D bb)
        {
            st.Rows++;
            var pts = new List<double>(); var lines = new List<double>();
            try { DuctsButton.Triangles(it, pts, lines); }
            catch (Exception ex) { st.Errors++; if (st.FirstError == null) st.FirstError = ex.GetType().Name + ": " + ex.Message; }
            int total = pts.Count / 9;
            var kept = new List<float>();
            for (int t = 0; t + 8 < pts.Count; t += 9)
            {
                double x0 = Math.Min(pts[t], Math.Min(pts[t + 3], pts[t + 6])), x1 = Math.Max(pts[t], Math.Max(pts[t + 3], pts[t + 6]));
                double y0 = Math.Min(pts[t + 1], Math.Min(pts[t + 4], pts[t + 7])), y1 = Math.Max(pts[t + 1], Math.Max(pts[t + 4], pts[t + 7]));
                double z0 = Math.Min(pts[t + 2], Math.Min(pts[t + 5], pts[t + 8])), z1 = Math.Max(pts[t + 2], Math.Max(pts[t + 5], pts[t + 8]));
                if (!Touches(st, x0, y0, z0, x1, y1, z1)) continue;
                for (int k = 0; k < 9; k++) kept.Add((float)pts[t + k]);
            }
            int nKept = kept.Count / 9;
            st.TrisTotal += total; st.Tris += nKept;
            if (hidden) st.HiddenRows++;
            string cat = anc.Cat != "" ? anc.Cat : "(no element)";
            int[] cc; if (!st.ByCat.TryGetValue(cat, out cc)) { cc = new int[3]; st.ByCat[cat] = cc; }
            cc[0]++; if (hidden) cc[1]++; cc[2] += nKept;
            st.W.WriteLine(DuctsButton.Csv(new List<string> {
                st.Rows.ToString(Inv), NavisId(it), it.DisplayName, it.ClassDisplayName, it.ClassName, it.IsHidden ? "1" : "0", hidden ? "1" : "0",
                anc.Guid, anc.Cat, anc.Fam, anc.Type, anc.Size, anc.Name, anc.Id, anc.Guid != "" ? (depth - anc.Depth).ToString(Inv) : "",
                path, total.ToString(Inv), nKept.ToString(Inv), (lines.Count / 6).ToString(Inv),
                N(bb.Min.X), N(bb.Min.Y), N(bb.Min.Z), N(bb.Max.X), N(bb.Max.Y), N(bb.Max.Z) }));
            if (nKept > 0)
            {
                st.Written++;
                st.Tri.Write(st.Rows); st.Tri.Write(nKept);
                foreach (float v in kept) st.Tri.Write(v);
            }
            if (st.Tris > MaxTriangles) st.Capped = true;
        }

        static bool Touches(State st, double x0, double y0, double z0, double x1, double y1, double z1)
        {
            return x1 >= st.Lo[0] && x0 <= st.Hi[0] && y1 >= st.Lo[1] && y0 <= st.Hi[1] && z1 >= st.Lo[2] && z0 <= st.Hi[2];
        }

        static string NavisId(ModelItem it) { return it.InstanceGuid != Guid.Empty ? it.InstanceGuid.ToString("N") : ""; }

        static string N(double v) { return DuctsButton.N(v); }

        static double PerFoot(Units u)
        {
            switch (u)
            {
                case Units.Feet: return 1;
                case Units.Inches: return 12;
                case Units.Meters: return 0.3048;
                case Units.Centimeters: return 30.48;
                case Units.Millimeters: return 304.8;
                default: return double.NaN;
            }
        }

        // ---- the box dialog; last values kept in C:\Temp\hvac\box\last.txt ----
        static bool Ask(out double[] c, out bool inches, out double halfFt, out bool ductsOnly)
        {
            c = null; inches = true; halfFt = 10; ductsOnly = false;
            string last = Path.Combine(OutRoot, "last.txt");
            string centre = "", half = "10", unit = "in", only = "0";
            try
            {
                if (File.Exists(last))
                {
                    string[] l = File.ReadAllLines(last);
                    if (l.Length > 0) centre = l[0]; if (l.Length > 1) half = l[1]; if (l.Length > 2) unit = l[2]; if (l.Length > 3) only = l[3];
                }
            }
            catch (Exception) { }

            using (var f = new Form())
            {
                f.Text = "Pluto Box"; f.FormBorderStyle = FormBorderStyle.FixedDialog; f.MaximizeBox = false; f.MinimizeBox = false;
                f.StartPosition = FormStartPosition.CenterScreen; f.ClientSize = new System.Drawing.Size(460, 200);
                var l1 = new Label { Text = "Centre E, N, EL (paste the Pluto viewer readout):", Left = 12, Top = 14, Width = 430 };
                var tc = new TextBox { Text = centre, Left = 12, Top = 36, Width = 300 };
                var cu = new ComboBox { Left = 322, Top = 36, Width = 120, DropDownStyle = ComboBoxStyle.DropDownList };
                cu.Items.AddRange(new object[] { "inches (viewer)", "feet (Navisworks)" });
                cu.SelectedIndex = unit == "ft" ? 1 : 0;
                var l2 = new Label { Text = "Half-size (ft; the box is twice this along E, N and EL):", Left = 12, Top = 72, Width = 330 };
                var th = new TextBox { Text = half, Left = 342, Top = 69, Width = 100 };
                var cb = new CheckBox { Text = "Only geometry under duct / fabrication elements", Left = 12, Top = 104, Width = 430, Checked = only == "1" };
                var ok = new Button { Text = "Run", Left = 266, Top = 152, Width = 84, DialogResult = DialogResult.OK };
                var cancel = new Button { Text = "Cancel", Left = 358, Top = 152, Width = 84, DialogResult = DialogResult.Cancel };
                f.Controls.AddRange(new Control[] { l1, tc, cu, l2, th, cb, ok, cancel });
                f.AcceptButton = ok; f.CancelButton = cancel;
                while (true)
                {
                    if (f.ShowDialog() != DialogResult.OK) return false;
                    string[] parts = tc.Text.Split(new[] { ',', ' ', '\t', ';' }, StringSplitOptions.RemoveEmptyEntries);
                    double x, y, z, hh;
                    if (parts.Length == 3 && double.TryParse(parts[0], NumberStyles.Float, Inv, out x) && double.TryParse(parts[1], NumberStyles.Float, Inv, out y)
                        && double.TryParse(parts[2], NumberStyles.Float, Inv, out z) && double.TryParse(th.Text.Trim(), NumberStyles.Float, Inv, out hh) && hh > 0)
                    {
                        c = new[] { x, y, z }; inches = cu.SelectedIndex == 0; halfFt = hh; ductsOnly = cb.Checked;
                        try
                        {
                            Directory.CreateDirectory(OutRoot);
                            File.WriteAllLines(last, new[] { tc.Text.Trim(), th.Text.Trim(), inches ? "in" : "ft", ductsOnly ? "1" : "0" });
                        }
                        catch (Exception) { }
                        return true;
                    }
                    MessageBox.Show("Centre: three numbers (E, N, EL), e.g. 12345.67, 23456.78, 1234.50\nHalf-size: a positive number of feet.", "Pluto Box");
                }
            }
        }
    }
}
