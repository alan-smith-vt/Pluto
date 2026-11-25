# The STAAD.Pro frame runner: a STAAD frame model's output file (.anl) -> a Pluto viewer model, <base>.bin +
# <base>.features.json, through StaadToPluto (scripts\arms\StaadToPluto.cs: AnlFrameReader + RawViewerWriter +
# FeaturesSidecar). Windows PowerShell 5.1; no Python, no STAAD.Pro needed. vault\arms\staad-frame-to-viewer.md.
# Steps, in this order:
#
#   check    -Anl must exist; the output base must lie OUTSIDE this repo -- project outputs never land in Pluto, so an
#            output inside it (the default beside an .anl kept in the repo included) is refused before anything loads
#   load     the C# batch: scripts\lib\Config.ps1 (one Add-Type of scripts\lib + scripts\arms)
#   export   StaadToPluto: the members (STAAD's local axes, BETA turned; releases; offsets across the member), their
#            sections (SAP's section library by name, else fitted to STAAD's PRINT MEMBER PROPERTIES, PRIS as given),
#            the supports (sidecar items the viewer draws as support symbols). Per load case, what the file printed:
#            member end forces (the force inside the member at each end), joint displacements (the deformed shape),
#            support reactions (they colour the support symbols) and code check ratios. A CHECK CODE under a LOAD LIST
#            of one case goes into that case; any other is an entry of its own (each member's governing ratio and
#            case). A model the reader cannot hold exactly is refused, every problem named. A previous
#            <base>.features.json is merged: colour / visibility edits, hand-made groups and section cuts survive.
#
# What to print in the .std for a full view: PRINT MEMBER PROPERTIES (sections without SAP's library), PRINT MEMBER
# FORCES, PRINT JOINT DISPLACEMENTS, PRINT SUPPORT REACTIONS, and for code ratios per case one LOAD LIST <case> +
# CHECK CODE per case (a CHECK CODE under several cases prints only each member's governing one).
#
# -Out <base>: writes <base>.bin + <base>.features.json. Default: beside the .anl, same name with spaces ->
#   underscores. An existing folder, or any path ending in \ or /, means <folder>\<name>; a trailing .bin is dropped.
# -ModelId: id written to the binary and the sidecar (default staad/<name>).
# -SapLibrary: SAP2000's section library folder (default: the newest SAP2000 installed, ...\Property Libraries\Sections).
#
# Every line of progress starts with "[step]"; the last line is "RESULT {json}" (machine-readable: bin, features,
# load cases, counts, warnings, notes). Open the .bin TOGETHER with the .features.json in the viewer (drop both files,
# or viewer\index.html?bin=<url>&features=<url>): the groups and the support symbols live in the sidecar.
#   powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File scripts\staad\Run-StaadFrame.ps1 -Anl "<project dir>\frame.anl" -Out "<project dir>\viewer\frame"
param(
    [Parameter(Mandatory = $true)][string]$Anl,
    [string]$Out,
    [string]$ModelId,
    [string]$SapLibrary
)
$ErrorActionPreference = "Stop"
function Say([string]$step, [string]$text) { Write-Output ("[{0}] {1}" -f $step.PadRight(7), $text) }

# ---- check: arguments and the output location, before the C# batch loads ----
if (-not (Test-Path -LiteralPath $Anl -PathType Leaf)) { throw "Run-StaadFrame: .anl not found: $Anl" }
$anlPath = (Resolve-Path -LiteralPath $Anl).ProviderPath
$libPath = $null
if ($SapLibrary) {
    if (-not (Test-Path -LiteralPath $SapLibrary -PathType Container)) { throw "Run-StaadFrame: -SapLibrary is not a folder: $SapLibrary" }
    $libPath = (Resolve-Path -LiteralPath $SapLibrary).ProviderPath
}
$jobName = [IO.Path]::GetFileNameWithoutExtension($anlPath) -replace ' ', '_'
if ($Out) {
    $outBase = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Out)
    # a folder: one that exists, or any -Out ending in a separator (a new folder, created below)
    if (($Out -match '[\\/]$') -or (Test-Path -LiteralPath $outBase -PathType Container)) { $outBase = [IO.Path]::Combine($outBase, $jobName) }
    elseif ($outBase -match '\.bin$') { $outBase = $outBase.Substring(0, $outBase.Length - 4) }
} else {
    $outBase = [IO.Path]::Combine([IO.Path]::GetDirectoryName($anlPath), $jobName)
}
$outBase = [IO.Path]::GetFullPath($outBase)
$repoRoot = (Resolve-Path -LiteralPath (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))).ProviderPath.TrimEnd('\', '/')
$inRepo = $outBase.Equals($repoRoot, [StringComparison]::OrdinalIgnoreCase) -or $outBase.StartsWith($repoRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
if ($inRepo) {
    throw ("Run-StaadFrame: refusing to write {0}.bin inside the Pluto repo ({1}): project outputs never land in Pluto. Pass -Out <project dir>\<name>." -f $outBase, $repoRoot)
}
if (-not $ModelId) { $ModelId = "staad/" + $jobName }
$outDir = [IO.Path]::GetDirectoryName($outBase)
if ($outDir) { $null = [IO.Directory]::CreateDirectory($outDir) }   # literal path ([ ] are not wildcards here)
Say "check" ("{0} -> {1}.bin + .features.json (model id {2})" -f $anlPath, $outBase, $ModelId)

# ---- load: the C# batch ----
$sw = [Diagnostics.Stopwatch]::StartNew()
. (Join-Path (Join-Path (Split-Path -Parent $PSScriptRoot) "lib") "Config.ps1")
Say "load" ("scripts\lib + scripts\arms compiled ({0:0.0}s)" -f $sw.Elapsed.TotalSeconds)

# ---- export ----
$sw.Reset(); $sw.Start()
$r = [StaadToPluto]::Export($anlPath, $outBase, $ModelId, $libPath)
foreach ($line in ($r.Summary() -split "`r?`n")) { if ($line.Trim()) { Say "export" $line } }
Say "export" ("done ({0:0.0}s)" -f $sw.Elapsed.TotalSeconds)
foreach ($n in $r.Notes) { Say "note" $n }
foreach ($w in $r.Warnings) { Say "warn" $w }
Say "open" ("{0} TOGETHER with {1} in the viewer (drop both, or index.html?bin=<url>&features=<url>): the groups and the support symbols live in the .features.json" -f $r.BinPath, $r.SidecarPath)
Write-Output ("RESULT " + $r.ToJson())
