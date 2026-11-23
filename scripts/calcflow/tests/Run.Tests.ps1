# Run.Tests.ps1 -- the calc stage with the tests' engine (CalcFlow.ScriptedEngine), on the fixture project: jobs from
# the plan, cells written where the library says (in the template's units, text as text), the governing case per
# check and per connection of a group, records per check, invalid and failed cases, what is due on the next run,
# a new revision carried forward, and a stale plan refused. The engine's DCRs are computed from the load cells it
# finds in each copy, so a wrong write shows up as a wrong DCR.

$dir = New-TestDir 'run'
$P = New-CfFixtureProject (Join-Path $dir 'proj') $null
$plan = New-CfFixturePlan $P
$lib = [CalcFlow.Library]::Load($P.Library)
$script:Spec = Get-Spec $lib

# the expected DCR of each demand row, from demands.csv
function Get-Expected($Plan) {
    $x = @{}
    foreach ($d in $Plan.Demands) {
        $key = "$($d['Calc'])|$($d['Step'])|$($d['Row'])"
        $x[$key] = ([Math]::Abs([double]$d['Fx (kip)']) + [Math]::Abs([double]$d['Fy (kip)']) + [Math]::Abs([double]$d['Fz (kip)'])) / 10.0
    }
    return $x
}
$expected = Get-Expected $plan

function New-Runner([string]$Work, [string]$Rev) {
    return New-Object CalcFlow.CalcRunner -ArgumentList @($Work, $Rev, $P.Package)
}

function Read-Checks([string]$Work, [string]$Calc, [int]$Step) {
    return @([CalcFlow.Csv]::ReadTable((Join-Path $Work "data/results/${Calc}__s$Step.checks.csv")))
}

Test 'jobs from the plan: one per ready calc step, an evaluation per demand row, inputs at their cells (names resolved), text kept as text' {
    $jobs = [CalcFlow.CalcJob]::FromPlan($plan, $lib, $P.Templates)
    Assert-Equal (@($jobs | ForEach-Object { "$($_.Calc)/$($_.Step)/$($_.Template)" }) -join ' ') 'G-BP/1/BP J1-M4A/1/WG-RECT J1-M4A/2/WG-RECT J2-M2A/1/WG-I J3-M2B/1/WG-I'
    foreach ($j in $jobs) {
        Assert-Equal $j.Problem ''
        Assert-Equal $j.Units.Count @($plan.Demands | Where-Object { $_['Calc'] -eq $j.Calc -and [int]$_['Step'] -eq $j.Step }).Count "$($j.Key) units"
        Assert-Equal $j.InputHash.Length 64
    }
    $bp = @($jobs | Where-Object { $_.Template -eq 'BP' })[0]
    $w = @($bp.Units[0].Writes | Where-Object { $_.Address -eq 'B5' -and $_.Sheet -eq 'Calc' })
    Assert-Equal $w.Count 1 'Bp (a defined name) is written at Calc!B5'
    Assert-True ($w[0].Value -is [double]) 'a number is written as a number'
    $rect = @($jobs | Where-Object { $_.Key -eq 'J1-M4A|2' })[0]
    $method = @($rect.Units[0].Writes | Where-Object { $_.Address -eq 'B5' })[0]
    Assert-Equal "$($method.Sheet)|$($method.Value)" 'Rect. Fillet Weld Group|LRFD'
    $wleg = @($rect.Units[0].Writes | Where-Object { $_.Address -eq 'B6' })[0]
    Assert-Equal $wleg.Value 0.375 'the step override'
    Assert-Equal (@($rect.Reads | ForEach-Object { "$($_.Label)=$($_.Sheet)!$([CalcFlow.Xlsx]::Ref($_.Row, $_.Col))" }) -join ' ') 'Governing DCR=Rect. Fillet Weld Group!F49'
}

$work = Join-Path $dir 'work'
$eng = New-Engine $null
$runner = New-Runner $work 'R01'
$jobs = [CalcFlow.CalcJob]::FromPlan($plan, $lib, $P.Templates)
$due = $runner.Due($jobs, $false, $null)
$sum = $runner.Run($eng, $due)

Test 'a run: every case calculated from its own written loads, the largest DCR per check with its case, per connection of a group' {
    Assert-Equal "$($sum.Jobs) $($sum.Ok) $($sum.Invalid) $($sum.Failed)" '5 5 0 0'
    Assert-Equal $sum.Evaluations @($plan.Demands).Count
    foreach ($j in $jobs) {
        $rows = @([CalcFlow.Csv]::ReadTable((Join-Path $work "data/results/$($j.Calc)__s$($j.Step).csv")))
        foreach ($r in $rows) { Assert-Near ([double]$r['Governing DCR']) $expected["$($j.Calc)|$($j.Step)|$($r['Row'])"] 1e-9 "$($j.Key) row $($r['Row'])" }
        $whole = @(Read-Checks $work $j.Calc $j.Step | Where-Object { $_['Connection'] -eq '' })
        $best = $rows | Sort-Object { [double]$_['Governing DCR'] } -Descending | Select-Object -First 1
        Assert-Near ([double]$whole[0]['Max DCR']) ([double]$best['Governing DCR']) 1e-12 $j.Key
        Assert-Equal $whole[0]['LC'] $best['LC'] "$($j.Key) governing case"
    }
    $g = Read-Checks $work 'G-BP' 1
    Assert-Equal (@($g | ForEach-Object { $_['Connection'] }) -join ',') ',J1-M1A,J4-M3B'
    foreach ($c in 'J1-M1A', 'J4-M3B') {
        $mine = @($plan.Demands | Where-Object { $_['Calc'] -eq 'G-BP' -and $_['Connection'] -eq $c } | ForEach-Object { $expected["G-BP|1|$($_['Row'])"] } | Measure-Object -Maximum).Maximum
        Assert-Near ([double]@($g | Where-Object { $_['Connection'] -eq $c })[0]['Max DCR']) $mine 1e-12 $c
    }
}

Test 'records: the calculated copy of each governing case in records\<rev>\<calc>, with its stamp; the state names them' {
    $st = [CalcFlow.CalcStateStore]::Load((Join-Path $work 'data/calc-state.csv'))
    foreach ($j in $jobs) {
        $s = $st.Get($j.Calc, $j.Step)
        Assert-Equal "$($s.Result) $($s.Rev) $($s.Engine)" 'OK R01 Scripted'
        Assert-Equal $s.InputHash $j.InputHash
        $rec = Join-Path $work $s.Records
        Assert-True (Test-Path -LiteralPath $rec) "no record $rec"
        Assert-Match $s.Records ("^records[\\/]R01[\\/]" + [regex]::Escape($j.Calc) + "[\\/]$($j.Step)-$($j.Template)-") $s.Records
        $json = [IO.File]::ReadAllText($rec + '.calcflow.json') | ConvertFrom-Json
        Assert-Equal "$($json.engine) $($json.rev) $($json.inputHash)" "Scripted R01 $($j.InputHash)"
        $top = @(Read-Checks $work $j.Calc $j.Step | Where-Object { $_['Connection'] -eq '' })[0]
        Assert-Equal $json.governs[0].lc $top['LC']
        # the record is the copy the DCR was read from
        $b = [CalcFlow.TemplateBook]::Open($rec)
        $out = @($j.Reads | Where-Object { $_.Out.Kind -eq 'dcr' })[0]
        Assert-Near ([double]$b.Sheet($out.Sheet).Get($out.Row, $out.Col).Value) ([double]$top['Max DCR']) 1e-12
    }
    Assert-Equal @(Get-ChildItem -LiteralPath (Join-Path $work 'run') -Directory -ErrorAction SilentlyContinue).Count 0
}

Test 'the next run: nothing is due; a changed input makes its one step due; a missing record makes its step due' {
    $r2 = New-Runner $work 'R01'
    Assert-Equal $r2.Due($jobs, $false, $null).Count 0
    Assert-Equal $r2.Due($jobs, $true, $null).Count 5 '-All'
    $calcs = New-Object 'System.Collections.Generic.List[string]'; $calcs.Add('J2-M2A')
    Assert-Equal (@($r2.Due($jobs, $true, $calcs) | ForEach-Object { $_.Key }) -join ' ') 'J2-M2A|1' '-Calc'
    # an edited plan value (as a re-plan with another parameter would give)
    $copy = Join-Path $dir 'plan-edited'
    Copy-Item -LiteralPath $P.Plan -Destination $copy -Recurse
    $text = [IO.File]::ReadAllText((Join-Path $copy 'plan.csv'))
    $line = @($text -split "`r`n" | Where-Object { $_ -match '^J1-M4A,BRC-GUSSET,2,.*,B6,' })[0]
    [IO.File]::WriteAllText((Join-Path $copy 'plan.csv'), $text.Replace($line, ($line -replace ',0\.375,', ',0.5,')), (New-Object Text.UTF8Encoding($true)))
    $edited = [CalcFlow.CalcJob]::FromPlan([CalcFlow.PlanSet]::Load($copy), $lib, $P.Templates)
    $d = @($r2.Due($edited, $false, $null))
    Assert-Equal (@($d | ForEach-Object { "$($_.Key): $($_.Due)" }) -join '; ') 'J1-M4A|2: inputs changed'
    $st = [CalcFlow.CalcStateStore]::Load((Join-Path $work 'data/calc-state.csv'))
    Remove-Item -LiteralPath (Join-Path $work $st.Get('J2-M2A', 1).Records)
    Assert-Equal (@($r2.Due($jobs, $false, $null) | ForEach-Object { "$($_.Key): $($_.Due)" }) -join '; ') 'J2-M2A|1: a record is missing'
    $s = $r2.Run($eng, $r2.Due($jobs, $false, $null))
    Assert-Equal "$($s.Jobs) $($s.Ok)" '1 1'
    Assert-Equal $r2.Due($jobs, $false, $null).Count 0
}

Test 'a new revision with unchanged inputs carries each record forward instead of calculating again' {
    $r3 = New-Runner $work 'R02'
    $d = $r3.Due($jobs, $false, $null)
    Assert-Equal (@($d | ForEach-Object { $_.Due } | Select-Object -Unique) -join '|') 'new revision R02 (carried from R01)'
    $calls = $eng.Calls
    $s = $r3.Run($eng, $d)
    Assert-Equal "$($s.Carried) $($s.Evaluations)" '5 0'
    Assert-Equal $eng.Calls $calls 'the engine was not called'
    $st = [CalcFlow.CalcStateStore]::Load((Join-Path $work 'data/calc-state.csv'))
    foreach ($j in $jobs) {
        $x = $st.Get($j.Calc, $j.Step)
        Assert-Equal "$($x.Rev) $($x.CarriedFrom)" 'R02 R01'
        Assert-Match $x.Records '^records[\\/]R02[\\/]'
        Assert-True (Test-Path -LiteralPath (Join-Path $work $x.Records)) $x.Records
        $json = [IO.File]::ReadAllText((Join-Path $work $x.Records) + '.calcflow.json') | ConvertFrom-Json
        Assert-Equal "$($json.rev) $($json.carriedFrom) $($json.carriedTo)" 'R01 R01 R02'
        Assert-Match @(Read-Checks $work $j.Calc $j.Step | Where-Object { $_['Connection'] -eq '' })[0]['Record'] '^records[\\/]R02[\\/]'
    }
    Assert-True (Test-Path -LiteralPath (Join-Path $work 'records/R01')) 'the R01 records stay'
    Assert-Equal $r3.Due($jobs, $false, $null).Count 0
}

Test 'two checks: one record when the same case governs both, one each when different cases do' {
    $twoLib = Join-Path $dir 'two-checks'
    Copy-Item -LiteralPath (Join-Path $P.Library 'text') -Destination (Join-Path $twoLib 'text') -Recurse
    $o = Join-Path (Join-Path $twoLib 'text') 'outputs.csv'
    [IO.File]::AppendAllText($o, "WG-RECT,Rect. Fillet Weld Group,F48,,,Base metal DCR,dcr,Base metal,,N,,,,`r`n", (New-Object Text.UTF8Encoding($false)))
    $lib2 = [CalcFlow.Library]::Load($twoLib)
    $P2 = New-CfFixtureProject (Join-Path $dir 'proj2') $null
    $plan2 = New-CfFixturePlan $P2 $twoLib
    $jobs2 = @([CalcFlow.CalcJob]::FromPlan($plan2, $lib2, $P2.Templates) | Where-Object { $_.Calc -eq 'J1-M4A' })
    $script:Spec = Get-Spec $lib2
    # the base metal check: half the weld's value at step 2 (the step with the 0.375 weld: the same governing case);
    # at step 1 largest where the weld's is smallest (a different case)
    $e2 = New-Engine { param($b, $t, $d)
        if ($t.Template -ne 'WG-RECT') { return }
        $weld = [double]$d['Rect. Fillet Weld Group!F49']
        if ([double](Get-Cell $b 'Rect. Fillet Weld Group!B6').Value -eq 0.375) { $d['Rect. Fillet Weld Group!F48'] = $weld / 2 }
        else { $d['Rect. Fillet Weld Group!F48'] = 1 / (1 + $weld) }
    }
    $w2 = Join-Path $dir 'work2'
    $r = New-Runner $w2 'R01'
    [void]$r.Run($e2, [CalcFlow.CalcJob[]]$jobs2)
    $script:Spec = Get-Spec $lib
    foreach ($step in 1, 2) {
        $k = @(Read-Checks $w2 'J1-M4A' $step | Where-Object { $_['Connection'] -eq '' })
        Assert-Equal (@($k | ForEach-Object { $_['Check'] }) -join ',') 'Weld,Base metal'
        $files = @(Get-ChildItem -LiteralPath (Join-Path $w2 'records/R01/J1-M4A') -Filter "$step-*.xlsx" | ForEach-Object { $_.Name })
        if ($step -eq 2) {
            Assert-Equal $k[0]['Row'] $k[1]['Row'] 'step 2: one case governs both'
            Assert-Equal ($files -join ' ') '2-WG-RECT-Weld+Base metal.xlsx'
        }
        else {
            Assert-True ($k[0]['Row'] -ne $k[1]['Row']) 'step 1: different cases govern'
            Assert-Equal (($files | Sort-Object) -join ' ') '1-WG-RECT-Base metal.xlsx 1-WG-RECT-Weld.xlsx'
        }
        foreach ($c in $k) { Assert-Match $c['Record'] ([regex]::Escape($c['Check'])) }
    }
}

Test 'invalid cases: a solver check off its Expect, a DCR that is an error value; the step keeps its first invalid case' {
    $bad = New-Engine { param($b, $t, $d)
        if ($t.Template -eq 'BP') { $d['Rigid!Converged'] = $false }
        if ($t.Template -eq 'WG-I') { $d['I Fillet Weld Group!F58'] = [CalcFlow.CellError]::new('#DIV/0!') }
    }
    $w3 = Join-Path $dir 'work3'
    $r = New-Runner $w3 'R01'
    $s = $r.Run($bad, $jobs)
    Assert-Equal "$($s.Ok) $($s.Invalid) $($s.Failed)" '2 3 0' (($bad.Errors + $s.Messages) -join '; ')
    $st = [CalcFlow.CalcStateStore]::Load((Join-Path $w3 'data/calc-state.csv'))
    Assert-Match $st.Get('G-BP', 1).Message 'Converged is FALSE, not TRUE'
    Assert-Match $st.Get('J2-M2A', 1).Message 'Governing DCR is #DIV/0!'
    Assert-Equal $st.Get('G-BP', 1).Result 'INVALID'
    Assert-Match $st.Get('G-BP', 1).Records '1-BP-invalid\.xlsx$'
    $json = [IO.File]::ReadAllText((Join-Path $w3 $st.Get('G-BP', 1).Records) + '.calcflow.json') | ConvertFrom-Json
    Assert-Match $json.invalid 'Converged'
    $rows = @([CalcFlow.Csv]::ReadTable((Join-Path $w3 'data/results/J2-M2A__s1.csv')))
    Assert-Equal (@($rows | ForEach-Object { $_['Valid'] }) -join '') ('N' * $rows.Count)
}

Test 'a case the engine cannot calculate fails its step; the next run takes the failed step again, and only it' {
    $flaky = New-Engine { param($b, $t, $d) if ($t.Template -eq 'BP' -and $script:FailNow) { throw 'engine fell over' } }
    $script:FailNow = $true
    $w4 = Join-Path $dir 'work4'
    $r = New-Runner $w4 'R01'
    $s = $r.Run($flaky, $jobs)
    Assert-Equal "$($s.Ok) $($s.Failed)" '4 1' ($s.Messages -join '; ')
    Assert-Match ($flaky.Errors -join ' ') 'engine fell over'
    $st = [CalcFlow.CalcStateStore]::Load((Join-Path $w4 'data/calc-state.csv'))
    Assert-Equal $st.Get('G-BP', 1).Result 'FAILED'
    Assert-Match $st.Get('G-BP', 1).Message 'not calculated: the engine did not calculate it'
    Assert-Equal $st.Get('G-BP', 1).Records '' 'no record of a failed step'
    $script:FailNow = $false
    $d = $r.Due($jobs, $false, $null)
    Assert-Equal (@($d | ForEach-Object { "$($_.Key): $($_.Due)" }) -join '; ') 'G-BP|1: failed last time'
    $s = $r.Run($flaky, $d)
    Assert-Equal "$($s.Ok) $($s.Failed)" '1 0'
}

Test 'a case table takes MaxCases rows per evaluation, each row of per-case cells one row further down' {
    $tl = Join-Path $dir 'table-lib'
    Copy-Item -LiteralPath (Join-Path $P.Library 'text') -Destination (Join-Path $tl 'text') -Recurse
    $t = Join-Path (Join-Path $tl 'text') 'templates.csv'
    $text = [IO.File]::ReadAllText($t)
    $text = $text -replace '(WG-RECT,Weld Group Design Tool\.xlsx,[0-9a-f]+,)single_case,', '${1}case_table,2'
    [IO.File]::WriteAllText($t, $text, (New-Object Text.UTF8Encoding($true)))
    $libT = [CalcFlow.Library]::Load($tl)
    $P3 = New-CfFixtureProject (Join-Path $dir 'proj3') $null
    $plan3 = New-CfFixturePlan $P3 $tl
    $j = @([CalcFlow.CalcJob]::FromPlan($plan3, $libT, $P3.Templates) | Where-Object { $_.Key -eq 'J1-M4A|2' })[0]
    $rows = @($plan3.Demands | Where-Object { $_['Calc'] -eq 'J1-M4A' -and $_['Step'] -eq '2' }).Count
    Assert-Equal $j.Units.Count ([Math]::Ceiling($rows / 2))
    Assert-Equal (@($j.Units[0].Rows | ForEach-Object { $_.Row }) -join ',') '1,2'
    $fx = @($j.Units[0].Writes | Where-Object { $_.Address -match '^B1[45]$|^B1[56]$' -and $_.Sheet -eq 'Rect. Fillet Weld Group' } | ForEach-Object { $_.Address })
    Assert-Equal ($fx -join ' ') 'B14 B15 B15 B16' 'Fx and Mx of row 1, then of row 2 one row down'
}

Test 'CalcFlow.ps1 run refuses a plan whose register changed since it was made' {
    $P5 = New-CfFixtureProject (Join-Path $dir 'proj5') $null
    [void](New-CfFixturePlan $P5)
    [IO.File]::AppendAllText($P5.Register, ' ')
    $o = Invoke-CfCli run -Plan $P5.Plan -Engine libreoffice
    Assert-Equal $LASTEXITCODE 2 ($o -join "`n")
    Assert-True (@($o | Where-Object { $_ -like '*the register*changed since the plan was made*' }).Count -eq 1) ($o -join "`n")
    Remove-Item -LiteralPath (Join-Path $P5.Plan 'plan-manifest.txt')
    $o = Invoke-CfCli run -Plan $P5.Plan
    Assert-Equal $LASTEXITCODE 2
    Assert-True (@($o | Where-Object { $_ -like '*no plan-manifest.txt*' }).Count -eq 1) ($o -join "`n")
}

Test "a copy with its cells written adds no schema error to its template's own (Open XML SDK); records are such copies" {
    foreach ($j in @($jobs | Where-Object { $_.Key -in 'G-BP|1', 'J1-M4A|1' })) {
        $copy = Join-Path $dir ("patched-" + $j.Template + ".xlsx")
        [CalcFlow.XlsxPatch]::Write($j.TemplatePath, $copy, $j.Units[0].Writes)
        $mine = Invoke-CfOoxmlCheck @($copy)
        if ($null -eq $mine) { Write-Host '        (no dotnet SDK here: skipped)' -ForegroundColor DarkGray; return }
        $theirs = Invoke-CfOoxmlCheck @($j.TemplatePath)
        $strip = { param($lines, $file) @($lines | ForEach-Object { $_.Substring($_.IndexOf(': ') + 2) }) }
        Assert-Equal ((& $strip $mine $copy) -join "`n") ((& $strip $theirs $j.TemplatePath) -join "`n") $j.Template
    }
}
