# Get-DuctSettings (scripts\duct\Get-DuctSettings.ps1) and Export-DuctsViewer -Config. Windows PowerShell 5.1:
#   powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File scripts\duct\tests\Test-DuctSettings.ps1
$ErrorActionPreference = 'Stop'
$scripts = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
. (Join-Path $scripts "arms\Import-Arms.ps1")
. (Join-Path $scripts "duct\Get-DuctSettings.ps1")
$fail = 0
function Check($name, $cond) { if ($cond) { Write-Host "ok   $name" } else { Write-Host "FAIL $name"; $script:fail++ } }
function Throws($sb) { try { & $sb; return "" } catch { return $_.Exception.Message } }
$tmp = Join-Path $env:TEMP ("ductsettings_test_" + [guid]::NewGuid().ToString("N")); New-Item -ItemType Directory $tmp | Out-Null
$notes = New-Object 'System.Collections.Generic.List[string]'

# defaults: an empty config
$d = Get-DuctSettings -Data @{} -Mode Revit -Notes $notes
Check 'defaults: no systems, cuts, drops, steel' ($d.PrimarySystems.Count -eq 0 -and $d.CutPoints.Count -eq 0 -and $d.ManualDrop.Count -eq 0 -and $d.Steel.Count -eq 0)
Check 'defaults: rotation on, gap 4.5 in, span 10 ft, snap 6 in' ($d.RotateLargeVertical -and $d.Supports.GapMaxIn -eq 4.5 -and $d.Supports.SpanFt -eq 10 -and $d.SteelSettings.SnapIn -eq 6)
Check 'defaults: AddinDir = C:\Temp\hvac' ($d.AddinDir -eq 'C:\Temp\hvac' -and [DuctSettings]::AddinDefaultDir -eq 'C:\Temp\hvac')

# config keys
$cuts = Join-Path $tmp "cuts.csv"
"Name,Found,X_in,Y_in,Z_in`r`nCUT1,1,1,2,3`r`nCUT2,0,4,5,6`r`nKEEP1,1,7,8,9" | Set-Content $cuts
$drop = Join-Path $tmp "drop.txt"; "100`r`n200" | Set-Content $drop
$cfg = @{ DuctSystems = @('SYSA=#4f8cff', 'SYSB'); DuctMains = @('SYSA=123'); DuctCuts = $cuts; DuctDrop = @($drop)
          DuctSupportGapIn = 3; DuctSpanFt = 12; DuctSteelSnapIn = 5; DuctSteelNearIn = 20; DuctRotateLargeVertical = $false
          AddinDir = $tmp; RevitMeshNoRunExclude = @('Duct Linings') }
$notes.Clear()
$d = Get-DuctSettings -Data $cfg -Mode Revit -Notes $notes
Check 'systems + colour' ($d.PrimarySystems.Count -eq 2 -and $d.PrimaryColors['SYSA'] -eq '#4f8cff' -and -not $d.PrimaryColors.ContainsKey('SYSB'))
Check 'pinned main' ($d.PrimaryPins.Count -eq 1 -and $d.PrimaryPins[0].Value -eq '123')
Check 'cut points: Found=1 only' ($d.CutPoints.Count -eq 2 -and $d.CutPoints[1].Key -eq 'KEEP1' -and $d.CutPoints[0].Value[2] -eq 3)
Check 'drop list' ($d.ManualDrop.Count -eq 2 -and $d.ManualDrop.Contains('200'))
Check 'supports, steel, rotation' ($d.Supports.GapMaxIn -eq 3 -and $d.Supports.SpanFt -eq 12 -and $d.SteelSettings.SnapIn -eq 5 -and $d.SteelSettings.NearIn -eq 20 -and -not $d.RotateLargeVertical)
Check 'AddinDir' ($d.AddinDir -eq $tmp)
Check 'notes in the old order' ($notes.Count -eq 2 -and $notes[0] -like 'cut points: 2 from *' -and $notes[1] -like 'manual drop: 2 parts from *')
Check 'Revit mode does not read RevitMeshNoRunExclude' ($d.NoRunExclude.Count -eq 0)
$m = Get-DuctSettings -Data $cfg -Mode RevitMesh -Notes $notes
Check 'RevitMesh mode: exclusions only' ($m.NoRunExclude.Contains('duct linings') -and $m.CutPoints.Count -eq 0 -and $m.PrimarySystems.Count -eq 0)

# overrides and none
$d = Get-DuctSettings -Data $cfg -Mode Revit -Systems 'X=#000000' -Cuts none -Drop none -Notes $notes
Check '-Systems overrides DuctSystems' ($d.PrimarySystems.Count -eq 1 -and $d.PrimarySystems[0] -eq 'X')
Check '-Cuts none / -Drop none = off' ($d.CutPoints.Count -eq 0 -and $d.ManualDrop.Count -eq 0)

# missing files name their key
$e = Throws { Get-DuctSettings -Data @{ DuctCuts = (Join-Path $tmp 'nope.csv') } -Mode Revit -Notes $notes }
Check 'missing cuts file names DuctCuts' ($e -like 'cut points file not found:*DuctCuts*')
$e = Throws { Get-DuctSettings -Data @{ DuctDrop = @((Join-Path $tmp 'nope.txt')) } -Mode Revit -Notes $notes }
Check 'missing drop list names DuctDrop' ($e -like 'drop list not found:*DuctDrop*')
$e = Throws { Get-DuctSettings -Data @{ DuctSteel = (Join-Path $tmp 'nope.csv') } -Mode Revit -Notes $notes }
Check 'missing steel CSV names DuctSteel' ($e -like 'steel CSV not found:*DuctSteel*')

# Export-DuctsViewer -Config: a missing file is an error before anything runs; a given one is printed first
$ev = Join-Path $scripts "navis\Export-DuctsViewer.ps1"
$ErrorActionPreference = 'Continue'   # the child's error stream is what is checked
$ps = Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"
$out = & $ps -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $ev -Run $tmp -Config (Join-Path $tmp 'none.psd1') 2>&1 | Out-String; $out = $out -replace '\s+', ' '
Check '-Config missing: error names -Config' ($out -match 'config not found: .*none\.psd1 \(-Config\)')
$given = Join-Path $tmp 'given.psd1'; "@{ AddinDir = '$tmp' }" | Set-Content $given
$out = & $ps -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $ev -Run $tmp -Config $given 2>&1 | Out-String; $out = $out -replace '\s+', ' '
Check '-Config given: printed first (then the run-folder check fails, no ducts.csv)' ($out.TrimStart().StartsWith("config: $given") -and $out -match 'No ducts\.csv')

Remove-Item $tmp -Recurse -Force
if ($fail) { Write-Host "$fail check(s) FAILED"; exit 1 } else { Write-Host "all pass" }
