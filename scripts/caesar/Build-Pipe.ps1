# The pipe model runner: one pipe model in, the files you choose out. Windows PowerShell 5.1; no Python, no CAESAR
# II needed.
#   in    -In <file> (alias -Config):
#           .xlsx       a pipe model workbook (PipeWorkbook: written by -Xlsx or -NewWorkbook, edited in Excel; the
#                       report lists every edit against the model it was written from)
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
#                   as "[notice ]" lines: check those in CAESAR after the import. An existing <base>.cii is replaced
#                   only when this runner wrote it, or it is the model's own source, or nothing would change in it;
#                   otherwise (CAESAR or someone changed it) -Force is needed. The report says whether the model is
#                   the one you previewed ("[review ]").
#         -Xlsx     <base>.xlsx, the model as a workbook to edit (never written over the input)
#         -Json     <base>.pipe.json, the whole model (PipeModelJson; exact)
#         -Preview  <base>.bin + <base>.features.json for the viewer, built from the model in memory (what
#                   Run-Caesar.ps1 makes of the .cii): look before CAESAR does. Written to temporary names and moved
#                   into place, so a viewer reloading them never sees half a file.
#         -Compare <file>  the model against another (.cii, .toml, .xlsx or .pipe.json) by meaning: elements, node
#                   positions (CAESAR's rules), restraints, SIFs, loads, carried data -- e.g. CAESAR's own re-export
#   -Watch          keep running: rebuild the chosen outputs (default -Preview) every time the input is saved; an
#                   input that does not build is reported and the last good outputs stay. Ctrl+C stops.
#   -NewWorkbook    start a model: -Out <base> gets <base>.xlsx, a small template (two legs, a bend, two anchors and a
#                   rest) to edit; with -Seed, the CAESAR job's settings (English units only)
#   -Seed <.cii>    a .toml model (or -NewWorkbook) takes that CAESAR job's settings: VERSION lines, UNITS (English
#                   only), execution options, first allowables block
#   -Set "a=1;b=2"  overrides [parameters] of a .toml
#   -Force          replace a .cii this runner did not write (see -Cii), or an existing -NewWorkbook file
#   -Out <base>     default beside the input, same name with spaces -> underscores; an existing folder or a path
#                   ending in \ or / means <folder>\<name>; a trailing .cii / .xlsx is dropped. Outputs inside this
#                   repo are refused: project outputs never land in Pluto.
#   -ModelId        the viewer model id with -Preview (default pipe/<name>)
# Beside the outputs, <base>.build.json records what was written from what (file and model fingerprints): it is how
# the runner knows its own .cii and the model you previewed.
#
# Every line of progress starts with "[step]"; the last line is "RESULT {json}" ({"error": "..."} with exit code 1
# when the input does not build or a file cannot be written; with -Watch, one RESULT line per build).
#   powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File scripts\caesar\Build-Pipe.ps1 -In "<project dir>\line.toml" -Out "<project dir>\line" -Preview
#   powershell.exe ... -File scripts\caesar\Build-Pipe.ps1 -In "<project dir>\job.cii" -Xlsx            (then edit job.xlsx in Excel)
#   powershell.exe ... -File scripts\caesar\Build-Pipe.ps1 -In "<project dir>\job.xlsx" -Out "<project dir>\job_v2" -Preview -Watch
#   powershell.exe ... -File scripts\caesar\Build-Pipe.ps1 -In "<project dir>\job.xlsx" -Out "<project dir>\job_v2" -Cii
#   powershell.exe ... -File scripts\caesar\Build-Pipe.ps1 -NewWorkbook -Out "<project dir>\new_line"
param(
    [Alias('Config')][string]$In,
    [string]$Out,
    [string]$Set,
    [switch]$Cii,
    [switch]$Xlsx,
    [switch]$Json,
    [switch]$Preview,
    [string]$Compare,
    [switch]$Watch,
    [switch]$NewWorkbook,
    [string]$Seed,
    [switch]$Force,
    [string]$ModelId
)
$ErrorActionPreference = "Stop"
function Say([string]$step, [string]$text) { Write-Output ("[{0}] {1}" -f $step.PadRight(7), $text) }
function Q([string]$s) { if ([string]::IsNullOrEmpty($s)) { return "null" } return (ConvertTo-Json -InputObject $s -Compress) }
function Lines([string]$step, [string]$text) { foreach ($line in ($text -split "`r?`n")) { if ($line.Trim()) { Say $step $line } } }
function Full([string]$p) { return (Resolve-Path -LiteralPath $p).ProviderPath }
function Sha([byte[]]$b) { $h = [Security.Cryptography.SHA256]::Create(); try { return (($h.ComputeHash($b) | ForEach-Object { $_.ToString("x2") }) -join "") } finally { $h.Dispose() } }
function FileSha([string]$p) { $fs = New-Object IO.FileStream($p, [IO.FileMode]::Open, [IO.FileAccess]::Read, ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)); try { $h = [Security.Cryptography.SHA256]::Create(); return (($h.ComputeHash($fs) | ForEach-Object { $_.ToString("x2") }) -join "") } finally { $fs.Dispose() } }
function Fail([string]$msg) { Say "error" $msg; Write-Output ("RESULT {""error"":" + (Q $msg) + "}"); exit 1 }

# ---- check: arguments and the output location, before the C# batch loads ----
if ($NewWorkbook) {
    if ($In) { Fail "Build-Pipe: -NewWorkbook starts a model from the template: give -Out <base>, not -In" }
    if (-not $Out) { Fail "Build-Pipe: -NewWorkbook needs -Out <project dir>\<name> (the workbook is <name>.xlsx)" }
    if ($Cii -or $Json -or $Preview -or $Xlsx -or $Compare -or $Watch -or $Set) { Fail "Build-Pipe: -NewWorkbook writes the workbook only; edit it, then run -In <name>.xlsx" }
    $kind = "new"; $inPath = $null; $jobName = ([IO.Path]::GetFileName(($Out.TrimEnd('\', '/') -replace '\.xlsx$', ''))) -replace ' ', '_'
} else {
    if (-not $In) { Fail "Build-Pipe: give -In <.xlsx | .toml | .cii | .pipe.json>, or -NewWorkbook -Out <base>" }
    if (-not (Test-Path -LiteralPath $In -PathType Leaf)) { Fail "Build-Pipe: input not found: $In" }
    $inPath = Full $In
    $ext = [IO.Path]::GetExtension($inPath).ToLowerInvariant()
    if ($inPath.ToLowerInvariant().EndsWith(".pipe.json")) { $kind = "json" }
    elseif ($ext -eq ".toml") { $kind = "toml" }
    elseif ($ext -eq ".cii") { $kind = "cii" }
    elseif ($ext -eq ".xlsx") { $kind = "xlsx" }
    else { Fail ("Build-Pipe: {0}: give a .xlsx workbook, a .toml config, a CAESAR .cii or a .pipe.json model" -f $inPath) }
    if ($Set -and $kind -ne "toml") { Fail "Build-Pipe: -Set overrides [parameters] of a .toml config" }
    if ($Seed -and $kind -ne "toml") { Fail "Build-Pipe: -Seed gives a new (.toml or -NewWorkbook) model a CAESAR job's settings; a .cii, .xlsx or .pipe.json has its own" }
    $jobName = ([IO.Path]::GetFileName($inPath) -replace '\.pipe\.json$', '' -replace '\.[^.]+$', '') -replace ' ', '_'
}
if ($Seed -and -not (Test-Path -LiteralPath $Seed -PathType Leaf)) { Fail "Build-Pipe: seed not found: $Seed" }
if ($Compare -and -not (Test-Path -LiteralPath $Compare -PathType Leaf)) { Fail "Build-Pipe: -Compare file not found: $Compare" }
if (-not ($Cii -or $Json -or $Preview -or $Compare -or $Xlsx) -and $kind -eq "toml") { $Cii = $true }   # a config builds a .cii unless told otherwise
if ($Watch -and -not ($Cii -or $Json -or $Preview -or $Xlsx)) { $Preview = $true }                        # watching: the preview, unless told otherwise
if ($Out) {
    $outBase = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Out)
    # a folder: one that exists, or any -Out ending in a separator (a new folder, created below)
    if (($Out -match '[\\/]$') -or (Test-Path -LiteralPath $outBase -PathType Container)) { $outBase = [IO.Path]::Combine($outBase, $jobName) }
    elseif ($outBase -match '\.(cii|xlsx)$') { $outBase = $outBase.Substring(0, $outBase.LastIndexOf('.')) }
} else {
    $outBase = [IO.Path]::Combine([IO.Path]::GetDirectoryName($inPath), $jobName)
}
$outBase = [IO.Path]::GetFullPath($outBase)
$ciiFile = $outBase + ".cii"; $csvFile = $outBase + ".nodes.csv"; $jsonFile = $outBase + ".pipe.json"; $xlsxFile = $outBase + ".xlsx"; $stampFile = $outBase + ".build.json"
if ($Cii -or $Json -or $Preview -or $Xlsx -or $NewWorkbook) {
    $repoRoot = (Resolve-Path -LiteralPath (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))).ProviderPath.TrimEnd('\', '/')
    $inRepo = $outBase.Equals($repoRoot, [StringComparison]::OrdinalIgnoreCase) -or $outBase.StartsWith($repoRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
    if ($inRepo) { Fail ("Build-Pipe: refusing to write {0}.* inside the Pluto repo ({1}): project outputs never land in Pluto. Pass -Out <project dir>\<name>." -f $outBase, $repoRoot) }
    foreach ($pair in @(@($Cii, $ciiFile, "-Cii"), @($Json, $jsonFile, "-Json"), @($Xlsx, $xlsxFile, "-Xlsx"))) {
        if ($pair[0] -and $inPath -and $pair[1].Equals($inPath, [StringComparison]::OrdinalIgnoreCase)) { Fail ("Build-Pipe: {0} would write over the input {1}; pass -Out <another name>" -f $pair[2], $inPath) }
    }
    $outDir = [IO.Path]::GetDirectoryName($outBase)
    if ($outDir) { $null = [IO.Directory]::CreateDirectory($outDir) }   # literal path ([ ] are not wildcards here)
}
if (-not $ModelId) { $ModelId = "pipe/" + $jobName }
$outs = @()
if ($NewWorkbook) { $outs += $xlsxFile }
if ($Cii) { $outs += $ciiFile }
if ($Xlsx) { $outs += $xlsxFile }
if ($Json) { $outs += $jsonFile }
if ($Preview) { $outs += ($outBase + ".bin + .features.json (model id " + $ModelId + ")") }
Say "check" ("{0} ({1}) -> {2}" -f $(if ($inPath) { $inPath } else { "the template" }), $kind, $(if ($outs.Count) { $outs -join ", " } else { "no files (a report only)" }))
if ($Set) { Say "check" ("-Set {0}" -f $Set) }

# ---- load: the C# batch ----
$sw = [Diagnostics.Stopwatch]::StartNew()
. (Join-Path (Join-Path (Split-Path -Parent $PSScriptRoot) "lib") "Config.ps1")
Say "load" ("scripts\lib + scripts\arms compiled ({0:0.0}s)" -f $sw.Elapsed.TotalSeconds)

# ---- -NewWorkbook: the template, written once ----
if ($NewWorkbook) {
    try {
        if ((Test-Path -LiteralPath $xlsxFile) -and -not $Force) { throw ("{0} exists: pick another -Out, or -Force to replace it" -f $xlsxFile) }
        $model = [PipeWorkbook]::Template()
        $seedText = $null
        if ($Seed) { $seedText = [CaesarPipeImport]::ApplySeed($model, (Full $Seed), $true); Say "seed" $seedText }
        [PipeWorkbook]::Write($model, $xlsxFile, "the Pluto template" + $(if ($Seed) { " + seed " + [IO.Path]::GetFileName($Seed) } else { "" }))
        Say "write" ("{0}: a {1}-element template to edit in Excel; then Build-Pipe.ps1 -In ""{0}"" -Preview" -f $xlsxFile, $model.Elements.Count)
        Write-Output ("RESULT {""kind"":""new"",""xlsx"":" + (Q $xlsxFile) + ",""seed"":" + (Q $seedText) + "}")
        exit 0
    } catch {
        $e = $_.Exception; while ($e.InnerException) { $e = $e.InnerException }
        Fail $e.Message
    }
}

function Model-Of([string]$path) {
    $p = $path.ToLowerInvariant()
    if ($p.EndsWith(".pipe.json")) { return [PipeModelJson]::Read([IO.File]::ReadAllText($path)) }
    if ($p.EndsWith(".toml")) { return ([PipeBuilder]::Build($path, $null)).Model }
    if ($p.EndsWith(".xlsx")) { return ([PipeWorkbook]::Read($path)).Model }
    return ([CaesarPipeImport]::FromCii($path)).Model
}

function Read-Stamp {
    if (-not (Test-Path -LiteralPath $stampFile -PathType Leaf)) { return $null }
    try { return (Get-Content -LiteralPath $stampFile -Raw | ConvertFrom-Json) } catch { return $null }
}

function Write-Stamp($cii, $preview) {
    $o = Read-Stamp
    $cj = "null"; $pj = "null"
    if ($cii) { $cj = $cii } elseif ($o -and $o.cii) { $cj = (ConvertTo-Json -InputObject $o.cii -Compress) }
    if ($preview) { $pj = $preview } elseif ($o -and $o.preview) { $pj = (ConvertTo-Json -InputObject $o.preview -Compress) }
    $text = "{""format"":""pluto-pipe-build"",""cii"":" + $cj + ",""preview"":" + $pj + "}"
    [IO.File]::WriteAllText($stampFile, $text, (New-Object Text.UTF8Encoding($false)))
}

# an existing .cii may be replaced when this runner wrote it, when it is the model's own source (the base), or when
# nothing in it would change; otherwise it was changed outside (CAESAR, a person) and -Force is needed
function Check-Target($model, $base) {
    if (-not (Test-Path -LiteralPath $ciiFile -PathType Leaf)) { return }
    $st = Read-Stamp
    if ($st -and $st.cii -and $st.cii.sha256 -eq (FileSha $ciiFile)) { return }
    try { $target = ([CaesarPipeImport]::FromCii($ciiFile)).Model } catch { if ($Force) { Say "warn" ("-Force: replacing {0}, which does not read as a neutral file" -f $ciiFile); return }; throw ("{0} exists and does not read as a neutral file: -Force replaces it" -f $ciiFile) }
    $c = [CaesarCompare]::Compare($target, [IO.Path]::GetFileName($ciiFile), $model, "this model", 0.01)
    if ($c.Differences -eq 0) { return }
    if ($base) { $b = [CaesarCompare]::Compare($base, "the base", $target, "it", 0.01); if ($b.Differences -eq 0) { return } }
    if ($Force) { Say "warn" ("-Force: replacing {0}, changed outside this runner ({1} difference(s) from this model)" -f $ciiFile, $c.Differences); return }
    Lines "target" $c.Summary(5)
    throw ("{0} was changed since this runner wrote it (by CAESAR or by hand) and differs from this model in {1} way(s), listed above. Make the workbook again from it (-In {2} -Xlsx), write elsewhere (-Out), or -Force to replace it." -f $ciiFile, $c.Differences, [IO.Path]::GetFileName($ciiFile))
}

# ---- one build: read the input, write the chosen outputs; throws on an input that does not build ----
function Build-Once {
    $script:sw.Reset(); $script:sw.Start()
    $r = @{ build = "null"; seed = $null; compare = "null"; notices = @(); preview = "null"; review = $null }
    $base = $null; $csv = $null
    if ($kind -eq "toml") {
        $b = [PipeBuilder]::Build($inPath, $Set)
        $model = $b.Model
        Lines "build" $b.Summary()
        foreach ($w in $b.Warnings) { Say "warn" $w }
        $r.build = $b.ToJson()
        $csv = $b.NodesCsv()
        if ($Seed) { $r.seed = [CaesarPipeImport]::ApplySeed($model, (Full $Seed), $true); Say "seed" $r.seed }
    } elseif ($kind -eq "cii") {
        $imp = [CaesarPipeImport]::FromCii($inPath)
        $model = $imp.Model
        $base = ([CaesarPipeImport]::FromCii($inPath)).Model
        Lines "import" $imp.Summary()
        foreach ($w in $imp.Warnings) { Say "warn" ("reader: " + $w) }
        $r.build = $imp.ToJson()
    } elseif ($kind -eq "xlsx") {
        $wb = [PipeWorkbook]::Read($inPath)
        $model = $wb.Model
        $base = $wb.Base
        Lines "read" $wb.Summary()
        foreach ($w in $wb.Warnings) { Say "warn" $w }
        $r.build = $wb.ToJson()
    } else {
        $model = [PipeModelJson]::Read([IO.File]::ReadAllText($inPath))
        Say "read" ("{0}: {1} elements, {2} restraints" -f $inPath, $model.Elements.Count, $model.Restraints.Count)
    }
    $modelHash = [PipeWorkbook]::Hash($model)
    $inSha = FileSha $inPath
    $now = [DateTime]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
    if ($Cii) {
        Check-Target $model $base
        $bytes = [CaesarNeutralWriter]::ToBytes($model)
        [IO.File]::WriteAllBytes($ciiFile, $bytes)
        Say "write" ("{0} ({1:0.0}s): import it in CAESAR II (File > Import > CAESAR II Neutral File)" -f $ciiFile, $script:sw.Elapsed.TotalSeconds)
        if ($csv) { [IO.File]::WriteAllText($csvFile, $csv, (New-Object Text.UTF8Encoding($false))); Say "write" ("{0}: node numbers by run and position" -f $csvFile) }
        $r.notices = @([CaesarNeutralWriter]::Notices($model))
        foreach ($n in $r.notices) { Say "notice" $n }
        $st = Read-Stamp
        if ($st -and $st.preview -and $st.preview.model) {
            $when = $st.preview.written
            if ($when -is [DateTime]) { $when = $when.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss") + " UTC" } else { $when = ([string]$when).Replace("T", " ").Replace("Z", " UTC") }
            if ($st.preview.model -eq $modelHash) { $r.review = "the model is the one previewed at " + $when }
            else { $r.review = "the model CHANGED since the preview of " + $when + ": preview it again before CAESAR imports it" }
        } else { $r.review = "no preview of this model was made (-Preview shows it in the viewer first)" }
        if (-not $Preview) { Say "review" $r.review }
        Write-Stamp ("{""sha256"":" + (Q (Sha $bytes)) + ",""model"":" + (Q $modelHash) + ",""input"":" + (Q $inPath) + ",""inputSha256"":" + (Q $inSha) + ",""written"":" + (Q $now) + "}") $null
    }
    if ($Xlsx) {
        [PipeWorkbook]::Write($model, $xlsxFile, [IO.Path]::GetFileName($inPath))
        Say "write" ("{0}: the model as a workbook to edit (open it on its Start sheet)" -f $xlsxFile)
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
        $r.compare = $c.ToJson()
    }
    if ($Preview) {
        # the model as CAESAR would read it, exported to temporary names (the sidecar's own groups merged in), then moved
        $script:sw.Reset(); $script:sw.Start()
        $tmp = $outBase + ".~new"
        $sidecar = $outBase + ".features.json"
        if (Test-Path -LiteralPath $sidecar -PathType Leaf) { Copy-Item -LiteralPath $sidecar -Destination ($tmp + ".features.json") -Force }
        $cm = [CaesarNeutralReader]::ParseText([CaesarNeutralWriter]::ToText($model))
        $p = [CaesarToPluto]::Export($cm, ($outBase + ".cii"), $null, $tmp, $ModelId, (New-Object CaesarOptions))
        Move-Item -LiteralPath ($tmp + ".bin") -Destination ($outBase + ".bin") -Force
        Move-Item -LiteralPath ($tmp + ".features.json") -Destination $sidecar -Force
        Lines "preview" ($p.Summary() -replace [regex]::Escape($tmp), $outBase)
        Say "preview" ("done ({0:0.0}s)" -f $script:sw.Elapsed.TotalSeconds)
        foreach ($w in $p.Warnings) { Say "warn" $w }
        Say "open" ("{0}.bin TOGETHER with {0}.features.json in the viewer (drop both, or index.html?bin=<url>&features=<url>); Auto-reload follows every rebuild" -f $outBase)
        $r.preview = $p.ToJson().Replace((ConvertTo-Json -InputObject $tmp -Compress).Trim('"'), (ConvertTo-Json -InputObject $outBase -Compress).Trim('"'))
        Write-Stamp $null ("{""model"":" + (Q $modelHash) + ",""input"":" + (Q $inPath) + ",""inputSha256"":" + (Q $inSha) + ",""written"":" + (Q $now) + "}")
        if ($Cii) { Say "review" "the .cii and this preview show the same model" ; $r.review = "the .cii and the preview are the same model" }
    }
    $res = "RESULT {""input"":" + (Q $inPath) + ",""kind"":" + (Q $kind) + ",""model"":" + (Q $modelHash)
    $res += ",""cii"":" + $(if ($Cii) { Q $ciiFile } else { "null" })
    $res += ",""nodes"":" + $(if ($Cii -and $csv) { Q $csvFile } else { "null" })
    $res += ",""xlsx"":" + $(if ($Xlsx) { Q $xlsxFile } else { "null" })
    $res += ",""json"":" + $(if ($Json) { Q $jsonFile } else { "null" })
    $res += ",""review"":" + (Q $r.review)
    $res += ",""notices"":[" + (($r.notices | ForEach-Object { Q $_ }) -join ",") + "]"
    $res += ",""seed"":" + (Q $r.seed) + ",""build"":" + $r.build + ",""compare"":" + $r.compare + ",""preview"":" + $r.preview + "}"
    return $res
}

function Innermost($err) { $e = $err.Exception; while ($e.InnerException) { $e = $e.InnerException }; return $e.Message }

if (-not $Watch) {
    try { Build-Once } catch { Fail (Innermost $_) }
    exit 0
}

# ---- -Watch: rebuild on every save; a save that does not build leaves the last good outputs ----
Say "watch" ("watching {0}: every save rebuilds {1} (Ctrl+C stops)" -f $inPath, ($outs -join ", "))
$last = $null
while ($true) {
    $fi = New-Object IO.FileInfo($inPath)
    $sig = if ($fi.Exists) { "{0}|{1}" -f $fi.LastWriteTimeUtc.Ticks, $fi.Length } else { "gone" }
    if ($sig -ne $last -and $sig -ne "gone") {
        Start-Sleep -Milliseconds 600          # let the save finish: the size and time must hold still
        $fi.Refresh()
        $sig2 = "{0}|{1}" -f $fi.LastWriteTimeUtc.Ticks, $fi.Length
        if ($sig2 -eq $sig) {
            $last = $sig
            Say "watch" ("{0} -- {1} saved, rebuilding" -f [DateTime]::Now.ToString("HH:mm:ss"), [IO.Path]::GetFileName($inPath))
            try { Build-Once }
            catch {
                $msg = Innermost $_
                Say "error" $msg
                Say "watch" "the last good outputs stay; fix the input and save again"
                Write-Output ("RESULT {""error"":" + (Q $msg) + "}")
            }
        }
    }
    Start-Sleep -Milliseconds 1000
}
