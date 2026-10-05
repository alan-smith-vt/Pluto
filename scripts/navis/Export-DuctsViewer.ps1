# A "Pluto Ducts" / "Pluto Fab" run folder -> Pluto viewer file (.bin + .features.json) to load with
# "Add overlay..." beside the SP3D plant file. Windows PowerShell 5.1; uses scripts\arms\DuctsToPluto.cs
# (compiled with the lib by scripts\lib\Config.ps1).
#   powershell -ExecutionPolicy Bypass -File scripts\navis\Export-DuctsViewer.ps1 -Run C:\Temp\hvac\ducts\<yyyyMMdd-HHmmss>
#     [-Mesh]            the raw triangles (ducts_tri.bin: Ducts + MEP Fabrication Ductwork) as a shell mesh
#                        instead of beams, to check each element's actual geometry; default out <run>\ducts_mesh
#     [-Out <base>]      default <run>\ducts  ->  <run>\ducts.bin + <run>\ducts.features.json
#     [-Unit in]         file length unit; "in" matches the plant export
#     [-ModelId <id>]    default hvac/ducts/<run folder name> (+ "/mesh")
param(
    [Parameter(Mandatory = $true)][string]$Run,
    [switch]$Mesh,
    [string]$Out,
    [string]$Unit = "in",
    [string]$ModelId
)
$ErrorActionPreference = "Stop"
$Run = [IO.Path]::GetFullPath($Run)
if (-not (Test-Path (Join-Path $Run "ducts.csv"))) { throw "No ducts.csv in $Run (a Pluto Ducts run folder)." }
if (-not $Out) { $Out = Join-Path $Run $(if ($Mesh) { "ducts_mesh" } else { "ducts" }) }
if (-not $ModelId) { $ModelId = "hvac/ducts/" + (Split-Path $Run -Leaf) + $(if ($Mesh) { "/mesh" } else { "" }) }
. (Join-Path (Split-Path $PSScriptRoot) "lib\Config.ps1")
$t0 = Get-Date
if ($Mesh) {
    $r = [DuctsToPluto]::ExportMesh($Run, $Out, $ModelId, $Unit)
    Write-Output ("mesh: {0} nodes; {1} rows without triangles" -f $r.Nodes, $r.Skipped)
} else {
    $r = [DuctsToPluto]::Export($Run, $Out, $ModelId, $Unit)
    $r.Summary()
}
Write-Output ("[done] {0:0}s. In the viewer: Add overlay... -> {1}.bin" -f ((Get-Date) - $t0).TotalSeconds, $Out)
