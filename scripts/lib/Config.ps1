using module .\std_Utils.psm1

$dataDir = Join-Path $PSScriptRoot "..\_STAADProcessedData"   # sanitized: original used an undefined $currDir prefix
$basePath = "C:/Temp/_STAADProcessedData/"
$workingDir = $basePath + "WorkingDirectory\"

#Write-Host "$PSScriptRoot"

# The core C# (this folder only). Domain code is not compiled here: scripts that need the arms or the
# duct model dot-source ..\arms\Import-Arms.ps1 (which loads the core too).
. (Join-Path $PSScriptRoot "Import-PlutoCore.ps1")

#Poor man's ternary operator (curse ye powershell 5.1 restrictions)
function Iif($cond, $t, $f) { if ($cond) { $t } else { $f } }