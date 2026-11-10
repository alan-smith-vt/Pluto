# Tests for the wall thickness table (duct\model\DuctThickness.cs). Windows PowerShell 5.1, a few seconds:
#   powershell -ExecutionPolicy Bypass -File scripts\duct\tests\Test-DuctThickness.ps1
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "..\..\arms\Import-Arms.ps1")
$script:fail = 0; $script:pass = 0
function Check($name, $cond) { if ($cond) { $script:pass++ } else { $script:fail++; Write-Output "FAIL: $name" } }
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("pluto-thk-" + [Guid]::NewGuid().ToString("N").Substring(0, 8) + ".csv")
function Table([string[]]$lines) { Set-Content $tmp (@("Run,Size,Thk_in") + $lines) -Encoding ASCII; New-Object DuctThickness $tmp }
function Near($a, $b) { [math]::Abs($a - $b) -lt 1e-9 }

$t = Table @(
    "SYSA,24x12,0.0625",
    "SYSA,13-24x0-60,0.0781",
    "SYSA,8-36 dia,0.05",
    "SYSA,18 dia,0.04",
    "SYSA,*,0.1094",
    "EXH,0-12x*,0.0312",
    "# comment line",
    "EXH,0-12x*,0.9"
)
Check "1 rows read (comment skipped)" ($t.Rows.Count -eq 7)
Check "2 exact rect beats range and *" (Near $t.Lookup("SYSA", $false, 24, 12, [double]::NaN) 0.0625)
Check "3 exact rect, either orientation" (Near $t.Lookup("SYSA", $false, 12, 24, [double]::NaN) 0.0625)
Check "4 range rect" (Near $t.Lookup("SYSA", $false, 20, 40, [double]::NaN) 0.0781)
Check "5 range rect, either orientation" (Near $t.Lookup("SYSA", $false, 40, 20, [double]::NaN) 0.0781)
Check "6 range ends inclusive" (Near $t.Lookup("SYSA", $false, 13, 60, [double]::NaN) 0.0781)
Check "7 outside the range -> *" (Near $t.Lookup("SYSA", $false, 30, 30, [double]::NaN) 0.1094)
Check "8 exact round beats round range" (Near $t.Lookup("SYSA", $true, [double]::NaN, [double]::NaN, 18) 0.04)
Check "9 round range" (Near $t.Lookup("SYSA", $true, [double]::NaN, [double]::NaN, 30) 0.05)
Check "10 round never matches a rect row" (Near $t.Lookup("SYSA", $true, [double]::NaN, [double]::NaN, 24) 0.05)
Check "11 round outside its range -> *" (Near $t.Lookup("SYSA", $true, [double]::NaN, [double]::NaN, 40) 0.1094)
Check "12 run case-insensitive" (Near $t.Lookup("sysa", $false, 24, 12, [double]::NaN) 0.0625)
Check "13 fragments and other: match the run" ((Near $t.Lookup("SYSA fragments", $false, 24, 12, [double]::NaN) 0.0625) -and (Near $t.Lookup("other: SYSA", $false, 24, 12, [double]::NaN) 0.0625))
Check "14 unknown run -> NaN (placeholder)" ([double]::IsNaN($t.Lookup("SYSB", $false, 24, 12, [double]::NaN)))
Check "15 * side in a range: 10 x 500 matches 0-12x*" (Near $t.Lookup("EXH", $false, 500, 10, [double]::NaN) 0.0312)
$o = $t.Overlaps
$null = $t.Lookup("EXH", $false, 10, 10, [double]::NaN)
Check "16 two equal rows: first in the file, overlap counted" ((Near $t.Lookup("EXH", $false, 10, 10, [double]::NaN) 0.0312) -and $t.Overlaps -gt $o)
Check "17 EXH 20x20: no row -> NaN" ([double]::IsNaN($t.Lookup("EXH", $false, 20, 20, [double]::NaN)))

foreach ($bad in @("SYSA,24x,0.06", "SYSA,24x12,0", "SYSA,big,0.06", ",24x12,0.06", "SYSA,24-12x12,0.06")) {
    $threw = $false; try { $null = Table @($bad) } catch { $threw = $true }
    Check "18 bad row refused: $bad" $threw
}
$threw = $false; Set-Content $tmp "Run,Thk_in`nSYSA,0.06" -Encoding ASCII; try { $null = New-Object DuctThickness $tmp } catch { $threw = $true }
Check "19 wrong header refused" $threw

# Apply on clean edges: table thickness, placeholder (NaN) where no row, rigid untouched, transition takes the larger end
$t = Table @("SYSA,24x12,0.0625")
$e1 = New-Object CEdge; $e1.SysName = "SYSA"; $e1.Dim = [double[]](0, 24, 12, [double]::NaN); $e1.Thk = 0.5
$e2 = New-Object CEdge; $e2.SysName = "SYSA"; $e2.Dim = [double[]](0, 30, 30, [double]::NaN); $e2.Thk = 0.5
$e3 = New-Object CEdge; $e3.SysName = "SYSA"; $e3.Rigid = $true; $e3.Thk = 0.5
$e4 = New-Object CEdge; $e4.SysName = "SYSA"; $e4.Dim = [double[]](0, 10, 10, [double]::NaN); $e4.DimB = [double[]](0, 24, 12, [double]::NaN); $e4.Thk = 0.5
$list = New-Object 'System.Collections.Generic.List[CEdge]'; $e1, $e2, $e3, $e4 | ForEach-Object { $list.Add($_) }
$t.Apply($list, $true)
Check "20 table value replaces the stated one" (Near $e1.Thk 0.0625)
Check "21 no row: NaN even with a stated thickness (Navisworks ignored)" ([double]::IsNaN($e2.Thk))
Check "22 rigid untouched" (Near $e3.Thk 0.5)
Check "23 transition: larger end" (Near $e4.Thk 0.0625)
Check "24 counts and note" ($t.Config -eq 2 -and $t.Placeholder -eq 1 -and $t.Note() -match "SYSA 30x30 \(1\)" -and $t.Note() -match "1 frames PLACEHOLDER")

Remove-Item $tmp -ErrorAction SilentlyContinue
Write-Output ("{0} passed, {1} failed" -f $script:pass, $script:fail)
exit $(if ($script:fail) { 1 } else { 0 })
