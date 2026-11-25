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
    Assert-Equal ((Get-Codes $m | Sort-Object) -join ',') 'CODE_CHECK_MISSING,FORCE_PRINTED_TWICE,FORCE_UNITS_CONVERTED,MEMBER_OFFSET'
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
    Assert-Equal (@($m.Prints | ForEach-Object { "$($_.Order):$($_.Command):$($_.LoadList):$($_.Read)" }) -join ' | ') '1:PRINT MEMBER FORCES ALL:ALL:True | 2:PRINT MEMBER FORCES LIST 2:ALL:True | 3:PRINT SUPPORT REACTION:ALL:True | 4:CHECK CODE ALL:ALL:True'
    $u = [AnlFrameReader]::Read((New-Variant 'unread' @{ '    61. CHECK CODE ALL' = '    61. PRINT ANALYSIS RESULTS' }))
    Assert-Match @($u.Problems | Where-Object { $_.Code -eq 'PRINT_NOT_READ' })[0].Message 'not read yet: PRINT ANALYSIS RESULTS$'
    Assert-Equal $u.Prints[3].Read $false
    # the fixture's code check prints one member of the seven it names
    Assert-Equal $m.CodeChecks.Count 1
    $c = $m.CodeChecks[0]
    Assert-Equal "$($c.Print)|$($c.Member)|$($c.Code)|$($c.Profile)|$($c.Table)|$($c.Status)|$($c.Ratio)|$($c.Critical)|$($c.LC)|$($c.Axial)|$($c.F[0])|$($c.F[5])|$($c.ForceText)-$($c.LengthText)" '4|1|AISC 360-16 (LRFD)|ST W14X90|AISC SECTIONS|PASS|0.215|AISC- H1-1B|101|C|12.34|15.67|kip-ft'
    Assert-Match @($m.Problems | Where-Object { $_.Code -eq 'CODE_CHECK_MISSING' })[0].Message 'no result for member\(s\) 2, 3, 4, 5, 6, 7$'
    $l = [AnlFrameReader]::Read((New-Variant 'loadlist' @{
        '    54. PRINT MEMBER FORCES ALL' = "    53. LOAD LIST 1 TO 3 101`n    54. PRINT MEMBER FORCES ALL"
        '    57. PRINT SUPPORT REACTION' = "    57. LOAD LIST 102`n    57. PRINT SUPPORT REACTIONS"
        '    41. LOAD 2 LOADTYPE LIVE TITLE LL' = '    41. LOAD 2 LOADTYPE Roof Live TITLE LL'
    }))
    $first = $l.Prints[0]
    Assert-Equal "$($first.Command)|$($first.LoadList)|$($first.AllCases)|$(@($first.Cases) -join ',')" 'PRINT MEMBER FORCES ALL|1 TO 3 101|False|1,2,3,101'
    $rx = @($l.Prints | Where-Object { $_.Command -eq 'PRINT SUPPORT REACTIONS' })[0]
    Assert-Equal "$($rx.LoadList)|$($rx.Read)" '102|True'
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

Test "the probe's real .anl (STAAD.Pro 2025): the model, every end force, the support reactions and the joint displacements" {
    $probe = Join-Path (Join-Path (Join-Path (Join-Path $script:Repo 'scripts') 'lib') 'probes') 'anl-prints-probe.anl'
    $r = [AnlFrameReader]::Read($probe)
    Assert-Equal $r.ErrorCount 0 (($r.ProblemsOf('Error') | ForEach-Object { $_.ToString() }) -join '; ')
    Assert-Equal "$($r.Nodes.Count) $($r.Members.Count) $($r.Supports.Count) $($r.LoadCases.Count)" '18 32 6 14'
    Assert-Equal $r.EndForces.Count (32 * 2 * 6) 'every member end in every case of the strength list'
    Assert-Equal (@($r.Members.Values | ForEach-Object { $_.Family } | Sort-Object -Unique) -join ' ') '2L C HSS L PIPE PRIS W WT'
    Assert-Equal $r.Members[654321].NodeB 123456 'six-digit numbers'
    Assert-Equal "$($r.LoadCases[3].LoadType)|$($r.LoadCases[103].FactorsText)" 'ROOF LIVE|1*1.2 + 2*1 + 4*1' 'a LOADTYPE of two words; a comment between a combination and its factors'
    # the two load lists, and what each print ran under
    $p = @{}; foreach ($x in $r.Prints) { if (-not $p.ContainsKey($x.Command)) { $p[$x.Command] = $x } }
    Assert-Equal "$($p['PRINT SUPPORT REACTIONS'].LoadList)|$($p['PRINT SUPPORT REACTIONS'].Read)|$($p['PRINT SUPPORT REACTIONS'].Rows)" '6 101 TO 104 201|True|36'
    Assert-Equal "$($p['PRINT JOINT DISPLACEMENTS'].LoadList)|$($p['PRINT JOINT DISPLACEMENTS'].Rows)" '1 2 301 TO 303|90'
    Assert-Equal @($r.Prints | Where-Object { $_.Command -like 'CHECK CODE*' }).Count 8 'the eight code checks'
    # reactions: supports x cases, in the units of the table's title, values as printed
    Assert-Equal "$($r.Reactions.Count) $($r.ReactionForceText) $($r.ReactionMomentText)" '36 kip kip-in'
    $x = @($r.Reactions | Where-Object { $_.Node -eq 1 -and $_.LC -eq 104 })[0]
    Assert-Equal ($x.V -join ' ') '-5.07 3.74 -5.67 -1333.95 0.2 -42.04'
    # displacements: joints x cases
    Assert-Equal "$($r.Displacements.Count) $($r.DisplacementLengthText)" '90 in'
    $d = @($r.Displacements | Where-Object { $_.Node -eq 123456 -and $_.LC -eq 1 })[0]
    Assert-Near $d.V[1] -0.00834 1e-12
    Assert-Near $d.V[5] 0.00055 1e-12
    Assert-True ((Get-Codes $r) -notcontains 'FORCE_ROW_UNREAD') 'no force row was lost'
    # a reaction row with touching numbers is refused
    $text = [IO.File]::ReadAllText($probe, [Text.Encoding]::GetEncoding(28591))
    $bad = Join-Path $dir 'probe-touching.anl'
    $row = '         104     -5.07      3.74     -5.67  -1333.95      0.20    -42.04'
    Assert-True ($text.IndexOf($row) -gt 0) 'the row is in the probe'
    [IO.File]::WriteAllText($bad, $text.Replace($row, '         104     -5.07      3.74-123456.67  -1333.95      0.20    -42.04'), [Text.Encoding]::GetEncoding(28591))
    Assert-True ((Get-Codes ([AnlFrameReader]::Read($bad))) -contains 'REACTION_ROW_UNREAD') 'touching numbers in the reaction table'
}

$probePath = Join-Path (Join-Path (Join-Path (Join-Path $script:Repo 'scripts') 'lib') 'probes') 'anl-prints-probe.anl'
$probe = [AnlFrameReader]::Read($probePath)
$probeText = [IO.File]::ReadAllText($probePath, [Text.Encoding]::GetEncoding(28591))
function New-ProbeVariant([string]$Name, [string]$Find, [string]$Put) {
    Assert-True ($probeText.IndexOf($Find) -gt 0) "the probe has: $Find"
    $p = Join-Path $dir "probe-$Name.anl"
    [IO.File]::WriteAllText($p, $probeText.Replace($Find, $Put), [Text.Encoding]::GetEncoding(28591))
    return [AnlFrameReader]::Read($p)
}

Test "the probe's real .anl: every output command is read, and nothing but the member offset is noted" {
    Assert-Equal ((Get-Codes $probe) -join ',') 'MEMBER_OFFSET'
    Assert-Equal @($probe.Prints | Where-Object { -not $_.Read }).Count 0 'no print is left unread'
    Assert-Equal (@($probe.Prints | ForEach-Object { $_.Rows }) -join ' ') '32 18 32 32 36 384 90 160 12 8 12 12 12 12 12'
}

Test "the probe's real .anl with a member list on PRINT MEMBER FORCES: the command's echoed line is not a force row" {
    # STAAD echoes the input line after its line number, so a list puts 4 to 10 numbers on it, like a row of the table
    $lists = [ordered]@{
        'three'     = 'LIST 101 102 103'
        'six'       = 'LIST 101 102 103 104 105 106'
        'seven'     = 'LIST 101 102 103 104 105 106 111'
        'eight'     = 'LIST 101 102 103 104 105 106 111 112'
        'ranges'    = 'LIST 101 TO 106 111 TO 115 201 TO 204'
        'continued' = "LIST 101 TO 106 111 TO 115 -`r`n   147. 201 202 203 204 211 212"
    }
    foreach ($k in $lists.Keys) {
        $v = New-ProbeVariant "forces-list-$k" '146. PRINT MEMBER FORCES' ('146. PRINT MEMBER FORCES ' + $lists[$k])
        Assert-Equal ((Get-Codes $v) -join ',') 'MEMBER_OFFSET' $k
        Assert-Equal $v.EndForces.Count 384 $k
    }
}

Test 'a row of numbers before the first member row of a force table is reported with its text, not given to member 0' {
    $v = New-ProbeVariant 'forces-stray' '146. PRINT MEMBER FORCES' "146. PRINT MEMBER FORCES`r`n        7     1.00     2.00     3.00     4.00     5.00     6.00"
    Assert-True ((Get-Codes $v) -notcontains 'FORCE_MEMBER_UNKNOWN') 'no member 0'
    $e = @($v.ProblemsOf('Error') | Where-Object { $_.Code -eq 'FORCE_ROW_UNREAD' })
    Assert-Equal $e.Count 1
    Assert-Match $e[0].Message '^1 line\(s\) of PRINT MEMBER FORCES hold numbers but are not a force row .*"7     1\.00     2\.00'
    Assert-Equal $v.EndForces.Count 384 'the table itself is read'
}

Test "the probe's code checks: eight CHECK CODE commands in three layouts, one row per member each" {
    Assert-Equal $probe.CodeChecks.Count 112
    $by = @($probe.CodeChecks | Group-Object Print | ForEach-Object { "$($_.Name)=$($_.Count):$($_.Group[0].Code)" })
    Assert-Equal ($by -join ' | ') '1=32:AISC 360-16 LRFD (V1.2) | 9=12:AISC 360-16 ASD (V1.2) | 10=8:AISC 360-16 ASD (V1.2) | 11=12:(AISC-360-10-LRFD) v1.4a | 12=12:(AISC 9TH EDITION) v1.0 | 13=12:(LRFD 3RD EDITION) v1.0 | 14=12:(AISC-360-05-LRFD) v3.3a | 15=12:AISC 360-22 LRFD (V1.0)'
    $row = { param($print, $member) @($probe.CodeChecks | Where-Object { $_.Print -eq $print -and $_.Member -eq $member })[0] }
    $show = { param($c) "$($c.Profile)|$($c.Status)|$($c.Ratio)|$($c.Critical)|$($c.LC)|$($c.Location)|$($c.Axial)|$(@(0..5 | ForEach-Object { if ($c.Has[$_]) { $c.F[$_] } else { '-' } }) -join ' ')|$($c.ForceText)-$($c.LengthText)|$($c.Starred)" }
    # the box of AISC 360-16: all six forces, in the units of the check
    Assert-Equal (& $show (& $row 1 101)) 'ST W10X49|FAIL|1.071|Eq.H1-1b|104|0|C|7.437 -1.324 -5.667 0.01668 111.2 -3.504|kip-ft|True'
    Assert-Equal (& $show (& $row 1 103)) 'ST W10X49|PASS|0.126|Eq.H1-1b|102|12|C|53.35 5.611E-05 -0.6551 -0 -7.861 -0.0006734|kip-ft|False'
    Assert-Equal (& $show (& $row 1 654321)).Split('|')[1] 'PASS' 'a six-digit member'
    Assert-Equal (& $show (& $row 15 301)) 'LD L40404|FAIL|1.115|Slenderness (C)|6|0|C|2.307 0 0 0 0 0|kip-in|True'
    # the table of AISC 360-10 and 360-05: FX, MY and MZ
    Assert-Equal (& $show (& $row 11 101)) 'ST W10X49|FAIL|1.071|Eq. H1-1b|104|0|C|7.44 - - - 1333.95 -42.04|kip-in|True'
    Assert-Equal (& $show (& $row 14 103)) 'ST W10X49|PASS|0.126|Eq. H1-1b|102|144|C|53.35 - - - -94.33 -0.01|kip-in|False'
    # the older codes: a drawing at TRACK 2 (its summary in kip-feet), the table at TRACK 0 and 1 (kip-inch)
    Assert-Equal (& $show (& $row 12 101)) 'ST W10X49|FAIL|1.952|AISC- H1-3|104|0|C|7.44 - - - 111.16 -3.5|kip-ft|False'
    Assert-Equal (& $show (& $row 12 103)) 'ST W10X49|PASS|0.283|AISC- H1-1|102|144|C|53.35 - - - -94.33 -0.01|kip-in|False'
    Assert-Equal (& $show (& $row 13 101)) 'ST W10X49|FAIL|1.082|LRFD-H1-1B-C|104|0|C|7.44 - - - 111.16 -3.5|kip-ft|False' 'the NUL STAAD writes into this profile line is read as a space'
    Assert-Equal (& $show (& $row 13 202)).Split('|')[3] 'DEFLECTION'
    # a member STAAD does not design, in its three wordings
    Assert-Equal (@(1, 11, 12 | ForEach-Object { $c = & $row $_ 306; "$($c.Status): $($c.Note)" }) -join ' | ') 'NOT DESIGNED: DESIGN OF SECTION FOR MEMBER # 306 IS NOT SUPPORTED | NOT DESIGNED: DESIGN OF SECTION FOR MEMBER # 306 IS NOT SUPPORTED; MEMBER DESIGN FAILED FOR MEMBER 306 | NOT DESIGNED: THIS VERSION DOES NOT DESIGN PRISMATIC SECTIONS (MEMBER 306)'
    Assert-Equal @($probe.CodeChecks | Where-Object { $_.Status -notin 'PASS', 'FAIL', 'NOT DESIGNED' }).Count 0
    # the deflection check ran under the service list
    Assert-Equal "$($probe.Prints[9].Command)|$($probe.Prints[9].LoadList)" 'CHECK CODE MEMB 201 TO 204 211 TO 213 20001|1 2 301 TO 303'
}

Test "the probe's code checks at TRACK 1 and 2: the checks behind a result, with demand, capacity, ratio, clause and case" {
    Assert-Equal (@($probe.CodeCheckDetails | Group-Object Print | ForEach-Object { "$($_.Name)=$($_.Count)" }) -join ' ') '1=122 9=122 10=33 11=126 14=129 15=122'
    $of = { param($print, $member) @($probe.CodeCheckDetails | Where-Object { $_.Print -eq $print -and $_.Member -eq $member } | ForEach-Object { "$($_.Check)=$($_.Ratio)" }) -join '; ' }
    # TRACK 2 of the box layout: a block per check
    Assert-Equal (& $of 1 101) 'TENSILE YIELDING=0.01; TENSILE RUPTURE=0.009; FLEXURAL BUCKLING X=0.089; FLEXURAL BUCKLING Y=0.1; FLEXURAL-TORSIONAL-BUCKLING=0.094; SHEAR ALONG X=0.019; SHEAR ALONG Y=0.023; FLEXURAL YIELDING (X)=0.093; FLEXURAL YIELDING (Y)=1.048; LAT TOR BUCK ABOUT X=0.097; COMBINED FORCES CLAUSE H1=1.071'
    # TRACK 1: a table per group
    Assert-Equal (& $of 1 102) 'TEN. YLD.=0; TEN. RUPT.=0; FLEX. BUCK. X=0.173; FLEX. BUCK. Y=0.195; FLEX. TOR. BUCK=0.184; SHEAR X=0.031; SHEAR Y=0.004; FLEX. YLD. X=0.019; FLEX. YLD. Y=1.667; L-T-B ABT X=0.02; CLAUSE H1=1.699'
    $d = @($probe.CodeCheckDetails | Where-Object { $_.Print -eq 1 -and $_.Member -eq 101 -and $_.Check -eq 'FLEXURAL YIELDING (Y)' })[0]
    Assert-Equal "$($d.Group)|$($d.Demand)|$($d.Capacity)|$($d.Reference)|$($d.LC)|$($d.Location)" 'CHECKS FOR BENDING|-111.2|106.1|Cl.F6.1|5|0'
    # AISC 360-10: a name over two lines, a clause of two words
    $d = @($probe.CodeCheckDetails | Where-Object { $_.Print -eq 11 -and $_.Member -eq 101 -and $_.Check -eq 'Flexural Tor Buck' })[0]
    Assert-Equal "$($d.Group)|$($d.Demand)|$($d.Capacity)|$($d.Ratio)|$($d.Reference)|$($d.LC)" 'CHECK FOR AXIAL COMPRESSION|51.2|545|0.094|Eq. E4-1|102'
    # a member's governing ratio is the largest of its checks
    $worst = (@($probe.CodeCheckDetails | Where-Object { $_.Print -eq 1 -and $_.Member -eq 102 } | ForEach-Object { $_.Ratio }) | Measure-Object -Maximum).Maximum
    Assert-Near $worst @($probe.CodeChecks | Where-Object { $_.Print -eq 1 -and $_.Member -eq 102 })[0].Ratio 1e-12
}

Test "the probe's member information and properties, as STAAD printed them" {
    Assert-Equal "$($probe.MemberInfo.Count) $($probe.MemberInfoLengthText)" '32 ft'
    $i = { param($id) $x = @($probe.MemberInfo | Where-Object { $_.Member -eq $id })[0]; "$($x.StartJoint)-$($x.EndJoint)|$($x.Length)|$($x.HasBeta):$($x.Beta)|$($x.Releases)" }
    Assert-Equal (& $i 103) '3-13|12|True:90|'
    Assert-Equal (& $i 201) '11-12|19.5|True:0|' 'the length has the offset taken off (joint to joint is 20 ft)'
    Assert-Equal (& $i 221) '11-14|15|True:0|000001000001'
    Assert-Equal (& $i 301) '1-12|23.324|False:0|TRUSS'
    Assert-Equal (& $i 654321) '16-123456|12|True:0|'
    Assert-Equal "$($probe.MemberProperties.Count) $($probe.MemberPropertyLengthText)" '32 in'
    $p = { param($id) $x = @($probe.MemberProperties | Where-Object { $_.Member -eq $id })[0]; "$($x.Profile)|$($x.V -join ' ')" }
    Assert-Equal (& $p 101) 'ST W10X49|14.4 272 93.4 1.39 3.4 7.47 54.4 18.68'
    Assert-Equal (& $p 223) 'ST C10X30|8.81 103 3.93 1.22 6.73 1.76 20.6 1.65' 'the last member before a page break'
    Assert-Equal (& $p 304) 'ST HSST6X6X0.375|7.58 39.5 39.5 64.6 4.19 4.19 13.17 13.17' 'a long profile name pushes the numbers to the next line'
    Assert-Equal (& $p 306) 'PRISMATIC|36 108 108 182.52 30.6 30.6 36 36'
    # what STAAD printed is checked against what the echo was read as
    $j = New-ProbeVariant 'joints' '    221       11      14          15.000     0.00    000001000001' '    221       11      15          15.000     0.00    000001000001'
    Assert-Match @($j.ProblemsOf('Error') | Where-Object { $_.Code -eq 'MEMBER_INFO_MISMATCH' })[0].Message 'members 221$'
    $r = New-ProbeVariant 'release' '    221       11      14          15.000     0.00    000001000001' '    221       11      14          15.000     0.00    000011000001'
    Assert-Match @($r.Problems | Where-Object { $_.Code -eq 'MEMBER_INFO_RELEASE' })[0].Message 'members 221$'
    $c = New-ProbeVariant 'coord' '     12           20.000         12.000          0.000' '     12           20.000         12.500          0.000'
    Assert-Match @($c.ProblemsOf('Error') | Where-Object { $_.Code -eq 'JOINT_PRINT_MISMATCH' })[0].Message 'joints 12$'
}

Test "the probe's section displacements: X Y Z at 13 stations per member and case, and STAAD's largest local displacement" {
    Assert-Equal "$($probe.SectionDisplacements.Count) $($probe.SectionStations) $($probe.SectionDisplacementLengthText)" '160 13 in' '32 members x 5 cases of the service list'
    $d = @($probe.SectionDisplacements | Where-Object { $_.Member -eq 101 -and $_.LC -eq 303 })[0]
    Assert-Equal $d.V.Length 39
    Assert-Equal "$($d.V[3]) $($d.V[4]) $($d.V[5]) | $($d.V[36]) $($d.V[37]) $($d.V[38])" '-0.0011 -0.0008 0.016 | 0.0117 -0.0093 1.8329'
    Assert-Equal @($probe.SectionDisplacements | Where-Object { $_.V.Length -ne 39 }).Count 0
    Assert-Equal $probe.SectionMaxima.Count 32
    $x = @($probe.SectionMaxima | Where-Object { $_.Member -eq 101 })[0]
    Assert-Equal "$($x.Disp) $($x.At) $($x.LC) $($x.LOverDisp)" '0.399 72 303 360'
    # numbers that run together are refused, not shifted
    $t = New-ProbeVariant 'sect' '                    -0.0031   -0.0015    0.0624   -0.0057   -0.0023    0.1373' '                    -0.0031   -0.0015    0.0624-123456.0057   -0.0023    0.1373'
    Assert-True ((Get-Codes $t) -contains 'SECTION_DISPLACEMENT_UNREAD') 'touching numbers in a section displacement block'
    Assert-Equal $t.ErrorCount 1
}

Test "a code check result in a layout that is not known is refused, not passed over" {
    $u = New-ProbeVariant 'status' '|  Status:        FAIL       Ratio:         1.071       Loadcase:      104    |' '|  Result:        FAIL       Ratio:         1.071       Loadcase:      104    |'
    $e = @($u.ProblemsOf('Error') | Where-Object { $_.Code -eq 'CODE_CHECK_UNREAD' })
    Assert-True ($e.Count -ge 1) ((Get-Codes $u) -join ',')
    Assert-Match $e[0].Message 'members 101$'
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
    Assert-Equal $files.Count 17
    Assert-Equal (@($files | ForEach-Object { [IO.Path]::GetFileName($_) } | Sort-Object) -join ' ') (@([FramePackageWriter]::Files | Sort-Object) -join ' ')
    $rc = [CalcFlow.Csv]::ReadAll((Join-Path $out 'reactions.csv'))
    Assert-Equal ($rc[0] -join ',') 'Node,LC,Fx (kip),Fy (kip),Fz (kip),Mx (kip-in),My (kip-in),Mz (kip-in)'
    Assert-Equal ($rc[1] -join ',') '1,1,0.512,8.432,0,0,0,2.151'
    Assert-Equal $rc.Count 5
    $pr = [CalcFlow.Csv]::ReadTable((Join-Path $out 'prints.csv'))
    Assert-Equal (($pr | ForEach-Object { "$($_['Command'])=$($_['Read'])/$($_['Rows'])" }) -join '; ') 'PRINT MEMBER FORCES ALL=Y/70; PRINT MEMBER FORCES LIST 2=Y/10; PRINT SUPPORT REACTION=Y/4; CHECK CODE ALL=Y/1'
    $read = { param($name) [CalcFlow.Csv]::ReadAll((Join-Path $out $name)) }
    $cc = & $read 'codechecks.csv'
    Assert-Equal ($cc[0] -join ',') 'Print,Member,Code,Profile,Table,Status,Ratio,Critical,LC,Location,Axial,FX,FY,FZ,MX,MY,MZ,Force unit,Length unit,Starred,Note'
    Assert-Equal ($cc[1] -join ',') '4,1,AISC 360-16 (LRFD),ST W14X90,AISC SECTIONS,PASS,0.215,AISC- H1-1B,101,0,C,12.34,,,,0,15.67,kip,ft,,'
    Assert-Equal ([CalcFlow.Csv]::ReadAll((Join-Path $out 'sectiondisplacements.csv'))).Count 1 'a table the file does not print is a header alone'
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
