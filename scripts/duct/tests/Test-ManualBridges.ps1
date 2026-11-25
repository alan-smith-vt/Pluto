# ManualBridges (hand-picked bridges, scripts\duct\model\ManualBridges.cs) on synthetic clean frames, and the DuctBridges
# setting (bridges.json). Windows PowerShell 5.1, a few seconds:
#   powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File scripts\duct\tests\Test-ManualBridges.ps1
$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'arms\Import-Arms.ps1')
. (Join-Path (Split-Path $PSScriptRoot -Parent) 'Get-DuctSettings.ps1')
$fail = 0; $pass = 0
function Check($name, $cond) { if ($cond) { $script:pass++ } else { Write-Host "FAIL $name"; $script:fail++ } }
function Rect($w, $h) { return [double[]]@(0, $w, $h, [double]::NaN) }
function Edge($a, $b, $id) {
    $e = New-Object CEdge; $e.A = $a; $e.B = $b; $e.Dim = (Rect 24 12); $e.Y = [double[]]@(0, 1, 0)
    $el = New-Object RvEl; $el.Id = $id; $el.SysName = "SYSA"; $e.Src.Add($el); return $e
}
function Pts($list) { $d = New-Object 'System.Collections.Generic.Dictionary[int,double[]]'; foreach ($q in $list) { $d[[int]$q[0]] = [double[]]@($q[1], $q[2], $q[3]) }; return ,$d }
function Br($mode, $a, $b) { $m = New-Object ManualBridge; $m.Mode = $mode; $m.A.Add($a); $m.B.Add($b); return $m }
function Run($c, $P, $brs) { $l = New-Object 'System.Collections.Generic.List[ManualBridge]'; foreach ($x in $brs) { $l.Add($x) }; return [ManualBridges]::Apply($c, $P, $l) }
function Near($a, $b) { [math]::Abs($a - $b) -lt 1e-6 }

# 1 rigid: A x 0..10 ft, gap, B x 12..20 (in line); a rigid link 10 -> 12
$P = Pts @(@(1, 0, 0, 0), @(2, 10, 0, 0), @(3, 12, 0, 0), @(4, 20, 0, 0))
$c = New-Object 'System.Collections.Generic.List[CEdge]'; $c.Add((Edge 1 2 "100")); $c.Add((Edge 3 4 "200"))
$note = Run $c $P @((Br "rigid" "100" "200"))
$lk = @($c | Where-Object { $_.Rigid })
Check "1 rigid: one link between the nearest open ends" ($lk.Count -eq 1 -and (@($lk[0].A, $lk[0].B) -join ",") -eq "2,3" -and $lk[0].Source -eq "manual-bridge" -and $lk[0].Flags.Contains("MANUAL_BRIDGE") -and $lk[0].SysName -eq "SYSA")
Check "1 rigid: note" ($note -like "NEW manual bridges (DuctBridges): 1 listed, 1 rigid links, 0 extended*" -and $note -like "*rigid link 2 ft between the nearest open ends*")

# 2 extend in line: A runs on to B's end (same line): A's end joint becomes B's end, no link
$P = Pts @(@(1, 0, 0, 0), @(2, 10, 0, 0), @(3, 12, 0, 0), @(4, 20, 0, 0))
$c = New-Object 'System.Collections.Generic.List[CEdge]'; $c.Add((Edge 1 2 "100")); $c.Add((Edge 3 4 "200"))
$note = Run $c $P @((Br "extend" "100" "200"))
$a = $c | Where-Object { $_.Src[0].Id -eq "100" }
Check "2 extend in line: A ends on B's end, no rigid link" ($a.B -eq 3 -and @($c | Where-Object { $_.Rigid }).Count -eq 0 -and $a.Flags.Contains("MANUAL_BRIDGE") -and $note -like "*A extended 2 ft along its axis to B")

# 3 extend into B's side (a T): A along x up to 10, B along y at x 14 from y -5..5: A runs to x 14, B split there
$P = Pts @(@(1, 0, 0, 0), @(2, 10, 0, 0), @(3, 14, -5, 0), @(4, 14, 5, 0))
$c = New-Object 'System.Collections.Generic.List[CEdge]'; $c.Add((Edge 1 2 "100")); $c.Add((Edge 3 4 "200"))
$note = Run $c $P @((Br "extend" "100" "200"))
$a = $c | Where-Object { $_.Src[0].Id -eq "100" }; $bs = @($c | Where-Object { $_.Src[0].Id -eq "200" })
Check "3 extend into a side: A ends on B's line, B split in two there" ((Near $P[$a.B][0] 14) -and (Near $P[$a.B][1] 0) -and $bs.Count -eq 2 -and ($bs | Where-Object { $_.A -eq $a.B -or $_.B -eq $a.B }).Count -eq 2)

# 4 extend with an offset: B 6 in above A's axis: A runs to x 14, a rigid link up to B
$P = Pts @(@(1, 0, 0, 0), @(2, 10, 0, 0), @(3, 14, -5, 0.5), @(4, 14, 5, 0.5))
$c = New-Object 'System.Collections.Generic.List[CEdge]'; $c.Add((Edge 1 2 "100")); $c.Add((Edge 3 4 "200"))
$note = Run $c $P @((Br "extend" "100" "200"))
$a = $c | Where-Object { $_.Src[0].Id -eq "100" }; $lk = @($c | Where-Object { $_.Rigid })
Check "4 extend with a 6 in offset: A to x 14 on its axis, rigid link to B" ((Near $P[$a.B][0] 14) -and (Near $P[$a.B][2] 0) -and $lk.Count -eq 1 -and $note -like "*rigid link over the 6 in offset*")

# 5 extend where B is beside the middle of A (behind both ends): falls back to a rigid link, said in the note
$P = Pts @(@(1, 0, 0, 0), @(2, 10, 0, 0), @(3, 5, 5, 0), @(4, 5, 10, 0))
$c = New-Object 'System.Collections.Generic.List[CEdge]'; $c.Add((Edge 1 2 "100")); $c.Add((Edge 3 4 "200"))
$note = Run $c $P @((Br "extend" "100" "200"))
Check "5 B behind A: rigid link instead, the reason in the note" (@($c | Where-Object { $_.Rigid }).Count -eq 1 -and $note -like "*extend not possible: B is not ahead of A's end*" -and $note -like "*1 extend fell back to a rigid link*")

# 6 a side not in the model: not placed
$c = New-Object 'System.Collections.Generic.List[CEdge]'; $c.Add((Edge 1 2 "100")); $c.Add((Edge 3 4 "200"))
$note = Run $c $P @((Br "rigid" "100" "999"))
Check "6 a side not in the clean model: not placed, named" ($c.Count -eq 2 -and $note -like "*1 not placed*" -and $note -like "*B (999) not in the clean model*")

# 7 settings: bridges.json read into DuctSettings.Bridges; a wrong file refused
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("pluto-bridges-" + [Guid]::NewGuid().ToString("N").Substring(0, 8) + ".json")
'{ "kind": "pluto-duct-bridges", "version": 1, "bridges": [ { "mode": "extend", "a": { "parts": ["100", "101"], "label": "clean | a" }, "b": { "parts": ["N:abc"], "label": "clean | b" } }, { "mode": "rigid", "a": { "parts": ["1"] }, "b": { "parts": ["2"] } } ] }' | Set-Content $tmp -Encoding UTF8
$notes = New-Object 'System.Collections.Generic.List[string]'
$d = Get-DuctSettings -Data @{ DuctBridges = $tmp } -Mode Revit -Notes $notes
Check "7 DuctBridges read" ($d.Bridges.Count -eq 2 -and $d.Bridges[0].Mode -eq "extend" -and ($d.Bridges[0].A -join ",") -eq "100,101" -and $d.Bridges[0].B[0] -eq "N:abc" -and $d.Bridges[1].Mode -eq "rigid" -and ($notes -join "|") -like "*manual bridges: 2 from*")
'{ "kind": "something" }' | Set-Content $tmp -Encoding UTF8
$refused = $false; try { $null = Get-DuctSettings -Data @{ DuctBridges = $tmp } -Mode Revit -Notes $notes } catch { $refused = $_.Exception.Message -like "*not a bridges.json*" }
Check "7 a file of another kind is refused" $refused
$d = Get-DuctSettings -Data @{ DuctBridges = $tmp } -Mode Revit -Bridges none -Notes $notes
Check "7 -Bridges none = off" ($d.Bridges.Count -eq 0)
Remove-Item $tmp

Write-Host ("{0} passed, {1} failed" -f $pass, $fail)
exit $(if ($fail) { 1 } else { 0 })
