# Tests for the wall and slab penetrations (duct\model\DuctPenetrations.cs). Windows PowerShell 5.1, a few seconds:
#   powershell -ExecutionPolicy Bypass -File scripts\duct\tests\Test-DuctPenetrations.ps1
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "..\..\arms\Import-Arms.ps1")
$script:fail = 0; $script:pass = 0
function Check($name, $cond) { if ($cond) { $script:pass++ } else { $script:fail++; Write-Output "FAIL: $name" } }
function Near($a, $b, $tol = 0.01) { [math]::Abs($a - $b) -lt $tol }
$ft = 0.3048
function Box([double]$x0, [double]$y0, [double]$x1, [double]$y1, [double]$z0, [double]$z1, [string]$id) {
    $w = New-Object WallBox; $w.Oid = $id
    $w.Lo = [double[]](($x0 * $ft), ($y0 * $ft), ($z0 * $ft)); $w.Hi = [double[]](($x1 * $ft), ($y1 * $ft), ($z1 * $ft))
    $w.Shape = [WallViewWriter]::ShapeOf(($x1 - $x0) * $ft, ($y1 - $y0) * $ft, ($z1 - $z0) * $ft)
    return $w
}
function Walls($list) { $l = New-Object 'System.Collections.Generic.List[WallBox]'; foreach ($w in $list) { $l.Add($w) }; return ,$l }
# a clean model: joints (feet) and duct elements between them
function Model($pts, $edges) {
    $pos = New-Object 'System.Collections.Generic.Dictionary[int,double[]]'
    $i = 1; foreach ($q in $pts) { $pos[$i] = [double[]]$q; $i++ }
    $clean = New-Object 'System.Collections.Generic.List[CEdge]'
    foreach ($e in $edges) {
        $c = New-Object CEdge; $c.A = $e[0]; $c.B = $e[1]; $c.Dim = [double[]](0, 24, 12, [double]::NaN); $c.SysName = "SYSA"
        if ($e.Count -gt 2 -and $e[2] -eq "rigid") { $c.Rigid = $true }
        if ($e.Count -gt 2 -and $e[2] -eq "hanger") { $c.Hanger = $true }
        if ($e.Count -gt 2 -and $e[2] -eq "transition") { $c.DimB = [double[]](0, 12, 12, [double]::NaN) }
        $clean.Add($c)
    }
    return @{ P = $pos; Clean = $clean }
}

# walls: one across x at x 20..21 (y 0..30, z 0..12), a slab at z 12..13, a skew block, a low curb
$walls = Walls @((Box 20 0 21 30 0 12 "wall-1"), (Box 0 0 60 30 12 13 "slab-1"), (Box 40 5 46 11 0 12 "block"), (Box 5 0 6 0.5 0 0.5 "curb"))

# 1 a horizontal duct through the wall: split at the wall's mid thickness
$m = Model @(@(10, 10, 5), @(30, 10, 5)) @(,@(1, 2))
$r = [DuctPenetrations]::Apply($m.Clean, $m.P, $walls)
Check "1 one penetration, a new joint, the duct split in two" ($r.Items.Count -eq 1 -and $r.Splits -eq 1 -and $m.Clean.Count -eq 2 -and $m.P.Count -eq 3)
$n = $r.Items[0].Node
Check "2 the joint is on the centreline at the middle of the wall" ((Near $m.P[$n][0] 20.5) -and (Near $m.P[$n][1] 10) -and (Near $m.P[$n][2] 5))
Check "3 the pieces keep the order and the element's data" ($m.Clean[0].A -eq 1 -and $m.Clean[0].B -eq $n -and $m.Clean[1].A -eq $n -and $m.Clean[1].B -eq 2 -and $m.Clean[1].SysName -eq "SYSA" -and $m.Clean[1].Dim[1] -eq 24)
Check "4 kind, wall, thickness, system" ($r.Items[0].Kind -eq "wall" -and $r.Items[0].WallOid -eq "wall-1" -and (Near $r.Items[0].ThkIn 12) -and $r.Items[0].System -eq "SYSA" -and $r.Items[0].Split)
Check "5 note" ($r.Note -eq "penetrations: 1 (wall 1, slab 0) at 1 joints: 1 new joints (the duct split there), 0 at an existing joint; 0 stretches of duct inside a wall or slab box left alone (<out>.penetrations.csv)")

# 6 a riser through the slab
$m = Model @(@(10, 10, 5), @(10, 10, 20)) @(,@(1, 2))
$r = [DuctPenetrations]::Apply($m.Clean, $m.P, $walls)
Check "6 a riser through the slab: joint at the slab's mid depth" ($r.Items.Count -eq 1 -and $r.Items[0].Kind -eq "slab" -and (Near $m.P[$r.Items[0].Node][2] 12.5) -and (Near $r.Items[0].ThkIn 12))

# 7 an existing joint inside the wall is used, no split
$m = Model @(@(10, 10, 5), @(20.5, 10, 5), @(30, 10, 5)) @(@(1, 2), @(2, 3))
$r = [DuctPenetrations]::Apply($m.Clean, $m.P, $walls)
Check "7 a joint already inside the wall is the penetration: no split, reported once" ($r.Splits -eq 0 -and $m.Clean.Count -eq 2 -and $r.Items.Count -eq 1 -and $r.Items[0].Node -eq 2 -and $r.AtJoint -eq 1)

# 8 a duct that only pokes into the wall (ends inside it): its end joint is the penetration
$m = Model @(@(10, 10, 5), @(20.6, 10, 5)) @(,@(1, 2))
$r = [DuctPenetrations]::Apply($m.Clean, $m.P, $walls)
Check "8 a duct ending inside the wall" ($r.Items.Count -eq 1 -and $r.Items[0].Node -eq 2 -and -not $r.Items[0].Split -and $m.Clean.Count -eq 1)

# 9 a transition is never split: its nearer end joint is used
$m = Model @(@(18, 10, 5), @(30, 10, 5)) @(,@(1, 2, "transition"))
$r = [DuctPenetrations]::Apply($m.Clean, $m.P, $walls)
Check "9 a transition keeps its length; the nearer end is the joint" ($m.Clean.Count -eq 1 -and $r.Items[0].Node -eq 1 -and -not $r.Items[0].Split)

# 10 rigid links and hanger rows are left alone
$m = Model @(@(10, 10, 5), @(30, 10, 5), @(10, 12, 5), @(30, 12, 5)) @(@(1, 2, "rigid"), @(3, 4, "hanger"))
$r = [DuctPenetrations]::Apply($m.Clean, $m.P, $walls)
Check "10 rigid links and hangers: nothing" ($r.Items.Count -eq 0 -and $m.Clean.Count -eq 2)

# 11 a duct running along inside the wall is not a penetration; one beside it is nothing
$m = Model @(@(20.5, 2, 5), @(20.5, 28, 5), @(25, 2, 5), @(25, 28, 5)) @(@(1, 2), @(3, 4))
$r = [DuctPenetrations]::Apply($m.Clean, $m.P, $walls)
Check "11 a duct inside the wall along its length is counted, not split" ($r.Items.Count -eq 0 -and $r.Inside -eq 1 -and $m.Clean.Count -eq 2)

# 12 skew boxes and small pieces are not walls: no penetration
$m = Model @(@(35, 8, 5), @(50, 8, 5), @(0, 0.25, 0.25), @(10, 0.25, 0.25)) @(@(1, 2), @(3, 4))
$r = [DuctPenetrations]::Apply($m.Clean, $m.P, $walls)
Check "12 a skew box and a curb give nothing" ($walls[2].Shape -eq "skew slab" -or $walls[2].Shape -eq "skew wall")
Check "12b no penetration there" ($r.Items.Count -eq 0)

# 13 one duct through a wall and, further on, another wall: two joints, three pieces, in order
$two = Walls @((Box 20 0 21 30 0 12 "wall-1"), (Box 40 0 41 30 0 12 "wall-2"))
$m = Model @(@(10, 10, 5), @(50, 10, 5)) @(,@(1, 2))
$r = [DuctPenetrations]::Apply($m.Clean, $m.P, $two)
Check "13 two walls: three pieces in order" ($r.Items.Count -eq 2 -and $m.Clean.Count -eq 3 -and $m.Clean[0].A -eq 1 -and $m.Clean[0].B -eq $r.Items[0].Node -and $m.Clean[1].B -eq $r.Items[1].Node -and $m.Clean[2].B -eq 2 -and (Near $m.P[$r.Items[1].Node][0] 40.5))

# 14 a diagonal duct through the wall: the middle of the stretch inside
$m = Model @(@(15, 5, 5), @(26, 16, 5)) @(,@(1, 2))
$r = [DuctPenetrations]::Apply($m.Clean, $m.P, $walls)
Check "14 a diagonal duct" ($r.Items.Count -eq 1 -and (Near $m.P[$r.Items[0].Node][0] 20.5) -and (Near $m.P[$r.Items[0].Node][1] 10.5))

# 15 csv and viewer groups
$m = Model @(@(10, 10, 5), @(30, 10, 5), @(10, 20, 5), @(10, 20, 20)) @(@(1, 2), @(3, 4))
$r = [DuctPenetrations]::Apply($m.Clean, $m.P, $walls)
$csv = Join-Path ([IO.Path]::GetTempPath()) ("pluto-pen-" + [Guid]::NewGuid().ToString("N").Substring(0, 8) + ".csv")
[DuctPenetrations]::WriteCsv($csv, $r, $m.P)
$rows = @(Import-Csv $csv)
Check "15 penetrations.csv: joint names as in frames.csv, inches" ($rows.Count -eq 2 -and $rows[0].Joint -eq ("N" + $r.Items[0].Node) -and $rows[0].Kind -eq "wall" -and $rows[0].X_in -eq "246" -and $rows[0].Thk_in -eq "12" -and $rows[0].NewJoint -eq "1" -and $rows[0].WallOid -eq "wall-1" -and $rows[1].Kind -eq "slab" -and $rows[1].Z_in -eq "150")
Remove-Item $csv -ErrorAction SilentlyContinue
$sc = New-Object FeaturesSidecar
[DuctPenetrations]::AddGroups($sc, $r, [Func[int,int]]{ param($n) $n + 100 })
$g = @($sc.Groups)
Check "16 two node groups with glyphs and anchors" ($g.Count -eq 2 -and $g[0].Name -eq "Penetration: wall" -and $g[1].Name -eq "Penetration: slab" -and $g[0].StaadName -eq "PEN_WALL" -and
    $g[0].Look.Section -eq "Penetrations" -and $g[0].Anchors.Count -eq 1 -and $g[0].Anchors[0].Domain -eq "nodes" -and $g[0].Anchors[0].Id -eq ($r.Items[0].Node + 100) -and
    $g[0].Anchors[0].Label -like "N* through a 12 in wall | wall-1" -and $g[0].Members[0].NodeIds.Count -eq 1)
$sc2 = New-Object FeaturesSidecar
[DuctPenetrations]::AddGroups($sc2, $r, [Func[int,int]]{ param($n) 0 })
Check "17 joints not in the viewer file give no group" (@($sc2.Groups).Count -eq 0)

Write-Output ("{0} passed, {1} failed" -f $script:pass, $script:fail)
if ($script:fail) { exit 1 }
