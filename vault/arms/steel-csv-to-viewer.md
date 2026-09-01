---
title: Steel CSV → viewer (usage)
status: current
created: 2026-09-01
---

# Steel CSV → viewer — usage

W-shape members from the production-side SQL dump to a Pluto v4 beam-only file. One
self-contained bridge: `viewer/exporters/SteelBeamsToPluto.cs` (CSV → members →
`.bin` + `.features.json`). No viewer changes were needed — I sections and
per-member orientation were already in `RawViewerWriter.SectionDef.IShape` and
`beamGeometry.js`.

## 1. CSV shape

One **row per member** (not per joint, unlike the pipe file). `Export-Csv
-NoTypeInformation`; quoted fields fine. All lengths **meters**, plant frame.

| column | meaning |
|---|---|
| `MemberOid` | unique id; becomes the hover/pick label |
| `X0 Y0 Z0 X1 Y1 Z1` | member end coordinates |
| `SectionName` | e.g. `W12X26`; drives sections + groups |
| `D Bf Tf Tw` | depth, flange width, flange thk, web thk (blank → UNSIZED) |
| `YDirX YDirY YDirZ` | unit vector of section local Y (web, bottom→top flange), world coords; blank → default |
| `Room` | optional; drives the pre-filter |
| `RunName` | optional; diagnostics only |

Orientation fallback (missing or parallel-to-axis vector, counted in
`orientDefaulted`): web toward global Z projected off the axis; global X for a
near-vertical member.

## 2. Run (production side, PowerShell 5.1, fresh window)

```powershell
Add-Type -Path .\RawViewerWriter.cs, .\FeaturesSidecar.cs, .\SQL_BeamExporter.cs, .\SteelBeamsToPluto.cs, .\Stubs.cs
$ex = New-Object Voyager.SteelBeamsToPluto
$ex.Rooms('C:\Temp\steel_v1.csv')                        # optional census
$members = $ex.Build('C:\Temp\steel_v1.csv', '<room>')   # or $ex.Build($csv) for all
$ex.Summary()
$r = [Voyager.SteelBeamsToPluto]::Export($members, 'C:\Temp\steel_<room>', '<plant>/steel/<room>', 'in')
$r.Summary()
```

`SQL_BeamExporter.cs` stays in the Add-Type set because it defines `Voyager.Vec3`.

## 3. What Export does

- Node dedupe by rounded coordinate (`1e-5 m`) — shared work points reconnect the frame; zero-length members dropped before minting nodes.
- One `SectionDef.IShape` per distinct `SectionName` (first row's dims win; disagreeing rows counted in `dimConflicts`). Missing dims → one `UNSIZED` section (generic W8x24-ish) grouped red.
- Recenter at whole-meter bbox center; offset in sidecar `units.worldOffset` (float32 wobble fix, same as pipes).
- Sidecar groups: one per section name, shallow→deep on the blue→red ramp, `UNSIZED` red; tags `["steel","section"]`; `staadName` sanitized.

## 4. View

Drop `.bin` + `.features.json` together on the viewer → **Color by groups** →
**Z up**. Hover shows `MemberOid` and the section name.

## Verified 2026-09-01

Strict C#5 compile (LangVersion 5, warnings-as-errors, dotnet 9 scratch project)
plus end-to-end probe: 6-row CSV → Build/Export (node dedupe, vertical fallback,
missing-YDir default, UNSIZED, zero-length drop, room filter) → binary read back
with the viewer's own `PlutoFormat.load` (sections, params in inches, labels,
groups, worldOffset all correct).

## Related

- [[vault/arms/pipe-csv-to-viewer|pipe-csv-to-viewer]] — the sibling pipe bridge
- [[vault/format/raw-viewer-writer|raw-viewer-writer]] — binary writer
- [[vault/format/features-sidecar|features-sidecar]] — groups JSON
