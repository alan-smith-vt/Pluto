using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

// WbStart  --  the kit's Start sheet and its per-row helper sheets (2026-11-05; from PipeWorkbookGui):
//   StartSheet  how to use the workbook, "Build it from here" (links to the launchers, the viewer, the folder, and the
//               commands they run), the sheets with their row counts, live checks (a count per check, OK / check / fix),
//               the colour key and a reference section. The texts are the workbook's.
//   WbCalc      a hidden helper sheet with one row per input row (row r reads row r of its input sheet through
//               INDEX(...,ROW()), so inserting rows keeps them together): references to its columns by key for the
//               highlight rules (At), the Start counts (Count) and other formulas (Range), and the row templates'
//               tokens (Expand).
// C# 5.

public sealed class WbSheetInfo
{
    public string Name, What, Count;
    public WbSheetInfo(string name, string what, string count) { Name = name; What = what; Count = count; }
}

public sealed class WbCheck
{
    public string What, Formula;
    public bool Warning;
    public WbCheck(string what, string formula, bool warning) { What = what; Formula = formula; Warning = warning; }
}

// a link of the run block: HYPERLINK(Target, Label) and what it does
public sealed class WbLink
{
    public string Target, Label, What;
    public WbLink(string target, string label, string what) { Target = target; Label = label; What = what; }
}

// a command of the run block: its label, the formula giving its text, a note on the label (null = none)
public sealed class WbCommand
{
    public string Label, Formula, Note;
    public WbCommand(string label, string formula, string note) { Label = label; Formula = formula; Note = note; }
}

public static class WbStart
{
    // the texts of a workbook's Start sheet
    public sealed class Texts
    {
        public string Heading, Subtitle;          // "Pipe model workbook"; "<title>   |   made from ...   |   units: ..."
        public string[] Steps;                    // "How to use it"
        public string CheckNote;                  // under the checks
        public string IdExample, RedText, AmberText;   // the colour key's Id example and the red / amber lines
        public string[] Reference;
    }

    // runBlock: writes "Build it from here" at a row and returns the next free row (null = none)
    public static void StartSheet(XlsxBookSheet s, WbTheme t, Texts x, Func<int, int> runBlock, List<WbSheetInfo> sheets, List<WbCheck> checks)
    {
        s.ShowGrid = false;
        s.TabColor = "1F2937";
        s.Width(0, 2); s.Width(1, 30); s.Width(2, 78); s.Width(3, 12); s.Width(4, 12);
        int r = 1;
        s.Set(r, 1, x.Heading, t.Title); s.Height(r, 30); r++;
        s.Set(r, 1, x.Subtitle, t.Subtitle); r += 2;

        s.Set(r, 1, "How to use it", t.Section); s.Style(r, 2, t.Section); s.Style(r, 3, t.Section); s.Style(r, 4, t.Section); r++;
        foreach (string st in x.Steps) { s.Set(r, 1, st, t.BodyWrap); s.Merge("B" + (r + 1) + ":E" + (r + 1)); s.Height(r, 18); r++; }
        r++;
        if (runBlock != null) r = runBlock(r);

        s.Set(r, 1, "Sheets", t.Section); s.Set(r, 2, "", t.Section); s.Set(r, 3, "Rows", t.Section); s.Style(r, 4, t.Section); r++;
        foreach (var si in sheets)
        {
            s.Set(r, 1, si.Name, t.Link); s.Link(r, 1, XlsxBook.SheetRef(si.Name) + "!A1", "go to " + si.Name);
            s.Set(r, 2, si.What, t.Body);
            if (si.Count != null) s.SetFormula(r, 3, si.Count, t.Stat);
            r++;
        }
        r++;

        s.Set(r, 1, "Checks", t.Section); s.Set(r, 2, "(live: they follow your edits)", t.Section); s.Set(r, 3, "Count", t.Section); s.Set(r, 4, "", t.Section); r++;
        int first = r;
        foreach (var c in checks)
        {
            s.Set(r, 1, c.What, t.Body); s.Merge("B" + (r + 1) + ":C" + (r + 1));
            s.SetFormula(r, 3, "IFERROR(" + c.Formula + ",\"?\")", t.Stat);
            string cnt = "D" + (r + 1);
            s.SetFormula(r, 4, "IF(" + cnt + "=0,\"OK\",IF(ISNUMBER(" + cnt + ")," + (c.Warning ? "\"check\"" : "\"fix\"") + ",\"see red cells\"))", t.Code);
            r++;
        }
        string status = "E" + (first + 1) + ":E" + r;
        s.Conditional(status, "E" + (first + 1) + "=\"OK\"", t.Green);
        s.Conditional(status, "E" + (first + 1) + "=\"check\"", t.Amber);
        s.Conditional(status, "AND(E" + (first + 1) + "<>\"OK\",E" + (first + 1) + "<>\"check\")", t.Red);
        s.Set(r, 1, x.CheckNote, t.Small); s.Merge("B" + (r + 1) + ":E" + (r + 1)); r += 2;

        s.Set(r, 1, "Colour key", t.Section); s.Style(r, 2, t.Section); s.Style(r, 3, t.Section); s.Style(r, 4, t.Section); r++;
        foreach (var g in t.Groups)
        {
            s.Set(r, 1, "", t.Chip[g[0]]);
            s.Set(r, 2, g[2], t.Body);
            r++;
        }
        s.Set(r, 1, x.IdExample, t.Key); s.Set(r, 2, "An Id: keep it", t.Body); r++;
        s.Set(r, 1, "red", t.Code); s.Conditional("B" + (r + 1), "TRUE", t.Red); s.Set(r, 2, x.RedText, t.Body); r++;
        s.Set(r, 1, "amber", t.Code); s.Conditional("B" + (r + 1), "TRUE", t.Amber); s.Set(r, 2, x.AmberText, t.Body); r++;
        s.Set(r, 1, "grey", t.ReadOnly); s.Set(r, 2, "Read-only: rebuilt on every export, never read back", t.Body); r += 2;

        s.Set(r, 1, "Reference", t.Section); s.Style(r, 2, t.Section); s.Style(r, 3, t.Section); s.Style(r, 4, t.Section); r++;
        foreach (string line in x.Reference) { s.Set(r, 1, line, t.BodyWrap); s.Merge("B" + (r + 1) + ":E" + (r + 1)); s.Height(r, line.Length > 150 ? 32 : 18); r++; }
    }

    // "Build it from here": links (to the launchers, the viewer, the folder), a note, then the commands; returns the next
    // free row
    public static int RunBlock(XlsxBookSheet s, WbTheme t, int r, List<WbLink> links, string note, List<WbCommand> commands)
    {
        s.Set(r, 1, "Build it from here", t.Section); s.Set(r, 2, "(save first: the build reads the saved file)", t.Section); s.Style(r, 3, t.Section); s.Style(r, 4, t.Section); r++;
        foreach (var l in links)
        {
            s.SetFormula(r, 1, "HYPERLINK(" + l.Target + "," + WbRun.Lit(l.Label) + ")", t.Link);
            s.Set(r, 2, l.What, t.BodyWrap); s.Merge("C" + (r + 1) + ":E" + (r + 1)); s.Height(r, 30);
            r++;
        }
        s.Set(r, 1, note, t.Small);
        s.Merge("B" + (r + 1) + ":E" + (r + 1)); s.Height(r, 26); r++;
        foreach (var c in commands)
        {
            s.Set(r, 1, c.Label, t.Label); s.SetFormula(r, 2, c.Formula, t.Small); s.Merge("C" + (r + 1) + ":E" + (r + 1)); s.Height(r, 40);
            if (c.Note != null) s.Note(r, 1, c.Note);
            r++;
        }
        return r + 1;
    }
}

// a hidden helper sheet with one row per input row, its columns named by key
public sealed class WbCalc
{
    public readonly string Sheet;
    readonly string[] _cols;
    public WbCalc(string sheet, string[] cols) { Sheet = sheet; _cols = cols; }

    public string Letter(string key) { return XlsxBook.Column(Array.IndexOf(_cols, key)); }

    // the helper's last row for an input of n rows (room to add 300)
    public static int Last(int rows) { return Math.Max(300, rows + 300) + 1; }

    // the helper cell of the row being formatted, found by ROW(): a highlight rule that named the helper's cell
    // (PlotCalc!$I2) kept that cell when a row was inserted above its first row and read the row above's flags from then on
    public string At(string key)
    {
        string c = Letter(key);
        return "INDEX(" + Sheet + "!$" + c + ":$" + c + ",ROW())";
    }
    public string Range(string key, int rows)
    {
        string c = Letter(key);
        return Sheet + "!$" + c + "$2:$" + c + "$" + Last(rows).ToString(CultureInfo.InvariantCulture);
    }
    public string Count(string key, int rows) { return "COUNTIF(" + Range(key, rows) + ",TRUE)"; }

    static readonly Regex Token = new Regex(@"\[(\w+)([-+:^*]?)\]");

    // a row template: [k] this row's cell; [k-] the row above; [k+] the row below; [k:] the rows above from the header
    // ($A$1:A(r-1)); [k^] the whole column from the header (a MATCH position is then the row number); [k*] the data rows
    public static string Expand(string tpl, Dictionary<string, string> col, int row, int last)
    {
        return Token.Replace(tpl, m =>
        {
            string c = col[m.Groups[1].Value];
            switch (m.Groups[2].Value)
            {
                case "-": return c + (row - 1);
                case "+": return c + (row + 1);
                case ":": return "$" + c + "$1:" + c + (row - 1);
                case "^": return "$" + c + "$1:$" + c + "$" + last;
                case "*": return "$" + c + "$2:$" + c + "$" + last;
            }
            return c + row;
        });
    }
}
