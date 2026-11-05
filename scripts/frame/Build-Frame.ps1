# The frame model runner (SAP2000 frame models: piping, ducting and steel as frames, with links, loads and cases).
# Windows PowerShell 5.1; no Python, no SAP2000 needed (except -Open, later, which runs scripts\sap\Run-Sap.ps1 in a
# process of its own). Built in phases (vault/arms/frame-workbook.md).
#   in    -In <file>:
#           .s2k / .$2k   a SAP2000 text file (File > Export > SAP2000 .s2k). Read losslessly (FrameS2k): everything
#                         the model does not edit -- areas, design data, analysis options -- is carried as written,
#                         and every import checks that writing it back gives the source byte for byte, and says so
#           .frame.json   a model saved with -Json
#   out   -S2k      <base>.s2k for SAP2000 (File > Import > SAP2000 .s2k). Never written over the input file; an
#                   existing <base>.s2k with other contents needs -Force.
#         -Json     <base>.frame.json, the whole model (FrameModelJson; exact)
#   -Out <base>     default beside the input, same name with spaces -> underscores; an existing folder or a path ending
#                   in \ or / means <folder>\<name>; a trailing .s2k is dropped. Outputs inside this repo are refused.
#   -Probe <folder>      writes probe-v22.s2k and probe-v26.s2k (the same probe stamped for SAP 22.0.0 and 26.3.0):
#                        one or more of every .s2k table and field the frame model writes that no SAP export here has
#                        shown yet (FrameProbe). Import the one matching your SAP (File > Import > SAP2000 .s2k; if
#                        Help > About shows another version, edit the Version on the file's PROGRAM CONTROL line to
#                        it), then File > Export > SAP2000 .s2k with every table, to <folder>\probe.sap-export.s2k.
#   -ProbeCheck <export> compares SAP's export with the probe it came from (-ProbeFile, default probe-v22.s2k beside
#                        the export) field by field and writes <export>.check.md: what SAP kept, changed, dropped or
#                        added, and the tables it wrote that the probe did not.
# Every line of progress starts with "[step]"; the last line is "RESULT {json}" ({"error": "..."} with exit code 1).
#   powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File scripts\frame\Build-Frame.ps1 -In "<project dir>\rack.s2k" -Json
#   powershell.exe ... -File scripts\frame\Build-Frame.ps1 -In "<project dir>\rack.frame.json" -Out "<project dir>\rack_v2" -S2k
#   powershell.exe ... -File scripts\frame\Build-Frame.ps1 -Probe "<project dir>\probe"
#   powershell.exe ... -File scripts\frame\Build-Frame.ps1 -ProbeCheck "<project dir>\probe\probe.sap-export.s2k"
param(
    [string]$In,
    [string]$Out,
    [switch]$S2k,
    [switch]$Json,
    [switch]$Force,
    [string]$Probe,
    [string]$ProbeCheck,
    [string]$ProbeFile
)
$ErrorActionPreference = "Stop"
function Say([string]$step, [string]$text) { Write-Output ("[{0}] {1}" -f $step.PadRight(7), $text) }
function Q([string]$s) { if ([string]::IsNullOrEmpty($s)) { return "null" } return (ConvertTo-Json -InputObject $s -Compress) }
function Full([string]$p) { return (Resolve-Path -LiteralPath $p).ProviderPath }
function Fail([string]$msg) { Say "error" $msg; Write-Output ("RESULT {""error"":" + (Q $msg) + "}"); exit 1 }
$repoRoot = (Resolve-Path -LiteralPath (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))).ProviderPath.TrimEnd('\', '/')
function InRepo([string]$p) { return $p.Equals($repoRoot, [StringComparison]::OrdinalIgnoreCase) -or $p.StartsWith($repoRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) }

# ---- check: arguments and the output location, before the C# batch loads ----
$modes = @($In, $Probe, $ProbeCheck) | Where-Object { $_ }
if ($modes.Count -ne 1) { Fail "Build-Frame: give one of -In <.s2k | .frame.json>, -Probe <folder>, -ProbeCheck <SAP's export .s2k>" }
if ($Probe) {
    $dir = [IO.Path]::GetFullPath($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Probe))
    if (InRepo $dir) { Fail ("Build-Frame: refusing to write the probe inside the Pluto repo ({0}); pass -Probe <project dir>\probe" -f $repoRoot) }
    $null = [IO.Directory]::CreateDirectory($dir)
}
if ($ProbeCheck) {
    if (-not (Test-Path -LiteralPath $ProbeCheck -PathType Leaf)) { Fail "Build-Frame: SAP's export not found: $ProbeCheck" }
    $exportPath = Full $ProbeCheck
    if ($ProbeFile) {
        if (-not (Test-Path -LiteralPath $ProbeFile -PathType Leaf)) { Fail "Build-Frame: probe not found: $ProbeFile" }
        $probePath = Full $ProbeFile
    } else {
        $probePath = [IO.Path]::Combine([IO.Path]::GetDirectoryName($exportPath), "probe-v22.s2k")
        if (-not (Test-Path -LiteralPath $probePath -PathType Leaf)) { Fail ("Build-Frame: no probe-v22.s2k beside {0}: pass -ProbeFile <the probe you imported>" -f $exportPath) }
    }
}
if ($In) {
    if (-not (Test-Path -LiteralPath $In -PathType Leaf)) { Fail "Build-Frame: input not found: $In" }
    $inPath = Full $In
    $ext = [IO.Path]::GetExtension($inPath).ToLowerInvariant()
    if ($inPath.ToLowerInvariant().EndsWith(".frame.json")) { $kind = "json" }
    elseif ($ext -eq ".s2k" -or $ext -eq ".`$2k") { $kind = "s2k" }
    else { Fail ("Build-Frame: {0}: give a SAP2000 .s2k (or .`$2k) or a .frame.json model" -f $inPath) }
    $jobName = ([IO.Path]::GetFileName($inPath) -replace '\.frame\.json$', '' -replace '\.[^.]+$', '') -replace ' ', '_'
    if ($Out) {
        $outBase = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Out)
        if (($Out -match '[\\/]$') -or (Test-Path -LiteralPath $outBase -PathType Container)) { $outBase = [IO.Path]::Combine($outBase, $jobName) }
        elseif ($outBase -match '\.s2k$') { $outBase = $outBase.Substring(0, $outBase.LastIndexOf('.')) }
    } else {
        $outBase = [IO.Path]::Combine([IO.Path]::GetDirectoryName($inPath), $jobName)
    }
    $outBase = [IO.Path]::GetFullPath($outBase)
    $s2kFile = $outBase + ".s2k"; $jsonFile = $outBase + ".frame.json"
    if ($S2k -or $Json) {
        if (InRepo $outBase) { Fail ("Build-Frame: refusing to write {0}.* inside the Pluto repo ({1}): project outputs never land in Pluto. Pass -Out <project dir>\<name>." -f $outBase, $repoRoot) }
        foreach ($pair in @(@($S2k, $s2kFile, "-S2k"), @($Json, $jsonFile, "-Json"))) {
            if ($pair[0] -and $pair[1].Equals($inPath, [StringComparison]::OrdinalIgnoreCase)) { Fail ("Build-Frame: {0} would write over the input {1}; pass -Out <another name>" -f $pair[2], $inPath) }
        }
        $outDir = [IO.Path]::GetDirectoryName($outBase)
        if ($outDir) { $null = [IO.Directory]::CreateDirectory($outDir) }
    }
    $outs = @(); if ($S2k) { $outs += $s2kFile }; if ($Json) { $outs += $jsonFile }
    Say "check" ("{0} ({1}) -> {2}" -f $inPath, $kind, $(if ($outs.Count) { $outs -join ", " } else { "no files (a report only)" }))
}

# ---- load: the C# batch ----
$sw = [Diagnostics.Stopwatch]::StartNew()
if ($env:PLUTO_DLL) { Add-Type -Path $env:PLUTO_DLL; Say "load" ("{0} (PLUTO_DLL: a prebuilt assembly, for development)" -f $env:PLUTO_DLL) }
else {
    . (Join-Path (Join-Path (Split-Path -Parent $PSScriptRoot) "lib") "Config.ps1")
    Say "load" ("scripts\lib + scripts\arms compiled ({0:0.0}s)" -f $sw.Elapsed.TotalSeconds)
}

try {
    if ($Probe) {
        $v22 = [IO.Path]::Combine($dir, "probe-v22.s2k"); $v26 = [IO.Path]::Combine($dir, "probe-v26.s2k")
        [FrameProbe]::Write($v22, "22.0.0")
        [FrameProbe]::Write($v26, "26.3.0")
        Say "write" ("{0} and {1}: import the one for your SAP version, then export it again as {2}" -f $v22, $v26, [IO.Path]::Combine($dir, "probe.sap-export.s2k"))
        Write-Output ("RESULT {""kind"":""probe"",""files"":[" + (Q $v22) + "," + (Q $v26) + "]}")
        exit 0
    }
    if ($ProbeCheck) {
        $lines = [FrameProbe]::Check($probePath, $exportPath)
        $report = $exportPath + ".check.md"
        [IO.File]::WriteAllText($report, ([string]::Join("`n", $lines.ToArray()) + "`n"), (New-Object Text.UTF8Encoding($false)))
        Say "check" $lines[2]
        Say "write" ("{0}: the field-by-field report" -f $report)
        Write-Output ("RESULT {""kind"":""probecheck"",""report"":" + (Q $report) + "}")
        exit 0
    }

    # ---- read ----
    $identical = $null
    if ($kind -eq "s2k") {
        $bytes = [IO.File]::ReadAllBytes($inPath)
        $doc = [S2kDoc]::Parse($bytes)
        $model = [FrameS2k]::Read($doc)
        $back = [FrameS2k]::ToBytes($model)
        $identical = [S2kDoc]::SameBytes($bytes, $back)
        if ($identical) { Say "read" ("{0}: read losslessly (written back byte for byte)" -f $inPath) }
        else { Say "warn" ("{0}: written back, it is NOT the same bytes; outputs keep the model, check them before use" -f $inPath) }
    } else {
        $model = [FrameModelJson]::Read([IO.File]::ReadAllText($inPath))
        Say "read" ("{0}: the frame model" -f $inPath)
    }
    $counts = "{0} joints, {1} frames, {2} sections, {3} materials, {4} link properties, {5} links, {6} groups, {7} load patterns, {8} loads, {9} cases, {10} combinations; units {11}" -f `
        $model.Joints.Count, $model.Frames.Count, $model.Sections.Count, $model.Materials.Count, $model.LinkProps.Count, $model.Links.Count, $model.Groups.Count, `
        $model.Patterns.Count, $model.Loads.Count, $model.Cases.Count, $model.Combos.Count, $model.Units
    Say "model" $counts
    $hash = [FrameModelJson]::Hash($model)

    # ---- write ----
    if ($S2k) {
        $bytes = [FrameS2k]::ToBytes($model)
        if ((Test-Path -LiteralPath $s2kFile -PathType Leaf) -and -not $Force) {
            $old = [IO.File]::ReadAllBytes($s2kFile)
            if (-not [S2kDoc]::SameBytes($old, $bytes)) { Fail ("Build-Frame: {0} exists with other contents (SAP or someone changed it?): pass -Force to replace it, or -Out <another name>" -f $s2kFile) }
        }
        [IO.File]::WriteAllBytes($s2kFile, $bytes)
        Say "write" ("{0}: for SAP2000 (File > Import > SAP2000 .s2k)" -f $s2kFile)
    }
    if ($Json) {
        [IO.File]::WriteAllText($jsonFile, [FrameModelJson]::Write($model), (New-Object Text.UTF8Encoding($false)))
        Say "write" ("{0}: the whole model" -f $jsonFile)
    }
    $res = "RESULT {""input"":" + (Q $inPath) + ",""kind"":" + (Q $kind) + ",""model"":" + (Q $hash)
    $res += ",""identical"":" + $(if ($identical -eq $null) { "null" } elseif ($identical) { "true" } else { "false" })
    $res += ",""s2k"":" + $(if ($S2k) { Q $s2kFile } else { "null" }) + ",""json"":" + $(if ($Json) { Q $jsonFile } else { "null" })
    $res += ",""counts"":" + (Q $counts) + "}"
    Write-Output $res
    exit 0
} catch {
    $e = $_.Exception; while ($e.InnerException) { $e = $e.InnerException }
    Fail $e.Message
}
