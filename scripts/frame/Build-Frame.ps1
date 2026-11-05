# The frame model runner (SAP2000 frame models: piping, ducting and steel as frames, with links, loads and cases).
# Windows PowerShell 5.1; no Python, no SAP2000 needed (except -Open, later, which runs scripts\sap\Run-Sap.ps1 in a
# process of its own). vault/arms/frame-workbook.md.
#   in    -In <file>:
#           .s2k / .$2k   a SAP2000 text file (File > Export > SAP2000 .s2k). Read losslessly (FrameS2k): everything
#                         the model does not edit -- areas, design data, analysis options -- is carried as written,
#                         and every import checks that writing it back gives the source byte for byte, and says so
#           .xlsx         a frame workbook (FrameWorkbook): its rows applied to the model it was written from
#           .frame.json   a model saved with -Json
#   out   -S2k      <base>.s2k for SAP2000 (File > Import > SAP2000 .s2k). Never written over the input file. An
#                   existing <base>.s2k is replaced only when this runner wrote it (the stamp, <base>.build.json, says
#                   so), when it is the model's own source, or when nothing in it would change; else -Force.
#         -Xlsx     <base>.xlsx, the frame workbook, and its launchers ("<base> - preview.cmd", "<base> - s2k.cmd",
#                   "<base> - open in SAP.cmd", "<base> - std.cmd")
#         -Std      <base>.std for STAAD.Pro (FrameStd; vault/format/frame-to-staad.md): Y up, a BETA per member, STAAD's
#                   table for W shapes, exact properties for the rest, patterns as reference loads and cases as primary
#                   loads. Exact or refused: what STAAD cannot hold exactly (a nonlinear link, a partly rigid end zone,
#                   an angle's product of inertia ...) refuses the file, every item named on a [refuse] line; what
#                   leaving out cannot change (modal cases, envelopes) is a [notice]. An existing <base>.std is replaced
#                   only when this runner wrote it or nothing in it would change; else -Force (STAAD saves its own edits)
#         -Json     <base>.frame.json, the whole model (FrameModelJson; exact)
#         -Preview  <base>.bin + <base>.features.json for the Pluto viewer, drawn from the very document -S2k writes
#                   (SapToPluto): frames with their sections, local axis angles, offsets and releases; supports,
#                   links, loads by pattern / case / combination, per-frame properties; the build's notes in Checks.
#                   A previous sidecar's user layer (colours, cuts, hand-made groups) is kept. -ModelId names it
#                   (default sap/<name>).
#   -Open           (with -S2k; production machine) imports the .s2k in a SAP2000 of its own: scripts\sap\Run-Sap.ps1
#                   -Open <s2k> -Stamp -NewInstance -KeepOpen in a separate PowerShell (the SAP controller), stamped for
#                   that SAP's version and saved beside it as .sdb; SAP's joint, frame and link counts are compared with
#                   the model's. Not with -Watch.
#   -Watch          with an .xlsx: every save rebuilds the outputs (-Preview when none is given); Ctrl+C stops
#   -NewWorkbook    -Out <name>.xlsx: an empty workbook (-Units "Kip, in, F" by default, -SapVersion 22.0.0) and its
#                   launchers
#   -Refresh        -In <x>.xlsx: the workbook in this version's layout, written over itself with every typed cell
#                   kept (the previous file stays as <x>.xlsx.bak)
#   -Out <base>     default beside the input, same name with spaces -> underscores ("<name>_edit" for a workbook beside
#                   the <name>.s2k it was made from); an existing folder or a path ending in \ or / means
#                   <folder>\<name>; a trailing .s2k or .xlsx is dropped. Outputs inside this repo are refused.
#   -Probe <folder>      writes probe-v22.s2k and probe-v26.s2k (the same probe stamped for SAP 22.0.0 and 26.3.0):
#                        one or more of every .s2k table and field the frame model writes that no SAP export here has
#                        shown yet (FrameProbe). Import the one matching your SAP (File > Import > SAP2000 .s2k; if
#                        Help > About shows another version, edit the Version on the file's PROGRAM CONTROL line to
#                        it), then File > Export > SAP2000 .s2k with every table, to <folder>\probe.sap-export.s2k.
#   -StdProbe <folder>   writes staad-probe.s2k and staad-probe.std (FrameStdProbe): one small model, every part
#                        analysable, for SAP2000 and STAAD.Pro alike. Run both (-SapVersion stamps the .s2k, default
#                        22.0.0) and compare SAP's joint displacements and reactions with STAAD's .anl: it confirms the
#                        BETA sign, gradient signs, spring units, inclined supports and offsets (vault/format/frame-to-staad.md).
#   -ProbeCheck <export> compares SAP's export with the probe it came from (-ProbeFile, default probe-v22.s2k beside
#                        the export) field by field and writes <export>.check.md: what SAP kept, changed, dropped or
#                        added, and the tables it wrote that the probe did not.
# Every line of progress starts with "[step]"; the last line is "RESULT {json}" ({"error": "..."} with exit code 1).
#   powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File scripts\frame\Build-Frame.ps1 -In "<project dir>\rack.s2k" -Xlsx
#   powershell.exe ... -File scripts\frame\Build-Frame.ps1 -In "<project dir>\rack.xlsx" -Preview -Watch
#   powershell.exe ... -File scripts\frame\Build-Frame.ps1 -In "<project dir>\rack.xlsx" -S2k -Preview
#   powershell.exe ... -File scripts\frame\Build-Frame.ps1 -In "<project dir>\rack.xlsx" -Std
#   powershell.exe ... -File scripts\frame\Build-Frame.ps1 -NewWorkbook -Out "<project dir>\duct.xlsx" -Units "lb, in, F"
#   powershell.exe ... -File scripts\frame\Build-Frame.ps1 -Probe "<project dir>\probe"
#   powershell.exe ... -File scripts\frame\Build-Frame.ps1 -ProbeCheck "<project dir>\probe\probe.sap-export.s2k"
param(
    [string]$In,
    [string]$Out,
    [switch]$S2k,
    [switch]$Std,
    [switch]$Xlsx,
    [switch]$Json,
    [switch]$Preview,
    [switch]$Watch,
    [switch]$Open,
    [switch]$NewWorkbook,
    [switch]$Refresh,
    [string]$Units,
    [string]$SapVersion,
    [string]$ModelId,
    [switch]$Force,
    [string]$Probe,
    [string]$StdProbe,
    [string]$ProbeCheck,
    [string]$ProbeFile
)
$ErrorActionPreference = "Stop"
function Say([string]$step, [string]$text) { Write-Output ("[{0}] {1}" -f $step.PadRight(7), $text) }
function Q([string]$s) { if ([string]::IsNullOrEmpty($s)) { return "null" } return (ConvertTo-Json -InputObject $s -Compress) }
function Full([string]$p) { return (Resolve-Path -LiteralPath $p).ProviderPath }
function Fail([string]$msg) { Say "error" $msg; Write-Output ("RESULT {""error"":" + (Q $msg) + "}"); exit 1 }
# every input is opened shared: Excel keeps an open workbook locked for writing, and a plain OpenRead / ReadAllBytes
# asks for that lock too, so it fails while the workbook is open (the live preview's whole use)
function OpenShared([string]$p) { return New-Object IO.FileStream($p, [IO.FileMode]::Open, [IO.FileAccess]::Read, ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)) }
function FileSha([string]$p) { $fs = OpenShared $p; try { $h = [Security.Cryptography.SHA256]::Create(); try { return (($h.ComputeHash($fs) | ForEach-Object { $_.ToString("x2") }) -join "") } finally { $h.Dispose() } } finally { $fs.Dispose() } }
function ReadShared([string]$p) { $fs = OpenShared $p; try { $ms = New-Object IO.MemoryStream; $fs.CopyTo($ms); return ,$ms.ToArray() } finally { $fs.Dispose() } }
function ReadSharedText([string]$p) { $fs = OpenShared $p; try { $sr = New-Object IO.StreamReader($fs, $true); try { return $sr.ReadToEnd() } finally { $sr.Dispose() } } finally { $fs.Dispose() } }
function Innermost($err) { $e = $err.Exception; while ($e.InnerException) { $e = $e.InnerException }; return $e.Message }
# a build's error with the step it came from when it is not one of Pluto's own (which name their sheet and cell)
$script:step = ""
function BuildError($err)
{
    $e = $err.Exception; while ($e.InnerException) { $e = $e.InnerException }
    if ($e -is [InvalidOperationException] -or -not $script:step) { return $e.Message }
    return ("{0} ({1}: an unexpected error, not a check; the workbook's other problems are reported once it is fixed)" -f $e.Message, $script:step)
}
$repoRoot = (Resolve-Path -LiteralPath (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))).ProviderPath.TrimEnd('\', '/')
function InRepo([string]$p) { return $p.Equals($repoRoot, [StringComparison]::OrdinalIgnoreCase) -or $p.StartsWith($repoRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) }

# ---- check: arguments and the output location, before the C# batch loads ----
$modes = @($In, $Probe, $StdProbe, $ProbeCheck) | Where-Object { $_ }
if ($NewWorkbook) {
    if ($modes.Count -ne 0 -or -not $Out) { Fail "Build-Frame: -NewWorkbook takes -Out <name>.xlsx (and -Units, -SapVersion) only" }
} elseif ($modes.Count -ne 1) { Fail "Build-Frame: give one of -In <.s2k | .xlsx | .frame.json>, -NewWorkbook -Out <name>.xlsx, -Probe <folder>, -StdProbe <folder>, -ProbeCheck <SAP's export .s2k>" }
if ($Probe) {
    $dir = [IO.Path]::GetFullPath($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Probe))
    if (InRepo $dir) { Fail ("Build-Frame: refusing to write the probe inside the Pluto repo ({0}); pass -Probe <project dir>\probe" -f $repoRoot) }
    $null = [IO.Directory]::CreateDirectory($dir)
}
if ($StdProbe) {
    $sdir = [IO.Path]::GetFullPath($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($StdProbe))
    if (InRepo $sdir) { Fail ("Build-Frame: refusing to write the STAAD probe inside the Pluto repo ({0}); pass -StdProbe <project dir>\staad-probe" -f $repoRoot) }
    $null = [IO.Directory]::CreateDirectory($sdir)
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
$kind = $null; $inPath = $null
if ($In) {
    if (-not (Test-Path -LiteralPath $In -PathType Leaf)) { Fail "Build-Frame: input not found: $In" }
    $inPath = Full $In
    $ext = [IO.Path]::GetExtension($inPath).ToLowerInvariant()
    if ($inPath.ToLowerInvariant().EndsWith(".frame.json")) { $kind = "json" }
    elseif ($ext -eq ".s2k" -or $ext -eq ".`$2k") { $kind = "s2k" }
    elseif ($ext -eq ".xlsx") { $kind = "xlsx" }
    else { Fail ("Build-Frame: {0}: give a SAP2000 .s2k (or .`$2k), a frame workbook .xlsx or a .frame.json model" -f $inPath) }
    if ($Refresh -and $kind -ne "xlsx") { Fail "Build-Frame: -Refresh takes -In <workbook>.xlsx" }
    if ($Watch -and $kind -ne "xlsx") { Fail "Build-Frame: -Watch follows a workbook: -In <name>.xlsx" }
    if ($Watch -and -not ($S2k -or $Std -or $Json -or $Preview)) { $Preview = $true }
    if ($Open -and $Watch) { Fail "Build-Frame: -Open is not for -Watch (it would start SAP on every save)" }
    if ($Open) { $S2k = $true }
}
$jobName = $null
if ($inPath) { $jobName = ([IO.Path]::GetFileName($inPath) -replace '\.frame\.json$', '' -replace '\.[^.]+$', '') -replace ' ', '_' }
if ($Out) {
    $outBase = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Out)
    if (($Out -match '[\\/]$') -or (Test-Path -LiteralPath $outBase -PathType Container)) { $outBase = [IO.Path]::Combine($outBase, $(if ($jobName) { $jobName } else { "frame" })) }
    elseif ($outBase -match '\.(s2k|xlsx)$') { $outBase = $outBase.Substring(0, $outBase.LastIndexOf('.')) }
} elseif ($inPath) {
    $outBase = [IO.Path]::Combine([IO.Path]::GetDirectoryName($inPath), $jobName)
    # a workbook beside an .s2k of its name that this runner did not write (SAP's export it was made from) writes
    # <name>_edit.*, so the SAP job is never written over
    if ($kind -eq "xlsx" -and (Test-Path -LiteralPath ($outBase + ".s2k") -PathType Leaf)) {
        $mine = $false
        try { $st0 = Get-Content -LiteralPath ($outBase + ".build.json") -Raw | ConvertFrom-Json; $mine = $st0.s2k -and $st0.s2k.sha256 -eq (FileSha ($outBase + ".s2k")) } catch { }
        if (-not $mine) { $outBase = $outBase + "_edit" }
    }
}
if ($outBase) {
    $outBase = [IO.Path]::GetFullPath($outBase)
    $s2kFile = $outBase + ".s2k"; $stdFile = $outBase + ".std"; $jsonFile = $outBase + ".frame.json"; $binFile = $outBase + ".bin"; $scFile = $outBase + ".features.json"; $stampFile = $outBase + ".build.json"
    $xlsxFile = $(if ($NewWorkbook -or $Xlsx) { $outBase + ".xlsx" } else { $inPath })
}
if ($S2k -or $Std -or $Json -or $Preview -or $Xlsx -or $NewWorkbook) {
    if (InRepo $outBase) { Fail ("Build-Frame: refusing to write {0}.* inside the Pluto repo ({1}): project outputs never land in Pluto. Pass -Out <project dir>\<name>." -f $outBase, $repoRoot) }
    foreach ($pair in @(@($S2k, $s2kFile, "-S2k"), @($Json, $jsonFile, "-Json"), @($Xlsx, ($outBase + ".xlsx"), "-Xlsx"))) {
        if ($pair[0] -and $inPath -and $pair[1].Equals($inPath, [StringComparison]::OrdinalIgnoreCase)) { Fail ("Build-Frame: {0} would write over the input {1}; pass -Out <another name>" -f $pair[2], $inPath) }
    }
    $outDir = [IO.Path]::GetDirectoryName($outBase)
    if ($outDir) { $null = [IO.Directory]::CreateDirectory($outDir) }
}
$outs = @(); if ($S2k) { $outs += $s2kFile }; if ($Std) { $outs += $stdFile }; if ($Xlsx) { $outs += ($outBase + ".xlsx") }; if ($Json) { $outs += $jsonFile }; if ($Preview) { $outs += $binFile; $outs += $scFile }
if ($inPath -and -not $Refresh) { Say "check" ("{0} ({1}) -> {2}" -f $inPath, $kind, $(if ($outs.Count) { $outs -join ", " } else { "no files (a report only)" })) }

# ---- load: the C# batch ----
$sw = [Diagnostics.Stopwatch]::StartNew()
if ($env:PLUTO_DLL) { Add-Type -Path $env:PLUTO_DLL; Say "load" ("{0} (PLUTO_DLL: a prebuilt assembly, for development)" -f $env:PLUTO_DLL) }
else {
    . (Join-Path (Join-Path (Split-Path -Parent $PSScriptRoot) "lib") "Config.ps1")
    Say "load" ("scripts\lib + scripts\arms compiled ({0:0.0}s)" -f $sw.Elapsed.TotalSeconds)
}

# ---- -Probe, -ProbeCheck ----
try {
    if ($Probe) {
        $v22 = [IO.Path]::Combine($dir, "probe-v22.s2k"); $v26 = [IO.Path]::Combine($dir, "probe-v26.s2k")
        [FrameProbe]::Write($v22, "22.0.0")
        [FrameProbe]::Write($v26, "26.3.0")
        Say "write" ("{0} and {1}: import the one for your SAP version, then export it again as {2}" -f $v22, $v26, [IO.Path]::Combine($dir, "probe.sap-export.s2k"))
        Write-Output ("RESULT {""kind"":""probe"",""files"":[" + (Q $v22) + "," + (Q $v26) + "]}")
        exit 0
    }
    if ($StdProbe) {
        $pnotes = New-Object 'System.Collections.Generic.List[string]'
        $files = [FrameStdProbe]::Write($sdir, $(if ($SapVersion) { $SapVersion } else { "22.0.0" }), $pnotes)
        foreach ($n in $pnotes) { Say "notice" ("std: " + $n) }
        Say "write" ("{0}: import it in SAP2000 (edit its Version to Help > About's if SAP asks), run all cases, export joint displacements and reactions" -f $files[0])
        Say "write" ("{0}: open it in STAAD.Pro and run it; its .anl holds the same results" -f $files[1])
        Write-Output ("RESULT {""kind"":""stdprobe"",""files"":[" + (Q $files[0]) + "," + (Q $files[1]) + "]}")
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
} catch { Fail (Innermost $_) }

# ---- -NewWorkbook: an empty model's workbook, written once ----
if ($NewWorkbook) {
    try {
        if ((Test-Path -LiteralPath $xlsxFile) -and -not $Force) { throw ("{0} exists: pick another -Out, or -Force to replace it" -f $xlsxFile) }
        $model = [FrameWorkbook]::Template($Units, $SapVersion)
        $run = [FrameWorkbookRun]::For($xlsxFile, $repoRoot, $null)
        [FrameWorkbook]::Write($model, $xlsxFile, "a new model", $run)
        Say "write" ("{0}: an empty frame model ({1}) to edit in Excel; then Build-Frame.ps1 -In ""{0}"" -Preview" -f $xlsxFile, $model.Units)
        foreach ($l in $run.WriteLaunchers()) { Say "write" ("{0}: double-click it, or use the Start sheet's link" -f $l) }
        Write-Output ("RESULT {""kind"":""new"",""xlsx"":" + (Q $xlsxFile) + ",""units"":" + (Q $model.Units) + "}")
        exit 0
    } catch { Fail (Innermost $_) }
}

# ---- -Refresh: the workbook read (edits and all), then written over itself in this version's layout ----
if ($Refresh) {
    try {
        # the layout comes from the hidden base and the typed cells, so a workbook whose sheets do not build (yet)
        # refreshes too: its problems are said, and its new checks then show where they are
        try {
            $wb = [FrameWorkbook]::Read($inPath)
            Say "read" $wb.Summary()
            foreach ($w in $wb.Warnings) { Say "warn" $w }
        } catch {
            Say "warn" ("the sheets do not build yet, refreshed all the same (fix them in the refreshed workbook): {0}" -f (Innermost $_))
            $wb = [FrameWorkbook]::ReadBase($inPath)
        }
        $source = $wb.SourceName; if (-not $source) { $source = [IO.Path]::GetFileName($inPath) }
        $run = [FrameWorkbookRun]::For($inPath, $repoRoot, $null)
        $tmp = $inPath + ".refresh.tmp"
        $notes = New-Object 'System.Collections.Generic.List[string]'
        [IO.File]::WriteAllBytes($tmp, [FrameWorkbook]::RefreshBytes($inPath, $wb, $source, $run, $notes))
        foreach ($n in $notes) { Say "keep" $n }
        $bak = $inPath + ".bak"
        try { [IO.File]::Replace($tmp, $inPath, $bak) }
        catch { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue; throw ("cannot replace {0} ({1}). Close it in Excel and run again." -f $inPath, $_.Exception.Message) }
        Say "write" ("{0}: refreshed to this version's layout; the previous file is {1}" -f $inPath, $bak)
        foreach ($l in $run.WriteLaunchers()) { Say "write" ("{0}: double-click it, or use the Start sheet's link" -f $l) }
        Write-Output ("RESULT {""kind"":""refresh"",""xlsx"":" + (Q $inPath) + ",""backup"":" + (Q $bak) + "}")
        exit 0
    } catch { Fail (Innermost $_) }
}

function Read-Stamp {
    if (-not (Test-Path -LiteralPath $stampFile -PathType Leaf)) { return $null }
    try { return (Get-Content -LiteralPath $stampFile -Raw | ConvertFrom-Json) } catch { return $null }
}

function Write-Stamp($s2kPart, $previewPart, $stdPart) {
    $o = Read-Stamp
    $sj = "null"; $pj = "null"; $tj = "null"
    if ($s2kPart) { $sj = $s2kPart } elseif ($o -and $o.s2k) { $sj = (ConvertTo-Json -InputObject $o.s2k -Compress) }
    if ($previewPart) { $pj = $previewPart } elseif ($o -and $o.preview) { $pj = (ConvertTo-Json -InputObject $o.preview -Compress) }
    if ($stdPart) { $tj = $stdPart } elseif ($o -and $o.std) { $tj = (ConvertTo-Json -InputObject $o.std -Compress) }
    [IO.File]::WriteAllText($stampFile, "{""format"":""pluto-frame-build"",""s2k"":" + $sj + ",""preview"":" + $pj + ",""std"":" + $tj + "}", (New-Object Text.UTF8Encoding($false)))
}

# an existing .s2k may be replaced when this runner wrote it, when it is the model's own source, or when nothing in it
# would change; otherwise it was changed outside (SAP, a person) and -Force is needed
function Check-Target([byte[]]$bytes, $model) {
    if (-not (Test-Path -LiteralPath $s2kFile -PathType Leaf)) { return }
    $st = Read-Stamp
    if ($st -and $st.s2k -and $st.s2k.sha256 -eq (FileSha $s2kFile)) { return }
    $old = ReadShared $s2kFile
    if ([S2kDoc]::SameBytes($old, $bytes)) { return }
    if ($model.Source -and [S2kDoc]::SameBytes($old, $model.Source.ToBytes())) { return }
    if ($Force) { Say "warn" ("-Force: replacing {0}, changed outside this runner" -f $s2kFile); return }
    $a = [Text.Encoding]::GetEncoding(28591).GetString($old) -split "`r?`n"; $b = [Text.Encoding]::GetEncoding(28591).GetString($bytes) -split "`r?`n"
    $only = @(Compare-Object -ReferenceObject $a -DifferenceObject $b | Where-Object { $_.InputObject.Trim().Length -gt 0 } | Select-Object -First 6)
    foreach ($d in $only) { Say "target" ("{0} {1}" -f $(if ($d.SideIndicator -eq "<=") { "only in the file:" } else { "only in this model:" }), $d.InputObject.Trim()) }
    throw ("{0} was changed since this runner wrote it (by SAP or by hand); the first differences are listed above. Make the workbook again from it (-In {1} -Xlsx), write elsewhere (-Out), or -Force to replace it." -f $s2kFile, [IO.Path]::GetFileName($s2kFile))
}

# an existing .std may be replaced when this runner wrote it or nothing in it would change; STAAD.Pro saves its own
# edits into the .std it opened, so anything else needs -Force
function Check-Std([byte[]]$bytes) {
    if (-not (Test-Path -LiteralPath $stdFile -PathType Leaf)) { return }
    $st = Read-Stamp
    if ($st -and $st.std -and $st.std.sha256 -eq (FileSha $stdFile)) { return }
    $old = ReadShared $stdFile
    if ([S2kDoc]::SameBytes($old, $bytes)) { return }
    if ($Force) { Say "warn" ("-Force: replacing {0}, changed outside this runner" -f $stdFile); return }
    throw (New-Object InvalidOperationException(("{0} was changed since this runner wrote it (STAAD.Pro saves its edits into it). Write elsewhere (-Out), or -Force to replace it." -f $stdFile)))
}

function Build-Once {
    $script:step = "reading " + [IO.Path]::GetFileName($inPath)
    $script:sw.Reset(); $script:sw.Start()
    $now = [DateTime]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
    $inSha = FileSha $inPath
    $identical = $null
    $warnings = @()
    if ($kind -eq "s2k") {
        $bytes = ReadShared $inPath
        $doc = [S2kDoc]::Parse($bytes)
        $model = [FrameS2k]::Read($doc)
        $identical = [S2kDoc]::SameBytes($bytes, [FrameS2k]::ToBytes($model))
        if ($identical) { Say "read" ("{0}: read losslessly (written back byte for byte)" -f $inPath) }
        else { Say "warn" ("{0}: written back, it is NOT the same bytes; outputs keep the model, check them before use" -f $inPath) }
    } elseif ($kind -eq "xlsx") {
        $wb = [FrameWorkbook]::Read($inPath)
        $model = $wb.Model
        Say "read" ("{0}: {1}" -f $inPath, $wb.Summary())
        foreach ($w in $wb.Warnings) { Say "warn" $w; $warnings += $w }
    } else {
        $model = [FrameModelJson]::Read((ReadSharedText $inPath))
        Say "read" ("{0}: the frame model" -f $inPath)
    }
    $counts = "{0} joints, {1} frames, {2} sections, {3} materials, {4} link properties, {5} links, {6} groups, {7} load patterns, {8} loads, {9} cases, {10} combinations; units {11}" -f `
        $model.Joints.Count, $model.Frames.Count, $model.Sections.Count, $model.Materials.Count, $model.LinkProps.Count, $model.Links.Count, $model.Groups.Count, `
        $model.Patterns.Count, $model.Loads.Count, $model.Cases.Count, $model.Combos.Count, $model.Units
    Say "model" $counts
    $hash = [FrameModelJson]::Hash($model)
    $notes = New-Object 'System.Collections.Generic.List[string]'
    $script:step = "writing the .s2k"
    $doc = [FrameS2k]::ToDoc($model, $notes)
    foreach ($n in $notes) { Say "notice" $n }

    if ($S2k) {
        $bytes = $doc.ToBytes()
        Check-Target $bytes $model
        [IO.File]::WriteAllBytes($s2kFile, $bytes)
        Say "write" ("{0}: for SAP2000 (File > Import > SAP2000 .s2k)" -f $s2kFile)
        Write-Stamp ("{""sha256"":" + (Q (FileSha $s2kFile)) + ",""model"":" + (Q $hash) + ",""input"":" + (Q $inPath) + ",""inputSha256"":" + (Q $inSha) + ",""written"":" + (Q $now) + "}") $null
    }
    $stdNotes = New-Object 'System.Collections.Generic.List[string]'
    if ($Std) {
        $script:step = "writing the STAAD .std"
        try { $text = [FrameStd]::ToText($model, [IO.Path]::GetFileName($inPath), $stdNotes) }
        catch {
            $e = $_.Exception; while ($e.InnerException) { $e = $e.InnerException }
            if ($e -isnot [FrameStdRefusal]) { throw }
            foreach ($i in $e.Items) { Say "refuse" $i }
            throw (New-Object InvalidOperationException(("STAAD: the {0} item(s) above cannot be written exactly; {1} is not written" -f $e.Items.Count, $stdFile)))
        }
        foreach ($n in $stdNotes) { Say "notice" ("std: " + $n) }
        $bytes = (New-Object Text.UTF8Encoding($false)).GetBytes($text)
        Check-Std $bytes
        [IO.File]::WriteAllBytes($stdFile, $bytes)
        Say "write" ("{0}: for STAAD.Pro (File > Open)" -f $stdFile)
        Write-Stamp $null $null ("{""sha256"":" + (Q (FileSha $stdFile)) + ",""model"":" + (Q $hash) + ",""input"":" + (Q $inPath) + ",""inputSha256"":" + (Q $inSha) + ",""written"":" + (Q $now) + "}")
    }
    if ($Open) {
        # the SAP controller in a process of its own (its Add-Type batch holds SAP2000v1.dll), on a SAP of its own
        $runSap = [IO.Path]::Combine($repoRoot, "scripts", "sap", "Run-Sap.ps1")
        $ps = [IO.Path]::Combine($env:SystemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe")
        Say "open" ("SAP2000: importing {0} in a SAP of its own ..." -f $s2kFile)
        $lines = @(& $ps -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $runSap -Open $s2kFile -Stamp -NewInstance -KeepOpen 2>&1 | ForEach-Object { "$_" })
        foreach ($l in $lines) { if ($l -notlike "RESULT *") { Write-Output ("   sap: " + $l) } }
        $last = $lines | Where-Object { $_ -like "RESULT *" } | Select-Object -Last 1
        if (-not $last) { throw "SAP2000: Run-Sap.ps1 gave no result (see its lines above)" }
        $sr = $last.Substring(7) | ConvertFrom-Json
        if ($sr.error) { throw ("SAP2000: " + $sr.error) }
        $want = @{ joints = $model.Joints.Count; frames = $model.Frames.Count; links = $model.Links.Count }
        $miss = @($want.Keys | Where-Object { $sr.counts.$_ -ne $want[$_] } | ForEach-Object { "{0}: SAP {1}, the model {2}" -f $_, $sr.counts.$_, $want[$_] })
        if ($miss.Count -gt 0) { Say "warn" ("SAP2000 holds other counts than the model: {0}. A table SAP did not read? Check its import log." -f ($miss -join "; ")) }
        else { Say "open" ("SAP2000 {0} holds the model ({1} joints, {2} frames, {3} links): {4}" -f $sr.version, $want.joints, $want.frames, $want.links, $sr.held) }
    }
    if ($Xlsx) {
        $xf = $outBase + ".xlsx"
        $run = [FrameWorkbookRun]::For($xf, $repoRoot, $inPath)
        [FrameWorkbook]::Write($model, $xf, [IO.Path]::GetFileName($inPath), $run)
        Say "write" ("{0}: the frame workbook" -f $xf)
        foreach ($l in $run.WriteLaunchers()) { Say "write" ("{0}: double-click it, or use the Start sheet's link" -f $l) }
    }
    if ($Json) {
        [IO.File]::WriteAllText($jsonFile, [FrameModelJson]::Write($model), (New-Object Text.UTF8Encoding($false)))
        Say "write" ("{0}: the whole model" -f $jsonFile)
    }
    if ($Preview) {
        $script:step = "building the viewer preview"
        # the very document -S2k writes, exported to temporary names (the sidecar's user layer merged in), then moved
        $tmp = $outBase + ".~new"
        if (Test-Path -LiteralPath $scFile -PathType Leaf) { Copy-Item -LiteralPath $scFile -Destination ($tmp + ".features.json") -Force }
        $pv = New-Object FramePreview
        $pv.SourceJson = [PipeSidecar]::SourceOf($inPath, $kind, $hash)
        if ($identical -eq $false) { $null = $pv.AddMessage("warning", "s2k-not-identical", "Written back, the .s2k is not the source byte for byte: check the outputs before use") }
        foreach ($w in $warnings) { $null = $pv.AddMessage("warning", "workbook", $w) }
        foreach ($n in $notes) { $null = $pv.AddMessage("notice", "s2k-write", $n) }
        $mid = $(if ($ModelId) { $ModelId } else { "sap/" + [IO.Path]::GetFileName($outBase) })
        $outlines = [IO.Path]::ChangeExtension($inPath, ".outlines.txt")
        try {
            $px = [SapToPluto]::Export($doc, $null, $outlines, $tmp, $mid, $false, $pv)
            Move-Item -LiteralPath ($tmp + ".bin") -Destination $binFile -Force
            Move-Item -LiteralPath ($tmp + ".features.json") -Destination $scFile -Force
        } finally {
            # a preview that failed leaves no temporary files (the last good preview stays)
            foreach ($f in @(($tmp + ".bin"), ($tmp + ".features.json"))) { if (Test-Path -LiteralPath $f) { Remove-Item -LiteralPath $f -Force -ErrorAction SilentlyContinue } }
        }
        foreach ($w in $px.Warnings) { Say "warn" $w }
        Say "write" ("{0} + .features.json: the viewer preview ({1} supports, {2} links, {3} loads; {4} frames released, {5} turned; {6:0.0}s)" -f `
            $binFile, $px.Supports, $px.Links, $px.Loads, $px.FramesReleased, $px.FramesTurned, $script:sw.Elapsed.TotalSeconds)
        Write-Stamp $null ("{""model"":" + (Q $hash) + ",""input"":" + (Q $inPath) + ",""inputSha256"":" + (Q $inSha) + ",""written"":" + (Q $now) + "}")
    }
    $res = "RESULT {""input"":" + (Q $inPath) + ",""kind"":" + (Q $kind) + ",""model"":" + (Q $hash)
    $res += ",""identical"":" + $(if ($identical -eq $null) { "null" } elseif ($identical) { "true" } else { "false" })
    $res += ",""s2k"":" + $(if ($S2k) { Q $s2kFile } else { "null" }) + ",""std"":" + $(if ($Std) { Q $stdFile } else { "null" }) + ",""xlsx"":" + $(if ($Xlsx) { Q ($outBase + ".xlsx") } else { "null" }) + ",""json"":" + $(if ($Json) { Q $jsonFile } else { "null" })
    $res += ",""bin"":" + $(if ($Preview) { Q $binFile } else { "null" }) + ",""sidecar"":" + $(if ($Preview) { Q $scFile } else { "null" })
    $res += ",""notices"":" + ($notes.Count + $stdNotes.Count) + ",""counts"":" + (Q $counts) + "}"
    Write-Output $res
}

if (-not $Watch) {
    try { Build-Once } catch { Fail (BuildError $_) }
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
                $msg = BuildError $_
                Say "error" $msg
                Say "watch" "the last good outputs stay; fix the input and save again"
                Write-Output ("RESULT {""error"":" + (Q $msg) + "}")
            }
        }
    }
    Start-Sleep -Milliseconds 1000
}
