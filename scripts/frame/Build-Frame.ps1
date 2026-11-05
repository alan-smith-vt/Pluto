# The frame model runner (SAP2000 frame models: piping, ducting and steel as frames). Windows PowerShell 5.1; no
# Python, no SAP2000 needed (except -Open, which runs scripts\sap\Run-Sap.ps1 in a process of its own).
# Built in phases (vault/arms/frame-workbook.md). Today:
#   -Probe <folder>      writes probe-v22.s2k and probe-v26.s2k (the same probe stamped for SAP 22.0.0 and 26.3.0):
#                        one or more of every .s2k table and field the frame model writes that no SAP export here has
#                        shown yet (FrameProbe). Import the one matching your SAP (File > Import > SAP2000 .s2k; if
#                        Help > About shows another version, edit the Version on the file's PROGRAM CONTROL line to
#                        it), then File > Export > SAP2000 .s2k with every table, to <folder>\probe.sap-export.s2k.
#   -ProbeCheck <export> compares SAP's export with the probe it came from (-ProbeFile, default probe-v22.s2k beside
#                        the export) field by field and writes <export>.check.md: what SAP kept, changed, dropped or
#                        added, and the tables it wrote that the probe did not.
# Every line of progress starts with "[step]"; the last line is "RESULT {json}" ({"error": "..."} with exit code 1).
#   powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File scripts\frame\Build-Frame.ps1 -Probe "<project dir>\probe"
#   powershell.exe ... -File scripts\frame\Build-Frame.ps1 -ProbeCheck "<project dir>\probe\probe.sap-export.s2k"
param(
    [string]$Probe,
    [string]$ProbeCheck,
    [string]$ProbeFile
)
$ErrorActionPreference = "Stop"
function Say([string]$step, [string]$text) { Write-Output ("[{0}] {1}" -f $step.PadRight(7), $text) }
function Q([string]$s) { if ([string]::IsNullOrEmpty($s)) { return "null" } return (ConvertTo-Json -InputObject $s -Compress) }
function Full([string]$p) { return (Resolve-Path -LiteralPath $p).ProviderPath }
function Fail([string]$msg) { Say "error" $msg; Write-Output ("RESULT {""error"":" + (Q $msg) + "}"); exit 1 }

# ---- check: arguments, before the C# batch loads ----
if (-not ($Probe -or $ProbeCheck)) { Fail "Build-Frame: give -Probe <folder> or -ProbeCheck <SAP's export .s2k> (the workbook switches come with the next phases)" }
if ($Probe -and $ProbeCheck) { Fail "Build-Frame: -Probe writes the probe, -ProbeCheck reads SAP's export of it: one at a time" }
$repoRoot = (Resolve-Path -LiteralPath (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))).ProviderPath.TrimEnd('\', '/')
function InRepo([string]$p) { return $p.Equals($repoRoot, [StringComparison]::OrdinalIgnoreCase) -or $p.StartsWith($repoRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) }
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

# ---- load: the C# batch ----
$sw = [Diagnostics.Stopwatch]::StartNew()
. (Join-Path (Join-Path (Split-Path -Parent $PSScriptRoot) "lib") "Config.ps1")
Say "load" ("scripts\lib + scripts\arms compiled ({0:0.0}s)" -f $sw.Elapsed.TotalSeconds)

try {
    if ($Probe) {
        $v22 = [IO.Path]::Combine($dir, "probe-v22.s2k"); $v26 = [IO.Path]::Combine($dir, "probe-v26.s2k")
        [FrameProbe]::Write($v22, "22.0.0")
        [FrameProbe]::Write($v26, "26.3.0")
        Say "write" ("{0} and {1}: import the one for your SAP version, then export it again as {2}" -f $v22, $v26, [IO.Path]::Combine($dir, "probe.sap-export.s2k"))
        Write-Output ("RESULT {""kind"":""probe"",""files"":[" + (Q $v22) + "," + (Q $v26) + "]}")
        exit 0
    }
    $lines = [FrameProbe]::Check($probePath, $exportPath)
    $report = $exportPath + ".check.md"
    [IO.File]::WriteAllText($report, ([string]::Join("`n", $lines.ToArray()) + "`n"), (New-Object Text.UTF8Encoding($false)))
    Say "check" $lines[2]
    Say "write" ("{0}: the field-by-field report" -f $report)
    Write-Output ("RESULT {""kind"":""probecheck"",""report"":" + (Q $report) + "}")
    exit 0
} catch {
    $e = $_.Exception; while ($e.InnerException) { $e = $e.InnerException }
    Fail $e.Message
}
