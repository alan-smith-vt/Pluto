using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

// StaadDesign -- a model's STAAD design and output requests (2026-11-25; vault/arms/staad-design.md): what STAAD does
// after the analysis. It rides in the frame model, in tables of Pluto's own (StaadCarry.IsOwn: the .s2k for SAP leaves
// them out), and the frame workbook shows it:
//   member values  one row per frame, on the frame itself (TMembers, a record of the frame: it follows a rename, a split,
//                  a deletion): a value per parameter (Columns), and Other for any other ("STP 1; TORSION 1"). The
//                  workbook's Frames rows hold them. They belong to the first set
//   Sets           one per parameter set (STAAD's PARAMETER n): its CODE, METHOD and the UNIT its values are in
//   Params         rows by list, for what a frame's row cannot hold: a set's values for ALL (its defaults: a frame's
//                  blank cell takes them), values for links (STAAD's truss members), and every row of a later set
//   Run            what STAAD does, in order: CHECK CODE or SELECT under a set, PRINT, or a COMMAND as written; each under a
//                  LOAD LIST (blank: the row above's) and a UNIT (blank: Units)
// Members name the model as the workbook does: its groups (as on the Groups sheet; a leading _ is STAAD's), its frame
// and link labels, ranges a TO b of number labels. -Std writes them as STAAD's numbers and group names (FrameStd); a
// name the model does not have is refused. DJ1 / DJ2 name joints by label. Load lists name STAAD's load numbers (the
// STAAD loads sheet), ENV and the envelope numbers, or ALL. C# 5 (prefix Staad).

// a member list as the model has it
public sealed class StaadMembers
{
    public bool All;
    public readonly List<string> Frames = new List<string>(), Others = new List<string>();   // frame labels; link labels
    public string Error;                                                                        // what it names that the model has not
}

public sealed class StaadDesign
{
    public const string TDesign = "PLUTO STAAD DESIGN", TParams = "PLUTO STAAD PARAMETERS", TRun = "PLUTO STAAD RUN", TMembers = "PLUTO STAAD MEMBERS";
    public static readonly string[] Columns = { "FYLD", "FU", "KY", "KZ", "LY", "LZ", "UNB", "UNT", "CB", "DFF", "DJ1", "DJ2", "NSF", "RATIO", "MAIN", "TRACK", "BEAM" };
    public static readonly string[] Actions = { "CHECK CODE", "SELECT", "PRINT", "COMMAND" };
    public static readonly string[] Codes = { "AISC UNIFIED 2016", "AISC UNIFIED 2010", "AISC UNIFIED 2005", "AISC" };
    public static readonly string[] Methods = { "LRFD", "ASD" };
    public const string Other = "Other";

    public sealed class Set { public string Name = "", Code = "", Method = "", Units = ""; }
    public sealed class Param
    {
        public string Set = "", Members = "ALL", Other = "";
        public readonly Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public bool Blank { get { return Values.Values.All(v => (v ?? "").Trim().Length == 0) && (Other ?? "").Trim().Length == 0; } }
    }
    public sealed class Step { public string LoadList = "", Action = "", Set = "", Members = "", Units = "", Text = ""; }

    public string Units = "";             // the UNIT prints and commands are in (blank: the model's)
    public string AnalysisPrint = "";     // PERFORM ANALYSIS's PRINT option (STATICS CHECK ...)
    public readonly List<Set> Sets = new List<Set>();
    public readonly List<Param> Params = new List<Param>();
    public readonly List<Step> Run = new List<Step>();
    public readonly List<string> Notes = new List<string>();

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static bool Is(string a, string b) { return string.Equals((a ?? "").Trim(), b, StringComparison.OrdinalIgnoreCase); }
    static string[] Tok(string s) { return (s ?? "").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries); }
    static bool Number(string s) { double v; return double.TryParse(s, NumberStyles.Float, Inv, out v); }

    // nothing by list or to run (a frame's own values are the model's: HasMembers)
    public bool Empty { get { return Sets.Count == 0 && Params.Count == 0 && Run.Count == 0 && AnalysisPrint.Length == 0; } }

    // a UNIT's words as one key ("FEET KIP" and "FT KIPS" alike); a word the stamper does not know, as written
    public static string UnitKey(string words, string inForce)
    {
        LengthUnit l; ForceUnit f;
        string len = null, force = null;
        foreach (string w in Tok(inForce).Concat(Tok(words)))
        {
            string u = w.ToUpperInvariant();
            if (AnlUnitStamper.TryLength(u, out l)) len = l.ToString();
            else if (AnlUnitStamper.TryForce(u, out f)) force = f.ToString();
        }
        return (len ?? "?") + " " + (force ?? "?");
    }

    // ---------------- a frame's own values (a record of the frame, TMembers)

    // col: one of Columns, or Other; "" when it has none
    public static string Member(FrameMember f, string col) { var r = f.Rec(TMembers); return r == null ? "" : r.Get(col) ?? ""; }
    public static void SetMember(FrameMember f, string col, string value)
    {
        var r = f.Rec(TMembers);
        value = (value ?? "").Trim().Replace("\"", "'");
        if (value.Length == 0)
        {
            if (r == null) return;
            r.Remove(col);
            if (r.F.Count <= 1) f.Drop(TMembers);
            return;
        }
        if (r == null) { r = new FrameRec(TMembers).Set("Frame", f.Label); f.Recs.Add(r); }
        if (r.Get(col) != value) r.Set(col, value);
    }
    public static bool HasMembers(FrameModel m) { return m.Frames.Any(f => f.Rec(TMembers) != null); }

    // "STP 1; TORSION 1" as name -> value, in order
    public static List<KeyValuePair<string, string>> Others(string text)
    {
        var o = new List<KeyValuePair<string, string>>();
        foreach (string part in (text ?? "").Split(';').Select(x => x.Trim()).Where(x => x.Length > 0))
        {
            var t = Tok(part);
            o.Add(new KeyValuePair<string, string>(t[0].ToUpperInvariant(), string.Join(" ", t.Skip(1).ToArray())));
        }
        return o;
    }
    static string OthersText(IEnumerable<KeyValuePair<string, string>> o) { return string.Join("; ", o.Select(kv => (kv.Key + " " + kv.Value).Trim()).ToArray()); }

    // ---------------- in the model: Pluto's own tables in its document

    public static StaadDesign Of(FrameModel m)
    {
        var d = new StaadDesign();
        if (m.Source == null) return d;
        var t = m.Source.Table(TDesign);
        if (t != null)
            foreach (var r in t.Rows)
            {
                if (Is(r.Get("Kind"), "Settings")) { d.Units = r.Get("Units") ?? ""; d.AnalysisPrint = r.Get("AnalysisPrint") ?? ""; }
                else if (Is(r.Get("Kind"), "Set")) d.Sets.Add(new Set { Name = r.Get("Name") ?? "", Code = r.Get("Code") ?? "", Method = r.Get("Method") ?? "", Units = r.Get("Units") ?? "" });
            }
        t = m.Source.Table(TParams);
        if (t != null)
            foreach (var r in t.Rows)
            {
                var p = new Param { Set = r.Get("Set") ?? "", Members = r.Get("Members") ?? "ALL", Other = r.Get(Other) ?? "" };
                foreach (string c in Columns) { string v = r.Get(c); if (!string.IsNullOrEmpty(v)) p.Values[c] = v; }
                d.Params.Add(p);
            }
        t = m.Source.Table(TRun);
        if (t != null)
            foreach (var r in t.Rows)
                d.Run.Add(new Step { LoadList = r.Get("LoadList") ?? "", Action = r.Get("Action") ?? "", Set = r.Get("Set") ?? "", Members = r.Get("Members") ?? "", Units = r.Get("Units") ?? "", Text = r.Get("Text") ?? "" });
        return d;
    }

    static string Q(string v) { return "\"" + (v ?? "").Replace("\"", "'") + "\""; }
    List<string>[] Rows()
    {
        var a = new List<string>(); var b = new List<string>(); var c = new List<string>();
        if (Units.Trim().Length > 0 || AnalysisPrint.Trim().Length > 0) a.Add("Kind=Settings   Units=" + Q(Units.Trim()) + "   AnalysisPrint=" + Q(AnalysisPrint.Trim()));
        foreach (var s in Sets) a.Add("Kind=Set   Name=" + Q(s.Name) + "   Code=" + Q(s.Code) + "   Method=" + Q(s.Method) + "   Units=" + Q(s.Units));
        int n = 0;
        foreach (var p in Params)
            b.Add("Row=" + (++n).ToString(Inv) + "   Set=" + Q(p.Set) + "   Members=" + Q(p.Members) +
                  string.Concat(Columns.Where(k => p.Values.ContainsKey(k) && p.Values[k].Trim().Length > 0).Select(k => "   " + k + "=" + Q(p.Values[k].Trim())).ToArray()) +
                  ((p.Other ?? "").Trim().Length > 0 ? "   " + Other + "=" + Q(p.Other.Trim()) : ""));
        n = 0;
        foreach (var s in Run)
            c.Add("Step=" + (++n).ToString(Inv) + "   LoadList=" + Q(s.LoadList) + "   Action=" + Q(s.Action) + "   Set=" + Q(s.Set) + "   Members=" + Q(s.Members) + "   Units=" + Q(s.Units) + "   Text=" + Q(s.Text));
        return new[] { a, b, c };
    }

    // the three tables made again from this (none when empty); false when nothing changed
    public bool Store(FrameModel m)
    {
        var now = Rows(); var was = Of(m).Rows();
        if (Enumerable.Range(0, 3).All(k => now[k].SequenceEqual(was[k]))) return false;
        string[] names = { TDesign, TParams, TRun };
        for (int k = 0; k < 3; k++)
        {
            var t = m.Source.Table(names[k]);
            if (t != null) m.Source.Tables.Remove(t);
            if (now[k].Count == 0) continue;
            t = m.Source.AddTable(names[k], null);
            foreach (string text in now[k]) { var r = new S2kRow(); S2kRow.Tokenize(text, r); t.AddRow(r); }
        }
        return true;
    }

    // a model that still carries a .std's commands as written (an older workbook's STAAD commands sheet, an older
    // .frame.json): its design from them, the commands dropped. Their member lists name the model's labels
    public static bool Adopt(FrameModel m, List<string> notes)
    {
        var cmds = StaadCarry.CommandsOf(m);
        if (cmds.Count == 0) return false;
        var d = FromCommands(cmds, FrameStd.StaadUnits(m));
        d.Distribute(m, text => ByLabel(m, text), null, null);
        d.Store(m);
        StaadCarry.Set(m, new List<string>(), StaadCarry.LoadsOf(m));
        if (notes != null)
        {
            notes.Add("the model's STAAD commands (" + cmds.Count.ToString(Inv) + ") read as its STAAD design: " + d.Sets.Count.ToString(Inv) + " set(s), " +
                      m.Frames.Count(f => f.Rec(TMembers) != null).ToString(Inv) + " frame(s) with values, " + d.Run.Count.ToString(Inv) + " step(s)");
            notes.AddRange(d.Notes);
        }
        return true;
    }

    // a member list by the model's labels: ALL; a group (a leading _ is STAAD's) as its frames and links; frame and link
    // labels; ranges a TO b [BY c] of number labels
    public static StaadMembers ByLabel(FrameModel m, string text)
    {
        var o = new StaadMembers();
        var t = (text ?? "").Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
        if (t.Length == 0 || (t.Length == 1 && Is(t[0], "ALL"))) { o.All = true; return o; }
        var frames = new HashSet<string>(m.Frames.Select(f => f.Label), StringComparer.Ordinal);
        var links = new HashSet<string>(m.Links.Select(l => l.Label), StringComparer.Ordinal);
        var missing = new List<string>();
        Func<string, bool> one = l => { if (frames.Contains(l)) { if (!o.Frames.Contains(l)) o.Frames.Add(l); return true; } if (links.Contains(l)) { if (!o.Others.Contains(l)) o.Others.Add(l); return true; } return false; };
        for (int k = 0; k < t.Length; k++)
        {
            int a, b, by = 1;
            if (k + 2 < t.Length && Is(t[k + 1], "TO") && int.TryParse(t[k], NumberStyles.None, Inv, out a) && int.TryParse(t[k + 2], NumberStyles.None, Inv, out b))
            {
                k += 2;
                if (k + 2 < t.Length && Is(t[k + 1], "BY") && int.TryParse(t[k + 2], NumberStyles.None, Inv, out by) && by > 0) k += 2; else by = 1;
                int found = 0;
                for (int n = a; n <= b && n - a < 1000000; n += by) if (one(n.ToString(Inv))) found++;
                if (found == 0) missing.Add(a.ToString(Inv) + " TO " + b.ToString(Inv));
                continue;
            }
            string g = t[k].TrimStart('_');
            var grp = m.Groups.FirstOrDefault(x => Is(x.Label, g) || Is(x.Label, t[k]));
            if (grp != null)
            {
                int found = 0;
                foreach (var mm in grp.Members) if ((Is(mm[0], "Frame") || Is(mm[0], "Link")) && one(mm[1])) found++;
                if (found == 0) missing.Add(t[k] + " (a group with no member)");
                continue;
            }
            if (!one(t[k])) missing.Add(t[k]);
        }
        if (missing.Count > 0) o.Error = string.Join(", ", missing.ToArray()) + ": no such group or member in the model";
        return o;
    }

    // ---------------- from a .std's commands after the analysis (StaadInput.Output, the analysis' PRINT first as
    // "PERFORM ANALYSIS PRINT ..."). baseUnits: the model's UNIT words ("FEET KIP"). Every parameter is a by-list row
    // here, its members as the .std writes them: Distribute then gives the first set's to the frames

    public static StaadDesign FromCommands(List<string> cmds, string baseUnits)
    {
        var d = new StaadDesign();
        string unit = baseUnits ?? "", list = "";
        Set cur = null;
        bool afterCheck = false;
        foreach (string raw in cmds)
        {
            string c = Regex.Replace(raw ?? "", @"\s+", " ").Trim();
            var t = Tok(c);
            if (t.Length == 0) continue;
            string h = t[0].ToUpperInvariant();
            Match m;
            if ((m = Regex.Match(c, @"^(?:PERFORM\s+ANALYSIS|PDELTA\b.*?\bANALYSIS)\s+(PRINT\b.*)$", RegexOptions.IgnoreCase)).Success) { d.AnalysisPrint = m.Groups[1].Value.Substring(5).Trim(); continue; }
            if (h == "UNIT") { unit = Merge(unit, string.Join(" ", t.Skip(1).ToArray())); continue; }
            if (t.Length >= 2 && h == "LOAD" && Is(t[1], "LIST")) { list = string.Join(" ", t.Skip(2).ToArray()); continue; }
            if (h == "PARAMETER" || h == "PARAMETERS")
            {
                string name = t.Length > 1 ? t[1] : (d.Sets.Count + 1).ToString(Inv);
                cur = d.Sets.FirstOrDefault(s => s.Name == name);
                if (cur == null) { cur = new Set { Name = name, Units = unit }; d.Sets.Add(cur); }
                afterCheck = false;
                continue;
            }
            if (cur != null && h == "CODE") { cur.Code = string.Join(" ", t.Skip(1).ToArray()); continue; }
            if (cur != null && h == "METHOD") { cur.Method = string.Join(" ", t.Skip(1).ToArray()); continue; }
            if (t.Length >= 2 && h == "CHECK" && Is(t[1], "CODE"))
            {
                d.Run.Add(new Step { LoadList = list, Action = "CHECK CODE", Set = cur == null ? "" : cur.Name, Members = Members(t, 2) });
                afterCheck = true;
                continue;
            }
            if (h == "SELECT" && (t.Length == 1 || Is(t[1], "ALL") || Is(t[1], "MEMB") || Is(t[1], "MEMBER")))
            {
                d.Run.Add(new Step { LoadList = list, Action = "SELECT", Set = cur == null ? "" : cur.Name, Members = Members(t, 1) });
                afterCheck = true;
                continue;
            }
            // a parameter: NAME value [values] ALL | MEMB <list>, inside a set
            int at = Array.FindIndex(t, x => Is(x, "ALL") || Is(x, "MEMB") || Is(x, "MEMBER") || Is(x, "LIST"));
            if (cur != null && t.Length >= 3 && Number(t[1]) && at >= 2)
            {
                if (UnitKey(unit, "") != UnitKey(cur.Units, ""))
                    d.Notes.Add("PARAMETER " + cur.Name + ": " + c + " stands under UNIT " + unit + ", the set's first under " + cur.Units + ": written as given, in the set's units (check it)");
                if (afterCheck) d.Notes.Add("PARAMETER " + cur.Name + ": " + c + " comes after a check of the set: the design gives it to every check of the set");
                string members = Members(t, at);
                string name = t[0].ToUpperInvariant(), val = string.Join(" ", t.Skip(1).Take(at - 1).ToArray());
                bool column = Columns.Contains(name) && at == 2;
                // into the set's last row for these members, unless a row after it gives this parameter (STAAD takes the
                // last line: the order of two lines of one parameter stays)
                int last = d.Params.FindLastIndex(x => x.Set == cur.Name && x.Members == members);
                bool later = last >= 0 && d.Params.Skip(last + 1).Any(x => x.Set == cur.Name && Has(x, name));
                Param p;
                if (last < 0 || later) { p = new Param { Set = cur.Name, Members = members }; d.Params.Add(p); } else p = d.Params[last];
                Put(p, name, val, column);
                continue;
            }
            if (h == "PRINT") { d.Run.Add(new Step { LoadList = list, Action = "PRINT", Units = unit, Text = c }); continue; }
            d.Run.Add(new Step { LoadList = list, Action = "COMMAND", Units = unit, Text = c });
        }
        // a step's units: blank where they are the design's (the first step's, else the model's)
        string first = d.Run.Where(s => s.Units.Length > 0).Select(s => s.Units).FirstOrDefault() ?? baseUnits ?? "";
        d.Units = first;
        foreach (var s in d.Run) if (s.Units.Length > 0 && UnitKey(s.Units, "") == UnitKey(first, "")) s.Units = "";
        // a load list: blank where it is the row above's
        string prev = null;
        foreach (var s in d.Run) { if (prev != null && s.LoadList == prev) s.LoadList = ""; else prev = s.LoadList; }
        return d;
    }

    static bool Has(Param p, string name) { return p.Values.ContainsKey(name) || Others(p.Other).Any(kv => kv.Key == name); }
    static void Put(Param p, string name, string val, bool column)
    {
        if (column) { p.Values[name] = val; return; }
        var o = Others(p.Other).Where(kv => kv.Key != name).ToList();
        o.Add(new KeyValuePair<string, string>(name, val));
        p.Other = OthersText(o);
    }
    static void Drop(Param p, string name)
    {
        p.Values.Remove(name);
        p.Other = OthersText(Others(p.Other).Where(kv => kv.Key != name));
    }
    // a row's parameters: name, value, whether a column
    static List<string[]> Items(Param p)
    {
        var o = new List<string[]>();
        foreach (string c in Columns) { string v; if (p.Values.TryGetValue(c, out v) && v.Trim().Length > 0) o.Add(new[] { c, v.Trim(), "1" }); }
        foreach (var kv in Others(p.Other)) o.Add(new[] { kv.Key, kv.Value, "" });
        return o;
    }

    // a later UNIT's words over the one in force (UNIT INCHES keeps the force)
    static string Merge(string inForce, string words)
    {
        LengthUnit l; ForceUnit f;
        string len = null, force = null;
        foreach (string w in Tok(inForce)) { if (AnlUnitStamper.TryLength(w.ToUpperInvariant(), out l)) len = w; else if (AnlUnitStamper.TryForce(w.ToUpperInvariant(), out f)) force = w; }
        foreach (string w in Tok(words)) { if (AnlUnitStamper.TryLength(w.ToUpperInvariant(), out l)) len = w; else if (AnlUnitStamper.TryForce(w.ToUpperInvariant(), out f)) force = w; }
        return ((len ?? "") + " " + (force ?? "")).Trim().ToUpperInvariant();
    }

    // the members after t[from]: ALL, or the list after MEMB / MEMBER / LIST as written
    static string Members(string[] t, int from)
    {
        if (from >= t.Length || Is(t[from], "ALL")) return "ALL";
        int k = Is(t[from], "MEMB") || Is(t[from], "MEMBER") || Is(t[from], "LIST") ? from + 1 : from;
        string s = string.Join(" ", t.Skip(k).ToArray()).Trim();
        return s.Length == 0 ? "ALL" : s;
    }

    // ---------------- the first set's by-list rows onto the model's frames, as STAAD reads them (a later line of a
    // parameter wins): a value given to ALL is the set's default (its ALL row) and clears what came before it; a value
    // given to a list goes to each frame of it (its Frames row), and to a row of their own for its links.
    //   resolve   a row's members as the model has them (ByLabel; a .std's numbers and groups: StdToFrame)
    //   relabel   a member list in the model's labels, for the rows and steps that stay by list (null: as written)
    //   joint     a DJ1 / DJ2 value as the model's joint label (null: as written)
    public void Distribute(FrameModel m, Func<string, StaadMembers> resolve, Func<string, string> relabel, Func<string, string> joint)
    {
        if (joint != null)
            foreach (var p in Params)
                foreach (string dj in new[] { "DJ1", "DJ2" }) { string v; if (p.Values.TryGetValue(dj, out v) && v.Trim().Length > 0) p.Values[dj] = joint(v.Trim()); }
        if (relabel != null)
        {
            foreach (var s in Run) if ((s.Action == "CHECK CODE" || s.Action == "SELECT") && !Is(s.Members, "ALL") && s.Members.Trim().Length > 0) s.Members = relabel(s.Members);
        }
        if (Sets.Count == 0) return;
        string first = Sets[0].Name;
        var byFrame = m.Frames.ToDictionary(f => f.Label, f => f, StringComparer.Ordinal);
        var defaults = new Param { Set = first, Members = "ALL" };
        var kept = new List<Param>();                               // rows that stay by list: links, and lists the model cannot resolve
        var values = new Dictionary<string, Dictionary<string, string[]>>(StringComparer.Ordinal);     // frame -> name -> { value, column }
        foreach (var p in Params.Where(x => x.Set == first).ToList())
        {
            var who = resolve(p.Members);
            if (who.Error != null)
            {
                Notes.Add("PARAMETER " + first + ", members " + p.Members + ": " + who.Error + "; kept by list, as written (-Std refuses it)");
                kept.Add(p);
                continue;
            }
            foreach (var it in Items(p))
            {
                if (who.All)
                {
                    Put(defaults, it[0], it[1], it[2] == "1");
                    foreach (var fv in values.Values) fv.Remove(it[0]);
                    foreach (var k in kept) Drop(k, it[0]);
                    continue;
                }
                foreach (string f in who.Frames)
                {
                    Dictionary<string, string[]> fv;
                    if (!values.TryGetValue(f, out fv)) values[f] = fv = new Dictionary<string, string[]>(StringComparer.Ordinal);
                    fv[it[0]] = new[] { it[1], it[2] };
                }
                if (who.Others.Count > 0)
                {
                    string list = string.Join(" ", who.Others.ToArray());
                    int last = kept.FindLastIndex(x => x.Members == list);
                    Param row;
                    if (last < 0 || kept.Skip(last + 1).Any(x => Has(x, it[0]))) { row = new Param { Set = first, Members = list }; kept.Add(row); } else row = kept[last];
                    Put(row, it[0], it[1], it[2] == "1");
                }
            }
        }
        foreach (var kv in values)
        {
            FrameMember f;
            if (!byFrame.TryGetValue(kv.Key, out f)) continue;
            foreach (string c in Columns) { string[] v; SetMember(f, c, kv.Value.TryGetValue(c, out v) && v[1] == "1" ? v[0] : ""); }
            SetMember(f, Other, OthersText(kv.Value.Where(x => x.Value[1] != "1").Select(x => new KeyValuePair<string, string>(x.Key, x.Value[0]))));
        }
        var rest = Params.Where(x => x.Set != first).ToList();
        if (relabel != null) foreach (var p in rest) if (!Is(p.Members, "ALL")) p.Members = relabel(p.Members);
        Params.Clear();
        if (!defaults.Blank) Params.Add(defaults);
        Params.AddRange(kept.Where(k => !k.Blank));
        Params.AddRange(rest);
    }

    // ---------------- a starter for a model with none (Build-Frame -NewDesign): set 1 (AISC UNIFIED 2016, LRFD) in the
    // model's units; a Run of Pluto's own prints (what -Std writes with no design) and CHECK CODE ALL under set 1

    public static StaadDesign Template(FrameModel m, string units)
    {
        var d = new StaadDesign { Units = units ?? "" };
        d.Sets.Add(new Set { Name = "1", Code = "AISC UNIFIED 2016", Method = "LRFD", Units = units ?? "" });
        d.Run.Add(new Step { LoadList = "ALL", Action = "PRINT", Text = "PRINT JOINT DISPLACEMENTS LIST ALL" });
        if (m.Frames.Count + m.Links.Count > 0) d.Run.Add(new Step { Action = "PRINT", Text = "PRINT MEMBER FORCES ALL" });
        if (m.Joints.Any(j => j.Restraint.Any(b => b) || j.Spring.Any(v => v != 0))) d.Run.Add(new Step { Action = "PRINT", Text = "PRINT SUPPORT REACTIONS" });
        d.Run.Add(new Step { Action = "CHECK CODE", Set = "1", Members = "ALL" });
        return d;
    }

    // ---------------- back to STAAD's commands (FrameStd), names resolved by the writer:
    //   members(text, who) -> "ALL" or "MEMB <STAAD list>", null after refusing (the writer names why)
    //   frameId(label)     -> a frame's STAAD number (0: not written)
    //   joint(label, who)  -> a joint's STAAD number as text, null after refusing
    //   condense(ids)      -> STAAD's list of them ("1 TO 4 7")
    //   loads(text, who)   -> the LOAD LIST's words, null after refusing
    // baseUnits: the UNIT the analysis runs under (the model's). A set's block: its rows by list in order (ALL first: the
    // defaults), then, for the first set, the frames' own values, one line per parameter and value

    public List<string> ToCommands(string baseUnits, FrameModel m, Func<string, string, string> members, Func<string, int> frameId, Func<string, string, string> joint,
                                   Func<List<int>, string> condense, Func<string, string, string> loads, Action<string> refuse)
    {
        var o = new List<string>();
        string cur = baseUnits, list = null, lastSet = null, wanted = "";
        Func<string, string> unitFor = u => (u ?? "").Trim().Length > 0 ? u.Trim() : (Units.Trim().Length > 0 ? Units.Trim() : baseUnits);
        Action<string> unit = u => { if (UnitKey(u, "") != UnitKey(cur, "")) { o.Add("UNIT " + u.ToUpperInvariant()); cur = u; } };
        bool memberValues = HasMembers(m), written = false;
        if (memberValues && Sets.Count == 0) refuse("the Frames sheet has STAAD design values, and STAAD sets gives no set for them (its code and method)");
        int row = 0;
        foreach (var s in Run)
        {
            row++;
            string who = "STAAD run row " + (row + 1).ToString(Inv);
            if (s.LoadList.Trim().Length > 0) wanted = s.LoadList.Trim();
            string act = (s.Action ?? "").Trim().ToUpperInvariant();
            var set = act == "CHECK CODE" || act == "SELECT" ? Sets.FirstOrDefault(x => Is(x.Name, s.Set)) : null;
            // the row's UNIT first (a set's when its PARAMETER block is written here), then its LOAD LIST
            if (act == "PRINT" || act == "COMMAND") unit(unitFor(s.Units));
            else if (set != null && lastSet != set.Name) unit(unitFor(set.Units));
            if (wanted.Length > 0 && wanted != list)
            {
                string l = loads(wanted, who);                      // null: refused; the row's other names are still checked
                if (l != null) o.Add("LOAD LIST " + l);
                list = wanted;
            }
            if (act == "CHECK CODE" || act == "SELECT")
            {
                if (set == null) { refuse(who + ": parameter set \"" + s.Set + "\" is not on STAAD sets"); continue; }
                if (lastSet != set.Name)
                {
                    o.Add("PARAMETER " + set.Name);
                    if (set.Code.Trim().Length > 0) o.Add("CODE " + set.Code.Trim());
                    if (set.Method.Trim().Length > 0) o.Add("METHOD " + set.Method.Trim());
                    int prow = 0;
                    foreach (var p in Params)
                    {
                        prow++;
                        if (!Is(p.Set, set.Name)) continue;
                        string pwho = "STAAD parameters row " + (prow + 1).ToString(Inv);
                        string mm = members(p.Members, pwho);
                        if (mm == null) continue;
                        foreach (var it in Items(p))
                        {
                            string v = it[1];
                            if (it[0] == "DJ1" || it[0] == "DJ2") { v = joint(v, pwho + ", " + it[0]); if (v == null) continue; }
                            o.Add((it[0] + " " + v).Trim() + " " + mm);
                        }
                    }
                    if (set == Sets[0] && memberValues) { MemberLines(o, m, frameId, joint, condense, refuse); written = true; }
                    lastSet = set.Name;
                }
                string sm = members((s.Members ?? "").Trim().Length > 0 ? s.Members : "ALL", who);
                if (sm == null) continue;
                o.Add(act + " " + sm);
            }
            else if (act == "PRINT" || act == "COMMAND")
            {
                string text = (s.Text ?? "").Trim();
                if (text.Length == 0) { refuse(who + ": " + act + " with no text"); continue; }
                if (act == "PRINT" && !text.StartsWith("PRINT", StringComparison.OrdinalIgnoreCase)) text = "PRINT " + text;
                o.Add(text);
            }
            else refuse(who + ": action \"" + s.Action + "\" (CHECK CODE, SELECT, PRINT or COMMAND)");
        }
        if (memberValues && Sets.Count > 0 && !written)
            Notes.Add("the Frames sheet's STAAD design values are not written: STAAD run has no CHECK CODE or SELECT under set " + Sets[0].Name);
        return o;
    }

    // the frames' own values: per parameter, one line per value with the members that have it
    void MemberLines(List<string> o, FrameModel m, Func<string, int> frameId, Func<string, string, string> joint, Func<List<int>, string> condense, Action<string> refuse)
    {
        var lines = new List<string>(); var ids = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        Action<string, int> add = (text, id) => { List<int> l; if (!ids.TryGetValue(text, out l)) { ids[text] = l = new List<int>(); lines.Add(text); } l.Add(id); };
        foreach (string c in Columns)
            foreach (var f in m.Frames)
            {
                string v = Member(f, c);
                if (v.Length == 0) continue;
                int id = frameId(f.Label);
                if (id <= 0) continue;
                if (!Number(v) && c != "DJ1" && c != "DJ2") { refuse("Frames, frame " + f.Label + ": " + c + " \"" + v + "\" is not a number"); continue; }
                if (c == "DJ1" || c == "DJ2") { v = joint(v, "Frames, frame " + f.Label + ", " + c); if (v == null) continue; }
                add(c + " " + v, id);
            }
        foreach (var f in m.Frames)
        {
            int id = frameId(f.Label);
            if (id <= 0) continue;
            foreach (var kv in Others(Member(f, Other))) add((kv.Key + " " + kv.Value).Trim(), id);
        }
        foreach (string text in lines) o.Add(text + " MEMB " + condense(ids[text]));
    }
}
