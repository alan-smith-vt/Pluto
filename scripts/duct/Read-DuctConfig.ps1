# Dot-source to load the duct config: . .\Read-DuctConfig.ps1 ; $C = Read-DuctConfig [-Path X]
# Looks for duct-config.psd1 beside the scripts (or -Path); explains what to do if it is missing.
function Read-DuctConfig([string]$Path) {
    if (-not $Path) { $Path = Join-Path $PSScriptRoot "duct-config.psd1" }
    if (-not (Test-Path $Path)) {
        throw "No config at $Path. Copy duct-config.example.psd1 to duct-config.psd1 beside the scripts and edit it."
    }
    $c = Import-PowerShellDataFile -Path (Resolve-Path $Path).Path
    foreach ($k in "WorkDir", "Group", "Cases", "Dcr", "Release") { if (-not $c.ContainsKey($k)) { throw "config $Path has no '$k' entry" } }
    foreach ($k in "Dead", "Steel", "Seismic", "Combo18", "Combo19", "Combo20") { if (-not $c.Cases.ContainsKey($k)) { throw "config Cases has no '$k'" } }
    if (-not $c.ContainsKey("SapDir")) { $c.SapDir = "" }
    # Optimize block: older configs have none; every key has a default.
    if (-not $c.ContainsKey("Optimize")) { $c.Optimize = @{} }
    $defaults = @{ Candidates = "auto"; MaxCandidates = 20; MaxJoints = 10; OnePerSpan = $true; Swap = $true; Removal = $true; MaxEvaluations = 0; SingularPivot = 1e-8 }
    foreach ($k in $defaults.Keys) { if (-not $c.Optimize.ContainsKey($k)) { $c.Optimize[$k] = $defaults[$k] } }
    $c.Path = (Resolve-Path $Path).Path
    return $c
}
# Names the DCR check scores: dead, 18, 19, 20.
function Get-CheckCombos($c) { return @($c.Cases.Dead, $c.Cases.Combo18, $c.Cases.Combo19, $c.Cases.Combo20) }
# Load vectors that superpose linearly: corrected one by one after a release. 18 is rebuilt from the first five.
function Get-LoadVectors($c) { return @($c.Cases.Dead, $c.Cases.Steel) + @($c.Cases.Seismic) + @($c.Cases.Combo19, $c.Cases.Combo20) }
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