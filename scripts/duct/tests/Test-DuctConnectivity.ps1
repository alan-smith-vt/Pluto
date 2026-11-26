# DuctConnectivity.Resolve (main or fragment from the FINAL clean connectivity: one definition) on synthetic frames.
# Windows PowerShell 5.1, a few seconds:
#   powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File scripts\duct\tests\Test-DuctConnectivity.ps1
$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'arms\Import-Arms.ps1')
$fail = 0; $pass = 0
function Check($name, $cond) { if ($cond) { $script:pass++ } else { Write-Host "FAIL $name"; $script:fail++ } }
function Edge($a, $b, $sys, [switch]$Rigid) {
    $e = New-Object CEdge; $e.A = $a; $e.B = $b; $e.Rigid = [bool]$Rigid; $e.Dim = [double[]]@(0, 24, 12, [double]::NaN)
    if ($Rigid) { $e.SysName = $sys } else { $el = New-Object RvEl; $el.Id = "R$a"; $el.SysName = $sys; $e.Src.Add($el) }
    return $e
}
function Pts($list) { $d = New-Object 'System.Collections.Generic.Dictionary[int,double[]]'; foreach ($q in $list) { $d[[int]$q[0]] = [double[]]@($q[1], $q[2], $q[3]) }; return ,$d }
function SysOf($c, $a) { [DuctGeom]::SystemOf(($c | Where-Object { $_.A -eq $a } | Select-Object -First 1)) }
$ds = New-Object DuctSettings; $ds.PrimarySystems.Add("SYSA"); $sys = New-Object DuctSystems $ds

# main SYSA 1-2-3 (+ a tap link 2-4); a branch named SYSA 10-11 NOT connected (0.4 in off the main's side);
# a piece named "SYSA fragments" 3-6 that IS connected to the main at joint 3; a fragment 20-21 alone; EXH 30-31
$P = Pts @(@(1, 0, 0, 0), @(2, 10, 0, 0), @(3, 20, 0, 0), @(4, 10, 1, 0), @(6, 25, 0, 0),
           @(10, 15, (0.4 / 12), 0), @(11, 15, 8, 0), @(20, 40, 0, 0), @(21, 50, 0, 0), @(30, 0, 30, 0), @(31, 5, 30, 0))
$c = New-Object 'System.Collections.Generic.List[CEdge]'
$c.Add((Edge 1 2 "SYSA")); $c.Add((Edge 2 3 "SYSA")); $c.Add((Edge 2 4 "SYSA" -Rigid)); $c.Add((Edge 3 6 "SYSA fragments"))
$c.Add((Edge 10 11 "SYSA")); $c.Add((Edge 20 21 "SYSA fragments")); $c.Add((Edge 30 31 "other: EXH"))
$note = [DuctConnectivity]::Resolve($c, $P, $sys)
Check "main stays main; the connected 'fragment' joins it (FRAGMENT_JOINED)" ((SysOf $c 1) -eq "SYSA" -and (SysOf $c 3) -eq "SYSA" -and ($c | Where-Object { $_.A -eq 3 }).Flags.Contains("FRAGMENT_JOINED"))
Check "the unconnected branch named SYSA is a fragment now (SPLIT_FROM_MAIN)" ((SysOf $c 10) -eq "SYSA fragments" -and ($c | Where-Object { $_.A -eq 10 }).Flags.Contains("SPLIT_FROM_MAIN"))
Check "a lone fragment stays a fragment, unflagged; the other system untouched" ((SysOf $c 20) -eq "SYSA fragments" -and ($c | Where-Object { $_.A -eq 20 }).Flags.Count -eq 0 -and (SysOf $c 30) -eq "other: EXH")
Check "no DISCONNECTED_FROM_MAIN anywhere (one definition)" (@($c | Where-Object { $_.Flags.Contains("DISCONNECTED_FROM_MAIN") }).Count -eq 0)
Check "note: 2 fragment pieces, 1 joined, 1 split with its gap" ($note -like "fragments (final connectivity, the model SAP gets): 2 pieces are fragments; 1 elements named a fragment*joined it (FRAGMENT_JOINED: SYSA 1); 1 elements named the main*" -and $note -like "*SYSA: 1 elements (8 ft) not connected to the main, now SYSA fragments: 0.4 in from it at joint N10*")

# the biggest piece is the main even if the naming called another the main (counts elements of SYSA and SYSA fragments)
$P = Pts @(@(1, 0, 0, 0), @(2, 10, 0, 0), @(3, 20, 0, 0), @(4, 30, 0, 0), @(10, 0, 50, 0), @(11, 10, 50, 0))
$c = New-Object 'System.Collections.Generic.List[CEdge]'
$c.Add((Edge 1 2 "SYSA fragments")); $c.Add((Edge 2 3 "SYSA fragments")); $c.Add((Edge 3 4 "SYSA fragments")); $c.Add((Edge 10 11 "SYSA"))
$null = [DuctConnectivity]::Resolve($c, $P, $sys)
Check "largest piece = main: 3 fragments become SYSA, the lone SYSA element becomes a fragment" ((SysOf $c 1) -eq "SYSA" -and (SysOf $c 3) -eq "SYSA" -and (SysOf $c 10) -eq "SYSA fragments")

Write-Host ("{0} passed, {1} failed" -f $pass, $fail)
exit $(if ($fail) { 1 } else { 0 })
