# Summary.Tests.ps1 -- the connection summary on the fixture project with every status at once: the column-base group
# over the limit (each base with the DCRs of its own demand rows), the brace failed at its second step, one beam end
# never run, the other invalid, an excluded member end and one of an unknown type (a gap). The joint rollup, the
# check columns, the colours, and CalcFlow.ps1 summary.

$dir = New-TestDir 'summary'
$P = New-CfFixtureProject (Join-Path $dir 'proj') @{ 'J5-M5B' = @{ Type = 'BC-WELD'; Disposition = 'Excluded'; Comment = 'by others' }; 'J6-M6A' = @{ Type = 'NO-SUCH-TYPE' } }
$plan = New-CfFixturePlan $P -AllowGaps
$lib = [CalcFlow.Library]::Load($P.Library)
$reg = [CalcFlow.Register]::Load($P.Register)
$script:Spec = Get-Spec $lib
$jobs = [CalcFlow.CalcJob]::FromPlan($plan, $lib, $P.Templates)
$runner = New-Object CalcFlow.CalcRunner -ArgumentList @($P.Work, 'R01', $P.Package)

# run 1: the bases at 2 + the usual DCR; the brace's second step (the 0.375 weld) falls over; J2-M2A left out
$e1 = New-Engine { param($b, $t, $d)
    if ($t.Template -eq 'BP') { $d['Calc!dcr_all'] = 2 + [double]$d['Calc!dcr_all'] }
    if ($t.Template -eq 'WG-RECT' -and [double](Get-Cell $b 'Rect. Fillet Weld Group!B6').Value -eq 0.375) { throw 'engine fell over' }
}
$some = New-Object 'System.Collections.Generic.List[string]'; foreach ($c in 'G-BP', 'J1-M4A', 'J3-M2B') { $some.Add($c) }
[void]$runner.Run($e1, $runner.Due($jobs, $false, $some))
# run 2: J3-M2B again, its DCR an error value
$e2 = New-Engine { param($b, $t, $d) if ($t.Template -eq 'WG-I') { $d['I Fillet Weld Group!F58'] = [CalcFlow.CellError]::new('#DIV/0!') } }
$one = New-Object 'System.Collections.Generic.List[string]'; $one.Add('J3-M2B')
[void]$runner.Run($e2, $runner.Due($jobs, $true, $one))
$sum = [CalcFlow.Summary]::Build($plan, $lib, $reg, $P.Work, 'R01')

function Get-Conn([string]$Id) { return @($sum.Connections | Where-Object { $_.Conn.Connection -eq $Id })[0] }

Test 'every status of the ladder, each with why' {
    $got = @($sum.Connections | Sort-Object { $_.Conn.Connection } | ForEach-Object { "$($_.Conn.Connection)=$($_.Status)" }) -join ' '
    Assert-Equal $got 'J1-M1A=Over limit J1-M4A=Failed J2-M2A=Not run J3-M2B=Invalid J4-M3B=Over limit J5-M5B=Excluded J6-M6A=Gaps'
    Assert-Match (Get-Conn 'J1-M4A').Why 'step 2: .*not calculated'
    Assert-Match (Get-Conn 'J2-M2A').Why 'never run'
    Assert-Match (Get-Conn 'J3-M2B').Why 'Governing DCR is #DIV/0!'
    Assert-Match (Get-Conn 'J6-M6A').Why "type 'NO-SUCH-TYPE' is not in the calc library"
    Assert-Equal (Get-Conn 'J5-M5B').Why 'by others'
    Assert-Match (Get-Conn 'J1-M1A').Why 'Base plate above 1'
}

Test "a group's connections each carry the DCRs of their own demand rows; the check columns; records and engine" {
    foreach ($c in 'J1-M1A', 'J4-M3B') {
        $mine = @($plan.Demands | Where-Object { $_['Calc'] -eq 'G-BP' -and $_['Connection'] -eq $c } | ForEach-Object {
            2 + ([Math]::Abs([double]$_['Fx (kip)']) + [Math]::Abs([double]$_['Fy (kip)']) + [Math]::Abs([double]$_['Fz (kip)'])) / 10 } | Measure-Object -Maximum).Maximum
        $x = Get-Conn $c
        Assert-Near $x.Max $mine 1e-12 $c
        Assert-Equal $x.GoverningCheck 'Base plate'
        Assert-Near ([double]$x.CheckValues['Base plate']) $mine 1e-12
        Assert-Equal "$($x.Engine) $($x.Rev)" 'Scripted 1 R01'
        Assert-Match $x.Records 'records[\\/]R01[\\/]G-BP[\\/]1-BP-Base plate\.xlsx'
    }
    Assert-True ((Get-Conn 'J1-M1A').Max -ne (Get-Conn 'J4-M3B').Max) 'the two bases differ'
    Assert-Equal ($sum.CheckNames -join ',') 'Base plate,Weld'
    Assert-True ((Get-Conn 'J1-M4A').CheckValues.ContainsKey('Weld')) 'the step that ran still shows its DCR'
}

Test 'joints: the worst connection at each, and the counts' {
    $sheets = [CalcFlow.Summary]::Sheets($sum, 'plan')
    $j = $sheets[1]
    Assert-Equal $j.Name 'Joints'
    $rows = @{}
    for ($r = 2; $r -lt $j.Rows.Count; $r++) { $cells = @($j.Rows[$r] | ForEach-Object { if ($_ -is [CalcFlow.XCell]) { "$($_.Value)" } else { "$_" } }); $rows[$cells[0]] = $cells }
    # Joint, Connections, Worst Status, Max DCR, Governing Connection, OK, Over limit, Invalid, Failed, Not run, Gaps, Excluded
    Assert-Equal (($rows['1'])[2, 1, 6, 8] -join ' ') 'Over limit 2 1 1'
    Assert-Equal ($rows['3'])[2] 'Invalid'
    Assert-Equal ($rows['5'])[2] 'Excluded'
    Assert-Equal ($rows['6'])[2] 'Gaps'
}

Test 'the colours: statuses, DCRs above the limit, problems; numbers are numbers' {
    $sheets = [CalcFlow.Summary]::Sheets($sum, 'plan')
    $c = $sheets[0]
    Assert-Equal (@($sheets | ForEach-Object { $_.Name }) -join ' ') 'Connections Joints Calcs Problems'
    $texts = @($c.CondFormats | Where-Object { $_.EqualsText } | ForEach-Object { $_.EqualsText })
    foreach ($t in 'OK', 'Over limit', 'Invalid', 'Failed', 'Not run', 'Excluded') { Assert-True ($texts -contains $t) "no colour for $t" }
    $over = @($c.CondFormats | Where-Object { $_.Operator -eq 'greaterThan' })
    Assert-True ($over.Count -ge 3) 'Max DCR and each check column'
    Assert-Equal $over[0].Value 1
    Assert-Equal "$($over[0].Style.Color)|$($over[0].Style.Bold)|$($over[0].Style.Fill)" 'FF9C0006|True|' 'a DCR above the limit: bold dark red, no fill'
    $ok = @($c.CondFormats | Where-Object { $_.EqualsText -eq 'OK' })[0]; $bad = @($c.CondFormats | Where-Object { $_.EqualsText -eq 'Over limit' })[0]
    Assert-Equal "$($ok.Style.Fill)/$($ok.Style.Color) $($bad.Style.Fill)/$($bad.Style.Color)" 'FFC6EFCE/FF006100 FFFFC7CE/FF9C0006' 'soft fills with dark text of the same hue'
    $maxCol = [Array]::IndexOf([string[]]@($c.Rows[1] | ForEach-Object { $_.Value }), 'Max DCR')
    $first = $c.Rows[2][$maxCol]
    Assert-True ($first.Value -is [double]) 'Max DCR is a number'
    Assert-Equal $first.Style.NumFmt '0.000'
    $p = $sheets[3]
    Assert-True (@($p.CondFormats | Where-Object { $_.EqualsText -eq 'Error' }).Count -eq 1) 'errors coloured'
}

Test 'CalcFlow.ps1 summary writes reports\Connection_Summary_<date>.xlsx and exits 1 while a calc is failed or invalid' {
    $o = Invoke-CfCli summary -Plan $P.Plan
    Assert-Equal $LASTEXITCODE 1 ($o -join "`n")
    $path = Join-Path (Join-Path $P.Work 'reports') ('Connection_Summary_' + (Get-Date -Format 'yyyy-MM-dd') + '.xlsx')
    Assert-True (Test-Path -LiteralPath $path) ($o -join "`n")
    Assert-True (@($o | Where-Object { $_ -like 'summary *7 member ends -- 0 OK, 2 over the limit, 1 invalid, 1 failed, 1 not run, 1 gaps / blocked, 1 excluded' }).Count -eq 1) ($o -join "`n")
    $wb = [CalcFlow.Xlsx]::Read($path)
    Assert-Equal @($wb.TableRows('tblSummaryConnections')).Count 7
    $brace = @([CalcFlow.Csv]::ReadTable((Join-Path $P.Work 'data/results/J1-M4A__s1.checks.csv')) | Where-Object { $_['Connection'] -eq '' })[0]
    $s1 = if ([double]$brace['Max DCR'] -gt 1) { 'Over limit' } else { 'OK' }
    Assert-Equal (@($wb.TableRows('tblSummaryCalcs') | ForEach-Object { "$($_['Calc'])/$($_['Step'])=$($_['Status'])" }) -join ' ') "G-BP/1=Over limit J1-M4A/1=$s1 J1-M4A/2=Failed J2-M2A/1=Not run J3-M2B/1=Invalid J6-M6A/=Gaps"
}
