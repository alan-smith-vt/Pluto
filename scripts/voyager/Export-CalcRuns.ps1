<#
.SYNOPSIS
  Scrape pipe-run names out of calc PDFs. One CSV row per (calc file, run name).

.DESCRIPTION
  Text comes from the Windows PDF IFilter (what Windows Search uses) via PdfText.cs,
  compiled in-session with Add-Type. No packages, no SDK. PowerShell 5.1 or 7.
  Each PDF's text is cached as <TextCache>\<name>.txt; -ReuseCache re-parses without re-reading PDFs.
  Reading stops -ExtraPages after the page holding the header (calcs run to 1000 pages; the table
  is near page 7), so the cache holds only the front of each PDF.

  The filter returns each page as one run of words, no line breaks. Parsing rule:
    find "Lines covered in this system:" -> collect every following token matching -RunPattern;
    stop after -TailWords consecutive tokens that are not run names (page headers/footers
    interleave when the table spans pages; a real gap is longer than that).

.EXAMPLE
  .\Export-CalcRuns.ps1 -PdfRoot 'X:\Calcs' -Recurse -StageLocal -Out C:\Temp\calc_runs.csv
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $PdfRoot,
    [string] $Out        = 'C:\Temp\calc_runs.csv',
    [string] $TextCache  = 'C:\Temp\calc_text',
    [string] $Header     = '  Lines covered in this system:',   # two leading spaces: how the filter renders the line break before it
    [string] $RunPattern = '^F-',      # names carry odd characters (a " was seen); the tail rule bounds the search, not the pattern
    [int]    $TailWords  = 40,
    [int]    $ExtraPages = 3,          # pages read past the one holding the header (table may spill over)
    [int]    $MaxPages   = 10,         # hard cap on pages read per PDF; 0 = none (table sits near page 7)
    [string[]] $ExcludeDir = @('Archive', 'Superseded', 'Old'),   # skip a PDF if any folder name under -PdfRoot contains one of these (case-insensitive)
    [switch] $Recurse,
    [switch] $ReuseCache,
    [switch] $StageLocal,              # copy each PDF to a local temp file before reading (network folders: the
                                       # filter seeks all over the file; one sequential copy beats hundreds of SMB reads)
    [string] $StageDir = (Join-Path $env:TEMP 'calc_stage')
)

$ErrorActionPreference = 'Stop'
if (-not ('Voyager.PdfText' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'PdfText.cs') }
New-Item -ItemType Directory -Force $TextCache | Out-Null
$rootLen = (Resolve-Path $PdfRoot).Path.TrimEnd('\').Length
$pdfs = Get-ChildItem -Path $PdfRoot -Filter *.pdf -File -Recurse:$Recurse | Where-Object {
    $rel = $_.DirectoryName.Substring($rootLen) -split '\\'
    $hit = $false
    foreach ($x in $ExcludeDir) { if ($x -and ($rel -like "*$x*")) { $hit = $true; break } }
    $_.Name -notlike '~$*' -and -not $hit
}
Write-Host "$($pdfs.Count) PDFs under $PdfRoot (excluding folders named: $($ExcludeDir -join ', '))"

function Get-PdfText([System.IO.FileInfo] $pdf) {
    $txt = Join-Path $TextCache ($pdf.BaseName + '.txt')
    if ($ReuseCache -and (Test-Path $txt)) { return [IO.File]::ReadAllText($txt) }
    $src = $pdf.FullName
    if ($StageLocal) {
        New-Item -ItemType Directory -Force $StageDir | Out-Null
        $src = Join-Path $StageDir $pdf.Name
        Copy-Item $pdf.FullName $src -Force
    }
    try   { $text = [Voyager.PdfText]::Extract($src, $Header, $ExtraPages, $MaxPages) }
    finally { if ($StageLocal) { Remove-Item $src -Force -ErrorAction SilentlyContinue } }
    [IO.File]::WriteAllText($txt, $text)
    return $text
}

function Get-RunNames([string] $text) {
    $found = New-Object System.Collections.Generic.List[string]
    foreach ($m in [regex]::Matches($text, [regex]::Escape($Header), 'IgnoreCase')) {
        $words = ($text.Substring($m.Index + $m.Length) -split '\s+') | Where-Object { $_ }
        $quiet = 0
        foreach ($w in $words) {
            if ($w -match $RunPattern) { $quiet = 0; $found.Add($w) }
            elseif (++$quiet -ge $TailWords) { break }
        }
    }
    return $found | Select-Object -Unique
}

$rows = New-Object System.Collections.Generic.List[object]
$noHeader = New-Object System.Collections.Generic.List[string]
$failed = New-Object System.Collections.Generic.List[string]
$n = 0
foreach ($pdf in $pdfs) {
    $n++
    Write-Progress -Activity 'Scraping calcs' -Status $pdf.Name -PercentComplete (100 * $n / $pdfs.Count)
    try { $text = Get-PdfText $pdf }
    catch { Write-Warning "$($pdf.Name): $($_.Exception.Message)"; $failed.Add($pdf.FullName); continue }
    if ($text -notmatch [regex]::Escape($Header)) { $noHeader.Add($pdf.FullName); continue }
    foreach ($r in (Get-RunNames $text)) {
        $rows.Add([pscustomobject]@{ File = $pdf.FullName; Run = $r })
    }
}

$rows | Export-Csv $Out -NoTypeInformation
$outDir = Split-Path $Out
$noHeader | Set-Content (Join-Path $outDir 'calc_runs_noheader.txt')
$failed   | Set-Content (Join-Path $outDir 'calc_runs_failed.txt')

Write-Host ("{0} rows, {1} files with runs, {2} without the header, {3} failed -> {4}" -f
    $rows.Count, @($rows.File | Select-Object -Unique).Count, $noHeader.Count, $failed.Count, $Out)
