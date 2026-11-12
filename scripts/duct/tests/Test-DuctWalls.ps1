# Tests for the walls in the clean model (duct\model\WallViewWriter.cs). Windows PowerShell 5.1, a few seconds:
#   powershell -ExecutionPolicy Bypass -File scripts\duct\tests\Test-DuctWalls.ps1
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "..\..\arms\Import-Arms.ps1")
. (Join-Path $PSScriptRoot "..\Get-DuctSettings.ps1")
$script:fail = 0; $script:pass = 0
function Check($name, $cond) { if ($cond) { $script:pass++ } else { $script:fail++; Write-Output "FAIL: $name" } }
function Near($a, $b) { [math]::Abs($a - $b) -lt 1e-3 }
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("pluto-walls-" + [Guid]::NewGuid().ToString("N").Substring(0, 8) + ".csv")

# shape rule (meters)
Check "s1 wall: tall, thin in plan" ([WallViewWriter]::ShapeOf(10, 0.3, 4) -eq "wall")
Check "s2 slab: not tall" ([WallViewWriter]::ShapeOf(10, 8, 0.3) -eq "slab")
Check "s3 thick slab stays a slab (height under a quarter of the smaller plan side)" ([WallViewWriter]::ShapeOf(30, 20, 2) -eq "slab")
Check "s4 skew slab: tall, fat in plan, lower than its plan" ([WallViewWriter]::ShapeOf(5, 5, 4) -eq "skew slab")
Check "s5 skew wall: tall, fat in plan, higher than its plan" ([WallViewWriter]::ShapeOf(2, 2, 9) -eq "skew wall")
Check "s6 small" ([WallViewWriter]::ShapeOf(0.5, 0.5, 1) -eq "small")

# the Export-Walls.ps1 CSV as Export-Csv writes it (quoted); meters. The Shape column is not used (k1 says skew).
@('"WallOid","Xmin","Xmax","Ymin","Ymax","Zmin","Zmax","Shape","Thk_m","Axis"',
  '"w1","0","10","0","0.3","0","4","wall","0.3","X"',
  '"w2","0","0.3","0","8","0","4","wall","0.3","Y"',
  '"s1","0","10","0","8","3.8","4.1","slab","0.3","X"',
  '"k1","20","25","20","25","0","4","skew","5","X"',
  '"m1","3","3.5","3","3.5","0","1","small","0.5","X"',
  '"far","500","510","0","0.3","0","4","wall","0.3","X"',
  '"flat","1","1","0","5","0","4","wall","0","Y"',
  '"t1","0","10","0","8","8","9.8","skew","1.8","X"',
  '"h1","12","14","12","14","0","9","skew","2","X"') | Set-Content $tmp -Encoding ASCII

$walls = [WallViewWriter]::Read($tmp)
Check "1 rows read" ($walls.Count -eq 9)
Check "2 shapes from the boxes, not the CSV column" ((@($walls | ForEach-Object { $_.Shape }) -join "|") -eq "wall|wall|slab|skew slab|small|wall|wall|slab|skew wall")
Check "3 extents in meters" ((Near $walls[0].Hi[0] 10) -and (Near $walls[0].Hi[1] 0.3) -and (Near $walls[2].Lo[2] 3.8))

$tmp2 = $tmp + ".noshape.csv"
@("Xmin,Xmax,Ymin,Ymax,Zmin,Zmax", "0,10,0,0.3,0,4", "0,10,0,8,3.8,4.1") | Set-Content $tmp2 -Encoding ASCII
Check "4 a CSV without a Shape column reads the same" ((@([WallViewWriter]::Read($tmp2) | ForEach-Object { $_.Shape }) -join "|") -eq "wall|slab")
$threw = $false; Set-Content $tmp2 "Xmin,Xmax,Ymin,Ymax,Zmin`n0,1,0,1,0" -Encoding ASCII; try { $null = [WallViewWriter]::Read($tmp2) } catch { $threw = $_.Exception.Message -like "*no column Zmax*" }
Check "5 a missing column is named" $threw

# Add: model box = two nodes (feet) around the origin; margin 10 ft keeps w1, w2, s1, m1 (flat is near but has no volume)
function Model {
    $nodes = New-Object 'System.Collections.Generic.Dictionary[int,Node]'; $next = 1
    $off = [double[]](0, 0, 0)
    $null = [DuctGeom]::MakeNode($nodes, [ref]$next, [double[]](0, 0, 0), $off, 12)
    $null = [DuctGeom]::MakeNode($nodes, [ref]$next, [double[]](30, 25, 12), $off, 12)
    return @{ Nodes = $nodes; Next = $next; Off = $off }
}
$m = Model; $next = $m.Next
$v = [WallViewWriter]::Add($walls, 10, $m.Nodes, [ref]$next, $m.Off, 12)
Check "6 near boxes drawn; far ones and the flat one left out" ($v.Total -eq 9 -and $v.Drawn -eq 4)
Check "7 six quads and eight nodes per box" ($v.Elements.Count -eq 24 -and $m.Nodes.Count -eq 2 + 32)
Check "8 groups by shape" ($v.ByShape["wall"].Count -eq 12 -and $v.ByShape["slab"].Count -eq 6 -and $v.ByShape["small"].Count -eq 6 -and $v.ByShape.Count -eq 3)
$want = "walls: 4 of 9 drawn (within 10 ft of the model): wall 2, slab 1, skew wall 0, skew slab 0, small 1; 1 with a zero extent left out`n" +
        "wall levels: 1 floor (top of slab) at 13.5 ft; boxes per level: below 13.5 ft: 3, above 13.5 ft: 1"
Check "9 note" ($v.Note -eq $want)
$e = $v.Elements[1]
Check "10 quad of four nodes" ($e.nNodes -eq 4 -and @($e.n | Where-Object { $_ }).Count -eq 4)
# first box 0..10 m x 0..0.3 m x 0..4 m -> inches (scale 12 per ft)
$xs = @($v.Elements.Values | Select-Object -First 6 | ForEach-Object { $_.n } | ForEach-Object { $_.xyz.X })
$zs = @($v.Elements.Values | Select-Object -First 6 | ForEach-Object { $_.n } | ForEach-Object { $_.xyz.Z })
Check "11 box corners in file units" ((Near ($xs | Measure-Object -Maximum).Maximum (10 / 0.3048 * 12)) -and (Near ($zs | Measure-Object -Maximum).Maximum (4 / 0.3048 * 12)) -and (Near ($xs | Measure-Object -Minimum).Minimum 0))
Check "12 label" ($v.Labels[1] -eq "wall | wall | thk 11.8 in | 32.8 x 1 x 13.1 ft | z 0 to 13.1 ft | level below 13.5 ft | w1")
# faces point outwards: normal . (face centre - box centre) > 0 for every quad of the first box
$out = $true
$c = [double[]]((10 / 0.3048 * 6), (0.3 / 0.3048 * 6), (4 / 0.3048 * 6))
foreach ($id in 1..6) {
    $n = $v.Elements[$id].n
    $a = [double[]](($n[1].xyz.X - $n[0].xyz.X), ($n[1].xyz.Y - $n[0].xyz.Y), ($n[1].xyz.Z - $n[0].xyz.Z))
    $b = [double[]](($n[2].xyz.X - $n[0].xyz.X), ($n[2].xyz.Y - $n[0].xyz.Y), ($n[2].xyz.Z - $n[0].xyz.Z))
    $nx = $a[1] * $b[2] - $a[2] * $b[1]; $ny = $a[2] * $b[0] - $a[0] * $b[2]; $nz = $a[0] * $b[1] - $a[1] * $b[0]
    $fx = ($n | ForEach-Object { $_.xyz.X } | Measure-Object -Average).Average - $c[0]
    $fy = ($n | ForEach-Object { $_.xyz.Y } | Measure-Object -Average).Average - $c[1]
    $fz = ($n | ForEach-Object { $_.xyz.Z } | Measure-Object -Average).Average - $c[2]
    if (($nx * $fx + $ny * $fy + $nz * $fz) -le 0) { $out = $false }
}
Check "13 faces wound outwards" $out

# every wall: two floors. s1 (80 m2, top 4.1 m) and k1 (25 m2, top 4.0 m) are one elevation; t1 (top 9.8 m) the other
$m = Model; $next = $m.Next
$v = [WallViewWriter]::Add($walls, -1, $m.Nodes, [ref]$next, $m.Off, 12)
Check "14 negative margin: every wall with a volume" ($v.Drawn -eq 8 -and $v.Note -like "walls: 8 of 9 drawn (every wall): wall 3, slab 2, skew wall 1, skew slab 1, small 1;*")
Check "15 two floors, area-weighted tops" ($v.FloorsFt.Count -eq 2 -and (Near $v.FloorsFt[0] ((25 * 4.0 + 80 * 4.1) / 105 / 0.3048)) -and (Near $v.FloorsFt[1] (9.8 / 0.3048)))
Check "16 three bands, low to high" (($v.Levels -join "|") -eq "below 13.4 ft|13.4 to 32.2 ft|above 32.2 ft")
# below: w1 w2 m1 far (mid height under the floor); middle: s1 k1 (their floor) + h1 (mid height 4.5 m); above: t1
Check "17 boxes per band: a slab with the band it floors, the rest by mid height" (($v.LevelBoxes -join ",") -eq "4,3,1" -and $v.Note -like "*boxes per level: below 13.4 ft: 4, 13.4 to 32.2 ft: 3, above 32.2 ft: 1")
$lab = @($v.Labels.Values | Where-Object { $_ -like "* | t1" })[0]
Check "18 the upper slab's label names its band" ($lab -like "wall | slab | thk 70.9 in |*| level above 32.2 ft | t1")

# a small slab at its own elevation is not a floor (under a quarter of the largest floor's area)
$few = New-Object 'System.Collections.Generic.List[WallBox]'
foreach ($r in @(@(0, 20, 0, 20, 3.8, 4.1), @(0, 5, 0, 5, 6.0, 6.3), @(0, 10, 0, 0.3, 0, 4))) {
    $w = New-Object WallBox; $w.Lo = [double[]]($r[0], $r[2], $r[4]); $w.Hi = [double[]]($r[1], $r[3], $r[5])
    $w.Shape = [WallViewWriter]::ShapeOf($r[1] - $r[0], $r[3] - $r[2], $r[5] - $r[4]); $few.Add($w)
}
$fl = [WallViewWriter]::Floors($few)
Check "19 a landing is not a floor" ($fl.Count -eq 1 -and (Near $fl[0] (4.1 / 0.3048)))
Check "20 the landing sits in the band above the floor" ([WallViewWriter]::LevelOf($few[1], $fl) -eq 1 -and [WallViewWriter]::LevelOf($few[2], $fl) -eq 0)

# no slab: no level groups
$only = New-Object 'System.Collections.Generic.List[WallBox]'; $only.Add($walls[0])
$m = Model; $next = $m.Next
$v0 = [WallViewWriter]::Add($only, -1, $m.Nodes, [ref]$next, $m.Off, 12)
Check "21 no slab: no levels" ($v0.Levels.Count -eq 0 -and $v0.Note -like "*`nwall levels: no slab found, no level groups" -and $v0.Labels[1] -notlike "*level*")
$m = Model; $next = $m.Next
Check "22 no walls: null" ($null -eq [WallViewWriter]::Add((New-Object 'System.Collections.Generic.List[WallBox]'), 10, $m.Nodes, [ref]$next, $m.Off, 12))

# groups: shapes painted, levels there for their eye only
$sc = New-Object FeaturesSidecar
[WallViewWriter]::AddGroups($sc, $v)
$names = @($sc.Groups | ForEach-Object { $_.Name }) -join "|"
Check "23 shape groups then level groups" ($names -eq "Walls: wall|Walls: slab|Walls: skew wall|Walls: skew slab|Walls: small|Walls level: below 13.4 ft|Walls level: 13.4 to 32.2 ft|Walls level: above 32.2 ft")
$shapeG = @($sc.Groups | Where-Object { $_.Name -notlike "Walls level:*" }); $levelG = @($sc.Groups | Where-Object { $_.Name -like "Walls level:*" })
Check "24 shape groups painted, level groups not" (@($shapeG | Where-Object { $_.Hidden -ne $false -or -not $_.Color }).Count -eq 0 -and @($levelG | Where-Object { $_.Hidden -ne $null -or $_.Color }).Count -eq 0)
Check "25 every shell in one shape group and one level group" ((($shapeG | ForEach-Object { $_.Members[0].Ids.Count } | Measure-Object -Sum).Sum -eq 48) -and (($levelG | ForEach-Object { $_.Members[0].Ids.Count } | Measure-Object -Sum).Sum -eq 48) -and
    @($sc.Groups | Where-Object { $_.Members[0].Domain -ne "shells" }).Count -eq 0)

# settings: DuctWalls, DuctWallMarginFt, -Walls none, missing file
$notes = New-Object 'System.Collections.Generic.List[string]'
$d = Get-DuctSettings -Data @{ DuctWalls = $tmp; DuctWallMarginFt = 25 } -Mode Revit -Notes $notes
Check "26 DuctWalls read into the settings" ($d.Walls.Count -eq 9 -and $d.WallMarginFt -eq 25 -and $notes[0] -like "walls: 9 boxes from *(wall 4, slab 2, skew wall 1, skew slab 1, small 1)")
$d = Get-DuctSettings -Data @{ DuctWalls = $tmp } -Mode Revit -Walls none -Notes $notes
Check "27 -Walls none = off; default margin 10" ($d.Walls.Count -eq 0 -and $d.WallMarginFt -eq 10)
$threw = $false; try { $null = Get-DuctSettings -Data @{ DuctWalls = "$tmp.nope" } -Mode Revit -Notes $notes } catch { $threw = $_.Exception.Message -like "walls CSV not found:*DuctWalls*" }
Check "28 a missing walls CSV names DuctWalls" $threw

Remove-Item $tmp, $tmp2 -ErrorAction SilentlyContinue
Write-Output ("{0} passed, {1} failed" -f $script:pass, $script:fail)
if ($script:fail) { exit 1 }
