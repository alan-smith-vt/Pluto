# Forces.Tests.ps1 -- what a template's forces are (the Templates tab's Forces column): the member end's own forces, or
# the support reaction at the member end's joint, given in that end's axes so that one sign map serves both. The
# axes are checked on the probe's real .anl (a column alone on its support: its end forces are the reaction), then
# the choice is taken through "check" on the fixture project, whose joint 1 holds a column and a brace.

$dir = New-TestDir 'forces'
$probePath = Join-Path (Join-Path (Join-Path (Join-Path $script:Repo 'scripts') 'lib') 'probes') 'anl-prints-probe.anl'
$probePkg = Join-Path $dir 'probe-pkg'
[void][FramePackageWriter]::Write([AnlFrameReader]::Read($probePath), $probePkg)
$pp = [CalcFlow.AnalysisPackage]::Load($probePkg)

Test "the probe's real .anl: where a column stands alone on its support, the reaction in its end axes is its end force" {
    # columns 103 and 106 (BETA 90) are the only members at joints 3 and 6; both tables are printed to 0.01
    foreach ($x in @(@(3, 103), @(6, 106))) {
        $e = $pp.End($x[0], $x[1], 'A')
        $cases = @($pp.Reactions($x[0]).Keys)
        Assert-Equal ($cases -join ' ') '6 101 102 103 104 201'
        foreach ($lc in $cases) {
            $r = $pp.ReactionAtEnd($e, $lc)
            $f = [CalcFlow.Planner]::EndAxes($pp.Forces($x[1], 'A')[$lc], 'A')
            foreach ($i in 0..5) { Assert-Near $r[$i] $f[$i] 0.0101 "joint $($x[0]) case $lc component $i" }
        }
        Assert-True ($pp.ReactionAtEnd($e, 6)[0] -gt 30) "joint $($x[0]), dead and live: the column is in compression, so x (up the column) is positive"
    }
    Assert-True ($pp.ReactionAtEnd($pp.End(3, 103, 'A'), 1) -eq $null) 'no reaction was printed for case 1'
    Assert-Equal $pp.Reactions(11).Count 0 'joint 11 is not a support'
}

Test "the probe's real .anl: with a brace on the same base the moments are the column's, and the forces are not" {
    # columns 101 and 102 (BETA 0) share joints 1 and 2 with truss braces, which carry no moment
    foreach ($x in @(@(1, 101), @(2, 102))) {
        $e = $pp.End($x[0], $x[1], 'A')
        $gap = 0.0
        foreach ($lc in @($pp.Reactions($x[0]).Keys)) {
            $r = $pp.ReactionAtEnd($e, $lc)
            $f = [CalcFlow.Planner]::EndAxes($pp.Forces($x[1], 'A')[$lc], 'A')
            foreach ($i in 3..5) { Assert-Near $r[$i] $f[$i] 0.0101 "joint $($x[0]) case $lc moment $i" }
            foreach ($i in 0..2) { $gap = [Math]::Max($gap, [Math]::Abs($r[$i] - $f[$i])) }
        }
        Assert-True ($gap -gt 1.0) "joint $($x[0]): the brace's force is in the reaction and not in the column ($gap kip)"
    }
}

# ---- the fixture project: column 1 (BETA 90) and brace 4 at joint 1, column 3 with its end J at joint 4
$fixture = Join-Path $script:Fixtures 'frame-demo.anl'
$templates = Join-Path $script:Fixtures 'templates'
$proj = Join-Path $dir 'proj'
$o = Invoke-CfCli new -Out $proj -Anl $fixture -Templates $templates
$newExit = $LASTEXITCODE
$wbPath = Join-Path $proj 'CalcFlow.xlsx'
$plate = 'Base_Plate_Anchorage'      # the workbook's one calc: its Calc sheet, with the inputs of the sheets Calc works with
$b = [CalcFlow.Xlsx]::Read($wbPath)
$fcol = Get-Col $b 'Templates' 'Forces' 2
$prow = @(2..40 | Where-Object { $b.Sheet('Templates').Text($_, 0) -eq $plate })[0] + 1

Test 'the Templates tab asks what the forces are: a list of two, the member end forces unless changed' {
    Assert-Equal $newExit 0 ($o -join "`n")
    $t = @($b.TableRows('tblConfigTemplates'))
    Assert-Equal (@($t | ForEach-Object { $_['Forces'] } | Sort-Object -Unique) -join '|') 'Member end forces'
    $h = [CalcFlow.Harvest]::Run([string[]]@(Get-ChildItem -LiteralPath $templates -Filter '*.xlsx' | ForEach-Object { $_.FullName }), $templates)
    $tt = @([CalcFlow.ProjectBook]::Sheets($null, $h) | Where-Object { $_.Name -eq 'Templates' })[0]
    $v = @($tt.Validations | Where-Object { $_.Sqref -like "${fcol}3:*" })
    Assert-Equal "$($v.Count) $($v[0].List)" '1 "Member end forces,Support reaction"'
}

# what the engineer fills in: one base plate mark at joint 1 (beam 1, end I) and at joint 4 (beam 3, end J)
$cells = New-Object System.Collections.Generic.List[object]
$cells.Add(@('Connection Lib', 'A3', 'BP-A')); $cells.Add(@('Connection Lib', 'B3', $plate))
foreach ($f in 'Fx', 'Fy', 'Fz') { $cells.Add(@($plate, ((Get-Col $b $plate $f 5) + '3'), 'kip')) }
foreach ($f in 'Mx', 'My', 'Mz') { $cells.Add(@($plate, ((Get-Col $b $plate $f 5) + '3'), 'kip-ft')) }
$cells.Add(@($plate, 'A6', 'BP-A'))
$cells.Add(@('Model Config', 'F3', 'BP-A')); $cells.Add(@('Model Config', 'G5', 'BP-A'))
$cells.Add(@('Templates', "$fcol$prow", 'Support reaction'))
$allCases = Join-Path $proj 'CalcFlow-all-cases.xlsx'
Save-Cells $wbPath $allCases $cells.ToArray()
# the fixture prints its reactions for cases 1 and 2 only, and a new workbook uses the combinations (3, 101, 102)
foreach ($lc in '1', '2', '3', '101', '102') {
    $use = 'N'; if ($lc -in '1', '2') { $use = 'Y' }
    $cells.Add(@('Load Cases', ('D' + (@(2..40 | Where-Object { $b.Sheet('Load Cases').Text($_, 0) -eq $lc })[0] + 1)), $use))
}
$filled = Join-Path $proj 'CalcFlow-filled.xlsx'
Save-Cells $wbPath $filled $cells.ToArray()
$demands = {
    param($conn)
    @([CalcFlow.Csv]::ReadTable((Join-Path $proj 'calcflow/plan/demands.csv')) | Where-Object { $_['Connection'] -eq $conn } | Sort-Object { [int]$_['LC'] })
}
$six = { param($d) @('Fx (kip)', 'Fy (kip)', 'Fz (kip)', 'Mx (kip-in)', 'My (kip-in)', 'Mz (kip-in)' | ForEach-Object { ([double]$d[$_]).ToString('0.###', [Globalization.CultureInfo]::InvariantCulture) }) -join ' ' }

Test 'Support reaction: the calc takes the reaction at its joint, in the axes of its beam end and in the plan units' {
    $o = Invoke-CfCli check -Workbook $filled
    Assert-Equal $LASTEXITCODE 0 ($o -join "`n")
    $steps = @([CalcFlow.Csv]::ReadTable((Join-Path $proj 'calcflow/generated/library/text/steps.csv')))
    Assert-Equal "$($steps[0]['Type'])>$($steps[0]['Slot'])" 'BP-A>reaction'
    # joint 1 is printed as FX 0.512, FY 8.432, MZ 2.151 kip-in (case 1). Column 1 runs up global Y with BETA 90, so
    # its y is global Z and its z is global X: x takes FY, z takes FX, and the moment about global Z is about y
    $d = & $demands 'J1-M1A'
    Assert-Equal (@($d | ForEach-Object { $_['LC'] }) -join ' ') '1 2'
    Assert-Equal (& $six $d[0]) '8.432 0 0.512 0 2.151 0'
    Assert-Equal (& $six $d[1]) '10.221 0 0.7 0 3.01 0'
    Assert-Match $d[0]['Note'] '^support reaction at joint 1\b'
    # joint 4 (FX -0.512, FY 7.124) is the J end of column 3, which runs down: x is still away from the joint (up),
    # its y is global X
    $d4 = & $demands 'J4-M3B'
    Assert-Equal (& $six $d4[0]) '7.124 -0.512 0 0 0 0'
    # not the column's own end force, which leaves out the brace: 9.5 kip at joint 1 in case 1
    $own = [CalcFlow.AnalysisPackage]::Load((Join-Path $proj 'calcflow/package/frame-demo')).Forces(1, 'A')[1]
    Assert-Near $own[0] 9.5 1e-9
}

Test 'Member end forces (the default) is unchanged: the same mark takes the column end' {
    $ends = Join-Path $proj 'CalcFlow-ends.xlsx'
    Save-Cells $filled $ends @(, @('Templates', "$fcol$prow", 'Member end forces'))
    $o = Invoke-CfCli check -Workbook $ends
    Assert-Equal $LASTEXITCODE 0 ($o -join "`n")
    $d = & $demands 'J1-M1A'
    Assert-Equal (& $six $d[0]) '9.5 0.85 0.05 0.24 3.6 34.8' 'member 1 at joint 1, case 1, printed in kip and feet'
    Assert-Equal $d[0]['Note'].IndexOf('support reaction') -1
    Assert-Equal @([CalcFlow.Csv]::ReadTable((Join-Path $proj 'calcflow/generated/library/text/steps.csv')))[0]['Slot'] 'this'
    # a blank cell, as in a workbook made before the column existed, is the same
    $blank = Join-Path $proj 'CalcFlow-blank.xlsx'
    Save-Cells $filled $blank @(, @('Templates', "$fcol$prow", ''))
    $o = Invoke-CfCli check -Workbook $blank
    Assert-Equal $LASTEXITCODE 0 ($o -join "`n")
    Assert-Equal (& $six (& $demands 'J1-M1A')[0]) '9.5 0.85 0.05 0.24 3.6 34.8'
}

Test 'Support reaction is refused with the reason: a case with no printed reaction, a joint that is not a support, a word that is not on the list' {
    $o = Invoke-CfCli check -Workbook $allCases
    Assert-Equal $LASTEXITCODE 1 ($o -join "`n")
    Assert-Match ($o -join "`n") 'joint 1 has no printed support reaction for case\(s\) 3, 101, 102: print the reactions under the same LOAD LIST as the member forces'
    # beam 2 runs from joint 2 to joint 3: neither is a support
    $beam = Join-Path $proj 'CalcFlow-beam.xlsx'
    Save-Cells $filled $beam @(, @('Model Config', 'F4', 'BP-A'))
    $o = Invoke-CfCli check -Workbook $beam
    Assert-Equal $LASTEXITCODE 1 ($o -join "`n")
    Assert-Match ($o -join "`n") "$([regex]::Escape($plate)) takes the support reaction, but joint 2 \(member 2 end A\) is not a support"
    $word = Join-Path $proj 'CalcFlow-word.xlsx'
    Save-Cells $filled $word @(, @('Templates', "$fcol$prow", 'Reactions'))
    $o = Invoke-CfCli check -Workbook $word
    Assert-Equal $LASTEXITCODE 1 ($o -join "`n")
    Assert-Match ($o -join "`n") "\[fix  \] Templates: template $([regex]::Escape($plate)): Forces is 'Member end forces' or 'Support reaction' \(got 'Reactions'\)"
}

Test 'refresh keeps the choice' {
    $w = Join-Path $proj 'refresh'
    New-Item -ItemType Directory -Force -Path $w | Out-Null
    $wb = Join-Path $w 'CalcFlow.xlsx'
    Copy-Item -LiteralPath $filled -Destination $wb
    foreach ($f in @(Get-ChildItem -LiteralPath $proj -File | Where-Object { $_.Name -notlike 'CalcFlow*.xlsx' })) { Copy-Item -LiteralPath $f.FullName -Destination $w }
    Copy-Item -LiteralPath (Join-Path $proj 'calcflow') -Destination $w -Recurse
    $o = Invoke-CfCli refresh -Workbook $wb -NoWait -NoOpen
    Assert-Equal $LASTEXITCODE 0 ($o -join "`n")
    $t = @([CalcFlow.Xlsx]::Read($wb).TableRows('tblConfigTemplates'))
    Assert-Equal (@($t | ForEach-Object { $_['Forces'] } | Sort-Object -Unique) -join '|') 'Member end forces|Support reaction'
    Assert-Equal @($t | Where-Object { $_['Template'] -eq $plate })[0]['Forces'] 'Support reaction'
}
