using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

// CaesarNeutralReader  --  CAESAR II neutral file (.cii) -> CaesarModel, for the pipestress arm.
// CaesarGeometry (CaesarGeometry.cs) turns the model into a pipe graph. C# 5 / Add-Type (PowerShell 5.1)
// compatible. Every type is prefixed Caesar: scripts/lib + scripts/arms compile as ONE global batch that
// already holds Node, Element, Group, Segment, Debug, ...
//
// The file is read by STRUCTURE, never by a per-version table. Block sizes change between CAESAR
// versions (15.01 writes 28-line ALLOWBLS, 14-line SIF&TEES and 2-line REDUCERS blocks where 11.00
// writes 26 / 10 / 1), so:
//   * a section starts at a "#$ NAME" line (trailing blanks allowed); every other line, blank ones
//     included, is data of the current section;
//   * numbers sit in 13-character columns after a 2-character indent and may touch
//     ("3.444566E+02-3.178331E+00"); integers are written as reals or as I13 and are rounded;
//     a line that does not split by columns falls back to a sign-aware number scan (counted);
//   * text rows (names, line numbers, tags, GUIDs) are a 12-character length field, a space, the text;
//   * lines per block = section line count / CONTROL count, checked (a remainder is a warning);
//   * an ELEMENTS record = its run of real rows (FROM TO DX DY DZ OD / WALL INSUL CORR T1 T2 T3 / ...),
//     then text and integer rows (element name, line number, "-1 -1"), then the LAST 3 rows: pointers
//     BEND RIGID EXPJT RESTRANT DISPLMNT FORCMNT / UNIFORM WIND OFFSETS ALLOWBLS SIF&TEES NODENAME /
//     REDUCERS FLANGES EQUIPMNT, each a 1-based block index into its section (0 = none);
//   * RESTRANT blocks hold 6 slots (NODE TYPE K GAP MU CNODE / cosines / tag / GUID; the slot count is
//     found structurally), DISPLMNT and SIF&TEES blocks hold 2 slots; a slot with node 0 is unused.
// A layout that does not fit gives a warning and a best-effort parse, never an exception
// (only a missing or unreadable file throws).
//
// Units: values are in the job's units (UNITS section: 22 factors English -> job, then labels).
// Deltas and bend radii are in LENGTH units; OD / wall in DIAMETER / WALL units (CaesarUnits.Convert);
// COORDS are in COMPOUND-LENGTH units and are converted to length units here (Coords; CoordsRaw keeps
// the values as written).
//
// Usage from PowerShell 5.1 (after dot-sourcing scripts/lib/Config.ps1):
//   $m = [CaesarNeutralReader]::Read('<project dir>\job.cii')
//   $m.Summary(); $m.Warnings
//   $g = [CaesarGeometry]::Build($m, 7.5)

public class CaesarUnits
{
    // factor / label index (0-based), in CAESAR's UNITS order
    public const int Length = 0, Force = 1, Mass = 2, MomentInput = 3, MomentOutput = 4, Stress = 5,
        TempScale = 6, TempOffset = 7, Pressure = 8, Modulus = 9, PipeDensity = 10, InsulationDensity = 11,
        FluidDensity = 12, TransStiffness = 13, RotStiffness = 14, UniformLoad = 15, GLoad = 16,
        WindPressure = 17, Elevation = 18, CompoundLength = 19, Diameter = 20, Wall = 21;
    public const int FactorCount = 22;

    public double[] Factors;        // English -> job units, FactorCount long (NaN where the file has none)
    public List<string> Labels;     // every label line after the factors, trimmed (blank lines kept as "")
    public int LabelOffset;         // Labels[LabelOffset + i] is the label of factor i (2: units-file name, on/off flag)
    public string UnitsFileName;    // first label line ("" when blank)
    public string LengthLabel;      // label of factor 0 as written, e.g. "in." or "mm."
    public string LengthUnit;       // normalized: m | mm | cm | in | ft | "" (unknown)

    public CaesarUnits()
    {
        Factors = new double[FactorCount];
        for (int i = 0; i < FactorCount; i++) Factors[i] = double.NaN;
        Labels = new List<string>();
        LabelOffset = 2;
        UnitsFileName = "";
        LengthLabel = "";
        LengthUnit = "";
    }

    public double Factor(int i)
    {
        return (Factors != null && i >= 0 && i < Factors.Length) ? Factors[i] : double.NaN;
    }

    public string Label(int i)
    {
        int k = LabelOffset + i;
        return (Labels != null && i >= 0 && k >= 0 && k < Labels.Count) ? Labels[k] : "";
    }

    // value in the units of factor `from` -> units of factor `to` (both factors English -> job).
    // Returns the value unchanged when either factor is missing or zero.
    public double Convert(double value, int from, int to)
    {
        double a = Factor(from), b = Factor(to);
        if (from == to || !IsUsable(a) || !IsUsable(b)) return value;
        return value / a * b;
    }

    public double ToLength(double value, int from) { return Convert(value, from, Length); }

    // one inch in job length units (1 for inches, 25.4 for mm); 1 when the factor is missing
    public double InchInLength { get { double f = Factor(Length); return IsUsable(f) ? Math.Abs(f) : 1.0; } }

    static bool IsUsable(double f) { return !double.IsNaN(f) && !double.IsInfinity(f) && Math.Abs(f) > 1e-30; }

    // "in." / "mm." / "m" / " ft." ... -> in | mm | m | cm | ft; the factor (English inch -> job length) decides
    // when it names a known unit, the label otherwise; "" when neither is known.
    public static string NormalizeLength(string label, double factor)
    {
        string byFactor = "";
        if (IsUsable(factor))
        {
            if (Math.Abs(factor - 1.0) < 1e-4) byFactor = "in";
            else if (Math.Abs(factor - 25.4) < 1e-3) byFactor = "mm";
            else if (Math.Abs(factor - 2.54) < 1e-4) byFactor = "cm";
            else if (Math.Abs(factor - 0.0254) < 1e-6) byFactor = "m";
            else if (Math.Abs(factor - 1.0 / 12.0) < 1e-5) byFactor = "ft";
        }
        string t = (label ?? "").Trim().ToLowerInvariant().TrimEnd('.').Trim();
        string byLabel = "";
        if (t == "in" || t == "inch" || t == "inches") byLabel = "in";
        else if (t == "mm") byLabel = "mm";
        else if (t == "cm") byLabel = "cm";
        else if (t == "m" || t == "meter" || t == "metre") byLabel = "m";
        else if (t == "ft" || t == "feet" || t == "foot") byLabel = "ft";
        return byFactor.Length > 0 ? byFactor : byLabel;
    }
}

public class CaesarElement
{
    public int Index;                    // 0-based position in ELEMENTS (CAESAR element number = Index + 1)
    public int From, To;
    public double Dx, Dy, Dz;            // job LENGTH units
    public double Od, Wall, Insulation, Corrosion;   // as written: OD in DIAMETER units, the rest in WALL units
    public double[][] RealRows;          // the real rows as read (RealRows[row][value])
    public string Name = "";             // element name text row ("" when blank)
    public string LineNumber = "";       // line-number text, carried forward from the last element that has one
    public string LineNumberRaw = "";    // line-number text as written on this element ("" when blank)
    public List<int[]> ExtraIntRows = new List<int[]>();   // integer rows between the text rows and the pointers ("-1 -1")
    // pointers: 1-based block index into the section, 0 = none (out-of-range values are reset to 0 with a warning)
    public int Bend, Rigid, ExpJt, Restraint, Displmnt, Forcmnt;
    public int Uniform, Wind, Offsets, Allowbls, SifTee, NodeName;
    public int Reducer, Flange, Equipmnt;
    public int Material;                 // MISCEL_1 material number (0 when absent)
    public string FromName = "", ToName = "";   // NODENAME entry of this element ("" when none)

    public double Length { get { return Math.Sqrt(Dx * Dx + Dy * Dy + Dz * Dz); } }

    // value c of real row r; NaN when the file has fewer
    public double Real(int r, int c)
    {
        if (RealRows == null || r < 0 || r >= RealRows.Length || RealRows[r] == null || c < 0 || c >= RealRows[r].Length) return double.NaN;
        return RealRows[r][c];
    }
}

public class CaesarBend
{
    public const double MidCode = -2.0202;   // angle code "M": the bend node sits at half the bend angle

    public int Block;                        // 1-based block index in BEND
    public double Radius;                    // job LENGTH units (resolved by CAESAR)
    public int TypeCode;                     // raw (0 = blank)
    public List<double[]> AngleNodes = new List<double[]>();   // {angle code as written, node} for slots with node > 0
    public int MiterPoints;
    public double FittingThickness;          // line 2 value 4, raw (the bend wall in the 15.01 fixture)
    public double KFactor;                   // line 2 value 5, raw
    public double[] Raw;                     // every value of the block, flattened
    public int ElementIndex = -1;            // element whose BEND pointer names this block

    public static bool IsMid(double code) { return Math.Abs(code - MidCode) < 1e-3; }
}

public class CaesarRigid
{
    public int Block;
    public double Weight;                    // job FORCE units
    public int TypeCode;                     // 0 Unspecified, 1 Valve, 2 Flange, 3 Flange Pair, 4 Flange Valve (15.01)
    public string TypeName = "";
    public double[] Raw;
    public int ElementIndex = -1;

    public static string NameOf(int code)
    {
        switch (code)
        {
            case 0: return "Unspecified";
            case 1: return "Valve";
            case 2: return "Flange";
            case 3: return "Flange Pair";
            case 4: return "Flange Valve";
        }
        return "Type " + code.ToString(CultureInfo.InvariantCulture);
    }
}

public class CaesarExpJoint
{
    public int Block;
    public double[] Raw;                     // axial K, transverse K, bending K, torsional K, effective ID (15.01 echo)
    public int ElementIndex = -1;

    public double AxialStiffness { get { return At(0); } }
    public double TransverseStiffness { get { return At(1); } }
    public double BendingStiffness { get { return At(2); } }
    public double TorsionalStiffness { get { return At(3); } }
    public double EffectiveId { get { return At(4); } }
    double At(int i) { return (Raw != null && i < Raw.Length) ? Raw[i] : double.NaN; }
}

public class CaesarRestraint
{
    public int Block;                        // 1-based RESTRANT block
    public int Slot;                         // 0-based slot inside the block
    public int Node;
    public int TypeCode;                     // neutral-file code (NOT the XML numbering)
    public string TypeName = "";             // ANC, X, Y, Z, RX, RY, RZ, GUI, LIM, +Y, +Z, or OTHER(code)
    public bool Confirmed;                   // TypeName comes from a confirmed code
    public double Stiffness;                 // as written (rigid default ~1e12 English, scaled by the units)
    public bool IsRigid;                     // K >= 1e11 x the stiffness factor, or K <= 0 (blank)
    public bool IsRotational;                // RX / RY / RZ
    public double Gap;                       // length units (degrees for rotational types)
    public double Friction;
    public int CNode;                        // connecting node, 0 = none
    public double[] Cosines = new double[3]; // as written; GUI / LIM cosines are dummies (take the pipe axis)
    public string Tag = "", Guid = "";
    public double[] Raw;                     // numeric values of the slot, flattened
    public int ElementIndex = -1;            // element whose RESTRANT pointer names the block

    // codes confirmed by CAESAR-written files and the 15.01 fixture's input echo
    public static string ConfirmedName(int code)
    {
        switch (code)
        {
            case 1: return "ANC";
            case 2: return "X";
            case 3: return "Y";
            case 4: return "Z";
            case 5: return "RX";
            case 6: return "RY";
            case 7: return "RZ";
            case 8: return "GUI";
            case 9: return "LIM";
            case 14: return "+Y";
            case 15: return "+Z";
        }
        return null;
    }

    // third-party table, NOT confirmed: only quoted in warnings so the user can check a restraint report
    public static string Guess(int code)
    {
        string[] g = { null, null, null, null, null, null, null, null, null, null,
            "XSNB", "YSNB", "ZSNB", "+X", null, null, "-X", "-Y", "-Z", "+RX", "+RY", "+RZ", "-RX", "-RY", "-RZ",
            "+LIM", "-LIM", "XROD", "YROD", "ZROD", "+XROD", "+YROD", "+ZROD", "-XROD", "-YROD", "-ZROD",
            "X2", "Y2", "Z2", "RX2", "RY2", "RZ2", "+X2", "+Y2", "+Z2", "-X2", "-Y2", "-Z2",
            "+RX2", "+RY2", "+RZ2", "-RX2", "-RY2", "-RZ2", "XSPR", "YSPR", "ZSPR",
            "+XSNB", "+YSNB", "+ZSNB", "-XSNB", "-YSNB", "-ZSNB" };
        return (code >= 0 && code < g.Length) ? g[code] : null;
    }
}

public class CaesarReducer
{
    public int Block;
    public double Od2, Wall2;                // second-end OD / wall, DIAMETER / WALL units as written
    public double Alpha, R1, R2;             // raw
    public double[] Raw;
    public int ElementIndex = -1;
}

public class CaesarSifTee
{
    public int Block, Slot;
    public int Node;
    public int TypeCode;                     // 0 = no type (user SIFs), 3 = welding tee (15.01 echo); others raw
    public string TypeName = "";
    public double SifIn, SifOut;
    public int SurfaceNode;                  // tee surface node ("Srf.Node", line 5 value 3 in 15.01); 0 = none. Best effort.
    public double[] Raw;
    public int ElementIndex = -1;

    public static string NameOf(int code)
    {
        if (code == 0) return "None";
        if (code == 3) return "Welding Tee";
        return "Type " + code.ToString(CultureInfo.InvariantCulture);
    }
}

public class CaesarDisplacement
{
    public const double Free = 9999.99;      // "not specified" sentinel

    public int Block, Slot;
    public int Node;
    public bool[] Fixed = new bool[6];       // DX DY DZ RX RY RZ: specified in any vector
    public double[][] Vectors;               // [vector][dof], NaN where free; 9 vectors in 11.00
    public double[] Raw;                     // numeric values of the slot, flattened (node line first)
    public int ElementIndex = -1;

    public bool AllFixed { get { for (int i = 0; i < 6; i++) if (!Fixed[i]) return false; return true; } }
    public int FixedCount { get { int n = 0; for (int i = 0; i < 6; i++) if (Fixed[i]) n++; return n; } }
}

// Spring hanger from MISCEL_1 (best effort: its layout comes from 11.00 files; the fixture has none).
public class CaesarHanger
{
    public int Index;                        // position in the hanger node list (not element order)
    public int Node;
    public int CNode;                        // connecting node (packed row 4), 0 = none
    public double[] Values;                  // the 2 data lines, flattened (11 values in 11.00)
    public string Tag = "";
    public int FreeAnchor1, FreeAnchor2, FreeDof1, FreeDof2;
    public int NumHangers, Table, ShortRange;   // packed rows 1-3, raw

    public double LoadVariation { get { return At(1); } }    // %
    public double AvailableSpace { get { return At(3); } }
    public double ColdLoad { get { return At(4); } }
    public double HotLoad { get { return At(5); } }
    double At(int i) { return (Values != null && i < Values.Length) ? Values[i] : double.NaN; }
}

public class CaesarNozzle
{
    public int Index;
    public int Node, VesselNode;             // the vessel node has no element geometry
    public double[] Values;
}

public class CaesarModel
{
    public string SourcePath = "";
    public string VersionLine = "";          // the numeric VERSION line, blanks collapsed ("5.00000 15.0100 1252")
    public double FileFormat = double.NaN;   // first VERSION value (5.0)
    public double Version = double.NaN;      // CAESAR version (15.01)
    public int CodePage;                     // Windows code page the text was decoded with (VERSION value 3)
    public int[] Control = new int[0];       // CONTROL values flattened, as read
    public int NumElements, NumNozzles, NumHangers, NumNodeNames, NumReducers, NumFlanges;
    public int NumBends, NumRigids, NumExpJoints, NumRestraintBlocks, NumDisplBlocks, NumForceBlocks;
    public int NumUniform, NumWind, NumOffsets, NumAllowables, NumSifTees, NumEquipment;
    public bool ZUp;                         // CONTROL IZUP (row 3 value 6): 1 = Z up, 0 = Y up
    public CaesarUnits Units = new CaesarUnits();
    public List<CaesarElement> Elements = new List<CaesarElement>();
    public List<CaesarBend> Bends = new List<CaesarBend>();             // index = block - 1
    public List<CaesarRigid> Rigids = new List<CaesarRigid>();          // index = block - 1
    public List<CaesarExpJoint> ExpJoints = new List<CaesarExpJoint>(); // index = block - 1
    public List<CaesarReducer> Reducers = new List<CaesarReducer>();    // index = block - 1
    public List<CaesarRestraint> Restraints = new List<CaesarRestraint>();       // used slots, file order
    public List<CaesarSifTee> SifTees = new List<CaesarSifTee>();                // used slots, file order
    public List<CaesarDisplacement> Displacements = new List<CaesarDisplacement>(); // used slots, file order
    public List<CaesarHanger> Hangers = new List<CaesarHanger>();
    public List<CaesarNozzle> Nozzles = new List<CaesarNozzle>();
    public Dictionary<int, string[]> NodeNames = new Dictionary<int, string[]>();   // NODENAME entry (1-based) -> {from name, to name}
    public Dictionary<int, string> NodeNameByNode = new Dictionary<int, string>();  // node -> name, through the element pointers
    public List<double[]> Coords = new List<double[]>();     // {node, x, y, z} in job LENGTH units
    public List<double[]> CoordsRaw = new List<double[]>();  // {node, x, y, z} as written (compound-length units)
    public Dictionary<string, int> SectionLines = new Dictionary<string, int>();   // section -> data line count
    public Dictionary<string, int> LinesPerBlock = new Dictionary<string, int>();  // section -> lines per block / element / slot
    public int NumberFallbacks;              // lines that did not split by 13-character columns
    public List<string> Warnings = new List<string>();

    public string Summary()
    {
        var ci = CultureInfo.InvariantCulture;
        return string.Format(ci,
            "CAESAR II {0} (VERSION \"{1}\", code page {2}), {3} up, length {4}: {5} elements, {6} bends, {7} rigids, " +
            "{8} expansion joints, {9} reducers, {10} restraints, {11} imposed displacements, {12} hangers, {13} SIF/tee slots, " +
            "{14} COORDS, {15} warnings",
            Version, VersionLine, CodePage, ZUp ? "Z" : "Y", Units.LengthUnit.Length > 0 ? Units.LengthUnit : "?",
            Elements.Count, Bends.Count, Rigids.Count, ExpJoints.Count, Reducers.Count, Restraints.Count,
            Displacements.Count, Hangers.Count, SifTees.Count, Coords.Count, Warnings.Count);
    }
}

public static class CaesarNeutralReader
{
    public static CaesarModel Read(string path)
    {
        if (path == null) throw new ArgumentNullException("path");
        byte[] bytes;
        // FileShare.ReadWrite: still reads when another program holds the file open
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var ms = new MemoryStream())
        {
            fs.CopyTo(ms);
            bytes = ms.ToArray();
        }
        CaesarModel m = Parse(bytes);
        m.SourcePath = path;
        return m;
    }

    // bytes of a .cii; the text is decoded with the code page named on the VERSION line (Latin-1 fallback)
    public static CaesarModel Parse(byte[] bytes)
    {
        if (bytes == null) throw new ArgumentNullException("bytes");
        int start = 0;
        Encoding bomEnc = null;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) { start = 3; bomEnc = new UTF8Encoding(false); }
        Encoding latin1 = Encoding.GetEncoding(28591);
        string text = (bomEnc ?? latin1).GetString(bytes, start, bytes.Length - start);
        int cp = bomEnc != null ? 65001 : PeekCodePage(text);
        Encoding enc = null;
        if (bomEnc == null && cp > 0 && cp != 28591)
        {
            try { enc = Encoding.GetEncoding(cp); }
            catch (Exception) { enc = null; }
            if (enc != null) text = enc.GetString(bytes, start, bytes.Length - start);
        }
        var p = new Ctx();
        p.M.CodePage = bomEnc != null ? 65001 : (enc != null ? cp : 28591);
        p.Run(SplitLines(text));
        if (bomEnc == null && cp > 0 && cp != 28591 && enc == null)
            p.M.Warnings.Insert(0, "code page " + cp.ToString(CultureInfo.InvariantCulture) + " is not available here; text was read as Latin-1");
        return p.M;
    }

    // already-decoded text (tests, or a caller that decoded it)
    public static CaesarModel ParseText(string text)
    {
        var p = new Ctx();
        p.Run(SplitLines(text ?? ""));
        return p.M;
    }

    // CRLF, LF or CR; a final newline does not make an extra line; blank lines are kept. A run of CRs
    // ending in LF (CR CR LF: a line-ending conversion done twice) is ONE line break.
    public static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        int s = 0, n = text.Length;
        for (int i = 0; i < n; i++)
        {
            char c = text[i];
            if (c == '\n' || c == '\r')
            {
                lines.Add(text.Substring(s, i - s));
                if (c == '\r')
                {
                    int j = i + 1;
                    while (j < n && text[j] == '\r') j++;
                    if (j < n && text[j] == '\n') i = j;
                }
                s = i + 1;
            }
        }
        if (s < n) lines.Add(text.Substring(s));
        return lines;
    }

    static int PeekCodePage(string text)
    {
        var lines = SplitLines(text.Length > 4096 ? text.Substring(0, 4096) : text);
        for (int i = 0; i + 1 < lines.Count; i++)
        {
            if (!lines[i].StartsWith("#$", StringComparison.Ordinal)) continue;
            if (lines[i].Substring(2).Trim().ToUpperInvariant() != "VERSION") continue;
            int fb = 0;
            double[] v = Nums(lines[i + 1], ref fb);
            if (v.Length >= 3 && v[2] > 0 && v[2] < 100000) return (int)Math.Round(v[2]);
            return 0;
        }
        return 0;
    }

    // ---------------------------------------------------------------- line level

    // numbers of a numeric line: 2-character indent, then 13-character columns (numbers may touch).
    // A line that does not split that way is scanned for sign-aware numbers instead (fallbacks++).
    public static double[] Nums(string line, ref int fallbacks)
    {
        bool anyReal;
        return Fields(line, ref fallbacks, out anyReal);
    }

    static double[] Fields(string line, ref int fallbacks, out bool anyReal)
    {
        anyReal = false;
        if (line == null || line.Trim().Length == 0) return new double[0];
        var vals = new List<double>();
        bool ok = line.Length < 2 || line.Substring(0, 2).Trim().Length == 0;
        for (int pos = 2; ok && pos < line.Length; pos += 13)
        {
            string f = line.Substring(pos, Math.Min(13, line.Length - pos)).Trim();
            if (f.Length == 0) continue;
            double v;
            if (!TryNum(f, out v)) { ok = false; break; }
            if (f.IndexOfAny(RealMarks) >= 0) anyReal = true;
            vals.Add(v);
        }
        if (ok) return vals.ToArray();
        fallbacks++;
        vals.Clear();
        anyReal = false;
        int i = 0, n = line.Length;
        while (i < n)
        {
            int j = ScanNumber(line, i);
            if (j <= i) { i++; continue; }
            string tok = line.Substring(i, j - i);
            double v;
            if (TryNum(tok, out v))
            {
                vals.Add(v);
                if (tok.IndexOfAny(RealMarks) >= 0) anyReal = true;
            }
            i = j;
        }
        return vals.ToArray();
    }

    static readonly char[] RealMarks = { '.', 'E', 'e', 'D', 'd' };

    // end of a number [-+]?(digits[.digits]|.digits)([EeDd][-+]?digits)? starting at i (i when none)
    static int ScanNumber(string s, int i)
    {
        int n = s.Length, j = i;
        if (j < n && (s[j] == '-' || s[j] == '+')) j++;
        int d0 = j;
        while (j < n && IsDigit(s[j])) j++;
        int intDigits = j - d0;
        int fracDigits = 0;
        if (j < n && s[j] == '.')
        {
            int k = j + 1;
            while (k < n && IsDigit(s[k])) k++;
            fracDigits = k - (j + 1);
            if (intDigits > 0 || fracDigits > 0) j = k;
        }
        if (intDigits == 0 && fracDigits == 0) return i;
        if (j < n && (s[j] == 'E' || s[j] == 'e' || s[j] == 'D' || s[j] == 'd'))
        {
            int k = j + 1;
            if (k < n && (s[k] == '-' || s[k] == '+')) k++;
            int e0 = k;
            while (k < n && IsDigit(s[k])) k++;
            if (k > e0) j = k;
        }
        return j;
    }

    static bool IsDigit(char c) { return c >= '0' && c <= '9'; }

    static bool TryNum(string f, out double v)
    {
        if (f.IndexOf('D') >= 0 || f.IndexOf('d') >= 0) f = f.Replace('D', 'E').Replace('d', 'e');
        return double.TryParse(f, NumberStyles.Float, CultureInfo.InvariantCulture, out v)
            && !double.IsNaN(v) && !double.IsInfinity(v);
    }

    // text row: right-aligned length in columns 1-12, a blank, then the text ("          14 FLG.PR_FLG_150")
    public static bool TryText(string line, out string text)
    {
        text = null;
        if (line == null || line.Length < 12) return false;
        string head = line.Substring(0, 12);
        if (head[11] < '0' || head[11] > '9') return false;
        string t = head.TrimStart(' ');
        for (int i = 0; i < t.Length; i++) if (t[i] < '0' || t[i] > '9') return false;
        if (line.Length > 12 && line[12] != ' ') return false;
        int n;
        if (!int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out n)) return false;
        string rest = line.Length > 13 ? line.Substring(13) : "";
        if (n < rest.Length) rest = rest.Substring(0, n);
        text = rest.TrimEnd();
        return true;
    }

    static int Round(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) return 0;
        if (v > int.MaxValue) return int.MaxValue;
        if (v < int.MinValue) return int.MinValue;
        return (int)Math.Round(v, MidpointRounding.AwayFromZero);
    }

    static string F(double v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }
    static string I(int v) { return v.ToString(CultureInfo.InvariantCulture); }

    // ---------------------------------------------------------------- parser

    sealed class Row
    {
        public string Line;
        public bool Blank, Text, AnyReal;
        public string TextValue;
        public double[] V;
        public bool IsReal { get { return !Blank && !Text && AnyReal && V.Length > 0; } }
        public bool IsInt { get { return !Blank && !Text && !AnyReal && V.Length > 0; } }
        public double At(int i) { return i >= 0 && i < V.Length ? V[i] : double.NaN; }
        public int IntAt(int i) { return i >= 0 && i < V.Length ? Round(V[i]) : 0; }
    }

    sealed class Section
    {
        public string Name;
        public List<string> Lines = new List<string>();
    }

    sealed class Ctx
    {
        public CaesarModel M = new CaesarModel();
        readonly Dictionary<string, Section> _sec = new Dictionary<string, Section>();
        readonly Dictionary<string, List<string>> _warnKeys = new Dictionary<string, List<string>>();
        readonly List<string> _warnOrder = new List<string>();
        readonly Dictionary<string, string> _warnText = new Dictionary<string, string>();
        int _fallbacks;

        Row Classify(string line)
        {
            var r = new Row();
            r.Line = line ?? "";
            if (r.Line.Trim().Length == 0) { r.Blank = true; r.V = new double[0]; return r; }
            string t;
            if (TryText(r.Line, out t)) { r.Text = true; r.TextValue = t; r.V = new double[0]; return r; }
            bool anyReal;
            r.V = Fields(r.Line, ref _fallbacks, out anyReal);
            r.AnyReal = anyReal;
            return r;
        }

        List<Row> Rows(List<string> lines)
        {
            var rows = new List<Row>(lines.Count);
            foreach (var l in lines) rows.Add(Classify(l));
            return rows;
        }

        void Warn(string msg) { M.Warnings.Add(msg); }

        // aggregated warning: one line per key, with up to 8 examples and a count
        void WarnAgg(string key, string text, string example)
        {
            List<string> ex;
            if (!_warnKeys.TryGetValue(key, out ex))
            {
                ex = new List<string>();
                _warnKeys[key] = ex;
                _warnOrder.Add(key);
                _warnText[key] = text;
            }
            ex.Add(example);
        }

        void FlushAgg()
        {
            foreach (var key in _warnOrder)
            {
                var ex = _warnKeys[key];
                string list = string.Join(", ", ex.Take(8).ToArray()) + (ex.Count > 8 ? ", ..." : "");
                M.Warnings.Add(_warnText[key] + " (" + I(ex.Count) + "x: " + list + ")");
            }
        }

        List<string> SectionLines(string name)
        {
            Section s;
            return _sec.TryGetValue(name, out s) ? s.Lines : new List<string>();
        }

        bool HasSection(string name) { return _sec.ContainsKey(name); }

        public void Run(List<string> lines)
        {
            Section cur = null;
            int stray = 0;
            for (int i = 0; i < lines.Count; i++)
            {
                string l = lines[i];
                if (l.StartsWith("#$", StringComparison.Ordinal))
                {
                    string name = l.Substring(2).Trim().ToUpperInvariant();
                    if (_sec.ContainsKey(name)) { Warn("section #$ " + name + " appears twice; the second one is ignored"); cur = new Section(); cur.Name = name; continue; }
                    cur = new Section();
                    cur.Name = name;
                    _sec[name] = cur;
                    continue;
                }
                if (cur == null) { if (l.Trim().Length > 0) stray++; continue; }
                cur.Lines.Add(l);
            }
            if (stray > 0) Warn(I(stray) + " non-blank lines before the first #$ section header were ignored");
            if (!HasSection("VERSION") || !HasSection("CONTROL") || !HasSection("ELEMENTS"))
                Warn("not a CAESAR II neutral file? missing section(s):" + (HasSection("VERSION") ? "" : " VERSION") +
                     (HasSection("CONTROL") ? "" : " CONTROL") + (HasSection("ELEMENTS") ? "" : " ELEMENTS"));
            foreach (var kv in _sec) M.SectionLines[kv.Key] = kv.Value.Lines.Count;

            Guard("VERSION", ParseVersion);
            Guard("CONTROL", ParseControl);
            Guard("UNITS", ParseUnits);
            Guard("ELEMENTS", ParseElements);
            Guard("NODENAME", ParseNodeNames);
            Guard("BEND", ParseBends);
            Guard("RIGID", ParseRigids);
            Guard("EXPJT", ParseExpJoints);
            Guard("RESTRANT", ParseRestraints);
            Guard("DISPLMNT", ParseDisplacements);
            Guard("REDUCERS", ParseReducers);
            Guard("SIF&TEES", ParseSifTees);
            Guard("other sections", CheckOtherSections);
            Guard("MISCEL_1", ParseMiscel);
            Guard("COORDS", ParseCoords);
            Guard("cross-links", Link);
            M.NumberFallbacks = _fallbacks;
            if (_fallbacks > 0) Warn(I(_fallbacks) + " line(s) did not split by 13-character columns and were scanned for numbers instead");
            FlushAgg();
        }

        void Guard(string what, Action a)
        {
            try { a(); }
            catch (Exception ex) { Warn("reading " + what + " failed (" + ex.GetType().Name + ": " + ex.Message + "); the rest of the file was still read"); }
        }

        // ---------------- VERSION / CONTROL / UNITS

        void ParseVersion()
        {
            var lines = SectionLines("VERSION");
            if (lines.Count == 0) { Warn("VERSION section is empty"); return; }
            var r = Classify(lines[0]);
            M.VersionLine = string.Join(" ", lines[0].Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries));
            if (r.V.Length >= 2) { M.FileFormat = r.V[0]; M.Version = r.V[1]; }
            else Warn("VERSION: first line is not numeric (\"" + M.VersionLine + "\")");
            if (r.V.Length < 3) Warn("VERSION: no code page on the first line; text read as Latin-1");
        }

        void ParseControl()
        {
            var rows = Rows(SectionLines("CONTROL")).Where(x => !x.Blank).ToList();
            var all = new List<int>();
            foreach (var r in rows) foreach (var v in r.V) all.Add(Round(v));
            M.Control = all.ToArray();
            if (rows.Count < 3) { Warn("CONTROL has " + I(rows.Count) + " rows (4 expected); counts may be wrong"); }
            Row r1 = rows.Count > 0 ? rows[0] : null, r2 = rows.Count > 1 ? rows[1] : null, r3 = rows.Count > 2 ? rows[2] : null, r4 = rows.Count > 3 ? rows[3] : null;
            if (r1 != null)
            {
                M.NumElements = r1.IntAt(0); M.NumNozzles = r1.IntAt(1); M.NumHangers = r1.IntAt(2);
                M.NumNodeNames = r1.IntAt(3); M.NumReducers = r1.IntAt(4); M.NumFlanges = r1.IntAt(5);
                if (r1.V.Length < 6) Warn("CONTROL row 1 has " + I(r1.V.Length) + " values (6 expected; older layout without FLANGES?)");
            }
            if (r2 != null)
            {
                M.NumBends = r2.IntAt(0); M.NumRigids = r2.IntAt(1); M.NumExpJoints = r2.IntAt(2);
                M.NumRestraintBlocks = r2.IntAt(3); M.NumDisplBlocks = r2.IntAt(4); M.NumForceBlocks = r2.IntAt(5);
            }
            if (r3 != null)
            {
                M.NumUniform = r3.IntAt(0); M.NumWind = r3.IntAt(1); M.NumOffsets = r3.IntAt(2);
                M.NumAllowables = r3.IntAt(3); M.NumSifTees = r3.IntAt(4);
                M.ZUp = r3.IntAt(5) == 1;
                if (r3.V.Length >= 6 && r3.IntAt(5) != 0 && r3.IntAt(5) != 1) Warn("CONTROL IZUP = " + I(r3.IntAt(5)) + " (0 or 1 expected); Y up assumed");
            }
            if (r4 != null) M.NumEquipment = r4.IntAt(0);
        }

        void ParseUnits()
        {
            var lines = SectionLines("UNITS");
            var u = M.Units;
            if (lines.Count == 0) { Warn("no UNITS section: values are taken as written, length unit unknown"); return; }
            int i = 0, k = 0;
            while (i < lines.Count && k < CaesarUnits.FactorCount)
            {
                var r = Classify(lines[i]);
                if (r.Blank || r.Text || r.V.Length == 0) break;
                foreach (var v in r.V) { if (k < CaesarUnits.FactorCount) u.Factors[k] = v; k++; }
                i++;
            }
            if (k != CaesarUnits.FactorCount) Warn("UNITS: " + I(k) + " conversion factors read (" + I(CaesarUnits.FactorCount) + " expected)");
            for (; i < lines.Count; i++) u.Labels.Add(lines[i].Trim());
            int extra = u.Labels.Count - CaesarUnits.FactorCount;
            if (extra == 2) u.LabelOffset = 2;
            else
            {
                u.LabelOffset = Math.Max(0, extra);
                Warn("UNITS: " + I(u.Labels.Count) + " label lines (24 expected: units-file name, on/off flag, 22 labels)");
            }
            u.UnitsFileName = u.LabelOffset > 0 && u.Labels.Count > 0 ? u.Labels[0] : "";
            u.LengthLabel = u.Label(CaesarUnits.Length);
            u.LengthUnit = CaesarUnits.NormalizeLength(u.LengthLabel, u.Factor(CaesarUnits.Length));
            if (u.LengthUnit.Length == 0) Warn("UNITS: length unit not recognised (label \"" + u.LengthLabel + "\", factor " + F(u.Factor(CaesarUnits.Length)) + ")");
            else
            {
                string byLabel = CaesarUnits.NormalizeLength(u.LengthLabel, double.NaN);
                if (byLabel.Length > 0 && byLabel != u.LengthUnit) Warn("UNITS: length label \"" + u.LengthLabel + "\" disagrees with its factor " + F(u.Factor(CaesarUnits.Length)) + "; " + u.LengthUnit + " used");
            }
        }

        // ---------------- ELEMENTS

        void ParseElements()
        {
            var rows = Rows(SectionLines("ELEMENTS"));
            int n = rows.Count;
            int nb = rows.Count(x => !x.Blank);   // a blank line is no part of a record (the pointer rows are the last 3 non-blank ones)
            if (M.NumElements > 0 && nb % M.NumElements == 0) M.LinesPerBlock["ELEMENTS"] = nb / M.NumElements;
            else if (M.NumElements > 0) Warn("ELEMENTS: " + I(nb) + " non-blank lines is not a multiple of NUMELT " + I(M.NumElements) + "; records found by structure");
            int i = 0, skipped = 0, badCounts = 0;
            var realRowCounts = new Dictionary<int, int>();
            while (i < n)
            {
                if (!rows[i].IsReal) { if (!rows[i].Blank) skipped++; i++; continue; }
                var real = new List<Row>();
                while (i < n && rows[i].IsReal) real.Add(rows[i++]);
                var tail = new List<Row>();
                while (i < n && !rows[i].IsReal) { if (!rows[i].Blank) tail.Add(rows[i]); i++; }
                var e = new CaesarElement();
                e.Index = M.Elements.Count;
                e.RealRows = real.Select(x => x.V).ToArray();
                int rc;
                realRowCounts.TryGetValue(real.Count, out rc);
                realRowCounts[real.Count] = rc + 1;
                if (real[0].V.Length < 6 || (real.Count > 1 && real[1].V.Length < 3)) badCounts++;
                e.From = Round(e.Real(0, 0)); e.To = Round(e.Real(0, 1));
                e.Dx = Z(e.Real(0, 2)); e.Dy = Z(e.Real(0, 3)); e.Dz = Z(e.Real(0, 4)); e.Od = Z(e.Real(0, 5));
                e.Wall = Z(e.Real(1, 0)); e.Insulation = Z(e.Real(1, 1)); e.Corrosion = Z(e.Real(1, 2));
                // pointers = the last 3 rows; the rows before them: text (name, line number) and integer rows
                int np = tail.Count >= 3 ? 3 : 0;
                bool ptrOk = np == 3 && tail[tail.Count - 3].IsInt && tail[tail.Count - 2].IsInt && tail[tail.Count - 1].IsInt;
                if (!ptrOk) WarnAgg("elem-ptr", "ELEMENTS: pointer rows not found at the end of the record; pointers set to 0", "element " + I(e.Index + 1));
                var texts = new List<string>();
                for (int k = 0; k < tail.Count - (ptrOk ? 3 : 0); k++)
                {
                    var t = tail[k];
                    if (t.Text) texts.Add(t.TextValue);
                    else if (t.IsInt) e.ExtraIntRows.Add(t.V.Select(x => Round(x)).ToArray());
                }
                if (texts.Count > 0) e.Name = texts[0];
                if (texts.Count > 1) e.LineNumberRaw = texts[1];
                if (ptrOk)
                {
                    Row p1 = tail[tail.Count - 3], p2 = tail[tail.Count - 2], p3 = tail[tail.Count - 1];
                    e.Bend = p1.IntAt(0); e.Rigid = p1.IntAt(1); e.ExpJt = p1.IntAt(2); e.Restraint = p1.IntAt(3); e.Displmnt = p1.IntAt(4); e.Forcmnt = p1.IntAt(5);
                    e.Uniform = p2.IntAt(0); e.Wind = p2.IntAt(1); e.Offsets = p2.IntAt(2); e.Allowbls = p2.IntAt(3); e.SifTee = p2.IntAt(4); e.NodeName = p2.IntAt(5);
                    e.Reducer = p3.IntAt(0); e.Flange = p3.IntAt(1); e.Equipmnt = p3.IntAt(2);
                    if (p1.V.Length != 6 || p2.V.Length != 6) WarnAgg("elem-ptrcount", "ELEMENTS: pointer rows do not hold 6 / 6 values", "element " + I(e.Index + 1));
                }
                M.Elements.Add(e);
            }
            if (skipped > 0) Warn("ELEMENTS: " + I(skipped) + " line(s) outside any element record were ignored");
            if (badCounts > 0) Warn("ELEMENTS: " + I(badCounts) + " record(s) with short real rows (FROM TO DX DY DZ OD / WALL ... expected)");
            if (realRowCounts.Count > 1 || (realRowCounts.Count == 1 && !realRowCounts.ContainsKey(9)))
                Warn("ELEMENTS: real rows per element = " + string.Join(", ", realRowCounts.Select(kv => I(kv.Key) + " (x" + I(kv.Value) + ")").ToArray()) + " (9 seen in 11.00 and 15.01 files)");
            if (M.NumElements != M.Elements.Count) Warn("ELEMENTS: " + I(M.Elements.Count) + " records read, CONTROL NUMELT = " + I(M.NumElements));
            // line numbers carry forward
            string running = "";
            foreach (var e in M.Elements)
            {
                if (e.LineNumberRaw.Length > 0) running = e.LineNumberRaw;
                e.LineNumber = running;
            }
            // pointer ranges
            foreach (var e in M.Elements)
            {
                e.Bend = Ptr(e, "BEND", e.Bend, M.NumBends);
                e.Rigid = Ptr(e, "RIGID", e.Rigid, M.NumRigids);
                e.ExpJt = Ptr(e, "EXPJT", e.ExpJt, M.NumExpJoints);
                e.Restraint = Ptr(e, "RESTRANT", e.Restraint, M.NumRestraintBlocks);
                e.Displmnt = Ptr(e, "DISPLMNT", e.Displmnt, M.NumDisplBlocks);
                e.Forcmnt = Ptr(e, "FORCMNT", e.Forcmnt, M.NumForceBlocks);
                e.Uniform = Ptr(e, "UNIFORM", e.Uniform, M.NumUniform);
                e.Wind = Ptr(e, "WIND", e.Wind, M.NumWind);
                e.Offsets = Ptr(e, "OFFSETS", e.Offsets, M.NumOffsets);
                e.Allowbls = Ptr(e, "ALLOWBLS", e.Allowbls, M.NumAllowables);
                e.SifTee = Ptr(e, "SIF&TEES", e.SifTee, M.NumSifTees);
                e.NodeName = Ptr(e, "NODENAME", e.NodeName, M.NumNodeNames);
                e.Reducer = Ptr(e, "REDUCERS", e.Reducer, M.NumReducers);
                e.Flange = Ptr(e, "FLANGES", e.Flange, M.NumFlanges);
                e.Equipmnt = Ptr(e, "EQUIPMNT", e.Equipmnt, M.NumEquipment);
            }
        }

        static double Z(double v) { return double.IsNaN(v) ? 0.0 : v; }

        int Ptr(CaesarElement e, string section, int p, int count)
        {
            if (p >= 0 && p <= count) return p;
            WarnAgg("ptr-" + section, section + " pointer outside 1.." + I(count) + " (set to 0)", "element " + I(e.Index + 1) + " -> " + I(p));
            return 0;
        }

        // ---------------- auxiliary sections

        // blocks of a section by line count / CONTROL count; null when the section is empty or the count is 0
        List<List<string>> Blocks(string section, int count)
        {
            var lines = SectionLines(section);
            if (count <= 0)
            {
                if (lines.Any(l => l.Trim().Length > 0)) Warn(section + ": " + I(lines.Count) + " lines but CONTROL count 0; ignored");
                return null;
            }
            if (lines.Count == 0) { Warn(section + ": CONTROL count " + I(count) + " but the section is empty or missing"); return null; }
            int per = lines.Count / count;
            if (per < 1) { Warn(section + ": " + I(lines.Count) + " lines for " + I(count) + " blocks; ignored"); return null; }
            if (lines.Count % count != 0) Warn(section + ": " + I(lines.Count) + " lines is not a multiple of the CONTROL count " + I(count) + "; read as " + I(per) + " lines per block");
            M.LinesPerBlock[section] = per;
            var blocks = new List<List<string>>(count);
            for (int b = 0; b < count; b++) blocks.Add(lines.GetRange(b * per, per));
            return blocks;
        }

        // values of every numeric row of the block, row by row
        List<double[]> NumRows(List<string> lines)
        {
            var res = new List<double[]>();
            foreach (var l in lines) { var r = Classify(l); res.Add(r.Text ? new double[0] : r.V); }
            return res;
        }

        static double At(List<double[]> rows, int r, int c)
        {
            if (r < 0 || r >= rows.Count || c < 0 || c >= rows[r].Length) return 0.0;
            return rows[r][c];
        }

        static double[] Flat(List<double[]> rows) { return rows.SelectMany(x => x).ToArray(); }

        void ParseBends()
        {
            var blocks = Blocks("BEND", M.NumBends);
            if (blocks == null) return;
            for (int b = 0; b < blocks.Count; b++)
            {
                var rows = NumRows(blocks[b]);
                var bend = new CaesarBend();
                bend.Block = b + 1;
                bend.Radius = At(rows, 0, 0);
                bend.TypeCode = Round(At(rows, 0, 1));
                double[][] slots = { new[] { At(rows, 0, 2), At(rows, 0, 3) }, new[] { At(rows, 0, 4), At(rows, 0, 5) }, new[] { At(rows, 1, 0), At(rows, 1, 1) } };
                foreach (var s in slots) if (Round(s[1]) > 0) bend.AngleNodes.Add(new[] { s[0], (double)Round(s[1]) });
                bend.MiterPoints = Round(At(rows, 1, 2));
                bend.FittingThickness = At(rows, 1, 3);
                bend.KFactor = At(rows, 1, 4);
                bend.Raw = Flat(rows);
                if (!(bend.Radius > 0)) WarnAgg("bend-r", "BEND: radius not positive", "block " + I(b + 1));
                M.Bends.Add(bend);
            }
        }

        void ParseRigids()
        {
            var blocks = Blocks("RIGID", M.NumRigids);
            if (blocks == null) return;
            for (int b = 0; b < blocks.Count; b++)
            {
                var rows = NumRows(blocks[b]);
                var r = new CaesarRigid();
                r.Block = b + 1;
                r.Weight = At(rows, 0, 0);
                r.TypeCode = Round(At(rows, 0, 1));
                r.TypeName = CaesarRigid.NameOf(r.TypeCode);
                r.Raw = Flat(rows);
                if (r.TypeCode < 0 || r.TypeCode > 4) WarnAgg("rigid-type", "RIGID: type code not known (0 Unspecified, 1 Valve, 2 Flange, 3 Flange Pair, 4 Flange Valve); drawn as a plain rigid", "block " + I(b + 1) + " type " + I(r.TypeCode));
                M.Rigids.Add(r);
            }
        }

        void ParseExpJoints()
        {
            var blocks = Blocks("EXPJT", M.NumExpJoints);
            if (blocks == null) return;
            for (int b = 0; b < blocks.Count; b++)
            {
                var x = new CaesarExpJoint();
                x.Block = b + 1;
                x.Raw = Flat(NumRows(blocks[b]));
                M.ExpJoints.Add(x);
            }
        }

        void ParseReducers()
        {
            var blocks = Blocks("REDUCERS", M.NumReducers);
            if (blocks == null) return;
            for (int b = 0; b < blocks.Count; b++)
            {
                var rows = NumRows(blocks[b]);
                var r = new CaesarReducer();
                r.Block = b + 1;
                r.Od2 = At(rows, 0, 0); r.Wall2 = At(rows, 0, 1); r.Alpha = At(rows, 0, 2); r.R1 = At(rows, 0, 3); r.R2 = At(rows, 0, 4);
                r.Raw = Flat(rows);
                M.Reducers.Add(r);
            }
        }

        // slots per RESTRANT block, found by structure: every slot starts with a numeric line of >= 5 values
        // followed (when the slot has 2+ lines) by a numeric line of 3 cosines. 6 slots in 10.00+, 4 in older files.
        int RestraintSlots(List<List<string>> blocks, int per)
        {
            int[] candidates = { 6, 4, 8, 5, 3, 2, 1 };
            foreach (int s in candidates)
            {
                if (per % s != 0) continue;
                int sl = per / s;
                bool ok = true;
                foreach (var blk in blocks)
                {
                    for (int k = 0; k < s && ok; k++)
                    {
                        var a = Classify(blk[k * sl]);
                        if (a.Text || a.Blank || a.V.Length < 5) ok = false;
                        if (ok && sl >= 2) { var c = Classify(blk[k * sl + 1]); if (c.Text || c.Blank || c.V.Length != 3) ok = false; }
                    }
                    if (!ok) break;
                }
                if (ok) return s;
            }
            return 0;
        }

        void ParseRestraints()
        {
            var blocks = Blocks("RESTRANT", M.NumRestraintBlocks);
            if (blocks == null) return;
            int per = M.LinesPerBlock["RESTRANT"];
            int slots = RestraintSlots(blocks, per);
            if (slots == 0)
            {
                slots = per % 6 == 0 ? 6 : (per % 4 == 0 ? 4 : 1);
                Warn("RESTRANT: " + I(per) + "-line blocks do not split into slots by structure; read as " + I(slots) + " slots (best effort)");
            }
            int sl = per / slots;
            M.LinesPerBlock["RESTRANT/slot"] = sl;
            double kTrans = M.Units.Factor(CaesarUnits.TransStiffness), kRot = M.Units.Factor(CaesarUnits.RotStiffness);
            if (double.IsNaN(kTrans) || kTrans <= 0) kTrans = 1.0;
            if (double.IsNaN(kRot) || kRot <= 0) kRot = 1.0;
            for (int b = 0; b < blocks.Count; b++)
            {
                for (int s = 0; s < slots; s++)
                {
                    var lines = blocks[b].GetRange(s * sl, sl);
                    var first = Classify(lines[0]);
                    int node = first.Text ? 0 : first.IntAt(0);
                    if (node <= 0) continue;
                    var r = new CaesarRestraint();
                    r.Block = b + 1; r.Slot = s; r.Node = node;
                    r.TypeCode = first.IntAt(1);
                    r.Stiffness = Z(first.At(2)); r.Gap = Z(first.At(3)); r.Friction = Z(first.At(4)); r.CNode = Math.Max(0, first.IntAt(5));
                    var nums = new List<double>(first.V);
                    var texts = new List<string>();
                    for (int k = 1; k < lines.Count; k++)
                    {
                        var x = Classify(lines[k]);
                        if (x.Text) texts.Add(x.TextValue);
                        else if (k == 1 && x.V.Length >= 3) { r.Cosines = new[] { x.V[0], x.V[1], x.V[2] }; nums.AddRange(x.V); }
                        else nums.AddRange(x.V);
                    }
                    if (texts.Count > 0) r.Tag = texts[0];
                    if (texts.Count > 1) r.Guid = texts[1];
                    r.Raw = nums.ToArray();
                    string name = CaesarRestraint.ConfirmedName(r.TypeCode);
                    r.Confirmed = name != null;
                    if (name == null)
                    {
                        name = "OTHER(" + I(r.TypeCode) + ")";
                        string guess = CaesarRestraint.Guess(r.TypeCode);
                        WarnAgg("rtype-" + I(r.TypeCode), "RESTRANT: type code " + I(r.TypeCode) + " is not confirmed" +
                            (guess != null ? " (a third-party table says " + guess + ")" : "") + "; kept as " + name + " until a restraint report confirms it", "node " + I(node));
                    }
                    r.TypeName = name;
                    r.IsRotational = r.TypeCode >= 5 && r.TypeCode <= 7;
                    double kf = r.IsRotational ? kRot : kTrans;
                    r.IsRigid = r.Stiffness <= 0 || r.Stiffness >= 1e11 * kf;
                    M.Restraints.Add(r);
                }
            }
        }

        void ParseDisplacements()
        {
            var blocks = Blocks("DISPLMNT", M.NumDisplBlocks);
            if (blocks == null) return;
            int per = M.LinesPerBlock["DISPLMNT"];
            if (per % 2 != 0) Warn("DISPLMNT: " + I(per) + "-line blocks do not split into 2 slots; read as 1 slot (best effort)");
            int slots = per % 2 == 0 ? 2 : 1;
            int sl = per / slots;
            M.LinesPerBlock["DISPLMNT/slot"] = sl;
            if (sl != 10) Warn("DISPLMNT: " + I(sl) + " lines per slot (node line + 9 vectors seen in 11.00); vectors read as they come");
            for (int b = 0; b < blocks.Count; b++)
            {
                for (int s = 0; s < slots; s++)
                {
                    var lines = blocks[b].GetRange(s * sl, sl);
                    var first = Classify(lines[0]);
                    int node = first.Text ? 0 : first.IntAt(0);
                    if (node <= 0) continue;
                    var d = new CaesarDisplacement();
                    d.Block = b + 1; d.Slot = s; d.Node = node;
                    var nums = new List<double>(first.V);
                    var vecs = new List<double[]>();
                    for (int k = 1; k < lines.Count; k++)
                    {
                        var x = Classify(lines[k]);
                        if (x.Text || x.Blank) continue;
                        nums.AddRange(x.V);
                        var vec = new double[6];
                        for (int q = 0; q < 6; q++)
                        {
                            double v = q < x.V.Length ? x.V[q] : CaesarDisplacement.Free;
                            bool free = Math.Abs(v - CaesarDisplacement.Free) < 0.01;
                            vec[q] = free ? double.NaN : v;
                            if (!free) d.Fixed[q] = true;
                        }
                        vecs.Add(vec);
                    }
                    d.Vectors = vecs.ToArray();
                    d.Raw = nums.ToArray();
                    M.Displacements.Add(d);
                }
            }
        }

        void ParseSifTees()
        {
            var blocks = Blocks("SIF&TEES", M.NumSifTees);
            if (blocks == null) return;
            int per = M.LinesPerBlock["SIF&TEES"];
            if (per % 2 != 0) Warn("SIF&TEES: " + I(per) + "-line blocks do not split into 2 slots; read as 1 slot (best effort)");
            int slots = per % 2 == 0 ? 2 : 1;
            int sl = per / slots;
            M.LinesPerBlock["SIF&TEES/slot"] = sl;
            for (int b = 0; b < blocks.Count; b++)
            {
                for (int s = 0; s < slots; s++)
                {
                    var rows = NumRows(blocks[b].GetRange(s * sl, sl));
                    int node = Round(At(rows, 0, 0));
                    if (node <= 0) continue;
                    var t = new CaesarSifTee();
                    t.Block = b + 1; t.Slot = s; t.Node = node;
                    t.TypeCode = Round(At(rows, 0, 1));
                    t.TypeName = CaesarSifTee.NameOf(t.TypeCode);
                    t.SifIn = At(rows, 0, 2); t.SifOut = At(rows, 0, 3);
                    // tee surface node: line 5 value 3 in 15.01 ("Srf.Node" in the input echo); 0 in 11.00 files
                    double sn = At(rows, 4, 2);
                    int sni = Round(sn);
                    if (sni > 0 && sni != node && Math.Abs(sn - sni) < 1e-6) t.SurfaceNode = sni;
                    t.Raw = Flat(rows);
                    M.SifTees.Add(t);
                }
            }
        }

        void ParseNodeNames()
        {
            var blocks = Blocks("NODENAME", M.NumNodeNames);
            if (blocks == null) return;
            for (int b = 0; b < blocks.Count; b++)
            {
                string l = blocks[b][0];
                string from = l.Length > 2 ? l.Substring(2, Math.Min(26, l.Length - 2)).Trim() : "";
                string to = l.Length > 28 ? l.Substring(28).Trim() : "";
                M.NodeNames[b + 1] = new[] { from, to };
            }
        }

        // sections read only for their line count (their blocks hold no geometry)
        void CheckOtherSections()
        {
            Blocks("FORCMNT", M.NumForceBlocks);
            Blocks("UNIFORM", M.NumUniform);
            Blocks("WIND", M.NumWind);
            Blocks("ALLOWBLS", M.NumAllowables);
            Blocks("FLANGES", M.NumFlanges);
            Blocks("EQUIPMNT", M.NumEquipment);
            Blocks("OFFSETS", M.NumOffsets);
            if (M.NumOffsets > 0)
            {
                var hit = M.Elements.Where(e => e.Offsets > 0).Select(e => I(e.From) + "-" + I(e.To)).ToList();
                Warn("OFFSETS: " + I(M.NumOffsets) + " element end offset block(s) are NOT applied to the geometry; elements: " +
                     (hit.Count > 0 ? string.Join(", ", hit.Take(12).ToArray()) + (hit.Count > 12 ? ", ..." : "") : "(none points to them)"));
            }
        }

        // ---------------- MISCEL_1: material numbers, nozzles, hangers, execution options

        void ParseMiscel()
        {
            var lines = SectionLines("MISCEL_1");
            if (lines.Count == 0) { if (M.NumHangers > 0 || M.NumNozzles > 0) Warn("MISCEL_1 missing: hangers / nozzles not read"); return; }
            var rows = Rows(lines);
            int n = rows.Count;
            long matLong = ((long)Math.Max(0, M.NumElements) + 5) / 6;
            if (matLong > n) { Warn("MISCEL_1: shorter than the material-number lines; skipped"); return; }
            int matLines = (int)matLong;
            var mats = new List<int>();
            for (int i = 0; i < matLines; i++) foreach (var v in rows[i].V) mats.Add(Round(v));
            for (int i = 0; i < M.Elements.Count && i < mats.Count; i++) M.Elements[i].Material = mats[i];
            if (mats.Count != M.NumElements) Warn("MISCEL_1: " + I(mats.Count) + " material numbers for " + I(M.NumElements) + " elements");
            M.LinesPerBlock["MISCEL_1/materials"] = matLines;
            int pos = matLines;
            const int optionLines = 4;   // execution options closing the section (11.00 and 15.01)
            int nh = M.NumHangers, nn = M.NumNozzles;
            if (nh < 0 || nh > n || nn < 0 || nn > n) { Warn("MISCEL_1: CONTROL hanger / nozzle counts (" + I(nh) + ", " + I(nn) + ") do not fit the section; skipped"); return; }
            int hgrStart = -1;
            if (nh > 0)
            {
                hgrStart = FindHangerList(rows, pos, nh);
                if (hgrStart < 0) { Warn("MISCEL_1: hanger list for " + I(nh) + " hanger(s) not found; hangers skipped"); }
            }
            int nozLines = 0;
            if (nn > 0)
            {
                int avail = (hgrStart >= 0 ? hgrStart : n - optionLines) - pos;
                if (avail > 0 && avail % nn == 0) nozLines = avail / nn;
                else { nozLines = 4; Warn("MISCEL_1: nozzle block size not clear (" + I(avail) + " lines for " + I(nn) + " nozzles); 4 lines each assumed"); }
                M.LinesPerBlock["MISCEL_1/nozzle"] = nozLines;
                for (int k = 0; k < nn && pos + (k + 1) * nozLines <= n; k++)
                {
                    var r = rows[pos + k * nozLines];
                    var z = new CaesarNozzle();
                    z.Index = k; z.Node = r.IntAt(0); z.VesselNode = r.IntAt(1);
                    var vals = new List<double>();
                    for (int q = 0; q < nozLines; q++) vals.AddRange(rows[pos + k * nozLines + q].V);
                    z.Values = vals.ToArray();
                    M.Nozzles.Add(z);
                }
            }
            if (hgrStart >= 0) ParseHangers(rows, hgrStart, nh);
        }

        // the hanger section starts with 2 default lines (an integer first field), then the hanger node list
        // (integers, 6 per line, every one an element or bend node). Returns the index of the first default line.
        int FindHangerList(List<Row> rows, int from, int nh)
        {
            var nodes = new HashSet<int>();
            foreach (var e in M.Elements) { nodes.Add(e.From); nodes.Add(e.To); }
            foreach (var b in M.Bends) foreach (var an in b.AngleNodes) nodes.Add(Round(an[1]));
            int listLines = (nh + 5) / 6;
            for (int pass = 0; pass < 2; pass++)
                for (int p = from; p + 2 + listLines <= rows.Count; p++)
                {
                    if (rows[p].Text || rows[p].V.Length < 2 || !rows[p + 1].IsInt) continue;
                    var vals = new List<int>();
                    bool ok = true;
                    for (int q = 0; q < listLines && ok; q++)
                    {
                        var r = rows[p + 2 + q];
                        if (!r.IsInt) ok = false; else vals.AddRange(r.V.Select(x => Round(x)));
                    }
                    if (!ok || vals.Count != nh) continue;
                    if (pass == 0 && vals.All(v => v > 0 && nodes.Contains(v))) return p;
                    if (pass == 1 && vals.All(v => v > 0))
                    {
                        Warn("MISCEL_1: hanger node list found by layout only (some of its nodes are on no element): " + string.Join(" ", vals.Select(v => I(v)).ToArray()));
                        return p;
                    }
                }
            return -1;
        }

        // h-line hanger blocks: each starts with 2 numeric lines; then nh free-anchor integer rows; then 4 packed integer groups
        static bool HangerLayoutOk(List<Row> rows, int q0, int nh, int h, int listLines)
        {
            int freeAt = q0 + nh * h, packedAt = freeAt + nh;
            if (packedAt + 4 * listLines > rows.Count) return false;
            for (int k = 0; k < nh; k++)
            {
                int b = q0 + k * h;
                if (rows[b].Text || rows[b].Blank || rows[b].V.Length == 0 || rows[b + 1].Text || rows[b + 1].Blank) return false;
                if (!rows[freeAt + k].IsInt) return false;
            }
            for (int q = 0; q < 4 * listLines; q++) if (!rows[packedAt + q].IsInt) return false;
            return true;
        }

        void ParseHangers(List<Row> rows, int p, int nh)
        {
            int listLines = (nh + 5) / 6;
            var nodes = new List<int>();
            for (int q = 0; q < listLines; q++) nodes.AddRange(rows[p + 2 + q].V.Select(x => Round(x)));
            int q0 = p + 2 + listLines;
            // per-hanger block: 2 numeric data lines, then its text rows (tag, GUID) -- 4 lines in 11.00. Candidates:
            // the text-row count of the first block, and what the line count leaves (4 option lines at the end).
            int hText = 2;
            while (q0 + hText < rows.Count && rows[q0 + hText].Text) hText++;
            int left = rows.Count - q0 - nh - 4 * listLines - 4;
            int hArith = left > 0 && left % nh == 0 ? left / nh : 0;
            int h = 0;
            foreach (int cand in new[] { hText, hArith, 4 })
                if (cand >= 2 && HangerLayoutOk(rows, q0, nh, cand, listLines)) { h = cand; break; }
            bool ok = h > 0;
            int freeAt = q0 + nh * h, packedAt = freeAt + nh;
            if (!ok)
            {
                Warn("MISCEL_1: hanger data for " + I(nh) + " hanger(s) does not fit the expected layout; only the hanger nodes were kept");
                for (int k = 0; k < nodes.Count; k++) { var hg = new CaesarHanger(); hg.Index = k; hg.Node = nodes[k]; hg.Values = new double[0]; M.Hangers.Add(hg); }
                return;
            }
            M.LinesPerBlock["MISCEL_1/hanger"] = h;
            var packed = new List<int>[4];
            for (int g = 0; g < 4; g++)
            {
                packed[g] = new List<int>();
                for (int q = 0; q < listLines; q++) packed[g].AddRange(rows[packedAt + g * listLines + q].V.Select(x => Round(x)));
            }
            for (int k = 0; k < nh; k++)
            {
                var hg = new CaesarHanger();
                hg.Index = k;
                hg.Node = k < nodes.Count ? nodes[k] : 0;
                int b = q0 + k * h;
                hg.Values = rows[b].V.Concat(rows[b + 1].V).ToArray();
                if (h > 2 && rows[b + 2].Text) hg.Tag = rows[b + 2].TextValue;
                var fr = rows[freeAt + k];
                hg.FreeAnchor1 = fr.IntAt(0); hg.FreeAnchor2 = fr.IntAt(1); hg.FreeDof1 = fr.IntAt(2); hg.FreeDof2 = fr.IntAt(3);
                hg.NumHangers = k < packed[0].Count ? packed[0][k] : 0;
                hg.Table = k < packed[1].Count ? packed[1][k] : 0;
                hg.ShortRange = k < packed[2].Count ? packed[2][k] : 0;
                hg.CNode = k < packed[3].Count ? Math.Max(0, packed[3][k]) : 0;
                M.Hangers.Add(hg);
            }
            int rest = rows.Count - (packedAt + 4 * listLines);
            if (rest != 4) Warn("MISCEL_1: " + I(rest) + " line(s) after the hanger data (4 execution-option lines expected); hanger values are best effort");
        }

        // ---------------- COORDS

        void ParseCoords()
        {
            var rows = Rows(SectionLines("COORDS")).Where(r => !r.Blank).ToList();
            if (rows.Count == 0) { Warn("COORDS is empty: the model starts at the origin"); return; }
            int count = rows[0].IntAt(0);
            for (int i = 1; i < rows.Count; i++)
            {
                var r = rows[i];
                if (r.Text || r.V.Length < 4) { WarnAgg("coords-bad", "COORDS: entry without node, X, Y, Z ignored", "line " + I(i + 1)); continue; }
                var raw = new[] { (double)r.IntAt(0), r.V[1], r.V[2], r.V[3] };
                M.CoordsRaw.Add(raw);
                var u = M.Units;
                M.Coords.Add(new[] { raw[0],
                    u.Convert(raw[1], CaesarUnits.CompoundLength, CaesarUnits.Length),
                    u.Convert(raw[2], CaesarUnits.CompoundLength, CaesarUnits.Length),
                    u.Convert(raw[3], CaesarUnits.CompoundLength, CaesarUnits.Length) });
            }
            if (count != M.Coords.Count) Warn("COORDS: count line says " + I(count) + ", " + I(M.Coords.Count) + " entries read");
        }

        // ---------------- cross-links

        void Link()
        {
            var seen = new Dictionary<string, int>();
            foreach (var e in M.Elements)
            {
                if (e.Bend > 0 && e.Bend <= M.Bends.Count) Own("BEND", e.Bend, e, seen, delegate(int i) { M.Bends[i].ElementIndex = e.Index; });
                if (e.Rigid > 0 && e.Rigid <= M.Rigids.Count) Own("RIGID", e.Rigid, e, seen, delegate(int i) { M.Rigids[i].ElementIndex = e.Index; });
                if (e.ExpJt > 0 && e.ExpJt <= M.ExpJoints.Count) Own("EXPJT", e.ExpJt, e, seen, delegate(int i) { M.ExpJoints[i].ElementIndex = e.Index; });
                if (e.Reducer > 0 && e.Reducer <= M.Reducers.Count) Own("REDUCERS", e.Reducer, e, seen, delegate(int i) { M.Reducers[i].ElementIndex = e.Index; });
                if (e.NodeName > 0)
                {
                    string[] nm;
                    if (M.NodeNames.TryGetValue(e.NodeName, out nm))
                    {
                        e.FromName = nm[0]; e.ToName = nm[1];
                        if (nm[0].Length > 0) M.NodeNameByNode[e.From] = nm[0];
                        if (nm[1].Length > 0) M.NodeNameByNode[e.To] = nm[1];
                    }
                }
            }
            var byBlockR = new Dictionary<int, int>(); var byBlockD = new Dictionary<int, int>(); var byBlockS = new Dictionary<int, int>();
            foreach (var e in M.Elements)
            {
                if (e.Restraint > 0 && !byBlockR.ContainsKey(e.Restraint)) byBlockR[e.Restraint] = e.Index;
                if (e.Displmnt > 0 && !byBlockD.ContainsKey(e.Displmnt)) byBlockD[e.Displmnt] = e.Index;
                if (e.SifTee > 0 && !byBlockS.ContainsKey(e.SifTee)) byBlockS[e.SifTee] = e.Index;
            }
            int ei;
            foreach (var r in M.Restraints) if (byBlockR.TryGetValue(r.Block, out ei)) r.ElementIndex = ei;
            foreach (var d in M.Displacements) if (byBlockD.TryGetValue(d.Block, out ei)) d.ElementIndex = ei;
            foreach (var t in M.SifTees) if (byBlockS.TryGetValue(t.Block, out ei)) t.ElementIndex = ei;
            int orphanR = M.Restraints.Count(r => r.ElementIndex < 0);
            if (orphanR > 0) Warn("RESTRANT: " + I(orphanR) + " restraint(s) in blocks no element points to");
            int orphanB = M.Bends.Count(b => b.ElementIndex < 0);
            if (orphanB > 0) Warn("BEND: " + I(orphanB) + " bend block(s) no element points to (not drawn)");
        }

        void Own(string section, int block, CaesarElement e, Dictionary<string, int> seen, Action<int> set)
        {
            string key = section + "#" + I(block);
            int prev;
            if (seen.TryGetValue(key, out prev))
            {
                WarnAgg("own-" + section, section + ": block named by more than one element (the first one keeps it)", "block " + I(block) + " elements " + I(prev + 1) + ", " + I(e.Index + 1));
                return;
            }
            seen[key] = e.Index;
            set(block - 1);
        }
    }
}
