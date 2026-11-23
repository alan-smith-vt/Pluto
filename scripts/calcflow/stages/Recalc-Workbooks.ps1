<#
  Child stage (Windows PowerShell 5.1, STA): Excel recalculates a batch of workbooks. Each is a copy of a calc
  template with its input cells already written (CalcFlow.XlsxPatch); the template itself is never opened here.
  For each file: open (no links, no macros, alerts off), calculate in full, save as OutDir\<same name>, close.
  A file Excel cannot open or save is listed in the result and left out; the rest go on. A heartbeat before every
  file lets the parent stop exactly this Excel if it stops answering.
  Plan (JSON): Files (paths), OutDir, Visible, SecondsPerFile. Result: Ok, Version, Done, Failed (File, Message).
#>
param([Parameter(Mandatory)][string]$Plan)
$ErrorActionPreference = 'Stop'
$cf = Split-Path -Parent $PSScriptRoot
. (Join-Path $cf 'Import-CalcFlow.ps1')
. (Join-Path $cf 'Stage.ps1')
. (Join-Path $cf 'ExcelCom.ps1')
Import-CalcFlowEngine
$p = Read-CfJson -Path $Plan
$result = @{ Ok = $false; Version = ''; Done = 0; Failed = @(); Message = '' }
$failed = New-Object System.Collections.Generic.List[object]
$xl = $null
$perFile = 300
if ($p.SecondsPerFile) { $perFile = [int]$p.SecondsPerFile }
try {
    Set-CfHeartbeat -Path $p.Heartbeat -Step 'starting Excel' -Seconds 120
    $xl = New-CfExcel -Visible:([bool]$p.Visible)
    $result.Version = [string]$xl.App.Version
    if (-not (Test-Path -LiteralPath $p.OutDir)) { New-Item -ItemType Directory -Path $p.OutDir | Out-Null }
    $n = 0
    foreach ($f in @($p.Files)) {
        $n++
        $name = [IO.Path]::GetFileName($f)
        $target = Join-Path $p.OutDir $name
        $wb = $null
        try {
            Set-CfHeartbeat -Path $p.Heartbeat -Step ("calculating {0} ({1} of {2})" -f $name, $n, @($p.Files).Count) -Seconds $perFile -KillPid $xl.Pid
            # read-only: a template's write-reservation password never prompts; the copy is saved under a new name
            $wb = Open-CfWorkbook -Excel $xl -Path $f -ReadOnly
            Invoke-CfCom { $xl.App.CalculateFull() } | Out-Null
            # 51 = xlOpenXMLWorkbook, 52 = xlOpenXMLWorkbookMacroEnabled
            $format = 51
            if ([IO.Path]::GetExtension($f) -ieq '.xlsm') { $format = 52 }
            if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Force }
            Invoke-CfCom { $wb.SaveAs($target, $format) } | Out-Null
            $wb.Close($false); $wb = $null
            $result.Done++
        }
        catch {
            $failed.Add(@{ File = $f; Message = $_.Exception.Message })
            if ($wb) { try { $wb.Close($false) } catch { } }
            if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Force -ErrorAction SilentlyContinue }
        }
    }
    $result.Ok = $true
}
catch {
    $result.Message = $_.Exception.Message
}
finally {
    Close-CfExcel $xl
    $result.Failed = $failed.ToArray()
    Write-CfJson -Path $p.ResultFile -Object $result
}
if (-not $result.Ok) { exit 1 }
exit 0
