# The pipe builder runner: a TOML config -> a CAESAR II neutral file (.cii) to import in CAESAR II, through
# PipeBuilder (scripts\arms\PipeBuilder.cs: TOML -> PipeModel) and CaesarNeutralWriter (PipeModel -> .cii).
# Windows PowerShell 5.1; no Python, no CAESAR II needed. Steps, in this order:
#
#   check    -Config must exist; the output base must lie OUTSIDE this repo -- project outputs never land in Pluto,
#            so an output inside it (the default beside a config kept in the repo included) is refused before
#            anything loads
#   load     the C# batch: scripts\lib\Config.ps1 (one Add-Type of scripts\lib + scripts\arms)
#   build    PipeBuilder: sections, runs (legs, bends, branches with welding tees), in-line components (valves,
#            flanges, rigids, reducers, expansion joints, couplings), supports by type or by DOF (gaps,
#            stiffness, friction, skewed directions, CNODEs), node-to-node ties, SIFs; temperatures (or delta T
#            from the ambient) and pressures per run. An unknown key is an error; every message names the config
#            item it comes from. Every key: scripts\caesar\examples\pipe-example.toml; usage: vault/arms/pipe-builder.md
#   write    <base>.cii (CAESAR II 15.01 neutral file; import it in CAESAR II) and <base>.nodes.csv (every node: run,
#            position along it, x y z, what is there)
#   preview  -Preview: <base>.bin + <base>.features.json through CaesarToPluto (what Run-Caesar.ps1 makes of the
#            written .cii): the model in the viewer, support symbols included, before CAESAR sees it
#
# -Out <base>: default beside the config, same name with spaces -> underscores. An existing folder, or any path
#   ending in \ or /, means <folder>\<config name>; a trailing .cii is dropped. Existing outputs are overwritten.
# -Set "name=value;name=value": overrides [parameters] values (numbers or expressions over the others).
# -ModelId: the viewer model id with -Preview (default pipe/<name>).
#
# Every line of progress starts with "[step]"; the last line is "RESULT {json}" (machine-readable: paths, counts,
# warnings; {"error": "..."} when the config does not build, with exit code 1).
#   powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File scripts\caesar\Build-Pipe.ps1 -Config "<project dir>\line.toml" -Out "<project dir>\line" -Preview
param(
    [Parameter(Mandatory = $true)][string]$Config,
    [string]$Out,
    [string]$Set,
    [switch]$Preview,
    [string]$ModelId
)
$ErrorActionPreference = "Stop"
function Say([string]$step, [string]$text) { Write-Output ("[{0}] {1}" -f $step.PadRight(7), $text) }
function Q([string]$s) { return (ConvertTo-Json -InputObject $s -Compress) }

# ---- check: arguments and the output location, before the C# batch loads ----
if (-not (Test-Path -LiteralPath $Config -PathType Leaf)) { throw "Build-Pipe: config not found: $Config" }
$cfgPath = (Resolve-Path -LiteralPath $Config).ProviderPath
$jobName = [IO.Path]::GetFileNameWithoutExtension($cfgPath) -replace ' ', '_'
if ($Out) {
    $outBase = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Out)
    # a folder: one that exists, or any -Out ending in a separator (a new folder, created below)
    if (($Out -match '[\\/]$') -or (Test-Path -LiteralPath $outBase -PathType Container)) { $outBase = [IO.Path]::Combine($outBase, $jobName) }
    elseif ($outBase -match '\.cii$') { $outBase = $outBase.Substring(0, $outBase.Length - 4) }
} else {
    $outBase = [IO.Path]::Combine([IO.Path]::GetDirectoryName($cfgPath), $jobName)
}
$outBase = [IO.Path]::GetFullPath($outBase)
$repoRoot = (Resolve-Path -LiteralPath (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))).ProviderPath.TrimEnd('\', '/')
$inRepo = $outBase.Equals($repoRoot, [StringComparison]::OrdinalIgnoreCase) -or $outBase.StartsWith($repoRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
if ($inRepo) {
    throw ("Build-Pipe: refusing to write {0}.cii inside the Pluto repo ({1}): project outputs never land in Pluto. Pass -Out <project dir>\<name>." -f $outBase, $repoRoot)
}
if (-not $ModelId) { $ModelId = "pipe/" + $jobName }
$outDir = [IO.Path]::GetDirectoryName($outBase)
if ($outDir) { $null = [IO.Directory]::CreateDirectory($outDir) }   # literal path ([ ] are not wildcards here)
$ciiFile = $outBase + ".cii"
$csvFile = $outBase + ".nodes.csv"
Say "check" ("{0} -> {1} + .nodes.csv{2}" -f $cfgPath, $ciiFile, $(if ($Preview) { " + .bin + .features.json (model id $ModelId)" } else { "" }))
if ($Set) { Say "check" ("-Set {0}" -f $Set) }

# ---- load: the C# batch ----
$sw = [Diagnostics.Stopwatch]::StartNew()
. (Join-Path (Join-Path (Split-Path -Parent $PSScriptRoot) "lib") "Config.ps1")
Say "load" ("scripts\lib + scripts\arms compiled ({0:0.0}s)" -f $sw.Elapsed.TotalSeconds)

# ---- build + write: a config error is reported as such (the innermost message names the config item) ----
$sw.Reset(); $sw.Start()
try {
    $r = [PipeBuilder]::Build($cfgPath, $Set)
    [CaesarNeutralWriter]::Write($r.Model, $ciiFile)
    [IO.File]::WriteAllText($csvFile, $r.NodesCsv(), (New-Object Text.UTF8Encoding($false)))
} catch {
    $e = $_.Exception
    while ($e.InnerException) { $e = $e.InnerException }
    Say "error" $e.Message
    Write-Output ("RESULT {""error"":" + (Q $e.Message) + "}")
    exit 1
}
foreach ($line in ($r.Summary() -split "`r?`n")) { if ($line.Trim()) { Say "build" $line } }
foreach ($w in $r.Warnings) { Say "warn" $w }
Say "write" ("{0} ({1:0.0}s): import it in CAESAR II (File > Import > CAESAR II Neutral File)" -f $ciiFile, $sw.Elapsed.TotalSeconds)
Say "write" ("{0}: node numbers by run and position" -f $csvFile)

# ---- preview: the written .cii, as Run-Caesar.ps1 exports it ----
$previewJson = "null"
if ($Preview) {
    $sw.Reset(); $sw.Start()
    $p = [CaesarToPluto]::Export($ciiFile, $null, $outBase, $ModelId, (New-Object CaesarOptions))
    foreach ($line in ($p.Summary() -split "`r?`n")) { if ($line.Trim()) { Say "preview" $line } }
    Say "preview" ("done ({0:0.0}s)" -f $sw.Elapsed.TotalSeconds)
    foreach ($w in $p.Warnings) { Say "warn" $w }
    Say "open" ("{0} TOGETHER with {1} in the viewer (drop both, or index.html?bin=<url>&features=<url>): the support symbols live in the .features.json" -f $p.BinPath, $p.SidecarPath)
    $previewJson = $p.ToJson()
}
Write-Output ("RESULT {""cii"":" + (Q $ciiFile) + ",""nodes"":" + (Q $csvFile) + ",""build"":" + $r.ToJson() + ",""preview"":" + $previewJson + "}")
