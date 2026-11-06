<#
  Child stage (Windows PowerShell 5.1, STA): saves an .xls / .xlsb as .xlsx through Excel (read-only open, no
  links, no macros), so the file can be read without Excel afterwards.
  Copied from pipe-review (src/stages/Convert-Workbook.ps1 at d4b33aa, 2026-11-06): prefix Pr -> Cf.
#>
param([Parameter(Mandatory)][string]$Plan)
$ErrorActionPreference = 'Stop'
$cf = Split-Path -Parent $PSScriptRoot
. (Join-Path $cf 'Import-CalcFlow.ps1')
. (Join-Path $cf 'Stage.ps1')
. (Join-Path $cf 'ExcelCom.ps1')
Import-CalcFlowEngine
$p = Read-CfJson -Path $Plan
$result = @{ Ok = $false; Message = '' }
$xl = $null; $wb = $null
try {
    Set-CfHeartbeat -Path $p.Heartbeat -Step 'starting Excel' -Seconds 120
    $xl = New-CfExcel
    Set-CfHeartbeat -Path $p.Heartbeat -Step "opening $($p.Source)" -Seconds 300 -KillPid $xl.Pid
    $wb = Open-CfWorkbook -Excel $xl -Path $p.Source -ReadOnly
    if (Test-Path -LiteralPath $p.Target) { Remove-Item -LiteralPath $p.Target -Force }
    Set-CfHeartbeat -Path $p.Heartbeat -Step 'saving as .xlsx' -Seconds 300 -KillPid $xl.Pid
    $wb.SaveAs($p.Target, 51)
    $wb.Close($false); $wb = $null
    $result.Ok = $true
}
catch {
    $result.Message = $_.Exception.Message
    if ($wb) { try { $wb.Close($false) } catch { } }
    if (Test-Path -LiteralPath $p.Target) { Remove-Item -LiteralPath $p.Target -Force -ErrorAction SilentlyContinue }
}
finally {
    Close-CfExcel $xl
    Write-CfJson -Path $p.ResultFile -Object $result
}
if (-not $result.Ok) { exit 1 }
exit 0
