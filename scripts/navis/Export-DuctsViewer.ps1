# A "Pluto Ducts" / "Pluto Fab" / "Pluto Box" run folder -> Pluto viewer file (.bin + .features.json) to load
# with "Add overlay..." beside the SP3D plant file. Windows PowerShell 5.1; uses scripts\arms\DuctsToPluto.cs
# (compiled with the lib by scripts\lib\Config.ps1).
#   powershell -ExecutionPolicy Bypass -File scripts\navis\Export-DuctsViewer.ps1 -Run C:\Temp\hvac\ducts\<yyyyMMdd-HHmmss>
#     [-Mesh [-Room <codes>]]  the raw triangles (ducts_tri.bin: Ducts + MEP Fabrication Ductwork) as a shell mesh
#                        instead of beams, to check each element's actual geometry; default out <run>\ducts_mesh
#     [-Centrelines]     the Revit centrelines (cl_segments.csv) as thin beams in their own file, one group
#                        per RunName, node groups for free ends / junctions / RunName changes; default out
#                        <run>\ducts_cl (load beside ducts.bin with Add overlay...)
#     [-Probe [-Room <code>]]  fabrication-part opening probe (connectors from the mesh): <out>.openings.csv,
#                        <out>.probe.txt and an overlay of opening stubs; -Room = Custom room number(s) (e.g. A-123 or "A-123,A-124"),
#                        needs a run from the 2026-10-08 build; default out <run>\probe[_<room>]
#                        [-Part <NavisId start>]: NEW reports that one part's rings and connectors instead
#     [-Revit <dir> [-Room <codes>]]  Revit connectivity overlay: a Pluto Connectors run (Revit add-in,
#                        C:\Temp\hvac\revit\<yyyyMMdd-HHmmss>) fitted onto this run's frame by IfcGUID and drawn for the
#                        room(s): Revit parts, connected / touching / open joints; <out>.match.txt lists what is in one
#                        model and not the other; default out <run>\revit[_<room>]. Load beside ducts_mesh[_<room>].
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
    [switch]$Probe,
    [string]$Room,
    [string]$Part,
    [string]$Revit,
    [string]$Out,
    [string]$Unit = "in",
    [string]$ModelId,
    [string]$Services
)
$ErrorActionPreference = "Stop"
if ($Room -and -not ($Mesh -or $Probe -or $Revit)) { throw "-Room works with -Mesh, -Probe or -Revit only." }
# file-name tag for the room filter: one room as is, a list as "<first>_plus<n>"
$roomList = @(if ($Room) { $Room -split '[,;\s]+' | Where-Object { $_ } })
$roomTag = $(if ($roomList.Count -eq 1) { "_" + ($roomList[0] -replace "[^A-Za-z0-9-]", "_") } elseif ($roomList.Count -gt 1) { "_" + ($roomList[0] -replace "[^A-Za-z0-9-]", "_") + "_plus" + ($roomList.Count - 1) } else { "" })
$Run = [IO.Path]::GetFullPath($Run)
$Box = Test-Path (Join-Path $Run "box_items.csv")
if (-not $Box -and -not (Test-Path (Join-Path $Run "ducts.csv"))) { throw "No ducts.csv or box_items.csv in $Run (a Pluto Ducts / Fab / Box run folder)." }
if (-not $Out) { $Out = Join-Path $Run $(if ($Box) { "box" } elseif ($Mesh) { "ducts_mesh" + $roomTag } elseif ($Centrelines) { "ducts_cl" } elseif ($Probe) { "probe" + $roomTag } elseif ($Revit) { "revit" + $roomTag } else { "ducts" }) }
if (-not $ModelId) {
    $ModelId = $(if ($Box) { "hvac/box/" + (Split-Path $Run -Leaf) } else { "hvac/ducts/" + (Split-Path $Run -Leaf) + $(if ($Mesh) { "/mesh" } elseif ($Centrelines) { "/cl" } elseif ($Probe) { "/probe" } elseif ($Revit) { "/revit" } else { "" }) })
}
. (Join-Path (Split-Path $PSScriptRoot) "lib\Config.ps1")
$t0 = Get-Date
if ($Box) {
    $r = [DuctsToPluto]::ExportBox($Run, $Out, $ModelId, $Unit)
    Write-Output ("box mesh: {0} nodes; {1} items without triangles in the box" -f $r.Nodes, $r.Skipped)
} elseif ($Mesh) {
    $r = [DuctsToPluto]::ExportMesh($Run, $Out, $ModelId, $Unit, $Room)
    Write-Output ("mesh: {0} nodes; {1} rows without triangles" -f $r.Nodes, $r.Skipped)
} elseif ($Revit) {
    $r = [DuctsToPluto]::ExportRevit($Run, [IO.Path]::GetFullPath($Revit), $Out, $ModelId, $Unit, $Room)
    Write-Output $r.Note
} elseif ($Probe) {
    $r = [DuctsToPluto]::ExportProbe($Run, $Out, $ModelId, $Unit, $Room, $Part)
    Write-Output $r.Note
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
