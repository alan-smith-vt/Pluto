// Palette.cs -- the one colour scheme of every workbook CalcFlow writes (calc library, register, harvest, summary).
// Each column is one kind of information, and each kind has its own dark header with white bold text:
//   Key        what the row is (Connection, Node, Member; Template, Sheet, Cell): navy header, bold data
//   Input      what a person fills in (Type, Support, Binding, Default ...): dark gold header, pale yellow cells
//   Tool       what CalcFlow writes (Status, Max DCR, governing case ...): charcoal header, plain cells
//   Reference  what was found or is shown for information (Example, Found, Role, Section): teal header, grey text
// Statuses and problem severities get solid fills with white (or black, on amber / light grey) bold text, and a DCR
// above the limit turns red. C# 5 (Windows PowerShell 5.1 Add-Type, warnings are errors).

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

        public static readonly string[][] StatusColours =
        {
            new[] { "OK", Green, White }, new[] { "Over limit", Red, White }, new[] { "Failed", Red, White },
            new[] { "Invalid", Orange, White }, new[] { "Not run", Grey, White }, new[] { "Gaps", Grey, White },
            new[] { "Blocked", Grey, White }, new[] { "Excluded", LightGrey, "FF000000" }
        };

        public static void Statuses(XOutSheet sheet, string sqref)
        {
            foreach (string[] c in StatusColours) Highlight(sheet, sqref, c[0], c[1], c[2]);
        }

        public static void Severities(XOutSheet sheet, string sqref)
        {
            Highlight(sheet, sqref, "Error", Red, White);
            Highlight(sheet, sqref, "Warning", Amber, "FF000000");
        }

        // a DCR above the limit: red, white bold
        public static void OverLimit(XOutSheet sheet, string sqref, double limit)
        {
            var cf = new XCondFormat();
            cf.Sqref = sqref; cf.Operator = "greaterThan"; cf.Value = limit;
            cf.Style = new XStyle(); cf.Style.Fill = Red; cf.Style.Color = White; cf.Style.Bold = true;
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
