# Fixture.ps1 -- the generic fixture project the run, engine and summary tests share: the frame-demo .anl's package,
# the example calc library on the generic templates (tests\fixtures\templates), and a register giving five member
# ends the three example types (two column bases as one group, two beam ends into columns, a brace with two steps and
# a step override), the combinations in scope taken as LRFD. The same project as Plan.Tests.ps1 builds.

function New-CfFixtureProject([string]$Dir, [hashtable]$Extra) {
    New-Item -ItemType Directory -Force -Path $Dir | Out-Null
    $pkgDir = Join-Path $Dir 'R01'
    [void][FramePackageWriter]::Write([AnlFrameReader]::Read((Join-Path $script:Fixtures 'frame-demo.anl')), $pkgDir)
    $library = Join-Path (Join-Path $script:CalcFlow 'examples') 'library'
    $seed = Join-Path $Dir 'seed.xlsx'
    [CalcFlow.Register]::WriteNew($seed, [CalcFlow.AnalysisPackage]::Load($pkgDir), [CalcFlow.Library]::Load($library))
    $assign = @{
        'J1-M1A' = @{ Type = 'BP-1'; Group = 'G-BP' }
        'J4-M3B' = @{ Type = 'BP-1'; Group = 'G-BP' }
        'J2-M2A' = @{ Type = 'BC-WELD' }
        'J3-M2B' = @{ Type = 'BC-WELD' }
        'J1-M4A' = @{ Type = 'BRC-GUSSET' }
    }
    if ($Extra) { foreach ($k in $Extra.Keys) { $assign[$k] = $Extra[$k] } }
    $wb = [CalcFlow.Xlsx]::Read($seed)
    $conns = New-Object 'System.Collections.Generic.List[System.Collections.Generic.Dictionary[string,string]]'
    foreach ($r in $wb.TableRows('tblConnections')) {
        [void]$r.Remove('__row')
        $a = $assign[$r['Connection']]
        if ($a) { foreach ($k in $a.Keys) { $r[$k] = $a[$k] } }
        $conns.Add($r)
    }
    $combos = $wb.TableRows('tblCombos'); foreach ($r in $combos) { [void]$r.Remove('__row'); if ($r['Use'] -eq 'Y') { $r['Basis'] = 'LRFD' } }
    $settings = $wb.TableRows('tblSettings'); foreach ($r in $settings) { [void]$r.Remove('__row') }
    $ov = New-Object 'System.Collections.Generic.List[System.Collections.Generic.Dictionary[string,string]]'
    $o = New-Object 'System.Collections.Generic.Dictionary[string,string]' ([StringComparer]::OrdinalIgnoreCase)
    $o['Connection'] = 'J1-M4A'; $o['Step'] = '2'; $o['Param'] = 'wleg'; $o['Value'] = '0.375'; $o['Note'] = 'heavier gusset weld'; $ov.Add($o)
    $register = Join-Path $Dir 'register.xlsx'
    [CalcFlow.Register]::WriteTables($register, $conns, $ov, $combos, $settings, $null)
    return [pscustomobject]@{
        Dir = $Dir; Package = $pkgDir; Library = $library; Register = $register
        Templates = (Join-Path $script:Fixtures 'templates'); Plan = (Join-Path (Join-Path $Dir 'calcflow') 'plan'); Work = (Join-Path $Dir 'calcflow')
    }
}

# CalcFlow.ps1 as a child process: its output lines, and $LASTEXITCODE
function Invoke-CfCli {
    $exe = (Get-Process -Id $PID).Path
    return @(& $exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $script:CalcFlow 'CalcFlow.ps1') @args 2>&1 | ForEach-Object { "$_" })
}

# the plan of a fixture project, written by the CLI (so it has its manifest); -AllowGaps takes a plan with errors
function New-CfFixturePlan($P, [string]$Library, [switch]$AllowGaps) {
    if (-not $Library) { $Library = $P.Library }
    $o = Invoke-CfCli plan -Package $P.Package -Library $Library -Register $P.Register -Templates $P.Templates -Out $P.Plan
    if ($LASTEXITCODE -ne 0 -and -not ($AllowGaps -and $LASTEXITCODE -eq 1)) { throw "plan failed: $($o -join "`n")" }
    return [CalcFlow.PlanSet]::Load($P.Plan)
}

# ---- the tests' engine on the fixture templates ----

# where each template tab takes Fx, Fy, Fz and gives its governing DCR (from the library, as the engine must find them)
function Get-Spec($Library) {
    $spec = @()
    foreach ($t in $Library.Templates.Values) {
        $loads = @($t.Inputs | Where-Object { $_.Binding -in 'load:Fx', 'load:Fy', 'load:Fz' } | ForEach-Object { "$($_.Sheet)!$($_.Cell)" })
        $dcr = @($t.Outputs | Where-Object { $_.Kind -eq 'dcr' } | ForEach-Object { "$($_.Sheet)!$($_.Cell)" })
        $spec += [pscustomobject]@{ Template = $t.Id; Sheet = $t.Inputs[0].Sheet; Loads = $loads; Dcr = $dcr }
    }
    return $spec
}

function Get-Cell($Book, [string]$SheetCell) {
    $i = $SheetCell.LastIndexOf('!')
    $s = $null; $r = 0; $c = 0; $e = ''
    if (-not $Book.Locate($SheetCell.Substring(0, $i), $SheetCell.Substring($i + 1), [ref]$s, [ref]$r, [ref]$c, [ref]$e)) { throw $e }
    return $s.Get($r, $c)
}

# DCR = (|Fx| + |Fy| + |Fz|) / 10 from the copy's own load cells; the base plate's solver checks pass. $Twist (a
# scriptblock on the book, the tab's spec and the outputs) changes what a variant returns.
function New-Engine([scriptblock]$Twist) {
    $script:Twist = $Twist
    $f = [Func[CalcFlow.TemplateBook, System.Collections.Generic.IDictionary[string, object]]] {
        param($b)
        $d = New-Object 'System.Collections.Generic.Dictionary[string,object]'
        foreach ($t in $script:Spec) {
            if (-not $b.Sheet($t.Sheet)) { continue }
            $sum = 0.0
            foreach ($l in $t.Loads) { $sum += [Math]::Abs([double](Get-Cell $b $l).Value) }
            foreach ($o in $t.Dcr) { $d[$o] = $sum / 10.0 }
            if ($t.Template -eq 'BP') { $d['Rigid!Converged'] = $true; $d['Rigid!Singular'] = $false }
            if ($script:Twist) { & $script:Twist $b $t $d }
        }
        return $d
    }
    return New-Object CalcFlow.ScriptedEngine -ArgumentList @(, $f)
}

# a minimal .xlsx from parts (no Excel, no writer): one sheet "Calc" whose sheet XML is given whole
function New-MiniXlsx([string]$Path, [string]$SheetXml, [string]$Names) {
    New-MiniBook $Path ([ordered]@{ 'Calc' = $SheetXml }) $Names
}

# the same with several sheets: an ordered table of sheet name -> sheet XML (cell style 1 is an unlocked cell).
# More: other parts by their path, in place of the ones written here (xl/styles.xml) or more (xl/theme/theme1.xml)
function New-MiniBook([string]$Path, [System.Collections.Specialized.OrderedDictionary]$Sheets, [string]$Names, [hashtable]$More) {
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
    $ns = 'http://schemas.openxmlformats.org/spreadsheetml/2006/main'
    $rel = 'http://schemas.openxmlformats.org/officeDocument/2006/relationships'
    $types = ''; $list = ''; $rels = ''; $n = 0
    $parts = [ordered]@{}
    foreach ($name in $Sheets.Keys) {
        $n++
        $types += "<Override PartName=`"/xl/worksheets/sheet$n.xml`" ContentType=`"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml`"/>"
        $list += "<sheet name=`"$([Security.SecurityElement]::Escape($name))`" sheetId=`"$n`" r:id=`"rId$n`"/>"
        $rels += "<Relationship Id=`"rId$n`" Type=`"$rel/worksheet`" Target=`"worksheets/sheet$n.xml`"/>"
    }
    $parts['[Content_Types].xml'] = '<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>' + $types + '<Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/></Types>'
    $parts['_rels/.rels'] = '<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="' + $rel + '/officeDocument" Target="xl/workbook.xml"/></Relationships>'
    $parts['xl/workbook.xml'] = '<?xml version="1.0" encoding="UTF-8"?><workbook xmlns="' + $ns + '" xmlns:r="' + $rel + '"><sheets>' + $list + '</sheets><definedNames>' + $Names + '</definedNames></workbook>'
    $parts['xl/_rels/workbook.xml.rels'] = '<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">' + $rels + '<Relationship Id="rId' + ($n + 1) + '" Type="' + $rel + '/styles" Target="styles.xml"/></Relationships>'
    $parts['xl/styles.xml'] = '<?xml version="1.0" encoding="UTF-8"?><styleSheet xmlns="' + $ns + '"><cellXfs count="2"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/><xf numFmtId="0" fontId="0" fillId="0" borderId="0" applyProtection="1"><protection locked="0"/></xf></cellXfs></styleSheet>'
    if ($More) { foreach ($k in $More.Keys) { $parts[$k] = $More[$k] } }
    $n = 0
    foreach ($name in $Sheets.Keys) { $n++; $parts["xl/worksheets/sheet$n.xml"] = '<?xml version="1.0" encoding="UTF-8"?><worksheet xmlns="' + $ns + '">' + $Sheets[$name] + '</worksheet>' }
    $fs = [IO.File]::Create($Path)
    try {
        $zip = New-Object System.IO.Compression.ZipArchive($fs, [System.IO.Compression.ZipArchiveMode]::Create)
        foreach ($k in $parts.Keys) {
            $e = $zip.CreateEntry($k)
            $w = New-Object IO.StreamWriter($e.Open(), (New-Object Text.UTF8Encoding($false)))
            $w.Write($parts[$k]); $w.Dispose()
        }
        $zip.Dispose()
    }
    finally { $fs.Dispose() }
}

# the column letter of a heading on a tab (schedule tabs: the heading row is 5; the others: 2)
function Get-Col($Book, [string]$Tab, [string]$Heading, [int]$Row) {
    $s = $Book.Sheet($Tab)
    for ($c = 0; $c -lt 300; $c++) { if ($s.Text($Row - 1, $c).Trim() -eq $Heading) { return [CalcFlow.Xlsx]::ColName($c) } }
    throw "no heading '$Heading' on $Tab"
}

# a copy of a workbook with cells written, as Excel would save them
function Save-Cells([string]$From, [string]$To, [object[]]$Cells) {
    $w = New-Object 'System.Collections.Generic.List[CalcFlow.CellWrite]'
    foreach ($c in $Cells) { $w.Add([CalcFlow.CellWrite]::new([string]$c[0], [string]$c[1], $c[2])) }
    [CalcFlow.XlsxPatch]::Write($From, $To, $w, $false)
}
