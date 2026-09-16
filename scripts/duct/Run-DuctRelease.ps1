# Offline expansion-joint release (Woodbury) from a link-model export, and the HTML report. Windows PowerShell 5.1, no SAP.
#   .\Run-DuctRelease.ps1 -Release -LinkDir ...\link -OutDir ...\released -Dead "1 DEAD" -Steel Steel_Loading -Seismic "3 Seismic X,4 Seismic Z,5 Seismic Y - vert" -Combo18 "18 BLC 7B" -Vectors "1 DEAD,Steel_Loading,...,19 BLC 7C,20 BLC 7D" [-Candidates 12,34] [-ConnectedDir ...\connected] [-DirectDir ...\direct] [-CheckCombos "1 DEAD,18 BLC 7B,19 BLC 7C,20 BLC 7D"] [-Tolerance 1e-4]
#   .\Run-DuctRelease.ps1 -Report -OutDir ...\released -Title "..." [-DcrConnected ...\dcr-connected] [-DcrReleased ...] [-DcrDirect ...] [-Limit 1.0] [-Facts "a|b|c"]
# -Release writes OutDir\forces.tsv (corrected vectors + rebuilt combo 18), release.txt, compare-*.txt, and compare.bin (for -Report).
param(
    [switch]$Release,
    [switch]$Report,
    [string]$LinkDir,
    [string]$OutDir,
    [string]$Candidates,          # link or candidate joint names to release; default every link in links.tsv
    [string]$Vectors,
    [string]$Dead, [string]$Steel, [string]$Seismic, [string]$Combo18,
    [string]$ConnectedDir,        # forces.tsv of the untouched model: base (link) model vs connected check
    [string]$DirectDir,           # forces.tsv of the directly disconnected model: released vs direct check
    [string]$CheckCombos,         # output cases compared (default: the vectors + combo 18)
    [double]$Tolerance = 1e-4,    # released vs direct: max |diff| / peak per component
    [double]$BaseTolerance = 0.02,# link model vs connected
    [string]$Title = "Release check",
    [string]$Facts,               # '|'-separated lines for the report head
    [string]$DcrConnected, [string]$DcrReleased, [string]$DcrDirect,
    [double]$Limit = 1.0
)
$ErrorActionPreference = "Stop"
function Split-List([string]$s) { if (-not $s) { return @() }; return @($s -split "," | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }
Add-Type -Path (Join-Path $PSScriptRoot "DuctRelease.cs"), (Join-Path $PSScriptRoot "DuctReport.cs")
$null = New-Item -ItemType Directory -Force $OutDir
$OutDir = (Resolve-Path $OutDir).Path

if ($Release) {
    $vec = Split-List $Vectors; $seis = Split-List $Seismic; $set = Split-List $Candidates
    if ($vec.Count -eq 0) { throw "-Vectors is required" }
    Write-Host "reading $LinkDir ..."
    $setArg = $null; if ($set.Count -gt 0) { $setArg = [string[]]$set }
    $rel = [DuctRelease]::Load((Resolve-Path $LinkDir).Path, $setArg)   # only the set's unit pairs are read
    Write-Host ("  {0} station rows x {1} output cases, {2} link(s)" -f $rel.Base.Keys.Count, $rel.Base.CaseKeys.Count, $rel.Links.Count)
    $res = $rel.Release($setArg, [string[]]$vec, $Dead, $Steel, [string[]]$seis, $Combo18)
    Write-Host $res.Text
    [IO.File]::WriteAllText((Join-Path $OutDir "release.txt"), $res.Text)
    if ($res.Singular) { throw "singular release system: the set leaves a mechanism" }
    $res.Table.Write((Join-Path $OutDir "forces.tsv"), $res.OutKeys)
    Write-Host "  wrote $OutDir\forces.tsv"

    $check = Split-List $CheckCombos
    if ($check.Count -eq 0) { $check = $vec + @($Combo18) }
    $cmpBase = $null; $cmpDirect = $null
    if ($ConnectedDir) {
        $con = [ForceTable]::Read((Join-Path (Resolve-Path $ConnectedDir).Path "forces.tsv"))
        $cmpBase = [DuctRelease]::Diff("link model (stiff links, nothing released) vs connected model", $con, $rel.Base, [string[]]$check, $BaseTolerance)
        $t = [DuctRelease]::CompareText($cmpBase); Write-Host $t; [IO.File]::WriteAllText((Join-Path $OutDir "compare-base-vs-connected.txt"), $t)
    }
    if ($DirectDir) {
        $dir = [ForceTable]::Read((Join-Path (Resolve-Path $DirectDir).Path "forces.tsv"))
        $cmpDirect = [DuctRelease]::Diff("released by superposition vs direct SAP disconnect", $dir, $res.Table, [string[]]$check, $Tolerance)
        $t = [DuctRelease]::CompareText($cmpDirect); Write-Host $t; [IO.File]::WriteAllText((Join-Path $OutDir "compare-released-vs-direct.txt"), $t)
    }
    # Keep the comparison objects for -Report (same process types, so a CLIXML round trip is enough).
    @{ Base = $cmpBase; Direct = $cmpDirect; Text = $res.Text } | Export-Clixml -Depth 6 -Path (Join-Path $OutDir "compare.xml")
    if ($cmpDirect -and -not $cmpDirect.Ok) { Write-Host "RELEASE CHECK: released forces differ from the direct run" -ForegroundColor Red; exit 2 }
    if ($cmpDirect) { Write-Host "RELEASE CHECK OK" -ForegroundColor Green }
}

if ($Report) {
    $saved = Import-Clixml (Join-Path $OutDir "compare.xml")
    function Rehydrate($o) {
        if ($null -eq $o) { return $null }
        $c = New-Object DuctRelease+Compare
        $c.Title = $o.Title; $c.Rows = $o.Rows; $c.Tolerance = $o.Tolerance; $c.Ok = $o.Ok
        $c.MaxAbs = [double[]]$o.MaxAbs; $c.Peak = [double[]]$o.Peak; $c.MaxRelPeak = [double[]]$o.MaxRelPeak; $c.WorstAt = [string[]]$o.WorstAt
        foreach ($r in $o.PerCase) { $c.PerCase.Add([string[]]$r) }
        foreach ($p in $o.Points) { $c.Points.Add([double[]]$p) }
        return $c
    }
    $factList = @(); if ($Facts) { $factList = @($Facts -split "\|") }
    $dc = [DuctReport]::DcrEnvelope($(if ($DcrConnected) { Join-Path $DcrConnected "dcr-frames.tsv" } else { $null }))
    $dr = [DuctReport]::DcrEnvelope($(if ($DcrReleased) { Join-Path $DcrReleased "dcr-frames.tsv" } else { $null }))
    $dd = [DuctReport]::DcrEnvelope($(if ($DcrDirect) { Join-Path $DcrDirect "dcr-frames.tsv" } else { $null }))
    $html = Join-Path $OutDir "release-report.html"
    [DuctReport]::Write($html, $Title, [string[]]$factList, [string]$saved.Text, (Rehydrate $saved.Base), (Rehydrate $saved.Direct), $dc, $dr, $dd, $Limit)
    Write-Host "report: $html"
}
