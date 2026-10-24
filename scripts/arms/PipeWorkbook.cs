using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

// PipeWorkbook  --  a pipe model (PipeModel) <-> an Excel workbook you edit, and read back. C# 5 / PowerShell 5.1:
// Build-Pipe.ps1 -In <model> -Xlsx writes one, -In <workbook>.xlsx reads one; -NewWorkbook starts from a template.
// Usage and the sheets: vault/arms/pipe-workbook.md.
//
// The workbook is the model's master copy. Each sheet is one table of the model, in the job's units (the
// header says which: "DX (in)"):
//   Settings      title, notes, vertical axis, ambient (read-only: units, source, base)
//   Elements      one row per element, in CAESAR's order: Id, From, To, DX DY DZ, Type, Section, Process, bend,
//                 rigid weight, reducer end, expansion joint, name, line; grouped (collapsed) columns override a
//                 section / process value for one element
//   Sections      pipe sizes: OD, wall, insulation, corrosion, densities, material (an element names one)
//   Processes     T1-T9, P1-P9, fluid density (an element names one)
//   Restraints    node, type, CNODE, stiffness (blank = rigid), gap, friction, cosines (blank = the type's), tag
//   SIFs, Joints, Forces, Displacements (blank = free), Coords
//   Uniform, Wind per element: a row names one element or a range of them (E3:E9, or From and To along a chain);
//                 an element no row names has none (CAESAR carries a load forward: the writer ends it there)
//   Nodes, Carried, Help    read-only: rebuilt on every export, never read
//   Lists (hidden), _pluto (veryHidden: the base model as exact JSON and its hash)
// Reading: every row with an Id is that item of the base model, every row without one a new item, every base
// item without a row is deleted; row order is CAESAR's element order. A value equal to the base's (to 1e-9)
// keeps the base's exact value, so a workbook saved without edits gives back the base model exactly -- and its
// .cii byte for byte. Blank Section / Process = the row above's. A header unit may be changed to another length
// unit (in ft mm cm m) on length columns: the values are converted. An error value or a formula Excel never
// calculated is reported by its address. Problems are collected and reported together.

public class PipeWorkbookResult
{
    public PipeModel Model;                   // the workbook's model: its base with the sheets applied
    public PipeModel Base;                    // the model the workbook was written from (null: no base, every row new)
    public string BaseHash = "", ModelHash = "", SourceName = "", Path = "";
    public CaesarCompareResult Changes;       // base -> model (null without a base)
    public readonly List<string> Warnings = new List<string>();

    public string Summary()
    {
        var sb = new StringBuilder();
        sb.AppendLine("PipeWorkbook: " + System.IO.Path.GetFileName(Path) + ": " + Model.Elements.Count + " elements, " + Model.Restraints.Count + " restraints, " +
                      Model.Forces.Count() + " force and " + Model.Displacements.Count() + " displacement node(s)" +
                      (SourceName.Length > 0 ? "; made from " + SourceName : ""));
        if (Base == null) sb.AppendLine("  no base model (the hidden _pluto sheet is missing): every row is new");
        else if (Changes.Differences == 0) sb.AppendLine("  no edits: the model is its base exactly (" + Short(BaseHash) + ")");
        else foreach (string l in Changes.Summary(25).Split('\n').Skip(1)) if (l.Trim().Length > 0) sb.AppendLine("  " + l.TrimEnd('\r'));
        if (Warnings.Count > 0) sb.AppendLine("  " + Warnings.Count + " warning(s)");
        return sb.ToString();
    }

    public string ToJson()
    {
        return "{\"path\":" + PipeWorkbook.Q(Path) + ",\"source\":" + PipeWorkbook.Q(SourceName) + ",\"base\":" + PipeWorkbook.Q(BaseHash) + ",\"model\":" + PipeWorkbook.Q(ModelHash) +
               ",\"elements\":" + Model.Elements.Count + ",\"edits\":" + (Changes == null ? "null" : Changes.Differences.ToString(CultureInfo.InvariantCulture)) +
               ",\"changes\":" + (Changes == null ? "null" : Changes.ToJson()) + ",\"warnings\":[" + string.Join(",", Warnings.Select(PipeWorkbook.Q).ToArray()) + "]}";
    }

    static string Short(string h) { return h.Length > 12 ? h.Substring(0, 12) : h; }
}

public static class PipeWorkbook
{
    public const string Format = "pluto-pipe-workbook";
    public const int Version = 1;
    const int Chunk = 30000;                  // characters of base JSON per cell (Excel holds 32767)

    // ======================================================================== units

    // the job's unit labels (CAESAR's UNITS labels, the 24 lines after the factors), trimmed for headers
    sealed class Units
    {
        public string[] L = new string[24];
        public PipeModel M;
        public bool English;
        public string Len { get { return L[2]; } }
        public string Force { get { return L[3]; } }
        public string MomIn { get { return L[5]; } }
        public string Temp { get { return L[8]; } }
        public string Press { get { return L[10]; } }
        public string E { get { return L[11]; } }
        public string PipeDens { get { return L[12]; } }
        public string InsDens { get { return L[13]; } }
        public string FluidDens { get { return L[14]; } }
        public string TransK { get { return L[15]; } }
        public string RotK { get { return L[16]; } }
        public string Uni { get { return L[17]; } }
        public string Dia { get { return L[22]; } }
        public string Thk { get { return L[23]; } }
    }

    static readonly string[] EnglishLabels =
    {
        "ENGLISH", "ON", "in", "lb", "lbm", "in.lb", "ft.lb", "lb/sq.in", "F", "", "lb/sq.in", "lb/sq.in", "lb/cu.in", "lb/cu.in", "lb/cu.in",
        "lb/in", "in.lb/deg", "lb/in", "g's", "lb/sq.ft", "ft", "ft", "in", "in"
    };

    static Units UnitsOf(PipeModel m)
    {
        var u = new Units { M = m };
        Array.Copy(EnglishLabels, u.L, 24);
        u.English = new[] { 0, 1, 3, 8, 13, 14, 15, 20, 21 }.All(i => Math.Abs(m.Factor(i) - PipeModelCodes.EnglishFactors[i]) < 1e-6);
        List<string> lines;
        if (m.Source != null && m.Source.Sections.TryGetValue("UNITS", out lines) && lines.Count >= 28)
            for (int i = 0; i < 24; i++) u.L[i] = Label(lines[4 + i]);
        return u;
    }

    static string Label(string raw)
    {
        string s = (raw ?? "").Trim().Replace("./", "/");
        while (s.EndsWith(".", StringComparison.Ordinal)) s = s.Substring(0, s.Length - 1);
        return s;
    }

    // inches per length unit a header may give
    static readonly Dictionary<string, double> LengthUnits = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
    {
        { "in", 1.0 }, { "inch", 1.0 }, { "inches", 1.0 }, { "ft", 12.0 }, { "feet", 12.0 }, { "mm", 1 / 25.4 }, { "cm", 1 / 2.54 }, { "m", 1000 / 25.4 }
    };

    // ======================================================================== write

    public static void Write(PipeModel m, string path, string sourceName) { Write(m, path, sourceName, null); }

    // run: the Start sheet's links to the launchers Build-Pipe.ps1 writes beside the workbook (PipeWorkbookRun); null = none
    public static void Write(PipeModel m, string path, string sourceName, PipeWorkbookRun run) { File.WriteAllBytes(path, ToBytes(m, sourceName, run)); }

    sealed class Sec
    {
        public string Name;
        public double Od, Wall, Insul, Corr, InsDens, E, Poisson, PipeDens;
        public int Material;
        public string Key() { return string.Join("|", new[] { Od, Wall, Insul, Corr, InsDens, E, Poisson, PipeDens, Material }.Select(R).ToArray()); }
    }

    sealed class Proc
    {
        public string Name;
        public double[] T = new double[9], P = new double[9];
        public double Fluid;
        public string Key() { return string.Join("|", T.Concat(P).Concat(new[] { Fluid }).Select(R).ToArray()); }
    }

    static Sec SecOf(PipeModelElement e) { return new Sec { Od = e.Od, Wall = e.Wall, Insul = e.Insulation, Corr = e.Corrosion, InsDens = e.InsulationDensity, E = e.Emod, Poisson = e.Poisson, PipeDens = e.PipeDensity, Material = e.Material }; }
    static Proc ProcOf(PipeModelElement e) { var p = new Proc { Fluid = e.FluidDensity }; Array.Copy(e.T, p.T, 9); Array.Copy(e.P, p.P, 9); return p; }

    static string Unique(string n, HashSet<string> used)
    {
        string s = n;
        for (int k = 2; used.Contains(s); k++) s = n + "-" + k.ToString(CultureInfo.InvariantCulture);
        used.Add(s);
        return s;
    }

    static string SecName(Sec s, Units u, int commonMaterial, HashSet<string> used)
    {
        string n = null;
        if (u.English)
        {
            double nps = PipeBuilder.NominalOf(s.Od);
            if (!double.IsNaN(nps))
            {
                foreach (string sch in new[] { "STD", "XS", "40", "80", "160" })
                {
                    double od, w;
                    if (PipeBuilder.PipeSize(nps, sch, out od, out w) && Math.Abs(w - s.Wall) < 5e-4) { n = F(nps) + "in " + sch; break; }
                }
                if (n == null) n = F(nps) + "in w" + F(s.Wall);
            }
        }
        if (n == null) n = F(s.Od) + "x" + F(s.Wall);
        if (s.Insul > 0) n += " ins" + F(s.Insul);
        if (s.Material != commonMaterial) n += " m" + s.Material;
        return Unique(n, used);
    }

    static string ProcName(Proc p, HashSet<string> used)
    {
        string n = p.T[0] == 0 && p.P[0] == 0 ? "cold" : "T" + F(p.T[0]) + " P" + F(p.P[0]);
        return Unique(n, used);
    }

    static readonly string[] ElementTypes = { "Pipe", "Rigid", "Valve", "Flange", "Flange pair", "Flanged valve", "Reducer", "Exp joint" };
    static readonly string[] BendTypes = { "Welded", "Single flange", "Double flange" };
    static readonly string[] WindTypes = { "wind", "wave", "off" };

    static string TypeOf(PipeModelElement e)
    {
        if (e.Rigid != null)
        {
            int t = e.Rigid.Type;
            return t == 1 ? "Valve" : t == 2 ? "Flange" : t == 3 ? "Flange pair" : t == 4 ? "Flanged valve" : t == 0 ? "Rigid" : "Rigid " + t.ToString(CultureInfo.InvariantCulture);
        }
        if (e.ExpJoint != null) return "Exp joint";
        if (e.Reducer != null) return "Reducer";
        return "Pipe";
    }

    // the Elements sheet's columns
    const int cId = 0, cFrom = 1, cTo = 2, cDx = 3, cDy = 4, cDz = 5, cType = 6, cSec = 7, cProc = 8, cBendR = 9, cBendN = 10, cBendT = 11, cWeight = 12,
              cOd2 = 13, cWall2 = 14, cEjAx = 15, cEjTr = 16, cEjBe = 17, cEjTo = 18, cEjId = 19, cName = 20, cLine = 21,
              cOd = 22, cWall = 23, cIns = 24, cCorr = 25, cInsD = 26, cMat = 27, cE = 28, cPois = 29, cPipeD = 30, cT1 = 31, cP1 = 40, cFluid = 49,
              cMiter = 50, cFit = 51, cK = 52;

    static string[] ElementHeaders(Units u)
    {
        var h = new string[53];
        h[cId] = "Id"; h[cFrom] = "From"; h[cTo] = "To"; h[cDx] = "DX (" + u.Len + ")"; h[cDy] = "DY (" + u.Len + ")"; h[cDz] = "DZ (" + u.Len + ")";
        h[cType] = "Type"; h[cSec] = "Section"; h[cProc] = "Process"; h[cBendR] = "Bend radius (" + u.Len + ")"; h[cBendN] = "Bend nodes"; h[cBendT] = "Bend type";
        h[cWeight] = "Weight (" + u.Force + ")"; h[cOd2] = "OD2 (" + u.Dia + ")"; h[cWall2] = "Wall2 (" + u.Thk + ")";
        h[cEjAx] = "EJ axial (" + u.TransK + ")"; h[cEjTr] = "EJ transverse (" + u.TransK + ")"; h[cEjBe] = "EJ bending (" + u.RotK + ")";
        h[cEjTo] = "EJ torsion (" + u.RotK + ")"; h[cEjId] = "EJ eff ID (" + u.Dia + ")"; h[cName] = "Name"; h[cLine] = "Line";
        h[cOd] = "OD (" + u.Dia + ")"; h[cWall] = "Wall (" + u.Thk + ")"; h[cIns] = "Insulation (" + u.Thk + ")"; h[cCorr] = "Corrosion (" + u.Thk + ")";
        h[cInsD] = "Insul density (" + u.InsDens + ")"; h[cMat] = "Material"; h[cE] = "E (" + u.E + ")"; h[cPois] = "Poisson"; h[cPipeD] = "Pipe density (" + u.PipeDens + ")";
        for (int k = 0; k < 9; k++) { h[cT1 + k] = "T" + (k + 1) + " (" + u.Temp + ")"; h[cP1 + k] = "P" + (k + 1) + " (" + u.Press + ")"; }
        h[cFluid] = "Fluid density (" + u.FluidDens + ")"; h[cMiter] = "Bend miter points"; h[cFit] = "Bend fitting thk (" + u.Thk + ")"; h[cK] = "Bend K factor";
        return h;
    }

    static readonly string[] SectionCols = { "Name", "OD", "Wall", "Insulation", "Corrosion", "Insul density", "Material", "E", "Poisson", "Pipe density" };
    static string[] SectionHeaders(Units u)
    {
        return new[] { "Name", "OD (" + u.Dia + ")", "Wall (" + u.Thk + ")", "Insulation (" + u.Thk + ")", "Corrosion (" + u.Thk + ")", "Insul density (" + u.InsDens + ")",
                       "Material", "E (" + u.E + ")", "Poisson", "Pipe density (" + u.PipeDens + ")" };
    }

    static string[] ProcessHeaders(Units u)
    {
        var h = new List<string> { "Name" };
        for (int k = 0; k < 9; k++) h.Add("T" + (k + 1) + " (" + u.Temp + ")");
        for (int k = 0; k < 9; k++) h.Add("P" + (k + 1) + " (" + u.Press + ")");
        h.Add("Fluid density (" + u.FluidDens + ")");
        return h.ToArray();
    }

    static string[] RestraintHeaders(Units u)
    {
        return new[] { "Id", "Node", "Type", "CNode", "Stiffness (" + u.TransK + " or " + u.RotK + "; blank = rigid)", "Gap (" + u.Len + " or deg)", "Mu", "Cos X", "Cos Y", "Cos Z", "Tag" };
    }

    public static byte[] ToBytes(PipeModel m, string sourceName) { return ToBytes(m, sourceName, null); }

    public static byte[] ToBytes(PipeModel m, string sourceName, PipeWorkbookRun run)
    {
        string json = PipeModelJson.Write(m);
        string hash = Sha(json);
        var u = UnitsOf(m);
        var b = new XlsxBook();
        var t = PipeWorkbookGui.MakeTheme(b);
        int n = m.Elements.Count;
        int lim = Math.Max(2000, n + 1500);                   // rows the hints, dropdowns and checks cover
        PipeWorkbookGui.RefRows = lim;
        var start = b.Add("Start");                           // filled last: it counts and checks the others

        // ---- Settings
        var st = b.Add("Settings");
        st.FreezeRows = 1; st.TabColor = "37474F";
        st.Width(0, 26); st.Width(1, 44); st.Width(2, 70);
        st.Set(0, 0, "Setting", t.Head["key"]); st.Set(0, 1, "Value", t.Head["prop"]); st.Set(0, 2, "Note", t.Head["text"]); st.Height(0, 24);
        int r = 1;
        Row(st, t, r++, "Title", m.Title, "CAESAR's job title (the VERSION PROJECT line)", "text");
        Row(st, t, r++, "Notes", m.Notes, "the VERSION NOTES line", "text");
        Row(st, t, r++, "Vertical axis", m.ZUp ? "Z" : "Y", "Y (CAESAR's default) or Z", "axis");
        st.Set(r, 0, "Ambient (" + u.Temp + ")", t.Label); st.Set(r, 1, m.Ambient, t.Value); st.Set(r, 2, "installation temperature: thermal strain from T - ambient", XlsxBook.Note);
        st.Validate("B5", new XlsxValidation { Type = "decimal", Operator = "between", Formula1 = "-1E+307", Formula2 = "1E+307", PromptTitle = "Ambient", Prompt = "The installation temperature (" + u.Temp + ").", ErrorTitle = "A number", Error = "The ambient temperature is a number." });
        st.Conditional("B5", "AND(B5<>\"\",NOT(ISNUMBER(B5)))", t.Red); r++;
        RowRo(st, r++, "Units", (u.L[0] ?? "").Trim() + ": length " + u.Len + ", force " + u.Force + ", temperature " + u.Temp + ", pressure " + u.Press, "the job's (read-only)");
        RowRo(st, r++, "Source", sourceName ?? "", "what this workbook was made from (read-only)");
        RowRo(st, r++, "Base", hash.Substring(0, 16), "the base model's fingerprint (read-only)");
        RowRo(st, r++, "Workbook", Format + " " + Version, "written by Pluto Build-Pipe.ps1 (read-only)");

        // ---- sections and processes, from the elements
        var secs = new List<Sec>(); var secByKey = new Dictionary<string, Sec>();
        var procs = new List<Proc>(); var procByKey = new Dictionary<string, Proc>();
        int common = m.Elements.Count == 0 ? 0 : m.Elements.GroupBy(e => e.Material).OrderByDescending(g => g.Count()).First().Key;
        var usedS = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var usedP = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var eSec = new Dictionary<PipeModelElement, Sec>(); var eProc = new Dictionary<PipeModelElement, Proc>();
        foreach (var e in m.Elements)
        {
            var s = SecOf(e); Sec s0;
            if (!secByKey.TryGetValue(s.Key(), out s0)) { s.Name = SecName(s, u, common, usedS); secByKey[s.Key()] = s; secs.Add(s); s0 = s; }
            eSec[e] = s0;
            var p = ProcOf(e); Proc p0;
            if (!procByKey.TryGetValue(p.Key(), out p0)) { p.Name = ProcName(p, usedP); procByKey[p.Key()] = p; procs.Add(p); p0 = p; }
            eProc[e] = p0;
        }
        int limS = Math.Max(500, secs.Count + 300), limP = Math.Max(500, procs.Count + 300);

        // ---- Elements
        var el = b.Add("Elements");
        el.FreezeRows = 1; el.FreezeCols = 3; el.TabColor = "1565C0";
        var view = b.Add(PipeWorkbookGui.View);
        var eh = ElementHeaders(u);
        for (int c = 0; c < eh.Length; c++) el.Width(c, c == cId || c == cFrom || c == cTo ? 8 : c == cBendN ? 15 : c == cSec || c == cProc ? 17 : c == cName || c == cLine ? 14 : c == cType ? 13 : 11);
        el.Group(cOd, cK);
        r = 1;
        foreach (var e in m.Elements)
        {
            el.Set(r, cId, "E" + e.Uid); el.Set(r, cFrom, e.From); el.Set(r, cTo, e.To);
            el.Set(r, cDx, e.Dx); el.Set(r, cDy, e.Dy); el.Set(r, cDz, e.Dz);
            el.Set(r, cType, TypeOf(e)); el.Set(r, cSec, eSec[e].Name); el.Set(r, cProc, eProc[e].Name);
            if (e.Bend != null)
            {
                string rn = PipeModelBend.DisplayName(e.Bend.Radius);       // a radius CAESAR wrote by name shows its name
                if (rn != null) el.Set(r, cBendR, rn); else el.Set(r, cBendR, e.Bend.Radius);
                el.Set(r, cBendN, BendNodesText(e.Bend));
                if (e.Bend.Type != 0) el.Set(r, cBendT, e.Bend.Type >= 0 && e.Bend.Type < 3 ? BendTypes[e.Bend.Type] : e.Bend.Type.ToString(CultureInfo.InvariantCulture));
                if (e.Bend.MiterPoints != 0) el.Set(r, cMiter, e.Bend.MiterPoints);
                if (e.Bend.FittingThickness != 0 && !Same(e.Bend.FittingThickness, e.Wall)) el.Set(r, cFit, e.Bend.FittingThickness);
                if (e.Bend.KFactor != 0) el.Set(r, cK, e.Bend.KFactor);
            }
            if (e.Rigid != null) el.Set(r, cWeight, e.Rigid.Weight);
            if (e.Reducer != null) { el.Set(r, cOd2, e.Reducer.Od2); el.Set(r, cWall2, e.Reducer.Wall2); }
            if (e.ExpJoint != null)
            {
                var x = e.ExpJoint;
                el.Set(r, cEjAx, x.AxialStiffness); el.Set(r, cEjTr, x.TransverseStiffness); el.Set(r, cEjBe, x.BendingStiffness); el.Set(r, cEjTo, x.TorsionStiffness); el.Set(r, cEjId, x.EffectiveId);
            }
            el.Set(r, cName, e.Name); el.Set(r, cLine, e.LineNumber);
            r++;
        }
        string secList = "Sections!$A$2:$A$" + limS, procList = "Processes!$A$2:$A$" + limP;
        PipeWorkbookGui.Table(el, t, eh, PipeWorkbookGui.ElementCols(secList, procList), n, lim);
        {
            string L = lim.ToString(CultureInfo.InvariantCulture);
            // a row with anything on it needs From and To; an element from a node to itself; nothing to stand on
            PipeWorkbookGui.Rule(el, "B2:C" + L, "AND(B2=\"\",COUNTA($A2,$D2:$J2)>0)", t.Red);
            PipeWorkbookGui.Rule(el, "B2:C" + L, "AND(ISNUMBER($B2),$B2=$C2)", t.Red);
            PipeWorkbookGui.Rule(el, "H2:H" + L, "AND(H2<>\"\",COUNTIF(" + secList + ",H2)=0)", t.Red);
            PipeWorkbookGui.Rule(el, "I2:I" + L, "AND(I2<>\"\",COUNTIF(" + procList + ",I2)=0)", t.Red);
            PipeWorkbookGui.Rule(el, "H2:I2", "AND(H2=\"\",$B2<>\"\")", t.Red);
            PipeWorkbookGui.Rule(el, "N2:O" + L, "AND($G2=\"Reducer\",N2=\"\")", t.Red);
            PipeWorkbookGui.Rule(el, "K2:K" + L, "AND(K2<>\"\",ISERROR(FIND(\":\",K2)))", t.Red);
            PipeWorkbookGui.Rule(el, "K2:K" + L, "AND(K2<>\"\"," + PipeWorkbookGui.BendNodeTwice("K2", "$K$2:$K$" + L, "$B$2:$B$" + L, "$C$2:$C$" + L) + ")", t.Red);
            PipeWorkbookGui.Rule(el, "J2:J" + L, PipeWorkbookGui.NoRadiusRule("J2"), t.Amber);
            PipeWorkbookGui.Rule(el, "D2:F" + L, "AND(ISNUMBER($B2),N($D2)=0,N($E2)=0,N($F2)=0,ISNUMBER($D2)+ISNUMBER($E2)+ISNUMBER($F2)+($D2=\"\")+($E2=\"\")+($F2=\"\")>=3)", t.Amber);
        }

        // ---- Sections, Processes
        var se = b.Add("Sections");
        se.FreezeRows = 1; se.TabColor = "00796B";
        var sh = SectionHeaders(u);
        for (int c = 0; c < sh.Length; c++) se.Width(c, c == 0 ? 22 : 13);
        r = 1;
        foreach (var s in secs)
        {
            se.Set(r, 0, s.Name); se.Set(r, 1, s.Od); se.Set(r, 2, s.Wall); se.Set(r, 3, s.Insul); se.Set(r, 4, s.Corr); se.Set(r, 5, s.InsDens); se.Set(r, 6, s.Material);
            if (s.E != 0) se.Set(r, 7, s.E);
            if (s.Poisson != 0) se.Set(r, 8, s.Poisson);
            if (s.PipeDens != 0) se.Set(r, 9, s.PipeDens);
            r++;
        }
        PipeWorkbookGui.Table(se, t, sh, PipeWorkbookGui.SectionCols(), secs.Count, limS);
        PipeWorkbookGui.Rule(se, "A2:A" + limS, "AND(A2<>\"\",COUNTIF($A$2:$A$" + limS + ",A2)>1)", t.Red);
        PipeWorkbookGui.Rule(se, "A2:A" + limS, "AND(A2=\"\",COUNTA($B2:$J2)>0)", t.Red);
        PipeWorkbookGui.Rule(se, "B2:C" + limS, "AND(B2=\"\",$A2<>\"\")", t.Red);
        var pr = b.Add("Processes");
        pr.FreezeRows = 1; pr.FreezeCols = 1; pr.TabColor = "00796B";
        var ph = ProcessHeaders(u);
        for (int c = 0; c < ph.Length; c++) pr.Width(c, c == 0 ? 18 : 10);
        pr.Group(4, 9); pr.Group(13, 18);
        r = 1;
        foreach (var p in procs)
        {
            pr.Set(r, 0, p.Name);
            for (int k = 0; k < 9; k++) { if (p.T[k] != 0 || k == 0) pr.Set(r, 1 + k, p.T[k]); if (p.P[k] != 0 || k == 0) pr.Set(r, 10 + k, p.P[k]); }
            pr.Set(r, 19, p.Fluid);
            r++;
        }
        PipeWorkbookGui.Table(pr, t, ph, PipeWorkbookGui.ProcessCols(), procs.Count, limP);
        PipeWorkbookGui.Rule(pr, "A2:A" + limP, "AND(A2<>\"\",COUNTIF($A$2:$A$" + limP + ",A2)>1)", t.Red);
        PipeWorkbookGui.Rule(pr, "A2:A" + limP, "AND(A2=\"\",COUNTA($B2:$T2)>0)", t.Red);

        // nodes the model has (amber when a row names another): From / To and the bend nodes
        string L2 = lim.ToString(CultureInfo.InvariantCulture);
        string nFrom = "Elements!$B$2:$B$" + L2, nTo = "Elements!$C$2:$C$" + L2, nBend = "Elements!$K$2:$K$" + L2;

        // ---- Restraints
        var rs = b.Add("Restraints");
        rs.FreezeRows = 1; rs.TabColor = "B45309";
        var rh = RestraintHeaders(u);
        for (int c = 0; c < rh.Length; c++) rs.Width(c, c == 4 ? 20 : c == 5 ? 13 : c == 10 ? 16 : 9);
        r = 1;
        for (int i = 0; i < m.Restraints.Count; i++)
        {
            var x = m.Restraints[i];
            rs.Set(r, 0, "R" + (i + 1)); rs.Set(r, 1, x.Node); rs.Set(r, 2, x.Type); if (x.CNode != 0) rs.Set(r, 3, x.CNode);
            if (!IsRigid(m, x)) rs.Set(r, 4, x.Stiffness);
            if (x.Gap != 0) rs.Set(r, 5, x.Gap);
            if (x.Mu != 0) rs.Set(r, 6, x.Mu);
            if (x.Cosines != null && !SameCos(x.Cosines, PipeModelCodes.DefaultCosines(x.Type))) { rs.Set(r, 7, x.Cosines[0]); rs.Set(r, 8, x.Cosines[1]); rs.Set(r, 9, x.Cosines[2]); }
            rs.Set(r, 10, x.Tag);
            r++;
        }
        var types = PipeModelCodes.BuildTypes.Concat(m.Restraints.Select(x => x.Type)).Distinct().ToList();
        int limR = Math.Max(1000, m.Restraints.Count + 500);
        string typeList = "Lists!$B$2:$B$" + (1 + types.Count);
        PipeWorkbookGui.Table(rs, t, rh, PipeWorkbookGui.RestraintCols(typeList), m.Restraints.Count, limR);
        PipeWorkbookGui.Rule(rs, "B2:B" + limR, "AND(B2=\"\",COUNTA($A2,$C2:$J2)>0)", t.Red);
        PipeWorkbookGui.Rule(rs, "B2:B" + limR, PipeWorkbookGui.NodeMissing("B2", nFrom, nTo, nBend), t.Amber);
        PipeWorkbookGui.Rule(rs, "D2:D" + limR, PipeWorkbookGui.NodeMissing("D2", nFrom, nTo, nBend), t.Amber);

        // ---- SIFs
        var sf = b.Add("SIFs");
        sf.FreezeRows = 1; sf.TabColor = "B45309";
        string[] sfh = { "Id", "Node", "Type (3 = welding tee, 0 = user SIFs)", "SIF in", "SIF out", "Surface node" };
        sf.Width(0, 8); sf.Width(1, 9); sf.Width(2, 22); sf.Width(3, 10); sf.Width(4, 10); sf.Width(5, 12);
        r = 1;
        for (int i = 0; i < m.Sifs.Count; i++)
        {
            var x = m.Sifs[i];
            sf.Set(r, 0, "S" + (i + 1)); sf.Set(r, 1, x.Node); sf.Set(r, 2, x.Type);
            if (x.SifIn != 0) sf.Set(r, 3, x.SifIn);
            if (x.SifOut != 0) sf.Set(r, 4, x.SifOut);
            if (x.SurfaceNode != 0) sf.Set(r, 5, x.SurfaceNode);
            r++;
        }
        var sifTypes = new[] { 0, 3 }.Concat(m.Sifs.Select(x => x.Type)).Distinct().OrderBy(v => v).ToList();
        int limF = Math.Max(1000, m.Sifs.Count + 500);
        PipeWorkbookGui.Table(sf, t, sfh, PipeWorkbookGui.SifCols("Lists!$J$2:$J$" + (1 + sifTypes.Count)), m.Sifs.Count, limF);
        PipeWorkbookGui.Rule(sf, "B2:B" + limF, PipeWorkbookGui.NodeMissing("B2", nFrom, nTo, nBend), t.Amber);

        // ---- Forces, Displacements
        var fo = b.Add("Forces");
        fo.FreezeRows = 1; fo.TabColor = "9A3412";
        string[] foh = { "Id", "Node", "Vector", "FX (" + u.Force + ")", "FY (" + u.Force + ")", "FZ (" + u.Force + ")", "MX (" + u.MomIn + ")", "MY (" + u.MomIn + ")", "MZ (" + u.MomIn + ")" };
        int nf = LoadRows(fo, m.Forces.ToList(), "F", false);
        var di = b.Add("Displacements");
        di.FreezeRows = 1; di.TabColor = "9A3412";
        string[] dih = { "Id", "Node", "Vector", "DX (" + u.Len + ")", "DY (" + u.Len + ")", "DZ (" + u.Len + ")", "RX (deg)", "RY (deg)", "RZ (deg)" };
        int nd = LoadRows(di, m.Displacements.ToList(), "D", true);
        foreach (var q in new[] { new { S = fo, H = foh, N = nf, D = false }, new { S = di, H = dih, N = nd, D = true } })
        {
            for (int c = 0; c < 9; c++) q.S.Width(c, c < 3 ? 9 : 12);
            int limL = Math.Max(1000, q.N + 500);
            PipeWorkbookGui.Table(q.S, t, q.H, PipeWorkbookGui.NodeLoadCols(q.D), q.N, limL);
            PipeWorkbookGui.Rule(q.S, "B2:C" + limL, "AND(B2=\"\",COUNTA($A2,$D2:$I2)>0)", t.Red);
            PipeWorkbookGui.Rule(q.S, "B2:B" + limL, PipeWorkbookGui.NodeMissing("B2", nFrom, nTo, nBend), t.Amber);
            PipeWorkbookGui.Rule(q.S, "C2:C" + limL, "AND(ISNUMBER($B2),ISNUMBER($C2),COUNTIFS($B$2:$B$" + limL + ",$B2,$C$2:$C$" + limL + ",$C2)>1)", t.Red);
        }

        // ---- Uniform, Wind (per element; chained elements with the same values share a row: E3:E9)
        var un = b.Add("Uniform");
        un.FreezeRows = 1; un.TabColor = "9A3412";
        string[] unh = { "Element", "From", "To", "Vector", "UX (" + u.Uni + " or g)", "UY (" + u.Uni + " or g)", "UZ (" + u.Uni + " or g)", "In g's" };
        r = 1;
        foreach (var g in SameRuns(m, q => q.Uniform != null, (p, q) => p.Uniform.SameAs(q.Uniform)))
        {
            var e = g[0];
            var used = Enumerable.Range(0, 9).Where(v => e.Uniform.Used(v)).ToList();
            if (used.Count == 0) used.Add(0);
            foreach (int v in used)
            {
                un.Set(r, 0, RangeId(g)); un.Set(r, 1, e.From, XlsxBook.ReadOnly); un.Set(r, 2, g[g.Count - 1].To, XlsxBook.ReadOnly); un.Set(r, 3, v + 1);
                for (int k = 0; k < 3; k++) un.Set(r, 4 + k, e.Uniform.Vectors[v][k]);
                un.Set(r, 7, e.Uniform.G[v] ? "yes" : "no");
                r++;
            }
        }
        int nu = r - 1, limU = Math.Max(1000, nu + 500);
        un.Width(0, 12); un.Width(1, 8); un.Width(2, 8); un.Width(3, 8); for (int c = 4; c < 7; c++) un.Width(c, 16); un.Width(7, 9);
        PipeWorkbookGui.Table(un, t, unh, PipeWorkbookGui.UniformCols(), nu, limU);
        PipeWorkbookGui.Rule(un, "B2:C" + limU, "AND($A2=\"\",B2=\"\",COUNTA($D2:$H2)>0)", t.Red);
        PipeWorkbookGui.Rule(un, "D2:D" + limU, "AND(D2=\"\",COUNTA($A2:$C2,$E2:$H2)>0)", t.Red);
        var wi = b.Add("Wind");
        wi.FreezeRows = 1; wi.TabColor = "9A3412";
        string[] wih = { "Element", "From", "To", "Type", "Shape factor (wave: drag)", "Added mass", "Lift", "Marine growth", "Growth density" };
        r = 1;
        foreach (var g in SameRuns(m, q => q.Wind != null, (p, q) => p.Wind.SameAs(q.Wind)))
        {
            var x = g[0].Wind;
            wi.Set(r, 0, RangeId(g)); wi.Set(r, 1, g[0].From, XlsxBook.ReadOnly); wi.Set(r, 2, g[g.Count - 1].To, XlsxBook.ReadOnly);
            wi.Set(r, 3, x.Type >= 0 && x.Type < 3 ? WindTypes[x.Type] : x.Type.ToString(CultureInfo.InvariantCulture)); wi.Set(r, 4, x.Shape);
            if (x.Type == 1 || x.AddedMass != 0 || x.Lift != 0 || x.MarineGrowth != 0 || x.MarineGrowthDensity != 0)
            { wi.Set(r, 5, x.AddedMass); wi.Set(r, 6, x.Lift); wi.Set(r, 7, x.MarineGrowth); wi.Set(r, 8, x.MarineGrowthDensity); }
            r++;
        }
        int nw = r - 1, limW = Math.Max(1000, nw + 500);
        wi.Width(0, 12); wi.Width(1, 8); wi.Width(2, 8); wi.Width(3, 9); wi.Width(4, 16); for (int c = 5; c < 9; c++) wi.Width(c, 12);
        PipeWorkbookGui.Table(wi, t, wih, PipeWorkbookGui.WindCols(), nw, limW);
        PipeWorkbookGui.Rule(wi, "B2:C" + limW, "AND($A2=\"\",B2=\"\",COUNTA($D2:$I2)>0)", t.Red);

        // ---- Joints
        var jo = b.Add("Joints");
        jo.FreezeRows = 1; jo.TabColor = "B45309";
        string[] joh = { "Id", "Node", "CNode", "Kind", "Frame", "DOFs", "SIF", "Tag" };
        jo.Width(0, 8); jo.Width(1, 8); jo.Width(2, 8); jo.Width(3, 12); jo.Width(4, 9); jo.Width(5, 50); jo.Width(6, 8); jo.Width(7, 14);
        r = 1;
        foreach (var j in m.Joints)
        {
            jo.Set(r, 0, "J" + j.Uid); jo.Set(r, 1, j.Node); jo.Set(r, 2, j.CNode); jo.Set(r, 3, j.Kind); jo.Set(r, 4, j.Frame); jo.Set(r, 5, DofsText(j));
            if (j.Sif != 0) jo.Set(r, 6, j.Sif);
            jo.Set(r, 7, j.Tag);
            r++;
        }
        int limJ = Math.Max(500, m.Joints.Count + 300);
        PipeWorkbookGui.Table(jo, t, joh, PipeWorkbookGui.JointCols(), m.Joints.Count, limJ);
        PipeWorkbookGui.Rule(jo, "B2:C" + limJ, PipeWorkbookGui.NodeMissing("B2", nFrom, nTo, nBend), t.Amber);

        // ---- Coords
        var co = b.Add("Coords");
        co.FreezeRows = 1; co.TabColor = "1565C0";
        string[] coh = { "Node", "X (" + u.Len + ")", "Y (" + u.Len + ")", "Z (" + u.Len + ")" };
        r = 1;
        foreach (var c in m.Coords) { co.Set(r, 0, c.Node); co.Set(r, 1, c.X); co.Set(r, 2, c.Y); co.Set(r, 3, c.Z); r++; }
        co.Width(0, 9); for (int c = 1; c < 4; c++) co.Width(c, 14);
        int limC = Math.Max(500, m.Coords.Count + 300);
        PipeWorkbookGui.Table(co, t, coh, PipeWorkbookGui.CoordCols(), m.Coords.Count, limC);

        // ---- read-only: Nodes, Carried
        var nodes = b.Add("Nodes");
        NodesSheet(nodes, m, u);
        var carried = b.Add("Carried");
        CarriedSheet(carried, m);
        foreach (var ro in new[] { nodes, carried }) { for (int c = 0; c < 7; c++) ro.Style(0, c, t.Head["ro"]); ro.Height(0, 24); }

        // ---- Lists (hidden), the 3D view's sheets (hidden), _pluto (veryHidden)
        var li = b.Add("Lists");
        li.State = "hidden";
        Head(li, "Element types", "Restraint types", "Bend types", "Wind types", "Yes / no", "Vertical axis", "Joint frame", "Views", "Vectors", "SIF types", "Joint kinds",
             "NPS OD (in)", "NPS", "Coded radius (x NPS)", "View azimuth", "View elevation");
        for (int k = 0; k < ElementTypes.Length; k++) li.Set(1 + k, 0, ElementTypes[k]);
        for (int k = 0; k < types.Count; k++) li.Set(1 + k, 1, types[k]);
        for (int k = 0; k < BendTypes.Length; k++) li.Set(1 + k, 2, BendTypes[k]);
        for (int k = 0; k < WindTypes.Length; k++) li.Set(1 + k, 3, WindTypes[k]);
        li.Set(1, 4, "yes"); li.Set(2, 4, "no"); li.Set(1, 5, "Y"); li.Set(2, 5, "Z"); li.Set(1, 6, "pipe"); li.Set(2, 6, "global");
        string[] views = { "Iso", "Plan", "Front", "Side", "Custom" };
        double[] az = { 45, 0, 0, 90, 45 }, elv = { 35.264, 90, 0, 0, 35.264 };
        for (int k = 0; k < views.Length; k++) { li.Set(1 + k, 7, views[k]); li.Set(1 + k, 14, az[k]); li.Set(1 + k, 15, elv[k]); }
        for (int k = 1; k <= 9; k++) li.Set(k, 8, k);
        for (int k = 0; k < sifTypes.Count; k++) li.Set(1 + k, 9, sifTypes[k]);
        string[] kinds = { "coupling", "hinge", "slip", "ball", "link", "custom" };
        for (int k = 0; k < kinds.Length; k++) li.Set(1 + k, 10, kinds[k]);
        var npsOd = new SortedDictionary<double, double>();
        foreach (double nps in new[] { 0.5, 0.75, 1, 1.25, 1.5, 2, 2.5, 3, 3.5, 4, 5, 6, 8, 10, 12 })
        {
            double od, w;
            if (PipeBuilder.PipeSize(nps, null, out od, out w)) npsOd[od] = nps;
            else if (nps == 1.25) npsOd[1.660] = nps;
            else if (nps == 3.5) npsOd[4.000] = nps;
        }
        int ko = 1;
        foreach (var kv in npsOd) { li.Set(ko, 11, kv.Key); li.Set(ko, 12, kv.Value); ko++; }
        for (int k = 1; k <= 9; k++) { double f; if (PipeModelBend.NamedRadii.TryGetValue(k, out f)) li.Set(k, 13, f); }
        st.List("B4", "Lists!$F$2:$F$3");
        st.Validate("B4", new XlsxValidation { PromptTitle = "Vertical axis", Prompt = "Y (CAESAR's default) or Z." });

        var calc = b.Add(PipeWorkbookGui.Calc);
        var pts = b.Add(PipeWorkbookGui.Pts);
        PipeWorkbookGui.ViewSheets(b, t, view, calc, pts, n, m.Restraints.Count, m.Factor(0), 1.0 / m.Factor(20), u.Len);

        var pl = b.Add("_pluto");
        pl.State = "veryHidden";
        string compact = Compact(json);
        int nc = (compact.Length + Chunk - 1) / Chunk;
        pl.Set(0, 0, Format); pl.Set(0, 1, Version);
        pl.Set(1, 0, "base"); pl.Set(1, 1, hash);
        pl.Set(2, 0, "source"); pl.Set(2, 1, sourceName ?? "");
        pl.Set(3, 0, "chunks"); pl.Set(3, 1, nc);
        for (int k = 0; k < nc; k++) pl.Set(4 + k, 0, compact.Substring(k * Chunk, Math.Min(Chunk, compact.Length - k * Chunk)));

        // ---- Start: how to use it, the sheets, the live checks, the colour key, the reference
        Func<string, string, string> rowsOf = (sheet, cols) => "SUMPRODUCT(--(LEN(" + string.Join("&", cols.Split(',').Select(c => sheet + "!$" + c + "$2:$" + c + "$" + L2).ToArray()) + ")>0))";
        var sheets = new List<PipeWorkbookGui.SheetInfo>
        {
            new PipeWorkbookGui.SheetInfo("Settings", "title, notes, vertical axis, ambient temperature", null),
            new PipeWorkbookGui.SheetInfo("Elements", "one row per element, in CAESAR's order: nodes, lengths, type, section, process, bend, components", rowsOf("Elements", "B,C")),
            new PipeWorkbookGui.SheetInfo(PipeWorkbookGui.View, "the model drawn live as you edit it", null),
            new PipeWorkbookGui.SheetInfo("Sections", "pipe sizes: OD, wall, insulation, corrosion, material", rowsOf("Sections", "A,B")),
            new PipeWorkbookGui.SheetInfo("Processes", "operating conditions: T1-T9, P1-P9, fluid density", rowsOf("Processes", "A,B")),
            new PipeWorkbookGui.SheetInfo("Restraints", "anchors, supports, guides, limit stops, springs, ties to other nodes", rowsOf("Restraints", "B,C")),
            new PipeWorkbookGui.SheetInfo("SIFs", "SIFs and tees at nodes", rowsOf("SIFs", "B,C")),
            new PipeWorkbookGui.SheetInfo("Forces", "forces and moments at nodes, per load vector", rowsOf("Forces", "B,C")),
            new PipeWorkbookGui.SheetInfo("Displacements", "imposed displacements at nodes, per load vector", rowsOf("Displacements", "B,C")),
            new PipeWorkbookGui.SheetInfo("Uniform", "uniform loads (weight, seismic g's) on elements", rowsOf("Uniform", "A,B")),
            new PipeWorkbookGui.SheetInfo("Wind", "wind / wave data on elements", rowsOf("Wind", "A,B")),
            new PipeWorkbookGui.SheetInfo("Joints", "couplings, hinges, slip joints: ties between two nodes per DOF", rowsOf("Joints", "B,C")),
            new PipeWorkbookGui.SheetInfo("Coords", "where pieces of the model start", rowsOf("Coords", "A")),
            new PipeWorkbookGui.SheetInfo("Nodes", "read-only: every node, its position and what is on it", null),
            new PipeWorkbookGui.SheetInfo("Carried", "read-only: what the job holds that the sheets do not show (kept as written)", null),
        };
        Func<string, string, string> textIn = (sheet, cols) => string.Join("+", cols.Split(',').Select(c => "SUMPRODUCT(--ISTEXT(" + sheet + "!$" + c.Split(':')[0] + "$2:$" + c.Split(':')[c.Split(':').Length - 1] + "$" + L2 + "))").ToArray());
        Func<string, string, string> errIn = (sheet, cols) => "SUMPRODUCT(--ISERROR(" + sheet + "!$" + cols.Split(':')[0] + "$2:$" + cols.Split(':')[1] + "$" + L2 + "))";
        Func<string, string, string, string> unknown = (rng, list, what) => "SUMPRODUCT((" + rng + "<>\"\")*(COUNTIF(" + list + "," + rng + ")=0))";
        Func<string, string> col = c => "Elements!$" + c + "$2:$" + c + "$" + L2;
        Func<string, string, string> missingNodes = (sheet, c) => "SUMPRODUCT(ISNUMBER(" + sheet + "!$" + c + "$2:$" + c + "$" + L2 + ")*(COUNTIF(" + nFrom + "," + sheet + "!$" + c + "$2:$" + c + "$" + L2 + ")+COUNTIF(" + nTo + "," + sheet + "!$" + c + "$2:$" + c + "$" + L2 + ")=0))";
        var checks = new List<PipeWorkbookGui.Check>
        {
            new PipeWorkbookGui.Check("Error values (#N/A, #DIV/0! ...) on the input sheets",
                errIn("Elements", "A:BA") + "+" + errIn("Sections", "A:J") + "+" + errIn("Processes", "A:T") + "+" + errIn("Restraints", "A:K") + "+" + errIn("Forces", "A:I") + "+" + errIn("Displacements", "A:I") + "+" +
                errIn("Uniform", "A:H") + "+" + errIn("Wind", "A:I") + "+" + errIn("Coords", "A:D"), false),
            new PipeWorkbookGui.Check("Elements: text where a number belongs", textIn("Elements", "B:F,J:J,M:T,W:BA") + "-SUMPRODUCT(" + string.Join("+", PipeWorkbookGui.RadiusNames.Select(rn => "(UPPER(TRIM(" + col("J") + "))=\"" + rn + "\")").ToArray()) + ")", false),
            new PipeWorkbookGui.Check("Elements: rows without From or To", "SUMPRODUCT(((" + col("B") + "=\"\")+(" + col("C") + "=\"\")>0)*(LEN(" + col("A") + "&" + col("D") + "&" + col("E") + "&" + col("F") + "&" + col("G") + ")>0))", false),
            new PipeWorkbookGui.Check("Elements: unknown Type, Section or Process",
                unknown(col("G"), PipeWorkbookGui.LType, "") + "+" + unknown(col("H"), secList, "") + "+" + unknown(col("I"), procList, ""), false),
            new PipeWorkbookGui.Check("Sections / Processes: text where a number belongs", textIn("Sections", "B:J") + "+" + textIn("Processes", "B:T"), false),
            new PipeWorkbookGui.Check("Restraints: unknown type, or text where a number belongs", unknown("Restraints!$C$2:$C$" + L2, typeList, "") + "+" + textIn("Restraints", "B:B,D:J"), false),
            new PipeWorkbookGui.Check("Forces / Displacements / Uniform: text where a number belongs, or a vector outside 1-9",
                textIn("Forces", "B:I") + "+" + textIn("Displacements", "B:I") + "+" + textIn("Uniform", "B:G") + "+" +
                string.Join("+", new[] { "Forces!$C", "Displacements!$C", "Uniform!$D" }.Select(c => "SUMPRODUCT(ISNUMBER(" + c + "$2:" + c.Substring(c.IndexOf('!') + 1) + "$" + L2 + ")*((" + c + "$2:" + c.Substring(c.IndexOf('!') + 1) + "$" + L2 + "<1)+(" + c + "$2:" + c.Substring(c.IndexOf('!') + 1) + "$" + L2 + ">9)>0))").ToArray()), false),
            new PipeWorkbookGui.Check("Uniform / Wind: unknown element Ids (single Ids)",
                "SUMPRODUCT((Uniform!$A$2:$A$" + L2 + "<>\"\")*ISERROR(FIND(\":\",Uniform!$A$2:$A$" + L2 + "))*(COUNTIF(" + col("A") + ",Uniform!$A$2:$A$" + L2 + ")=0))+SUMPRODUCT((Wind!$A$2:$A$" + L2 +
                "<>\"\")*ISERROR(FIND(\":\",Wind!$A$2:$A$" + L2 + "))*(COUNTIF(" + col("A") + ",Wind!$A$2:$A$" + L2 + ")=0))", false),
            new PipeWorkbookGui.Check("Restraints, SIFs, loads: nodes on no element's From / To (bend nodes are fine)",
                missingNodes("Restraints", "B") + "+" + missingNodes("SIFs", "B") + "+" + missingNodes("Forces", "B") + "+" + missingNodes("Displacements", "B"), true),
            new PipeWorkbookGui.Check("Elements: a bend node number used twice (on two bends, or also a From / To): CAESAR stops on it",
                "SUMPRODUCT(--" + PipeWorkbookGui.BendNodeTwice(col("K"), nBend, nFrom, nTo) + ")", false),
            new PipeWorkbookGui.Check("Ids used twice (a copied row is read as a new item)",
                "SUMPRODUCT((" + col("A") + "<>\"\")*(COUNTIF(" + col("A") + "," + col("A") + ")>1))+SUMPRODUCT((Restraints!$A$2:$A$" + L2 + "<>\"\")*(COUNTIF(Restraints!$A$2:$A$" + L2 + ",Restraints!$A$2:$A$" + L2 + ")>1))", true),
        };
        string[] reference =
        {
            "Units are the job's, shown in each header: " + u.Len + ", " + u.Force + ", " + u.Temp + ", " + u.Press + ". A length header may name another length unit (in ft mm cm m): the column is read in that unit.",
            "Id: keep it. A row without an Id is a new item; a deleted row deletes the item; moving rows reorders the elements (CAESAR's order). A copied row keeps its original's Id: clear it.",
            "Elements: a bend radius makes the element a bend at its TO node (LR / SR = 1.5 / 1 x nominal). Bend nodes: angle:node pairs, M = the mid point (M:19; 0:18).",
            "Bend radius by name: LR or Long (1.5 x nominal), SR or Short (1 x), 3D, 5D. A radius CAESAR wrote by name shows its name and is written back as CAESAR's code. A negative number (-2.0202, -6.0606 ...) is a code CAESAR reads as no radius (amber): keep it or replace it.",
            "Section / Process: a name from those sheets; blank = the row above's. The grouped columns at the right of Elements (+) override one value for one element.",
            "Restraints: Stiffness blank = rigid; Cos blank = the type's own axis (guides and limit stops take the pipe's). Forces / Displacements: one row per node and load vector (1-9); a displacement left blank is free, 0 holds it.",
            "Uniform / Wind: per element; an element no row names has none (CAESAR carries a load on to later elements; Pluto ends it there). Element = an Id, a range in the Elements order (E3:E9), or blank with From and To (a chain of elements).",
            "Joints: DOFs like 'axial gap=0.125 k=5000; shear; torsion; bending gap=2' (frame pipe) or 'X; Y; RZ k=1e6' (frame global); a DOF not listed is free.",
            "Read back: only the input sheets. Start, 3D View, Nodes and Carried are rebuilt on every export. The hidden _pluto sheet holds the model this workbook was written from: what you did not change is given back exactly.",
        };
        PipeWorkbookGui.StartSheet(start, t, m.Title ?? "", sourceName ?? "", u.Len + ", " + u.Force + ", " + u.Temp + ", " + u.Press, sheets, checks, reference, run);
        return b.ToBytes();
    }

    static void Head(XlsxBookSheet s, params string[] h) { for (int c = 0; c < h.Length; c++) { s.Set(0, c, h[c], XlsxBook.Header); s.Width(c, Math.Max(8, Math.Min(30, h[c].Length + 2))); } }
    static void Row(XlsxBookSheet s, PipeWorkbookGui.Theme t, int r, string k, string v, string note, string kind)
    {
        s.Set(r, 0, k, t.Label); s.Set(r, 1, v ?? "", t.Value); s.Set(r, 2, note, XlsxBook.Note);
        if (kind == "text") s.Validate(XlsxBook.Ref(r, 1), new XlsxValidation { PromptTitle = k, Prompt = note });
        if (kind == "axis") s.Conditional(XlsxBook.Ref(r, 1), "AND(" + XlsxBook.Ref(r, 1) + "<>\"Y\"," + XlsxBook.Ref(r, 1) + "<>\"Z\")", t.Red);
    }

    static void RowRo(XlsxBookSheet s, int r, string k, string v, string note) { s.Set(r, 0, k, XlsxBook.Bold); s.Set(r, 1, v ?? "", XlsxBook.ReadOnly); s.Set(r, 2, note, XlsxBook.Note); }

    // one row per used vector (an entry with none: one row of vector 1); the rows written
    static int LoadRows(XlsxBookSheet s, List<PipeModelNodeLoad> loads, string prefix, bool disp)
    {
        int r = 1;
        for (int i = 0; i < loads.Count; i++)
        {
            var d = loads[i];
            var used = Enumerable.Range(0, 9).Where(d.Used).ToList();
            if (used.Count == 0) used.Add(0);
            foreach (int v in used)
            {
                s.Set(r, 0, prefix + (i + 1)); s.Set(r, 1, d.Node); s.Set(r, 2, v + 1);
                for (int k = 0; k < 6; k++)
                {
                    double x = d.Vectors[v][k];
                    if (double.IsNaN(x)) continue;          // free (displacement) / not given (force): blank
                    if (!disp && x == 0) continue;
                    s.Set(r, 3 + k, x);
                }
                r++;
            }
        }
        return r - 1;
    }

    static string BendNodesText(PipeModelBend b)
    {
        return string.Join("; ", b.AngleNodes.Select(an => (Math.Abs(an[0] - PipeModelBend.MidPoint) < 1e-3 ? "M" : R(an[0])) + ":" + R(an[1])).ToArray());
    }

    static string DofsText(PipeModelJoint j)
    {
        return string.Join("; ", j.Dofs.Select(d => d.Dof + (d.Gap != 0 ? " gap=" + R(d.Gap) : "") + (d.Stiffness != 0 ? " k=" + R(d.Stiffness) : "")).ToArray());
    }

    static void NodesSheet(XlsxBookSheet s, PipeModel m, Units u)
    {
        s.TabColor = "FF7F7F7F";
        s.FreezeRows = 1;
        Head(s, "Node", "X (" + u.Len + ")", "Y (" + u.Len + ")", "Z (" + u.Len + ")", "Elements", "Restraints", "Loads");
        s.Width(4, 22); s.Width(5, 26); s.Width(6, 26);
        s.Set(0, 8, "read-only: rebuilt on every export, never read back", XlsxBook.Note);
        var els = new SortedDictionary<int, List<string>>();
        foreach (var e in m.Elements)
            foreach (int n in new[] { e.From, e.To }) { List<string> l; if (!els.TryGetValue(n, out l)) { l = new List<string>(); els[n] = l; } l.Add("E" + e.Uid); }
        CaesarPipeGraph g = null;
        try { g = CaesarGeometry.Build(CaesarNeutralReader.ParseText(CaesarNeutralWriter.ToText(m))); }
        catch (Exception ex) { s.Set(1, 8, "positions not computed: " + ex.Message, XlsxBook.Note); }
        int r = 1;
        foreach (var kv in els)
        {
            s.Set(r, 0, kv.Key, XlsxBook.ReadOnly);
            var p = g == null ? null : g.Position(kv.Key);
            if (p != null) for (int k = 0; k < 3; k++) s.Set(r, 1 + k, p[k], XlsxBook.ReadOnly);
            s.Set(r, 4, string.Join(" ", kv.Value.ToArray()), XlsxBook.ReadOnly);
            s.Set(r, 5, string.Join(" ", m.Restraints.Where(x => x.Node == kv.Key).Select(x => x.Type + (x.CNode > 0 ? ">" + x.CNode : "")).ToArray()), XlsxBook.ReadOnly);
            s.Set(r, 6, string.Join(" ", m.NodeLoads.Where(x => x.Node == kv.Key).SelectMany(x => Enumerable.Range(0, 9).Where(x.Used).Select(v => (x.IsDisplacement ? "D" : "F") + (v + 1))).ToArray()), XlsxBook.ReadOnly);
            r++;
        }
    }

    static void CarriedSheet(XlsxBookSheet s, PipeModel m)
    {
        s.TabColor = "FF7F7F7F";
        Head(s, "Where", "What", "Lines");
        s.Width(0, 22); s.Width(1, 60);
        s.Set(0, 4, "read-only: what the model carries as written and does not show; it travels with its element", XlsxBook.Note);
        int r = 1;
        foreach (var e in m.Elements)
            foreach (var kv in e.Carried.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                s.Set(r, 0, "E" + e.Uid + " (" + e.From + "-" + e.To + ")", XlsxBook.ReadOnly); s.Set(r, 1, kv.Key + " block " + kv.Value.Seq, XlsxBook.ReadOnly);
                s.Set(r, 2, kv.Value.Lines.Count, XlsxBook.ReadOnly); r++;
            }
        if (m.Source != null && !m.Source.Seed)
        {
            s.Set(r, 0, "MISCEL_1", XlsxBook.ReadOnly); s.Set(r, 1, "nozzles, hangers and execution options (the ambient is shown in Settings)", XlsxBook.ReadOnly); s.Set(r, 2, m.Source.MiscelTail.Count, XlsxBook.ReadOnly); r++;
            foreach (var kv in m.Source.Sections.OrderBy(k => k.Key, StringComparer.Ordinal))
            { s.Set(r, 0, kv.Key, XlsxBook.ReadOnly); s.Set(r, 1, "the section as written", XlsxBook.ReadOnly); s.Set(r, 2, kv.Value.Count, XlsxBook.ReadOnly); r++; }
        }
        if (r == 1) s.Set(1, 0, "(nothing carried)", XlsxBook.Note);
    }

    // the base JSON without its line breaks and indents (they are never inside a string: PipeModelJson escapes them)
    static string Compact(string json)
    {
        var sb = new StringBuilder(json.Length);
        int i = 0;
        while (i < json.Length)
        {
            char c = json[i];
            if (c == '\n') { i++; while (i < json.Length && json[i] == ' ') i++; continue; }
            sb.Append(c); i++;
        }
        return sb.ToString();
    }

    // ======================================================================== read

    sealed class Ctx
    {
        public PipeModel M, Base;
        public Units U;
        public readonly List<string> Errors = new List<string>();
        public readonly List<string> Warnings = new List<string>();
        public void Err(string s) { if (Errors.Count < 200) Errors.Add(s); }
    }

    // a sheet's columns by header name (the text before " ("), with the header's unit
    sealed class Cols
    {
        public CaesarSheet S;
        public readonly Dictionary<string, int> At = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<int, string> Unit = new Dictionary<int, string>();
        public int Rows { get { return S.Rows.Count; } }
    }

    static Cols ColsOf(CaesarSheet s, Ctx x, string[] known)
    {
        var c = new Cols { S = s };
        var head = s.Rows.Count > 0 ? s.Rows[0] : null;
        if (head == null) return c;
        for (int k = 0; k < head.Length; k++)
        {
            string h = (head[k] ?? "").Trim();
            if (h.Length == 0) continue;
            string name = h, unit = null;
            int p = h.IndexOf(" (", StringComparison.Ordinal);
            if (p > 0 && h.EndsWith(")", StringComparison.Ordinal)) { name = h.Substring(0, p).Trim(); unit = h.Substring(p + 2, h.Length - p - 3).Trim(); }
            if (c.At.ContainsKey(name)) { x.Err(s.Name + "!" + XlsxBook.Ref(0, k) + ": a second column \"" + name + "\""); continue; }
            c.At[name] = k;
            if (unit != null) c.Unit[k] = unit;
            if (known != null && !known.Contains(name, StringComparer.OrdinalIgnoreCase)) x.Warnings.Add(s.Name + ": column \"" + h + "\" is not one the workbook reads (ignored)");
        }
        return c;
    }

    static string Text(Cols c, int row, string col, Ctx x)
    {
        int k;
        if (!c.At.TryGetValue(col, out k)) return "";
        string addr = XlsxBook.Ref(row, k);
        string prob;
        if (c.S.Problems.TryGetValue(addr, out prob)) { x.Err(c.S.Name + "!" + addr + ": " + prob); return ""; }
        return c.S.Text(row, k);
    }

    // a number cell: NaN when blank; a length column converted from its header's unit (kind: the factor index
    // of the unit the model holds -- 0 length, 20 diameter, 21 wall -- or -1 for no conversion)
    static double Num(Cols c, int row, string col, Ctx x, int kind, string expectUnit)
    {
        int k;
        if (!c.At.TryGetValue(col, out k)) return double.NaN;
        string t = Text(c, row, col, x);
        if (t.Length == 0) return double.NaN;
        double v;
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) { x.Err(c.S.Name + "!" + XlsxBook.Ref(row, k) + ": \"" + t + "\" is not a number"); return double.NaN; }
        string unit;
        if (expectUnit != null && c.Unit.TryGetValue(k, out unit) && !string.Equals(unit, expectUnit, StringComparison.OrdinalIgnoreCase))
        {
            double per, perJob;
            if (kind >= 0 && LengthUnits.TryGetValue(unit, out per))
            {
                double f = x.M.Factor(kind);
                if (LengthUnits.TryGetValue(expectUnit, out perJob) && Math.Abs(perJob * f - 1) < 1e-6) v = v * per / perJob;
                else v = v * per * f;
            }
            else x.Err(c.S.Name + "!" + XlsxBook.Ref(0, k) + ": the unit \"" + unit + "\" (the job's is \"" + expectUnit + "\"; " + (kind >= 0 ? "a length column takes in, ft, mm, cm or m" : "only length columns convert") + ")");
        }
        return v;
    }

    static int Int(Cols c, int row, string col, Ctx x, int dflt)
    {
        double v = Num(c, row, col, x, -1, null);
        if (double.IsNaN(v)) return dflt;
        if (Math.Abs(v - Math.Round(v)) > 1e-9) { x.Err(c.S.Name + "!" + XlsxBook.Ref(row, c.At[col]) + ": a whole number is expected (" + R(v) + ")"); return dflt; }
        return (int)Math.Round(v);
    }

    static bool Blank(CaesarSheet s, int row)
    {
        var r = row < s.Rows.Count ? s.Rows[row] : null;
        return r == null || r.All(v => v == null || v.Trim().Length == 0);
    }

    static int IdNum(string id, char prefix)
    {
        int n;
        if (id.Length > 1 && char.ToUpperInvariant(id[0]) == prefix && int.TryParse(id.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n > 0) return n;
        return 0;
    }

    public static PipeWorkbookResult Read(string path)
    {
        var res = new PipeWorkbookResult { Path = path ?? "" };
        var sheets = CaesarWorkbook.Read(path);
        Func<string, CaesarSheet> sheet = n => sheets.FirstOrDefault(s => string.Equals(s.Name, n, StringComparison.OrdinalIgnoreCase));
        var x = new Ctx();

        // the base model from _pluto
        var pl = sheet("_pluto");
        if (pl != null && pl.Text(0, 0) == Format)
        {
            int ver = (int)ToNum(pl.Text(0, 1));
            if (ver > Version) throw new InvalidOperationException("PipeWorkbook: " + Path.GetFileName(path) + " is workbook version " + ver + ", newer than this reader (" + Version + ")");
            int n = (int)ToNum(pl.Text(3, 1));
            var sb = new StringBuilder();
            for (int k = 0; k < n; k++) sb.Append(pl.Cell(4 + k, 0) ?? "");
            try { res.Base = PipeModelJson.Read(sb.ToString()); }
            catch (Exception ex) { throw new InvalidOperationException("PipeWorkbook: the hidden base model (_pluto) does not read (" + ex.Message + "): make the workbook again from its source"); }
            res.BaseHash = pl.Text(1, 1);
            res.SourceName = pl.Text(2, 1);
            if (Sha(PipeModelJson.Write(res.Base)) != res.BaseHash) res.Warnings.Add("the hidden base model does not match its fingerprint: it was changed outside Pluto");
        }
        else x.Warnings.Add("no hidden base model (_pluto): every row is read as new, and nothing CAESAR wrote is carried");
        var m = res.Base != null ? PipeModelJson.Read(PipeModelJson.Write(res.Base)) : new PipeModel();
        x.M = m; x.Base = res.Base; x.U = UnitsOf(m);

        ReadSettings(sheet("Settings"), x);
        var secs = ReadSections(sheet("Sections"), x);
        var procs = ReadProcesses(sheet("Processes"), x);
        var ids = ReadElements(sheet("Elements"), x, secs, procs);
        ReadRestraints(sheet("Restraints"), x);
        ReadSifs(sheet("SIFs"), x);
        var loadOrder = new List<PipeModelNodeLoad>(m.NodeLoads);
        ReadNodeLoads(sheet("Forces"), x, false);
        ReadNodeLoads(sheet("Displacements"), x, true);
        var reordered = m.NodeLoads.Select((d, i) => new { d, i, k = loadOrder.IndexOf(d) }).OrderBy(q => q.k < 0 ? int.MaxValue : q.k).ThenBy(q => q.i).Select(q => q.d).ToList();
        m.NodeLoads.Clear(); m.NodeLoads.AddRange(reordered);   // the base's order kept: an unedited workbook gives the base exactly
        ReadUniform(sheet("Uniform"), x, ids);
        ReadWind(sheet("Wind"), x, ids);
        ReadJoints(sheet("Joints"), x);
        ReadCoords(sheet("Coords"), x);
        m.AssignUids();
        foreach (string e in CaesarNeutralWriter.Validate(m)) x.Err(e);
        if (x.Errors.Count == 0)
        {
            try { CaesarNeutralWriter.ToLines(m); }
            catch (Exception ex) { x.Err(ex.Message.Replace("CaesarNeutralWriter: ", "")); }
        }
        if (x.Errors.Count > 0)
            throw new InvalidOperationException("PipeWorkbook: " + Path.GetFileName(path) + ": " + x.Errors.Count + " problem(s): " + string.Join("; ", x.Errors.Take(25).ToArray()) + (x.Errors.Count > 25 ? "; ..." : ""));
        res.Warnings.AddRange(x.Warnings);
        res.Model = m;
        res.ModelHash = Hash(m);
        if (res.Base != null) res.Changes = CaesarCompare.Compare(res.Base, "the base", m, "the workbook", 1e-6 * Math.Max(1, m.Factor(0)));
        return res;
    }

    static double ToNum(string s) { double v; return double.TryParse((s ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0; }

    // ---- Settings

    static void ReadSettings(CaesarSheet s, Ctx x)
    {
        if (s == null) { x.Err("no Settings sheet"); return; }
        var m = x.M;
        for (int r = 1; r < s.Rows.Count; r++)
        {
            string key = s.Text(r, 0), val = s.Text(r, 1), addr = "Settings!" + XlsxBook.Ref(r, 1);
            string prob;
            if (s.Problems.TryGetValue(XlsxBook.Ref(r, 1), out prob)) { x.Err(addr + ": " + prob); continue; }
            if (key == "Title") { if (val != (m.Title ?? "").Trim()) m.Title = val; }
            else if (key == "Notes") { if (val != (m.Notes ?? "").Trim()) m.Notes = val; }
            else if (key == "Vertical axis")
            {
                if (val.Equals("Y", StringComparison.OrdinalIgnoreCase)) m.ZUp = false;
                else if (val.Equals("Z", StringComparison.OrdinalIgnoreCase)) m.ZUp = true;
                else x.Err(addr + ": the vertical axis is Y or Z (\"" + val + "\")");
            }
            else if (key.StartsWith("Ambient", StringComparison.Ordinal))
            {
                double v;
                if (!double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) x.Err(addr + ": the ambient temperature is a number");
                else if (!Same(v, m.Ambient)) m.Ambient = v;
            }
        }
    }

    // ---- Sections, Processes

    static Dictionary<string, Sec> ReadSections(CaesarSheet s, Ctx x)
    {
        var d = new Dictionary<string, Sec>(StringComparer.OrdinalIgnoreCase);
        if (s == null) return d;
        var u = x.U;
        var c = ColsOf(s, x, SectionCols);
        for (int r = 1; r < s.Rows.Count; r++)
        {
            if (Blank(s, r)) continue;
            string name = Text(c, r, "Name", x);
            if (name.Length == 0) { x.Err("Sections row " + (r + 1) + ": no name"); continue; }
            if (d.ContainsKey(name)) { x.Err("Sections row " + (r + 1) + ": a second section \"" + name + "\""); continue; }
            var sec = new Sec
            {
                Name = name, Od = Num(c, r, "OD", x, 20, u.Dia), Wall = Num(c, r, "Wall", x, 21, u.Thk), Insul = Z(Num(c, r, "Insulation", x, 21, u.Thk)),
                Corr = Z(Num(c, r, "Corrosion", x, 21, u.Thk)), InsDens = Z(Num(c, r, "Insul density", x, -1, u.InsDens)), Material = Int(c, r, "Material", x, 0),
                E = Z(Num(c, r, "E", x, -1, u.E)), Poisson = Z(Num(c, r, "Poisson", x, -1, null)), PipeDens = Z(Num(c, r, "Pipe density", x, -1, u.PipeDens))
            };
            if (!(sec.Od > 0) || !(sec.Wall > 0)) x.Err("Sections row " + (r + 1) + " (\"" + name + "\"): OD and wall are required");
            d[name] = sec;
        }
        return d;
    }

    static Dictionary<string, Proc> ReadProcesses(CaesarSheet s, Ctx x)
    {
        var d = new Dictionary<string, Proc>(StringComparer.OrdinalIgnoreCase);
        if (s == null) return d;
        var u = x.U;
        var known = new List<string> { "Name", "Fluid density" };
        for (int k = 1; k <= 9; k++) { known.Add("T" + k); known.Add("P" + k); }
        var c = ColsOf(s, x, known.ToArray());
        for (int r = 1; r < s.Rows.Count; r++)
        {
            if (Blank(s, r)) continue;
            string name = Text(c, r, "Name", x);
            if (name.Length == 0) { x.Err("Processes row " + (r + 1) + ": no name"); continue; }
            if (d.ContainsKey(name)) { x.Err("Processes row " + (r + 1) + ": a second process \"" + name + "\""); continue; }
            var p = new Proc { Name = name, Fluid = Z(Num(c, r, "Fluid density", x, -1, u.FluidDens)) };
            for (int k = 0; k < 9; k++) { p.T[k] = Z(Num(c, r, "T" + (k + 1), x, -1, u.Temp)); p.P[k] = Z(Num(c, r, "P" + (k + 1), x, -1, u.Press)); }
            d[name] = p;
        }
        return d;
    }

    // ---- Elements

    static string[] ElementKnown()
    {
        var u = new Units();
        Array.Copy(EnglishLabels, u.L, 24);
        return ElementHeaders(u).Select(h => { int p = h.IndexOf(" (", StringComparison.Ordinal); return p > 0 ? h.Substring(0, p) : h; }).ToArray();
    }

    // returns element id (as written, "E12") -> element
    static Dictionary<string, PipeModelElement> ReadElements(CaesarSheet s, Ctx x, Dictionary<string, Sec> secs, Dictionary<string, Proc> procs)
    {
        var ids = new Dictionary<string, PipeModelElement>(StringComparer.OrdinalIgnoreCase);
        var m = x.M;
        if (s == null) { x.Err("no Elements sheet"); return ids; }
        var u = x.U;
        var c = ColsOf(s, x, ElementKnown());
        foreach (string req in new[] { "Id", "From", "To", "DX", "DY", "DZ", "Type", "Section", "Process" })
            if (!c.At.ContainsKey(req)) x.Err("Elements: no \"" + req + "\" column");
        if (x.Errors.Count > 0) return ids;
        var byUid = m.Elements.ToDictionary(e => e.Uid);
        var list = new List<PipeModelElement>();
        var seen = new HashSet<int>();
        Sec lastSec = null; Proc lastProc = null;
        for (int r = 1; r < s.Rows.Count; r++)
        {
            if (Blank(s, r)) continue;
            string w = "Elements row " + (r + 1);
            string id = Text(c, r, "Id", x);
            PipeModelElement e = null;
            int uid = IdNum(id, 'E');
            if (uid > 0 && byUid.TryGetValue(uid, out e))
            {
                if (!seen.Add(uid)) { x.Warnings.Add(w + ": Id " + id + " repeats (a copied row?): read as a new element"); e = null; }
            }
            else if (id.Length > 0) x.Warnings.Add(w + ": Id \"" + id + "\" is not in the base model: read as a new element");
            if (id.Length > 0) w += " (" + id + ")";
            if (e == null) e = new PipeModelElement();
            int from = Int(c, r, "From", x, 0), to = Int(c, r, "To", x, 0);
            if (from <= 0 || to <= 0) { x.Err(w + ": From and To node numbers are required"); continue; }
            e.From = from; e.To = to;
            SetN(ref e.Dx, Z(Num(c, r, "DX", x, 0, u.Len))); SetN(ref e.Dy, Z(Num(c, r, "DY", x, 0, u.Len))); SetN(ref e.Dz, Z(Num(c, r, "DZ", x, 0, u.Len)));
            // section and process (blank = the row above's), then this element's overrides
            string sn = Text(c, r, "Section", x), pn = Text(c, r, "Process", x);
            Sec sec = lastSec; Proc proc = lastProc;
            if (sn.Length > 0 && !secs.TryGetValue(sn, out sec)) { x.Err(w + ": unknown section \"" + sn + "\" (Sections: " + string.Join(", ", secs.Keys.Take(12).ToArray()) + ")"); continue; }
            if (pn.Length > 0 && !procs.TryGetValue(pn, out proc)) { x.Err(w + ": unknown process \"" + pn + "\" (Processes: " + string.Join(", ", procs.Keys.Take(12).ToArray()) + ")"); continue; }
            if (sec == null) { x.Err(w + ": no section (and none above)"); continue; }
            if (proc == null) { x.Err(w + ": no process (and none above)"); continue; }
            lastSec = sec; lastProc = proc;
            SetN(ref e.Od, Ov(c, r, "OD", x, 20, u.Dia, sec.Od)); SetN(ref e.Wall, Ov(c, r, "Wall", x, 21, u.Thk, sec.Wall));
            SetN(ref e.Insulation, Ov(c, r, "Insulation", x, 21, u.Thk, sec.Insul)); SetN(ref e.Corrosion, Ov(c, r, "Corrosion", x, 21, u.Thk, sec.Corr));
            SetN(ref e.InsulationDensity, Ov(c, r, "Insul density", x, -1, u.InsDens, sec.InsDens));
            e.Material = Int(c, r, "Material", x, sec.Material);
            SetN(ref e.Emod, Ov(c, r, "E", x, -1, u.E, sec.E)); SetN(ref e.Poisson, Ov(c, r, "Poisson", x, -1, null, sec.Poisson)); SetN(ref e.PipeDensity, Ov(c, r, "Pipe density", x, -1, u.PipeDens, sec.PipeDens));
            for (int k = 0; k < 9; k++) { SetN(ref e.T[k], Ov(c, r, "T" + (k + 1), x, -1, u.Temp, proc.T[k])); SetN(ref e.P[k], Ov(c, r, "P" + (k + 1), x, -1, u.Press, proc.P[k])); }
            SetN(ref e.FluidDensity, Ov(c, r, "Fluid density", x, -1, u.FluidDens, proc.Fluid));
            // type: rigid / expansion joint / reducer
            string type = Text(c, r, "Type", x);
            if (type.Length == 0) type = "Pipe";
            int rigidCode = RigidCode(type);
            if (rigidCode >= 0)
            {
                if (e.Rigid == null) e.Rigid = new PipeModelRigid();
                if (e.Rigid.Type != rigidCode) e.Rigid.Type = rigidCode;
                SetN(ref e.Rigid.Weight, Z(Num(c, r, "Weight", x, -1, u.Force)));
            }
            else if (!type.Equals("Pipe", StringComparison.OrdinalIgnoreCase) && !type.Equals("Reducer", StringComparison.OrdinalIgnoreCase) && !type.Equals("Exp joint", StringComparison.OrdinalIgnoreCase))
            { x.Err(w + ": type \"" + type + "\" (" + string.Join(", ", ElementTypes) + ")"); continue; }
            else e.Rigid = null;
            if (type.Equals("Exp joint", StringComparison.OrdinalIgnoreCase))
            {
                if (e.ExpJoint == null) e.ExpJoint = new PipeModelExpJoint();
                var j = e.ExpJoint;
                SetN(ref j.AxialStiffness, Z(Num(c, r, "EJ axial", x, -1, u.TransK))); SetN(ref j.TransverseStiffness, Z(Num(c, r, "EJ transverse", x, -1, u.TransK)));
                SetN(ref j.BendingStiffness, Z(Num(c, r, "EJ bending", x, -1, u.RotK))); SetN(ref j.TorsionStiffness, Z(Num(c, r, "EJ torsion", x, -1, u.RotK)));
                SetN(ref j.EffectiveId, Z(Num(c, r, "EJ eff ID", x, 20, u.Dia)));
            }
            else e.ExpJoint = null;
            if (type.Equals("Reducer", StringComparison.OrdinalIgnoreCase))
            {
                if (e.Reducer == null) e.Reducer = new PipeModelReducer();
                double od2 = Num(c, r, "OD2", x, 20, u.Dia), w2 = Num(c, r, "Wall2", x, 21, u.Thk);
                if (!(od2 > 0) || !(w2 > 0)) x.Err(w + ": a reducer needs OD2 and Wall2 (its TO end)");
                SetN(ref e.Reducer.Od2, Z(od2)); SetN(ref e.Reducer.Wall2, Z(w2));
            }
            else e.Reducer = null;
            // bend at the TO end
            string br = Text(c, r, "Bend radius", x);
            if (br.Length == 0) e.Bend = null;
            else
            {
                double rad, mult = PipeModelBend.NamedMultiple(br);
                if (!double.IsNaN(mult))
                {
                    // a radius by name (LR / Long, SR / Short, 3D, 5D): a code CAESAR wrote for that name stays as written;
                    // otherwise the multiple of the nominal size, as a plain radius
                    int had = e.Bend != null ? PipeModelBend.RadiusCode(e.Bend.Radius) : 0;
                    double f;
                    if (had > 0 && PipeModelBend.NamedRadii.TryGetValue(had, out f) && f == mult) rad = e.Bend.Radius;
                    else
                    {
                        double nps = PipeBuilder.NominalOf(e.Od / x.M.Factor(20));
                        if (double.IsNaN(nps)) { x.Err(w + ": bend radius " + br + " needs a standard pipe size (give the radius)"); continue; }
                        rad = mult * nps * x.M.Factor(0);
                    }
                }
                else rad = Num(c, r, "Bend radius", x, 0, u.Len);
                if (!(rad > 0) && PipeModelBend.RadiusCode(rad) == 0) { x.Err(w + ": the bend radius must be positive, a name (LR, SR, Long, Short, 3D, 5D), or a code CAESAR wrote (-1.0101 ... -9.0909)"); continue; }
                if (PipeModelBend.NoRadiusCode(PipeModelBend.RadiusCode(rad))) x.Warnings.Add(w + ": bend radius " + R(rad) + " is a code CAESAR reads as no radius (0)");
                bool fresh = e.Bend == null;
                if (fresh) e.Bend = new PipeModelBend();
                var b = e.Bend;
                SetN(ref b.Radius, rad);
                string bt = Text(c, r, "Bend type", x);
                int btc = bt.Length == 0 ? 0 : Array.FindIndex(BendTypes, t => t.Equals(bt, StringComparison.OrdinalIgnoreCase));
                if (btc < 0 && !int.TryParse(bt, NumberStyles.Integer, CultureInfo.InvariantCulture, out btc)) { x.Err(w + ": bend type \"" + bt + "\" (" + string.Join(", ", BendTypes) + ")"); continue; }
                b.Type = btc;
                var an = BendNodes(Text(c, r, "Bend nodes", x), w, x);
                if (an != null && !SameNodes(an, b.AngleNodes)) { b.AngleNodes.Clear(); b.AngleNodes.AddRange(an); }
                b.MiterPoints = Int(c, r, "Bend miter points", x, 0);
                // blank fitting thickness: a new bend's is the wall (as CAESAR writes it); one that was 0 stays 0; one that was the
                // wall follows the wall
                double fit = Num(c, r, "Bend fitting thk", x, 21, u.Thk);
                if (double.IsNaN(fit)) fit = fresh ? e.Wall : b.FittingThickness == 0 ? 0 : SameAsOldWall(x, e) ? e.Wall : b.FittingThickness;
                SetN(ref b.FittingThickness, fit);
                SetN(ref b.KFactor, Z(Num(c, r, "Bend K factor", x, -1, null)));
            }
            string name = Text(c, r, "Name", x), line = Text(c, r, "Line", x);
            if (name != (e.Name ?? "").Trim()) e.Name = name;
            if (line != (e.LineNumber ?? "").Trim()) e.LineNumber = line;
            e.Kind = e.Rigid != null ? (rigidCode == 1 ? "valve" : rigidCode == 2 ? "flange" : rigidCode == 3 ? "flange_pair" : rigidCode == 4 ? "flanged_valve" : "rigid")
                   : e.ExpJoint != null ? "expansion_joint" : e.Reducer != null ? "reducer" : e.Bend != null ? "bend" : "pipe";
            list.Add(e);
            if (id.Length > 0 && !ids.ContainsKey(id)) ids[id] = e;
        }
        foreach (var e in m.Elements.Where(q => !list.Contains(q))) x.Warnings.Add("element E" + e.Uid + " (" + e.From + "-" + e.To + ") has no row: deleted" + (e.Carried.Count > 0 ? " with its carried " + string.Join(", ", e.Carried.Keys.ToArray()) + " block(s)" : ""));
        m.Elements.Clear();
        m.Elements.AddRange(list);
        m.AssignUids();
        return ids;
    }

    // an override cell, else the section / process value
    static double Ov(Cols c, int r, string col, Ctx x, int kind, string unit, double from)
    {
        double v = Num(c, r, col, x, kind, unit);
        return double.IsNaN(v) ? from : v;
    }

    // the fitting thickness was the base wall (blank in the sheet): it follows the wall
    static bool SameAsOldWall(Ctx x, PipeModelElement e)
    {
        if (x.Base == null) return true;
        var old = x.Base.Elements.FirstOrDefault(q => q.Uid == e.Uid);
        return old == null || old.Bend == null || Same(old.Bend.FittingThickness, old.Wall);
    }

    static int RigidCode(string type)
    {
        string t = type.Trim();
        if (t.Equals("Rigid", StringComparison.OrdinalIgnoreCase)) return 0;
        if (t.Equals("Valve", StringComparison.OrdinalIgnoreCase)) return 1;
        if (t.Equals("Flange", StringComparison.OrdinalIgnoreCase)) return 2;
        if (t.Equals("Flange pair", StringComparison.OrdinalIgnoreCase)) return 3;
        if (t.Equals("Flanged valve", StringComparison.OrdinalIgnoreCase)) return 4;
        int n;
        if (t.StartsWith("Rigid ", StringComparison.OrdinalIgnoreCase) && int.TryParse(t.Substring(6), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return n;
        return -1;
    }

    static List<double[]> BendNodes(string t, string w, Ctx x)
    {
        var l = new List<double[]>();
        if (t.Trim().Length == 0) return l;
        foreach (string part in t.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split(':');
            double ang, node;
            if (kv.Length != 2) { x.Err(w + ": bend nodes \"" + t + "\" (angle:node pairs, M = mid point: M:19; 0:18)"); return null; }
            string a = kv[0].Trim();
            if (a.Equals("M", StringComparison.OrdinalIgnoreCase)) ang = PipeModelBend.MidPoint;
            else if (!double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out ang)) { x.Err(w + ": bend node angle \"" + a + "\""); return null; }
            if (!double.TryParse(kv[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out node) || node <= 0) { x.Err(w + ": bend node \"" + kv[1].Trim() + "\""); return null; }
            l.Add(new[] { ang, Math.Round(node) });
        }
        if (l.Count > 3) { x.Err(w + ": at most 3 bend nodes"); return null; }
        return l;
    }

    static bool SameNodes(List<double[]> a, List<double[]> b)
    {
        if (a.Count != b.Count) return false;
        for (int k = 0; k < a.Count; k++) if (!Same(a[k][0], b[k][0]) || Math.Round(a[k][1]) != Math.Round(b[k][1])) return false;
        return true;
    }

    // ---- Restraints, SIFs, Joints

    static void ReadRestraints(CaesarSheet s, Ctx x)
    {
        var m = x.M;
        if (s == null) { m.Restraints.Clear(); return; }
        var c = ColsOf(s, x, new[] { "Id", "Node", "Type", "CNode", "Stiffness", "Gap", "Mu", "Cos X", "Cos Y", "Cos Z", "Tag" });
        var old = new List<PipeModelRestraint>(m.Restraints);
        var list = new List<PipeModelRestraint>();
        var seen = new HashSet<int>();
        for (int r = 1; r < s.Rows.Count; r++)
        {
            if (Blank(s, r)) continue;
            string id = Text(c, r, "Id", x), w = "Restraints row " + (r + 1) + (id.Length > 0 ? " (" + id + ")" : "");
            int n = IdNum(id, 'R');
            PipeModelRestraint q = n > 0 && n <= old.Count && seen.Add(n) ? old[n - 1] : null;
            if (q == null) q = new PipeModelRestraint { Source = "workbook" };
            int node = Int(c, r, "Node", x, 0);
            if (node <= 0) { x.Err(w + ": a node is required"); continue; }
            if (node != q.Node) { q.Node = node; q.OwnerUid = 0; q.Slot = -1; }
            string type = Text(c, r, "Type", x).ToUpperInvariant();
            int code;
            if (type != (q.Type ?? "").ToUpperInvariant() || q.Code == 0)
            {
                if (!PipeModelCodes.Restraint.TryGetValue(type, out code)) { x.Err(w + ": restraint type \"" + type + "\" (CAESAR's: " + string.Join(" ", PipeModelCodes.BuildTypes) + " ...)"); continue; }
                q.Type = PipeModelCodes.NameOf(code); q.Code = code;
            }
            q.CNode = Int(c, r, "CNode", x, 0);
            double k = Num(c, r, "Stiffness", x, -1, null);
            if (double.IsNaN(k) || k == 0) { if (!IsRigid(m, q)) q.Stiffness = 0; }
            else SetN(ref q.Stiffness, k);
            SetN(ref q.Gap, Z(Num(c, r, "Gap", x, -1, null)));
            SetN(ref q.Mu, Z(Num(c, r, "Mu", x, -1, null)));
            double cx = Num(c, r, "Cos X", x, -1, null), cy = Num(c, r, "Cos Y", x, -1, null), cz = Num(c, r, "Cos Z", x, -1, null);
            if (double.IsNaN(cx) && double.IsNaN(cy) && double.IsNaN(cz))
            {
                if (q.Cosines != null && !SameCos(q.Cosines, PipeModelCodes.DefaultCosines(q.Type))) q.Cosines = null;
                else if (q.Cosines != null && q.Raw == null) q.Cosines = null;
            }
            else
            {
                var cos = new[] { Z(cx), Z(cy), Z(cz) };
                if (q.Cosines == null || !SameCos(q.Cosines, cos)) q.Cosines = cos;
            }
            string tag = Text(c, r, "Tag", x);
            if (tag != (q.Tag ?? "").Trim()) q.Tag = tag;
            list.Add(q);
        }
        m.Restraints.Clear();
        m.Restraints.AddRange(list);
    }

    static void ReadSifs(CaesarSheet s, Ctx x)
    {
        var m = x.M;
        if (s == null) { m.Sifs.Clear(); return; }
        var c = ColsOf(s, x, new[] { "Id", "Node", "Type", "SIF in", "SIF out", "Surface node" });
        var old = new List<PipeModelSif>(m.Sifs);
        var list = new List<PipeModelSif>();
        var seen = new HashSet<int>();
        for (int r = 1; r < s.Rows.Count; r++)
        {
            if (Blank(s, r)) continue;
            string id = Text(c, r, "Id", x), w = "SIFs row " + (r + 1);
            int n = IdNum(id, 'S');
            var t = n > 0 && n <= old.Count && seen.Add(n) ? old[n - 1] : new PipeModelSif();
            int node = Int(c, r, "Node", x, 0);
            if (node <= 0) { x.Err(w + ": a node is required"); continue; }
            if (node != t.Node) { t.Node = node; t.OwnerUid = 0; t.Slot = -1; }
            t.Type = Int(c, r, "Type", x, 0);
            SetN(ref t.SifIn, Z(Num(c, r, "SIF in", x, -1, null))); SetN(ref t.SifOut, Z(Num(c, r, "SIF out", x, -1, null)));
            t.SurfaceNode = Int(c, r, "Surface node", x, 0);
            list.Add(t);
        }
        m.Sifs.Clear();
        m.Sifs.AddRange(list);
    }

    static void ReadJoints(CaesarSheet s, Ctx x)
    {
        var m = x.M;
        if (s == null) { m.Joints.Clear(); return; }
        var c = ColsOf(s, x, new[] { "Id", "Node", "CNode", "Kind", "Frame", "DOFs", "SIF", "Tag" });
        var old = m.Joints.ToDictionary(j => j.Uid);
        var list = new List<PipeModelJoint>();
        for (int r = 1; r < s.Rows.Count; r++)
        {
            if (Blank(s, r)) continue;
            string id = Text(c, r, "Id", x), w = "Joints row " + (r + 1);
            PipeModelJoint j;
            int n = IdNum(id, 'J');
            if (n <= 0 || !old.TryGetValue(n, out j)) j = new PipeModelJoint();
            else old.Remove(n);
            j.Node = Int(c, r, "Node", x, 0); j.CNode = Int(c, r, "CNode", x, 0);
            string kind = Text(c, r, "Kind", x); j.Kind = kind.Length > 0 ? kind : "custom";
            string frame = Text(c, r, "Frame", x).ToLowerInvariant();
            if (frame != "pipe" && frame != "global") { x.Err(w + ": frame \"" + frame + "\" (pipe or global)"); continue; }
            j.Frame = frame;
            j.Dofs.Clear();
            foreach (string part in Text(c, r, "DOFs", x).Split(';'))
            {
                var toks = part.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (toks.Length == 0) continue;
                var d = new PipeModelJointDof { Dof = toks[0] };
                foreach (string tk in toks.Skip(1))
                {
                    var kv = tk.Split('=');
                    double v;
                    if (kv.Length != 2 || !double.TryParse(kv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out v)) { x.Err(w + ": DOF option \"" + tk + "\" (gap=<value> or k=<value>)"); continue; }
                    if (kv[0].Equals("gap", StringComparison.OrdinalIgnoreCase)) d.Gap = v;
                    else if (kv[0].Equals("k", StringComparison.OrdinalIgnoreCase)) d.Stiffness = v;
                    else x.Err(w + ": DOF option \"" + tk + "\" (gap=<value> or k=<value>)");
                }
                j.Dofs.Add(d);
            }
            j.Sif = Z(Num(c, r, "SIF", x, -1, null));
            j.Tag = Text(c, r, "Tag", x);
            list.Add(j);
        }
        m.Joints.Clear();
        m.Joints.AddRange(list);
        int next = m.Joints.Count == 0 ? 1 : m.Joints.Max(j => j.Uid) + 1;
        foreach (var j in m.Joints) if (j.Uid <= 0) j.Uid = next++;
    }

    // ---- loads

    static void ReadNodeLoads(CaesarSheet s, Ctx x, bool disp)
    {
        var m = x.M;
        var old = (disp ? m.Displacements : m.Forces).ToList();
        m.NodeLoads.RemoveAll(d => d.IsDisplacement == disp);
        if (s == null) return;
        var u = x.U;
        string[] dofs = disp ? PipeModelCodes.DisplacementDofs : PipeModelCodes.ForceDofs;
        var c = ColsOf(s, x, new[] { "Id", "Node", "Vector" }.Concat(dofs).ToArray());
        char prefix = disp ? 'D' : 'F';
        // rows grouped by entry: an Id, else (for new rows) the node
        var groups = new List<KeyValuePair<string, List<int>>>();
        var byKey = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        for (int r = 1; r < s.Rows.Count; r++)
        {
            if (Blank(s, r)) continue;
            string id = Text(c, r, "Id", x);
            string key = id.Length > 0 ? "id:" + id : "node:" + Int(c, r, "Node", x, 0);
            List<int> rows;
            if (!byKey.TryGetValue(key, out rows)) { rows = new List<int>(); byKey[key] = rows; groups.Add(new KeyValuePair<string, List<int>>(key, rows)); }
            rows.Add(r);
        }
        var used = new HashSet<int>();
        foreach (var g in groups)
        {
            int n = g.Key.StartsWith("id:", StringComparison.Ordinal) ? IdNum(g.Key.Substring(3), prefix) : 0;
            var d = n > 0 && n <= old.Count && used.Add(n) ? old[n - 1] : new PipeModelNodeLoad(disp ? "displacement" : "force") { Source = "workbook" };
            int node = 0;
            var vals = new double[9][];
            for (int v = 0; v < 9; v++) { vals[v] = new double[6]; for (int k = 0; k < 6; k++) vals[v][k] = disp ? double.NaN : 0; }
            bool bad = false;
            foreach (int r in g.Value)
            {
                int nd = Int(c, r, "Node", x, 0);
                if (nd <= 0) { x.Err(s.Name + " row " + (r + 1) + ": a node is required"); bad = true; continue; }
                if (node != 0 && nd != node) { x.Err(s.Name + " row " + (r + 1) + ": one Id, two nodes (" + node + ", " + nd + ")"); bad = true; continue; }
                node = nd;
                int vec = Int(c, r, "Vector", x, 1);
                if (vec < 1 || vec > 9) { x.Err(s.Name + " row " + (r + 1) + ": vector 1 .. 9"); bad = true; continue; }
                for (int k = 0; k < 6; k++)
                {
                    string unit = k < 3 ? (disp ? u.Len : u.Force) : (disp ? "deg" : u.MomIn);
                    double v = Num(c, r, dofs[k], x, disp && k < 3 ? 0 : -1, unit);
                    if (!double.IsNaN(v)) vals[vec - 1][k] = v;
                }
            }
            if (bad) continue;
            if (node != d.Node) { d.Node = node; d.OwnerUid = 0; d.Slot = -1; }
            for (int v = 0; v < 9; v++)
                for (int k = 0; k < 6; k++)
                {
                    double nv = vals[v][k], ov = d.Vectors[v][k];
                    bool same = disp ? (double.IsNaN(nv) && double.IsNaN(ov)) || Same(nv, ov) : Same(Z(nv), Z(ov));
                    if (!same) d.Vectors[v][k] = nv;
                }
            m.NodeLoads.Add(d);
        }
    }

    // the elements a Uniform / Wind row names: an element Id, or a range of Ids in the Elements sheet's order
    // ("E3:E9", new rows between included); else From and To: one element, or the chain of consecutive elements
    // from the one starting at From to the one ending at To
    static List<PipeModelElement> ElementsOf(Cols c, int r, Ctx x, Dictionary<string, PipeModelElement> ids, string w)
    {
        var els = x.M.Elements;
        string id = Text(c, r, "Element", x);
        if (id.Length > 0)
        {
            string[] ends = id.Split(':').Select(q => q.Trim()).ToArray();
            if (ends.Length > 2) { x.Err(w + ": element \"" + id + "\" (an Id, or a range like E3:E9)"); return null; }
            var at = new List<int>();
            foreach (string q in ends)
            {
                PipeModelElement e;
                if (!ids.TryGetValue(q, out e)) { x.Err(w + ": element \"" + q + "\" is not on the Elements sheet"); return null; }
                at.Add(els.IndexOf(e));
            }
            if (at[at.Count - 1] < at[0]) { x.Err(w + ": " + id + " runs backwards (" + ends[1] + " comes before " + ends[0] + " on the Elements sheet)"); return null; }
            return els.GetRange(at[0], at[at.Count - 1] - at[0] + 1);
        }
        int from = Int(c, r, "From", x, 0), to = Int(c, r, "To", x, 0);
        for (int i = 0; i < els.Count; i++)
        {
            if (els[i].From != from) continue;
            for (int j = i; j < els.Count; j++)
            {
                if (els[j].To == to) return els.GetRange(i, j - i + 1);
                if (j + 1 >= els.Count || els[j + 1].From != els[j].To) break;
            }
        }
        x.Err(w + ": no element " + from + "-" + to + ", nor a chain of elements from " + from + " to " + to + " (give Element, or From and To)");
        return null;
    }

    // consecutive elements, each starting where the one before ends, with the same uniform load / wind: one
    // row each on the sheet
    static List<List<PipeModelElement>> SameRuns(PipeModel m, Func<PipeModelElement, bool> has, Func<PipeModelElement, PipeModelElement, bool> same)
    {
        var runs = new List<List<PipeModelElement>>();
        List<PipeModelElement> cur = null;
        foreach (var e in m.Elements)
        {
            if (!has(e)) { cur = null; continue; }
            var last = cur == null ? null : cur[cur.Count - 1];
            if (last != null && last.To == e.From && same(last, e)) cur.Add(e);
            else { cur = new List<PipeModelElement> { e }; runs.Add(cur); }
        }
        return runs;
    }

    static string RangeId(List<PipeModelElement> g) { return "E" + g[0].Uid + (g.Count > 1 ? ":E" + g[g.Count - 1].Uid : ""); }

    static void ReadUniform(CaesarSheet s, Ctx x, Dictionary<string, PipeModelElement> ids)
    {
        var m = x.M;
        var keep = new Dictionary<PipeModelElement, PipeModelUniform>();
        foreach (var e in m.Elements) { if (e.Uniform != null) keep[e] = e.Uniform; e.Uniform = null; }
        if (s == null) return;
        var c = ColsOf(s, x, new[] { "Element", "From", "To", "Vector", "UX", "UY", "UZ", "In g's" });
        var built = new Dictionary<PipeModelElement, PipeModelUniform>();
        var rowOf = new Dictionary<PipeModelElement, int[]>();     // the row that gave each vector (1-based)
        for (int r = 1; r < s.Rows.Count; r++)
        {
            if (Blank(s, r)) continue;
            string w = "Uniform row " + (r + 1);
            var es = ElementsOf(c, r, x, ids, w);
            if (es == null) continue;
            int vec = Int(c, r, "Vector", x, 1);
            if (vec < 1 || vec > 9) { x.Err(w + ": vector 1 .. 9"); continue; }
            string gt = Text(c, r, "In g's", x).ToLowerInvariant();
            bool g = gt == "yes" || gt == "y" || gt == "true" || gt == "g" || gt == "1";
            if (!(g || gt.Length == 0 || gt == "no" || gt == "n" || gt == "false" || gt == "0")) { x.Err(w + ": In g's = yes or no (\"" + gt + "\")"); continue; }
            var val = new double[3];
            for (int k = 0; k < 3; k++) val[k] = Z(Num(c, r, new[] { "UX", "UY", "UZ" }[k], x, -1, null));
            int[] rows;
            var twice = es.FirstOrDefault(e => rowOf.TryGetValue(e, out rows) && rows[vec - 1] > 0);
            if (twice != null) { x.Err(w + ": vector " + vec + " on element " + twice.From + "-" + twice.To + " again (row " + rowOf[twice][vec - 1] + " gives it)"); continue; }
            foreach (var e in es)
            {
                if (!rowOf.TryGetValue(e, out rows)) { rows = new int[9]; rowOf[e] = rows; }
                rows[vec - 1] = r + 1;
                PipeModelUniform u;
                if (!built.TryGetValue(e, out u)) { u = new PipeModelUniform(); built[e] = u; }
                Array.Copy(val, u.Vectors[vec - 1], 3);
                u.G[vec - 1] = g;
            }
        }
        foreach (var kv in built)
        {
            PipeModelUniform o;
            if (keep.TryGetValue(kv.Key, out o) && SameUniform(o, kv.Value)) { kv.Key.Uniform = o; continue; }   // unchanged: its text as written
            if (o != null)
            {
                kv.Value.Seq = o.Seq; kv.Value.Raw = o.Raw;
                for (int v = 0; v < 9; v++) if (!kv.Value.Used(v)) kv.Value.G[v] = o.G[v];   // a g flag on an unused vector, as written
            }
            kv.Key.Uniform = kv.Value;
        }
    }

    static void ReadWind(CaesarSheet s, Ctx x, Dictionary<string, PipeModelElement> ids)
    {
        var m = x.M;
        var keep = new Dictionary<PipeModelElement, PipeModelWind>();
        foreach (var e in m.Elements) { if (e.Wind != null) keep[e] = e.Wind; e.Wind = null; }
        if (s == null) return;
        var c = ColsOf(s, x, new[] { "Element", "From", "To", "Type", "Shape factor", "Added mass", "Lift", "Marine growth", "Growth density" });
        for (int r = 1; r < s.Rows.Count; r++)
        {
            if (Blank(s, r)) continue;
            string w = "Wind row " + (r + 1);
            var es = ElementsOf(c, r, x, ids, w);
            if (es == null) continue;
            var twice = es.FirstOrDefault(e => e.Wind != null);
            if (twice != null) { x.Err(w + ": a second wind entry on element " + twice.From + "-" + twice.To); continue; }
            string tt = Text(c, r, "Type", x);
            int type = tt.Length == 0 ? 0 : Array.FindIndex(WindTypes, t => t.Equals(tt, StringComparison.OrdinalIgnoreCase));
            if (type < 0 && !int.TryParse(tt, NumberStyles.Integer, CultureInfo.InvariantCulture, out type)) { x.Err(w + ": type \"" + tt + "\" (wind, wave or off)"); continue; }
            double shape = Z(Num(c, r, "Shape factor", x, -1, null)), mass = Z(Num(c, r, "Added mass", x, -1, null)), lift = Z(Num(c, r, "Lift", x, -1, null));
            double growth = Z(Num(c, r, "Marine growth", x, -1, null)), density = Z(Num(c, r, "Growth density", x, -1, null));
            foreach (var e in es)
            {
                var nw = new PipeModelWind { Type = type, Shape = shape, AddedMass = mass, Lift = lift, MarineGrowth = growth, MarineGrowthDensity = density };
                PipeModelWind o;
                if (keep.TryGetValue(e, out o) && SameWind(o, nw)) { e.Wind = o; continue; }
                if (o != null) { nw.Seq = o.Seq; nw.Raw = o.Raw; }
                e.Wind = nw;
            }
        }
    }

    static bool SameUniform(PipeModelUniform a, PipeModelUniform b)
    {
        for (int v = 0; v < 9; v++)
        {
            if (a.G[v] != b.G[v] && (a.Used(v) || b.Used(v))) return false;   // a g flag on an unused vector means nothing
            for (int k = 0; k < 3; k++) if (!Same(a.Vectors[v][k], b.Vectors[v][k])) return false;
        }
        return true;
    }

    static bool SameWind(PipeModelWind a, PipeModelWind b)
    {
        return a.Type == b.Type && Same(a.Shape, b.Shape) && Same(a.AddedMass, b.AddedMass) && Same(a.Lift, b.Lift) && Same(a.MarineGrowth, b.MarineGrowth) && Same(a.MarineGrowthDensity, b.MarineGrowthDensity);
    }

    // ---- Coords

    static void ReadCoords(CaesarSheet s, Ctx x)
    {
        var m = x.M;
        if (s == null) return;
        var u = x.U;
        var c = ColsOf(s, x, new[] { "Node", "X", "Y", "Z" });
        var old = new Dictionary<int, PipeModelCoord>();
        foreach (var q in m.Coords) if (!old.ContainsKey(q.Node)) old[q.Node] = q;
        var list = new List<PipeModelCoord>();
        for (int r = 1; r < s.Rows.Count; r++)
        {
            if (Blank(s, r)) continue;
            string w = "Coords row " + (r + 1);
            int node = Int(c, r, "Node", x, 0);
            if (node <= 0) { x.Err(w + ": a node is required"); continue; }
            PipeModelCoord q;
            if (old.TryGetValue(node, out q)) old.Remove(node); else q = new PipeModelCoord { Node = node };
            SetN(ref q.X, Z(Num(c, r, "X", x, 0, u.Len))); SetN(ref q.Y, Z(Num(c, r, "Y", x, 0, u.Len))); SetN(ref q.Z, Z(Num(c, r, "Z", x, 0, u.Len)));
            list.Add(q);
        }
        m.Coords.Clear();
        m.Coords.AddRange(list);
    }

    // ======================================================================== the new-workbook template

    // a small English model to start from: two legs with a bend, an anchor at each end and a rest
    public static PipeModel Template()
    {
        var m = new PipeModel { Title = "New line", Notes = "started with Pluto Build-Pipe.ps1 -NewWorkbook" };
        Func<int, int, double, double, double, PipeModelElement> el = (f, t, dx, dy, dz) =>
        {
            var e = new PipeModelElement { From = f, To = t, Dx = dx, Dy = dy, Dz = dz, Od = 6.625, Wall = 0.28, Material = 102 };
            e.T[0] = 200; e.P[0] = 100; e.FluidDensity = 62.4 / 1728.0;
            m.Elements.Add(e);
            return e;
        };
        var a = el(10, 20, 120, 0, 0);
        el(20, 30, 120, 0, 0);
        var b = el(30, 40, 60, 0, 0);
        b.Bend = new PipeModelBend { Radius = 9, FittingThickness = 0.28 };
        b.Bend.AngleNodes.Add(new[] { PipeModelBend.MidPoint, 39.0 });
        b.Kind = "bend";
        el(40, 50, 0, 0, 120);
        el(50, 60, 0, 0, 120);
        a.LineNumber = "LINE-1";
        m.Restraints.Add(new PipeModelRestraint { Node = 10, Type = "ANC", Code = 1, Source = "template" });
        m.Restraints.Add(new PipeModelRestraint { Node = 30, Type = "+Y", Code = 14, Mu = 0.3, Source = "template" });
        m.Restraints.Add(new PipeModelRestraint { Node = 60, Type = "ANC", Code = 1, Source = "template" });
        m.Coords.Add(new PipeModelCoord { Node = 10 });
        m.AssignUids();
        return m;
    }

    // ======================================================================== helpers

    // a fingerprint of a model: SHA-256 of its exact JSON
    public static string Hash(PipeModel m) { return Sha(PipeModelJson.Write(m)); }

    static string Sha(string text)
    {
        using (var h = SHA256.Create())
            return string.Concat(h.ComputeHash(new UTF8Encoding(false).GetBytes(text)).Select(b => b.ToString("x2")).ToArray());
    }

    // a value the sheet gives: the field keeps its exact value when the cell holds the same one
    static void SetN(ref double field, double v) { if (!Same(field, v)) field = v; }

    static bool Same(double a, double b)
    {
        if (a == b) return true;
        if (double.IsNaN(a) || double.IsNaN(b)) return false;
        return Math.Abs(a - b) <= 1e-9 * Math.Max(Math.Abs(a), Math.Abs(b)) + 1e-15;
    }

    static bool SameCos(double[] a, double[] b) { return a != null && b != null && Same(a[0], b[0]) && Same(a[1], b[1]) && Same(a[2], b[2]); }

    static bool IsRigid(PipeModel m, PipeModelRestraint r)
    {
        double kf = m.Factor(PipeModelCodes.IsRotationCode(r.Code) ? 14 : 13);
        return r.Stiffness <= 0 || r.Stiffness >= 1e11 * kf;
    }

    static double Z(double v) { return double.IsNaN(v) ? 0 : v; }
    static string R(double v) { return v.ToString("R", CultureInfo.InvariantCulture); }
    static string F(double v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }

    internal static string Q(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in s ?? "")
        {
            if (c == '"' || c == '\\') sb.Append('\\').Append(c);
            else if (c < 0x20 || c > 0x7e) sb.Append("\\u").Append(((int)c).ToString("x4"));
            else sb.Append(c);
        }
        return sb.Append('"').ToString();
    }
}
