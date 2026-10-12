# HVAC duct SAP model from a Revit overlay run's frames.csv (Export-DuctsViewer -Revit). Windows PowerShell 5.1.
#   .\Run-SapDucts.ps1 -Frames C:\Temp\hvac\ducts\<run>\revit_<room>.frames.csv -SavePath C:\Temp\hvac\sap\ducts_<room>.sdb
# Builds a NEW blank model (geometry, sections, DUCT_ALL / SRC_ / FLAG_ groups; no supports or loads) and saves
# it at -SavePath. If a model is open in SAP it is reopened afterwards so SAP is left where it was.
param(
    [Parameter(Mandatory = $true)][string]$Frames,
    [Parameter(Mandatory = $true)][string]$SavePath,
    [string]$SapDir = $env:PLUTO_SAP_DIR
)
$ErrorActionPreference = "Stop"
$Frames = [IO.Path]::GetFullPath($Frames); $SavePath = [IO.Path]::GetFullPath($SavePath)
if (-not (Test-Path $Frames)) { throw "No $Frames" }
. (Join-Path $PSScriptRoot "Import-SapApi.ps1")   # SAP classes, $SapDir, $SapExe
$null = New-Item -ItemType Directory -Force (Split-Path $SavePath)
$sap = [SapSurvey]::AttachOrStart($SapExe, $false)
$d = New-Object SapDucts($sap)
$original = $null
try { $original = $d.ModelPath() } catch { }   # PowerShell cannot call the COM model directly
if ($original -and [IO.Path]::GetFullPath($original) -eq $SavePath) { throw "-SavePath must differ from the open model ($original)" }
try {
    $d.Build($Frames, $SavePath)
} finally {
    if ($original -and (Test-Path $original)) { $sap.OpenModel($original, $false) }
    $sap.Close()
}
