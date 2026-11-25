using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

// FrameMerge -- a file SAP2000 or STAAD.Pro holds (an .s2k it exported, a .std it saved) read again into the frame
// model, keeping from the master (the workbook or .frame.json the file was written from) what that program has no place
// for (2026-11-24; Build-Frame -In <file> -Merge <master>; vault/format/frame-to-staad.md). The file's model is the
// newer: its program's edits win, and the master only fills in.
//   from an .s2k   the master's STAAD data: its design (StaadDesign: sets, parameters by list and run as they are; the
//                  frames' own values for the frames still there, by label); its load and envelope numbers and
//                  envelope types for the loads still there, by name (StaadCarry)
//   from a .std    the master's SAP-only data: the tables STAAD has no place for (FrameStd.StaadHolds false, not
//                  structural: design, preferences, mass source, functions, output stations, grids ...), their rows
//                  kept for the joints, frames, sections, materials, patterns and groups still there and whole where no
//                  object owns them; and the load cases and combinations STAAD has no place for (cases other than
//                  linear and nonlinear static, combinations of another type or of such a case) that the .std lacks,
//                  with all their rows. Structural SAP tables are never carried (-Std refuses them, so the .std's
//                  structure stands), nor links' rows, nor loads
// Done on the documents: the file's model's .s2k document gets the master's rows, and is read again. C# 5 (prefix Frame).
public static class FrameMerge
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static bool Is(string a, string b) { return string.Equals((a ?? "").Trim(), b, StringComparison.OrdinalIgnoreCase); }

    // kind: "s2k" or "std", the file's; notes gets what was carried and what was not
    public static FrameModel Merge(FrameModel file, FrameModel master, string kind, List<string> notes)
    {
        var doc = FrameS2k.ToDoc(file);
        var from = FrameS2k.ToDoc(master);
        var order = from.TableNames().ToList();
        if (kind == "s2k") Staad(doc, from, notes);
        else if (kind == "std") Sap(doc, from, order, notes);
        else throw new ArgumentException("FrameMerge: kind is s2k or std, not " + kind);
        return FrameS2k.Read(doc);
    }

    // ---------------- an .s2k: the master's STAAD data

    static void Staad(S2kDoc doc, S2kDoc from, List<string> notes)
    {
        string[] whole = { StaadDesign.TDesign, StaadDesign.TParams, StaadDesign.TRun };
        var members = from.Table(StaadDesign.TMembers);
        var loads = from.Table(StaadCarry.Loads);
        if (whole.All(n => from.Table(n) == null) && members == null && loads == null) { notes.Add("merge: the master has no STAAD data (no STAAD design, no STAAD load numbers): nothing to carry"); return; }
        foreach (string name in whole.Concat(new[] { StaadDesign.TMembers, StaadCarry.Loads })) { var t = doc.Table(name); if (t != null) doc.Tables.Remove(t); }
        // the design's sets, rows by list and run, as they are
        int rows = 0;
        foreach (string name in whole)
        {
            var src = from.Table(name);
            if (src == null) continue;
            var t = doc.AddTable(name, null);
            foreach (var r in src.Rows) { t.AddRow(r.Clone()); rows++; }
        }
        if (rows > 0) notes.Add("merge: the master's STAAD design carried (its sets, parameters by list and run, " + rows.ToString(Inv) + " rows; member and group names in them are not checked against SAP's edits)");
        // the frames' own design values, for the frames still there
        if (members != null)
        {
            var frames = Labels(doc, "CONNECTIVITY - FRAME", "Frame");
            var t = doc.AddTable(StaadDesign.TMembers, null);
            var gone = new List<string>();
            int kept = 0;
            foreach (var r in members.Rows)
            {
                string f = r.Get("Frame") ?? "";
                if (frames.Contains(f)) { t.AddRow(r.Clone()); kept++; } else gone.Add(f);
            }
            if (kept == 0) doc.Tables.Remove(t);
            notes.Add("merge: the master's STAAD design values carried for " + kept.ToString(Inv) + " frame(s), by label" +
                      (gone.Count > 0 ? "; not for " + string.Join(", ", gone.Take(12).ToArray()) + (gone.Count > 12 ? " ..." : "") + " (no longer in the model, or renamed)" : ""));
        }
        if (loads != null)
        {
            var cases = Labels(doc, "LOAD CASE DEFINITIONS", "Case");
            var combos = new Dictionary<string, string>(StringComparer.Ordinal);
            var ct = doc.Table("COMBINATION DEFINITIONS");
            if (ct != null) foreach (var r in ct.Rows) { string n = r.Get("ComboName") ?? "", ty = r.Get("ComboType"); if (!combos.ContainsKey(n) || ty != null) combos[n] = ty ?? combos[n]; }
            var t = doc.AddTable(StaadCarry.Loads, null);
            var dropped = new List<string>();
            int kept = 0;
            foreach (var r in loads.Rows)
            {
                string k = r.Get("Kind") ?? "", n = r.Get("Name") ?? "", ty;
                bool there = k == "Case" ? cases.Contains(n) : combos.TryGetValue(n, out ty) && (k == "Envelope") == Is(ty, "Envelope");
                if (there) { t.AddRow(r.Clone()); kept++; } else dropped.Add(k.ToLowerInvariant() + " " + n);
            }
            if (kept == 0) doc.Tables.Remove(t);
            notes.Add("merge: the master's STAAD numbers carried for " + kept.ToString(Inv) + " load(s), by name" +
                      (dropped.Count > 0 ? "; not for " + string.Join(", ", dropped.Take(12).ToArray()) + (dropped.Count > 12 ? " ..." : "") + " (no longer in the model, or renamed)" : ""));
        }
    }

    // ---------------- a .std: the master's SAP-only data

    static readonly Dictionary<string, string[]> Main = new Dictionary<string, string[]>
    {
        { "joint", new[] { "JOINT COORDINATES", "Joint" } }, { "frame", new[] { "CONNECTIVITY - FRAME", "Frame" } },
        { "section", new[] { "FRAME SECTION PROPERTIES 01 - GENERAL", "SectionName" } }, { "material", new[] { "MATERIAL PROPERTIES 01 - GENERAL", "Material" } },
        { "pattern", new[] { "LOAD PATTERN DEFINITIONS", "LoadPat" } }, { "group", new[] { "GROUPS 1 - DEFINITIONS", "GroupName" } },
        { "case", new[] { "LOAD CASE DEFINITIONS", "Case" } }, { "combo", new[] { "COMBINATION DEFINITIONS", "ComboName" } },
    };

    static void Sap(S2kDoc doc, S2kDoc from, List<string> order, List<string> notes)
    {
        var have = new Dictionary<string, HashSet<string>>();
        foreach (var kv in Main) have[kv.Key] = Labels(doc, kv.Value[0], kv.Value[1]);

        // the cases and combinations STAAD has no place for, absent from the .std: carried whole
        var caseType = new Dictionary<string, string>(StringComparer.Ordinal);
        var lc = from.Table("LOAD CASE DEFINITIONS");
        if (lc != null) foreach (var r in lc.Rows) { string n = r.Get("Case"); if (n != null && !caseType.ContainsKey(n)) caseType[n] = r.Get("Type") ?? ""; }
        var cases = new HashSet<string>(caseType.Where(kv => !have["case"].Contains(kv.Key) && !Is(kv.Value, "LinStatic") && !Is(kv.Value, "NonStatic")).Select(kv => kv.Key), StringComparer.Ordinal);
        var comboRows = new Dictionary<string, List<S2kRow>>(StringComparer.Ordinal);
        var cd = from.Table("COMBINATION DEFINITIONS");
        if (cd != null) foreach (var r in cd.Rows) { string n = r.Get("ComboName") ?? ""; List<S2kRow> l; if (!comboRows.TryGetValue(n, out l)) comboRows[n] = l = new List<S2kRow>(); l.Add(r); }
        var combos = new HashSet<string>(StringComparer.Ordinal);
        for (bool more = true; more; )
        {
            more = false;
            foreach (var kv in comboRows)
            {
                if (combos.Contains(kv.Key) || have["combo"].Contains(kv.Key)) continue;
                string ty = kv.Value.Select(r => r.Get("ComboType")).FirstOrDefault(x => x != null) ?? "";
                bool other = !new[] { "Linear Add", "SRSS", "Abs Add", "Envelope" }.Any(x => Is(ty, x));
                bool ofCarried = kv.Value.Any(r => cases.Contains(r.Get("CaseName") ?? "") || combos.Contains(r.Get("CaseName") ?? ""));
                if (other || ofCarried) { combos.Add(kv.Key); more = true; }
            }
        }
        // a carried combination whose items are not all in the model after the merge stays out
        foreach (string c in combos.ToList())
            foreach (var r in comboRows[c])
            {
                string item = r.Get("CaseName") ?? "";
                if (!(have["case"].Contains(item) || have["combo"].Contains(item) || cases.Contains(item) || combos.Contains(item))) { combos.Remove(c); notes.Add("merge: the master's combination " + c + " not carried: " + item + " is not in the model"); break; }
            }

        var carriedTables = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var lost = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var t in from.Tables)
        {
            string name = t.Name;
            if (StaadCarry.IsOwn(name)) continue;
            bool sapOnly = doc.Table(name) == null && !FrameStd.StaadHolds(name) && !FrameStd.IsStructural(name);
            S2kTable into = null;
            foreach (var r in t.Rows)
            {
                string kind; bool main;
                bool owned = FrameS2k.OwnerOf(name, r, out kind, out main);
                string label = owned && r.Values.Count > 0 ? r.Values[0] : null;
                bool take;
                if (owned && kind == "case") take = cases.Contains(label) || (sapOnly && have["case"].Contains(label));
                else if (owned && kind == "combo") take = combos.Contains(label);
                else if (!sapOnly) take = false;
                else if (!owned) take = true;
                else if (Main.ContainsKey(kind)) take = have[kind].Contains(label);
                else take = false;                                   // links, loads, PROGRAM CONTROL: the .std's
                if (!take) { if (sapOnly) { int n0; lost.TryGetValue(name, out n0); lost[name] = n0 + 1; } continue; }
                if (into == null) into = doc.Table(name) ?? doc.AddTable(name, order);
                into.AddRow(r.Clone());
                if (!sapOnly) continue;                              // a carried case's or combination's row
                int n1; carriedTables.TryGetValue(name, out n1); carriedTables[name] = n1 + 1;
            }
        }
        if (cases.Count + combos.Count > 0)
            notes.Add("merge: the master's load cases and combinations STAAD has no place for, carried: " + string.Join(", ", cases.Concat(combos).Take(12).ToArray()) + (cases.Count + combos.Count > 12 ? " ..." : ""));
        if (carriedTables.Count > 0)
            notes.Add("merge: the master's SAP-only tables carried: " + string.Join(", ", carriedTables.Select(kv => kv.Key + " (" + kv.Value.ToString(Inv) + ")").ToArray()));
        else notes.Add("merge: the master has no SAP-only table to carry");
        if (lost.Count > 0)
            notes.Add("merge: SAP-only rows not carried, their object no longer in the model (or a link's): " + string.Join(", ", lost.Select(kv => kv.Key + " (" + kv.Value.ToString(Inv) + ")").ToArray()));
        var structural = from.Tables.Where(x => FrameStd.IsStructural(x.Name) && !FrameStd.StaadHolds(x.Name) && x.Rows.Any()).Select(x => x.Name).ToList();
        if (structural.Count > 0)
            notes.Add("merge: the master's structural SAP tables not carried (STAAD's model stands): " + string.Join(", ", structural.ToArray()));
    }

    static HashSet<string> Labels(S2kDoc doc, string table, string key)
    {
        var s = new HashSet<string>(StringComparer.Ordinal);
        var t = doc.Table(table);
        if (t != null) foreach (var r in t.Rows) { string v = r.Get(key); if (v != null) s.Add(v); }
        return s;
    }
}
