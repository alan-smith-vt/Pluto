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
    public readonly List<PipeModelHanger> Hangers = new List<PipeModelHanger>();   // spring hangers (MISCEL_1), in CAESAR's order
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
    // Element row 8, values 5 and 6: CAESAR's element orientation angles (to end, from end; its items 47 and 48,
    // "FEATools data only"), which CAESAR 15.01 writes on the elements meeting at a welding tee (90 / 180 / 315 in
    // the fixture). 9999.99 = not set, what the builder writes; kept so a CAESAR file round-trips exactly.
    public double TeeAngle1 = 9999.99, TeeAngle2 = 9999.99;
    // Design data, rows 6-9 of the record past the densities (CAESAR's items 33-46 and 49-53): 0 = not given
    public double MillTolMinus, MillTolPlus;          // mill tolerance, % of the wall (33, 34)
    public double SeamWeld;                           // 1 = seam welded (35)
    public double HydroPressure;                      // hydrotest pressure (36)
    public readonly double[] EHot = new double[9];    // hot elastic modulus for T1-T9 (37-45; 0 = from the material)
    public double WlFactor;                           // weld strength reduction factor (46)
    public double CladThickness, CladDensity, InsCladWeight, RefractoryThickness, RefractoryDensity;   // 49-53

    // the design data in record order: rows 6-9 from their third, first, first and first value (the orientation angles
    // between them stay apart)
    public double[] Design()
    {
        return new[] { MillTolMinus, MillTolPlus, SeamWeld, HydroPressure, EHot[0], EHot[1], EHot[2], EHot[3], EHot[4], EHot[5], EHot[6], EHot[7], EHot[8], WlFactor,
                       CladThickness, CladDensity, InsCladWeight, RefractoryThickness, RefractoryDensity };
    }

    public void SetDesign(double[] v)
    {
        Func<int, double> at = i => v != null && i < v.Length && !double.IsNaN(v[i]) ? v[i] : 0;
        MillTolMinus = at(0); MillTolPlus = at(1); SeamWeld = at(2); HydroPressure = at(3);
        for (int k = 0; k < 9; k++) EHot[k] = at(4 + k);
        WlFactor = at(13); CladThickness = at(14); CladDensity = at(15); InsCladWeight = at(16); RefractoryThickness = at(17); RefractoryDensity = at(18);
    }

    // where each design value sits in the record: {real row, value}
    public static readonly int[][] DesignAt =
    {
        new[] { 5, 2 }, new[] { 5, 3 }, new[] { 5, 4 }, new[] { 5, 5 },
        new[] { 6, 0 }, new[] { 6, 1 }, new[] { 6, 2 }, new[] { 6, 3 }, new[] { 6, 4 }, new[] { 6, 5 }, new[] { 7, 0 }, new[] { 7, 1 }, new[] { 7, 2 }, new[] { 7, 3 },
        new[] { 8, 0 }, new[] { 8, 1 }, new[] { 8, 2 }, new[] { 8, 3 }, new[] { 8, 4 }
    };
    public static readonly string[] DesignNames =
    {
        "mill tolerance -", "mill tolerance +", "seam weld", "hydro pressure", "E hot 1", "E hot 2", "E hot 3", "E hot 4", "E hot 5", "E hot 6", "E hot 7", "E hot 8", "E hot 9",
        "wL factor", "cladding thickness", "cladding density", "insulation + cladding weight", "refractory thickness", "refractory density"
    };
    public string Name = "";                          // element name (rigids: the component)
    public string LineNumber = "";                    // written on the first element of a line, carried forward by CAESAR
    public PipeModelBend Bend;                        // a bend at the TO end
    public PipeModelRigid Rigid;
    public PipeModelExpJoint ExpJoint;
    public PipeModelReducer Reducer;
    public PipeModelUniform Uniform;                  // uniform loads acting on this element (null = none); CAESAR carries
    public PipeModelWind Wind;                        // them forward, so an element's may come from a block before it (Raw null)
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
    // (like -2.0202 = "M" in the angle fields), not a length: a radius chosen by name in CAESAR's input, which
    // follows the pipe size. It is always carried as written, so CAESAR resolves it as before. Returns the code's
    // number (1-9), or 0 for an ordinary radius.
    public static int RadiusCode(double radius)
    {
        if (!(radius < 0)) return 0;
        double k = -radius / 1.0101;
        int n = (int)Math.Round(k);
        return n >= 1 && n <= 9 && Math.Abs(k - n) < 1e-4 ? n : 0;
    }

    // The codes as CAESAR 15.01 reads them, as multiples of the nominal pipe size: a probe job with one bend per
    // code, imported into CAESAR and exported again (2026-10-23). CAESAR writes Long back as the plain radius and
    // keeps the codes of 3D, Short and 5D. Codes 2 and 6-9 are no radius at all: CAESAR shows 0.
    public static readonly Dictionary<int, double> NamedRadii = new Dictionary<int, double> { { 1, 1.5 }, { 3, 3.0 }, { 4, 1.0 }, { 5, 5.0 } };

    public static bool NoRadiusCode(int code) { return code > 0 && !NamedRadii.ContainsKey(code); }

    // a coded radius in inches for a pipe of this OD (inches): 0 for a code CAESAR reads as no radius; NaN when
    // the OD is not a standard size
    public static double ResolveCode(int code, double odInches)
    {
        if (NoRadiusCode(code)) return 0;
        double f, nps = PipeBuilder.NominalOf(odInches);
        return NamedRadii.TryGetValue(code, out f) && !double.IsNaN(nps) ? f * nps : double.NaN;
    }

    public static string CodeName(int code)
    {
        double f;
        if (!NamedRadii.TryGetValue(code, out f)) return "code " + code + " (no radius)";
        return NameOf(f);
    }

    static string NameOf(double multiple)
    {
        return multiple == 1.5 ? "Long" : multiple == 1 ? "Short" : multiple.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "D";
    }

    // a radius by name -> its multiple of the nominal size (LR / Long 1.5, SR / Short 1, 3D, 5D); NaN for another text
    public static double NamedMultiple(string name)
    {
        string k = (name ?? "").Trim().ToUpperInvariant();
        return k == "LR" || k == "LONG" ? 1.5 : k == "SR" || k == "SHORT" ? 1.0 : k == "3D" ? 3.0 : k == "5D" ? 5.0 : double.NaN;
    }

    // the name a radius shows in the workbook: a confirmed code's (Short, 3D, 5D, Long); null for a plain radius or a
    // code CAESAR reads as no radius (kept as the number)
    public static string DisplayName(double radius)
    {
        int code = RadiusCode(radius);
        double f;
        return code > 0 && NamedRadii.TryGetValue(code, out f) ? NameOf(f) : null;
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
// (job units) or in g's (G). The model holds what acts on each element. CAESAR carries a block forward: it acts on
// its element and on every later one (input order) until another block changes it, a zero block ending it. The
// import gives an element without a block a copy of the load before it (Raw null); the writer puts a block only
// where the load starts or changes, and a zero block where it stops.
public class PipeModelUniform
{
    public readonly double[][] Vectors = new double[9][];
    public readonly bool[] G = new bool[9];
    public int Seq;
    public List<string> Raw;                  // the block as written (6 lines); null = new

    public PipeModelUniform() { for (int v = 0; v < 9; v++) Vectors[v] = new double[3]; }

    public bool Used(int v) { return Vectors[v][0] != 0 || Vectors[v][1] != 0 || Vectors[v][2] != 0; }

    // no load: no block, or a block with every vector zero (CAESAR's way to end a carried load)
    public static bool IsNone(PipeModelUniform u) { return u == null || Enumerable.Range(0, 9).All(v => !u.Used(v)); }

    // the same load acting (none is none however it is written)
    public static bool Same(PipeModelUniform a, PipeModelUniform b)
    {
        bool na = IsNone(a), nb = IsNone(b);
        return na || nb ? na == nb : a.SameAs(b);
    }

    // a copy for an element the load is carried onto (no block of its own)
    public PipeModelUniform Carry()
    {
        var c = new PipeModelUniform();
        for (int v = 0; v < 9; v++) { Array.Copy(Vectors[v], c.Vectors[v], 3); c.G[v] = G[v]; }
        return c;
    }

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

// Wind / wave data on an element (WIND), carried forward like uniform loads: an "off" block (type 2) ends it.
// CAESAR's input also ends wind with a shape factor of 0.
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

    public static bool IsNone(PipeModelWind x) { return x == null || x.Type == 2 || (x.Type == 0 && x.Shape == 0); }

    public static bool Same(PipeModelWind a, PipeModelWind b)
    {
        bool na = IsNone(a), nb = IsNone(b);
        return na || nb ? na == nb : a.SameAs(b);
    }

    public PipeModelWind Carry()
    {
        return new PipeModelWind { Type = Type, Shape = Shape, AddedMass = AddedMass, Lift = Lift, MarineGrowth = MarineGrowth, MarineGrowthDensity = MarineGrowthDensity };
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

// A spring hanger (MISCEL_1 hanger data, one per hanger node). CAESAR designs it from a hanger table when no
// spring rate is given; given a rate and an installed (cold) load it is a spring as given; given a constant-
// effort load, a constant support. Values in the job's units (rate: translational stiffness; loads: force;
// space and travel: length). The 11 data values are CAESAR's HGRDAT in its order.
public class PipeModelHanger
{
    public const double NoLimit = 9999.99;    // CAESAR's "not given" for the travel limit
    public int Node;
    public int CNode;                         // 0 = to ground
    public double Rate;                       // 1 spring rate; 0 = CAESAR designs the hanger
    public double LoadVariation = 25;         // 2 allowed load variation, %
    public double RigidCriteria;              // 3 rigid support displacement criteria (0 = CAESAR's default)
    public double Space;                      // 4 available space (0 = not given)
    public double ColdLoad;                   // 5 theoretical cold (installed) load
    public double HotLoad;                    // 6 hot load (CAESAR's, 0 on input)
    public double OperatingLoad;              // 7 user-defined operating load (variable springs)
    public double MaxTravel = NoLimit;        // 8 maximum allowed travel
    public double MultiLoad;                  // 9 multiple load case design option
    public double HardwareWeight;             // 10
    public double ConstantLoad;               // 11 constant-effort support load (CEFF)
    public string Tag = "", Guid = "";
    public int FreeNode1, FreeNode2;          // anchors freed while the hanger is designed (0 = none)
    public int FreeCode1, FreeCode2;          // what each frees: 1 Y, 2 XY, 3 ZY, 4 XYZ, 5 all
    public int Count;                         // hangers at the location (0 = one)
    public int Table;                         // hanger table (0 = the job's default)
    public int ShortRange = 1;                // 1 = short-range springs may be used
    public string Source = "";                // which config item made it (messages)
    public List<string> Raw;                  // its lines as written (2 data lines, tag, GUID); null = new
    public string FreeRaw;                    // its free-anchor line as written

    public double[] Data()
    {
        return new[] { Rate, LoadVariation, RigidCriteria, Space, ColdLoad, HotLoad, OperatingLoad, MaxTravel, MultiLoad, HardwareWeight, ConstantLoad };
    }

    public void SetData(double[] v)
    {
        Func<int, double> at = i => i < v.Length ? v[i] : 0;
        Rate = at(0); LoadVariation = at(1); RigidCriteria = at(2); Space = at(3); ColdLoad = at(4); HotLoad = at(5);
        OperatingLoad = at(6); MaxTravel = at(7); MultiLoad = at(8); HardwareWeight = at(9); ConstantLoad = at(10);
    }

    // what CAESAR does with it
    public string Kind
    {
        get { return ConstantLoad != 0 ? "constant" : Rate != 0 ? "spring" : "designed"; }
    }
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
    // the tail's parts, when the import could tell them apart: its nozzle lines first, then the hanger section
    // (TailHangers hangers in TailHangerLines lines), then the execution options. -1: not told apart, so the
    // tail is carried whole (and the model holds no hangers)
    public int TailNozzleLines = -1, TailHangerLines, TailHangers;
    public string CoordsCountLine;            // the COORDS count line as written
    public string OriginalTitle = "", OriginalNotes = "";
    public int ElementCount;                  // elements in the source (the material lines' count)

    public int Lines(string key, int dflt)
    {
        int v;
        return LinesPerBlock.TryGetValue(key, out v) && v > 0 ? v : dflt;
    }
}

// CAESAR restraint type codes: the neutral file's table in CAESAR II's own documentation (1-62; codes 1-26
// confirmed by CAESAR 15.01, which showed and exported a probe job's codes as written). Types the builder and
// the workbook write: the plain ones (BuildTypes, all confirmed); rods, bilinear supports and springs need data
// the restraint record does not hold.
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
