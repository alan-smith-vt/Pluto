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

Write-Output ("{0} passed, {1} failed" -f $script:pass, $script:fail)
exit $(if ($script:fail) { 1 } else { 0 })
