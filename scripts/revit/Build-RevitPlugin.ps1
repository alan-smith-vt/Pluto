# Builds PlutoRevit.dll and installs it for the current user. No Visual Studio, no .NET SDK, no Roslyn: compiles a
# .NET Framework 4.x DLL with the csc.exe that ships with Windows (C# 5) against the installed Revit's RevitAPI.dll /
# RevitAPIUI.dll (Revit 2025+ runs .NET 8 and loads Framework DLLs that avoid removed APIs). Revit 2025's API is built
# on .NET 8, so the Framework facades (System.Runtime etc.) are referenced to resolve its base types; CS1701/CS1702
# version-unification warnings are expected and suppressed.
# Install = copy the DLL + PlutoRevit.addin into %APPDATA%\Autodesk\Revit\Addins\<year>\ (per-user, no elevation).
# Close Revit first (a loaded DLL is locked). Then: Add-Ins tab -> External Tools -> Pluto Connectors.
#   powershell -ExecutionPolicy Bypass -File Build-RevitPlugin.ps1 [-RevitRoot <dir>] [-NoInstall]
param(
    [string]$RevitRoot,
    [switch]$NoInstall
)
$ErrorActionPreference = "Stop"
if (-not $RevitRoot) {
    $RevitRoot = Get-ChildItem "C:\Program Files\Autodesk" -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^Revit \d{4}$' -and (Test-Path (Join-Path $_.FullName "RevitAPI.dll")) } |
        Sort-Object { [int]($_.Name -replace '\D', '') } | Select-Object -Last 1 -ExpandProperty FullName
}
if (-not $RevitRoot -or -not (Test-Path (Join-Path $RevitRoot "RevitAPI.dll"))) { throw "No Revit found. Pass -RevitRoot '<install folder>'." }
$year = (Split-Path $RevitRoot -Leaf) -replace '\D', ''
$api = Join-Path $RevitRoot "RevitAPI.dll"
$ui = Join-Path $RevitRoot "RevitAPIUI.dll"
Write-Output ("[revit] {0}  (API {1})" -f $RevitRoot, (Get-Item $api).VersionInfo.FileVersion)

$fw = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319"
$csc = Join-Path $fw "csc.exe"
if (-not (Test-Path $csc)) { throw "csc.exe not found at $csc" }
$refs = @("/reference:$api", "/reference:$ui")
foreach ($f in "System.Runtime.dll", "System.Collections.dll", "netstandard.dll") {
    foreach ($d in $fw, (Join-Path $fw "WPF")) { $p = Join-Path $d $f; if (Test-Path $p) { $refs += "/reference:$p"; break } }
}
Write-Output ("[refs]  " + (($refs | ForEach-Object { Split-Path ($_ -replace '^/reference:', '') -Leaf }) -join ', '))
$out = Join-Path $PSScriptRoot "bin"
$null = New-Item -ItemType Directory -Force $out
$dll = Join-Path $out "PlutoRevit.dll"
$src = @(Get-ChildItem $PSScriptRoot -Filter *.cs | ForEach-Object FullName)
& $csc /nologo /target:library /platform:x64 /optimize+ /nowarn:1701,1702 "/out:$dll" $refs $src
if ($LASTEXITCODE -ne 0) { throw "compile failed (paste the errors above)" }
Write-Output "[build] $dll"

if (-not $NoInstall) {
    $dest = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$year"
    $null = New-Item -ItemType Directory -Force $dest
    if (Get-Process Revit -ErrorAction SilentlyContinue) { Write-Output "[warn]  Revit is running: close it, then run this again (the DLL is locked once loaded)" }
    Copy-Item $dll (Join-Path $dest "PlutoRevit.dll") -Force
    $manifest = (Get-Content (Join-Path $PSScriptRoot "PlutoRevit.addin") -Raw) -replace '__DLL__', [Security.SecurityElement]::Escape((Join-Path $dest "PlutoRevit.dll"))
    Set-Content -Path (Join-Path $dest "PlutoRevit.addin") -Value $manifest -Encoding UTF8
    Write-Output "[install] $dest\PlutoRevit.dll + PlutoRevit.addin"
    Write-Output "[next]  start Revit, open the model, Add-Ins tab -> External Tools -> Pluto Connectors (first load asks to trust the add-in: Always Load)"
}
