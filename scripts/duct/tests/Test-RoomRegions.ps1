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

# ---- the cull by room (RoomRegions.Plan) ----
# west and east rooms + the far box; a duct in the west room only; steel (meters): west, east, far box, and one in no region
function Steel([double]$x, [double]$y, [double]$z, [string]$id) {
    $m = New-Object SteelMember; $m.MemberOid = $id; $m.Room = ""
    $m.P0 = New-Object Vec3 (($x - 1) * $ft), ($y * $ft), ($z * $ft); $m.P1 = New-Object Vec3 (($x + 1) * $ft), ($y * $ft), ($z * $ft)
    return $m
}
function SteelList($list) { $l = New-Object 'System.Collections.Generic.List[SteelMember]'; foreach ($m in $list) { $l.Add($m) }; return ,$l }
function Segs($list) { $l = New-Object 'System.Collections.Generic.List[double[]]'; foreach ($s in $list) { $l.Add([double[]]$s) }; return ,$l }
$upperWall = Box 10 1 11 29 13 25      # stands on the ceiling slab: the level above
$ceiling = Box 0 0 60 30 12 13
$all = Walls ($plan + $slab + $farBox + $ceiling + $upperWall)
$steel = SteelList @((Steel 15 20 5 "west"), (Steel 50 5 5 "east"), (Steel 135 15 5 "far"), (Steel 300 300 5 "none"))
$ducts = Segs @(,@(10, 10, 5, 20, 10, 5))
$pl = [RoomRegions]::Plan($all, $ducts, $steel, (Voters @(,@(10, 10, 5, "R101"))), 10, 4, $true)
Check "c1 three regions on the duct level, one holds the duct" (@($pl.All | Where-Object { $_.Level -eq 1 }).Count -eq 3 -and $pl.Kept.Count -eq 1 -and $pl.Kept[0].Contains(15, 15) -and $pl.Culled)
Check "c2 only the steel in a room with ducts stays" ((@($pl.Steel | ForEach-Object { $_.MemberOid }) -join " ") -eq "west" -and $pl.SteelDropped -eq 2 -and $pl.SteelNoRegion -eq 1)
Check "c3 the kept region is named; floors from the walls near the ducts" ($pl.Kept[0].Name -eq "R101" -and ($pl.FloorsFt -join ",") -eq "0,13")
Check "c4 ceiling of the kept region = the next floor" ((Near $pl.Kept[0].CeilFt 13 0.01) -and (Near $pl.Kept[0].FloorFt 0 0.01))
Check "c5 note" ($pl.Note -like "rooms (walls, gaps up to 4 ft closed): level 0 to 13 ft: 3 regions from 10 walls*`nroom cull: 1 of 3 regions hold drawn ducts (areas 812 sq ft); steel: 1 of 4 members in a room with ducts kept, 2 in a room without ducts and 1 in no room left out; drawn duct outside every room: 0 ft`nroom names: 1 regions named by 1 votes (0 mixed), 0 unnamed (<out>_clean.rooms.csv)")
$off0 = [RoomRegions]::Plan($all, $ducts, $steel, (Voters @(,@(10, 10, 5, "R101"))), 10, 4, $false)
Check "c6 DuctRoomCull off: the steel stays, the regions are still culled" ($off0.Steel.Count -eq 4 -and $off0.SteelDropped -eq 2 -and $off0.Kept.Count -eq 1 -and $off0.Note -like "*in no room kept (DuctRoomCull off)*")
$none = [RoomRegions]::Plan($all, (Segs @(,@(300, 300, 5, 310, 300, 5))), $steel, (Voters @()), -1, 4, $true)
Check "c7 no region holds a duct: nothing culled" (-not $none.Culled -and $none.Steel.Count -eq 4 -and $none.Kept.Count -eq 0 -and $none.Note -like "*`nroom cull: none of * regions holds a duct: nothing culled*")
# a duct partly outside every room: its outside length is reported, the duct is not removed
$half = [RoomRegions]::Plan($all, (Segs @(,@(10, 10, 5, 10, -50, 5))), $steel, (Voters @()), 10, 4, $true)
Check "c7b drawn duct outside every room is measured" ($half.Kept.Count -eq 1 -and $half.DuctOutsideFt -gt 40 -and $half.DuctOutsideFt -lt 56 -and $half.Note -like "*; drawn duct outside every room: * ft*")
# walls cut to the kept (west) room, x 1..30, y 1..29, floor 0, ceiling 13
$names = @("south", "north", "west", "east", "part", "stub", "floor", "f1", "f2", "f3", "f4", "ceiling", "upper")
$cut = [RoomRegions]::CutWalls($all, $pl)
$got = @{}; $i = 0
foreach ($w in $all) { if ($cut.ContainsKey($w)) { $got[$names[$i]] = $cut[$w] }; $i++ }
Check "c8 the room's four walls and its floor and ceiling slabs are kept; nothing else" ((($names | Where-Object { $got.ContainsKey($_) }) -join " ") -eq "south north west part floor ceiling")
$s = $got["south"][0]; $n = $got["north"][0]; $wp = $got["west"][0]; $pt = $got["part"][0]
Check "c8b the south wall is cut at the partition (over its thickness: the corner), full thickness, floor to its top" ($got["south"].Count -eq 1 -and (Near $s.Lo[0] 0 0.01) -and (Near $s.Hi[0] 31 0.01) -and (Near $s.Lo[1] 0 0.01) -and (Near $s.Hi[1] 1 0.01) -and (Near $s.Lo[2] 0 0.01) -and (Near $s.Hi[2] 12 0.01))
Check "c8c the north wall is cut at the room's edge (the partition stops short of it)" ((Near $n.Lo[0] 0 0.01) -and (Near $n.Hi[0] 30 0.01))
Check "c8d the west wall and the partition keep their length" ((Near $wp.Lo[1] 0 0.01) -and (Near $wp.Hi[1] 30 0.01) -and (Near $pt.Lo[1] 1 0.01) -and (Near $pt.Hi[1] 26 0.01) -and $pt.Level -eq 1)
$fl = $got["floor"][0]
Check "c8e the slabs are cut to the room's floor area" ($got["floor"].Count -eq 1 -and (Near $fl.Lo[0] 1 0.01) -and (Near $fl.Hi[0] 30 0.01) -and (Near $fl.Lo[1] 1 0.01) -and (Near $fl.Hi[1] 29 0.01) -and (Near $fl.Lo[2] -1 0.01) -and (Near $got["ceiling"][0].Hi[2] 13 0.01))
# both rooms kept: the south wall runs on to the east wall; the stub wall stands in the east room and is kept whole
$both = [RoomRegions]::Plan($all, (Segs @(@(10, 10, 5, 20, 10, 5), @(50, 5, 5, 55, 5, 5))), $steel, (Voters @()), 10, 4, $true)
$cut2 = [RoomRegions]::CutWalls($all, $both)
Check "c8f two kept rooms: one piece of the south wall along both, the stub and the east wall kept" ($both.Kept.Count -eq 2 -and $cut2[$all[0]].Count -eq 1 -and (Near $cut2[$all[0]][0].Hi[0] 60 0.01) -and $cut2.ContainsKey($all[5]) -and $cut2.ContainsKey($all[3]) -and -not $cut2.ContainsKey($all[7]))
# ---- steel by position: the whole CSV near the ducts, whatever its room code ----
function SteelR([double]$x, [double]$y, [double]$z, [string]$id, [string]$room) { $m = Steel $x $y $z $id; $m.Room = $room; return $m }
$csvAll = SteelList @((SteelR 15 20 5 "labelled" "R101"), (SteelR 16 20 5 "no code" ""), (SteelR 17 20 5 "wrong code" "R900"), (SteelR 18 20 5 "variant" "R101A"),
    (SteelR 50 5 5 "next room" "R102"), (SteelR 900 900 5 "far away" "R101"))
$dsg = Segs @(,@(10, 10, 5, 20, 10, 5))
$cand = [RoomRegions]::CropSteel($csvAll, $dsg, 40)
Check "p1 candidates: the CSV within the margin of the ducts, whatever the room code" ((@($cand | ForEach-Object { $_.MemberOid }) -join "|") -eq "labelled|no code|wrong code|variant|next room")
$pp = [RoomRegions]::Plan($all, $dsg, $cand, (Voters @()), 10, 4, $true, -1)
Check "p2 kept by position: everything standing in the room with ducts, labelled or not" ((@($pp.Steel | ForEach-Object { $_.MemberOid }) -join "|") -eq "labelled|no code|wrong code|variant")
Check "p3 the audit line: what the room list would have lost" ([RoomRegions]::PositionNote($pp.Steel, $cand.Count, $csvAll.Count, "R101", 3) -eq
    "steel by position: 4 members kept (their midpoint in a room with ducts) of 5 near the ducts, 6 in the steel CSV; 1 of them have no room code, 1 a room code not on the room list (R900 1); the room list alone gave 3 members")
Check "p4 no room list (whole building): no code counts as off the list" ([RoomRegions]::PositionNote($pp.Steel, 5, 6, "", 6) -like "*1 of them have no room code, 0 a room code not on the room list; the room list alone gave 6 members")

# ---- the box cull: kept regions trimmed to the drawn ducts' plan box + pad ----
# east room x 31..59, y 1..29 (two bays joined past the stub wall); a duct in its south bay only: (50,5) to (55,5).
# box + 5 ft = x 45..60, y 0..10: the room is trimmed to x 45..59, y 1..10; the steel in its north bay goes
$steelE = SteelList @((Steel 50 6 5 "south bay"), (Steel 50 20 5 "north bay"), (Steel 35 5 5 "same room, west of the box"))
$bx = [RoomRegions]::Plan($all, (Segs @(,@(50, 5, 5, 55, 5, 5))), $steelE, (Voters @()), 10, 4, $true, 5)
$er = $bx.Kept[0]
Check "b1 the kept room is trimmed to the box" ($bx.Kept.Count -eq 1 -and (Near $er.Lo[0] 45 0.01) -and (Near $er.Hi[0] 59 0.01) -and (Near $er.Lo[1] 1 0.01) -and (Near $er.Hi[1] 10 0.01) -and (Near $er.AreaSqFt (14 * 9) 0.5))
Check "b2 steel outside the box goes, though it is in the same room" ((@($bx.Steel | ForEach-Object { $_.MemberOid }) -join "|") -eq "south bay")
Check "b3 note" ($bx.Note -like "*`nroom box: the drawn ducts' plan box + 5 ft (15 x 10 ft): 1 of 1 kept regions trimmed, floor kept 126 of 768 sq ft*")
$cutB = [RoomRegions]::CutWalls($all, $bx)
$eastW = $cutB[$all[3]][0]; $southW = $cutB[$all[0]][0]
Check "b4 walls are cut at the box: the east wall to y 10, the south wall from x 45" ((Near $eastW.Hi[1] 10 0.01) -and (Near $eastW.Lo[1] 0 0.01) -and (Near $southW.Lo[0] 45 0.01) -and (Near $southW.Hi[0] 60 0.01) -and -not $cutB.ContainsKey($all[1]) -and -not $cutB.ContainsKey($all[4]))
Check "b5 the slab is cut to the trimmed room" ((Near $cutB[$all[6]][0].Lo[0] 45 0.01) -and (Near $cutB[$all[6]][0].Hi[1] 10 0.01))
$nb = [RoomRegions]::Plan($all, (Segs @(,@(50, 5, 5, 55, 5, 5))), $steelE, (Voters @()), 10, 4, $true, -1)
Check "b6 a negative pad = no box" ((Near $nb.Kept[0].AreaSqFt 768 1) -and $nb.Steel.Count -eq 3 -and $nb.Note -notlike "*room box*")
$nodes = New-Object 'System.Collections.Generic.Dictionary[int,Node]'; $next = 1
$null = [DuctGeom]::MakeNode($nodes, [ref]$next, [double[]](10, 10, 5), $off, 12); $null = [DuctGeom]::MakeNode($nodes, [ref]$next, [double[]](300, 300, 5), $off, 12)
$v = [WallViewWriter]::Add($all, 10, $nodes, [ref]$next, $off, 12, $pl)
Check "c9 the view draws the cut pieces only, levels from the plan" ($v.Drawn -eq 6 -and $v.Pieces -eq 6 -and $v.Elements.Count -eq 36 -and $v.Note -like "walls: 6 of 13 drawn (cut to the rooms with ducts, 6 pieces): wall 4, slab 2,*" -and ($v.FloorsFt -join ",") -eq "0,13")
$lab = @($v.Labels.Values | Where-Object { $_ -like "wall | wall | thk 12 in | 31 x 1 x 12 ft*" })[0]
Check "c9b a cut piece says so and names its level" ($lab -like "* | level 0 to 13 ft | cut to the room*")
$v2 = [WallViewWriter]::Add($all, 10, $nodes, [ref]$next, $off, 12, $none)
Check "c10 no kept region: the margin rule" ($v2.Note -like "walls: * drawn (within 10 ft of the model)*")

Write-Output ("{0} passed, {1} failed" -f $script:pass, $script:fail)
if ($script:fail) { exit 1 }
