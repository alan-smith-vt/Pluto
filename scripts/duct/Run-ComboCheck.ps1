# Rebuild combo 18 (linear cases + SRSS of the seismic cases) from the load cases in forces.tsv and compare with SAP's rows.
# Windows PowerShell 5.1, no SAP needed: reads the forces.tsv that Run-DuctCheck.ps1 exported (it includes the load cases).
# Names come from duct-config.psd1 (Cases.Dead, Cases.Steel, Cases.Seismic, Cases.Combo18).
#   Set-ExecutionPolicy -Scope Process Bypass
#   .\Run-ComboCheck.ps1 [-Config X.psd1] [-ForcesTsv path]     (default: WorkDir\ductcheck\sap\forces.tsv)
param(
    [string]$Config,
    [string]$ForcesTsv
)
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "Read-DuctConfig.ps1")
$C = Read-DuctConfig $Config
if (-not $ForcesTsv) { $ForcesTsv = Join-Path $C.WorkDir "ductcheck\sap\forces.tsv" }
Add-Type -Path (Join-Path $PSScriptRoot "DuctCombo.cs")
$ok = $false
[DuctCombo]::CheckLinearSrss((Resolve-Path $ForcesTsv).Path, $C.Cases.Combo18, [string[]]@($C.Cases.Dead, $C.Cases.Steel), [string[]]@($C.Cases.Seismic), 1.0, [ref]$ok)
if (-not $ok) { exit 1 }
