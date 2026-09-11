<#
.SYNOPSIS
  Join calc->run (from Export-CalcRuns.ps1) with run->room (from the v4.2 pipe CSV).

.DESCRIPTION
  Room on the pipe CSV is the run UDF dispid 2 and may hold several rooms as 'a/b/c'
  (see Pipe Extraction v1, boundary-straddling runs). Split on '/', trim, one row per room.

  Outputs:
    <Out>                 Room, File, Runs            (one row per room x calc; Runs = matched run names, ';'-joined)
    <Out>_by_run.csv      Run, Room, File             (long form, for auditing)
    <Out>_unmatched.csv   Run, File                   (calc runs with no row in the pipe CSV)

.EXAMPLE
  .\Join-CalcRooms.ps1 -CalcRuns C:\Temp\calc_runs.csv -PipeCsv C:\Temp\pipe_v4_sized.csv -Out C:\Temp\room_calcs.csv
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $CalcRuns,
    [Parameter(Mandatory)] [string] $PipeCsv,
    [string] $Out = 'C:\Temp\room_calcs.csv',
    [string] $RunCol = 'RunName',
    [string] $RoomCol = 'Room'
)
$ErrorActionPreference = 'Stop'

# run -> set of rooms (distinct, from ~400k pipe rows)
$runRooms = @{}
foreach ($r in (Import-Csv $PipeCsv)) {
    $name = $r.$RunCol; if (-not $name) { continue }
    if (-not $runRooms.ContainsKey($name)) { $runRooms[$name] = New-Object System.Collections.Generic.HashSet[string] }
    foreach ($room in ($r.$RoomCol -split '/')) { $t = $room.Trim(); if ($t) { [void]$runRooms[$name].Add($t) } }
}
Write-Host "$($runRooms.Count) distinct runs in pipe CSV"

$long = New-Object System.Collections.Generic.List[object]
$unmatched = New-Object System.Collections.Generic.List[object]
foreach ($c in (Import-Csv $CalcRuns)) {
    if ($runRooms.ContainsKey($c.Run)) {
        foreach ($room in $runRooms[$c.Run]) {
            $long.Add([pscustomobject]@{ Run = $c.Run; Room = $room; File = $c.File })
        }
    } else { $unmatched.Add([pscustomobject]@{ Run = $c.Run; File = $c.File }) }
}

$stem = [IO.Path]::ChangeExtension($Out, $null).TrimEnd('.')
$long | Sort-Object Room, File, Run | Export-Csv "$stem`_by_run.csv" -NoTypeInformation
$unmatched | Export-Csv "$stem`_unmatched.csv" -NoTypeInformation

$long | Group-Object Room, File | ForEach-Object {
    [pscustomobject]@{
        Room = $_.Group[0].Room
        File = $_.Group[0].File
        Runs = ($_.Group.Run | Sort-Object -Unique) -join ';'
    }
} | Sort-Object Room, File | Export-Csv $Out -NoTypeInformation

Write-Host ("{0} room-calc pairs over {1} rooms; {2} calc runs unmatched -> {3}" -f
    ($long | Group-Object Room, File).Count, ($long.Room | Select-Object -Unique).Count, $unmatched.Count, $Out)
