<#
.SYNOPSIS
  STAAD.Pro output file (.anl) of a frame model -> CalcFlow's analysis package (CSV files + manifest.json).

.DESCRIPTION
  Reads the input echo (joints, members, sections, BETA, releases, offsets, truss / tension / cable members,
  supports, load cases, repeat loads and combinations) and every PRINT MEMBER FORCES table of the .anl, works out
  STAAD's member local axes and the joint index (the member ends that meet at each joint), and writes into -Out:
    manifest.json   source + SHA-256, units, counts, analysis, every problem
    nodes.csv  members.csv  supports.csv  loadcases.csv  endforces.csv  ends.csv  joints.csv
  Geometry is in inches; end forces are as STAAD printed them (member local axes, STAAD's sign, the units in the
  endforces.csv header). Exact or refused: an input the reader cannot hold exactly (SET Z UP, generated joints,
  a list it cannot expand, force rows it cannot place, a print without units) is an error, and no package is written.
  Windows PowerShell 5.1 or PowerShell 7; no STAAD and no Excel needed. vault/calcflow/calcflow-staad-frame-reader.md.
  The model file needs PRINT MEMBER FORCES ALL after its analysis.
  Development only: $env:PLUTO_DLL names a prebuilt assembly of scripts\lib + scripts\arms instead of compiling it.

  Exit code 0 = package written; 1 = the model has errors (each listed, nothing written); 2 = bad arguments.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\calcflow\Export-AnlFrame.ps1 -Anl "<project dir>\staad\MODEL-A\R03\MODEL-A.anl" -Out "<project dir>\calcflow\package\MODEL-A\R03"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Anl,
    [Parameter(Mandatory)][string]$Out
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Import-CalcFlow.ps1')

if (-not (Test-Path -LiteralPath $Anl -PathType Leaf)) { Write-Host "No such file: $Anl" -ForegroundColor Red; exit 2 }
$anlPath = (Resolve-Path -LiteralPath $Anl).Path
$outPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Out)

# a package is project data: never inside the repo (the tests' own output folder excepted, it is gitignored)
$repo = (Resolve-Path -LiteralPath (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path.TrimEnd('\', '/')
$testsOut = Join-Path (Join-Path $PSScriptRoot 'tests') 'out'
$under = { param($path, $root) $path.StartsWith($root.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or $path -eq $root }
if ((& $under $outPath $repo) -and -not (& $under $outPath $testsOut)) {
    Write-Host "The package is project data: write it outside the repo, e.g. <project dir>\calcflow\package\<model>\<rev> (got $outPath)." -ForegroundColor Red
    exit 2
}

Import-PlutoLib
$sw = [Diagnostics.Stopwatch]::StartNew()
$m = [AnlFrameReader]::Read($anlPath)
Write-Host ''
Write-Host ("read     {0} ({1:0.0} s)" -f $anlPath, $sw.Elapsed.TotalSeconds)
Write-Host ("model    {0} joints, {1} members, {2} supports, {3} load cases, {4} member end forces" -f $m.Nodes.Count, $m.Members.Count, $m.Supports.Count, $m.LoadCases.Count, $m.EndForces.Count)
$units = if ($m.ForceUnitsKnown) { "{0}, {1}" -f $m.ForceUnitText, $m.MomentUnitText } else { 'not found' }
Write-Host ("units    geometry in, end forces {0} (member local axes, STAAD's sign, as printed)" -f $units)
if ($m.Analysis) { Write-Host ("analysis {0}" -f $m.Analysis) }
foreach ($p in $m.Problems) {
    if ($p.Severity -eq 'Error') { Write-Host ("[error] {0}: {1}" -f $p.Code, $p.Message) -ForegroundColor Red }
    else { Write-Host ("[warn ] {0}: {1}" -f $p.Code, $p.Message) -ForegroundColor Yellow }
}
if ($m.ErrorCount -gt 0) {
    Write-Host ("No package written: {0} error(s). Fix the model (or the .anl's print commands) and run again." -f $m.ErrorCount) -ForegroundColor Red
    exit 1
}
$files = [FramePackageWriter]::Write($m, $outPath)
Write-Host ("package  {0}" -f $outPath) -ForegroundColor Green
foreach ($f in $files) { Write-Host ("         {0}" -f [IO.Path]::GetFileName($f)) }
exit 0
