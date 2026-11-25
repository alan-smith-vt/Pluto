# Xlsx.Tests.ps1 -- the Excel-free reader and writer (CalcFlow.Xlsx)
# Copied from pipe-review (tests/Xlsx.Tests.ps1 at d4b33aa, 2026-11-06): namespace CalcFlow; the CAESAR output workbook is
# Pluto's own copy (scripts/caesar/testdata), the same file as pipe-review's fixture.

$dir = New-TestDir 'xlsx'

Test 'writer -> reader round trip keeps text, numbers, booleans and blanks' {
    $s = New-Object CalcFlow.XOutSheet 'Data'
    $s.Add('Line', 'Node', 'Value', 'Flag', 'Text')
    $s.Add('LINE-A', 10, 1.25, $true, 'a < b & "c"')
    $s.Add('LINE-B', 20, -0.000125, $false, $null)
    $s.Add('LINE-B', 30, 123456789.5, $null, "two`nlines")
    $p = Join-Path $dir 'roundtrip.xlsx'
    [CalcFlow.XlsxWriter]::Write($p, [CalcFlow.XOutSheet[]]@($s), $null)
    $wb = [CalcFlow.Xlsx]::Read($p)
    $r = $wb.Sheet('Data')
    Assert-Equal $r.Text(1, 0) 'LINE-A'
    Assert-Equal $r.Text(1, 1) '10'
    Assert-Near ([double]$r.Text(1, 2)) 1.25
    Assert-Equal $r.Text(1, 3) 'TRUE'
    Assert-Equal $r.Text(1, 4) 'a < b & "c"'
    Assert-Near ([double]$r.Text(2, 2)) -0.000125 1e-15
    Assert-Equal $r.Cell(2, 4) $null
    Assert-Near ([double]$r.Text(3, 2)) 123456789.5
    Assert-Equal $r.Cell(3, 4) "two`nlines"
}

Test 'tables and defined names read back by name' {
    $s = New-Object CalcFlow.XOutSheet 'Supports'
    $s.Add('Title row')
    $s.Add()
    $s.Add('Line', 'Node', 'Support Detail')
    $s.Add('LINE-A', 10, 'PS-01')
    $s.Add()
    $s.Add('LINE-A', 20, 'PS-02')
    $t = New-Object CalcFlow.XOutTable
    $t.Name = 'tblSupports'; $t.Top = 2; $t.Left = 0; $t.Bottom = 5; $t.Right = 2
    $s.Tables.Add($t)
    $names = New-Object 'System.Collections.Generic.Dictionary[string,string]'
    $names['DCR_Limit'] = "Supports!`$A`$1"
    $p = Join-Path $dir 'tables.xlsx'
    [CalcFlow.XlsxWriter]::Write($p, [CalcFlow.XOutSheet[]]@($s), $names)
    $wb = [CalcFlow.Xlsx]::Read($p)
    $rows = $wb.TableRows('tblSupports')
    Assert-Equal $rows.Count 2 'blank row skipped:'
    Assert-Equal $rows[1]['Support Detail'] 'PS-02'
    Assert-Equal $rows[0]['__row'] '4'
    Assert-Equal $wb.NameValue('DCR_Limit') 'Title row'
}

Test 'styled report features write a valid package (widths, freeze, merge, filter, validation, cond. format, print)' {
    $s = New-Object CalcFlow.XOutSheet 'All Supports'
    $h = New-Object CalcFlow.XStyle; $h.Bold = $true; $h.Fill = 'FF1F3864'; $h.Color = 'FFFFFFFF'; $h.Wrap = $true; $h.Border = $true
    $n = New-Object CalcFlow.XStyle; $n.NumFmt = '0.000'
    $s.Add((New-Object CalcFlow.XCell 'Report', $h))
    $s.Merges.Add('A1:C1')
    $s.Add((New-Object CalcFlow.XCell 'Line', $h), (New-Object CalcFlow.XCell 'Status', $h), (New-Object CalcFlow.XCell 'DCR', $h))
    $s.Add('LINE-A', 'OK', (New-Object CalcFlow.XCell 0.5, $n))
    $s.FreezeRows = 2; $s.AutoFilter = 'A2:C3'; $s.Landscape = $true; $s.FitToWidth = $true; $s.PrintTitleFirstRow = 2; $s.PrintTitleLastRow = 2
    $s.Width(0, 12); $s.Width(1, 18)
    $v = New-Object CalcFlow.XValidation; $v.Sqref = 'B3:B100'; $v.List = '"OK,Recheck"'; $s.Validations.Add($v)
    $cf = New-Object CalcFlow.XCondFormat; $cf.Sqref = 'B3:B100'; $cf.EqualsText = 'OK'; $cf.Style = New-Object CalcFlow.XStyle; $cf.Style.Fill = 'FFC6EFCE'; $s.CondFormats.Add($cf)
    $p = Join-Path $dir 'styled.xlsx'
    [CalcFlow.XlsxWriter]::Write($p, [CalcFlow.XOutSheet[]]@($s), $null)
    $wb = [CalcFlow.Xlsx]::Read($p)
    Assert-Equal $wb.Sheet('All Supports').Text(2, 1) 'OK'
    Assert-Equal $wb.Sheet('All Supports').Text(0, 0) 'Report'
}

Test 'writer refuses NaN, duplicate sheets and bad table names' {
    $a = New-Object CalcFlow.XOutSheet 'A'; $a.Add([double]::NaN)
    Assert-Throws { [CalcFlow.XlsxWriter]::Write((Join-Path $dir 'nan.xlsx'), [CalcFlow.XOutSheet[]]@($a), $null) } 'finite'
    $b = New-Object CalcFlow.XOutSheet 'Same'; $c = New-Object CalcFlow.XOutSheet 'same'
    Assert-Throws { [CalcFlow.XlsxWriter]::Write((Join-Path $dir 'dup.xlsx'), [CalcFlow.XOutSheet[]]@($b, $c), $null) } 'duplicate'
    $d = New-Object CalcFlow.XOutSheet 'T'; $d.Add('x'); $d.Add('1')
    $t = New-Object CalcFlow.XOutTable; $t.Name = 'bad name'; $t.Top = 0; $t.Bottom = 1; $d.Tables.Add($t)
    Assert-Throws { [CalcFlow.XlsxWriter]::Write((Join-Path $dir 'tbl.xlsx'), [CalcFlow.XOutSheet[]]@($d), $null) } 'table name'
}

# a part of a written workbook, rewritten by a script block (text in, text out)
function Edit-XlsxPart([string]$Path, [string]$Part, [scriptblock]$Change) {
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
    $zip = [IO.Compression.ZipFile]::Open($Path, [IO.Compression.ZipArchiveMode]::Update)
    try {
        $e = $zip.GetEntry($Part)
        $r = New-Object IO.StreamReader($e.Open(), [Text.Encoding]::UTF8)
        $text = $r.ReadToEnd(); $r.Dispose()
        $e.Delete()
        $w = New-Object IO.StreamWriter($zip.CreateEntry($Part).Open(), (New-Object Text.UTF8Encoding($false)))
        $w.Write((& $Change $text)); $w.Dispose()
    }
    finally { $zip.Dispose() }
}

Test "a table heading as Excel takes it: one line, single spaces, nothing invisible, 255 characters at most" {
    Assert-Equal ([CalcFlow.Xlsx]::TableHeading("  Plate`r`nthickness`t(in) ")) 'Plate thickness (in)'
    Assert-Equal ([CalcFlow.Xlsx]::TableHeading("nb$([char]0xA0)sp zero$([char]0x200B)width")) 'nb sp zerowidth'
    $long = 'Long ' + ('label text ' * 30)
    $cut = [CalcFlow.Xlsx]::TableHeading($long)
    Assert-True ($cut.Length -le 255 -and $cut.Length -ge 250 -and $cut.EndsWith(' ...') -and $cut.StartsWith('Long label text label')) $cut
    $tagged = [CalcFlow.Xlsx]::TableHeading($long, ' [B5]')
    Assert-True ($tagged.Length -le 255 -and $tagged.Length -ge 250 -and $tagged.EndsWith(' ... [B5]')) $tagged
    Assert-Equal ([CalcFlow.Xlsx]::TableHeading('wleg (in.)', ' [B6]')) 'wleg (in.) [B6]'
    # alike: letter case, the forms of a character that look the same, spacing. Not alike: other signs, other words
    $same = { param($a, $b) [CalcFlow.Xlsx]::Headings.Equals($a, $b) }
    Assert-True (& $same 'd (in)' 'D (in)')
    Assert-True (& $same "Ohm $([char]0x2126)" "ohm $([char]0x3A9)") 'the ohm sign and omega'
    Assert-True (& $same "Area (in$([char]0xB2))" 'area (in2)') 'a superscript two'
    Assert-True (& $same 'a  b' "a$([char]0xA0)b")
    Assert-True (-not (& $same '+x' '-x'))
    Assert-True (-not (& $same "cx$([char]0x2212) (in)" 'cx+ (in)'))
    Assert-True (-not (& $same 'Fy' 'Fy (ksi)'))
}

Test "the writer makes a table's headings what Excel takes, the cell and the table part alike, and refuses two that are alike" {
    $s = New-Object CalcFlow.XOutSheet 'T'
    $long = 'Note ' + ('that goes on ' * 25)
    $s.Add('Mark', 12, " Plate`nthickness ", $long, '+x', '=SUM', "f'c [psi] #1", $true)
    $s.Add('A', 1, 2, 3, 4, 5, 6, 7)
    $t = New-Object CalcFlow.XOutTable; $t.Name = 'tblT'; $t.Top = 0; $t.Bottom = 1; $t.Left = 0; $t.Right = 7; $s.Tables.Add($t)
    $p = Join-Path $dir 'headings.xlsx'
    [CalcFlow.XlsxWriter]::Write($p, [CalcFlow.XOutSheet[]]@($s), $null)
    Assert-Equal ([CalcFlow.XlsxCheck]::Tables($p) -join '; ') ''
    $wb = [CalcFlow.Xlsx]::Read($p)
    $cols = $wb.Tables[0].Columns
    Assert-Equal ($cols[0..2] -join '|') 'Mark|12|Plate thickness'
    Assert-Equal "$($cols[3].Length)|$($cols[4])|$($cols[5])|$($cols[6])|$($cols[7])" "255|+x|=SUM|f'c [psi] #1|True"
    foreach ($i in 0..7) { Assert-Equal $wb.Sheet('T').Cell(0, $i) $cols[$i] "the heading cell of column $i" }
    foreach ($pair in @(@('d (in)', 'D (in)'), @("Area (in$([char]0xB2))", 'Area (in2)'), @('Fy', ' fy '))) {
        $d = New-Object CalcFlow.XOutSheet 'D'; $d.Add($pair[0], $pair[1]); $d.Add(1, 2)
        $u = New-Object CalcFlow.XOutTable; $u.Name = 'tblD'; $u.Top = 0; $u.Bottom = 1; $u.Left = 0; $u.Right = 1; $d.Tables.Add($u)
        Assert-Throws { [CalcFlow.XlsxWriter]::Write((Join-Path $dir 'alike.xlsx'), [CalcFlow.XOutSheet[]]@($d), $null) } 'takes for one name'
    }
    $e = New-Object CalcFlow.XOutSheet 'E'; $e.Add('a', ''); $e.Add(1, 2)
    $v = New-Object CalcFlow.XOutTable; $v.Name = 'tblE'; $v.Top = 0; $v.Bottom = 1; $v.Left = 0; $v.Right = 1; $e.Tables.Add($v)
    Assert-Throws { [CalcFlow.XlsxWriter]::Write((Join-Path $dir 'blank.xlsx'), [CalcFlow.XOutSheet[]]@($e), $null) } 'needs a text heading'
}

Test "the check of Excel's table rules finds what Excel repairs: a heading that is a number, not the column's name, too long, alike, merged" {
    # the files spoiled on purpose are named patched-*: the checks of every written workbook leave those out
    $make = {
        param($name)
        $s = New-Object CalcFlow.XOutSheet 'T'
        $s.Add('Mark', 'Size', 'Grade'); $s.Add('A', 1, 'x'); $s.Add('B', 2, 'y')
        $t = New-Object CalcFlow.XOutTable; $t.Name = 'tblT'; $t.Top = 0; $t.Bottom = 2; $t.Left = 0; $t.Right = 2; $s.Tables.Add($t)
        $p = Join-Path $dir $name
        [CalcFlow.XlsxWriter]::Write($p, [CalcFlow.XOutSheet[]]@($s), $null)
        $p
    }
    $good = & $make 'rules-good.xlsx'
    Assert-Equal ([CalcFlow.XlsxCheck]::Tables($good)).Count 0
    $table = 'xl/tables/table1.xml'; $sheet = 'xl/worksheets/sheet1.xml'
    $p = & $make 'patched-rules-name.xlsx'
    Edit-XlsxPart $p $table { param($x) $x.Replace('name="Size"', 'name="Size (in)"') }
    Assert-Match ([CalcFlow.XlsxCheck]::Tables($p) -join '; ') "B1 reads 'Size' but the column is named 'Size \(in\)'"
    $p = & $make 'patched-rules-long.xlsx'
    Edit-XlsxPart $p $table { param($x) $x.Replace('name="Size"', 'name="' + ('S' * 300) + '"') }
    Assert-Match ([CalcFlow.XlsxCheck]::Tables($p) -join '; ') 'B1 has a name of 300 characters \(255 at most\)'
    $p = & $make 'patched-rules-alike.xlsx'
    Edit-XlsxPart $p $table { param($x) $x.Replace('name="Grade"', 'name="SIZE"') }
    Assert-Match ([CalcFlow.XlsxCheck]::Tables($p) -join '; ') "C1 'SIZE' is the same name as 'Size'"
    $p = & $make 'patched-rules-number.xlsx'
    Edit-XlsxPart $p $sheet { param($x) [regex]::Replace($x, '<c r="B1"[^>]*>.*?</c>', '<c r="B1"><v>12</v></c>') }
    Assert-Match ([CalcFlow.XlsxCheck]::Tables($p) -join '; ') "B1 is not a text cell \(n 12\): a heading 'Size' must be text"
    $p = & $make 'patched-rules-rows.xlsx'
    Edit-XlsxPart $p $table { param($x) $x.Replace('A1:C3', 'A1:C1') }
    Assert-Match ([CalcFlow.XlsxCheck]::Tables($p) -join '; ') 'no data row under the headings'
    $p = & $make 'patched-rules-merge.xlsx'
    Edit-XlsxPart $p $sheet { param($x) $x.Replace('</sheetData>', '</sheetData><mergeCells count="1"><mergeCell ref="A2:B2"/></mergeCells>') }
    Assert-Match ([CalcFlow.XlsxCheck]::Tables($p) -join '; ') 'merged cells A2:B2 inside the table'
}

Test "a note beside a cell: on the drop-down the cell has, else alone; within Excel's lengths; the check finds one that is not" {
    $s = New-Object CalcFlow.XOutSheet 'N'
    $s.Add('note row'); $s.Add('Mark', 'Size', 'Grade', 'Side'); $s.Add('A', 1, 'x', 'L')
    $v = New-Object CalcFlow.XValidation; $v.Sqref = 'C3:D50'; $v.List = '"x,y"'; $s.Validations.Add($v)
    [CalcFlow.Hints]::Column($s, 1, 1, 2, 'Size', ('The size of the thing. ' * 20))
    [CalcFlow.Hints]::Column($s, 3, 1, 2, ('A title that is far too long for a note' * 2), 'Which side.')
    # the drop-down over two columns became one a column, and the note went to the right one
    Assert-Equal (@($s.Validations | ForEach-Object { "$($_.Sqref)[$($_.List)]" }) -join ' ') 'C3:C50["x,y"] D3:D50["x,y"] B2[] B3:B2000[] D2[]'
    Assert-Equal "$($s.Validations[0].Prompt)|$($s.Validations[1].Prompt)" '|Which side.'
    $p = Join-Path $dir 'notes.xlsx'
    [CalcFlow.XlsxWriter]::Write($p, [CalcFlow.XOutSheet[]]@($s), $null)
    Assert-Equal ([CalcFlow.XlsxCheck]::Validations($p) -join '; ') '' 'the writer cuts a title at 32 characters and a text at 255'
    $bad = Join-Path $dir 'patched-notes.xlsx'
    Copy-Item -LiteralPath $p -Destination $bad
    Edit-XlsxPart $bad 'xl/worksheets/sheet1.xml' { param($x) $x.Replace('prompt="Which side."', 'prompt="' + ('w' * 300) + '"').Replace('sqref="B3:B2000"', 'sqref="B3:C2000"') }
    $found = [CalcFlow.XlsxCheck]::Validations($bad) -join '; '
    Assert-Match $found 'validation D3:D50 has a prompt of 300 characters \(255 at most\)'
    Assert-Match $found 'the validations C3:C50 and B3:C2000 share a cell'
}

Test 'excel-check lists what Excel repairs in a workbook, and with the repaired copy what Excel changed' {
    $o = Invoke-CfCli excel-check -Workbook (Join-Path $dir 'rules-good.xlsx')
    Assert-Equal $LASTEXITCODE 0 ($o -join "`n")
    Assert-Match ($o -join "`n") 'nothing in them, or in the drop-downs and cell notes, breaks a rule of Excel'
    $bad = Join-Path $dir 'patched-rules-long.xlsx'
    # Excel's repair, played: the name cut to 255 characters in the table and in its cell
    $fixed = Join-Path $dir 'patched-rules-long-repaired.xlsx'
    Copy-Item -LiteralPath $bad -Destination $fixed
    Edit-XlsxPart $fixed 'xl/tables/table1.xml' { param($x) $x.Replace(('S' * 300), ('S' * 255)) }
    $o = Invoke-CfCli excel-check -Workbook $bad -Repaired $fixed
    Assert-Equal $LASTEXITCODE 1 ($o -join "`n")
    $all = $o -join "`n"
    Assert-Match $all '\[fix  \] patched-rules-long\.xlsx xl/tables/table1\.xml \(tblT, sheet1\.xml A1:C3\): B1 has a name of 300 characters \(255 at most\)'
    Assert-Match $all "\[excel\] tab T, table tblT: B1 'S+\.\.\.' \(300 characters\) became 'S+\.\.\.'"
    Assert-Match $all '1 change\(s\) by Excel in the tables'
}

Test "writer keeps to Excel's limits: too many rows or columns refused before a byte is written, long text cut visibly" {
    $r = New-Object CalcFlow.XOutSheet 'Rows'; $r.Set([CalcFlow.XlsxWriter]::MaxRows, 0, 'x')
    $p = Join-Path $dir 'rows.xlsx'
    Assert-Throws { [CalcFlow.XlsxWriter]::Write($p, [CalcFlow.XOutSheet[]]@($r), $null) } '1048577 rows; Excel holds 1048576'
    Assert-True (-not (Test-Path -LiteralPath $p)) 'nothing written'
    $c = New-Object CalcFlow.XOutSheet 'Cols'; $c.Set(0, [CalcFlow.XlsxWriter]::MaxCols, 'x')
    Assert-Throws { [CalcFlow.XlsxWriter]::Write((Join-Path $dir 'cols.xlsx'), [CalcFlow.XOutSheet[]]@($c), $null) } '16385 columns'
    $t = New-Object CalcFlow.XOutSheet 'Text'; $t.Add(('y' * 40000)); $t.Add('short')
    $p = Join-Path $dir 'text.xlsx'
    [CalcFlow.XlsxWriter]::Write($p, [CalcFlow.XOutSheet[]]@($t), $null)
    $back = [CalcFlow.Xlsx]::Read($p).Sheet('Text')
    Assert-Equal $back.Text(0, 0).Length 32767
    Assert-Match $back.Text(0, 0) 'y \[\.\.\. 7\d{3} more characters\]$'
    $cut = $back.Text(0, 0); $shown = $cut.IndexOf(' [', [StringComparison]::Ordinal)
    Assert-Equal ($shown + [int]([regex]::Match($cut, '(\d+) more').Groups[1].Value)) 40000 'the count is exact'
    Assert-Equal $back.Text(1, 0) 'short'
}

Test 'reads the generic CAESAR output workbook (Pluto test job)' {
    $wb = [CalcFlow.Xlsx]::Read((Join-Path (Join-Path (Join-Path (Join-Path $script:Repo 'scripts') 'caesar') 'testdata') 'Sample Caesar II ouputs.xlsx'))
    Assert-Equal $wb.Sheets.Count 21
    Assert-True ($null -ne $wb.Sheet('Restraint Summary'))
    Assert-Equal $wb.Sheet('Restraint Summary').Text(18, 0) '1980'
}

Test 'csv and text files read as one sheet' {
    $csv = Join-Path $dir 'a.csv'
    [IO.File]::WriteAllText($csv, "Node,Load Case,FX`r`n10,""1(OPE)"",""1,234""`r`n")
    $wb = [CalcFlow.Xlsx]::Read($csv)
    Assert-Equal $wb.Sheets[0].Text(1, 2) '1,234'
    $txt = Join-Path $dir 'b.txt'
    [IO.File]::WriteAllText($txt, "line one`r`n`tline two`r`n")
    $wb2 = [CalcFlow.Xlsx]::Read($txt)
    Assert-Equal $wb2.Sheets[0].Text(1, 0) 'line two'
}

Test 'cell references, column names and _xHHHH_ escapes' {
    $c = 0; $r = 0
    Assert-True ([CalcFlow.Xlsx]::ParseRef('AB12', [ref]$c, [ref]$r))
    Assert-Equal $c 27; Assert-Equal $r 11
    Assert-Equal ([CalcFlow.Xlsx]::ColName(0)) 'A'
    Assert-Equal ([CalcFlow.Xlsx]::ColName(27)) 'AB'
    Assert-Equal ([CalcFlow.Xlsx]::DecodeX('a_x000D_b')) "a`rb"
}
