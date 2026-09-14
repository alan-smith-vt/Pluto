# Duct DCRs (HVAC). Windows PowerShell 5.1; no SAP needed. From a PowerShell console:
#   Set-ExecutionPolicy -Scope Process Bypass
#   .\Run-DuctDcr.ps1 -Check                                                  # reproduce the Mathcad template results
#   .\Run-DuctDcr.ps1 -Skeleton -SectionsTsv C:\Temp\sapsurvey\sections.tsv [-ForcesTsv ...\forces.tsv] [-Material CARBON] -OutCsv duct-sections.csv
#   .\Run-DuctDcr.ps1 -Evaluate -ForcesTsv C:\Temp\sapforces\forces.tsv -TablesDir C:\Temp\ducttables -OutDir C:\Temp\ductdcr [-Limit 1.0]
# TablesDir holds duct-materials.csv, duct-sections.csv, duct-combos.csv (layout: Notes vault, Projects/<project>/HVAC Member Table.md).
param(
    [switch]$Check,
    [switch]$Skeleton,
    [switch]$Evaluate,
    [string]$SectionsTsv,
    [string]$ForcesTsv,
    [string]$Material = "CARBON",
    [string]$OutCsv = "duct-sections.csv",
    [string]$TablesDir,
    [string]$OutDir = (Join-Path $env:TEMP "ductdcr"),
    [double]$Limit = 1.0
)
$ErrorActionPreference = "Stop"
if (-not ($Check -or $Skeleton -or $Evaluate)) { throw "Pass -Check, -Skeleton or -Evaluate." }

Add-Type -Path (Join-Path $PSScriptRoot "DuctDcr.cs"), (Join-Path $PSScriptRoot "DuctEvaluate.cs")

if ($Check) {
    $ok = $false
    [DuctEvaluate]::CheckTemplate([ref]$ok)
    if (-not $ok) { exit 1 }
}
if ($Skeleton) {
    [DuctEvaluate]::Skeleton((Resolve-Path $SectionsTsv).Path, $(if ($ForcesTsv) { (Resolve-Path $ForcesTsv).Path } else { $null }), $Material, [IO.Path]::GetFullPath($OutCsv))
}
if ($Evaluate) {
    [DuctEvaluate]::Run((Resolve-Path $ForcesTsv).Path, (Resolve-Path $TablesDir).Path, [IO.Path]::GetFullPath($OutDir), $Limit)
}
