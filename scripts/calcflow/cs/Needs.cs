// Needs.cs -- what a cell of the engineer's workbook still needs, shown as the choices are made: conditional formats
// over the tabs of CalcFlow.xlsx, each a formula on what other cells hold (a column's Source, a mark's template, a
// result's Use ...). Four looks, said on the Start tab (Legend):
//   Need   still to be filled in, because of a choice made elsewhere      a stronger yellow than an input cell's own
//   Muted  not read as things are chosen: nothing to type here            grey
//   Redo   waits for Refresh config                                       orange
//   Bad    does not fit (a mark that is not on Connection Lib, the same mark twice)   red
// Every rule here is one that check also says in words; the colours say it while the tab is being filled in.
// The formulas keep to what every Excel takes in a conditional format (XlsxCheck.FormatFormula): another tab is
// reached through INDIRECT, there is no array constant, and each stays within 255 characters. Tabs written by
// WorkbookTables.Sheet have their headings in row 2 and their rows from row 3; rules reach row 2000, like the
// drop-downs. C# 5 (Windows PowerShell 5.1 Add-Type, warnings are errors).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CalcFlow
{
    public static class Needs
    {
        const int Last = 2000;      // the last row a rule reaches (Excel's numbering)
        static readonly string[] InputLook = { Palette.PaleYellow, "FF000000" };

        static string Col(int c) { return Xlsx.ColName(c); }
        static string N(int n) { return n.ToString(CultureInfo.InvariantCulture); }
        // a range of another tab, for COUNTIF and the like
        static string Of(string tab, string range) { return "INDIRECT(" + Palette.Q("'" + tab.Replace("'", "''") + "'!" + range) + ")"; }

        // ---- Connection Lib: A = the mark, B = its template
        public static void ConnectionLib(XOutSheet s)
        {
            string a = "A3:A" + N(Last), b = "B3:B" + N(Last);
            Palette.When(s, a, "AND($A3<>\"\",COUNTIF($A$3:$A$" + N(Last) + ",$A3)>1)", Palette.Bad);                                     // the same mark twice
            Palette.When(s, a, "AND($A3=\"\",$B3<>\"\")", Palette.Need);                                                                  // a template chosen, no mark named
            // the mark has no row yet on the tab of its template (not said while that tab is not there: the rule on B says that)
            Palette.When(s, a, "AND($A3<>\"\",$B3<>\"\",IFERROR(COUNTIF(INDIRECT(\"'\"&$B3&\"'!$A$" + N(ProjectBook.FirstMarkRow + 1) + ":$A$" + N(Last) + "\"),$A3),1)=0)", Palette.Need);
            Palette.When(s, b, "AND($B3<>\"\",COUNTIF(" + Of(ProjectBook.TemplatesTab, "$A$3:$A$" + N(Last)) + ",$B3)=0)", Palette.Bad);     // not a template of the Templates tab
            Palette.When(s, b, "AND($B3<>\"\",ISERROR(INDIRECT(\"'\"&$B3&\"'!A1\")))", Palette.Redo);                                       // its tab is made by Refresh config
            Palette.When(s, b, "AND($A3<>\"\",$B3=\"\")", Palette.Need);                                                                  // a mark with no template
        }

        // ---- Model Config: a mark at a beam end that Connection Lib does not have
        public static void ModelConfig(XOutSheet s, int connectionI, int connectionJ)
        {
            if (connectionI < 0 || connectionJ < connectionI) return;
            string first = Col(connectionI) + "3";
            Palette.When(s, first + ":" + Col(connectionJ) + N(Last), "AND(" + first + "<>\"\",COUNTIF(" + Of(ProjectBook.ConnectionLib, "$A$3:$A$" + N(Last)) + "," + first + ")=0)", Palette.Bad);
        }

        // ---- a template's tab. loadColumn[i]: column i + 1 of the tab (B = 0) is a column of the template's load table
        public static void Template(XOutSheet s, string tab, IList<bool> loadColumn)
        {
            int src = ProjectBook.SourceRow + 1, unit = ProjectBook.UnitRow + 1, first = ProjectBook.FirstMarkRow + 1;
            string rows = N(first) + ":$A$" + N(Last), lib = ProjectBook.ConnectionLib;
            // the Connection column: the same mark twice; a mark that is not one of this template's; values with no mark
            Palette.When(s, "A" + N(first) + ":A" + N(Last), "AND($A" + N(first) + "<>\"\",COUNTIF($A$" + rows + ",$A" + N(first) + ")>1)", Palette.Bad);
            Palette.When(s, "A" + N(first) + ":A" + N(Last), "AND($A" + N(first) + "<>\"\",COUNTIFS(" + Of(lib, "$A$3:$A$" + N(Last)) + ",$A" + N(first) + "," + Of(lib, "$B$3:$B$" + N(Last)) + "," + Palette.Q(tab) + ")=0)", Palette.Bad);
            Palette.When(s, "A" + N(first) + ":A" + N(Last), "AND($A" + N(first) + "=\"\",COUNTA($B" + N(first) + ":$ZZ" + N(first) + ")>0)", Palette.Need);
            if (loadColumn.Count == 0) return;
            string lastCol = Col(loadColumn.Count);
            // the Unit row: a column that takes a force or a moment of the model needs its unit
            string b = "B" + N(src);
            Palette.When(s, "B" + N(unit) + ":" + lastCol + N(unit),
                "AND(B" + N(unit) + "=\"\",OR(" + string.Join(",", new[] { "Fx", "Fy", "Fz", "Mx", "My", "Mz" }.Select(f => b + "=\"" + f + "\"").ToArray()) + "))", Palette.Need);
            // the marks' rows, one run of columns of a kind at a time
            for (int i = 0; i < loadColumn.Count; )
            {
                int j = i;
                while (j + 1 < loadColumn.Count && loadColumn[j + 1] == loadColumn[i]) j++;
                string c = Col(i + 1), cell = c + N(first), source = c + "$" + N(src), mark = "$A" + N(first) + "<>\"\"";
                string range = cell + ":" + Col(j + 1) + N(Last), typed = source + "=" + Palette.Q(ProjectBook.Typed);
                if (loadColumn[i])
                {
                    // a column of the table: the mark's to fill only when its Source is Typed here, and then needed
                    Palette.When(s, range, "AND(" + mark + "," + cell + "=\"\"," + typed + ")", Palette.Need);
                    Palette.When(s, range, "AND(" + mark + "," + typed + ")", InputLook);
                    Palette.When(s, range, "AND(" + mark + "," + source + "<>" + Palette.Q(ProjectBook.Typed) + ")", Palette.Muted);
                }
                else
                    // a column typed for the mark: not read when its Source is the model's or the template's own
                    Palette.When(s, range, "AND(" + mark + "," + source + "<>\"\"," + source + "<>" + Palette.Q(ProjectBook.Typed) + ")", Palette.Muted);
                i = j + 1;
            }
        }

        // ---- a column of a template's load table (col: its column on the template's tab) whose Source is turned to
        // "Typed per beam end" waits for Refresh config until the tab of beam ends has its column (named as the tab of
        // beam ends names it; while that tab is not there at all, the rule holds too)
        public static void PerEnd(XOutSheet s, int col, string heading, string named, string endsTab)
        {
            string c = Col(col), src = c + N(ProjectBook.SourceRow + 1);
            string name = string.Equals(heading, named, StringComparison.Ordinal) ? c + N(ProjectBook.HeaderRow + 1) : Palette.Q(named);
            string f = "AND(" + src + "=" + Palette.Q(ProjectBook.PerEnd) + ",ISERROR(MATCH(" + name + "," + Of(endsTab, "$2:$2") + ",0)))";
            if (f.Length <= 255) Palette.When(s, src, f, Palette.Redo);
        }

        // ---- a tab of beam ends: a blank value in the row of a beam end (its cell in the template is left empty)
        public static void Ends(XOutSheet s, int firstTyped, int lastTyped)
        {
            if (firstTyped < 0 || lastTyped < firstTyped) return;
            string c = Col(firstTyped) + "3";
            Palette.When(s, c + ":" + Col(lastTyped) + N(Last), "AND($A3<>\"\"," + c + "=\"\")", Palette.Need);
        }

        // ---- Templates: a load table needs its rows; one said on this tab (not on the template's own) needs its
        // columns and the units of the forces it takes. ownRows: the rows (0-based, of the sheet) of the templates
        // whose table is said here
        public static void Templates(XOutSheet s, int at, int columns, int maxRows, int forceUnit, int momentUnit, IList<int> ownRows)
        {
            if (at < 0 || maxRows < 0) return;
            string a = "$" + Col(at);
            Palette.When(s, Col(maxRows) + "3:" + Col(maxRows) + N(Last), "AND(" + a + "3<>\"\"," + Col(maxRows) + "3=\"\")", Palette.Need);
            if (columns < 0 || forceUnit < 0 || momentUnit < 0) return;
            foreach (int r0 in ownRows)
            {
                string r = N(r0 + 1), cols = "$" + Col(columns) + r;
                Palette.When(s, Col(columns) + r, "AND(" + a + r + "<>\"\"," + cols + "=\"\")", Palette.Need);
                Palette.When(s, Col(forceUnit) + r, "AND(" + a + r + "<>\"\"," + Col(forceUnit) + r + "=\"\"," + Takes(cols, "F") + ")", Palette.Need);
                Palette.When(s, Col(momentUnit) + r, "AND(" + a + r + "<>\"\"," + Col(momentUnit) + r + "=\"\"," + Takes(cols, "M") + ")", Palette.Need);
            }
        }

        // the list in cell names a force (F) or a moment (M) of the model
        static string Takes(string cell, string kind)
        {
            return "OR(" + string.Join(",", new[] { "x", "y", "z" }.Select(x => "ISNUMBER(SEARCH(\"" + kind + x + "\"," + cell + "))").ToArray()) + ")";
        }

        // ---- Outputs: a template in use needs a result with Use = Y; a health result in use needs what it must equal
        public static void Outputs(XOutSheet s, int use, int kind, int expect)
        {
            if (use < 0 || kind < 0 || expect < 0) return;
            string u = "$" + Col(use), all = "$" + N(Last);
            Palette.When(s, Col(use) + "3:" + Col(use) + N(Last),
                "AND($A3<>\"\",COUNTIFS($A$3:$A" + all + ",$A3," + u + "$3:" + u + all + ",\"Y*\")=0,COUNTIF(" + Of(ProjectBook.ConnectionLib, "$B$3:$B$" + N(Last)) + ",$A3)>0)", Palette.Need);
            Palette.When(s, Col(expect) + "3:" + Col(expect) + N(Last), "AND(LEFT(" + u + "3,1)=\"Y\",$" + Col(kind) + "3=\"health\"," + Col(expect) + "3=\"\")", Palette.Need);
        }

        // ---- Load Cases: none is in use yet
        public static void LoadCases(XOutSheet s, int use)
        {
            if (use < 0) return;
            string u = "$" + Col(use);
            Palette.When(s, Col(use) + "3:" + Col(use) + N(Last), "AND($A3<>\"\",COUNTIF(" + u + "$3:" + u + "$" + N(Last) + ",\"Y*\")=0)", Palette.Need);
        }

        // ---- the Start tab: what the colours say: fill, text colour, a name, what it means
        public static readonly string[][] Legend =
        {
            new[] { Palette.PaleYellow, "FF000000", "Yours", "A cell you fill in." },
            new[] { Palette.Need[0], Palette.Need[1], "Still needed", "Needed because of what is chosen elsewhere: a mark with no template or no row on its template's tab, a force with no unit, a value typed per mark or per beam end left blank, a template in use with no result to read back." },
            new[] { Palette.Muted[0], Palette.Muted[1], "Not read", "Nothing to type here as things are chosen: a column whose Source is the model's or the template's own, or a cell the tool writes." },
            new[] { Palette.Redo[0], Palette.Redo[1], "Refresh config", "Waits for Refresh config: a mark whose template has no tab yet, a column turned to Typed per beam end that the tab of beam ends does not have yet." },
            new[] { Palette.Bad[0], Palette.Bad[1], "Does not fit", "A mark that is not on Connection Lib or belongs to another template, the same mark twice, a template that is not on the Templates tab." }
        };
    }
}
