<#
.SYNOPSIS
  Routed SP3D ductwork as a port table: one CSV row per (part, port) with the port's place point,
  the 80007 hub on that port (joint) and the part's run. Draft v1 (2026-10-08), not yet verified.

.DESCRIPTION
  Port-anchored, unlike the hub-anchored pipe v4.2: duct ports (80011) carry JDistribPort.PlacePoint*
  (verified 2026-10-08 with the atlas on an 80009), so each row is an exact endpoint and a tee gives
  three rows. Parts are whatever points at an 80011 other than a hub, so duct fitting classes need not
  be known up front (PartClass is in the CSV; -Census counts them). Topology: two parts sharing a HubOid
  are joined there; a port with no hub is an open end (or a joint SP3D does not record).
  Run = the 80010 that points at the part (assumed like the pipe 80013 run -> part edge).
  Not covered: fabrication ducts modelled as equipment (20014) - they have no ports.
  Size is not read here: the join to Navisworks (Custom OID) supplies the stated size.

  Needs Import-SqlExplorer.ps1 dot-sourced first ([Voyager.Atlas]::Plant set from config.json).
  GUIDs stay in C:\Temp; report counts only.

.EXAMPLE
  .\Export-DuctPorts.ps1 -Census            # part classes / hub coverage, no CSV
  .\Export-DuctPorts.ps1 -Top 50            # first 50 rows to C:\Temp\duct_ports_top.csv
  .\Export-DuctPorts.ps1                    # all rows to C:\Temp\duct_ports.csv
#>
[CmdletBinding()]
param(
    [switch] $Census,
    [int]    $Top = 0,
    [string] $Out
)
$ErrorActionPreference = 'Stop'
if (-not (Get-Command Invoke-Sql -ErrorAction SilentlyContinue)) { throw "Dot-source Import-SqlExplorer.ps1 first." }
$plant = [Voyager.Atlas]::Plant
if (-not $plant -or $plant -eq '<plant>') { throw "Atlas plant not set (config.json `"plant`")." }
$m = $plant + '_MDB.dbo.'

$body = @"
FROM ${m}CoreBaseClass pt
JOIN ${m}JDistribPort d ON d.oid = pt.oid
JOIN ${m}CoreRelationOrigin po ON po.oidTarget = pt.oid
JOIN ${m}CoreBaseClass pb ON pb.oid = po.oid AND pb.classid <> 80007 AND pb.persistentFlag & 1024 = 0
LEFT JOIN (SELECT r.oidTarget AS PortOid, r.oid AS HubOid
           FROM ${m}CoreRelationOrigin r JOIN ${m}CoreBaseClass c ON c.oid = r.oid AND c.classid = 80007) hb
       ON hb.PortOid = pt.oid
LEFT JOIN (SELECT r.oidTarget AS P, r.oid AS RunOid
           FROM ${m}CoreRelationOrigin r JOIN ${m}CoreBaseClass c ON c.oid = r.oid AND c.classid = 80010) rn
       ON rn.P = po.oid
WHERE pt.classid = 80011
"@

$t0 = Get-Date
if ($Census) {
    # one line per part class: parts, port rows, rows with a hub, rows with a run
    $q = "SELECT pb.classid AS PartClass, COUNT(DISTINCT po.oid) AS Parts, COUNT(*) AS PortRows, " +
         "COUNT(hb.HubOid) AS WithHub, COUNT(rn.RunOid) AS WithRun " + $body + " GROUP BY pb.classid ORDER BY Parts DESC"
    Invoke-Sql $q | Format-Table -AutoSize
} else {
    $sel = "SELECT " + $(if ($Top -gt 0) { "TOP $Top " } else { "" }) +
           "po.oid AS PartOid, pb.classid AS PartClass, pt.oid AS PortOid, " +
           "d.PlacePointX AS X, d.PlacePointY AS Y, d.PlacePointZ AS Z, hb.HubOid, rn.RunOid, nm.ItemName AS RunName "
    # JNamedItem unqualified, as in the pipe v4.2 query (resolves in the connection's database)
    $q = $sel + $body.Replace("WHERE pt.classid", "LEFT JOIN JNamedItem nm ON nm.oid = rn.RunOid`r`nWHERE pt.classid")
    if (-not $Out) { $Out = $(if ($Top -gt 0) { 'C:\Temp\duct_ports_top.csv' } else { 'C:\Temp\duct_ports.csv' }) }
    $rows = @(Invoke-Sql $q)
    $rows | Select-Object * -ExcludeProperty RowError, RowState, Table, ItemArray, HasErrors | Export-Csv $Out -NoTypeInformation
    Write-Host ("{0} rows -> {1}" -f $rows.Count, $Out)
}
Write-Host ("[done] {0:0}s" -f ((Get-Date) - $t0).TotalSeconds)
