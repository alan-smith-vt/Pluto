# Dot-source after the arms are loaded (scripts\arms\Import-Arms.ps1): builds the converter's DuctSettings
# (scripts\duct\model\DuctSettings.cs) from the duct config data, read ONCE by the caller, plus the command-line
# overrides. Same rules and error texts as before 2026-10-30 (none = off, a missing file throws naming its key).
#   $ds = Get-DuctSettings -Data $dcfg -Mode Revit [-Systems s] [-Cuts csv] [-Drop files] [-Steel csv] [-Rooms codes] -Notes $notes
# -Mode Revit: everything; RevitMesh: RevitMeshNoRunExclude only; anything else: AddinDir only.
# $notes (a List[string]) receives the console lines ("cut points: n from x", ...) in the order they used to print.
function Get-DuctSettings {
    param([hashtable]$Data, [string]$Mode, [string]$Systems, [string]$Cuts, [string]$Drop, [string]$Steel, [string]$Rooms,
          [System.Collections.Generic.List[string]]$Notes)
    if (-not $Data) { $Data = @{} }
    $ds = New-Object DuctSettings
    # files shared with the add-ins (room boxes, service codes) live in AddinDir, default the add-ins' C:\Temp\hvac
    if ($Data.AddinDir) { $ds.AddinDir = $Data.AddinDir }
    if ($Mode -eq "RevitMesh") {
        # Revit categories left out of the _norun file
        foreach ($c in @($Data.RevitMeshNoRunExclude | Where-Object { $_ })) { [void]$ds.NoRunExclude.Add($c.Trim()) }
    }
    if ($Mode -ne "Revit") { return $ds }

    if (-not $Systems) { $Systems = ($Data.DuctSystems) -join "," }
    foreach ($s in @($Systems -split '[,;\s]+' | Where-Object { $_ })) {
        $n, $c = $s -split '=', 2
        $ds.PrimarySystems.Add($n)
        if ($c) { $ds.PrimaryColors[$n] = $c }
    }
    # pinned mains: "<name>=<Revit id>" (a fragment holding that part becomes "<name> 2", ...)
    foreach ($p in @($Data.DuctMains | Where-Object { $_ })) {
        $n, $id = $p -split '=', 2
        if ($id) { $ds.PrimaryPins.Add((New-Object 'System.Collections.Generic.KeyValuePair[string,string]' $n.Trim(), $id.Trim())) }
    }
    # scope cut points: a Run-Sap.ps1 -Points CSV (Name, Found, X_in, Y_in, Z_in) from -Cuts or DuctCuts
    if (-not $Cuts) { $Cuts = $Data.DuctCuts }
    if ($Cuts -and $Cuts -ne "none") {
        if (-not (Test-Path $Cuts)) { throw "cut points file not found: $Cuts (DuctCuts in duct-config.psd1 or -Cuts; -Cuts none = no cuts)" }
        $inv = [Globalization.CultureInfo]::InvariantCulture
        foreach ($c in (Import-Csv $Cuts | Where-Object { $_.Found -eq "1" })) {
            $xyz = [double[]]@([double]::Parse($c.X_in, $inv), [double]::Parse($c.Y_in, $inv), [double]::Parse($c.Z_in, $inv))
            $ds.CutPoints.Add((New-Object 'System.Collections.Generic.KeyValuePair[string,double[]]' $c.Name, $xyz))
        }
        $Notes.Add(("cut points: {0} from {1}" -f $ds.CutPoints.Count, $Cuts))
    }
    # add-on (CleanModel.ApplyManualDrop): hand-picked drop lists from the viewer's Cut picker; DuctDrop names one file or a
    # list, only those named (no default); -Drop overrides, -Drop none = off
    $dropFiles = @($(if ($Drop) { $Drop -split ',' } else { $Data.DuctDrop }) | ForEach-Object { "$_".Trim() } | Where-Object { $_ -and $_ -ne "none" })
    foreach ($f in $dropFiles) {
        if (-not (Test-Path $f)) { throw "drop list not found: $f (DuctDrop in duct-config.psd1 or -Drop; -Drop none = off)" }
        $Notes.Add(("manual drop: {0} parts from {1}" -f [DuctsToPluto]::ReadManualDrop($ds, (Resolve-Path $f).Path), $f))
    }
    # utility rack steel in the clean model (SteelViewWriter.cs): DuctSteel (SteelToPluto CSV, meters, this frame); -Steel
    # overrides, -Steel none = off. DuctSteelSnapIn / DuctSteelNearIn: connection snap and OPEN_NEAR distances (in)
    if (-not $Steel) { $Steel = $Data.DuctSteel }
    if ($Data.DuctSteelSnapIn) { $ds.SteelSettings.SnapIn = [double]$Data.DuctSteelSnapIn }
    if ($Data.DuctSteelNearIn) { $ds.SteelSettings.NearIn = [double]$Data.DuctSteelNearIn }
    # DuctSteelSnap = centreline (default) | contact (prototype: the end section touches the other member, within DuctSteelContactIn)
    if ($Data.DuctSteelSnap) { $ds.SteelSettings.SnapMode = [string]$Data.DuctSteelSnap }
    if ($Data.DuctSteelContactIn) { $ds.SteelSettings.ContactIn = [double]$Data.DuctSteelContactIn }
    # duct-to-steel supports (duct\model\SupportPlacer.cs): DuctSupportGapIn = largest duct bottom -> steel top gap that is a
    # support (in), DuctSpanFt = unsupported span limit (ft); the gap histogram in match.txt helps choose the gap
    if ($Data.DuctSupportGapIn) { $ds.Supports.GapMaxIn = [double]$Data.DuctSupportGapIn }
    if ($Data.DuctSpanFt) { $ds.Supports.SpanFt = [double]$Data.DuctSpanFt }
    # Navisworks hanger check (duct\model\HangerCheck.cs): a hanger and a support match within DuctHangerMatchIn (in)
    if ($Data.DuctHangerMatchIn) { $ds.HangerMatchIn = [double]$Data.DuctHangerMatchIn }
    # duct orientation (duct\model\Orienter.cs): horizontal ducts with the larger side vertical are flagged and, unless
    # DuctRotateLargeVertical = $false, rotated 90 deg about the centreline with their size run
    $ds.RotateLargeVertical = -not ($Data.ContainsKey('DuctRotateLargeVertical') -and -not $Data.DuctRotateLargeVertical)
    if ($Steel -and $Steel -ne "none") {
        if (-not (Test-Path $Steel)) { throw "steel CSV not found: $Steel (DuctSteel in duct-config.psd1 or -Steel; -Steel none = off)" }
        # the steel takes the ducts' room scope (-Rooms: Export-DuctsViewer's -Room / DuctRooms; empty = whole building)
        # with the duct rule: each code also keeps its variants (values that start with it)
        $sr = New-Object SteelToPluto; $sr.Progress = $false; $sr.RoomPrefix = $true
        $ds.Steel.AddRange($sr.Build((Resolve-Path $Steel).Path, $Rooms))
        $scope = $(if ($Rooms) { "rooms " + (@($Rooms -split '[,;\s]+' | Where-Object { $_ }) -join ",") } else { "rooms: whole building" })
        $Notes.Add(("steel: {0} members from {1} ({2}) ({3})" -f $ds.Steel.Count, $Steel, $scope, ($sr.Summary() -replace "`n", "; ")))
    }
    return $ds
}
