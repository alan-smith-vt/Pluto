using System;
using System.Collections.Generic;
using System.Linq;

// WbForm  --  the workbook kit's form layer (2026-11-05), shared by the CAESAR pipe workbook and the SAP frame workbook:
// a theme (cell styles and the conditional formats, made in a fixed order so a book's bytes do not move), the column
// spec (name, header colour group, kind, list, hint, note) and Table, which puts a column table on a sheet: header
// colours and notes, a hint and a dropdown or rule per input column, red where a cell's kind is broken, amber where an
// Id repeats. Kinds the kit knows:
//   id      an Id: key style, duplicates amber               text   free text
//   num     any number      pos  a number > 0                nonneg a number >= 0
//   int0    a whole number >= 0                              cos    a number in -1 .. 1
//   yesno   yes / no (a dropdown of List)                    list!  one of a list (anything else is red)
//   list?   a dropdown, anything allowed                     ref    a name on List (an id range), or a range a:b of two
// A workbook adds its own kinds through KindRule / KindValidation (CAESAR: node, catalog, vector, radius, elemref).
// Nothing here is read back. C# 5. vault/arms/workbook-kit.md.

public sealed class WbTheme
{
    public int Title, Subtitle, Section, Body, BodyWrap, Small, Link, Key, Label, Value, Stat, Code, Note, ReadOnly, Center;
    public readonly Dictionary<string, int> Head = new Dictionary<string, int>(StringComparer.Ordinal);
    public readonly Dictionary<string, int> Chip = new Dictionary<string, int>(StringComparer.Ordinal);
    public int Red, Amber, Green, Muted;           // conditional formats
    public string[][] Groups;                      // header colour groups: { key, colour, what it means }
    public WbForm Form;                            // the book's form (its kinds, how far references look)

    // the styles every workbook uses, then one header style and one chip per group: the same calls in the same order
    public static WbTheme Make(XlsxBook b, string[][] groups)
    {
        var t = new WbTheme();
        t.Groups = groups;
        t.Title = b.Style(new XlsxStyle { Bold = true, Size = 20, Color = "1F2937" });
        t.Subtitle = b.Style(new XlsxStyle { Size = 11, Color = "6B7280" });
        t.Section = b.Style(new XlsxStyle { Bold = true, Size = 13, Color = "1F2937", Bottom = "1565C0", BottomMedium = true });
        t.Body = b.Style(new XlsxStyle { Color = "1F2937", VAlign = "top" });
        t.BodyWrap = b.Style(new XlsxStyle { Color = "1F2937", VAlign = "top", Wrap = true });
        t.Small = b.Style(new XlsxStyle { Size = 9, Color = "6B7280", VAlign = "top", Wrap = true });
        t.Link = b.Style(new XlsxStyle { Color = "1565C0", Underline = true, VAlign = "top" });
        t.Key = b.Style(new XlsxStyle { Bold = true, Color = "37474F", Fill = "ECEFF1" });
        t.Label = b.Style(new XlsxStyle { Bold = true, Color = "37474F", VAlign = "center" });
        t.Value = b.Style(new XlsxStyle { Color = "1F2937", Fill = "FFFFFF", Border = "B0BEC5", Align = "center", VAlign = "center" });
        t.Stat = b.Style(new XlsxStyle { Color = "1F2937", Align = "right", Format = "#,##0.###" });
        t.Code = b.Style(new XlsxStyle { Bold = true, Align = "center", Border = "D0D7DE" });
        t.Note = XlsxBook.Note;
        t.ReadOnly = XlsxBook.ReadOnly;
        t.Center = b.Style(new XlsxStyle { Align = "center" });
        foreach (var g in groups)
        {
            t.Head[g[0]] = b.Style(new XlsxStyle { Bold = true, Color = "FFFFFF", Fill = g[1], Align = "center", VAlign = "center", Wrap = true, Border = "FFFFFF" });
            t.Chip[g[0]] = b.Style(new XlsxStyle { Fill = g[1], Border = "FFFFFF" });
        }
        t.Red = b.Dxf("9C0006", "FFC7CE", false);
        t.Amber = b.Dxf("9C5700", "FFEB9C", false);
        t.Green = b.Dxf("006100", "C6EFCE", false);
        t.Muted = b.Dxf("9E9E9E", null, false);
        return t;
    }
}

// a column: its header name (units in brackets are allowed on the sheet), colour group, kind, list, hint and note
public sealed class WbCol
{
    public string Name, Group, Kind, List, Hint, Note;
    public WbCol(string name, string group, string kind, string list, string hint, string note) { Name = name; Group = group; Kind = kind; List = list; Hint = hint; Note = note; }
}

public sealed class WbForm
{
    public int RefRows = 5000;                                  // how far rules look into a sheet they reference
    public const string NewId = "new";                          // the Id a row shows until the model has given it one
    const int NewRows = 300;                                    // the rows below the data that show it
    public Func<WbCol, string, string> KindRule;                // a workbook's own kinds: the red rule (null = none)
    public Func<WbCol, XlsxValidation, bool> KindValidation;    // ... and the dropdown / hint (true = handled)

    // "DX (in)" -> "DX"
    public static string NameOf(string header)
    {
        int p = header.IndexOf(" (", StringComparison.Ordinal);
        return p > 0 && header.EndsWith(")", StringComparison.Ordinal) ? header.Substring(0, p) : header;
    }

    // the cell rule for a kind (at = the column's first data cell, relative), red when true
    public string Rule(WbCol c, string at)
    {
        switch (c.Kind)
        {
            case "num": return "AND(" + at + "<>\"\",NOT(ISNUMBER(" + at + ")))";
            case "pos": return "IF(ISNUMBER(" + at + ")," + at + "<=0," + at + "<>\"\")";
            case "nonneg": return "IF(ISNUMBER(" + at + ")," + at + "<0," + at + "<>\"\")";
            case "int0": return "IF(ISNUMBER(" + at + "),OR(" + at + "<0," + at + "<>INT(" + at + "))," + at + "<>\"\")";
            case "cos": return "IF(ISNUMBER(" + at + "),ABS(" + at + ")>1," + at + "<>\"\")";
            case "list!": return "AND(" + at + "<>\"\",COUNTIF(" + c.List + "," + at + ")=0)";
            case "yesno":   // no array constant: Excel refuses them in conditional formatting (and repairs the sheet away)
                string lo = "LOWER(TRIM(" + at + "&\"\"))";
                return "AND(" + at + "<>\"\",NOT(OR(" + string.Join(",", new[] { "yes", "y", "true", "g", "1", "no", "n", "false", "0" }.Select(w => lo + "=\"" + w + "\"").ToArray()) + ")))";
            case "ref": return RefRule(at, c.List);
        }
        return KindRule != null ? KindRule(c, at) : null;
    }

    // a name on the id range, or a range "a:b" of two of them (red when not)
    public static string RefRule(string at, string ids)
    {
        string t = "TRIM(" + at + ")";
        return "IF(" + t + "=\"\",FALSE,IF(ISNUMBER(FIND(\":\"," + t + ")),OR(COUNTIF(" + ids + ",TRIM(LEFT(" + t + ",FIND(\":\"," + t + ")-1)))=0,COUNTIF(" + ids + ",TRIM(MID(" + t + ",FIND(\":\"," + t +
               ")+1,99)))=0),COUNTIF(" + ids + "," + t + ")=0))";
    }

    public XlsxValidation Validation(WbCol c, string at)
    {
        var v = new XlsxValidation { PromptTitle = NameOf(c.Name), Prompt = c.Hint };
        switch (c.Kind)
        {
            case "num": v.Type = "decimal"; v.Operator = "between"; v.Formula1 = "-1E+307"; v.Formula2 = "1E+307"; v.ErrorTitle = "A number"; v.Error = c.Name + " takes a number."; break;
            case "pos": v.Type = "decimal"; v.Operator = "greaterThan"; v.Formula1 = "0"; v.ErrorTitle = "A positive number"; v.Error = c.Name + " takes a number above 0."; break;
            case "nonneg": v.Type = "decimal"; v.Operator = "greaterThanOrEqual"; v.Formula1 = "0"; v.ErrorTitle = "A number"; v.Error = c.Name + " takes a number, 0 or more."; break;
            case "int0": v.Type = "whole"; v.Operator = "greaterThanOrEqual"; v.Formula1 = "0"; v.ErrorTitle = "A whole number"; v.Error = c.Name + " takes a whole number, 0 or more."; break;
            case "cos": v.Type = "decimal"; v.Operator = "between"; v.Formula1 = "-1"; v.Formula2 = "1"; v.ErrorTitle = "A direction cosine"; v.Error = c.Name + " takes a number from -1 to 1."; break;
            case "list!": case "yesno": v.Type = "list"; v.Formula1 = c.List; v.ErrorTitle = "Not on the list"; v.Error = "Pick a value from the list (" + NameOf(c.Name) + ")."; break;
            case "list?": v.Type = "list"; v.Formula1 = c.List; v.ShowError = false; break;
            default: if (KindValidation != null) KindValidation(c, v); break;
        }
        return v;
    }

    // headers styled by group, notes, a hint and a rule per column, highlighting; rows: the data rows written; lim: the
    // last row the rules cover
    public void Table(XlsxBookSheet s, WbTheme t, string[] headers, WbCol[] cols, int rows, int lim)
    {
        var byName = new Dictionary<string, WbCol>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in cols) byName[c.Name] = c;
        s.Height(0, 46);
        s.FreezeRows = Math.Max(1, s.FreezeRows);
        for (int k = 0; k < headers.Length; k++)
        {
            WbCol c;
            if (!byName.TryGetValue(NameOf(headers[k]), out c)) { s.Set(0, k, headers[k], t.Head["ro"]); continue; }
            s.Set(0, k, headers[k], t.Head[c.Group]);
            if (c.Note != null || c.Hint != null) s.Note(0, k, (c.Note ?? c.Hint));
            string col = XlsxBook.Column(k), at = col + "2", range = col + "2:" + col + lim;
            s.Validate(range, Validation(c, at));
            s.Conditional(range, "ISERROR(" + at + ")", t.Red);
            string rule = Rule(c, at);
            if (rule != null) s.Conditional(range, rule, t.Red);
            if (c.Kind == "id")
            {
                s.Conditional(range, "AND(" + at + "<>\"\"," + at + "<>\"" + NewId + "\",COUNTIF($" + col + "$2:$" + col + "$" + lim + "," + at + ")>1)", t.Amber);
                for (int r = 1; r <= rows; r++) s.Style(r, k, t.Key);
                // the rows below: "new" as soon as the next column (the row's node, label or name) is filled, so every
                // row shows an Id and none is typed; the reader takes "new" as a blank Id (a new object)
                if (k + 1 < headers.Length)
                {
                    string key = XlsxBook.Column(k + 1);
                    for (int r = rows + 1; r <= Math.Min(lim - 1, rows + NewRows); r++)
                        s.SetFormula(r, k, "IF(" + key + (r + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + "=\"\",\"\",\"" + NewId + "\")", t.Key);
                }
            }
        }
        s.AutoFilter = "A1:" + XlsxBook.Column(headers.Length - 1) + Math.Max(2, rows + 1);
    }
}
