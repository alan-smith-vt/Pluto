---
title: Steel CSV → viewer (usage)
status: current
created: 2026-09-01
---

# Steel CSV → viewer — usage

*↑ [[vault/Pluto|Home]] › [[vault/arms/Arms map|Arms map]]*

W-shape members from the production-side SQL dump to a Pluto v4 beam-only file. One
self-contained bridge: `scripts/arms/SteelToPluto.cs` (CSV → members →
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
| `CP` | optional; SP3D cardinal point, 15-point code (8 = top-center, confirmed vs Navisworks 2026-09-02). The routed line passes through the CP, so the section is offset the opposite way (writer `OffsetAy/Az/By/Bz`, viewer applies in the section frame). Blank/0/5/10 = centered; 11–15 (shear-center codes) treated as centroid for doubly-symmetric W (`cp unmapped` counts them). `cpApplied` in the Export summary counts offset members |

Orientation fallback (missing or parallel-to-axis vector, counted in
`orientDefaulted`): web toward global Z projected off the axis; global X for a
near-vertical member.

## 2. Run (production side, PowerShell 5.1, fresh window)

```powershell
. <repo>\scripts\lib\Config.ps1   # one Add-Type batch: scripts/lib + scripts/arms
$ex = New-Object SteelToPluto
$ex.Rooms('C:\Temp\steel_v1.csv')                        # optional census
$members = $ex.Build('C:\Temp\steel_v1.csv', '<room>')   # or $ex.Build($csv) for all
$ex.Summary()
$r = [SteelToPluto]::Export($members, 'C:\Temp\steel_<room>', '<plant>/steel/<room>', 'in')
$r.Summary()
```

`PipeCsvReader.cs` stays in the Add-Type set because it defines `PipeBeam`; `Types.cs` supplies `Vec3`.

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

## Combined pipes + steel in one file

> [!note] Full start-to-finish recipe (incl. the pipe bbox fallback):
> [[vault/arms/plant-combined-to-viewer|plant-combined-to-viewer]]. Below is the short form.

`scripts/arms/CombinedToPluto.cs`: one `.bin` + sidecar from both lists —
shared node table (a pipe point and a steel work point within 0.01 mm merge into
one node), one recenter offset, merged section table, groups from both disciplines
(discipline as tags `pipe`/`steel`, not umbrella groups — the viewer resolves one
group per element). Unprefixed `PartOid`/`MemberOid` labels. Unsized groups are
`UNSIZED PIPE` (red) and `UNSIZED STEEL` (orange).

```powershell
. <repo>\scripts\lib\Config.ps1   # one Add-Type batch: scripts/lib + scripts/arms (CombinedToPluto included)
$pipes = (New-Object PipeCsvReader).Build('C:\Temp\pipe_v4.csv', '<room>')
$steel = (New-Object SteelToPluto).Build('C:\Temp\steel_v1.csv', '<room>')
$r = [CombinedToPluto]::Export($pipes, $steel, 'C:\Temp\plant_<room>', '<plant>/combined/<room>', 'in')
$r.Summary()
```

Either list may be null/empty (not both). Verified 2026-09-01 same as above
(strict C#5 compile + probe incl. cross-discipline node dedupe + reader readback).

## Related

- [[vault/arms/pipe-csv-to-viewer|pipe-csv-to-viewer]] — the sibling pipe bridge
- [[vault/format/raw-viewer-writer|raw-viewer-writer]] — binary writer
- [[vault/format/features-sidecar|features-sidecar]] — groups JSON
