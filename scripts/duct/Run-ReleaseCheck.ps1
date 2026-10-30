# RENAMED (2026-10-30): this command is now Run-JointReleaseCheck.ps1. Kept for one release; it prints the new command and runs it.
$new = Join-Path $PSScriptRoot "Run-JointReleaseCheck.ps1"
# arguments as typed, quoted where they hold spaces or commas, so the printed line can be copied
$shown = ($args | ForEach-Object { $a = "$_"; if ($a -eq "" -or $a -match "[\s,;]") { "'" + ($a -replace "'", "''") + "'" } else { $a } }) -join " "
Write-Host ("Run-ReleaseCheck.ps1 is renamed. Next time run:  powershell -ExecutionPolicy Bypass -File `"" + $new + "`" " + $shown) -ForegroundColor Yellow
& $new @args
exit $LASTEXITCODE
