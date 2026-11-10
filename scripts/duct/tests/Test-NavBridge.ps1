# NavBridge (gap bridging, scripts\duct\model\NavBridge.cs) on synthetic clean frames. Windows PowerShell 5.1:
#   powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File scripts\duct\tests\Test-NavBridge.ps1
$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'arms\Import-Arms.ps1')
$fail = 0
function Check($name, $cond) { if ($cond) { Write-Host "ok   $name" } else { Write-Host "FAIL $name"; $script:fail++ } }
function Rect($w, $h) { return [double[]]@(0, $w, $h, [double]::NaN) }
function RevitEdge($a, $b, $dim, $sys) {
    $e = New-Object CEdge; $e.A = $a; $e.B = $b; $e.Dim = $dim; $e.Y = [double[]]@(0, 1, 0)
    $el = New-Object RvEl; $el.Id = "R$a"; $el.SysName = $sys; $e.Src.Add($el); return $e
}
function NavEdge($a, $b, $dim) {
    $e = New-Object CEdge; $e.A = $a; $e.B = $b; $e.Dim = $dim; $e.Source = 'navisworks-only'; $e.NavisId = "N$a"; $e.Y = [double[]]@(0, 0, 1)
    [void]$e.Flags.Add('NAVIS_ONLY'); return $e
}
function Pts($list) { $d = New-Object 'System.Collections.Generic.Dictionary[int,double[]]'; foreach ($q in $list) { $d[[int]$q[0]] = [double[]]@($q[1], $q[2], $q[3]) }; return ,$d }

# 1. two-sided, same size: Revit 1-2 (x 0..10 ft), gap, Navisworks 3-4 (x 10.5..19.5, 6 in off each end), Revit 5-6 (x 20..30)
$P = Pts @(@(1, 0, 0, 0), @(2, 10, 0, 0), @(3, 10.5, 0, 0), @(4, 19.5, 0, 0), @(5, 20, 0, 0), @(6, 30, 0, 0))
$c = New-Object 'System.Collections.Generic.List[CEdge]'
$c.Add((RevitEdge 1 2 (Rect 24 12) 'SYSA')); $nav = (NavEdge 3 4 (Rect 26 12)); $c.Add($nav); $c.Add((RevitEdge 5 6 (Rect 24 12) 'SYSA'))
$nl = $null; $note = [NavBridge]::Apply($c, $P, [ref]$nl)
Check 'two-sided: joined to both Revit ends' ($nav.A -eq 2 -and $nav.B -eq 5)
Check 'two-sided: neighbours size, no transition' ($nav.Dim[1] -eq 24 -and $nav.Dim[2] -eq 12 -and $nav.DimB -eq $null)
Check 'two-sided: flags NAVIS_BRIDGE + SIZE_FROM_NEIGHBOURS' ($nav.Flags.Contains('NAVIS_BRIDGE') -and $nav.Flags.Contains('SIZE_FROM_NEIGHBOURS'))
Check 'two-sided: system and width direction from the neighbour' ($nav.SysName -eq 'SYSA' -and $nav.Y[1] -eq 1 -and [DuctGeom]::SystemOf($nav) -eq 'SYSA')
Check 'two-sided: source navisworks-bridge' ($nav.Source -eq 'navisworks-bridge')
Check 'NEW line counts' ($nl -like 'NEW bridges: 1 Navisworks-only part*1 between two ends, 0 at one end; resized from the neighbours 1*')

# 2. two-sided, different sizes: a transition; stated size equal to one side -> still SIZE_FROM_NEIGHBOURS (it changes)
$P = Pts @(@(1, 0, 0, 0), @(2, 10, 0, 0), @(3, 10.5, 0, 0), @(4, 19.5, 0, 0), @(5, 20, 0, 0), @(6, 30, 0, 0))
$c = New-Object 'System.Collections.Generic.List[CEdge]'
$c.Add((RevitEdge 1 2 (Rect 24 12) 'SYSA')); $nav = (NavEdge 3 4 (Rect 24 12)); $c.Add($nav); $c.Add((RevitEdge 5 6 (Rect 18 12) 'SYSA'))
$note = [NavBridge]::Apply($c, $P, [ref]$nl)
Check 'different sides: transition 24x12 -> 18x12' ($nav.Dim[1] -eq 24 -and $nav.DimB -ne $null -and $nav.DimB[1] -eq 18)

# 3. one-sided: Revit only before it
$P = Pts @(@(1, 0, 0, 0), @(2, 10, 0, 0), @(3, 10.5, 0, 0), @(4, 19.5, 0, 0))
$c = New-Object 'System.Collections.Generic.List[CEdge]'
$c.Add((RevitEdge 1 2 (Rect 24 12) 'SYSB')); $nav = (NavEdge 3 4 (Rect 24 12)); $c.Add($nav)
$note = [NavBridge]::Apply($c, $P, [ref]$nl)
Check 'one-sided: joined at A, B free' ($nav.A -eq 2 -and $nav.B -eq 4)
Check 'one-sided: the Revit side size, stated equal -> no SIZE_FROM_NEIGHBOURS' ($nav.Dim[1] -eq 24 -and -not $nav.Flags.Contains('SIZE_FROM_NEIGHBOURS'))

# 4. no bridge: 3 in sideways, too far along (2 ft), or a Revit end pointing away
foreach ($case in @(@('sideways 3 in', 0.25, 0.5), @('2 ft along', 0, 2))) {
    $P = Pts @(@(1, 0, 0, 0), @(2, 10, 0, 0), @(3, (10 + $case[2]), $case[1], 0), @(4, 19.5, $case[1], 0))
    $c = New-Object 'System.Collections.Generic.List[CEdge]'
    $c.Add((RevitEdge 1 2 (Rect 24 12) 'SYSA')); $nav = (NavEdge 3 4 (Rect 24 12)); $c.Add($nav)
    $note = [NavBridge]::Apply($c, $P, [ref]$nl)
    Check ("no bridge: " + $case[0]) ($nav.Source -eq 'navisworks-only' -and $nav.A -eq 3 -and $nl -eq '')
}
$P = Pts @(@(1, 10.4, 0, 0), @(2, 0.4, 0, 0), @(3, 10.5, 0, 0), @(4, 19.5, 0, 0))   # Revit 1-2 points away (its open end 2 is at x 0.4)
$c = New-Object 'System.Collections.Generic.List[CEdge]'
$e = (RevitEdge 1 2 (Rect 24 12) 'SYSA'); $c.Add($e); $nav = (NavEdge 3 4 (Rect 24 12)); $c.Add($nav)
$c.Add((RevitEdge 9 1 (Rect 24 12) 'SYSA')); $P[9] = [double[]]@(10.4, 5, 0)   # end 1 is connected: not open
$note = [NavBridge]::Apply($c, $P, [ref]$nl)
Check 'no bridge: the only open Revit end faces away' ($nav.Source -eq 'navisworks-only')

if ($fail -gt 0) { Write-Host "$fail FAILED"; exit 1 } else { Write-Host 'all passed' }
