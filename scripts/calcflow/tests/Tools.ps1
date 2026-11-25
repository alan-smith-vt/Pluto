# Tools.ps1 -- open-source checks of the workbooks CalcFlow writes, for the tests (development side only; nothing in
# CalcFlow itself needs them). Each one says "skipped" when its tool is not on this machine.
#   Invoke-CfOoxmlCheck  Microsoft's Open XML SDK validator (MIT licence; tests\tools\OoxmlCheck, built once with the
#                        dotnet SDK into tests\out\tools): the schema Excel reads. Returns one line per error.
#   Invoke-CfLibreOpen   LibreOffice opens each workbook headless and saves it again; a load error is an error.
#   Export-CfPreview     LibreOffice -> PDF -> poppler's pdftoppm -> PNG pages, to look at a workbook's colours.
# Dot-sourced by Run-Tests.ps1 after the CalcFlow C# is loaded.

$script:ToolsOut = Join-Path (Join-Path $PSScriptRoot 'out') 'tools'

function Get-CfCommand([string]$Name) {
    $c = Get-Command $Name -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($c) { return $c.Source }
    return $null
}

# the OoxmlCheck assembly, built when its source changed; $null when there is no dotnet SDK (or no NuGet to restore from)
function Get-CfOoxmlCheck {
    if ($script:OoxmlDll) { return $script:OoxmlDll }
    $dotnet = Get-CfCommand 'dotnet'
    if (-not $dotnet) { return $null }
    $src = Join-Path (Join-Path $PSScriptRoot 'tools') 'OoxmlCheck'
    $work = Join-Path $script:ToolsOut 'OoxmlCheck-src'
    $bin = Join-Path $script:ToolsOut 'OoxmlCheck'
    $dll = Join-Path $bin 'OoxmlCheck.dll'
    $stamp = Join-Path $bin 'source.sha256'
    $hash = (@(Get-ChildItem -LiteralPath $src -File | Sort-Object Name | ForEach-Object { [CalcFlow.SharedFile]::Sha256($_.FullName) }) -join ' ')
    if ((Test-Path -LiteralPath $dll) -and (Test-Path -LiteralPath $stamp) -and ([IO.File]::ReadAllText($stamp) -eq $hash)) { $script:OoxmlDll = $dll; return $dll }
    # built from a copy in tests\out so that no obj\ or bin\ lands beside the source
    if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $work | Out-Null
    Copy-Item -Path (Join-Path $src '*') -Destination $work
    $o = & $dotnet build (Join-Path $work 'OoxmlCheck.csproj') -c Release -o $bin -nologo 2>&1 | ForEach-Object { "$_" }
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $dll)) {
        Write-Host ('        (OoxmlCheck did not build: ' + (($o | Select-Object -Last 3) -join ' ') + ')') -ForegroundColor DarkGray
        return $null
    }
    [IO.File]::WriteAllText($stamp, $hash)
    $script:OoxmlDll = $dll
    return $dll
}

# schema errors of the files (empty when clean); $null when OoxmlCheck is not available here
function Invoke-CfOoxmlCheck([string[]]$Files) {
    $dll = Get-CfOoxmlCheck
    if (-not $dll) { return $null }
    $o = @(& (Get-CfCommand 'dotnet') $dll $Files 2>&1 | ForEach-Object { "$_" })
    if ($LASTEXITCODE -ne 0 -and $o.Count -eq 0) { return @("OoxmlCheck failed (exit $LASTEXITCODE)") }
    return , $o
}

# what Excel would repair in the tables of the files, and remove of their drop-downs and cell notes (empty when
# clean): Excel's own rules, which neither the schema validator nor LibreOffice checks in full (cs\XlsxCheck.cs)
function Invoke-CfExcelRules([string[]]$Files) {
    $o = New-Object System.Collections.Generic.List[string]
    foreach ($f in $Files) {
        foreach ($line in [CalcFlow.XlsxCheck]::Tables($f)) { $o.Add($line) }
        foreach ($line in [CalcFlow.XlsxCheck]::Validations($f)) { $o.Add($line) }
    }
    return , $o.ToArray()
}

# load errors of the files as LibreOffice opens them (empty when clean); $null when LibreOffice is not installed
function Invoke-CfLibreOpen([string[]]$Files) {
    $so = [CalcFlow.LibreOffice]::Find()
    if (-not $so) { return $null }
    $out = Join-Path $script:ToolsOut ('lo-open-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    $bad = New-Object System.Collections.Generic.List[string]
    # one conversion per batch of files with distinct names (soffice writes <name>.xlsx into one folder)
    $groups = @($Files | Group-Object { [IO.Path]::GetFileName($_).ToLowerInvariant() })
    $depth = 0; foreach ($g in $groups) { if ($g.Count -gt $depth) { $depth = $g.Count } }
    for ($i = 0; $i -lt $depth; $i++) {
        $batch = @($groups | Where-Object { $_.Count -gt $i } | ForEach-Object { $_.Group[$i] })
        $dir = Join-Path $out "$i"
        try { [void][CalcFlow.LibreOffice]::Convert($so, [string[]]$batch, $dir, 'xlsx', (Join-Path $script:ToolsOut 'lo-profile'), 300) }
        catch { $bad.Add($_.Exception.Message) }
    }
    Remove-Item -LiteralPath $out -Recurse -Force -ErrorAction SilentlyContinue
    return , $bad.ToArray()
}

# PNG pages of a workbook (LibreOffice prints it to PDF, pdftoppm rasterises the first $Pages pages); $null without the tools
function Export-CfPreview([string]$File, [string]$OutDir, [int]$Pages = 3, [int]$Dpi = 110) {
    $so = [CalcFlow.LibreOffice]::Find()
    $ppm = Get-CfCommand 'pdftoppm'
    if (-not $so -or -not $ppm) { return $null }
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
    [void][CalcFlow.LibreOffice]::Convert($so, [string[]]@($File), $OutDir, 'pdf', (Join-Path $script:ToolsOut 'lo-profile'), 300)
    $pdf = Join-Path $OutDir ([IO.Path]::GetFileNameWithoutExtension($File) + '.pdf')
    $prefix = Join-Path $OutDir ([IO.Path]::GetFileNameWithoutExtension($File))
    & $ppm -png -r $Dpi -f 1 -l $Pages $pdf $prefix 2>&1 | Out-Null
    return @(Get-ChildItem -LiteralPath $OutDir -Filter ([IO.Path]::GetFileNameWithoutExtension($File) + '-*.png') | Sort-Object Name | ForEach-Object { $_.FullName })
}

# every workbook the tests wrote since $Since under tests\out (the tools' own folder aside; records are copies of a
# template, so a template's own faults would show: Run.Tests.ps1 checks that a copy adds none). Left out too: the
# files the tests patch cell by cell as a stand-in for typing in Excel (patched-*, and the copies a refresh keeps of
# them), where a renamed heading is renamed in its cell only
function Get-CfWrittenWorkbooks([datetime]$Since) {
    $root = Join-Path $PSScriptRoot 'out'
    if (-not (Test-Path -LiteralPath $root)) { return @() }
    $tools = $script:ToolsOut + [IO.Path]::DirectorySeparatorChar
    return @(Get-ChildItem -LiteralPath $root -Recurse -File -Filter '*.xlsx' |
        Where-Object { $_.LastWriteTimeUtc -ge $Since.ToUniversalTime() -and -not $_.FullName.StartsWith($tools) -and $_.FullName -notmatch '[\\/](records|patched-[^\\/]*$|[^\\/]* \(before refresh [^\\/]*$)' } |
        Sort-Object FullName | ForEach-Object { $_.FullName })
}
