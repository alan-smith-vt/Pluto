// Hints.cs -- the note Excel shows beside a cell when it is selected: what the cell is, and what is expected in it.
// It is the input message of a data validation, as in the pipe-review workbooks: no macro, no comment shapes. A
// cell holds one validation, so a column that already has a drop-down gets its note on that validation, and a
// drop-down that spans several columns is split into one a column. Excel's limits are kept by the writer (a title of
// 32 characters, a text of 255). The heading cell of a column says the same as the cells under it.
// C# 5 (Windows PowerShell 5.1 Add-Type, warnings are errors).

using System;
using System.Collections.Generic;
using System.Linq;

namespace CalcFlow
{
    public static class Hints
    {
        const int LastRow = 1999;       // notes reach row 2000, like the drop-downs

        // the note of one column: its cells from firstRow down (0-based), and its heading cell (headRow; -1: none)
        public static void Column(XOutSheet s, int col, int headRow, int firstRow, string title, string text)
        {
            if (string.IsNullOrEmpty(text) || col < 0) return;
            if (headRow >= 0) Add(s, Xlsx.Ref(headRow, col), title, text);
            for (int i = 0; i < s.Validations.Count; i++)
            {
                XValidation v = s.Validations[i];
                int r1, c1, r2, c2;
                if (!Rect(v.Sqref, out r1, out c1, out r2, out c2) || col < c1 || col > c2 || LastRow < r1 || firstRow > r2) continue;
                if (c1 != c2)
                {
                    s.Validations.RemoveAt(i);
                    for (int c = c1; c <= c2; c++)
                    {
                        var one = new XValidation();
                        one.Sqref = Xlsx.Ref(r1, c) + ":" + Xlsx.Ref(r2, c); one.List = v.List; one.Strict = v.Strict; one.PromptTitle = v.PromptTitle; one.Prompt = v.Prompt;
                        s.Validations.Insert(i + c - c1, one);
                    }
                    v = s.Validations[i + col - c1];
                }
                v.PromptTitle = title; v.Prompt = text;
                return;
            }
            Add(s, Xlsx.Ref(firstRow, col) + ":" + Xlsx.Ref(Math.Max(LastRow, firstRow), col), title, text);
        }

        // the note of a range given as it is ("B2:AZ2"): on the drop-down of exactly that range when there is one
        public static void Range(XOutSheet s, string sqref, string title, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            XValidation v = s.Validations.FirstOrDefault(x => string.Equals(x.Sqref, sqref, StringComparison.OrdinalIgnoreCase));
            if (v != null) { v.PromptTitle = title; v.Prompt = text; }
            else Add(s, sqref, title, text);
        }

        // every column of a tab written by WorkbookTables.Sheet (a note row, the headings in row 2, the rows from 3)
        public static void Table(XOutSheet s, string table, IList<string> cols)
        {
            Dictionary<string, string> notes;
            if (!Notes.TryGetValue(table, out notes)) return;
            string all;
            notes.TryGetValue("*", out all);
            for (int c = 0; c < cols.Count; c++)
            {
                string text;
                if (!notes.TryGetValue(cols[c], out text)) text = all;
                Column(s, c, 1, 2, cols[c], text);
            }
        }

        static void Add(XOutSheet s, string sqref, string title, string text)
        {
            var v = new XValidation();
            v.Sqref = sqref; v.List = ""; v.Strict = false; v.PromptTitle = title; v.Prompt = text;
            s.Validations.Add(v);
        }

        static bool Rect(string sqref, out int r1, out int c1, out int r2, out int c2)
        {
            r1 = c1 = r2 = c2 = 0;
            if (string.IsNullOrEmpty(sqref) || sqref.IndexOf(' ') >= 0) return false;
            string[] p = sqref.Split(':');
            if (!Xlsx.ParseRef(p[0], out c1, out r1)) return false;
            if (p.Length < 2 || !Xlsx.ParseRef(p[1], out c2, out r2)) { c2 = c1; r2 = r1; }
            return true;
        }

        const string Tool = " Written by the tool: a change here is not read, and the next Refresh config writes it again.";

        static Dictionary<string, string> D(params string[] kv)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i + 1 < kv.Length; i += 2) d[kv[i]] = kv[i + 1];
            return d;
        }

        // table -> column -> the note ("*": every column the table does not name)
        static readonly Dictionary<string, Dictionary<string, string>> Notes = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            { "tblModelConfig", D(
                "Beam ID", "The member's number in the STAAD model." + Tool,
                "Node I", "The joint at the member's start (end I)." + Tool,
                "Node J", "The joint at the member's end (end J)." + Tool,
                "Section", "The section as the model names it." + Tool,
                "Shape", "The family of the section (W, HSS, L ...)." + Tool,
                "Connection I", "Pick the connection at end I: a mark from the Connection Lib tab. Blank = no connection calc at this end.",
                "Connection J", "Pick the connection at end J: a mark from the Connection Lib tab. Blank = no connection calc at this end.",
                "Frames into I", "The number of the member that end I connects to (its support). A first guess by the tool: correct it where it is wrong. Blank = it connects to no member, as at a base plate.",
                "Frames into J", "The number of the member that end J connects to (its support). A first guess by the tool: correct it where it is wrong. Blank = it connects to no member, as at a base plate.",
                "Comment", "Your own note on this beam. Kept by Refresh config. The calcs do not use it.") },
            { "tblConnectionLib", D(
                "Connection", "A name of your own for one connection design, such as WELD-B1. A mark is one template and one row of values on that template's tab. Model Config picks its marks from this column.",
                "Template", "Pick the calc template this mark uses: a row of the Templates tab. Then give the mark its row on the tab of that name.",
                "Description", "What the connection is, in your own words. For the reader: the calcs do not use it.") },
            { "tblConfigTemplates", D(
                "Template", "The template's name, which is also the name of its tab." + Tool,
                "File", "The template workbook, under the Templates folder set on the Start tab." + Tool,
                "Sheet", "The sheet of that workbook this calc is named for. The tab may hold inputs of its other sheets too: see the Template Sheets tab." + Tool,
                "Sha256", "The file's fingerprint when it was learned. Check says when the file has changed since." + Tool,
                "Loads", "Envelope: the largest and smallest of each force, each with what acts with it. All cases: every load case in use. Largest of each force: one row with the largest of each (not at one time), for a table of one governing row a beam end.",
                "Basis", "LRFD or ASD when the template takes only one: it then gets only the load cases of that Basis on the Load Cases tab. Blank = every case in use.",
                "Sign map", "Which member-end force each force of the template is: the name of a row of the Sign Maps tab. IDENTITY = the member end's forces as they are.",
                "Forces", "Member end forces: of the beam end the mark sits at. Support reaction: the reaction at that beam end's joint, for an anchorage where more than the column lands on the base.",
                "Load table at", "Only for a template that takes a table of load rows: the table's first cell, such as A2. Blank = the template takes one load case at a time.",
                "Load table columns", "The load table's columns in order: Beam, Node, LC, Fx to Mz, Section, - to leave one alone, [name] typed per beam end, {name} typed per mark. Where the template's tab shows the table's columns, change its Source row: this cell is then written by the tool.",
                "Max rows", "How many rows the load table holds. More rows than this are calculated in several fills of the template.",
                "Force unit", "The unit of the forces in the load table, such as kip or kN. Where the template's tab shows the table's columns, it is the Unit row there that counts.",
                "Moment unit", "The unit of the moments in the load table, such as kip-in or kN-m. Where the template's tab shows the table's columns, it is the Unit row there that counts.",
                "Input cells", "Only when the inputs found are not the ones wanted: the cells to fill, such as B5:B12, F5 or Anchors = table B37:D44. Start with the word also to add to the ones found. Then run Refresh config.",
                "Result cells", "Only when the results found are not the ones wanted: the cells to read back, such as H98:H107 or DCR = F44. Then run Refresh config.",
                "Notes", "Your own note on this template. Kept by Refresh config.",
                "Beam ends tab", "The tab where the values this template takes per beam end are typed (the columns in square brackets of its load table). Blank = it has none." + Tool) },
            { "tblTemplateSheets", D(
                "*", "What became of this sheet of a template workbook: the inputs and results found on it, and the tab that holds its inputs." + Tool,
                "Says", "What the tool made of this sheet, and what to do when it needs an input that was not picked up." + Tool) },
            { "tblTemplateCells", D(
                "*", "Where each column of a template's tab goes in the template workbook. The tool writes this tab and reads it back: leave it as it is. To change the cells, use Input cells on the Templates tab.") },
            { "tblConfigOutputs", D(
                "Template", "The template the result belongs to. For a result you add by hand: the template's name as on the Templates tab.",
                "Use", "Y = read this result back after every calc. Blank or N = not read.",
                "Check", "The column of the summary this result goes in. Results with the same Check count as one check: the largest governs.",
                "Kind", "dcr: a ratio, judged against the DCR limit. status: OK or NG as text. health: a flag that must equal Expect, else the calc is invalid. value: reported as it is.",
                "Expect", "Only for Kind = health: what the cell must show for the calc to count, such as TRUE.",
                "Per row", "Y for a result beside a load table: one value per table row, Cell being its first cell. Blank otherwise.",
                "Result", "The result's name, from its label in the template. For a result you add by hand: any name.",
                "Sheet", "The sheet of the template workbook the result is on.",
                "Cell", "The cell, or the cell's name, the result is read from.",
                "Label cell", "The cell that holds the result's label. The tool checks the label before it reads the result, so a cell that moved is caught. Blank = not checked.",
                "Label", "The label expected in Label cell.") },
            { "tblSignMaps", D(
                "Frame", "A name of your own for this map: the Sign map column of the Templates tab picks it. IDENTITY always exists and needs no row.",
                "Fx", "Which member-end force is the template's Fx: +Fx, -Fy, +Fz ... End axes: x along the member away from the joint, y the member's local y, z = x cross y.",
                "Fy", "Which member-end force is the template's Fy: +Fx, -Fy, +Fz ... End axes: x along the member away from the joint, y the member's local y, z = x cross y.",
                "Fz", "Which member-end force is the template's Fz: +Fx, -Fy, +Fz ... End axes: x along the member away from the joint, y the member's local y, z = x cross y.",
                "Mx", "Which member-end moment is the template's Mx: +Mx, -My, +Mz ... The moments follow the forces: where Fx = +Fz, Mx = +Mz (a mirrored map takes the opposite sign).",
                "My", "Which member-end moment is the template's My: +Mx, -My, +Mz ... The moments follow the forces: where Fy = +Fy, My = +My (a mirrored map takes the opposite sign).",
                "Mz", "Which member-end moment is the template's Mz: +Mx, -My, +Mz ... The moments follow the forces: where Fz = +Fx, Mz = +Mx (a mirrored map takes the opposite sign).",
                "Mirror", "Y only when the template's axes are a mirror image of the member end's (a left-handed set): the moments then take the opposite sign. Blank or N otherwise.",
                "Action", "on_member: the forces as STAAD prints them, acting on the member end. on_connection: the opposite, acting on the connection (all six reversed).",
                "Confirmed", "Y once you have checked this map by hand against the template's own sketch. For your record.",
                "Notes", "Your own note on this map.") },
            { "tblConfigCases", D(
                "LC", "The load case or combination number in the model." + Tool,
                "Title", "The case's title in the model." + Tool,
                "Kind", "What the case is in the model: a primary case, a REPEAT LOAD, or a combination and how it combines." + Tool,
                "Use", "Y = the calcs take this load case. N = left out.",
                "Basis", "LRFD or ASD: a template whose Basis is set takes only the cases of its basis. Blank = taken by every template.") }
        };
    }
}
