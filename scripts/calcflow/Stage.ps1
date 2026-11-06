# Stage.ps1 -- runs the Excel work in a child Windows PowerShell 5.1 process (STA, as COM needs) and watches it.
# The child writes a heartbeat before every long COM call: the step it is on, a deadline, and the process id of the
# Excel instance it started. If the deadline passes, that one process is stopped (never an Excel the engineer has
# open), so a hidden dialog becomes an error message instead of a hang.
# Copied from pipe-review (src/Stage.ps1 at d4b33aa, 2026-11-06): prefix Pr -> Cf; the .xls conversion takes its
# work folder and timeout as parameters (pipe-review read them from its project config).

function Test-CfWindows { return [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT }

function Set-CfHeartbeat {
    param([string]$Path, [string]$Step, [int]$Seconds = 300, [int]$KillPid = 0)
    if (-not $Path) { return }
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds).Ticks
    [IO.File]::WriteAllText($Path, ("{0}|{1}|{2}" -f $deadline, $KillPid, $Step))
}

function Read-CfJson {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    return ([IO.File]::ReadAllText($Path) | ConvertFrom-Json)
}

function Write-CfJson {
    param([string]$Path, $Object)
    [IO.File]::WriteAllText($Path, ($Object | ConvertTo-Json -Depth 8))
}

function Invoke-CfStage {
    param(
        [Parameter(Mandatory)][string]$Name,          # stage script in scripts\calcflow\stages, without .ps1
        [Parameter(Mandatory)][hashtable]$Plan,
        [Parameter(Mandatory)][string]$WorkDir,
        [int]$TimeoutSeconds = 600
    )
    if (-not (Test-CfWindows)) { throw "The '$Name' stage drives Excel and needs Windows." }
    if (-not (Test-Path -LiteralPath $WorkDir)) { New-Item -ItemType Directory -Path $WorkDir | Out-Null }
    $script = Join-Path (Join-Path $PSScriptRoot 'stages') "$Name.ps1"
    $planFile = Join-Path $WorkDir "$Name-plan.json"
    $Plan.Heartbeat = Join-Path $WorkDir "$Name.heartbeat"
    $Plan.ResultFile = Join-Path $WorkDir "$Name-result.json"
    foreach ($f in $Plan.Heartbeat, $Plan.ResultFile) { if (Test-Path -LiteralPath $f) { Remove-Item -LiteralPath $f -Force } }
    Write-CfJson -Path $planFile -Object $Plan

    $exe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.Arguments = '-NoProfile -NonInteractive -STA -ExecutionPolicy Bypass -File "{0}" -Plan "{1}"' -f $script, $planFile
    $psi.UseShellExecute = $false
    $p = [System.Diagnostics.Process]::Start($psi)
    $null = $p.Handle
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds).Ticks
    $step = 'starting'; $killPid = 0; $killed = $false
    while (-not $p.WaitForExit(2000)) {
        if (Test-Path -LiteralPath $Plan.Heartbeat) {
            try {
                $hb = [IO.File]::ReadAllText($Plan.Heartbeat).Split('|')
                if ($hb.Count -ge 3) { $deadline = [long]$hb[0]; $killPid = [int]$hb[1]; $step = $hb[2] }
            }
            catch { }
        }
        if ([DateTime]::UtcNow.Ticks -gt $deadline) {
            Write-Host ("        no response while {0}: stopping it" -f $step) -ForegroundColor Red
            if ($killPid -gt 0) { Stop-Process -Id $killPid -Force -ErrorAction SilentlyContinue }
            $killed = $true
            if (-not $p.WaitForExit(30000)) { try { $p.Kill() } catch { } }
            break
        }
    }
    $p.WaitForExit()
    $result = Read-CfJson -Path $Plan.ResultFile
    return [pscustomobject]@{ ExitCode = $p.ExitCode; Killed = $killed; Step = $step; Result = $result }
}

# .xls / .xlsb -> .xlsx through Excel (a template or register saved in an old format)
function Invoke-CfConvertToXlsx {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Target,
        [Parameter(Mandatory)][string]$WorkDir,
        [int]$TimeoutSeconds = 300
    )
    $r = Invoke-CfStage -Name 'Convert-Workbook' -Plan @{ Source = $Source; Target = $Target } -WorkDir $WorkDir -TimeoutSeconds $TimeoutSeconds
    if ($r.Killed) { throw "Excel did not respond while $($r.Step) converting $Source. Open it in Excel and save it as .xlsx instead." }
    if ($r.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $Target)) {
        $msg = if ($r.Result) { $r.Result.Message } else { "exit code $($r.ExitCode)" }
        throw "Could not convert $Source to .xlsx through Excel: $msg. Save it as .xlsx instead."
    }
}
