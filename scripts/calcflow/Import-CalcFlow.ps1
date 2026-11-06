# Import-CalcFlow.ps1 -- loads CalcFlow's C# once per PowerShell session. Dot-source it, then call:
#   Import-CalcFlowEngine   scripts\calcflow\cs\*.cs (namespace CalcFlow: the .xlsx reader / writer without Excel,
#                           CSV records, calc run state, the Excel window -> process id helper)
#   Import-PlutoLib         Pluto's one C# batch, scripts\lib + scripts\arms, which holds the STAAD .anl readers
#                           (AnlFrameReader): compiled through scripts\lib\Config.ps1. Development only:
#                           $env:PLUTO_DLL names a prebuilt assembly instead (as scripts\frame\Build-Frame.ps1 does);
#                           that is how the tests run under PowerShell 7 on Linux.
# Windows PowerShell 5.1 compiles as C# 5 with warnings as errors (the .NET Framework compiler); PowerShell 7
# compiles the CalcFlow sources with Roslyn. A type cannot be unloaded: after editing C#, start a new session.
# Adapted from pipe-review (src/Engine.ps1 at d4b33aa, 2026-11-06).

function Import-CalcFlowEngine {
    if ('CalcFlow.Xlsx' -as [type]) { return }
    $csDir = Join-Path $PSScriptRoot 'cs'
    $files = @(Get-ChildItem -LiteralPath $csDir -Filter '*.cs' | Sort-Object Name | ForEach-Object { $_.FullName })
    if ($files.Count -eq 0) { throw "No C# sources found in $csDir" }
    if ($PSVersionTable.PSEdition -eq 'Core') {
        Add-Type -Path $files -ErrorAction Stop
    }
    else {
        Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
        $refs = @('System.IO.Compression', 'System.IO.Compression.FileSystem', 'System.Xml', 'System.Core')
        Add-Type -Path $files -ReferencedAssemblies $refs -ErrorAction Stop
    }
}

function Import-PlutoLib {
    if ('AnlFrameReader' -as [type]) { return }
    if ($env:PLUTO_DLL) {
        Add-Type -Path $env:PLUTO_DLL
        return
    }
    . (Join-Path (Join-Path (Split-Path -Parent $PSScriptRoot) 'lib') 'Config.ps1')
}
