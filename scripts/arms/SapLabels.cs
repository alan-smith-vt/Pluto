using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

// SapLabels  --  SAP2000 labels as the viewer's numeric ids (2026-11-05). A SAP label is text: joints "A1", frames
// "B-12" are legal. The binary's node, beam and shell ids are numbers, and SapToPluto read every label with int.Parse.
// Here an integer label keeps its number, and any other label gets the next id above the largest integer label of its
// kind (in the order the model defines them), the same in the model's and the results' tables. The original text is
// the viewer's label (LABL). View(tables) gives the tables with every label field so mapped: joints (Joint, JointI,
// JointJ, Joint1..n, MonitorJt), frames (Frame), areas (Area, AreaElem), links (Link, outside the link-property
// definitions, whose Link is the property's name), and group members (ObjectLabel by its ObjectType). A table with no
// text label is the source's own list.
// C# 5, one global batch.

public sealed class SapLabels
{
    readonly Dictionary<string, Dictionary<string, int>> _ids = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
    readonly Dictionary<string, Dictionary<int, string>> _text = new Dictionary<string, Dictionary<int, string>>(StringComparer.Ordinal);
    readonly Dictionary<string, int> _next = new Dictionary<string, int>(StringComparer.Ordinal);
    public int TextLabels;                    // labels that were not integers

    // the model's own definitions fix the numbering: joints, frames, areas, links
    public SapLabels(IS2kTables model)
    {
        Define("joint", model, "JOINT COORDINATES", "Joint");
        Define("frame", model, "CONNECTIVITY - FRAME", "Frame");
        Define("area", model, "CONNECTIVITY - AREA", "Area");
        Define("link", model, "CONNECTIVITY - LINK", "Link");
    }

    void Define(string kind, IS2kTables t, string table, string field)
    {
        var rows = t.GetTable(table);
        int max = 0;
        foreach (var r in rows) { string s; int v; if (r.TryGetValue(field, out s) && IsInt(s, out v) && v > max) max = v; }
        _next[kind] = max + 1;
        foreach (var r in rows) { string s; if (r.TryGetValue(field, out s)) Id(kind, s); }
    }

    static bool IsInt(string s, out int v) { return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) && v > 0; }

    // the id of a label (a new one for a label the model does not define: results of a mesh, a stray reference)
    public int Id(string kind, string label)
    {
        Dictionary<string, int> m;
        if (!_ids.TryGetValue(kind, out m)) { _ids[kind] = m = new Dictionary<string, int>(StringComparer.Ordinal); _text[kind] = new Dictionary<int, string>(); }
        int id;
        if (m.TryGetValue(label ?? "", out id)) return id;
        if (!IsInt(label, out id))
        {
            int next;
            if (!_next.TryGetValue(kind, out next)) next = 1;
            while (_text[kind].ContainsKey(next)) next++;
            id = next;
            _next[kind] = next + 1;
            TextLabels++;
        }
        m[label ?? ""] = id;
        _text[kind][id] = label ?? "";
        return id;
    }

    // the label an id came from (the id as text when unknown)
    public string Label(string kind, int id)
    {
        Dictionary<int, string> m; string s;
        return _text.TryGetValue(kind, out m) && m.TryGetValue(id, out s) ? s : id.ToString(CultureInfo.InvariantCulture);
    }

    public bool Has(string kind, string label) { Dictionary<string, int> m; return _ids.TryGetValue(kind, out m) && m.ContainsKey(label ?? ""); }

    // what a field names, if a label. Area and Frame fields count only in tables about areas / frames (a section's Area is
    // its cross-section area); Link counts outside the link-property definitions (where it is the property's name).
    public static string KindOf(string table, string field, Dictionary<string, string> row)
    {
        string k = FieldKind(table, field);
        if (k != Object) return k;
        string ot;
        if (row == null || !row.TryGetValue("ObjectType", out ot)) return null;
        ot = ot.ToLowerInvariant();
        return ot == "joint" || ot == "frame" || ot == "area" || ot == "link" ? ot : null;
    }

    const string Object = "*object";      // ObjectLabel: the kind is the row's ObjectType

    static string FieldKind(string table, string field)
    {
        string f = field.ToUpperInvariant(), t = (table ?? "").ToUpperInvariant();
        if (f == "JOINT" || f == "JOINTI" || f == "JOINTJ" || f == "MONITORJT" || (f.StartsWith("JOINT") && f.Length > 5 && char.IsDigit(f[5]))) return "joint";
        if ((f == "FRAME" || f == "FRAMEELEM") && t.Contains("FRAME")) return "frame";
        if ((f == "AREA" || f == "AREAELEM") && t.Contains("AREA")) return "area";
        if (f == "LINK" || f == "LINKELEM") return t.StartsWith("LINK PROPERTY DEFINITIONS") ? null : "link";
        if (f == "OBJECTLABEL") return Object;
        return null;
    }

    public IS2kTables View(IS2kTables t) { return new Mapped(t, this); }

    sealed class Mapped : IS2kTables
    {
        readonly IS2kTables _t; readonly SapLabels _l;
        public Mapped(IS2kTables t, SapLabels l) { _t = t; _l = l; }
        public bool HasTable(string name) { return _t.HasTable(name); }
        public IEnumerable<string> TableNames() { return _t.TableNames(); }
        public List<Dictionary<string, string>> GetTable(string name)
        {
            var rows = _t.GetTable(name);
            var kinds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);    // field -> kind (null: not a label)
            List<Dictionary<string, string>> o = null;
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                Dictionary<string, string> c = null;
                foreach (var kv in r)
                {
                    string kind;
                    if (!kinds.TryGetValue(kv.Key, out kind)) kinds[kv.Key] = kind = FieldKind(name, kv.Key);
                    if (kind == null) continue;
                    int v;
                    if (IsInt(kv.Value, out v)) continue;   // an integer label is its own id
                    if (kind == Object) { kind = KindOf(name, kv.Key, r); if (kind == null) continue; }
                    if (c == null) c = new Dictionary<string, string>(r, StringComparer.OrdinalIgnoreCase);
                    c[kv.Key] = _l.Id(kind, kv.Value).ToString(CultureInfo.InvariantCulture);
                }
                if (c != null && o == null) o = new List<Dictionary<string, string>>(rows.GetRange(0, i));
                if (o != null) o.Add(c ?? r);
            }
            return o ?? rows;
        }
    }
}
