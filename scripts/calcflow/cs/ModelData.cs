// ModelData.cs -- Model_Data.xlsx: everything read from the STAAD .anl, tabulated for the engineer. Generated from
// the analysis package (Package.cs) by the post-process step, rebuilt on every run, never edited:
//   About                  the source file, units, counts; every output command of the file with the load list it ran
//                          under and whether its table is read yet; the reader's warnings
//   Members                Member, Node I, Node J, Section, Family, Kind, Length, Beta, releases, offsets
//   Joints                 Node, X, Y, Z, Support, how many columns / beams / braces end there
//   Supports               Node, Type, Restrained, Springs
//   Member Ends            one row per member end at a joint: Role, Section, the member it runs through
//   Load Cases             LC, Kind, Method, Load Type, Title, Factors, Forces printed
//   All End Forces         Member, End (I / J), Node, LC, Fx .. Mz as STAAD printed them (member local axes). Past
//                          RowsPerSheet rows it continues on "All End Forces (2)" ..., split between members
//   Enveloped End Forces   per member end and component: the case of the largest and of the smallest value, with the
//                          forces that act with it (12 rows an end)
//   End Force Extremes     per member end: the largest and smallest of each component on one row (not concurrent)
// End A / B of the package is shown as I / J, the engineer's words. The long tables are written row by row
// (XOutSheet.MoreRows): they are never held in memory. C# 5 (Windows PowerShell 5.1 Add-Type, warnings are errors).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace CalcFlow
{
    public sealed class ModelDataResult
    {
        public string Path = "";
        public int Members, Joints, Cases, EndForceRows, EnvelopeRows, ForceSheets;
        public readonly List<string> NotRead = new List<string>();     // output commands whose tables are not read yet
    }

    public static class ModelData
    {
        public const string FileName = "Model_Data.xlsx";
        public static int RowsPerSheet = 1000000;       // rows of a long table on one tab (Excel holds 1,048,576)
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static readonly string[] Comp = { "Fx", "Fy", "Fz", "Mx", "My", "Mz" };

        public static string EndName(string ab) { return ab == "B" ? "J" : "I"; }

        // writes the workbook (a numbered name when the usual one is open in Excel); returns what it wrote
        public static ModelDataResult Write(AnalysisPackage pkg, string dir)
        {
            Directory.CreateDirectory(dir);
            string path = System.IO.Path.Combine(dir, FileName);
            for (int i = 2; SharedFile.IsLocked(path); i++)
                path = System.IO.Path.Combine(dir, System.IO.Path.GetFileNameWithoutExtension(FileName) + "_" + i.ToString(Inv) + ".xlsx");
            var res = new ModelDataResult();
            res.Path = path;
            XlsxWriter.Write(path, Sheets(pkg, res), null);
            return res;
        }

        public static List<XOutSheet> Sheets(AnalysisPackage pkg, ModelDataResult res)
        {
            var sheets = new List<XOutSheet>();
            string fu = pkg.ForceUnit == null ? "" : pkg.ForceUnit.ToString(), mu = pkg.MomentUnit == null ? "" : pkg.MomentUnit.ToString();
            var members = pkg.Members.Values.OrderBy(m => m.Id).ToList();
            List<Dictionary<string, string>> memberRows = Table(pkg, "members.csv"), supports = Table(pkg, "supports.csv"),
                ends = Table(pkg, "ends.csv"), cases = Table(pkg, "loadcases.csv"), joints = Table(pkg, "joints.csv"), prints = Table(pkg, "prints.csv");
            res.Members = members.Count; res.Joints = pkg.Joints.Count; res.Cases = pkg.Cases.Count;

            // ---- the member ends with forces, in member order
            var endKeys = new List<KeyValuePair<PkgMember, string>>();
            foreach (PkgMember m in members)
                foreach (string e in new[] { "A", "B" })
                    if (pkg.Forces(m.Id, e).Count > 0) endKeys.Add(new KeyValuePair<PkgMember, string>(m, e));
            int forceRows = endKeys.Sum(k => pkg.Forces(k.Key.Id, k.Value).Count);
            res.EndForceRows = forceRows; res.EnvelopeRows = endKeys.Count * 12;

            // ---- About
            var about = new XOutSheet("About");
            about.Add(new XCell("Model data", Palette.Title()));
            about.Add(new XCell("Everything read from the STAAD analysis output. This workbook is generated: it is written again by every post-process run, so keep nothing in it.", Palette.Note()));
            about.Add();
            Action<string, object> kv = (k, v) => about.Add(new XCell(k, Palette.Data(ColumnKind.Key)), v);
            kv("Analysis output", pkg.Source);
            kv("SHA-256", pkg.SourceSha256);
            kv("Package", pkg.Folder);
            kv("Lengths", "in");
            kv("Forces", fu);
            kv("Moments", mu);
            kv("End forces", "in each member's local axes, STAAD's sign (the force on the member at that end), as printed");
            kv("Members", members.Count);
            kv("Joints", pkg.Joints.Count);
            kv("Load cases", pkg.Cases.Count);
            kv("End force rows", forceRows);
            about.Add();
            int top = about.Rows.Count;
            string[] pcols = { "Order", "Output command", "Load list", "In this workbook" };
            about.Add(pcols.Select(c => (object)new XCell(c, Palette.Header(c == "In this workbook" ? ColumnKind.Tool : ColumnKind.Reference))).ToArray());
            foreach (var p in prints)
            {
                bool read = V(p, "Read") == "Y";
                if (!read) res.NotRead.Add(V(p, "Command"));
                about.Add(Num(V(p, "Order")), V(p, "Command"), V(p, "LoadList"), read ? "Yes (" + V(p, "Rows") + " rows)" : "Not read yet");
            }
            if (prints.Count == 0) about.Add("", "(the package lists no output commands: written by an earlier reader)", "", "");
            Palette.Highlight(about, "D" + (top + 2) + ":D" + (top + 1 + Math.Max(1, prints.Count)), "Not read yet", Palette.Amber, "FF000000");
            about.Add();
            var problems = Problems(pkg);
            if (problems.Count > 0)
            {
                int ptop = about.Rows.Count;
                about.Add(new XCell("Severity", Palette.Header(ColumnKind.Tool)), new XCell("What the reader noted", Palette.Header(ColumnKind.Tool)));
                foreach (string[] p in problems) about.Add(p[0], p[2]);
                Palette.Severities(about, "A" + (ptop + 2) + ":A" + (ptop + 1 + problems.Count));
            }
            about.Width(0, 18); about.Width(1, 70); about.Width(2, 34); about.Width(3, 24);
            about.Landscape = true; about.FitToWidth = true;
            sheets.Add(about);

            // ---- small tables
            sheets.Add(Small("Members", "tblMembers", new[] { "Member", "Node I", "Node J", "Section", "Family", "Kind", "Length (in)", "Beta", "Release I", "Release J", "Offset I (in)", "Offset J (in)", "Property" },
                memberRows.Select(r => Row("Member", V(r, "Member"), "Node I", V(r, "NodeA"), "Node J", V(r, "NodeB"), "Section", V(r, "Section"), "Family", V(r, "Family"),
                    "Kind", V(r, "Kind"), "Length (in)", V(r, "Length (in)"), "Beta", V(r, "Beta"), "Release I", V(r, "ReleaseA"), "Release J", V(r, "ReleaseB"),
                    "Offset I (in)", V(r, "OffsetA (in)"), "Offset J (in)", V(r, "OffsetB (in)"), "Property", V(r, "Property"))).ToList(),
                "one row per member, as STAAD analysed it", new[] { "Member" }, new[] { "Member", "Node I", "Node J" }, new[] { "Length (in)", "Beta" }));
            sheets.Add(Small("Joints", "tblJoints", new[] { "Node", "X (in)", "Y (in)", "Z (in)", "Support", "Member ends", "Columns", "Beams", "Braces" },
                joints.Select(r => Row("Node", V(r, "Node"), "X (in)", V(r, "X (in)"), "Y (in)", V(r, "Y (in)"), "Z (in)", V(r, "Z (in)"), "Support", V(r, "Support"),
                    "Member ends", V(r, "Ends"), "Columns", V(r, "Columns"), "Beams", V(r, "Beams"), "Braces", V(r, "Braces"))).ToList(),
                "one row per joint", new[] { "Node" }, new[] { "Node", "Member ends", "Columns", "Beams", "Braces" }, new[] { "X (in)", "Y (in)", "Z (in)" }));
            sheets.Add(Small("Supports", "tblSupports", new[] { "Node", "Type", "Restrained", "Springs", "As written" },
                supports.Select(r => Row("Node", V(r, "Node"), "Type", V(r, "Type"), "Restrained", V(r, "Restrained"), "Springs", V(r, "Springs"), "As written", V(r, "Spec"))).ToList(),
                "one row per supported joint. The support reactions are not read yet (see About)", new[] { "Node" }, new[] { "Node" }, new string[0]));
            sheets.Add(Small("Member Ends", "tblMemberEnds", new[] { "Node", "Member", "End", "Role", "Section", "Family", "Kind", "Released", "Runs through as member" },
                ends.Select(r => Row("Node", V(r, "Node"), "Member", V(r, "Member"), "End", EndName(V(r, "End")), "Role", V(r, "Role"), "Section", V(r, "Section"),
                    "Family", V(r, "Family"), "Kind", V(r, "Kind"), "Released", V(r, "Released"), "Runs through as member", V(r, "ThroughMember"))).ToList(),
                "one row per member end at a joint: what a connection is assigned to", new[] { "Node", "Member", "End" }, new[] { "Node", "Member", "Runs through as member" }, new string[0]));
            sheets.Add(Small("Load Cases", "tblLoadCases", new[] { "LC", "Kind", "Method", "Load Type", "Title", "Factors", "Forces printed" },
                cases.Select(r => Row("LC", V(r, "LC"), "Kind", V(r, "Kind"), "Method", V(r, "Method"), "Load Type", V(r, "LoadType"), "Title", V(r, "Title"),
                    "Factors", V(r, "Factors"), "Forces printed", V(r, "HasForces"))).ToList(),
                "every load case and combination. SRSS and ABS combinations are formed from magnitudes: their signs are not those of a real load", new[] { "LC" }, new[] { "LC" }, new string[0]));

            // ---- All End Forces: split between members past RowsPerSheet
            string[] fcols = { "Member", "End", "Node", "LC", "Fx (" + fu + ")", "Fy (" + fu + ")", "Fz (" + fu + ")", "Mx (" + mu + ")", "My (" + mu + ")", "Mz (" + mu + ")" };
            var parts = new List<List<KeyValuePair<PkgMember, string>>>();
            var cur = new List<KeyValuePair<PkgMember, string>>();
            int curRows = 0;
            foreach (var g in endKeys.GroupBy(k => k.Key.Id))
            {
                int n = g.Sum(k => pkg.Forces(k.Key.Id, k.Value).Count);
                if (curRows > 0 && curRows + n > RowsPerSheet) { parts.Add(cur); cur = new List<KeyValuePair<PkgMember, string>>(); curRows = 0; }
                cur.AddRange(g); curRows += n;
            }
            if (cur.Count > 0 || parts.Count == 0) parts.Add(cur);
            res.ForceSheets = parts.Count;
            for (int i = 0; i < parts.Count; i++)
            {
                List<KeyValuePair<PkgMember, string>> part = parts[i];
                int n = part.Sum(k => pkg.Forces(k.Key.Id, k.Value).Count);
                string name = "All End Forces" + (i == 0 ? "" : " (" + (i + 1).ToString(Inv) + ")");
                string range = part.Count == 0 ? "" : ": members " + part[0].Key.Id.ToString(Inv) + " to " + part[part.Count - 1].Key.Id.ToString(Inv);
                string note = "every printed member end force, in the member's local axes, STAAD's sign" + (parts.Count > 1 ? " (part " + (i + 1) + " of " + parts.Count + range + ")" : "");
                sheets.Add(Long(name, "tblAllEndForces" + (i == 0 ? "" : (i + 1).ToString(Inv)), fcols, 4, note, n, ForceRows(pkg, part)));
            }

            // ---- envelopes
            string[] ecols = { "Member", "End", "Node", "Governs", "LC", fcols[4], fcols[5], fcols[6], fcols[7], fcols[8], fcols[9] };
            sheets.Add(Long("Enveloped End Forces", "tblEnvelopedEndForces", ecols, 5,
                "per member end and component: the case of its largest and of its smallest value, with the forces that act with it (over the cases with printed forces)",
                endKeys.Count * 12, EnvelopeRows(pkg, endKeys)));
            var xcols = new List<string> { "Member", "End", "Node" };
            for (int c = 0; c < 6; c++) { string u = c < 3 ? fu : mu; xcols.Add(Comp[c] + " max (" + u + ")"); xcols.Add(Comp[c] + " min (" + u + ")"); }
            sheets.Add(Long("End Force Extremes", "tblEndForceExtremes", xcols.ToArray(), 3,
                "per member end: the largest and smallest value of each component over the cases with printed forces. These do not act together: for concurrent forces use Enveloped End Forces",
                endKeys.Count, ExtremeRows(pkg, endKeys)));
            return sheets;
        }

        // ---- rows of the long tables, produced as they are written

        static IEnumerable<object[]> ForceRows(AnalysisPackage pkg, List<KeyValuePair<PkgMember, string>> ends)
        {
            foreach (var k in ends)
            {
                int node = k.Value == "A" ? k.Key.NodeA : k.Key.NodeB;
                foreach (var f in pkg.Forces(k.Key.Id, k.Value))
                    yield return new object[] { k.Key.Id, EndName(k.Value), node, f.Key, f.Value[0], f.Value[1], f.Value[2], f.Value[3], f.Value[4], f.Value[5] };
            }
        }

        // the case of the largest (max true) or smallest value of component c; the lowest case number wins a tie
        static int Extreme(SortedDictionary<int, double[]> forces, int c, bool max)
        {
            int best = 0; bool any = false; double v = 0;
            foreach (var f in forces)
                if (!any || (max ? f.Value[c] > v : f.Value[c] < v)) { best = f.Key; v = f.Value[c]; any = true; }
            return best;
        }

        static IEnumerable<object[]> EnvelopeRows(AnalysisPackage pkg, List<KeyValuePair<PkgMember, string>> ends)
        {
            foreach (var k in ends)
            {
                int node = k.Value == "A" ? k.Key.NodeA : k.Key.NodeB;
                SortedDictionary<int, double[]> forces = pkg.Forces(k.Key.Id, k.Value);
                for (int c = 0; c < 6; c++)
                    foreach (bool max in new[] { true, false })
                    {
                        int lc = Extreme(forces, c, max);
                        double[] f = forces[lc];
                        yield return new object[] { k.Key.Id, EndName(k.Value), node, Comp[c] + (max ? " max" : " min"), lc, f[0], f[1], f[2], f[3], f[4], f[5] };
                    }
            }
        }

        static IEnumerable<object[]> ExtremeRows(AnalysisPackage pkg, List<KeyValuePair<PkgMember, string>> ends)
        {
            foreach (var k in ends)
            {
                int node = k.Value == "A" ? k.Key.NodeA : k.Key.NodeB;
                SortedDictionary<int, double[]> forces = pkg.Forces(k.Key.Id, k.Value);
                var row = new object[15];
                row[0] = k.Key.Id; row[1] = EndName(k.Value); row[2] = node;
                for (int c = 0; c < 6; c++)
                {
                    row[3 + 2 * c] = forces[Extreme(forces, c, true)][c];
                    row[4 + 2 * c] = forces[Extreme(forces, c, false)][c];
                }
                yield return row;
            }
        }

        // ---- sheets

        // a long numeric table: a note row, the header, then the rows from a source; the first keyCols columns are keys
        static XOutSheet Long(string name, string table, string[] cols, int keyCols, string note, int rows, IEnumerable<object[]> source)
        {
            var s = new XOutSheet(name);
            s.Add(new XCell(note, Palette.Note()));
            s.Add(cols.Select((c, i) => (object)new XCell(c, Palette.Header(i < keyCols ? ColumnKind.Key : ColumnKind.Tool))).ToArray());
            s.RowHeights[1] = 30;
            if (rows > 0) { s.MoreRows = source; s.MoreRowsCount = rows; }
            else s.Add(cols.Select(c => (object)"").ToArray());
            var t = new XOutTable();
            t.Name = table; t.Top = 1; t.Left = 0; t.Right = cols.Length - 1; t.Bottom = 1 + Math.Max(rows, 1); t.Style = Palette.TableStyle;
            s.Tables.Add(t);
            s.FreezeRows = 2;
            s.Landscape = true; s.FitToWidth = true; s.PrintTitleFirstRow = 2; s.PrintTitleLastRow = 2;
            for (int i = 0; i < cols.Length; i++) s.Width(i, Math.Max(10, cols[i].Length + 3));
            return s;
        }

        // a small table of text rows; whole-number and decimal columns are held as numbers
        static XOutSheet Small(string name, string table, string[] cols, List<Dictionary<string, string>> rows, string note, string[] keys, string[] ints, string[] decimals)
        {
            XOutSheet s = WorkbookTables.Sheet(name, table, cols, rows, note, c => keys.Contains(c) ? ColumnKind.Key : ColumnKind.Reference);
            foreach (string c in ints) WorkbookTables.Numbers(s, Array.IndexOf(cols, c), "0");
            foreach (string c in decimals) WorkbookTables.Numbers(s, Array.IndexOf(cols, c), "0.###");
            return s;
        }

        static Dictionary<string, string> Row(params string[] kv)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i + 1 < kv.Length; i += 2) d[kv[i]] = kv[i + 1];
            return d;
        }

        static List<Dictionary<string, string>> Table(AnalysisPackage pkg, string file)
        {
            return Csv.ReadTable(System.IO.Path.Combine(pkg.Folder, file));
        }

        static string V(Dictionary<string, string> r, string k) { string v; return r.TryGetValue(k, out v) ? (v ?? "") : ""; }

        static object Num(string s)
        {
            double d;
            return double.TryParse(s, NumberStyles.Float, Inv, out d) ? (object)d : s;
        }

        // the reader's problems, from the package's manifest: severity, code, message
        public static List<string[]> Problems(AnalysisPackage pkg)
        {
            var list = new List<string[]>();
            string man = System.IO.Path.Combine(pkg.Folder, "manifest.json");
            if (!File.Exists(man)) return list;
            foreach (Match m in Regex.Matches(File.ReadAllText(man), "\\{\\s*\"severity\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"\\s*,\\s*\"code\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"\\s*,\\s*\"message\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"\\s*\\}"))
                list.Add(new[] { Regex.Unescape(m.Groups[1].Value), Regex.Unescape(m.Groups[2].Value), Regex.Unescape(m.Groups[3].Value) });
            return list;
        }
    }
}
