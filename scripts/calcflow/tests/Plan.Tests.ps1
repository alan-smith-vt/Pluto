# Plan.Tests.ps1 -- the package loader, the connection register and the plan dry run, on a fixture project: the
# frame-demo .anl's package, the example calc library on the generic templates, and a register that gives five member
# ends the three example types (two column bases as one group, two beam ends into columns, a brace with a step
# override). Variants of the library and the register show that every gap is named.

$dir = New-TestDir 'plan'
$example = Join-Path (Join-Path $script:CalcFlow 'examples') 'library'
$templates = Join-Path $script:Fixtures 'templates'
$exe = (Get-Process -Id $PID).Path
$cfps = Join-Path $script:CalcFlow 'CalcFlow.ps1'

$pkgDir = Join-Path $dir 'package'
[void][FramePackageWriter]::Write([AnlFrameReader]::Read((Join-Path $script:Fixtures 'frame-demo.anl')), $pkgDir)
$pkg = [CalcFlow.AnalysisPackage]::Load($pkgDir)
$seed = Join-Path $dir 'seed.xlsx'
[CalcFlow.Register]::WriteNew($seed, $pkg, [CalcFlow.Library]::Load($example))

$assign = @{
    'J1-M1A' = @{ Type = 'BP-1'; Group = 'G-BP' }
    'J4-M3B' = @{ Type = 'BP-1'; Group = 'G-BP' }
    'J2-M2A' = @{ Type = 'BC-WELD' }
    'J3-M2B' = @{ Type = 'BC-WELD' }
    'J1-M4A' = @{ Type = 'BRC-GUSSET' }
}

function New-Dict { return (New-Object 'System.Collections.Generic.Dictionary[string,string]' ([StringComparer]::OrdinalIgnoreCase)) }

# a register from the seed: rows assigned, overrides, combos in scope (LRFD); $Edit (a scriptblock on the rows) and
# $Drop (connections to leave out) make the variants
function New-Register([string]$Name, [hashtable]$Assign, [scriptblock]$Edit, [string[]]$Drop) {
    $wb = [CalcFlow.Xlsx]::Read($seed)
    $conns = New-Object 'System.Collections.Generic.List[System.Collections.Generic.Dictionary[string,string]]'
    foreach ($r in $wb.TableRows('tblConnections')) {
        [void]$r.Remove('__row')
        if ($Drop -contains $r['Connection']) { continue }
        $a = $Assign[$r['Connection']]
        if ($a) { foreach ($k in $a.Keys) { $r[$k] = $a[$k] } }
        $conns.Add($r)
    }
    $combos = $wb.TableRows('tblCombos'); foreach ($r in $combos) { [void]$r.Remove('__row'); if ($r['Use'] -eq 'Y') { $r['Basis'] = 'LRFD' } }
    $settings = $wb.TableRows('tblSettings'); foreach ($r in $settings) { [void]$r.Remove('__row') }
    $ov = New-Object 'System.Collections.Generic.List[System.Collections.Generic.Dictionary[string,string]]'
    $o = New-Dict; $o['Connection'] = 'J1-M4A'; $o['Step'] = '2'; $o['Param'] = 'wleg'; $o['Value'] = '0.375'; $o['Note'] = 'heavier gusset weld'; $ov.Add($o)
    if ($Edit) { & $Edit $conns $ov $combos }
    $p = Join-Path $dir "$Name.xlsx"
    [CalcFlow.Register]::WriteTables($p, $conns, $ov, $combos, $settings, $null)
    return $p
}

# a copy of the example library with text replaced in its CSV files (file -> @(old, new) pairs)
function New-LibraryVariant([string]$Name, [hashtable]$Edits) {
    $f = Join-Path $dir $Name
    Copy-Item -LiteralPath (Join-Path $example 'text') -Destination (Join-Path $f 'text') -Recurse
    foreach ($k in $Edits.Keys) {
        $p = Join-Path (Join-Path $f 'text') $k
        $t = [IO.File]::ReadAllText($p)
        for ($i = 0; $i -lt $Edits[$k].Count; $i += 2) {
            if ($t.IndexOf($Edits[$k][$i]) -lt 0) { throw "library variant ${Name}: '$($Edits[$k][$i])' is not in $k" }
            $t = $t.Replace($Edits[$k][$i], $Edits[$k][$i + 1])
        }
        [IO.File]::WriteAllText($p, $t, (New-Object Text.UTF8Encoding($true)))
    }
    return $f
}

function Invoke-Plan([string]$Library, [string]$Register) {
    return [CalcFlow.Planner]::Run($pkg, [CalcFlow.Library]::Load($Library), [CalcFlow.Register]::Load($Register), $templates)
}

function Get-Codes($res, [string]$Severity) { return @($res.Problems | Where-Object { $_.Severity -eq $Severity } | ForEach-Object { $_.Code }) }

$reg = New-Register 'register' $assign $null $null
$res = Invoke-Plan $example $reg

Test 'the package reads back: members, ends at joints, cases, end forces with their units' {
    Assert-Equal "$($pkg.Members.Count)/$($pkg.Joints.Count)/$($pkg.Ends.Count)/$($pkg.Cases.Count)" '7/7/14/5'
    Assert-Equal "$($pkg.ForceUnit)|$($pkg.MomentUnit)" 'kip|kip-ft'
    $f = $pkg.Forces(2, 'A')[3]
    Assert-Equal (($f | ForEach-Object { $_ }) -join ' ') '37.312 3.731 0.339 0.136 0 0'
    Assert-Equal $pkg.EndAt(3, 5).End 'A'
    Assert-Equal $pkg.Joints[1].Support 'FIXED'
}

Test 'register-new seeds one row per member end, guesses each Support, and puts the combinations in scope' {
    $r = [CalcFlow.Register]::Load($seed)
    Assert-Equal $r.Errors.Count 0
    Assert-Equal $r.Connections.Count 14
    $s = @{}; foreach ($c in $r.Connections) { $s[$c.Connection] = $c.Support }
    Assert-Equal "$($s['J2-M2A'])/$($s['J3-M2B'])/$($s['J1-M4A'])/$($s['J1-M1A'])/$($s['J5-M5B'])" '1/3/1/0/6' 'beams into their columns, the brace into the column, a column base onto nothing'
    Assert-Equal (($r.Combos.Values | ForEach-Object { "$($_.LC):$($_.Use)" }) -join ' ') '1:False 2:False 3:True 101:True 102:True'
    Assert-Throws { [CalcFlow.Register]::WriteNew($seed, $pkg, $null) } 'never overwritten'
}

Test 'the plan resolves every input of the three types: four calcs ready, no errors' {
    Assert-Equal $res.ErrorCount 0 (($res.Problems | Where-Object { $_.Severity -eq 'Error' } | ForEach-Object { $_.ToString() }) -join '; ')
    Assert-Equal (($res.Calcs | ForEach-Object { "$($_.Id):$($_.Type):$($_.Status)" }) -join ' ') 'G-BP:BP-1:Ready J1-M4A:BRC-GUSSET:Ready J2-M2A:BC-WELD:Ready J3-M2B:BC-WELD:Ready'
    Assert-Equal (@($res.Calcs | Where-Object { $_.Id -eq 'G-BP' })[0].Connections.Count) 2
    Assert-Equal $res.Cells.Count 80
    Assert-Equal ((Get-Codes $res 'Warning' | Sort-Object -Unique) -join ',') 'LIBRARY,UNSIGNED_CASES'
}

Test 'each cell says where its value came from: package, section table, the support slot, a type default, a step override' {
    $cell = { param($calc, $step, $c) @($res.Cells | Where-Object { $_.Calc -eq $calc -and $_.Step -eq $step -and $_.Cell -eq $c })[0] }
    $v = & $cell 'J2-M2A' 1 'B7'; Assert-Equal "$($v.Value)|$($v.Source)" '23.6|section W24X55 d'
    $v = & $cell 'J2-M2A' 1 'F10'; Assert-Equal "$($v.Value)|$($v.Source)" '0.71|section W14X90 tf' 'tBM = the column flange (support slot)'
    $v = & $cell 'G-BP' 1 'ColType'; Assert-Equal $v.Value 'W'
    $v = & $cell 'G-BP' 1 'MomUnit'; Assert-Equal $v.Value 'kip-ft'
    $v = & $cell 'J1-M4A' 1 'B6'; Assert-Equal "$($v.Value)|$($v.Source)" '0.25|type BRC-GUSSET default'
    $v = & $cell 'J1-M4A' 2 'B6'; Assert-Equal "$($v.Value)|$($v.Source)" '0.375|override (J1-M4A step 2)'
    $v = & $cell 'J1-M4A' 2 'B7'; Assert-Equal $v.Value '12' 'step 2 parameter'
}

Test 'end axes: a column drawn bottom-up and one drawn top-down both bear down on their base plate' {
    $rows = @($res.Demands | Where-Object { $_.Calc -eq 'G-BP' -and $_.LC -eq 3 })
    Assert-Equal (($rows | ForEach-Object { $_.Connection } | Sort-Object) -join ' ') 'J1-M1A J4-M3B'
    foreach ($r in $rows) { Assert-True ($r.F[2] -lt 0) "$($r.Connection) case 3: Fz = $($r.F[2]) (compression on the plate is negative)" }
    Assert-Equal ([CalcFlow.Planner]::EndAxes([double[]]@(1, 2, 3, 4, 5, 6), 'B') -join ' ') '-1 2 -3 -4 5 -6'
}

Test 'demands: the concurrent envelope holds each extreme once with its concurrent forces; per-case cells follow in the template units' {
    $d = @($res.Demands | Where-Object { $_.Calc -eq 'J2-M2A' })
    Assert-Equal (($d | ForEach-Object { $_.LC }) -join ' ') '3 101'
    Assert-Match $d[0].Note 'min Fz'
    Assert-Near $d[0].F[5] -1.632 1e-9 'Mz in kip-in (0.136 kip-ft)'
    $mz = @($res.CaseCells | Where-Object { $_.Calc -eq 'J2-M2A' -and $_.Row -eq 1 -and $_.Cell -eq 'G16' })[0]
    Assert-Equal "$($mz.Value)|$($mz.Unit)|$($mz.Binding)" '-1.632|kip-in|load:Mz'
    $bp = @($res.CaseCells | Where-Object { $_.Calc -eq 'G-BP' -and $_.Row -eq 1 -and $_.Cell -eq 'My_k' })[0]
    Assert-Equal "$($bp.Value)|$($bp.Unit)" '9.837|kip-ft' 'the base plate takes kip-ft (118.044 kip-in)'
    $g = @($res.Demands | Where-Object { $_.Calc -eq 'J1-M4A' -and $_.Step -eq 2 })
    Assert-Equal (($g | ForEach-Object { $_.LC }) -join ' ') '101 3 102' 'governing_set adds the top cases by the normalised sum'
}

Test 'other demand modes: per_combo takes every case in scope, abs_envelope one row of non-concurrent maxima' {
    $lib = New-LibraryVariant 'modes' @{ 'steps.csv' = @('BC-WELD,1,WG-I,concurrent_envelope', 'BC-WELD,1,WG-I,per_combo', 'BP-1,1,BP,concurrent_envelope', 'BP-1,1,BP,abs_envelope') }
    $r = Invoke-Plan $lib $reg
    Assert-Equal $r.ErrorCount 0
    Assert-Equal ((@($r.Demands | Where-Object { $_.Calc -eq 'J2-M2A' }) | ForEach-Object { $_.LC }) -join ' ') '3 101 102'
    $env = @($r.Demands | Where-Object { $_.Calc -eq 'G-BP' })
    Assert-Equal $env.Count 1
    Assert-Equal $env[0].LC 0
    Assert-Match $env[0].Note 'not concurrent'
}

Test 'gaps are named: an unknown type, a missing parameter, a missing slot, a template moved, an unpinned revision, new connections' {
    $r = Invoke-Plan $example (New-Register 'gaps' $assign {
            param($conns, $ov, $combos)
            foreach ($c in $conns) {
                if ($c['Connection'] -eq 'J3-M2B') { $c['Support'] = '' }
                if ($c['Connection'] -eq 'J5-M5B') { $c['Type'] = 'BC-BOLTED' }
            }
        } @('J7-M7B'))
    $codes = Get-Codes $r 'Error'
    foreach ($c in 'TYPE_UNKNOWN', 'UNRESOLVED') { Assert-True ($codes -contains $c) "$c in $($codes -join ',')" }
    Assert-True (@($r.Problems | Where-Object { $_.Calc -eq 'J3-M2B' -and $_.Message -like "*slot 'support' is not given*" }).Count -ge 1) 'the missing support slot is named'
    Assert-True ((Get-Codes $r 'Warning') -contains 'NEW_CONNECTIONS') 'a member end missing from the register is reported'
    Assert-Equal (@($r.Calcs | Where-Object { $_.Id -eq 'J3-M2B' })[0].Status) 'Gaps'

    $lib = New-LibraryVariant 'noparam' @{ 'params.csv' = @("BP-1,,tp,1,in,`r`n", '') }
    $r = Invoke-Plan $lib $reg
    $m = @($r.Problems | Where-Object { $_.Code -eq 'UNRESOLVED' -and $_.Calc -eq 'G-BP' })
    Assert-True ($m.Count -eq 1 -and $m[0].Message -like "*parameter 'tp' has no value*") (($r.Problems | ForEach-Object { $_.ToString() }) -join '; ')

    $lib = New-LibraryVariant 'moved' @{ 'inputs.csv' = @('B7,A7,w (in)', 'B7,A8,w (in)') }
    $r = Invoke-Plan $lib $reg
    Assert-True (@($r.Problems | Where-Object { $_.Code -eq 'GUARD_MOVED' -and $_.Message -like "*A8 reads 'ax  (in.)'*" }).Count -eq 1) (($r.Problems | ForEach-Object { $_.ToString() }) -join '; ')
    Assert-Equal (@($r.Calcs | Where-Object { $_.Id -eq 'J1-M4A' })[0].Status) 'Blocked' 'a calc on a template whose guards fail is blocked'

    $lib = New-LibraryVariant 'repinned' @{ 'templates.csv' = @('f80e3e820c8e5eb87f3953f1b72ccf7f2843d0ecf14b7c65e64f3072900af13c', ('0' * 64)) }
    $r = Invoke-Plan $lib $reg
    $m = @($r.Problems | Where-Object { $_.Code -eq 'TEMPLATE_CHANGED' })
    Assert-True ($m.Count -eq 1 -and $m[0].Message -like '*every guard still holds*') (($r.Problems | ForEach-Object { $_.ToString() }) -join '; ')
}

Test 'a group whose connections would need different inputs is refused' {
    $a = $assign.Clone(); $a['J6-M6A'] = @{ Type = 'BP-1'; Group = 'G-BP' }
    $r = Invoke-Plan $example (New-Register 'groupdiff' $a $null $null)
    $m = @($r.Problems | Where-Object { $_.Code -eq 'GROUP_DIFFERS' })
    Assert-True (@($m | Where-Object { $_.Message -like '*d (in)*J1-M1A: 14, J6-M6A: 8*' }).Count -eq 1) (($m | ForEach-Object { $_.Message }) -join '; ')
}

Test 'CalcFlow.ps1 register-new and plan: the files, and exit 0 for a complete plan, 1 for one with gaps' {
    $out = Join-Path $dir 'cli-plan'
    $o = & $exe -NoProfile -ExecutionPolicy Bypass -File $cfps plan -Package $pkgDir -Library $example -Register $reg -Templates $templates -Out $out 2>&1 | ForEach-Object { "$_" }
    Assert-Equal $LASTEXITCODE 0 ($o -join "`n")
    foreach ($f in 'plan.csv', 'cases.csv', 'demands.csv', 'calcs.csv', 'problems.csv') { Assert-True (Test-Path -LiteralPath (Join-Path $out $f)) "no $f" }
    Assert-True (@($o | Where-Object { $_ -like 'plan *4 calcs (4 ready), 80 cells*' }).Count -eq 1) ($o -join "`n")
    $calcs = [CalcFlow.Csv]::ReadTable((Join-Path $out 'calcs.csv'))
    Assert-Equal (@($calcs | ForEach-Object { $_['Connections'] }) -join ' / ') 'J1-M1A J4-M3B / J1-M4A / J2-M2A / J3-M2B'
    $o = & $exe -NoProfile -ExecutionPolicy Bypass -File $cfps plan -Package $pkgDir -Library (New-LibraryVariant 'cli-gap' @{ 'params.csv' = @("BP-1,,tp,1,in,`r`n", '') }) -Register $reg -Templates $templates -Out (Join-Path $dir 'cli-gap') 2>&1 | ForEach-Object { "$_" }
    Assert-Equal $LASTEXITCODE 1 ($o -join "`n")
    Assert-True (@($o | Where-Object { $_ -like "*parameter 'tp' has no value*" }).Count -eq 1) ($o -join "`n")
    $newReg = Join-Path $dir 'cli-register.xlsx'
    $o = & $exe -NoProfile -ExecutionPolicy Bypass -File $cfps register-new -Package $pkgDir -Library $example -Out $newReg 2>&1 | ForEach-Object { "$_" }
    Assert-Equal $LASTEXITCODE 0 ($o -join "`n")
    Assert-Equal ([CalcFlow.Register]::Load($newReg)).Connections.Count 14
}
