# The duct DCR / expansion-joint chain, offline and synthetic (no SAP). Each check compares the code with an independent
# calculation made here. Windows PowerShell 5.1.
#   powershell -ExecutionPolicy Bypass -File scripts\duct\tests\Test-DuctDcrChain.ps1 [-Keep]
# 1. DuctRelease: an abstract linear model (fixed seed) with stiff 6-DOF links between master / far DOF sets, exported
#    in SapRelease's files (forces.tsv, linkdisp.tsv, links.tsv; unit pair = +1 far, -1 master). Releasing links by
#    superposition must equal a direct re-solve with those links removed; combo 18 = dead + steel +/- SRSS(seismic) of
#    the released vectors; a release that leaves a mechanism is reported singular.
# 2. DuctCombo: SAP-style combo rows built here are recognised; one perturbed row is caught.
# 3. DuctDcr: the Mathcad template check (DuctEvaluate.CheckTemplate, golden values from the sheets) and invariants
#    (DCR linear in load, tension / compression split by sign, stress increase 1.5 except limit states A / B).
param([switch]$Keep)
$ErrorActionPreference = "Stop"
$duct = Split-Path $PSScriptRoot
Add-Type -Path (Join-Path $duct "DuctDcr.cs"), (Join-Path $duct "DuctEvaluate.cs"), (Join-Path $duct "DuctCombo.cs"), (Join-Path $duct "DuctRelease.cs"), (Join-Path $duct "DuctReport.cs")
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

// Abstract linear structure: K u = f over m DOFs; frame "forces" are fixed linear maps of u (rows x 6). Links join
// DOFs master[0..5] and far[0..5] with stiffness k (translations kt, rotations kr).
public class ReleaseModel
{
    public int M, Rows = 8;
    public double[,] Ks;                      // structure without links
    public double[][,] B;                     // per row: 6 x M
    public List<int[]> LinkMaster = new List<int[]>(), LinkFar = new List<int[]>();
    public List<string> LinkNames = new List<string>();
    public double Kt = 5e6, Kr = 5e8;
    public Dictionary<string, double[]> Loads = new Dictionary<string, double[]>();
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // mechanismLink: that link's far DOFs get no structure stiffness (only the link holds them)
    public static ReleaseModel Make(int seed, int nLinks, int mechanismLink)
    {
        var r = new Random(seed); var md = new ReleaseModel();
        md.M = 6 + 12 * nLinks;
        int m = md.M;
        var A = new double[m, m];
        for (int i = 0; i < m; i++) for (int j = 0; j < m; j++) A[i, j] = r.NextDouble() - 0.5;
        md.Ks = new double[m, m];
        for (int i = 0; i < m; i++) for (int j = 0; j < m; j++) { double s = 0; for (int k = 0; k < m; k++) s += A[i, k] * A[j, k]; md.Ks[i, j] = 100 * s + (i == j ? 50 : 0); }
        for (int l = 0; l < nLinks; l++)
        {
            int[] ms = new int[6], fs = new int[6];
            for (int k = 0; k < 6; k++) { ms[k] = 6 + 12 * l + k; fs[k] = 12 + 12 * l + k; }
            md.LinkMaster.Add(ms); md.LinkFar.Add(fs); md.LinkNames.Add("L" + (l + 1));
            if (l == mechanismLink)
                foreach (int a in fs) for (int j = 0; j < m; j++) { md.Ks[a, j] = 0; md.Ks[j, a] = 0; }   // far side held by the link alone
        }
        md.B = new double[md.Rows][,];
        for (int q = 0; q < md.Rows; q++) { md.B[q] = new double[6, m]; for (int c = 0; c < 6; c++) for (int j = 0; j < m; j++) md.B[q][c, j] = 10 * (r.NextDouble() - 0.5); }
        foreach (string n in new[] { "DEAD", "STEEL", "SX", "SZ", "SY", "C19" }) { var f = new double[m]; for (int j = 0; j < m; j++) f[j] = r.NextDouble() - 0.5; md.Loads[n] = f; }
        return md;
    }

    public double K(int k) { return k < 3 ? Kt : Kr; }

    // stiffness with the links in `keep` (null = all links)
    public double[,] Stiffness(ICollection<string> keep)
    {
        var K = (double[,])Ks.Clone();
        for (int l = 0; l < LinkNames.Count; l++)
        {
            if (keep != null && !keep.Contains(LinkNames[l])) continue;
            for (int k = 0; k < 6; k++)
            {
                int a = LinkMaster[l][k], b = LinkFar[l][k]; double s = this.K(k);
                K[a, a] += s; K[b, b] += s; K[a, b] -= s; K[b, a] -= s;
            }
        }
        return K;
    }

    public static double[] Solve(double[,] K, double[] f)
    {
        int n = f.Length; var a = (double[,])K.Clone(); var x = (double[])f.Clone();
        for (int c = 0; c < n; c++)
        {
            int p = c; for (int i = c + 1; i < n; i++) if (Math.Abs(a[i, c]) > Math.Abs(a[p, c])) p = i;
            if (p != c) { for (int j = 0; j < n; j++) { double t = a[c, j]; a[c, j] = a[p, j]; a[p, j] = t; } double tt = x[c]; x[c] = x[p]; x[p] = tt; }
            for (int i = c + 1; i < n; i++) { double q = a[i, c] / a[c, c]; for (int j = c; j < n; j++) a[i, j] -= q * a[c, j]; x[i] -= q * x[c]; }
        }
        for (int i = n - 1; i >= 0; i--) { double s = x[i]; for (int j = i + 1; j < n; j++) s -= a[i, j] * x[j]; x[i] = s / a[i, i]; }
        return x;
    }

    public double[][] Forces(double[] u)
    {
        var o = new double[Rows][];
        for (int q = 0; q < Rows; q++) { o[q] = new double[6]; for (int c = 0; c < 6; c++) { double s = 0; for (int j = 0; j < M; j++) s += B[q][c, j] * u[j]; o[q][c] = s; } }
        return o;
    }

    static string N(double v) { return v.ToString("R", Inv); }
    static string Row(int q) { return "F" + (q / 2 + 1) + "\tDUCT\t" + (q % 2 == 0 ? "0" : "120"); }

    // SapRelease export of the all-links model: every load vector plus one unit pair case per link DOF
    public void Export(string dir)
    {
        Directory.CreateDirectory(dir);
        var K = Stiffness(null);
        var cases = new List<KeyValuePair<string, double[]>>();
        foreach (var kv in Loads) cases.Add(kv);
        for (int l = 0; l < LinkNames.Count; l++)
            for (int k = 0; k < 6; k++)
            {
                var f = new double[M]; f[LinkFar[l][k]] = 1; f[LinkMaster[l][k]] = -1;
                cases.Add(new KeyValuePair<string, double[]>("PAIR_" + LinkNames[l] + "_" + (k + 1), f));
            }
        var fs = new StringBuilder("Frame\tSection\tStation_in\tOutputCase\tStepType\tP_kip\tV2_kip\tV3_kip\tT_kipin\tM2_kipin\tM3_kipin\n");
        var ld = new StringBuilder("Link\tSide\tOutputCase\tStepType\tU1\tU2\tU3\tR1\tR2\tR3\n");
        foreach (var c in cases)
        {
            double[] u = Solve(K, c.Value); double[][] F = Forces(u);
            for (int q = 0; q < Rows; q++)
            {
                fs.Append(Row(q)).Append('\t').Append(c.Key).Append('\t');
                for (int k = 0; k < 6; k++) fs.Append('\t').Append(N(F[q][k]));
                fs.Append('\n');
            }
            for (int l = 0; l < LinkNames.Count; l++)
                foreach (string side in new[] { "master", "other" })
                {
                    int[] dofs = side == "master" ? LinkMaster[l] : LinkFar[l];
                    ld.Append(LinkNames[l]).Append('\t').Append(side).Append('\t').Append(c.Key).Append('\t');
                    for (int k = 0; k < 6; k++) ld.Append('\t').Append(N(u[dofs[k]]));
                    ld.Append('\n');
                }
        }
        var lk = new StringBuilder("Link\tCandidate\tMaster\tOther\tkt_kip_in\tkr_kipin_rad\tPatterns\n");
        for (int l = 0; l < LinkNames.Count; l++)
        {
            var p = new List<string>(); for (int k = 0; k < 6; k++) p.Add("PAIR_" + LinkNames[l] + "_" + (k + 1));
            lk.Append(LinkNames[l]).Append("\tJ").Append(l + 1).Append("\tM").Append(l + 1).Append("\tO").Append(l + 1).Append('\t')
              .Append(N(Kt)).Append('\t').Append(N(Kr)).Append('\t').Append(string.Join(",", p.ToArray())).Append('\n');
        }
        File.WriteAllText(Path.Combine(dir, "forces.tsv"), fs.ToString());
        File.WriteAllText(Path.Combine(dir, "linkdisp.tsv"), ld.ToString());
        File.WriteAllText(Path.Combine(dir, "links.tsv"), lk.ToString());
    }

    // largest |a - b| over rows / components, relative to the largest |b|
    public static double RelDiff(double[][] a, double[][] b)
    {
        double d = 0, peak = 1e-12;
        for (int q = 0; q < b.Length; q++) for (int k = 0; k < 6; k++) { d = Math.Max(d, Math.Abs(a[q][k] - b[q][k])); peak = Math.Max(peak, Math.Abs(b[q][k])); }
        return d / peak;
    }
}
'@

$script:pass = 0; $script:fail = 0
function Check([string]$what, [bool]$ok, [string]$detail = "") {
    if ($ok) { $script:pass++; Write-Output "ok   $what" } else { $script:fail++; Write-Output ("FAIL $what" + $(if ($detail) { "  ($detail)" } else { "" })) }
}
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("pluto-dcr-test-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
$vectors = [string[]]@("DEAD", "STEEL", "SX", "SZ", "SY", "C19")
$seis = [string[]]@("SX", "SZ", "SY")
try {
    # ---- 1. DuctRelease vs a direct re-solve without the released links
    $md = [ReleaseModel]::Make(17, 2, -1)
    $dir = Join-Path $tmp "two-links"; $md.Export($dir)
    foreach ($set in @(@("L1"), @("J2"), $null)) {
        $rel = [DuctRelease]::Load($dir)
        $r = $rel.Release($(if ($set) { [string[]]$set } else { $null }), $vectors, "DEAD", "STEEL", $seis, "18")
        $label = $(if ($set) { $set -join "," } else { "all" })
        Check "release {$label}: not singular" (-not $r.Singular) ("pivot ratio " + $r.PivotRatio)
        if ($r.Singular) { continue }
        $released = @{ "L1" = $true; "L2" = $true }
        if ($set) { $released = @{}; foreach ($s in $set) { $released[($s -replace '^J', 'L')] = $true } }
        $kept = New-Object 'System.Collections.Generic.List[string]'
        foreach ($n in $md.LinkNames) { if (-not $released.ContainsKey($n)) { $kept.Add($n) } }
        $K = $md.Stiffness($kept)
        $direct = @{}
        foreach ($v in $vectors) {
            $direct[$v] = $md.Forces([ReleaseModel]::Solve($K, $md.Loads[$v]))
            $got = $r.Table.Rows[$rel.Base.VectorKey($v)]
            $d = [ReleaseModel]::RelDiff($got, $direct[$v])
            Check "release {$label}: $v = direct solve without the link(s)" ($d -lt 1e-7) ("rel diff {0:E2}" -f $d)
        }
        # combo 18 from the direct (released) vectors
        $mx = $r.Table.Rows["18`tMax"]; $mn = $r.Table.Rows["18`tMin"]; $worst = 0.0
        for ($q = 0; $q -lt $md.Rows; $q++) {
            for ($k = 0; $k -lt 6; $k++) {
                $lin = $direct["DEAD"][$q][$k] + $direct["STEEL"][$q][$k]
                $sr = [Math]::Sqrt(($seis | ForEach-Object { [Math]::Pow($direct[$_][$q][$k], 2) } | Measure-Object -Sum).Sum)
                $peak = [Math]::Max(1, [Math]::Abs($lin) + $sr)
                $worst = [Math]::Max($worst, [Math]::Max([Math]::Abs($mx[$q][$k] - ($lin + $sr)), [Math]::Abs($mn[$q][$k] - ($lin - $sr))) / $peak)
            }
        }
        Check "release {$label}: combo 18 Max / Min = dead + steel +/- SRSS(seismic) of the released vectors" ($worst -lt 1e-7) ("rel diff {0:E2}" -f $worst)
    }
    # a link whose far side has nothing else: releasing it leaves a mechanism
    $mm = [ReleaseModel]::Make(23, 1, 0)
    $dir2 = Join-Path $tmp "mechanism"; $mm.Export($dir2)
    $r2 = ([DuctRelease]::Load($dir2)).Release($null, $vectors, "DEAD", "STEEL", $seis, "18")
    Check "release of a link that alone holds its far side: singular (mechanism)" ($r2.Singular -or $r2.PivotRatio -lt 1e-8) ("pivot ratio " + $r2.PivotRatio)

    # ---- 2. DuctCombo on SAP-style combo rows
    $rows = Get-Content (Join-Path $dir "forces.tsv")
    $head = $rows[0]; $byCase = @{}
    foreach ($ln in $rows[1..($rows.Count - 1)]) { $c = $ln.Split("`t"); $key = $c[0] + "`t" + $c[2]; if (-not $byCase.ContainsKey($c[3])) { $byCase[$c[3]] = [ordered]@{} }; $byCase[$c[3]][$key] = $c }
    $inv = [Globalization.CultureInfo]::InvariantCulture
    $comboLines = New-Object 'System.Collections.Generic.List[string]'
    foreach ($key in $byCase["DEAD"].Keys) {
        foreach ($step in "Max", "Min") {
            $c0 = $byCase["DEAD"][$key]; $vals = @()
            for ($k = 5; $k -le 10; $k++) {
                $lin = 1.2 * ([double]::Parse($byCase["DEAD"][$key][$k], $inv) + [double]::Parse($byCase["STEEL"][$key][$k], $inv))
                $sq = 0.0; foreach ($s in $seis) { $sq += [Math]::Pow([double]::Parse($byCase[$s][$key][$k], $inv), 2) }
                $vals += ($lin + $(if ($step -eq "Max") { 1 } else { -1 }) * [Math]::Sqrt($sq)).ToString("R", $inv)
            }
            $comboLines.Add((@($c0[0], $c0[1], $c0[2], "18 BLC", $step) + $vals) -join "`t")
        }
    }
    $cf = Join-Path $tmp "combo-forces.tsv"
    [IO.File]::WriteAllLines($cf, [string[]](@($rows) + $comboLines))
    $ok = $false; $txt = [DuctCombo]::CheckLinearSrss($cf, "18 BLC", [string[]]@("DEAD", "STEEL"), $seis, 1.2, [ref]$ok)
    Check "DuctCombo: 1.2 (dead + steel) +/- SRSS(seismic) rows recognised" $ok ($txt -split "`n" | Select-Object -Last 2)
    $bad = $comboLines[3].Split("`t"); $bad[9] = ([double]::Parse($bad[9], $inv) * 1.01 + 1).ToString("R", $inv); $comboLines[3] = $bad -join "`t"
    [IO.File]::WriteAllLines($cf, [string[]](@($rows) + $comboLines))
    $ok = $true; $null = [DuctCombo]::CheckLinearSrss($cf, "18 BLC", [string[]]@("DEAD", "STEEL"), $seis, 1.2, [ref]$ok)
    Check "DuctCombo: one perturbed M2 row is caught" (-not $ok)
    $ok = $true; $null = [DuctCombo]::CheckLinearSrss($cf, "NO SUCH COMBO", [string[]]@("DEAD"), $seis, 1.0, [ref]$ok)
    Check "DuctCombo: a missing combo is not OK" (-not $ok)

    # ---- 3. DuctDcr: Mathcad golden values + invariants
    $ok = $false; $txt = [DuctEvaluate]::CheckTemplate([ref]$ok)
    Check "DuctDcr: Mathcad template values (carbon + stainless, DuctEvaluate.CheckTemplate)" $ok ($txt -split "`n" | Where-Object { $_ -match 'MISMATCH' } | Select-Object -First 3)
    $mat = New-Object DuctMaterial; $mat.Name = "T"; $mat.Stainless = $true; $mat.E = 28000; $mat.Mu = 0.3; $mat.Fy = 25; $mat.Fu = 70
    $sec = New-Object DuctSection; $sec.Name = "DUCT 74x28"; $sec.Material = $mat; $sec.A = 28; $sec.B = 74; $sec.T = 0.0625; $sec.HA = 74; $sec.HB = 28
    $cap = [DuctCapacity]::Compute($sec)
    Check "DuctDcr: increase factor 1 for limit states A / B, 1.5 otherwise" ([DuctDcr]::StressIncrease("A") -eq 1 -and [DuctDcr]::StressIncrease("B") -eq 1 -and [DuctDcr]::StressIncrease("C") -eq 1.5 -and [DuctDcr]::StressIncrease("D") -eq 1.5)
    $d1 = [DuctDcr]::FromSap($cap, 1.0, -2.0, 0.3, 0.4, 50, 80); $d3 = [DuctDcr]::FromSap($cap, 1.0, -6.0, 0.9, 1.2, 150, 240)
    Check "DuctDcr: every DCR linear in the load (3 x load = 3 x DCR)" ([Math]::Abs($d3.Combined - 3 * $d1.Combined) -lt 1e-9 * $d3.Combined -and [Math]::Abs($d3.DcrVa - 3 * $d1.DcrVa) -lt 1e-12 -and [Math]::Abs($d3.DcrMb - 3 * $d1.DcrMb) -lt 1e-12)
    $dc = [DuctDcr]::FromSap($cap, 1.5, -2.0, 0, 0, 0, 0); $dc1 = [DuctDcr]::FromSap($cap, 1.0, -2.0, 0, 0, 0, 0)
    Check "DuctDcr: the 1.5 increase divides every DCR by 1.5" ([Math]::Abs($dc.DcrC * 1.5 - $dc1.DcrC) -lt 1e-12)
    $dt = [DuctDcr]::FromSap($cap, 1.0, 2.0, 0, 0, 0, 0)
    Check "DuctDcr: P > 0 is tension only, P < 0 compression only" ($dt.DcrT -gt 0 -and $dt.DcrC -eq 0 -and $dc1.DcrC -gt 0 -and $dc1.DcrT -eq 0)
    $dm = [DuctDcr]::FromSap($cap, 1.0, 0, 0, 0, -50, -80)
    Check "DuctDcr: moment signs do not matter" ([Math]::Abs($dm.Combined - ([DuctDcr]::FromSap($cap, 1.0, 0, 0, 0, 50, 80)).Combined) -lt 1e-15)
    Check "DuctDcr: combined = max(T, C) + Ma + Mb; governing = max(combined, shear)" ([Math]::Abs($d1.Combined - ([Math]::Max($d1.DcrT, $d1.DcrC) + $d1.DcrMa + $d1.DcrMb)) -lt 1e-15 -and $d1.Governing -eq [Math]::Max($d1.Combined, [Math]::Max($d1.DcrVa, $d1.DcrVb)))
} finally {
    if ($Keep) { Write-Output "kept: $tmp" } elseif (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
}
Write-Output ("{0} passed, {1} failed" -f $script:pass, $script:fail)
exit $(if ($script:fail) { 1 } else { 0 })
