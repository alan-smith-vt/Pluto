# The runners' console report (Build-Frame.ps1, Build-Pipe.ps1). Dot-source it at the top of a runner:
#   . (Join-Path (Join-Path (Split-Path -Parent $PSScriptRoot) "lib") "Console.ps1")
# Windows PowerShell 5.1; nothing here needs the C# batch. The report has two forms of the same messages:
#   tidy    for a person at a console: a short head, file names for paths (their folder said once), each message wrapped
#           under its tag, the notices gathered in a block per phase (one said twice shows once), colour by severity
#           where the console takes it (NO_COLOR turns it off), and a one-line summary at the end
#   plain   for a program reading the output (redirected or piped): one line per message, "[step   ] text", and a last
#           line "RESULT {json}" ({"error": "..."} with exit code 1)
# PLUTO_CONSOLE=plain or =tidy forces one.
#   Say <step> <text> [colour]   a message; step "notice" waits for its block, "write" / "keep" show "<file>: what" in
#                                two columns. colour: an ANSI code for the text in the tidy form ("31" red, "33" yellow)
#   Lines <step> <text>          a message of several lines, each said
#   Head <title> <lines>         the tidy form's head (a runner says its plain "check" line itself)
#   Result <json> <first> [tail] the end of a run: RESULT {json}, or the summary "<first>, 3 notes, no warnings<tail>"
#   Fail <message>               the error, RESULT {"error": ...}, exit 1
#   Say-Launchers <paths>        a workbook's launchers on one line
# A runner sets: $script:Dirs (the folders left off paths), $script:NotePhases (a notice's "phase: " prefixes and their
# titles), $script:NoteTitle (the title of the others), and adds its own steps to $script:Tags (tag, colour, section).

$script:Tidy = $false; $script:Ansi = $false; $script:Wide = 100
if ($env:PLUTO_CONSOLE -eq "tidy") { $script:Tidy = $true }
elseif ($env:PLUTO_CONSOLE -ne "plain") { try { $script:Tidy = -not [Console]::IsOutputRedirected } catch { } }
if ($script:Tidy) {
    try { $script:Ansi = ([bool]$Host.UI.SupportsVirtualTerminal) -and (-not $env:NO_COLOR) } catch { }
    try { $cw = [Console]::WindowWidth; if ($cw -ge 60) { $script:Wide = [Math]::Min($cw - 1, 118) } } catch { }
}
$script:Notes = New-Object 'System.Collections.Generic.List[string]'       # notices waiting for their block
$script:Shown = New-Object 'System.Collections.Generic.HashSet[string]'    # notices shown in this run
$script:Tally = @{ }                                                         # messages by step, for the summary
$script:LastTag = ""; $script:LastSection = ""; $script:Dirs = @()
$script:NotePhases = @(); $script:NoteTitle = "the model"
# a step's tag, colour (ANSI) and section; a blank line parts two sections. A step not here is its own tag, in "work"
$script:Tags = @{ notice = @("notes", "", "notes"); warn = @("warn", "33", "alert"); refuse = @("refuse", "31", "alert"); error = @("error", "31", "end");
                  write = @("wrote", "32", "out"); keep = @("kept", "32", "out"); done = @("done", "32", "end") }

function Q([string]$s) { if ([string]::IsNullOrEmpty($s)) { return "null" } return (ConvertTo-Json -InputObject $s -Compress) }

function Paint([string]$s, [string]$code) {
    if ($script:Ansi -and $code -and $s.Trim().Length -gt 0) { return (([string][char]27) + "[" + $code + "m" + $s + ([string][char]27) + "[0m") }
    return $s
}

# words wrapped at a width (a word longer than it stays whole)
function Wrap([string]$text, [int]$width) {
    $out = New-Object 'System.Collections.Generic.List[string]'
    foreach ($para in ($text -split "`r?`n")) {
        $line = ""
        foreach ($w in ($para -split ' ')) {
            if ($line.Length -eq 0) { $line = $w }
            elseif (($line.Length + 1 + $w.Length) -le $width) { $line = $line + " " + $w }
            else { $out.Add($line); $line = $w }
        }
        $out.Add($line)
    }
    return ,$out.ToArray()
}

# the run's own folders taken off a path: a file is said by its name, its folder once in the head
function Short([string]$t) { foreach ($d in $script:Dirs) { if ($d) { $t = $t.Replace($d, "") } }; return $t }

# one message under its tag: the tag once for a run of the same, the text wrapped at the text column
function Line([string]$step, [string]$text, [string]$bullet, [string]$textColor) {
    $name = $step; $color = ""; $section = "work"
    $info = $script:Tags[$step]
    if ($info) { $name = $info[0]; $color = $info[1]; $section = $info[2] }
    if ($script:LastSection -and $section -ne $script:LastSection) { Write-Output "" }
    $script:LastSection = $section
    $pad = 9
    # a line's own indent stays on its wrapped lines (a summary's "  - item" rows)
    $own = $text.Length - $text.TrimStart().Length
    $lines = Wrap ($text.TrimStart()) ($script:Wide - $pad - $bullet.Length - $own)
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $head = ""
        if ($i -eq 0 -and $name -ne $script:LastTag) { $head = $name }
        if ($i -eq 0) { Write-Output ((Paint ($head.PadRight($pad)) $color) + (" " * $own) + (Paint $bullet $textColor) + (Paint ($lines[$i]) $textColor)) }
        else { Write-Output ((" " * ($pad + $own + $bullet.Length)) + (Paint ($lines[$i]) $textColor)) }
    }
    $script:LastTag = $name
}

# the waiting notices as a block per phase (a "phase: " prefix names it: $script:NotePhases), each a wrapped bullet
function Flush-Notes {
    if ($script:Notes.Count -eq 0) { return }
    $order = New-Object 'System.Collections.Generic.List[string]'; $by = @{ }; $titles = @{ "" = $script:NoteTitle }
    foreach ($ph in $script:NotePhases) { $titles[$ph[0]] = $ph[1] }
    foreach ($n in $script:Notes) {
        $phase = ""; $text = $n
        foreach ($ph in $script:NotePhases) { if ($n.StartsWith($ph[0] + ": ")) { $phase = $ph[0]; $text = $n.Substring($ph[0].Length + 2) } }
        if (-not $script:Shown.Add($text)) { continue }
        if (-not $by.ContainsKey($phase)) { $by[$phase] = New-Object 'System.Collections.Generic.List[string]'; $order.Add($phase) }
        $by[$phase].Add($text)
    }
    $script:Notes.Clear()
    foreach ($phase in $order) {
        $script:LastTag = ""
        Line "notice" ("{0} ({1})" -f $titles[$phase], $by[$phase].Count) "" ""
        foreach ($text in $by[$phase]) { Line "notice" (Short $text) "- " "90" }
        $script:Tally["notes"] = $by[$phase].Count + [int]$script:Tally["notes"]
    }
}

function Say([string]$step, [string]$text, [string]$color) {
    if (-not $script:Tidy) { Write-Output ("[{0}] {1}" -f $step.PadRight(7), $text); return }
    if ($step -eq "notice") { $script:Notes.Add($text); return }
    $script:Tally[$step] = 1 + [int]$script:Tally[$step]
    Flush-Notes
    $text = Short $text
    if ($step -eq "write" -or $step -eq "keep") {
        # "<file>: what it is" as two columns
        $at = $text.IndexOf(": ")
        if ($at -gt 0 -and $at -lt 40) { $text = $text.Substring(0, $at).PadRight(20) + "  " + $text.Substring($at + 2) }
    }
    Line $step $text "" $color
}

# a message of several lines (a reader's summary), each said under the step
function Lines([string]$step, [string]$text) { foreach ($line in ($text -split "`r?`n")) { if ($line.Trim()) { Say $step $line } } }

# the head of a tidy report: what is built from what, and the folders
function Head([string]$title, [string[]]$lines) {
    Write-Output (Paint $title "1")
    foreach ($l in $lines) { if ($l) { Write-Output ("  " + $l) } }
    $script:LastSection = "head"; $script:LastTag = ""
}

# the end of a run: RESULT {json} for a program; for a person a one-line summary, "<first>, 3 notes, no warnings<tail>"
# (the waiting notices are shown first, so the summary counts them)
function Result([string]$json, [string]$first, [string]$tail) {
    if (-not $script:Tidy) { Write-Output ("RESULT " + $json); return }
    Flush-Notes
    Line "done" ((Summary $first) + $tail) "" ""
}

# "2 files written, 3 notes, no warnings"
function Summary([string]$first) {
    $parts = @()
    if ($first) { $parts += $first }
    $w = [int]$script:Tally["warn"]; $n = [int]$script:Tally["notes"]
    if ($n -gt 0) { $parts += ("{0} note{1}" -f $n, $(if ($n -eq 1) { "" } else { "s" })) }
    $parts += $(if ($w -eq 0) { "no warnings" } else { "{0} warning{1} (above)" -f $w, $(if ($w -eq 1) { "" } else { "s" }) })
    return ($parts -join ", ")
}

# a run starts again (-Watch): its tally and the notices it has shown
function Reset-Report { $script:Tally = @{ }; $script:Shown.Clear() }

function Fail([string]$msg) {
    Say "error" $msg
    if ($script:Tidy) { Flush-Notes } else { Write-Output ("RESULT {""error"":" + (Q $msg) + "}") }
    exit 1
}

# a workbook's launchers, on one line
function Say-Launchers($paths) {
    $names = @($paths | ForEach-Object { ([IO.Path]::GetFileNameWithoutExtension($_)) -replace '^.* - ', '' })
    Say "write" ("{0} launchers beside it ({1}): double-click one, or use the Start sheet's links" -f $names.Count, ($names -join ", "))
}

# the folders a tidy report leaves off its paths: the output's, then the input's (the longer first)
function Set-ReportDirs([string]$outBase, [string]$inPath) {
    $sep = [string][IO.Path]::DirectorySeparatorChar
    $d = @()
    if ($outBase) { $d += ([IO.Path]::GetDirectoryName($outBase) + $sep) }
    if ($inPath) { $d += ([IO.Path]::GetDirectoryName($inPath) + $sep) }
    $script:Dirs = @($d | Select-Object -Unique | Sort-Object -Property Length -Descending)
}

# the head's folder lines: "in <folder>" when the input and the outputs share it, else "from" and "to"
function Folder-Lines([string]$outBase, [string]$inPath) {
    $o = $(if ($outBase) { [IO.Path]::GetDirectoryName($outBase) } else { $null })
    $i = $(if ($inPath) { [IO.Path]::GetDirectoryName($inPath) } else { $null })
    if ($o -and $i -and $o -ne $i) { return @(("from  " + $i), ("to    " + $o)) }
    return @(("in    " + $(if ($i) { $i } else { $o })))
}
