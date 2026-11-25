# DuctConnectivity (same name as a main, not connected to it: DISCONNECTED_FROM_MAIN) on synthetic clean frames.
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
$ds = New-Object DuctSettings; $ds.PrimarySystems.Add("SYSA"); $sys = New-Object DuctSystems $ds

# main SYSA 1-2-3 along X (ft) with a tap link 2-4; an offshoot SYSA 10-11 along Y whose end sits 0.4 in off the
# main's side (never joined); a fragment piece 20-21 (named fragments: not flagged); EXH 30-31 (other: not a main)
$P = Pts @(@(1, 0, 0, 0), @(2, 10, 0, 0), @(3, 20, 0, 0), @(4, 10, 1, 0), @(10, 15, (0.4 / 12), 0), @(11, 15, 8, 0),
           @(20, 40, 0, 0), @(21, 50, 0, 0), @(30, 0, 30, 0), @(31, 5, 30, 0))
$c = New-Object 'System.Collections.Generic.List[CEdge]'
$c.Add((Edge 1 2 "SYSA")); $c.Add((Edge 2 3 "SYSA")); $c.Add((Edge 2 4 "SYSA" -Rigid))
$c.Add((Edge 10 11 "SYSA")); $c.Add((Edge 20 21 "SYSA fragments")); $c.Add((Edge 30 31 "other: EXH"))
$note = [DuctConnectivity]::Apply($c, $P, $sys)
$flagged = @($c | Where-Object { $_.Flags.Contains("DISCONNECTED_FROM_MAIN") })
Check "only the SYSA offshoot is flagged (not the main, the fragment, the other system)" ($flagged.Count -eq 1 -and $flagged[0].A -eq 10)
Check "note: one piece, its gap to the main (0.4 in at N10)" ($note -like "NEW disconnected from main (DISCONNECTED_FROM_MAIN): 1 elements in 1 pieces*" -and $note -like "*SYSA: 1 elements (8 ft), 0.4 in from the main at its joint N10*")

# joined (the offshoot shares joint 2 with the main): nothing flagged
$c = New-Object 'System.Collections.Generic.List[CEdge]'
$c.Add((Edge 1 2 "SYSA")); $c.Add((Edge 2 3 "SYSA")); $c.Add((Edge 2 11 "SYSA"))
$note = [DuctConnectivity]::Apply($c, $P, $sys)
Check "connected: nothing flagged" (@($c | Where-Object { $_.Flags.Contains("DISCONNECTED_FROM_MAIN") }).Count -eq 0 -and $note -like "connectivity: every main is one connected piece*")

Write-Host ("{0} passed, {1} failed" -f $pass, $fail)
exit $(if ($fail) { 1 } else { 0 })
