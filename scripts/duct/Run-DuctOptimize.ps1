# Expansion-joint optimizer, offline steps (HVAC). Windows PowerShell 5.1, no SAP. Called by Run-Optimizer.ps1.
#   .\Run-DuctOptimize.ps1 -Select   -ConnectedDir ...\connected -Spec auto|"12,34"|list.txt [-MaxCandidates 20] -OutDir ...\opt
#   .\Run-DuctOptimize.ps1 -Optimize -LinkDir ...\link -TablesDir ...\tables -OutDir ...\opt -Vectors "..." -Dead "1 DEAD" -Steel Steel_Loading -Seismic "a,b,c"
#                          [-ConnectedDir ...\connected] [-MaxJoints 10] [-OnePerSpan false] [-Swap false] [-Removal false] [-MaxEvaluations 0] [-SingularPivot 1e-8] [-EndsOnly] [-ComboLS C] [-Limit 1.0]
#   .\Run-DuctOptimize.ps1 -Report   -OutDir ...\opt -Title "..." [-Facts "a|b"] [-ConnectedDir ...] [-DcrConnected dir] [-DcrBase dir] [-DcrFinal dir] [-DcrDirect dir] [-Limit 1.0]
# -Select writes OutDir\candidates-selected.tsv + selected.txt. -Optimize writes evaluations.tsv, steps.tsv, chosen.txt / chosen.tsv,
# candidates-used.tsv, envelope-base.tsv, envelope-final.tsv, optimize.txt. -Report writes OutDir\optimizer-report.html.
param(
    [switch]$Select,
    [switch]$Optimize,
    [switch]$Report,
    [string]$ConnectedDir,
    [string]$Spec = "auto",
    [int]$MaxCandidates = 0,
    [string]$LinkDir,
    [string]$TablesDir,
    [string]$OutDir,
    [string]$Vectors,
    [string]$Dead, [string]$Steel, [string]$Seismic,
    [int]$MaxJoints = 10,
    [string]$OnePerSpan = "true",   # true / false (a -File call passes strings)
    [string]$Swap = "true",
    [string]$Removal = "true",
    [int]$MaxEvaluations = 0,
    [int]$MaxSwapPasses = 3,
    [double]$SingularPivot = 1e-8,
    [switch]$EndsOnly,
    [string]$ComboLS = "C",
    [double]$Limit = 1.0,
    [string]$Title = "Expansion-joint optimizer",
    [string]$Facts,
    [string]$DcrConnected, [string]$DcrBase, [string]$DcrFinal, [string]$DcrDirect
)
$ErrorActionPreference = "Stop"
function Split-List([string]$s) { if (-not $s) { return @() }; return @($s -split "," | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }
function IsOn([string]$s) { return $s -match "^(1|true|yes)$" }
function Full([string]$p) { if ($p) { return [IO.Path]::GetFullPath($p) } else { return $null } }
if (-not ($Select -or $Optimize -or $Report)) { throw "Pass -Select, -Optimize or -Report." }
Add-Type -Path (Join-Path $PSScriptRoot "DuctDcr.cs"), (Join-Path $PSScriptRoot "DuctRelease.cs"), (Join-Path $PSScriptRoot "DuctReport.cs"), (Join-Path $PSScriptRoot "DuctOptimize.cs"), (Join-Path $PSScriptRoot "DuctOptReport.cs")
$null = New-Item -ItemType Directory -Force $OutDir
$OutDir = (Resolve-Path $OutDir).Path

if ($Select) {
    $msg = [DuctOptimize]::Select((Resolve-Path $ConnectedDir).Path, $Spec, $MaxCandidates, $OutDir)
    Write-Host $msg
}

if ($Optimize) {
    $vec = Split-List $Vectors; $seis = Split-List $Seismic
    if ($vec.Count -eq 0) { throw "-Vectors is required" }
    Write-Host "reading $LinkDir ..."
    $opt = [DuctOptimize]::Load((Resolve-Path $LinkDir).Path, (Full $ConnectedDir), (Resolve-Path $TablesDir).Path, (Join-Path $PSScriptRoot "duct-materials.csv"),
        [string[]]$vec, $Dead, $Steel, [string[]]$seis, [bool]$EndsOnly, $ComboLS, $Limit)
    $opt.SingularPivot = $SingularPivot
    Write-Host $opt.Info.ToString()
    $null = $opt.Run($MaxJoints, (IsOn $OnePerSpan), (IsOn $Swap), (IsOn $Removal), $MaxEvaluations, $MaxSwapPasses)
    $opt.WriteOutputs($OutDir)
    Write-Host "  wrote $OutDir\chosen.txt, evaluations.tsv, steps.tsv, envelope-*.tsv"
}

if ($Report) {
    $factList = @(); if ($Facts) { $factList = @($Facts -split "\|") }
    function DcrPath([string]$dir) { if ($dir) { return (Join-Path $dir "dcr-frames.tsv") } else { return $null } }
    $html = Join-Path $OutDir "optimizer-report.html"
    [DuctOptReport]::Write($html, $Title, [string[]]$factList, $OutDir, (Full $ConnectedDir), (DcrPath $DcrConnected), (DcrPath $DcrBase), (DcrPath $DcrFinal), (DcrPath $DcrDirect), $Limit)
    Write-Host "report: $html"
}
