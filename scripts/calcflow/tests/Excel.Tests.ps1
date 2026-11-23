# Excel.Tests.ps1 -- the fixture project calculated by Excel (COM, through the Recalc-Workbooks stage, a Windows
# PowerShell 5.1 child): the same DCRs as LibreOffice gives (Engine.Tests.ps1). Runs only on Windows with Excel
# installed; skipped elsewhere.

Test 'Excel calculates the fixture project to the same DCRs as LibreOffice' {
    $excel = (Test-CfWindows) -and ($null -ne [Type]::GetTypeFromProgID('Excel.Application'))
    if (-not $excel) { Write-Host '        (no Excel here: skipped)' -ForegroundColor DarkGray; return }
    Test-CfEngine 'excel' 'Excel' 1e-6
}
