---
title: Pipe CSV → viewer (usage)
status: current
created: 2026-08-27
---

# Pipe CSV → viewer — usage

The SP3D piping arm end to end, as run in the production environment (everything under `C:\Temp`, PowerShell 5.1, C# 5 via `Add-Type`). Source files: `viewer/exporters/SQL_BeamExporter.cs` (CSV → beams) and `viewer/exporters/PipeBeamsToPluto.cs` (beams → `.bin` + `.features.json`), plus `viewer/RawViewerWriter.cs`, `viewer/FeaturesSidecar.cs` and the production-box `Stubs.cs`.

## 1. Get the CSV

Run the **v4.1** query from SQL_Tutor `vault/40-join-paths/Pipe Extraction v1.md` and export it raw:

```powershell
Invoke-Sql $q | Export-Csv C:\Temp\pipe_v4.csv -NoTypeInformation
```

Columns: `ConnOid, PartOid, PartClass, X, Y, Z, RunOid, RunName, Room, Udf3, Udf4, NPD, OD, EndNPD, EndOD` (meters; `OD` = the part's own size from the model, `EndOD` = the size at that hub = the neighbour across it, so reducers get a different value per row). ~400k rows. **No sizing pass, no Excel filter** — `Build` reads `OD` directly and falls back to the `RunName` regex where it is blank.

## 2. Compile (fresh window)

```powershell
Add-Type -Path .\RawViewerWriter.cs, .\FeaturesSidecar.cs, .\SQL_BeamExporter.cs, .\PipeBeamsToPluto.cs, .\Stubs.cs
```

All files in one `Add-Type` call — they reference each other. `SQL_BeamExporter.cs` defines `Vec3` and `SQL_Beam`; if `Stubs.cs` also defines `Vec3`, delete one copy. Types can't be redefined: edit → new window.

## 3. See what rooms are in the file (optional)

```powershell
$ex = New-Object Voyager.SQL_BeamExporter
$ex.Rooms('C:\Temp\pipe_v4.csv') | Sort-Object Value -Descending | Select-Object -First 20
```

Returns room → row count (trimmed, case-insensitive). Blank key = parts whose run has no room UDF.

## 3b. Separate size table (optional - only for CSVs without an `OD` column)

Run the v4.1 size query (SQL_Tutor `Task - 3D Viewer Export` §2: pipes via `JDPipePort`, fittings via the route feature tables `NomDiam`/`OuterDiameter`) and export it:

```powershell
Invoke-Sql $qSizes | Export-Csv C:\Temp\pipe_sizes.csv -NoTypeInformation
$ex.LoadSizes('C:\Temp\pipe_sizes.csv')      # once per session, before Build
```

Columns `PartOid, SrcClass, NPD, OD` (OD in meters). `Build` then uses the model OD for every part it finds there and the `RunName` regex only for the rest; `Summary()` reports `fromData / fromName / none`. A part with two different ODs (reducer, reducing tee) is counted under `span two ODs` and rendered at the larger one for now; `$ex.SizeSpanParts()` lists them (PartOid -> min, max).

## 4. Build beams — whole plant or one room

```powershell
$beams = $ex.Build('C:\Temp\pipe_v4.csv', '<room>')     # one room
$beams = $ex.Build('C:\Temp\pipe_v4.csv')               # whole plant
$ex.Summary()
```

While it runs an ASCII bar redraws in place:

```
[##################......................]  46%  184,000 rows     4s
```

(`$ex.Progress = $false` silences it; `$ex.ProgressEveryRows = 20000` slows the redraw.)

`Summary()` prints `room=<room> rows=<read> kept=<after filter> parts=<distinct PartOid> skipped=<parts with <2 joints> unsized=<no size at all> jointConflicts=0 runConflicts≈0` and a second line `sizes: fromData=… fromName=… none=…  tapered beams=…` (beams whose `D0`/`D1` differ - reducers and reducing-tee branches; rendered at the larger end until the viewer tapers). Expect `skipped` to be a few percent (open ends); `jointConflicts` must be 0.

What `Build` does per row: keeps it if `Room` matches (before anything else, so a part is in or out with all its joints); groups rows by `PartOid`; one point per `ConnOid` (v4 hub) — `WeldOid` is accepted for old v3 CSVs; size = `SizeInches` column if present, else the `<n>"`, `<a/b>"`, `<n a/b>"` token in `RunName` → meters, `NaN` if none; 2 joints → one chord beam, 3+ → star from each joint to the centroid, <2 → skipped and counted.

## 5. Write the viewer files

```powershell
$r = [Voyager.PipeBeamsToPluto]::Export($beams, 'C:\Temp\pipes_<room>', '<plant>/pipes/<room>', 'in')
$r.Summary()
```

Writes `C:\Temp\pipes_<room>.bin` (v4 binary, beams only, geometry only, `PartOid` as label) and `C:\Temp\pipes_<room>.features.json` (one group per pipe size on a blue→red ramp, `UNSIZED` red). Third argument is the model id string; fourth is the file's length unit (`m`, `mm`, `in`, `ft`).

## 6. View

Drop the `.bin` and `.features.json` together on the viewer's file picker → tick **Color by groups** → tick **Z up**. Hover shows `PartOid`.

## Troubleshooting

| symptom | cause / fix |
|---|---|
| `Build` returns few beams, `skipped` high | wrong `Room` spelling (use `Rooms()`), or CSV from a query that dropped hubs |
| everything `UNSIZED` | `RunName` column missing/renamed, or names lack the `"` token — check one row |
| `jointConflicts > 0` | same `ConnOid` at two coordinates — the SQL side duplicated a hub row; report it |
| duplicate type `Vec3` at `Add-Type` | remove the copy in `Stubs.cs` (or in `SQL_BeamExporter.cs`) |
| bar never moves | it ticks every 5k rows; if nothing after 10 s the file is not being read — check the path |

## Related

- [[vault/format/raw-viewer-writer|raw-viewer-writer]] — the binary writer and bridges section
- [[vault/format/features-sidecar|features-sidecar]] — groups JSON
- [[vault/handoff|handoff]] — current state and constraints
- SQL_Tutor `vault/40-join-paths/Pipe Extraction v1.md` (query) and `Beam Export - Pipe CSV to Beams.md` (design rationale)
