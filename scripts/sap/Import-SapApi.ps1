# Compiles the SAP2000 controller classes into this PowerShell session (Windows PowerShell 5.1).
# Dot-source it:  . (Join-Path $PSScriptRoot "Import-SapApi.ps1")   (from scripts\sap\)
# Sets $SapDir (the install whose SAP2000v1.dll is used) and $SapExe. Every class in scripts\sap\Sap*.cs
# is compiled in one batch, so any runner can use SapSession, SapSurvey, SapRelease and SapExport.
# Install: -SapDir / $SapDir set by the caller, else $env:PLUTO_SAP_DIR, else the newest
# "C:\Program Files\Computers and Structures\SAP2000 NN".
if (-not $SapDir) { $SapDir = $env:PLUTO_SAP_DIR }
if (-not $SapDir) {
    $SapDir = Get-ChildItem "C:\Program Files\Computers and Structures" -Directory -Filter "SAP2000 *" |
        Sort-Object { [int]($_.Name -replace '\D', '') } | Select-Object -Last 1 -ExpandProperty FullName
}
if (-not $SapDir -or -not (Test-Path (Join-Path $SapDir "SAP2000v1.dll"))) { throw "No SAP2000 install found (SapDir '$SapDir'); pass -SapDir or set PLUTO_SAP_DIR." }
$SapExe = Join-Path $SapDir "SAP2000.exe"
$sapDll = Join-Path $SapDir "SAP2000v1.dll"
if (-not ("SapSession" -as [type])) {
    [void][System.Reflection.Assembly]::LoadFrom($sapDll)   # so the Add-Type'd code resolves it at run time
    $sapCs = Get-ChildItem -Path $PSScriptRoot -Filter "Sap*.cs" | Select-Object -ExpandProperty FullName
    # netstandard: SAP 26's dll targets it; harmless for SAP 22
    Add-Type -Path $sapCs -ReferencedAssemblies $sapDll, "System.Runtime.InteropServices", "netstandard" -ErrorAction Stop
}
