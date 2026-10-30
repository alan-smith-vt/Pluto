# A "Pluto Ducts" / "Pluto Fab" / "Pluto Box" run folder -> Pluto viewer file (.bin + .features.json) to load
# with "Add overlay..." beside the SP3D plant file. Windows PowerShell 5.1; uses scripts\arms\DuctsToPluto.cs
# (compiled with the arms by scripts\arms\Import-Arms.ps1).
#   powershell -ExecutionPolicy Bypass -File scripts\navis\Export-DuctsViewer.ps1 -Run C:\Temp\hvac\ducts\<yyyyMMdd-HHmmss>
#     [-Mesh [-Room <codes>]]  the raw triangles (ducts_tri.bin: Ducts + MEP Fabrication Ductwork) as a shell mesh
#                        instead of beams, to check each element's actual geometry; default out <run>\ducts_mesh
#     [-Centrelines]     the Revit centrelines (cl_segments.csv) as thin beams in their own file, one group
#                        per RunName, node groups for free ends / junctions / RunName changes; default out
#                        <run>\ducts_cl (load beside ducts.bin with Add overlay...)
#     [-Probe [-Room <code>]]  fabrication-part opening probe (connectors from the mesh): <out>.openings.csv,
#                        <out>.probe.txt and an overlay of opening stubs; -Room = Custom room number(s) (e.g. R1 or "R1,R2"),
#                        needs a run from the 2026-10-08 build; default out <run>\probe[_<room>]
#                        [-Part <NavisId start>]: NEW reports that one part's rings and connectors instead
#     [-Revit <dir> [-Room <codes>]]  (no / empty -Room: DuctRooms from duct-config.psd1; -Room * = whole building) Revit connectivity overlay: a Pluto Connectors run (Revit add-in,
#                        C:\Temp\hvac\revit\<yyyyMMdd-HHmmss>) fitted onto this run's frame by IfcGUID and drawn for the
#                        room(s): Revit parts, connected / touching / open joints; <out>.match.txt lists what is in one
#                        model and not the other; default out <run>\revit[_<room>]. Load beside ducts_mesh[_<room>].
#                        Also <out>.flags.csv (discrepancy flags per part, 'Flag: <name>' viewer groups) and
#                        <out>.frames.csv, the SAP model input: scripts\sap\Run-SapDucts.ps1 -Frames <it> -SavePath <x.sdb>
#                        [-Part <Revit ElementId>]: NEW reports that part's connectors and what lies near each
#                        [-Part <NavisId start or IfcGUID>]: NEW reports why that Navisworks row is in or out of the overlay
#                        [-Part <id>,<id>]: NEW reports both parts' systems, the closest free ends between them and
#                        the Revit parts / Navisworks rows lying between those ends
#                        (-Revit wins over -Probe: leave -Revit off for the probe's -Part)
#                        [-Systems <list>]: primary system names, comma separated, each optionally "<name>=#rrggbb"
#                        (fragments get a desaturated variant; other groups start unpainted / hidden) (default: DuctSystems in
#                        scripts\duct\duct-config.psd1). A part whose run / system name contains one belongs to it;
#                        per name the largest connected component is the system, the rest "<name> fragments",
#                        anything else "other: <name>" (viewer groups, frames.csv System column, SAP SYS_ groups)
#                        [-Cuts <csv>|none]: scope cut points (default: DuctCuts in the duct config), the CSV of
#                        scripts\sap\Run-Sap.ps1 -Points (cut nodes read from a SAP model): each snaps to a clean
#                        joint or duct within 6 in; per system the largest piece is kept, pieces beyond a cut dropped;
#                        kept frames ending at a cut get CUT_END (viewer "Clean: cut ends", SAP FLAG_CUT_END)
#                        [-Steel <csv>|none]: utility rack steel (default: DuctSteel in the duct config) drawn in the
#                        clean model with its end conditions; <out>.steel_joints.csv / .steel_frames.csv (SAP input)
#     [-Out <base>]      default <run>\ducts  ->  <run>\ducts.bin + <run>\ducts.features.json
#     [-Unit in]         file length unit; "in" matches the plant export
#     [-ModelId <id>]    default hvac/ducts/<run folder name> (+ "/mesh")
#     [-Services <list>] draw only these services (the service field of IfcObjectProperties.RunName,
#                        "A-<bldg>-<service>-DUCT-<n>"), comma separated; default: the codes in
#                        <AddinDir>\service-codes.txt (Pluto Service's file) when it exists; "*" = all.
#                        AddinDir: from the duct config, default C:\Temp\hvac (where the add-ins read and write)
#                        Rows without a RunName are always drawn, in their own group.
#   A Pluto Box run (C:\Temp\hvac\box\<run>: box_items.csv + box_tri.bin) is detected: every item in the box as
#   a shell mesh: groups per element category, per RunName (+ "no run name") and per hidden state (Visible /
#   Hidden: self / Hidden by: <item>, red); default out <run>\box. With -Revit -Room the overlay writes the room
#   boxes for the Box dialog's "room boxes" option (<AddinDir>\box\room-boxes.txt) and for Revit Pluto Mesh
#   (<AddinDir>\revit\mesh-boxes.txt).
#   A Revit Pluto Mesh run (C:\Temp\hvac\revit-mesh\<run>: mesh_items.csv + mesh_tri.bin) is detected too: Revit
#   solids as a shell mesh in the Navisworks frame, groups per category, RunName, fabrication; default out <run>\revit_mesh.
param(
    [Parameter(Mandatory = $true)][string]$Run,
    [switch]$Mesh,
    [switch]$Centrelines,
    [switch]$Probe,
    [string]$Room,
    [string[]]$Part,   # string[]: an unquoted "-Part a,b" arrives as an array; joined below
    [string]$Revit,
    [string]$Out,
    [string]$Unit = "in",
    [string]$ModelId,
    [string]$Services,
    [string]$Systems,
    [string]$Cuts,
    [string]$Steel,  # steel CSV for the clean model (default DuctSteel in the duct config); none = off
    [string]$Drop   # add-on: hand-picked drop lists (viewer Cut picker), comma separated; default DuctDrop in the duct config; none = off
)
$ErrorActionPreference = "Stop"
$Part = $(if ($Part) { $Part -join "," } else { $null })
# -Revit without -Room: DuctRooms from the duct config (a fresh session lost the list once and ran the whole building)
$cfg = Join-Path (Split-Path $PSScriptRoot) "duct\duct-config.psd1"
if ($Revit -and -not $Room -and (Test-Path $cfg)) {   # an empty -Room $var counts as not given
    $dr = @((Import-PowerShellDataFile $cfg).DuctRooms | Where-Object { $_ })
    if ($dr.Count) { $Room = $dr -join ","; Write-Output ("rooms from duct-config.psd1: {0} codes" -f $dr.Count) }
}
if ($Room -eq "*") { $Room = "" }
if ($Revit -and -not $Room) { Write-Output "rooms: none (whole building)" }
if ($Room -and -not ($Mesh -or $Probe -or $Revit)) { throw "-Room works with -Mesh, -Probe or -Revit only." }
# file-name tag for the room filter: one room as is, a list as "<first>_plus<n>"
$roomList = @(if ($Room) { $Room -split '[,;\s]+' | Where-Object { $_ } })
$roomTag = $(if ($roomList.Count -eq 1) { "_" + ($roomList[0] -replace "[^A-Za-z0-9-]", "_") } elseif ($roomList.Count -gt 1) { "_" + ($roomList[0] -replace "[^A-Za-z0-9-]", "_") + "_plus" + ($roomList.Count - 1) } else { "" })
$Run = [IO.Path]::GetFullPath($Run)
$Box = Test-Path (Join-Path $Run "box_items.csv")
$RvMesh = Test-Path (Join-Path $Run "mesh_items.csv")
if (-not $Box -and -not $RvMesh -and -not (Test-Path (Join-Path $Run "ducts.csv"))) { throw "No ducts.csv or box_items.csv in $Run (a Pluto Ducts / Fab / Box or Revit Pluto Mesh run folder)." }
if (-not $Out) { $Out = Join-Path $Run $(if ($Box) { "box" } elseif ($RvMesh) { "revit_mesh" } elseif ($Mesh) { "ducts_mesh" + $roomTag } elseif ($Centrelines) { "ducts_cl" } elseif ($Probe) { "probe" + $roomTag } elseif ($Revit) { "revit" + $roomTag } else { "ducts" }) }
if (-not $ModelId) {
    $ModelId = $(if ($Box) { "hvac/box/" + (Split-Path $Run -Leaf) } elseif ($RvMesh) { "hvac/revit-mesh/" + (Split-Path $Run -Leaf) } else { "hvac/ducts/" + (Split-Path $Run -Leaf) + $(if ($Mesh) { "/mesh" } elseif ($Centrelines) { "/cl" } elseif ($Probe) { "/probe" } elseif ($Revit) { "/revit" } else { "" }) })
}
. (Join-Path (Split-Path $PSScriptRoot) "arms\Import-Arms.ps1")
# every setting the converter takes, in one object (scripts\duct\model\DuctSettings.cs), handed to the Export* call
$ds = New-Object DuctSettings
# files shared with the add-ins (room boxes, service codes) live in AddinDir from the duct config, default the add-ins' C:\Temp\hvac
$addinDir = [DuctSettings]::AddinDefaultDir
if (Test-Path $cfg) { $w = (Import-PowerShellDataFile $cfg).AddinDir; if ($w) { $addinDir = $w } }
$ds.AddinDir = $addinDir
$t0 = Get-Date
if ($Box) {
    $r = [DuctsToPluto]::ExportBox($Run, $Out, $ModelId, $Unit)
    Write-Output $r.Note
    Write-Output ("box mesh: {0} nodes; {1} items without triangles in the box" -f $r.Nodes, $r.Skipped)
} elseif ($RvMesh) {
    # Revit categories left out of the _norun file: RevitMeshNoRunExclude in duct-config.psd1
    $cfg = Join-Path (Split-Path $PSScriptRoot) "duct\duct-config.psd1"
    if (Test-Path $cfg) { foreach ($c in @((Import-PowerShellDataFile $cfg).RevitMeshNoRunExclude | Where-Object { $_ })) { [void]$ds.NoRunExclude.Add($c.Trim()) } }
    $r = [DuctsToPluto]::ExportRevitMesh($Run, $Out, $ModelId, $Unit, $ds)
    Write-Output $r.Note
    Write-Output ("Revit mesh: {0} nodes; {1} elements without triangles in the boxes" -f $r.Nodes, $r.Skipped)
} elseif ($Mesh) {
    $r = [DuctsToPluto]::ExportMesh($Run, $Out, $ModelId, $Unit, $Room)
    Write-Output ("mesh: {0} nodes; {1} rows without triangles" -f $r.Nodes, $r.Skipped)
} elseif ($Revit) {
    $cfg = Join-Path (Split-Path $PSScriptRoot) "duct\duct-config.psd1"
    if (-not $Systems -and (Test-Path $cfg)) { $Systems = ((Import-PowerShellDataFile $cfg).DuctSystems) -join "," }
    foreach ($s in @($Systems -split '[,;\s]+' | Where-Object { $_ })) {
        $n, $c = $s -split '=', 2
        $ds.PrimarySystems.Add($n)
        if ($c) { $ds.PrimaryColors[$n] = $c }
    }
    # pinned mains: "<name>=<Revit id>" (a fragment holding that part becomes "<name> 2", ...)
    if (Test-Path $cfg) {
        foreach ($p in @((Import-PowerShellDataFile $cfg).DuctMains | Where-Object { $_ })) {
            $n, $id = $p -split '=', 2
            if ($id) { $ds.PrimaryPins.Add((New-Object 'System.Collections.Generic.KeyValuePair[string,string]' $n.Trim(), $id.Trim())) }
        }
    }
    # scope cut points: a Run-Sap.ps1 -Points CSV (Name, Found, X_in, Y_in, Z_in) from -Cuts or DuctCuts in the config
    if (-not $Cuts -and (Test-Path $cfg)) { $Cuts = (Import-PowerShellDataFile $cfg).DuctCuts }
    if ($Cuts -and $Cuts -ne "none") {
        if (-not (Test-Path $Cuts)) { throw "cut points file not found: $Cuts (DuctCuts in duct-config.psd1 or -Cuts; -Cuts none = no cuts)" }
        $inv = [Globalization.CultureInfo]::InvariantCulture
        foreach ($c in (Import-Csv $Cuts | Where-Object { $_.Found -eq "1" })) {
            $xyz = [double[]]@([double]::Parse($c.X_in, $inv), [double]::Parse($c.Y_in, $inv), [double]::Parse($c.Z_in, $inv))
            $ds.CutPoints.Add((New-Object 'System.Collections.Generic.KeyValuePair[string,double[]]' $c.Name, $xyz))
        }
        Write-Output ("cut points: {0} from {1}" -f $ds.CutPoints.Count, $Cuts)
    }
    # add-on (DuctsManualDrop.cs): hand-picked drops from the viewer's Cut picker
    # DuctDrop: one file or a list of files, only those named (no default); -Drop overrides, -Drop none = off
    $dropFiles = @($(if ($Drop) { $Drop -split ',' } elseif (Test-Path $cfg) { (Import-PowerShellDataFile $cfg).DuctDrop }) |
        ForEach-Object { "$_".Trim() } | Where-Object { $_ -and $_ -ne "none" })
    foreach ($f in $dropFiles) {
        if (-not (Test-Path $f)) { throw "drop list not found: $f (DuctDrop in duct-config.psd1 or -Drop; -Drop none = off)" }
        Write-Output ("manual drop: {0} parts from {1}" -f [DuctsToPluto]::ReadManualDrop($ds, (Resolve-Path $f).Path), $f)
    }
    # utility rack steel in the clean model (DuctsSteel.cs): DuctSteel in the config (SteelToPluto CSV, meters, this frame);
    # -Steel overrides, -Steel none = off. DuctSteelSnapIn / DuctSteelNearIn: connection snap and OPEN_NEAR distances (in)
    $dcfg = $(if (Test-Path $cfg) { Import-PowerShellDataFile $cfg } else { @{} })
    if (-not $Steel) { $Steel = $dcfg.DuctSteel }
    $ds.SteelSettings = New-Object 'SteelModel+Settings'
    if ($dcfg.DuctSteelSnapIn) { $ds.SteelSettings.SnapIn = [double]$dcfg.DuctSteelSnapIn }
    if ($dcfg.DuctSteelNearIn) { $ds.SteelSettings.NearIn = [double]$dcfg.DuctSteelNearIn }
    # duct-to-steel supports (DuctsCleanSupports.cs): DuctSupportGapIn = largest duct bottom -> steel top gap that is a
    # support (in), DuctSpanFt = unsupported span limit (ft); the gap histogram in match.txt helps choose the gap
    $ds.Supports = New-Object 'DuctSupports+Settings'
    if ($dcfg.DuctSupportGapIn) { $ds.Supports.GapMaxIn = [double]$dcfg.DuctSupportGapIn }
    if ($dcfg.DuctSpanFt) { $ds.Supports.SpanFt = [double]$dcfg.DuctSpanFt }
    # Navisworks hanger check (DuctsCleanHangers.cs): a hanger and a support match within DuctHangerMatchIn (in)
    $ds.HangerMatchIn = $(if ($dcfg.DuctHangerMatchIn) { [double]$dcfg.DuctHangerMatchIn } else { 24 })
    # duct orientation (DuctsCleanOrient.cs): horizontal ducts with the larger side vertical are flagged and, unless
    # DuctRotateLargeVertical = $false, rotated 90 deg about the centreline with their size run
    $ds.RotateLargeVertical = -not ($dcfg.ContainsKey('DuctRotateLargeVertical') -and -not $dcfg.DuctRotateLargeVertical)
    if ($Steel -and $Steel -ne "none") {
        if (-not (Test-Path $Steel)) { throw "steel CSV not found: $Steel (DuctSteel in duct-config.psd1 or -Steel; -Steel none = off)" }
        $sr = New-Object SteelToPluto; $sr.Progress = $false
        $ds.Steel.AddRange($sr.Build((Resolve-Path $Steel).Path))
        Write-Output ("steel: {0} members from {1} ({2})" -f $ds.Steel.Count, $Steel, ($sr.Summary() -replace "`n", "; "))
    }
    Write-Output ("primary systems: " + $(if ($ds.PrimarySystems.Count) { $ds.PrimarySystems -join ", " } else { "none configured (DuctSystems in duct-config.psd1 or -Systems)" }))
    $r = [DuctsToPluto]::ExportRevit($Run, [IO.Path]::GetFullPath($Revit), $Out, $ModelId, $Unit, $Room, $Part, $ds)
    Write-Output $r.Note
} elseif ($Probe) {
    $r = [DuctsToPluto]::ExportProbe($Run, $Out, $ModelId, $Unit, $Room, $Part)
    Write-Output $r.Note
} elseif ($Centrelines) {
    $r = [DuctsToPluto]::ExportCentrelines($Run, $Out, $ModelId, $Unit)
    Write-Output $r.Note
} else {
    $codesFile = Join-Path $addinDir "service-codes.txt"
    if (-not $Services -and (Test-Path $codesFile)) { $Services = (Get-Content $codesFile -Raw) }
    $svc = $null
    if ($Services -and $Services.Trim() -ne "*") { $svc = [string[]]@($Services -split '[,;\r\n]' | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }
    $r = [DuctsToPluto]::Export($Run, $Out, $ModelId, $Unit, $svc)
    $r.Summary()
}
$files = $(if ($Box -or $RvMesh) { (@("_run", "_norun") | ForEach-Object { "$Out$_.bin" } | Where-Object { Test-Path $_ }) -join " and " } else { "$Out.bin" })
$secs = ((Get-Date) - $t0).TotalSeconds
if ($Revit -and (Test-Path "${Out}_clean.bin")) {
    Write-Output ("[done] {0:0.0}s. Read {1}.match.txt first." -f $secs, $Out)
    Write-Output ("       In the viewer: Choose files -> {0}_clean.bin + {0}_clean.features.json (the clean model); Add overlay... -> {0}.bin (the Revit overlay)" -f $Out)
} else {
    Write-Output ("[done] {0:0.0}s. In the viewer: Add overlay... -> {1}" -f $secs, $files)
}
