# Import-SqlExplorer.ps1 - compile and wire up SqlExplorer.cs in a PS 5.1 session.
#
# No arguments. Connection settings come from config.json next to this script
# (copy config.example.json -> config.json and fill it in; config.json is gitignored).
#
# Dot-source it:   . .\Import-SqlExplorer.ps1
# Then:            Invoke-Sql "SELECT TOP 10 name FROM sys.tables"

$configPath = Join-Path $PSScriptRoot "config.json"
if (-not (Test-Path $configPath)) {
    throw "Missing $configPath - copy config.example.json to config.json and fill in server/database."
}
$config = Get-Content $configPath -Raw | ConvertFrom-Json

foreach ($key in @("server", "database")) {
    if ([string]::IsNullOrWhiteSpace($config.$key)) {
        throw "config.json is missing required key '$key'."
    }
}
$timeout = 15
if ($config.PSObject.Properties.Name -contains "connectTimeoutSeconds") {
    $timeout = [int]$config.connectTimeoutSeconds
}

# Types can't be redefined in a session - skip Add-Type if already loaded.
if (-not ("Voyager.SqlExplorer" -as [type])) {
    $csPath = Join-Path $PSScriptRoot "SqlExplorer.cs"
    $atlasPath = Join-Path $PSScriptRoot "Atlas.cs"
    $paths = @($csPath)
    if (Test-Path $atlasPath) { $paths += $atlasPath }   # Atlas must compile in the SAME Add-Type call
    Add-Type -Path $paths -ReferencedAssemblies "System.Data", "System.Xml"
}

[Voyager.SqlExplorer]::ConnectionString =
    "Server=$($config.server);Database=$($config.database);Integrated Security=true;Connect Timeout=$timeout;"

if ($config.PSObject.Properties.Name -contains "commandTimeoutSeconds") {
    [Voyager.SqlExplorer]::CommandTimeoutSeconds = [int]$config.commandTimeoutSeconds
}

function Invoke-Sql {
    param([Parameter(Mandatory = $true)][string]$Sql)
    # DataTable unrolls row-by-row on the pipeline, so Format-Table/Export-Csv just work.
    [Voyager.SqlExplorer]::QueryReadOnly($Sql)
}

function Invoke-SqlScalar {
    param([Parameter(Mandatory = $true)][string]$Sql)
    [Voyager.SqlExplorer]::Scalar($Sql)
}

# Class map for the atlas (names, HTML colors, noExpand) from classids.json next to this script.
# Edit the JSON, then re-run Import-AtlasClassMap to reload without restarting the session.
function Import-AtlasClassMap {
    param([string]$Path = (Join-Path $PSScriptRoot "classids.json"))
    if (-not (Test-Path $Path)) { Write-Warning "No class map at $Path - atlas will print bare classids."; return }
    $map = Get-Content $Path -Raw | ConvertFrom-Json
    if ($map.PSObject.Properties.Name -contains "defaultColor") { [Voyager.Atlas]::DefaultColor = [string]$map.defaultColor }
    $n = 0
    foreach ($prop in $map.classes.PSObject.Properties) {
        $c = $prop.Value
        $noExpand = $false
        if ($c.PSObject.Properties.Name -contains "noExpand") { $noExpand = [bool]$c.noExpand }
        [Voyager.Atlas]::SetClass([int]$prop.Name, [string]$c.name, [string]$c.color, $noExpand)
        $n++
    }
    Write-Host "Atlas class map: $n classids loaded from $Path"
}
Import-AtlasClassMap

# Relation labels: relations.json next to this script (GITIGNORED - it holds real GUIDs, production box only).
# Shape: { "G_WBS": "<guid>", "G_MEM_XS": "<guid>", ... }  -> atlas prints --G_WBS--> instead of the GUID.
function Import-AtlasRelationMap {
    param([string]$Path = (Join-Path $PSScriptRoot "relations.json"))
    if (-not (Test-Path $Path)) { Write-Host "No relations.json - atlas prints relation GUIDs bare."; return }
    $map = Get-Content $Path -Raw | ConvertFrom-Json
    $n = 0
    foreach ($prop in $map.PSObject.Properties) {
        $g = ([string]$prop.Value).Trim().ToLowerInvariant()
        if ($g -match '^[0-9a-f-]{36}$') { [Voyager.Atlas]::RelationLabels[$g] = [string]$prop.Name; $n++ }
    }
    Write-Host "Atlas relation map: $n labels loaded from $Path"
}
Import-AtlasRelationMap

# Neighborhood atlas: Invoke-Atlas -Oid <guid> [-Hops 2] [-OneWay] [-OutDir C:\Temp\atlas]  (production box: only C:\Temp is writable)
# Prints the text tree; writes <OutDir>\atlas-<oid8>.html / .csv / .nodes.csv.
function Invoke-Atlas {
    param(
        [Parameter(Mandatory = $true)][string]$Oid,
        [int]$Hops = 2,
        [switch]$OneWay,
        [string]$OutDir = "C:\Temp\atlas"
    )
    if ([Voyager.Atlas]::Plant -eq "<plant>") { throw "Set [Voyager.Atlas]::Plant = '<plant>' first." }
    if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir | Out-Null }
    $r = [Voyager.Atlas]::Explore($Oid, $Hops, -not $OneWay)
    $stem = Join-Path (Resolve-Path $OutDir) ("atlas-" + $Oid.Substring(0, 8))
    [Voyager.Atlas]::WriteHtml($r, "$stem.html")
    [Voyager.Atlas]::WriteCsv($r, "$stem.csv")
    [Voyager.Atlas]::Report($r)
    Write-Host "Wrote $stem.html / .csv / .nodes.csv"
    $r
}

Write-Host "SqlExplorer ready against $($config.server)/$($config.database). Use Invoke-Sql / Invoke-SqlScalar."
