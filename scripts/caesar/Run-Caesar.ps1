# The CAESAR II runner: a CAESAR II neutral file (.cii) [+ CAESAR's output workbook] -> a Pluto viewer model,
# <base>.bin + <base>.features.json, through CaesarToPluto (scripts\arms\CaesarToPluto.cs: CaesarNeutralReader +
# CaesarGeometry + CaesarResults + RawViewerWriter + FeaturesSidecar). Windows PowerShell 5.1; no Python, no
# CAESAR II needed. Steps, in this order:
#
#   check    -Neutral must exist (-Results too, when given); the output base must lie OUTSIDE this repo -- project
#            outputs never land in Pluto, so an output inside it (the default beside a .cii kept in the repo
#            included) is refused before anything loads
#   load     the C# batch: scripts\lib\Config.ps1 (one Add-Type of scripts\lib + scripts\arms)
#   results  -Results <xlsx|csv|xls|xlsb>: CAESAR's output reports, one per tab (displacements, restraint summary,
#            local element forces, code stresses; other tabs are listed as skipped). An .xlsx or .csv is read
#            directly, also while it is open in Excel. An .xls (Excel 97-2003) or .xlsb goes through Excel first:
#            opened read-only with macros, events and prompts off, saved as a temporary .xlsx under %TEMP%, which
#            is deleted after the export (needs Excel on the machine; otherwise save the workbook as .xlsx).
#   export   CaesarToPluto: the whole pipe (bends as chords, reducers and valve bow-ties tapered, display sizes for
#            flanges / rigids / expansion joints) + sidecar groups (pipe sizes, components, line numbers, SIF /
#            tees, one node group per restraint combination) + the sidecar "supports" items the viewer draws as
#            support symbols. With results: per load case, displacements (contours and the deformed shape),
#            local element forces and code stresses on the beams, and the restraint loads at each support node
#            (they colour the support symbols). Without -Results, or when no report tab is recognised, the
#            export is geometry only. A previous <base>.features.json is merged: colour / visibility edits,
#            hand-made groups and supports, and section cuts survive a re-export.
#
# -Out <base>: writes <base>.bin + <base>.features.json. Default: beside the .cii, same name with spaces ->
#   underscores. An existing folder, or any path ending in \ or /, means <folder>\<name>; a trailing .bin is dropped.
# -ModelId: id written to the binary and the sidecar (default caesar/<name>).
# -ArcStepDeg: bend chord step in degrees (default 7.5, kept within 0.5 .. 45).
#
# Every line of progress starts with "[step]"; the last line is "RESULT {json}" (machine-readable: bin, features,
# load cases, counts, warnings). Open the .bin TOGETHER with the .features.json in the viewer (drop both files, or
# viewer\index.html?bin=<url>&features=<url>): the groups and the support symbols live in the sidecar.
#   powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File scripts\caesar\Run-Caesar.ps1 -Neutral "<project dir>\job.cii" -Results "<project dir>\job.xlsx" -Out "<project dir>\viewer\job"
param(
    [Parameter(Mandatory = $true)][string]$Neutral,
    [string]$Results,
    [string]$Out,
    [string]$ModelId,
    [double]$ArcStepDeg = 7.5
)
$ErrorActionPreference = "Stop"
function Say([string]$step, [string]$text) { Write-Output ("[{0}] {1}" -f $step.PadRight(7), $text) }

# An .xls / .xlsb -> a temporary .xlsx through Excel (late-bound COM): read-only, no macros, no events, no
# prompts; every COM object released and Excel quit, also on failure (a half-written copy is removed).
function Convert-WorkbookToXlsx([string]$path) {
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ("pluto-caesar-" + [Guid]::NewGuid().ToString("N") + ".xlsx")   # %TEMP%
    $xl = $null; $books = $null; $wb = $null
    try {
        try { $xl = New-Object -ComObject Excel.Application }
        catch { throw ("Run-Caesar: {0} needs Excel to be read (an .xls / .xlsb workbook) and Excel did not start ({1}). Save it as .xlsx and pass that." -f $path, $_.Exception.Message) }
        $xl.Visible = $false
        $xl.DisplayAlerts = $false
        $xl.EnableEvents = $false
        $xl.AutomationSecurity = 3              # msoAutomationSecurityForceDisable: no macro runs
        $books = $xl.Workbooks
        $wb = $books.Open($path, 0, $true)      # UpdateLinks 0, ReadOnly
        $wb.SaveAs($tmp, 51)                    # 51 = xlOpenXMLWorkbook (.xlsx)
        $wb.Close($false)
    } catch {
        if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }
        throw
    } finally {
        $com = [Runtime.InteropServices.Marshal]
        if ($wb) { [void]$com::FinalReleaseComObject($wb) }
        if ($books) { [void]$com::FinalReleaseComObject($books) }
        if ($xl) { $xl.Quit(); [void]$com::FinalReleaseComObject($xl) }
        $wb = $null; $books = $null; $xl = $null
        [GC]::Collect(); [GC]::WaitForPendingFinalizers()
    }
    return $tmp
}

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
if (-not $resultsPath) { Say "check" "no -Results: geometry only (pass CAESAR's output workbook for displacements, forces, stresses and restraint loads)" }

# ---- load: the C# batch ----
$sw = [Diagnostics.Stopwatch]::StartNew()
. (Join-Path (Join-Path (Split-Path -Parent $PSScriptRoot) "lib") "Config.ps1")
Say "load" ("scripts\lib + scripts\arms compiled ({0:0.0}s)" -f $sw.Elapsed.TotalSeconds)

# ---- results: what kind of file (magic bytes, so a renamed file is still read right) ----
$readPath = $resultsPath
$tempXlsx = $null
if ($resultsPath) {
    $head = New-Object byte[] 4
    $fs = [IO.File]::Open($resultsPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]'ReadWrite, Delete')
    try { $n = $fs.Read($head, 0, 4) } finally { $fs.Dispose() }
    $isXls = ($n -ge 4) -and ($head[0] -eq 0xD0) -and ($head[1] -eq 0xCF) -and ($head[2] -eq 0x11) -and ($head[3] -eq 0xE0)
    $isZip = ($n -ge 2) -and ($head[0] -eq 0x50) -and ($head[1] -eq 0x4B)
    $isXlsb = $isZip -and ([IO.Path]::GetExtension($resultsPath) -ieq '.xlsb')
    if ($isXls -or $isXlsb) {
        $sw.Reset(); $sw.Start()
        Say "results" ("{0}: {1} workbook, converting through Excel (read-only, macros off)" -f $resultsPath, $(if ($isXls) { "an Excel 97-2003" } else { "a binary" }))
        $tempXlsx = Convert-WorkbookToXlsx $resultsPath
        $readPath = $tempXlsx
        Say "results" ("converted to a temporary .xlsx ({0:0.0}s), deleted after the export" -f $sw.Elapsed.TotalSeconds)
    } else {
        Say "results" ("{0}: {1}, read directly" -f $resultsPath, $(if ($isZip) { "an .xlsx workbook" } else { "text (CSV)" }))
    }
}

# ---- export ----
$sw.Reset(); $sw.Start()
$opt = New-Object CaesarOptions
$opt.ArcStepDeg = $ArcStepDeg
try {
    $r = [CaesarToPluto]::Export($ciiPath, $readPath, $outBase, $ModelId, $opt)
} finally {
    if ($tempXlsx -and (Test-Path -LiteralPath $tempXlsx)) { Remove-Item -LiteralPath $tempXlsx -Force -ErrorAction SilentlyContinue }
}
if ($tempXlsx) { $r.ResultsPath = $resultsPath }          # report the workbook given, not the temporary copy
foreach ($line in ($r.Summary() -split "`r?`n")) { if ($line.Trim()) { Say "export" $line } }
Say "export" ("done ({0:0.0}s)" -f $sw.Elapsed.TotalSeconds)
foreach ($w in $r.Warnings) { Say "warn" $w }
Say "open" ("{0} TOGETHER with {1} in the viewer (drop both, or index.html?bin=<url>&features=<url>): the groups and the support symbols live in the .features.json" -f $r.BinPath, $r.SidecarPath)
Write-Output ("RESULT " + $r.ToJson())
