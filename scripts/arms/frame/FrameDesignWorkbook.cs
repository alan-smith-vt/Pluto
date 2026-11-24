using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

// StaadDesignWorkbook -- a model's STAAD design workbook, <model>.design.xlsx (2026-11-24;
// vault/arms/staad-design-workbook.md): StaadDesign as sheets, beside the frame workbook that holds the structure and
// loads. Build-Frame -In <x>.std -Xlsx writes it from the .std's commands after the analysis; -Std reads it (-Design, or
// the one beside the model workbook). Edited in Excel; read back by header names, every error named by its cell.
//   Start       what it is, how it runs, the sheets
//   Settings    Units (the UNIT prints and commands are in), Analysis print (PERFORM ANALYSIS PRINT ...), Model
//   Codes       Set, Code, Method, Units
//   Parameters  Set, Members, FYLD FU KY KZ LY LZ UNB UNT CB DFF DJ1 DJ2 NSF RATIO MAIN TRACK BEAM, Other
//   Run         Load list, Action, Set, Members, Units, Text
// C# 5 (prefix Staad).
public static class StaadDesignWorkbook
{
    public const string Format = "pluto-staad-design";
    public const int Version = 1;
    const string SSettings = "Settings", SCodes = "Codes", SParams = "Parameters", SRun = "Run";
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    static readonly string[][] Groups =
    {
        new[] { "key", "37474F", "What a row applies to: its set, its members" },
        new[] { "design", "4A148C", "Design: codes and parameters" },
        new[] { "run", "1565C0", "What STAAD does after the analysis, in order" },
        new[] { "ro", "9E9E9E", "Read-only" },
    };
    static readonly string[] CodeList = { "AISC UNIFIED 2022", "AISC UNIFIED 2016", "AISC UNIFIED 2010", "AISC UNIFIED 2005", "AISC", "LRFD", "AISI", "ASCE", "API" };
    static readonly string[] MethodList = { "LRFD", "ASD" };

    static WbCol C(string name, string group, string kind, string hint) { return new WbCol(name, group, kind, null, hint, null); }
    static WbCol L(string name, string group, string list, string hint) { return new WbCol(name, group, "list?", list, hint, null); }
    static string ListRange(int col, int n) { string c = XlsxBook.Column(col); return "Lists!$" + c + "$2:$" + c + "$" + (1 + n).ToString(Inv); }
    static void Value(XlsxBookSheet s, int r, int c, string v)
    {
        double x;
        if (string.IsNullOrEmpty(v)) return;
        if (double.TryParse(v, NumberStyles.Float, Inv, out x) && x.ToString("R", Inv) == v.Trim()) s.Set(r, c, x); else s.Set(r, c, v);
    }

    public static void Write(StaadDesign d, string path, string modelName) { File.WriteAllBytes(path, Book(d, modelName).ToBytes()); }

    public static XlsxBook Book(StaadDesign d, string modelName)
    {
        var b = new XlsxBook();
        var t = WbTheme.Make(b, Groups);
        int lim = 2000;
        t.Form = new WbForm { RefRows = lim };

        // ---- Start
        var st = b.Add("Start");
        st.ShowGrid = false; st.TabColor = "1F2937"; st.Width(0, 110);
        var lines = new List<string>
        {
            "STAAD design workbook" + (string.IsNullOrEmpty(modelName) ? "" : " for " + modelName),
            "",
            "What STAAD does after the analysis: its design parameters and checks, and the prints. The structure and loads are in the model's frame workbook;",
            "this one names its groups, members and STAAD load numbers (the model workbook's STAAD loads sheet).",
            "",
            "Build-Frame.ps1 -In <model>.xlsx -Std writes the .std with it: the design workbook beside the model workbook (<model>.design.xlsx), or the one -Design names.",
            "A group, member or load the model does not have is refused, by its cell. SAP takes none of this (-S2k).",
            "",
            "Codes       one row per parameter set (STAAD's PARAMETER n): its CODE, METHOD, and the UNIT its parameter values are in.",
            "Parameters  one row per set and members: ALL, a group (as on the model's Groups sheet), member labels, ranges like 1 TO 4, or a mix.",
            "            A column per parameter; a blank cell writes nothing. Other takes any other: \"STP 1; TORSION 1\".",
            "Run         in order: CHECK CODE or SELECT (under a set, for its members: blank is ALL), PRINT (its text: SUPPORT REACTIONS ...),",
            "            or COMMAND (any STAAD command, as written). Load list: ALL, load numbers and ranges, or ENV and envelope numbers;",
            "            blank keeps the row above's. Units: the UNIT for a PRINT or COMMAND row (blank: Settings' Units).",
            "",
            "A set's PARAMETER block is written before the first CHECK CODE or SELECT that uses it, and again when another set came between.",
        };
        for (int i = 0; i < lines.Count; i++) st.Set(i, 0, lines[i], i == 0 ? XlsxBook.Bold : 0);

        // ---- Settings
        var se = b.Add(SSettings);
        se.Set(0, 0, "Setting", t.Head["ro"]); se.Set(0, 1, "Value", t.Head["design"]); se.Set(0, 2, "Note", t.Head["ro"]);
        se.Width(0, 18); se.Width(1, 30); se.Width(2, 90);
        se.Set(1, 0, "Units", XlsxBook.Bold); se.Set(1, 1, d.Units ?? ""); se.Set(1, 2, "The UNIT prints and commands are in (FEET KIP, INCHES KIP, METER KN ...); blank: the model's.", XlsxBook.Note);
        se.Set(2, 0, "Analysis print", XlsxBook.Bold); se.Set(2, 1, d.AnalysisPrint ?? ""); se.Set(2, 2, "PERFORM ANALYSIS's PRINT option (STATICS CHECK, LOAD DATA, ALL ...); blank: none.", XlsxBook.Note);
        se.Set(3, 0, "Model", XlsxBook.Bold); se.Set(3, 1, modelName ?? "", XlsxBook.ReadOnly); se.Set(3, 2, "The model workbook it was written with (a record).", XlsxBook.Note);

        // ---- Lists (hidden)
        var li = b.Add("Lists");
        li.State = "hidden";
        var lists = new[] { StaadDesign.Actions, CodeList, MethodList };
        var names = new[] { "Actions", "Codes", "Methods" };
        for (int k = 0; k < lists.Length; k++) { li.Set(0, k, names[k], XlsxBook.Bold); for (int i = 0; i < lists[k].Length; i++) li.Set(i + 1, k, lists[k][i]); }

        // ---- Codes
        var co = b.Add(SCodes);
        int r0 = 1;
        foreach (var s in d.Sets) { Value(co, r0, 0, s.Name); co.Set(r0, 1, s.Code); co.Set(r0, 2, s.Method); co.Set(r0, 3, s.Units); r0++; }
        t.Form.Table(co, t, new[] { "Set", "Code", "Method", "Units" }, new[] {
            C("Set", "key", "text", "The set's name, written as PARAMETER <set> (1, 2 ...). Run's Set names it."),
            L("Code", "design", ListRange(1, CodeList.Length), "STAAD's CODE: AISC UNIFIED 2016, AISC UNIFIED 2010 ..."),
            L("Method", "design", ListRange(2, MethodList.Length), "LRFD or ASD (blank: the code's default)."),
            C("Units", "design", "text", "The UNIT its parameter values are in (INCHES KIP ...); blank: Settings' Units."),
        }, d.Sets.Count, lim);
        co.Width(0, 8); co.Width(1, 24); co.Width(2, 10); co.Width(3, 16);

        // ---- Parameters
        var pa = b.Add(SParams);
        var heads = new List<string> { "Set", "Members" };
        heads.AddRange(StaadDesign.Columns);
        heads.Add("Other");
        r0 = 1;
        foreach (var p in d.Params)
        {
            Value(pa, r0, 0, p.Set); pa.Set(r0, 1, p.Members);
            for (int k = 0; k < StaadDesign.Columns.Length; k++) { string v; if (p.Values.TryGetValue(StaadDesign.Columns[k], out v)) Value(pa, r0, 2 + k, v); }
            pa.Set(r0, 2 + StaadDesign.Columns.Length, p.Other ?? "");
            r0++;
        }
        var pcols = new List<WbCol> {
            C("Set", "key", "text", "The parameter set (Codes)."),
            C("Members", "key", "text", "ALL, a group as on the model's Groups sheet (COLUMNS, or STAAD's _COLUMNS), member labels, ranges (1 TO 4), or a mix."),
        };
        foreach (string col in StaadDesign.Columns) pcols.Add(C(col, "design", "text", "STAAD's " + col + " for these members, in the set's units; blank: not written."));
        pcols.Add(C("Other", "design", "text", "Any other parameter: NAME value, several separated by ;"));
        t.Form.Table(pa, t, heads.ToArray(), pcols.ToArray(), d.Params.Count, lim);
        pa.Width(0, 6); pa.Width(1, 22); for (int k = 0; k < StaadDesign.Columns.Length; k++) pa.Width(2 + k, 8); pa.Width(heads.Count - 1, 30);
        pa.FreezeCols = 2;

        // ---- Run
        var ru = b.Add(SRun);
        r0 = 1;
        foreach (var s in d.Run) { ru.Set(r0, 0, s.LoadList); ru.Set(r0, 1, s.Action); Value(ru, r0, 2, s.Set); ru.Set(r0, 3, s.Members); ru.Set(r0, 4, s.Units); ru.Set(r0, 5, s.Text); r0++; }
        t.Form.Table(ru, t, new[] { "Load list", "Action", "Set", "Members", "Units", "Text" }, new[] {
            C("Load list", "run", "text", "ALL, STAAD load numbers and ranges (101 TO 103), or ENV and envelope numbers (ENV 1 2); blank: the row above's."),
            L("Action", "run", ListRange(0, StaadDesign.Actions.Length), "CHECK CODE, SELECT, PRINT or COMMAND."),
            C("Set", "run", "text", "CHECK CODE / SELECT: the parameter set (Codes)."),
            C("Members", "run", "text", "CHECK CODE / SELECT: the members (as on Parameters); blank: ALL."),
            C("Units", "run", "text", "PRINT / COMMAND: the UNIT it is in; blank: Settings' Units."),
            C("Text", "run", "text", "PRINT: what to print (PRINT SUPPORT REACTIONS); COMMAND: the STAAD command as written."),
        }, d.Run.Count, lim);
        ru.Width(0, 16); ru.Width(1, 13); ru.Width(2, 6); ru.Width(3, 22); ru.Width(4, 13); ru.Width(5, 60);

        var pl = b.Add("_pluto");
        pl.State = "veryHidden";
        pl.Set(0, 0, Format); pl.Set(0, 1, Version);
        return b;
    }

    // ---------------- read

    sealed class Ctx : WbReadCtx { }

    // the design, or null with errors (each by its cell); warnings: columns not read
    public static StaadDesign Read(string path, List<string> errors, List<string> warnings)
    {
        var sheets = CaesarWorkbook.Read(path);
        Func<string, CaesarSheet> sheet = n => sheets.FirstOrDefault(s => string.Equals(s.Name, n, StringComparison.OrdinalIgnoreCase));
        var pl = sheet("_pluto");
        if (pl == null || pl.Text(0, 0) != Format) { errors.Add(Path.GetFileName(path) + " is not a STAAD design workbook (Build-Frame writes one with -In <x>.std -Xlsx)"); return null; }
        var x = new Ctx();
        var d = new StaadDesign();
        Func<WbCols, int, string, string> text = (c, r, col) => WbRead.Text(c, r, col, x).Trim();
        Func<WbCols, int, string> at = (c, r) => c.S.Name + "!row " + (r + 1).ToString(Inv);
        Func<WbCols, IEnumerable<int>> rows = c => Enumerable.Range(1, Math.Max(0, c.Rows - 1)).Where(r => !WbRead.Blank(c.S, r));

        var se = sheet(SSettings);
        if (se != null)
            for (int r = 1; r < se.Rows.Count; r++)
            {
                string k = se.Text(r, 0);
                if (k == "Units") d.Units = se.Text(r, 1);
                else if (k == "Analysis print") d.AnalysisPrint = System.Text.RegularExpressions.Regex.Replace(se.Text(r, 1), @"^PRINT\s+", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }
        var cs = sheet(SCodes);
        if (cs != null)
        {
            var c = WbRead.ColsOf(cs, x, new[] { "Set", "Code", "Method", "Units" });
            foreach (int r in rows(c))
            {
                var s = new StaadDesign.Set { Name = text(c, r, "Set"), Code = text(c, r, "Code"), Method = text(c, r, "Method"), Units = text(c, r, "Units") };
                if (s.Name.Length == 0) { x.Err(at(c, r) + ": a set with no name"); continue; }
                if (d.Sets.Any(y => y.Name == s.Name)) { x.Err(at(c, r) + ": set " + s.Name + " twice"); continue; }
                d.Sets.Add(s);
            }
        }
        var ps = sheet(SParams);
        if (ps != null)
        {
            var known = new List<string> { "Set", "Members", "Other" }; known.AddRange(StaadDesign.Columns);
            var c = WbRead.ColsOf(ps, x, known.ToArray());
            foreach (int r in rows(c))
            {
                var p = new StaadDesign.Param { Set = text(c, r, "Set"), Members = text(c, r, "Members"), Other = text(c, r, "Other") };
                if (p.Members.Length == 0) p.Members = "ALL";
                if (!d.Sets.Any(y => y.Name == p.Set)) { x.Err(at(c, r) + ": set \"" + p.Set + "\" is not on Codes"); continue; }
                foreach (string col in StaadDesign.Columns)
                {
                    string v = text(c, r, col);
                    if (v.Length == 0) continue;
                    double num;
                    if (!v.Split(' ').All(w => double.TryParse(w, NumberStyles.Float, Inv, out num))) { x.Err(at(c, r) + ", " + col + ": \"" + v + "\" is not a number"); continue; }
                    p.Values[col] = v;
                }
                d.Params.Add(p);
            }
        }
        var rs = sheet(SRun);
        if (rs != null)
        {
            var c = WbRead.ColsOf(rs, x, new[] { "Load list", "Action", "Set", "Members", "Units", "Text" });
            foreach (int r in rows(c))
            {
                var s = new StaadDesign.Step { LoadList = text(c, r, "Load list"), Action = text(c, r, "Action").ToUpperInvariant(), Set = text(c, r, "Set"), Members = text(c, r, "Members"), Units = text(c, r, "Units"), Text = text(c, r, "Text") };
                if (!StaadDesign.Actions.Contains(s.Action)) { x.Err(at(c, r) + ": action \"" + s.Action + "\" (CHECK CODE, SELECT, PRINT or COMMAND)"); continue; }
                if ((s.Action == "CHECK CODE" || s.Action == "SELECT") && !d.Sets.Any(y => y.Name == s.Set)) { x.Err(at(c, r) + ": set \"" + s.Set + "\" is not on Codes"); continue; }
                if ((s.Action == "PRINT" || s.Action == "COMMAND") && s.Text.Length == 0) { x.Err(at(c, r) + ": " + s.Action + " with no Text"); continue; }
                d.Run.Add(s);
            }
        }
        errors.AddRange(x.Errors);
        warnings.AddRange(x.Warnings);
        return x.Errors.Count > 0 ? null : d;
    }
}
