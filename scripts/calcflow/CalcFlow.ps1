<#
.SYNOPSIS
  CalcFlow: set up, plan and run the connection calcs from one STAAD analysis package (vault/calcflow/CalcFlow map.md).

.DESCRIPTION
  The front door (2026-11-24) is a workbook: its Start tab links to launchers that run the verbs for you.
    new             the engineer's workbook and its launchers, once: -Out <project folder> [-Anl <file>]
                    [-Templates <folder>] [-InputFill <colour>: the fill of the cells to fill in when it is not yellow]
                    -> CalcFlow.xlsx: Start (the steps as link cells, the settings), Model Config (one row per beam of
                    the .anl, a connection mark at each end), Connection Lib (the marks), Templates, a schedule tab per
                    template found in -Templates (one row per mark, one column per input), Outputs, Sign Maps, Load
                    Cases; and the launchers "Post-process ANL.cmd", "Check.cmd", "Run calcs.cmd"
    check           reads the saved workbook and the model and lists what is missing or does not fit, tab by tab:
                    -Workbook <CalcFlow.xlsx>. Writes the calc library text, a register and the plan under
                    <workbook folder>\calcflow\ (all generated). Nothing is calculated.
    calcs           check, then run and summary on the plan: -Workbook <CalcFlow.xlsx> [-Engine ...]
    post-process    reads the STAAD .anl and tabulates it: -Workbook <CalcFlow.xlsx> [-Anl <file>] [-NoOpen]
                    The .anl is -Anl, else the workbook's "Anl file" setting, else you are asked for it (a file dialog).
                    -> the analysis package in <workbook folder>\calcflow\package\<anl name>\ and Model_Data.xlsx
                    beside the workbook (members, joints, supports, load cases, all end forces, enveloped end forces),
                    opened when done. Model_Data.xlsx is generated: every run writes it again.
    refresh         the workbook written again around the model and the templates folder as they are now, with
                    everything filled in carried over: -Workbook <CalcFlow.xlsx> [-Templates <folder>] [-NoWait]
                    [-NoOpen]. For a template added to the folder or changed, new beams or load cases in the model, or
                    Input cells / Result cells given on the Templates tab. The workbook as it was is kept beside it
                    ("CalcFlow (before refresh <date>).xlsx"). Excel must have closed the workbook: the step waits for
                    that (-NoWait: it refuses instead), and opens the workbook again when done.
                    post-process and check say when a refresh is due.
    launchers       the launchers again (their path to this script is the one at write time): -Workbook <CalcFlow.xlsx>
    map             a look at what each template asks for, read from the cells filled yellow: -Templates <folder>
                    -Out <folder> [-InputFill <colour>,<colour>: the fill of the cells to fill in when it is not
                    yellow, six hex digits each as the Fills tab lists them] -> Template_Map.xlsx (tab Inputs found: one row per input cell, or per column of a
                    list or table, with its heading and who would fill it; tab Fills: every fill colour found, and
                    whether it is taken as inputs). Reads only: the same reading builds a template's tab in new and refresh.
    excel-check     for a workbook Excel opened with "Repaired Records: Table from /xl/tables/...": lists what in its
                    tables breaks a rule Excel has for them (a heading over 255 characters, two headings alike, a
                    heading that is not text ...): -Workbook <any .xlsx> [-Repaired <the copy saved from Excel after
                    its repair>]. With -Repaired it also lists what Excel changed, heading by heading. Reads only.
  The verbs underneath, in the order a project uses them (only run calculates: with Excel where it is installed, else
  LibreOffice):
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
                    every gap named (problems.csv), and plan-manifest.txt (the sources it was made from)
    run             calculates the plan's due calcs: -Plan <folder> [-Work <folder>] [-Rev R03] [-Engine auto|excel|
                    libreoffice] [-All] [-Calc <ids>] [-Visible] [-KeepRun]. Due: never run, failed, inputs changed,
                    a record missing, or a new revision (carried forward when its inputs are unchanged). Writes
                    records\<rev>\<calc>\ (the calculated copy that governs each check, with its .calcflow.json),
                    data\results\, data\calc-state.csv, then the summary. Refuses a plan whose sources changed.
    summary         reports\Connection_Summary_<date>.xlsx from the plan and the last runs: -Plan <folder>
                    [-Work <folder>] [-Register <register.xlsx>] [-Rev R03]
  -Work defaults to the plan folder's parent, -Rev to the package folder's name.
  The package itself comes from Export-AnlFrame.ps1 (a STAAD .anl -> the package).
  Exit code 0 = done with no errors; 1 = errors were listed, or a calc failed or was invalid (the files are still
  written so they can be read); 2 = bad arguments. Windows PowerShell 5.1 or PowerShell 7.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\calcflow\CalcFlow.ps1 new -Out "<project dir>"
  powershell -ExecutionPolicy Bypass -File scripts\calcflow\CalcFlow.ps1 harvest -Templates "<templates folder>" -Out "<project dir>\calcflow\harvest.xlsx"
  powershell -ExecutionPolicy Bypass -File scripts\calcflow\CalcFlow.ps1 plan -Package "<project dir>\calcflow\package\MODEL-A\R03" -Library "<library folder>" -Register "<project dir>\Connection_Register.xlsx" -Out "<project dir>\calcflow\plan"
  powershell -ExecutionPolicy Bypass -File scripts\calcflow\CalcFlow.ps1 run -Plan "<project dir>\calcflow\plan"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)][ValidateSet('new', 'post-process', 'refresh', 'launchers', 'check', 'calcs', 'excel-check', 'map', 'harvest', 'library-new', 'library-export', 'register-new', 'plan', 'run', 'summary')][string]$Verb,
    [string[]]$Templates,
    [string]$Library,
    [string]$Harvest,
    [string]$Package,
    [string]$Register,
    [string]$Out,
    [switch]$FromText,
    [string]$Plan,
    [string]$Work,
    [string]$Rev,
    [ValidateSet('auto', 'excel', 'libreoffice')][string]$Engine = 'auto',
    [switch]$All,
    [string[]]$Calc,
    [switch]$Visible,
    [switch]$KeepRun,
    [string]$Workbook,
    [string]$Anl,
    [switch]$NoOpen,
    [switch]$NoWait,
    [string]$Repaired,
    [string[]]$InputFill
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Import-CalcFlow.ps1')
. (Join-Path $PSScriptRoot 'Stage.ps1')
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

# the plan, the library and the work folder of run / summary (the plan's manifest says where its sources are)
function Open-CfPlan {
    Need 'Plan' $Plan
    $planDir = Get-CfFull $Plan
    if (-not (Test-Path -LiteralPath (Join-Path $planDir 'calcs.csv'))) { Stop-CfArgs "Not a plan folder (no calcs.csv): $planDir" }
    $set = [CalcFlow.PlanSet]::Load($planDir)
    $workDir = if ($Work) { Get-CfFull $Work } else { Split-Path -Parent $planDir }
    Assert-CfOutsideRepo $workDir
    $libDir = $set.Source('library')
    if (-not $libDir) { Stop-CfArgs "The plan has no plan-manifest.txt (made before CalcFlow ran calcs): run plan again." }
    $lib = [CalcFlow.Library]::Load($libDir)
    if ($lib.ErrorCount -gt 0) { Show-CfProblems $lib.Problems; Stop-CfArgs "The calc library has errors: fix them, run library-export, plan, then run." }
    $r = if ($Rev) { $Rev } else { $set.DefaultRev }
    return [pscustomobject]@{ Dir = $planDir; Set = $set; Work = $workDir; Library = $lib; Rev = $r }
}

function Write-CfSummary($P) {
    $regPath = if ($Register) { Get-CfFull $Register } else { $P.Set.Source('register') }
    if (-not $regPath -or -not (Test-Path -LiteralPath $regPath)) { Stop-CfArgs "No register to summarise against ($regPath): give -Register." }
    $reg = [CalcFlow.Register]::Load($regPath)
    $sum = [CalcFlow.Summary]::Build($P.Set, $P.Library, $reg, $P.Work, $P.Rev)
    $path = [CalcFlow.Summary]::Write($sum, (Join-Path $P.Work 'reports'), $P.Dir)
    Write-Host ("summary  {0}: {1} member ends -- {2} OK, {3} over the limit, {4} invalid, {5} failed, {6} not run, {7} gaps / blocked, {8} excluded" -f $path, $sum.Connections.Count,
        $sum.Count('OK'), $sum.Count('Over limit'), $sum.Count('Invalid'), $sum.Count('Failed'), $sum.Count('Not run'), ($sum.Count('Gaps') + $sum.Count('Blocked')), $sum.Count('Excluded')) -ForegroundColor Green
    return $sum
}

# the engine: Excel (through the Recalc-Workbooks stage, a Windows PowerShell 5.1 child) or LibreOffice
function New-CfEngine([string]$WorkDir) {
    $excel = $false
    if (Test-CfWindows) { $excel = $null -ne [Type]::GetTypeFromProgID('Excel.Application') }
    $why = ''
    $pick = [CalcFlow.Engines]::Pick($Engine, $excel, [ref]$why)
    if (-not $pick) { Stop-CfArgs $why }
    $runDir = Join-Path $WorkDir 'run'
    if ($pick -eq 'libreoffice') {
        return New-Object CalcFlow.LibreOfficeEngine -ArgumentList @([CalcFlow.LibreOffice]::Find(), (Join-Path $runDir 'libreoffice-profile'))
    }
    $ver = ''
    try { $ver = ((Get-ItemProperty -LiteralPath 'Registry::HKEY_CLASSES_ROOT\Excel.Application\CurVer').'(default)') -replace '^Excel\.Application\.', '' } catch { }
    $script:StageDir = $runDir
    $recalc = [Action[System.Collections.Generic.IList[string], string]] {
        param($files, $outDir)
        $r = Invoke-CfStage -Name 'Recalc-Workbooks' -Plan @{ Files = @($files); OutDir = $outDir; Visible = [bool]$Visible; SecondsPerFile = 300 } -WorkDir $script:StageDir -TimeoutSeconds 900
        if ($r.Killed) { throw "Excel stopped answering while $($r.Step); it was stopped" }
        if (-not $r.Result -or -not $r.Result.Ok) {
            $msg = "exit code $($r.ExitCode)"
            if ($r.Result -and $r.Result.Message) { $msg = $r.Result.Message }
            throw "Excel could not calculate the batch: $msg"
        }
        foreach ($f in @($r.Result.Failed)) { if ($f) { Write-Host ("  [error] Excel: {0}: {1}" -f $f.File, $f.Message) -ForegroundColor Red } }
    }
    return New-Object CalcFlow.DelegateEngine -ArgumentList @('Excel', $ver, 50, $recalc)
}

# the .anl to read, picked in a file dialog (Windows, an interactive run); $null when none was picked
function Select-CfAnl([string]$StartDir) {
    if (-not (Test-CfWindows) -or -not [Environment]::UserInteractive) { return $null }
    Add-Type -AssemblyName System.Windows.Forms
    $d = New-Object System.Windows.Forms.OpenFileDialog
    $d.Title = 'CalcFlow: choose the STAAD analysis output (.anl)'
    $d.Filter = 'STAAD analysis output (*.anl)|*.anl|All files (*.*)|*.*'
    $d.InitialDirectory = $StartDir
    # an owner that stays on top, so the dialog does not open behind the console window
    $owner = New-Object System.Windows.Forms.Form
    $owner.TopMost = $true
    $picked = $null
    try { if ($d.ShowDialog($owner) -eq [System.Windows.Forms.DialogResult]::OK) { $picked = $d.FileName } }
    finally { $owner.Dispose(); $d.Dispose() }
    return $picked
}

# the template workbooks under a folder (or the files given), Excel's lock files left out
function Get-CfTemplateFiles([string[]]$From) {
    $files = New-Object System.Collections.Generic.List[string]
    foreach ($t in $From) {
        $full = Get-CfFull $t
        if (Test-Path -LiteralPath $full -PathType Container) {
            $patterns = '*.xlsx', '*.xlsm', '*.xltx', '*.xltm', '*.xls', '*.xlsb', '*.xlt'
            foreach ($f in Get-ChildItem -LiteralPath $full -Recurse -File -Include $patterns | Where-Object { $_.Name -notlike '~$*' } | Sort-Object FullName) { $files.Add($f.FullName) }
        }
        elseif (Test-Path -LiteralPath $full -PathType Leaf) { $files.Add($full) }
        else { Stop-CfArgs "No such template file or folder: $t" }
    }
    return , $files.ToArray()
}

# reads an .anl into the package folder of a project (<project>\calcflow\package\<anl name>) and notes it as the
# project's current model; exits 1 when the analysis output has errors. Returns the package.
function Read-CfAnl([string]$AnlPath, [string]$ProjectDir) {
    if (-not (Test-Path -LiteralPath $AnlPath -PathType Leaf)) { Stop-CfArgs "No such .anl: $AnlPath" }
    Import-PlutoLib
    Write-Host ''
    Write-Host "reading  $AnlPath"
    $m = [AnlFrameReader]::Read($AnlPath)
    foreach ($p in $m.Problems) {
        $color = if ($p.Severity -eq 'Error') { 'Red' } else { 'Yellow' }
        $tag = if ($p.Severity -eq 'Error') { '[error]' } else { '[warn ]' }
        Write-Host ("{0} {1}: {2}" -f $tag, $p.Code, $p.Message) -ForegroundColor $color
    }
    if ($m.ErrorCount -gt 0) {
        Write-Host ("not written: the analysis output has {0} error(s). Nothing was changed." -f $m.ErrorCount) -ForegroundColor Red
        exit 1
    }
    $work = Join-Path $ProjectDir 'calcflow'
    $name = [IO.Path]::GetFileNameWithoutExtension($AnlPath)
    $pkgDir = Join-Path (Join-Path $work 'package') $name
    [void][FramePackageWriter]::Write($m, $pkgDir)
    [IO.File]::WriteAllText((Join-Path $work 'current-package.txt'), $name)
    return [CalcFlow.AnalysisPackage]::Load($pkgDir)
}

# the project's current model (the last .anl post-processed); stops when there is none
function Get-CfCurrentPackage([string]$ProjectDir) {
    $work = Join-Path $ProjectDir 'calcflow'
    $ptr = Join-Path $work 'current-package.txt'
    if (-not (Test-Path -LiteralPath $ptr)) { Stop-CfArgs 'No model has been read for this workbook yet: run "Post-process .ANL" first.' }
    $pkgDir = Join-Path (Join-Path $work 'package') ([IO.File]::ReadAllText($ptr).Trim())
    return [CalcFlow.AnalysisPackage]::Load($pkgDir)
}

# the templates folder of a workbook: -Templates, else its "Templates folder" setting ('' when neither is given)
# the fills that mark the cells to fill in, when it is not yellow: checked here, then used by every harvest of the run
function Set-CfInputFills([string[]]$Fills) {
    try { $set = [CalcFlow.TemplateLook]::Colours([string[]]@($Fills | Where-Object { $_ })) }
    catch { Stop-CfArgs ("Input fill: " + $_.Exception.InnerException.Message) }
    [CalcFlow.Harvest]::InputFills = New-Object 'System.Collections.Generic.List[string]' (, [string[]]@($set))
}

# how each template's inputs were found: from its coloured cells, or guessed from its formulas when it has none
function Show-CfColoured($H) {
    $n = $H.Coloured.Count
    $guessed = @($H.Templates | Where-Object { -not $H.Coloured.Contains($_['Template']) } | ForEach-Object { $_['File'] })
    Write-Host ("inputs   {0} template workbook(s) read from their coloured cells, {1} with none: guessed from their formulas" -f $n, $guessed.Count)
    if ($n -gt 0 -and $guessed.Count -gt 0) {
        $more = ''
        if ($guessed.Count -gt 8) { $more = ' ...' }
        Write-Host ("[note ] no cell with the input fill in: {0}{1}. Its inputs are guessed; if it uses another colour, give it as Input fill on the Start tab." -f (($guessed | Select-Object -First 8) -join ', '), $more) -ForegroundColor Yellow
    }
}

function Get-CfTemplatesDir([string]$Wb) {
    if ($Templates) { return (Get-CfFull $Templates[0]) }
    $set = [CalcFlow.EngineerWorkbook]::Settings($Wb)
    if ($set.ContainsKey([CalcFlow.EngineerWorkbook]::TemplatesSetting)) { return $set[[CalcFlow.EngineerWorkbook]::TemplatesSetting] }
    return ''
}

# what changed since the workbook was set up (a template added or changed, beams or load cases new in the model):
# said as notes that end with what to do. Returns how many.
function Show-CfDrift([string]$Wb, $Pkg) {
    $tdir = Get-CfTemplatesDir $Wb
    $files = $null
    if ($tdir -and (Test-Path -LiteralPath $tdir -PathType Container)) { $files = [string[]](Get-CfTemplateFiles @($tdir)) }
    $notes = @([CalcFlow.ProjectBook]::Drift($Wb, $Pkg, $tdir, $files))
    foreach ($n in $notes) { Write-Host "[note ] $n" -ForegroundColor Yellow }
    if ($notes.Count -gt 0) { Write-Host '         Start tab, "Refresh config" brings the workbook up to date and keeps what you have filled in.' -ForegroundColor Yellow }
    return $notes.Count
}

# check: the saved workbook -> library text, register and plan under <project>\calcflow; $true when nothing is wrong
function Invoke-CfCheck([string]$Wb) {
    $dir = Split-Path -Parent $Wb
    Assert-CfOutsideRepo $dir
    $work = Join-Path $dir 'calcflow'
    $pkg = Get-CfCurrentPackage $dir
    $set = [CalcFlow.EngineerWorkbook]::Settings($Wb)
    $tdir = ''
    if ($Templates) { $tdir = Get-CfFull $Templates[0] }
    elseif ($set.ContainsKey([CalcFlow.EngineerWorkbook]::TemplatesSetting)) { $tdir = $set[[CalcFlow.EngineerWorkbook]::TemplatesSetting] }
    if (-not $tdir) { Stop-CfArgs 'Start tab: give the "Templates folder" setting (the folder that holds the calc template workbooks).' }
    $limit = ''
    if ($set.ContainsKey([CalcFlow.EngineerWorkbook]::LimitSetting)) { $limit = $set[[CalcFlow.EngineerWorkbook]::LimitSetting] }
    Write-Host ''
    Write-Host ("workbook {0}" -f $Wb)
    Write-Host ("model    {0} ({1})" -f $pkg.Folder, $pkg.Source)
    [void](Show-CfDrift $Wb $pkg)
    $x = [CalcFlow.ProjectBook]::Expand($Wb, $pkg, (Join-Path $work 'generated'), $limit, $tdir)
    foreach ($p in $x.Problems) {
        $color = if ($p[0] -eq 'Error') { 'Red' } else { 'Yellow' }
        $tag = if ($p[0] -eq 'Error') { '[fix  ]' } else { '[note ]' }
        Write-Host ("{0} {1}: {2}" -f $tag, $p[1], $p[2]) -ForegroundColor $color
    }
    Write-Host ("read     {0} connection marks on {1} templates, {2} beam ends with a connection" -f $x.Marks, $x.Templates, $x.Connections)
    if ($x.ErrorCount -gt 0) {
        Write-Host ("check    {0} thing(s) to fix in the workbook. Fix them, save, and check again." -f $x.ErrorCount) -ForegroundColor Red
        return $false
    }
    $lib = [CalcFlow.Library]::Load($x.LibraryFolder)
    $reg = [CalcFlow.Register]::Load($x.RegisterPath)
    $res = [CalcFlow.Planner]::Run($pkg, $lib, $reg, $tdir)
    $planDir = Join-Path $work 'plan'
    [void]$res.Write($planDir)
    [CalcFlow.PlanManifest]::Write($planDir, $pkg, $lib, $reg, $tdir)
    Show-CfProblems $res.Problems
    $ready = @($res.Calcs | Where-Object { $_.Status -eq 'Ready' }).Count
    $color = if ($res.ErrorCount -gt 0) { 'Red' } else { 'Green' }
    Write-Host ("check    {0} of {1} connection calcs are ready: {2} error(s), {3} warning(s). Details in {4}" -f $ready, $res.Calcs.Count, $res.ErrorCount, $res.WarningCount, $planDir) -ForegroundColor $color
    return ($res.ErrorCount -eq 0)
}

switch ($Verb) {
    'new' {
        Need 'Out' $Out
        $folder = Get-CfFull $Out
        Assert-CfOutsideRepo $folder
        $wb = Join-Path $folder ([CalcFlow.EngineerWorkbook]::FileName)
        if (Test-Path -LiteralPath $wb) { Stop-CfArgs "$wb exists: the workbook is yours once written and is never overwritten." }
        $pkg = $null; $h = $null; $tdir = ''
        if ($Anl) { $pkg = Read-CfAnl (Get-CfFull $Anl) $folder }
        if ($Templates) {
            $tdir = Get-CfFull $Templates[0]
            $tfiles = Get-CfTemplateFiles $Templates
            if ($tfiles.Count -eq 0) { Stop-CfArgs "No template workbooks found in $tdir." }
            Set-CfInputFills $InputFill
            $h = [CalcFlow.Harvest]::Run([string[]]$tfiles, $tdir)
            foreach ($k in $h.Skipped) { Write-Host ("[skip ] {0}: {1}" -f $k[0], $k[1]) -ForegroundColor Yellow }
            foreach ($p in $h.Problems) { Write-Host "[error] $p" -ForegroundColor Red }
            Write-Host ("learned  {0} template workbook(s): {1} inputs, {2} results" -f $h.Templates.Count, $h.Inputs.Count, $h.Outputs.Count)
            Show-CfColoured $h
        }
        $files = [CalcFlow.EngineerWorkbook]::WriteNew($folder, (Join-Path $PSScriptRoot 'CalcFlow.ps1'), $pkg, $h, $tdir)
        if ($h) { $line = [CalcFlow.ProjectBook]::SheetsLine($wb); if ($line) { Write-Host "sheets   $line" } }
        foreach ($f in $files) { Write-Host "written  $f" -ForegroundColor Green }
        if ($pkg) {
            $md = [CalcFlow.ModelData]::Write($pkg, $folder)
            Write-Host ("written  {0}" -f $md.Path) -ForegroundColor Green
        }
        else { Write-Host 'No -Anl given: Model Config is empty. Post-process an .anl, then copy the beams from Model_Data.xlsx (tab Members).' -ForegroundColor Yellow }
        if (-not $h) { Write-Host 'No -Templates given: there are no schedule tabs yet.' -ForegroundColor Yellow }
        Write-Host 'Open the workbook: its Start tab has the steps.'
        exit 0
    }
    'check' {
        Need 'Workbook' $Workbook
        $wb = Get-CfFull $Workbook
        if (-not (Test-Path -LiteralPath $wb)) { Stop-CfArgs "No such workbook: $wb" }
        if (Invoke-CfCheck $wb) { exit 0 }
        exit 1
    }
    'calcs' {
        Need 'Workbook' $Workbook
        $wb = Get-CfFull $Workbook
        if (-not (Test-Path -LiteralPath $wb)) { Stop-CfArgs "No such workbook: $wb" }
        if (-not (Invoke-CfCheck $wb)) { exit 1 }
        $work = Join-Path (Split-Path -Parent $wb) 'calcflow'
        $exe = (Get-Process -Id $PID).Path
        & $exe -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath run -Plan (Join-Path $work 'plan') -Work $work -Engine $Engine
        exit $LASTEXITCODE
    }
    'refresh' {
        Need 'Workbook' $Workbook
        $wb = Get-CfFull $Workbook
        if (-not (Test-Path -LiteralPath $wb)) { Stop-CfArgs "No such workbook: $wb" }
        $dir = Split-Path -Parent $wb
        Assert-CfOutsideRepo $dir
        # the workbook is rewritten, so Excel must have closed it: wait for that (the step is started from inside it)
        if ([CalcFlow.SharedFile]::IsLocked($wb)) {
            if ($NoWait) { Stop-CfArgs "$([IO.Path]::GetFileName($wb)) is open in Excel: save and close it, then refresh." }
            Write-Host ''
            Write-Host ("{0} is open in Excel." -f [IO.Path]::GetFileName($wb)) -ForegroundColor Yellow
            Write-Host 'SAVE it and CLOSE it now: the refresh starts when it is closed, and opens it again when it is done.' -ForegroundColor Yellow
            $waited = 0
            while ([CalcFlow.SharedFile]::IsLocked($wb)) {
                Start-Sleep -Seconds 1
                $waited++
                if ($waited -ge 900) { Stop-CfArgs 'The workbook was still open after 15 minutes: nothing was changed. Close it and run Refresh config again.' }
            }
            Start-Sleep -Seconds 1
        }
        $tdir = Get-CfTemplatesDir $wb
        $h = $null
        if ($tdir) {
            if (-not (Test-Path -LiteralPath $tdir -PathType Container)) { Stop-CfArgs "The templates folder is not there: $tdir (Start tab, `"Templates folder`")." }
            $tfiles = Get-CfTemplateFiles @($tdir)
            if ($tfiles.Count -gt 0) {
                $fills = $InputFill
                if (-not $fills) {
                    $set = [CalcFlow.EngineerWorkbook]::Settings($wb)
                    if ($set.ContainsKey([CalcFlow.EngineerWorkbook]::FillSetting)) { $fills = @($set[[CalcFlow.EngineerWorkbook]::FillSetting]) }
                }
                Set-CfInputFills $fills
                $h = [CalcFlow.Harvest]::Run([string[]]$tfiles, $tdir)
                foreach ($k in $h.Skipped) { Write-Host ("[skip ] {0}: {1}" -f $k[0], $k[1]) -ForegroundColor Yellow }
                foreach ($p in $h.Problems) { Write-Host "[error] $p" -ForegroundColor Red }
                Write-Host ("learned  {0} template workbook(s): {1} inputs, {2} results" -f $h.Templates.Count, $h.Inputs.Count, $h.Outputs.Count)
                Show-CfColoured $h
            }
        }
        else { Write-Host 'No templates folder is given (Start tab): the template tabs stay as they are.' -ForegroundColor Yellow }
        $pkg = $null
        if (Test-Path -LiteralPath (Join-Path (Join-Path $dir 'calcflow') 'current-package.txt')) { $pkg = Get-CfCurrentPackage $dir }
        else { Write-Host 'No model has been read yet (Post-process .ANL): Model Config and Load Cases stay as they are.' -ForegroundColor Yellow }
        $res = [CalcFlow.EngineerWorkbook]::Refresh($wb, (Join-Path $PSScriptRoot 'CalcFlow.ps1'), $pkg, $h, $tdir)
        foreach ($n in $res.Notes) { Write-Host "[note ] $n" -ForegroundColor Yellow }
        Write-Host ("kept     {0} (the workbook as it was)" -f $res.Backup)
        $line = [CalcFlow.ProjectBook]::SheetsLine($res.Workbook)
        if ($line) { Write-Host "sheets   $line" }
        Write-Host ("written  {0}: everything you had filled in is carried over ({1} note(s) above)" -f $res.Workbook, $res.Notes.Count) -ForegroundColor Green
        if (-not $NoOpen -and (Test-CfWindows) -and [Environment]::UserInteractive) { Invoke-Item -LiteralPath $res.Workbook }
        exit 0
    }
    'map' {
        Need 'Templates' $Templates
        Need 'Out' $Out
        $folder = Get-CfFull $Out
        Assert-CfOutsideRepo $folder
        $tdir = Get-CfFull $Templates[0]
        $tfiles = Get-CfTemplateFiles $Templates
        if ($tfiles.Count -eq 0) { Stop-CfArgs "No template workbooks found in $tdir." }
        $found = New-Object 'System.Collections.Generic.List[System.Collections.Generic.KeyValuePair[string,CalcFlow.TemplateMapResult]]'
        $bad = 0
        foreach ($f in $tfiles) {
            $rel = $f
            if ($f.StartsWith($tdir, [StringComparison]::OrdinalIgnoreCase)) { $rel = $f.Substring($tdir.Length).TrimStart('\', '/').Replace('\', '/') }
            try { $m = [CalcFlow.TemplateMap]::Read($f, [string[]]$InputFill) }
            catch {
                $why = $_.Exception.Message
                if ($_.Exception.InnerException) { $why = $_.Exception.InnerException.Message }
                Write-Host ("[error] {0}: {1}" -f $rel, $why) -ForegroundColor Red
                $bad++
                continue
            }
            $found.Add((New-Object 'System.Collections.Generic.KeyValuePair[string,CalcFlow.TemplateMapResult]' $rel, $m))
            $cells = @($m.Blocks | Where-Object { $_.Rows -eq 1 }).Count
            $tables = @($m.Blocks | Where-Object { $_.Rows -gt 1 })
            $other = @($m.Fills | Where-Object { -not $_.Input } | ForEach-Object { $_.Cells } | Measure-Object -Sum).Sum
            Write-Host ("found    {0}: {1} cell(s) or rows of cells, {2} list(s) or table(s) ({3} written by the tool); {4} cell(s) of other fills not taken" -f $rel, $cells, $tables.Count, @($tables | Where-Object { $_.LoadRows }).Count, [int]$other)
            foreach ($n in $m.Notes) { Write-Host ("[note ] {0}: {1}" -f $rel, $n) -ForegroundColor Yellow }
        }
        if (-not (Test-Path -LiteralPath $folder)) { New-Item -ItemType Directory -Force -Path $folder | Out-Null }
        $path = Join-Path $folder 'Template_Map.xlsx'
        [CalcFlow.XlsxWriter]::Write($path, [CalcFlow.XOutSheet[]]@([CalcFlow.TemplateMap]::Sheets($found)), $null)
        Write-Host "written  $path" -ForegroundColor Green
        if ($bad -gt 0) { exit 1 }
        exit 0
    }
    'excel-check' {
        Need 'Workbook' $Workbook
        $wb = Get-CfFull $Workbook
        if (-not (Test-Path -LiteralPath $wb)) { Stop-CfArgs "No such workbook: $wb" }
        Write-Host ("workbook {0}" -f $wb)
        $found = @([CalcFlow.XlsxCheck]::Tables($wb)) + @([CalcFlow.XlsxCheck]::Validations($wb))
        foreach ($line in $found) { Write-Host ("[fix  ] {0}" -f $line) -ForegroundColor Red }
        if ($found.Count -eq 0) { Write-Host 'tables   nothing in them, or in the drop-downs and cell notes, breaks a rule of Excel that this check knows' -ForegroundColor Green }
        else { Write-Host ("tables   {0} thing(s) Excel repairs or removes on opening" -f $found.Count) -ForegroundColor Red }
        if ($Repaired) {
            $rp = Get-CfFull $Repaired
            if (-not (Test-Path -LiteralPath $rp)) { Stop-CfArgs "No such workbook: $rp" }
            $changed = @([CalcFlow.XlsxCheck]::Changed($wb, $rp))
            foreach ($line in $changed) { Write-Host ("[excel] {0}" -f $line) -ForegroundColor Yellow }
            Write-Host ("repaired {0} change(s) by Excel in the tables of {1}" -f $changed.Count, $rp)
        }
        if ($found.Count -gt 0) { exit 1 }
        exit 0
    }
    'launchers' {
        Need 'Workbook' $Workbook
        $wb = Get-CfFull $Workbook
        if (-not (Test-Path -LiteralPath $wb)) { Stop-CfArgs "No such workbook: $wb" }
        foreach ($f in [CalcFlow.EngineerWorkbook]::WriteLaunchers((Split-Path -Parent $wb), (Join-Path $PSScriptRoot 'CalcFlow.ps1'))) { Write-Host "written  $f" -ForegroundColor Green }
        exit 0
    }
    'post-process' {
        Need 'Workbook' $Workbook
        $wb = Get-CfFull $Workbook
        if (-not (Test-Path -LiteralPath $wb)) { Stop-CfArgs "No such workbook: $wb (CalcFlow.ps1 new -Out <folder> writes one)" }
        $dir = Split-Path -Parent $wb
        Assert-CfOutsideRepo $dir
        $anlPath = ''
        if ($Anl) { $anlPath = Get-CfFull $Anl }
        else {
            $set = [CalcFlow.EngineerWorkbook]::Settings($wb)
            $named = ''
            if ($set.ContainsKey([CalcFlow.EngineerWorkbook]::AnlSetting)) { $named = $set[[CalcFlow.EngineerWorkbook]::AnlSetting] }
            if ($named) {
                # a name without a folder is beside the workbook
                $anlPath = if ([IO.Path]::IsPathRooted($named)) { $named } else { Join-Path $dir $named }
            }
            else {
                Write-Host 'Choose the STAAD analysis output (.anl) in the dialog ...'
                $anlPath = Select-CfAnl $dir
                if (-not $anlPath) { Stop-CfArgs "No .anl was chosen. Give one in the dialog, in the workbook's `"Anl file`" setting (Start tab), or with -Anl." }
            }
        }
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $pkg = Read-CfAnl $anlPath $dir
        $pkgDir = $pkg.Folder
        $res = [CalcFlow.ModelData]::Write($pkg, $dir)
        Write-Host ("model    {0} members, {1} joints, {2} load cases, {3} end-force rows ({4:0.0} s)" -f $res.Members, $res.Joints, $res.Cases, $res.EndForceRows, $sw.Elapsed.TotalSeconds)
        Write-Host "package  $pkgDir"
        $also = @()
        if ($res.ReactionRows -gt 0) { $also += "$($res.ReactionRows) support reactions" }
        if ($res.DisplacementRows -gt 0) { $also += "$($res.DisplacementRows) joint displacements" }
        if ($res.CodeCheckRows -gt 0) { $also += "$($res.CodeCheckRows) code check results" }
        if ($res.MemberInfoRows -gt 0) { $also += "member information" }
        if ($res.MemberPropertyRows -gt 0) { $also += "member properties" }
        if ($res.SectionDisplacementRows -gt 0) { $also += "$($res.SectionDisplacementRows) section displacement rows" }
        if ($also.Count -gt 0) { Write-Host ("         with {0}" -f ($also -join ', ')) }
        [void](Show-CfDrift $wb $pkg)
        Write-Host ("written  {0}" -f $res.Path) -ForegroundColor Green
        if ($res.ForceSheets -gt 1) { Write-Host ("         All End Forces is on {0} tabs (a tab holds {1:n0} rows)" -f $res.ForceSheets, [CalcFlow.ModelData]::RowsPerSheet) }
        if ($res.NotRead.Count -gt 0) { Write-Host ("         not read yet (listed on the About tab): {0}" -f ($res.NotRead -join '; ')) -ForegroundColor Yellow }
        if ([IO.Path]::GetFileName($res.Path) -ne [CalcFlow.ModelData]::FileName) { Write-Host ("         {0} is open in Excel, so this run wrote a numbered copy." -f [CalcFlow.ModelData]::FileName) -ForegroundColor Yellow }
        if (-not $NoOpen -and (Test-CfWindows) -and [Environment]::UserInteractive) { Invoke-Item -LiteralPath $res.Path }
        exit 0
    }
    'harvest' {
        Need 'Templates' $Templates
        $files = New-Object System.Collections.Generic.List[string]
        $root = ''
        foreach ($t in $Templates) {
            $full = Get-CfFull $t
            if (Test-Path -LiteralPath $full -PathType Container) {
                if (-not $root) { $root = $full }
                $patterns = '*.xlsx', '*.xlsm', '*.xltx', '*.xltm', '*.xls', '*.xlsb', '*.xlt'
                foreach ($f in Get-ChildItem -LiteralPath $full -Recurse -File -Include $patterns | Where-Object { $_.Name -notlike '~$*' } | Sort-Object FullName) { $files.Add($f.FullName) }
            }
            elseif (Test-Path -LiteralPath $full -PathType Leaf) { $files.Add($full) }
            else { Stop-CfArgs "No such template file or folder: $t" }
        }
        if ($files.Count -eq 0) { Stop-CfArgs 'No .xlsx templates found.' }
        $outPath = if ($Out) { Get-CfFull $Out } else { Get-CfFull 'harvest.xlsx' }
        Assert-CfOutsideRepo $outPath
        $r = [CalcFlow.Harvest]::Run([string[]]$files.ToArray(), $root)
        foreach ($t in $r.Templates) { Write-Host ("template {0}  ({1})" -f $t['Template'], $t['File']); Write-Host ("         {0}" -f $t['Notes']) -ForegroundColor DarkGray }
        foreach ($k in $r.Skipped) { Write-Host ("[skip ] {0}: {1}" -f $k[0], $k[1]) -ForegroundColor Yellow }
        foreach ($p in $r.Problems) { Write-Host "[error] $p" -ForegroundColor Red }
        $written = [CalcFlow.Harvest]::WriteWorkbook($r, $outPath)
        Write-Host ("harvest  {0}: {1} templates, {2} inputs, {3} outputs" -f $outPath, $r.Templates.Count, $r.Inputs.Count, $r.Outputs.Count) -ForegroundColor Green
        Write-Host ("layouts  {0} cells in {1} workbook(s) in {2}" -f $r.Layout.Count, ($written.Count - 1), (Join-Path (Split-Path -Parent $outPath) 'harvest-layout'))
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
        [CalcFlow.PlanManifest]::Write($outPath, $pkg, $lib, $reg, $tdir)
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
    'run' {
        $P = Open-CfPlan
        $stale = [CalcFlow.PlanManifest]::Stale($P.Dir)
        if ($stale.Count -gt 0) {
            foreach ($w in $stale) { Write-Host "[stale] $w" -ForegroundColor Red }
            Stop-CfArgs 'The plan is out of date: run plan again, then run.'
        }
        $tdir = if ($Templates) { Get-CfFull $Templates[0] } else { $P.Set.Source('templates') }
        $eng = New-CfEngine $P.Work
        $jobs = [CalcFlow.CalcJob]::FromPlan($P.Set, $P.Library, $tdir)
        $runner = New-Object CalcFlow.CalcRunner -ArgumentList @($P.Work, $P.Rev, $P.Set.Source('package'))
        $runner.KeepRunFolder = [bool]$KeepRun
        $runner.Log = [Action[string]] { param($m) Write-Host "  $m" }
        $calcs = New-Object 'System.Collections.Generic.List[string]'
        foreach ($c in @($Calc)) { if ($c) { $calcs.Add($c) } }
        $due = $runner.Due($jobs, [bool]$All, $calcs)
        Write-Host ''
        Write-Host ("plan     {0}: {1} calc steps ready, {2} due (rev {3})" -f $P.Dir, $jobs.Count, $due.Count, $P.Rev)
        foreach ($g in @($due | Group-Object Due)) { Write-Host ("         {0}: {1}" -f $g.Name, $g.Count) -ForegroundColor DarkGray }
        Write-Host ("engine   {0} {1}" -f $eng.Name, $eng.Version)
        $bad = 0
        if ($due.Count -gt 0) {
            $s = $runner.Run($eng, $due)
            foreach ($m in $s.Messages) { Write-Host "  [error] $m" -ForegroundColor Red }
            $color = if ($s.Failed -gt 0 -or $s.Invalid -gt 0) { 'Red' } else { 'Green' }
            Write-Host ("run      {0} calc steps: {1} OK, {2} invalid, {3} failed, {4} carried forward; {5} cases in {6} workbooks ({7:0.0} s)" -f $s.Jobs, $s.Ok, $s.Invalid, $s.Failed, $s.Carried, $s.Cases, $s.Evaluations, $s.Seconds) -ForegroundColor $color
            $bad = $s.Failed + $s.Invalid
        }
        else { Write-Host 'run      every calc step is up to date (-All runs them again)' -ForegroundColor Green }
        [void](Write-CfSummary $P)
        if ($bad -gt 0) { exit 1 }
        exit 0
    }
    'summary' {
        $P = Open-CfPlan
        $stale = [CalcFlow.PlanManifest]::Stale($P.Dir)
        foreach ($w in $stale) { Write-Host "[stale] $w (the summary shows the plan as it was)" -ForegroundColor Yellow }
        $sum = Write-CfSummary $P
        if (($sum.Count('Failed') + $sum.Count('Invalid')) -gt 0) { exit 1 }
        exit 0
    }
}
