// LibreOffice.cs -- LibreOffice (open source) as CalcFlow's calculator where Excel is not installed, and as the check
// that a workbook CalcFlow wrote opens cleanly.
//   XlsxPatch      a copy of a workbook with some cells set (numbers, text, blanks) at the XML level, and marked to be
//                  calculated in full when it opens; nothing else in the file changes
//   LibreOffice    finds soffice (PATH, /usr/bin, Program Files\LibreOffice\program) and runs it headless on a batch of
//                  files, with a profile of its own that recalculates every formula on load, and a time limit
// C# 5 (Windows PowerShell 5.1 Add-Type, warnings are errors).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace CalcFlow
{
    public sealed class CellWrite
    {
        public string Sheet = "", Address = "";
        public object Value;        // double / int: a number; string: text; null or "": an empty cell

        public CellWrite() { }
        public CellWrite(string sheet, string address, object value) { Sheet = sheet; Address = address; Value = value; }
    }

    // an Excel error value to write (the tests' engine writes "#DIV/0!" this way)
    public sealed class CellError
    {
        public string Text = "#N/A";
        public CellError(string text) { Text = text; }
        public override string ToString() { return Text; }
    }

    public static class XlsxPatch
    {
        static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        static readonly XNamespace NsR = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

        // src -> dst with the cells written; dst is calculated in full when it opens
        public static void Write(string src, string dst, IList<CellWrite> writes) { Write(src, dst, writes, true); }

        // fullCalc false: workbook.xml is left as it is
        public static void Write(string src, string dst, IList<CellWrite> writes, bool fullCalc)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dst)));
            string tmp = dst + ".tmp";
            using (FileStream ins = SharedFile.OpenRead(src))
            using (var zin = new ZipArchive(ins, ZipArchiveMode.Read, false))
            {
                string wbPart;
                Dictionary<string, string> sheetParts = SheetParts(zin, out wbPart);
                var bySheet = new Dictionary<string, List<CellWrite>>(StringComparer.OrdinalIgnoreCase);
                foreach (CellWrite w in writes)
                {
                    string part;
                    if (!sheetParts.TryGetValue(w.Sheet, out part)) throw new Exception(Path.GetFileName(src) + " has no sheet '" + w.Sheet + "'");
                    List<CellWrite> l;
                    if (!bySheet.TryGetValue(part, out l)) { l = new List<CellWrite>(); bySheet[part] = l; }
                    l.Add(w);
                }
                using (var outs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var zout = new ZipArchive(outs, ZipArchiveMode.Create))
                {
                    foreach (ZipArchiveEntry e in zin.Entries)
                    {
                        string name = e.FullName.Replace('\\', '/');
                        List<CellWrite> mine;
                        byte[] data;
                        using (var ms = new MemoryStream()) { using (Stream s = e.Open()) s.CopyTo(ms); data = ms.ToArray(); }
                        // the calc chain lists the formula cells; it is dropped (with its two references) so that a
                        // written cell that held a formula can never leave it naming a cell that has none
                        if (name.EndsWith("/calcChain.xml", StringComparison.OrdinalIgnoreCase)) continue;
                        if (bySheet.TryGetValue(name, out mine)) data = PatchSheet(data, mine);
                        else if (fullCalc && string.Equals(name, wbPart, StringComparison.OrdinalIgnoreCase)) data = FullCalc(data);
                        else if (string.Equals(name, "[Content_Types].xml", StringComparison.OrdinalIgnoreCase)) data = DropCalcChain(data, "Override", "PartName");
                        else if (name.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)) data = DropCalcChain(data, "Relationship", "Target");
                        ZipArchiveEntry o = zout.CreateEntry(e.FullName, CompressionLevel.Optimal);
                        using (Stream s = o.Open()) s.Write(data, 0, data.Length);
                    }
                }
            }
            if (File.Exists(dst)) File.Delete(dst);
            File.Move(tmp, dst);
        }

        // sheet name -> its part ("xl/worksheets/sheet1.xml")
        static Dictionary<string, string> SheetParts(ZipArchive zip, out string wbPath)
        {
            wbPath = "xl/workbook.xml";
            foreach (string[] r in Xlsx.Rels(zip, "_rels/.rels", ""))
                if (r[1].EndsWith("/officeDocument", StringComparison.OrdinalIgnoreCase)) { wbPath = r[2]; break; }
            string wbDir = Xlsx.DirOf(wbPath);
            var rels = Xlsx.Rels(zip, wbDir + "_rels/" + Xlsx.FileOf(wbPath) + ".rels", wbDir).ToDictionary(r => r[0], r => r[2]);
            XDocument wb;
            using (Stream s = Xlsx.Entry(zip, wbPath).Open()) wb = XDocument.Load(s);
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (XElement sh in wb.Descendants(Ns + "sheet"))
            {
                string id = (string)sh.Attribute(NsR + "id");
                string target;
                if (id != null && rels.TryGetValue(id, out target)) map[(string)sh.Attribute("name")] = target;
            }
            return map;
        }

        static byte[] DropCalcChain(byte[] data, string element, string attribute)
        {
            XDocument d = Load(data);
            List<XElement> gone = d.Root.Elements().Where(x => x.Name.LocalName == element &&
                ((string)x.Attribute(attribute) ?? "").EndsWith("calcChain.xml", StringComparison.OrdinalIgnoreCase)).ToList();
            if (gone.Count == 0) return data;
            foreach (XElement x in gone) x.Remove();
            return Save(d);
        }

        static byte[] FullCalc(byte[] data)
        {
            XDocument d = Load(data);
            XElement root = d.Root;
            XElement calc = root.Element(Ns + "calcPr");
            if (calc == null)
            {
                calc = new XElement(Ns + "calcPr");
                // calcPr comes after sheets / definedNames and before the later workbook elements
                XElement after = root.Element(Ns + "definedNames") ?? root.Element(Ns + "sheets");
                if (after != null) after.AddAfterSelf(calc); else root.Add(calc);
            }
            calc.SetAttributeValue("fullCalcOnLoad", "1");
            return Save(d);
        }

        static byte[] PatchSheet(byte[] data, List<CellWrite> writes)
        {
            XDocument d = Load(data);
            XElement sheetData = d.Root.Element(Ns + "sheetData");
            if (sheetData == null) { sheetData = new XElement(Ns + "sheetData"); d.Root.Add(sheetData); }
            foreach (CellWrite w in writes)
            {
                int col, row;
                if (!Xlsx.ParseRef(w.Address, out col, out row) || row < 0) throw new Exception("not a cell address: " + w.Address);
                XElement r = RowOf(sheetData, row + 1);
                r.SetAttributeValue("spans", null);
                XElement c = CellOf(r, w.Address.Replace("$", "").ToUpperInvariant(), col);
                c.Elements().Remove();
                c.SetAttributeValue("t", null);
                object v = w.Value;
                if (v == null || (v is string && ((string)v).Length == 0)) continue;
                if (v is string)
                {
                    c.SetAttributeValue("t", "inlineStr");
                    var t = new XElement(Ns + "t", (string)v);
                    if (((string)v).Trim() != (string)v) t.SetAttributeValue(XNamespace.Xml + "space", "preserve");
                    c.Add(new XElement(Ns + "is", t));
                }
                else if (v is bool) { c.SetAttributeValue("t", "b"); c.Add(new XElement(Ns + "v", (bool)v ? "1" : "0")); }
                else if (v is CellError) { c.SetAttributeValue("t", "e"); c.Add(new XElement(Ns + "v", ((CellError)v).Text)); }
                else if (v is IConvertible) c.Add(new XElement(Ns + "v", Convert.ToDouble(v, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture)));
                else
                {
                    // anything else (a wrapped object) as its text
                    c.SetAttributeValue("t", "inlineStr");
                    c.Add(new XElement(Ns + "is", new XElement(Ns + "t", v.ToString())));
                }
            }
            return Save(d);
        }

        static XElement RowOf(XElement sheetData, int r)
        {
            XElement before = null;
            foreach (XElement x in sheetData.Elements(Ns + "row"))
            {
                int n;
                if (!int.TryParse((string)x.Attribute("r"), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) continue;
                if (n == r) return x;
                if (n > r) { before = x; break; }
            }
            var row = new XElement(Ns + "row", new XAttribute("r", r));
            if (before != null) before.AddBeforeSelf(row); else sheetData.Add(row);
            return row;
        }

        static XElement CellOf(XElement row, string reference, int col)
        {
            XElement before = null;
            foreach (XElement x in row.Elements(Ns + "c"))
            {
                int c, r;
                string rf = (string)x.Attribute("r");
                if (rf != null && string.Equals(rf, reference, StringComparison.OrdinalIgnoreCase)) return x;
                if (rf != null && Xlsx.ParseRef(rf, out c, out r) && c > col) { before = x; break; }
            }
            var cell = new XElement(Ns + "c", new XAttribute("r", reference));
            if (before != null) before.AddBeforeSelf(cell); else row.Add(cell);
            return cell;
        }

        static XDocument Load(byte[] data)
        {
            using (var ms = new MemoryStream(data)) return XDocument.Load(ms, LoadOptions.PreserveWhitespace);
        }

        static byte[] Save(XDocument d)
        {
            using (var ms = new MemoryStream())
            {
                var st = new System.Xml.XmlWriterSettings();
                st.Encoding = new UTF8Encoding(false);
                using (var w = System.Xml.XmlWriter.Create(ms, st)) d.Save(w);
                return ms.ToArray();
            }
        }
    }

    public static class LibreOffice
    {
        // soffice on this machine; null when there is none
        public static string Find()
        {
            var places = new List<string>();
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string dir in path.Split(Path.PathSeparator))
            {
                if (dir.Length == 0) continue;
                places.Add(Path.Combine(dir, "soffice"));
                places.Add(Path.Combine(dir, "soffice.exe"));
            }
            places.Add("/usr/bin/soffice");
            places.Add("/usr/lib/libreoffice/program/soffice");
            places.Add("/Applications/LibreOffice.app/Contents/MacOS/soffice");
            foreach (string pf in new[] { Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramFiles(x86)") })
                if (!string.IsNullOrEmpty(pf)) places.Add(Path.Combine(Path.Combine(Path.Combine(pf, "LibreOffice"), "program"), "soffice.exe"));
            foreach (string p in places)
                if (File.Exists(p)) return p;
            return null;
        }

        static string _version;

        // "LibreOffice 24.2.7.2 ..." ("" when it does not say)
        public static string Version(string soffice)
        {
            if (_version != null) return _version;
            try
            {
                var psi = new ProcessStartInfo(soffice, "--version");
                psi.UseShellExecute = false; psi.RedirectStandardOutput = true; psi.CreateNoWindow = true;
                using (Process p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(60000);
                    _version = o.Trim().Split('\n')[0].Trim();
                }
            }
            catch (Exception) { _version = ""; }
            return _version;
        }

        // a profile of CalcFlow's own: every formula of an Excel or ODF file is recalculated when it opens
        public static string Profile(string dir)
        {
            string user = Path.Combine(dir, "user");
            Directory.CreateDirectory(user);
            string xcu = Path.Combine(user, "registrymodifications.xcu");
            const string text = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                "<oor:items xmlns:oor=\"http://openoffice.org/2001/registry\" xmlns:xs=\"http://www.w3.org/2001/XMLSchema\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">\n" +
                "<item oor:path=\"/org.openoffice.Office.Calc/Formula/Load\"><prop oor:name=\"OOXMLRecalcMode\" oor:op=\"fuse\"><value>0</value></prop></item>\n" +
                "<item oor:path=\"/org.openoffice.Office.Calc/Formula/Load\"><prop oor:name=\"ODFRecalcMode\" oor:op=\"fuse\"><value>0</value></prop></item>\n" +
                "</oor:items>\n";
            if (!File.Exists(xcu)) File.WriteAllText(xcu, text);
            return new Uri(Path.GetFullPath(dir)).AbsoluteUri;
        }

        // converts files (each opened, recalculated, saved as `filter`) into outDir; returns soffice's output.
        // filter: "xlsx" (Calc MS Excel 2007 XML), "xlsm" (the same with its macros), "pdf", "csv" ... Throws when soffice
        // fails or runs past timeoutSeconds, and (requireAll) when a file was not written.
        public static string Convert(string soffice, IList<string> files, string outDir, string filter, string profileDir, int timeoutSeconds)
        {
            return Convert(soffice, files, outDir, filter, profileDir, timeoutSeconds, true);
        }

        public static string Convert(string soffice, IList<string> files, string outDir, string filter, string profileDir, int timeoutSeconds, bool requireAll)
        {
            Directory.CreateDirectory(outDir);
            string conv = filter == "xlsx" ? "xlsx:\"Calc MS Excel 2007 XML\"" : filter == "xlsm" ? "xlsm:\"Calc MS Excel 2007 VBA XML\"" : filter;
            var args = new StringBuilder();
            args.Append("-env:UserInstallation=").Append(Profile(profileDir)).Append(" --headless --norestore --nolockcheck --nodefault --convert-to ").Append(conv);
            args.Append(" --outdir \"").Append(outDir).Append("\"");
            foreach (string f in files) args.Append(" \"").Append(f).Append("\"");
            var psi = new ProcessStartInfo(soffice, args.ToString());
            psi.UseShellExecute = false; psi.RedirectStandardOutput = true; psi.RedirectStandardError = true; psi.CreateNoWindow = true;
            var output = new StringBuilder();
            using (Process p = new Process())
            {
                p.StartInfo = psi;
                p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
                p.Start();
                p.BeginOutputReadLine(); p.BeginErrorReadLine();
                if (!p.WaitForExit(timeoutSeconds * 1000))
                {
                    try { p.Kill(); } catch (InvalidOperationException) { }
                    throw new TimeoutException("LibreOffice did not finish converting " + files.Count + " file(s) within " + timeoutSeconds + " s");
                }
                p.WaitForExit();
                if (p.ExitCode != 0) throw new Exception("LibreOffice failed (exit " + p.ExitCode + "): " + output.ToString().Trim());
            }
            if (!requireAll) return output.ToString();
            foreach (string f in files)
            {
                string expect = Path.Combine(outDir, Path.GetFileNameWithoutExtension(f) + "." + filter.Split(':')[0]);
                if (!File.Exists(expect)) throw new Exception("LibreOffice did not write " + expect + ": " + output.ToString().Trim());
            }
            return output.ToString();
        }
    }
}
