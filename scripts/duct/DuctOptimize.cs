// Expansion-joint optimizer (HVAC): which candidate joints to release so every duct member's DCR
// envelope is <= the limit, with as few joints as practical. Offline, from a SapRelease link-model export in
// which EVERY candidate carries a stiff link and its unit load pairs. Releasing a set = the Woodbury solve of
// DuctRelease restricted to the set's link DOFs; the score is the Excel-workflow DCR envelope (axial + M2 + M3
// over the checked rows: dead, 18 Max, 18 Min, 19, 20; ends only) computed straight from the corrected P, M2, M3.
// Search: greedy forward -> swap pass -> removal pass; a singular system (mechanism) rejects a set. Every
// evaluated set is logged. Sets are small (<= a few dozen link DOFs), so each evaluation refactors its own
// small matrix: no incremental update is needed. C# 5, no SAP dependency.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

public class OptCandidate
{
    public string Joint;
    public int Span = -1;                       // from the connected model's duct-joints.tsv (-1 = unknown)
    public double X, Y, Z;
    public bool HasXyz;
    public List<int> LinkIdx = new List<int>(); // indices into DuctOptimize.Links (a tee gives n-1 links)
    public int[] Dofs;                          // pair-column indices, 6 per link
    public string Excluded;                     // null = allowed; else why it is never tried
}

public class OptScore
{
    public double Excess;                       // sum over frames of max(envelope - limit, 0)
    public int Over;                            // frames over the limit
    public double Max;                          // largest envelope
    public double Pivot;                        // min / max |pivot| of the release system (1 = nothing released)
    public bool Singular;
    public double[] EnvA, EnvMa, EnvMb;         // per frame, only when requested
}

public class OptEval
{
    public int Index;
    public string Phase;                        // greedy | swap | removal | base | final
    public int Step;
    public string Trial;                        // the joint added / swapped in / removed
    public int[] Set;                           // candidate indices
    public OptScore Score;
    public double Ms;
    public bool Accepted;
}

public class DuctOptimize
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public List<LinkInfo> Links = new List<LinkInfo>();
    public List<OptCandidate> Cands = new List<OptCandidate>();
    public List<string> Frames = new List<string>();
    public double Limit = 1.0, LsFactor = 1.5;
    public double SingularPivot = 1e-8;             // pivot ratio below this = mechanism (a true one shows ~1e-11 in round-off; healthy sets ~1e-2)
    public string[] Vectors;
    public List<string> CheckNames = new List<string>();
    public StringBuilder Info = new StringBuilder();
    public List<OptEval> Evals = new List<OptEval>();
    public List<OptEval> Steps = new List<OptEval>();     // the accepted sets, in order (base first, final last)
    public int[] Chosen = new int[0];
    public double SearchSeconds;

    int nPair; string[] pairCase; double[] kInv;
    double[,] D;                                          // [dof i, pair j]: delta_i under unit pair j
    double[][] d0;                                        // [vector][dof]
    int V, iDead, iSteel; int[] iSeis, iOther;
    int nRow; string[] rowKey; int[] rowFrame; double[][] w;  // w[r] = { wT, wC, wMa, wMb }
    double[][] F0;                                        // [vector][r * 3 + c], c = P, M2, M3
    double[][] G;                                         // [pair][r * 3 + c]
    Dictionary<int, int> spanSupports = new Dictionary<int, int>();

    // ------------------------------------------------------------------ candidate selection (before the link model)

    // spec: "auto" = every row of connectedDir\candidates.tsv whose span has 2+ support joints; a comma list of
    // joints; or a file with one joint per line. maxCandidates > 0 = an evenly spread sample of the allowed ones.
    // Writes outDir\candidates-selected.tsv and selected.txt (comma list for Run-SapRelease -LinkModel).
    public static string Select(string connectedDir, string spec, int maxCandidates, string outDir)
    {
        Directory.CreateDirectory(outDir);
        Dictionary<string, string[]> joints = new Dictionary<string, string[]>(StringComparer.Ordinal);
        List<string> order = new List<string>();
        string[] head = null;
        string jointsPath = Path.Combine(connectedDir, "duct-joints.tsv");
        if (File.Exists(jointsPath))
            using (StreamReader rd = new StreamReader(jointsPath))
            {
                head = rd.ReadLine().Split('\t');
                string line;
                while ((line = rd.ReadLine()) != null)
                {
                    if (line.Length == 0) continue;
                    string[] c = line.Split('\t');
                    joints[c[0]] = c; order.Add(c[0]);
                }
            }
        Dictionary<int, int> supports = ReadSpans(connectedDir);
        List<string> names = new List<string>();
        bool auto = string.IsNullOrEmpty(spec) || spec.Trim().ToLowerInvariant() == "auto";
        if (auto)
        {
            if (head == null) throw new Exception("no " + jointsPath + ": run the connected survey with -Candidates first");
            int iClass = ForceTable.Col(head, "Class"), iLoad = ForceTable.Col(head, "JointLoad");
            foreach (string j in order) if (joints[j][iClass] == "inline" && !string.Equals(joints[j][iLoad], "True", StringComparison.OrdinalIgnoreCase)) names.Add(j);
        }
        else if (File.Exists(spec))
        {
            foreach (string l in File.ReadAllLines(spec)) { string t = l.Split('#')[0].Trim(); if (t.Length > 0) names.Add(t); }
        }
        else foreach (string s in spec.Split(',')) { string t = s.Trim(); if (t.Length > 0) names.Add(t); }
        if (names.Count == 0) throw new Exception("candidate selection is empty");

        int iSpan = head == null ? -1 : ForceTable.Col(head, "Span"), iX = head == null ? -1 : ForceTable.Col(head, "X_in"), iY = head == null ? -1 : ForceTable.Col(head, "Y_in"), iZ = head == null ? -1 : ForceTable.Col(head, "Z_in");
        List<string[]> rows = new List<string[]>();
        List<int> allowed = new List<int>();
        foreach (string j in names)
        {
            string[] c; string span = "", x = "", y = "", z = "", reason = "";
            if (joints.TryGetValue(j, out c)) { span = c[iSpan]; x = c[iX]; y = c[iY]; z = c[iZ]; }
            int sp; int sup;
            if (auto && int.TryParse(span, NumberStyles.Integer, Inv, out sp) && supports.TryGetValue(sp, out sup) && sup < 2)
                reason = F("span {0} has {1} support joint(s): a release would leave a cantilever", sp, sup);
            if (reason.Length == 0) allowed.Add(rows.Count);
            rows.Add(new string[] { j, span, x, y, z, "", reason });
        }
        int selected = 0;
        if (maxCandidates > 0 && allowed.Count > maxCandidates)
        {
            HashSet<int> keep = new HashSet<int>();
            for (int i = 0; i < maxCandidates; i++) keep.Add(allowed[(int)Math.Floor((double)i * allowed.Count / maxCandidates)]);
            foreach (int i in allowed) if (!keep.Contains(i)) rows[i][6] = F("not in the spread sample of {0}", maxCandidates);
        }
        List<string> selList = new List<string>(), outRows = new List<string>();
        outRows.Add("Joint\tSpan\tX_in\tY_in\tZ_in\tSelected\tReason");
        foreach (string[] r in rows)
        {
            r[5] = r[6].Length == 0 ? "yes" : "no";
            if (r[5] == "yes") { selList.Add(r[0]); selected++; }
            outRows.Add(string.Join("\t", r));
        }
        if (selected == 0) throw new Exception("no candidate survives the selection");
        File.WriteAllText(Path.Combine(outDir, "candidates-selected.tsv"), string.Join("\r\n", outRows) + "\r\n");
        File.WriteAllText(Path.Combine(outDir, "selected.txt"), string.Join(",", selList.ToArray()));
        return F("candidates: {0} named ({1}), {2} allowed, {3} selected for the link model -> {4}", rows.Count, auto ? "auto: inline, no joint load" : "given", allowed.Count, selected, Path.Combine(outDir, "selected.txt"));
    }

    static Dictionary<int, int> ReadSpans(string connectedDir)
    {
        Dictionary<int, int> d = new Dictionary<int, int>();
        string p = Path.Combine(connectedDir ?? "", "spans.tsv");
        if (string.IsNullOrEmpty(connectedDir) || !File.Exists(p)) return d;
        using (StreamReader rd = new StreamReader(p))
        {
            string[] h = rd.ReadLine().Split('\t');
            int iS = ForceTable.Col(h, "Span"), iSup = ForceTable.Col(h, "SupportJoints");
            string line;
            while ((line = rd.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                string[] c = line.Split('\t');
                d[int.Parse(c[iS], Inv)] = int.Parse(c[iSup], Inv);
            }
        }
        return d;
    }

    // ------------------------------------------------------------------ loading

    // linkDir: the all-candidate link-model export. connectedDir (optional): duct-joints.tsv / spans.tsv for spans
    // and coordinates. tablesDir: duct-sections.csv (+ duct-materials.csv, else materialsFallback). vectors: the
    // load vectors (dead, steel, seismic..., 19, 20); every vector that is not dead / steel / seismic is a check
    // case of its own. ls: the limit state letter of every check (A / B = no increase, else 1.5).
    public static DuctOptimize Load(string linkDir, string connectedDir, string tablesDir, string materialsFallback,
        string[] vectors, string dead, string steel, string[] seismic, bool endsOnly, string ls, double limit)
    {
        Stopwatch sw = Stopwatch.StartNew();
        DuctOptimize o = new DuctOptimize();
        o.Limit = limit; o.LsFactor = DuctDcr.StressIncrease(ls); o.Vectors = vectors;
        o.V = vectors.Length;
        o.iDead = Array.IndexOf(vectors, dead); o.iSteel = Array.IndexOf(vectors, steel);
        if (o.iDead < 0 || o.iSteel < 0) throw new Exception("dead and steel must be among the vectors");
        o.iSeis = new int[seismic.Length];
        for (int i = 0; i < seismic.Length; i++) { o.iSeis[i] = Array.IndexOf(vectors, seismic[i]); if (o.iSeis[i] < 0) throw new Exception("seismic case " + seismic[i] + " is not among the vectors"); }
        List<int> other = new List<int>();
        for (int v = 0; v < o.V; v++) if (v != o.iDead && v != o.iSteel && Array.IndexOf(o.iSeis, v) < 0) other.Add(v);
        o.iOther = other.ToArray();
        o.CheckNames.Add(dead); o.CheckNames.Add("combo 18 Max"); o.CheckNames.Add("combo 18 Min");
        foreach (int v in o.iOther) o.CheckNames.Add(vectors[v]);

        // links + candidates
        using (StreamReader reader = new StreamReader(Path.Combine(linkDir, "links.tsv")))
        {
            string[] h = reader.ReadLine().Split('\t');
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                string[] c = line.Split('\t');
                LinkInfo l = new LinkInfo();
                l.Name = c[ForceTable.Col(h, "Link")]; l.Candidate = c[ForceTable.Col(h, "Candidate")];
                l.Master = c[ForceTable.Col(h, "Master")]; l.Other = c[ForceTable.Col(h, "Other")];
                l.Kt = N(c[ForceTable.Col(h, "kt_kip_in")]); l.Kr = N(c[ForceTable.Col(h, "kr_kipin_rad")]);
                l.Patterns = c[ForceTable.Col(h, "Patterns")].Split(',');
                o.Links.Add(l);
            }
        }
        if (o.Links.Count == 0) throw new Exception("links.tsv has no links");
        o.nPair = 6 * o.Links.Count;
        o.pairCase = new string[o.nPair]; o.kInv = new double[o.nPair];
        Dictionary<string, OptCandidate> byJoint = new Dictionary<string, OptCandidate>(StringComparer.Ordinal);
        for (int li = 0; li < o.Links.Count; li++)
        {
            LinkInfo l = o.Links[li];
            for (int k = 0; k < 6; k++) { o.pairCase[6 * li + k] = l.Patterns[k]; o.kInv[6 * li + k] = 1.0 / l.K(k); }
            OptCandidate cd;
            if (!byJoint.TryGetValue(l.Candidate, out cd)) { cd = new OptCandidate(); cd.Joint = l.Candidate; byJoint[cd.Joint] = cd; o.Cands.Add(cd); }
            cd.LinkIdx.Add(li);
        }
        foreach (OptCandidate cd in o.Cands)
        {
            cd.Dofs = new int[6 * cd.LinkIdx.Count];
            for (int i = 0; i < cd.LinkIdx.Count; i++) for (int k = 0; k < 6; k++) cd.Dofs[6 * i + k] = 6 * cd.LinkIdx[i] + k;
        }
        // spans and coordinates from the connected survey
        if (!string.IsNullOrEmpty(connectedDir))
        {
            o.spanSupports = ReadSpans(connectedDir);
            string jp = Path.Combine(connectedDir, "duct-joints.tsv");
            if (File.Exists(jp))
                using (StreamReader rd = new StreamReader(jp))
                {
                    string[] h = rd.ReadLine().Split('\t');
                    int iJ = ForceTable.Col(h, "Joint"), iSpan = ForceTable.Col(h, "Span"), iX = ForceTable.Col(h, "X_in"), iY = ForceTable.Col(h, "Y_in"), iZ = ForceTable.Col(h, "Z_in");
                    string line;
                    while ((line = rd.ReadLine()) != null)
                    {
                        if (line.Length == 0) continue;
                        string[] c = line.Split('\t');
                        OptCandidate cd;
                        if (!byJoint.TryGetValue(c[iJ], out cd)) continue;
                        int sp; if (int.TryParse(c[iSpan], NumberStyles.Integer, Inv, out sp)) cd.Span = sp;
                        cd.X = N(c[iX]); cd.Y = N(c[iY]); cd.Z = N(c[iZ]); cd.HasXyz = true;
                    }
                }
        }

        // capacities
        string matPath = Path.Combine(tablesDir, "duct-materials.csv");
        if (!File.Exists(matPath)) matPath = materialsFallback;
        Dictionary<string, DuctMaterial> mats = DuctTables.Materials(matPath);
        Dictionary<string, DuctSection> secs = DuctTables.Sections(Path.Combine(tablesDir, "duct-sections.csv"), mats);
        Dictionary<string, DuctCapacity> caps = new Dictionary<string, DuctCapacity>(StringComparer.OrdinalIgnoreCase);
        foreach (DuctSection s in secs.Values) caps[s.Name] = DuctCapacity.Compute(s);

        // case columns: vectors first, then the unit pairs
        Dictionary<string, int> caseCol = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int v = 0; v < o.V; v++) { if (caseCol.ContainsKey(vectors[v])) throw new Exception("vector listed twice: " + vectors[v]); caseCol[vectors[v]] = v; }
        for (int j = 0; j < o.nPair; j++) { if (caseCol.ContainsKey(o.pairCase[j])) throw new Exception("unit pair case name clashes: " + o.pairCase[j]); caseCol[o.pairCase[j]] = o.V + j; }

        // forces.tsv pass 1: distinct station rows and each frame's end stations
        string forcesPath = Path.Combine(linkDir, "forces.tsv");
        List<string> keys = new List<string>();
        Dictionary<string, int> keyIdx = new Dictionary<string, int>(StringComparer.Ordinal);
        Dictionary<string, double[]> range = new Dictionary<string, double[]>(StringComparer.Ordinal);
        long lines = 0;
        using (StreamReader rd = new StreamReader(forcesPath))
        {
            string[] h = rd.ReadLine().Split('\t');
            int iF = ForceTable.Col(h, "Frame"), iS = ForceTable.Col(h, "Section"), iSta = ForceTable.Col(h, "Station_in");
            string line;
            while ((line = rd.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                lines++;
                string[] c = line.Split('\t');
                string key = c[iF] + "\t" + c[iS] + "\t" + c[iSta];
                if (keyIdx.ContainsKey(key)) continue;
                keyIdx[key] = keys.Count; keys.Add(key);
                double sta = N(c[iSta]);
                double[] rg;
                if (!range.TryGetValue(c[iF], out rg)) range[c[iF]] = new double[] { sta, sta };
                else { rg[0] = Math.Min(rg[0], sta); rg[1] = Math.Max(rg[1], sta); }
            }
        }
        HashSet<string> missingSec = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<string> rows = new List<string>(); List<int> rowFr = new List<int>(); List<double[]> rowW = new List<double[]>();
        Dictionary<string, int> frameIdx = new Dictionary<string, int>(StringComparer.Ordinal);
        Dictionary<string, int> rowIdx = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string key in keys)
        {
            string[] c = key.Split('\t');
            if (endsOnly) { double sta = N(c[2]); double[] rg = range[c[0]]; if (sta != rg[0] && sta != rg[1]) continue; }
            DuctCapacity cap;
            if (!caps.TryGetValue(c[1], out cap)) { missingSec.Add(c[1]); continue; }
            int fi;
            if (!frameIdx.TryGetValue(c[0], out fi)) { fi = o.Frames.Count; frameIdx[c[0]] = fi; o.Frames.Add(c[0]); }
            double f = o.LsFactor;
            rowIdx[key] = rows.Count; rows.Add(key); rowFr.Add(fi);
            rowW.Add(new double[] { 1.0 / (cap.Ag * f * cap.SigTAll), 1.0 / (cap.Ae * f * cap.SigCAll), 1.0 / (cap.Sae * f * cap.SigMaAll), 1.0 / (cap.Sbe * f * cap.SigMbAll) });
        }
        o.nRow = rows.Count; o.rowKey = rows.ToArray(); o.rowFrame = rowFr.ToArray(); o.w = rowW.ToArray();
        if (o.nRow == 0) throw new Exception("no station rows to score (sections missing from duct-sections.csv?)");

        // pass 2: P, M2, M3 of the scored rows for the vectors and the unit pairs
        o.F0 = new double[o.V][]; o.G = new double[o.nPair][];
        for (int v = 0; v < o.V; v++) o.F0[v] = Nan(3 * o.nRow);
        for (int j = 0; j < o.nPair; j++) o.G[j] = Nan(3 * o.nRow);
        string[] stepOf = new string[o.V + o.nPair];
        using (StreamReader rd = new StreamReader(forcesPath))
        {
            string[] h = rd.ReadLine().Split('\t');
            int iF = ForceTable.Col(h, "Frame"), iS = ForceTable.Col(h, "Section"), iSta = ForceTable.Col(h, "Station_in"), iC = ForceTable.Col(h, "OutputCase"), iStep = ForceTable.Col(h, "StepType");
            int iP = ForceTable.Col(h, "P_kip"), iM2 = ForceTable.Col(h, "M2_kipin"), iM3 = ForceTable.Col(h, "M3_kipin");
            string line;
            while ((line = rd.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                string[] c = line.Split('\t');
                int col; if (!caseCol.TryGetValue(c[iC], out col)) continue;
                int r; if (!rowIdx.TryGetValue(c[iF] + "\t" + c[iS] + "\t" + c[iSta], out r)) continue;
                if (stepOf[col] == null) stepOf[col] = c[iStep];
                else if (stepOf[col] != c[iStep]) throw new Exception("output case |" + c[iC] + "| has several step types: not a single load vector");
                double[] t = col < o.V ? o.F0[col] : o.G[col - o.V];
                t[3 * r] = N(c[iP]); t[3 * r + 1] = N(c[iM2]); t[3 * r + 2] = N(c[iM3]);
            }
        }
        for (int v = 0; v < o.V; v++) CheckFilled(o.F0[v], o.rowKey, vectors[v]);
        for (int j = 0; j < o.nPair; j++) CheckFilled(o.G[j], o.rowKey, o.pairCase[j]);

        // linkdisp.tsv: relative displacement (other - master) across every link DOF under every column
        o.D = new double[o.nPair, o.nPair];
        o.d0 = new double[o.V][];
        for (int v = 0; v < o.V; v++) o.d0[v] = new double[o.nPair];
        Dictionary<string, int> linkIdx = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int li = 0; li < o.Links.Count; li++) linkIdx[o.Links[li].Name] = li;
        byte[,] seen = new byte[o.Links.Count, o.V + o.nPair];
        using (StreamReader rd = new StreamReader(Path.Combine(linkDir, "linkdisp.tsv")))
        {
            string[] h = rd.ReadLine().Split('\t');
            int iL = ForceTable.Col(h, "Link"), iSide = ForceTable.Col(h, "Side"), iC = ForceTable.Col(h, "OutputCase");
            int[] iv = { ForceTable.Col(h, "U1"), ForceTable.Col(h, "U2"), ForceTable.Col(h, "U3"), ForceTable.Col(h, "R1"), ForceTable.Col(h, "R2"), ForceTable.Col(h, "R3") };
            string line;
            while ((line = rd.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                string[] c = line.Split('\t');
                int li; if (!linkIdx.TryGetValue(c[iL], out li)) continue;
                int col; if (!caseCol.TryGetValue(c[iC], out col)) continue;
                double sign = c[iSide] == "other" ? 1.0 : -1.0;
                seen[li, col]++;
                for (int k = 0; k < 6; k++)
                {
                    double val = sign * N(c[iv[k]]);
                    if (col < o.V) o.d0[col][6 * li + k] += val; else o.D[6 * li + k, col - o.V] += val;
                }
            }
        }
        for (int li = 0; li < o.Links.Count; li++)
            for (int col = 0; col < o.V + o.nPair; col++)
                if (seen[li, col] != 2) throw new Exception(F("linkdisp.tsv: link {0} has {1} row(s) (want 2) under |{2}|", o.Links[li].Name, seen[li, col], col < o.V ? vectors[col] : o.pairCase[col - o.V]));

        o.Info.AppendLine(F("link model: {0} candidate(s), {1} link(s), {2} unit pairs; {3} force rows read in {4:0.0} s", o.Cands.Count, o.Links.Count, o.nPair, lines, sw.Elapsed.TotalSeconds));
        o.Info.AppendLine(F("scored: {0} frames, {1} station rows ({2}), checks: {3}, stress increase {4}, limit {5}", o.Frames.Count, o.nRow, endsOnly ? "ends only" : "all stations", string.Join(", ", o.CheckNames.ToArray()), o.LsFactor, limit));
        o.Info.AppendLine(F("a set is a mechanism when the release system's pivot ratio is below {0}", o.SingularPivot));
        if (missingSec.Count > 0) o.Info.AppendLine("sections not in duct-sections.csv (rows skipped): " + string.Join(", ", new List<string>(missingSec).ToArray()));
        int noSpan = 0; foreach (OptCandidate cd in o.Cands) if (cd.Span < 0) noSpan++;
        if (noSpan > 0) o.Info.AppendLine(F("{0} candidate(s) have no span (not in duct-joints.tsv): the one-per-span rule cannot see them", noSpan));
        return o;
    }

    static double[] Nan(int n) { double[] a = new double[n]; for (int i = 0; i < n; i++) a[i] = double.NaN; return a; }
    static void CheckFilled(double[] a, string[] keys, string caseName)
    {
        for (int i = 0; i < a.Length; i++) if (double.IsNaN(a[i])) throw new Exception("forces.tsv has no row for |" + caseName + "| at " + keys[i / 3].Replace("\t", " "));
    }

    // ------------------------------------------------------------------ scoring

    int[] DofsOf(int[] set)
    {
        List<int> d = new List<int>();
        foreach (int c in set) d.AddRange(Cands[c].Dofs);
        return d.ToArray();
    }

    public OptScore Evaluate(int[] set, bool keepEnv)
    {
        int[] dofs = DofsOf(set); int n = dofs.Length;
        OptScore s = new OptScore(); s.Pivot = 1;
        double[][] x = new double[V][];
        if (n > 0)
        {
            double[,] A = new double[n, n];
            for (int i = 0; i < n; i++) { for (int j = 0; j < n; j++) A[i, j] = -D[dofs[i], dofs[j]]; A[i, i] += kInv[dofs[i]]; }
            double[,] lu; int[] perm;
            s.Singular = !DuctRelease.Lu(A, out lu, out perm, out s.Pivot) || s.Pivot < SingularPivot;
            if (s.Singular) { s.Excess = double.PositiveInfinity; s.Over = int.MaxValue; s.Max = double.PositiveInfinity; return s; }
            for (int v = 0; v < V; v++)
            {
                double[] b = new double[n];
                for (int i = 0; i < n; i++) b[i] = d0[v][dofs[i]];
                x[v] = DuctRelease.Solve(lu, perm, b);
            }
        }
        int nf = Frames.Count;
        double[] envA = new double[nf], envMa = new double[nf], envMb = new double[nf];
        double[] f = new double[3 * V];
        for (int r = 0; r < nRow; r++)
        {
            int b0 = 3 * r;
            for (int v = 0; v < V; v++)
                for (int c = 0; c < 3; c++)
                {
                    double val = F0[v][b0 + c];
                    if (n > 0) { double[] xv = x[v]; for (int j = 0; j < n; j++) val += xv[j] * G[dofs[j]][b0 + c]; }
                    f[3 * v + c] = val;
                }
            int fi = rowFrame[r]; double[] wr = w[r];
            Check(f, 3 * iDead, wr, envA, envMa, envMb, fi);
            double[] lin = new double[3], sq = new double[3];
            for (int c = 0; c < 3; c++) lin[c] = f[3 * iDead + c] + f[3 * iSteel + c];
            foreach (int sv in iSeis) for (int c = 0; c < 3; c++) { double e = f[3 * sv + c]; sq[c] += e * e; }
            double[] c18 = new double[3];
            for (int c = 0; c < 3; c++) c18[c] = lin[c] + Math.Sqrt(sq[c]);
            Check(c18, 0, wr, envA, envMa, envMb, fi);
            for (int c = 0; c < 3; c++) c18[c] = lin[c] - Math.Sqrt(sq[c]);
            Check(c18, 0, wr, envA, envMa, envMb, fi);
            foreach (int ov in iOther) Check(f, 3 * ov, wr, envA, envMa, envMb, fi);
        }
        for (int i = 0; i < nf; i++)
        {
            double env = envA[i] + envMa[i] + envMb[i];
            if (env > Limit) { s.Excess += env - Limit; s.Over++; }
            if (env > s.Max) s.Max = env;
        }
        if (keepEnv) { s.EnvA = envA; s.EnvMa = envMa; s.EnvMb = envMb; }
        return s;
    }

    static void Check(double[] f, int b, double[] wr, double[] envA, double[] envMa, double[] envMb, int fi)
    {
        double p = f[b];
        double a = p > 0 ? p * wr[0] : -p * wr[1];
        double ma = Math.Abs(f[b + 1]) * wr[2], mb = Math.Abs(f[b + 2]) * wr[3];
        if (a > envA[fi]) envA[fi] = a;
        if (ma > envMa[fi]) envMa[fi] = ma;
        if (mb > envMb[fi]) envMb[fi] = mb;
    }

    // a strictly better than b: less excess, then fewer frames over, then a lower maximum.
    static bool Better(OptScore a, OptScore b)
    {
        if (a.Singular) return false;
        if (b.Singular) return true;
        if (a.Excess < b.Excess - 1e-9) return true;
        if (a.Excess > b.Excess + 1e-9) return false;
        if (a.Over != b.Over) return a.Over < b.Over;
        return a.Max < b.Max - 1e-9;
    }
    static bool NotWorse(OptScore a, OptScore b)
    {
        if (a.Singular) return false;
        return a.Excess <= b.Excess + 1e-9 && a.Over <= b.Over;
    }

    // ------------------------------------------------------------------ search

    OptEval Record(string phase, int step, string trial, int[] set, OptScore score, double ms)
    {
        OptEval e = new OptEval(); e.Index = Evals.Count; e.Phase = phase; e.Step = step; e.Trial = trial; e.Set = set; e.Score = score; e.Ms = ms;
        Evals.Add(e);
        return e;
    }
    OptEval Try(string phase, int step, string trial, int[] set)
    {
        Stopwatch sw = Stopwatch.StartNew();
        OptScore s = Evaluate(set, false);
        return Record(phase, step, trial, set, s, sw.Elapsed.TotalMilliseconds);
    }
    bool SpanFree(int cand, int[] set, int skipPos)
    {
        int sp = Cands[cand].Span;
        if (sp < 0) return true;
        for (int i = 0; i < set.Length; i++) if (i != skipPos && Cands[set[i]].Span == sp) return false;
        return true;
    }
    static int[] With(int[] set, int c) { int[] s = new int[set.Length + 1]; Array.Copy(set, s, set.Length); s[set.Length] = c; return s; }
    static int[] Without(int[] set, int pos) { List<int> l = new List<int>(set); l.RemoveAt(pos); return l.ToArray(); }
    static int[] Replaced(int[] set, int pos, int c) { int[] s = (int[])set.Clone(); s[pos] = c; return s; }
    string Names(int[] set) { string[] n = new string[set.Length]; for (int i = 0; i < n.Length; i++) n[i] = Cands[set[i]].Joint; return n.Length == 0 ? "(none)" : string.Join(",", n); }
    static string Sc(OptScore s) { return s.Singular ? "singular" : F("excess {0:0.###}, over {1}, max {2:0.###}", s.Excess, s.Over, s.Max); }

    public int[] Run(int maxJoints, bool onePerSpan, bool swap, bool removal, int maxEvals, int maxSwapPasses)
    {
        Stopwatch total = Stopwatch.StartNew();
        Evals.Clear(); Steps.Clear();
        int[] set = new int[0];
        Stopwatch sw = Stopwatch.StartNew();
        OptScore cur = Evaluate(set, false);
        OptEval baseEval = Record("base", 0, "", set, cur, sw.Elapsed.TotalMilliseconds); baseEval.Accepted = true; Steps.Add(baseEval);
        Say(F("base (nothing released): {0}   [{1} candidates allowed of {2}]", Sc(cur), Allowed(), Cands.Count));
        string stop = "";
        // A cut added to a mechanism leaves a mechanism, so a candidate that made the set singular is skipped for the
        // rest of the greedy pass (the set only grows); one singular on its own is skipped by every phase.
        HashSet<int> deadGreedy = new HashSet<int>(), deadAlone = new HashSet<int>();
        // greedy forward
        for (int step = 1; step <= maxJoints; step++)
        {
            if (cur.Excess <= 0) { stop = "every frame within the limit"; break; }
            if (maxEvals > 0 && Evals.Count >= maxEvals) { stop = "evaluation cap reached"; break; }
            Stopwatch ss = Stopwatch.StartNew();
            OptEval best = null; int tried = 0, singular = 0, skipped = 0;
            for (int c = 0; c < Cands.Count; c++)
            {
                if (Cands[c].Excluded != null || Array.IndexOf(set, c) >= 0) continue;
                if (onePerSpan && !SpanFree(c, set, -1)) continue;
                if (deadGreedy.Contains(c)) { skipped++; continue; }
                OptEval e = Try("greedy", step, Cands[c].Joint, With(set, c));
                tried++;
                if (e.Score.Singular) { singular++; deadGreedy.Add(c); if (set.Length == 0) deadAlone.Add(c); }
                if (best == null || Better(e.Score, best.Score)) best = e;
            }
            if (best == null) { stop = "no candidate left to try"; break; }
            if (!Better(best.Score, cur)) { stop = F("step {0}: no candidate improves the score", step); break; }
            best.Accepted = true; set = best.Set; cur = best.Score; Steps.Add(best);
            Say(F("greedy {0}: + joint {1} (span {2})  ->  {3}   [{4} tried, {5} singular, {6} skipped as known mechanisms, {7:0.0} s]", step, best.Trial, Cands[best.Set[best.Set.Length - 1]].Span, Sc(cur), tried, singular, skipped, ss.Elapsed.TotalSeconds));
        }
        if (stop.Length == 0) stop = F("{0} joints reached", maxJoints);
        Say("greedy stopped: " + stop);
        // swap pass: each chosen joint against every unused candidate, best replacement accepted if better
        if (swap && set.Length > 0)
        {
            for (int pass = 1; pass <= maxSwapPasses; pass++)
            {
                bool improved = false;
                for (int pos = 0; pos < set.Length; pos++)
                {
                    if (maxEvals > 0 && Evals.Count >= maxEvals) break;
                    OptEval best = null;
                    for (int c = 0; c < Cands.Count; c++)
                    {
                        if (Cands[c].Excluded != null || Array.IndexOf(set, c) >= 0) continue;
                        if (onePerSpan && !SpanFree(c, set, pos)) continue;
                        if (deadAlone.Contains(c)) continue;
                        OptEval e = Try("swap", pass, Cands[set[pos]].Joint + ">" + Cands[c].Joint, Replaced(set, pos, c));
                        if (best == null || Better(e.Score, best.Score)) best = e;
                    }
                    if (best != null && Better(best.Score, cur))
                    {
                        Say(F("swap pass {0}: joint {1}  ->  {2}", pass, best.Trial, Sc(best.Score)));
                        best.Accepted = true; set = best.Set; cur = best.Score; Steps.Add(best); improved = true;
                    }
                }
                if (!improved) { Say(F("swap pass {0}: no improvement", pass)); break; }
            }
        }
        // removal pass: drop any joint whose absence does not worsen the score
        if (removal && set.Length > 0)
        {
            bool removed = true;
            while (removed && set.Length > 0)
            {
                removed = false;
                for (int pos = 0; pos < set.Length; pos++)
                {
                    OptEval e = Try("removal", 0, Cands[set[pos]].Joint, Without(set, pos));
                    if (NotWorse(e.Score, cur))
                    {
                        Say(F("removal: - joint {0}  ->  {1}", e.Trial, Sc(e.Score)));
                        e.Accepted = true; set = e.Set; cur = e.Score; Steps.Add(e); removed = true; break;
                    }
                }
            }
        }
        Chosen = set;
        SearchSeconds = total.Elapsed.TotalSeconds;
        Say(F("final: {0} joint(s) {1}  ->  {2}   [{3} evaluations, {4:0.0} s]", set.Length, Names(set), Sc(cur), Evals.Count, SearchSeconds));
        return set;
    }

    int Allowed() { int n = 0; foreach (OptCandidate c in Cands) if (c.Excluded == null) n++; return n; }
    public List<string> Log = new List<string>();
    void Say(string s) { Log.Add(s); Console.WriteLine(s); }

    // ------------------------------------------------------------------ outputs

    public void WriteOutputs(string outDir)
    {
        Directory.CreateDirectory(outDir);
        List<string> rows = new List<string>();
        rows.Add("Index\tPhase\tStep\tTrial\tJoints\tSet\tExcess\tOver\tMax\tPivot\tSingular\tMs\tAccepted");
        foreach (OptEval e in Evals)
            rows.Add(Tab(e.Index, e.Phase, e.Step, e.Trial, e.Set.Length, Names(e.Set), e.Score.Singular ? "" : R(e.Score.Excess), e.Score.Singular ? "" : e.Score.Over.ToString(Inv), e.Score.Singular ? "" : R(e.Score.Max), R(e.Score.Pivot), e.Score.Singular, e.Ms.ToString("0.###", Inv), e.Accepted));
        File.WriteAllText(Path.Combine(outDir, "evaluations.tsv"), string.Join("\r\n", rows) + "\r\n");
        rows.Clear();
        rows.Add("Order\tPhase\tStep\tTrial\tJoints\tSet\tExcess\tOver\tMax\tPivot");
        for (int i = 0; i < Steps.Count; i++)
        {
            OptEval e = Steps[i];
            rows.Add(Tab(i, e.Phase, e.Step, e.Trial, e.Set.Length, Names(e.Set), R(e.Score.Excess), e.Score.Over, R(e.Score.Max), R(e.Score.Pivot)));
        }
        File.WriteAllText(Path.Combine(outDir, "steps.tsv"), string.Join("\r\n", rows) + "\r\n");
        rows.Clear();
        rows.Add("Joint\tSpan\tX_in\tY_in\tZ_in\tLinks\tExcluded\tChosen");
        foreach (OptCandidate c in Cands)
            rows.Add(Tab(c.Joint, c.Span < 0 ? "" : c.Span.ToString(Inv), c.HasXyz ? R(c.X) : "", c.HasXyz ? R(c.Y) : "", c.HasXyz ? R(c.Z) : "", c.LinkIdx.Count, c.Excluded ?? "", Array.IndexOf(Chosen, Cands.IndexOf(c)) >= 0));
        File.WriteAllText(Path.Combine(outDir, "candidates-used.tsv"), string.Join("\r\n", rows) + "\r\n");
        rows.Clear();
        rows.Add("Joint\tSpan\tX_in\tY_in\tZ_in\tAddedAtStep\tExcessBefore\tExcessAfter");
        foreach (int c in Chosen)
        {
            OptCandidate cd = Cands[c];
            string added = "", before = "", after = "";
            for (int i = 1; i < Steps.Count; i++)
                if (Steps[i].Phase == "greedy" && Steps[i].Set[Steps[i].Set.Length - 1] == c) { added = Steps[i].Step.ToString(Inv); before = R(Steps[i - 1].Score.Excess); after = R(Steps[i].Score.Excess); }
            rows.Add(Tab(cd.Joint, cd.Span < 0 ? "" : cd.Span.ToString(Inv), cd.HasXyz ? R(cd.X) : "", cd.HasXyz ? R(cd.Y) : "", cd.HasXyz ? R(cd.Z) : "", added, before, after));
        }
        File.WriteAllText(Path.Combine(outDir, "chosen.tsv"), string.Join("\r\n", rows) + "\r\n");
        File.WriteAllText(Path.Combine(outDir, "chosen.txt"), Names(Chosen) == "(none)" ? "" : Names(Chosen));
        WriteEnvelope(Path.Combine(outDir, "envelope-base.tsv"), Evaluate(new int[0], true));
        WriteEnvelope(Path.Combine(outDir, "envelope-final.tsv"), Evaluate(Chosen, true));
        StringBuilder sb = new StringBuilder();
        sb.Append(Info.ToString());
        foreach (string l in Log) sb.AppendLine(l);
        File.WriteAllText(Path.Combine(outDir, "optimize.txt"), sb.ToString());
    }

    void WriteEnvelope(string path, OptScore s)
    {
        List<string> rows = new List<string>();
        rows.Add("Frame\tEnv_axial\tEnv_ma\tEnv_mb\tDCR_envelope");
        for (int i = 0; i < Frames.Count; i++) rows.Add(Tab(Frames[i], R(s.EnvA[i]), R(s.EnvMa[i]), R(s.EnvMb[i]), R(s.EnvA[i] + s.EnvMa[i] + s.EnvMb[i])));
        File.WriteAllText(path, string.Join("\r\n", rows) + "\r\n");
    }

    static double N(string s) { return double.Parse(s, NumberStyles.Float, Inv); }
    static string R(double v) { return v.ToString("R", Inv); }
    static string F(string fmt, params object[] args) { return string.Format(Inv, fmt, args); }
    static string Tab(params object[] cells)
    {
        string[] s = new string[cells.Length];
        for (int i = 0; i < cells.Length; i++) s[i] = Convert.ToString(cells[i], Inv);
        return string.Join("\t", s);
    }
}
