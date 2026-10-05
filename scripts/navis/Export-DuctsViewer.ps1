# A "Pluto Ducts" run folder -> Pluto viewer file (.bin + .features.json) to load with "Add overlay..."
# beside the SP3D plant file. Windows PowerShell 5.1; uses scripts\arms\DuctsToPluto.cs (compiled with the
# lib by scripts\lib\Config.ps1).
#   powershell -ExecutionPolicy Bypass -File scripts\navis\Export-DuctsViewer.ps1 -Run C:\Temp\hvac\ducts\<yyyyMMdd-HHmmss>
#     [-Out <base>]      default <run>\ducts  ->  <run>\ducts.bin + <run>\ducts.features.json
#     [-Unit in]         file length unit; "in" matches the plant export
#     [-ModelId <id>]    default hvac/ducts/<run folder name>
param(
    [Parameter(Mandatory = $true)][string]$Run,
    [string]$Out,
    [string]$Unit = "in",
    [string]$ModelId
)
$ErrorActionPreference = "Stop"
$Run = [IO.Path]::GetFullPath($Run)
if (-not (Test-Path (Join-Path $Run "ducts.csv"))) { throw "No ducts.csv in $Run (a Pluto Ducts run folder)." }
if (-not $Out) { $Out = Join-Path $Run "ducts" }
if (-not $ModelId) { $ModelId = "hvac/ducts/" + (Split-Path $Run -Leaf) }
. (Join-Path (Split-Path $PSScriptRoot) "lib\Config.ps1")
$t0 = Get-Date
$r = [DuctsToPluto]::Export($Run, $Out, $ModelId, $Unit)
$r.Summary()
Write-Output ("[done] {0:0}s. In the viewer: Add overlay... -> {1}.bin" -f ((Get-Date) - $t0).TotalSeconds, $Out)
