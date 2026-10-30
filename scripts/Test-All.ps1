# Every test suite in the repo, one command. Windows PowerShell 5.1 (+ node for the viewer, python for pythonTools).
#   powershell -ExecutionPolicy Bypass -File scripts\Test-All.ps1 [-Sap] [-Only <name pattern>] [-Log <dir>] [-TimeoutMin 10]
# Each suite runs in its own powershell.exe 5.1 / node / python process (a fresh Add-Type session each), its output
# goes to <Log>\<suite>.log, and a table of suite / result / seconds is printed. Exit 1 if any suite failed.
# -Sap adds the suites that drive SAP2000 (Test-SapSteel, Test-Demo with the SAP build); without it Test-Demo runs
# with -NoSap. Run SAP suites one at a time per machine (they attach to a running SAP).
# A new suite: add one line to $suites below (name, kind, path, args, needs SAP).
param([switch]$Sap, [string]$Only, [string]$Log = (Join-Path $env:TEMP "pluto-test-all"), [int]$TimeoutMin = 10)
$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot
$ps = Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"
$demoRoot = Join-Path $Log "demo"

# name, kind (ps / node / pytest), path (repo-relative), extra args, needs SAP
$suites = @(
    @("viewer",          "node",   "viewer\tests",                                   @(),                     $false),
    @("core-import",     "ps",     "scripts\lib\tests\Test-PlutoImport.ps1",         @(),                     $false),
    @("sidecar-look",    "ps",     "scripts\lib\sidecar\Test-FeaturesSidecarLook.ps1", @(),                   $false),
    @("steel-model",     "ps",     "scripts\arms\tests\Test-SteelModel.ps1",         @(),                     $false),
    @("duct-orient",     "ps",     "scripts\duct\tests\Test-DuctOrient.ps1",         @(),                     $false),
    @("duct-supports",   "ps",     "scripts\duct\tests\Test-DuctSupports.ps1",       @(),                     $false),
    @("duct-bridge",     "ps",     "scripts\duct\tests\Test-NavBridge.ps1",          @(),                     $false),
    @("duct-settings",   "ps",     "scripts\duct\tests\Test-DuctSettings.ps1",       @(),                     $false),
    @("duct-dcr-chain",  "ps",     "scripts\duct\tests\Test-DuctDcrChain.ps1",   @(),                     $false),
    @("duct-fixture",    "ps",     "scripts\duct\tests\Test-DuctFixture.ps1",        @(),                     $false),
    @("demo",            "ps",     "scripts\duct\tests\Test-Demo.ps1",               @("-Root", $demoRoot, "-NoSap"), $false),
    @("demo-sap",        "ps",     "scripts\duct\tests\Test-Demo.ps1",               @("-Root", $demoRoot),   $true),
    @("sap-steel",       "ps",     "scripts\sap\Test-SapSteel.ps1",                  @(),                     $true),
    @("tank-builder",    "pytest", "pythonTools\sap",                                @(),                     $false)
)

$null = New-Item -ItemType Directory -Force $Log
$rows = @(); $failed = 0
# a Test-*.ps1 in the repo that is not in the list above is reported, so a new suite is not silently skipped
$listed = @($suites | ForEach-Object { [IO.Path]::GetFileName($_[2]) })
foreach ($f in Get-ChildItem (Join-Path $repo "scripts") -Recurse -Filter "Test-*.ps1" | Where-Object { $_.Name -ne "Test-All.ps1" -and $listed -notcontains $_.Name }) {
    Write-Host ("  not in Test-All's list: {0}" -f $f.FullName.Substring($repo.Length + 1)) -ForegroundColor Yellow
    $rows += [pscustomobject]@{ Suite = $f.BaseName; Result = "NOT LISTED (add it to Test-All.ps1)"; Seconds = "" }
}
foreach ($s in $suites) {
    $name, $kind, $path, $extra, $needsSap = $s
    if ($Only -and $name -notlike $Only) { continue }
    if ($needsSap -and -not $Sap) { $rows += [pscustomobject]@{ Suite = $name; Result = "skipped (-Sap)"; Seconds = "" }; continue }
    if ($name -eq "demo" -and $Sap) { continue }   # demo-sap covers it
    $full = Join-Path $repo $path; $out = Join-Path $Log "$name.log"
    $t0 = Get-Date; $ok = $true; $note = ""
    Write-Host ("[{0:HH:mm:ss}] {1}" -f $t0, $name) -ForegroundColor Cyan
    try {
        switch ($kind) {
            "ps" {
                # own process with a time limit: a powershell.exe child was seen to stall at start-up (0 CPU) now and then;
                # it is killed with its tree and reported as HANG instead of blocking the run
                $argStr = (@("-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", $full) + $extra | ForEach-Object { '"' + $_ + '"' }) -join " "
                for ($try = 1; $try -le 2; $try++) {   # a hang is retried once
                    $pr = Start-Process $ps -ArgumentList $argStr -NoNewWindow -PassThru -RedirectStandardOutput $out -RedirectStandardError "$out.err"
                    $null = $pr.Handle   # PS 5.1: without an open handle ExitCode reads null once the process is gone
                    if ($pr.WaitForExit($TimeoutMin * 60000)) { $pr.WaitForExit(); $ok = $pr.ExitCode -eq 0; $note = $(if ($try -gt 1) { "(after a hang) " } else { "" }); break }
                    & taskkill.exe /T /F /PID $pr.Id | Out-Null
                    $ok = $false; $note = "HANG (killed after $TimeoutMin min, twice)"
                    Write-Host "  $name hung; killed$(if ($try -eq 1) { ', retrying' })" -ForegroundColor Yellow
                }
                if ((Test-Path "$out.err") -and (Get-Item "$out.err").Length -eq 0) { Remove-Item "$out.err" }
            }
            "node" {
                if (-not (Get-Command node -ErrorAction SilentlyContinue)) { $ok = $false; $note = "node not found" ; break }
                $lines = @()
                foreach ($f in Get-ChildItem $full -Filter "test_*.js" | Sort-Object Name) {
                    $lines += "== $($f.Name)"; $lines += @(& node $f.FullName 2>&1 | ForEach-Object { "$_" })
                    if ($LASTEXITCODE -ne 0) { $ok = $false; $note += $f.BaseName + " " }
                }
                $lines | Out-File $out -Encoding utf8
            }
            "pytest" {
                Push-Location $full
                try { & python -m pytest -q *> $out; $ok = $LASTEXITCODE -eq 0 } finally { Pop-Location }
            }
        }
    } catch { $ok = $false; $note = $_.Exception.Message }
    if (-not $ok) { $failed++ }
    $rows += [pscustomobject]@{ Suite = $name; Result = $(if ($ok) { ("pass " + $note).Trim() } else { "FAIL " + $note }); Seconds = [int]((Get-Date) - $t0).TotalSeconds }
}
$rows | Format-Table -AutoSize | Out-String -Width 200 | Write-Output
Write-Output ("{0} suite(s) failed; logs in {1}" -f $failed, $Log)
if ($failed) { exit 1 }
