<#
.SYNOPSIS
  Find the SP3D classes behind a kind of object (walls, slabs, openings ...) by table name, and say whether
  their bounding boxes look like that kind of object. Read-only census. Draft v1 (2026-11-12), run on a synthetic database only.

.DESCRIPTION
  The piping tables hold one classid per table and the table name names the class. This applies the same
  idea to any name fragment:
    1. objects in <plant>_MDB whose name contains a fragment (tables; -Views adds views), and whether they
       have an oid column;
    2. for each one with an oid: the classids of its rows (join to CoreBaseClass), with counts;
    3. for each classid found (or given with -Class): rows, live rows (flag 1024 clear), rows with a bbox,
       and the bbox shape split:  wall-like  = thin in plan (min of dx, dy under -ThinM) and taller than -ThinM
                                  slab-like  = dz under -ThinM, both plan extents over -ThinM
                                  other      = everything else (skewed walls, L shapes, small pieces);
    4. three sample oids per classid -> C:\Temp\class_census_samples.csv, for Invoke-Atlas.
  -Series lists every classid in a 10000-wide block with counts (to spot unknown classes next to known ones).

  Needs Import-SqlExplorer.ps1 dot-sourced first. GUIDs and table names stay in C:\Temp; report the NEW lines.

.EXAMPLE
  .\Export-ClassCensus.ps1 -Like Wall,Slab,Opening
  .\Export-ClassCensus.ps1 -Like Wall -Views
  .\Export-ClassCensus.ps1 -Class 250012,250013        # shape split + samples for known classids
  .\Export-ClassCensus.ps1 -Series 240000              # classids 240000..249999 with counts
#>
[CmdletBinding()]
param(
    [string[]] $Like = @(),
    [int[]]    $Class = @(),
    [int]      $Series = -1,
    [switch]   $Views,
    [double]   $ThinM = 1.5,
    [string]   $Out = 'C:\Temp\class_census.txt',
    [string]   $Samples = 'C:\Temp\class_census_samples.csv'
)
$ErrorActionPreference = 'Stop'
if (-not (Get-Command Invoke-Sql -ErrorAction SilentlyContinue)) { throw "Dot-source Import-SqlExplorer.ps1 first." }
$plant = [Voyager.Atlas]::Plant
if (-not $plant -or $plant -eq '<plant>') { throw "Atlas plant not set (config.json `"plant`")." }
if ($Like.Count -eq 0 -and $Class.Count -eq 0 -and $Series -lt 0) { throw "Give -Like <fragments>, -Class <classids> or -Series <start>." }
foreach ($f in $Like) { if ($f -notmatch '^[A-Za-z0-9_]+$') { throw "-Like '$f': letters, digits and _ only." } }
$db = $plant + '_MDB'
$m = $db + '.dbo.'
$thin = $ThinM.ToString([Globalization.CultureInfo]::InvariantCulture)

$t0 = Get-Date
$log = New-Object System.Collections.Generic.List[string]
function Say([string]$s) { Write-Host $s; $log.Add($s) }
function Rows([string]$q) { @(Invoke-Sql $q) }

$newLines = @()

# ---- -Series: classid counts in one block -----------------------------------------------------------------
if ($Series -ge 0) {
    $hi = $Series + 9999
    Say "classids $Series..$hi (rows, live = flag 1024 clear):"
    $r = Rows ("SELECT classid, COUNT(*) AS N, SUM(CASE WHEN persistentFlag & 1024 = 0 THEN 1 ELSE 0 END) AS Live " +
               "FROM ${m}CoreBaseClass WHERE classid BETWEEN $Series AND $hi GROUP BY classid ORDER BY classid")
    foreach ($x in $r) { Say ("  {0,-8} {1,9} live {2,9}" -f $x.classid, $x.N, $x.Live) }
    $newLines += ("NEW series {0}: {1} classids" -f $Series, $r.Count)
}

# ---- 1 + 2: objects by name, classids per object ----------------------------------------------------------
$classes = New-Object System.Collections.Generic.List[int]
foreach ($c in $Class) { if (-not $classes.Contains($c)) { $classes.Add($c) } }
if ($Like.Count -gt 0) {
    $types = if ($Views) { "'U','V'" } else { "'U'" }
    $where = ($Like | ForEach-Object { "o.name LIKE '%$_%'" }) -join ' OR '
    $objs = Rows ("SELECT o.name AS Name, RTRIM(o.type) AS Kind, " +
                  "(SELECT COUNT(*) FROM $db.sys.columns c WHERE c.object_id = o.object_id AND c.name = 'oid') AS HasOid " +
                  "FROM $db.sys.objects o WHERE o.type IN ($types) AND ($where) ORDER BY o.name")
    $withOid = @($objs | Where-Object { $_.HasOid -gt 0 })
    Say ("objects matching {0}: {1} ({2} with an oid column)" -f ($Like -join ', '), $objs.Count, $withOid.Count)
    $i = 0
    foreach ($o in $objs) {
        $i++
        if ($o.HasOid -le 0) { Say ("  {0} [{1}] no oid column" -f $o.Name, $o.Kind); continue }
        try {
            $r = Rows ("SELECT b.classid, COUNT(*) AS N FROM ${m}[$($o.Name)] t JOIN ${m}CoreBaseClass b ON b.oid = t.oid " +
                       "GROUP BY b.classid ORDER BY N DESC")
            if ($r.Count -eq 0) { Say ("  {0} [{1}] empty" -f $o.Name, $o.Kind) }
            else {
                $parts = $r | ForEach-Object { "{0} x{1}" -f $_.classid, $_.N }
                Say ("  {0} [{1}] {2}" -f $o.Name, $o.Kind, ($parts -join ', '))
                foreach ($x in $r) { $cid = [int]$x.classid; if (-not $classes.Contains($cid)) { $classes.Add($cid) } }
            }
        } catch { Say ("  {0} [{1}] query failed: {2}" -f $o.Name, $o.Kind, $_.Exception.Message) }
        Write-Host ("    [{0}/{1}] {2:0}s" -f $i, $objs.Count, ((Get-Date) - $t0).TotalSeconds)
    }
    $newLines += ("NEW objects: {0} match, {1} with oid, {2} classids" -f $objs.Count, $withOid.Count, $classes.Count)
}

# ---- 3 + 4: bbox shape split and samples per classid ------------------------------------------------------
if ($classes.Count -gt 0) {
    $in = ($classes | Sort-Object) -join ','
    $shape = "CASE WHEN s.oid IS NULL THEN 'nobbox' " +
             "WHEN (s.zmax - s.zmin) > $thin AND ((s.xmax - s.xmin) < $thin OR (s.ymax - s.ymin) < $thin) THEN 'wall' " +
             "WHEN (s.zmax - s.zmin) <= $thin AND (s.xmax - s.xmin) > $thin AND (s.ymax - s.ymin) > $thin THEN 'slab' " +
             "ELSE 'other' END"
    $r = Rows ("SELECT q.classid, COUNT(*) AS N, SUM(q.Live) AS Live, " +
               "SUM(CASE WHEN q.Shape = 'wall' THEN 1 ELSE 0 END) AS WallLike, " +
               "SUM(CASE WHEN q.Shape = 'slab' THEN 1 ELSE 0 END) AS SlabLike, " +
               "SUM(CASE WHEN q.Shape = 'other' THEN 1 ELSE 0 END) AS Other, " +
               "SUM(CASE WHEN q.Shape = 'nobbox' THEN 1 ELSE 0 END) AS NoBbox " +
               "FROM (SELECT b.classid, CASE WHEN b.persistentFlag & 1024 = 0 THEN 1 ELSE 0 END AS Live, $shape AS Shape " +
               "      FROM ${m}CoreBaseClass b LEFT JOIN ${m}CoreSpatialIndex s ON s.oid = b.oid WHERE b.classid IN ($in)) q " +
               "GROUP BY q.classid ORDER BY N DESC")
    Say ""
    Say ("bbox shape per classid (thin = under {0} m):" -f $thin)
    Say "  classid        rows      live      wall      slab     other    nobbox"
    $best = $null
    foreach ($x in $r) {
        Say ("  {0,-8} {1,10} {2,9} {3,9} {4,9} {5,9} {6,9}" -f $x.classid, $x.N, $x.Live, $x.WallLike, $x.SlabLike, $x.Other, $x.NoBbox)
        if ($best -eq $null -or [int]$x.WallLike -gt [int]$best.WallLike) { $best = $x }
    }
    if ($best -ne $null) {
        $newLines += ("NEW most wall-like: {0} rows {1} wall {2} slab {3} other {4}" -f $best.classid, $best.N, $best.WallLike, $best.SlabLike, $best.Other)
    }

    $s = Rows ("SELECT q.classid, q.oid, q.Shape, q.dx, q.dy, q.dz FROM (" +
               "SELECT b.classid, b.oid, $shape AS Shape, (s.xmax - s.xmin) AS dx, (s.ymax - s.ymin) AS dy, (s.zmax - s.zmin) AS dz, " +
               "ROW_NUMBER() OVER (PARTITION BY b.classid ORDER BY (s.xmax - s.xmin) + (s.ymax - s.ymin) + (s.zmax - s.zmin) DESC) AS rn " +
               "FROM ${m}CoreBaseClass b JOIN ${m}CoreSpatialIndex s ON s.oid = b.oid " +
               "WHERE b.classid IN ($in) AND b.persistentFlag & 1024 = 0) q WHERE q.rn <= 3 ORDER BY q.classid, q.rn")
    $s | Select-Object classid, oid, Shape, dx, dy, dz | Export-Csv $Samples -NoTypeInformation
    Say ("samples (3 largest per classid, for Invoke-Atlas): {0} rows -> {1}" -f $s.Count, $Samples)
}

Say ""
foreach ($l in $newLines) { Say $l }
Say ("[done] {0:0}s" -f ((Get-Date) - $t0).TotalSeconds)
Set-Content -Path $Out -Value $log -Encoding ASCII
Write-Host "report -> $Out"
