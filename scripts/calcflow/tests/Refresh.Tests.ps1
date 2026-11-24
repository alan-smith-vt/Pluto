# Refresh.Tests.ps1 -- what happens after the workbook was set up: a template is added to the folder, the model has
# other beams, a template's inputs are given by hand. post-process and check say so, "refresh" rebuilds the workbook
# around what was filled in, and the Start tab checks the force units as they are typed.

$dir = New-TestDir 'refresh'
$fixture = Join-Path $script:Fixtures 'frame-demo.anl'
$tdir = Join-Path $dir 'templates'
New-Item -ItemType Directory -Force -Path $tdir | Out-Null
Copy-Item -Path (Join-Path (Join-Path $script:Fixtures 'templates') '*') -Destination $tdir -Recurse
$proj = Join-Path $dir 'proj'
$o = Invoke-CfCli new -Out $proj -Anl $fixture -Templates $tdir
$newExit = $LASTEXITCODE
$wbPath = Join-Path $proj 'CalcFlow.xlsx'
$weld = 'Rect. Fillet Weld Group'
$plate = 'Base_Plate_Anchorage Calc'

# cells written into the workbook itself, as Excel saves it
function Save-InPlace([string]$Path, [object[]]$Cells) {
    $tmp = $Path + '.tmp'
    Save-Cells $Path $tmp $Cells
    Move-Item -LiteralPath $tmp -Destination $Path -Force
}

# the column index of a heading on a tab
function Get-ColIndex($Book, [string]$Tab, [string]$Heading, [int]$Row) {
    $s = $Book.Sheet($Tab)
    for ($c = 0; $c -lt 300; $c++) { if ($s.Text($Row - 1, $c).Trim() -eq $Heading) { return $c } }
    throw "no heading '$Heading' on $Tab"
}

# the workbook as LibreOffice calculates it ($null: no LibreOffice here)
function Get-Recalculated([string]$File) {
    $so = [CalcFlow.LibreOffice]::Find()
    if (-not $so) { return $null }
    $out = Join-Path $dir ('recalc-' + [Guid]::NewGuid().ToString('N').Substring(0, 6))
    [void][CalcFlow.LibreOffice]::Convert($so, [string[]]@($File), $out, 'xlsx', $out, 180)
    return [CalcFlow.Xlsx]::Read((Join-Path $out ([IO.Path]::GetFileName($File))))
}

# the Start tab's unit check: tab -> "status|missing"
function Get-UnitCheck($Book) {
    $s = $Book.Sheet('Start')
    $top = @(0..80 | Where-Object { $s.Text($_, 0) -eq 'Units' -and $s.Text($_, 1) -eq 'Template tab' })[0]
    $h = @{}
    for ($r = $top + 1; $s.Text($r, 1).Length -gt 0; $r++) { $h[$s.Text($r, 1)] = "$($s.Text($r, 0))|$($s.Text($r, 3))" }
    return $h
}

Test 'the Start tab checks the force units as they are typed: Fix with a count, then OK' {
    Assert-Equal $newExit 0 ($o -join "`n")
    $u = Get-UnitCheck ([CalcFlow.Xlsx]::Read($wbPath))
    Assert-Equal "$($u[$weld]) $($u[$plate])" 'Not used|6 Not used|6' 'as written: six forces without a unit on each tab, and no connection on either yet'
    $b = [CalcFlow.TemplateBook]::Open($wbPath)
    $s = $b.Sheet('Start')
    $row = @(0..80 | Where-Object { $c = $s.Get($_, 1); $c -and $c.Value -eq $weld })[0]
    Assert-Match $s.Get($row, 3).Formula "^SUMPRODUCT\(ISNUMBER\(MATCH\('Rect\. Fillet Weld Group'!\`$B\`$2:\`$XFD\`$2,"
    Assert-Match $s.Get($row, 1).Formula "^HYPERLINK\(""#'Rect\. Fillet Weld Group'!A1"""
    $lo = Get-Recalculated $wbPath
    if (-not $lo) { Write-Host '        (no LibreOffice here: the formulas are not calculated)' -ForegroundColor DarkGray; return }
    $u = Get-UnitCheck $lo
    Assert-Equal "$($u[$weld]) $($u[$plate])" 'Not used|6 Not used|6' 'as calculated'
    # a mark on the weld template, and five of its six units typed: one is left to fix
    $bk = [CalcFlow.Xlsx]::Read($wbPath)
    $part = Join-Path $dir 'part.xlsx'
    $typed = @(@('Fx', 'Fy', 'Fz', 'Mx', 'My') | ForEach-Object { , @($weld, ((Get-Col $bk $weld $_ 5) + '3'), 'kip') })
    Save-Cells $wbPath $part ($typed + @(@('Connection Lib', 'A3', 'WELD-A'), @('Connection Lib', 'B3', $weld)))
    $u = Get-UnitCheck (Get-Recalculated $part)
    Assert-Equal "$($u[$weld]) $($u[$plate])" 'Fix|1 Not used|6'
}

# ---- what the engineer fills in, saved in the workbook itself
$b = [CalcFlow.Xlsx]::Read($wbPath)
$cells = New-Object System.Collections.Generic.List[object]
$cells.Add(@('Connection Lib', 'A3', 'WELD-A')); $cells.Add(@('Connection Lib', 'B3', $weld)); $cells.Add(@('Connection Lib', 'C3', 'beam end weld'))
$cells.Add(@('Connection Lib', 'A4', 'WELD-B')); $cells.Add(@('Connection Lib', 'B4', $weld))
$cells.Add(@('Connection Lib', 'A5', 'BP-A')); $cells.Add(@('Connection Lib', 'B5', $plate))
foreach ($f in 'Fx', 'Fy', 'Fz') { $cells.Add(@($weld, ((Get-Col $b $weld $f 5) + '3'), 'kip')); $cells.Add(@($plate, ((Get-Col $b $plate $f 5) + '3'), 'kip')) }
foreach ($f in 'Mx', 'My', 'Mz') { $cells.Add(@($weld, ((Get-Col $b $weld $f 5) + '3'), 'kip-in')); $cells.Add(@($plate, ((Get-Col $b $plate $f 5) + '3'), 'kip-ft')) }
$leg = Get-Col $b $weld 'wleg (in.)' 5
$fth = Get-Col $b $weld 'fth' 5
$cells.Add(@($weld, ($fth + '2'), "Template's own value"))
$cells.Add(@($weld, 'A6', 'WELD-A')); $cells.Add(@($weld, ($leg + '6'), 0.25))
$cells.Add(@($weld, 'A7', 'WELD-B')); $cells.Add(@($weld, ($leg + '7'), 0.5))
$cells.Add(@($plate, 'A6', 'BP-A'))
$out = $b.Sheet('Outputs')
for ($r = 2; $r -lt $out.Rows.Count; $r++) {
    if ($out.Text($r, 0) -eq $plate -and $out.Text($r, 8) -in 'Converged', 'Singular') {
        $cells.Add(@('Outputs', "B$($r + 1)", 'Y')); $cells.Add(@('Outputs', "D$($r + 1)", 'health'))
        $cells.Add(@('Outputs', "E$($r + 1)", $(if ($out.Text($r, 8) -eq 'Converged') { 'TRUE' } else { 'FALSE' })))
    }
}
$cells.Add(@('Model Config', 'F4', 'WELD-A')); $cells.Add(@('Model Config', 'G4', 'WELD-B')); $cells.Add(@('Model Config', 'F3', 'BP-A')); $cells.Add(@('Model Config', 'J4', 'check the weld access'))
$trow = @(2..40 | Where-Object { $b.Sheet('Templates').Text($_, 0) -eq $weld })[0] + 1
$cells.Add(@('Templates', "F$trow", 'LRFD'))
$lrow = @(2..40 | Where-Object { $b.Sheet('Load Cases').Text($_, 0) -eq '3' })[0] + 1
$cells.Add(@('Load Cases', "D$lrow", 'N'))
$st = $b.Sheet('Start')
$cells.Add(@('Start', ('B' + (@(0..60 | Where-Object { $st.Text($_, 0) -eq 'DCR limit' })[0] + 1)), 0.9))
$cells.Add(@('Start', ('B' + (@(0..60 | Where-Object { $st.Text($_, 0) -eq 'Anl file' })[0] + 1)), 'frame-demo.anl'))
Save-InPlace $wbPath $cells.ToArray()
Copy-Item -LiteralPath $fixture -Destination (Join-Path $proj 'frame-demo.anl')      # the "Anl file" setting names it
$before = Invoke-CfCli check -Workbook $wbPath
$beforeExit = $LASTEXITCODE

Test 'with the units typed the Start tab says OK; check is quiet about changes when nothing changed' {
    Assert-Equal $beforeExit 0 ($before -join "`n")
    Assert-True (@($before | Where-Object { $_ -like 'check *3 of 3 connection calcs are ready*' }).Count -eq 1) ($before -join "`n")
    Assert-True (@($before | Where-Object { $_ -like '*Refresh config*' }).Count -eq 0) 'no refresh is asked for'
    $lo = Get-Recalculated $wbPath
    if (-not $lo) { Write-Host '        (no LibreOffice here: skipped)' -ForegroundColor DarkGray; return }
    $u = Get-UnitCheck $lo
    Assert-Equal "$($u[$weld]) $($u[$plate]) $($u['I Fillet Weld Group'])" 'OK|0 OK|0 Not used|6'
}

# ---- a template is added to the folder; a beam is missing from Model Config, another is no longer in the model
$rowsXml = '<sheetData><row r="1">' + (@('Beam', 'Node', 'LC', 'Fx (kip)', 'Fy (kip)', 'Fz (kip)', 'Mx (kip-in)', 'My (kip-in)', 'Mz (kip-in)', 'DCR') | ForEach-Object -Begin { $i = 0 } -Process { $c = [CalcFlow.Xlsx]::ColName($i); $i++; "<c r=`"${c}1`" t=`"inlineStr`"><is><t>$_</t></is></c>" }) -join ''
$rowsXml += '<c r="M1" t="inlineStr"><is><t>Capacity (kip)</t></is></c><c r="N1"><v>100</v></c></row>'
for ($r = 2; $r -le 11; $r++) { $rowsXml += "<row r=`"$r`"><c r=`"J$r`"><f>IF(A$r=`"`",`"`",(ABS(D$r)+ABS(E$r)+ABS(F$r))/`$N`$1)</f></c></row>" }
$rowsXml += '</sheetData>'
New-MiniXlsx (Join-Path $tdir 'Beam Shear Batch.xlsx') $rowsXml ''
$b = [CalcFlow.Xlsx]::Read($wbPath)
$mc = $b.Sheet('Model Config')
$r7 = @(2..40 | Where-Object { $mc.Text($_, 0) -eq '7' })[0] + 1
Save-InPlace $wbPath @(@('Model Config', "A$r7", 99), @('Model Config', "F$r7", 'WELD-A'))

Test 'post-process and check say what changed: a template the workbook does not know, a beam without a row' {
    $o = Invoke-CfCli post-process -Workbook $wbPath -NoOpen
    Assert-Equal $LASTEXITCODE 0 ($o -join "`n")
    Assert-True (@($o | Where-Object { $_ -like '*`[note `] the templates folder has a template the workbook does not know: Beam Shear Batch.xlsx' }).Count -eq 1) ($o -join "`n")
    Assert-True (@($o | Where-Object { $_ -like '*1 beam(s) of the model have no row on Model Config: 7' }).Count -eq 1) ($o -join "`n")
    Assert-True (@($o | Where-Object { $_ -like '*"Refresh config" brings the workbook up to date and keeps what you have filled in.' }).Count -eq 1) ($o -join "`n")
    $c = Invoke-CfCli check -Workbook $wbPath
    Assert-Equal $LASTEXITCODE 1 'beam 99 is not in the model'
    Assert-True (@($c | Where-Object { $_ -like '*template the workbook does not know: Beam Shear Batch.xlsx' }).Count -eq 1) ($c -join "`n")
}

$ref = Invoke-CfCli refresh -Workbook $wbPath -NoWait -NoOpen
$refExit = $LASTEXITCODE

Test 'refresh: the new template gets its tab, the model its beams, and everything filled in is carried over' {
    Assert-Equal $refExit 0 ($ref -join "`n")
    $kept = @(Get-ChildItem -LiteralPath $proj -Filter 'CalcFlow (before refresh *).xlsx')
    Assert-Equal $kept.Count 1 'the workbook as it was is kept'
    Assert-True (@($ref | Where-Object { $_ -like '*new template: Beam-Shear-Batch (Beam Shear Batch.xlsx, sheet Calc)*' }).Count -eq 1) ($ref -join "`n")
    Assert-True (@($ref | Where-Object { $_ -like '*1 beam(s) of the model are new on Model Config: 7' }).Count -eq 1) ($ref -join "`n")
    Assert-True (@($ref | Where-Object { $_ -like '*beam 99 is no longer in the model: its row is dropped (it had WELD-A at end I)' }).Count -eq 1) ($ref -join "`n")
    $b = [CalcFlow.Xlsx]::Read($wbPath, 300)
    $tabs = @($b.Sheets | ForEach-Object { $_.Name })
    Assert-True ($tabs -contains 'Beam-Shear-Batch') 'the new tab'
    $t = @($b.TableRows('tblConfigTemplates'))
    $nt = @($t | Where-Object { $_['Template'] -eq 'Beam-Shear-Batch' })[0]
    Assert-Equal "$($nt['Load table at'])|$($nt['Max rows'])|$($nt['Force unit'])" 'A2|10|kip'
    Assert-Equal @($t | Where-Object { $_['Template'] -eq $weld })[0]['Basis'] 'LRFD' 'a Templates setting'
    Assert-Equal (@($b.TableRows('tblConnectionLib') | ForEach-Object { "$($_['Connection'])=$($_['Template'])" }) -join ' ') "WELD-A=$weld WELD-B=$weld BP-A=$plate"
    Assert-Equal @($b.TableRows('tblConnectionLib'))[0]['Description'] 'beam end weld'
    # the weld tab: its marks, their values, the units and the Source that were typed
    $s = $b.Sheet($weld)
    $lc = (Get-ColIndex $b $weld 'wleg (in.)' 5); $fc = (Get-ColIndex $b $weld 'fth' 5); $mz = (Get-ColIndex $b $weld 'Mz' 5)
    Assert-Equal "$($s.Text(5, 0)) $($s.Text(5, $lc)) | $($s.Text(6, 0)) $($s.Text(6, $lc))" 'WELD-A 0.25 | WELD-B 0.5'
    Assert-Equal "$($s.Text(1, $fc))|$($s.Text(2, $mz))|$($s.Text(1, $mz))" "Template's own value|kip-in|Mz"
    Assert-Equal ([CalcFlow.TemplateBook]::Open($wbPath).Sheet($weld).Get(5, $lc).Type) '' 'a typed number is a number again, not text'
    # Model Config: the connections and the comment; beam 7 back, beam 99 gone
    $mc = @($b.TableRows('tblModelConfig'))
    Assert-Equal (@($mc | ForEach-Object { $_['Beam ID'] }) -join ' ') '1 2 3 4 5 6 7'
    $b2 = @($mc | Where-Object { $_['Beam ID'] -eq '2' })[0]
    Assert-Equal "$($b2['Connection I']) $($b2['Connection J']) $($b2['Comment'])|$(@($mc | Where-Object { $_['Beam ID'] -eq '1' })[0]['Connection I'])" 'WELD-A WELD-B check the weld access|BP-A'
    # Outputs, Load Cases, the settings
    $health = @($b.TableRows('tblConfigOutputs') | Where-Object { $_['Template'] -eq $plate -and $_['Kind'] -eq 'health' -and $_['Use'] -eq 'Y' })
    Assert-Equal (@($health | ForEach-Object { "$(($_['Result'] -split ' ')[0])=$($_['Expect'])" } | Sort-Object) -join ' ') 'Converged=TRUE Singular=FALSE'
    Assert-Equal @($b.TableRows('tblConfigCases') | Where-Object { $_['LC'] -eq '3' })[0]['Use'] 'N'
    $set = [CalcFlow.EngineerWorkbook]::Settings($wbPath)
    Assert-Equal "$($set['DCR limit'])|$($set['Anl file'])|$($set['Templates folder'])" "0.9|frame-demo.anl|$tdir"
    Assert-True (Test-Path -LiteralPath (Join-Path $proj 'Refresh config.cmd')) 'the launcher'
    Assert-Match ([IO.File]::ReadAllText((Join-Path $proj 'Refresh config.cmd'))) 'CalcFlow\.ps1" refresh -Workbook "%~dp0CalcFlow\.xlsx"'
}

Test 'after the refresh check finds the same three calcs ready, and asks for no refresh' {
    $c = Invoke-CfCli check -Workbook $wbPath
    Assert-Equal $LASTEXITCODE 0 ($c -join "`n")
    Assert-True (@($c | Where-Object { $_ -like 'check *3 of 3 connection calcs are ready: 0 error(s)*' }).Count -eq 1) ($c -join "`n")
    Assert-True (@($c | Where-Object { $_ -like '*Refresh config*' }).Count -eq 0) ($c -join "`n")
}

Test 'a schedule column the template no longer has is kept at the end with its values, and said' {
    $b = [CalcFlow.Xlsx]::Read($wbPath, 300)
    $p4 = Join-Path $dir 'proj4'
    New-Item -ItemType Directory -Force -Path $p4 | Out-Null
    $w4 = Join-Path $p4 'CalcFlow.xlsx'
    Save-Cells $wbPath $w4 @(, @($weld, ((Get-Col $b $weld 'wleg (in.)' 5) + '5'), 'weld leg'))
    Copy-Item -LiteralPath (Join-Path $proj 'calcflow') -Destination $p4 -Recurse
    $o = Invoke-CfCli refresh -Workbook $w4 -NoWait -NoOpen
    Assert-Equal $LASTEXITCODE 0 ($o -join "`n")
    Assert-True (@($o | Where-Object { $_ -like "*tab ${weld}: column 'weld leg' is no longer an input of the template*" }).Count -eq 1) ($o -join "`n")
    $n = [CalcFlow.Xlsx]::Read($w4, 300)
    $s = $n.Sheet($weld)
    $old = (Get-ColIndex $n $weld 'weld leg' 5); $new = (Get-ColIndex $n $weld 'wleg (in.)' 5)
    Assert-Equal "$($s.Text(5, $old)) $($s.Text(6, $old))|$($s.Text(5, $new))$($s.Text(6, $new))" '0.25 0.5|'
    # a workbook that Excel has open is not touched
    $fs = [IO.File]::Open($w4, 'Open', 'ReadWrite', 'None')
    try {
        $o = Invoke-CfCli refresh -Workbook $w4 -NoWait -NoOpen
        Assert-Equal $LASTEXITCODE 2 ($o -join "`n")
        Assert-True (@($o | Where-Object { $_ -like '*is open in Excel: save and close it, then refresh.*' }).Count -eq 1) ($o -join "`n")
    }
    finally { $fs.Dispose() }
}

# ---- a template whose inputs and results the engineer gives: a block typed as one table
$t5 = Join-Path $dir 'templates5'
New-Item -ItemType Directory -Force -Path $t5 | Out-Null
$txt = { param($ref, $text) "<c r=`"$ref`" t=`"inlineStr`"><is><t>$text</t></is></c>" }
$x = '<sheetData>'
$x += '<row r="1">' + (& $txt 'A1' 'Plate width (in)') + '<c r="B1"><v>12</v></c>' + (& $txt 'D1' 'N anchors') + '<c r="E1"><f>COUNT(A5:A10)</f></c></row>'
$x += '<row r="2">' + (& $txt 'A2' 'Plate depth (in)') + '<c r="B2"><v>12</v></c>' + (& $txt 'D2' 'Depth seen') + '<c r="E2"><f>IF(B2="","none",B2)</f></c></row>'
$x += '<row r="3">' + (& $txt 'D3' 'Anchor DCR') + '<c r="E3"><f>(SUM(A5:B10)+ABS(B12))/(B1*100)</f></c></row>'
$x += '<row r="4">' + (& $txt 'A4' 'x (in)') + (& $txt 'B4' 'y (in)') + '</row>'
foreach ($r in 5..9) { $x += "<row r=`"$r`"><c r=`"A$r`"><v>$r</v></c><c r=`"B$r`"><v>1</v></c></row>" }
$x += '<row r="12">' + (& $txt 'A12' 'Fx (kip)') + '<c r="B12"><v>5</v></c></row>'
$x += '</sheetData>'
New-MiniXlsx (Join-Path $t5 'Anchor Plate.xlsx') $x ''
$p5 = Join-Path $dir 'proj5'
[void](Invoke-CfCli new -Out $p5 -Anl $fixture -Templates $t5)
$w5 = Join-Path $p5 'CalcFlow.xlsx'
$ap = 'Anchor-Plate'

Test "a template's Input cells and Result cells given by hand become its columns at a refresh; a block is one table column" {
    $b = [CalcFlow.Xlsx]::Read($w5)
    $s = $b.Sheet($ap)
    Assert-Equal (@(1..6 | ForEach-Object { $s.Text(4, $_) } | Where-Object { $_ }) -join '|') 'Plate width (in)|Plate depth (in)|Fx (kip)' 'what harvest finds: the anchor block is not an input to it'
    $ic = Get-Col $b 'Templates' 'Input cells' 2; $rc = Get-Col $b 'Templates' 'Result cells' 2
    Save-InPlace $w5 @(@('Templates', ($ic + '3'), 'B1, B2, B12, Anchors = table A5:B10'), @('Templates', ($rc + '3'), 'E1, E2, E3'))
    $c = Invoke-CfCli check -Workbook $w5
    Assert-True (@($c | Where-Object { $_ -like "*template ${ap}: its 'Input cells' are not the columns of its tab yet*" }).Count -eq 1) ($c -join "`n")
    $o = Invoke-CfCli refresh -Workbook $w5 -NoWait -NoOpen
    Assert-Equal $LASTEXITCODE 0 ($o -join "`n")
    $b = [CalcFlow.Xlsx]::Read($w5)
    $s = $b.Sheet($ap)
    Assert-Equal (@(1..6 | ForEach-Object { $s.Text(4, $_) } | Where-Object { $_ }) -join '|') 'Plate width (in)|Plate depth (in)|Fx (kip)|Anchors'
    Assert-Equal "$($s.Text(3, 4))|$($s.Text(1, 3))|$($s.Text(2, 3))" '5, 1; 6, 1; 7, 1; 8, 1; 9, 1|Fx|kip' "the block as the template holds it; the force column with the unit of its label"
    $cell = @($b.TableRows('tblTemplateCells') | Where-Object { $_['Column'] -eq 'Anchors' })[0]
    Assert-Equal "$($cell['Cell'])|$($cell['Kind'])" 'A5:B10|table'
    $outs = @($b.TableRows('tblConfigOutputs') | Where-Object { $_['Template'] -eq $ap })
    Assert-Equal (@($outs | ForEach-Object { "$($_['Result']):$($_['Kind']):$($_['Use']):$($_['Cell'])" }) -join ' ') 'Anchor DCR:dcr:Y:E3 N anchors:value:Y:E1 Depth seen:value:Y:E2'
}

Test 'a table typed in one cell fills its block per mark and empties the rest; (blank) empties a cell' {
    if (-not [CalcFlow.LibreOffice]::Find()) { Write-Host '        (no LibreOffice here: skipped)' -ForegroundColor DarkGray; return }
    $b = [CalcFlow.Xlsx]::Read($w5)
    $an = Get-Col $b $ap 'Anchors' 5; $wd = Get-Col $b $ap 'Plate width (in)' 5; $dp = Get-Col $b $ap 'Plate depth (in)' 5
    Save-InPlace $w5 @(
        @('Connection Lib', 'A3', 'AP-1'), @('Connection Lib', 'B3', $ap), @('Connection Lib', 'A4', 'AP-2'), @('Connection Lib', 'B4', $ap),
        @($ap, 'A6', 'AP-1'), @($ap, ($an + '6'), '2, 2; 2, 10; 10, 2; 10, 10'),
        @($ap, 'A7', 'AP-2'), @($ap, ($an + '7'), "3, 3`n3, 9"), @($ap, ($wd + '7'), 14), @($ap, ($dp + '7'), '(blank)'),
        @('Model Config', 'F4', 'AP-1'), @('Model Config', 'G4', 'AP-2')
    )
    $o = Invoke-CfCli calcs -Workbook $w5 -Engine libreoffice
    Assert-Equal $LASTEXITCODE 0 ($o -join "`n")
    Assert-True (@($o | Where-Object { $_ -like 'run      2 calc steps: 2 OK, 0 invalid, 0 failed*' }).Count -eq 1) ($o -join "`n")
    $work = Join-Path $p5 'calcflow'
    $a = @([CalcFlow.Csv]::ReadTable((Join-Path $work 'data/results/J2-M2A__s1.csv')))
    $c = @([CalcFlow.Csv]::ReadTable((Join-Path $work 'data/results/J3-M2B__s1.csv')))
    Assert-Equal "$($a[0]['N anchors']) $($a[0]['Depth seen'])|$($c[0]['N anchors']) $($c[0]['Depth seen'])" '4 12|2 none' 'four anchors and the plate as it is; two anchors, the rest of the block and the depth emptied'
    # the DCR follows the coordinates typed: (sum of the block + |Fx|) / (width x 100)
    $dem = @([CalcFlow.Csv]::ReadTable((Join-Path $work 'plan/demands.csv')) | Where-Object { $_['Calc'] -eq 'J3-M2B' })
    foreach ($r in $c) {
        $d = @($dem | Where-Object { $_['Row'] -eq $r['Row'] })[0]
        Assert-Near ([double]$r['Anchor DCR']) ((18 + [Math]::Abs([double]$d['Fx (kip)'])) / 1400) 1e-9 "row $($r['Row'])"
    }
    # a table too long for its block, or a row of another width, is said by check
    $bad = Join-Path $p5 'CalcFlow-bad.xlsx'
    Save-Cells $w5 $bad @(@($ap, ($an + '6'), '1,1; 2,2; 3,3; 4,4; 5,5; 6,6; 7,7'), @($ap, ($an + '7'), '3, 3, 3'))
    $o = Invoke-CfCli check -Workbook $bad
    Assert-Equal $LASTEXITCODE 1
    Assert-True (@($o | Where-Object { $_ -like "*row 6, column 'Anchors': its table has 7 rows; the template's block A5:B10 holds 6*" }).Count -eq 1) ($o -join "`n")
    Assert-True (@($o | Where-Object { $_ -like "*row 7, column 'Anchors': a row of its table holds 3 value(s) (3, 3, 3); the template's block A5:B10 is 2 wide*" }).Count -eq 1) ($o -join "`n")
}

Test 'the cells an engineer gives: a list of cells, ranges and named tables on any sheet' {
    $why = ''
    $p = [CalcFlow.ProjectBook]::ParseCells("B4, B6:B7; 'Bolt group'!C3, Anchors = table Sheet2!`$B`$5:`$C`$12", 'Calc', [ref]$why)
    Assert-Equal (@($p | ForEach-Object { $_ -join '/' }) -join ' ') 'Calc/B4// Calc/B6// Calc/B7// Bolt group/C3// Sheet2/B5:C12/table/Anchors'
    Assert-Equal $null ([CalcFlow.ProjectBook]::ParseCells('B4, weld leg', 'Calc', [ref]$why))
    Assert-Match $why "'weld leg' is not a cell or a range"
}
