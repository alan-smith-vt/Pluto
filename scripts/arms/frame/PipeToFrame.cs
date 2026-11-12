using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

// PipeToFrame  --  a CAESAR II model (PipeModel: a .cii, a TOML build, the CAESAR workbook) -> a SAP2000 frame model,
// the structural check model of the pipe (2026-11-12; vault/format/pipe-to-frame.md): the same pipe, loads and
// supports, for member forces and support reactions. No code checks: SIFs, allowables and code stresses stay in CAESAR.
// The model is written as SAP's own .s2k text and read back (FrameS2k), so every Build-Frame output follows: the .s2k,
// the frame workbook, the preview, the STAAD .std. Seeded once: edits go on in the frame workbook.
//
// Mapping (phase 1, the linear model; a nonlinear support refuses the model, every one named):
//   geometry   CaesarGeometry on the model's own .cii text (CAESAR's rules: bends to the tangent intersection, CNODE
//              placement). A Y-up job is turned to SAP's Z up: (x, y, z) -> (x, -z, y). Joints are CAESAR's node numbers;
//              bend points <to>N / <to>F / <to>A1.., reducer steps <to>R1.. Zero-length pieces join their ends.
//   pipe       a frame per piece, SAP's Pipe section (OD x wall) of the element's material; bends as chords of at most
//              ChordDeg through every bend node, I22 / I33 divided by the bend's flexibility factor k (CAESAR's k when
//              given, else 1.65 / h with the flanged-end factor h^(1/6) or h^(1/3), at least 1), weight scaled from the
//              chords to the arc; reducers as ReducerSteps stepped frames; rigids with the wall RigidWallFactor times
//              (the bore kept), weightless sections, their weight as loads; a finite expansion joint as a General
//              section giving its axial, bending and torsion stiffness (bending from the transverse as L^2 / 12, as for
//              a bellows), a zero-length one as a link.
//   materials  E, Poisson, density and alpha per CAESAR material (PipeModel.Materials; an element's own E / Poisson /
//              density wins, as in CAESAR); a material not listed takes PipeMaterialDefaults, with a warning.
//   weights    W_PIPE: self weight (the material's unit weight) and the rigids' weights; W_FLUID: contents;
//              W_INSUL: insulation, cladding, refractory (1.75 times on a rigid, as CAESAR). CAESAR's W is the three.
//   thermal    Tn: a temperature load T - ambient per frame (a value under CAESAR's alpha tolerance is an expansion,
//              given as that strain over alpha).
//   pressure   Pn: the expansion joints' thrust, P x the effective area, at both ends (CAESAR's other pressure effects
//              are options it has off by default).
//   loads      Fn joint forces, Dn ground displacements (the DOF held in every case), Un distributed loads (or g's of
//              the frame's weight), H the hangers' installed loads. Wind is CAESAR's load-case data: not written.
//   supports   rigid two-way restraints as joint restraints (in joint local axes when skewed); stiffnesses as joint
//              springs; to a CNODE a linear link; spring hangers as springs with their installed load in H, constant
//              ones as the load; custom nozzles as springs along the nozzle. Snubbers act in dynamic cases only.
//   cases      CAESAR's recommended set: OPE (W + Tn + Dn + Pn + F1 + H) per thermal set, SUS (W + P1 + F1 + H), EXP =
//              OPE - SUS as a Linear Add combination.
// C# 5, one global batch (prefix Frame / Pipe).

public sealed class PipeToFrameRefusal : Exception
{
    public readonly List<string> Items;
    public PipeToFrameRefusal(List<string> items)
        : base("PipeToFrame: " + items.Count.ToString(CultureInfo.InvariantCulture) + " item(s) the check model cannot hold yet: " + string.Join("; ", items.Take(8).ToArray()) + (items.Count > 8 ? "; ..." : ""))
    { Items = items; }
}

public static class PipeToFrame
{
    public const double ChordDeg = 22.5;          // the largest bend chord
    public const int ReducerSteps = 10;           // stepped frames per reducer (CAESAR's stepping: to be confirmed)
    public const double RigidWallFactor = 10;     // CAESAR's rigid: ten times the wall, the bore kept
    public const double RigidInsulation = 1.75;   // CAESAR's rigid: 1.75 times the insulation of the same pipe
    public const double AlphaTolerance = 0.05;    // CAESAR's: a temperature under it is a thermal expansion (length / length)
    public const double RigidStiffness = 1e11;    // English lb/in (in.lb/deg): at or above it a CAESAR stiffness is rigid
    const double G = 386.0885826771654;           // in/s^2

    // the frame model (its source the generated .s2k text); warnings: what the user must check (default materials ...);
    // notes: what the check model leaves out or approximates
    public static FrameModel Build(PipeModel m, string sapVersion, List<string> warnings, List<string> notes)
    {
        return FrameS2k.Read(S2kDoc.ParseText(Text(m, sapVersion, warnings, notes)));
    }

    // the pipe model of an input Build-Frame seeds from: a .cii, a TOML build, a .pipe.json or a CAESAR workbook; the
    // readers' warnings into warnings
    public static PipeModel LoadPipe(string path, List<string> warnings)
    {
        string p = (path ?? "").ToLowerInvariant();
        if (p.EndsWith(".toml"))
        {
            var b = PipeBuilder.Build(path, null);
            if (warnings != null) warnings.AddRange(b.Warnings);
            return b.Model;
        }
        if (p.EndsWith(".pipe.json")) return PipeModelJson.Read(System.IO.File.ReadAllText(path));
        if (p.EndsWith(".xlsx"))
        {
            var wb = PipeWorkbook.Read(path);
            if (warnings != null) warnings.AddRange(wb.Warnings);
            return wb.Model;
        }
        var imp = CaesarPipeImport.FromCii(path);
        if (warnings != null) warnings.AddRange(imp.Warnings.Select(w => "reader: " + w));
        return imp.Model;
    }

    public static string Text(PipeModel m, string sapVersion, List<string> warnings, List<string> notes)
    {
        var s = new Seed(m, warnings ?? new List<string>(), notes ?? new List<string>());
        return s.Run(string.IsNullOrEmpty(sapVersion) ? "22.0.0" : sapVersion);
    }

    // ------------------------------------------------------------------ the seed
    sealed class Dof
    {
        public double[] Dir;                      // SAP global, unit
        public bool Rot, Rigid;
        public double K;                          // lb/in, or in.lb/rad
        public string From = "";
    }

    sealed class LinkSpec { public string I, J; public readonly List<Dof> Dofs = new List<Dof>(); public string Why = ""; }

    sealed class FrameSpec
    {
        public string Label, I, J, Section, SectionType = "Pipe", Mat;
        public double[] Mods;                     // AMod AS2Mod AS3Mod JMod I22Mod I33Mod MassMod WeightMod; null = none
        public double Length;                     // in
        public double WeightPerIn;                // metal (or a rigid's own) weight per length, lb/in: for g loads
        public int Element;                       // cm element index
        public readonly List<string> Groups = new List<string>();
        public readonly Dictionary<string, double> Gravity = new Dictionary<string, double>();   // pattern -> lb/in down
    }

    sealed class MatSpec { public string Name; public int Number; public double E, Nu, Rho, Alpha; }   // psi, -, lb/in^3, /F

    sealed class Seed
    {
        readonly PipeModel M;
        readonly List<string> Warnings, Notes;
        readonly List<string> Refusals = new List<string>();
        readonly Dictionary<string, List<string>> Nonlinear = new Dictionary<string, List<string>>();
        CaesarModel Cm;
        CaesarPipeGraph Gr;
        List<PipeModelElement> Order;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // units: job -> English by the model's factors; English -> the output set
        double fLen, fDia, fWall, fForce, fMom, fPress, fMod, fPipeD, fInsD, fFluidD, fTK, fRK, fUni, tScale, tOff;
        bool si;
        double oL = 1, oF = 1, oT = 1;

        // the parts of the file
        readonly Dictionary<int, string> JointOfVertex = new Dictionary<int, string>();
        readonly Dictionary<string, double[]> JointPos = new Dictionary<string, double[]>();   // SAP global, in
        readonly List<string> JointOrder = new List<string>();
        readonly HashSet<string> Labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly List<FrameSpec> Frames = new List<FrameSpec>();
        readonly Dictionary<string, MatSpec> Mats = new Dictionary<string, MatSpec>();
        readonly List<MatSpec> MatOrder = new List<MatSpec>();
        readonly Dictionary<string, double[]> PipeSecs = new Dictionary<string, double[]>();      // name -> {od, t} in
        readonly Dictionary<string, string> SecMat = new Dictionary<string, string>();
        readonly Dictionary<string, double[]> GenSecs = new Dictionary<string, double[]>();       // name -> {od, A, J, I}
        readonly List<string> SecOrder = new List<string>();
        readonly Dictionary<string, List<Dof>> Grounded = new Dictionary<string, List<Dof>>();
        readonly Dictionary<string, bool[]> Imposed = new Dictionary<string, bool[]>();           // joint -> SAP global DOFs held for Dn
        readonly Dictionary<string, LinkSpec> Links = new Dictionary<string, LinkSpec>();
        readonly List<string> LinkOrder = new List<string>();
        readonly List<string[]> LinkRows = new List<string[]>();          // {label, I, J, prop, angle}
        readonly List<KeyValuePair<string, List<string[]>>> LinkProps = new List<KeyValuePair<string, List<string[]>>>();   // name -> DOF rows {dof, fixed, k}
        readonly List<string> RestraintRows = new List<string>(), AxesRows = new List<string>(), SpringRows = new List<string>();
        readonly List<string> GroundRows = new List<string>(), DistRows = new List<string>(), TempRows = new List<string>();
        readonly HashSet<string> PatternsUsed = new HashSet<string>();
        readonly Dictionary<string, List<string[]>> GroupMembers = new Dictionary<string, List<string[]>>();
        readonly List<string> GroupOrder = new List<string>();
        readonly Dictionary<string, double[]> ForceAt = new Dictionary<string, double[]>();         // pattern|joint -> 6 (lb, in.lb)
        readonly List<string> ForceKeys = new List<string>();
        readonly SortedSet<int> StrainVectors = new SortedSet<int>();
        readonly HashSet<string> StrainFrames = new HashSet<string>();

        public Seed(PipeModel m, List<string> warnings, List<string> notes) { M = m; Warnings = warnings; Notes = notes; }

        // ---------------- small helpers
        static string N(double v) { return FrameModel.Fmt(v); }
        static string Q(string v) { return v.IndexOfAny(new[] { ' ', '"', '\'', '(', ')', ',' }) >= 0 ? "\"" + v.Replace("\"", "'") + "\"" : v; }
        static double[] Sub(double[] a, double[] b) { return new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] }; }
        static double Dot(double[] a, double[] b) { return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]; }
        static double Len(double[] a) { return Math.Sqrt(Dot(a, a)); }
        static double[] Cross(double[] a, double[] b) { return new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] }; }
        static double[] Unit(double[] a) { double l = Len(a); return l < 1e-12 ? null : new[] { a[0] / l, a[1] / l, a[2] / l }; }
        static double[] Scale(double[] a, double k) { return new[] { a[0] * k, a[1] * k, a[2] * k }; }
        static bool Parallel(double[] a, double[] b) { return Math.Abs(Dot(a, b)) > 1 - 1e-6; }
        static string I(int v) { return v.ToString(Inv); }
        double Fac(int i) { double f = M.Factor(i); return Math.Abs(f) < 1e-30 || double.IsNaN(f) ? 1 : f; }
        // CAESAR's axes -> SAP's (Z up)
        double[] R(double[] v) { return M.ZUp ? new[] { v[0], v[1], v[2] } : new[] { v[0], -v[2], v[1] }; }
        double[] Up { get { return M.ZUp ? new[] { 0.0, 0, 1 } : new[] { 0.0, 1, 0 }; } }   // CAESAR's
        void Refuse(string s) { if (!Refusals.Contains(s)) Refusals.Add(s); }
        void Note(string s) { if (!Notes.Contains(s)) Notes.Add(s); }
        void Warn(string s) { if (!Warnings.Contains(s)) Warnings.Add(s); }
        void NonLin(string kind, string who) { List<string> l; if (!Nonlinear.TryGetValue(kind, out l)) { l = new List<string>(); Nonlinear[kind] = l; } l.Add(who); }
        void Group(string g, string type, string label)
        {
            List<string[]> l;
            if (!GroupMembers.TryGetValue(g, out l)) { l = new List<string[]>(); GroupMembers[g] = l; GroupOrder.Add(g); }
            if (!l.Any(x => x[0] == type && x[1] == label)) l.Add(new[] { type, label });
        }
        static string Safe(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in (s ?? "").Trim()) sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' ? c : '_');
            return sb.ToString();
        }
        // output units
        double OL(double inch) { return inch * oL; }
        double OF(double lb) { return lb * oF; }
        double OFL(double lbPerIn) { return lbPerIn * oF / oL; }
        double OM(double inLb) { return inLb * oF * oL; }
        double OS(double psi) { return psi * oF / (oL * oL); }
        double OW(double lbPerIn3) { return lbPerIn3 * oF / (oL * oL * oL); }
        double OKT(double lbPerIn) { return lbPerIn * oF / oL; }
        double OKR(double inLbPerRad) { return inLbPerRad * oF * oL; }
        double ODT(double degF) { return degF * oT; }
        double OA(double perF) { return perF / oT; }

        // ---------------- the run
        public string Run(string version)
        {
            Units();
            Order = CaesarNeutralWriter.InputOrder(M, null);
            Cm = CaesarNeutralReader.ParseText(CaesarNeutralWriter.ToText(M));
            Gr = CaesarGeometry.Build(Cm, ChordDeg);
            if (Cm.Elements.Count != Order.Count) throw new InvalidOperationException("PipeToFrame: the model's .cii holds " + I(Cm.Elements.Count) + " elements, the model " + I(Order.Count));
            foreach (string w in Gr.Warnings) Note("geometry: " + w);
            JoinVertices();
            ElementsToFrames();
            if (StrainFrames.Count > 0)
                Note(string.Join(", ", StrainVectors.Select(k => "T" + I(k)).ToArray()) + " on " + I(StrainFrames.Count) + " frame(s) are CAESAR's thermal expansions (under its alpha tolerance, length per length): written as the strain over the material's alpha, so the growth is CAESAR's whatever alpha is");
            Restraints();
            Hangers();
            Nozzles();
            NodeLoads();
            Supports();
            if (Nonlinear.Count > 0)
                foreach (var kv in Nonlinear)
                    Refuse("nonlinear supports, for phase 2 of the check model -- " + kv.Key + " (" + I(kv.Value.Count) + "): " + string.Join(", ", kv.Value.Take(12).ToArray()) + (kv.Value.Count > 12 ? " ..." : ""));
            if (Refusals.Count > 0) throw new PipeToFrameRefusal(Refusals);
            Note("no code checks: SIFs, allowables and code stresses stay in CAESAR");
            return Emit(version);
        }

        void Units()
        {
            fLen = Fac(CaesarUnits.Length); fDia = Fac(CaesarUnits.Diameter); fWall = Fac(CaesarUnits.Wall); fForce = Fac(CaesarUnits.Force);
            fMom = Fac(CaesarUnits.MomentInput); fPress = Fac(CaesarUnits.Pressure); fMod = Fac(CaesarUnits.Modulus); fPipeD = Fac(CaesarUnits.PipeDensity);
            fInsD = Fac(CaesarUnits.InsulationDensity); fFluidD = Fac(CaesarUnits.FluidDensity); fTK = Fac(CaesarUnits.TransStiffness); fRK = Fac(CaesarUnits.RotStiffness);
            fUni = Fac(CaesarUnits.UniformLoad); tScale = Fac(CaesarUnits.TempScale); tOff = M.Factor(CaesarUnits.TempOffset);
            if (double.IsNaN(tOff)) tOff = 0;
            double l = Math.Abs(fLen);
            si = Math.Abs(l - 25.4) < 1e-3 || Math.Abs(l - 2.54) < 1e-4 || Math.Abs(l - 0.0254) < 1e-6 || Math.Abs(l - 0.0254 * 1000) < 1e-3;
            if (si) { oL = 25.4; oF = 4.4482216152605; oT = 1 / 1.8; }
        }
        string UnitsText { get { return si ? "N, mm, C" : "lb, in, F"; } }

        // ---------------- joints: piece vertices, zero-length pieces joined (an expansion joint's kept apart: its link)
        readonly Dictionary<int, int> parent = new Dictionary<int, int>();
        int Find(int a) { int p; while (parent.TryGetValue(a, out p) && p != a) a = p; return a; }
        void Union(int a, int b) { a = Find(a); b = Find(b); if (a == b) return; if (Real(b) && (!Real(a) || b < a)) { int t = a; a = b; b = t; } parent[b] = a; }
        bool Real(int id) { return !Gr.IsSynthetic(id); }

        void JoinVertices()
        {
            foreach (int id in Gr.Nodes.Keys) parent[id] = id;
            foreach (var p in Gr.Pieces) if (p.Length == 0 && p.Kind != CaesarPieceKind.ExpJoint) Union(p.FromNode, p.ToNode);
            var groups = Gr.Nodes.Keys.GroupBy(Find).OrderBy(gq => gq.Key).ToList();
            var synthCount = new Dictionary<string, int>();
            foreach (var gq in groups)
            {
                int rep = gq.Key;
                string label;
                if (Real(rep)) label = I(rep);
                else label = SynthLabel(rep, synthCount);
                var reals = gq.Where(Real).OrderBy(v => v).ToList();
                if (reals.Count > 1) Note("nodes " + string.Join(", ", reals.Select(I).ToArray()) + " are one point (zero-length elements between them): one joint " + label);
                AddJoint(label, CaesarPos(rep));
                foreach (int v in gq) JointOfVertex[v] = label;
            }
        }

        string SynthLabel(int id, Dictionary<string, int> count)
        {
            CaesarSyntheticNode s;
            string to = "S", kind = "S";
            if (Gr.Synthetic.TryGetValue(id, out s))
            {
                if (s.ElementIndex >= 0 && s.ElementIndex < Cm.Elements.Count) to = I(Cm.Elements[s.ElementIndex].To);
                kind = s.Kind == "NEAR" ? "N" : s.Kind == "FAR" ? "F" : s.Kind == "ARC" ? "A" : s.Kind == "MITER" ? "M" : "S";
            }
            string b = to + kind;
            if (kind == "N" || kind == "F") { if (!Labels.Contains(b)) return b; }
            int k;
            count.TryGetValue(b, out k);
            string l;
            do { k++; l = b + I(k); } while (Labels.Contains(l));
            count[b] = k;
            return l;
        }

        double[] CaesarPos(int id)
        {
            var p = Gr.Position(id);
            if (p == null) return null;
            return R(new[] { p[0] / fLen, p[1] / fLen, p[2] / fLen });
        }

        void AddJoint(string label, double[] pos)
        {
            if (JointPos.ContainsKey(label)) return;
            Labels.Add(label);
            JointPos[label] = pos ?? new double[3];
            JointOrder.Add(label);
        }

        // the joint of a CAESAR node: its vertex's, else a joint of its own where CaesarGeometry put it (a CNODE, a vessel
        // node); a bend node CAESAR's rules draw straight is on the pipe (its host's joint)
        string JointOfNode(int node, int near, string who)
        {
            string j;
            if (JointOfVertex.TryGetValue(node, out j)) return j;
            int host;
            if (Gr.AttachedHost.TryGetValue(node, out host) && IsStraightBendNode(node) && JointOfVertex.TryGetValue(host, out j))
            {
                Note("node " + I(node) + " is a bend node on a bend CAESAR's rules draw straight: on joint " + j);
                JointOfVertex[node] = j;
                return j;
            }
            string label = I(node);
            var pos = CaesarPos(node);
            if (pos == null)
            {
                string nj;
                pos = near > 0 && JointOfVertex.TryGetValue(near, out nj) ? JointPos[nj] : new double[3];
                Note(who + ": node " + I(node) + " has no position of its own: placed at node " + I(near));
            }
            AddJoint(label, pos);
            JointOfVertex[node] = label;
            return label;
        }

        bool IsStraightBendNode(int node)
        {
            foreach (var b in Gr.Bends) if (b.Straight && b.NodeAngles.Any(na => (int)na[0] == node)) return true;
            return false;
        }

        // ---------------- materials
        readonly HashSet<int> warnedMat = new HashSet<int>();
        MatSpec MaterialOf(PipeModelElement e, string who)
        {
            int n = e.Material;
            if (PipeMaterialDefaults.IsColdSpring(n)) { Refuse(who + ": CAESAR's cold-spring material " + I(n) + " (a cut short or long) is not in the check model"); n = 1; }
            var mm = M.MaterialNumbered(n);
            if (mm == null)
            {
                mm = PipeMaterialDefaults.For(M, n);
                if (warnedMat.Add(n))
                {
                    bool own = e.Emod > 0 || e.PipeDensity > 0;
                    Warn("material " + PipeModelCatalog.MaterialLabel(n) + ": no values given (none in a .cii, a TOML build or a workbook without the Materials sheet): Pluto's defaults for " +
                         PipeMaterialDefaults.ClassName(n) + " (E " + N(mm.E) + ", Poisson " + N(mm.Poisson) + ", density " + N(mm.Density) + ", alpha " + N(mm.Alpha) + " per degree)" +
                         (own ? "; E / density where CAESAR's input gives them" : "") + ". Give the job's on the CAESAR workbook's Materials sheet");
                }
            }
            else if (PipeMaterialDefaults.IsDefaultNote(mm.Note) && warnedMat.Add(n))
                Note("material " + PipeModelCatalog.MaterialLabel(n) + " keeps Pluto's default values (its Materials row says so): check them against CAESAR's material database");
            double E = (e.Emod > 0 ? e.Emod : mm.E) / fMod, nu = e.Poisson > 0 ? e.Poisson : mm.Poisson, rho = (e.PipeDensity > 0 ? e.PipeDensity : mm.Density) / fPipeD;
            double alpha = mm.Alpha * tScale;                 // per F
            string key = I(n) + "|" + N(E) + "|" + N(nu) + "|" + N(rho) + "|" + N(alpha);
            MatSpec ms;
            if (Mats.TryGetValue(key, out ms)) return ms;
            int same = MatOrder.Count(x => x.Number == n);
            ms = new MatSpec { Name = "MAT" + I(n) + (same > 0 ? "-" + I(same + 1) : ""), Number = n, E = E, Nu = nu, Rho = rho, Alpha = alpha };
            Mats[key] = ms; MatOrder.Add(ms);
            return ms;
        }

        string PipeSection(double od, double t, MatSpec mat, bool rigid)
        {
            string name = (rigid ? "RIGID" : "P") + N(Math.Round(OL(od), 4)) + "X" + N(Math.Round(OL(t), 4));
            string have;
            if (SecMat.TryGetValue(name, out have) && have != mat.Name) name = name + "_" + mat.Name;
            if (!PipeSecs.ContainsKey(name)) { PipeSecs[name] = new[] { od, t }; SecMat[name] = mat.Name; SecOrder.Add(name); }
            return name;
        }

        // ---------------- elements -> frames (and the zero-length expansion joints -> links)
        void ElementsToFrames()
        {
            bool reducerNoted = false;
            for (int ei = 0; ei < Cm.Elements.Count; ei++)
            {
                var ce = Cm.Elements[ei];
                var pe = Order[ei];
                string who = "element " + I(ce.From) + "-" + I(ce.To);
                List<int> pis;
                if (!Gr.ElementPieces.TryGetValue(ei, out pis)) continue;
                var mat = MaterialOf(pe, who);
                var pieces = pis.Select(i => Gr.Pieces[i]).ToList();
                bool ej = pe.ExpJoint != null, rigid = pe.Rigid != null;
                // the bend's k and its arc-over-chords weight ratio
                double k = 1, ratio = 1;
                CaesarBendGeom bg;
                if (pe.Bend != null && Gr.BendByElement.TryGetValue(ei, out bg))
                {
                    if (bg.Straight) Note(who + ": a bend CAESAR's rules make straight (" + bg.Reason + "): plain pipe here");
                    else
                    {
                        k = BendK(pe, bg, who);
                        double chords = pieces.Where(p => p.Kind == CaesarPieceKind.Bend).Sum(p => p.Length);
                        double rad = (bg.Fitted ? bg.DrawRadius : bg.Radius), arc = rad * bg.AngleDeg * Math.PI / 180;
                        if (chords > 0 && arc > 0) ratio = arc / chords;
                        if (bg.Fitted || bg.Chorded) Note(who + ": the bend's tangent reaches past its FROM node: drawn " + (bg.Fitted ? "fitted" : "as a chord") + ", as the viewer draws it; check the CAESAR model");
                    }
                }
                var written = pieces.Where(p => p.Length > 0 || p.Kind == CaesarPieceKind.ExpJoint && ej).ToList();
                // a zero-length element with weight: its weight on the joint
                if (written.Count == 0 && rigid && pe.Rigid.Weight != 0)
                {
                    string j = JointOfVertex[pieces[0].FromNode];
                    AddForce("W_PIPE", j, new[] { 0, 0, -pe.Rigid.Weight / fForce, 0, 0, 0 });
                    Note(who + ": a zero-length rigid: its weight as a joint load at " + j);
                }
                int sub = 0, total = written.Count + written.Count(p => p.Kind == CaesarPieceKind.Reducer && p.IsTaper) * (ReducerSteps - 1);
                double rigidLen = rigid ? written.Sum(p => p.Length) / fLen : 0;
                foreach (var p in written)
                {
                    string a = JointOfVertex[p.FromNode], b = JointOfVertex[p.ToNode];
                    if (p.Kind == CaesarPieceKind.ExpJoint && p.Length == 0) { ZeroLengthEj(pe, ce, ei, a, b, who); continue; }
                    if (a == b) continue;
                    double od = p.OdA / fLen, t = p.WallA / fLen, len = p.Length / fLen;
                    if (p.Kind == CaesarPieceKind.Reducer && p.IsTaper)
                    {
                        if (!reducerNoted) { Note("reducers: " + I(ReducerSteps) + " stepped frames each, the OD and wall at each step's middle (CAESAR's own stepping is to be confirmed by the probe)"); reducerNoted = true; }
                        double[] pa = JointPos[a], pb = JointPos[b];
                        string prev = a;
                        for (int s = 0; s < ReducerSteps; s++)
                        {
                            string next = b;
                            if (s < ReducerSteps - 1)
                            {
                                next = Unique(I(ce.To) + "R" + I(s + 1));
                                double f = (s + 1.0) / ReducerSteps;
                                AddJoint(next, new[] { pa[0] + (pb[0] - pa[0]) * f, pa[1] + (pb[1] - pa[1]) * f, pa[2] + (pb[2] - pa[2]) * f });
                            }
                            double fm = (s + 0.5) / ReducerSteps;
                            double odS = Math.Round((p.OdA + (p.OdB - p.OdA) * fm) / fLen, 6), tS = Math.Round((p.WallA + (p.WallB - p.WallA) * fm) / fLen, 6);
                            var fs = NewFrame(ce, ++sub, total, prev, next, len / ReducerSteps, ei);
                            fs.Section = PipeSection(odS, tS, mat, false); fs.Mat = mat.Name;
                            fs.WeightPerIn = mat.Rho * Area(odS, tS);
                            PipeWeights(fs, pe, odS, tS, 1);
                            fs.Groups.Add("CAESAR_REDUCERS");
                            Finish(fs, pe, mat, who);
                            prev = next;
                        }
                        continue;
                    }
                    var f1 = NewFrame(ce, ++sub, total, a, b, len, ei);
                    f1.Mat = mat.Name;
                    if (p.Kind == CaesarPieceKind.ExpJoint) { FiniteEj(f1, pe, mat, od, t, len, who); Finish(f1, pe, mat, who); continue; }
                    if (rigid)
                    {
                        f1.Section = PipeSection(od + 2 * (RigidWallFactor - 1) * t, RigidWallFactor * t, mat, true);
                        f1.Mods = new double[] { 1, 1, 1, 1, 1, 1, 0, 0 };
                        double wu = pe.Rigid.Weight / fForce;
                        if (wu != 0 && rigidLen > 0)
                        {
                            f1.Gravity["W_PIPE"] = wu / rigidLen;
                            f1.WeightPerIn = wu / rigidLen;
                            PipeWeights(f1, pe, od, t, 1);
                            double ins; if (f1.Gravity.TryGetValue("W_INSUL", out ins)) f1.Gravity["W_INSUL"] = ins * RigidInsulation;
                        }
                        f1.Groups.Add("CAESAR_RIGIDS");
                        Finish(f1, pe, mat, who);
                        continue;
                    }
                    f1.Section = PipeSection(od, t, mat, false);
                    f1.WeightPerIn = mat.Rho * Area(od, t);
                    bool bend = p.Kind == CaesarPieceKind.Bend;
                    if (bend)
                    {
                        f1.Mods = new double[] { 1, 1, 1, 1, 1 / k, 1 / k, ratio, ratio };
                        f1.WeightPerIn *= ratio;
                        f1.Groups.Add("CAESAR_BENDS");
                    }
                    PipeWeights(f1, pe, od, t, bend ? ratio : 1);
                    Finish(f1, pe, mat, who);
                }
            }
        }

        string Unique(string l) { if (!Labels.Contains(l)) return l; int k = 2; while (Labels.Contains(l + "_" + I(k))) k++; return l + "_" + I(k); }

        FrameSpec NewFrame(CaesarElement ce, int sub, int total, string a, string b, double len, int ei)
        {
            string label = I(ce.From) + "-" + I(ce.To) + (total > 1 ? "." + I(sub) : "");
            if (Frames.Any(f => f.Label == label)) { int k = 2; while (Frames.Any(f => f.Label == label + "_" + I(k))) k++; label = label + "_" + I(k); }
            var fs = new FrameSpec { Label = label, I = a, J = b, Length = len, Element = ei };
            Frames.Add(fs);
            return fs;
        }

        static double Area(double od, double t) { double id = od - 2 * t; return Math.PI / 4 * (od * od - id * id); }

        // contents, insulation, cladding, refractory per length (lb/in), times the bend's ratio
        void PipeWeights(FrameSpec fs, PipeModelElement e, double od, double t, double ratio)
        {
            double tr = e.RefractoryThickness / fWall, id = od - 2 * t, bore = id - 2 * tr;
            double fluid = e.FluidDensity / fFluidD * Math.PI / 4 * bore * bore;
            double ti = e.Insulation / fWall, tc = e.CladThickness / fWall;
            double ins = e.InsulationDensity / fInsD * Math.PI / 4 * ((od + 2 * ti) * (od + 2 * ti) - od * od);
            double clad = e.CladDensity / fInsD * Math.PI / 4 * ((od + 2 * ti + 2 * tc) * (od + 2 * ti + 2 * tc) - (od + 2 * ti) * (od + 2 * ti));
            double refr = e.RefractoryDensity / fInsD * Math.PI / 4 * (id * id - bore * bore);
            if (e.InsCladWeight != 0) { ins = e.InsCladWeight / fUni; clad = 0; Note("an insulation + cladding unit weight is taken as force per length (to be confirmed)"); }
            if (fluid > 0) fs.Gravity["W_FLUID"] = fluid * ratio;
            if (ins + clad + refr > 0) fs.Gravity["W_INSUL"] = (ins + clad + refr) * ratio;
        }

        // the bend's flexibility factor: CAESAR's k when given, else the code's 1.65 / h (h = t R / r^2), times the
        // flanged-end factor, at least 1
        double BendK(PipeModelElement e, CaesarBendGeom bg, string who)
        {
            if (e.Bend.KFactor > 0) return e.Bend.KFactor;
            if (e.Bend.MiterPoints > 0) { Refuse(who + ": a mitred bend: give its flexibility factor (the Elements sheet's Bend K factor, CAESAR's k) for the check model"); return 1; }
            double od = e.Od / fDia, t = (e.Bend.FittingThickness > 0 ? e.Bend.FittingThickness : e.Wall) / fWall, rad = bg.Radius / fLen;
            double r2 = (od - t) / 2;
            if (r2 <= 0 || t <= 0 || rad <= 0) return 1;
            double h = t * rad / (r2 * r2), k = 1.65 / h;
            if (e.Bend.Type == 1) k *= Math.Pow(h, 1.0 / 6);
            else if (e.Bend.Type == 2) k *= Math.Pow(h, 1.0 / 3);
            return Math.Max(1, k);
        }

        void FiniteEj(FrameSpec fs, PipeModelElement e, MatSpec mat, double od, double t, double len, string who)
        {
            var x = e.ExpJoint;
            double kax = x.AxialStiffness / fTK, ktr = x.TransverseStiffness / fTK, kb = x.BendingStiffness / fRK * 180 / Math.PI, kt = x.TorsionStiffness / fRK * 180 / Math.PI;
            if (ktr > 0 && kb > 0) Refuse(who + ": an expansion joint with a length takes the transverse or the bending stiffness, not both (CAESAR works out the other)");
            if (kb <= 0 && ktr > 0) kb = ktr * len * len / 12;
            double gmod = mat.E / (2 * (1 + mat.Nu));
            if (kax <= 0) Refuse(who + ": an expansion joint with no axial stiffness");
            double A = kax * len / mat.E, Iv = kb > 0 ? kb * len / mat.E : 0, J = kt > 0 ? kt * len / gmod : 0;
            if (Iv <= 0) Refuse(who + ": an expansion joint with no bending or transverse stiffness");
            if (J <= 0) { Note(who + ": an expansion joint with no torsional stiffness: free in torsion"); J = 1e-6 * Area(od, t) * od * od; }
            string name = "EJ" + I(e.From) + "-" + I(e.To);
            GenSecs[name] = new[] { od, A, J, Iv };
            SecMat[name] = mat.Name;
            SecOrder.Add(name);
            fs.Section = name; fs.SectionType = "General";
            fs.Mods = new double[] { 1, 1, 1, 1, 1, 1, 0, 0 };
            fs.Groups.Add("CAESAR_EXPJOINTS");
            Note("expansion joints carry no weight here (CAESAR's convention to be confirmed by the probe); a finite one's bending stiffness is its transverse one times L^2 / 12 when only that is given");
            EjThrust(e, fs.I, fs.J, Unit(Sub(JointPos[fs.J], JointPos[fs.I])), who);
        }

        void ZeroLengthEj(PipeModelElement e, CaesarElement ce, int ei, string a, string b, string who)
        {
            var x = e.ExpJoint;
            var ax = Gr.AxisAt(ei, ce.From);
            if (ax == null) { Refuse(who + ": a zero-length expansion joint with no pipe to give its axis"); return; }
            var u = Unit(R(ax));
            Func<double, bool> rigidT = v => v * Math.Abs(1 / fTK) >= RigidStiffness, rigidR = v => v * Math.Abs(1 / fRK) >= RigidStiffness;
            double kax = x.AxialStiffness / fTK, ktr = x.TransverseStiffness / fTK, kb = x.BendingStiffness / fRK * 180 / Math.PI, kt = x.TorsionStiffness / fRK * 180 / Math.PI;
            var ls = new LinkSpec { I = a, J = b, Why = who + " (zero-length expansion joint)" };
            // axial, two transverse, torsion, two bending: along u and square to it
            double[] p2 = Parallel(u, new[] { 0.0, 0, 1 }) ? new[] { 1.0, 0, 0 } : Unit(Cross(new[] { 0.0, 0, 1 }, u)), p3 = Cross(u, p2);
            Action<double[], bool, double, bool> add = (d, rot, kv, rg) => { if (rg || kv > 0) ls.Dofs.Add(new Dof { Dir = d, Rot = rot, K = kv, Rigid = rg, From = who }); };
            add(u, false, kax, rigidT(x.AxialStiffness)); add(p2, false, ktr, rigidT(x.TransverseStiffness)); add(p3, false, ktr, rigidT(x.TransverseStiffness));
            add(u, true, kt, rigidR(x.TorsionStiffness)); add(p2, true, kb, rigidR(x.BendingStiffness)); add(p3, true, kb, rigidR(x.BendingStiffness));
            string key = "EJ|" + a + "|" + b;
            Links[key] = ls; LinkOrder.Add(key);
            Group("CAESAR_EXPJOINTS", "Link", "EJ" + a + "-" + b);
            EjThrust(e, a, b, u, who);
        }

        void EjThrust(PipeModelElement e, string a, string b, double[] u, string who)
        {
            double deff = e.ExpJoint.EffectiveId / fDia;
            if (deff <= 0 || u == null) return;
            for (int k = 0; k < 9; k++)
            {
                if (e.P[k] == 0) continue;
                double f = e.P[k] / fPress * Math.PI / 4 * deff * deff;
                AddForce("P" + I(k + 1), a, new[] { -f * u[0], -f * u[1], -f * u[2], 0, 0, 0 });
                AddForce("P" + I(k + 1), b, new[] { f * u[0], f * u[1], f * u[2], 0, 0, 0 });
            }
        }

        // the frame's groups, its temperature loads, its uniform loads
        void Finish(FrameSpec fs, PipeModelElement e, MatSpec mat, string who)
        {
            fs.Groups.Insert(0, "PL_KIND_PIPE");
            string line = Safe(e.LineNumber);
            if (line.Length > 0) fs.Groups.Insert(1, "PL_LINE_" + line);
            foreach (string g in fs.Groups) Group(g, "Frame", fs.Label);
            double amb = M.Ambient;
            for (int k = 0; k < 9; k++)
            {
                double tv = e.T[k];
                if (tv == 0) continue;
                double dT;
                if (Math.Abs(tv) < AlphaTolerance)
                {
                    if (mat.Alpha == 0) { Refuse(who + ": T" + I(k + 1) + " is a thermal expansion (" + N(tv) + "), and its material has no alpha to give it as a temperature"); continue; }
                    dT = tv / mat.Alpha;
                    StrainVectors.Add(k + 1); StrainFrames.Add(fs.Label);
                }
                else dT = (tv - amb) / tScale;
                if (dT == 0) continue;
                TempRows.Add("Frame=" + Q(fs.Label) + "   LoadPat=T" + I(k + 1) + "   Type=Temperature   Temp=" + N(ODT(dT)));
                PatternsUsed.Add("T" + I(k + 1));
            }
            if (e.Uniform != null)
                for (int v = 0; v < 9; v++)
                {
                    if (!e.Uniform.Used(v)) continue;
                    var c = R(e.Uniform.Vectors[v]);
                    // the frame's whole weight per length: its metal (self weight) or a rigid's own, contents, insulation
                    double total = fs.Gravity.Values.Sum() + (fs.Gravity.ContainsKey("W_PIPE") ? 0 : fs.WeightPerIn);
                    string[] dirs = { "X", "Y", "Z" };
                    for (int d = 0; d < 3; d++)
                    {
                        if (c[d] == 0) continue;
                        double w = e.Uniform.G[v] ? c[d] * total : c[d] / fUni;
                        DistRows.Add("Frame=" + Q(fs.Label) + "   LoadPat=U" + I(v + 1) + "   CoordSys=GLOBAL   Type=Force   Dir=" + dirs[d] + "   DistType=RelDist   RelDistA=0   RelDistB=1   AbsDistA=0   AbsDistB=" + N(WrittenLength(fs.I, fs.J)) + "   FOverLA=" + N(OFL(w)) + "   FOverLB=" + N(OFL(w)));
                        PatternsUsed.Add("U" + I(v + 1));
                    }
                }
            if (e.Wind != null && !PipeModelWind.IsNone(e.Wind)) Note("wind / wave: CAESAR's wind loads (its shape factors on the elements, its profile with the load cases) are not written");
        }

        // ---------------- restraints (CAESAR's own list: the joints expanded, as CAESAR reads them)
        void Restraints()
        {
            foreach (var r in Cm.Restraints)
            {
                string t = (r.TypeName ?? "").Trim().ToUpperInvariant();
                string who = "restraint " + t + " at " + I(r.Node) + (r.CNode > 0 ? " (CNode " + I(r.CNode) + ")" : "");
                string bare = t.TrimStart('+', '-');
                int sign = t.StartsWith("+", StringComparison.Ordinal) ? 1 : t.StartsWith("-", StringComparison.Ordinal) ? -1 : 0;
                if (bare.EndsWith("SNB", StringComparison.Ordinal)) { Note("snubbers act in dynamic cases only: not in the static check model (" + who + ")"); continue; }
                if (bare.EndsWith("ROD", StringComparison.Ordinal) || bare.EndsWith("SPR", StringComparison.Ordinal) || bare.EndsWith("2", StringComparison.Ordinal) || !r.Confirmed)
                { Refuse(who + ": a type the check model does not hold (rods, bilinear and spring restraints, codes outside CAESAR's table)"); continue; }
                bool rot = CaesarRestraint.IsRotationName(t);
                if (sign != 0) { NonLin("one-way", I(r.Node) + " " + t); continue; }
                if (r.Gap != 0) { NonLin("with a gap", I(r.Node) + " " + t + " gap " + N(r.Gap)); continue; }
                if (r.Friction != 0) { NonLin("with friction", I(r.Node) + " " + t + " mu " + N(r.Friction)); continue; }
                bool rigid = r.IsRigid;
                double kT = r.Stiffness / fTK, kR = r.Stiffness / fRK * 180 / Math.PI;
                var dirs = new List<double[]>();
                bool anc = bare == "ANC";
                if (anc) { dirs.Add(new[] { 1.0, 0, 0 }); dirs.Add(new[] { 0.0, 1, 0 }); dirs.Add(new[] { 0.0, 0, 1 }); }
                else if (bare == "GUI" || bare == "LIM")
                {
                    var ax = AxisAtNode(r.Node, r.ElementIndex);
                    if (ax == null) { Refuse(who + ": no pipe at the node to give the guide's / limit stop's direction"); continue; }
                    if (bare == "LIM") dirs.Add(ax);
                    else if (Parallel(ax, Up))
                    {
                        dirs.Add(new[] { 1.0, 0, 0 });
                        dirs.Add(M.ZUp ? new[] { 0.0, 1, 0 } : new[] { 0.0, 0, 1 });
                    }
                    else dirs.Add(Unit(Cross(Up, ax)));
                }
                else
                {
                    var d = Unit(r.Cosines ?? new double[3]) ?? Unit(PipeModelCodes.DefaultCosines(t));
                    if (d == null) { Refuse(who + ": no direction"); continue; }
                    dirs.Add(d);
                }
                string a = JointOfNode(r.Node, 0, who);
                string b = r.CNode > 0 ? JointOfNode(r.CNode, r.Node, who) : null;
                if (b != null && b == a) { Note(who + ": the node and its CNode are one joint here (zero-length elements between them): left out"); continue; }
                var list = new List<Dof>();
                foreach (var dc in dirs)
                {
                    var d = Unit(R(dc));
                    if (anc) { list.Add(new Dof { Dir = d, Rot = false, Rigid = rigid, K = kT, From = who }); list.Add(new Dof { Dir = d, Rot = true, Rigid = rigid, K = kR, From = who }); }
                    else list.Add(new Dof { Dir = d, Rot = rot, Rigid = rigid, K = rot ? kR : kT, From = who });
                }
                if (anc && !rigid) Note(who + ": a flexible anchor: its stiffness in all six DOFs (translational and rotational units alike, as CAESAR)");
                AddSupport(a, b, list, who);
            }
        }

        // the pipe's axis (SAP axes not yet: CAESAR's) at a node
        double[] AxisAtNode(int node, int ei)
        {
            if (ei >= 0 && ei < Cm.Elements.Count && (Cm.Elements[ei].From == node || Cm.Elements[ei].To == node || BendNodeOf(ei, node)))
            {
                var a = Gr.AxisAt(ei, node);
                if (a != null) return Unit(a);
            }
            List<int> ps;
            if (Gr.NodePieces.TryGetValue(node, out ps))
                foreach (int pi in ps)
                {
                    var a = Gr.AxisAt(Gr.Pieces[pi].ElementIndex, node);
                    if (a != null) return Unit(a);
                }
            return null;
        }

        bool BendNodeOf(int ei, int node)
        {
            CaesarBendGeom b;
            return Gr.BendByElement.TryGetValue(ei, out b) && b.NodeAngles.Any(na => (int)na[0] == node);
        }

        void AddSupport(string a, string b, List<Dof> dofs, string who)
        {
            if (b == null)
            {
                List<Dof> l;
                if (!Grounded.TryGetValue(a, out l)) { l = new List<Dof>(); Grounded[a] = l; }
                l.AddRange(dofs);
                return;
            }
            string key = "CN|" + a + "|" + b;
            LinkSpec ls;
            if (!Links.TryGetValue(key, out ls)) { ls = new LinkSpec { I = a, J = b, Why = who }; Links[key] = ls; LinkOrder.Add(key); }
            ls.Dofs.AddRange(dofs);
        }

        // ---------------- hangers: a spring with its installed load in H, or a constant load
        void Hangers()
        {
            var src = M.Source;
            if (src != null && !src.Seed && src.TailNozzleLines < 0 && src.MiscelTail.Count > 4)
                Refuse("this job's hangers and nozzles are in a MISCEL_1 layout Pluto does not take apart: the check model cannot hold them");
            foreach (var h in M.Hangers)
            {
                string who = "hanger at " + I(h.Node);
                int n = h.Count > 0 ? h.Count : 1;
                string a = JointOfNode(h.Node, 0, who), b = h.CNode > 0 ? JointOfNode(h.CNode, h.Node, who) : null;
                var up = new[] { 0.0, 0, 1 };
                if (h.ConstantLoad != 0)
                {
                    double f = h.ConstantLoad / fForce * n;
                    AddForce("H", a, new[] { 0, 0, f, 0, 0, 0 });
                    if (b != null) AddForce("H", b, new[] { 0, 0, -f, 0, 0, 0 });
                    continue;
                }
                if (h.Rate == 0) { Refuse(who + ": a hanger CAESAR designs: give the rate and installed (cold) load CAESAR chose (the CAESAR workbook's Hangers sheet) for the check model"); continue; }
                double k = h.Rate / fTK * n, cold = h.ColdLoad / fForce * n;
                if (n > 1) Note(who + ": " + I(n) + " hangers at the location: rate and load times " + I(n) + " (to be confirmed by the probe)");
                AddSupport(a, b, new List<Dof> { new Dof { Dir = up, Rot = false, K = k, From = who } }, who);
                if (cold != 0)
                {
                    AddForce("H", a, new[] { 0, 0, cold, 0, 0, 0 });
                    if (b != null) AddForce("H", b, new[] { 0, 0, -cold, 0, 0, 0 });
                }
            }
        }

        // ---------------- nozzles: custom stiffnesses along the nozzle; the computed kinds need CAESAR's numbers
        void Nozzles()
        {
            foreach (var z in M.Nozzles)
            {
                string who = "nozzle at " + I(z.Node) + " (" + z.TypeName + ")";
                if (z.Type != 3) { Refuse(who + ": CAESAR works out its flexibility: give the stiffnesses CAESAR computed as a custom nozzle for the check model"); continue; }
                var ax = AxisAtNode(z.Node, -1);
                if (ax == null) { Refuse(who + ": no pipe at the node to give the nozzle's axis"); continue; }
                var e1 = Unit(R(ax));
                var va = Unit(R(new[] { z.Get(PipeModelNozzle.FieldIndex("axis X")), z.Get(PipeModelNozzle.FieldIndex("axis Y")), z.Get(PipeModelNozzle.FieldIndex("axis Z")) }));
                double[] e2 = va == null ? null : Unit(Sub(va, Scale(e1, Dot(va, e1))));
                if (e2 == null) e2 = Parallel(e1, new[] { 0.0, 0, 1 }) ? new[] { 1.0, 0, 0 } : Unit(Cross(new[] { 0.0, 0, 1 }, e1));
                var e3 = Cross(e1, e2);
                Func<string, int, double> kOf = (f, unit) => { double v = z.Get(PipeModelNozzle.FieldIndex(f)); return unit == 0 ? v / fTK : v / fRK * 180 / Math.PI; };
                double kax = kOf("axial stiffness", 0), kin = kOf("in-plane stiffness", 1), kout = kOf("out-of-plane stiffness", 1), ktor = kOf("torsional stiffness", 1);
                var list = new List<Dof>
                {
                    new Dof { Dir = e1, K = kax, Rigid = kax <= 0, From = who }, new Dof { Dir = e2, Rigid = true, From = who }, new Dof { Dir = e3, Rigid = true, From = who },
                    new Dof { Dir = e1, Rot = true, K = ktor, Rigid = ktor <= 0, From = who }, new Dof { Dir = e3, Rot = true, K = kin, Rigid = kin <= 0, From = who },
                    new Dof { Dir = e2, Rot = true, K = kout, Rigid = kout <= 0, From = who }
                };
                Note("custom nozzles: axial along the nozzle, in-plane bending about the normal to the nozzle and vessel axes, out-of-plane about the vessel axis; a stiffness left blank is rigid (to be confirmed by the probe)");
                string a = JointOfNode(z.Node, 0, who), b = z.VesselNode > 0 ? JointOfNode(z.VesselNode, z.Node, who) : null;
                AddSupport(a, b, list, who);
            }
        }

        // ---------------- forces and imposed displacements at nodes
        void NodeLoads()
        {
            // CAESAR DX DY DZ RX RY RZ -> SAP global U1..R3 (Y up: X, -Z, Y)
            foreach (var d in M.NodeLoads)
            {
                string who = (d.IsDisplacement ? "displacement" : "force") + " at " + I(d.Node);
                string j = JointOfNode(d.Node, 0, who);
                if (!d.IsDisplacement)
                {
                    for (int v = 0; v < 9; v++)
                    {
                        if (!d.Used(v)) continue;
                        var x = d.Vectors[v];
                        Func<int, double> at = i => double.IsNaN(x[i]) ? 0 : x[i];
                        var f = R(new[] { at(0) / fForce, at(1) / fForce, at(2) / fForce });
                        var mo = R(new[] { at(3) / fMom, at(4) / fMom, at(5) / fMom });
                        AddForce("F" + I(v + 1), j, new[] { f[0], f[1], f[2], mo[0], mo[1], mo[2] });
                    }
                    continue;
                }
                bool[] held;
                if (!Imposed.TryGetValue(j, out held)) { held = new bool[6]; Imposed[j] = held; }
                var map = M.ZUp ? new[] { 0, 1, 2 } : new[] { 0, 2, 1 };     // CAESAR axis i -> SAP axis map[i]
                for (int v = 0; v < 9; v++)
                    for (int i = 0; i < 6; i++)
                        if (!double.IsNaN(d.Vectors[v][i])) held[(i < 3 ? 0 : 3) + map[i % 3]] = true;
                for (int v = 0; v < 9; v++)
                {
                    if (!d.Used(v)) continue;
                    var x = d.Vectors[v];
                    Func<int, double> at = i => double.IsNaN(x[i]) ? 0 : x[i];
                    var u = R(new[] { at(0) / fLen, at(1) / fLen, at(2) / fLen });
                    var r = R(new[] { at(3) * Math.PI / 180, at(4) * Math.PI / 180, at(5) * Math.PI / 180 });
                    GroundRows.Add("Joint=" + Q(j) + "   LoadPat=D" + I(v + 1) + "   CoordSys=GLOBAL   U1=" + N(OL(u[0])) + "   U2=" + N(OL(u[1])) + "   U3=" + N(OL(u[2])) +
                                   "   R1=" + N(r[0]) + "   R2=" + N(r[1]) + "   R3=" + N(r[2]));
                    PatternsUsed.Add("D" + I(v + 1));
                }
            }
        }

        void AddForce(string pat, string joint, double[] f6)
        {
            string key = pat + "\n" + joint;
            double[] have;
            if (!ForceAt.TryGetValue(key, out have)) { have = new double[6]; ForceAt[key] = have; ForceKeys.Add(key); }
            for (int i = 0; i < 6; i++) have[i] += f6[i];
            PatternsUsed.Add(pat);
        }

        // ---------------- supports: joint restraints and springs (local axes for a skew), links to CNODEs
        static readonly double[][] Axes = { new[] { 1.0, 0, 0 }, new[] { 0.0, 1, 0 }, new[] { 0.0, 0, 1 } };

        void Supports()
        {
            foreach (string j in JointOrder.ToList())
            {
                List<Dof> dofs;
                bool[] imposed;
                Grounded.TryGetValue(j, out dofs);
                Imposed.TryGetValue(j, out imposed);
                if (dofs == null && imposed == null) continue;
                dofs = dofs ?? new List<Dof>();
                double[][] frame = null;
                if (dofs.Any(d => !Axes.Any(a => Parallel(a, d.Dir))))
                {
                    frame = FrameOf(dofs.Select(d => d.Dir).ToList());
                    if (frame == null) { Refuse("joint " + j + ": supports in directions that are not square to each other (" + string.Join("; ", dofs.Select(d => d.From).Distinct().ToArray()) + ")"); continue; }
                    if (imposed != null && imposed.Any(x => x)) { Refuse("joint " + j + ": an imposed displacement at a joint with skewed supports (SAP holds both in the joint's local axes)"); continue; }
                }
                var ax = frame ?? Axes;
                var res = new bool[6]; var k = new double[6];
                foreach (var d in dofs)
                {
                    int i = Array.FindIndex(ax, a => Parallel(a, d.Dir)) + (d.Rot ? 3 : 0);
                    if (d.Rigid) res[i] = true; else k[i] += d.K;
                }
                if (imposed != null) for (int i = 0; i < 6; i++) res[i] |= imposed[i];
                for (int i = 0; i < 6; i++) if (res[i]) k[i] = 0;
                if (frame != null)
                {
                    var abc = AnglesOf(frame);
                    AxesRows.Add("Joint=" + Q(j) + "   AngleA=" + N(abc[0]) + "   AngleB=" + N(abc[1]) + "   AngleC=" + N(abc[2]) + "   AdvanceAxes=No");
                }
                if (res.Any(x => x))
                    RestraintRows.Add("Joint=" + Q(j) + string.Concat(Enumerable.Range(0, 6).Select(i => "   " + FrameJoint.Dofs[i] + "=" + (res[i] ? "Yes" : "No")).ToArray()));
                if (k.Any(x => x != 0))
                    SpringRows.Add("Joint=" + Q(j) + "   CoordSys=" + (frame != null ? "Local" : "GLOBAL") + string.Concat(Enumerable.Range(0, 6).Select(i => "   " + FrameJoint.Dofs[i] + "=" + N(i < 3 ? OKT(k[i]) : OKR(k[i]))).ToArray()));
            }
            int nProp = 0;
            var propByKey = new Dictionary<string, string>();
            foreach (string key in LinkOrder)
            {
                var ls = Links[key];
                bool ej = key.StartsWith("EJ|", StringComparison.Ordinal);
                double[] pa = JointPos[ls.I], pb = JointPos[ls.J];
                double dist = Len(Sub(pb, pa));
                double[][] ax;
                double angle = 0;
                if (dist < 1e-4)
                {
                    // a zero-length link's axes: 1 = +Z, 2 and 3 turned about it by the link's angle
                    var h = ls.Dofs.Select(d => d.Dir).FirstOrDefault(d => Math.Abs(d[2]) < 1 - 1e-6);
                    if (h != null)
                    {
                        var hh = Unit(new[] { h[0], h[1], 0 });
                        if (hh != null && Math.Abs(h[2]) < 1e-6) angle = Math.Atan2(hh[1], hh[0]) * 180 / Math.PI;
                    }
                    double tr = angle * Math.PI / 180;
                    ax = new[] { new[] { 0.0, 0, 1 }, new[] { Math.Cos(tr), Math.Sin(tr), 0 }, new[] { -Math.Sin(tr), Math.Cos(tr), 0 } };
                }
                else ax = FrameSidecar.LocalAxes(Sub(pb, pa), 0);
                var fixd = new bool[6]; var k = new double[6];
                bool ok = true;
                foreach (var d in ls.Dofs)
                {
                    int i = Array.FindIndex(ax, a => Parallel(a, d.Dir));
                    if (i < 0 || dist >= 1e-4 && (i != 0 || d.Rot)) { ok = false; break; }
                    i += d.Rot ? 3 : 0;
                    if (d.Rigid) fixd[i] = true; else k[i] += d.K;
                }
                if (!ok)
                {
                    Refuse(ls.Why + ": " + (dist < 1e-4 ? "a tie in a direction neither vertical nor horizontal (SAP's link needs advanced local axes, which the check model does not write yet)"
                                                        : "a tie to a node away from it, acting other than along the line between them"));
                    continue;
                }
                for (int i = 0; i < 6; i++) if (fixd[i]) k[i] = 0;
                var rows = new List<string[]>();
                for (int i = 0; i < 6; i++)
                    if (fixd[i] || k[i] != 0) rows.Add(new[] { FrameJoint.Dofs[i], fixd[i] ? "Yes" : "No", N(i < 3 ? OKT(k[i]) : OKR(k[i])) });
                if (rows.Count == 0) { Note(ls.Why + ": no stiffness: left out"); continue; }
                string sig = string.Join(";", rows.Select(r => string.Join(",", r)).ToArray());
                string prop;
                if (!propByKey.TryGetValue(sig, out prop))
                {
                    prop = (ej ? "EJ_P" : "TIE") + I(++nProp);
                    propByKey[sig] = prop;
                    LinkProps.Add(new KeyValuePair<string, List<string[]>>(prop, rows));
                }
                string label = (ej ? "EJ" : "C") + ls.I + "-" + ls.J;
                LinkRows.Add(new[] { label, ls.I, ls.J, prop, N(Math.Round(angle, 9)) });
                if (!ej) Group("CAESAR_TIES", "Link", label);
            }
        }

        // an orthonormal frame holding every direction (each along one of its axes), or null; the vertical as local 3
        // when one is vertical, local 1 never vertical
        static double[][] FrameOf(List<double[]> dirs)
        {
            var z = new[] { 0.0, 0, 1 };
            var uniq = new List<double[]>();
            foreach (var d in dirs) if (!uniq.Any(u => Parallel(u, d))) uniq.Add(d);
            if (uniq.Count > 3) return null;
            for (int i = 0; i < uniq.Count; i++) for (int j = i + 1; j < uniq.Count; j++) if (Math.Abs(Dot(uniq[i], uniq[j])) > 1e-6) return null;
            var v = uniq.FirstOrDefault(d => Parallel(d, z));
            var others = uniq.Where(d => !Parallel(d, z)).ToList();
            double[] e1, e2, e3;
            if (v != null)
            {
                e3 = z;
                e1 = others.Count > 0 ? others[0] : new[] { 1.0, 0, 0 };
                if (Math.Abs(e1[2]) > 1e-6) return null;
                e2 = Cross(e3, e1);
            }
            else
            {
                e1 = others.FirstOrDefault(d => Math.Abs(d[2]) < 1 - 1e-6) ?? others[0];
                var second = others.FirstOrDefault(d => !Parallel(d, e1));
                if (second != null) { e2 = second; e3 = Cross(e1, e2); }
                else
                {
                    // one skewed direction: local 2 horizontal, square to it
                    e2 = Unit(Cross(z, e1)) ?? new[] { 0.0, 1, 0 };
                    e3 = Cross(e1, e2);
                }
            }
            var f = new[] { Unit(e1), Unit(e2), Unit(e3) };
            foreach (var d in uniq) if (!f.Any(a => Parallel(a, d))) return null;
            return f;
        }

        // SAP's joint angles (A about Z, B about the new Y, C about the new X: local 1 = (cA cB, sA cB, -sB)) of a frame
        static double[] AnglesOf(double[][] f)
        {
            double[] e1 = f[0], e2 = f[1], e3 = f[2];
            double cb = Math.Sqrt(e1[0] * e1[0] + e1[1] * e1[1]);
            double A = Math.Atan2(e1[1], e1[0]), B = Math.Atan2(-e1[2], cb), C = Math.Atan2(e2[2], e3[2]);
            Func<double, double> deg = x => { double d = Math.Round(x * 180 / Math.PI, 9); return d == 0 ? 0 : d; };
            return new[] { deg(A), deg(B), deg(C) };
        }

        // ---------------- the cases: CAESAR's recommended set
        List<string[]> CaseRows = new List<string[]>();       // {case, pattern}
        List<string> CaseOrder = new List<string>();
        List<string[]> ComboRows = new List<string[]>();      // {combo, case, sf}

        void Cases()
        {
            var w = new[] { "W_PIPE", "W_FLUID", "W_INSUL" }.Where(PatternsUsed.Contains).ToList();
            Func<string, bool> has = PatternsUsed.Contains;
            var ope = new List<int>();
            for (int k = 1; k <= 9; k++) if (has("T" + I(k)) || has("D" + I(k))) ope.Add(k);
            if (ope.Count == 0) ope.Add(1);
            int n = 0;
            var opeNames = new List<string>();
            foreach (int k in ope)
            {
                string c = "L" + I(++n) + "_OPE";
                var pats = new List<string>(w);
                foreach (string p in new[] { "D" + I(k), "T" + I(k), has("P" + I(k)) ? "P" + I(k) : "P1", "F1", "H" }) if (has(p) && !pats.Contains(p)) pats.Add(p);
                foreach (string p in pats) CaseRows.Add(new[] { c, p });
                CaseOrder.Add(c); opeNames.Add(c);
            }
            string sus = "L" + I(++n) + "_SUS";
            foreach (string p in w.Concat(new[] { "P1", "F1", "H" }.Where(has))) CaseRows.Add(new[] { sus, p });
            CaseOrder.Add(sus);
            foreach (string o in opeNames)
            {
                string c = "L" + I(++n) + "_EXP";
                ComboRows.Add(new[] { c, o, "1" }); ComboRows.Add(new[] { c, sus, "-1" });
            }
            // a pattern no case takes (U1 g's, F2 ...): a case of its own, so SAP runs it and the occasional cases can use it
            var rest = PatternsUsed.Where(p => !CaseRows.Any(r => r[1] == p) && !p.StartsWith("W", StringComparison.Ordinal)).OrderBy(p => p, StringComparer.Ordinal).ToList();
            foreach (string p in rest) { CaseRows.Add(new[] { p, p }); CaseOrder.Add(p); }
            Note("cases: CAESAR's recommended set (OPE per thermal set with its displacements and pressure, SUS, EXP = OPE - SUS; F1 and H in OPE and SUS)" +
                 (rest.Count > 0 ? "; " + string.Join(", ", rest.ToArray()) + " alone as cases of their own (in no recommended case): build the occasional cases you need from them" : ""));
        }

        // ---------------- the .s2k text
        string Emit(string version)
        {
            // frame gravity loads by pattern (first: the cases take the weight patterns there are)
            foreach (var f in Frames)
                foreach (var kv in f.Gravity.OrderBy(q => Array.IndexOf(new[] { "W_PIPE", "W_FLUID", "W_INSUL" }, q.Key)))
                {
                    if (kv.Value == 0) continue;
                    DistRows.Add("Frame=" + Q(f.Label) + "   LoadPat=" + kv.Key + "   CoordSys=GLOBAL   Type=Force   Dir=Gravity   DistType=RelDist   RelDistA=0   RelDistB=1   AbsDistA=0   AbsDistB=" + N(WrittenLength(f.I, f.J)) + "   FOverLA=" + N(OFL(kv.Value)) + "   FOverLB=" + N(OFL(kv.Value)));
                    PatternsUsed.Add(kv.Key);
                }
            PatternsUsed.Add("W_PIPE");
            Cases();
            var w = new List<string>();
            w.Add("File generated by Pluto PipeToFrame.cs: the structural check model of a CAESAR II job (pipe, loads and supports as frames, links and loads)");
            w.Add("");
            Action<string, IEnumerable<string>> table = (name, rows) => { var l = rows.ToList(); if (l.Count == 0) return; w.Add("TABLE:  \"" + name + "\""); foreach (var r in l) w.Add("   " + r); w.Add(""); };
            table("PROGRAM CONTROL", new[] { "ProgramName=SAP2000   Version=" + version + "   CurrUnits=\"" + UnitsText + "\"" });
            table("TABLES AUTOMATICALLY SAVED AFTER ANALYSIS", new[] { "SaveFile=No" });
            table("ACTIVE DEGREES OF FREEDOM", new[] { "UX=Yes   UY=Yes   UZ=Yes   RX=Yes   RY=Yes   RZ=Yes" });
            double gOut = G * oL;
            table("MATERIAL PROPERTIES 01 - GENERAL", MatOrder.Select(x => "Material=" + Q(x.Name) + "   Type=Other   SymType=Isotropic   TempDepend=No   Color=Blue"));
            table("MATERIAL PROPERTIES 02 - BASIC MECHANICAL PROPERTIES", MatOrder.Select(x =>
                "Material=" + Q(x.Name) + "   UnitWeight=" + N(OW(x.Rho)) + "   UnitMass=" + N(OW(x.Rho) / gOut) + "   E1=" + N(OS(x.E)) + "   G12=" + N(OS(x.E / (2 * (1 + x.Nu)))) + "   U12=" + N(x.Nu) + "   A1=" + N(OA(x.Alpha))));
            table("FRAME SECTION PROPERTIES 01 - GENERAL", SecOrder.Select(s =>
            {
                if (PipeSecs.ContainsKey(s)) { var d = PipeSecs[s]; return "SectionName=" + Q(s) + "   Material=" + Q(SecMat[s]) + "   Shape=Pipe   t3=" + N(OL(d[0])) + "   tw=" + N(OL(d[1])) + "   Color=" + (s.StartsWith("RIGID", StringComparison.Ordinal) ? "Gray8Dark" : "Green"); }
                var gd = GenSecs[s];
                double l2 = oL * oL, l4 = l2 * l2;
                return "SectionName=" + Q(s) + "   Material=" + Q(SecMat[s]) + "   Shape=General   t3=" + N(OL(gd[0])) + "   t2=" + N(OL(gd[0])) + "   Area=" + N(gd[1] * l2) + "   TorsConst=" + N(gd[2] * l4) +
                       "   I33=" + N(gd[3] * l4) + "   I22=" + N(gd[3] * l4) + "   I23=0   AS2=0   AS3=0   Color=Magenta";
            }));
            table("JOINT COORDINATES", JointOrder.Select(j => { var p = JointPos[j]; return "Joint=" + Q(j) + "   CoordSys=GLOBAL   CoordType=Cartesian   XorR=" + N(OL(Clean(p[0]))) + "   Y=" + N(OL(Clean(p[1]))) + "   Z=" + N(OL(Clean(p[2]))); }));
            table("JOINT RESTRAINT ASSIGNMENTS", RestraintRows);
            table("JOINT LOCAL AXES ASSIGNMENTS 1 - TYPICAL", AxesRows);
            table("JOINT SPRING ASSIGNMENTS 1 - UNCOUPLED", SpringRows);
            table("CONNECTIVITY - FRAME", Frames.Select(f => "Frame=" + Q(f.Label) + "   JointI=" + Q(f.I) + "   JointJ=" + Q(f.J) + "   IsCurved=No"));
            table("FRAME SECTION ASSIGNMENTS", Frames.Select(f => "Frame=" + Q(f.Label) + "   SectionType=" + f.SectionType + "   AutoSelect=N.A.   AnalSect=" + Q(f.Section) + "   DesignSect=" + Q(f.Section) + "   MatProp=Default"));
            table("FRAME PROPERTY MODIFIERS", Frames.Where(f => f.Mods != null).Select(f =>
                "Frame=" + Q(f.Label) + string.Concat(Enumerable.Range(0, 8).Select(i => "   " + FrameMember.ModifierKeys[i] + "=" + N(Math.Round(f.Mods[i], 12))).ToArray())));
            string gen = "   Mass=0   Weight=0   RotInert1=0   RotInert2=0   RotInert3=0   DefLength=1   DefArea=1   PDM2I=0   PDM2J=0   PDM3I=0   PDM3J=0   Color=Magenta";
            table("LINK PROPERTY DEFINITIONS 01 - GENERAL", LinkProps.Select(p => "Link=" + Q(p.Key) + "   LinkType=Linear" + gen));
            table("LINK PROPERTY DEFINITIONS 02 - LINEAR", LinkProps.SelectMany(p => p.Value.Select(r =>
                "Link=" + Q(p.Key) + "   DOF=" + r[0] + (r[1] == "Yes" ? "   Fixed=Yes" : "   Fixed=No   " + (r[0].StartsWith("R", StringComparison.Ordinal) ? "RotKE=" + r[2] + "   RotCE=0" : "TransKE=" + r[2] + "   TransCE=0")))));
            table("CONNECTIVITY - LINK", LinkRows.Select(r => "Link=" + Q(r[0]) + "   JointI=" + Q(r[1]) + "   JointJ=" + Q(r[2])));
            table("LINK PROPERTY ASSIGNMENTS", LinkRows.Select(r => "Link=" + Q(r[0]) + "   LinkProp=" + Q(r[3]) + "   LinkFDProp=None"));
            table("LINK LOCAL AXES ASSIGNMENTS 1 - TYPICAL", LinkRows.Where(r => r[4] != "0").Select(r => "Link=" + Q(r[0]) + "   Angle=" + r[4] + "   AdvanceAxes=No"));
            string gflags = "   Selection=Yes   SectionCut=Yes   Steel=Yes   Concrete=Yes   Aluminum=Yes   ColdFormed=Yes   Stage=Yes   Bridge=Yes   AutoSeismic=No   AutoWind=No   SelDesSteel=No   SelDesAlum=No   SelDesCold=No   MassWeight=Yes   Color=Green";
            var linkLabels = new HashSet<string>(LinkRows.Select(r => r[0]));
            foreach (var g in GroupMembers.Values) g.RemoveAll(x => x[0] == "Link" && !linkLabels.Contains(x[1]));
            table("GROUPS 1 - DEFINITIONS", GroupOrder.Where(g => GroupMembers[g].Count > 0).Select(g => "GroupName=" + Q(g) + gflags));
            table("GROUPS 2 - ASSIGNMENTS", GroupOrder.SelectMany(g => GroupMembers[g].Select(x => "GroupName=" + Q(g) + "   ObjectType=" + x[0] + "   ObjectLabel=" + Q(x[1]))));
            var pats = new List<string> { "W_PIPE", "W_FLUID", "W_INSUL" };
            for (int k = 1; k <= 9; k++) pats.Add("T" + I(k));
            for (int k = 1; k <= 9; k++) pats.Add("P" + I(k));
            for (int k = 1; k <= 9; k++) pats.Add("D" + I(k));
            for (int k = 1; k <= 9; k++) pats.Add("F" + I(k));
            for (int k = 1; k <= 9; k++) pats.Add("U" + I(k));
            pats.Add("H");
            pats = pats.Where(PatternsUsed.Contains).ToList();
            Func<string, string> dtype = p => p.StartsWith("W", StringComparison.Ordinal) ? "Dead" : p.StartsWith("T", StringComparison.Ordinal) ? "Temperature" : "Other";
            table("LOAD PATTERN DEFINITIONS", pats.Select(p => "LoadPat=" + p + "   DesignType=" + dtype(p) + "   SelfWtMult=" + (p == "W_PIPE" ? "1" : "0")));
            table("JOINT LOADS - FORCE", ForceKeys.Where(k => ForceAt[k].Any(v => v != 0)).Select(k =>
            {
                var f = ForceAt[k]; var parts = k.Split('\n');
                return "Joint=" + Q(parts[1]) + "   LoadPat=" + parts[0] + "   CoordSys=GLOBAL   F1=" + N(OF(Clean(f[0]))) + "   F2=" + N(OF(Clean(f[1]))) + "   F3=" + N(OF(Clean(f[2]))) +
                       "   M1=" + N(OM(Clean(f[3]))) + "   M2=" + N(OM(Clean(f[4]))) + "   M3=" + N(OM(Clean(f[5])));
            }));
            table("JOINT LOADS - GROUND DISPLACEMENT", GroundRows);
            table("FRAME LOADS - DISTRIBUTED", DistRows);
            table("FRAME LOADS - TEMPERATURE", TempRows);
            table("LOAD CASE DEFINITIONS", CaseOrder.Select(c => "Case=" + c + "   Type=LinStatic   InitialCond=Zero"));
            table("CASE - STATIC 1 - LOAD ASSIGNMENTS", CaseRows.Select(r => "Case=" + r[0] + "   LoadType=\"Load pattern\"   LoadName=" + r[1] + "   LoadSF=1"));
            var combos = new List<string>();
            string prevC = null;
            foreach (var r in ComboRows)
            {
                if (r[0] != prevC)
                    combos.Add("ComboName=" + r[0] + "   ComboType=\"Linear Add\"   AutoDesign=No   CaseType=\"Linear Static\"   CaseName=" + r[1] + "   ScaleFactor=" + r[2] + "   SteelDesign=None   ConcDesign=None   AlumDesign=None   ColdDesign=None");
                else combos.Add("ComboName=" + r[0] + "   CaseType=\"Linear Static\"   CaseName=" + r[1] + "   ScaleFactor=" + r[2]);
                prevC = r[0];
            }
            table("COMBINATION DEFINITIONS", combos);
            w.Add("END TABLE DATA");
            w.Add("");
            return string.Join("\r\n", w.ToArray());
        }

        static double Clean(double v) { double r = Math.Round(v, 9); return Math.Abs(r) < 1e-12 ? 0 : r; }

        // a frame's length as SAP (and the frame workbook) work it out: from its joints' coordinates as written
        double WrittenLength(string a, string b)
        {
            Func<string, int, double> at = (j, i) => double.Parse(N(OL(Clean(JointPos[j][i]))), CultureInfo.InvariantCulture);
            double dx = at(b, 0) - at(a, 0), dy = at(b, 1) - at(a, 1), dz = at(b, 2) - at(a, 2);
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
    }
}
