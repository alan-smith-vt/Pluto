# DuctOrient geometry checks (synthetic). Windows PowerShell 5.1:
#   powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File scripts\duct\tests\Test-DuctOrient.ps1
# The run propagation and the flags are covered by the duct fixture (orientation case of the demo data).
$ErrorActionPreference = 'Stop'
$src = Get-Content -Raw (Join-Path (Split-Path $PSScriptRoot -Parent) 'model\Orienter.cs')
$cls = $src.Substring($src.IndexOf('public static class DuctOrient'))
Add-Type -TypeDefinition ("using System;`r`n" + $cls)
$fail = 0
function Check($name, $cond) { if ($cond) { Write-Host "ok   $name" } else { Write-Host "FAIL $name"; $script:fail++ } }
function Near($a, $b) { [math]::Abs($a - $b) -lt 1e-9 }
function V($x, $y, $z) { return [double[]]@($x, $y, $z) }

$x = V 1 0 0; $y = V 0 1 0; $z = V 0 0 1
# 18 wide (along Y) x 36 high, running along X: larger side vertical
Check '18x36, width horizontal: larger side vertical' ([DuctOrient]::State($x, $y, 18, 36, 1) -eq 1)
Check '36x18, width horizontal: larger side horizontal' ([DuctOrient]::State($x, $y, 36, 18, 1) -eq -1)
Check '36x18, width vertical: larger side vertical' ([DuctOrient]::State($x, $z, 36, 18, 1) -eq 1)
Check 'square: neither' ([DuctOrient]::State($x, $y, 24, 24.5, 1) -eq 0)

# rotation about the axis: the 18x36 then has its larger side horizontal
$u2 = [DuctOrient]::Rotate($x, $y)
Check 'rotated width dir is vertical' ((Near ([math]::Abs($u2[2])) 1))
Check 'rotated 18x36: larger side horizontal' ([DuctOrient]::State($x, $u2, 18, 36, 1) -eq -1)

# a riser (axis Z) carrying the same width dir Y, rotated about its own axis: its 36 side must point the same way
# as the horizontal's 36 side after rotation (they meet at the bend)
$r2 = [DuctOrient]::Rotate($z, $y)
$hH = @(($x[1] * $u2[2] - $x[2] * $u2[1]), ($x[2] * $u2[0] - $x[0] * $u2[2]), ($x[0] * $u2[1] - $x[1] * $u2[0]))   # horizontal's H dir
$rH = @(($z[1] * $r2[2] - $z[2] * $r2[1]), ($z[2] * $r2[0] - $z[0] * $r2[2]), ($z[0] * $r2[1] - $z[1] * $r2[0]))   # riser's H dir
Check 'riser and horizontal: 36 side along the same line after rotation' ((Near ([math]::Abs($hH[0] * $rH[0] + $hH[1] * $rH[1] + $hH[2] * $rH[2])) 1))
Check 'riser width dir stays horizontal' ((Near $r2[2] 0))

if ($fail -gt 0) { Write-Host "$fail FAILED"; exit 1 } else { Write-Host 'all passed' }
