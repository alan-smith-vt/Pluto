# State.Tests.ps1 -- CSV, natural sort, calc run state
# Copied from pipe-review (tests/State.Tests.ps1 at d4b33aa, 2026-11-06): namespace CalcFlow; the load archive and
# design-rev tests stay in pipe-review (those classes were not copied); a calc-state round trip is added.

$dir = New-TestDir 'state'

Test 'CSV round trip keeps commas, quotes, line breaks and numbers' {
    $p = Join-Path $dir 'a.csv'
    $data = New-Object 'System.Collections.Generic.List[object[]]'
    $data.Add([object[]]@('x,y', 'say "hi"', 1.5))
    $data.Add([object[]]@("two`nlines", '', -0.25))
    [CalcFlow.Csv]::Write($p, [string[]]@('A', 'B', 'C'), $data)
    $rows = [CalcFlow.Csv]::ReadAll($p)
    Assert-Equal $rows.Count 3
    Assert-Equal $rows[1][0] 'x,y'
    Assert-Equal $rows[1][1] 'say "hi"'
    Assert-Equal $rows[2][0] "two`nlines"
    Assert-Equal $rows[2][2] '-0.25'
    $t = [CalcFlow.Csv]::ReadTable($p)
    Assert-Equal $t[0]['c'] '1.5'
}

Test 'natural sort orders revision names the way people number them' {
    $s = [CalcFlow.NaturalComparer]::Sort([string[]]@('R10', 'R2', 'R01', 'Rev B', 'Rev A', 'V7', 'V6'))
    Assert-Equal ($s -join ',') 'R01,R2,R10,Rev A,Rev B,V6,V7'
}

Test 'calc state saves and loads by results tab' {
    $store = New-Object CalcFlow.CalcStateStore
    $c = New-Object CalcFlow.CalcState
    $c.Calc = 'BC-ST-4'; $c.Tab = 'G-BC-03'; $c.Template = 'shear-tab.xlsx'; $c.InputHash = 'abc123'; $c.Result = 'OK'
    $store.ByTab[$c.Tab] = $c
    $p = Join-Path $dir 'calc-state.csv'
    $store.Save($p)
    $back = [CalcFlow.CalcStateStore]::Load($p)
    Assert-Equal $back.Get('g-bc-03').InputHash 'abc123'
    Assert-Equal $back.Get('G-BC-03').Calc 'BC-ST-4'
    Assert-Equal $back.Get('nothing') $null
}
