using module .\std_Utils.psm1

$dataDir = $currDir + "$PSScriptRoot\..\_STAADProcessedData"
$basePath = "C:/Temp/_STAADProcessedData/"
$workingDir = $basePath + "WorkingDirectory\"

#Write-Host "$PSScriptRoot"

$csFiles = Get-ChildItem -Path ".\lib\" -Recurse -Filter *.cs | Select-Object -ExpandProperty FullName
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