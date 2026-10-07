using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

// TomlReader  --  the subset of TOML (v1.0) the Pluto configs use, for C# 5 / PowerShell 5.1 (no package).
// Reads: comments; bare, quoted and dotted keys; [tables], [[arrays of tables]] (dotted names too);
// basic "strings" (escapes \" \\ \/ \b \f \n \r \t \uXXXX) and literal 'strings'; integers and floats
// (underscores, exponents, a leading sign); true / false; arrays (multi-line, nested, trailing comma,
// comments between values); inline tables { k = v, ... }. Not read (an error with the line number):
// multi-line strings, dates and times, hex / octal / binary integers, inf / nan.
//
// Result: tables are Dictionary<string, object> (key order kept: a TomlTable), arrays List<object>,
// numbers double (integers too; TomlReader.IsInteger tells them apart in the source), strings string,
// booleans bool. A key defined twice, or a table reopened, is an error.
//
//   var root = TomlReader.Parse(File.ReadAllText(path), path);
//   var model = TomlReader.Table(root, "model");          // null when absent
//   double r = TomlReader.Number(model, "radius", 30.0);  // default when absent

public class TomlTable : Dictionary<string, object>
{
    public readonly List<string> Order = new List<string>();   // keys in the order they were defined
    public bool Explicit;      // opened by a [header] (a second [header] for it is an error)
    public bool Inline;        // an inline table (closed for good)
    public int Line;           // line where it was opened
    public TomlTable() : base(StringComparer.Ordinal) { }
    public void Put(string key, object value)
    {
        if (!ContainsKey(key)) Order.Add(key);
        this[key] = value;
    }
}

public class TomlException : Exception
{
    public readonly int LineNumber;
    public TomlException(string source, int line, string message)
        : base((string.IsNullOrEmpty(source) ? "" : source + ":") + line.ToString(CultureInfo.InvariantCulture) + ": " + message)
    { LineNumber = line; }
}

public class TomlReader
{
    readonly string s;
    readonly string src;
    int p;
    int line = 1;

    TomlReader(string text, string source) { s = text ?? ""; src = source; }

    public static TomlTable Parse(string text, string source)
    {
        var r = new TomlReader(text, source);
        return r.Document();
    }

    // ---------------- typed access (null / default when absent; an error when the type is wrong)

    public static TomlTable Table(TomlTable t, string key)
    {
        object v;
        if (t == null || !t.TryGetValue(key, out v)) return null;
        var tt = v as TomlTable;
        if (tt == null) throw new ArgumentException("'" + key + "' is not a table");
        return tt;
    }

    public static List<object> Array(TomlTable t, string key)
    {
        object v;
        if (t == null || !t.TryGetValue(key, out v)) return null;
        var a = v as List<object>;
        if (a == null) throw new ArgumentException("'" + key + "' is not an array");
        return a;
    }

    public static string String(TomlTable t, string key, string dflt)
    {
        object v;
        if (t == null || !t.TryGetValue(key, out v)) return dflt;
        var str = v as string;
        if (str == null) throw new ArgumentException("'" + key + "' is not a string");
        return str;
    }

    public static double Number(TomlTable t, string key, double dflt)
    {
        object v;
        if (t == null || !t.TryGetValue(key, out v)) return dflt;
        if (v is double) return (double)v;
        throw new ArgumentException("'" + key + "' is not a number");
    }

    public static bool Bool(TomlTable t, string key, bool dflt)
    {
        object v;
        if (t == null || !t.TryGetValue(key, out v)) return dflt;
        if (v is bool) return (bool)v;
        throw new ArgumentException("'" + key + "' is not true / false");
    }

    // ---------------- document

    TomlTable Document()
    {
        var root = new TomlTable();
        root.Line = 1;
        TomlTable current = root;
        while (true)
        {
            SkipWsCommentsNewlines();
            if (p >= s.Length) break;
            char c = s[p];
            if (c == '[')
            {
                bool array = p + 1 < s.Length && s[p + 1] == '[';
                int at = line;
                p += array ? 2 : 1;
                SkipWs();
                var path = KeyPath();
                SkipWs();
                Expect(']');
                if (array) Expect(']');
                EndOfLine();
                current = array ? OpenArrayTable(root, path, at) : OpenTable(root, path, at);
                continue;
            }
            int kvLine = line;
            var keys = KeyPath();
            SkipWs();
            Expect('=');
            SkipWs();
            object value = Value();
            Assign(current, keys, value, kvLine);
            EndOfLine();
        }
        return root;
    }

    TomlTable OpenTable(TomlTable root, List<string> path, int at)
    {
        TomlTable t = root;
        for (int i = 0; i < path.Count; i++)
        {
            string k = path[i];
            object v;
            bool last = i == path.Count - 1;
            if (!t.TryGetValue(k, out v))
            {
                var nt = new TomlTable();
                nt.Line = at;
                t.Put(k, nt);
                t = nt;
            }
            else if (v is TomlTable)
            {
                t = (TomlTable)v;
                if (t.Inline) throw Err(at, "table '" + string.Join(".", path.ToArray()) + "' was defined inline and cannot be extended");
            }
            else if (v is List<object> && !last)
            {
                var a = (List<object>)v;
                if (a.Count == 0 || !(a[a.Count - 1] is TomlTable)) throw Err(at, "'" + k + "' is an array of values, not of tables");
                t = (TomlTable)a[a.Count - 1];
            }
            else throw Err(at, "'" + string.Join(".", path.ToArray()) + "': '" + k + "' already holds a value");
        }
        if (t.Explicit) throw Err(at, "table [" + string.Join(".", path.ToArray()) + "] defined twice (first on line " + t.Line.ToString(CultureInfo.InvariantCulture) + ")");
        t.Explicit = true;
        return t;
    }

    TomlTable OpenArrayTable(TomlTable root, List<string> path, int at)
    {
        TomlTable t = root;
        for (int i = 0; i < path.Count - 1; i++)
        {
            string k = path[i];
            object v;
            if (!t.TryGetValue(k, out v)) { var nt = new TomlTable(); nt.Line = at; t.Put(k, nt); t = nt; }
            else if (v is TomlTable) t = (TomlTable)v;
            else if (v is List<object>)
            {
                var a = (List<object>)v;
                if (a.Count == 0 || !(a[a.Count - 1] is TomlTable)) throw Err(at, "'" + k + "' is an array of values, not of tables");
                t = (TomlTable)a[a.Count - 1];
            }
            else throw Err(at, "'" + k + "' already holds a value");
        }
        string lastKey = path[path.Count - 1];
        object cur;
        List<object> arr;
        if (!t.TryGetValue(lastKey, out cur)) { arr = new List<object>(); t.Put(lastKey, arr); }
        else
        {
            arr = cur as List<object>;
            if (arr == null || (arr.Count > 0 && !(arr[0] is TomlTable)) || IsStaticArray(arr))
                throw Err(at, "[[" + string.Join(".", path.ToArray()) + "]]: '" + lastKey + "' is already defined as something else");
        }
        var item = new TomlTable();
        item.Line = at;
        item.Explicit = true;
        arr.Add(item);
        return item;
    }

    // arrays written as values ([1, 2] or [{...}]) are closed; only [[header]] arrays grow
    readonly HashSet<List<object>> staticArrays = new HashSet<List<object>>();
    bool IsStaticArray(List<object> a) { return staticArrays.Contains(a); }

    void Assign(TomlTable t, List<string> keys, object value, int at)
    {
        for (int i = 0; i < keys.Count - 1; i++)
        {
            object v;
            if (!t.TryGetValue(keys[i], out v)) { var nt = new TomlTable(); nt.Line = at; t.Put(keys[i], nt); t = nt; }
            else if (v is TomlTable && !((TomlTable)v).Inline) t = (TomlTable)v;
            else throw Err(at, "'" + string.Join(".", keys.ToArray()) + "': '" + keys[i] + "' already holds a value");
        }
        string last = keys[keys.Count - 1];
        if (t.ContainsKey(last)) throw Err(at, "key '" + string.Join(".", keys.ToArray()) + "' defined twice");
        t.Put(last, value);
    }

    // ---------------- keys

    List<string> KeyPath()
    {
        var path = new List<string>();
        while (true)
        {
            SkipWs();
            path.Add(Key());
            SkipWs();
            if (p < s.Length && s[p] == '.') { p++; continue; }
            break;
        }
        return path;
    }

    string Key()
    {
        if (p >= s.Length) throw Err(line, "a key was expected");
        char c = s[p];
        if (c == '"') return BasicString();
        if (c == '\'') return LiteralString();
        int start = p;
        while (p < s.Length && (char.IsLetterOrDigit(s[p]) || s[p] == '_' || s[p] == '-')) p++;
        if (p == start) throw Err(line, "a key was expected, found '" + Show(c) + "'");
        return s.Substring(start, p - start);
    }

    // ---------------- values

    object Value()
    {
        if (p >= s.Length) throw Err(line, "a value was expected");
        char c = s[p];
        if (c == '"')
        {
            if (p + 2 < s.Length && s[p + 1] == '"' && s[p + 2] == '"') throw Err(line, "multi-line strings are not supported");
            return BasicString();
        }
        if (c == '\'')
        {
            if (p + 2 < s.Length && s[p + 1] == '\'' && s[p + 2] == '\'') throw Err(line, "multi-line strings are not supported");
            return LiteralString();
        }
        if (c == '[') return ArrayValue();
        if (c == '{') return InlineTable();
        if (Match("true")) return true;
        if (Match("false")) return false;
        return NumberValue();
    }

    bool Match(string word)
    {
        if (string.CompareOrdinal(s, p, word, 0, word.Length) != 0) return false;
        int e = p + word.Length;
        if (e < s.Length && (char.IsLetterOrDigit(s[e]) || s[e] == '_')) return false;
        p = e;
        return true;
    }

    object NumberValue()
    {
        int start = p, at = line;
        while (p < s.Length && "+-0123456789._eE".IndexOf(s[p]) >= 0) p++;
        string tok = s.Substring(start, p - start);
        if (tok.Length == 0)
        {
            int e = start;
            while (e < s.Length && !char.IsWhiteSpace(s[e]) && s[e] != ',' && s[e] != ']' && s[e] != '}' && s[e] != '#') e++;
            throw Err(at, "not a value: '" + s.Substring(start, Math.Min(e - start, 40)) + "' (strings need quotes)");
        }
        if (p < s.Length && (char.IsLetter(s[p]) || s[p] == ':')) throw Err(at, "not a number: '" + tok + s[p] + "...' (dates, hex and inf / nan are not supported; strings need quotes)");
        if (tok.IndexOf("__", StringComparison.Ordinal) >= 0 || tok.StartsWith("_") || tok.EndsWith("_")) throw Err(at, "misplaced underscore in '" + tok + "'");
        string clean = tok.Replace("_", "");
        double v;
        if (!double.TryParse(clean, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
                             CultureInfo.InvariantCulture, out v) || double.IsNaN(v) || double.IsInfinity(v))
            throw Err(at, "not a number: '" + tok + "'");
        return v;
    }

    List<object> ArrayValue()
    {
        Expect('[');
        var list = new List<object>();
        staticArrays.Add(list);
        while (true)
        {
            SkipWsCommentsNewlines();
            if (p >= s.Length) throw Err(line, "unterminated array");
            if (s[p] == ']') { p++; break; }
            list.Add(Value());
            SkipWsCommentsNewlines();
            if (p < s.Length && s[p] == ',') { p++; continue; }
            SkipWsCommentsNewlines();
            if (p < s.Length && s[p] == ']') { p++; break; }
            throw Err(line, "',' or ']' expected in an array");
        }
        return list;
    }

    TomlTable InlineTable()
    {
        int at = line;
        Expect('{');
        var t = new TomlTable();
        t.Line = at;
        SkipWs();
        if (p < s.Length && s[p] == '}') { p++; t.Inline = true; return t; }
        while (true)
        {
            SkipWs();
            var keys = KeyPath();
            SkipWs();
            Expect('=');
            SkipWs();
            object v = Value();
            Assign(t, keys, v, line);
            SkipWs();
            if (p < s.Length && s[p] == ',') { p++; continue; }
            if (p < s.Length && s[p] == '}') { p++; break; }
            throw Err(line, "',' or '}' expected in an inline table (inline tables stay on one line)");
        }
        t.Inline = true;
        return t;
    }

    string BasicString()
    {
        Expect('"');
        var sb = new StringBuilder();
        while (true)
        {
            if (p >= s.Length || s[p] == '\n') throw Err(line, "unterminated string");
            char c = s[p++];
            if (c == '"') break;
            if (c != '\\') { sb.Append(c); continue; }
            if (p >= s.Length) throw Err(line, "unterminated string");
            char e = s[p++];
            switch (e)
            {
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case '/': sb.Append('/'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'u':
                case 'U':
                    {
                        int n = e == 'u' ? 4 : 8;
                        if (p + n > s.Length) throw Err(line, "bad \\" + e + " escape");
                        int code;
                        if (!int.TryParse(s.Substring(p, n), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code)) throw Err(line, "bad \\" + e + " escape");
                        sb.Append(char.ConvertFromUtf32(code));
                        p += n;
                        break;
                    }
                default: throw Err(line, "unknown escape \\" + e + " (use '...' for a literal string such as a Windows path)");
            }
        }
        return sb.ToString();
    }

    string LiteralString()
    {
        Expect('\'');
        int start = p;
        while (p < s.Length && s[p] != '\'' && s[p] != '\n') p++;
        if (p >= s.Length || s[p] != '\'') throw Err(line, "unterminated literal string");
        string v = s.Substring(start, p - start);
        p++;
        return v;
    }

    // ---------------- whitespace / lines

    void SkipWs() { while (p < s.Length && (s[p] == ' ' || s[p] == '\t')) p++; }

    void SkipComment()
    {
        if (p < s.Length && s[p] == '#') while (p < s.Length && s[p] != '\n') p++;
    }

    void SkipWsCommentsNewlines()
    {
        while (p < s.Length)
        {
            char c = s[p];
            if (c == ' ' || c == '\t' || c == '\r' || c == '\uFEFF') { p++; continue; }
            if (c == '\n') { p++; line++; continue; }
            if (c == '#') { SkipComment(); continue; }
            break;
        }
    }

    void EndOfLine()
    {
        SkipWs();
        SkipComment();
        if (p < s.Length && s[p] == '\r') p++;
        if (p >= s.Length) return;
        if (s[p] != '\n') throw Err(line, "end of line expected after the value, found '" + Show(s[p]) + "'");
        p++;
        line++;
    }

    void Expect(char c)
    {
        if (p >= s.Length || s[p] != c) throw Err(line, "'" + c + "' expected" + (p < s.Length ? ", found '" + Show(s[p]) + "'" : " at the end of the file"));
        p++;
    }

    static string Show(char c) { return c == '\n' ? "end of line" : c.ToString(); }

    TomlException Err(int at, string msg) { return new TomlException(src, at, msg); }
}
