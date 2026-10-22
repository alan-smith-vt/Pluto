using System;
using System.Collections.Generic;
using System.Linq;

// PipeModel  --  a piping stress model in CAESAR II's own terms, independent of any file layout: what the
// pipe builder (PipeBuilder, from a TOML config) and the importer (CaesarPipeImport, from a .cii) produce
// and the writers consume (CaesarNeutralWriter now; a SAP2000 .s2k writer later). C# 5, one global batch:
// every type is prefixed PipeModel.
//
// Units: values are in the JOB's units, as CAESAR writes them. A new model (the TOML builder) uses CAESAR's
// English set: lengths, diameters and walls in inches, positions in inches (COORDS are written in feet),
// temperatures F, pressures psi, densities lb/cu.in., weights lb, translational stiffness lb/in.,
// rotational in.lb/deg, rotational gaps deg. An imported model keeps the units of its file (UnitFactors,
// English -> job, and the file's UNITS block in Source).
//
// Elements are in CAESAR input order. An element's deltas run FROM -> TO with CAESAR's bend convention:
// the element carrying a bend runs to the tangent intersection point, its TO node being the bend's far
// point; the next element is measured from the tangent intersection point too. A node that no element
// has reached yet starts where the previous element ended (CAESAR's rule), unless it has a COORDS entry.
//
// Loads: forces / moments and imposed displacements at nodes (NodeLoads: FORCMNT, DISPLMNT), uniform loads
// and wind / wave data per element (Uniform, Wind): the neutral file holds them on every element they act on,
// one block each, and an element without a block has none (a CAESAR 15.01 export and its input screen show
// it: no carry-forward in the file). Load cases are not in the neutral file: CAESAR builds them.
//
// Carried data (an imported .cii): everything the model does not edit stays as written -- the element
// records, each block's lines and its number in CAESAR's own order (Seq), the restraint / SIF / load slots
// with the element block that held them, the blocks the model does not read (Carried), and the file's
// VERSION, CONTROL, UNITS and MISCEL_1 tail (Source). The writer overlays only the fields that changed, so
// an unedited import is written back identically (CaesarPipeImport checks it).

public class PipeModel
{
    public string Title = "";                 // VERSION "PROJECT:" line (the output file only)
    public string Notes = "";                 // VERSION "NOTES  :" line
    public bool ZUp;                          // CONTROL IZUP: false = Y up (CAESAR's default)
    public double Ambient = 70.0;             // F, installation temperature: thermal strain from T - ambient
    public string Code = "B31.1";             // piping code of the built-in allowables block
    public List<string> AllowablesBlock;      // a seed file's ALLOWBLS block, verbatim (null = built-in B31.1)
    public double[] UnitFactors;              // English -> job unit factors (22, CAESAR's UNITS order); null = English
    public PipeModelSource Source;            // the file a model was imported from (or seeded by); null = new
    public readonly List<PipeModelElement> Elements = new List<PipeModelElement>();
    public readonly List<PipeModelRestraint> Restraints = new List<PipeModelRestraint>();
    public readonly List<PipeModelJoint> Joints = new List<PipeModelJoint>();     // expanded to CNODE restraints when written
    public readonly List<PipeModelSif> Sifs = new List<PipeModelSif>();
    public readonly List<PipeModelNodeLoad> NodeLoads = new List<PipeModelNodeLoad>();   // forces / moments and imposed displacements
    public readonly List<PipeModelCoord> Coords = new List<PipeModelCoord>();
    public readonly Dictionary<int, double[]> Positions = new Dictionary<int, double[]>();   // node -> x y z (in), as built
    public readonly List<string> Warnings = new List<string>();

    // English -> job factor i (CaesarUnits order); the English value (1, or the template's) when not known
    public double Factor(int i)
    {
        if (UnitFactors != null && i >= 0 && i < UnitFactors.Length && !double.IsNaN(UnitFactors[i]) && Math.Abs(UnitFactors[i]) > 1e-30) return UnitFactors[i];
        return i >= 0 && i < PipeModelCodes.EnglishFactors.Length ? PipeModelCodes.EnglishFactors[i] : 1.0;
    }

    public PipeModelElement ElementByUid(int uid)
    {
        if (uid <= 0) return null;
        foreach (var e in Elements) if (e.Uid == uid) return e;
        return null;
    }

    // uids for elements that have none (the builder's), keeping the ones set
    public void AssignUids()
    {
        int next = Elements.Count == 0 ? 1 : Math.Max(1, Elements.Max(e => e.Uid) + 1);
        foreach (var e in Elements) if (e.Uid <= 0) e.Uid = next++;
    }

    public IEnumerable<PipeModelNodeLoad> Forces { get { return NodeLoads.Where(x => !x.IsDisplacement); } }
    public IEnumerable<PipeModelNodeLoad> Displacements { get { return NodeLoads.Where(x => x.IsDisplacement); } }
}

public class PipeModelElement
{
    public int Uid;                                   // stable id (not written): an import numbers 1..n in file order
    public int From, To;
    public double Dx, Dy, Dz;                         // length units
    public double Od, Wall, Insulation, Corrosion;    // diameter / wall units
    public readonly double[] T = new double[9];       // T1..T9 (0 = not used)
    public readonly double[] P = new double[9];       // P1..P9
    public double Emod, Poisson, PipeDensity;         // 0 = from CAESAR's material database (by Material)
    public double InsulationDensity, FluidDensity;
    public int Material;                              // CAESAR material number (MISCEL_1)
    // Element row 8, values 5 and 6: angles (deg) CAESAR 15.01 writes on the elements meeting at a welding
    // tee (90 / 180 / 315 in the fixture; not in the input echo, meaning unconfirmed). 9999.99 = not set,
    // what the builder writes; kept so a CAESAR file round-trips exactly.
    public double TeeAngle1 = 9999.99, TeeAngle2 = 9999.99;
    public string Name = "";                          // element name (rigids: the component)
    public string LineNumber = "";                    // written on the first element of a line, carried forward by CAESAR
    public PipeModelBend Bend;                        // a bend at the TO end
    public PipeModelRigid Rigid;
    public PipeModelExpJoint ExpJoint;
    public PipeModelReducer Reducer;
    public PipeModelUniform Uniform;                  // uniform loads on this element (null = none)
    public PipeModelWind Wind;                        // wind / wave data on this element (null = none)
    public string Kind = "pipe";                      // for summaries: pipe, bend, valve, flange, ...
    public List<string> Raw;                          // the record as written (import); null = new
    // blocks this element owns and the model does not read (OFFSETS ALLOWBLS NODENAME FLANGES EQUIPMNT, and a
    // FORCMNT / UNIFORM / WIND block in a layout the model does not know), as written, with their number
    public readonly Dictionary<string, PipeModelBlock> Carried = new Dictionary<string, PipeModelBlock>();
    // this element's RESTRANT / SIF&TEES / DISPLMNT / FORCMNT block as written: its number orders the blocks,
    // its lines give back the slots no item fills
    public readonly Dictionary<string, PipeModelBlock> SlotBlocks = new Dictionary<string, PipeModelBlock>();
}

public class PipeModelBlock
{
    public int Seq;                           // block number in the source file (CAESAR's order); 0 = new
    public List<string> Lines = new List<string>();
}

public class PipeModelBend
{
    public double Radius;                     // length units
    public int Type;                          // 0 = welded (CAESAR's default)
    public readonly List<double[]> AngleNodes = new List<double[]>();   // {angle (deg, -2.0202 = mid point), node}; up to 3
    public int MiterPoints;
    public double FittingThickness, KFactor;  // the wall (as CAESAR writes it), the code's k (0)
    public const double MidPoint = -2.0202;   // CAESAR's code for the "M" (mid point) bend node

    // A negative radius CAESAR writes as a multiple of -1.0101 (-1.0101 .. -9.0909) is one of its coded values
    // (like -2.0202 = "M" in the angle fields), not a length: most likely a radius picked by name in CAESAR's
    // input. Which code means which radius is not confirmed yet, so a coded radius is carried as written and
    // drawn as a sharp corner. Returns the code's number (1-9), or 0 for an ordinary radius.
    public static int RadiusCode(double radius)
    {
        if (!(radius < 0)) return 0;
        double k = -radius / 1.0101;
        int n = (int)Math.Round(k);
        return n >= 1 && n <= 9 && Math.Abs(k - n) < 1e-4 ? n : 0;
    }
    public int Seq;                           // block number in the source file; 0 = new
    public List<string> Raw;                  // the block as written; null = new
}

public class PipeModelRigid
{
    public double Weight;                     // force units
    public int Type;                          // 0 unspecified, 1 valve, 2 flange, 3 flange pair, 4 flange + valve
    public int Seq;
    public List<string> Raw;
}

public class PipeModelExpJoint
{
    public double AxialStiffness, TransverseStiffness, BendingStiffness, TorsionStiffness, EffectiveId;   // lb/in, lb/in, in.lb/deg, in.lb/deg, in
    public int Seq;
    public List<string> Raw;
}

public class PipeModelReducer
{
    public double Od2, Wall2;                 // the TO end; the FROM end is the element's own Od / Wall
    public double Alpha, R1, R2;              // 0 = CAESAR computes them
    public int Seq;
    public List<string> Raw;
}

public class PipeModelRestraint
{
    public int Node;
    public string Type = "";                  // CAESAR's name: ANC X Y Z RX RY RZ GUI LIM +X +Y ... (OTHER(n): a code not in its table)
    public int Code;                          // CAESAR's type code
    public double Stiffness;                  // 0 = rigid (written as 1e12 in the job's stiffness unit)
    public double Gap;                        // length units, or deg for a rotation
    public double Mu;                         // friction coefficient
    public int CNode;
    public double[] Cosines;                  // null = along the global axis of the type (written as CAESAR does: PipeModelCodes.DefaultCosines)
    public string Tag = "";
    public string Guid = "";                  // CAESAR's restraint GUID text row (imported; "" = none)
    public string Source = "";                // which config item made it (messages)
    public int OwnerUid;                      // element whose block held it in the source (0 = none): kept there when it still can be
    public int Slot = -1;                     // its slot in that block
    public List<string> Raw;                  // the slot as written; null = new
}

public class PipeModelSif
{
    public int Node;
    public int Type;                          // 0 = user SIFs only, 3 = welding tee
    public double SifIn, SifOut;              // 0 = from the code
    public int SurfaceNode;                   // a tee's surface node (CAESAR's "Srf.Node"), 0 = none
    public int OwnerUid;
    public int Slot = -1;
    public List<string> Raw;
}

// Forces / moments (FORCMNT: FX FY FZ MX MY MZ) or imposed displacements (DISPLMNT: DX DY DZ RX RY RZ) at a
// node, in up to 9 load vectors (CAESAR's F1..F9 / D1..D9). Job units: force, moment-input, length, degrees.
// A displacement DOF that is not imposed is NaN (CAESAR writes 9999.99: free); an imposed 0 holds the DOF.
// A force component not given is 0 (NaN when the file wrote 9999.99 there; it acts as 0).
public class PipeModelNodeLoad
{
    public const double Free = 9999.99;       // CAESAR's "not specified"
    public string Kind = "force";             // force | displacement
    public int Node;
    public readonly double[][] Vectors = new double[9][];
    public string Source = "";                // which config item made it (messages)
    public int OwnerUid;                      // element whose block held it in the source (0 = none)
    public int Slot = -1;
    public List<string> Raw;                  // the slot as written (node line + 9 vector lines); null = new

    public PipeModelNodeLoad() : this("force") { }

    public PipeModelNodeLoad(string kind)
    {
        Kind = kind;
        for (int v = 0; v < 9; v++)
        {
            Vectors[v] = new double[6];
            if (IsDisplacement) for (int k = 0; k < 6; k++) Vectors[v][k] = double.NaN;
        }
    }

    public bool IsDisplacement { get { return string.Equals(Kind, "displacement", StringComparison.OrdinalIgnoreCase); } }
    public string Section { get { return IsDisplacement ? "DISPLMNT" : "FORCMNT"; } }

    // a vector carries something: an imposed DOF (displacement) or a non-zero component (force)
    public bool Used(int v)
    {
        foreach (double x in Vectors[v]) if (IsDisplacement ? !double.IsNaN(x) : !double.IsNaN(x) && x != 0) return true;
        return false;
    }
}

// Uniform loads on an element (UNIFORM): 9 vectors (CAESAR's U1..U9) of UX UY UZ, each in force per length
// (job units) or in g's (G). A run of loaded elements has one block per element, the same values in each.
public class PipeModelUniform
{
    public readonly double[][] Vectors = new double[9][];
    public readonly bool[] G = new bool[9];
    public int Seq;
    public List<string> Raw;                  // the block as written (6 lines); null = new

    public PipeModelUniform() { for (int v = 0; v < 9; v++) Vectors[v] = new double[3]; }

    public bool Used(int v) { return Vectors[v][0] != 0 || Vectors[v][1] != 0 || Vectors[v][2] != 0; }

    public bool SameAs(PipeModelUniform o)
    {
        if (o == null) return false;
        for (int v = 0; v < 9; v++)
        {
            if (G[v] != o.G[v] && (Used(v) || o.Used(v))) return false;
            for (int k = 0; k < 3; k++) if (Vectors[v][k] != o.Vectors[v][k]) return false;
        }
        return true;
    }
}

// Wind / wave data on an element (WIND), one block per element like uniform loads.
public class PipeModelWind
{
    public int Type;                          // 0 wind, 1 wave, 2 off
    public double Shape;                      // wind shape factor (wave: drag coefficient)
    public double AddedMass, Lift, MarineGrowth, MarineGrowthDensity;   // wave only
    public int Seq;
    public List<string> Raw;                  // the block as written (1 line); null = new

    public bool SameAs(PipeModelWind o)
    {
        return o != null && Type == o.Type && Shape == o.Shape && AddedMass == o.AddedMass && Lift == o.Lift &&
               MarineGrowth == o.MarineGrowth && MarineGrowthDensity == o.MarineGrowthDensity;
    }

    public static string TypeName(int t) { return t == 0 ? "wind" : t == 1 ? "wave" : t == 2 ? "off" : "type " + t.ToString(System.Globalization.CultureInfo.InvariantCulture); }
}

public class PipeModelCoord
{
    public int Node;
    public double X, Y, Z;                    // length units (written in the compound-length unit: feet in English)
    public string Raw;                        // the entry as written; null = new
    public double[] ImportedXyz;              // X Y Z as imported: an unchanged one keeps its text as written
}

// A connection between two nodes, per degree of freedom: rigid, free, or a gap and / or a stiffness. Written
// as CNODE restraints (node -> cnode). Frame "pipe": axial, shear (both), torsion, bending (both) along the
// pipe axis at the node (the element ending at the node, else the one starting at the cnode); frame
// "global": X Y Z RX RY RZ. A coupling, a hinge, a slip joint, a rigid link are joints.
public class PipeModelJoint
{
    public int Uid;
    public string Kind = "coupling";          // coupling | hinge | slip | ball | link | custom (a label)
    public int Node, CNode;
    public string Frame = "pipe";             // pipe | global
    public readonly List<PipeModelJointDof> Dofs = new List<PipeModelJointDof>();   // a DOF not listed is FREE
    public double Sif;                        // > 0: a user SIF at the node
    public string Tag = "";
}

public class PipeModelJointDof
{
    public string Dof = "";                   // pipe: axial shear torsion bending; global: X Y Z RX RY RZ
    public double Gap;                        // free travel each way (length units, deg for rotations); 0 = none
    public double Stiffness;                  // after the gap; 0 = rigid
}

// What an imported (or seed) file carries besides the model: its global sections as written, so the writer
// gives them back unchanged.
public class PipeModelSource
{
    public string FileName = "";              // the source file's name (no folder)
    public double Version;                    // CAESAR version on the VERSION line (15.01)
    public string VersionLine = "";
    public int CodePage = 1252;
    public string LineEnding = "\r\n";
    public bool FinalNewline = true;
    public bool Seed;                         // only the global sections apply (a new model's settings)
    public readonly List<string> Prelude = new List<string>();
    public readonly List<string> SectionOrder = new List<string>();
    public readonly Dictionary<string, string> Headers = new Dictionary<string, string>();          // "#$" lines as written
    public readonly Dictionary<string, List<string>> Sections = new Dictionary<string, List<string>>();   // VERSION CONTROL UNITS AUX_DATA, unknown ones
    public readonly Dictionary<string, int> LinesPerBlock = new Dictionary<string, int>();     // ELEMENTS (record), RESTRANT/slot ...
    public readonly Dictionary<string, List<string>> Trailing = new Dictionary<string, List<string>>();   // lines past a built section's blocks / entries (blank lines at the end of a file ...)
    public List<string> MiscelMaterialLines = new List<string>();   // MISCEL_1 material-number lines as written
    public List<string> MiscelTail = new List<string>();            // the rest: nozzles, hangers, the 4 execution-option lines
    public string CoordsCountLine;            // the COORDS count line as written
    public string OriginalTitle = "", OriginalNotes = "";
    public int ElementCount;                  // elements in the source (the material lines' count)

    public int Lines(string key, int dflt)
    {
        int v;
        return LinesPerBlock.TryGetValue(key, out v) && v > 0 ? v : dflt;
    }
}

// CAESAR restraint type codes: the neutral file's table in CAESAR II's own documentation (1-62; codes 1-9,
// 14 and 15 also seen in CAESAR-written files). Types the builder and the workbook write: the plain ones
// (BuildTypes); rods, bilinear supports and springs need data the restraint record does not hold.
public static class PipeModelCodes
{
    static readonly string[] Names =
    {
        null, "ANC", "X", "Y", "Z", "RX", "RY", "RZ", "GUI", "LIM", "XSNB", "YSNB", "ZSNB", "+X", "+Y", "+Z", "-X", "-Y", "-Z",
        "+RX", "+RY", "+RZ", "-RX", "-RY", "-RZ", "+LIM", "-LIM", "XROD", "YROD", "ZROD", "+XROD", "+YROD", "+ZROD", "-XROD", "-YROD", "-ZROD",
        "X2", "Y2", "Z2", "RX2", "RY2", "RZ2", "+X2", "+Y2", "+Z2", "-X2", "-Y2", "-Z2", "+RX2", "+RY2", "+RZ2", "-RX2", "-RY2", "-RZ2",
        "XSPR", "YSPR", "ZSPR", "+XSNB", "+YSNB", "+ZSNB", "-XSNB", "-YSNB", "-ZSNB"
    };

    public static readonly Dictionary<string, int> Restraint = BuildTable();

    static Dictionary<string, int> BuildTable()
    {
        var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < Names.Length; i++) d[Names[i]] = i;
        return d;
    }

    // the types a config or a workbook may give (their records hold everything they need)
    public static readonly string[] BuildTypes =
    {
        "ANC", "X", "Y", "Z", "RX", "RY", "RZ", "GUI", "LIM", "+X", "+Y", "+Z", "-X", "-Y", "-Z",
        "+RX", "+RY", "+RZ", "-RX", "-RY", "-RZ", "+LIM", "-LIM", "XSNB", "YSNB", "ZSNB"
    };

    public static string NameOf(int code) { return code > 0 && code < Names.Length ? Names[code] : null; }

    // CAESAR's English UNITS factors (the fixture's block): what PipeModel.Factor gives without UnitFactors
    public static readonly double[] EnglishFactors =
    {
        1, 1, 1, 1, 8.33333e-2, 1, 1, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 144, 8.33333e-2, 8.33333e-2, 1, 1
    };

    // a rotational restraint (RX, +RY, RZ2 ...): stiffness in the rotational unit, gap in degrees
    public static bool IsRotation(string type)
    {
        string t = (type ?? "").Trim().ToUpperInvariant().TrimStart('+', '-');
        return t.Length >= 2 && t[0] == 'R' && (t[1] == 'X' || t[1] == 'Y' || t[1] == 'Z');
    }

    public static bool IsRotationCode(int code) { return IsRotation(NameOf(code)); }

    // The cosines CAESAR 15.01 writes for a restraint given by its type alone (the fixture's 112 restraints):
    // ANC none (0 0 0); every other type its axis (X RX 1 0 0, Y RY +Y 0 1 0, Z RZ 0 0 1); GUI and LIM a dummy
    // 1 0 0 (CAESAR takes the pipe axis). The types not seen in a file yet follow their axis the same way.
    public static double[] DefaultCosines(string type)
    {
        string t = (type ?? "").Trim().ToUpperInvariant().TrimStart('+', '-');
        if (t.Length == 0 || t == "ANC") return new double[3];
        if (t.StartsWith("R", StringComparison.Ordinal) && t.Length >= 2 && "XYZ".IndexOf(t[1]) >= 0) t = t.Substring(1);
        if (t.StartsWith("Y", StringComparison.Ordinal)) return new[] { 0.0, 1.0, 0.0 };
        if (t.StartsWith("Z", StringComparison.Ordinal)) return new[] { 0.0, 0.0, 1.0 };
        return new[] { 1.0, 0.0, 0.0 };
    }

    // the DOF of a load vector component: 0..5 = X Y Z RX RY RZ
    public static readonly string[] ForceDofs = { "FX", "FY", "FZ", "MX", "MY", "MZ" };
    public static readonly string[] DisplacementDofs = { "DX", "DY", "DZ", "RX", "RY", "RZ" };
}
