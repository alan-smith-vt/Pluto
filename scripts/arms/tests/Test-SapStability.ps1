# SapStability (the DEAD-case stability check of the SAP viewer file, scripts\arms\SapStability.cs) through
# SapToPluto.Export on a small hand-written model + results (.s2k text, no SAP needed). Windows PowerShell 5.1:
#   powershell -ExecutionPolicy Bypass -File scripts\arms\tests\Test-SapStability.ps1
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "..\Import-Arms.ps1")
$script:fail = 0; $script:pass = 0
function Check($name, $cond) { if ($cond) { $script:pass++ } else { $script:fail++; Write-Output "FAIL: $name" } }
$dir = Join-Path ([IO.Path]::GetTempPath()) ("pluto-stab-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
$null = New-Item -ItemType Directory $dir

# a beam 1-2-3 on the ground, a separate 2-frame piece 4-5-6 (flies off), a two-joint link 3-7 and a lone joint 7
$model = @(
    'TABLE:  "PROGRAM CONTROL"', '   ProgramName=SAP2000   Version=26.3.0   CurrUnits="Kip, in, F"', '',
    'TABLE:  "JOINT COORDINATES"',
    '   Joint=1   CoordSys=GLOBAL   CoordType=Cartesian   XorR=0   Y=0   Z=0   SpecialJt=No   GlobalX=0   GlobalY=0   GlobalZ=0',
    '   Joint=2   CoordSys=GLOBAL   CoordType=Cartesian   XorR=60   Y=0   Z=0   SpecialJt=No   GlobalX=60   GlobalY=0   GlobalZ=0',
    '   Joint=3   CoordSys=GLOBAL   CoordType=Cartesian   XorR=120   Y=0   Z=0   SpecialJt=No   GlobalX=120   GlobalY=0   GlobalZ=0',
    '   Joint=4   CoordSys=GLOBAL   CoordType=Cartesian   XorR=0   Y=100   Z=0   SpecialJt=No   GlobalX=0   GlobalY=100   GlobalZ=0',
    '   Joint=5   CoordSys=GLOBAL   CoordType=Cartesian   XorR=60   Y=100   Z=0   SpecialJt=No   GlobalX=60   GlobalY=100   GlobalZ=0',
    '   Joint=6   CoordSys=GLOBAL   CoordType=Cartesian   XorR=120   Y=100   Z=0   SpecialJt=No   GlobalX=120   GlobalY=100   GlobalZ=0',
    '   Joint=7   CoordSys=GLOBAL   CoordType=Cartesian   XorR=120   Y=0   Z=-10   SpecialJt=No   GlobalX=120   GlobalY=0   GlobalZ=-10', '',
    'TABLE:  "CONNECTIVITY - FRAME"',
    '   Frame=1   JointI=1   JointJ=2   IsCurved=No', '   Frame=2   JointI=2   JointJ=3   IsCurved=No',
    '   Frame=3   JointI=4   JointJ=5   IsCurved=No', '   Frame=4   JointI=5   JointJ=6   IsCurved=No', '',
    'TABLE:  "CONNECTIVITY - LINK"', '   Link=1   JointI=3   JointJ=7', '',
    'TABLE:  "LINK PROPERTY ASSIGNMENTS"', '   Link=1   LinkType="Two Joint"   LinkProp=SUP_GUIDE', '',
    'TABLE:  "GROUPS 2 - ASSIGNMENTS"',
    '   GroupName=SYS_SYSA   ObjectType=Frame   ObjectLabel=3', '   GroupName=SYS_SYSA   ObjectType=Frame   ObjectLabel=4',
    '   GroupName=SUPPORT_GUIDE   ObjectType=Link   ObjectLabel=1', '',
    'END TABLE DATA')
function Results($u) {   # $u: joint -> U3 under DEAD
    $l = @('TABLE:  "JOINT DISPLACEMENTS"')
    foreach ($j in 1..7) { $l += ('   Joint={0}   OutputCase=DEAD   CaseType=LinStatic   U1=0   U2=0   U3={1}   R1=0   R2=0   R3=0' -f $j, $u[$j]) }
    $l += @('', 'END TABLE DATA'); return $l
}
$m = Join-Path $dir "m.s2k"; [IO.File]::WriteAllLines($m, $model)

# 1 stable: every joint under 12 in
$r = Join-Path $dir "r1.s2k"; [IO.File]::WriteAllLines($r, (Results @{ 1 = -0.1; 2 = -0.3; 3 = -0.1; 4 = -0.2; 5 = -0.5; 6 = -0.2; 7 = 0 }))
$res = [SapToPluto]::Export($m, $r, (Join-Path $dir "a"), "t/a")
Check "1 stable: summary says so, report written" ($res.Stability -like "stability (DEAD): stable, no joint over 12 in*" -and (Test-Path (Join-Path $dir "a.stability.txt")))
$items = @((Get-Content (Join-Path $dir "a.features.json") -Raw | ConvertFrom-Json).groups.items)
Check "1 stable: no unstable groups" (@($items | Where-Object { $_.name -like "*nstable*" -or $_.name -eq "Check: UNSTABLE" }).Count -eq 0)

# 2 the 4-5-6 piece flies off (E+9), joint 3 drops 20 in on its own: two pieces, the largest first
$r = Join-Path $dir "r2.s2k"; [IO.File]::WriteAllLines($r, (Results @{ 1 = -0.1; 2 = -0.3; 3 = -20; 4 = -3e9; 5 = -3.2e9; 6 = -3e9; 7 = 0 }))
$res = [SapToPluto]::Export($m, $r, (Join-Path $dir "b"), "t/b")
Check "2 summary: 4 joints over 12 in, 2 pieces, largest 3.2e+9" ($res.Stability -like "stability (DEAD): UNSTABLE, 4 joints over 12 in in 2 pieces (largest 3.2e+9 in)*")
$items = @((Get-Content (Join-Path $dir "b.features.json") -Raw | ConvertFrom-Json).groups.items)
$u1 = $items | Where-Object { $_.name -like "Unstable 1:*" }
Check "2 piece 1 = the two SYSA frames, painted, largest movement in its name" ($null -ne $u1 -and $u1.name -eq "Unstable 1: 3.2e+9 in (2 frames)" -and $u1.hidden -eq $false -and @($u1.members[0].ids).Count -eq 2)
$g = $items | Where-Object { $_.name -eq "Check: UNSTABLE" }
Check "2 one warn glyph per piece, at the largest movement (joint 5, then joint 3)" ($g.look.glyph -eq "warn" -and @($g.anchors).Count -eq 2 -and @($g.anchors)[0].id -eq 5 -and @($g.anchors)[1].id -eq 3)
$txt = [IO.File]::ReadAllText((Join-Path $dir "b.stability.txt"))
Check "2 report: piece 1 is SYSA with no support (the hint), piece 2 has the guide" ($txt -match "Unstable 1: 3\.2e\+9 in \(2 frames\); largest at joint 5" -and $txt -match "ducts: SYSA 2; steel frames 0" -and $txt -match "no support in it" -and $txt -match "Unstable 2: 20 in \(0 frames\)[^\n]*\n[^\n]*1 guides")

Remove-Item $dir -Recurse -Force
Write-Output ("{0} passed, {1} failed" -f $script:pass, $script:fail)
exit $(if ($script:fail) { 1 } else { 0 })
