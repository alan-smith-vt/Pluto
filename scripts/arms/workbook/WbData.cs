using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

// The workbook kit's data side (2026-11-05; from PipeWorkbook):
//   WbBase     the hidden base model (_pluto, veryHidden): format, version, the base's fingerprint, the source's name and
//              the model's JSON in cells of 30,000 characters. Rows tie to the base by their Id; what the sheets do not
//              show rides along in it. Fingerprints (SHA-256 of exact JSON), Same (a typed value equal to the base's).
//   WbRead     reading a sheet back: columns by header name (the unit in brackets apart), a cell's text with its
//              problem reported (an error value, a formula with no value), whole numbers, list entries ("3 - name"),
//              Ids ("F12"); errors and warnings collected per read.
//   WbRefresh  a workbook in this version's layout with what was typed kept as typed: every input cell's formula or
//              entry back where it was, columns matched by header (a moved column takes its cells along, references to
//              it rewritten; a column of the user's own goes after this version's), a sheet of the user's own copied as
//              values and formulas, settings kept by name.
// The xlsx is read by CaesarWorkbook (CaesarResults.cs, generic despite its name). C# 5.

public static class WbBase
{
    public const int Chunk = 30000;           // characters of base JSON per cell (Excel holds 32767)

    // the _pluto sheet, added to the book where this is called (sheet order is part of the bytes)
    public static XlsxBookSheet Write(XlsxBook b, string format, int version, string hash, string sourceName, string json)
    {
        var pl = b.Add("_pluto");
        pl.State = "veryHidden";
        string compact = Compact(json);
        int nc = (compact.Length + Chunk - 1) / Chunk;
        pl.Set(0, 0, format); pl.Set(0, 1, version);
        pl.Set(1, 0, "base"); pl.Set(1, 1, hash);
        pl.Set(2, 0, "source"); pl.Set(2, 1, sourceName ?? "");
        pl.Set(3, 0, "chunks"); pl.Set(3, 1, nc);
        for (int k = 0; k < nc; k++) pl.Set(4 + k, 0, compact.Substring(k * Chunk, Math.Min(Chunk, compact.Length - k * Chunk)));
        return pl;
    }

    public sealed class Stored { public int Version; public string Hash, Source, Json; }

    // what _pluto holds; null when the sheet is missing or of another format
    public static Stored Read(CaesarSheet pl, string format)
    {
        if (pl == null || pl.Text(0, 0) != format) return null;
        var st = new Stored { Version = (int)WbRead.ToNum(pl.Text(0, 1)), Hash = pl.Text(1, 1), Source = pl.Text(2, 1) };
        int n = (int)WbRead.ToNum(pl.Text(3, 1));
        var sb = new StringBuilder();
        for (int k = 0; k < n; k++) sb.Append(pl.Cell(4 + k, 0) ?? "");
        st.Json = sb.ToString();
        return st;
    }

    // the JSON without its line breaks and indents (they are never inside a string: the model writers escape them)
    public static string Compact(string json)
    {
        var sb = new StringBuilder(json.Length);
        int i = 0;
        while (i < json.Length)
        {
            char c = json[i];
            if (c == '\n') { i++; while (i < json.Length && json[i] == ' ') i++; continue; }
            sb.Append(c); i++;
        }
        return sb.ToString();
    }

    // SHA-256 of text (UTF-8), lowercase hex
    public static string Sha(string text)
    {
        using (var h = SHA256.Create())
            return string.Concat(h.ComputeHash(new UTF8Encoding(false).GetBytes(text)).Select(b => b.ToString("x2")).ToArray());
    }

    // a value read back equals the base's (to 1e-9 relative): the field keeps its exact value
    public static bool Same(double a, double b)
    {
        if (a == b) return true;
        if (double.IsNaN(a) || double.IsNaN(b)) return false;
        return Math.Abs(a - b) <= 1e-9 * Math.Max(Math.Abs(a), Math.Abs(b)) + 1e-15;
    }
}

// errors and warnings of one read
public class WbReadCtx
{
    public readonly List<string> Errors = new List<string>();
    public readonly List<string> Warnings = new List<string>();
    public void Err(string s) { if (Errors.Count < 200) Errors.Add(s); }
}

// a sheet's columns by header name (the text before " ("), with the header's unit
public sealed class WbCols
{
    public CaesarSheet S;
    public readonly Dictionary<string, int> At = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<int, string> Unit = new Dictionary<int, string>();
    public int Rows { get { return S.Rows.Count; } }
}

public static class WbRead
{
    // known: the columns the workbook reads (another one is warned about and ignored); null = no check
    public static WbCols ColsOf(CaesarSheet s, WbReadCtx x, string[] known)
    {
        var c = new WbCols { S = s };
        var head = s.Rows.Count > 0 ? s.Rows[0] : null;
        if (head == null) return c;
        for (int k = 0; k < head.Length; k++)
        {
            string h = (head[k] ?? "").Trim();
            if (h.Length == 0) continue;
            string name = h, unit = null;
            int p = h.IndexOf(" (", StringComparison.Ordinal);
            if (p > 0 && h.EndsWith(")", StringComparison.Ordinal)) { name = h.Substring(0, p).Trim(); unit = h.Substring(p + 2, h.Length - p - 3).Trim(); }
            if (c.At.ContainsKey(name)) { x.Err(s.Name + "!" + XlsxBook.Ref(0, k) + ": a second column \"" + name + "\""); continue; }
            c.At[name] = k;
            if (unit != null) c.Unit[k] = unit;
            if (known != null && !known.Contains(name, StringComparer.OrdinalIgnoreCase)) x.Warnings.Add(s.Name + ": column \"" + h + "\" is not one the workbook reads (ignored)");
        }
        return c;
    }

    // a cell's text; "" for a missing column, and for a cell with a problem (reported)
    public static string Text(WbCols c, int row, string col, WbReadCtx x)
    {
        int k;
        if (!c.At.TryGetValue(col, out k)) return "";
        string addr = XlsxBook.Ref(row, k);
        string prob;
        if (c.S.Problems.TryGetValue(addr, out prob)) { x.Err(c.S.Name + "!" + addr + ": " + prob); return ""; }
        return c.S.Text(row, k);
    }

    // a number cell as written (NaN when blank or missing; text that is not a number is an error); unit: the header's
    public static double Raw(WbCols c, int row, string col, WbReadCtx x, out string unit)
    {
        unit = null;
        int k;
        if (!c.At.TryGetValue(col, out k)) return double.NaN;
        string t = Text(c, row, col, x);
        if (t.Length == 0) return double.NaN;
        double v;
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) { x.Err(c.S.Name + "!" + XlsxBook.Ref(row, k) + ": \"" + t + "\" is not a number"); return double.NaN; }
        c.Unit.TryGetValue(k, out unit);
        return v;
    }

    // a number from a list, as a list entry ("106 - A106 Grade B") or the bare number; blank = dflt
    public static int LeadInt(WbCols c, int row, string col, WbReadCtx x, int dflt)
    {
        if (!c.At.ContainsKey(col)) return dflt;
        string tx = Text(c, row, col, x).Trim();
        if (tx.Length == 0) return dflt;
        int sp = tx.IndexOf(' ');
        string head = sp > 0 ? tx.Substring(0, sp) : tx;
        double v;
        if (double.TryParse(head, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && Math.Abs(v - Math.Round(v)) < 1e-9) return (int)Math.Round(v);
        x.Err(c.S.Name + "!" + XlsxBook.Ref(row, c.At[col]) + ": " + col + " \"" + tx + "\" (pick one from the list, or give its number)");
        return dflt;
    }

    // a whole number; blank = dflt
    public static int Int(WbCols c, int row, string col, WbReadCtx x, int dflt)
    {
        string unit;
        double v = Raw(c, row, col, x, out unit);
        if (double.IsNaN(v)) return dflt;
        if (Math.Abs(v - Math.Round(v)) > 1e-9) { x.Err(c.S.Name + "!" + XlsxBook.Ref(row, c.At[col]) + ": a whole number is expected (" + v.ToString("R", CultureInfo.InvariantCulture) + ")"); return dflt; }
        return (int)Math.Round(v);
    }

    public static bool Blank(CaesarSheet s, int row)
    {
        var r = row < s.Rows.Count ? s.Rows[row] : null;
        return r == null || r.All(v => v == null || v.Trim().Length == 0);
    }

    // "E12" with prefix 'E' -> 12; 0 when it is not such an Id
    public static int IdNum(string id, char prefix)
    {
        int n;
        if (id.Length > 1 && char.ToUpperInvariant(id[0]) == prefix && int.TryParse(id.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n > 0) return n;
        return 0;
    }

    // "PL12" with prefix "PL" -> 12; 0 when it is not such an Id
    public static int IdNum(string id, string prefix)
    {
        int n;
        if (id.Length > prefix.Length && id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(id.Substring(prefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n > 0) return n;
        return 0;
    }

    public static double ToNum(string s) { double v; return double.TryParse((s ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0; }
}

public static class WbRefresh
{
    // b: this version's book for the model the workbook holds (already read from path); inputs: the input sheets whose
    // rows and columns carry over; settings: the sheet of name / value rows (null = none), editable: the names whose
    // values are kept, key: a name's key (a unit in the name aside); notes: what the caller should say
    public static byte[] Bytes(XlsxBook b, string path, IList<string> inputs, string settings, ICollection<string> editable, Func<string, string> key, List<string> notes)
    {
        notes = notes ?? new List<string>();
        var old = CaesarWorkbook.Read(path);
        Func<string, XlsxBookSheet> mine = n => b.Sheets.FirstOrDefault(q => string.Equals(q.Name, n, StringComparison.OrdinalIgnoreCase));

        // columns: old -> new, per input sheet
        var maps = new Dictionary<string, Dictionary<int, int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var os in old)
        {
            var ns = mine(os.Name);
            if (ns == null || !inputs.Contains(ns.Name, StringComparer.OrdinalIgnoreCase)) continue;
            var map = new Dictionary<int, int>();
            int newLast = ns.LastCol(0), extra = newLast + 1, oldLast = os.Rows.Count > 0 && os.Rows[0] != null ? os.Rows[0].Length - 1 : -1;
            var used = new HashSet<int>();
            Func<string, string> stem = h => { int k = h.IndexOf(" ("); return (k > 0 ? h.Substring(0, k) : h).Trim().ToLowerInvariant(); };
            for (int c = 0; c <= oldLast; c++)
            {
                string h = os.Text(0, c);
                if (h.Length == 0) continue;
                int to = -1;
                for (int k = 0; k <= newLast && to < 0; k++) if (!used.Contains(k) && ns.TextAt(0, k) == h) to = k;
                for (int k = 0; k <= newLast && to < 0; k++) if (!used.Contains(k) && stem(ns.TextAt(0, k) ?? "") == stem(h)) to = k;
                if (to < 0) { to = extra++; ns.Set(0, to, h, ns.StyleAt(0, newLast)); ns.Width(to, Math.Max(8, Math.Min(30, h.Length + 2))); notes.Add(os.Name + ": the column \"" + h + "\" is not Pluto's: kept, after Pluto's columns"); }
                used.Add(to);
                map[c] = to;
            }
            maps[os.Name] = map;
        }
        Func<string, Func<string, int, bool, int>> colMap = here => (sh, c, abs) =>
        {
            Dictionary<int, int> mp; int to;
            return maps.TryGetValue(sh ?? here, out mp) && mp.TryGetValue(c, out to) ? to : c;
        };

        foreach (var os in old)
        {
            var ns = mine(os.Name);
            if (ns == null)
            {
                if (!os.Name.StartsWith("_")) { ns = b.Add(os.Name); Copy(os, ns, null, colMap(os.Name), 0); notes.Add(os.Name + ": a sheet of the workbook's own, copied as values and formulas (its formatting is in the .bak)"); }
                continue;
            }
            if (settings != null && string.Equals(os.Name, settings, StringComparison.OrdinalIgnoreCase)) { KeepSettings(os, ns, colMap(os.Name), editable, key); continue; }
            Dictionary<int, int> map;
            if (!maps.TryGetValue(os.Name, out map)) continue;   // Start, the views, the hidden sheets: this version's
            // the rows the model's rows came from: this version's columns' cells per old row; this version's new columns
            // (if any) move from the rows they were written on to those
            var newOnly = Enumerable.Range(0, ns.LastCol(0) + 1).Where(k => !map.ContainsValue(k)).ToList();
            var oldRows = Enumerable.Range(1, Math.Max(0, os.Rows.Count - 1)).Where(r => map.Keys.Any(c => map[c] <= ns.LastCol(0) && Has(os, r, c))).ToList();
            var newRows = ns.RowsUsed().Where(r => r > 0 && map.Values.Any(k => ns.HasContent(r, k))).OrderBy(r => r).ToList();
            if (newOnly.Count > 0 && newOnly.Any(k => newRows.Any(r => ns.HasContent(r, k))) && !oldRows.SequenceEqual(newRows))
            {
                if (oldRows.Count == newRows.Count) Move(ns, newOnly, newRows, oldRows);
                else
                {
                    foreach (int k in newOnly) foreach (int r in newRows) ns.ClearContent(r, k);
                    notes.Add(os.Name + ": the new column(s) " + string.Join(", ", newOnly.Select(k => "\"" + ns.TextAt(0, k) + "\"").ToArray()) + " left blank: the rows could not be matched to the model's");
                }
            }
            Copy(os, ns, map, colMap(os.Name), 1);
        }
        return b.ToBytes();
    }

    static bool Has(CaesarSheet s, int r, int c)
    {
        string a = XlsxBook.Ref(r, c);
        return s.AllFormulas.ContainsKey(a) || s.Problems.ContainsKey(a) || !string.IsNullOrEmpty(s.Cell(r, c));
    }

    // this version's cells in columns cols moved from rows `from` to rows `to` (pairwise), styles staying with their rows
    static void Move(XlsxBookSheet ns, List<int> cols, List<int> from, List<int> to)
    {
        var keep = new List<object[]>();
        foreach (int k in cols)
            for (int i = 0; i < from.Count; i++)
            {
                if (!ns.HasContent(from[i], k)) continue;
                string tx = ns.TextAt(from[i], k);
                keep.Add(new object[] { to[i], k, tx });
            }
        foreach (int k in cols) foreach (int r in from) ns.ClearContent(r, k);
        foreach (var x in keep)
        {
            int r = (int)x[0], k = (int)x[1]; string tx = (string)x[2];
            if (tx != null) ns.Set(r, k, tx, ns.StyleAt(r, k));
        }
    }

    // the old sheet's cells from row `first` on, column c going to map[c] (map null: the same column), as typed: a formula
    // with its references rewritten and the value it last gave, else the value (text stays text); the new cell's style kept
    static void Copy(CaesarSheet os, XlsxBookSheet ns, Dictionary<int, int> map, Func<string, int, bool, int> colMap, int first)
    {
        int lastRow = Math.Max(os.Rows.Count - 1, ns.RowsUsed().DefaultIfEmpty(0).Max());
        var cols = map != null ? map.Keys.ToList() : Enumerable.Range(0, os.Rows.Where(q => q != null).Select(q => q.Length).DefaultIfEmpty(0).Max()).ToList();
        foreach (int c in cols)
        {
            int k = map != null ? map[c] : c;
            for (int r = first; r <= lastRow; r++)
            {
                ns.ClearContent(r, k);
                Put(os, r, c, ns, r, k, colMap);
            }
        }
    }

    static void Put(CaesarSheet os, int r, int c, XlsxBookSheet ns, int nr, int k, Func<string, int, bool, int> colMap)
    {
        string a = XlsxBook.Ref(r, c), f, v = os.Cell(r, c);
        int st = ns.StyleAt(nr, k);
        bool text = os.TextCells.Contains(a);
        double d;
        bool num = !text && v != null && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d);
        if (os.AllFormulas.TryGetValue(a, out f))
        {
            f = XlsxFormulaRefs.Rewrite(f, colMap, (row, abs) => row);
            if (num) ns.SetFormula(nr, k, f, double.Parse(v, NumberStyles.Float, CultureInfo.InvariantCulture), st);
            else if (v != null) ns.SetFormulaText(nr, k, f, v, st);
            else ns.SetFormula(nr, k, f, st);
            return;
        }
        if (v == null) return;
        if (num) ns.Set(nr, k, double.Parse(v, NumberStyles.Float, CultureInfo.InvariantCulture), st);
        else ns.Set(nr, k, v, st);
    }

    // settings: the values typed for the settings this version still has, by name (key: a name's key)
    static void KeepSettings(CaesarSheet os, XlsxBookSheet ns, Func<string, int, bool, int> colMap, ICollection<string> editable, Func<string, string> key)
    {
        var at = new Dictionary<string, int>();
        foreach (int r in ns.RowsUsed()) { string k = ns.TextAt(r, 0); if (r > 0 && !string.IsNullOrEmpty(k) && !at.ContainsKey(key(k))) at[key(k)] = r; }
        for (int r = 1; r < os.Rows.Count; r++)
        {
            int nr; string k = key(os.Text(r, 0));
            if (!editable.Contains(k) || !at.TryGetValue(k, out nr)) continue;
            ns.ClearContent(nr, 1);
            Put(os, r, 1, ns, nr, 1, colMap);
        }
    }
}
