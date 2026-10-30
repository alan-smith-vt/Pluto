# Round trip test of the duct + steel SAP build (SapDucts + SapSteel), Windows PowerShell 5.1, needs SAP.
#   powershell -ExecutionPolicy Bypass -File scripts\sap\Test-SapSteel.ps1 [-Out <dir>] [-SapDir <install>]
# Writes a small synthetic set (4 duct joints, two rack frames, a wall-pinned cantilever, 2 supports, one span flag)
# in the steel-model / duct-supports formats, builds it with Run-SapDucts.ps1, exports it with
# Run-Sap.ps1 -Export -NoResults and checks the exported tables: frames, releases, restraints, stubs, groups, PLANT.
# The .bin beside the export loads in the viewer (recipe in vault/arms/sap-controller.md).
param(
    [string]$Out = (Join-Path $env:TEMP "pluto-sap-steel-test"),
    [string]$SapDir = $env:PLUTO_SAP_DIR
)
$ErrorActionPreference = "Stop"
$null = New-Item -ItemType Directory -Force $Out
$Out = (Resolve-Path $Out).Path
function W([string]$name, [string[]]$lines) { $p = Join-Path $Out $name; [IO.File]::WriteAllLines($p, $lines); $p }

# ducts along X at z 150 (24x12 box, bottom at 144): N1 - N2 - N3 - N4
$frames = W "test.frames.csv" @(
    "Frame,JointI,JointJ,XI_in,YI_in,ZI_in,XJ_in,YJ_in,ZJ_in,Shape,W_in,H_in,Dia_in,Thk_in,WX,WY,WZ,Source,Flags,RevitElementId",
    "D1,N1,N2,0,0,150,60,0,150,rect,24,12,,0.06,0,1,0,revit,,1",
    "D2,N2,N3,60,0,150,420,0,150,rect,24,12,,0.06,0,1,0,revit,,2",
    "D3,N3,N4,420,0,150,480,0,150,rect,24,12,,0.06,0,1,0,revit,,3")
# rack 1 at x 60: columns S1-S2, S3-S4, beam split at SS1 (the support joint); rack 2 at x 420: columns S5-S6,
# S10-S8, beam S6-S8 whole (the support joint SS2 is new: auto-meshed); cantilever S6-S9 pinned at the wall
$joints = W "test.steel_joints.csv" @(
    "Joint,X_in,Y_in,Z_in,Restraint,Condition,Flags",
    "S1,60,-30,0,FIXED,open-fixed,", "S2,60,-30,132,,corner,", "S3,60,30,0,FIXED,open-fixed,", "S4,60,30,132,,corner,",
    "SS1,60,0,132,,,", "S5,420,-30,0,FIXED,open-fixed,", "S6,420,-30,132,,framed,", "S8,420,30,132,,corner,",
    "S9,420,-90,132,PINNED,open-pinned,OPEN_NEAR", "S10,420,30,0,FIXED,open-fixed,")
$w12 = "W12X26,12.2,6.49,0.38,0.23"
$steel = W "test.steel_frames.csv" @(
    "Frame,JointI,JointJ,XI_in,YI_in,ZI_in,XJ_in,YJ_in,ZJ_in,Section,D_in,Bf_in,Tf_in,Tw_in,WebX,WebY,WebZ,CP,OffY_in,OffZ_in,ReleaseI,ReleaseJ,EndI,EndJ,MemberOid,Flags",
    "SF1,S1,S2,60,-30,0,60,-30,132,$w12,0,1,0,,0,0,,,open-fixed,corner,M1,",
    "SF2,S3,S4,60,30,0,60,30,132,$w12,1,0,0,,0,0,,,open-fixed,corner,M2,",
    "SF3,S2,SS1,60,-30,132,60,0,132,$w12,0,0,1,8,0,0,M2M3,,corner,interior,M3,",
    "SF4,SS1,S4,60,0,132,60,30,132,$w12,0,0,1,8,0,0,,M2M3,interior,corner,M3,",
    "SF5,S5,S6,420,-30,0,420,-30,132,$w12,1,0,0,,0,0,,,open-fixed,interior,M4,",
    "SF6,S10,S8,420,30,0,420,30,132,$w12,1,0,0,,0,0,,,open-fixed,corner,M5,",
    "SF7,S6,S8,420,-30,132,420,30,132,$w12,0,0,1,8,0,0,M2M3,M2M3,framed,corner,M6,",
    "SF8,S6,S9,420,-30,132,420,-90,132,,,,,,0,0,1,,0,0,M2M3,,framed,open-pinned,M7,")
$supports = W "test.supports.csv" @(
    "SupportId,Type,DuctJoint,DX,DY,DZ,SteelJoint,SX,SY,SZ,SteelMember,AxisX,AxisY,AxisZ,Gap_in,System,Flags",
    "1,guide,N2,60,0,150,SS1,60,0,132,SF3,1,0,0,12,SYS-A,",
    "2,anchor,N3,420,0,150,SS2,420,0,132,SF7,1,0,0,12,SYS-A,LARGE_GAP")
$flags = W "test.flags.csv" @("Frame,Flag", "D2,UNSUPPORTED_SPAN")

$sdb = Join-Path $Out "test_steel.sdb"
$ps = Join-Path $env:WINDIR "System32\WindowsPowerShell\v1.0\powershell.exe"
$extra = @(); if ($SapDir) { $extra = @("-SapDir", $SapDir) }
& $ps -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "Run-SapDucts.ps1") -Frames $frames -SavePath $sdb -SteelFrames $steel -Supports $supports -Flags $flags @extra
if ($LASTEXITCODE) { throw "Run-SapDucts failed" }
$base = Join-Path $Out "test_steel"
& $ps -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "Run-Sap.ps1") -Open $sdb -NoSaveSdb -Export $base -NoResults -NoViewerFile @extra | Select-String "^\[(export|mesh)|^RESULT" | ForEach-Object { $_.Line.Substring(0, [Math]::Min(200, $_.Line.Length)) }
if ($LASTEXITCODE) { throw "Run-Sap -Export failed" }
# .bin in a fresh process: lib\Config.ps1's Add-Type fails ("System.Console") once SAP is attached in the session
$lib = Join-Path (Split-Path $PSScriptRoot) "lib\Config.ps1"
& $ps -NoProfile -ExecutionPolicy Bypass -Command ". '$lib'; [SapToPluto]::Export('$base.model.s2k', `$null, '$base', 'sap/test_steel', `$false).Summary()"
if ($LASTEXITCODE) { throw "SapToPluto failed" }

# the exported model tables: rows per table, and a row's fields
$tables = @{}; $cur = $null
foreach ($line in [IO.File]::ReadAllLines("$base.model.s2k")) {
    if ($line -match '^TABLE:\s+"(.+)"') { $cur = $Matches[1]; $tables[$cur] = New-Object System.Collections.ArrayList; continue }
    if ($cur -and $line.Trim()) { [void]$tables[$cur].Add($line) }
}
function Rows([string]$t) { if ($tables.ContainsKey($t)) { @($tables[$t]) } else { @() } }
function InGroup([string]$g, [string]$type) { @(Rows "GROUPS 2 - ASSIGNMENTS" | Where-Object { $_ -match "GroupName=`"?$g`"?\s" -and $_ -match "ObjectType=`"?$type" }).Count }
$fail = 0
function Expect([string]$what, $got, $want) {
    $ok = "$got" -eq "$want"; if (-not $ok) { $script:fail++ }
    Write-Output ("{0} {1}: {2}{3}" -f $(if ($ok) { "ok  " } else { "FAIL" }), $what, $got, $(if ($ok) { "" } else { " (want $want)" }))
}
# 3 ducts + 8 steel objects (SF7 meshed in two at SS2) + 2 stubs
Expect "frame elements" (Rows "CONNECTIVITY - FRAME").Count 14
Expect "joints" (Rows "JOINT COORDINATES").Count 15
# SF3 I, SF4 J, SF7 both (first / last element), SF8 I
$rel = Rows "FRAME RELEASE ASSIGNMENTS 1 - GENERAL"
Expect "frames with releases" $rel.Count 5
Expect "releases are M2+M3 only" @($rel | Where-Object { $_ -match "PI=Yes|V2I=Yes|V3I=Yes|TI=Yes|PJ=Yes|V2J=Yes|V3J=Yes|TJ=Yes" }).Count 0
$res = Rows "JOINT RESTRAINT ASSIGNMENTS"
Expect "restrained joints" $res.Count 5
Expect "fixed joints" @($res | Where-Object { $_ -match "R1=Yes" -and $_ -match "R3=Yes" }).Count 4
Expect "pinned joints" @($res | Where-Object { $_ -match "U3=Yes" -and $_ -match "R1=No" }).Count 1
Expect "insertion points (CP 8 on 3 beams = 4 elements)" (Rows "FRAME INSERTION POINT ASSIGNMENTS").Count 4
Expect "SUPPORT_ALL frames" (InGroup "SUPPORT_ALL" "Frame") 2
Expect "SUPPORT_GUIDE frames" (InGroup "SUPPORT_GUIDE" "Frame") 1
Expect "STEEL_ALL frames" (InGroup "STEEL_ALL" "Frame") 9
Expect "STEEL_OPEN_FIXED joints" (InGroup "STEEL_OPEN_FIXED" "Joint") 4
Expect "FLAG_OPEN_NEAR joints" (InGroup "FLAG_OPEN_NEAR" "Joint") 1
Expect "FLAG_UNSUPPORTED_SPAN frames" (InGroup "FLAG_UNSUPPORTED_SPAN" "Frame") 1
Expect "FLAG_NO_SIZE frames" (InGroup "FLAG_NO_SIZE" "Frame") 1
Expect "PLANT coordinate system" @(Rows "COORDINATE SYSTEMS" | Where-Object { $_ -match "Name=`"?PLANT" }).Count 1
Expect "viewer file" (Test-Path "$base.bin") $true
if ($fail) { throw "$fail checks failed (tables in $base.model.s2k)" }
Write-Output "all checks passed; viewer file $base.bin"
