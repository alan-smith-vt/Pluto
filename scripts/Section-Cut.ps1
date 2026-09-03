param(
    [string]$ModelPath,
    [string]$CutsPath,
    [switch]$Expand = $false,
    [switch]$TBeam = $false,
    [int[]]$LCs = $null
)
. ".\lib\Config.ps1"
$workingDir = "" ## sanitized
$outdir = "" ## sanitized
if (!(Test-Path $outdir)) { $null = New-Item -ItemType Directory -Path $outdir }

[System.Collections.Generic.List[string]]$ANLFiles = @()
[System.Collections.Generic.List[string]]$CutsFiles = @()

# =======================================================================================
# Get path to model ANL files
# ---------------------------------------------------------------------------------------
if (!$ModelPath) {
    Write-Host "Defaulting to .ANL files at the following location:`n$workingDir" -ForegroundColor Yellow
    $ModelPath = $workingDir
}
if ($ModelPath.EndsWith(".ANL")) { $ANLFiles.Add($ModelPath) }
else { $ANLFiles.AddRange([string[]]((Get-ChildItem -Path $ModelPath -Filter "*.ANL").FullName)) }

if ($ANLFiles.Count -eq 0) {
    $err = [SectionCutException]::new("No .ANL output file found at the following location. Try again.`n$ModelPath")
    Write-Error $err
    exit
}
# ---------------------------------------------------------------------------------------
# =======================================================================================
# Get path to section cut JSONs
# ---------------------------------------------------------------------------------------
if (!$CutsPath) { $CutsPath = Read-Host "Path to .JSON file or directory containing .JSON files for section cuts" }
if ($CutsPath.EndsWith(".json")) { $CutsFiles.Add($CutsPath) }
else { $CutsFiles.AddRange([string[]]((Get-ChildItem -Path $CutsPath -Filter "*.json").FullName)) }

if ($CutsFiles.Count -eq 0) {
    $err = [SectionCutException]::new("No .JSON section cut file found at the following location. Try again.`n$CutsPath")
    Write-Error $err
    exit
}
# ---------------------------------------------------------------------------------------
# ---------------------------------------------------------------------------------------

# =======================================================================================
# Load models
# ---------------------------------------------------------------------------------------
if ($ANLFiles.Count -eq 1) {
    $anl = $ANLFiles[0]
    $ModelID = $anl.Split("\")[-1]

    $modelConfig = [ModelConfig]::new($anl);
    $modelConfig.loadCenterStresses = $false;
    $modelConfig.loadDisplacements = $false;
    $modelConfig.loadForces = $false;
    $model = [StaadModel]::new($modelConfig);
    $model.AssignModelIDs($ModelID);
    $model.AbsBandaid(30);
    $model.AbsBandaid(31);
    $model.AbsBandaid(34);
}
else {
    if (!$stresses) {
        $stresses = [System.Collections.Generic.List[StressRecord]]::new()
        $modelSet = [System.Collections.Generic.HashSet[Object]]::new()
    }
    $modelsToLoad = $ANLFiles.Where{ -not $modelSet.Contains([System.IO.Path]::GetFileName($_)) }
    if ($modelsToLoad.Count -gt 0) {
        for ($i = 0; $i -lt $modelsToLoad.Count; $i++) {
            $anl = $modelsToLoad[$i]

            $ModelID = [System.IO.Path]::GetFileName($anl)

            Write-Host "`nReading model data from $ModelID" -ForegroundColor Yellow
            $modelConfig = [ModelConfig]::new($anl);
            $modelConfig.loadCenterStresses = $false;
            $modelConfig.loadDisplacements = $false;
            $modelConfig.loadForces = $false;
            $model = [StaadModel]::new($modelConfig)
            $model.AssignModelIDs($ModelID);
            $model.AbsBandaid(30);
            $model.AbsBandaid(31);
            $model.AbsBandaid(34);
            $stresses.AddRange($model.StressList)
            $modelSet.Add($ModelID) | Out-Null
        }
        $model.StressList = $stresses
    }
}
# ---------------------------------------------------------------------------------------
# ---------------------------------------------------------------------------------------

# =======================================================================================
# Load section cut inputs from all provided JSONs
# ---------------------------------------------------------------------------------------
$cutDataList = [System.Collections.Generic.List[hashtable]]::new()
foreach ($cutFile in $CutsFiles) {
    $cutsJSONstring = Get-Content -Path $cutFile -Raw
    $cutsJSON = ConvertFrom-Json -InputObject $cutsJSONstring
    $cutsInputs = $cutsJSON.cuts
    $cutDefGroup = $cutFile.Split("\.")[-2].Replace(" ", "")
    foreach ($cutInput in $cutsInputs) {
        if ($null -ne $cutInput.group) { $cutGroup = $cutInput.group }
        else { $cutGroup = $cutDefGroup }
        $cutInput.name = $cutGroup + " " + $cutInput.name

        $cutHash = [hashtable]@{}
        $cutInput.PSObject.Properties.foreach{ $cutHash[$_.Name] = $_.Value }
        $cutHash["point"] = [double[]]$cutHash["point"]

        $cutDataList.Add($cutHash)
    }
}
# ---------------------------------------------------------------------------------------
# ---------------------------------------------------------------------------------------

# =======================================================================================
# Generate section cuts
# =======================================================================================
$fceUnits = "lbf" ## Force units - These should match the requested output units
$thickUnits = "in" ## Thickness units - These should match the units at the time
##                                      thicknesses are defined
$lenUnits = "in" ## Length units - These should match the node coordinate units
$units = [UnitSystem]::New($fceUnits, $thickUnits, $lenUnits)
foreach ($cutData in $cutDataList) {
    Write-Host "`nGenerating the following section cut:" -ForegroundColor Yellow
    Write-Host "$($cutData["name"])`n"
    try {
        if ($TBeam) { $cut = [PlateSectionCut]::TBeamSectionCut($cutData, $model, $outdir, $units, $Expand, $LCs) }
        else { $cut = [PlateSectionCut]::CreatePlateSectionCut($cutData, $model, $outdir, $units, $Expand, $LCs) }
        $cut.CreateOutputExcel()
    }
    catch { Write-Host "Error in section cut. Not creating Excel file." -ForegroundColor Red }
}
# ---------------------------------------------------------------------------------------
# ---------------------------------------------------------------------------------------