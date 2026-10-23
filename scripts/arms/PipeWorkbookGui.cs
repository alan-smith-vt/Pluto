using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

// PipeWorkbookGui  --  what makes the pipe model workbook (PipeWorkbook) a form: header colours by kind of column,
// a note on every header, a hint and a dropdown or rule on every input column, conditional formatting that marks
// what the build would refuse (red) or should be looked at (amber), the Start sheet (how to use it, the sheets,
// live checks, the colour key) and the 3D View sheet: an XY chart of the model projected from 3D by formulas
// on two hidden sheets (PlotCalc: one row per element; PlotPts: the chart's points), so it follows every edit.
// Nothing here is read back: the input sheets keep their columns and values. C# 5.

static class PipeWorkbookGui
{
    // ---------------------------------------------------------------- look

    internal sealed class Theme
    {
        public int Title, Subtitle, Section, Body, BodyWrap, Small, Link, Key, Label, Value, Stat, Code, Note, ReadOnly, Center;
        public readonly Dictionary<string, int> Head = new Dictionary<string, int>(StringComparer.Ordinal);
        public readonly Dictionary<string, int> Chip = new Dictionary<string, int>(StringComparer.Ordinal);
        public int Red, Amber, Green, Muted;           // conditional formats
    }

    // header colours by kind of column (the Start sheet's key explains them)
    internal static readonly string[][] Groups =
    {
        new[] { "key", "37474F", "Ids and node numbers: what ties a row to the model" },
        new[] { "geom", "1565C0", "Geometry: element lengths, coordinates" },
        new[] { "prop", "00796B", "Properties: type, section, process, restraint type" },
        new[] { "bend", "B45309", "Bends: radius, bend nodes, bend type" },
        new[] { "comp", "6A1B9A", "Components: rigid weight, reducer end, expansion joint" },
        new[] { "load", "9A3412", "Loads: vectors and their values" },
        new[] { "text", "546E7A", "Names and tags" },
        new[] { "over", "78909C", "Overrides for one element (the grouped columns, +)" },
        new[] { "ro", "9E9E9E", "Read-only: rebuilt on every export, never read" },
    };

    internal static Theme MakeTheme(XlsxBook b)
    {
        var t = new Theme();
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
        foreach (var g in Groups)
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

    // ---------------------------------------------------------------- columns

    // a column's kind: what its cells hold, and so its dropdown / rule / highlighting
    //   id      an Id (E1, R3 ...): key style, duplicates amber
    //   node    a node number (whole, >= 1)     int0  a whole number >= 0      num  any number
    //   pos     a number > 0                    nonneg  a number >= 0          cos  a number in -1 .. 1
    //   vector  a load vector 1 .. 9            yesno  yes / no
    //   list!   one of a list (a dropdown; anything else is red)   list?  a dropdown, anything allowed
    //   radius  a bend radius: > 0, LR, SR, or a CAESAR coded value   elemref  an element Id or range (E3:E9)
    //   text    free text
    internal sealed class Col
    {
        public string Name, Group, Kind, List, Hint, Note;
        public Col(string name, string group, string kind, string list, string hint, string note) { Name = name; Group = group; Kind = kind; List = list; Hint = hint; Note = note; }
    }

    static Col C(string name, string group, string kind, string hint) { return new Col(name, group, kind, null, hint, null); }
    static Col C(string name, string group, string kind, string hint, string note) { return new Col(name, group, kind, null, hint, note); }
    static Col L(string name, string group, string kind, string list, string hint, string note) { return new Col(name, group, kind, list, hint, note); }

    // the lists (the hidden Lists sheet): column letter -> what it holds
    internal const string LType = "Lists!$A$2:$A$9", LBendType = "Lists!$C$2:$C$4", LWind = "Lists!$D$2:$D$4", LYesNo = "Lists!$E$2:$E$3", LAxis = "Lists!$F$2:$F$3",
                          LFrame = "Lists!$G$2:$G$3", LView = "Lists!$H$2:$H$6", LVector = "Lists!$I$2:$I$10", LKind = "Lists!$K$2:$K$7", LCodes = "Lists!$N$2:$N$10";

    internal static Col[] ElementCols(string sections, string processes)
    {
        var l = new List<Col>
        {
            C("Id", "key", "id", "Keep it: it ties the row to the element this workbook was written from. On a new row leave it blank.",
              "Id (E1, E2 ...): the element of the model this workbook was written from. Keep it when you edit the row; leave it blank on a new row; delete the row to delete the element. Moving rows reorders the elements (CAESAR's order). A copied row keeps its original's Id: clear it on the copy."),
            C("From", "key", "node", "The node the element starts at (a whole number).",
              "From: the start node. An element starts where an earlier element reached its From node, else at its Coords entry, else where the element above ended (CAESAR's rule)."),
            C("To", "key", "node", "The node the element ends at (a whole number).", "To: the end node. With a bend radius, the bend is at this node."),
            C("DX", "geom", "num", "Length along X, in the header's unit. Blank = 0.",
              "DX, DY, DZ: the element's length along each axis, in the header's unit; blank = 0. The header's unit may be changed to in, ft, mm, cm or m (e.g. 'DX (ft)'): the column is then read in that unit."),
            C("DY", "geom", "num", "Length along Y, in the header's unit. Blank = 0."),
            C("DZ", "geom", "num", "Length along Z, in the header's unit. Blank = 0."),
            L("Type", "prop", "list!", LType, "Pick one: Pipe, Rigid, Valve, Flange, Flange pair, Flanged valve, Reducer, Exp joint.",
              "Type: Pipe (the default); a rigid element (Rigid, Valve, Flange, Flange pair, Flanged valve: give its Weight); a Reducer (give OD2 and Wall2, its TO end); an Exp joint (give its stiffnesses)."),
            L("Section", "prop", "list?", sections, "A pipe size from the Sections sheet. Blank = the row above's.",
              "Section: the pipe size (OD, wall, insulation, material ...), named on the Sections sheet. Blank = the row above's. To change one value for one element, use the grouped columns at the right (click + above them)."),
            L("Process", "prop", "list?", processes, "Temperatures and pressures from the Processes sheet. Blank = the row above's.",
              "Process: the operating conditions (T1-T9, P1-P9, fluid density), named on the Processes sheet. Blank = the row above's."),
            C("Bend radius", "bend", "radius", "Makes a bend at the TO node: a radius, or a name: LR / Long (1.5 x nominal), SR / Short (1 x), 3D, 5D. Blank = no bend.",
              "Bend radius: makes this element a bend at its TO node (CAESAR's convention: the element runs to the corner). A radius in the header's unit, or a name: LR or Long = 1.5 x the nominal size, SR or Short = 1 x, 3D, 5D. A radius CAESAR wrote by name shows its name and goes back to CAESAR as its code. A negative value such as -2.0202 is a code CAESAR reads as no radius (0): amber."),
            C("Bend nodes", "bend", "text", "Nodes on the bend: angle:node pairs, M = mid point. E.g. M:19; 0:18",
              "Bend nodes: the nodes CAESAR places on the bend's curve, as angle:node pairs separated by ';'. The angle is in degrees from the near end of the bend; M = the mid point. E.g. 'M:19; 0:18'. At most 3."),
            L("Bend type", "bend", "list!", LBendType, "Welded (or blank), Single flange or Double flange.", null),
            C("Weight", "comp", "nonneg", "The rigid element's weight (Rigid, Valve, Flange ... types)."),
            C("OD2", "comp", "pos", "Reducer: the OD at the TO end."),
            C("Wall2", "comp", "pos", "Reducer: the wall at the TO end."),
            C("EJ axial", "comp", "nonneg", "Expansion joint: axial stiffness.", "EJ: an expansion joint's stiffnesses (axial, transverse, bending, torsion) and its effective inside diameter (for the pressure thrust)."),
            C("EJ transverse", "comp", "nonneg", "Expansion joint: transverse stiffness."),
            C("EJ bending", "comp", "nonneg", "Expansion joint: bending stiffness."),
            C("EJ torsion", "comp", "nonneg", "Expansion joint: torsional stiffness."),
            C("EJ eff ID", "comp", "pos", "Expansion joint: effective inside diameter (pressure thrust)."),
            C("Name", "text", "text", "The element's name (CAESAR shows it)."),
            C("Line", "text", "text", "The line number (CAESAR carries it to the elements after it)."),
            C("OD", "over", "pos", "This element's OD, instead of its Section's.",
              "The grouped columns (OD ... Bend K factor) override one value of the Section or Process for this element only. Blank = the Section's / Process's value."),
            C("Wall", "over", "pos", "This element's wall, instead of its Section's."),
            C("Insulation", "over", "nonneg", "This element's insulation thickness, instead of its Section's."),
            C("Corrosion", "over", "nonneg", "This element's corrosion allowance, instead of its Section's."),
            C("Insul density", "over", "nonneg", "This element's insulation density, instead of its Section's."),
            C("Material", "over", "int0", "This element's CAESAR material number (e.g. 106 = A106 B), instead of its Section's."),
            C("E", "over", "nonneg", "This element's elastic modulus. Blank = the Section's (or CAESAR's database)."),
            C("Poisson", "over", "nonneg", "This element's Poisson's ratio. Blank = the Section's (or CAESAR's database)."),
            C("Pipe density", "over", "nonneg", "This element's pipe density. Blank = the Section's (or CAESAR's database)."),
        };
        for (int k = 1; k <= 9; k++) l.Add(C("T" + k, "over", "num", "This element's temperature T" + k + ", instead of its Process's."));
        for (int k = 1; k <= 9; k++) l.Add(C("P" + k, "over", "num", "This element's pressure P" + k + ", instead of its Process's."));
        l.Add(C("Fluid density", "over", "nonneg", "This element's fluid density, instead of its Process's."));
        l.Add(C("Bend miter points", "over", "int0", "A mitred bend: the number of miter cuts. Blank = a smooth bend."));
        l.Add(C("Bend fitting thk", "over", "pos", "The bend's wall when it differs from the pipe's. Blank = the wall."));
        l.Add(C("Bend K factor", "over", "nonneg", "The bend's flexibility factor. Blank = the code's."));
        return l.ToArray();
    }

    internal static Col[] SectionCols()
    {
        return new[]
        {
            C("Name", "key", "text", "A unique name; elements use it in their Section column.", "Name: what the Elements sheet's Section column says. Each name once."),
            C("OD", "geom", "pos", "Outside diameter (required)."),
            C("Wall", "geom", "pos", "Wall thickness (required)."),
            C("Insulation", "prop", "nonneg", "Insulation thickness. Blank or 0 = none."),
            C("Corrosion", "prop", "nonneg", "Corrosion allowance."),
            C("Insul density", "prop", "nonneg", "Insulation density."),
            C("Material", "prop", "int0", "CAESAR material number (e.g. 106 = A106 B)."),
            C("E", "prop", "nonneg", "Elastic modulus. Blank = CAESAR's material database."),
            C("Poisson", "prop", "nonneg", "Poisson's ratio. Blank = CAESAR's material database."),
            C("Pipe density", "prop", "nonneg", "Pipe density. Blank = CAESAR's material database."),
        };
    }

    internal static Col[] ProcessCols()
    {
        var l = new List<Col> { C("Name", "key", "text", "A unique name; elements use it in their Process column.", "Name: what the Elements sheet's Process column says. Each name once.") };
        for (int k = 1; k <= 9; k++) l.Add(C("T" + k, "prop", "num", "Temperature of operating condition " + k + " (CAESAR's T" + k + "). Blank = not used."));
        for (int k = 1; k <= 9; k++) l.Add(C("P" + k, "prop", "num", "Pressure of operating condition " + k + " (CAESAR's P" + k + "). Blank = not used."));
        l.Add(C("Fluid density", "prop", "nonneg", "Density of the contents. 0 = empty."));
        return l.ToArray();
    }

    internal static Col[] RestraintCols(string types)
    {
        return new[]
        {
            C("Id", "key", "id", "Keep it on rows from the model; leave it blank on a new row."),
            C("Node", "key", "node", "The restrained node (a node of the Elements sheet)."),
            L("Type", "prop", "list!", types, "CAESAR's restraint type: ANC, X, Y, Z, RX ... GUI, LIM, +Y, -Y ...",
              "Type: CAESAR's restraint type. ANC anchors; X / Y / Z (and +Y ...) act along an axis, RX ... about one; GUI guides and LIM limit stops act across / along the pipe. Codes CAESAR's documentation lists but no CAESAR file has shown yet are written with a notice."),
            C("CNode", "key", "node", "Connecting node: the restraint ties Node to it. Blank = to ground."),
            C("Stiffness", "prop", "nonneg", "Spring stiffness. Blank = rigid."),
            C("Gap", "prop", "nonneg", "Gap before the restraint acts (length; degrees for rotations)."),
            C("Mu", "prop", "nonneg", "Friction coefficient. Blank = none."),
            C("Cos X", "geom", "cos", "Direction cosine X. Blank = the type's own axis.", "Cos X, Y, Z: the restraint's direction, for a skewed one. Blank = the type's own axis (guides and limit stops: the pipe's)."),
            C("Cos Y", "geom", "cos", "Direction cosine Y. Blank = the type's own axis."),
            C("Cos Z", "geom", "cos", "Direction cosine Z. Blank = the type's own axis."),
            C("Tag", "text", "text", "A tag (support number ...), kept with the restraint."),
        };
    }

    internal static Col[] SifCols(string types)
    {
        return new[]
        {
            C("Id", "key", "id", "Keep it on rows from the model; leave it blank on a new row."),
            C("Node", "key", "node", "The node of the SIF / tee."),
            L("Type", "prop", "list!", types, "3 = welding tee, 0 = user SIFs only.", null),
            C("SIF in", "prop", "nonneg", "In-plane SIF. Blank = the code's."),
            C("SIF out", "prop", "nonneg", "Out-of-plane SIF. Blank = the code's."),
            C("Surface node", "key", "node", "A tee's surface node (CAESAR's Srf.Node). Blank = none."),
        };
    }

    internal static Col[] NodeLoadCols(bool disp)
    {
        var l = new List<Col>
        {
            C("Id", "key", "id", "Keep it on rows from the model; leave it blank on a new row. One Id = one node."),
            C("Node", "key", "node", "The loaded node."),
            L("Vector", "load", "vector", LVector, disp ? "Load vector 1-9 (CAESAR's D1-D9)." : "Load vector 1-9 (CAESAR's F1-F9).",
              disp ? "Vector: CAESAR's displacement vectors D1-D9; one row per node and vector. A DOF left blank is free; 0 holds it." : "Vector: CAESAR's force vectors F1-F9; one row per node and vector."),
        };
        foreach (string d in disp ? PipeModelCodes.DisplacementDofs : PipeModelCodes.ForceDofs)
            l.Add(C(d, "load", "num", disp ? d + " imposed. Blank = free; 0 holds it." : d + ". Blank = 0."));
        return l.ToArray();
    }

    internal static Col[] UniformCols()
    {
        return new[]
        {
            C("Element", "key", "elemref", "An element Id (E4) or a range (E3:E9). Or leave blank and give From and To.",
              "Element: the elements this row loads: an Id (E4), or a range in the Elements sheet's order (E3:E9). Or leave it blank and give From and To: one element, or the chain of elements from From to To. An element no row names has no uniform load."),
            C("From", "key", "node", "With Element blank: the From node of the first element."),
            C("To", "key", "node", "With Element blank: the To node of the last element."),
            L("Vector", "load", "vector", LVector, "Load vector 1-9 (CAESAR's U1-U9).", null),
            C("UX", "load", "num", "Load along X: per length, or in g's (In g's = yes)."),
            C("UY", "load", "num", "Load along Y: per length, or in g's (In g's = yes)."),
            C("UZ", "load", "num", "Load along Z: per length, or in g's (In g's = yes)."),
            L("In g's", "load", "yesno", LYesNo, "yes = UX UY UZ are in g's; no = force per length.", null),
        };
    }

    internal static Col[] WindCols()
    {
        return new[]
        {
            C("Element", "key", "elemref", "An element Id (E4) or a range (E3:E9). Or leave blank and give From and To.",
              "Element: the elements this row loads: an Id, a range (E3:E9), or blank with From and To. An element no row names has no wind."),
            C("From", "key", "node", "With Element blank: the From node of the first element."),
            C("To", "key", "node", "With Element blank: the To node of the last element."),
            L("Type", "load", "list!", LWind, "wind, wave or off.", null),
            C("Shape factor", "load", "nonneg", "Wind shape factor (wave: drag coefficient)."),
            C("Added mass", "load", "nonneg", "Wave only: added-mass coefficient."),
            C("Lift", "load", "nonneg", "Wave only: lift coefficient."),
            C("Marine growth", "load", "nonneg", "Wave only: marine growth thickness."),
            C("Growth density", "load", "nonneg", "Wave only: marine growth density."),
        };
    }

    internal static Col[] JointCols()
    {
        return new[]
        {
            C("Id", "key", "id", "Keep it on rows from the model; leave it blank on a new row."),
            C("Node", "key", "node", "One end of the joint."),
            C("CNode", "key", "node", "The other end (written as CNODE restraints Node -> CNode)."),
            L("Kind", "prop", "list?", LKind, "A label: coupling, hinge, slip, ball, link (or your own).", null),
            L("Frame", "prop", "list!", LFrame, "pipe: DOFs along the pipe (axial, shear, torsion, bending); global: X Y Z RX RY RZ.", null),
            C("DOFs", "prop", "text", "e.g. axial gap=0.125 k=5000; shear; torsion; bending gap=2. A DOF not listed is free.",
              "DOFs: the connected degrees of freedom, separated by ';'. Frame pipe: axial, shear, torsion, bending. Frame global: X, Y, Z, RX, RY, RZ. Each may take gap=<value> and k=<stiffness>; without k it is rigid. A DOF not listed is free."),
            C("SIF", "prop", "nonneg", "A SIF at the joint. Blank = none."),
            C("Tag", "text", "text", "A tag, kept with the joint."),
        };
    }

    internal static Col[] CoordCols()
    {
        return new[]
        {
            C("Node", "key", "node", "A node that starts a piece of the model.", "Coords: where a piece of the model starts (CAESAR's COORDS). Other nodes are placed by the element lengths."),
            C("X", "geom", "num", "Position X of the node."),
            C("Y", "geom", "num", "Position Y of the node."),
            C("Z", "geom", "num", "Position Z of the node."),
        };
    }

    // ---------------------------------------------------------------- applying a column table to a sheet

    static string NameOf(string header)
    {
        int p = header.IndexOf(" (", StringComparison.Ordinal);
        return p > 0 && header.EndsWith(")", StringComparison.Ordinal) ? header.Substring(0, p) : header;
    }

    // the cell rule for a kind ({0} = the column's first data cell, relative), red when true
    static string Rule(Col c, string at)
    {
        switch (c.Kind)
        {
            case "num": return "AND(" + at + "<>\"\",NOT(ISNUMBER(" + at + ")))";
            case "pos": return "IF(ISNUMBER(" + at + ")," + at + "<=0," + at + "<>\"\")";
            case "nonneg": return "IF(ISNUMBER(" + at + ")," + at + "<0," + at + "<>\"\")";
            case "int0": return "IF(ISNUMBER(" + at + "),OR(" + at + "<0," + at + "<>INT(" + at + "))," + at + "<>\"\")";
            case "node": return "IF(ISNUMBER(" + at + "),OR(" + at + "<1," + at + "<>INT(" + at + "))," + at + "<>\"\")";
            case "cos": return "IF(ISNUMBER(" + at + "),ABS(" + at + ")>1," + at + "<>\"\")";
            case "vector": return "IF(ISNUMBER(" + at + "),OR(" + at + "<1," + at + ">9," + at + "<>INT(" + at + "))," + at + "<>\"\")";
            case "list!": return "AND(" + at + "<>\"\",COUNTIF(" + c.List + "," + at + ")=0)";
            case "yesno":   // no array constant: Excel refuses them in conditional formatting (and repairs the sheet away)
                string lo = "LOWER(TRIM(" + at + "&\"\"))";
                return "AND(" + at + "<>\"\",NOT(OR(" + string.Join(",", new[] { "yes", "y", "true", "g", "1", "no", "n", "false", "0" }.Select(w => lo + "=\"" + w + "\"").ToArray()) + ")))";
            case "radius":
                return "IF(ISNUMBER(" + at + "),AND(" + at + "<=0,NOT(AND(" + at + "<0,ABS(-" + at + "/1.0101-ROUND(-" + at + "/1.0101,0))<0.0001,ROUND(-" + at + "/1.0101,0)<=9))),AND(" + at +
                       "<>\"\"," + string.Join(",", RadiusNames.Select(n => "UPPER(TRIM(" + at + "))<>\"" + n + "\"").ToArray()) + "))";
            case "elemref":
                string t = "TRIM(" + at + ")", ids = "Elements!$A$2:$A$" + RefRows;
                return "IF(" + t + "=\"\",FALSE,IF(ISNUMBER(FIND(\":\"," + t + ")),OR(COUNTIF(" + ids + ",TRIM(LEFT(" + t + ",FIND(\":\"," + t + ")-1)))=0,COUNTIF(" + ids + ",TRIM(MID(" + t + ",FIND(\":\"," + t +
                       ")+1,99)))=0),COUNTIF(" + ids + "," + t + ")=0))";
        }
        return null;
    }

    internal static int RefRows = 5000;      // how far rules look into the Elements sheet

    static readonly string[] RadiusNames = { "LR", "SR", "LONG", "SHORT", "3D", "5D" };

    // a bend radius CAESAR reads as no radius (a code 2 or 6-9): amber
    internal static string NoRadiusRule(string at)
    {
        return "AND(ISNUMBER(" + at + ")," + at + "<0,ABS(-" + at + "/1.0101-ROUND(-" + at + "/1.0101,0))<0.0001,OR(ROUND(-" + at + "/1.0101,0)=2,AND(ROUND(-" + at + "/1.0101,0)>=6,ROUND(-" + at + "/1.0101,0)<=9)))";
    }

    static XlsxValidation Validation(Col c, string at)
    {
        var v = new XlsxValidation { PromptTitle = NameOf(c.Name), Prompt = c.Hint };
        switch (c.Kind)
        {
            case "num": v.Type = "decimal"; v.Operator = "between"; v.Formula1 = "-1E+307"; v.Formula2 = "1E+307"; v.ErrorTitle = "A number"; v.Error = c.Name + " takes a number."; break;
            case "pos": v.Type = "decimal"; v.Operator = "greaterThan"; v.Formula1 = "0"; v.ErrorTitle = "A positive number"; v.Error = c.Name + " takes a number above 0."; break;
            case "nonneg": v.Type = "decimal"; v.Operator = "greaterThanOrEqual"; v.Formula1 = "0"; v.ErrorTitle = "A number"; v.Error = c.Name + " takes a number, 0 or more."; break;
            case "int0": v.Type = "whole"; v.Operator = "greaterThanOrEqual"; v.Formula1 = "0"; v.ErrorTitle = "A whole number"; v.Error = c.Name + " takes a whole number, 0 or more."; break;
            case "node": v.Type = "whole"; v.Operator = "greaterThanOrEqual"; v.Formula1 = "1"; v.ErrorTitle = "A node number"; v.Error = c.Name + " takes a node number (a whole number, 1 or more)."; break;
            case "cos": v.Type = "decimal"; v.Operator = "between"; v.Formula1 = "-1"; v.Formula2 = "1"; v.ErrorTitle = "A direction cosine"; v.Error = c.Name + " takes a number from -1 to 1."; break;
            case "vector": v.Type = "list"; v.Formula1 = c.List ?? LVector; v.ErrorTitle = "A load vector"; v.Error = "The vector is 1 to 9."; break;
            case "list!": case "yesno": v.Type = "list"; v.Formula1 = c.List; v.ErrorTitle = "Not on the list"; v.Error = "Pick a value from the list (" + NameOf(c.Name) + ")."; break;
            case "list?": v.Type = "list"; v.Formula1 = c.List; v.ShowError = false; break;
            case "radius":
                v.Type = "custom"; v.Formula1 = "OR(ISNUMBER(" + at + ")," + string.Join(",", RadiusNames.Select(n => "UPPER(TRIM(" + at + "))=\"" + n + "\"").ToArray()) + ")";
                v.ErrorTitle = "A bend radius"; v.Error = "A radius (a number), or LR, SR, Long, Short, 3D, 5D."; break;
        }
        return v;
    }

    // headers styled by group, notes, a hint and a rule per column, highlighting; rows: the data rows written
    internal static void Table(XlsxBookSheet s, Theme t, string[] headers, Col[] cols, int rows, int lim)
    {
        var byName = new Dictionary<string, Col>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in cols) byName[c.Name] = c;
        s.Height(0, 46);
        s.FreezeRows = Math.Max(1, s.FreezeRows);
        for (int k = 0; k < headers.Length; k++)
        {
            Col c;
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
                s.Conditional(range, "AND(" + at + "<>\"\",COUNTIF($" + col + "$2:$" + col + "$" + lim + "," + at + ")>1)", t.Amber);
                for (int r = 1; r <= rows; r++) s.Style(r, k, t.Key);
            }
        }
        s.AutoFilter = "A1:" + XlsxBook.Column(headers.Length - 1) + Math.Max(2, rows + 1);
    }

    // a rule across columns: red (or amber) where the formula is true
    internal static void Rule(XlsxBookSheet s, string range, string formula, int dxf) { s.Conditional(range, formula, dxf); }

    // a node no element has (amber): a node number on Elements' From / To, or in its bend nodes
    internal static string NodeMissing(string at, string from, string to, string bendNodes)
    {
        return "AND(ISNUMBER(" + at + "),COUNTIF(" + from + "," + at + ")=0,COUNTIF(" + to + "," + at + ")=0,COUNTIF(" + bendNodes + ",\"*:\"&" + at + ")=0,COUNTIF(" + bendNodes + ",\"*:\"&" + at + "&\";*\")=0)";
    }

    // ---------------------------------------------------------------- the Start sheet

    internal sealed class SheetInfo
    {
        public string Name, What, Count;
        public SheetInfo(string name, string what, string count) { Name = name; What = what; Count = count; }
    }

    internal sealed class Check
    {
        public string What, Formula;
        public bool Warning;
        public Check(string what, string formula, bool warning) { What = what; Formula = formula; Warning = warning; }
    }

    internal static void StartSheet(XlsxBookSheet s, Theme t, string title, string source, string units, List<SheetInfo> sheets, List<Check> checks, string[] reference, PipeWorkbookRun run)
    {
        s.ShowGrid = false;
        s.TabColor = "1F2937";
        s.Width(0, 2); s.Width(1, 30); s.Width(2, 78); s.Width(3, 12); s.Width(4, 12);
        int r = 1;
        s.Set(r, 1, "Pipe model workbook", t.Title); s.Height(r, 30); r++;
        s.Set(r, 1, (title.Length > 0 ? title + "   |   " : "") + "made from " + (source.Length > 0 ? source : "a new model") + "   |   units: " + units, t.Subtitle); r += 2;

        s.Set(r, 1, "How to use it", t.Section); s.Style(r, 2, t.Section); s.Style(r, 3, t.Section); s.Style(r, 4, t.Section); r++;
        string[] steps =
        {
            "1   Edit the white cells on the input sheets. A dropdown offers the allowed values; selecting a cell shows a hint; the red corner on a header holds its note.",
            "2   Red cells will stop the build; amber ones are worth a look. The checks below count them as you type.",
            "3   3D View draws the model as you edit it: pipe, bends, rigids, expansion joints, restraints.",
            "4   Save, then build: the links under Build it from here, or Build-Pipe.ps1 -In <this>.xlsx -Preview (the viewer) or -Cii (CAESAR). -Watch rebuilds on every save.",
            "5   Keep the Id column. It ties each row to the model this workbook was written from; leave it blank on a new row.",
        };
        foreach (string st in steps) { s.Set(r, 1, st, t.BodyWrap); s.Merge("B" + (r + 1) + ":E" + (r + 1)); s.Height(r, 18); r++; }
        r++;
        if (run != null) r = RunBlock(s, t, r, run);

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
        s.Set(r, 1, "A check counts cells or rows. Red counts stop the build; amber ones (nodes no element has, copied Ids) may be fine.", t.Small); s.Merge("B" + (r + 1) + ":E" + (r + 1)); r += 2;

        s.Set(r, 1, "Colour key", t.Section); s.Style(r, 2, t.Section); s.Style(r, 3, t.Section); s.Style(r, 4, t.Section); r++;
        foreach (var g in Groups)
        {
            s.Set(r, 1, "", t.Chip[g[0]]);
            s.Set(r, 2, g[2], t.Body);
            r++;
        }
        s.Set(r, 1, "E12", t.Key); s.Set(r, 2, "An Id: keep it", t.Body); r++;
        s.Set(r, 1, "red", t.Code); s.Conditional("B" + (r + 1), "TRUE", t.Red); s.Set(r, 2, "Will stop the build: text where a number belongs, a name no sheet has, a missing node ...", t.Body); r++;
        s.Set(r, 1, "amber", t.Code); s.Conditional("B" + (r + 1), "TRUE", t.Amber); s.Set(r, 2, "Worth a look: a node no element has, a copied Id, a zero-length element", t.Body); r++;
        s.Set(r, 1, "grey", t.ReadOnly); s.Set(r, 2, "Read-only: rebuilt on every export, never read back", t.Body); r += 2;

        s.Set(r, 1, "Reference", t.Section); s.Style(r, 2, t.Section); s.Style(r, 3, t.Section); s.Style(r, 4, t.Section); r++;
        foreach (string line in reference) { s.Set(r, 1, line, t.BodyWrap); s.Merge("B" + (r + 1) + ":E" + (r + 1)); s.Height(r, line.Length > 150 ? 32 : 18); r++; }
    }

    // "Build it from here": links to the launchers beside the workbook, the viewer and the folder, and the commands
    // they run (PipeWorkbookRun); returns the next free row
    static int RunBlock(XlsxBookSheet s, Theme t, int r, PipeWorkbookRun run)
    {
        s.Set(r, 1, "Build it from here", t.Section); s.Set(r, 2, "(save first: the build reads the saved file)", t.Section); s.Style(r, 3, t.Section); s.Style(r, 4, t.Section); r++;
        Action<string, string, string> link = (target, label, what) =>
        {
            s.SetFormula(r, 1, "HYPERLINK(" + target + "," + PipeWorkbookRun.Lit(label) + ")", t.Link);
            s.Set(r, 2, what, t.BodyWrap); s.Merge("C" + (r + 1) + ":E" + (r + 1)); s.Height(r, 30);
            r++;
        };
        link(PipeWorkbookRun.Lit(run.PreviewCmd), "\u25B6  Live preview",
             "Runs \"" + run.PreviewCmd + "\" beside this workbook: every save (Ctrl+S) rebuilds " + run.Out + ".bin and " + run.Out +
             ".features.json for Pluto's viewer (tick Auto-reload there). Close its window to stop.");
        link(PipeWorkbookRun.Lit(run.CiiCmd), "\u25B6  Write the .cii",
             "Runs \"" + run.CiiCmd + "\": " + run.Out + ".cii for CAESAR II (File, Import, CAESAR II Neutral File) and the preview with it. Its report says whether the model is the one you previewed.");
        if (run.Viewer != null)
            link(PipeWorkbookRun.Lit(run.Viewer), "Open Pluto's viewer", "Then Choose files... and pick " + run.Out + ".bin and " + run.Out + ".features.json together (Chrome or Edge).");
        link(run.FolderFormula(), "Open this folder", "The workbook, the two launchers and what they write.");
        s.Set(r, 1, "Excel asks before it follows a link to a .cmd file; where it will not, double-click the file in the folder. Or paste a command into PowerShell:", t.Small);
        s.Merge("B" + (r + 1) + ":E" + (r + 1)); s.Height(r, 26); r++;
        s.Set(r, 1, "Preview command", t.Label); s.SetFormula(r, 2, run.CommandFormula(true), t.Small); s.Merge("C" + (r + 1) + ":E" + (r + 1)); s.Height(r, 40); r++;
        s.Set(r, 1, ".cii command", t.Label); s.SetFormula(r, 2, run.CommandFormula(false), t.Small); s.Merge("C" + (r + 1) + ":E" + (r + 1)); s.Height(r, 40); r++;
        return r + 1;
    }

    // ---------------------------------------------------------------- the 3D view

    internal const string View = "3D View", Calc = "PlotCalc", Pts = "PlotPts";

    // PlotCalc's columns, in order: one row per element (row r = the Elements sheet's row r)
    static readonly string[] CalcCols =
    {
        "Id", "Fr", "To", "OK", "DX", "DY", "DZ", "Ty", "Kd", "Sc", "OD", "ODin", "Mi", "NP", "Rr", "R",
        "X1", "Y1", "Z1", "LX", "LY", "LZ", "X2", "Y2", "Z2", "L", "ux", "uy", "uz", "Nx", "uox", "uoy", "uoz", "cs", "th", "T", "Tin",
        "P0x", "P0y", "P0z", "P1x", "P1y", "P1z", "P2x", "P2y", "P2z", "P3x", "P3y", "P3z", "P4x", "P4y", "P4z", "P5x", "P5y", "P5z",
        "U0", "V0", "U1", "V1", "U2", "V2", "U3", "V3", "U4", "V4", "U5", "V5", "SL",
        "Pc", "Rt",
        "c1", "g1", "d1x", "d1y", "d1z", "o1x", "o1y", "o1z", "R1", "X1a", "Y1a", "Z1a", "X2a", "Y2a", "Z2a",
        "c2", "g2", "d2x", "d2y", "d2z", "o2x", "o2y", "o2z", "R2", "X1f", "Y1f", "Z1f", "X2f", "Y2f", "Z2f",
    };

    static readonly Regex Token = new Regex(@"\[(\w+)([-+:^*]?)\]");

    // [k] this row's cell; [k-] the row above; [k+] the row below; [k:] the rows above from the header ($A$1:A(r-1));
    // [k^] the whole column from the header (a MATCH position is then the row number); [k*] the data rows
    static string Expand(string tpl, Dictionary<string, string> col, int row, int last)
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

    static string E(string colParam) { return "INDEX(pl_el,ROW()," + colParam + ")"; }

    internal static void ViewSheets(XlsxBook b, Theme t, XlsxBookSheet view, XlsxBookSheet calc, XlsxBookSheet pts, int elements, int restraints, double inToLen, double diaToIn, string lenUnit)
    {
        int cap = Math.Max(300, elements + 300);           // element rows the plot follows
        int last = cap + 1;                                // PlotCalc's last row (1-based)
        int capR = Math.Max(200, restraints + 200);
        string vs = XlsxBook.SheetRef(View);

        // ---- PlotCalc: one row per element
        calc.State = "hidden";
        var col = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int k = 0; k < CalcCols.Length; k++) { col[CalcCols[k]] = XlsxBook.Column(k); calc.Set(0, k, CalcCols[k], XlsxBook.Header); }
        var f = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "Id", "IFERROR(" + E("pl_id") + "&\"\",\"\")" },
            { "Fr", "IFERROR(IF(ISNUMBER(" + E("pl_from") + ")," + E("pl_from") + ",\"\"),\"\")" },
            { "To", "IFERROR(IF(ISNUMBER(" + E("pl_to") + ")," + E("pl_to") + ",\"\"),\"\")" },
            { "OK", "AND(ISNUMBER([Fr]),ISNUMBER([To]))" },
            { "DX", "IFERROR(N(" + E("pl_dx") + "),0)" }, { "DY", "IFERROR(N(" + E("pl_dy") + "),0)" }, { "DZ", "IFERROR(N(" + E("pl_dz") + "),0)" },
            { "Ty", "IFERROR(" + E("pl_type") + "&\"\",\"\")" },
            { "Kd", "IF(OR(LEFT([Ty],5)=\"Rigid\",[Ty]=\"Valve\",[Ty]=\"Flange\",[Ty]=\"Flange pair\",[Ty]=\"Flanged valve\"),1,IF([Ty]=\"Exp joint\",2,0))" },
            { "Sc", "IFERROR(IF(" + E("pl_sec") + "&\"\"<>\"\"," + E("pl_sec") + "&\"\",[Sc-]),[Sc-])" },
            { "OD", "IFERROR(IF(ISNUMBER(" + E("pl_od") + ")," + E("pl_od") + ",INDEX(pl_secs,MATCH([Sc],INDEX(pl_secs,0,pl_sname),0),pl_sod)),\"\")" },
            { "ODin", "IFERROR([OD]*pl_d2in,\"\")" },
            { "Mi", "IFERROR(MATCH([ODin]+0.02,pl_odl,1),0)" },
            { "NP", "IFERROR(IF([ODin]>=13.9,IF(ABS([ODin]-ROUND([ODin],0))<0.02,ROUND([ODin],0),\"\"),IF(ABS(INDEX(pl_odl,[Mi])-[ODin])<0.02,INDEX(pl_npl,[Mi]),\"\")),\"\")" },
            { "Rr", "IFERROR(" + E("pl_bend") + "&\"\",\"\")" },
            { "R", "IFERROR(IF([Rr]=\"\",0,IF(ISNUMBER(--[Rr]),IF(--[Rr]>0,--[Rr],IF(--[Rr]<0,INDEX(pl_codes,ROUND(-[Rr]/1.0101,0))*[NP]*pl_i2l,0)),IF(OR(UPPER(TRIM([Rr]))=\"LR\",UPPER(TRIM([Rr]))=\"LONG\"),1.5,IF(OR(UPPER(TRIM([Rr]))=\"SR\",UPPER(TRIM([Rr]))=\"SHORT\"),1,IF(UPPER(TRIM([Rr]))=\"3D\",3,IF(UPPER(TRIM([Rr]))=\"5D\",5,0))))*[NP]*pl_i2l)),0)" },
            { "X1", "IF([OK],IFERROR(INDEX([X2:],MATCH([Fr],[To:],0)),IFERROR(INDEX([X1:],MATCH([Fr],[Fr:],0)),IFERROR(INDEX(Coords!$B:$B,MATCH([Fr],Coords!$A:$A,0))+0,[LX-]))),\"\")" },
            { "Y1", "IF([OK],IFERROR(INDEX([Y2:],MATCH([Fr],[To:],0)),IFERROR(INDEX([Y1:],MATCH([Fr],[Fr:],0)),IFERROR(INDEX(Coords!$C:$C,MATCH([Fr],Coords!$A:$A,0))+0,[LY-]))),\"\")" },
            { "Z1", "IF([OK],IFERROR(INDEX([Z2:],MATCH([Fr],[To:],0)),IFERROR(INDEX([Z1:],MATCH([Fr],[Fr:],0)),IFERROR(INDEX(Coords!$D:$D,MATCH([Fr],Coords!$A:$A,0))+0,[LZ-]))),\"\")" },
            { "LX", "IF(ISNUMBER([X2]),[X2],[LX-])" }, { "LY", "IF(ISNUMBER([Y2]),[Y2],[LY-])" }, { "LZ", "IF(ISNUMBER([Z2]),[Z2],[LZ-])" },
            { "X2", "IF([OK],[X1]+[DX],\"\")" }, { "Y2", "IF([OK],[Y1]+[DY],\"\")" }, { "Z2", "IF([OK],[Z1]+[DZ],\"\")" },
            { "L", "IF([OK],SQRT([DX]^2+[DY]^2+[DZ]^2),0)" },
            { "ux", "IF([L]>0,[DX]/[L],0)" }, { "uy", "IF([L]>0,[DY]/[L],0)" }, { "uz", "IF([L]>0,[DZ]/[L],0)" },
            { "Nx", "IF(NOT([OK]),0,IF(N([Fr+])=[To],ROW()+1,IFERROR(MATCH([To],[Fr^],0),0)))" },
            { "uox", "IF([Nx]>0,INDEX([ux^],[Nx]),0)" }, { "uoy", "IF([Nx]>0,INDEX([uy^],[Nx]),0)" }, { "uoz", "IF([Nx]>0,INDEX([uz^],[Nx]),0)" },
            { "cs", "[ux]*[uox]+[uy]*[uoy]+[uz]*[uoz]" },
            { "th", "ACOS(MAX(-1,MIN(1,[cs])))" },
            { "T", "IF(AND([R]>0,[Nx]>0,[th]>0.0175,[th]<3.124),[R]*TAN([th]/2),0)" },
            { "Tin", "IF([OK],IFERROR(IF(INDEX([Nx^],MATCH([Fr],[To^],0))=ROW(),INDEX([T^],MATCH([Fr],[To^],0)),0),0),0)" },
            { "SL", "IF([OK],IF(AND(ISNA(MATCH([Fr],[To:],0)),ISNA(MATCH([Fr],[Fr:],0))),1,0),0)" },
        };
        foreach (string a in new[] { "x", "y", "z" })
        {
            string A = a.ToUpperInvariant(), d = "u" + a, o = "uo" + a;
            f["P0" + a] = "[" + A + "1]+[" + d + "]*[Tin]";
            f["P1" + a] = "[" + A + "2]-[" + d + "]*[T]";
            f["P5" + a] = "[" + A + "2]+[" + o + "]*[T]";
            f["P2" + a] = "0.5625*[P1" + a + "]+0.375*[" + A + "2]+0.0625*[P5" + a + "]";
            f["P3" + a] = "0.25*[P1" + a + "]+0.5*[" + A + "2]+0.25*[P5" + a + "]";
            f["P4" + a] = "0.0625*[P1" + a + "]+0.375*[" + A + "2]+0.5625*[P5" + a + "]";
        }
        // pieces: a run of elements joined in row order (Pc: the row it starts on) is placed where its first node was
        // reached, else at its Coords entry, else where the element above ended (CAESAR's rule); a piece that meets an
        // already placed piece at a node further down the sheet (a branch entered from its far end) is then moved
        // onto it, in two passes (Rt: placed from the start; R1, R2: placed after pass 1, 2; o: the move)
        f["Pc"] = "IF([OK],IFERROR(INDEX([Pc:],MATCH([Fr],[To:],0)),IFERROR(INDEX([Pc:],MATCH([Fr],[Fr:],0)),ROW())),\"\")";
        f["Rt"] = "IF([OK],IF([Pc]=ROW(),OR(COUNTIF([OK:],TRUE)=0,COUNTIF(Coords!$A:$A,[Fr])>0),INDEX([Rt^],[Pc])),FALSE)";
        for (int k = 1; k <= 2; k++)
        {
            string R = k == 1 ? "Rt" : "R1", A = k == 1 ? "" : "a", c = "c" + k, ok = "[Pc*],\"<>\"&[Pc],[" + R + "*],TRUE";
            string viaTo = "COUNTIFS([To*],[To]," + ok + ")+COUNTIFS([Fr*],[To]," + ok + ")", viaFr = "COUNTIFS([To*],[Fr]," + ok + ")+COUNTIFS([Fr*],[Fr]," + ok + ")";
            f[c] = "IF(AND([OK],NOT([" + R + "])),IF(" + viaTo + ">0,1,IF(" + viaFr + ">0,2,0)),0)";
            // g: the piece's first row that meets a placed piece (a loop that does not close exactly is closed there)
            f["g" + k] = "AND([" + c + "]>0,COUNTIFS([Pc:],[Pc],[" + c + ":],\">0\")=0)";
            foreach (string a3 in new[] { "x", "y", "z" })
            {
                string X = a3.ToUpperInvariant(), p1 = "[" + X + "1" + A + "*]", p2 = "[" + X + "2" + A + "*]";
                if (k == 1) { p1 = "[" + X + "1*]"; p2 = "[" + X + "2*]"; }
                string at = "IF(COUNTIFS([To*],{0}," + ok + ")>0,AVERAGEIFS(" + p2 + ",[To*],{0}," + ok + "),AVERAGEIFS(" + p1 + ",[Fr*],{0}," + ok + "))";
                f["d" + k + a3] = "IF([" + c + "]=1," + string.Format(at, "[To]") + "-[" + X + "2],IF([" + c + "]=2," + string.Format(at, "[Fr]") + "-[" + X + "1],0))";
                f["o" + k + a3] = "IF([" + R + "]," + (k == 1 ? "0" : "[o1" + a3 + "]") + ",SUMIFS([d" + k + a3 + "*],[Pc*],[Pc],[" + c + "*],\">0\",[g" + k + "*],TRUE))";
            }
            f["R" + k] = "IF([OK],OR([" + R + "],COUNTIFS([Pc*],[Pc],[" + c + "*],\">0\")>0),FALSE)";
            string sfx = k == 1 ? "a" : "f";
            foreach (string a3 in new[] { "x", "y", "z" })
            {
                string X = a3.ToUpperInvariant();
                f[X + "1" + sfx] = "IF([OK],[" + X + "1]+[o" + k + a3 + "],\"\")";
                f[X + "2" + sfx] = "IF([OK],[" + X + "2]+[o" + k + a3 + "],\"\")";
            }
        }
        for (int j = 0; j <= 5; j++)
        {
            string x = "([P" + j + "x]+[o2x])", y = "([P" + j + "y]+[o2y])", z = "([P" + j + "z]+[o2z])";
            string zz = "IF(pl_zup,-" + y + "," + z + ")", yy = "IF(pl_zup," + z + "," + y + ")";
            f["U" + j] = "IFERROR(" + x + "*pl_cos_a-" + zz + "*pl_sin_a,\"\")";
            f["V" + j] = "IFERROR(" + yy + "*pl_cos_e-(" + x + "*pl_sin_a+" + zz + "*pl_cos_a)*pl_sin_e,\"\")";
        }
        var first = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "Sc", "IFERROR(" + E("pl_sec") + "&\"\",\"\")" },
            { "LX", "IF(ISNUMBER([X2]),[X2],0)" }, { "LY", "IF(ISNUMBER([Y2]),[Y2],0)" }, { "LZ", "IF(ISNUMBER([Z2]),[Z2],0)" },
            { "X1", "IF([OK],IFERROR(INDEX(Coords!$B:$B,MATCH([Fr],Coords!$A:$A,0))+0,0),\"\")" },
            { "Y1", "IF([OK],IFERROR(INDEX(Coords!$C:$C,MATCH([Fr],Coords!$A:$A,0))+0,0),\"\")" },
            { "Z1", "IF([OK],IFERROR(INDEX(Coords!$D:$D,MATCH([Fr],Coords!$A:$A,0))+0,0),\"\")" },
            { "SL", "IF([OK],1,0)" },
            { "Pc", "IF([OK],ROW(),\"\")" },
        };
        for (int row = 2; row <= last; row++)
            for (int k = 0; k < CalcCols.Length; k++)
            {
                string key = CalcCols[k], tpl;
                if (!(row == 2 && first.TryGetValue(key, out tpl))) tpl = f[key];
                calc.SetFormula(row - 1, k, Expand(tpl, col, row, last), 0);
            }

        // parameters, right of the table; each a named cell
        int pc = CalcCols.Length + 1, pr = 1;
        Action<string, string, string> param = (name, formula, what) =>
        {
            calc.Set(pr, pc, name, XlsxBook.Bold); calc.SetFormula(pr, pc + 1, formula, 0); calc.Set(pr, pc + 2, what, XlsxBook.Note);
            b.Names[name] = Calc + "!$" + XlsxBook.Column(pc + 1) + "$" + (pr + 1);
            pr++;
        };
        b.Names["pl_el"] = "Elements!$1:$1048576";
        b.Names["pl_secs"] = "Sections!$1:$1048576";
        b.Names["pl_rest"] = "Restraints!$1:$1048576";
        b.Names["pl_odl"] = "Lists!$L$2:$L$27";
        b.Names["pl_npl"] = "Lists!$M$2:$M$27";
        b.Names["pl_codes"] = LCodes;
        param("pl_id", "MATCH(\"Id\",Elements!$1:$1,0)", "Elements columns, found by their headers");
        param("pl_from", "MATCH(\"From\",Elements!$1:$1,0)", "");
        param("pl_to", "MATCH(\"To\",Elements!$1:$1,0)", "");
        param("pl_dx", "MATCH(\"DX*\",Elements!$1:$1,0)", "");
        param("pl_dy", "MATCH(\"DY*\",Elements!$1:$1,0)", "");
        param("pl_dz", "MATCH(\"DZ*\",Elements!$1:$1,0)", "");
        param("pl_type", "MATCH(\"Type\",Elements!$1:$1,0)", "");
        param("pl_sec", "MATCH(\"Section\",Elements!$1:$1,0)", "");
        param("pl_bend", "MATCH(\"Bend radius*\",Elements!$1:$1,0)", "");
        param("pl_od", "MATCH(\"OD (*\",Elements!$1:$1,0)", "");
        param("pl_sname", "MATCH(\"Name\",Sections!$1:$1,0)", "Sections columns");
        param("pl_sod", "MATCH(\"OD*\",Sections!$1:$1,0)", "");
        param("pl_rnode", "MATCH(\"Node\",Restraints!$1:$1,0)", "Restraints columns");
        param("pl_rtype", "MATCH(\"Type\",Restraints!$1:$1,0)", "");
        param("pl_zup", "IFERROR(UPPER(TRIM(INDEX(Settings!$B:$B,MATCH(\"Vertical axis\",Settings!$A:$A,0))))=\"Z\",FALSE)", "Z is up (Settings)");
        param("pl_az", "IF(" + vs + "!$C$6=\"Custom\",N(" + vs + "!$C$7),IFERROR(INDEX(Lists!$O$2:$O$6,MATCH(" + vs + "!$C$6,Lists!$H$2:$H$6,0)),45))", "azimuth, deg");
        param("pl_elev", "IF(" + vs + "!$C$6=\"Custom\",N(" + vs + "!$C$8),IFERROR(INDEX(Lists!$P$2:$P$6,MATCH(" + vs + "!$C$6,Lists!$H$2:$H$6,0)),35.264))", "elevation, deg");
        param("pl_cos_a", "COS(RADIANS(pl_az))", "");
        param("pl_sin_a", "SIN(RADIANS(pl_az))", "");
        param("pl_cos_e", "COS(RADIANS(pl_elev))", "");
        param("pl_sin_e", "SIN(RADIANS(pl_elev))", "");
        param("pl_i2l", inToLen.ToString("R", CultureInfo.InvariantCulture), "inches -> the job's length unit");
        param("pl_d2in", diaToIn.ToString("R", CultureInfo.InvariantCulture), "the job's diameter unit -> inches");
        string us = string.Join(",", Enumerable.Range(0, 6).Select(j => "$" + col["U" + j] + "$2:$" + col["U" + j] + "$" + last).ToArray());
        string vsr = string.Join(",", Enumerable.Range(0, 6).Select(j => "$" + col["V" + j] + "$2:$" + col["V" + j] + "$" + last).ToArray());
        param("pl_minu", "MIN(" + us + ")", "the drawing's extent");
        param("pl_maxu", "MAX(" + us + ")", "");
        param("pl_minv", "MIN(" + vsr + ")", "");
        param("pl_maxv", "MAX(" + vsr + ")", "");
        param("pl_cu", "(pl_minu+pl_maxu)/2", "its centre");
        param("pl_cv", "(pl_minv+pl_maxv)/2", "");
        param("pl_h", "MAX(pl_maxu-pl_minu,pl_maxv-pl_minv,1E-6)/2*1.12", "half the square frame (equal scales)");
        param("pl_hl", "TRIM(" + vs + "!$C$10&\"\")", "highlighted element");
        param("pl_lab", "LOWER(TRIM(" + vs + "!$C$11&\"\"))=\"yes\"", "node numbers shown");
        param("pl_shr", "LOWER(TRIM(" + vs + "!$C$12&\"\"))<>\"no\"", "restraints shown");
        calc.Width(pc, 12); calc.Width(pc + 2, 40);

        // ---- PlotPts: the chart's points. A point not drawn has #N/A as its v (y) and 0 as its u (x): Excel and
        // LibreOffice skip it, and an x column stays numeric (a series whose x values are all errors would make
        // LibreOffice number its points 1, 2, 3 ... instead)
        pts.State = "hidden";
        string[] ph = { "pipe u", "pipe v", "highlight u", "highlight v", "rigid u", "rigid v", "EJ u", "EJ v", "", "node", "u", "v", "type", "anchor u", "anchor v", "support u", "support v",
                        "other u", "other v", "", "to u", "to v", "to label", "start u", "start v", "start label", "", "frame u", "frame v", "", "X u", "X v", "X", "Y u", "Y v", "Y", "Z u", "Z v", "Z" };
        for (int k = 0; k < ph.Length; k++) if (ph[k].Length > 0) pts.Set(0, k, ph[k], XlsxBook.Header);
        string calcR = Calc + "!";
        for (int e = 0; e < cap; e++)
        {
            int cr = e + 2;
            for (int j = 0; j <= 5; j++)
            {
                // each element is drawn from its end back to its start, so its first point is never where the points
                // of the element before it ended (LibreOffice merges such points across the empty row and drops the
                // element's first segment); the first point is also moved by 0.1 % of the half frame, under the line
                int row = 1 + e * 7 + j, xr = row + 1, pj = 5 - j;
                string u = calcR + col["U" + pj] + cr, v = calcR + col["V" + pj] + cr;
                pts.SetFormula(row, 0, "IF(ISNUMBER(" + u + ")," + u + "-pl_cu" + (j == 0 ? "+0.001*pl_h" : "") + ",0)", 0);
                pts.SetFormula(row, 1, "IF(ISNUMBER(" + v + ")," + v + "-pl_cv,NA())", 0);
                string hl = "AND(pl_hl<>\"\"," + calcR + col["Id"] + cr + "=pl_hl)";
                pts.SetFormula(row, 2, "IF(" + hl + ",A" + xr + ",0)", 0);
                pts.SetFormula(row, 3, "IF(" + hl + ",B" + xr + ",NA())", 0);
                pts.SetFormula(row, 4, "IF(" + calcR + col["Kd"] + cr + "=1,A" + xr + ",0)", 0);
                pts.SetFormula(row, 5, "IF(" + calcR + col["Kd"] + cr + "=1,B" + xr + ",NA())", 0);
                pts.SetFormula(row, 6, "IF(" + calcR + col["Kd"] + cr + "=2,A" + xr + ",0)", 0);
                pts.SetFormula(row, 7, "IF(" + calcR + col["Kd"] + cr + "=2,B" + xr + ",NA())", 0);
            }
            // the row after an element's points stays empty: the chart's lines break there
        }
        int lastPt = 1 + cap * 7;
        // restraints: row r = the Restraints sheet's row r
        string toR = calcR + "$" + col["To"] + "$2:$" + col["To"] + "$" + last, frR = calcR + "$" + col["Fr"] + "$2:$" + col["Fr"] + "$" + last;
        Func<string, string> colR = c => calcR + "$" + col[c] + "$2:$" + col[c] + "$" + last;
        for (int rr = 2; rr <= capR + 1; rr++)
        {
            int row = rr - 1;
            string n = "J" + rr, ty = "M" + rr;
            pts.SetFormula(row, 9, "IFERROR(INDEX(pl_rest,ROW(),pl_rnode)+0,\"\")", 0);
            pts.SetFormula(row, 10, "IF(ISNUMBER(" + n + "),IFERROR(INDEX(" + colR("U5") + ",MATCH(" + n + "," + toR + ",0))-pl_cu,IFERROR(INDEX(" + colR("U0") + ",MATCH(" + n + "," + frR + ",0))-pl_cu,NA())),NA())", 0);
            pts.SetFormula(row, 11, "IF(ISNUMBER(" + n + "),IFERROR(INDEX(" + colR("V5") + ",MATCH(" + n + "," + toR + ",0))-pl_cv,IFERROR(INDEX(" + colR("V0") + ",MATCH(" + n + "," + frR + ",0))-pl_cv,NA())),NA())", 0);
            pts.SetFormula(row, 12, "IFERROR(UPPER(TRIM(INDEX(pl_rest,ROW(),pl_rtype)&\"\")),\"\")", 0);
            string vert = "OR(" + ty + "=IF(pl_zup,\"Z\",\"Y\")," + ty + "=IF(pl_zup,\"+Z\",\"+Y\")," + ty + "=IF(pl_zup,\"-Z\",\"-Y\"))";
            pts.SetFormula(row, 13, "IF(AND(pl_shr," + ty + "=\"ANC\",ISNUMBER(K" + rr + ")),K" + rr + ",0)", 0);
            pts.SetFormula(row, 14, "IF(AND(pl_shr," + ty + "=\"ANC\"),L" + rr + ",NA())", 0);
            pts.SetFormula(row, 15, "IF(AND(pl_shr," + vert + ",ISNUMBER(K" + rr + ")),K" + rr + ",0)", 0);
            pts.SetFormula(row, 16, "IF(AND(pl_shr," + vert + "),L" + rr + ",NA())", 0);
            pts.SetFormula(row, 17, "IF(AND(pl_shr," + ty + "<>\"\"," + ty + "<>\"ANC\",NOT(" + vert + "),ISNUMBER(K" + rr + ")),K" + rr + ",0)", 0);
            pts.SetFormula(row, 18, "IF(AND(pl_shr," + ty + "<>\"\"," + ty + "<>\"ANC\",NOT(" + vert + ")),L" + rr + ",NA())", 0);
        }
        // node labels: row r = PlotCalc's row r (the element's To node, at its far point; a piece's first From node)
        for (int cr = 2; cr <= last; cr++)
        {
            int row = cr - 1;
            string u5 = calcR + col["U5"] + cr, v5 = calcR + col["V5"] + cr, u0 = calcR + col["U0"] + cr, v0 = calcR + col["V0"] + cr, sl = calcR + col["SL"] + cr;
            pts.SetFormula(row, 20, "IF(ISNUMBER(" + u5 + ")," + u5 + "-pl_cu,0)", 0);
            pts.SetFormula(row, 21, "IF(ISNUMBER(" + v5 + ")," + v5 + "-pl_cv,NA())", 0);
            pts.SetFormula(row, 22, "IF(AND(pl_lab,ISNUMBER(" + u5 + "))," + calcR + col["To"] + cr + "&\"\",\"\")", 0);
            pts.SetFormula(row, 23, "IF(AND(" + sl + "=1,ISNUMBER(" + u0 + "))," + u0 + "-pl_cu,0)", 0);
            pts.SetFormula(row, 24, "IF(AND(" + sl + "=1,ISNUMBER(" + v0 + "))," + v0 + "-pl_cv,NA())", 0);
            pts.SetFormula(row, 25, "IF(AND(pl_lab," + sl + "=1)," + calcR + col["Fr"] + cr + "&\"\",\"\")", 0);
        }
        // the frame: a square around the drawing, so both axes take the same scale
        pts.SetFormula(1, 27, "-pl_h", 0); pts.SetFormula(1, 28, "-pl_h", 0); pts.SetFormula(2, 27, "pl_h", 0); pts.SetFormula(2, 28, "pl_h", 0);
        // the axis triad, bottom left: X, Y, Z of the model projected like the pipe
        string[][] axes = { new[] { "1", "0", "0", "X" }, new[] { "0", "1", "0", "Y" }, new[] { "0", "0", "1", "Z" } };
        for (int a = 0; a < 3; a++)
        {
            string x = axes[a][0], y = axes[a][1], z = axes[a][2];
            string zz = "IF(pl_zup,-" + y + "," + z + ")", yy = "IF(pl_zup," + z + "," + y + ")";
            string pu = "(" + x + "*pl_cos_a-" + zz + "*pl_sin_a)", pv = "(" + yy + "*pl_cos_e-(" + x + "*pl_sin_a+" + zz + "*pl_cos_a)*pl_sin_e)";
            int c0 = 30 + a * 3;
            pts.SetFormula(1, c0, "-0.82*pl_h", 0); pts.SetFormula(1, c0 + 1, "-0.82*pl_h", 0);
            pts.SetFormula(2, c0, "-0.82*pl_h+0.13*pl_h*" + pu, 0); pts.SetFormula(2, c0 + 1, "-0.82*pl_h+0.13*pl_h*" + pv, 0);
            pts.Set(2, c0 + 2, axes[a][3]);
        }

        // ---- the 3D View sheet: controls, figures, the chart
        view.ShowGrid = false;
        view.TabColor = "6A1B9A";
        view.Width(0, 2); view.Width(1, 22); view.Width(2, 26); view.Width(3, 2);
        view.Set(1, 1, "3D view", t.Title); view.Height(1, 30);
        view.Set(2, 1, "Live: follows Elements, Sections, Restraints and Coords as you edit them.", t.Subtitle);
        view.Set(4, 1, "View", t.Section); view.Style(4, 2, t.Section);
        view.Set(5, 1, "Direction", t.Label); view.Set(5, 2, "Iso", t.Value);
        view.Validate("C6", new XlsxValidation { Type = "list", Formula1 = LView, PromptTitle = "View", Prompt = "Iso (CAESAR's), Plan (from above), Front (along -Z), Side (along -X), or Custom: give the azimuth and elevation below.", ErrorTitle = "A view", Error = "Pick a view from the list." });
        view.Set(6, 1, "Azimuth (deg)", t.Label); view.Set(6, 2, 45, t.Value);
        view.Set(7, 1, "Elevation (deg)", t.Label); view.Set(7, 2, 35.264, t.Value);
        foreach (string a in new[] { "C7", "C8" })
            view.Validate(a, new XlsxValidation { Type = "decimal", Operator = "between", Formula1 = "-360", Formula2 = "360", PromptTitle = "Custom view", Prompt = "Used when the view is Custom. Azimuth turns the model about the vertical axis; elevation tilts it toward you (90 = from above).", ErrorTitle = "Degrees", Error = "An angle in degrees, -360 to 360." });
        view.Conditional("C7:C8", "$C$6<>\"Custom\"", t.Muted);
        view.Set(9, 1, "Highlight element", t.Label); view.Set(9, 2, "", t.Value);
        view.Validate("C10", new XlsxValidation { Type = "list", Formula1 = "Elements!$A$2:$A$" + RefRows, ShowError = false, PromptTitle = "Highlight", Prompt = "An element Id (E12): drawn thick and orange. Blank = none." });
        view.Set(10, 1, "Node numbers", t.Label); view.Set(10, 2, "no", t.Value);
        view.Set(11, 1, "Restraints", t.Label); view.Set(11, 2, "yes", t.Value);
        foreach (string a in new[] { "C11", "C12" })
            view.Validate(a, new XlsxValidation { Type = "list", Formula1 = LYesNo, PromptTitle = a == "C11" ? "Node numbers" : "Restraints", Prompt = a == "C11" ? "yes: label the nodes (Excel 2013 and later)." : "yes: draw the restraints as markers." });
        view.Set(13, 1, "Figures", t.Section); view.Style(13, 2, t.Section);
        string X2 = calcR + "$" + col["X2"] + "$2:$" + col["X2"] + "$" + last;
        Func<string, string, string> ext = (a1, a2) => "MAX(" + calcR + "$" + col[a1] + "$2:$" + col[a1] + "$" + last + "," + calcR + "$" + col[a2] + "$2:$" + col[a2] + "$" + last + ")-MIN(" +
                                                        calcR + "$" + col[a1] + "$2:$" + col[a1] + "$" + last + "," + calcR + "$" + col[a2] + "$2:$" + col[a2] + "$" + last + ")";
        string[][] figs =
        {
            new[] { "Highlighted", "IF(pl_hl=\"\",\"-\",INDEX(" + colR("Fr") + ",MATCH(pl_hl," + colR("Id") + ",0))&\"-\"&INDEX(" + colR("To") + ",MATCH(pl_hl," + colR("Id") + ",0))&\", \"&TEXT(INDEX(" +
                    colR("L") + ",MATCH(pl_hl," + colR("Id") + ",0)),\"0.###\")&\" " + lenUnit + ", \"&INDEX(" + colR("Ty") + ",MATCH(pl_hl," + colR("Id") + ",0)))" },
            new[] { "Elements drawn", "COUNT(" + X2 + ")" },
            new[] { "Rows not drawn", "MAX(0,COUNTA(INDEX(pl_el,0,pl_from))+COUNTA(INDEX(pl_el,0,pl_to))-2-2*COUNT(" + X2 + "))/2" },
            new[] { "Pipe length (" + lenUnit + ")", "SUM(" + calcR + "$" + col["L"] + "$2:$" + col["L"] + "$" + last + ")" },
            new[] { "Extent X (" + lenUnit + ")", ext("X1f", "X2f") },
            new[] { "Extent Y (" + lenUnit + ")", ext("Y1f", "Y2f") },
            new[] { "Extent Z (" + lenUnit + ")", ext("Z1f", "Z2f") },
        };
        for (int i = 0; i < figs.Length; i++) { view.Set(14 + i, 1, figs[i][0], t.Label); view.SetFormula(14 + i, 2, "IFERROR(" + figs[i][1] + ",\"-\")", t.Stat); }
        view.Conditional("C17", "AND(ISNUMBER(C17),C17>0)", t.Amber);
        view.Set(22, 1, "Key", t.Section); view.Style(22, 2, t.Section);
        string[][] legend =
        {
            new[] { "\u2501\u2501\u2501", "1F4E79", "Pipe" }, new[] { "\u2501\u2501\u2501", "37474F", "Rigid / valve / flange" }, new[] { "\u254D\u254D\u254D", "2E7D32", "Expansion joint" },
            new[] { "\u2501\u2501\u2501", "F57C00", "Highlighted element" }, new[] { "\u25A0", "C62828", "Anchor" }, new[] { "\u25B2", "43A047", "Vertical support" },
            new[] { "\u25C6", "8E24AA", "Other restraint" }, new[] { "X Y Z", "1565C0", "Axes (X blue, Y green, Z red)" },
        };
        for (int i = 0; i < legend.Length; i++)
        {
            view.Set(23 + i, 1, legend[i][0], b.Style(new XlsxStyle { Bold = true, Color = legend[i][1], Align = "center" }));
            view.Set(23 + i, 2, legend[i][2], t.Body);
        }
        view.Set(32, 1, "Bends are drawn with their radius (a name - LR, SR, 3D, 5D, or one CAESAR wrote - from the section's OD). A CAESAR code that is no radius is drawn as a corner. " +
                        "Nodes placed on a bend's curve, and elements past the first " + cap + " rows, are not drawn. Rows not drawn: their From / To are missing or not numbers.", t.Small);
        view.Merge("B33:C41");

        // a square chart with no title, legend or axes: its plot area is square, and the frame makes both scales the
        // same. (A manual plot-area layout would fix the square exactly, but LibreOffice then drops line segments.)
        var ch = new XlsxChart { Col = 4, Row = 1, WidthCm = 19, HeightCm = 19, Legend = false };
        string P = Pts + "!";
        Func<string, int, int, string> rng = (c, r0, r1) => P + "$" + c + "$" + r0 + ":$" + c + "$" + r1;
        ch.Series.Add(new XlsxChartSeries { Name = "Pipe", X = rng("A", 2, lastPt), Y = rng("B", 2, lastPt), Line = "1F4E79", LineWidth = 2.25 });
        ch.Series.Add(new XlsxChartSeries { Name = "Rigid / valve / flange", X = rng("E", 2, lastPt), Y = rng("F", 2, lastPt), Line = "37474F", LineWidth = 6 });
        ch.Series.Add(new XlsxChartSeries { Name = "Expansion joint", X = rng("G", 2, lastPt), Y = rng("H", 2, lastPt), Line = "2E7D32", LineWidth = 5, Dashed = true });
        ch.Series.Add(new XlsxChartSeries { Name = "Highlighted", X = rng("C", 2, lastPt), Y = rng("D", 2, lastPt), Line = "F57C00", LineWidth = 5, Marker = "circle", MarkerSize = 9, MarkerFill = "FFB74D", MarkerLine = "E65100" });
        ch.Series.Add(new XlsxChartSeries { Name = "Anchor", X = rng("N", 2, capR + 1), Y = rng("O", 2, capR + 1), Marker = "square", MarkerSize = 9, MarkerFill = "C62828", MarkerLine = "7F0000" });
        ch.Series.Add(new XlsxChartSeries { Name = "Vertical support", X = rng("P", 2, capR + 1), Y = rng("Q", 2, capR + 1), Marker = "triangle", MarkerSize = 9, MarkerFill = "43A047", MarkerLine = "1B5E20" });
        ch.Series.Add(new XlsxChartSeries { Name = "Other restraint", X = rng("R", 2, capR + 1), Y = rng("S", 2, capR + 1), Marker = "diamond", MarkerSize = 8, MarkerFill = "8E24AA", MarkerLine = "4A148C" });
        ch.Series.Add(new XlsxChartSeries { Name = "Nodes", X = rng("U", 2, last), Y = rng("V", 2, last), Labels = rng("W", 2, last), InLegend = false });
        ch.Series.Add(new XlsxChartSeries { Name = "Starts", X = rng("X", 2, last), Y = rng("Y", 2, last), Labels = rng("Z", 2, last), InLegend = false });
        ch.Series.Add(new XlsxChartSeries { Name = "Frame", X = rng("AB", 2, 3), Y = rng("AC", 2, 3), InLegend = false });
        string[] axisColor = { "1565C0", "2E7D32", "C62828" };
        for (int a = 0; a < 3; a++)
        {
            string cu = XlsxBook.Column(30 + a * 3), cv = XlsxBook.Column(31 + a * 3), cl = XlsxBook.Column(32 + a * 3);
            ch.Series.Add(new XlsxChartSeries { Name = "XYZ"[a] + " axis", X = rng(cu, 2, 3), Y = rng(cv, 2, 3), Line = axisColor[a], LineWidth = 1.75, Labels = rng(cl, 2, 3), LabelColor = axisColor[a], LabelSize = 9, InLegend = false });
        }
        view.Charts.Add(ch);
    }
}
