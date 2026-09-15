# Duct DCR check of the model open in SAP2000 (HVAC): one call, settings in duct-config.psd1.
# Windows PowerShell 5.1. From a PowerShell console, with the model open and analysed in SAP:
#   Set-ExecutionPolicy -Scope Process Bypass
#   .\Run-DuctCheck.ps1 [-Config X.psd1] [-ReadSap:$false] [-RebuildSections:$false]
# Steps: template check -> SAP survey (sections.tsv) + frame forces (forces.tsv) -> duct-sections.csv -> DCRs.
# Each step runs in its own powershell.exe, so the script can be re-run in the same console.
# -ReadSap:$false reuses sap\sections.tsv and sap\forces.tsv from a previous run; -RebuildSections:$false keeps
# a hand-edited tables\duct-sections.csv. duct-materials.csv: tables\ copy if present, else the one beside this script.
param(
    [string]$Config,
    [bool]$ReadSap = $true,
    [bool]$RebuildSections = $true
)
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "Read-DuctConfig.ps1")
$C = Read-DuctConfig $Config
$work = Join-Path $C.WorkDir "ductcheck"
$sapOut = Join-Path $work "sap"
$tables = Join-Path $work "tables"
$dcrOut = Join-Path $work "dcr"
$null = New-Item -ItemType Directory -Force $sapOut, $tables, $dcrOut
$dcr = Join-Path $PSScriptRoot "Run-DuctDcr.ps1"
$survey = Join-Path (Split-Path $PSScriptRoot) "sap\Run-SapSurvey.ps1"
Write-Host ("duct check: group {0}   work dir {1}   config {2}" -f $C.Group, $work, $C.Path) -ForegroundColor Yellow

Invoke-Step "template check" @($dcr, "-Check")

if ($ReadSap) {
    $a = @($survey, "-Survey", "-Forces", "-IncludeCases", "-Group", $C.Group, "-OutDir", $sapOut)
    if ($C.SapDir) { $a += @("-SapDir", $C.SapDir) }
    Invoke-Step "SAP survey + forces" $a
}
foreach ($f in "sections.tsv", "forces.tsv") {
    if (-not (Test-Path (Join-Path $sapOut $f))) { throw "missing $sapOut\$f (run with -ReadSap `$true)" }
}

$secCsv = Join-Path $tables "duct-sections.csv"
if ($RebuildSections -or -not (Test-Path $secCsv)) {
    $a = @($dcr, "-Skeleton", "-SectionsTsv", (Join-Path $sapOut "sections.tsv"), "-ForcesTsv", (Join-Path $sapOut "forces.tsv"),
           "-Material", $C.Dcr.Material, "-OutCsv", $secCsv)
    if (@($C.Dcr.CarbonSections).Count -gt 0) { $a += @("-CarbonSections", (@($C.Dcr.CarbonSections) -join ",")) }
    Invoke-Step "duct-sections.csv" $a
}

$a = @($dcr, "-Evaluate", "-ForcesTsv", (Join-Path $sapOut "forces.tsv"), "-TablesDir", $tables, "-OutDir", $dcrOut,
       "-Limit", $C.Dcr.Limit, "-Combos", ((Get-CheckCombos $C) -join ","), "-ComboLS", $C.Dcr.ComboLS)
if ($C.Dcr.EndsOnly) { $a += "-EndsOnly" }
if ($C.Dcr.NoShear) { $a += "-NoShear" }
Invoke-Step "DCRs" $a

Write-Host "== results in $dcrOut (dcr-summary.txt, dcr-frames.tsv, dcr-stations.tsv)" -ForegroundColor Cyan
