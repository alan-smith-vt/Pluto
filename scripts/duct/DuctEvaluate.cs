// Duct DCR evaluation over a SAP frame-force table, plus the Mathcad check and the section-table skeleton.
// C# 5, no SAP dependency. Forces table: tab-separated with a header containing
// Frame, Section, Station_in, OutputCase, StepType, P_kip, V2_kip, V3_kip, M2_kipin, M3_kipin (SapSurvey -Forces writes it).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

public static class DuctEvaluate
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // Per-station DCRs -> dcr-stations.tsv; worst station per frame -> dcr-frames.tsv; counts -> dcr-summary.txt.
    // endsOnly: only each frame's first and last station (the Excel workflow checks the two ends).
    // Frame envelope (Excel): max axial + max M2 + max M3 over every evaluated row of the frame, each max taken separately.
    // noShear: V2 / V3 DCRs are still written but left out of the governing value and the counts.
    // comboList (optional): output case names that replace duct-combos.csv, all with limit state lsForList.
    // duct-materials.csv: TablesDir's copy if present, else the one beside this script (materialsFallback).
    public static string Run(string forcesPath, string tablesDir, string outDir, double limit, bool endsOnly,
        bool noShear, string[] comboList, string lsForList, string materialsFallback)
    {
        Directory.CreateDirectory(outDir);
        string matPath = Path.Combine(tablesDir, "duct-materials.csv");
        if (!File.Exists(matPath)) matPath = materialsFallback;
        Dictionary<string, DuctMaterial> mats = DuctTables.Materials(matPath);
        Dictionary<string, DuctSection> secs = DuctTables.Sections(Path.Combine(tablesDir, "duct-sections.csv"), mats);
        Dictionary<string, string> combos;
        if (comboList != null && comboList.Length > 0)
        {
            combos = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string k in comboList) combos[k.Trim()] = lsForList;
        }
        else combos = DuctTables.Combos(Path.Combine(tablesDir, "duct-combos.csv"));
        Dictionary<string, DuctCapacity> caps = new Dictionary<string, DuctCapacity>(StringComparer.OrdinalIgnoreCase);
        foreach (DuctSection s in secs.Values) caps[s.Name] = DuctCapacity.Compute(s);

        HashSet<string> missingSec = new HashSet<string>(), skippedCombo = new HashSet<string>();
        Dictionary<string, string[]> worst = new Dictionary<string, string[]>();      // frame -> row of its governing station
        Dictionary<string, double> worstVal = new Dictionary<string, double>();
        Dictionary<string, HashSet<string>> overByCombo = new Dictionary<string, HashSet<string>>();
        Dictionary<string, double[]> env = new Dictionary<string, double[]>();          // frame -> max axial, max M_a, max M_b, max V_a, max V_b
        Dictionary<string, double[]> ends = endsOnly ? StationRange(forcesPath) : null;
        int rowsIn = 0, rowsOut = 0;
        HashSet<string> usedSec = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using (StreamReader rd = new StreamReader(forcesPath))
        using (StreamWriter st = new StreamWriter(Path.Combine(outDir, "dcr-stations.tsv")))
        {
            string header = rd.ReadLine();
            if (header == null) throw new Exception("empty forces file");
            string[] h = header.Split('\t');
            int iF = Col(h, "Frame"), iS = Col(h, "Section"), iSta = Col(h, "Station_in"), iC = Col(h, "OutputCase"), iStep = Col(h, "StepType"),
                iP = Col(h, "P_kip"), iV2 = Col(h, "V2_kip"), iV3 = Col(h, "V3_kip"), iM2 = Col(h, "M2_kipin"), iM3 = Col(h, "M3_kipin");
            string outHead = "Frame\tSection\tStation_in\tOutputCase\tStepType\tLS\tDCR_t\tDCR_c\tDCR_ma\tDCR_mb\tDCR_va\tDCR_vb\tDCR_combined\tDCR_governing";
            st.Write(outHead + "\r\n");
            string line;
            while ((line = rd.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                rowsIn++;
                string[] c = line.Split('\t');
                if (ends != null)
                {
                    double sta = N(c[iSta]);
                    double[] rg = ends[c[iF]];
                    if (sta != rg[0] && sta != rg[1]) continue;
                }
                string ls;
                if (!combos.TryGetValue(c[iC], out ls)) { skippedCombo.Add(c[iC]); continue; }
                DuctCapacity cap;
                if (!caps.TryGetValue(c[iS], out cap)) { missingSec.Add(c[iS]); continue; }
                DuctDcr d = DuctDcr.FromSap(cap, DuctDcr.StressIncrease(ls), N(c[iP]), N(c[iV2]), N(c[iV3]), N(c[iM2]), N(c[iM3]));
                if (noShear) d.Governing = d.Combined;
                string[] row = { c[iF], c[iS], c[iSta], c[iC], c[iStep], ls, G(d.DcrT), G(d.DcrC), G(d.DcrMa), G(d.DcrMb), G(d.DcrVa), G(d.DcrVb), G(d.Combined), G(d.Governing) };
                st.Write(string.Join("\t", row) + "\r\n");
                rowsOut++;
                double[] e;
                if (!env.TryGetValue(c[iF], out e)) { e = new double[5]; env[c[iF]] = e; }
                e[0] = Math.Max(e[0], Math.Max(d.DcrT, d.DcrC)); e[1] = Math.Max(e[1], d.DcrMa); e[2] = Math.Max(e[2], d.DcrMb);
                e[3] = Math.Max(e[3], d.DcrVa); e[4] = Math.Max(e[4], d.DcrVb);
                usedSec.Add(c[iS]);
                double prev;
                if (!worstVal.TryGetValue(c[iF], out prev) || d.Governing > prev) { worstVal[c[iF]] = d.Governing; worst[c[iF]] = row; }
                if (d.Governing > limit)
                {
                    if (!overByCombo.ContainsKey(c[iC])) overByCombo[c[iC]] = new HashSet<string>();
                    overByCombo[c[iC]].Add(c[iF]);
                }
            }
        }

        List<string> frames = new List<string>(worst.Keys);
        frames.Sort(delegate(string x, string y) { return worstVal[y].CompareTo(worstVal[x]); });
        List<string> fr = new List<string>();
        fr.Add("Frame\tSection\tStation_in\tOutputCase\tStepType\tLS\tDCR_t\tDCR_c\tDCR_ma\tDCR_mb\tDCR_va\tDCR_vb\tDCR_combined\tDCR_governing\tControls\tEnv_axial\tEnv_ma\tEnv_mb\tDCR_envelope\tEnv_va\tEnv_vb\tDCR_shear");
        double excess = 0; int over = 0, overEnv = 0, overShear = 0; double maxShear = 0;
        foreach (string f in frames)
        {
            string[] r = worst[f];
            string controls = Controls(r, noShear);
            double[] e = env[f];
            double envSum = e[0] + e[1] + e[2];
            double shear = Math.Max(e[3], e[4]);
            fr.Add(string.Join("\t", r) + "\t" + controls + "\t" + G(e[0]) + "\t" + G(e[1]) + "\t" + G(e[2]) + "\t" + G(envSum) + "\t" + G(e[3]) + "\t" + G(e[4]) + "\t" + G(shear));
            if (shear > limit) overShear++;
            maxShear = Math.Max(maxShear, shear);
            if (worstVal[f] > limit) { over++; excess += worstVal[f] - limit; }
            if (envSum > limit) overEnv++;
        }
        File.WriteAllText(Path.Combine(outDir, "dcr-frames.tsv"), string.Join("\r\n", fr) + "\r\n");
        List<string> sc = new List<string>();
        sc.Add("Section\tMaterial\ta_in\tb_in\tt_in\tStiffSpacing_in\tOmega_v\th_a_in\th_a_over_t\tkv_a\tFcr_a_ksi\tlambda_va\tVn_a_web_kip\tVall_a_kip\th_b_in\th_b_over_t\tkv_b\tFcr_b_ksi\tlambda_vb\tVn_b_web_kip\tVall_b_kip");
        List<string> secNames = new List<string>(usedSec);
        secNames.Sort(StringComparer.OrdinalIgnoreCase);
        foreach (string k in secNames)
        {
            DuctCapacity q = caps[k]; DuctSection s = q.Section;
            sc.Add(string.Join("\t", new string[] { s.Name, s.Material.Name, G(s.A), G(s.B), G(s.T), double.IsNaN(s.StiffSpacing) ? "sheet" : G(s.StiffSpacing), G(s.Material.OmegaV),
                G(q.HVa), G(q.HVa / s.T), G(q.Kva), G(q.Fcra), G(q.LambdaVa), G(q.Vna), G(q.VAllA), G(q.HVb), G(q.HVb / s.T), G(q.Kvb), G(q.Fcrb), G(q.LambdaVb), G(q.Vnb), G(q.VAllB) }));
        }
        File.WriteAllText(Path.Combine(outDir, "shear-capacity.tsv"), string.Join("\r\n", sc) + "\r\n");

        StringBuilder sb = new StringBuilder();
        sb.AppendLine("DuctEvaluate " + DateTime.Now.ToString("yyyy-MM-dd HH:mm", Inv));
        sb.AppendLine(F("force rows {0}   evaluated {1}   frames {2}   DCR limit {3}   stations {4}", rowsIn, rowsOut, frames.Count, limit, endsOnly ? "ends only" : "all"));
        sb.AppendLine(F("materials {0}   shear {1}", matPath, noShear ? "excluded from governing" : "included"));
        sb.AppendLine(F("frames over limit {0}   sum of excess (per frame, governing) {1:0.###}", over, excess));
        sb.AppendLine(F("frames over limit on the envelope (max axial + max M2 + max M3) {0}", overEnv));
        sb.AppendLine(F("shear (reported separately, max of V2 / V3 DCR): frames over limit {0}, max {1:0.###}; capacities in shear-capacity.tsv", overShear, maxShear));
        sb.AppendLine("frames over limit by output case / combo:");
        List<string> cs = new List<string>(combos.Keys);
        cs.Sort(StringComparer.OrdinalIgnoreCase);
        foreach (string k in cs) sb.AppendLine(F("  {0,-30} LS {1}  {2}", k, combos[k], overByCombo.ContainsKey(k) ? overByCombo[k].Count : 0));
        if (skippedCombo.Count > 0) sb.AppendLine("output cases in forces but not in duct-combos.csv (skipped): " + string.Join(", ", Sorted(skippedCombo)));
        if (missingSec.Count > 0) sb.AppendLine("sections in forces but not in duct-sections.csv (skipped): " + string.Join(", ", Sorted(missingSec)));
        File.WriteAllText(Path.Combine(outDir, "dcr-summary.txt"), sb.ToString());
        return sb.ToString();
    }

    // Frame -> {min, max} station over the whole forces file.
    static Dictionary<string, double[]> StationRange(string forcesPath)
    {
        Dictionary<string, double[]> d = new Dictionary<string, double[]>();
        using (StreamReader rd = new StreamReader(forcesPath))
        {
            string[] h = rd.ReadLine().Split('\t');
            int iF = Col(h, "Frame"), iSta = Col(h, "Station_in");
            string line;
            while ((line = rd.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                string[] c = line.Split('\t');
                double sta = N(c[iSta]);
                double[] rg;
                if (!d.TryGetValue(c[iF], out rg)) d[c[iF]] = new double[] { sta, sta };
                else { rg[0] = Math.Min(rg[0], sta); rg[1] = Math.Max(rg[1], sta); }
            }
        }
        return d;
    }

    static string Controls(string[] r, bool noShear)
    {
        double comb = N(r[12]), va = N(r[10]), vb = N(r[11]);
        if (noShear || (comb >= va && comb >= vb)) return N(r[6]) >= N(r[7]) ? "tension+bending" : "compression+bending";
        return va >= vb ? "shear a" : "shear b";
    }

    // Reproduces the Mathcad template results (all inputs 1, L = 1 ft). Returns a report; ok = every value within 1e-9 relative.
    public static string CheckTemplate(out bool ok)
    {
        StringBuilder sb = new StringBuilder();
        ok = true;
        foreach (bool stainless in new bool[] { false, true })
        {
            DuctMaterial m = new DuctMaterial();
            m.Name = stainless ? "template-stainless" : "template-carbon";
            m.Stainless = stainless; m.E = 1; m.Mu = 1; m.Fy = 1; m.Fu = 1;
            DuctSection s = new DuctSection();
            s.Name = m.Name; s.Material = m; s.A = 1; s.B = 1; s.T = 1; s.HA = 1; s.HB = 1; s.K = 1.0; s.LFt = 1;
            s.StiffA = !stainless; s.StiffB = !stainless;
            DuctCapacity c = DuctCapacity.Compute(s);
            DuctDcr d = DuctDcr.FromSheet(c, DuctDcr.StressIncrease("A"), 1, 1, 1, 1, 1, 1);
            sb.AppendLine(m.Name);
            // Expected: Mathcad result.xml of Duct_Mathcad_Carbon / _Stainless.mcdx (2026-09-14).
            ok &= Cmp(sb, "A_g", c.Ag, 4);
            ok &= Cmp(sb, "A_e", c.Ae, 4);
            ok &= Cmp(sb, "I_a", c.Ia, 0.66666666666666674);
            ok &= Cmp(sb, "I_ae", c.Iae, 0.66666666666666674);
            ok &= Cmp(sb, "r_a", c.Ra, 0.408248290463863);
            ok &= Cmp(sb, "lambda_max", c.LambdaMax, 29.393876913398142);
            ok &= Cmp(sb, "S_ae", c.Sae, 1.3333333333333335);
            ok &= Cmp(sb, "F_cre", c.Fcre, 0.01142315324200157);
            ok &= Cmp(sb, "lambda_c", c.LambdaC, 9.3563616148041149);
            ok &= Cmp(sb, "F_n", c.Fn, stainless ? 0.01142315324200157 : 0.010018105393235376);
            ok &= Cmp(sb, "k_va", c.Kva, stainless ? 5.34 : 9.34);
            ok &= Cmp(sb, "V_ya", c.Vya, 0.6);
            ok &= Cmp(sb, "DCR_t", d.DcrT, 0.25);
            ok &= Cmp(sb, "DCR_c", d.DcrC, stainless ? 21.885375666744963 : 24.954818320119688);
            ok &= Cmp(sb, "DCR_ma", d.DcrMa, 0.74999999999999978);
            ok &= Cmp(sb, "DCR_t+ma+mb", d.DcrT + d.DcrMa + d.DcrMb, 1.7499999999999996);
            ok &= Cmp(sb, "DCR_c+ma+mb", d.Combined, stainless ? 23.385375666744963 : 26.454818320119688);
            sb.AppendLine(F("  shear (mu = 1 divides by zero in the sheet too): sigma_va_all = {0}", G(c.SigVaAll)));
        }
        sb.AppendLine(ok ? "CHECK OK" : "CHECK FAILED");
        return sb.ToString();
    }

    static bool Cmp(StringBuilder sb, string name, double got, double want)
    {
        bool pass = Math.Abs(got - want) <= 1e-9 * Math.Max(1.0, Math.Abs(want));
        sb.AppendLine(F("  {0,-14} {1,22} {2,22}  {3}", name, G(got), G(want), pass ? "ok" : "MISMATCH"));
        return pass;
    }

    // duct-sections.csv skeleton from a SapSurvey sections.tsv: Box sections only, optionally only those named
    // in a forces file. a = t3, b = t2, t = tf; stiffeners "yes" with h_a = b, h_b = a; K 1, L 10 ft;
    // r_a = SAP r22, r_b = SAP r33 (I_a is about the axis across b, i.e. SAP I22).
    // capacitiesCsv (optional): Section, Tension, Bending, Shear2, Shear3 (ksi, before the increase) -> the sig_* overrides.
    // carbonSections (optional): section names that get material "CARBON" instead of material.
    public static string Skeleton(string sectionsTsv, string forcesPath, string material, string outCsv, string capacitiesCsv, string[] carbonSections)
    {
        return Skeleton(sectionsTsv, forcesPath, material, outCsv, capacitiesCsv, carbonSections, double.NaN);
    }
    // stiffSpacing: transverse stiffener spacing (in) for every section, NaN = blank (the sheet's shear as written).
    public static string Skeleton(string sectionsTsv, string forcesPath, string material, string outCsv, string capacitiesCsv, string[] carbonSections, double stiffSpacing)
    {
        HashSet<string> carbon = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (carbonSections != null) foreach (string k in carbonSections) carbon.Add(k.Trim());
        Dictionary<string, Dictionary<string, string>> capRows = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(capacitiesCsv))
            foreach (Dictionary<string, string> r in DuctTables.ReadCsv(capacitiesCsv)) capRows[r["Section"]] = r;
        List<string> noCap = new List<string>();
        HashSet<string> used = null;
        if (!string.IsNullOrEmpty(forcesPath))
        {
            used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (StreamReader rd = new StreamReader(forcesPath))
            {
                string[] h = rd.ReadLine().Split('\t');
                int iS = Col(h, "Section");
                string line;
                while ((line = rd.ReadLine()) != null) if (line.Length > 0) used.Add(line.Split('\t')[iS]);
            }
        }
        List<string> rows = new List<string>();
        rows.Add("Section,Material,a_in,b_in,t_in,stiff_a,stiff_b,h_a_in,h_b_in,K,L_ft,sig_t_ksi,sig_m2_ksi,sig_m3_ksi,sig_v2_ksi,sig_v3_ksi,r_a_in,r_b_in,stiff_spacing_in,Notes");
        int n = 0, skipped = 0;
        foreach (Dictionary<string, string> r in DuctTables.ReadCsv(sectionsTsv))
        {
            if (!string.Equals(r["Type"], "Box", StringComparison.OrdinalIgnoreCase)) continue;
            if (used != null && !used.Contains(r["Section"])) { skipped++; continue; }
            string note = r["tf_in"] == r["tw_in"] ? "" : "tf <> tw: check t";
            string st = "", sm = "", sv2 = "", sv3 = "";
            Dictionary<string, string> cr;
            if (capRows.TryGetValue(r["Section"], out cr)) { st = cr["Tension"]; sm = cr["Bending"]; sv2 = cr["Shear2"]; sv3 = cr["Shear3"]; }
            else if (capRows.Count > 0) noCap.Add(r["Section"]);
            rows.Add(string.Join(",", new string[] { r["Section"], carbon.Contains(r["Section"]) ? "CARBON" : material, r["t3_in"], r["t2_in"], r["tf_in"], "yes", "yes", r["t2_in"], r["t3_in"], "1.0", "10", st, sm, sm, sv2, sv3, r["r22_in"], r["r33_in"], double.IsNaN(stiffSpacing) ? "" : G(stiffSpacing), note }));
            n++;
        }
        File.WriteAllText(outCsv, string.Join("\r\n", rows) + "\r\n");
        return F("{0}: {1} Box sections{2}", outCsv, n, used != null ? F(" (skipped {0} not in the forces file)", skipped) : "")
            + (noCap.Count > 0 ? "\r\nnot in the capacities file (computed allowables): " + string.Join(", ", noCap) : "");
    }

    static int Col(string[] head, string name)
    {
        for (int i = 0; i < head.Length; i++) if (string.Equals(head[i].Trim(), name, StringComparison.OrdinalIgnoreCase)) return i;
        throw new Exception("column " + name + " not found");
    }
    static List<string> Sorted(HashSet<string> s) { List<string> l = new List<string>(s); l.Sort(StringComparer.OrdinalIgnoreCase); return l; }
    static double N(string s) { return double.Parse(s, NumberStyles.Float, Inv); }
    static string G(double v) { return v.ToString("G6", Inv); }
    static string F(string fmt, params object[] args) { return string.Format(Inv, fmt, args); }
}
