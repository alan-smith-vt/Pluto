using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

// CaesarResults  --  CAESAR II analysis output written to Excel (Output Processor -> Microsoft Excel, one report
// per tab) -> load cases, node displacements, restraint loads, local element forces and code stresses.
// C# 5 / Add-Type (PowerShell 5.1) compatible, no Excel needed; every type is prefixed Caesar (one global batch).
//
// Workbook (CaesarWorkbook). An .xlsx is read as a zip of XML, opened with FileShare.ReadWrite so a workbook
// still open in Excel reads too: the officeDocument part from _rels/.rels, sheets in workbook order through the
// workbook relationships (relative or absolute targets), shared strings (rich text runs joined, phonetic runs
// skipped) and inline strings, every cell at its true row / column (the r reference; a missing r = the next
// one), blank rows kept. Elements are matched by local name, so transitional and strict files both read.
// A text file reads as CSV (one sheet; the separator , ; or tab by count). An .xls (BIFF) or an .xlsb (binary
// parts in the zip) is refused with a message: Run-Caesar.ps1 converts both to .xlsx through Excel first.
//
// Tables. Each tab is a report: a title row ("DISPLACEMENTS REPORT: Nodal Movements"), the load case label
// ("CASE 1 (OPE) W+T1+P1"), then a header row whose first cell is "Node", each header carrying its unit
// ("DX in.", "fx lb.", "Code lb./sq.in."). The report kind comes from the title, else from the headers:
//   DISPLACEMENTS            one row per node: DX DY DZ RX RY RZ
//   RESTRAINT SUMMARY        node-major: a node row ("TYPE=Rigid +Y; Rigid GUI;"), then one row per case
//                            ("  1(OPE)") with FX FY FZ MX MY MZ, then a MAX row (skipped). Loads ON the
//                            restraints, CAESAR's sign, summed per node by CAESAR
//   RESTRAINTS REPORT        a single-case alternative: rows per restraint, summed here per node
//   LOCAL ELEMENT FORCES     two rows per element segment (FROM node, TO node; bends split at their nodes):
//                            fx fy fz mx my mz in the element's local axes, as reported (end forces)
//   <code> STRESSES REPORT   two rows per segment: SLP F/A Bending Torsion, the four SIFs, Code, Allowable,
//                            Ratio (blank cells -- rigid elements, cases without a check -- are NaN)
// GLOBAL ELEMENT FORCES and CODE COMPLIANCE tabs are recognised and not used (the per-case stress tabs carry
// the same code stresses); anything else is listed as not used. Load cases are keyed on CAESAR's case NUMBER;
// the name comes from the longest label seen ("CASE 4 (EXP) L4=L1-L3"), the definition key included. A row
// or a case that cannot be read is a warning, never an exception.
//
// Use: var r = CaesarResults.Read(path); r.Cases, r.Displacements[case][node] = {DX,DY,DZ,RX,RY,RZ}, ...

public class CaesarSheet
{
    public string Name = "";
    public readonly List<string[]> Rows = new List<string[]>();   // Rows[r] = cells of spreadsheet row r+1 (null = no cells)

    public string Cell(int row, int col)
    {
        if (row < 0 || row >= Rows.Count) return null;
        string[] r = Rows[row];
        return r == null || col < 0 || col >= r.Length ? null : r[col];
    }

    public string Text(int row, int col)
    {
        string s = Cell(row, col);
        return s == null ? "" : s.Trim();
    }

    public void Set(int row, int col, string value)
    {
        while (Rows.Count <= row) Rows.Add(null);
        string[] r = Rows[row];
        if (r == null || r.Length <= col)
        {
            var n = new string[Math.Max(col + 1, r == null ? 8 : r.Length * 2)];
            if (r != null) Array.Copy(r, n, r.Length);
            r = n;
            Rows[row] = r;
        }
        r[col] = value;
    }
}

public static class CaesarWorkbook
{
    public static List<CaesarSheet> Read(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) throw new Exception("CaesarWorkbook: file not found: " + path);
        byte[] head = new byte[8];
        int n;
        using (var fs = OpenShared(path)) n = fs.Read(head, 0, head.Length);
        if (n >= 4 && head[0] == 0xD0 && head[1] == 0xCF && head[2] == 0x11 && head[3] == 0xE0)
            throw new Exception("CaesarWorkbook: " + path + " is an Excel 97-2003 workbook (.xls): save it as .xlsx " +
                                "(Run-Caesar.ps1 converts it through Excel), or export the reports to .xlsx.");
        if (n >= 2 && head[0] == (byte)'P' && head[1] == (byte)'K')
        {
            using (var fs = OpenShared(path)) return ReadXlsx(fs);
        }
        var one = new List<CaesarSheet>();
        one.Add(ReadCsv(path));
        return one;
    }

    // FileShare.ReadWrite: Excel holds a workbook it has open for writing; reading still works
    static FileStream OpenShared(string path)
    {
        try { return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
        catch (IOException ex)
        {
            throw new Exception("CaesarWorkbook: cannot open " + path + " (" + ex.Message + "). Close it in Excel and run again.");
        }
    }

    // ---- xlsx -----------------------------------------------------------------------------------
    public static List<CaesarSheet> ReadXlsx(Stream s)
    {
        var sheets = new List<CaesarSheet>();
        using (var zip = new ZipArchive(s, ZipArchiveMode.Read, true))
        {
            string wbPath = null;
            foreach (string[] r in Rels(zip, "_rels/.rels", ""))
                if (r[1].EndsWith("/officeDocument", StringComparison.OrdinalIgnoreCase)) { wbPath = r[2]; break; }
            if (wbPath == null || Entry(zip, wbPath) == null) wbPath = "xl/workbook.xml";
            if (wbPath.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) || (Entry(zip, wbPath) == null && Entry(zip, "xl/workbook.bin") != null))
                throw new Exception("CaesarWorkbook: a binary workbook (.xlsb): save it as .xlsx (Run-Caesar.ps1 converts it through Excel).");
            if (Entry(zip, wbPath) == null) throw new Exception("CaesarWorkbook: no workbook part in the .xlsx (not an Excel workbook?)");
            string wbDir = DirOf(wbPath);
            var idToTarget = new Dictionary<string, string>(StringComparer.Ordinal);
            string ssPath = null;
            foreach (string[] r in Rels(zip, wbDir + "_rels/" + FileOf(wbPath) + ".rels", wbDir))
            {
                idToTarget[r[0]] = r[2];
                if (r[1].EndsWith("/sharedStrings", StringComparison.OrdinalIgnoreCase)) ssPath = r[2];
            }
            if (ssPath == null && Entry(zip, wbDir + "sharedStrings.xml") != null) ssPath = wbDir + "sharedStrings.xml";
            List<string> shared = ssPath != null ? SharedStrings(zip, ssPath) : new List<string>();

            // <sheet name=".." r:id=".."/> in workbook order (the id attribute's namespace differs between
            // transitional and strict files: match the local name)
            var order = new List<string[]>();
            using (XmlReader xr = Xml(Entry(zip, wbPath)))
                while (xr.Read())
                {
                    if (xr.NodeType != XmlNodeType.Element || xr.LocalName != "sheet") continue;
                    string name = xr.GetAttribute("name"), rid = null;
                    if (xr.MoveToFirstAttribute())
                    {
                        do { if (xr.LocalName == "id" && xr.NamespaceURI.Length > 0) rid = xr.Value; } while (xr.MoveToNextAttribute());
                        xr.MoveToElement();
                    }
                    order.Add(new[] { name ?? "", rid ?? "" });
                }
            int k = 0;
            foreach (string[] sh in order)
            {
                k++;
                string target;
                if (!idToTarget.TryGetValue(sh[1], out target)) target = wbDir + "worksheets/sheet" + k.ToString(CultureInfo.InvariantCulture) + ".xml";
                ZipArchiveEntry e = Entry(zip, target);
                if (e == null) continue;            // a chart sheet or a missing part: nothing to read
                var sheet = new CaesarSheet();
                sheet.Name = sh[0];
                ReadSheet(e, shared, sheet);
                sheets.Add(sheet);
            }
        }
        return sheets;
    }

    static XmlReader Xml(ZipArchiveEntry e)
    {
        var st = new XmlReaderSettings();
        st.DtdProcessing = DtdProcessing.Prohibit;
        st.XmlResolver = null;
        st.IgnoreComments = true;
        st.IgnoreProcessingInstructions = true;
        st.CloseInput = true;
        return XmlReader.Create(e.Open(), st);
    }

    static ZipArchiveEntry Entry(ZipArchive zip, string path)
    {
        if (path == null) return null;
        string p = path.Replace('\\', '/').TrimStart('/');
        ZipArchiveEntry e = zip.GetEntry(p);
        if (e != null) return e;
        foreach (ZipArchiveEntry x in zip.Entries)
            if (string.Equals(x.FullName.Replace('\\', '/').TrimStart('/'), p, StringComparison.OrdinalIgnoreCase)) return x;
        return null;
    }

    static string DirOf(string p)
    {
        int i = p.LastIndexOf('/');
        return i < 0 ? "" : p.Substring(0, i + 1);
    }

    static string FileOf(string p)
    {
        int i = p.LastIndexOf('/');
        return i < 0 ? p : p.Substring(i + 1);
    }

    // relationships: {Id, Type, resolved target path}; external targets skipped
    static List<string[]> Rels(ZipArchive zip, string relsPath, string baseDir)
    {
        var list = new List<string[]>();
        ZipArchiveEntry e = Entry(zip, relsPath);
        if (e == null) return list;
        using (XmlReader xr = Xml(e))
            while (xr.Read())
            {
                if (xr.NodeType != XmlNodeType.Element || xr.LocalName != "Relationship") continue;
                if (string.Equals(xr.GetAttribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase)) continue;
                string t = xr.GetAttribute("Target") ?? "";
                list.Add(new[] { xr.GetAttribute("Id") ?? "", xr.GetAttribute("Type") ?? "", Resolve(baseDir, t) });
            }
        return list;
    }

    static string Resolve(string baseDir, string target)
    {
        string t = target.Replace('\\', '/');
        if (t.StartsWith("/", StringComparison.Ordinal)) return t.TrimStart('/');
        var parts = new List<string>((baseDir + t).Split('/'));
        var outp = new List<string>();
        foreach (string p in parts)
        {
            if (p.Length == 0 || p == ".") continue;
            if (p == "..") { if (outp.Count > 0) outp.RemoveAt(outp.Count - 1); continue; }
            outp.Add(p);
        }
        return string.Join("/", outp.ToArray());
    }

    static List<string> SharedStrings(ZipArchive zip, string path)
    {
        var list = new List<string>();
        ZipArchiveEntry e = Entry(zip, path);
        if (e == null) return list;
        var sb = new StringBuilder();
        bool inSi = false, inT = false;
        int phonetic = 0;
        using (XmlReader xr = Xml(e))
            while (xr.Read())
            {
                switch (xr.NodeType)
                {
                    case XmlNodeType.Element:
                        if (xr.LocalName == "si") { inSi = true; sb.Length = 0; if (xr.IsEmptyElement) { list.Add(""); inSi = false; } }
                        else if (xr.LocalName == "rPh" && !xr.IsEmptyElement) phonetic++;
                        else if (xr.LocalName == "t" && inSi && !xr.IsEmptyElement) inT = true;
                        break;
                    case XmlNodeType.Text:
                    case XmlNodeType.CDATA:
                    case XmlNodeType.Whitespace:
                    case XmlNodeType.SignificantWhitespace:
                        if (inT && phonetic == 0) sb.Append(xr.Value);
                        break;
                    case XmlNodeType.EndElement:
                        if (xr.LocalName == "t") inT = false;
                        else if (xr.LocalName == "rPh") phonetic = Math.Max(0, phonetic - 1);
                        else if (xr.LocalName == "si") { list.Add(sb.ToString()); inSi = false; }
                        break;
                }
            }
        return list;
    }

    // cells -> sheet.Rows at their true positions; s = shared string, inlineStr = <is><t>, else the <v> text
    static void ReadSheet(ZipArchiveEntry e, List<string> shared, CaesarSheet sheet)
    {
        int row = -1, col = -1;
        string type = null;
        bool inCell = false, inV = false, inT = false, inSheetData = false;
        int phonetic = 0;
        var v = new StringBuilder();
        var t = new StringBuilder();
        using (XmlReader xr = Xml(e))
            while (xr.Read())
            {
                switch (xr.NodeType)
                {
                    case XmlNodeType.Element:
                        switch (xr.LocalName)
                        {
                            case "sheetData": inSheetData = !xr.IsEmptyElement; break;
                            case "row":
                                if (!inSheetData) break;
                                int rn;
                                string ra = xr.GetAttribute("r");
                                row = ra != null && int.TryParse(ra, NumberStyles.Integer, CultureInfo.InvariantCulture, out rn) && rn > 0 ? rn - 1 : row + 1;
                                col = -1;
                                break;
                            case "c":
                                if (!inSheetData) break;
                                string cr = xr.GetAttribute("r");
                                int cc, crow;
                                if (ParseRef(cr, out cc, out crow)) { col = cc; if (crow >= 0) row = crow; } else col++;
                                if (row < 0) row = 0;
                                type = xr.GetAttribute("t");
                                v.Length = 0; t.Length = 0;
                                inCell = !xr.IsEmptyElement;
                                break;
                            case "v": if (inCell && !xr.IsEmptyElement) inV = true; break;
                            case "t": if (inCell && !xr.IsEmptyElement) inT = true; break;
                            case "rPh": if (inCell && !xr.IsEmptyElement) phonetic++; break;
                        }
                        break;
                    case XmlNodeType.Text:
                    case XmlNodeType.CDATA:
                    case XmlNodeType.Whitespace:
                    case XmlNodeType.SignificantWhitespace:
                        if (inV) v.Append(xr.Value);
                        else if (inT && phonetic == 0) t.Append(xr.Value);
                        break;
                    case XmlNodeType.EndElement:
                        switch (xr.LocalName)
                        {
                            case "sheetData": inSheetData = false; break;
                            case "v": inV = false; break;
                            case "t": inT = false; break;
                            case "rPh": phonetic = Math.Max(0, phonetic - 1); break;
                            case "c":
                                if (inCell)
                                {
                                    string val = null;
                                    if (type == "s")
                                    {
                                        int si;
                                        if (int.TryParse(v.ToString().Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out si) && si >= 0 && si < shared.Count) val = shared[si];
                                    }
                                    else if (type == "inlineStr") val = t.ToString();
                                    else if (v.Length > 0) val = v.ToString();
                                    else if (t.Length > 0) val = t.ToString();
                                    if (val != null) sheet.Set(row, col, val);
                                }
                                inCell = false;
                                break;
                        }
                        break;
                }
            }
    }

    // "AB12" -> col 27 (0-based), row 11 (0-based); a reference without digits gives row -1
    static bool ParseRef(string r, out int col, out int row)
    {
        col = -1; row = -1;
        if (string.IsNullOrEmpty(r)) return false;
        int i = 0, c = 0;
        while (i < r.Length && char.IsLetter(r[i])) { c = c * 26 + (char.ToUpperInvariant(r[i]) - 'A' + 1); i++; }
        if (i == 0 || c <= 0) return false;
        col = c - 1;
        int rn;
        if (i < r.Length && int.TryParse(r.Substring(i), NumberStyles.Integer, CultureInfo.InvariantCulture, out rn) && rn > 0) row = rn - 1;
        return true;
    }

    // ---- csv ------------------------------------------------------------------------------------
    public static CaesarSheet ReadCsv(string path)
    {
        string text;
        using (var fs = OpenShared(path))
        using (var sr = new StreamReader(fs, Encoding.UTF8, true)) text = sr.ReadToEnd();
        var sheet = new CaesarSheet();
        sheet.Name = Path.GetFileNameWithoutExtension(path);
        char sep = Separator(text);
        int row = 0, col = 0;
        var cell = new StringBuilder();
        bool quoted = false, any = false;
        for (int i = 0; i <= text.Length; i++)
        {
            char ch = i < text.Length ? text[i] : '\n';
            if (quoted)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                    else quoted = false;
                }
                else cell.Append(ch);
                continue;
            }
            if (ch == '"') { quoted = true; any = true; continue; }
            if (ch == sep) { if (cell.Length > 0 || any) sheet.Set(row, col, cell.ToString()); cell.Length = 0; any = false; col++; continue; }
            if (ch == '\r') continue;
            if (ch == '\n')
            {
                if (cell.Length > 0 || any) sheet.Set(row, col, cell.ToString());
                cell.Length = 0; any = false; col = 0; row++;
                continue;
            }
            cell.Append(ch);
        }
        return sheet;
    }

    static char Separator(string text)
    {
        int n = 0, commas = 0, semis = 0, tabs = 0;
        bool quoted = false;
        foreach (char ch in text)
        {
            if (ch == '"') quoted = !quoted;
            else if (quoted) continue;
            else if (ch == ',') commas++;
            else if (ch == ';') semis++;
            else if (ch == '\t') tabs++;
            else if (ch == '\n' && ++n > 200) break;
        }
        if (tabs > commas && tabs > semis) return '\t';
        return semis > commas ? ';' : ',';
    }
}

public class CaesarLoadCase
{
    public int Number;                   // CAESAR's case number: L<n>
    public string Stress = "";           // OPE, SUS, EXP, OCC, HYD, HGR, FAT, ...
    public string Text = "";             // what follows the type: "W+T1+P1", "L4=L1-L3"
    public bool IsCombination { get { return Text.IndexOf('=') >= 0; } }

    // "L1 (OPE) W+T1+P1", the way CAESAR names cases in its combinations
    public string Name
    {
        get
        {
            string s = "L" + Number.ToString(CultureInfo.InvariantCulture);
            if (Stress.Length > 0) s += " (" + Stress + ")";
            if (Text.Length > 0) s += " " + Text;
            return s;
        }
    }
}

// One element segment of a two-row report: FROM row, TO row; values in the report's column order.
public class CaesarSegmentResult
{
    public int From, To;
    public double[] AtFrom, AtTo;
    public string ElementName = "";
    public int Row;                      // 1-based spreadsheet row of the FROM row
}

public class CaesarResults
{
    // column keys of the stress table (Stresses[case][i].AtFrom[k] follows this order; a missing column is NaN)
    public static readonly string[] StressKeys = { "SLP", "F/A", "BENDING", "TORSION", "SIF IN", "SIF OUT", "SIF T", "SIF A", "CODE", "ALLOWABLE", "RATIO" };
    public static readonly string[] ForceKeys = { "FX", "FY", "FZ", "MX", "MY", "MZ" };
    public static readonly string[] DispKeys = { "DX", "DY", "DZ", "RX", "RY", "RZ" };

    public string SourcePath = "";
    public readonly List<CaesarLoadCase> Cases = new List<CaesarLoadCase>();                      // ascending number

    public readonly Dictionary<int, Dictionary<int, double[]>> Displacements = new Dictionary<int, Dictionary<int, double[]>>();   // case -> node -> DX..RZ
    public string DispUnit = "", RotUnit = "";                                                     // as written: "in.", "deg."

    public readonly Dictionary<int, Dictionary<int, double[]>> RestraintLoads = new Dictionary<int, Dictionary<int, double[]>>(); // case -> node -> FX..MZ
    public readonly Dictionary<int, string> RestraintTypeText = new Dictionary<int, string>();  // node -> "Rigid +Y; Rigid GUI;"
    public string RestraintForceUnit = "", RestraintMomentUnit = "";

    public readonly Dictionary<int, List<CaesarSegmentResult>> LocalForces = new Dictionary<int, List<CaesarSegmentResult>>();   // case -> segments (fx..mz)
    public string ForceUnit = "", MomentUnit = "";

    public readonly Dictionary<int, List<CaesarSegmentResult>> Stresses = new Dictionary<int, List<CaesarSegmentResult>>();      // case -> segments (StressKeys)
    public string StressUnit = "", StressCode = "";
    public readonly bool[] StressHasColumn = new bool[11];

    public readonly List<string> TablesRead = new List<string>();      // "(1)Displacements: displacements, case 1, 153 nodes"
    public readonly List<string> TablesSkipped = new List<string>();
    public readonly List<string> Warnings = new List<string>();

    readonly Dictionary<int, CaesarLoadCase> _cases = new Dictionary<int, CaesarLoadCase>();

    static readonly Regex CaseLabel = new Regex(@"^\s*(?:LOAD\s*CASE|CASE)\s*:?\s*L?(\d+)\s*\(\s*([A-Za-z][A-Za-z0-9 \-/]*?)\s*\)\s*(.*?)\s*$", RegexOptions.IgnoreCase);
    static readonly Regex ShortCase = new Regex(@"^\s*L?(\d+)\s*\(\s*([A-Za-z][A-Za-z0-9 \-/]*?)\s*\)\s*(.*?)\s*$", RegexOptions.IgnoreCase);
    static readonly Regex SheetNumber = new Regex(@"^\s*\(\s*(\d+)\s*\)");

    public static CaesarResults Read(string path)
    {
        var r = Parse(CaesarWorkbook.Read(path));
        r.SourcePath = path ?? "";
        return r;
    }

    public static CaesarResults Parse(List<CaesarSheet> sheets)
    {
        var r = new CaesarResults();
        foreach (CaesarSheet sh in sheets)
        {
            try { r.ReadSheet(sh); }
            catch (Exception ex) { r.Warnings.Add("tab \"" + sh.Name + "\" could not be read: " + ex.Message); }
        }
        foreach (int n in r._cases.Keys.OrderBy(k => k)) r.Cases.Add(r._cases[n]);
        // the definition key and labels name the cases; a case with data but no label keeps its number
        return r;
    }

    // ---- one tab --------------------------------------------------------------------------------
    void ReadSheet(CaesarSheet sh)
    {
        // case labels anywhere in column A (the definition key on the summary tabs, the label above a table)
        for (int i = 0; i < sh.Rows.Count; i++) NoteCase(sh.Text(i, 0));

        var headers = new List<int>();
        for (int i = 0; i < sh.Rows.Count; i++)
            if (string.Equals(sh.Text(i, 0), "Node", StringComparison.OrdinalIgnoreCase) && Count(sh, i) >= 3) headers.Add(i);
        if (headers.Count == 0)
        {
            if (!sh.Rows.Any(x => x != null)) return;
            string title = Title(sh, Math.Min(sh.Rows.Count, 12));
            TablesSkipped.Add(Quote(sh.Name) + ": " + (title.Length > 0 ? "\"" + title + "\" not used" : "no table (no header row starting with \"Node\")"));
            return;
        }
        for (int h = 0; h < headers.Count; h++)
        {
            int hdr = headers[h], end = h + 1 < headers.Count ? headers[h + 1] : sh.Rows.Count;
            string title = Title(sh, hdr);
            string kind = Kind(title, sh, hdr);
            switch (kind)
            {
                case "displacements": ReadDisplacements(sh, hdr, end, title); break;
                case "summary": ReadSummary(sh, hdr, end); break;
                case "restraints": ReadRestraintReport(sh, hdr, end, title); break;
                case "forces": ReadSegments(sh, hdr, end, title, false); break;
                case "stresses": ReadSegments(sh, hdr, end, title, true); break;
                default: TablesSkipped.Add(Quote(sh.Name) + ": " + (title.Length > 0 ? "\"" + title + "\"" : "a table") + " not used"); break;
            }
        }
    }

    static int Count(CaesarSheet sh, int row)
    {
        string[] r = row < sh.Rows.Count ? sh.Rows[row] : null;
        return r == null ? 0 : r.Count(c => c != null && c.Trim().Length > 0);
    }

    static string Quote(string s) { return "tab \"" + s + "\""; }

    // the nearest "... REPORT ..." text above the header (column A)
    static string Title(CaesarSheet sh, int hdr)
    {
        for (int i = hdr - 1; i >= 0 && i >= hdr - 12; i--)
        {
            string t = sh.Text(i, 0);
            if (t.IndexOf("REPORT", StringComparison.OrdinalIgnoreCase) >= 0) return t;
        }
        return "";
    }

    static string Kind(string title, CaesarSheet sh, int hdr)
    {
        string t = title.ToUpperInvariant();
        if (t.Contains("RESTRAINT SUMMARY")) return "summary";
        if (t.Contains("DISPLACEMENT")) return "displacements";
        if (t.Contains("LOCAL ELEMENT FORCES")) return "forces";
        if (t.Contains("GLOBAL ELEMENT FORCES") || t.Contains("CODE COMPLIANCE")) return "skip";
        if (t.Contains("STRESSES REPORT")) return "stresses";
        if (t.Contains("RESTRAINTS REPORT") || t.Contains("LOADS ON RESTRAINTS")) return "restraints";
        if (t.Length > 0) return "skip";
        // no title: decide from the headers
        var keys = HeaderKeys(sh, hdr);
        if (keys.ContainsKey("DX") && keys.ContainsKey("DY")) return "displacements";
        if (keys.ContainsKey("LOADCASE") && keys.ContainsKey("FX")) return "summary";
        if (keys.ContainsKey("CODE") && keys.ContainsKey("BENDING")) return "stresses";
        if (keys.ContainsKey("FX") && (keys.ContainsKey("ELEMENTNAME") || HeaderIsLower(sh, hdr, "fx"))) return "forces";
        if (keys.ContainsKey("FX")) return "restraints";
        return "skip";
    }

    static bool HeaderIsLower(CaesarSheet sh, int hdr, string name)
    {
        string[] r = sh.Rows[hdr];
        if (r == null) return false;
        foreach (string c in r)
            if (c != null && c.Trim().StartsWith(name, StringComparison.Ordinal)) return true;
        return false;
    }

    // header cell -> {normalised key, unit}; "DX in." -> DX / in.   "SIF/Index In-Plane" -> SIF IN / ""
    static Dictionary<string, int> HeaderKeys(CaesarSheet sh, int hdr)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        string[] r = sh.Rows[hdr];
        if (r == null) return map;
        for (int c = 0; c < r.Length; c++)
        {
            if (r[c] == null || r[c].Trim().Length == 0) continue;
            string unit;
            string key = HeaderKey(r[c], out unit);
            if (key.Length > 0 && !map.ContainsKey(key)) map[key] = c;
        }
        return map;
    }

    static readonly string[] BareUnits = { "in", "mm", "cm", "m", "ft", "deg", "rad", "lb", "lbf", "kip", "kips", "n", "kn", "psi", "ksi", "kpa", "mpa", "pa", "bar", "%" };

    static string HeaderKey(string cell, out string unit)
    {
        unit = "";
        string s = Regex.Replace(cell.Trim(), @"\s+", " ");
        string[] parts = s.Split(' ');
        int n = parts.Length;
        if (n > 1)
        {
            string last = parts[n - 1];
            string lo = last.ToLowerInvariant().TrimEnd('.');
            if (last.IndexOf('.') >= 0 || last.IndexOf('/') >= 0 || last == "%" || Array.IndexOf(BareUnits, lo) >= 0)
            {
                unit = last;
                s = string.Join(" ", parts, 0, n - 1);
            }
        }
        string k = s.ToUpperInvariant().Replace(" ", "");
        if (k.StartsWith("SIF/INDEX", StringComparison.Ordinal) || k.StartsWith("SIF", StringComparison.Ordinal))
        {
            if (k.Contains("IN-PLANE") || k.Contains("INPLANE")) return "SIF IN";
            if (k.Contains("OUT-PLANE") || k.Contains("OUTPLANE")) return "SIF OUT";
            if (k.Contains("TORSION")) return "SIF T";
            if (k.Contains("AXIAL")) return "SIF A";
        }
        if (k == "RATIO%" ) return "RATIO";
        if (k.StartsWith("ALLOWABLE", StringComparison.Ordinal)) return "ALLOWABLE";
        if (k == "CODE" || k == "CODESTRESS") return "CODE";
        return k;
    }

    static string UnitOf(CaesarSheet sh, int hdr, int col)
    {
        string unit;
        HeaderKey(sh.Text(hdr, col), out unit);
        return unit;
    }

    // a case label ("CASE 1 (OPE) W+T1+P1") -> the case table; the longest text for a number wins
    CaesarLoadCase NoteCase(string text)
    {
        if (text == null || text.Length == 0) return null;
        Match mt = CaseLabel.Match(text);
        if (!mt.Success) return null;
        return Case(int.Parse(mt.Groups[1].Value, CultureInfo.InvariantCulture), mt.Groups[2].Value, mt.Groups[3].Value);
    }

    CaesarLoadCase Case(int number, string stress, string label)
    {
        CaesarLoadCase c;
        if (!_cases.TryGetValue(number, out c)) { c = new CaesarLoadCase(); c.Number = number; _cases[number] = c; }
        stress = (stress ?? "").Trim().ToUpperInvariant();
        label = (label ?? "").Trim();
        if (stress.Length > 0 && c.Stress.Length == 0) c.Stress = stress;
        if (label.Length > c.Text.Length) c.Text = label;
        return c;
    }

    // the case of a single-case table: the label between the title and the header, else "(n)" in the tab name
    CaesarLoadCase TableCase(CaesarSheet sh, int hdr)
    {
        for (int i = hdr - 1; i >= 0 && i >= hdr - 12; i--)
        {
            CaesarLoadCase c = NoteCase(sh.Text(i, 0));
            if (c != null) return c;
        }
        Match mt = SheetNumber.Match(sh.Name ?? "");
        if (mt.Success) return Case(int.Parse(mt.Groups[1].Value, CultureInfo.InvariantCulture), "", "");
        return null;
    }

    static bool TryNode(string s, out int node)
    {
        node = 0;
        double d;
        if (!TryNum(s, out d)) return false;
        if (d < 0 || d > int.MaxValue || Math.Abs(d - Math.Round(d)) > 1e-6) return false;
        node = (int)Math.Round(d);
        return node > 0;
    }

    public static bool TryNum(string s, out double v)
    {
        v = double.NaN;
        if (s == null) return false;
        string t = s.Trim();
        if (t.Length == 0) return false;
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return true;
        // a CSV saved in a decimal-comma locale: "-1,25"
        if (t.IndexOf(',') >= 0 && t.IndexOf('.') < 0 &&
            double.TryParse(t.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return true;
        v = double.NaN;
        return false;
    }

    static double Num(CaesarSheet sh, int row, int col)
    {
        if (col < 0) return double.NaN;
        double v;
        return TryNum(sh.Cell(row, col), out v) ? v : double.NaN;
    }

    // ---- displacements: one row per node ----
    void ReadDisplacements(CaesarSheet sh, int hdr, int end, string title)
    {
        CaesarLoadCase c = TableCase(sh, hdr);
        if (c == null) { Warnings.Add(Quote(sh.Name) + ": displacement table without a load case label: not used"); return; }
        var keys = HeaderKeys(sh, hdr);
        int[] cols = DispKeys.Select(k => keys.ContainsKey(k) ? keys[k] : -1).ToArray();
        if (cols[0] < 0 || cols[1] < 0 || cols[2] < 0) { Warnings.Add(Quote(sh.Name) + ": no DX / DY / DZ columns: not used"); return; }
        if (DispUnit.Length == 0) DispUnit = UnitOf(sh, hdr, cols[0]);
        if (RotUnit.Length == 0 && cols[3] >= 0) RotUnit = UnitOf(sh, hdr, cols[3]);
        Dictionary<int, double[]> map;
        if (!Displacements.TryGetValue(c.Number, out map)) { map = new Dictionary<int, double[]>(); Displacements[c.Number] = map; }
        int n = 0, bad = 0;
        for (int i = hdr + 1; i < end; i++)
        {
            int node;
            if (!TryNode(sh.Cell(i, 0), out node)) continue;
            var v = new double[6];
            for (int k = 0; k < 6; k++) v[k] = Num(sh, i, cols[k]);
            if (double.IsNaN(v[0]) && double.IsNaN(v[1]) && double.IsNaN(v[2])) { bad++; continue; }
            map[node] = v;
            n++;
        }
        TablesRead.Add(Quote(sh.Name) + ": displacements, " + c.Name + ", " + n + " nodes" + (bad > 0 ? " (" + bad + " rows without values)" : ""));
    }

    // ---- restraint summary: node row, then one row per case ----
    void ReadSummary(CaesarSheet sh, int hdr, int end)
    {
        var keys = HeaderKeys(sh, hdr);
        int[] cols = ForceKeys.Select(k => keys.ContainsKey(k) ? keys[k] : -1).ToArray();
        int caseCol = keys.ContainsKey("LOADCASE") ? keys["LOADCASE"] : 1;
        if (cols[0] < 0) { Warnings.Add(Quote(sh.Name) + ": restraint summary without FX: not used"); return; }
        if (RestraintForceUnit.Length == 0) RestraintForceUnit = UnitOf(sh, hdr, cols[0]);
        if (RestraintMomentUnit.Length == 0 && cols[3] >= 0) RestraintMomentUnit = UnitOf(sh, hdr, cols[3]);
        int node = 0, rows = 0, nodes = 0, odd = 0;
        var casesSeen = new HashSet<int>();
        for (int i = hdr + 1; i < end; i++)
        {
            int nd;
            string a = sh.Text(i, 0);
            if (TryNode(a, out nd))
            {
                node = nd; nodes++;
                for (int c = 1; c < 10; c++)
                {
                    string tx = sh.Text(i, c);
                    if (tx.StartsWith("TYPE=", StringComparison.OrdinalIgnoreCase)) { RestraintTypeText[node] = tx.Substring(5).Trim(); break; }
                }
                // a one-row-per-node layout (values on the node row) is not CAESAR's; values here are ignored
                continue;
            }
            string cs = sh.Text(i, caseCol);
            if (cs.Length == 0) continue;
            Match mt = ShortCase.Match(cs);
            if (!mt.Success) continue;                    // MAX row, notes
            if (node <= 0) { odd++; continue; }
            CaesarLoadCase lc = Case(int.Parse(mt.Groups[1].Value, CultureInfo.InvariantCulture), mt.Groups[2].Value, mt.Groups[3].Value);
            var v = new double[6];
            for (int k = 0; k < 6; k++) v[k] = Num(sh, i, cols[k]);
            Dictionary<int, double[]> map;
            if (!RestraintLoads.TryGetValue(lc.Number, out map)) { map = new Dictionary<int, double[]>(); RestraintLoads[lc.Number] = map; }
            map[node] = v;
            casesSeen.Add(lc.Number);
            rows++;
        }
        if (odd > 0) Warnings.Add(Quote(sh.Name) + ": " + odd + " case row(s) before any node row: skipped");
        TablesRead.Add(Quote(sh.Name) + ": restraint summary, " + nodes + " nodes x " + casesSeen.Count + " cases (" + rows + " rows)");
    }

    // ---- a single-case restraint report: rows per restraint, summed per node ----
    void ReadRestraintReport(CaesarSheet sh, int hdr, int end, string title)
    {
        CaesarLoadCase c = TableCase(sh, hdr);
        if (c == null) { Warnings.Add(Quote(sh.Name) + ": restraint table without a load case label: not used"); return; }
        if (RestraintLoads.ContainsKey(c.Number)) { TablesSkipped.Add(Quote(sh.Name) + ": restraints, " + c.Name + " (already read from the restraint summary)"); return; }
        var keys = HeaderKeys(sh, hdr);
        int[] cols = ForceKeys.Select(k => keys.ContainsKey(k) ? keys[k] : -1).ToArray();
        if (cols[0] < 0) return;
        if (RestraintForceUnit.Length == 0) RestraintForceUnit = UnitOf(sh, hdr, cols[0]);
        if (RestraintMomentUnit.Length == 0 && cols[3] >= 0) RestraintMomentUnit = UnitOf(sh, hdr, cols[3]);
        var map = new Dictionary<int, double[]>();
        int node = 0, rows = 0;
        for (int i = hdr + 1; i < end; i++)
        {
            int nd;
            if (TryNode(sh.Cell(i, 0), out nd)) node = nd;
            else if (sh.Text(i, 0).Length > 0) { node = 0; continue; }
            if (node <= 0) continue;
            var v = new double[6];
            bool any = false;
            for (int k = 0; k < 6; k++) { v[k] = Num(sh, i, cols[k]); if (!double.IsNaN(v[k])) any = true; }
            if (!any) continue;
            double[] sum;
            if (!map.TryGetValue(node, out sum)) { map[node] = v; }
            else for (int k = 0; k < 6; k++) sum[k] = (double.IsNaN(sum[k]) ? 0 : sum[k]) + (double.IsNaN(v[k]) ? 0 : v[k]);
            rows++;
        }
        RestraintLoads[c.Number] = map;
        TablesRead.Add(Quote(sh.Name) + ": restraints, " + c.Name + ", " + map.Count + " nodes (" + rows + " rows)");
    }

    // ---- two-row segments: local element forces or code stresses ----
    void ReadSegments(CaesarSheet sh, int hdr, int end, string title, bool stresses)
    {
        CaesarLoadCase c = TableCase(sh, hdr);
        string what = stresses ? "stresses" : "element forces";
        if (c == null) { Warnings.Add(Quote(sh.Name) + ": " + what + " without a load case label: not used"); return; }
        var keys = HeaderKeys(sh, hdr);
        string[] want = stresses ? StressKeys : ForceKeys;
        int[] cols = want.Select(k => keys.ContainsKey(k) ? keys[k] : -1).ToArray();
        if (cols.All(x => x < 0)) { Warnings.Add(Quote(sh.Name) + ": no " + what + " columns: not used"); return; }
        int nameCol = keys.ContainsKey("ELEMENTNAME") ? keys["ELEMENTNAME"] : -1;
        if (stresses)
        {
            for (int k = 0; k < cols.Length; k++) if (cols[k] >= 0) StressHasColumn[k] = true;
            int unitCol = cols[8] >= 0 ? cols[8] : cols[2];
            if (StressUnit.Length == 0 && unitCol >= 0) StressUnit = UnitOf(sh, hdr, unitCol);
            if (StressCode.Length == 0)
            {
                int at = title.IndexOf("STRESSES REPORT", StringComparison.OrdinalIgnoreCase);
                if (at > 0) StressCode = title.Substring(0, at).Trim();
            }
        }
        else
        {
            if (ForceUnit.Length == 0 && cols[0] >= 0) ForceUnit = UnitOf(sh, hdr, cols[0]);
            if (MomentUnit.Length == 0 && cols[3] >= 0) MomentUnit = UnitOf(sh, hdr, cols[3]);
        }
        // numeric node rows in order; a segment = two ADJACENT rows (CAESAR leaves a blank row between segments)
        var dataRows = new List<int>();
        for (int i = hdr + 1; i < end; i++)
        {
            int nd;
            if (TryNode(sh.Cell(i, 0), out nd)) dataRows.Add(i);
        }
        var list = new List<CaesarSegmentResult>();
        int single = 0;
        for (int j = 0; j < dataRows.Count; )
        {
            int i = dataRows[j];
            if (j + 1 < dataRows.Count && dataRows[j + 1] == i + 1)
            {
                var sgm = new CaesarSegmentResult();
                int a, b;
                TryNode(sh.Cell(i, 0), out a);
                TryNode(sh.Cell(i + 1, 0), out b);
                sgm.From = a; sgm.To = b; sgm.Row = i + 1;
                sgm.AtFrom = cols.Select(col => Num(sh, i, col)).ToArray();
                sgm.AtTo = cols.Select(col => Num(sh, i + 1, col)).ToArray();
                if (nameCol >= 0)
                {
                    string nm = sh.Text(i + 1, nameCol);
                    if (nm.Length == 0) nm = sh.Text(i, nameCol);
                    sgm.ElementName = nm;
                }
                list.Add(sgm);
                j += 2;
            }
            else { single++; j++; }
        }
        if (single > 0) Warnings.Add(Quote(sh.Name) + ": " + single + " row(s) not paired into a FROM / TO segment: skipped");
        var target = stresses ? Stresses : LocalForces;
        if (target.ContainsKey(c.Number)) Warnings.Add(Quote(sh.Name) + ": a second " + what + " table for " + c.Name + ": replaces the first");
        target[c.Number] = list;
        TablesRead.Add(Quote(sh.Name) + ": " + what + ", " + c.Name + ", " + list.Count + " segments");
    }

    public string Summary()
    {
        var sb = new StringBuilder();
        sb.AppendLine("CaesarResults: " + Cases.Count + " load case(s): " + string.Join(", ", Cases.Select(c => c.Name).ToArray()));
        sb.AppendLine(string.Format("  displacements {0} case(s) [{1} {2}], restraint loads {3} case(s) at {4} node(s) [{5} {6}], element forces {7} case(s) [{8} {9}], stresses {10} case(s) [{11} {12}]",
            Displacements.Count, DispUnit, RotUnit, RestraintLoads.Count, RestraintLoads.Values.SelectMany(x => x.Keys).Distinct().Count(),
            RestraintForceUnit, RestraintMomentUnit, LocalForces.Count, ForceUnit, MomentUnit, Stresses.Count, StressCode, StressUnit));
        foreach (string t in TablesRead) sb.AppendLine("  read    " + t);
        foreach (string t in TablesSkipped) sb.AppendLine("  skipped " + t);
        foreach (string w in Warnings) sb.AppendLine("  WARN " + w);
        return sb.ToString();
    }
}
