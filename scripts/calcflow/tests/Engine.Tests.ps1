# Engine.Tests.ps1 -- the fixture project calculated for real by LibreOffice (open source, headless), through
# CalcFlow.ps1 plan and run: the DCRs of the generic templates (the base plate's and the rectangular weld's were
# cross-checked against EngCalc-Templates' own Python references, baseplate_ref and weld_ref, to 1e-14), the base
# plate's solver checks, the records, and a second run with nothing due. Skipped where LibreOffice is not installed.
# Excel.Tests.ps1 runs the same project through Excel where Excel is installed.

$script:EngineExpect = [ordered]@{
    'G-BP|1'   = @('Base plate', 1.20014815948496, '102')
    'J1-M4A|1' = @('Weld', 0.426465503693227, '3')
    'J1-M4A|2' = @('Weld', 0.157950186553047, '3')
    'J2-M2A|1' = @('Weld', 0.0910476377314831, '3')
    'J3-M2B|1' = @('Weld', 0.286163797995741, '3')
}

# plan and run the fixture project with -Engine $Engine; then the checks every engine must pass
function Test-CfEngine([string]$Engine, [string]$Name, [double]$Tolerance) {
    $dir = New-TestDir "engine-$Engine"
    $P = New-CfFixtureProject (Join-Path $dir 'proj') $null
    [void](New-CfFixturePlan $P)
    $o = Invoke-CfCli run -Plan $P.Plan -Engine $Engine
    Assert-Equal $LASTEXITCODE 0 ($o -join "`n")
    Assert-True (@($o | Where-Object { $_ -like 'run      5 calc steps: 5 OK, 0 invalid, 0 failed*' }).Count -eq 1) ($o -join "`n")
    Assert-True (@($o | Where-Object { $_ -like "engine   $Name *" }).Count -eq 1) ($o -join "`n")
    $st = [CalcFlow.CalcStateStore]::Load((Join-Path $P.Work 'data/calc-state.csv'))
    foreach ($k in $script:EngineExpect.Keys) {
        $calc, $step = $k.Split('|')
        $x = @([CalcFlow.Csv]::ReadTable((Join-Path $P.Work "data/results/${calc}__s$step.checks.csv")) | Where-Object { $_['Connection'] -eq '' })[0]
        $e = $script:EngineExpect[$k]
        Assert-Equal $x['Check'] $e[0] $k
        Assert-Near ([double]$x['Max DCR']) $e[1] $Tolerance $k
        Assert-Equal $x['LC'] $e[2] "$k governing case"
        $s = $st.Get($calc, [int]$step)
        Assert-Equal "$($s.Result) $($s.Engine)" "OK $Name"
        Assert-True (Test-Path -LiteralPath (Join-Path $P.Work $s.Records)) $s.Records
    }
    # the base plate's solver converged in every case (LibreOffice saves TRUE as 1; Excel as TRUE)
    foreach ($r in [CalcFlow.Csv]::ReadTable((Join-Path $P.Work 'data/results/G-BP__s1.csv'))) {
        Assert-True ([CalcFlow.CalcRunner]::SameValue($r['Converged'], 'TRUE')) "row $($r['Row']) Converged $($r['Converged'])"
        Assert-True ([CalcFlow.CalcRunner]::SameValue($r['Singular'], 'FALSE')) "row $($r['Row']) Singular $($r['Singular'])"
    }
    # the record of the base plate holds the governing case's own loads and its DCR
    $rec = [CalcFlow.TemplateBook]::Open((Join-Path $P.Work $st.Get('G-BP', 1).Records))
    $ts = $null; $r0 = 0; $c0 = 0; $err = ''
    [void]$rec.Locate('Calc', 'dcr_all', [ref]$ts, [ref]$r0, [ref]$c0, [ref]$err)
    Assert-Near ([double]$ts.Get($r0, $c0).Value) $script:EngineExpect['G-BP|1'][1] $Tolerance 'the record'
    $o = Invoke-CfCli run -Plan $P.Plan -Engine $Engine
    Assert-Equal $LASTEXITCODE 0 ($o -join "`n")
    Assert-True (@($o | Where-Object { $_ -like '*every calc step is up to date*' }).Count -eq 1) ($o -join "`n")
    Assert-True (Test-Path -LiteralPath (Join-Path (Join-Path $P.Work 'reports') ('Connection_Summary_' + (Get-Date -Format 'yyyy-MM-dd') + '.xlsx'))) 'summary'
}

Test 'LibreOffice calculates the fixture project: the DCRs, the solver checks, the records, nothing due the second time' {
    if (-not [CalcFlow.LibreOffice]::Find()) { Write-Host '        (no LibreOffice here: skipped)' -ForegroundColor DarkGray; return }
    Test-CfEngine 'libreoffice' 'LibreOffice' 1e-9
}

Test 'XlsxPatch: numbers, text, a blank, a boolean and an error value land in a copy; the calc chain goes; full calculation on load' {
    $dir = New-TestDir 'patch'
    $t = Join-Path (Join-Path $script:Fixtures 'templates') 'Weld Group Design Tool.xlsx'
    $w = New-Object 'System.Collections.Generic.List[CalcFlow.CellWrite]'
    $sheet = 'Rect. Fillet Weld Group'
    foreach ($x in @(@('F5', [double]60), @('B5', 'ASD'), @('B14', $null), @('Z200', $true), @('Z201', [CalcFlow.CellError]::new('#N/A')), @('A1', ' padded '))) {
        $w.Add([CalcFlow.CellWrite]::new($sheet, $x[0], $x[1]))
    }
    $copy = Join-Path $dir 'copy.xlsx'
    [CalcFlow.XlsxPatch]::Write($t, $copy, $w)
    $b = [CalcFlow.TemplateBook]::Open($copy)
    $s = $b.Sheet($sheet)
    Assert-Equal $s.Get(4, 5).Value '60'
    Assert-Equal "$($s.Get(4, 1).Value)|$($s.Get(4, 1).Type)" 'ASD|inlineStr'
    Assert-True ($s.Get(13, 1) -eq $null -or $s.Get(13, 1).IsEmpty) 'B14 blank'
    Assert-Equal "$($s.Get(199, 25).Value)|$($s.Get(199, 25).Type)" 'TRUE|b'
    Assert-Equal "$($s.Get(200, 25).Value)|$($s.Get(200, 25).Type)" '#N/A|e'
    Assert-Equal $s.Get(0, 0).Value ' padded '
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
    $z = [IO.Compression.ZipFile]::OpenRead($copy)
    try {
        Assert-Equal @($z.Entries | Where-Object { $_.FullName -like '*calcChain*' }).Count 0
        $rd = New-Object IO.StreamReader(($z.Entries | Where-Object { $_.FullName -eq 'xl/workbook.xml' }).Open())
        Assert-Match $rd.ReadToEnd() 'fullCalcOnLoad="1"'
        $rd.Dispose()
    }
    finally { $z.Dispose() }
    Assert-Equal $b.Sheet('I Fillet Weld Group').Get(4, 5).Value ([CalcFlow.TemplateBook]::Open($t).Sheet('I Fillet Weld Group').Get(4, 5).Value) 'the other sheet is untouched'
}
