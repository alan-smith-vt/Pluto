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
$plate = 'Base_Plate_Anchorage'      # the workbook's one calc: its Calc sheet, with the inputs of the sheets Calc works with

Test 'new with an .anl and templates: Model Config per beam with frames-into guesses, a schedule tab per calc tab, launchers' {
    Assert-Equal $newExit 0 ($o -join "`n")
    $b = [CalcFlow.Xlsx]::Read($wbPath)
    $tabs = @($b.Sheets | ForEach-Object { $_.Name })
    foreach ($t in 'Start', 'Model Config', 'Connection Lib', 'Templates', $weld, 'I Fillet Weld Group', $plate, 'Template Cells', 'Outputs', 'Sign Maps', 'Load Cases') { Assert-True ($tabs -contains $t) "no tab $t" }
    Assert-True (@($tabs | Where-Object { $_ -like 'Base_Plate_Anchorage ?*' }).Count -eq 0) 'the sheets of the base plate workbook read one another: one calc, one tab'
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

Test 'results: one governing DCR is chosen per template, a row title does not name the helper cells along its row, a result at the end of a row is guarded by the row title' {
    $b = [CalcFlow.Xlsx]::Read($wbPath)
    $outs = @($b.TableRows('tblConfigOutputs'))
    foreach ($t in $weld, 'Circ. Fillet Weld Group', 'I Fillet Weld Group', $plate) {
        $y = @($outs | Where-Object { $_['Template'] -eq $t -and $_['Use'] -eq 'Y' })
        Assert-Equal "$($y.Count) $($y[0]['Result'])" '1 Governing DCR' $t
    }
    # the circular tab's sweep table shares rows 42 to 44 with the results: its cells are not results
    $circ = @($outs | Where-Object { $_['Template'] -eq 'Circ. Fillet Weld Group' } | ForEach-Object { $_['Cell'] })
    Assert-Equal (@($circ | Where-Object { $_ -match '^(O|P|S|T|U|V|W|X|Y|Z|AA|AB|AC)4[2-4]$' }).Count) 0 ($circ -join ' ')
    Assert-True ($circ -contains 'F44') 'the governing DCR itself'
    # "DCR bearing fp / fpmax" holds the letters max, not the word
    Assert-Equal @($outs | Where-Object { $_['Template'] -eq $plate -and $_['Result'] -like '*fpmax*' -and $_['Use'] -eq 'Y' }).Count 0
    $h99 = @($outs | Where-Object { $_['Template'] -eq $plate -and $_['Cell'] -eq 'H99' })[0]
    Assert-True ("$($h99['Label cell'])|$($h99['Label'])" -like 'A99|Plate bending (k-in/in) * DG1') "the row title guards H99: $($h99['Label cell'])|$($h99['Label'])"
}

Test 'the base plate workbook is one calc: its tab holds the inputs of the Calc sheet and of the sheets Calc works with' {
    $b = [CalcFlow.Xlsx]::Read($wbPath)
    $s = $b.Sheet($plate)
    $heads = @(); $n = 1; while ($s.Text(4, $n)) { $heads += $s.Text(4, $n); $n++ }
    Assert-Equal (@($heads | Where-Object { $_ -like 'ACI: *' }).Count) 9 'the anchorage sheet has nine inputs of its own'
    Assert-True ($heads -contains 'Work: Es (ksi)' -and $heads -contains 'Rigid: Iteration 1') ($heads -join '|')
    Assert-Equal $heads[0] 'B (in)' "the Calc sheet's own inputs come first, under their own labels"
    $rows = @{}; foreach ($r in @($b.TableRows('tblTemplateSheets') | Where-Object { $_['File'] -eq 'Base_Plate_Anchorage.xlsx' })) { $rows[$r['Sheet']] = $r }
    Assert-Equal (@('Calc', 'ACI', 'Work', 'Rigid', 'Plate' | ForEach-Object { "$_=$($rows[$_]['On tab'])" }) -join ' ') "Calc=$plate ACI=$plate Work=$plate Rigid=$plate Plate="
    # the weld shapes read nothing of one another: a tab each
    Assert-Equal @($b.TableRows('tblTemplateSheets') | Where-Object { $_['File'] -eq 'Weld Group Design Tool.xlsx' -and $_['Says'] -eq 'A calc tab of its own.' }).Count 6
}

Test 'every tab keeps its heading rows and the columns that say what the row is in view' {
    $h = [CalcFlow.Harvest]::Run([string[]]@(Get-ChildItem -LiteralPath $templates -Filter '*.xlsx' | ForEach-Object { $_.FullName }), $templates)
    $pkgDir = Join-Path $dir 'freeze-pkg'
    [void][FramePackageWriter]::Write([AnlFrameReader]::Read($fixture), $pkgDir)
    $pkg = [CalcFlow.AnalysisPackage]::Load($pkgDir)
    $f = @{}; foreach ($s in [CalcFlow.ProjectBook]::Sheets($pkg, $h)) { $f[$s.Name] = "$($s.FreezeRows)/$($s.FreezeCols)" }
    Assert-Equal (@('Model Config', 'Connection Lib', 'Templates', 'Template Sheets', $plate, $weld, 'Template Cells', 'Outputs', 'Sign Maps', 'Load Cases' | ForEach-Object { "$_ $($f[$_])" }) -join ' | ') "Model Config 2/1 | Connection Lib 2/1 | Templates 2/1 | Template Sheets 2/2 | $plate 5/1 | $weld 5/1 | Template Cells 2/2 | Outputs 2/1 | Sign Maps 2/1 | Load Cases 2/2"
    # the generated workbooks too: a member's number and end stay beside its forces
    $md = @{}; foreach ($s in [CalcFlow.ModelData]::Sheets($pkg, (New-Object CalcFlow.ModelDataResult))) { $md[$s.Name] = "$($s.FreezeRows)/$($s.FreezeCols)" }
    Assert-Equal "$($md['Members']) $($md['Joints']) $($md['All End Forces'])" '2/1 2/1 2/4'
}

Test 'a note beside every cell to fill says what it is and what is expected: on each tab, on its heading too' {
    $h = [CalcFlow.Harvest]::Run([string[]]@(Get-ChildItem -LiteralPath $templates -Filter '*.xlsx' | ForEach-Object { $_.FullName }), $templates)
    $tabs = @{}; foreach ($s in [CalcFlow.ProjectBook]::Sheets($null, $h)) { $tabs[$s.Name] = $s }
    $note = { param($tab, $sqref) @($tabs[$tab].Validations | Where-Object { $_.Sqref -like $sqref })[0] }
    # a column with a drop-down carries its note on the drop-down; one drop-down over both ends became one an end
    $ci = & $note 'Model Config' 'F3:F*'
    Assert-Equal "$($ci.PromptTitle)|$($ci.List)" "Connection I|'Connection Lib'!`$A`$3:`$A`$2000"
    Assert-Match $ci.Prompt '^Pick the connection at end I: a mark from the Connection Lib tab'
    Assert-Match (& $note 'Model Config' 'G3:G*').Prompt 'at end J'
    Assert-Match (& $note 'Model Config' 'A2').Prompt "^The member's number in the STAAD model\. Written by the tool"
    Assert-Match (& $note 'Templates' 'H3:H*').Prompt '^Member end forces: .* Support reaction: '
    Assert-Match (& $note 'Templates' 'N3:N*').Prompt 'Start with the word also'
    Assert-Match (& $note 'Outputs' 'D3:D*').Prompt '^dcr: a ratio'
    Assert-Match (& $note 'Sign Maps' 'I3:I*').Prompt '^on_member: '
    Assert-Equal (& $note 'Sign Maps' 'I3:I*').List '"on_member,on_connection"'
    Assert-Match (& $note 'Load Cases' 'D3:D*').Prompt '^Y = the calcs take this load case'
    Assert-Match (& $note 'Template Sheets' 'F3:F*').Prompt 'what to do when it needs an input that was not picked up'
    # a schedule tab: the three rows above the headings, the mark, and each input with its template cell and value
    Assert-Match (& $note $weld 'B2:*').Prompt "^Where this column's value comes from"
    Assert-Match (& $note $weld 'B3:*').Prompt 'needs one, such as kip'
    Assert-Match (& $note $weld 'A6:*').Prompt '^A mark from the Connection Lib tab whose Template is this tab'
    $leg = @($tabs[$weld].Validations | Where-Object { $_.PromptTitle -eq 'wleg (in.)' })
    Assert-Equal (@($leg | ForEach-Object { $_.Sqref }) -join ' ') 'D5 D6:D2000' 'its heading and its cells'
    Assert-Equal $leg[1].Prompt "Template cell B6. The mark's value for this input, used when Source (row 2) is Typed here. Blank = the template's own value (0.25). (blank) = empty the cell."
    $aci = @($tabs[$plate].Validations | Where-Object { $_.PromptTitle -eq 'ACI: +x' })[1]
    Assert-Match $aci.Prompt "^Template cell 'ACI'!C310\. "
    foreach ($s in $tabs.Values) {
        foreach ($v in $s.Validations) { Assert-True ($v.Prompt.Length -le 255) "$($s.Name) $($v.Sqref): a note of $($v.Prompt.Length) characters" }
    }
    # the Start tab's settings, in the written workbook
    $zip = [IO.Compression.ZipFile]::OpenRead($wbPath)
    try { $r = New-Object IO.StreamReader($zip.GetEntry('xl/worksheets/sheet1.xml').Open()); $start = $r.ReadToEnd(); $r.Dispose() } finally { $zip.Dispose() }
    Assert-Match $start 'promptTitle="Templates folder" prompt="The folder that holds the calc template workbooks\." sqref="B\d+"'
}

Test 'cells that hold text as typed are formatted as text: the sign map entries, the given cells, a table column' {
    $h = [CalcFlow.Harvest]::Run([string[]]@(Get-ChildItem -LiteralPath $templates -Filter '*.xlsx' | ForEach-Object { $_.FullName }), $templates)
    $sheets = [CalcFlow.ProjectBook]::Sheets($null, $h)
    $sm = @($sheets | Where-Object { $_.Name -eq 'Sign Maps' })[0]
    Assert-Equal (@(1..6 | ForEach-Object { $sm.Rows[2][$_].Style.NumFmt }) -join '') '@@@@@@' '-Fz typed in Excel must stay text'
    $tt = @($sheets | Where-Object { $_.Name -eq 'Templates' })[0]
    $ic = [Array]::IndexOf([string[]]@($tt.Rows[1] | ForEach-Object { $_.Value }), 'Input cells')
    Assert-Equal "$($tt.Rows[2][$ic].Style.NumFmt)$($tt.Rows[2][$ic + 1].Style.NumFmt)" '@@'
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
    # named patched-*: the heading is renamed in its cell only (the test's stand-in for typing in Excel, which would
    # rename the table's column too), so the checks of every written workbook leave this file out
    $bad = Join-Path $proj 'patched-bad.xlsx'
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

# ---- labels a real template can hold, and Excel's rules for the headings of a table (a workbook that breaks one is
# "repaired" by Excel when it opens): a note of 300 characters beside an input, two labels that differ by a
# superscript or by case only, a label on two lines, a label that is a number
$tdir3 = Join-Path $dir 'templates3'
New-Item -ItemType Directory -Force -Path $tdir3 | Out-Null
$longLabel = 'Plate width. ' + ('Measure it between the outer faces, not between the welds. ' * 5)
$awkward = @($longLabel, "Area (in$([char]0xB2))", 'Area (in2)', "Plate`nthickness", '12', 'd', 'D')
$x3 = '<sheetData>'
for ($i = 0; $i -lt $awkward.Count; $i++) {
    $r = $i + 2
    $x3 += "<row r=`"$r`"><c r=`"A$r`" t=`"inlineStr`"><is><t xml:space=`"preserve`">$([Security.SecurityElement]::Escape($awkward[$i]))</t></is></c><c r=`"B$r`" s=`"1`"><v>$($i + 1)</v></c></row>"
}
$x3 += '<row r="10"><c r="A10" t="inlineStr"><is><t>Governing DCR</t></is></c><c r="B10"><f>SUM(B2:B8)/100</f></c></row></sheetData>'
New-MiniXlsx (Join-Path $tdir3 'Awkward Labels.xlsx') $x3 ''
$proj3 = Join-Path $dir 'proj3'
$o3 = Invoke-CfCli new -Out $proj3 -Anl $fixture -Templates $tdir3
$new3 = $LASTEXITCODE
$wb3 = Join-Path $proj3 'CalcFlow.xlsx'
$awk = 'Awkward-Labels'

Test "awkward template labels become headings Excel takes: cut at 255 characters, one line, and told apart where Excel would see one name" {
    Assert-Equal $new3 0 ($o3 -join "`n")
    Assert-True ($longLabel.Length -gt 255) 'the note is longer than a heading may be'
    Assert-Equal ([CalcFlow.XlsxCheck]::Tables($wb3) -join '; ') ''
    $s = [CalcFlow.Xlsx]::Read($wb3).Sheet($awk)
    $heads = @(1..7 | ForEach-Object { $s.Text(4, $_) })
    Assert-True ($heads[0].Length -le 255 -and $heads[0].StartsWith('Plate width. Measure it') -and $heads[0].EndsWith(' ...')) $heads[0]
    Assert-Equal ($heads[1..6] -join '|') "Area (in$([char]0xB2))|Area (in2) [B4]|Plate thickness|12|d|D [B8]"
    # the Template Cells tab names each column the same way: that is how a column finds its cell
    $cells = @([CalcFlow.Xlsx]::Read($wb3).TableRows('tblTemplateCells') | Where-Object { $_['Template'] -eq $awk })
    Assert-Equal (@($cells | ForEach-Object { "$($_['Column'].Length -le 255)$($_['Cell'])" }) -join ' ') 'TrueB2 TrueB3 TrueB4 TrueB5 TrueB6 TrueB7 TrueB8'
    Assert-Equal @($cells | Where-Object { $_['Cell'] -eq 'B2' })[0]['Column'] $heads[0]
}

Test 'a value typed under a cut heading reaches its cell, and a refresh keeps it' {
    $b = [CalcFlow.Xlsx]::Read($wb3)
    $filled3 = Join-Path $proj3 'CalcFlow-filled.xlsx'
    Save-Cells $wb3 $filled3 @(
        @('Connection Lib', 'A3', 'AW-1'), @('Connection Lib', 'B3', $awk),
        @($awk, 'A6', 'AW-1'), @($awk, 'B6', 42), @($awk, 'D6', 7),
        @('Model Config', 'F4', 'AW-1')
    )
    $o = Invoke-CfCli check -Workbook $filled3
    Assert-Equal $LASTEXITCODE 0 ($o -join "`n")
    $plan = [CalcFlow.Csv]::ReadTable((Join-Path $proj3 'calcflow/plan/plan.csv'))
    Assert-Equal (@($plan | Where-Object { $_['Calc'] -eq 'J2-M2A' } | Sort-Object { $_['Cell'] } | ForEach-Object { "$($_['Cell'])=$($_['Value'])" }) -join ' ') 'B2=42 B4=7'
    # refresh: the workbook is rebuilt, and the values stay under their headings
    $w = Join-Path $proj3 'refresh'
    New-Item -ItemType Directory -Force -Path $w | Out-Null
    foreach ($f in @(Get-ChildItem -LiteralPath $proj3 -File | Where-Object { $_.Name -notlike 'CalcFlow*.xlsx' })) { Copy-Item -LiteralPath $f.FullName -Destination $w }
    Copy-Item -LiteralPath (Join-Path $proj3 'calcflow') -Destination $w -Recurse
    Copy-Item -LiteralPath $filled3 -Destination (Join-Path $w 'CalcFlow.xlsx')
    $o = Invoke-CfCli refresh -Workbook (Join-Path $w 'CalcFlow.xlsx') -NoWait -NoOpen
    Assert-Equal $LASTEXITCODE 0 ($o -join "`n")
    $s = [CalcFlow.Xlsx]::Read((Join-Path $w 'CalcFlow.xlsx')).Sheet($awk)
    Assert-Equal "$($s.Text(5, 0))|$($s.Text(5, 1))|$($s.Text(5, 3))|$($s.Text(4, 3))" 'AW-1|42|7|Area (in2) [B4]'
    Assert-Equal ([CalcFlow.XlsxCheck]::Tables((Join-Path $w 'CalcFlow.xlsx')) -join '; ') ''
}

# ---- a workbook's sheets as calcs: which sheet reads which says what is one calc, and no input is left on no tab
# without the Template Sheets tab saying so
$tdir4 = Join-Path $dir 'templates4'
New-Item -ItemType Directory -Force -Path $tdir4 | Out-Null
$sheetOf = {
    param($labels, $extra)
    $x = '<sheetData>'
    for ($i = 0; $i -lt $labels.Count; $i++) { $r = $i + 2; $x += "<row r=`"$r`"><c r=`"A$r`" t=`"inlineStr`"><is><t>$($labels[$i])</t></is></c><c r=`"B$r`"><v>$($i + 1)</v></c></row>" }
    $x + $extra + '</sheetData>'
}
$result = { param($row, $label, $formula) "<row r=`"$row`"><c r=`"A$row`" t=`"inlineStr`"><is><t>$label</t></is></c><c r=`"B$row`"><f>$formula</f></c></row>" }
New-MiniBook (Join-Path $tdir4 'Split Calc.xlsx') ([ordered]@{
    'Input'  = & $sheetOf @('Width (in)', 'Depth (in)', 'Thick (in)', 'Fy (ksi)', 'Bolts', 'Edge (in)') ''
    'Calc'   = & $sheetOf @('Factor', 'Limit') (& $result 5 'Governing DCR' 'SUM(Input!B2:B7)*B2/B3/100+Shared!B2*0')
    'Other'  = & $sheetOf @('a (in)', 'b (in)', 'c (in)', 't (in)', 'n') (& $result 8 'DCR' 'SUM(B2:B6)/100+Shared!B2*0')
    'Shared' = & $sheetOf @('Es (ksi)') ''
    'Small'  = & $sheetOf @('P (kip)', 'Cap (kip)') (& $result 5 'DCR' 'B2/B3')
    'Tiny'   = & $sheetOf @('k') (& $result 4 'Area' 'B2*2')
    'Info'   = '<sheetData><row r="1"><c r="A1" t="inlineStr"><is><t>Read me</t></is></c></row></sheetData>'
}) ''
$proj4 = Join-Path $dir 'proj4'
$o4 = Invoke-CfCli new -Out $proj4 -Anl $fixture -Templates $tdir4
$new4 = $LASTEXITCODE
$wb4 = Join-Path $proj4 'CalcFlow.xlsx'

Test "a workbook's sheets become calcs by which sheet reads which: a tab per calc with the inputs of every sheet it reads, a shared sheet on each" {
    Assert-Equal $new4 0 ($o4 -join "`n")
    $b = [CalcFlow.Xlsx]::Read($wb4)
    Assert-Equal (@($b.TableRows('tblConfigTemplates') | ForEach-Object { "$($_['Template'])=$($_['Sheet'])" }) -join ' | ') 'Split-Calc Calc=Calc | Split-Calc Other=Other | Split-Calc Small=Small'
    $heads = { param($tab) $s = $b.Sheet($tab); $n = 1; $h = @(); while ($s.Text(4, $n)) { $h += $s.Text(4, $n); $n++ }; $h -join '|' }
    # Calc reads Input and Shared: its tab has its own two inputs, then theirs under the sheet's name
    Assert-Equal (& $heads 'Split-Calc Calc') 'Factor|Limit|Input: Width (in)|Input: Depth (in)|Input: Thick (in)|Input: Fy (ksi)|Input: Bolts|Input: Edge (in)|Shared: Es (ksi)'
    Assert-Equal (& $heads 'Split-Calc Other') 'a (in)|b (in)|c (in)|t (in)|n|Shared: Es (ksi)' 'Other reads Shared too, and nothing of Calc'
    Assert-Equal (& $heads 'Split-Calc Small') 'P (kip)|Cap (kip)' 'two inputs and a DCR: a small calc of its own'
    $cell = @($b.TableRows('tblTemplateCells') | Where-Object { $_['Template'] -eq 'Split-Calc Calc' -and $_['Column'] -eq 'Input: Width (in)' })[0]
    Assert-Equal "$($cell['Sheet'])!$($cell['Cell'])" 'Input!B2'
    # the Template Sheets tab: every sheet, and where its inputs went
    $rows = @{}; foreach ($r in $b.TableRows('tblTemplateSheets')) { $rows[$r['Sheet']] = $r }
    Assert-Equal (@('Input', 'Calc', 'Other', 'Shared', 'Small', 'Tiny', 'Info' | ForEach-Object { "$_=$($rows[$_]['Inputs']):$($rows[$_]['On tab'])" }) -join ' | ') 'Input=6:Split-Calc Calc | Calc=2:Split-Calc Calc | Other=5:Split-Calc Other | Shared=1:Split-Calc Calc, Split-Calc Other | Small=2:Split-Calc Small | Tiny=1: | Info=0:'
    Assert-Match $rows['Calc']['Says'] 'A calc tab of its own, which also holds the inputs of the sheets it works with: Input, Shared\.'
    Assert-Match $rows['Shared']['Says'] "Part of that calc: its inputs are columns of the tab, headed 'Shared: \.\.\.'"
    Assert-Match $rows['Tiny']['Says'] 'Its inputs are on no tab: .* add a row on Templates with this File and Sheet'
    Assert-Match $rows['Info']['Says'] '^No input found\.'
    Assert-True (@($o4 | Where-Object { $_ -like 'sheets *7 sheets in the template workbooks: 3 with a calc tab, 2 more whose inputs are on those tabs, 1 with inputs on no tab*Template Sheets*' }).Count -eq 1) ($o4 -join "`n")
}

Test 'a value typed for an input of another sheet of the calc reaches that sheet' {
    $filled4 = Join-Path $proj4 'CalcFlow-filled.xlsx'
    $b = [CalcFlow.Xlsx]::Read($wb4)
    $tab = 'Split-Calc Calc'
    $orow = @(2..40 | Where-Object { $b.Sheet('Outputs').Text($_, 0) -eq $tab })[0] + 1
    Save-Cells $wb4 $filled4 @(
        @('Connection Lib', 'A3', 'SC-1'), @('Connection Lib', 'B3', $tab),
        @($tab, 'A6', 'SC-1'), @($tab, ((Get-Col $b $tab 'Input: Width (in)' 5) + '6'), 12), @($tab, ((Get-Col $b $tab 'Shared: Es (ksi)' 5) + '6'), 30000), @($tab, ((Get-Col $b $tab 'Limit' 5) + '6'), 4),
        @('Model Config', 'F4', 'SC-1')
    )
    Assert-Equal $b.Sheet('Outputs').Text($orow - 1, 1) 'Y' 'the one governing DCR of the calc is chosen'
    $o = Invoke-CfCli check -Workbook $filled4
    Assert-Equal $LASTEXITCODE 0 ($o -join "`n")
    $plan = [CalcFlow.Csv]::ReadTable((Join-Path $proj4 'calcflow/plan/plan.csv'))
    Assert-Equal (@($plan | Where-Object { $_['Calc'] -eq 'J2-M2A' } | Sort-Object { "$($_['Sheet'])$($_['Cell'])" } | ForEach-Object { "$($_['Sheet'])!$($_['Cell'])=$($_['Value'])" }) -join ' ') 'Calc!B3=4 Input!B2=12 Shared!B2=30000'
    if (-not [CalcFlow.LibreOffice]::Find()) { Write-Host '        (no LibreOffice here: the calc itself is skipped)' -ForegroundColor DarkGray; return }
    $o = Invoke-CfCli calcs -Workbook $filled4 -Engine libreoffice
    Assert-Equal $LASTEXITCODE 0 ($o -join "`n")
    # Input!B2 = 12 in place of 1: (12 + 2 + 3 + 4 + 5 + 6) * 1 / 4 / 100
    $chk = @([CalcFlow.Csv]::ReadTable((Join-Path $proj4 'calcflow/data/results/J2-M2A__s1.checks.csv')))[0]
    Assert-Near ([double]$chk['Max DCR']) 0.08 1e-9
}
