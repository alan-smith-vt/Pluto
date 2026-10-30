# Rebuild combo 18 (linear cases + SRSS of the seismic cases) from the load cases in forces.tsv and compare with SAP's rows.
# Windows PowerShell 5.1, no SAP needed: reads the forces.tsv that Run-DuctCheck.ps1 exported (it includes the load cases).
# Names come from duct-config.psd1 (Cases.Dead, Cases.Steel, Cases.Seismic, Cases.Combo18).
#   Set-ExecutionPolicy -Scope Process Bypass
#   .\steps\Run-ComboCheck.ps1 [-Config X.psd1] [-ForcesTsv path]     (default: WorkDir\ductcheck\sap\forces.tsv)
param(
    [string]$Config,
    [string]$ForcesTsv
)
$ErrorActionPreference = "Stop"
. (Join-Path (Split-Path $PSScriptRoot) "lib\DuctConfig.ps1")
$C = Read-DuctConfig $Config
if (-not $ForcesTsv) { $ForcesTsv = Join-Path $C.WorkDir "ductcheck\sap\forces.tsv" }
$duct = Split-Path $PSScriptRoot   # scripts\duct: the C# sources and tables of the step
Add-Type -Path (Join-Path $duct "DuctCombo.cs")
$ok = $false
[DuctCombo]::CheckLinearSrss((Resolve-Path $ForcesTsv).Path, $C.Cases.Combo18, [string[]]@($C.Cases.Dead, $C.Cases.Steel), [string[]]@($C.Cases.Seismic), 1.0, [ref]$ok)
if (-not $ok) { exit 1 }
