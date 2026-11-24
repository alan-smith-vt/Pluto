# ProjectBook.Tests.ps1 -- the engineer's tabs of CalcFlow.xlsx on the fixture project: "new" with the .anl and the
# generic templates writes Model Config, Connection Lib, Templates and a schedule tab per calc tab; the cells an
# engineer would fill are then written into a copy (as Excel would save them), and "check" and "calcs" run on it:
# two weld marks on one template with different leg sizes, and a base plate mark.

$dir = New-TestDir 'projectbook'
$fixture = Join-Path $script:Fixtures 'frame-demo.anl'
$templates = Join-Path $script:Fixtures 'templates'
$proj = Join-Path $dir 'proj'
$o = Invoke-CfCli new -Out $proj -Anl $fixture -Templates $templates
$newExit = $LASTEXITCODE
$wbPath = Join-Path $proj 'CalcFlow.xlsx'
$weld = 'Rect. Fillet Weld Group'
$plate = 'Base_Plate_Anchorage Calc'

# the column letter of a heading on a tab (schedule tabs: the heading row is 5; the others: 2)
function Get-Col($Book, [string]$Tab, [string]$Heading, [int]$Row) {
    $s = $Book.Sheet($Tab)
    for ($c = 0; $c -lt 300; $c++) { if ($s.Text($Row - 1, $c).Trim() -eq $Heading) { return [CalcFlow.Xlsx]::ColName($c) } }
    throw "no heading '$Heading' on $Tab"
}

# a copy of a workbook with cells written, as Excel would save them
function Save-Cells([string]$From, [string]$To, [object[]]$Cells) {
    $w = New-Object 'System.Collections.Generic.List[CalcFlow.CellWrite]'
    foreach ($c in $Cells) { $w.Add([CalcFlow.CellWrite]::new([string]$c[0], [string]$c[1], $c[2])) }
    [CalcFlow.XlsxPatch]::Write($From, $To, $w, $false)
}

Test 'new with an .anl and templates: Model Config per beam with frames-into guesses, a schedule tab per calc tab, launchers' {
    Assert-Equal $newExit 0 ($o -join "`n")
    $b = [CalcFlow.Xlsx]::Read($wbPath)
    $tabs = @($b.Sheets | ForEach-Object { $_.Name })
    foreach ($t in 'Start', 'Model Config', 'Connection Lib', 'Templates', $weld, 'I Fillet Weld Group', $plate, 'Template Cells', 'Outputs', 'Sign Maps', 'Load Cases') { Assert-True ($tabs -contains $t) "no tab $t" }
    Assert-True ($tabs -notcontains 'Base_Plate_Anchorage Rigid') 'a sheet with one constant gets no schedule tab'
    $mc = @($b.TableRows('tblModelConfig'))
    Assert-Equal $mc.Count 7
    Assert-Equal "$($mc[1]['Beam ID']) $($mc[1]['Node I']) $($mc[1]['Node J']) $($mc[1]['Section']) $($mc[1]['Shape']) $($mc[1]['Frames into I']) $($mc[1]['Frames into J'])" '2 2 3 W24X55 W 1 3'
    # the weld tab's schedule: its 22 inputs as columns, the forces sourced from the beam end, the rest typed
    $s = $b.Sheet($weld)
    $heads = @(1..22 | ForEach-Object { $s.Text(4, $_) })
    Assert-Equal ($heads[0..3] -join '|') 'Des. method|FEXX (ksi)|wleg (in.)|fth'
    $src = @{}; foreach ($i in 1..22) { $src[$s.Text(4, $i)] = $s.Text(1, $i) }
    Assert-Equal "$($src['Fx']) $($src['Mz']) $($src['wleg (in.)'])" 'Fx Mz Typed here'
    Assert-Equal $s.Text(2, 3) 'in' 'the unit from the label'
    Assert-Equal $s.Text(3, 2) '70' "the template's own value"
    foreach ($f in 'Post-process ANL.cmd', 'Check.cmd', 'Run calcs.cmd') { Assert-True (Test-Path -LiteralPath (Join-Path $proj $f)) "no $f" }
    Assert-Match ([IO.File]::ReadAllText((Join-Path $proj 'Run calcs.cmd'))) 'CalcFlow\.ps1" calcs -Workbook "%~dp0CalcFlow\.xlsx"'
    Assert-Equal ([CalcFlow.EngineerWorkbook]::Settings($wbPath))['Templates folder'] $templates
    Assert-True (Test-Path -LiteralPath (Join-Path $proj 'Model_Data.xlsx')) 'Model_Data.xlsx'
}

# ---- what the engineer fills in
$b = [CalcFlow.Xlsx]::Read($wbPath)
$cells = New-Object System.Collections.Generic.List[object]
$cells.Add(@('Connection Lib', 'A3', 'WELD-A')); $cells.Add(@('Connection Lib', 'B3', $weld)); $cells.Add(@('Connection Lib', 'C3', 'beam end weld'))
$cells.Add(@('Connection Lib', 'A4', 'WELD-B')); $cells.Add(@('Connection Lib', 'B4', $weld))
$cells.Add(@('Connection Lib', 'A5', 'BP-A')); $cells.Add(@('Connection Lib', 'B5', $plate))
foreach ($f in 'Fx', 'Fy', 'Fz') { $cells.Add(@($weld, ((Get-Col $b $weld $f 5) + '3'), 'kip')); $cells.Add(@($plate, ((Get-Col $b $plate $f 5) + '3'), 'kip')) }
foreach ($f in 'Mx', 'My', 'Mz') { $cells.Add(@($weld, ((Get-Col $b $weld $f 5) + '3'), 'kip-in')); $cells.Add(@($plate, ((Get-Col $b $plate $f 5) + '3'), 'kip-ft')) }
$leg = Get-Col $b $weld 'wleg (in.)' 5
$cells.Add(@($weld, 'A6', 'WELD-A')); $cells.Add(@($weld, ($leg + '6'), 0.25))
$cells.Add(@($weld, 'A7', 'WELD-B')); $cells.Add(@($weld, ($leg + '7'), 0.5))
$cells.Add(@($plate, 'A6', 'BP-A'))
# the base plate's solver flags must hold
$out = $b.Sheet('Outputs')
for ($r = 2; $r -lt $out.Rows.Count; $r++) {
    if ($out.Text($r, 0) -eq $plate -and $out.Text($r, 8) -in 'Converged', 'Singular') {
        $cells.Add(@('Outputs', "B$($r + 1)", 'Y')); $cells.Add(@('Outputs', "D$($r + 1)", 'health'))
        $cells.Add(@('Outputs', "E$($r + 1)", $(if ($out.Text($r, 8) -eq 'Converged') { 'TRUE' } else { 'FALSE' })))
    }
}
# beam 2 (joints 2 to 3) welded at both ends, column 1 on a base plate at joint 1
$cells.Add(@('Model Config', 'F4', 'WELD-A')); $cells.Add(@('Model Config', 'G4', 'WELD-B')); $cells.Add(@('Model Config', 'F3', 'BP-A'))
$filled = Join-Path $proj 'CalcFlow-filled.xlsx'
Save-Cells $wbPath $filled $cells.ToArray()

Test 'check: the tabs become the library text and a register, and every connection calc is ready' {
    $o = Invoke-CfCli check -Workbook $filled
    Assert-Equal $LASTEXITCODE 0 ($o -join "`n")
    Assert-True (@($o | Where-Object { $_ -like 'read *3 connection marks on * templates, 3 beam ends with a connection*' }).Count -eq 1) ($o -join "`n")
    Assert-True (@($o | Where-Object { $_ -like 'check *3 of 3 connection calcs are ready: 0 error(s)*' }).Count -eq 1) ($o -join "`n")
    $text = Join-Path $proj 'calcflow/generated/library/text'
    $in = [CalcFlow.Csv]::ReadTable((Join-Path $text 'inputs.csv'))
    $fx = @($in | Where-Object { $_['Template'] -eq $weld -and $_['Binding'] -eq 'load:Fx' })[0]
    Assert-Equal "$($fx['Cell'])|$($fx['Unit'])|$($fx['PerCase'])|$($fx['GuardText'])" 'B14|kip|Y|Fx'
    $lg = @($in | Where-Object { $_['Template'] -eq $weld -and $_['Cell'] -eq 'B6' })[0]
    Assert-Equal "$($lg['Binding'])|$($lg['Required'])" 'param:c_B6|N'
    $pa = [CalcFlow.Csv]::ReadTable((Join-Path $text 'params.csv'))
    Assert-Equal (@($pa | Where-Object { $_['Param'] -eq 'c_B6' } | ForEach-Object { "$($_['Type'])=$($_['Default'])" }) -join ' ') 'WELD-A=0.25 WELD-B=0.5'
    Assert-Equal (@([CalcFlow.Csv]::ReadTable((Join-Path $text 'steps.csv')) | ForEach-Object { "$($_['Type'])>$($_['Template'])>$($_['Demand'])" }) -join ' ') "BP-A>$plate>concurrent_envelope WELD-A>$weld>concurrent_envelope WELD-B>$weld>concurrent_envelope"
    $reg = [CalcFlow.Register]::Load((Join-Path $proj 'calcflow/generated/Connection_Register.xlsx'))
    Assert-Equal $reg.Connections.Count 14 'every member end of the model'
    Assert-Equal (@($reg.Connections | Where-Object { $_.Type } | ForEach-Object { "$($_.Connection)=$($_.Type)" }) -join ' ') 'J1-M1A=BP-A J2-M2A=WELD-A J3-M2B=WELD-B'
    Assert-True (@($o | Where-Object { $_ -like '*NEW_CONNECTIONS*' }).Count -eq 0) 'beam ends without a connection are not a warning'
    # the typed leg reaches the plan; a blank typed cell is not written at all
    $plan = [CalcFlow.Csv]::ReadTable((Join-Path $proj 'calcflow/plan/plan.csv'))
    Assert-Equal (@($plan | Where-Object { $_['Cell'] -eq 'B6' } | ForEach-Object { "$($_['Calc'])=$($_['Value'])" }) -join ' ') 'J2-M2A=0.25 J3-M2B=0.5'
    Assert-Equal @($plan | Where-Object { $_['Calc'] -eq 'J2-M2A' -and $_['Cell'] -eq 'F5' }).Count 0 'FEXX is left at the template value'
}

Test 'check says what to fix, by tab: a mark with no row, a force with no unit, a mark that does not exist, a renamed heading' {
    $b2 = [CalcFlow.Xlsx]::Read($filled)
    $bad = Join-Path $proj 'CalcFlow-bad.xlsx'
    Save-Cells $filled $bad @(
        @($weld, 'A7', ''),
        @($weld, ((Get-Col $b2 $weld 'Fz' 5) + '3'), ''),
        @('Model Config', 'F5', 'NOPE'),
        @($weld, ((Get-Col $b2 $weld 'fth' 5) + '5'), 'throat factor')
    )
    $o = Invoke-CfCli check -Workbook $bad
    Assert-Equal $LASTEXITCODE 1 ($o -join "`n")
    $all = $o -join "`n"
    Assert-Match $all "\[fix  \] $([regex]::Escape($weld)): connection WELD-B has no row on this tab"
    Assert-Match $all "column 'Fz' takes Fz: give its unit in the Unit row"
    Assert-Match $all "Model Config: beam 3, end I: connection 'NOPE' is not on the Connection Lib tab"
    Assert-Match $all "column 'throat factor' is not one of the template's inputs"
    Assert-Match $all '4 thing\(s\) to fix in the workbook'
}

Test 'calcs: check, then LibreOffice fills and calculates each mark; two marks on one template give their own DCRs' {
    if (-not [CalcFlow.LibreOffice]::Find()) { Write-Host '        (no LibreOffice here: skipped)' -ForegroundColor DarkGray; return }
    $o = Invoke-CfCli calcs -Workbook $filled -Engine libreoffice
    Assert-Equal $LASTEXITCODE 0 ($o -join "`n")
    Assert-True (@($o | Where-Object { $_ -like 'run      3 calc steps: 3 OK, 0 invalid, 0 failed*' }).Count -eq 1) ($o -join "`n")
    $work = Join-Path $proj 'calcflow'
    $a = @([CalcFlow.Csv]::ReadTable((Join-Path $work 'data/results/J2-M2A__s1.checks.csv')))[0]
    $c = @([CalcFlow.Csv]::ReadTable((Join-Path $work 'data/results/J3-M2B__s1.checks.csv')))[0]
    Assert-Equal "$($a['Check'])|$($c['Check'])" 'DCR|DCR'
    Assert-True ([double]$a['Max DCR'] -gt 0 -and [double]$c['Max DCR'] -gt 0) 'both welds have a DCR'
    # the record of WELD-A holds its own leg; the other cells are the template's
    $rec = [CalcFlow.TemplateBook]::Open((Join-Path $work $a['Record']))
    Assert-Equal $rec.Sheet($weld).Get(5, 1).Value '0.25'
    Assert-Equal $rec.Sheet($weld).Get(4, 5).Value '70'
    $recB = [CalcFlow.TemplateBook]::Open((Join-Path $work $c['Record']))
    Assert-Equal $recB.Sheet($weld).Get(5, 1).Value '0.5'
    $bp = @([CalcFlow.Csv]::ReadTable((Join-Path $work 'data/results/J1-M1A__s1.csv')))
    Assert-True (@($bp | Where-Object { $_['Valid'] -ne 'Y' }).Count -eq 0) 'the base plate converged in every case'
    Assert-True (@(Get-ChildItem -LiteralPath (Join-Path $work 'reports') -Filter 'Connection_Summary_*.xlsx').Count -ge 1) 'the summary'
}

# ---- a template that takes a table of load rows: one copy per mark, its rows from the beam ends that call the mark
$tdir2 = Join-Path $dir 'templates2'
New-Item -ItemType Directory -Force -Path $tdir2 | Out-Null
$rowsXml = '<sheetData><row r="1">' + (@('Beam', 'Node', 'LC', 'Fx (kip)', 'Fy (kip)', 'Fz (kip)', 'Mx (kip-in)', 'My (kip-in)', 'Mz (kip-in)', 'DCR') | ForEach-Object -Begin { $i = 0 } -Process { $c = [CalcFlow.Xlsx]::ColName($i); $i++; "<c r=`"${c}1`" t=`"inlineStr`"><is><t>$_</t></is></c>" }) -join ''
$rowsXml += '<c r="M1" t="inlineStr"><is><t>Capacity (kip)</t></is></c><c r="N1"><v>100</v></c></row>'
for ($r = 2; $r -le 21; $r++) {
    $rowsXml += "<row r=`"$r`">"
    if ($r -le 3) { foreach ($c in 'A', 'B', 'C', 'D', 'E', 'F', 'G', 'H', 'I') { $rowsXml += "<c r=`"$c$r`"><v>9</v></c>" } }      # sample rows
    $rowsXml += "<c r=`"J$r`"><f>IF(A$r=`"`",`"`",(ABS(D$r)+ABS(E$r)+ABS(F$r))/`$N`$1)</f></c>"
    if ($r -eq 3) { $rowsXml += '<c r="M3" t="inlineStr"><is><t>Max DCR</t></is></c><c r="N3"><f>MAX(J2:J21)</f></c>' }
    $rowsXml += '</row>'
}
$rowsXml += '</sheetData>'
New-MiniXlsx (Join-Path $tdir2 'Beam Shear Batch.xlsx') $rowsXml ''
$proj2 = Join-Path $dir 'proj2'
$o2 = Invoke-CfCli new -Out $proj2 -Anl $fixture -Templates $tdir2
$batch = 'Beam-Shear-Batch'

Test "a load table is found in a template: its first cell, its columns in order, its rows and its units" {
    Assert-Equal $LASTEXITCODE 0 ($o2 -join "`n")
    $b = [CalcFlow.Xlsx]::Read((Join-Path $proj2 'CalcFlow.xlsx'))
    $t = @($b.TableRows('tblConfigTemplates'))[0]
    Assert-Equal "$($t['Template'])|$($t['Load table at'])|$($t['Load table columns'])|$($t['Max rows'])|$($t['Force unit'])|$($t['Moment unit'])|$($t['Loads'])" "$batch|A2|Beam, Node, LC, Fx, Fy, Fz, Mx, My, Mz|20|kip|kip-in|All cases"
    Assert-Equal ($b.Sheet($batch).Text(4, 1)) 'Capacity (kip)' 'the one typed input is a column of the schedule'
}

$b = [CalcFlow.Xlsx]::Read((Join-Path $proj2 'CalcFlow.xlsx'))
$cap = Get-Col $b $batch 'Capacity (kip)' 5
$orow = 3; $os = $b.Sheet('Outputs'); while ($os.Text($orow - 1, 0)) { $orow++ }
$filled2 = Join-Path $proj2 'CalcFlow-filled.xlsx'
Save-Cells (Join-Path $proj2 'CalcFlow.xlsx') $filled2 @(
    @('Connection Lib', 'A3', 'SB-1'), @('Connection Lib', 'B3', $batch), @('Connection Lib', 'A4', 'SB-2'), @('Connection Lib', 'B4', $batch),
    @($batch, 'A6', 'SB-1'), @($batch, 'A7', 'SB-2'), @($batch, ($cap + '7'), 50),
    # beam 2 end I and beam 5 end J call SB-1; beam 2 end J and beam 7 end I call SB-2
    @('Model Config', 'F4', 'SB-1'), @('Model Config', 'G7', 'SB-1'), @('Model Config', 'G4', 'SB-2'), @('Model Config', 'F9', 'SB-2'),
    # a result beside the table: one DCR per row, from J2 down
    @('Outputs', "A$orow", $batch), @('Outputs', "B$orow", 'Y'), @('Outputs', "C$orow", 'Row DCR'), @('Outputs', "D$orow", 'dcr'), @('Outputs', "F$orow", 'Y'),
    @('Outputs', "G$orow", 'DCR'), @('Outputs', "H$orow", 'Calc'), @('Outputs', "I$orow", 'J2')
)

Test 'a mark on a load-table template is one calc: its table lists only the beam ends that call it, each with its own cases' {
    $o = Invoke-CfCli check -Workbook $filled2
    Assert-Equal $LASTEXITCODE 0 ($o -join "`n")
    Assert-True (@($o | Where-Object { $_ -like 'check *2 of 2 connection calcs are ready: 0 error(s)*' }).Count -eq 1) ($o -join "`n")
    $plan = Join-Path $proj2 'calcflow/plan'
    $calcs = [CalcFlow.Csv]::ReadTable((Join-Path $plan 'calcs.csv'))
    Assert-Equal (@($calcs | ForEach-Object { "$($_['Calc']): $($_['Connections'])" }) -join ' | ') 'SB-1: J2-M2A J5-M5B | SB-2: J2-M7A J3-M2B'
    $dem = @([CalcFlow.Csv]::ReadTable((Join-Path $plan 'demands.csv')) | Where-Object { $_['Calc'] -eq 'SB-1' })
    Assert-Equal (@($dem | ForEach-Object { "$($_['Connection']):$($_['LC'])" }) -join ' ') 'J2-M2A:3 J2-M2A:101 J2-M2A:102 J5-M5B:3 J5-M5B:101 J5-M5B:102'
    $cases = @([CalcFlow.Csv]::ReadTable((Join-Path $plan 'cases.csv')) | Where-Object { $_['Calc'] -eq 'SB-1' })
    $row4 = @{}; foreach ($c in $cases | Where-Object { $_['Row'] -eq '4' }) { $row4[$c['Cell']] = $c['Value'] }
    Assert-Equal "$($row4['A2']) $($row4['B2']) $($row4['C2'])" '5 5 3' 'the fourth row is beam 5 at node 5, case 3 (cells are given for the first row; each row sits one lower)'
    $in = [CalcFlow.Csv]::ReadTable((Join-Path $proj2 'calcflow/generated/library/text/inputs.csv'))
    $fx = @($in | Where-Object { $_['Binding'] -eq 'load:Fx' })[0]
    Assert-Equal "$($fx['Cell'])|$($fx['Unit'])|$($fx['PerCase'])|$($fx['GuardCell'])|$($fx['GuardText'])" 'D2|kip|Y|D1|Fx (kip)'
}

Test 'calcs on a load table: each row gets its DCR, the unused rows are cleared, each mark keeps its own copy and capacity' {
    if (-not [CalcFlow.LibreOffice]::Find()) { Write-Host '        (no LibreOffice here: skipped)' -ForegroundColor DarkGray; return }
    $o = Invoke-CfCli calcs -Workbook $filled2 -Engine libreoffice
    Assert-Equal $LASTEXITCODE 0 ($o -join "`n")
    Assert-True (@($o | Where-Object { $_ -like 'run      2 calc steps: 2 OK, 0 invalid, 0 failed*' }).Count -eq 1) ($o -join "`n")
    $work = Join-Path $proj2 'calcflow'
    $dem = @([CalcFlow.Csv]::ReadTable((Join-Path $work 'plan/demands.csv')))
    foreach ($m in @(@('SB-1', 100.0), @('SB-2', 50.0))) {
        $rows = @([CalcFlow.Csv]::ReadTable((Join-Path $work "data/results/$($m[0])__s1.csv")))
        Assert-Equal $rows.Count 6 "$($m[0]): two beam ends x three cases"
        foreach ($r in $rows) {
            $d = @($dem | Where-Object { $_['Calc'] -eq $m[0] -and $_['Row'] -eq $r['Row'] })[0]
            $want = ([Math]::Abs([double]$d['Fx (kip)']) + [Math]::Abs([double]$d['Fy (kip)']) + [Math]::Abs([double]$d['Fz (kip)'])) / $m[1]
            Assert-Near ([double]$r['DCR']) $want 1e-9 "$($m[0]) row $($r['Row'])"
        }
    }
    # per connection: its own rows' largest DCR
    $chk = @([CalcFlow.Csv]::ReadTable((Join-Path $work 'data/results/SB-1__s1.checks.csv')) | Where-Object { $_['Check'] -eq 'Row DCR' })
    Assert-Equal (@($chk | ForEach-Object { $_['Connection'] }) -join ',') ',J2-M2A,J5-M5B'
    # the record: six rows filled, the seventh cleared (the template's sample rows are gone)
    $rec = [CalcFlow.TemplateBook]::Open((Join-Path $work @($chk)[0]['Record']))
    $s = $rec.Sheet('Calc')
    Assert-Equal "$($s.Get(1, 0).Value) $($s.Get(1, 1).Value) $($s.Get(6, 0).Value)" '2 2 5'
    Assert-True ($s.Get(7, 0) -eq $null -or $s.Get(7, 0).IsEmpty) 'row 8 is empty'
    Assert-True ($s.Get(7, 3) -eq $null -or $s.Get(7, 3).IsEmpty) 'its forces too'
    # the summary: each beam end shows its own DCR
    $sum = [CalcFlow.Xlsx]::Read(@(Get-ChildItem -LiteralPath (Join-Path $work 'reports') -Filter 'Connection_Summary_*.xlsx')[0].FullName)
    $conn = @($sum.TableRows('tblSummaryConnections') | Where-Object { $_['Type'] })
    Assert-Equal (@($conn | ForEach-Object { "$($_['Connection'])=$($_['Calc'])" }) -join ' ') 'J2-M2A=SB-1 J2-M7A=SB-2 J3-M2B=SB-2 J5-M5B=SB-1'
    Assert-True (@($conn | ForEach-Object { [double]$_['Row DCR'] } | Sort-Object -Unique).Count -ge 3) 'the beam ends have their own DCRs'
}
