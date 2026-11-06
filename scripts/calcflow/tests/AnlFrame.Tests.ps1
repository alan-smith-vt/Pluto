# AnlFrame.Tests.ps1 -- the STAAD .anl frame reader (AnlFrameReader), the package writer (FramePackageWriter), the
# indexer's command-end markers (AnlIndexer) and the runner (Export-AnlFrame.ps1), on the made-up fixture
# fixtures\frame-demo.anl: a portal frame in FEET KIP with a BETA 90 column, a beam released at its start, a truss
# brace, a member with an offset, a member along -Z, three kinds of support, two primary loads, a repeat load, an
# algebraic and an SRSS combination; a second force print in KIP INCH; then a support reaction print and a code
# check whose numeric rows must not be read as member forces.

$dir = New-TestDir 'anlframe'
$fixture = Join-Path $script:Fixtures 'frame-demo.anl'
$fixtureText = [IO.File]::ReadAllText($fixture)

# a copy of the fixture with whole lines replaced (text -> text, each must occur)
function New-Variant {
    param([string]$Name, [hashtable]$Replace)
    $t = $fixtureText
    foreach ($k in $Replace.Keys) {
        if ($t.IndexOf($k) -lt 0) { throw "variant ${Name}: '$k' is not in the fixture" }
        $t = $t.Replace($k, $Replace[$k])
    }
    $p = Join-Path $dir "$Name.anl"
    [IO.File]::WriteAllText($p, $t)
    return $p
}

function Get-Codes($model) { return @($model.Problems | ForEach-Object { $_.Code }) }

$m = [AnlFrameReader]::Read($fixture)

Test 'reads the fixture: counts, force units, and only the warnings it should give' {
    Assert-Equal $m.Nodes.Count 7
    Assert-Equal $m.Members.Count 7
    Assert-Equal $m.Supports.Count 3
    Assert-Equal $m.LoadCases.Count 5
    Assert-Equal $m.EndForces.Count 70 'end forces (7 members x 5 cases x 2 ends):'
    Assert-Equal $m.ErrorCount 0 ("errors: " + (($m.ProblemsOf('Error') | ForEach-Object { $_.ToString() }) -join '; '))
    Assert-Equal ((Get-Codes $m | Sort-Object) -join ',') 'FORCE_PRINTED_TWICE,FORCE_UNITS_CONVERTED,MEMBER_OFFSET'
    Assert-Equal $m.ForceUnitText 'kip'
    Assert-Equal $m.MomentUnitText 'kip-ft'
    Assert-Equal $m.Analysis 'PERFORM ANALYSIS'
    Assert-Equal $m.PropertyCountry 'AMERICAN'
    Assert-Equal $m.SourceSha256.Length 64
}

Test 'joints are in inches; members carry their sections and families (a group name in MEMBER PROPERTY)' {
    Assert-Near $m.Nodes[3].X 240
    Assert-Near $m.Nodes[3].Y 144
    Assert-Near $m.Nodes[7].Z -180
    Assert-Equal "$($m.Members[2].NodeA)-$($m.Members[2].NodeB)" '2-3'
    Assert-Equal $m.Members[1].Section 'W14X90'
    Assert-Equal $m.Members[3].Section 'W14X90' 'member 3 from _COLS:'
    Assert-Equal $m.Members[1].Family 'W'
    Assert-Equal $m.Members[4].Family 'HSS'
    Assert-Equal $m.Members[4].Property 'TABLE ST HSST6X6X0.375'
    Assert-Near $m.Members[2].Length 240
    Assert-Near $m.Members[4].Length ([Math]::Sqrt(240 * 240 + 144 * 144)) 1e-9
}

Test "STAAD's local axes: BETA 90 column, beam, column pointing down, member along -Z, brace" {
    $ax = { param($mem) (($mem.LocalX + $mem.LocalY + $mem.LocalZ) | ForEach-Object { [Math]::Round($_, 6) }) -join ' ' }
    Assert-Equal $m.Members[1].Beta 90
    Assert-Equal (& $ax $m.Members[1]) '0 1 0 0 0 1 1 0 0' 'vertical up, BETA 90 (x / y / z):'
    Assert-Equal (& $ax $m.Members[6]) '0 1 0 -1 0 0 0 0 1' 'vertical up, BETA 0:'
    Assert-Equal (& $ax $m.Members[3]) '0 -1 0 1 0 0 0 0 1' 'vertical down, BETA 0:'
    Assert-Equal (& $ax $m.Members[2]) '1 0 0 0 1 0 0 0 1' 'along +X:'
    Assert-Equal (& $ax $m.Members[7]) '0 0 -1 0 1 0 1 0 0' 'along -Z (z = x cross Y):'
    Assert-Equal (& $ax $m.Members[4]) '0.857493 0.514496 0 -0.514496 0.857493 0 0 0 1' 'inclined brace:'
}

Test 'releases, offset, truss member and supports' {
    Assert-Equal $m.Members[2].ReleaseA.Text 'MY MZ'
    Assert-Equal $m.Members[2].ReleaseB.Text ''
    Assert-Equal (($m.Members[5].OffsetA | ForEach-Object { $_ }) -join ' ') '6 0 0' 'START 0.5 ft in inches:'
    Assert-Equal $m.Members[5].OffsetB $null
    Assert-Equal $m.Members[4].Kind 'TRUSS'
    Assert-Equal $m.Supports[1].Type 'FIXED'
    Assert-Equal $m.Supports[1].RestrainedText 'FX FY FZ MX MY MZ'
    Assert-Equal $m.Supports[4].Type 'FIXED BUT'
    Assert-Equal $m.Supports[4].RestrainedText 'FX FY FZ MX MY'
    Assert-Equal $m.Supports[6].Type 'PINNED'
    Assert-Equal $m.Supports[6].RestrainedText 'FX FY FZ'
}

Test 'load cases: primaries with LOADTYPE and TITLE, the repeat load and both combinations with their factors' {
    $c = $m.LoadCases
    Assert-Equal "$($c[1].Kind)|$($c[1].LoadType)|$($c[1].Title)" 'PRIMARY|DEAD|DL'
    Assert-Equal "$($c[2].Kind)|$($c[2].LoadType)|$($c[2].Title)" 'PRIMARY|LIVE|LL'
    Assert-Equal "$($c[3].Kind)|$($c[3].FactorsText)" 'REPEAT|1*1.2 + 2*1.6'
    Assert-Equal $c[3].Title '1.2DL + 1.6LL (REPEAT)'
    Assert-Equal "$($c[101].Kind)|$($c[101].Method)|$($c[101].FactorsText)|$($c[101].Title)" 'COMBINATION||1*1.4|1.4DL'
    Assert-Equal "$($c[102].Kind)|$($c[102].Method)|$($c[102].FactorsText)" 'COMBINATION|SRSS|1*1 + 2*1'
    foreach ($id in 1, 2, 3, 101, 102) { Assert-True $c[$id].HasForces "case $id has forces" }
}

Test 'end forces as STAAD printed them, at the right end; the second print in kip-in converted to kip-ft' {
    $expect = @{
        '1/1/A' = @(9.5, 0.85, 0.05, 0.02, 0.3, 2.9)
        '1/1/B' = @(-9.1, -0.85, -0.05, -0.02, 0.15, 4.06)
        '3/2/B' = @(-16.577, -1.85, -0.206, -0.082, 0.616, 9.015)
        '3/3/A' = @(42.4, 4.58, 0.51, 0.203, 3.053, 15.942)
        '4/2/A' = @(19.18, 0, 0, 0, 0, 0)
        '7/102/B' = @(30.7, 3.986, 0.594, 0.238, 1.781, 19.709)
        '2/3/A' = @(37.312, 3.731, 0.339, 0.136, 0, 0)
        '2/101/B' = @(-14.84, -1.54, -0.14, -0.056, 0.42, 7.448)
    }
    foreach ($k in $expect.Keys) {
        $p = $k.Split('/')
        $f = $m.EndForce([int]$p[0], $p[2], [int]$p[1])
        Assert-True ($null -ne $f) "no end force $k"
        for ($i = 0; $i -lt 6; $i++) { Assert-Near ([double]$f.F[$i]) $expect[$k][$i] 2e-4 "$k component $i" }
    }
    Assert-Equal $m.EndForce(1, 'A', 1).Node 1
    Assert-Equal $m.EndForce(1, 'B', 1).Node 2
    Assert-Equal $m.EndForce(1, 'A', 999) $null
}

Test 'a force print followed by the support reaction print and the code check reads no row from them' {
    # the second force print is followed directly by PRINT SUPPORT REACTION and CHECK CODE output with 7- and
    # 8-number rows; before the command-end markers they were read as member 2's forces
    $secs = [AnlIndexer]::IndexFile($fixture)
    $names = @($secs | ForEach-Object { $_.Name })
    foreach ($n in 'PRINT SUPPORT REACTION', 'PARAMETER', 'CODE', 'CHECK CODE', 'REPEAT LOAD', 'MEMBER TRUSS', 'END') {
        Assert-True ($names -contains $n) "no '$n' section"
    }
    $forceSecs = @($secs | Where-Object { $_.Name -eq 'PRINT MEMBER FORCES' })
    Assert-Equal $forceSecs.Count 2
    $p = New-Object AnlForceParser $fixture
    $p.UnitsReadByCaller = $true
    $p.ParseSection($forceSecs[1], (New-Object 'System.Collections.Generic.Dictionary[int,Member]'), [CoordFrame]::Local)
    Assert-Equal $p.Forces.Count 10 'rows of the second print:'
    Assert-Equal (@($p.Forces | Where-Object { $_.memID -ne 2 -or ($_.nodeID -ne 2 -and $_.nodeID -ne 3) })).Count 0
}

Test 'joint index: roles, supports, and a through member of the same section' {
    $j3 = $m.Joints[3]
    Assert-Equal (($j3.Ends | ForEach-Object { "$($_.Member)$($_.End):$($_.Role)" }) -join ' ') '2B:Beam 3A:Column 4B:Brace 5A:Beam'
    Assert-Equal "$($j3.Columns)/$($j3.Beams)/$($j3.Braces)" '1/2/1'
    Assert-Equal $m.Joints[1].Support.Type 'FIXED'
    Assert-Equal $m.Joints[2].Support $null
    $e = @($m.Joints[2].Ends | Where-Object { $_.Member -eq 2 })[0]
    Assert-Equal $e.Released 'MY MZ'
    Assert-Equal (($e.Dir | ForEach-Object { $_ }) -join ' ') '1 0 0'
    $b = @($j3.Ends | Where-Object { $_.Member -eq 2 })[0]
    Assert-Equal (($b.Dir | ForEach-Object { $_ }) -join ' ') '-1 0 0' 'direction away from the joint:'
    Assert-Equal $b.ThroughMember 0 'W24X55 into W12X26 is not a through member:'
    $v = [AnlFrameReader]::Read((New-Variant 'through' @{ '5 TABLE ST W12X26' = '5 TABLE ST W24X55' }))
    $b2 = @($v.Joints[3].Ends | Where-Object { $_.Member -eq 2 })[0]
    Assert-Equal "$($b2.ThroughMember)$($b2.ThroughEnd)" '5A'
}

Test 'a CRLF file reads the same' {
    $p = Join-Path $dir 'crlf.anl'
    [IO.File]::WriteAllText($p, $fixtureText.Replace("`r`n", "`n").Replace("`n", "`r`n"))
    $c = [AnlFrameReader]::Read($p)
    Assert-Equal "$($c.Nodes.Count)/$($c.Members.Count)/$($c.EndForces.Count)/$($c.ErrorCount)" '7/7/70/0'
    Assert-Equal $c.LoadCases[3].Title '1.2DL + 1.6LL (REPEAT)'
}

Test 'refused: SET Z UP, joints generated with REPEAT, no member force print, a group that does not exist' {
    $z = [AnlFrameReader]::Read((New-Variant 'zup' @{ '     5. INPUT WIDTH 79' = '     5. SET Z UP' }))
    Assert-True ((Get-Codes $z) -contains 'Z_UP') 'SET Z UP'
    $r = [AnlFrameReader]::Read((New-Variant 'repeat' @{ '     9. 5 28 12 0; 6 28 0 0; 7 0 12 -15' = '     9. REPEAT 1 28 0 0' }))
    Assert-True ((Get-Codes $r) -contains 'JOINTS_GENERATED') 'REPEAT'
    $n = [AnlFrameReader]::Read((New-Variant 'noforces' @{ 'PRINT MEMBER FORCES ALL' = 'PRINT MEMBER STRESSES'; 'PRINT MEMBER FORCES LIST 2' = 'PRINT MEMBER STRESSES' }))
    Assert-True ((Get-Codes $n) -contains 'NO_FORCES') 'no PRINT MEMBER FORCES'
    $g = [AnlFrameReader]::Read((New-Variant 'group' @{ '_COLS TABLE ST W14X90' = '_POSTS TABLE ST W14X90' }))
    Assert-True ((Get-Codes $g) -contains 'GROUP_UNKNOWN') 'unknown group'
    foreach ($x in $z, $r, $n, $g) { Assert-Throws { [FramePackageWriter]::Write($x, (Join-Path $dir 'refused')) } 'error' }
}

Test 'BETA ANGLE keeps the member, flags its beta as unknown and takes the axes at BETA 0' {
    $a = [AnlFrameReader]::Read((New-Variant 'angle' @{ 'BETA 90 MEMB 1' = 'BETA ANGLE MEMB 1' }))
    Assert-Equal $a.ErrorCount 0
    Assert-True ((Get-Codes $a) -contains 'BETA_UNKNOWN')
    Assert-Equal $a.Members[1].BetaKnown $false
    Assert-Equal $a.Members[1].BetaSpec 'ANGLE'
    Assert-Equal (($a.Members[1].LocalY | ForEach-Object { $_ }) -join ' ') '-1 0 0'
}

Test 'the package: every file, its header and its rows' {
    $out = Join-Path $dir 'package'
    $files = [FramePackageWriter]::Write($m, $out)
    Assert-Equal $files.Count 8
    $read = { param($name) [CalcFlow.Csv]::ReadAll((Join-Path $out $name)) }
    $ef = & $read 'endforces.csv'
    Assert-Equal ($ef[0] -join ',') 'Member,End,Node,LC,Fx (kip),Fy (kip),Fz (kip),Mx (kip-ft),My (kip-ft),Mz (kip-ft)'
    Assert-Equal $ef.Count 71
    Assert-Equal ($ef[1] -join ',') '1,A,1,1,9.5,0.85,0.05,0.02,0.3,2.9'
    $mem = [CalcFlow.Csv]::ReadTable((Join-Path $out 'members.csv'))
    Assert-Equal $mem.Count 7
    Assert-Equal "$($mem[0]['Section'])|$($mem[0]['Beta'])|$($mem[0]['yZ'])|$($mem[0]['zX'])" 'W14X90|90|1|1'
    Assert-Equal $mem[1]['ReleaseA'] 'MY MZ'
    Assert-Equal $mem[4]['OffsetA (in)'] '6 0 0'
    $lc = [CalcFlow.Csv]::ReadTable((Join-Path $out 'loadcases.csv'))
    Assert-Equal (($lc | ForEach-Object { $_['LC'] + ':' + $_['Kind'] }) -join ' ') '1:PRIMARY 2:PRIMARY 3:REPEAT 101:COMBINATION 102:COMBINATION'
    $ends = [CalcFlow.Csv]::ReadTable((Join-Path $out 'ends.csv'))
    Assert-Equal $ends.Count 14
    $j = [CalcFlow.Csv]::ReadTable((Join-Path $out 'joints.csv'))
    Assert-Equal (($j | ForEach-Object { $_['Node'] + ':' + $_['Support'] }) -join ' ') '1:FIXED 2: 3: 4:FIXED BUT 5: 6:PINNED 7:'
    $man = [IO.File]::ReadAllText((Join-Path $out 'manifest.json')) | ConvertFrom-Json
    Assert-Equal "$($man.schema)|$($man.forceUnit)|$($man.momentUnit)|$($man.counts.endForces)|$($man.problems.Count)" 'calcflow-analysis-package/1|kip|kip-ft|70|3'
    Assert-Equal $man.sourceSha256 $m.SourceSha256
}

Test 'Export-AnlFrame.ps1 writes the package, refuses a model with errors, and refuses a folder inside the repo' {
    $exe = (Get-Process -Id $PID).Path
    $runner = Join-Path $script:CalcFlow 'Export-AnlFrame.ps1'
    $out = Join-Path $dir 'runner'
    $o = & $exe -NoProfile -ExecutionPolicy Bypass -File $runner -Anl $fixture -Out $out 2>&1 | ForEach-Object { "$_" }
    Assert-Equal $LASTEXITCODE 0 ($o -join "`n")
    Assert-True (Test-Path -LiteralPath (Join-Path $out 'endforces.csv'))
    Assert-True (@($o | Where-Object { $_ -like 'model*7 joints, 7 members, 3 supports, 5 load cases, 70 member end forces*' }).Count -eq 1) ($o -join "`n")
    $bad = New-Variant 'runner-zup' @{ '     5. INPUT WIDTH 79' = '     5. SET Z UP' }
    $out2 = Join-Path $dir 'runner-zup'
    $o = & $exe -NoProfile -ExecutionPolicy Bypass -File $runner -Anl $bad -Out $out2 2>&1 | ForEach-Object { "$_" }
    Assert-Equal $LASTEXITCODE 1 ($o -join "`n")
    Assert-True (-not (Test-Path -LiteralPath $out2)) 'no package for a model with errors'
    Assert-True (@($o | Where-Object { $_ -like '*Z_UP*' }).Count -ge 1) ($o -join "`n")
    $o = & $exe -NoProfile -ExecutionPolicy Bypass -File $runner -Anl $fixture -Out (Join-Path $script:Repo 'package') 2>&1 | ForEach-Object { "$_" }
    Assert-Equal $LASTEXITCODE 2 ($o -join "`n")
}

Test 'StaadModel (the plate-model reader) now opens a frame-only file' {
    if ($PSVersionTable.PSEdition -eq 'Core') { Write-Host '        (needs the .NET Framework JSON serializer: Windows PowerShell 5.1 only; skipped)' -ForegroundColor DarkGray; return }
    $p = Join-Path $dir 'staadmodel.anl'
    Copy-Item -LiteralPath $fixture -Destination $p
    $sm = New-Object StaadModel (New-Object ModelConfig $p)
    Assert-Equal $sm.ElementList.Count 0
    Assert-Equal $sm.MemberList.Count 7
    Assert-Equal $sm.ForceList.Count 80 'both prints, as the plate reader keeps them:'
}
