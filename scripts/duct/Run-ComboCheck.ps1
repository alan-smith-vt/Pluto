# Rebuild combo 18 (linear cases + SRSS of the seismic cases) from the load cases in forces.tsv and compare with SAP's rows.
# Windows PowerShell 5.1, no SAP needed: reads the forces.tsv that Run-DuctCheck.ps1 exported (it includes the load cases).
#   Set-ExecutionPolicy -Scope Process Bypass
#   .\Run-ComboCheck.ps1
# Paste the names from SAP (or from the forces file), exactly.

# ==================== EDIT HERE ====================
$ForcesTsv = "C:\Temp\ductcheck\sap\forces.tsv"
$Combo     = "18 BLC 7B"                                      # the SAP combo to reproduce (its Max / Min rows)
$Linear    = @("1 DEAD", "Steel_Loading")                     # linear terms, factor $LinearFactor each
$Srss      = @("3 Seismic X", "4 Seismic Z", "5 Seismic Y - vert")   # terms of the nested SRSS combo, factor 1 each
$LinearFactor = 1.0
# ===================================================

$ErrorActionPreference = "Stop"
Add-Type -Path (Join-Path $PSScriptRoot "DuctCombo.cs")
$ok = $false
[DuctCombo]::CheckLinearSrss((Resolve-Path $ForcesTsv).Path, $Combo, [string[]]$Linear, [string[]]$Srss, $LinearFactor, [ref]$ok)
if (-not $ok) { exit 1 }
