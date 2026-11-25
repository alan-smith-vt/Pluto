// Palette.cs -- the one colour scheme of every workbook CalcFlow writes (calc library, register, harvest, summary).
// Each column is one kind of information, and each kind has its own dark header with white bold text:
//   Key        what the row is (Connection, Node, Member; Template, Sheet, Cell): navy header, bold data
//   Input      what a person fills in (Type, Support, Binding, Default ...): dark gold header, pale yellow cells
//   Tool       what CalcFlow writes (Status, Max DCR, governing case ...): charcoal header, plain cells
//   Reference  what was found or is shown for information (Example, Found, Role, Section): teal header, grey text
// Statuses, checks and problem severities get a soft fill with bold dark text of the same hue (the look of the
// pipe-review workbooks: pale green / dark green for good, pale red / dark red for bad ...), and a DCR above the limit
// turns bold dark red. The strong colours are kept for the headers. C# 5 (Windows PowerShell 5.1 Add-Type, warnings
// are errors).

using System;
using System.Collections.Generic;

namespace CalcFlow
{
    public enum ColumnKind { Key, Input, Tool, Reference }

    public static class Palette
    {
        public const string Navy = "FF203864", Gold = "FF7F6000", Charcoal = "FF3A3A3A", Teal = "FF1F6F78";
        public const string PaleYellow = "FFFFF2CC", White = "FFFFFFFF", GreyText = "FF404040";
        public const string Green = "FF2E7D32", Red = "FFC00000", Orange = "FFC55A11", Grey = "FF595959", LightGrey = "FFBFBFBF", Amber = "FFFFC000";
        public const string MidYellow = "FFFFE699", MidGreen = "FFA9D08E", MidBlue = "FF9BC2E6", MidOrange = "FFF4B183";
        // the looks of a status or a check: fill, text
        public static readonly string[] Good = { "FFC6EFCE", "FF006100" }, Bad = { "FFFFC7CE", "FF9C0006" }, Caution = { "FFFFEB9C", "FF7F6000" },
            Redo = { "FFFFE0B2", "FF7F3F00" }, DataIssue = { "FFE4DFEC", "FF5B2C6F" }, Muted = { "FFEDEDED", "FF7F7F7F" };
        // the looks a cell takes from what is chosen elsewhere in the workbook (conditional formats): Need = still to be
        // filled in (a stronger yellow than an input cell's own); Muted = not read as things are chosen; Redo = waits
        // for Refresh config; Bad = does not fit
        public static readonly string[] Need = { "FFFFD966", "FF7F6000" };
        public const string DarkRed = "FF9C0006";
        public const string TableStyle = "TableStyleMedium1";

        public static XStyle Header(ColumnKind k)
        {
            var s = new XStyle();
            s.Bold = true; s.Color = White; s.Border = true; s.Wrap = true; s.VAlign = "center";
            s.Fill = k == ColumnKind.Key ? Navy : (k == ColumnKind.Input ? Gold : (k == ColumnKind.Tool ? Charcoal : Teal));
            return s;
        }

        // the data cells of a column; null = plain
        public static XStyle Data(ColumnKind k)
        {
            var s = new XStyle();
            switch (k)
            {
                case ColumnKind.Key: s.Bold = true; return s;
                case ColumnKind.Input: s.Fill = PaleYellow; s.Border = true; return s;
                case ColumnKind.Reference: s.Color = GreyText; return s;
                default: return null;
            }
        }

        public static XStyle Note()
        {
            var s = new XStyle();
            s.Italic = true; s.Color = GreyText;
            return s;
        }

        public static XStyle Title()
        {
            var s = new XStyle();
            s.Bold = true; s.Size = 13; s.Color = Navy;
            return s;
        }

        // a cell whose text equals `text` gets this fill and font colour, bold
        public static void Highlight(XOutSheet sheet, string sqref, string text, string fill, string color)
        {
            var cf = new XCondFormat();
            cf.Sqref = sqref; cf.EqualsText = text;
            cf.Style = new XStyle(); cf.Style.Fill = fill; cf.Style.Color = color; cf.Style.Bold = true;
            sheet.CondFormats.Add(cf);
        }

        public static void Highlight(XOutSheet sheet, string sqref, string text, string[] look) { Highlight(sheet, sqref, text, look[0], look[1]); }

        // the cells of sqref for which the formula is true take this look. The formula is written for the first cell of
        // sqref, without "=". Of two rules over one cell the one added first wins.
        public static void When(XOutSheet sheet, string sqref, string formula, string[] look)
        {
            var cf = new XCondFormat();
            cf.Sqref = sqref; cf.Formula = formula;
            cf.Style = new XStyle(); cf.Style.Fill = look[0]; cf.Style.Color = look[1];
            sheet.CondFormats.Add(cf);
        }

        // a text inside such a formula
        public static string Q(string text) { return "\"" + (text ?? "").Replace("\"", "\"\"") + "\""; }

        public static readonly string[][] StatusColours =
        {
            new[] { "OK", Good[0], Good[1] }, new[] { "Over limit", Bad[0], Bad[1] }, new[] { "Failed", Bad[0], Bad[1] },
            new[] { "Invalid", Redo[0], Redo[1] }, new[] { "Not run", Caution[0], Caution[1] }, new[] { "Gaps", DataIssue[0], DataIssue[1] },
            new[] { "Blocked", DataIssue[0], DataIssue[1] }, new[] { "Excluded", Muted[0], Muted[1] }
        };

        public static void Statuses(XOutSheet sheet, string sqref)
        {
            foreach (string[] c in StatusColours) Highlight(sheet, sqref, c[0], c[1], c[2]);
        }

        public static void Severities(XOutSheet sheet, string sqref)
        {
            Highlight(sheet, sqref, "Error", Bad);
            Highlight(sheet, sqref, "Warning", Caution);
        }

        // a DCR above the limit: bold dark red
        public static void OverLimit(XOutSheet sheet, string sqref, double limit)
        {
            var cf = new XCondFormat();
            cf.Sqref = sqref; cf.Operator = "greaterThan"; cf.Value = limit;
            cf.Style = new XStyle(); cf.Style.Color = DarkRed; cf.Style.Bold = true;
            sheet.CondFormats.Add(cf);
        }

        // the kind of a column of a table the tool writes (library, harvest drafts, register)
        public static ColumnKind KindOf(string table, string column)
        {
            string t = table.Replace("tblHarvest", "tbl");
            string[] keys;
            if (Keys.TryGetValue(t, out keys) && Array.IndexOf(keys, column) >= 0) return ColumnKind.Key;
            string[] refs;
            if (References.TryGetValue(t, out refs) && Array.IndexOf(refs, column) >= 0) return ColumnKind.Reference;
            string[] tool;
            if (ToolColumns.TryGetValue(t, out tool) && Array.IndexOf(tool, column) >= 0) return ColumnKind.Tool;
            if (t == "tblLayout") return ColumnKind.Reference;
            return ColumnKind.Input;
        }

        static readonly Dictionary<string, string[]> Keys = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "tblTemplates", new[] { "Template" } },
            { "tblInputs", new[] { "Template", "Sheet", "Cell" } },
            { "tblOutputs", new[] { "Template", "Sheet", "Cell" } },
            { "tblFrames", new[] { "Frame" } },
            { "tblTypes", new[] { "Type" } },
            { "tblSteps", new[] { "Type", "Step" } },
            { "tblParams", new[] { "Type", "Step", "Param" } },
            { "tblSections", new[] { "Section", "Prop" } },
            { "tblLayout", new[] { "Template", "Sheet", "Cell" } },
            { "tblLayouts", new[] { "Template" } },
            { "tblConnections", new[] { "Connection", "Node", "Member", "End" } },
            { "tblCombos", new[] { "LC" } },
            { "tblSettings", new[] { "Setting" } }
        };

        static readonly Dictionary<string, string[]> References = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "tblTemplates", new[] { "Sha256", "Notes" } },
            { "tblInputs", new[] { "GuardCell", "GuardText", "Notes", "Example", "Kind", "List", "Description", "Found" } },
            { "tblOutputs", new[] { "GuardCell", "GuardText", "Notes", "Example", "Formula", "Found" } },
            { "tblFrames", new[] { "Notes" } }, { "tblTypes", new[] { "Notes" } }, { "tblSteps", new[] { "Notes" } },
            { "tblParams", new[] { "Notes" } }, { "tblSections", new[] { "Notes" } },
            { "tblLayouts", new[] { "File", "Sheets", "Cells", "Inputs", "Outputs" } },
            { "tblConnections", new[] { "Role", "Section" } },
            { "tblCombos", new[] { "Title", "Kind" } },
            { "tblSettings", new[] { "Notes" } }
        };

        static readonly Dictionary<string, string[]> ToolColumns = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "tblConnections", new[] { "Status", "Status Detail", "Max DCR", "Governing Check", "Governing Combo", "Calc", "Design Rev" } }
        };
    }
}
