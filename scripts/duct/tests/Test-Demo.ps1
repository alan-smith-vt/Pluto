# End-to-end demo acceptance check: runs scripts\duct\Run-Demo.ps1 (generate -> Export-DuctsViewer -Revit -> Run-SapDucts)
# and checks that every stage's outputs exist and agree with each other: data, Revit fit, clean model, cuts / drops,
# steel, supports, spans, viewer files, SAP model. Invariants, not counts (the counts move with every feature; exact
# outputs are Test-DuctFixture.ps1's job). Windows PowerShell 5.1.
#   powershell -ExecutionPolicy Bypass -File scripts\duct\tests\Test-Demo.ps1 [-Root <dir>] [-NoSap] [-NoRun]
# -NoRun checks an earlier run's <Root> as it is (its Run-SapDucts log is <Root>\run-demo.log). Exit 1 on any FAIL.
param([string]$Root = (Join-Path $env:TEMP "hvac-demo-check"), [switch]$NoSap, [switch]$NoRun)
$ErrorActionPreference = "Stop"
$repo = Split-Path (Split-Path (Split-Path $PSScriptRoot))
$Root = [IO.Path]::GetFullPath($Root)
$runId = "20261030-120000"
$log = Join-Path $Root "run-demo.log"
$inv = [Globalization.CultureInfo]::InvariantCulture
$script:fails = 0; $script:n = 0
function Check([string]$stage, [string]$what, [bool]$ok, [string]$detail = "") {
    $script:n++
    if (-not $ok) { $script:fails++ }
    Write-Output ("{0}  {1,-9} {2}{3}" -f $(if ($ok) { "PASS" } else { "FAIL" }), $stage, $what, $(if ($detail) { "  ($detail)" } else { "" }))
}
function Num([string]$s) { $v = 0.0; if ([double]::TryParse($s, [Globalization.NumberStyles]::Float, $inv, [ref]$v)) { $v } else { [double]::NaN } }
function Rows([string]$p) { if (Test-Path $p) { @(Import-Csv $p) } else { @() } }

if (-not $NoRun) {
    $null = New-Item -ItemType Directory -Force $Root
    $a = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Join-Path $repo "scripts\duct\Run-Demo.ps1"), "-Root", $Root)
    if ($NoSap) { $a += "-NoSap" }
    & (Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe") @a *>&1 | Out-File $log -Encoding utf8
    Check "run" "Run-Demo.ps1 exit 0" ($LASTEXITCODE -eq 0) "log $log"
}
$logText = $(if (Test-Path $log) { Get-Content $log -Raw } else { "" })

# ---- data
$d = Join-Path $Root "ducts\$runId"; $r = Join-Path $Root "revit\$runId"
foreach ($f in "$d\ducts.csv", "$d\ducts_tri.bin", "$d\cl_nodes.csv", "$d\cl_segments.csv", "$r\elements.csv", "$r\connectors.csv",
    "$Root\steel\steel.csv", "$Root\cuts\cuts.csv", "$Root\cuts\drop.txt", "$Root\duct-config.psd1", "$Root\README.md") {
    Check "data" ("exists, not empty: " + $f.Substring($Root.Length + 1)) ((Test-Path $f) -and (Get-Item $f).Length -gt 0)
}
$nav = Rows "$d\ducts.csv"; $els = Rows "$r\elements.csv"; $steelIn = Rows "$Root\steel\steel.csv"
$cfg = Import-PowerShellDataFile "$Root\duct-config.psd1"
Check "data" "config DuctSteel names the demo steel" ($cfg.DuctSteel -eq "$Root\steel\steel.csv") $cfg.DuctSteel

# ---- overlay (Revit fit onto Navisworks): the generator's known transform is a 30 deg turn about Z
$o = Join-Path $Root "out"
$match = $(if (Test-Path "$o\revit.match.txt") { Get-Content "$o\revit.match.txt" -Raw } else { "" })
$m = [regex]::Match($match, 'NEW fit ([0-9.]+) ft median \(\d+ pairs[^,]*, rot ([-0-9.]+) deg')
Check "overlay" "fit line in match.txt" $m.Success
if ($m.Success) {
    Check "overlay" "fit median <= 0.05 ft" ((Num $m.Groups[1].Value) -le 0.05) $m.Groups[1].Value
    Check "overlay" "rotation within 0.05 deg of the generator's 30" ([Math]::Abs((Num $m.Groups[2].Value) - 30) -le 0.05) $m.Groups[2].Value
}
$m = [regex]::Match($match, 'NEW room: Navisworks (\d+) rows[^,]*, Revit (\d+) el')
Check "overlay" "every Navisworks row and Revit element in scope" ($m.Success -and [int]$m.Groups[1].Value -eq $nav.Count -and [int]$m.Groups[2].Value -eq $els.Count) $m.Value
# side files go under the demo's WorkDir, never the production C:\Temp\hvac the add-ins read
$m = [regex]::Match($match, 'room boxes: \d+ written to (.+?) \(Pluto Box\) and (.+?) \(Revit Pluto Mesh\)')
Check "overlay" "room boxes written under the demo root" ($m.Success -and $m.Groups[1].Value.StartsWith($Root) -and $m.Groups[2].Value.StartsWith($Root) -and
    (Test-Path $m.Groups[1].Value) -and (Test-Path $m.Groups[2].Value)) $m.Value

# ---- clean model (frames.csv)
$fr = Rows "$o\revit.frames.csv"
Check "clean" "frames.csv has frames" ($fr.Count -gt 0) "$($fr.Count)"
$duct = @($fr | Where-Object { $_.Shape -ne "rigid" -and $_.Source -ne "navisworks-only" })
$noSize = @($duct | Where-Object { -not (($_.W_in -and $_.H_in) -or $_.Dia_in) })
Check "clean" "every Revit duct frame has a size" ($noSize.Count -eq 0) (($noSize | ForEach-Object { $_.Frame }) -join " ")
$noW = @($duct | Where-Object { $_.Shape -eq "rect" -and -not $_.WX })
Check "clean" "every rect frame has a width direction" ($noW.Count -eq 0) (($noW | ForEach-Object { $_.Frame }) -join " ")
$noT = @($duct | Where-Object { -not ((Num $_.Thk_in) -gt 0) })
Check "clean" "every duct frame has a wall thickness" ($noT.Count -eq 0) (($noT | ForEach-Object { $_.Frame }) -join " ")
foreach ($s in @($cfg.DuctSystems | ForEach-Object { ($_ -split '=')[0] })) {
    Check "clean" "primary system $s has main frames" (@($fr | Where-Object { $_.System -eq $s -and $_.SystemRole -eq "main" }).Count -gt 0)
}
Check "clean" "the cut point is placed" ($match -match 'NEW cuts: (\d+) of \1 placed')
Check "clean" "the drop list drops a member" ($match -match 'NEW manual drop \(DuctDrop\): \d+ listed, [1-9]\d* members')

# ---- steel
$sf = Rows "$o\revit.steel_frames.csv"; $sj = Rows "$o\revit.steel_joints.csv"
Check "steel" "steel frames >= input members" ($sf.Count -ge $steelIn.Count -and $steelIn.Count -gt 0) "$($sf.Count) frames from $($steelIn.Count) members"
$oids = @{}; foreach ($x in $steelIn) { $oids[$x.MemberOid] = $x.SectionName }
$lost = @($steelIn | Where-Object { $o2 = $_.MemberOid; -not ($sf | Where-Object { $_.MemberOid -eq $o2 }) })
Check "steel" "every input member is in the model" ($lost.Count -eq 0) (($lost | ForEach-Object { $_.MemberOid }) -join " ")
$badSec = @($sf | Where-Object { $oids[$_.MemberOid] -ne $_.Section })
Check "steel" "every frame keeps its member's section" ($badSec.Count -eq 0) (($badSec | ForEach-Object { $_.Frame }) -join " ")
$jn = @{}; foreach ($x in $sj) { $jn[$x.Joint] = $x }
$dangling = @($sf | Where-Object { -not $jn.ContainsKey($_.JointI) -or -not $jn.ContainsKey($_.JointJ) })
Check "steel" "every frame's joints are in steel_joints.csv" ($dangling.Count -eq 0) (($dangling | ForEach-Object { $_.Frame }) -join " ")
$badRel = @($sf | Where-Object { ($_.ReleaseI -and $_.ReleaseI -ne "M2M3") -or ($_.ReleaseJ -and $_.ReleaseJ -ne "M2M3") })
Check "steel" "releases are M2M3 or none" ($badRel.Count -eq 0)
foreach ($c in "PINNED", "FIXED") { Check "steel" "restraint $c present" (@($sj | Where-Object { $_.Restraint -eq $c }).Count -gt 0) }
foreach ($c in "framed", "corner", "collinear", "open-pinned", "open-fixed") { Check "steel" "condition $c present" (@($sj | Where-Object { $_.Condition -eq $c }).Count -gt 0) }
$cpOff = @($sf | Where-Object { $_.CP -eq "8" -and (Num $_.OffZ_in) -ge 0 })
Check "steel" "CP 8 members hang below their line (OffZ < 0)" ($cpOff.Count -eq 0) (($cpOff | ForEach-Object { $_.Frame }) -join " ")

# ---- supports + spans
$sp = Rows "$o\revit.supports.csv"
Check "supports" "supports.csv has supports" ($sp.Count -gt 0) "$($sp.Count)"
$ductJ = @{}; foreach ($x in $fr) { $ductJ[$x.JointI] = 1; $ductJ[$x.JointJ] = 1 }
$bad = @($sp | Where-Object { -not $ductJ.ContainsKey($_.DuctJoint) -or -not $jn.ContainsKey($_.SteelJoint) })
Check "supports" "each support joins a duct joint to a steel joint" ($bad.Count -eq 0) (($bad | ForEach-Object { $_.SupportId }) -join " ")
$gm = [regex]::Match($match, 'gap <= ([0-9.]+) in')
$lim = $(if ($gm.Success) { Num $gm.Groups[1].Value } else { [double]::NaN })
$badGap = @($sp | Where-Object { -not ((Num $_.Gap_in) -le $lim) })
Check "supports" "every gap within the configured limit" ($gm.Success -and $badGap.Count -eq 0) ("limit $lim in; " + (($badGap | ForEach-Object { $_.SupportId + "=" + $_.Gap_in }) -join " "))
$skew = @($sp | Where-Object { [Math]::Abs((Num $_.DX) - (Num $_.SX)) -gt 1 -or [Math]::Abs((Num $_.DY) - (Num $_.SY)) -gt 1 -or (Num $_.DZ) -le (Num $_.SZ) })
Check "supports" "each stub is vertical, steel below the duct" ($skew.Count -eq 0) (($skew | ForEach-Object { $_.SupportId }) -join " ")
Check "spans" "unsupported spans flagged" (@($fr | Where-Object { $_.Flags -match 'UNSUPPORTED_SPAN' }).Count -gt 0)

# ---- viewer files
foreach ($b in "revit", "revit_clean") {
    Check "viewer" "$b.bin + features.json" ((Test-Path "$o\$b.bin") -and (Get-Item "$o\$b.bin").Length -gt 0 -and (Test-Path "$o\$b.features.json"))
}
$groups = @{}; $anchors = @{}
if (Test-Path "$o\revit_clean.features.json") {
    $fj = Get-Content "$o\revit_clean.features.json" -Raw | ConvertFrom-Json
    foreach ($g in $fj.groups.items) { $k = 0; foreach ($mm in $g.members) { $k += @($mm.ids).Count }; $groups[$g.name] = $k; $anchors[$g.name] = @($g.anchors).Count }
}
Check "viewer" "clean group 'Flag: UNSUPPORTED_SPAN'" ($groups["Flag: UNSUPPORTED_SPAN"] -gt 0)
# end-condition glyphs: one anchor per steel frame end of that condition (hinged = released corner / framed ends)
$ends = @($sf | ForEach-Object { "$($_.EndI)|$($_.ReleaseI)"; "$($_.EndJ)|$($_.ReleaseJ)" })
$want = [ordered]@{ "End: open-pinned" = @($ends | Where-Object { $_ -like "open-pinned|*" }).Count; "End: open-fixed" = @($ends | Where-Object { $_ -like "open-fixed|*" }).Count
    "End: hinged" = @($ends | Where-Object { $_ -eq "corner|M2M3" -or $_ -eq "framed|M2M3" }).Count; "End: collinear-hinged" = @($ends | Where-Object { $_ -eq "collinear|M2M3" }).Count }
foreach ($g in $want.Keys) { Check "viewer" "'$g' glyphs = steel_frames.csv ends" ($want[$g] -gt 0 -and $anchors[$g] -eq $want[$g]) "$($anchors[$g]) vs $($want[$g])" }
Check "viewer" "a 'Steel: <section>' group per section" (@($oids.Values | Sort-Object -Unique | Where-Object { -not $groups.ContainsKey("Steel: $_") }).Count -eq 0)
Check "viewer" "one TAP_STUB beam per support" ($groups["Support: TAP_STUB"] -eq $sp.Count) "$($groups['Support: TAP_STUB']) vs $($sp.Count)"
$types = @($sp | ForEach-Object { $_.Type } | Sort-Object -Unique)
foreach ($t in $types) { Check "viewer" "'Support: $t' glyphs = supports.csv rows of that type" ($anchors["Support: $t"] -eq @($sp | Where-Object { $_.Type -eq $t }).Count) "$($anchors["Support: $t"])" }

# ---- SAP (Run-SapDucts' own summary lines against the CSVs it was given)
if (-not $NoSap) {
    Check "sap" "demo.sdb saved" (Test-Path "$Root\sap\demo.sdb")
    $m = [regex]::Match($logText, 'NEW steel: (\d+) frames.*restraints (\d+) pinned \+ (\d+) fixed')
    Check "sap" "steel frames = steel_frames.csv" ($m.Success -and [int]$m.Groups[1].Value -eq $sf.Count) $m.Value
    if ($m.Success) {
        Check "sap" "pinned / fixed restraints = steel_joints.csv" ([int]$m.Groups[2].Value -eq @($sj | Where-Object { $_.Restraint -eq "PINNED" }).Count -and
            [int]$m.Groups[3].Value -eq @($sj | Where-Object { $_.Restraint -eq "FIXED" }).Count)
    }
    $m = [regex]::Match($logText, 'NEW supports: (\d+) TAP_STUB links')
    Check "sap" "TAP_STUB links = supports.csv" ($m.Success -and [int]$m.Groups[1].Value -eq $sp.Count) $m.Value
    Check "sap" "every rect frame oriented" ($logText -match 'NEW orientation: \d+ rect frames oriented, 0 without')

    # SAP model -> viewer with glyph looks (scripts\duct\sap-looks.csv): anchors against the CSVs SAP was built from
    $sfj = "$Root\sap\demo.features.json"
    Check "sap" "demo.bin + features.json exported" ((Test-Path "$Root\sap\demo.bin") -and (Test-Path $sfj))
    $si = @{}
    if (Test-Path $sfj) { foreach ($g in (Get-Content $sfj -Raw | ConvertFrom-Json).groups.items) { $si[$g.name] = $g } }
    function Anchors([string]$g) { if ($si.ContainsKey($g) -and $si[$g].anchors) { @($si[$g].anchors) } else { @() } }
    function NodeCount([string]$g) { $k = 0; if ($si.ContainsKey($g)) { foreach ($mm in $si[$g].members) { if ($mm.domain -eq "nodes") { $k += @($mm.nodeIds).Count } } }; $k }
    $relEnds = @($sf | Where-Object { $_.ReleaseI -eq "M2M3" }).Count + @($sf | Where-Object { $_.ReleaseJ -eq "M2M3" }).Count
    $a = Anchors "STEEL_RELEASED"
    Check "sap" "hinge glyphs (STEEL_RELEASED anchors) = M2M3 ends in steel_frames.csv" ($si["STEEL_RELEASED"].look.glyph -eq "half" -and $a.Count -eq $relEnds) "$($a.Count) vs $relEnds"
    Check "sap" "hinge glyphs sit at beam ends (t 0 / 1)" (@($a | Where-Object { $_.t -ne 0 -and $_.t -ne 1 }).Count -eq 0)
    foreach ($c in "open-fixed", "open-pinned") {
        $g = "STEEL_" + $c.Replace("-", "_").ToUpperInvariant(); $want = @($sj | Where-Object { $_.Condition -eq $c }).Count
        Check "sap" "$g glyph per joint = $c joints" ($si.ContainsKey($g) -and $si[$g].look.tier -eq "exception" -and (NodeCount $g) -eq $want) "$(NodeCount $g) vs $want"
    }
    foreach ($t in @($sp | Group-Object { $_.Type.ToUpperInvariant() })) {
        $a = Anchors ("SUPPORT_" + $t.Name)
        Check "sap" "SUPPORT_$($t.Name) glyphs = supports.csv, each at the duct end with a direction" ($a.Count -eq $t.Count -and @($a | Where-Object { $_.t -ne 0 -or -not $_.dir }).Count -eq 0) "$($a.Count) vs $($t.Count)"
    }
    $flags = @($si.Keys | Where-Object { $_ -like "FLAG_*" })
    Check "sap" "every FLAG_ group marks with warn, never repaints" ($flags.Count -gt 0 -and @($flags | Where-Object { $si[$_].look.glyph -ne "warn" }).Count -eq 0) ($flags -join " ")
    $secs = @($oids.Values | Sort-Object -Unique)
    Check "sap" "steel section groups are context" (@($secs | Where-Object { -not ($si.ContainsKey($_) -and @($si[$_].tags) -contains "context") }).Count -eq 0) ($secs -join " ")
}

Write-Output ("{0} checks, {1} failed" -f $script:n, $script:fails)
if ($script:fails) { exit 1 }
