# RENAMED (2026-10-30): this command is now Run-JointOptimizer.ps1. Kept for one release; it prints the new command and runs it.
$new = Join-Path $PSScriptRoot "Run-JointOptimizer.ps1"
Write-Host ("Run-Optimizer.ps1 is renamed. Next time run:  powershell -ExecutionPolicy Bypass -File " + $new + " " + ($args -join " ")) -ForegroundColor Yellow
& $new @args
exit $LASTEXITCODE
