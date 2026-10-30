# The HVAC model in one command: Navisworks + Revit exports -> clean model (ducts, steel, supports) -> SAP model ->
# SAP model as a viewer file. Windows PowerShell 5.1.
#   powershell -ExecutionPolicy Bypass -File scripts\duct\Run-HvacModel.ps1 -Config <duct-config.psd1>
#       -Run <Navisworks ducts run> -Revit <Revit connectors run> [-Out <folder>]
#       [-From model|sap|view] [-To model|sap|view] [-Fragments] [-Other] [-NavisOnly]
# Steps (each in its own powershell.exe; log <Out>\logs\<n>-<step>.log, everything also in <Out>\run.log):
#   1 model  Export-DuctsViewer -Config -Run -Revit -Out <Out>\model\revit   (overlay, clean model, CSVs, match.txt)
#   2 sap    Run-SapDucts -Frames / -SteelFrames / -Supports from step 1 -SavePath <Out>\sap\hvac.sdb
#   3 view   Run-Sap -Open <Out>\sap\hvac.sdb -NoSaveSdb -Export <Out>\sap\hvac -NoResults -Looks sap-looks.csv
# Every setting comes from -Config (rooms, systems, cuts, drops, steel, WorkDir, AddinDir). -Out default: a new
# <WorkDir>\model\<yyyyMMdd-HHmmss>. SAP: each SAP step starts and closes its own SAP (it never attaches to a SAP
# you have open). A failed step stops the run (exit 1) with the command that resumes it
# (-Out <same> -From <step>); nothing earlier is deleted or overwritten.
param(
    [Parameter(Mandatory = $true)][string]$Config,
    [Parameter(Mandatory = $true)][string]$Run,
    [Parameter(Mandatory = $true)][string]$Revit,
    [string]$Out,
    [ValidateSet("model", "sap", "view")][string]$From = "model",
    [ValidateSet("model", "sap", "view")][string]$To = "view",
    [switch]$Fragments,
    [switch]$Other,
    [switch]$NavisOnly
)
$ErrorActionPreference = "Stop"
$StepMinutes = @{ model = 15; sap = 60; view = 60 }   # per-step time limits (a hosted SAP request has its own 15 min watchdog)
$scripts = Split-Path $PSScriptRoot
$ps = Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"
$steps = @("model", "sap", "view")
$i0 = [array]::IndexOf($steps, $From); $i1 = [array]::IndexOf($steps, $To)
if ($i0 -gt $i1) { throw "-From $From comes after -To $To" }

$Config = (Resolve-Path $Config).Path
$cfg = Import-PowerShellDataFile $Config
foreach ($p in @($Run, $Revit)) { if (-not (Test-Path $p -PathType Container)) { throw "not a folder: $p" } }
$Run = (Resolve-Path $Run).Path; $Revit = (Resolve-Path $Revit).Path
if (-not $Out -and $From -ne "model") { throw "-From $From needs -Out <the earlier run's folder>" }
if (-not $Out) {
    $work = $(if ($cfg.WorkDir) { $cfg.WorkDir } else { "C:\Temp\hvac" })
    $Out = Join-Path $work ("model\" + (Get-Date -Format "yyyyMMdd-HHmmss"))
}
$Out = [IO.Path]::GetFullPath($Out)
if ($From -eq "model" -and (Test-Path $Out) -and @(Get-ChildItem $Out -Force).Count -gt 0) {
    throw "-Out $Out is not empty: give a new folder, or resume in it with -From sap / -From view"
}
$null = New-Item -ItemType Directory -Force (Join-Path $Out "logs"), (Join-Path $Out "model"), (Join-Path $Out "sap")
$runLog = Join-Path $Out "run.log"
$base = Join-Path $Out "model\revit"
$sdb = Join-Path $Out "sap\hvac.sdb"

function Say([string]$s) { Write-Host $s; Add-Content -Path $runLog -Value $s -Encoding UTF8 }
function Quote([string]$a) { if ($a -match '[\s"]') { '"' + $a.Replace('"', '\"') + '"' } else { $a } }
$resume = "powershell -ExecutionPolicy Bypass -File " + (Quote $PSCommandPath) + " -Config " + (Quote $Config) + " -Run " + (Quote $Run) +
    " -Revit " + (Quote $Revit) + " -Out " + (Quote $Out) +
    $(if ($Fragments) { " -Fragments" }) + $(if ($Other) { " -Other" }) + $(if ($NavisOnly) { " -NavisOnly" })

# one step in its own process, time-limited; stops the run with the resume command on failure
function Step([int]$n, [string]$name, [string]$script, [string[]]$argList) {
    $log = Join-Path $Out ("logs\{0}-{1}.log" -f $n, $name)
    $line = "powershell -File " + (Quote $script) + " " + (($argList | ForEach-Object { Quote $_ }) -join " ")
    Say ("[{0:HH:mm:ss}] == {1} {2}" -f (Get-Date), $n, $name); Say ("   " + $line)
    $t0 = Get-Date
    $all = @("-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", $script) + $argList
    $pr = Start-Process $ps -ArgumentList (($all | ForEach-Object { '"' + $_ + '"' }) -join " ") -NoNewWindow -PassThru -RedirectStandardOutput $log -RedirectStandardError "$log.err"
    $null = $pr.Handle
    $done = $pr.WaitForExit($StepMinutes[$name] * 60000)
    if (-not $done) { & taskkill.exe /T /F /PID $pr.Id | Out-Null }
    else { $pr.WaitForExit() }
    if ((Test-Path "$log.err") -and (Get-Item "$log.err").Length -gt 0) { Get-Content "$log.err" | Add-Content $log } ; Remove-Item "$log.err" -ErrorAction SilentlyContinue
    Get-Content $log | Add-Content -Path $runLog -Encoding UTF8
    $secs = ((Get-Date) - $t0).TotalSeconds
    if (-not $done -or $pr.ExitCode -ne 0) {
        Say ("[FAILED] step {0} {1} after {2:0.0} s: {3}" -f $n, $name, $secs, $(if (-not $done) { "no end after $($StepMinutes[$name]) min, killed" } else { "exit " + $pr.ExitCode }))
        Say "   log: $log"; Get-Content $log -Tail 15 | ForEach-Object { Say ("   | " + $_) }
        Say "   resume after fixing: $resume -From $name"
        exit 1
    }
    Say ("   done in {0:0.0} s" -f $secs)
}

Say ("Run-HvacModel {0:yyyy-MM-dd HH:mm:ss}`n  config {1}`n  ducts run {2}`n  Revit run {3}`n  out {4}" -f (Get-Date), $Config, $Run, $Revit, $Out)
$t00 = Get-Date
if ($i0 -le 0 -and $i1 -ge 0) {
    Step 1 "model" (Join-Path $scripts "navis\Export-DuctsViewer.ps1") @("-Config", $Config, "-Run", $Run, "-Revit", $Revit, "-Out", $base)
    $m = Get-Content "$base.match.txt" -ErrorAction SilentlyContinue
    foreach ($l in @($m | Where-Object { $_ -match '^NEW (fit|supports|orientation)|^flags \(|^unsupported spans|^steel model' })) { Say ("   " + $l.Substring(0, [Math]::Min(200, $l.Length))) }
}
if ($i0 -le 1 -and $i1 -ge 1) {
    if (-not (Test-Path "$base.frames.csv")) { Say "[FAILED] step 2 sap: no $base.frames.csv (run step model first)"; exit 1 }
    $a = @("-Frames", "$base.frames.csv", "-SavePath", $sdb)
    if (Test-Path "$base.steel_frames.csv") { $a += @("-SteelFrames", "$base.steel_frames.csv") }
    if (Test-Path "$base.supports.csv") { $a += @("-Supports", "$base.supports.csv") }
    if ($Fragments) { $a += "-Fragments" }; if ($Other) { $a += "-Other" }; if ($NavisOnly) { $a += "-NavisOnly" }
    Step 2 "sap" (Join-Path $scripts "sap\Run-SapDucts.ps1") $a
    $sl = Get-Content (Join-Path $Out "logs\2-sap.log") -ErrorAction SilentlyContinue
    foreach ($l in @($sl | Where-Object { $_ -match '^NEW (kept|SAP model)' })) { Say ("   " + $l.Substring(0, [Math]::Min(200, $l.Length))) }
}
if ($i0 -le 2 -and $i1 -ge 2) {
    if (-not (Test-Path $sdb)) { Say "[FAILED] step 3 view: no $sdb (run step sap first)"; exit 1 }
    Step 3 "view" (Join-Path $scripts "sap\Run-Sap.ps1") (@("-Open", $sdb, "-NoSaveSdb", "-Export", (Join-Path $Out "sap\hvac"), "-NoResults",
        "-Looks", (Join-Path $PSScriptRoot "sap-looks.csv")))
}

$el = (Get-Date) - $t00
Say ("[done] {0}. Outputs in {1}" -f $(if ($el.TotalSeconds -lt 60) { "{0:0.0} s" -f $el.TotalSeconds } else { "{0}m {1:00}s" -f [int][Math]::Floor($el.TotalMinutes), $el.Seconds }), $Out)
if (Test-Path "$base.match.txt") { Say "  1. Read  $base.match.txt  (fit, supports, unsupported spans, flags)" }
if (Test-Path "${base}_clean.bin") { Say "  2. Viewer: Choose files -> ${base}_clean.bin + ${base}_clean.features.json  (clean model: ducts, steel, supports)" }
if (Test-Path (Join-Path $Out "sap\hvac.bin")) { Say ("  3. Viewer: Choose files -> {0} + {1}  (the SAP model as built)" -f (Join-Path $Out "sap\hvac.bin"), (Join-Path $Out "sap\hvac.features.json")) }
if (Test-Path $sdb) { Say "  4. SAP:    $sdb  (no loads yet; material PLACEHOLDER)" }
