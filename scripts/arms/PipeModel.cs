using System;
using System.Collections.Generic;

// PipeModel  --  a piping stress model in CAESAR II's own terms, independent of any file layout: what the
// pipe builder (PipeBuilder, from a TOML config) produces and the writers consume (CaesarNeutralWriter
// now; a SAP2000 .s2k writer later). C# 5, one global batch: every type is prefixed PipeModel.
//
// Units are CAESAR's English set throughout: lengths, diameters and walls in inches, positions in inches
// (COORDS are written in feet), temperatures F, pressures psi, densities lb/cu.in., weights lb,
// translational stiffness lb/in., rotational in.lb/deg, rotational gaps deg.
//
// Elements are in CAESAR input order. An element's deltas run FROM -> TO with CAESAR's bend convention:
// the element carrying a bend runs to the tangent intersection point, its TO node being the bend's far
// point; the next element is measured from the tangent intersection point too. A node that no element
// has reached yet starts where the previous element ended (CAESAR's rule), unless it has a COORDS entry.

public class PipeModel
{
    public string Title = "";                 // VERSION "PROJECT:" line (the output file only)
    public string Notes = "";                 // VERSION "NOTES  :" line
    public bool ZUp;                          // CONTROL IZUP: false = Y up (CAESAR's default)
    public double Ambient = 70.0;             // F, installation temperature: thermal strain from T - ambient
    public string Code = "B31.1";             // piping code of the built-in allowables block
    public List<string> AllowablesBlock;      // a seed file's ALLOWBLS block, verbatim (null = built-in B31.1)
    public readonly List<PipeModelElement> Elements = new List<PipeModelElement>();
    public readonly List<PipeModelRestraint> Restraints = new List<PipeModelRestraint>();
    public readonly List<PipeModelSif> Sifs = new List<PipeModelSif>();
    public readonly List<PipeModelCoord> Coords = new List<PipeModelCoord>();
    public readonly Dictionary<int, double[]> Positions = new Dictionary<int, double[]>();   // node -> x y z (in), as built
    public readonly List<string> Warnings = new List<string>();
}

public class PipeModelElement
{
    public int From, To;
    public double Dx, Dy, Dz;                 // in
    public double Od, Wall, Insulation, Corrosion;   // in
    public readonly double[] T = new double[9];      // F, T1..T9 (0 = not used)
    public readonly double[] P = new double[9];      // psi, P1..P9
    public double Emod, Poisson, PipeDensity;        // 0 = from CAESAR's material database (by Material)
    public double InsulationDensity, FluidDensity;   // lb/cu.in.
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
    public string Kind = "pipe";                       // for summaries: pipe, bend, valve, flange, ...
}

public class PipeModelBend
{
    public double Radius;                     // in
    public int Type;                          // 0 = welded (CAESAR's default)
    public readonly List<double[]> AngleNodes = new List<double[]>();   // {angle (deg, -2.0202 = mid point), node}; up to 3
    public int MiterPoints;
    public double FittingThickness, KFactor;  // 0 = CAESAR's default (the pipe wall, the code's k)
    public const double MidPoint = -2.0202;   // CAESAR's code for the "M" (mid point) bend node
}

public class PipeModelRigid
{
    public double Weight;                     // lb
    public int Type;                          // 0 unspecified, 1 valve, 2 flange, 3 flange pair, 4 flange + valve
}

public class PipeModelExpJoint
{
    public double AxialStiffness, TransverseStiffness, BendingStiffness, TorsionStiffness, EffectiveId;   // lb/in, lb/in, in.lb/deg, in.lb/deg, in
}

public class PipeModelReducer
{
    public double Od2, Wall2;                 // in: the TO end; the FROM end is the element's own Od / Wall
    public double Alpha, R1, R2;              // 0 = CAESAR computes them
}

public class PipeModelRestraint
{
    public int Node;
    public string Type = "";                  // ANC X Y Z RX RY RZ GUI LIM +X +Y +Z -X -Y -Z
    public int Code;                          // CAESAR's type code
    public double Stiffness;                  // 0 = rigid (written as 1e12)
    public double Gap;                        // in, or deg for a rotation
    public double Mu;                         // friction coefficient
    public int CNode;
    public double[] Cosines;                  // null = along the global axis of the type (written as CAESAR does: PipeModelCodes.DefaultCosines)
    public string Tag = "";
    public string Source = "";                // which config item made it (messages)
}

public class PipeModelSif
{
    public int Node;
    public int Type;                          // 0 = user SIFs only, 3 = welding tee
    public double SifIn, SifOut;              // 0 = from the code
    public int SurfaceNode;                   // a tee's surface node (CAESAR's "Srf.Node"), 0 = none
}

public class PipeModelCoord
{
    public int Node;
    public double X, Y, Z;                    // in (written in feet)
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

    public static bool IsRotation(string type)
    {
        return string.Equals(type, "RX", StringComparison.OrdinalIgnoreCase) || string.Equals(type, "RY", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(type, "RZ", StringComparison.OrdinalIgnoreCase);
    }

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
