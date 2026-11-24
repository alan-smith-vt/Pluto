using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

// StaadDesign -- a model's STAAD design and output requests (2026-11-24; vault/arms/staad-design-workbook.md), apart
// from the structure and loads: the design workbook (<model>.design.xlsx, StaadDesignWorkbook) holds it, -Std writes it
// after the analysis (FrameStd), -In <x>.std reads it from the commands after the analysis (FromCommands).
//   Codes       one row per parameter set (STAAD's PARAMETER n): its CODE, METHOD and the UNIT its values are in
//   Parameters  one row per set and members (ALL, a group, a member list, or both): a column per parameter (FYLD FU KY KZ
//               LY LZ UNB UNT CB DFF DJ1 DJ2 NSF RATIO MAIN TRACK BEAM), and Other for any other ("STP 1; TORSION 1")
//   Run         what STAAD does after the analysis, in order: CHECK CODE or SELECT under a set, PRINT, or a COMMAND as
//               written; each under a LOAD LIST (blank: the row above's) and a UNIT (blank: the workbook's Units)
// Members name the model as the workbook does: its groups (as on the Groups sheet; a leading _ is STAAD's), its frame
// and link labels, ranges a TO b of number labels. -Std writes them as STAAD's numbers and group names; a name the model
// does not have is refused. Load lists name STAAD's load numbers (the model workbook's STAAD loads sheet), ENV and the
// envelope numbers, or ALL. C# 5 (prefix Staad).
public sealed class StaadDesign
{
    public static readonly string[] Columns = { "FYLD", "FU", "KY", "KZ", "LY", "LZ", "UNB", "UNT", "CB", "DFF", "DJ1", "DJ2", "NSF", "RATIO", "MAIN", "TRACK", "BEAM" };
    public static readonly string[] Actions = { "CHECK CODE", "SELECT", "PRINT", "COMMAND" };

    public sealed class Set { public string Name = "", Code = "", Method = "", Units = ""; }
    public sealed class Param
    {
        public string Set = "", Members = "ALL", Other = "";
        public readonly Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }
    public sealed class Step { public string LoadList = "", Action = "", Set = "", Members = "", Units = "", Text = ""; }

    public string Units = "";             // the UNIT prints and commands are in (blank: the model's)
    public string AnalysisPrint = "";     // PERFORM ANALYSIS's PRINT option (STATICS CHECK ...)
    public readonly List<Set> Sets = new List<Set>();
    public readonly List<Param> Params = new List<Param>();
    public readonly List<Step> Run = new List<Step>();
    public readonly List<string> Notes = new List<string>();

    static bool Is(string a, string b) { return string.Equals((a ?? "").Trim(), b, StringComparison.OrdinalIgnoreCase); }
    static string[] Tok(string s) { return (s ?? "").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries); }
    static bool Number(string s) { double v; return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v); }

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

    // ---------------- from a .std's commands after the analysis (StaadInput.Output, the analysis' PRINT first as
    // "PERFORM ANALYSIS PRINT ...", StdToFrame's carried list). baseUnits: the model's UNIT words ("FEET KIP")

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
                string name = t.Length > 1 ? t[1] : (d.Sets.Count + 1).ToString(CultureInfo.InvariantCulture);
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
                if (afterCheck) d.Notes.Add("PARAMETER " + cur.Name + ": " + c + " comes after a check of the set: the design workbook gives it to every check of the set");
                string members = Members(t, at);
                var p = d.Params.FirstOrDefault(x => x.Set == cur.Name && x.Members == members);
                if (p == null) { p = new Param { Set = cur.Name, Members = members }; d.Params.Add(p); }
                string name = t[0].ToUpperInvariant(), val = string.Join(" ", t.Skip(1).Take(at - 1).ToArray());
                if (Columns.Contains(name) && at == 2) p.Values[name] = val;
                else p.Other = (p.Other.Length > 0 ? p.Other + "; " : "") + name + " " + val;
                continue;
            }
            if (h == "PRINT") { d.Run.Add(new Step { LoadList = list, Action = "PRINT", Units = unit, Text = c }); continue; }
            d.Run.Add(new Step { LoadList = list, Action = "COMMAND", Units = unit, Text = c });
        }
        // a step's units: blank where they are the workbook's (the first step's, else the model's)
        string first = d.Run.Where(s => s.Units.Length > 0).Select(s => s.Units).FirstOrDefault() ?? baseUnits ?? "";
        d.Units = first;
        foreach (var s in d.Run) if (s.Units.Length > 0 && UnitKey(s.Units, "") == UnitKey(first, "")) s.Units = "";
        // a load list: blank where it is the row above's
        string prev = null;
        foreach (var s in d.Run) { if (prev != null && s.LoadList == prev) s.LoadList = ""; else prev = s.LoadList; }
        return d;
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

    // ---------------- back to STAAD's commands (FrameStd), names resolved by the writer:
    //   members(text, who) -> "ALL" or "MEMB <STAAD list>", null after refusing (the writer names why)
    //   loads(text, who)   -> the LOAD LIST's words, null after refusing
    // baseUnits: the UNIT the analysis runs under (the model's)

    public List<string> ToCommands(string baseUnits, Func<string, string, string> members, Func<string, string, string> loads, Action<string> refuse)
    {
        var o = new List<string>();
        string cur = baseUnits, list = null, lastSet = null, wanted = "";
        Func<string, string> unitFor = u => (u ?? "").Trim().Length > 0 ? u.Trim() : (Units.Trim().Length > 0 ? Units.Trim() : baseUnits);
        Action<string> unit = u => { if (UnitKey(u, "") != UnitKey(cur, "")) { o.Add("UNIT " + u.ToUpperInvariant()); cur = u; } };
        int row = 0;
        foreach (var s in Run)
        {
            row++;
            string who = "the design workbook's Run row " + (row + 1).ToString(CultureInfo.InvariantCulture);
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
                if (set == null) { refuse(who + ": parameter set \"" + s.Set + "\" is not on Codes"); continue; }
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
                        string pwho = "the design workbook's Parameters row " + (prow + 1).ToString(CultureInfo.InvariantCulture);
                        string mm = members(p.Members, pwho);
                        if (mm == null) continue;
                        foreach (string col in Columns) { string v; if (p.Values.TryGetValue(col, out v) && v.Trim().Length > 0) o.Add(col + " " + v.Trim() + " " + mm); }
                        foreach (string other in (p.Other ?? "").Split(';').Select(x => x.Trim()).Where(x => x.Length > 0)) o.Add(other + " " + mm);
                    }
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
        return o;
    }
}
