# ExcelCom.ps1 -- Excel COM helpers for the child stages (scripts\calcflow\stages). Functions only; nothing runs on load.
# One dedicated Excel instance per stage, its process id taken from its window handle, alerts / events / macros
# off, links never updated. Values are written as whole blocks (one Value2 call per column).
# Copied from pipe-review (src/ExcelCom.ps1 at d4b33aa, 2026-11-06): prefix Pr -> Cf, namespace CalcFlow.

function New-CfExcel {
    param([switch]$Visible)
    try { $app = New-Object -ComObject Excel.Application }
    catch { throw "Excel is not available on this machine ($($_.Exception.Message))." }
    $xlPid = 0
    try { $xlPid = [CalcFlow.Win32]::ProcessIdOfWindow([long]$app.Hwnd) } catch { }
    $app.Visible = [bool]$Visible
    $app.DisplayAlerts = $false
    $app.ScreenUpdating = [bool]$Visible
    $app.EnableEvents = $false
    try { $app.AutomationSecurity = 3 } catch { }
    if (-not $Visible) { try { $app.Interactive = $false } catch { } }
    return [pscustomobject]@{ App = $app; Pid = $xlPid }
}

function Close-CfExcel {
    param($Excel)
    if ($null -eq $Excel) { return }
    try { $Excel.App.Quit() } catch { }
    try { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($Excel.App) } catch { }
    [GC]::Collect(); [GC]::WaitForPendingFinalizers(); [GC]::Collect()
    if ($Excel.Pid -gt 0) {
        $deadline = (Get-Date).AddSeconds(10)
        while ((Get-Date) -lt $deadline -and (Get-Process -Id $Excel.Pid -ErrorAction SilentlyContinue)) { Start-Sleep -Milliseconds 250 }
        Stop-Process -Id $Excel.Pid -Force -ErrorAction SilentlyContinue
    }
}

# Excel answers "busy" (RPC_E_CALL_REJECTED / RPC_E_SERVERCALL_RETRYLATER) while it is still loading: retry
function Invoke-CfCom {
    param([Parameter(Mandatory)][scriptblock]$Body, [int]$Tries = 20)
    for ($i = 1; ; $i++) {
        try { return & $Body }
        catch {
            $hr = 0
            if ($_.Exception -is [Runtime.InteropServices.COMException]) { $hr = $_.Exception.HResult }
            elseif ($_.Exception.InnerException -is [Runtime.InteropServices.COMException]) { $hr = $_.Exception.InnerException.HResult }
            if (($hr -eq -2147418111 -or $hr -eq -2147417846) -and $i -lt $Tries) { Start-Sleep -Milliseconds 500; continue }
            throw
        }
    }
}

function Open-CfWorkbook {
    param([Parameter(Mandatory)]$Excel, [Parameter(Mandatory)][string]$Path, [switch]$ReadOnly)
    $m = [Type]::Missing
    # Open(Filename, UpdateLinks, ReadOnly, Format, Password, WriteResPassword, IgnoreReadOnlyRecommended, Origin, Delimiter, Editable, Notify, Converter, AddToMru)
    $wb = Invoke-CfCom { $Excel.App.Workbooks.Open($Path, 0, [bool]$ReadOnly, $m, $m, $m, $true, $m, $m, $false, $false, $m, $false) }
    if (-not $ReadOnly -and $wb.ReadOnly) {
        $wb.Close($false)
        throw "$Path is open somewhere else (it opened read-only). Close it and run again."
    }
    return $wb
}

function Get-CfListObject {
    param([Parameter(Mandatory)]$Workbook, [Parameter(Mandatory)][string]$Name)
    foreach ($ws in $Workbook.Worksheets) { foreach ($lo in $ws.ListObjects) { if ($lo.Name -eq $Name) { return $lo } } }
    throw "Table '$Name' not found in $($Workbook.Name)."
}

function Get-CfColumnIndex {
    param([Parameter(Mandatory)]$ListObject)
    $idx = @{}
    $i = 0
    foreach ($lc in $ListObject.ListColumns) { $i++; $idx[[string]$lc.Name] = $i }
    return $idx
}

# Values of one table column as a 0-based object[] (Value2 returns a scalar for a one-cell range)
function Get-CfColumnValues {
    param([Parameter(Mandatory)]$ListObject, [Parameter(Mandatory)][string]$Column)
    $body = $ListObject.ListColumns.Item($Column).DataBodyRange
    if ($null -eq $body) { return , @() }
    $v = $body.Value2
    $n = [int]$body.Rows.Count
    $out = New-Object object[] $n
    if ($n -eq 1) { $out[0] = $v; return , $out }
    for ($i = 0; $i -lt $n; $i++) { $out[$i] = $v[($i + 1), 1] }
    return , $out
}

function Set-CfColumnValues {
    param([Parameter(Mandatory)]$ListObject, [Parameter(Mandatory)][string]$Column, [object[]]$Values, [string]$NumberFormat)
    $body = $ListObject.ListColumns.Item($Column).DataBodyRange
    if ($null -eq $body) { return }
    $n = [int]$body.Rows.Count
    $arr = New-Object 'object[,]' $n, 1
    for ($i = 0; $i -lt $n -and $i -lt $Values.Count; $i++) { $arr[$i, 0] = $Values[$i] }
    if ($NumberFormat) { $body.NumberFormat = $NumberFormat }
    $body.Value2 = $arr
}
