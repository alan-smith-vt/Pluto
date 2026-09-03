---
title: Combined plant (pipes + steel) → viewer (usage)
status: current
created: 2026-09-02
---

# Combined plant → viewer — the whole workflow

Pipes and W-shape steel in ONE `.bin` + `.features.json`: shared node table
(coincident points merge across disciplines), one recenter frame, white steel,
pipes on the size ramp, gray synthetic pipes. Production side, PowerShell 5.1,
C# 5 via `Add-Type`. This note is the start-to-finish recipe; the per-arm
notes hold the CSV column specs and troubleshooting:
[[vault/arms/pipe-csv-to-viewer|pipe-csv-to-viewer]],
[[vault/arms/steel-csv-to-viewer|steel-csv-to-viewer]].

## 0. Inputs (three CSVs from the SQL agent)

| file | what | spec |
|---|---|---|
| `pipe_v4.csv` | pipe joints, one row per (hub, part), meters | pipe note §1 (v4.2 columns incl. `OD`, `Room`) |
| `pipe_parts_bbox.csv` | `PartOid, PartClass, CX, CY, CZ, EX, EY, EZ` — centroid + FULL extents (box = C ± E/2), meters | pipe note §3c; SQL_Tutor `Pipe Extraction v1` § v4.3 |
| `steel_v1.csv` | one row per member: `MemberOid, X0..Z1, SectionName, D, Bf, Tf, Tw, YDirX/Y/Z, Room, RunName, CP` | steel note §1 (`YDir` = web direction; `CP` = SP3D 15-point cardinal, 8 = top-center) |

The bbox file is optional but recommended: without it, one-hub pipe parts
(~6% — hubless pipe-to-pipe joints, caps, blind flanges) are skipped instead
of rescued.

## 1. Compile (fresh window every time a .cs changes)

```powershell
. <repo>\scripts\lib\Config.ps1   # one Add-Type batch: scripts/lib + scripts/arms
```

One call — the files reference each other. `PipeCsvReader.cs` defines
`PipeBeam` (`Vec3` now comes from `Types.cs`; the old `Stubs.cs` is retired). Add
`.\PipeToPluto.cs` to the list only if you also want single-discipline pipe
exports in the same session.

## 2. Build pipe records (with the one-hub fallback)

```powershell
$pex = New-Object PipeCsvReader
$pex.Rooms('C:\Temp\pipe_v4.csv')                 # optional: room census first
$pipes = $pex.Build('C:\Temp\pipe_v4.csv', '<room>', 'C:\Temp\pipe_parts_bbox.csv')
$pex.Summary()
```

- `'<room>'` → `$null` for the whole plant. Multi-room values match by token,
  including prefix-elided ones (`A-123/321` = A-123 and A-321).
- Third argument = bbox CSV path (not a flag). Omit it (or pass `$null`) and
  behaviour is byte-identical to the pre-fallback builder.
- `Summary()` line 3: `fallback: rescued / rejected / noBbox`. Expect rescued
  in the thousands plant-wide; a large `rejected` means hub coordinates
  inconsistent with their part bboxes — report to the SQL agent.

## 3. Build steel records

```powershell
$sex = New-Object SteelToPluto
$steel = $sex.Build('C:\Temp\steel_v1.csv', '<room>')
$sex.Summary()
```

Watch `orient: missing/badVector` (blank or axis-parallel `YDir` → fallback
orientation), `cp: offCentroid/unmapped`, `dimConflicts`.

## 4. Export one combined file

```powershell
$r = [CombinedToPluto]::Export($pipes, $steel, 'C:\Temp\plant_<room>', '<plant>/combined/<room>', 'in')
$r.Summary()
```

Either list may be `$null`/empty (not both). What it does: dedupes endpoints
across BOTH disciplines (1e-5 m — pipe supports landing on steel work points
become shared nodes), one whole-meter bbox recenter (`units.worldOffset` in
the sidecar), merged section table, labels = `PartOid`/`MemberOid` unprefixed.

Groups (one per element, exclusive):

| group | color | members |
|---|---|---|
| `PIPE <n> in` per size | blue→red ramp | sized pipes |
| `SYNTHETIC` | light gray `#c9ccd2` | one-hub-rescued pipes (instead of size) |
| `UNSIZED PIPE` | red `#ff3b3b` | pipes with no OD anywhere |
| one per `SectionName` | neutral white `#f2f2f5` | steel (white like the ungrouped view) |
| `UNSIZED STEEL` | orange `#ff7a3b` | steel rows missing dims |

## 5. View

Drop `plant_<room>.bin` + `plant_<room>.features.json` together on the
viewer's file picker → tick **Color by groups** → tick **Z up**. Hover shows
the oid and section name; double-click focuses.

## 6. Pipe weight heatmap (optional — `viewer/heatmap.html`)

Top-down psf heatmap of pipe weight over the steel plan. Serve `viewer/` the
usual way, open `heatmap.html`, and drop TWO files on it:

1. the combined **`.bin`** from step 4 (no CSV, no `.features.json` — pipes vs
   steel come from the section types in the binary), and
2. a **psf JSON** you write, mapping pipe OD (inches) → lb/ft² over the pipe's
   plan footprint (length × OD):

```json
{ "4.5": 12.3, "2.375": 6.1, "0.84": 2.0, "default": 5 }
```

- Keys are matched to each pipe's OD within **5%** (so `"4.5"` catches 4.500″
  schedule variants); anything unmatched falls to `"default"`; pipes matching
  neither are counted, warned on screen, and contribute **zero** weight.
- The ODs present in a file are exactly the sidecar group names
  (`PIPE 4.5 in`, …) — crib the key list from there. `UNSIZED` pipes render at
  the 2″ placeholder, so give `"2"` (or `default`) a value if you have many.

Controls: **Smear** slider (left = ~1″ cells, each step doubles, right = one
cell = room total), **Z cutoff** (pre-filled with the lowest bottom flange of
horizontal steel — hangers excluded; pipes below it count as floor-supported),
steel-plan overlay toggle. Hover a cell for psf + lb; the stats line shows the
total, counted/ignored pipes, and unmatched-psf warnings.

Caveats: vertical runs carry ~no weight (no plan length — the psf model;
switching to plf later fixes risers), and the psf is assumed to apply over the
pipe's own plan footprint. Check the single-cell total against a hand number
before trusting the fine grid.

## Sanity checklist after an export

- `jointConflicts = 0` (pipe side; nonzero = SQL duplicated a hub).
- `rescued + rejected + noBbox` = the drop in `skipped` vs a bbox-less run.
- `orientDefaulted` (steel) ≈ members with blank/axis-parallel `YDir` only.
- One hanging column + one framing beam eyeballed against Navisworks after
  any change to `YDir`/`CP` handling (rotation and cardinal offsets).
- Synthetic pipes render gray and continuous through hubless joints; elbow
  far-ends overshoot the corner (accepted — weights, not models).

## Related

- [[vault/arms/pipe-csv-to-viewer|pipe-csv-to-viewer]] — pipe CSV spec, size table, fallback detail, troubleshooting
- [[vault/arms/steel-csv-to-viewer|steel-csv-to-viewer]] — steel CSV spec (YDir, CP), single-discipline usage
- [[vault/format/raw-viewer-writer|raw-viewer-writer]] — the binary writer
- [[vault/format/features-sidecar|features-sidecar]] — groups JSON
