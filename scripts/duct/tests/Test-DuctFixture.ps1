# Regression fixture for the Revit duct path (compartmentalizing step 0, vault/arms/revit-duct-model.md).
# Generates the synthetic demo (pythonTools/ducts/make_demo_data.py, fixed seed) into a temp root, runs
# Export-DuctsViewer.ps1 -Run -Revit on it with the demo duct config, and diffs every text output against
# scripts/duct/tests/expected/. Temp paths are written as <ROOT>. Windows PowerShell 5.1.
#   powershell -ExecutionPolicy Bypass -File scripts\duct\tests\Test-DuctFixture.ps1 [-Update] [-Keep]
# -Update rewrites expected/ from this run (only for an intended output change: say so in the commit).
param([switch]$Update, [switch]$Keep)
$ErrorActionPreference = "Stop"
$repo = Split-Path (Split-Path (Split-Path $PSScriptRoot))
$expected = Join-Path $PSScriptRoot "expected"
$root = Join-Path ([IO.Path]::GetTempPath()) ("pluto-duct-fixture-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
$cfg = Join-Path $repo "scripts\duct\duct-config.psd1"
$cfgBak = $cfg + ".fixture-bak"
$run = "20261030-120000"

& python (Join-Path $repo "pythonTools\ducts\make_demo_data.py") --out $root | Out-Null
if ($LASTEXITCODE -ne 0) { throw "make_demo_data.py failed" }

# the exporter reads scripts\duct\duct-config.psd1: swap in the demo config for the run, put the user's back after
if (Test-Path $cfg) { Move-Item $cfg $cfgBak -Force }
try {
    Copy-Item (Join-Path $root "duct-config.psd1") $cfg
    $out = Join-Path $root "out"
    New-Item -ItemType Directory $out | Out-Null
    $log = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repo "scripts\navis\Export-DuctsViewer.ps1") `
        -Run (Join-Path $root "ducts\$run") -Revit (Join-Path $root "revit\$run") -Out (Join-Path $out "revit") 2>&1
    if ($LASTEXITCODE -ne 0) { $log | Out-Host; throw "Export-DuctsViewer.ps1 failed" }
    $log | Out-File (Join-Path $out "run.log") -Encoding utf8
} finally {
    Remove-Item $cfg -ErrorAction SilentlyContinue
    if (Test-Path $cfgBak) { Move-Item $cfgBak $cfg -Force }
}

# text outputs only (.bin is checked through geometryHash in its features); temp root normalised
$got = @{}
Get-ChildItem $out -File | Where-Object { $_.Extension -in ".txt", ".csv", ".json", ".log" } | ForEach-Object {
    $t = [IO.File]::ReadAllText($_.FullName)
    $t = $t.Replace($root, "<ROOT>").Replace($root.Replace("\", "\\"), "<ROOT>").Replace("`r`n", "`n")
    # group ids are fresh GUIDs on every run; geometryHash still pins the .bin
    if ($_.Extension -eq ".json") { $t = [Text.RegularExpressions.Regex]::Replace($t, '"id": "[0-9a-f]{32}"', '"id": "<ID>"') }
    if ($_.Extension -eq ".log") { $t = [Text.RegularExpressions.Regex]::Replace($t, '\[done\] [0-9.]+s', '[done] <T>s') }
    $got[$_.Name] = $t
}

if ($Update) {
    if (Test-Path $expected) { Remove-Item $expected -Recurse -Force }
    New-Item -ItemType Directory $expected | Out-Null
    foreach ($k in $got.Keys) { [IO.File]::WriteAllText((Join-Path $expected $k), $got[$k], (New-Object Text.UTF8Encoding($false))) }
    Write-Output ("expected/ updated: {0} files" -f $got.Count)
} else {
    $fail = 0
    $names = @($got.Keys) + @(Get-ChildItem $expected -File | ForEach-Object { $_.Name }) | Sort-Object -Unique
    foreach ($k in $names) {
        $e = Join-Path $expected $k
        if (-not (Test-Path $e)) { Write-Output "NEW      $k"; $fail++; continue }
        if (-not $got.ContainsKey($k)) { Write-Output "MISSING  $k"; $fail++; continue }
        $et = [IO.File]::ReadAllText($e).Replace("`r`n", "`n")
        if ($et -ne $got[$k]) {
            $a = $et -split "`n"; $b = $got[$k] -split "`n"; $i = 0
            while ($i -lt $a.Count -and $i -lt $b.Count -and $a[$i] -eq $b[$i]) { $i++ }
            Write-Output ("DIFF     {0} (first at line {1})" -f $k, ($i + 1))
            $fail++
        } else { Write-Output "ok       $k" }
    }
    Write-Output ("{0} files, {1} differ" -f $names.Count, $fail)
    if (-not $Keep) { Remove-Item $root -Recurse -Force }
    if ($fail -gt 0) { exit 1 }
}
if ($Keep -or $Update) { Write-Output "work folder: $root" }
