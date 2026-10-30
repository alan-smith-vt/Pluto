# DuctSupports / DuctSupportShapes unit checks (synthetic cases). Windows PowerShell 5.1:
#   powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File scripts\duct\tests\Test-DuctSupports.ps1
$ErrorActionPreference = 'Stop'
$duct = Split-Path $PSScriptRoot -Parent
Add-Type -Path (Join-Path $duct 'DuctSupports.cs'), (Join-Path $duct 'DuctSupportShapes.cs') -ReferencedAssemblies System.Core
$fail = 0
function Check($name, $cond) { if ($cond) { Write-Host "ok   $name" } else { Write-Host "FAIL $name"; $script:fail++ } }
function D($ja, $jb, $a, $b, $w, $h) {
    $d = New-Object 'DuctSupports+Duct'; $d.JointA = $ja; $d.JointB = $jb; $d.A = [double[]]$a; $d.B = [double[]]$b
    $d.W = $w; $d.H = $h; $d.System = 'SYS-A'; return $d
}
function S($i, $j, $top) {
    $s = New-Object 'DuctSupports+Steel'; $s.I = [double[]]$i; $s.J = [double[]]$j; $s.TopI = $top; $s.TopJ = $top; $s.Section = 'W12X26'; return $s
}

# a 24x12 duct along X at Z 120 (bottom 114), 40 ft long; beams across it in Y
$ducts = New-Object 'System.Collections.Generic.List[DuctSupports+Duct]'
$ducts.Add((D 1 2 @(0, 0, 120) @(480, 0, 120) 24 12))
$steel = New-Object 'System.Collections.Generic.List[DuctSupports+Steel]'
$steel.Add((S @(60, -48, 106) @(60, 48, 106) 112))     # gap 2 -> support
$steel.Add((S @(200, -48, 104) @(200, 48, 104) 110))   # gap 4 -> support (at the limit)
$steel.Add((S @(300, -48, 100) @(300, 48, 100) 106))   # gap 8 -> none
$steel.Add((S @(400, -48, 112) @(400, 48, 112) 118))   # steel top 4 in inside the duct -> clash
$steel.Add((S @(100, 20, 108) @(400, 20, 108) 112))    # parallel, beside the duct (20 > 12) -> not counted
$steel.Add((S @(100, 6, 108) @(400, 6, 108) 112))     # parallel, under the duct, gap 2 -> counted as parallel
$steel.Add((S @(500, -48, 106) @(500, 48, 106) 112))   # beyond the duct end -> no crossing
$steel.Add((S @(60, -48, 160) @(60, 48, 160) 166))     # above the duct -> negative gap, clash range? no: -52, out of the duct depth
$ds = New-Object DuctSupports -ArgumentList (New-Object "DuctSupports+Settings")
$ds.Find($ducts, $steel)
Check 'two supports' ($ds.Supports.Count -eq 2)
Check 'support 1 at x 60, gap 2' ([math]::Abs($ds.Supports[0].PD[0] - 60) -lt 1e-6 -and [math]::Abs($ds.Supports[0].Gap - 2) -lt 1e-6)
Check 'support 1 steel point on the joint line' ([math]::Abs($ds.Supports[0].PS[2] - 106) -lt 1e-6 -and [math]::Abs($ds.Supports[0].PS[1]) -lt 1e-6)
Check 'support type default guide' ($ds.Supports[0].Type -eq 'guide')
Check 'one clash' ($ds.Clashes -eq 1)
Check 'parallel counted' ($ds.Parallel -eq 1)
Check 'histogram gaps (2, 4, 8, -4)' ($ds.Gaps.Count -eq 4)
Check 'histogram text' ($ds.Histogram() -match 'gap histogram')

# rotated duct: width vertical (24 tall) -> half depth 12, bottom 108; the 112 beam then clashes
$rot = New-Object 'System.Collections.Generic.List[DuctSupports+Duct]'
$r = (D 1 2 @(0, 0, 120) @(480, 0, 120) 24 12); $r.WidthDir = [double[]]@(0, 0, 1); $rot.Add($r)
$ds2 = New-Object DuctSupports -ArgumentList (New-Object "DuctSupports+Settings"); $ds2.Find($rot, $steel)
Check 'rotated duct: half depth from width (only the 8 in gap beam supports)' ($ds2.Supports.Count -eq 1 -and [math]::Abs($ds2.Supports[0].PD[0] - 300) -lt 1e-6 -and $ds2.Clashes -eq 3)

# vertical duct: never supported
$v = New-Object 'System.Collections.Generic.List[DuctSupports+Duct]'
$v.Add((D 1 2 @(60, 0, 120) @(60, 0, 240) 24 12))
$ds3 = New-Object DuctSupports -ArgumentList (New-Object "DuctSupports+Settings"); $ds3.Find($v, $steel)
Check 'vertical duct: no supports' ($ds3.Supports.Count -eq 0)

# spans: chain 1-2-3-4-5 (each 60 in) with a support joint at 3; a vertical riser 5-6; a 20 in rigid tap 2-7 + branch 7-8 (60 in)
$c = New-Object 'System.Collections.Generic.List[DuctSupports+Duct]'
$c.Add((D 1 2 @(0, 0, 0) @(60, 0, 0) 24 12)); $c.Add((D 2 3 @(60, 0, 0) @(120, 0, 0) 24 12))
$c.Add((D 3 4 @(120, 0, 0) @(180, 0, 0) 24 12)); $c.Add((D 4 5 @(180, 0, 0) @(240, 0, 0) 24 12))
$c.Add((D 5 6 @(240, 0, 0) @(240, 0, 200) 24 12))
$t = (D 2 7 @(60, 0, 0) @(60, 20, 0) 0 0); $t.Rigid = $true; $c.Add($t)
$c.Add((D 7 8 @(60, 20, 0) @(60, 80, 0) 12 12))
$sj = New-Object 'System.Collections.Generic.HashSet[int]'; [void]$sj.Add(3)
$sp = $ds.Spans($c, $sj)
# left stretch: 60 + 60 + 20 + 60 = 200 in > 120 -> flagged; right stretch 120 in = 10 ft -> not (> only)
Check 'one span over 10 ft' ($sp.Count -eq 1)
Check 'span length 200 in, bounded by 1 support' ($sp.Count -eq 1 -and [math]::Abs($sp[0].LengthIn - 200) -lt 1e-6 -and $sp[0].Supports -eq 1)
Check 'span holds frames 0,1,5,6' ($sp.Count -eq 1 -and (($sp[0].Ducts | Sort-Object) -join ',') -eq '0,1,5,6')

# guide shape: 8 triangles, all within the box around the duct
$set = New-Object 'DuctSupportShapes+Settings'
$tri = [DuctSupportShapes]::Guide([double[]]@(0, 0, 120), [double[]]@(1, 0, 0), [double[]]@(0, 1, 0), 24, 12, 8, $set)
Check 'guide: 8 triangles' ($tri.Count -eq 8)
$zs = @(); $ys = @(); foreach ($x in $tri) { for ($k = 0; $k -lt 3; $k++) { $zs += $x[3 * $k + 2]; $ys += $x[3 * $k + 1] } }
Check 'guide: base plate on the steel top (112), top plate above the duct (126.25)' ([math]::Abs(($zs | Measure-Object -Minimum).Minimum - 112) -lt 1e-9 -and [math]::Abs(($zs | Measure-Object -Maximum).Maximum - 126.25) -lt 1e-9)
Check 'guide: ears 2 in beyond the side plates' ([math]::Abs(($ys | Measure-Object -Maximum).Maximum - 14.25) -lt 1e-9)

if ($fail -gt 0) { Write-Host "$fail FAILED"; exit 1 } else { Write-Host 'all passed' }
