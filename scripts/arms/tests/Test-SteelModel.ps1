# Synthetic tests for SteelModel.cs (steel connectivity, end releases, open-end restraints) and the steel in the
# clean duct model. Windows PowerShell 5.1, fresh window:
#   powershell -ExecutionPolicy Bypass -File scripts\arms\tests\Test-SteelModel.ps1
# Exit code 0 = all pass.
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "..\Import-Arms.ps1")
$script:fail = 0; $script:pass = 0
function Check($name, $cond) {
    if ($cond) { $script:pass++ } else { $script:fail++; Write-Output "FAIL: $name" }
}
$in = 0.0254
# member from inches; web up unless given
function M($oid, $x0, $y0, $z0, $x1, $y1, $z1) {
    $m = New-Object SteelMember
    $m.MemberOid = $oid; $m.SectionName = "W12X26"
    $m.P0 = New-Object Vec3 ($x0 * $in), ($y0 * $in), ($z0 * $in)
    $m.P1 = New-Object Vec3 ($x1 * $in), ($y1 * $in), ($z1 * $in)
    $m.D = 12.2 * $in; $m.Bf = 6.49 * $in; $m.Tf = 0.38 * $in; $m.Tw = 0.23 * $in
    $m
}
function Model([object[]]$ms) {
    $l = New-Object 'System.Collections.Generic.List[SteelMember]'
    foreach ($m in $ms) { $l.Add($m) }
    [SteelModel]::Build($l, (New-Object 'SteelModel+Settings'))
}
# the end conditions / releases of the frame(s) of a member oid
function Ends($sm, $oid) {
    $f = @($sm.Frames | Where-Object { $sm.Members[$_.Source].MemberOid -eq $oid })
    $first = $f[0]; $last = $f[-1]
    @{ I = $first.EndI; RI = $first.ReleaseI; J = $last.EndJ; RJ = $last.ReleaseJ; N = $f.Count; JI = $sm.Joints[$first.I - 1]; JJ = $sm.Joints[$last.J - 1] }
}

# 1 open horizontal: pinned both ends, far from steel
$sm = Model @((M "B" 0 0 120 240 0 120))
$e = Ends $sm "B"
Check "1 horizontal open = pinned" ($e.I -eq "open-pinned" -and $e.J -eq "open-pinned" -and $e.JJ.Restraint -eq "PINNED" -and $e.RI -eq "")
Check "1 lone pinned-pinned member: I end also holds torsion (PINNED+T, axis along the member)" ($e.JI.Restraint -eq "PINNED+T" -and [math]::Abs($e.JI.TAxis[0] - 1) -lt 1e-9)
Check "1 lone member: no OPEN_NEAR" (-not ($e.JI.Flags -contains "OPEN_NEAR"))

# 2 open vertical: fixed
$e = Ends (Model @((M "C" 0 0 0 0 0 144))) "C"
Check "2 vertical open = fixed" ($e.I -eq "open-fixed" -and $e.JJ.Restraint -eq "FIXED")

# 3 sloped: 30 deg pinned, 60 deg fixed
$e = Ends (Model @((M "S30" 0 0 0 (100 * [math]::Cos([math]::PI / 6)) 0 (100 * [math]::Sin([math]::PI / 6))))) "S30"
Check "3a slope 30 = pinned" ($e.I -eq "open-pinned")
$e = Ends (Model @((M "S60" 0 0 0 (100 * [math]::Cos([math]::PI / 3)) 0 (100 * [math]::Sin([math]::PI / 3))))) "S60"
Check "3b slope 60 = fixed" ($e.I -eq "open-fixed")

# 4 beam framing into a column side, its end 3 in off the column centreline (CP offset): snapped, hinged; column split, continuous
$sm = Model @((M "COL" 0 0 0 0 0 240), (M "BM" 3 0 120 120 0 120))
$b = Ends $sm "BM"; $c = Ends $sm "COL"
Check "4 framed end hinged" ($b.I -eq "framed" -and $b.RI -eq "M2M3" -and $b.J -eq "open-pinned")
Check "4 joint moved onto the column centreline" ([math]::Abs($b.JI.X) -lt 1e-6 -and [math]::Abs($b.JI.Z - 120) -lt 1e-6)
Check "4 column split in 2, no release" ($c.N -eq 2 -and (@($sm.Frames | Where-Object { $sm.Members[$_.Source].MemberOid -eq "COL" -and ($_.ReleaseI -ne "" -or $_.ReleaseJ -ne "") }).Count -eq 0))
Check "4 column bottom fixed, top fixed" ($c.I -eq "open-fixed" -and $c.J -eq "open-fixed")

# 5 snap tolerance: 5 in snaps, 7 in stays open
$e = Ends (Model @((M "COL" 0 0 0 0 0 240), (M "BM" 5 0 120 120 0 120))) "BM"
Check "5a 5 in snaps" ($e.I -eq "framed")
$e = Ends (Model @((M "COL" 0 0 0 0 0 240), (M "BM" 7 0 120 120 0 120))) "BM"
Check "5b 7 in open" ($e.I -eq "open-pinned")
Check "5c open end 7 in from steel: OPEN_NEAR" ($e.JI.Flags -contains "OPEN_NEAR")

# 6 corner: two beams ending at one point (2 in apart)
$sm = Model @((M "A" 0 0 120 120 0 120), (M "B" 120 2 120 120 120 120))
$a = Ends $sm "A"; $b = Ends $sm "B"
$fa = @($sm.Frames | Where-Object { $sm.Members[$_.Source].MemberOid -eq "A" })[0]; $fb = @($sm.Frames | Where-Object { $sm.Members[$_.Source].MemberOid -eq "B" })[0]
Check "6 corner: one end released, the other keeps its moments for stability" ($a.J -eq "corner" -and $b.I -eq "corner" -and (@($a.RJ, $b.RI) -contains "M2M3") -and (@($a.RJ, $b.RI) -contains "") -and ($fa.KeptJ -or $fb.KeptI))
Check "6 corner: both ends drawn as hinges" ([SteelModel]::EndGroup($a.J, $a.RJ, $fa.KeptJ) -eq "hinged" -and [SteelModel]::EndGroup($b.I, $b.RI, $fb.KeptI) -eq "hinged")
Check "6 corner one joint" ($a.JJ.Id -eq $b.JI.Id -and $a.JJ.Condition -eq "corner")

# 7 collinear splice (1 in gap)
$sm = Model @((M "A" 0 0 120 120 0 120), (M "B" 121 0 120 240 0 120))
$a = Ends $sm "A"; $b = Ends $sm "B"
$fa = @($sm.Frames | Where-Object { $sm.Members[$_.Source].MemberOid -eq "A" })[0]; $fb = @($sm.Frames | Where-Object { $sm.Members[$_.Source].MemberOid -eq "B" })[0]
Check "7 collinear: one side released, one kept" ($a.J -eq "collinear" -and $b.I -eq "collinear" -and (@($a.RJ, $b.RI) -contains "M2M3") -and ($fa.KeptJ -or $fb.KeptI))
Check "7 collinear end groups" ([SteelModel]::EndGroup($a.J, $a.RJ, $fa.KeptJ) -eq "collinear-hinged" -and [SteelModel]::EndGroup($b.I, $b.RI, $fb.KeptI) -eq "collinear-hinged")

# 8 four horizontals into a continuous vertical (each 3 in off the centreline on its side)
$sm = Model @((M "COL" 0 0 0 0 0 240), (M "E" 3 0 120 120 0 120), (M "W" (-3) 0 120 (-120) 0 120), (M "N" 0 3 120 0 120 120), (M "S" 0 (-3) 120 0 (-120) 120))
$ok = $true
foreach ($o in "E", "W", "N", "S") { $x = Ends $sm $o; if ($x.I -ne "framed" -or $x.RI -ne "M2M3") { $ok = $false } }
Check "8 four horizontals hinged" $ok
Check "8 one joint, column in 2" ((@($sm.Joints | Where-Object { $_.Condition -eq "framed" }).Count -eq 1) -and (Ends $sm "COL").N -eq 2)

# 9 four horizontals ending at the top of a vertical: horizontals hinged, vertical kept
$sm = Model @((M "COL" 0 0 0 0 0 120), (M "E" 0 0 120 120 0 120), (M "W" 0 0 120 (-120) 0 120), (M "N" 0 0 120 0 120 120), (M "S" 0 0 120 0 (-120) 120))
$ok = $true
foreach ($o in "E", "W", "N", "S") { $x = Ends $sm $o; if ($x.RI -ne "M2M3") { $ok = $false } }
Check "9 horizontals hinged at the column top" $ok
Check "9 column top kept" ((Ends $sm "COL").RJ -eq "")
$fc = @($sm.Frames | Where-Object { $sm.Members[$_.Source].MemberOid -eq "COL" })[-1]
Check "9 column kept as continuous, not for stability (no hinge glyph)" (-not $fc.KeptJ -and $null -eq [SteelModel]::EndGroup($fc.EndJ, $fc.ReleaseJ, $fc.KeptJ))

# 10 OPEN_NEAR threshold: open end 12 in from other steel flagged, 30 in not
$sm = Model @((M "A" 0 0 0 240 0 0), (M "B" 100 30 0 100 300 0), (M "C" 200 12 0 200 300 0))
Check "10a 30 in -> no flag" (-not ((Ends $sm "B").JI.Flags -contains "OPEN_NEAR"))
Check "10b 12 in -> OPEN_NEAR" ((Ends $sm "C").JI.Flags -contains "OPEN_NEAR")

# 11 CSV files
$tmp = Join-Path $env:TEMP ("steelmodel_test_" + [guid]::NewGuid().ToString("N"))
$sm = Model @((M "COL" 0 0 0 0 0 240), (M "BM" 3 0 120 120 0 120))
$sm.WriteCsv($tmp)
$fr = @(Import-Csv "$tmp.steel_frames.csv"); $jt = @(Import-Csv "$tmp.steel_joints.csv")
Check "11 frames csv" ($fr.Count -eq 3 -and $fr[0].Frame -eq "SF1" -and $fr[0].JointI -like "S*" -and $fr[0].Section -eq "W12X26" -and [double]$fr[0].D_in -eq 12.2)
Check "11 joints csv" ($jt.Count -eq 4 -and @($jt | Where-Object { $_.Restraint -eq "FIXED" }).Count -eq 2 -and @($jt | Where-Object { $_.Restraint -eq "PINNED" }).Count -eq 1)
Check "11 release column" (@($fr | Where-Object { $_.ReleaseI -eq "M2M3" -or $_.ReleaseJ -eq "M2M3" }).Count -eq 1)
Remove-Item "$tmp.steel_frames.csv", "$tmp.steel_joints.csv"

# 13 stability rules (2026-10-30)
# 13a column base where a brace lands: FIXED, column continuous, brace hinged
$sm = Model @((M "COL" 0 0 0 0 0 120), (M "BR" 0 0 0 100 0 120), (M "BM" 0 0 120 100 0 120))
$c = Ends $sm "COL"; $br = Ends $sm "BR"
Check "13a column base: FIXED, condition column-base" ($c.JI.Restraint -eq "FIXED" -and $c.JI.Condition -eq "column-base" -and $c.I -eq "column-base" -and $c.RI -eq "")
Check "13a brace at the base hinged" ($br.I -eq "corner" -and $br.RI -eq "M2M3")
Check "13a column-base drawn as a fixed support" ([SteelModel]::EndGroup($c.I, $c.RI, $false) -eq "open-fixed")
Check "13a counted" ($sm.ColumnBases -eq 1)
# 13b a joint where something goes on below is not a base (a brace hanging under it)
$sm = Model @((M "COL" 0 0 60 0 0 120), (M "BR" 0 0 60 60 0 0))
Check "13b member going below: not a base" ((Ends $sm "COL").JI.Condition -ne "column-base")
# 13c hung frame knee (hanger fixed at the ceiling, beam between two hangers): one member keeps its moments per knee
$sm = Model @((M "H1" 0 0 130 0 0 100), (M "H2" 0 96 130 0 96 100), (M "BM" 0 0 100 0 96 100))
$kept = @($sm.Frames | Where-Object { $_.KeptI -or $_.KeptJ }).Count
Check "13c two knees, one kept member end each" ($kept -eq 2 -and $sm.KeptMoments -eq 2)
Check "13c the vertical hanger is the one kept" (@($sm.Frames | Where-Object { $sm.Members[$_.Source].MemberOid -like "H*" -and $_.KeptJ }).Count -eq 2)
# 13d a framed joint has a continuous member: nothing kept
$sm = Model @((M "COL" 0 0 0 0 0 240), (M "BM" 3 0 120 120 0 120))
Check "13d framed: nothing kept" ($sm.KeptMoments -eq 0)
# 13e a member pinned at both ends but touching other steel is not lone: no torsion restraint
$sm = Model @((M "A" 0 0 120 120 0 120), (M "B" 120 0 120 240 0 120))
Check "13e collinear pair: no PINNED+T" (@($sm.Joints | Where-Object { $_.Restraint -eq "PINNED+T" }).Count -eq 0)
# 13f joints csv carries the torsion axis
$tmp = Join-Path $env:TEMP ("steelmodel_test_" + [guid]::NewGuid().ToString("N"))
$sm = Model @((M "S" 0 0 0 0 120 0)); $sm.WriteCsv($tmp)
$jt = @(Import-Csv "$tmp.steel_joints.csv")
Check "13f csv: PINNED+T with TAxis, the other end PINNED" ($jt[0].Restraint -eq "PINNED+T" -and [double]$jt[0].TAxisY -eq 1 -and $jt[1].Restraint -eq "PINNED" -and $jt[1].TAxisX -eq "")
Remove-Item "$tmp.steel_frames.csv", "$tmp.steel_joints.csv"

# 12 SplitAt / TopAt (the duct-supports hook): split keeps the open end; top of a CP 8 beam = its routed line
$sm = Model @((M "BM" 0 0 120 240 0 120))
$sm.Members[0].Cp = 8
$t = 0.0; $d = 0.0
$f = $sm.NearestFrame([double[]]@(60, 0, 130), [ref]$t, [ref]$d)
$j = $sm.SplitAt($f, $t, "SUPPORT")
$e = Ends $sm "BM"
Check "12a split in 2, ends kept" ($e.N -eq 2 -and $e.I -eq "open-pinned" -and $e.J -eq "open-pinned" -and [math]::Abs($j.X - 60) -lt 1e-6 -and $j.Flags -contains "SUPPORT")
$top = $sm.TopAt($f, 1.0)
Check "12b CP 8 top = routed line" ([math]::Abs($top[2] - 120) -lt 1e-6)
$sm.Members[0].Cp = 0
Check "12c centred: top = line + D/2" ([math]::Abs($sm.TopAt($f, 1.0)[2] - 126.1) -lt 1e-6)
Check "12d split near an end returns the end" ($sm.SplitAt($f, 0.001, "").Id -eq $f.I)

# 13 snap rule "contact" (prototype): the end's section touches the other member's solid
function ModelMode([object[]]$ms, $mode) {
    $l = New-Object 'System.Collections.Generic.List[SteelMember]'
    foreach ($m in $ms) { $l.Add($m) }
    $s = New-Object 'SteelModel+Settings'; $s.SnapMode = $mode
    [SteelModel]::Build($l, $s)
}
# a beam sitting on a girder: centroids one depth (12.2 in) apart, the end over the girder's middle
$stack = @((M "G" 0 (-120) 120 0 120 120), (M "B" 0 0 132.2 (-120) 0 132.2))
$sm = ModelMode $stack "centreline"; $e = Ends $sm "B"
Check "13a centreline: a beam on a girder stays open (12.2 in > 6)" ($e.I -like "open-*" -and $e.JI.Flags -contains "OPEN_NEAR")
$sm = ModelMode $stack "contact"; $e = Ends $sm "B"; $g = Ends $sm "G"
Check "13b contact: the beam on the girder connects (framed, girder split)" ($e.I -eq "framed" -and $e.RI -eq "M2M3" -and $g.N -eq 2)
Check "13c contact: the joint stays at the placement point projected on the girder line" ($e.JI.X -eq 0 -and $e.JI.Y -eq 0 -and $e.JI.Z -eq 120)
# a beam ending 9 in from a girder's centreline: 5.8 in of air to its flange, not a contact
$air = @((M "G" 0 (-120) 120 0 120 120), (M "B" 9 0 120 129 0 120))
$sm = ModelMode $air "contact"; $e = Ends $sm "B"
Check "13d contact: 5.8 in of air stays open, flagged OPEN_NEAR" ($e.I -like "open-*" -and $e.JI.Flags -contains "OPEN_NEAR")
Check "13e gap histogram: that end in the 5-6 in bin" ($sm.GapHist.ContainsKey(5) -and $sm.GapSummary() -match "contact")
# the cases that already connect still connect: a beam end on a column side, ends meeting
$sm = ModelMode @((M "C" 0 0 0 0 0 144), (M "B" 0 0 120 120 0 120)) "contact"; $e = Ends $sm "B"
Check "13f contact: a beam on a column still connects" ($e.I -ne "" -and $e.I -notlike "open-*")

# 14 members are never moved (2026-11-12): the joint may sit elsewhere, the member end stays, with a joint offset
function Fr($sm, $oid) { @($sm.Frames | Where-Object { $sm.Members[$_.Source].MemberOid -eq $oid }) }
function V3($p, $x, $y, $z) { [math]::Abs($p[0] - $x) -lt 1e-6 -and [math]::Abs($p[1] - $y) -lt 1e-6 -and [math]::Abs($p[2] - $z) -lt 1e-6 }
$sm = Model @((M "COL" 0 0 0 0 0 240), (M "BM" 3 0 120 120 0 120))
$bf = (Fr $sm "BM")[0]
Check "14a beam into a column: joint on the column line, the beam end where it was (offset 3 in)" ((V3 $sm.JointPos($bf.I) 0 0 120) -and (V3 $sm.EndPos($bf, $true) 3 0 120) -and (V3 $bf.OffI 3 0 0) -and (V3 $bf.OffJ 0 0 0))
Check "14a the column is not moved and has no offsets" (@(Fr $sm "COL" | Where-Object { [SteelModel]::OffLen($_.OffI) -gt 0 -or [SteelModel]::OffLen($_.OffJ) -gt 0 }).Count -eq 0 -and (V3 $sm.EndPos((Fr $sm "COL")[0], $true) 0 0 0) -and (V3 $sm.EndPos((Fr $sm "COL")[-1], $false) 0 0 240))
Check "14a summary counts the offset end" ($sm.Summary() -like "*members not moved: 1 ends offset from their joint (largest 3 in); 0 ends left open*")
Check "14a top of steel follows the real line" ([math]::Abs($sm.TopAt($bf, 0)[0] - 3) -lt 1e-6)
$tmp14 = Join-Path ([IO.Path]::GetTempPath()) ("pluto-steel-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
$sm.WriteCsv($tmp14)
$row = @(Import-Csv "$tmp14.steel_frames.csv" | Where-Object { $_.MemberOid -eq "BM" })[0]
Check "14a steel_frames.csv: joint coordinates and the joint offset columns" ($row.XI_in -eq "0" -and $row.OffIX_in -eq "3" -and $row.OffIY_in -eq "0" -and $row.OffJX_in -eq "0")
Remove-Item "$tmp14.steel_frames.csv", "$tmp14.steel_joints.csv" -ErrorAction SilentlyContinue
# a split (duct support) on a member with an end offset: along the real line; the offset stays with its end
$tt = 0.0; $dd = 0.0
$nf = $sm.NearestFrame([double[]]@(60, 0, 125), [ref]$tt, [ref]$dd)
$j = $sm.SplitAt($nf, $tt, "SUPPORT")
$parts = Fr $sm "BM"
Check "14b split on the real line; the first part keeps the end offset, the new joint has none" ($nf.Source -eq $bf.Source -and [math]::Abs($j.X - 60) -lt 1e-6 -and $parts.Count -eq 2 -and (V3 $parts[0].OffI 3 0 0) -and (V3 $parts[0].OffJ 0 0 0) -and (V3 $parts[1].OffI 0 0 0))
# two ends meeting 2 in apart still join; the second keeps its position
$sm = Model @((M "A" 0 0 120 120 0 120), (M "B" 120 2 120 120 120 120))
$fa = (Fr $sm "A")[0]; $fb = (Fr $sm "B")[0]
Check "14c corner 2 in apart: one joint, both members where they were" ($fa.J -eq $fb.I -and (V3 $sm.EndPos($fa, $false) 120 0 120) -and (V3 $sm.EndPos($fb, $true) 120 2 120) -and ([SteelModel]::OffLen($fa.OffJ) + [SteelModel]::OffLen($fb.OffI)) -eq 2)
# a column missing from the data: two beams cut back to it, 5 in apart in line. Not tied to each other: open, flagged
$sm = Model @((M "A" 0 0 120 120 0 120), (M "B" 125 0 120 245 0 120))
$a = Ends $sm "A"; $b = Ends $sm "B"
Check "14d in line, 5 in apart: both ends open and OPEN_NEAR, not joined" ($a.J -like "open-*" -and $b.I -like "open-*" -and $a.JJ.Id -ne $b.JI.Id -and ($a.JJ.Flags -contains "OPEN_NEAR") -and $sm.EndGapSkipped -eq 2)
Check "14d nothing moved, no offsets" ((V3 $sm.EndPos((Fr $sm "A")[0], $false) 120 0 120) -and (V3 $sm.EndPos((Fr $sm "B")[0], $true) 125 0 120) -and $sm.Summary() -like "*members not moved: 0 ends offset from their joint (largest 0 in); 2 ends left open, the other member's end over 2 in away")
# the same at a corner: beams from two sides of the missing column, ends 5.7 in apart
$sm = Model @((M "A" 0 0 120 116 0 120), (M "B" 120 4 120 120 124 120))
Check "14e square to each other, 5.7 in apart: open, not joined" ((Ends $sm "A").J -like "open-*" -and (Ends $sm "B").I -like "open-*")
# with the column there, both beams connect to it and neither is moved
$sm = Model @((M "COL" 120 0 0 120 0 240), (M "A" 0 0 120 116 0 120), (M "B" 120 4 120 120 124 120))
Check "14f with the column: both framed into it, ends kept" ((Ends $sm "A").J -eq "framed" -and (Ends $sm "B").I -eq "framed" -and (V3 $sm.EndPos((Fr $sm "A")[-1], $false) 116 0 120) -and (V3 $sm.EndPos((Fr $sm "B")[0], $true) 120 4 120))
# EndJoinIn is a setting
$l = New-Object 'System.Collections.Generic.List[SteelMember]'; $l.Add((M "A" 0 0 120 120 0 120)); $l.Add((M "B" 125 0 120 245 0 120))
$st = New-Object 'SteelModel+Settings'; $st.EndJoinIn = 6
$sm = [SteelModel]::Build($l, $st)
Check "14g EndJoinIn 6: the 5 in gap joins (one joint), B keeps its position by an offset" ((Ends $sm "A").JJ.Id -eq (Ends $sm "B").JI.Id -and (V3 $sm.EndPos((Fr $sm "B")[0], $true) 125 0 120))

# 15 slab filter (2026-11-12): a vertical open end is fixed only in or near a slab; it only removes restraints
function ModelSlabs([object[]]$ms, $slabs, $near = 12) {
    $l = New-Object 'System.Collections.Generic.List[SteelMember]'; foreach ($m in $ms) { $l.Add($m) }
    $s = New-Object 'SteelModel+Settings'; $s.SlabNearIn = $near
    if ($null -ne $slabs) { $s.Slabs = New-Object 'System.Collections.Generic.List[double[]]'; foreach ($b in $slabs) { $s.Slabs.Add([double[]]$b) } }
    [SteelModel]::Build($l, $s)
}
$ceiling = ,@(-100, -100, 240, 100, 100, 252)     # a slab over the hanger's top
$sm = ModelSlabs @((M "H" 0 0 120 0 0 240)) $ceiling; $e = Ends $sm "H"
Check "15a hanger: top at the slab stays fixed, bottom in the air is not fixed" ($e.J -eq "open-fixed" -and $e.JJ.Restraint -eq "FIXED" -and $e.I -eq "open-free" -and $e.JI.Restraint -eq "" -and ($e.JI.Flags -contains "NO_SLAB") -and -not ($e.JJ.Flags -contains "NO_SLAB"))
Check "15a counted, and in the summary" ($sm.FixedKept -eq 1 -and $sm.FixedRemoved -eq 1 -and $sm.Summary() -like "*; slab filter: 1 fixed ends kept (in or within 12 in of a slab), 1 not fixed (no slab near: open-free, NO_SLAB)")
Check "15a the free end has no end marker" ($null -eq [SteelModel]::EndGroup($e.I, $e.RI, $false))
$floor = ,@(-100, -100, 108, 100, 100, 120)
$e = Ends (ModelSlabs @((M "P" 0 0 120 0 0 240)) $floor) "P"
Check "15b a post on a floor: the bottom stays fixed, the top is free (floor or ceiling alike)" ($e.I -eq "open-fixed" -and $e.J -eq "open-free")
$e = Ends (ModelSlabs @((M "H" 0 0 120 0 0 230)) $ceiling) "H"
Check "15c 10 in under the slab: within 12 in, fixed" ($e.J -eq "open-fixed")
$e = Ends (ModelSlabs @((M "H" 0 0 120 0 0 226)) $ceiling) "H"
Check "15c 14 in under the slab: not fixed" ($e.J -eq "open-free")
$e = Ends (ModelSlabs @((M "H" 0 0 120 0 0 226)) $ceiling 20) "H"
Check "15c the distance is a setting" ($e.J -eq "open-fixed")
$e = Ends (ModelSlabs @((M "H" 150 0 120 150 0 240)) $ceiling) "H"
Check "15d beside the slab in plan (50 in off its edge): not fixed" ($e.J -eq "open-free")
$sm = ModelSlabs @((M "H" 0 0 120 0 0 240)) $null; $e = Ends $sm "H"
Check "15e no slabs given: every vertical open end fixed, as before" ($e.I -eq "open-fixed" -and $e.J -eq "open-fixed" -and $sm.Summary() -notlike "*slab filter*")
$e = Ends (ModelSlabs @((M "H" 0 0 120 0 0 240)) @()) "H"
Check "15e an empty slab list: the same (no filter)" ($e.I -eq "open-fixed" -and $e.J -eq "open-fixed")
# it only removes: a horizontal open end at a slab stays pinned, a connected end stays as it is
$sm = ModelSlabs @((M "B" 0 0 240 90 0 240), (M "COL" 0 0 120 0 0 240)) $ceiling
Check "15f a horizontal open end by the slab stays pinned; nothing new is fixed" ((Ends $sm "B").J -eq "open-pinned" -and @($sm.Joints | Where-Object { $_.Restraint -eq "FIXED" }).Count -eq 0)
# column base (a brace landing on the column foot): fixed only with a slab there
$base = @((M "COL" 0 0 0 0 0 240), (M "BR" 0 0 0 60 0 60))
$sm = ModelSlabs $base (,@(-100, -100, -12, 100, 100, 0)); $c = Ends $sm "COL"
Check "15g column base on a slab: FIXED column-base" ($c.JI.Restraint -eq "FIXED" -and $c.JI.Condition -eq "column-base")
$sm = ModelSlabs $base $ceiling; $c = Ends $sm "COL"; $br = Ends $sm "BR"
Check "15g column base with no slab: not fixed, the brace still hinged, the column top (at the slab) fixed" ($c.JI.Restraint -eq "" -and $c.JI.Condition -eq "open-free" -and ($c.JI.Flags -contains "NO_SLAB") -and $c.I -eq "open-free" -and $br.RI -eq "M2M3" -and $c.J -eq "open-fixed")

Write-Output ("{0} passed, {1} failed" -f $script:pass, $script:fail)
exit $(if ($script:fail) { 1 } else { 0 })
