# Tests for the walls in the clean model (duct\model\WallViewWriter.cs). Windows PowerShell 5.1, a few seconds:
#   powershell -ExecutionPolicy Bypass -File scripts\duct\tests\Test-DuctWalls.ps1
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "..\..\arms\Import-Arms.ps1")
. (Join-Path $PSScriptRoot "..\Get-DuctSettings.ps1")
$script:fail = 0; $script:pass = 0
function Check($name, $cond) { if ($cond) { $script:pass++ } else { $script:fail++; Write-Output "FAIL: $name" } }
function Near($a, $b) { [math]::Abs($a - $b) -lt 1e-3 }
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("pluto-walls-" + [Guid]::NewGuid().ToString("N").Substring(0, 8) + ".csv")

# the Export-Walls.ps1 CSV as Export-Csv writes it (quoted); meters
@('"WallOid","Xmin","Xmax","Ymin","Ymax","Zmin","Zmax","Shape","Thk_m","Axis"',
  '"w1","0","10","0","0.3","0","4","wall","0.3","X"',
  '"w2","0","0.3","0","8","0","4","wall","0.3","Y"',
  '"s1","0","10","0","8","3.8","4.1","slab","0.3","X"',
  '"k1","20","25","20","25","0","4","skew","5","X"',
  '"m1","3","3.5","3","3.5","0","1","small","0.5","X"',
  '"far","500","510","0","0.3","0","4","wall","0.3","X"',
  '"flat","1","1","0","5","0","4","wall","0","Y"') | Set-Content $tmp -Encoding ASCII

$walls = [WallViewWriter]::Read($tmp)
Check "1 rows read" ($walls.Count -eq 7)
Check "2 shapes from the CSV" ((@($walls | ForEach-Object { $_.Shape }) -join " ") -eq "wall wall slab skew small wall wall")
Check "3 extents in meters" ((Near $walls[0].Hi[0] 10) -and (Near $walls[0].Hi[1] 0.3) -and (Near $walls[2].Lo[2] 3.8))

# no Shape column: the Export-Walls rule
$tmp2 = $tmp + ".noshape.csv"
@("Xmin,Xmax,Ymin,Ymax,Zmin,Zmax", "0,10,0,0.3,0,4", "0,10,0,8,3.8,4.1", "20,25,20,25,0,4", "3,3.5,3,3.5,0,1") | Set-Content $tmp2 -Encoding ASCII
Check "4 shape computed without a Shape column" ((@([WallViewWriter]::Read($tmp2) | ForEach-Object { $_.Shape }) -join " ") -eq "wall slab skew small")
$threw = $false; Set-Content $tmp2 "Xmin,Xmax,Ymin,Ymax,Zmin`n0,1,0,1,0" -Encoding ASCII; try { $null = [WallViewWriter]::Read($tmp2) } catch { $threw = $_.Exception.Message -like "*no column Zmax*" }
Check "5 a missing column is named" $threw

# Add: model box = two nodes (feet) around the origin; margin 10 ft keeps the near boxes, drops 'far' (1640 ft away)
function Model {
    $nodes = New-Object 'System.Collections.Generic.Dictionary[int,Node]'; $next = 1
    $off = [double[]](0, 0, 0)
    $null = [DuctGeom]::MakeNode($nodes, [ref]$next, [double[]](0, 0, 0), $off, 12)
    $null = [DuctGeom]::MakeNode($nodes, [ref]$next, [double[]](30, 25, 12), $off, 12)
    return @{ Nodes = $nodes; Next = $next; Off = $off }
}
$m = Model; $next = $m.Next
$v = [WallViewWriter]::Add($walls, 10, $m.Nodes, [ref]$next, $m.Off, 12)
Check "6 near boxes drawn, far and flat left out" ($v.Total -eq 7 -and $v.Drawn -eq 4)
Check "7 six quads and eight nodes per box" ($v.Elements.Count -eq 24 -and $m.Nodes.Count -eq 2 + 32)
Check "8 groups by shape" ($v.ByShape["wall"].Count -eq 12 -and $v.ByShape["slab"].Count -eq 6 -and $v.ByShape["small"].Count -eq 6 -and -not $v.ByShape.ContainsKey("skew"))
Check "9 note" ($v.Note -eq "walls: 4 of 7 drawn (within 10 ft of the model): wall 2, slab 1, skew 0, small 1; 1 with a zero extent left out")
$e = $v.Elements[1]
Check "10 quad of four nodes" ($e.nNodes -eq 4 -and @($e.n | Where-Object { $_ }).Count -eq 4)
# first box 0..10 m x 0..0.3 m x 0..4 m -> inches (scale 12 per ft)
$xs = @($v.Elements.Values | Select-Object -First 6 | ForEach-Object { $_.n } | ForEach-Object { $_.xyz.X })
$zs = @($v.Elements.Values | Select-Object -First 6 | ForEach-Object { $_.n } | ForEach-Object { $_.xyz.Z })
Check "11 box corners in file units" ((Near ($xs | Measure-Object -Maximum).Maximum (10 / 0.3048 * 12)) -and (Near ($zs | Measure-Object -Maximum).Maximum (4 / 0.3048 * 12)) -and (Near ($xs | Measure-Object -Minimum).Minimum 0))
Check "12 label" ($v.Labels[1] -like "wall | wall | thk 11.8 in | 32.8 x 1 x 13.1 ft | w1")
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

$m = Model; $next = $m.Next
$v = [WallViewWriter]::Add($walls, -1, $m.Nodes, [ref]$next, $m.Off, 12)
Check "14 negative margin: every wall with a volume" ($v.Drawn -eq 6 -and $v.Note -like "walls: 6 of 7 drawn (every wall)*skew 1*")
$m = Model; $next = $m.Next
Check "15 no walls: null" ($null -eq [WallViewWriter]::Add((New-Object 'System.Collections.Generic.List[WallBox]'), 10, $m.Nodes, [ref]$next, $m.Off, 12))

# groups
$sc = New-Object FeaturesSidecar
$m = Model; $next = $m.Next
[WallViewWriter]::AddGroups($sc, [WallViewWriter]::Add($walls, -1, $m.Nodes, [ref]$next, $m.Off, 12))
Check "16 four shell groups, painted" ((@($sc.Groups | ForEach-Object { $_.Name }) -join "|") -eq "Walls: wall|Walls: slab|Walls: skew|Walls: small" -and
    @($sc.Groups | Where-Object { $_.Members[0].Domain -ne "shells" -or $_.Hidden -ne $false }).Count -eq 0)

# settings: DuctWalls, DuctWallMarginFt, -Walls none, missing file
$notes = New-Object 'System.Collections.Generic.List[string]'
$d = Get-DuctSettings -Data @{ DuctWalls = $tmp; DuctWallMarginFt = 25 } -Mode Revit -Notes $notes
Check "17 DuctWalls read into the settings" ($d.Walls.Count -eq 7 -and $d.WallMarginFt -eq 25 -and $notes[0] -like "walls: 7 boxes from *(wall 4, slab 1, skew 1, small 1)")
$d = Get-DuctSettings -Data @{ DuctWalls = $tmp } -Mode Revit -Walls none -Notes $notes
Check "18 -Walls none = off; default margin 10" ($d.Walls.Count -eq 0 -and $d.WallMarginFt -eq 10)
$threw = $false; try { $null = Get-DuctSettings -Data @{ DuctWalls = "$tmp.nope" } -Mode Revit -Notes $notes } catch { $threw = $_.Exception.Message -like "walls CSV not found:*DuctWalls*" }
Check "19 a missing walls CSV names DuctWalls" $threw

Remove-Item $tmp, $tmp2 -ErrorAction SilentlyContinue
Write-Output ("{0} passed, {1} failed" -f $script:pass, $script:fail)
if ($script:fail) { exit 1 }
