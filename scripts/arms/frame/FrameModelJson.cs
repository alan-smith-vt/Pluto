using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

// FrameModelJson  --  the whole frame model as JSON, exactly (2026-11-05): "pluto-frame-model" v1. Every value in
// the model is text, so the JSON is exact by construction: the source file as written (its text), and every object
// with its uid and its rows (table, fields). Reading it gives the model back; the source read again gives the base the
// writer overlays edits on (FrameS2k.ToDoc), so nothing else is stored. Non-ASCII characters are escaped (an e with an acute accent as \u00e9),
// the text stays ASCII. Hash: SHA-256 of the JSON, the model's fingerprint (the workbook's base, the build stamp).
// C# 5, one global batch (prefix Frame).

public static class FrameModelJson
{
    public const string Format = "pluto-frame-model";
    public const int Version = 1;

    public static string Write(FrameModel m)
    {
        var sb = new StringBuilder();
        sb.Append("{\"format\":").Append(Q(Format)).Append(",\"version\":").Append(Version);
        sb.Append(",\"nextUid\":").Append(m.NextUid.ToString(CultureInfo.InvariantCulture));
        sb.Append(",\n\"source\":").Append(Q(m.Source == null ? "" : m.Source.ToText()));
        sb.Append(",\n\"control\":"); Fields(sb, m.Control);
        sb.Append(",\n\"objects\":[");
        bool first = true;
        foreach (var o in m.All())
        {
            sb.Append(first ? "\n" : ",\n");
            first = false;
            sb.Append("{\"kind\":").Append(Q(o.Kind)).Append(",\"uid\":").Append(o.Uid.ToString(CultureInfo.InvariantCulture));
            var ld = o as FrameLoad;
            if (ld != null) sb.Append(",\"table\":").Append(Q(ld.Table));
            sb.Append(",\"recs\":[");
            for (int i = 0; i < o.Recs.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"t\":").Append(Q(o.Recs[i].Table)).Append(",\"f\":");
                Fields(sb, o.Recs[i]);
                sb.Append('}');
            }
            sb.Append("]}");
        }
        sb.Append("\n]}\n");
        return sb.ToString();
    }

    static void Fields(StringBuilder sb, FrameRec r)
    {
        sb.Append('[');
        if (r != null)
            for (int i = 0; i < r.F.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('[').Append(Q(r.F[i][0])).Append(',').Append(Q(r.F[i][1])).Append(']');
            }
        sb.Append(']');
    }

    public static FrameModel Read(string json)
    {
        var root = new Parser(json ?? "").Value() as Dictionary<string, object>;
        if (root == null) throw new FormatException("FrameModelJson: not a JSON object");
        if (Str(root, "format") != Format) throw new FormatException("FrameModelJson: format \"" + Str(root, "format") + "\" (\"" + Format + "\" expected)");
        if (Num(root, "version") > Version) throw new FormatException("FrameModelJson: version " + Num(root, "version") + " is newer than this reader (" + Version + ")");
        var m = new FrameModel { Source = S2kDoc.ParseText(Str(root, "source") ?? "") };
        m.NextUid = (int)Num(root, "nextUid");
        m.Control = Rec("PROGRAM CONTROL", Get(root, "control") as List<object>);
        var objs = Get(root, "objects") as List<object> ?? new List<object>();
        foreach (var x in objs)
        {
            var o = x as Dictionary<string, object>;
            string kind = Str(o, "kind");
            FrameObj obj;
            if (kind == "load") obj = new FrameLoad { Table = Str(o, "table") ?? "" };
            else
            {
                switch (kind)
                {
                    case "joint": obj = new FrameJoint(); break;
                    case "frame": obj = new FrameMember(); break;
                    case "section": obj = new FrameSection(); break;
                    case "material": obj = new FrameMaterial(); break;
                    case "linkprop": obj = new FrameLinkProp(); break;
                    case "link": obj = new FrameLink(); break;
                    case "group": obj = new FrameGroup(); break;
                    case "pattern": obj = new FramePattern(); break;
                    case "case": obj = new FrameCase(); break;
                    case "combo": obj = new FrameCombo(); break;
                    default: throw new FormatException("FrameModelJson: unknown object kind \"" + kind + "\"");
                }
            }
            obj.Uid = (int)Num(o, "uid");
            foreach (var rx in Get(o, "recs") as List<object> ?? new List<object>())
            {
                var ro = rx as Dictionary<string, object>;
                obj.Recs.Add(Rec(Str(ro, "t"), Get(ro, "f") as List<object>));
            }
            m.Attach(obj);
        }
        return m;
    }

    static FrameRec Rec(string table, List<object> fields)
    {
        var r = new FrameRec(table ?? "");
        if (fields != null) foreach (var f in fields) { var p = f as List<object>; if (p != null && p.Count == 2) r.F.Add(new[] { (string)p[0], (string)p[1] }); }
        return r;
    }

    public static string Hash(FrameModel m)
    {
        using (var h = SHA256.Create())
            return string.Concat(h.ComputeHash(Encoding.UTF8.GetBytes(Write(m))).Select(b => b.ToString("x2")).ToArray());
    }

    // ---------------- JSON text

    static string Q(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in s ?? "")
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20 || c > 0x7e) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    static object Get(Dictionary<string, object> o, string k) { object v; return o != null && o.TryGetValue(k, out v) ? v : null; }
    static string Str(Dictionary<string, object> o, string k) { return Get(o, k) as string; }
    static double Num(Dictionary<string, object> o, string k) { object v = Get(o, k); return v is double ? (double)v : 0; }

    // a small reader: objects, arrays, strings, numbers, true / false / null
    sealed class Parser
    {
        readonly string s; int i;
        public Parser(string text) { s = text; }
        void Ws() { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }
        public object Value()
        {
            Ws();
            if (i >= s.Length) throw new FormatException("FrameModelJson: unexpected end");
            char c = s[i];
            if (c == '{')
            {
                i++;
                var o = new Dictionary<string, object>(StringComparer.Ordinal);
                Ws();
                if (s[i] == '}') { i++; return o; }
                while (true)
                {
                    Ws(); string k = String(); Ws();
                    if (s[i] != ':') throw new FormatException("FrameModelJson: ':' expected at " + i);
                    i++;
                    o[k] = Value(); Ws();
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == '}') { i++; return o; }
                    throw new FormatException("FrameModelJson: ',' or '}' expected at " + i);
                }
            }
            if (c == '[')
            {
                i++;
                var a = new List<object>();
                Ws();
                if (s[i] == ']') { i++; return a; }
                while (true)
                {
                    a.Add(Value()); Ws();
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == ']') { i++; return a; }
                    throw new FormatException("FrameModelJson: ',' or ']' expected at " + i);
                }
            }
            if (c == '"') return String();
            // in place: a Substring of the rest of the text per value made reading quadratic (4 s for a 1.4 MB model)
            if (string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
            if (string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
            if (string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }
            int st = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            return double.Parse(s.Substring(st, i - st), NumberStyles.Float, CultureInfo.InvariantCulture);
        }
        string String()
        {
            if (s[i] != '"') throw new FormatException("FrameModelJson: string expected at " + i);
            i++;
            var sb = new StringBuilder();
            while (s[i] != '"')
            {
                char c = s[i++];
                if (c != '\\') { sb.Append(c); continue; }
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u': sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture)); i += 4; break;
                    default: sb.Append(e); break;
                }
            }
            i++;
            return sb.ToString();
        }
    }
}
