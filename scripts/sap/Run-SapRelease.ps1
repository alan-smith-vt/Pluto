# Expansion-joint release models over the SAP2000 OAPI. Windows PowerShell 5.1. Called by duct\Run-ReleaseCheck.ps1.
#   .\Run-SapRelease.ps1 -LinkModel   -Candidates "123,456" -SavePath C:\Temp\hvac\link.sdb   -OutDir C:\Temp\hvac\link   -Group "DUCT_ALL" [-Factor 1000]
#   .\Run-SapRelease.ps1 -DirectModel -Candidates "123,456" -SavePath C:\Temp\hvac\direct.sdb -OutDir C:\Temp\hvac\direct -Group "DUCT_ALL"
#   .\Run-SapRelease.ps1 -TestModel -SavePath C:\Temp\hvac\test.sdb          (builds and runs the synthetic duct; SAP 26 here)
# The open model is saved AS a copy at -SavePath before any edit; the original file is not written.
# -ModelPath X.sdb opens that file first (default: the model already open in SAP). The model is reopened
# from its original path afterwards so SAP is left where it was.
param(
    [switch]$LinkModel,
    [switch]$DirectModel,
    [switch]$TestModel,
    [string]$Candidates,
    [string]$SavePath,
    [string]$OutDir,
    [string]$Group = "DUCT_ALL",
    [double]$Factor = 1000,
    [string]$ModelPath,
    [string]$SapDir = $env:PLUTO_SAP_DIR
)
$ErrorActionPreference = "Stop"
if (-not ($LinkModel -or $DirectModel -or $TestModel)) { throw "Pass -LinkModel, -DirectModel or -TestModel." }
if (-not $SavePath) { throw "-SavePath is required" }
$cands = @()
if ($Candidates) { $cands = @($Candidates -split "," | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }

. (Join-Path $PSScriptRoot "Import-SapApi.ps1")   # SAP classes, $SapDir, $SapExe
$exe = $SapExe
$null = New-Item -ItemType Directory -Force (Split-Path $SavePath)

if ($TestModel) {
    $sap = [SapSurvey]::AttachOrStart($exe, $false)
    $rel = New-Object SapRelease($sap)
    $rel.BuildTestModel([IO.Path]::GetFullPath($SavePath))
    exit 0
}
if ($cands.Count -eq 0) { throw "-Candidates is required (comma-separated joint names)" }
$sap = [SapSurvey]::AttachOrStart($exe, -not $ModelPath)
if ($ModelPath) { $sap.OpenModel($ModelPath, $false) }
$rel = New-Object SapRelease($sap)
$original = $rel.ModelPath()   # PowerShell cannot call the COM model directly
if ([IO.Path]::GetFullPath($original) -eq [IO.Path]::GetFullPath($SavePath)) { throw "-SavePath must differ from the open model ($original)" }
try {
    if ($LinkModel)   { $rel.BuildLinkModel([IO.Path]::GetFullPath($SavePath), [string[]]$cands, $Factor, [IO.Path]::GetFullPath($OutDir), $Group) }
    if ($DirectModel) { $rel.BuildDirectModel([IO.Path]::GetFullPath($SavePath), [string[]]$cands, [IO.Path]::GetFullPath($OutDir), $Group) }
} finally {
    if ($original) { $sap.OpenModel($original, $false) }   # leave SAP on the untouched original
    $sap.Close()
}