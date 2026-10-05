# Builds PlutoNavis.dll and installs it. No Visual Studio, no .NET SDK: compiles with the .NET Framework 4.x
# csc.exe that ships with Windows (C# 5), against the Autodesk.Navisworks.Api.dll of the installed Navisworks
# (Manage or Simulate, newest year unless -NavisRoot).
# Installs to <NavisRoot>\Plugins\PlutoNavis\ (folder name = DLL name) with a plain copy; when Windows refuses,
# it opens both folders in Explorer for a manual copy (never elevates: UAC needs IT here). The per-user
# %APPDATA%\Autodesk Navisworks <Product> <Year>\Plugins folder did NOT load on Simulate 2025 (2026-10-05);
# a stale copy there is removed. Close Navisworks first: a loaded DLL is locked.
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
& $csc /nologo /target:library /platform:x64 /optimize+ "/out:$dll" "/reference:$api" /reference:System.Windows.Forms.dll $src
if ($LASTEXITCODE -ne 0) { throw "compile failed" }
Write-Output "[build] $dll"

if (-not $NoInstall) {
    if (Get-Process Roamer -ErrorAction SilentlyContinue) { throw "Navisworks is running (Roamer.exe): close it, then rerun to install. The build is in $dll" }
    $plugins = Join-Path $NavisRoot "Plugins\PlutoNavis"
    # Plain copy, never elevation (a UAC prompt here needs IT credentials). When Windows refuses, open the
    # build folder and the Plugins folder in Explorer for a drag-and-drop copy.
    $copied = $false
    try {
        $null = New-Item -ItemType Directory -Force $plugins
        Copy-Item $dll $plugins -Force
        $copied = (Get-FileHash $dll).Hash -eq (Get-FileHash (Join-Path $plugins "PlutoNavis.dll")).Hash
    } catch { }
    if ($copied) { Write-Output "[install] $plugins\PlutoNavis.dll" }
    else {
        Write-Output "[install] no write access to $plugins from the script: copy by hand."
        Write-Output "          Drag $dll into $plugins (create the PlutoNavis folder if missing; replace the old file)."
        $plugParent = Split-Path $plugins
        Start-Process explorer.exe $out
        Start-Process explorer.exe $(if (Test-Path $plugins) { $plugins } else { $plugParent })
    }
    $stale = Join-Path $env:APPDATA ("Autodesk " + $product + "\Plugins\PlutoNavis")
    if (Test-Path $stale) { Remove-Item -Recurse -Force $stale; Write-Output "[install] removed the old per-user copy $stale" }
    Write-Output "[next] start $product, ribbon 'Tool add-ins 1'"
}
