# The pipe model runner: one pipe model in, the files you choose out. Windows PowerShell 5.1; no Python, no CAESAR
# II needed.
#   in    -In <file> (alias -Config):
#           .toml       a parametric config (PipeBuilder: runs, bends, branches, components, supports by type or DOF,
#                       ties, SIFs, temperatures / delta T, pressures, forces, imposed displacements, uniform loads,
#                       wind; every key: scripts\caesar\examples\pipe-example.toml)
#           .cii        an existing CAESAR II job (CaesarPipeImport: everything the model does not edit -- hangers,
#                       nozzles, allowables, node names, offsets, units, options -- is carried as written; every
#                       import checks that writing it back gives the source identically, and says so)
#           .pipe.json  a model saved with -Json
#   out   -Cii      <base>.cii, the CAESAR II neutral file (+ <base>.nodes.csv for a .toml: node numbers by position).
#                   The default for a .toml when no output is chosen. Never written over the input file. What it holds
#                   that no CAESAR-written file has shown yet (layouts and codes from CAESAR's documentation) is listed
#                   as "[notice ]" lines: check those in CAESAR after the import.
#         -Json     <base>.pipe.json, the whole model (PipeModelJson; exact, for tests and as the workbook's base)
#         -Preview  <base>.bin + <base>.features.json for the viewer, built from the model in memory (what
#                   Run-Caesar.ps1 makes of the .cii): look before CAESAR does
#         -Compare <file>  the model against another (.cii, .toml or .pipe.json) by meaning: elements, node positions
#                   (CAESAR's rules), restraints, SIFs, carried data -- e.g. CAESAR's own re-export of the job
#   -Seed <.cii>    a .toml model takes that CAESAR job's settings: VERSION lines, UNITS (English only for a .toml),
#                   execution options, first allowables block
#   -Set "a=1;b=2"  overrides [parameters] of a .toml
#   -Out <base>     default beside the input, same name with spaces -> underscores; an existing folder or a path
#                   ending in \ or / means <folder>\<name>; a trailing .cii is dropped. Outputs inside this repo are
#                   refused: project outputs never land in Pluto.
#   -ModelId        the viewer model id with -Preview (default pipe/<name>)
#
# Every line of progress starts with "[step]"; the last line is "RESULT {json}" ({"error": "..."} with exit code 1
# when the input does not build or a file cannot be written).
#   powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File scripts\caesar\Build-Pipe.ps1 -In "<project dir>\line.toml" -Out "<project dir>\line" -Preview
#   powershell.exe ... -File scripts\caesar\Build-Pipe.ps1 -In "<project dir>\job.cii" -Compare "<project dir>\job_from_caesar.cii"
param(
    [Parameter(Mandatory = $true)][Alias('Config')][string]$In,
    [string]$Out,
    [string]$Set,
    [switch]$Cii,
    [switch]$Json,
    [switch]$Preview,
    [string]$Compare,
    [string]$Seed,
    [string]$ModelId
)
$ErrorActionPreference = "Stop"
function Say([string]$step, [string]$text) { Write-Output ("[{0}] {1}" -f $step.PadRight(7), $text) }
function Q([string]$s) { if ([string]::IsNullOrEmpty($s)) { return "null" } return (ConvertTo-Json -InputObject $s -Compress) }
function Lines([string]$step, [string]$text) { foreach ($line in ($text -split "`r?`n")) { if ($line.Trim()) { Say $step $line } } }
function Full([string]$p) { return (Resolve-Path -LiteralPath $p).ProviderPath }

# ---- check: arguments and the output location, before the C# batch loads ----
if (-not (Test-Path -LiteralPath $In -PathType Leaf)) { throw "Build-Pipe: input not found: $In" }
$inPath = Full $In
$ext = [IO.Path]::GetExtension($inPath).ToLowerInvariant()
if ($inPath.ToLowerInvariant().EndsWith(".pipe.json")) { $kind = "json" }
elseif ($ext -eq ".toml") { $kind = "toml" }
elseif ($ext -eq ".cii") { $kind = "cii" }
else { throw ("Build-Pipe: {0}: give a .toml config, a CAESAR .cii or a .pipe.json model" -f $inPath) }
if ($Set -and $kind -ne "toml") { throw "Build-Pipe: -Set overrides [parameters] of a .toml config" }
if ($Seed -and $kind -ne "toml") { throw "Build-Pipe: -Seed gives a new (.toml) model a CAESAR job's settings; a .cii or .pipe.json has its own" }
if ($Seed -and -not (Test-Path -LiteralPath $Seed -PathType Leaf)) { throw "Build-Pipe: seed not found: $Seed" }
if ($Compare -and -not (Test-Path -LiteralPath $Compare -PathType Leaf)) { throw "Build-Pipe: -Compare file not found: $Compare" }
if (-not ($Cii -or $Json -or $Preview -or $Compare) -and $kind -eq "toml") { $Cii = $true }   # a config builds a .cii unless told otherwise
$jobName = ([IO.Path]::GetFileName($inPath) -replace '\.pipe\.json$', '' -replace '\.[^.]+$', '') -replace ' ', '_'
if ($Out) {
    $outBase = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Out)
    # a folder: one that exists, or any -Out ending in a separator (a new folder, created below)
    if (($Out -match '[\\/]$') -or (Test-Path -LiteralPath $outBase -PathType Container)) { $outBase = [IO.Path]::Combine($outBase, $jobName) }
    elseif ($outBase -match '\.cii$') { $outBase = $outBase.Substring(0, $outBase.Length - 4) }
} else {
    $outBase = [IO.Path]::Combine([IO.Path]::GetDirectoryName($inPath), $jobName)
}
$outBase = [IO.Path]::GetFullPath($outBase)
$ciiFile = $outBase + ".cii"
$csvFile = $outBase + ".nodes.csv"
$jsonFile = $outBase + ".pipe.json"
if ($Cii -or $Json -or $Preview) {
    $repoRoot = (Resolve-Path -LiteralPath (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))).ProviderPath.TrimEnd('\', '/')
    $inRepo = $outBase.Equals($repoRoot, [StringComparison]::OrdinalIgnoreCase) -or $outBase.StartsWith($repoRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
    if ($inRepo) {
        throw ("Build-Pipe: refusing to write {0}.* inside the Pluto repo ({1}): project outputs never land in Pluto. Pass -Out <project dir>\<name>." -f $outBase, $repoRoot)
    }
    if ($Cii -and $ciiFile.Equals($inPath, [StringComparison]::OrdinalIgnoreCase)) { throw ("Build-Pipe: -Cii would write over the input {0}; pass -Out <another name>" -f $inPath) }
    if ($Json -and $jsonFile.Equals($inPath, [StringComparison]::OrdinalIgnoreCase)) { throw ("Build-Pipe: -Json would write over the input {0}; pass -Out <another name>" -f $inPath) }
    $outDir = [IO.Path]::GetDirectoryName($outBase)
    if ($outDir) { $null = [IO.Directory]::CreateDirectory($outDir) }   # literal path ([ ] are not wildcards here)
}
if (-not $ModelId) { $ModelId = "pipe/" + $jobName }
$outs = @()
if ($Cii) { $outs += $ciiFile }
if ($Json) { $outs += $jsonFile }
if ($Preview) { $outs += ($outBase + ".bin + .features.json (model id " + $ModelId + ")") }
Say "check" ("{0} ({1}) -> {2}" -f $inPath, $kind, $(if ($outs.Count) { $outs -join ", " } else { "no files (a report only)" }))
if ($Set) { Say "check" ("-Set {0}" -f $Set) }

# ---- load: the C# batch ----
$sw = [Diagnostics.Stopwatch]::StartNew()
. (Join-Path (Join-Path (Split-Path -Parent $PSScriptRoot) "lib") "Config.ps1")
Say "load" ("scripts\lib + scripts\arms compiled ({0:0.0}s)" -f $sw.Elapsed.TotalSeconds)

function Model-Of([string]$path) {
    $p = $path.ToLowerInvariant()
    if ($p.EndsWith(".pipe.json")) { return [PipeModelJson]::Read([IO.File]::ReadAllText($path)) }
    if ($p.EndsWith(".toml")) { return ([PipeBuilder]::Build($path, $null)).Model }
    return ([CaesarPipeImport]::FromCii($path)).Model
}

# ---- build / import, write, compare: an input error is reported as such (the innermost message names it) ----
$sw.Reset(); $sw.Start()
$buildJson = "null"; $seedText = $null; $compareJson = "null"; $notices = @()
try {
    if ($kind -eq "toml") {
        $r = [PipeBuilder]::Build($inPath, $Set)
        $model = $r.Model
        Lines "build" $r.Summary()
        foreach ($w in $r.Warnings) { Say "warn" $w }
        $buildJson = $r.ToJson()
        if ($Seed) { $seedText = [CaesarPipeImport]::ApplySeed($model, (Full $Seed), $true); Say "seed" $seedText }
    } elseif ($kind -eq "cii") {
        $imp = [CaesarPipeImport]::FromCii($inPath)
        $model = $imp.Model
        Lines "import" $imp.Summary()
        foreach ($w in $imp.Warnings) { Say "warn" ("reader: " + $w) }
        $buildJson = $imp.ToJson()
    } else {
        $model = [PipeModelJson]::Read([IO.File]::ReadAllText($inPath))
        Say "read" ("{0}: {1} elements, {2} restraints" -f $inPath, $model.Elements.Count, $model.Restraints.Count)
    }
    if ($Cii) {
        [CaesarNeutralWriter]::Write($model, $ciiFile)
        Say "write" ("{0} ({1:0.0}s): import it in CAESAR II (File > Import > CAESAR II Neutral File)" -f $ciiFile, $sw.Elapsed.TotalSeconds)
        if ($kind -eq "toml") {
            [IO.File]::WriteAllText($csvFile, $r.NodesCsv(), (New-Object Text.UTF8Encoding($false)))
            Say "write" ("{0}: node numbers by run and position" -f $csvFile)
        }
        $notices = @([CaesarNeutralWriter]::Notices($model))
        foreach ($n in $notices) { Say "notice" $n }
    }
    if ($Json) {
        [IO.File]::WriteAllText($jsonFile, [PipeModelJson]::Write($model), (New-Object Text.UTF8Encoding($false)))
        Say "write" ("{0}: the whole model" -f $jsonFile)
    }
    if ($Compare) {
        $cmpPath = Full $Compare
        $other = Model-Of $cmpPath
        $c = [CaesarCompare]::Compare($model, [IO.Path]::GetFileName($inPath), $other, [IO.Path]::GetFileName($cmpPath), 0.01)
        Lines "compare" $c.Summary(25)
        $compareJson = $c.ToJson()
    }
} catch {
    $e = $_.Exception
    while ($e.InnerException) { $e = $e.InnerException }
    Say "error" $e.Message
    Write-Output ("RESULT {""error"":" + (Q $e.Message) + "}")
    exit 1
}

# ---- preview: the model as CAESAR would read it, built in memory ----
$previewJson = "null"
if ($Preview) {
    $sw.Reset(); $sw.Start()
    $cm = [CaesarNeutralReader]::ParseText([CaesarNeutralWriter]::ToText($model))
    $p = [CaesarToPluto]::Export($cm, ($outBase + ".cii"), $null, $outBase, $ModelId, (New-Object CaesarOptions))
    Lines "preview" $p.Summary()
    Say "preview" ("done ({0:0.0}s)" -f $sw.Elapsed.TotalSeconds)
    foreach ($w in $p.Warnings) { Say "warn" $w }
    Say "open" ("{0} TOGETHER with {1} in the viewer (drop both, or index.html?bin=<url>&features=<url>): the support symbols live in the .features.json" -f $p.BinPath, $p.SidecarPath)
    $previewJson = $p.ToJson()
}
$res = "RESULT {""input"":" + (Q $inPath) + ",""kind"":" + (Q $kind)
$res += ",""cii"":" + $(if ($Cii) { Q $ciiFile } else { "null" })
$res += ",""nodes"":" + $(if ($Cii -and $kind -eq "toml") { Q $csvFile } else { "null" })
$res += ",""json"":" + $(if ($Json) { Q $jsonFile } else { "null" })
$res += ",""notices"":[" + (($notices | ForEach-Object { Q $_ }) -join ",") + "]"
$res += ",""seed"":" + (Q $seedText) + ",""build"":" + $buildJson + ",""compare"":" + $compareJson + ",""preview"":" + $previewJson + "}"
Write-Output $res
