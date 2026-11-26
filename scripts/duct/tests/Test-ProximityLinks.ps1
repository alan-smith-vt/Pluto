# ProximityLinks (the clean model connects what the system naming joined: open ends within 6 in of a same-system
# duct's SURFACE) on synthetic clean frames. Windows PowerShell 5.1, a few seconds:
#   powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File scripts\duct\tests\Test-ProximityLinks.ps1
$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'arms\Import-Arms.ps1')
$fail = 0; $pass = 0
function Check($name, $cond) { if ($cond) { $script:pass++ } else { Write-Host "FAIL $name"; $script:fail++ } }
function Edge($a, $b, $sys, $w, $h, [double[]]$y = @(0, 1, 0)) {
    $e = New-Object CEdge; $e.A = $a; $e.B = $b; $e.Dim = [double[]]@(0, $w, $h, [double]::NaN); $e.Y = $y
    $el = New-Object RvEl; $el.Id = "R$a"; $el.SysName = $sys; $e.Src.Add($el); return $e
}
function Pts($list) { $d = New-Object 'System.Collections.Generic.Dictionary[int,double[]]'; foreach ($q in $list) { $d[[int]$q[0]] = [double[]]@($q[1], $q[2], $q[3]) }; return ,$d }
function Near($a, $b) { [math]::Abs($a - $b) -lt 1e-6 }

# trunk SYSA 84x28 along X (ft), width across Y: its face at y = 3.5 ft. A branch 24x12 along Y ending ON the face
# (its centreline 42 in from the trunk's): joined by a 42 in rigid link to the trunk's centreline, the trunk split
$P = Pts @(@(1, 0, 0, 0), @(2, 40, 0, 0), @(3, 20, 3.5, 0), @(4, 20, 15, 0))
$c = New-Object 'System.Collections.Generic.List[CEdge]'
$c.Add((Edge 1 2 "SYSA" 84 28)); $c.Add((Edge 3 4 "SYSA" 24 12 @(1, 0, 0)))
$note = [ProximityLinks]::Apply($c, $P)
$lk = @($c | Where-Object { $_.Rigid })
$trunk = @($c | Where-Object { -not $_.Rigid -and $_.Src[0].Id -eq "R1" -or (-not $_.Rigid -and $_.Src[0].Id -eq "R1") })
Check "1 branch on the face of a 7 ft duct: one rigid link from its end to the trunk centreline (42 in)" ($lk.Count -eq 1 -and $lk[0].A -eq 3 -and (Near $P[$lk[0].B][0] 20) -and (Near $P[$lk[0].B][1] 0) -and $lk[0].Flags.Contains("JOINED_BY_PROXIMITY") -and $lk[0].Source -eq "proximity-link")
Check "1 the trunk is split where the branch lands, nothing moved" (@($c | Where-Object { -not $_.Rigid -and $_.Src[0].Id -eq "R1" }).Count -eq 2 -and (Near $P[3][1] 3.5))
Check "1 note" ($note -like "NEW proximity joins (JOINED_BY_PROXIMITY): 1 open duct ends*1 by a rigid link (longest 42 in*1 ducts split*SYSA 1")

# 2 the branch end 8 in off the face: not joined; 5 in off: joined
foreach ($k in @(@(8, 0), @(5, 1))) {
    $P = Pts @(@(1, 0, 0, 0), @(2, 40, 0, 0), @(3, 20, (3.5 + $k[0] / 12), 0), @(4, 20, 15, 0))
    $c = New-Object 'System.Collections.Generic.List[CEdge]'
    $c.Add((Edge 1 2 "SYSA" 84 28)); $c.Add((Edge 3 4 "SYSA" 24 12 @(1, 0, 0)))
    $null = [ProximityLinks]::Apply($c, $P)
    Check ("2 end {0} in off the surface: {1} link" -f $k[0], $k[1]) (@($c | Where-Object { $_.Rigid }).Count -eq $k[1])
}

# 3 another system touching: never joined
$P = Pts @(@(1, 0, 0, 0), @(2, 40, 0, 0), @(3, 20, 3.5, 0), @(4, 20, 15, 0))
$c = New-Object 'System.Collections.Generic.List[CEdge]'
$c.Add((Edge 1 2 "SYSA" 84 28)); $c.Add((Edge 3 4 "SYSB" 24 12 @(1, 0, 0)))
$note = [ProximityLinks]::Apply($c, $P)
Check "3 a different system on the face: not joined" (@($c | Where-Object { $_.Rigid }).Count -eq 0 -and $note -like "proximity joins: no open duct end*")

# 4 end to end, 0.5 in apart, offset sideways: joined directly at the other's end (no link, nothing split)
$P = Pts @(@(1, 0, 0, 0), @(2, 10, 0, 0), @(3, (10 + 0.5 / 12), 0, 0), @(4, 20, 0, 0))
$c = New-Object 'System.Collections.Generic.List[CEdge]'
$c.Add((Edge 1 2 "SYSA" 24 12)); $c.Add((Edge 3 4 "SYSA" 24 12))
$null = [ProximityLinks]::Apply($c, $P)
Check "4 ends 0.5 in apart: joined directly, no link" (@($c | Where-Object { $_.Rigid }).Count -eq 0 -and (($c | Where-Object { $_.Src[0].Id -eq "R1" }).B -eq 3 -or ($c | Where-Object { $_.Src[0].Id -eq "R3" }).A -eq 2))

Write-Host ("{0} passed, {1} failed" -f $pass, $fail)
exit $(if ($fail) { 1 } else { 0 })
