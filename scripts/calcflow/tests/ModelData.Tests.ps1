# ModelData.Tests.ps1 -- CalcFlow's front door: the engineer's workbook with its Start tab and launcher
# (CalcFlow.ps1 new), and the post-process step that reads the .anl and writes the package and Model_Data.xlsx
# (CalcFlow.ps1 post-process), on the frame-demo fixture.

$dir = New-TestDir 'modeldata'
$fixture = Join-Path $script:Fixtures 'frame-demo.anl'
$proj = Join-Path $dir 'proj'
$wbPath = Join-Path $proj 'CalcFlow.xlsx'

Test 'new: the workbook (Start tab, a link cell to the launcher, the settings table) and the launcher; never overwritten' {
    $o = Invoke-CfCli new -Out $proj
    Assert-Equal $LASTEXITCODE 0 ($o -join "`n")
    Assert-True (Test-Path -LiteralPath $wbPath) 'the workbook'
    $cmd = Join-Path $proj 'Post-process ANL.cmd'
    $text = [IO.File]::ReadAllText($cmd)
    Assert-Match $text 'CalcFlow\.ps1" post-process -Workbook "%~dp0CalcFlow\.xlsx"'
    Assert-Match $text "`r`npause`r`n$"
    Assert-True ($text.IndexOf('powershell.exe" -NoProfile -ExecutionPolicy Bypass -File') -gt 0) 'runs Windows PowerShell'
    $b = [CalcFlow.TemplateBook]::Open($wbPath)
    $link = $b.Sheet('Start').Get(5, 1)
    Assert-Match $link.Formula '^HYPERLINK\("Post-process ANL\.cmd",".*Post-process \.ANL"\)$'
    Assert-Match $link.Value 'Post-process \.ANL$' 'the link shows its label before Excel calculates'
    $set = [CalcFlow.EngineerWorkbook]::Settings($wbPath)
    Assert-True ($set.ContainsKey('Anl file')) 'tblStart has the Anl file setting'
    Assert-Equal $set['Anl file'] ''
    $o = Invoke-CfCli new -Out $proj
    Assert-Equal $LASTEXITCODE 2 'a second new is refused'
    Assert-True (@($o | Where-Object { $_ -like '*never overwritten*' }).Count -eq 1) ($o -join "`n")
}

Test 'post-process: the package and Model_Data.xlsx beside the workbook; every tab; ends shown as I / J' {
    $o = Invoke-CfCli post-process -Workbook $wbPath -Anl $fixture -NoOpen
    Assert-Equal $LASTEXITCODE 0 ($o -join "`n")
    $pkgDir = Join-Path $proj 'calcflow/package/frame-demo'
    foreach ($f in [FramePackageWriter]::Files) { Assert-True (Test-Path -LiteralPath (Join-Path $pkgDir $f)) "no $f" }
    $md = [CalcFlow.Xlsx]::Read((Join-Path $proj 'Model_Data.xlsx'))
    Assert-Equal (@($md.Sheets | ForEach-Object { $_.Name }) -join '|') 'About|Members|Joints|Supports|Reactions|Reaction Extremes|Member Ends|Load Cases|All End Forces|Enveloped End Forces|End Force Extremes|Code Check'
    $f = @($md.TableRows('tblAllEndForces'))
    Assert-Equal $f.Count 70
    Assert-Equal "$($f[0]['Member']) $($f[0]['End']) $($f[0]['Node']) $($f[0]['LC']) $($f[0]['Fx (kip)']) $($f[0]['Mz (kip-ft)'])" '1 I 1 1 9.5 2.9'
    Assert-Equal (@($f | ForEach-Object { $_['End'] } | Sort-Object -Unique) -join '') 'IJ'
    Assert-Equal @($md.TableRows('tblMembers')).Count 7
    Assert-Equal @($md.TableRows('tblMemberEnds')).Count 14
    Assert-Equal @($md.TableRows('tblLoadCases')).Count 5
    Assert-Equal @($md.TableRows('tblSupports')).Count 3
    $about = $md.Sheet('About')
    $cells = @(0..40 | ForEach-Object { "$($about.Text($_, 1))=$($about.Text($_, 3))" })
    Assert-True ($cells -contains 'CHECK CODE ALL=Yes (1 rows)') 'the code check is read'
    $cc = @($md.TableRows('tblCodeCheck'))
    Assert-Equal "$($cc.Count) $($cc[0]['Member']) $($cc[0]['Command']) $($cc[0]['Status']) $($cc[0]['Ratio']) $($cc[0]['Governs']) $($cc[0]['LC'])" '1 1 CHECK CODE ALL PASS 0.215 Y 101'
    Assert-True ($cells -contains 'PRINT SUPPORT REACTION=Yes (4 rows)') 'the reaction print is read'
    $rx = @($md.TableRows('tblReactions'))
    Assert-Equal "$($rx.Count) $($rx[0]['Node']) $($rx[0]['LC']) $($rx[0]['Fy (kip)'])" '4 1 1 8.432'
    $re = @($md.TableRows('tblReactionExtremes') | Where-Object { $_['Node'] -eq '1' })[0]
    Assert-Equal "$($re['Fy max (kip)']) $($re['Fy min (kip)'])" '10.221 8.432'
    Assert-True ($cells -contains 'PRINT MEMBER FORCES ALL=Yes (70 rows)') 'the force print is read'
    Assert-True (@($o | Where-Object { $_ -like '*not read yet*' }).Count -eq 0) 'every output command of the fixture is read'
}

Test "post-process of the probe's real .anl: the code check, member tables and section displacements on their tabs" {
    $pp = Join-Path $dir 'probe'
    [void](Invoke-CfCli new -Out $pp)
    $probe = Join-Path (Join-Path (Join-Path (Join-Path $script:Repo 'scripts') 'lib') 'probes') 'anl-prints-probe.anl'
    $o = Invoke-CfCli post-process -Workbook (Join-Path $pp 'CalcFlow.xlsx') -Anl $probe -NoOpen
    Assert-Equal $LASTEXITCODE 0 ($o -join "`n")
    Assert-True (@($o | Where-Object { $_ -like '*not read yet*' }).Count -eq 0) ($o -join "`n")
    $md = [CalcFlow.Xlsx]::Read((Join-Path $pp 'Model_Data.xlsx'))
    Assert-Equal (@($md.Sheets | ForEach-Object { $_.Name } | Select-Object -Skip 11) -join '|') 'Joint Displacements|Code Check|Code Check Details|Member Information|Member Properties|Section Displ. Maxima|Section Displacements'
    $cc = @($md.TableRows('tblCodeCheck'))
    Assert-Equal $cc.Count 112
    Assert-Equal @($cc | Where-Object { $_['Governs'] -eq 'Y' }).Count 31 'one governing row per designed member'
    # member 101 is checked seven times; the largest ratio governs
    $m101 = @($cc | Where-Object { $_['Member'] -eq '101' })
    Assert-Equal (@($m101 | ForEach-Object { "$($_['Check']):$($_['Ratio'])$($_['Governs'])" }) -join ' ') '1:1.071 9:1.61 11:1.071 12:1.952Y 13:1.082 14:1.071 15:1.071'
    $r = $m101[0]
    Assert-Equal "$($r['Command'])|$($r['Load list'])|$($r['Code'])|$($r['Profile'])|$($r['Status'])|$($r['Critical'])|$($r['LC'])|$($r['Axial'])|$($r['FX'])|$($r['MZ'])|$($r['Force unit'])|$($r['Length unit'])" 'CHECK CODE ALL|ALL|AISC 360-16 LRFD (V1.2)|ST W10X49|FAIL|Eq.H1-1b|104|C|7.437|-3.504|kip|ft'
    $nd = @($cc | Where-Object { $_['Member'] -eq '306' -and $_['Check'] -eq '1' })[0]
    Assert-Equal "$($nd['Status'])|$($nd['Ratio'])|$($nd['Governs'])|$($nd['Note'])" 'NOT DESIGNED|||DESIGN OF SECTION FOR MEMBER # 306 IS NOT SUPPORTED'
    $det = @($md.TableRows('tblCodeCheckDetails'))
    Assert-Equal $det.Count 654
    $d = @($det | Where-Object { $_['Member'] -eq '101' -and $_['Check'] -eq '1' -and $_['Clause check'] -eq 'SHEAR ALONG Y' })[0]
    Assert-Equal "$($d['Group'])|$($d['Demand'])|$($d['Capacity'])|$($d['Ratio'])|$($d['Reference'])|$($d['LC'])" 'CHECKS FOR SHEAR|2.36|102|0.023|Cl.G1|102'
    $mi = @($md.TableRows('tblMemberInformation'))
    Assert-Equal "$($mi.Count) $(@($mi | Where-Object { $_['Member'] -eq '221' })[0]['Releases']) $(@($mi | Where-Object { $_['Member'] -eq '201' })[0]['Length (ft)'])" '32 000001000001 19.5'
    $mp = @($md.TableRows('tblMemberProperties') | Where-Object { $_['Member'] -eq '304' })[0]
    Assert-Equal "$($mp['Profile']) $($mp['AX (in^2)']) $($mp['IZ (in^4)']) $($mp['SY (in^3)'])" 'ST HSST6X6X0.375 7.58 39.5 13.17'
    $sm = @($md.TableRows('tblSectionDisplMaxima') | Where-Object { $_['Member'] -eq '101' })[0]
    Assert-Equal "$($sm['Max local displacement (in)']) $($sm['At (in)']) $($sm['LC']) $($sm['L/Disp'])" '0.399 72 303 360'
    $sd = @($md.TableRows('tblSectionDisplacements'))
    Assert-Equal $sd.Count (32 * 5 * 13)
    $last = @($sd | Where-Object { $_['Member'] -eq '101' -and $_['LC'] -eq '303' -and $_['Station'] -eq '12' })[0]
    Assert-Equal "$($last['X (in)']) $($last['Y (in)']) $($last['Z (in)'])" '0.0117 -0.0093 1.8329'

    # a long table is split between members
    $pkg = [CalcFlow.AnalysisPackage]::Load((Join-Path $pp 'calcflow/package/anl-prints-probe'))
    $keep = [CalcFlow.ModelData]::RowsPerSheet
    try {
        [CalcFlow.ModelData]::RowsPerSheet = 1000
        $split = Join-Path $dir 'probe-split'
        $res = [CalcFlow.ModelData]::Write($pkg, $split)
        Assert-Equal "$($res.SectionDisplacementRows) $($res.SectionSheets)" '2080 3' '65 rows a member: fifteen members a tab'
        $b = [CalcFlow.Xlsx]::Read($res.Path)
        $counts = @('tblSectionDisplacements', 'tblSectionDisplacements2', 'tblSectionDisplacements3' | ForEach-Object { @($b.TableRows($_)).Count })
        Assert-Equal ($counts -join ' ') '975 975 130'
    }
    finally { [CalcFlow.ModelData]::RowsPerSheet = $keep }
}

Test 'the envelopes: per member end and component, the case of the largest and smallest value with its concurrent forces' {
    $pkg = [CalcFlow.AnalysisPackage]::Load((Join-Path $proj 'calcflow/package/frame-demo'))
    $md = [CalcFlow.Xlsx]::Read((Join-Path $proj 'Model_Data.xlsx'))
    $env = @($md.TableRows('tblEnvelopedEndForces'))
    Assert-Equal $env.Count (14 * 12)
    # member 2 end J (B), by hand from the package
    $forces = $pkg.Forces(2, 'B')
    foreach ($ci in 0..5) {
        $name = @('Fx', 'Fy', 'Fz', 'Mx', 'My', 'Mz')[$ci]
        $best = $null; $bestLc = 0
        foreach ($lc in $forces.Keys) { if ($null -eq $best -or $forces[$lc][$ci] -gt $best) { $best = $forces[$lc][$ci]; $bestLc = $lc } }
        $row = @($env | Where-Object { $_['Member'] -eq '2' -and $_['End'] -eq 'J' -and $_['Governs'] -eq "$name max" })[0]
        Assert-Equal $row['LC'] "$bestLc" "$name max case"
        $col = @($row.Keys | Where-Object { $_ -like "$name (*" })[0]
        Assert-Near ([double]$row[$col]) $best 1e-9 "$name max"
        # the other five are that case's own values
        $other = @($row.Keys | Where-Object { $_ -like 'Fy (*' })[0]
        Assert-Near ([double]$row[$other]) $forces[$bestLc][1] 1e-9 "$name max: concurrent Fy"
    }
    $x = @($md.TableRows('tblEndForceExtremes') | Where-Object { $_['Member'] -eq '2' -and $_['End'] -eq 'J' })[0]
    $mzs = @($forces.Values | ForEach-Object { $_[5] })
    Assert-Near ([double]$x['Mz max (kip-ft)']) ($mzs | Measure-Object -Maximum).Maximum 1e-9
    Assert-Near ([double]$x['Mz min (kip-ft)']) ($mzs | Measure-Object -Minimum).Minimum 1e-9
}

Test 'a long table continues on numbered tabs, split between members; the Anl file setting names the .anl; errors write nothing' {
    $pkg = [CalcFlow.AnalysisPackage]::Load((Join-Path $proj 'calcflow/package/frame-demo'))
    $keep = [CalcFlow.ModelData]::RowsPerSheet
    try {
        [CalcFlow.ModelData]::RowsPerSheet = 25
        $res = New-Object CalcFlow.ModelDataResult
        $sheets = [CalcFlow.ModelData]::Sheets($pkg, $res)
        $p = Join-Path $dir 'split.xlsx'
        [CalcFlow.XlsxWriter]::Write($p, $sheets, $null)
    }
    finally { [CalcFlow.ModelData]::RowsPerSheet = $keep }
    Assert-Equal $res.ForceSheets 4
    $md = [CalcFlow.Xlsx]::Read($p)
    $names = @($md.Sheets | Where-Object { $_.Name -like 'All End Forces*' } | ForEach-Object { $_.Name })
    Assert-Equal ($names -join '|') 'All End Forces|All End Forces (2)|All End Forces (3)|All End Forces (4)'
    $counts = @('tblAllEndForces', 'tblAllEndForces2', 'tblAllEndForces3', 'tblAllEndForces4' | ForEach-Object { @($md.TableRows($_)).Count })
    Assert-Equal ($counts -join ',') '20,20,20,10' 'whole members on each tab'
    Assert-Equal (@($md.TableRows('tblAllEndForces2') | ForEach-Object { $_['Member'] } | Sort-Object -Unique) -join ',') '3,4'

    # the setting: the name of an .anl beside the workbook, typed into Start (written here as Excel would save it)
    $p2 = Join-Path $dir 'proj2'
    [void](Invoke-CfCli new -Out $p2)
    Copy-Item -LiteralPath $fixture -Destination (Join-Path $p2 'frame-demo.anl')
    $w = New-Object 'System.Collections.Generic.List[CalcFlow.CellWrite]'
    $start = [CalcFlow.Xlsx]::Read((Join-Path $p2 'CalcFlow.xlsx')).Sheet('Start')
    $row = @(0..40 | Where-Object { $start.Text($_, 0) -eq 'Anl file' })[0] + 1
    $w.Add([CalcFlow.CellWrite]::new('Start', "B$row", 'frame-demo.anl'))
    $saved = Join-Path $p2 'saved.xlsx'
    [CalcFlow.XlsxPatch]::Write((Join-Path $p2 'CalcFlow.xlsx'), $saved, $w, $false)
    Assert-Equal ([CalcFlow.EngineerWorkbook]::Settings($saved))['Anl file'] 'frame-demo.anl'
    $o = Invoke-CfCli post-process -Workbook $saved -NoOpen
    Assert-Equal $LASTEXITCODE 0 ($o -join "`n")
    Assert-True (Test-Path -LiteralPath (Join-Path $p2 'Model_Data.xlsx')) 'written from the setting'

    # an .anl with errors: listed, nothing written
    $bad = Join-Path $dir 'bad.anl'
    [IO.File]::WriteAllText($bad, [IO.File]::ReadAllText($fixture).Replace('     5. INPUT WIDTH 79', '     5. SET Z UP'))
    $p3 = Join-Path $dir 'proj3'
    [void](Invoke-CfCli new -Out $p3)
    $o = Invoke-CfCli post-process -Workbook (Join-Path $p3 'CalcFlow.xlsx') -Anl $bad -NoOpen
    Assert-Equal $LASTEXITCODE 1 ($o -join "`n")
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $p3 'Model_Data.xlsx'))) 'no Model_Data.xlsx'
    Assert-True (@($o | Where-Object { $_ -like '*Z_UP*' }).Count -ge 1) ($o -join "`n")
}

Test 'the writer: rows from a source must be as many as promised' {
    $s = New-Object CalcFlow.XOutSheet 'Long'
    $s.Add('h')
    $s.MoreRows = [System.Collections.Generic.List[object[]]]@(, [object[]]@(1))
    $s.MoreRowsCount = 2
    Assert-Throws { [CalcFlow.XlsxWriter]::Write((Join-Path $dir 'short.xlsx'), [CalcFlow.XOutSheet[]]@($s), $null) } 'promised 2 more rows and given 1'
}
