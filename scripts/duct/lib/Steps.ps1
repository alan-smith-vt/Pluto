# Step runner for the duct workflows. Dot-source: . (Join-Path <scripts\duct> "lib\Steps.ps1")
# Full path of powershell.exe 5.1 for the per-step child runs.
function Get-Ps51 { return Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe" }
# Runs a script in its own powershell.exe with a timestamped banner and elapsed time; throws on failure.
function Invoke-Step([string]$title, [string[]]$argList) {
    $t0 = Get-Date
    Write-Host ("[{0:HH:mm:ss}] == {1}" -f $t0, $title) -ForegroundColor Cyan
    & (Get-Ps51) -NoProfile -NonInteractive -ExecutionPolicy Bypass -File @argList
    if ($LASTEXITCODE -ne 0) { throw "$title failed (exit $LASTEXITCODE)" }
    Write-Host ("[{0:HH:mm:ss}]    done in {1:0.0} s" -f (Get-Date), ((Get-Date) - $t0).TotalSeconds) -ForegroundColor DarkCyan
}