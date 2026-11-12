<#
.SYNOPSIS
  SP3D walls as axis-aligned boxes: one CSV row per wall object (classid 240024 with a bounding box).
  Draft v1 (2026-11-12), run on a synthetic database only.

.DESCRIPTION
  A wall object holds both faces, so its bbox (CoreSpatialIndex, meters, plant frame) is the wall solid
  including its thickness, as long as the wall runs along X or Y. Each row gets a shape:
      wall       taller than -ThinM and thin in plan (min of dx, dy under -ThinM): the box is the wall
      slab       flat: both plan extents over -ThinM and dz under -ThinM, or under a quarter of the smaller plan
                 extent (a thick slab stays a slab)
      skew slab  taller than -ThinM, not thin in plan, lower than its smaller plan extent: a sloped or stepped
                 slab, or a wall far off the axes (the box cannot tell these apart)
      skew wall  taller than -ThinM, not thin in plan, at least as high as its smaller plan extent: a wall off
                 the axes, an L shape, a shaft
      small      everything else (short pieces, curbs)
  The viewer export (duct\model\WallViewWriter.cs) applies the same rule to the boxes; it does not read Shape.
  Thk_m = the thin plan extent of a wall (dz of a slab); Axis = the direction a wall runs along (X or Y).

  Report (console and -Report): counts per shape, wall thickness histogram (5 cm bins), the most common
  bottom elevations (0.5 m bins; these are the levels). -Attrs adds a census of the string attributes the
  class carries (interface, slot, rows, distinct values, lengths; no values are printed) to see whether
  walls carry a room or a name.

  Needs Import-SqlExplorer.ps1 dot-sourced first. GUIDs stay in C:\Temp; report the NEW lines.

.EXAMPLE
  .\Export-Walls.ps1                 # all walls -> C:\Temp\walls.csv, report -> C:\Temp\walls.txt
  .\Export-Walls.ps1 -Top 50         # first 50 rows -> C:\Temp\walls_top.csv
  .\Export-Walls.ps1 -Attrs          # also the string attribute census
#>
[CmdletBinding()]
param(
    [int]    $Class = 240024,
    [double] $ThinM = 1.5,
    [int]    $Top = 0,
    [switch] $Attrs,
    [string] $Out,
    [string] $Report = 'C:\Temp\walls.txt'
)
$ErrorActionPreference = 'Stop'
if (-not (Get-Command Invoke-Sql -ErrorAction SilentlyContinue)) { throw "Dot-source Import-SqlExplorer.ps1 first." }
$plant = [Voyager.Atlas]::Plant
if (-not $plant -or $plant -eq '<plant>') { throw "Atlas plant not set (config.json `"plant`")." }
$m = $plant + '_MDB.dbo.'
$thin = $ThinM.ToString([Globalization.CultureInfo]::InvariantCulture)
if (-not $Out) { $Out = $(if ($Top -gt 0) { 'C:\Temp\walls_top.csv' } else { 'C:\Temp\walls.csv' }) }

$t0 = Get-Date
$log = New-Object System.Collections.Generic.List[string]
function Say([string]$s) { Write-Host $s; $log.Add($s) }
function Rows([string]$q) { @(Invoke-Sql $q) }

# one row per wall object with a bbox: extents, shape, thickness, axis
$inner = @"
SELECT b.oid AS WallOid, s.xmin AS Xmin, s.xmax AS Xmax, s.ymin AS Ymin, s.ymax AS Ymax, s.zmin AS Zmin, s.zmax AS Zmax,
       (s.xmax - s.xmin) AS dx, (s.ymax - s.ymin) AS dy, (s.zmax - s.zmin) AS dz
FROM ${m}CoreBaseClass b
JOIN ${m}CoreSpatialIndex s ON s.oid = b.oid
WHERE b.classid = $Class AND b.persistentFlag & 1024 = 0
"@
$plan = "(CASE WHEN w.dx < w.dy THEN w.dx ELSE w.dy END)"
$shape = "CASE WHEN w.dz > $thin AND $plan < $thin THEN 'wall' " +
         "WHEN $plan > $thin AND (w.dz <= $thin OR w.dz < $plan / 4) THEN 'slab' " +
         "WHEN w.dz > $thin AND w.dz < $plan THEN 'skew slab' " +
         "WHEN w.dz > $thin THEN 'skew wall' ELSE 'small' END"
$thk = "CASE WHEN $plan > $thin AND w.dz < $plan THEN w.dz ELSE $plan END"   # a slab or skew slab: its depth
$axis = "CASE WHEN w.dx >= w.dy THEN 'X' ELSE 'Y' END"
$rowsSql = "SELECT w.WallOid, w.Xmin, w.Xmax, w.Ymin, w.Ymax, w.Zmin, w.Zmax, $shape AS Shape, $thk AS Thk_m, $axis AS Axis FROM ($inner) w"

# ---- counts ------------------------------------------------------------------------------------------------
$all = @(Rows ("SELECT COUNT(*) AS N, SUM(CASE WHEN b.persistentFlag & 1024 = 0 THEN 1 ELSE 0 END) AS Live, " +
             "SUM(CASE WHEN b.persistentFlag & 1024 = 0 AND s.oid IS NOT NULL THEN 1 ELSE 0 END) AS WithBbox " +
             "FROM ${m}CoreBaseClass b LEFT JOIN ${m}CoreSpatialIndex s ON s.oid = b.oid WHERE b.classid = $Class"))
Say ("classid {0}: {1} rows, {2} live, {3} live with a bbox" -f $Class, $all[0].N, $all[0].Live, $all[0].WithBbox)

$byShape = Rows ("SELECT q.Shape, COUNT(*) AS N FROM ($rowsSql) q GROUP BY q.Shape ORDER BY N DESC")
$cnt = @{ 'wall' = 0; 'slab' = 0; 'skew wall' = 0; 'skew slab' = 0; 'small' = 0 }
foreach ($x in $byShape) { $cnt[[string]$x.Shape] = [int]$x.N }
Say ("shape (thin = under {0} m): wall {1}, slab {2}, skew wall {3}, skew slab {4}, small {5}" -f $thin, $cnt['wall'], $cnt['slab'], $cnt['skew wall'], $cnt['skew slab'], $cnt['small'])

# ---- wall thickness histogram, 5 cm bins -------------------------------------------------------------------
Say ""
Say "wall thickness, 5 cm bins (m: count):"
$h = Rows ("SELECT FLOOR(q.Thk_m / 0.05 + 0.000001) AS Bin, COUNT(*) AS N FROM ($rowsSql) q WHERE q.Shape = 'wall' GROUP BY FLOOR(q.Thk_m / 0.05 + 0.000001) ORDER BY Bin")
$top1 = $null
foreach ($x in $h) {
    $lo = [double]$x.Bin * 0.05
    Say ("  {0,5:0.00}-{1,4:0.00}: {2}" -f $lo, ($lo + 0.05), $x.N)
    if ($top1 -eq $null -or [int]$x.N -gt [int]$top1.N) { $top1 = $x }
}

# ---- levels: most common bottom elevations of walls, 0.5 m bins --------------------------------------------
Say ""
Say "wall bottom elevation, 0.5 m bins, 15 most common (m: count, mean height m):"
$lv = Rows ("SELECT TOP 15 FLOOR(q.Zmin / 0.5) AS Bin, COUNT(*) AS N, AVG(q.Zmax - q.Zmin) AS H FROM ($rowsSql) q " +
            "WHERE q.Shape = 'wall' GROUP BY FLOOR(q.Zmin / 0.5) ORDER BY N DESC")
foreach ($x in ($lv | Sort-Object { [double]$_.Bin })) {
    Say ("  {0,7:0.0}: {1,6}  h {2:0.0}" -f ([double]$x.Bin * 0.5), $x.N, [double]$x.H)
}

# ---- string attributes on the class (no values) ------------------------------------------------------------
if ($Attrs) {
    Say ""
    Say "string attributes on the class (interface first 8, slot: objects, distinct values, length min-max):"
    try {
        $a = Rows ("SELECT a.iid, a.dispid, COUNT(*) AS N, COUNT(DISTINCT a.value) AS Distinct_, MIN(LEN(a.value)) AS L0, MAX(LEN(a.value)) AS L1 " +
                   "FROM ${m}CoreBstrAttribute a JOIN ${m}CoreBaseClass b ON b.oid = a.oid AND b.classid = $Class AND b.persistentFlag & 1024 = 0 " +
                   "WHERE a.value <> '' GROUP BY a.iid, a.dispid ORDER BY N DESC")
        if ($a.Count -eq 0) { Say "  none" }
        foreach ($x in $a) {
            Say ("  {0} slot {1}: {2} objects, {3} distinct, length {4}-{5}" -f ([string]$x.iid).Substring(0, 8), $x.dispid, $x.N, $x.Distinct_, $x.L0, $x.L1)
        }
        Say ("NEW attrs: {0} (interface, slot) pairs with text" -f $a.Count)
    } catch { Say ("  attribute census failed: " + $_.Exception.Message) }
}

# ---- CSV ---------------------------------------------------------------------------------------------------
$sel = $rowsSql
if ($Top -gt 0) { $sel = $rowsSql.Replace("SELECT w.WallOid", "SELECT TOP $Top w.WallOid") }
$rows = Rows $sel
$rows | Select-Object WallOid, Xmin, Xmax, Ymin, Ymax, Zmin, Zmax, Shape, Thk_m, Axis | Export-Csv $Out -NoTypeInformation
Say ""
Say ("{0} rows -> {1}" -f $rows.Count, $Out)

Say ""
Say ("NEW walls: {0} live with bbox; wall {1} slab {2} skew wall {3} skew slab {4} small {5}" -f $all[0].WithBbox, $cnt['wall'], $cnt['slab'], $cnt['skew wall'], $cnt['skew slab'], $cnt['small'])
if ($top1 -ne $null) {
    $lo = [double]$top1.Bin * 0.05
    Say ("NEW thickness: most common {0:0.00}-{1:0.00} m ({2} of {3} walls), {4} bins" -f $lo, ($lo + 0.05), $top1.N, $cnt['wall'], $h.Count)
}
Say ("[done] {0:0}s" -f ((Get-Date) - $t0).TotalSeconds)
Set-Content -Path $Report -Value $log -Encoding ASCII
Write-Host "report -> $Report"
