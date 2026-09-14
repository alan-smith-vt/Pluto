# SAP2000 survey + axis probe. Windows PowerShell 5.1.
#   Survey (read-only) of the model open in SAP:
#     powershell.exe -NonInteractive -File scripts\sap\Run-SapSurvey.ps1 -Survey [-OutDir C:\Temp\sapsurvey] [-SampleFrames 5] [-ModelPath X.sdb]
#   First-pass expansion-joint candidates from the DUCT group's connectivity (read-only):
#     powershell.exe -NonInteractive -File scripts\sap\Run-SapSurvey.ps1 -Candidates [-Group DUCT] [-AngleTol 5] [-OutDir ...] [-ModelPath X.sdb]
#   Axis probe (builds its own two-cantilever model; close your model first, or run in a second SAP):
#     powershell.exe -NonInteractive -File scripts\sap\Run-SapSurvey.ps1 -Probe [-OutDir C:\Temp\sapsurvey]
#   -SapDir "C:\Program Files\Computers and Structures\SAP2000 22" picks the install (default: newest, or $env:PLUTO_SAP_DIR).
param(
    [switch]$Survey,
    [switch]$Probe,
    [switch]$Candidates,
    [string]$Group = "DUCT",     # -Candidates: the frame group holding the duct
    [double]$AngleTol = 5,       # -Candidates: max bend (deg) at a joint still called inline
    [string]$OutDir = (Join-Path $env:TEMP "sapsurvey"),
    [int]$SampleFrames = 5,
    [string]$ModelPath,          # -Survey only: open this .sdb instead of attaching to the model already open

    [string]$SapDir = $env:PLUTO_SAP_DIR
)
$ErrorActionPreference = "Stop"
if (-not ($Survey -or $Probe -or $Candidates)) { throw "Pass -Survey, -Candidates and/or -Probe." }

if (-not $SapDir) {
    $SapDir = Get-ChildItem "C:\Program Files\Computers and Structures" -Directory -Filter "SAP2000 *" |
        Sort-Object { [int]($_.Name -replace '\D', '') } | Select-Object -Last 1 -ExpandProperty FullName
}
$dll = Join-Path $SapDir "SAP2000v1.dll"
[void][System.Reflection.Assembly]::LoadFrom($dll)
Add-Type -Path (Join-Path $PSScriptRoot "SapSurvey.cs") -ReferencedAssemblies $dll, "System.Runtime.InteropServices", "netstandard"

$null = New-Item -ItemType Directory -Force $OutDir
$exe = Join-Path $SapDir "SAP2000.exe"

if ($Survey) {
    if ($ModelPath) {
        $sap = [SapSurvey]::AttachOrStart($exe, $false)
        $sap.OpenModel($ModelPath, $false)
    } else {
        $sap = [SapSurvey]::AttachOrStart($exe, $true)   # attach only: the model must already be open in SAP
    }
    try { $sap.Survey($OutDir, $SampleFrames) } finally { $sap.Close() }   # Close only exits a SAP this script started
    Get-Content (Join-Path $OutDir "summary.txt")
}
if ($Candidates) {
    if ($ModelPath) {
        $sap = [SapSurvey]::AttachOrStart($exe, $false)
        $sap.OpenModel($ModelPath, $false)
    } else {
        $sap = [SapSurvey]::AttachOrStart($exe, $true)
    }
    try { $sap.Candidates($OutDir, $Group, $AngleTol) } finally { $sap.Close() }
}
if ($Probe) {
    $sap = [SapSurvey]::AttachOrStart($exe, $false)
    try {
        $s2k = Join-Path $OutDir "axis-probe.s2k"
        [SapSurvey]::WriteProbeS2k($s2k, $sap.Version())
        $text = $sap.RunProbe($s2k)
        Set-Content -Path (Join-Path $OutDir "axis-probe.txt") -Value $text
        $text
    } finally { $sap.Close() }
}
