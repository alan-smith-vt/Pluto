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
    // Read-only inventory of the open model: every item carrying an Element-tab IfcGUID (Revit elements;
    // the stable id) -> one row. Writes <OutRoot>\<yyyyMMdd-HHmmss>\ (a new folder per run, never overwritten):
    //   items.csv           IfcGUID, source file, name, class, common Revit properties, geometry flag, bbox
    //   property-names.csv  every (tab, property) seen on those items: count + a sample value, to pick
    //                       the columns the extraction keeps
    //   summary.txt         models, units, item counts by source file and by category
    // Bounding boxes are in the document units (summary.txt). C# 5 (built by Build-NavisPlugin.ps1).
    [Plugin("InventoryButton", "Pluto",
        DisplayName = "Pluto Inventory",
        ToolTip = "Read-only: lists every item with an IfcGUID to C:\\Temp\\hvac\\inventory")]
    [AddInPlugin(AddInLocation.AddIn)]
    public class InventoryButton : AddInPlugin
    {
        const string OutRoot = @"C:\Temp\hvac\inventory";
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        // Element-tab properties written as columns when present (Revit names; empty when absent).
        static readonly string[] ElementCols = { "Category", "Family", "Type", "System Type", "System Name",
            "System Classification", "Size", "Width", "Height", "Diameter", "Level", "Reference Level" };

        class PropStat { public int Count; public string Sample; }

        public override int Execute(params string[] parameters)
        {
            Document doc = Autodesk.Navisworks.Api.Application.ActiveDocument;
            if (doc == null || doc.Models.Count == 0) { MessageBox.Show("Open a model first.", "Pluto Inventory"); return 0; }
            DateTime t0 = DateTime.Now;
            string dir = Path.Combine(OutRoot, t0.ToString("yyyyMMdd-HHmmss", Inv));
            Directory.CreateDirectory(dir);

            // pass 1: count, for the progress bar
            int total = 0;
            foreach (ModelItem root in doc.Models.RootItems)
                foreach (ModelItem it in root.DescendantsAndSelf) total++;

            var props = new SortedDictionary<string, PropStat>(StringComparer.Ordinal);
            var bySource = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var byCategory = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int seen = 0, rows = 0, withGeom = 0;
            bool cancelled = false;
            Progress progress = Autodesk.Navisworks.Api.Application.BeginProgress("Pluto inventory");
            try
            {
                using (var w = new StreamWriter(Path.Combine(dir, "items.csv"), false, new UTF8Encoding(false)))
                {
                    var head = new List<string> { "IfcGUID", "InstanceGuid", "SourceFile", "DisplayName", "ClassDisplayName" };
                    head.AddRange(ElementCols);
                    head.AddRange(new[] { "HasGeometry", "MinX", "MinY", "MinZ", "MaxX", "MaxY", "MaxZ" });
                    w.WriteLine(Csv(head));
                    foreach (ModelItem root in doc.Models.RootItems)
                    {
                        foreach (ModelItem it in root.DescendantsAndSelf)
                        {
                            seen++;
                            if (seen % 2000 == 0 && !progress.Update(total > 0 ? (double)seen / total : 0)) { cancelled = true; break; }
                            string guid = Prop(it, "Element", "IfcGUID");
                            if (string.IsNullOrEmpty(guid)) continue;
                            rows++;
                            string src = Prop(it, "Item", "Source File");
                            string cat = Prop(it, "Element", "Category");
                            Bump(bySource, src); Bump(byCategory, cat);
                            foreach (PropertyCategory pc in it.PropertyCategories)
                                foreach (DataProperty dp in pc.Properties)
                                {
                                    string key = pc.DisplayName + "\t" + dp.DisplayName;
                                    PropStat st;
                                    if (!props.TryGetValue(key, out st)) { st = new PropStat(); props[key] = st; }
                                    st.Count++;
                                    if (st.Sample == null) { string v = Value(dp); if (!string.IsNullOrEmpty(v)) st.Sample = v; }
                                }
                            var row = new List<string> { guid, it.InstanceGuid.ToString(), src, it.DisplayName, it.ClassDisplayName };
                            foreach (string c in ElementCols) row.Add(Prop(it, "Element", c));
                            row.Add(it.HasGeometry ? "1" : "0");
                            if (it.HasGeometry) withGeom++;
                            BoundingBox3D bb = it.BoundingBox();
                            if (bb == null || bb.IsEmpty) row.AddRange(new[] { "", "", "", "", "", "" });
                            else row.AddRange(new[] { N(bb.Min.X), N(bb.Min.Y), N(bb.Min.Z), N(bb.Max.X), N(bb.Max.Y), N(bb.Max.Z) });
                            w.WriteLine(Csv(row));
                        }
                        if (cancelled) break;
                    }
                }
            }
            finally { Autodesk.Navisworks.Api.Application.EndProgress(); }

            using (var w = new StreamWriter(Path.Combine(dir, "property-names.csv"), false, new UTF8Encoding(false)))
            {
                w.WriteLine("Tab,Property,Count,Sample");
                foreach (KeyValuePair<string, PropStat> kv in props)
                {
                    string[] tp = kv.Key.Split('\t');
                    w.WriteLine(Csv(new List<string> { tp[0], tp[1], kv.Value.Count.ToString(Inv), kv.Value.Sample ?? "" }));
                }
            }

            var s = new StringBuilder();
            s.AppendLine("Pluto inventory " + t0.ToString("yyyy-MM-dd HH:mm:ss", Inv) + (cancelled ? "  ** CANCELLED: partial **" : ""));
            s.AppendLine("Document: " + (string.IsNullOrEmpty(doc.FileName) ? "(unsaved)" : doc.FileName));
            s.AppendLine("Document units (bounding boxes): " + doc.Units);
            s.AppendLine(string.Format(Inv, "Items walked {0} of {1}; with IfcGUID {2} (with geometry {3}); {4:0}s",
                seen, total, rows, withGeom, (DateTime.Now - t0).TotalSeconds));
            s.AppendLine();
            s.AppendLine("Models (" + doc.Models.Count + "):");
            foreach (Model m in doc.Models) s.AppendLine("  " + m.FileName + "   [source: " + m.SourceFileName + ", units " + m.Units + "]");
            s.AppendLine();
            s.AppendLine("IfcGUID items by source file:");
            foreach (KeyValuePair<string, int> kv in bySource) s.AppendLine(string.Format(Inv, "  {0,8}  {1}", kv.Value, kv.Key == "" ? "(none)" : kv.Key));
            s.AppendLine();
            s.AppendLine("IfcGUID items by Element Category:");
            foreach (KeyValuePair<string, int> kv in byCategory) s.AppendLine(string.Format(Inv, "  {0,8}  {1}", kv.Value, kv.Key == "" ? "(none)" : kv.Key));
            File.WriteAllText(Path.Combine(dir, "summary.txt"), s.ToString(), new UTF8Encoding(false));

            MessageBox.Show(string.Format(Inv, "{0}{1} items with IfcGUID ({2} walked).\n\nWritten to:\n{3}",
                cancelled ? "CANCELLED, partial results.\n" : "", rows, seen, dir), "Pluto Inventory");
            return 0;
        }

        static string Prop(ModelItem it, string tab, string name)
        {
            DataProperty dp = it.PropertyCategories.FindPropertyByDisplayName(tab, name);
            return dp == null ? "" : Value(dp);
        }

        static string Value(DataProperty dp)
        {
            try { return dp.Value == null ? "" : dp.Value.ToDisplayString(); }
            catch (Exception) { return ""; }
        }

        static void Bump(SortedDictionary<string, int> d, string k)
        {
            int n; d.TryGetValue(k ?? "", out n); d[k ?? ""] = n + 1;
        }

        static string N(double v) { return v.ToString("R", Inv); }

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
