# Harvest.Tests.ps1 -- the template reader (CalcFlow.TemplateBook, FormulaRefs) and harvest, on the two generic
# templates in fixtures\templates (a weld group tool with one calc per tab, a base plate tool with defined names) and
# on a hand-made workbook that has what they lack: a protected sheet with an unlocked cell, a shared formula, a defined
# name, a blank input and a drop-down.

$dir = New-TestDir 'harvest'
$tdir = Join-Path $script:Fixtures 'templates'
$weld = Join-Path $tdir 'Weld Group Design Tool.xlsx'
$plate = Join-Path $tdir 'Base_Plate_Anchorage.xlsx'

# a minimal .xlsx from parts (no Excel, no writer): the sheet XML is given whole
function New-MiniXlsx([string]$Path, [string]$SheetXml, [string]$Names) {
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
    $parts = [ordered]@{
        '[Content_Types].xml' = '<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/></Types>'
        '_rels/.rels' = '<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>'
        'xl/workbook.xml' = '<?xml version="1.0" encoding="UTF-8"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Calc" sheetId="1" r:id="rId1"/></sheets><definedNames>' + $Names + '</definedNames></workbook>'
        'xl/_rels/workbook.xml.rels' = '<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>'
        'xl/styles.xml' = '<?xml version="1.0" encoding="UTF-8"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><cellXfs count="2"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/><xf numFmtId="0" fontId="0" fillId="0" borderId="0" applyProtection="1"><protection locked="0"/></xf></cellXfs></styleSheet>'
        'xl/worksheets/sheet1.xml' = '<?xml version="1.0" encoding="UTF-8"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">' + $SheetXml + '</worksheet>'
    }
    $fs = [IO.File]::Create($Path)
    try {
        $zip = New-Object System.IO.Compression.ZipArchive($fs, [System.IO.Compression.ZipArchiveMode]::Create)
        foreach ($k in $parts.Keys) {
            $e = $zip.CreateEntry($k)
            $w = New-Object IO.StreamWriter($e.Open(), (New-Object Text.UTF8Encoding($false)))
            $w.Write($parts[$k]); $w.Dispose()
        }
        $zip.Dispose()
    }
    finally { $fs.Dispose() }
}

function T([string]$ref, [string]$text) { return "<c r=`"$ref`" t=`"inlineStr`"><is><t>$text</t></is></c>" }

$mini = Join-Path $dir 'mini.xlsx'
$sheet = '<sheetData>' +
    '<row r="1">' + (T 'A1' 'Load (kip)') + '<c r="B1" s="1"/></row>' +
    '<row r="2">' + (T 'A2' 'Span (ft)') + '<c r="B2"><v>20</v></c>' + (T 'D2' 'Opt. factor') + '</row>' +
    '<row r="3">' + (T 'A3' 'Moment') + '<c r="B3"><f>B1*B2/8</f><v>12.5</v></c></row>' +
    '<row r="4">' + (T 'A4' 'Governing DCR') + '<c r="B4"><f>B3/Cap*IF(E2="",1,E2)</f></c></row>' +
    '<row r="5"><c r="A5"><f t="shared" ref="A5:A7" si="0">B2*2</f><v>40</v></c></row>' +
    '<row r="6"><c r="A6"><f t="shared" si="0"/></c></row>' +
    '<row r="7"><c r="A7"><f t="shared" si="0"/></c></row>' +
    '<row r="8">' + (T 'A8' 'Use?') + (T 'B8' 'Y') + '<c r="C8"><f>IF(B8="Y",1,0)</f></c></row>' +
    '<row r="10">' + (T 'C10' 'Capacity') + '<c r="D10"><v>100</v></c></row>' +
    '<row r="20"><c r="A20"><v>1</v></c><c r="B20"><v>2</v></c></row><row r="21"><c r="A21"><v>3</v></c><c r="B21"><v>4</v></c><c r="C21"><f>SUM(A20:B21)</f></c></row>' +
    '</sheetData><sheetProtection sheet="1"/>' +
    '<dataValidations count="1"><dataValidation type="list" sqref="B8"><formula1>"Y,N"</formula1></dataValidation></dataValidations>'
New-MiniXlsx $mini $sheet '<definedName name="Cap">Calc!$D$10</definedName>'

Test 'formula references: cells, ranges, other sheets, names, table columns; strings and function names are not references' {
    $refs = [CalcFlow.FormulaRefs]::Extract('SUM(Sheet2!$A$1:B3)+''My Sheet''!C4+Cap+Tbl[Col]+LOG10(D5)+LEN("A1")')
    $cells = @($refs | Where-Object { -not $_.Name } | ForEach-Object { "{0}!{1}:{2}" -f $_.Sheet, [CalcFlow.Xlsx]::Ref($_.Row1, $_.Col1), [CalcFlow.Xlsx]::Ref($_.Row2, $_.Col2) })
    Assert-Equal ($cells -join ' ') 'Sheet2!A1:B3 My Sheet!C4:C4 !D5:D5'
    $names = @($refs | Where-Object { $_.Name } | ForEach-Object { if ($_.TableColumn) { "$($_.Name)[$($_.TableColumn)]" } else { $_.Name } })
    Assert-Equal ($names -join ' ') 'Cap Tbl[Col]'
    Assert-Equal ([CalcFlow.FormulaRefs]::Shift('B2*$C$3+D$4+"B2"', 2, 1)) 'C4*$C$3+E$4+"B2"'
}

Test 'the template reader: values, formulas (a shared formula shifted to each cell), protection, unlocked cells, names' {
    $b = [CalcFlow.TemplateBook]::Open($mini)
    $s = $b.Sheet('Calc')
    Assert-True $s.Protected 'protected'
    Assert-Equal $s.Get(0, 1).Locked $false 'B1 unlocked'
    Assert-Equal $s.Get(1, 1).Locked $true
    Assert-Equal $s.Get(5, 0).Formula 'B3*2' 'A6 from the shared formula'
    Assert-Equal $s.Get(6, 0).Formula 'B4*2' 'A7 from the shared formula'
    Assert-True ($b.IsReferenced($s, 2, 1)) 'B3 is read (by A6)'
    Assert-True ($b.IsReferenced($s, 9, 3)) 'D10 is read through the name Cap'
    $ts = $null; $r = 0; $c = 0; $err = ''
    Assert-True ($b.Locate('Calc', 'Cap', [ref]$ts, [ref]$r, [ref]$c, [ref]$err)) $err
    Assert-Equal ([CalcFlow.Xlsx]::Ref($r, $c)) 'D10'
    Assert-Equal $b.Sha256.Length 64
}

Test 'harvest of the hand-made workbook: unlocked, read, blank, drop-down and named inputs; the DCR output; no lookup grid' {
    $h = [CalcFlow.Harvest]::Run([string[]]@($mini), $dir)
    $in = @($h.Inputs | ForEach-Object { "{0}:{1}:{2}" -f $_['Cell'], $_['Kind'], $_['GuardText'] })
    Assert-Equal ($in -join ' | ') 'B1:blank:Load (kip) | B2:number:Span (ft) | B8:list:Use? | Cap:number:Capacity | E2:blank:Opt. factor'
    $b2 = @($h.Inputs | Where-Object { $_['Cell'] -eq 'B2' })[0]
    Assert-Equal $b2['Unit'] 'ft'
    Assert-Equal @($h.Inputs | Where-Object { $_['Cell'] -eq 'B8' })[0]['List'] 'Y,N'
    Assert-Match @($h.Inputs | Where-Object { $_['Cell'] -eq 'B1' })[0]['Found'] 'unlocked'
    $out = @($h.Outputs | ForEach-Object { "{0}:{1}:{2}" -f $_['Cell'], $_['Kind'], $_['Output'] })
    Assert-Equal ($out -join ' | ') 'B4:dcr:Governing DCR'
    Assert-Match $h.Templates[0]['Notes'] 'protected sheets: Calc'
    Assert-Equal $h.Templates[0]['File'] 'mini.xlsx'
}

Test "the weld tool's rectangular tab: exactly its 22 inputs, each with its guard label and unit, and its DCR outputs" {
    $h = [CalcFlow.Harvest]::Run([string[]]@($weld), $tdir)
    $rect = @($h.Inputs | Where-Object { $_['Sheet'] -eq 'Rect. Fillet Weld Group' })
    Assert-Equal (($rect | ForEach-Object { $_['Cell'] }) -join ' ') 'B5 F5 B6 F6 B7 F7 B8 F8 B9 F9 B10 F10 B11 F11 B12 F12 B14 D14 G14 B15 D15 G15'
    $f5 = @($rect | Where-Object { $_['Cell'] -eq 'F5' })[0]
    Assert-Equal "$($f5['GuardCell'])|$($f5['GuardText'])|$($f5['Unit'])|$($f5['Example'])" 'D5|FEXX (ksi)|ksi|70'
    $b5 = @($rect | Where-Object { $_['Cell'] -eq 'B5' })[0]
    Assert-Equal "$($b5['Kind'])|$($b5['List'])" 'list|LRFD,ASD'
    Assert-Equal @($rect | Where-Object { $_['Cell'] -eq 'F6' })[0]['Example'] '0.707'
    $dcr = @($h.Outputs | Where-Object { $_['Sheet'] -eq 'Rect. Fillet Weld Group' -and $_['Kind'] -eq 'dcr' })
    Assert-Equal (($dcr | ForEach-Object { "$($_['Cell'])=$($_['Output'])" }) -join ' ') 'F47=DCRweld F48=DCRbase F49=Governing DCR'
    Assert-Equal $h.Templates[0]['Sha256'] ([CalcFlow.SharedFile]::Sha256($weld))
}

Test 'the base plate tool: inputs and outputs by their defined names, and its solver checks as health outputs' {
    $h = [CalcFlow.Harvest]::Run([string[]]@($plate), $tdir)
    $bp = @($h.Inputs | Where-Object { $_['Cell'] -eq 'Bp' })[0]
    Assert-Equal "$($bp['Sheet'])|$($bp['GuardCell'])|$($bp['GuardText'])|$($bp['Unit'])" 'Calc|A5|B (in)|in'
    Assert-Match $bp['Found'] 'named \(B5\)'
    $all = @($h.Outputs | Where-Object { $_['Cell'] -eq 'dcr_all' })[0]
    Assert-Equal "$($all['Kind'])|$($all['Output'])" 'dcr|Governing DCR'
    Assert-Equal (@($h.Outputs | Where-Object { $_['Kind'] -eq 'health' } | ForEach-Object { $_['Cell'] }) -join ' ') 'Converged Singular'
}

Test 'CalcFlow.ps1 harvest writes harvest.xlsx with the drafts as tables in the library columns, and the layout' {
    $exe = (Get-Process -Id $PID).Path
    $out = Join-Path $dir 'harvest.xlsx'
    $o = & $exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $script:CalcFlow 'CalcFlow.ps1') harvest -Templates $tdir -Out $out 2>&1 | ForEach-Object { "$_" }
    Assert-Equal $LASTEXITCODE 0 ($o -join "`n")
    $wb = [CalcFlow.Xlsx]::Read($out)
    Assert-Equal (@($wb.TableRows('tblHarvestTemplates') | ForEach-Object { $_['Template'] }) -join ' ') 'Base_Plate_Anchorage Weld-Group-Design-Tool'
    $cols = $wb.Table('tblHarvestInputs').Columns
    for ($i = 0; $i -lt [CalcFlow.LibrarySchema]::InputColumns.Length; $i++) { Assert-Equal $cols[$i] ([CalcFlow.LibrarySchema]::InputColumns[$i]) }
    Assert-True ($wb.TableRows('tblHarvestLayout').Count -gt 1000) 'layout rows'
    $o = & $exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $script:CalcFlow 'CalcFlow.ps1') harvest -Templates $tdir -Out (Join-Path $script:Repo 'harvest.xlsx') 2>&1 | ForEach-Object { "$_" }
    Assert-Equal $LASTEXITCODE 2 'a harvest written into the repo is refused'
}
