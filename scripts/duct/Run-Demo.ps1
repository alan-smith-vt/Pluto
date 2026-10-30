# The whole duct pipeline on the synthetic demo, in one command. Windows PowerShell 5.1.
#   powershell -ExecutionPolicy Bypass -File scripts\duct\Run-Demo.ps1 [-Root <dir>] [-NoSap]
# 1. pythonTools\ducts\make_demo_data.py -> <Root> (Navisworks + Revit runs, rack steel, cuts, drop list, demo config)
# 2. Export-DuctsViewer.ps1 -Run -Revit with the demo config -> <Root>\out\revit.* (overlay, clean model with steel
#    and supports, frames / steel / supports CSVs, match.txt)
# 3. Run-SapDucts.ps1 with steel, supports and the flag groups -> <Root>\sap\demo.sdb, then Run-Sap.ps1 -Export
#    -NoResults -Looks scripts\duct\sap-looks.csv -> <Root>\sap\demo.bin + features (skipped with -NoSap)
# The exporter reads scripts\duct\duct-config.psd1: the demo config is swapped in for step 2 and the user's put back.
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

Step "Export-DuctsViewer -Run -Revit"
$cfg = Join-Path $repo "scripts\duct\duct-config.psd1"; $bak = $cfg + ".demo-bak"
$out = Join-Path $Root "out"; $null = New-Item -ItemType Directory -Force $out
if (Test-Path $cfg) { Move-Item $cfg $bak -Force }
try {
    Copy-Item (Join-Path $Root "duct-config.psd1") $cfg
    & $ps -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repo "scripts\navis\Export-DuctsViewer.ps1") `
        -Run (Join-Path $Root "ducts\$run") -Revit (Join-Path $Root "revit\$run") -Out (Join-Path $out "revit")
    if ($LASTEXITCODE -ne 0) { throw "Export-DuctsViewer.ps1 failed" }
} finally {
    Remove-Item $cfg -ErrorAction SilentlyContinue
    if (Test-Path $bak) { Move-Item $bak $cfg -Force }
}

if (-not $NoSap) {
    Step "Run-SapDucts (ducts + steel + supports)"
    & $ps -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repo "scripts\sap\Run-SapDucts.ps1") `
        -Frames (Join-Path $out "revit.frames.csv") -SteelFrames (Join-Path $out "revit.steel_frames.csv") `
        -Supports (Join-Path $out "revit.supports.csv") -SavePath (Join-Path $Root "sap\demo.sdb") -Fragments -Other
    if ($LASTEXITCODE -ne 0) { throw "Run-SapDucts.ps1 failed" }
    Step "Run-Sap -Export (SAP model -> viewer, glyph looks)"
    & $ps -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repo "scripts\sap\Run-Sap.ps1") `
        -Open (Join-Path $Root "sap\demo.sdb") -NoSaveSdb -Export (Join-Path $Root "sap\demo") -NoResults `
        -Looks (Join-Path $repo "scripts\duct\sap-looks.csv")
    if ($LASTEXITCODE -ne 0) { throw "Run-Sap.ps1 -Export failed" }
}
Step "done. Viewer: $out\revit_clean.bin (+ revit.bin overlay)$(if (-not $NoSap) { "; SAP: $Root\sap\demo.sdb, viewer $Root\sap\demo.bin" })"
