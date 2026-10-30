# Round-trip test for the `look` / `anchors` group fields of FeaturesSidecar.cs.
# Windows PowerShell 5.1:  powershell -NoProfile -File scripts\lib\sidecar\Test-FeaturesSidecarLook.ps1
$ErrorActionPreference = 'Stop'
Add-Type -Path (Join-Path $PSScriptRoot 'FeaturesSidecar.cs') -ReferencedAssemblies System.Web.Extensions, System.Core
Add-Type -AssemblyName System.Web.Extensions

$fails = 0
function Check($cond, $msg) { if ($cond) { "ok   $msg" } else { "FAIL $msg"; $script:fails++ } }

$sc = New-Object FeaturesSidecar
$sc.ModelId = 'test/look'; $sc.GeometryHash = 'sha256:00'
$sc.AddGroup('Steel: W1', '#8b919c', 'beams', [uint32[]](1, 2), [string[]]('steel', 'context')) | Out-Null

$look = New-Object FeaturesSidecar+Look
$look.Glyph = 'half'; $look.Tier = 'detail'; $look.Label = 'Hinged'; $look.Section = 'Member ends'; $look.InsetPx = 12
$a0 = New-Object FeaturesSidecar+Anchor; $a0.Domain = 'beams'; $a0.Id = 12; $a0.T = 0
$a1 = New-Object FeaturesSidecar+Anchor; $a1.Domain = 'beams'; $a1.Id = 12; $a1.T = 1; $a1.Label = 'SF12 at "S40"'
$a2 = New-Object FeaturesSidecar+Anchor; $a2.Domain = 'nodes'; $a2.Id = 5; $a2.Dir = [double[]](0.5, -1e-7, 0)
$sc.AddLookGroup('End: hinged', $null, $look, [FeaturesSidecar+Anchor[]]($a0, $a1, $a2), [string[]]('end')) | Out-Null

# set-after style (AddGroup, then Look / Anchors), as the exporters do
$g = $sc.AddGroup('Flag: open', $null, 'beams', [uint32[]](7), [string[]]('flag'))
$w = New-Object FeaturesSidecar+Look; $w.Glyph = 'warn'; $w.Tier = 'exception'; $g.Look = $w

$json = $sc.ToJson()
$ser = New-Object System.Web.Script.Serialization.JavaScriptSerializer
$root = $ser.DeserializeObject($json)
$items = $root['groups']['items']

Check ($items.Count -eq 3) 'three groups written'
Check (-not $items[0].ContainsKey('look') -and -not $items[0].ContainsKey('anchors')) 'plain group: no look / anchors keys'
$h = $items[1]
Check ($h['look']['glyph'] -eq 'half' -and $h['look']['tier'] -eq 'detail') 'look glyph + tier'
Check ($h['look']['label'] -eq 'Hinged' -and $h['look']['section'] -eq 'Member ends' -and $h['look']['insetPx'] -eq 12) 'look label, section, insetPx'
Check (-not $h.ContainsKey('color')) 'null colour not written (viewer default tint)'
Check ($h['anchors'].Count -eq 3) 'three anchors'
Check ($h['anchors'][0]['domain'] -eq 'beams' -and $h['anchors'][0]['id'] -eq 12 -and $h['anchors'][0]['t'] -eq 0) 'anchor t=0'
Check ($h['anchors'][1]['label'] -eq 'SF12 at "S40"') 'anchor label escaped and round-trips'
Check (-not $h['anchors'][2].ContainsKey('t')) 'node anchor: no t'
Check ($h['anchors'][2]['dir'][0] -eq 0.5 -and $h['anchors'][2]['dir'][1] -eq -1e-7) 'dir round-trips exactly (R format, invariant culture)'
Check ($h['members'].Count -eq 0) 'AddLookGroup without members writes an empty members list'
$f = $items[2]
Check ($f['look']['glyph'] -eq 'warn' -and -not $f['look'].ContainsKey('label')) 'set-after look; unset label not written'
Check (-not $f.ContainsKey('anchors')) 'empty anchors not written'
Check ($f['members'][0]['ids'][0] -eq 7) 'members kept beside look'

# culture: a comma-decimal culture must not leak into numbers
$prev = [System.Threading.Thread]::CurrentThread.CurrentCulture
[System.Threading.Thread]::CurrentThread.CurrentCulture = 'de-DE'
try { $j2 = $sc.ToJson() } finally { [System.Threading.Thread]::CurrentThread.CurrentCulture = $prev }
Check ($j2.Contains('"dir": [0.5,-1E-07,0]')) 'numbers invariant under de-DE'

if ($fails) { "$fails failure(s)"; exit 1 } else { 'all passed' }
