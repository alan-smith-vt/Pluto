using module .\std_Utils.psm1

$dataDir = Join-Path $PSScriptRoot "..\_STAADProcessedData"   # sanitized: original used an undefined $currDir prefix
$basePath = "C:/Temp/_STAADProcessedData/"
$workingDir = $basePath + "WorkingDirectory\"

#Write-Host "$PSScriptRoot"

# One Add-Type batch: the lib (this folder) + the Pluto arms (../arms). Path-anchored on this
# file so the caller's working directory does not matter (was ".\lib\" -- cwd-relative).
$csFiles = Get-ChildItem -Path $PSScriptRoot, (Join-Path $PSScriptRoot "..\arms") -Recurse -Filter *.cs | Select-Object -ExpandProperty FullName
$excelAssembly = [System.Reflection.Assembly]::LoadWithPartialName("Microsoft.Office.Interop.Excel")

$refAssemblies = @($excelAssembly.Location,
	"System.Console", "System.IO",
	"System.Collections", "System.Numerics",
	"System.Numerics.Vectors", "System.Web.Extensions",
	"System.Net.Http", "System.Xml","System.Xml.Linq", 
	"System.IO.Compression", "System.IO.Compression.FileSystem")

Add-Type -path $csFiles -ReferencedAssemblies $refAssemblies

#Poor man's ternary operator (curse ye powershell 5.1 restrictions)
function Iif($cond, $t, $f) { if ($cond) { $t } else { $f } }