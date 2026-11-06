# Library.Tests.ps1 -- the calc library (CalcFlow.Library): the example library (scripts\calcflow\examples\library), the
# workbook <-> canonical text round trip, the refusal of a workbook edited after its export, the frame and binding
# checks, and the units.

$dir = New-TestDir 'library'
$example = Join-Path (Join-Path $script:CalcFlow 'examples') 'library'
$exe = (Get-Process -Id $PID).Path
$cfps = Join-Path $script:CalcFlow 'CalcFlow.ps1'

# a library folder holding only the given tables (file name -> CSV text)
function New-TextLibrary([string]$Name, [hashtable]$Files) {
    $f = Join-Path $dir $Name
    $t = Join-Path $f 'text'
    New-Item -ItemType Directory -Path $t -Force | Out-Null
    foreach ($k in $Files.Keys) { [IO.File]::WriteAllText((Join-Path $t $k), $Files[$k]) }
    return $f
}

Test 'the example library: three templates, three types with their steps, frames, parameters and sections; no errors' {
    $lib = [CalcFlow.Library]::Load($example)
    Assert-Equal $lib.ErrorCount 0 (($lib.Problems | ForEach-Object { $_.ToString() }) -join '; ')
    Assert-Equal (($lib.Templates.Keys | Sort-Object) -join ' ') 'BP WG-I WG-RECT'
    Assert-Equal (($lib.Types.Keys | Sort-Object) -join ' ') 'BC-WELD BP-1 BRC-GUSSET'
    Assert-Equal (($lib.Frames.Keys | Sort-Object) -join ' ') 'BEAM-END-WELD BP-COLUMN BRACE-WELD IDENTITY'
    $g = $lib.Types['BRC-GUSSET']
    Assert-Equal (($g.Steps | ForEach-Object { "$($_.Step):$($_.Template):$($_.Demand)" }) -join ' ') '1:WG-RECT:concurrent_envelope 2:WG-RECT:governing_set'
    Assert-Equal $g.Param('wleg', 2).Default '0.3125' 'a step row wins'
    Assert-Equal $g.Param('FEXX', 2).Default '70' 'else the all-steps row'
    Assert-Equal $lib.Templates['WG-I'].Inputs.Count 24
    Assert-Equal $lib.Sections['W24X55']['tf'][0] '0.505'
    Assert-Equal (@($lib.Problems | ForEach-Object { $_.Message }) -join ' | ') ('frame BEAM-END-WELD (used by BC-WELD step 1) is not confirmed by an engineer | ' +
        'frame BP-COLUMN (used by BP-1 step 1) is not confirmed by an engineer | frame BRACE-WELD (used by BRC-GUSSET step 1) is not confirmed by an engineer')
}

Test 'library-new from the text, then library-export, gives the canonical text back byte for byte' {
    $f = Join-Path $dir 'roundtrip'
    Copy-Item -LiteralPath (Join-Path $example 'text') -Destination (Join-Path $f 'text') -Recurse
    $o = & $exe -NoProfile -ExecutionPolicy Bypass -File $cfps library-new -Library $f -FromText 2>&1 | ForEach-Object { "$_" }
    Assert-Equal $LASTEXITCODE 0 ($o -join "`n")
    Remove-Item -LiteralPath (Join-Path $f 'text') -Recurse
    $o = & $exe -NoProfile -ExecutionPolicy Bypass -File $cfps library-export -Library $f 2>&1 | ForEach-Object { "$_" }
    Assert-Equal $LASTEXITCODE 0 ($o -join "`n")
    foreach ($t in [CalcFlow.LibrarySchema]::Tables) {
        $a = [IO.File]::ReadAllBytes((Join-Path (Join-Path $example 'text') $t.File))
        $b = [IO.File]::ReadAllBytes((Join-Path (Join-Path $f 'text') $t.File))
        Assert-True ([Convert]::ToBase64String($a) -eq [Convert]::ToBase64String($b)) "$($t.File) differs after the round trip"
    }
    Assert-Match ([IO.File]::ReadAllText((Join-Path (Join-Path $f 'text') '_source.txt'))) 'sha256: [0-9a-f]{64}'
    $o = & $exe -NoProfile -ExecutionPolicy Bypass -File $cfps library-new -Library $f 2>&1 | ForEach-Object { "$_" }
    Assert-Equal $LASTEXITCODE 2 'an existing library workbook is never overwritten'
    $script:RoundTripLibrary = $f
}

Test 'a library workbook changed after its last export is refused until it is exported again' {
    $f = $script:RoundTripLibrary
    Assert-Equal ([CalcFlow.Library]::Load($f)).ErrorCount 0
    $wb = Join-Path $f ([CalcFlow.Library]::WorkbookName)
    Remove-Item -LiteralPath $wb
    $rows = New-Object 'System.Collections.Generic.Dictionary[string,System.Collections.Generic.List[System.Collections.Generic.Dictionary[string,string]]]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($t in [CalcFlow.LibrarySchema]::Tables) { $rows[$t.Name] = [CalcFlow.Csv]::ReadTable((Join-Path (Join-Path $f 'text') $t.File)) }
    $rows['tblTypes'][0]['Notes'] = 'edited in Excel'
    [CalcFlow.Library]::WriteWorkbook($wb, $rows)
    $lib = [CalcFlow.Library]::Load($f)
    Assert-Equal $lib.ErrorCount 1
    Assert-Match $lib.Problems[0].Message 'changed after its last export'
    [void][CalcFlow.Library]::Export($wb)
    Assert-Equal ([CalcFlow.Library]::Load($f)).ErrorCount 0
    Assert-Equal @([CalcFlow.Csv]::ReadTable((Join-Path (Join-Path $f 'text') 'types.csv')) | Where-Object { $_['Notes'] -eq 'edited in Excel' }).Count 1
}

Test 'frames: a mirror image needs Mirror = Y, the moments must follow the forces, a force takes a force' {
    $h = 'Frame,Fx,Fy,Fz,Mx,My,Mz,Mirror,Action,Confirmed,Notes'
    $f = New-TextLibrary 'frames' @{ 'frames.csv' = ($h + "`r`nROT,-Fz,+Fy,+Fx,-Mz,+My,+Mx,N,on_connection,Y,`r`nMIR,+Fx,+Fz,+Fy,-Mx,-Mz,-My,N,,Y,`r`nMIRY,+Fx,+Fz,+Fy,-Mx,-Mz,-My,Y,,Y,`r`nBADM,-Fz,+Fy,+Fx,+Mz,+My,+Mx,N,,Y,`r`nMIX,+Mx,+Fy,+Fz,+Mx,+My,+Mz,N,,Y,`r`n") }
    $lib = [CalcFlow.Library]::Load($f)
    $msgs = @($lib.Problems | ForEach-Object { $_.Message })
    Assert-True ($lib.Frames.ContainsKey('ROT') -and $lib.Frames.ContainsKey('MIRY')) 'the rotation and the declared mirror are kept'
    Assert-True (@($msgs | Where-Object { $_ -like 'MIR:*mirror image*' }).Count -eq 1) ($msgs -join '; ')
    Assert-True (@($msgs | Where-Object { $_ -like 'BADM.Mx must be -Mz*' }).Count -eq 1) ($msgs -join '; ')
    Assert-True (@($msgs | Where-Object { $_ -like 'MIX.Fx*a force takes a force*' }).Count -eq 1) ($msgs -join '; ')
    $rot = $lib.Frames['ROT']
    Assert-Equal (($rot.Apply([double[]]@(1, 2, 3, 4, 5, 6)) | ForEach-Object { $_ }) -join ' ') '3 -2 -1 6 -5 -4' 'on_connection negates'
}

Test 'bindings: the grammar, PerCase, and the dimension of the cell are checked' {
    $kip = [CalcFlow.Units]::Parse('kip'); $kipin = [CalcFlow.Units]::Parse('kip-in'); $in = [CalcFlow.Units]::Parse('in')
    Assert-Equal ([CalcFlow.Bindings]::Check('load:Fx', $kip, $true)) $null
    Assert-Match ([CalcFlow.Bindings]::Check('load:Mx', $kip, $true)) 'moment'
    Assert-Match ([CalcFlow.Bindings]::Check('load:Fx', $kip, $false)) 'PerCase = Y'
    Assert-Match ([CalcFlow.Bindings]::Check('param:wleg', $in, $true)) 'PerCase = N'
    Assert-Equal ([CalcFlow.Bindings]::Check('pkg:this.Length', $in, $false)) $null
    Assert-Match ([CalcFlow.Bindings]::Check('pkg:this.Length', $null, $false)) 'give the cell'
    Assert-Match ([CalcFlow.Bindings]::Check('pkg:this.Depth', $in, $false)) 'no field'
    Assert-Match ([CalcFlow.Bindings]::Check('fn:prop(this)', $in, $false)) 'takes 2 arguments'
    Assert-Match ([CalcFlow.Bindings]::Check('fn:len(this)', $kipin, $false)) 'length'
    Assert-Match ([CalcFlow.Bindings]::Check('formula:A1*2', $in, $false)) 'unknown binding kind'
    Assert-Equal ([CalcFlow.Bindings]::Check('case:title', $null, $true)) $null
}

Test 'units: conversions within a dimension; a moment never converts to a force; units read from labels' {
    Assert-Near ([CalcFlow.Units]::Convert(1, [CalcFlow.Units]::Parse('kip-ft'), [CalcFlow.Units]::Parse('kip-in'))) 12
    Assert-Near ([CalcFlow.Units]::Convert(1, [CalcFlow.Units]::Parse('kN'), [CalcFlow.Units]::Parse('kip'))) 0.22480894309971 1e-12
    Assert-Near ([CalcFlow.Units]::Convert(1, [CalcFlow.Units]::Parse('kN-m'), [CalcFlow.Units]::Parse('kip-ft'))) 0.737562149 1e-8
    Assert-Near ([CalcFlow.Units]::Convert(6, [CalcFlow.Units]::Parse('in'), [CalcFlow.Units]::Parse('ft'))) 0.5
    Assert-Throws { [CalcFlow.Units]::Convert(1, [CalcFlow.Units]::Parse('kip-ft'), [CalcFlow.Units]::Parse('kip')) } 'moment'
    Assert-Equal ([CalcFlow.Units]::FromLabel('wleg (in.)')) 'in'
    Assert-Equal ([CalcFlow.Units]::FromLabel("f'c (psi)")) 'psi'
    Assert-Equal ([CalcFlow.Units]::FromLabel('Fu,Base (ksi)')) 'ksi'
    Assert-Equal ([CalcFlow.Units]::FromLabel('Description')) ''
}
