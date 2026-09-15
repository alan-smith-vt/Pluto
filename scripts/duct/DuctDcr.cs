// Rectangular duct DCRs, transcribed from the <project> Mathcad sheets (Duct_Mathcad_Carbon / _Stainless).
// Spec: Notes vault, Projects/<project>/HVAC Duct DCR Equations.md. C# 5, no SAP dependency; units kip, in, ksi.
// Axis mapping (SAP Box/Tube, verified on SAP 22 and 26): a = t3, b = t2, M_a = M2, M_b = M3, V_a = V2, V_b = V3, P > 0 tension.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

public class DuctMaterial
{
    public string Name;
    public bool Stainless;                       // picks the F_n formula and the shear constants
    public double E, Mu, Fy, Fu;                  // ksi, -, ksi, ksi
    public double EtaF = 1.0, EtaE = 1.0;         // temperature reductions (yield, modulus)
    public double FyLambda = double.NaN;          // Fy in lambda_c only (NaN = Fy); the Excel workflow uses 33 for 304L
    public double OmegaT = 1, OmegaC = 1, OmegaF = 1, OmegaV = 1;
    public double Alpha = 1, Beta0 = 1, Beta1 = 1, Beta2 = 1;   // stainless global buckling coefficients
}

public class DuctSection
{
    public string Name;
    public DuctMaterial Material;
    public double A, B, T;                        // in: a (= SAP t3), b (= SAP t2), wall thickness
    public bool StiffA, StiffB;
    public double HA, HB;                         // in: web stiffener depth, a / b direction
    public double K = 1.0, LFt = 10.0;            // effective length factor, span (ft)
    // Allowable overrides (ksi, before the stress increase), e.g. the Excel "standard capacities" sheet. NaN = computed.
    public double SigTOverride = double.NaN, SigM2Override = double.NaN, SigM3Override = double.NaN;
    public double SigV2Override = double.NaN, SigV3Override = double.NaN;
    public double RAOverride = double.NaN, RBOverride = double.NaN;   // in: SAP r22 / r33 (exact box) instead of the thin-wall r
}

// Shear constants of the web shear capacity, per direction: lambda limit, c, p in
// V_n = V_y if lambda_v <= lim, else (1 - c (V_cr/V_y)^p) (V_cr/V_y)^p V_y.
public class DuctShearRule
{
    public double Lim, C, P;
    public DuctShearRule(double lim, double c, double p) { Lim = lim; C = c; P = p; }
    public static DuctShearRule Dsm() { return new DuctShearRule(0.776, 0.15, 0.4); }
    public static DuctShearRule Winter() { return new DuctShearRule(0.673, 0.22, 0.5); }
}

// Everything that depends only on section + material: computed once per section.
public class DuctCapacity
{
    public DuctSection Section;
    public double Ag, Awa, Awb, Ia, Ib, Ra, Rb, LambdaMax, Sa, Sb;
    public double AEffA, BEffB, Ae, Iae, Ibe, Sae, Sbe;
    public double SigTAll, Fcre, LambdaC, Fn, SigCAll, SigMAll, SigMaAll, SigMbAll;
    public double Kva, Fcra, Vya, Vcra, LambdaVa, Vna, SigVaAll;
    public double Kvb, Fcrb, Vyb, Vcrb, LambdaVb, Vnb, SigVbAll;

    // Carbon sheet as written: direction a DSM (0.776/0.15/0.4), direction b Winter (0.673/0.22/0.5).
    // Stainless sheet: Winter both ways. Pass explicit rules to override once the sheet owner answers.
    public static DuctCapacity Compute(DuctSection s) { return Compute(s, null, null); }

    public static DuctCapacity Compute(DuctSection s, DuctShearRule ruleA, DuctShearRule ruleB)
    {
        DuctMaterial m = s.Material;
        if (ruleA == null) ruleA = m.Stainless ? DuctShearRule.Winter() : DuctShearRule.Dsm();
        if (ruleB == null) ruleB = DuctShearRule.Winter();
        DuctCapacity c = new DuctCapacity();
        c.Section = s;
        double a = s.A, b = s.B, t = s.T, L = s.LFt * 12.0;

        // Unreduced section
        c.Ag = 2 * t * (a + b);
        c.Awa = s.HA * t;
        c.Awb = s.HB * t;
        c.Ia = 2 * a * t * Sq(b / 2) + 2 * t * b * b * b / 12;
        c.Ib = 2 * b * t * Sq(a / 2) + 2 * t * a * a * a / 12;
        c.Ra = double.IsNaN(s.RAOverride) ? Math.Sqrt(c.Ia / c.Ag) : s.RAOverride;
        c.Rb = double.IsNaN(s.RBOverride) ? Math.Sqrt(c.Ib / c.Ag) : s.RBOverride;
        c.LambdaMax = Math.Max(s.K * L / c.Ra, s.K * L / c.Rb);
        c.Sa = 2 * c.Ia / b;
        c.Sb = 2 * c.Ib / a;

        // Effective section
        c.AEffA = Math.Min(30 * t, a / 2);
        c.BEffB = Math.Min(30 * t, b / 2);
        c.Ae = 4 * t * (c.AEffA + c.BEffB);
        c.Iae = 2 * 2 * c.AEffA * t * Sq(b / 2) + 2 * 2 * t * Math.Pow(c.BEffB, 3) / 12 + 2 * 2 * t * c.BEffB * Sq((b - c.BEffB) / 2);
        c.Ibe = 2 * 2 * c.BEffB * t * Sq(a / 2) + 2 * 2 * t * Math.Pow(c.AEffA, 3) / 12 + 2 * 2 * t * c.AEffA * Sq((a - c.AEffA) / 2);
        c.Sae = 2 * c.Iae / b;
        c.Sbe = 2 * c.Ibe / a;

        // Tension, compression, bending allowables
        c.SigTAll = m.EtaF * m.Fy / m.OmegaT;
        c.Fcre = Math.PI * Math.PI * m.E / Sq(c.LambdaMax);
        c.LambdaC = Math.Sqrt((double.IsNaN(m.FyLambda) ? m.Fy : m.FyLambda) / c.Fcre);
        if (!m.Stainless)
            c.Fn = c.LambdaC <= 1.5 ? Math.Pow(0.658, Sq(c.LambdaC)) * m.Fy : 0.877 / Sq(c.LambdaC) * m.Fy;
        else if (c.LambdaC <= m.Beta0)
            c.Fn = m.Fy + (1 - c.LambdaC / m.Beta0) * (m.Fu - m.Fy);
        else if (c.LambdaC <= 1.8)
            c.Fn = 1.2 * Math.Pow(m.Beta1, Math.Pow(c.LambdaC, m.Alpha)) * m.Fy;
        else
            c.Fn = m.Beta2 * c.Fcre;
        c.SigCAll = c.Fn / m.OmegaC;
        c.SigMAll = m.Fy / m.OmegaF;

        // Shear, each direction
        Shear(m, a, s.HA, t, c.Awa, s.StiffA, ruleA, out c.Vya, out c.Kva, out c.Fcra, out c.Vcra, out c.LambdaVa, out c.Vna, out c.SigVaAll);
        Shear(m, b, s.HB, t, c.Awb, s.StiffB, ruleB, out c.Vyb, out c.Kvb, out c.Fcrb, out c.Vcrb, out c.LambdaVb, out c.Vnb, out c.SigVbAll);

        // Overrides (a = M2 / V2, b = M3 / V3); compression is always computed.
        c.SigMaAll = c.SigMAll; c.SigMbAll = c.SigMAll;
        if (!double.IsNaN(s.SigTOverride)) c.SigTAll = s.SigTOverride;
        if (!double.IsNaN(s.SigM2Override)) c.SigMaAll = s.SigM2Override;
        if (!double.IsNaN(s.SigM3Override)) c.SigMbAll = s.SigM3Override;
        if (!double.IsNaN(s.SigV2Override)) c.SigVaAll = s.SigV2Override;
        if (!double.IsNaN(s.SigV3Override)) c.SigVbAll = s.SigV3Override;
        return c;
    }

    static void Shear(DuctMaterial m, double side, double h, double t, double aw, bool stiff, DuctShearRule rule,
        out double vy, out double kv, out double fcr, out double vcr, out double lam, out double vn, out double sigAll)
    {
        vy = 0.6 * m.EtaF * aw * m.Fy;
        if (stiff) kv = side / h <= 1.0 ? 4.00 + 5.34 / Sq(side / h) : 5.34 + 4.00 / Sq(side / h);
        else kv = 5.34;
        fcr = Math.PI * Math.PI * m.EtaE * m.E * kv / (12 * (1 - m.Mu * m.Mu) * Sq(h / t));
        vcr = aw * fcr;
        lam = Math.Sqrt(vy / vcr);
        vn = lam <= rule.Lim ? vy : (1 - rule.C * Math.Pow(vcr / vy, rule.P)) * Math.Pow(vcr / vy, rule.P) * vy;
        sigAll = vn / (2 * aw) / m.OmegaV;
    }

    static double Sq(double x) { return x * x; }
}

// DCRs at one output station. f = stress increase factor of the combination's limit state.
public class DuctDcr
{
    public double DcrT, DcrC, DcrMa, DcrMb, DcrVa, DcrVb, Combined, Governing;

    public static double StressIncrease(string limitState)
    {
        return limitState == "A" || limitState == "B" ? 1.0 : 1.5;
    }

    // Sheet form: separate tension T and compression P magnitudes (the Mathcad template applies both).
    public static DuctDcr FromSheet(DuctCapacity c, double f, double T, double P, double Ma, double Mb, double Va, double Vb)
    {
        DuctDcr d = new DuctDcr();
        double sigT = T / c.Ag, sigC = P / c.Ae;
        double sigMa = Ma / c.Sae, sigMb = Mb / c.Sbe;
        double sigVa = Va / (2 * c.Awa), sigVb = Vb / (2 * c.Awb);
        d.DcrT = sigT / (f * c.SigTAll);
        d.DcrC = sigC / (f * c.SigCAll);
        d.DcrMa = sigMa / (f * c.SigMaAll);
        d.DcrMb = sigMb / (f * c.SigMbAll);
        d.DcrVa = sigVa / (f * c.SigVaAll);
        d.DcrVb = sigVb / (f * c.SigVbAll);
        d.Combined = Math.Max(d.DcrT, d.DcrC) + d.DcrMa + d.DcrMb;
        d.Governing = Math.Max(d.Combined, Math.Max(d.DcrVa, d.DcrVb));
        return d;
    }

    // SAP station form: signed P (> 0 tension), frame-local V2, V3, M2, M3 (kip, kip-in).
    public static DuctDcr FromSap(DuctCapacity c, double f, double p, double v2, double v3, double m2, double m3)
    {
        return FromSheet(c, f, p > 0 ? p : 0, p < 0 ? -p : 0, Math.Abs(m2), Math.Abs(m3), Math.Abs(v2), Math.Abs(v3));
    }
}

// Member tables (CSV, the layout of assets/duct-*.csv in the Notes vault).
public static class DuctTables
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static List<Dictionary<string, string>> ReadCsv(string path)
    {
        List<Dictionary<string, string>> rows = new List<Dictionary<string, string>>();
        string[] lines = File.ReadAllLines(path);
        if (lines.Length == 0) return rows;
        char sep = lines[0].IndexOf('\t') >= 0 ? '\t' : ',';
        string[] head = lines[0].Split(sep);
        for (int i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim().Length == 0) continue;
            string[] cells = lines[i].Split(sep);
            Dictionary<string, string> r = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int k = 0; k < head.Length; k++) r[head[k].Trim()] = k < cells.Length ? cells[k].Trim() : "";
            rows.Add(r);
        }
        return rows;
    }

    public static Dictionary<string, DuctMaterial> Materials(string path)
    {
        Dictionary<string, DuctMaterial> d = new Dictionary<string, DuctMaterial>(StringComparer.OrdinalIgnoreCase);
        foreach (Dictionary<string, string> r in ReadCsv(path))
        {
            DuctMaterial m = new DuctMaterial();
            m.Name = r["Material"];
            m.Stainless = string.Equals(Get(r, "Sheet", "Carbon"), "Stainless", StringComparison.OrdinalIgnoreCase);
            m.E = Num(r, "E_ksi", double.NaN); m.Mu = Num(r, "mu", double.NaN);
            m.Fy = Num(r, "Fy_ksi", double.NaN); m.Fu = Num(r, "Fu_ksi", double.NaN);
            m.EtaF = Num(r, "eta_f", 1); m.EtaE = Num(r, "eta_e", 1);
            m.FyLambda = Num(r, "Fy_lambda_ksi", double.NaN);
            m.OmegaT = Num(r, "Omega_t", double.NaN); m.OmegaC = Num(r, "Omega_c", double.NaN);
            m.OmegaF = Num(r, "Omega_f", double.NaN); m.OmegaV = Num(r, "Omega_v", double.NaN);
            m.Alpha = Num(r, "alpha", 1); m.Beta0 = Num(r, "beta0", 1); m.Beta1 = Num(r, "beta1", 1); m.Beta2 = Num(r, "beta2", 1);
            d[m.Name] = m;
        }
        return d;
    }

    public static Dictionary<string, DuctSection> Sections(string path, Dictionary<string, DuctMaterial> materials)
    {
        Dictionary<string, DuctSection> d = new Dictionary<string, DuctSection>(StringComparer.OrdinalIgnoreCase);
        foreach (Dictionary<string, string> r in ReadCsv(path))
        {
            DuctSection s = new DuctSection();
            s.Name = r["Section"];
            string mat = r["Material"];
            if (!materials.TryGetValue(mat, out s.Material)) throw new Exception("section " + s.Name + ": unknown material " + mat);
            s.A = Num(r, "a_in", double.NaN); s.B = Num(r, "b_in", double.NaN); s.T = Num(r, "t_in", double.NaN);
            s.StiffA = Yes(r, "stiff_a"); s.StiffB = Yes(r, "stiff_b");
            s.HA = Num(r, "h_a_in", s.A); s.HB = Num(r, "h_b_in", s.B);   // blank h = full wall length
            s.K = Num(r, "K", 1.0); s.LFt = Num(r, "L_ft", 10.0);
            s.SigTOverride = Num(r, "sig_t_ksi", double.NaN); s.SigM2Override = Num(r, "sig_m2_ksi", double.NaN);
            s.SigM3Override = Num(r, "sig_m3_ksi", double.NaN); s.SigV2Override = Num(r, "sig_v2_ksi", double.NaN);
            s.SigV3Override = Num(r, "sig_v3_ksi", double.NaN);
            s.RAOverride = Num(r, "r_a_in", double.NaN); s.RBOverride = Num(r, "r_b_in", double.NaN);
            d[s.Name] = s;
        }
        return d;
    }

    // Combo -> limit state; rows with Include = no are skipped.
    public static Dictionary<string, string> Combos(string path)
    {
        Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Dictionary<string, string> r in ReadCsv(path))
            if (!string.Equals(Get(r, "Include", "yes"), "no", StringComparison.OrdinalIgnoreCase)) d[r["Combo"]] = Get(r, "LS", "A");
        return d;
    }

    static string Get(Dictionary<string, string> r, string key, string def) { string v; return r.TryGetValue(key, out v) && v.Length > 0 ? v : def; }
    static bool Yes(Dictionary<string, string> r, string key) { return string.Equals(Get(r, key, "no"), "yes", StringComparison.OrdinalIgnoreCase); }
    static double Num(Dictionary<string, string> r, string key, double def)
    {
        string v; double x;
        if (!r.TryGetValue(key, out v) || v.Length == 0) return def;
        if (!double.TryParse(v, NumberStyles.Float, Inv, out x)) throw new Exception("bad number in " + key + ": " + v);
        return x;
    }
}
