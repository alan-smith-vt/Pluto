<#
.SYNOPSIS
  CalcFlow: set up and plan the connection calcs from one STAAD analysis package (vault/calcflow/CalcFlow map.md).

.DESCRIPTION
  The verbs, in the order a project uses them (no Excel is needed for any of them):
    harvest         learn each calc template's layout once: -Templates <folder or .xlsx files> [-Out harvest.xlsx]
                    -> harvest.xlsx with the drafted calc-library rows (Templates, Inputs, Outputs: inputs with their
                    guard labels and units, DCR / status / solver-check outputs) and every cell of every template
    library-new     a new calc library workbook: -Library <folder> [-Harvest harvest.xlsx] [-FromText]
                    (seeded with harvest's drafts, or rebuilt from the library's exported text)
    library-export  the edited library workbook -> its canonical text (text\*.csv), which the tool runs from:
                    -Library <folder>
    register-new    a new connection register from a package: -Package <folder> -Library <folder> -Out <register.xlsx>
    plan            the dry run: -Package <folder> -Library <folder> -Register <register.xlsx> [-Templates <folder>]
                    -Out <folder>. Every cell of every calc resolved (plan.csv, cases.csv, demands.csv, calcs.csv) and
                    every gap named (problems.csv)
  The package itself comes from Export-AnlFrame.ps1 (a STAAD .anl -> the package).
  Exit code 0 = done with no errors; 1 = errors were listed (the files are still written so they can be read);
  2 = bad arguments. Windows PowerShell 5.1 or PowerShell 7.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\calcflow\CalcFlow.ps1 harvest -Templates "<templates folder>" -Out "<project dir>\calcflow\harvest.xlsx"
  powershell -ExecutionPolicy Bypass -File scripts\calcflow\CalcFlow.ps1 plan -Package "<project dir>\calcflow\package\MODEL-A\R03" -Library "<library folder>" -Register "<project dir>\Connection_Register.xlsx" -Out "<project dir>\calcflow\plan"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)][ValidateSet('harvest', 'library-new', 'library-export', 'register-new', 'plan')][string]$Verb,
    [string[]]$Templates,
    [string]$Library,
    [string]$Harvest,
    [string]$Package,
    [string]$Register,
    [string]$Out,
    [switch]$FromText
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Import-CalcFlow.ps1')
Import-CalcFlowEngine

function Get-CfFull([string]$Path) { return $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path) }

function Stop-CfArgs([string]$Message) { Write-Host $Message -ForegroundColor Red; exit 2 }

# project data (packages, registers, plans, harvests of real templates) never lands in the repo; the tests' own
# output folder (gitignored) is the exception
function Assert-CfOutsideRepo([string]$Path) {
    $repo = (Resolve-Path -LiteralPath (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path.TrimEnd('\', '/')
    $testsOut = (Join-Path (Join-Path $PSScriptRoot 'tests') 'out')
    $sep = [IO.Path]::DirectorySeparatorChar
    $inRepo = $Path.StartsWith($repo + $sep, [StringComparison]::OrdinalIgnoreCase) -or $Path -eq $repo
    $inTests = $Path.StartsWith($testsOut + $sep, [StringComparison]::OrdinalIgnoreCase) -or $Path -eq $testsOut
    if ($inRepo -and -not $inTests) { Stop-CfArgs "This is project data: write it outside the repo (got $Path)." }
}

function Need([string]$Name, $Value) { if (-not $Value) { Stop-CfArgs "$Verb needs -$Name." } }

function Show-CfProblems($Problems, [string]$Indent = '') {
    foreach ($p in $Problems) {
        $color = if ($p.Severity -eq 'Error') { 'Red' } else { 'Yellow' }
        $tag = if ($p.Severity -eq 'Error') { '[error]' } else { '[warn ]' }
        Write-Host ("{0}{1} {2}" -f $Indent, $tag, ($p.ToString().Substring($p.Severity.Length + 1))) -ForegroundColor $color
    }
}

switch ($Verb) {
    'harvest' {
        Need 'Templates' $Templates
        $files = New-Object System.Collections.Generic.List[string]
        $root = ''
        foreach ($t in $Templates) {
            $full = Get-CfFull $t
            if (Test-Path -LiteralPath $full -PathType Container) {
                if (-not $root) { $root = $full }
                foreach ($f in Get-ChildItem -LiteralPath $full -Recurse -File -Include '*.xlsx', '*.xlsm' | Where-Object { $_.Name -notlike '~$*' } | Sort-Object FullName) { $files.Add($f.FullName) }
            }
            elseif (Test-Path -LiteralPath $full -PathType Leaf) { $files.Add($full) }
            else { Stop-CfArgs "No such template file or folder: $t" }
        }
        if ($files.Count -eq 0) { Stop-CfArgs 'No .xlsx templates found.' }
        $outPath = if ($Out) { Get-CfFull $Out } else { Get-CfFull 'harvest.xlsx' }
        Assert-CfOutsideRepo $outPath
        $r = [CalcFlow.Harvest]::Run([string[]]$files.ToArray(), $root)
        foreach ($t in $r.Templates) { Write-Host ("template {0}  ({1})" -f $t['Template'], $t['File']); Write-Host ("         {0}" -f $t['Notes']) -ForegroundColor DarkGray }
        foreach ($p in $r.Problems) { Write-Host "[error] $p" -ForegroundColor Red }
        [CalcFlow.Harvest]::WriteWorkbook($r, $outPath)
        Write-Host ("harvest  {0}: {1} inputs, {2} outputs, {3} layout cells" -f $outPath, $r.Inputs.Count, $r.Outputs.Count, $r.Layout.Count) -ForegroundColor Green
        if ($r.Problems.Count -gt 0) { exit 1 }
        exit 0
    }
    'library-new' {
        Need 'Library' $Library
        $folder = Get-CfFull $Library
        Assert-CfOutsideRepo $folder
        $wb = Join-Path $folder ([CalcFlow.Library]::WorkbookName)
        if (Test-Path -LiteralPath $wb) { Stop-CfArgs "$wb exists: the library workbook is edited in Excel and never overwritten." }
        $rows = New-Object 'System.Collections.Generic.Dictionary[string,System.Collections.Generic.List[System.Collections.Generic.Dictionary[string,string]]]' ([StringComparer]::OrdinalIgnoreCase)
        if ($FromText) {
            $text = Join-Path $folder ([CalcFlow.Library]::TextFolder)
            if (-not (Test-Path -LiteralPath $text)) { Stop-CfArgs "No exported text in $text." }
            foreach ($t in [CalcFlow.LibrarySchema]::Tables) { $rows[$t.Name] = [CalcFlow.Csv]::ReadTable((Join-Path $text $t.File)) }
        }
        if ($Harvest) {
            $h = [CalcFlow.Xlsx]::Read((Get-CfFull $Harvest))
            foreach ($pair in @(@('tblTemplates', 'tblHarvestTemplates'), @('tblInputs', 'tblHarvestInputs'), @('tblOutputs', 'tblHarvestOutputs'))) {
                $got = $h.TableRows($pair[1])
                if ($null -eq $got) { Stop-CfArgs "$Harvest has no table $($pair[1]) (is it a harvest.xlsx?)" }
                foreach ($x in $got) { [void]$x.Remove('__row') }
                if (-not $rows.ContainsKey($pair[0])) { $rows[$pair[0]] = New-Object 'System.Collections.Generic.List[System.Collections.Generic.Dictionary[string,string]]' }
                $rows[$pair[0]].AddRange($got)
            }
        }
        if (-not (Test-Path -LiteralPath $folder)) { New-Item -ItemType Directory -Path $folder | Out-Null }
        [CalcFlow.Library]::WriteWorkbook($wb, $rows)
        Write-Host "library  $wb" -ForegroundColor Green
        Write-Host '         edit it in Excel (bindings, types, steps, frames, parameters, sections), save, then run library-export'
        exit 0
    }
    'library-export' {
        Need 'Library' $Library
        $folder = Get-CfFull $Library
        $wb = if ($folder -like '*.xlsx') { $folder } else { Join-Path $folder ([CalcFlow.Library]::WorkbookName) }
        if (-not (Test-Path -LiteralPath $wb)) { Stop-CfArgs "No library workbook $wb." }
        $lib = [CalcFlow.Library]::Export($wb)
        Write-Host ("exported {0} -> {1}" -f $wb, (Join-Path $lib.Folder ([CalcFlow.Library]::TextFolder))) -ForegroundColor Green
        Write-Host ("         {0} templates, {1} types, {2} frames, {3} sections" -f $lib.Templates.Count, $lib.Types.Count, $lib.Frames.Count, $lib.Sections.Count)
        foreach ($p in $lib.Problems) {
            $color = if ($p.Severity -eq 'Error') { 'Red' } else { 'Yellow' }
            Write-Host ("[{0}] {1}" -f $(if ($p.Severity -eq 'Error') { 'error' } else { 'warn ' }), $p.ToString().Substring($p.Severity.Length + 1)) -ForegroundColor $color
        }
        if ($lib.ErrorCount -gt 0) { exit 1 }
        exit 0
    }
    'register-new' {
        Need 'Package' $Package; Need 'Out' $Out
        $outPath = Get-CfFull $Out
        Assert-CfOutsideRepo $outPath
        $pkg = [CalcFlow.AnalysisPackage]::Load((Get-CfFull $Package))
        $lib = $null
        if ($Library) { $lib = [CalcFlow.Library]::Load((Get-CfFull $Library)) }
        [CalcFlow.Register]::WriteNew($outPath, $pkg, $lib)
        Write-Host ("register {0}: {1} member ends at {2} joints, {3} load cases" -f $outPath, $pkg.Ends.Count, $pkg.Joints.Count, $pkg.Cases.Count) -ForegroundColor Green
        Write-Host '         give each connection that needs a calc its Type (and check Support), save, then run plan'
        exit 0
    }
    'plan' {
        Need 'Package' $Package; Need 'Library' $Library; Need 'Register' $Register; Need 'Out' $Out
        $outPath = Get-CfFull $Out
        Assert-CfOutsideRepo $outPath
        $pkg = [CalcFlow.AnalysisPackage]::Load((Get-CfFull $Package))
        $lib = [CalcFlow.Library]::Load((Get-CfFull $Library))
        $reg = [CalcFlow.Register]::Load((Get-CfFull $Register))
        $tdir = if ($Templates) { Get-CfFull $Templates[0] } elseif ($reg.Setting('Templates', '')) { Get-CfFull ($reg.Setting('Templates', '')) } else { Join-Path $lib.Folder 'templates' }
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $res = [CalcFlow.Planner]::Run($pkg, $lib, $reg, $tdir)
        $files = $res.Write($outPath)
        Write-Host ''
        Write-Host ("package  {0} ({1})" -f $pkg.Folder, $pkg.Source)
        Write-Host ("library  {0}: {1} templates, {2} types" -f $lib.Folder, $lib.Templates.Count, $lib.Types.Count)
        Write-Host ("register {0}: {1} rows" -f $reg.Path, $reg.Connections.Count)
        Write-Host ("plan     {0} calcs ({1} ready), {2} cells, {3} demand rows, {4} per-case cells ({5:0.0} s)" -f $res.Calcs.Count, @($res.Calcs | Where-Object { $_.Status -eq 'Ready' }).Count, $res.Cells.Count, $res.Demands.Count, $res.CaseCells.Count, $sw.Elapsed.TotalSeconds)
        Show-CfProblems $res.Problems
        $color = if ($res.ErrorCount -gt 0) { 'Red' } else { 'Green' }
        Write-Host ("written  {0}: {1} errors, {2} warnings" -f $outPath, $res.ErrorCount, $res.WarningCount) -ForegroundColor $color
        if ($res.ErrorCount -gt 0) { exit 1 }
        exit 0
    }
}
