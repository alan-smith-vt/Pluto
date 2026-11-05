using System;
using System.Collections.Generic;
using System.Globalization;

// WbPlot  --  the workbook kit's live 3D view (2026-11-05; from the CAESAR workbook's 3D View, 2026-10). A workbook draws
// its model as an Excel XY chart that follows the sheets as they are edited: a hidden calc sheet works out each
// point's projection, a hidden points sheet lays the chart's series out, and a view sheet holds the controls, the
// figures, a key and the chart. What is generic lives here:
//   Param       a named cell right of the calc table (name, formula, what it is)
//   Angles      pl_az pl_elev pl_cos_a pl_sin_a pl_cos_e pl_sin_e, from the view's Direction / Azimuth / Elevation
//   U V         the projection of (x, y, z) on the view plane; pl_zup (a named cell) makes Z the vertical axis
//   Extents     pl_minu pl_maxu pl_minv pl_maxv pl_cu pl_cv pl_h: the drawing's extent and its square frame
//   Square      the frame (a square around the drawing, so both scales are the same) and the axis triad, on the
//               points sheet (columns AB:AC and AE:AM, rows 2-3), and their chart series
//   Controls    the view sheet's title, subtitle and the View block (rows 5-8, C6:C8)
//   Key         the view sheet's key: a coloured symbol and a label per row
// Every formula is built exactly as the CAESAR workbook built it, so its bytes do not move. C# 5.

public sealed class WbPlot
{
    readonly XlsxBook _b;
    readonly XlsxBookSheet _calc;
    readonly string _calcName;
    readonly int _pc;
    int _pr = 1;

    // the parameters go at column `col` (0-based) of the calc sheet, from its second row
    public WbPlot(XlsxBook b, XlsxBookSheet calc, string calcName, int col) { _b = b; _calc = calc; _calcName = calcName; _pc = col; }

    // a named cell: its name, formula and a note, one row each
    public void Param(string name, string formula, string what)
    {
        _calc.Set(_pr, _pc, name, XlsxBook.Bold); _calc.SetFormula(_pr, _pc + 1, formula, 0); _calc.Set(_pr, _pc + 2, what, XlsxBook.Note);
        _b.Names[name] = _calcName + "!$" + XlsxBook.Column(_pc + 1) + "$" + (_pr + 1);
        _pr++;
    }
    public void Widths() { _calc.Width(_pc, 12); _calc.Width(_pc + 2, 40); }

    // the view angles: vs = the view sheet's reference; names / az / el = the Lists ranges of the views and their angles
    public void Angles(string vs, string names, string az, string el)
    {
        Param("pl_az", "IF(" + vs + "!$C$6=\"Custom\",N(" + vs + "!$C$7),IFERROR(INDEX(" + az + ",MATCH(" + vs + "!$C$6," + names + ",0)),45))", "azimuth, deg");
        Param("pl_elev", "IF(" + vs + "!$C$6=\"Custom\",N(" + vs + "!$C$8),IFERROR(INDEX(" + el + ",MATCH(" + vs + "!$C$6," + names + ",0)),35.264))", "elevation, deg");
        Param("pl_cos_a", "COS(RADIANS(pl_az))", "");
        Param("pl_sin_a", "SIN(RADIANS(pl_az))", "");
        Param("pl_cos_e", "COS(RADIANS(pl_elev))", "");
        Param("pl_sin_e", "SIN(RADIANS(pl_elev))", "");
    }

    // the projection of (x, y, z): u across, v up the view. x y z are formula terms (wrapped in brackets when compound)
    public static string U(string x, string y, string z) { return x + "*pl_cos_a-" + Zz(y, z) + "*pl_sin_a"; }
    public static string V(string x, string y, string z) { return Yy(y, z) + "*pl_cos_e-(" + x + "*pl_sin_a+" + Zz(y, z) + "*pl_cos_a)*pl_sin_e"; }
    static string Zz(string y, string z) { return "IF(pl_zup,-" + y + "," + z + ")"; }
    static string Yy(string y, string z) { return "IF(pl_zup," + z + "," + y + ")"; }

    // the drawing's extent over the u and v ranges (comma-separated), and the square frame's half side
    public void Extents(string us, string vs)
    {
        Param("pl_minu", "MIN(" + us + ")", "the drawing's extent");
        Param("pl_maxu", "MAX(" + us + ")", "");
        Param("pl_minv", "MIN(" + vs + ")", "");
        Param("pl_maxv", "MAX(" + vs + ")", "");
        Param("pl_cu", "(pl_minu+pl_maxu)/2", "its centre");
        Param("pl_cv", "(pl_minv+pl_maxv)/2", "");
        Param("pl_h", "MAX(pl_maxu-pl_minu,pl_maxv-pl_minv,1E-6)/2*1.12", "half the square frame (equal scales)");
    }

    // the points sheet's frame (AB:AC) and axis triad (AE:AM), rows 2-3; their headers are the caller's
    public static void Square(XlsxBookSheet pts)
    {
        pts.SetFormula(1, 27, "-pl_h", 0); pts.SetFormula(1, 28, "-pl_h", 0); pts.SetFormula(2, 27, "pl_h", 0); pts.SetFormula(2, 28, "pl_h", 0);
        // the axis triad, bottom left: X, Y, Z of the model projected like the model
        string[][] axes = { new[] { "1", "0", "0", "X" }, new[] { "0", "1", "0", "Y" }, new[] { "0", "0", "1", "Z" } };
        for (int a = 0; a < 3; a++)
        {
            string x = axes[a][0], y = axes[a][1], z = axes[a][2];
            string pu = "(" + U(x, y, z) + ")", pv = "(" + V(x, y, z) + ")";
            int c0 = 30 + a * 3;
            pts.SetFormula(1, c0, "-0.82*pl_h", 0); pts.SetFormula(1, c0 + 1, "-0.82*pl_h", 0);
            pts.SetFormula(2, c0, "-0.82*pl_h+0.13*pl_h*" + pu, 0); pts.SetFormula(2, c0 + 1, "-0.82*pl_h+0.13*pl_h*" + pv, 0);
            pts.Set(2, c0 + 2, axes[a][3]);
        }
    }

    // the frame's and the triad's series (P: the points sheet's reference with its "!")
    public static void SquareSeries(XlsxChart ch, string P)
    {
        Func<string, int, int, string> rng = (c, r0, r1) => P + "$" + c + "$" + r0 + ":$" + c + "$" + r1;
        ch.Series.Add(new XlsxChartSeries { Name = "Frame", X = rng("AB", 2, 3), Y = rng("AC", 2, 3), InLegend = false });
        string[] axisColor = { "1565C0", "2E7D32", "C62828" };
        for (int a = 0; a < 3; a++)
        {
            string cu = XlsxBook.Column(30 + a * 3), cv = XlsxBook.Column(31 + a * 3), cl = XlsxBook.Column(32 + a * 3);
            ch.Series.Add(new XlsxChartSeries { Name = "XYZ"[a] + " axis", X = rng(cu, 2, 3), Y = rng(cv, 2, 3), Line = axisColor[a], LineWidth = 1.75, Labels = rng(cl, 2, 3), LabelColor = axisColor[a], LabelSize = 9, InLegend = false });
        }
    }

    // the view sheet's head and its View block: Direction (C6, a list), Azimuth and Elevation (C7, C8, for Custom)
    public static void Controls(WbTheme t, XlsxBookSheet view, string subtitle, string viewList, string directionPrompt)
    {
        view.ShowGrid = false;
        view.TabColor = "6A1B9A";
        view.Width(0, 2); view.Width(1, 22); view.Width(2, 26); view.Width(3, 2);
        view.Set(1, 1, "3D view", t.Title); view.Height(1, 30);
        view.Set(2, 1, subtitle, t.Subtitle);
        view.Set(4, 1, "View", t.Section); view.Style(4, 2, t.Section);
        view.Set(5, 1, "Direction", t.Label); view.Set(5, 2, "Iso", t.Value);
        view.Validate("C6", new XlsxValidation { Type = "list", Formula1 = viewList, PromptTitle = "View", Prompt = directionPrompt, ErrorTitle = "A view", Error = "Pick a view from the list." });
        view.Set(6, 1, "Azimuth (deg)", t.Label); view.Set(6, 2, 45, t.Value);
        view.Set(7, 1, "Elevation (deg)", t.Label); view.Set(7, 2, 35.264, t.Value);
        foreach (string a in new[] { "C7", "C8" })
            view.Validate(a, new XlsxValidation { Type = "decimal", Operator = "between", Formula1 = "-360", Formula2 = "360", PromptTitle = "Custom view", Prompt = "Used when the view is Custom. Azimuth turns the model about the vertical axis; elevation tilts it toward you (90 = from above).", ErrorTitle = "Degrees", Error = "An angle in degrees, -360 to 360." });
        view.Conditional("C7:C8", "$C$6<>\"Custom\"", t.Muted);
    }

    // the key, from row `row` (0-based): {symbol, colour, label} per row
    public static void Key(XlsxBook b, WbTheme t, XlsxBookSheet view, int row, string[][] legend)
    {
        for (int i = 0; i < legend.Length; i++)
        {
            view.Set(row + i, 1, legend[i][0], b.Style(new XlsxStyle { Bold = true, Color = legend[i][1], Align = "center" }));
            view.Set(row + i, 2, legend[i][2], t.Body);
        }
    }

    // the views a Direction list offers, and their azimuth and elevation (degrees)
    public static readonly string[] Views = { "Iso", "Plan", "Front", "Side", "Custom" };
    public static readonly double[] ViewAz = { 45, 0, 0, 90, 45 }, ViewEl = { 35.264, 90, 0, 0, 35.264 };

    public static string Num(double v) { return v.ToString("R", CultureInfo.InvariantCulture); }
}
