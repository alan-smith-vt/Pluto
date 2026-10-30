using module .\std_Utils.psm1

$dataDir = Join-Path $PSScriptRoot "..\_STAADProcessedData"   # sanitized: original used an undefined $currDir prefix
$basePath = "C:/Temp/_STAADProcessedData/"
$workingDir = $basePath + "WorkingDirectory\"

#Write-Host "$PSScriptRoot"

# One Add-Type batch: the lib (this folder) + the Pluto arms (../arms) + the duct model code the duct arms call
# (../duct/model; the rest of scripts/duct compiles on its own). Path-anchored on this
# file so the caller's working directory does not matter (was ".\lib\" -- cwd-relative).
$csFiles = Get-ChildItem -Path $PSScriptRoot, (Join-Path $PSScriptRoot "..\arms"), (Join-Path $PSScriptRoot "..\duct\model") -Recurse -Filter *.cs | Select-Object -ExpandProperty FullName
$excelAssembly = [System.Reflection.Assembly]::LoadWithPartialName("Microsoft.Office.Interop.Excel")

# References by full path where the Framework folder has the dll. A bare name only resolves while nothing
# else is loaded: once the SAP API is in the session every bare name fails with 0x80131502
# (2026-10-30, Run-Sap -Export). A name with no dll there is passed on as before.
$fwDir = [System.Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()
$refAssemblies = @($excelAssembly.Location) + @("System.Console", "System.IO",
	"System.Collections", "System.Numerics",
	"System.Numerics.Vectors", "System.Web.Extensions",
	"System.Net.Http", "System.Xml","System.Xml.Linq",
	"System.IO.Compression", "System.IO.Compression.FileSystem") | ForEach-Object {
	$p = Join-Path $fwDir "$_.dll"
	if ($_ -like "*\*") { $_ } elseif (Test-Path $p) { $p } else { $_ }
} | Where-Object { $_ }

Add-Type -path $csFiles -ReferencedAssemblies $refAssemblies

#Poor man's ternary operator (curse ye powershell 5.1 restrictions)
function Iif($cond, $t, $f) { if ($cond) { $t } else { $f } }