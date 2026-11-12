# Tests for the room regions from the walls (duct\model\RoomRegions.cs). Windows PowerShell 5.1, a few seconds:
#   powershell -ExecutionPolicy Bypass -File scripts\duct\tests\Test-RoomRegions.ps1
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "..\..\arms\Import-Arms.ps1")
$script:fail = 0; $script:pass = 0
function Check($name, $cond) { if ($cond) { $script:pass++ } else { $script:fail++; Write-Output "FAIL: $name" } }
function Near($a, $b, $tol = 0.5) { [math]::Abs($a - $b) -lt $tol }
$ft = 0.3048
# a wall box given in feet (the reader's unit is meters)
function Box([double]$x0, [double]$y0, [double]$x1, [double]$y1, [double]$z0, [double]$z1) {
    $w = New-Object WallBox
    $w.Lo = [double[]](($x0 * $ft), ($y0 * $ft), ($z0 * $ft)); $w.Hi = [double[]](($x1 * $ft), ($y1 * $ft), ($z1 * $ft))
    $w.Shape = [WallViewWriter]::ShapeOf(($x1 - $x0) * $ft, ($y1 - $y0) * $ft, ($z1 - $z0) * $ft)
    return $w
}
function Walls($list) { $l = New-Object 'System.Collections.Generic.List[WallBox]'; foreach ($w in $list) { $l.Add($w) }; return ,$l }
function Floors($list) { $l = New-Object 'System.Collections.Generic.List[double]'; foreach ($f in $list) { $l.Add([double]$f) }; return ,$l }
function Voters($list) { $l = New-Object 'System.Collections.Generic.List[RoomVoter]'; foreach ($v in $list) { $o = New-Object RoomVoter; $o.X = $v[0]; $o.Y = $v[1]; $o.Z = $v[2]; $o.Room = $v[3]; $l.Add($o) }; return ,$l }

# plan, feet, walls 1 ft thick, floor at 0, walls 0..12:
#   outer box 0..60 x 0..30; a partition at x = 30 from y = 1 to y = 26 (a 3 ft gap to the north wall: closed by the
#   4 ft gap rule, open with a 1 ft rule); the east room has a partition at y = 15 from x = 30 to x = 45 only
#   (15 ft short: stays open, so the east room is one L-free region of two bays)
$plan = @(
    (Box 0 0 60 1 0 12), (Box 0 29 60 30 0 12), (Box 0 0 1 30 0 12), (Box 59 0 60 30 0 12),
    (Box 30 1 31 26 0 12),
    (Box 31 15 45 16 0 12))
$slab = Box 0 0 60 30 -1 0
$lo = [double[]](10, 5, 2); $hi = [double[]](50, 25, 10)

$r = [RoomRegions]::Find((Walls ($plan + $slab)), (Floors @(0)), $lo, $hi, 4, 10)
$reg = @($r.Regions)
Check "1 two rooms above the floor (the 3 ft gap is closed)" ($reg.Count -eq 2)
$west = $reg | Where-Object { $_.Contains(15, 15) }; $east = $reg | Where-Object { $_.Contains(50, 5) }
Check "2 west room 29 x 28 ft" ($west -and (Near $west.AreaSqFt (29 * 28) 1))
Check "3 east room: both bays, less the partition" ($east -and $east.Contains(50, 25) -and (Near $east.AreaSqFt (28 * 28 - 16) 1))
Check "4 the east room is several rectangles, the west one is one" ($west.Rects.Count -eq 1 -and $east.Rects.Count -gt 1)
Check "5 walls are not in a room" (-not ($reg | Where-Object { $_.Contains(30.5, 10) }))
Check "6 level and floor" ($west.LevelName -eq "above 0 ft" -and $west.Level -eq 1 -and (Near $west.FloorFt 0 0.01))
Check "7 note" ($r.Note -eq "rooms (walls, gaps up to 4 ft closed): level above 0 ft: 2 regions from 6 walls")

# a 1 ft gap rule leaves the 3 ft gap open: one room
$r1 = [RoomRegions]::Find((Walls ($plan + $slab)), (Floors @(0)), $lo, $hi, 1, 10)
Check "8 a gap wider than the rule joins the rooms" (@($r1.Regions).Count -eq 1 -and (Near @($r1.Regions)[0].AreaSqFt (58 * 28 - 25.5 - 14.5) 1))

# an open plan (no north wall) reaches the window's edge: no region
$openPlan = @($plan | Where-Object { $_ -ne $plan[1] })
$r2 = [RoomRegions]::Find((Walls ($openPlan + $slab)), (Floors @(0)), $lo, $hi, 4, 10)
Check "9 a room open to the outside is not a region" (@($r2.Regions).Count -eq 0)

# a low wall (4 ft) does not divide; a wall of the level above does not block this level
$low = Box 15 1 16 29 0 4
$upper = Box 15 1 16 29 13 25
$r3 = [RoomRegions]::Find((Walls ($plan + $slab + $low + $upper + (Box 0 0 60 30 12 13))), (Floors @(0, 13)), $lo, $hi, 4, 10)
$lv1 = @($r3.Regions | Where-Object { $_.Level -eq 1 })
Check "10 low and upper-level walls do not divide this level" ($lv1.Count -eq 2 -and ($lv1 | Where-Object { $_.Contains(15.5, 15) }))
Check "11 the band between two floors is named by both" ($lv1[0].LevelName -eq "0 to 13 ft")

# regions far from the model are left out (keep 10 ft): a second closed box 60 ft east is inside the search window
$farBox = @((Box 120 0 150 1 0 12), (Box 120 29 150 30 0 12), (Box 120 0 121 30 0 12), (Box 149 0 150 30 0 12))
$r4 = [RoomRegions]::Find((Walls ($plan + $slab + $farBox)), (Floors @(0)), $lo, $hi, 4, 10)
Check "12 a far room is found but left out" (@($r4.Regions).Count -eq 2 -and $r4.Note -like "*2 regions from 10 walls, 1 beyond the model left out")
$r5 = [RoomRegions]::Find((Walls ($plan + $slab + $farBox)), (Floors @(0)), $lo, $hi, 4, -1)
Check "13 keep -1: every region in the window" (@($r5.Regions).Count -eq 3)

# no slab at all: one band
$r6 = [RoomRegions]::Find((Walls $plan), (Floors @()), $lo, $hi, 4, 10)
Check "14 no floors: one band 'all', floor at the wall bottoms" (@($r6.Regions).Count -eq 2 -and @($r6.Regions)[0].LevelName -eq "all" -and (Near @($r6.Regions)[0].FloorFt 0 0.01))

# names: votes by position; A/B halves; the level decides; outside or in a wall: no vote
$reg = @($r.Regions); $west = $reg | Where-Object { $_.Contains(15, 15) }; $east = $reg | Where-Object { $_.Contains(50, 5) }
$note = [RoomRegions]::Name($r.Regions, (Voters @(@(10, 10, 5, "R101"), @(12, 20, 5, "R101"), @(14, 8, 5, "R101/R102"),
    @(45, 5, 5, "R102"), @(50, 20, 5, "R103"), @(40, 20, 5, "R103"), @(41, 21, 5, "R102"),
    @(10, 10, -5, "R900"), @(30.5, 10, 5, "R900"), @(500, 10, 5, "R900"), @(10, 10, 5, ""))))
Check "15 west = R101 (2.5 of 3 votes)" ($west.Name -eq "R101" -and -not $west.Mixed -and (Near $west.Votes["R101"] 2.5 0.01) -and (Near $west.Votes["R102"] 0.5 0.01))
Check "16 east = a tie on 2 votes each: mixed, first by name" ($east.Name -eq "R102" -and $east.Mixed)
Check "17 name note" ($note -eq "room names: 2 regions named by 7 votes (1 mixed), 0 unnamed")
Check "18 vote text" ($west.VoteText() -eq "R101 2.5, R102 0.5")

# the same code on two regions, and an unnamed one
$three = [RoomRegions]::Find((Walls ($plan + $slab + $farBox)), (Floors @(0)), $lo, $hi, 4, -1)
$note = [RoomRegions]::Name($three.Regions, (Voters @(@(10, 10, 5, "R101"), @(50, 5, 5, "R101"))))
$names = @($three.Regions | ForEach-Object { $_.Name }) -join "|"
Check "19 one code on two regions is numbered; no votes = unnamed" ($names -eq "R101|R101 (2)|unnamed 1" -and $note -eq "room names: 2 regions named by 2 votes (0 mixed), 1 unnamed; a code on several regions: R101 x2")

# viewer: patches continue the wall view's shells; groups; csv
$nodes = New-Object 'System.Collections.Generic.Dictionary[int,Node]'; $next = 1; $off = [double[]](0, 0, 0)
$null = [DuctGeom]::MakeNode($nodes, [ref]$next, $lo, $off, 12); $null = [DuctGeom]::MakeNode($nodes, [ref]$next, $hi, $off, 12)
$v = [WallViewWriter]::Add((Walls ($plan + $slab)), 10, $nodes, [ref]$next, $off, 12)
Check "20 the wall view carries the model box" ((Near $v.ModelLo[0] 10 0.01) -and (Near $v.ModelHi[2] 10 0.01))
$wallShells = $v.Elements.Count
$rr = [RoomRegions]::Find((Walls ($plan + $slab)), $v.FloorsFt, $v.ModelLo, $v.ModelHi, 4, 10)
$null = [RoomRegions]::Name($rr.Regions, (Voters @(,@(10, 10, 5, "R101"))))
[RoomRegions]::AddToView($v, $rr.Regions, $nodes, [ref]$next, $off, 12)
$rects = ($rr.Regions | ForEach-Object { $_.Rects.Count } | Measure-Object -Sum).Sum
Check "21 one flat quad per rectangle, ids after the walls" ($v.Elements.Count -eq $wallShells + $rects -and ($v.Elements.Keys | Measure-Object -Maximum).Maximum -eq $wallShells + $rects)
$patch = $v.Elements[$wallShells + 1]
Check "21b the patches are in their level band too (the level eye hides them)" ($v.ByLevel[1].Count -eq $wallShells + $rects -and $v.ByLevel[1] -contains [uint32]($wallShells + 1))
Check "22 patch just above the floor" ((@($patch.n | ForEach-Object { $_.xyz.Z } | Select-Object -Unique).Count -eq 1) -and (Near $patch.n[0].xyz.Z (0.05 * 12) 0.01))
Check "23 patch label" ($v.Labels[$wallShells + 1] -eq "room | R101 | level above 0 ft | 812 sq ft | votes: R101 1")
$sc = New-Object FeaturesSidecar
[WallViewWriter]::AddGroups($sc, $v); [RoomRegions]::AddGroups($sc, $v)
$rooms = @($sc.Groups | Where-Object { $_.Name -like "Room: *" })
Check "24 room groups: painted shells, the unnamed one grey" ((@($rooms | ForEach-Object { $_.Name }) -join "|") -eq "Room: R101|Room: unnamed 1" -and
    @($rooms | Where-Object { $_.Hidden -ne $false -or $_.Members[0].Domain -ne "shells" }).Count -eq 0 -and $rooms[1].Color -eq "#5a5f68" -and $rooms[0].Color -ne "#5a5f68")
$csv = Join-Path ([IO.Path]::GetTempPath()) ("pluto-rooms-" + [Guid]::NewGuid().ToString("N").Substring(0, 8) + ".csv")
[RoomRegions]::WriteCsv($csv, $rr.Regions)
$rowsCsv = @(Import-Csv $csv)
Check "25 rooms.csv" ($rowsCsv.Count -eq 2 -and $rowsCsv[0].Room -eq "R101" -and $rowsCsv[0].Level -eq "above 0 ft" -and $rowsCsv[0].Area_sqft -eq "812" -and $rowsCsv[0].Votes -eq "R101 1" -and $rowsCsv[1].Named -eq "0")
Remove-Item $csv -ErrorAction SilentlyContinue

Write-Output ("{0} passed, {1} failed" -f $script:pass, $script:fail)
if ($script:fail) { exit 1 }
