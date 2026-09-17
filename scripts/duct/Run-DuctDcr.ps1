# Duct DCRs (HVAC). Windows PowerShell 5.1; no SAP needed. From a PowerShell console:
#   Set-ExecutionPolicy -Scope Process Bypass
#   .\Run-DuctDcr.ps1 -Check                                                  # reproduce the Mathcad template results
#   .\Run-DuctDcr.ps1 -Skeleton -SectionsTsv C:\Temp\sapsurvey\sections.tsv [-ForcesTsv ...\forces.tsv] [-Material CARBON] [-CarbonSections A,B] [-CapacitiesCsv standard-capacities.csv] [-StiffenerSpacing 24] -OutCsv duct-sections.csv
#   .\Run-DuctDcr.ps1 -Evaluate -ForcesTsv C:\Temp\sapforces\forces.tsv -TablesDir C:\Temp\ducttables -OutDir C:\Temp\ductdcr [-Limit 1.0] [-EndsOnly] [-NoShear] [-Combos DEAD,COMB18 [-ComboLS C]]
# TablesDir holds duct-sections.csv, and optionally duct-combos.csv (replaced by -Combos) and duct-materials.csv
# (default: the one beside this script). Layout: Notes vault, Projects/<project>/HVAC Member Table.md.
param(
    [switch]$Check,
    [switch]$Skeleton,
    [switch]$Evaluate,
    [string]$SectionsTsv,
    [string]$ForcesTsv,
    [string]$Material = "CARBON",
    [string]$OutCsv = "duct-sections.csv",
    [string]$CapacitiesCsv,
    [string[]]$CarbonSections,
    [double]$StiffenerSpacing = [double]::NaN,   # in, every section; blank = the sheet's shear as written
    [string]$TablesDir,
    [string]$OutDir = (Join-Path $env:TEMP "ductdcr"),
    [double]$Limit = 1.0,
    [switch]$EndsOnly,
    [switch]$NoShear,
    [string[]]$Combos,
    [string]$ComboLS = "C"
)
$ErrorActionPreference = "Stop"
# -File passes "a,b" as one string: split list parameters on commas.
if ($Combos) { $Combos = @($Combos | ForEach-Object { $_ -split "," } | Where-Object { $_.Trim() }) }
if ($CarbonSections) { $CarbonSections = @($CarbonSections | ForEach-Object { $_ -split "," } | Where-Object { $_.Trim() }) }
if (-not ($Check -or $Skeleton -or $Evaluate)) { throw "Pass -Check, -Skeleton or -Evaluate." }

Add-Type -Path (Join-Path $PSScriptRoot "DuctDcr.cs"), (Join-Path $PSScriptRoot "DuctEvaluate.cs")

if ($Check) {
    $ok = $false
    [DuctEvaluate]::CheckTemplate([ref]$ok)
    if (-not $ok) { exit 1 }
}
if ($Skeleton) {
    [DuctEvaluate]::Skeleton((Resolve-Path $SectionsTsv).Path, $(if ($ForcesTsv) { (Resolve-Path $ForcesTsv).Path } else { $null }), $Material, [IO.Path]::GetFullPath($OutCsv), $(if ($CapacitiesCsv) { (Resolve-Path $CapacitiesCsv).Path } else { $null }), $CarbonSections, $StiffenerSpacing)
}
if ($Evaluate) {
    [DuctEvaluate]::Run((Resolve-Path $ForcesTsv).Path, (Resolve-Path $TablesDir).Path, [IO.Path]::GetFullPath($OutDir), $Limit, [bool]$EndsOnly, [bool]$NoShear, $Combos, $ComboLS, (Join-Path $PSScriptRoot "duct-materials.csv"))
}
