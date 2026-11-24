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
    Assert-Equal ((Get-Codes $m | Sort-Object) -join ',') 'FORCE_PRINTED_TWICE,FORCE_UNITS_CONVERTED,MEMBER_OFFSET,PRINT_NOT_READ'
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

Test "sections: STAAD 2025's database form, double angles, a quoted database name; comment lines in the echo are skipped" {
    $d = [AnlFrameReader]::Read((New-Variant 'dbform' @{
        '    19. 2 TABLE ST W24X55' = "    19. 2 TABLE 'W' ST 'W24X55'"
        '    21. 5 TABLE ST W12X26' = '    21. 5 TABLE LD L40404 SP 0.375'
        '    17. MEMBER PROPERTY AMERICAN' = "    17. MEMBER PROPERTY 'US (AISC 2023).db3'"
    }))
    Assert-Equal $d.ErrorCount 0 (($d.ProblemsOf('Error') | ForEach-Object { $_.ToString() }) -join '; ')
    Assert-Equal "$($d.Members[2].Section)|$($d.Members[2].Family)" 'W24X55|W'
    Assert-Equal "$($d.Members[5].Section)|$($d.Members[5].Family)" 'L40404|2L'
    Assert-Equal $d.PropertyCountry "'US (AISC 2023).db3'"
    # a comment inside the joints, the properties, and between a combination and its factors
    $lines = $fixtureText -split "`n"
    $out = New-Object System.Collections.Generic.List[string]
    foreach ($l in $lines) {
        $out.Add($l)
        if ($l -match '^\s+\d+\. (JOINT COORDINATES|MEMBER PROPERTY AMERICAN|LOAD COMB 101)') { $out.Add('   900. * a comment line; with a semicolon and 1 2 3') }
    }
    $p = Join-Path $dir 'comments.anl'
    [IO.File]::WriteAllText($p, ($out -join "`n"))
    $c = [AnlFrameReader]::Read($p)
    Assert-Equal "$($c.Nodes.Count) $($c.Members.Count) $($c.ErrorCount)" '7 7 0'
    Assert-Equal $c.LoadCases[101].FactorsText $m.LoadCases[101].FactorsText 'the combination keeps its factors'
    Assert-Equal @($c.Members.Values | Where-Object { $_.Section -eq '' }).Count 0
}

Test 'LOAD LIST: every output command is recorded with the list it ran under; what is not read is named; LOADTYPE keeps its words' {
    Assert-Equal (@($m.Prints | ForEach-Object { "$($_.Order):$($_.Command):$($_.LoadList):$($_.Read)" }) -join ' | ') '1:PRINT MEMBER FORCES ALL:ALL:True | 2:PRINT MEMBER FORCES LIST 2:ALL:True | 3:PRINT SUPPORT REACTION:ALL:False | 4:CHECK CODE ALL:ALL:False'
    Assert-Match @($m.Problems | Where-Object { $_.Code -eq 'PRINT_NOT_READ' })[0].Message 'PRINT SUPPORT REACTION; CHECK CODE ALL'
    $l = [AnlFrameReader]::Read((New-Variant 'loadlist' @{
        '    54. PRINT MEMBER FORCES ALL' = "    53. LOAD LIST 1 TO 3 101`n    54. PRINT MEMBER FORCES ALL"
        '    57. PRINT SUPPORT REACTION' = "    57. LOAD LIST 102`n    57. PRINT SUPPORT REACTIONS"
        '    41. LOAD 2 LOADTYPE LIVE TITLE LL' = '    41. LOAD 2 LOADTYPE Roof Live TITLE LL'
    }))
    $first = $l.Prints[0]
    Assert-Equal "$($first.Command)|$($first.LoadList)|$($first.AllCases)|$(@($first.Cases) -join ',')" 'PRINT MEMBER FORCES ALL|1 TO 3 101|False|1,2,3,101'
    $rx = @($l.Prints | Where-Object { $_.Command -eq 'PRINT SUPPORT REACTIONS' })[0]
    Assert-Equal "$($rx.LoadList)|$($rx.Read)" '102|False'
    Assert-Equal $l.LoadCases[2].LoadType 'Roof Live'
    # the fixture prints all five cases, so a list of four is not short of any
    Assert-True ((Get-Codes $l) -notcontains 'FORCE_CASE_MISSING') 'no case missing'
    $short = [AnlFrameReader]::Read((New-Variant 'listshort' @{ '    56. PRINT MEMBER FORCES LIST 2' = "    55. LOAD LIST 1 999`n    56. PRINT MEMBER FORCES LIST 2" }))
    Assert-True ((Get-Codes $short) -contains 'LOAD_LIST_CASE') 'a listed case with no LOAD command'
}

Test 'no force row is lost silently: a line with touching numbers refuses the package; a GLOBAL print is refused' {
    $row = @($fixtureText -split "`n" | Where-Object { $_ -match '^\s+3\s+1\s+3\s' })[0]
    Assert-True ($row.Length -gt 0) 'the fixture has member 3, case 1, joint 3 on one row'
    $touch = ('      3    1     3' + ('1234567.89' * 2) + '   0.00   0.00   0.00   0.00')
    $t = [AnlFrameReader]::Read((New-Variant 'touching' @{ $row = $touch }))
    $e = @($t.ProblemsOf('Error') | Where-Object { $_.Code -eq 'FORCE_ROW_UNREAD' })
    Assert-Equal $e.Count 1 'the line is an error'
    Assert-Match $e[0].Message '1 line\(s\) of PRINT MEMBER FORCES ALL .*1234567\.891234567\.89'
    Assert-Throws { [FramePackageWriter]::Write($t, (Join-Path $dir 'refused2')) } 'error'
    # a member whose end B rows are gone (the reader drops nothing itself: the lines are removed here)
    $lines = New-Object System.Collections.Generic.List[string]
    $inFirst = $false; $skipNext = $false
    foreach ($l in ($fixtureText -split "`n")) {
        if ($l -match 'PRINT MEMBER FORCES ALL') { $inFirst = $true }
        if ($l -match 'PRINT MEMBER FORCES LIST 2') { $inFirst = $false }
        if ($inFirst -and $skipNext -and $l -match '^\s+\d+\s+-?\d') { $skipNext = $false; continue }
        if ($inFirst -and $l -match '^\s+6\s+1\s+\d+\s') { $skipNext = $true }
        $lines.Add($l)
    }
    $p = Join-Path $dir 'oneend.anl'
    [IO.File]::WriteAllText($p, ($lines -join "`n"))
    $o = [AnlFrameReader]::Read($p)
    $me = @($o.ProblemsOf('Error') | Where-Object { $_.Code -eq 'FORCE_END_MISSING' })
    Assert-Equal $me.Count 1 (($o.Problems | ForEach-Object { $_.Code }) -join ',')
    Assert-Match $me[0].Message ': 6$'
    $g = [AnlFrameReader]::Read((New-Variant 'global' @{ '    54. PRINT MEMBER FORCES ALL' = '    54. PRINT MEMBER FORCES GLOBAL ALL' }))
    Assert-True ((Get-Codes $g) -contains 'FORCE_GLOBAL') 'GLOBAL'
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
    Assert-Equal $files.Count 9
    $pr = [CalcFlow.Csv]::ReadTable((Join-Path $out 'prints.csv'))
    Assert-Equal (($pr | ForEach-Object { "$($_['Command'])=$($_['Read'])/$($_['Rows'])" }) -join '; ') 'PRINT MEMBER FORCES ALL=Y/70; PRINT MEMBER FORCES LIST 2=Y/10; PRINT SUPPORT REACTION=N/0; CHECK CODE ALL=N/0'
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
    Assert-Equal "$($man.schema)|$($man.forceUnit)|$($man.momentUnit)|$($man.counts.endForces)|$($man.problems.Count)" 'calcflow-analysis-package/1|kip|kip-ft|70|4'
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
