# SAP2000 OAPI proof of concept: beam .s2k -> SAP -> run -> result tables. Windows PowerShell 5.1.
#   powershell.exe -NonInteractive -File scripts\sap\Run-SapPoc.ps1 [-OutDir C:\Temp\sappoc] [-SapDir "C:\Program Files\Computers and Structures\SAP2000 22"]
param(
    [string]$OutDir = (Join-Path $env:TEMP "sappoc"),
    [string]$SapDir = $env:PLUTO_SAP_DIR   # else the newest "SAP2000 NN" install
)
$ErrorActionPreference = "Stop"

if (-not $SapDir) {
    $SapDir = Get-ChildItem "C:\Program Files\Computers and Structures" -Directory -Filter "SAP2000 *" |
        Sort-Object { [int]($_.Name -replace '\D', '') } | Select-Object -Last 1 -ExpandProperty FullName
}
$dll = Join-Path $SapDir "SAP2000v1.dll"
[void][System.Reflection.Assembly]::LoadFrom($dll)   # so the Add-Type'd code resolves it at run time
Add-Type -Path (Join-Path $PSScriptRoot "SapPoc.cs") -ReferencedAssemblies $dll, "System.Runtime.InteropServices", "netstandard"   # netstandard: SAP 26's dll targets it; harmless for SAP 22

$null = New-Item -ItemType Directory -Force $OutDir
$s2k = Join-Path $OutDir "beam.s2k"
$sap = [SapPoc]::AttachOrStart((Join-Path $SapDir "SAP2000.exe"))
try {
    [SapPoc]::WriteBeamS2k($s2k, $sap.Version(), 20.0, 10.0)   # 20 ft span, 10 kip at midspan -> R = 5 kip, M = 50 kip-ft
    $sap.Open($s2k)
    $sap.Run()
    Set-Content -NoNewline -Path (Join-Path $OutDir "joint-displacements.tsv") -Value $sap.JointDisplacements()
    Set-Content -NoNewline -Path (Join-Path $OutDir "frame-forces.tsv") -Value $sap.FrameForces()
} finally { $sap.Close() }

Get-Content (Join-Path $OutDir "frame-forces.tsv")
