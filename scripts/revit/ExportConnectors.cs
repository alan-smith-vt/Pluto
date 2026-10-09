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
    // Read-only: every HVAC element with connectors (fabrication parts, ducts, fittings, accessories, flex, terminals,
    // equipment) and every connector, from the open document. No view needed (works from any opening view).
    // Writes C:\Temp\hvac\revit\<yyyyMMdd-HHmmss>\ (new folder per run):
    //   elements.csv    ElementId, UniqueId, IfcGUID, category, family/product, type, size, system, level, connector count
    //   connectors.csv  owner ElementId, connector Id, type, domain, shape, origin (ft), direction (BasisZ),
    //                   width / height / diameter (in), connected-to ElementId:ConnectorId list (';')
    //   summary.txt     counts by category and by family/product, connector counts, unconnected ends, errors
    // Coordinates are Revit internal (feet, project internal origin). C# 5 (built by Build-RevitPlugin.ps1).
    [Transaction(TransactionMode.ReadOnly)]
    public class ExportConnectors : IExternalCommand
    {
        const string OutRoot = @"C:\Temp\hvac\revit";
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static readonly BuiltInCategory[] Cats = {
            BuiltInCategory.OST_FabricationDuctwork, BuiltInCategory.OST_DuctCurves, BuiltInCategory.OST_DuctFitting,
            BuiltInCategory.OST_DuctAccessory, BuiltInCategory.OST_FlexDuctCurves, BuiltInCategory.OST_DuctTerminal,
            BuiltInCategory.OST_MechanicalEquipment };

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            Document doc = data.Application.ActiveUIDocument.Document;
            DateTime t0 = DateTime.Now;
            string dir = Path.Combine(OutRoot, t0.ToString("yyyyMMdd-HHmmss", Inv));
            Directory.CreateDirectory(dir);

            var cats = new List<BuiltInCategory>(Cats);
            var filter = new ElementMulticategoryFilter(cats);
            var els = new FilteredElementCollector(doc).WherePasses(filter).WhereElementIsNotElementType().ToElements();

            var byCat = new SortedDictionary<string, int>();
            var byFam = new SortedDictionary<string, int>();
            var errors = new List<string>();
            int nConn = 0, nOpen = 0, nNoMgr = 0;
            var openByCat = new SortedDictionary<string, int>();
            var open = new List<OpenEnd>();
            var e = new StringBuilder();
            var c = new StringBuilder();
            e.AppendLine("ElementId,UniqueId,IfcGUID,Category,Family,Type,Size,SystemName,SystemType,Level,IsFabrication,Connectors");
            c.AppendLine("ElementId,ConnectorId,ConnType,Domain,Shape,X_ft,Y_ft,Z_ft,DirX,DirY,DirZ,Width_in,Height_in,Diameter_in,ConnectedTo");

            foreach (Element el in els)
            {
                try
                {
                    string cat = el.Category != null ? el.Category.Name : "";
                    string fam = "", typ = "";
                    bool fab = el is FabricationPart;
                    if (fab)
                    {
                        var fp = (FabricationPart)el;
                        fam = Safe(delegate { return fp.ProductName; });
                        typ = Safe(delegate { return fp.Alias; });
                    }
                    else
                    {
                        Element t = doc.GetElement(el.GetTypeId());
                        var et = t as ElementType;
                        if (et != null) { fam = et.FamilyName; typ = et.Name; }
                    }
                    string size = Param(el, BuiltInParameter.RBS_CALCULATED_SIZE);
                    if (size == "" && fab) size = Safe(delegate { return ((FabricationPart)el).OverallSize; });
                    if (size == "") size = Lookup(el, "Size");
                    string lvl = "";
                    Level lv = doc.GetElement(el.LevelId) as Level;
                    if (lv != null) lvl = lv.Name; else lvl = Lookup(el, "Reference Level");

                    ConnectorManager cm = Manager(el);
                    int n = 0;
                    if (cm == null) nNoMgr++;
                    else
                    {
                        foreach (Connector k in cm.Connectors)
                        {
                            n++; nConn++;
                            c.Append(el.Id.ToString()).Append(',').Append(k.Id.ToString(Inv)).Append(',')
                             .Append(k.ConnectorType).Append(',').Append(k.Domain).Append(',').Append(k.Shape).Append(',');
                            XYZ o = null, d = null;
                            try { o = k.Origin; } catch { }
                            try { d = k.CoordinateSystem.BasisZ; } catch { }
                            c.Append(o == null ? ",," : F(o.X) + "," + F(o.Y) + "," + F(o.Z)).Append(',');
                            c.Append(d == null ? ",," : F(d.X) + "," + F(d.Y) + "," + F(d.Z)).Append(',');
                            double w = double.NaN, h = double.NaN, r = double.NaN;
                            try { if (k.Shape == ConnectorProfileType.Rectangular || k.Shape == ConnectorProfileType.Oval) { w = k.Width * 12; h = k.Height * 12; } } catch { }
                            try { if (k.Shape == ConnectorProfileType.Round) r = k.Radius * 24; } catch { }
                            c.Append(N(w)).Append(',').Append(N(h)).Append(',').Append(N(r)).Append(',');
                            var to = new List<string>();
                            try
                            {
                                foreach (Connector o2 in k.AllRefs)
                                {
                                    if (o2.Owner == null || o2.Owner.Id == el.Id) continue;
                                    if (o2.ConnectorType == ConnectorType.Logical) continue;   // system links, not physical joints
                                    to.Add(o2.Owner.Id.ToString() + ":" + o2.Id.ToString(Inv));
                                }
                            }
                            catch { }
                            if (to.Count == 0 && k.ConnectorType == ConnectorType.End)
                            {
                                nOpen++;
                                Bump(openByCat, Abbr(el));
                                if (o != null) open.Add(new OpenEnd { X = o.X, Y = o.Y, Z = o.Z, Owner = el.Id.ToString(), Cat = Abbr(el) });
                            }
                            c.AppendLine(string.Join(";", to));
                        }
                    }

                    e.Append(el.Id.ToString()).Append(',').Append(Q(el.UniqueId)).Append(',').Append(Q(IfcGuid(el))).Append(',')
                     .Append(Q(cat)).Append(',').Append(Q(fam)).Append(',').Append(Q(typ)).Append(',').Append(Q(size)).Append(',')
                     .Append(Q(Param(el, BuiltInParameter.RBS_SYSTEM_NAME_PARAM))).Append(',')
                     .Append(Q(Param(el, BuiltInParameter.RBS_SYSTEM_CLASSIFICATION_PARAM) != "" ? Param(el, BuiltInParameter.RBS_SYSTEM_CLASSIFICATION_PARAM) : Lookup(el, "System Type"))).Append(',')
                     .Append(Q(lvl)).Append(',').Append(fab ? "1" : "0").Append(',').Append(n.ToString(Inv)).AppendLine();
                    Bump(byCat, cat);
                    Bump(byFam, cat + " | " + fam + " | " + typ);
                }
                catch (Exception ex) { errors.Add(el.Id.ToString() + ": " + ex.GetType().Name + " " + ex.Message); }
            }

            File.WriteAllText(Path.Combine(dir, "elements.csv"), e.ToString());
            File.WriteAllText(Path.Combine(dir, "connectors.csv"), c.ToString());
            // Open End connectors with another element's open End within Touch ft: drawn end to end but not
            // connected in Revit (geometric joint), vs. truly open (terminal, cap, stub, gap).
            var touchByCat = new SortedDictionary<string, int>();
            int nTouch = Touching(open, touchByCat);

            var s = new StringBuilder();
            // NEW block: 3 short lines to type back from the secure machine.
            s.AppendLine(string.Format(Inv, "NEW el {0} conn {1} open {2} touching {3} err {4}", els.Count, nConn, nOpen, nTouch, errors.Count));
            s.AppendLine("NEW open " + Compact(openByCat));
            s.AppendLine("NEW touch " + Compact(touchByCat));
            s.AppendLine();
            s.AppendLine("PlutoRevit ExportConnectors  " + t0.ToString("yyyy-MM-dd HH:mm:ss", Inv));
            s.AppendLine(string.Format(Inv, "touching = open End connector with another element's open End within {0} ft", Touch));
            s.AppendLine("abbreviations: Fab = MEP Fabrication Ductwork, Duct, Fit = Duct Fittings, Acc = Duct Accessories, Flex, Term = Air Terminals, Eq = Mechanical Equipment");
            s.AppendLine("document: " + doc.PathName);
            s.AppendLine("revit:    " + data.Application.Application.VersionName + " " + data.Application.Application.VersionBuild);
            s.AppendLine(string.Format(Inv, "elements {0}  connectors {1}  unconnected End connectors {2}  no connector manager {3}  errors {4}  {5:0.0} s",
                els.Count, nConn, nOpen, nNoMgr, errors.Count, (DateTime.Now - t0).TotalSeconds));
            s.AppendLine("\nby category:");
            foreach (var kv in byCat) s.AppendLine(string.Format(Inv, "  {0,7}  {1}", kv.Value, kv.Key));
            s.AppendLine("\nby category | family-or-product | type:");
            foreach (var kv in byFam) s.AppendLine(string.Format(Inv, "  {0,7}  {1}", kv.Value, kv.Key));
            if (errors.Count > 0) { s.AppendLine("\nerrors (first 50):"); for (int i = 0; i < errors.Count && i < 50; i++) s.AppendLine("  " + errors[i]); }
            File.WriteAllText(Path.Combine(dir, "summary.txt"), s.ToString());

            TaskDialog.Show("Pluto Connectors", string.Format(Inv,
                "{0} elements, {1} connectors ({2} open ends), {3} errors.\n\n{4}", els.Count, nConn, nOpen, errors.Count, dir));
            return Result.Succeeded;
        }

        const double Touch = 0.02;   // ft (~1/4 in)
        class OpenEnd { public double X, Y, Z; public string Owner, Cat; }

        static int Touching(List<OpenEnd> open, IDictionary<string, int> byCat)
        {
            var grid = new Dictionary<string, List<OpenEnd>>();
            foreach (var p in open)
            {
                string key = Cell(p.X, 0) + "," + Cell(p.Y, 0) + "," + Cell(p.Z, 0);
                List<OpenEnd> l;
                if (!grid.TryGetValue(key, out l)) { l = new List<OpenEnd>(); grid[key] = l; }
                l.Add(p);
            }
            int n = 0;
            foreach (var p in open)
            {
                bool hit = false;
                for (int i = -1; i <= 1 && !hit; i++)
                    for (int j = -1; j <= 1 && !hit; j++)
                        for (int k = -1; k <= 1 && !hit; k++)
                        {
                            List<OpenEnd> l;
                            if (!grid.TryGetValue(Cell(p.X, i) + "," + Cell(p.Y, j) + "," + Cell(p.Z, k), out l)) continue;
                            foreach (var q in l)
                            {
                                if (q.Owner == p.Owner) continue;
                                double dx = p.X - q.X, dy = p.Y - q.Y, dz = p.Z - q.Z;
                                if (dx * dx + dy * dy + dz * dz <= Touch * Touch) { hit = true; break; }
                            }
                        }
                if (hit) { n++; Bump(byCat, p.Cat); }
            }
            return n;
        }

        static long Cell(double v, int off) { return (long)Math.Floor(v / Touch) + off; }

        static string Compact(IDictionary<string, int> d)
        {
            var parts = new List<string>();
            foreach (var kv in d) parts.Add(kv.Key + " " + K(kv.Value));
            return string.Join(" ", parts);
        }

        static string K(int v) { return v >= 10000 ? (v / 1000).ToString(Inv) + "k" : v >= 1000 ? (v / 1000.0).ToString("0.#", Inv) + "k" : v.ToString(Inv); }

        static string Abbr(Element el)
        {
            if (el.Category == null) return "?";
            switch ((BuiltInCategory)el.Category.Id.Value)
            {
                case BuiltInCategory.OST_FabricationDuctwork: return "Fab";
                case BuiltInCategory.OST_DuctCurves: return "Duct";
                case BuiltInCategory.OST_DuctFitting: return "Fit";
                case BuiltInCategory.OST_DuctAccessory: return "Acc";
                case BuiltInCategory.OST_FlexDuctCurves: return "Flex";
                case BuiltInCategory.OST_DuctTerminal: return "Term";
                case BuiltInCategory.OST_MechanicalEquipment: return "Eq";
            }
            return "?";
        }

        static ConnectorManager Manager(Element el)
        {
            var fp = el as FabricationPart; if (fp != null) return fp.ConnectorManager;
            var mc = el as MEPCurve; if (mc != null) return mc.ConnectorManager;
            var fi = el as FamilyInstance; if (fi != null && fi.MEPModel != null) return fi.MEPModel.ConnectorManager;
            return null;
        }

        static string IfcGuid(Element el)
        {
            string g = Param(el, BuiltInParameter.IFC_GUID);
            return g != "" ? g : Lookup(el, "IfcGUID");
        }

        static string Param(Element el, BuiltInParameter bip)
        {
            try { Parameter p = el.get_Parameter(bip); return p == null ? "" : (p.AsValueString() ?? p.AsString() ?? ""); }
            catch { return ""; }
        }

        static string Lookup(Element el, string name)
        {
            try { Parameter p = el.LookupParameter(name); return p == null ? "" : (p.AsString() ?? p.AsValueString() ?? ""); }
            catch { return ""; }
        }

        delegate string Getter();
        static string Safe(Getter g) { try { return g() ?? ""; } catch { return ""; } }
        static void Bump(IDictionary<string, int> d, string k) { int v; d.TryGetValue(k, out v); d[k] = v + 1; }
        static string F(double v) { return v.ToString("0.#####", Inv); }
        static string N(double v) { return double.IsNaN(v) ? "" : v.ToString("0.###", Inv); }
        static string Q(string v)
        {
            if (v == null) return "";
            return v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0 ? v : "\"" + v.Replace("\"", "\"\"") + "\"";
        }
    }
}
