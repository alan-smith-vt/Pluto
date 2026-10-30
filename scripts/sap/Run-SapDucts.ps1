# HVAC duct SAP model from a Revit overlay run's frames.csv (Export-DuctsViewer -Revit). Windows PowerShell 5.1.
#   .\Run-SapDucts.ps1 -Frames C:\Temp\hvac\ducts\<run>\revit_<room>.frames.csv -SavePath C:\Temp\hvac\sap\ducts_<room>.sdb
# Builds a NEW blank model (geometry, sections with the parts' wall thickness, box width along local 3 from the
# frames' width direction, PLACEHOLDER material DUCT_MAT_TBD, DUCT_ALL / SRC_ / SYS_ / FLAG_ groups; no loads) and saves
# it at -SavePath. If a model is open in SAP it is reopened afterwards so SAP is left where it was.
# Only the primary systems (mains and pinned mains) go in by default; -Fragments, -Other (other systems) and
# -NavisOnly (Navisworks rows with no Revit part) add those. Frames flagged FLOATING (a duct piece with no support) are
# left out (stability rule 1; -KeepFloating keeps them). Each frame's GUID field names its Revit / Navisworks ids.
# Steel and supports (SapSteel.cs; each optional):
#   -SteelFrames <out>.steel_frames.csv  W sections, M2+M3 releases, cardinal points; joints and restraints from
#                -SteelJoints (default: the same name with steel_joints). Groups STEEL_ALL, STEEL_RELEASED, STEEL_<condition>
#   -Supports    <base>.supports.csv: TAP_STUB frames duct joint -> steel joint, groups SUPPORT_ALL, SUPPORT_<type>
#   -Flags       csv Frame,Flag: extra FLAG_ groups (FLAG_UNSUPPORTED_SPAN)
#   -PlantRotZ   plant axes: coordinate system PLANT turned this many degrees about Z (opt-in, e.g. 90; default none)
param(
    [Parameter(Mandatory = $true)][string]$Frames,
    [Parameter(Mandatory = $true)][string]$SavePath,
    [switch]$Fragments,
    [switch]$Other,
    [switch]$NavisOnly,
    [switch]$KeepFloating,
    [string]$SteelFrames,
    [string]$SteelJoints,
    [string]$Supports,
    [string]$Flags,
    [string]$PlantRotZ = "none",
    [string]$SapDir = $env:PLUTO_SAP_DIR
)
$include = @($(if ($Fragments) { "fragment" }), $(if ($Other) { "other" }), $(if ($NavisOnly) { "navisworks-only" })) -join ","
$ErrorActionPreference = "Stop"
$Frames = [IO.Path]::GetFullPath($Frames); $SavePath = [IO.Path]::GetFullPath($SavePath)
foreach ($p in @($Frames, $SteelFrames, $SteelJoints, $Supports, $Flags)) { if ($p -and -not (Test-Path $p)) { throw "No $p" } }
. (Join-Path $PSScriptRoot "Import-SapApi.ps1")   # SAP classes, $SapDir, $SapExe
$null = New-Item -ItemType Directory -Force (Split-Path $SavePath)
$sap = [SapSurvey]::AttachOrStart($SapExe, $false)
$d = New-Object SapDucts($sap)
if ($SteelFrames) { $d.SteelFramesCsv = [IO.Path]::GetFullPath($SteelFrames) }
if ($SteelJoints) { $d.SteelJointsCsv = [IO.Path]::GetFullPath($SteelJoints) }
if ($Supports) { $d.SupportsCsv = [IO.Path]::GetFullPath($Supports) }
if ($Flags) { $d.FlagsCsv = [IO.Path]::GetFullPath($Flags) }
$d.KeepFloating = [bool]$KeepFloating
$d.PlantRotZ = $(if ($PlantRotZ -eq "none") { [double]::NaN } else { [double]::Parse($PlantRotZ, [Globalization.CultureInfo]::InvariantCulture) })
$original = $null
try { $original = $d.ModelPath() } catch { }   # PowerShell cannot call the COM model directly
if ($original -and [IO.Path]::GetFullPath($original) -eq $SavePath) { throw "-SavePath must differ from the open model ($original)" }
try {
    $d.Build($Frames, $SavePath, $include)
} finally {
    if ($original -and (Test-Path $original)) { $sap.OpenModel($original, $false) }
    $sap.Close()
}
