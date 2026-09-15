# Validate the expansion-joint release by superposition on the model open in SAP2000 (HVAC).
# Windows PowerShell 5.1. Settings come from duct-config.psd1 beside this script (copy duct-config.example.psd1).
#   Set-ExecutionPolicy -Scope Process Bypass
#   .\Run-ReleaseCheck.ps1 [-Config X.psd1] [-Candidates "123,456"] [-SkipSap] [-TestModel]
# With the analysed model open in SAP:
#   1 connected : survey + forces of the untouched model                 -> WorkDir\release\connected
#   2 link      : save-as, candidates disconnected + stiff links + unit pairs, run, export -> ...\link  (link.sdb)
#   3 direct    : save-as, candidates disconnected, run, export          -> ...\direct (direct.sdb)
#   4 release   : Woodbury release offline, rebuild combo 18, compare with 1 and 3 -> ...\released
#   5 DCRs      : Excel-workflow DCRs on connected / released / direct   -> ...\dcr-*
#   6 report    : ...\released\release-report.html
# -SkipSap reuses the exports of a previous run (steps 4-6 only). -Candidates overrides Release.Candidates.
# -TestModel builds the synthetic duct in SAP first (SAP 26 here) and checks joint 3 of it (or -Candidates).
# Each step runs in its own powershell.exe, so the script can be re-run in the same console.
param(
    [string]$Config,
    [string]$Candidates,
    [switch]$SkipSap,
    [switch]$TestModel
)
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "Read-DuctConfig.ps1")
$C = Read-DuctConfig $Config
$cands = @($C.Release.Candidates)
if ($Candidates) { $cands = @($Candidates -split "," | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }
if ($TestModel -and -not $Candidates) { $cands = @("3") }
if ($cands.Count -eq 0) { throw "no candidates: set Release.Candidates in the config or pass -Candidates" }
$candList = $cands -join ","

$root = Join-Path $C.WorkDir "release"
$dirs = @{}
foreach ($d in "connected", "link", "direct", "released", "tables", "dcr-connected", "dcr-released", "dcr-direct") { $dirs[$d] = Join-Path $root $d; $null = New-Item -ItemType Directory -Force $dirs[$d] }
$sapRel = Join-Path (Split-Path $PSScriptRoot) "sap\Run-SapRelease.ps1"
$survey = Join-Path (Split-Path $PSScriptRoot) "sap\Run-SapSurvey.ps1"
$dcr = Join-Path $PSScriptRoot "Run-DuctDcr.ps1"
$relScript = Join-Path $PSScriptRoot "Run-DuctRelease.ps1"
$sapArg = @(); if ($C.SapDir) { $sapArg = @("-SapDir", $C.SapDir) }
$t0 = Get-Date
Write-Host ("release check: candidates {0}   work dir {1}   config {2}" -f $candList, $root, $C.Path) -ForegroundColor Yellow

if (-not $SkipSap) {
    if ($TestModel) { Invoke-Step "0 synthetic test model" (@($sapRel, "-TestModel", "-SavePath", (Join-Path $root "test.sdb")) + $sapArg) }
    Invoke-Step "1 connected model: survey + forces" (@($survey, "-Survey", "-Forces", "-IncludeCases", "-Group", $C.Group, "-OutDir", $dirs.connected, "-SampleFrames", "0") + $sapArg)
    Invoke-Step "2 link model: disconnect + stiff links + unit pairs, run, export" (@($sapRel, "-LinkModel", "-Candidates", $candList, "-SavePath", (Join-Path $root "link.sdb"), "-OutDir", $dirs.link, "-Group", $C.Group, "-Factor", $C.Release.StiffnessFactor) + $sapArg)
    Invoke-Step "3 direct model: disconnect, run, export" (@($sapRel, "-DirectModel", "-Candidates", $candList, "-SavePath", (Join-Path $root "direct.sdb"), "-OutDir", $dirs.direct, "-Group", $C.Group) + $sapArg)
}
foreach ($f in "connected\forces.tsv", "connected\sections.tsv", "link\forces.tsv", "link\linkdisp.tsv", "link\links.tsv", "direct\forces.tsv") {
    if (-not (Test-Path (Join-Path $root $f))) { throw "missing $root\$f (run without -SkipSap)" }
}

$vectors = (Get-LoadVectors $C) -join ","
$check = (Get-CheckCombos $C) -join ","
Invoke-Step "4 release by superposition + comparisons" @($relScript, "-Release", "-LinkDir", $dirs.link, "-OutDir", $dirs.released, "-Candidates", $candList,
    "-Vectors", $vectors, "-Dead", $C.Cases.Dead, "-Steel", $C.Cases.Steel, "-Seismic", ($C.Cases.Seismic -join ","), "-Combo18", $C.Cases.Combo18,
    "-ConnectedDir", $dirs.connected, "-DirectDir", $dirs.direct, "-CheckCombos", $check)

$secCsv = Join-Path $dirs.tables "duct-sections.csv"
$a = @($dcr, "-Skeleton", "-SectionsTsv", (Join-Path $dirs.connected "sections.tsv"), "-ForcesTsv", (Join-Path $dirs.connected "forces.tsv"), "-Material", $C.Dcr.Material, "-OutCsv", $secCsv)
if (@($C.Dcr.CarbonSections).Count -gt 0) { $a += @("-CarbonSections", (@($C.Dcr.CarbonSections) -join ",")) }
Invoke-Step "5a duct-sections.csv" $a
foreach ($m in "connected", "released", "direct") {
    $a = @($dcr, "-Evaluate", "-ForcesTsv", (Join-Path $dirs[$m] "forces.tsv"), "-TablesDir", $dirs.tables, "-OutDir", $dirs["dcr-$m"], "-Limit", $C.Dcr.Limit, "-Combos", $check, "-ComboLS", $C.Dcr.ComboLS)
    if ($C.Dcr.EndsOnly) { $a += "-EndsOnly" }
    if ($C.Dcr.NoShear) { $a += "-NoShear" }
    Invoke-Step "5b DCRs: $m" $a
}
$facts = @("candidates: $candList", "stiff link factor: $($C.Release.StiffnessFactor)", "group: $($C.Group)", "load vectors: $vectors", "combo 18 = $($C.Cases.Dead) + $($C.Cases.Steel) +/- SRSS($($C.Cases.Seismic -join ', '))", "checked cases: $check", "work dir: $root")
Invoke-Step "6 report" @($relScript, "-Report", "-OutDir", $dirs.released, "-Title", "Release check: joint $candList", "-Facts", ($facts -join "|"),
    "-DcrConnected", $dirs["dcr-connected"], "-DcrReleased", $dirs["dcr-released"], "-DcrDirect", $dirs["dcr-direct"], "-Limit", $C.Dcr.Limit)
Write-Host ("== done in {0:0} s: {1}\released\release-report.html   (compare-*.txt and release.txt beside it)" -f ((Get-Date) - $t0).TotalSeconds, $root) -ForegroundColor Yellow
