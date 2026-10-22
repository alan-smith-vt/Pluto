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
// Carried data (an imported .cii): everything the model does not edit stays as written -- the element
// records, each block's lines and its number in CAESAR's own order (Seq), the restraint / SIF / displacement
// slots with the element block that held them, the load and other blocks the model does not read
// (Carried), and the file's VERSION, CONTROL, UNITS and MISCEL_1 tail (Source). The writer overlays only
// the fields that changed, so an unedited import is written back identically (CaesarPipeImport checks it).

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
    public readonly List<PipeModelNodeItem> NodeItems = new List<PipeModelNodeItem>();   // carried node slots (DISPLMNT)
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
    public string Kind = "pipe";                      // for summaries: pipe, bend, valve, flange, ...
    public List<string> Raw;                          // the record as written (import); null = new
    // blocks this element owns and the model does not read (FORCMNT UNIFORM WIND OFFSETS ALLOWBLS NODENAME
    // FLANGES EQUIPMNT), as written, with their number in the file
    public readonly Dictionary<string, PipeModelBlock> Carried = new Dictionary<string, PipeModelBlock>();
    // this element's RESTRANT / SIF&TEES / DISPLMNT block as written: its number orders the blocks, its
    // lines give back the slots no item fills
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
    public string Type = "";                  // ANC X Y Z RX RY RZ GUI LIM +X +Y +Z -X -Y -Z (OTHER(n) when imported unconfirmed)
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

// a node slot the model carries without reading it (an imposed displacement: DISPLMNT)
public class PipeModelNodeItem
{
    public string Section = "DISPLMNT";
    public int Node;
    public int OwnerUid;
    public int Slot = -1;
    public List<string> Raw = new List<string>();
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

// CAESAR restraint type codes. Confirmed by CAESAR-written files (the fixture, four 11.00 jobs):
// 1 ANC, 2-4 X/Y/Z, 5-7 RX/RY/RZ, 8 GUI, 9 LIM, 14 +Y, 15 +Z. +X and the negative one-ways follow
// the same list (+X between ZROD and +Y; -X -Y -Z after +Z) but no CAESAR file has shown them yet:
// the builder writes them with a warning until an import confirms them.
public static class PipeModelCodes
{
    public static readonly Dictionary<string, int> Restraint = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        { "ANC", 1 }, { "X", 2 }, { "Y", 3 }, { "Z", 4 }, { "RX", 5 }, { "RY", 6 }, { "RZ", 7 },
        { "GUI", 8 }, { "LIM", 9 }, { "+X", 13 }, { "+Y", 14 }, { "+Z", 15 }, { "-X", 16 }, { "-Y", 17 }, { "-Z", 18 }
    };
    public static readonly HashSet<string> Unconfirmed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "+X", "-X", "-Y", "-Z" };

    // CAESAR's English UNITS factors (the fixture's block): what PipeModel.Factor gives without UnitFactors
    public static readonly double[] EnglishFactors =
    {
        1, 1, 1, 1, 8.33333e-2, 1, 1, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 144, 8.33333e-2, 8.33333e-2, 1, 1
    };

    public static bool IsRotation(string type)
    {
        return string.Equals(type, "RX", StringComparison.OrdinalIgnoreCase) || string.Equals(type, "RY", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(type, "RZ", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsRotationCode(int code) { return code >= 5 && code <= 7; }

    // The cosines CAESAR 15.01 writes for a restraint given by its type alone (the fixture's 112 restraints):
    // ANC none (0 0 0); every other type its axis (X RX 1 0 0, Y RY +Y 0 1 0, Z RZ 0 0 1); GUI and LIM a dummy
    // 1 0 0 (CAESAR takes the pipe axis). The one-ways not seen yet (+X -X -Y -Z) follow their axis.
    public static double[] DefaultCosines(string type)
    {
        string t = (type ?? "").Trim().ToUpperInvariant().TrimStart('+', '-');
        if (t.Length == 0 || t == "ANC") return new double[3];
        if (t == "Y" || t == "RY") return new[] { 0.0, 1.0, 0.0 };
        if (t == "Z" || t == "RZ") return new[] { 0.0, 0.0, 1.0 };
        return new[] { 1.0, 0.0, 0.0 };
    }
}
