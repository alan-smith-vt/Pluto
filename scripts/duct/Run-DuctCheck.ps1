# Duct DCR check of the model open in SAP2000 (HVAC): one call, settings in the block below.
# Windows PowerShell 5.1. From a PowerShell console, with the model open and analysed in SAP:
#   Set-ExecutionPolicy -Scope Process Bypass
#   .\Run-DuctCheck.ps1
# Steps: template check -> SAP survey (sections.tsv) + frame forces (forces.tsv) -> duct-sections.csv -> DCRs.
# Each step runs in its own powershell.exe, so the script can be re-run in the same console.

# ==================== EDIT HERE ====================
$WorkDir       = "C:\Temp\ductcheck"             # everything is written under here
$Group         = "DUCT_ALL"                      # SAP frame group to check
$Combos        = @("DEAD", "COMB18", "COMB19", "COMB20")   # output case / combo names exactly as SAP lists them
$ComboLS       = "C"                             # A or B = no increase; anything else = 1.5 x allowables
$Material      = "304L"                          # material of every section (row name in duct-materials.csv) ...
$CarbonSections = @()                            # ... except these section names, which get CARBON
$EndsOnly      = $true                           # check only the two frame ends (the Excel workflow)
$NoShear       = $true                           # V2 / V3 left out of the governing DCR
$Limit         = 1.0                             # DCR limit for the counts
$SapDir        = ""                              # "" = newest SAP2000 install; else e.g. "C:\Program Files\Computers and Structures\SAP2000 22"
$ReadSap       = $true                           # $false = reuse sap\sections.tsv and sap\forces.tsv from a previous run
$RebuildSections = $true                         # $false = keep a hand-edited tables\duct-sections.csv
# duct-materials.csv: tables\duct-materials.csv if present, else the copy beside this script.
# ===================================================

$ErrorActionPreference = "Stop"
$ps = Join-Path $PSHOME "powershell.exe"
$sapOut = Join-Path $WorkDir "sap"
$tables = Join-Path $WorkDir "tables"
$dcrOut = Join-Path $WorkDir "dcr"
$null = New-Item -ItemType Directory -Force $sapOut, $tables, $dcrOut
$dcr = Join-Path $PSScriptRoot "Run-DuctDcr.ps1"
$survey = Join-Path (Split-Path $PSScriptRoot) "sap\Run-SapSurvey.ps1"

function Step([string]$title, [string[]]$argList) {
    Write-Host "== $title" -ForegroundColor Cyan
    & $ps -NoProfile -NonInteractive -ExecutionPolicy Bypass -File @argList
    if ($LASTEXITCODE -ne 0) { throw "$title failed (exit $LASTEXITCODE)" }
}

Step "template check" @($dcr, "-Check")

if ($ReadSap) {
    $a = @($survey, "-Survey", "-Forces", "-IncludeCases", "-Group", $Group, "-OutDir", $sapOut)
    if ($SapDir) { $a += @("-SapDir", $SapDir) }
    Step "SAP survey + forces" $a
}
foreach ($f in "sections.tsv", "forces.tsv") {
    if (-not (Test-Path (Join-Path $sapOut $f))) { throw "missing $sapOut\$f (run with `$ReadSap = `$true)" }
}

$secCsv = Join-Path $tables "duct-sections.csv"
if ($RebuildSections -or -not (Test-Path $secCsv)) {
    $a = @($dcr, "-Skeleton", "-SectionsTsv", (Join-Path $sapOut "sections.tsv"), "-ForcesTsv", (Join-Path $sapOut "forces.tsv"),
           "-Material", $Material, "-OutCsv", $secCsv)
    if ($CarbonSections.Count -gt 0) { $a += @("-CarbonSections", ($CarbonSections -join ",")) }
    Step "duct-sections.csv" $a
}

$a = @($dcr, "-Evaluate", "-ForcesTsv", (Join-Path $sapOut "forces.tsv"), "-TablesDir", $tables, "-OutDir", $dcrOut,
       "-Limit", $Limit, "-Combos", ($Combos -join ","), "-ComboLS", $ComboLS)
if ($EndsOnly) { $a += "-EndsOnly" }
if ($NoShear) { $a += "-NoShear" }
Step "DCRs" $a

Write-Host "== results in $dcrOut (dcr-summary.txt, dcr-frames.tsv, dcr-stations.tsv)" -ForegroundColor Cyan
