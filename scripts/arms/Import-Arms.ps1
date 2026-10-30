# Compiles the Pluto arms (scripts\arms\*.cs) and the HVAC duct model (scripts\duct\model\*.cs) in one batch
# against the core dll (scripts\lib\Import-PlutoCore.ps1) and loads them (Windows PowerShell 5.1). Dot-source it.
# One batch because the duct arms and the duct model are parts of the same partial classes (DuctsToPluto).
# The Run-Duct*.ps1 in scripts\duct load their own .cs by name and are not part of this batch.
# Without a usable cached core dll, core + arms compile in one in-memory batch (the pre-2026-10-30 build).
if (-not ('DuctsToPluto' -as [type])) {
    $PlutoWantArms = $true
    . (Join-Path $PSScriptRoot "..\lib\Import-PlutoCore.ps1")
    $armsCs = @(Get-ChildItem -Path $PSScriptRoot, (Join-Path $PSScriptRoot "..\duct\model") -Recurse -Filter *.cs |
        Select-Object -ExpandProperty FullName)
    if ($PlutoCoreDll) {
        $PlutoArmsDll = Import-PlutoCs "PlutoArms" $armsCs (@($PlutoCoreDll) + $PlutoRefs)
        if (-not $PlutoArmsDll) { Add-Type -Path $armsCs -ReferencedAssemblies (@($PlutoCoreDll) + $PlutoRefs) }
    } else {
        $coreCs = @(Get-ChildItem -Path (Join-Path $PSScriptRoot "..\lib") -Recurse -Filter *.cs | Select-Object -ExpandProperty FullName)
        Add-Type -Path ($coreCs + $armsCs) -ReferencedAssemblies $PlutoRefs
    }
}
