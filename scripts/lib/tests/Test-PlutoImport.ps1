# Test of the C# build: Import-PlutoCore.ps1 / ..\arms\Import-Arms.ps1 (Windows PowerShell 5.1).
#   powershell -ExecutionPolicy Bypass -File scripts\lib\tests\Test-PlutoImport.ps1
# Each case runs in a fresh powershell.exe (types cannot be unloaded from a session):
#   core alone (no arms types), arms (core + arms, two dlls), dot-sourced twice, warm cache (no rebuild),
#   in-memory fallback when no dll can be written or loaded (PLUTO_CS_CACHE points at a file).
$ErrorActionPreference = "Stop"
$lib = Split-Path $PSScriptRoot
$arms = Join-Path (Split-Path $lib) "arms"
$fail = 0
function Check([bool]$ok, [string]$what) { if ($ok) { Write-Output "ok   $what" } else { Write-Output "FAIL $what"; $script:fail++ } }
function Run([string]$body, [hashtable]$envVars = @{}) {
    $f = Join-Path ([IO.Path]::GetTempPath()) ("pluto-import-case-" + [Guid]::NewGuid().ToString("N").Substring(0, 8) + ".ps1")
    [IO.File]::WriteAllText($f, $body)
    $old = @{}
    foreach ($k in $envVars.Keys) { $old[$k] = [Environment]::GetEnvironmentVariable($k); [Environment]::SetEnvironmentVariable($k, $envVars[$k]) }
    try { $o = & powershell -NoProfile -ExecutionPolicy Bypass -File $f 2>&1 | ForEach-Object { "$_" } }
    finally { foreach ($k in $old.Keys) { [Environment]::SetEnvironmentVariable($k, $old[$k]) }; Remove-Item $f -ErrorAction SilentlyContinue }
    $o
}
$probe = '"core=" + [bool](''FeaturesSidecar'' -as [type]); "arms=" + [bool](''DuctsToPluto'' -as [type]); "coreDll=$PlutoCoreDll"; "armsDll=$PlutoArmsDll"'

$o = Run (". '$lib\Import-PlutoCore.ps1'`n$probe")
Check ($o -contains "core=True") "core alone: core types loaded"
Check ($o -contains "arms=False") "core alone: no arms types (core compiles without the domain code)"
Check (($o | Where-Object { $_ -like "coreDll=*PlutoCore-*.dll" }).Count -eq 1) "core alone: cached core dll"

$o = Run (". '$arms\Import-Arms.ps1'`n. '$arms\Import-Arms.ps1'`n. '$lib\Import-PlutoCore.ps1'`n$probe")
Check ($o -contains "core=True" -and $o -contains "arms=True") "arms, dot-sourced twice + core again: all types, no error"
Check (($o | Where-Object { $_ -like "armsDll=*PlutoArms-*.dll" }).Count -eq 1) "arms: cached arms dll"
$armsDll = ($o | Where-Object { $_ -like "armsDll=*" }) -replace '^armsDll=', ''
$t0 = (Get-Item $armsDll).LastWriteTimeUtc

$o = Run (". '$arms\Import-Arms.ps1'`n$probe")
Check ((($o | Where-Object { $_ -like "armsDll=*" }) -replace '^armsDll=', '') -eq $armsDll -and (Get-Item $armsDll).LastWriteTimeUtc -eq $t0) "warm cache: same dll, not rebuilt"

$blocker = Join-Path ([IO.Path]::GetTempPath()) ("pluto-import-blocker-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
[IO.File]::WriteAllText($blocker, "")   # a file where the cache folder should be: no dll can be written there
try {
    $o = Run (". '$arms\Import-Arms.ps1'`n$probe`n'cs=' + ([type]'DuctsToPluto').Assembly.Location") @{ PLUTO_CS_CACHE = $blocker }
} finally { Remove-Item $blocker -ErrorAction SilentlyContinue }
Check ($o -contains "core=True" -and $o -contains "arms=True") "fallback: all types loaded in memory"
Check ($o -contains "coreDll=" -and $o -contains "cs=") "fallback: no cached dll used"
Check (($o | Where-Object { $_ -like "*compiling in memory*" }).Count -ge 1) "fallback: says so (warning)"

if ($fail) { Write-Output "$fail failed"; exit 1 } else { Write-Output "all passed" }
