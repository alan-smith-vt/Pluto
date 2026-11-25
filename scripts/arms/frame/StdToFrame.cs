using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

// StdToFrame  --  a STAAD.Pro input file Pluto wrote (Build-Frame -Std) read back as a frame model, for Build-Frame -In
// <x>.std (2026-11-23; vault/format/frame-to-staad.md). The inverse of FrameStd, over the .anl reader's own STAAD input
// parser (StaadInput, scripts/lib/readers/ANLReader.cs). STAAD's commands are the model, so an edit made in STAAD comes
// back; the comments FrameStd writes give back what STAAD has no place for: SAP's labels, its section, material and group
// names, a table section's SAP row, the SAP version and units. Exact or refused: a command the frame model cannot hold
// exactly, or one FrameStd does not write, refuses the file, every item named. The model is written as .s2k text and read
// by FrameS2k, as PipeToFrame's seed is.
//   axes        STAAD (X, Y, Z) is SAP (X, -Z, Y); a member's BETA becomes its SAP local axis angle
//   sections    PRIS: a General section with SAP's analysis properties (AY / AZ absent: shear areas 0); TABLE ST: the SAP
//               row in the comment above it, else (a shape picked in STAAD) its entry in SAP's section library, as SAP
//               writes a catalog section (FrameSapLibrary.Row; I sections). The same SAP section
//               with other numbers (its frames' modifiers, which FrameStd folds in) gets them back as frame modifiers
//   materials   each STAAD material a SAP material (Steel with TYPE STEEL, else Other); one FrameStd made for a weight
//               modifier (NAME_W1) stays a material of its own, with its unit weight
//   offsets     along the member: end lengths (rigid); across it, the same at both ends: joint offsets (local)
//   supports    global: restraints and springs; INCLINED: the joint's local axes from STAAD's (its BETA-0 axes along the
//               vector); ENFORCED: restraints (a support displacement needs them)
//   trusses     linear two-joint links, U1 = E AX / L (the header's EA / L when it agrees)
//   loads       reference loads -> load patterns (self weight on rigid end zones, which FrameStd adds as joint loads, taken
//               off again: SAP's self weight covers them); primary loads -> linear static cases, P-Delta (nonlinear, from
//               zero) under a PDELTA analysis; a primary load's own loads and support displacements -> a pattern of its
//               own; REPEAT LOAD -> the repeated loads' patterns, times the factors; combinations (before or after the
//               analysis) -> Linear Add, SRSS, Abs Add
//   a model made in STAAD (2026-11-24): a later UNIT's numbers converted to the first UNIT's (StaadInput); GEOMETRY and
//               FLOOR groups as groups of their members; a material no member uses left out, a TYPE other than STEEL
//               SAP's Other; SELFWEIGHT ... LIST of every member as none; LOADTYPE ... REDUCIBLE as its type
//   carried     what SAP has no place for, in Pluto's own tables (StaadCarry; the workbook's STAAD sheets): the commands
//               after the analysis as written (the analysis' PRINT option first), and STAAD's load numbers when they are
//               not -Std's own; FrameStd writes both again, the .s2k for SAP leaves them out
// C# 5 (prefix StdToFrame).

public sealed class StdToFrameRefusal : Exception
{
    public readonly List<string> Items;
    public StdToFrameRefusal(List<string> items) : base(Text(items)) { Items = items; }
    static string Text(List<string> items)
    {
        return "the frame model cannot hold this .std exactly (" + items.Count.ToString(CultureInfo.InvariantCulture) + " item" + (items.Count == 1 ? "" : "s") + "): " +
               string.Join(" | ", items.ToArray());
    }
}

public static class StdToFrame
{
    // the frame model of a .std (its source the generated .s2k text); notes gets every notice; sapVersion (null: the
    // header's, else 22.0.0) stamps it; sapLibrary: SAP's section library, for a TABLE ST shape with no SAP row (null: the
    // one installed, FrameSapLibrary.Folder); throws StdToFrameRefusal with every refusal
    public static FrameModel Read(string path, string sapVersion, List<string> notes)
    {
        return Read(path, sapVersion, notes, null);
    }

    public static FrameModel Read(string path, string sapVersion, List<string> notes, string sapLibrary)
    {
        return FrameS2k.Read(S2kDoc.ParseText(Text(path, sapVersion, notes, sapLibrary)));
    }

    public static string Text(string path, string sapVersion, List<string> notes)
    {
        return Text(path, sapVersion, notes, null);
    }

    public static string Text(string path, string sapVersion, List<string> notes, string sapLibrary)
    {
        return new Reader(StaadInput.Read(path), sapVersion, notes ?? new List<string>(), sapLibrary).Run();
    }

    // whether a .std's model and the model written back are the same as STAAD reads them: each joint, member, plate,
    // property, constant, offset, release, support and group by its id, each material by its name, each primary load
    // through its reference loads and their factors (so loads given in a primary load itself, which come back as a
    // reference load of their own, are the same), each combination by its number; numbers to the digits the file gives
    // (STAAD.Pro's own save keeps 6 significant ones). Order, layout, comments, prints and STAAD's job information aside.
    // Null when the same; else the first differences, at most max. file: the .std; back: the text written back
    public static List<string> Differences(string file, string back, int max)
    {
        string tmp = System.IO.Path.GetTempFileName();
        try
        {
            System.IO.File.WriteAllText(tmp, back);
            FactSet a = Facts(StaadInput.Read(file)), b = Facts(StaadInput.Read(tmp));
            var o = new List<string>();
            foreach (string k in a.Order.Concat(b.Order.Where(x => !a.By.ContainsKey(x))))
            {
                List<double[]> la, lb;
                a.By.TryGetValue(k, out la); b.By.TryGetValue(k, out lb);
                var left = lb == null ? new List<double[]>() : new List<double[]>(lb);
                var only = new List<double[]>();
                foreach (var v in la ?? new List<double[]>())
                {
                    int j = left.FindIndex(w => w.Length == v.Length && Enumerable.Range(0, v.Length).All(i => Close(v[i], w[i])));
                    if (j >= 0) left.RemoveAt(j); else only.Add(v);
                }
                for (int i = 0; i < Math.Max(only.Count, left.Count) && o.Count < max; i++)
                {
                    if (i < only.Count && i < left.Count) o.Add(k + ": " + Show(only[i]) + " in the file, " + Show(left[i]) + " written back");
                    else if (i < only.Count) o.Add("only in the file: " + k + (only[i].Length > 0 ? " " + Show(only[i]) : ""));
                    else o.Add("only written back: " + k + (left[i].Length > 0 ? " " + Show(left[i]) : ""));
                }
            }
            return o.Count == 0 ? null : o;
        }
        finally { try { System.IO.File.Delete(tmp); } catch (System.IO.IOException) { } }
    }

    static string Show(double[] v) { return string.Join(" ", v.Select(x => FrameStd.N(x)).ToArray()); }

    sealed class FactSet
    {
        public readonly Dictionary<string, List<double[]>> By = new Dictionary<string, List<double[]>>(StringComparer.Ordinal);
        public readonly List<string> Order = new List<string>();
        public void Add(string key, params double[] v)
        {
            List<double[]> l;
            if (!By.TryGetValue(key, out l)) { By[key] = l = new List<double[]>(); Order.Add(key); }
            l.Add(v);
        }
    }

    // words as a key ("UNI GY #") and their numbers
    static string Words(IEnumerable<string> t, List<double> nums)
    {
        var w = new List<string>();
        foreach (string x in t) { double v; if (Num(x, out v)) { nums.Add(v); w.Add("#"); } else w.Add(x.ToUpperInvariant()); }
        return string.Join(" ", w.ToArray());
    }

    // a release or support list as a set: FX .. MZ, and KFX v .. KMZ v, in any order
    // a command with its member or joint list after MEMB / MEMBER / LIST by the ids it names: ranges expanded, numbers in
    // order after the group names ("DFF 240 MEMB 5 TO 7 11" is "DFF 240 MEMB 5 6 7 11")
    static string IdsSorted(string c)
    {
        var t = c.Split(' ');
        int at = Array.FindIndex(t, x => x == "MEMB" || x == "MEMBER" || x == "LIST");
        if (at < 0 || at + 1 >= t.Length) return c;
        var names = new List<string>(); var ids = new SortedSet<long>();
        for (int k = at + 1; k < t.Length; k++)
        {
            long a, b, by = 1;
            if (k + 2 < t.Length && t[k + 1] == "TO" && long.TryParse(t[k], NumberStyles.None, Inv, out a) && long.TryParse(t[k + 2], NumberStyles.None, Inv, out b) && b - a < 100000)
            {
                k += 2;
                if (k + 2 < t.Length && t[k + 1] == "BY" && long.TryParse(t[k + 2], NumberStyles.None, Inv, out by) && by > 0) k += 2; else by = 1;
                for (long n = a; n <= b; n += by) ids.Add(n);
                continue;
            }
            if (long.TryParse(t[k], NumberStyles.None, Inv, out a)) ids.Add(a); else names.Add(t[k]);
        }
        names.Sort(StringComparer.Ordinal);
        return string.Join(" ", t.Take(at + 1).Concat(names).Concat(ids.Select(x => x.ToString(Inv))).ToArray());
    }

    static void ItemFacts(FactSet f, string key, string[] t, int from)
    {
        for (int k = from; k < t.Length; k++)
        {
            double v;
            if (k + 1 < t.Length && t[k].StartsWith("K", StringComparison.OrdinalIgnoreCase) && Num(t[k + 1], out v)) { f.Add(key + " " + t[k].ToUpperInvariant(), v); k++; }
            else f.Add(key + " " + t[k].ToUpperInvariant());
        }
    }

    // STAAD's unit words, as SAP's units (STAAD takes each spelling: KN and KNS are one unit)
    static readonly Dictionary<string, string> LenWords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
        { "INCHES", "in" }, { "INCH", "in" }, { "IN", "in" }, { "FEET", "ft" }, { "FOOT", "ft" }, { "FT", "ft" }, { "MMS", "mm" }, { "MM", "mm" },
        { "CM", "cm" }, { "CMS", "cm" }, { "METER", "m" }, { "METERS", "m" }, { "METRE", "m" }, { "M", "m" } };
    static readonly Dictionary<string, string> ForceWords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
        { "KIP", "Kip" }, { "KIPS", "Kip" }, { "POUND", "lb" }, { "POUNDS", "lb" }, { "LB", "lb" }, { "LBS", "lb" }, { "KNS", "KN" }, { "KN", "KN" },
        { "NEWTON", "N" }, { "NEWTONS", "N" }, { "N", "N" }, { "KG", "Kgf" }, { "KGS", "Kgf" }, { "MTON", "Tonf" }, { "MTONS", "Tonf" } };

    // a STAAD table property: { type (ST, T, LD, SD), name, spacing } from the legacy form (TABLE ST W10X33 under MEMBER
    // PROPERTY AMERICAN) or STAAD 2025's database form (TABLE 'W' ST 'W12X26' under MEMBER PROPERTY 'US (AISC 2023).db3',
    // the names probe); null with why when it is neither. table: the MEMBER PROPERTY block's
    internal static string[] TableOf(string table, string spec, out string why)
    {
        why = null;
        var q = Regex.Matches(spec ?? "", @"'[^']*'|\S+").Cast<Match>().Select(m => m.Value).ToList();
        Func<string, string> unq = x => x.Length >= 2 && x[0] == '\'' && x[x.Length - 1] == '\'' ? x.Substring(1, x.Length - 2).Trim() : x;
        if (q.Count < 3 || !Is(q[0], "TABLE")) { why = "not a table property"; return null; }
        bool db = q[1].StartsWith("'", StringComparison.Ordinal);
        int k = db ? 2 : 1;
        string t = (table ?? "").Trim();
        if (db && !t.StartsWith("'", StringComparison.Ordinal)) { why = "a database table under MEMBER PROPERTY " + t; return null; }
        if (!db && !Is(t, "AMERICAN")) { why = "MEMBER PROPERTY " + t + " (FrameStd writes AMERICAN and STAAD 2025's AISC databases)"; return null; }
        if (q.Count < k + 2) { why = "no shape"; return null; }
        string type = q[k].ToUpperInvariant(), name = unq(q[k + 1]);
        if (type != "ST" && type != "T" && type != "LD" && type != "SD") { why = "TABLE " + type + " (Pluto reads ST, T, LD and SD)"; return null; }
        string sp = "0";
        int r = k + 2;
        if (r < q.Count)
        {
            double v;
            if (r + 2 == q.Count && Is(q[r], "SP") && Num(q[r + 1], out v) && (type == "LD" || type == "SD")) sp = q[r + 1];
            else { why = "TABLE ... " + string.Join(" ", q.Skip(r).ToArray()) + " (Pluto reads a spacing SP of a double angle only)"; return null; }
        }
        return new[] { type, name, sp };
    }

    static bool TryValue(StaadMaterial m, string key, out double v)
    {
        v = 0;
        foreach (var kv in m.Values) if (Is(kv.Key, key)) { v = kv.Value; return true; }
        return false;
    }

    static FactSet Facts(StaadInput s)
    {
        var f = new FactSet();
        foreach (string u in s.Unread) f.Add("not read: " + u);
        // a unit by what it is, not its spelling (KN, KNS); a word neither table has, as written
        Func<string, string> unit = w => { string x; return LenWords.TryGetValue(w, out x) || ForceWords.TryGetValue(w, out x) ? x : w.ToUpperInvariant(); };
        foreach (var u in s.Units.Take(1).Select(x => string.Join(" ", x.Select(unit).ToArray()))) f.Add("UNIT " + u);   // every number is in the first
        foreach (int id in s.JointIds) f.Add("joint " + I(id), s.Joints[id]);
        foreach (var m in s.Members) f.Add("member " + I(m[0]) + " from joint " + I(m[1]) + " to " + I(m[2]));
        foreach (var p in s.Plates) f.Add("plate " + I(p[0]) + " on joints " + string.Join(" ", p.Skip(1).Select(I).ToArray()));
        foreach (int id in s.PlaneStress) f.Add("plate " + I(id) + " plane stress");
        foreach (int id in s.Truss) f.Add("member " + I(id) + " truss");
        foreach (string g in s.GroupOrder)
        {
            string kind; s.GroupKinds.TryGetValue(g, out kind);
            if (kind == "GEOMETRY" || kind == "FLOOR") kind = "MEMBER";       // read as a group of its members (-Std writes MEMBER)
            f.Add("group " + g.ToUpperInvariant() + " (" + (kind ?? "") + "): " + string.Join(" ", s.Groups[g].Distinct().OrderBy(x => x).Select(I).ToArray()));
        }
        // the materials a member or plate uses (CONSTANTS MATERIAL); DAMP aside (static cases only)
        var usedMats = new HashSet<string>(s.Constants.Where(c => c.Spec.Length == 2 && Is(c.Spec[0], "MATERIAL")).Select(c => c.Spec[1]), StringComparer.OrdinalIgnoreCase);
        foreach (var m in s.Materials.Where(x => usedMats.Contains(x.Name)))
        {
            string h = "material " + m.Name.ToUpperInvariant();
            f.Add(h + " TYPE " + (m.Type ?? "").ToUpperInvariant());
            foreach (var kv in m.Values) if (!Is(kv.Key, "DAMP")) f.Add(h + " " + kv.Key.ToUpperInvariant(), kv.Value);
            // no G: STAAD's, E / 2 (1 + POISSON) (a material made in STAAD's GUI gives none; -Std writes it)
            double e, nu;
            if (!m.Values.Keys.Any(k => Is(k, "G")) && TryValue(m, "E", out e) && TryValue(m, "POISSON", out nu)) f.Add(h + " G", e / (2 * (1 + nu)));
            foreach (var kv in m.Strength) f.Add(h + " STRENGTH " + kv.Key.ToUpperInvariant(), kv.Value);
        }
        foreach (var p in s.MemberProperties)
            foreach (int id in p.Ids)
            {
                string h = "member " + I(id) + " property ";
                double v;
                string tw;
                string[] t = p.Spec.Length > 0 && Is(p.Spec[0], "TABLE") ? TableOf(p.Table, string.Join(" ", p.Spec), out tw) : null;
                string pw;
                double[] pr = p.Spec.Length % 2 == 1 && Is(p.Spec[0], "PRIS") ? Reader.Pris(p.Spec, out pw) : null;
                // a PRIS by the numbers STAAD analyses: its short forms worked out as STAAD does, a round one's ZD its YD
                if (pr != null)
                {
                    string[] keys = { "AX", "IX", "IY", "IZ", "AY", "AZ", "YD", "ZD" };
                    for (int k = 0; k < 8; k++) if (!double.IsNaN(pr[k])) f.Add(h + "PRIS " + keys[k], pr[k]);
                }
                else if (p.Spec.Length % 2 == 1 && Is(p.Spec[0], "PRIS"))
                    for (int k = 1; k + 1 < p.Spec.Length; k += 2) { if (Num(p.Spec[k + 1], out v)) f.Add(h + "PRIS " + p.Spec[k].ToUpperInvariant(), v); else f.Add(h + string.Join(" ", p.Spec)); }
                // a table section by its shape, in either form, from whichever STAAD table (AMERICAN or a database)
                else if (t != null) { Num(t[2], out v); f.Add(h + "TABLE " + t[0] + " " + t[1].ToUpperInvariant() + " SP", v); }
                else { var n = new List<double>(); f.Add(h + (p.Table ?? "") + " " + Words(p.Spec, n), n.ToArray()); }
            }
        foreach (var p in s.ElementProperties) foreach (int id in p.Ids) { var n = new List<double>(); f.Add("plate " + I(id) + " " + Words(p.Spec, n), n.ToArray()); }
        var all = s.Members.Select(m => m[0]).Concat(s.Plates.Select(p => p[0])).ToList();
        foreach (var c in s.Constants) foreach (int id in c.All ? all : c.Ids) { var n = new List<double>(); f.Add("member or plate " + I(id) + " " + Words(c.Spec, n), n.ToArray()); }
        foreach (var o in s.Offsets) foreach (int id in o.Ids) { var n = new List<double>(); f.Add("member " + I(id) + " offset " + Words(o.Spec, n), n.ToArray()); }
        foreach (var r in s.Releases)
            foreach (int id in r.Ids)
                foreach (string end in Is(r.Spec[0], "BOTH") ? new[] { "START", "END" } : new[] { r.Spec[0].ToUpperInvariant() })
                    ItemFacts(f, "member " + I(id) + " released at its " + end, r.Spec, 1);
        foreach (var sp in s.Supports)
            foreach (int id in sp.Ids)
            {
                string h = "joint " + I(id) + " support";
                var t = sp.Spec;
                int k = 0;
                if (Is(t[0], "INCLINED") && t.Length >= 5) { var n = new List<double>(); Words(t.Skip(1).Take(3), n); f.Add(h + " INCLINED", n.ToArray()); k = 4; }
                if (k < t.Length) f.Add(h + " " + t[k].ToUpperInvariant());
                if (k + 1 < t.Length && Is(t[k + 1], "BUT"))
                {
                    // a DOF released and given a spring is the spring (FIXED BUT MX KMX v is FIXED BUT KMX v)
                    var rest = t.Skip(k + 2).ToList();
                    var sprung = new HashSet<string>(rest.Where(x => x.Length == 3 && (x[0] == 'K' || x[0] == 'k')).Select(x => x.Substring(1).ToUpperInvariant()));
                    ItemFacts(f, h + " but", rest.Where(x => !sprung.Contains(x.ToUpperInvariant())).ToArray(), 0);
                }
                else if (k + 1 < t.Length) { var n = new List<double>(); f.Add(h + " " + Words(t.Skip(k + 1), n), n.ToArray()); }
            }
        // loads: each primary load through its reference loads, a factor with every item
        var refs = new Dictionary<string, StaadLoad>(StringComparer.OrdinalIgnoreCase);
        var primaries = new Dictionary<string, StaadLoad>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in s.Loads) if (l.Kind == "reference") refs[l.Id] = l; else if (l.Kind == "primary" && !primaries.ContainsKey(l.Id)) primaries[l.Id] = l;
        // a primary load's items times a factor: its own, its reference loads', and a REPEAT LOAD's repeated loads'
        Action<StaadLoad, string, double, int> items = null;
        items = (l, h, factor, depth) =>
        {
            foreach (var b in l.Blocks)
            {
                if (b.Name != "REFERENCE LOAD" && b.Name != "REPEAT LOAD") { LoadFacts(f, h, b, factor, s); continue; }
                foreach (var line in b.Lines)
                    for (int k = 0; k < line.Spec.Length; k += 2)
                    {
                        StaadLoad r; double fk;
                        bool ok = k + 1 < line.Spec.Length && Num(line.Spec[k + 1], out fk) && (b.Name == "REFERENCE LOAD" ? refs.TryGetValue(line.Spec[k], out r) : primaries.TryGetValue(line.Spec[k], out r)) && depth < 20;
                        if (!ok) { f.Add(h + " " + b.Name + " " + string.Join(" ", line.Spec.Skip(k).Take(2).ToArray())); continue; }
                        Num(line.Spec[k + 1], out fk);
                        if (b.Name == "REFERENCE LOAD") { refs.TryGetValue(line.Spec[k], out r); foreach (var rb in r.Blocks) LoadFacts(f, h, rb, factor * fk, s); }
                        else { primaries.TryGetValue(line.Spec[k], out r); items(r, h, factor * fk, depth + 1); }
                    }
            }
        };
        foreach (var l in s.Loads)
        {
            if (l.Kind == "primary")
            {
                string h = "LOAD " + l.Id;
                // the LOADTYPE as the model holds it (SAP's design type: REDUCIBLE and none are design data, NONE)
                bool known;
                f.Add(h + " LOADTYPE " + Reader.DesignType(l.LoadType, out known).ToUpperInvariant() + " TITLE " + Regex.Replace(l.Title.ToUpperInvariant(), @"\s+", " "));
                items(l, h, 1, 0);
            }
            else if (l.Kind == "combination")
            {
                string h = "LOAD COMBINATION " + l.Id;
                f.Add(h + " " + l.Method + " " + Regex.Replace(l.Title.ToUpperInvariant(), @"\s+", " "));
                foreach (var b in l.Blocks)
                    foreach (var line in b.Lines)
                        for (int k = 0; k + 1 < line.Spec.Length; k += 2) { double v; if (Num(line.Spec[k + 1], out v)) f.Add(h + " load " + line.Spec[k], v); else f.Add(h + " " + string.Join(" ", line.Spec)); }
            }
        }
        if (s.Analysis != null) f.Add(Regex.Replace(s.Analysis.ToUpperInvariant(), @"\s+", " "));
        // each envelope's loads as written: STAAD lists a load under every envelope that names it
        foreach (var e in s.Envelopes)
            f.Add("ENVELOPE " + I(e.Number) + " TYPE " + e.Type + ": loads " + FrameStd.Condense(e.Loads.Distinct()));
        DesignFacts(s, f);
        return f;
    }

    // the commands after the analysis as STAAD reads them (StaadDesign.FromCommands): each set's code and method; each
    // parameter's value for ALL and for each member, a later line winning (so "KY 1.2 MEMB _COLUMNS" and "KY 1.2 MEMB 1 TO
    // 4" are the same, and a value given to ALL clears the lists before it); the run's steps in order, each with its load
    // list, units and members
    static void DesignFacts(StaadInput s, FactSet f)
    {
        var cmds = new List<string>();
        if (s.AnalysisPrint != null) cmds.Add("PERFORM ANALYSIS " + s.AnalysisPrint);
        cmds.AddRange(s.Output);
        if (cmds.Count == 0) return;
        var d = StaadDesign.FromCommands(cmds, s.Units.Count > 0 ? string.Join(" ", s.Units[0]) : "");
        if (d.AnalysisPrint.Length > 0) f.Add("the analysis' PRINT " + Regex.Replace(d.AnalysisPrint.ToUpperInvariant(), @"\s+", " "));
        // a member list as its ids, in order (null: ALL); a group by its members
        Func<string, List<long>> ids = text =>
        {
            var t = (text ?? "").Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (t.Length == 0 || (t.Length == 1 && Is(t[0], "ALL"))) return null;
            var o = new SortedSet<long>();
            for (int k = 0; k < t.Length; k++)
            {
                long a, b, by = 1;
                if (k + 2 < t.Length && Is(t[k + 1], "TO") && long.TryParse(t[k], NumberStyles.None, Inv, out a) && long.TryParse(t[k + 2], NumberStyles.None, Inv, out b) && b - a < 1000000)
                {
                    k += 2;
                    if (k + 2 < t.Length && Is(t[k + 1], "BY") && long.TryParse(t[k + 2], NumberStyles.None, Inv, out by) && by > 0) k += 2; else by = 1;
                    for (long n = a; n <= b; n += by) o.Add(n);
                    continue;
                }
                string g = s.Groups.Keys.FirstOrDefault(x => Is(x, t[k]));
                if (g != null) { foreach (int id in s.Groups[g]) o.Add(id); continue; }
                if (long.TryParse(t[k], NumberStyles.None, Inv, out a)) o.Add(a); else o.Add(-1 - Math.Abs((long)t[k].ToUpperInvariant().GetHashCode()));     // a name nothing defines
            }
            return o.ToList();
        };
        Action<string, string> value = (key, v) => { double x; if (Num(v, out x)) f.Add(key, x); else f.Add(key + " = " + Regex.Replace((v ?? "").ToUpperInvariant(), @"\s+", " ")); };
        foreach (var set in d.Sets)
        {
            string who = "design set " + set.Name;
            f.Add(who + ": CODE " + Regex.Replace(set.Code.ToUpperInvariant(), @"\s+", " ") + ", METHOD " + set.Method.ToUpperInvariant() + ", UNIT " + StaadDesign.UnitKey(set.Units, ""));
            var all = new Dictionary<string, string>(StringComparer.Ordinal);
            var per = new SortedDictionary<long, Dictionary<string, string>>();
            foreach (var p in d.Params.Where(x => x.Set == set.Name))
            {
                var list = ids(p.Members);
                var items = StaadDesign.Columns.Where(c => p.Values.ContainsKey(c) && p.Values[c].Trim().Length > 0).Select(c => new KeyValuePair<string, string>(c, p.Values[c].Trim())).Concat(StaadDesign.Others(p.Other));
                foreach (var it in items)
                {
                    if (list == null) { all[it.Key] = it.Value; foreach (var m in per.Values) m.Remove(it.Key); continue; }
                    foreach (long id in list)
                    {
                        Dictionary<string, string> m;
                        if (!per.TryGetValue(id, out m)) per[id] = m = new Dictionary<string, string>(StringComparer.Ordinal);
                        m[it.Key] = it.Value;
                    }
                }
            }
            foreach (var kv in all.OrderBy(x => x.Key, StringComparer.Ordinal)) value(who + ": " + kv.Key + " ALL", kv.Value);
            foreach (var m in per) foreach (var kv in m.Value.OrderBy(x => x.Key, StringComparer.Ordinal)) value(who + ": " + kv.Key + " member " + I((int)m.Key), kv.Value);
        }
        string loads = "";
        for (int k = 0; k < d.Run.Count; k++)
        {
            var st = d.Run[k];
            if (st.LoadList.Trim().Length > 0) loads = Regex.Replace(st.LoadList.ToUpperInvariant(), @"\s+", " ").Trim();
            string head = "design step " + I(k + 1) + ": LOAD LIST " + (loads.Length > 0 ? loads : "(none)") + "; ";
            if (st.Action == "CHECK CODE" || st.Action == "SELECT")
            {
                var list = ids(st.Members);
                f.Add(head + st.Action + " under PARAMETER " + st.Set + ", " + (list == null ? "ALL" : "members " + string.Join(" ", list.Select(x => x.ToString(Inv)).ToArray())));
            }
            else
                f.Add(head + "UNIT " + StaadDesign.UnitKey(st.Units.Trim().Length > 0 ? st.Units : d.Units, "") + "; " + IdsSorted(Regex.Replace(st.Text.ToUpperInvariant(), @"\s+", " ")));
        }
    }

    // a load block's items, each with the factor it is applied with; joint loads summed per joint and direction
    static void LoadFacts(FactSet f, string h, StaadBlock b, double factor, StaadInput s)
    {
        if (b.Name == "SELFWEIGHT")
        {
            // LIST of every member and plate: the same as none (StdToFrame reads it so)
            var head = b.Head;
            if (head.Length > 3 && Is(head[2], "LIST"))
            {
                int i = 3;
                var ids = new HashSet<int>(s.IdList(head, ref i));
                if (i == head.Length && s.Members.Select(x => x[0]).Concat(s.Plates.Select(x => x[0])).All(ids.Contains)) head = head.Take(2).ToArray();
            }
            var n = new List<double>(); f.Add(h + " SELFWEIGHT " + Words(head, n), n.Concat(new[] { factor }).ToArray()); return;
        }
        var sums = new SortedDictionary<string, double>(StringComparer.Ordinal);
        foreach (var line in b.Lines)
            foreach (int id in line.Ids)
            {
                if (b.Name == "JOINT LOAD" && line.Spec.Length % 2 == 0)
                {
                    for (int k = 0; k + 1 < line.Spec.Length; k += 2)
                    {
                        double v; string key = h + " JOINT LOAD joint " + I(id) + " " + line.Spec[k].ToUpperInvariant();
                        if (!Num(line.Spec[k + 1], out v)) { f.Add(key + " " + line.Spec[k + 1]); continue; }
                        double have; sums.TryGetValue(key, out have); sums[key] = have + v;
                    }
                    continue;
                }
                var n = new List<double>();
                f.Add(h + " " + b.Name + " " + I(id) + " " + Words(line.Spec, n), n.Concat(new[] { factor }).ToArray());
            }
        foreach (var kv in sums) f.Add(kv.Key, kv.Value, factor);
    }

    // half a unit in the last digit a number is written with (0.05 for 11153.8): the file's own precision
    static double HalfUnit(double v)
    {
        if (v == 0 || double.IsNaN(v) || double.IsInfinity(v)) return 0;
        string t = Math.Abs(v).ToString("R", Inv);
        int e = t.IndexOfAny(new[] { 'E', 'e' }), exp = 0;
        if (e >= 0) { exp = int.Parse(t.Substring(e + 1), Inv); t = t.Substring(0, e); }
        int dot = t.IndexOf('.');
        return 0.5 * Math.Pow(10, exp - (dot < 0 ? 0 : t.Length - dot - 1));
    }

    // two numbers the same to the digits they are written with (Pluto writes 12 significant ones, STAAD.Pro's own save 6;
    // never looser than 1e-5 of their size)
    static bool Close(double a, double b)
    {
        double scale = Math.Max(Math.Abs(a), Math.Abs(b));
        return Math.Abs(a - b) <= Math.Min(HalfUnit(a) + HalfUnit(b), 1e-5 * scale) + 1e-12 * scale + 1e-10;
    }

    // a number's relative precision as written: 0 for a whole number, at most 1e-5
    static double RelHalf(double v)
    {
        if (v == 0 || (v == Math.Round(v) && Math.Abs(v) < 1e6)) return 0;
        return Math.Min(HalfUnit(v) / Math.Abs(v), 1e-5);
    }

    // ---------------- vectors (STAAD coordinates unless named)

    static double[] Rinv(double[] v) { return new[] { v[0] + 0.0, -v[2] + 0.0, v[1] + 0.0 }; }   // STAAD global -> SAP global
    static double[] Sub(double[] a, double[] b) { return new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] }; }
    static double[] Add(double[] a, double[] b) { return new[] { a[0] + b[0], a[1] + b[1], a[2] + b[2] }; }
    static double[] Mul(double[] a, double s) { return new[] { a[0] * s, a[1] * s, a[2] * s }; }
    static double Dot(double[] a, double[] b) { return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]; }
    static double Len(double[] a) { return Math.Sqrt(Dot(a, a)); }
    static double[] Unit(double[] a) { double l = Len(a); return l < 1e-300 ? new double[3] : Mul(a, 1 / l); }
    static double[] Cross(double[] a, double[] b) { return new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] }; }

    // an angle in degrees: within 1e-7 of a whole degree snapped to it, in (-180, 180]
    static double Snap(double deg)
    {
        double r = Math.Round(deg);
        if (Math.Abs(deg - r) < 1e-7) deg = r;
        if (deg <= -180 + 1e-9) deg += 360;
        if (deg > 180 + 1e-9) deg -= 360;
        return deg == 0 ? 0 : deg;
    }

    // SAP's joint angles A B C (local axes the columns of Rz(A) Ry(B) Rx(C)) for local axes a1 a2 a3 (SAP coordinates)
    static double[] Euler(double[] a1, double[] a2, double[] a3)
    {
        double sb = Math.Max(-1, Math.Min(1, -a1[2]));
        double b = Math.Asin(sb), cb = Math.Cos(b), a, c;
        if (Math.Abs(cb) > 1e-9) { a = Math.Atan2(a1[1], a1[0]); c = Math.Atan2(a2[2], a3[2]); }
        else { a = 0; c = Math.Atan2(sb * a2[0], a2[1]); }
        return new[] { Snap(a * 180 / Math.PI), Snap(b * 180 / Math.PI), Snap(c * 180 / Math.PI) };
    }

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static string F(double v) { return FrameModel.Fmt(v == 0 ? 0 : v); }

    // the shortest number whose STAAD form (to) is the file's, to its digits: SAP gets a spring of 1000 per radian, not
    // the 999.99999999752 that STAAD's 17.4532925199 per degree multiplies out to; it writes back the same
    static double Shortest(double v, Func<double, double> to, double written)
    {
        for (int k = 1; k <= 15; k++)
        {
            double c = double.Parse(v.ToString("G" + k.ToString(Inv), Inv), Inv);
            if (Close(to(c), written)) return c;
        }
        return v;
    }
    static double Round12(double v) { return double.Parse(v.ToString("G12", Inv), Inv); }
    static string I(int v) { return v.ToString(Inv); }
    static string Q(string v) { return v.IndexOfAny(new[] { ' ', '"', '\'', '(', ')', ',' }) >= 0 ? "\"" + v.Replace("\"", "'") + "\"" : v; }
    static bool Is(string a, string b) { return string.Equals((a ?? "").Trim(), b, StringComparison.OrdinalIgnoreCase); }
    static bool Num(string s, out double v) { return double.TryParse(s, NumberStyles.Float, Inv, out v); }

    // ---------------- the reader

    sealed class Mem
    {
        public int Id, I, J; public string Label; public double L;
        public double[] X, Y, Z;                 // along it, SAP's local 2 and 3 (STAAD's y and z after BETA)
        public double Beta, Angle;
        public double Delta;                      // a single angle's turn from SAP's local 2 to STAAD's y (its principal axis)
        public string Mat;                        // the STAAD material
        public StaadSpec Prop;
        public double ZoneI, ZoneJ;
        public double[] Trans = new double[3];    // the offset across it
        public double[] OffI, OffJ;
        public string Section; public double[] Mods;
        public double Depth, Width, Ax;           // the section's YD / ZD, and AX (the property as written)
        public bool[] Rel = new bool[12]; public double[] Pf = new double[12]; public bool HasRel;
    }

    sealed class Sec { public string Name, Mat, Shape, Row; public double[] P; }   // P: AX IX IY IZ AY AZ YD ZD (a row: SAP's own)

    sealed class Pat
    {
        public string Name, Type; public double SelfWeight;
        public readonly SortedDictionary<int, double[]> Joint = new SortedDictionary<int, double[]>();     // SAP F1..M3
        public readonly SortedDictionary<int, double[]> Ground = new SortedDictionary<int, double[]>();    // SAP U1..R3
        public readonly List<string> Point = new List<string>(), Dist = new List<string>(), Temp = new List<string>();
    }

    sealed class Case { public int Number; public string Name, Type; public bool TypeNeeded; public readonly List<KeyValuePair<string, double>> Loads = new List<KeyValuePair<string, double>>(); }
    sealed class Combo { public int Number; public string Name, Kind; public readonly List<KeyValuePair<int, double>> Items = new List<KeyValuePair<int, double>>(); }
    // a STAAD load envelope (DEFINE ENVELOPE): a SAP Envelope combination of cases ("C") and combinations ("K")
    sealed class Env { public int Number; public string Name, Type; public readonly List<string[]> Items = new List<string[]>(); }

    sealed class Reader
    {
        readonly StaadInput In; readonly string SapVersion; readonly List<string> Notes;
        string Library;                                                                    // SAP's section library ("": none here)
        readonly Dictionary<string, string[]> LibRow = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);   // shape -> row, why
        readonly List<string> Refused = new List<string>();
        string HeadVersion, HeadUnits, UnitsText, LenUnit;
        double Gravity;
        bool Pluto;
        readonly Dictionary<string, Dictionary<int, string>> HeadLabel = new Dictionary<string, Dictionary<int, string>>();
        readonly Dictionary<int, double> HeadKe = new Dictionary<int, double>();
        readonly Dictionary<string, string> GroupLabel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> MatLabel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<int, string> CaseHead = new Dictionary<int, string>(), ComboHead = new Dictionary<int, string>();

        readonly Dictionary<int, double[]> Pos = new Dictionary<int, double[]>();          // STAAD coordinates
        readonly Dictionary<int, string> JointLabel = new Dictionary<int, string>();
        readonly List<Mem> Frames = new List<Mem>();
        readonly Dictionary<int, Mem> FrameOf = new Dictionary<int, Mem>();
        readonly List<int[]> Trusses = new List<int[]>();
        readonly Dictionary<int, string> TrussLabel = new Dictionary<int, string>();
        readonly Dictionary<int, string> MatOf = new Dictionary<int, string>();             // member / plate -> STAAD material
        readonly Dictionary<int, double> BetaOf = new Dictionary<int, double>();
        readonly Dictionary<string, StaadMaterial> Mats = new Dictionary<string, StaadMaterial>(StringComparer.OrdinalIgnoreCase);
        readonly List<string> MatOrder = new List<string>();                                // STAAD names written as SAP materials
        readonly List<Sec> Secs = new List<Sec>();
        readonly Dictionary<int, StaadSpec> PropOf = new Dictionary<int, StaadSpec>();
        readonly List<string[]> Links = new List<string[]>();                               // label, i, j, prop
        readonly List<string[]> LinkProps = new List<string[]>();                           // name, KE
        readonly Dictionary<int, string> AreaLabel = new Dictionary<int, string>();
        readonly List<string[]> Areas = new List<string[]>();                               // label, section, matprop, joints...
        readonly List<string[]> AreaSecs = new List<string[]>();                            // name, material, type, thickness
        readonly Dictionary<int, bool[]> Restraint = new Dictionary<int, bool[]>();
        readonly Dictionary<int, double[]> Spring = new Dictionary<int, double[]>();
        readonly Dictionary<int, double[]> JointAngles = new Dictionary<int, double[]>();
        readonly HashSet<int> Enforced = new HashSet<int>();
        readonly List<string[]> GroupRows = new List<string[]>();                           // group, type, label
        readonly List<string> GroupOrder = new List<string>();
        readonly List<Pat> Pats = new List<Pat>();
        readonly List<Case> Cases = new List<Case>();
        readonly List<Combo> Combos = new List<Combo>();
        readonly List<Env> Envs = new List<Env>();
        bool PDelta, KeepNumbers;
        bool DistFirst;                                                                    // FrameStd's order: the distributed loads' table first

        public Reader(StaadInput input, string sapVersion, List<string> notes, string library)
        {
            In = input; SapVersion = sapVersion; Notes = notes;
            Library = string.IsNullOrEmpty(library) ? null : library;                       // PowerShell passes "" for $null
        }

        void Refuse(string s) { if (!Refused.Contains(s)) Refused.Add(s); }
        List<string> Carried = new List<string>();           // STAAD's commands after the analysis (StaadCarry)

        // the refusals, those with the same reason as one ("<reason>: member 1, member 2 ..."), long lists cut
        List<string> Grouped()
        {
            var order = new List<string>();
            var by = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (string r in Refused)
            {
                var g = Regex.Match(r, @"^((?:joint|member|plate|group|material) [^:]+): (.*)$");
                string who = g.Success ? g.Groups[1].Value : "", what = g.Success ? g.Groups[2].Value : r;
                List<string> l;
                if (!by.TryGetValue(what, out l)) { by[what] = l = new List<string>(); order.Add(what); }
                l.Add(who);
            }
            return order.Select(w => by[w].Count == 1 ? (by[w][0] == "" ? w : by[w][0] + ": " + w) : w + ": " + string.Join(", ", by[w].Take(12).ToArray()) + (by[w].Count > 12 ? " and " + (by[w].Count - 12).ToString(Inv) + " more" : "")).ToList();
        }
        void Note(string s) { if (!Notes.Contains(s)) Notes.Add(s); }

        public string Run()
        {
            Head();
            Units();
            foreach (var u in In.Unread) Refuse("not read (FrameStd does not write it): " + u);
            // the commands after the analysis are the model's STAAD design (StaadDesign: the Frames rows' design values and
            // the workbook's STAAD sheets; -Std writes it again; SAP takes none); a Pluto file's own prints are -Std's
            Carried = new List<string>();
            if (In.AnalysisPrint != null) Carried.Add("PERFORM ANALYSIS " + In.AnalysisPrint);
            Carried.AddRange(In.Output);
            var own = new[] { "PRINT JOINT DISPLACEMENTS LIST ALL", "PRINT MEMBER FORCES ALL", "PRINT SUPPORT REACTIONS", "PRINT ELEMENT JOINT STRESSES LIST ALL" };
            if (Pluto && Carried.All(c => own.Any(x => Is(Regex.Replace(c, @"\s+", " "), x)))) Carried.Clear();
            foreach (string c in Carried) if (c.IndexOf('"') >= 0) Refuse("a STAAD command with a double quote (the model carries commands in quotes): " + c);
            JointsIn();
            MembersIn();
            ConstantsIn();
            MaterialsIn();
            PropertiesIn();
            AnglesIn();
            OffsetsIn();
            ReleasesIn();
            TrussesIn();
            PlatesIn();
            SupportsIn();
            GroupsIn();
            LoadsIn();
            if (Refused.Count > 0) throw new StdToFrameRefusal(Grouped());
            string text = Emit();
            if (Carried.Count == 0) return text;
            // the design into the model: its first set's parameters onto the frames they name, the rest by list, in the
            // model's labels
            var model = FrameS2k.Read(S2kDoc.ParseText(text));
            var d = StaadDesign.FromCommands(Carried, FrameStd.StaadUnits(model));
            d.Distribute(model, DesignMembers, DesignLabels, v => { int id; string l; return int.TryParse(v, NumberStyles.None, Inv, out id) && JointLabel.TryGetValue(id, out l) ? l : v; });
            d.Store(model);
            foreach (string n in d.Notes) Note(n);
            int withOwn = model.Frames.Count(x => x.Rec(StaadDesign.TMembers) != null);
            Note("STAAD's commands after the analysis (" + Carried.Count.ToString(Inv) + ") read as the model's STAAD design: " + d.Sets.Count.ToString(Inv) + " parameter set(s), " + withOwn.ToString(Inv) +
                 " frame(s) with values of their own (the Frames sheet), " + d.Params.Count.ToString(Inv) + " row(s) by list, " + d.Run.Count.ToString(Inv) + " step(s); -Std writes it after the analysis, SAP takes none");
            return FrameS2k.ToDoc(model).ToText();
        }

        // a STAAD member list (numbers, ranges, _GROUP names) as the model's frames and links
        StaadMembers DesignMembers(string text)
        {
            var o = new StaadMembers();
            var t = (text ?? "").Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (t.Length == 0 || (t.Length == 1 && Is(t[0], "ALL"))) { o.All = true; return o; }
            var missing = new List<string>();
            Func<int, bool> one = id =>
            {
                Mem m; string l;
                if (FrameOf.TryGetValue(id, out m)) { if (!o.Frames.Contains(m.Label)) o.Frames.Add(m.Label); return true; }
                if (TrussLabel.TryGetValue(id, out l)) { if (!o.Others.Contains(l)) o.Others.Add(l); return true; }
                return false;
            };
            for (int k = 0; k < t.Length; k++)
            {
                int a, b, by = 1;
                if (k + 2 < t.Length && Is(t[k + 1], "TO") && int.TryParse(t[k], NumberStyles.None, Inv, out a) && int.TryParse(t[k + 2], NumberStyles.None, Inv, out b))
                {
                    k += 2;
                    if (k + 2 < t.Length && Is(t[k + 1], "BY") && int.TryParse(t[k + 2], NumberStyles.None, Inv, out by) && by > 0) k += 2; else by = 1;
                    int found = 0;
                    for (int n = a; n <= b && n - a < 1000000; n += by) if (one(n)) found++;
                    if (found == 0) missing.Add(I(a) + " TO " + I(b));
                    continue;
                }
                string g = In.Groups.Keys.FirstOrDefault(x => Is(x, t[k]));
                if (g != null)
                {
                    int found = 0;
                    foreach (int id in In.Groups[g]) if (one(id)) found++;
                    if (found == 0) missing.Add(t[k] + " (a group with no member)");
                    continue;
                }
                if (!int.TryParse(t[k], NumberStyles.None, Inv, out a) || !one(a)) missing.Add(t[k]);
            }
            if (missing.Count > 0) o.Error = string.Join(", ", missing.ToArray()) + ": no such group or member in the .std";
            return o;
        }

        // a STAAD member list in the model's labels, for the design's rows by list and its steps: a group by the model's
        // name, a number by its member's label, a range kept where its members' labels are their numbers
        string DesignLabels(string text)
        {
            var t = (text ?? "").Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
            var o = new List<string>();
            Func<int, string> label = id => { Mem m; string l; return FrameOf.TryGetValue(id, out m) ? m.Label : TrussLabel.TryGetValue(id, out l) ? l : null; };
            for (int k = 0; k < t.Length; k++)
            {
                int a, b, by = 1;
                if (k + 2 < t.Length && Is(t[k + 1], "TO") && int.TryParse(t[k], NumberStyles.None, Inv, out a) && int.TryParse(t[k + 2], NumberStyles.None, Inv, out b) && b - a < 1000000)
                {
                    int at = k;
                    k += 2;
                    if (k + 2 < t.Length && Is(t[k + 1], "BY") && int.TryParse(t[k + 2], NumberStyles.None, Inv, out by) && by > 0) k += 2; else by = 1;
                    var labels = new List<string>(); bool same = true;
                    for (int n = a; n <= b; n += by) { string l = label(n); if (l == null) continue; labels.Add(l); if (l != I(n)) same = false; }
                    if (same) o.AddRange(t.Skip(at).Take(k - at + 1)); else o.AddRange(labels);
                    continue;
                }
                string g = In.Groups.Keys.FirstOrDefault(x => Is(x, t[k]));
                if (g != null) { string gl = GroupLabel.ContainsKey(g) ? GroupLabel[g] : g.TrimStart('_'); o.Add(gl.Length > 0 ? gl : g); continue; }
                o.Add(int.TryParse(t[k], NumberStyles.None, Inv, out a) ? label(a) ?? t[k] : t[k]);
            }
            return string.Join(" ", o.ToArray());
        }

        // ---------------- the head: STAAD SPACE, and the comments FrameStd writes

        static List<string> Items(List<string> h, string title, string[] starts)
        {
            var o = new List<string>();
            int k = h.IndexOf(title);
            if (k < 0) return o;
            var titles = new[] { "SAP labels and their STAAD numbers:", "Load cases:", "Notes:" };
            for (k++; k < h.Count && !titles.Contains(h[k]); k++)
            {
                string line = h[k];
                if (o.Count == 0 || starts.Any(s => line.StartsWith(s, StringComparison.Ordinal))) o.Add(line);
                else o[o.Count - 1] += " " + line;
            }
            return o;
        }

        string Label(string kind, int id)
        {
            Dictionary<int, string> d; string s;
            return HeadLabel.TryGetValue(kind, out d) && d.TryGetValue(id, out s) ? s : I(id);
        }

        void Head()
        {
            var pre = In.Preamble;
            if (pre.Count == 0 || !pre[0].StartsWith("STAAD SPACE", StringComparison.OrdinalIgnoreCase))
                Refuse("the file starts " + (pre.Count == 0 ? "with no STAAD command" : pre[0]) + ", not STAAD SPACE (a space frame)");
            foreach (string p in pre.Skip(1)) if (!p.StartsWith("INPUT WIDTH", StringComparison.OrdinalIgnoreCase)) Refuse("not read (FrameStd does not write it): " + p);
            if (In.JobInformation.Count > 0) Note("STAAD's job information is a record only, not carried (" + string.Join("; ", In.JobInformation.ToArray()) + ")");
            var h = In.Header;
            Pluto = h.Count > 0 && h[0].StartsWith("Written by Pluto", StringComparison.Ordinal);
            if (!Pluto) { Note("the file has no Pluto header: labels are STAAD's numbers, names STAAD's, the SAP version " + (string.IsNullOrEmpty(SapVersion) ? "22.0.0" : SapVersion)); return; }
            var first = new StringBuilder();
            for (int k = 0; k < h.Count && !h[k].StartsWith("SAP's Z up", StringComparison.Ordinal); k++) first.Append(h[k]).Append(' ');
            var m = Regex.Match(first.ToString().Trim(), @"SAP2000 (\S+), units (.+)\.$");
            if (m.Success) { HeadVersion = m.Groups[1].Value; HeadUnits = m.Groups[2].Value; }
            var pats = new[] {
                new[] { "joint", @"^joint (.+) = (\d+)$" }, new[] { "frame", @"^frame (.+) = member (\d+)$" }, new[] { "area", @"^area (.+) = plate (\d+)$" },
                new[] { "link", @"^link (.+) = member (\d+) \(truss, EA/L = ([^)]+)\)$" } };
            foreach (string it in Items(h, "SAP labels and their STAAD numbers:", new[] { "joint ", "frame ", "area ", "link ", "group ", "material " }))
            {
                Match g = Regex.Match(it, @"^group (.+) = (_\S+)$");
                if (g.Success) { GroupLabel[g.Groups[2].Value] = g.Groups[1].Value; continue; }
                g = Regex.Match(it, @"^material (.+) = (\S+)$");
                if (g.Success) { MatLabel[g.Groups[2].Value] = g.Groups[1].Value; continue; }
                foreach (var p in pats)
                {
                    g = Regex.Match(it, p[1]);
                    if (!g.Success) continue;
                    int id = int.Parse(g.Groups[2].Value, Inv);
                    Dictionary<int, string> d;
                    if (!HeadLabel.TryGetValue(p[0], out d)) HeadLabel[p[0]] = d = new Dictionary<int, string>();
                    d[id] = g.Groups[1].Value;
                    double ke;
                    if (p[0] == "link" && Num(g.Groups[3].Value, out ke)) HeadKe[id] = ke;
                    break;
                }
            }
            foreach (string it in Items(h, "Load cases:", new[] { "LOAD " }))
            {
                Match g = Regex.Match(it, @"^LOAD (\d+) = SAP case (.+?)( \(P-Delta\))?$");
                if (g.Success) { CaseHead[int.Parse(g.Groups[1].Value, Inv)] = g.Groups[2].Value; continue; }
                g = Regex.Match(it, @"^LOAD (\d+) = SAP combination (.+)$");
                if (g.Success) ComboHead[int.Parse(g.Groups[1].Value, Inv)] = g.Groups[2].Value;
            }
        }

        // the SAP units: the header's when it names STAAD's length and force, else made from UNIT
        void Units()
        {
            var len = LenWords; var force = ForceWords;
            if (In.Units.Count == 0) { Refuse("no UNIT command"); UnitsText = "Kip, in, F"; LenUnit = "in"; Gravity = 386.08858267716535; return; }
            var sets = In.Units.Select(u => string.Join(" ", u).ToUpperInvariant()).Distinct().ToList();
            // later units: the input reader gives every number in the first UNIT's (StaadInput.Converted)
            if (sets.Count > 1 && In.Converted.Count > 0)
                Note("UNIT changes along the file (" + string.Join("; ", sets.ToArray()) + "): the numbers under a later UNIT are converted to the first, " +
                     sets[0] + " (" + string.Join(", ", In.Converted.ToArray()) + ")");
            string l = null, f = null;
            foreach (string w in In.Units[0]) { string x; if (l == null && len.TryGetValue(w, out x)) l = x; else if (f == null && force.TryGetValue(w, out x)) f = x; }
            if (l == null || f == null) { Refuse("UNIT " + string.Join(" ", In.Units[0]) + ": no " + (l == null ? "length" : "force") + " unit SAP has"); l = l ?? "in"; f = f ?? "Kip"; }
            UnitsText = f + ", " + l + ", " + (l == "in" || l == "ft" ? "F" : "C");
            LenUnit = l;
            if (HeadUnits != null)
            {
                string hf, hl, ht;
                FrameUnits.Parse(HeadUnits, out hf, out hl, out ht);
                if (Is(hf, f) && Is(hl, l)) UnitsText = HeadUnits;
                else Note("the header's units (" + HeadUnits + ") are not the file's UNIT (" + string.Join(" ", In.Units[0]) + "): " + UnitsText);
            }
            var g = new Dictionary<string, double> { { "in", 386.08858267716535 }, { "ft", 32.17404855643045 }, { "mm", 9806.65 }, { "cm", 980.665 }, { "m", 9.80665 } };
            Gravity = g[l];
        }

        // ---------------- geometry

        void JointsIn()
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (int id in In.JointIds)
            {
                Pos[id] = In.Joints[id];
                string lab = Label("joint", id);
                if (!used.Add(lab)) { Note("joint " + I(id) + ": the header's label " + lab + " is taken; it keeps its number"); lab = I(id); if (!used.Add(lab)) Refuse("joint " + I(id) + ": label " + lab + " twice"); }
                JointLabel[id] = lab;
            }
        }

        void MembersIn()
        {
            var truss = new HashSet<int>(In.Truss);
            var ids = new HashSet<int>();
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var mi in In.Members)
            {
                if (!ids.Add(mi[0])) { Refuse("member " + I(mi[0]) + ": defined twice"); continue; }
                if (truss.Contains(mi[0])) { Trusses.Add(mi); continue; }
                var m = new Mem { Id = mi[0], I = mi[1], J = mi[2] };
                var d = Sub(Pos[m.J], Pos[m.I]);
                m.L = Len(d);
                if (m.L < 1e-9) { Refuse("member " + I(m.Id) + ": zero length"); continue; }
                m.X = Unit(d);
                m.Label = Label("frame", m.Id);
                if (!used.Add(m.Label)) { Note("member " + I(m.Id) + ": the header's label " + m.Label + " is taken; it keeps its number"); m.Label = I(m.Id); if (!used.Add(m.Label)) Refuse("member " + I(m.Id) + ": label " + m.Label + " twice"); }
                Frames.Add(m);
                FrameOf[m.Id] = m;
            }
            foreach (int t in truss) if (!ids.Contains(t)) Refuse("MEMBER TRUSS: member " + I(t) + " is not in MEMBER INCIDENCES");
            foreach (var p in In.Plates) if (!ids.Add(p[0])) Refuse("plate " + I(p[0]) + ": its number is a member's too");
        }

        // CONSTANTS: MATERIAL name MEMB ...; BETA b MEMB ...
        void ConstantsIn()
        {
            var plates = new HashSet<int>(In.Plates.Select(p => p[0]));
            var all = In.Members.Select(m => m[0]).Concat(plates).ToList();
            foreach (var c in In.Constants)
            {
                var ids = c.All ? all : c.Ids;
                double b;
                if (c.Spec.Length == 2 && Is(c.Spec[0], "MATERIAL"))
                    foreach (int id in ids)
                    {
                        if (MatOf.ContainsKey(id)) { Refuse((plates.Contains(id) ? "plate " : "member ") + I(id) + ": two materials"); continue; }
                        MatOf[id] = c.Spec[1];
                    }
                else if (c.Spec.Length == 2 && Is(c.Spec[0], "BETA") && Num(c.Spec[1], out b))
                    foreach (int id in ids)
                    {
                        if (!FrameOf.ContainsKey(id)) { if (!c.All) Refuse((plates.Contains(id) ? "plate " : "member ") + I(id) + ": a BETA " + (plates.Contains(id) ? "on a plate" : "on a truss member")); continue; }
                        if (BetaOf.ContainsKey(id)) { Refuse("member " + I(id) + ": two BETAs"); continue; }
                        BetaOf[id] = b;
                    }
                else Refuse("CONSTANTS: " + c.Text + " (FrameStd writes MATERIAL and BETA only)");
            }
            foreach (var m in Frames)
            {
                double b; BetaOf.TryGetValue(m.Id, out b);
                m.Beta = b;
                // STAAD's y after BETA, about x (right-handed): SAP's local 2; z its local 3
                var ax = FrameStd.StaadAxes(m.X);
                double r = b * Math.PI / 180;
                m.Y = Add(Mul(ax[1], Math.Cos(r)), Mul(Cross(m.X, ax[1]), Math.Sin(r)));
                m.Z = Cross(m.X, m.Y);
                // SAP's angle: from its default local 2 to that, about local 1
                var l2 = FrameStd.R(FrameSidecar.LocalAxes(Rinv(m.X), 0)[1]);
                var dsap = Rinv(m.X); var mx = m.X;
                m.Angle = Shortest(Snap(Math.Atan2(Dot(Cross(l2, m.Y), m.X), Dot(l2, m.Y)) * 180 / Math.PI), x => FrameStd.Beta(mx, FrameStd.R(FrameSidecar.LocalAxes(dsap, x)[1])), b);
                string mat;
                if (!MatOf.TryGetValue(m.Id, out mat)) Refuse("member " + I(m.Id) + ": no material (CONSTANTS MATERIAL)");
                m.Mat = mat;
            }
        }

        // ---------------- materials

        void MaterialsIn()
        {
            foreach (var m in In.Materials)
            {
                if (Mats.ContainsKey(m.Name)) { Refuse("material " + m.Name + ": defined twice"); continue; }
                Mats[m.Name] = m;
            }
            var trussIds = new HashSet<int>(Trusses.Select(t => t[0]));
            var users = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in MatOf)
            {
                if (!Mats.ContainsKey(kv.Value)) { Refuse("member or plate " + I(kv.Key) + ": material " + kv.Value + " is not defined"); continue; }
                List<int> l; if (!users.TryGetValue(kv.Value, out l)) users[kv.Value] = l = new List<int>(); l.Add(kv.Key);
            }
            foreach (var m in In.Materials)
            {
                List<int> l;
                bool any = users.TryGetValue(m.Name, out l);
                bool onlyTruss = any && l.All(trussIds.Contains);
                if (!any || onlyTruss) { if (!onlyTruss) Note("material " + m.Name + ": no member or plate uses it (left out)"); continue; }
                string who = "material " + m.Name;
                double e, nu, g, v;
                if (!m.Values.TryGetValue("E", out e) || e <= 0) { Refuse(who + ": no E"); continue; }
                bool hasNu = m.Values.TryGetValue("POISSON", out nu), hasG = m.Values.TryGetValue("G", out g);
                if (!hasNu && hasG) nu = e / (2 * g) - 1;
                // to STAAD.Pro's own save's 6 digits (1e-5 of the size): it writes E 4.1771e+06 and G 1.60658e+06, each rounded
                // apart (round 4, 2026-11-24), and a number in E notation reads with no trace of its digits
                if (hasG && !Close(g, e / (2 * (1 + nu))) && Math.Abs(g - e / (2 * (1 + nu))) > 1e-5 * g) Refuse(who + ": G " + FrameStd.N(g) + " apart from E / (2 (1 + POISSON)) = " + FrameStd.N(e / (2 * (1 + nu))) + " (SAP's isotropic material takes G from E and Poisson's ratio)");
                if (nu < 0 || nu >= 0.5) Refuse(who + ": Poisson's ratio " + FrameStd.N(nu));
                if (m.Values.ContainsKey("DAMP")) Note(who + ": its DAMP is not carried (static cases only)");
                bool steel = m.Type != null && Is(m.Type, "STEEL");
                if (m.Type != null && !steel)
                    Note(who + ": TYPE " + m.Type + " read as SAP's material type Other (its analysis numbers kept" + (m.Strength.Count > 0 ? "; its STRENGTH " + string.Join(" ", m.Strength.Keys.ToArray()) + ", design data, not carried" : "") + ")");
                if (steel) foreach (string k in m.Strength.Keys) if (!new[] { "FY", "FU", "RY", "RT" }.Any(x => Is(k, x))) Refuse(who + ": STRENGTH " + k + " (FrameStd writes FY FU RY RT)");
                if (m.Strength.Count > 0 && m.Type == null) Refuse(who + ": a STRENGTH without TYPE STEEL");
                if (m.Values.TryGetValue("DENSITY", out v) && v < 0) Refuse(who + ": a negative DENSITY");
                MatOrder.Add(m.Name);
            }
        }

        string SapMat(string staad) { string s; return MatLabel.TryGetValue(staad, out s) ? s : staad; }
        double Val(string staad, string key) { StaadMaterial m; double v; return Mats.TryGetValue(staad, out m) && m.Values.TryGetValue(key, out v) ? v : 0; }

        // ---------------- member properties: sections

        static string SectionName(List<string> comments, string prefix, out string row)
        {
            row = null;
            int k = comments.FindIndex(c => c.StartsWith(prefix, StringComparison.Ordinal));
            if (k < 0) return null;
            string text = string.Join(" ", comments.Skip(k).ToArray()).Substring(prefix.Length).Trim();
            int colon = text.IndexOf(": ", StringComparison.Ordinal);
            if (colon < 0) return text;
            row = text.Substring(colon + 2).Trim();
            return text.Substring(0, colon).Trim();
        }

        // PRIS AX .. IX .. IY .. IZ .. [AY ..] [AZ ..] [YD ..] [ZD ..] -> AX IX IY IZ AY AZ YD ZD (NaN: not given). STAAD's
        // short forms are its own sections, whose numbers STAAD works out (its PRINT MEMBER PROPERTIES, 2026-11-24):
        //   PRIS YD d ZD b   a rectangle: AX = d b, IZ = b d^3 / 12, IY = d b^3 / 12, IX by the rectangle series (SAP's
        //                    too: 593.26 for 12 x 6, 159.69 for 4 x 10), AY = AZ = 0.85 AX (SAP's rectangle takes 5/6)
        //   PRIS YD d        a circle: AX = pi d^2 / 4, IZ = IY = pi d^4 / 64, IX = 2 IZ, AY = AZ = 0.89 AX (SAP's: 0.9)
        // and a full PRIS with YD but no ZD is round: ZD = YD. YB / ZB (tees, trapezoids) are not read
        internal static double[] Pris(string[] spec, out string why)
        {
            var p = PrisWritten(spec, out why);
            if (p == null) return null;
            bool full = !double.IsNaN(p[0]) || !double.IsNaN(p[1]) || !double.IsNaN(p[2]) || !double.IsNaN(p[3]) || !double.IsNaN(p[4]) || !double.IsNaN(p[5]);
            double d = p[6], b = p[7];
            if (!full && !double.IsNaN(d) && d > 0)
            {
                if (!double.IsNaN(b) && b > 0)
                {
                    double ax = d * b, t = Math.Min(d, b), a = Math.Max(d, b);
                    return new[] { ax, a * t * t * t * (1.0 / 3 - 0.21 * (t / a) * (1 - Math.Pow(t / a, 4) / 12)), d * b * b * b / 12, b * d * d * d / 12, ax * 85 / 100, ax * 85 / 100, d, b };
                }
                double ac = Math.PI * d * d / 4, ic = Math.PI * Math.Pow(d, 4) / 64;
                return new[] { ac, 2 * ic, ic, ic, ac * 89 / 100, ac * 89 / 100, d, d };
            }
            if (full && !double.IsNaN(d) && double.IsNaN(b)) p[7] = d;
            return p;
        }

        static double[] PrisWritten(string[] spec, out string why)
        {
            why = null;
            string[] keys = { "AX", "IX", "IY", "IZ", "AY", "AZ", "YD", "ZD" };
            var p = Enumerable.Repeat(double.NaN, 8).ToArray();
            if (spec.Length % 2 != 1) { why = "its numbers do not pair with their names"; return null; }
            for (int k = 1; k + 1 < spec.Length; k += 2)
            {
                int i = Array.FindIndex(keys, x => Is(spec[k], x));
                double v;
                if (i < 0) { why = "PRIS " + spec[k] + " (FrameStd writes AX IX IY IZ AY AZ YD ZD)"; return null; }
                if (!Num(spec[k + 1], out v) || !double.IsNaN(p[i])) { why = "PRIS " + spec[k] + " " + spec[k + 1]; return null; }
                p[i] = v;
            }
            return p;
        }

        // a SAP row's analysis properties, as PRIS's (AX IX IY IZ AY AZ YD ZD)
        static double[] RowProps(string row)
        {
            var r = new S2kRow();
            S2kRow.Tokenize(row, r);
            Func<string, double> get = k => { int i = r.Keys.FindIndex(x => Is(x, k)); double v; return i >= 0 && Num(r.Values[i], out v) ? v : 0; };
            return new[] { get("Area"), get("TorsConst"), get("I22"), get("I33"), get("AS2"), get("AS3"), get("t3"), get("t2") };
        }

        static string RowGet(string row, string key)
        {
            var r = new S2kRow();
            S2kRow.Tokenize(row, r);
            int i = r.Keys.FindIndex(x => Is(x, key));
            return i < 0 ? null : r.Values[i];
        }

        void PropertiesIn()
        {
            foreach (var p in In.MemberProperties)
                foreach (int id in p.Ids)
                {
                    if (PropOf.ContainsKey(id)) { Refuse("member " + I(id) + ": two member properties"); continue; }
                    PropOf[id] = p;
                }
            var byName = new Dictionary<string, List<Sec>>(StringComparer.OrdinalIgnoreCase);
            var bySpec = new Dictionary<string, Sec>(StringComparer.Ordinal);
            int unnamed = 0;
            // table sections first: a frame of one with modifiers (written PRIS) then reads as factors on it
            foreach (var m in Frames.Where(f => PropOf.ContainsKey(f.Id) && Is(PropOf[f.Id].Spec[0], "TABLE")).Concat(Frames.Where(f => !PropOf.ContainsKey(f.Id) || !Is(PropOf[f.Id].Spec[0], "TABLE"))))
            {
                StaadSpec p;
                string who = "member " + I(m.Id);
                if (!PropOf.TryGetValue(m.Id, out p)) { Refuse(who + ": no MEMBER PROPERTY"); continue; }
                m.Prop = p;
                string row, name = SectionName(p.Comments, "SAP section ", out row), why;
                double[] props;
                string shape = "General";
                if (Is(p.Spec[0], "PRIS"))
                {
                    props = Pris(p.Spec, out why);
                    if (props == null) { Refuse(who + ": " + why); continue; }
                    if (!p.Spec.Any(x => Is(x, "AX")))
                        Note("PRIS " + string.Join(" ", p.Spec.Skip(1).ToArray()) + ": STAAD's own " + (p.Spec.Length == 3 ? "circle" : "rectangle") + ", as a SAP general section with the numbers STAAD works out (shear areas " + (p.Spec.Length == 3 ? "0.89" : "0.85") + " A, which SAP's " + (p.Spec.Length == 3 ? "Circle (0.9 A)" : "Rectangular (5/6 A)") + " would not give)");
                    if (Enumerable.Range(0, 4).Any(i => double.IsNaN(props[i]) || props[i] <= 0)) { Refuse(who + ": PRIS needs AX IX IY IZ, each positive"); continue; }
                    if (double.IsNaN(props[6]) || double.IsNaN(props[7]) || props[6] <= 0 || props[7] <= 0) { Refuse(who + ": PRIS without YD and ZD (SAP's general section needs its depth and width)"); continue; }
                    row = null;
                }
                else if (Is(p.Spec[0], "TABLE"))
                {
                    string tw, what = string.Join(" ", p.Spec);
                    var t = TableOf(p.Table, what, out tw);
                    if (t == null) { Refuse(who + ": MEMBER PROPERTY " + what + ": " + tw); continue; }
                    string inFile = row == null ? null : RowGet(row, "SectInFile");
                    // the comment's SAP row is this property's when -Std writes this property from it, or names the same
                    // shape (HSS24X12X3/4 for STAAD's HSS24X12X.750, when SAP's library file is not here to say so)
                    if (row != null && !Same(t, row) && !(t[0] == "ST" && inFile != null && FrameSapLibrary.SameShape(inFile, t[1])))
                    {
                        // the shape changed in STAAD under the comment of the one Pluto wrote: the comment is not its row
                        Note(what + " under the SAP row of " + (inFile ?? name ?? "another section") + ": taken from SAP's section library");
                        row = null; name = null;
                    }
                    if (row == null)
                    {
                        string[] lib = Catalog(t, what);
                        if (lib[0] == null) { Refuse(who + ": " + what + ", a STAAD table section with no SAP row in the comment above it: " + lib[1]); continue; }
                        row = lib[0]; name = null;
                    }
                    else row = LocalFile(row, what);
                    shape = RowGet(row, "Shape");
                    if (string.IsNullOrEmpty(shape)) { Refuse(who + ": the SAP row of " + what + " has no Shape"); continue; }
                    props = RowProps(row);
                    var sr = new S2kRow();
                    S2kRow.Tokenize(row, sr);
                    row = sr.Render().TrimStart();
                }
                else { Refuse(who + ": MEMBER PROPERTY " + string.Join(" ", p.Spec) + " (FrameStd writes PRIS and TABLE)"); continue; }
                m.Depth = props[6]; m.Width = props[7]; m.Ax = props[0];
                string key = (name ?? "") + "\n" + string.Join(" ", p.Spec);
                if (name == null && !bySpec.ContainsKey(key)) name = row != null ? RowGet(row, "SectInFile") ?? "TABLE" + I(++unnamed) : "PRIS" + I(++unnamed);
                Sec s;
                double[] mods;
                if (bySpec.TryGetValue(key, out s)) { m.Section = s.Name; m.Mods = Factors(s, props, row != null, out mods) ? mods : null; continue; }
                List<Sec> same;
                if (!byName.TryGetValue(name, out same)) byName[name] = same = new List<Sec>();
                // the same SAP section with other numbers: its frames' modifiers, when they read as factors on the first
                Sec baseSec = same.Count > 0 ? same[0] : null;
                if (baseSec != null && Factors(baseSec, props, row != null, out mods)) { bySpec[key] = baseSec; m.Section = baseSec.Name; m.Mods = mods; continue; }
                string nm = name; int n = 2;
                while (Secs.Any(x => Is(x.Name, nm))) nm = name + "_" + I(n++);
                s = new Sec { Name = nm, Mat = m.Mat, Shape = shape, Row = row, P = props };
                Secs.Add(s); same.Add(s); bySpec[key] = s;
                m.Section = s.Name;
                m.Mods = null;
            }
            var modded = Frames.Where(f => f.Mods != null).ToList();
            if (modded.Count > 0) Note("frames whose property is their section's with factors take it as property modifiers (FrameStd folds a frame's modifiers into its PRIS): " + string.Join(", ", modded.Select(f => f.Label).Take(12).ToArray()) + (modded.Count > 12 ? " ..." : ""));
        }

        // a STAAD table shape's SAP row from SAP's section library: { row, null } or { null, why }. t: TableOf's
        // { type, name, spacing }: ST the shape (by SAP's label or EDI name), T the tee cut from a W (SAP's WT), LD / SD the
        // double angle of that angle (SAP's 2L)
        string[] Catalog(string[] t, string what)
        {
            string[] r;
            string key = string.Join("|", t).ToUpperInvariant();
            if (LibRow.TryGetValue(key, out r)) return r;
            if (Library == null) Library = FrameSapLibrary.Folder(!string.IsNullOrEmpty(SapVersion) ? SapVersion : HeadVersion) ?? "";
            if (Library.Length == 0) r = new[] { null, "SAP's section library is not on this machine (name its folder with -SapLibrary), or give the shape as PRIS" };
            else
            {
                string file, why, row;
                double sp;
                Num(t[2], out sp);
                if (t[0] == "T") row = FrameSapLibrary.TeeRow(Library, t[1], LenUnit, out file, out why);
                else if (t[0] == "LD" || t[0] == "SD") row = FrameSapLibrary.DoubleAngleRow(Library, t[1], t[0] == "LD", sp, LenUnit, out file, out why);
                else row = FrameSapLibrary.Row(Library, t[1], LenUnit, out file, out why);
                r = new[] { row, why };
                if (row != null)
                {
                    string kind = RowGet(row, "Shape"), inFile = RowGet(row, "SectInFile");
                    Note(what + ": a catalog section from SAP's section library, " + inFile + " (" + kind + ") in " + file + " (no SAP row of it in the file)");
                    if (Is(kind, "Angle"))
                        Note(what + ": SAP works out an angle's I23 itself; Pluto's (its legs and root fillet, 0.18 % above SAP's on L3X2-1/2X3/16) sets the frame's orientation within about 0.02 deg");
                    if (Is(kind, "Channel") || Is(kind, "Tee") || Is(kind, "Double Angle"))
                        Note(what + ": SAP2000 twists a " + kind.ToLowerInvariant() + " about its shear center; STAAD's member is centroidal (the probe: up to 0.03 in on a 70 in channel cantilever)");
                    var back = FrameStd.StaadTable(k => RowGet(row, k));
                    if (back == null)
                        Note(what + ": -Std writes it back as PRIS, SAP's numbers (STAAD's form for it is not seen), so the read-back check lists it");
                    else if (!Same(t, row))
                        Note(what + ": -Std writes it back as " + back[1] + " (MEMBER PROPERTY " + back[0] + "), so the read-back check lists it");
                }
            }
            return LibRow[key] = r;
        }

        // a comment's catalog row whose library file is not on this machine: SAP's own library's file of that name, as -S2k
        // does (FrameSapLibrary.Check), so -Std can name the shape again (an HSS's STAAD name is in the file); with none,
        // a notice that -Std writes it back as numbers here
        string LocalFile(string row, string what)
        {
            string fn = (RowGet(row, "FileName") ?? "").Trim();
            if (fn.Length == 0 || !Is(RowGet(row, "FromFile") ?? "", "Yes") || System.IO.File.Exists(fn)) return row;
            if (Library == null) Library = FrameSapLibrary.Folder(!string.IsNullOrEmpty(SapVersion) ? SapVersion : HeadVersion) ?? "";
            string file = fn.Substring(fn.LastIndexOfAny(new[] { '\\', '/' }) + 1);
            string local = Library.Length == 0 || file.Length == 0 ? null : System.IO.Directory.GetFiles(Library).FirstOrDefault(f => Is(System.IO.Path.GetFileName(f), file));
            if (local == null)
            {
                if (FrameStd.StaadTable(k => RowGet(row, k)) == null)
                    Note(what + ": its SAP row's library file " + fn + " is not on this machine, so -Std writes it back as PRIS here (it takes STAAD's name from the file); the read-back check lists it");
                return row;
            }
            var r = new S2kRow();
            S2kRow.Tokenize(row, r);
            r.Set("FileName", local);
            return r.Render().TrimStart();
        }

        // whether -Std writes this table property (TableOf's) from this SAP row: the same shape, by type and name
        static bool Same(string[] t, string row)
        {
            var back = FrameStd.StaadTable(k => RowGet(row, k));
            if (back == null) return false;
            string why;
            var b = TableOf(back[0], back[1], out why);
            double x, y;
            return b != null && b[0] == t[0] && Is(b[1], t[1]) && Num(b[2], out x) && Num(t[2], out y) && x == y;
        }

        // single angles: STAAD bends a TABLE ... ST angle about its principal axes (its y the minor one), SAP about its legs
        // with I23 (the angle probe, 2026-11-24). SAP's local 2 is STAAD's y turned back by FrameStd.AngleTurn, from the
        // SAP row's I33, I22 and I23 (SAP's own from a SAP export; Pluto's estimate for a shape picked in STAAD)
        void AnglesIn()
        {
            var turned = new List<string>();
            foreach (var m in Frames)
            {
                var s = m.Section == null ? null : Secs.FirstOrDefault(x => x.Name == m.Section);
                if (s == null || s.Row == null || !Is((s.Shape ?? "").Trim(), "Angle")) continue;
                double i33, i22, i23;
                if (!Num(RowGet(s.Row, "I33") ?? "", out i33) || !Num(RowGet(s.Row, "I22") ?? "", out i22) || !Num(RowGet(s.Row, "I23") ?? "", out i23) || i23 == 0)
                { Refuse("member " + I(m.Id) + ": angle " + s.Name + " with no I33, I22 and I23 in its SAP row"); continue; }
                double d = FrameStd.AngleTurn(i33, i22, i23), b = m.Beta;
                m.Delta = d;
                var ax = FrameStd.StaadAxes(m.X);
                double r = (b - d) * Math.PI / 180;
                m.Y = Add(Mul(ax[1], Math.Cos(r)), Mul(Cross(m.X, ax[1]), Math.Sin(r)));
                m.Z = Cross(m.X, m.Y);
                var l2 = FrameStd.R(FrameSidecar.LocalAxes(Rinv(m.X), 0)[1]);
                var dsap = Rinv(m.X); var mx = m.X;
                m.Angle = Shortest(Snap(Math.Atan2(Dot(Cross(l2, m.Y), m.X), Dot(l2, m.Y)) * 180 / Math.PI), x => FrameStd.NormBeta(FrameStd.Beta(mx, FrameStd.R(FrameSidecar.LocalAxes(dsap, x)[1])) + d), b);
                turned.Add(m.Label + " (" + s.Name + ", " + FrameStd.N(d) + ")");
            }
            if (turned.Count > 0)
                Note("single angles: STAAD bends a table angle about its principal axes, SAP about its legs with I23; each frame's SAP angle is its BETA less the turn to those axes (the angle probe, to 0.007 deg): " + string.Join(", ", turned.Take(12).ToArray()) + (turned.Count > 12 ? " ..." : ""));
        }

        // whether these numbers are the section's with factors: the property modifiers (AMod AS2Mod AS3Mod JMod I22Mod I33Mod
        // MassMod WeightMod; the weight and mass go with the area, as STAAD's DENSITY x AX does), mods null when all are 1.
        // A table section's numbers are its own: no factors
        static bool Factors(Sec s, double[] p, bool table, out double[] mods)
        {
            mods = null;
            if (table) return s.Row != null && s.P.SequenceEqual(p);
            var b = s.P;
            Func<double, double> nz = x => double.IsNaN(x) ? 0 : x;
            if (nz(p[6]) != nz(b[6]) || nz(p[7]) != nz(b[7])) return false;
            if ((nz(p[4]) > 0) != (nz(b[4]) > 0) || (nz(p[5]) > 0) != (nz(b[5]) > 0)) return false;
            Func<int, double> f = i => nz(b[i]) > 0 ? Shortest(nz(p[i]) / nz(b[i]), x => nz(b[i]) * x, nz(p[i])) : 1;
            double a = f(0);
            var o = new[] { a, f(4), f(5), f(1), f(2), f(3), a, a };
            if (o.Any(x => !(x > 0))) return false;
            if (!o.All(x => x == 1)) mods = o;
            return true;
        }

        // ---------------- offsets, releases

        void OffsetsIn()
        {
            foreach (var o in In.Offsets)
                foreach (int id in o.Ids)
                {
                    Mem m;
                    string who = "member " + I(id);
                    if (!FrameOf.TryGetValue(id, out m)) { Refuse(who + ": a MEMBER OFFSET on " + (Trusses.Any(q => q[0] == id) ? "a truss member" : "no member")); continue; }
                    double x, y, z;
                    if (o.Spec.Length != 4 || !(Is(o.Spec[0], "START") || Is(o.Spec[0], "END")) || !Num(o.Spec[1], out x) || !Num(o.Spec[2], out y) || !Num(o.Spec[3], out z))
                    { Refuse(who + ": MEMBER OFFSET " + string.Join(" ", o.Spec) + " (FrameStd writes START / END x y z, global)"); continue; }
                    bool start = Is(o.Spec[0], "START");
                    if ((start ? m.OffI : m.OffJ) != null) { Refuse(who + ": two " + o.Spec[0] + " offsets"); continue; }
                    if (start) m.OffI = new[] { x, y, z }; else m.OffJ = new[] { x, y, z };
                }
            foreach (var m in Frames)
            {
                if (m.OffI == null && m.OffJ == null) continue;
                string who = "member " + I(m.Id);
                var oi = m.OffI ?? new double[3]; var oj = m.OffJ ?? new double[3];
                double a = Dot(oi, m.X), b = -Dot(oj, m.X);
                var ti = Sub(oi, Mul(m.X, a)); var tj = Add(oj, Mul(m.X, b));
                // to the digits the offsets are written with (STAAD.Pro's own save keeps 6)
                double tol = 1e-9 * m.L + 2 * oi.Concat(oj).Select(c => Math.Min(HalfUnit(c), 1e-5 * m.L)).Max();
                if (Len(Sub(ti, tj)) > tol) { Refuse(who + ": offsets across it that differ at its ends (SAP's joint offsets are read for a member parallel to its joints' line)"); continue; }
                if (Math.Abs(a) <= tol) a = 0;
                if (Math.Abs(b) <= tol) b = 0;
                if (a < 0 || b < 0 || a + b >= m.L) { Refuse(who + ": offsets along it of " + FrameStd.N(a) + " and " + FrameStd.N(b) + " (SAP's end lengths lie inside the member)"); continue; }
                m.ZoneI = a; m.ZoneJ = b; m.Trans = ti;
            }
        }

        void ReleasesIn()
        {
            string[] dof = { "FX", "FY", "FZ", "MX", "MY", "MZ" };
            foreach (var r in In.Releases)
                foreach (int id in r.Ids)
                {
                    Mem m;
                    string who = "member " + I(id);
                    if (!FrameOf.TryGetValue(id, out m)) { Refuse(who + ": a MEMBER RELEASE on " + (Trusses.Any(q => q[0] == id) ? "a truss member" : "no member")); continue; }
                    var t = r.Spec;
                    bool s = Is(t[0], "START") || Is(t[0], "BOTH"), e = Is(t[0], "END") || Is(t[0], "BOTH");
                    if (!s && !e) { Refuse(who + ": MEMBER RELEASE " + string.Join(" ", t)); continue; }
                    var rel = new bool[6]; var pf = new double[6];
                    bool ok = t.Length > 1;
                    for (int k = 1; ok && k < t.Length; k++)
                    {
                        int i = Array.FindIndex(dof, x => Is(t[k], x));
                        if (i >= 0) { ok = !rel[i]; rel[i] = true; continue; }
                        i = Array.FindIndex(dof, x => Is(t[k], "K" + x));
                        double v;
                        if (i < 0 || k + 1 >= t.Length || !Num(t[k + 1], out v) || v <= 0 || rel[i]) { ok = false; break; }
                        rel[i] = true; pf[i] = i < 3 ? v : Shortest(v * 180 / Math.PI, x => x * Math.PI / 180, v);
                        k++;
                    }
                    if (!ok) { Refuse(who + ": MEMBER RELEASE " + string.Join(" ", t) + " (FrameStd writes FX .. MZ and KFX v .. KMZ v)"); continue; }
                    if (m.Delta != 0 && (rel[1] != rel[2] || pf[1] != pf[2] || rel[4] != rel[5] || pf[4] != pf[5]))
                    { Refuse(who + ": MEMBER RELEASE " + string.Join(" ", t) + " on an angle, not the same about y and z (STAAD's y / z are its principal axes, SAP's 2 / 3 its legs)"); continue; }
                    for (int end = 0; end < 2; end++)
                    {
                        if (end == 0 ? !s : !e) continue;
                        if (Enumerable.Range(0, 6).Any(i => m.Rel[end * 6 + i])) { Refuse(who + ": released twice at its " + (end == 0 ? "start" : "end")); continue; }
                        for (int i = 0; i < 6; i++) { m.Rel[end * 6 + i] = rel[i]; m.Pf[end * 6 + i] = pf[i]; }
                        m.HasRel = true;
                    }
                }
        }

        // ---------------- trusses: linear axial links

        void TrussesIn()
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            var props = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var t in Trusses)
            {
                string who = "member " + I(t[0]) + " (truss)";
                StaadSpec p; string mat;
                if (!PropOf.TryGetValue(t[0], out p)) { Refuse(who + ": no MEMBER PROPERTY"); continue; }
                string why;
                double[] pr = Is(p.Spec[0], "PRIS") ? Pris(p.Spec, out why) : null;
                if (pr == null || double.IsNaN(pr[0]) || pr[0] <= 0) { Refuse(who + ": a property other than PRIS AX (FrameStd writes the area only)"); continue; }
                if (!MatOf.TryGetValue(t[0], out mat) || !Mats.ContainsKey(mat)) { Refuse(who + ": no material"); continue; }
                double e = Val(mat, "E"), l = Len(Sub(Pos[t[2]], Pos[t[1]]));
                if (l < 1e-9) { Refuse(who + ": zero length"); continue; }
                if (Val(mat, "DENSITY") != 0 && In.Loads.Any(x => x.Blocks.Any(b => b.Name == "SELFWEIGHT"))) Refuse(who + ": its material " + mat + " has a DENSITY under SELFWEIGHT (SAP's links carry no weight)");
                double ke = e * pr[0] / l, hk;
                if (HeadKe.TryGetValue(t[0], out hk) && Close(pr[0], hk * l / e)) ke = hk;
                string kt = F(ke), name;
                if (!props.TryGetValue(kt, out name)) { name = "TRUSS" + I(props.Count + 1); props[kt] = name; LinkProps.Add(new[] { name, kt }); }
                string lab = Label("link", t[0]);
                if (!used.Add(lab)) { lab = I(t[0]); if (!used.Add(lab)) { Refuse(who + ": label " + lab + " twice"); continue; } }
                TrussLabel[t[0]] = lab;
                Links.Add(new[] { lab, JointLabel[t[1]], JointLabel[t[2]], name });
            }
            if (Links.Count > 0) Note("truss members as SAP's linear links, U1 = E AX / L (" + Links.Count.ToString(Inv) + ")");
        }

        // ---------------- plates

        void PlatesIn()
        {
            var plane = new HashSet<int>(In.PlaneStress);
            var specOf = new Dictionary<int, StaadSpec>();
            foreach (var p in In.ElementProperties)
            {
                double t;
                if (p.Spec.Length != 2 || !Is(p.Spec[0], "THICKNESS") || !Num(p.Spec[1], out t)) { Refuse("ELEMENT PROPERTY: " + p.Text + " (FrameStd writes THICKNESS t)"); continue; }
                foreach (int id in p.Ids) { if (specOf.ContainsKey(id)) Refuse("plate " + I(id) + ": two thicknesses"); specOf[id] = p; }
            }
            foreach (int id in plane) if (!In.Plates.Any(p => p[0] == id)) Refuse("ELEMENT PLANE STRESS: plate " + I(id) + " is not in ELEMENT INCIDENCES SHELL");
            var secOf = new Dictionary<string, string>(StringComparer.Ordinal);
            var used = new HashSet<string>(StringComparer.Ordinal);
            int unnamed = 0;
            foreach (var p in In.Plates)
            {
                int id = p[0];
                string who = "plate " + I(id), mat;
                StaadSpec ts;
                double t;
                if (!specOf.TryGetValue(id, out ts) || !In.Thickness.TryGetValue(id, out t) || t <= 0) { Refuse(who + ": no THICKNESS"); continue; }
                if (!MatOf.TryGetValue(id, out mat) || !Mats.ContainsKey(mat)) { Refuse(who + ": no material"); continue; }
                string row, name = SectionName(ts.Comments, "SAP area section ", out row) ?? ("PLATE" + I(++unnamed));
                string type = plane.Contains(id) ? "Membrane" : "Shell-Thin";
                string key = name + "\n" + FrameStd.N(t) + "\n" + type, sec;
                if (!secOf.TryGetValue(key, out sec))
                {
                    sec = name; int n = 2;
                    while (AreaSecs.Any(x => Is(x[0], sec))) sec = name + "_" + I(n++);
                    secOf[key] = sec;
                    AreaSecs.Add(new[] { sec, mat, type, F(t) });
                }
                string secMat = AreaSecs.First(x => x[0] == sec)[1];
                string lab = Label("area", id);
                if (!used.Add(lab)) { lab = I(id); if (!used.Add(lab)) { Refuse(who + ": label " + lab + " twice"); continue; } }
                AreaLabel[id] = lab;
                var row2 = new List<string> { lab, sec, Is(secMat, mat) ? "Default" : SapMat(mat) };
                row2.AddRange(p.Skip(1).Select(j => JointLabel[j]));
                Areas.Add(row2.ToArray());
            }
            if (Areas.Count > 0) Note("plates as SAP's areas: " + string.Join(", ", AreaSecs.Select(x => x[0] + " (" + x[2] + ", " + FrameStd.N(double.Parse(x[3], Inv)) + ")").ToArray()));
        }

        // ---------------- supports

        void SupportsIn()
        {
            string[] dof = { "FX", "FY", "FZ", "MX", "MY", "MZ" };
            int[] global = { 0, 2, 1, 3, 5, 4 };              // STAAD FX FY FZ MX MY MZ -> SAP U1 U3 U2 R1 R3 R2 (global)
            foreach (var s in In.Supports)
                foreach (int id in s.Ids)
                {
                    string who = "joint " + I(id);
                    if (!Pos.ContainsKey(id)) { Refuse(who + ": a support on a joint not defined"); continue; }
                    if (Restraint.ContainsKey(id) || Spring.ContainsKey(id)) { Refuse(who + ": two supports"); continue; }
                    var t = s.Spec;
                    int k = 0;
                    double[][] axes = null;
                    if (Is(t[0], "INCLINED"))
                    {
                        double a, b, c;
                        if (t.Length < 5 || !Num(t[1], out a) || !Num(t[2], out b) || !Num(t[3], out c) || Len(new[] { a, b, c }) < 1e-12) { Refuse(who + ": SUPPORTS " + string.Join(" ", t)); continue; }
                        axes = FrameStd.StaadAxes(Unit(new[] { a, b, c }));
                        k = 4;
                    }
                    // fixed (true), free, or a spring in each STAAD DOF
                    var fix = new bool[6]; var spr = new double[6]; var released = new bool[6];
                    bool ok = true, enforced = false;
                    string kind = t[k].ToUpperInvariant();
                    if (kind == "FIXED" || kind == "ENFORCED") { for (int i = 0; i < 6; i++) fix[i] = true; enforced = kind == "ENFORCED"; }
                    else if (kind == "PINNED") { for (int i = 0; i < 3; i++) fix[i] = true; }
                    else ok = false;
                    k++;
                    if (ok && k < t.Length)
                    {
                        if (!Is(t[k], "BUT") || kind == "PINNED") ok = false;
                        for (k++; ok && k < t.Length; k++)
                        {
                            // a DOF released, or given a spring (KMX v): on a DOF the list also releases, before or after
                            // it, the spring stands (STAAD's FIXED BUT MX KMX v, as FIXED BUT KMX v)
                            int i = Array.FindIndex(dof, x => Is(t[k], x));
                            if (i >= 0 && fix[i]) { fix[i] = false; released[i] = true; continue; }
                            if (i >= 0 && spr[i] != 0) continue;
                            i = Array.FindIndex(dof, x => Is(t[k], "K" + x));
                            double v;
                            if (enforced || i < 0 || !(fix[i] || released[i]) || spr[i] != 0 || k + 1 >= t.Length || !Num(t[k + 1], out v) || v <= 0) { ok = false; break; }
                            fix[i] = false; spr[i] = i < 3 ? v : Shortest(v * 180 / Math.PI, x => x * Math.PI / 180, v);
                            k++;
                        }
                    }
                    if (!ok) { Refuse(who + ": SUPPORTS " + string.Join(" ", t) + " (FrameStd writes FIXED, PINNED, ENFORCED, BUT with FX .. MZ and KFX v .. KMZ v, INCLINED)"); continue; }
                    if (enforced && axes != null) { Refuse(who + ": an INCLINED ENFORCED support (FrameStd writes ENFORCED in global axes)"); continue; }
                    var res = new bool[6]; var ks = new double[6];
                    for (int i = 0; i < 6; i++) { int g = axes == null ? global[i] : i; res[g] = fix[i]; ks[g] = spr[i]; }
                    if (res.Any(x => x)) Restraint[id] = res;
                    if (ks.Any(x => x != 0)) Spring[id] = ks;
                    if (axes != null) JointAngles[id] = Euler(Rinv(axes[0]), Rinv(axes[1]), Rinv(axes[2]));
                    if (enforced) Enforced.Add(id);
                }
        }

        // ---------------- groups

        void GroupsIn()
        {
            var seqs = new Dictionary<string, List<string>>();
            var members = new Dictionary<string, List<string[]>>(StringComparer.Ordinal);
            foreach (string g in In.GroupOrder)
            {
                string kind; In.GroupKinds.TryGetValue(g, out kind);
                string label = GroupLabel.ContainsKey(g) ? GroupLabel[g] : g.TrimStart('_');
                if (label.Length == 0) label = g;
                string who = "group " + g;
                // STAAD's GEOMETRY groups hold members and plates, its FLOOR groups members: SAP's groups take both
                if (kind == "GEOMETRY" || kind == "FLOOR") { Note(who + ": a " + kind + " group, read as a group of its members" + (kind == "GEOMETRY" ? " and plates" : "") + " (-Std writes it as a MEMBER or ELEMENT group)"); }
                else if (kind != "JOINT" && kind != "MEMBER" && kind != "ELEMENT") { Refuse(who + ": a " + (kind ?? "(no kind)") + " group (FrameStd writes JOINT, MEMBER and ELEMENT groups)"); continue; }
                List<string[]> rows;
                if (!members.TryGetValue(label, out rows)) members[label] = rows = new List<string[]>();
                foreach (int id in In.Groups[g])
                {
                    Mem m; string l;
                    if (kind == "JOINT" && JointLabel.TryGetValue(id, out l)) rows.Add(new[] { "Joint", l });
                    else if ((kind == "MEMBER" || kind == "GEOMETRY" || kind == "FLOOR") && FrameOf.TryGetValue(id, out m)) rows.Add(new[] { "Frame", m.Label });
                    else if ((kind == "MEMBER" || kind == "GEOMETRY" || kind == "FLOOR") && TrussLabel.TryGetValue(id, out l)) rows.Add(new[] { "Link", l });
                    else if ((kind == "ELEMENT" || kind == "GEOMETRY") && AreaLabel.TryGetValue(id, out l)) rows.Add(new[] { "Area", l });
                    else Refuse(who + ": " + kind.ToLowerInvariant() + " " + I(id) + " is not in the model");
                }
                List<string> seq;
                if (!seqs.TryGetValue(kind, out seq)) seqs[kind] = seq = new List<string>();
                if (!seq.Contains(label)) seq.Add(label);
            }
            // SAP's order: each kind's groups keep their order (FrameStd writes them by kind, each in SAP's order)
            foreach (string kind in new[] { "JOINT", "MEMBER", "ELEMENT", "GEOMETRY", "FLOOR" })
            {
                List<string> seq;
                if (!seqs.TryGetValue(kind, out seq)) continue;
                for (int k = 0; k < seq.Count; k++)
                {
                    if (GroupOrder.Contains(seq[k])) continue;
                    int at = GroupOrder.Count;
                    for (int q = k + 1; q < seq.Count; q++) { int p = GroupOrder.IndexOf(seq[q]); if (p >= 0) { at = p; break; } }
                    GroupOrder.Insert(at, seq[k]);
                }
            }
            foreach (string g in GroupOrder) foreach (var r in members[g]) if (!GroupRows.Any(x => x[0] == g && x[1] == r[0] && x[2] == r[1])) GroupRows.Add(new[] { g, r[0], r[1] });
        }

        // ---------------- loads

        internal static string DesignType(string loadType, out bool known)
        {
            known = true;
            string t = Regex.Replace(loadType ?? "", @"\s+REDUCIBLE$", "", RegexOptions.IgnoreCase).Replace(" ", "").ToUpperInvariant();   // a live load's REDUCIBLE: design only
            if (t == "DEAD") return "Dead";
            if (t == "LIVE") return "Live";
            if (t == "ROOFLIVE") return "Roof Live";
            if (t == "SEISMIC-H") return "Quake";
            if (t == "WIND") return "Wind";
            if (t == "SNOW") return "Snow";
            if (t == "TEMPERATURE") return "Temperature";
            if (t == "NONE" || t == "") return "Other";
            known = false;
            return "Other";
        }

        Pat NewPattern(string name, string type)
        {
            var p = new Pat { Name = name, Type = type };
            Pats.Add(p);
            return p;
        }

        string Unique(string want, IEnumerable<string> taken)
        {
            var t = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
            string s = want; int n = 2;
            while (t.Contains(s)) s = want + "_" + I(n++);
            return s;
        }

        void LoadsIn()
        {
            if (In.Analysis == null) { if (In.Loads.Count > 0) Note("no PERFORM ANALYSIS: the cases are read as linear"); }
            else if (Regex.IsMatch(In.Analysis, @"^PDELTA(\s+\d+)?\s+ANALYSIS(\s+SMALLDELTA)?$", RegexOptions.IgnoreCase)) { PDelta = true; Note("PDELTA: the cases are SAP's P-Delta, nonlinear static from zero (" + In.Analysis + ")"); }
            else if (!Regex.IsMatch(In.Analysis, @"^PERFORM ANALYSIS$", RegexOptions.IgnoreCase)) Refuse(In.Analysis + " (FrameStd writes PERFORM ANALYSIS or PDELTA 30 ANALYSIS SMALLDELTA)");
            var refs = new Dictionary<string, Pat>(StringComparer.OrdinalIgnoreCase);
            var numbers = new Dictionary<int, Case>();
            foreach (var l in In.Loads)
            {
                bool known;
                if (l.Kind == "reference")
                {
                    string type = DesignType(l.LoadType, out known);
                    if (!known) Note("LOAD " + l.Id + ": LOADTYPE " + l.LoadType + " read as Other");
                    string name = l.Title.Length > 0 ? l.Title : l.Id;
                    if (Pats.Any(x => Is(x.Name, name))) { Refuse("LOAD " + l.Id + ": a second reference load titled " + name); continue; }
                    if (refs.ContainsKey(l.Id)) { Refuse("LOAD " + l.Id + ": defined twice"); continue; }
                    var p = NewPattern(name, type);
                    refs[l.Id] = p;
                    foreach (var b in l.Blocks) Block(b, p, "LOAD " + l.Id);
                    SelfWeightZones(p);
                    continue;
                }
                if (l.Kind == "primary")
                {
                    int n = int.Parse(l.Id, Inv);
                    if (numbers.ContainsKey(n) || Combos.Any(x => x.Number == n)) { Refuse("LOAD " + l.Id + ": the number twice"); continue; }
                    string h, name = CaseHead.TryGetValue(n, out h) && FrameStd.Plain(h, 60) == l.Title ? h : l.Title.Length > 0 ? l.Title : "LOAD" + l.Id;
                    if (Cases.Any(x => Is(x.Name, name))) { Refuse("LOAD " + l.Id + ": a second load case titled " + name); continue; }
                    var c = new Case { Number = n, Name = name, Type = DesignType(l.LoadType, out known) };
                    if (!known) Note("LOAD " + l.Id + ": LOADTYPE " + l.LoadType + " read as Other");
                    Pat own = null, disp = null;
                    foreach (var b in l.Blocks)
                    {
                        if (b.Name == "REFERENCE LOAD")
                        {
                            foreach (var line in b.Lines)
                            {
                                var t = line.Spec;
                                for (int k = 0; k < t.Length; k += 2)
                                {
                                    Pat rp; double f;
                                    if (k + 1 >= t.Length || !refs.TryGetValue(t[k], out rp) || !Num(t[k + 1], out f)) { Refuse("LOAD " + l.Id + ": REFERENCE LOAD " + line.Text + " (" + t[k] + ": no such reference load, or no factor)"); break; }
                                    c.Loads.Add(new KeyValuePair<string, double>(rp.Name, f));
                                }
                            }
                            continue;
                        }
                        if (b.Name == "REPEAT LOAD")
                        {
                            // an earlier primary load's load patterns, each times the factor (a SAP case of several patterns)
                            foreach (var line in b.Lines)
                            {
                                var t = line.Spec;
                                for (int k = 0; k < t.Length; k += 2)
                                {
                                    int rn; double f; Case rc;
                                    if (k + 1 >= t.Length || !int.TryParse(t[k], NumberStyles.None, Inv, out rn) || !Num(t[k + 1], out f)) { Refuse("LOAD " + l.Id + ": REPEAT LOAD " + line.Text); break; }
                                    if (!numbers.TryGetValue(rn, out rc)) { Refuse("LOAD " + l.Id + ": REPEAT LOAD of load " + t[k] + ", not a primary load before it"); break; }
                                    foreach (var kv in rc.Loads) c.Loads.Add(new KeyValuePair<string, double>(kv.Key, kv.Value * f));
                                }
                            }
                            Note("LOAD " + l.Id + " (" + name + "): REPEAT LOAD as the repeated loads' patterns, each times its factor");
                            continue;
                        }
                        if (b.Name == "SUPPORT DISPLACEMENT LOAD")
                        {
                            if (disp == null) disp = NewPattern(Unique(name + "_SD", Pats.Select(x => x.Name)), c.Type);
                            SupportDisplacements(b, disp, "LOAD " + l.Id);
                            continue;
                        }
                        // loads given in the primary load itself: a pattern of its own
                        if (own == null) own = NewPattern(Unique(name, Pats.Select(x => x.Name)), c.Type);
                        Block(b, own, "LOAD " + l.Id);
                    }
                    if (own != null) { SelfWeightZones(own); c.Loads.Insert(0, new KeyValuePair<string, double>(own.Name, 1)); Note("LOAD " + l.Id + " (" + name + "): its own loads as load pattern " + own.Name); }
                    if (disp != null) c.Loads.Add(new KeyValuePair<string, double>(disp.Name, 1));
                    // a pattern named twice (REPEAT LOAD of loads sharing one): one entry, the factors summed
                    var merged = new List<KeyValuePair<string, double>>();
                    foreach (var kv in c.Loads)
                    {
                        int at = merged.FindIndex(x => x.Key == kv.Key);
                        if (at < 0) merged.Add(kv); else merged[at] = new KeyValuePair<string, double>(kv.Key, merged[at].Value + kv.Value);
                    }
                    c.Loads.Clear(); c.Loads.AddRange(merged);
                    // STAAD's LOADTYPE is the case's; FrameStd writes the first pattern's when the case names none
                    var firstPat = Pats.Where(p => c.Loads.Any(x => x.Key == p.Name)).FirstOrDefault();
                    c.TypeNeeded = firstPat == null || firstPat.Type != c.Type;
                    Cases.Add(c);
                    numbers[n] = c;
                    continue;
                }
                // a combination of primary loads
                int cn = int.Parse(l.Id, Inv);
                if (numbers.ContainsKey(cn) || Combos.Any(x => x.Number == cn)) { Refuse("LOAD COMBINATION " + l.Id + ": the number twice"); continue; }
                string ch, cname = ComboHead.TryGetValue(cn, out ch) && FrameStd.Plain(ch, 60) == l.Title ? ch : l.Title.Length > 0 ? l.Title : "COMB" + l.Id;
                if (Cases.Any(x => Is(x.Name, cname)) || Combos.Any(x => Is(x.Name, cname))) { Refuse("LOAD COMBINATION " + l.Id + ": the name " + cname + " is a case's or another combination's"); continue; }
                var co = new Combo { Number = cn, Name = cname, Kind = l.Method == "SRSS" ? "SRSS" : l.Method == "ABS" ? "Abs Add" : "Linear Add" };
                foreach (var b in l.Blocks)
                    foreach (var line in b.Lines)
                    {
                        var t = line.Spec;
                        for (int k = 0; k < t.Length; k += 2)
                        {
                            int n; double f;
                            if (k + 1 >= t.Length || !int.TryParse(t[k], NumberStyles.None, Inv, out n) || !Num(t[k + 1], out f)) { Refuse("LOAD COMBINATION " + l.Id + ": " + line.Text); break; }
                            if (!numbers.ContainsKey(n)) { Refuse("LOAD COMBINATION " + l.Id + ": load " + t[k] + " is not a primary load before it (FrameStd combines primary loads)"); break; }
                            co.Items.Add(new KeyValuePair<int, double>(n, f));
                        }
                    }
                if (co.Items.Count == 0) Refuse("LOAD COMBINATION " + l.Id + ": no loads");
                Combos.Add(co);
            }
            // DEFINE ENVELOPE (TR.40): SAP's Envelope combinations of the cases and combinations listed, as written. A load
            // listed in two envelopes stays in both: STAAD.Pro 2025's LOAD LIST ENV lists it under each, and its design takes
            // the last envelope's TYPE for it (round 4, 2026-11-25; TR.40 and STAAD's own save keep it in the last only)
            var lastIn = new Dictionary<int, int>();
            foreach (var e in In.Envelopes) foreach (int id in e.Loads) lastIn[id] = e.Number;
            foreach (var e in In.Envelopes)
            {
                string who = "ENVELOPE " + I(e.Number);
                if (Envs.Any(x => x.Number == e.Number)) { Refuse(who + ": defined twice"); continue; }
                var env = new Env { Number = e.Number, Type = e.Type, Name = Unique("ENVELOPE " + I(e.Number), Cases.Select(c => c.Name).Concat(Combos.Select(c => c.Name)).Concat(Envs.Select(x => x.Name))) };
                var later = new List<int>();
                foreach (int id in e.Loads.Distinct())
                {
                    if (lastIn[id] != e.Number) later.Add(id);
                    Case c; var co = Combos.FirstOrDefault(x => x.Number == id);
                    if (numbers.TryGetValue(id, out c)) env.Items.Add(new[] { "C", c.Name });
                    else if (co != null) env.Items.Add(new[] { "K", co.Name });
                    else Refuse(who + ": load " + I(id) + " is not a load case or a combination");
                }
                foreach (var g in later.GroupBy(id => lastIn[id]))
                {
                    var last = In.Envelopes.Last(x => x.Number == g.Key);
                    bool one = g.Count() == 1;
                    Note(who + ": " + (one ? "load " : "loads ") + FrameStd.Condense(g) + (one ? " is" : " are") + " in ENVELOPE " + I(g.Key) + " too. STAAD lists " + (one ? "it" : "them") + " under both, and designs " + (one ? "it" : "them") + " as ENVELOPE " + I(g.Key) + "'s" + (last.Type.Length > 0 ? " TYPE " + last.Type : " (no TYPE)"));
                }
                if (env.Items.Count == 0) { Note(who + ": no load in it, not read"); continue; }
                Envs.Add(env);
            }
            if (Envs.Count > 0) Note("DEFINE ENVELOPE as SAP Envelope combinations: " + string.Join(", ", Envs.Select(x => x.Name + (x.Type.Length > 0 ? " (TYPE " + x.Type + ")" : "")).ToArray()) + "; their numbers and types ride in the workbook's STAAD loads sheet");
            // a pattern's member loads come in its model's table order (point, then distributed, or the other way round)
            DistFirst = In.Loads.SelectMany(x => x.Blocks).Where(b => b.Name == "MEMBER LOAD").Any(b =>
            {
                int firstPoint = b.Lines.FindIndex(x => x.Spec.Length > 0 && (Is(x.Spec[0], "CON") || Is(x.Spec[0], "CMOM")));
                int firstDist = b.Lines.FindIndex(x => x.Spec.Length > 0 && !(Is(x.Spec[0], "CON") || Is(x.Spec[0], "CMOM")));
                return firstDist >= 0 && firstPoint > firstDist;
            });
            // STAAD's load numbers, carried when they are not -Std's own (cases from 1, then the combinations): a LOAD LIST names them
            int want = 1;
            KeepNumbers = false;
            foreach (var c in Cases) { if (c.Number != want) KeepNumbers = true; want++; }
            foreach (var co in Combos) { if (co.Number != want) KeepNumbers = true; want++; }
            if (KeepNumbers) Note("STAAD's load numbers carried (the workbook's STAAD loads sheet): -Std writes LOAD " + string.Join(", ", Cases.Select(c => I(c.Number)).Concat(Combos.Select(c => I(c.Number))).Take(12).ToArray()) + (Cases.Count + Combos.Count > 12 ? " ..." : "") + " again");
            foreach (int j in Enforced) if (!Pats.Any(p => p.Ground.ContainsKey(j))) Note("joint " + JointLabel[j] + ": ENFORCED with no support displacement (written back as FIXED)");
        }

        void Block(StaadBlock b, Pat p, string who)
        {
            string[] k6 = { "FX", "FY", "FZ", "MX", "MY", "MZ" };
            if (b.Name == "SELFWEIGHT")
            {
                double f;
                var head = b.Head;
                // SELFWEIGHT Y -1 LIST <ids>: the same as none when the list holds every member and plate (SAP's self weight is
                // the whole model's)
                if (head.Length > 3 && Is(head[2], "LIST"))
                {
                    int i = 3;
                    var ids = new HashSet<int>(In.IdList(head, ref i));
                    var every = In.Members.Select(x => x[0]).Concat(In.Plates.Select(x => x[0])).ToList();
                    if (i != head.Length || !every.All(ids.Contains)) { Refuse(who + ": SELFWEIGHT " + string.Join(" ", head) + " (on some members only; SAP's self weight is the whole model's)"); return; }
                    Note(who + ": SELFWEIGHT ... LIST of every member and plate, read as SELFWEIGHT " + head[0] + " " + head[1]);
                    head = head.Take(2).ToArray();
                }
                if (head.Length != 2 || !Is(head[0], "Y") || !Num(head[1], out f)) { Refuse(who + ": SELFWEIGHT " + string.Join(" ", b.Head) + " (FrameStd writes SELFWEIGHT Y -f)"); return; }
                if (p.SelfWeight != 0) { Refuse(who + ": SELFWEIGHT twice"); return; }
                p.SelfWeight = -f + 0.0;
                return;
            }
            foreach (var line in b.Lines)
            {
                var t = line.Spec;
                if (b.Name == "JOINT LOAD")
                {
                    var v = new double[6];
                    bool ok = t.Length % 2 == 0;
                    for (int k = 0; ok && k < t.Length; k += 2)
                    {
                        int i = Array.FindIndex(k6, x => Is(t[k], x));
                        double x6;
                        if (i < 0 || !Num(t[k + 1], out x6)) { ok = false; break; }
                        v[i] += x6;
                    }
                    if (!ok) { Refuse(who + ": JOINT LOAD " + line.Text); continue; }
                    foreach (int j in line.Ids)
                    {
                        if (!Pos.ContainsKey(j)) { Refuse(who + ": a joint load on joint " + I(j) + ", not defined"); continue; }
                        double[] have;
                        if (!p.Joint.TryGetValue(j, out have)) p.Joint[j] = have = new double[6];
                        var f = Rinv(new[] { v[0], v[1], v[2] }); var m = Rinv(new[] { v[3], v[4], v[5] });
                        for (int i = 0; i < 3; i++) { have[i] += f[i]; have[3 + i] += m[i]; }
                    }
                    continue;
                }
                if (b.Name == "MEMBER LOAD") { foreach (int id in line.Ids) MemberLoad(id, t, p, who, line.Text); continue; }
                if (b.Name == "TEMPERATURE LOAD") { foreach (int id in line.Ids) TempLoad(id, t, p, who, line.Text); continue; }
                Refuse(who + ": " + b.Name + " " + line.Text);
            }
        }

        // FrameStd's self weight on the rigid end zones, as joint loads (STAAD's SELFWEIGHT loads the clear length only),
        // taken off: SAP's own self weight covers the zones
        void SelfWeightZones(Pat p)
        {
            if (p.SelfWeight == 0) return;
            foreach (var m in Frames)
            {
                double d = Val(m.Mat, "DENSITY"), ax = FrameStd.Printed(m.Ax), q = d * ax * p.SelfWeight;
                if (q == 0) continue;
                // the weight's precision: its factors' as written (6 digits each in a file STAAD.Pro saved)
                double rel = RelHalf(d) + RelHalf(ax) + RelHalf(p.SelfWeight) + 1e-12;
                if (m.ZoneI > 0) Untransfer(m, p, m.ZoneI / 2, new[] { 0, -q * m.ZoneI, 0 }, true, rel);
                if (m.ZoneJ > 0) Untransfer(m, p, m.L - m.ZoneJ / 2, new[] { 0, -q * m.ZoneJ, 0 }, false, rel);
            }
        }

        // a force FrameStd moved from inside an end zone (at s from joint I) to its joint, taken off that joint's load
        void Untransfer(Mem m, Pat p, double s, double[] f, bool atI, double rel)
        {
            int j = atI ? m.I : m.J;
            var r = Add(m.Trans, Mul(m.X, atI ? s : s - m.L));
            var mm = Cross(r, f);
            var take = Rinv(f).Concat(Rinv(mm)).ToArray();
            double[] have;
            if (!p.Joint.TryGetValue(j, out have)) { Refuse("joint " + I(j) + ": " + p.Name + " has no joint load for the weight of member " + I(m.Id) + "'s rigid end zone (SAP's self weight loads end zones, STAAD's does not: FrameStd writes their weight as joint loads)"); return; }
            for (int i = 0; i < 6; i++)
            {
                // what is left once the zone's weight is off, to the digits the joint load is written with
                double before = have[i], v = before - take[i], h = Math.Min(HalfUnit(before), 1e-5 * Math.Abs(before));
                if (Math.Abs(v) <= h + rel * Math.Abs(take[i]) + 1e-10) v = 0;
                else { if (h > 0) v = Math.Round(v / (2 * h)) * 2 * h; v = Round12(v); }
                have[i] = v;
            }
            if (have.All(x => x == 0)) p.Joint.Remove(j);
        }

        // a STAAD load direction as SAP's: CoordSys, Dir and the sign SAP's value takes
        static bool Dir(string key, bool point, bool moment, bool negative, out string cs, out string dir, out double sign)
        {
            cs = "GLOBAL"; dir = null; sign = 1;
            string k = key.ToUpperInvariant();
            if (k == "X" || k == "Y" || k == "Z") { cs = "Local"; dir = (k[0] - 'X' + 1).ToString(Inv); return true; }
            if (k.Length != 2 || (k[0] != 'G' && k[0] != 'P')) return false;
            bool proj = k[0] == 'P';
            if (proj && (point || moment)) return false;
            string s = proj ? " Proj" : "";
            if (k[1] == 'X') { dir = "X" + s; return true; }
            if (k[1] == 'Y') { if (negative) { dir = "Gravity" + s; sign = -1; } else dir = "Z" + s; return true; }
            if (k[1] == 'Z') { dir = "Y" + s; sign = -1; return true; }
            return false;
        }

        void MemberLoad(int id, string[] t, Pat p, string who, string text)
        {
            Mem m;
            string what = who + ": MEMBER LOAD " + I(id) + " " + string.Join(" ", t);
            if (!FrameOf.TryGetValue(id, out m)) { Refuse(what + " (" + (Trusses.Any(q => q[0] == id) ? "a truss member" : "no such member") + ")"); return; }
            string type = t[0].ToUpperInvariant();
            bool point = type == "CON" || type == "CMOM", moment = type == "CMOM" || type == "UMOM";
            int want = type == "CON" || type == "CMOM" ? 4 : type == "UNI" || type == "UMOM" ? -1 : type == "TRAP" ? -2 : 0;
            if (want == 0 || t.Length < 3) { Refuse(what + " (FrameStd writes CON CMOM UNI UMOM TRAP)"); return; }
            var v = new List<double>();
            for (int k = 2; k < t.Length; k++) { double x; if (!Num(t[k], out x)) { Refuse(what); return; } v.Add(x); }
            if ((want == 4 && v.Count != 2) || (want == -1 && v.Count != 1 && v.Count != 3) || (want == -2 && v.Count != 2 && v.Count != 4)) { Refuse(what + " (its numbers)"); return; }
            double clear = m.L - m.ZoneI - m.ZoneJ;
            // on the clear length, to the digits the distance is written with; a hair past its end taken as the end
            Func<double, double> slack = d => 1e-9 * m.L + Math.Min(HalfUnit(d), 1e-5 * m.L);
            Func<double, bool> inside = d => d >= -slack(d) && d <= clear + slack(d);
            Func<double, double> onto = d => Math.Max(0, Math.Min(clear, d));
            double w1 = v[0], w2 = type == "TRAP" ? v[1] : v[0];
            string cs, dir; double sign;
            if (!Dir(t[1], point, moment, !moment && w1 <= 0 && w2 <= 0 && (w1 < 0 || w2 < 0), out cs, out dir, out sign)) { Refuse(what + " (direction " + t[1] + ")"); return; }
            if (m.Delta != 0 && cs == "Local" && dir != "1") { Refuse(what + " (along local " + t[1].ToLowerInvariant() + " on an angle: STAAD's y / z are its principal axes, SAP's 2 / 3 its legs)"); return; }
            string head = "Frame=" + Q(m.Label) + "   LoadPat=" + Q(p.Name) + "   CoordSys=" + cs + "   Type=" + (moment ? "Moment" : "Force") + "   Dir=" + Q(dir) + "   DistType=AbsDist";
            if (point)
            {
                double d = v[1];
                if (!inside(d)) { Refuse(what + " (at " + FrameStd.N(d) + ", off its clear length " + FrameStd.N(clear) + ")"); return; }
                d = onto(d);
                double zi = m.ZoneI, s = zi == 0 ? d : Shortest(zi + d, x => x - zi, d);
                p.Point.Add(head + "   RelDist=" + F(Round12(s / m.L)) + "   AbsDist=" + F(s) + "   " + (moment ? "Moment=" : "Force=") + F(v[0] * sign));
                return;
            }
            double d1 = 0, d2 = clear;
            if (v.Count > 2 || (type == "TRAP" && v.Count == 4)) { d1 = type == "TRAP" ? v[2] : v[1]; d2 = type == "TRAP" ? v[3] : v[2]; }
            if (d2 < d1) { double x = d1; d1 = d2; d2 = x; x = w1; w1 = w2; w2 = x; }
            if (!inside(d1) || !inside(d2) || d2 - d1 <= 1e-12 * m.L) { Refuse(what + " (from " + FrameStd.N(d1) + " to " + FrameStd.N(d2) + ", off its clear length " + FrameStd.N(clear) + ")"); return; }
            d1 = onto(d1); d2 = onto(d2);
            double z0 = m.ZoneI, a = z0 == 0 ? d1 : Shortest(z0 + d1, x => x - z0, d1), b = z0 == 0 ? d2 : Shortest(z0 + d2, x => x - z0, d2);
            string kA = moment ? "MOverLA" : "FOverLA", kB = moment ? "MOverLB" : "FOverLB";
            p.Dist.Add(head + "   RelDistA=" + F(Round12(a / m.L)) + "   RelDistB=" + F(Round12(b / m.L)) + "   AbsDistA=" + F(a) + "   AbsDistB=" + F(b) + "   " + kA + "=" + F(w1 * sign) + "   " + kB + "=" + F(w2 * sign));
        }

        void TempLoad(int id, string[] t, Pat p, string who, string text)
        {
            Mem m;
            string what = who + ": TEMPERATURE LOAD " + I(id) + " " + string.Join(" ", t);
            if (!FrameOf.TryGetValue(id, out m)) { Refuse(what + " (" + (Trusses.Any(q => q[0] == id) ? "a truss member" : "no such member") + ")"); return; }
            if (!Is(t[0], "TEMP") || t.Length < 2 || t.Length > 4) { Refuse(what + " (FrameStd writes TEMP a [b [c]])"); return; }
            var v = new double[3];
            for (int k = 1; k < t.Length; k++) if (!Num(t[k], out v[k - 1])) { Refuse(what); return; }
            string head = "Frame=" + Q(m.Label) + "   LoadPat=" + Q(p.Name);
            if (v[0] != 0) p.Temp.Add(head + "   Type=Temperature   Temp=" + F(v[0]));
            if (m.Delta != 0 && (v[1] != 0 || v[2] != 0)) { Refuse(what + " (a gradient on an angle: STAAD's y / z are its principal axes, SAP's 2 / 3 its legs)"); return; }
            // STAAD's differences top to bottom (along y) and side to side (along z), over the depth / width: SAP's gradients
            double dep = m.Depth, wid = m.Width;
            if (v[1] != 0) { if (dep > 0) p.Temp.Add(head + "   Type=Gradient2   TempGrad2=" + F(Shortest(v[1] / dep, x => x * dep, v[1]))); else Refuse(what + " (a gradient on a section with no depth)"); }
            if (v[2] != 0) { if (wid > 0) p.Temp.Add(head + "   Type=Gradient3   TempGrad3=" + F(Shortest(v[2] / wid, x => x * wid, v[2]))); else Refuse(what + " (a gradient on a section with no width)"); }
        }

        void SupportDisplacements(StaadBlock b, Pat p, string who)
        {
            string[] k6 = { "FX", "FY", "FZ", "MX", "MY", "MZ" };
            int[] global = { 0, 2, 1, 3, 5, 4 };
            foreach (var line in b.Lines)
            {
                var t = line.Spec;
                double v;
                int i = t.Length == 2 ? Array.FindIndex(k6, x => Is(t[0], x)) : -1;
                if (i < 0 || !Num(t[1], out v)) { Refuse(who + ": SUPPORT DISPLACEMENT LOAD " + line.Text + " (FrameStd writes joint DOF value)"); continue; }
                foreach (int j in line.Ids)
                {
                    bool[] res;
                    if (!Restraint.TryGetValue(j, out res) || !res[global[i]] || JointAngles.ContainsKey(j)) { Refuse(who + ": a support displacement " + k6[i] + " at joint " + I(j) + ", which its support does not hold in global axes"); continue; }
                    double[] have;
                    if (!p.Ground.TryGetValue(j, out have)) p.Ground[j] = have = new double[6];
                    var s = new double[6];
                    s[i] = i < 3 ? v : Shortest(v * Math.PI / 180, x => x * 180 / Math.PI, v);
                    var u = Rinv(new[] { s[0], s[1], s[2] }); var r = Rinv(new[] { s[3], s[4], s[5] });
                    for (int k = 0; k < 3; k++) { have[k] += u[k]; have[3 + k] += r[k]; }
                }
            }
        }

        // ---------------- the .s2k text

        string Emit()
        {
            var w = new List<string>();
            w.Add("File generated by Pluto StdToFrame.cs: a STAAD.Pro .std (" + System.IO.Path.GetFileName(In.Path) + ") read back as a frame model");
            w.Add("");
            Action<string, IEnumerable<string>> table = (name, rows) => { var l = rows.ToList(); if (l.Count == 0) return; w.Add("TABLE:  \"" + name + "\""); foreach (var r in l) w.Add("   " + r); w.Add(""); };
            string version = !string.IsNullOrEmpty(SapVersion) ? SapVersion : HeadVersion ?? "22.0.0";
            table("PROGRAM CONTROL", new[] { "ProgramName=SAP2000   Version=" + version + "   CurrUnits=\"" + UnitsText + "\"" });
            table("TABLES AUTOMATICALLY SAVED AFTER ANALYSIS", new[] { "SaveFile=No" });
            table("ACTIVE DEGREES OF FREEDOM", new[] { "UX=Yes   UY=Yes   UZ=Yes   RX=Yes   RY=Yes   RZ=Yes" });
            table("MATERIAL PROPERTIES 01 - GENERAL", MatOrder.Select(n => "Material=" + Q(SapMat(n)) + "   Type=" + (Mats[n].Type != null && Is(Mats[n].Type, "STEEL") ? "Steel" : "Other") + "   SymType=Isotropic   TempDepend=No   Color=Blue"));
            table("MATERIAL PROPERTIES 02 - BASIC MECHANICAL PROPERTIES", MatOrder.Select(n =>
            {
                var m = Mats[n];
                double e = m.Values["E"], nu, g, d = Val(n, "DENSITY");
                bool hasNu = m.Values.TryGetValue("POISSON", out nu), hasG = m.Values.TryGetValue("G", out g);
                if (!hasNu) nu = hasG ? e / (2 * g) - 1 : 0;
                if (!hasG) g = e / (2 * (1 + nu));
                return "Material=" + Q(SapMat(n)) + "   UnitWeight=" + F(d) + "   UnitMass=" + F(Round12(d / Gravity)) + "   E1=" + F(e) + "   G12=" + F(g) + "   U12=" + F(hasNu ? nu : double.Parse(nu.ToString("G12", Inv), Inv)) + "   A1=" + F(Val(n, "ALPHA"));
            }));
            table("MATERIAL PROPERTIES 03A - STEEL DATA", MatOrder.Where(n => Mats[n].Type != null && Is(Mats[n].Type, "STEEL") && Mats[n].Strength.ContainsKey("FY") && Mats[n].Strength.ContainsKey("FU")).Select(n =>
            {
                var s = Mats[n].Strength;
                double fy = s["FY"], fu = s["FU"], ry, rt;
                if (!s.TryGetValue("RY", out ry)) ry = 1;
                if (!s.TryGetValue("RT", out rt)) rt = 1;
                return "Material=" + Q(SapMat(n)) + "   Fy=" + F(fy) + "   Fu=" + F(fu) + "   EffFy=" + F(Shortest(ry * fy, x => x / fy, ry)) + "   EffFu=" + F(Shortest(rt * fu, x => x / fu, rt)) +
                       "   SSCurveOpt=Simple   SSHysType=Kinematic   SHard=0.015   SMax=0.11   SRup=0.17   FinalSlope=-0.1   CoupModType=\"Von Mises\"";
            }));
            table("FRAME SECTION PROPERTIES 01 - GENERAL", Secs.Select(s =>
            {
                string head = "SectionName=" + Q(s.Name) + "   Material=" + Q(SapMat(s.Mat)) + "   ";
                if (s.Row != null) return head + s.Row;
                var p = s.P;
                Func<double, double> z = x => double.IsNaN(x) ? 0 : x;
                return head + "Shape=General   t3=" + F(p[6]) + "   t2=" + F(p[7]) + "   Area=" + F(p[0]) + "   TorsConst=" + F(p[1]) + "   I33=" + F(p[3]) + "   I22=" + F(p[2]) +
                       "   I23=0   AS2=" + F(z(p[4])) + "   AS3=" + F(z(p[5])) + "   Color=Green";
            }));
            table("AREA SECTION PROPERTIES", AreaSecs.Select(s => "Section=" + Q(s[0]) + "   Material=" + Q(SapMat(s[1])) + "   MatAngle=0   AreaType=Shell   Type=" + s[2] + "   Thickness=" + s[3] + "   BendThick=" + s[3] + "   Color=Gray8Dark"));
            table("JOINT COORDINATES", In.JointIds.Select(id => { var p = Rinv(Pos[id]); return "Joint=" + Q(JointLabel[id]) + "   CoordSys=GLOBAL   CoordType=Cartesian   XorR=" + F(p[0]) + "   Y=" + F(p[1]) + "   Z=" + F(p[2]); }));
            table("JOINT RESTRAINT ASSIGNMENTS", In.JointIds.Where(Restraint.ContainsKey).Select(id => "Joint=" + Q(JointLabel[id]) + string.Concat(Enumerable.Range(0, 6).Select(i => "   " + FrameJoint.Dofs[i] + "=" + (Restraint[id][i] ? "Yes" : "No")).ToArray())));
            table("JOINT LOCAL AXES ASSIGNMENTS 1 - TYPICAL", In.JointIds.Where(JointAngles.ContainsKey).Select(id => "Joint=" + Q(JointLabel[id]) + "   AngleA=" + F(JointAngles[id][0]) + "   AngleB=" + F(JointAngles[id][1]) + "   AngleC=" + F(JointAngles[id][2]) + "   AdvanceAxes=No"));
            table("JOINT SPRING ASSIGNMENTS 1 - UNCOUPLED", In.JointIds.Where(Spring.ContainsKey).Select(id => "Joint=" + Q(JointLabel[id]) + "   CoordSys=" + (JointAngles.ContainsKey(id) ? "Local" : "GLOBAL") +
                string.Concat(Enumerable.Range(0, 6).Select(i => "   " + FrameJoint.Dofs[i] + "=" + F(Spring[id][i])).ToArray())));
            var fr = Frames.Where(m => m.Section != null).ToList();
            table("CONNECTIVITY - FRAME", fr.Select(m => "Frame=" + Q(m.Label) + "   JointI=" + Q(JointLabel[m.I]) + "   JointJ=" + Q(JointLabel[m.J]) + "   IsCurved=No"));
            table("FRAME SECTION ASSIGNMENTS", fr.Select(m =>
            {
                var s = Secs.First(x => x.Name == m.Section);
                return "Frame=" + Q(m.Label) + "   SectionType=" + Q(s.Shape) + "   AutoSelect=N.A.   AnalSect=" + Q(s.Name) + "   DesignSect=" + Q(s.Name) + "   MatProp=" + (Is(s.Mat, m.Mat) ? "Default" : Q(SapMat(m.Mat)));
            }));
            table("FRAME LOCAL AXES ASSIGNMENTS 1 - TYPICAL", fr.Where(m => m.Angle != 0).Select(m => "Frame=" + Q(m.Label) + "   Angle=" + F(m.Angle) + "   AdvanceAxes=No"));
            table("FRAME RELEASE ASSIGNMENTS 1 - GENERAL", fr.Where(m => m.HasRel).Select(m => "Frame=" + Q(m.Label) + string.Concat(Enumerable.Range(0, 12).Select(i => "   " + FrameMember.ReleaseDofs[i] + "=" + (m.Rel[i] ? "Yes" : "No")).ToArray()) +
                "   PartialFix=" + (m.Pf.Any(x => x != 0) ? "Yes" : "No")));
            table("FRAME RELEASE ASSIGNMENTS 2 - PARTIAL FIXITY", fr.Where(m => m.Pf.Any(x => x != 0)).Select(m => "Frame=" + Q(m.Label) + string.Concat(Enumerable.Range(0, 12).Where(i => m.Pf[i] != 0).Select(i => "   " + FrameMember.ReleaseDofs[i] + "=" + F(m.Pf[i])).ToArray())));
            table("FRAME INSERTION POINT ASSIGNMENTS", fr.Where(m => Len(m.Trans) > 0).Select(m =>
            {
                double y = Dot(m.Trans, m.Y), z = Dot(m.Trans, m.Z);
                if (Math.Abs(y) <= 1e-12 * m.L) y = 0;
                if (Math.Abs(z) <= 1e-12 * m.L) z = 0;
                return "Frame=" + Q(m.Label) + "   CardinalPt=\"10 (centroid)\"   CoordSys=Local   JtOffsetXI=0   JtOffsetYI=" + F(y) + "   JtOffsetZI=" + F(z) + "   JtOffsetXJ=0   JtOffsetYJ=" + F(y) + "   JtOffsetZJ=" + F(z) + "   Mirror2=No   Mirror3=No   Transform=Yes";
            }));
            table("FRAME OFFSET ALONG LENGTH ASSIGNMENTS", fr.Where(m => m.ZoneI > 0 || m.ZoneJ > 0).Select(m => "Frame=" + Q(m.Label) + "   Type=User   LengthI=" + F(m.ZoneI) + "   LengthJ=" + F(m.ZoneJ) + "   RigidFactor=1"));
            table("FRAME PROPERTY MODIFIERS", fr.Where(m => m.Mods != null).Select(m => "Frame=" + Q(m.Label) + string.Concat(Enumerable.Range(0, 8).Select(i => "   " + FrameMember.ModifierKeys[i] + "=" + F(m.Mods[i])).ToArray())));
            // STAAD's members meet only at their ends: SAP's auto mesh off, as the frames were
            table("FRAME AUTO MESH ASSIGNMENTS", fr.Select(m => "Frame=" + Q(m.Label) + "   AutoMesh=No   AtJoints=No   AtFrames=No   NumSegments=0   MaxLength=0   MaxDegrees=0"));
            table("CONNECTIVITY - AREA", Areas.Select(a => "Area=" + Q(a[0]) + "   NumJoints=" + I(a.Length - 3) + string.Concat(Enumerable.Range(3, a.Length - 3).Select(k => "   Joint" + I(k - 2) + "=" + Q(a[k])).ToArray())));
            table("AREA SECTION ASSIGNMENTS", Areas.Select(a => "Area=" + Q(a[0]) + "   Section=" + Q(a[1]) + "   MatProp=" + Q(a[2])));
            string gen = "   Mass=0   Weight=0   RotInert1=0   RotInert2=0   RotInert3=0   DefLength=1   DefArea=1   PDM2I=0   PDM2J=0   PDM3I=0   PDM3J=0   Color=Magenta";
            table("LINK PROPERTY DEFINITIONS 01 - GENERAL", LinkProps.Select(p => "Link=" + Q(p[0]) + "   LinkType=Linear" + gen));
            table("LINK PROPERTY DEFINITIONS 02 - LINEAR", LinkProps.Select(p => "Link=" + Q(p[0]) + "   DOF=U1   Fixed=No   TransKE=" + p[1] + "   TransCE=0"));
            table("CONNECTIVITY - LINK", Links.Select(l => "Link=" + Q(l[0]) + "   JointI=" + Q(l[1]) + "   JointJ=" + Q(l[2])));
            table("LINK PROPERTY ASSIGNMENTS", Links.Select(l => "Link=" + Q(l[0]) + "   LinkProp=" + Q(l[3]) + "   LinkFDProp=None"));
            string gflags = "   Selection=Yes   SectionCut=Yes   Steel=Yes   Concrete=Yes   Aluminum=Yes   ColdFormed=Yes   Stage=Yes   Bridge=Yes   AutoSeismic=No   AutoWind=No   SelDesSteel=No   SelDesAlum=No   SelDesCold=No   MassWeight=Yes   Color=Green";
            table("GROUPS 1 - DEFINITIONS", GroupOrder.Select(g => "GroupName=" + Q(g) + gflags));
            table("GROUPS 2 - ASSIGNMENTS", GroupRows.Select(r => "GroupName=" + Q(r[0]) + "   ObjectType=" + r[1] + "   ObjectLabel=" + Q(r[2])));
            // a Wind or Quake pattern names its automatic load, none (SAP 24.2 warns of the blank field otherwise)
            table("LOAD PATTERN DEFINITIONS", Pats.Select(p => "LoadPat=" + Q(p.Name) + "   DesignType=" + Q(p.Type) + "   SelfWtMult=" + F(p.SelfWeight) +
                (p.Type == "Wind" || p.Type == "Quake" ? "   AutoLoad=None" : "")));
            table("JOINT LOADS - FORCE", Pats.SelectMany(p => p.Joint.Where(kv => kv.Value.Any(x => x != 0)).Select(kv => "Joint=" + Q(JointLabel[kv.Key]) + "   LoadPat=" + Q(p.Name) + "   CoordSys=GLOBAL   F1=" + F(kv.Value[0]) + "   F2=" + F(kv.Value[1]) + "   F3=" + F(kv.Value[2]) +
                "   M1=" + F(kv.Value[3]) + "   M2=" + F(kv.Value[4]) + "   M3=" + F(kv.Value[5]))));
            table("JOINT LOADS - GROUND DISPLACEMENT", Pats.SelectMany(p => p.Ground.Select(kv => "Joint=" + Q(JointLabel[kv.Key]) + "   LoadPat=" + Q(p.Name) + "   CoordSys=GLOBAL" + string.Concat(Enumerable.Range(0, 6).Select(i => "   " + FrameJoint.Dofs[i] + "=" + F(kv.Value[i])).ToArray()))));
            if (DistFirst) table("FRAME LOADS - DISTRIBUTED", Pats.SelectMany(p => p.Dist));
            table("FRAME LOADS - POINT", Pats.SelectMany(p => p.Point));
            if (!DistFirst) table("FRAME LOADS - DISTRIBUTED", Pats.SelectMany(p => p.Dist));
            table("FRAME LOADS - TEMPERATURE", Pats.SelectMany(p => p.Temp));
            string type = PDelta ? "NonStatic" : "LinStatic";
            // a design type of the case's own is user specified: without DesTypeOpt SAP 24.2 sets its own from the patterns
            // (LOAD 5 LOADTYPE None, of DEAD and LIVE, came back Dead in round 4)
            table("LOAD CASE DEFINITIONS", Cases.Select(c => "Case=" + Q(c.Name) + "   Type=" + type + "   InitialCond=Zero" + (c.TypeNeeded ? "   DesTypeOpt=\"User Spec\"   DesignType=" + Q(c.Type) : "")));
            table("CASE - STATIC 1 - LOAD ASSIGNMENTS", Cases.SelectMany(c => c.Loads.Select(kv => "Case=" + Q(c.Name) + "   LoadType=\"Load pattern\"   LoadName=" + Q(kv.Key) + "   LoadSF=" + F(kv.Value))));
            string nlp = "   ResultsSave=\"Final State\"   SolScheme=\"Iterative Events\"   MaxTotal=200   MaxNull=50   EvLumpTol=0.01   MaxEvPerStp=24   MaxIterCS=10   MaxIterNR=40   ItConvTol=0.0001   TFMaxIter=10   TFTol=0.01   TFAccelFact=1   TFNoStop=No";
            if (PDelta) table("CASE - STATIC 4 - NONLINEAR PARAMETERS", Cases.Select(c => "Case=" + Q(c.Name) + "   GeoNonLin=P-Delta" + nlp));
            var byNumber = Cases.ToDictionary(c => c.Number);
            string ctype = PDelta ? "\"NonLin Static\"" : "\"Linear Static\"";
            var combos = new List<string>();
            foreach (var co in Combos)
                for (int k = 0; k < co.Items.Count; k++)
                {
                    string item = "CaseType=" + ctype + "   CaseName=" + Q(byNumber[co.Items[k].Key].Name) + "   ScaleFactor=" + F(co.Items[k].Value);
                    combos.Add("ComboName=" + Q(co.Name) + (k == 0 ? "   ComboType=" + Q(co.Kind) + "   AutoDesign=No   " + item + "   SteelDesign=None   ConcDesign=None   AlumDesign=None   ColdDesign=None" : "   " + item));
                }
            foreach (var e in Envs)
                for (int k = 0; k < e.Items.Count; k++)
                {
                    string item = "CaseType=" + (e.Items[k][0] == "C" ? ctype : "\"Response Combo\"") + "   CaseName=" + Q(e.Items[k][1]) + "   ScaleFactor=1";
                    combos.Add("ComboName=" + Q(e.Name) + (k == 0 ? "   ComboType=Envelope   AutoDesign=No   " + item + "   SteelDesign=None   ConcDesign=None   AlumDesign=None   ColdDesign=None" : "   " + item));
                }
            table("COMBINATION DEFINITIONS", combos);
            // Pluto's own table of STAAD's load numbers (StaadCarry): what SAP has no place for. The design follows in Run
            table(StaadCarry.Loads, (KeepNumbers ? Cases.Select(c => "Kind=Case   Name=" + Q(c.Name) + "   Number=" + I(c.Number))
                                        .Concat(Combos.Select(c => "Kind=Combination   Name=" + Q(c.Name) + "   Number=" + I(c.Number))) : new string[0])
                                        .Concat(Envs.Select(e => "Kind=Envelope   Name=" + Q(e.Name) + "   Number=" + I(e.Number) + (e.Type.Length > 0 ? "   Type=" + e.Type : ""))));
            w.Add("END TABLE DATA");
            w.Add("");
            return string.Join("\r\n", w.ToArray());
        }
    }
}
