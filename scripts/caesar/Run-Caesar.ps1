# The CAESAR II runner: a CAESAR II neutral file (.cii) -> a Pluto viewer model, <base>.bin + <base>.features.json,
# through CaesarToPluto (scripts\arms\CaesarToPluto.cs: CaesarNeutralReader + CaesarGeometry + RawViewerWriter +
# FeaturesSidecar). Windows PowerShell 5.1; no Python, no CAESAR II needed. Steps, in this order:
#
#   check    -Neutral must exist (-Results too, when given); the output base must lie OUTSIDE this repo -- project
#            outputs never land in Pluto, so an output inside it (the default beside a .cii kept in the repo
#            included) is refused before anything loads
#   load     the C# batch: scripts\lib\Config.ps1 (one Add-Type of scripts\lib + scripts\arms)
#   export   CaesarToPluto, GEOMETRY ONLY: the whole pipe (bends as chords, reducers and valve bow-ties tapered,
#            display sizes for flanges / rigids / expansion joints) + sidecar groups (pipe sizes, components,
#            line numbers, SIF / tees, one node group per restraint combination). A previous <base>.features.json
#            is merged: colour / visibility edits, hand-made groups and section cuts survive a re-export.
#   results  -Results <xlsx|csv>: NOT read yet (next stage) -- a warning; the export stays geometry-only
#
# -Out <base>: writes <base>.bin + <base>.features.json. Default: beside the .cii, same name with spaces ->
#   underscores. An existing folder, or any path ending in \ or /, means <folder>\<name>; a trailing .bin is dropped.
# -ModelId: id written to the binary and the sidecar (default caesar/<name>).
# -ArcStepDeg: bend chord step in degrees (default 7.5, kept within 0.5 .. 45).
#
# Every line of progress starts with "[step]"; the last line is "RESULT {json}" (machine-readable: bin, features,
# counts, warnings). Open the .bin TOGETHER with the .features.json in the viewer (drop both files, or
# viewer\index.html?bin=<url>&features=<url>): the groups and support markers live in the sidecar.
#   powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File scripts\caesar\Run-Caesar.ps1 -Neutral "<project dir>\job.cii" -Out "<project dir>\viewer\job"
param(
    [Parameter(Mandatory = $true)][string]$Neutral,
    [string]$Results,
    [string]$Out,
    [string]$ModelId,
    [double]$ArcStepDeg = 7.5
)
$ErrorActionPreference = "Stop"
function Say([string]$step, [string]$text) { Write-Output ("[{0}] {1}" -f $step.PadRight(7), $text) }

# ---- check: arguments and the output location, before the C# batch loads ----
if (-not (Test-Path -LiteralPath $Neutral -PathType Leaf)) { throw "Run-Caesar: neutral file not found: $Neutral" }
$ciiPath = (Resolve-Path -LiteralPath $Neutral).ProviderPath
$resultsPath = $null
if ($Results) {
    if (-not (Test-Path -LiteralPath $Results -PathType Leaf)) { throw "Run-Caesar: results file not found: $Results" }
    $resultsPath = (Resolve-Path -LiteralPath $Results).ProviderPath
}
$jobName = [IO.Path]::GetFileNameWithoutExtension($ciiPath) -replace ' ', '_'
if ($Out) {
    $outBase = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Out)
    # a folder: one that exists, or any -Out ending in a separator (a new folder, created below)
    if (($Out -match '[\\/]$') -or (Test-Path -LiteralPath $outBase -PathType Container)) { $outBase = [IO.Path]::Combine($outBase, $jobName) }
    elseif ($outBase -match '\.bin$') { $outBase = $outBase.Substring(0, $outBase.Length - 4) }
} else {
    $outBase = [IO.Path]::Combine([IO.Path]::GetDirectoryName($ciiPath), $jobName)
}
$outBase = [IO.Path]::GetFullPath($outBase)
$repoRoot = (Resolve-Path -LiteralPath (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))).ProviderPath.TrimEnd('\', '/')
$inRepo = $outBase.Equals($repoRoot, [StringComparison]::OrdinalIgnoreCase) -or $outBase.StartsWith($repoRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
if ($inRepo) {
    throw ("Run-Caesar: refusing to write {0}.bin inside the Pluto repo ({1}): project outputs never land in Pluto. Pass -Out <project dir>\<name>." -f $outBase, $repoRoot)
}
if (-not $ModelId) { $ModelId = "caesar/" + $jobName }
$outDir = [IO.Path]::GetDirectoryName($outBase)
if ($outDir) { $null = [IO.Directory]::CreateDirectory($outDir) }   # literal path ([ ] are not wildcards here)
Say "check" ("{0} -> {1}.bin + .features.json (model id {2})" -f $ciiPath, $outBase, $ModelId)
if ($resultsPath) { Say "results" ("{0}: NOT read yet (next stage); the export is geometry only" -f $resultsPath) }

# ---- load: the C# batch ----
$sw = [Diagnostics.Stopwatch]::StartNew()
. (Join-Path (Join-Path (Split-Path -Parent $PSScriptRoot) "lib") "Config.ps1")
Say "load" ("scripts\lib + scripts\arms compiled ({0:0.0}s)" -f $sw.Elapsed.TotalSeconds)

# ---- export ----
$sw.Reset(); $sw.Start()
$opt = New-Object CaesarOptions
$opt.ArcStepDeg = $ArcStepDeg
$r = [CaesarToPluto]::Export($ciiPath, $resultsPath, $outBase, $ModelId, $opt)
foreach ($line in ($r.Summary() -split "`r?`n")) { if ($line.Trim()) { Say "export" $line } }
Say "export" ("done ({0:0.0}s)" -f $sw.Elapsed.TotalSeconds)
foreach ($w in $r.Warnings) { Say "warn" $w }
Say "open" ("{0} TOGETHER with {1} in the viewer (drop both, or index.html?bin=<url>&features=<url>): the groups and support markers live in the .features.json" -f $r.BinPath, $r.SidecarPath)
Write-Output ("RESULT " + $r.ToJson())
