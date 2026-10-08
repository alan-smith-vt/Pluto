# A "Pluto Ducts" / "Pluto Fab" / "Pluto Box" run folder -> Pluto viewer file (.bin + .features.json) to load
# with "Add overlay..." beside the SP3D plant file. Windows PowerShell 5.1; uses scripts\arms\DuctsToPluto.cs
# (compiled with the lib by scripts\lib\Config.ps1).
#   powershell -ExecutionPolicy Bypass -File scripts\navis\Export-DuctsViewer.ps1 -Run C:\Temp\hvac\ducts\<yyyyMMdd-HHmmss>
#     [-Mesh]            the raw triangles (ducts_tri.bin: Ducts + MEP Fabrication Ductwork) as a shell mesh
#                        instead of beams, to check each element's actual geometry; default out <run>\ducts_mesh
#     [-Centrelines]     the Revit centrelines (cl_segments.csv) as thin beams in their own file, one group
#                        per RunName, node groups for free ends / junctions / RunName changes; default out
#                        <run>\ducts_cl (load beside ducts.bin with Add overlay...)
#     [-Out <base>]      default <run>\ducts  ->  <run>\ducts.bin + <run>\ducts.features.json
#     [-Unit in]         file length unit; "in" matches the plant export
#     [-ModelId <id>]    default hvac/ducts/<run folder name> (+ "/mesh")
#     [-Services <list>] draw only these services (the service field of IfcObjectProperties.RunName,
#                        "A-<bldg>-<service>-DUCT-<n>"), comma separated; default: the codes in
#                        C:\Temp\hvac\service-codes.txt (Pluto Service's file) when it exists; "*" = all.
#                        Rows without a RunName are always drawn, in their own group.
#   A Pluto Box run (C:\Temp\hvac\box\<run>: box_items.csv + box_tri.bin) is detected: every item in the box as
#   a shell mesh, one group per element category, hidden items separate (red); default out <run>\box.
param(
    [Parameter(Mandatory = $true)][string]$Run,
    [switch]$Mesh,
    [switch]$Centrelines,
    [string]$Out,
    [string]$Unit = "in",
    [string]$ModelId,
    [string]$Services
)
$ErrorActionPreference = "Stop"
$Run = [IO.Path]::GetFullPath($Run)
$Box = Test-Path (Join-Path $Run "box_items.csv")
if (-not $Box -and -not (Test-Path (Join-Path $Run "ducts.csv"))) { throw "No ducts.csv or box_items.csv in $Run (a Pluto Ducts / Fab / Box run folder)." }
if (-not $Out) { $Out = Join-Path $Run $(if ($Box) { "box" } elseif ($Mesh) { "ducts_mesh" } elseif ($Centrelines) { "ducts_cl" } else { "ducts" }) }
if (-not $ModelId) {
    $ModelId = $(if ($Box) { "hvac/box/" + (Split-Path $Run -Leaf) } else { "hvac/ducts/" + (Split-Path $Run -Leaf) + $(if ($Mesh) { "/mesh" } elseif ($Centrelines) { "/cl" } else { "" }) })
}
. (Join-Path (Split-Path $PSScriptRoot) "lib\Config.ps1")
$t0 = Get-Date
if ($Box) {
    $r = [DuctsToPluto]::ExportBox($Run, $Out, $ModelId, $Unit)
    Write-Output ("box mesh: {0} nodes; {1} items without triangles in the box" -f $r.Nodes, $r.Skipped)
} elseif ($Mesh) {
    $r = [DuctsToPluto]::ExportMesh($Run, $Out, $ModelId, $Unit)
    Write-Output ("mesh: {0} nodes; {1} rows without triangles" -f $r.Nodes, $r.Skipped)
} elseif ($Centrelines) {
    $r = [DuctsToPluto]::ExportCentrelines($Run, $Out, $ModelId, $Unit)
    Write-Output $r.Note
} else {
    $codesFile = "C:\Temp\hvac\service-codes.txt"
    if (-not $Services -and (Test-Path $codesFile)) { $Services = (Get-Content $codesFile -Raw) }
    $svc = $null
    if ($Services -and $Services.Trim() -ne "*") { $svc = [string[]]@($Services -split '[,;\r\n]' | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }
    $r = [DuctsToPluto]::Export($Run, $Out, $ModelId, $Unit, $svc)
    $r.Summary()
}
Write-Output ("[done] {0:0}s. In the viewer: Add overlay... -> {1}.bin" -f ((Get-Date) - $t0).TotalSeconds, $Out)
