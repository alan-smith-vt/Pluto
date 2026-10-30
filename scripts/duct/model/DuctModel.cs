using System;
using System.Collections.Generic;

// The duct converter's shared records (2026-10-30, split stage 1 of the DuctsToPluto refactor): plain public
// classes, moved out of the DuctsToPluto partial class so the pieces can be split into their own classes.
//   DuctRow      one Navisworks ducts.csv row        DuctOpening  a measured connector (probe / mesh)
//   RvEl         one Revit element (Pluto Connectors) RvConn     one of its connectors
//   CEdge        one clean-model edge
public class DuctRow
{
    public string Guid, NavisId, Cat, System, SizeText, Name, Shape;
    public string Hidden = "";               // "self" / "ancestor" / "" (runs from 2026-10-06 on)
    public double HiddenGeom;                 // own geometry items hidden (NaN on older runs)
    public double W, H, D, T;                 // inches; NaN = unknown
    public double FitA, FitB;                 // fitted cross-section, inches (fallback when no size)
    public double FitLen;                     // fitted length, feet
    public double NameW = double.NaN, NameH = double.NaN, NameD = double.NaN;   // size read from the Name (fabrication parts)
    public double[] Min, Max, E1, E2;         // feet; null = absent
    public string Room = "", RefLevel = "", Oid = "", Cwp = "";   // Custom room number etc. (runs from 2026-10-08 on)
    public string RunName = "", Service = ""; // IfcObjectProperties.RunName "A-<bldg>-<service>-DUCT-<n>" (runs from 2026-10-07 on)
    public bool Out;                          // service not in the requested list: not drawn (row kept so row numbers stay aligned)
}

// ---- DuctOpening probe (2026-10-08) ------------------------------------------------------------------
// Fabrication ductwork has no connector data (no Revit, SP3D models it as equipment), so connectors
// must come from the mesh. This probe asks whether they can: per fabrication part (optionally one
// Custom room number), weld its own triangles, chain the boundary edges (edges on one triangle) into
// loops, and describe each loop: centre, plane normal, round (diameter) or rectangular (a x b), planarity.
// An open-ended skin should give 2 loops on a straight / elbow, 3 on a tee; a closed solid gives none.
// Each opening is then matched to the nearest opening of another part: coincident and facing (joint),
// coaxial within 6 in (slip joint / gap), or unmatched. Nothing here builds the graph yet.
// Writes <out>.openings.csv, <out>.probe.txt and a viewer overlay: one 6 in stub per opening along its
// normal, groups by shape and by match status.
public class DuctOpening
{
    public int Row; public string Shape; public double A, B, Dia;   // inches
    public double[] C, N; public double Planar, Perim;              // C feet; Planar / Perim inches
    public double[] Ua;                                            // rect: in-plane unit direction of side A (null for round)
    public bool SizeOk; public string SizeBand = "no stated size"; public double Near = double.NaN, Dot = double.NaN; public int NearRow; public string Status = "";
    public double DA = double.NaN, DB = double.NaN;                 // measured minus stated (larger / smaller side), inches
    public DuctOpening Mate;                                            // the matched connector (joint / near), null otherwise
    public List<double[]> Cands;                                   // every ring centred at this end: { round 1/0, A, B, Dia } (MeshConnectors)
    public List<DuctOpening> Members;                                  // rings merged into this connector, smallest first
    public int Rings = 1; public string Outer = "";                // rings merged into this connector; largest ring's size
}

public class RvEl
{
    public string Id, Uid, Guid, GuidCalc, Cat, Fam, Type, Size, System, Level, Match = "", RunName = "";   // RunName: Revit parameter (runs from 2026-10-12 on)
    public bool Fab, Only;
    public int LocRow = -1;   // Revit-only: the Navisworks-only row it lies inside (same part, other IfcGUID)
    public List<RvConn> Conns = new List<RvConn>();
    public List<int> Beams = new List<int>();
    public List<int[]> Segs = new List<int[]>();   // SAP frames: { node i, node j, connector whose size it takes (-1 = the element's) }
    public List<string> Flags = new List<string>(), Detail = new List<string>();
    public double[] C, P;     // centre: Revit internal / Navisworks frame (feet)
    public string Kind = "";  // DuctRunGraph: straight / bend / transition / branch / end / equipment
    public int Run = -1, Seg = -1;   // DuctRunGraph run and segment ids (-1 = none)
    public int Sys = -1; public string SysName = "";   // DuctsSystems: connected component and its name
    public double Thk = double.NaN;   // wall thickness (in) from its Navisworks row's WallThk_in; NaN = not stated
    public double[] Voted;    // the segment's voted size { round 1/0, big side or dia, small side } (null = none)
}

public class RvConn
{
    public RvEl Owner;
    public string Key, Type;
    public double[] P, D;     // Navisworks frame after the fit; D = outward (BasisZ)
    public double[] WX;       // Width direction (BasisX; runs from 2026-10-12 on, else null), after the fit
    public string Shape;      // Rectangular / Round / Oval
    public double W, H, Dia;  // inches, NaN = n/a
    public List<string> To = new List<string>();
    public DuctOpening Mesh;      // the probe's connector on the matched Navisworks row at this end (mesh-measured size)
    public int MeshRow = -1;  // the row (0-based) that mesh connector belongs to
}

public class CEdge
{
    public int A, B; public bool Rigid; public double[] Dim, DimB, Y;   // Dim / DimB: { round 1/0, W, H, Dia } inches
    public List<RvEl> Src = new List<RvEl>(); public HashSet<string> Flags = new HashSet<string>(StringComparer.Ordinal);
    public string Source = "revit"; public string NavisId = ""; public bool Dead;
    public double Thk = double.NaN;   // wall thickness (in) from Navisworks; NaN = not stated (frames.csv: 0.06 default)
    public string Span = "";        // unsupported span id US<n> (SupportPlacer.cs), frames.csv Span
    public int Sys { get { return Src.Count > 0 ? Src[0].Sys : -1; } }
}
