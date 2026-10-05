# Builds PlutoNavis.dll. No Visual Studio, no .NET SDK: compiles with the .NET Framework 4.x csc.exe that
# ships with Windows (C# 5), against the Autodesk.Navisworks.Api.dll of the installed Navisworks (Manage or
# Simulate, newest year unless -NavisRoot), then prints where to copy it: <NavisRoot>\Plugins\PlutoNavis\
# (folder name = DLL name). The copy is by hand in Explorer: the script has no write access to Program Files
# and never elevates (UAC needs IT on the work machine). The per-user %APPDATA%\Autodesk Navisworks
# <Product> <Year>\Plugins folder did NOT load on Simulate 2025 (2026-10-05); a stale copy there is removed.
#   powershell -ExecutionPolicy Bypass -File Build-NavisPlugin.ps1 [-NavisRoot <dir>] [-NoInstall]
param(
    [string]$NavisRoot,
    [switch]$NoInstall
)
$ErrorActionPreference = "Stop"
if (-not $NavisRoot) {
    $NavisRoot = Get-ChildItem "C:\Program Files\Autodesk" -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^Navisworks (Manage|Simulate) \d{4}$' -and (Test-Path (Join-Path $_.FullName "Autodesk.Navisworks.Api.dll")) } |
        Sort-Object { [int]($_.Name -replace '\D', '') } | Select-Object -Last 1 -ExpandProperty FullName
}
if (-not $NavisRoot -or -not (Test-Path (Join-Path $NavisRoot "Autodesk.Navisworks.Api.dll"))) {
    throw "No Navisworks Manage / Simulate found (Freedom cannot load add-ins). Pass -NavisRoot '<install folder>'."
}
$product = Split-Path $NavisRoot -Leaf                       # e.g. "Navisworks Simulate 2025"
$api = Join-Path $NavisRoot "Autodesk.Navisworks.Api.dll"
Write-Output ("[navis] {0}  (API {1})" -f $NavisRoot, (Get-Item $api).VersionInfo.FileVersion)

$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { throw "csc.exe not found at $csc" }
$out = Join-Path $PSScriptRoot "bin"
$null = New-Item -ItemType Directory -Force $out
$dll = Join-Path $out "PlutoNavis.dll"
$src = @(Get-ChildItem $PSScriptRoot -Filter *.cs | ForEach-Object FullName)
$refs = @("/reference:$api", "/reference:System.Windows.Forms.dll")
foreach ($com in "Autodesk.Navisworks.ComApi.dll", "Autodesk.Navisworks.Interop.ComApi.dll") {   # triangles (ComApiBridge)
    $p = Join-Path $NavisRoot $com
    if (-not (Test-Path $p)) { throw "$com not found in $NavisRoot" }
    $refs += "/reference:$p"
}
& $csc /nologo /target:library /platform:x64 /optimize+ "/out:$dll" $refs $src
if ($LASTEXITCODE -ne 0) { throw "compile failed" }
Write-Output "[build] $dll"

if (-not $NoInstall) {
    # The script has no write access to Program Files and never elevates: the copy is by hand in Explorer.
    $stale = Join-Path $env:APPDATA ("Autodesk " + $product + "\Plugins\PlutoNavis")
    if (Test-Path $stale) { Remove-Item -Recurse -Force $stale; Write-Output "[clean] removed the old per-user copy $stale" }
    Write-Output ("[copy]  close Navisworks, then copy`n          {0}`n        into`n          {1}" -f $dll, (Join-Path $NavisRoot "Plugins\PlutoNavis\"))
    Write-Output "[next]  start $product, ribbon 'Tool add-ins 1'"
}
