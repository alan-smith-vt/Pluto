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
