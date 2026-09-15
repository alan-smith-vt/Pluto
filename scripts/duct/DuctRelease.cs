// Exact release of stiff expansion-joint links by superposition (Woodbury), offline from SAP.
// Input: a SapRelease link-model export (forces.tsv with every case incl. the unit pairs, linkdisp.tsv,
// links.tsv). For a set of links, solve (K^-1 - D) X = d0 per load vector, where d0 = relative joint
// displacement across each link DOF under the vector, D = the same under the unit pairs, K = the link
// stiffness; corrected forces = F0 + G X. The linear combos 19 / 20 are vectors; combo 18 is rebuilt as
// Dead + Steel +/- SRSS(seismic) from the corrected cases. C# 5, no SAP dependency.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

public class ForceTable
{
    public List<string> Keys = new List<string>();                 // frame \t section \t station, first-seen order
    public Dictionary<string, int> Index = new Dictionary<string, int>(StringComparer.Ordinal);
    public List<string> CaseKeys = new List<string>();             // case \t step, first-seen order
    public Dictionary<string, double[][]> Rows = new Dictionary<string, double[][]>(StringComparer.Ordinal);   // caseKey -> [station][6]

    public static ForceTable Read(string path)
    {
        ForceTable t = new ForceTable();
        List<KeyValuePair<string, KeyValuePair<int, double[]>>> pending = new List<KeyValuePair<string, KeyValuePair<int, double[]>>>();
        using (StreamReader reader = new StreamReader(path))
        {
            string[] h = reader.ReadLine().Split('\t');
            int iF = Col(h, "Frame"), iS = Col(h, "Section"), iSta = Col(h, "Station_in"), iC = Col(h, "OutputCase"), iStep = Col(h, "StepType");
            int[] iv = { Col(h, "P_kip"), Col(h, "V2_kip"), Col(h, "V3_kip"), Col(h, "T_kipin"), Col(h, "M2_kipin"), Col(h, "M3_kipin") };
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                string[] c = line.Split('\t');
                string key = c[iF] + "\t" + c[iS] + "\t" + c[iSta];
                int idx;
                if (!t.Index.TryGetValue(key, out idx)) { idx = t.Keys.Count; t.Index[key] = idx; t.Keys.Add(key); }
                string ck = c[iC] + "\t" + c[iStep];
                if (!t.Rows.ContainsKey(ck)) { t.Rows[ck] = null; t.CaseKeys.Add(ck); }
                double[] v = new double[6];
                for (int k = 0; k < 6; k++) v[k] = double.Parse(c[iv[k]], NumberStyles.Float, CultureInfo.InvariantCulture);
                pending.Add(new KeyValuePair<string, KeyValuePair<int, double[]>>(ck, new KeyValuePair<int, double[]>(idx, v)));
            }
        }
        foreach (string ck in t.CaseKeys) t.Rows[ck] = new double[t.Keys.Count][];
        foreach (KeyValuePair<string, KeyValuePair<int, double[]>> p in pending) t.Rows[p.Key][p.Value.Key] = p.Value.Value;
        return t;
    }

    // The single case key for an output case name (a load vector must have exactly one step type).
    public string VectorKey(string caseName)
    {
        List<string> found = KeysOf(caseName);
        if (found.Count == 0) throw new Exception("output case |" + caseName + "| is not in the forces file (names must match SAP exactly)");
        if (found.Count > 1) throw new Exception("output case |" + caseName + "| has " + found.Count + " step types: not a single load vector (an envelope or SRSS combo?)");
        return found[0];
    }
    public List<string> KeysOf(string caseName)
    {
        List<string> found = new List<string>();
        foreach (string ck in CaseKeys) if (ck.Split('\t')[0] == caseName) found.Add(ck);
        return found;
    }
    public void Add(string caseKey, double[][] rows) { if (!Rows.ContainsKey(caseKey)) CaseKeys.Add(caseKey); Rows[caseKey] = rows; }

    public void Write(string path, IList<string> caseKeys)
    {
        using (StreamWriter w = new StreamWriter(path))
        {
            w.Write("Frame\tSection\tStation_in\tOutputCase\tStepType\tP_kip\tV2_kip\tV3_kip\tT_kipin\tM2_kipin\tM3_kipin\r\n");
            for (int i = 0; i < Keys.Count; i++)
                foreach (string ck in caseKeys)
                {
                    double[] v = Rows[ck][i];
                    if (v == null) continue;
                    w.Write(Keys[i] + "\t" + ck);
                    for (int k = 0; k < 6; k++) w.Write("\t" + v[k].ToString("R", CultureInfo.InvariantCulture));
                    w.Write("\r\n");
                }
        }
    }
    public static int Col(string[] head, string name)
    {
        for (int i = 0; i < head.Length; i++) if (string.Equals(head[i].Trim(), name, StringComparison.OrdinalIgnoreCase)) return i;
        throw new Exception("column " + name + " not found");
    }
}

public class LinkInfo
{
    public string Name, Candidate, Master, Other;
    public double Kt, Kr;
    public string[] Patterns;
    public double K(int d) { return d < 3 ? Kt : Kr; }
}

public class DuctRelease
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public static readonly string[] Comp = { "P", "V2", "V3", "T", "M2", "M3" };

    public ForceTable Base;
    public List<LinkInfo> Links = new List<LinkInfo>();
    Dictionary<string, double[]> disp = new Dictionary<string, double[]>(StringComparer.Ordinal);   // link \t side \t caseKey -> 6

    public static DuctRelease Load(string linkDir)
    {
        DuctRelease r = new DuctRelease();
        r.Base = ForceTable.Read(Path.Combine(linkDir, "forces.tsv"));
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
                r.Links.Add(l);
            }
        }
        using (StreamReader reader = new StreamReader(Path.Combine(linkDir, "linkdisp.tsv")))
        {
            string[] h = reader.ReadLine().Split('\t');
            int iL = ForceTable.Col(h, "Link"), iSide = ForceTable.Col(h, "Side"), iC = ForceTable.Col(h, "OutputCase"), iStep = ForceTable.Col(h, "StepType");
            int[] iv = { ForceTable.Col(h, "U1"), ForceTable.Col(h, "U2"), ForceTable.Col(h, "U3"), ForceTable.Col(h, "R1"), ForceTable.Col(h, "R2"), ForceTable.Col(h, "R3") };
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                string[] c = line.Split('\t');
                double[] v = new double[6];
                for (int k = 0; k < 6; k++) v[k] = N(c[iv[k]]);
                r.disp[c[iL] + "\t" + c[iSide] + "\t" + c[iC] + "\t" + c[iStep]] = v;
            }
        }
        return r;
    }

    // Relative displacement (other - master) across link l under case key ck.
    double[] Delta(LinkInfo l, string ck)
    {
        double[] a, b;
        if (!disp.TryGetValue(l.Name + "\tother\t" + ck, out a) || !disp.TryGetValue(l.Name + "\tmaster\t" + ck, out b))
            throw new Exception("linkdisp.tsv has no row for link " + l.Name + " under |" + ck.Replace("\t", "|") + "|");
        double[] d = new double[6];
        for (int k = 0; k < 6; k++) d[k] = a[k] - b[k];
        return d;
    }

    public class Result
    {
        public ForceTable Table;                      // corrected vectors + rebuilt combo 18
        public List<string> OutKeys = new List<string>();
        public double[][] X;                          // [vector][6 x links]
        public double PivotRatio;                     // min / max |pivot| of (K^-1 - D)
        public bool Singular;
        public string Text;
    }

    // Release the links named in `set` (by link or candidate name; all links if null). vectors: output
    // case names corrected one by one. combo18: name to write; its rows are dead + steel +/- SRSS(seismic),
    // all from the corrected vectors.
    public Result Release(string[] set, string[] vectors, string dead, string steel, string[] seismic, string combo18)
    {
        List<LinkInfo> S = new List<LinkInfo>();
        foreach (LinkInfo l in Links) if (set == null || Array.IndexOf(set, l.Name) >= 0 || Array.IndexOf(set, l.Candidate) >= 0) S.Add(l);
        if (S.Count == 0) throw new Exception("no links to release");
        int n = 6 * S.Count;
        // Unit pair columns: G[j] = force rows under pair j; D[i,j] = delta_i under pair j.
        string[] pairKey = new string[n];
        for (int j = 0; j < n; j++) pairKey[j] = Base.VectorKey(S[j / 6].Patterns[j % 6]);
        double[,] A = new double[n, n];
        for (int j = 0; j < n; j++)
            for (int li = 0; li < S.Count; li++)
            {
                double[] d = Delta(S[li], pairKey[j]);
                for (int k = 0; k < 6; k++) A[6 * li + k, j] = -d[k];
            }
        for (int i = 0; i < n; i++) A[i, i] += 1.0 / S[i / 6].K(i % 6);
        Result res = new Result();
        double[,] lu; int[] perm;
        res.Singular = !Lu(A, out lu, out perm, out res.PivotRatio);
        StringBuilder sb = new StringBuilder();
        sb.AppendLine(F("release of {0} link(s) ({1} DOF): pivot ratio {2}{3}", S.Count, n, G(res.PivotRatio), res.Singular ? "  SINGULAR (mechanism)" : ""));
        if (res.Singular) { res.Text = sb.ToString(); return res; }

        res.Table = new ForceTable();
        res.Table.Keys = Base.Keys; res.Table.Index = Base.Index;
        res.X = new double[vectors.Length][];
        Dictionary<string, double[][]> corrected = new Dictionary<string, double[][]>(StringComparer.Ordinal);
        sb.AppendLine("vector                         link    released gap: U1 U2 U3 (in), R1 R2 R3 (rad)                        pair forces X: F1 F2 F3 (kip), M1 M2 M3 (kip-in)");
        for (int v = 0; v < vectors.Length; v++)
        {
            string ck = Base.VectorKey(vectors[v]);
            double[] d0 = new double[n];
            for (int li = 0; li < S.Count; li++) { double[] d = Delta(S[li], ck); for (int k = 0; k < 6; k++) d0[6 * li + k] = d[k]; }
            double[] x = Solve(lu, perm, d0);
            res.X[v] = x;
            double[][] f0 = Base.Rows[ck], f = new double[f0.Length][];
            for (int i = 0; i < f0.Length; i++)
            {
                if (f0[i] == null) continue;
                double[] o = (double[])f0[i].Clone();
                for (int j = 0; j < n; j++)
                {
                    double[] g = Base.Rows[pairKey[j]][i];
                    if (g == null) throw new Exception("unit pair " + pairKey[j] + " has no row at " + Base.Keys[i]);
                    for (int k = 0; k < 6; k++) o[k] += x[j] * g[k];
                }
                f[i] = o;
            }
            res.Table.Add(ck, f); res.OutKeys.Add(ck);
            corrected[vectors[v]] = f;
            for (int li = 0; li < S.Count; li++)
            {
                StringBuilder gap = new StringBuilder(), px = new StringBuilder();
                for (int k = 0; k < 6; k++) { gap.Append(F("{0,12}", G(x[6 * li + k] / S[li].K(k)))); px.Append(F("{0,13}", G(x[6 * li + k]))); }
                sb.AppendLine(F("{0,-30} {1,-6} {2}   {3}", li == 0 ? vectors[v] : "", S[li].Name, gap, px));
            }
        }
        if (!string.IsNullOrEmpty(combo18))
        {
            double[][] L = corrected[dead], St = corrected[steel];
            double[][] max = new double[L.Length][], min = new double[L.Length][];
            for (int i = 0; i < L.Length; i++)
            {
                if (L[i] == null) continue;
                double[] lin = new double[6], sq = new double[6];
                for (int k = 0; k < 6; k++) lin[k] = L[i][k] + St[i][k];
                foreach (string s in seismic) { double[] e = corrected[s][i]; for (int k = 0; k < 6; k++) sq[k] += e[k] * e[k]; }
                max[i] = new double[6]; min[i] = new double[6];
                for (int k = 0; k < 6; k++) { double r = Math.Sqrt(sq[k]); max[i][k] = lin[k] + r; min[i][k] = lin[k] - r; }
            }
            res.Table.Add(combo18 + "\tMax", max); res.OutKeys.Add(combo18 + "\tMax");
            res.Table.Add(combo18 + "\tMin", min); res.OutKeys.Add(combo18 + "\tMin");
        }
        res.Text = sb.ToString();
        return res;
    }

    // ---- comparison of two force tables over the checked output cases ----
    public class Compare
    {
        public string Title;
        public int Rows;
        public double[] MaxAbs = new double[6], Peak = new double[6], MaxRelPeak = new double[6];
        public string[] WorstAt = new string[6];
        public List<string[]> PerCase = new List<string[]>();     // case, step, rows, worst comp, max abs, rel to peak
        public List<double[]> Points = new List<double[]>();       // per row per comp: [comp, a, b] for the scatter
        public double Tolerance; public bool Ok;
    }

    // a = reference (x axis), b = the table under test. cases: output case names (every step type of each).
    public static Compare Diff(string title, ForceTable a, ForceTable b, string[] cases, double relTol)
    {
        Compare c = new Compare(); c.Title = title; c.Tolerance = relTol;
        foreach (string cs in cases)
            foreach (string ck in a.KeysOf(cs))
            {
                if (!b.Rows.ContainsKey(ck)) { c.PerCase.Add(new string[] { cs, ck.Split('\t')[1], "0", "missing in B", "", "" }); c.Ok = false; continue; }
                double[][] ra = a.Rows[ck], rb = b.Rows[ck];
                int rows = 0; double[] mx = new double[6], pk = new double[6]; int worst = 0;
                for (int i = 0; i < a.Keys.Count; i++)
                {
                    int ib; if (!b.Index.TryGetValue(a.Keys[i], out ib) || ra[i] == null || rb[ib] == null) continue;
                    rows++;
                    for (int k = 0; k < 6; k++)
                    {
                        double d = Math.Abs(ra[i][k] - rb[ib][k]);
                        pk[k] = Math.Max(pk[k], Math.Abs(ra[i][k]));
                        if (d > mx[k]) mx[k] = d;
                        if (d > c.MaxAbs[k]) { c.MaxAbs[k] = d; c.WorstAt[k] = ck.Replace("\t", " ") + " " + a.Keys[i].Replace("\t", " ") + F(" (A {0}, B {1})", G(ra[i][k]), G(rb[ib][k])); }
                        c.Points.Add(new double[] { k, ra[i][k], rb[ib][k] });
                    }
                }
                double pkw = 0; for (int k = 0; k < 6; k++) pkw = Math.Max(pkw, pk[k]);
                for (int k = 0; k < 6; k++) { c.Peak[k] = Math.Max(c.Peak[k], pk[k]); if (mx[k] / Math.Max(pk[k], 1e-6 * pkw) > mx[worst] / Math.Max(pk[worst], 1e-6 * pkw)) worst = k; }
                double rel = pk[worst] > 0 ? mx[worst] / Math.Max(pk[worst], 1e-6 * pkw) : 0;
                c.PerCase.Add(new string[] { cs, ck.Split('\t')[1], rows.ToString(Inv), Comp[worst], G(mx[worst]), G(rel) });
                c.Rows += rows;
            }
        bool ok = c.Rows > 0;
        // A component that is ~zero everywhere (torsion in a planar model) is measured against the largest peak instead.
        double peakAll = 0; for (int k = 0; k < 6; k++) peakAll = Math.Max(peakAll, c.Peak[k]);
        for (int k = 0; k < 6; k++) { double pk = Math.Max(c.Peak[k], 1e-6 * peakAll); c.MaxRelPeak[k] = pk > 0 ? c.MaxAbs[k] / pk : 0; if (c.MaxRelPeak[k] > relTol) ok = false; }
        foreach (string[] r in c.PerCase) if (r[3] == "missing in B") ok = false;
        c.Ok = ok;
        return c;
    }

    public static string CompareText(Compare c)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine(F("{0}: {1} station rows, tolerance {2} of each component's peak -> {3}", c.Title, c.Rows, G(c.Tolerance), c.Ok ? "OK" : "DIFFERENT"));
        sb.AppendLine("  comp   max |diff|     peak |A|    diff/peak   worst at");
        for (int k = 0; k < 6; k++) sb.AppendLine(F("  {0,-4} {1,12} {2,12} {3,12}   {4}", Comp[k], G(c.MaxAbs[k]), G(c.Peak[k]), G(c.MaxRelPeak[k]), c.WorstAt[k] ?? ""));
        sb.AppendLine("  case                            step   rows    worst comp   max |diff|   diff/peak");
        foreach (string[] r in c.PerCase) sb.AppendLine(F("  {0,-30} {1,-6} {2,6}   {3,-10} {4,12} {5,10}", r[0], r[1], r[2], r[3], r[4], r[5]));
        return sb.ToString();
    }

    // ---- dense LU with partial pivoting ----
    static bool Lu(double[,] a, out double[,] lu, out int[] perm, out double pivotRatio)
    {
        int n = a.GetLength(0);
        lu = (double[,])a.Clone(); perm = new int[n];
        for (int i = 0; i < n; i++) perm[i] = i;
        double pmin = double.MaxValue, pmax = 0;
        for (int c = 0; c < n; c++)
        {
            int p = c;
            for (int r = c + 1; r < n; r++) if (Math.Abs(lu[r, c]) > Math.Abs(lu[p, c])) p = r;
            if (p != c) { for (int k = 0; k < n; k++) { double t = lu[c, k]; lu[c, k] = lu[p, k]; lu[p, k] = t; } int tp = perm[c]; perm[c] = perm[p]; perm[p] = tp; }
            double piv = Math.Abs(lu[c, c]);
            pmin = Math.Min(pmin, piv); pmax = Math.Max(pmax, piv);
            if (piv == 0) { pivotRatio = 0; return false; }
            for (int r = c + 1; r < n; r++)
            {
                lu[r, c] /= lu[c, c];
                for (int k = c + 1; k < n; k++) lu[r, k] -= lu[r, c] * lu[c, k];
            }
        }
        pivotRatio = pmax > 0 ? pmin / pmax : 0;
        return pivotRatio > 1e-13;
    }
    static double[] Solve(double[,] lu, int[] perm, double[] b)
    {
        int n = b.Length; double[] y = new double[n], x = new double[n];
        for (int i = 0; i < n; i++) { double s = b[perm[i]]; for (int k = 0; k < i; k++) s -= lu[i, k] * y[k]; y[i] = s; }
        for (int i = n - 1; i >= 0; i--) { double s = y[i]; for (int k = i + 1; k < n; k++) s -= lu[i, k] * x[k]; x[i] = s / lu[i, i]; }
        return x;
    }

    static double N(string s) { return double.Parse(s, NumberStyles.Float, Inv); }
    static string G(double v) { return v.ToString("G5", Inv); }
    static string F(string fmt, params object[] args) { return string.Format(Inv, fmt, args); }
}
