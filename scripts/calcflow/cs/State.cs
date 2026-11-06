// State.cs -- CalcFlow's own record files: CSV I/O, a natural sort for revision names, and the run state of each
// calc (the input hash it last ran on). Everything is written invariant-culture, UTF-8 (with BOM, so Excel opens it
// cleanly), CRLF.
// Copied from pipe-review (src/cs/State.cs at d4b33aa, 2026-11-06): Csv and NaturalComparer unchanged; CalcState /
// CalcStateStore with neutral column names (Calc, not Support Detail). The pipe-specific LoadArchive, DesignRev,
// DesignRevRegistry and RunLog were left out (CAESAR rows and the support register); CalcFlow writes its own.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace CalcFlow
{
    public static class Csv
    {
        public static List<string[]> ReadAll(string path)
        {
            var rows = new List<string[]>();
            if (!File.Exists(path)) return rows;
            string text;
            using (var fs = SharedFile.OpenRead(path))
            using (var sr = new StreamReader(fs, Encoding.UTF8, true)) text = sr.ReadToEnd();
            var cur = new List<string>();
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
                if (ch == ',') { cur.Add(cell.ToString()); cell.Length = 0; any = true; continue; }
                if (ch == '\r') continue;
                if (ch == '\n')
                {
                    if (cell.Length > 0 || any || cur.Count > 0) { cur.Add(cell.ToString()); rows.Add(cur.ToArray()); }
                    cur.Clear(); cell.Length = 0; any = false;
                    continue;
                }
                cell.Append(ch);
            }
            return rows;
        }

        // rows as header -> value dictionaries (case-insensitive)
        public static List<Dictionary<string, string>> ReadTable(string path)
        {
            var all = ReadAll(path);
            var list = new List<Dictionary<string, string>>();
            if (all.Count == 0) return list;
            string[] h = all[0];
            for (int r = 1; r < all.Count; r++)
            {
                var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int c = 0; c < h.Length; c++) d[h[c]] = c < all[r].Length ? all[r][c] : "";
                list.Add(d);
            }
            return list;
        }

        public static string Field(object v)
        {
            if (v == null) return "";
            string s;
            if (v is double) s = Num((double)v);
            else if (v is float) s = Num((float)v);
            else if (v is DateTime) s = ((DateTime)v).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            else s = Convert.ToString(v, CultureInfo.InvariantCulture);
            if (s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 || (s.Length > 0 && (s[0] == ' ' || s[s.Length - 1] == ' ')))
                s = "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        public static string Num(double d)
        {
            if (double.IsNaN(d)) return "";
            return d.ToString("R", CultureInfo.InvariantCulture);
        }

        public static void Write(string path, IList<string> header, IEnumerable<object[]> rows)
        {
            var sb = new StringBuilder();
            sb.Append(string.Join(",", header.Select(h => Field(h)).ToArray())).Append("\r\n");
            foreach (object[] r in rows) sb.Append(string.Join(",", r.Select(Field).ToArray())).Append("\r\n");
            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string tmp = SharedFile.TempBeside(path);
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(true));
            SharedFile.ReplaceWith(tmp, path);
        }

        public static double ParseNum(string s)
        {
            double d;
            if (string.IsNullOrWhiteSpace(s)) return double.NaN;
            return double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : double.NaN;
        }
    }

    // "R2" < "R10", "V6" < "V7", "Rev A" < "Rev B"; digit runs compare by value
    public sealed class NaturalComparer : IComparer<string>
    {
        public static readonly NaturalComparer Instance = new NaturalComparer();

        public int Compare(string a, string b)
        {
            a = a ?? ""; b = b ?? "";
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                bool da = char.IsDigit(a[i]), db = char.IsDigit(b[j]);
                if (da && db)
                {
                    int si = i, sj = j;
                    while (i < a.Length && char.IsDigit(a[i])) i++;
                    while (j < b.Length && char.IsDigit(b[j])) j++;
                    string na = a.Substring(si, i - si).TrimStart('0'), nb = b.Substring(sj, j - sj).TrimStart('0');
                    if (na.Length != nb.Length) return na.Length.CompareTo(nb.Length);
                    int c = string.CompareOrdinal(na, nb);
                    if (c != 0) return c;
                }
                else
                {
                    int c = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
                    if (c != 0) return c;
                    i++; j++;
                }
            }
            return (a.Length - i).CompareTo(b.Length - j);
        }

        public static string[] Sort(IEnumerable<string> items)
        {
            var l = items.ToList();
            l.Sort(Instance);
            return l.ToArray();
        }
    }


    // data\calc-state.csv : the last run of each calc
    public sealed class CalcState
    {
        public string Calc = "";
        public string Tab = "";
        public string Template = "";
        public string InputHash = "";      // hash of exactly what the calc consumed when it last ran OK
        public string TemplateStamp = "";  // size + time of the template after that run
        public string LastRun = "";
        public string Result = "";         // OK | FAILED | (blank = never run)
        public string Seconds = "";
        public string Message = "";
    }

    public sealed class CalcStateStore
    {
        public static readonly string[] Header = { "Calc", "Results Tab", "Template", "Input Hash", "Template Stamp", "Last Run", "Result", "Seconds", "Message" };
        public readonly Dictionary<string, CalcState> ByTab = new Dictionary<string, CalcState>(StringComparer.OrdinalIgnoreCase);

        public static CalcStateStore Load(string path)
        {
            var s = new CalcStateStore();
            foreach (var d in Csv.ReadTable(path))
            {
                var c = new CalcState();
                c.Calc = V(d, "Calc"); c.Tab = V(d, "Results Tab"); c.Template = V(d, "Template");
                c.InputHash = V(d, "Input Hash"); c.TemplateStamp = V(d, "Template Stamp"); c.LastRun = V(d, "Last Run");
                c.Result = V(d, "Result"); c.Seconds = V(d, "Seconds"); c.Message = V(d, "Message");
                if (c.Tab.Length > 0) s.ByTab[c.Tab] = c;
            }
            return s;
        }

        static string V(Dictionary<string, string> d, string k) { string v; return d.TryGetValue(k, out v) ? (v ?? "").Trim() : ""; }

        public CalcState Get(string tab)
        {
            CalcState c;
            return ByTab.TryGetValue(tab, out c) ? c : null;
        }

        public void Save(string path)
        {
            Csv.Write(path, Header, ByTab.Values.OrderBy(c => c.Tab, StringComparer.OrdinalIgnoreCase)
                .Select(c => new object[] { c.Calc, c.Tab, c.Template, c.InputHash, c.TemplateStamp, c.LastRun, c.Result, c.Seconds, c.Message }));
        }
    }
}
