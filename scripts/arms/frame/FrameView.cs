using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

// FrameView  --  the frame workbook's live 3D view (2026-11-05), on the kit's WbPlot (vault/arms/workbook-kit.md). The
// 3D View sheet draws the model as an Excel XY chart that follows Joints, Frames, Releases, Sections and Links as they
// are edited: steel and pipe frames joint to joint, the highlighted frame, pinned ends (a circle just inside an end
// whose M2 or M3 its release preset frees: solid and wider than a line, as LibreOffice draws markers under the lines
// and Excel over them), fixed and other supports from Joints' Restraint, links (dashed, or a
// diamond for a one-joint link), joint labels, the frame square and the axis triad. Z is up (SAP).
//   PlotCalc (hidden)  a frame block, row = the Frames row: its joints' rows on Joints, length, kind (pipe: a Pipe section
//                      or Kind PIPE / DUCT), pins, the projected ends; and a joint block, row = the Joints row:
//                      coordinates, projection, restraint. Then the named parameters (WbPlot).
//   PlotPts (hidden)   the chart's series: each frame drawn J to I with a gap row after it (WbPlot's rule), pins, links,
//                      supports, labels, the frame and the triad.
// Above Most frames the view is a note only (the formulas would make the workbook slow). C# 5 (prefix Frame).

static class FrameView
{
    internal const string View = "3D View", Calc = "PlotCalc", Pts = "PlotPts";
    internal const int Most = 8000;
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // PlotCalc: the frame block, a gap, the joint block
    static readonly string[] Cols = { "Fr", "mI", "mJ", "OK", "L", "Kd", "mR", "pI", "pJ", "UI", "VI", "UJ", "VJ", "", "Jt", "JX", "JY", "JZ", "JU", "JV", "Rs", "Fx" };

    static string In(string sheet, string c) { return "INDEX(" + sheet + "!$" + c + ":$" + c + ",ROW())"; }

    // view: the 3D View sheet; frames / joints / links: the model's counts (rows for 200 more each); refRows: the input
    // sheets' rows; lists: the Lists ranges of the views, their azimuths and elevations
    internal static void Sheets(XlsxBook b, WbTheme t, XlsxBookSheet view, XlsxBookSheet calc, XlsxBookSheet pts, int frames, int joints, int links, int refRows,
                                string lenUnit, string viewList, string azList, string elList, string yesNo)
    {
        if (frames > Most)
        {
            view.ShowGrid = false; view.TabColor = "6A1B9A";
            view.Width(0, 2); view.Width(1, 90);
            view.Set(1, 1, "3D view", t.Title); view.Height(1, 30);
            view.Set(2, 1, "Not drawn: the model has " + frames.ToString(Inv) + " frames, more than the workbook draws (" + Most.ToString(Inv) + "). Its viewer (Build-Frame.ps1 -Preview) shows it whole.", t.Subtitle);
            calc.State = "hidden"; pts.State = "hidden";
            return;
        }
        int capF = frames + 200, capJ = joints + 200, capL = links + 200;
        int lastF = capF + 1, lastJ = capJ + 1, last = Math.Max(lastF, lastJ);
        string vs = XlsxBook.SheetRef(View), J = FrameWorkbook.SJoints, F = FrameWorkbook.SFrames;

        // ---- PlotCalc
        calc.State = "hidden";
        var col = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int k = 0; k < Cols.Length; k++) if (Cols[k].Length > 0) { col[Cols[k]] = XlsxBook.Column(k); calc.Set(0, k, Cols[k], XlsxBook.Header); }
        string kind = "UPPER(TRIM(" + In(F, "G") + "&\"\"))";
        string shape = "IFERROR(INDEX(" + XlsxBook.SheetRef(FrameWorkbook.SSections) + "!$C:$C,MATCH(" + In(F, "F") + "," + XlsxBook.SheetRef(FrameWorkbook.SSections) + "!$B:$B,0))&\"\",\"\")";
        string rel = XlsxBook.SheetRef(FrameWorkbook.SReleases) + "!";
        Func<string, string> yes = c => "LOWER(TRIM(INDEX(" + rel + "$" + c + ":$" + c + ",[mR])&\"\"))=\"yes\"";
        var fFrame = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "Fr", "IFERROR(" + In(F, "B") + "&\"\",\"\")" },
            { "mI", "IF([Fr]=\"\",0,IFERROR(MATCH(" + In(F, "C") + "," + J + "!$B:$B,0),0))" },
            { "mJ", "IF([Fr]=\"\",0,IFERROR(MATCH(" + In(F, "D") + "," + J + "!$B:$B,0),0))" },
            { "OK", "IFERROR(AND([mI]>0,[mJ]>0,[mI]<>[mJ],ISNUMBER(INDEX([JU^],[mI])),ISNUMBER(INDEX([JU^],[mJ]))),FALSE)" },
            { "L", "IF([OK],SQRT((INDEX([JX^],[mJ])-INDEX([JX^],[mI]))^2+(INDEX([JY^],[mJ])-INDEX([JY^],[mI]))^2+(INDEX([JZ^],[mJ])-INDEX([JZ^],[mI]))^2),0)" },
            { "Kd", "IF(OR(LEFT(" + kind + ",4)=\"PIPE\",LEFT(" + kind + ",4)=\"DUCT\"," + shape + "=\"Pipe\"),1,0)" },
            // a blank Release is FIXED
            { "mR", "IF([Fr]=\"\",0,IFERROR(MATCH(IF(TRIM(" + In(F, "I") + "&\"\")=\"\",\"FIXED\",TRIM(" + In(F, "I") + "&\"\"))," + rel + "$A:$A,0),0))" },
            { "pI", "IF([mR]>0,OR(" + yes("F") + "," + yes("G") + "),FALSE)" },
            { "pJ", "IF([mR]>0,OR(" + yes("L") + "," + yes("M") + "),FALSE)" },
            { "UI", "IF([OK],INDEX([JU^],[mI]),\"\")" }, { "VI", "IF([OK],INDEX([JV^],[mI]),\"\")" },
            { "UJ", "IF([OK],INDEX([JU^],[mJ]),\"\")" }, { "VJ", "IF([OK],INDEX([JV^],[mJ]),\"\")" },
        };
        Func<string, string> coord = c => "IF([Jt]=\"\",\"\",IFERROR(" + In(J, c) + "+0,\"\"))";
        string have = "AND(ISNUMBER([JX]),ISNUMBER([JY]),ISNUMBER([JZ]))";
        string rs = "[Rs]";
        var fJoint = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "Jt", "IFERROR(" + In(J, "B") + "&\"\",\"\")" },
            { "JX", coord("C") }, { "JY", coord("D") }, { "JZ", coord("E") },
            { "JU", "IF(" + have + "," + WbPlot.U("[JX]", "[JY]", "[JZ]") + ",\"\")" },
            { "JV", "IF(" + have + "," + WbPlot.V("[JX]", "[JY]", "[JZ]") + ",\"\")" },
            { "Rs", "IFERROR(UPPER(TRIM(" + In(J, "F") + "&\"\")),\"\")" },
            { "Fx", "OR(" + rs + "=\"FIXED\",AND(" + string.Join(",", new[] { "U1", "U2", "U3", "R1", "R2", "R3" }.Select(d => "ISNUMBER(SEARCH(\"" + d + "\"," + rs + "))").ToArray()) + "))" },
        };
        for (int row = 2; row <= last; row++)
        {
            foreach (var kv in fFrame) if (row <= lastF) calc.SetFormula(row - 1, Array.IndexOf(Cols, kv.Key), WbCalc.Expand(kv.Value, col, row, last), 0);
            foreach (var kv in fJoint) if (row <= lastJ) calc.SetFormula(row - 1, Array.IndexOf(Cols, kv.Key), WbCalc.Expand(kv.Value, col, row, last), 0);
        }
        Func<string, int, string> range = (k, n) => "$" + col[k] + "$2:$" + col[k] + "$" + n.ToString(Inv);

        var pl = new WbPlot(b, calc, Calc, Cols.Length + 1);
        pl.Param("pl_zup", "TRUE", "Z is up (SAP)");
        pl.Angles(vs, viewList, azList, elList);
        pl.Extents(range("JU", lastJ), range("JV", lastJ));
        pl.Param("pl_hl", "TRIM(" + vs + "!$C$10&\"\")", "highlighted frame");
        pl.Param("pl_lab", "LOWER(TRIM(" + vs + "!$C$11&\"\"))=\"yes\"", "joint labels shown");
        pl.Param("pl_shr", "LOWER(TRIM(" + vs + "!$C$12&\"\"))<>\"no\"", "supports shown");
        pl.Param("pl_rel", "LOWER(TRIM(" + vs + "!$C$13&\"\"))<>\"no\"", "pinned ends shown");
        pl.Param("pl_lnk", "LOWER(TRIM(" + vs + "!$C$14&\"\"))<>\"no\"", "links shown");
        pl.Widths();

        // ---- PlotPts. A point not drawn has #N/A as its v and 0 as its u (WbPlot: Excel and LibreOffice skip it)
        pts.State = "hidden";
        string[] ph = { "steel u", "steel v", "pipe u", "pipe v", "highlight u", "highlight v", "", "pin u", "pin v", "", "link u", "link v", "grounded u", "grounded v", "",
                        "fixed u", "fixed v", "support u", "support v", "joint u", "joint v", "joint", "", "", "", "", "", "frame u", "frame v", "", "X u", "X v", "X", "Y u", "Y v", "Y", "Z u", "Z v", "Z" };
        for (int k = 0; k < ph.Length; k++) if (ph[k].Length > 0) pts.Set(0, k, ph[k], XlsxBook.Header);
        string c0 = Calc + "!";
        Func<string, int, string> at = (k, r) => c0 + col[k] + r.ToString(Inv);
        for (int e = 0; e < capF; e++)
        {
            int cr = e + 2;
            string ok = at("OK", cr), kd = at("Kd", cr);
            for (int j = 0; j < 2; j++)
            {
                // each frame drawn from J back to I, its first point moved by 0.1 % of the half frame (WbPlot's rule)
                int row = 1 + e * 3 + j;
                string u = j == 0 ? at("UJ", cr) + "-pl_cu+0.001*pl_h" : at("UI", cr) + "-pl_cu", v = (j == 0 ? at("VJ", cr) : at("VI", cr)) + "-pl_cv";
                pts.SetFormula(row, 0, "IF(AND(" + ok + "," + kd + "=0)," + u + ",0)", 0);
                pts.SetFormula(row, 1, "IF(AND(" + ok + "," + kd + "=0)," + v + ",NA())", 0);
                pts.SetFormula(row, 2, "IF(AND(" + ok + "," + kd + "=1)," + u + ",0)", 0);
                pts.SetFormula(row, 3, "IF(AND(" + ok + "," + kd + "=1)," + v + ",NA())", 0);
                string hl = "AND(" + ok + ",pl_hl<>\"\"," + at("Fr", cr) + "=pl_hl)";
                pts.SetFormula(row, 4, "IF(" + hl + "," + u + ",0)", 0);
                pts.SetFormula(row, 5, "IF(" + hl + "," + v + ",NA())", 0);
            }
            // pins: 8 % in from the end they are at
            for (int j = 0; j < 2; j++)
            {
                int row = 1 + e * 2 + j;
                string near = j == 0 ? "I" : "J", far = j == 0 ? "J" : "I", show = "AND(pl_rel," + ok + "," + at("p" + near, cr) + ")";
                pts.SetFormula(row, 7, "IF(" + show + "," + at("U" + near, cr) + "+0.08*(" + at("U" + far, cr) + "-" + at("U" + near, cr) + ")-pl_cu,0)", 0);
                pts.SetFormula(row, 8, "IF(" + show + "," + at("V" + near, cr) + "+0.08*(" + at("V" + far, cr) + "-" + at("V" + near, cr) + ")-pl_cv,NA())", 0);
            }
        }
        int lastSeg = 1 + capF * 3, lastPin = 1 + capF * 2;
        // links: row = 3 per link (I, J, a gap); a one-joint link is a marker at its joint
        string lk = XlsxBook.SheetRef(FrameWorkbook.SLinks) + "!", ju = c0 + "$" + col["JU"] + "$1:$" + col["JU"] + "$" + lastJ, jv = c0 + "$" + col["JV"] + "$1:$" + col["JV"] + "$" + lastJ;
        for (int k = 0; k < capL; k++)
        {
            string lr = (k + 2).ToString(Inv), ci = lk + "$C$" + lr, cj = lk + "$D$" + lr;
            Func<string, string, string> pu = (cell, fallback) => "IFERROR(INDEX(" + ju + ",MATCH(" + cell + "," + J + "!$B:$B,0))-pl_cu," + fallback + ")";
            Func<string, string, string> pv = (cell, fallback) => "IFERROR(INDEX(" + jv + ",MATCH(" + cell + "," + J + "!$B:$B,0))-pl_cv," + fallback + ")";
            string two = "AND(pl_lnk," + ci + "&\"\"<>\"\"," + cj + "&\"\"<>\"\")", one = "AND(pl_lnk," + ci + "&\"\"<>\"\"," + cj + "&\"\"=\"\")";
            int row = 1 + k * 3;
            pts.SetFormula(row, 10, "IF(" + two + "," + pu(cj, "0") + ",0)", 0);
            pts.SetFormula(row, 11, "IF(" + two + "," + pv(cj, "NA()") + ",NA())", 0);
            pts.SetFormula(row + 1, 10, "IF(" + two + "," + pu(ci, "0") + ",0)", 0);
            pts.SetFormula(row + 1, 11, "IF(" + two + "," + pv(ci, "NA()") + ",NA())", 0);
            pts.SetFormula(row, 12, "IF(" + one + "," + pu(ci, "0") + ",0)", 0);
            pts.SetFormula(row, 13, "IF(" + one + "," + pv(ci, "NA()") + ",NA())", 0);
        }
        int lastLk = 1 + capL * 3;
        // supports and labels: row = the Joints row
        for (int cr = 2; cr <= lastJ; cr++)
        {
            int row = cr - 1;
            string u = at("JU", cr), v = at("JV", cr), num = "ISNUMBER(" + u + ")";
            string fx = "AND(pl_shr," + num + "," + at("Fx", cr) + ")", sp = "AND(pl_shr," + num + "," + at("Rs", cr) + "<>\"\",NOT(" + at("Fx", cr) + "))", lb = "AND(pl_lab," + num + ")";
            pts.SetFormula(row, 15, "IF(" + fx + "," + u + "-pl_cu,0)", 0); pts.SetFormula(row, 16, "IF(" + fx + "," + v + "-pl_cv,NA())", 0);
            pts.SetFormula(row, 17, "IF(" + sp + "," + u + "-pl_cu,0)", 0); pts.SetFormula(row, 18, "IF(" + sp + "," + v + "-pl_cv,NA())", 0);
            pts.SetFormula(row, 19, "IF(" + lb + "," + u + "-pl_cu,0)", 0); pts.SetFormula(row, 20, "IF(" + lb + "," + v + "-pl_cv,NA())", 0);
            pts.SetFormula(row, 21, "IF(" + lb + "," + at("Jt", cr) + ",\"\")", 0);
        }
        WbPlot.Square(pts);

        // ---- the 3D View sheet
        WbPlot.Controls(t, view, "Live: follows Joints, Frames, Releases, Sections and Links as you edit them.", viewList,
                        "Iso, Plan (from above), Front (along +Y), Side (along -X), or Custom: give the azimuth and elevation below.");
        view.Set(9, 1, "Highlight frame", t.Label); view.Set(9, 2, "", t.Value);
        view.Validate("C10", new XlsxValidation { Type = "list", Formula1 = F + "!$B$2:$B$" + refRows.ToString(Inv), ShowError = false, PromptTitle = "Highlight", Prompt = "A frame's label: drawn thick and orange. Blank = none." });
        string[][] toggles = { new[] { "Joint labels", "no", "yes: label the joints (Excel 2013 and later)." }, new[] { "Supports", "yes", "yes: draw the joints' restraints." },
                               new[] { "Pinned ends", "yes", "yes: a circle just inside each end whose M2 or M3 its release preset frees." }, new[] { "Links", "yes", "yes: draw the links." } };
        for (int i = 0; i < toggles.Length; i++)
        {
            view.Set(10 + i, 1, toggles[i][0], t.Label); view.Set(10 + i, 2, toggles[i][1], t.Value);
            view.Validate("C" + (11 + i).ToString(Inv), new XlsxValidation { Type = "list", Formula1 = yesNo, PromptTitle = toggles[i][0], Prompt = toggles[i][2] });
        }
        view.Set(15, 1, "Figures", t.Section); view.Style(15, 2, t.Section);
        Func<string, string> cr2 = k => c0 + range(k, k[0] == 'J' && k != "Jt" ? lastJ : lastF);
        string fr = cr2("Fr"), okR = cr2("OK"), lR = cr2("L"), kdR = cr2("Kd");
        Func<string, string> ext = k => "MAX(" + cr2(k) + ")-MIN(" + cr2(k) + ")";
        Func<string, string> pick = k => "INDEX(" + c0 + range(k, lastF) + ",MATCH(pl_hl," + fr + ",0))";
        string[][] figs =
        {
            new[] { "Highlighted", "IF(pl_hl=\"\",\"-\",INDEX(" + F + "!$C$2:$C$" + lastF + ",MATCH(pl_hl," + fr + ",0))&\"-\"&INDEX(" + F + "!$D$2:$D$" + lastF + ",MATCH(pl_hl," + fr + ",0))&\", \"&TEXT(" + pick("L") + ",\"0.###\")&\" " + lenUnit + ", \"&INDEX(" + F + "!$F$2:$F$" + lastF + ",MATCH(pl_hl," + fr + ",0)))" },
            new[] { "Frames drawn", "COUNTIF(" + okR + ",TRUE)" },
            new[] { "Rows not drawn", "COUNTIF(" + fr + ",\"?*\")-COUNTIF(" + okR + ",TRUE)" },
            new[] { "Steel length (" + lenUnit + ")", "SUMIFS(" + lR + "," + kdR + ",0," + okR + ",TRUE)" },
            new[] { "Pipe length (" + lenUnit + ")", "SUMIFS(" + lR + "," + kdR + ",1," + okR + ",TRUE)" },
            new[] { "Extent X (" + lenUnit + ")", ext("JX") },
            new[] { "Extent Y (" + lenUnit + ")", ext("JY") },
            new[] { "Extent Z (" + lenUnit + ")", ext("JZ") },
        };
        for (int i = 0; i < figs.Length; i++) { view.Set(16 + i, 1, figs[i][0], t.Label); view.SetFormula(16 + i, 2, "IFERROR(" + figs[i][1] + ",\"-\")", t.Stat); }
        view.Conditional("C19", "AND(ISNUMBER(C19),C19>0)", t.Amber);
        view.Set(25, 1, "Key", t.Section); view.Style(25, 2, t.Section);
        WbPlot.Key(b, t, view, 26, new[]
        {
            new[] { "\u2501\u2501\u2501", "546E7A", "Steel" }, new[] { "\u2501\u2501\u2501", "1565C0", "Pipe / duct (a Pipe section, or Kind PIPE / DUCT)" },
            new[] { "\u2501\u2501\u2501", "F57C00", "Highlighted frame" }, new[] { "\u25CF", "E53935", "Pinned end (M2 or M3 released)" },
            new[] { "\u25A0", "C62828", "Fixed support" }, new[] { "\u25B2", "43A047", "Other support" },
            new[] { "\u254D\u254D\u254D", "8E24AA", "Link" }, new[] { "\u25C6", "8E24AA", "One-joint (grounded) link" },
            new[] { "X Y Z", "1565C0", "Axes (X blue, Y green, Z red)" },
        });
        view.Set(36, 1, "Frames are drawn joint to joint: insertion points and offsets are not shown. Rows not drawn: a joint not on Joints (or without X Y Z), " +
                        "or a frame from a joint to itself. Frames past the first " + capF.ToString(Inv) + " rows, joints past " + capJ.ToString(Inv) + " and links past " + capL.ToString(Inv) +
                        " are not drawn: Refresh the workbook to follow a model that grew.", t.Small);
        view.Merge("B37:C45");

        var ch = new XlsxChart { Col = 4, Row = 1, WidthCm = 19, HeightCm = 19, Legend = false };
        string P = Pts + "!";
        Func<string, int, int, string> rng = (c, r0, r1) => P + "$" + c + "$" + r0 + ":$" + c + "$" + r1;
        ch.Series.Add(new XlsxChartSeries { Name = "Steel", X = rng("A", 2, lastSeg), Y = rng("B", 2, lastSeg), Line = "546E7A", LineWidth = 2.25 });
        ch.Series.Add(new XlsxChartSeries { Name = "Pipe", X = rng("C", 2, lastSeg), Y = rng("D", 2, lastSeg), Line = "1565C0", LineWidth = 2.75 });
        ch.Series.Add(new XlsxChartSeries { Name = "Highlighted", X = rng("E", 2, lastSeg), Y = rng("F", 2, lastSeg), Line = "F57C00", LineWidth = 5, Marker = "circle", MarkerSize = 9, MarkerFill = "FFB74D", MarkerLine = "E65100" });
        ch.Series.Add(new XlsxChartSeries { Name = "Link", X = rng("K", 2, lastLk), Y = rng("L", 2, lastLk), Line = "8E24AA", LineWidth = 1.5, Dashed = true });
        ch.Series.Add(new XlsxChartSeries { Name = "Grounded link", X = rng("M", 2, lastLk), Y = rng("N", 2, lastLk), Marker = "diamond", MarkerSize = 8, MarkerFill = "8E24AA", MarkerLine = "4A148C" });
        ch.Series.Add(new XlsxChartSeries { Name = "Pinned end", X = rng("H", 2, lastPin), Y = rng("I", 2, lastPin), Marker = "circle", MarkerSize = 9, MarkerFill = "EF5350", MarkerLine = "B71C1C" });
        ch.Series.Add(new XlsxChartSeries { Name = "Fixed support", X = rng("P", 2, lastJ), Y = rng("Q", 2, lastJ), Marker = "square", MarkerSize = 9, MarkerFill = "C62828", MarkerLine = "7F0000" });
        ch.Series.Add(new XlsxChartSeries { Name = "Other support", X = rng("R", 2, lastJ), Y = rng("S", 2, lastJ), Marker = "triangle", MarkerSize = 9, MarkerFill = "43A047", MarkerLine = "1B5E20" });
        ch.Series.Add(new XlsxChartSeries { Name = "Joints", X = rng("T", 2, lastJ), Y = rng("U", 2, lastJ), Labels = rng("V", 2, lastJ), InLegend = false });
        WbPlot.SquareSeries(ch, P);
        view.Charts.Add(ch);
    }
}
