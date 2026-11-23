<#
.SYNOPSIS
  Runs every scripts\calcflow\tests\*.Tests.ps1 file. Works in Windows PowerShell 5.1 and PowerShell 7 (Windows or
  Linux). Excel is never started here: the COM stages are covered by parsing them, and Excel.Tests.ps1 runs only
  where Excel is installed. Every workbook the tests write is then checked by the open-source tools on the machine
  (tests\Tools.ps1: the Open XML SDK validator, LibreOffice), each skipped when absent.
  Under PowerShell 7 the Pluto lib (the .anl readers) is loaded from $env:PLUTO_DLL, a prebuilt assembly of
  scripts\lib + scripts\arms: the lib uses the .NET Framework's JSON serializer, which PowerShell 7 does not have.
  Copied from pipe-review (tests/Run-Tests.ps1 at d4b33aa, 2026-11-06): loads CalcFlow and the Pluto lib.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\calcflow\tests\Run-Tests.ps1
  powershell -ExecutionPolicy Bypass -File scripts\calcflow\tests\Run-Tests.ps1 -Filter AnlFrame
#>
[CmdletBinding()]
param([string]$Filter = '')

$ErrorActionPreference = 'Stop'
$cf = Split-Path -Parent $PSScriptRoot
$repo = Split-Path -Parent (Split-Path -Parent $cf)
. (Join-Path $PSScriptRoot 'Assert.ps1')
. (Join-Path $cf 'Import-CalcFlow.ps1')
Import-CalcFlowEngine
Import-PlutoLib
# the function files; the runners (Export-AnlFrame.ps1, CalcFlow.ps1) are run as child processes
foreach ($f in 'Stage.ps1', 'ExcelCom.ps1') { . (Join-Path $cf $f) }
. (Join-Path $PSScriptRoot 'Tools.ps1')
. (Join-Path $PSScriptRoot 'Fixture.ps1')
$script:Fixtures = Join-Path $PSScriptRoot 'fixtures'
$script:CalcFlow = $cf
$script:Repo = $repo

Write-Host ("PowerShell {0} ({1}), {2}" -f $PSVersionTable.PSVersion, $PSVersionTable.PSEdition, [Environment]::OSVersion.VersionString) -ForegroundColor DarkGray
$started = [DateTime]::UtcNow.AddSeconds(-2)
$files = @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.Tests.ps1' | Sort-Object Name | Where-Object { $_.Name -like "*$Filter*" })
foreach ($f in $files) {
    $script:CurrentFile = $f.Name
    Write-Host $f.Name -ForegroundColor Cyan
    try { . $f.FullName }
    catch {
        $script:TestResults.Add([pscustomobject]@{ File = $f.Name; Name = '(file)'; Ok = $false; Message = $_.Exception.Message; Ms = 0 })
        Write-Host ("  FAIL  (file) {0}" -f $_.Exception.Message) -ForegroundColor Red
    }
}
# every workbook the tests above wrote, checked by the open-source tools that are on this machine
$script:CurrentFile = 'workbooks'
$written = @(Get-CfWrittenWorkbooks $started)
if ($written.Count -gt 0) {
    Write-Host ("workbooks written by this run ({0})" -f $written.Count) -ForegroundColor Cyan
    Test 'each follows the Office Open XML schema Excel reads (Open XML SDK validator)' {
        $e = Invoke-CfOoxmlCheck $written
        if ($null -eq $e) { Write-Host '        (no dotnet SDK here: skipped)' -ForegroundColor DarkGray; return }
        Assert-True ($e.Count -eq 0) ($e -join '; ')
    }
    Test 'each opens in LibreOffice without a load error' {
        $e = Invoke-CfLibreOpen $written
        if ($null -eq $e) { Write-Host '        (no LibreOffice here: skipped)' -ForegroundColor DarkGray; return }
        Assert-True ($e.Count -eq 0) ($e -join '; ')
    }
}
$failed = @($script:TestResults | Where-Object { -not $_.Ok })
Write-Host ''
Write-Host ("{0} tests, {1} failed" -f $script:TestResults.Count, $failed.Count) -ForegroundColor $(if ($failed.Count) { 'Red' } else { 'Green' })
if ($failed.Count -gt 0) { exit 1 }
exit 0
