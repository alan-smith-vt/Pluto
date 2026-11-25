# DuctMaterials (carbon / stainless per system, scripts\duct\model\DuctMaterials.cs) and the DuctMaterials setting.
# Windows PowerShell 5.1, a few seconds:
#   powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File scripts\duct\tests\Test-DuctMaterials.ps1
$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'arms\Import-Arms.ps1')
. (Join-Path (Split-Path $PSScriptRoot -Parent) 'Get-DuctSettings.ps1')
$fail = 0; $pass = 0
function Check($name, $cond) { if ($cond) { $script:pass++ } else { Write-Host "FAIL $name"; $script:fail++ } }
function Edge($sys, [switch]$Rigid) {
    $e = New-Object CEdge; $e.A = 1; $e.B = 2; $e.Rigid = [bool]$Rigid
    if (-not $Rigid) { $el = New-Object RvEl; $el.Id = "1"; $el.SysName = $sys; $e.Src.Add($el) }
    return $e
}
function List($edges) { $l = New-Object 'System.Collections.Generic.List[CEdge]'; foreach ($e in $edges) { $l.Add($e) }; return ,$l }
$notes = New-Object 'System.Collections.Generic.List[string]'

# 1 per system: stainless, carbon, fragments and other: match their run, an unlisted system takes the default + flag
$d = Get-DuctSettings -Data @{ DuctMaterials = @{ SYSA = 'Stainless'; SYSB = 'carbon'; SYSZ = 'carbon' } } -Mode Revit -Notes $notes
$c = List @((Edge "SYSA"), (Edge "SYSA fragments"), (Edge "other: SYSB"), (Edge "SYSC"), (Edge "" -Rigid))
$note = [DuctMaterials]::Apply($c, $d)
Check "1 materials by run" (($c | ForEach-Object { $_.Material }) -join "," -eq "stainless,stainless,carbon,carbon,")
Check "1 unlisted system: default carbon, flag MATERIAL_DEFAULT; listed ones not flagged" ($c[3].Flags.Contains("MATERIAL_DEFAULT") -and -not $c[0].Flags.Contains("MATERIAL_DEFAULT"))
Check "1 note: counts, unlisted, unused key" ($note -eq "materials (DuctMaterials): carbon 2, stainless 2; systems not listed, carbon (MATERIAL_DEFAULT): SYSC (1); listed but matching no frame: SYSZ")

# 2 not configured: everything the default, nothing flagged
$d = Get-DuctSettings -Data @{} -Mode Revit -Notes $notes
$c = List @((Edge "SYSA"))
$note = [DuctMaterials]::Apply($c, $d)
Check "2 no DuctMaterials: carbon, no flag" ($c[0].Material -eq "carbon" -and -not $c[0].Flags.Contains("MATERIAL_DEFAULT") -and $note -like "*DuctMaterials not set: every duct carbon*")

# 3 default stainless; a bad value refused
$d = Get-DuctSettings -Data @{ DuctMaterialDefault = 'stainless' } -Mode Revit -Notes $notes
$c = List @((Edge "SYSA")); $null = [DuctMaterials]::Apply($c, $d)
Check "3 DuctMaterialDefault stainless" ($c[0].Material -eq "stainless")
$refused = $false; try { $null = Get-DuctSettings -Data @{ DuctMaterials = @{ SYSA = 'aluminium' } } -Mode Revit -Notes $notes } catch { $refused = $_.Exception.Message -like "*carbon or stainless*" }
Check "3 a value other than carbon / stainless is refused" $refused

Write-Host ("{0} passed, {1} failed" -f $pass, $fail)
exit $(if ($fail) { 1 } else { 0 })
