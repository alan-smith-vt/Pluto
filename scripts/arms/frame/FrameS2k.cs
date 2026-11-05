using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

// FrameS2k  --  .s2k <-> FrameModel (2026-11-05), overlay and carry as the CAESAR writer (vault/decisions/
// 2026-10-22-lossless-pipe-model.md):
//   Read     every row goes to the object that owns it: a table's rows are owned by the joint / frame / section /
//            material / link property / link / group / load pattern / case / combination its key field names
//            (OwnerOf); each joint or frame load row is an object of its own; rows nobody owns (areas, solids,
//            constraints, analysis options, rows naming nothing) stay in the source only. Uids are given in file
//            order, so reading the same file twice gives the same uids.
//   ToDoc    the source again, with the model's edits overlaid: the base model is the source read again; each object
//            is matched to its base by uid and each of its rows to the base row at the same place in the same table.
//            A field whose text is unchanged is not touched (the row stays as written, continuation lines and all);
//            a changed field is rewritten in its row; a new row goes after the object's other rows in that table (a
//            new table goes where SAP's alphabetical order puts it); a row the object no longer has is removed, and a
//            deleted object's rows with it. Rows nobody owns follow renames (a field naming a renamed joint, frame,
//            section ... takes the new name); one whose key names a deleted object goes too; one that only uses a
//            deleted object (an area on a deleted joint) refuses the write.
// An unedited model writes its source back byte for byte. C# 5, one global batch (prefix Frame).

public static class FrameS2k
{
    // ---------------- read

    public static FrameModel Load(string path) { return Read(S2kDoc.Load(path)); }

    public static FrameModel Read(S2kDoc doc)
    {
        var m = new FrameModel { Source = doc };
        var byKey = new Dictionary<string, FrameObj>(StringComparer.Ordinal);   // kind + "\n" + label
        // main rows first (in file order), then the rows that attach to them
        for (int pass = 0; pass < 2; pass++)
            foreach (var t in doc.Tables)
                foreach (var row in t.Rows)
                {
                    string kind; bool main;
                    if (!OwnerOf(t.Name, row, out kind, out main)) continue;
                    if (kind == "control") { if (pass == 0 && m.Control == null) m.Control = Rec(t.Name, row); continue; }
                    if (kind == "load")
                    {
                        if (pass != 0) continue;
                        var ld = new FrameLoad { Table = t.Name, Uid = m.NextUid++ };
                        ld.Recs.Add(Rec(t.Name, row));
                        m.Loads.Add(ld);
                        continue;
                    }
                    string keyField = KeyFieldOf(kind);
                    string label = row.Get(keyField);
                    if (label == null) continue;
                    FrameObj o;
                    byKey.TryGetValue(kind + "\n" + label, out o);
                    if (pass == 0)
                    {
                        if (!main) continue;
                        if (o == null)
                        {
                            o = New(kind);
                            o.Uid = m.NextUid++;
                            m.Attach(o);
                            byKey[kind + "\n" + label] = o;
                        }
                        o.Recs.Add(Rec(t.Name, row));      // a combination's further rows join it
                        continue;
                    }
                    if (main || o == null) continue;       // second pass: only rows attaching to an object
                    o.Recs.Add(Rec(t.Name, row));
                }
        if (m.Control == null) m.Control = new FrameRec("PROGRAM CONTROL").Set("ProgramName", "SAP2000");
        return m;
    }

    static FrameRec Rec(string table, S2kRow row)
    {
        var r = new FrameRec(table) { Row = row };
        for (int i = 0; i < row.Keys.Count; i++) r.F.Add(new[] { row.Keys[i], row.Values[i] });
        return r;
    }

    static FrameObj New(string kind)
    {
        switch (kind)
        {
            case "joint": return new FrameJoint();
            case "frame": return new FrameMember();
            case "section": return new FrameSection();
            case "material": return new FrameMaterial();
            case "linkprop": return new FrameLinkProp();
            case "link": return new FrameLink();
            case "group": return new FrameGroup();
            case "pattern": return new FramePattern();
            case "case": return new FrameCase();
            case "combo": return new FrameCombo();
        }
        throw new ArgumentException("FrameS2k: no object kind " + kind);
    }

    public static string KeyFieldOf(string kind)
    {
        switch (kind)
        {
            case "joint": return "Joint";
            case "frame": return "Frame";
            case "section": return "SectionName";
            case "material": return "Material";
            case "linkprop": return "Link";
            case "link": return "Link";
            case "group": return "GroupName";
            case "pattern": return "LoadPat";
            case "case": return "Case";
            case "combo": return "ComboName";
        }
        return "";
    }

    // which object owns a row of this table, and whether this table defines it (its main table)
    public static bool OwnerOf(string table, S2kRow row, out string kind, out bool main)
    {
        kind = null; main = false;
        string t = table.ToUpperInvariant();
        string first = row.Keys.Count > 0 ? row.Keys[0] : "";
        Func<string, bool> key = k => string.Equals(first, k, StringComparison.OrdinalIgnoreCase);
        if (t == "PROGRAM CONTROL") { kind = "control"; return true; }
        if (t == "JOINT COORDINATES") { kind = "joint"; main = true; return key("Joint"); }
        if (t.StartsWith("JOINT LOADS")) { kind = "load"; return key("Joint"); }
        if (t.StartsWith("FRAME LOADS")) { kind = "load"; return key("Frame"); }
        if (t.StartsWith("JOINT ") && key("Joint")) { kind = "joint"; return true; }
        if (t.StartsWith("MATERIAL PROPERTIES") && key("Material")) { kind = "material"; main = t.StartsWith("MATERIAL PROPERTIES 01"); return true; }
        if (t.StartsWith("FRAME SECTION PROPERTIES") && key("SectionName")) { kind = "section"; main = t.StartsWith("FRAME SECTION PROPERTIES 01"); return true; }
        if (t == "CONNECTIVITY - FRAME") { kind = "frame"; main = true; return key("Frame"); }
        if (t.StartsWith("FRAME ") && key("Frame")) { kind = "frame"; return true; }
        if (t.StartsWith("LINK PROPERTY DEFINITIONS") && key("Link")) { kind = "linkprop"; main = t.StartsWith("LINK PROPERTY DEFINITIONS 01"); return true; }
        if (t == "CONNECTIVITY - LINK") { kind = "link"; main = true; return key("Link"); }
        if (t.StartsWith("LINK ") && key("Link")) { kind = "link"; return true; }
        if (t.StartsWith("GROUPS ") && key("GroupName")) { kind = "group"; main = t.StartsWith("GROUPS 1"); return true; }
        if (t == "LOAD PATTERN DEFINITIONS") { kind = "pattern"; main = true; return key("LoadPat"); }
        if (key("LoadPat") && !t.Contains(" LOADS")) { kind = "pattern"; return true; }   // auto wind / seismic data of a pattern
        if (t == "LOAD CASE DEFINITIONS") { kind = "case"; main = true; return key("Case"); }
        if (t.StartsWith("CASE - ") && key("Case")) { kind = "case"; return true; }
        if (t == "COMBINATION DEFINITIONS") { kind = "combo"; main = true; return key("ComboName"); }
        return false;
    }

    // ---------------- write

    public static string ToText(FrameModel m) { return ToDoc(m).ToText(); }
    public static byte[] ToBytes(FrameModel m) { return ToDoc(m).ToBytes(); }
    public static void Write(FrameModel m, string path) { File.WriteAllBytes(path, ToBytes(m)); }

    public static S2kDoc ToDoc(FrameModel m) { var notes = new List<string>(); return ToDoc(m, notes); }

    public static S2kDoc ToDoc(FrameModel m, List<string> notes)
    {
        var bm = Read(m.Source);
        var map = new Dictionary<S2kRow, S2kRow>();
        var doc = m.Source.Clone(map);
        var owned = new HashSet<S2kRow>();
        foreach (var o in bm.All()) foreach (var r in o.Recs) if (r.Row != null) owned.Add(map[r.Row]);
        if (bm.Control != null && bm.Control.Row != null) owned.Add(map[bm.Control.Row]);

        // PROGRAM CONTROL
        var added = new List<string>();     // tables given new rows
        Reconcile(doc, map, bm.Control == null ? new List<FrameRec>() : new List<FrameRec> { bm.Control }, m.Control == null ? new List<FrameRec>() : new List<FrameRec> { m.Control }, added);

        var baseByUid = new Dictionary<int, FrameObj>();
        foreach (var o in bm.All()) baseByUid[o.Uid] = o;
        var renamed = new Dictionary<string, Dictionary<string, string>>();
        var deleted = new Dictionary<string, HashSet<string>>();
        var seen = new HashSet<int>();
        foreach (var o in m.All())
        {
            FrameObj b;
            if (!baseByUid.TryGetValue(o.Uid, out b) || b.GetType() != o.GetType()) b = null;
            if (b != null)
            {
                seen.Add(b.Uid);
                if (b.Kind != "load" && b.Label != o.Label) Map(renamed, b.Kind)[b.Label] = o.Label;
            }
            Reconcile(doc, map, b == null ? new List<FrameRec>() : b.Recs, o.Recs, added);
        }
        foreach (var b in bm.All())
        {
            if (seen.Contains(b.Uid)) continue;
            foreach (var r in b.Recs) RemoveRow(doc, r.Table, map[r.Row]);
            if (b.Kind != "load") Set(deleted, b.Kind).Add(b.Label);
        }
        // a label deleted and then used again by a new object is no deletion for the rows that name it
        foreach (var o in m.All()) if (o.Kind != "load") { HashSet<string> d; if (deleted.TryGetValue(o.Kind, out d)) d.Remove(o.Label); }

        // rows nobody owns: renames followed, deletions carried out or refused
        if (renamed.Count > 0 || deleted.Count > 0)
        {
            var errors = new List<string>();
            foreach (var t in doc.Tables)
                foreach (var row in t.Rows.ToList())
                {
                    if (owned.Contains(row)) continue;
                    for (int i = 0; i < row.Keys.Count; i++)
                    {
                        string kind = RefKind(t.Name, row, row.Keys[i]);
                        if (kind == null) continue;
                        string v = row.Values[i];
                        Dictionary<string, string> rn; HashSet<string> dl; string to;
                        if (renamed.TryGetValue(kind, out rn) && rn.TryGetValue(v, out to)) { row.Set(row.Keys[i], to); continue; }
                        if (deleted.TryGetValue(kind, out dl) && dl.Contains(v))
                        {
                            if (i == 0) { t.RemoveRow(row); notes.Add(string.Format("{0}: a row of the deleted {1} {2} removed with it", t.Name, kind, v)); break; }
                            errors.Add(string.Format("the {0} {1} is deleted, but {2} still uses it ({3}={1}): remove that in SAP first, or keep the {0}", kind, v, t.Name, row.Keys[i]));
                        }
                    }
                }
            if (errors.Count > 0) throw new InvalidOperationException("FrameS2k: " + string.Join("; ", errors.Distinct().Take(12).ToArray()));
        }
        foreach (string t in added.Distinct(StringComparer.OrdinalIgnoreCase))
            if (!FrameS2kSchema.Confirmed(t)) notes.Add("s2k-unconfirmed: new rows in " + t + ", a table SAP has not confirmed yet (the SAP probe settles it): check them after the import");
        return doc;
    }

    static Dictionary<string, string> Map(Dictionary<string, Dictionary<string, string>> d, string k)
    {
        Dictionary<string, string> v;
        if (!d.TryGetValue(k, out v)) d[k] = v = new Dictionary<string, string>(StringComparer.Ordinal);
        return v;
    }
    static HashSet<string> Set(Dictionary<string, HashSet<string>> d, string k)
    {
        HashSet<string> v;
        if (!d.TryGetValue(k, out v)) d[k] = v = new HashSet<string>(StringComparer.Ordinal);
        return v;
    }

    static string RefKind(string table, S2kRow row, string field) { return RefKind(table, row.Get, field); }

    // what object kind a field of a row names, if any (get: another field of the same row)
    public static string RefKind(string table, Func<string, string> get, string field)
    {
        string f = field.ToUpperInvariant(), t = table.ToUpperInvariant();
        if (f == "OBJECTLABEL")
        {
            string ot = (get("ObjectType") ?? "").ToLowerInvariant();
            return ot == "joint" || ot == "frame" || ot == "link" ? ot : null;
        }
        if (f == "JOINT" || f == "JOINTI" || f == "JOINTJ" || f == "MONITORJT" || (f.StartsWith("JOINT") && f.Length > 5 && char.IsDigit(f[5]))) return "joint";
        if (f == "FRAME") return "frame";
        if (f == "LINK") return t.StartsWith("LINK PROPERTY") ? "linkprop" : "link";
        if (f == "ANALSECT" || f == "DESIGNSECT" || f == "SECTIONNAME") return "section";
        if (f == "MATERIAL" || f == "MATPROP") return "material";
        if (f == "LINKPROP") return "linkprop";
        if (f == "LOADPAT") return "pattern";
        if (f == "LOADNAME") { string lt = get("LoadType") ?? ""; return lt.IndexOf("pattern", StringComparison.OrdinalIgnoreCase) >= 0 ? "pattern" : null; }
        if (f == "CASENAME") { string ct = get("CaseType") ?? ""; return ct.IndexOf("Combo", StringComparison.OrdinalIgnoreCase) >= 0 ? "combo" : "case"; }
        if (f == "CASE" || f == "INITIALCOND") return "case";
        if (f == "COMBONAME") return "combo";
        if (f == "GROUPNAME") return "group";
        return null;
    }

    // one object's rows against its base rows, table by table, row by row in order
    static void Reconcile(S2kDoc doc, Dictionary<S2kRow, S2kRow> map, List<FrameRec> baseRecs, List<FrameRec> curRecs, List<string> added)
    {
        var tables = new List<string>();
        foreach (var r in baseRecs) if (!tables.Contains(r.Table, StringComparer.OrdinalIgnoreCase)) tables.Add(r.Table);
        foreach (var r in curRecs) if (!tables.Contains(r.Table, StringComparer.OrdinalIgnoreCase)) tables.Add(r.Table);
        foreach (string t in tables)
        {
            var b = baseRecs.Where(r => string.Equals(r.Table, t, StringComparison.OrdinalIgnoreCase)).ToList();
            var c = curRecs.Where(r => string.Equals(r.Table, t, StringComparison.OrdinalIgnoreCase)).ToList();
            S2kRow last = null;
            for (int i = 0; i < Math.Max(b.Count, c.Count); i++)
            {
                if (i < b.Count && i < c.Count)
                {
                    var row = map[b[i].Row];
                    foreach (var f in c[i].F) if (b[i].Get(f[0]) != f[1]) row.Set(f[0], f[1]);
                    foreach (var f in b[i].F) if (!c[i].Has(f[0])) row.Remove(f[0]);
                    last = row;
                }
                else if (i < c.Count)
                {
                    var row = new S2kRow { Eol = doc.Eol, Dirty = true };
                    foreach (var f in c[i].F) { row.Keys.Add(f[0]); row.Values.Add(f[1]); row.Quoted.Add(false); }
                    var table = doc.Table(t) ?? InsertTable(doc, t);
                    if (last != null) table.InsertRowAfter(last, row); else table.AddRow(row);
                    last = row;
                    added.Add(t);
                }
                else RemoveRow(doc, t, map[b[i].Row]);
            }
        }
    }

    static void RemoveRow(S2kDoc doc, string table, S2kRow row)
    {
        var t = doc.Table(table);
        if (t != null) t.RemoveRow(row);
    }

    // a new table where SAP's order puts it among the file's tables: PROGRAM CONTROL first, the rest alphabetical
    static S2kTable InsertTable(S2kDoc doc, string name)
    {
        var order = doc.Tables.Select(t => t.Name).ToList();
        int from = order.Count > 0 && order[0].Equals("PROGRAM CONTROL", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        int at = -1;
        for (int i = from; i < order.Count; i++)
            if (string.CompareOrdinal(order[i].ToUpperInvariant(), name.ToUpperInvariant()) > 0) { at = i; break; }
        order.Insert(at < 0 ? order.Count : at, name);
        return doc.AddTable(name, order);
    }

    // ---------------- editing helpers: renames and deletions that keep the model consistent

    // a new label, set in the object's own rows and in every row of the model that names it
    public static void Rename(FrameModel m, FrameObj o, string label)
    {
        string old = o.Label;
        if (old == label) return;
        if (o.Kind == "load") throw new ArgumentException("FrameS2k: a load has no label of its own");
        foreach (var x in m.All())
            foreach (var r in x.Recs)
            {
                var rec = r;
                for (int i = 0; i < rec.F.Count; i++)
                    if (rec.F[i][1] == old && RefKind(rec.Table, rec.Get, rec.F[i][0]) == o.Kind) rec.F[i][1] = label;
            }
    }

    // an object out of the model with what only describes it: the loads on a joint or frame, its group
    // memberships; refused (an exception) while another object still needs it (a frame on a joint, a frame of a
    // section, a case of a pattern ...), with the list of what does
    public static void Delete(FrameModel m, FrameObj o)
    {
        string label = o.Label;
        if (o.Kind != "load")
        {
            var users = new List<string>();
            foreach (var x in m.All())
            {
                if (x == o || x is FrameGroup || x is FrameLoad && (o.Kind == "joint" || o.Kind == "frame")) continue;   // those go with it
                foreach (var r in x.Recs)
                    foreach (var f in r.F)
                        if (f[1] == label && RefKind(r.Table, r.Get, f[0]) == o.Kind && !(x.Kind == o.Kind && f[0].Equals(x.KeyField, StringComparison.OrdinalIgnoreCase)))
                            users.Add(x.Kind + " " + x.Label);
            }
            if (users.Count > 0)
                throw new InvalidOperationException(string.Format("FrameS2k: the {0} {1} is still used by {2}", o.Kind, label, string.Join(", ", users.Distinct().Take(8).ToArray())));
            if (o.Kind == "joint" || o.Kind == "frame")
                foreach (var ld in m.Loads.Where(l => (o.Kind == "joint") == l.Table.StartsWith("JOINT", StringComparison.OrdinalIgnoreCase) && l.Target == label).ToList()) m.Remove(ld);
            foreach (var g in m.Groups)
                g.Recs.RemoveAll(r => string.Equals(r.Table, FrameGroup.Assign, StringComparison.OrdinalIgnoreCase) &&
                                      string.Equals(r.Get("ObjectLabel"), label, StringComparison.Ordinal) && RefKind(r.Table, r.Get, "ObjectLabel") == o.Kind);
        }
        m.Remove(o);
    }

    // ---------------- a new model: a source holding only PROGRAM CONTROL
    public static FrameModel NewModel(string units, string version)
    {
        string text = "File generated by Pluto (frame model)\r\n\r\nTABLE:  \"PROGRAM CONTROL\"\r\n   ProgramName=SAP2000   Version=" + version +
                      "   CurrUnits=\"" + units + "\"\r\n\r\nEND TABLE DATA\r\n";
        return Read(S2kDoc.ParseText(text));
    }
}
