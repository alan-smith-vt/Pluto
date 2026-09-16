# Expansion-joint optimizer on the model open in SAP2000 (HVAC), end to end, with an HTML report.
# Windows PowerShell 5.1. Settings come from duct-config.psd1 beside this script (Optimize block; copy duct-config.example.psd1).
#   Set-ExecutionPolicy -Scope Process Bypass
#   .\Run-Optimizer.ps1 [-Config X.psd1] [-MaxCandidates 20] [-MaxJoints 10] [-SkipSap] [-NoValidate] [-TestModel]
# With the analysed model open in SAP:
#   1 connected : survey + forces + candidate joints of the untouched model     -> WorkDir\optimize\connected
#   2 select    : candidate list for the link model (config Optimize.Candidates, MaxCandidates = a spread sample) -> ...\opt\selected.txt
#   3 link      : save-as, EVERY selected candidate disconnected + stiff link + unit pairs, one run, export -> ...\link (link.sdb)
#   4 tables    : duct-sections.csv; DCRs of the connected model and of the link model (DuctEvaluate)  -> ...\tables, dcr-connected, dcr-base
#   5 optimize  : greedy -> swap -> removal over the candidates, offline                               -> ...\opt (chosen.txt, evaluations.tsv ...)
#   6 direct    : save-as, the chosen joints disconnected, run, export (validation; -NoValidate skips)  -> ...\direct (direct.sdb)
#   7 release   : the chosen set by superposition, compared with 6; DCRs of both                        -> ...\final, dcr-final, dcr-direct
#   8 report    : ...\opt\optimizer-report.html
# -SkipSap reuses the exports of a previous run (steps 4, 5, 7 without the direct comparison, 8). -MaxCandidates / -MaxJoints
# override the config for a trial. -TestModel builds the synthetic duct in SAP first (SAP 26 here).
param(
    [string]$Config,
    [int]$MaxCandidates = -1,
    [int]$MaxJoints = -1,
    [switch]$SkipSap,
    [switch]$NoValidate,
    [switch]$TestModel
)
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "Read-DuctConfig.ps1")
$C = Read-DuctConfig $Config
$O = $C.Optimize
if ($MaxCandidates -ge 0) { $O.MaxCandidates = $MaxCandidates }
if ($MaxJoints -ge 0) { $O.MaxJoints = $MaxJoints }

$root = Join-Path $C.WorkDir "optimize"
$dirs = @{}
foreach ($d in "connected", "link", "direct", "final", "tables", "opt", "dcr-connected", "dcr-base", "dcr-final", "dcr-direct") { $dirs[$d] = Join-Path $root $d; $null = New-Item -ItemType Directory -Force $dirs[$d] }
$sapRel = Join-Path (Split-Path $PSScriptRoot) "sap\Run-SapRelease.ps1"
$survey = Join-Path (Split-Path $PSScriptRoot) "sap\Run-SapSurvey.ps1"
$dcr = Join-Path $PSScriptRoot "Run-DuctDcr.ps1"
$relScript = Join-Path $PSScriptRoot "Run-DuctRelease.ps1"
$optScript = Join-Path $PSScriptRoot "Run-DuctOptimize.ps1"
$sapArg = @(); if ($C.SapDir) { $sapArg = @("-SapDir", $C.SapDir) }
$spec = $O.Candidates; if ($spec -is [array]) { $spec = ($spec -join ",") }
$t0 = Get-Date
Write-Host ("optimizer: candidates {0} (max {1})   max joints {2}   work dir {3}   config {4}" -f $spec, $O.MaxCandidates, $O.MaxJoints, $root, $C.Path) -ForegroundColor Yellow

if (-not $SkipSap) {
    if ($TestModel) { Invoke-Step "0 synthetic test model" (@($sapRel, "-TestModel", "-SavePath", (Join-Path $root "test.sdb")) + $sapArg) }
    Invoke-Step "1 connected model: survey + forces + candidates" (@($survey, "-Survey", "-Forces", "-IncludeCases", "-Candidates", "-Group", $C.Group, "-OutDir", $dirs.connected, "-SampleFrames", "0") + $sapArg)
    Invoke-Step "2 candidate selection" @($optScript, "-Select", "-ConnectedDir", $dirs.connected, "-Spec", $spec, "-MaxCandidates", $O.MaxCandidates, "-OutDir", $dirs.opt)
    $candList = (Get-Content (Join-Path $dirs.opt "selected.txt") -Raw).Trim()
    Invoke-Step "3 link model: every candidate disconnected + stiff link + unit pairs, run, export" (@($sapRel, "-LinkModel", "-Candidates", $candList, "-SavePath", (Join-Path $root "link.sdb"), "-OutDir", $dirs.link, "-Group", $C.Group, "-Factor", $C.Release.StiffnessFactor) + $sapArg)
}
foreach ($f in "connected\forces.tsv", "connected\sections.tsv", "connected\duct-joints.tsv", "link\forces.tsv", "link\linkdisp.tsv", "link\links.tsv", "opt\selected.txt") {
    if (-not (Test-Path (Join-Path $root $f))) { throw "missing $root\$f (run without -SkipSap)" }
}

$vectors = (Get-LoadVectors $C) -join ","
$check = (Get-CheckCombos $C) -join ","
$secCsv = Join-Path $dirs.tables "duct-sections.csv"
$a = @($dcr, "-Skeleton", "-SectionsTsv", (Join-Path $dirs.connected "sections.tsv"), "-ForcesTsv", (Join-Path $dirs.connected "forces.tsv"), "-Material", $C.Dcr.Material, "-OutCsv", $secCsv)
if (@($C.Dcr.CarbonSections).Count -gt 0) { $a += @("-CarbonSections", (@($C.Dcr.CarbonSections) -join ",")) }
Invoke-Step "4a duct-sections.csv" $a
function Invoke-Dcr([string]$title, [string]$forcesDir, [string]$outDir) {
    $a = @($dcr, "-Evaluate", "-ForcesTsv", (Join-Path $forcesDir "forces.tsv"), "-TablesDir", $dirs.tables, "-OutDir", $outDir, "-Limit", $C.Dcr.Limit, "-Combos", $check, "-ComboLS", $C.Dcr.ComboLS)
    if ($C.Dcr.EndsOnly) { $a += "-EndsOnly" }
    if ($C.Dcr.NoShear) { $a += "-NoShear" }
    Invoke-Step $title $a
}
Invoke-Dcr "4b DCRs: connected" $dirs.connected $dirs["dcr-connected"]
Invoke-Dcr "4c DCRs: link model, nothing released" $dirs.link $dirs["dcr-base"]

$a = @($optScript, "-Optimize", "-LinkDir", $dirs.link, "-ConnectedDir", $dirs.connected, "-TablesDir", $dirs.tables, "-OutDir", $dirs.opt,
    "-Vectors", $vectors, "-Dead", $C.Cases.Dead, "-Steel", $C.Cases.Steel, "-Seismic", ($C.Cases.Seismic -join ","),
    "-MaxJoints", $O.MaxJoints, "-OnePerSpan", $O.OnePerSpan, "-Swap", $O.Swap, "-Removal", $O.Removal, "-MaxEvaluations", $O.MaxEvaluations, "-SingularPivot", $O.SingularPivot, "-ComboLS", $C.Dcr.ComboLS, "-Limit", $C.Dcr.Limit)
if ($C.Dcr.EndsOnly) { $a += "-EndsOnly" }
Invoke-Step "5 optimize" $a
$chosen = ([string](Get-Content (Join-Path $dirs.opt "chosen.txt") -Raw)).Trim()
if (-not $chosen) { Write-Host "no joint chosen: nothing to validate" -ForegroundColor Yellow }

$validate = $chosen -and -not $NoValidate -and -not $SkipSap
if ($validate) {
    Invoke-Step "6 direct model: the chosen joints disconnected, run, export" (@($sapRel, "-DirectModel", "-Candidates", $chosen, "-SavePath", (Join-Path $root "direct.sdb"), "-OutDir", $dirs.direct, "-Group", $C.Group) + $sapArg)
}
$haveDirect = (Test-Path (Join-Path $dirs.direct "forces.tsv")) -and ($validate -or $SkipSap)
if ($chosen) {
    $a = @($relScript, "-Release", "-LinkDir", $dirs.link, "-OutDir", $dirs.final, "-Candidates", $chosen,
        "-Vectors", $vectors, "-Dead", $C.Cases.Dead, "-Steel", $C.Cases.Steel, "-Seismic", ($C.Cases.Seismic -join ","), "-Combo18", $C.Cases.Combo18,
        "-ConnectedDir", $dirs.connected, "-CheckCombos", $check)
    if ($haveDirect) { $a += @("-DirectDir", $dirs.direct) }
    Invoke-Step "7a release the chosen set by superposition" $a
    Invoke-Dcr "7b DCRs: chosen set released" $dirs.final $dirs["dcr-final"]
    if ($haveDirect) { Invoke-Dcr "7c DCRs: direct disconnect" $dirs.direct $dirs["dcr-direct"] }
}

$facts = @("candidates: $spec (max $($O.MaxCandidates)), one per span: $($O.OnePerSpan), swap: $($O.Swap), removal: $($O.Removal)", "max joints: $($O.MaxJoints)", "stiff link factor: $($C.Release.StiffnessFactor)", "group: $($C.Group)",
    "load vectors: $vectors", "combo 18 = $($C.Cases.Dead) + $($C.Cases.Steel) +/- SRSS($($C.Cases.Seismic -join ', '))", "checked cases: $check, limit state $($C.Dcr.ComboLS), limit $($C.Dcr.Limit)", "work dir: $root")
$a = @($optScript, "-Report", "-OutDir", $dirs.opt, "-Title", "Expansion-joint optimizer: $($C.Group)", "-Facts", ($facts -join "|"), "-ConnectedDir", $dirs.connected,
    "-DcrConnected", $dirs["dcr-connected"], "-DcrBase", $dirs["dcr-base"], "-Limit", $C.Dcr.Limit)
if ($chosen) { $a += @("-DcrFinal", $dirs["dcr-final"]) }
if ($chosen -and $haveDirect) { $a += @("-DcrDirect", $dirs["dcr-direct"]) }
Invoke-Step "8 report" $a
Write-Host ("== done in {0:0} s: {1}\opt\optimizer-report.html   (chosen.txt, evaluations.tsv, steps.tsv beside it)" -f ((Get-Date) - $t0).TotalSeconds, $root) -ForegroundColor Yellow
