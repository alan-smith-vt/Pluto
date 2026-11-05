using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

// S2kDoc  --  a SAP2000 .s2k / .$2k text file held losslessly (2026-11-05). Sap2kParser keeps rows as dictionaries,
// which is right for reading (and for the multi-GB results files) but loses the text: field order, quoting, the
// physical lines a row was continued over ("_"), banners, comments and line endings. S2kDoc keeps all of it, so a
// file read and written again is byte for byte the source (Load(bytes).ToBytes() == bytes: checked on every import),
// and an edit rewrites only the rows it changes, each in SAP's own style, its fields in their order.
//   Preamble   the lines before the first TABLE: line ("File ... was saved on ...")
//   Tables     each TABLE:  "NAME" line, then its items: rows, and the lines between them kept as written (blank,
//              banner ";;;", comment, anything with no Key=Value)
//   Tail       END TABLE DATA and whatever follows it
// Text is read as ISO-8859-1 (every byte one char and back): SAP writes ANSI. IS2kTables gives the same row view
// as Sap2kParser, so SapToPluto and Sap2kReader read either.

public interface IS2kTables
{
    bool HasTable(string name);
    List<Dictionary<string, string>> GetTable(string name);
    IEnumerable<string> TableNames();
}

// one physical line and the terminator that followed it ("" for the file's last line without one)
public sealed class S2kLine
{
    public string Text = "";
    public string Eol = "";
    public S2kLine() { }
    public S2kLine(string text, string eol) { Text = text ?? ""; Eol = eol ?? ""; }
    public S2kLine Clone() { return new S2kLine(Text, Eol); }
}

// one row: Key=Value fields in order. Lines are the physical lines it was read from (continuations included); a new
// row has none. A dirty row is written from its fields, a clean one as its lines.
public sealed class S2kRow
{
    public readonly List<S2kLine> Lines = new List<S2kLine>();
    public readonly List<string> Keys = new List<string>();
    public readonly List<string> Values = new List<string>();
    public readonly List<bool> Quoted = new List<bool>();
    public readonly List<bool> Bare = new List<bool>();      // read as an empty value with no quotes (GUID=): rendered so while unchanged
    public bool Dirty;
    public string Eol = "\r\n";

    public int IndexOf(string key)
    {
        for (int i = 0; i < Keys.Count; i++) if (string.Equals(Keys[i], key, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }
    public bool Has(string key) { return IndexOf(key) >= 0; }
    public string Get(string key) { int i = IndexOf(key); return i < 0 ? null : Values[i]; }

    // set a field (appended when absent); the row is dirty only when the text changes
    public void Set(string key, string value)
    {
        value = value ?? "";
        int i = IndexOf(key);
        if (i < 0) { Keys.Add(key); Values.Add(value); Quoted.Add(false); Bare.Add(false); Dirty = true; return; }
        if (Values[i] == value) return;
        Values[i] = value;
        Bare[i] = false;
        Dirty = true;
    }
    // a field set; when absent it goes right after `after` (at the end when that is absent too), the order SAP writes
    public void SetAfter(string after, string key, string value)
    {
        if (Has(key) || after == null || !Has(after)) { Set(key, value); return; }
        int i = IndexOf(after) + 1;
        Keys.Insert(i, key); Values.Insert(i, value ?? ""); Quoted.Insert(i, false); Bare.Insert(i, false);
        Dirty = true;
    }
    public void Remove(string key)
    {
        int i = IndexOf(key);
        if (i < 0) return;
        Keys.RemoveAt(i); Values.RemoveAt(i); Quoted.RemoveAt(i); Bare.RemoveAt(i);
        Dirty = true;
    }

    public Dictionary<string, string> ToDictionary()
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < Keys.Count; i++) d[Keys[i]] = Values[i];
        return d;
    }

    public S2kRow Clone()
    {
        var r = new S2kRow { Dirty = Dirty, Eol = Eol };
        foreach (var l in Lines) r.Lines.Add(l.Clone());
        r.Keys.AddRange(Keys); r.Values.AddRange(Values); r.Quoted.AddRange(Quoted); r.Bare.AddRange(Bare);
        return r;
    }

    // SAP's style: three spaces before every field, values with a space, a comma or nothing quoted
    public string Render()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < Keys.Count; i++)
        {
            string v = Values[i] ?? "";
            bool q = Quoted[i] || (v.Length == 0 && !Bare[i]) || v.IndexOf(' ') >= 0 || v.IndexOf(',') >= 0;
            sb.Append("   ").Append(Keys[i]).Append('=');
            if (q) sb.Append('"').Append(v).Append('"'); else sb.Append(v);
        }
        return sb.ToString();
    }

    internal void WriteTo(StringBuilder sb)
    {
        if (!Dirty && Lines.Count > 0) { foreach (var l in Lines) sb.Append(l.Text).Append(l.Eol); return; }
        sb.Append(Render()).Append(Lines.Count > 0 ? (Lines[Lines.Count - 1].Eol.Length > 0 ? Lines[Lines.Count - 1].Eol : Eol) : Eol);
    }

    // the fields of a logical row (its continuations joined): Key=Value tokens, values quoted or not
    internal static void Tokenize(string text, S2kRow row)
    {
        int i = 0, n = text.Length;
        while (i < n)
        {
            while (i < n && char.IsWhiteSpace(text[i])) i++;
            if (i >= n) break;
            int ks = i;
            while (i < n && text[i] != '=' && !char.IsWhiteSpace(text[i])) i++;
            if (i >= n || text[i] != '=') { while (i < n && !char.IsWhiteSpace(text[i])) i++; continue; }
            string key = text.Substring(ks, i - ks);
            i++;
            string value; bool quoted = false;
            if (i < n && text[i] == '"')
            {
                quoted = true;
                i++;
                int vs = i;
                while (i < n && text[i] != '"') i++;
                value = text.Substring(vs, i - vs);
                if (i < n) i++;
            }
            else
            {
                int vs = i;
                while (i < n && !char.IsWhiteSpace(text[i])) i++;
                value = text.Substring(vs, i - vs);
            }
            row.Keys.Add(key); row.Values.Add(value); row.Quoted.Add(quoted); row.Bare.Add(!quoted && value.Length == 0);
        }
    }
}

public sealed class S2kTable
{
    public string Name = "";
    public S2kLine Header;                                  // the TABLE: line as written
    public readonly List<object> Items = new List<object>(); // S2kRow, or S2kLine (a line kept as written)

    public IEnumerable<S2kRow> Rows { get { foreach (var o in Items) { var r = o as S2kRow; if (r != null) yield return r; } } }

    // a row after the table's last row (before the blank lines that close it)
    public void AddRow(S2kRow row)
    {
        int at = -1;
        for (int i = Items.Count - 1; i >= 0; i--) if (Items[i] is S2kRow) { at = i; break; }
        Items.Insert(at + 1, row);
    }
    public void InsertRowAfter(S2kRow after, S2kRow row)
    {
        int i = Items.IndexOf(after);
        if (i < 0) AddRow(row); else Items.Insert(i + 1, row);
    }
    public bool RemoveRow(S2kRow row) { return Items.Remove(row); }
}

public sealed class S2kDoc : IS2kTables
{
    public static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

    public readonly List<S2kLine> Preamble = new List<S2kLine>();
    public readonly List<S2kTable> Tables = new List<S2kTable>();
    public readonly List<S2kLine> Tail = new List<S2kLine>();
    public string Eol = "\r\n";                                   // the file's line ending, for new lines

    // ---------------- read

    public static S2kDoc Load(string path) { return Parse(File.ReadAllBytes(path)); }
    public static S2kDoc Parse(byte[] bytes) { return ParseText(Latin1.GetString(bytes)); }

    public static S2kDoc ParseText(string text)
    {
        var d = new S2kDoc();
        var lines = SplitLines(text ?? "");
        var firstEol = lines.FirstOrDefault(l => l.Eol.Length > 0);
        if (firstEol != null) d.Eol = firstEol.Eol;
        S2kTable t = null;
        bool ended = false;
        S2kRow pending = null;                                   // a row continued with "_"
        foreach (var l in lines)
        {
            if (ended) { d.Tail.Add(l); continue; }
            string s = l.Text.Trim();
            if (pending != null)
            {
                pending.Lines.Add(l);
                if (!Continues(s)) { Finish(pending); pending = null; }
                continue;
            }
            if (s.StartsWith("TABLE:", StringComparison.OrdinalIgnoreCase))
            {
                t = new S2kTable { Name = TableName(s), Header = l };
                d.Tables.Add(t);
                continue;
            }
            if (s.StartsWith("END TABLE DATA", StringComparison.OrdinalIgnoreCase)) { ended = true; d.Tail.Add(l); continue; }
            if (t == null) { d.Preamble.Add(l); continue; }
            if (s.Length == 0 || s.IndexOf('=') < 0 || IsBanner(s) || s[0] == '$') { t.Items.Add(l); continue; }
            var r = new S2kRow { Eol = l.Eol.Length > 0 ? l.Eol : d.Eol };
            r.Lines.Add(l);
            t.Items.Add(r);
            if (Continues(s)) pending = r; else Finish(r);
        }
        if (pending != null) Finish(pending);
        return d;
    }

    static bool IsBanner(string s) { foreach (char c in s) if (c != ';') return false; return true; }
    static bool Continues(string s) { return s.Length > 0 && s[s.Length - 1] == '_' && (s.Length == 1 || char.IsWhiteSpace(s[s.Length - 2])); }

    static void Finish(S2kRow r)
    {
        var sb = new StringBuilder();
        foreach (var l in r.Lines)
        {
            string s = l.Text.TrimEnd();
            if (Continues(s.Trim())) s = s.Substring(0, s.Length - 1);
            sb.Append(s).Append(' ');
        }
        S2kRow.Tokenize(sb.ToString(), r);
        r.Dirty = false;
    }

    static string TableName(string s)
    {
        int q1 = s.IndexOf('"');
        if (q1 < 0) return s.Substring(s.IndexOf(':') + 1).Trim();
        int q2 = s.IndexOf('"', q1 + 1);
        return (q2 < 0 ? s.Substring(q1 + 1) : s.Substring(q1 + 1, q2 - q1 - 1)).Trim();
    }

    static List<S2kLine> SplitLines(string text)
    {
        var o = new List<S2kLine>();
        int i = 0, start = 0, n = text.Length;
        while (i < n)
        {
            char c = text[i];
            if (c == '\r' || c == '\n')
            {
                string eol = c == '\r' && i + 1 < n && text[i + 1] == '\n' ? "\r\n" : c.ToString();
                o.Add(new S2kLine(text.Substring(start, i - start), eol));
                i += eol.Length;
                start = i;
                continue;
            }
            i++;
        }
        if (start < n) o.Add(new S2kLine(text.Substring(start), ""));
        return o;
    }

    // ---------------- write

    public string ToText()
    {
        var sb = new StringBuilder();
        foreach (var l in Preamble) sb.Append(l.Text).Append(l.Eol);
        foreach (var t in Tables)
        {
            sb.Append(t.Header.Text).Append(t.Header.Eol);
            foreach (var o in t.Items)
            {
                var r = o as S2kRow;
                if (r != null) r.WriteTo(sb);
                else { var l = (S2kLine)o; sb.Append(l.Text).Append(l.Eol); }
            }
        }
        foreach (var l in Tail) sb.Append(l.Text).Append(l.Eol);
        return sb.ToString();
    }

    public byte[] ToBytes() { return Latin1.GetBytes(ToText()); }

    // the same bytes (for the runners: PowerShell 5.1 does not call Enumerable.SequenceEqual on arrays reliably)
    public static bool SameBytes(byte[] a, byte[] b)
    {
        if (a == null || b == null || a.Length != b.Length) return a == b;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    // ---------------- edit

    public S2kTable Table(string name)
    {
        foreach (var t in Tables) if (string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) return t;
        return null;
    }

    // a new, empty table: before the first table that `order` puts after it, else last; a blank line closes it
    public S2kTable AddTable(string name, IList<string> order)
    {
        var t = new S2kTable { Name = name, Header = new S2kLine("TABLE:  \"" + name + "\"", Eol) };
        t.Items.Add(new S2kLine("", Eol));
        int at = Tables.Count;
        int mine = order == null ? -1 : IndexIn(order, name);
        if (mine >= 0)
            for (int i = 0; i < Tables.Count; i++)
            {
                int k = IndexIn(order, Tables[i].Name);
                if (k > mine) { at = i; break; }
            }
        Tables.Insert(at, t);
        if (Tail.Count == 0) { Tail.Add(new S2kLine("END TABLE DATA", Eol)); }
        return t;
    }

    static int IndexIn(IList<string> order, string name)
    {
        for (int i = 0; i < order.Count; i++) if (string.Equals(order[i], name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    // a deep copy; map: each row of this document -> its copy
    public S2kDoc Clone(Dictionary<S2kRow, S2kRow> map)
    {
        var d = new S2kDoc { Eol = Eol };
        foreach (var l in Preamble) d.Preamble.Add(l.Clone());
        foreach (var t in Tables)
        {
            var c = new S2kTable { Name = t.Name, Header = t.Header.Clone() };
            foreach (var o in t.Items)
            {
                var r = o as S2kRow;
                if (r != null) { var rc = r.Clone(); if (map != null) map[r] = rc; c.Items.Add(rc); }
                else c.Items.Add(((S2kLine)o).Clone());
            }
            d.Tables.Add(c);
        }
        foreach (var l in Tail) d.Tail.Add(l.Clone());
        return d;
    }

    // ---------------- IS2kTables

    public bool HasTable(string name) { return Table(name) != null; }

    public List<Dictionary<string, string>> GetTable(string name)
    {
        var o = new List<Dictionary<string, string>>();
        foreach (var t in Tables)
            if (string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
                foreach (var r in t.Rows) o.Add(r.ToDictionary());
        return o;
    }

    public IEnumerable<string> TableNames()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in Tables) if (seen.Add(t.Name)) yield return t.Name;
    }
}
