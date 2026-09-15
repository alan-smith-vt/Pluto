// Rebuild a linear + SRSS combination from its load cases in a SapSurvey forces.tsv and compare with SAP's own rows.
// Max = sum(linear) + sqrt(sum(srss^2)), Min = sum(linear) - sqrt(...), per force component (P V2 V3 T M2 M3).
// C# 5, no SAP dependency. This is the recombination the joint-release optimizer applies after correcting each case.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

public static class DuctCombo
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly string[] Comp = { "P", "V2", "V3", "T", "M2", "M3" };

    public static string CheckLinearSrss(string forcesPath, string combo, string[] linear, string[] srss, double linearFactor, out bool ok)
    {
        HashSet<string> want = new HashSet<string>(StringComparer.Ordinal);
        foreach (string s in linear) want.Add(s);
        foreach (string s in srss) want.Add(s);
        // case -> (frame \t station) -> 6 components; SAP combo rows: "Max" / "Min" -> key -> 6
        Dictionary<string, Dictionary<string, double[]>> cases = new Dictionary<string, Dictionary<string, double[]>>(StringComparer.Ordinal);
        Dictionary<string, Dictionary<string, double[]>> sap = new Dictionary<string, Dictionary<string, double[]>>(StringComparer.OrdinalIgnoreCase);
        foreach (string s in want) cases[s] = new Dictionary<string, double[]>();
        sap["Max"] = new Dictionary<string, double[]>(); sap["Min"] = new Dictionary<string, double[]>();
        HashSet<string> seenCases = new HashSet<string>();

        using (StreamReader rd = new StreamReader(forcesPath))
        {
            string[] h = rd.ReadLine().Split('\t');
            int iF = Col(h, "Frame"), iSta = Col(h, "Station_in"), iC = Col(h, "OutputCase"), iStep = Col(h, "StepType");
            int[] iv = { Col(h, "P_kip"), Col(h, "V2_kip"), Col(h, "V3_kip"), Col(h, "T_kipin"), Col(h, "M2_kipin"), Col(h, "M3_kipin") };
            string line;
            while ((line = rd.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                string[] c = line.Split('\t');
                seenCases.Add(c[iC]);
                Dictionary<string, double[]> target = null;
                if (c[iC] == combo) sap.TryGetValue(c[iStep], out target);
                else if (want.Contains(c[iC])) target = cases[c[iC]];
                if (target == null) continue;
                double[] v = new double[6];
                for (int k = 0; k < 6; k++) v[k] = double.Parse(c[iv[k]], NumberStyles.Float, Inv);
                target[c[iF] + "\t" + c[iSta]] = v;
            }
        }

        StringBuilder sb = new StringBuilder();
        ok = true;
        List<string> missing = new List<string>();
        if (!seenCases.Contains(combo)) missing.Add(combo);
        foreach (string s in want) if (!seenCases.Contains(s)) missing.Add(s);
        if (missing.Count > 0)
        {
            ok = false;
            sb.AppendLine("not in the forces file (export with -IncludeCases; names must match exactly):");
            foreach (string s in missing) sb.AppendLine("  |" + s + "|");
            return sb.ToString();
        }

        double[] worstAbs = new double[6], worstRel = new double[6], peak = new double[6];
        string[] worstAt = new string[6];
        int n = 0, noCase = 0;
        foreach (string step in new string[] { "Max", "Min" })
        {
            double sign = step == "Max" ? 1 : -1;
            foreach (KeyValuePair<string, double[]> kv in sap[step])
            {
                double[] lin = new double[6], sq = new double[6];
                bool complete = true;
                foreach (string s in linear)
                {
                    double[] v;
                    if (!cases[s].TryGetValue(kv.Key, out v)) { complete = false; break; }
                    for (int k = 0; k < 6; k++) lin[k] += linearFactor * v[k];
                }
                foreach (string s in srss)
                {
                    double[] v;
                    if (!complete || !cases[s].TryGetValue(kv.Key, out v)) { complete = false; break; }
                    for (int k = 0; k < 6; k++) sq[k] += v[k] * v[k];
                }
                if (!complete) { noCase++; continue; }
                n++;
                for (int k = 0; k < 6; k++)
                {
                    double mine = lin[k] + sign * Math.Sqrt(sq[k]);
                    double d = Math.Abs(mine - kv.Value[k]);
                    peak[k] = Math.Max(peak[k], Math.Abs(kv.Value[k]));
                    if (d > worstAbs[k]) { worstAbs[k] = d; worstAt[k] = step + " frame " + kv.Key.Replace("\t", " station ") + F(" (SAP {0}, rebuilt {1})", G(kv.Value[k]), G(mine)); }
                    double rel = d / Math.Max(1e-6, Math.Abs(kv.Value[k]));
                    if (Math.Abs(kv.Value[k]) > 1e-3 * Math.Max(1, peak[k])) worstRel[k] = Math.Max(worstRel[k], rel);
                }
            }
        }
        sb.AppendLine(F("combo |{0}| = {1} x ({2}) +/- SRSS({3})", combo, G(linearFactor), string.Join(" + ", linear), string.Join(", ", srss)));
        sb.AppendLine(F("station rows compared {0} (Max + Min){1}", n, noCase > 0 ? F(", {0} skipped: a component case row missing", noCase) : ""));
        sb.AppendLine("component   max |diff|        max rel diff   peak |SAP|    worst at");
        for (int k = 0; k < 6; k++)
        {
            sb.AppendLine(F("  {0,-4} {1,16} {2,16} {3,12}    {4}", Comp[k], G(worstAbs[k]), G(worstRel[k]), G(peak[k]), worstAt[k] ?? ""));
            if (worstAbs[k] > 1e-6 * Math.Max(1, peak[k])) ok = false;
        }
        if (n == 0) ok = false;
        sb.AppendLine(ok ? "COMBO CHECK OK (every component within 1e-6 of peak)" : "COMBO CHECK: differences above 1e-6 of peak, see worst rows");
        return sb.ToString();
    }

    static int Col(string[] head, string name)
    {
        for (int i = 0; i < head.Length; i++) if (string.Equals(head[i].Trim(), name, StringComparison.OrdinalIgnoreCase)) return i;
        throw new Exception("column " + name + " not found");
    }
    static string G(double v) { return v.ToString("G6", Inv); }
    static string F(string fmt, params object[] args) { return string.Format(Inv, fmt, args); }
}
