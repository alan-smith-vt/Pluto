# The whole duct pipeline on the synthetic demo, in one command. Windows PowerShell 5.1.
#   powershell -ExecutionPolicy Bypass -File scripts\duct\Run-Demo.ps1 [-Root <dir>] [-NoSap]
# 1. pythonTools\ducts\make_demo_data.py -> <Root> (Navisworks + Revit runs, rack steel, cuts, drop list, demo config)
# 2. Run-HvacModel.ps1 -Config <Root>\duct-config.psd1 -> <Root>\out: model\revit.* (overlay, clean model with steel and
#    supports, frames / steel / supports CSVs, match.txt), sap\hvac.sdb and sap\hvac.bin + features (the SAP model with
#    glyph looks), logs\ and run.log. -NoSap stops after the model step.
# The repo's duct-config.psd1 is never touched. <Root>\out from an earlier demo run is replaced.
param([string]$Root = (Join-Path $env:TEMP "hvac-demo"), [switch]$NoSap)
$ErrorActionPreference = "Stop"
$repo = Split-Path (Split-Path $PSScriptRoot)
$Root = [IO.Path]::GetFullPath($Root)
$run = "20261030-120000"
$ps = Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"
function Step([string]$t) { Write-Host ("[{0:HH:mm:ss}] == {1}" -f (Get-Date), $t) -ForegroundColor Cyan }

Step "generate $Root"
& python (Join-Path $repo "pythonTools\ducts\make_demo_data.py") --out $Root
if ($LASTEXITCODE -ne 0) { throw "make_demo_data.py failed" }

$out = Join-Path $Root "out"
if (Test-Path $out) {   # only an earlier demo output (run.log, or revit.match.txt before Run-HvacModel) is removed
    if (-not (Test-Path (Join-Path $out "run.log")) -and -not (Test-Path (Join-Path $out "revit.match.txt"))) { throw "$out exists and is not a demo output: move it away" }
    Remove-Item $out -Recurse -Force
}
$a = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Join-Path $PSScriptRoot "Run-HvacModel.ps1"),
    "-Config", (Join-Path $Root "duct-config.psd1"), "-Run", (Join-Path $Root "ducts\$run"), "-Revit", (Join-Path $Root "revit\$run"),
    "-Out", $out, "-Fragments", "-Other")
if ($NoSap) { $a += @("-To", "model") }
Step "Run-HvacModel"
& $ps @a
if ($LASTEXITCODE -ne 0) { throw "Run-HvacModel.ps1 failed (see $out\run.log)" }
