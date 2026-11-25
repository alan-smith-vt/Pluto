# TemplateMap.Tests.ps1 -- what a template asks for, read from the cells filled yellow (cs\TemplateMap.cs), on three
# made-up templates of different layouts: a batch table under merged headings with two merged cells above it, a table
# of one row a member, and a sheet with a grid of positions, a row of cells under grouped headings and labelled values
# with their units beside them. Every name and number in them is invented for these tests.

$dir = New-TestDir 'templatemap'
$yellow = New-Object CalcFlow.XStyle; $yellow.Fill = 'FFFFFF99'
$pale = New-Object CalcFlow.XStyle; $pale.Fill = 'FFFFF2CC'
$blue = New-Object CalcFlow.XStyle; $blue.Fill = 'FFDDEBF7'
function Put($Sheet, [string]$At, $Value, $Style) {
    $col = 0; $row = 0
    [void][CalcFlow.Xlsx]::ParseRef($At, [ref]$col, [ref]$row)
    if ($Style) { $Sheet.Set($row, $col, (New-Object CalcFlow.XCell $Value, $Style)) } else { $Sheet.Set($row, $col, $Value) }
}
function Fill($Sheet, [string]$From, [string]$To, $Style) {
    $c1 = 0; $r1 = 0; $c2 = 0; $r2 = 0
    [void][CalcFlow.Xlsx]::ParseRef($From, [ref]$c1, [ref]$r1); [void][CalcFlow.Xlsx]::ParseRef($To, [ref]$c2, [ref]$r2)
    for ($r = $r1; $r -le $r2; $r++) { for ($c = $c1; $c -le $c2; $c++) { $Sheet.Set($r, $c, (New-Object CalcFlow.XCell '', $Style)) } }
}
function Merge($Sheet, [string]$Range, $Value, $Style) { $Sheet.Merges.Add($Range); Put $Sheet ($Range -split ':')[0] $Value $Style }

# ---- 1. a batch table: two merged cells to fill, a heading band of three rows, ten rows of yellow cells
$clip = New-Object CalcFlow.XOutSheet 'Clip'
Put $clip 'A1' 'Clip angle batch'
Put $clip 'A3' 'Girder:'; Merge $clip 'B3:C3' 'W12X26' $yellow; Put $clip 'C3' '' $yellow
Put $clip 'A4' 'Post:'; Merge $clip 'B4:C4' 'W8X31' $yellow; Put $clip 'C4' '' $yellow
Merge $clip 'A6:A8' 'Stiffened?'
Merge $clip 'B6:I6' 'End forces from the analysis'
Merge $clip 'B7:B8' 'Beam #'; Merge $clip 'C7:C8' 'Node #'
Put $clip 'D7' 'FX'; Put $clip 'D8' '(kip)'
$k = 0; foreach ($h in "FY`n(kip)", 'FZ (kip)', 'MX (kip-in)', 'MY (kip-in)', 'MZ (kip-in)') { $c = [CalcFlow.Xlsx]::ColName(4 + $k); Merge $clip "${c}7:${c}8" $h; $k++ }
Merge $clip 'J6:K6' 'Checks'; Merge $clip 'J7:J8' 'Shear ratio'; Merge $clip 'K7:K8' 'Bolt ratio'
Fill $clip 'A9' 'I18' $yellow
Put $clip 'A9' 'Yes' $yellow; Put $clip 'B9' 12 $yellow; Put $clip 'C9' 5 $yellow; Put $clip 'D9' -19.05 $yellow; Put $clip 'E9' 0.04 $yellow
foreach ($r in 9..18) {
    Put $clip "J$r" (New-Object CalcFlow.XFormula "IF(B$r=`"`",`"`",ABS(E$r)/Capacities!`$B`$2)", '')
    Put $clip "K$r" (New-Object CalcFlow.XFormula "IF(B$r=`"`",`"`",ABS(D$r)/Capacities!`$B`$3)", '')
}
Put $clip 'I5' 'Largest:'; Put $clip 'J5' (New-Object CalcFlow.XFormula 'MAX(J9:J18)', '0')
$caps = New-Object CalcFlow.XOutSheet 'Capacities'
Put $caps 'A2' 'Shear capacity (kip)'; Put $caps 'B2' 34.5; Put $caps 'A3' 'Bolt capacity (kip)'; Put $caps 'B3' 20
$clipPath = Join-Path $dir 'Clip Batch.xlsx'
[CalcFlow.XlsxWriter]::Write($clipPath, [CalcFlow.XOutSheet[]]@($clip, $caps), $null)

# ---- 2. one row a member, with a governing force
$brace = New-Object CalcFlow.XOutSheet 'Braces'
Put $brace 'F3' 'Worst:'; Put $brace 'G3' (New-Object CalcFlow.XFormula 'MAX(G6:G8)', '0')
$k = 0; foreach ($h in 'Shape', 'Grade', 'Slotted?', 'Span (in)', 'Member #', 'Axial (kip)', 'Ratio') { $c = [CalcFlow.Xlsx]::ColName($k); Merge $brace "${c}4:${c}5" $h; $k++ }
Fill $brace 'A6' 'F8' $pale
Put $brace 'A6' 'HSS4X4X1/4' $pale; Put $brace 'B6' 'A500' $pale; Put $brace 'C6' 'No' $pale; Put $brace 'D6' 60 $pale; Put $brace 'E6' 6 $pale; Put $brace 'F6' 8 $pale
foreach ($r in 6..8) { Put $brace "G$r" (New-Object CalcFlow.XFormula "IF(E$r=`"`",`"`",F$r/50)", '') }
$bracePath = Join-Path $dir 'Brace Batch.xlsx'
[CalcFlow.XlsxWriter]::Write($bracePath, [CalcFlow.XOutSheet[]]@($brace), $null)

# ---- 3. another layout altogether: a grid of positions, a row of cells under grouped headings, values with their
# own labels and units beside them, and the loads on another sheet
$plate = New-Object CalcFlow.XOutSheet 'Pattern'
Merge $plate 'C2:E2' 'Bolt pattern'
Put $plate 'C3' 'Bolt'; Put $plate 'D3' 'u (in)'; Put $plate 'E3' 'v (in)'
foreach ($i in 1..6) { $r = 3 + $i; Put $plate "C$r" "#$i"; Put $plate "D$r" (3 * (($i + 1) % 2)) $yellow; Put $plate "E$r" (4 * [Math]::Floor(($i - 1) / 2)) $yellow }
Merge $plate 'G3:G4' 'Mark'; Merge $plate 'H3:H4' 'Joint'; Merge $plate 'I3:I4' 'Face'
Merge $plate 'J3:K3' 'Clearances'; Put $plate 'J4' 'left (in)'; Put $plate 'K4' 'right (in)'
Put $plate 'G5' 'P-1' $yellow; Put $plate 'H5' 12 $yellow; Put $plate 'I5' 'North' $yellow; Put $plate 'J5' 6 $yellow; Put $plate 'K5' 4.5 $yellow
Put $plate 'G8' 't'; Put $plate 'H8' 0.75 $yellow; Put $plate 'I8' 'in'; Put $plate 'J8' 'Plate thickness'
Put $plate 'G9' 'd'; Put $plate 'H9' 0.875 $yellow; Put $plate 'I9' 'in'; Put $plate 'J9' 'Bolt diameter'
Put $plate 'G10' 'Fy'; Put $plate 'H10' 36 $yellow; Put $plate 'I10' 'ksi'; Put $plate 'J10' 'Yield strength'
Put $plate 'G12' 'Twice t'; Put $plate 'H12' (New-Object CalcFlow.XCell (New-Object CalcFlow.XFormula 'H8*2', '1.5'), $yellow)
Put $plate 'L3' 'Factor'; Put $plate 'M3' 1.25 $blue
$loads = New-Object CalcFlow.XOutSheet 'Loads'
Put $loads 'A1' 'Forces at the joint'
Put $loads 'A2' 'LC'; Put $loads 'B2' 'Fx (kip)'; Put $loads 'C2' 'Fy (kip)'; Put $loads 'D2' 'Mz (kip-ft)'
Fill $loads 'A3' 'D8' $yellow
Put $loads 'E2' 'Ratio'
foreach ($r in 3..8) { Put $loads "E$r" (New-Object CalcFlow.XFormula "IF(A$r=`"`",`"`",ABS(B$r)/(Pattern!`$H`$8*100))", '') }
$platePath = Join-Path $dir 'Plate.xlsx'
[CalcFlow.XlsxWriter]::Write($platePath, [CalcFlow.XOutSheet[]]@($plate, $loads), $null)

$say = { param($m) @($m.Blocks | ForEach-Object { $b = $_; "$($b.Sheet)!$($b.Range) $($b.Shape)$(if ($b.LoadRows) { ' (tool)' })" }) -join ' | ' }
$cols = { param($b) @($b.Columns | ForEach-Object { "$($_.Cells)=$($_.Heading)[$($_.Unit)]>$($_.FilledBy)" }) -join ' | ' }

Test 'a batch table under merged headings: one table of rows the tool writes, each column named across the heading rows; a merged cell counts once' {
    $m = [CalcFlow.TemplateMap]::Read($clipPath)
    Assert-Equal (& $say $m) 'Clip!B3:C3 Cell | Clip!B4:C4 Cell | Clip!A9:I18 Table (tool)'
    Assert-Equal "$($m.Blocks[0].Columns[0].Heading)|$($m.Blocks[0].Columns[0].Example)|$($m.Blocks[0].Columns[0].FilledBy)|$($m.Blocks[0].Columns[0].Notes)" 'Girder|W12X26|Typed per mark|merged cells B3:C3'
    $t = $m.Blocks[2]
    Assert-Equal $t.Rows 10
    Assert-Equal (& $cols $t) 'A9:A18=Stiffened?[]>Typed per beam end | B9:B18=Beam #[]>Beam number | C9:C18=Node #[]>Node | D9:D18=FX (kip)[kip]>Fx | E9:E18=FY (kip)[kip]>Fy | F9:F18=FZ (kip)[kip]>Fz | G9:G18=MX (kip-in)[kip-in]>Mx | H9:H18=MY (kip-in)[kip-in]>My | I9:I18=MZ (kip-in)[kip-in]>Mz'
    Assert-Equal "$($t.Columns[3].Group)|$($t.Columns[8].Group)|$($t.Columns[0].Group)" 'End forces from the analysis|End forces from the analysis|' 'the heading merged over the group; other text higher up is not a heading'
    Assert-Equal $t.Columns[0].Example 'Yes'
    # the capacities a formula reads are not yellow: no input, on either sheet
    Assert-Equal @($m.Blocks | Where-Object { $_.Sheet -eq 'Capacities' }).Count 0
}

Test 'one row a member: the table is the tool''s, a pale yellow counts, an axial force is offered as Fx and said to be a guess' {
    $m = [CalcFlow.TemplateMap]::Read($bracePath)
    Assert-Equal (& $say $m) 'Braces!A6:F8 Table (tool)'
    Assert-Equal (& $cols $m.Blocks[0]) 'A6:A8=Shape[]>Typed per beam end | B6:B8=Grade[]>Typed per beam end | C6:C8=Slotted?[]>Typed per beam end | D6:D8=Span (in)[in]>Typed per beam end | E6:E8=Member #[]>Beam number | F6:F8=Axial (kip)[kip]>Fx'
    Assert-Equal "$($m.Blocks[0].Columns[5].Notes)|$($m.Blocks[0].Columns[5].Group)" 'Fx is a guess from the word axial|' 'the label of the summary cell above is not its heading'
}

Test 'a sheet of another layout: a grid typed for the mark, a row of cells under grouped headings, values with their own labels and units, loads on another sheet' {
    $m = [CalcFlow.TemplateMap]::Read($platePath)
    Assert-Equal (& $say $m) 'Pattern!D4:E9 Table | Pattern!G5:K5 Row of cells | Pattern!H8 Cell | Pattern!H9 Cell | Pattern!H10 Cell | Loads!A3:D8 Table (tool)'
    Assert-Equal (& $cols $m.Blocks[0]) 'D4:D9=u (in)[in]>Typed per mark (a table) | E4:E9=v (in)[in]>Typed per mark (a table)'
    Assert-Equal (& $cols $m.Blocks[1]) 'G5=Mark[]>Typed per mark | H5=Joint[]>Node | I5=Face[]>Typed per mark | J5=left (in)[in]>Typed per mark | K5=right (in)[in]>Typed per mark'
    Assert-Equal $m.Blocks[1].Columns[3].Group 'Clearances'
    Assert-Equal (@($m.Blocks[2..4] | ForEach-Object { $c = $_.Columns[0]; "$($c.Heading)=$($c.Example) $($c.Unit)" }) -join ' | ') 't=0.75 in | d=0.875 in | Fy=36 ksi'
    Assert-Equal (& $cols $m.Blocks[5]) 'A3:A8=LC[]>Load case | B3:B8=Fx (kip)[kip]>Fx | C3:C8=Fy (kip)[kip]>Fy | D3:D8=Mz (kip-ft)[kip-ft]>Mz'
    # a yellow cell that holds a formula is said, and is no input; a blue cell is listed as a fill not taken
    Assert-Match ($m.Notes -join '; ') 'Pattern: 1 cell\(s\) with the input fill FFFF99 hold a formula and are not inputs'
    $bluefill = @($m.Fills | Where-Object { $_.Fill -eq 'DDEBF7' })[0]
    Assert-Equal "$($bluefill.Cells)|$($bluefill.Input)|$($bluefill.Example)" '1|False|M3'
}

Test 'the input colour is the yellow family, whether typed as a colour, picked from the theme or from the old palette' {
    foreach ($c in 'FFFF00', 'FFFF99', 'FFFFCC', 'FFF2CC', 'FFE699', 'FFC000', 'E6DF9A') { Assert-True ([CalcFlow.TemplateLook]::IsInputColour($c)) $c }
    foreach ($c in 'FFFFFF', 'F2F2F2', 'DDEBF7', 'E2EFDA', 'FCE4D6', 'C6EFCE', 'FFC7CE', 'D9D9D9', '000000', 'FBFBF2', 'EAF3FF') { Assert-True (-not [CalcFlow.TemplateLook]::IsInputColour($c)) $c }
    Assert-Equal ([CalcFlow.TemplateLook]::Tint('FFC000', 0.8)) 'FFF2CC' 'a theme gold made 80% lighter, as Excel does it'
    Assert-Equal ([CalcFlow.TemplateLook]::Tint('FFC000', -0.25)) 'BF9000'
    # a workbook whose yellow comes from the theme with a tint, and from the palette by index
    $styles = '<?xml version="1.0" encoding="UTF-8"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><fills count="4"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill>' +
        '<fill><patternFill patternType="solid"><fgColor theme="7" tint="0.79998168889431442"/><bgColor indexed="64"/></patternFill></fill><fill><patternFill patternType="solid"><fgColor indexed="43"/></patternFill></fill></fills>' +
        '<cellXfs count="3"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/><xf numFmtId="0" fontId="0" fillId="2" borderId="0" applyFill="1"/><xf numFmtId="0" fontId="0" fillId="3" borderId="0" applyFill="1"/></cellXfs></styleSheet>'
    $theme = '<?xml version="1.0" encoding="UTF-8"?><a:theme xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" name="T"><a:themeElements><a:clrScheme name="T"><a:dk1><a:sysClr val="windowText" lastClr="000000"/></a:dk1><a:lt1><a:sysClr val="window" lastClr="FFFFFF"/></a:lt1><a:dk2><a:srgbClr val="44546A"/></a:dk2><a:lt2><a:srgbClr val="E7E6E6"/></a:lt2><a:accent1><a:srgbClr val="4472C4"/></a:accent1><a:accent2><a:srgbClr val="ED7D31"/></a:accent2><a:accent3><a:srgbClr val="A5A5A5"/></a:accent3><a:accent4><a:srgbClr val="FFC000"/></a:accent4><a:accent5><a:srgbClr val="5B9BD5"/></a:accent5><a:accent6><a:srgbClr val="70AD47"/></a:accent6><a:hlink><a:srgbClr val="0563C1"/></a:hlink><a:folHlink><a:srgbClr val="954F72"/></a:folHlink></a:clrScheme></a:themeElements></a:theme>'
    $p = Join-Path $dir 'Themed.xlsx'
    New-MiniBook $p ([ordered]@{ 'Calc' = '<sheetData><row r="2"><c r="A2" t="inlineStr"><is><t>Width (in)</t></is></c><c r="B2" s="1"><v>4</v></c></row><row r="3"><c r="A3" t="inlineStr"><is><t>Depth (in)</t></is></c><c r="B3" s="2"/></row><row r="4"><c r="A4" t="inlineStr"><is><t>Plain</t></is></c><c r="B4"><v>1</v></c></row></sheetData>' }) '' @{ 'xl/styles.xml' = $styles; 'xl/theme/theme1.xml' = $theme }
    $m = [CalcFlow.TemplateMap]::Read($p)
    Assert-Equal (@($m.Fills | ForEach-Object { "$($_.Fill):$($_.Input)" }) -join ' ') 'FFF2CC:True FFFF99:True'
    Assert-Equal (@($m.Blocks | ForEach-Object { "$($_.Range)=$($_.Columns[0].Heading)" }) -join ' | ') 'B2=Width (in) | B3=Depth (in)' 'an empty yellow cell is an input too; the plain one is not'
}

Test 'what cannot be seen is not an input: a hidden row, a hidden column, a hidden sheet' {
    $styles = '<?xml version="1.0" encoding="UTF-8"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><fills count="3"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill>' +
        '<fill><patternFill patternType="solid"><fgColor rgb="FFFFFF99"/></patternFill></fill></fills><cellXfs count="2"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/><xf numFmtId="0" fontId="0" fillId="2" borderId="0" applyFill="1"/></cellXfs></styleSheet>'
    $cell = { param($at, $label) $r = $at.Substring(1); "<row r=`"$r`"$(if ($at -eq 'B4') { ' hidden="1"' })><c r=`"A$r`" t=`"inlineStr`"><is><t>$label</t></is></c><c r=`"B$r`" s=`"1`"><v>1</v></c><c r=`"D$r`" s=`"1`"><v>2</v></c></row>" }
    $shown = '<cols><col min="4" max="4" width="9" hidden="1"/></cols><sheetData>' + (& $cell 'B2' 'Width (in)') + (& $cell 'B3' 'Depth (in)') + (& $cell 'B4' 'Unseen (in)') + '</sheetData>'
    $p = Join-Path $dir 'Hidden.xlsx'
    New-MiniBook $p ([ordered]@{ 'Calc' = $shown; 'Lists (hidden)' = '<sheetData><row r="2"><c r="B2" s="1"><v>5</v></c></row></sheetData>' }) '' @{ 'xl/styles.xml' = $styles }
    $m = [CalcFlow.TemplateMap]::Read($p)
    Assert-Equal (@($m.Blocks | ForEach-Object { "$($_.Sheet)!$($_.Range)=$($_.Columns[0].Heading)" }) -join ' | ') 'Calc!B2=Width (in) | Calc!B3=Depth (in)' 'row 4 and column D are hidden, and so is the sheet Lists'
    Assert-Match ($m.Notes -join '; ') 'Calc: 4 cell\(s\) with the input fill sit in hidden rows or columns and are left out'
    Assert-Match ($m.Notes -join '; ') 'Lists: a hidden sheet, not read'
}

Test 'a first answer is only offered where the heading and the value agree: Beam over a shape is typed, Beam # over a number is the beam' {
    $s = New-Object CalcFlow.XOutSheet 'Calc'
    Put $s 'A2' 'Beam:'; Put $s 'B2' 'W8X31' $yellow
    Put $s 'A3' 'Beam #'; Put $s 'B3' 12 $yellow
    Put $s 'A4' 'Node'; Put $s 'B4' '' $yellow
    Put $s 'A5' 'Fy (ksi)'; Put $s 'B5' 50 $yellow
    Put $s 'A6' 'Fy (kip)'; Put $s 'B6' 3.5 $yellow
    Put $s 'B8' 7 $yellow
    $p = Join-Path $dir 'Answers.xlsx'
    [CalcFlow.XlsxWriter]::Write($p, [CalcFlow.XOutSheet[]]@($s), $null)
    $m = [CalcFlow.TemplateMap]::Read($p)
    Assert-Equal (@($m.Blocks | ForEach-Object { $c = $_.Columns[0]; "$($c.Heading)>$($c.FilledBy)>$($c.Check)" }) -join ' | ') 'Beam>Typed per mark> | Beam #>Beam number>taken from the model by its heading | Node>Node>taken from the model by its heading | Fy (ksi)>Typed per mark> | Fy (kip)>Fy>taken from the model by its heading | >Typed per mark>no heading found: named by its cell'
    Assert-Equal "$($m.Blocks[0].Columns[0].LabelCell) $($m.Blocks[4].Columns[0].LabelCell)" 'A2 A6' 'the cell each heading was read from'
    # the fills of a real library: the yellows are inputs, black and the blues are not
    Assert-Equal (@('FFF2CC', '000000', 'FFF2C9', 'FFEEB9', '4472C4', 'FFFF00', 'B4C7E7' | ForEach-Object { "$_=$([CalcFlow.TemplateLook]::IsInputColour($_))" }) -join ' ') 'FFF2CC=True 000000=False FFF2C9=True FFEEB9=True 4472C4=False FFFF00=True B4C7E7=False'
}

Test 'a library whose inputs are not yellow: the fill is named, and only its cells are inputs' {
    $m = [CalcFlow.TemplateMap]::Read($platePath, [string[]]@('#ddebf7'))
    Assert-Equal (& $say $m) 'Pattern!M3 Cell'
    Assert-Equal "$($m.Blocks[0].Columns[0].Heading)=$($m.Blocks[0].Columns[0].Example)" 'Factor=1.25'
    Assert-Throws { [CalcFlow.TemplateMap]::Read($platePath, [string[]]@('yellow')) } 'six hex digits'
    $o = Invoke-CfCli map -Templates $platePath -Out (Join-Path $dir 'out-blue') -InputFill DDEBF7
    Assert-Equal $LASTEXITCODE 0 ($o -join "`n")
    Assert-True (@($o | Where-Object { $_ -like 'found *1 cell(s) or rows of cells, 0 list(s) or table(s)*' }).Count -eq 1) ($o -join "`n")
}

Test 'map writes the look at a templates folder: a row per input cell or table column, and every fill found' {
    $out = Join-Path $dir 'out'
    $tdir = Join-Path $dir 'folder'
    New-Item -ItemType Directory -Force -Path $tdir | Out-Null
    foreach ($f in $clipPath, $bracePath, $platePath) { Copy-Item -LiteralPath $f -Destination $tdir }
    $o = Invoke-CfCli map -Templates $tdir -Out $out
    Assert-Equal $LASTEXITCODE 0 ($o -join "`n")
    Assert-True (@($o | Where-Object { $_ -like 'found *Clip Batch.xlsx: 2 cell(s) or rows of cells, 1 list(s) or table(s) (1 written by the tool); 0 cell(s) of other fills not taken' }).Count -eq 1) ($o -join "`n")
    Assert-True (@($o | Where-Object { $_ -like '*Plate.xlsx: Pattern: 1 cell(s) with the input fill*hold a formula*' }).Count -eq 1) ($o -join "`n")
    $wb = [CalcFlow.Xlsx]::Read((Join-Path $out 'Template_Map.xlsx'))
    $rows = @($wb.TableRows('tblMapInputs'))
    Assert-Equal $rows.Count (6 + (2 + 9) + (2 + 5 + 3 + 4)) 'brace 6, clip 2 + 9, plate 2 + 5 + 3 + 4'
    $fx = @($rows | Where-Object { $_['Template'] -eq 'Clip Batch.xlsx' -and $_['Heading'] -eq 'FX (kip)' })[0]
    Assert-Equal "$($fx['Block'])|$($fx['Shape'])|$($fx['Cells'])|$($fx['Under'])|$($fx['Unit'])|$($fx['Filled by'])" 'A9:I18|Table of 10 rows, written by the tool|D9:D18|End forces from the analysis|kip|Fx'
    Assert-Equal @($wb.TableRows('tblMapFills') | Where-Object { $_['Taken as inputs'] -eq 'N' }).Count 1
    # for a library of many templates: a count per template sheet, and the few rows to look at marked
    $sum = @{}; foreach ($r in $wb.TableRows('tblMapTemplates')) { $sum["$($r['Template'])|$($r['Sheet'])"] = "$($r['Single inputs'])/$($r['Tables to type'])/$($r['Tables the tool writes'])/$($r['To look at'])" }
    Assert-Equal "$($sum['Clip Batch.xlsx|Clip']) $($sum['Brace Batch.xlsx|Braces']) $($sum['Plate.xlsx|Pattern']) $($sum['Plate.xlsx|Loads'])" '2/0/1/0 0/0/1/1 8/1/0/1 0/0/1/0'
    Assert-Equal (@($rows | Where-Object { $_['Look at'] } | ForEach-Object { "$($_['Heading']): $($_['Look at'])" }) -join ' | ') 'Axial (kip): a guess | Joint: taken from the model by its heading'
}

# ---- the whole way with templates read from their yellow cells: the tabs, the marks, the beam ends, the calcs
$lib = Join-Path $dir 'library'
New-Item -ItemType Directory -Force -Path $lib | Out-Null
foreach ($f in $clipPath, $bracePath, $platePath) { Copy-Item -LiteralPath $f -Destination $lib }
$proj = Join-Path $dir 'proj'
$fixtureAnl = Join-Path $script:Fixtures 'frame-demo.anl'
$oNew = Invoke-CfCli new -Out $proj -Anl $fixtureAnl -Templates $lib
$newExit = $LASTEXITCODE
$wb = Join-Path $proj 'CalcFlow.xlsx'
function Set-Cells([string]$Path, [object[]]$Cells) { $tmp = $Path + '.tmp'; Save-Cells $Path $tmp $Cells; Move-Item -LiteralPath $tmp -Destination $Path -Force }
$headsOf = { param($book, $tab) $s = $book.Sheet($tab); $h = @(); $n = 1; while ($s.Text(4, $n)) { $h += "$($s.Text(4, $n))<$($s.Text(1, $n))" ; $n++ }; $h -join ' | ' }

Test 'new on templates with yellow cells: a tab holds the cells typed once, then the columns of the table the tool writes with what fills each; its ratios are results, one a row' {
    Assert-Equal $newExit 0 ($oNew -join "`n")
    $b = [CalcFlow.Xlsx]::Read($wb)
    $t = @{}; foreach ($r in $b.TableRows('tblConfigTemplates')) { $t[$r['Template']] = $r }
    Assert-Equal (@($t.Keys | Sort-Object) -join ' ') 'Brace-Batch Clip-Batch Plate'
    $c = $t['Clip-Batch']
    Assert-Equal "$($c['Sheet'])|$($c['Load table at'])|$($c['Load table columns'])|$($c['Max rows'])|$($c['Force unit'])|$($c['Moment unit'])|$($c['Loads'])|$($c['Beam ends tab'])" 'Clip|A9|[Stiffened?], Beam, Node, Fx, Fy, Fz, Mx, My, Mz|10|kip|kip-in|All cases|Clip-Batch ends'
    Assert-Equal $t['Brace-Batch']['Load table columns'] '[Shape], [Grade], [Slotted?], [Span (in)], Beam, Fx'
    Assert-Equal "$($t['Plate']['Sheet'])|$($t['Plate']['Load table columns'])|$($t['Plate']['Beam ends tab'])" 'Loads|LC, Fx, Fy, Mz|' 'the plate is filled in on its Pattern sheet and takes its loads on another'
    # typed once per mark: the two merged cells of the clip, and every cell of the pattern sheet under its own heading
    Assert-Equal (& $headsOf $b 'Clip-Batch') 'Girder<Typed here | Post<Typed here | Stiffened?<Typed per beam end | Beam #<Beam number | Node #<Node | FX (kip)<Fx | FY (kip)<Fy | FZ (kip)<Fz | MX (kip-in)<Mx | MY (kip-in)<My | MZ (kip-in)<Mz'
    # a template whose only yellow cells are its table still shows what it takes: the table's headings, each with what fills it
    Assert-Equal (& $headsOf $b 'Brace-Batch') 'Shape<Typed per beam end | Grade<Typed per beam end | Slotted?<Typed per beam end | Span (in)<Typed per beam end | Member #<Beam number | Axial (kip)<Fx'
    Assert-Match $b.Sheet('Brace-Batch').Text(0, 0) '^This template is a table of load rows: a mark is ONE copy of it'
    Assert-Equal (& $headsOf $b 'Plate') 'Bolt pattern<Typed here | Mark<Typed here | Joint<Node | Face<Typed here | left (in)<Typed here | right (in)<Typed here | t<Typed here | d<Typed here | Fy<Typed here | LC<Load case | Fx (kip)<Fx | Fy (kip)<Fy | Mz (kip-ft)<Mz'
    # a column of the table has its unit and the template's own first value above its heading, like any other
    $bs = $b.Sheet('Brace-Batch')
    Assert-Equal "$($bs.Text(2, 6))|$($bs.Text(3, 6))|$($bs.Text(3, 1))" 'kip|8|HSS4X4X1/4'
    $lc = @($b.TableRows('tblTemplateCells') | Where-Object { $_['Kind'] -eq 'load column' -and $_['Template'] -eq 'Brace-Batch' } | ForEach-Object { "$($_['Column'])@$($_['Cell'])" })
    Assert-Equal ($lc -join ' ') 'Shape@A6 Grade@B6 Slotted?@C6 Span (in)@D6 Member #@E6 Axial (kip)@F6'
    Assert-Equal @($b.TableRows('tblTemplateSheets') | Where-Object { $_['Sheet'] -eq 'Braces' })[0]['Inputs'] '6' 'the columns of its table count as its inputs'
    $block = @($b.TableRows('tblTemplateCells') | Where-Object { $_['Column'] -eq 'Bolt pattern' })[0]
    Assert-Equal "$($block['Sheet'])!$($block['Cell'])|$($block['Kind'])|$($block['Template value'])" 'Pattern!D4:E9|table|0, 0; 3, 0; 0, 4; 3, 4; 0, 8; 3, 8' 'the grid is one cell to type a table in'
    $outs = @($b.TableRows('tblConfigOutputs') | ForEach-Object { "$($_['Template']):$($_['Result'])@$($_['Sheet'])!$($_['Cell']) use=$($_['Use']) check=$($_['Check']) row=$($_['Per row'])" })
    Assert-Equal ($outs -join ' | ') 'Brace-Batch:Ratio@Braces!G6 use=Y check=Ratio row=Y | Clip-Batch:Shear ratio@Clip!J9 use=Y check=Shear ratio row=Y | Clip-Batch:Bolt ratio@Clip!K9 use=Y check=Bolt ratio row=Y | Plate:Ratio@Loads!E3 use=Y check=Ratio row=Y'
    # nothing was guessed from formulas: the capacities the clip's formulas read are on no tab
    Assert-Equal @($b.TableRows('tblTemplateCells') | Where-Object { $_['Sheet'] -eq 'Capacities' }).Count 0
}

$b0 = [CalcFlow.Xlsx]::Read($wb)
$loadsCol = Get-Col $b0 'Templates' 'Loads' 2
$braceRow = @(2..12 | Where-Object { $b0.Sheet('Templates').Text($_, 0) -eq 'Brace-Batch' })[0] + 1
Set-Cells $wb @(
    @('Connection Lib', 'A3', 'CL-1'), @('Connection Lib', 'B3', 'Clip-Batch'), @('Connection Lib', 'A4', 'BR-1'), @('Connection Lib', 'B4', 'Brace-Batch'), @('Connection Lib', 'A5', 'PL-1'), @('Connection Lib', 'B5', 'Plate'),
    @('Clip-Batch', 'A6', 'CL-1'), @('Brace-Batch', 'A6', 'BR-1'), @('Plate', 'A6', 'PL-1'), @('Plate', ((Get-Col $b0 'Plate' 't' 5) + '6'), 1),
    # the clip at beam 2 end I and beam 5 end J, the brace at beam 4 end I, the plate under column 1
    @('Model Config', 'F4', 'CL-1'), @('Model Config', 'G7', 'CL-1'), @('Model Config', 'F6', 'BR-1'), @('Model Config', 'F3', 'PL-1'),
    @('Templates', "$loadsCol$braceRow", 'Largest of each force')
)

Test 'a beam end that calls a mark needs its row of values typed per beam end: check says to refresh, and refresh lists the beam ends' {
    $o = Invoke-CfCli check -Workbook $wb
    Assert-Equal $LASTEXITCODE 1 ($o -join "`n")
    Assert-Match ($o -join "`n") '\[fix  \] Clip-Batch ends: beam 2, end I calls CL-1 and has no row on this tab: run Refresh config, then fill its row in'
    $o = Invoke-CfCli refresh -Workbook $wb -NoWait -NoOpen
    Assert-Equal $LASTEXITCODE 0 ($o -join "`n")
    $b = [CalcFlow.Xlsx]::Read($wb)
    Assert-Equal (@($b.TableRows('tblEnds2') | ForEach-Object { "$($_['Beam'])$($_['End'])@$($_['Node'])=$($_['Connection'])" }) -join ' ') '2I@2=CL-1 5J@5=CL-1'
    $e1 = @($b.TableRows('tblEnds1'))
    Assert-Equal "$($e1.Count) $($e1[0]['Beam'])$($e1[0]['End']) $(@($e1[0].Keys | Where-Object { $_ -ne '__row' }) -join ',')" '1 4I Beam,End,Node,Connection,Shape,Grade,Slotted?,Span (in)'
    Assert-Equal ([CalcFlow.XlsxCheck]::Tables($wb) + [CalcFlow.XlsxCheck]::Validations($wb)).Count 0
}

Set-Cells $wb @(
    @('Clip-Batch ends', 'E3', 'Yes'), @('Clip-Batch ends', 'E4', 'No'),
    @('Brace-Batch ends', 'E3', 'HSS4X4X1/4'), @('Brace-Batch ends', 'F3', 'A500'), @('Brace-Batch ends', 'G3', 'No'), @('Brace-Batch ends', 'H3', 60)
)

Test 'check: each row of a table the tool writes carries its beam end''s own values; a refresh keeps what was typed per beam end' {
    $o = Invoke-CfCli refresh -Workbook $wb -NoWait -NoOpen
    Assert-Equal $LASTEXITCODE 0 ($o -join "`n")
    Assert-Equal (@([CalcFlow.Xlsx]::Read($wb).TableRows('tblEnds2') | ForEach-Object { $_['Stiffened?'] }) -join ' ') 'Yes No'
    $o = Invoke-CfCli check -Workbook $wb
    Assert-Equal $LASTEXITCODE 0 ($o -join "`n")
    Assert-True (@($o | Where-Object { $_ -like 'check *3 of 3 connection calcs are ready: 0 error(s)*' }).Count -eq 1) ($o -join "`n")
    Assert-True (@($o | Where-Object { $_ -like '*NO_GUARD*' }).Count -eq 0) 'under headings merged over two rows each table column still has its heading as a guard'
    $cases = @([CalcFlow.Csv]::ReadTable((Join-Path $proj 'calcflow/plan/cases.csv')))
    $cell = { param($calc, $row, $at) @($cases | Where-Object { $_['Calc'] -eq $calc -and $_['Row'] -eq "$row" -and $_['Cell'] -eq $at })[0]['Value'] }
    # the clip: two beam ends, three cases each; rows 1 to 3 are beam 2 at node 2, rows 4 to 6 beam 5 at node 5
    Assert-Equal "$(& $cell 'CL-1' 1 'A9') $(& $cell 'CL-1' 1 'B9') $(& $cell 'CL-1' 1 'C9') $(& $cell 'CL-1' 1 'E9') | $(& $cell 'CL-1' 4 'A9') $(& $cell 'CL-1' 4 'B9') $(& $cell 'CL-1' 4 'C9')" 'Yes 2 2 3.731 | No 5 5'
    # the brace: one row a beam end, the largest of each force
    $br = @($cases | Where-Object { $_['Calc'] -eq 'BR-1' })
    Assert-Equal (@($br | Sort-Object { $_['Cell'] } | ForEach-Object { "$($_['Cell'])=$($_['Value'])" }) -join ' ') 'A6=HSS4X4X1/4 B6=A500 C6=No D6=60 E6=4 F6=47.488'
    Assert-Equal (@($br | ForEach-Object { $_['Row'] } | Sort-Object -Unique) -join ',') '1'
    # the plate: its node from the model and the typed thickness on the Pattern sheet, its loads on the Loads sheet
    $plan = @([CalcFlow.Csv]::ReadTable((Join-Path $proj 'calcflow/plan/plan.csv')) | Where-Object { $_['Calc'] -eq 'PL-1' })
    Assert-Equal (@($plan | Sort-Object { $_['Cell'] } | ForEach-Object { "$($_['Sheet'])!$($_['Cell'])=$($_['Value'])" }) -join ' ') 'Pattern!H5=1 Pattern!H8=1'
    Assert-Equal "$(& $cell 'PL-1' 1 'A3') $(& $cell 'PL-1' 1 'B3')" '3 32.224'
}

Test 'what fills a column of the template''s table is said on its tab: check reads the Source and Unit rows there, and Refresh config repeats them on Templates' {
    $p2 = Join-Path $dir 'proj-source'
    Copy-Item -LiteralPath $proj -Destination $p2 -Recurse
    $w2 = Join-Path $p2 'CalcFlow.xlsx'
    $b = [CalcFlow.Xlsx]::Read($w2)
    $fz = Get-Col $b 'Clip-Batch' 'FZ (kip)' 5; $st = Get-Col $b 'Clip-Batch' 'Stiffened?' 5; $nd = Get-Col $b 'Clip-Batch' 'Node #' 5; $fx = Get-Col $b 'Clip-Batch' 'FX (kip)' 5
    # a column left to the template, and the one typed per beam end no longer typed
    Set-Cells $w2 @(@('Clip-Batch', "${fz}2", "Template's own value"), @('Clip-Batch', "${st}2", "Template's own value"))
    $o = Invoke-CfCli check -Workbook $w2
    Assert-Equal $LASTEXITCODE 0 ($o -join "`n")
    $cases = @([CalcFlow.Csv]::ReadTable((Join-Path $p2 'calcflow/plan/cases.csv')) | Where-Object { $_['Calc'] -eq 'CL-1' -and $_['Row'] -eq '1' })
    Assert-Equal (@($cases | ForEach-Object { $_['Cell'] } | Sort-Object) -join ' ') 'B9 C9 D9 E9 G9 H9 I9' 'neither A9 (was typed per beam end) nor F9 (Fz) is written'
    # a Source that is not a choice, and a force with no unit: said on the tab, by the column's heading
    Set-Cells $w2 @(@('Clip-Batch', "${nd}2", 'Nonsense'), @('Clip-Batch', "${fx}3", ''))
    $o = Invoke-CfCli check -Workbook $w2
    Assert-Equal $LASTEXITCODE 1 ($o -join "`n")
    Assert-Match ($o -join "`n") "\[fix  \] Clip-Batch: column 'Node #' is a column of the template's table: its Source is one of Typed per beam end, Typed here, Template's own value, Beam number"
    Assert-Match ($o -join "`n") "\[fix  \] Clip-Batch: column 'FX \(kip\)' takes Fx: give its unit in the Unit row"
    # a refresh keeps the tab's rows and writes them again on Templates; the tab of beam ends is gone with its last column
    Set-Cells $w2 @(@('Clip-Batch', "${nd}2", 'Node'), @('Clip-Batch', "${fx}3", 'kN'))
    $o = Invoke-CfCli refresh -Workbook $w2 -NoWait -NoOpen
    Assert-Equal $LASTEXITCODE 0 ($o -join "`n")
    $b = [CalcFlow.Xlsx]::Read($w2)
    $c = @($b.TableRows('tblConfigTemplates') | Where-Object { $_['Template'] -eq 'Clip-Batch' })[0]
    Assert-Equal "$($c['Load table columns'])|$($c['Force unit'])|$($c['Moment unit'])|$($c['Beam ends tab'])" '-, Beam, Node, Fx, Fy, -, Mx, My, Mz|kN|kip-in|'
    Assert-Equal (& $headsOf $b 'Clip-Batch') "Girder<Typed here | Post<Typed here | Stiffened?<Template's own value | Beam #<Beam number | Node #<Node | FX (kip)<Fx | FY (kip)<Fy | FZ (kip)<Template's own value | MX (kip-in)<Mx | MY (kip-in)<My | MZ (kip-in)<Mz"
    Assert-True ($b.Sheet('Clip-Batch ends') -eq $null) 'no column is typed per beam end any more'
    Assert-Equal ([CalcFlow.XlsxCheck]::Tables($w2) + [CalcFlow.XlsxCheck]::Validations($w2)).Count 0
    # a column turned to typed per beam end: check asks for the refresh that gives it its column on the tab of beam ends
    Set-Cells $w2 @(, @('Brace-Batch', ((Get-Col $b 'Brace-Batch' 'Member #' 5) + '2'), 'Typed per beam end'))
    $o = Invoke-CfCli check -Workbook $w2
    Assert-Equal $LASTEXITCODE 1 ($o -join "`n")
    Assert-Match ($o -join "`n") "\[fix  \] Brace-Batch ends: template Brace-Batch now has 'Member #' typed per beam end, and this tab has no column for it yet: run Refresh config"
}

Test 'a column of the template''s table can be typed once per mark: with Source = Typed here, the mark''s value goes on every row of its table' {
    $p4 = Join-Path $dir 'proj-mark'
    Copy-Item -LiteralPath $proj -Destination $p4 -Recurse
    $w4 = Join-Path $p4 'CalcFlow.xlsx'
    $b = [CalcFlow.Xlsx]::Read($w4)
    $g = Get-Col $b 'Brace-Batch' 'Grade' 5
    # the grade was typed per beam end (A500 at beam 4); now it is the mark's
    Set-Cells $w4 @(@('Brace-Batch', "${g}2", 'Typed here'), @('Brace-Batch', "${g}6", 'A36'))
    $o = Invoke-CfCli check -Workbook $w4
    Assert-Equal $LASTEXITCODE 0 ($o -join "`n")
    $br = { @([CalcFlow.Csv]::ReadTable((Join-Path $p4 'calcflow/plan/cases.csv')) | Where-Object { $_['Calc'] -eq 'BR-1' } | Sort-Object { $_['Cell'] } | ForEach-Object { "$($_['Cell'])=$($_['Value'])" }) -join ' ' }
    Assert-Equal (& $br) 'A6=HSS4X4X1/4 B6=A36 C6=No D6=60 E6=4 F6=47.488'
    # a refresh keeps the mark's value, says the column as {Grade} on Templates, and takes it off the tab of beam ends
    $o = Invoke-CfCli refresh -Workbook $w4 -NoWait -NoOpen
    Assert-Equal $LASTEXITCODE 0 ($o -join "`n")
    $b = [CalcFlow.Xlsx]::Read($w4)
    Assert-Equal @($b.TableRows('tblConfigTemplates') | Where-Object { $_['Template'] -eq 'Brace-Batch' })[0]['Load table columns'] '[Shape], {Grade}, [Slotted?], [Span (in)], Beam, Fx'
    $bt = $b.Sheet('Brace-Batch'); $gi = @(1..20 | Where-Object { $bt.Text(4, $_) -eq 'Grade' })[0]
    Assert-Equal "$($bt.Text(1, $gi))|$($bt.Text(5, $gi))|$($bt.Text(5, 0))" 'Typed here|A36|BR-1'
    Assert-Equal (@(@($b.TableRows('tblEnds1'))[0].Keys | Where-Object { $_ -ne '__row' }) -join ',') 'Beam,End,Node,Connection,Shape,Slotted?,Span (in)'
    $o = Invoke-CfCli check -Workbook $w4
    Assert-Equal $LASTEXITCODE 0 ($o -join "`n")
    Assert-Equal (& $br) 'A6=HSS4X4X1/4 B6=A36 C6=No D6=60 E6=4 F6=47.488'
    Assert-Equal ([CalcFlow.XlsxCheck]::Tables($w4) + [CalcFlow.XlsxCheck]::Validations($w4)).Count 0
    # left blank for a mark: said, and the cells are left empty
    Set-Cells $w4 @(, @('Brace-Batch', "${g}6", ''))
    $o = Invoke-CfCli check -Workbook $w4
    Assert-Match ($o -join "`n") "Brace-Batch: connection BR-1: 'Grade' is blank, so its cells in the template's table are left empty"
}

Test 'a workbook saved before a tab showed its table: Refresh config puts the columns on the tab as the Templates row had them' {
    $p3 = Join-Path $dir 'proj-before'
    Copy-Item -LiteralPath $proj -Destination $p3 -Recurse
    $w3 = Join-Path $p3 'CalcFlow.xlsx'
    $b = [CalcFlow.Xlsx]::Read($w3)
    # as such a workbook is: no table column known on Template Cells, and 'Load table columns' edited by hand
    $kind = Get-Col $b 'Template Cells' 'Kind' 2
    $cs = $b.Sheet('Template Cells'); $edits = @()
    for ($r = 2; $r -lt 80; $r++) { if ($cs.Text($r, 0) -eq 'Clip-Batch' -and $cs.Text($r, 7) -eq 'load column') { $edits += , @('Template Cells', "$kind$($r + 1)", '') } }
    Assert-Equal $edits.Count 9
    $ts = $b.Sheet('Templates'); $tr = @(2..12 | Where-Object { $ts.Text($_, 0) -eq 'Clip-Batch' })[0] + 1
    $edits += , @('Templates', ((Get-Col $b 'Templates' 'Load table columns' 2) + $tr), '[Stiffened?], Beam, Node, Fx, Fy, -, Mx, My, Mz')
    $edits += , @('Templates', ((Get-Col $b 'Templates' 'Moment unit' 2) + $tr), 'kip-ft')
    Set-Cells $w3 $edits
    $o = Invoke-CfCli refresh -Workbook $w3 -NoWait -NoOpen
    Assert-Equal $LASTEXITCODE 0 ($o -join "`n")
    $b = [CalcFlow.Xlsx]::Read($w3)
    Assert-Equal (& $headsOf $b 'Clip-Batch') "Girder<Typed here | Post<Typed here | Stiffened?<Typed per beam end | Beam #<Beam number | Node #<Node | FX (kip)<Fx | FY (kip)<Fy | FZ (kip)<Template's own value | MX (kip-in)<Mx | MY (kip-in)<My | MZ (kip-in)<Mz"
    $cb = $b.Sheet('Clip-Batch'); $mx = @(1..20 | Where-Object { $cb.Text(4, $_) -eq 'MX (kip-in)' })[0]
    Assert-Equal $cb.Text(2, $mx) 'kip-ft' 'the unit the Templates row had'
    Assert-Equal (@($b.TableRows('tblEnds2') | ForEach-Object { $_['Stiffened?'] }) -join ' ') 'Yes No' 'what was typed per beam end is kept'
    $o = Invoke-CfCli check -Workbook $w3
    Assert-Equal $LASTEXITCODE 0 ($o -join "`n")
}

Test 'calcs: the ratios of every row come back, each beam end with its own' {
    if (-not [CalcFlow.LibreOffice]::Find()) { Write-Host '        (no LibreOffice here: skipped)' -ForegroundColor DarkGray; return }
    $o = Invoke-CfCli calcs -Workbook $wb -Engine libreoffice
    Assert-Equal $LASTEXITCODE 0 ($o -join "`n")
    Assert-True (@($o | Where-Object { $_ -like 'run      3 calc steps: 3 OK, 0 invalid, 0 failed*' }).Count -eq 1) ($o -join "`n")
    $chk = { param($calc) @([CalcFlow.Csv]::ReadTable((Join-Path $proj "calcflow/data/results/${calc}__s1.checks.csv"))) }
    $cl = & $chk 'CL-1'
    $of = { param($rows, $check, $conn) [double]@($rows | Where-Object { $_['Check'] -eq $check -and $_['Connection'] -eq $conn })[0]['Max DCR'] }
    # shear ratio = |Fy| / 34.5, bolt ratio = |Fx| / 20, from the capacities sheet no input was made of
    Assert-Near (& $of $cl 'Shear ratio' 'J2-M2A') (3.731 / 34.5) 1e-9
    Assert-Near (& $of $cl 'Bolt ratio' 'J2-M2A') (37.312 / 20) 1e-9
    Assert-True ((& $of $cl 'Shear ratio' 'J5-M5B') -gt (& $of $cl 'Shear ratio' 'J2-M2A')) 'beam 5 has its own rows'
    Assert-Near ([double]@(& $chk 'BR-1')[0]['Max DCR']) (47.488 / 50) 1e-9
    Assert-Near ([double]@(& $chk 'PL-1')[0]['Max DCR']) (32.224 / (1 * 100)) 1e-9 'the thickness typed for the mark, 1 in place of 0.75'
    # the record of the clip: the rows written, the rest of the ten emptied
    $rec = [CalcFlow.TemplateBook]::Open((Join-Path (Join-Path $proj 'calcflow') @($cl)[0]['Record']))
    $s = $rec.Sheet('Clip')
    Assert-Equal "$($s.Get(8, 0).Value) $($s.Get(8, 1).Value) $($s.Get(11, 0).Value) $($s.Get(11, 1).Value)" 'Yes 2 No 5'
    Assert-True ($s.Get(14, 1) -eq $null -or $s.Get(14, 1).IsEmpty) 'the seventh row is empty'
}

# ---- a library of more than twenty calc sheets: a template's tab is made when a mark uses it
$big = Join-Path $dir 'big-library'
New-Item -ItemType Directory -Force -Path $big | Out-Null
foreach ($i in 1..22) {
    $s = New-Object CalcFlow.XOutSheet 'Calc'
    Put $s 'A2' 'Width (in)'; Put $s 'B2' $i $yellow
    Put $s 'A3' 'Fx (kip)'; Put $s 'B3' 1 $yellow
    Put $s 'A5' 'Governing DCR'; Put $s 'B5' (New-Object CalcFlow.XFormula 'ABS(B3)/(B2*10)', '0')
    [CalcFlow.XlsxWriter]::Write((Join-Path $big ('T{0:00}.xlsx' -f $i)), [CalcFlow.XOutSheet[]]@($s), $null)
}
# and one more that is nothing but a table of rows the tool writes (the brace table again, under another name)
Copy-Item -LiteralPath $bracePath -Destination (Join-Path $big 'Z Braces.xlsx')
$projBig = Join-Path $dir 'proj-big'
$oBig = Invoke-CfCli new -Out $projBig -Anl $fixtureAnl -Templates $big
$bigExit = $LASTEXITCODE
$wbBig = Join-Path $projBig 'CalcFlow.xlsx'

Test 'a large library: every template is listed, and a tab is made only for the ones a mark uses' {
    Assert-Equal $bigExit 0 ($oBig -join "`n")
    $b = [CalcFlow.Xlsx]::Read($wbBig)
    Assert-Equal @($b.TableRows('tblConfigTemplates')).Count 23
    Assert-Equal (@($b.Sheets | ForEach-Object { $_.Name }) -join '|') 'Start|Model Config|Connection Lib|Templates|Template Sheets|Template Cells|Outputs|Sign Maps|Load Cases'
    Assert-Match @($b.TableRows('tblTemplateSheets'))[4]['Says'] '^A calc of its own, with no tab yet: it is made when a mark on Connection Lib uses template T05\.'
    Assert-True (@($oBig | Where-Object { $_ -like 'sheets *23 sheets in the template workbooks: 0 with a calc tab, 23 whose tab is made when a mark on Connection Lib uses the template*' }).Count -eq 1) ($oBig -join "`n")
    Assert-True (@($oBig | Where-Object { $_ -like 'inputs *23 template workbook(s) read from their coloured cells, 0 with none*' }).Count -eq 1) ($oBig -join "`n")
    # a mark on a template with no tab yet: check says what to do, and Refresh config makes the tab
    Set-Cells $wbBig @(@('Connection Lib', 'A3', 'M-5'), @('Connection Lib', 'B3', 'T05'), @('Model Config', 'F4', 'M-5'))
    $o = Invoke-CfCli check -Workbook $wbBig
    Assert-Equal $LASTEXITCODE 1 ($o -join "`n")
    Assert-Match ($o -join "`n") '\[fix  \] Connection Lib: connection M-5 uses template T05, which has no tab yet: run Refresh config to make it'
    $o = Invoke-CfCli refresh -Workbook $wbBig -NoWait -NoOpen
    Assert-Equal $LASTEXITCODE 0 ($o -join "`n")
    $b = [CalcFlow.Xlsx]::Read($wbBig)
    Assert-Equal (@($b.Sheets | ForEach-Object { $_.Name }) -join '|') 'Start|Model Config|Connection Lib|Templates|Template Sheets|T05|Template Cells|Outputs|Sign Maps|Load Cases'
    Assert-Equal (& $headsOf $b 'T05') 'Width (in)<Typed here | Fx (kip)<Fx' 'a lone yellow cell headed Fx takes the force'
    Assert-Equal (@($b.TableRows('tblConfigOutputs') | ForEach-Object { "$($_['Template']):$($_['Result']):$($_['Use'])" }) -join ' ') 'T05:Governing DCR:Y'
    # the mark gets its row, and the calc is ready: one template of the twenty-two in play
    Set-Cells $wbBig @(@('T05', 'A6', 'M-5'), @('T05', ((Get-Col $b 'T05' 'Width (in)' 5) + '6'), 8))
    $o = Invoke-CfCli check -Workbook $wbBig
    Assert-Equal $LASTEXITCODE 0 ($o -join "`n")
    Assert-True (@($o | Where-Object { $_ -like 'check *1 of 1 connection calcs are ready: 0 error(s)*' }).Count -eq 1) ($o -join "`n")
    # a template that is nothing but a table of rows the tool writes: the tab made for it shows the table's headings
    # and what fills each, and says in its first words what it is (it was a tab with the one column Connection)
    Set-Cells $wbBig @(@('Connection Lib', 'A4', 'ZB-1'), @('Connection Lib', 'B4', 'Z-Braces'))
    $o = Invoke-CfCli refresh -Workbook $wbBig -NoWait -NoOpen
    Assert-Equal $LASTEXITCODE 0 ($o -join "`n")
    $b = [CalcFlow.Xlsx]::Read($wbBig)
    Assert-Equal (@($b.Sheets | ForEach-Object { $_.Name }) -join '|') 'Start|Model Config|Connection Lib|Templates|Template Sheets|T05|Z-Braces|Z-Braces ends|Template Cells|Outputs|Sign Maps|Load Cases'
    Assert-Equal (& $headsOf $b 'Z-Braces') 'Shape<Typed per beam end | Grade<Typed per beam end | Slotted?<Typed per beam end | Span (in)<Typed per beam end | Member #<Beam number | Axial (kip)<Fx'
    Assert-Match $b.Sheet('Z-Braces').Text(0, 0) '^This template is a table of load rows: a mark is ONE copy of it, and the tool writes a row for each beam end that calls the mark'
    Assert-Match @($b.TableRows('tblTemplateSheets') | Where-Object { $_['File'] -eq 'Z Braces.xlsx' })[0]['Says'] 'It is a table of load rows \(from A6, 3 rows\)'
}

Test 'a sheet on which no input is found any more keeps its tab, and the tab says so in its first words; so does the refresh' {
    $el = Join-Path $dir 'pair-library'
    New-Item -ItemType Directory -Force -Path $el | Out-Null
    $pair = { param($fill)
        $sheets = @()
        foreach ($n in 'First', 'Second') {
            $s = New-Object CalcFlow.XOutSheet $n
            $st = $yellow; if ($n -eq 'Second' -and -not $fill) { $st = $null }
            Put $s 'A2' 'Width (in)'; Put $s 'B2' 4 $st
            Put $s 'A3' 'Fx (kip)'; Put $s 'B3' 1 $st
            Put $s 'A5' 'Governing DCR'; Put $s 'B5' (New-Object CalcFlow.XFormula 'ABS(B3)/(B2*10)', '0')
            $sheets += $s
        }
        [CalcFlow.XlsxWriter]::Write((Join-Path $el 'Pair.xlsx'), [CalcFlow.XOutSheet[]]$sheets, $null)
    }
    & $pair $true
    $pp = Join-Path $dir 'proj-pair'
    $o = Invoke-CfCli new -Out $pp -Anl $fixtureAnl -Templates $el
    Assert-Equal $LASTEXITCODE 0 ($o -join "`n")
    $w = Join-Path $pp 'CalcFlow.xlsx'
    Assert-Equal (& $headsOf ([CalcFlow.Xlsx]::Read($w)) 'Pair Second') 'Width (in)<Typed here | Fx (kip)<Fx'
    # the second sheet loses its fill in the template
    & $pair $false
    $o = Invoke-CfCli refresh -Workbook $w -NoWait -NoOpen
    Assert-Equal $LASTEXITCODE 0 ($o -join "`n")
    Assert-Match ($o -join "`n") '\[note \] tab Pair Second: no input was found on sheet Second of Pair\.xlsx \(no cell with the input fill that holds a typed value\), so the tab has no column'
    $b = [CalcFlow.Xlsx]::Read($w)
    Assert-Equal (& $headsOf $b 'Pair Second') ''
    Assert-Match $b.Sheet('Pair Second').Text(0, 0) '^NO INPUT WAS FOUND on this sheet of the template'
    Assert-Equal (& $headsOf $b 'Pair First') 'Width (in)<Typed here | Fx (kip)<Fx'
}

# ---- what a cell still needs, shown by its colour as things are chosen (cs\Needs.cs): every rule's formula is
# calculated by LibreOffice for the first cell of its range, in the finished project and in copies with things missing
Test 'the colours of what is still needed: no rule fires in a finished project, and each fires for the choice it watches' {
    $nd = Join-Path $dir 'needs'
    New-Item -ItemType Directory -Force -Path $nd | Out-Null
    $v0 = Join-Path $nd 'done.xlsx'; $v1 = Join-Path $nd 'missing.xlsx'; $v2 = Join-Path $nd 'no-mark.xlsx'; $v3 = Join-Path $nd 'no-template.xlsx'; $v4 = Join-Path $nd 'no-tab.xlsx'
    Copy-Item -LiteralPath $wb -Destination $v0
    $b = [CalcFlow.Xlsx]::Read($wb)
    Assert-Equal ([CalcFlow.XlsxCheck]::FormatRules($wb)).Count 0
    $folder = @(0..40 | Where-Object { $b.Sheet('Start').Text($_, 0) -eq 'Templates folder' })[0] + 1
    $own = "Template's own value"
    # 1. things missing or misplaced, each at the first cell of a rule's range
    Save-Cells $wb $v1 @(
        @('Connection Lib', 'A6', 'CL-1'), @('Connection Lib', 'B6', 'Clip-Batch'), @('Model Config', 'F3', 'ZZ-9'),
        @('Clip-Batch', 'B2', $own), @('Clip-Batch', 'D2', 'Typed here'), @('Clip-Batch', 'D6', ''), @('Clip-Batch', 'A6', 'BR-1'), @('Clip-Batch', 'A7', 'BR-1'),
        @('Brace-Batch', 'B2', 'Fx'), @('Brace-Batch', 'B3', ''), @('Brace-Batch', 'F2', 'Typed per beam end'), @('Brace-Batch', 'A6', ''), @('Brace-Batch', 'B6', 'x'), @('Brace-Batch ends', 'E3', ''),
        @('Outputs', 'B3', 'N'), @('Templates', ((Get-Col $b 'Templates' 'Max rows' 2) + '3'), ''),
        @('Load Cases', 'D3', 'N'), @('Load Cases', 'D4', 'N'), @('Load Cases', 'D5', 'N'), @('Load Cases', 'D6', 'N'), @('Load Cases', 'D7', 'N'), @('Load Cases', 'D8', 'N'), @('Load Cases', 'D9', 'N'), @('Load Cases', 'D10', 'N'))
    # 2. a template with no mark named, a template that is not one, a health result with nothing to equal, no templates folder
    Save-Cells $wb $v2 @(@('Connection Lib', 'A3', ''), @('Connection Lib', 'B3', 'Nope'), @('Outputs', 'D3', 'health'), @('Outputs', 'E3', ''), @('Start', "B$folder", ''))
    # 3. a mark with no template
    Save-Cells $wb $v3 @(, @('Connection Lib', 'B3', ''))
    # 4. in the large library: a mark whose template has no tab yet
    Save-Cells $wbBig $v4 @(, @('Connection Lib', 'B3', 'T07'))
    $got = Get-CfFormatValues @($v0, $v1, $v2, $v3, $v4) $nd
    if ($null -eq $got) { Write-Host '        (no LibreOffice here: skipped)' -ForegroundColor DarkGray; return }
    $odd = @($got | Where-Object { $_.Value -ne 'TRUE' -and $_.Value -ne 'FALSE' } | ForEach-Object { "$($_.File) $($_.Tab)!$($_.Range): '$($_.Value)' from $($_.Formula)" })
    Assert-Equal ($odd -join "`n") '' 'every formula calculates to TRUE or FALSE'
    Assert-True (@($got | Where-Object { $_.File -eq 'done.xlsx' }).Count -ge 30) 'the rules were found'
    # a finished project: nothing is asked for; the only rules that hold are the ones that grey a cell not read
    $on = { param($file) @($got | Where-Object { $_.File -eq $file -and $_.Value -eq 'TRUE' } | ForEach-Object { "$($_.Tab)!$($_.Range) $($_.Formula)" } | Sort-Object) }
    $asks = { param($file) @(& $on $file | Where-Object { $_ -notlike '*<>"Typed here")' }) }
    Assert-Equal ((& $asks 'done.xlsx') -join "`n") ''
    Assert-Equal ((& $on 'done.xlsx') -join "`n") (@(
        'Brace-Batch!B6:G2000 AND($A6<>"",B$2<>"Typed here")', 'Clip-Batch!D6:L2000 AND($A6<>"",D$2<>"Typed here")', 'Plate!K6:N2000 AND($A6<>"",K$2<>"Typed here")') -join "`n")
    Assert-Equal ((& $on 'missing.xlsx') -join "`n") (@(
        'Brace-Batch ends!E3:H2000 AND($A3<>"",E3="")',
        'Brace-Batch!A6:A2000 AND($A6="",COUNTA($B6:$ZZ6)>0)',
        'Brace-Batch!B3:G3 AND(B3="",OR(B2="Fx",B2="Fy",B2="Fz",B2="Mx",B2="My",B2="Mz"))',
        'Brace-Batch!F2 AND(F2="Typed per beam end",ISERROR(MATCH(F5,INDIRECT("''Brace-Batch ends''!$2:$2"),0)))',
        'Clip-Batch!A6:A2000 AND($A6<>"",COUNTIF($A$6:$A$2000,$A6)>1)',
        'Clip-Batch!A6:A2000 AND($A6<>"",COUNTIFS(INDIRECT("''Connection Lib''!$A$3:$A$2000"),$A6,INDIRECT("''Connection Lib''!$B$3:$B$2000"),"Clip-Batch")=0)',
        'Clip-Batch!B6:C2000 AND($A6<>"",B$2<>"",B$2<>"Typed here")',
        'Clip-Batch!D6:L2000 AND($A6<>"",D$2="Typed here")',
        'Clip-Batch!D6:L2000 AND($A6<>"",D6="",D$2="Typed here")',
        'Connection Lib!A3:A2000 AND($A3<>"",$B3<>"",IFERROR(COUNTIF(INDIRECT("''"&$B3&"''!$A$6:$A$2000"),$A3),1)=0)',
        'Connection Lib!A3:A2000 AND($A3<>"",COUNTIF($A$3:$A$2000,$A3)>1)',
        'Load Cases!D3:D2000 AND($A3<>"",COUNTIF($D$3:$D$2000,"Y*")=0)',
        'Model Config!F3:G2000 AND(F3<>"",COUNTIF(INDIRECT("''Connection Lib''!$A$3:$A$2000"),F3)=0)',
        'Outputs!B3:B2000 AND($A3<>"",COUNTIFS($A$3:$A$2000,$A3,$B$3:$B$2000,"Y*")=0,COUNTIF(INDIRECT("''Connection Lib''!$B$3:$B$2000"),$A3)>0)',
        'Plate!K6:N2000 AND($A6<>"",K$2<>"Typed here")',
        'Templates!K3:K2000 AND($I3<>"",K3="")') -join "`n")
    # (the row of CL-1 on its template's tab turns red with it: CL-1 is no longer a mark of that template)
    $orphan = 'Clip-Batch!A6:A2000 AND($A6<>"",COUNTIFS(INDIRECT("''Connection Lib''!$A$3:$A$2000"),$A6,INDIRECT("''Connection Lib''!$B$3:$B$2000"),"Clip-Batch")=0)'
    Assert-Equal ((& $asks 'no-mark.xlsx') -join "`n") (@(
        $orphan,
        'Connection Lib!A3:A2000 AND($A3="",$B3<>"")',
        'Connection Lib!B3:B2000 AND($B3<>"",COUNTIF(INDIRECT("''Templates''!$A$3:$A$2000"),$B3)=0)',
        'Connection Lib!B3:B2000 AND($B3<>"",ISERROR(INDIRECT("''"&$B3&"''!A1")))',
        'Outputs!E3:E2000 AND(LEFT($B3,1)="Y",$D3="health",E3="")',
        "Start!B$folder B$folder=`"`"") -join "`n")
    Assert-Equal ((& $asks 'no-template.xlsx') -join "`n") (@($orphan, 'Connection Lib!B3:B2000 AND($A3<>"",$B3="")') -join "`n")
    # a template that is on the Templates tab and has no tab of its own yet: the one rule that waits for Refresh config
    Assert-Equal (@(& $asks 'no-tab.xlsx' | Where-Object { $_ -like 'Connection Lib!*' }) -join "`n") 'Connection Lib!B3:B2000 AND($B3<>"",ISERROR(INDIRECT("''"&$B3&"''!A1")))'
}

Test 'a library whose input colour is not yellow: given once, kept on the Start tab, used again by a refresh' {
    $blueLib = Join-Path $dir 'blue-library'
    New-Item -ItemType Directory -Force -Path $blueLib | Out-Null
    Copy-Item -LiteralPath $platePath -Destination $blueLib
    $pb = Join-Path $dir 'proj-blue'
    $o = Invoke-CfCli new -Out $pb -Anl $fixtureAnl -Templates $blueLib -InputFill ddebf7
    Assert-Equal $LASTEXITCODE 0 ($o -join "`n")
    $w = Join-Path $pb 'CalcFlow.xlsx'
    Assert-Equal ([CalcFlow.EngineerWorkbook]::Settings($w))['Input fill'] 'DDEBF7'
    Assert-Equal (& $headsOf ([CalcFlow.Xlsx]::Read($w)) 'Plate') 'Factor<Typed here' 'the one blue cell, and none of the yellow ones'
    $o = Invoke-CfCli refresh -Workbook $w -NoWait -NoOpen
    Assert-Equal $LASTEXITCODE 0 ($o -join "`n")
    Assert-Equal (& $headsOf ([CalcFlow.Xlsx]::Read($w)) 'Plate') 'Factor<Typed here'
    $o = Invoke-CfCli new -Out (Join-Path $dir 'proj-bad') -Anl $fixtureAnl -Templates $blueLib -InputFill blue
    Assert-Equal $LASTEXITCODE 2 ($o -join "`n")
    Assert-Match ($o -join "`n") "Input fill: 'blue' is not a fill colour: give it as six hex digits"
}
