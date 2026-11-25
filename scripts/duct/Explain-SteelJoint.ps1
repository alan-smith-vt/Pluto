# Why a steel joint came out as it did (read-only diagnostic, 2026-11-25). Windows PowerShell 5.1:
#   powershell -ExecutionPolicy Bypass -File scripts\duct\Explain-SteelJoint.ps1 -Config <duct-config.psd1> -Oid <oid>,<oid>[,...]
# Takes the member oids at one joint (the viewer's steel label ends with the oid), reads the steel CSV of the config
# (DuctSteel, whole building), builds the steel model with the config's snap settings from the members within 20 ft
# of the named ones, and prints for each end of each named member: the condition it got, the other member ends at
# its joint, and every member within 24 in of the end with the distance to its line and whether the end lies beside
# that member or beyond its end. Writes nothing.
param(
    [Parameter(Mandatory = $true)][string]$Config,
    [Parameter(Mandatory = $true)][string[]]$Oid
)
$ErrorActionPreference = "Stop"
. (Join-Path (Split-Path $PSScriptRoot) "arms\Import-Arms.ps1")
$d = Import-PowerShellDataFile $Config
if (-not $d.DuctSteel -or -not (Test-Path $d.DuctSteel)) { throw "DuctSteel in $Config not found: $($d.DuctSteel)" }
$s = New-Object 'SteelModel+Settings'
if ($d.DuctSteelSnapIn) { $s.SnapIn = [double]$d.DuctSteelSnapIn }
if ($d.DuctSteelNearIn) { $s.NearIn = [double]$d.DuctSteelNearIn }
if ($d.DuctSteelSnap) { $s.SnapMode = [string]$d.DuctSteelSnap }
if ($d.DuctSteelContactIn) { $s.ContactIn = [double]$d.DuctSteelContactIn }
if ($d.ContainsKey('DuctSteelEndJoinIn')) { $s.EndJoinIn = [double]$d.DuctSteelEndJoinIn }
$sr = New-Object SteelToPluto; $sr.Progress = $false
$all = $sr.Build((Resolve-Path $d.DuctSteel).Path, $null)
$Oid = @($Oid | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$named = @($all | Where-Object { $Oid -contains $_.MemberOid })
$missing = @($Oid | Where-Object { $o = $_; -not ($named | Where-Object { $_.MemberOid -eq $o }) })
if ($missing.Count) { Write-Output ("not in the steel CSV: " + ($missing -join " ")) }
if (-not $named.Count) { exit 1 }

function ToIn([Vec3]$v) { return ,([double[]]@(($v.X / 0.0254), ($v.Y / 0.0254), ($v.Z / 0.0254))) }
function SegInfo($a, $b, $p) {
    $ab = @(($b[0] - $a[0]), ($b[1] - $a[1]), ($b[2] - $a[2])); $L2 = $ab[0] * $ab[0] + $ab[1] * $ab[1] + $ab[2] * $ab[2]
    $t = (($p[0] - $a[0]) * $ab[0] + ($p[1] - $a[1]) * $ab[1] + ($p[2] - $a[2]) * $ab[2]) / $L2
    $tc = [math]::Max(0, [math]::Min(1, $t)); $q = @(($a[0] + $ab[0] * $tc - $p[0]), ($a[1] + $ab[1] * $tc - $p[1]), ($a[2] + $ab[2] * $tc - $p[2]))
    @{ T = $t; D = [math]::Sqrt($q[0] * $q[0] + $q[1] * $q[1] + $q[2] * $q[2]); L = [math]::Sqrt($L2) }
}
function Axis($m) { $a = ToIn $m.P0; $b = ToIn $m.P1; $L = [math]::Sqrt(($b[0] - $a[0]) * ($b[0] - $a[0]) + ($b[1] - $a[1]) * ($b[1] - $a[1]) + ($b[2] - $a[2]) * ($b[2] - $a[2])); @((($b[0] - $a[0]) / $L), (($b[1] - $a[1]) / $L), (($b[2] - $a[2]) / $L)) }

# the neighbourhood: members with any point within 20 ft of a named member's end
$ends = @(); foreach ($m in $named) { $ends += , (ToIn $m.P0); $ends += , (ToIn $m.P1) }
$local = New-Object 'System.Collections.Generic.List[SteelMember]'
foreach ($m in $all) { $a = ToIn $m.P0; $b = ToIn $m.P1; foreach ($p in $ends) { if ((SegInfo $a $b $p).D -le 240) { $local.Add($m); break } } }
$sm = [SteelModel]::Build($local, $s)
Write-Output ("steel snap: {0}, SnapIn {1} in, EndJoinIn {2} in, collinear within {3} deg; {4} members within 20 ft built" -f $s.SnapMode, $s.SnapIn, $s.EndJoinIn, $s.CollinearDeg, $local.Count)

foreach ($m in $named) {
    $mi = $sm.Members.IndexOf($m); $ax = Axis $m
    $fr = @($sm.Frames | Where-Object { $_.Source -eq $mi })
    Write-Output ("{0} {1} {2:0.#} ft, slope {3:0} deg" -f $m.MemberOid, $m.SectionName, ((SegInfo (ToIn $m.P0) (ToIn $m.P1) (ToIn $m.P0)).L / 12), ([math]::Asin([math]::Min(1, [math]::Abs($ax[2]))) * 180 / [math]::PI))
    foreach ($k in 0, 1) {
        $p = if ($k -eq 0) { ToIn $m.P0 } else { ToIn $m.P1 }
        $f = if ($k -eq 0) { $fr[0] } else { $fr[-1] }
        $jid = if ($k -eq 0) { $f.I } else { $f.J }; $jt = $sm.Joints[$jid - 1]
        $endC = if ($k -eq 0) { $f.EndI } else { $f.EndJ }; $rel = if ($k -eq 0) { $f.ReleaseI } else { $f.ReleaseJ }
        $at = @($sm.Frames | Where-Object { ($_.I -eq $jid -or $_.J -eq $jid) -and $_.Source -ne $mi } | ForEach-Object { $sm.Members[$_.Source].MemberOid } | Sort-Object -Unique)
        Write-Output ("  end {0} ({1:0.#}, {2:0.#}, {3:0.#} in): {4}, release '{5}', joint {6} {7}; also at the joint: {8}" -f $(if ($k -eq 0) { "I" } else { "J" }), $p[0], $p[1], $p[2], $endC, $rel, $jt.Name, $jt.Condition, $(if ($at.Count) { $at -join " " } else { "-" }))
        $near = foreach ($o in $sm.Members) {
            if ($o -eq $m) { continue }
            $g = SegInfo (ToIn $o.P0) (ToIn $o.P1) $p
            if ($g.D -gt 24) { continue }
            $oa = Axis $o; $dot = [math]::Abs($ax[0] * $oa[0] + $ax[1] * $oa[1] + $ax[2] * $oa[2])
            $where = if ($g.T -lt 0 -or $g.T -gt 1) { "beyond its end by {0:0.#} in" -f ([math]::Max(-$g.T, $g.T - 1) * $g.L) } else { "beside it, {0:0.#} in from its nearer end" -f ([math]::Min($g.T, 1 - $g.T) * $g.L) }
            [pscustomobject]@{ D = $g.D; Text = ("    {0} {1}: {2:0.##} in from its line, {3}, {4}" -f $o.MemberOid, $o.SectionName, $g.D, $where, $(if ($dot -ge [math]::Cos($s.CollinearDeg * [math]::PI / 180)) { "IN LINE" } else { "{0:0} deg to it" -f ([math]::Acos([math]::Min(1, $dot)) * 180 / [math]::PI) })) }
        }
        $near = @($near | Sort-Object D)
        if ($near.Count) { $near | ForEach-Object { Write-Output $_.Text } } else { Write-Output "    no steel within 24 in" }
    }
}
