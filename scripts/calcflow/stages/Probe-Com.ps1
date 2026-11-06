<#
  Child stage (Windows PowerShell 5.1, STA): checks that Excel starts through COM and reports its version and
  bitness (the calc templates' Excel-only features, data tables and 365 functions, depend on it). For a future
  `doctor` check.
  Copied from pipe-review (src/stages/Probe-Com.ps1 at d4b33aa, 2026-11-06): prefix Pr -> Cf; the Mathcad checks
  were left out (CalcFlow's calcs are Excel workbooks).
#>
param([Parameter(Mandatory)][string]$Plan)
$ErrorActionPreference = 'Stop'
$cf = Split-Path -Parent $PSScriptRoot
. (Join-Path $cf 'Import-CalcFlow.ps1')
. (Join-Path $cf 'Stage.ps1')
. (Join-Path $cf 'ExcelCom.ps1')
Import-CalcFlowEngine
$p = Read-CfJson -Path $Plan
$r = [ordered]@{ Apartment = [Threading.Thread]::CurrentThread.GetApartmentState().ToString(); Excel = ''; ExcelVersion = ''; ExcelBits = '' }
$xl = $null
try {
    Set-CfHeartbeat -Path $p.Heartbeat -Step 'starting Excel' -Seconds 120
    $xl = New-CfExcel
    $r.Excel = 'OK'
    $r.ExcelVersion = [string]$xl.App.Version
    $r.ExcelBits = if ("$($xl.App.Path)" -match '\(x86\)') { '32-bit' } else { '64-bit' }
}
catch { $r.Excel = "FAILED: $($_.Exception.Message)" }
finally { Close-CfExcel $xl }
Write-CfJson -Path $p.ResultFile -Object $r
exit 0
